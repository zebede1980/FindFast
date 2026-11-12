# FastFind

A fast, two-pane file manager for Windows with "Everything"-style instant search built in. Written in-house so it can
be used where third-party tools are not approved; it has no third-party dependencies.

A tab is a **location plus a query**:

* No location: search every indexed drive (the original FastFind view).
* A location and no query: a plain folder listing, read live from disk.
* Type in the box: the folder is filtered as you type, with the full query language.
* **Subfolders** on: the folder and everything beneath it as one flat, sortable, searchable list.

## What it does

### File manager

* **Two panes side by side** (Ctrl+\ for one or two), each with its own **tabs**, address bar, history and filter box.
  Tab moves between panes; drag the divider to resize, double-click it to equalise. **Open in other pane**
  (right-click, or Shift+Enter) shows a folder on the opposite side, bringing up the second pane if only one is showing.
* **Address bar** with clickable breadcrumbs; Ctrl+L to type a path (`%TEMP%` and `~` work).
  **Back / forward / up** with Alt+arrows, Backspace or the extra mouse buttons.
* **Copy and move** with the same engine, progress and conflict dialogs as Explorer: F5 / F6 to the other pane,
  Ctrl+C / X / V (works with Explorer and other programs), drag and drop between panes, onto folders, onto the
  sidebar, and to or from other programs. Dropping moves within a drive and copies between drives; Ctrl copies, Shift moves.
* **Delete** to the Recycle Bin (Del) or permanently (Shift+Del); **rename** (F2) and **bulk rename** (Ctrl+F2);
  **new folder** (Ctrl+Shift+N) and **new file** (Ctrl+N); **zip and unzip**; Properties (Alt+Enter).
* **The real Windows right-click menu** (Send to, 7-Zip, Git, Open with, New...) is one click away at the bottom of the
  built-in menu, or directly with Shift+right-click.
* **Folders stay up to date on their own**: each folder on show is watched, so files created or deleted by other
  programs appear and disappear without pressing refresh.
* **Sidebar**: search everywhere, your usual places, **pinned folders** (click "Pin current folder", drag a folder
  onto it, or right-click any folder), and drives with free space. Drop files on a place to copy or move them there.
* **Preview pane** (Alt+P): thumbnail (pictures, PDFs, Office documents where Windows has a handler), details, and
  the first lines of text files.
* **Session restore**: the tabs you had open come back at the next launch.
* `FastFind.exe C:\some\folder` opens that folder in the running window. Settings can add **Open in FastFind** to the
  right-click menu of folders and drives in Windows.

### Things the index makes possible

* **Instant search** over every file and folder on your drives: a parallel scan over an in-memory name table (the
  approach Everything uses), a few milliseconds across a million entries.
* **Folder sizes**: the Size column shows each folder's total size, and sorting by it shows where the space went.
  `foldersize:>1gb` finds large folders anywhere.
* **Disk usage map** (Ctrl+U, or right-click a folder or a drive): where the space went, in the manner of WinDirStat.
  The top half is the folder tree sorted by size, with each item's share of its folder; beside it, the file types
  taking the most room. The bottom half is a treemap: every file is a block whose area is its size, nested inside its
  folder's block and shaded so the nesting reads as depth.
  * Click a block to find that file in the tree; click it again to step out to its folder, and again to the folder
    above. Select something in the tree and the map spotlights it.
  * Double-click, or scroll the wheel, to zoom the map into a folder; Backspace, the wheel or the breadcrumb zooms
    back out. The file type list and the legend follow the folder on show.
  * Colour is the kind of file (system, archives, applications, data, images, code, documents, media), the same in
    every map; each extension has its own shade of its kind's colour. Click a kind in the legend, or a type in the
    list, to pick those files out and dim the rest.
  * Right-click for Show in FastFind, zoom, copy path, delete and properties. Deleting updates the map on the spot.
  * A whole drive takes a few seconds, because the sizes come from the index; a folder the index does not cover is
    read from disk instead. Online-only OneDrive files are listed but counted as taking no space, since they do not.
  * **Space by kind**: a bar above the map splits the folder on show into system files, archives, applications,
    data, images, code, documents and media, with the size of each beside it.
  * **Applications** (tab on the right): what each application takes, gathered from everywhere Windows scatters it
    (Program Files, your application data, ProgramData, Store packages, dot-folders in your profile) and split into
    program, data, caches and logs. Click one to light up all its folders on the map; **Clear** empties its caches.
  * **Clean up** (tab on the right): what could go, in four groups.
    * *Safe to clear*: application caches, temporary files more than two days old, crash dumps. One click and a
      confirmation; programs make these again as they need them.
    * *Can be rebuilt*: the NuGet, Gradle, Maven and Cargo stores, and `node_modules`, `bin` and `obj` folders, the
      last three only inside a git working copy (the same folder names inside a released package or an installed
      program are part of that product and are never offered).
    * *Windows looks after these*: update downloads, a previous Windows installation, system dumps, the hibernation
      file. FastFind does not delete these; it opens Disk Clean-up or gives you the command.
    * *Yours to review*: the Recycle Bin, installers and old items in Downloads, large files untouched for two
      years. These are only pointed out.
  * What a clean-up may touch is checked twice, by the analysis and again at the moment of deleting: never Windows,
    Program Files or ProgramData, never your profile, Documents, Downloads or anything above them, never through a
    link, and only files that were measured and shown (anything that arrived since is left, as is anything in use).
    Clean-ups delete for good rather than to the Recycle Bin, and each one is written to `fastfind.log`.
