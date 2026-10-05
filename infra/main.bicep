// CloudTales Narrator: every Azure resource for narrating blog posts with Azure Speech.
// Zero keys by design: no account keys, no connection-string secrets, no API keys anywhere.
// Every service-to-service call is authenticated with Microsoft Entra ID.
targetScope = 'resourceGroup'

@description('Azure region for all resources. The voice must be available here.')
param location string = resourceGroup().location

@description('Speech resource name. Also its custom subdomain, so it must be globally unique.')
param speechAccountName string = 'speech-narrator-${uniqueString(resourceGroup().id)}'

@description('Speech pricing tier. F0 is free but limited to one per subscription.')
@allowed(['F0', 'S0'])
param speechSku string = 'S0'

@description('Storage account that holds the narration MP3s (3-24 lowercase letters/digits).')
param audioStorageAccountName string = 'staudio${uniqueString(resourceGroup().id)}'

@description('Neural voice used for narration.')
param voice string = 'en-US-Andrew:DragonHDLatestNeural'

@description('Base URL of the WordPress site whose posts are narrated.')
param wordPressBaseUrl string = 'https://cloudtales.gr/'

@description('Hosts allowed to play the audio (checked against Referer/Origin).')
param allowedAudioHosts string = 'cloudtales.gr,www.cloudtales.gr'

@description('Cost guardrail: maximum articles queued for synthesis per day.')
@minValue(1)
param maxSynthesesPerRun int = 3

@description('Optional: Entra object ID of a developer who runs the CLI locally. Leave empty to skip.')
param developerPrincipalId string = ''

var suffix = uniqueString(resourceGroup().id)
var functionAppName = 'func-cloudtales-narrator-${suffix}'
var hostStorageName = 'stnarr${suffix}'
var deploymentContainerName = 'app-package'

// Built-in role definition IDs
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  speechUser: 'f2dc8367-1007-4938-bd23-fe263f013447'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

// ---------- Speech: Entra ID only (local/key auth disabled) ----------

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechAccountName
  location: location
  kind: 'SpeechServices'
  sku: { name: speechSku }
  properties: {
    customSubDomainName: speechAccountName // required for Entra ID token auth
    disableLocalAuth: true                 // the resource's keys stop working
    publicNetworkAccess: 'Enabled'
  }
}

// ---------- Audio storage: private container, no shared keys ----------

resource audioStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: audioStorageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false   // playback only via short-lived user delegation SAS
    allowSharedKeyAccess: false    // forces Entra ID for data access and SAS signing
  }

  resource blobService 'blobServices' = {
    name: 'default'

    resource audioContainer 'containers' = {
      name: 'audio'
      properties: {
        publicAccess: 'None'
      }
    }
  }
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

  // One message per post to narrate; the poison queue is created by the runtime on first failure
  resource queueService 'queueServices' = {
    name: 'default'

    resource narrationQueue 'queues' = {
      name: 'narration-requests'
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
        { name: 'Audio__AllowedHosts', value: allowedAudioHosts }
        { name: 'WordPress__BaseUrl', value: wordPressBaseUrl }
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

// Timer writes and queue trigger reads the narration queue (AzureWebJobsStorage connection)
resource hostQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionApp.id, roles.storageQueueDataContributor)
  scope: hostStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageQueueDataContributor)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Also covers generating user delegation keys for the playback SAS links
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

// ---------- Optional: local developer access for the CLI ----------

resource devSpeechUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(speech.id, developerPrincipalId, roles.speechUser)
  scope: speech
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.speechUser)
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

resource devAudioContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(audioStorage.id, developerPrincipalId, roles.storageBlobDataContributor)
  scope: audioStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

output functionAppName string = functionApp.name
output audioEndpoint string = 'https://${functionApp.name}.azurewebsites.net/api/audio/'
output speechResourceId string = speech.id
output audioBlobEndpoint string = audioStorage.properties.primaryEndpoints.blob
