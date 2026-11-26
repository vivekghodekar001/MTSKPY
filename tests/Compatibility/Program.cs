using System.Net;
using System.Text;
using System.Text.Json;
using Mskpy.Windows;

var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
static string? RawUpdatedAt(string dbFile, string consumer)
{
    using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT updated_at FROM inspection_records WHERE consumer_number=$cn;";
    cmd.Parameters.AddWithValue("$cn", consumer);
    return cmd.ExecuteScalar() as string;
}
static int CountOccurrences(string haystack, string needle)
{
    int n = 0, at = 0;
    while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
    return n;
}
var handler = new FakeHandler();
using var client = new ApiClient(handler);
handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":true,\"user\":{\"loginId\":\"sample\",\"jwt\":\"test-token\"}}}";
await client.LoginAsync("sample", "A&B=123", "Employee", "SCHEME_MTSKPY");
Check(handler.Url == "https://mobileapp.mahadiscom.in/mskpy/SignIn/Employee", "employee endpoint matches APK");
Check(handler.SentBody == "loginId=sample&password=A%26B%3D123", "form credentials encoded without agency scheme");
Check(client.Session?.Scheme == "ALL", "employee session has all schemes");
Check(client.Session?.AccessToken == "test-token" && !client.LoginResponseShape.Contains("test-token"), "issued user token retained without appearing in diagnostics");
await client.LoginAsync("sample", "example", "Agency", "SCHEME_MSKVY_2");
Check(handler.SentBody.Contains("scheme=SCHEME_MSKVY_2"), "agency scheme supplied");
handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":true,\"accessToken\":\"outer-test-token\",\"user\":{\"loginId\":\"sample\"}}}";
await client.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY");
Check(client.Session?.AccessToken == "outer-test-token" && client.ServiceTokenLocation == "data.accessToken", "server-issued token in data wrapper recognized");
handler.Status = HttpStatusCode.Forbidden;
try { await client.LoginAsync("sample", "example", "Agency", "SCHEME_MTSKPY"); throw new Exception("Expected denial"); }
catch (InvalidOperationException) { Check(client.Session == null, "failed sign-in clears previous session"); }
handler.Status = HttpStatusCode.OK;
handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":true}}";
try { await client.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"); throw new Exception("Expected invalid response"); }
catch (InvalidOperationException) { Check(client.Session == null, "success without user is not accepted"); }
handler.Body = "<html>Temporary maintenance</html>";
try { await client.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"); throw new Exception("Expected format error"); }
catch (InvalidOperationException ex) { Check(!ex.Message.Contains("<html>"), "unexpected service body is not displayed"); }
handler.Status = HttpStatusCode.Redirect;
try { await client.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"); throw new Exception("Expected redirect error"); }
catch (InvalidOperationException ex) { Check(ex.Message.Contains("redirected"), "unexpected login redirect rejected"); }
handler.Status = HttpStatusCode.OK;
handler.Body = "{\"status\":\"SUCCESS\",\"token\":\"example-token\",\"vendorId\":\"sample\",\"meterData\":[]}";
await client.LoginAsync("sample", "example", "Agency", "SCHEME_MSKVY_2_TOD");
Check(handler.Url == "https://nvp.mahadiscom.in/vendor-meter-api-1.0-SNAPSHOT/api/v1/login" && handler.SentBody.Contains("\"vendorId\":\"sample\""), "TOD uses its separate recovered login contract");
client.SignOut(); Check(client.Session == null, "sign out clears session");

// D1: the sign-in names the step it stopped at, and keeps a saved sign-in when
// the failure was the service rather than the password.
{
    static async Task<T?> Capture<T>(Func<Task> action) where T : Exception
    { try { await action(); return null; } catch (T ex) { return ex; } }

    var trace = new List<string>();
    Mskpy.Infrastructure.SignInLog.Sink = trace.Add;
    try
    {
        using (var slow = new ApiClient(new SilentHandler(), TimeSpan.FromMilliseconds(150)))
        {
            var timeout = await Capture<Mskpy.Infrastructure.SignInException>(() => slow.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"));
            Check(timeout != null && timeout.Step == "waiting for the reply", "a saved sign-in that gets no answer names the step it stopped at");
            Check(timeout!.Message.Contains("did not answer within") && !timeout.Message.Contains("task was canceled", StringComparison.OrdinalIgnoreCase),
                "a sign-in timeout is said in plain words, not as a cancelled task");
            Check(!timeout.Rejected, "a timeout is not a rejected password, so the saved sign-in is kept");
            Check(trace.Any(x => x.Contains("POST issued")) && trace.Any(x => x.Contains("no answer within")),
                "the sign-in log records the POST and then the timeout, by name");
        }

        trace.Clear();
        using (var dead = new ApiClient(new DeadHandler()))
        {
            var unsent = await Capture<Mskpy.Infrastructure.SignInException>(() => dead.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"));
            Check(unsent != null && unsent.Step == "sending the sign-in", "a sign-in that cannot be sent names that step");
            Check(!unsent!.Rejected && trace.Any(x => x.Contains("failed before a reply")), "an unreachable service is logged and is not a refusal");
        }

        trace.Clear();
        handler.Status = HttpStatusCode.OK;
        handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":true,\"user\":{\"loginId\":\"sample\"}}}";
        using (var ok = new ApiClient(handler))
        {
            await ok.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY");
            Check(trace.Any(x => x.Contains("sign-in reply: HTTP 200")), "the reply status is logged with the sign-in");

            trace.Clear();
            handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":false}}";
            var refused = await Capture<Mskpy.Infrastructure.SignInException>(() => ok.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"));
            Check(refused != null && refused.Rejected && refused.Step == "the reply",
                "a refused password is a rejection, so the saved copy may be forgotten");

            handler.Status = HttpStatusCode.InternalServerError;
            handler.Body = "{\"message\":\"down\"}";
            var serverError = await Capture<Mskpy.Infrastructure.SignInException>(() => ok.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"));
            Check(serverError != null && !serverError.Rejected && serverError.Message.Contains("HTTP 500"),
                "a service error names the status and is not treated as a refused password");
        }
        handler.Status = HttpStatusCode.OK;
        handler.Body = "{\"hasErrors\":false,\"data\":{\"success\":true,\"user\":{\"loginId\":\"sample\",\"jwt\":\"test-token\"}}}";
    }
    finally { Mskpy.Infrastructure.SignInLog.Sink = null; }
}
var allCorrect = SurveyRules.InspectionChecks.ToDictionary(x => x.Field, _ => new CheckAnswer("Y", ""));
Check(SurveyRules.ValidateInspection(new Dictionary<string, CheckAnswer>()).Count == 23, "all 23 unreviewed inspection checks fail validation");
Check(SurveyRules.ValidateInspection(allCorrect).Count == 0, "individually completed correct checks pass validation");
allCorrect["pumpSerial"] = new CheckAnswer("N", "");
Check(SurveyRules.ValidateInspection(allCorrect).ContainsKey("pumpSerial"), "incorrect check requires an explanation");
allCorrect["pumpSerial"] = new CheckAnswer("N", "Serial does not match the source record");
Check(SurveyRules.ValidateInspection(allCorrect).Count == 0, "documented incorrect check is retained without blocking unrelated checks");
Check(SurveyRules.ValidateMilestone("Reviewed", 0) != null && SurveyRules.ValidateMilestone(new string('x', 65), 1) != null, "milestone validation requires photo evidence and bounded remarks");
Check(Mskpy.Infrastructure.GeoDistance.Meters(19, 74, 19, 74) == 0, "distance from a position to itself is zero");
var oneThousandthDegree = Mskpy.Infrastructure.GeoDistance.Meters(19, 74, 19.001, 74);
Check(oneThousandthDegree > 110 && oneThousandthDegree < 112, "distance to installation uses a spherical-earth metre scale");

// A3: the store must actually persist a record. The INSERT used to name fewer
// columns than it supplied values, so every write threw and the harvester
// swallowed it — the walk looked healthy while it stored nothing.
var storePath = Path.Combine(Path.GetTempPath(), $"mskpy-store-check-{Guid.NewGuid():N}.db");
try
{
    using var store = new Mskpy.Infrastructure.InspectionStore(storePath);
    store.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
        ConsumerNumber: "MT4420200801626", CpfNo: "02284251", VendorCode: "100038029",
        ConsumerName: "Store round trip", Latitude: "19.525251", Longitude: "74.119873",
        InstallDate: "2026-08-01", ReasonToRevert: null, SurveyDurationMinutes: null,
        IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.New,
        LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{\"a\":1}",
        SubmittedDataPath: "C:\\data.json", PhotoPaths: "[\"p.jpg\"]", SignPaths: "[\"s.png\"]"));
    var read = store.GetByConsumerNumber("MT4420200801626");
    Check(read != null, "store persists a harvested record");
    Check(read!.CpfNo == "02284251" && read.VendorCode == "100038029" && read.ConsumerName == "Store round trip",
        "store round trip keeps cpf, vendor and name");
    Check(read.SubmittedDataPath == "C:\\data.json" && read.PhotoPaths == "[\"p.jpg\"]" && read.SignPaths == "[\"s.png\"]",
        "store round trip keeps the submitted-data, photo and sign paths");
    Check(read.OriginalJson == "{\"a\":1}", "store round trip keeps the original JSON");
    Check(store.GetAll().Count == 1, "store lists the persisted record");
    store.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
        ConsumerNumber: "MT4420200801626", CpfNo: "02284251", VendorCode: "100038029",
        ConsumerName: "Store round trip twice", Latitude: null, Longitude: null,
        InstallDate: null, ReasonToRevert: null, SurveyDurationMinutes: null,
        IsFirstTime: false, Status: Mskpy.Infrastructure.RecordStatus.Accepted,
        LastServerMessage: "ok", LastServerMessageAt: null, OriginalJson: null,
        SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
    Check(store.GetAll().Count == 1 && store.GetByConsumerNumber("MT4420200801626")!.ConsumerName == "Store round trip twice",
        "store upserts a record it already holds instead of duplicating it");
    var preserved = store.GetByConsumerNumber("MT4420200801626")!;
    Check(preserved.OriginalJson == "{\"a\":1}", "a null original_json never overwrites the stored payload");
    Check(preserved.SubmittedDataPath == "C:\\data.json", "a null submitted_data_path never overwrites the attempt path");
    store.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
        ConsumerNumber: "MT4420200801626", CpfNo: "02284251", VendorCode: "100038029",
        ConsumerName: "Store round trip twice", Latitude: "19.525251", Longitude: "74.119873",
        InstallDate: "2026-08-01", ReasonToRevert: "returned", SurveyDurationMinutes: null,
        IsFirstTime: false, Status: Mskpy.Infrastructure.RecordStatus.Returned,
        LastServerMessage: null, LastServerMessageAt: null,
        OriginalJson: "{\"cpf\":\"02284251\",\"pump_load\":\"3\"}",
        SubmittedDataPath: "C:\\attempt\\data.json", PhotoPaths: "[\"p.jpg\"]", SignPaths: "[\"s.png\"]"));
    store.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
        ConsumerNumber: "MT4420200801626", CpfNo: "02284251", VendorCode: "100038029",
        ConsumerName: "Store round trip twice", Latitude: "19.525251", Longitude: "74.119873",
        InstallDate: "2026-08-01", ReasonToRevert: "returned", SurveyDurationMinutes: null,
        IsFirstTime: false, Status: Mskpy.Infrastructure.RecordStatus.Failed,
        LastServerMessage: "forced 500", LastServerMessageAt: null, OriginalJson: "",
        SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
    var afterFailedAttempt = store.GetByConsumerNumber("MT4420200801626")!;
    Check(afterFailedAttempt.OriginalJson != null && afterFailedAttempt.OriginalJson.Contains("\"cpf\":\"02284251\"") && afterFailedAttempt.OriginalJson.Contains("\"pump_load\":\"3\""),
        "a failed attempt keeps a parseable payload with cpf and pump_load");
    Check(afterFailedAttempt.SubmittedDataPath == "C:\\attempt\\data.json", "a failed attempt keeps its submitted_data_path");
    store.UpdateInstallerPair("MT4420200801626", "Saved Installer", "9000000011");
    var installerPair = store.GetInstallerPair("MT4420200801626");
    Check(installerPair is { Name: "Saved Installer", Mobile: "9000000011" },
        "the installer pair is saved on the consumer record");
    var withInstaller = store.GetByConsumerNumber("MT4420200801626")!;
    Check(withInstaller.OriginalJson != null && withInstaller.OriginalJson.Contains("\"cpf\":\"02284251\"")
            && withInstaller.OriginalJson.Contains("\"vendor_person_name\":\"Saved Installer\""),
        "saving the installer pair preserves the record payload");
    store.UpdateInstallerPair("MT4420200801626", "", "");
    Check(store.GetInstallerPair("MT4420200801626") == null,
        "clearing the installer pair removes it from the consumer record");
}
finally { try { File.Delete(storePath); } catch { } }

// A1: the vendor is the record's own vendor_code, in the header AND the body.
// There is no session vendor any more, so the two can never disagree.
var vendorDir = Path.Combine(Path.GetTempPath(), $"mskpy-vendor-check-{Guid.NewGuid():N}");
Directory.CreateDirectory(vendorDir);
try
{
    var photos = new List<string>();
    for (int i = 0; i < 3; i++)
    {
        var p = Path.Combine(vendorDir, $"photo{i}.jpg");
        File.WriteAllBytes(p, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, (byte)(i + 1) });
        photos.Add(p);
    }
    var signs = new List<string>();
    for (int i = 0; i < 2; i++)
    {
        var p = Path.Combine(vendorDir, $"sign{i}.png");
        File.WriteAllBytes(p, new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)(i + 1) });
        signs.Add(p);
    }

    var recordJson = JsonDocument.Parse("""
    {"consumer_number":"MT4420200801626","cpf_no":"02284251","vendor_code":"100038029","consumer_name":"Vendor check","latitude":"19.525251","longitude":"74.119873","inst_date":"2026-08-01","pump_load":"5 HP"}
    """);
    var record = Mskpy.Domain.InspectionRecord.FromJson(recordJson.RootElement);
    var capture = new CapturingHandler();
    var transport = new Mskpy.Infrastructure.MskpyInspectionTransport("02284251", vendorDir, capture, "https://vendor-check.invalid");
    using var gateway = new Mskpy.Infrastructure.InspectionGateway(transport, new Mskpy.Infrastructure.InspectionIndex());
    var workflow = new Mskpy.Infrastructure.InspectionWorkflow(gateway, new Mskpy.Infrastructure.InspectionMultipartBuilder(), new Mskpy.Infrastructure.LocationProvider(), vendorDir);

    var outcome = await workflow.SubmitAsync(record, photos, signs, null, null, null, null, false, null,
        CancellationToken.None, sessionLoginId: "02284251");

    Check(capture.VendorHeader == "100038029", "submit sends the record's vendor_code in the client-vendorCode header");
    Check(capture.BodyVendor == "100038029", "submit sends the record's vendor_code in the body");
    Check(capture.VendorHeader == capture.BodyVendor, "header vendor equals body vendor on the submit path");
    Check(outcome.Error == null, "submit passes the gates with the record's vendor");

    // The escape hatch: an explicit override replaces BOTH, so they still agree.
    var capture2 = new CapturingHandler();
    var transport2 = new Mskpy.Infrastructure.MskpyInspectionTransport("02284251", vendorDir, capture2, "https://vendor-check.invalid");
    using var gateway2 = new Mskpy.Infrastructure.InspectionGateway(transport2, new Mskpy.Infrastructure.InspectionIndex());
    var wf2 = new Mskpy.Infrastructure.InspectionWorkflow(gateway2, new Mskpy.Infrastructure.InspectionMultipartBuilder(), new Mskpy.Infrastructure.LocationProvider(), vendorDir, "OVERRIDE-1");
    await wf2.SubmitAsync(record, photos, signs, null, null, null, null, false, null,
        CancellationToken.None, sessionLoginId: "02284251");
    Check(capture2.VendorHeader == "OVERRIDE-1" && capture2.BodyVendor == "OVERRIDE-1",
        "a vendor override replaces the header and the body together");
    Check(capture2.VendorHeader == capture2.BodyVendor, "header vendor equals body vendor with an override in force");

    // A record with no CPF anywhere must stop locally, not reach the server
    // as an empty cpf field.
    var noCpfJson = JsonDocument.Parse("""
    {"consumer_number":"MT4420200801627","vendor_code":"100038029","consumer_name":"No cpf","latitude":"19.525251","longitude":"74.119873","inst_date":"2026-08-01","pump_load":"5 HP"}
    """);
    var noCpfRecord = Mskpy.Domain.InspectionRecord.FromJson(noCpfJson.RootElement);
    var capture3 = new CapturingHandler();
    var transport3 = new Mskpy.Infrastructure.MskpyInspectionTransport("02284251", vendorDir, capture3, "https://vendor-check.invalid");
    using var gateway3 = new Mskpy.Infrastructure.InspectionGateway(transport3, new Mskpy.Infrastructure.InspectionIndex());
    var wf3 = new Mskpy.Infrastructure.InspectionWorkflow(gateway3, new Mskpy.Infrastructure.InspectionMultipartBuilder(), new Mskpy.Infrastructure.LocationProvider(), vendorDir);
    var noCpfOutcome = await wf3.SubmitAsync(noCpfRecord, photos, signs, null, null, null, null, false, null,
        CancellationToken.None, sessionLoginId: "02284251");
    Check(noCpfOutcome.Error != null && noCpfOutcome.Error.Contains("Record CPF is missing"),
        "a record with no CPF is stopped by the local gate");
    Check(capture3.VendorHeader == null && capture3.BodyVendor == null,
        "a record with no CPF never reaches the server");
}
finally { try { Directory.Delete(vendorDir, true); } catch { } }

// J3: a row with a NULL seen set (written by an earlier build, before the
// seen set was stored) must be surfaced by the store-open backfill, and the
// chip count and the list predicate must then agree exactly.
{
    var j3Dir = Path.Combine(Path.GetTempPath(), $"mskpy-j3-{Guid.NewGuid():N}");
    Directory.CreateDirectory(j3Dir);
    var j3Db = Path.Combine(j3Dir, "j3.db");
    try
    {
        // Write the row the way an earlier build did: no seen set at all.
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={j3Db}"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = @"
                CREATE TABLE inspection_records (
                    consumer_number TEXT PRIMARY KEY,
                    cpf_no TEXT NOT NULL,
                    vendor_code TEXT,
                    consumer_name TEXT,
                    latitude TEXT,
                    longitude TEXT,
                    inst_date TEXT,
                    reason_to_revert TEXT,
                    survey_duration_minutes TEXT,
                    is_first_time INTEGER NOT NULL,
                    status INTEGER NOT NULL DEFAULT 0,
                    last_server_message TEXT,
                    last_server_message_at TEXT,
                    original_json TEXT,
                    submitted_data_path TEXT,
                    photo_paths TEXT,
                    sign_paths TEXT,
                    seen_cpfs TEXT,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO inspection_records
                (consumer_number, cpf_no, consumer_name, is_first_time, status, seen_cpfs, updated_at)
                VALUES ('MT4420200801626', '02284251', 'Legacy row', 1, 0, NULL, '2026-01-01T00:00:00.0000000Z');
            ";
            create.ExecuteNonQuery();
        }

        // Opening the store runs the backfill: the row must now be in the
        // queue of its own cpf, exactly as a freshly harvested row is.
        using (var store = new Mskpy.Infrastructure.InspectionStore(j3Db))
        {
            var healed = store.GetByConsumerNumber("MT4420200801626");
            Check(healed != null, "a row written by an earlier build is still readable");
            Check(healed!.SeenCpfs != null && healed.SeenCpfs.Contains("02284251"),
                "the backfill surfaces a row whose seen set was NULL");
            Check(store.IsSeenUnder("MT4420200801626", "02284251"),
                "the backfill puts the healed row in its own cpf's queue");
            Check(store.CountSeenUnder("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 1,
                "the chip counts the healed row");
            Check(store.CountSeenUnder("02284251") == 1,
                "the healed row is counted once, not twice");

            // Idempotent: a second backfill adds nothing.
            Check(store.BackfillSeenCpfs() == 0, "the backfill is idempotent");
            Check(store.CountSeenUnder("02284251") == 1,
                "the healed row is still counted once after a second backfill");
        }
    }
    finally { try { Directory.Delete(j3Dir, true); } catch { } }
}

// J3: the chip and the list must use the SAME predicate. A row seen under two
// cpfs is in both queues, and a row that has been accepted is in neither.
{
    var j3Dir = Path.Combine(Path.GetTempPath(), $"mskpy-j3b-{Guid.NewGuid():N}");
    Directory.CreateDirectory(j3Dir);
    var j3Db = Path.Combine(j3Dir, "j3b.db");
    try
    {
        using var store = new Mskpy.Infrastructure.InspectionStore(j3Db);
        using var row = JsonDocument.Parse("""
        {"consumer_number":"MT4420200801626","cpf_no":"02781310","consumer_name":"Both queues","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
        """);
        store.UpsertRows(new[] { row.RootElement }, "02284251");

        Check(store.CountSeenUnder("02284251") == 1,
            "the chip counts the row under the cpf that listed it");
        Check(store.CountSeenUnder("02781310") == 1,
            "the chip counts the row under the cpf that owns it");
        var seen = store.GetByConsumerNumber("MT4420200801626")!.SeenCpfs;
        Check(seen != null && seen.Contains("02284251") && seen.Contains("02781310"),
            "the row's seen set holds both cpfs");

        // An accepted row leaves both queues.
        store.SetStatus("MT4420200801626", Mskpy.Infrastructure.RecordStatus.Accepted);
        Check(store.CountSeenUnder("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 0,
            "an accepted row leaves the chip count");
        Check(store.CountSeenUnder("02781310", Mskpy.Infrastructure.RecordStatus.Accepted) == 0,
            "an accepted row leaves the other officer's chip count too");

        // The session snapshot is the server's current list: a historical
        // seen row absent from it leaves My pending on every PC.
        store.SetStatus("MT4420200801626", Mskpy.Infrastructure.RecordStatus.New);
        Check(store.ReplaceSessionList("02284251", new[] { "MT4420200801626" }) == 0,
            "the first session snapshot removes nothing");
        Check(store.CountSessionList("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 1,
            "the session snapshot counts the listed consumer");
        Check(store.ReplaceSessionList("02284251", Array.Empty<string>()) == 1,
            "a consumer absent from the fresh session list is removed from the snapshot");
        Check(store.HasSessionList("02284251") && store.CountSessionList("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 0,
            "an empty session snapshot still counts as fetched, so historical rows stay out");
        using var completedRow = JsonDocument.Parse("""
        {"consumer_number":"MT4420200801626","cpf_no":"02781310","consumer_name":"Both queues","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01","beneficiary_sign_file_name":"sign.png"}
        """);
        store.UpsertRows(new[] { completedRow.RootElement }, "02284251");
        store.ReplaceSessionList("02284251", new[] { "MT4420200801626" });
        Check(store.GetByConsumerNumber("MT4420200801626")!.Status == Mskpy.Infrastructure.RecordStatus.New,
            "a server-listed row with a beneficiary signature stays pending");
        Check(store.CountSessionList("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 1,
            "a server-listed row stays in My pending like the mobile app");
        store.SetStatus("MT4420200801626", Mskpy.Infrastructure.RecordStatus.Accepted);
        store.UpsertRows(new[] { completedRow.RootElement }, "02284251");
        Check(store.GetByConsumerNumber("MT4420200801626")!.Status == Mskpy.Infrastructure.RecordStatus.New,
            "re-importing a server-listed row repairs a wrong Accepted mark");
    }
    finally { try { Directory.Delete(j3Dir, true); } catch { } }
}

// F1: a consumer seen under two cpfs belongs to BOTH queues, its cpf_no never
// changes, and the detail can be fetched from either side.
{
    var ownDir = Path.Combine(Path.GetTempPath(), $"mskpy-own-{Guid.NewGuid():N}");
    Directory.CreateDirectory(ownDir);
    var ownDb = Path.Combine(ownDir, "own.db");
    try
    {
        using var ownStore = new Mskpy.Infrastructure.InspectionStore(ownDb);
        using var row = JsonDocument.Parse("""
        {"consumer_number":"MT4420200801626","cpf_no":"02781310","consumer_name":"Seen twice","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
        """);
        ownStore.UpsertRows(new[] { row.RootElement }, "02284251");
        ownStore.UpsertRows(new[] { row.RootElement }, "02781310");

        var stored = ownStore.GetByConsumerNumber("MT4420200801626")!;
        Check(stored.CpfNo == "02781310", "the row's cpf_no is set on insert and never overwritten");
        Check(stored.SeenCpfs != null && stored.SeenCpfs.Contains("02284251") && stored.SeenCpfs.Contains("02781310"),
            "a consumer seen under two cpfs is recorded under both");
        Check(ownStore.IsSeenUnder("MT4420200801626", "02284251"), "the row belongs to the first officer's queue");
        Check(ownStore.IsSeenUnder("MT4420200801626", "02781310"), "the row belongs to the second officer's queue too");
        Check(ownStore.GetDetailCpf("MT4420200801626", "02284251") == "02284251",
            "the detail is fetched with the session's cpf when the row was seen under it");
        Check(ownStore.GetDetailCpf("MT4420200801626", "09999999") == "02781310",
            "the detail falls back to the row's own cpf for an officer who has not seen it");

        // A re-walk must not move the owner either.
        ownStore.UpsertRows(new[] { row.RootElement }, "02781310");
        var after = ownStore.GetByConsumerNumber("MT4420200801626")!;
        Check(after.CpfNo == "02781310", "a re-walk leaves the row's cpf_no unchanged");
        Check(after.SeenCpfs != null && after.SeenCpfs.Contains("02284251"),
            "a re-walk does not drop a cpf the row was seen under");

        // H3: the queue counts go through record_cpfs with the same predicate
        // the list scope uses, so the chip and the rows can never disagree.
        Check(ownStore.CountSeenUnder("02284251") == 1,
            "a consumer seen under two cpfs counts in the first officer's queue");
        Check(ownStore.CountSeenUnder("02781310") == 1,
            "a consumer seen under two cpfs counts in the second officer's queue too");

        // A consumer seen under exactly one cpf counts in that queue only.
        using var solo = JsonDocument.Parse("""
        {"consumer_number":"MT4420200999999","cpf_no":"02781310","consumer_name":"Seen once","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
        """);
        ownStore.UpsertRows(new[] { solo.RootElement }, "02781310");
        Check(ownStore.CountSeenUnder("02284251") == 1,
            "a consumer seen under one cpf does not count in the other officer's queue");
        Check(ownStore.CountSeenUnder("02781310") == 2,
            "a consumer seen under one cpf counts in that officer's queue");
        Check(ownStore.CountSeenUnder("09999999") == 0,
            "an officer who has seen nothing counts nothing");

        // 4D: a 2xx lands as Accepted, so the row leaves pending for the
        // owning cpf and for every cpf that listed it, while staying in All.
        ownStore.SetStatus("MT4420200801626", Mskpy.Infrastructure.RecordStatus.Accepted, "{\"message\":\"ok\"}");
        Check(ownStore.CountSeenUnder("02284251", Mskpy.Infrastructure.RecordStatus.Accepted) == 0,
            "an accepted row drops out of the first officer's pending count");
        Check(ownStore.CountSeenUnder("02781310", Mskpy.Infrastructure.RecordStatus.Accepted) == 1,
            "an accepted row drops out of the owning officer's pending count too");
        Check(ownStore.GetSeenUnder("02284251", Mskpy.Infrastructure.RecordStatus.Accepted).Count == 0,
            "an accepted row leaves the first officer's pending rows");
        Check(ownStore.GetAll().Any(r => r.ConsumerNumber == "MT4420200801626"
                && r.Status == Mskpy.Infrastructure.RecordStatus.Accepted),
            "an accepted row remains reachable in the full store");
        // 4D5 store side: recording a failure message under the status the
        // row already had does not move it between queues.
        ownStore.SetStatus("MT4420200999999", Mskpy.Infrastructure.RecordStatus.New, "forced 500");
        Check(ownStore.CountSeenUnder("02781310", Mskpy.Infrastructure.RecordStatus.Accepted) == 1,
            "a failure recorded under the existing status leaves the pending count unchanged");
    }
    finally { try { Directory.Delete(ownDir, true); } catch { } }
}

// D2: a completed pass must idle and then re-arm itself from the start.
{
    var walkDir = Path.Combine(Path.GetTempPath(), $"mskpy-walk-{Guid.NewGuid():N}");
    Directory.CreateDirectory(walkDir);
    var indexFile = Path.Combine(walkDir, "index.tsv");
    var cursorFile = Path.Combine(walkDir, "harvester-cursor.txt");
    var logFile = Path.Combine(walkDir, "harvester.log");
    File.WriteAllLines(indexFile, new[] { "MTWALK0000000001\t02781310" });
    var dbFile = Path.Combine(walkDir, "walk.db");

    int port = 15731;
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var serving = Task.Run(() =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                var body = "[{\"consumer_number\":\"MTWALK0000000001\",\"cpf_no\":\"02781310\",\"consumer_name\":\"Walk test\",\"vendor_code\":\"100038029\",\"latitude\":\"19.5\",\"longitude\":\"74.1\",\"inst_date\":\"2026-08-01\"}]";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { break; }
        }
    });

    try
    {
        using var walkStore = new Mskpy.Infrastructure.InspectionStore(dbFile);
        using var harvester = new Mskpy.Infrastructure.InspectionHarvester(
            walkStore, indexFile, $"http://127.0.0.1:{port}", cursorFile,
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: true);
        harvester.Start();

        var deadline = DateTime.UtcNow.AddSeconds(25);
        var log = "";
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            try { if (File.Exists(logFile)) log = File.ReadAllText(logFile); } catch { }
            if (log.Contains("cycle 2 start")) break;
        }
        Check(log.Contains("cycle 1 complete"), "a finished pass logs its cycle end");
        Check(log.Contains("cycle 2 start"), "a finished pass re-arms itself after the idle");
        Check(harvester.CycleNumber >= 2, "the harvester reports the cycle it is on");
        Check(log.Contains("store vacuumed"), "a completed pass vacuums the store");
        Check(walkStore.GetAll().Count == 1, "the re-walk re-reads the same consumer without duplicating it");

        // E1: a record the officer has already submitted must survive the
        // re-walk as Accepted and stay out of "My pending".
        walkStore.SetStatus("MTWALK0000000001", Mskpy.Infrastructure.RecordStatus.Accepted);
        var beforeStamp = RawUpdatedAt(dbFile, "MTWALK0000000001");
        Thread.Sleep(1500);
        var accepted = walkStore.GetByConsumerNumber("MTWALK0000000001")!;
        Check(accepted.Status == Mskpy.Infrastructure.RecordStatus.Accepted, "a re-walk does not reset an accepted record to New");
        Check(RawUpdatedAt(dbFile, "MTWALK0000000001") == beforeStamp, "updated_at does not move when nothing changed");
        var pending = walkStore.GetAll().Count(r => r.Status != Mskpy.Infrastructure.RecordStatus.Accepted);
        Check(pending == 0, "an accepted record stays out of the officer's pending queue");
    }
    finally
    {
        listener.Stop();
        listener.Close();
        try { Directory.Delete(walkDir, true); } catch { }
    }
}

// J4: the cycle proof must survive a restart. The cycle number continues
// instead of repeating "cycle 1", and the log records how long the pass ran
// and how long the brake was, so a completed pass can be read off the log.
{
    var walkDir = Path.Combine(Path.GetTempPath(), $"mskpy-cycle-{Guid.NewGuid():N}");
    Directory.CreateDirectory(walkDir);
    var indexFile = Path.Combine(walkDir, "index.tsv");
    var cursorFile = Path.Combine(walkDir, "harvester-cursor.txt");
    var logFile = Path.Combine(walkDir, "harvester.log");
    var cycleFile = Path.Combine(walkDir, "harvester-cycle.txt");
    File.WriteAllLines(indexFile, new[] { "MTWALK0000000001\t02781310" });
    var dbFile = Path.Combine(walkDir, "walk.db");

    int port = 15741;
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var serving = Task.Run(() =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                var body = "[{\"consumer_number\":\"MTWALK0000000001\",\"cpf_no\":\"02781310\",\"consumer_name\":\"Walk test\",\"vendor_code\":\"100038029\",\"latitude\":\"19.5\",\"longitude\":\"74.1\",\"inst_date\":\"2026-08-01\"}]";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { break; }
        }
    });

    string ReadLog() { try { return File.Exists(logFile) ? File.ReadAllText(logFile) : ""; } catch { return ""; } }

    try
    {
        using (var first = new Mskpy.Infrastructure.InspectionHarvester(
            new Mskpy.Infrastructure.InspectionStore(dbFile), indexFile, $"http://127.0.0.1:{port}", cursorFile,
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false))
        {
            first.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !ReadLog().Contains("cycle 2 start")) Thread.Sleep(250);
        }
        var afterFirst = ReadLog();
        Check(afterFirst.Contains("cycle 1 complete"), "the first pass logs its cycle end");
        Check(afterFirst.Contains("cycle 2 start"), "the first pass re-arms itself");
        Check(afterFirst.Contains(" in ") && afterFirst.Contains("cpfs, seen="),
            "the cycle end records the pass counters and how long the pass ran");
        Check(afterFirst.Contains("(after "), "the re-arm records the brake before the next pass");

        // Restart on the same folder: the numbering must continue.
        using (var second = new Mskpy.Infrastructure.InspectionHarvester(
            new Mskpy.Infrastructure.InspectionStore(dbFile), indexFile, $"http://127.0.0.1:{port}", cursorFile,
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false))
        {
            Check(second.CycleNumber == 2, "the cycle number continues across a restart");
            second.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !ReadLog().Contains("cycle 3 start")) Thread.Sleep(250);
        }
        var afterSecond = ReadLog();
        Check(afterSecond.Contains("cycle 2 complete"), "the restarted pass completes the cycle it was in");
        Check(afterSecond.Contains("cycle 3 start"), "the restarted pass re-arms into the next cycle");
        Check(CountOccurrences(afterSecond, "cycle 1 complete") == 1,
            "a restart does not repeat the cycle number");
        Check(File.Exists(cycleFile) && File.ReadAllLines(cycleFile).Length >= 2,
            "the cycle state is persisted next to the cursor");
    }
    finally
    {
        listener.Stop();
        listener.Close();
        try { Directory.Delete(walkDir, true); } catch { }
    }
}

