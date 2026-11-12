using System.Text;
using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// K1: probes for officers the app was never told about.
///
/// The walk list holds 3,361 cpfs because that is the only officer list
/// anyone ever handed the app, and the server echoes the queried cpf - a
/// row can never name an officer the walk was not already told about.
/// So the only way to grow the walk list is to enumerate the space
/// ourselves: a cpf is an 8-digit number, and the whole observed space is
/// 02xxxxxx.
///
/// MEASURED FIRST (K0, docs/evidence/k0-probe.md):
///   - an unknown cpf answers HTTP 200 with an empty list, exactly like a
///     known cpf that has nothing pending. The two are indistinguishable,
///     so the ONLY usable signal is "this cpf returned at least one row";
///   - 3 of 500 random cpfs returned rows (0.600%), and not one request
///     failed in 500 - so probing at one request per second is safe.
///
/// A cpf that answered with rows is an officer with pending work: it goes
/// into the index, so the walk covers it from then on. A cpf that answered
/// with nothing is recorded in the probed set, so the same number is never
/// asked twice, but it does NOT go into the index - a dead number must
/// never pollute the walk list.
/// </summary>
public sealed class CpfProbe
{
    /// <summary>The first cpf of the observed space (K0: 02xxxxxx).</summary>
    public const int FirstCpf = 2000000;

    /// <summary>The last cpf of the observed space.</summary>
    public const int LastCpf = 2999999;

    /// <summary>
    /// The sweep order. The candidate for step n is
    /// 2,000,000 + ((n * 7919) mod 1,000,000): a bijection of the space, so
    /// the probe visits every number in the space exactly once, and it
    /// samples the WHOLE space uniformly as it goes.
    ///
    /// Why not simply 2,000,000, 2,000,001, ...: because the officers are
    /// not spread evenly, and a plain ascending sweep starts in the emptiest
    /// place there is. Measured on the live service, 307 probes of the
    /// plain ascending sweep (02000000-02000306) found nothing at all,
    /// while 2,000 probes spread across the space found 9 officers with
    /// pending work, 5 of them officers nobody had told the app about.
    ///
    /// 7919 is prime and coprime with 1,000,000, so the sequence is a
    /// bijection: no number is ever visited twice, and the sweep still
    /// starts at 02000000.
    /// </summary>
    public const int SweepStride = 7919;

    /// <summary>The number of steps in the sweep (the size of the space).</summary>
    public const int SpaceSize = 1000000;

    /// <summary>The cpf the probe looks at on a given step of the sweep.</summary>
    public static string CandidateFor(long step)
    {
        var offset = ((step % SpaceSize) + SpaceSize) % SpaceSize;
        return (FirstCpf + offset * SweepStride % SpaceSize).ToString("D8");
    }

    readonly InspectionStore store;
    readonly string indexPath;
    readonly string cursorPath;
    readonly string probedPath;
    readonly string discoveredPath;
    readonly HashSet<string> probed = new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> discovered = new HashSet<string>(StringComparer.Ordinal);
    long step;

    /// <summary>How many cpfs have been probed (the size of the probed set).</summary>
    public int ProbedCount => probed.Count;

    /// <summary>
    /// How many probed cpfs answered with at least one row. This is the
    /// durable list of officers the app found for itself: a cpf found once
    /// stays in the walk list, even after its rows are accepted and gone.
    /// </summary>
    public int DiscoveredCount => discovered.Count;

    /// <summary>Where the discovered cpfs are kept.</summary>
    public string DiscoveredPath => discoveredPath;

    /// <summary>How many pairs the probe has added to the index.</summary>
    public int IndexPairsAdded { get; private set; }

    /// <summary>K2: prospecting can be switched off entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The next cpf the probe would look at (null at the end of the space).</summary>
    public string? NextCpf => step < SpaceSize ? CandidateFor(step) : null;

    /// <summary>How far through the sweep the probe is (a step, not a number).</summary>
    public long SweepStep => step;

