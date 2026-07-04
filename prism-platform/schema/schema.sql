/* ============================================================================
   PRISM - consolidated warehouse schema  ·  Release 1.0.0-rc.1  (Azure SQL)

   THE single source of truth for the Prism data warehouse: every schema,
   table, index, analytic view and the curated product reference data the
   views build on - one dependency-ordered file. It is idempotent AND
   self-upgrading: tables are creation-guarded, later-release columns are
   guarded ALTER ADDs, superseded indexes drop-and-recreate under their
   current names, and views use CREATE OR ALTER. Running this one file
   therefore stands up a NEW database and upgrades an EXISTING one alike.

       sqlcmd -S <server>.database.windows.net -d prism -G -i schema/schema.sql

   Upgrading a pre-release (preview) database? Run schema/migrate-v2.sql once
   AFTER this file - it backfills computed data and seeds the verdict-history
   baseline (details in that file). Fresh deployments never need it.

   Tenant-specific commercial data (negotiated unit prices and EA contract
   quantities) lives in schema/seed-commercial.sql, which you run afterwards.

   LAYOUT
     dim.*    dimensions          current state, REPLACE-loaded each run
     fact.*   facts               current-state facts REPLACE-loaded;
                                   time-series facts UPSERT-loaded by natural key
     ref.*    reference data      curated maps & catalogs the views build on
     score.*  engine output       per-seat verdicts (REPLACE per scoring run)
     meta.*   load-run log         one row per (run, entity)
     vw.*     analytic views       the seam the scoring engine + dashboard read

   Every ingested row carries provenance: Source, RunId, SnapshotUtc, LoadedUtc.
   Tables are guarded (IF OBJECT_ID ... IS NULL); views use CREATE OR ALTER;
   indexes are guarded; reference seeds use idempotent MERGE.
   ============================================================================ */

IF SCHEMA_ID('dim')   IS NULL EXEC('CREATE SCHEMA dim');
IF SCHEMA_ID('fact')  IS NULL EXEC('CREATE SCHEMA fact');
IF SCHEMA_ID('ref')   IS NULL EXEC('CREATE SCHEMA ref');
IF SCHEMA_ID('score') IS NULL EXEC('CREATE SCHEMA score');
IF SCHEMA_ID('meta')  IS NULL EXEC('CREATE SCHEMA meta');
IF SCHEMA_ID('vw')    IS NULL EXEC('CREATE SCHEMA vw');
GO

/* ==========================================================================
   CORE - schemas, dimensions, current-state & time-series facts, load log
   ========================================================================== */

/* ---------------------------------------------------------------------------
   DIMENSIONS  (REPLACE-loaded: each run replaces all rows for that Source)
   --------------------------------------------------------------------------- */

IF OBJECT_ID('dim.[User]') IS NULL
CREATE TABLE dim.[User]
(
    UserId                            nvarchar(64)   NOT NULL CONSTRAINT PK_dim_User PRIMARY KEY,
    UserPrincipalName                 nvarchar(256)  NULL,
    DisplayName                       nvarchar(256)  NULL,
    AccountEnabled                    bit            NULL,
    Department                        nvarchar(128)  NULL,
    JobTitle                          nvarchar(128)  NULL,
    UsageLocation                     nvarchar(8)    NULL,
    CreatedDateTime                   datetime2(3)   NULL,
    EmployeeHireDate                  date           NULL,
    EmployeeLeaveDateTime             datetime2(3)   NULL,
    LastSignInDateTime                datetime2(3)   NULL,
    LastNonInteractiveSignInDateTime  datetime2(3)   NULL,
    LastSuccessfulSignInDateTime      datetime2(3)   NULL,
    SecurityIdentifier                nvarchar(128)  NULL,
    OnPremisesSecurityIdentifier      nvarchar(128)  NULL,
    UserType                          nvarchar(32)   NULL,   -- Member | Guest (paid SKU on a Guest = governance flag)
    OnPremisesSyncEnabled             bit            NULL,   -- hybrid-synced account (explains AD-derived SIDs)
    Source                            nvarchar(64)   NULL,
    RunId                             nvarchar(40)   NULL,
    SnapshotUtc                       datetime2(3)   NULL,
    LoadedUtc                         datetime2(3)   NOT NULL CONSTRAINT DF_dim_User_Loaded DEFAULT sysutcdatetime()
);
GO
-- v2 columns (self-migrating adds for databases created before them)
IF COL_LENGTH('dim.[User]','UserType') IS NULL              ALTER TABLE dim.[User] ADD UserType nvarchar(32) NULL;
IF COL_LENGTH('dim.[User]','OnPremisesSyncEnabled') IS NULL ALTER TABLE dim.[User] ADD OnPremisesSyncEnabled bit NULL;
GO
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_Upn','IndexID') IS NULL
    CREATE INDEX IX_User_Upn     ON dim.[User](UserPrincipalName);
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_Enabled','IndexID') IS NULL
    CREATE INDEX IX_User_Enabled ON dim.[User](AccountEnabled) INCLUDE (LastSignInDateTime);
-- v2: SID lookups feed vw.AppUsageByUser90's agent-usage attribution; DisplayName /
-- Department serve the command-palette LIKE search without a full table scan.
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_Sid','IndexID') IS NULL
    CREATE INDEX IX_User_Sid        ON dim.[User](SecurityIdentifier)          WHERE SecurityIdentifier IS NOT NULL;
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_OnPremSid','IndexID') IS NULL
    CREATE INDEX IX_User_OnPremSid  ON dim.[User](OnPremisesSecurityIdentifier) WHERE OnPremisesSecurityIdentifier IS NOT NULL;
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_DisplayName','IndexID') IS NULL
    CREATE INDEX IX_User_DisplayName ON dim.[User](DisplayName);
IF IndexProperty(OBJECT_ID('dim.[User]'),'IX_User_Department','IndexID') IS NULL
    CREATE INDEX IX_User_Department  ON dim.[User](Department) INCLUDE (DisplayName, JobTitle);
GO

IF OBJECT_ID('dim.Sku') IS NULL
CREATE TABLE dim.Sku
(
    SkuId                  nvarchar(64)  NOT NULL CONSTRAINT PK_dim_Sku PRIMARY KEY,
    SkuPartNumber          nvarchar(128) NULL,
    DisplayName            nvarchar(256) NULL,
    CapabilityStatus       nvarchar(32)  NULL,
    PrepaidUnitsEnabled    int           NULL,
    PrepaidUnitsWarning    int           NULL,
    PrepaidUnitsSuspended  int           NULL,
    ConsumedUnits          int           NULL,
    Source                 nvarchar(64)  NULL,
    RunId                  nvarchar(40)  NULL,
    SnapshotUtc            datetime2(3)  NULL,
    LoadedUtc              datetime2(3)  NOT NULL CONSTRAINT DF_dim_Sku_Loaded DEFAULT sysutcdatetime()
);
GO

IF OBJECT_ID('dim.Device') IS NULL
CREATE TABLE dim.Device
(
    DeviceId           nvarchar(64)   NOT NULL CONSTRAINT PK_dim_Device PRIMARY KEY,
    DeviceName         nvarchar(256)  NULL,
    UserId             nvarchar(64)   NULL,
    UserPrincipalName  nvarchar(256)  NULL,
    OperatingSystem    nvarchar(64)   NULL,
    OsVersion          nvarchar(64)   NULL,
    ComplianceState    nvarchar(32)   NULL,
    OwnerType          nvarchar(32)   NULL,
    ManagementState    nvarchar(32)   NULL,
    EnrolledDateTime   datetime2(3)   NULL,
    LastSyncDateTime   datetime2(3)   NULL,
    Model              nvarchar(128)  NULL,
    Manufacturer       nvarchar(128)  NULL,
    SerialNumber       nvarchar(128)  NULL,
    IsEncrypted        bit            NULL,
    Source             nvarchar(64)   NULL,
    RunId              nvarchar(40)   NULL,
    SnapshotUtc        datetime2(3)   NULL,
    LoadedUtc          datetime2(3)   NOT NULL CONSTRAINT DF_dim_Device_Loaded DEFAULT sysutcdatetime()
);
GO
IF IndexProperty(OBJECT_ID('dim.Device'),'IX_Device_User','IndexID') IS NULL
    CREATE INDEX IX_Device_User ON dim.Device(UserId);
IF IndexProperty(OBJECT_ID('dim.Device'),'IX_Device_Upn','IndexID') IS NULL
    CREATE INDEX IX_Device_Upn  ON dim.Device(UserPrincipalName);
-- v2: DeviceName is the join key for the agent correlation (vw.AppUsageCorrelated),
-- the MDE DNS-host match (vw.SoftwareInstallByUser) and the device drill - all of
-- which previously scanned the table.
IF IndexProperty(OBJECT_ID('dim.Device'),'IX_Device_Name','IndexID') IS NULL
    CREATE INDEX IX_Device_Name ON dim.Device(DeviceName) INCLUDE (UserId, UserPrincipalName, OperatingSystem, ComplianceState);
GO

/* ---------------------------------------------------------------------------
   CURRENT-STATE FACTS  (REPLACE-loaded)
   --------------------------------------------------------------------------- */

IF OBJECT_ID('fact.LicenseAssignment') IS NULL
CREATE TABLE fact.LicenseAssignment
(
    UserId                   nvarchar(64)  NOT NULL,
    SkuId                    nvarchar(64)  NOT NULL,
    SkuPartNumber            nvarchar(128) NULL,
    AssignedDirectly         bit           NULL,
    AssignedByGroupId        nvarchar(64)  NULL,
    State                    nvarchar(32)  NULL,
    LastUpdatedDateTime      datetime2(3)  NULL,
    DisabledServicePlanIds   nvarchar(max) NULL,
    DisabledPlanCount        int           NULL,   -- materialised by the sink; saves per-row OPENJSON in vw.LicenseSignals
    Source                   nvarchar(64)  NULL,
    RunId                    nvarchar(40)  NULL,
    SnapshotUtc              datetime2(3)  NULL,
    LoadedUtc                datetime2(3)  NOT NULL CONSTRAINT DF_fact_LA_Loaded DEFAULT sysutcdatetime(),
    CONSTRAINT PK_fact_LicenseAssignment PRIMARY KEY (UserId, SkuId)
);
GO
IF COL_LENGTH('fact.LicenseAssignment','DisabledPlanCount') IS NULL
    ALTER TABLE fact.LicenseAssignment ADD DisabledPlanCount int NULL;
GO
-- v2: widened to a covering index - the SKU drill and per-SKU rollups project these
-- columns, and the old key-only index forced a lookup per row.
IF IndexProperty(OBJECT_ID('fact.LicenseAssignment'),'IX_LA_Sku','IndexID') IS NOT NULL
    DROP INDEX IX_LA_Sku ON fact.LicenseAssignment;
IF IndexProperty(OBJECT_ID('fact.LicenseAssignment'),'IX_LA_Sku_v2','IndexID') IS NULL
    CREATE INDEX IX_LA_Sku_v2 ON fact.LicenseAssignment(SkuId)
        INCLUDE (UserId, SkuPartNumber, AssignedDirectly, State, LastUpdatedDateTime);
GO

IF OBJECT_ID('fact.ServiceUsage') IS NULL
CREATE TABLE fact.ServiceUsage
(
    ServiceUsageId             bigint IDENTITY(1,1) CONSTRAINT PK_fact_ServiceUsage PRIMARY KEY,
    UserPrincipalName          nvarchar(256) NULL,
    DisplayName                nvarchar(256) NULL,
    Concealed                  bit           NULL,
    ReportRefreshDate          date          NULL,
    ReportPeriodDays           int           NULL,
    IsDeleted                  bit           NULL,
    HasExchangeLicense         bit           NULL,
    HasOneDriveLicense         bit           NULL,
    HasSharePointLicense       bit           NULL,
    HasTeamsLicense            bit           NULL,
    HasYammerLicense           bit           NULL,
    HasSkypeLicense            bit           NULL,
    ExchangeLastActivityDate   date          NULL,
    OneDriveLastActivityDate   date          NULL,
    SharePointLastActivityDate date          NULL,
    TeamsLastActivityDate      date          NULL,
    YammerLastActivityDate     date          NULL,
    SkypeLastActivityDate      date          NULL,
    LastActivityAnyDate        date          NULL,
    AssignedProducts           nvarchar(1024) NULL,
    Source                     nvarchar(64)  NULL,
    RunId                      nvarchar(40)  NULL,
    SnapshotUtc                datetime2(3)  NULL,
    LoadedUtc                  datetime2(3)  NOT NULL CONSTRAINT DF_fact_SU_Loaded DEFAULT sysutcdatetime()
);
GO
-- v2: vw.LicenseSignals joins this table by UPN and projects the workload dates for
-- EVERY licensed seat on EVERY scoring run - the old key-only index caused a RID
-- lookup per assignment. The covering replacement serves the whole view from one seek.
IF IndexProperty(OBJECT_ID('fact.ServiceUsage'),'IX_SU_Upn','IndexID') IS NOT NULL
    DROP INDEX IX_SU_Upn ON fact.ServiceUsage;
IF IndexProperty(OBJECT_ID('fact.ServiceUsage'),'IX_SU_Upn_v2','IndexID') IS NULL
    CREATE INDEX IX_SU_Upn_v2 ON fact.ServiceUsage(UserPrincipalName)
        INCLUDE (LastActivityAnyDate, TeamsLastActivityDate, ExchangeLastActivityDate,
                 OneDriveLastActivityDate, SharePointLastActivityDate, ReportRefreshDate, Concealed);
GO

IF OBJECT_ID('fact.DetectedApp') IS NULL
CREATE TABLE fact.DetectedApp
(
    DetectedAppId  bigint IDENTITY(1,1) CONSTRAINT PK_fact_DetectedApp PRIMARY KEY,
    AppId          nvarchar(128) NULL,
    DisplayName    nvarchar(256) NULL,
    [Version]      nvarchar(64)  NULL,
    Publisher      nvarchar(256) NULL,
    Platform       nvarchar(64)  NULL,
    DeviceCount    int           NULL,
    SizeInByte     bigint        NULL,
    Source         nvarchar(64)  NULL,
    RunId          nvarchar(40)  NULL,
    SnapshotUtc    datetime2(3)  NULL,
    LoadedUtc      datetime2(3)  NOT NULL CONSTRAINT DF_fact_DA_Loaded DEFAULT sysutcdatetime()
);
GO
-- v2: the watched-apps estate (LIKE match), the app drill and the palette search all
-- probe DisplayName; AppId serves the version join from vw.AppInstall.
IF IndexProperty(OBJECT_ID('fact.DetectedApp'),'IX_DetectedApp_Name','IndexID') IS NULL
    CREATE INDEX IX_DetectedApp_Name  ON fact.DetectedApp(DisplayName) INCLUDE (Publisher, [Version], Platform, DeviceCount);
IF IndexProperty(OBJECT_ID('fact.DetectedApp'),'IX_DetectedApp_AppId','IndexID') IS NULL
    CREATE INDEX IX_DetectedApp_AppId ON fact.DetectedApp(AppId) INCLUDE ([Version]);
GO

-- Item 4: per-device application installs (which devices/users have an app),
-- populated by the Intune connector for licensing-relevant apps (bounded by
-- ConnectorOptions.InstallVisibilityPatterns). vw.AppInstall reads this.
IF OBJECT_ID('fact.AppInstall') IS NULL
CREATE TABLE fact.AppInstall
(
    AppInstallId      bigint IDENTITY(1,1) CONSTRAINT PK_fact_AppInstall PRIMARY KEY,
    AppId             nvarchar(128) NULL,
    DisplayName       nvarchar(256) NULL,
    DeviceId          nvarchar(64)  NULL,
    DeviceName        nvarchar(256) NULL,
    UserPrincipalName nvarchar(256) NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    SnapshotUtc       datetime2(3)  NULL,
    LoadedUtc         datetime2(3)  NOT NULL CONSTRAINT DF_fact_AI_Loaded DEFAULT sysutcdatetime()
);
GO
-- v2: per-device install expansion is read three ways - by device (install-coverage
-- EXISTS probes + device drill), by app name (app drill) and by user (install signal).
IF IndexProperty(OBJECT_ID('fact.AppInstall'),'IX_AppInstall_Device','IndexID') IS NULL
    CREATE INDEX IX_AppInstall_Device ON fact.AppInstall(DeviceId)     INCLUDE (DisplayName);
