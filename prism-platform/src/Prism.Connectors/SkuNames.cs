// ============================================================
//  SkuNames.cs
//  Best-effort mapping from cryptic skuPartNumber to a friendly
//  product name for the dashboard. This is a small built-in set
//  of the common ones; unknown SKUs pass through their part
//  number. For exhaustive coverage, load Microsoft's published
//  "Product names and service plan identifiers" CSV at the IaC
//  step and merge it over this table.
// ============================================================
namespace Prism.Connectors;

internal static class SkuNames
{
    private static readonly Dictionary<string, string> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTERPRISEPACK"]            = "Office 365 E3",
        ["ENTERPRISEPREMIUM"]        = "Office 365 E5",
        ["SPE_E3"]                   = "Microsoft 365 E3",
        ["SPE_E5"]                   = "Microsoft 365 E5",
        ["SPE_F1"]                   = "Microsoft 365 F3",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS_PREMIUM"]    = "Microsoft 365 Business Standard",
        ["SPB"]                      = "Microsoft 365 Business Premium",
        ["DESKLESSPACK"]             = "Office 365 F3",
        ["EMS"]                      = "Enterprise Mobility + Security E3",
        ["EMSPREMIUM"]               = "Enterprise Mobility + Security E5",
        ["AAD_PREMIUM"]              = "Microsoft Entra ID P1",
        ["AAD_PREMIUM_P2"]           = "Microsoft Entra ID P2",
        ["POWER_BI_PRO"]             = "Power BI Pro",
        ["POWER_BI_STANDARD"]        = "Power BI (free)",
        ["PROJECTPROFESSIONAL"]      = "Project Plan 3",
        ["VISIOCLIENT"]              = "Visio Plan 2",
        ["FLOW_FREE"]                = "Power Automate (free)",
        ["TEAMS_EXPLORATORY"]        = "Teams Exploratory",
        ["WIN10_PRO_ENT_SUB"]        = "Windows 10/11 Enterprise E3",
        ["MCOMEETADV"]               = "Microsoft 365 Audio Conferencing",
        ["MCOEV"]                    = "Microsoft Teams Phone Standard",
        ["DEFENDER_ENDPOINT_P1"]     = "Microsoft Defender for Endpoint P1",
    };

    public static string? Friendly(string? skuPartNumber)
    {
        if (string.IsNullOrEmpty(skuPartNumber)) return null;
        return s_map.TryGetValue(skuPartNumber, out string? name) ? name : skuPartNumber;
    }
}
