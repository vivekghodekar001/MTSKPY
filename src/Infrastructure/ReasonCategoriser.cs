namespace Mskpy.Infrastructure;

/// <summary>
/// M1: turn a free-text <c>reason_to_revert</c> into one of the eight
/// categories the return reasons fall into. The strings are matched verbatim
/// (some are Marathi in the field) and are never translated: the officer's
/// words are kept as they were written, only a category label is added.
///
/// The function is pure: the same string always yields the same category, and
/// it does no I/O, so it is trivially testable and can be run over the whole
/// store at any time.
/// </summary>
public static class ReasonCategoriser
{
    public const string Photo = "photo";
    public const string SystemIncomplete = "system_incomplete";
    public const string WaterDischarge = "water_discharge";
    public const string WaterSource = "water_source";
    public const string BeneficiaryAbsent = "beneficiary_absent";
    public const string WrongData = "wrong_data";
    public const string Unfixable = "unfixable";
    public const string Other = "other";

    public static readonly string[] All =
    [
        Photo, SystemIncomplete, WaterDischarge, WaterSource,
        BeneficiaryAbsent, WrongData, Unfixable, Other
    ];

    /// <summary>Classify one reason string. Null/blank → <see cref="Other"/>.</summary>
    public static string Categorise(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Other;
        var s = reason.ToLowerInvariant();
        if (ContainsAny(s, "not working", "not installed", "not instal", "pump not install", "solar pump not",
            "motor issue", "panel covered", "tree branch", "damaged", "broken", "incomplete system",
            "not installed as per")) return Unfixable;
        if (ContainsAny(s, "water discharge", "water flow", "discharge not", "flow not seen",
            "discharge photo", "water flow photo")) return WaterDischarge;
        if (ContainsAny(s, "water source", "source mismatch", "bore well", "borewell", "well photo",
            "shettale", "source not seen", "source not")) return WaterSource;
        if (ContainsAny(s, "beneficiary not", "beneficiary absent", "not available", "not present",
            "no one present", "company represent", "representative not")) return BeneficiaryAbsent;
        if (ContainsAny(s, "lat long", "latlong", "lat/long", "lat-long", "coordinate", "co-ordinate",
            "location", "panel mismatch", "pannel mismatch", "panel no", "panel no.", "capacity",
            "policy no", "consumer", "existing connection", "ag connection", "msedcl connection", "date",
            "installation date", "wrong site", "wrong location", "wrong data", "wrong lineman",
            "panels required", "no. of panels", "all 9 panels", "13 panels", "wrong inspection",
            "reinsp", "re-insp", "re inspection")) return WrongData;
        if (ContainsAny(s, "incomplete", "pending installation", "pending for material", "pending for install",
            "pending motor", "pending install", "not submitted", "not upload", "installation report not",
            "not submit at site", "not submit from site", "not submit from actual")) return SystemIncomplete;
        if (ContainsAny(s, "photo", "image", "picture", "upload", "clear", "blur", "blurr", "not seen",
            "not clear", "not found", "missing", "not uploaded", "screen photo", "computer screen",
            "mobile photo", "repeat", "duplicate", "same photo", "fake", "photo on photo", "no photo",
            "photo not", "uniform", "lineman not", "no lineman", "no linemn")) return Photo;
        return Other;
    }

    static bool ContainsAny(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k)) return true;
        return false;
    }
}
