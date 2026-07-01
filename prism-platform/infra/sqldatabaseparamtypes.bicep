@export()
@description('The type for a sever-external administrator.')
type serverExternalAdministratorType = {
  @description('Optional. Type of the sever administrator.')
  administratorType: 'ActiveDirectory'?

  @description('Required. Azure Active Directory only Authentication enabled.')
  azureADOnlyAuthentication: bool

  @description('Required. Login name of the server administrator.')
  login: string

  @description('Required. Principal Type of the sever administrator.')
  principalType: 'Application' | 'Group' | 'User'

  @description('Required. SID (object ID) of the server administrator.')
  sid: string

  @description('Optional. Tenant ID of the administrator.')
  tenantId: string?
}

@export()
@description('The elastic pool SKU.')
type skuType = {
  @description('Optional. The capacity of the particular SKU.')
  capacity: int?

  @description('Optional. If the service has different generations of hardware, for the same SKU, then that can be captured here.')
  family: string?

  @description('Required. The name of the SKU, typically, a letter + Number code, e.g. P3.')
  name:
    | 'BasicPool'
    | 'StandardPool'
    | 'PremiumPool'
    | 'GP_Gen5'
    | 'GP_DC'
    | 'GP_FSv2'
    | 'BC_Gen5'
    | 'BC_DC'
    | 'HS_Gen5'
    | 'HS_PRMS'
    | 'HS_MOPRMS'
    | 'ServerlessPool'

  @description('Optional. Size of the particular SKU.')
  size: string?

  @description('Optional. The tier or edition of the particular SKU, e.g. Basic, Premium.')
  tier: string?
}

@export()
@description('The per database settings for the elastic pool.')
type perDatabaseSettingsType = {
  @description('Optional. Auto Pause Delay for per database within pool.')
  autoPauseDelay: int?

  @description('Required. The maximum capacity any one database can consume. Examples: \'0.5\', \'2\'.')
  maxCapacity: string

  // using string as minCapacity can be fractional
  @description('Required. The minimum capacity all databases are guaranteed. Examples: \'0.5\', \'1\'.')
  minCapacity: string
}

@export()
@description('The type for an elastic pool property.')
type elasticPoolType = {
  @description('Required. The name of the Elastic Pool.')
  name: string

  @description('Optional. Tags of the resource.')
  tags: object?

  @description('Optional. The elastic pool SKU.')
  sku: skuType?

  @description('Optional. Time in minutes after which elastic pool is automatically paused. A value of -1 means that automatic pause is disabled.')
  autoPauseDelay: int?

  @description('Required. If set to 1, 2 or 3, the availability zone is hardcoded to that value. If set to -1, no zone is defined. Note that the availability zone numbers here are the logical availability zone in your Azure subscription. Different subscriptions might have a different mapping of the physical zone and logical zone. To understand more, please refer to [Physical and logical availability zones](https://learn.microsoft.com/en-us/azure/reliability/availability-zones-overview?tabs=azure-cli#physical-and-logical-availability-zones).')
  availabilityZone: (-1 | 1 | 2 | 3)

  @description('Optional. The number of secondary replicas associated with the elastic pool that are used to provide high availability. Applicable only to Hyperscale elastic pools.')
  highAvailabilityReplicaCount: int?

  @description('Optional. The license type to apply for this elastic pool.')
  licenseType: 'BasePrice' | 'LicenseIncluded'?

  @description('Optional. Maintenance configuration id assigned to the elastic pool. This configuration defines the period when the maintenance updates will will occur.')
  maintenanceConfigurationId: string?

  @description('Optional. The storage limit for the database elastic pool in bytes.')
  maxSizeBytes: int?

  @description('Optional. Minimal capacity that serverless pool will not shrink below, if not paused.')
  minCapacity: int?

  @description('Optional. The per database settings for the elastic pool.')
  perDatabaseSettings: perDatabaseSettingsType?

  @description('Optional. Type of enclave requested on the elastic pool.')
  preferredEnclaveType: 'Default' | 'VBS'?

  @description('Optional. Whether or not this elastic pool is zone redundant, which means the replicas of this elastic pool will be spread across multiple availability zones.')
  zoneRedundant: bool?
}

@description('The database SKU.')
type databaseSkuType = {
  @description('Optional. The capacity of the particular SKU.')
  capacity: int?

  @description('Optional. If the service has different generations of hardware, for the same SKU, then that can be captured here.')
  family: string?

  @description('Required. The name of the SKU, typically, a letter + Number code, e.g. P3.')
  name: string

  @description('Optional. Size of the particular SKU.')
  size: string?

  @description('Optional. The tier or edition of the particular SKU, e.g. Basic, Premium.')
  tier: string?
}

