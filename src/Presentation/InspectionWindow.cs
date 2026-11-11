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
    readonly WindowsLocationProvider winLoc;
    readonly InspectionRecord record;
    readonly string consumerNumber;
    readonly string cpfNo;
    readonly string loginId;
    readonly string? vendorCode;
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
    readonly InkCanvas beneficiary = new() { Background = Brushes.White, Height = 155 };
    readonly InkCanvas representative = new() { Background = Brushes.White, Height = 155 };

    readonly TextBlock status = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock bodyBox = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock manifestBox = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock locationBox = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox manualLat = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualLong = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox manualAcc = new() { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox allowedDist = new() { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
    readonly CheckBox distRestricted = new() { Content = "Distance restricted", Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox vendorPersonNameBox = new() { Width = 300, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox vendorPersonMobileBox = new() { Width = 300, Margin = new Thickness(0, 0, 8, 0) };

    readonly Dictionary<string, (RadioButton yes, RadioButton no, TextBox remark)> checkAnswers = new();

    bool submitBusy;
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
        string? vendorCode,
        InspectionWorkflow workflow,
        WindowsLocationProvider winLoc,
        Window owner,
        InspectionHarvester? harvester = null,
        InspectionStore? store = null,
        Action<InspectionRecord, SubmitOutcome>? onSubmitComplete = null,
        Action<InspectionRecord, VerifyOutcome>? onVerifyComplete = null)
    {
        this.record = record;
        this.consumerNumber = consumerNumber;
        this.cpfNo = cpfNo;
        this.loginId = loginId;
        this.vendorCode = vendorCode;
        this.workflow = workflow;
        this.winLoc = winLoc;
        this.harvester = harvester;
        this.store = store; // W1
        this.onSubmitComplete = onSubmitComplete;
        this.onVerifyComplete = onVerifyComplete;
        signatureDebounceTimer = new System.Threading.Timer(_ => CheckSignatureDuplicates(), null, Timeout.Infinite, Timeout.Infinite);

        Title = $"Pump Inspection — {consumerNumber}";
        Width = 1000; MinWidth = 760; MinHeight = 700;
        // T5: size to content height, capped at the work area so the submit,
        // verify, manifest and body blocks are always reachable without hunting.
        SizeToContent = SizeToContent.Height;
        MaxHeight = System.Windows.SystemParameters.WorkArea.Height - 60;
        Background = (Brush)System.Windows.Application.Current.FindResource("Bg");
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var outer = new DockPanel { Margin = new Thickness(26), Background = Background };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(Label("Pump Inspection by Linestaff", 24));
        header.Children.Add(Label($"Consumer: {consumerNumber}  ·  CPF: {cpfNo}  ·  Login: {loginId}", 13));
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);

        var bottom = new StackPanel();
        bottom.Children.Add(status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        outer.Children.Add(bottom);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        outer.Children.Add(scroll);
        var body = new StackPanel();
        scroll.Content = body;

        // Header card, read-only
        var headerCard = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        headerCard.Children.Add(Label("Record Details", 16));
        var info = new StackPanel();
        foreach (var (key, title) in new[]
        {
            ("consumer_name", "Name"),
            ("consumer_number", "Application No"),
            ("address", "Address"),
            ("inst_date", "Inst Date"),
            ("latitude", "Inst Latitude"),
            ("longitude", "Inst Longitude")
        })
        {
            var val = FieldValue(key);
            // T3: Inst Date rendered via toDDMONYYYY (same formatter as commDate),
            // header card only — the blob is untouched.
            if (key == "inst_date" && !string.IsNullOrEmpty(val))
                val = FormatInstDate(val);
            info.Children.Add(Label(title + ": " + (string.IsNullOrEmpty(val) ? "Not provided" : val), 14));
        }
        headerCard.Children.Add(info);
        var navBtn = new Button
        {
            Content = "Navigate to Inst Location",
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 4, 10, 4)
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
        headerCard.Children.Add(navBtn);
        body.Children.Add(headerCard);

        // Installer Details section
        var installerCard = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        installerCard.Children.Add(Label("Installer Details", 16));
        var installerFields = new StackPanel();
        // P1: installer boxes start EMPTY — the officer types whoever actually
        // attended. The stored name is only whoever attended last time and must
        // not block a legitimate re-inspection with a different person.
        vendorPersonNameBox.Text = "";
        vendorPersonMobileBox.Text = "";
        var clearName = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0), ToolTip = "Clear Vendor Person Name" };
        clearName.Click += (_, _) => vendorPersonNameBox.Text = "";
        var clearMobile = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0), ToolTip = "Clear Vendor Person Mobile No" };
        clearMobile.Click += (_, _) => vendorPersonMobileBox.Text = "";
        installerFields.Children.Add(CreateFieldRow("Vendor Person Name", vendorPersonNameBox, clearName));
        installerFields.Children.Add(CreateFieldRow("Vendor Person Mobile No", vendorPersonMobileBox, clearMobile));
        installerCard.Children.Add(installerFields);
        body.Children.Add(installerCard);

        // Location (P4): manual override -> live Windows position -> installation
        // coordinates. The source used for the upload is always reported.
        var locationCard = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        locationCard.Children.Add(Label("Location", 16));
        var clearLat = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearLat.Click += (_, _) => manualLat.Text = "";
        var clearLong = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearLong.Click += (_, _) => manualLong.Text = "";
        var clearAcc = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        clearAcc.Click += (_, _) => manualAcc.Text = "";
        locationCard.Children.Add(CreateFieldRow("Manual latitude", manualLat, clearLat));
        locationCard.Children.Add(CreateFieldRow("Manual longitude", manualLong, clearLong));
        locationCard.Children.Add(CreateFieldRow("Manual accuracy (m)", manualAcc, clearAcc));
        var locationActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var resolveBtn = new Button { Content = "Use current location", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10, 4, 10, 4) };
        resolveBtn.Click += async (_, _) => await ResolveLocation();
        locationActions.Children.Add(resolveBtn);
        locationActions.Children.Add(distRestricted);
        locationActions.Children.Add(new TextBlock { Text = "Allowed distance (m): ", VerticalAlignment = VerticalAlignment.Center });
        locationActions.Children.Add(allowedDist);
        locationCard.Children.Add(locationActions);
        locationBox.TextWrapping = TextWrapping.Wrap;
        locationBox.Text = "Not resolved yet. Submit uses the live Windows position, then the installation coordinates.";
        locationCard.Children.Add(locationBox);
        body.Children.Add(locationCard);

        // Checks (Part A and Part B tabs)
        body.Children.Add(Label("Inspection Checks", 19));
        var tabPanel = new TabControl { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var part in new[] { "A", "B" })
        {
            var partChecks = SurveyRules.InspectionChecks.Where(x => x.Part == part).ToList();
            if (partChecks.Count == 0) continue;
            var tabItem = new TabItem { Header = $"Part {part}", Margin = new Thickness(0) };
            var partPanel = new StackPanel { Margin = new Thickness(8) };
            foreach (var check in partChecks)
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
                    VerticalAlignment = VerticalAlignment.Center
                });
                labelPanel.Children.Add(new TextBlock
                {
                    Text = valText,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
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
                partPanel.Children.Add(checkPanel);
            }
            tabItem.Content = partPanel;
            tabPanel.Items.Add(tabItem);
        }
        body.Children.Add(tabPanel);

        // Photos
        body.Children.Add(Label("Site Photos (3 JPEG)", 19));
        foreach (var slot in PhotoSlots) AddPhotoSlot(body, slot.Suffix, slot.Label);

        // Signatures
        body.Children.Add(Label("Signatures (2 PNG)", 19));
        AddSignature(body, "Beneficiary signature", beneficiary);
        AddSignature(body, "Vendor representative signature", representative);

        // Manifest
        body.Children.Add(Label("Manifest (pre-send)", 17));
        manifestBox.FontFamily = new FontFamily("Consolas");
        manifestBox.FontSize = 11;
        manifestBox.Background = Brushes.White;
        manifestBox.Padding = new Thickness(8);
        manifestBox.Text = "No submission yet.";
        body.Children.Add(manifestBox);

        // Actions
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var submitBtn = new Button { Content = "Submit Survey", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(14, 6, 14, 6) };
        submitBtn.Click += async (_, _) => await Submit();
        actions.Children.Add(submitBtn);
        var verifyBtn = new Button { Content = "Verify (re-read)", Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(14, 6, 14, 6) };
        verifyBtn.Click += async (_, _) => await Verify();
        actions.Children.Add(verifyBtn);
        var closeBtn = new Button { Content = "Close", Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        closeBtn.Click += (_, _) => Close();
        actions.Children.Add(closeBtn);
        body.Children.Add(actions);

        body.Children.Add(Label("Submission response (verbatim)", 17));
        bodyBox.FontFamily = new FontFamily("Consolas");
        bodyBox.FontSize = 11;
        bodyBox.Background = Brushes.White;
        bodyBox.Padding = new Thickness(8);
        bodyBox.Text = "Awaiting submission…";
        body.Children.Add(bodyBox);

        Content = outer;
        allowedDist.Text = "";
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

    static string DescribeLocation(LocationResult loc, double? distance) =>
        $"Source: {loc.Source}  ·  Lat={loc.Lat}  ·  Long={loc.Long}  ·  " +
        // T2: show the same accuracy the blob carries (12 when blank, never 0).
        $"Accuracy={(string.IsNullOrEmpty(loc.Accuracy) ? "12" : loc.Accuracy)}  ·  Mocked={loc.Mocked}  ·  " +
        $"Distance from installation={(distance.HasValue ? distance.Value.ToString("0.##", CultureInfo.InvariantCulture) : "n/a")} m  ·  " +
        (loc.Error != null ? "Error: " + loc.Error : "OK");

    async Task ResolveLocation()
    {
        status.Text = "Resolving location…";
        var loc = await workflow.location.ResolveAsync(
            Empty(manualLat.Text), Empty(manualLong.Text), Empty(manualAcc.Text),
            record.Latitude, record.Longitude, CancellationToken.None);
        locationBox.Text = DescribeLocation(loc, DistanceToInstall(loc.Lat, loc.Long));
        status.Text = "Location resolved: " + loc.Source;
    }

    void AddPhotoSlot(Panel target, string suffix, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(label, 15));
        var image = new Image { Height = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 6) };
        var fname = Label("No photo selected", 12);
        panel.Children.Add(fname); panel.Children.Add(image);
        var row = new WrapPanel();
        var choose = new Button { Content = "Choose JPEG…", Margin = new Thickness(0, 0, 8, 0) };
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
                // T1b: never accept an already-stamped JPEG.
                if (IsStampedFile(path))
                    throw new InvalidDataException("Do not select an already-stamped JPEG (*-stamped.jpg).");
                var info = new FileInfo(path);
                if (info.Length > 20 * 1024 * 1024) throw new InvalidDataException("Select a JPEG smaller than 20 MB.");
                using var stream = info.OpenRead();
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder is not JpegBitmapDecoder) throw new InvalidDataException("The selected file is not a genuine JPEG.");
                // Stamp EXIF into a copy; upload the stamped copy (P5).
                // E5: use the same coordinates that go into the blob.
                var stamped = JpegExifStamper.Stamp(
                    path,
                    ResolveCoordinate("latitude", manualLat.Text),
                    ResolveCoordinate("longitude", manualLong.Text),
                    DateTime.UtcNow);
                // S2: photo-duplicate refusal at attach time
                var photoHash = ComputeSha256(stamped);
                foreach (var otherKvp in photoHashes)
                {
                    if (otherKvp.Key != suffix && otherKvp.Value == photoHash)
                        throw new InvalidDataException($"Duplicate photo: '{label}' uses the same image as '{otherKvp.Key}'. Use three different originals.");
                }
                photos[suffix] = stamped;
                photoSources[suffix] = path;
                photoHashes[suffix] = photoHash;
                // S4: copy stamped photo to LocalAppData for retry
                var consumerDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RenewableEnergyDesktop", "inspection-data", consumerNumber);
                Directory.CreateDirectory(consumerDir);
                var consumerPhotoPath = Path.Combine(consumerDir, Path.GetFileName(stamped));
                File.Copy(stamped, consumerPhotoPath, overwrite: true);
                photos[suffix] = consumerPhotoPath;
                image.Source = Thumbnail(consumerPhotoPath);
                fname.Text = Path.GetFileName(stamped) + " (EXIF stamped)";
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidDataException)
            {
                status.Text = "Photo could not be loaded: " + ex.Message;
            }
        };
        var remove = new Button { Content = "Remove", Margin = new Thickness(0, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            photos.Remove(suffix);
            image.Source = null;
            fname.Text = "No photo selected";
        };
        row.Children.Add(choose); row.Children.Add(remove);
        panel.Children.Add(row);
        target.Children.Add(panel);
    }

    void AddSignature(Panel target, string title, InkCanvas canvas)
    {
        target.Children.Add(Label(title, 15));
        canvas.DefaultDrawingAttributes.Color = Colors.Black;
        canvas.DefaultDrawingAttributes.Width = canvas.DefaultDrawingAttributes.Height = 2.5;
        canvas.Strokes.StrokesChanged += (_, _) =>
        {
            // T2: at attach time, only report signed/not signed — no refusal here.
            status.Text = canvas.Strokes.Count == 0
                ? $"Blank signature: '{title}' has no strokes. Draw a signature."
                : $"Signed: '{title}'.";
            // T3: debounced duplicate check at attach time.
            signatureDebounceTimer.Change(400, Timeout.Infinite);
        };
        target.Children.Add(canvas);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        var clear = new Button { Content = "Clear signature", Margin = new Thickness(0, 0, 0, 0) };
        clear.Click += (_, _) => canvas.Strokes.Clear();
        actions.Children.Add(clear);
        target.Children.Add(actions);
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

    static TextBlock Label(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 7)
    };

    static StackPanel CreateFieldRow(string label, TextBox box, Button clear)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = label + ": ",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = 150
        });
        row.Children.Add(box);
        row.Children.Add(clear);
        return row;
    }

    async Task Submit()
    {
        if (submitBusy) return;
        submitBusy = true;
        // P3: stop harvesting for the duration of the upload.
        harvester?.Pause();
        status.Text = "Submitting…";
        try
        {
            // S8: vendor mismatch is a warning, not a refusal — submit proceeds.
            if (!string.IsNullOrWhiteSpace(vendorCode) &&
                !string.IsNullOrWhiteSpace(record.VendorCode) &&
                !string.Equals(vendorCode, record.VendorCode, StringComparison.Ordinal))
                status.Text = $"Warning: session vendor {vendorCode} ≠ record vendor {record.VendorCode}. Submitting anyway.";

            // T4: installer field validation (bytecode Yup: non-empty name,
            // mobile ^[1-9]\d{9,} 10+ digits).
            var vpName = vendorPersonNameBox.Text.Trim();
            var vpMobile = vendorPersonMobileBox.Text.Trim();
            if (vpName.Length == 0)
                throw new InvalidDataException("Vendor Person Name is required.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(vpMobile, @"^[1-9]\d{9,}$"))
                throw new InvalidDataException(
                    "Vendor Person Mobile No must be 10+ digits starting with a non-zero digit.");

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

            // P4: resolve the position once, then re-stamp the three photos from
            // their originals with exactly the pair the blob will carry, so the
            // upload, the EXIF GPS and the distance all agree.
            var resolved = await workflow.location.ResolveAsync(
                Empty(manualLat.Text), Empty(manualLong.Text), Empty(manualAcc.Text),
                record.Latitude, record.Longitude, CancellationToken.None);
            locationBox.Text = DescribeLocation(resolved, DistanceToInstall(resolved.Lat, resolved.Long));

            var blobLat = resolved.Lat ?? record.Latitude ?? "";
            var blobLong = resolved.Long ?? record.Longitude ?? "";
            _ = double.TryParse(blobLat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
            _ = double.TryParse(blobLong, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);

            var photoPaths = new List<string>();
            var extraData = new Dictionary<string, object?>();
            var stampAt = DateTime.UtcNow;
            var stampSecond = 0;
            // Q1: within-submission photo duplicate gate — the same image must not
            // be submitted under two different roles.
            var seenPhotoHashes = new HashSet<string>();
            foreach (var slot in PhotoSlots)
            {
                if (!photos.TryGetValue(slot.Suffix, out var stampedPath) || !File.Exists(stampedPath))
                    throw new InvalidDataException("Missing photo: " + slot.Label);
                var original = photoSources.TryGetValue(slot.Suffix, out var source) && File.Exists(source)
                    ? source
                    : stampedPath;
                // T1a: if the source is already a "-stamped" file it was stamped
                // at attach; do not stamp it again. Otherwise re-stamp from the
                // original with the submit-time coordinates so EXIF and blob agree.
                if (IsStampedFile(original))
                {
                    stampedPath = original;
                }
                else
                {
                    // R7: per-photo timestamps one second apart, stamped at submit.
                    var photoAt = stampAt.AddSeconds(stampSecond++);
                    var stamped = JpegExifStamper.Stamp(original, lat, lon, photoAt);
                    stampedPath = stamped;
                    extraData[slot.Suffix + "DateTime"] =
                        photoAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                // Q1: refuse if this photo's bytes match a photo already submitted
                // in this run.
                var photoHash = ComputeSha256(stampedPath);
                if (!seenPhotoHashes.Add(photoHash))
                    throw new InvalidDataException($"Duplicate photo: '{slot.Label}' uses the same image as a previous photo in this submission. Use three different originals.");
                photoPaths.Add(stampedPath);
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
                vendorCode,
                CancellationToken.None,
                formOpenTime: formOpenTime,
                checkAnswers: answers,
                vendorPersonName: vendorPersonNameBox.Text,
                vendorPersonMobile: vendorPersonMobileBox.Text,
                resolvedLocation: resolved,
                sessionLoginId: loginId);

            manifestBox.Text = "MANIFEST:\n  " + string.Join("\n  ", outcome.Manifest.Select(m =>
                $"{m.PartName} ({m.ContentType}, {m.Size}B, source={m.Source})"));
            manifestBox.Text += "\n\n" + (outcome.LocationSummary ?? "Location: not resolved.");

            bodyBox.Text = $"HTTP {(int)outcome.StatusCode} {outcome.StatusCode}\n\n{outcome.Body}";
            if (outcome.Error != null) bodyBox.Text += "\n\nGate: " + outcome.Error;
            status.Text = outcome.Ok ? "Submit complete (HTTP 2xx). Body shown above." : $"Submit returned HTTP {(int)outcome.StatusCode}. Body shown above.";
            if (outcome.DataFilePath != null)
                status.Text += $"  data.json written: {outcome.DataFilePath}";

            // W1: insert photo hash rows only after a successful 2xx upload.
            var sentAt = DateTime.UtcNow.ToString("o");
            foreach (var (suffix, hash) in photoHashChecks)
                store?.InsertPhotoHash(hash, consumerNumber, suffix, sentAt);

            // W4: update record status based on submit outcome.
            if (outcome.Ok) {
                store?.SetStatus(consumerNumber, RecordStatus.Accepted);
            } else {
                store?.SetStatus(consumerNumber, RecordStatus.Failed, outcome.Body);
            }

            onSubmitComplete?.Invoke(record, outcome);
        }
        catch (Exception ex)
        {
            bodyBox.Text = "Submit failed: " + ex.Message;
            status.Text = "Submit failed.";
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
        status.Text = outcome.Ok ? "Verify complete." : "Verify failed.";
        if (outcome.Count > 1)
            MessageBox.Show(this, $"WARN: count={outcome.Count} > 1 for consumer_number {consumerNumber}", "Verify warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        onVerifyComplete?.Invoke(record, outcome);
    }

    /// <summary>T3: debounced signature-duplicate check at attach time.</summary>
    void CheckSignatureDuplicates()
    {
        var pathA = SignToPng(beneficiary);
        var pathB = SignToPng(representative);
        var duplicate = pathA != null && pathB != null && ComputeSha256(pathA) == ComputeSha256(pathB);
        this.Dispatcher.Invoke(() =>
        {
            if (duplicate)
                status.Text = "Duplicate signature: beneficiary and vendor representative signatures are byte-identical. Draw two different signatures.";
        });
    }

    /// <summary>W1: SHA-256 of a file for the photo uniqueness gate.</summary>
    static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
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
        var width = Math.Max(1, (int)Math.Ceiling(canvas.ActualWidth > 0 ? canvas.ActualWidth : 400));
        var height = Math.Max(1, (int)Math.Ceiling(canvas.ActualHeight > 0 ? canvas.ActualHeight : 200));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(target));
        var tmp = Path.Combine(Path.GetTempPath(), $"insp-sign-{Guid.NewGuid():N}.png");
        using (var stream = File.Create(tmp)) png.Save(stream);
        return tmp;
    }
}



