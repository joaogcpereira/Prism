/* ============================================================================
   PRISM - commercial seed  (tenant-specific; run AFTER schema/schema.sql)

   Loads Contoso's negotiated EUR unit prices into ref.SkuCost and the Enterprise
   Agreement contract quantities into ref.Contract. These are commercial figures
   specific to Contoso's agreement - edit them to match your current contract.

       sqlcmd -S <server>.database.windows.net -d prism -G -i schema/seed-commercial.sql

   Both loaders are self-adapting (they match your tenant's own dim.Sku rows by
   friendly name or part number and write the real SkuPartNumber, so they cannot
   mis-key) and idempotent (re-runnable). After running, re-run the prism-scoring
   job so the prices flow into the savings figures, then check vw.UnpricedSkus
   for any SKUs still needing a price.

   Prices are EUR/user/month. Origin marks provenance: negotiated | fallback-list
   -2026 | free | consumption | capacity-not-per-seat | bundle-component | included.
   ============================================================================ */

/* ==========================================================================
   NEGOTIATED PRICES - Contoso Final Offer (EUR/user/month) + 2026 list fallbacks
   ========================================================================== */

;WITH price(NameLike, PartLike, MonthlyEUR, Origin) AS (
  SELECT * FROM (VALUES
    -- ============ Contoso NEGOTIATED (Final Offer 2024-12, EUR/user/month) ============
    (N'%Microsoft 365 E3%',              N'SPE_E3',                 24.68, N'negotiated'),
    (N'%E5 Security%',                   N'%E5_SECURITY%',           8.45, N'negotiated'),
    (N'%Microsoft 365 F3%',              N'SPE_F1',                  6.27, N'negotiated'),
    (N'%Microsoft 365 F1%',              N'M365_F1_NOMATCH',         1.76, N'negotiated'),
    (N'%Office 365 F3%',                 N'DESKLESSPACK',            2.76, N'negotiated'),
    (N'%Office 365 E1%',                 N'STANDARDPACK',            5.34, N'negotiated'),
    (N'%F5 Security%',                   N'%F5_SEC%',                5.80, N'negotiated'),
    (N'%Exchange Online Archiving%',     N'EXCHANGEARCHIVE_ADDON',   2.64, N'negotiated'),
    (N'%Entra ID P1%',                   N'AAD_PREMIUM',             4.49, N'negotiated'),
    (N'%Azure AD Premium P1%',           N'AAD_PREMIUM',             4.49, N'negotiated'),
    (N'%Entra ID P2%',                   N'AAD_PREMIUM_P2',          6.73, N'negotiated'),
    (N'%Azure AD Premium P2%',           N'AAD_PREMIUM_P2',          6.73, N'negotiated'),
    (N'%Copilot for Sales%',             N'%Sales%Copilot%',        18.72, N'negotiated'),
    (N'%365 Copilot%',                   N'Microsoft_365_Copilot',  28.08, N'negotiated'),
    (N'%Phone System%',                  N'MCOEV',                   5.49, N'negotiated'),
    (N'%Teams Phone%',                   N'MCOEV',                   5.49, N'negotiated'),
    (N'%Teams Premium%',                 N'%Teams_Premium%',         6.16, N'negotiated'),
    (N'%Teams Rooms Pro%',               N'%Teams_Rooms_Pro%',      32.37, N'negotiated'),
    (N'%Power BI Pro%',                  N'POWER_BI_PRO',            6.87, N'negotiated'),
    (N'%Power Automate%',                N'%FLOW_PER_USER%',        12.14, N'negotiated'),
    (N'%Power Apps%',                    N'%POWERAPPS_PER_USER%',   16.19, N'negotiated'),
    (N'%Project%Essentials%',            N'PROJECTESSENTIALS',       5.23, N'negotiated'),
    (N'%Project Plan 1%',                N'PROJECT_P1',              7.48, N'negotiated'),
    (N'%Project Plan 3%',                N'PROJECTPROFESSIONAL',    20.64, N'negotiated'),
    (N'%Project Plan 5%',                N'PROJECTPREMIUM',         37.84, N'negotiated'),
    (N'%Visio%Plan 1%',                  N'%VISIO%PLAN1%',           3.98, N'negotiated'),
    (N'%Visio%Plan 2%',                  N'VISIOCLIENT',            10.98, N'negotiated'),
    (N'%Dynamics 365%Sales%Enterprise%', N'%ENTERPRISE_SALES%',     69.29, N'negotiated'),
    (N'%Customer Service%Enterprise%',   N'%ENTERPRISE_CUSTOMER_SERVICE%', 69.29, N'negotiated'),
    (N'%Team Members%',                  N'%TEAM_MEMBERS%',          5.28, N'negotiated'),
    (N'%Defender for Endpoint%',         N'%MDATP%',                 4.07, N'negotiated'),
    -- ============ FALLBACK list prices (~2026, EUR) for SKUs NOT negotiated ============
    (N'%Office 365 E3%',                 N'ENTERPRISEPACK',         24.00, N'fallback-list-2026'),
    (N'%Office 365 E5%',                 N'ENTERPRISEPREMIUM',      38.00, N'fallback-list-2026'),
    (N'%Microsoft 365 E5%',              N'SPE_E5',                 55.00, N'fallback-list-2026'),
    (N'%Enterprise Mobility%E5%',        N'EMSPREMIUM',             17.00, N'fallback-list-2026'),
    (N'%Enterprise Mobility%E3%',        N'EMS',                    11.00, N'fallback-list-2026'),
    (N'%Exchange Online (Plan 1)%',      N'EXCHANGESTANDARD',        4.00, N'fallback-list-2026'),
    (N'%Exchange Online (Plan 2)%',      N'EXCHANGEENTERPRISE',      8.00, N'fallback-list-2026'),
    (N'%Visio%Online%Plan 1%',           N'VISIOONLINE_PLAN1',       3.98, N'fallback-list-2026'),
    -- ============ FREE / no per-seat cost ============
    (N'%Power BI%Standard%',             N'POWER_BI_STANDARD',       0.00, N'free'),
    (N'%Microsoft Flow Free%',           N'FLOW_FREE',               0.00, N'free'),
    (N'%Microsoft Power Automate Free%', N'FLOW_FREE',               0.00, N'free'),
    (N'%Teams Exploratory%',             N'TEAMS_EXPLORATORY',       0.00, N'free'),
    (N'%PowerApps%Viral%',               N'POWERAPPS_VIRAL',         0.00, N'free'),
    (N'%Rights Management%Service Basic%', N'RMSBASIC',              0.00, N'free')
  ) v(NameLike, PartLike, MonthlyEUR, Origin)
)
MERGE ref.SkuCost AS t
USING (
    SELECT s.SkuPartNumber, s.DisplayName, x.MonthlyEUR, x.Origin
    FROM dim.Sku s
    CROSS APPLY (
        SELECT TOP 1 p.MonthlyEUR, p.Origin
        FROM price p
        WHERE s.DisplayName LIKE p.NameLike OR s.SkuPartNumber LIKE p.PartLike
        ORDER BY CASE WHEN p.Origin = N'negotiated' THEN 0 ELSE 1 END, LEN(p.NameLike) DESC
    ) x
) src ON t.SkuPartNumber = src.SkuPartNumber
WHEN MATCHED THEN UPDATE SET
    t.MonthlyUnitCost = src.MonthlyEUR, t.Currency = N'EUR',
    t.Origin = src.Origin, t.AsOfDate = '2026-06-01', t.UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SkuPartNumber, DisplayName, MonthlyUnitCost, Currency, Origin, AsOfDate)
    VALUES (src.SkuPartNumber, src.DisplayName, src.MonthlyEUR, N'EUR', src.Origin, '2026-06-01');