// J7: the give-up policy. A walk that cannot get an answer must stop asking:
// after the give-up window without a single success the pass is abandoned, the
// walk stops, and the officer resumes it.
{
    var walkDir = Path.Combine(Path.GetTempPath(), $"mskpy-giveup-{Guid.NewGuid():N}");
    Directory.CreateDirectory(walkDir);
    var indexFile = Path.Combine(walkDir, "index.tsv");
    var cursorFile = Path.Combine(walkDir, "harvester-cursor.txt");
    var logFile = Path.Combine(walkDir, "harvester.log");
    File.WriteAllLines(indexFile, new[] { "MTWALK0000000001\t02781310", "MTWALK0000000002\t02781311" });
    var dbFile = Path.Combine(walkDir, "giveup.db");

    // A port nothing listens on: every request fails at once.
    int port = 15751;
    int requests = 0;
    var counting = new HttpListener();
    counting.Prefixes.Add($"http://127.0.0.1:{port}/");
    counting.Start();
    var serving = Task.Run(() =>
    {
        while (counting.IsListening)
        {
            try
            {
                var ctx = counting.GetContext();
                Interlocked.Increment(ref requests);
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { break; }
        }
    });

    string ReadLog() { try { return File.Exists(logFile) ? File.ReadAllText(logFile) : ""; } catch { return ""; } }

    try
    {
        using var store = new Mskpy.Infrastructure.InspectionStore(dbFile);
        // A two-second give-up window: the policy is the same, the clock is not.
        using var harvester = new Mskpy.Infrastructure.InspectionHarvester(
            store, indexFile, $"http://127.0.0.1:{port}", cursorFile,
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false,
            giveUpAfter: TimeSpan.FromSeconds(2));

        harvester.Start();
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !harvester.GaveUp) Thread.Sleep(200);
        Check(harvester.GaveUp, "a walk that cannot get an answer gives up");
        Check(harvester.GaveUpAt.HasValue, "the give-up is timestamped");
        Check(ReadLog().Contains("abandoned"), "the give-up is logged");

        // No further request may be made.
        Thread.Sleep(2500);
        var afterGiveUp = Volatile.Read(ref requests);
        Thread.Sleep(2500);
        Check(Volatile.Read(ref requests) == afterGiveUp, "a walk that gave up stops asking the host");

        // The officer resumes it.
        harvester.ResumeHarvest();
        Check(!harvester.GaveUp, "resuming clears the give-up");
        Thread.Sleep(2500);
        Check(Volatile.Read(ref requests) > afterGiveUp, "the resumed walk asks the host again");
        Check(ReadLog().Contains("resumed by the officer after a give-up"), "the resume is logged");
    }
    finally
    {
        counting.Stop();
        counting.Close();
        try { Directory.Delete(walkDir, true); } catch { }
    }
}

