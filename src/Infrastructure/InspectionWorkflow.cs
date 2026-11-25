using System.Globalization;
using System.Net;
using System.Text.Json;
using Mskpy.Domain;
using Mskpy.Windows;

namespace Mskpy.Infrastructure;

/// <summary>
/// Outcome of a linestaff-inspection submission.
/// </summary>
public sealed record SubmitOutcome(
    bool Ok,
    HttpStatusCode StatusCode,
    string Body,
    string? Error,
    string? DataFilePath,
    IReadOnlyList<InspectionMultipartBuilder.ManifestEntry> Manifest,
    string? LocationSummary = null,
    // C3: the position the survey was submitted from, so the row stays auditable.
    string? SubmissionLat = null,
    string? SubmissionLong = null,
    string? LocationSource = null
);

/// <summary>
/// Outcome of the post-submit verify (P10/P11).
/// </summary>
public sealed record VerifyOutcome(
    bool Ok,
    int Count,
    string? UpdatedDt,
    string? Error
);

/// <summary>
/// Application use case for linestaff-inspection.
/// Orchestrates transport → domain → multipart → upload → verify.
/// Lives in Infrastructure because it composes Infrastructure services;
/// the Presentation layer consumes it directly.
/// </summary>
public sealed class InspectionWorkflow
{
    private readonly InspectionGateway gateway;
    private readonly InspectionMultipartBuilder builder;
    public readonly LocationProvider location;
    private readonly string dataDirectory;

    public InspectionWorkflow(InspectionGateway gateway, InspectionMultipartBuilder builder, LocationProvider location, string? dataDirectory = null, string? vendorOverride = null)
    {
        this.gateway = gateway;
        this.builder = builder;
        this.location = location;
        this.VendorOverride = vendorOverride;
        this.dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", "inspection-data");
    }

    /// <summary>
    /// Escape hatch for the vendor. When set it replaces the record's vendor for
    /// the header AND the body, so the two can never disagree — A1's guarantee
    /// holds either way. Empty (the default) means the record's own vendor_code.
    /// </summary>
    public string? VendorOverride { get; set; }

    /// <summary>List pending inspection applications for the session.</summary>
    public async Task<(HttpStatusCode StatusCode, string Body, IReadOnlyList<InspectionRecord> Records)> ListAsync(CancellationToken ct = default)
        => await gateway.ListAsync(ct);

    /// <summary>Resolve cpf_no for a consumer number via the startup index.</summary>
    public string? ResolveCpf(string consumerNumber) => gateway.ResolveCpf(consumerNumber);

    public bool IndexLoaded => gateway.IndexLoaded;

    /// <summary>Fetch detail for a consumer; refuse on 0 or >1 match (R4).</summary>
    public Task<DetailOutcome> DetailAsync(string consumerNumber, string cpfNo, CancellationToken ct = default)
        => gateway.DetailAsync(consumerNumber, cpfNo, ct);

