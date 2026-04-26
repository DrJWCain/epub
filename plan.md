# WinUI 3 EPUB Reader — Build Plan

## 0. Decisions locked (2026-04-26)

**Product / scope:**
- Single-book per window (no tabs)
- Personal-first, kept shareable later
- Library = "in place": point at user folders (e.g. `C:\reading`), don't copy

**Platform:**
- Windows 11 only (uses mica/snap-layouts directly, no fallback paths)
- **ARM64 native** (user's machine); x64 added later if needed
- `<Platforms>ARM64</Platforms>`, `<RuntimeIdentifier>win-arm64</RuntimeIdentifier>`

**Engineering:**
- IDE: Visual Studio Community 2026 (ships .NET 9 + Windows App SDK templates)
- Solution format: **SLNX** (XML, cleaner git diffs)
- Source control: local git, GitHub private remote (set up on request, not auto-pushed)
- Location format v1: simple `{spineIndex, charOffset}` — design behind an `IBookLocation` interface so real EPUB CFI can be swapped in for v2
- Test fixtures: 2–3 synthetic EPUBs committed; integration suite against `C:\reading` runs locally only (skipped in CI)

**Deferred to later milestones (not blockers for M0–M1):** code-block overflow strategy (M4), single vs. two-page spread (M4), home-screen layout (M6), per-book settings (M8), search scope (M10).

## 1. Tech stack (locked-in choices)

| Concern | Choice | Why |
|---|---|---|
| Shell framework | **WinUI 3 / Windows App SDK 1.7+** | Native Windows 11 look, mica, snap layouts |
| Language | **C# / .NET 9** | Ships with VS 2026, no extra SDK install |
| Reader pane | **WebView2** | Chromium handles all XHTML/CSS/JS that EPUB throws at it |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated boilerplate, no ceremony |
| DI | **Microsoft.Extensions.DependencyInjection** | Standard, painless |
| ZIP | **System.IO.Compression** | Built-in, sufficient |
| XML | **System.Xml.Linq** | Built-in, OPF/nav are simple |
| DB | **Microsoft.Data.Sqlite** | Lightweight, no server |
| Tests | **xUnit + FluentAssertions** | Standard |
| Packaging | **MSIX** (single-project, unpackaged dev mode initially) | Store-ready when needed |

**Deliberately not using:** VersOne.Epub or other third-party EPUB libs — parser is small enough to own and you'll need to extend it for CFI/highlights anyway.

## 2. Solution layout

```
epub.sln
├─ src/
│  ├─ Epub.Core/           class lib — parser, models, no UI deps
│  │   ├─ Parsing/         (Container, Opf, Nav, ManifestItem, Spine)
│  │   ├─ Cfi/             (CFI generator + resolver)
│  │   └─ Models/          (Book, Chapter, TocNode, Metadata)
│  ├─ Epub.Library/        SQLite library service
│  │   ├─ LibraryService.cs
│  │   ├─ Migrations/
│  │   └─ Entities/
│  ├─ Epub.Renderer/       WebView2 host control + injected JS/CSS
│  │   ├─ ReaderControl.xaml
│  │   ├─ Resources/reader.js
│  │   ├─ Resources/reader.css
│  │   └─ EpubResourceHandler.cs   (virtual host → zip)
│  ├─ Epub.App/            WinUI 3 shell
│  │   ├─ App.xaml
│  │   ├─ Views/           (LibraryPage, ReaderPage, SettingsPage)
│  │   ├─ ViewModels/
│  │   └─ Services/        (theme, navigation, dialog)
└─ tests/
   ├─ Epub.Core.Tests/
   └─ Epub.Library.Tests/
```

Hard rule: **`Epub.Core` has zero dependency on WinUI, WebView2, or SQLite.** Keeps the parser testable and reusable for a future CLI/import tool.

## 3. Milestones (each one is a usable increment)

### M0 — Bootstrap (½ day)
- Create solution, projects, project refs
- Wire CommunityToolkit.Mvvm + DI host
- Empty NavigationView shell with Library/Reader/Settings pages
- Set up xUnit projects, CI placeholder

**Done when:** App launches, navigates between three blank pages.

### M1 — EPUB parser (2 days)
- `EpubReader.OpenAsync(path)` returns a `Book`
- Parses `META-INF/container.xml` → finds OPF
- Parses OPF: metadata, manifest, spine
- Parses `nav.xhtml` (EPUB 3) and falls back to NCX (EPUB 2)
- Resource resolver: given a manifest item, return a stream

**Done when:** Unit tests pass against 5 real EPUBs (mix of EPUB 2/3, fixed/reflowable). Test fixtures committed.

### M2 — Single-chapter render (1 day)
- `ReaderControl` hosts WebView2
- Implement `EpubResourceHandler` using `CoreWebView2.AddWebResourceRequestedFilter` + a custom virtual scheme `epub://book/<path>`
- Load first spine item

**Done when:** Open an EPUB, see chapter 1 rendered correctly with its own CSS, fonts, and images served from inside the zip.

### M3 — Spine navigation (½ day)
- Next/prev chapter buttons
- Keyboard shortcuts (←/→, PgUp/PgDn, Space)
- Progress indicator (N of M chapters)

### M4 — Pagination within chapter (3 days — the hard one)
- Inject `reader.css` that applies `column-width: 100vw; column-gap: 0; height: 100vh; overflow: hidden`
- Inject `reader.js` that:
  - Measures `scrollWidth`, computes page count
  - Translates the body horizontally on next/prev page
  - Bridges page transitions to chapter transitions at the edges
- Handle resize → recompute page count, preserve current location
- Smooth animation between pages

**Done when:** A 30-page chapter paginates cleanly, swipe and arrow keys feel responsive, resizing the window doesn't lose your spot.

### M5 — TOC sidebar (½ day)
- Collapsible NavigationView pane showing parsed TOC tree
- Click → jump to spine item + intra-chapter anchor

### M6 — Library import + grid (2 days)
- Drag-drop `.epub` files onto Library page
- Extract cover image (from OPF `<meta name="cover">` or first image)
- Grid view with covers, titles, authors
- Right-click → remove / show in Explorer

### M7 — Persistence (1 day)
- SQLite schema: `books`, `reading_positions` (bookId, cfi, percent, updatedAt), `bookmarks`, `annotations`
- Save reading position on chapter/page change (debounced)
- Restore on book open
- **CFI** (`Epub.Core/Cfi/`) — port the algorithm from epub.js's CFI module; this is the only "intellectually expensive" piece left. Required for stable bookmarks.

### M8 — Themes & typography (1 day)
- Reader settings flyout: font size, line height, font family, margin width
- Themes: light / dark / sepia / system (mica-aware)
- Settings injected as CSS custom properties into WebView2; no reload needed

### M9 — Bookmarks & highlights (2 days)
- Selection in WebView2 → bridge selection range out via JS-to-host messaging
- Store as CFI range + color + optional note
- Render existing highlights on chapter load via injected JS overlay

### M10 — Search within book (1 day)
- Background index of spine text content (in-memory, lazy)
- Results list with snippet + jump-to-location

### M11 — Settings, accessibility, polish (2 days)
- Keyboard nav for everything
- Screen reader labels (`AutomationProperties`)
- High contrast theme respect
- Error states (corrupt EPUB, missing resources)
- About / licenses page

### M12 — Packaging (1 day)
- MSIX manifest, file association for `.epub`
- Test install/uninstall
- Icons, store assets if going to Store

**MVP = M0–M7.** That's a usable reader in ~2 weeks of focused work.

## 4. Key technical approaches

### Serving zip resources to WebView2
Use `CoreWebView2.AddWebResourceRequestedFilter("epub://*", All)` and a `WebResourceRequested` handler that:
1. Parses the URL → manifest path
2. Opens a `ZipArchiveEntry` stream from the open `ZipArchive`
3. Returns a `CoreWebView2WebResourceResponse` with correct MIME type

Keep one `ZipArchive` open per book. Don't extract to disk — slow, leaks, and breaks if the user moves the source file.

### Pagination JS contract
`reader.js` exposes a tiny API the host calls via `ExecuteScriptAsync`:
- `Reader.goToPage(n)`, `Reader.nextPage()`, `Reader.prevPage()`
- `Reader.goToCfi(cfi)`
- `Reader.getCurrentCfi()` → returned via `WebMessageReceived`
- `Reader.applySettings({ fontSize, lineHeight, ... })`

Host listens for `pageChanged`, `selectionChanged`, `linkClicked` messages.

### CFI
Implement `CfiGenerator.FromRange(range)` and `CfiResolver.ToRange(cfi, document)` in `Epub.Core`. Reference: epub.js's `epubcfi.js`. This is ~500 lines of careful code; budget a full day for it in M7.

### Threading
Parsing and library scans run on `Task.Run`. UI thread does only UI. Use `IAsyncRelayCommand` from CommunityToolkit for buttons that trigger I/O.

## 5. Risks & mitigations

| Risk | Mitigation |
|---|---|
| WinUI 3 control gaps (e.g., no native PDF viewer) | Out of scope for v1; EPUB only |
| WebView2 zip serving has CORS or MIME quirks | Build M2 spike early; if it fails, fall back to extracting to per-book temp dir |
| Fixed-layout EPUBs need different layout path | Detect via OPF `rendition:layout`; render with viewport meta as-is, paginate by spine item not by column |
| CFI implementation drift from epub.js | Cross-test against epub.js outputs on the same fixtures |
| WebView2 runtime not installed on user's machine | Detect at startup, prompt to install (evergreen runtime is standard on Win11) |

## 6. Real-world content profile (from `C:\reading`)

The target library is **19 technical books** from four publishers (snapshot 2026-04-26).

**Publisher templates encountered:**

| Publisher | Code annotation style | Code listing | Folder layout | Sample size |
|---|---|---|---|---|
| **No Starch** | Embedded OTF fonts with circled-number glyphs | HTML `<pre><code>` with inline `<span>` markup | Flat `OEBPS/` | ~1 MB |
| **O'Reilly** | Tiny PNG assets (`assets/1.png`, etc.) | HTML `<pre><code>` with inline `<span>` markup | Flat `OEBPS/` | 2–10 MB |
| **Manning** | Inline numbered references | HTML `<pre><code>` | Nested `OEBPS/OEBPS/Text/` | 2–6 MB |
| **Pearson InformIT** | N/A — see code listing column | **Rasterized JPGs** (`graphics/f0539-01.jpg`) | Flat `OEBPS/` with huge `graphics/` | 86 MB+ |

**Universal traits:**
- All EPUB 3, all reflowable (no fixed-layout encountered).
- Chapter HTML files run 100–230 KB; lazy pagination required.
- Resource paths are always **relative to the OPF**, never to zip root.
- Mixed `.html` and `.xhtml` extensions — trust manifest `media-type`, not extension.
- `<aside epub:type="sidebar">` for notes; `<span epub:type="pagebreak">` for print-page markers.
- Publisher-styled syntax highlighting baked into book CSS — **don't add our own highlighter.**

**Implications added to milestones:**

- **M2:** verify font MIME types (`font/otf`, `font/woff2`) in `EpubResourceHandler`. Test against No Starch annotation fonts.
- **M4:** spike code-block handling early. Wide `<pre>` lines in narrow columns will overflow — pick from `overflow-x: auto`, force-wrap, or scaled font. Add `break-inside: avoid` to `<pre>` and `<aside>`. Lazy-paginate (current screen first, rest in background) to keep 200+ KB chapters responsive.
- **M5:** nav-first TOC parser, NCX fallback only. Resolve all paths relative to the OPF base URL.
- **M7:** consider exposing print-page markers as "print page N" indicator alongside reflow pagination.
- **M8 (new):** detect Pearson-style image-based code (manifest signal: `graphics/` folder + many `f####-##.jpg` filenames + low text-to-image ratio). Surface a small notice on the reader chrome ("Code in this book is image-based; copy and search are limited") so users aren't confused when copy/search seem broken.

## 7. Deferred past v1

- LCP DRM (library books) — significant undertaking, separate phase
- Cloud sync of positions/highlights
- Read-aloud / SMIL media overlays
- OPDS catalog browsing
- AZW3/MOBI conversion (point users at Calibre)

## 7. First concrete step

Scaffold M0 — create the solution, projects, and the empty NavigationView shell. That's a single session and gives you something to run.
