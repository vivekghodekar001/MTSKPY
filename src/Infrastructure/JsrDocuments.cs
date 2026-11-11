using System.Text.Json;
using Mskpy.Application;
using Mskpy.Domain;

namespace Mskpy.Infrastructure;

public sealed class JsrDocuments : IJsrDocuments
{
    const long MaximumBytes = 32 * 1024 * 1024;
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    sealed record StoredDraft(string Format, JsrDraft Draft);

    public async Task<JsrDocument> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Select a document smaller than 32 MB.");
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Select one application record or a saved JSR draft.");
        if (!document.RootElement.TryGetProperty("Format", out var format)) return FromRecord(text);
        if (format.GetString() != "mskpy-jsr-draft-v1") throw new InvalidDataException("This is not a supported JSR draft.");
        var stored = JsonSerializer.Deserialize<StoredDraft>(text) ?? throw new InvalidDataException("The draft could not be read.");
        if (stored.Draft is null || stored.Draft.SourceDocumentJson is null || stored.Draft.Answers is null || stored.Draft.SelectedNetworks is null || stored.Draft.Photos is null)
            throw new InvalidDataException("The draft is incomplete.");
        ValidateStructure(stored.Draft);
        var context = FromRecord(stored.Draft.SourceDocumentJson).Context;
        return new JsrDocument(context, stored.Draft);
    }

    public JsrDocument FromRecord(string originalJson)
    {
        using var document = JsonDocument.Parse(originalJson);
        var row = document.RootElement;
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("consumer_number", out var identity) || identity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(identity.GetString()))
            throw new InvalidDataException("Select one application object containing consumer_number. Application arrays require an explicit record selection.");
        string Text(string key) => row.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array) ? value.ToString() : "Not provided";
        bool action122 = row.TryGetProperty("wf_action_id", out var action) && action.ValueKind == JsonValueKind.Number && action.TryGetInt32(out var code) && code == 122;
        var beneficiary = new[] { ("consumer_name", "Name"), ("adhar_no", "Aadhar No"), ("beneficiary_mobile_no", "Mobile No"), ("gender", "Gender"), ("cons_cast_category", "Category"), ("is_pump_exist_y_n", "Existing pump") }
            .Select(f => new DisplayField(f.Item2, Text(f.Item1))).ToArray();
        var land = new[] { ("land_ownername", "Land owner"), ("land_survey_no", "Land survey number"), ("land_sub_survey_no", "Land sub-survey number"), ("land_area", "Land area"), ("land_village_name_c", "Village"), ("taluka_name", "Taluka"), ("district_name", "District"), ("status", "Status"), ("vendor_name", "Vendor"), ("irrigation_source", "Irrigation source"), ("pump_load", "Pump capacity") }
            .Select(f => new DisplayField(f.Item2, Text(f.Item1))).ToArray();
        return new JsrDocument(new JsrContext(identity.GetString()!, action122, beneficiary, land),
            new JsrDraft(originalJson, JsrForm.InitialValues(), [], new Dictionary<string, PhotoReference>()));
    }

    public async Task SaveAsync(string path, JsrDraft draft, CancellationToken cancellationToken)
    {
        // Validate structure without changing the original record or inventing inspection evidence.
        ValidateStructure(draft);
        FromRecord(draft.SourceDocumentJson);
        foreach (var pair in draft.Photos)
        {
            if (!JsrForm.PhotoRoles.Any(role => role.Key == pair.Key)) throw new InvalidDataException("The draft contains an unknown photo role.");
            var photo = pair.Value;
            if (photo.LocalPath.Length < 3 || photo.LocalPath[1] != ':' || !Path.IsPathFullyQualified(photo.LocalPath)) throw new InvalidDataException("Draft images must use local file paths.");
            await using var image = File.OpenRead(photo.LocalPath);
            if (image.Length > 20 * 1024 * 1024 || image.Length != photo.Bytes) throw new InvalidDataException("A selected photo changed. Select it again.");
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(image, cancellationToken));
            if (!hash.Equals(photo.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A selected photo changed. Select it again.");
        }
        var serialized = JsonSerializer.Serialize(new StoredDraft("mskpy-jsr-draft-v1", draft), Options);
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumBytes) throw new InvalidDataException("The draft exceeds 32 MB.");
        var absolute = Path.GetFullPath(path);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, serialized, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, absolute, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static void ValidateStructure(JsrDraft draft)
    {
        if (draft.SourceDocumentJson is null || draft.Answers is null || draft.SelectedNetworks is null || draft.Photos is null) throw new InvalidDataException("The draft is incomplete.");
        var known = JsrForm.InitialValues();
        if (draft.Answers.Any(pair => !known.ContainsKey(pair.Key) || pair.Value is null)) throw new InvalidDataException("The draft contains unknown or null form values.");
        if (draft.SelectedNetworks.Any(name => !JsrForm.NetworkProviders.Contains(name))) throw new InvalidDataException("The draft contains an unsupported network provider.");
        if (draft.Photos.Any(pair => !JsrForm.PhotoRoles.Any(role => role.Key == pair.Key) || pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.LocalPath) || string.IsNullOrWhiteSpace(pair.Value.Sha256) || pair.Value.Bytes < 1))
            throw new InvalidDataException("The draft contains an invalid photo reference.");
    }
}
