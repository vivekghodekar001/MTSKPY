using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Mskpy.Infrastructure;

namespace Mskpy.Windows;

/// <summary>
/// V2: modal installer picker. One row per contact (name left, number right),
/// filtered live by name or number. OK returns the chosen pair; nothing is
/// written to the record until the caller sees <see cref="OkOrNull"/> true.
/// The contact book itself is only changed by the Add new area.
/// </summary>
public sealed class InstallerPickerWindow : Window
{
    /// <summary>V2/V4: one row of the picker - the name and the full number.</summary>
    public sealed record PickerRow(long RowId, string Name, string Mobile);

    public string VendorCode { get; }
    public string? ChosenName { get; private set; }
    public string? ChosenMobile { get; private set; }
    public bool? OkOrNull { get; private set; }

    readonly InspectionStore? sharedStore;
    InspectionStore? ownedStore;
    readonly IReadOnlyList<(string Name, string Mobile)> bookRows;

    readonly TextBox searchBox = new()
    {
        Height = 28, MinHeight = 0, FontSize = 12, Padding = new Thickness(6, 2, 6, 2)
    };
    readonly TextBlock searchHint = new()
    {
        Text = "Search name or number",
        Foreground = Brushes.Gray,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
        IsHitTestVisible = false
    };
    readonly ListBox list = new() { MinHeight = 160, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 6) };
    readonly Button okButton = new() { Content = "OK", IsEnabled = false, MinWidth = 76, MinHeight = 0, FontSize = 12, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
    readonly Button cancelButton = new() { Content = "Cancel", MinWidth = 76, MinHeight = 0, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
    readonly Button undoLink = new() { Content = "", Visibility = Visibility.Collapsed, MinHeight = 0, FontSize = 12, Padding = new Thickness(6, 4, 6, 4) };
    readonly TextBox addName = new() { Width = 130, Height = 28, MinHeight = 0, FontSize = 12, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Installer name" };
    readonly TextBox addMobile = new() { Width = 130, Height = 28, MinHeight = 0, FontSize = 12, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Mobile No (10 digits)" };
    readonly Button addButton = new() { Content = "Add", MinWidth = 54, MinHeight = 0, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
    readonly TextBlock addErrorBlock = new()
    {
        Foreground = Brushes.DarkRed,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
        Visibility = Visibility.Collapsed
    };
    readonly TextBlock inlineError = new()
    {
        Foreground = Brushes.Red,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
        Visibility = Visibility.Collapsed
    };

    ObservableCollection<PickerRow> rows = new();
    DateTime _openStart = DateTime.UtcNow;
    string _lastStep = "none";
    DateTime _lastStepTime = DateTime.UtcNow;
    bool _busy;
    bool _rebuilding;
    bool _imported;
    bool _watchdogFired;
    PickerRow? lastRemoved = null;
    ListBoxItem? lastRemovedItem = null;
    int lastRemovedIndex = -1;
    Brush mutedBrush = Brushes.Gray;

    /// <summary>V3: the caller passes its own store (so the picker and the record
    /// share one connection) plus the officer's book for this vendor, which is
    /// merged in so a person added through Edit… is offered too.</summary>
    public InstallerPickerWindow(
        string vendorCode,
        InspectionStore? store = null,
        IEnumerable<(string Name, string Mobile)>? bookRows = null)
    {
        VendorCode = vendorCode ?? "";
        this.sharedStore = store;
        this.bookRows = (bookRows ?? Array.Empty<(string, string)>()).ToList();

        Title = "Select installer";
        Width = 380;
        Height = 420;
        MinWidth = 380;
        MinHeight = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner != null) Owner = owner;

        // Match the app's surface: the default white dialog with the theme's
        // dark fields looked like a broken form.
        var app = System.Windows.Application.Current;
        var bg = app?.TryFindResource("Bg") as Brush ?? Brushes.White;
        var surface = app?.TryFindResource("Surface") as Brush ?? Brushes.White;
        var text = app?.TryFindResource("Text") as Brush ?? Brushes.Black;
        var muted = app?.TryFindResource("Muted") as Brush ?? Brushes.Gray;
        mutedBrush = muted;
        Background = bg;
        Foreground = text;
        FontFamily = new FontFamily("Segoe UI");
        searchHint.Foreground = muted;
        list.Background = surface;
        list.Foreground = text;
        list.BorderBrush = muted;
        list.BorderThickness = new Thickness(1);
        list.Padding = new Thickness(2);

        var main = new Grid { Margin = new Thickness(12) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                         // search
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                         // add link/panel
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // list
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                         // buttons
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                         // inline error

        var searchHost = new Grid();
        searchHost.Children.Add(searchBox);
        searchHost.Children.Add(searchHint);
        main.Children.Add(searchHost);
        Grid.SetRow(searchHost, 0);

        var addLink = new Button
        {
            Content = "Add new installer",
            Style = (Style)System.Windows.Application.Current.FindResource("LinkButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 4)
        };
        main.Children.Add(addLink);
        Grid.SetRow(addLink, 1);

        var addPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 8), Visibility = Visibility.Collapsed };
        var nameHost = new Grid { Width = 130, Margin = new Thickness(0, 0, 6, 0) };
        nameHost.Children.Add(addName);
        var nameHint = new TextBlock { Text = "Name", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false };
        nameHost.Children.Add(nameHint);
        addName.TextChanged += (_, _) => nameHint.Visibility = string.IsNullOrWhiteSpace(addName.Text) ? Visibility.Visible : Visibility.Collapsed;
        var mobileHost = new Grid { Width = 130, Margin = new Thickness(0, 0, 6, 0) };
        mobileHost.Children.Add(addMobile);
        var mobileHint = new TextBlock { Text = "Mobile (10 digits)", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false };
        mobileHost.Children.Add(mobileHint);
        addMobile.TextChanged += (_, _) => mobileHint.Visibility = string.IsNullOrWhiteSpace(addMobile.Text) ? Visibility.Visible : Visibility.Collapsed;
        var addRow = new WrapPanel();
        addRow.Children.Add(nameHost);
        addRow.Children.Add(mobileHost);
        addRow.Children.Add(addButton);
        addPanel.Children.Add(addRow);
        addPanel.Children.Add(addErrorBlock);
        main.Children.Add(addPanel);
        Grid.SetRow(addPanel, 1);

        addLink.Click += (_, _) =>
        {
            addPanel.Visibility = addPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            addLink.Content = addPanel.Visibility == Visibility.Visible ? "Hide new installer" : "Add new installer";
        };

        main.Children.Add(list);
        Grid.SetRow(list, 2);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(undoLink);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        main.Children.Add(buttons);
        Grid.SetRow(buttons, 3);
        main.Children.Add(inlineError);
        Grid.SetRow(inlineError, 4);
        Content = main;

        AutomationProperties.SetName(searchBox, "Search installer");
        AutomationProperties.SetName(list, "Installer contacts");
        AutomationProperties.SetName(okButton, "OK");
        AutomationProperties.SetName(cancelButton, "Cancel");
        AutomationProperties.SetName(addName, "New installer name");
        AutomationProperties.SetName(addMobile, "New installer mobile");
        AutomationProperties.SetName(addButton, "Add installer");
        AutomationProperties.SetName(undoLink, "Undo remove installer");

        // Esc closes without touching the record, wherever focus is.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            OkOrNull = false;
            Close();
            e.Handled = true;
        };

        searchBox.TextChanged += (_, _) => { if (_rebuilding) return; UpdateSearchHint(); if (_rebuilding) return; ApplyFilter(); };
        list.SelectionChanged += (_, _) => okButton.IsEnabled = SelectedRow() != null;
        list.MouseDoubleClick += (_, _) => { if (SelectedRow() != null) Ok(); };
        list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && SelectedRow() != null) Ok(); if (e.Key == Key.Delete && list.SelectedItem is ListBoxItem it) { DeleteRow(it); e.Handled = true; } };
        searchBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && SelectedRow() != null) Ok(); };
        okButton.Click += (_, _) => Ok();
        cancelButton.Click += (_, _) => { OkOrNull = false; Close(); };
        addButton.Click += (_, _) => DoAdd();
        undoLink.Click += (_, _) => UndoRemove();
        addName.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoAdd(); };
        addMobile.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoAdd(); };

        UpdateSearchHint();
        Loaded += (_, __) => OpenSequenceAsync();
        StartWatchdog();
    }

    void Step(string name)
    {
        _lastStep = name;
        _lastStepTime = DateTime.UtcNow;
        var ms = (int)(_lastStepTime - _openStart).TotalMilliseconds;
        LogInstaller($"pick {name} t={ms}");
    }

    void StartWatchdog()
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                System.Threading.Thread.Sleep(500);
                if (_busy && (DateTime.UtcNow - _lastStepTime).TotalMilliseconds > 3000 && !_watchdogFired)
                {
                    _watchdogFired = true;
                    LogInstaller($"pick WATCHDOG hung at {_lastStep}");
                    LogInstaller(Environment.StackTrace);
                }
            }
        }) { IsBackground = true };
        t.Start();
    }

    public System.Threading.Tasks.Task OpenAsync() { OpenSequenceAsync(); return System.Threading.Tasks.Task.CompletedTask; }
    void OpenSequenceAsync()
    {
        _openStart = DateTime.UtcNow;
        Step("open-enter");
        _busy = true;
        Step("busy-on");
        try
        {
            Step("vendor-resolve");
            rows.Clear();
            LogInstaller($"pick mutate count={rows.Count}");
            Step("import-enter");
            if (!_imported && bookRows.Any())
            {
                try { Store.ImportVendorContacts(VendorCode, bookRows); } catch { }
                _imported = true;
            }
            Step("import-exit");
            Step("load-enter");
            IReadOnlyList<(long RowId, string PersonName, string Mobile)> stored;
            try { stored = Store.VendorContactRows(VendorCode); }
            catch { stored = Array.Empty<(long, string, string)>(); }
            var loaded = stored.Select(c => new PickerRow(c.RowId, c.PersonName, c.Mobile)).ToList();
            rows.Clear();
            foreach (var r in loaded) rows.Add(r);
            Step("load-exit");
            Step("filter-enter");
            ApplyFilter();
            Step("filter-exit");
            Step("render-enter");
            Step("render-exit");
        }
        finally
        {
            _busy = false;
            Step("busy-off");
            Step("open-exit");
        }
    }

    InspectionStore Store
    {
        get
        {
            if (sharedStore != null) return sharedStore;
            return ownedStore ??= new InspectionStore();
        }
    }

    /// <summary>V2/V4: read the book once per open (and after each Add). The
    /// store rows win over the officer's book when a name appears in both.</summary>
    void Reload()
    {
        try
        {
            if (bookRows.Any())
            {
                var imported = Store.ImportVendorContacts(VendorCode, bookRows);
                LogInstaller($"picker import: vendor='{VendorCode}' imported={imported}");
            }
        }
        catch { }
        IReadOnlyList<(long RowId, string PersonName, string Mobile)> stored;
        try { stored = Store.VendorContactRows(VendorCode); }
        catch { stored = Array.Empty<(long, string, string)>(); }
        rows = new ObservableCollection<PickerRow>(stored.Select(c => new PickerRow(c.RowId, c.PersonName, c.Mobile)));
        LogInstaller($"vendor book {VendorCode}: {rows.Count} rows");
        ApplyFilter();
    }

    /// <summary>V4: one row per person name, alphabetical. No DISTINCT / TOP /
    /// grouping that could collapse two different names.</summary>
    internal static List<PickerRow> MergeContacts(
        IEnumerable<(long RowId, string PersonName, string Mobile)> fromStore,
        IEnumerable<(string Name, string Mobile)> fromBook)
    {
        var byName = new Dictionary<string, PickerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in fromStore)
        {
            var name = (c.PersonName ?? "").Trim();
            if (name.Length == 0) continue;
            byName[name] = new PickerRow(c.RowId, name, (c.Mobile ?? "").Trim());
        }
        foreach (var p in fromBook)
        {
            var name = (p.Name ?? "").Trim();
            if (name.Length == 0 || byName.ContainsKey(name)) continue;
            byName[name] = new PickerRow(0, name, (p.Mobile ?? "").Trim());
        }
        return byName.Values
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    void ApplyFilter()
    {
        var typed = (searchBox.Text ?? "").Trim();
        var filtered = typed.Length == 0
            ? rows.ToList()
            : rows.Where(r => r.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                           || r.Mobile.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();

        list.Items.Clear();
        if (filtered.Count == 0 && typed.Length == 0)
        {
            list.Items.Add(new ListBoxItem { Content = "No installers for this vendor — use Add new installer", IsEnabled = false });
        }
        foreach (var row in filtered)
        {
            var item = new ListBoxItem { Tag = row, Padding = new Thickness(2, 3, 2, 3) };
            item.Content = RowContent(row, () => DeleteRow(item));
            list.Items.Add(item);
        }

        okButton.IsEnabled = false;
        addErrorBlock.Visibility = Visibility.Collapsed;
    }

    void DeleteRow(ListBoxItem item)
    {
        if (item.Tag is not PickerRow row) return;
        var sv = FindScrollViewer(list);
        var offset = sv?.VerticalOffset ?? 0;
        System.Diagnostics.Debug.WriteLine($"delete: vendor='{VendorCode}' name='{row.Name}'");
        string deletedName = row.Name, deletedVendor = VendorCode;
        int n = 0;
        try { n = Store.DeleteVendorContactByRowId(row.RowId, out deletedName, out deletedVendor); } catch { }
        bool stillPresent = Store.VendorContactRows(VendorCode).Any(r => r.RowId == row.RowId);
        System.Diagnostics.Debug.WriteLine($"delete: rowid={row.RowId} name='{row.Name}' removed={(stillPresent?"no":"yes")}, {Store.VendorContactRows(VendorCode).Count} left");
        if (stillPresent || n == 0)
        {
            inlineError.Text = "Not removed — row still present after delete";
            inlineError.Visibility = Visibility.Visible;
            undoLink.Visibility = Visibility.Collapsed;
            lastRemoved = null;
            return;
        }
        int before = rows.Count;
        lastRemovedIndex = rows.IndexOf(row);
        rows.Remove(row);
        System.Diagnostics.Debug.WriteLine($"picker rows {before} -> {rows.Count}");
        lastRemoved = row;
        lastRemovedItem = item;
        list.Items.Remove(item);
        if (list.SelectedItem == item) { list.SelectedItem = null; okButton.IsEnabled = false; }
        ApplyFilter();
        if (sv != null) { sv.ScrollToVerticalOffset(offset); }
        undoLink.Content = $"Removed {row.Name} — Undo";
        undoLink.Visibility = Visibility.Visible;
        inlineError.Visibility = Visibility.Collapsed;
        if (list.Items.Count == 0 && (searchBox.Text ?? "").Trim().Length == 0)
        {
            list.Items.Add(new ListBoxItem { Content = "No installers for this vendor — use Add new installer", IsEnabled = false });
        }
    }

    void UndoRemove()
    {
        if (lastRemoved == null) return;
        var row = lastRemoved;
        try
        {
            Store.UpsertVendorContact(VendorCode, row.Name, row.Mobile);
            Store.ClearTombstone(VendorCode, row.Name);
            System.Diagnostics.Debug.WriteLine($"tombstone clear {VendorCode}/{row.Name}");
            System.Diagnostics.Debug.WriteLine($"undo: restored {row.Name}, {Store.VendorContacts(VendorCode).Count} left");
        }
        catch { }
        if (!rows.Any(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
        {
            int idx = lastRemovedIndex >= 0 && lastRemovedIndex <= rows.Count ? lastRemovedIndex : rows.TakeWhile(r => string.Compare(r.Name, row.Name, StringComparison.OrdinalIgnoreCase) < 0).Count();
            rows.Insert(idx, row);
        }
        lastRemoved = null;
        lastRemovedItem = null;
        undoLink.Visibility = Visibility.Collapsed;
        inlineError.Visibility = Visibility.Collapsed;
        ApplyFilter();
        foreach (var it in list.Items.OfType<ListBoxItem>())
        {
            if (it.Tag is PickerRow r && string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            { list.SelectedItem = it; break; }
        }
        okButton.IsEnabled = list.SelectedItem != null;
    }

    static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (r != null) return r;
        }
        return null;
    }

    void ReloadCount()
    {
        // no-op placeholder for future
    }

    /// <summary>V2: delete (24) | name (*) | number (Auto, right, dim).</summary>
    Grid RowContent(PickerRow row, Action delete)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var del = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 11, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(1),
            BorderBrush = mutedBrush, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            ToolTip = "Remove from list", VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(del, "Remove " + row.Name);
        del.Click += (_, e) => { delete(); e.Handled = true; };
        grid.Children.Add(del);
        var name = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var mobile = new TextBlock { Text = row.Mobile, VerticalAlignment = VerticalAlignment.Center, Foreground = mutedBrush, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 2, 0) };
        Grid.SetColumn(mobile, 2);
        grid.Children.Add(mobile);
        return grid;
    }

    PickerRow? SelectedRow() => (list.SelectedItem as ListBoxItem)?.Tag as PickerRow;

    void Ok()
    {
        var row = SelectedRow();
        if (row == null) return;
        ChosenName = row.Name;
        ChosenMobile = row.Mobile;
        OkOrNull = true;
        Close();
    }

    void DoAdd()
    {
        var name = (addName.Text ?? "").Trim();
        var mobile = (addMobile.Text ?? "").Trim();
        addErrorBlock.Visibility = Visibility.Collapsed;
        if (name.Length == 0) { ShowAddError("Name is required."); return; }
        if (!Regex.IsMatch(mobile, @"^[0-9]{10}$"))
        {
            ShowAddError("Mobile No must be exactly 10 digits.");
            return;
        }
        try { Store.UpsertVendorContact(VendorCode, name, mobile); }
        catch (Exception ex) { ShowAddError("Could not save: " + ex.Message); return; }

        addName.Text = "";
        addMobile.Text = "";
        searchBox.Text = ""; // the new row must be visible even under a filter
        Reload();
        SelectRow(name);
    }

    void SelectRow(string name)
    {
        foreach (var item in list.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is PickerRow row && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                list.SelectedItem = item;
                list.ScrollIntoView(item);
                return;
            }
        }
    }

    void ShowAddError(string text)
    {
        addErrorBlock.Text = text;
        addErrorBlock.Visibility = Visibility.Visible;
    }

    void UpdateSearchHint() =>
        searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnClosed(EventArgs e)
    {
        ownedStore?.Dispose();
        ownedStore = null;
        base.OnClosed(e);
    }

    static void LogInstaller(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "inspection.log"),
                string.Format("{0:O} {1}{2}", System.DateTime.Now, message, System.Environment.NewLine));
        }
        catch { }
    }
}
