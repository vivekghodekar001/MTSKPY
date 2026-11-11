using System.Text.RegularExpressions;

namespace Mskpy.Windows;

// These rules are reconstructed from the exact APK's Formik/Yup schemas.
// They are independent of production submission and do not provide location evidence.
public sealed record InspectionCheck(string Field, string Label, string OriginalRecordField, string Part);
public sealed record CheckAnswer(string Correctness, string Remark);
public static class SurveyRules
{
    public static readonly InspectionCheck[] InspectionChecks =
    [
        new("commDate", "Commissioning date", "commision_date", "A"),
        new("rfidTagPasted", "RFID tag pasted", "rfid_tag_pasted_y_n", "A"),
        new("tiltProvided", "Tilt structure provided", "tilt_structure_provided_y_n", "A"),
        new("motMaxDynHead", "Maximum dynamic head", "pump_head", "A"),
        new("contManu", "Controller manufacturer", "contrlr_manuft_name", "A"),
        new("contSerial", "Controller serial number", "contrlr_serial_no", "A"),
        new("imeiSerial", "IMEI serial number", "imei_serail_no", "A"),
        new("pumpManu", "Pump manufacturer", "pump_manu_name", "A"),
        new("pumpLoad", "Pump load", "pump_load", "A"),
        new("pumpSerial", "Pump serial number", "pump_serial_no", "A"),
        new("panelManu", "Panel manufacturer", "panel_manu_name", "A"),
        new("panelKW", "Panel capacity", "panel_kw", "A"),
        new("noOfPanels", "Number of panels", "no_of_panels", "A"),
        new("panelSerials", "Panel serial numbers", "", "A"),
        new("minPVArrayCap", "Minimum PV array capacity", "min_pv_array_capacity", "A"),
        new("actPVArrayCap", "Actual PV array capacity", "actual_pv_arrat_capacity", "A"),
        new("systemInShadowFreeArea", "System installed in shadow-free area", "inst_in_shawdow_free_area_y_n", "B"),
        new("lightArresterGiven", "Lightning arrester provided", "lightening_arrester_given_y_n", "B"),
        new("earthingProvided", "Earthing provided", "earthing_provide_to_all_y_n", "B"),
        new("projectNamePlate", "Project name plate", "proj_name_plate_y_n", "B"),
        new("landLeveling", "Land levelling", "is_land_in_leveling_y_n", "B"),
        new("rccFoundation", "RCC foundation completed", "rcc_foundation_completed_y_n", "B"),
        new("waterUseIrrigation", "Water used for irrigation", "water_use", "B")
    ];

    public static Dictionary<string, string> ValidateInspection(IReadOnlyDictionary<string, CheckAnswer> answers)
    {
        var errors = new Dictionary<string, string>();
        foreach (var check in InspectionChecks)
        {
            if (!answers.TryGetValue(check.Field, out var answer) || answer.Correctness is not ("Y" or "N"))
                errors[check.Field] = "Please attend all checks.";
            else if (answer.Correctness == "N" && string.IsNullOrWhiteSpace(answer.Remark))
                errors[check.Field] = "Add a remark for an incorrect check.";
        }
        return errors;
    }

    public static Dictionary<string, string> ValidateJsr(IReadOnlyDictionary<string, string> fields, bool anyNetworkSelected, int sitePhotoCount)
    {
        var errors = new Dictionary<string, string>();
        string V(string key) => fields.TryGetValue(key, out var value) ? value : "";
        void Required(string key) { if (string.IsNullOrWhiteSpace(V(key))) errors[key] = "Required"; }
        void Selected(string key) { if (string.IsNullOrWhiteSpace(V(key)) || V(key) == "Select") errors[key] = "Please select."; }
        void Numeric(string key) { Required(key); if (!Regex.IsMatch(V(key), @"^-?\d+(\.\d+)?$")) errors[key] = "Enter a number."; }
        Required("remark");
        foreach (var key in new[] { "sourceWidth", "sourceDepth", "shadowFreeSpace", "distanceFromLTLine", "alternateMobile" }) Numeric(key);
        if (V("alternateMobile").Length < 10) errors["alternateMobile"] = "Enter at least 10 characters.";
        foreach (var key in new[] { "source", "pumpHead", "landUnderShadow", "mobileNetwork", "liveAgConnection", "benPresent" }) Selected(key);
        if (V("source") == "Well") Selected("sourceRailing");
        if (V("liveAgConnection") == "Yes") { Required("existingLiveConsNo"); if (V("existingLiveConsNo").Length < 12) errors["existingLiveConsNo"] = "Enter at least 12 characters."; }
        if (V("liveAgConnection") == "No") Selected("anyPDConnection");
        if (V("anyPDConnection") == "Yes") { Required("pdConsNo"); if (V("pdConsNo").Length < 12) errors["pdConsNo"] = "Enter at least 12 characters."; }
        if (V("benPresent") == "No") { Required("presentPersonName"); Selected("relationWithBen"); }
        if (V("mobileNetwork") == "Yes" && !anyNetworkSelected) errors["networks"] = "Please select available networks.";
        if (sitePhotoCount < 3) errors["photos"] = "Please upload site photos.";
        return errors;
    }

    public static string? ValidateMilestone(string remark, int photoCount)
    {
        if (string.IsNullOrWhiteSpace(remark)) return "Please add remark";
        if (remark.Length > 64) return "Remark must be at most 64 characters.";
        if (photoCount < 1) return "Please add at least 1 photo.";
        return null;
    }
}
