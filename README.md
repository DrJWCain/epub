# Epub.App

A native Windows EPUB reader, optimised for the **Microsoft Surface Pro 12 (ARM64)** and touch.

Tap the left third of a page to go back, the right third to go forward. Pages flow seamlessly across chapter boundaries. Close the app and reopen the same book — it lands where you left off.

## Status

MVP complete (M0–M7). Genuinely usable on a daily-driver Surface.

| Feature | State |
|---|---|
| EPUB 2 + 3 parsing | ✅ Tested against 47 real books across 4 publisher templates (No Starch, O'Reilly, Manning, Pearson) |
| Reflowable layout, single-column pagination | ✅ CSS-columns + transform stride; 200ms page transitions |
| Cross-chapter page flow | ✅ Page-forward at end of chapter advances spine; page-back from page 0 lands on the previous chapter's last page |
| Touch tap-to-turn | ✅ Left 40% / right 40%; middle 20% inert. Pointer-event-aware, doesn't double-fire on synthesised clicks |
| Keyboard navigation | ✅ ←/→, PgUp/PgDn, Space |
| Table of contents sidebar | ✅ Flat list with depth indent; intra-chapter anchor scrolling |
| Library | ✅ Point at a folder, scans EPUBs in place (no copying), grid of cover tiles |
| Last-read position | ✅ SQLite-backed, debounced save, restored on open |
| Maximized / borderless fullscreen | ✅ Launches maximized; F11 toggles real fullscreen (collapses the title bar) |
| Book title in title bar | ✅ Updates when a book is open, resets on the Library tab |

Themes/typography, bookmarks/highlights, in-book search, and MSIX packaging are post-MVP (see `plan.md`).

## Why a custom reader

The Surface Pro 12 has a beautiful 12" panel and runs Windows on ARM64. Most EPUB readers fall into one of three buckets: web-based (Electron, Tauri — heavy and don't feel native), Win32 holdovers (don't know about mica or snap layouts), or universal cross-platform apps (compromise on Windows feel). This one is built specifically for Windows 11 on ARM, with mica chrome and touch as a first-class input.

## Tech stack

- **WinUI 3 / Windows App SDK 1.8** — native shell, mica backdrop, snap layouts, custom title bar
- **WebView2** — renders EPUB content (Chromium handles whatever XHTML/CSS/JS the book throws at it)
- **C# / .NET 10**
- **SQLite** (Microsoft.Data.Sqlite) — local library + reading positions
- **Custom `epub://` URL scheme** — serves resources straight out of the open `ZipArchive`, no extraction to disk

## Build and run

Requires Visual Studio 2026 (or the .NET 10 SDK + Windows App SDK 1.8 workloads) on a Windows 11 ARM64 machine.

```bash
dotnet build epub.slnx -p:Platform=ARM64
dotnet test  epub.slnx -p:Platform=ARM64
```

To launch from the IDE, open `epub.slnx` in Visual Studio 2026 and F5 the `Epub.App` project.

The first launch is empty — open **Settings**, point the library at a folder of `.epub` files (e.g. `C:\reading`), and they'll appear as covers.

## Project layout

```
src/
  Epub.Core/      EPUB parser — container/OPF/nav/NCX, models, no UI deps. net10.0.
  Epub.Library/   Library scan + SQLite-backed reading positions. net10.0.
  Epub.Renderer/  WebView2 host UserControl + injected reader.js (pagination, tap-zones,
                  anchor scrolling). net10.0-windows.
  Epub.App/       WinUI 3 shell — Library, Reader, Settings pages; DI host. ARM64-only.
tests/
  Epub.Core.Tests/    190 unit + integration tests (skips integration if C:\reading missing)
  Epub.Library.Tests/ (placeholder — store tests pending)
plan.md           Build plan, decisions log, real-world fixes from rendering 47 books.
```

Hard rule: `Epub.Core` has zero dependency on WinUI, WebView2, or SQLite. The parser is reusable for a future CLI/import tool.

## Locked design decisions

These are settled and documented in `plan.md`:

- Single-book per window (no tabs)
- Library is "in place" — points at user folders, doesn't copy or move EPUBs
- Windows 11 only; ARM64 only for now (x64 is a one-line `csproj` change later)
- Location format v1 is `{spineIndex, pageInChapter}` behind an `IBookLocation` interface — real EPUB CFI is swappable in for v2 (needed for stable bookmarks)
- Test fixtures are 2–3 synthetic EPUBs committed; integration suite against a real `C:\reading` folder is local-only and skipped in CI

## License

MIT — see [LICENSE](LICENSE).