    /// <summary>
    /// Build the upload body (data.json written to disk) and submit it.
    /// Returns the full status and body verbatim. No retry on 2xx (R7).
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync(
        InspectionRecord record,
        IReadOnlyList<string> photoPaths,   // 3 photos: setup, manual, waterflow
        IReadOnlyList<string> signPaths,    // 2 signs: benSign, vendorRepSign
        string? manualLat,
        string? manualLong,
        string? manualAccuracy,
        string? allowedDistanceMeter,
        bool distanceRestricted,
        IReadOnlyDictionary<string, object?>? extraData = null,
        CancellationToken ct = default,
        DateTimeOffset? formOpenTime = null,
        IReadOnlyDictionary<string, CheckAnswer>? checkAnswers = null,
        string? vendorPersonName = null,
        string? vendorPersonMobile = null,
        LocationResult? resolvedLocation = null,
        string? sessionLoginId = null,
        Action<string>? onDataFileBuilt = null)
    {
        // R1: username is the session loginId (userInfo.loginId), not the record's
        // cpf_no. They are different fields that have been equal in every test.
        var sessionLogin = !string.IsNullOrWhiteSpace(sessionLoginId)
            ? sessionLoginId!
            : gateway.transport.LoginId;
        // --- Location resolution (P6/P7) ---
        // P4: the caller may resolve once, re-stamp the photos with that same
        // pair and pass it here, so blob, EXIF and upload agree exactly.
        var loc = resolvedLocation ?? await location.ResolveAsync(
            manualLat, manualLong, manualAccuracy,
            record.Latitude, record.Longitude, ct);

        // P2: the check answers come from the form's Yes/No + remark controls.
        // Unanswered fields keep the accepted default (Y, empty remark).
        (string? IsOk, string? Remark) AnswerFor(string field) =>
            checkAnswers != null && checkAnswers.TryGetValue(field, out var answer)
                ? (answer.Correctness, answer.Remark)
                : (null, null);

        // --- H-B: formik-equivalent key set, then the A3 tail in F3 order ---
        // The app does not spread the returned record. It spreads the two formik
        // `values` objects, whose keys are these 25 camelCase fields.
        var dataDict = new Dictionary<string, object?>();
        foreach (var (formikKey, recordKey) in FormikFieldMap)
        {
            if (PlainStringFields.Contains(formikKey))
            {
                var recordValue = record.Raw.TryGetValue(recordKey, out var rawPerson)
                    ? JsonToObject(rawPerson) ?? ""
                    : "";
                dataDict[formikKey] = formikKey == "vendorPersonName"
                    ? vendorPersonName ?? recordValue
                    : vendorPersonMobile ?? recordValue;
                continue;
            }
            if (formikKey == "panelSerials")
            {
                dataDict[formikKey] = BuildCheckField(JoinPanelSerials(record.Raw), AnswerFor(formikKey));
                continue;
            }
            if (formikKey == "pumpLoad")
            {
                // R2 (bytecode 531989-532005): ''.concat(record.pump_load, ' HP').
                dataDict[formikKey] = BuildCheckField(ConcatPumpLoad(record.Raw), AnswerFor(formikKey));
                continue;
            }
            if (formikKey == "commDate")
            {
                // R2 (bytecode 531859-531889): 'NA' when falsy, otherwise
                // toDDMONYYYY(new Date(record.commision_date)).
                dataDict[formikKey] = BuildCheckField(FormatCommDate(record.Raw), AnswerFor(formikKey));
                continue;
            }
            if (ConvertValueFields.Contains(formikKey))
            {
                // U1.1 (bytecode 531812): convertValue on .value only.
                // Y->Yes, N->No, I->Inside, O->Outside, anything else -> NA.
                var recordValue = record.Raw.TryGetValue(recordKey, out var rawVal)
                    ? JsonToObject(rawVal) ?? ""
                    : "";
                dataDict[formikKey] = BuildCheckField(ConvertValueFlag(recordValue), AnswerFor(formikKey));
                continue;
            }
            if (ToStringOrNaFields.Contains(formikKey))
            {
                // U1.2 (bytecode 531915-531924): .toString() then "NA" when falsy.
                dataDict[formikKey] = BuildCheckField(ToStringOrNa(record.Raw, recordKey), AnswerFor(formikKey));
                continue;
            }
            if (formikKey == "waterUseIrrigation")
            {
                // U1.3: raw water_use, "NA" when falsy.
                var recordValue = record.Raw.TryGetValue(recordKey, out var rawVal)
                    ? JsonToObject(rawVal) ?? ""
                    : "";
                if (string.IsNullOrEmpty(recordValue?.ToString())) recordValue = "NA";
                dataDict[formikKey] = BuildCheckField(recordValue, AnswerFor(formikKey));
                continue;
            }
            var fieldValue = record.Raw.TryGetValue(recordKey, out var raw)
                ? JsonToObject(raw) ?? ""
                : "";
            // F1: every non-person field is a check shaped {value, isOK, remark}.
            dataDict[formikKey] = BuildCheckField(fieldValue, AnswerFor(formikKey));
        }

        dataDict["consumerNumber"] = record.ConsumerNumber;
        dataDict["cpf"] = record.CpfNo;

        // P4: the position actually used for the upload wins (manual override ->
        // Windows geolocator -> installation coordinates). Photo EXIF is stamped
        // with this same pair; the record coordinates are only the fallback.
        var blobLat = loc.Lat ?? record.Latitude ?? "";
        var blobLong = loc.Long ?? record.Longitude ?? "";

        // H-A: photo Lat/Long/DateTime stay strings; a missing role emits the
        // literal "undefined", which is what '' + undefined produces in the app.
        var photoSuffixes = new[] { "setupPhotoWithBen", "manualHandOverPhoto", "waterFlowPhoto" };
        for (var i = 0; i < photoSuffixes.Length; i++)
        {
            var suffix = photoSuffixes[i];
            var rolePresent = i < photoPaths.Count &&
                              !string.IsNullOrWhiteSpace(photoPaths[i]) &&
                              File.Exists(photoPaths[i]);
            dataDict[suffix + "Lat"] = rolePresent ? blobLat : "undefined";
            dataDict[suffix + "Long"] = rolePresent ? blobLong : "undefined";
            if (extraData != null && extraData.TryGetValue(suffix + "DateTime", out var vDt))
                dataDict[suffix + "DateTime"] = vDt;
            else if (record.Raw.TryGetValue(suffix + "DateTime", out var rDt))
                dataDict[suffix + "DateTime"] = JsonToObject(rDt);
            else if (!rolePresent)
                dataDict[suffix + "DateTime"] = "undefined";
        }

        // F1/F2: the app writes surveyStartTime/surveyEndTime from the session and
        // surveyDuration as calculateTimeDifferenceInMinutes(...).toFixed(0), i.e.
        // whole minutes as a string, never a JSON number.
        // H-A: end is captured at submit, start is held from form open.
        var submitAt = DateTimeOffset.UtcNow;
        var openedAt = formOpenTime ?? submitAt;
        dataDict["surveyStartTime"] = openedAt.UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        dataDict["surveyEndTime"] = submitAt.UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        // S6: surveyDuration = real elapsed minutes; floor to 1 only under a minute.
        var wholeMinutes = (int)Math.Floor((submitAt - openedAt).TotalMinutes);
        if (wholeMinutes < 1) wholeMinutes = 1;
        dataDict["surveyDuration"] = wholeMinutes
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        // P4 (raw types, not strings): distanceFromInst is the haversine distance
        // between the position actually used and the installation coordinates;
        // locationAccuracy is the geolocator accuracy when it exists, else the
        // previous fixed 10; locationMocked is true whenever the position is not
        // a live geolocator fix (manual override or installation fallback).
        var distanceFromInst = 0d;
        if (double.TryParse(blobLat, NumberStyles.Float, CultureInfo.InvariantCulture, out var usedLat) &&
            double.TryParse(blobLong, NumberStyles.Float, CultureInfo.InvariantCulture, out var usedLong) &&
            double.TryParse(record.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var instLat) &&
            double.TryParse(record.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var instLong))
        {
            // C3: whole metres, rounded - the distance is a small real number.
            distanceFromInst = Math.Round(GeoDistance.Meters(usedLat, usedLong, instLat, instLong), 0);
        }
        // V1: locationAccuracy is 0 whenever the position is the record's
        // installation coordinates (every run on this machine). If a real
        // geolocator fix ever exists, use its own accuracy instead — one
        // condition, not a constant.
        object locationAccuracy = 0d;
        if (double.TryParse(loc.Accuracy, NumberStyles.Float, CultureInfo.InvariantCulture, out var accuracyValue))
            locationAccuracy = accuracyValue;

        dataDict["distanceFromInst"] = distanceFromInst;
        dataDict["lat"] = blobLat;
        dataDict["long"] = blobLong;
        dataDict["locationAccuracy"] = locationAccuracy;
        dataDict["locationMocked"] = loc.Mocked;

        var locationSummary =
            $"Location: source={loc.Source}, lat={blobLat}, long={blobLong}, " +
            $"accuracy={locationAccuracy}, mocked={loc.Mocked}, " +
            $"distanceFromInst={distanceFromInst.ToString("0.##", CultureInfo.InvariantCulture)}m" +
            (loc.Error != null ? $", error={loc.Error}" : "");
        // A1: the vendor is the record's own vendor_code. The same value goes in
        // the body and in the client-vendorCode header — there is no session vendor.
        // An explicit override replaces BOTH, so header and body still agree.
        var vendorCode = !string.IsNullOrWhiteSpace(VendorOverride)
            ? VendorOverride!.Trim()
            : (record.VendorCode ?? "");
        var vendorSource = !string.IsNullOrWhiteSpace(VendorOverride) ? "override" : "record";
        dataDict["vendorCode"] = vendorCode;
        // R1: username = session loginId (bytecode 532909-532915); cpf = the
        // record's own cpf_no (bytecode 532714-532725).
        dataDict["username"] = sessionLogin;


        var dataJson = JsonSerializer.Serialize(dataDict, new JsonSerializerOptions { WriteIndented = false });

        // --- Build multipart body; data.json written to disk (P5) ---
        // Part names: Insp-<consumer_number>-<suffix>.{ext}
        var built = builder.Build(record.ConsumerNumber, dataJson, photoPaths, signPaths, dataDirectory);
        // 4B1: the attempt's data.json path is recorded before the POST, so a
        // failed attempt keeps an inspectable body.
        onDataFileBuilt?.Invoke(built.DataFilePath);

        // --- Gates (P7) ---
        var gateErrors = new List<string>();
        foreach (var part in built.Manifest)
        {
            if (part.Size < 1)
                gateErrors.Add($"Missing part: {part.PartName}");
        }
        if (!LocationProvider.IsValidCoordinatePair(blobLat, blobLong))
            gateErrors.Add(LocationProvider.UnableToAccessLocation);
        if (distanceRestricted &&
            double.TryParse(allowedDistanceMeter, NumberStyles.Float, CultureInfo.InvariantCulture, out var allowed) &&
            double.TryParse(Convert.ToString(dataDict["distanceFromInst"], CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var dist) &&
            dist > allowed)
        {
            gateErrors.Add($"Distance {dist}m exceeds allowed {allowed}m (distanceRestricted).");
        }
        if (gateErrors.Count > 0)
            return new SubmitOutcome(false, HttpStatusCode.BadRequest, "", string.Join("; ", gateErrors), built.DataFilePath, built.Manifest, locationSummary, blobLat, blobLong, loc.Source.ToString());

        // --- Build the POST request ---
        var host = gateway.transport.InspectionHost;
        var request = new HttpRequestMessage(HttpMethod.Post,
            host + "/mtskpy/linestaff-inspection/upload-survey")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        // Frozen baseline: HTTP/1.1 exact, no 100-continue.
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("client-loginid", sessionLogin); // session loginId (userInfo.loginId)
        request.Headers.TryAddWithoutValidation("client-app-name", "RE");
        request.Headers.TryAddWithoutValidation("client-os", "ANDROID");
        request.Headers.TryAddWithoutValidation("client-os-version", "36");
        request.Headers.TryAddWithoutValidation("client-version", "2.6.4");
        request.Headers.TryAddWithoutValidation("client-device-id", Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("client-device", "Windows-Desktop");
        request.Headers.TryAddWithoutValidation("user-agent", "okhttp/4.12.0");
        if (!string.IsNullOrWhiteSpace(vendorCode))
            request.Headers.TryAddWithoutValidation("client-vendorCode", vendorCode);
        request.Content = built.Content;

        var manifestText = string.Join("\n  ", built.Manifest.Select(m =>
            $"{m.PartName} ({m.ContentType}, {m.Size}B, source={m.Source})"));

        // H-B diagnostic: emitted key set vs. the formik key set + the A3 tail.
        var keyDiff = DescribeKeyDiff(built.DataJson);

        var (status, body) = await gateway.transport.UploadAsync(request,
            $"MANIFEST ({built.Manifest.Count} parts):\n  {manifestText}\n" +
            $"BLOB KEY DIFF: {keyDiff}\n" +
            $"Session: username={sessionLogin}, cpf={record.CpfNo}" +
            (string.Equals(sessionLogin, record.CpfNo, StringComparison.Ordinal)
                ? " (same)"
                : " (DIFFER: username is the session loginId, cpf is the record's cpf_no)") + "\n" +
            $"Source: distance={dataDict["distanceFromInst"]}, locationMocked={dataDict["locationMocked"]}, " +
            $"locationSource={loc.Source}, mocked={loc.Mocked}, accuracy={locationAccuracy}, " +
            // A1: one vendor value, logged once pre-send. Header and body are the same.
            $"vendor={vendorCode}, vendorSource={vendorSource}", ct);

        // R7: No retry on 2xx. Never infer success from a status code.
        return new SubmitOutcome(status == HttpStatusCode.OK, status, body, null, built.DataFilePath, built.Manifest, locationSummary, blobLat, blobLong, loc.Source.ToString());
    }

    /// <summary>
    /// Verify after submit (P11): on 2xx and hasErrors:false, GET the record with
    /// userName=cpf_no. Print count and updated_dt. Warn if count > 1.
    /// </summary>
    public async Task<VerifyOutcome> VerifyAsync(string consumerNumber, string cpfNo, CancellationToken ct = default)
    {
        var (status, body, doc) = await gateway.transport.DetailAsync(consumerNumber, cpfNo, ct);
        if (doc == null)
            return new VerifyOutcome(false, 0, null, $"HTTP {(int)status}: {body}");
        try
        {
            var rows = InspectionResponseParser.ParseApplications(doc.RootElement);
            var count = rows.Count;
            string? updated = null;
            bool hasErrors = false;
            if (rows.Count == 1)
            {
                var row = rows[0];
                if (row.TryGetProperty("hasErrors", out var he) && he.ValueKind == JsonValueKind.True)
                    hasErrors = true;
                if (row.TryGetProperty("updated_dt", out var ud))
                    updated = ud.GetString() ?? "";
                else if (row.TryGetProperty("updatedDate", out var ud2))
                    updated = ud2.GetString() ?? "";
            }
            if (hasErrors)
                return new VerifyOutcome(false, count, updated, $"Server reported hasErrors:true. Body: {body}");
            if (count > 1)
                return new VerifyOutcome(true, count, updated, $"WARN: count={count} > 1 for consumer_number {consumerNumber}");
            return new VerifyOutcome(true, count, updated, null);
        }
        catch (Exception ex)
        {
            return new VerifyOutcome(false, 0, null, ex.Message + ". Body: " + body);
        }
    }

    /// <summary>
    /// H-B: the two formik `values` objects, in bytecode order. Left = the camelCase
    /// key the app sends, right = the snake_case record key it is read from.
    /// </summary>
    private static readonly (string Formik, string Record)[] FormikFieldMap =
    [
        ("commDate", "commision_date"),
        ("rfidTagPasted", "rfid_tag_pasted_y_n"),
        ("tiltProvided", "tilt_structure_provided_y_n"),
        ("motMaxDynHead", "pump_head"),
        ("contManu", "contrlr_manuft_name"),
        ("contSerial", "contrlr_serial_no"),
        ("imeiSerial", "imei_serail_no"),
        ("pumpManu", "pump_manu_name"),
        ("pumpLoad", "pump_load"),
        ("pumpSerial", "pump_serial_no"),
        ("panelManu", "panel_manu_name"),
        ("panelKW", "panel_kw"),
        ("noOfPanels", "no_of_panels"),
        ("panelSerials", "panel_serial_no_1"), // built by JoinPanelSerials
        ("minPVArrayCap", "min_pv_array_capacity"),
        ("actPVArrayCap", "actual_pv_arrat_capacity"),
        ("systemInShadowFreeArea", "inst_in_shawdow_free_area_y_n"),
        ("lightArresterGiven", "lightening_arrester_given_y_n"),
        ("earthingProvided", "earthing_provide_to_all_y_n"),
        ("projectNamePlate", "proj_name_plate_y_n"),
        ("landLeveling", "is_land_in_leveling_y_n"),
        ("rccFoundation", "rcc_foundation_completed_y_n"),
        ("waterUseIrrigation", "water_use"),
        ("vendorPersonName", "vendor_person_name"),
        ("vendorPersonMobile", "vendor_person_mobile_no"),
    ];

    /// <summary>
    /// Keys whose initialValues entry is a plain string rather than a
    /// {value, isOK, remark} check object (bytecode 597862).
    /// </summary>
    private static readonly HashSet<string> PlainStringFields = new(StringComparer.Ordinal)
    {
        "vendorPersonName", "vendorPersonMobile",
    };

    /// <summary>
    /// U1.1: flag checks converted by convertValue (bytecode 531812):
    /// Y->Yes, N->No, I->Inside, O->Outside, anything else -> NA.
    /// Applied to .value only; isOK stays "Y"/"N".
    /// </summary>
    private static readonly HashSet<string> ConvertValueFields = new(StringComparer.Ordinal)
    {
        "rfidTagPasted", "tiltProvided", "systemInShadowFreeArea",
        "lightArresterGiven", "earthingProvided", "projectNamePlate",
        "landLeveling", "rccFoundation",
    };

    /// <summary>
    /// U1.2: .toString() then "NA" when falsy (bytecode pattern at 531915-531924).
    /// </summary>
    private static readonly HashSet<string> ToStringOrNaFields = new(StringComparer.Ordinal)
    {
        "motMaxDynHead", "contManu", "contSerial", "imeiSerial",
        "pumpManu", "pumpSerial", "panelManu", "panelKW", "noOfPanels",
        "minPVArrayCap", "actPVArrayCap",
    };

    /// <summary>
    /// F1: every non-person field is a check object. The UI's Yes/No buttons do
    /// setFieldValue(name, {...old, isOK: 'Y'|'N'}) and the remark box leaves
    /// value/isOK alone, so the shape at JSON.stringify is {value, isOK, remark}.
    /// P2: the form's per-check answer overrides the default (Y, "").
    /// </summary>
    private static Dictionary<string, object?> BuildCheckField(
        object? value,
        (string? IsOk, string? Remark) answer = default) =>
        new()
        {
            ["value"] = value switch
            {
                null => "",
                string s => s,
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            },
            ["isOK"] = answer.IsOk is "Y" or "N" ? answer.IsOk : "Y",
            ["remark"] = answer.Remark ?? "",
        };

    /// <summary>
    /// R2 (bytecode 531989-532005): the app sets pumpLoad.value to
    /// ''.concat(record.pump_load, ' HP'). A missing record yields JS undefined
    /// and a JSON null yields null, and both stringify inside concat exactly as
    /// JS does, so the value is "&lt;n&gt; HP" for every live record.
    /// </summary>
    private static string ConcatPumpLoad(IReadOnlyDictionary<string, JsonElement> raw)
    {
        if (!raw.TryGetValue("pump_load", out var v)) return "undefined HP";
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "null HP",
            JsonValueKind.String => (v.GetString() ?? "") + " HP",
            JsonValueKind.True => "true HP",
            JsonValueKind.False => "false HP",
            _ => v.ToString() + " HP",
        };
    }

    /// <summary>
    /// R2 (bytecode 531859-531889): commDate.value is 'NA' when commision_date
    /// is falsy, else toDDMONYYYY(new Date(commision_date)). toDDMONYYYY
    /// (bytecode 467546) = DD + '-' + Mon + '-' + YYYY, with getDate()/getMonth()/
    /// getFullYear(), i.e. the device's local date. The machine is IST, the same
    /// zone the app runs in.
    /// </summary>
    private static string FormatCommDate(IReadOnlyDictionary<string, JsonElement> raw)
    {
        if (!raw.TryGetValue("commision_date", out var v)) return "NA";
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "NA";
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        if (string.IsNullOrWhiteSpace(text)) return "NA";
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return text; // defensive: the app would render an Invalid Date
        var local = parsed.ToLocalTime();
        return $"{local.Day:00}-{Months[local.Month - 1]}-{local.Year}";
    }

    /// <summary>The exact month table used by toDDMONYYYY in the bundle.</summary>
    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Joins panel_serial_no_1..30 in order, comma separated, skipping blanks.</summary>
    private static string JoinPanelSerials(IReadOnlyDictionary<string, JsonElement> raw)
    {
        var parts = new List<string>();
        for (var i = 1; i <= 30; i++)
        {
            if (!raw.TryGetValue("panel_serial_no_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), out var v))
                continue;
            if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            // The app also drops the literal string "null" (bytecode 532079).
            if (!string.IsNullOrWhiteSpace(s) && s != "null") parts.Add(s!);
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// U1.1: convertValue (bytecode 531812): Y->Yes, N->No, I->Inside, O->Outside,
    /// anything else -> NA. Applied to .value only.
    /// </summary>
    private static string ConvertValueFlag(object? value)
    {
        var v = value?.ToString() ?? "";
        return v switch
        {
            "Y" => "Yes",
            "N" => "No",
            "I" => "Inside",
            "O" => "Outside",
            _ => "NA",
        };
    }

    /// <summary>
    /// U1.2: .toString() then "NA" when falsy (bytecode pattern at 531915-531924).
    /// JS: value.toString() — empty string is falsy -> "NA".
    /// </summary>
    private static string ToStringOrNa(IReadOnlyDictionary<string, JsonElement> raw, string key)
    {
        if (!raw.TryGetValue(key, out var v)) return "NA";
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "NA";
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        if (string.IsNullOrEmpty(text)) return "NA";
        return text;
    }

    /// <summary>A3 tail keys, in the order the app appends them.</summary>
    private static readonly string[] A3TailKeys =
    [
        "consumerNumber", "cpf",
        "setupPhotoWithBenLat", "setupPhotoWithBenLong", "setupPhotoWithBenDateTime",
        "manualHandOverPhotoLat", "manualHandOverPhotoLong", "manualHandOverPhotoDateTime",
        "waterFlowPhotoLat", "waterFlowPhotoLong", "waterFlowPhotoDateTime",
        "surveyStartTime", "surveyEndTime", "surveyDuration",
        "distanceFromInst", "lat", "long", "locationAccuracy", "locationMocked",
        "vendorCode", "username"
    ];

    /// <summary>
    /// H-B: reports keys present in the emitted blob that the formik key set /
    /// A3 tail do not define, and expected keys that are missing.
    /// </summary>
    private static string DescribeKeyDiff(string dataJson)
    {
        var expected = new HashSet<string>(FormikFieldMap.Select(m => m.Formik), StringComparer.Ordinal);
        foreach (var key in A3TailKeys) expected.Add(key);

        using var doc = JsonDocument.Parse(dataJson);
        var emitted = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        var emittedSet = new HashSet<string>(emitted, StringComparer.Ordinal);

        var extra = emitted.Where(k => !expected.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missing = expected.Where(k => !emittedSet.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var text = $"extra=[{string.Join(", ", extra)}] missing=[{string.Join(", ", missing)}]";
        System.Diagnostics.Debug.WriteLine("BLOB KEY DIFF: " + text);
        return text;
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