IF IndexProperty(OBJECT_ID('fact.AppInstall'),'IX_AppInstall_Name','IndexID') IS NULL
    CREATE INDEX IX_AppInstall_Name   ON fact.AppInstall(DisplayName)  INCLUDE (DeviceName, UserPrincipalName, AppId);
IF IndexProperty(OBJECT_ID('fact.AppInstall'),'IX_AppInstall_Upn','IndexID') IS NULL
    CREATE INDEX IX_AppInstall_Upn    ON fact.AppInstall(UserPrincipalName) INCLUDE (DisplayName);
GO

IF OBJECT_ID('fact.DiscoveredApp') IS NULL
CREATE TABLE fact.DiscoveredApp
(
    DiscoveredAppId   bigint IDENTITY(1,1) CONSTRAINT PK_fact_DiscoveredApp PRIMARY KEY,
    AppName           nvarchar(256) NULL,
    Category          nvarchar(128) NULL,
    RiskScore         float         NULL,
    UserCount         bigint        NULL,
    UploadedBytes     bigint        NULL,
    DownloadedBytes   bigint        NULL,
    TrafficTotalBytes bigint        NULL,
    TransactionCount  bigint        NULL,
    LastSeen          nvarchar(64)  NULL,
    Tags              nvarchar(128) NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    SnapshotUtc       datetime2(3)  NULL,
    LoadedUtc         datetime2(3)  NOT NULL CONSTRAINT DF_fact_DiscA_Loaded DEFAULT sysutcdatetime()
);
GO

/* ---------------------------------------------------------------------------
   TIME-SERIES FACTS  (UPSERT-loaded by natural key; history accumulates)
   Uniqueness is enforced by a UNIQUE INDEX on the natural key (no computed column,
   so no determinism constraints); the UPSERT MERGE matches on the same key columns.
   --------------------------------------------------------------------------- */

IF OBJECT_ID('fact.AzureCost') IS NULL
CREATE TABLE fact.AzureCost
(
    Scope          nvarchar(256)  NOT NULL,
    UsageDate      date           NOT NULL,
    Cost           decimal(19,4)  NULL,
    Currency       nvarchar(8)    NULL,
    ServiceName    nvarchar(128)  NULL,
    ResourceGroup  nvarchar(128)  NULL,
    Source         nvarchar(64)   NULL,
    RunId          nvarchar(40)   NULL,
    SnapshotUtc    datetime2(3)   NULL,
    LoadedUtc      datetime2(3)   NOT NULL CONSTRAINT DF_fact_Cost_Loaded DEFAULT sysutcdatetime()
);
GO
-- Natural-key uniqueness (matches the UPSERT MERGE predicate). A plain unique index avoids the
-- determinism rules a computed/persisted hash column hits, and treats NULLs as equal so a missing
-- ServiceName/ResourceGroup collapses to one bucket (same semantics as the previous hash key).
IF IndexProperty(OBJECT_ID('fact.AzureCost'),'UQ_fact_AzureCost','IndexID') IS NULL
    CREATE UNIQUE INDEX UQ_fact_AzureCost ON fact.AzureCost(Scope, UsageDate, ServiceName, ResourceGroup);
IF IndexProperty(OBJECT_ID('fact.AzureCost'),'IX_Cost_Date','IndexID') IS NULL
    CREATE INDEX IX_Cost_Date ON fact.AzureCost(UsageDate) INCLUDE (Cost, ServiceName);
GO

IF OBJECT_ID('fact.AppUsage') IS NULL
CREATE TABLE fact.AppUsage
(
    [Date]                    date           NOT NULL,
    DeviceThumbprint          nvarchar(64)   NOT NULL,
    MachineName               nvarchar(128)  NULL,
    UserSid                   nvarchar(128)  NULL,
    ExePath                   nvarchar(500)  NOT NULL,
    DisplayName               nvarchar(256)  NULL,
    ProductName               nvarchar(256)  NULL,
    [Description]             nvarchar(256)  NULL,
    Company                   nvarchar(256)  NULL,
    FileVersion               nvarchar(64)   NULL,
    Launches                  int            NULL,
    FirstSeenUtc              datetime2(3)   NULL,
    LastSeenUtc               datetime2(3)   NULL,
    ForegroundActiveSeconds   bigint         NULL,
    ForegroundIdleSeconds     bigint         NULL,
    VisibleBackgroundSeconds  bigint         NULL,
    MinimizedSeconds          bigint         NULL,
    TraySeconds               bigint         NULL,
    UtcOffsetMinutes          int            NULL,
    AgentVersion              nvarchar(32)   NULL,
    ReceiveId                 nvarchar(64)   NULL,
    Source                    nvarchar(64)   NULL,
    RunId                     nvarchar(40)   NULL,
    SnapshotUtc               datetime2(3)   NULL,
    LoadedUtc                 datetime2(3)   NOT NULL CONSTRAINT DF_fact_AU_Loaded DEFAULT sysutcdatetime()
);
GO
IF IndexProperty(OBJECT_ID('fact.AppUsage'),'UQ_fact_AppUsage','IndexID') IS NULL
    CREATE UNIQUE INDEX UQ_fact_AppUsage ON fact.AppUsage([Date], DeviceThumbprint, UserSid, ExePath);
IF IndexProperty(OBJECT_ID('fact.AppUsage'),'IX_AU_Date','IndexID') IS NULL
    CREATE INDEX IX_AU_Date ON fact.AppUsage([Date]) INCLUDE (ForegroundActiveSeconds);
IF IndexProperty(OBJECT_ID('fact.AppUsage'),'IX_AU_Sid','IndexID') IS NULL
    CREATE INDEX IX_AU_Sid  ON fact.AppUsage(UserSid);
-- v2: vw.AppUsageByUser90 slices the last 90 days per SID and aggregates per exe -
-- (UserSid, Date) with the projected measure columns serves it without touching the heap.
IF IndexProperty(OBJECT_ID('fact.AppUsage'),'IX_AU_SidDate','IndexID') IS NULL
    CREATE INDEX IX_AU_SidDate ON fact.AppUsage(UserSid, [Date]) INCLUDE (ExePath, MachineName, ForegroundActiveSeconds);
GO

/* ---------------------------------------------------------------------------
   LOAD-RUN LOG  (one row per (run, entity) load, for observability)
   --------------------------------------------------------------------------- */

IF OBJECT_ID('meta.LoadRun') IS NULL
CREATE TABLE meta.LoadRun
(
    LoadRunId    bigint IDENTITY(1,1) CONSTRAINT PK_meta_LoadRun PRIMARY KEY,
    RunId        nvarchar(40)  NULL,
    Source       nvarchar(64)  NULL,
    Entity       nvarchar(64)  NULL,
    Mode         nvarchar(16)  NULL,
    [RowCount]   int           NULL,
    StartedUtc   datetime2(3)  NULL,
    CompletedUtc datetime2(3)  NULL,
    Status       nvarchar(16)  NULL,
    Message      nvarchar(512) NULL
);
GO
-- v2: the data-health page reads "latest load per (source, entity)" and per-run rollups.
IF IndexProperty(OBJECT_ID('meta.LoadRun'),'IX_LoadRun_Entity','IndexID') IS NULL
    CREATE INDEX IX_LoadRun_Entity ON meta.LoadRun(Entity, StartedUtc DESC) INCLUDE (Source, Mode, [RowCount], Status, CompletedUtc);
IF IndexProperty(OBJECT_ID('meta.LoadRun'),'IX_LoadRun_Run','IndexID') IS NULL
    CREATE INDEX IX_LoadRun_Run    ON meta.LoadRun(RunId) INCLUDE (Entity, Status);
GO

/* ---------------------------------------------------------------------------
   CONNECTOR STATE  (delta links / watermarks so incremental pulls survive restarts)
   --------------------------------------------------------------------------- */

IF OBJECT_ID('meta.ConnectorState') IS NULL
CREATE TABLE meta.ConnectorState
(
    ConnectorName nvarchar(64)   NOT NULL CONSTRAINT PK_meta_ConnectorState PRIMARY KEY,
    Watermark     nvarchar(max)  NULL,      -- delta token, high-water timestamp, or JSON cursor
    UpdatedUtc    datetime2(3)   NOT NULL CONSTRAINT DF_meta_CS_Upd DEFAULT sysutcdatetime()
);
GO

/* ---------------------------------------------------------------------------
   ANALYTIC VIEWS  (the seam the scoring engine + dashboard build on)
   --------------------------------------------------------------------------- */

-- Seats owned vs. consumed per product, with idle (unassigned) seats.
CREATE OR ALTER VIEW vw.SkuUtilization AS
SELECT
    s.SkuId,
    s.SkuPartNumber,
    s.DisplayName,
    s.PrepaidUnitsEnabled                         AS SeatsOwned,
    s.ConsumedUnits                               AS SeatsAssigned,
    (ISNULL(s.PrepaidUnitsEnabled,0) - ISNULL(s.ConsumedUnits,0)) AS SeatsIdle,
    CASE WHEN ISNULL(s.PrepaidUnitsEnabled,0) = 0 THEN NULL
         ELSE CONVERT(decimal(5,2), 100.0 * s.ConsumedUnits / s.PrepaidUnitsEnabled) END AS PercentAssigned
FROM dim.Sku s;
GO

-- vw.LicenseSignals - the scoring engine's primary input - is defined ONCE, in the
-- SCORING section below. (Historical note: a second copy once lived in a separate
-- scoring.sql; dual CREATE OR ALTER owners silently reverted each other and broke
-- scoring. This consolidated file is now the single owner - never redefine the view
-- anywhere else.)

-- Per (user, app) desktop foreground usage over the last 30 days, from the agent.
CREATE OR ALTER VIEW vw.AppUsageLast30 AS
SELECT
    UserSid,
    ExePath,
    MAX(DisplayName)                       AS DisplayName,
    MAX(ProductName)                       AS ProductName,
    SUM(ForegroundActiveSeconds)           AS FgActiveSeconds,
    SUM(Launches)                          AS Launches,
    MAX([Date])                            AS LastDay,
    COUNT(DISTINCT [Date])                 AS ActiveDays
FROM fact.AppUsage
WHERE [Date] >= CONVERT(date, DATEADD(day, -30, sysutcdatetime()))
GROUP BY UserSid, ExePath;
GO

/* ==========================================================================
   SCORING - cost map, SKU descriptions, verdict tables, license-signals view
   ========================================================================== */

/* ---- editable cost map (populated by the pricing connector or by hand) ----
   Prices are NOT hardcoded here - they change and they're tenant-specific. They
   are loaded by the pricing.skucost connector from your negotiated Price Sheet
   API (per currency, current month), or maintained manually (Origin='manual').
   See PRICING.md. A SKU with no row simply yields no savings figure (it is still
   scored on activity). An optional bootstrap of stale list prices is available in
   schema/seed-fallback-prices.sql if you want figures before pricing is wired. */

IF OBJECT_ID('ref.SkuCost') IS NULL
CREATE TABLE ref.SkuCost
(
    SkuPartNumber   nvarchar(128) NOT NULL CONSTRAINT PK_ref_SkuCost PRIMARY KEY,
    DisplayName     nvarchar(256) NULL,
    MonthlyUnitCost decimal(10,2) NULL,
    Currency        nvarchar(8)   NOT NULL DEFAULT 'USD',
    Origin          nvarchar(32)  NOT NULL DEFAULT 'manual',  -- 'price-sheet' | 'manual' | 'org' | 'fallback-list-price'
    AsOfDate        date          NULL,                       -- the pricing month the figure came from
    UpdatedUtc      datetime2(3)  NOT NULL DEFAULT sysutcdatetime()
);
GO

/* ---- human-readable SKU descriptions (shown in the license drill) ----------
   Curated offline (Microsoft product naming is stable); maintain freely - the
   dashboard shows whatever is here, unmatched SKUs simply show no blurb. */
IF OBJECT_ID('ref.SkuDescription') IS NULL
CREATE TABLE ref.SkuDescription
(
    SkuPartNumber nvarchar(128) NOT NULL CONSTRAINT PK_ref_SkuDescription PRIMARY KEY,
    Description   nvarchar(1024) NOT NULL,
    UpdatedUtc    datetime2(3)  NOT NULL CONSTRAINT DF_ref_SkuDesc_Upd DEFAULT sysutcdatetime()
);
GO
MERGE ref.SkuDescription AS t
USING (SELECT * FROM (VALUES
 (N'SPE_E3',           N'Microsoft 365 E3: the full enterprise suite - Office desktop apps (Word, Excel, PowerPoint, Outlook), Exchange Online, Teams, SharePoint, OneDrive (1 TB+), Windows Enterprise upgrade rights, Intune device management and Entra ID P1 identity. The standard knowledge-worker license.'),
 (N'SPE_E5',           N'Microsoft 365 E5: everything in E3 plus the advanced security stack (Defender for Office/Endpoint/Identity), Entra ID P2, advanced compliance/eDiscovery, Power BI Pro and Teams Phone with audio conferencing. The premium tier - typically justified by security or analytics use.'),
 (N'ENTERPRISEPACK',   N'Office 365 E3: Office desktop apps plus Exchange, Teams, SharePoint and OneDrive - without the Windows license, Intune and Entra P1 that Microsoft 365 E3 adds.'),
 (N'STANDARDPACK',     N'Office 365 E1: web/mobile-only Office, Exchange (50 GB), Teams, SharePoint and OneDrive. No desktop Office installs.'),
 (N'DESKLESSPACK',     N'Office 365 F3: frontline-worker plan - web/mobile Office, 2 GB mailbox, Teams and SharePoint with kiosk-level limits.'),
 (N'SPE_F1',           N'Microsoft 365 F3: frontline-worker suite - web/mobile Office, Teams, 2 GB Exchange mailbox, Windows Enterprise rights and Intune, designed for shift/plant workers without a desk.'),
 (N'M365_F1_COMM',     N'Microsoft 365 F1: the lightest frontline plan - Teams, SharePoint and web Office viewing (no user mailbox by default).'),
 (N'SPB',              N'Microsoft 365 Business Premium: SMB suite (max 300 seats) - desktop Office, Exchange, Teams, SharePoint plus Intune and Defender for Business.'),
 (N'Microsoft_365_Copilot', N'Microsoft 365 Copilot: the AI assistant add-on embedded in Word, Excel, PowerPoint, Outlook and Teams, grounded in the tenant''s data via Microsoft Graph. Requires an underlying M365 license; priced per user.'),
 (N'POWER_BI_PRO',     N'Power BI Pro: publish, share and collaborate on Power BI reports and dashboards. Needed to share content with others; viewing shared content also requires Pro (unless hosted in Premium capacity).'),
 (N'PBI_PREMIUM_PER_USER', N'Power BI Premium Per User: everything in Pro plus paginated reports, larger models, more frequent refresh and AI features, per user instead of per capacity.'),
 (N'POWER_BI_STANDARD', N'Power BI (free): personal authoring and viewing of own content only - cannot share or consume shared workspaces. Costs nothing; never waste.'),
 (N'VISIOCLIENT',      N'Visio Plan 2: the full Visio desktop app plus Visio for the web - diagramming, BPMN, org charts, with Office integration.'),
 (N'PROJECTPROFESSIONAL', N'Project Plan 3: Project desktop app and Project for the web - schedules, resources, roadmaps; the standard PM license.'),
 (N'PROJECTPREMIUM',   N'Project Plan 5: Plan 3 plus portfolio selection/optimisation, demand management and enterprise resource planning.'),
 (N'EMS',              N'Enterprise Mobility + Security E3: Entra ID P1, Intune, Azure Information Protection P1 and Advanced Threat Analytics - the identity/device bundle that overlaps with Microsoft 365 E3.'),
 (N'EMSPREMIUM',       N'Enterprise Mobility + Security E5: EMS E3 plus Entra ID P2 (PIM, risk-based Conditional Access), Defender for Identity, AIP P2 and Defender for Cloud Apps.'),
 (N'AAD_PREMIUM',      N'Microsoft Entra ID P1: Conditional Access, group-based licensing, self-service password reset, hybrid identity (whole-tenant capability; per-user licensed).'),
 (N'AAD_PREMIUM_P2',   N'Microsoft Entra ID P2: P1 plus Privileged Identity Management, Identity Protection (risk policies) and access reviews.'),
 (N'IDENTITY_THREAT_PROTECTION', N'Microsoft 365 E5 Security: the E5 security stack as an add-on to E3 - Defender for Endpoint/Office/Identity, Entra ID P2 and Defender for Cloud Apps.'),
 (N'INFORMATION_PROTECTION_COMPLIANCE', N'Microsoft 365 E5 Compliance: the E5 compliance stack as an add-on to E3 - advanced eDiscovery/audit, insider risk, records management and information protection.'),
 (N'SPE_F5_SEC',       N'Microsoft 365 F5 Security add-on: brings the E5-grade security stack (Defender suite, Entra ID P2) to frontline F1/F3 users.'),
 (N'MCOEV',            N'Microsoft Teams Phone Standard: turns Teams into a PBX - call control, voicemail, transfer/forwarding. Calling plans or operator connect are separate.'),
 (N'MCOPSTNC',         N'Communications Credits: pay-as-you-go balance for dial-out/toll-free in Teams audio conferencing and calling. Consumption-based; the "seat" count is not a per-user license.'),
 (N'Microsoft_Teams_Audio_Conferencing_select_dial_out', N'Teams Audio Conferencing (select dial-out): lets meeting organisers include dial-in numbers / limited dial-out in Teams meetings. Zero-cost add-on in most agreements.'),
 (N'DYN365_ENTERPRISE_CUSTOMER_SERVICE', N'Dynamics 365 Customer Service Enterprise: the full case-management/omnichannel service-desk application with SLAs, entitlements, knowledge base and Copilot features. One of the costlier per-seat licenses - service accounts holding it deserve scrutiny.'),
 (N'WIN10_VDA_E3',     N'Windows Enterprise E3 (VDA): Windows Enterprise upgrade + virtualisation access rights per user. Usually bundled inside Microsoft 365 E3 - standalone copies alongside M365 E3 are typically redundant.'),
 (N'WIN10_VDA_E5',     N'Windows Enterprise E5 (VDA): Windows E3 rights plus Defender for Endpoint P2. Bundled inside Microsoft 365 E5; standalone copies alongside M365 E5 are typically redundant.'),
 (N'FLOW_FREE',        N'Power Automate (free): personal flows with standard connectors. Costs nothing; never waste.'),
 (N'POWERAPPS_VIRAL',  N'Power Apps (viral/trial plan): self-service sign-up plan with no cost. Never waste.'),
 (N'STREAM',           N'Microsoft Stream (classic seat): video portal access, included with most M365 suites at no separate cost.'),
 (N'EXCHANGESTANDARD', N'Exchange Online Plan 1: a 50 GB cloud mailbox without the rest of the suite - common for service/shared scenarios.'),
 (N'EXCHANGEENTERPRISE', N'Exchange Online Plan 2: 100 GB mailbox plus litigation hold and DLP - required for unlimited archiving and legal hold.')
) v(SkuPartNumber, Description)) s
ON  t.SkuPartNumber = s.SkuPartNumber
WHEN MATCHED THEN UPDATE SET Description = s.Description, UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT (SkuPartNumber, Description) VALUES (s.SkuPartNumber, s.Description);
GO

