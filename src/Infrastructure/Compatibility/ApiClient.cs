using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mskpy.Infrastructure;

namespace Mskpy.Windows;

public sealed record UserSession(string Username, string Role, string Scheme, JsonElement User, JsonElement? MeterData, string AccessToken = "");
public sealed class ApiAuthorizationException(HttpStatusCode status) : InvalidOperationException("The service did not authorize this request. Check the account and its access permissions.")
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient http;
    public UserSession? Session { get; private set; }
    public string LoginResponseShape { get; private set; } = "No login response received.";
    public string ServiceTokenLocation { get; private set; } = "Not supplied";

    /// <summary>
    /// D1: the account service has been measured at over seventy seconds for a
    /// perfectly good sign-in. Thirty-five seconds - the read budget - turned
    /// that into a cancelled task and one generic message. The sign-in gets its
    /// own, longer budget, and every read keeps the old one.
    /// </summary>
    public static readonly TimeSpan DefaultLoginBudget = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(35);

    readonly TimeSpan loginBudget;

    public ApiClient(HttpMessageHandler? handler = null, TimeSpan? loginTimeout = null)
    {
        loginBudget = loginTimeout ?? DefaultLoginBudget;
        // The per-call budgets below are the only timeouts: the client itself
        // must not fire first, or a long sign-in is still a cancelled task.
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MSKPY-Windows-Reconstruction/0.1");
    }

    public async Task LoginAsync(string username, string password, string role, string scheme, CancellationToken cancellationToken = default)
    {
        Session = null;
        LoginResponseShape = "No login response received.";
        ServiceTokenLocation = "Not supplied";
        var tod = role == "Agency" && scheme == "SCHEME_MSKVY_2_TOD";
        using var request = new HttpRequestMessage(HttpMethod.Post, tod
            ? "https://nvp.mahadiscom.in/vendor-meter-api-1.0-SNAPSHOT/api/v1/login"
            : "https://mobileapp.mahadiscom.in/mskpy/SignIn/" + (role == "Employee" ? "Employee" : "Agency"));
        if (tod)
            request.Content = new StringContent(JsonSerializer.Serialize(new { vendorId = username, password }), Encoding.UTF8, "application/json");
        else
        {
            var fields = new Dictionary<string, string> { ["loginId"] = username, ["password"] = password };
            if (role == "Agency") fields["scheme"] = scheme;
            request.Content = new FormUrlEncodedContent(fields);
        }
        // D1: the password is never logged; the login id and the endpoint are,
        // so two attempts can be compared line by line.
        SignInLog.Write($"POST issued: {request.RequestUri!.PathAndQuery} (role {role}{(tod ? ", scheme " + scheme : "")}) loginId {username}");
        var root = await SendAsync(request, cancellationToken, loginBudget, "sign-in reply");
        LoginResponseShape = "Response fields: " + Keys(root);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var shapeData))
            LoginResponseShape += "\nData fields: " + Keys(shapeData);
        if (tod)
        {
            if (String(root, "status") != "SUCCESS" || string.IsNullOrWhiteSpace(String(root, "token")))
            {
                SignInLog.Write("sign-in reply: the vendor sign-in was not accepted");
                throw new SignInException("the reply",
                    "The service declined the sign-in (step: the reply). Check the account type, scheme, username and password.", rejected: true);
            }
            ServiceTokenLocation = "response.token";
            Session = new UserSession(username, role, scheme, root.Clone(), root.TryGetProperty("meterData", out var meters) ? meters.Clone() : null, String(root, "token"));
            return;
        }
        if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "hasErrors") || String(root, "status") == "FAIL"
            || !root.TryGetProperty("data", out var data) || !IsTrue(data, "success"))
        {
            SignInLog.Write("sign-in reply: the service did not accept the credentials");
            throw new SignInException("the reply",
                "The service declined the sign-in (step: the reply). Check the account type, scheme, username and password.", rejected: true);
        }
        if (!data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            SignInLog.Write("sign-in reply: no user object in the reply");
            throw new SignInException("the reply",
                "The reply did not carry the signed-in user (step: reading the reply). Please try again.");
        }
        LoginResponseShape += "\nUser fields: " + Keys(user);
        var issuedToken = "";
        foreach (var candidate in new[] { (Value: user, Path: "data.user"), (Value: data, Path: "data"), (Value: root, Path: "response") })
        {
            foreach (var name in new[] { "jwt", "accessToken", "access_token", "token" })
            {
                var tokenField = candidate.Value.EnumerateObject().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (tokenField.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenField.Value.GetString())) continue;
                issuedToken = tokenField.Value.GetString()!;
                ServiceTokenLocation = candidate.Path + "." + tokenField.Name;
                break;
            }
            if (issuedToken.Length > 0) break;
        }
        Session = new UserSession(username, role, role == "Employee" ? "ALL" : scheme, user.Clone(), null, issuedToken);
    }

    public async Task<IReadOnlyList<JsonElement>> ApplicationsAsync(string module, string applicationNumber = "", CancellationToken cancellationToken = default)
    {
        var session = Session ?? throw new InvalidOperationException("Please sign in first.");
        var args = new Dictionary<string, string>();
        string path;
        string host;
        if (module == "milestones")
        {
            host = "https://mobileapp.mahadiscom.in";
            path = "/mskpy/Mskvy2Application/Bulk";
            args["loginId"] = session.Username;
            args["userType"] = session.Role.ToUpperInvariant();
            args["filters"] = "";
            args["pageNumber"] = "1";
            if (session.Role == "Employee")
            {
                args["officeCode"] = String(session.User, "officeCode");
                args["officeType"] = String(session.User, "officeType");
            }
        }
        else if (module is "jsr" or "inspection")
        {
            host = "https://mobileappnj.mahadiscom.in";
            path = module == "jsr" ? "/mtskpy/jsr/applications" : "/mtskpy/linestaff-inspection/applications";
            args["userName"] = String(session.User, "loginId");
            if (string.IsNullOrWhiteSpace(args["userName"])) throw new InvalidOperationException("The login response did not include the account identifier required for this module.");
            if (!string.IsNullOrWhiteSpace(applicationNumber)) args["applicationNo"] = applicationNumber;
        }
        else throw new InvalidOperationException("This workflow is still being reconstructed.");
        var query = string.Join("&", args.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, host + path + "?" + query);
        request.Headers.Add("client-os", "WINDOWS");
        request.Headers.Add("client-os-version", Environment.OSVersion.Version.ToString());
        request.Headers.Add("client-version", "2.6.4");
        if (module != "milestones")
        {
            // /mtskpy/* endpoints do not use Authorization headers (R1).
            // The stale "no service token / contract unresolved" throw is removed.
            request.Headers.Add("client-app-name", "RE");
            request.Headers.Add("client-loginId", String(session.User, "loginId"));
            var vendor = String(session.User, "vendorCode");
            if (!string.IsNullOrWhiteSpace(vendor)) request.Headers.Add("client-vendorCode", vendor);
            request.Headers.Add("client-device", "Windows Desktop");
        }
        var root = await SendAsync(request, cancellationToken, ReadBudget);
        if (IsTrue(root, "hasErrors") || String(root, "status") == "FAIL") throw new InvalidOperationException("The service could not load these applications.");
        JsonElement records;
        if (module == "milestones")
        {
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("applications", out records))
                throw new InvalidOperationException("The application list response did not match the recovered format.");
        }
        else if (module == "inspection")
        {
            if (!root.TryGetProperty("applications", out records)) throw new InvalidOperationException("The application list response did not match the recovered format.");
        }
        else records = root;
        if (records.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The service did not return an application list in the expected format.");
        return records.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan budget, string? phase = null)
    {
        // D1: a per-call budget, so the sign-in can wait several times longer
        // than a list read without weakening either.
        using var budgetSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetSource.CancelAfter(budget);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budgetSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A read keeps the old shape (the shell says "timed out"); the
            // sign-in says which step it stopped at, in plain words.
            if (phase == null) throw;
            SignInLog.Write($"{phase}: no answer within {budget.TotalSeconds:0} s (timeout)");
            throw new SignInException("waiting for the reply",
                $"The account service did not answer within {budget.TotalSeconds:0} seconds (step: waiting for the reply). Please try again.");
        }
        catch (HttpRequestException ex) when (phase != null)
        {
            SignInLog.Write($"{phase}: the request failed before a reply ({ex.Message})");
            throw new SignInException("sending the sign-in",
                "The sign-in could not be sent to the account service (step: sending the POST): " + ex.Message);
        }
        // At most one bounded retry for an idempotent read. Never replay login or submissions.
        if (request.Method == HttpMethod.Get && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250));
            if (delay <= TimeSpan.FromSeconds(3))
            {
                response.Dispose();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, budgetSource.Token);
                using var retry = new HttpRequestMessage(HttpMethod.Get, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response = await http.SendAsync(retry, HttpCompletionOption.ResponseContentRead, budgetSource.Token);
            }
        }
        using var ownedResponse = response;
        if (phase != null) SignInLog.Write($"{phase}: HTTP {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            if (phase != null)
                throw new SignInException("the reply", $"The account service redirected the sign-in (step: the reply, HTTP {(int)response.StatusCode}).");
            throw new InvalidOperationException($"The service redirected this request. Body: {body}");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (phase != null)
                throw new SignInException("the reply",
                    $"The account service refused the sign-in (step: the reply, HTTP {(int)response.StatusCode}). Check the account and its access permissions.", rejected: true);
            throw new ApiAuthorizationException(response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            if (phase != null)
                throw new SignInException("the reply", $"The account service answered HTTP {(int)response.StatusCode} (step: the reply). Please try again.");
            throw new InvalidOperationException($"The service returned HTTP {(int)response.StatusCode}. Body: {body}");
        }
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
        catch (JsonException)
        {
            if (phase != null)
                throw new SignInException("the reply", "The account service answered with something that was not JSON (step: reading the reply).");
            throw new InvalidOperationException("The service response was not in the expected format. Windows compatibility remains unverified.");
        }
    }

    public static string String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        && field.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? field.ToString() : "";
    private static bool IsTrue(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    private static string Keys(JsonElement value) => value.ValueKind == JsonValueKind.Object ? string.Join(", ", value.EnumerateObject().Select(x => x.Name)) : "(" + value.ValueKind + ")";
    public void SignOut() { Session = null; LoginResponseShape = "No active session."; ServiceTokenLocation = "Not supplied"; }
    public void Dispose() => http.Dispose();
}
