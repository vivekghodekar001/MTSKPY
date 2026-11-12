using System.Text;

namespace Mskpy.Infrastructure;

/// <summary>
/// K4: the import path - the real escape hatch.
///
/// The walk list is 3,361 cpfs because that is the only officer list
/// anyone ever handed the app. If MSEDCL ever hands over a proper
/// employee roster, the app must be able to absorb it with no code change
/// and no new build: drop a file in the app's data folder and it is merged
/// at the next start.
///
/// The file is <c>index-import.tsv</c> in
/// %LOCALAPPDATA%\RenewableEnergyDesktop. Two shapes are accepted:
///
///   consumer_number<TAB>cpf_no   a pair, exactly like index.tsv
///   cpf_no                        one column: an officer with no consumer
///                                 named - the cpf joins the walk list on a
///                                 line of its own
///
/// After it is merged the file is renamed <c>index-import.done</c>, so the
/// same file is never merged twice.
/// </summary>
public sealed class InspectionIndexImporter
{
    /// <summary>The file that is dropped in the app's data folder.</summary>
    public const string ImportFileName = "index-import.tsv";

    /// <summary>The file it is renamed to after it has been merged.</summary>
    public const string DoneFileName = "index-import.done";

    /// <summary>Where the cumulative count of imported entries is kept.</summary>
    public const string CountFileName = "index-import.count";

    /// <summary>What an import did.</summary>
    public sealed record Result(int Entries, int Cpfs, int PairsAdded, string? Path, bool Merged);

    /// <summary>
    /// Merge the import file into the index, if there is one waiting. The
    /// import is renamed afterwards, so it is merged exactly once.
    /// </summary>
    public static Result MergeIfPresent(string importPath, string indexPath)
    {
        if (!File.Exists(importPath)) return new Result(0, 0, 0, null, false);

        int entries = 0, cpfs = 0;
        var pairs = new List<(string Consumer, string Cpf)>();
        try
        {
            foreach (var line in File.ReadAllLines(importPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                var consumer = parts[0].Trim();
                if (consumer.Length == 0) continue;
                var cpf = parts.Length >= 2 ? parts[1].Trim() : consumer;
                if (cpf.Length == 0) continue;
                entries++;
                if (!pairs.Any(p => p.Cpf == cpf)) cpfs++;
                pairs.Add((consumer, cpf));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The import file could not be read: " + ex.Message, ex);
        }

        var added = InspectionIndexBuilder.AddPairs(indexPath, pairs);

        // The import is merged once and once only.
        var donePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(importPath)) ?? ".", DoneFileName);
        try { File.Move(importPath, donePath, overwrite: true); } catch { }

        // The cumulative count, so the Diagnostics page can show it after a
        // restart.
        var countPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(importPath)) ?? ".", CountFileName);
        var total = ReadCount(countPath) + entries;
        try { File.WriteAllText(countPath, total.ToString()); } catch { }

        // A durable record of what was imported and when.
        try
        {
            var logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(importPath)) ?? ".", "index-import.log");
            File.AppendAllText(logPath,
                $"{DateTime.Now:s} imported {entries} entries ({cpfs} cpfs, {added} new pairs) from {Path.GetFileName(importPath)} into {Path.GetFileName(indexPath)}\n");
        }
        catch { }

        return new Result(entries, cpfs, added, importPath, true);
    }

    /// <summary>How many entries have been imported in total.</summary>
    public static int ReadCount(string countPath)
    {
        try
        {
            if (!File.Exists(countPath)) return 0;
            return int.TryParse(File.ReadAllText(countPath).Trim(), out var n) ? n : 0;
        }
        catch { return 0; }
    }
}
