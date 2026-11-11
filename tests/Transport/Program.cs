using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mskpy.Application;
using Mskpy.Infrastructure;
using Mskpy.Windows;

var passes = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passes++; Console.WriteLine("PASS " + label); }
const string Success = """{"hasErrors":false,"data":{"success":true,"user":{"loginId":"sample","jwt":"synthetic-token","password":"synthetic-secret"}}}""";
var reads = 0; var posts = 0; var requests = new List<HttpRequestMessage>();
using (var api = new ApiClient(new Handler((req, ct) =>
{
    if (req.Method == HttpMethod.Post) { posts++; return Task.FromResult(Reply(HttpStatusCode.OK, Success)); }
    requests.Add(req); reads++;
    var response = Reply(reads == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{\"applications\":[]}");
    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return Task.FromResult(response);
})))
{
    await api.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"); await api.ApplicationsAsync("inspection");
    Check(reads == 2 && !ReferenceEquals(requests[0], requests[1]), "transient GET retries once with a fresh request object");
    Check(posts == 1, "successful login is never replayed");
}
var loginAttempts = 0;
using (var api = new ApiClient(new Handler((_, _) => { loginAttempts++; return Task.FromResult(Reply(HttpStatusCode.ServiceUnavailable, "{}")); })))
{
    try { await api.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY"); } catch (InvalidOperationException) { }
    Check(loginAttempts == 1, "failed login POST is not retried");
}
var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var api = new ApiClient(new Handler(async (_, ct) => { signal.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Reply(HttpStatusCode.OK, Success); })))
using (var cancellation = new CancellationTokenSource())
{
    var request = api.LoginAsync("sample", "example", "Employee", "SCHEME_MTSKPY", cancellation.Token);
    await signal.Task; cancellation.Cancel(); var cancelled = false; try { await request; } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && api.Session == null, "caller cancellation reaches HTTP and does not create a session");
}
var deniedReads = 0;
using (var gateway = new ExistingApiGateway(new ApiClient(new Handler((req, _) =>
{
    if (req.Method == HttpMethod.Post) return Task.FromResult(Reply(HttpStatusCode.OK, Success)); deniedReads++; return Task.FromResult(Reply(HttpStatusCode.Unauthorized, "{}"));
}))))
{
    await gateway.LoginAsync(new LoginRequest("sample", "example", "Employee", "SCHEME_MTSKPY"), CancellationToken.None);
    Check(!gateway.Profile!.Fields.Any(x => x.Label.Contains("jwt") || x.Label.Contains("password")), "session display fields exclude credentials and tokens");
    Check(!gateway.Diagnostics.Contains("synthetic-token") && !gateway.Diagnostics.Contains("synthetic-secret"), "diagnostics omit secret values");
    var expired = false; try { await gateway.GetApplicationsAsync("inspection", CancellationToken.None); } catch (SessionExpiredException) { expired = true; }
    Check(expired && gateway.Profile == null && deniedReads == 1, "HTTP 401 clears session without retry or authorization fallback");
}
Console.WriteLine($"{passes} HTTP integration checks passed using injected handlers; no production requests.");
static HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request, ct); }
