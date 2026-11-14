using System.IO;
using System.Text.Json;

namespace Mskpy.Presentation.Services;

/// <summary>
/// D9: the signed-in session, persisted so the next launch does not need the
/// network. It holds the login id, the role and scheme, and the display fields
/// the account host returned. The password is NOT here - it lives in the
/// credential store - but the two are always written and cleared together.
/// </summary>
public sealed class SessionStore
{
    public sealed record StoredSession(string LoginId, string Role, string Scheme, List<(string Label, string Value)> Fields, DateTimeOffset SavedAt);

    readonly string path;

    public SessionStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", "session.json");
    }

    public string FilePath => path;

    public StoredSession? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var loginId = root.TryGetProperty("loginId", out var l) ? l.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(loginId)) return null;
            var role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "Employee" : "Employee";
            var scheme = root.TryGetProperty("scheme", out var s) ? s.GetString() ?? "SCHEME_MTSKPY" : "SCHEME_MTSKPY";
            var fields = new List<(string, string)>();
            if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in f.EnumerateArray())
                {
                    var label = item.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
                    var value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                    fields.Add((label, value));
                }
            }
            var savedAt = root.TryGetProperty("savedAt", out var sa) && sa.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(sa.GetString()!)
                : DateTimeOffset.Now;
            return new StoredSession(loginId, role, scheme, fields, savedAt);
        }
        catch
        {
            return null;
        }
    }

    public void Save(StoredSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var fields = session.Fields.Select(x => new { label = x.Item1, value = x.Item2 }).ToArray();
            var obj = new
            {
                loginId = session.LoginId,
                role = session.Role,
                scheme = session.Scheme,
                fields,
                savedAt = session.SavedAt.ToString("o")
            };
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* a session that cannot be saved is not a failed sign-in */ }
    }

    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
