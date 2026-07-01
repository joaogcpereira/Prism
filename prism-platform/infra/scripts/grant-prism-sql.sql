-- ============================================================================
-- grant-prism-sql.sql  —  Prism (AppID 0001)
-- ----------------------------------------------------------------------------
-- Creates the contained database user for the Prism user-assigned managed
-- identity and grants it the access the jobs/apps need:
--   db_datareader + db_datawriter  (read/write the warehouse)
--   EXECUTE                        (scoring runs the stored procedures)
--
-- HOW TO RUN
--   * Connect to the PRISM DATABASE (not master) on contoso-prism-sql-dev as the
--     Entra admin (e.g. sqladmin@contoso.com — the AAD admin set in Phase 1).
--   * The SQL server has public network access DISABLED, so connect from inside
--     the VNet: a jump box / Bastion VM, a self-hosted agent, or a peered network
--     that can reach the Private Endpoint. (A temporary public firewall exception
--     also works if your policy allows it.)
--   * Tooling: sqlcmd, Azure Data Studio, or SSMS with "Azure Active Directory"
--     authentication.
--
-- The contained-user NAME must equal the managed identity's name exactly.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'id-im-prism-platform')
    CREATE USER [id-im-prism-platform] FROM EXTERNAL PROVIDER;
GO

ALTER ROLE db_datareader ADD MEMBER [id-im-prism-platform];
ALTER ROLE db_datawriter ADD MEMBER [id-im-prism-platform];
GO

-- Scoring executes the stored procedures in the score schema.
GRANT EXECUTE TO [id-im-prism-platform];
GO

-- Verify:
SELECT dp.name, dp.type_desc, dp.authentication_type_desc
FROM sys.database_principals dp
WHERE dp.name = N'id-im-prism-platform';
GO
