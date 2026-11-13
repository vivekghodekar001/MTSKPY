namespace Mskpy.Infrastructure;

/// <summary>
/// M3: tell a state refusal from a payload refusal by reading the response
/// body. A state refusal means the survey itself cannot proceed as it stands
/// (the beneficiary, the site, the connection) and needs the SDO; a payload
/// refusal means the data sent was wrong (photo, signature) and can be fixed
/// and retried.
/// </summary>
public static class RefusalClassifier
{
    static readonly string[] StateKeywords =
    [
        "not installed", "beneficiary", "location", "coordinate", "site",
        "connection", "policy", "consumer", "existing", "panel", "capacity",
        "not available", "not present", "water source", "water discharge"
    ];
    static readonly string[] PayloadKeywords =
    [
        "photo", "signature", "image", "upload", "blur", "clear", "missing"
    ];

    public static bool IsStateRefusal(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var s = body.ToLowerInvariant();
        bool hasPayload = PayloadKeywords.Any(k => s.Contains(k));
        bool hasState = StateKeywords.Any(k => s.Contains(k));
        if (hasPayload && !hasState) return false;
        if (hasState && !hasPayload) return true;
        // both or neither: default to payload (retryable) unless state is
        // explicit.
        return false;
    }

    public static string Reason(string? body) =>
        string.IsNullOrWhiteSpace(body) ? "Refused by the server." : body.Trim();
}
