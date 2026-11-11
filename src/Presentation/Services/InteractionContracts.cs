namespace Mskpy.Presentation.Services;

public interface ISecretInput { string ReadAndClear(); }
public interface IDesktopActions
{
    Task OpenInspectionAsync(string? originalJson, CancellationToken cancellationToken);
    Task OpenJsrAsync(string? originalJson, CancellationToken cancellationToken);
    void CopyDiagnostics(string text);
}
