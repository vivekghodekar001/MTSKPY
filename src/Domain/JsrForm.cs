namespace Mskpy.Domain;

public enum JsrFieldKind { Text, Selection, Networks, Photos }
public enum JsrCondition { Always, Well, LiveConnection, NotLiveConnection, DisconnectedConnection, BeneficiaryAbsent, Workflow122, NetworkAvailable }
public sealed record JsrFieldDefinition(string Key, string Label, JsrFieldKind Kind = JsrFieldKind.Text,
    IReadOnlyList<string>? Options = null, int MaxLength = 50, JsrCondition Condition = JsrCondition.Always);
public sealed record PhotoReference(string LocalPath, long Bytes, string Sha256);
public sealed record JsrContext(string Identity, bool IsWorkflow122, IReadOnlyList<DisplayField> Beneficiary, IReadOnlyList<DisplayField> Land);

public static class JsrForm
{
    static readonly IReadOnlyList<string> YesNo = ["Select", "Yes", "No"];
    public static readonly IReadOnlyList<string> NetworkProviders = ["BSNL", "Airtel", "Jio", "Vi"];
    public static readonly IReadOnlyList<(string Key, string Label)> PhotoRoles =
    [
        ("waterSourcePhoto", "Photo of Water Source"),
        ("landMarkPhoto", "Photo of Farmland or Landmark(Tree/Home)"),
        ("benVendEmpPhoto", "Photo with Beneficiary and MSEDCL Representative")
    ];
    // Values, ordering and conditions are derived from the exact bundle's JSR component.
    public static readonly IReadOnlyList<JsrFieldDefinition> Fields =
    [
        new("source", "Irrigation Source", JsrFieldKind.Selection, ["Select", "Canal", "Well", "River", "Borewell", "Shettale", "Lake", "No Water Source"]),
        new("sourceRailing", "Guard(Kathada) available around well?", JsrFieldKind.Selection, YesNo, Condition: JsrCondition.Well),
        new("sourceWidth", "Irrigation Source Width(Feet)"),
        new("sourceDepth", "Irrigation Source Depth(Meter)"),
        new("pumpHead", "Required Pump Head(Meter)", JsrFieldKind.Selection, ["Select", "30", "50", "70", "100"]),
        new("landUnderShadow", "Is Land Under Shadow?", JsrFieldKind.Selection, YesNo),
        new("shadowFreeSpace", "Shadow Free Space Available for Solar Panels(Sq.ft)"),
        new("mobileNetwork", "Is Mobile Network Available?", JsrFieldKind.Selection, YesNo),
        new("__networks", "Select Available Networks", JsrFieldKind.Networks, Condition: JsrCondition.NetworkAvailable),
        new("distanceFromLTLine", "Distance from LT Line(Meter)"),
        new("liveAgConnection", "Any live AG Connection in the name of beneficiary?", JsrFieldKind.Selection, YesNo),
        new("existingLiveConsNo", "Existing(Live) Consumer Number", MaxLength: 12, Condition: JsrCondition.LiveConnection),
        new("anyPDConnection", "Any permanently disconnected AG Connection in the name of beneficiary?", JsrFieldKind.Selection, YesNo, Condition: JsrCondition.NotLiveConnection),
        new("pdConsNo", "Permanently Disconnected(PD) Consumer Number", MaxLength: 12, Condition: JsrCondition.DisconnectedConnection),
        new("alternateMobile", "Beneficiary's Alternate Mobile No", MaxLength: 10),
        new("__photos", "Upload Site Photos", JsrFieldKind.Photos),
        new("vendorRepName", "Vendor Representative Name", Condition: JsrCondition.Workflow122),
        new("benPresent", "Beneficiary Present for Survey", JsrFieldKind.Selection, YesNo),
        new("presentPersonName", "Present Person Name", MaxLength: 100, Condition: JsrCondition.BeneficiaryAbsent),
        new("relationWithBen", "Relation with Beneficiary", JsrFieldKind.Selection,
            ["Select", "Father", "Mother", "Husband", "Wife", "Son", "Daughter", "Brother", "Sister", "Father In Law", "Mother In Law", "Son In Law", "Daughter In Law"], Condition: JsrCondition.BeneficiaryAbsent),
        new("remark", "Remark", MaxLength: 500)
    ];

    public static IReadOnlyDictionary<string, string> InitialValues() => Fields.Where(f => !f.Key.StartsWith("__"))
        .ToDictionary(f => f.Key, f => f.Kind == JsrFieldKind.Selection ? "Select" : "");

    public static bool IsVisible(JsrFieldDefinition field, IReadOnlyDictionary<string, string> values, bool workflow122)
    {
        string Value(string key) => values.GetValueOrDefault(key, "");
        return field.Condition switch
        {
            JsrCondition.Well => Value("source") == "Well",
            JsrCondition.LiveConnection => Value("liveAgConnection") == "Yes",
            JsrCondition.NotLiveConnection => Value("liveAgConnection") != "Yes",
            JsrCondition.DisconnectedConnection => Value("anyPDConnection") == "Yes",
            JsrCondition.BeneficiaryAbsent => Value("benPresent") == "No",
            JsrCondition.Workflow122 => workflow122,
            JsrCondition.NetworkAvailable => Value("mobileNetwork") == "Yes",
            _ => true
        };
    }
}
