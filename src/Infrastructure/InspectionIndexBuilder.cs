using System.Text;

namespace Mskpy.Infrastructure;

/// <summary>
/// J5: rebuilds index.tsv from the harvest store.
///
/// The file is (consumer_number -&gt; cpf_no), one line per pair the app can
/// resolve a detail for, and it is the list the background harvester walks
/// (the distinct cpfs in its second column).
///
/// WHY A REBUILD AND NOT AN EDIT. The file that shipped was a snapshot taken
/// when the harvest had walked part of the index. It carried ONE cpf per
/// consumer and it missed every consumer the harvest has found since. The
/// store knows all of it: the row's own cpf_no plus every cpf whose list
/// has carried the row - a consumer listed under two officers resolves under
/// either of them, and the old file could only ever name one.
///
/// WHY THE OLD FILE IS KEPT AS WELL. A rebuild from the store alone would
/// DROP the cpfs the harvest has not reached yet - the store only knows the
/// cpfs it has already walked - and with them every consumer still listed
/// under those officers. So the rebuild is a UNION: everything the store
/// knows, plus everything the file already had. The result is a superset of
/// both, so a rebuild can never lose reach.
///
/// The file is written through a temporary file and moved into place, so a
/// reader never sees a half-written index.
/// </summary>
public sealed class InspectionIndexBuilder
{
    /// <summary>What a rebuild produced, and what the file held before it.</summary>
    public sealed record Result(
        string Path,
        int Pairs, int Consumers, int Cpfs,
        int PairsBefore, int ConsumersBefore, int CpfsBefore,
        int PairsAdded, int ConsumersAdded, int CpfsAdded);

    /// <summary>
    /// Rebuild the index at <paramref name="indexPath"/> from the store and
    /// from the pairs the file already holds. Returns what changed.
    /// </summary>
    public static Result Rebuild(InspectionStore store, string indexPath)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(indexPath)) throw new ArgumentException("The index path is empty.", nameof(indexPath));

        var before = ReadPairs(indexPath);
        var after = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // The file's own pairs first: they are the reach that must not be lost.
        foreach (var entry in before)
            foreach (var cpf in entry.Value) Add(after, entry.Key, cpf, primary: false);

        // Then the store: the row's own cpf first, so the file resolves the
        // consumer to the officer the record was first seen under.
        foreach (var pair in store.GetIndexPairs()) Add(after, pair.ConsumerNumber, pair.CpfNo, pair.Primary);

        var dir = Path.GetDirectoryName(Path.GetFullPath(indexPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = indexPath + ".tmp";
        using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            foreach (var consumer in after.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var cpfs = after[consumer];
                foreach (var cpf in cpfs) writer.Write(consumer + "\t" + cpf + "\n");
            }
        }
        File.Move(tmp, indexPath, overwrite: true);

        var beforeConsumers = before.Count;
        var beforeCpfs = before.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).Count();
        var pairs = after.Values.Sum(v => v.Count);
        var distinctCpfs = after.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).Count();
        return new Result(
            Path: indexPath,
            Pairs: pairs, Consumers: after.Count, Cpfs: distinctCpfs,
            PairsBefore: before.Values.Sum(v => v.Count), ConsumersBefore: beforeConsumers, CpfsBefore: beforeCpfs,
            PairsAdded: pairs - before.Values.Sum(v => v.Count),
            ConsumersAdded: after.Count - beforeConsumers,
            CpfsAdded: distinctCpfs - beforeCpfs);
    }

    /// <summary>
    /// K1: add pairs to the index without rewriting it.
    ///
    /// A probe that finds an officer with pending work has to put that
    /// officer into the walk list straight away, without waiting for the
    /// next full rebuild. The pairs are appended to the file; the rebuild
    /// unions the file with the store anyway, so nothing is lost either
    /// way. Returns how many pairs were new.
    /// </summary>
    public static int AddPairs(string indexPath, IEnumerable<(string Consumer, string Cpf)> pairs)
    {
        var existing = ReadPairs(indexPath);
        var missing = new List<(string Consumer, string Cpf)>();
        foreach (var (consumer, cpf) in pairs)
        {
            if (consumer.Length == 0 || cpf.Length == 0) continue;
            if (existing.TryGetValue(consumer, out var list) && list.Contains(cpf)) continue;
            missing.Add((consumer, cpf));
        }
        if (missing.Count == 0) return 0;
        var dir = Path.GetDirectoryName(Path.GetFullPath(indexPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var writer = new StreamWriter(indexPath, true, new UTF8Encoding(false)))
        {
            foreach (var (consumer, cpf) in missing) writer.Write(consumer + "\t" + cpf + "\n");
        }
        return missing.Count;
    }

    /// <summary>Read the (consumer, cpf) pairs out of an index file.</summary>
    public static Dictionary<string, List<string>> ReadPairs(string indexPath)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!File.Exists(indexPath)) return map;
        foreach (var line in File.ReadAllLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var consumer = parts[0].Trim();
            var cpf = parts[1].Trim();
            if (consumer.Length == 0 || cpf.Length == 0) continue;
            Add(map, consumer, cpf, primary: false);
        }
        return map;
    }

    static void Add(Dictionary<string, List<string>> map, string consumer, string cpf, bool primary)
    {
        if (consumer.Length == 0 || cpf.Length == 0) return;
        if (!map.TryGetValue(consumer, out var list)) map[consumer] = list = new List<string>();
        if (primary)
        {
            // The primary cpf goes first, so the index resolves the consumer
            // to the officer the record was first seen under.
            list.Remove(cpf);
            list.Insert(0, cpf);
        }
        else if (!list.Contains(cpf)) list.Add(cpf);
    }
}