GO

/* What got priced, and what is still missing (paste the second result back). */
SELECT s.SkuPartNumber, s.DisplayName, c.MonthlyUnitCost, c.Currency, c.Origin
FROM dim.Sku s LEFT JOIN ref.SkuCost c ON c.SkuPartNumber = s.SkuPartNumber
ORDER BY CASE WHEN c.MonthlyUnitCost IS NULL THEN 0 ELSE 1 END, s.DisplayName;
GO

/* ==========================================================================
   PRICE GAPS - exact-part-number fills and free/consumption/capacity zeros
   ========================================================================== */

;WITH g(SkuPartNumber, MonthlyEUR, Origin) AS (
  SELECT * FROM (VALUES
    -- ----- real paid add-ons -----
    (N'IDENTITY_THREAT_PROTECTION',          8.45, N'negotiated'),          -- Microsoft 365 E5 Security
    (N'M365_F1_COMM',                        1.76, N'negotiated'),          -- Microsoft 365 F1
    (N'DYN365_BUSCENTRAL_PREMIUM',         100.00, N'fallback-list-2026'),  -- Business Central Premium (~$110)
    (N'INFORMATION_PROTECTION_COMPLIANCE',  11.00, N'fallback-list-2026'),  -- Microsoft 365 E5 Compliance
    (N'eCDN',                                0.37, N'negotiated'),          -- Microsoft eCDN
    (N'CDS_DB_CAPACITY',                    32.37, N'negotiated'),          -- Dataverse DB capacity (per unit)
    (N'SHAREPOINTSTORAGE',                   0.17, N'negotiated'),          -- extra file storage (per GB)
    -- ----- capacity, not per-seat -> 0 -----
    (N'PBI_PREMIUM_P1_ADDON',                0.00, N'capacity-not-per-seat'),
    -- ----- free / trial / consumption -> 0 -----
    (N'CCIBOTS_PRIVPREV_VIRAL',              0.00, N'free'),
    (N'Dynamics_365_Guides_vTrial',          0.00, N'free'),
    (N'MCOMEETACPEA',                        0.00, N'consumption'),
    (N'MCOPSTNC',                            0.00, N'consumption'),
    (N'Microsoft_Teams_Audio_Conferencing_select_dial_out', 0.00, N'free'),
    (N'Power_Pages_vTrial_for_Makers',       0.00, N'free'),
    (N'POWERAPPS_DEV',                       0.00, N'free'),
    (N'RIGHTSMANAGEMENT_ADHOC',              0.00, N'consumption'),
    (N'STREAM',                              0.00, N'free'),
    (N'WIN10_VDA_E5',                        0.00, N'included'),            -- Windows component, €0 in the EA
    (N'WINDOWS_STORE',                       0.00, N'free'),
    (N'PHONESYSTEM_VIRTUALUSER',             0.00, N'free'),                -- resource accounts
    -- ----- fix: free Flow had been matched to paid Power Automate -----
    (N'FLOW_FREE',                           0.00, N'free')
  ) v(SkuPartNumber, MonthlyEUR, Origin)
)
MERGE ref.SkuCost AS t
USING g ON t.SkuPartNumber = g.SkuPartNumber
WHEN MATCHED THEN UPDATE SET
    t.MonthlyUnitCost = g.MonthlyEUR, t.Currency = N'EUR',
    t.Origin = g.Origin, t.AsOfDate = '2026-06-01', t.UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SkuPartNumber, DisplayName, MonthlyUnitCost, Currency, Origin, AsOfDate)
    VALUES (g.SkuPartNumber, g.SkuPartNumber, g.MonthlyEUR, N'EUR', g.Origin, '2026-06-01');
