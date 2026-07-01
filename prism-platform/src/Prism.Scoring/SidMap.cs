// ============================================================
//  SidMap.cs  (Prism.Scoring)
//  NOTE: As of Wave 3.2 the engine resolves desktop app usage to
//  the licensed user via Intune's device PRIMARY USER (see
//  vw.AppUsageByUser90), which works for Entra-joined AND hybrid
//  devices alike — so this SID derivation is no longer on the
//  scoring path. It is retained as a reference/utility for any
//  future Entra-joined-only, SID-keyed correlation.
//
//  Converts an Entra (Azure AD) object id (GUID) to the Windows
//  account SID the agent reports on Entra-joined devices. The SID
//  S-1-12-1-a-b-c-d encodes the GUID as four little-endian uint32s.
//  Hybrid/AD-joined SIDs derive from on-prem AD instead, so this
//  mapping does not hold there — which is exactly why the engine no
//  longer depends on it.
// ============================================================
namespace Prism.Scoring;

public static class SidMap
{
    public static string? FromEntraObjectId(string? objectId)
    {
        if (!Guid.TryParse(objectId, out Guid g)) return null;
        byte[] b = g.ToByteArray();   // little-endian for the first three GUID groups
        uint a1 = BitConverter.ToUInt32(b, 0);
        uint a2 = BitConverter.ToUInt32(b, 4);
        uint a3 = BitConverter.ToUInt32(b, 8);
        uint a4 = BitConverter.ToUInt32(b, 12);
        return $"S-1-12-1-{a1}-{a2}-{a3}-{a4}";
    }
}
