using System.Collections.ObjectModel;
using FastFind.App.Services;
using FastFind.Core.Api;

namespace FastFind.App.ViewModels;

/// <summary>One side of the window: a strip of tabs, one of which is showing.</summary>
public sealed class PaneViewModel : ObservableObject
{
    private readonly IIndexService _index;
    private readonly Stack<TabState> _closed = new();
    private FileTabViewModel? _selectedTab;
    private bool _isActive;
    private bool _showWindowCommands;

    public MainViewModel Main { get; }
    public string Name { get; }
    public ObservableCollection<FileTabViewModel> Tabs { get; } = new();

    public RelayCommand NewTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }

    public PaneViewModel(MainViewModel main, IIndexService index, string name)
    {
        Main = main;
        _index = index;
        Name = name;
        NewTabCommand = new RelayCommand(() => AddTab(SelectedTab?.Location ?? ""));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as FileTabViewModel ?? SelectedTab));
    }

    public FileTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;
            if (!Set(ref _selectedTab, value)) return;
            if (previous != null) previous.IsSelectedTab = false;
            if (value != null)
            {
                value.Start();
                value.IsSelectedTab = true;
            }
        }
    }

    /// <summary>True for the pane that keyboard commands and the sidebar act on.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    /// <summary>The window-level buttons (jump, split, settings) live in the tab strip of the right-most visible pane.</summary>
    public bool ShowWindowCommands { get => _showWindowCommands; set => Set(ref _showWindowCommands, value); }

    public FileTabViewModel AddTab(string location = "", string query = "", bool recursive = false, bool select = true, string? selectName = null)
    {
        var tab = new FileTabViewModel(_index, location, query, recursive) { PendingSelectName = selectName };
        int at = SelectedTab != null ? Tabs.IndexOf(SelectedTab) + 1 : Tabs.Count;
        Tabs.Insert(Math.Clamp(at, 0, Tabs.Count), tab);
        if (select || SelectedTab == null) SelectedTab = tab;
        return tab;
    }

    public void CloseTab(FileTabViewModel? tab)
    {
        if (tab == null) return;
        int i = Tabs.IndexOf(tab);
        if (i < 0) return;
        _closed.Push(tab.ToState());
        // Never leave a pane without a tab: closing the last one resets it to the home folder.
        if (Tabs.Count == 1) AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), select: false);
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab == null) SelectedTab = Tabs[Math.Clamp(i, 0, Tabs.Count - 1)];
        tab.Dispose();
    }

    /// <summary>Brings back the most recently closed tab (Ctrl+Shift+T).</summary>
    public void ReopenClosedTab()
    {
        if (_closed.Count == 0) return;
        var state = _closed.Pop();
        AddTab(state.Location, state.Query, state.Recursive).IsLocked = state.Locked;
    }

    public void SelectRelative(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab == null) return;
        int i = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[((i + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    /// <summary>Switches to the tab that searches everywhere, creating it if this pane does not have one.</summary>
    public FileTabViewModel ShowEverywhere()
    {
        var tab = Tabs.FirstOrDefault(t => t.IsEverywhere);
        if (tab == null) return AddTab("");
        SelectedTab = tab;
        return tab;
    }

    public void Restore(IReadOnlyList<TabState> tabs, int selected, string fallbackLocation)
    {
        // Added directly (not via AddTab) so the saved order is kept and only the selected tab loads its folder.
        foreach (var t in tabs) Tabs.Add(new FileTabViewModel(_index, t.Location, t.Query, t.Recursive) { IsLocked = t.Locked });
        if (Tabs.Count == 0) Tabs.Add(new FileTabViewModel(_index, fallbackLocation));
        SelectedTab = Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
    }
}
