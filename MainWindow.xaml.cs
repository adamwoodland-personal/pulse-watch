using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ConnectionChecker.Models;
using ConnectionChecker.Services;
using WinForms = System.Windows.Forms;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using TextBox = System.Windows.Controls.TextBox;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using ScrollChangedEventArgs = System.Windows.Controls.ScrollChangedEventArgs;

namespace ConnectionChecker;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<HostEntry> _hosts = new();
    private readonly ObservableCollection<GroupTab> _tabs = new();
    private readonly System.Windows.Data.ListCollectionView _view; // the rows on the selected tab
    private Guid? _selectedGroup; // null = Default
    private readonly MonitorService _monitor = new();
    private readonly AlertOverlayWindow _overlay = new();
    private readonly HistoryLog _history = new();
    private HistoryWindow? _historyWindow;
    private readonly WinForms.NotifyIcon _trayIcon;
    private AppSettings _settings = new();
    private bool _exiting;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

        Icon = AppIcon.WindowIcon;

        _settings = SettingsService.Load(out var loadWarning);
        ApplyOverlaySettings();
        _overlay.Muted = _settings.MuteSounds;

        // Subscribe before any loop starts so a fast first check can't
        // publish its initial offline transition into the void.
        _monitor.StatusChanged += OnStatusChanged;

        foreach (var host in _settings.Hosts)
        {
            _hosts.Add(host);
            if (host.Enabled)
                _monitor.Start(host);
        }

        if (loadWarning != null)
        {
            Loaded += (_, _) => System.Windows.MessageBox.Show(this, loadWarning,
                "PULSE//WATCH", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The list shows one group (tab) at a time; a target moved to another
        // group drops out of view as soon as its GroupId changes.
        _view = new System.Windows.Data.ListCollectionView(_hosts)
        {
            Filter = item => ((HostEntry)item).GroupId == _selectedGroup,
            IsLiveFiltering = true
        };
        _view.LiveFilteringProperties.Add(nameof(HostEntry.GroupId));
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateEmptyState();
        HostList.ItemsSource = _view;
        HostList.SelectionChanged += (_, _) =>
        {
            var hasSelection = HostList.SelectedItem != null;
            EditButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
        };

        _tabs.Add(GroupTab.Default());
        foreach (var group in _settings.Groups)
            _tabs.Add(new GroupTab { Id = group.Id, Name = group.Name });
        TabList.ItemsSource = _tabs;
        ShowGroup(_settings.SelectedGroup);
        RefreshTabs();
        UpdateEmptyState();
        Loaded += (_, _) => BringTabIntoView(TabList.SelectedItem);

        _trayIcon = CreateTrayIcon();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };

        // The overlay shows itself on the first tile and hides when empty,
        // so it's never on screen (or in the way) while nothing is alerting.

        Closing += (_, e) =>
        {
            // X minimises to tray when enabled; only the tray's Exit really quits.
            if (_settings.CloseToTray && !_exitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_settings.ConfirmOnExit)
            {
                // Owner only when visible — exiting from the tray has a hidden window.
                var answer = IsVisible
                    ? System.Windows.MessageBox.Show(this,
                        "Stop monitoring and exit PULSE//WATCH?", "PULSE//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                    : System.Windows.MessageBox.Show(
                        "Stop monitoring and exit PULSE//WATCH?", "PULSE//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    _exitRequested = false;
                    return;
                }
            }

            _exiting = true;
            _monitor.Dispose();
            SaveSettings();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _overlay.Close();
        };
    }

    // ===== Tray =====

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("History", null, (_, _) => { RestoreFromTray(); ShowHistory(); });

        var mute = new WinForms.ToolStripMenuItem("Mute alert sounds")
        {
            CheckOnClick = true,
            Checked = _settings.MuteSounds
        };
        mute.CheckedChanged += (_, _) =>
        {
            _settings.MuteSounds = mute.Checked;
            _overlay.Muted = mute.Checked;
            SaveSettings();
        };
        menu.Items.Add(mute);
        menu.Items.Add(BuildSuppressMenu());

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });

        var icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateTrayIcon(),
            Text = $"PULSE//WATCH {VersionTag}",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ===== Tile suppression (session-only) =====

    private DateTime? _suppressUntil; // null = off, MaxValue = until re-enabled
    private WinForms.ToolStripMenuItem? _suppressMenu;
    private WinForms.ToolStripMenuItem? _suppressOffItem;

    private bool TilesSuppressed => _suppressUntil is { } until && DateTime.Now < until;

    private WinForms.ToolStripMenuItem BuildSuppressMenu()
    {
        _suppressMenu = new WinForms.ToolStripMenuItem("Suppress tiles");
        _suppressOffItem = AddSuppressOption("Off — show tiles", null);
        _suppressOffItem.Checked = true;
        AddSuppressOption("Until re-enabled", TimeSpan.MaxValue);
        foreach (var (label, minutes) in new[]
                 { ("For 1 minute", 1), ("For 5 minutes", 5), ("For 15 minutes", 15),
                   ("For 30 minutes", 30), ("For 1 hour", 60), ("For 12 hours", 720) })
            AddSuppressOption(label, TimeSpan.FromMinutes(minutes));

        // A timed suppression may have lapsed since the menu was last open —
        // snap the checkmark and title back to reality before showing it.
        _suppressMenu.DropDownOpening += (_, _) =>
        {
            if (!TilesSuppressed && _suppressUntil != null)
            {
                _suppressUntil = null;
                CheckOnlySuppressItem(_suppressOffItem!);
            }
            RefreshSuppressTitle();
        };
        return _suppressMenu;
    }

    private WinForms.ToolStripMenuItem AddSuppressOption(string label, TimeSpan? duration)
    {
        var item = new WinForms.ToolStripMenuItem(label);
        item.Click += (_, _) =>
        {
            _suppressUntil = duration == null ? null
                : duration == TimeSpan.MaxValue ? DateTime.MaxValue
                : DateTime.Now + duration.Value;
            CheckOnlySuppressItem(item);
            RefreshSuppressTitle();
        };
        _suppressMenu!.DropDownItems.Add(item);
        return item;
    }

    private void CheckOnlySuppressItem(WinForms.ToolStripMenuItem selected)
    {
        foreach (WinForms.ToolStripMenuItem item in _suppressMenu!.DropDownItems)
            item.Checked = ReferenceEquals(item, selected);
    }

    private void RefreshSuppressTitle()
    {
        _suppressMenu!.Text = _suppressUntil switch
        {
            null => "Suppress tiles",
            { } u when u == DateTime.MaxValue => "Suppress tiles (until re-enabled)",
            { } u => $"Suppress tiles (until {u:HH:mm})"
        };
    }

    private bool _trayShowsAlert;

    // Version in the tooltip so it's always obvious WHICH build is running —
    // a stale auto-started copy once masqueraded as a fixed one for days.
    private static readonly string VersionTag =
        $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";

    /// <summary>Red ring while any active target is offline, cyan otherwise.</summary>
    private void UpdateTrayIcon()
    {
        var offline = _hosts.Count(h => h.Enabled && h.Status == HostStatus.Offline);
        var alert = offline > 0;
        _trayIcon.Text = alert
            ? $"PULSE//WATCH {VersionTag} — {offline} offline"
            : $"PULSE//WATCH {VersionTag} — all targets up";
        if (alert == _trayShowsAlert) return;

        _trayShowsAlert = alert;
        var old = _trayIcon.Icon;
        _trayIcon.Icon = AppIcon.CreateTrayIcon(alert);
        old?.Dispose();
    }

    private void HideToTray() => Hide(); // to tray; the overlay keeps showing alerts

    private void ApplyOverlaySettings()
    {
        if (ValidationHelpers.ParseColor(_settings.OfflineTileColor) is { } offline)
            _overlay.OfflineColor = offline;
        if (ValidationHelpers.ParseColor(_settings.OnlineTileColor) is { } online)
            _overlay.OnlineColor = online;
        _overlay.OnLeft = _settings.AlertSide == AlertSide.Left;
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();

    /// <summary>One history window at a time; a second request brings it forward.</summary>
    private void ShowHistory()
    {
        if (_historyWindow != null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_history) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ApplyOverlaySettings();
            SaveSettings();
        }
    }

    // ===== State =====

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        var none = _hosts.Count == 0;
        EmptyTitle.Text = none ? "NO TARGETS CONFIGURED" : "NO TARGETS IN THIS GROUP";
        EmptyHint.Text = none
            ? "Add a host or IP below to begin monitoring."
            : "Add one below, or drag a target here from another tab.";
    }

    private void SaveSettings()
    {
        UpdateTrayIcon(); // list or status just changed (remove/pause/edit clear alerts)
        RefreshTabs();
        _settings.Hosts = _hosts.ToList();
        _settings.Groups = _tabs.Where(t => t.Id != null)
            .Select(t => new HostGroup { Id = t.Id!.Value, Name = t.Name }).ToList();
        _settings.SelectedGroup = _selectedGroup;
        if (!SettingsService.Save(_settings))
        {
            _trayIcon?.ShowBalloonTip(3000, "PULSE//WATCH",
                "Settings could not be saved — recent changes may be lost on exit.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    // ===== CRUD =====

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditHostWindow(_settings, _selectedGroup) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _hosts.Add(dialog.Result);
            if (dialog.Result.Enabled)
                _monitor.Start(dialog.Result);
            SaveSettings();
            ShowGroup(dialog.Result.GroupId); // added to another group: go there so it's in view
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private static System.Windows.Controls.ListBoxItem? FindListBoxItem(object? source)
    {
        var element = source as DependencyObject;
        while (element != null && element is not System.Windows.Controls.ListBoxItem)
        {
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as System.Windows.Controls.ListBoxItem;
    }

    // Double-click a row = edit that row; double-click empty space = add.
    // (Selection alone can't tell these apart — a selected row stays selected
    // when you click the background.)
    private void HostList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource) is { DataContext: HostEntry host })
        {
            HostList.SelectedItem = host;
            EditSelected();
        }
        else
        {
            Add_Click(sender, e);
        }
    }

    // ===== Drag-to-reorder =====

    private Point _dragStart;
    private HostEntry? _dragCandidate;

    private void HostList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindListBoxItem(e.OriginalSource)?.DataContext as HostEntry;
    }

    private void HostList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;

        // Don't hijack plain clicks/double-clicks: require a real drag distance.
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(HostList, dragged, DragDropEffects.Move);
    }

    private void HostList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(HostEntry)) is not HostEntry dragged) return;
        var oldIndex = _hosts.IndexOf(dragged);
        if (oldIndex < 0) return;

        int insertAt;
        var targetItem = FindListBoxItem(e.OriginalSource);
        if (targetItem?.DataContext is HostEntry target && !ReferenceEquals(target, dragged))
        {
            // Above the row's midpoint = before it, below = after it.
            insertAt = _hosts.IndexOf(target);
            if (e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2) insertAt++;
            if (insertAt > oldIndex) insertAt--; // account for removal of the dragged row
        }
        else
        {
            insertAt = _hosts.Count - 1; // dropped on empty space: move to end
        }

        if (insertAt != oldIndex)
        {
            _hosts.Move(oldIndex, insertAt);
            SaveSettings();
        }
    }

    private void EditSelected()
    {
        if (HostList.SelectedItem is not HostEntry host) return;

        var dialog = new EditHostWindow(_settings, host) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.RemoveRequested)
        {
            RemoveHost(host);
        }
        else if (dialog.Result != null)
        {
            _monitor.Stop(host.Id); // stop the old loop before mutating shared state
            host.Name = dialog.Result.Name;
            host.Address = dialog.Result.Address;
            host.CheckType = dialog.Result.CheckType;
            host.Port = dialog.Result.Port;
            host.Path = dialog.Result.Path;
            host.IgnoreCertErrors = dialog.Result.IgnoreCertErrors;
            host.DnsServer = dialog.Result.DnsServer;
            host.DnsRecordType = dialog.Result.DnsRecordType;
            host.IpVersion = dialog.Result.IpVersion;
            host.IntervalSeconds = dialog.Result.IntervalSeconds;
            host.RetryCount = dialog.Result.RetryCount;
            host.TimeoutMs = dialog.Result.TimeoutMs;
            host.PlaySound = dialog.Result.PlaySound;
            host.OfflineColor = dialog.Result.OfflineColor;
            host.OnlineColor = dialog.Result.OnlineColor;
            host.GroupId = dialog.Result.GroupId;
            host.Enabled = dialog.Result.Enabled;
            host.Status = HostStatus.Unknown;
            host.LatencyMs = -1;
            host.LastFailure = null;
            if (host.Enabled)
            {
                _monitor.Start(host); // fresh loop with new parameters
            }
            else
            {
                _overlay.DismissTilesFor(host.Id);
            }
            SaveSettings();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is HostEntry host)
            RemoveHost(host);
    }

    private void RemoveHost(HostEntry host)
    {
        _monitor.Stop(host.Id);
        _hosts.Remove(host);
        _overlay.DismissTilesFor(host.Id);
        SaveSettings();
    }

    // ===== Groups (tabs) =====
    // Cosmetic only: a group decides which tab lists a target, never how it's checked.

    /// <summary>Show a group's tab (null or unknown = Default).</summary>
    private void ShowGroup(Guid? id) => TabList.SelectedItem = _tabs.FirstOrDefault(t => t.Id == id) ?? _tabs[0];

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is not GroupTab tab)
        {
            // Ctrl+click can deselect a ListBox item, but a tab is always showing.
            ShowGroup(e.RemovedItems.OfType<GroupTab>().FirstOrDefault(_tabs.Contains)?.Id);
            return;
        }
        _selectedGroup = tab.Id;
        HostList.SelectedItem = null;
        _view.Refresh();
        BringTabIntoView(tab);
    }

    /// <summary>Counts, red dots and move-left/right availability on every tab.</summary>
    private void RefreshTabs()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            tab.Count = _hosts.Count(h => h.GroupId == tab.Id);
            tab.Offline = _hosts.Count(h => h.GroupId == tab.Id && h.Enabled && h.Status == HostStatus.Offline);
            tab.CanMoveLeft = i > 1; // Default stays first
            tab.CanMoveRight = i > 0 && i < _tabs.Count - 1;
        }
    }

    // Ctrl+PgUp / Ctrl+PgDn step through the tabs, as in Excel and browsers.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.PageUp or Key.PageDown)
        {
            var next = TabList.SelectedIndex + (e.Key == Key.PageDown ? 1 : -1);
            if (next >= 0 && next < _tabs.Count) TabList.SelectedIndex = next;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var tab = new GroupTab { Id = Guid.NewGuid(), Name = HostGroup.Unique("New group", _tabs.Select(t => t.Name)) };
        _tabs.Add(tab);
        TabList.SelectedItem = tab;
        SaveSettings();
        BeginRename(tab);
    }

    private static GroupTab? MenuTab(object sender) => (sender as FrameworkElement)?.DataContext as GroupTab;

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab) BeginRename(tab);
    }

    private void MoveGroupLeft_Click(object sender, RoutedEventArgs e) => MoveGroup(MenuTab(sender), -1);

    private void MoveGroupRight_Click(object sender, RoutedEventArgs e) => MoveGroup(MenuTab(sender), +1);

    private void MoveGroup(GroupTab? tab, int by)
    {
        if (tab == null) return;
        var from = _tabs.IndexOf(tab);
        var to = from + by;
        if (from < 1 || to < 1 || to >= _tabs.Count) return; // Default stays first
        _tabs.Move(from, to);
        SaveSettings();
        BringTabIntoView(tab);
    }

    /// <summary>Its targets move to Default; nothing stops being monitored.</summary>
    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { CanEdit: true } tab) return;
        var members = _hosts.Where(h => h.GroupId == tab.Id).ToList();
        if (members.Count > 0)
        {
            var moving = members.Count == 1 ? "Its target moves" : $"Its {members.Count} targets move";
            var answer = System.Windows.MessageBox.Show(this,
                $"Delete the group \"{tab.Name}\"?\n\n{moving} to {HostGroup.DefaultName} and keep being monitored.",
                "PULSE//WATCH", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        foreach (var host in members) host.GroupId = null;
        if (ReferenceEquals(TabList.SelectedItem, tab))
            TabList.SelectedItem = _tabs[_tabs.IndexOf(tab) - 1];
        _tabs.Remove(tab);
        SaveSettings();
    }

    // ----- Rename in place (double-click, F2, or the tab's menu) -----

    private void BeginRename(GroupTab tab)
    {
        if (!tab.CanEdit) return;
        TabList.SelectedItem = tab;
        tab.IsEditing = true; // shows the tab's TextBox, which focuses itself
    }

    private void TabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource) is { DataContext: GroupTab { IsEditing: false } tab })
            BeginRename(tab);
    }

    private void TabList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && TabList.SelectedItem is GroupTab { IsEditing: false } tab)
        {
            BeginRename(tab);
            e.Handled = true;
        }
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: GroupTab tab, IsVisible: true } box) return;
        box.Text = tab.Name;
        // Late enough that a closing context menu has finished handing focus back.
        Dispatcher.InvokeAsync(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.ContextIdle);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: GroupTab tab } box) return;
        if (e.Key == Key.Enter) CommitRename(tab, box.Text);
        else if (e.Key == Key.Escape) tab.IsEditing = false;
        else return;
        e.Handled = true;
        (TabList.ItemContainerGenerator.ContainerFromItem(tab) as UIElement)?.Focus();
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: GroupTab { IsEditing: true } tab } box)
            CommitRename(tab, box.Text);
    }

    /// <summary>Blank keeps the old name; a name another tab has gets a number.</summary>
    private void CommitRename(GroupTab tab, string text)
    {
        tab.IsEditing = false;
        if (HostGroup.CleanName(text) is not string name || name == tab.Name) return;
        tab.Name = HostGroup.Unique(name, _tabs.Where(t => t != tab).Select(t => t.Name));
        SaveSettings();
    }

    // ----- Tab overflow: Excel-style arrows, and the mouse wheel -----

    private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        TabArrows.Visibility = TabScroller.ScrollableWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        TabsLeftButton.IsEnabled = TabScroller.HorizontalOffset > 0.5;
        TabsRightButton.IsEnabled = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
    }

    private void TabsLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-1);

    private void TabsRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(+1);

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollTabs(e.Delta > 0 ? -1 : +1);
        e.Handled = true;
    }

    /// <summary>
    /// One tab at a time: right brings the next cut-off tab fully into view, left the previous one.
    /// A tab cut off by only a sliver is skipped, so every click visibly moves.
    /// </summary>
    private void ScrollTabs(int direction)
    {
        const double minStep = 12;
        var offset = TabScroller.HorizontalOffset;
        var viewRight = offset + TabScroller.ViewportWidth;
        var edges = _tabs.Select(t => TabList.ItemContainerGenerator.ContainerFromItem(t))
            .OfType<FrameworkElement>()
            .Select(c =>
            {
                var left = c.TransformToAncestor(TabList).Transform(new Point(0, 0)).X;
                return (Left: left, Right: left + c.ActualWidth);
            })
            .ToList();

        if (direction > 0)
        {
            foreach (var tab in edges)
                if (tab.Right > viewRight + minStep) { TabScroller.ScrollToHorizontalOffset(tab.Right - TabScroller.ViewportWidth); return; }
            TabScroller.ScrollToRightEnd();
        }
        else
        {
            for (var i = edges.Count - 1; i >= 0; i--)
                if (edges[i].Left < offset - minStep) { TabScroller.ScrollToHorizontalOffset(edges[i].Left); return; }
            TabScroller.ScrollToLeftEnd();
        }
    }

    private void BringTabIntoView(object? tab)
        => (tab == null ? null : TabList.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView();

    // ----- Drag a target row onto a tab to move it to that group -----

    private static HostEntry? DraggedHost(DragEventArgs e) => e.Data.GetData(typeof(HostEntry)) as HostEntry;

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        var tab = (sender as FrameworkElement)?.DataContext as GroupTab;
        var ok = tab != null && DraggedHost(e) is { } host && host.GroupId != tab.Id;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (tab != null) tab.IsDropTarget = ok;
        e.Handled = true;
    }

    private void Tab_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupTab tab) tab.IsDropTarget = false;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupTab tab) return;
        tab.IsDropTarget = false;
        e.Handled = true;
        if (DraggedHost(e) is not { } host || host.GroupId == tab.Id || !_hosts.Contains(host)) return;
        host.GroupId = tab.Id; // no restart: grouping doesn't touch the check loop
        SaveSettings();
    }

    private void ConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(SettingsService.Dir);
        System.Diagnostics.Process.Start("explorer.exe", SettingsService.Dir);
    }

    // ===== Alerts =====

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;

            // Drop events from loops that were stopped/replaced (host removed,
            // edited, or paused) between publication and this marshal.
            if (!_monitor.IsCurrent(e.Host.Id, e.Token)) return;

            UpdateTrayIcon();
            RefreshTabs();
            _history.Record(e.Host, e.NewStatus); // logged even while tiles are suppressed

            if (e.NewStatus == HostStatus.Offline)
            {
                if (!TilesSuppressed)
                    _overlay.ShowAlert(e.Host, isOnline: false, _settings.AlertDurationSeconds);
            }
            else if (e.NewStatus == HostStatus.Online && e.OldStatus == HostStatus.Offline)
            {
                _overlay.DismissTilesFor(e.Host.Id); // clear stale red tiles even while suppressed
                if (!TilesSuppressed)
                    _overlay.ShowAlert(e.Host, isOnline: true, _settings.AlertDurationSeconds);
            }
        });
    }
}
