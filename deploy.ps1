<#
.SYNOPSIS
  Builds the Functions project and deploys it to the Flex Consumption Function App.
.PARAMETER ResourceGroup
  Resource group the infrastructure (infra/main.bicep) was deployed to.
.PARAMETER FunctionApp
  Function App name. If omitted, it is read from the 'main' deployment's outputs.
.NOTES
  The zip is built entry by entry with '/' separators: Windows PowerShell's Compress-Archive
  and ZipFile.CreateFromDirectory write '\', which Linux hosts do not treat as folders.
#>
param(
    [Parameter(Mandatory)]
    [string]$ResourceGroup,
    [string]$FunctionApp
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $FunctionApp) {
    $FunctionApp = az deployment group show -g $ResourceGroup -n main --query properties.outputs.functionAppName.value -o tsv
    if (-not $FunctionApp) { throw "Function App name not found; pass -FunctionApp or deploy infra/main.bicep first." }
}

$publishDir = Join-Path $PSScriptRoot "publish"
$zipPath    = Join-Path $PSScriptRoot "publish.zip"

Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# Full rebuild: never trust incremental state for a deployment artifact
dotnet build src/CloudTales.Narrator.Functions -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

dotnet publish src/CloudTales.Narrator.Functions -c Release -o $publishDir --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    Get-ChildItem $publishDir -Recurse -File -Force | ForEach-Object {
        $entry = $_.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry)
    }
}
finally {
    $zip.Dispose()
}

# Flex Consumption runs the package from its deployment container. Uploading it there and restarting is
# the same thing the deployment API does, without the SCM endpoint (which failed through some networks).
# Needs Storage Blob Data Contributor on the host storage account for the signed-in user.
$deployStorage = az resource show -g $ResourceGroup -n $FunctionApp --resource-type Microsoft.Web/sites `
    --query properties.functionAppConfig.deployment.storage.value -o tsv
if (-not $deployStorage) { throw "Deployment storage not found: is $FunctionApp a Flex Consumption app?" }
$deployUri = [Uri]$deployStorage
$account   = $deployUri.Host.Split('.')[0]
$container = $deployUri.AbsolutePath.Trim('/')

az storage blob upload --account-name $account -c $container -n released-package.zip -f $zipPath `
    --auth-mode login --overwrite --only-show-errors -o none
if ($LASTEXITCODE -ne 0) { throw "Package upload failed" }

az functionapp restart -g $ResourceGroup -n $FunctionApp
if ($LASTEXITCODE -ne 0) { throw "Restart failed" }

Write-Host "Deployed to https://$FunctionApp.azurewebsites.net" -ForegroundColor Green
