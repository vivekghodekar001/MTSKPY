using Mskpy.Domain;
using Mskpy.Windows;

namespace Mskpy.Application;

// Local draft format only. This is intentionally not a backend submission DTO.
public sealed record JsrDraft(string SourceDocumentJson, IReadOnlyDictionary<string, string> Answers,
    IReadOnlyList<string> SelectedNetworks, IReadOnlyDictionary<string, PhotoReference> Photos);
public sealed record JsrDocument(JsrContext Context, JsrDraft Draft);

public interface IJsrDocuments
{
    Task<JsrDocument> OpenAsync(string path, CancellationToken cancellationToken);
    JsrDocument FromRecord(string originalJson);
    Task SaveAsync(string path, JsrDraft draft, CancellationToken cancellationToken);
}

public sealed class JsrWorkflow(IJsrDocuments documents)
{
    public Task<JsrDocument> OpenAsync(string path, CancellationToken ct) => documents.OpenAsync(path, ct);
    public JsrDocument FromRecord(string sourceJson) => documents.FromRecord(sourceJson);
    public Task SaveDraftAsync(string path, JsrDraft draft, CancellationToken ct) => documents.SaveAsync(path, draft, ct);

    public IReadOnlyDictionary<string, string> Validate(JsrDraft draft)
    {
        var photoCount = JsrForm.PhotoRoles.Count(role => draft.Photos.ContainsKey(role.Key));
        var errors = SurveyRules.ValidateJsr(draft.Answers,
            draft.SelectedNetworks.Any(JsrForm.NetworkProviders.Contains), photoCount);
        foreach (var field in JsrForm.Fields.Where(f => !f.Key.StartsWith("__")))
            if (draft.Answers.GetValueOrDefault(field.Key, "").Length > field.MaxLength)
                errors[field.Key] = $"Enter no more than {field.MaxLength} characters.";
        return errors;
    }
}
