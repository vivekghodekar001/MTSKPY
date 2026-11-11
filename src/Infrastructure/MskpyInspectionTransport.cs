using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// Transport for /mtskpy/* (linestaff-inspection) calls.
///
/// Contract (G1 bytecode, G2 reference):
///   GET  /mtskpy/linestaff-inspection/applications?userName=<session>                     list
///   GET  /mtskpy/linestaff-inspection/applications?applicationNo=<consumer>&userName=<cpf> detail
///   POST /mtskpy/linestaff-inspection/upload-survey                                        multipart form-data
///
/// Rules (R1/R2/R9/R10):
///   * NO Authorization header on /mtskpy/*.
///   * Headers: client-loginId, client-app-name=RE, client-os=ANDROID,
///     client-os-version=36, client-version=2.6.4, client-device-id, client-device,
///     user-agent=okhttp/4.12.0.
///   * HTTP/1.1 exact, VersionPolicy=RequestVersionExact, ExpectContinue=false.
///   * ByteArrayContent with real Content-Length.
///   * Pre-send log line (URL, headers, manifest) even on failure.
///   * Outbound calls bypass the proxy.
/// </summary>
public sealed class MskpyInspectionTransport : IDisposable
{
    private const string DefaultInspectionHost = "https://mobileappnj.mahadiscom.in";
    private const string ClientAppName = "RE";
    private const string ClientOs = "ANDROID";
    private const string ClientOsVersion = "36";
    private const string ClientVersion = "2.6.4";
    private const string ClientDevice = "Windows-Desktop";
    private const string UserAgent = "okhttp/4.12.0";

    private readonly HttpClient http;
    private readonly string logPath;
    private readonly string loginId;
    private readonly string? vendorOverride;
    private readonly string deviceId;
    private readonly string inspectionHost;

    public MskpyInspectionTransport(string loginId, string? logDirectory = null, HttpMessageHandler? handler = null, string? inspectionHost = null, string? vendorOverride = null)
    {
        if (string.IsNullOrWhiteSpace(loginId)) throw new ArgumentException("Login id is required for /mtskpy/* calls.", nameof(loginId));
        this.loginId = loginId;
        this.vendorOverride = string.IsNullOrWhiteSpace(vendorOverride) ? null : vendorOverride.Trim();
        this.deviceId = Guid.NewGuid().ToString("N");
        this.inspectionHost = inspectionHost ?? DefaultInspectionHost;

        var logDir = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenewableEnergyDesktop", "logs");
        Directory.CreateDirectory(logDir);
        this.logPath = Path.Combine(logDir, "mskpy-inspection.log");

        // R10: bypass the proxy so outbound calls don't loop through a local proxy.
        var transport = handler ?? new SocketsHttpHandler { UseProxy = false };
        this.http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public string InspectionHost => inspectionHost;

    /// <summary>The session loginId (userInfo.loginId) this transport was built with.</summary>
    public string LoginId => loginId;

    public void Dispose() => http.Dispose();

    private HttpRequestMessage InspectionRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, inspectionHost + path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("client-loginid", loginId);
        request.Headers.TryAddWithoutValidation("client-app-name", ClientAppName);
        request.Headers.TryAddWithoutValidation("client-os", ClientOs);
        request.Headers.TryAddWithoutValidation("client-os-version", ClientOsVersion);
        request.Headers.TryAddWithoutValidation("client-version", ClientVersion);
        request.Headers.TryAddWithoutValidation("client-device-id", deviceId);
        request.Headers.TryAddWithoutValidation("client-device", ClientDevice);
        request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        // A1: no session vendor. The client-vendorCode header is only sent when
        // an explicit override is in force; otherwise the submit request carries
        // the record's own vendor_code.
        if (!string.IsNullOrWhiteSpace(vendorOverride))
            request.Headers.TryAddWithoutValidation("client-vendorCode", vendorOverride);
        // R1: NO Authorization header for /mtskpy/*.
        return request;
    }

    private void LogPreSend(string phase, HttpRequestMessage request, string? manifest = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {phase} {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
            sb.AppendLine($"METHOD: {request.Method} {request.RequestUri}");
            sb.AppendLine("HEADERS SENT:");
            foreach (var h in request.Headers)
                sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            if (request.Content?.Headers is HttpContentHeaders ch)
            {
                foreach (var h in ch)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            }
            if (manifest != null)
            {
                sb.AppendLine("MANIFEST (pre-send):");
                sb.Append(manifest);
            }
            File.AppendAllText(logPath, sb.ToString());
        }
        catch
        {
            // Logging must never prevent a request from being attempted.
        }
    }