@description('The short-term backup retention policy for the database.')
type shortTermBackupRetentionPolicyType = {
  @description('Optional. Differential backup interval in hours. For Hyperscale tiers this value will be ignored.')
  diffBackupIntervalInHours: int?

  @description('Optional. Point-in-time retention in days.')
  retentionDays: int?
}

@description('The long-term backup retention policy for the database.')
type longTermBackupRetentionPolicyType = {
  @description('Optional. The BackupStorageAccessTier for the LTR backups.')
  backupStorageAccessTier: 'Archive' | 'Hot'?

  @description('Optional. The setting whether to make LTR backups immutable.')
  makeBackupsImmutable: bool?

  @description('Optional. Monthly retention in ISO 8601 duration format.')
  monthlyRetention: string?

  @description('Optional. Weekly retention in ISO 8601 duration format.')
  weeklyRetention: string?

  @description('Optional. Week of year backup to keep for yearly retention.')
  weekOfYear: int?

  @description('Optional. Yearly retention in ISO 8601 duration format.')
  yearlyRetention: string?
}

import { managedIdentityOnlyUserAssignedType, diagnosticSettingFullType } from 'br/public:avm/utl/types/avm-common-types:0.6.1'

@export()
@description('The type for a database.')
type databaseType = {
  @description('Required. The name of the Elastic Pool.')
  name: string

  @description('Optional. Tags of the resource.')
  tags: object?

  @description('Optional. The managed identities for the database.')
  managedIdentities: managedIdentityOnlyUserAssignedType?

  @description('Optional. The database SKU.')
  sku: databaseSkuType?

  @description('Optional. Time in minutes after which database is automatically paused. A value of -1 means that automatic pause is disabled.')
  autoPauseDelay: int?

  @description('Required. If set to 1, 2 or 3, the availability zone is hardcoded to that value. If set to -1, no zone is defined. Note that the availability zone numbers here are the logical availability zone in your Azure subscription. Different subscriptions might have a different mapping of the physical zone and logical zone. To understand more, please refer to [Physical and logical availability zones](https://learn.microsoft.com/en-us/azure/reliability/availability-zones-overview?tabs=azure-cli#physical-and-logical-availability-zones).')
  availabilityZone: (-1 | 1 | 2 | 3)

  @description('Optional. Collation of the metadata catalog.')
  catalogCollation: string?

  @description('Optional. The collation of the database.')
  collation: string?

  @description('Optional. Specifies the mode of database creation.')
  createMode:
    | 'Copy'
    | 'Default'
    | 'OnlineSecondary'
    | 'PointInTimeRestore'
    | 'Recovery'
    | 'Restore'
    | 'RestoreExternalBackup'
    | 'RestoreExternalBackupSecondary'
    | 'RestoreLongTermRetentionBackup'
    | 'Secondary'?

  @description('Optional. The resource identifier of the elastic pool containing this database.')
  elasticPoolResourceId: string?

  @description('Optional. The azure key vault URI of the database if it\'s configured with per Database Customer Managed Keys.')
  encryptionProtector: string?

  @description('Optional. The flag to enable or disable auto rotation of database encryption protector AKV key.')
  encryptionProtectorAutoRotation: bool?

  @description('Optional. The Client id used for cross tenant per database CMK scenario.')
  @minLength(36)
  @maxLength(36)
  federatedClientId: string?

  @description('Optional. Specifies the behavior when monthly free limits are exhausted for the free database.')
  freeLimitExhaustionBehavior: 'AutoPause' | 'BillOverUsage'?

  @description('Optional. The number of secondary replicas associated with the database that are used to provide high availability. Not applicable to a Hyperscale database within an elastic pool.')
  highAvailabilityReplicaCount: int?

  @description('Optional. Whether or not this database is a ledger database, which means all tables in the database are ledger tables.')
  isLedgerOn: bool?

  // keys
  @description('Optional. The license type to apply for this database.')
  licenseType: 'BasePrice' | 'LicenseIncluded'?

  @description('Optional. The resource identifier of the long term retention backup associated with create operation of this database.')
  longTermRetentionBackupResourceId: string?

  @description('Optional. Maintenance configuration id assigned to the database. This configuration defines the period when the maintenance updates will occur.')
  maintenanceConfigurationId: string?

  @description('Optional. Whether or not customer controlled manual cutover needs to be done during Update Database operation to Hyperscale tier.')
  manualCutover: bool?

  @description('Optional. The max size of the database expressed in bytes.')
  maxSizeBytes: int?

  // string to enable fractional values
  @description('Optional. Minimal capacity that database will always have allocated, if not paused.')
  minCapacity: string?

  @description('Optional. To trigger customer controlled manual cutover during the wait state while Scaling operation is in progress.')
  performCutover: bool?

  @description('Optional. Type of enclave requested on the database.')
  preferredEnclaveType: 'Default' | 'VBS'?

  @description('Optional. The state of read-only routing. If enabled, connections that have application intent set to readonly in their connection string may be routed to a readonly secondary replica in the same region. Not applicable to a Hyperscale database within an elastic pool.')
  readScale: 'Disabled' | 'Enabled'?

  @description('Optional. The resource identifier of the recoverable database associated with create operation of this database.')
  recoverableDatabaseResourceId: string?

  @description('Optional. The resource identifier of the recovery point associated with create operation of this database.')
  recoveryServicesRecoveryPointResourceId: string?

  @description('Optional. The storage account type to be used to store backups for this database.')
  requestedBackupStorageRedundancy: 'Geo' | 'GeoZone' | 'Local' | 'Zone'?

  @description('Optional. The resource identifier of the restorable dropped database associated with create operation of this database.')
  restorableDroppedDatabaseResourceId: string?

  @description('Optional. Specifies the point in time (ISO8601 format) of the source database that will be restored to create the new database.')
  restorePointInTime: string?

  @description('Optional. The name of the sample schema to apply when creating this database.')
  sampleName: string?

  @description('Optional. The secondary type of the database if it is a secondary.')
  secondaryType: 'Geo' | 'Named' | 'Standby'?

  @description('Optional. Specifies the time that the database was deleted.')
  sourceDatabaseDeletionDate: string?

  @description('Optional. The resource identifier of the source database associated with create operation of this database.')
  sourceDatabaseResourceId: string?

  @description('Optional. The resource identifier of the source associated with the create operation of this database.')
  sourceResourceId: string?

  @description('Optional. Whether or not the database uses free monthly limits. Allowed on one database in a subscription.')
  useFreeLimit: bool?

  @description('Optional. Whether or not this database is zone redundant, which means the replicas of this database will be spread across multiple availability zones.')
  zoneRedundant: bool?

  @description('Optional. The diagnostic settings of the service.')
  diagnosticSettings: diagnosticSettingFullType[]?

  @description('Optional. The short term backup retention policy for the database.')
  backupShortTermRetentionPolicy: shortTermBackupRetentionPolicyType?

  @description('Optional. The long term backup retention policy for the database.')
  backupLongTermRetentionPolicy: longTermBackupRetentionPolicyType?
}

