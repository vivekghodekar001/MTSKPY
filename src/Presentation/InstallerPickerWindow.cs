using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mskpy.Infrastructure;

namespace Mskpy.Windows;

/// <summary>V5/V2: modal picker for a vendor person and their number. One row per
/// person, alphabetical. OK returns the chosen person; nothing is written to the
/// record until OK.</summary>
public sealed class InstallerPickerWindow : Window
{
    public string? VendorCode { get; set; }
    public string? ChosenName { get; private set; }
    public string? ChosenMobile { get; private set; }
    public bool? OkOrNull { get; private set; }

    readonly TextBox searchBox = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly ListBox list = new() { MinHeight = 160, VerticalAlignment = VerticalAlignment.Stretch };
    readonly Button okButton = new() { Content = "_OK", IsEnabled = false, Margin = new Thickness(0, 0, 6, 0) };
    readonly Button cancelButton = new() { Content = "_Cancel" };
    readonly TextBox addName = new() { Width = 140 };
    readonly TextBox addMobile = new() { Width = 140 };
    readonly Button addButton = new() { Content = "Add" };
    readonly TextBlock addErrorBlock = new()
    {
        Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed
    };

    readonly Dictionary<string, (string Name, string Mobile)[]> cache = new();

    public InstallerPickerWindow(string vendorCode)
    {
        VendorCode = vendorCode;
        Title = "Select installer";
        Width = 380; Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Owner = System.Windows.Application.Current.MainWindow;

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // search
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // add error
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // buttons
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // add row

        main.Children.Add(searchBox); Grid.SetRow(searchBox, 0);
        main.Children.Add(list); Grid.SetRow(list, 1);
        main.Children.Add(addErrorBlock); Grid.SetRow(addErrorBlock, 2);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        main.Children.Add(buttons); Grid.SetRow(buttons, 3);

        var addRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        addRow.Children.Add(new TextBlock { Text = "Add new:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 4, 0) });
        addRow.Children.Add(addName);
        addRow.Children.Add(addMobile);
        addRow.Children.Add(addButton);
        main.Children.Add(addRow); Grid.SetRow(addRow, 4);
        Content = main;

        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem != null) Ok();
            else if (e.Key == Key.Escape) { OkOrNull = false; Close(); }
        };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) Ok(); };
        searchBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem != null) Ok(); };
        okButton.Click += (_, _) => Ok();
        cancelButton.Click += (_, _) => { OkOrNull = false; Close(); };
        searchBox.TextChanged += (_, _) => RefreshList();
        list.SelectionChanged += (_, _) => okButton.IsEnabled = list.SelectedItem != null;
        addButton.Click += (_, _) => DoAdd();
        addName.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoAdd(); };
        addMobile.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoAdd(); };

        RefreshList();
    }

    void Ok()
    {
        OkOrNull = true;
        if (list.SelectedItem is string chosen && !string.IsNullOrWhiteSpace(chosen))
        {
            var row = GetRowByName(chosen);
            ChosenName = row.Item1;
            ChosenMobile = row.Item2;
        }
        Close();
    }

    (string Name, string Mobile) GetRowByName(string name)
    {
        foreach (var row in GetRows(VendorCode ?? ""))
            if (string.Equals(row.Name, name, StringComparison.Ordinal)) return row;
        return (name, "");
    }

    void RefreshList()
    {
        var vendorCode = VendorCode ?? "";
        var rows = GetRows(vendorCode);
        LogInstaller("vendor book " + vendorCode + ": " + rows.Length + " rows");

        var typed = (searchBox.Text ?? "").Trim();
        var filtered = typed.Length == 0
            ? rows
            : rows.Where(r => r.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                           || r.Mobile.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToArray();

        list.ItemsSource = filtered.Select(r => r.Name);
        if (filtered.Length > 0 && list.SelectedItem == null) list.SelectedIndex = 0;
        addErrorBlock.Visibility = Visibility.Collapsed;
    }

    (string Name, string Mobile)[] GetRows(string vendorCode)
    {
        if (cache.TryGetValue(vendorCode, out var cached)) return cached;
        var rows = new (string, string)[0];
        try
        {
            using var store = new InspectionStore();
            rows = store.VendorContacts(vendorCode).Select(c => (c.PersonName, c.Mobile)).ToArray();
        }
        catch { rows = new (string, string)[0]; }
        cache[vendorCode] = rows;
        return rows;
    }

    void DoAdd()
    {
        var name = (addName.Text ?? "").Trim();
        var mobile = (addMobile.Text ?? "").Trim();
        addErrorBlock.Visibility = Visibility.Collapsed;
        if (name.Length == 0) { ShowAddError("Name is required."); return; }
        if (!Regex.IsMatch(mobile, @"^[1-9]\d{9,}$")) { ShowAddError("Mobile No must be 10+ digits starting with a non-zero digit."); return; }
        try
        {
            using var store = new InspectionStore();
            store.UpsertVendorContact(VendorCode ?? "", name, mobile);
        }
        catch (Exception ex) { ShowAddError("Could not save: " + ex.Message); return; }
        cache.Clear();
        addName.Text = ""; addMobile.Text = "";
        RefreshList();
        if (list.Items.Count > 0) list.SelectedIndex = list.Items.Count - 1;
    }

    void ShowAddError(string text)
    {
        addErrorBlock.Text = text;
        addErrorBlock.Visibility = Visibility.Visible;
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
