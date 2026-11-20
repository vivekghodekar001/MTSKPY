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
public sealed class InspectionWindow : Window
{
    readonly InspectionWorkflow workflow;

    readonly InspectionRecord record;
    readonly string consumerNumber;
    readonly string cpfNo;
    readonly string loginId;
    // A1: there is no session vendor code. The vendor always comes from the
    // record's own vendor_code, and the header and body carry the same value.
    // P3: the harvester is paused while an upload is in flight.
    readonly InspectionHarvester? harvester;
    readonly InspectionStore? store; // W1
    readonly Action<InspectionRecord, SubmitOutcome>? onSubmitComplete;
    readonly Action<InspectionRecord, VerifyOutcome>? onVerifyComplete;
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
    readonly TextBlock bodyBox = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
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
    readonly TextBox manualLat = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualLong = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualAcc = new() { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox allowedDist = new() { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
    readonly CheckBox distRestricted = new() { Content = "Distance restricted", Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox vendorPersonNameBox = new() { IsEditable = true, StaysOpenOnEdit = true, IsTextSearchEnabled = false, DisplayMemberPath = "Name", Width = 300, Padding = new Thickness(0, 0, 26, 0), Margin = new Thickness(0, 0, 8, 0) };
    /// <summary>
    /// H3: how a contact row prints in the dropdown - "name  ·  mobile". The
    /// row shows this template; the box's own text is ToString() (the mobile),
    /// so WPF's selection write can never paste the label into the field.
    /// </summary>
    static readonly DataTemplate ContactRowTemplate = BuildContactRowTemplate();

    static DataTemplate BuildContactRowTemplate()
    {
        var rowText = new FrameworkElementFactory(typeof(TextBlock));
        rowText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(VendorOption.Label)));
        return new DataTemplate { VisualTree = rowText };
    }

    readonly ComboBox vendorPersonMobileBox = new() { IsEditable = true, StaysOpenOnEdit = true, IsTextSearchEnabled = false, Width = 300, Padding = new Thickness(0, 0, 26, 0), Margin = new Thickness(0, 0, 8, 0), ItemTemplate = ContactRowTemplate };
    readonly Dictionary<string, (RadioButton yes, RadioButton no, TextBox remark)> checkAnswers = new();
    /// <summary>C4: the manual coordinate boxes, hidden behind a link.</summary>
    StackPanel? manualRows;

    TextBlock? riskHeader;
    TextBlock? riskFooter;
    bool locationResolved;
    bool submitBusy;
    Button? submitBtn;
    Button? acknowledgeBtn;
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
        this.consumerNumber = consumerNumber;
        this.cpfNo = cpfNo;
        this.loginId = loginId;
        this.workflow = workflow;
        // H1: named installerBook, not vendorPeople - a same-named parameter
        // would shadow this field everywhere below and hand out null.
        this.vendorPeople = installerBook ?? new Mskpy.Infrastructure.VendorPeople();

        this.harvester = harvester;
        this.store = store; // W1
        this.onSubmitComplete = onSubmitComplete;
        this.onVerifyComplete = onVerifyComplete;
        signatureDebounceTimer = new System.Threading.Timer(_ => CheckSignatureDuplicates(), null, Timeout.Infinite, Timeout.Infinite);

        Title = $"Pump Inspection — {consumerNumber}";
        Width = 1000; MinWidth = 760; MinHeight = 560;
        // Z7: use the full work area so the Photos tab shows all three photo slots
        // and both signature canvases without scrolling. Still no SizeToContent.
        Height = Math.Min(1000, System.Windows.SystemParameters.WorkArea.Height - 60);
        Background = (Brush)System.Windows.Application.Current.FindResource("Bg");
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Segoe UI");
        // Z7: the window now fills the work area, so centre it on the screen —
        // centring on the owner pushed its footer below the taskbar.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Z5: the status line uses the accent brush and collapses when empty.
        status.Foreground = (Brush)System.Windows.Application.Current.FindResource("Accent");
        status.Visibility = Visibility.Collapsed;

        // Y2: Grid layout — header row, scrollable tabs row, footer row
        var grid = new Grid { Margin = new Thickness(16), Background = Background };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Y1: Read-only header card (record info), Row 0 — two lines, compact
        var headerCard = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(new TextBlock
        {
            Text = $"Name: {FieldValue("consumer_name")}   |   Application No: {FieldValue("consumer_number")}   |   Address: {FieldValue("address")}",
            FontSize = 13,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerCard.Children.Add(line1);

        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var instDate = FieldValue("inst_date");
        var instDateText = string.IsNullOrEmpty(instDate) ? "Not provided" : FormatInstDate(instDate);
        line2.Children.Add(new TextBlock
        {
            Text = $"Inst Date: {instDateText}   |   Inst Latitude: {FieldValue("latitude")}   |   Inst Longitude: {FieldValue("longitude")}",
            FontSize = 13,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var navBtn = new Button
        {
            Content = "Navigate to Inst Location",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center
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
        line2.Children.Add(navBtn);
        headerCard.Children.Add(line2);

        // M2: the pre-submit risk band and its reasons, shown before the form
        // is filled.
        riskHeader = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        headerCard.Children.Add(riskHeader);

        Grid.SetRow(headerCard, 0);
        grid.Children.Add(headerCard);

        // Z6: each tab owns exactly one ScrollViewer. The Photos tab's single
        // scroller is shared by both of its columns.
        var tabControl = new TabControl { Margin = new Thickness(0, 0, 0, 10) };

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
                var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
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
                var radios = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var yes = new RadioButton { Content = "Correct", GroupName = check.Field, IsChecked = true };
                var no = new RadioButton { Content = "Incorrect", GroupName = check.Field };
                radios.Children.Add(yes); radios.Children.Add(no);
                checkPanel.Children.Add(radios);
                var remark = new TextBox
                {
                    Width = 520,
                    Margin = new Thickness(0, 4, 0, 0),
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
                var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
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
                var radios = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var yes = new RadioButton { Content = "Correct", GroupName = check.Field, IsChecked = true };
                var no = new RadioButton { Content = "Incorrect", GroupName = check.Field };
                radios.Children.Add(yes); radios.Children.Add(no);
                checkPanel.Children.Add(radios);
                var remark = new TextBox
                {
                    Width = 520,
                    Margin = new Thickness(0, 4, 0, 0),
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

        // Y4: Photos & Signatures tab — two columns
        var photosTab = new TabItem { Header = "Photos & Signatures", Margin = new Thickness(0) };
        var photosGrid = new Grid { Margin = new Thickness(8) };
        photosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        photosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Z6: no per-column scroller — the tab below carries one shared scroller.
        // Left column: Site Photos + Signatures
        var leftPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        leftPanel.Children.Add(Label("Site Photos (3 JPEG)", 15, new Thickness(0, 0, 0, 6)));
        foreach (var slot in PhotoSlots) AddPhotoSlot(leftPanel, slot.Suffix, slot.Label);

        // Z3: both signature canvases sit side by side directly under the third slot.
        var signRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        signRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        signRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var benBlock = BuildSignatureBlock("Beneficiary signature", beneficiary);
        var repBlock = BuildSignatureBlock("Vendor representative signature", representative);
        Grid.SetColumn(benBlock, 0);
        Grid.SetColumn(repBlock, 1);
        signRow.Children.Add(benBlock);
        signRow.Children.Add(repBlock);
        leftPanel.Children.Add(signRow);

        Grid.SetColumn(leftPanel, 0);
        photosGrid.Children.Add(leftPanel);

        // G1a: right column — the installer card rebuilt as a vertical stack.
        var rightPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var installerStack = new StackPanel();
        // P1: installer boxes start EMPTY — the officer types whoever actually
        // attended. The stored name is only whoever attended last time and must
        // not block a legitimate re-inspection with a different person.
        vendorPersonNameBox.Text = "";
        vendorPersonMobileBox.Text = "";
        // P3: prefill last-used pair for this vendor (beneficiary's previous
        // pair if it exists, else the vendor's most recent). Empty only when
        // there is no history for this vendor.
        PrefillInstallerPair();
        var clearName = MakeClearButton("Clear Vendor Person Name", (Brush)System.Windows.Application.Current.FindResource("Muted"), (Brush)System.Windows.Application.Current.FindResource("Accent"), () => vendorPersonNameBox.Text = "");
        var clearMobile = MakeClearButton("Clear Vendor Person Mobile No", (Brush)System.Windows.Application.Current.FindResource("Muted"), (Brush)System.Windows.Application.Current.FindResource("Accent"), () => vendorPersonMobileBox.Text = "");
        System.Windows.Automation.AutomationProperties.SetName(vendorPersonNameBox, "Vendor Person Name");
        System.Windows.Automation.AutomationProperties.SetName(vendorPersonMobileBox, "Vendor Person Mobile No");

        // C5: the officer's own book of installers. The boxes stay free text
        // and stay empty; the book only offers what is already in it.
        if (vendorPeople.LoadWarning != null) LogStartupNote(vendorPeople.LoadWarning);
        var editBook = new Button
        {
            Content = "Edit…",
            Style = (Style)System.Windows.Application.Current.FindResource("LinkButton"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Add, rename or remove the people in your installer list"
        };
        System.Windows.Automation.AutomationProperties.SetName(editBook, "Edit the installer list");
        editBook.Click += (_, _) => EditVendorPeople(vendorPeople);

        // Row 0 — "Installer Details" | "Edit…"
        var installerHeader = new Grid();
        installerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        installerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        installerHeader.Children.Add(new TextBlock
        {
            Text = "Installer Details",
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center
        });
        installerHeader.Children.Add(editBook);
        Grid.SetColumn(editBook, 1);
        installerStack.Children.Add(installerHeader);

        // Rows 1–4 — label, field, label, field. Every field carries an
        // explicit height; the clear disc is overlaid inside its own field.
        installerStack.Children.Add(FieldLabel("Vendor Person Name"));
        installerStack.Children.Add(CreateComboRow(vendorPersonNameBox, clearName));
        installerStack.Children.Add(FieldLabel("Vendor Person Mobile No"));
        installerStack.Children.Add(CreateComboRow(vendorPersonMobileBox, clearMobile));

        // Row 5 — the empty-book hint, collapsed the moment the book has anyone.
        installerHint = new TextBlock
        {
            Text = "No saved installers — press Edit to add one.",
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = vendorPeople.People.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        installerStack.Children.Add(installerHint);

        vendorPersonMobileBox.Loaded += (_, _) =>
        {
            if (vendorPersonMobileBox.Template.FindName("PART_EditableTextBox", vendorPersonMobileBox) is TextBox tb)
            {
                tb.TextChanged += (_, _) =>
                {
                    // H3: a pick is filling this box - rebuilding under it
                    // would drop the very entry being picked.
                    if (vendorPicking) { UpdateRisk(); return; }
                    // H3: the contact box offers the same book, by name or by
                    // number, and opens on a match exactly like the name box.
                    vendorPersonMobileBox.IsDropDownOpen = RefreshContactSuggestionList() > 0;
                    UpdateRisk();
                };
            }
        };
        vendorPersonMobileBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                vendorPersonMobileBox.IsDropDownOpen = false;
                e.Handled = true;
            }
        };
        vendorPersonMobileBox.DropDownOpened += (_, _) => RefreshContactSuggestionList(offerBookWhenEmpty: true);
        vendorPersonMobileBox.SelectionChanged += (_, e) =>
        {
            // H3: read the pick off the event - replacing the item list clears
            // SelectedItem before this handler runs.
            var picked = e.AddedItems.Count > 0 ? e.AddedItems[0] as VendorOption : null;
            picked ??= vendorPersonMobileBox.SelectedItem as VendorOption;
            if (picked == null) return;
            vendorPicking = true;
            try
            {
                if (!string.Equals(vendorPersonMobileBox.Text, picked.Person.Mobile, StringComparison.Ordinal))
                    vendorPersonMobileBox.Text = picked.Person.Mobile;
                // H3: a contact chosen first also names the person - but never
                // over a name the officer has already typed.
                if (string.IsNullOrWhiteSpace(vendorPersonNameBox.Text))
                    vendorPersonNameBox.Text = picked.Person.Name;
                UpdateRisk();
            }
            finally { vendorPicking = false; }
            vendorPersonMobileBox.IsDropDownOpen = false;
        };

        // The suggestions: what the officer has typed so far, matched against
        // the book. Picking one fills the mobile - but never over a mobile the
        // officer has already typed.
        vendorPersonNameBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                vendorPersonNameBox.IsDropDownOpen = false;
                e.Handled = true;
            }
        };
        vendorPersonNameBox.SelectionChanged += (_, e) =>
        {
            // H3: read the pick off the event, not off SelectedItem - the item
            // list is replaced while the text is filled, and a replaced list
            // clears SelectedItem before this handler runs. That is why a click
            // used to leave both boxes untouched.
            var picked = e.AddedItems.Count > 0 ? e.AddedItems[0] as Mskpy.Infrastructure.VendorPeople.Person : null;
            picked ??= vendorPersonNameBox.SelectedItem as Mskpy.Infrastructure.VendorPeople.Person;
            if (picked != null) PickVendorPerson(picked);
        };
        vendorPersonNameBox.Loaded += (_, _) =>
        {
            if (vendorPersonNameBox.Template.FindName("PART_EditableTextBox", vendorPersonNameBox) is TextBox tb)
            {
                tb.TextChanged += (_, _) =>
                {
                    // H3: a pick is filling this box - replacing the item list
                    // here would clear the selection it is filling from.
                    if (vendorPicking) { UpdateRisk(); return; }
                    // H1/H3: every key press rebuilds the list; no match means
                    // nothing is offered, so the popup stays shut.
                    vendorPersonNameBox.IsDropDownOpen = RefreshVendorSuggestionList() > 0;
                    RefreshSuggestions();
                    UpdateRisk();
                };
            }
        };
        // P3: persist the pair on blur, so a typed number is saved against
        // the chosen person even without a submit.
        vendorPersonNameBox.LostFocus += (_, _) => PersistInstallerPair();
        vendorPersonMobileBox.LostFocus += (_, _) => PersistInstallerPair();

        // H1: opening the dropdown by clicking the arrow also rebuilds it, so a
        // name added through Edit… is offered without a keystroke. With an empty
        // box the whole book (first six) is what the officer is offered.
        vendorPersonNameBox.DropDownOpened += (_, _) => RefreshVendorSuggestionList(offerBookWhenEmpty: true);
        RefreshSuggestions();

        // The soft warning: the person was last used with another vendor.
        vendorWarning = new TextBlock
        {
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        installerStack.Children.Add(vendorWarning);
        rightPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            Margin = new Thickness(0, 0, 0, 12),
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = installerStack
        });

        // C3/C4: the location. The submission's position is the customer's
        // site; the raw boxes are hidden behind a link, for the rare record
        // whose registered coordinates are plainly wrong.
        var locationCard = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        // G1a: "Location" and the "set manually" link share one line.
        var locationHeader = new Grid();
        locationHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        locationHeader.Children.Add(new TextBlock
        {
            Text = "Location",
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center
        });
        locationCard.Children.Add(locationHeader);
        var clearLat = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearLat.Click += (_, _) => manualLat.Text = "";
        var clearLong = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearLong.Click += (_, _) => manualLong.Text = "";
        var clearAcc = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearAcc.Click += (_, _) => manualAcc.Text = "";
        // C4: the manual boxes live behind a link and stay out of the way
        // until the officer needs them.
        manualRows = new StackPanel { Visibility = Visibility.Collapsed };
        manualLat.TextChanged += (_, _) => UpdateSubmitEnabled();
        manualLong.TextChanged += (_, _) => UpdateSubmitEnabled();
        manualAcc.TextChanged += (_, _) => UpdateSubmitEnabled();
        manualRows.Children.Add(CreateFieldRow("Manual latitude", manualLat, clearLat));
        manualRows.Children.Add(CreateFieldRow("Manual longitude", manualLong, clearLong));
        manualRows.Children.Add(CreateFieldRow("Manual accuracy (m)", manualAcc, clearAcc));
        locationCard.Children.Add(manualRows);
        var setManually = new Button
        {
            Content = "set manually",
            Style = (Style)System.Windows.Application.Current.FindResource("LinkButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Enter the coordinates by hand - for a record whose registered coordinates are plainly wrong"
        };
        System.Windows.Automation.AutomationProperties.SetName(setManually, "set manually");
        setManually.Click += (_, _) =>
        {
            manualRows.Visibility = manualRows.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            setManually.Content = manualRows.Visibility == Visibility.Visible ? "hide the manual boxes" : "set manually";
        };
        locationHeader.Children.Add(setManually);
        Grid.SetColumn(setManually, 1);
        var locationActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var resolveBtn = new Button { Content = "Use current location", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10, 4, 10, 4) };
        resolveBtn.Click += async (_, _) => await ResolveLocation();
        locationActions.Children.Add(resolveBtn);
        locationActions.Children.Add(distRestricted);
        locationActions.Children.Add(new TextBlock { Text = "Allowed distance (m): ", Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
        locationActions.Children.Add(allowedDist);
        locationCard.Children.Add(locationActions);
        locationBox.TextWrapping = TextWrapping.Wrap;
        // Z1: body-text brush — the inherited tab foreground is the accent purple.
        locationBox.Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        locationBox.Text = "Not resolved yet. A submission is made from the customer's own site; the recorded coordinates are jittered a few metres.";
        locationCard.Children.Add(locationBox);
        // D6: the resolved pair, its source and the distance are shown before
        // the form is filled, so a wrong registered site is caught up front.
        // A record with no coordinates resolves to "Manual" and keeps Submit
        // disabled until the officer types them.
        _ = ResolveLocation();
        rightPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Background = (Brush)System.Windows.Application.Current.FindResource("Surface"),
            MaxWidth = 420,
            Child = locationCard
        });

        Grid.SetColumn(rightPanel, 1);
        photosGrid.Children.Add(rightPanel);

        // Z6: one shared scroller for the whole Photos tab — both columns scroll together.
        photosTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = photosGrid
        };
        tabControl.Items.Add(photosTab);

        // Y2: Footer row — manifest box, buttons row, body box
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        // H4: the banner is part of the fixed footer, above the status line.
        footer.Children.Add(banner);
        // Z5: One status line — collapses when empty, accent brush when populated.
        SetStatus("");
        footer.Children.Add(status);
        // J1: manifest label + a box that sizes to its content up to a maximum
        // and then scrolls. With no parts it collapses to nothing, so the empty
        // state is not a large blank box.
        manifestLabel.Text = "Manifest (pre-send):";
        manifestLabel.Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        footer.Children.Add(manifestLabel);
        manifestBox.Child = manifestPanel;
        manifestScroll.Content = manifestBox;
        footer.Children.Add(manifestScroll);
        SetManifest(Array.Empty<InspectionMultipartBuilder.ManifestEntry>());

        // M2: the risk warnings, in the fixed footer.
        riskFooter = new TextBlock
        {
            FontSize = 13,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        footer.Children.Add(riskFooter);

        acknowledgeBtn = new Button { Content = "Acknowledge", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 6, 14, 6), Visibility = Visibility.Collapsed };
            footer.Children.Add(acknowledgeBtn);

        // Actions — left-aligned buttons row
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        submitBtn = new Button { Content = "Submit Survey", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(14, 6, 14, 6), IsEnabled = false };
        submitBtn.Click += async (_, _) => await Submit();
        actions.Children.Add(submitBtn);
        var verifyBtn = new Button { Content = "Verify (re-read)", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(14, 6, 14, 6) };
        verifyBtn.Click += async (_, _) => await Verify();
        actions.Children.Add(verifyBtn);
        var closeBtn = new Button { Content = "Close", Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        closeBtn.Click += (_, _) => Close();
        actions.Children.Add(closeBtn);
        footer.Children.Add(actions);

        // Z4: body box fixed 90 px with its own scroll
        bodyBox.FontFamily = new FontFamily("Consolas");
        bodyBox.FontSize = 11;
        bodyBox.Background = Brushes.White;
        // Z1: the body box is white, so its text must be dark.
        bodyBox.Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1B, 0x1F));
        bodyBox.Padding = new Thickness(8);
        bodyBox.Text = "Awaiting submission…";  // Y3: keep this
        bodyBox.TextWrapping = TextWrapping.Wrap;
        var bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 90
        };
        bodyScroll.Content = bodyBox;
        footer.Children.Add(bodyScroll);

        Grid.SetRow(tabControl, 1);
        grid.Children.Add(tabControl);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

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

        var installerBlank = string.IsNullOrWhiteSpace(vendorPersonNameBox.Text)
            || string.IsNullOrWhiteSpace(vendorPersonMobileBox.Text);

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
            }
        }
        if (riskFooter != null)
        {
            riskFooter.Text = "";
            riskFooter.Visibility = Visibility.Collapsed;
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

    void AddPhotoSlot(Panel target, string suffix, string label)
    {
        // Z2/Z7: compact slot — label, thumbnail, then the Choose/Remove row
        // immediately under it, with the file-name hint on the same line.
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(Label(label, 14, new Thickness(0, 0, 0, 2)));
        var image = new Image
        {
            // P1: show the whole photo, never a fixed 4:3 crop.
            Stretch = Stretch.Uniform
        };
        // A neutral frame so an empty slot reads as a placeholder, not a
        // broken image.
        var thumbFrame = new Border
        {
            Width = 132,
            Height = 96,
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF0, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xD7, 0xE6)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 2),
            Child = image
        };
        var fname = Hint("No photo selected", 11);
        fname.VerticalAlignment = VerticalAlignment.Center;
        var orientationNote = Hint("Portrait photo", 11);
        orientationNote.Margin = new Thickness(0, 0, 0, 2);
        orientationNote.Visibility = Visibility.Collapsed;
        panel.Children.Add(thumbFrame);
        panel.Children.Add(orientationNote);
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var choose = CompactButton("Choose JPEG…");
        choose.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JPEG images|*.jpg;*.jpeg",
                Title = label,
                Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var path = picker.FileName;
                // P2: the source is read-only. Picking the app's own working
                // file (or an older *-stamped copy) is refused by name.
                if (IsStampedFile(path) || IsSlotFile(path))
                    throw new InvalidDataException("Pick the original photo, not a file the app has already prepared.");
                ValidatePhotoSource(path);
                Directory.CreateDirectory(PhotoSlotDirectory);

