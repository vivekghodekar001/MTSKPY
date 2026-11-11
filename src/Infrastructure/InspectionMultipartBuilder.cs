using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mskpy.Infrastructure;

/// <summary>
/// Builds the linestaff-inspection multipart upload body.
///
/// Ported from G2 MultipartBuilder.cs (AA2). Part names come from a fixed mapping,
/// never from disk filenames. Order is fixed:
///   1 data                    text/plain
///   2 Insp-<consumer>-setupPhotoWithBen.jpg      image/jpeg
///   3 Insp-<consumer>-manualHandOverPhoto.jpg    image/jpeg
///   4 Insp-<consumer>-waterFlowPhoto.jpg         image/jpeg
///   5 Insp-<consumer>-benSign.png                image/png
///   6 Insp-<consumer>-vendorRepSign.png          image/png
///
/// data JSON (H-B, frozen by the accepted upload): the 25 formik keys in bytecode
/// order (23 checks as {value, isOK, remark}; vendorPersonName/vendorPersonMobile
/// as plain strings), then the 21 A3 tail keys:
///   consumerNumber, cpf,
///   setupPhotoWithBenLat/Long/DateTime,
///   manualHandOverPhotoLat/Long/DateTime,
///   waterFlowPhotoLat/Long/DateTime,
///   surveyStartTime, surveyEndTime, surveyDuration,
///   distanceFromInst, lat, long, locationAccuracy, locationMocked,
///   vendorCode, username
///
/// reason_to_revert is never emitted: it is a returned-list field, not an upload field.
/// </summary>
public sealed class InspectionMultipartBuilder
{
    private static readonly string[] PhotoSuffixes = { "setupPhotoWithBen", "manualHandOverPhoto", "waterFlowPhoto" };
    private static readonly string[] SignSuffixes = { "benSign", "vendorRepSign" };

    public sealed class ManifestEntry
    {
        public required string PartName { get; init; }
        public required string ContentType { get; init; }
        public required long Size { get; init; }
        public required string Source { get; init; }
    }

    public sealed class BuiltBody
    {
        public required MultipartFormDataContent Content { get; init; }
        public required string DataJson { get; init; }
        public required string DataFilePath { get; init; }
        public required IReadOnlyList<ManifestEntry> Manifest { get; init; }
    }

    /// <summary>
    /// Builds the multipart body and writes data.json to disk. The file on disk
    /// is what gets posted (not an in-memory blob) per P5.
    /// Part names: Insp-<consumer_number>-<suffix>.{ext}
    /// </summary>
    public BuiltBody Build(
        string consumerNumber,
        string dataJson,
        IReadOnlyList<string> photoPaths,      // 3 photos in PhotoSuffixes order
        IReadOnlyList<string> signPaths,       // 2 signs in SignSuffixes order
        string dataDirectory,
        IReadOnlyList<ManifestEntry>? extraManifest = null)
    {
        if (photoPaths.Count != 3)
            throw new InvalidOperationException($"Expected 3 photo paths, got {photoPaths.Count}.");
        if (signPaths.Count != 2)
            throw new InvalidOperationException($"Expected 2 signature paths, got {signPaths.Count}.");

        Directory.CreateDirectory(dataDirectory);
        var dataFilePath = Path.Combine(dataDirectory, $"data-{consumerNumber}.json");
        File.WriteAllText(dataFilePath, dataJson);

        JsonElement dataObj;
        using (var doc = JsonDocument.Parse(dataJson))
            dataObj = doc.RootElement.Clone();

        var dict = new Dictionary<string, object?>();

        // --- Full record keys first (verbatim, file order) ---
        foreach (var prop in dataObj.EnumerateObject())
        {
            dict[prop.Name] = JsonToObject(prop.Value);
        }

        // E2: never upload the returned-list field.
        dict.Remove("reason_to_revert");

        // --- Tail keys (per contract) ---
        // Prefer the values the workflow already normalised; fall back to the
        // snake_case record fields for direct builder callers.
        dict["consumerNumber"] = Prefer(dataObj, "consumerNumber", "consumer_number") ?? consumerNumber;
        dict["cpf"] = Prefer(dataObj, "cpf", "cpf_no") ?? "";

        // Photo Lat/Long/DateTime triples — captured at attach time, stored in record.
        foreach (var suffix in PhotoSuffixes)
        {
            var latKey = suffix + "Lat";
            var longKey = suffix + "Long";
            var dtKey = suffix + "DateTime";
            JsonElement vLat, vLong, vDt;
            dict[latKey] = dataObj.TryGetProperty(latKey, out vLat) ? (vLat.GetString() ?? "") : "";
            dict[longKey] = dataObj.TryGetProperty(longKey, out vLong) ? (vLong.GetString() ?? "") : "";
            dict[dtKey] = dataObj.TryGetProperty(dtKey, out vDt) ? (vDt.GetString() ?? "") : "";
        }

        dict["surveyStartTime"] = Prefer(dataObj, "surveyStartTime", "survey_start_time") ?? "";
        dict["surveyEndTime"] = Prefer(dataObj, "surveyEndTime", "survey_end_time") ?? "";
        // F1/F2: surveyDuration is a string of whole minutes (toFixed(0)), not a
        // JSON number. Prefer() stringifies numbers, so this normalises either form.
        dict["surveyDuration"] = Prefer(dataObj, "surveyDuration", "survey_duration_in_minutes") ?? "";
        // H-A: the app passes distanceFromInst / locationAccuracy / locationMocked
        // straight through as number / number / boolean. Keep the JSON types the
        // workflow produced; fall back to the same raw types.
        dict["distanceFromInst"] = TailValue(dataObj, "distanceFromInst") ?? 0;
        dict["lat"] = Prefer(dataObj, "lat", "latitude") ?? "";
        dict["long"] = Prefer(dataObj, "long", "longitude") ?? "";
        dict["locationAccuracy"] = TailValue(dataObj, "locationAccuracy") ?? 10;
        dict["locationMocked"] = TailValue(dataObj, "locationMocked") ?? false;
        dict["vendorCode"] = Prefer(dataObj, "vendorCode", "vendor_code") ?? "";
        dict["username"] = Prefer(dataObj, "username") ?? "";

        var finalJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(dataFilePath, finalJson);

        var mp = new MultipartFormDataContent();
        var manifest = new List<ManifestEntry>
        {
            new() { PartName = "data", ContentType = "text/plain", Size = Encoding.UTF8.GetByteCount(finalJson), Source = "data.json" }
        };

        // Part 1: data (text/plain, no filename) — quoted name, NO Content-Type header
        var dataContent = new StringContent(finalJson, Encoding.UTF8, "text/plain");
        dataContent.Headers.Remove("Content-Disposition");
        dataContent.Headers.Remove("Content-Type");
        dataContent.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"data\"");
        mp.Add(dataContent);