// K1: the probe classifies the answer. A cpf that answers with rows is an
// officer with pending work: it goes into the index. One that answers with
// nothing is only remembered as probed, so it is never asked twice and it
// never pollutes the walk list.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-probe-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var indexFile = Path.Combine(dir, "index.tsv");
    var cursorFile = Path.Combine(dir, "harvester-cursor.txt");
    var probedFile = Path.Combine(dir, "harvester-probed-cpfs.txt");
    var probeCursorFile = Path.Combine(dir, "harvester-probe-cursor.txt");
    File.WriteAllLines(indexFile, new[] { "MTOLD0000000001\t02781310" });
    var dbFile = Path.Combine(dir, "probe.db");

    int port = 15761;
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var asked = new List<string>();
    var serving = Task.Run(() =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                var user = ctx.Request.QueryString["userName"] ?? "";
                lock (asked) asked.Add(user);
                // 1 in 5 answers with a row.
                var body = "[]";
                if (asked.Count % 5 == 0)
                    body = "[{\"consumer_number\":\"MTPROBE" + user + "\",\"cpf_no\":\"" + user +
                           "\",\"consumer_name\":\"Probed\",\"vendor_code\":\"100038029\",\"latitude\":\"19.5\",\"longitude\":\"74.1\",\"inst_date\":\"2026-08-01\"}]";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { break; }
        }
    });

    try
    {
        using var store = new Mskpy.Infrastructure.InspectionStore(dbFile);
        using var harvester = new Mskpy.Infrastructure.InspectionHarvester(
            store, indexFile, $"http://127.0.0.1:{port}", cursorFile,
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false);

        var results = new List<Mskpy.Infrastructure.ProbeResult>();
        for (int i = 0; i < 5; i++)
        {
            var r = await harvester.ProbeOnceAsync();
            if (r != null) results.Add(r);
        }
        Check(results.Count == 5, "five probes were issued");
        Check(results.Count(r => r.Discovered) == 1, "only the cpf that answered with rows was discovered");
        Check(results.Count(r => r.Rows > 0) == 1, "one probe in five answered with a row");
        Check(results.Select(r => r.Cpf).Distinct().Count() == 5, "no cpf was probed twice");

        var lines = File.ReadAllLines(indexFile);
        var indexCpfs = lines.Select(l => l.Split('\t')[1].Trim()).Distinct().ToList();
        Check(indexCpfs.Count == 2, "the index holds the old cpf and the one the probe found");
        var found = results.First(r => r.Discovered).Cpf;
        Check(indexCpfs.Contains(found), "the discovered cpf is in the index");
        Check(!indexCpfs.Contains(results.First(r => !r.Discovered).Cpf), "a cpf with no rows does not go into the index");

        Check(File.Exists(probedFile), "the probe keeps a probed set next to the cursor");
        Check(File.ReadAllLines(probedFile).Length == 5, "the probed set records every probed cpf");
        Check(File.Exists(probeCursorFile), "the probe keeps its own cursor");
        Check(store.CountSeenUnder(found) == 1, "the probed rows are in the store under the probed cpf");

        // Re-running must not probe any of them again.
        var firstFive = results.Select(r => r.Cpf).ToList();
        var askedBefore = asked.Count;
        for (int i = 0; i < 3; i++) await harvester.ProbeOnceAsync();
        Check(asked.Count == askedBefore + 3, "a re-run issues new probes");
        lock (asked)
            Check(!asked.Skip(askedBefore).Any(a => firstFive.Contains(a)),
                "a re-run does not re-probe a cpf that was already probed");
        Check(File.ReadAllLines(probedFile).Length == 8, "the probed set grows with every probe");
    }
    finally
    {
        listener.Stop();
        listener.Close();
        try { Directory.Delete(dir, true); } catch { }
    }
}

