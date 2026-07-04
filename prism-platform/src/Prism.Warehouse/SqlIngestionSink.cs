// ============================================================
//  SqlIngestionSink.cs  (Prism.Warehouse)
//  IIngestionSink over Azure SQL. Dispatches each entity to its
//  table with the correct load mode (REPLACE for snapshots, UPSERT
//  for time-series), building a typed DataTable per entity. Drop-in
//  for the connectors' FileIngestionSink - same call site.
//
//  Empty snapshots are SKIPPED (not replaced) so a failed/empty pull
//  can't wipe good data.
// ============================================================
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Prism.Warehouse.Model;

namespace Prism.Warehouse;

public sealed class SqlIngestionSink : IIngestionSink
{
    private readonly WarehouseWriter _writer;
    private readonly ILogger _log;

    public SqlIngestionSink(WarehouseOptions opts, ILogger log)
    {
        _writer = new WarehouseWriter(opts.ConnectionString, log);
        _log = log;
    }

    public async Task WriteAsync<T>(string entityName, IEnumerable<EntityEnvelope<T>> items, CancellationToken ct)
    {
        switch (entityName)
        {
            case "users":               await Replace("dim.[User]", Cast<DimUser>(items), UserMap, entityName, ct); break;
            case "skus":                await Replace("dim.Sku", Cast<DimSku>(items), SkuMap, entityName, ct); break;
            case "license-assignments": await Replace("fact.LicenseAssignment", Cast<FactLicenseAssignment>(items), AssignMap, entityName, ct); break;
            case "service-usage":       await Replace("fact.ServiceUsage", Cast<FactServiceUsage>(items), ServiceUsageMap, entityName, ct); break;
            case "devices":             await Replace("dim.Device", Cast<DimDevice>(items), DeviceMap, entityName, ct); break;
            case "detected-apps":       await Replace("fact.DetectedApp", Cast<FactDetectedApp>(items), DetectedMap, entityName, ct); break;
            case "app-installs":        await Replace("fact.AppInstall", Cast<FactAppInstall>(items), AppInstallMap, entityName, ct); break;
            case "discovered-apps":     await Replace("fact.DiscoveredApp", Cast<FactDiscoveredApp>(items), DiscoveredMap, entityName, ct); break;
            case "software-inventory":  await Replace("fact.SoftwareInventory", Cast<FactSoftwareInventory>(items), SoftwareInvMap, entityName, ct); break;
            case "software-installs":   await Replace("fact.SoftwareInstall", Cast<FactSoftwareInstall>(items), SoftwareInstallMap, entityName, ct); break;
            case "software-runs":       await Replace("fact.SoftwareRun", Cast<FactSoftwareRun>(items), SoftwareRunMap, entityName, ct); break;
            case "app-signins":         await Replace("fact.AppSignIn", Cast<FactAppSignIn>(items), AppSignInMap, entityName, ct); break;
            case "m365-app-usage":      await Replace("fact.M365AppUsage", Cast<FactM365AppUsage>(items), M365AppUsageMap, entityName, ct); break;
            case "copilot-usage":       await Replace("fact.CopilotUsage", Cast<FactCopilotUsage>(items), CopilotUsageMap, entityName, ct); break;
            case "teams-activity":      await Replace("fact.TeamsActivity", Cast<FactTeamsActivity>(items), TeamsActivityMap, entityName, ct); break;
            case "service-detail":      await Replace("fact.ServiceActivityDetail", Cast<FactServiceActivityDetail>(items), ServiceDetailMap, entityName, ct); break;
            case "app-health":          await Replace("fact.AppHealth", Cast<FactAppHealth>(items), AppHealthMap, entityName, ct); break;
            case "mobile-app-installs": await Replace("fact.MobileAppInstall", Cast<FactMobileAppInstall>(items), MobileAppInstallMap, entityName, ct); break;
            case "sp-signins":          await Replace("fact.ServicePrincipalSignIn", Cast<FactServicePrincipalSignIn>(items), SpSignInMap, entityName, ct); break;
            case "deleted-user-licenses": await Replace("fact.DeletedUserLicense", Cast<FactDeletedUserLicense>(items), DeletedUserLicenseMap, entityName, ct); break;
            case "mailbox-settings":    await Replace("fact.Mailbox", Cast<FactMailbox>(items), MailboxMap, entityName, ct); break;
            case "pstn-usage":          await Replace("fact.PstnUsage", Cast<FactPstnUsage>(items), PstnUsageMap, entityName, ct); break;
            case "auth-methods":        await Replace("fact.AuthMethodRegistration", Cast<FactAuthMethod>(items), AuthMethodMap, entityName, ct); break;

            case "azure-cost":          await Upsert("fact.AzureCost", Cast<FactAzureCost>(items), CostMap, CostKeys, CostOn, entityName, ct); break;
            case "app-usage":           await Upsert("fact.AppUsage", Cast<FactAppUsage>(items), AppUsageMap, AppKeys, AppOn, entityName, ct); break;

            default: _log.LogWarning("warehouse: unknown entity '{Entity}', skipped.", entityName); break;
        }
    }