                // P2: one working file per slot, named att-<field>-<sha1[0:10]>.jpg.
                // The name never derives from the source file name, so the same
                // source can sit in any number of slots without colliding.
                var slotPath = SlotPhotoPath(suffix, ComputeSha1(path));
                var produced = JpegExifStamper.Stamp(
                    path,
                    ResolveCoordinate("latitude", manualLat.Text),
                    ResolveCoordinate("longitude", manualLong.Text),
                    DateTime.UtcNow,
                    slotPath,
                    LogStartupNote);

                // Re-picking replaces this slot's file only. Never the source,
                // never another slot's file.
                if (photos.TryGetValue(suffix, out var previous)
                    && !string.Equals(previous, produced, StringComparison.OrdinalIgnoreCase))
                    DeleteSlotFile(previous);

                photos[suffix] = produced;
                photoSources[suffix] = path;
                photoHashes[suffix] = ComputeSha256(path); // source fingerprint (W1)

                try
                {
                    image.Source = Thumbnail(produced);
                    orientationNote.Visibility = IsPortrait(produced) ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    // P1: never bind a null/broken preview. Named message, the
                    // attach itself still stands.
                    image.Source = null;
                    orientationNote.Visibility = Visibility.Collapsed;
                    SetStatus($"Cannot read {Path.GetFileName(produced)}: {ex.Message}");
                }
                fname.Text = Path.GetFileName(produced);
                // J1: the manifest preview lists what is attached.
                RefreshManifestPreview();
            }
            catch (Exception ex)
            {
                // A bad, locked or unreadable file must never take the app down
                // mid-inspection — report it and let the officer pick another.
                SetStatus("Photo could not be loaded: " + ex.Message);
            }
        };
        var remove = CompactButton("Remove");
        remove.Click += (_, _) =>
        {
            // P2: remove deletes this slot's att- file only.
            if (photos.TryGetValue(suffix, out var slotFile)) DeleteSlotFile(slotFile);
            photos.Remove(suffix);
            photoSources.Remove(suffix);
            photoHashes.Remove(suffix);
            image.Source = null;
            orientationNote.Visibility = Visibility.Collapsed;
            fname.Text = "No photo selected";
            // J1: the manifest preview drops the removed part.
            RefreshManifestPreview();
        };
        row.Children.Add(choose); row.Children.Add(remove);
        // Z1: the file-name caption is a hint — muted brush, on the button row.
        row.Children.Add(fname);
        panel.Children.Add(row);
        target.Children.Add(panel);
    }

    /// <summary>P2: where this consumer's slot files live.</summary>
    string PhotoSlotDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenewableEnergyDesktop", "inspection-data", consumerNumber);

    /// <summary>P2: att-<field>-<sha1(source bytes)[0:10]>.jpg.</summary>
    string SlotPhotoPath(string suffix, string sha1)
    {
        var stamp = sha1.Length > 10 ? sha1[..10] : sha1;
        return Path.Combine(PhotoSlotDirectory, $"att-{suffix}-{stamp}.jpg");
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
        if (info.Length > 20 * 1024 * 1024) throw new InvalidDataException($"Cannot read {name}: larger than 20 MB.");
        try
        {
            using var stream = info.OpenRead();
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not JpegBitmapDecoder)
                throw new InvalidDataException($"Cannot read {name}: not a genuine JPEG.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException($"Cannot read {name}: {ex.Message}"); }
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

    /// <summary>Z3: a signature block — label, 110 px canvas, Clear disc in its corner.</summary>
    StackPanel BuildSignatureBlock(string title, InkCanvas canvas)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        block.Children.Add(Label(title, 14, new Thickness(0, 0, 0, 2)));
        // Name the canvas so it is reachable by assistive tech and by UI automation.
        System.Windows.Automation.AutomationProperties.SetName(canvas, title + " canvas");
        canvas.DefaultDrawingAttributes.Color = Colors.Black;
        canvas.DefaultDrawingAttributes.Width = canvas.DefaultDrawingAttributes.Height = 2.5;
        canvas.Strokes.StrokesChanged += (_, _) =>
        {
            // T2: at attach time, only report signed/not signed — no refusal here.
            SetStatus(canvas.Strokes.Count == 0
                ? $"Blank signature: '{title}' has no strokes. Draw a signature."
                : $"Signed: '{title}'.");
            // T3: debounced duplicate check at attach time.
            signatureDebounceTimer.Change(400, Timeout.Infinite);
        };
        // J2: the canvas hosts the small round Clear disc in its top-right corner.
        var host = new Grid();
        host.Children.Add(canvas);
        var muted = (Brush)System.Windows.Application.Current.FindResource("Muted");
        var hover = new SolidColorBrush(Color.FromRgb(0xE2, 0xDE, 0xE8));
        var clear = MakeClearButton(
            title.StartsWith("Beneficiary", StringComparison.Ordinal) ? "Clear beneficiary signature" : "Clear representative signature",
            muted, hover,
            () => canvas.Strokes.Clear());
        host.Children.Add(clear);
        block.Children.Add(host);
        return block;
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

    /// <summary>Z1: the muted brush is reserved for hints such as "No photo selected".</summary>
    static TextBlock Hint(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>
    /// C5: the officer's own book of installers - a small dialog with the
    /// list, an add box, rename and delete.
    /// </summary>
    void EditVendorPeople(VendorPeople book)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var nameBox = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
        var mobileBox = new TextBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(nameBox, "New installer name");
        System.Windows.Automation.AutomationProperties.SetName(mobileBox, "New installer mobile");

        void Rebuild()
        {
            rows.Children.Clear();
            foreach (var person in book.People.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var name = new TextBox { Text = person.Name, Width = 220, Margin = new Thickness(0, 0, 8, 0) };
                var mobile = new TextBox { Text = person.Mobile, Width = 160, Margin = new Thickness(0, 0, 8, 0) };
                var delete = new Button { Content = "remove", Style = (Style)System.Windows.Application.Current.FindResource("LinkButton") };
                System.Windows.Automation.AutomationProperties.SetName(delete, "Remove " + person.Name);
                delete.Click += (_, _) =>
                {
                    book.Remove(person.Name);
                    book.Save();
                    Rebuild();
                };
                var save = new Button { Content = "save", Style = (Style)System.Windows.Application.Current.FindResource("LinkButton") };
                System.Windows.Automation.AutomationProperties.SetName(save, "Save " + person.Name);
                save.Click += (_, _) =>
                {
                    // C5: a rename keeps the mobile unless it is changed too.
                    book.Rename(person.Name, name.Text, mobile.Text);
                    book.Save();
                    Rebuild();
                };
                row.Children.Add(name);
                row.Children.Add(mobile);
                row.Children.Add(save);
                row.Children.Add(delete);
                rows.Children.Add(row);
            }
        }
        Rebuild();

        var addButton = new Button { Content = "Add", Padding = new Thickness(14, 6, 14, 6) };
        System.Windows.Automation.AutomationProperties.SetName(addButton, "Add to the installer list");
        addButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            book.Add(nameBox.Text, mobileBox.Text);
            book.Save();
            nameBox.Text = "";
            mobileBox.Text = "";
            Rebuild();
        };

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Your installer list", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(new TextBlock
        {
            Text = "Yours alone. It stays in this machine's app folder and is never sent anywhere.",
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        addRow.Children.Add(nameBox);
        addRow.Children.Add(mobileBox);
        addRow.Children.Add(addButton);
        content.Children.Add(addRow);
        content.Children.Add(rows);

        var window = new Window
        {
            Title = "Installer list",
            Width = 580,
            Height = 460,
            Background = (Brush)System.Windows.Application.Current.FindResource("Bg"),
            Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
            FontFamily = new FontFamily("Segoe UI"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false
        };
        var closeButton = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 6, 14, 6) };
        closeButton.Click += (_, _) => window.Close();
        content.Children.Add(closeButton);
        window.Content = content;
        window.ShowDialog();
        RefreshSuggestions();
    }

    /// <summary>
    /// H3: one row of the contact dropdown: what the officer sees in the list,
    /// and who it is. ToString() is the MOBILE, not the label - an editable
    /// ComboBox writes the selected item's text into the box itself, after the
    /// pick handler has run, and that write used to replace the mobile with
    /// "name  ·  mobile". The rows print Label; the box takes ToString().
    /// </summary>
    public sealed record VendorOption(string Label, VendorPeople.Person Person)
    {
        public override string ToString() => Person.Mobile;
    }

    /// <summary>
    /// H3: set while a pick is filling the boxes. Rebuilding the dropdown's
    /// item list under a pick drops the selection the pick is filling from -
    /// which is why picking used to leave both boxes empty.
    /// </summary>
    bool vendorPicking;

    /// <summary>C5: the officer's own book of installers.</summary>
    readonly VendorPeople vendorPeople;
    TextBlock? vendorWarning;
    TextBlock? installerHint;

    /// <summary>C5: take a person from the book.</summary>
    void PickVendorPerson(VendorPeople.Person person)
    {
        // H3: everything below fills boxes, and each fill fires TextChanged.
        // The guard stops those handlers from rebuilding the dropdown's item
        // list mid-pick - a rebuilt list drops the selection, and the pick was
        // being thrown away before it could put anything anywhere.
        vendorPicking = true;
        try
        {
            // H3: the name goes INTO THE BOX, not merely into the selection.
            if (!string.Equals(vendorPersonNameBox.Text, person.Name, StringComparison.Ordinal))
                vendorPersonNameBox.Text = person.Name;

            // Picking never overwrites a mobile the officer has typed.
            switch (VendorPeople.DecidePick(vendorPersonMobileBox.Text, person.Mobile))
            {
                case VendorPeople.Pick.Fill:
                    vendorPersonMobileBox.Text = person.Mobile;
                    break;
                case VendorPeople.Pick.Ask:
                    var answer = MessageBox.Show(this,
                        "The mobile you typed (" + vendorPersonMobileBox.Text + ") is not the one in your list (" + person.Mobile + ").\n\nUse the one in your list?",
                        "Mobile", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes) return;
                    vendorPersonMobileBox.Text = person.Mobile;
                    break;
                case VendorPeople.Pick.Keep:
                    break;
            }
            UpdateRisk();
        }
        finally { vendorPicking = false; }
        // H3: the pick is done - the list closes, and typing opens it again.
        vendorPersonNameBox.IsDropDownOpen = false;
        // The vendor in the payload is the RECORD's vendor, untouched. If the
        // person was last used with another vendor, say so - softly.
        var recordVendor = record.VendorCode ?? "";
        var warning = vendorWarning;
        if (warning == null) return;
        if (!string.IsNullOrWhiteSpace(person.Vendor) && !string.IsNullOrWhiteSpace(recordVendor)
            && !string.Equals(person.Vendor, recordVendor, StringComparison.OrdinalIgnoreCase))
        {
            warning.Text = person.Name + " was last used with vendor " + person.Vendor
                + "; this record's vendor is " + recordVendor + ". The submitted vendor is the record's.";
            warning.Visibility = Visibility.Visible;
        }
        else
        {
            warning.Visibility = Visibility.Collapsed;
        }
    }
    /// <summary>P3: prefill the last-used pair for this vendor on open.</summary>
    void PrefillInstallerPair()
    {
        if (!string.IsNullOrWhiteSpace(vendorPersonNameBox.Text) || !string.IsNullOrWhiteSpace(vendorPersonMobileBox.Text)) return;
        var vendorCode = record.VendorCode ?? "";
        (string Name, string Mobile)? pair = null;
        if (store != null)
        {
            var last = store.LastVendorContact(vendorCode);
            if (last.HasValue) pair = (last.Value.PersonName, last.Value.Mobile);
        }
        if (pair == null)
        {
            var book = vendorPeople.People
                .Where(p => string.IsNullOrWhiteSpace(p.Vendor) || string.Equals(p.Vendor, vendorCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastUsedAt)
                .FirstOrDefault();
            if (book != null) pair = (book.Name, book.Mobile);
        }
        if (pair == null) return;
        vendorPicking = true;
        try
        {
            vendorPersonNameBox.Text = pair.Value.Name;
            vendorPersonMobileBox.Text = pair.Value.Mobile;
        }
        finally { vendorPicking = false; }
        UpdateRisk();
    }

    /// <summary>P3: persist the current pair on blur or submit.</summary>
    void PersistInstallerPair()
    {
        var name = vendorPersonNameBox.Text.Trim();
        var mobile = vendorPersonMobileBox.Text.Trim();
        if (name.Length == 0 || mobile.Length == 0) return;
        var vendorCode = record.VendorCode ?? "";
        store?.UpsertVendorContact(vendorCode, name, mobile);
        vendorPeople.Add(name, mobile, vendorCode);
        try { vendorPeople.Save(); } catch { }
    }

    /// <summary>C5: the entries that match what has been typed.</summary>
    void RefreshSuggestions()
    {
        // G1a: the dropdown is the offer (F1 opens it on a match); the only
        // chrome left under the fields is the empty-book hint.
        var hint = installerHint;
        if (hint == null) return;
        hint.Visibility = vendorPeople.People.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>G1a: a 12 px muted caption above a field.</summary>
    static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted"),
        Margin = new Thickness(0, 6, 0, 0)
    };

    /// <summary>
    /// G1a: an explicit-height field with its clear disc overlaid inside the
    /// field's own row — 4 px from the right edge, never floating beside it.
    /// </summary>
    static Grid CreateComboRow(ComboBox box, Button clear)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        box.Height = 30;
        box.Width = double.NaN;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.Margin = new Thickness(0, 0, 4, 0);
        grid.Children.Add(box);
        grid.Children.Add(clear);
        return grid;
    }

    /// <summary>
    /// H1: what the vendor name dropdown offers, and how many entries it is
    /// offering. Typing filters the book (case-insensitive); opening the box
    /// with an empty field offers the book itself, so a name just added
    /// through Edit… shows up without a keystroke.
    /// </summary>
    internal int RefreshVendorSuggestionList(bool offerBookWhenEmpty = false)
    {
        var typed = (vendorPersonNameBox.Text ?? "").Trim();
        IReadOnlyList<VendorPeople.Person> list = typed.Length == 0
            ? (offerBookWhenEmpty ? vendorPeople.People.Take(6).ToList() : Array.Empty<VendorPeople.Person>())
            : vendorPeople.Suggest(typed);
        vendorPersonNameBox.ItemsSource = list;
        return list.Count;
    }

    /// <summary>
    /// H3: what the CONTACT dropdown offers - the same book, matched on the
    /// name OR the number, laid out as "name  ·  mobile". Returns how many
    /// entries are being offered.
    /// </summary>
    internal int RefreshContactSuggestionList(bool offerBookWhenEmpty = false)
    {
        var typed = (vendorPersonMobileBox.Text ?? "").Trim();
        IReadOnlyList<VendorOption> list = typed.Length == 0
            ? (offerBookWhenEmpty ? vendorPeople.People.Take(6).Select(ToOption).ToList() : Array.Empty<VendorOption>())
            : vendorPeople.People
                .Where(p => p.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                         || (p.Mobile.Length > 0 && p.Mobile.Contains(typed, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Name.Length)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(ToOption)
                .ToList();
        vendorPersonMobileBox.ItemsSource = list;
        return list.Count;
    }

    /// <summary>H3: one book entry as a row the contact dropdown can print.</summary>
    static VendorOption ToOption(VendorPeople.Person p) =>
        new(p.Mobile.Length > 0 ? p.Name + "  ·  " + p.Mobile : p.Name, p);

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

    static StackPanel CreateFieldRow(string label, FrameworkElement box, Button clear)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
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
            var vpName = vendorPersonNameBox.Text.Trim();
            var vpMobile = vendorPersonMobileBox.Text.Trim();
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
                VendorCode: record.VendorCode,
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
            ));

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
                vendorPersonName: vendorPersonNameBox.Text,
                vendorPersonMobile: vendorPersonMobileBox.Text,
                resolvedLocation: resolved,
                sessionLoginId: loginId);

            // J1: every part is listed by name with its size; signatures are muted.
            SetManifest(outcome.Manifest, outcome.LocationSummary);

            bodyBox.Text = $"HTTP {(int)outcome.StatusCode} {outcome.StatusCode}\n\n{outcome.Body}";
            if (outcome.Error != null) bodyBox.Text += "\n\nGate: " + outcome.Error;
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
}