    public CpfProbe(InspectionStore store, string indexPath, string cursorPath)
    {
        this.store = store;
        this.indexPath = indexPath;
        this.cursorPath = cursorPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(cursorPath)) ?? ".";
        this.probedPath = Path.Combine(dir, "harvester-probed-cpfs.txt");
        this.discoveredPath = Path.Combine(dir, "harvester-discovered-cpfs.txt");
        Load();
    }

    /// <summary>
    /// The next cpf worth probing: sequential from 02000000, skipping every
    /// cpf that is already in the index, already in the store, or already
    /// probed. <paramref name="isKnown"/> answers "do we know this one?".
    /// </summary>
    public string? NextCandidate(Func<string, bool> isKnown)
    {
        while (step < SpaceSize)
        {
            var cpf = CandidateFor(step);
            step++;
            if (probed.Contains(cpf)) continue;
            if (isKnown(cpf)) continue;
            SaveCursor();
            return cpf;
        }
        return null;
    }

    /// <summary>
    /// K1: classify the answer and act on it.
    ///
    /// Rows -> the cpf is an officer with pending work: the rows go through
    /// the SAME UpsertRows path the walk uses (with the probed cpf as the
    /// listing cpf, so seen_cpfs and record_cpfs stay correct and the rows
    /// show in that officer's queue), and the pairs go into the index, so
    /// the walk list grows.
    ///
    /// No rows -> the cpf is recorded as probed and nothing else happens.
    /// It does NOT go into the index: "nothing pending today" is not the
    /// same as "nobody", and the index must not fill with dead numbers.
    /// </summary>
    public ProbeResult Classify(string cpf, IReadOnlyList<JsonElement> rows)
    {
        probed.Add(cpf);
        SaveProbed();
        if (rows.Count == 0)
            return new ProbeResult(cpf, 0, false, 0, 0, 0);

        var (added, duplicates) = store.UpsertRows(rows, cpf);
        if (discovered.Add(cpf)) SaveDiscovered();
        var pairs = new List<(string Consumer, string Cpf)>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var consumer = row.TryGetProperty("consumer_number", out var cn) && cn.ValueKind == JsonValueKind.String
                ? cn.GetString() ?? "" : "";
            if (consumer.Length == 0) continue;
            pairs.Add((consumer, cpf));
        }
        var added2 = InspectionIndexBuilder.AddPairs(indexPath, pairs);
        IndexPairsAdded += added2;
        return new ProbeResult(cpf, rows.Count, true, added, duplicates, added2);
    }

    /// <summary>Does the store already hold rows for this cpf?</summary>
    public bool IsKnownInStore(string cpf)
    {
        try { return store.CountSeenUnder(cpf) > 0; }
        catch { return false; }
    }

    void Load()
    {
        try
        {
            if (File.Exists(cursorPath))
            {
                var text = File.ReadAllText(cursorPath).Trim();
                if (long.TryParse(text, out var saved) && saved >= 0 && saved <= SpaceSize) step = saved;
            }
        }
        catch { /* an unreadable cursor restarts the sweep */ }
        try
        {
            if (!File.Exists(probedPath)) { }
            foreach (var line in File.ReadAllLines(probedPath))
            {
                var t = line.Trim();
                if (t.Length == 8) probed.Add(t);
            }
        }
        catch { /* an unreadable probed set is an empty one */ }
        try
        {
            if (!File.Exists(discoveredPath)) return;
            foreach (var line in File.ReadAllLines(discoveredPath))
            {
                var t = line.Trim();
                if (t.Length == 8) discovered.Add(t);
            }
        }
        catch { /* an unreadable discovered list is an empty one */ }
    }

    void SaveCursor()
    {
        try { File.WriteAllText(cursorPath, step.ToString()); } catch { }
    }

    /// <summary>
    /// The durable list of officers the probe found. It is append-only: a
    /// cpf found once is a cpf for ever, so the walk list can never shrink
    /// below what the app has already discovered.
    /// </summary>
    void SaveDiscovered()
    {
        try
        {
            using var writer = new StreamWriter(discoveredPath, true, new UTF8Encoding(false));
            foreach (var cpf in discovered) writer.Write(cpf + "\n");
        }
        catch { }
    }

    void SaveProbed()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var cpf in probed) sb.Append(cpf).Append('\n');
            File.WriteAllText(probedPath, sb.ToString());
        }
        catch { }
    }
}

/// <summary>What one probe found.</summary>
public sealed record ProbeResult(
    string Cpf,
    int Rows,
    bool Discovered,
    int Added,
    int Duplicates,
    int IndexPairsAdded);
