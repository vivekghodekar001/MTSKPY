using Mskpy.Domain;
namespace Mskpy.Application;

public sealed record LoginRequest(string Username, string Password, string Role, string Scheme);
public sealed class SessionExpiredException : InvalidOperationException
{
    public SessionExpiredException() : base("Your session expired. Sign in again.") { }
}
// Opaque original JSON is an application transfer document for the legacy offline editor.
// It is not a domain entity and is never rendered or logged by the shell.
public sealed record ApplicationDocument(string Identity, string Summary, IReadOnlyList<DisplayField> Fields, string OriginalJson);

public interface IClientGateway
{
    AccountProfile? Profile { get; }
    string Diagnostics { get; }
    Task<AccountProfile> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApplicationDocument>> GetApplicationsAsync(string module, CancellationToken cancellationToken);
    void SignOut();
}

public sealed class ClientWorkflow(IClientGateway gateway)
{
    public string Diagnostics => gateway.Diagnostics;
    public async Task<AccountProfile> SignInAsync(LoginRequest input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input.Username)) throw new InvalidOperationException("Please enter username.");
        if (input.Username.Trim().Length > 50) throw new InvalidOperationException("Username must not exceed 50 characters.");
        if (string.IsNullOrEmpty(input.Password)) throw new InvalidOperationException("Please enter password.");
        if (input.Role is not ("Employee" or "Agency")) throw new InvalidOperationException("Select Employee or Agency.");
        if (input.Role == "Agency" && input.Scheme is not ("SCHEME_MTSKPY" or "SCHEME_MSKVY_2" or "SCHEME_MSKVY_2_TOD"))
            throw new InvalidOperationException("Select a supported scheme.");
        return await gateway.LoginAsync(input with { Username = input.Username.Trim() }, cancellationToken);
    }
    public Task<IReadOnlyList<ApplicationDocument>> LoadAsync(string module, CancellationToken cancellationToken)
    {
        if (gateway.Profile is null) throw new InvalidOperationException("Please sign in first.");
        if (!VerifiedNavigation.Modules.Any(x => x.Key == module)) throw new InvalidOperationException("This module has not yet been integrated.");
        return gateway.GetApplicationsAsync(module, cancellationToken);
    }
    public void SignOut() => gateway.SignOut();
}