@export()
@description('The type for a firewall rule.')
type firewallRuleType = {
  @description('Required. The name of the firewall rule.')
  name: string

  @description('Optional. The start IP address of the firewall rule. Must be IPv4 format. Use value \'0.0.0.0\' for all Azure-internal IP addresses.')
  startIpAddress: string?

  @description('Optional. The end IP address of the firewall rule. Must be IPv4 format. Must be greater than or equal to startIpAddress. Use value \'0.0.0.0\' for all Azure-internal IP addresses.')
  endIpAddress: string?
}

@export()
@description('The type for a virtual network rule.')
type virtualNetworkRuleType = {
  @description('Required. The name of the Server Virtual Network Rule.')
  name: string

  @description('Required. The resource ID of the virtual network subnet.')
  virtualNetworkSubnetResourceId: string

  @description('Optional. Allow creating a firewall rule before the virtual network has vnet service endpoint enabled.')
  ignoreMissingVnetServiceEndpoint: bool?
}

@export()
@description('The type for a security alert policy.')
type securityAlerPolicyType = {
  @description('Required. The name of the Security Alert Policy.')
  name: string

  @description('Optional. Alerts to disable.')
  disabledAlerts: (
    | 'Sql_Injection'
    | 'Sql_Injection_Vulnerability'
    | 'Access_Anomaly'
    | 'Data_Exfiltration'
    | 'Unsafe_Action'
    | 'Brute_Force')[]?

  @description('Optional. Specifies that the alert is sent to the account administrators.')
  emailAccountAdmins: bool?

  @description('Optional. Specifies an array of email addresses to which the alert is sent.')
  emailAddresses: string[]?

  @description('Optional. Specifies the number of days to keep in the Threat Detection audit logs.')
  retentionDays: int?

  @description('Optional. Specifies the state of the policy, whether it is enabled or disabled or a policy has not been applied yet on the specific database.')
  state: 'Enabled' | 'Disabled'?

  @description('Optional. Specifies the identifier key of the Threat Detection audit storage account.')
  storageAccountAccessKey: string?

  @description('Optional. Specifies the blob storage endpoint. This blob storage will hold all Threat Detection audit logs.')
  storageEndpoint: string?
}

