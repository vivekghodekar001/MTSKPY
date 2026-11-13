using Mskpy.Domain;

namespace Mskpy.Infrastructure;

/// <summary>
/// M5: detect whether a record changed on the server since the officer opened
/// it, so a submit sends the current version rather than last week's.
/// </summary>
public static class RecordChangeDetector
{
    public static bool Changed(InspectionRecord original, InspectionRecord fresh)
    {
        if (original == null || fresh == null) return false;
        var a = System.Text.Json.JsonSerializer.Serialize(original.Raw);
        var b = System.Text.Json.JsonSerializer.Serialize(fresh.Raw);
        return !string.Equals(a, b, StringComparison.Ordinal);
    }
}
