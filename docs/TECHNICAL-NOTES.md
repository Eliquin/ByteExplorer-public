# ByteExplorer: technical notes

The project's detailed notes: how to build and run it on each system, how the code is laid out, and every feature with
how it works and why it was built that way. For an overview, see the [README](../README.md).

The app began as a Windows-only WPF program and was rebuilt on Avalonia for Windows, Linux and macOS; the section
"Changes from the WPF version" below describes the features as they are now, compared with that first version.

## License

ByteExplorer is free and open-source software under the **[Mozilla Public License 2.0](../LICENSE)**.
Copyright © 2026 Eliquin and ByteExplorer contributors.

In short: anyone may use, change and share it, also in other (including commercial) programs; changes to ByteExplorer's
own files must stay under the MPL 2.0 and be published with any release that includes them, while files of their own
may use any license. Each source file carries the MPL 2.0 notice (Exhibit A); none carries the "Incompatible With
Secondary Licenses" notice, so the code can be combined with GPL and LGPL code.

Contributions are accepted under the same license: by opening a pull request you agree that your contribution is
licensed under the MPL 2.0.

The third-party components (Avalonia, VLC, SkiaSharp, Serilog, the Inter font, the Fluent icons and others), their
licenses and VLC's source are listed in **[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)**; their license texts are
in [`licenses`](../licenses). `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/` are copied next to the program in every
build and publish, and Settings › About › Legal points to them.

### Updates and network access

New versions are published on the GitHub repository; you update by downloading a new release yourself. ByteExplorer
has **no automatic updates and no update check**, and it never connects to the internet on its own: it contains no
HTTP or socket code, no telemetry, and its usage history (Home's "Your activity") stays in its data folder. The only
connections it makes are the ones you ask for (opening a network location, a phone over USB) and a pipe on your own
computer through which a second start hands a folder to the running window. Contributions must keep it that way.
The two links in Settings › About (the project page and the Ko-fi page, `SettingsViewModel.ProjectUrl`/`SupportUrl`)
open in the browser only when clicked.

Donations: the author's Ko-fi page is <https://ko-fi.com/eliquin081>; `.github/FUNDING.yml` shows it as the
repository's "Sponsor" button.

### Publishing a release

- Ship the build folder as it is: `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/` must stay next to `ByteExplorer.exe`.
- Windows releases include VLC (LGPL 2.1+, some plugins GPL 2.0+): attach VLC's source,
  `vlc-3.0.21.tar.xz` from <https://download.videolan.org/pub/videolan/vlc/3.0.21/>, to the release (or keep it
  downloadable from the project for three years), as `THIRD-PARTY-NOTICES.md` promises. If the LibVLC package version
  changes, update the version and link there.
- When a package is added or updated, add or update its row and license in `THIRD-PARTY-NOTICES.md`
  (`dotnet list ByteExplorer.csproj package --include-transitive` lists everything shipped).

## Build and run

Requirements: the .NET 9 SDK (or newer; .NET 10 builds it too). The app restores from these packages only (all from
nuget.org; an offline machine needs them in a local package folder): Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent
11.3.22, Avalonia.Controls.DataGrid 11.3.13, Avalonia.Fonts.Inter 11.3.22, Avalonia.Diagnostics 11.3.22 (Debug only), LibVLCSharp 3.10.2,
VideoLAN.LibVLC.Windows 3.0.21 or later 3.0.x, System.Windows.Extensions 9.0.11, Serilog 4.2.0, Serilog.Sinks.Debug 3.0.0,
Serilog.Sinks.File 6.0.0, plus their dependencies. The icon font is bundled (`Assets/Fonts`, see THIRD-PARTY-NOTICES.md)
and the UI font is Inter on every OS.

```bash
dotnet build ByteExplorer.sln -c Release
dotnet run --project ByteExplorer.csproj -c Release
dotnet test ByteExplorer.sln
```

Publish a self-contained build for one OS:

```bash
dotnet publish ByteExplorer.csproj -c Release -r linux-x64 --self-contained
```

(`win-x64`, `osx-arm64`, `osx-x64` and `linux-arm64` work the same way.)

### Installer

