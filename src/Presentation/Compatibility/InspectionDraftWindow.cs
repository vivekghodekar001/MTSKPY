using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mskpy.Client;

namespace Mskpy.Windows;

public sealed class InspectionDraftWindow : Window
{
    readonly JsonElement record;
    readonly Dictionary<string, (RadioButton yes, RadioButton no, TextBox remark)> answers = new();
    readonly Dictionary<string, string> photos = new();
    readonly Dictionary<string, (Image image, TextBlock label)> photoViews = new();
    readonly Dictionary<string, JsonElement> fields;
    readonly TextBox vendorName = new(), vendorMobile = new();
    readonly InkCanvas beneficiary = new() { Background = Brushes.White, Height = 155 };
    readonly InkCanvas representative = new() { Background = Brushes.White, Height = 155 };
    readonly TextBlock status = new() { Margin = new Thickness(0, 14, 0, 0) };
    bool dirty;
    static readonly (string Key, string Label)[] Slots =
    [
        ("setupPhotoWithBen", "Complete solar water pumping system with beneficiary and vendor representative"),
        ("manualHandOverPhoto", "Handover of user manual to beneficiary"),
        ("waterFlowPhoto", "Water flow")
    ];

    public InspectionDraftWindow(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Select a single application object.");
        JsonElement? saved = null;
        if (source.TryGetProperty("format", out var format) && format.GetString() == "mskpy-offline-inspection-v1")
        {
            saved = source.Clone();
            if (!source.TryGetProperty("sourceRecord", out source) || source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The draft does not contain its source application.");
        }
        record = source.Clone();
        if (string.IsNullOrWhiteSpace(ApiClient.String(record, "consumer_number"))) throw new InvalidDataException("The selected file must contain one inspection application with a consumer_number field.");
        fields = source.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
        Title = "Pump inspection — offline draft"; Width = 960; Height = 840; MinWidth = 720; MinHeight = 600;
        Background = (Brush)System.Windows.Application.Current.FindResource("Bg");
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Segoe UI"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var outer = new DockPanel { Margin = new Thickness(26), Background = Background };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Label("Pump Inspection by Linestaff", 26));
        header.Children.Add(Label("Offline draft · no live submission", 14));
        DockPanel.SetDock(header, Dock.Top); outer.Children.Add(header);
        var bottom = new StackPanel(); bottom.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        AddButton(actions, "Validate draft", (_, _) => Validate());
        AddButton(actions, "Save draft…", (_, _) => SaveDraft());
        bottom.Children.Add(actions); DockPanel.SetDock(bottom, Dock.Bottom); outer.Children.Add(bottom);
        var body = new StackPanel(); var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; outer.Children.Add(scroll);
        var info = new StackPanel();
        foreach (var (key, title) in new[] { ("consumer_number", "Application No"), ("consumer_name", "Beneficiary"), ("commision_date", "Installation date") })
        {
            var value = InspectionFormSchema.Text(fields, key);
            info.Children.Add(Label(title + ": " + (value.Length == 0 ? "Not provided" : value), 16));
        }
        body.Children.Add(Card(info));
        body.Children.Add(Label("Installer details", 21));
        AddField(body, "Vendor person name", vendorName);
        AddField(body, "Vendor person mobile", vendorMobile);
        vendorName.TextChanged += (_, _) => dirty = true; vendorMobile.TextChanged += (_, _) => dirty = true;
        var tabs = new TabControl { Margin = new Thickness(0, 18, 0, 18), Background = Background, BorderThickness = new Thickness(0) };
        foreach (var part in new[] { "Part A", "Part B" })
        {
            var checks = new StackPanel { Margin = new Thickness(12) };
            foreach (var check in InspectionFormSchema.Checks.Where(x => x.Section == part)) AddCheck(checks, check);
            tabs.Items.Add(new TabItem { Header = part, Content = checks });
        }
        body.Children.Add(tabs);
        body.Children.Add(Label("Upload Site Photos", 21));
        foreach (var slot in Slots) AddPhotoSlot(body, slot.Key, slot.Label);
        body.Children.Add(Label("Signatures", 21));
        AddSignature(body, "Beneficiary signature", beneficiary);
        AddSignature(body, "Vendor representative signature", representative);
        body.Children.Add(Label("Location has not been captured in this offline editor. Recorded location data stays unchanged in the source record. This draft cannot be submitted to the live service.", 14));
        Content = outer;
        Closing += (_, e) => { if (dirty && MessageBox.Show(this, "Close without saving your latest changes?", "Unsaved draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true; };
        status.Text = "Review each check individually. Saving writes a local draft only.";
        if (saved.HasValue) Restore(saved.Value);
    }

    void AddCheck(Panel target, Mskpy.Client.InspectionCheck check)
    {
        var panel = new StackPanel(); panel.Children.Add(Label(check.Label, 17));
        var value = InspectionFormSchema.CheckValue(check, fields);
        panel.Children.Add(Label(value.Length > 0 ? value : "Not provided in source record", 15));
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        var yes = new RadioButton { Content = "Correct", GroupName = check.Key };
        var no = new RadioButton { Content = "Incorrect", GroupName = check.Key };
        choices.Children.Add(yes); choices.Children.Add(no); panel.Children.Add(choices);
        var remark = new TextBox { MaxLength = 100, ToolTip = "Required when incorrect", Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(Label("Remark", 13)); panel.Children.Add(remark);
        yes.Checked += (_, _) => dirty = true; no.Checked += (_, _) => dirty = true; remark.TextChanged += (_, _) => dirty = true;
        answers.Add(check.Key, (yes, no, remark)); target.Children.Add(Card(panel));
    }

    void AddPhotoSlot(Panel target, string key, string title)
    {
        var panel = new StackPanel(); panel.Children.Add(Label(title, 16));
        var image = new Image { Height = 125, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 8) };
        var filename = Label("No photo selected", 13); panel.Children.Add(filename); panel.Children.Add(image);
        photoViews[key] = (image, filename);
        var row = new WrapPanel();
        AddButton(row, "Choose JPEG…", (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "JPEG images|*.jpg;*.jpeg", Title = title };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                ValidateJpeg(picker.FileName);
                image.Source = Thumbnail(picker.FileName); photos[key] = picker.FileName; filename.Text = Path.GetFileName(picker.FileName); dirty = true;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException) { status.Text = "Photo could not be loaded: " + ex.Message; }
        });
        AddButton(row, "Remove", (_, _) => { photos.Remove(key); image.Source = null; filename.Text = "No photo selected"; dirty = true; });
        panel.Children.Add(row); target.Children.Add(Card(panel));
    }
    void AddSignature(Panel target, string title, InkCanvas canvas)
    {
        target.Children.Add(Label(title, 16));
        canvas.DefaultDrawingAttributes.Color = Colors.Black; canvas.DefaultDrawingAttributes.Width = canvas.DefaultDrawingAttributes.Height = 2.5;
        canvas.Strokes.StrokesChanged += (_, _) => dirty = true;
        target.Children.Add(canvas);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 18) };
        AddButton(actions, "Clear signature", (_, _) => canvas.Strokes.Clear()); target.Children.Add(actions);
    }
    List<string> Errors()
    {
        var checks = answers.ToDictionary(x => x.Key, x => new CheckAnswer(x.Value.yes.IsChecked == true ? "Y" : x.Value.no.IsChecked == true ? "N" : "U", x.Value.remark.Text));
        var errors = SurveyRules.ValidateInspection(checks).Select(x => InspectionFormSchema.Checks.First(c => c.Key == x.Key).Label + ": " + x.Value).ToList();
        if (string.IsNullOrWhiteSpace(vendorName.Text)) errors.Add("Enter the vendor person's name.");
        if (vendorMobile.Text.Trim().Length < 10) errors.Add("Enter the vendor person's mobile number (at least 10 characters).");
        foreach (var slot in Slots) if (!photos.TryGetValue(slot.Key, out var path) || !File.Exists(path)) errors.Add("Missing photo: " + slot.Label);
        if (beneficiary.Strokes.Count == 0) errors.Add("Beneficiary signature is missing.");
        if (representative.Strokes.Count == 0) errors.Add("Vendor representative signature is missing.");
        return errors;
    }
    void Validate()
    {
        var errors = Errors(); status.Text = errors.Count == 0 ? "Offline form checks passed. Location and live submission remain unverified." : $"{errors.Count} item(s) need attention:\n" + string.Join("\n", errors.Take(5));
    }
    void SaveDraft()
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Inspection draft JSON|*.json", FileName = "inspection-offline-draft.json", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var selectedPhotos = new Dictionary<string, object>();
            foreach (var pair in photos)
            {
                ValidateJpeg(pair.Value);
                using var file = File.OpenRead(pair.Value);
                selectedPhotos[pair.Key] = new { path = pair.Value, bytes = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(file)), mediaType = "image/jpeg" };
            }
            var draft = new
            {
                format = "mskpy-offline-inspection-v1",
                savedAt = DateTimeOffset.UtcNow,
                sourceRecord = record,
                vendorPersonName = vendorName.Text.Trim(),
                vendorPersonMobile = vendorMobile.Text.Trim(),
                answers = answers.ToDictionary(x => x.Key, x => new { isOK = x.Value.yes.IsChecked == true ? "Y" : x.Value.no.IsChecked == true ? "N" : "U", remark = x.Value.remark.Text }),
                photos = selectedPhotos,
                beneficiarySignaturePng = Signature(beneficiary),
                vendorSignaturePng = Signature(representative),
                beneficiaryStrokes = Strokes(beneficiary),
                vendorStrokes = Strokes(representative),
                locationCaptured = false,
                validationErrors = Errors(),
                liveSubmissionAvailable = false
            };
            File.WriteAllText(picker.FileName, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
            dirty = false; status.Text = "Local draft saved. It references the selected image files; keep those files with your draft. Nothing was submitted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { status.Text = "Draft could not be saved: " + ex.Message; }
    }
    void Restore(JsonElement draft)
    {
        vendorName.Text = ApiClient.String(draft, "vendorPersonName"); vendorMobile.Text = ApiClient.String(draft, "vendorPersonMobile");
        if (draft.TryGetProperty("answers", out var savedAnswers) && savedAnswers.ValueKind == JsonValueKind.Object)
            foreach (var pair in answers)
                if (savedAnswers.TryGetProperty(pair.Key, out var answer))
                {
                    var correctness = ApiClient.String(answer, "isOK"); pair.Value.yes.IsChecked = correctness == "Y"; pair.Value.no.IsChecked = correctness == "N";
                    pair.Value.remark.Text = ApiClient.String(answer, "remark");
                }
        // Restore only local paths; never fetch a network location from a draft file.
        if (draft.TryGetProperty("photos", out var savedPhotos) && savedPhotos.ValueKind == JsonValueKind.Object)
            foreach (var slot in Slots)
                if (savedPhotos.TryGetProperty(slot.Key, out var photo))
                {
                    var path = ApiClient.String(photo, "path");
                    if (path.Length < 3 || path[1] != ':' || !Path.IsPathFullyQualified(path)) continue;
                    try { ValidateJpeg(path); var image = Thumbnail(path); photos[slot.Key] = path; photoViews[slot.Key].image.Source = image; photoViews[slot.Key].label.Text = Path.GetFileName(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { }
                }
        foreach (var (name, canvas) in new[] { ("beneficiaryStrokes", beneficiary), ("vendorStrokes", representative) })
        {
            var encoded = ApiClient.String(draft, name); if (encoded.Length == 0 || encoded.Length > 4 * 1024 * 1024) continue;
            try { using var stream = new MemoryStream(Convert.FromBase64String(encoded)); canvas.Strokes = new StrokeCollection(stream); canvas.Strokes.StrokesChanged += (_, _) => dirty = true; }
            catch (Exception ex) when (ex is ArgumentException or FormatException or IOException) { }
        }
        dirty = false; status.Text = $"Draft reopened. {photos.Count} local photo references restored. Use Choose JPEG to review or replace each photo.";
    }
    static string? Strokes(InkCanvas canvas)
    {
        if (canvas.Strokes.Count == 0) return null;
        using var stream = new MemoryStream(); canvas.Strokes.Save(stream); return Convert.ToBase64String(stream.ToArray());
    }
    static BitmapImage Thumbnail(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 260;
        using (var stream = File.OpenRead(path)) { image.StreamSource = stream; image.EndInit(); image.Freeze(); }
        return image;
    }
    static string? Signature(InkCanvas canvas)
    {
        if (canvas.Strokes.Count == 0) return null;
        var target = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(canvas.ActualWidth)), Math.Max(1, (int)Math.Ceiling(canvas.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream(); png.Save(stream); return Convert.ToBase64String(stream.ToArray());
    }
    static void ValidateJpeg(string path)
    {
        var file = new FileInfo(path); if (file.Length > 20 * 1024 * 1024) throw new InvalidDataException("Select a JPEG smaller than 20 MB.");
        using var stream = file.OpenRead();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not JpegBitmapDecoder) throw new InvalidDataException("The selected file is not a genuine JPEG.");
        var frame = decoder.Frames[0]; if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidDataException("Select a photo below 40 megapixels.");
    }
    static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, Foreground = (Brush)System.Windows.Application.Current.FindResource("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 7) };
    static Border Card(UIElement child) => new() { Child = child, Background = (Brush)System.Windows.Application.Current.FindResource("Surface"), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 15) };
    static void AddField(Panel panel, string label, TextBox field) { panel.Children.Add(Label(label, 14)); field.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(field); }
    static void AddButton(Panel panel, string text, RoutedEventHandler click) { var button = new Button { Content = text, Margin = new Thickness(0, 0, 10, 0) }; button.Click += click; panel.Children.Add(button); }
}
