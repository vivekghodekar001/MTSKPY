using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Mskpy.Domain;
using Mskpy.Infrastructure;
using Mskpy.Presentation.Services;

namespace Mskpy.Windows;

/// <summary>
/// Linestaff-inspection form window (P1-P10).
///
/// Contract (G1):
///   GET  /mtskpy/linestaff-inspection/applications?userName=<session>               list
///   GET  .../applications?applicationNo=<consumer>&userName=<cpf>                  detail
///   POST .../upload-survey                                                           multipart
///
/// Parts, in order: data (text/plain), then Insp-<consumer>-setupPhotoWithBen.jpg,
/// -manualHandOverPhoto.jpg, -waterFlowPhoto.jpg (image/jpeg),
/// -benSign.png, -vendorRepSign.png (image/png).
///
/// data.json is written to disk and that file is posted (P5).
/// EXIF GPS + DateTimeOriginal is stamped into each uploaded JPEG copy (P5).
/// Location (P4): live Windows position when available, installation coordinates
/// as the fallback; distance is computed between them and the source is reported.
/// Installer details and the 23 checks feed the same key set the server accepted
/// (P2); the harvester is paused for the duration of the upload (P3).
/// Manifest logged pre-send; gates refuse on missing parts, empty coords,
/// or distance over allowedDistanceMeter when distanceRestricted (P7).
/// Status + full body verbatim on submit; no retry on 2xx (P7/P9).
/// Verify: re-read with userName=cpf_no, print count + updated_dt (P11).
/// </summary>
public sealed class InspectionWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    readonly InspectionWorkflow workflow;

    readonly InspectionRecord record;
    string recordVendorCode;
    readonly string consumerNumber;
    // One key for every book read and write; an empty record code uses the record number.
    string BookVendorCode => string.IsNullOrWhiteSpace(recordVendorCode) ? consumerNumber : recordVendorCode;
    readonly string cpfNo;
    readonly string loginId;
    // A1: there is no session vendor code. The vendor always comes from the
    // record's own vendor_code, and the header and body carry the same value.
    // P3: the harvester is paused while an upload is in flight.
    readonly InspectionHarvester? harvester;
    readonly InspectionStore? store; // W1
    readonly Action<InspectionRecord, SubmitOutcome>? onSubmitComplete;
    readonly Action<InspectionRecord, VerifyOutcome>? onVerifyComplete;
    bool _pickerOpen;
    bool pickerImported;
    Grid? pickerOverlay;
    Border? pickerCard;
    TextBox pickerSearchBox = null!;
    ListBox pickerListBox = null!;
    Button pickerOkButton = null!;
    Button pickerCancelButton = null!;
    Button pickerChooseButton => vendorPersonChoose;
    readonly List<PickerRow> pickerRows = new();
    TextBox pickerAddName = null!;
    TextBox pickerAddMobile = null!;
    TextBlock pickerAddError = null!;
    StackPanel pickerAddForm = null!;
    TextBlock pickerAddLinkText = null!;
    Button pickerUndoLink = null!;
    TextBlock pickerEmptyText = null!;
    TextBlock pickerDeleteError = null!;
    TextBlock pickerSearchHint = null!;
    Button pickerSearchClear = null!;
    DispatcherTimer? pickerStateTimer;
    PickerRow? lastDeletedRow;
    int lastDeletedIndex = -1;
    private sealed record PickerRow(long RowId, string Name, string Mobile);
    // H-A: survey start is held from form open; end is captured at submit.
    readonly DateTimeOffset formOpenTime = DateTimeOffset.UtcNow;

    readonly Dictionary<string, string> photos = new();   // suffix -> stamped upload copy
    readonly Dictionary<string, string> photoSources = new(); // suffix -> original chosen file
    readonly Dictionary<string, string> photoHashes = new(); // suffix -> SHA-256 of stamped file (W1)
    readonly Dictionary<string, string> signs = new();    // suffix -> local png path
    // Z3: signature canvases sit side by side under the third photo slot, 110 px tall.
    readonly InkCanvas beneficiary = new() { Background = Brushes.White, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly InkCanvas representative = new() { Background = Brushes.White, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch };

    readonly TextBlock status = new() { Margin = new Thickness(0, 2, 0, 2) };
    // H4: the banner tells the officer which case they are in — server-returned
    // or opened from the local store. It lives in the fixed footer so it can
    // never scroll away with the tab content.
    readonly TextBlock banner = new()
    {
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAB))
    };
    readonly TextBlock bodyBox = new() { Margin = new Thickness(0, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    // J1: the manifest box sizes itself to its content up to a maximum, then
    // scrolls. Signature parts are muted so "PNG present" reads at a glance.
    readonly StackPanel manifestPanel = new();
    readonly Border manifestBox = new()
    {
        Background = Brushes.White,
        Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(0, 2, 0, 2)
    };
    readonly TextBlock manifestLabel = new() { FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
    readonly ScrollViewer manifestScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = double.NaN, MaxHeight = 190 };
    readonly TextBlock locationBox = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox manualLat = new() { Width = 140, Height = 30, MinHeight = 0, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualLong = new() { Width = 140, Height = 30, MinHeight = 0, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualAcc = new() { Width = 100, Height = 30, MinHeight = 0, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox allowedDist = new() { Width = 100, MinWidth = 90, Height = 30, MinHeight = 0, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
    readonly CheckBox distRestricted = new() { Content = "Distance restricted", Margin = new Thickness(0, 0, 8, 0) };
    // V1: the row is plain strings. Name = read-only TextBox + Choose button;
    // mobile = read-only full-width TextBox. Placeholders behind each.
    readonly TextBox vendorPersonNameBox = new()
    {
        Height = 30, MinHeight = 0, FontSize = 12, IsReadOnly = true, IsReadOnlyCaretVisible = false,
        Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 0, 0, 0),
    };
    readonly Button vendorPersonChoose = new()
    {
        Content = "Choose...", Height = 30, Width = 90, FontSize = 12,
        Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 8, 0)
    };
    readonly TextBox vendorPersonMobileBox = new()
    {
        Height = 30, MinHeight = 0, FontSize = 12, IsReadOnly = true, IsReadOnlyCaretVisible = false,
        Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 0, 0, 0),
    };

    // V1: placeholders sit behind the fields; collapsed when the field has a value.
    readonly TextBlock namePlaceholder = new()
    {
        Text = "Select installer name",
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        FontSize = 12, IsHitTestVisible = false,
        Margin = new Thickness(2, 0, 0, 0)
    };
    readonly TextBlock mobilePlaceholder = new()
    {
        Text = "No number",
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        FontSize = 12, IsHitTestVisible = false,
        Margin = new Thickness(2, 0, 0, 0)
    };
    readonly Button installerClearLink = new()
    {
        Content = "Clear", Margin = new Thickness(0, 2, 8, 0), FontSize = 12,
        Style = (Style)System.Windows.Application.Current.FindResource("LinkButton"),
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    void UpdatePlaceholders()
    {
        namePlaceholder.Visibility = string.IsNullOrWhiteSpace(vendorPersonNameBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        mobilePlaceholder.Visibility = string.IsNullOrWhiteSpace(vendorPersonMobileBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        installerClearLink.Visibility = Visibility.Visible;
        installerClearLink.IsEnabled = true;
        installerClearLink.Opacity = 1;
    }
    readonly Dictionary<string, (RadioButton yes, RadioButton no, TextBox remark)> checkAnswers = new();
    /// <summary>C4: the manual coordinate boxes, hidden behind a link.</summary>
    StackPanel? manualRows;

    TextBlock? riskHeader;
    Border? riskBand;
    ScrollViewer? bodyScroll;
    readonly Mskpy.Infrastructure.VendorPeople vendorPeople = null!;
    TextBlock? installerNote;
    TextBlock? vendorWarning;
    bool locationResolved;
    bool submitBusy;
    Button? submitBtn;
    Button? acknowledgeBtn = null;
    bool isBlockedRecord;
    readonly System.Threading.Timer signatureDebounceTimer;

    static readonly (string Suffix, string Label)[] PhotoSlots =
    [
        ("setupPhotoWithBen", "Setup photo with beneficiary"),
        ("manualHandOverPhoto", "Manual handover photo"),
        ("waterFlowPhoto", "Water flow photo")
    ];
    static readonly (string Suffix, string Label)[] SignSlots =
    [
        ("benSign", "Beneficiary signature"),
        ("vendorRepSign", "Vendor representative signature")
    ];

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    string _vendorPersonName = "";
    public string VendorPersonName
    {
        get => _vendorPersonName;
        set { if (_vendorPersonName != value) { _vendorPersonName = value; OnPropertyChanged(nameof(VendorPersonName)); } }
    }
    string _vendorPersonMobile = "";
    public string VendorPersonMobile
    {
        get => _vendorPersonMobile;
        set { if (_vendorPersonMobile != value) { _vendorPersonMobile = value; OnPropertyChanged(nameof(VendorPersonMobile)); } }
    }

    public InspectionWindow(
        InspectionRecord record,
        string consumerNumber,
        string cpfNo,
        string loginId,
        InspectionWorkflow workflow,

        Window owner,
        InspectionHarvester? harvester = null,
        InspectionStore? store = null,
        Action<InspectionRecord, SubmitOutcome>? onSubmitComplete = null,
        Action<InspectionRecord, VerifyOutcome>? onVerifyComplete = null,
        Mskpy.Infrastructure.VendorPeople? installerBook = null)
    {
        this.record = record;
        this.recordVendorCode = record.VendorCode ?? "";
        this.consumerNumber = consumerNumber;
        this.cpfNo = cpfNo;
        this.loginId = loginId;
        this.workflow = workflow;
        // H1: named installerBook, not vendorPeople - a same-named parameter
        // would shadow this field everywhere below and hand out null.
        this.vendorPeople = installerBook ?? new Mskpy.Infrastructure.VendorPeople();
        // B1/V1: two-way bind the read-only fields to the plain string properties.
        vendorPersonNameBox.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(VendorPersonName)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        vendorPersonMobileBox.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(VendorPersonMobile)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });

        this.harvester = harvester;
        this.store = store; // W1
        this.onSubmitComplete = onSubmitComplete;
        this.onVerifyComplete = onVerifyComplete;
        signatureDebounceTimer = new System.Threading.Timer(_ => CheckSignatureDuplicates(), null, Timeout.Infinite, Timeout.Infinite);

        Title = $"Pump Inspection — {consumerNumber}";
        Width = 1200; MinWidth = 1180; MinHeight = 560;
        // Z7: use the full work area so the Photos tab shows all three photo slots
        // and both signature canvases without scrolling. Still no SizeToContent.
        Height = Math.Min(1000, System.Windows.SystemParameters.WorkArea.Height - 60);
        Background = (Brush)System.Windows.Application.Current.FindResource("Bg");
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Segoe UI");
        // Z7: the window now fills the work area, so centre it on the screen —
        // centring on the owner pushed its footer below the taskbar.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PreviewKeyDown += (_, e) =>
        {
            if (!_pickerOpen || e.Key != Key.Escape) return;
            if (pickerAddForm != null && pickerAddForm.Visibility == Visibility.Visible)
            {
                CollapseAddForm();
            }
            else
            {
                CollapsePickerOverlay();
            }
            e.Handled = true;
        };

        // Z5: the status line uses the accent brush and collapses when empty.
        status.Foreground = (Brush)System.Windows.Application.Current.FindResource("Accent");
        status.Visibility = Visibility.Collapsed;

        // Y2: Grid layout — header row, scrollable tabs row, footer row
        var grid = new Grid { Margin = new Thickness(8), Background = Background };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        // Y1: Read-only header card (record info), Row 0 — two compact lines.
        var headerCard = new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = new Grid()
        };
        var headerGrid = (Grid)headerCard.Child;
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerLeft = new StackPanel { Margin = new Thickness(0) };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        line1.Children.Add(new TextBlock
        {
            Text = $"Name: {FieldValue("consumer_name")}   |   Application No: {FieldValue("consumer_number")}   |   Address: {FieldValue("address").Trim()}",
            FontSize = 12,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerLeft.Children.Add(line1);

        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        var instDate = FieldValue("inst_date");
        var instDateText = string.IsNullOrEmpty(instDate) ? "Not provided" : FormatInstDate(instDate);
        line2.Children.Add(new TextBlock
        {
            Text = $"Inst Date: {instDateText}   |   Inst Latitude: {FieldValue("latitude")}   |   Inst Longitude: {FieldValue("longitude")}",
            FontSize = 11,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerLeft.Children.Add(line2);

        riskHeader = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0)
        };
        riskBand = new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 2, 0, 0),
            Child = riskHeader
        };
        headerLeft.Children.Add(riskBand);

        headerGrid.Children.Add(headerLeft);
        var navBtn = new Button
        {
            Content = "Navigate to Inst Location",
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        navBtn.Click += (_, _) =>
        {
            var lat = FieldValue("latitude");
            var lon = FieldValue("longitude");
            if (!string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.google.com/maps?q={lat},{lon}",
                    UseShellExecute = true
                }); } catch { }
            }
        };
        headerGrid.Children.Add(navBtn);
        Grid.SetColumn(navBtn, 1);

        Grid.SetRow(headerCard, 0);
        Grid.SetColumn(headerCard, 0);
        Grid.SetColumnSpan(headerCard, grid.ColumnDefinitions.Count);

        grid.Children.Add(headerCard);

        // Z6: each tab owns exactly one ScrollViewer. The Photos tab's single
        // scroller is shared by both of its columns.
        var tabControl = new TabControl { Margin = new Thickness(0, 0, 0, 4) };

        // Part A tab — checks only
        var partA = SurveyRules.InspectionChecks.Where(x => x.Part == "A").ToList();
        if (partA.Count > 0)
        {
            var partATab = new TabItem { Header = "Part A", Margin = new Thickness(0) };
            var partAPanel = new StackPanel { Margin = new Thickness(8) };
            foreach (var check in partA)
            {
                var checkPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
                var val = FieldValue(check.OriginalRecordField);
                var valText = string.IsNullOrEmpty(val) ? "Not provided" : val;
                var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
                labelPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ ",
                    FontSize = 13,
                    Foreground = Brushes.Orange,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = check.Label + ": ",
                    FontSize = 13,
                    FontWeight = FontWeights.Normal,
                    // Z1: body-text brush, not the inherited accent foreground.
                    Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = valText,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                checkPanel.Children.Add(labelPanel);
                var radios = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
                var yes = new RadioButton { Content = "Correct", GroupName = check.Field, IsChecked = true };
                var no = new RadioButton { Content = "Incorrect", GroupName = check.Field };
                radios.Children.Add(yes); radios.Children.Add(no);
                checkPanel.Children.Add(radios);
                var remark = new TextBox
                {
                    Width = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxWidth = 760,
                    Margin = new Thickness(0, 0, 0, 0),
                    ToolTip = "Remark (required when Incorrect)",
                    IsEnabled = false
                };
                no.Checked += (_, _) => remark.IsEnabled = true;
                yes.Checked += (_, _) => remark.IsEnabled = false;
                checkPanel.Children.Add(remark);
                checkAnswers[check.Field] = (yes, no, remark);
                partAPanel.Children.Add(checkPanel);
            }
            partATab.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = partAPanel
            };
            tabControl.Items.Add(partATab);
        }

        // Part B tab — checks only
        var partB = SurveyRules.InspectionChecks.Where(x => x.Part == "B").ToList();
        if (partB.Count > 0)
        {
            var partBTab = new TabItem { Header = "Part B", Margin = new Thickness(0) };
            var partBPanel = new StackPanel { Margin = new Thickness(8) };
            foreach (var check in partB)
            {
                var checkPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
                var val = FieldValue(check.OriginalRecordField);
                var valText = string.IsNullOrEmpty(val) ? "Not provided" : val;
                var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
                labelPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ ",
                    FontSize = 13,
                    Foreground = Brushes.Orange,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = check.Label + ": ",
                    FontSize = 13,
                    FontWeight = FontWeights.Normal,
                    // Z1: body-text brush, not the inherited accent foreground.
                    Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = valText,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                checkPanel.Children.Add(labelPanel);
                var radios = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
                var yes = new RadioButton { Content = "Correct", GroupName = check.Field, IsChecked = true };
                var no = new RadioButton { Content = "Incorrect", GroupName = check.Field };
                radios.Children.Add(yes); radios.Children.Add(no);
                checkPanel.Children.Add(radios);
                var remark = new TextBox
                {
                    Width = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxWidth = 760,
                    Margin = new Thickness(0, 0, 0, 0),
                    ToolTip = "Remark (required when Incorrect)",
                    IsEnabled = false
                };
                no.Checked += (_, _) => remark.IsEnabled = true;
                yes.Checked += (_, _) => remark.IsEnabled = false;
                checkPanel.Children.Add(remark);
                checkAnswers[check.Field] = (yes, no, remark);
                partBPanel.Children.Add(checkPanel);
            }
            partBTab.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = partBPanel
            };
            tabControl.Items.Add(partBTab);
        }

                // Y4: Photos & Signatures tab — body grid with left/right columns.
        var photosTab = new TabItem { Header = "Photos & Signatures", Margin = new Thickness(0) };
        var bodyGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        bodyGrid.ColumnDefinitions.Clear();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        // Left column: photos row, then signatures in their own row below it.
        var leftCol = new Grid { Margin = new Thickness(0, 0, 6, 0) };
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rightCol = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };

        var photosLabel = SectionLabel("Photos");
        Grid.SetRow(photosLabel, 0);
        leftCol.Children.Add(photosLabel);

        var photoRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < PhotoSlots.Length; i++)
        {
            var slotCard = BuildPhotoSlot(PhotoSlots[i].Suffix, PhotoSlots[i].Label);
            slotCard.Margin = new Thickness(0, 0, 8, 0);
            slotCard.VerticalAlignment = VerticalAlignment.Top;
            photoRow.Children.Add(slotCard);
        }
        Grid.SetRow(photoRow, 1);
        leftCol.Children.Add(photoRow);

        var signaturesLabel = SectionLabel("Signatures", new Thickness(0, 8, 0, 4));
        Grid.SetRow(signaturesLabel, 2);
        leftCol.Children.Add(signaturesLabel);

        // The signature preview canvas is 90 px; the grid sizes to its label + preview + state.
        var signGrid = new Grid { Margin = new Thickness(0), Height = double.NaN };
        signGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        signGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var benBlock = BuildSignatureBlock("Beneficiary signature", beneficiary);
        var repBlock = BuildSignatureBlock("Vendor representative signature", representative);
        Grid.SetColumn(benBlock, 0);
        benBlock.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(repBlock, 1);
        repBlock.Margin = new Thickness(6, 0, 0, 0);
        signGrid.Children.Add(benBlock);
        signGrid.Children.Add(repBlock);
        Grid.SetRow(signGrid, 3);
        leftCol.Children.Add(signGrid);

        Grid.SetColumn(leftCol, 0);
        bodyGrid.Children.Add(leftCol);

        // Right: installer card
        var installerStack = new StackPanel();
        VendorPersonName = "";
        VendorPersonMobile = "";
        PrefillInstallerPair();
        System.Windows.Automation.AutomationProperties.SetName(vendorPersonNameBox, "Vendor Person Name");
        System.Windows.Automation.AutomationProperties.SetName(vendorPersonMobileBox, "Vendor Person Mobile No");

        if (vendorPeople.LoadWarning != null) LogStartupNote(vendorPeople.LoadWarning);

        var installerHeader = new Grid();
        installerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        installerHeader.Children.Add(new TextBlock { Text = "Installer Details", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        installerStack.Children.Add(installerHeader);

        installerStack.Children.Add(FieldLabel("Vendor Person Name"));
        // Row 3: TextBox + Choose
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        // WPF Grid has no ColumnSpacing property; an 8 px right margin on the
        // star column gives the same 8 px gutter before the fixed button.
        nameRow.Margin = new Thickness(0);

        vendorPersonNameBox.Height = 30; vendorPersonNameBox.MinHeight = 0; vendorPersonNameBox.Padding = new Thickness(6, 0, 6, 0); vendorPersonNameBox.Margin = new Thickness(0, 0, 8, 0);
        vendorPersonNameBox.TextWrapping = TextWrapping.NoWrap; vendorPersonNameBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        vendorPersonChoose.Content = "Choose…"; vendorPersonChoose.Height = 30; vendorPersonChoose.Width = 96; vendorPersonChoose.Margin = new Thickness(0); vendorPersonChoose.HorizontalAlignment = HorizontalAlignment.Stretch; vendorPersonChoose.VerticalAlignment = VerticalAlignment.Center;
        vendorPersonChoose.Style = (Style)System.Windows.Application.Current.FindResource("LinkButton");
        // ghost style via manual
        vendorPersonChoose.Background = Brushes.Transparent; vendorPersonChoose.BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"); vendorPersonChoose.BorderThickness = new Thickness(1); vendorPersonChoose.Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"); vendorPersonChoose.Opacity = 1;
        nameRow.Children.Add(vendorPersonNameBox);
        nameRow.Children.Add(vendorPersonChoose);
        Grid.SetColumn(vendorPersonChoose, 1);
        installerStack.Children.Add(nameRow);

        installerStack.Children.Add(FieldLabel("Vendor Person Mobile No"));
        var mobileRow = new Grid();
        mobileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mobileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        // Same 8 px gutter as the name row, without an extra Grid column.
        mobileRow.Margin = new Thickness(0);

        vendorPersonMobileBox.Height = 30; vendorPersonMobileBox.MinHeight = 0; vendorPersonMobileBox.Padding = new Thickness(6, 0, 6, 0); vendorPersonMobileBox.Margin = new Thickness(0, 0, 8, 0);
        vendorPersonMobileBox.TextWrapping = TextWrapping.NoWrap; vendorPersonMobileBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        installerClearLink.Content = "Clear"; installerClearLink.Height = 30; installerClearLink.Width = 72; installerClearLink.Margin = new Thickness(0); installerClearLink.HorizontalAlignment = HorizontalAlignment.Stretch; installerClearLink.VerticalAlignment = VerticalAlignment.Center;
        installerClearLink.Style = null;
        installerClearLink.Background = Brushes.Transparent; installerClearLink.BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"); installerClearLink.BorderThickness = new Thickness(1); installerClearLink.Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"); installerClearLink.Opacity = 1;
        mobileRow.Children.Add(vendorPersonMobileBox);
        mobileRow.Children.Add(installerClearLink);
        Grid.SetColumn(installerClearLink, 1);
        installerStack.Children.Add(mobileRow);

        System.Windows.Automation.AutomationProperties.SetName(vendorPersonChoose, "Choose...");
        System.Windows.Automation.AutomationProperties.SetName(installerClearLink, "Clear");
        installerClearLink.Click += (_, _) => DoClear();
        vendorPersonChoose.Click += (_, _) =>
        {
            if (_pickerOpen) return;
            _pickerOpen = true;
            pickerSearchBox.Text = string.Empty;
            LoadPickerRows();
            LogPickerState();
            if (pickerOverlay != null) pickerOverlay.Visibility = Visibility.Visible;
            SchedulePickerStateLog();
            Log($"overlay shown: children={(pickerOverlay != null ? pickerOverlay.Children.Count : 0)} " +
                $"w={(pickerOverlay != null ? pickerOverlay.ActualWidth : 0)} " +
                $"h={(pickerOverlay != null ? pickerOverlay.ActualHeight : 0)} " +
                $"list={pickerListBox.Items.Count}");
            pickerChooseButton.Focus();
        };
        UpdatePlaceholders();
        installerNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        installerStack.Children.Add(installerNote);
        vendorWarning = new TextBlock { Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed };
        installerStack.Children.Add(vendorWarning);

        var installerCard = new Border { Background = (Brush)System.Windows.Application.Current.FindResource("Surface"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 8, 12, 8), BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"), BorderThickness = new Thickness(1), Child = installerStack, Margin = new Thickness(0, 0, 0, 0) };
        rightCol.Children.Add(installerCard);

        // Right: location card
        var locationCard = new StackPanel();
        locationCard.Children.Add(new TextBlock { Text = "Location", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        manualRows = new StackPanel { Visibility = Visibility.Collapsed };
        manualLat.TextChanged += (_, _) => UpdateSubmitEnabled();
        manualLong.TextChanged += (_, _) => UpdateSubmitEnabled();
        manualAcc.TextChanged += (_, _) => UpdateSubmitEnabled();
        var clearLat = new Button { Content = "✕", Height = 24, Padding = new Thickness(4,0,4,0) }; clearLat.Click += (_,_) => manualLat.Text="";
        var clearLong = new Button { Content = "✕", Height = 24, Padding = new Thickness(4,0,4,0) }; clearLong.Click += (_,_) => manualLong.Text="";
        var clearAcc = new Button { Content = "✕", Height = 24, Padding = new Thickness(4,0,4,0) }; clearAcc.Click += (_,_) => manualAcc.Text="";
        manualRows.Children.Add(CreateFieldRow("Manual latitude", manualLat, clearLat));
        manualRows.Children.Add(CreateFieldRow("Manual longitude", manualLong, clearLong));
        manualRows.Children.Add(CreateFieldRow("Manual accuracy (m)", manualAcc, clearAcc));
        locationCard.Children.Add(manualRows);
        var setManually = new Button { Content = "set manually", Style = (Style)System.Windows.Application.Current.FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(setManually, "set manually");
        setManually.Click += (_, _) => { manualRows.Visibility = manualRows.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; setManually.Content = manualRows.Visibility == Visibility.Visible ? "hide the manual boxes" : "set manually"; };
        locationCard.Children.Add(setManually);
        var locationActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        var resolveBtn = new Button { Content = "Use current location", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
        resolveBtn.Click += async (_, _) => await ResolveLocation();
        locationActions.Children.Add(resolveBtn);
        locationActions.Children.Add(distRestricted);
        locationActions.Children.Add(new TextBlock { Text = "Allowed distance (m): ", Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,4,0), FontSize = 12 });
        locationActions.Children.Add(allowedDist);
        locationCard.Children.Add(locationActions);
        locationBox.TextWrapping = TextWrapping.Wrap; locationBox.Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"); locationBox.FontSize = 12;
        locationBox.Text = "Not resolved yet.";
        locationCard.Children.Add(locationBox);
        _ = ResolveLocation();
        var locationCardBorder = new Border { Background = (Brush)System.Windows.Application.Current.FindResource("Surface"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"), BorderThickness = new Thickness(1), Child = locationCard, Margin = new Thickness(0, 0, 0, 0) };
        rightCol.Children.Add(locationCardBorder);

        // spacer
        rightCol.Children.Add(new Border { Height = 1, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch });

        Grid.SetColumn(rightCol, 1);
        bodyGrid.Children.Add(rightCol);

        photosTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = bodyGrid
        };
        tabControl.Items.Add(photosTab);

        // Y2: Footer row — one row, warnings above.
        var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        var footerBorder = new Border { BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0) };
        var footerRow = new Grid();
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        submitBtn = new Button { Content = "Submit Survey", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(12, 6, 12, 6), IsEnabled = false, FontSize = 12 };
        submitBtn.Click += async (_, _) => await Submit();
        buttons.Children.Add(submitBtn);
        var verifyBtn = new Button { Content = "Verify (re-read)", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(12, 6, 12, 6), FontSize = 12 };
        verifyBtn.Click += async (_, _) => await Verify();
        buttons.Children.Add(verifyBtn);
        var closeBtn = new Button { Content = "Close", Padding = new Thickness(12, 6, 12, 6), FontSize = 12, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x36, 0x46)), Foreground = (Brush)System.Windows.Application.Current.FindResource("Text") };
        closeBtn.Click += (_, _) => Close();
        buttons.Children.Add(closeBtn);
        footerRow.Children.Add(buttons);
        status.FontSize = 11; status.Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"); status.TextTrimming = TextTrimming.CharacterEllipsis; status.Margin = new Thickness(12, 0, 0, 0);
        footerRow.Children.Add(status);
        Grid.SetColumn(status, 1);
        footerBorder.Child = footerRow;
        footer.Children.Add(footerBorder);

        // body box hidden until submit/verify
        bodyBox.FontFamily = new FontFamily("Consolas"); bodyBox.FontSize = 11; bodyBox.Background = Brushes.White; bodyBox.Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F)); bodyBox.Padding = new Thickness(8); bodyBox.Text = "Awaiting submission…"; bodyBox.TextWrapping = TextWrapping.Wrap;
        bodyScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 90, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 0) };
        bodyScroll.Content = bodyBox;
        footer.Children.Add(bodyScroll);

        Grid.SetRow(tabControl, 1);
        Grid.SetColumn(tabControl, 0);
        Grid.SetColumnSpan(tabControl, grid.ColumnDefinitions.Count);
        grid.Children.Add(tabControl);
        Grid.SetRow(footer, 2);
        Grid.SetColumn(footer, 0);
        Grid.SetColumnSpan(footer, grid.ColumnDefinitions.Count);
        grid.Children.Add(footer);

        // Picker overlay: search, inline Add form, list, then action row.
        var pickerOverlayControl = new Grid { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)) };
        Grid.SetZIndex(pickerOverlayControl, 100);
        Grid.SetRowSpan(pickerOverlayControl, grid.RowDefinitions.Count);
        Grid.SetColumn(pickerOverlayControl, 0);
        Grid.SetColumnSpan(pickerOverlayControl, grid.ColumnDefinitions.Count);
        pickerOverlayControl.Visibility = Visibility.Collapsed;
        var scrim = new System.Windows.Shapes.Rectangle { Fill = Brushes.Transparent };
        scrim.MouseLeftButtonDown += (_, __) => CollapsePickerOverlay();
        pickerOverlayControl.Children.Add(scrim);

        var card = new Border
        {
            Width = 420,
            Height = double.NaN,
            MaxHeight = Math.Max(320, Height - 120),
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(40, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        pickerCard = card;
        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search Auto
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // add Auto (link + form)
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list *
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer Auto

        var overlayHeaderGrid = new Grid();
        overlayHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlayHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        overlayHeaderGrid.Children.Add(new TextBlock { Text = "Select installer", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        var closeX = new Button { Content = "✕", Width = 20, Height = 20, MinHeight = 0, FontSize = 12, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)System.Windows.Application.Current.FindResource("Text") };
        closeX.Click += (_, __) => CollapsePickerOverlay();
        overlayHeaderGrid.Children.Add(closeX);
        Grid.SetColumn(closeX, 1);
        cardGrid.Children.Add(overlayHeaderGrid);

        pickerSearchBox = new TextBox
        {
            Height = 30,
            MinHeight = 0,
            FontSize = 12,
            Padding = new Thickness(6, 2, 26, 2),
            Margin = new Thickness(0),
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1)
        };
        var searchHost = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        searchHost.Children.Add(pickerSearchBox);
        pickerSearchHint = new TextBlock
        {
            Text = "Search name or number",
            FontSize = 12,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            Margin = new Thickness(8, 0, 28, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        searchHost.Children.Add(pickerSearchHint);
        pickerSearchClear = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            MinHeight = 0,
            FontSize = 10,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Clear search"
        };
        pickerSearchClear.Click += (_, __) =>
        {
            pickerSearchBox.Text = string.Empty;
            pickerSearchBox.Focus();
        };
        searchHost.Children.Add(pickerSearchClear);
        pickerSearchBox.GotFocus += (_, __) => pickerSearchHint.Visibility = Visibility.Collapsed;
        pickerSearchBox.LostFocus += (_, __) =>
            pickerSearchHint.Visibility = string.IsNullOrEmpty(pickerSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        pickerSearchBox.TextChanged += (_, __) =>
            pickerSearchHint.Visibility = string.IsNullOrEmpty(pickerSearchBox.Text) && !pickerSearchBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
        Grid.SetRow(searchHost, 1);
        cardGrid.Children.Add(searchHost);

        pickerAddLinkText = new TextBlock { Text = "Add new installer", FontSize = 12, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text") };
        var addLink = new Button
        {
            Content = pickerAddLinkText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            Height = 18,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4)
        };
        addLink.MouseEnter += (_, __) => pickerAddLinkText.TextDecorations = TextDecorations.Underline;
        addLink.MouseLeave += (_, __) => pickerAddLinkText.TextDecorations = null;
        addLink.Click += (_, __) =>
        {
            if (pickerAddForm.Visibility == Visibility.Visible) { CollapseAddForm(); return; }
            pickerAddForm.Visibility = Visibility.Visible;
            pickerAddName.Focus();
        };
        var addRow = new StackPanel { Margin = new Thickness(0) };
        addRow.Children.Add(addLink);
        Grid.SetRow(addRow, 2);
        cardGrid.Children.Add(addRow);

        pickerAddForm = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        var nameField = PickerInputField("Name", 0);
        pickerAddName = nameField.Box;
        pickerAddForm.Children.Add(nameField.Host);
        var mobileField = PickerInputField("Mobile (10 digits)", 10);
        pickerAddMobile = mobileField.Box;
        pickerAddMobile.PreviewTextInput += (_, e) =>
        {
            if ((e.Text ?? "").Any(ch => !char.IsDigit(ch))) e.Handled = true;
        };
        pickerAddForm.Children.Add(mobileField.Host);
        var addActionRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        addActionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        addActionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var addButton = new Button { Content = "Add", Width = 80, Height = 30, MinHeight = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        addButton.Click += (_, __) => DoPickerAdd();
        addActionRow.Children.Add(addButton);
        var cancelAddText = new TextBlock { Text = "Cancel add", FontSize = 12, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text") };
        var cancelAdd = new Button
        {
            Content = cancelAddText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 0, 0),
            MinHeight = 0,
            Height = 18,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        cancelAdd.MouseEnter += (_, __) => cancelAddText.TextDecorations = TextDecorations.Underline;
        cancelAdd.MouseLeave += (_, __) => cancelAddText.TextDecorations = null;
        cancelAdd.Click += (_, __) => CollapseAddForm();
        Grid.SetColumn(cancelAdd, 1);
        addActionRow.Children.Add(cancelAdd);
        pickerAddForm.Children.Add(addActionRow);
        pickerAddError = new TextBlock { Foreground = Brushes.DarkRed, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        pickerAddForm.Children.Add(pickerAddError);
        addRow.Children.Add(pickerAddForm);

        pickerListBox = new ListBox
        {
            MinHeight = 180,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6),
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(pickerListBox, ScrollBarVisibility.Auto);
        pickerListBox.SelectionChanged += (_, _) => pickerOkButton.IsEnabled = SelectedPickerRow() != null;
        pickerListBox.MouseDoubleClick += (_, _) => { if (SelectedPickerRow() != null) AcceptPickerSelection(); };
        pickerEmptyText = new TextBlock
        {
            Text = "No installers for this vendor — use Add new installer",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16)
        };
        var listHost = new Grid();
        listHost.Children.Add(pickerListBox);
        listHost.Children.Add(pickerEmptyText);
        Grid.SetRow(listHost, 3);
        cardGrid.Children.Add(listHost);

        var btnRow = new Grid();
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickerUndoLink = new Button { Content = "", Visibility = Visibility.Collapsed, FontSize = 12, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
        pickerUndoLink.Click += (_, __) => UndoPickerDelete();
        btnRow.Children.Add(pickerUndoLink);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        pickerCancelButton = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6), MinHeight = 0, Height = 30, FontSize = 12 };
        pickerCancelButton.Click += (_, __) => CollapsePickerOverlay();
        pickerOkButton = new Button { Content = "OK", Padding = new Thickness(12, 6, 12, 6), MinHeight = 0, Height = 30, FontSize = 12, IsEnabled = false };
        pickerOkButton.Click += (_, __) => AcceptPickerSelection();
        btns.Children.Add(pickerCancelButton);
        btns.Children.Add(pickerOkButton);
        btnRow.Children.Add(btns);
        Grid.SetColumn(btns, 1);
        pickerDeleteError = new TextBlock
        {
            Foreground = Brushes.DarkRed,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var footerStack = new StackPanel();
        footerStack.Children.Add(pickerDeleteError);
        footerStack.Children.Add(btnRow);
        Grid.SetRow(footerStack, 4);
        cardGrid.Children.Add(footerStack);

        card.Child = cardGrid;
        pickerOverlayControl.Children.Add(card);
        grid.Children.Add(pickerOverlayControl);
        this.pickerOverlay = pickerOverlayControl;

        pickerSearchBox.TextChanged += (_, __) => ApplyPickerFilter();

        UpdateRisk();

        Content = grid;
        allowedDist.Text = "";
    }

    /// <summary>
    /// Z5: one status line — collapses when empty, accent brush when populated.
    /// Safe to call from any thread: the debounce timer and the harvester both
    /// report from background threads.
    /// </summary>
    /// <summary>
    /// H4: shown when the record was opened from the local store because the
    /// server did not return it. A submit on such a record may be rejected.
    /// </summary>
    public void ShowLocalStoreBanner()
    {
        banner.Text = "Opened from the local store. The server did not return this record; submit may be rejected.";
        banner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// J1: renders every part of the pre-send manifest. The box grows with its
    /// content up to the ceiling and then scrolls; with no parts it collapses
    /// to nothing. Signature parts are muted and carry their byte size.
    /// </summary>
    void SetManifest(IReadOnlyList<InspectionMultipartBuilder.ManifestEntry> parts, string? location = null)
    {
        manifestPanel.Children.Clear();
        var muted = (Brush)System.Windows.Application.Current.FindResource("Muted");
        var body = new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F));
        foreach (var part in parts)
        {
            var isSignature = part.PartName.EndsWith("-benSign.png", StringComparison.OrdinalIgnoreCase)
                            || part.PartName.EndsWith("-vendorRepSign.png", StringComparison.OrdinalIgnoreCase);
            manifestPanel.Children.Add(new TextBlock
            {
                // The data part's size is only known once the blob is built.
                Text = part.Size > 0
                    ? $"{part.PartName}  ({part.ContentType}, {part.Size} B)"
                    : $"{part.PartName}  ({part.ContentType}, size set at submit)",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = isSignature ? muted : body,
                Margin = new Thickness(0, 1, 0, 1),
                TextWrapping = TextWrapping.NoWrap
            });
        }
        if (!string.IsNullOrWhiteSpace(location))
        {
            manifestPanel.Children.Add(new TextBlock
            {
                Text = location!,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = muted,
                Margin = new Thickness(0, 4, 0, 1),
                TextWrapping = TextWrapping.Wrap
            });
        }
        // Nothing to show: collapse the label and the box entirely.
        var visible = parts.Count > 0;
        manifestLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        manifestScroll.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetStatus(string? text)
    {
        void Apply()
        {
            status.Text = text ?? "";
            status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(new Action(Apply));
    }

    string FieldValue(string key) =>
        record.Raw.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    static bool IsNumber(string? text) =>
        text == null || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Manual override first, then the record coordinate, then 0.</summary>
    double ResolveCoordinate(string recordKey, string manualText)
    {
        if (double.TryParse(manualText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        if (double.TryParse(FieldValue(recordKey), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return value;
        return 0d;
    }

    /// <summary>Distance in metres from a position to the record's installation coordinates.</summary>
    double? DistanceToInstall(string? lat, string? lon)
    {
        if (double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var usedLat) &&
            double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var usedLong) &&
            double.TryParse(FieldValue("latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var instLat) &&
            double.TryParse(FieldValue("longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var instLong))
            return GeoDistance.Meters(usedLat, usedLong, instLat, instLong);
        return null;
    }

    /// <summary>
    /// C3: the manifest line names the source and the real jitter, so the
    /// officer can see that the position is the customer's site and how far
    /// off the recorded installation the submitted pair sits.
    /// </summary>
    static string DescribeLocation(LocationResult loc, double? distance)
    {
        var source = loc.Source == LocationSource.SiteCoords
            ? $"SiteCoords (installation ±{(loc.JitterMetres.HasValue ? loc.JitterMetres.Value.ToString("0", CultureInfo.InvariantCulture) : "0")} m)"
            : loc.Source == LocationSource.None
                ? "Manual"
                : loc.Source.ToString();
        return $"Source: {source}  ·  Lat={loc.Lat}  ·  Long={loc.Long}  ·  " +
            $"Accuracy={(string.IsNullOrEmpty(loc.Accuracy) ? "0" : loc.Accuracy)}  ·  Mocked={loc.Mocked}  ·  " +
            $"Distance from installation={(distance.HasValue ? Math.Round(distance.Value).ToString("0", CultureInfo.InvariantCulture) : "n/a")} m  ·  " +
            (loc.Error != null ? "Error: " + loc.Error : "OK");
    }

    async Task ResolveLocation()
    {
        SetStatus("Resolving location…");
        var loc = await workflow.location.ResolveAsync(
            Empty(manualLat.Text), Empty(manualLong.Text), Empty(manualAcc.Text),
            record.Latitude, record.Longitude, CancellationToken.None);
        locationBox.Text = DescribeLocation(loc, DistanceToInstall(loc.Lat, loc.Long));
        locationResolved = loc.HasCoords;
        UpdateSubmitEnabled();
        SetStatus("Location resolved: " + loc.Source);
    }

    /// <summary>
    /// D6: Submit stays disabled until a position is resolved - either the
    /// record's own coordinates (SiteCoords) or coordinates typed by hand.
    /// </summary>
    /// <summary>
    /// M2: gather the signals that exist and paint the band + reasons in the
    /// header card and the warnings in the fixed footer.
    /// </summary>
    void UpdateRisk()
    {
        var stored = store?.GetByConsumerNumber(consumerNumber);
        var category = stored?.ReasonCategory ?? ReasonCategoriser.Categorise(stored?.ReasonToRevert);
        var returnedCount = stored?.Status == RecordStatus.Returned ? 1 : 0;

        var installerBlank = string.IsNullOrWhiteSpace(VendorPersonName)
            || string.IsNullOrWhiteSpace(VendorPersonMobile);

        var allDefaultNoRemark = checkAnswers.Count > 0
            && checkAnswers.Values.All(c => string.IsNullOrWhiteSpace(c.remark.Text));

        var accuracyOutlier = false;
        if (double.TryParse(manualAcc.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var acc))
            accuracyOutlier = acc < 4 || acc > 12;

        var input = new ReturnRiskScorer.RiskInput(
            ReturnedBeforeSameReasonUnchanged: false,
            UnfixableCategory: category == ReasonCategoriser.Unfixable,
            ReturnedCount: returnedCount,
            SmallOrNearIdenticalPhotos: false,
            InstallerBlank: installerBlank,
            InstallerCopied: false,
            SignaturesIdentical: false,
            AllChecksDefaultNoRemark: allDefaultNoRemark,
            DistanceOutlier: false,
            AccuracyOutlier: accuracyOutlier);

        var result = ReturnRiskScorer.Score(input);
        var label = ReturnRiskScorer.BandLabel(result.Band);
        var blocked = stored?.Status == RecordStatus.Blocked;
        isBlockedRecord = blocked;

        var reasons = result.Reasons.Take(2).ToArray();
        var reasonText = reasons.Length > 0 ? " - " + string.Join("; ", reasons) : "";

        if (riskHeader != null)
        {
            if (blocked)
            {
                riskHeader.Text = "Risk: blocked — " + (stored?.BlockedReason ?? "waiting on SDO");
                riskHeader.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAB));
                if (riskBand != null) riskBand.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x2A, 0x2E));
            }
            else
            {
                riskHeader.Text = reasons.Length > 0
                    ? $"Risk: {ReturnRiskScorer.BandLabel(result.Band).ToLowerInvariant()} — {string.Join("; ", reasons)}"
                    : $"Risk: {ReturnRiskScorer.BandLabel(result.Band).ToLowerInvariant()}";
                riskHeader.Foreground = result.Band == ReturnRiskScorer.Band.LikelyReturned
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAB))
                    : result.Band == ReturnRiskScorer.Band.Risky
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x80))
                        : (Brush)System.Windows.Application.Current.FindResource("Muted");
                if (riskBand != null)
                    riskBand.Background = result.Band == ReturnRiskScorer.Band.LikelyReturned
                        ? new SolidColorBrush(Color.FromRgb(0x4A, 0x2A, 0x2E))
                        : result.Band == ReturnRiskScorer.Band.Risky
                            ? new SolidColorBrush(Color.FromRgb(0x4A, 0x3E, 0x28))
                            : (Brush)System.Windows.Application.Current.FindResource("Surface");
            }
        }
        if (acknowledgeBtn != null)
        {
            acknowledgeBtn.Visibility = Visibility.Collapsed;
        }

        UpdateSubmitEnabled();
    }

    void UpdateSubmitEnabled()
    {
        if (submitBtn == null) return;
        var manualOk = !string.IsNullOrWhiteSpace(manualLat.Text)
            && !string.IsNullOrWhiteSpace(manualLong.Text)
            && double.TryParse(manualLat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && double.TryParse(manualLong.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        submitBtn.IsEnabled = (locationResolved || manualOk) && !isBlockedRecord;
    }

    /// <summary>
    /// J2: the signature clear button — a small round muted disc with an
    /// uppercase X, docked in the top-right corner of the ink canvas. It is
    /// deliberately small and in the corner so it cannot eat a stroke tap.
    /// </summary>
    static Button MakeClearButton(string automationName, Brush muted, Brush hover, Action onClick)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.NameProperty, "bg");
        border.SetValue(Border.BackgroundProperty, muted);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.WidthProperty, 22.0);
        border.SetValue(Border.HeightProperty, 22.0);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var button = new Button
        {
            Template = template,
            Content = "X",
            Width = 22,
            Height = 22,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            // Dark glyph on the muted disc — white on a light muted disc is unreadable.
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = automationName,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 3, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        button.MouseEnter += (_, _) => { if (button.Template.FindName("bg", button) is Border b) b.Background = hover; };
        button.MouseLeave += (_, _) => { if (button.Template.FindName("bg", button) is Border b) b.Background = muted; };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Z2: a compact button. The app-wide Button style is 44 px tall, which pushed
    /// the third photo slot and both signature canvases below the fold.
    /// </summary>
    static Button CompactButton(string content, string? toolTip = null) => new()
    {
        Content = content,
        Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(12, 4, 12, 4),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = toolTip
    };

    /// <summary>
    /// J1: the pre-send manifest, rebuilt as the officer attaches. It lists the
    /// data part and every photo and signature that is present, with sizes, so
    /// the officer can see what is about to go on the wire.
    /// </summary>
    void RefreshManifestPreview()
    {
        var parts = new List<InspectionMultipartBuilder.ManifestEntry>
        {
            new() { PartName = "data", ContentType = "text/plain", Size = 0, Source = "data.json (size set at submit)" }
        };
        foreach (var slot in PhotoSlots)
        {
            if (!photos.TryGetValue(slot.Suffix, out var path) || !File.Exists(path)) continue;
            parts.Add(new InspectionMultipartBuilder.ManifestEntry
            {
                PartName = $"Insp-{consumerNumber}-{slot.Suffix}.jpg",
                ContentType = "image/jpeg",
                Size = new FileInfo(path).Length,
                Source = path
            });
        }
        foreach (var slot in SignSlots)
        {
            if (!signs.TryGetValue(slot.Suffix, out var path) || !File.Exists(path)) continue;
            parts.Add(new InspectionMultipartBuilder.ManifestEntry
            {
                PartName = $"Insp-{consumerNumber}-{slot.Suffix}.png",
                ContentType = "image/png",
                Size = new FileInfo(path).Length,
                Source = path
            });
        }
        SetManifest(parts);
    }

    /// <summary>
    /// J1: renders a canvas to a PNG at a stable path so the pre-send manifest
    /// can show the signature part and its size before the submit.
    /// </summary>
    void RefreshSignaturePreviews()
    {
        foreach (var (suffix, canvas) in new[] { ("benSign", beneficiary), ("vendorRepSign", representative) })
        {
            if (canvas.Strokes.Count == 0) { signs.Remove(suffix); continue; }
            // L3: the preview lives with the app's own data, not in the
            // system temp folder.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop", "photos");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"insp-sign-preview-{consumerNumber}-{suffix}.png");
            if (RenderCanvasToPng(canvas, path) != null) signs[suffix] = path;
        }
        RefreshManifestPreview();
    }

    Border BuildPhotoSlot(string suffix, string label)
    {
        Border card = null!;
        var stack = new StackPanel { Margin = new Thickness(0), Height = double.NaN };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) });

        var image = new Image { Stretch = Stretch.Uniform };
        var placeholder = new TextBlock { Text = "🖼", FontSize = 28, Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewHost = new Grid();
        previewHost.Children.Add(image);
        previewHost.Children.Add(placeholder);
        var thumbFrame = new Border
        {
            Height = 120,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F)),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = previewHost,
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(thumbFrame);

        var btnRow = new Grid();
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        btnRow.Margin = new Thickness(0, 0, 0, 6);
        var choose = new Button { Content = "Choose JPEG…", Height = 26, FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        System.Windows.Automation.AutomationProperties.SetName(choose, "Choose JPEG for " + label);
        var remove = new Button { Content = "Remove", Height = 26, FontSize = 12, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false, Opacity = 0.45 };
        System.Windows.Automation.AutomationProperties.SetName(remove, "Remove " + label);
        btnRow.Children.Add(choose);
        btnRow.Children.Add(remove);
        Grid.SetColumn(remove, 1);
        stack.Children.Add(btnRow);

        var state = new TextBlock { Text = "No photo selected", FontSize = 11, Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 0) };
        stack.Children.Add(state);

        choose.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.tif;*.tiff",
                Title = label,
                Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var path = picker.FileName;
                ValidatePhotoSource(path);
                Directory.CreateDirectory(PhotoSlotDirectory);
                var sha = ComputeSha1(path);
                var ext = Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                var slotPath = SlotPhotoPath(suffix, sha, ext);
                if (photos.TryGetValue(suffix, out var previous) && !string.Equals(previous, slotPath, StringComparison.OrdinalIgnoreCase))
                    DeleteSlotFile(previous);

                var produced = JpegExifStamper.Stamp(path, ResolveCoordinate("latitude", manualLat.Text), ResolveCoordinate("longitude", manualLong.Text), DateTime.UtcNow, slotPath, LogStartupNote);
                photos[suffix] = produced;
                photoSources[suffix] = path;
                try { photoHashes[suffix] = ComputeSha256(path); } catch { }

                try
                {
                    image.Source = Thumbnail(produced);
                    placeholder.Visibility = Visibility.Collapsed;
                    try
                    {
                        using var fs = File.OpenRead(produced);
                        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        var fr = dec.Frames[0];
                        state.Text = Path.GetFileName(produced) + "  (" + fr.PixelWidth + "x" + fr.PixelHeight + ")";
                    }
                    catch { state.Text = Path.GetFileName(produced) + "  (attached)"; }
                }
                catch (Exception ex)
                {
                    image.Source = null;
                    placeholder.Visibility = Visibility.Visible;
                    state.Text = "Preview unavailable — " + Path.GetFileName(produced);
                    SetStatus("Preview unavailable: " + ex.Message);
                }
                remove.IsEnabled = true;
                remove.Opacity = 1;
                RefreshManifestPreview();
            }
            catch (Exception ex)
            {
                SetStatus("Photo could not be loaded: " + ex.Message);
            }
        };
        remove.Click += (_, _) =>
        {
            if (photos.TryGetValue(suffix, out var slotFile)) DeleteSlotFile(slotFile);
            photos.Remove(suffix);
            photoSources.Remove(suffix);
            photoHashes.Remove(suffix);
            image.Source = null;
            placeholder.Visibility = Visibility.Visible;
            state.Text = "No photo selected";
            remove.IsEnabled = false;
            remove.Opacity = 0.45;
            RefreshManifestPreview();
        };

        card = new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1),
            Child = stack,
            Margin = new Thickness(0, 0, 0, 0),
            Height = double.NaN,
            VerticalAlignment = VerticalAlignment.Top
        };
        return card;
    }

    /// <summary>P2: where this consumer's slot files live.</summary>
    string PhotoSlotDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenewableEnergyDesktop", "inspection-data", consumerNumber);

    /// <summary>P2: att-<field>-<sha1(source bytes)[0:10]>.jpg.</summary>
    string SlotPhotoPath(string suffix, string sha1, string ext)
    {
        var stamp = sha1.Length > 10 ? sha1[..10] : sha1;
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        if (!ext.StartsWith(".")) ext = "." + ext;
        return Path.Combine(PhotoSlotDirectory, $"att-{suffix}-{stamp}{ext}");
    }

    /// <summary>P2: true when this is one of the app's own slot files.</summary>
    bool IsSlotFile(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("att-", StringComparison.OrdinalIgnoreCase)) return false;
            var dir = Path.GetFullPath(Path.GetDirectoryName(path) ?? "");
            return string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(PhotoSlotDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>P2: delete only a slot-owned file. The source is never touched.</summary>
    void DeleteSlotFile(string path)
    {
        try
        {
            if (IsSlotFile(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogStartupNote($"Slot file '{Path.GetFileName(path)}' could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// P1: the picked file must exist, be non-empty, be a real JPEG and decode.
    /// Everything else is a named message, never a null preview.
    /// </summary>
    static void ValidatePhotoSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("no file was selected.");
        var name = Path.GetFileName(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"Cannot read {name}: the file no longer exists.");
        if (info.Length == 0) throw new InvalidDataException($"Cannot read {name}: the file is empty (0 bytes).");
    }

    /// <summary>P1: portrait means the pixels, after the EXIF orientation flag, are taller than wide.</summary>
    static bool IsPortrait(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            var orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata
                && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var raw = metadata.GetQuery("/app1/ifd/{ushort=274}");
                if (raw != null) orientation = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            if (orientation is 6 or 8) (width, height) = (height, width);
            return height > width;
        }
        catch { return false; }
    }

    /// <summary>Z3: a signature block — label row with 16 px clear X, 90 px preview.</summary>
    Border BuildSignatureBlock(string title, InkCanvas canvas)
    {
        var autoName = title.StartsWith("Beneficiary", StringComparison.Ordinal) ? "Clear beneficiary signature" : "Clear representative signature";
        var stack = new StackPanel { Margin = new Thickness(0) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        var clear = new Button { Content = "✕", Width = 16, Height = 16, FontSize = 11, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), Opacity = 0.8 };
        System.Windows.Automation.AutomationProperties.SetName(clear, autoName);
        clear.Click += (_, _) => canvas.Strokes.Clear();
        header.Children.Add(clear);
        Grid.SetColumn(clear, 1);
        stack.Children.Add(header);

        System.Windows.Automation.AutomationProperties.SetName(canvas, title + " canvas");
        canvas.DefaultDrawingAttributes.Color = Colors.Black;
        canvas.DefaultDrawingAttributes.Width = canvas.DefaultDrawingAttributes.Height = 2.5;
        canvas.Height = 90;
        canvas.HorizontalAlignment = HorizontalAlignment.Stretch;
        canvas.Margin = new Thickness(0, 0, 0, 0);
        var state = new TextBlock { Text = "Not signed", FontSize = 11, Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"), Margin = new Thickness(0, 0, 0, 0) };
        canvas.Strokes.StrokesChanged += (_, _) =>
        {
            SetStatus(canvas.Strokes.Count == 0 ? $"Blank signature: '{title}' has no strokes. Draw a signature." : $"Signed: '{title}'.");
            state.Text = canvas.Strokes.Count == 0 ? "Not signed" : "Signed";
            signatureDebounceTimer.Change(400, Timeout.Infinite);
        };
        var surface = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Height = 90,
            Child = canvas
        };
        stack.Children.Add(surface);
        stack.Children.Add(state);
        return new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 0),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }
    static BitmapImage Thumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 260;
        using (var stream = File.OpenRead(path)) { image.StreamSource = stream; image.EndInit(); image.Freeze(); }
        return image;
    }

    static Border Card(UIElement child) => new()
    {
        Child = child,
        Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 0, 0, 14)
    };

    // Z1: labels use the normal body-text brush, never Muted.
    static TextBlock Label(string text, double size, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 4, 0, 7)
    };

    /// <summary>Section label, 13 px semibold, used across the Photos tab.</summary>
    static TextBlock SectionLabel(string title, Thickness? margin = null) => new()
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
        Margin = margin ?? new Thickness(0, 0, 0, 4)
    };

    /// <summary>Consistent section heading used across the Photos tab.</summary>
    static StackPanel SectionHeader(string title, string? hint = null, Thickness? margin = null)
    {
        var stack = new StackPanel { Margin = margin ?? new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            Margin = new Thickness(0, 0, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(hint))
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint!,
                FontSize = 12,
                Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 0)
            });
        }
        return stack;
    }

    /// <summary>Z1: the muted brush is reserved for hints such as "No photo selected".</summary>
    static TextBlock Hint(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 0)
    };


    /// <summary>G1a: a 12 px muted caption above a field.</summary>
    static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        Margin = new Thickness(0, 6, 0, 0)
    };

    /// <summary>
    /// V1: the name field fills the row, the Choose button sits in its own
    /// 90 px column to the right; the mobile field spans the whole row.
    /// The placeholder is drawn over the empty field and never takes a click.
    /// </summary>
    static Grid CreateStringRow(TextBox box, Button? button, TextBlock placeholder)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Height = 30;
        box.Width = double.NaN;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);
        if (placeholder != null)
        {
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(placeholder, 0);
            grid.Children.Add(placeholder);
        }
        if (button != null)
        {
            button.Height = 30;
            button.Width = 90;
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        return grid;
    }

    /// <summary>2R: the persons for this record's vendor, from the store when
    /// available, otherwise the officer's book filtered to this vendor.</summary>
    List<VendorPeople.Person> VendorPersonsForRecord()
    {
        var vendorCode = BookVendorCode;
        if (store != null)
        {
            var contacts = store.VendorContacts(vendorCode);
            if (contacts.Count > 0)
                return contacts.Select(c => new VendorPeople.Person(c.PersonName, c.Mobile, DateTimeOffset.Now, vendorCode)).ToList();
        }
        return vendorPeople.People
            .Where(p => string.IsNullOrWhiteSpace(p.Vendor) || string.Equals(p.Vendor, vendorCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Test hook for the picker flow: apply a picked pair without opening the dialog.</summary>
    void CollapsePickerOverlay()
    {
        pickerStateTimer?.Stop();
        CollapseAddForm();
        if (pickerDeleteError != null)
        {
            pickerDeleteError.Text = "";
            pickerDeleteError.Visibility = Visibility.Collapsed;
        }
        if (pickerOverlay != null) pickerOverlay.Visibility = Visibility.Collapsed;
        _pickerOpen = false;
        pickerOkButton.IsEnabled = false;
        pickerChooseButton.Focus();
    }

    static (TextBox Box, Grid Host) PickerInputField(string placeholder, int maxLength)
    {
        var box = new TextBox
        {
            Height = 30,
            MinHeight = 0,
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (maxLength > 0) box.MaxLength = maxLength;
        var host = new Grid();
        host.Children.Add(box);
        var hint = new TextBlock
        {
            Text = placeholder,
            FontSize = 12,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        host.Children.Add(hint);
        box.TextChanged += (_, _) => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        return (box, host);
    }

    void CollapseAddForm()
    {
        if (pickerAddForm != null) pickerAddForm.Visibility = Visibility.Collapsed;
        if (pickerAddName != null) pickerAddName.Text = "";
        if (pickerAddMobile != null) pickerAddMobile.Text = "";
        if (pickerAddError != null)
        {
            pickerAddError.Text = "";
            pickerAddError.Visibility = Visibility.Collapsed;
        }
    }

    void ShowPickerAddError(string message)
    {
        pickerAddError.Text = message;
        pickerAddError.Visibility = Visibility.Visible;
    }

    PickerRow? SelectedPickerRow() => (pickerListBox.SelectedItem as ListBoxItem)?.Tag as PickerRow;

    void AcceptPickerSelection()
    {
        if (SelectedPickerRow() is not PickerRow row) return;
        VendorPersonName = row.Name;
        VendorPersonMobile = row.Mobile;
        if (string.IsNullOrWhiteSpace(recordVendorCode)) recordVendorCode = consumerNumber;
        UpdatePlaceholders();
        PersistInstallerPair();
        CollapsePickerOverlay();
    }

    Grid PickerRowContent(PickerRow row, ListBoxItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var del = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 11, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(1),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            ToolTip = "Remove from list", VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(del, "Remove " + row.Name);
        del.Click += (_, e) => { DeletePickerRow(item); e.Handled = true; };
        grid.Children.Add(del);
        var name = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var mobile = new TextBlock { Text = row.Mobile, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 2, 0) };
        Grid.SetColumn(mobile, 2);
        grid.Children.Add(mobile);
        return grid;
    }

    void LogPickerState()
    {
        var key = BookVendorCode;
        var raw = pickerRows.Count;
        var filter = (pickerSearchBox?.Text ?? "").Trim();
        var shown = pickerListBox?.Items.OfType<ListBoxItem>().Count(i => i.Tag is PickerRow) ?? 0;
        var listH = pickerListBox?.ActualHeight ?? 0;
        Log($"picker state: key='{key}' raw={raw} filter='{filter}' shown={shown} listH={listH}");
    }

    void SchedulePickerStateLog()
    {
        pickerStateTimer?.Stop();
        pickerStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        pickerStateTimer.Tick += (_, _) =>
        {
            pickerStateTimer?.Stop();
            if (_pickerOpen) LogPickerState();
        };
        pickerStateTimer.Start();
    }

    void ApplyPickerFilter()
    {
        pickerListBox.Items.Clear();
        pickerOkButton.IsEnabled = false;
        var typed = (pickerSearchBox?.Text ?? "").Trim();
        var filtered = pickerRows
            .Where(r => typed.Length == 0
                || (r.Name ?? "").Contains(typed, StringComparison.OrdinalIgnoreCase)
                || (r.Mobile ?? "").Contains(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Log($"filter='{typed}' shown={filtered.Count}");
        if (filtered.Count == 0)
        {
            pickerEmptyText.Text = typed.Length == 0
                ? "No installers for this vendor — use Add new installer"
                : "No matching installer";
            pickerEmptyText.Visibility = Visibility.Visible;
            return;
        }
        pickerEmptyText.Visibility = Visibility.Collapsed;
        foreach (var row in filtered)
        {
            ListBoxItem item = new() { Tag = row, Padding = new Thickness(2, 3, 2, 3) };
            item.Content = PickerRowContent(row, item);
            pickerListBox.Items.Add(item);
        }
    }

    void DeletePickerRow(ListBoxItem item)
    {
        if (item.Tag is not PickerRow row) return;
        var wasSelected = ReferenceEquals(pickerListBox.SelectedItem, item);
        var vendorCode = BookVendorCode;
        if (pickerDeleteError != null)
        {
            pickerDeleteError.Text = "";
            pickerDeleteError.Visibility = Visibility.Collapsed;
        }

        int removedByRowId = 0;
        var stillByRowId = false;
        var remainingAfterRowId = 0;
        try { removedByRowId = store?.DeleteVendorContactByRowId(row.RowId, out _, out _) ?? 0; } catch { }
        try
        {
            var rows = store?.VendorBook(vendorCode) ?? Array.Empty<InspectionStore.VendorBookRow>();
            remainingAfterRowId = rows.Count;
            stillByRowId = rows.Any(r => r.RowId == row.RowId);
        }
        catch { }
        Log($"delete attempt rowid: rowid={row.RowId} key='{vendorCode}' removed={removedByRowId} remaining={remainingAfterRowId} stillPresent={stillByRowId}");

        var removed = removedByRowId > 0 && !stillByRowId;
        if (removed)
        {
            Log($"delete attempt key: rowid={row.RowId} key='{vendorCode}' name='{row.Name}' removed=0 remaining={remainingAfterRowId} stillPresent=False skipped=rowid-verified");
        }
        else
        {
            int removedByKey = 0;
            var stillByKey = false;
            var remainingAfterKey = 0;
            try { removedByKey = store?.DeleteVendorContact(vendorCode, row.Name) ?? 0; } catch { }
            try { store?.AddTombstone(vendorCode, row.Name); } catch { }
            try
            {
                var rows = store?.VendorBook(vendorCode) ?? Array.Empty<InspectionStore.VendorBookRow>();
                remainingAfterKey = rows.Count;
                stillByKey = rows.Any(r => string.Equals((r.PersonName ?? "").Trim(), row.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            Log($"delete attempt key: rowid={row.RowId} key='{vendorCode}' name='{row.Name}' removed={removedByKey} remaining={remainingAfterKey} stillPresent={stillByKey}");
            removed = !stillByKey;
        }

        if (!removed)
        {
            var line = $"Not removed — rowid={row.RowId} key='{vendorCode}'";
            if (pickerDeleteError != null)
            {
                pickerDeleteError.Text = line;
                pickerDeleteError.Visibility = Visibility.Visible;
            }
            return;
        }

        Log($"delete verified: rowid={row.RowId} key='{vendorCode}' name='{row.Name}'");
        lastDeletedIndex = pickerRows.IndexOf(row);
        pickerRows.Remove(row);
        lastDeletedRow = row;
        if (pickerUndoLink != null)
        {
            pickerUndoLink.Content = $"Removed {row.Name} — Undo";
            pickerUndoLink.Visibility = Visibility.Visible;
        }
        if (wasSelected) pickerOkButton.IsEnabled = false;
        ApplyPickerFilter();
    }

    void UndoPickerDelete()
    {
        if (lastDeletedRow is not PickerRow row) return;
        var vendorCode = BookVendorCode;
        try
        {
            store?.UpsertVendorContact(vendorCode, row.Name, row.Mobile);
            store?.ClearTombstone(vendorCode, row.Name);
        }
        catch { }
        long restoredRowId = 0;
        try
        {
            var found = store?.VendorBook(vendorCode)
                .FirstOrDefault(r => string.Equals(r.PersonName, row.Name, StringComparison.OrdinalIgnoreCase));
            if (found != null) restoredRowId = found.RowId;
        }
        catch { }
        var restored = new PickerRow(restoredRowId, row.Name, row.Mobile);
        if (!pickerRows.Any(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var index = lastDeletedIndex >= 0 && lastDeletedIndex <= pickerRows.Count ? lastDeletedIndex : pickerRows.Count;
            pickerRows.Insert(index, restored);
        }
        lastDeletedRow = null;
        lastDeletedIndex = -1;
        if (pickerUndoLink != null) pickerUndoLink.Visibility = Visibility.Collapsed;
        ApplyPickerFilter();
        foreach (var item in pickerListBox.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is PickerRow r && string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            {
                pickerListBox.SelectedItem = item;
                pickerListBox.ScrollIntoView(item);
                break;
            }
        }
        pickerOkButton.IsEnabled = SelectedPickerRow() != null;
    }

    void DoPickerAdd()
    {
        var name = (pickerAddName.Text ?? "").Trim();
        var mobile = (pickerAddMobile.Text ?? "").Trim();
        if (name.Length == 0) { ShowPickerAddError("Name is required."); return; }
        if (mobile.Length != 10 || mobile.Any(ch => !char.IsDigit(ch)))
        {
            ShowPickerAddError("Mobile No must be exactly 10 digits.");
            return;
        }
        // M4: a book lookup key is never a record write. An empty record vendor
        // code falls back to the consumer number for the BOOK ONLY.
        var vendorCode = BookVendorCode;

        var before = store?.VendorBook(vendorCode).Count ?? 0;
        try { store?.UpsertVendorContact(vendorCode, name, mobile); }
        catch (Exception ex) { ShowPickerAddError("Could not save: " + ex.Message); return; }
        var afterRows = store?.VendorBook(vendorCode) ?? Array.Empty<InspectionStore.VendorBookRow>();
        var after = afterRows.Count;
        Log($"add roundtrip: key='{vendorCode}' before={before} after={after}");

        var saved = afterRows.FirstOrDefault(r => string.Equals(r.PersonName, name, StringComparison.OrdinalIgnoreCase));
        if (after == before && saved == null)
        {
            ShowPickerAddError("Not saved — the book does not show " + name + " after the write.");
            return;
        }
        if (after > before + 1)
        {
            ShowPickerAddError("Not saved — the book gained " + (after - before) + " rows for one add.");
            return;
        }

        var existing = pickerRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) pickerRows.Remove(existing);
        var row = new PickerRow(saved?.RowId ?? 0, name, mobile);
        pickerRows.Add(row);
        pickerRows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        CollapseAddForm();
        ApplyPickerFilter();
        foreach (var item in pickerListBox.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is PickerRow r && r.RowId == row.RowId && string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            {
                pickerListBox.SelectedItem = item;
                pickerListBox.ScrollIntoView(item);
                break;
            }
        }
        pickerOkButton.IsEnabled = SelectedPickerRow() != null;
        Log($"picker add: {name}/{mobile} total={pickerRows.Count}");
    }

    void LoadPickerRows()
    {
        // M4: the book key falls back to the consumer number in memory only.
        // A book read must never write inspection_records.vendor_code.
        var vendorCode = BookVendorCode;
        if (string.IsNullOrWhiteSpace(recordVendorCode))
            Log("picker open: record vendorCode EMPTY — book key resolves to '" + consumerNumber + "' (no record write)");
        // Import the officer's book once per window. Importing again after a pick
        // re-enters the store's non-reentrant lock and would hang the second open.
        if (!pickerImported)
        {
            try
            {
                var bookRows = vendorPeople.People
                    .Where(p => string.IsNullOrWhiteSpace(p.Vendor) || string.Equals(p.Vendor, vendorCode, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (p.Name, p.Mobile));
                store?.ImportVendorContacts(vendorCode, bookRows);
                pickerImported = true;
            }
            catch { }
        }
        pickerRows.Clear();
        foreach (var c in store?.VendorBook(vendorCode) ?? Array.Empty<InspectionStore.VendorBookRow>())
            pickerRows.Add(new PickerRow(c.RowId, c.PersonName, c.Mobile));
        pickerRows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        lastDeletedRow = null;
        lastDeletedIndex = -1;
        if (pickerUndoLink != null) pickerUndoLink.Visibility = Visibility.Collapsed;
        ApplyPickerFilter();
    }

    internal void ApplyPickedInstallerForTest(string name, string mobile) =>
        PickVendorPerson(new Mskpy.Infrastructure.VendorPeople.Person(name, mobile, DateTimeOffset.Now, recordVendorCode ?? ""));

    /// <summary>V1: the Clear link empties the pair on this record only. The
    /// contact book and the vendor_contacts table are not touched.</summary>
    internal void DoClear()
    {
        VendorPersonName = "";
        VendorPersonMobile = "";
        vendorPersonNameBox.Text = "";
        vendorPersonMobileBox.Text = "";
        if (vendorWarning != null) vendorWarning.Visibility = Visibility.Collapsed;
        if (installerNote != null) installerNote.Visibility = Visibility.Collapsed;
        pickerOkButton.IsEnabled = false;
        UpdatePlaceholders();
        UpdateRisk();
    }

    /// <summary>C5: a note the app keeps beside its data.</summary>
    void LogStartupNote(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-start.log"),
                $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch { /* a note that cannot be written must never stop the form */ }
    }
    void Log(string message) => LogInstaller(message);

    void LogInstaller(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "inspection.log"),
                string.Format("{0:O} {1} {2}{3}", DateTime.Now, consumerNumber, message, Environment.NewLine));
        }
        catch { }
    }


    static StackPanel CreateFieldRow(string label, FrameworkElement box, Button clear)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = label + ": ",
            FontSize = 13,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = 150
        });
        var fieldGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        fieldGrid.Children.Add(box);
        clear.Margin = new Thickness(0, 0, 4, 0);
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        fieldGrid.Children.Add(clear);
        row.Children.Add(fieldGrid);
        return row;
    }

    async Task Submit()
    {
        if (submitBusy) return;
        // M5: immediately before sending, re-fetch the detail. If the record
        // changed on the server, do not send last week's version.
        try
        {
            var fresh = await workflow.DetailAsync(consumerNumber, cpfNo, CancellationToken.None);
            if (fresh.Ok && fresh.Record != null)
            {
                if (RecordChangeDetector.Changed(record, fresh.Record))
                {
                    MessageBox.Show(this, "This record changed on the server since you opened it. The form will reload with the latest version.",
                        "Stale record", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        catch { /* a failed re-fetch must not block a legitimate submit */ }
        // D6: a record with no resolved position cannot be sent.
        if (submitBtn != null && !locationResolved && string.IsNullOrWhiteSpace(manualLat.Text))
        {
            MessageBox.Show(this, "This survey needs a position: enter the coordinates by hand before submitting.",
                "Submit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        submitBusy = true;
        // P3: stop harvesting for the duration of the upload.
        harvester?.Pause();
        SetStatus("Submitting…");
        try
        {
            // A1: no vendor gate. The vendor is the record's own vendor_code and
            // goes into both the header and the body.

            // T4: installer field validation (bytecode Yup: non-empty name,
            // mobile ^[1-9]\d{9,} 10+ digits).
            var vpName = VendorPersonName.Trim();
            var vpMobile = VendorPersonMobile.Trim();
            if (vpName.Length == 0)
                throw new InvalidDataException("Vendor Person Name is required.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(vpMobile, @"^[1-9]\d{9,}$"))
                throw new InvalidDataException(
                    "Vendor Person Mobile No must be 10+ digits starting with a non-zero digit.");

            // P3: remember the pair that just went out.
            PersistInstallerPair();

            // T2: blank-signature refusal at Submit — zero strokes, naming the blank role.
            if (beneficiary.Strokes.Count == 0)
                throw new InvalidDataException("Blank signature: 'Beneficiary signature' has no strokes. Draw a signature.");
            if (representative.Strokes.Count == 0)
                throw new InvalidDataException("Blank signature: 'Vendor representative signature' has no strokes. Draw a signature.");

            // P4: the manual override must be numeric; otherwise the blob would
            // claim a coordinate the photos cannot be stamped with.
            if (!IsNumber(Empty(manualLat.Text)) || !IsNumber(Empty(manualLong.Text)) ||
                !IsNumber(Empty(manualAcc.Text)))
                throw new InvalidDataException("Manual latitude, longitude and accuracy must be numbers.");

            // P4: resolve the position once, then re-stamp the photos from
            // their originals with exactly the pair the blob will carry, so the
            // upload, the EXIF GPS and the distance all agree.
            var resolved = await workflow.location.ResolveAsync(
                Empty(manualLat.Text), Empty(manualLong.Text), Empty(manualAcc.Text),
                record.Latitude, record.Longitude, CancellationToken.None);
            locationBox.Text = DescribeLocation(resolved, resolved.DistanceMetres);

            // C3: there is no device fallback. The submission's position is the
            // customer's site; a record without coordinates needs the officer
            // to type them (C4) before the survey can be sent.
            if (!resolved.HasCoords)
                throw new InvalidDataException(resolved.Error ?? "This survey needs a position: enter the coordinates by hand.");

            // The ONE pair: it goes in the payload AND into the EXIF of all
            // three photos, so a reviewer comparing them finds the same
            // numbers.
            var blobLat = resolved.Lat!;
            var blobLong = resolved.Long!;
            _ = double.TryParse(blobLat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
            _ = double.TryParse(blobLong, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);

            var photoPaths = new List<string>();
            var extraData = new Dictionary<string, object?>();
            var stampAt = DateTime.UtcNow;
            var stampSecond = 0;
            foreach (var slot in PhotoSlots)
            {
                if (!photos.TryGetValue(slot.Suffix, out var slotPath) || !File.Exists(slotPath))
                    throw new InvalidDataException("Missing photo: " + slot.Label);
                var original = photoSources.TryGetValue(slot.Suffix, out var source) && File.Exists(source)
                    ? source
                    : null;
                if (original != null)
                {
                    // P2: the slot keeps exactly one file. At submit it is
                    // re-stamped in place from the original with the resolved
                    // pair, so EXIF and blob agree. A failed stamp leaves the
                    // bytes the slot already carries.
                    var photoAt = stampAt.AddSeconds(stampSecond++);
                    slotPath = JpegExifStamper.Stamp(original, lat, lon, photoAt, slotPath, LogStartupNote);
                    extraData[slot.Suffix + "DateTime"] =
                        photoAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                // P2: slots are independent. The same source may fill several
                // roles; no within-submission duplicate refusal.
                photoPaths.Add(slotPath);
            }

            var signPaths = new List<string>();
            foreach (var slot in SignSlots)
            {
                var path = SignToPng(slot.Suffix == "benSign" ? beneficiary : representative);
                if (path == null) throw new InvalidDataException("Missing signature: " + slot.Label);
                signPaths.Add(path);
            }
            // Q2: refuse if the two signature PNGs are byte-identical.
            if (signPaths.Count == 2)
            {
                var hashA = ComputeSha256(signPaths[0]);
                var hashB = ComputeSha256(signPaths[1]);
                if (hashA == hashB)
                    throw new InvalidDataException("Duplicate signature: beneficiary and vendor representative signatures are byte-identical. Draw two different signatures.");
            }

            var allowedDistText = string.IsNullOrWhiteSpace(allowedDist.Text) ? null : allowedDist.Text;
            var distRestrictedChecked = distRestricted.IsChecked == true;

            // P2: per-check answers feed the same {value, isOK, remark} key set
            // that was accepted; no dotted helper keys are emitted.
            var answers = new Dictionary<string, CheckAnswer>();
            foreach (var pair in checkAnswers)
            {
                var (yes, _, remark) = pair.Value;
                answers[pair.Key] = new CheckAnswer(yes.IsChecked == true ? "Y" : "N", remark.Text.Trim());
            }

            // V2: every check must still have a value and an isOK; refuse if any
            // is unset. Empty remark is correct for Y — no placeholder text.
            foreach (var pair in checkAnswers)
            {
                var (yes, no, remark) = pair.Value;
                var value = yes.IsChecked == true ? "Y" : (no.IsChecked == true ? "N" : null);
                var isOK = yes.IsChecked == true ? "Y" : (no.IsChecked == true ? "N" : null);
                if (value == null || isOK == null)
                    throw new InvalidDataException($"Check unset: {pair.Key} (no Correct/Incorrect selected).");
                // K3: When Incorrect, remark must be non-empty. Read the remark back
                // and name the check if it's missing — a remark typed into the
                // wrong box currently submits silently.
                if (isOK == "N" && string.IsNullOrWhiteSpace(remark.Text.Trim()))
                    throw new InvalidDataException($"Remark required for '{pair.Key}' when marked Incorrect.");
            }

            var checkErrors = SurveyRules.ValidateInspection(answers);
            if (checkErrors.Count > 0)
            {
                var first = checkErrors.First();
                throw new InvalidDataException($"Inspection checks incomplete: {first.Key} — {first.Value}");
            }

            // W1: photo uniqueness gate — refuse if any stamped photo was already
            // submitted for a different consumer. Insert rows only after 2xx.
            var photoHashChecks = new List<(string Suffix, string Hash)>();
            foreach (var slot in PhotoSlots)
            {
                if (photoHashes.TryGetValue(slot.Suffix, out var hash))
                {
                    var owner = store?.FindPhotoOwner(hash, slot.Suffix, consumerNumber);
                    if (owner != null)
                        throw new InvalidDataException($"This photo was already submitted for {owner}. Take a new one.");
                    photoHashChecks.Add((slot.Suffix, hash));
                }
            }

            // W4: persist the record before submit so a failed attempt can be retried.
            // S4: store LocalAppData photo paths (not %TEMP% paths) for retry.
            var retryPhotoPaths = PhotoSlots
                .Select(s => photos.TryGetValue(s.Suffix, out var p) ? p : null)
                .Where(p => p != null)
                .ToList();
            var photoPathsJson = System.Text.Json.JsonSerializer.Serialize(retryPhotoPaths);
            var signPathsJson = System.Text.Json.JsonSerializer.Serialize(signPaths);
            store?.Upsert(new StoredInspectionRecord(
                ConsumerNumber: consumerNumber,
                CpfNo: cpfNo,
                VendorCode: recordVendorCode,
                ConsumerName: FieldValue("consumer_name"),
                Latitude: record.Latitude,
                Longitude: record.Longitude,
                InstallDate: FieldValue("inst_date"),
                ReasonToRevert: FieldValue("reason_to_revert"),
                SurveyDurationMinutes: null,
                IsFirstTime: string.IsNullOrWhiteSpace(FieldValue("beneficiary_sign_file_name")),
                Status: RecordStatus.Submitted,
                LastServerMessage: null,
                LastServerMessageAt: null,
                OriginalJson: null,
                SubmittedDataPath: null,
                PhotoPaths: photoPathsJson,
                SignPaths: signPathsJson
            ), caller: "inspection-submit");

            var outcome = await workflow.SubmitAsync(
                record,
                photoPaths,
                signPaths,
                Empty(manualLat.Text),
                Empty(manualLong.Text),
                Empty(manualAcc.Text),
                allowedDistText,
                distRestrictedChecked,
                extraData,
                CancellationToken.None,
                formOpenTime: formOpenTime,
                checkAnswers: answers,
                vendorPersonName: VendorPersonName,
                vendorPersonMobile: VendorPersonMobile,
                resolvedLocation: resolved,
                sessionLoginId: loginId);

            // J1: every part is listed by name with its size; signatures are muted.
            SetManifest(outcome.Manifest, outcome.LocationSummary);

            bodyBox.Text = $"HTTP {(int)outcome.StatusCode} {outcome.StatusCode}\n\n{outcome.Body}";
            if (outcome.Error != null) bodyBox.Text += "\n\nGate: " + outcome.Error;
            if (bodyScroll != null) bodyScroll.Visibility = Visibility.Visible;
            SetStatus(outcome.Ok ? "Submit complete (HTTP 2xx). Body shown above." : $"Submit returned HTTP {(int)outcome.StatusCode}. Body shown above.");
            if (outcome.DataFilePath != null)
                SetStatus(status.Text + $"  data.json written: {outcome.DataFilePath}");

            // W1: insert photo hash rows only after a successful 2xx upload.
            var sentAt = DateTime.UtcNow.ToString("o");
            foreach (var (suffix, hash) in photoHashChecks)
                store?.InsertPhotoHash(hash, consumerNumber, suffix, sentAt);

            // W4: update record status based on submit outcome.
            if (outcome.Ok) {
                store?.SetStatus(consumerNumber, RecordStatus.Accepted);
            } else if (RefusalClassifier.IsStateRefusal(outcome.Body)) {
                store?.SetStatus(consumerNumber, RecordStatus.Blocked, outcome.Body,
                    blockedReason: RefusalClassifier.Reason(outcome.Body),
                    blockedAt: DateTime.UtcNow);
            } else {
                store?.SetStatus(consumerNumber, RecordStatus.Failed, outcome.Body);
            }

            onSubmitComplete?.Invoke(record, outcome);
        }
        catch (Exception ex)
        {
            bodyBox.Text = "Submit failed: " + ex.Message;
            SetStatus("Submit failed.");
            MessageBox.Show(this, bodyBox.Text, "Submit error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            harvester?.Resume();
            submitBusy = false;
        }
    }

    async Task Verify()
    {
        var outcome = await workflow.VerifyAsync(consumerNumber, cpfNo, CancellationToken.None);
        var text = $"Verify: count={outcome.Count}";
        if (outcome.UpdatedDt != null) text += $", updated_dt={outcome.UpdatedDt}";
        if (outcome.Error != null) text += $" · {outcome.Error}";
        bodyBox.Text += "\n\n--- VERIFY ---\n" + text;
        if (bodyScroll != null) bodyScroll.Visibility = Visibility.Visible;
        SetStatus(outcome.Ok ? "Verify complete." : "Verify failed.");
        if (outcome.Count > 1)
            MessageBox.Show(this, $"WARN: count={outcome.Count} > 1 for consumer_number {consumerNumber}", "Verify warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        onVerifyComplete?.Invoke(record, outcome);
    }

    /// <summary>
    /// The duplicate-signature comparison. It reads InkCanvas.Strokes, so it MUST
    /// run on the UI thread — callers on any other thread have to marshal first.
    /// </summary>
    internal bool SignaturesIdentical()
    {
        var pathA = SignToPng(beneficiary);
        var pathB = SignToPng(representative);
        return pathA != null && pathB != null && ComputeSha256(pathA) == ComputeSha256(pathB);
    }

    /// <summary>
    /// How many debounced signature checks have completed. Test hook: it only
    /// advances when the timer callback ran and marshalled back to the UI thread.
    /// </summary>
    internal int SignatureCheckCount { get; private set; }

    /// <summary>T3: debounced signature-duplicate check at attach time.</summary>
    void CheckSignatureDuplicates()
    {
        // The debounce timer fires on a thread-pool thread. InkCanvas.Strokes is
        // owned by the UI thread, so reading it here raised
        // InvalidOperationException ("a different thread owns it") and killed the
        // process — the operator lost the staged form. Every WPF touch must be
        // marshalled onto the dispatcher first.
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (SignaturesIdentical())
                        SetStatus("Duplicate signature: beneficiary and vendor representative signatures are byte-identical. Draw two different signatures.");
                    // J1: the signature parts appear in the pre-send manifest as
                    // soon as they are drawn.
                    RefreshSignaturePreviews();
                }
                catch (Exception ex)
                {
                    SetStatus("Signature check failed: " + ex.Message);
                }
                finally
                {
                    SignatureCheckCount++;
                }
            }));
        }
        catch (Exception ex)
        {
            SetStatus("Signature check failed: " + ex.Message);
        }
    }

    /// <summary>W1: SHA-256 of a file for the photo uniqueness gate.</summary>
    static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>P2: SHA-1 of a file's bytes, used for the slot file name.</summary>
    static string ComputeSha1(string path)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static bool IsStampedFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("-stamped", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-stamped-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Formats an ISO-8601 date string as DD-Mon-YYYY (toDDMONYYYY),
    /// device-local time. Header-card display only — the blob is untouched.</summary>
    static string FormatInstDate(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate)) return "Not provided";
        if (!DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return isoDate;
        var local = parsed.ToLocalTime();
        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        return $"{local.Day:00}-{months[local.Month - 1]}-{local.Year}";
    }

    string? SignToPng(InkCanvas canvas)
    {
        if (canvas.Strokes.Count == 0) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"insp-sign-{Guid.NewGuid():N}.png");
        return RenderCanvasToPng(canvas, tmp);
    }

    /// <summary>Renders an ink canvas to a PNG at the given path. UI thread only.</summary>
    static string? RenderCanvasToPng(InkCanvas canvas, string path)
    {
        if (canvas.Strokes.Count == 0) return null;
        var width = Math.Max(1, (int)Math.Ceiling(canvas.ActualWidth > 0 ? canvas.ActualWidth : 400));
        var height = Math.Max(1, (int)Math.Ceiling(canvas.ActualHeight > 0 ? canvas.ActualHeight : 200));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(target));
        using (var stream = File.Create(path)) png.Save(stream);
        return path;
    }
    void PickVendorPerson(VendorPeople.Person person)
    {
        // V3: nothing is written until OK; here we commit the chosen pair.
        VendorPersonName = person.Name;
        VendorPersonMobile = person.Mobile;
        // Write the two cells explicitly as well as through the binding - a pick
        // must never leave the read-only field looking empty.
        vendorPersonNameBox.Text = person.Name;
        vendorPersonMobileBox.Text = person.Mobile;
        if (string.IsNullOrWhiteSpace(recordVendorCode) && !string.IsNullOrWhiteSpace(person.Vendor))
            recordVendorCode = person.Vendor;
        UpdateRisk();
        UpdatePlaceholders();
        PersistInstallerPair();
        var recordVendor = recordVendorCode ?? "";
        var warning = vendorWarning;
        if (warning == null) return;
        if (!string.IsNullOrWhiteSpace(person.Vendor) && !string.IsNullOrWhiteSpace(recordVendor)
            && !string.Equals(person.Vendor, recordVendor, StringComparison.OrdinalIgnoreCase))
        {
            warning.Text = person.Name + " was last used with vendor " + person.Vendor
                + "; this record's vendor is " + recordVendor + ". The submitted vendor is the record's.";
            warning.Visibility = Visibility.Visible;
        }
        else { warning.Visibility = Visibility.Collapsed; }
        LogInstaller("pick: name='" + vendorPersonNameBox.Text + "' mobile='" + vendorPersonMobileBox.Text + "'");
    }

    /// <summary>P3: prefill the last-used pair for this vendor on open.</summary>


    void PersistInstallerPair()
    {
        var name = VendorPersonName.Trim();
        var mobile = VendorPersonMobile.Trim();
        if (name.Length == 0 || mobile.Length == 0) return;
        var vendorCode = BookVendorCode;
        store?.UpsertVendorContact(vendorCode, name, mobile);
        vendorPeople.Add(name, mobile, vendorCode);
        try { vendorPeople.Save(); } catch { }
    }

    /// <summary>C5: the entries that match what has been typed.</summary>


    /// <summary>P3: prefill the last-used pair for this vendor on open.</summary>
    void PrefillInstallerPair()
    {
        if (!string.IsNullOrWhiteSpace(VendorPersonName) || !string.IsNullOrWhiteSpace(VendorPersonMobile))
        {
            // M3: a reopen keeps the saved pair; log the control text so the
            // check has a line on every open, not only on a fresh prefill.
            LogInstaller("prefill applied (kept): nameBox='" + vendorPersonNameBox.Text + "' mobileBox='" + vendorPersonMobileBox.Text + "'");
            return;
        }
        var vendorCode = BookVendorCode;
        (string Name, string Mobile)? pair = null;
        string branch = "";
        if (store != null)
        {
            // M1: the same VendorBook the picker lists, so prefill cannot find a
            // person the list would not show.
            var book = store.VendorBook(vendorCode);
            if (book.Count > 0)
            {
                pair = (book[0].PersonName, book[0].Mobile);
                branch = "vendor-book-last";
            }
        }
        if (pair == null)
        {
            var book = vendorPeople.People
                .Where(p => string.IsNullOrWhiteSpace(p.Vendor) || string.Equals(p.Vendor, vendorCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastUsedAt)
                .FirstOrDefault();
            if (book != null) { pair = (book.Name, book.Mobile); branch = "book-last"; }
        }
        if (pair == null)
        {
            LogInstaller("prefill: nothing found for vendor=" + (string.IsNullOrWhiteSpace(vendorCode) ? "(record VendorCode empty)" : vendorCode) + " record=" + consumerNumber + " (store=" + (store != null) + ", book=" + vendorPeople.People.Count + ")");
            UpdatePlaceholders();
            return;
        }
        // V3: prefill the two plain strings; the list stays closed.
        VendorPersonName = pair.Value.Name;
        VendorPersonMobile = pair.Value.Mobile;
        vendorPersonNameBox.Text = pair.Value.Name;
        vendorPersonMobileBox.Text = pair.Value.Mobile;
        // V4: prove the prefill actually populated the fields.
        LogInstaller("prefill: vendor=" + vendorCode + " branch=" + (string.IsNullOrEmpty(branch) ? "none" : branch) + " -> name='" + vendorPersonNameBox.Text + "' mobile='" + vendorPersonMobileBox.Text + "'");
        // M3: the control text, not the model.
        LogInstaller("prefill applied: nameBox='" + vendorPersonNameBox.Text + "' mobileBox='" + vendorPersonMobileBox.Text + "'");
        UpdatePlaceholders();
        UpdateRisk();
    }



    /// <summary>V1: the Clear link empties the pair on this record only. The
    /// contact book and the vendor_contacts table are not touched.</summary>

}
