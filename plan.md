# WinUI 3 EPUB Reader — Build Plan

## Status snapshot (2026-04-26)

**7 of 8 MVP milestones complete.** App is genuinely usable: open it, see your 47 books as covers, tap one, page through with arrows / buttons / taps, jump around via TOC. Chapters flow seamlessly into each other. Tap-zones work on the Surface tablet.

| | Milestone | Commit | Notes |
|---|---|---|---|
| ✅ | M0 — Bootstrap | `2e0ed38` | WinUI 3 NavigationView shell, ARM64-only, .NET 10, SLNX |
| ✅ | M1 — EPUB parser | `55d386b` | 79→190 tests; works on all 47 real books |
| ✅ | M2 — WebView2 render | `3355138` | Custom `epub://` scheme, in-place ZipArchive serving |
| ✅ | M3 — Spine navigation | `61f347a` | Prev/next + chapter indicator + KB accelerators |
| ✅ | M4 — Pagination | `a7033f7` | CSS columns, JS bridge, page-spanning prev/next, reading margins |
| ✅ | M5 — TOC sidebar | `a60b46c` | SplitView + flat-list, anchor scrolling, nav-href fix |
| ✅ | (Settings) | `f1440cc` | ApplicationData.LocalSettings + library folder picker |
| ✅ | M6 — Library + cover grid | `14abda0` | Scans folder, GridView of cover tiles, click-to-read |
| ✅ | (Tap-zones) | `8ec56dd` | Touch tap-to-turn-page (left 40% prev, right 40% next) |
| ⏭️ | **M7 — Persistence** | — | **MVP closer**: SQLite for last-read position |
| ⏳ | M8–M12 | — | Themes/typography, bookmarks/highlights, search, polish, MSIX packaging |

**Open follow-ups (parked, not blockers):**
- **Cover/title-page polish** — handful of books still render cover on page 2/2 (publisher CSS edge cases beyond the 60px slack). Tracked.
- FluentAssertions 8 license warning on test runs (drop to AwesomeAssertions if it gets annoying)
- `.gitattributes` for `* text=auto` to silence CRLF warnings

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
- IDE: Visual Studio Community 2026
- Runtime: **.NET 10** (10.0.201, ships with VS 2026), Windows App SDK 1.8
- Solution format: **SLNX** (XML, cleaner git diffs)
- Source control: local git, GitHub private remote (set up on request, not auto-pushed)
- Location format v1: simple `{spineIndex, charOffset}` — design behind an `IBookLocation` interface so real EPUB CFI can be swapped in for v2
- Test fixtures: 2–3 synthetic EPUBs committed; integration suite against `C:\reading` runs locally only (skipped in CI)

**Deferred to later milestones (not blockers for M0–M1):** code-block overflow strategy (M4), single vs. two-page spread (M4), home-screen layout (M6), per-book settings (M8), search scope (M10).

## 1. Tech stack (locked-in choices)

| Concern | Choice | Why |
|---|---|---|
| Shell framework | **WinUI 3 / Windows App SDK 1.8** | Native Windows 11 look, mica, snap layouts |
| Language | **C# / .NET 10** | Ships with VS 2026, no extra SDK install |
| Reader pane | **WebView2** | Chromium handles all XHTML/CSS/JS that EPUB throws at it |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated boilerplate, no ceremony |
| DI | **Microsoft.Extensions.DependencyInjection** | Standard, painless |
| ZIP | **System.IO.Compression** | Built-in, sufficient |
| XML | **System.Xml.Linq** | Built-in, OPF/nav are simple |
| DB | **Microsoft.Data.Sqlite** | Lightweight, no server |
| Tests | **xUnit + FluentAssertions** | (FA 8 license warns on every run — swap to AwesomeAssertions later if annoying) |
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

The target library is **47 technical books** from four publishers (snapshot 2026-04-26 — grew from 8 to 47 during M0–M6 development).

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

- **M2 [done]:** font MIME types (`font/otf`, `font/woff2`) handled by `MimeTypes` fallback in resource handler. No Starch annotation fonts work.
- **M4 [done]:** code-block strategy = `overflow-x: auto` on `<pre>`. `break-inside: avoid` set on `<pre>`, `<aside>`, `<figure>`, headings. Pagination is lazy via single layout pass.
- **M5 [done with caveat]:** **TOC hrefs resolve against the nav.xhtml/NCX file's own zip directory, NOT the OPF's** — caught by Building a Debugger which has nav at `OEBPS/xhtml/nav.xhtml` with hrefs relative to that subdir.
- **M8 (still pending):** detect Pearson-style image-based code (manifest signal: `graphics/` folder + many `f####-##.jpg` filenames + low text-to-image ratio). Surface a small notice on the reader chrome.

## 7. Real-world fixes that emerged during build

These weren't in the original plan but real EPUBs forced them:

- **Cover detection fallback** — Write Great Code v3 has a broken OPF (`<meta name="cover" content="cover-image"/>` but the actual image item has `id="covera"`). Added a third fallback: any image-typed manifest item whose id contains "cover".
- **Figure-margin override for cover pages** — multiple publishers' `<figure>` rules (e.g., O'Reilly's `#sbo-rt-content figure { margin: 15px auto !important }` at specificity 1,1,1) push cover figures past column height, then `break-inside: avoid` punts them entirely to the next column. Override with `#__epub_reader_content[id] figure { margin: 0 !important }` (matching specificity, cascade later).
- **Image height slack** — `img { max-height: calc(100vh - 48px - 60px) }` leaves 60px for ancestor wrapper margins (e.g. WGC v2's `<div class="cover">` with publisher CSS adding `.2em` margin).
- **Reading margins via column-gap, not padding** — padding inside the wrapper made transform-stride mismatch column-stride, leaving content offset. Solution: `wrapper margin: 24px 48px; column-gap: 96px` (= 2× side margin), so the gap absorbs the visual padding without leaking next-page content.
- **Anchor scrolling** — element offsets in column flow: use `getBoundingClientRect` (forces layout flush, more reliable than raw `offsetLeft`) with a 4 px subpixel tolerance. Strip URL hash with `history.replaceState` before init runs so the browser doesn't try its own anchor-scroll and shift `body.scrollLeft`.
- **Touch tap-to-turn** — needs both `pointerdown` (touch/pen) and `click` (mouse) handlers; just `click` alone doesn't fire reliably for finger taps in WebView2-on-WinUI-3. `touch-action: manipulation` kills the legacy 300 ms tap delay.
- **WebView2 background** — defaults to transparent, which on a mica window bleeds through as black. Set `DefaultBackgroundColor="White"` and `Profile.PreferredColorScheme = Light` so EPUB content with no explicit background renders correctly.

## 8. Deferred past v1

- LCP DRM (library books) — significant undertaking, separate phase
- Cloud sync of positions/highlights
- Read-aloud / SMIL media overlays
- OPDS catalog browsing
- AZW3/MOBI conversion (point users at Calibre)
- Image-based-code detection notice (M8 polish)
- Cover-page polish for remaining publisher edge cases

## 9. Next step

**M7 — Persistence**: SQLite-backed last-read position per book, so reopening a book lands on the page you left. Plan budget 1 day. Closes the MVP.
