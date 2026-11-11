using System.Text.Json;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Infrastructure;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
const string source = """{"consumer_number":"SAMPLE-JSR","consumer_name":"Synthetic source","wf_action_id":122,"originalNull":null,"unknownSourceField":{"retained":true}}""";
var documents = new JsrDocuments();
var workflow = new JsrWorkflow(documents);
var document = documents.FromRecord(source);
Check(document.Context.IsWorkflow122 && document.Draft.Answers.Count == 19, "original workflow action controls vendor-name visibility and all 19 initial fields exist");
Check(!documents.FromRecord(source.Replace(":122", ":\"122\"")).Context.IsWorkflow122, "workflow flag preserves the original numeric comparison");
Check(JsrForm.Fields.First(x => x.Key == "pumpHead").Options!.SequenceEqual(new[] { "Select", "30", "50", "70", "100" }), "pump-head option values match the recovered source");
var answers = new Dictionary<string, string>(JsrForm.InitialValues())
{
    ["source"] = "Canal",
    ["sourceWidth"] = "10",
    ["sourceDepth"] = "20",
    ["pumpHead"] = "50",
    ["landUnderShadow"] = "No",
    ["shadowFreeSpace"] = "100",
    ["mobileNetwork"] = "No",
    ["distanceFromLTLine"] = "200",
    ["liveAgConnection"] = "No",
    ["anyPDConnection"] = "No",
    ["alternateMobile"] = "0000000000",
    ["benPresent"] = "Yes",
    ["remark"] = "Synthetic test only"
};
var roles = JsrForm.PhotoRoles.ToDictionary(x => x.Key, _ => new PhotoReference("C:\\synthetic.jpg", 1, "synthetic"));
JsrDraft Draft() => new(source, new Dictionary<string, string>(answers), [], roles);
Check(workflow.Validate(Draft()).Count == 0, "no network provider is required when mobile network is No");
answers["mobileNetwork"] = "Yes";
Check(workflow.Validate(Draft()).ContainsKey("networks"), "available network requires at least one original provider");
Check(!workflow.Validate(Draft() with { SelectedNetworks = ["Jio"] }).ContainsKey("networks"), "selecting an original provider clears the conditional error");
answers["mobileNetwork"] = "No"; answers["source"] = "Well";
Check(workflow.Validate(Draft()).ContainsKey("sourceRailing"), "well guard selection is required for Well only");
answers["sourceRailing"] = "Yes"; answers["liveAgConnection"] = "Yes";
Check(workflow.Validate(Draft()).ContainsKey("existingLiveConsNo"), "live agricultural connection requires consumer number");
answers["existingLiveConsNo"] = "000000000000"; answers["benPresent"] = "No";
var absent = workflow.Validate(Draft());
Check(absent.ContainsKey("presentPersonName") && absent.ContainsKey("relationWithBen"), "absent beneficiary requires the present person's identity and relation");
answers["presentPersonName"] = "Synthetic person"; answers["relationWithBen"] = "Brother";
Check(workflow.Validate(Draft()).Count == 0, "completed conditional form validates without invented location evidence");
var wrongRoles = Draft() with { Photos = new Dictionary<string, PhotoReference> { ["x"] = roles.First().Value, ["y"] = roles.First().Value, ["z"] = roles.First().Value } };
Check(workflow.Validate(wrongRoles).ContainsKey("photos"), "three arbitrary attachments do not satisfy three named photo roles");
var hidden = JsrForm.Fields.First(x => x.Key == "vendorRepName");
Check(JsrForm.IsVisible(hidden, answers, true) && !JsrForm.IsVisible(hidden, answers, false), "vendor representative field follows workflow action, not guessed account role");
var refusedArray = false; try { documents.FromRecord("[" + source + "]"); } catch (InvalidDataException) { refusedArray = true; }
Check(refusedArray, "application arrays are not silently reduced to their first record");

var folder = Path.Combine(Path.GetTempPath(), "mskpy-jsr-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var path = Path.Combine(folder, "draft.json");
    var draft = Draft() with { Photos = new Dictionary<string, PhotoReference>() };
    await workflow.SaveDraftAsync(path, draft, CancellationToken.None);
    var reopened = await workflow.OpenAsync(path, CancellationToken.None);
    Check(reopened.Draft.SourceDocumentJson == source && reopened.Draft.Answers["presentPersonName"] == "Synthetic person", "draft round trip preserves source JSON verbatim and conditional answers");
    var originalBytes = await File.ReadAllBytesAsync(path);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var didCancel = false; try { await workflow.SaveDraftAsync(path, draft, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; }
    var bytesAfterCancellation = await File.ReadAllBytesAsync(path);
    Check(didCancel && originalBytes.SequenceEqual(bytesAfterCancellation) && Directory.GetFiles(folder, "*.tmp").Length == 0, "cancelled save preserves existing draft and cleans its temporary file");
    var malformed = JsonSerializer.Serialize(new { Format = "mskpy-jsr-draft-v1", Draft = new { SourceDocumentJson = source, Answers = new Dictionary<string, string?> { ["source"] = null }, SelectedNetworks = Array.Empty<string>(), Photos = new Dictionary<string, object>() } });
    var invalidPath = Path.Combine(folder, "invalid.json"); await File.WriteAllTextAsync(invalidPath, malformed);
    var refusedNull = false; try { await workflow.OpenAsync(invalidPath, CancellationToken.None); } catch (InvalidDataException) { refusedNull = true; }
    Check(refusedNull, "null answer values are rejected rather than normalized into claimed measurements");
    var missingPath = Path.Combine(folder, "does-not-exist.jpg");
    var missingPhoto = draft with { Photos = new Dictionary<string, PhotoReference> { ["waterSourcePhoto"] = new(missingPath, 1, "00") } };
    var refusedMissing = false; try { await workflow.SaveDraftAsync(path, missingPhoto, CancellationToken.None); } catch (FileNotFoundException) { refusedMissing = true; }
    var bytesAfterMissingPhoto = await File.ReadAllBytesAsync(path);
    Check(refusedMissing && originalBytes.SequenceEqual(bytesAfterMissingPhoto), "missing photo prevents replacement of a good saved draft");
}
finally
{
    foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
    Directory.Delete(folder);
}
Console.WriteLine($"{checks} JSR source-parity and draft integration checks passed. No network requests.");
Console.WriteLine("RESULT=success");