// K2: the request budget is shared. One probe in every eight requests, so
// the walk keeps seven of every eight; and no probe at all in the last 100
// cpfs of a pass, because the end of a pass belongs to the walk.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-budget-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var dbFile = Path.Combine(dir, "budget.db");

    int port = 15771;
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var asked = new List<string>();
    var serving = Task.Run(() =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                lock (asked) asked.Add(ctx.Request.QueryString["userName"] ?? "");
                var bytes = System.Text.Encoding.UTF8.GetBytes("[]");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { break; }
        }
    });

    try
    {
        // Phase A: a long index, so the last-100 rule never bites.
        var indexA = Path.Combine(dir, "index-a.tsv");
        var walkA = Enumerable.Range(1, 200).Select(i => "MTWALK" + i.ToString("D8") + "\t0210" + i.ToString("D4")).ToList();
        File.WriteAllLines(indexA, walkA);
        var walkCpfs = new HashSet<string>(walkA.Select(l => l.Split('\t')[1].Trim()));

        using (var store = new Mskpy.Infrastructure.InspectionStore(dbFile))
        using (var harvester = new Mskpy.Infrastructure.InspectionHarvester(
            store, indexA, $"http://127.0.0.1:{port}", Path.Combine(dir, "cursor-a.txt"),
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false,
            requestInterval: TimeSpan.FromMilliseconds(25)))
        {
            harvester.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline) { lock (asked) if (asked.Count >= 80) break; Thread.Sleep(50); }
        }
        List<string> first80;
        lock (asked) first80 = asked.Take(80).ToList();
        var walks = first80.Count(c => walkCpfs.Contains(c));
        var probes = first80.Count - walks;
        Check(first80.Count == 80, "the run made 80 requests");
        Check(walks == 70, "the walk kept 70 of the 80 requests");
        Check(probes == 10, "the probe took 10 of the 80 requests - one in eight");
        Check(first80.Where(c => walkCpfs.Contains(c)).Distinct().Count() == walks,
            "the walk skipped no cpf - a probe tick does not consume one");

        // Phase B: a short index, so the walk enters its last 100 cpfs.
        var indexB = Path.Combine(dir, "index-b.tsv");
        var walkB = Enumerable.Range(1, 120).Select(i => "MTWALK" + i.ToString("D8") + "\t0220" + i.ToString("D4")).ToList();
        File.WriteAllLines(indexB, walkB);
        var walkCpfsB = new HashSet<string>(walkB.Select(l => l.Split('\t')[1].Trim()));

        int askedBeforeB;
        lock (asked) askedBeforeB = asked.Count;
        using (var store2 = new Mskpy.Infrastructure.InspectionStore(Path.Combine(dir, "budget2.db")))
        using (var harvester2 = new Mskpy.Infrastructure.InspectionHarvester(
            store2, indexB, $"http://127.0.0.1:{port}", Path.Combine(dir, "cursor-b.txt"),
            reWalkIdle: TimeSpan.FromSeconds(2), vacuumOnCycleComplete: false,
            requestInterval: TimeSpan.FromMilliseconds(25)))
        {
            harvester2.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline) { lock (asked) if (asked.Count >= askedBeforeB + 120) break; Thread.Sleep(50); }
        }
        List<string> afterB;
        lock (asked) afterB = asked.Skip(askedBeforeB).ToList();
        // The walk reaches the last 100 cpfs of a 120 cpf index after 20 walks.
        var walkOrder = afterB.Where(c => walkCpfsB.Contains(c)).ToList();
        Check(walkOrder.Count >= 20, "the walk passed the start of the last 100 cpfs");
        var afterTail = afterB.Skip(walkOrder.FindIndex(c => c == walkOrder[19]) + 1).ToList();
        Check(afterTail.Count(c => !walkCpfsB.Contains(c)) == 0,
            "no probe is made once the pass is inside its last 100 cpfs");
    }
    finally
    {
        listener.Stop();
        listener.Close();
        try { Directory.Delete(dir, true); } catch { }
    }
}

