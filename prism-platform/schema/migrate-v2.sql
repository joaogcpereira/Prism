/* ============================================================================
   PRISM 1.0.0-rc.1 - one-time upgrade for databases created from a PRE-RELEASE
   (preview) schema.  Fresh deployments NEVER need this file.
   ----------------------------------------------------------------------------
   schema/schema.sql is the single consolidated schema and is self-upgrading
   (guarded tables, guarded column ALTERs, drop-and-recreate index revisions,
   CREATE OR ALTER views). Re-running it against a preview database performs
   the structural upgrade. This companion file then does the three things a
   structural pass cannot: one-time data backfills, the verdict-history
   baseline, and a verification report.

   RUN ORDER (preview database → RC1):
     1. (recommended) Take a copy/backup:  az sql db copy ...
     2. sqlcmd -S <server>.database.windows.net -d prism -G -i schema/schema.sql
     3. sqlcmd -S <server>.database.windows.net -d prism -G -i schema/migrate-v2.sql
     4. Re-deploy the RC1 containers (connectors / scoring / dashboard / gateway).

   WHAT RC1 ADDS OVER THE PREVIEW SCHEMA (summary):
     New signal tables      fact.Mailbox, fact.PstnUsage, fact.AuthMethodRegistration
     New scoring artifacts  score.VerdictHistory (trends), score.DecisionLog (audit),
                            score.Decision + SnoozeUntilUtc/Note + vocabulary CHECK,
                            score.AssignmentVerdict + SignalCount/EvidenceJson
     New meta               meta.ConnectorState (delta watermarks), LoadRun indexes
     Enriched dims          dim.User + UserType/OnPremisesSyncEnabled (+4 indexes),
                            dim.Device + IX_Device_Name
     Faster facts           fact.LicenseAssignment + DisabledPlanCount (+covering IX),
                            fact.ServiceUsage covering IX, fact.DetectedApp/AppInstall/
                            fact.AppUsage new covering indexes
     Views                  vw.LicenseSignals (mailbox purpose, MFA, guest, hybrid),
                            vw.ReviewQueue (decision-aware), vw.AppEstate (powers the
                            dashboard search / app estate), vw.VerdictDelta,
                            vw.DataFreshness, vw.PstnUsageByUser, vw.CopilotDepthByUser
   ============================================================================ */

SET NOCOUNT ON;

/* ---- 0. Guard: schema.sql v2 must have run first -------------------------- */
IF OBJECT_ID('score.VerdictHistory') IS NULL OR OBJECT_ID('fact.Mailbox') IS NULL
BEGIN
    RAISERROR('v2 objects not found. Run schema/schema.sql first (it is idempotent and self-migrating), then re-run this file.', 16, 1);
    RETURN;
END

/* ---- 1. One-time backfill: DisabledPlanCount for rows loaded before v2 ----
   The sink materialises this going forward; backfill legacy rows once so
   vw.LicenseSignals never pays the OPENJSON fallback again. ------------------ */
UPDATE la SET DisabledPlanCount =
       CASE WHEN la.DisabledServicePlanIds IS NULL THEN 0
            ELSE (SELECT COUNT(*) FROM OPENJSON(la.DisabledServicePlanIds)) END
FROM fact.LicenseAssignment la
WHERE la.DisabledPlanCount IS NULL;
PRINT CONCAT('Backfilled DisabledPlanCount on ', @@ROWCOUNT, ' assignment row(s).');

/* ---- 2. One-time seed: current verdicts into the history so vw.VerdictDelta
        has a baseline on the very first v2 scoring run ----------------------- */
IF NOT EXISTS (SELECT 1 FROM score.VerdictHistory)
BEGIN
    INSERT INTO score.VerdictHistory (RunId, ScoredUtc, UserId, SkuId, SkuPartNumber, Verdict, WasteScore, Confidence, ReasonCodes, EstMonthlySavings)
    SELECT ISNULL(RunId, 'pre-v2'), ScoredUtc, UserId, SkuId, SkuPartNumber, Verdict, WasteScore, Confidence, ReasonCodes, EstMonthlySavings
    FROM score.AssignmentVerdict;
    PRINT CONCAT('Seeded score.VerdictHistory with ', @@ROWCOUNT, ' current verdict(s).');
END

/* ---- 3. Verification ------------------------------------------------------ */
DECLARE @missing TABLE (What sysname);
INSERT INTO @missing (What)
SELECT v FROM (VALUES
    ('fact.Mailbox'), ('fact.PstnUsage'), ('fact.AuthMethodRegistration'),
    ('score.VerdictHistory'), ('score.DecisionLog'), ('meta.ConnectorState')
) x(v) WHERE OBJECT_ID(x.v) IS NULL
UNION ALL
SELECT c FROM (VALUES
    ('dim.[User].UserType'), ('dim.[User].OnPremisesSyncEnabled'),
    ('fact.LicenseAssignment.DisabledPlanCount'),
    ('score.AssignmentVerdict.EvidenceJson'), ('score.Decision.SnoozeUntilUtc')
) y(c) WHERE COL_LENGTH(PARSENAME(y.c, 3) + '.' + PARSENAME(y.c, 2), PARSENAME(y.c, 1)) IS NULL
UNION ALL
SELECT 'vw.' + v FROM (VALUES ('AppEstate'), ('VerdictDelta'), ('DataFreshness'), ('PstnUsageByUser'), ('CopilotDepthByUser')
) z(v) WHERE OBJECT_ID('vw.' + z.v, 'V') IS NULL;

IF EXISTS (SELECT 1 FROM @missing)
BEGIN
    SELECT What AS MissingV2Object FROM @missing;
    RAISERROR('v2 migration INCOMPLETE - objects above are missing. Re-run schema/schema.sql and check its output for errors.', 16, 1);
END
ELSE
    PRINT 'v2 migration verified: all new tables, columns and views are present.';
