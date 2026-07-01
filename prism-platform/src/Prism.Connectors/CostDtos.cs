// ============================================================
//  CostDtos.cs / GatewayDtos.cs (combined)
//  DTOs for (a) the Cost Management Query request/response and
//  (b) reading the gateway's landed NDJSON. The gateway DTOs mirror
//  the gateway's LandedBatch/ReceivedBatch/UsageRollup wire shape
//  (camelCase) - keep in sync with prism-gateway's contracts.
// ============================================================
using System.Text.Json.Serialization;

namespace Prism.Connectors.Cost;

// ---- Query request -------------------------------------------------------
public sealed class CostQuery
{
    public string Type { get; set; } = "ActualCost";       // ActualCost | AmortizedCost | Usage
    public string Timeframe { get; set; } = "MonthToDate";  // MonthToDate | TheLastMonth | ...
    public CostDataset Dataset { get; set; } = new();
}

public sealed class CostDataset
{
    public string Granularity { get; set; } = "Daily";
    public Dictionary<string, CostAggregation> Aggregation { get; set; } = new();
    public List<CostGrouping> Grouping { get; set; } = new();
}

public sealed class CostAggregation
{
    public string Name { get; set; } = "PreTaxCost";        // EA/MG: PreTaxCost; MCA: Cost
    public string Function { get; set; } = "Sum";
}

public sealed class CostGrouping
{
    public string Type { get; set; } = "Dimension";
    public string Name { get; set; } = "ServiceName";
}

// ---- Query response ------------------------------------------------------
public sealed class CostQueryResult
{
    public CostQueryProperties? Properties { get; set; }
}

public sealed class CostQueryProperties
{
    public string? NextLink { get; set; }
    public List<CostColumn> Columns { get; set; } = new();
    // Rows are positional arrays; element types vary (number/string), so read as JsonElement.
    public List<List<System.Text.Json.JsonElement>> Rows { get; set; } = new();
}

public sealed class CostColumn
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
}

// ---- Subscription enumeration (ARM) --------------------------------------
//  GET {arm}/subscriptions?api-version=2020-01-01
//  Used when CostManagementScope is the sentinel "subscriptions:*": the
//  connector queries every ENABLED subscription the managed identity can
//  see (i.e. has an RBAC role on) and unions the results.
public sealed class SubscriptionList
{
    public List<SubscriptionItem> Value { get; set; } = new();
    public string? NextLink { get; set; }
}

public sealed class SubscriptionItem
{
    public string SubscriptionId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? State { get; set; }   // "Enabled" | "Disabled" | "Warned" | ...
}