/* ---- operator-supplied descriptions (2026-06-11) ---------------------------
   Provided by Contoso IM; this MERGE runs AFTER the baseline seed above, so these
   take precedence on overlapping keys. Part numbers marked (inferred) had none in
   the source list and use Microsoft's standard skuPartNumber - verify against
   dim.Sku if one doesn't match. */
MERGE ref.SkuDescription AS t
USING (SELECT * FROM (VALUES
 (N'SPE_E3',                  N'Enterprise productivity suite including Office desktop apps, Exchange Online, SharePoint, OneDrive, Teams, Intune, Windows Enterprise, and core security/compliance capabilities.'),
 (N'AAD_PREMIUM',             N'Advanced identity management: Conditional Access, self-service password reset, dynamic groups, hybrid identity support.'),
 (N'PROJECTPREMIUM',          N'Full Microsoft Project capabilities with portfolio management, advanced resource planning, and Project Online functionality.'),
 (N'RMSBASIC',                N'Basic information protection allowing encryption and usage rights on documents and emails.'),
 (N'STANDARDPACK',            N'Web/mobile Office apps, Exchange Online, SharePoint, OneDrive, Teams; no desktop Office applications.'),
 (N'INFORMATION_PROTECTION_COMPLIANCE', N'Microsoft Purview features such as data classification, sensitivity labels, retention policies, and compliance tools.'),
 (N'Microsoft_Teams_Audio_Conferencing_select_dial_out', N'Allows Teams meetings to dial out to participants'' phone numbers.'),
 (N'DYN365_ENTERPRISE_SALES', N'CRM platform for managing leads, opportunities, sales forecasting, pipelines, and customer relationships.'),
 (N'STREAM',                  N'Enterprise video platform for uploading, managing, and sharing internal videos.'),
 (N'IDENTITY_THREAT_PROTECTION', N'Detects compromised identities and suspicious authentication activities. Often bundled with Defender security suites.'),
 (N'Microsoft_Teams_Premium', N'Advanced Teams meeting features such as meeting templates, webinars, AI-generated recaps, enhanced security, and premium virtual appointments.'),   -- (inferred)
 (N'Power_Pages_vTrial_for_Makers', N'Trial environment for building external-facing business websites using Power Platform.'),   -- (inferred)
 (N'PHONESYSTEM_VIRTUALUSER', N'Enables auto attendants and call queues in Teams Phone environments.'),
 (N'MCOPSTNC',                N'Pay-as-you-go credits for PSTN services such as dial-out and toll-free usage in Teams Phone.'),
 (N'WIN10_VDA_E5',            N'Virtual Desktop Access rights plus advanced endpoint security capabilities for virtual environments.'),
 (N'FLOW_PER_USER',           N'Premium workflow automation with access to premium connectors and attended RPA capabilities.'),
 (N'DESKLESSPACK',            N'Frontline worker license with web/mobile Office apps, email, Teams, and limited productivity services.'),
 (N'Microsoft_Teams_Rooms_Pro', N'Full management and advanced meeting experiences for Teams Rooms devices.'),   -- (inferred)
 (N'MDATP_Server',            N'Endpoint detection and response (EDR) protection for Windows and Linux servers.'),
 (N'M365_F1_COMM',            N'Basic frontline communication license focused on Teams, Yammer/Viva Engage, and SharePoint access.'),
 (N'PROJECTPROFESSIONAL',     N'Project management solution including desktop Project app, task scheduling, reporting, and Project Online.'),
 (N'POWERAPPS_DEV',           N'Free development environment for building and testing Power Apps and Power Automate solutions.'),
 (N'CCIBOTS_PRIVPREV_VIRAL',  N'Trial access to conversational AI and chatbot creation capabilities within Microsoft''s low-code ecosystem.'),
 (N'Microsoft_365_Copilot',   N'AI assistant integrated with Word, Excel, PowerPoint, Outlook, Teams, and other Microsoft 365 applications.'),
 (N'WINDOWS_STORE',           N'Legacy capability for managing and distributing Microsoft Store applications.'),
 (N'SPE_F1',                  N'Frontline suite including Teams, web/mobile Office apps, Intune, Entra ID, and Windows Enterprise features for frontline workers.'),
 (N'SPE_F5_SEC',              N'Advanced security add-on providing Defender capabilities, identity protection, and endpoint security enhancements.'),
 (N'ENTERPRISEPACK',          N'Enterprise productivity suite with Office desktop apps, Exchange, Teams, SharePoint, and OneDrive. Excludes Intune and Windows Enterprise rights.'),
 (N'DYN365_ENTERPRISE_CUSTOMER_SERVICE', N'Customer service platform for case management, knowledge bases, SLAs, omnichannel support, and agent productivity tools.'),
 (N'DYN365_TEAM_MEMBERS',     N'Light-use Dynamics access for reading data, updating basic records, and completing simple tasks.'),   -- (inferred)
 (N'PBI_PREMIUM_P1_ADDON',    N'Dedicated Power BI capacity enabling large-scale report sharing without requiring Pro licenses for viewers.'),   -- (inferred)
 (N'MICROSOFT_ECDN',          N'Enterprise Content Delivery Network that optimizes bandwidth usage during large-scale live video events.'),   -- (inferred)
 (N'AAD_PREMIUM_P2',          N'Adds Identity Protection, Privileged Identity Management (PIM), and risk-based Conditional Access to P1 capabilities.'),
 (N'RIGHTSMANAGEMENT_ADHOC',  N'Free rights management capabilities allowing users to protect documents and emails.'),
 (N'SHAREPOINTSTORAGE',       N'Additional storage capacity for SharePoint Online environments.'),
 (N'Dynamics_365_Guides_vTrial', N'Trial license for mixed-reality work instructions using HoloLens devices.'),   -- (inferred)
 (N'POWER_BI_STANDARD',       N'Personal report creation and dashboard consumption. Does not include broad sharing capabilities.'),
 (N'POWERAPPS_PER_USER',      N'Full access to unlimited custom Power Apps using premium connectors and Dataverse.'),
 (N'O365_w/o Teams Bundle_M3', N'Office 365 enterprise productivity services excluding Microsoft Teams.'),
 (N'VISIOCLIENT',             N'Desktop Visio application with advanced diagramming, templates, data linking, and collaboration features.'),
 (N'POWERAPPS_VIRAL',         N'Free/community-style access generated through app sharing; limited production use.'),
 (N'MCOMEETACPEA',            N'Allows meeting organizers to include dial-in telephone numbers for Teams meetings.'),
 (N'MCOEV',                   N'Cloud PBX functionality enabling users to make and receive telephone calls within Teams.'),
 (N'CDS_DB_CAPACITY',         N'Additional storage for Microsoft Dataverse environments.'),
 (N'FLOW_FREE',               N'Basic workflow automation using standard connectors with limited capabilities.'),
 (N'POWER_BI_PRO',            N'Enables publishing, sharing, collaboration, scheduled refresh, and workspace participation in Power BI.'),
 (N'DYN365_BUSCENTRAL_PREMIUM', N'ERP solution covering finance, supply chain, manufacturing, projects, warehousing, and service management.')   -- (inferred)
) v(SkuPartNumber, Description)) s
ON  t.SkuPartNumber = s.SkuPartNumber
WHEN MATCHED THEN UPDATE SET Description = s.Description, UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT (SkuPartNumber, Description) VALUES (s.SkuPartNumber, s.Description);
GO

/* ---- verdict tables (REPLACE-loaded per run) ---------------------------- */

IF OBJECT_ID('score.AssignmentVerdict') IS NULL
CREATE TABLE score.AssignmentVerdict
(
    UserId                nvarchar(64)  NOT NULL,
    SkuId                 nvarchar(64)  NOT NULL,
    UserPrincipalName     nvarchar(256) NULL,
    DisplayName           nvarchar(256) NULL,
    SkuPartNumber         nvarchar(128) NULL,
    SkuName               nvarchar(256) NULL,
    Verdict               nvarchar(16)  NULL,   -- KEEP | REVIEW | RECLAIM
    WasteScore            int           NULL,   -- 0..100
    Confidence            nvarchar(8)   NULL,   -- LOW | MEDIUM | HIGH
    ReasonCodes           nvarchar(512) NULL,   -- comma-separated controlled vocabulary
    EffectiveInactiveDays int           NULL,
    EstMonthlySavings     decimal(12,2) NULL,
    Currency              nvarchar(8)   NULL,
    Department            nvarchar(128) NULL,
    Country               nvarchar(16)  NULL,
    SignalCount           int           NULL,           -- v2: independent signal sources consulted for this seat
    EvidenceJson          nvarchar(2000) NULL,          -- v2: compact per-signal evidence trail (dashboard drawer)
    Source                nvarchar(64)  NULL,
    RunId                 nvarchar(40)  NULL,
    ScoredUtc             datetime2(3)  NOT NULL CONSTRAINT DF_score_AV_Scored DEFAULT sysutcdatetime(),
    CONSTRAINT PK_score_AssignmentVerdict PRIMARY KEY (UserId, SkuId)
);
GO
IF COL_LENGTH('score.AssignmentVerdict','SignalCount')  IS NULL ALTER TABLE score.AssignmentVerdict ADD SignalCount int NULL;
IF COL_LENGTH('score.AssignmentVerdict','EvidenceJson') IS NULL ALTER TABLE score.AssignmentVerdict ADD EvidenceJson nvarchar(2000) NULL;
GO
-- v2: the review queue orders by savings within a verdict; the user/SKU drills filter
-- by their respective keys. Covering indexes serve all three without heap lookups.
IF IndexProperty(OBJECT_ID('score.AssignmentVerdict'),'IX_AV_Verdict','IndexID') IS NOT NULL
    DROP INDEX IX_AV_Verdict ON score.AssignmentVerdict;
IF IndexProperty(OBJECT_ID('score.AssignmentVerdict'),'IX_AV_Verdict_v2','IndexID') IS NULL
    CREATE INDEX IX_AV_Verdict_v2 ON score.AssignmentVerdict(Verdict)
        INCLUDE (UserPrincipalName, DisplayName, SkuPartNumber, SkuName, WasteScore, Confidence,
                 EstMonthlySavings, EffectiveInactiveDays, Department, Country);
IF IndexProperty(OBJECT_ID('score.AssignmentVerdict'),'IX_AV_SkuPart','IndexID') IS NULL
    CREATE INDEX IX_AV_SkuPart ON score.AssignmentVerdict(SkuPartNumber)
        INCLUDE (Verdict, EstMonthlySavings, Department, Country, EffectiveInactiveDays);
GO

IF OBJECT_ID('score.SkuVerdict') IS NULL
CREATE TABLE score.SkuVerdict
(
    SkuId             nvarchar(64)  NOT NULL CONSTRAINT PK_score_SkuVerdict PRIMARY KEY,
    SkuPartNumber     nvarchar(128) NULL,
    SkuName           nvarchar(256) NULL,
    Verdict           nvarchar(16)  NULL,
    SeatsOwned        int           NULL,
    SeatsAssigned     int           NULL,
    SeatsIdle         int           NULL,
    ReasonCodes       nvarchar(256) NULL,
    EstMonthlySavings decimal(12,2) NULL,
    Currency          nvarchar(8)   NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    ScoredUtc         datetime2(3)  NOT NULL CONSTRAINT DF_score_SV_Scored DEFAULT sysutcdatetime()
);
GO
-- v2: the SKU drill keys by part number; make that lookup a seek (unique where present).
IF IndexProperty(OBJECT_ID('score.SkuVerdict'),'IX_SV_Part','IndexID') IS NULL
    CREATE UNIQUE INDEX IX_SV_Part ON score.SkuVerdict(SkuPartNumber) WHERE SkuPartNumber IS NOT NULL;
GO

IF OBJECT_ID('score.RunSummary') IS NULL
CREATE TABLE score.RunSummary
(
    RunId                   nvarchar(40)  NOT NULL CONSTRAINT PK_score_RunSummary PRIMARY KEY,
    ScoredUtc               datetime2(3)  NOT NULL,
    Assignments             int           NULL,
    KeepCount               int           NULL,
    ReviewCount             int           NULL,
    ReclaimCount            int           NULL,
    ReclaimMonthlySavings   decimal(14,2) NULL,
    ReviewMonthlySavings    decimal(14,2) NULL,
    IdleSeatMonthlySavings  decimal(14,2) NULL,
    Currency                nvarchar(8)   NULL
);
GO
-- v2: /api/summary (TOP 1 ORDER BY ScoredUtc DESC) and the trend chart both sort on this.
IF IndexProperty(OBJECT_ID('score.RunSummary'),'IX_RunSummary_Scored','IndexID') IS NULL
    CREATE INDEX IX_RunSummary_Scored ON score.RunSummary(ScoredUtc DESC);
GO

/* ---- verdict history (append-only; one row per seat per scoring run) -------
   Powers trend lines, "what changed since the last run" (vw.VerdictDelta) and
   flap analysis. The scoring job appends each run and purges rows older than
   its retention window (default 400 days) so growth stays bounded. */
