using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    public ApiClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
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
        var root = await SendAsync(request, cancellationToken);
        LoginResponseShape = "Response fields: " + Keys(root);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var shapeData))
            LoginResponseShape += "\nData fields: " + Keys(shapeData);
        if (tod)
        {
            if (String(root, "status") != "SUCCESS" || string.IsNullOrWhiteSpace(String(root, "token")))
                throw new InvalidOperationException("Login was not accepted. Check your account type, scheme, username and password.");
            ServiceTokenLocation = "response.token";
            Session = new UserSession(username, role, scheme, root.Clone(), root.TryGetProperty("meterData", out var meters) ? meters.Clone() : null, String(root, "token"));
            return;
        }
        if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "hasErrors") || String(root, "status") == "FAIL"
            || !root.TryGetProperty("data", out var data) || !IsTrue(data, "success")
            || !data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Login was not accepted. Check your account type, scheme, username and password.");
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
        var root = await SendAsync(request, cancellationToken);
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

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        // At most one bounded retry for an idempotent read. Never replay login or submissions.
        if (request.Method == HttpMethod.Get && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250));
            if (delay <= TimeSpan.FromSeconds(3))
            {
                response.Dispose();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
                using var retry = new HttpRequestMessage(HttpMethod.Get, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response = await http.SendAsync(retry, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
        }
        using var ownedResponse = response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidOperationException($"The service redirected this request. Body: {body}");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ApiAuthorizationException(response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The service returned HTTP {(int)response.StatusCode}. Body: {body}");
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("The service response was not in the expected format. Windows compatibility remains unverified."); }
    }

    public static string String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        && field.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? field.ToString() : "";
    private static bool IsTrue(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    private static string Keys(JsonElement value) => value.ValueKind == JsonValueKind.Object ? string.Join(", ", value.EnumerateObject().Select(x => x.Name)) : "(" + value.ValueKind + ")";
    public void SignOut() { Session = null; LoginResponseShape = "No active session."; ServiceTokenLocation = "Not supplied"; }
    public void Dispose() => http.Dispose();
}
