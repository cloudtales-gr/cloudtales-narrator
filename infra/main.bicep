// CloudTales Narrator: daily Azure Function that narrates blog posts.
// Keyless by design: every service-to-service call uses the Function's managed identity.
targetScope = 'resourceGroup'

@description('Azure region for all new resources.')
param location string = resourceGroup().location

@description('Existing Speech resource (must have a custom domain for Entra ID auth).')
param speechAccountName string

@description('Existing storage account holding the narration MP3s.')
param audioStorageAccountName string

@description('Neural voice used for narration.')
param voice string = 'en-US-Andrew:DragonHDLatestNeural'

@description('Cost guardrail: maximum articles synthesized per daily run.')
@minValue(1)
param maxSynthesesPerRun int = 3

var suffix = uniqueString(resourceGroup().id)
var functionAppName = 'func-cloudtales-narrator-${suffix}'
var hostStorageName = 'stnarr${suffix}'
var deploymentContainerName = 'app-package'

// Built-in role definition IDs
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  speechUser: 'f2dc8367-1007-4938-bd23-fe263f013447'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

// ---------- Existing resources (created in steps 1 and 4) ----------

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: speechAccountName
}

resource audioStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: audioStorageAccountName
}

// ---------- Function host storage: no keys, no public access ----------

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
  }

  resource blobService 'blobServices' = {
    name: 'default'

    resource deploymentContainer 'containers' = {
      name: deploymentContainerName
    }
  }
}

// ---------- Monitoring: Entra ID ingestion only ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-cloudtales-narrator-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-cloudtales-narrator-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

// ---------- Flex Consumption plan and Function App ----------

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-cloudtales-narrator-${suffix}'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        // Host storage via managed identity (no connection string, no key)
        { name: 'AzureWebJobsStorage__accountName', value: hostStorage.name }
        // Telemetry via Entra ID (local auth disabled on the component)
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
        // Application settings read by Program.cs / NarratorCredential
        { name: 'NARRATOR_USE_MANAGED_IDENTITY', value: 'true' }
        { name: 'Speech__ResourceId', value: speech.id }
        { name: 'Speech__Region', value: speech.location }
        { name: 'Speech__Voice', value: voice }
        { name: 'Storage__BlobEndpoint', value: audioStorage.properties.primaryEndpoints.blob }
        { name: 'Narrator__MaxSynthesesPerRun', value: string(maxSynthesesPerRun) }
      ]
    }
  }
}

// ---------- Least-privilege role assignments for the Function identity ----------

resource hostStorageOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionApp.id, roles.storageBlobDataOwner)
  scope: hostStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataOwner)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource audioStorageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(audioStorage.id, functionApp.id, roles.storageBlobDataContributor)
  scope: audioStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource speechUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, functionApp.id, roles.speechUser)
  scope: speech
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.speechUser)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource telemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, functionApp.id, roles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringMetricsPublisher)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output hostStorageName string = hostStorage.name