IF OBJECT_ID('score.VerdictHistory') IS NULL
CREATE TABLE score.VerdictHistory
(
    VerdictHistoryId  bigint IDENTITY(1,1) CONSTRAINT PK_score_VerdictHistory PRIMARY KEY,
    RunId             nvarchar(40)  NOT NULL,
    ScoredUtc         datetime2(3)  NOT NULL,
    UserId            nvarchar(64)  NOT NULL,
    SkuId             nvarchar(64)  NOT NULL,
    SkuPartNumber     nvarchar(128) NULL,
    Verdict           nvarchar(16)  NULL,
    WasteScore        int           NULL,
    Confidence        nvarchar(8)   NULL,
    ReasonCodes       nvarchar(512) NULL,
    EstMonthlySavings decimal(12,2) NULL
);
GO
IF IndexProperty(OBJECT_ID('score.VerdictHistory'),'IX_VH_Seat','IndexID') IS NULL
    CREATE INDEX IX_VH_Seat ON score.VerdictHistory(UserId, SkuId, ScoredUtc DESC) INCLUDE (Verdict, WasteScore, EstMonthlySavings);
IF IndexProperty(OBJECT_ID('score.VerdictHistory'),'IX_VH_Run','IndexID') IS NULL
    CREATE INDEX IX_VH_Run  ON score.VerdictHistory(RunId) INCLUDE (Verdict, EstMonthlySavings);
IF IndexProperty(OBJECT_ID('score.VerdictHistory'),'IX_VH_Scored','IndexID') IS NULL
    CREATE INDEX IX_VH_Scored ON score.VerdictHistory(ScoredUtc);
GO

/* ---- extended signals view (raw fields; the engine computes day-deltas) -- */

CREATE OR ALTER VIEW vw.LicenseSignals AS
WITH concealed AS (
    -- displayConcealedNames is tenant-wide; when on, the usage report masks UPNs so
    -- they can't join to real users. Surface it as a tenant flag so the engine can
    -- lower confidence rather than silently losing the M365 signal for everyone.
    SELECT CAST(MAX(CAST(ISNULL(Concealed, 0) AS int)) AS bit) AS AnyConcealed FROM fact.ServiceUsage
)
SELECT
    u.UserId,
    u.UserPrincipalName,
    u.DisplayName,
    u.AccountEnabled,
    u.Department,
    u.JobTitle,
    u.UsageLocation                   AS Country,
    u.UserType,                                       -- v2: Member | Guest
    u.OnPremisesSyncEnabled,                          -- v2: hybrid provenance
    u.EmployeeHireDate,
    u.CreatedDateTime,
    u.EmployeeLeaveDateTime,
    u.LastSignInDateTime,
    u.LastNonInteractiveSignInDateTime,
    la.SkuId,
    la.SkuPartNumber,
    sk.DisplayName                  AS SkuName,
    la.AssignedDirectly,
    la.State                        AS AssignmentState,
    la.LastUpdatedDateTime          AS AssignmentLastUpdatedDateTime,
    c.AnyConcealed                  AS M365ActivityConcealed,
    su.LastActivityAnyDate          AS M365LastActivityDate,
    -- Per-workload last activity: lets the engine judge DEPTH of use (a premium
    -- suite active in only one workload is a right-size candidate), and report
    -- presence (a report row that says "no activity" is evidence; a missing row
    -- is just missing telemetry).
    su.TeamsLastActivityDate,
    su.ExchangeLastActivityDate,
    su.OneDriveLastActivityDate,
    su.SharePointLastActivityDate,
    su.ReportRefreshDate            AS M365ReportRefreshDate,
    u.LastSuccessfulSignInDateTime,
    -- Prefer the count the sink materialises; legacy rows (loaded before v2)
    -- fall back to counting the JSON array once, until the next connector run.
    CASE WHEN la.DisabledPlanCount IS NOT NULL THEN la.DisabledPlanCount
         WHEN la.DisabledServicePlanIds IS NULL THEN 0
         ELSE (SELECT COUNT(*) FROM OPENJSON(la.DisabledServicePlanIds)) END AS DisabledPlanCount,
    -- v2 deterministic mailbox purpose (shared/room/equipment beats the name heuristic)
    mb.UserPurpose                  AS MailboxPurpose,
    mb.AutomaticRepliesStatus       AS MailboxAutoReply,
    -- v2 onboarding evidence: never-registered MFA corroborates "never onboarded"
    am.IsMfaRegistered,
    am.LastUpdatedDateTime          AS AuthMethodsUpdatedDateTime
FROM fact.LicenseAssignment la
JOIN dim.[User] u              ON u.UserId = la.UserId
LEFT JOIN dim.Sku sk          ON sk.SkuId = la.SkuId
LEFT JOIN fact.ServiceUsage su ON su.UserPrincipalName = u.UserPrincipalName  -- matches only when not concealed
LEFT JOIN fact.Mailbox mb      ON mb.UserId = u.UserId
LEFT JOIN fact.AuthMethodRegistration am ON am.UserId = u.UserId
CROSS JOIN concealed c;
GO

/* ---- dashboard views ---------------------------------------------------- */

-- The human review queue: everything not KEEP, richest waste first. v2: decision-aware -
-- each row carries the human's standing decision (and snooze state) so re-scoring never
-- buries or resurfaces what a reviewer already handled. The verdict itself stays the
-- engine's honest, freshly-computed view; the DECISION is the human's, kept alongside.
CREATE OR ALTER VIEW vw.ReviewQueue AS
SELECT
    v.UserId, v.SkuId, v.UserPrincipalName, v.DisplayName, v.SkuPartNumber, v.SkuName,
    v.Verdict, v.WasteScore, v.Confidence, v.ReasonCodes,
    v.EffectiveInactiveDays, v.EstMonthlySavings, v.Currency, v.Department, v.Country,
    v.SignalCount, v.EvidenceJson, v.ScoredUtc,
    d.Decision       AS HumanDecision,
    d.DecidedBy, d.DecidedUtc, d.SnoozeUntilUtc, d.Note AS DecisionNote,
    CASE WHEN d.Decision = 'snooze' AND (d.SnoozeUntilUtc IS NULL OR d.SnoozeUntilUtc > sysutcdatetime())
         THEN 1 ELSE 0 END AS Snoozed
FROM score.AssignmentVerdict v
LEFT JOIN score.Decision d ON d.UserId = v.UserId AND d.SkuId = v.SkuId
WHERE v.Verdict <> 'KEEP';
GO

-- v2: what changed since the previous scoring run - new arrivals in the queue, verdicts
-- that escalated/relaxed, seats that left. The dashboard's "what's new" strip.
CREATE OR ALTER VIEW vw.VerdictDelta AS
WITH runs AS (
    SELECT RunId, ScoredUtc, ROW_NUMBER() OVER (ORDER BY ScoredUtc DESC) AS rn
    FROM (SELECT DISTINCT RunId, ScoredUtc FROM score.VerdictHistory) r
),
cur  AS (SELECT h.* FROM score.VerdictHistory h JOIN runs r ON r.RunId = h.RunId WHERE r.rn = 1),
prev AS (SELECT h.* FROM score.VerdictHistory h JOIN runs r ON r.RunId = h.RunId WHERE r.rn = 2)
SELECT
    COALESCE(c.UserId, p.UserId)   AS UserId,
    COALESCE(c.SkuId, p.SkuId)     AS SkuId,
    COALESCE(c.SkuPartNumber, p.SkuPartNumber) AS SkuPartNumber,
    p.Verdict                      AS PrevVerdict,
    c.Verdict                      AS CurrVerdict,
    p.WasteScore                   AS PrevScore,
    c.WasteScore                   AS CurrScore,
    c.EstMonthlySavings,
    CASE
        WHEN p.UserId IS NULL                                   THEN 'NEW'
        WHEN c.UserId IS NULL                                   THEN 'GONE'
        WHEN c.Verdict <> p.Verdict AND c.Verdict = 'RECLAIM'   THEN 'ESCALATED'
        WHEN c.Verdict <> p.Verdict AND p.Verdict = 'RECLAIM'   THEN 'RELAXED'
        WHEN c.Verdict <> p.Verdict                             THEN 'CHANGED'
        ELSE 'SAME'
    END AS ChangeKind,
    c.ScoredUtc
FROM cur c
FULL OUTER JOIN prev p ON p.UserId = c.UserId AND p.SkuId = c.SkuId
WHERE c.Verdict IS NULL OR p.Verdict IS NULL OR c.Verdict <> p.Verdict;
GO

-- v2: per-connector data freshness - the dashboard's data-health strip. A verdict is
-- only as trustworthy as its inputs are fresh; surface that instead of hiding it.
CREATE OR ALTER VIEW vw.DataFreshness AS
SELECT
    Entity,
    MAX(Source)                                        AS Source,
    MAX(CASE WHEN Status = 'OK' THEN CompletedUtc END) AS LastOkUtc,
    MAX(CompletedUtc)                                  AS LastAttemptUtc,
    SUM(CASE WHEN Status <> 'OK' THEN 1 ELSE 0 END)    AS FailedLoads7d,
    MAX(CASE WHEN Status = 'OK' THEN [RowCount] END)   AS LastOkRows,
    DATEDIFF(hour, MAX(CASE WHEN Status = 'OK' THEN CompletedUtc END), sysutcdatetime()) AS HoursSinceOk
FROM meta.LoadRun
WHERE StartedUtc >= DATEADD(day, -7, sysutcdatetime())
GROUP BY Entity;
GO

-- Latest-run savings rollup (assignment + idle-seat waste).
CREATE OR ALTER VIEW vw.SavingsSummary AS
SELECT TOP (1)
    rs.RunId, rs.ScoredUtc, rs.Assignments,
    rs.KeepCount, rs.ReviewCount, rs.ReclaimCount,
    rs.ReclaimMonthlySavings,
    rs.ReviewMonthlySavings,
    rs.IdleSeatMonthlySavings,
    (ISNULL(rs.ReclaimMonthlySavings,0) + ISNULL(rs.IdleSeatMonthlySavings,0)) AS HighConfidenceMonthlySavings,
    (ISNULL(rs.ReclaimMonthlySavings,0) + ISNULL(rs.ReviewMonthlySavings,0) + ISNULL(rs.IdleSeatMonthlySavings,0)) AS TotalPotentialMonthlySavings,
    rs.Currency
FROM score.RunSummary rs
ORDER BY rs.ScoredUtc DESC;
GO

-- Owned SKUs with no price on file (or a stale-by-origin one): what the operator
-- still needs to price for savings figures to be complete.
CREATE OR ALTER VIEW vw.UnpricedSkus AS
SELECT s.SkuId, s.SkuPartNumber, s.DisplayName AS SkuName,
       s.PrepaidUnitsEnabled AS SeatsOwned, s.ConsumedUnits AS SeatsAssigned,
       c.MonthlyUnitCost, c.Currency, c.Origin, c.AsOfDate
FROM dim.Sku s
LEFT JOIN ref.SkuCost c ON c.SkuPartNumber = s.SkuPartNumber
WHERE c.MonthlyUnitCost IS NULL;
GO

/* ==========================================================================
   OPTIMISATION - tier ladder, redundancy map, contracts, right-size/overlap/renewal/reallocation
   ========================================================================== */

/* ---- 1. Tier ladder: which SKU can step DOWN to which cheaper SKU --------- */
IF OBJECT_ID('ref.SkuDowngrade') IS NULL
CREATE TABLE ref.SkuDowngrade(
    FromPart nvarchar(128) NOT NULL,
    ToPart   nvarchar(128) NOT NULL,
    Note     nvarchar(200) NULL,
    CONSTRAINT PK_ref_SkuDowngrade PRIMARY KEY (FromPart, ToPart));
GO
MERGE ref.SkuDowngrade AS t
USING (SELECT * FROM (VALUES
    (N'SPE_E5',             N'SPE_E3',             N'Microsoft 365 E5 → E3'),
    (N'ENTERPRISEPREMIUM',  N'ENTERPRISEPACK',     N'Office 365 E5 → E3'),
    (N'ENTERPRISEPACK',     N'STANDARDPACK',       N'Office 365 E3 → E1'),
    (N'EMSPREMIUM',         N'EMS',                N'EMS E5 → E3'),
    (N'AAD_PREMIUM_P2',     N'AAD_PREMIUM',        N'Entra ID P2 → P1'),
    (N'PROJECTPREMIUM',     N'PROJECTPROFESSIONAL',N'Project Plan 5 → 3'),
    (N'PROJECTPROFESSIONAL',N'PROJECTESSENTIALS',  N'Project Plan 3 → Essentials'),
    (N'VISIOCLIENT',        N'VISIOONLINE_PLAN1',  N'Visio Plan 2 → 1')
) v(FromPart,ToPart,Note)) s
ON t.FromPart=s.FromPart AND t.ToPart=s.ToPart
WHEN MATCHED THEN UPDATE SET Note=s.Note
WHEN NOT MATCHED THEN INSERT(FromPart,ToPart,Note) VALUES(s.FromPart,s.ToPart,s.Note);
GO

/* ---- 2. Redundancy map: holding HeldPart makes RedundantPart wasteful ----- */
IF OBJECT_ID('ref.SkuRedundancy') IS NULL
CREATE TABLE ref.SkuRedundancy(
    RedundantPart nvarchar(128) NOT NULL,   -- the SKU that becomes waste
    HeldPart      nvarchar(128) NOT NULL,   -- the suite/SKU that already covers it
    Note          nvarchar(200) NULL,
    CONSTRAINT PK_ref_SkuRedundancy PRIMARY KEY (RedundantPart, HeldPart));
GO
MERGE ref.SkuRedundancy AS t
USING (SELECT * FROM (VALUES
    (N'ENTERPRISEPACK',  N'SPE_E3',  N'Office 365 E3 is included in Microsoft 365 E3'),
    (N'ENTERPRISEPACK',  N'SPE_E5',  N'Office 365 E3 is included in Microsoft 365 E5'),
    (N'STANDARDPACK',    N'SPE_E3',  N'Office 365 E1 is superseded by Microsoft 365 E3'),
    (N'EMS',             N'SPE_E3',  N'EMS E3 is included in Microsoft 365 E3'),
    (N'EMS',             N'SPE_E5',  N'EMS is included in Microsoft 365 E5'),
    (N'EMSPREMIUM',      N'SPE_E5',  N'EMS E5 is included in Microsoft 365 E5'),
    (N'AAD_PREMIUM',     N'SPE_E3',  N'Entra ID P1 is included in Microsoft 365 E3'),
    (N'AAD_PREMIUM',     N'EMS',     N'Entra ID P1 is included in EMS E3'),
    (N'AAD_PREMIUM_P2',  N'SPE_E5',  N'Entra ID P2 is included in Microsoft 365 E5'),
    (N'MCOEV',           N'SPE_E5',  N'Phone System is included in Microsoft 365 E5'),
    (N'POWER_BI_PRO',    N'SPE_E5',  N'Power BI Pro is included in Microsoft 365 E5'),
    (N'EXCHANGESTANDARD',N'SPE_E3',  N'Exchange Online is included in the suite'),
    (N'EXCHANGEENTERPRISE',N'SPE_E5',N'Exchange Online P2 is included in Microsoft 365 E5')
) v(RedundantPart,HeldPart,Note)) s
ON t.RedundantPart=s.RedundantPart AND t.HeldPart=s.HeldPart
WHEN MATCHED THEN UPDATE SET Note=s.Note
WHEN NOT MATCHED THEN INSERT(RedundantPart,HeldPart,Note) VALUES(s.RedundantPart,s.HeldPart,s.Note);
GO

/* ---- 3. Contracts / renewals (Contoso fills this in) ----------------------- */
IF OBJECT_ID('ref.Contract') IS NULL
CREATE TABLE ref.Contract(
    SkuPartNumber nvarchar(128) NOT NULL CONSTRAINT PK_ref_Contract PRIMARY KEY,
    RenewalDate   date          NULL,
    QuantityOwned int           NULL,
    Term          nvarchar(60)  NULL,     -- e.g. 'Annual', '3-year EA' (width kept in sync with contracts.sql)
    Notes         nvarchar(200) NULL);
GO
-- Example (delete / replace with your real contract lines):
-- INSERT ref.Contract(SkuPartNumber,RenewalDate,QuantityOwned,Term)
-- VALUES (N'SPE_E3','2026-07-01',2080,'3-year EA');

