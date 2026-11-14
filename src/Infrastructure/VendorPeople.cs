using System.Text;
using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// C5: the officer's own book of installers.
///
/// A linestaff officer inspects for the same people over and over, and
/// typing the same name and mobile into every survey is how a wrong mobile
/// gets into a record. The book is the officer's own: it lives in the app's
/// data folder, it is never sent anywhere, and the app only ever offers
/// what is in it.
///
/// The file is vendor-people.json: [{"name": ..., "mobile": ...,
/// "lastUsedAt": ...}]. It is written atomically - a temporary file and a
/// move - so a crash mid-write cannot leave a half file behind, and a file
/// that cannot be read falls back to an empty book instead of taking the
/// app down with it.
/// </summary>
public sealed class VendorPeople
{
    /// <summary>The file in the app's data folder.</summary>
    public const string FileName = "vendor-people.json";

    /// <summary>One entry: who, and the mobile last used for them.</summary>
    public sealed record Person(string Name, string Mobile, DateTimeOffset LastUsedAt, string? Vendor);

    readonly string path;
    readonly List<Person> people = new();

    /// <summary>C5: set when the file could not be read. Logged once.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Where the book lives.</summary>
    public string FilePath => path;

    public IReadOnlyList<Person> People => people;

    public VendorPeople(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", FileName);
        Load();
    }

    void Load()
    {
        people.Clear();
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;
            var entries = JsonSerializer.Deserialize<List<Entry>>(json);
            if (entries == null) return;
            foreach (var e in entries)
            {
                if (e?.Name == null) continue;
                var name = e.Name.Trim();
                if (name.Length == 0) continue;
                people.Add(new Person(name, (e.Mobile ?? "").Trim(), e.LastUsedAt ?? DateTimeOffset.MinValue, e.Vendor));
            }
        }
        catch (Exception ex)
        {
            // A file that cannot be read is an empty book, not a broken app.
            people.Clear();
            LoadWarning = "The installer list could not be read (" + ex.Message + "); it starts empty.";
        }
    }

    /// <summary>Write the book atomically: a temporary file, then a move.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var entries = people.Select(p => new Entry
        {
            Name = p.Name,
            Mobile = p.Mobile,
            LastUsedAt = p.LastUsedAt,
            Vendor = p.Vendor
        }).ToList();
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The entry with this name (case-insensitive), or null.</summary>
    public Person? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim();
        return people.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entries whose name starts with what has been typed.</summary>
    public IReadOnlyList<Person> Suggest(string? prefix)
    {
        var wanted = (prefix ?? "").Trim();
        if (wanted.Length == 0) return Array.Empty<Person>();
        return people
            .Where(p => p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name.Length)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    /// <summary>Add or update an entry and remember when it was last used.</summary>
    public void Add(string name, string mobile, string? vendor = null, DateTimeOffset? lastUsedAt = null)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return;
        var existing = people.FirstOrDefault(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase));
        var when = lastUsedAt ?? DateTimeOffset.Now;
        if (existing == null)
        {
            people.Add(new Person(clean, (mobile ?? "").Trim(), when, vendor));
        }
        else
        {
            var index = people.IndexOf(existing);
            var mobile2 = string.IsNullOrWhiteSpace(mobile) ? existing.Mobile : mobile.Trim();
            var vendor2 = vendor ?? existing.Vendor;
            people[index] = new Person(clean, mobile2, when, vendor2);
        }
    }

    /// <summary>Rename an entry. The mobile is kept unless a new one is given.</summary>
    public bool Rename(string name, string newName, string? newMobile = null)
    {
        var clean = (newName ?? "").Trim();
        if (clean.Length == 0) return false;
        var existing = people.FirstOrDefault(p => string.Equals(p.Name, name ?? "", StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;
        var index = people.IndexOf(existing);
        var mobile = string.IsNullOrWhiteSpace(newMobile) ? existing.Mobile : newMobile!.Trim();
        people[index] = new Person(clean, mobile, existing.LastUsedAt, existing.Vendor);
        return true;
    }

    /// <summary>Remove an entry. A removed entry is never offered again.</summary>
    public bool Remove(string name)
    {
        var existing = people.FirstOrDefault(p => string.Equals(p.Name, name ?? "", StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;
        people.Remove(existing);
        return true;
    }

    /// <summary>What picking a person from the book does to the mobile box.</summary>
    public enum Pick
    {
        /// <summary>The mobile box was empty: it takes the person's mobile.</summary>
        Fill,
        /// <summary>The mobile box holds something else: the officer is asked.</summary>
        Ask,
        /// <summary>The mobile box already holds the person's mobile: nothing changes.</summary>
        Keep
    }

    /// <summary>
    /// C5: picking a person never overwrites a mobile the officer has typed.
    /// An empty box takes the person's mobile; a box with the same mobile is
    /// left alone; a box with a different one has to be confirmed.
    /// </summary>
    public static Pick DecidePick(string? typedMobile, string? personMobile)
    {
        var typed = (typedMobile ?? "").Trim();
        var person = (personMobile ?? "").Trim();
        if (typed.Length == 0) return Pick.Fill;
        if (string.Equals(typed, person, StringComparison.OrdinalIgnoreCase)) return Pick.Keep;
        return Pick.Ask;
    }

    class Entry
    {
        public string? Name { get; set; }
        public string? Mobile { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
        public string? Vendor { get; set; }
    }
}
