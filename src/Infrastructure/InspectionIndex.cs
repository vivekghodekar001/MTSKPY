using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// Loads index.tsv (consumer_number -> cpf_no) at startup (P2).
/// Falls back gracefully when the file is absent (app still works from the list).
/// The file can be reloaded at runtime (A5) so a fresher index takes effect
/// without restarting the app.
/// </summary>
public sealed class InspectionIndex
{
    readonly object gate = new();
    IReadOnlyDictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
    bool loaded;
    string? tsvPath;
    // J5: the file can name a consumer under more than one cpf (a consumer
    // listed under two officers resolves under either). The file's counts are
    // kept so the app can show what it actually holds.
    int pairCount;
    int cpfCount;

    public IReadOnlyDictionary<string, string> Entries
    {
        get { lock (gate) return entries; }
    }

    /// <summary>J5: the number of (consumer, cpf) pairs in the file.</summary>
    public int PairCount
    {
        get { lock (gate) return pairCount; }
    }

    /// <summary>J5: the number of distinct cpfs in the file - the walk list.</summary>
    public int CpfCount
    {
        get { lock (gate) return cpfCount; }
    }

    public bool Loaded
    {
        get { lock (gate) return loaded; }
    }

    /// <summary>The file this index was loaded from (null = no path given).</summary>
    public string? Path => tsvPath;

    public InspectionIndex(string? tsvPath = null)
    {
        this.tsvPath = tsvPath;
        Reload();
    }

    /// <summary>
    /// A5: re-read the tsv from disk. Safe to call at any time; the previous
    /// contents are replaced atomically so a reader never sees a half-loaded file.
    /// Passing a path points the index at a different file.
    /// </summary>
    public void Reload(string? path = null)
    {
        lock (gate)
        {
            if (path != null) tsvPath = path;
        }
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var cpfs = new HashSet<string>(StringComparer.Ordinal);
        int pairs = 0;
        var current = tsvPath;
        var exists = current != null && File.Exists(current);
        if (current != null && exists)
        {
            foreach (var line in File.ReadAllLines(current))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    var consumer = parts[0].Trim();
                    var cpf = parts[1].Trim();
                    pairs++;
                    cpfs.Add(cpf);
                    // J5: the FIRST cpf named for a consumer wins. The file
                    // lists the row's own cpf first, so that is the officer
                    // the record was first seen under; a consumer listed under
                    // two officers resolves under either, so the choice is
                    // stable rather than dependent on the line order.
                    if (!dict.ContainsKey(consumer)) dict[consumer] = cpf;
                }
            }
        }
        lock (gate)
        {
            entries = dict;
            loaded = exists;
            pairCount = pairs;
            cpfCount = cpfs.Count;
        }
    }

    /// <summary>Resolve a consumer number to its cpf_no. Returns null if unknown.</summary>
    public string? ResolveCpf(string consumerNumber) =>
        Entries.TryGetValue(consumerNumber, out var cpf) ? cpf : null;

    /// <summary>Check whether a consumer number exists in the index.</summary>
    public bool Contains(string consumerNumber) => Entries.ContainsKey(consumerNumber);
}
