// The live configuration of cloudtales.gr, kept as a working example.
// Copy it, change every value, and deploy with:
//   az deployment group create -g <resource-group> -n main -p infra/examples/<your-file>.bicepparam
using '../main.bicep'

// Existing resource names: changing these would create new resources next to the old ones
param namePrefix = 'cloudtales-narrator'
param speechAccountName = 'cloudtales-speech'
param audioStorageAccountName = 'stcloudtalesaudio'

param wordPressBaseUrl = 'https://cloudtales.gr/'
param allowedAudioHosts = 'cloudtales.gr,www.cloudtales.gr'

// Spoken in every narration and part of its SSML hash: changing them re-synthesizes every post
param siteName = 'CloudTales'
param spokenAddress = 'CloudTales dot G R'
