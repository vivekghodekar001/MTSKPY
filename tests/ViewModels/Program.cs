using System.IO;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Presentation.Services;
using Mskpy.Presentation.ViewModels;

var passes = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passes++; Console.WriteLine("PASS " + label); }
var gateway = new FakeGateway(); var secrets = new FakeSecrets(); var desktop = new FakeDesktop();
var workflow = new ClientWorkflow(gateway);
// A3: the list is served from the store, so the test gets its own seeded store
// instead of whatever the live app happens to hold.
var storePath = Path.Combine(Path.GetTempPath(), $"vm-store-{Guid.NewGuid():N}.db");
using (var seed = new Mskpy.Infrastructure.InspectionStore(storePath))
{
    // Three rows on the session's own cpf ("sample") and two on another cpf, so
    // the D1 scope split can be checked.
    foreach (var name in new[] { "Record 119", "Record 5", "Record 7" })
        seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
            ConsumerNumber: "MT" + name.GetHashCode().ToString("X8"), CpfNo: "sample",
            VendorCode: "100038029", ConsumerName: name, Latitude: "19.525251", Longitude: "74.119873",
            InstallDate: "2026-08-01", ReasonToRevert: null, SurveyDurationMinutes: null,
            IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.New,
            LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
            SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
    foreach (var name in new[] { "Other 1", "Other 2" })
        seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
            ConsumerNumber: "MX" + name.GetHashCode().ToString("X8"), CpfNo: "02781310",
            VendorCode: "100038029", ConsumerName: name, Latitude: "19.525251", Longitude: "74.119873",
            InstallDate: "2026-08-01", ReasonToRevert: null, SurveyDurationMinutes: null,
            IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.New,
            LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
            SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
}
using var shell = new ShellViewModel(workflow, secrets, desktop, storePath: storePath);
await shell.Login.ExecuteAsync();
Check(shell.HasError && gateway.LoginCalls == 0, "empty username fails before contacting backend");
shell.Username = "sample"; await shell.Login.ExecuteAsync();
Check(shell.IsSignedIn && secrets.Reads == 2, "successful login clears view input at action time");
Check(!shell.GetType().GetProperties().Any(x => x.Name.Contains("Password")), "password is not a bindable view-model property");
await shell.OpenModule.ExecuteAsync(VerifiedNavigation.Modules[0]);
Check(shell.IsList && shell.Applications.Count == 3 && gateway.LoadCalls == 1, "list comes from the store with no display cap and no live fallback");
Check(shell.ListScope == "MyPending" && shell.Applications.All(x => x.Fields.Any(f => f.Label == "Cpf" && f.Value == "sample")),
    "the officer lands on their own pending queue by default");
Check(shell.MyPendingCount == 3 && shell.MyPendingChip.Contains("3") && shell.AllChip.Contains("5"),
    "the scope chips carry the counts of each set");
