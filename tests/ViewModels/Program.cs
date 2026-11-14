using System.IO;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Presentation.Services;
using Mskpy.Presentation.ViewModels;

var passes = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passes++; Console.WriteLine("PASS " + label); }
// D1: the sign-in log is captured in memory, so a suite never writes into the
// live profile and can assert the step names it produced.
var signInSteps = new List<string>();
Mskpy.Infrastructure.SignInLog.Sink = signInSteps.Add;
var gateway = new FakeGateway(); var secrets = new FakeSecrets(); var desktop = new FakeDesktop();
var workflow = new ClientWorkflow(gateway);
// A3: the list is served from the store, so the test gets its own seeded store
// instead of whatever the live app happens to hold.
var storePath = Path.Combine(Path.GetTempPath(), $"vm-store-{Guid.NewGuid():N}.db");
// C7: the walk starts at the sign-in, so the test's index is its own - a test
// must never walk the live one.
var uiIndexPath = Path.Combine(Path.GetTempPath(), $"vm-index-{Guid.NewGuid():N}.tsv");
File.WriteAllLines(uiIndexPath, new[] { "MTWALK00000001\t02781310" });
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
// D2: a dead host, so the delayed walk can start in a test without touching
// production. Nothing the shell reads comes from here.
using var shell = new ShellViewModel(workflow, secrets, desktop, indexTsvPath: uiIndexPath, storePath: storePath,
    inspectionHostOverride: "http://127.0.0.1:1");
await shell.Login.ExecuteAsync();
Check(shell.HasError && gateway.LoginCalls == 0, "empty username fails before contacting backend");
shell.Username = "sample"; await shell.Login.ExecuteAsync();
Check(shell.IsSignedIn && secrets.Reads == 2, "successful login clears view input at action time");
Check(!shell.GetType().GetProperties().Any(x => x.Name.Contains("Password")), "password is not a bindable view-model property");
Check(shell.IsList && shell.Applications.Count == 3, "C1: the sign-in lands on the officer's own list, with no way in before it");
await shell.OpenModule.ExecuteAsync(VerifiedNavigation.Modules[0]);
Check(shell.IsList && shell.Applications.Count == 3 && gateway.LoadCalls == 2, "list comes from the store with no display cap and no live fallback");
Check(shell.ListScope == "MyPending" && shell.Applications.All(x => x.Fields.Any(f => f.Label == "Cpf" && f.Value == "sample")),
    "the officer lands on their own pending queue by default");
Check(shell.MyPendingCount == 3 && shell.MyPendingChip.Contains("3") && shell.AllChip.Contains("5"),
    "the scope chips carry the counts of each set");