/* ---- 4. RIGHT-SIZE: REVIEW-band users who could drop a tier (save delta) -- */
CREATE OR ALTER VIEW vw.RightSize AS
SELECT av.UserId, av.UserPrincipalName, av.DisplayName, av.Department, av.Country,
       dg.FromPart AS FromSku, fc.DisplayName AS FromName, fc.MonthlyUnitCost AS FromCost,
       dg.ToPart   AS ToSku,   tc.DisplayName AS ToName,   tc.MonthlyUnitCost AS ToCost,
       dg.Note     AS Path,
       CAST(fc.MonthlyUnitCost - tc.MonthlyUnitCost AS decimal(12,2)) AS MonthlyDelta,
       av.EffectiveInactiveDays, av.Confidence, av.ReasonCodes
FROM score.AssignmentVerdict av
JOIN ref.SkuDowngrade dg ON dg.FromPart = av.SkuPartNumber
JOIN ref.SkuCost fc      ON fc.SkuPartNumber = dg.FromPart
JOIN ref.SkuCost tc      ON tc.SkuPartNumber = dg.ToPart
WHERE av.Verdict = 'REVIEW'
  AND fc.MonthlyUnitCost IS NOT NULL AND tc.MonthlyUnitCost IS NOT NULL
  AND fc.MonthlyUnitCost > tc.MonthlyUnitCost;
GO

/* ---- 5. OVERLAP: users holding a SKU already covered by a suite they hold - */
CREATE OR ALTER VIEW vw.SkuOverlap AS
SELECT a.UserId, a.UserPrincipalName, a.DisplayName, a.Department,
       rr.RedundantPart, rc.DisplayName AS RedundantName, rc.MonthlyUnitCost AS MonthlyWaste,
       rr.HeldPart AS CoveredByPart, hc.DisplayName AS CoveredByName, rr.Note
FROM ref.SkuRedundancy rr
JOIN score.AssignmentVerdict a ON a.SkuPartNumber = rr.RedundantPart
LEFT JOIN ref.SkuCost rc ON rc.SkuPartNumber = rr.RedundantPart
LEFT JOIN ref.SkuCost hc ON hc.SkuPartNumber = rr.HeldPart
WHERE EXISTS (SELECT 1 FROM score.AssignmentVerdict b
              WHERE b.UserId = a.UserId AND b.SkuPartNumber = rr.HeldPart);
GO

/* ---- 6. RENEWAL exposure: recoverable € ahead of each contract renewal ---- */
CREATE OR ALTER VIEW vw.RenewalExposure AS
SELECT c.SkuPartNumber, COALESCE(sv.SkuName, sc.DisplayName, c.SkuPartNumber) AS SkuName,
       c.RenewalDate, c.Term, c.QuantityOwned,
       DATEDIFF(day, CAST(sysutcdatetime() AS date), c.RenewalDate) AS DaysToRenewal,
       sv.SeatsAssigned, sv.SeatsIdle,
       ISNULL(sv.EstMonthlySavings,0)        AS IdleMonthly,
       ISNULL(rec.ReclaimMonthly,0)          AS ReclaimMonthly,
       ISNULL(rec.ReclaimSeats,0)            AS ReclaimSeats,
       CAST(ISNULL(sv.EstMonthlySavings,0)+ISNULL(rec.ReclaimMonthly,0) AS decimal(14,2)) AS RecoverableMonthly,
       c.Notes
FROM ref.Contract c
LEFT JOIN score.SkuVerdict sv ON sv.SkuPartNumber = c.SkuPartNumber
LEFT JOIN ref.SkuCost sc      ON sc.SkuPartNumber = c.SkuPartNumber
LEFT JOIN (SELECT SkuPartNumber, SUM(EstMonthlySavings) AS ReclaimMonthly, COUNT(*) AS ReclaimSeats
           FROM score.AssignmentVerdict WHERE Verdict='RECLAIM' GROUP BY SkuPartNumber) rec
       ON rec.SkuPartNumber = c.SkuPartNumber;
GO

/* ---- 7. REALLOCATION netting: recoverable pool before buying new seats ---- */
CREATE OR ALTER VIEW vw.Reallocation AS
SELECT sv.SkuPartNumber, sv.SkuName,
       ISNULL(sv.SeatsIdle,0)                          AS IdleSeats,
       ISNULL(rec.ReclaimSeats,0)                      AS ReclaimableSeats,
       ISNULL(sv.SeatsIdle,0)+ISNULL(rec.ReclaimSeats,0) AS RecoverablePool,
       sc.MonthlyUnitCost,
       CAST((ISNULL(sv.SeatsIdle,0)+ISNULL(rec.ReclaimSeats,0))*sc.MonthlyUnitCost AS decimal(14,2)) AS PoolMonthlyValue
FROM score.SkuVerdict sv
LEFT JOIN ref.SkuCost sc ON sc.SkuPartNumber = sv.SkuPartNumber
LEFT JOIN (SELECT SkuPartNumber, COUNT(*) AS ReclaimSeats
           FROM score.AssignmentVerdict WHERE Verdict='RECLAIM' GROUP BY SkuPartNumber) rec
       ON rec.SkuPartNumber = sv.SkuPartNumber
WHERE sc.MonthlyUnitCost > 0
  AND (ISNULL(sv.SeatsIdle,0)+ISNULL(rec.ReclaimSeats,0)) > 0;
GO

/* ==========================================================================
   AGENT USAGE - desktop foreground-time correlation
   ========================================================================== */

CREATE OR ALTER VIEW vw.AppUsageCorrelated AS
WITH agg AS (
    SELECT
        au.MachineName,
        au.ExePath,
        MAX(COALESCE(au.DisplayName, au.ProductName, au.Description)) AS AppName,
        MAX(au.ProductName) AS ProductName,
        MAX(au.Company)     AS Company,
        MAX(au.FileVersion) AS FileVersion,
        COUNT(DISTINCT au.UserSid) AS DistinctUserSids,
        SUM(au.ForegroundActiveSeconds) AS FgActiveSecondsAll,
        SUM(CASE WHEN au.[Date] >= CONVERT(date, DATEADD(day, -30, sysutcdatetime()))
                 THEN au.ForegroundActiveSeconds ELSE 0 END) AS FgActiveSeconds30,
        SUM(CASE WHEN au.[Date] >= CONVERT(date, DATEADD(day, -90, sysutcdatetime()))
                 THEN au.ForegroundActiveSeconds ELSE 0 END) AS FgActiveSeconds90,
        SUM(au.ForegroundActiveSeconds + au.ForegroundIdleSeconds + au.VisibleBackgroundSeconds
            + au.MinimizedSeconds + au.TraySeconds) AS RunningSecondsAll,
        SUM(au.Launches) AS Launches,
        COUNT(DISTINCT CASE WHEN au.[Date] >= CONVERT(date, DATEADD(day, -90, sysutcdatetime()))
                            THEN au.[Date] END) AS ActiveDays90,
        MAX(au.[Date]) AS LastUsedDate
    FROM fact.AppUsage au
    GROUP BY au.MachineName, au.ExePath
)
SELECT
    a.MachineName, a.ExePath, a.AppName, a.ProductName, a.Company, a.FileVersion,
    a.DistinctUserSids,
    d.DeviceId, d.DeviceName, d.OperatingSystem, d.ComplianceState,
    d.UserId            AS UserId,
    du.UserPrincipalName,
    du.DisplayName      AS UserDisplayName,
    du.Department, du.JobTitle, du.UsageLocation AS Country,
    a.FgActiveSecondsAll, a.FgActiveSeconds30, a.FgActiveSeconds90,
    CAST(a.FgActiveSeconds90 / 3600.0 AS decimal(12,1)) AS FgActiveHours90,
    a.RunningSecondsAll, a.Launches, a.ActiveDays90, a.LastUsedDate,
    DATEDIFF(day, a.LastUsedDate, CONVERT(date, sysutcdatetime())) AS DaysSinceUsed,
    CASE
        WHEN a.FgActiveSeconds30 >= 600 THEN 'Active'      -- >=10 min foreground in last 30d
        WHEN a.FgActiveSeconds90 >= 600 THEN 'Light'       -- used within the quarter
        WHEN a.RunningSecondsAll  >  0  THEN 'Open-only'   -- launched/visible but barely foreground
        ELSE 'Dormant'
    END AS UsageState
FROM agg a
LEFT JOIN dim.Device  d  ON d.DeviceName = a.MachineName
LEFT JOIN dim.[User]  du ON du.UserId    = d.UserId;
GO

/* Estate-wide per-application usage truth - the companion to Intune's install
   counts on the Applications tab: how many devices/users actually use an app,
   and how much. */
CREATE OR ALTER VIEW vw.AppUsageByApp AS
SELECT
    AppName,
    MAX(Company) AS Company,
    COUNT(DISTINCT MachineName)                              AS Devices,
    COUNT(DISTINCT UserPrincipalName)                        AS Users,
    SUM(FgActiveSeconds90)                                   AS FgActiveSeconds90,
    CAST(SUM(FgActiveSeconds90) / 3600.0 AS decimal(14,1))   AS FgActiveHours90,
    SUM(CASE WHEN UsageState IN ('Active','Light') THEN 1 ELSE 0 END) AS UsedInstalls,
    SUM(CASE WHEN UsageState = 'Open-only'         THEN 1 ELSE 0 END) AS OpenOnlyInstalls,
    SUM(CASE WHEN UsageState = 'Dormant'           THEN 1 ELSE 0 END) AS DormantInstalls,
    MAX(LastUsedDate)                                        AS LastUsedDate
FROM vw.AppUsageCorrelated
GROUP BY AppName;
GO

/* Quick sanity probe after the first batches land:
   SELECT TOP 50 AppName, UserPrincipalName, Department, FgActiveHours90, ActiveDays90, UsageState
   FROM vw.AppUsageCorrelated ORDER BY FgActiveSeconds90 DESC; */

/* ==========================================================================
   AGENT USAGE - app→licence mapping and measured right-sizing
   ========================================================================== */

IF OBJECT_ID('ref.AppProduct') IS NULL
CREATE TABLE ref.AppProduct(
    AppProductId   int IDENTITY(1,1) NOT NULL,
    SkuPartNumber  nvarchar(128) NOT NULL,
    ExePattern     nvarchar(256) NULL,   -- LIKE against vw.AppUsageCorrelated.ExePath (lower-case)
    ProductPattern nvarchar(256) NULL,   -- LIKE against AppName / ProductName
    Note           nvarchar(200) NULL,
    -- Surrogate PK: SQL Server forbids a PRIMARY KEY on nullable columns, and the
    -- pattern columns are intentionally nullable (a row may match by exe OR product,
    -- and vw.LicenceUsage keys off "IS NOT NULL"). The natural key is kept as a
    -- UNIQUE constraint instead, which permits nullable members and still dedupes seeds.
    CONSTRAINT PK_ref_AppProduct PRIMARY KEY (AppProductId),
    CONSTRAINT UQ_ref_AppProduct UNIQUE (SkuPartNumber, ExePattern, ProductPattern)
);
GO
-- A licence can have several match rows; vw.LicenceUsage attributes usage to the
-- SKU the user actually holds, so mapping one exe to multiple candidate SKUs is safe.
MERGE ref.AppProduct AS t
USING (SELECT * FROM (VALUES
    (N'VISIOCLIENT',          N'%\visio.exe',      N'%Visio%',              N'Visio Plan 2 (desktop)'),
    (N'PROJECTPROFESSIONAL',  N'%\winproj.exe',    N'%Microsoft Project%',  N'Project Plan 3 (desktop)'),
    (N'PROJECTPREMIUM',       N'%\winproj.exe',    N'%Microsoft Project%',  N'Project Plan 5 (desktop)'),
    (N'POWER_BI_PRO',         N'%\pbidesktop.exe', N'%Power BI Desktop%',   N'Power BI Pro (desktop)')
) v(SkuPartNumber, ExePattern, ProductPattern, Note)) s
ON  t.SkuPartNumber = s.SkuPartNumber
AND ISNULL(t.ExePattern,N'') = ISNULL(s.ExePattern,N'')
AND ISNULL(t.ProductPattern,N'') = ISNULL(s.ProductPattern,N'')
WHEN MATCHED THEN UPDATE SET Note = s.Note
WHEN NOT MATCHED THEN INSERT (SkuPartNumber, ExePattern, ProductPattern, Note)
    VALUES (s.SkuPartNumber, s.ExePattern, s.ProductPattern, s.Note);
GO

/* Per (holder, mapped licence): measured foreground usage of the licensed app.
   UsageVerdict = Unused (held, 0 foreground in 90d) / Light / Active.
   ReclaimableMonthly = the SKU's unit cost when Unused. */
CREATE OR ALTER VIEW vw.LicenceUsage AS
WITH mapped AS (
    SELECT c.UserPrincipalName, p.SkuPartNumber,
           SUM(c.FgActiveSeconds90) AS FgActiveSeconds90,
           MAX(c.ActiveDays90)      AS ActiveDays90,
           MAX(c.LastUsedDate)      AS LastUsedDate
    FROM vw.AppUsageCorrelated c
    JOIN ref.AppProduct p
      ON (p.ExePattern     IS NOT NULL AND c.ExePath     LIKE p.ExePattern)
      OR (p.ProductPattern IS NOT NULL AND (c.AppName LIKE p.ProductPattern OR c.ProductName LIKE p.ProductPattern))
    WHERE c.UserPrincipalName IS NOT NULL
    GROUP BY c.UserPrincipalName, p.SkuPartNumber
)
SELECT
    av.UserId, av.UserPrincipalName, av.DisplayName, av.Department, av.Country,
    av.SkuPartNumber, av.SkuName, sc.MonthlyUnitCost,
    CAST(ISNULL(m.FgActiveSeconds90,0) / 3600.0 AS decimal(12,1)) AS FgActiveHours90,
    ISNULL(m.ActiveDays90,0) AS ActiveDays90, m.LastUsedDate,
    CASE WHEN ISNULL(m.FgActiveSeconds90,0) >= 3600 THEN 'Active'   -- >=1h foreground in 90d
         WHEN ISNULL(m.FgActiveSeconds90,0) >  0    THEN 'Light'
         ELSE 'Unused' END AS UsageVerdict,
    CAST(CASE WHEN ISNULL(m.FgActiveSeconds90,0) = 0 THEN sc.MonthlyUnitCost ELSE 0 END AS decimal(12,2)) AS ReclaimableMonthly
FROM score.AssignmentVerdict av
LEFT JOIN ref.SkuCost sc ON sc.SkuPartNumber = av.SkuPartNumber
LEFT JOIN mapped m ON m.UserPrincipalName = av.UserPrincipalName AND m.SkuPartNumber = av.SkuPartNumber
WHERE EXISTS (SELECT 1 FROM ref.AppProduct ap WHERE ap.SkuPartNumber = av.SkuPartNumber);
GO

/* Per-SKU rollup of measured licence waste. */
CREATE OR ALTER VIEW vw.LicenceUsageSummary AS
SELECT SkuPartNumber, MAX(SkuName) AS SkuName,
       COUNT(*)                                                   AS Holders,
       SUM(CASE WHEN UsageVerdict = 'Unused' THEN 1 ELSE 0 END)   AS UnusedHolders,
       SUM(CASE WHEN UsageVerdict = 'Active' THEN 1 ELSE 0 END)   AS ActiveHolders,
       CAST(SUM(ReclaimableMonthly) AS decimal(14,2))             AS ReclaimableMonthly
FROM vw.LicenceUsage
GROUP BY SkuPartNumber;
GO

/* Probe once the agent has reported for a few days:
   SELECT * FROM vw.LicenceUsageSummary ORDER BY ReclaimableMonthly DESC;
   SELECT TOP 50 DisplayName, Department, SkuName, FgActiveHours90, UsageVerdict, ReclaimableMonthly
   FROM vw.LicenceUsage WHERE UsageVerdict='Unused' ORDER BY ReclaimableMonthly DESC; */

/* ==========================================================================
   AGENT USAGE - per-user (SID-resolved) usage signal for the engine
   ========================================================================== */

