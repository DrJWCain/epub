# Semantic search & discovery — Build plan

Branch: `semantic-search`. Builds on the MVP described in `plan.md` (M0–M7 complete).

## 1. Goal

Turn the library from "books I can read" into "a navigable space of ideas." Local-only, offline-first, one-time bulk indexing followed by cheap incremental updates as new books are added. Search is the foundation; orbital neighbors, theme cards, a 2D map, a 3D starfield, and concept threads are layered on top of the same index.

## 2. Locked decisions (2026-04-27)

- **Local-only.** No hosted embedding or LLM APIs. Offline stays a product property.
- **Bulk-then-incremental indexing.** A "Build index" command in Settings; user accepts a one-time multi-minute hit. After that, new books are indexed in the background as they appear.
- **No book removal path.** User has stated they will never remove books — schema and refit logic do not need tombstones, compaction, or orphan cleanup.
- **2D first, 3D as wow-mode.** Same data, different view. 3D ships as a toggle on the map page, not a separate data path.
- **No "ask your library" RAG in this track.** Concept threads (S6) use a local LLM, but for stitching passages, not Q&A. Q&A is a separate decision.

## 3. Tech stack additions

| Concern | Choice | Why |
|---|---|---|
| Vector store | **Managed brute-force cosine** over `float[]` BLOBs in existing `library.db`, behind an `IVectorIndex` abstraction | sqlite-vec ships no win-arm64 prebuilt (P0 spike, 2026-04-27); brute-force scans 10k × 384-dim in <500 ms with `Vector<T>` SIMD, ample headroom for 47-book corpus. Swap to managed HNSW behind the interface if scale ever demands it. |
| Embedding model | **all-MiniLM-L6-v2** (384-dim) via **ONNX Runtime** | ~80 MB, CPU-fast on ARM64, well-validated for English |
| Tokenizer | **Microsoft.ML.Tokenizers** BertTokenizer | Pairs cleanly with MiniLM |
| Clustering | **HDBSCAN.NET** | Variable-density clusters surface real themes; no K to pick |
| 2D projection | **UMAP.NET** (precompute on full refit) | Stored as `(x, y)` columns; reused for 3D's first two axes |
| Map renderer | **deck.gl** ScatterplotLayer in WebView2 | Handles 100k+ points smoothly via WebGL |
| 3D renderer | **three.js** Points in WebView2 | Same data, additional view |
| Local LLM (S6 only) | **Phi-3-mini-4k-instruct** ONNX or **Llama-3.2-1B** via **LLamaSharp** | ~1–2 GB, gated behind explicit opt-in download |

**New project:** `Epub.Search` (class lib) — chunking, embedding pipeline, sqlite-vec store, query API. Zero WinUI / WebView2 deps so it stays testable.

## 4. Database schema

Extends the existing `library.db` (created in M7) with new tables. All text content lives in `chunks`; `embeddings` is a sqlite-vec virtual table; layout/cluster columns are written by the refit job.

```sql
CREATE TABLE chunks (
  id           INTEGER PRIMARY KEY,
  book_id      INTEGER NOT NULL,        -- FK into library books table
  spine_idx    INTEGER NOT NULL,        -- back-link into reader's IBookLocation
  char_offset  INTEGER NOT NULL,
  char_length  INTEGER NOT NULL,
  text         TEXT NOT NULL,
  added_at     INTEGER NOT NULL         -- unix ms; used for "new since last refit"
);
CREATE INDEX idx_chunks_book ON chunks(book_id);

CREATE TABLE embeddings (             -- raw float[384] BLOB; loaded once into RAM and scanned with Vector<T> SIMD
  chunk_id     INTEGER PRIMARY KEY,
  vec          BLOB NOT NULL          -- MemoryMarshal.AsBytes(ReadOnlySpan<float>); 384 × 4 = 1536 bytes
);

CREATE TABLE chunk_layout (             -- written by UMAP refit
  chunk_id     INTEGER PRIMARY KEY,
  umap_x       REAL NOT NULL,
  umap_y       REAL NOT NULL,
  z            REAL NOT NULL,           -- third PCA component for 3D
  layout_version INTEGER NOT NULL       -- which refit produced these coords
);

CREATE TABLE clusters (
  id           INTEGER PRIMARY KEY,
  label        TEXT NOT NULL,           -- c-TF-IDF terms; LLM-rewritten in S6
  centroid     BLOB NOT NULL,           -- 384-dim float for fast assignment
  color        TEXT NOT NULL,           -- stable across refits via Hungarian match
  layout_version INTEGER NOT NULL
);

CREATE TABLE chunk_clusters (
  chunk_id     INTEGER PRIMARY KEY,
  cluster_id   INTEGER NOT NULL,
  layout_version INTEGER NOT NULL
);

CREATE TABLE index_meta (
  key          TEXT PRIMARY KEY,        -- 'model', 'last_full_refit', 'layout_version', 'book_count_at_refit'
  value        TEXT NOT NULL
);

CREATE TABLE concept_threads (          -- S6
  id           INTEGER PRIMARY KEY,
  query        TEXT NOT NULL,
  created_at   INTEGER NOT NULL,
  passages_json TEXT NOT NULL           -- ordered chunk_ids + transition prose
);
```

