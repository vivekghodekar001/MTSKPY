using System.Net;
using System.Text;
using System.Text.Json;
using Mskpy.Windows;

var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
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