// K3: the index must not shrink. A rebuild is a union: every pair the file
// had, every pair the store knows, and every cpf the probe has discovered.
// Discovery is monotonic - a cpf found once stays in the walk list even
// after its rows are accepted and gone.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-shrink-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var indexFile = Path.Combine(dir, "index.tsv");
    var discoveredFile = Path.Combine(dir, "harvester-discovered-cpfs.txt");
    var dbFile = Path.Combine(dir, "shrink.db");

    // A file with two cpfs: one the store knows and one the probe found.
    File.WriteAllLines(indexFile, new[]
    {
        "MTOLD0000000001\t02781310",
        "MTPROBED00000001\t02222222",
    });
    File.WriteAllLines(discoveredFile, new[] { "02222222" });

    using var store = new Mskpy.Infrastructure.InspectionStore(dbFile);
    // The store knows one consumer under a cpf of its own.
    using (var row = JsonDocument.Parse("""
    {"consumer_number":"MTNEW0000000001","cpf_no":"02660016","consumer_name":"Fresh","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
    """))
        store.UpsertRows(new[] { row.RootElement }, "02660016");

    var result = Mskpy.Infrastructure.InspectionIndexBuilder.Rebuild(store, indexFile, discoveredFile);
    var lines = File.ReadAllLines(indexFile);
    var cpfs = lines.Select(l => l.Split('\t')[1].Trim()).Distinct().ToList();

    Check(cpfs.Contains("02781310"), "the rebuild keeps a cpf only the old file had");
    Check(cpfs.Contains("02222222"), "the rebuild keeps a cpf the probe discovered");
    Check(cpfs.Contains("02660016"), "the rebuild adds a cpf the store knows");
    Check(result.Cpfs >= result.CpfsBefore, "the rebuild never loses a cpf");
    Check(result.Pairs >= result.PairsBefore, "the rebuild never loses a pair");
    Check(result.Consumers >= result.ConsumersBefore, "the rebuild never loses a consumer");

    // The discovered cpf stays even when the store has nothing for it any
    // more - its rows are accepted and dropped.
    store.DeleteByConsumerNumbers(new[] { "MTNEW0000000001" });
    var again = Mskpy.Infrastructure.InspectionIndexBuilder.Rebuild(store, indexFile, discoveredFile);
    var after = File.ReadAllLines(indexFile).Select(l => l.Split('\t')[1].Trim()).Distinct().ToList();
    Check(after.Contains("02222222"), "a discovered cpf stays in the walk list after its rows are gone");
    Check(again.Cpfs >= again.CpfsBefore, "the second rebuild still never loses a cpf");
    try { Directory.Delete(dir, true); } catch { }
}

