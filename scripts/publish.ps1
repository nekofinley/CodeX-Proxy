param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\publish\$Runtime"

dotnet publish (Join-Path $root 'src\CodeXProxy\CodeXProxy.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=$($SelfContained.IsPresent) `
    -o $output

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Published to $output"