* **Jump to any folder** (Ctrl+P): type a few letters of a folder's name from anywhere on any indexed drive.
* **Flat view of a subtree** in milliseconds, with every filter and sort available.
* **Everything-compatible query language**: `space` = AND, `|` = OR, `!` = NOT, quotes, `*`/`?` wildcards, and
  `ext:` `size:` `foldersize:` `dm:` `dc:` `path:` `infolder:` `parent:` `folder:` `file:` `type:` `regex:` `case:`
  `wfn:` `attrib:` `empty:`. Press **F1** for the full reference and every keyboard shortcut.
* **Filter panel**: match options, type, kind (documents, images, audio, video, archives, executables, code),
  extensions, size range, modified date, hide hidden/system.
* **Match highlighting**, a **column chooser** (right-click a column header) with a remembered layout, and **export**
  of any list or selection to CSV, text or Everything's `.efu` format (Ctrl+S).
* **Network shares on a schedule**: add UNC paths in Settings; they are scanned, cached and rescanned every N hours.
* **Tray icon and global hotkey**: closing the window keeps FastFind in the notification area; **Ctrl+Alt+F**
  (configurable) brings up search-everywhere from any program. Ctrl+Q exits.

## How the index is hosted

The window always runs as a normal user. That matters for a file manager: files you open do not inherit administrator
rights, drag and drop with other programs works, and your mapped drives are visible.

Reading the NTFS Master File Table needs administrator rights, so that job belongs to a separate small process:

```
FastFind.exe  (normal user: UI, live folder listings, file operations)
     |  named pipe, per user:  \\.\pipe\FastFind.Index.v1.<your SID>
FastFind.Service.exe  (elevated: reads the MFT, follows the USN change journal, answers searches)
```

* **Settings > Install index service** registers `FastFind.Service.exe` as a scheduled task that starts at sign-in
  with the highest available privileges (one administrator prompt, once). With it, NTFS volumes index in seconds and
  stay live from the change journal.
* **Without the service** FastFind builds the index inside its own process by walking folders (minutes on a large
  drive the first time; cached under `%LOCALAPPDATA%\FastFind\cache` so later launches are searchable straight away)
  and keeps it fresh with `FileSystemWatcher`. Everything still works; folder browsing never depends on the index.
* If the service stops or is removed while the window is open, the window notices and switches to its own index.
* Only names, sizes, dates and attributes cross the pipe, never file contents. The pipe accepts connections from the
  user the service was installed for (plus administrators), and only an administrator can create it, so another
  process cannot pose as the index.
* When the service is installed by the same account that uses it, it runs as that user and can reach their network
  shares. When a separate administrator account installs it, it runs as SYSTEM and indexes local volumes only.

### When .NET is updated underneath it

A Windows or Visual Studio update can install a new .NET runtime and delete the one FastFind is running on. What a
program has already loaded keeps working, but anything it has not used yet (a right-click menu, say) then fails until
the program is restarted. Both processes check for this every 30 seconds:

* The **window** asks whether to restart now. Tabs and layout are kept. If it is hidden in the tray, it asks when it
  is next shown. Say No while a copy or move is running and restart afterwards.
* The **index service** restarts itself straight away, keeping its administrator rights; the window reconnects within
  a second or so.

### Using the index from scripts and other tools

The same pipe is available to anything else. The quickest way is the CLI:

```powershell
# one path per line on standard output; status on standard error
FastFind.Cli.exe --service --max 50 "ext:cs size:>10kb dm:last30days"
```

```powershell
# the biggest things in a folder and the file types using the most space, from the index (add --walk to read the disk)
FastFind.Cli.exe --usage C:\Users --max 15
# the same, plus what each application takes and what could be removed (it only reports; it never deletes)
FastFind.Cli.exe --usage C:\ --analyse
```

From .NET, reference `FastFind.Core` and use `PipeIndexClient.TryConnect(PipeProtocol.PipeName(PipeProtocol.CurrentUserSid()))`,
then `SearchAsync(new SearchRequest { Text = "..." })` and page rows with `GetRows`. The wire format is described at
the top of `PipeProtocol.cs`.

## Build and run

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
.\src\FastFind.App\bin\Release\net10.0-windows\FastFind.exe
```

`FastFind.Service.exe` is built alongside and copied next to `FastFind.exe`; keep them together.

Diagnostics: `%LOCALAPPDATA%\FastFind\fastfind.log` (window) and `service.log` (index service).
Settings: `%LOCALAPPDATA%\FastFind\settings.json`.

### Tests

```powershell
dotnet test
```

* `FastFind.Core.Tests`: query parser and search engine, size/date parsing, live index mutation and the incremental
  name order, folder sizes, live folder listing, path handling, the MFT record parser (synthetic records), the pipe
  protocol end to end (a real named pipe with a fake index behind it), the scheduled-task definition, and the disk
  usage tree (totals, ordering, removal, file kinds, reading from the index, through the pipe and from disk), and the
  usage analysis on a made-up machine (applications gathered across folders, what is and is not offered for clean-up).
* `FastFind.App.Tests`: tab behaviour on a real temporary folder (listing, filtering, history, subfolders, live
  refresh, folder sizes), zip round trip, export, name validation, the disk usage map's layout and shading (every
  pixel accounted for, areas in proportion, blocks nested in their folders), the rows of its tree, thumbnail
  orientation, and clean-ups run for real against a made-up machine in a temporary folder (files in use, new files,
  files that arrived after measuring, links and protected folders are all left alone).

## Project layout

| Path | Purpose |
| --- | --- |
| `src/FastFind.Core/Native`, `Mft`, `Usn` | Raw volume access, MFT reader, USN change-journal watcher |
| `src/FastFind.Core/Indexing` | In-memory volume index, directory-walk indexer, watcher, cache, volume manager |
| `src/FastFind.Core/Search` | Query model, parser, size/date parsing, parallel search engine |
| `src/FastFind.Core/Browse` | Live folder listing and path helpers |
| `src/FastFind.Core/Usage` | Disk usage: the size tree, its loaders (index or disk) and the file kinds |
| `src/FastFind.Core/Api` | `IIndexService`, the in-process implementation, the named-pipe server and client, the service installer |
| `src/FastFind.Service` | The elevated index host |
| `src/FastFind.App` | WPF UI (Fluent theme): panes, tabs, sidebar, preview, file operations, shell integration |
| `src/FastFind.App/Treemap` | The disk usage map: squarified layout, cushion shading, colours, and the control that shows it |
| `src/FastFind.Cli` | Console harness for timing the engine and querying the service |
| `tests/` | xUnit tests |

## Notes and limits

* FastFind browses real paths: drive letters and UNC shares. Virtual shell locations (This PC, Recycle Bin, phones,
  inside zip files) are handed to Explorer; the sidebar has links for the first two.
* There is no undo (Ctrl+Z) for file operations; deleted items can be restored from the Recycle Bin.
* Hard links are shown once (the first Win32 name in the MFT record).
* Mapped drive letters are not visible to an elevated process, so index network locations by their UNC path.
* In directory-walk mode, changes made while nothing is running are reconciled by the background rescan at start-up.