// K4: the import path. A file dropped in the app's data folder is merged
// into the index at start - one column per line is a cpf on its own, two
// columns are a consumer and a cpf - and it is renamed afterwards, so it
// is merged exactly once.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-import-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var indexFile = Path.Combine(dir, "index.tsv");
    var importFile = Path.Combine(dir, "index-import.tsv");
    var doneFile = Path.Combine(dir, "index-import.done");
    var countFile = Path.Combine(dir, "index-import.count");
    File.WriteAllLines(indexFile, new[] { "MTOLD0000000001\t02781310" });

    // Five cpfs, one column per line, and one consumer+cpf pair.
    File.WriteAllLines(importFile, new[]
    {
        "02011111", "02022222", "02033333", "02044444", "02055555",
        "MTIMPORTED0001\t02066666",
    });

    var result = Mskpy.Infrastructure.InspectionIndexImporter.MergeIfPresent(importFile, indexFile);
    Check(result.Merged, "the import file was merged");
    Check(result.Entries == 6, "every entry in the import file was read");
    Check(result.Cpfs == 6, "every entry carried a cpf");

    var lines = File.ReadAllLines(indexFile);
    var cpfs = lines.Select(l => l.Split('\t')[1].Trim()).Distinct().ToList();
    Check(cpfs.Count == 7, "the imported cpfs joined the walk list");
    Check(cpfs.Contains("02011111") && cpfs.Contains("02066666"), "a cpf-only entry and a pair both landed");
    Check(cpfs.Contains("02781310"), "the index kept what it had");
    Check(lines.Any(l => l == "MTIMPORTED0001\t02066666"), "a consumer+cpf entry is merged as a pair");
    Check(File.Exists(doneFile), "the import file is renamed after it is merged");
    Check(!File.Exists(importFile), "the import file is not left behind");
    Check(Mskpy.Infrastructure.InspectionIndexImporter.ReadCount(countFile) == 6, "the imported count is kept");

    // A second start must not merge it again.
    var again = Mskpy.Infrastructure.InspectionIndexImporter.MergeIfPresent(importFile, indexFile);
    Check(!again.Merged, "a renamed import is not merged again");
    var after = File.ReadAllLines(indexFile).Select(l => l.Split('\t')[1].Trim()).Distinct().ToList();
    Check(after.Count == cpfs.Count, "a second start does not change the index");
    try { Directory.Delete(dir, true); } catch { }
}

// K5: the sweep order. The probe must visit every number in the space
// exactly once and it must sample the WHOLE space as it goes: a plain
// ascending sweep starts in the emptiest band there is.
{
    Check(Mskpy.Infrastructure.CpfProbe.CandidateFor(0) == "02000000", "the sweep starts at 02000000");
    var seen = new HashSet<string>();
    var bands = new HashSet<string>();
    for (long i = 0; i < 10000; i++)
    {
        var c = Mskpy.Infrastructure.CpfProbe.CandidateFor(i);
        seen.Add(c);
        bands.Add(c.Substring(0, 4));
    }
    Check(seen.Count == 10000, "the sweep visits no number twice");
    Check(bands.Count >= 90, "the sweep samples the whole space, not one band");
    // The stride is coprime with the space, so the whole sweep is a
    // bijection: every number is visited exactly once.
    var gcdA = Mskpy.Infrastructure.CpfProbe.SweepStride;
    var gcdB = Mskpy.Infrastructure.CpfProbe.SpaceSize;
    while (gcdB != 0) { var t = gcdA % gcdB; gcdA = gcdB; gcdB = t; }
    Check(gcdA == 1, "the sweep stride is coprime with the space, so every number is visited once");
}

// C3: the coordinates in a submission are always the customer's site - the
// record's own installation coordinates, jittered a few metres - and never
// the device's position.
{
    var provider = new Mskpy.Infrastructure.LocationProvider();

    // The jitter and the accuracy stay inside their bounds, over many draws.
    var jitters = new List<double>();
    var accuracies = new List<double>();
    for (int i = 0; i < 200; i++)
    {
        var r = await provider.ResolveAsync(null, null, null, "19.336027", "74.095619");
        jitters.Add(r.JitterMetres!.Value);
        accuracies.Add(double.Parse(r.Accuracy!, System.Globalization.CultureInfo.InvariantCulture));
    }
    Check(jitters.All(j => j >= 3.0 && j <= 15.0), "every jitter is between 3 and 15 metres");
    Check(accuracies.All(a => a >= 4.0 && a <= 12.0), "every accuracy is between 4 and 12");
    Check(jitters.Distinct().Count() > 100, "the jitter is not a constant");

    // One draw, in full.
    var site = await provider.ResolveAsync(null, null, null, "19.336027", "74.095619");
    Check(site.Source == Mskpy.Infrastructure.LocationSource.SiteCoords, "the position comes from the record's own site");
    Check(site.Mocked == false, "the site position is not a mocked fix");
    Check(site.HasCoords, "the site position has a pair");

    // The distance the payload carries is the distance of THAT pair from the
    // installation - not a constant.
    var lat = double.Parse(site.Lat!, System.Globalization.CultureInfo.InvariantCulture);
    var lon = double.Parse(site.Long!, System.Globalization.CultureInfo.InvariantCulture);
    var computed = Mskpy.Infrastructure.GeoDistance.Meters(lat, lon, 19.336027, 74.095619);
    Check(Math.Abs(computed - site.DistanceMetres!.Value) < 0.01, "the distance matches the pair that is submitted");
    Check(computed >= 2.9 && computed <= 15.1, "the distance is the jitter");
    Check(site.Lat != "19.336027" || site.Long != "74.095619", "the submitted pair differs from the stored one");

    // A record with no coordinates: no device fallback, the officer has to
    // enter them.
    var none = await provider.ResolveAsync(null, null, null, null, null);
    Check(none.Source == Mskpy.Infrastructure.LocationSource.None, "a record with no coordinates has no position");
    Check(!none.HasCoords, "nothing is made up for a record with no coordinates");
    Check(none.Error == "Unable to access your location.", "a record with no coordinates gets the client location message");

    // What the officer typed wins (C4), and it is used as it is.
    var manual = await provider.ResolveAsync("19.1", "74.1", "5", "19.336027", "74.095619");
    Check(manual.Source == Mskpy.Infrastructure.LocationSource.ManualOverride, "what the officer typed is the source");
    Check(manual.Lat == "19.1" && manual.Long == "74.1", "the typed pair is used as it is");
    Check(manual.Accuracy == "5", "the typed accuracy is used as it is");
    Check(manual.Mocked == false, "a typed position is not mocked");

    var zero = await provider.ResolveAsync(null, null, null, "0", "0");
    Check(!zero.HasCoords && zero.Error == "Unable to access your location.", "0,0 coordinates are rejected");
    var badManual = await provider.ResolveAsync("abc", "74.1", null, "19.336027", "74.095619");
    Check(!badManual.HasCoords && badManual.Error == "Unable to access your location.", "non-numeric coordinates are rejected");
    var nan = await provider.ResolveAsync("NaN", "74.1", null, "19.336027", "74.095619");
    Check(!nan.HasCoords && nan.Error == "Unable to access your location.", "NaN coordinates are rejected");
    var infinity = await provider.ResolveAsync(null, null, null, "Infinity", "74.095619");
    Check(!infinity.HasCoords && infinity.Error == "Unable to access your location.", "Infinity coordinates are rejected");
}

// C5: the officer's own book of installers.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-vendor-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var bookFile = Path.Combine(dir, "vendor-people.json");

    var book = new Mskpy.Infrastructure.VendorPeople(bookFile);
    Check(book.People.Count == 0, "a book that does not exist is empty");
    Check(book.LoadWarning == null, "a book that does not exist is not a warning");

    book.Add("Vishal thombare", "7397926761", vendor: "100036833");
    book.Add("Sunil Patil", "9822011223");
    book.Save();
    Check(File.Exists(bookFile), "the book is written to its file");

    // Read it back: the book is the officer's own and survives a restart.
    var reloaded = new Mskpy.Infrastructure.VendorPeople(bookFile);
    Check(reloaded.People.Count == 2, "the book survives a restart");
    Check(reloaded.Find("vishal thombare")?.Mobile == "7397926761", "an entry keeps its mobile");
    Check(reloaded.Find("Vishal thombare")?.Vendor == "100036833", "an entry keeps the vendor it was last used with");
    Check(reloaded.Find("nobody") == null, "an unknown name is not in the book");

    // Typing offers the matching entries.
    Check(reloaded.Suggest("Vish").Count == 1, "typing offers the matching entry");
    Check(reloaded.Suggest("VISH").Count == 1, "the offer does not care about the case");
    Check(reloaded.Suggest("zz").Count == 0, "a name that matches nothing offers nothing");
    Check(reloaded.Suggest("").Count == 0, "an empty name offers nothing");

    // Picking fills the mobile, and never overwrites one that was typed.
    Check(Mskpy.Infrastructure.VendorPeople.DecidePick("", "7397926761") == Mskpy.Infrastructure.VendorPeople.Pick.Fill,
        "picking fills an empty mobile box");
    Check(Mskpy.Infrastructure.VendorPeople.DecidePick("7397926761", "7397926761") == Mskpy.Infrastructure.VendorPeople.Pick.Keep,
        "picking leaves a mobile that already matches alone");
    Check(Mskpy.Infrastructure.VendorPeople.DecidePick("1111111111", "7397926761") == Mskpy.Infrastructure.VendorPeople.Pick.Ask,
        "picking a different mobile has to be confirmed");

    // A removed entry is never offered again.
    Check(reloaded.Remove("Sunil Patil"), "an entry can be removed");
    reloaded.Save();
    var afterRemove = new Mskpy.Infrastructure.VendorPeople(bookFile);
    Check(afterRemove.People.Count == 1, "the removed entry is gone from the file");
    Check(afterRemove.Suggest("Sunil").Count == 0, "a removed entry is never offered again");

    // A file that cannot be read falls back to an empty book, and says so
    // once, rather than taking the app down.
    File.WriteAllText(bookFile, "{ this is not json");
    var corrupt = new Mskpy.Infrastructure.VendorPeople(bookFile);
    Check(corrupt.People.Count == 0, "a corrupt file falls back to an empty book");
    Check(corrupt.LoadWarning != null, "a corrupt file is reported once");
    var recovered = new Mskpy.Infrastructure.VendorPeople(bookFile);
    recovered.Add("Pavan", "9000000000");
    recovered.Save();
    Check(new Mskpy.Infrastructure.VendorPeople(bookFile).People.Count == 1, "the book works again after a corrupt file");

    try { Directory.Delete(dir, true); } catch { }
}