    private static IEnumerable<EntityEnvelope<TD>> Cast<TD>(object items) => (IEnumerable<EntityEnvelope<TD>>)items;

    private async Task Replace<T>(string target, IEnumerable<EntityEnvelope<T>> items, Col<T>[] map, string entity, CancellationToken ct)
    {
        var list = items as IList<EntityEnvelope<T>> ?? items.ToList();
        if (list.Count == 0) { _log.LogInformation("warehouse {Entity}: empty snapshot, skipped (no replace).", entity); return; }
        var (dt, cols) = BuildTable(list, map);
        await _writer.ReplaceAsync(target, cols, dt, list[0].Source, list[0].RunId, entity, ct);
    }

    private async Task Upsert<T>(string target, IEnumerable<EntityEnvelope<T>> items, Col<T>[] map, string[] keys, string on, string entity, CancellationToken ct)
    {
        var list = items as IList<EntityEnvelope<T>> ?? items.ToList();
        if (list.Count == 0) { _log.LogInformation("warehouse {Entity}: nothing new.", entity); return; }
        var (dt, cols) = BuildTable(list, map);
        await _writer.UpsertAsync(target, cols, keys, on, dt, list[0].Source, list[0].RunId, entity, ct);
    }

    // ---- generic table builder -----------------------------------------
    private readonly record struct Col<T>(string Name, Type Type, Func<EntityEnvelope<T>, object> Val);

    private static (DataTable, string[]) BuildTable<T>(IList<EntityEnvelope<T>> rows, Col<T>[] map)
    {
        var dt = new DataTable();
        foreach (Col<T> m in map) dt.Columns.Add(m.Name, m.Type);
        foreach (EntityEnvelope<T> e in rows)
        {
            DataRow r = dt.NewRow();
            for (int i = 0; i < map.Length; i++) r[i] = map[i].Val(e);
            dt.Rows.Add(r);
        }
        return (dt, map.Select(m => m.Name).ToArray());
    }

    private static Col<T>[] Prov<T>() =>
    [
        new("Source",      typeof(string),   e => Str(e.Source)),
        new("RunId",       typeof(string),   e => Str(e.RunId)),
        new("SnapshotUtc", typeof(DateTime), e => Dt(e.SnapshotUtc)),
        new("LoadedUtc",   typeof(DateTime), _ => DateTime.UtcNow),
    ];

