# FastFind

An "Everything"-style instant file search for Windows, built in-house so it can be used where third-party tools are not approved.

* **Instant search** over every file and folder on your drives. Searching is a parallel linear scan over an in-memory
  name table (the same approach Everything uses), so a query across a million entries answers in a few milliseconds.
* **Two indexing engines**
  * **NTFS Master File Table reader** (when running as administrator): reads the raw MFT in one sequential pass and
    gets names, parent links, sizes and timestamps for the whole volume in seconds. Kept live with the USN change journal.
  * **Directory walk** (no admin rights needed): a parallel scan using the low-allocation `FileSystemEnumerator`.
    Kept live with `FileSystemWatcher`. The result is cached under `%LOCALAPPDATA%\FastFind\cache`, so later
    launches are searchable in well under a second while a fresh scan runs in the background.
* **Volumes that appear later** (BitLocker drives unlocked after start-up, USB sticks, mounted VHDs) are picked up
  automatically via `WM_DEVICECHANGE` plus a five-second safety poll, and indexed without a restart.
* **Everything-compatible query language**: `space` = AND, `|` = OR, `!` = NOT, quotes, `*`/`?` wildcards, and
  `ext:` `size:` `dm:` `dc:` `path:` `infolder:` `parent:` `folder:` `file:` `type:` `regex:` `case:` `wfn:` `attrib:` `empty:`.
  Press **F1** in the app for the full reference.
* **Filter panel**: type (files/folders), kind (documents, images, audio, video, archives, executables, code),
  extensions, size range, modified date presets or custom range, folder scope, hide hidden/system.
* **Tabs** (Ctrl+T / Ctrl+W / Ctrl+Tab, middle-click to close), **drag and drop** (drag results out to Explorer or any
  app; drop a folder onto the window to search inside it), Explorer-style context menu, Fluent (Windows 11) theme with
  light/dark/system modes. Double-click a name to open it, double-click a path to open that folder.
* **Tray icon and global hotkey**: closing the window keeps FastFind in the notification area so the index stays in
  memory; **Ctrl+Alt+F** (configurable) shows or hides it from any app. Ctrl+Q exits.
* **Export**: Ctrl+S saves all results (Ctrl+Shift+S the selection) as CSV, one path per line, or Everything's `.efu`
  file-list format.
* **Match highlighting**: the parts of each name (and path, for `path:` terms) that matched are shown in bold accent colour.
* **Column chooser and persisted layout**: right-click a column header to choose columns (Name, Path, Size, Date modified,
  Date created, Type, Attributes, Full path). Widths, order, visibility and the sort column are remembered.
* **Network shares on a schedule**: add UNC paths or mapped drives in Settings. They are scanned, cached, and rescanned
  every N hours (default 6) rather than watched live. "Rescan now" forces a scan.
* **Single instance** (toggle in Settings): launching FastFind again brings the running window forward.
* **Rename and bulk actions**: F2 renames one item; Ctrl+F2 (or "Rename multiple…") opens a preview-driven bulk renamer
  with find/replace (plain or regex), prefix/suffix, numbering, case changes and extension changes. Move to folder,
  Copy to folder and Delete (to the Recycle Bin, with confirmation) use the normal Windows progress dialogs.

## Build and run

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
.\src\FastFind.App\bin\Release\net10.0-windows\FastFind.exe
```

The app manifest asks for `highestAvailable`, so members of the local Administrators group get a UAC prompt and the
fast NTFS engine; everyone else runs with the directory-walk engine and no prompt. The status bar shows which engine is
active, and the **Run as administrator** link relaunches elevated (you can enter separate admin credentials there).

Diagnostics: `%LOCALAPPDATA%\FastFind\fastfind.log`. Settings: `%LOCALAPPDATA%\FastFind\settings.json`.

### Command-line tester

```powershell
# index one folder with the directory walker and run a query
dotnet run --project src/FastFind.Cli -c Release -- --root C:\Users\me\source "ext:cs size:>10kb dm:last30days"

# in an elevated prompt: index all fixed drives from the MFT, then an interactive query loop
dotnet run --project src/FastFind.Cli -c Release
```

### Tests

```powershell
dotnet test
```

The tests cover the query parser and search engine, size/date expression parsing, live index mutation, and the MFT
record parser (fixups, attributes, extension records, data-run decoding) using synthetic records.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/FastFind.Core/Native` | P/Invoke for raw volume access and FSCTL calls |
| `src/FastFind.Core/Mft` | MFT reader and FILE record parser |
| `src/FastFind.Core/Usn` | USN change-journal watcher |
| `src/FastFind.Core/Indexing` | In-memory volume index, directory-walk indexer, FileSystemWatcher monitor, index cache, volume manager |
| `src/FastFind.Core/Search` | Query model, parser, size/date parsing, parallel search engine |
| `src/FastFind.App` | WPF UI (Fluent theme), tabs, filters, drag and drop, settings |
| `src/FastFind.Cli` | Console harness for timing and testing the engine |
| `tests/FastFind.Core.Tests` | xUnit tests |

## Notes and limits

* Hard links are shown once (the first Win32 name in the MFT record).
* Network drives can be indexed (Settings) but only with the directory walker, and large shares are slow to scan.
* In directory-walk mode, changes made while the app is closed are reconciled by the background rescan at start-up.
* Delete always goes to the Recycle Bin and always asks first.