shell.ListScope = "All";
Check(shell.Applications.Count == 5, "the statewide set is one click away");
shell.ListScope = "MyPending";
Check(shell.Applications.Count == 3, "switching back to the officer's own queue restores it");
var reads = gateway.LoadCalls; shell.Search = "Record 119";
Check(shell.Applications.Count == 1 && gateway.LoadCalls == reads, "search filters locally without transmitting query keystrokes");
shell.SelectedApplication = shell.Applications[0]; shell.ViewDetails.Execute(null);
Check(shell.IsDetails && shell.Fields.Any(x => x.Value == "Record 119"), "details are selected through commands and bindings");
shell.Navigate.Execute("Diagnostics"); shell.CopyDiagnostics.Execute(null);
Check(desktop.Copied == "Response fields only", "diagnostics command copies structural information only");
gateway.Pending = new TaskCompletionSource<IReadOnlyList<ApplicationDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);
var loading = shell.OpenModule.ExecuteAsync(VerifiedNavigation.Modules[0]);
Check(shell.IsBusy && shell.Cancel.CanExecute(null), "pending network operation exposes cancellation state");
shell.SignOut.Execute(null);
gateway.Pending.SetResult(gateway.Records);
await loading;
Check(shell.IsSignedOut && shell.Applications.Count == 0 && !shell.IsBusy, "sign-out cancels and suppresses stale list results");
shell.Username = "sample"; await shell.Login.ExecuteAsync(); gateway.Pending = null; gateway.Expire = true;
await shell.OpenModule.ExecuteAsync(VerifiedNavigation.Modules[0]);
Check(shell.IsSignedOut && shell.Error.Contains("expired"), "expired session clears navigation and requests sign-in");
gateway.Expire = false; await shell.Login.ExecuteAsync(); var beforeInvalidModule = gateway.LoadCalls;
var blocked = false; try { await workflow.LoadAsync("invented-module", CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
Check(blocked && gateway.LoadCalls == beforeInvalidModule, "unregistered modules cannot create undocumented backend requests");
// The store is the local cache of the statewide harvest, not session state: a
// sign-out must not leave the next sign-in with a dead store.
await shell.OpenModule.ExecuteAsync(VerifiedNavigation.Modules[0]);
Check(shell.Applications.Count == 3, "the harvested store survives a sign-out and is still listed after signing back in");
const string jsrSource = """{"consumer_number":"SAMPLE-JSR","consumer_name":"Synthetic record","wf_action_id":122}""";
var jsrDocument = new Mskpy.Infrastructure.JsrDocuments().FromRecord(jsrSource);
var drafts = new FakeJsrDocuments(jsrDocument);
var jsrInteractions = new FakeJsrInteractions();
using var jsr = new JsrViewModel(jsrDocument, new JsrWorkflow(drafts), jsrInteractions);
JsrFieldViewModel Field(string key) => jsr.Fields.Single(f => f.Key == key);
Field("source").Value = "Well";
Check(Field("sourceRailing").IsVisible, "JSR well selection reveals the original guard question");
Field("source").Value = "Canal";
Check(!Field("sourceRailing").IsVisible && Field("vendorRepName").IsVisible, "JSR conditional visibility follows source and workflow action");
Field("mobileNetwork").Value = "No"; jsr.Validate.Execute(null);
Check(!Field("__networks").HasError && !Field("__networks").IsVisible, "JSR does not demand provider selection when mobile network is unavailable");
Field("mobileNetwork").Value = "Yes"; jsr.Validate.Execute(null);
Check(Field("__networks").HasError && Field("__networks").IsVisible, "JSR network choices appear and validate only when applicable");
jsr.Networks.Single(n => n.Name == "Jio").IsSelected = true;
Check(!Field("__networks").HasError, "JSR edits refresh conditional validation without a network call");
await jsr.ChoosePhoto.ExecuteAsync(jsr.Photos[0]);
Check(jsr.Photos[0].HasPhoto && jsr.IsDirty, "JSR photo command updates its explicit role and dirty state");
await jsr.SaveDraft.ExecuteAsync();
Check(drafts.Saves == 1 && drafts.Saved!.SourceDocumentJson == jsrSource && !jsr.IsDirty, "JSR draft save delegates to application port and preserves original JSON");
await jsr.ValidateAndSave.ExecuteAsync();
Check(drafts.Saves == 1 && jsr.HasValidationMessages, "invalid JSR does not save a claimed validated form");
jsr.RemovePhoto.Execute(jsr.Photos[0]);
Check(!jsr.Photos[0].HasPhoto && jsr.IsDirty, "JSR remove-photo command clears the role without altering other answers");
// J5: the shell rebuilds the index from the harvest store and re-arms the
// walk from the beginning of the new list.
{
    var indexDir = Path.Combine(Path.GetTempPath(), $"vm-index-{Guid.NewGuid():N}");
    Directory.CreateDirectory(indexDir);
    var indexFile = Path.Combine(indexDir, "index.tsv");
    var cursorFile = Path.Combine(indexDir, "harvester-cursor.txt");
    // The old file: one consumer the store knows nothing about.
    File.WriteAllLines(indexFile, new[] { "MTOLD0000000001\t02781310" });
    // A cursor left over from an earlier walk - the rebuild must drop it.
    File.WriteAllText(cursorFile, "9999");
    var index = new Mskpy.Infrastructure.InspectionIndex(indexFile);
    // A dead host: the rebuild re-arms the walk, and the walk must not be
    // able to reach the production service from a test.
    using var rebuilt = new ShellViewModel(workflow, secrets, desktop, index, null, null, null,
        "http://127.0.0.1:1", indexFile, storePath);
    Check(rebuilt.IndexConsumers == 1 && rebuilt.IndexPairs == 1 && rebuilt.IndexCpfs == 1,
        "the shell reports what the index file holds");
    rebuilt.RebuildIndexCommand.Execute(null);
    Check(!rebuilt.HasError && rebuilt.Status.Contains("Index rebuilt"), "the rebuild reports what it did");
    // The store holds five rows over two cpfs; the old file adds one consumer
    // the store has not reached. The rebuild is the union of the two.
    Check(File.ReadAllLines(indexFile).Length == 6, "the rebuilt index holds the store's pairs and the file's own");
    Check(rebuilt.IndexConsumers == 6 && rebuilt.IndexPairs == 6 && rebuilt.IndexCpfs == 2,
        "the shell reports the rebuilt population");
    Check(!File.Exists(cursorFile) || File.ReadAllText(cursorFile).Trim() != "9999",
        "the rebuild drops the walk cursor, so the new list is walked from 0");
    try { Directory.Delete(indexDir, true); } catch { }
}

Console.WriteLine($"{passes} view-model/application checks passed. No network requests.");

sealed class FakeSecrets : ISecretInput { public int Reads; public string ReadAndClear() { Reads++; return "synthetic-password"; } }
sealed class FakeDesktop : IDesktopActions
{
    public string? Copied; public void CopyDiagnostics(string text) => Copied = text;
    public Task OpenInspectionAsync(string? json, CancellationToken ct) => Task.CompletedTask;
    public Task OpenJsrAsync(string? json, CancellationToken ct) => Task.CompletedTask;
}
sealed class FakeGateway : IClientGateway
{
    public int LoginCalls, LoadCalls; public bool Expire;
    public AccountProfile? Profile { get; private set; }
    public string Diagnostics => "Response fields only";
    public TaskCompletionSource<IReadOnlyList<ApplicationDocument>>? Pending;
    public IReadOnlyList<ApplicationDocument> Records = Enumerable.Range(0, 120).Select(i => new ApplicationDocument("sample-" + i, "Record " + i, [new DisplayField("Name", "Record " + i)], "{}")).ToArray();
    public Task<AccountProfile> LoginAsync(LoginRequest request, CancellationToken ct) { LoginCalls++; Profile = new(request.Username, request.Role, request.Scheme, []); return Task.FromResult(Profile); }
    public Task<IReadOnlyList<ApplicationDocument>> GetApplicationsAsync(string module, CancellationToken ct) { LoadCalls++; if (Expire) { SignOut(); throw new SessionExpiredException(); } return Pending?.Task ?? Task.FromResult(Records); }
    public void SignOut() => Profile = null;
}
sealed class FakeJsrDocuments(JsrDocument document) : IJsrDocuments
{
    public int Saves; public JsrDraft? Saved;
    public JsrDocument FromRecord(string json) => document;
    public Task<JsrDocument> OpenAsync(string path, CancellationToken ct) => Task.FromResult(document);
    public Task SaveAsync(string path, JsrDraft draft, CancellationToken ct) { Saves++; Saved = draft; return Task.CompletedTask; }
}
sealed class FakeJsrInteractions : IJsrInteractions
{
    public string? ChooseDraftPath() => "synthetic-draft-path";
    public Task<SelectedPhoto?> ChoosePhotoAsync(string title, CancellationToken ct) => Task.FromResult<SelectedPhoto?>(new(new PhotoReference("C:\\synthetic.jpg", 1, "synthetic"), null));
    public Task<SelectedPhoto> RestorePhotoAsync(PhotoReference photo, CancellationToken ct) => Task.FromResult(new SelectedPhoto(photo, null));
}
