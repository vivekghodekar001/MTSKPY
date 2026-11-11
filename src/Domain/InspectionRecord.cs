using System.Text.Json;

namespace Mskpy.Domain;

/// <summary>
/// A single linestaff-inspection record, sourced from the server response.
/// Field names match the G1 bytecode contract and the G2/G6 real records.
/// </summary>
public sealed record InspectionRecord(
    string ConsumerNumber,
    string CpfNo,
    string VendorCode,
    string ConsumerName,
    string? Latitude,
    string? Longitude,
    string? InstallDate,
    string? SurveyStartTime,
    string? SurveyEndTime,
    double? SurveyDurationMinutes,
    string? Address,
    IReadOnlyDictionary<string, JsonElement> Raw
)
{
    public static InspectionRecord FromJson(JsonElement row)
    {
        string? S(string key) =>
            row.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
                : null;
        double? D(string key) =>
            row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.TryGetDouble(out var d) ? d : null
                : null;

        return new InspectionRecord(
            ConsumerNumber: S("consumer_number") ?? "",
            CpfNo: S("cpf_no") ?? "",
            VendorCode: S("vendor_code") ?? "",
            ConsumerName: S("consumer_name") ?? "",
            Latitude: S("latitude"),
            Longitude: S("longitude"),
            InstallDate: S("inst_date"),
            SurveyStartTime: S("survey_start_time"),
            SurveyEndTime: S("survey_end_time"),
            SurveyDurationMinutes: D("survey_duration_in_minutes"),
            Address: S("address"),
            Raw: row.ValueKind == JsonValueKind.Object
                ? row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>()
        );
    }
}
