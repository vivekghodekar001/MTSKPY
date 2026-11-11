using System.Text.Json;
using System.Text.RegularExpressions;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Windows;

namespace Mskpy.Infrastructure;

public sealed class ExistingApiGateway(ApiClient api) : IClientGateway, IDisposable
{
    public AccountProfile? Profile { get; private set; }
    public string Diagnostics => api.LoginResponseShape + "\nRecognized token location: " + api.ServiceTokenLocation;
    public async Task<AccountProfile> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        Profile = null;
        await api.LoginAsync(request.Username, request.Password, request.Role, request.Scheme, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var session = api.Session ?? throw new InvalidOperationException("The service did not establish a session.");
        Profile = new AccountProfile(session.Username, session.Role, session.Scheme, DisplayFields(session.User));
        return Profile;
    }
    public async Task<IReadOnlyList<ApplicationDocument>> GetApplicationsAsync(string module, CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> rows;
        try { rows = await api.ApplicationsAsync(module, cancellationToken: cancellationToken); }
        catch (ApiAuthorizationException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        { SignOut(); throw new SessionExpiredException(); }
        return rows.Select(row =>
        {
            var fields = DisplayFields(row);
            var identity = ApiClient.String(row, "consumer_number");
            if (identity.Length == 0) identity = ApiClient.String(row, "applicationNo");
            return new ApplicationDocument(identity.Length > 0 ? identity : "Application", string.Join(" · ", fields.Take(3).Select(x => x.Value)), fields, row.GetRawText());
        }).ToArray();
    }
    private static IReadOnlyList<DisplayField> DisplayFields(JsonElement row) => row.ValueKind != JsonValueKind.Object ? [] :
        row.EnumerateObject().Where(x => !IsSensitive(x.Name) && x.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            .Select(x => new DisplayField(Regex.Replace(x.Name, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' '), x.Value.ValueKind == JsonValueKind.Null ? "Not provided" : x.Value.ToString())).ToArray();
    private static bool IsSensitive(string name) => new[] { "password", "jwt", "token", "secret", "credential", "authorization" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    public void SignOut() { api.SignOut(); Profile = null; }
    public void Dispose() { SignOut(); api.Dispose(); }
}