    // ---- per-entity column maps ----------------------------------------
    private static readonly Col<DimUser>[] UserMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("AccountEnabled", typeof(bool), e => e.Data.AccountEnabled),
        new("Department", typeof(string), e => Str(e.Data.Department)),
        new("JobTitle", typeof(string), e => Str(e.Data.JobTitle)),
        new("UsageLocation", typeof(string), e => Str(e.Data.UsageLocation)),
        new("CreatedDateTime", typeof(DateTime), e => Dt(e.Data.CreatedDateTime)),
        new("EmployeeHireDate", typeof(DateTime), e => Da(e.Data.EmployeeHireDate)),
        new("EmployeeLeaveDateTime", typeof(DateTime), e => Dt(e.Data.EmployeeLeaveDateTime)),
        new("LastSignInDateTime", typeof(DateTime), e => Dt(e.Data.LastSignInDateTime)),
        new("LastNonInteractiveSignInDateTime", typeof(DateTime), e => Dt(e.Data.LastNonInteractiveSignInDateTime)),
        new("LastSuccessfulSignInDateTime", typeof(DateTime), e => Dt(e.Data.LastSuccessfulSignInDateTime)),
        new("SecurityIdentifier", typeof(string), e => Str(e.Data.SecurityIdentifier)),
        new("OnPremisesSecurityIdentifier", typeof(string), e => Str(e.Data.OnPremisesSecurityIdentifier)),
        new("UserType", typeof(string), e => Str(e.Data.UserType)),
        new("OnPremisesSyncEnabled", typeof(bool), e => NBit(e.Data.OnPremisesSyncEnabled)),
        .. Prov<DimUser>(),
    ];

    private static readonly Col<DimSku>[] SkuMap =
    [
        new("SkuId", typeof(string), e => Str(e.Data.SkuId)),
        new("SkuPartNumber", typeof(string), e => Str(e.Data.SkuPartNumber)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("CapabilityStatus", typeof(string), e => Str(e.Data.CapabilityStatus)),
        new("PrepaidUnitsEnabled", typeof(int), e => e.Data.PrepaidUnitsEnabled),
        new("PrepaidUnitsWarning", typeof(int), e => e.Data.PrepaidUnitsWarning),
        new("PrepaidUnitsSuspended", typeof(int), e => e.Data.PrepaidUnitsSuspended),
        new("ConsumedUnits", typeof(int), e => e.Data.ConsumedUnits),
        .. Prov<DimSku>(),
    ];

    private static readonly Col<FactLicenseAssignment>[] AssignMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("SkuId", typeof(string), e => Str(e.Data.SkuId)),
        new("SkuPartNumber", typeof(string), e => Str(e.Data.SkuPartNumber)),
        new("AssignedDirectly", typeof(bool), e => e.Data.AssignedDirectly),
        new("AssignedByGroupId", typeof(string), e => Str(e.Data.AssignedByGroupId)),
        new("State", typeof(string), e => Str(e.Data.State)),
        new("LastUpdatedDateTime", typeof(DateTime), e => Dt(e.Data.LastUpdatedDateTime)),
        new("DisabledServicePlanIds", typeof(string), e => Json(e.Data.DisabledServicePlanIds)),
        // Materialised count so vw.LicenseSignals no longer OPENJSONs per row on every read.
        new("DisabledPlanCount", typeof(int), e => e.Data.DisabledServicePlanIds?.Length ?? 0),
        .. Prov<FactLicenseAssignment>(),
    ];

    private static readonly Col<FactServiceUsage>[] ServiceUsageMap =
    [
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("Concealed", typeof(bool), e => e.Data.Concealed),
        new("ReportRefreshDate", typeof(DateTime), e => Da(e.Data.ReportRefreshDate)),
        new("ReportPeriodDays", typeof(int), e => IntStr(e.Data.ReportPeriodDays)),
        new("IsDeleted", typeof(bool), e => e.Data.IsDeleted),
        new("HasExchangeLicense", typeof(bool), e => e.Data.HasExchangeLicense),
        new("HasOneDriveLicense", typeof(bool), e => e.Data.HasOneDriveLicense),
        new("HasSharePointLicense", typeof(bool), e => e.Data.HasSharePointLicense),
        new("HasTeamsLicense", typeof(bool), e => e.Data.HasTeamsLicense),
        new("HasYammerLicense", typeof(bool), e => e.Data.HasYammerLicense),
        new("HasSkypeLicense", typeof(bool), e => e.Data.HasSkypeLicense),
        new("ExchangeLastActivityDate", typeof(DateTime), e => Da(e.Data.ExchangeLastActivityDate)),
        new("OneDriveLastActivityDate", typeof(DateTime), e => Da(e.Data.OneDriveLastActivityDate)),
        new("SharePointLastActivityDate", typeof(DateTime), e => Da(e.Data.SharePointLastActivityDate)),
        new("TeamsLastActivityDate", typeof(DateTime), e => Da(e.Data.TeamsLastActivityDate)),
        new("YammerLastActivityDate", typeof(DateTime), e => Da(e.Data.YammerLastActivityDate)),
        new("SkypeLastActivityDate", typeof(DateTime), e => Da(e.Data.SkypeLastActivityDate)),
        new("LastActivityAnyDate", typeof(DateTime), e => Da(e.Data.LastActivityAnyDate)),
        new("AssignedProducts", typeof(string), e => Str(e.Data.AssignedProducts)),
        .. Prov<FactServiceUsage>(),
    ];

    private static readonly Col<DimDevice>[] DeviceMap =
    [
        new("DeviceId", typeof(string), e => Str(e.Data.DeviceId)),
        new("DeviceName", typeof(string), e => Str(e.Data.DeviceName)),
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("OperatingSystem", typeof(string), e => Str(e.Data.OperatingSystem)),
        new("OsVersion", typeof(string), e => Str(e.Data.OsVersion)),
        new("ComplianceState", typeof(string), e => Str(e.Data.ComplianceState)),
        new("OwnerType", typeof(string), e => Str(e.Data.OwnerType)),
        new("ManagementState", typeof(string), e => Str(e.Data.ManagementState)),
        new("EnrolledDateTime", typeof(DateTime), e => Dt(e.Data.EnrolledDateTime)),
        new("LastSyncDateTime", typeof(DateTime), e => Dt(e.Data.LastSyncDateTime)),
        new("Model", typeof(string), e => Str(e.Data.Model)),
        new("Manufacturer", typeof(string), e => Str(e.Data.Manufacturer)),
        new("SerialNumber", typeof(string), e => Str(e.Data.SerialNumber)),
        new("IsEncrypted", typeof(bool), e => NBit(e.Data.IsEncrypted)),
        .. Prov<DimDevice>(),
    ];

    private static readonly Col<FactDetectedApp>[] DetectedMap =
    [
        new("AppId", typeof(string), e => Str(e.Data.AppId)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("Version", typeof(string), e => Str(e.Data.Version)),
        new("Publisher", typeof(string), e => Str(e.Data.Publisher)),
        new("Platform", typeof(string), e => Str(e.Data.Platform)),
        new("DeviceCount", typeof(int), e => e.Data.DeviceCount),
        new("SizeInByte", typeof(long), e => e.Data.SizeInByte),
        .. Prov<FactDetectedApp>(),
    ];

    private static readonly Col<FactAppInstall>[] AppInstallMap =
    [
        new("AppId", typeof(string), e => Str(e.Data.AppId)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("DeviceId", typeof(string), e => Str(e.Data.DeviceId)),
        new("DeviceName", typeof(string), e => Str(e.Data.DeviceName)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        .. Prov<FactAppInstall>(),
    ];

    private static readonly Col<FactDiscoveredApp>[] DiscoveredMap =
    [
        new("AppName", typeof(string), e => Str(e.Data.AppName)),
        new("Category", typeof(string), e => Str(e.Data.Category)),
        new("RiskScore", typeof(double), e => Dbl(e.Data.RiskScore)),
        new("UserCount", typeof(long), e => NLong(e.Data.UserCount)),
        new("UploadedBytes", typeof(long), e => NLong(e.Data.UploadedBytes)),
        new("DownloadedBytes", typeof(long), e => NLong(e.Data.DownloadedBytes)),
        new("TrafficTotalBytes", typeof(long), e => NLong(e.Data.TrafficTotalBytes)),
        new("TransactionCount", typeof(long), e => NLong(e.Data.TransactionCount)),
        new("LastSeen", typeof(string), e => Str(e.Data.LastSeen)),
        new("Tags", typeof(string), e => Str(e.Data.Tags)),
        .. Prov<FactDiscoveredApp>(),
    ];

    private static readonly Col<FactSoftwareInventory>[] SoftwareInvMap =
    [
        new("SoftwareId", typeof(string), e => Str(e.Data.SoftwareId)),
        new("Name", typeof(string), e => Str(e.Data.Name)),
        new("Vendor", typeof(string), e => Str(e.Data.Vendor)),
        new("Weaknesses", typeof(long), e => NLong(e.Data.Weaknesses)),
        new("PublicExploit", typeof(bool), e => NBit(e.Data.PublicExploit)),
        new("ActiveAlert", typeof(bool), e => NBit(e.Data.ActiveAlert)),
        new("ExposedMachines", typeof(long), e => NLong(e.Data.ExposedMachines)),
        new("ImpactScore", typeof(double), e => Dbl(e.Data.ImpactScore)),
        .. Prov<FactSoftwareInventory>(),
    ];

    private static readonly Col<FactSoftwareInstall>[] SoftwareInstallMap =
    [
        new("SoftwareId", typeof(string), e => Str(e.Data.SoftwareId)),
        new("SoftwareName", typeof(string), e => Str(e.Data.SoftwareName)),
        new("Vendor", typeof(string), e => Str(e.Data.Vendor)),
        new("MachineId", typeof(string), e => Str(e.Data.MachineId)),
        new("ComputerDnsName", typeof(string), e => Str(e.Data.ComputerDnsName)),
        new("OsPlatform", typeof(string), e => Str(e.Data.OsPlatform)),
        .. Prov<FactSoftwareInstall>(),
    ];

    private static readonly Col<FactSoftwareRun>[] SoftwareRunMap =
    [
        new("FileName", typeof(string), e => Str(e.Data.FileName)),
        new("DeviceId", typeof(string), e => Str(e.Data.DeviceId)),
        new("DeviceName", typeof(string), e => Str(e.Data.DeviceName)),
        new("AccountUpn", typeof(string), e => Str(e.Data.AccountUpn)),
        new("LastRunUtc", typeof(DateTime), e => Dt(e.Data.LastRunUtc)),
        new("RunCount", typeof(long), e => e.Data.RunCount),
        new("RunDays", typeof(int), e => e.Data.RunDays),
        .. Prov<FactSoftwareRun>(),
    ];

    private static readonly Col<FactAppSignIn>[] AppSignInMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("AppId", typeof(string), e => Str(e.Data.AppId)),
        new("AppDisplayName", typeof(string), e => Str(e.Data.AppDisplayName)),
        new("LastSignInUtc", typeof(DateTime), e => Dt(e.Data.LastSignInUtc)),
        new("SignInCount", typeof(long), e => e.Data.SignInCount),
        new("WindowDays", typeof(int), e => e.Data.WindowDays),
        .. Prov<FactAppSignIn>(),
    ];

    private static readonly Col<FactM365AppUsage>[] M365AppUsageMap =
    [
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("Concealed", typeof(bool), e => e.Data.Concealed),
        new("ReportRefreshDate", typeof(DateTime), e => Dt(e.Data.ReportRefreshDate)),
        new("ReportPeriodDays", typeof(string), e => Str(e.Data.ReportPeriodDays)),
        new("IsDeleted", typeof(bool), e => e.Data.IsDeleted),
        new("WordLastActivityDate", typeof(DateTime), e => Dt(e.Data.WordLastActivityDate)),
        new("ExcelLastActivityDate", typeof(DateTime), e => Dt(e.Data.ExcelLastActivityDate)),
        new("PowerPointLastActivityDate", typeof(DateTime), e => Dt(e.Data.PowerPointLastActivityDate)),
        new("OutlookLastActivityDate", typeof(DateTime), e => Dt(e.Data.OutlookLastActivityDate)),
        new("OneNoteLastActivityDate", typeof(DateTime), e => Dt(e.Data.OneNoteLastActivityDate)),
        new("TeamsLastActivityDate", typeof(DateTime), e => Dt(e.Data.TeamsLastActivityDate)),
        new("LastActivityAnyDate", typeof(DateTime), e => Dt(e.Data.LastActivityAnyDate)),
        new("UsedWeb", typeof(bool), e => e.Data.UsedWeb),
        new("UsedMobile", typeof(bool), e => e.Data.UsedMobile),
        new("UsedWindows", typeof(bool), e => e.Data.UsedWindows),
        new("UsedMac", typeof(bool), e => e.Data.UsedMac),
        .. Prov<FactM365AppUsage>(),
    ];

    private static readonly Col<FactCopilotUsage>[] CopilotUsageMap =
    [
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("Concealed", typeof(bool), e => e.Data.Concealed),
        new("ReportRefreshDate", typeof(DateTime), e => Da(e.Data.ReportRefreshDate)),
        new("ReportPeriodDays", typeof(string), e => Str(e.Data.ReportPeriodDays)),
        new("LastActivityDate", typeof(DateTime), e => Da(e.Data.LastActivityDate)),
        new("TeamsLastActivityDate", typeof(DateTime), e => Da(e.Data.TeamsLastActivityDate)),
        new("WordLastActivityDate", typeof(DateTime), e => Da(e.Data.WordLastActivityDate)),
        new("ExcelLastActivityDate", typeof(DateTime), e => Da(e.Data.ExcelLastActivityDate)),
        new("PowerPointLastActivityDate", typeof(DateTime), e => Da(e.Data.PowerPointLastActivityDate)),
        new("OutlookLastActivityDate", typeof(DateTime), e => Da(e.Data.OutlookLastActivityDate)),
        new("OneNoteLastActivityDate", typeof(DateTime), e => Da(e.Data.OneNoteLastActivityDate)),
        new("LoopLastActivityDate", typeof(DateTime), e => Da(e.Data.LoopLastActivityDate)),
        new("ChatLastActivityDate", typeof(DateTime), e => Da(e.Data.ChatLastActivityDate)),
        new("LastActivityAnyDate", typeof(DateTime), e => Da(e.Data.LastActivityAnyDate)),
        .. Prov<FactCopilotUsage>(),
    ];

    private static readonly Col<FactTeamsActivity>[] TeamsActivityMap =
    [
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("Concealed", typeof(bool), e => e.Data.Concealed),
        new("ReportRefreshDate", typeof(DateTime), e => Da(e.Data.ReportRefreshDate)),
        new("ReportPeriodDays", typeof(string), e => Str(e.Data.ReportPeriodDays)),
        new("LastActivityDate", typeof(DateTime), e => Da(e.Data.LastActivityDate)),
        new("TeamChatMessageCount", typeof(int), e => IntStr(e.Data.TeamChatMessageCount)),
        new("PrivateChatMessageCount", typeof(int), e => IntStr(e.Data.PrivateChatMessageCount)),
        new("CallCount", typeof(int), e => IntStr(e.Data.CallCount)),
        new("MeetingCount", typeof(int), e => IntStr(e.Data.MeetingCount)),
        .. Prov<FactTeamsActivity>(),
    ];

    private static readonly Col<FactServiceActivityDetail>[] ServiceDetailMap =
    [
        new("Service", typeof(string), e => Str(e.Data.Service)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("Concealed", typeof(bool), e => e.Data.Concealed),
        new("ReportRefreshDate", typeof(DateTime), e => Da(e.Data.ReportRefreshDate)),
        new("ReportPeriodDays", typeof(string), e => Str(e.Data.ReportPeriodDays)),
        new("LastActivityDate", typeof(DateTime), e => Da(e.Data.LastActivityDate)),
        new("ViewedOrEditedFileCount", typeof(int), e => IntStr(e.Data.ViewedOrEditedFileCount)),
        new("SyncedFileCount", typeof(int), e => IntStr(e.Data.SyncedFileCount)),
        new("SharedInternallyFileCount", typeof(int), e => IntStr(e.Data.SharedInternallyFileCount)),
        new("SharedExternallyFileCount", typeof(int), e => IntStr(e.Data.SharedExternallyFileCount)),
        new("VisitedPageCount", typeof(int), e => IntStr(e.Data.VisitedPageCount)),
        new("StorageUsedBytes", typeof(long), e => LongStr(e.Data.StorageUsedBytes)),
        new("ItemCount", typeof(int), e => IntStr(e.Data.ItemCount)),
        .. Prov<FactServiceActivityDetail>(),
    ];

    private static readonly Col<FactAppHealth>[] AppHealthMap =
    [
        new("AppName", typeof(string), e => Str(e.Data.AppName)),
        new("AppDisplayName", typeof(string), e => Str(e.Data.AppDisplayName)),
        new("AppPublisher", typeof(string), e => Str(e.Data.AppPublisher)),
        new("AppUsageDuration", typeof(long), e => NLong(e.Data.AppUsageDuration)),
        new("ActiveDeviceCount", typeof(int), e => NInt(e.Data.ActiveDeviceCount)),
        new("AppCrashCount", typeof(int), e => NInt(e.Data.AppCrashCount)),
        new("AppHangCount", typeof(int), e => NInt(e.Data.AppHangCount)),
        new("AppHealthScore", typeof(double), e => Dbl(e.Data.AppHealthScore)),
        new("MeanTimeToFailureInMinutes", typeof(double), e => Dbl(e.Data.MeanTimeToFailureInMinutes)),
        .. Prov<FactAppHealth>(),
    ];

    private static readonly Col<FactMobileAppInstall>[] MobileAppInstallMap =
    [
        new("AppId", typeof(string), e => Str(e.Data.AppId)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("Publisher", typeof(string), e => Str(e.Data.Publisher)),
        new("Platform", typeof(string), e => Str(e.Data.Platform)),
        new("InstalledDeviceCount", typeof(int), e => NInt(e.Data.InstalledDeviceCount)),
        new("FailedDeviceCount", typeof(int), e => NInt(e.Data.FailedDeviceCount)),
        new("NotInstalledDeviceCount", typeof(int), e => NInt(e.Data.NotInstalledDeviceCount)),
        new("PendingInstallDeviceCount", typeof(int), e => NInt(e.Data.PendingInstallDeviceCount)),
        .. Prov<FactMobileAppInstall>(),
    ];

    private static readonly Col<FactServicePrincipalSignIn>[] SpSignInMap =
    [
        new("AppId", typeof(string), e => Str(e.Data.AppId)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("LastSignInUtc", typeof(DateTime), e => Dt(e.Data.LastSignInUtc)),
        .. Prov<FactServicePrincipalSignIn>(),
    ];

    private static readonly Col<FactDeletedUserLicense>[] DeletedUserLicenseMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("DeletedDateTime", typeof(DateTime), e => Dt(e.Data.DeletedDateTime)),
        new("SkuId", typeof(string), e => Str(e.Data.SkuId)),
        .. Prov<FactDeletedUserLicense>(),
    ];

    private static readonly Col<FactMailbox>[] MailboxMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("UserPurpose", typeof(string), e => Str(e.Data.UserPurpose)),
        new("AutomaticRepliesStatus", typeof(string), e => Str(e.Data.AutomaticRepliesStatus)),
        new("TimeZone", typeof(string), e => Str(e.Data.TimeZone)),
        .. Prov<FactMailbox>(),
    ];

    private static readonly Col<FactPstnUsage>[] PstnUsageMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("CallCount", typeof(int), e => e.Data.CallCount),
        new("TotalDurationSeconds", typeof(long), e => e.Data.TotalDurationSeconds),
        new("LastCallDateTime", typeof(DateTime), e => Dt(e.Data.LastCallDateTime)),
        new("WindowDays", typeof(int), e => e.Data.WindowDays),
        .. Prov<FactPstnUsage>(),
    ];

    private static readonly Col<FactAuthMethod>[] AuthMethodMap =
    [
        new("UserId", typeof(string), e => Str(e.Data.UserId)),
        new("UserPrincipalName", typeof(string), e => Str(e.Data.UserPrincipalName)),
        new("IsAdmin", typeof(bool), e => NBit(e.Data.IsAdmin)),
        new("IsMfaRegistered", typeof(bool), e => NBit(e.Data.IsMfaRegistered)),
        new("IsMfaCapable", typeof(bool), e => NBit(e.Data.IsMfaCapable)),
        new("IsPasswordlessCapable", typeof(bool), e => NBit(e.Data.IsPasswordlessCapable)),
        new("IsSsprRegistered", typeof(bool), e => NBit(e.Data.IsSsprRegistered)),
        new("IsSsprEnabled", typeof(bool), e => NBit(e.Data.IsSsprEnabled)),
        new("IsSsprCapable", typeof(bool), e => NBit(e.Data.IsSsprCapable)),
        new("MethodsRegistered", typeof(string), e => Str(e.Data.MethodsRegistered)),
        new("DefaultMethod", typeof(string), e => Str(e.Data.DefaultMethod)),
        new("LastUpdatedDateTime", typeof(DateTime), e => Dt(e.Data.LastUpdatedDateTime)),
        .. Prov<FactAuthMethod>(),
    ];

    private static readonly Col<FactAzureCost>[] CostMap =
    [
        new("Scope", typeof(string), e => Str(e.Data.Scope)),
        new("UsageDate", typeof(DateTime), e => Da(e.Data.UsageDate)),
        new("Cost", typeof(decimal), e => e.Data.Cost),
        new("Currency", typeof(string), e => Str(e.Data.Currency)),
        new("ServiceName", typeof(string), e => Str(e.Data.ServiceName)),
        new("ResourceGroup", typeof(string), e => Str(e.Data.ResourceGroup)),
        .. Prov<FactAzureCost>(),
    ];
    private static readonly string[] CostKeys = ["Scope", "UsageDate", "ServiceName", "ResourceGroup"];
    private const string CostOn =
        "T.Scope = S.Scope AND T.UsageDate = S.UsageDate AND " +
        "ISNULL(T.ServiceName,'') = ISNULL(S.ServiceName,'') AND ISNULL(T.ResourceGroup,'') = ISNULL(S.ResourceGroup,'')";

    private static readonly Col<FactAppUsage>[] AppUsageMap =
    [
        new("Date", typeof(DateTime), e => Da(e.Data.Date)),
        new("DeviceThumbprint", typeof(string), e => Str(e.Data.DeviceThumbprint)),
        new("MachineName", typeof(string), e => Str(e.Data.MachineName)),
        new("UserSid", typeof(string), e => Str(e.Data.UserSid)),
        new("ExePath", typeof(string), e => Str(e.Data.ExePath)),
        new("DisplayName", typeof(string), e => Str(e.Data.DisplayName)),
        new("ProductName", typeof(string), e => Str(e.Data.ProductName)),
        new("Description", typeof(string), e => Str(e.Data.Description)),
        new("Company", typeof(string), e => Str(e.Data.Company)),
        new("FileVersion", typeof(string), e => Str(e.Data.FileVersion)),
        new("Launches", typeof(int), e => e.Data.Launches),
        new("FirstSeenUtc", typeof(DateTime), e => Dt(e.Data.FirstSeenUtc)),
        new("LastSeenUtc", typeof(DateTime), e => Dt(e.Data.LastSeenUtc)),
        new("ForegroundActiveSeconds", typeof(long), e => e.Data.ForegroundActiveSeconds),
        new("ForegroundIdleSeconds", typeof(long), e => e.Data.ForegroundIdleSeconds),
        new("VisibleBackgroundSeconds", typeof(long), e => e.Data.VisibleBackgroundSeconds),
        new("MinimizedSeconds", typeof(long), e => e.Data.MinimizedSeconds),
        new("TraySeconds", typeof(long), e => e.Data.TraySeconds),
        new("UtcOffsetMinutes", typeof(int), e => e.Data.UtcOffsetMinutes),
        new("AgentVersion", typeof(string), e => Str(e.Data.AgentVersion)),
        new("ReceiveId", typeof(string), e => Str(e.Data.ReceiveId)),
        .. Prov<FactAppUsage>(),
    ];
    private static readonly string[] AppKeys = ["Date", "DeviceThumbprint", "UserSid", "ExePath"];
    private const string AppOn =
        "T.[Date] = S.[Date] AND T.DeviceThumbprint = S.DeviceThumbprint AND " +
        "ISNULL(T.UserSid,'') = ISNULL(S.UserSid,'') AND T.ExePath = S.ExePath";

    // ---- value conversion (-> value or DBNull) -------------------------
    private static object Str(string? v) => string.IsNullOrEmpty(v) ? DBNull.Value : v;
    private static object NBit(bool? v) => v ?? (object)DBNull.Value;
    private static object NLong(long? v) => v ?? (object)DBNull.Value;
    private static object Dbl(double? v) => v ?? (object)DBNull.Value;
    private static object IntStr(string? v) => int.TryParse(v, out int n) ? n : DBNull.Value;
    private static object LongStr(string? v) => long.TryParse(v, out long n) ? n : DBNull.Value;
    private static object NInt(int? v) => v ?? (object)DBNull.Value;
    private static object Json(string[]? a) => a is { Length: > 0 } ? JsonSerializer.Serialize(a) : DBNull.Value;

    private static object Dt(string? v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime d)
            ? d : DBNull.Value;

    private static object Da(string? v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime d)
            ? d.Date : DBNull.Value;
}