GO

/* The "Office 365 (without Teams) Bundle" M3 split carries a non-breaking space
   in its part number, so match it with LIKE. Price sits on SPE_E3 -> set 0. */
UPDATE ref.SkuCost
SET MonthlyUnitCost = 0, Currency = N'EUR', Origin = N'bundle-component',
    AsOfDate = '2026-06-01', UpdatedUtc = sysutcdatetime()
WHERE SkuPartNumber LIKE N'O365[_]w/o%Teams%Bundle[_]M3';

INSERT ref.SkuCost (SkuPartNumber, DisplayName, MonthlyUnitCost, Currency, Origin, AsOfDate)
SELECT s.SkuPartNumber, s.DisplayName, 0, N'EUR', N'bundle-component', '2026-06-01'
FROM dim.Sku s
WHERE s.SkuPartNumber LIKE N'O365[_]w/o%Teams%Bundle[_]M3'
  AND NOT EXISTS (SELECT 1 FROM ref.SkuCost c WHERE c.SkuPartNumber = s.SkuPartNumber);
GO

/* Confirm: every SKU should now be priced (0 rows = fully covered). */
SELECT s.SkuPartNumber, s.DisplayName
FROM dim.Sku s LEFT JOIN ref.SkuCost c ON c.SkuPartNumber = s.SkuPartNumber
WHERE c.MonthlyUnitCost IS NULL
ORDER BY s.DisplayName;
GO

/* ==========================================================================
   CONTRACTS - Enterprise Agreement quantities & renewal date
   ========================================================================== */

-- >>> set this to your real EA renewal / anniversary date <<<
DECLARE @Renewal date     = '2027-12-31';
DECLARE @Term    nvarchar(60) = N'EAS Level B · 3-yr EA (2025–2027)';

