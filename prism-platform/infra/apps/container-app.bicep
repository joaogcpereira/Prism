// =============================================================================
// apps/container-app.bicep  —  Prism (AppID 0001) reusable Container App module
// -----------------------------------------------------------------------------
// Phase 2 building block for the two HTTP-ingress Prism services:
//   * prism-gateway   (device-agent mTLS ingestion endpoint, port 8080)
//   * prism-dashboard (read-only FinOps UI + /api, port 8080)
//
// PAA (Private Application Architecture) notes:
//   * The app is deployed INTO the existing internal, VNet-injected Container
//     Apps environment created in Phase 1 (container-env.bicep). Because that
//     environment uses an INTERNAL load balancer, `ingress.external = true`
//     publishes the app on the environment's PRIVATE VNet IP only — it is NOT
//     reachable from the public internet. There is no public ingress IP.
//   * Images are pulled from the Phase 1 Premium ACR (public access Disabled)
//     using the shared user-assigned managed identity — no admin user, no
//     registry credentials/secrets.
//   * SQL is reached over the Phase 1 Private Endpoint using the same managed
//     identity (secret-free Entra auth); no SQL password ever exists.
// =============================================================================

// =============== //
// Parameters      //
// =============== //

@description('Required. Name of the Container App (e.g. "prism-gateway").')
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

@description('Required. Image name:tag relative to the registry, e.g. "prism-gateway:1.0.0".')
param image string

@description('Required. Container port the app listens on (8080 for both Prism apps).')
param targetPort int

@description('Optional. Whether the app is exposed via ingress. With an INTERNAL environment, external=true means "reachable inside the VNet only". Default true.')
param external bool = true

@description('Optional. Ingress client-certificate handling. "accept" forwards the device cert to the app via the X-Forwarded-Client-Cert header (gateway mTLS); "ignore" for the dashboard. Default "ignore".')
@allowed([
  'ignore'
  'accept'
  'require'
])
param clientCertificateMode string = 'ignore'

@description('Optional. Environment variables for the container ({ name, value } or { name, secretRef }). Default [].')
param env array = []

@description('Optional. Secrets available to the container ({ name, value }). Default [].')
@secure()
param secrets object = { items: [] }

@description('Optional. vCPU for the container (string; converted with json()). Default "0.5".')
param cpu string = '0.5'

@description('Optional. Memory for the container. Must pair with cpu (0.5 vCPU -> 1Gi). Default "1Gi".')
param memory string = '1Gi'

@description('Optional. Minimum replica count. Keep >= 1 so the gateway/dashboard are always warm. Default 1.')
param minReplicas int = 1

@description('Optional. Maximum replica count. Default 3.')
param maxReplicas int = 3

// =============== //
// Variables       //
// =============== //

// secrets is passed as a secured object { items: [...] } because Bicep cannot mark
// a bare array @secure(); unwrap it for the resource and omit when empty.
var secretsArray = secrets.items
var hasSecrets = !empty(secretsArray)

// =============== //
// Resource        //
// =============== //

resource app 'Microsoft.App/containerApps@2024-10-02-preview' = {
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
    managedEnvironmentId: environmentResourceId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: external
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        clientCertificateMode: clientCertificateMode
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
      }
      registries: [
        {
          server: acrLoginServer
          // Pull with the user-assigned managed identity (needs AcrPull on the ACR).
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
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// =============== //
// Outputs         //
// =============== //

@description('The name of the Container App.')
output name string = app.name

@description('The resource ID of the Container App.')
output resourceId string = app.id

@description('The private FQDN of the app (on the internal environment default domain). Send to the platform team if a custom A record is needed.')
output fqdn string = app.properties.configuration.ingress.fqdn
