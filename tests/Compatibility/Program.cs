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