## 5. Indexing pipeline

**Per book (called for each book at bulk-build time and again when a new book is added):**

1. Re-use existing `EpubReader.OpenAsync` to get spine + chapter text.
2. **Chunk**: paragraph-level, 256-token cap (BertTokenizer counts), 32-token overlap. Track `(book_id, spine_idx, char_offset, char_length)` so every chunk back-links into the existing reader via `LoadBookAsync`.
3. **Embed** each chunk through MiniLM ONNX in batches of 32; insert into `chunks` + `embeddings`.
4. **Append-place** (incremental only — see §6): assign each new chunk to nearest existing cluster centroid; project into existing UMAP space via weighted-centroid of its 15 nearest already-laid-out neighbors.

**Bulk build runs all books, then triggers an initial refit (HDBSCAN + UMAP from scratch).**

## 6. Incremental indexing — adding books over time

User has stated books are added but never removed. The strategy is **append-fast, refit-on-request**.

### Fast append (automatic, on every new book)

Triggered when the library scan detects a new `.epub` not yet in `chunks`. Runs on a background worker.

1. Chunk + embed the new book (§5 steps 1–3).
2. **Cluster assignment:** for each new chunk, compute cosine distance to every existing cluster centroid; assign to nearest. Cheap — N_chunks × N_clusters dot products. Write `chunk_clusters` rows with the *current* `layout_version`.
3. **Layout placement:** for each new chunk, find its 15 nearest neighbors in `chunks` that already have `chunk_layout` rows for the current `layout_version`; write its `(umap_x, umap_y, z)` as the cosine-weighted centroid of those neighbors' coords. This is "datum projection" — Atlas does the same thing. Approximate but visually correct.
4. Update `index_meta.book_count_at_refit_delta`.

After step 4, search and the orbital, map, and 3D views all show the new content immediately and approximately correctly. The user sees no UI gap.

### Drift and the case for refits

Fast-append accumulates two kinds of drift:

- **Cluster drift** — a new book introduces a topic with no existing centroid; its chunks get force-assigned to the nearest unrelated cluster. The Discover page shows the new chunks under a wrong label until refit.
- **Layout drift** — projection-by-neighbors is locally correct but globally distorted; new dots crowd into existing regions even when they should form new ones.

These don't break anything — search ranking is unaffected (it's pure embedding distance) — but the *map* and *theme cards* degrade in quality the further the corpus grows past the last refit.

### Refit (manual or auto-prompted)

Settings → "Rebuild themes & map." Heavy: minutes for the full library. Two trigger paths:

- **Manual**: button is always available.
- **Auto-prompt**: when `(books_added_since_refit / book_count_at_refit) > 0.20`, show a one-time toast: "You've added X new books — refresh the theme map?" Don't auto-run; user picks.

Refit pipeline:

1. Run HDBSCAN over all embeddings → new clusters.
2. **Stable colour assignment**: match new cluster IDs to old cluster IDs by Hungarian matching on centroid cosine similarity, then carry forward old colours. Prevents the map from suddenly recolouring everything.
3. Run UMAP over all embeddings → new `(umap_x, umap_y)`, third PCA component → `z`.
4. Bump `layout_version`; write new `chunk_layout`, `clusters`, `chunk_clusters` rows tagged with the new version.
5. Old layout rows can be dropped after success (no removal contract for *content*; layout is regenerable).
6. Map page animates dots from old → new positions on next open. Tron-style.