// L2: the first run. A fresh machine has no index; the seed index that
// travels with the folder is copied there once. An index that already
// exists is never overwritten.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-seed-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var indexFile = Path.Combine(dir, "index.tsv");
    var seedFile = Path.Combine(dir, "seed-index.tsv");
    File.WriteAllLines(seedFile, new[] { "MTSEED00000001\t02781310", "MTSEED00000002\t02781311" });

    var seeded = Mskpy.Infrastructure.InspectionIndexSeeder.Seed(indexFile, seedFile, out var message);
    Check(seeded, "the seed index is copied when there is no index");
    Check(File.Exists(indexFile), "the index is in place after the seed");
    Check(File.ReadAllLines(indexFile).Length == 2, "the seeded index is the seed");
    Check(message.Contains("seeded"), "the seed says what it did");

    // An index that is already there is never overwritten.
    File.AppendAllText(indexFile, "MTOWN0000000001\t09999999\n");
    var again = Mskpy.Infrastructure.InspectionIndexSeeder.Seed(indexFile, seedFile, out var secondMessage);
    Check(!again, "an existing index is not seeded over");
    Check(File.ReadAllLines(indexFile).Length == 3, "the officer's own index is untouched");
    Check(secondMessage.Contains("already there"), "the second seed says why it did nothing");

    // No seed in the folder is not an error.
    var missing = Path.Combine(dir, "nothing-here.tsv");
    var third = Mskpy.Infrastructure.InspectionIndexSeeder.Seed(Path.Combine(dir, "other.tsv"), missing, out var thirdMessage);
    Check(!third && thirdMessage.Contains("no seed index"), "a folder without a seed index is not an error");
    try { Directory.Delete(dir, true); } catch { }
}

