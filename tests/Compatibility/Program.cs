using System.Net;
using System.Text;
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
