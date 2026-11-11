namespace Mskpy.Domain;

public sealed record DisplayField(string Label, string Value);
public sealed record AccountProfile(string LoginId, string Role, string Scheme, IReadOnlyList<DisplayField> Fields);
public sealed record ModuleDefinition(string Key, string Name, string Description);

public static class VerifiedNavigation
{
    // Screen availability was observed for the reference account, not for every role.
    // R2: Live Inspections only — hide offline inspection, JSR, pump installation,
    // groundwater, vendor survey, TOD. Settings and Diagnostics stay.
    public static readonly IReadOnlyList<ModuleDefinition> Modules =
    [
        new("inspection", "MTSKPY", "Live Inspections by Linestaff")
    ];
}