IF OBJECT_ID('ref.Contract') IS NULL
CREATE TABLE ref.Contract(
    SkuPartNumber nvarchar(128) NOT NULL CONSTRAINT PK_ref_Contract PRIMARY KEY,
    RenewalDate   date NULL, QuantityOwned int NULL, Term nvarchar(60) NULL, Notes nvarchar(200) NULL);

;WITH q(NameLike, PartLike, Qty) AS (
  SELECT * FROM (VALUES
    (N'%Microsoft 365 E3%',              N'SPE_E3',                   2100),  -- 2080 FromSA + 20
    (N'%E5 Security%',                   N'IDENTITY_THREAT_PROTECTION',2100),
    (N'%Microsoft 365 F3%',              N'SPE_F1',                    110),
    (N'%Microsoft 365 F1%',              N'M365_F1_COMM',              340),
    (N'%Office 365 F3%',                 N'DESKLESSPACK',              340),
    (N'%Office 365 E1%',                 N'STANDARDPACK',              450),
    (N'%F5 Security%',                   N'%F5_SEC%',                  450),
    (N'%Exchange Online Archiving%',     N'EXCHANGEARCHIVE_ADDON',       1),
    (N'%Entra ID P1%',                   N'AAD_PREMIUM',                 1),
    (N'%Entra ID P2%',                   N'AAD_PREMIUM_P2',              1),
    (N'%365 Copilot%',                   N'Microsoft_365_Copilot',      30),
    (N'%Copilot for Sales%',             N'%Sales%Copilot%',             5),
    (N'%eCDN%',                          N'eCDN',                        1),
    (N'%Extra File Storage%',            N'SHAREPOINTSTORAGE',           1),
    (N'%Phone System%',                  N'MCOEV',                     310),
    (N'%Teams Phone%',                   N'MCOEV',                     310),
    (N'%Phone%Virtual%',                 N'PHONESYSTEM_VIRTUALUSER',    10),
    (N'%Teams Premium%',                 N'%Teams_Premium%',            25),
    (N'%Teams Rooms Pro%',               N'%Teams_Rooms_Pro%',          54),
    (N'%Dynamics 365%Sales%',            N'%ENTERPRISE_SALES%',         44),
    (N'%Customer Service%',              N'%ENTERPRISE_CUSTOMER_SERVICE%',54),
    (N'%Team Members%',                  N'%TEAM_MEMBERS%',              3),
    (N'%Dataverse%',                     N'CDS_DB_CAPACITY',            60),
    (N'%Power BI Premium P1%',           N'PBI_PREMIUM_P1_ADDON',        2),
    (N'%Power BI Pro%',                  N'POWER_BI_PRO',               55),
    (N'%Power Automate%',                N'%FLOW_PER_USER%',             6),
    (N'%Power Apps%',                    N'%POWERAPPS_PER_USER%',        7),
    (N'%Project%Essentials%',            N'PROJECTESSENTIALS',           1),
    (N'%Project Plan 1%',                N'PROJECT_P1',                  1),
    (N'%Project Plan 3%',                N'PROJECTPROFESSIONAL',       180),
    (N'%Project Plan 5%',                N'PROJECTPREMIUM',              5),
    (N'%Visio%Plan 1%',                  N'%VISIO%PLAN1%',               1),
    (N'%Visio%Plan 2%',                  N'VISIOCLIENT',               260),
    (N'%Defender for Endpoint%',         N'%MDATP%',                   170)
  ) v(NameLike, PartLike, Qty)
)
MERGE ref.Contract AS t
USING (
    SELECT s.SkuPartNumber, x.Qty
    FROM dim.Sku s
    CROSS APPLY (
        SELECT TOP 1 q.Qty FROM q
        WHERE s.DisplayName LIKE q.NameLike OR s.SkuPartNumber LIKE q.PartLike
        ORDER BY LEN(q.NameLike) DESC
    ) x
) src ON t.SkuPartNumber = src.SkuPartNumber
WHEN MATCHED THEN UPDATE SET
    QuantityOwned = src.Qty, RenewalDate = @Renewal, Term = @Term
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SkuPartNumber, RenewalDate, QuantityOwned, Term)
    VALUES (src.SkuPartNumber, @Renewal, src.Qty, @Term);
GO

SELECT SkuPartNumber, QuantityOwned, Term, RenewalDate FROM ref.Contract ORDER BY QuantityOwned DESC;
GO