### What survives a refit

- `chunks` and `embeddings` — never rewritten, just re-clustered/re-projected.
- `concept_threads` — pin to `chunk_id`, so the threads themselves still resolve.
- `reading_positions` — entirely independent of search infra.

### What breaks if the user does delete a book

Out of scope per locked decision, but to leave a paper trail: chunks would orphan their `book_id`; clusters would have phantom members; layout would have phantom dots. Adding deletion later would mean a tombstone table and a sweep job. Don't build it now.

## 7. Milestones

Each is a usable increment.

### S1 — Search foundation (~3 days)

- Spike first: **verify sqlite-vec loads on ARM64 Windows** in the existing process. Fall back to a side index (HNSW.Net) if it doesn't.
- New `Epub.Search` project; chunking, MiniLM ONNX wrapper, sqlite-vec store.
- **Indexing UX**: Settings → "Build semantic index" with progress (book N of M, chunk N of M); resumable; runs on `Task.Run` and respects window-close.
- **Search UI**: new `SearchPage` accessible from NavigationView; query box, ranked results (snippet + book cover thumbnail + author + page), click → reuses `LoadBookAsync(spineIdx, charOffset)`.
- Incremental fast-append (§6) wired into the existing library scan.

**Done when:** From cold open on a fresh DB, build the index for all 47 books in one sitting; type "free will" and get passages from across multiple books, each opening at the right paragraph. Add a new book to `C:\reading`, restart, see it indexed automatically without rebuilding.

### S2 — Orbital neighbors in the reader (~2 days)

The killer-app moment: while reading, find related passages across the library.

- Right-click (long-press for touch) any paragraph in WebView2 → "Show neighbors."
- Modal overlay: 20 nearest chunks shown as a force-directed orbit (D3 / vis-network in a sibling WebView2 or popup), seed paragraph at center, nodes coloured by cluster, sized by distance.
- Hover → snippet tooltip; click → snippet card with "Open in [book title] →" link.
- Bridge: `reader.js` gains `getSelectionContext()` posting `{spineIdx, charOffset, text}` to host; host queries sqlite-vec; results posted back to the overlay.
- Reference UX: Connected Papers' radial layout.

**Done when:** Reading any book, right-click a paragraph, see related passages from at least 3 other books in the orbit, click one, land on its page in the reader.

### S3 — Cluster-cards "Discover" page (~3 days)

Library-level browsing by theme rather than by book.

- After bulk build / refit, run HDBSCAN; persist clusters.
- **Auto-labels (no LLM yet)**: top-5 c-TF-IDF terms per cluster; pick the chunk with the highest centroid-cosine as "quote of the cluster."
- New `DiscoverPage`: GridView of cluster cards. Each card = label + quote + thumbnail strip of contributing book covers + chunk count.
- Click card → cluster detail page: passages list grouped by book, per-passage "open here" link.
- "Surprise me" button: random walk into a low-density cluster.
- Reference UX: Atlas cluster browse + Spotify "Made for you" cards.

**Done when:** Discover page shows ~15–30 auto-labelled themes from the 47-book library; each card opens to a usable passage list; gut-check pass on label accuracy.

### S4 — Zoomable 2D map (~3 days)

The "see your whole library at once" moment.

- Precompute UMAP 2D at refit time → `chunk_layout`.
- New `MapPage` hosts a WebView2 with deck.gl ScatterplotLayer.
- 100k+ points coloured by cluster; HexagonLayer density rendering when zoomed out.
- Hover → snippet tooltip; click → snippet card with open-in-book link.
- Pan/zoom Google-Maps style; cluster labels appear at appropriate zoom levels (semantic LOD).
- "Refresh layout" affordance triggers a refit (§6).
- Reference UX: Nomic Atlas, Map of Reddit.

**Done when:** Open Map page, see a recognisable terrain of dots, zoom into a region, identify what topic it is from the labels, click a point, jump to the passage.