-- 2) Per-user usage, resolved by real SID first, device primary user as fallback.
CREATE OR ALTER VIEW vw.AppUsageByUser90 AS
WITH usage AS (
    SELECT au.UserSid, au.MachineName, au.ExePath, au.[Date], au.ForegroundActiveSeconds
    FROM fact.AppUsage au
    WHERE au.[Date] >= CONVERT(date, DATEADD(day, -90, sysutcdatetime()))
),
resolved AS (
    SELECT
        COALESCE(us.UserId, dv.UserId) AS UserId,   -- real user (SID) wins; device primary user only as fallback
        u.ExePath, u.[Date], u.ForegroundActiveSeconds
    FROM usage u
    LEFT JOIN dim.[User] us
           ON us.SecurityIdentifier = u.UserSid
           OR us.OnPremisesSecurityIdentifier = u.UserSid
    LEFT JOIN dim.Device dv
           ON dv.DeviceName = u.MachineName
    WHERE COALESCE(us.UserId, dv.UserId) IS NOT NULL
),
cov AS (
    SELECT UserId, COUNT(DISTINCT [Date]) AS CoverageDays
    FROM resolved
    GROUP BY UserId
)
SELECT
    r.UserId,
    r.ExePath,
    SUM(r.ForegroundActiveSeconds) AS FgActiveSeconds,
    COUNT(DISTINCT r.[Date])       AS ActiveDays,
    MAX(r.[Date])                  AS LastDay,
    MAX(c.CoverageDays)            AS CoverageDays
FROM resolved r
JOIN cov c ON c.UserId = r.UserId
GROUP BY r.UserId, r.ExePath;
GO

/* ==========================================================================
   GOVERNANCE - direct-assignment anomalies and per-device install visibility
   ========================================================================== */

-- ---- item 9: direct (non-group) licence assignments -----------------------
CREATE OR ALTER VIEW vw.DirectAssignments AS
SELECT
    la.UserId,
    u.UserPrincipalName,
    u.DisplayName,
    u.Department,
    u.AccountEnabled,
    la.SkuPartNumber,
    COALESCE(NULLIF(sc.DisplayName, N''), s.DisplayName, la.SkuPartNumber) AS SkuName,
    sc.MonthlyUnitCost,
    sc.Currency,
    la.State,
    la.LastUpdatedDateTime
FROM fact.LicenseAssignment la
LEFT JOIN dim.[User]  u  ON u.UserId       = la.UserId
LEFT JOIN dim.Sku     s  ON s.SkuId        = la.SkuId
LEFT JOIN ref.SkuCost sc ON sc.SkuPartNumber = la.SkuPartNumber
WHERE la.AssignedDirectly = 1
  -- Free / viral / trial plans cost nothing: a direct assignment of one is not a
  -- governance problem and only buries the real deviations. This mirrors
  -- ScoringEngine.IsFreeSku (€0 price on file, "(free)" display name, free/viral
  -- part-number patterns) - keep the two in sync.
  AND NOT (
        ISNULL(sc.MonthlyUnitCost, -1) = 0
     OR COALESCE(NULLIF(sc.DisplayName, N''), s.DisplayName, N'') LIKE N'%(free)%'
     OR la.SkuPartNumber LIKE N'%FREE%'
     OR la.SkuPartNumber LIKE N'%VIRAL%'
     OR la.SkuPartNumber LIKE N'%TRIAL%'
     OR la.SkuPartNumber LIKE N'%EXPLORATORY%'
     OR la.SkuPartNumber = N'POWER_BI_STANDARD'
  );
GO

CREATE OR ALTER VIEW vw.DirectAssignmentSummary AS
SELECT
    COALESCE(NULLIF(Department, N''), N'Unattributed') AS Department,
    COUNT(*)               AS DirectAssignments,
    COUNT(DISTINCT UserId) AS Users,
    CAST(SUM(ISNULL(MonthlyUnitCost, 0)) AS decimal(14,2)) AS MonthlyCost
FROM vw.DirectAssignments
GROUP BY COALESCE(NULLIF(Department, N''), N'Unattributed');
GO

-- ---- item 4: per-device / per-user application install visibility ---------
CREATE OR ALTER VIEW vw.AppInstall AS
SELECT
    ai.DisplayName,
    ai.DeviceName,
    ai.UserPrincipalName,
    u.DisplayName AS UserDisplayName,
    u.Department,
    d.OperatingSystem,
    d.OsVersion,
    d.ComplianceState,
    -- Intune's detected-app inventory is per (app, version): each version has its
    -- own AppId, which fact.AppInstall carries - so the installed VERSION per
    -- device is recoverable by joining back. Lets the app drill filter the device
    -- list by version.
    da.[Version]  AS AppVersion
FROM fact.AppInstall ai
LEFT JOIN dim.Device   d ON d.DeviceName = ai.DeviceName
LEFT JOIN dim.[User]   u ON u.UserPrincipalName = ai.UserPrincipalName
LEFT JOIN fact.DetectedApp da ON da.AppId = ai.AppId
-- Windows-only: dim.Device holds only Windows endpoints (item 6), so requiring a
-- device match keeps installs Windows-scoped and attaches OS/compliance/owner.
WHERE d.DeviceId IS NOT NULL;
GO

/* ==========================================================================
   DEFENDER - endpoint software inventory
   ========================================================================== */

