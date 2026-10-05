<#
.SYNOPSIS
  Builds the Functions project and deploys it to the Flex Consumption Function App.
.NOTES
  The zip is built entry by entry with '/' separators: Windows PowerShell's Compress-Archive
  and ZipFile.CreateFromDirectory write '\', which Linux hosts do not treat as folders.
#>
param(
    [string]$ResourceGroup = "rg-cloudtales-tts",
    [string]$FunctionApp   = "func-cloudtales-narrator-nwdwpxyw52omi"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

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

az functionapp deployment source config-zip -g $ResourceGroup -n $FunctionApp --src $zipPath
if ($LASTEXITCODE -ne 0) { throw "Deployment failed" }

Write-Host "Deployed to https://$FunctionApp.azurewebsites.net" -ForegroundColor Green