        // Parts 2-4: photos (image/jpeg) — quoted name
        foreach (var (idx, suffix) in PhotoSuffixes.Select((s, i) => (i, s)))
        {
            var path = photoPaths[idx];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException($"Missing photo part: {suffix} ({path}).");
            var bytes = File.ReadAllBytes(path);
            var part = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } };
            var partName = $"Insp-{consumerNumber}-{suffix}.jpg";
            var cd = $"form-data; name=\"{partName}\"; filename=\"{partName}\"";
            part.Headers.Remove("Content-Disposition");
            part.Headers.TryAddWithoutValidation("Content-Disposition", cd);
            mp.Add(part);
            manifest.Add(new ManifestEntry
            {
                PartName = partName,
                ContentType = "image/jpeg",
                Size = bytes.Length,
                Source = path
            });
        }

        // Parts 5-6: signatures (image/png) — quoted name
        foreach (var (idx, suffix) in SignSuffixes.Select((s, i) => (i, s)))
        {
            var path = signPaths[idx];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException($"Missing signature part: {suffix} ({path}).");
            var bytes = File.ReadAllBytes(path);
            var part = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
            var partName = $"Insp-{consumerNumber}-{suffix}.png";
            var cd = $"form-data; name=\"{partName}\"; filename=\"{partName}\"";
            part.Headers.Remove("Content-Disposition");
            part.Headers.TryAddWithoutValidation("Content-Disposition", cd);
            mp.Add(part);
            manifest.Add(new ManifestEntry
            {
                PartName = partName,
                ContentType = "image/png",
                Size = bytes.Length,
                Source = path
            });
        }

        if (extraManifest != null)
            foreach (var e in extraManifest) manifest.Add(e);

        // Debug manifest line (G2 pattern)
        Debug.WriteLine("INSPECTION MULTIPART MANIFEST: " + string.Join(" | ", manifest.Select(m => $"{m.PartName}={m.Size}B")));

        return new BuiltBody
        {
            Content = mp,
            DataJson = finalJson,
            DataFilePath = dataFilePath,
            Manifest = manifest
        };
    }

    /// <summary>Returns the first present, non-null value among <paramref name="keys"/>.</summary>
    private static string? Prefer(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        return null;
    }

    /// <summary>Returns the raw JSON value (number/boolean/string preserved).</summary>
    private static object? TailValue(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return JsonToObject(value);
    }

    private static object? JsonToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => value.EnumerateArray().Select(JsonToObject).ToList(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        _ => value.ToString()
    };
}
