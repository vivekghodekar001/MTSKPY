using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mskpy.Windows;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Presentation.Services;
using Mskpy.Presentation.ViewModels;
using Mskpy.Presentation.Views;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new App(); app.InitializeComponent();
        using var sample = JsonDocument.Parse("""
        {"consumer_number":"SAMPLE-ONLY","consumer_name":"Synthetic demonstration record","commision_date":"2026-01-01","pump_load":"5","pump_serial_no":"SAMPLE-PUMP","contrlr_serial_no":"SAMPLE-CONTROLLER","no_of_panels":10}
        """);
        var window = new InspectionDraftWindow(sample.RootElement);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(930, 790)); root.Arrange(new Rect(0, 0, 930, 790)); root.UpdateLayout();
        var nodes = Descendants(root).ToArray();
        Assert(nodes.OfType<RadioButton>().Count() == 46, "23 independent pairs of correctness choices");
        Assert(nodes.OfType<InkCanvas>().Count() == 2, "two independent signature canvases");
        Assert(nodes.OfType<Button>().Count(x => x.Content?.ToString() == "Choose JPEG…") == 3, "three explicit photo-role selectors");
        Assert(!nodes.OfType<Button>().Any(x => x.Content?.ToString()?.Contains("Submit", StringComparison.OrdinalIgnoreCase) == true), "offline editor has no live submission action");
        Directory.CreateDirectory(args[0]);
        var image = new RenderTargetBitmap(930, 790, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(args[0], "inspection-render.png"))) encoder.Save(file);
        var scroll = nodes.OfType<ScrollViewer>().First(); scroll.ScrollToEnd(); root.UpdateLayout();
        var lower = new RenderTargetBitmap(930, 790, 96, 96, PixelFormats.Pbgra32); lower.Render(root);
        var lowerEncoder = new PngBitmapEncoder(); lowerEncoder.Frames.Add(BitmapFrame.Create(lower));
        using (var file = File.Create(Path.Combine(args[0], "inspection-photos-render.png"))) lowerEncoder.Save(file);
        using var saved = JsonDocument.Parse("""
        {"format":"mskpy-offline-inspection-v1","sourceRecord":{"consumer_number":"SAMPLE-ONLY"},"vendorPersonName":"Synthetic representative","vendorPersonMobile":"0000000000","answers":{"pumpSerial":{"isOK":"N","remark":"Synthetic check"}},"photos":{"waterFlowPhoto":{"path":"\\\\untrusted.invalid\\share\\photo.jpg"}}}
        """);
        var restored = new InspectionDraftWindow(saved.RootElement);
        var restoredNodes = Descendants((FrameworkElement)restored.Content).ToArray();
        Assert(restoredNodes.OfType<RadioButton>().Any(x => x.GroupName == "pumpSerial" && x.Content?.ToString() == "Incorrect" && x.IsChecked == true), "saved individual check restored");
        Assert(restoredNodes.OfType<TextBlock>().Any(x => x.Text.Contains("0 local photo references restored")), "network image paths in imported drafts are not fetched");
        var jpegPath = Path.GetFullPath(Path.Combine(args[0], "synthetic-test.jpg"));
        var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 50, 100, 150 }, 3)));
        using (var output = File.Create(jpegPath)) jpeg.Save(output);
        using var localDraft = JsonDocument.Parse(JsonSerializer.Serialize(new { format = "mskpy-offline-inspection-v1", sourceRecord = new { consumer_number = "SAMPLE-ONLY" }, photos = new { waterFlowPhoto = new { path = jpegPath } } }));
        var localWindow = new InspectionDraftWindow(localDraft.RootElement);
        Assert(Descendants((FrameworkElement)localWindow.Content).OfType<TextBlock>().Any(x => x.Text.Contains("1 local photo references restored")), "genuine JPEG file restored with its photo-role mapping");
        var invalidPath = Path.GetFullPath(Path.Combine(args[0], "not-a-jpeg.jpg")); File.WriteAllText(invalidPath, "This is not image data");
        using var invalidDraft = JsonDocument.Parse(JsonSerializer.Serialize(new { format = "mskpy-offline-inspection-v1", sourceRecord = new { consumer_number = "SAMPLE-ONLY" }, photos = new { waterFlowPhoto = new { path = invalidPath } } }));
        var invalidWindow = new InspectionDraftWindow(invalidDraft.RootElement);
        Assert(Descendants((FrameworkElement)invalidWindow.Content).OfType<TextBlock>().Any(x => x.Text.Contains("0 local photo references restored")), "renamed non-image files are rejected");
        var desktop = new RenderDesktop(); var gateway = new RenderGateway();
        using var shell = new ShellViewModel(new ClientWorkflow(gateway), new RenderSecret(), desktop) { Username = "sample" };
        var main = new MainWindow { DataContext = shell };
        var shellRoot = (FrameworkElement)main.Content;
        foreach (var width in new[] { 740, 1100, 1500 })
        {
            shellRoot.Measure(new Size(width, 780)); shellRoot.Arrange(new Rect(0, 0, width, 780)); shellRoot.UpdateLayout();
            var render = new RenderTargetBitmap(width, 780, 96, 96, PixelFormats.Pbgra32); render.Render(shellRoot);
            var imageFile = new PngBitmapEncoder(); imageFile.Frames.Add(BitmapFrame.Create(render));
            using var stream = File.Create(Path.Combine(args[0], $"shell-login-{width}.png")); imageFile.Save(stream);
        }
        Assert(Descendants(shellRoot).OfType<System.Windows.Controls.PasswordBox>().Count() == 1, "MVVM login preserves a single unbound password input");
        shell.Login.ExecuteAsync().GetAwaiter().GetResult();
        shellRoot.Measure(new Size(1100, 780)); shellRoot.Arrange(new Rect(0, 0, 1100, 780)); shellRoot.UpdateLayout();
        var home = new RenderTargetBitmap(1100, 780, 96, 96, PixelFormats.Pbgra32); home.Render(shellRoot);
        var homeEncoder = new PngBitmapEncoder(); homeEncoder.Frames.Add(BitmapFrame.Create(home));
        using (var stream = File.Create(Path.Combine(args[0], "shell-home.png"))) homeEncoder.Save(stream);
        Assert(shell.IsHome && shell.Modules.Count == 1 && shell.Modules[0].Key == "inspection", "verified home inventory is bound to the shell view model");
        const string jsrSource = """{"consumer_number":"SAMPLE-JSR-ONLY","consumer_name":"Synthetic demonstration record","wf_action_id":122,"land_ownername":"Synthetic owner","land_survey_no":"SAMPLE-SURVEY","land_area":"10","land_village_name_c":"Synthetic village"}""";
        var jsrService = new Mskpy.Infrastructure.JsrDocuments();
        var jsrDocument = jsrService.FromRecord(jsrSource);
        using var jsr = new JsrViewModel(jsrDocument, new JsrWorkflow(jsrService), new RenderJsrInteraction());
        jsr.Fields.Single(f => f.Key == "source").Value = "Well";
        jsr.Fields.Single(f => f.Key == "mobileNetwork").Value = "Yes";
        jsr.Fields.Single(f => f.Key == "benPresent").Value = "No";
        var jsrWindow = new JsrWindow(jsr);
        var jsrRoot = (FrameworkElement)jsrWindow.Content;
        jsrRoot.Measure(new Size(1000, 900)); jsrRoot.Arrange(new Rect(0, 0, 1000, 900)); jsrRoot.UpdateLayout();
        SaveRender(jsrRoot, 1000, 900, Path.Combine(args[0], "jsr-summary.png"));
        var jsrScroll = (ScrollViewer)jsrWindow.FindName("SurveyScroll");
        jsrScroll.ScrollToVerticalOffset(720); jsrRoot.UpdateLayout();
        SaveRender(jsrRoot, 1000, 900, Path.Combine(args[0], "jsr-fields.png"));
        var jsrNodes = VisualDescendants(jsrRoot).ToArray();
        Assert(jsrNodes.OfType<ComboBox>().Any(c => c.Items.Cast<object>().Any(x => x.ToString() == "Shettale")), "JSR renders source-derived irrigation choices through data templates");
        Assert(jsrNodes.OfType<CheckBox>().Count() == 4, "JSR renders the four original mobile-network providers");
        Assert(jsrNodes.OfType<Button>().Count(b => b.Content?.ToString() == "Choose photo…") == 3, "JSR renders three named photo roles");
        Assert(jsrNodes.OfType<Button>().Any(b => b.Content?.ToString() == "Upload Survey" && !b.IsEnabled && b.Command == null), "unverified JSR upload cannot trigger a network submission");
        var sourceSelector = jsrNodes.OfType<ComboBox>().First(c => c.Items.Cast<object>().Any(x => x.ToString() == "Shettale"));
        sourceSelector.SelectedItem = "River"; jsrRoot.UpdateLayout();
        Assert(jsr.Fields.Single(f => f.Key == "source").Value == "River" && !jsr.Fields.Single(f => f.Key == "sourceRailing").IsVisible, "JSR selector updates its bound answer and dependent question visibility");
        Assert(sourceSelector.Template.FindName("PART_Popup", sourceSelector) is System.Windows.Controls.Primitives.Popup, "dark selection template retains the required native ComboBox popup part");
        var rejectedNetwork = false;
        try { new WpfJsrInteractions(() => null).RestorePhotoAsync(new PhotoReference("\\\\untrusted.invalid\\photo.jpg", 1, "test"), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejectedNetwork = true; }
        Assert(rejectedNetwork, "JSR imported network image paths are rejected before file access");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(jpegPath)));
        var selectedPhoto = new WpfJsrInteractions(() => null).RestorePhotoAsync(new PhotoReference(jpegPath, new FileInfo(jpegPath).Length, hash), CancellationToken.None).GetAwaiter().GetResult();
        Assert(selectedPhoto.Preview is BitmapImage preview && preview.IsFrozen && selectedPhoto.Reference.Sha256 == hash, "JSR restores a genuine JPEG into a frozen thumbnail with a verified file hash");
        Console.WriteLine("In-process WPF render and control checks completed using synthetic data. No windows were shown and no production requests were sent.");
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var node in Descendants(child)) yield return node;
    }
    static void Assert(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    static void SaveRender(FrameworkElement root, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}

sealed class RenderSecret : ISecretInput { public string ReadAndClear() => "synthetic-password"; }
sealed class RenderDesktop : IDesktopActions { public void CopyDiagnostics(string text) { } public Task OpenInspectionAsync(string? json, CancellationToken ct) => Task.CompletedTask; public Task OpenJsrAsync(string? json, CancellationToken ct) => Task.CompletedTask; }
sealed class RenderGateway : IClientGateway
{
    public AccountProfile? Profile { get; private set; }
    public string Diagnostics => "Synthetic render only";
    public Task<AccountProfile> LoginAsync(LoginRequest request, CancellationToken ct) { Profile = new(request.Username, request.Role, request.Scheme, []); return Task.FromResult(Profile); }
    public Task<IReadOnlyList<ApplicationDocument>> GetApplicationsAsync(string module, CancellationToken ct) => Task.FromResult<IReadOnlyList<ApplicationDocument>>([]);
    public void SignOut() => Profile = null;
}
sealed class RenderJsrInteraction : IJsrInteractions
{
    public string? ChooseDraftPath() => null;
    public Task<SelectedPhoto?> ChoosePhotoAsync(string title, CancellationToken ct) => Task.FromResult<SelectedPhoto?>(null);
    public Task<SelectedPhoto> RestorePhotoAsync(PhotoReference photo, CancellationToken ct) => throw new NotSupportedException();
}