shell.ListScope = "All";
Check(shell.AllLoad != null, "D2: choosing All starts an on-demand load instead of reading the statewide set at sign-in");
await shell.AllLoad!;
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
    using var rebuilt = new ShellViewModel(workflow, secrets, desktop, index, null, null,
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

// C6/D1: the saved sign-in is used once, and a rejected password is not retried.
// D1 measured the real failure on this machine: the credential file decrypts,
// the account service accepts it, but a sign-in can take over seventy seconds
// while the app gave up at thirty-five - in both the saved and the typed path.
// So the tests now pin the steps, the messages, and what survives a failure.
{
    var credFile = Path.Combine(Path.GetTempPath(), $"vm-cred-{Guid.NewGuid():N}.bin");
    var credentials = new Mskpy.Presentation.Services.CredentialStore(credFile);
    credentials.Save("sample", "a-wrong-password");
    gateway.SignInFailure = new Mskpy.Infrastructure.SignInException("the reply",
        "The service declined the sign-in (step: the reply).", rejected: true);
    var loginCallsBefore = gateway.LoginCalls;
    using var saved = new ShellViewModel(workflow, secrets, desktop, storePath: storePath, credentials: credentials);
    await saved.AutoSignInAsync();
    Check(!saved.IsSignedIn, "D1: a rejected saved sign-in does not sign in");
    Check(saved.Username == "sample", "D1: the username is still filled in for the officer");
    Check(saved.Error.Contains("the reply") && saved.Error.Contains("declined"), "D1: the saved failure names the step, not one generic sentence");
    Check(gateway.LoginCalls == loginCallsBefore + 1, "D1: the rejected password is tried once and not again");
    Check(credentials.Load() == null, "D1: a rejected saved sign-in is forgotten");
    gateway.SignInFailure = null;
}

// D1: a timeout is not a rejection. The file stays, the officer is told which
// step stopped, and the same credentials typed by hand sign in.
{
    var credFile = Path.Combine(Path.GetTempPath(), $"vm-cred-{Guid.NewGuid():N}.bin");
    var credentials = new Mskpy.Presentation.Services.CredentialStore(credFile);
    credentials.Save("sample", "synthetic-password");
    gateway.SignInFailure = new Mskpy.Infrastructure.SignInException("waiting for the reply",
        "The account service did not answer within 120 seconds (step: waiting for the reply). Please try again.");
    using var slow = new ShellViewModel(workflow, secrets, desktop, indexTsvPath: uiIndexPath, storePath: storePath,
        inspectionHostOverride: "http://127.0.0.1:1", credentials: credentials);
    await slow.AutoSignInAsync();
    Check(!slow.IsSignedIn && slow.Error.Contains("waiting for the reply"), "D1: a saved sign-in that timed out says which step stopped");
    Check(slow.Username == "sample", "D1: the username survives a timeout");
    Check(credentials.Load() is not null, "D1: a timeout keeps the saved sign-in for the next launch");
    Check(!slow.IsBusy && slow.Login.CanExecute(null), "D1: the form is ready for the same credentials after a timeout");
    Check(signInSteps.Any(l => l.Contains("client ready")) && signInSteps.Any(l => l.Contains("failed at waiting for the reply")),
        "D1: login.log names each step and the step that stopped");
    gateway.SignInFailure = null;
    await slow.Login.ExecuteAsync();
    Check(slow.IsSignedIn, "D1: the same credentials typed by hand sign in - the saved sign-in was not the broken part");
}

// D1: a saved file this Windows account cannot open is named at the unprotect
// step and forgotten; it cannot be retried into working.
{
    var credFile = Path.Combine(Path.GetTempPath(), $"vm-cred-{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(credFile, [1, 2, 3, 4, 5, 6, 7, 8]);
    var credentials = new Mskpy.Presentation.Services.CredentialStore(credFile);
    var loaded = credentials.LoadWithStep();
    Check(loaded.Found && !loaded.Ok && loaded.Step == "unprotect", "D1: an unreadable saved sign-in reports the unprotect step");
    using var broken = new ShellViewModel(workflow, secrets, desktop, storePath: storePath, credentials: credentials);
    await broken.AutoSignInAsync();
    Check(!broken.IsSignedIn && broken.Error.Contains("unprotect"), "D1: the officer is told the file could not be decrypted, not that the password is wrong");
    Check(!File.Exists(credFile), "D1: an unreadable saved sign-in is forgotten");
}

// D1: the saved sign-in waits beside the form, not in front of it. A typed
// sign-in takes over, and only one attempt ends up signed in.
{
    var credFile = Path.Combine(Path.GetTempPath(), $"vm-cred-{Guid.NewGuid():N}.bin");
    var credentials = new Mskpy.Presentation.Services.CredentialStore(credFile);
    credentials.Save("sample", "synthetic-password");
    var before = gateway.LoginCalls;
    gateway.PendingLogin = new TaskCompletionSource<AccountProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var open = new ShellViewModel(workflow, secrets, desktop, indexTsvPath: uiIndexPath, storePath: storePath,
        inspectionHostOverride: "http://127.0.0.1:1", credentials: credentials);
    var automatic = open.AutoSignInAsync();
    Check(!open.IsBusy && open.Login.CanExecute(null), "D1: the form stays usable while the saved sign-in is waiting");
    open.Username = "sample";
    var typed = open.Login.ExecuteAsync();
    Check(open.IsBusy, "D1: a typed sign-in owns the busy state");
    gateway.PendingLogin.TrySetResult(new AccountProfile("sample", "Employee", "ALL", []));
    gateway.PendingLogin = null;
    await typed;
    await automatic;
    Check(open.IsSignedIn && gateway.LoginCalls == before + 2, "D1: the typed sign-in takes over and signs in");
}
// D2: the landing list is the officer's own queue, read on its own, and it is
// interactive before the walk begins. A statewide store this size used to be
// read in full to show three rows, which is what made the sign-in feel broken.
{
    var bigStore = Path.Combine(Path.GetTempPath(), $"vm-store-big-{Guid.NewGuid():N}.db");
    using (var seed = new Mskpy.Infrastructure.InspectionStore(bigStore))
    {
        for (var i = 0; i < 4997; i++)
            seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
                ConsumerNumber: $"MB{i:D7}", CpfNo: $"02{i % 1000:D6}", VendorCode: "100038029",
                ConsumerName: "Statewide " + i, Latitude: "19.525251", Longitude: "74.119873",
                InstallDate: "2026-08-01", ReasonToRevert: null, SurveyDurationMinutes: null,
                IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.New,
                LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
                SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
        for (var i = 0; i < 3; i++)
            seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
                ConsumerNumber: $"MMINE{i:D6}", CpfNo: "sample", VendorCode: "100038029",
                ConsumerName: "Mine " + i, Latitude: "19.525251", Longitude: "74.119873",
                InstallDate: "2026-08-01", ReasonToRevert: null, SurveyDurationMinutes: null,
                IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.New,
                LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
                SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
    }

    // Three consecutive cold starts: the list is on screen in under two
    // seconds each time, and the walk has not begun inside that window.
    var runtimes = new List<double>();
    for (var run = 0; run < 3; run++)
    {
        using var timed = new ShellViewModel(workflow, secrets, desktop,
            indexTsvPath: uiIndexPath, storePath: bigStore, inspectionHostOverride: "http://127.0.0.1:1")
        { Username = "sample" };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await timed.Login.ExecuteAsync();
        var elapsed = clock.Elapsed.TotalMilliseconds;
        runtimes.Add(elapsed);
        Check(timed.IsList && timed.Applications.Count == 3,
            $"D2: run {run + 1} lands on the officer's three rows, not the statewide set");
        Check(!timed.IsWalking, $"D2: run {run + 1} shows the list before the walk starts");
        Check(timed.AllChip.Contains("5000") && timed.Applications.All(x => x.Fields.Any(f => f.Label == "Cpf" && f.Value == "sample")),
            $"D2: run {run + 1} carries the statewide count on its chip without reading the statewide rows");
    }
    Check(runtimes.All(ms => ms < 2000),
        $"D2: the interactive list is under 2 s on three consecutive cold starts (worst {runtimes.Max():0} ms)");

    // All Maharashtra is read only when the officer chooses it, and the chip can
    // say the count while the rows are still loading.
    using var onDemand = new ShellViewModel(workflow, secrets, desktop,
        indexTsvPath: uiIndexPath, storePath: bigStore, inspectionHostOverride: "http://127.0.0.1:1")
    { Username = "sample" };
    await onDemand.Login.ExecuteAsync();
    Check(onDemand.AllLoad == null, "D2: the statewide set is not read at sign-in");
    onDemand.ListScope = "All";
    Check(onDemand.Applications.Count == 0 && onDemand.AllChip.Contains("5000"),
        "D2: choosing All keeps the count on the chip while the rows load");
    await onDemand.AllLoad!;
    Check(onDemand.Applications.Count == 5000, "D2: All Maharashtra fills on demand");
    onDemand.ListScope = "MyPending";
    Check(onDemand.Applications.Count == 3, "D2: the officer's queue comes back without another statewide read");

    // The walk is armed, not started: it begins once the list has had the screen.
    Check(!onDemand.IsWalking, "D2: the walk is not running while the officer reads the list");
    var walking = false;
    for (var waited = 0; waited < 80 && !walking; waited++)
    {
        await Task.Delay(100);
        walking = onDemand.IsWalking;
    }
    Check(walking, $"D2: the walk starts by itself after the list has had the screen ({ShellViewModel.HarvestStartDelayMs} ms)");
}
// E5: a Blocked row lands in Waiting on SDO and not in Needs redo.
{
    using var seed = new Mskpy.Infrastructure.InspectionStore(storePath);
    seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
        ConsumerNumber: "MBLOCKED", CpfNo: "sample", VendorCode: "V", ConsumerName: "blocked one",
        Latitude: "1", Longitude: "2", InstallDate: "2026-08-01",
        ReasonToRevert: null, SurveyDurationMinutes: null,
        IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.Blocked,
        LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
        SubmittedDataPath: null, PhotoPaths: null, SignPaths: null,
        BlockedReason: "state", BlockedAt: DateTime.UtcNow));
    shell.ShowBlocked.Execute(null);
    Check(shell.BlockedRecords.Count == 1, "E5: a seeded Blocked row appears in Waiting on SDO");
    shell.ShowNeedsRedo.Execute(null);
    Check(shell.NeedsRedoRecords.Count == 0, "E5: a blocked row does not appear in Needs redo");
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
    public TaskCompletionSource<AccountProfile>? PendingLogin;
    public IReadOnlyList<ApplicationDocument> Records = Enumerable.Range(0, 120).Select(i => new ApplicationDocument("sample-" + i, "Record " + i, [new DisplayField("Name", "Record " + i)], "{}")).ToArray();
    public Exception? SignInFailure;
    public Task<AccountProfile> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        LoginCalls++;
        if (PendingLogin != null) return PendingLogin.Task;
        if (SignInFailure != null) return Task.FromException<AccountProfile>(SignInFailure);
        Profile = new(request.Username, request.Role, request.Scheme, []);
        return Task.FromResult(Profile);
    }
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