/* ---- Org-wide software inventory (one row per title) --------------------- */
IF OBJECT_ID('fact.SoftwareInventory') IS NULL
CREATE TABLE fact.SoftwareInventory
(
    SoftwareInventoryId bigint IDENTITY(1,1) CONSTRAINT PK_fact_SoftwareInventory PRIMARY KEY,
    SoftwareId          nvarchar(256) NOT NULL,          -- e.g. "microsoft-_-edge"
    Name                nvarchar(256) NULL,
    Vendor              nvarchar(256) NULL,
    Weaknesses          bigint        NULL,
    PublicExploit       bit           NULL,
    ActiveAlert         bit           NULL,
    ExposedMachines     bigint        NULL,              -- machines carrying the title (install footprint)
    ImpactScore         float         NULL,
    Source              nvarchar(64)  NULL,
    RunId               nvarchar(40)  NULL,
    SnapshotUtc         datetime2(3)  NULL,
    LoadedUtc           datetime2(3)  NOT NULL CONSTRAINT DF_fact_SwInv_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_SoftwareInventory_SoftwareId')
    CREATE INDEX IX_fact_SoftwareInventory_SoftwareId ON fact.SoftwareInventory (SoftwareId);
GO

/* ---- Per-device expansion (optional; one row per software+device) -------- */
IF OBJECT_ID('fact.SoftwareInstall') IS NULL
CREATE TABLE fact.SoftwareInstall
(
    SoftwareInstallId bigint IDENTITY(1,1) CONSTRAINT PK_fact_SoftwareInstall PRIMARY KEY,
    SoftwareId        nvarchar(256) NOT NULL,
    SoftwareName      nvarchar(256) NULL,
    Vendor            nvarchar(256) NULL,
    MachineId         nvarchar(128) NULL,
    ComputerDnsName   nvarchar(256) NULL,
    OsPlatform        nvarchar(64)  NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    SnapshotUtc       datetime2(3)  NULL,
    LoadedUtc         datetime2(3)  NOT NULL CONSTRAINT DF_fact_SwInst_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_SoftwareInstall_SoftwareId')
    CREATE INDEX IX_fact_SoftwareInstall_SoftwareId ON fact.SoftwareInstall (SoftwareId);
GO

/* ---- Convenience view: title + actual installed-device count ------------- */
CREATE OR ALTER VIEW vw.MdeSoftware AS
SELECT
    si.SoftwareId,
    si.Name,
    si.Vendor,
    si.ExposedMachines,
    si.Weaknesses,
    si.PublicExploit,
    si.ActiveAlert,
    si.ImpactScore,
    InstalledDeviceCount = COUNT(DISTINCT inst.MachineId),
    si.SnapshotUtc
FROM fact.SoftwareInventory si
LEFT JOIN fact.SoftwareInstall inst ON inst.SoftwareId = si.SoftwareId
GROUP BY
    si.SoftwareId, si.Name, si.Vendor, si.ExposedMachines, si.Weaknesses,
    si.PublicExploit, si.ActiveAlert, si.ImpactScore, si.SnapshotUtc;
GO

/* ==========================================================================
   DEFENDER - fused per-user install evidence & coverage
   ========================================================================== */

/* ---- One row per (user, installed app, source). Device multiplicity is
       deduplicated by the reader; names stay raw for fragment matching.
       MDE machine names arrive as DNS names (host.domain.tld) - matched to
       Intune's dim.Device.DeviceName on the bare lowercase host. ------------ */
CREATE OR ALTER VIEW vw.SoftwareInstallByUser AS
WITH dev AS (
    SELECT DeviceId,
           LOWER(DeviceName) AS DeviceNameNorm,
           UserId
    FROM dim.Device
    WHERE UserId IS NOT NULL AND DeviceName IS NOT NULL
),
intune AS (
    SELECT d.UserId, ai.DisplayName AS AppName, CAST('intune' AS nvarchar(8)) AS SourceSystem, d.DeviceId
    FROM fact.AppInstall ai
    JOIN dev d ON d.DeviceId = ai.DeviceId
    WHERE ai.DisplayName IS NOT NULL
),
mde AS (
    SELECT d.UserId, si.SoftwareName AS AppName, CAST('mde' AS nvarchar(8)) AS SourceSystem, d.DeviceId
    FROM fact.SoftwareInstall si
    JOIN dev d
      ON d.DeviceNameNorm = LOWER(CASE WHEN CHARINDEX('.', si.ComputerDnsName) > 0
                                       THEN LEFT(si.ComputerDnsName, CHARINDEX('.', si.ComputerDnsName) - 1)
                                       ELSE si.ComputerDnsName END)
    WHERE si.SoftwareName IS NOT NULL
)
SELECT UserId, AppName, SourceSystem, DeviceId FROM intune
UNION ALL
SELECT UserId, AppName, SourceSystem, DeviceId FROM mde;
GO

/* ---- Per-user inventory visibility. ABSENCE of an install row is only
       evidence when at least one inventory actually covered the user's
       devices - the engine's "absence of telemetry never auto-reclaims"
       doctrine applied to install data. -------------------------------------- */
CREATE OR ALTER VIEW vw.UserInstallCoverage AS
WITH dev AS (
    SELECT DeviceId, LOWER(DeviceName) AS DeviceNameNorm, UserId
    FROM dim.Device
    WHERE UserId IS NOT NULL AND DeviceName IS NOT NULL
),
cov AS (
    SELECT d.UserId, d.DeviceId,
        CASE WHEN EXISTS (SELECT 1 FROM fact.AppInstall ai WHERE ai.DeviceId = d.DeviceId)
             THEN 1 ELSE 0 END AS SeenIntune,
        CASE WHEN EXISTS (SELECT 1 FROM fact.SoftwareInstall si
                          WHERE LOWER(CASE WHEN CHARINDEX('.', si.ComputerDnsName) > 0
                                           THEN LEFT(si.ComputerDnsName, CHARINDEX('.', si.ComputerDnsName) - 1)
                                           ELSE si.ComputerDnsName END) = d.DeviceNameNorm)
             THEN 1 ELSE 0 END AS SeenMde
    FROM dev d
)
SELECT UserId,
       COUNT(*)        AS ManagedDeviceCount,
       SUM(SeenIntune) AS IntuneSeenDeviceCount,
       SUM(SeenMde)    AS MdeSeenDeviceCount
FROM cov
GROUP BY UserId;
GO

/* ==========================================================================
   DEFENDER - Advanced Hunting process-run telemetry
   ========================================================================== */

IF OBJECT_ID('fact.SoftwareRun') IS NULL
CREATE TABLE fact.SoftwareRun
(
    SoftwareRunId bigint IDENTITY(1,1) CONSTRAINT PK_fact_SoftwareRun PRIMARY KEY,
    FileName      nvarchar(256) NOT NULL,
    DeviceId      nvarchar(128) NULL,          -- MDE machine id
    DeviceName    nvarchar(256) NULL,
    AccountUpn    nvarchar(256) NULL,
    LastRunUtc    datetime2(3)  NULL,
    RunCount      bigint        NULL,
    RunDays       int           NULL,          -- distinct days the exe started in the window
    Source        nvarchar(64)  NULL,
    RunId         nvarchar(40)  NULL,
    SnapshotUtc   datetime2(3)  NULL,
    LoadedUtc     datetime2(3)  NOT NULL CONSTRAINT DF_fact_SwRun_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_SoftwareRun_FileName')
    CREATE INDEX IX_fact_SoftwareRun_FileName ON fact.SoftwareRun (FileName);
GO

/* ---- Attribute runs to Entra users. AccountUpn (who actually ran it) wins;
       fall back to the device's Intune primary user for local accounts.
       MDE DeviceName arrives as a DNS name - matched on the bare host. -------- */
CREATE OR ALTER VIEW vw.SoftwareRunByUser AS
WITH dev AS (
    SELECT DeviceId, LOWER(DeviceName) AS DeviceNameNorm, UserId
    FROM dim.Device
    WHERE UserId IS NOT NULL AND DeviceName IS NOT NULL
)
SELECT
    COALESCE(u.UserId, d.UserId) AS UserId,
    sr.FileName,
    MAX(sr.LastRunUtc)           AS LastRunUtc,
    SUM(ISNULL(sr.RunCount, 0))  AS RunCount,
    MAX(ISNULL(sr.RunDays, 0))   AS RunDays
FROM fact.SoftwareRun sr
LEFT JOIN dim.[User] u
       ON u.UserPrincipalName = sr.AccountUpn
LEFT JOIN dev d
       ON d.DeviceNameNorm = LOWER(CASE WHEN CHARINDEX('.', sr.DeviceName) > 0
                                        THEN LEFT(sr.DeviceName, CHARINDEX('.', sr.DeviceName) - 1)
                                        ELSE sr.DeviceName END)
WHERE COALESCE(u.UserId, d.UserId) IS NOT NULL
GROUP BY COALESCE(u.UserId, d.UserId), sr.FileName;
GO

/* ==========================================================================
   USAGE BREADTH - per-app sign-ins, M365 Apps usage, deleted-but-licensed users
   ========================================================================== */

/* ---- Entra per-application sign-ins -------------------------------------- */
IF OBJECT_ID('fact.AppSignIn') IS NULL
CREATE TABLE fact.AppSignIn
(
    AppSignInId       bigint IDENTITY(1,1) CONSTRAINT PK_fact_AppSignIn PRIMARY KEY,
    UserId            nvarchar(64)  NULL,
    UserPrincipalName nvarchar(256) NULL,
    AppId             nvarchar(64)  NULL,
    AppDisplayName    nvarchar(256) NULL,
    LastSignInUtc     datetime2(3)  NULL,
    SignInCount       bigint        NULL,
    WindowDays        int           NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    SnapshotUtc       datetime2(3)  NULL,
    LoadedUtc         datetime2(3)  NOT NULL CONSTRAINT DF_fact_AppSignIn_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_AppSignIn_User')
    CREATE INDEX IX_fact_AppSignIn_User ON fact.AppSignIn (UserId, AppId);
GO

CREATE OR ALTER VIEW vw.AppSignInByUser AS
SELECT UserId, AppId,
       MAX(AppDisplayName)        AS AppDisplayName,
       MAX(LastSignInUtc)         AS LastSignInUtc,
       SUM(ISNULL(SignInCount,0)) AS SignInCount
FROM fact.AppSignIn
WHERE UserId IS NOT NULL AND AppId IS NOT NULL
GROUP BY UserId, AppId;
GO

/* ---- Microsoft 365 Apps usage (per-user, per-app last activity) ---------- */
IF OBJECT_ID('fact.M365AppUsage') IS NULL
CREATE TABLE fact.M365AppUsage
(
    M365AppUsageId             bigint IDENTITY(1,1) CONSTRAINT PK_fact_M365AppUsage PRIMARY KEY,
    UserPrincipalName          nvarchar(256) NULL,
    DisplayName                nvarchar(256) NULL,
    Concealed                  bit           NULL,
    ReportRefreshDate          datetime2(3)  NULL,
    ReportPeriodDays           nvarchar(8)   NULL,
    IsDeleted                  bit           NULL,
    WordLastActivityDate       datetime2(3)  NULL,
    ExcelLastActivityDate      datetime2(3)  NULL,
    PowerPointLastActivityDate datetime2(3)  NULL,
    OutlookLastActivityDate    datetime2(3)  NULL,
    OneNoteLastActivityDate    datetime2(3)  NULL,
    TeamsLastActivityDate      datetime2(3)  NULL,
    LastActivityAnyDate        datetime2(3)  NULL,
    UsedWeb                    bit           NULL,
    UsedMobile                 bit           NULL,
    UsedWindows                bit           NULL,
    UsedMac                    bit           NULL,
    Source                     nvarchar(64)  NULL,
    RunId                      nvarchar(40)  NULL,
    SnapshotUtc                datetime2(3)  NULL,
    LoadedUtc                  datetime2(3)  NOT NULL CONSTRAINT DF_fact_M365AppUsage_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_M365AppUsage_Upn')
    CREATE INDEX IX_fact_M365AppUsage_Upn ON fact.M365AppUsage (UserPrincipalName);
GO

/* Resolve UPN -> UserId so scoring can key on UserId like every other signal. */
CREATE OR ALTER VIEW vw.M365AppUsageByUser AS
SELECT
    u.UserId,
    m.WordLastActivityDate, m.ExcelLastActivityDate, m.PowerPointLastActivityDate,
    m.OutlookLastActivityDate, m.OneNoteLastActivityDate, m.TeamsLastActivityDate,
    m.LastActivityAnyDate,
    m.UsedWeb, m.UsedMobile, m.UsedWindows, m.UsedMac, m.Concealed
FROM fact.M365AppUsage m
JOIN dim.[User] u ON u.UserPrincipalName = m.UserPrincipalName
WHERE m.Concealed = 0;
GO

/* ---- Deleted-but-licensed users (still billing) -------------------------- */
IF OBJECT_ID('fact.DeletedUserLicense') IS NULL
CREATE TABLE fact.DeletedUserLicense
(
    DeletedUserLicenseId bigint IDENTITY(1,1) CONSTRAINT PK_fact_DeletedUserLicense PRIMARY KEY,
    UserId            nvarchar(64)  NOT NULL,
    UserPrincipalName nvarchar(256) NULL,
    DisplayName       nvarchar(256) NULL,
    DeletedDateTime   datetime2(3)  NULL,
    SkuId             nvarchar(64)  NOT NULL,
    Source            nvarchar(64)  NULL,
    RunId             nvarchar(40)  NULL,
    SnapshotUtc       datetime2(3)  NULL,
    LoadedUtc         datetime2(3)  NOT NULL CONSTRAINT DF_fact_DelUserLic_Loaded DEFAULT sysutcdatetime()
);
GO

/* Deleted-user licences priced out - immediate, unambiguous reclaim list. */
CREATE OR ALTER VIEW vw.DeletedUserLicense AS
SELECT
    d.UserId, d.UserPrincipalName, d.DisplayName, d.DeletedDateTime,
    d.SkuId,
    sku.SkuPartNumber,
    c.MonthlyUnitCost AS UnitCost, c.Currency,
    DaysSinceDeleted = DATEDIFF(DAY, d.DeletedDateTime, sysutcdatetime())
FROM fact.DeletedUserLicense d
LEFT JOIN dim.Sku     sku ON sku.SkuId = d.SkuId
LEFT JOIN ref.SkuCost c   ON c.SkuPartNumber = sku.SkuPartNumber;   -- ref.SkuCost is keyed by part number, not SkuId
GO

/* ==========================================================================
   COPILOT - Microsoft 365 Copilot per-user usage
   ========================================================================== */

IF OBJECT_ID('fact.CopilotUsage') IS NULL
CREATE TABLE fact.CopilotUsage
(
    CopilotUsageId             bigint IDENTITY(1,1) CONSTRAINT PK_fact_CopilotUsage PRIMARY KEY,
    UserPrincipalName          nvarchar(256) NULL,
    DisplayName                nvarchar(256) NULL,
    Concealed                  bit           NULL,
    ReportRefreshDate          datetime2(3)  NULL,
    ReportPeriodDays           nvarchar(8)   NULL,
    LastActivityDate           datetime2(3)  NULL,
    TeamsLastActivityDate      datetime2(3)  NULL,
    WordLastActivityDate       datetime2(3)  NULL,
    ExcelLastActivityDate      datetime2(3)  NULL,
    PowerPointLastActivityDate datetime2(3)  NULL,
    OutlookLastActivityDate    datetime2(3)  NULL,
    OneNoteLastActivityDate    datetime2(3)  NULL,
    LoopLastActivityDate       datetime2(3)  NULL,
    ChatLastActivityDate       datetime2(3)  NULL,
    LastActivityAnyDate        datetime2(3)  NULL,
    Source                     nvarchar(64)  NULL,
    RunId                      nvarchar(40)  NULL,
    SnapshotUtc                datetime2(3)  NULL,
    LoadedUtc                  datetime2(3)  NOT NULL CONSTRAINT DF_fact_CopilotUsage_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_CopilotUsage_Upn')
    CREATE INDEX IX_fact_CopilotUsage_Upn ON fact.CopilotUsage (UserPrincipalName);
GO

/* Resolve UPN -> UserId so scoring can key on UserId like every other signal.
   Concealed rows are dropped (anonymized report = no usable per-user signal). */
CREATE OR ALTER VIEW vw.CopilotUsageByUser AS
SELECT
    u.UserId,
    c.LastActivityAnyDate,
    c.TeamsLastActivityDate, c.WordLastActivityDate, c.ExcelLastActivityDate,
    c.PowerPointLastActivityDate, c.OutlookLastActivityDate, c.OneNoteLastActivityDate,
    c.LoopLastActivityDate, c.ChatLastActivityDate, c.Concealed
FROM fact.CopilotUsage c
JOIN dim.[User] u ON u.UserPrincipalName = c.UserPrincipalName
WHERE c.Concealed = 0;
GO

/* ==========================================================================
   v2 SIGNALS - mailbox purpose, PSTN calling, auth-method registration
   ========================================================================== */

/* ---- Mailbox settings: the DETERMINISTIC shared/room/equipment discriminator ----
   Replaces the name-pattern heuristic for shared-mailbox detection. A licensed
   'shared' mailbox under 50 GB usually needs no license at all - the classic
   compliance trap, now evidence-backed instead of guessed. -------------------- */
IF OBJECT_ID('fact.Mailbox') IS NULL
CREATE TABLE fact.Mailbox
(
    MailboxId              bigint IDENTITY(1,1) CONSTRAINT PK_fact_Mailbox PRIMARY KEY,
    UserId                 nvarchar(64)  NOT NULL,
    UserPrincipalName      nvarchar(256) NULL,
    UserPurpose            nvarchar(32)  NULL,   -- user | shared | room | equipment | linked | others
    AutomaticRepliesStatus nvarchar(32)  NULL,   -- a permanent OOF corroborates long absence
    TimeZone               nvarchar(64)  NULL,
    Source                 nvarchar(64)  NULL,
    RunId                  nvarchar(40)  NULL,
    SnapshotUtc            datetime2(3)  NULL,
    LoadedUtc              datetime2(3)  NOT NULL CONSTRAINT DF_fact_Mailbox_Loaded DEFAULT sysutcdatetime()
);
GO
IF IndexProperty(OBJECT_ID('fact.Mailbox'),'IX_Mailbox_User','IndexID') IS NULL
    CREATE INDEX IX_Mailbox_User ON fact.Mailbox(UserId) INCLUDE (UserPurpose, AutomaticRepliesStatus);
GO

/* ---- Teams PSTN calling usage (real call-detail records, per user) ----------
   The authoritative Teams Phone signal: getPstnCalls returns actual PSTN legs,
   so "zero calls in the window" is hard evidence for an MCOEV seat - much
   stronger than the Teams activity report's coarse CallCount (which also counts
   VoIP). ---------------------------------------------------------------------- */
IF OBJECT_ID('fact.PstnUsage') IS NULL
CREATE TABLE fact.PstnUsage
(
    PstnUsageId          bigint IDENTITY(1,1) CONSTRAINT PK_fact_PstnUsage PRIMARY KEY,
    UserId               nvarchar(64)  NULL,
    UserPrincipalName    nvarchar(256) NULL,
    CallCount            int           NULL,
    TotalDurationSeconds bigint        NULL,
    LastCallDateTime     datetime2(3)  NULL,
    WindowDays           int           NULL,
    Source               nvarchar(64)  NULL,
    RunId                nvarchar(40)  NULL,
    SnapshotUtc          datetime2(3)  NULL,
    LoadedUtc            datetime2(3)  NOT NULL CONSTRAINT DF_fact_PstnUsage_Loaded DEFAULT sysutcdatetime()
);
GO
IF IndexProperty(OBJECT_ID('fact.PstnUsage'),'IX_PstnUsage_User','IndexID') IS NULL
    CREATE INDEX IX_PstnUsage_User ON fact.PstnUsage(UserId) INCLUDE (CallCount, TotalDurationSeconds, LastCallDateTime, WindowDays);
GO

/* Resolve to UserId (rows may arrive keyed by UPN only). */
CREATE OR ALTER VIEW vw.PstnUsageByUser AS
SELECT
    COALESCE(p.UserId, u.UserId)       AS UserId,
    SUM(ISNULL(p.CallCount, 0))        AS CallCount,
    SUM(ISNULL(p.TotalDurationSeconds, 0)) AS TotalDurationSeconds,
    MAX(p.LastCallDateTime)            AS LastCallDateTime,
    MAX(ISNULL(p.WindowDays, 0))       AS WindowDays
FROM fact.PstnUsage p
LEFT JOIN dim.[User] u ON u.UserPrincipalName = p.UserPrincipalName
WHERE COALESCE(p.UserId, u.UserId) IS NOT NULL
GROUP BY COALESCE(p.UserId, u.UserId);
GO

/* ---- Authentication-method registration --------------------------------------
   A holder who never registered MFA/SSPR was likely never onboarded at all -
   positive corroboration for NEVER_ACTIVE (never a sole reclaim trigger). ------ */
IF OBJECT_ID('fact.AuthMethodRegistration') IS NULL
CREATE TABLE fact.AuthMethodRegistration
(
    AuthMethodId          bigint IDENTITY(1,1) CONSTRAINT PK_fact_AuthMethod PRIMARY KEY,
    UserId                nvarchar(64)  NOT NULL,
    UserPrincipalName     nvarchar(256) NULL,
    IsAdmin               bit           NULL,
    IsMfaRegistered       bit           NULL,
    IsMfaCapable          bit           NULL,
    IsPasswordlessCapable bit           NULL,
    IsSsprRegistered      bit           NULL,
    IsSsprEnabled         bit           NULL,
    IsSsprCapable         bit           NULL,
    MethodsRegistered     nvarchar(1024) NULL,  -- comma-joined method list
    DefaultMethod         nvarchar(64)  NULL,
    LastUpdatedDateTime   datetime2(3)  NULL,
    Source                nvarchar(64)  NULL,
    RunId                 nvarchar(40)  NULL,
    SnapshotUtc           datetime2(3)  NULL,
    LoadedUtc             datetime2(3)  NOT NULL CONSTRAINT DF_fact_AuthMethod_Loaded DEFAULT sysutcdatetime()
);
GO
IF IndexProperty(OBJECT_ID('fact.AuthMethodRegistration'),'IX_AuthMethod_User','IndexID') IS NULL
    CREATE INDEX IX_AuthMethod_User ON fact.AuthMethodRegistration(UserId) INCLUDE (IsMfaRegistered, IsAdmin, LastUpdatedDateTime);
GO

/* ---- Copilot depth-of-use (how many host apps, not just "used") --------------
   A Copilot seat living in one surface only (say, occasional Chat) is a right-size
   conversation; the per-app activity dates are already in fact.CopilotUsage. ---- */
CREATE OR ALTER VIEW vw.CopilotDepthByUser AS
SELECT
    u.UserId,
    c.LastActivityAnyDate,
    (CASE WHEN c.TeamsLastActivityDate      IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.WordLastActivityDate       IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.ExcelLastActivityDate      IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.PowerPointLastActivityDate IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.OutlookLastActivityDate    IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.OneNoteLastActivityDate    IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.LoopLastActivityDate       IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN c.ChatLastActivityDate       IS NOT NULL THEN 1 ELSE 0 END) AS SurfacesUsed
FROM fact.CopilotUsage c
JOIN dim.[User] u ON u.UserPrincipalName = c.UserPrincipalName
WHERE c.Concealed = 0;
GO

/* ==========================================================================
   ENRICHMENT - Teams, service detail, app health, mobile apps, enterprise-app sign-ins
   ========================================================================== */

/* ---- Microsoft Teams user activity (per-user message/call/meeting counts) -- */
IF OBJECT_ID('fact.TeamsActivity') IS NULL
CREATE TABLE fact.TeamsActivity
(
    TeamsActivityId         bigint IDENTITY(1,1) CONSTRAINT PK_fact_TeamsActivity PRIMARY KEY,
    UserPrincipalName       nvarchar(256) NULL,
    Concealed               bit           NULL,
    ReportRefreshDate       datetime2(3)  NULL,
    ReportPeriodDays        nvarchar(8)   NULL,
    LastActivityDate        datetime2(3)  NULL,
    TeamChatMessageCount    int           NULL,
    PrivateChatMessageCount int           NULL,
    CallCount               int           NULL,
    MeetingCount            int           NULL,
    Source                  nvarchar(64)  NULL,
    RunId                   nvarchar(40)  NULL,
    SnapshotUtc             datetime2(3)  NULL,
    LoadedUtc               datetime2(3)  NOT NULL CONSTRAINT DF_fact_TeamsActivity_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_TeamsActivity_Upn')
    CREATE INDEX IX_fact_TeamsActivity_Upn ON fact.TeamsActivity (UserPrincipalName);
GO

/* Resolve UPN -> UserId; one row per user (MAX collapses any duplicate UPN rows). */
CREATE OR ALTER VIEW vw.TeamsActivityByUser AS
SELECT u.UserId,
       MAX(t.LastActivityDate)                  AS LastActivityDate,
       MAX(ISNULL(t.CallCount,0))               AS CallCount,
       MAX(ISNULL(t.MeetingCount,0))            AS MeetingCount,
       MAX(ISNULL(t.TeamChatMessageCount,0))    AS TeamChatMessageCount,
       MAX(ISNULL(t.PrivateChatMessageCount,0)) AS PrivateChatMessageCount
FROM fact.TeamsActivity t
JOIN dim.[User] u ON u.UserPrincipalName = t.UserPrincipalName
WHERE t.Concealed = 0
GROUP BY u.UserId;
GO

/* ---- Consolidated M365 service detail (mailbox / OneDrive / SharePoint) ----- */
IF OBJECT_ID('fact.ServiceActivityDetail') IS NULL
CREATE TABLE fact.ServiceActivityDetail
(
    ServiceActivityId         bigint IDENTITY(1,1) CONSTRAINT PK_fact_ServiceActivityDetail PRIMARY KEY,
    Service                   nvarchar(16)  NULL,   -- 'mailbox' | 'onedrive' | 'sharepoint'
    UserPrincipalName         nvarchar(256) NULL,
    Concealed                 bit           NULL,
    ReportRefreshDate         datetime2(3)  NULL,
    ReportPeriodDays          nvarchar(8)   NULL,
    LastActivityDate          datetime2(3)  NULL,
    ViewedOrEditedFileCount   int           NULL,
    SyncedFileCount           int           NULL,
    SharedInternallyFileCount int           NULL,
    SharedExternallyFileCount int           NULL,
    VisitedPageCount          int           NULL,
    StorageUsedBytes          bigint        NULL,
    ItemCount                 int           NULL,
    Source                    nvarchar(64)  NULL,
    RunId                     nvarchar(40)  NULL,
    SnapshotUtc               datetime2(3)  NULL,
    LoadedUtc                 datetime2(3)  NOT NULL CONSTRAINT DF_fact_ServiceActivityDetail_Loaded DEFAULT sysutcdatetime()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_fact_ServiceActivityDetail_Upn')
    CREATE INDEX IX_fact_ServiceActivityDetail_Upn ON fact.ServiceActivityDetail (UserPrincipalName, Service);
GO

/* Per-user file activity (OneDrive + SharePoint): "has a licence, edited zero files". */
CREATE OR ALTER VIEW vw.FileActivityByUser AS
SELECT u.UserId,
       MAX(d.LastActivityDate)                  AS LastActivityDate,
       MAX(ISNULL(d.ViewedOrEditedFileCount,0)) AS ViewedOrEditedFileCount
FROM fact.ServiceActivityDetail d
JOIN dim.[User] u ON u.UserPrincipalName = d.UserPrincipalName
WHERE d.Concealed = 0 AND d.Service IN ('onedrive','sharepoint')
GROUP BY u.UserId;
GO

/* ---- Intune Endpoint Analytics App Health (tenant/app-level) --------------- */
IF OBJECT_ID('fact.AppHealth') IS NULL
CREATE TABLE fact.AppHealth
(
    AppHealthId                bigint IDENTITY(1,1) CONSTRAINT PK_fact_AppHealth PRIMARY KEY,
    AppName                    nvarchar(256) NULL,
    AppDisplayName             nvarchar(256) NULL,
    AppPublisher               nvarchar(256) NULL,
    AppUsageDuration           bigint        NULL,
    ActiveDeviceCount          int           NULL,
    AppCrashCount              int           NULL,
    AppHangCount               int           NULL,
    AppHealthScore             float         NULL,
    MeanTimeToFailureInMinutes float         NULL,
    Source                     nvarchar(64)  NULL,
    RunId                      nvarchar(40)  NULL,
    SnapshotUtc                datetime2(3)  NULL,
    LoadedUtc                  datetime2(3)  NOT NULL CONSTRAINT DF_fact_AppHealth_Loaded DEFAULT sysutcdatetime()
);
GO
CREATE OR ALTER VIEW vw.AppHealth AS
SELECT AppName, AppDisplayName, AppPublisher, AppUsageDuration, ActiveDeviceCount,
       AppCrashCount, AppHangCount, AppHealthScore, MeanTimeToFailureInMinutes
FROM fact.AppHealth;
GO

/* ---- Intune managed-app deployment status (per-app) ------------------------ */
IF OBJECT_ID('fact.MobileAppInstall') IS NULL
CREATE TABLE fact.MobileAppInstall
(
    MobileAppInstallId        bigint IDENTITY(1,1) CONSTRAINT PK_fact_MobileAppInstall PRIMARY KEY,
    AppId                     nvarchar(64)  NULL,
    DisplayName               nvarchar(256) NULL,
    Publisher                 nvarchar(256) NULL,
    Platform                  nvarchar(64)  NULL,
    InstalledDeviceCount      int           NULL,
    FailedDeviceCount         int           NULL,
    NotInstalledDeviceCount   int           NULL,
    PendingInstallDeviceCount int           NULL,
    Source                    nvarchar(64)  NULL,
    RunId                     nvarchar(40)  NULL,
    SnapshotUtc               datetime2(3)  NULL,
    LoadedUtc                 datetime2(3)  NOT NULL CONSTRAINT DF_fact_MobileAppInstall_Loaded DEFAULT sysutcdatetime()
);
GO
CREATE OR ALTER VIEW vw.MobileAppInstall AS
SELECT AppId, DisplayName, Publisher, Platform,
       InstalledDeviceCount, FailedDeviceCount, NotInstalledDeviceCount, PendingInstallDeviceCount
FROM fact.MobileAppInstall;
GO

/* ---- Entra enterprise-app (service principal) sign-in activity (per-app) ---- */
IF OBJECT_ID('fact.ServicePrincipalSignIn') IS NULL
CREATE TABLE fact.ServicePrincipalSignIn
(
    ServicePrincipalSignInId bigint IDENTITY(1,1) CONSTRAINT PK_fact_SpSignIn PRIMARY KEY,
    AppId                    nvarchar(64)  NULL,
    DisplayName              nvarchar(256) NULL,
    LastSignInUtc            datetime2(3)  NULL,
    Source                   nvarchar(64)  NULL,
    RunId                    nvarchar(40)  NULL,
    SnapshotUtc              datetime2(3)  NULL,
    LoadedUtc                datetime2(3)  NOT NULL CONSTRAINT DF_fact_SpSignIn_Loaded DEFAULT sysutcdatetime()
);
GO
CREATE OR ALTER VIEW vw.ServicePrincipalSignIn AS
SELECT AppId, DisplayName, LastSignInUtc FROM fact.ServicePrincipalSignIn;
GO

/* ==========================================================================
   WATCHED APPS - SKU-independent paid-software install/idle tracking
   ========================================================================== */

IF OBJECT_ID('ref.WatchedApp') IS NULL
CREATE TABLE ref.WatchedApp
(
    WatchedAppId    int IDENTITY(1,1) CONSTRAINT PK_ref_WatchedApp PRIMARY KEY,
    Name            nvarchar(128) NOT NULL,                          -- friendly name
    Vendor          nvarchar(128) NULL,
    MatchPattern    nvarchar(256) NOT NULL,                          -- T-SQL LIKE vs fact.DetectedApp.DisplayName
    LicenseModel    nvarchar(16)  NOT NULL CONSTRAINT DF_WA_Model DEFAULT 'per-user',  -- per-user | per-device | server | free
    AnnualUnitCost  decimal(12,2) NULL,                              -- EUR per seat/device per year; NULL = unknown
    Currency        nvarchar(3)   NOT NULL CONSTRAINT DF_WA_Ccy DEFAULT 'EUR',
    CostConfidence  nvarchar(16)  NULL,                              -- list | estimate | unknown
    Track           bit           NOT NULL CONSTRAINT DF_WA_Track DEFAULT 1,  -- 0 = catalogued but not counted
    Notes           nvarchar(400) NULL,
    CONSTRAINT UQ_ref_WatchedApp_Name UNIQUE (Name)
);
GO

/* ---- Seed / refresh the catalog (idempotent upsert keyed on Name) ----------
   Costs are EUR per user/device per YEAR. Confidence:
     list     = from public list pricing (still verify your plan/contract)
     estimate = ballpark for enterprise software with no public per-seat price
     unknown  = no reliable figure; exposure shown by install count only        */
MERGE ref.WatchedApp AS t
USING (VALUES
  -- Name,                     Vendor,            MatchPattern,                 Model,        Cost,   Conf,        Track, Notes
  (N'Claude',                  N'Anthropic',      N'%Claude%',                  N'per-user',  240.00, N'estimate', 1, N'Team Standard ~EUR20/user/mo annual; Premium/Enterprise higher. Confirm your plan.'),
  (N'Adobe Photoshop',         N'Adobe',          N'%Photoshop%',               N'per-user',  360.00, N'list',     1, N'Single-app plan; business single-app ~EUR30-36/mo. Photography plan is cheaper if applicable.'),
  (N'think-cell',              N'think-cell',     N'%think-cell%',              N'per-user',  200.00, N'list',     1, N'Entry list ~EUR189/user/yr; volume lowers it. Per-user, any device.'),
  (N'Navisworks (paid)',       N'Autodesk',       N'%Navisworks Manage%',       N'per-user', 2500.00, N'list',     1, N'Manage ~EUR2.5k/yr, Simulate ~EUR1k/yr. NOTE: "Navfree"/Freedom is the FREE viewer (separate row).'),
  (N'PHA-Pro',                 N'Sphera',         N'%PHA-Pro%',                  N'per-user', 1500.00, N'estimate', 1, N'Process-hazard-analysis software; enterprise, no public price. Replace with contract value.'),
  (N'Meridian',                N'Accruent',       N'%Meridian%',                N'per-user', 1000.00, N'estimate', 1, N'Engineering doc management; named-user enterprise. Replace with contract value.'),
  (N'Arkieva',                 N'Arkieva',        N'%Arkieva%',                  N'per-user',   NULL, N'unknown',  1, N'Supply-chain planning; enterprise/site licensing - per-seat often not meaningful.'),
  (N'Historian',               N'AVEVA/GE',       N'%Historian%',               N'server',     NULL, N'unknown',  1, N'Industrial data historian - SERVER/tag-based licensing, not per seat. Pattern is broad; tune it.'),
  (N'OpenText DesktopLink',    N'OpenText',       N'%DesktopLink%',             N'per-user',   NULL, N'unknown',  1, N'Content Server desktop integration; enterprise. Replace with contract value.'),
  (N'ManualMaster',            N'ManualMaster',   N'%ManualMaster%',            N'per-user',   NULL, N'unknown',  1, N'QHSE/document management; enterprise. Replace with contract value.'),
  -- Free clients: catalogued for visibility, Track=0 so they never count as waste.
  (N'SAP GUI',                 N'SAP',            N'%SAP GUI%',                 N'free',       0.00, N'list',     0, N'FREE client. The SAP named-user licence is separate and not measured by install presence.'),
  (N'SAP Secure Login Client', N'SAP',            N'%Secure Login Client%',     N'free',       0.00, N'list',     0, N'FREE component of SAP SSO.'),
  (N'SAP HANA Studio',         N'SAP',            N'%HANA Studio%',             N'free',       0.00, N'list',     0, N'FREE Eclipse-based tool; HANA licence is separate.'),
  (N'Navisworks Freedom',      N'Autodesk',       N'%Navisworks%Freedom%',      N'free',       0.00, N'list',     0, N'FREE NWD viewer ("Navfree"). Not a paid seat.')
) AS s(Name, Vendor, MatchPattern, LicenseModel, AnnualUnitCost, CostConfidence, Track, Notes)
ON (t.Name = s.Name)
WHEN MATCHED THEN UPDATE SET
    t.Vendor = s.Vendor, t.MatchPattern = s.MatchPattern, t.LicenseModel = s.LicenseModel,
    t.AnnualUnitCost = s.AnnualUnitCost, t.CostConfidence = s.CostConfidence, t.Track = s.Track, t.Notes = s.Notes
WHEN NOT MATCHED THEN
    INSERT (Name, Vendor, MatchPattern, LicenseModel, AnnualUnitCost, Currency, CostConfidence, Track, Notes)
    VALUES (s.Name, s.Vendor, s.MatchPattern, s.LicenseModel, s.AnnualUnitCost, 'EUR', s.CostConfidence, s.Track, s.Notes);
GO

/* ---- Install footprint + cost exposure ------------------------------------
   fact.DetectedApp is REPLACE'd per run, so it already holds only the latest
   snapshot. DeviceCount is per inventory row (one row per app+version); we sum
   across matched versions for a per-app install footprint. AnnualExposure is the
   UPPER BOUND spend if every install is a paid seat - the "idle" reduction comes
   later from agent usage (fact.AppUsage). */
CREATE OR ALTER VIEW vw.WatchedAppEstate AS
SELECT
    w.WatchedAppId,
    w.Name,
    w.Vendor,
    w.LicenseModel,
    w.AnnualUnitCost,
    w.Currency,
    w.CostConfidence,
    COUNT(d.DetectedAppId)                       AS MatchedVersions,
    ISNULL(SUM(d.DeviceCount), 0)                AS InstallDeviceCount,
    CASE WHEN w.AnnualUnitCost IS NULL THEN NULL
         ELSE CAST(w.AnnualUnitCost * ISNULL(SUM(d.DeviceCount), 0) AS decimal(14,2))
    END                                          AS AnnualExposure
FROM ref.WatchedApp w
LEFT JOIN fact.DetectedApp d ON d.DisplayName LIKE w.MatchPattern
WHERE w.Track = 1
GROUP BY w.WatchedAppId, w.Name, w.Vendor, w.LicenseModel,
         w.AnnualUnitCost, w.Currency, w.CostConfidence;
GO

/* ---- Matched inventory rows (use this to TUNE the LIKE patterns) -----------
   Run: SELECT * FROM vw.WatchedAppInventory ORDER BY Name, DeviceCount DESC;
   If a pattern is too broad (e.g. Historian) or misses (e.g. think-cell spelled
   differently in inventory), adjust ref.WatchedApp.MatchPattern and re-query. */
CREATE OR ALTER VIEW vw.WatchedAppInventory AS
SELECT
    w.Name,
    w.MatchPattern,
    d.DisplayName,
    d.Publisher,
    d.[Version],
    d.DeviceCount
FROM ref.WatchedApp w
JOIN fact.DetectedApp d ON d.DisplayName LIKE w.MatchPattern;
GO

/* ---- The whole application estate, one row per app ---------------------------
   The dashboard's search, vendor drill and app drill read THIS view (it was
   referenced but never defined - /api/search and /api/watched-apps 500'd).
   Watched apps come through with their license model and cost exposure; every
   other inventoried app follows with install counts only, so the palette can
   find ANY application in the tenant, not just the curated ones. -------------- */
CREATE OR ALTER VIEW vw.AppEstate AS
SELECT
    w.Name, w.Vendor, w.LicenseModel, w.AnnualUnitCost, w.Currency, w.CostConfidence,
    w.MatchedVersions, w.InstallDeviceCount, w.AnnualExposure
FROM vw.WatchedAppEstate w
UNION ALL
SELECT
    d.DisplayName                    AS Name,
    MAX(d.Publisher)                 AS Vendor,
    CAST(NULL AS nvarchar(16))       AS LicenseModel,
    CAST(NULL AS decimal(12,2))      AS AnnualUnitCost,
    CAST(NULL AS nvarchar(3))        AS Currency,
    CAST(NULL AS nvarchar(16))       AS CostConfidence,
    COUNT(*)                         AS MatchedVersions,
    ISNULL(SUM(d.DeviceCount), 0)    AS InstallDeviceCount,
    CAST(NULL AS decimal(14,2))      AS AnnualExposure
FROM fact.DetectedApp d
WHERE d.DisplayName IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM ref.WatchedApp w2 WHERE w2.Track = 1 AND d.DisplayName LIKE w2.MatchPattern)
GROUP BY d.DisplayName;
GO

/* ==========================================================================
   GOVERNANCE LOG - human decisions recorded from the dashboard
   ========================================================================== */

IF OBJECT_ID('score.Decision') IS NULL
CREATE TABLE score.Decision
(
    UserId         nvarchar(64)  NOT NULL,   -- match dim.[User]/score.AssignmentVerdict key widths
    SkuId          nvarchar(64)  NOT NULL,
    SkuPartNumber  nvarchar(128) NULL,
    Decision       nvarchar(20)  NOT NULL,     -- 'reclaim' | 'keep' | 'snooze'
    DecidedBy      nvarchar(256) NULL,         -- from Entra SSO (X-MS-CLIENT-PRINCIPAL-NAME)
    DecidedUtc     datetime2(3)  NOT NULL CONSTRAINT DF_score_Decision_ts DEFAULT sysutcdatetime(),
    RunId          nvarchar(64)  NULL,         -- scoring run the decision was made against
    SnoozeUntilUtc datetime2(3)  NULL,         -- v2: snooze expires instead of hiding forever
    Note           nvarchar(400) NULL,         -- v2: reviewer rationale (audit trail)
    CONSTRAINT PK_score_Decision PRIMARY KEY (UserId, SkuId)
);
GO
IF COL_LENGTH('score.Decision','SnoozeUntilUtc') IS NULL ALTER TABLE score.Decision ADD SnoozeUntilUtc datetime2(3) NULL;
IF COL_LENGTH('score.Decision','Note')           IS NULL ALTER TABLE score.Decision ADD Note nvarchar(400) NULL;
GO
-- Defence in depth: the API validates the vocabulary; the table now enforces it too.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_score_Decision_Vocab')
    ALTER TABLE score.Decision WITH NOCHECK
    ADD CONSTRAINT CK_score_Decision_Vocab CHECK (Decision IN ('reclaim','keep','snooze'));
GO

/* ---- append-only audit of every decision (the Decision table keeps only the
       LATEST per seat; this log keeps them all, for governance export) --------- */
IF OBJECT_ID('score.DecisionLog') IS NULL
CREATE TABLE score.DecisionLog
(
    DecisionLogId  bigint IDENTITY(1,1) CONSTRAINT PK_score_DecisionLog PRIMARY KEY,
    UserId         nvarchar(64)  NOT NULL,
    SkuId          nvarchar(64)  NOT NULL,
    SkuPartNumber  nvarchar(128) NULL,
    Decision       nvarchar(20)  NOT NULL,
    DecidedBy      nvarchar(256) NULL,
    DecidedUtc     datetime2(3)  NOT NULL CONSTRAINT DF_score_DecisionLog_ts DEFAULT sysutcdatetime(),
    RunId          nvarchar(64)  NULL,
    SnoozeUntilUtc datetime2(3)  NULL,
    Note           nvarchar(400) NULL,
    VerdictAtTime  nvarchar(16)  NULL,        -- the engine's verdict when the human decided
    SavingsAtTime  decimal(12,2) NULL
);
GO
IF IndexProperty(OBJECT_ID('score.DecisionLog'),'IX_DecisionLog_Seat','IndexID') IS NULL
    CREATE INDEX IX_DecisionLog_Seat ON score.DecisionLog(UserId, SkuId, DecidedUtc DESC);
GO
