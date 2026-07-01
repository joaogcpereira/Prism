// =============================================================================
// apps/container-job.bicep  —  Prism (AppID 0001) reusable Container Apps Job
// -----------------------------------------------------------------------------
// Phase 2 building block for the two scheduled (cron) Prism workloads:
//   * prism-connectors (Microsoft Graph / Intune / Defender / Entra / Cost ETL
//     into the SQL warehouse)
//   * prism-scoring    (reads the warehouse views, writes verdicts back)
//
// These are Container Apps JOBS (triggerType = Schedule), not always-on apps:
// each run starts a replica, the .dll runs to completion, the replica exits.
// They have NO ingress. Deployed into the same internal, VNet-injected Phase 1
// environment; same secret-free ACR pull + SQL/Graph auth via the shared UAMI.
// =============================================================================

// =============== //
// Parameters      //
// =============== //

@description('Required. Name of the Container Apps Job (e.g. "prism-connectors").')
param name string

@description('Optional. Location for the resource. Default is the resource group location.')
param location string = resourceGroup().location

@description('Optional. Tags of the resource. Default is the resource group tags.')
param tags object = resourceGroup().tags

@description('Required. Resource ID of the EXISTING Container Apps managed environment (Phase 1).')
param environmentResourceId string

@description('Required. Resource ID of the shared user-assigned managed identity (ACR pull + SQL/Graph auth).')
param uamiResourceId string

@description('Required. ACR login server, e.g. "contosoprismacrdev.azurecr.io".')
param acrLoginServer string

@description('Required. Image name:tag relative to the registry, e.g. "prism-connectors:1.0.0".')
param image string

@description('Required. Cron schedule in UTC (Container Apps jobs use UTC), e.g. "0 2 * * *".')
param cronExpression string

@description('Optional. Environment variables for the container ({ name, value } or { name, secretRef }). Default [].')
param env array = []

@description('Optional. Secrets available to the container ({ name, value }). Default [].')
@secure()
param secrets object = { items: [] }

@description('Optional. vCPU for the replica (string; converted with json()). Connectors are I/O bound but the install sweep benefits from headroom. Default "1.0".')
param cpu string = '1.0'

@description('Optional. Memory for the replica. Must pair with cpu (1.0 vCPU -> 2Gi). Default "2Gi".')
param memory string = '2Gi'

@description('Optional. Max seconds a replica may run before it is stopped. Connectors honor an unbounded internal deadline, so give the run plenty of room. Default 10800 (3h).')
param replicaTimeout int = 10800

@description('Optional. Retry attempts for a failed replica. Default 1.')
param replicaRetryLimit int = 1

@description('Optional. Parallel replicas per scheduled run (and completion count). Default 1.')
param parallelism int = 1

// =============== //
// Variables       //
// =============== //

var secretsArray = secrets.items
var hasSecrets = !empty(secretsArray)

// =============== //
// Resource        //
// =============== //

resource job 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiResourceId}': {}
    }
  }
  properties: {
    environmentId: environmentResourceId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: replicaRetryLimit
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: parallelism
        replicaCompletionCount: parallelism
      }
      registries: [
        {
          server: acrLoginServer
          identity: uamiResourceId
        }
      ]
      secrets: hasSecrets ? secretsArray : null
    }
    template: {
      containers: [
        {
          name: name
          image: '${acrLoginServer}/${image}'
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: env
        }
      ]
    }
  }
}

// =============== //
// Outputs         //
// =============== //

@description('The name of the Container Apps Job.')
output name string = job.name

@description('The resource ID of the Container Apps Job.')
output resourceId string = job.id
