using System.Text.Json;
namespace Mskpy.Client;

public sealed record InspectionCheck(string Key, string Label, string Source, string Section);
public static class InspectionFormSchema
{
    public static readonly InspectionCheck[] Checks ={
  new("commDate","Date of Commissioning","commision_date","Part A"),
  new("contManu","Controller Manufacturer","contrlr_manuft_name","Part A"),
  new("contSerial","Controller Serial No","contrlr_serial_no","Part A"),
  new("imeiSerial","IMEI Serial No","imei_serail_no","Part A"),
  new("pumpManu","Pump Manufacturer","pump_manu_name","Part A"),
  new("pumpLoad","Pump Capacity (HP)","pump_load","Part A"),
  new("pumpSerial","Pump Serial No","pump_serial_no","Part A"),
  new("motMaxDynHead","Motor Max Dynamic Head (Meter)","pump_head","Part A"),
  new("panelManu","Panel Manufacturer","panel_manu_name","Part A"),
  new("panelKW","Panel Capacity (Wp)","panel_kw","Part A"),
  new("noOfPanels","No of Panels","no_of_panels","Part A"),
  new("panelSerials","Panel Serials","","Part A"),
  new("tiltProvided","Tilt adjustment provided?","tilt_structure_provided_y_n","Part A"),
  new("rfidTagPasted","RFID Tag Pasted?","rfid_tag_pasted_y_n","Part A"),
  new("minPVArrayCap","Min PV Array Capacity (Wp)","min_pv_array_capacity","Part A"),
  new("actPVArrayCap","Actual Array Capacity (Wp)","actual_pv_arrat_capacity","Part A"),
  new("systemInShadowFreeArea","System installed in shadow free area?","inst_in_shawdow_free_area_y_n","Part B"),
  new("lightArresterGiven","Lightning Arrester Given?","lightening_arrester_given_y_n","Part B"),
  new("earthingProvided","Separate earthing pit provided for controller/motor/SPV?","earthing_provide_to_all_y_n","Part B"),
  new("projectNamePlate","Project name panel is in prescribed format?","proj_name_plate_y_n","Part B"),
  new("landLeveling","Is land leveling done?","is_land_in_leveling_y_n","Part B"),
  new("rccFoundation","RCC Foundation done?","rcc_foundation_completed_y_n","Part B"),
  new("waterUseIrrigation","Water use irrigation system","water_use","Part B")
 };
    public static string Text(IReadOnlyDictionary<string, JsonElement> fields, params string[] keys)
    {
        foreach (var key in keys) if (fields.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null && v.ToString().Trim().Length > 0) return v.ToString(); return "";
    }
    public static string CheckValue(InspectionCheck check, IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (fields.TryGetValue(check.Key, out var saved) && saved.ValueKind == JsonValueKind.Object && saved.TryGetProperty("value", out var value)) return value.ToString();
        if (check.Key == "panelSerials") return string.Join(", ", Enumerable.Range(1, 30).Select(i => Text(fields, "panel_serial_no_" + i)).Where(x => x.Length > 0));
        return Text(fields, check.Source);
    }
}
