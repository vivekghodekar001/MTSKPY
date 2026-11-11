using System.IO;
using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// X1: Persists the session vendor code per login id under LocalAppData so it
/// survives a restart. Keyed by login id; values are vendor codes.
/// </summary>
public sealed class VendorCodeStore : IDisposable
{
    private readonly string path;
    private readonly Dictionary<string, string> codes = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public VendorCodeStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "vendor-codes.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dict != null)
                foreach (var kvp in dict) codes[kvp.Key] = kvp.Value;
        }
        catch
        {
            // Corrupt store -> start empty; must never block sign-in.
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(codes, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Persistence is best-effort.
        }
    }

    public string? Get(string loginId)
    {
        if (string.IsNullOrWhiteSpace(loginId)) return null;
        return codes.TryGetValue(loginId, out var code) ? code : null;
    }

    public void Set(string loginId, string? vendorCode)
    {
        if (string.IsNullOrWhiteSpace(loginId)) return;
        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            codes.Remove(loginId);
        }
        else
        {
            codes[loginId] = vendorCode.Trim();
        }
        Save();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            Save();
            disposed = true;
        }
    }
}