### S5 — 3D starfield overview (~2 days)

Wow-mode toggle on top of S4. Pure UI work, no new data.

- "Toggle 3D" button on Map page → swap WebView2 page from deck.gl to three.js.
- Same `(x, y)` from `chunk_layout` plus `z`.
- Camera: free-fly with WASD + mouse-look, plus "snap to cluster" when clicking a star.
- Click star → smooth fly-in, then drop back to 2D map zoomed on that region.
- Performance budget: 100k Points-material at 60 fps on the user's ARM64 box.

**Done when:** Toggle 3D, fly through the library starfield, pick a cluster, smooth-zoom in, click a star, hop into the book at the right page.

### S6 — Concept thread (the novel one, ~5 days)

Pick a concept, get a curated reading sequence stitched from passages across books that progress through the idea. No prior art I'm confident in — this is the experiment.

- Local LLM dependency lands here: Phi-3-mini-4k-instruct ONNX (~2 GB), gated behind a "Download AI features" prompt. Not bundled.
- "New thread" UI: query box + length slider (5/10/20 passages).
- Pipeline:
  1. Embed query → top-K (~200) candidate chunks from `IVectorIndex.SearchAsync`.
  2. LLM ranks/orders/filters into a coherent sequence and writes a one-sentence transition between each pair.
  3. Render as a vertically-scrolling "anthology" page: passage cards, transition prose between, "open in book" affordance per card.
- Save threads to `concept_threads`; revisit later.
- **Retrofit**: same LLM relabels S3 clusters with better names on next refit.

**Done when:** Type "how attention works in neural networks", get 10 passages drawn from 4+ books in a coherent reading order, with a sentence between each explaining the transition, and each passage links to its source page.

## 8. Risks & open questions

| Risk / question | Note |
|---|---|
| ~~sqlite-vec on ARM64 Windows~~ — *resolved 2026-04-27* | sqlite-vec ships no win-arm64 prebuilt; locked to managed brute-force cosine over `float[]` BLOBs behind `IVectorIndex`. P0 spike (`tests/Epub.Library.Tests/EmbeddingStorageSpikeTests.cs`) confirms 10k × 384-dim brute-force scan in <500 ms. |
| Indexing time for 47 books | Unknown until measured. all-MiniLM-L6-v2 on CPU does roughly 1–2k chunks/sec; library is probably 200k–500k chunks → minutes, not hours. Confirm during S1 spike. |
| UMAP.NET maturity | Less battle-tested than the Python original. Fallback: t-SNE.NET, or a one-shot UMAP via ONNX-wrapped Python embedding model. |
| Datum-projection quality (§6) | Acceptable for visual continuity between refits, not as good as a fresh fit. Refit threshold (20%) is a guess — tune after watching real growth. |
| Cluster-label quality without LLM | c-TF-IDF labels are often awkward. Acceptable for v1; S6 LLM retrofit upgrades them. |
| WebView2 ↔ deck.gl ↔ host bridging cost | Bench early — high-frequency hover events crossing the boundary can stutter. Throttle. |
| LLM binary size in S6 | 2 GB is a lot. Explicit opt-in download, not bundled. |
| Hungarian-match colour stability | If many clusters appear/disappear between refits, matching is ambiguous. Fall back to "carry colour for top-N most similar; assign new palette to the rest." |

## 9. Out of scope (this branch)

- Book removal / orphan cleanup — locked out per user statement
- Cross-device sync of index / threads
- Hosted embedding or LLM APIs
- "Ask your library" Q&A / RAG
- Fixed-layout / image-only books — chunks need text; PDF-style books can be skipped at indexing time with a notice

## 10. Relationship to the original M8–M12 polish track

`plan.md` lists M8 (themes), M9 (bookmarks), M10 (in-book search), M11 (polish), M12 (packaging) as the original post-MVP milestones. They remain valid but become a parallel polish track on `main` while semantic-search is in flight. Two natural bridges to watch:

- **M9 ↔ S2** — bookmarks/highlights and orbital neighbors both need a robust passage-selection bridge from WebView2. Build it once in whichever lands first.
- **M10 ↔ S1** — in-book full-text search and library-wide semantic search can share a single SearchPage with a "this book / library" toggle.
