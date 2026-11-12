namespace Mskpy.Infrastructure;

/// <summary>
/// L2: the first run.
///
/// A fresh laptop has no index and no store. The published folder ships a
/// seed index (seed\index.tsv) - a copy of the index the app was built
/// with - and if there is no index at the path the app is going to use,
/// the seed is copied there once and the fact is logged.
///
/// Two rules that matter:
///   - an index that already exists is NEVER overwritten. The officer's own
///     index, with every officer the app has found, is theirs;
///   - a store is NEVER seeded. The store is data, not a starting point:
///     it fills from the harvest.
/// </summary>
public static class InspectionIndexSeeder
{
    /// <summary>The seed file in the published folder.</summary>
    public const string SeedFileName = "index.tsv";

    /// <summary>Where the seed sits inside the published folder.</summary>
    public const string SeedFolderName = "seed";

    /// <summary>The full path of the seed that travels with this build.</summary>
    public static string SeedPathFor(string appFolder) =>
        Path.Combine(appFolder, SeedFolderName, SeedFileName);

    /// <summary>
    /// Copy the seed index into place if there is no index there. Returns
    /// true when the seed was copied.
    /// </summary>
    public static bool Seed(string indexPath, string seedPath, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(indexPath)) { message = "no index path"; return false; }
        try
        {
            if (File.Exists(indexPath)) { message = "an index is already there - nothing seeded"; return false; }
            if (!File.Exists(seedPath)) { message = "no seed index in this folder"; return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(indexPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(seedPath, indexPath, overwrite: false);
            message = $"seeded the index from {Path.GetFileName(seedPath)}";
            return true;
        }
        catch (Exception ex)
        {
            message = "the seed index could not be copied: " + ex.Message;
            return false;
        }
    }
}
