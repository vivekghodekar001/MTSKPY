using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// Loads index.tsv (consumer_number -> cpf_no) at startup (P2).
/// Falls back gracefully when the file is absent (app still works from the list).
/// </summary>
public sealed class InspectionIndex
{
    public IReadOnlyDictionary<string, string> Entries { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public bool Loaded { get; }

    public InspectionIndex(string? tsvPath = null)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tsvPath != null && File.Exists(tsvPath))
        {
            foreach (var line in File.ReadAllLines(tsvPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    dict[parts[0].Trim()] = parts[1].Trim();
            }
        }
        Entries = dict;
        Loaded = tsvPath != null && File.Exists(tsvPath);
    }

    /// <summary>Resolve a consumer number to its cpf_no. Returns null if unknown.</summary>
    public string? ResolveCpf(string consumerNumber) =>
        Entries.TryGetValue(consumerNumber, out var cpf) ? cpf : null;

    /// <summary>Check whether a consumer number exists in the index.</summary>
    public bool Contains(string consumerNumber) => Entries.ContainsKey(consumerNumber);
}