// J5: the index rebuild. The file is a snapshot that carried one cpf per
// consumer and missed every consumer the harvest has found since; the
// rebuild unions the store's pairs with the pairs the file already held.
{
    var dir = Path.Combine(Path.GetTempPath(), $"mskpy-index-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var indexFile = Path.Combine(dir, "index.tsv");
    var dbFile = Path.Combine(dir, "index.db");

    // The old file: one cpf per consumer, and one consumer the harvest has
    // not reached yet - the store knows nothing about it.
    File.WriteAllLines(indexFile, new[]
    {
        "MTOLD0000000001\t02781310",
        "MTOLD0000000002\t02781311",
        "MTOLD0000000003\t02861747",
    });

    using (var store = new Mskpy.Infrastructure.InspectionStore(dbFile))
    {
        try
        {
        // A consumer the harvest has seen under two officers.
        using var two = JsonDocument.Parse("""
        {"consumer_number":"MTNEW0000000001","cpf_no":"02660016","consumer_name":"Two officers","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
        """);
        store.UpsertRows(new[] { two.RootElement }, "02660016");
        store.UpsertRows(new[] { two.RootElement }, "02729563");
        // A consumer the old file never had.
        using var fresh = JsonDocument.Parse("""
        {"consumer_number":"MTNEW0000000002","cpf_no":"02284251","consumer_name":"Fresh","vendor_code":"100038029","latitude":"19.5","longitude":"74.1","inst_date":"2026-08-01"}
        """);
        store.UpsertRows(new[] { fresh.RootElement }, "02284251");

        var pairs = store.GetIndexPairs();
        Check(pairs.Count(p => p.ConsumerNumber == "MTNEW0000000001") == 2,
            "the store knows a consumer listed under two officers");
        Check(pairs.First(p => p.ConsumerNumber == "MTNEW0000000001").Primary,
            "the row's own cpf is the primary pair");

        var result = Mskpy.Infrastructure.InspectionIndexBuilder.Rebuild(store, indexFile);
        Check(result.Consumers == 5, "the rebuilt index holds the old and the new consumers");
        Check(result.Pairs == 6, "the rebuilt index holds both pairs of a two-officer consumer");
        Check(result.Cpfs == 6, "the rebuilt index holds every cpf of either side");

        var lines = File.ReadAllLines(indexFile);
        Check(lines.Any(l => l == "MTOLD0000000003\t02861747"),
            "the rebuild keeps a consumer the harvest has not reached");
        Check(lines.Any(l => l == "MTNEW0000000002\t02284251"),
            "the rebuild adds a consumer the old file never had");
        Check(lines.Any(l => l == "MTNEW0000000001\t02660016") && lines.Any(l => l == "MTNEW0000000001\t02729563"),
            "a consumer listed under two officers is written under both");
        var firstForTwo = lines.First(l => l.StartsWith("MTNEW0000000001\t", StringComparison.Ordinal));
        Check(firstForTwo == "MTNEW0000000001\t02660016",
            "the row's own cpf is written first, so the index resolves to it");

        // The file is written fresh, which is what clears the stale warning.
        Check((DateTime.Now - File.GetLastWriteTime(indexFile)).TotalMinutes < 5,
            "the rebuilt index file is fresh, so the stale-index warning clears");

        // A second rebuild changes nothing.
        var again = Mskpy.Infrastructure.InspectionIndexBuilder.Rebuild(store, indexFile);
        Check(again.Pairs == result.Pairs && again.Consumers == result.Consumers && again.Cpfs == result.Cpfs,
            "rebuilding twice changes nothing");
        Check(File.ReadAllLines(indexFile).SequenceEqual(lines),
            "rebuilding twice writes the same file");

        var loaded = new Mskpy.Infrastructure.InspectionIndex(indexFile);
        Check(loaded.Loaded && loaded.Entries.Count == 5 && loaded.PairCount == 6 && loaded.CpfCount == 6,
            "the loaded index reports what it holds");
        Check(loaded.ResolveCpf("MTNEW0000000001") == "02660016",
            "a consumer listed under two officers resolves to the officer it was first seen under");
        Check(loaded.ResolveCpf("MTOLD0000000003") == "02861747",
            "a consumer only the old file knew still resolves");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

// D3: an append to the index refreshes its write time, so the stale warning
// clears; a file that is genuinely untouched keeps its old stamp and still warns.
{
    var dir = Path.Combine(Path.GetTempPath(), $"d3-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var indexFile = Path.Combine(dir, "index.tsv");
        File.WriteAllText(indexFile, "MTA\t12345678\n");

        // A genuinely untouched file stays stale: freeze the stamp 25 h back.
        var oldStamp = DateTime.Now.AddHours(-25);
        File.SetLastWriteTime(indexFile, oldStamp);
        Check(Mskpy.Infrastructure.InspectionIndexBuilder.AddPairs(indexFile, new[] { ("MTA", "12345678") }) == 0,
            "D3: re-adding an existing pair appends nothing");
        Check(Math.Abs((File.GetLastWriteTime(indexFile) - oldStamp).TotalMinutes) < 1,
            "D3: a no-op append does not touch the file, so a stale file stays stale");

        var before = File.GetLastWriteTime(indexFile);
        var added = Mskpy.Infrastructure.InspectionIndexBuilder.AddPairs(indexFile, new[] { ("MTB", "87654321") });
        Check(added == 1, "D3: a new pair is appended");
        Check(File.ReadAllLines(indexFile).Any(l => l == "MTB\t87654321"), "D3: the appended pair is on disk");
        Check((File.GetLastWriteTime(indexFile) - before).TotalSeconds >= 0
            && (DateTime.Now - File.GetLastWriteTime(indexFile)).TotalSeconds < 5,
            "D3: the append refreshes the file's write time");

        // Rebuild also refreshes it, even when the content is unchanged.
        var dbPath = Path.Combine(dir, "store.db");
        using (var store = new Mskpy.Infrastructure.InspectionStore(dbPath))
        {
            var old = File.GetLastWriteTime(indexFile);
            Thread.Sleep(20);
            Mskpy.Infrastructure.InspectionIndexBuilder.Rebuild(store, indexFile);
            Check((File.GetLastWriteTime(indexFile) - old).TotalMilliseconds >= 0
                && (DateTime.Now - File.GetLastWriteTime(indexFile)).TotalSeconds < 5,
                "D3: a rebuild refreshes the write time");
        }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}
// M1: every reason string lands in one of the eight categories.
{
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise(null) == Mskpy.Infrastructure.ReasonCategoriser.Other,
        "M1: a missing reason is 'other'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("attach proper photo") == Mskpy.Infrastructure.ReasonCategoriser.Photo,
        "M1: a photo reason is 'photo'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("Pump not installed") == Mskpy.Infrastructure.ReasonCategoriser.Unfixable,
        "M1: an unfixable reason is 'unfixable'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("Water source is not seen in the photo.") == Mskpy.Infrastructure.ReasonCategoriser.WaterSource,
        "M1: a water-source reason is 'water_source'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("Water discharge is not seen in the photo.") == Mskpy.Infrastructure.ReasonCategoriser.WaterDischarge,
        "M1: a water-discharge reason is 'water_discharge'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("Beneficiary not available") == Mskpy.Infrastructure.ReasonCategoriser.BeneficiaryAbsent,
        "M1: a beneficiary-absent reason is 'beneficiary_absent'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("lat long diff") == Mskpy.Infrastructure.ReasonCategoriser.WrongData,
        "M1: a wrong-data reason is 'wrong_data'");
    Check(Mskpy.Infrastructure.ReasonCategoriser.Categorise("pending for installtion") == Mskpy.Infrastructure.ReasonCategoriser.SystemIncomplete,
        "M1: a system-incomplete reason is 'system_incomplete'");
    // M1: the store backfills the category on every row that carries a reason.
    var dir = Path.Combine(Path.GetTempPath(), $"m1-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var db = Path.Combine(dir, "store.db");
        using (var seed = new Mskpy.Infrastructure.InspectionStore(db))
        {
            seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
                ConsumerNumber: "MCAT", CpfNo: "sample", VendorCode: "V", ConsumerName: "x",
                Latitude: "1", Longitude: "2", InstallDate: "2026-08-01",
                ReasonToRevert: "attach proper photo", SurveyDurationMinutes: null,
                IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.Returned,
                LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
                SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
        }
        using (var reloaded = new Mskpy.Infrastructure.InspectionStore(db))
        {
            var row = reloaded.GetByConsumerNumber("MCAT");
            Check(row != null && row.ReasonCategory == Mskpy.Infrastructure.ReasonCategoriser.Photo,
                "M1: the store stores the category on the row");
        }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}
// M2: the pre-submit score is a pure function of the signals.
{
    var clean = Mskpy.Infrastructure.ReturnRiskScorer.Score(new Mskpy.Infrastructure.ReturnRiskScorer.RiskInput(
        false, false, 0, false, false, false, false, false, false, false));
    Check(clean.Band == Mskpy.Infrastructure.ReturnRiskScorer.Band.LikelyAccepted,
        "M2: a clean record is likely accepted");
    Check(clean.Reasons.Length == 0, "M2: a clean record has no warnings");

    var twiceUnfixable = Mskpy.Infrastructure.ReturnRiskScorer.Score(new Mskpy.Infrastructure.ReturnRiskScorer.RiskInput(
        false, true, 2, false, false, false, false, false, false, false));
    Check(twiceUnfixable.Band == Mskpy.Infrastructure.ReturnRiskScorer.Band.LikelyReturned,
        "M2: a record returned twice with an unfixable reason scores the lowest band");

    var risky = Mskpy.Infrastructure.ReturnRiskScorer.Score(new Mskpy.Infrastructure.ReturnRiskScorer.RiskInput(
        false, false, 0, false, true, false, false, false, false, false));
    Check(risky.Band == Mskpy.Infrastructure.ReturnRiskScorer.Band.Risky,
        "M2: one medium signal makes a record risky");
    Check(risky.Reasons.Length == 1 && risky.Reasons[0].Contains("Installer"),
        "M2: the reasons name the signal");

    var veryHigh = Mskpy.Infrastructure.ReturnRiskScorer.Score(new Mskpy.Infrastructure.ReturnRiskScorer.RiskInput(
        true, false, 0, false, false, false, false, false, false, false));
    Check(veryHigh.Band == Mskpy.Infrastructure.ReturnRiskScorer.Band.LikelyReturned,
        "M2: returned before unchanged is very high");
}
// M3: a state refusal classifies as state; a payload refusal as payload.
{
    Check(Mskpy.Infrastructure.RefusalClassifier.IsStateRefusal("beneficiary not available at site") == true,
        "M3: a state refusal is classified as state");
    Check(Mskpy.Infrastructure.RefusalClassifier.IsStateRefusal("photo not clear") == false,
        "M3: a payload refusal is classified as payload");
    // store-level: a state refusal lands in Blocked, not Failed.
    var dir = Path.Combine(Path.GetTempPath(), $"m3-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var db = Path.Combine(dir, "store.db");
        using (var seed = new Mskpy.Infrastructure.InspectionStore(db))
        {
            seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
                ConsumerNumber: "MBLK", CpfNo: "sample", VendorCode: "V", ConsumerName: "x",
                Latitude: "1", Longitude: "2", InstallDate: "2026-08-01",
                ReasonToRevert: null, SurveyDurationMinutes: null,
                IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.Submitted,
                LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
                SubmittedDataPath: null, PhotoPaths: null, SignPaths: null));
        }
        using (var s = new Mskpy.Infrastructure.InspectionStore(db))
        {
            s.SetStatus("MBLK", Mskpy.Infrastructure.RecordStatus.Blocked, "beneficiary not available",
                blockedReason: "beneficiary not available", blockedAt: DateTime.UtcNow);
            var blocked = s.GetAll(status: Mskpy.Infrastructure.RecordStatus.Blocked);
            var failed = s.GetAll(status: Mskpy.Infrastructure.RecordStatus.Failed);
            Check(blocked.Count == 1 && blocked[0].BlockedReason == "beneficiary not available",
                "M3: a state refusal produces Blocked with a reason");
            Check(failed.Count == 0, "M3: a blocked record does not appear in Needs redo");
        }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}
// M4: a blocked row is re-probed; when the probe says it is actionable it
// returns to pending marked "now actionable".
{
    var dir = Path.Combine(Path.GetTempPath(), $"m4-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var db = Path.Combine(dir, "store.db");
        var indexFile = Path.Combine(dir, "index.tsv");
        File.WriteAllText(indexFile, "MTX\t12345678\n");
        using (var seed = new Mskpy.Infrastructure.InspectionStore(db))
        {
            seed.Upsert(new Mskpy.Infrastructure.StoredInspectionRecord(
                ConsumerNumber: "MBLK2", CpfNo: "12345678", VendorCode: "V", ConsumerName: "x",
                Latitude: "1", Longitude: "2", InstallDate: "2026-08-01",
                ReasonToRevert: null, SurveyDurationMinutes: null,
                IsFirstTime: true, Status: Mskpy.Infrastructure.RecordStatus.Blocked,
                LastServerMessage: null, LastServerMessageAt: null, OriginalJson: "{}",
                SubmittedDataPath: null, PhotoPaths: null, SignPaths: null,
                BlockedReason: "state", BlockedAt: DateTime.UtcNow));
        }
        using (var store = new Mskpy.Infrastructure.InspectionStore(db))
        using (var harvester = new Mskpy.Infrastructure.InspectionHarvester(
            store, indexFile, "http://127.0.0.1:1"))
        {
            await harvester.ReprobeBlockedAsync(
                (rec, ct) => Task.FromResult(true), CancellationToken.None);
            var rows = store.GetAll(status: Mskpy.Infrastructure.RecordStatus.New);
            Check(rows.Count == 1 && rows[0].LastServerMessage == "now actionable",
                "M4: an actionable re-probe returns the row to pending marked now actionable");
        }
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}
// M5: stale detection compares the record as opened with the fresh one.
{
    var raw = new Dictionary<string, JsonElement>();
    using (var d = JsonDocument.Parse("{\"consumer_number\":\"C1\",\"consumer_name\":\"A\"}"))
        foreach (var p in d.RootElement.EnumerateObject()) raw[p.Name] = p.Value.Clone();
    var r1 = new Mskpy.Domain.InspectionRecord("C1", "0", "V", "A", "1", "2", "2026-08-01",
        null, null, null, "addr", raw);
    var raw2 = new Dictionary<string, JsonElement>();
    using (var d = JsonDocument.Parse("{\"consumer_number\":\"C1\",\"consumer_name\":\"B\"}"))
        foreach (var p in d.RootElement.EnumerateObject()) raw2[p.Name] = p.Value.Clone();
    var r2 = new Mskpy.Domain.InspectionRecord("C1", "0", "V", "B", "1", "2", "2026-08-01",
        null, null, null, "addr", raw2);
    Check(Mskpy.Infrastructure.RecordChangeDetector.Changed(r1, r2),
        "M5: a changed record is detected as stale");
    Check(!Mskpy.Infrastructure.RecordChangeDetector.Changed(r1, r1),
        "M5: an unchanged record is not stale");
}
Console.WriteLine($"{passed} checks passed. No production requests were sent.");
sealed class FakeHandler : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Body = "{}", Url = "", SentBody = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Url = request.RequestUri!.ToString(); SentBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>D1: a service that never answers, so the sign-in budget is what ends it.</summary>
sealed class SilentHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("the silent handler should never return");
    }
}

/// <summary>D1: a service that cannot be reached at all.</summary>
sealed class DeadHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("synthetic network failure");
}

/// <summary>Captures the client-vendorCode header and the body's vendorCode.</summary>
sealed class CapturingHandler : HttpMessageHandler
{
    public string? VendorHeader;
    public string? BodyVendor;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValues("client-vendorCode", out var v)) VendorHeader = string.Join(",", v);
        if (request.Content != null)
        {
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            var at = text.IndexOf("\"vendorCode\":\"", StringComparison.Ordinal);
            if (at >= 0)
            {
                var from = at + "\"vendorCode\":\"".Length;
                var to = text.IndexOf('"', from);
                if (to > from) BodyVendor = text.Substring(from, to - from);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"hasErrors\":false,\"status\":200,\"message\":\"success\"}", Encoding.UTF8, "application/json")
        };
    }
}
