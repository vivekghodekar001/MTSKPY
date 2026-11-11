using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mskpy.Windows;
using Mskpy.Application;
using Mskpy.Infrastructure;
using Mskpy.Presentation.ViewModels;
using Mskpy.Presentation.Views;

namespace Mskpy.Presentation.Services;

public sealed class PasswordBoxInput(PasswordBox box) : ISecretInput
{
    public string ReadAndClear() { var value = box.Password; box.Clear(); return value; }
}
public sealed class WpfDesktopActions(Window owner) : IDesktopActions
{
    public Task OpenInspectionAsync(string? originalJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (originalJson == null)
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Application or inspection draft JSON|*.json", Title = "Open a local application or saved inspection draft" };
                if (picker.ShowDialog(owner) != true) return Task.CompletedTask;
                if (new FileInfo(picker.FileName).Length > 32 * 1024 * 1024) throw new InvalidDataException("Select a JSON file smaller than 32 MB.");
                originalJson = File.ReadAllText(picker.FileName);
            }
            using var document = JsonDocument.Parse(originalJson);
            new InspectionDraftWindow(document.RootElement) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { MessageBox.Show(owner, ex.Message, "Unable to open offline inspection", MessageBoxButton.OK, MessageBoxImage.Information); }
        return Task.CompletedTask;
    }
    public void CopyDiagnostics(string text) => Clipboard.SetText(text);
    public async Task OpenJsrAsync(string? originalJson, CancellationToken cancellationToken)
    {
        try
        {
            var workflow = new JsrWorkflow(new JsrDocuments());
            JsrDocument document;
            if (originalJson is null)
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Application or JSR draft JSON|*.json", Title = "Open one application or a saved JSR draft" };
                if (picker.ShowDialog(owner) != true) return;
                document = await workflow.OpenAsync(picker.FileName, cancellationToken);
            }
            else document = workflow.FromRecord(originalJson);
            JsrWindow? window = null;
            var viewModel = new JsrViewModel(document, workflow, new WpfJsrInteractions(() => window));
            await viewModel.RestorePhotosAsync(cancellationToken);
            window = new JsrWindow(viewModel) { Owner = owner };
            window.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        { MessageBox.Show(owner, exception.Message, "Unable to open JSR", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
