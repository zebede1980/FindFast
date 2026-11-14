# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

FastFind is a two-pane Windows file manager (WPF, .NET 10) with Everything-style instant search. `README.md` is the
user-facing reference for features, shortcuts, the index service and the project layout; read it before larger work.

## Commands

```powershell
dotnet build -c Release                     # what the user runs (see below)
dotnet test                                 # both test projects (builds Debug)
dotnet test tests/FastFind.App.Tests --filter "FullyQualifiedName~OtherPaneTests"   # one class or test
```

- The user launches FastFind from a taskbar shortcut to `src\FastFind.App\bin\Release\net10.0-windows\FastFind.exe`.
  `dotnet test` only builds Debug, so after a change they will try, run `dotnet build src/FastFind.App -c Release`.
- FastFind must be closed before building: the running process locks its output files.
- There is no linter. Warnings are not errors, and a few are already there (CS8602 in `TreemapView.cs`, WFO0003, xUnit2013).

## Hard constraints

- **No third-party dependencies** in shipped code. The tool exists because third-party tools are not approved where it is
  used. Only the test projects reference NuGet packages (xUnit).
- The window always runs as a normal user. Anything needing admin (reading the MFT) belongs in `FastFind.Service`, behind
  the named pipe. Never make the UI elevate.
- Source files use **CRLF** line endings. When scripting edits, keep them; a text-mode Python read/write converts the whole
  file to LF.

## Architecture

**Index access goes through `IIndexService`** (`Core/Api/Contracts.cs`). There are three implementations: `LocalIndexService`
(index built in this process), `PipeIndexClient` (talks to the elevated `FastFind.Service` over a per-user named pipe) and
`App/Services/IndexHost`. `IndexHost` is the one the UI holds. It picks the service if it can, otherwise falls back to an
in-process index, and switches transparently if the service comes or goes (`SourceChanged`). Folder browsing never
depends on the index; only search, subtree views and folder sizes do.

**A tab is a location plus a query.** `FileTabViewModel.Mode` is derived from those two things:
`Everywhere` (no location; searches the index), `Folder` (a live disk listing via `Core/Browse/FolderListing`, watched
with a `FileSystemWatcher`) or `Subtree` (the "Subfolders" toggle; searches the index under the location). `RunSearch`
branches on the mode. Tabs persist through `TabState` in `Settings.Session`. Add any new per-tab state there, in
`ToState()`, and in `PaneViewModel.Restore`/`ReopenClosedTab`.

**View-model shape:** `MainViewModel` → `Left`/`Right` `PaneViewModel` → `Tabs` of `FileTabViewModel`. `MainViewModel`
owns cross-pane actions (`ActivePane`, `OtherPane`, `OpenInOtherPane`, `IsDualPane`).

**One `FilePaneView` per pane, reused across tabs.** The `TabControl` swaps the `DataContext` rather than creating a view
per tab. Per-tab view state (scroll position, selection) is saved and restored in `OnDataContextChanged`. Anything you add
to the view that depends on the tab must be reset there.

**Keyboard shortcuts live in two places.** `MainWindow.OnPreviewKeyDown` handles window-level keys (tabs, panes, F1,
Ctrl+P). `FilePaneView.HandleKey` handles pane keys; note the `inList` gate for keys that would clash with typing.
When adding or changing a shortcut, also update `Views/SyntaxHelpWindow.xaml` (the F1 reference) and `README.md`.

**Settings** are a static `App.Settings` (JSON at `%LOCALAPPDATA%\FastFind\settings.json`). Logging goes through
`Services/Logger` to `fastfind.log` in the same folder.

**Shell integration** (`App/Services/Shell*.cs`) is COM interop with Windows itself: the real context menu, Explorer's
copy/move engine, thumbnails, icons and drag data objects. Prefer these over reimplementing Explorer behaviour.

## Tests

- `FastFind.App.Tests` drives view models on a real temporary folder. View models need an STA thread with a dispatcher:
  wrap test bodies in `Ui.Run(async () => ...)` and poll with `Ui.WaitFor(...)` (both in `TabTests.cs`). `StubIndex`
  stands in for the index. `MainViewModel` can be built with an `IndexHost` that is never started, because folder views
  read the disk.
- Tests that set `App.Settings.Session` must reset it in `Dispose`.

## Style

- Doc comments describe behaviour from the user's point of view, in plain English (see existing `<summary>`s), and
  explain *why* where it is not obvious. Match that voice.
- Pattern matching on view models is the idiom used for guards, e.g. `if (Vm is { IsLocked: true } && Pane is { } pane)`.
