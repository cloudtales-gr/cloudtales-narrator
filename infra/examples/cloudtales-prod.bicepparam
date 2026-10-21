using '../main.bicep'

param namePrefix = 'cloudtales-narrator'
param location = 'swedencentral'
param speechAccountName = 'cloudtales-speech-gr'
param audioStorageAccountName = 'stcloudtalesnarration'

param allowedAudioHosts = 'cloudtales.gr,www.cloudtales.gr'

param siteName = 'CloudTales'
param spokenAddress = 'CloudTales dot G R'
param siteFeedUrl = 'https://cloudtales.gr/narration.json'
param contactSender = 'contact@cloudtales.gr'
param contactRecipient = 'vdionisop@cloudtales.gr'
param contactAllowedOrigins = 'https://cloudtales.gr,https://www.cloudtales.gr'