`Tools/Installer/ByteExplorer.iss` is an [Inno Setup 6](https://jrsoftware.org/isinfo.php) script for a Windows
installer; `Tools/Installer/build-installer.ps1` builds it on Windows (needs the .NET 9 SDK and Inno Setup 6):

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Installer\build-installer.ps1               # version from ByteExplorer.csproj
.\Tools\Installer\build-installer.ps1 -Version 1.1.0 -Iscc 'D:\Inno Setup 6\ISCC.exe'      # or with ISCC.exe elsewhere
```

It runs `dotnet publish -c Release -r win-x64 --self-contained false` into `artifacts\publish\win-x64` (without VLC's
unused 32-bit copy) and compiles `artifacts\installer\ByteExplorer-<version>-win-x64-setup.exe`. The installer:

- installs the published folder as it is (`ByteExplorer.exe`, its libraries, VLC's `libvlc\win-x64`, `LICENSE`,
  `THIRD-PARTY-NOTICES.md`, `licenses\`) to `Program Files\ByteExplorer` for all users, or, chosen in its first dialog,
  to `%LOCALAPPDATA%\Programs\ByteExplorer` for the current user only (no administrator rights);
- shows the MPL 2.0 as its license page, adds a Start menu shortcut, an optional desktop shortcut and an uninstaller;
- checks for the .NET 9 runtime (`Microsoft.NETCore.App` 9.x, x64; Avalonia needs no Windows Desktop runtime). When it is
  missing it says where to download it (<https://dotnet.microsoft.com/download/dotnet/9.0>) and lets you go on or stop:
  **it downloads nothing**;
- on uninstall, runs `ByteExplorer.exe --unregister-default-file-manager` first, so if ByteExplorer was the default file
  explorer, folders, drives and Win+E open in File Explorer again. This switch (and `--register-default-file-manager`)
  runs without a window and exits (0 done or nothing to do, 1 failed). It undoes the registration of the account that
  uninstalls; other accounts of an all-users install keep theirs, and can undo it with
  `%LOCALAPPDATA%\ByteExplorer\restore-file-explorer.reg` (also the way back if the .NET runtime was removed first).
  Settings, logs and the search index are kept (in `%LOCALAPPDATA%\ByteExplorer`, or next to the program in a
  current-user install).

There is **no automatic update** in the installer either: a new version is installed by running its installer, which
upgrades in place (same AppId) and keeps the settings.

Code signing is optional. With a certificate, sign the program before packing it and the installer afterwards, e.g.:

```powershell
.\Tools\Installer\build-installer.ps1                                   # publish and compile once
signtool sign /fd SHA256 /td SHA256 /tr <your CA's timestamp URL> /a artifacts\publish\win-x64\ByteExplorer.exe
.\Tools\Installer\build-installer.ps1 -SkipPublish                      # pack the signed program
signtool sign /fd SHA256 /td SHA256 /tr <your CA's timestamp URL> /a artifacts\installer\ByteExplorer-1.0.0-win-x64-setup.exe
```

The script and the installer were written for Windows and reviewed, but not run in this repository's (Linux) build.

### Per-OS notes

| | Windows | Linux | macOS |
|---|---|---|---|
| Window frame | Native frame (Aero Snap, shadow) with custom caption buttons | Custom frame: drag to move, double-click to maximize, edge grips to resize | Native traffic lights |
| Delete | Recycle Bin | freedesktop.org Trash (restorable from GNOME Files, Dolphin, Nemo, Thunar…) | Trash (`~/.Trash`, `.Trashes` on other volumes) |
| Drives | Drive letters | `/` plus volumes under `/media`, `/run/media`, `/mnt` and network mounts | `/` plus `/Volumes/*` |
| Open with | System "Open with" dialog | Desktop entries + `mimeapps.list` (the desktop's own associations) | Pick an app from `/Applications` |
| Terminal | Windows Terminal, else cmd | `$TERMINAL`, `x-terminal-emulator`, GNOME Terminal, Konsole, … | Terminal.app |
| Media preview | libvlc ships with the app | Needs VLC/libvlc installed (`sudo apt install vlc`) | Needs VLC.app in `/Applications` |
| Document preview | The system's preview handlers (Office, PDF, …) | PDF first page with `pdftoppm` (`sudo apt install poppler-utils`); text of .docx/.odt | Same as Linux (`brew install poppler`) |
| Sounds | System sounds | Desktop sound theme (`canberra-gtk-play`, else `paplay`) | `/System/Library/Sounds` |
| Phones (MTP) | Windows Portable Devices, as `phone://Name/Storage/…` | Mounted by the desktop (GVfs `mtp:`/`gphoto2:`, or jmtpfs/simple-mtpfs); not yet mounted ones are mounted on opening | Not supported |

Without libvlc the app runs normally; only audio/video previews (and their media details) are unavailable.

libvlc on Linux and macOS: the app loads the system's libvlc 3 (`libvlc.so.5`/`libvlccore.so.9` on Linux, through
the resolver in `App.axaml.cs`, since only the -dev package installs the unversioned `libvlc.so`; on macOS only
`/Applications/VLC.app/Contents/MacOS/lib`). Users need the full `vlc` package (Debian/Ubuntu split the plugins out of
`libvlc5`; Fedora's own build lacks H.264/HEVC, RPM Fusion's has them). VLC from Flatpak or Snap is sandboxed and can't
be loaded. The user-facing instructions are in the public README, "Video and music previews (VLC)".

### Document previews

The details pane previews Word, Excel, PowerPoint, PDF and other documents (Settings › Folder options › "Show previews
of documents", on by default; off, no preview handler is ever started). Code: `Infrastructure/Preview/`
(`DocumentPreviewer` picks the method), `Views/PreviewHandlerHost.cs`.

- **Windows**: the same preview handlers File Explorer's preview pane uses. The handler's CLSID is looked up under
  `HKCR\.ext\ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}`, then the ProgID, `SystemFileAssociations\.ext` and the
  perceived type; it is created out of process first (prevhost.exe, so a crashing handler can't take the app down),
  initialized with a stream (a managed `IStream`), a path or a shell item, and drawn into a native child window. Every
  call into it is cancelled after 10 seconds (COM call cancellation), and the handler is unloaded and released as soon
  as the selection changes. A handler that fails shows "No preview available". This part compiles everywhere but has
  only been tested by review: it runs on Windows only.
- **Linux and macOS**: a PDF's first page is rendered by `pdftoppm` (poppler-utils) when it is on `PATH` (15-second
  limit, cached in `~/.cache/ByteExplorer/pdf-previews`, newest 64 pages); .docx and .odt files show their text (read
  from the ZIP package; formatting, pictures and tables are left out). Windows uses these too for types without a
  preview handler. Other documents show "No preview available".

## Layout

- `Core/`: no UI. Models, services (folder index, file operations, backup, analytics, settings, archives), the native
  layer (`Native/`: folder enumeration and file operations, Win32 on Windows and managed code on Unix) and `Platform/`
  (Trash, mounted volumes, mount points, launching files and terminals, "Open with", system sounds).
  `PathUtil` handles separators and case sensitivity per OS (Linux is case-sensitive).
- `Infrastructure/`: helpers shared by the views and view models: commands and a `CommandManager` replacement, the UI
  dispatcher, clipboard and drag-and-drop (with Explorer, GNOME and KDE file formats), converters, icon tables, special
  folders, image decoding (SkiaSharp, bounded size, EXIF orientation), and the global search service.
- `Core/Search/`: the search index (`SearchIndex`, namespace `ByteSearcher`): crawler, in-memory chunks, query language,
  ranking, live updates, the index file. `Infrastructure/Search/` runs it for the app (`GlobalSearchService`, change
  watchers); `ViewModels/Search/` and `Views/Search/` show the results.
- `ViewModels/`: UI-agnostic view models. They never create windows; dialogs go through `IDialogService`, which the
  main window implements.
- `Controls/`: the file list's virtualizing wrap panel (also used, in single-column mode, by Details, List, Columns and
  Nexus), the `FileListBox` list with an index-range selection model, and the shared Details column layout.
- `Views/`: Avalonia views. `Menus/FileContextMenu` holds the app's own right-click menus (the same on every OS),
  `Dialogs/` the Rename, message/confirm and Properties windows.
- `Themes/`: `Palette.axaml` (Dark and Light palettes as theme dictionaries, plus the icon templates) and `Styles.axaml`
  (shared control styles). The theme follows `Appearance.Theme` ("System", "Light", "Dark"), switchable live from the
  toolbar's "..." menu.
- Window layouts (`Appearance.WindowLayout`, read at startup): `ModernMainWindow` ("Modern", the WPF build's "Dark"
  layout: title bar, address bar and command toolbar above the sidebar, file list and details) and `NemoMainWindow`
  ("Trove", formerly "Nemo" and "Trove", after the Linux Nemo file manager: a full-height sidebar with places, pinned folders and drives with their
  capacity, one title bar with navigation, address, search and the application menu) and `FinderMainWindow` ("Folio", formerly "Finder",
  after macOS Finder: a full-height sidebar with round window buttons at its top (the native ones on macOS) and small
  gray section headers — Favorites, Locations, Tags with colored dots; a unified toolbar with back/forward, the folder's
  name as the title, a segmented Icons/List/Columns/Gallery switcher, the menu and a rounded search field; the omnibar as
  a path bar and a centered status bar at the bottom; striped list rows; Finder-like colors in both themes, as overrides
  of the palette for that window only; the status bar has the zoom slider, then the cycle-view button and the preview
  toggle). Choose it in Settings > Appearance > Window layout. Both share the application menu
  (`Views/AppMenu`: Modern left of Back, Trove at the right of its title bar) and the details pane (`Views/DetailsPaneView`). Both are only
  XAML: their behaviour (chrome per OS, shortcuts, address bar, sidebar rows and drops, panels, split view, search,
  toasts, dialogs) is one implementation, `MainWindowBase`. "Dark" is read as "Modern"; "Default" (the WPF MainWindow,
  not ported) opens the Modern window.
- `Assets/Icon/`: the application icon ("Byte Folder": a blue folder with a B and its byte, 01000010) — the SVG
  master, PNGs from 16 to 1024 px, `ByteExplorer.ico` (16, 20, 24, 32, 40, 48, 64 px as 32-bit BMP frames, 256 px as
  PNG; the exe icon and both windows' icon) and `ByteExplorer.icns` for a macOS app bundle. Sizes up to 24 px and up to
  64 px have their own simplified drawings rather than shrunken copies. Generated by `Tools/Icon/make_icon.py` (Python
  3 standard library plus the Playwright Chromium used for rendering); edit the drawing there and rerun it, never the
  files by hand.
- `Tests/ByteExplorer.Tests`: xUnit tests plus Avalonia headless UI tests. Set `BYTEEXPLORER_SCREENSHOT_DIR` to save a
  screenshot of the rendered main window.

## Logging

Logs go to `logs/byteexplorer-<date>.log` next to the executable, or under the user's local app-data folder when that
folder is read-only. Release builds log at Information level and Debug builds at Debug level;
`BYTEEXPLORER_LOG_LEVEL=Debug` overrides the level.

## Changes from the WPF version

Deliberate changes, beyond the move to Avalonia:

- **Right-click menu**: the app's own menu (Open, Open with, Open in terminal, Show in the system file manager, Cut,
  Copy, Paste into folder, Copy as path, Rename, Compress to ZIP, Extract all… / Extract here, Delete / Move to Trash, Delete
  permanently, Properties) and a folder-background menu (View, Sort by, Refresh, Paste, New folder or text document,
  Open in terminal, Select all, Show hidden items, Properties). They replace the Windows shell menu.
- **Properties** (`Views/Dialogs/PropertiesWindow*.cs`, Alt+Enter or the right-click menu): the app's own window on
  every OS, laid out like Explorer's with OK / Cancel / Apply (Apply only while something changed; Ctrl+Tab cycles the
  tabs). **General**: an editable name (renamed through the file list, so Undo works), type, default app, location with
  a copy button, size and size on disk (cluster-rounded on Windows, allocated blocks on Linux), folder contents counted
  in the background (live, cancelled when closed, never following symlinks), the three times, and the Read-only / Hidden
  attributes (Hidden is the dot name on Linux and macOS, shown only). Several items: counts, common type and folder,
  combined sizes, tri-state attributes. Drives and mount points: type, file system, used / free space with a ring chart.
  **Details**: the details pane's metadata (image size, date taken, camera, media length and tags, archive contents) in
  Explorer's groups, plus the file system facts (full path, MIME type, owner, group, links, mode). **Security**: owner and
  permissions, read-only (Unix mode bits as a table; the Windows ACL). **Hashes**: MD5 / SHA-1 / SHA-256 on request, one
  pass, cancellable, with copy buttons and a compare box.
- **Icons**: Fluent UI System Icons (a font bundled in the repo, drawn by `Icons/SymbolIcon`) instead of Segoe MDL2
  Assets, which exists only on Windows. Windows
  shell icons are not used; the `UseShellIcons` setting is ignored.
- **Bugs fixed**: case-only renames ("report.txt" → "Report.txt") work; failed renames are counted in analytics; a
  cancelled image preview no longer leaks its decoded bitmap, and replaced previews are disposed; path identity follows
  the OS's case rules (on Linux, case-insensitive cache keys could show one folder's listing for another); the inert
  "Network" sidebar row was removed.
- **Audit of the files unchanged since the first port** (42 files; all still in use; `OlderCodeAuditTests`):
  - On Windows a copy in progress no longer shows its temporary file (`.name.byteexplorer-….partial`) in the list: the
    listing treats the engine's temporaries as hidden on every OS, not only where a leading dot hides a file.
  - Usage statistics count every copy, move and delete, including Redo and Retry failed (they were counted only for
    paste, drop and delete), once each.
  - Home's frequent and recent folders and recent files follow renames and moves (and their undo/redo).
  - What is tracked while the saved statistics load at startup is added to them; it used to be dropped (only the
    session survived), losing the first folder visits of every start.
  - Folder statistics use the OS's path rules (two folders differing only in case are two entries on Linux); file
    type counts are case-insensitive everywhere.
  - Linux: folders on network file systems (NFS, SMB/CIFS, SSHFS, GVfs, …) get the network timeout Windows shares
    get, read from the kernel's mount table so the check itself never waits on a dead share. macOS network volumes
    are still not detected (no non-blocking source there).
  - Long Windows paths with forward slashes work with the `\\?\` prefix; a numeric or unknown view in the settings
    file falls back to Details.
- **Logging**: Information level by default in Release builds (it was always Debug).
- **Light theme** after Windows 11 File Explorer, next to the dark one; "System" follows the OS setting live.
- **Trove layout**: follows the Light/Dark theme (the WPF window had fixed light colors); "Computer" opens This PC and
  "Recent" the Home dashboard (both opened Home), "Home" opens the home folder (it opened the Desktop); drive capacity
  bars are proportional to the row (the WPF bar was UsedPercent pixels wide); rows take dropped files and have a
  right-click menu; the tools menu adds split view, theme, copy path and properties; the sidebar is resizable; the
  inert "Network" row is gone.
- **Item spacing** (Settings > Files and folders, per view, applied live): empty space between rows in Details (default
  4 px) and List (6 px), around each item in Icons (every size) and Tiles (16 px), 0–40 px. The gap is the list's own
  space, so a rubber band selection can start between items (before, the tiles' spacing belonged to the items, and a
  press there started a drag).
- **Look and layout customization** (Settings > Appearance and Left panel, applied live, all layouts): for each of the
  six window areas (address bar, toolbar, left panel, main view, details panel, status bar) a text size (75–160 %, a
  scale of the area so headings and icons keep their proportions; fixed-height bars grow with it), a text color and a
  background color, with "Use everywhere" for one background in every area; a background image behind the main view's
  files (strength and fit: fill, fit, stretch, tile, center); the order of the left panel's sections. Menus and tooltips
  opened from a customized area keep the theme's colors. A folder color (folder icons everywhere: the views, the tree,
  the address bar, pins, search results, the details pane and Properties). "Reset to default" returns all of it to the
  default look.
- **Greetings**: the greeting and goodbye toasts are off by default (an older settings file is switched off once).
- **Split view** (all layouts: Modern, Trove, Folio): a toolbar button in each (Trove's title bar, Folio's toolbar)
  and Ctrl+\ turn it on and off; a click anywhere in a pane, its empty background included, makes it the active pane,
  marked by an accent line on top; the address bar, status bar, preview, sidebar rows, Folio's title and view switcher
  act on the active pane, and each pane keeps its own view; it turns on from any view but Nexus (Columns included,
  each pane then has its own column browser); each tab has its own split (see Tabs). A middle click on a sidebar
  folder opens it in a new tab, or in the other pane with Settings › Tabs › "Middle-clicking a folder".
- **Startup**: the window opens on the Home dashboard (the WPF window started on an empty pane).
- **File list**: selection survives scrolling, filtering, sorting and view switches in every view; switching views
  never leaves an invisible selection that Delete could act on; the rubber band selects by position and auto-scrolls;
  dragging several selected files works; empty folders say so; much faster select all, invert and scrolling on very
  large folders.
- **Preview**: media runs off the UI thread; the preview follows the focused pane in split view; audio stops when the
  details panel is hidden.

- **Views performance**: Details, List, Icons and Tiles draw each item with one control (no per-field bindings),
  text layouts are shared through a cache, the next pages' text is prepared on idle time and the container pool is
  warmed after a folder opens. Smooth wheel scrolling on a 100,000-item folder: about 1 ms of work per frame (95th
  percentile 2-6 ms). Probes: `BYTEEXPLORER_PERF=1 dotnet test Tests/ByteExplorer.Tests -c Release --filter ViewPerformanceProbe`.
- **File list data path** (`Core/DirectoryEntrySorter`, `FileListViewModel`):
  - **Natural order everywhere**: "file2" before "file10", case ignored, on Linux and macOS too (`NaturalStringComparer`:
    the shell's StrCmpLogicalW on Windows, a managed equivalent elsewhere, which used to fall back to plain
    alphabetical order).
  - **Sorting** splits folders and files once and sorts big groups in parallel slices; name order comes free from the
    index and descending name order is a reversal. 100,000 items in Release on 4 cores: open ~710 → ~520 ms, sort by
    type ~240 → ~150 ms, name descending ~150 → ~65 ms (`BYTEEXPLORER_PERF=1 dotnet test -c Release --filter SortPerformanceProbe`).
  - **Filtering never sorts**: the sorted view is kept, each keystroke only filters it, and a longer filter narrows the
    previous result. Only typing waits 50 ms for a pause; sort clicks and setting changes apply at once.
  - **Refresh in place**: a change on disk (the folder watcher), F5 and the refresh after a file operation re-read the
    folder and keep the filter text, sort, selection and scroll position; nothing flashes "Loading...", the watcher keeps
    running and Home's frequent folders don't count a visit. A change that alters nothing visible leaves the list
    alone. These used to reload the folder as a navigation: a file created while filtering cleared the filter and the
    selection.
  - **Navigation latency**: a folder starts loading at once (every navigation used to wait 80 ms; now only rapid bursts
    are coalesced), and Up, Back and Forward work while a folder is still loading (pressing Up twice goes up twice).
  - **Less work per item**: size, date and type text is formatted when first shown, type names are shared per
    extension, and the item cache's recency list no longer allocates on every access.
- **Memory** (measured with `MemoryProbe`: a 100,000-item folder in four views, a folder of photos, sixty folders, Home;
  Release, Linux, `BYTEEXPLORER_PERF=1 dotnet test -c Release --filter MemoryProbe`):

  | after | managed heap | GC committed | resident |
  |---|---|---|---|
  | the big folder in four views | 79 → 48 MB | 145 → ~118 MB | 325 → ~298 MB |
  | the photo folder | 80 → 43 MB | 182 → ~110 MB | 399 → ~322 MB |
  | sixty folders, then Home | 87 → 51 MB | 133 → 81 MB | 354 → 296 MB |

  - The shared text layout cache keeps 4,096 layouts (it kept 12,000, about 25 MB of glyph runs and native text blobs
    for the session; the views prepare at most a couple of pages ahead).
  - Thumbnails: at most 64 MB of decoded pixels (it was 160 MB, which one photo folder could fill). Views waiting for a
    thumbnail are registered per file and held weakly: every view used to subscribe to one static event, so each
    decoded thumbnail called every view (pooled ones included), and a view that never left its visual tree (a closed
    window does not detach) kept the whole window alive.
  - A hidden view releases its pooled item containers (Details, List, Icons and Tiles each kept hundreds, with their
    templates, for the session); switching back rebuilds them once.
  - Folder listings past the cache's 5 s lifetime are dropped at once (the last eight folders opened stayed in memory).
  - One LibVLC instance for playback and media details (there were two, and the playback one was rebuilt each time a
    preview started playing; each loads VLC's plugins, tens of MB on Windows).
  - Kept on purpose: background (concurrent) GC. Without it the GC committed about 40 MB less, but the session's GC
    pauses grew from 1.7 s to 7.6 s, which the UI would feel.
  - Not the app's: at idle, ~120 MB of the resident set is shared code (the .NET runtime, Skia, fonts, the graphics
    driver), the same for every .NET/Avalonia app. A Debug build also loads Avalonia's developer tools and runs
    unoptimized code: measure a Release build.
- **Thumbnails** for images in the Icons (medium and up) and Tiles views, decoded in the background at the shown size,
  newest first, cancelled when scrolled away, cached up to 160 MB (`FileList.ShowThumbnails`).
- **In-place rename** (F2) in Details, List, Icons and Tiles; **Back/Forward restore** scroll position and selection;
  **Up selects** the folder just left; hover tips with name, type, size and date.
- **Bulk rename**: F2 / Rename with several items selected opens **Rename items**: rules applied top to bottom (find and
  replace, plain or regex; remove characters; a new name with numbering, `Holiday ({n})`, start, step and padding, or
  Explorer's quick "Name (1), Name (2)…"; prefix/suffix; modified date; letter case; extension), numbered in list
  order, with a live old → new preview that flags invalid names, duplicates and names taken in the folder (Rename stays
  disabled until none is left). The batch is one operation: one Ctrl+Z undoes it; chains, swaps (a↔b) and case-only
  renames go through temporary names; failures are listed and the rest kept; the renamed items are selected. Not inside
  ZIP files or on phones (rename one at a time there).
- **Keys**: Backspace and the mouse back/forward buttons navigate; Ctrl+Shift+1..7 switch views; Ctrl and +/- zoom;
  Finder's Cmd+[ / Cmd+] / Cmd+Up / Cmd+Down on macOS. Window shortcuts no longer steal keys from a focused text box
  (Escape in the address bar used to be swallowed).
- **Type-to-select**: typing in the file list selects the next item whose name starts with the typed text (prefix
  builds while typing, repeating one letter cycles, wraps around; any keyboard layout or input method). It reads
  names from the folder index, so it is instant on a 100,000-item folder and creates no items.
- **File operations** (`Core/Services/FileOperationService`, `ViewModels/FileOperations/`):
  - **Name clashes** ask: Replace (the existing file goes to the trash), Skip, Keep both ("name (2).ext") or Cancel,
    with "Do this for all remaining conflicts"; a folder onto a folder offers Merge, and then asks only about the files
    that clash inside. The WPF build always kept both silently. A replacement never leaves the file missing: it is
    written to a hidden temporary file first, and the old file is put back if the last step fails.
  - **Queue and progress**: copies and moves run one after another (a new paste used to cancel the running one); a
    panel over the file list shows each job taking longer than a moment with its current item, a progress bar, "1.2
    GB of 3.4 GB", speed (over the last 3 seconds), time left, pause/resume and cancel. Deletes, undo and archives run
    beside the queue, but a job waits for any earlier job working on the same items (a Shift+Del on a folder a move is
    still copying waits for it). Nothing blocks the window while they run. Closing the app with jobs running asks
    first, then waits for each to stop at a safe point.
  - **Data safety** (audited; each guarantee has a test that breaks it on purpose, `FileOperationSafetyTests`):
    - A file is never written under its real name until it is complete: copies go to a hidden temporary sibling, are
      checked (Settings > File operations > Check copies: size, or SHA-256 read back from the disk, not the cache), and
      are renamed to their name. A failure, cancellation, crash or power cut never leaves a truncated file that looks
      complete. A name taken during the copy keeps both.
    - A moved file's source is deleted only after its copy is verified and on the disk (flushed, and on Linux the folder
      synced): a power cut at any moment leaves at least one whole copy. A Replace never hides the source.
    - Links (symbolic links, junctions) are copied and moved as links, never followed: a folder holding a link to
      ~/Photos can be moved to another drive without touching ~/Photos, and a link loop doesn't nest copies.
    - An operation that can't fit is refused before anything is written; a disk filling up stops it once, with what was
      done kept and nothing half-written.
    - Crash recovery: each copy or move keeps a small journal (`operations/` in the data folder, locked while it runs);
      at the next start, leftover temporary files are removed, an interrupted Replace is finished or rolled back, and
      the person is told what was interrupted. Another window's running operation is never touched.
    - Drops from other programs (Explorer, Nautilus) tell them "copy" when ByteExplorer moves the files itself, so they
      never delete the originals before the move is done.
    - After a cut is pasted the clipboard is cleared only if the move succeeded and it still holds that cut.
    - Undoing a created item on a drive without a trash deletes it for good only if it is unchanged (or an empty folder).
    - Folder dates (and Windows Hidden/System marks) are kept on copies and moves, as file dates always were.
    - The pre-operation backup runs where it adds safety: cross-drive moves and permanent deletes.
  - **Undo** (Ctrl+Z / Cmd+Z, also in the folder background menu, the "..." menu and the application menu): copy, move,
    rename, delete to the trash, replace, new folder or text document, compress and extract. Copies go to the trash,
    moves go back, trashed items are restored from the trash (Linux: the freedesktop.org trash; macOS: ~/.Trash;
    Windows: the Recycle Bin's $R/$I entries), emptied folders are recreated. An item changed since is reported and
    the rest continue; a restored item whose place is taken comes back as "name (2)". Last 50 operations of the session.
    A permanent delete can't be undone. Escape no longer cancels file operations (use the panel's cancel button).
  - **Redo** (Ctrl+Y, or Ctrl+Shift+Z / Cmd+Shift+Z, next to Undo in the same menus): does a fully undone copy, move,
    rename, delete to the trash, new folder or text document again, newest undone first, as a new operation (name
    clashes are asked, failures listed) that Ctrl+Z undoes in turn. A new operation clears the redo list; an undo that
    couldn't reverse everything is not offered for redo (it would do half an operation twice); compressing and
    extracting aren't redone. Pressing Redo again while one still runs queues it instead of dropping it.
  - **When an item fails** (Settings > File operations > "Ask what to do", on by default): the operation stops on the
    item and asks Retry, Skip, Skip all or Cancel, as Total Commander does (a locked file can be closed and retried).
    Off, failed items are skipped and listed at the end. A finished job with failures has a **Retry failed** button
    that runs just those items again, each to where it was going.
  - **Replace if newer** in the name clash dialog (files only): replaces when the incoming file is newer, otherwise
    skips; with "for all remaining" it brings a copy of a folder up to date.
  - **Cut items are dimmed** in every view until they are pasted, the cut is replaced on the clipboard, or another
    program puts something on it.
  - **Delete confirmation names the item** ("Move "report.pdf" to the Recycle Bin?"; several items: the count and the
    first few names), and says when it can't be undone.
  - **Ctrl+drag in the items' own folder duplicates them** ("name - Copy", then "name - Copy (2)"), as in Explorer; a plain
    drag there still does nothing. Holding Ctrl and pressing on a selected item keeps it selected until the button is
    released (before, the press deselected it at once, so a Ctrl+drag had nothing to drag); a Ctrl+click without a drag
    still deselects it.
  - **Right-button drag** (`Views/Menus/DropChoice.cs`): dropped anywhere files can go (the file list, a folder in it,
    the tree, Quick access, the sidebar, a breadcrumb, a tab), a menu asks, as in Explorer: Copy here, Move here, Create
    shortcuts here, Cancel, with what a left drag would do in bold. Move isn't offered into the items' own folder;
    shortcuts aren't offered for phones or inside ZIP files. Shortcuts are "name - Shortcut.lnk" on Windows (made with
    the shell's IShellLink) and symbolic links "Link to name" on Linux and macOS; Ctrl+Z removes them, Ctrl+Y makes them
    again. Right-button drags started in ByteExplorer get the menu; a right-button drag from another program is handled
    like a left one.
- **Tabs** (`ViewModels/Tabs`, `Views/Tabs`, `MainWindowBase.Tabs.cs`, Settings › Tabs; every layout):
  - **A tab is a whole workspace**, as in the Files app: its own pane, or two while split (with which one has focus),
    each with its own folder, view mode and zoom, history (Back/Forward), selection and scroll position, and its own
    search (leaving a tab keeps its query; coming back brings it back). The toolbar, address bar, status bar, details
    pane, sidebar and window title follow the selected tab's active pane. Background tabs stay live: a file operation
    or a change on disk updates them too.
  - **Title bar** (Modern layout; Trove and Folio show these tabs on top): the tabs sit in the window's title bar, as in
    Windows 11's File Explorer, after the app's icon; the title bar grows to 40 px and its text gives way to them; the
    selected tab takes the address bar's color, so it reads as the front of the address bar below. A strip of title bar
    (72 px) always stays free before Minimize, however many tabs there are: it moves the window, a double click
    maximizes or restores it, and the window's top edge still resizes; with few tabs the whole rest of the title bar
    does. Too many tabs shrink, then scroll with the wheel. The default position (an older settings file's "Top", the
    default then, moves here once).
  - **Left** in the Modern layout is the window's left edge, before the sidebar (as Edge); in Trove and Folio, whose
    sidebar holds the window's own title area, Left stays beside the files.
  - **Where the bar sits**: Title bar, Top (a row above the files, as Explorer, Dolphin and Nautilus), or Left / Right
    (vertical tabs, as Edge), from Settings › Tabs, the bar's right-click menu, the app menu's View › Tab bar,
    or the omnibar ("Tabs on the left"). Horizontal tabs share the room equally between 96 and 232 px, then the row
    scrolls (wheel) and an "all tabs" menu appears; vertical tabs are resized by dragging their edge (150–480 px) and
    can shrink to icons. The bar can hide itself while there is only one tab.
  - **Opening**: Ctrl+T or + (Home, This PC, or the current folder: a setting); a middle click on a folder in the file
    list, the tree, Quick access, Recent, or a Home / This PC tile; "Open in new tab" (one folder: shown at once, as
    Explorer does; several: "Open in N new tabs", behind); folders dropped on + or the bar's empty part; a double click
    (or middle click) on the empty bar. A folder opened from a tab goes right after it, after the tabs already opened
    from it (Chrome's rule), behind the current tab unless "Switch to a folder opened in a new tab" is on. A new tab
    shows the view being looked at.
  - **Closing**: the × (on the selected and the pointed-at tab), a middle click, Ctrl+W or Ctrl+F4; closing the selected
    tab selects the next one (the previous one for the last). Closing several with the mouse keeps the tabs' width until
    the pointer leaves the bar, so the next × is under the pointer. "Close other tabs" and "Close tabs to the right /
    left" (below / above) leave pinned tabs open. There is always a tab: closing the only one leaves a new tab.
    Ctrl+Shift+T reopens the last 25 closed tabs, newest first, where they were, split as they were.
  - **Pinned tabs**: first, icon-only in a horizontal bar (a pin and a line below them in a vertical one), no ×, and
    they come back at every start. Dragging reorders tabs (the others slide aside); pinned and other tabs keep to their
    own side. Ctrl+Shift+PageUp / PageDown move the tab.
  - **Keys**: Ctrl+Tab / Ctrl+Shift+Tab and Ctrl+PageDown / PageUp next and previous, Ctrl+1…8 a tab, Ctrl+9 the last
    (Cmd on macOS, except Ctrl+Tab); they work in text boxes too, as in browsers; Ctrl+Shift+1…7 stay the view keys.
  - **Files dragged over a tab** open it after a moment (spring-loaded, to reach its subfolders); dropped on it, they
    are copied or moved into its folder (the usual Ctrl / Shift rules).
  - **Sessions**: with Folder options › "Open ByteExplorer to: Where I left off", every tab comes back (split, focus,
    pinned) and each sleeps until it is first selected, so twenty tabs cost one folder read at start, as Edge's sleeping
    tabs; a folder that's gone opens Home. Pinned tabs always come back, before the startup place. Files written before
    tabs (one folder and a second one) still open as they did. Saved as `Tabs.SessionTabs` in settings.json.
  - A tab shows its folder as the address bar names it (a drive by its label, a share as "photos on nas", the home
    folder by its name, so it can't be taken for Home), a spinner while a folder takes a moment to read, and the full
    paths (both panes) in its tip. Up to 100 tabs per window.
  - **Changed for tabs**: a middle click on a sidebar folder opened it in the other pane; it now opens a new tab (the
    setting brings the old behaviour back), and the menus offer both. The file list remembers scroll positions per pane
    instead of per view, so each tab comes back where it was scrolled.
  - Not done: dragging a tab out into a new window (Ctrl+N opens one; a tab can't be dragged into it).
- **New windows in one process** (`MainWindowBase.Windows.cs`, `ViewModels/AppServices.cs`): Ctrl+N, the app menu's
  "New window" and the omnibar open another main window of the same layout at the active pane's place (Explorer's
  way); a folder's right-click menu has "Open in new window", and the Windows menu's "Open in new window" verb opens one
  instead of a tab. Every window has its own tabs, panes, navigation and layout, and they share one settings file, one
  search index, one tag store, one analytics session and one operation queue (undo and the operations panel are
  app-wide). Settings changed in one window apply to all; a later start's folder opens in the window used last.
  Closing a window leaves the others' operations and the index running; only the last one asks about running
  operations, saves the session's tabs and quits the app (the settings window and dialogs never keep it open).
- **Folder sizes** (Total Commander's way; `Core/Services/FolderSizes.cs`, `ViewModels/FileListViewModel.FolderSizes.cs`):
  right-click folders › Calculate size, Alt+Shift+Enter (or the background menu, or "..." › Calculate folder sizes) for
  every folder in the open folder; Settings › Files and folders › "Calculate folder sizes automatically" counts them
  as a folder opens on this computer's drives (never network drives or phones, where counting is slow; Windows phones
  are never counted). Two folders are counted at a time in the whole app, off the UI thread, each shown as it grows
  ("1.2 GB…"); links and junctions are not followed; unreadable subfolders show as "≥ 1.2 GB". A counted size shows in
  the Size column (Details) and Tiles, in tooltips and the details pane (with file and folder counts), counts in the
  status bar's selection size ("≥" when a selected folder isn't counted), and sorting by size orders the folders by it
  (uncounted ones as the smallest). Sizes are kept for the session: file operations forget them for the folders they
  touch and every folder above; F5 counts the open folder's folders again; leaving a folder stops what still counts
  there. A change made by another program deep inside a counted folder shows after F5.
- **Folder options** (Settings › Folder options; Windows Explorer's Folder Options for every layout and view, each applied
  at once; `Core/Models/FolderOptionsSettings.cs`, read while drawing through `Infrastructure/FolderOptions.cs`):
  - General: open ByteExplorer to Home, This PC, a chosen folder, or where it was left off (the folders of both panes and
    the split view at the last close; a place that's gone falls back to Home); single-click to open (names underlined
    when pointed at; Ctrl and Shift still select; a press that becomes a drag never opens); typing in the file list
    selects the typed item or types into the search box.
  - View: show hidden and system files and thumbnails (moved here from Files and folders); hide extensions for known
    file types (the types ByteExplorer has an icon or a category for; an in-place rename edits the shown name and keeps
    the extension; search results keep full names, as their highlighting follows them); hide empty drives (removable and
    disc drives with no media; an unreachable network drive stays); Windows: show drive letters, and before the name;
    the full path in the title bar ("Home", "This PC" on those pages); check boxes to select items (a column in Details
    and List, the top-left corner in Icons and Tiles when pointed at or selected, a box in Timeline, Columns and Nexus;
    a tick toggles that item only and never opens it); pop-up descriptions, with or without sizes.
  - Navigation pane: "Show all folders" puts the home folder (named after the user) above This PC.
  - Privacy: show or hide recently used files (Home, This PC) and frequently used folders (Home, the left panel), and
    clear the history. Defaults keep ByteExplorer as it was (Explorer itself hides extensions and empty drives).
- **Phones** (Android phones in "File transfer" mode, cameras, MTP music players; `Core/Platform/Phones`): listed in
  This PC (name, maker and model, storages, free space) and in the tree with the drives, within a few seconds of being
  plugged in (listed in the background every 5 seconds, so the drive poll never waits for a phone). Browse them like
  folders (breadcrumbs, Back/Up, the address bar, the tree, every view), and copy, move, paste, drag, delete, rename and
  create folders or text documents on them. Windows uses Windows Portable Devices directly (declared in
  `Wpd/WpdInterop.cs` from the SDK headers, no package; a test checks every interface's method order); USB sticks that
  Windows also lists there are left out (they are drives already). Linux uses the folders GVfs mounts.
  - Copies to and from a phone keep every feature of the queue: progress, pause, cancel, the name clash question,
    "Ask what to do" on errors and "Retry failed". What a phone can't do is done differently
    (`FileOperationService.Phone.cs`): files into the PC still arrive as a hidden temporary file, size-checked, then
    renamed into place; onto a phone a file is created in one transfer and exists only once complete (a cancelled or
    broken upload leaves nothing); a Replace on a phone uploads the new file under a temporary name and removes the old
    one only after; a move deletes the source only after the copy is complete and its size checked; phone to phone goes
    through a temporary file on the PC.
  - Deleting on a phone is permanent (phones have no recycle bin): the confirmation says so, and the menu item reads
    "Delete from phone". Operations that change a phone aren't undoable; copies from a phone into the PC are.
  - Opening a file of a Windows phone copies it to a temporary folder first (as Explorer does); changes stay on the PC.
  - No thumbnails or previews of phone items (each would download the whole photo or video over USB); the details pane
    shows name, type, size and date. Items of a Windows phone have no file path, so Properties, "Open in terminal",
    "Open with", compressing and pinning aren't offered there, and the clipboard and drags carry them in ByteExplorer's
    own format (other programs can't receive them). Linux phones are real folders and keep those commands.
  - **Fixed: the app died silently a few seconds after start while a phone was plugged in** (Windows). The device ids
    were declared as `IntPtr[]` in `IPortableDeviceManager.GetDevices`; in a COM interface an array is marshalled as a
    SAFEARRAY by default, so the marshaller corrupted memory once there was a device to list (an access violation, which
    .NET can't catch or log). They are passed as a raw pointer to a pinned buffer now, and a test checks that no array in
    the app's COM interfaces is left to the SAFEARRAY default. Found with the fatal error capture below.
  - **One thread for every Windows Portable Devices call** (`Wpd/WpdThread.cs`): the manager, devices, their content
    objects and transfer streams are made, used and released on one long-lived thread, one call at a time (before,
    on whichever short-lived worker asked, sometimes two at once). The drive poll reads a phone's storages at most
    every 30 seconds, not every 5.
  - **Fatal error capture** (`Infrastructure/Logging/FatalErrorCapture.cs`): what the runtime prints when the process
    dies past every handler (an access violation, a stack overflow) goes to `logs/crash-yyyyMMdd.log`.
  - The log records each step with a newly connected device, once (connected, opened with its protocol, storages read,
    disconnected), so a misbehaving driver shows in its last line; the drive list is logged when it changes (naming
    each drive), not at every poll.
  - Folder options › "Show phones and cameras" (`FolderOptions.ShowPhones`, default on): off, the app never talks to
    devices and Windows Portable Devices isn't even loaded.
  - Checked on real hardware (Windows 10, an Android phone): the phone is listed, its storage opens and photos copy to
    the PC. Not yet checked on a real phone: copying to it, new folder, rename and delete there (they run against a
    simulated phone in `PhoneTests`), and Linux MTP mounts.
- **Toolbar** (Modern layout): New ▾, Cut, Copy, Paste, Rename, Delete, Sort, Filter, Split view, "..." on the left; View
  at the right end (its menu opens leftwards).
  - **New** (`Views/NewItemSelector.cs`, also the right-click menu's New): Folder, Shortcut… (asks what it should open,
    as Explorer's Create Shortcut does; a link on Linux and macOS), Text Document, then every kind of file the system's
    file manager offers (`Core/Platform/NewItemTemplates.cs`): on Windows the registry's ShellNew entries Explorer's New
    menu uses, so Word, Excel, PowerPoint, Access and Publisher documents appear when Office is installed, with Rich
    Text, Bitmap image, Compressed (zipped) Folder and whatever else programs register (names as Explorer shows them;
    kinds made by running a program, and ones whose template file is missing, are left out); on Linux and macOS the
    files of the Templates folder (XDG_TEMPLATES_DIR), as GNOME Files does. Read once in the background. A new file gets
    a free name ("New Microsoft Word Document (2).docx"), never replaces one, and can be undone and redone (with the
    same contents). Phones take folders and text documents only; nothing is made inside ZIP files.
  - Fixed: the New button's menu opened empty. Rows put into a MenuFlyout while it opens never reached its popup in
    this Avalonia version; the menu is now built whole, with its rows, before it is shown (at each click).
  - **"..."** adds, for the selection: Select none, Compress to ZIP file, Pin to Quick access (Unpin when all are
    pinned); and Settings.
  - Fixed: a file to show after going to it (a path typed in the address bar, "Open file location") was sometimes not
    selected: the request was used up by a list that arrived before the folder's items were all in. It now waits for a
    list that has the file.
- **Application menu** (the three-bar button; `Views/AppMenu`, shared by both layouts): zoom, Undo, and categories:
  **Edit** (new folder or text file, cut, copy, paste, copy path, rename, delete, delete permanently, select all,
  invert, properties, with their shortcuts), **Go** (Home, This PC, open in terminal), **View** (switches for the
  navigation pane, details pane, toolbar (Modern only) and status bar, split view, and the layout presets with the
  current one marked), **Sort by** (column and direction, set rather than toggled), **Navigation tree** (follow the
  current folder, expand to it), **Search index** (status, refresh, stop), plus Show in view, Theme, **Settings**
  (Ctrl+,) and Refresh. Back, Forward and Up are not repeated in it (they are beside it in the address bar).
  A category's items open **horizontally**, in a submenu beside the menu (the classic way: hover or click; moving to
  another row closes it after a short grace period, so the pointer can cross diagonally), or **vertically**, unfolded in
  place: `Appearance.MenuSubmenuStyle` ("Horizontal", the default, or "Vertical"), in Settings > Layout. Each category's
  rows exist once and move into the submenu while it is open. Categories close again when the menu closes.
  - Fixed: the rows that run a command inside a category (Edit's Select all, Invert selection, New folder, Cut, Copy,
    Paste…, Go's Open in terminal, View's tab bar position, Search index's Refresh and Stop) did nothing. The menu closed
    before Avalonia ran the row's command, the row lost its binding meanwhile, and no command was run; it closes after
    the command now. `AppMenuRowsTests` clicks every kind of row with the pointer, in both menu styles.
- **Settings window** (`Views/Settings/SettingsWindow`, Ctrl+, or the menu's Settings, also an omnibar command): laid
  out like Windows 11 Settings, with a "Find a setting" search over every page and pages for Appearance, Layout, Files
  and folders, File operations, Search, Network, Sounds and greetings, Privacy, Advanced, Settings file and About. Each
  setting is a card (switch, choice, number with its range and unit, folder picker, editable list, action, read-only
  value) generated from one line in `ViewModels/Settings/SettingsViewModel`. Changes save at once and apply live where
  the app can (theme, panels, menu style, hidden and system items, thumbnails, ZIP handling, index schedule, sounds,
  network timeouts, file operation safety); settings read only at startup (layout, font, text size, history limits,
  the Advanced page) are marked "Applies after a restart", and changing one shows a bar with **Restart now**, which
  closes the window the normal way and starts ByteExplorer again. Values changed from the main window (menu, status
  bar, omnibar) show in the open settings window at once.
  - **The settings file has a home there** (Settings file page): its location (copy the path, open it in the default
    editor, or show it in ByteExplorer), the data folder, **Export** a copy, **Import** one (validated and sanitized
    like the startup load, then the app restarts) and **Reset all settings** (quick access pins, bookmarks and network
    locations are kept). The file itself stays where it was (next to the executable, or the local app data folder when
    that is read-only: `Core/AppPaths`).
- **Bars**: the Modern status bar is 32 px (was 26) and its toolbar 40 px (was 44); the Trove status bar is 43 px (was 35).
  The zoom slider sits on the status bar's center line (the Fluent slider's header room and track margins are removed for it).
- **Address bar buttons**: Back, Refresh, Forward (Refresh between the two arrows), then Up; the Trove title bar the same.
- **Title bar**: the app's own icon (Assets/Icon) instead of a generic folder glyph.
- **Sort and filter failures are visible**: a sort, filter or re-sort that throws (a bug, or data the sort cannot handle)
  used to be only logged, leaving a list that silently ignored the change. Now it is logged with its context (folder,
  column, direction, filter), shown in the status bar, and announced by a notification (the same failure again within
  five seconds, e.g. while typing in the filter box, only updates the status bar). The list keeps its previous order; a
  folder whose sort fails while it opens or refreshes is shown in name order instead of not at all, and the status bar
  says so. The Columns view reports the same way.
- **Tags** (`Core/Tags`, `Views/Tags`, the sidebar's Tags section): give any file or folder one or more colored tags and a
  comment.
  - **Tags and sub-tags**: a name, one of nine colors (readable on both themes), an optional description and an optional
    parent. Typing a name with slashes creates the sub-tags automatically ("Work/Project A" makes Project A under Work);
    sub-tags can also be created from a tag's menu or the editor, and moved under another tag (never under themselves).
    A tag includes its sub-tags when its items are shown or counted.
  - **Seeing them**: a **Tags column** in the Details view draws each tag as a colored label ("+N" when they do not fit),
    and **sorts** by tags (click its header, or Sort by › Tags): items grouped by their tags in tag-name order, untagged
    items last in either direction, folders first as always; the list re-sorts when tags change;
    List, Icons and Tiles, and the search results, show up to three colored dots. Hovering an item shows its tags (full
    names, colors, descriptions) and its comment, and the details pane lists them.
  - **The quick way**: right-click items › **Tags**: check or uncheck any tag for all the selected items (a count shows
    when only some have it), New tag… (created and given to them), Remove all tags.
  - **The detailed way**: Tags › **Edit tags and comment…** opens the tag editor: every tag with a check box for the items
    (a dash: some of them have it), a find-or-create box, the selected tag's name, color, parent and description, and the
    items' comment. Tag changes apply at once; the items' tags and comment when you press Save (Cancel leaves them).
    Without items it is the tag manager (a tag's menu in the sidebar, Settings › Tags, the omnibar's "Manage tags").
  - **The sidebar's Tags section** fills itself as tags are created anywhere; each row has its color and item count, sub-
    tags fold under their parent, + creates a tag, right-click offers New sub-tag, Rename, Color, Delete and the manager,
    and files dropped on a tag get it. Clicking a tag shows its items in the results view (sorting, filters and the file
    menu work there) through a **tag search**: `tag:Work`, `tag:"Work/Project A"`, several tags (`tag:Work tag:Urgent`,
    items with all of them), plus words the names must contain (`tag:Invoices 2024`). Tag searches come straight from the
    tag store, so tagged items outside the indexed locations are found too.
  - **Robust**: tags are kept in `tags.json` in the data folder (not in the files, so they work on any drive, share or
    read-only file), saved atomically a moment after each change and on exit; a damaged file is kept aside and the app
    starts without tags. Renames and moves done in ByteExplorer, and their undo, take the tags along (a folder's tagged
    contents too); a deleted item's tags are set aside and come back if the item does (an undone delete); at each start,
    and from Settings › Tags, items gone for good lose their tags, except where the folder itself cannot be reached (a
    drive or share not connected). Paths in the user folders are saved portable, so tags follow the app to another
    computer. Not followed: renames made by other programs while ByteExplorer is closed, and copies (a copy starts
    untagged).
- **Lens** (Modern layout; left panel › **Lens**, under Quick access; `ViewModels/Lens`, `Views/Lens`): smart views over every
  indexed drive: **Today, This week, This month, Photos, Videos, Music, Documents, Archives, Large files** (1 GB and up) and
  **Downloads** (what arrived there in the last 30 days). A click shows the lens in place of the file list; opening a
  folder, starting a search or switching tabs brings the file list back.
  - **The view**: the lens's name and what it shows, how many files, and a filter that narrows it with search words or
    filters (`invoice`, `ext:pdf`, `size:>10mb`). Files are listed newest first in time groups, as people say it: this
    morning / afternoon / evening, yesterday, earlier this week, last week, earlier this month, then the months, then
    month and year, each heading with its count. Each row: the icon (Explorer's with "Show Windows icons"), the name with
    its tags as chips, the folder it is in (monospaced), size and time. Files behave as in the search results:
    double-click or Enter opens (every selected file), Ctrl+Enter shows it in its folder, the right-click menu is the
    file list's with "Open file location", Ctrl+C / Ctrl+X / Delete / F2 / Alt+Enter work, files drag out; F5 refreshes.
  - **The time river** (right side): one bar per month for the last twelve months, then one per year back to the oldest
    file, each as long as its share of the files (the busiest is the longest). The period at the top of the list is lit
    as you scroll; a click on a bar jumps there. Hover a bar for its count.
  - **How**: each lens is a search of the index (`modified:today`, `kind:image`, `size:>1gb`…), sorted by date, so it is
    instant on a million files: no disk is read. Rows are built only as they are shown (headings are two small arrays
    of group starts), and positions are calculated, not measured, so jumps land exactly. Folders of the operating system,
    installed programs and application data are left out (Windows, Program Files, ProgramData, AppData; `/usr`, `~/.cache`…),
    so "Photos" is your pictures, not every program's icons; hidden files only with "Show hidden files". The lenses are
    counted in the background (at most once a minute; hover a lens for its count). Until the search index is ready, a
    lens says so and fills in as soon as it is.
  - The section can be collapsed, or moved like the others (Settings › Left panel).
- **Right-click menu** (Settings › Folder options › "Right-click menu"; Windows; `Infrastructure/Shell/ShellContextMenu.cs`,
  `Views/Menus/FileContextMenu.Windows.cs`): three choices.
  - **Hybrid** (default): ByteExplorer's menu, ending in **Show more options**, which opens the Windows menu at the same
    place, as Windows 11 does; **Shift+right-click** opens the Windows menu directly.
  - **ByteExplorer**: the app's own menu only.
  - **Windows**: the Windows shell menu, with every program's entries (7-Zip, "Open with Code", TortoiseGit, antivirus,
    Send to, Open with, Share…), for files, folders, the empty area of a folder (Paste, New and what programs add there),
    the folder tree, search results and Lens.
  - From the Windows menu, the app does what it does better: Rename (the shell's only works inside Explorer's view), Delete
    (undoable, in the queue; Shift: permanently), Cut and Copy (cut items dim), Paste (the queue, with the name-clash
    dialog), Undo/Redo, New folder (named in place), Refresh, and opening folders (in the pane; "Open in new window" opens
    a window of the same process). Everything else is run by the shell. Where Windows can't make one menu (items of different folders, inside a
    ZIP, a phone, a password-protected item), ByteExplorer's menu is shown. On Linux and macOS it is always the app's menu.
  - **How**: the WPF build's IContextMenu code, ported: `IShellFolder.GetUIObjectOf` for items, `CreateViewObject` for a
    folder's background, `TrackPopupMenuEx` on the window, the menu's messages forwarded to IContextMenu2/3 through
    Avalonia's WndProc hook (or Send to, Open with and owner-drawn entries come up empty), the chosen entry's verb read as
    Unicode before anything is invoked.
- **Default file explorer** (Settings › Folder options › "Make ByteExplorer the default file explorer";
  `Infrastructure/DefaultApp`, `Infrastructure/Launch`, `MainWindowBase.Launch.cs`): folders open in ByteExplorer instead of
  File Explorer. For the current user only, no administrator rights, and turning it off puts back exactly what was there.
  - **Windows**: a double-click on a folder or drive (desktop, Start, a program's "Open folder") and **Win+E** open in
    ByteExplorer. Done in `HKEY_CURRENT_USER\Software\Classes`: a "ByteExplorer" verb on `Directory` and `Drive`, made
    their default, and Win+E's "File Explorer" verb (`{52205fd8-…}\shell\opennewwindow`). `Folder` is left alone, so
    Control Panel, the Recycle Bin and other virtual folders keep opening in File Explorer; a program that starts
    explorer.exe itself also still gets Explorer. What it replaces (another file manager's verb, say) is saved under
    `HKEY_CURRENT_USER\Software\ByteExplorer\DefaultFileManager` and restored when the switch is turned off; while it is
    on, `%LOCALAPPDATA%\ByteExplorer\restore-file-explorer.reg` does the same with a double-click, in case ByteExplorer is
    deleted while still the default. Moved to another folder, ByteExplorer points the registration at itself on its next
    start (only when the registered copy is gone).
  - **Linux**: a desktop entry (`~/.local/share/applications/byteexplorer-filemanager.desktop`) made the default for
    `inode/directory` with `xdg-mime`, which the desktops and other apps' "Open folder" follow; off gives folders back to
    the file manager they had before. Needs xdg-utils.
  - **macOS**: not available (Finder can't be replaced); the switch is greyed with the reason.
  - **One ByteExplorer for all of them**: a folder opened while ByteExplorer runs opens in a **new tab** of the running
    window used last, which comes to the front; it doesn't start another copy. The first copy holds a named mutex and
    listens on a named pipe that only the same user can reach; a later start with a folder (or from Win+E) hands it the folder and
    exits. If the running copy doesn't answer within 4 seconds, the start goes on as a copy of its own, so a folder
    always opens. Starting ByteExplorer by hand with no folder opens another copy, as before.
  - **Command line**: `ByteExplorer <folder>` opens that folder (instead of the startup place; after the restored tabs when
    the last session or pinned tabs come back), `ByteExplorer <file>` or `/select,<file>` opens its folder with it
    selected; `file://` URIs and relative paths are understood; shell namespaces (`::{…}`, `shell:`) are ignored.
- **Details columns** (right-click a column header; `Controls/DetailsColumns.cs`, `Views/FileListView.Columns.cs`,
  `Views/Dialogs/ChooseColumnsDialog.cs`, `Infrastructure/ItemDetails.cs`): choose what the Details view shows, as in File
  Explorer. The header's menu: **Hide "Type"** (the column right-clicked; Name always stays), **Size column to fit**, **Size
  all columns to fit**, the usual columns to check or uncheck (Name, Date modified, Type, Size, Tags, Date created, Date
  accessed, Dimensions, Length), **More…** and **Reset columns**. Double-click a column's right edge to fit it.
  - **More…** opens "Choose columns": every column with a check box, its group and whether it sorts, the shown ones first
    in their order; **Move up / Move down** (or Alt+↑/↓) reorder them, Space shows or hides, the width box sets the
    selected column's width in pixels, the line under the list says what it holds; Reset gives the defaults.
  - **Columns**: Name, Date modified, Type, Size, Tags, **Date created**, **Date accessed**, **File extension**,
    **Attributes** (R H S A D L C E O, as Explorer's letters), **Folder path**, **Owner**, **Dimensions** (pictures and
    videos), **Date taken** and **Camera model** (from a photo's EXIF), **Length**, **Contributing artists** and **Album**
    (songs and videos).
  - **Sorting**: a click on a header sorts as before; Date created and Date accessed sort too (also in the toolbar's and the
    menu's Sort by), and File extension sorts as Type. Owner, Dimensions, Date taken, Camera model, Length, Artists and
    Album **can't be sorted**, and neither can Attributes and Folder path: the first ones are read file by file, so a
    folder's worth are not all known when it opens (the chooser and the header's tip say so).
  - **How**: the dates and attributes come with the folder listing (no extra disk access). The per-file columns are read
    in the background only for the rows on screen and only what the shown columns need (a picture's header, not its
    pixels; LibVLC's parse for songs and videos; the owner from the security descriptor on Windows, the file's uid
    elsewhere): newest request first, so a fast scroll reads what is on screen now; two threads for files, one for media;
    kept for the session by path and modification time (50,000 at most). A row shows nothing for a value until it is read,
    then only that row redraws. Nothing is read inside archives or on phones.
  - One set of columns for every tab and pane (Explorer keeps one per folder type), saved in the settings
    (`FileList.DetailsColumns`, keys and widths) a moment after the last change, so dragging an edge does not write the
    file on every pixel.
- **Windows icons** (Settings › Folder options › "Show Windows icons", on by default; Windows only;
  `Infrastructure/SystemIcons`): the file views (Details, List, Icons, Tiles, Columns), the folder tree and the search
  results draw the icons File Explorer shows for **files** (each program's own icon, documents by the app that opens
  them, shortcuts, icon files) and **drives**. **Folders keep ByteExplorer's own folder icon** and the folder color
  setting either way.
  - **How**: `SHGetFileInfo` names the item's slot in the system image list (the list Explorer draws from) and that list's
    16, 32, 48 or 256 px icon becomes a bitmap. Ordinary files are asked about by name only (no disk access); only items
    with icons of their own (`.exe`, `.lnk`, `.ico`, `.url`…, drives) are asked
    about by path. Icons that have no 256 px image are scaled up from 48 px rather than drawn tiny, as Explorer does.
  - **Speed**: one dictionary lookup per drawn item. Items share icons the way the shell groups them (every `.docx` one
    bitmap per size), bitmaps are made at the next size up (16, 32, 48, 64, 96, 128, 256) and filtered down when drawn,
    48 MB at most (least recently used out). A type seen for the first time draws the glyph once while one background
    STA thread asks the shell; only the views waiting for that icon redraw.
  - Image thumbnails still take the icon's place where they are shown. Off (or on Linux and macOS), the app's glyphs are
    drawn for files too, as before.
- **Password protection** (`Core/Security`, `ViewModels/FileListViewModel.Protection.cs`, right-click › **Password**, the toolbar's "..." › **Password**, or the app menu's **Password** category in every layout):
  real encryption of files and folders, not a hidden flag.
  - **Protect with password…** (any local files or folders, one password for all of them): each item is encrypted into
    one `name.bxlock` file beside it (`Photos` → `Photos.bxlock`, `report.docx` → `report.docx.bxlock`; a free name when
    taken). The encrypted copy is then **read back and every file compared** (SHA-256) with what was read from the
    original; only then is the original deleted, **permanently** (not to the Recycle Bin, where it would stay readable).
    It runs as a job in the operations panel (progress, pause, cancel); cancelled or failed, the partial file is removed
    and the original kept. Not offered for drives, items inside archives or on phones, or folders holding links.
  - **Open** (or a double-click on the `.bxlock` file): asks for the password, then shows what is inside like a ZIP file
    (read-only: open, copy, extract). "Keep it unlocked until I lock it or close ByteExplorer" (on by default); unchecked,
    it locks by itself once no pane or tab shows anything inside it. A wrong password is said in the dialog, which stays
    open to try again.
  - **Lock now** (the item's Password menu, or the background menu inside it): forgets the key (zeroed in memory), drops
    the cached listing, deletes the files opened from it, and panes inside go back to its folder. Everything locks when
    ByteExplorer closes.
  - **Remove protection…**: decrypts it back into the original item beside it (under a free name if that one is taken)
    and deletes the `.bxlock` file. Everything is first decrypted into a hidden folder next to it, so a damaged file or a
    cancel leaves nothing half-restored.
  - **Change password…**: the current password (not asked while unlocked), then the new one twice; the item is
    re-encrypted into a new file, read back and compared, which then replaces the old one: the old password stops working
    only when the new file is in place.
  - **The format** (`Core/Security/ProtectedFormat.cs`): AES-256-GCM in 1 MiB chunks, the key from PBKDF2-HMAC-SHA256
    (600,000 rounds) with a random salt per file. Each chunk is authenticated with the header, its number and a "last
    chunk" flag, so a changed byte, swapped or missing chunks, or a file cut short are detected and refused, never
    decrypted into something else; a password check block tells a wrong password apart before any content is read.
    The content is a ZIP of the item (already-compressed kinds are stored), so a folder is browsed without decrypting
    all of it. Crafted headers with absurd costs are refused. No length or name is stored in the clear beyond the file's
    own name.
  - **What stays private**: places inside a protected item are never written to the history kept on disk (frequent
    folders, recent files, the saved tabs keep the folder holding the item instead), cannot be pinned, and files opened
    from it are extracted into `%TEMP%\ByteExplorer\unlocked` (never indexed by search), deleted on lock, at exit and,
    after a crash, at the next start.
  - **Limits, said in the dialogs**: a forgotten password can't be recovered, by anyone. The original isn't overwritten
    before it is deleted: on SSDs and copy-on-write file systems an overwrite doesn't reach the old blocks anyway, so
    traces of it can remain on the drive until the space is reused (full-disk encryption, BitLocker, covers that). A
    file opened from an unlocked item is a decrypted copy on disk while it is open. Passwords are at least 8 characters,
    with a strength hint.
- **Left panel order**: Home, Quick access and Recent at the top, the folder tree, then Tags and the saved network locations
  under it (Modern; in the Trove layout they follow the drive list).
- **Left panel designs** (Settings › Left panel › Design; Modern layout; `SidebarDesign` in the settings): five looks for
  the same left panel, chosen live:
  - **Classic**: lines between sections, the title with its chevron (as before).
  - **Quiet labels**: small letter-spaced capitals, no lines; a collapsed section is one short line with its count; the
    chevron and + show when you point at the header.
  - **Soft cards**: each section a tinted rounded group with its icon; collapsed, a slim pill with a count badge.
  - **Icon headers with guides**: chevron, icon and title like the folder tree, the rows beside a thin guide line, the
    count on the right while collapsed.
  - **Folded chips**: open sections as Quiet; collapsed sections leave the list and become chips (icon, name, count)
    above the folder tree; a chip opens its section in place.
  Only the look changes: sections open, close, remember their state, add (+) and reorder the same way in all five.
  **How**: one section template (`SidebarSection`: an icon, the title in capitals, the count, chevrons at the start or
  the end, a card and a guide line, all optional parts) and one style set per design in `Themes/Styles.axaml`, picked by
  classes on the panel (`sbModern`, `sbQuiet`, `sbCards`, `sbGuides`, `sbChips`); the chips row is
  `Views/Sidebar/SidebarChipDock.cs`. Collapsed, the five sections' headers take about 25% less height in Quiet and
  Guides than in Classic.
- **The left panel scrolls as one** (Modern, as Trove and Folio already did): Home, Quick access, Lens, Recent, the folder
  tree, Tags and Network are one column with one scroll bar, so the wheel moves through them all; the tree is one
  section like the others and can be moved anywhere in Settings › Left panel (it used to stay in the middle, with the
  sections above and below it in scrollers of their own, capped to 60% and 20% of the panel). The tree is still
  virtualized: only the rows the panel shows exist (`NavigationPaneView.ScrollsWithPanel`), with 50,000 expanded rows
  as with ten. Keys in the tree (↑ ↓, Page Up/Down by the panel's height, Home, End) and a drag resting at the panel's
  edge scroll the panel. When the tree follows the folder the file list opened, the panel scrolls to it only if that
  navigation didn't start in the panel and the tree is in sight: a click on a pin or a lens keeps those rows where
  they are ("Follow the current folder" off: the panel never moves on its own).
- **Hidden and system items**: hidden items (the hidden attribute, or a dot name on Linux and macOS) are drawn
  translucent, icon and text, and system items have their text in a conspicuous red (`SystemItemText`: a soft red on
  the dark theme, Windows red on the light one, readable on the selection too), in every file view, the folder tree and
  the search results. The search index records the system attribute for that (indexes built before show it after their
  next rebuild).
- **Search results scroll smoothly through any number of results**: each row is one control drawing cached text
  (`Views/Search/SearchResultRowView`) instead of a template of about ten controls; the folder is cut at its start by a
  bounded binary search instead of TextBlock's prefix ellipsis, which re-shaped long paths many times per row. Dragging
  the scroll bar through 94,000 results with long folders froze the window before; now a jump to new rows costs about
  30 ms.
- **Panels are remembered**: the navigation pane, details pane, toolbar and status bar are view-model state
  (`MainViewModel.Is*Visible`) saved as `FileList.StartWithLeftPanelCollapsed`, `StartWithRightPanelCollapsed`,
  `StartWithToolbarCollapsed` (new) and `StartWithStatusBarCollapsed`; the menu, the status-bar buttons and the omnibar
  commands all change the same state. A pane dragged wider keeps its width when shown again.
- **Details panel** (`Views/DetailsPaneView`, one view for both layouts): the header follows what the file list has in
  focus — one item, a multiple selection ("3 items selected", what they contain and their total size, no preview), or,
  with nothing selected, the open folder ("File folder (open)" and its contents preview). Type, size and date appear at
  once with the selection; the preview and the metadata load after the selection settles (110 ms during fast arrow-key
  runs, so scrolling through a folder does not start a decode per item), and replace the rows in one update. While the
  panel is hidden (the default) nothing loads at all and the decoded image or playing media is released; opening it
  loads the current subject. Images decode at the panel's size in device pixels (384-2560 px) instead of up to 4K.
  Besides type, size and date, the selected item's location and creation date; folder contents
  (counted in the background); image dimensions, and for JPEG the date taken and camera (EXIF); audio and video
  length, resolution, title, artist and album (LibVLC's parser); ZIP entries and unpacked size.
- **Quick access** (the sidebar's pinned places: `ViewModels/Sidebar`, `Views/Sidebar`; saved in `Navigation.Pinned`):
  - **Follows the computer it runs on** (`Infrastructure/PortablePaths`): the portable app's settings travel with it, so
    the user's well-known folders and anything inside them are saved as tokens (`::desktop`, `::documents/Projects`,
    `::home/...`) and resolved to this computer's folders. Pins saved before (absolute paths from another computer, such
    as `C:\Users\name\Downloads` or `/home/name/Pictures/x`) are mapped to the same place here when they do not exist,
    by folder name (also under OneDrive), then saved in the portable form. A path that exists is never remapped, and one
    with no counterpart stays as it was, dimmed.
  - **Pin** a folder (or a ZIP file, or a network path) with Ctrl+D (Cmd+D), "Pin to Quick access" in any folder's
    right-click menu (several folders at once too), the + on the section header, the omnibar's pin, or by dragging
    folders onto the list (an accent line shows where they land). **Unpin** with the pin button on hover, Delete, or
    the menus. Pins and the former bookmarks are one list; older bookmarks are moved into it once.
  - **Arrange**: drag rows to reorder, or Alt+↑/↓; "Rename…" shows a pin under another name without renaming the
    folder; "Color" gives its icon one of nine colors. The trash is a pin like the others (move or unpin it).
  - **Use**: click opens, middle-click (or "Open in the other pane", Ctrl+Enter) opens it in the other pane, turning
    split view on; files dropped on the middle of a pin go into that folder (copy or move), on the trash to the trash.
    The pin of the folder shown is highlighted; a pin whose folder is gone or on a disconnected drive is dimmed and
    offers to unpin itself. Full keyboard use: arrows, Home/End, Enter, Delete, F2, the menu key.
  - **Sections** (Quick access, Network, Recent) fold away from their headers and stay as left. First run: Desktop, Downloads,
    Documents, Pictures, Music, Videos and the trash (one that doesn't exist on the machine is shown dimmed).
- **Recycle Bin / Trash view** (`ViewModels/FileListViewModel.Trash.cs`, `Views/TrashView.axaml`, `Core/Platform/Trash.Browse.cs`,
  `Core/Platform/RecycleBin.Windows.cs`): the sidebar's trash pin (every layout) and the omnibar's "Recycle Bin"/"Trash"
  place open the trash in the pane, a page like This PC (its own tab title, breadcrumb, Back/Forward entry and saved
  tab location `::trash`); it no longer opens Explorer (Windows) or the bare files/ folder (Linux, macOS). Columns: name,
  original location, date deleted, size (folders counted in the background on Linux and macOS), type; click a header
  to sort, again to reverse. Right-click an item: **Restore** (to where it was; a taken name keeps both as "name (2)";
  a deleted parent folder is recreated; one job of the operations queue, and Ctrl+Z sends the items back to the trash),
  **Delete permanently** (or Delete / Shift+Delete; asked first), Select all (Ctrl+A); the background menu and the
  header's button **Empty** it (asked first). Enter and double-click open nothing: items are restored, never edited in
  the trash. Windows reads each drive's `$Recycle.Bin\<SID>` $I/$R records (the reader the undo history already used)
  and empties through `SHEmptyRecycleBin`; Linux follows the freedesktop.org Trash spec 1.0 (the home trash and the
  `$topdir/.Trash/$uid`, `$topdir/.Trash-$uid` trashes of other local disks); macOS lists `~/.Trash` without original
  places (Finder keeps them private), so Restore is disabled there. Not covered: network drives' Recycle Bins on
  Windows, other volumes' `.Trashes` on macOS, and dropping files on the view (drop them on the sidebar's trash).
- **Network locations**: the sidebar's Network section (+ to add) keeps shared folders, saved in
  `Navigation.NetworkLocations`. Windows: \\server\share, opened directly. Linux: smb://, sftp://, ftp://, dav://…
  mounted with GIO (gvfs) and browsed through its folder; a share that needs a password must be connected once from
  the desktop's file manager, since the app can't answer GIO's prompt. macOS: the address goes to Finder, which mounts
  it under /Volumes. The WPF Nemo layout's inert "Network" row is now this section.
- **Inside ZIP files and other archives**: opening an archive browses it like a folder (setting "Open archives as
  folders", key `FileList.OpenZipFilesAsFolders`, default on), with breadcrumbs, Up, Back and the address bar
  ("…/photos.zip/2024"). Read-only: files open from a temporary extracted copy, Copy extracts first, "Extract to …"
  (right-click an item), "Extract all…" (into a chosen folder) and "Extract here" (a folder named after the archive)
  go through the operations queue (clash questions, progress, undo); Rename, Delete and Paste show why they're off.
  Entries named to escape the folder ("../x", absolute paths) are never listed or extracted; links and devices are
  skipped. Temporary extractions older than a day are removed at startup. Formats (`Core/Services/Archives`, one
  provider each): ZIP, .tar, .tar.gz/.tgz and single .gz files built in everywhere (System.Formats.Tar, GZipStream);
  .tar.xz/.txz, .tar.bz2/.tbz2, .tar.zst and single .xz/.bz2/.zst through an installed decompressor (xz, bzip2, zstd,
  bsdcat or 7-Zip) feeding the built-in TAR reader; 7z, RAR, ISO and CAB (and compressed TAR without a decompressor)
  through bsdtar (Windows 10 1803+'s `tar.exe`, macOS's `/usr/bin/bsdtar`, Linux "libarchive-tools") or 7-Zip (`7z`,
  `7zz`, `7za`, `C:\Program Files\7-Zip\7z.exe`), started with an argument list, a timeout and closed input. With no
  tool, such files open in their application and the status bar (and a disabled "Extract here" tip) says what to
  install. Encrypted 7z/RAR archives aren't opened: "This archive is password protected; extract it with 7-Zip."
  `Core/Services/ArchiveBrowser`.
- **Omnibar** (the address bar, rewritten: `Core/Omnibar`, `ViewModels/Omnibar`, `Views/Omnibar`):
  - **At rest**: breadcrumbs as people read locations ("Home", volume labels, "photos on nas", ZIP files marked);
    long paths fold from the left into "…" (its menu lists the hidden segments) so the current folder always shows;
    each separator opens that folder's subfolders (type to filter); right-click a segment for the folder's menu; drop
    files on a segment to copy or move them there, or hold the drag to open it (spring-loaded); a star to bookmark the
    folder and a button to copy its path; a thin animated line while a folder takes more than a moment to load.
  - **Typing** (click beside the breadcrumbs, Ctrl+L, Alt+D, F4, or type "/" or "~" in the file list — "\\" on
    Windows): suggestions as you type, fuzzy matched with the matching letters highlighted — completions of the path
    being typed, recent and frequent folders, places, drives, bookmarks, network locations, the current folder's items,
    commands and search. The top completion appears faded after the caret: Tab or → takes it. "~", environment
    variables, relative paths, quotes and file:// addresses are understood.
  - **Prefixes**: "&gt;" for commands (new folder, views, sorting, themes, hidden items, split view, terminal, undo,
    panels…), "?" to search everywhere or filter this folder. http(s) addresses open in the browser; smb:// and similar
    connect and open.
  - **Enter**: the highlighted suggestion (picked with ↑↓), else the text: a folder or ZIP opens, a file opens its
    folder with the file selected, words go to the best suggestion. Text that leads nowhere is refused with a shake and
    a reason. Esc clears the highlight, then closes; Ctrl+Backspace deletes a path segment.
  - **Performance**: each keystroke cancels the previous query (30 ms debounce); folder listings come from the shared
    index cache with its network timeouts and ZIP support; matching 100,000 names takes milliseconds; the list is
    virtualized; existence checks of typed paths run off the UI thread with a timeout.
- **Navigation tree**: a virtualized flat list of the visible rows (NavigationTreeRows) instead of a TreeView, which
  virtualizes only its top level; expanding a folder with 50,000 subfolders creates only the rows on screen, and a
  load replaces a node's children in one notification instead of one per folder.
  - **Follows the pane**: whatever opened a folder (the file list, the omnibar, Quick access, history), the tree
    expands down to it and selects its row without opening it again (`Navigation.ExpandTreeToCurrentFolder`, default
    on; off, it only selects a row already visible). This PC is selected on This PC; Home selects nothing.
    `Navigation.TreeFollowsCurrentFolder` (default on; in the application menu's Navigation tree category) turns the
    following off: navigating from the main views then leaves the tree's selection and expansion where they are, and
    turning it back on brings the tree to the folder shown now.
  - **Reloads keep what is open**: a change on disk (live watcher, F5, a shell action) adds and removes only the
    folders that changed; folders still there keep their expanded state, loaded children, watcher and the selection.
    Before, creating one folder in an expanded folder collapsed everything open below it.
  - **Mouse**: click opens; middle click (or "Open in other pane", Ctrl+Enter) opens in the other pane; a drag resting
    on a closed folder for 0.7 s opens it (spring-loaded), and dragging near the top or bottom edge scrolls.
  - **Keyboard**: a selection made with the arrows or by typing opens after a 350 ms rest, so arrowing past ten folders
    opens one (Enter opens at once, Esc stays); typing jumps to the next visible folder starting with the letters;
    ←/→ collapse/expand or move to the parent/first child, numpad +/- expand and collapse; F5 reloads the folder.
    Delete, Shift+Delete, F2 (rename in a dialog), Ctrl+C/X/V, Ctrl+Shift+C and Alt+Enter act on the tree's folder:
    the window's shortcuts used to run first and act on the file list's selection, so Delete with a tree folder
    focused deleted the selected files. Quick access gets its keys the same way (`Views/IFileKeyScope`).
  - **Menu**: Expand/Collapse, Open in other pane, Refresh (Refresh drives on This PC), Collapse all folders, then the
    folder's file menu (now including Rename).
- **Global search** (the box at the right of the address bar; Ctrl+F or F3):
  - **As you type**: every match, ranked: an exact name first, then the name without its extension, names starting with
    the word, the word starting a word of the name; shorter names, shallower places, folders and recent changes rank
    higher, hidden items lower. Matching letters are highlighted. The previous search returned the first 500 matches its
    parallel scan happened to reach, then sorted those, so the best match could be missing entirely.
  - **Syntax** (the ⓘ button shows it): words in any order, `"exact phrase"`, `-leaveout`, `*.pdf`, `report-??.docx`,
    `photos/beach` (beach inside a folder named photos), `ext:pdf;docx`, `kind:image|video|audio|document|archive|code|folder`,
    `size:>10mb`, `size:1mb..5mb`, `size:large`, `modified:today|week|month|year|2024|>2024-05-01`, `in:C:\Projects`,
    `in:backup`, `content:invoice` (the text inside files, see "Search inside files"). A filter that cannot be read is
    searched as text.
  - **Results view** (`Views/Search`): virtualized over every match (a million scrolls like ten), columns Name, Folder, Size,
    Date modified, Type, sortable (again to reverse; re-orders without searching again); quick filters (Folders, Documents,
    Pictures, Videos, Music, Archives, Code); "Everywhere" or the active pane's folder; type icons; status with the
    count and time. Enter or double click opens (a folder in the pane), Ctrl+Enter opens the file location with the item
    selected; right-click gives the file menu (open with, copy, cut, delete, rename, compress, pin, properties) plus "Open
    file location" and "Copy full path"; results drag out to folders and other apps; Ctrl+C/X, Delete, F2, Alt+Enter act on
    the selected results; Esc goes back to the box, Down from the box into the results.
  - **Always available**: search works whenever an index is loaded, also while it is rebuilt (on schedule, every 6 hours
    by default, or from the search icon's menu), after a stopped or failed rebuild, and with a partial index. The previous
    service deleted the index before every rebuild and turned search off until the crawl finished.
  - **Current between builds**: the app's own operations, the folders being viewed and (on Windows and macOS, and on Linux
    when the folders fit the inotify limit) file system watchers on the indexed drives report changed folders; each is
    listed again and patched into the index within half a second (new items, removed items with their subtrees, new sizes
    and dates, new folders crawled), and a shown search refreshes. Saved 15 seconds after the last change and at exit.
  - **Settings** (`Index`): `Locations` (empty = every drive; on Linux and macOS the home folder and volumes),
    `ExcludedFolders`, `ExcludedNames` (e.g. node_modules), `WatchForChanges`, `EnableAutoRebuild`,
    `AutoRebuildIntervalHours`.
- **Search index** (`Core/Search`):
  - **Crawl**: a pool of workers shares one stack of folders (FindFirstFileEx with large fetch on Windows, readdir
    elsewhere), each appending whole folders under one short lock; more workers for network roots, capped. Symbolic
    links, junctions and mount points are indexed but not followed; cloud placeholders are crawled without downloading.
  - **Memory**: chunks of 65,536 entries, column by column (parent, flags, kind, depth, size, date, name twice: lower case
    for searching, as typed for display, the second omitted when equal). About 60 bytes per entry with the names; paths are
    rebuilt from parent links when shown. The previous index kept every entry's full path.
  - **Search**: vectorized substring scan of the lower-case names, all chunks in parallel, then the other conditions and
    the score per candidate; ordering by 64-bit keys consistent with the natural name order, ties sorted in parallel.
    3,000,000 entries (Release, 4 cores): selective searches 3-20 ms, "report" (196,000 matches) ~100 ms, a single letter
    (1.8 million matches) ~450 ms. Probe: `BYTEEXPLORER_PERF=1 dotnet test -c Release --filter SearchPerformanceProbe`.
  - **File**: one `index.bxi`, written to a temporary file, flushed and renamed over the old one, so a crash or a
    cancelled build never leaves a broken index (the previous format overwrote four files in place while crawling);
    every offset and parent link is checked when loading, so a damaged file is refused and rebuilt. Files of the old
    format are removed after the first build. The previous build could also hang for good if writing failed (the
    crawler waited forever on a full queue); the in-memory build has no such queue.
- **Search inside files** (`Core/Search/Content`):
  - **Query**: `content:invoice` (or `contains:`) finds files whose text holds the word; words of three letters or more
    also match longer words starting with them (`content:invoic`), case and accents are ignored (`cafe` finds "Café").
    `content:"exact phrase"` finds the words, then confirms the order by reading the file (the first 400 candidates;
    beyond that the word match is trusted). It combines with every other filter (`content:budget ext:xlsx modified:year`).
    With **Search file contents** on, the words typed in the box also find files by their text: name matches first,
    then content matches, each with a "Contains: …" line showing the text around the first match (read in the
    background for the rows on screen only, cached).
  - **Formats** (class library only, no package): text and source code (~120 extensions; BOM, BOM-less UTF-16 and
    UTF-8/Latin-1 detection; binary files skipped; HTML without tags), Word/Excel/PowerPoint (.docx/.xlsx/.pptx and macro
    and template variants: text runs, headers, footers, notes, comments; shared strings, inline strings and numbers;
    slides in order and their notes), OpenDocument (.odt/.ods/.odp), RTF (control words, tables, pictures, field codes
    and info groups dropped; `\uN`, `\'hh` in the document's code page), PDF best effort (objects found by scanning, also
    inside object streams; uncompressed and FlateDecode streams; `Tj`, `TJ`, `'`, `"`, literal and hex strings;
    ToUnicode maps). **Limits**: PDFs with composite fonts lacking a ToUnicode map, custom simple-font encodings,
    encryption, LZW or other filters, and scanned pages (no OCR) yield little or nothing; Chinese and Japanese are
    matched by characters and character pairs; at most 8 million chars and 200,000 distinct words are read per file.
  - **What is read**: the files the name index lists (no crawl of its own), not hidden, not empty, of those types, at
    most **Largest file to read** (20 MB by default), outside system and program folders, the excluded folders and
    dependency caches (node_modules, `__pycache__`, site-packages, virtual environments).
  - **Index**: an inverted index in the index folder's `Content` subfolder: immutable segments (sorted terms with
    delta-encoded document ids; only every 64th term is held in memory, lookups read one run of terms from disk) plus a
    document table (`docs.bxc`: path, size and time of each file; the commit record). New documents gather in a bounded
    memory buffer and become a segment at each commit; more than eight segments are merged, dropping removed and
    replaced files. Writes go to temporary files that are flushed and renamed, so a crash loses at most the files read
    since the last commit; a damaged index is dropped and rebuilt. One process writes (a lock file); another
    ByteExplorer searches the same index read-only.
  - **Background and polite**: one worker thread at the lowest priority (background I/O priority on Windows) syncs 30
    seconds after start and a few seconds after the name index changes (a build, watchers, the app's own operations):
    only new and changed files (size or time) are read, newest first, and files gone from the name index are removed.
    It rests as long as each file took (half a core at most), waits while a search runs and while the name index is
    rebuilt, and is cancellable at every step. Normal searches cost nothing extra; a content search adds the term
    lookups (milliseconds) and one pass over the matching paths. 3,000 text files of 18 KB index in about 3 s of work.
  - **Settings** (Search › File contents): **Index file contents** (default on; off deletes the content index),
    **Search file contents** (default on), **Largest file to read** (MB), and the content index's status (files indexed,
    files left). In the settings file: `Index.IndexFileContents`, `SearchFileContents`, `ContentMaxFileSizeMB`.
- **Back/Forward history** (`ViewModels/NavigationHistory`): right-click Back or Forward for the places behind or
  ahead (16 nearest) and jump there in one step. Home and This PC are history places of their own: going back to This
  PC used to open Home, and moving between the two pages was not recorded. Renaming the folder shown (or one above
  it) follows it to its new path.