    private void LogResponse(string phase, HttpRequestMessage request, HttpResponseMessage? response, string? body)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {phase}-RESPONSE {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
            sb.AppendLine($"METHOD: {request.Method} {request.RequestUri}");
            if (response != null)
            {
                sb.AppendLine($"STATUS: {(int)response.StatusCode} {response.ReasonPhrase}");
                sb.AppendLine("RESPONSE HEADERS:");
                foreach (var h in response.Headers)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                if (response.Content?.Headers is HttpContentHeaders rch)
                    foreach (var h in rch)
                        sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            }
            else
            {
                sb.AppendLine("STATUS: (no response received)");
            }
            sb.AppendLine("RESPONSE BODY:");
            sb.AppendLine(body ?? "(empty)");
            File.AppendAllText(logPath, sb.ToString());
        }
        catch
        {
            // Logging must never mask the outcome.
        }
    }

    /// <summary>List pending inspection applications. userName = session loginId.</summary>
    public async Task<(HttpStatusCode StatusCode, string Body, JsonDocument? Document)> ListAsync(CancellationToken ct = default)
    {
        using var request = InspectionRequest(HttpMethod.Get, $"/mtskpy/linestaff-inspection/applications?userName={Uri.EscapeDataString(loginId)}");
        LogPreSend("LIST", request);
        HttpResponseMessage? response = null;
        string body = "(no response)";
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            body = $"{ex.GetType().Name}: {ex.Message}";
            LogResponse("LIST", request, null, body);
            throw;
        }
        finally
        {
            LogResponse("LIST", request, response, body);
        }
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(body); } catch { /* keep raw body for error display */ }
        return (response!.StatusCode, body, doc);
    }

    /// <summary>
    /// Detail for a single record. userName = record cpf_no; applicationNo = consumer_number.
    /// The server IGNORES &amp;applicationNo and returns multiple records; the caller must
    /// select the element whose consumer_number matches.
    /// </summary>
    public async Task<(HttpStatusCode StatusCode, string Body, JsonDocument? Document)> DetailAsync(string applicationNo, string userName, CancellationToken ct = default)
    {
        using var request = InspectionRequest(HttpMethod.Get,
            $"/mtskpy/linestaff-inspection/applications?applicationNo={Uri.EscapeDataString(applicationNo)}&userName={Uri.EscapeDataString(userName)}");
        LogPreSend("DETAIL", request);
        HttpResponseMessage? response = null;
        string body = "(no response)";
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            body = $"{ex.GetType().Name}: {ex.Message}";
            LogResponse("DETAIL", request, null, body);
            throw;
        }
        finally
        {
            LogResponse("DETAIL", request, response, body);
        }
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(body); } catch { }
        return (response!.StatusCode, body, doc);
    }

    /// <summary>
    /// Submit a multipart survey upload. The multipart content (with a pre-computed manifest)
    /// is supplied by the caller so the manifest is logged before the request is sent.
    /// </summary>
    public async Task<(HttpStatusCode StatusCode, string Body)> UploadAsync(HttpRequestMessage request, string manifest, CancellationToken ct = default)
    {
        LogPreSend("UPLOAD", request, manifest);
        HttpResponseMessage? response = null;
        string body = "(no response)";
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            body = $"{ex.GetType().Name}: {ex.Message}";
            LogResponse("UPLOAD", request, null, body);
            throw;
        }
        finally
        {
            LogResponse("UPLOAD", request, response, body);
        }
        return (response!.StatusCode, body);
    }
}

/// <summary>
/// Parses the /applications response into a flat list of records regardless of wrapper shape.
/// </summary>
public static class InspectionResponseParser
{
    public static List<JsonElement> ParseApplications(JsonElement root)
    {
        var rows = root;
        if (rows.ValueKind == JsonValueKind.Object)
        {
            if (rows.TryGetProperty("applications", out var applications)) rows = applications;
            else if (rows.TryGetProperty("data", out var data) && data.TryGetProperty("applications", out var apps2)) rows = apps2;
        }
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The service did not return an application list.");
        return rows.EnumerateArray().Select(x => x.Clone()).ToList();
    }
}