@export()
@description('The type for a key.')
type keyType = {
  @description('Optional. The name of the key. Must follow the [<keyVaultName>_<keyName>_<keyVersion>] pattern.')
  name: string?

  @description('Optional. The server key type.')
  serverKeyType: 'ServiceManaged' | 'AzureKeyVault'?

  @description('Optional. The URI of the server key. If the ServerKeyType is AzureKeyVault, then the URI is required. The AKV URI is required to be in this format: \'https://YourVaultName.azure.net/keys/YourKeyName/YourKeyVersion\'.')
  uri: string?
}

@description('The type for recurring scans.')
type recurringScansType = {
  @description('Required. Specifies an array of e-mail addresses to which the scan notification is sent.')
  emails: string[]

  @description('Optional. Specifies that the schedule scan notification will be sent to the subscription administrators.')
  emailSubscriptionAdmins: bool?

  @description('Optional. Recurring scans state.')
  isEnabled: bool?
}

@export()
@description('The type for a vulnerability assessment.')
type vulnerabilityAssessmentType = {
  @description('Required. The name of the vulnerability assessment.')
  name: string

  @description('Optional. The recurring scans settings.')
  recurringScans: recurringScansType?

  @description('Required. The resource ID of the storage account to store the scan reports.')
  storageAccountResourceId: string

  @description('Optional. Specifies whether to use the storage account access key to access the storage account.')
  useStorageAccountAccessKey: bool?

  @description('Optional. Specifies whether to create a role assignment for the storage account.')
  createStorageRoleAssignment: bool?
}

@export()
@description('The type for audit settings.')
type auditSettingsType = {
  @description('Optional. Specifies the name of the audit settings.')
  name: string?

  @description('Optional. Specifies the Actions-Groups and Actions to audit.')
  auditActionsAndGroups: string[]?

  @description('Optional. Specifies whether audit events are sent to Azure Monitor.')
  isAzureMonitorTargetEnabled: bool?

  @description('Optional. Specifies the state of devops audit. If state is Enabled, devops logs will be sent to Azure Monitor.')
  isDevopsAuditEnabled: bool?

  @description('Optional. Specifies whether Managed Identity is used to access blob storage.')
  isManagedIdentityInUse: bool?

  @description('Optional. Specifies whether storageAccountAccessKey value is the storage\'s secondary key.')
  isStorageSecondaryKeyInUse: bool?

  @description('Optional. Specifies the amount of time in milliseconds that can elapse before audit actions are forced to be processed.')
  queueDelayMs: int?

  @description('Optional. Specifies the number of days to keep in the audit logs in the storage account.')
  retentionDays: int?

  @description('Optional. Specifies the state of the audit. If state is Enabled, storageEndpoint or isAzureMonitorTargetEnabled are required.')
  state: 'Enabled' | 'Disabled'?

  @description('Optional. Specifies the identifier key of the auditing storage account.')
  storageAccountResourceId: string?
}

@export()
@description('The type for a secrets export configuration.')
type secretsExportConfigurationType = {
  @description('Required. The resource ID of the key vault where to store the secrets of this module.')
  keyVaultResourceId: string

  @description('Optional. The sqlAdminPassword secret name to create.')
  sqlAdminPasswordSecretName: string?

  @description('Optional. The sqlAzureConnectionString secret name to create.')
  sqlAzureConnectionStringSercretName: string?
}

@description('The type for a read-only endpoint.')
type readOnlyEndpointType = {
  @description('Required. Failover policy of the read-only endpoint for the failover group.')
  failoverPolicy: 'Disabled' | 'Enabled'

  @description('Required. The target partner server where the read-only endpoint points to.')
  targetServer: string
}

@description('The type for a read-write endpoint.')
type readWriteEndpointType = {
  @description('Required. Failover policy of the read-write endpoint for the failover group. If failoverPolicy is Automatic then failoverWithDataLossGracePeriodMinutes is required.')
  failoverPolicy: 'Automatic' | 'Manual'

  @description('Optional. Grace period before failover with data loss is attempted for the read-write endpoint.')
  failoverWithDataLossGracePeriodMinutes: int?
}

@export()
@description('The type for a failover group.')
type failoverGroupType = {
  @description('Required. The name of the failover group.')
  name: string

  @description('Optional. Tags of the resource.')
  tags: object?

  @description('Required. List of databases in the failover group.')
  databases: string[]

  @description('Required. List of the partner servers for the failover group.')
  partnerServers: string[]

  @description('Optional. Read-only endpoint of the failover group instance.')
  readOnlyEndpoint: readOnlyEndpointType?

  @description('Required. Read-write endpoint of the failover group instance.')
  readWriteEndpoint: readWriteEndpointType

  @description('Required. Databases secondary type on partner server.')
  secondaryType: 'Geo' | 'Standby'
}