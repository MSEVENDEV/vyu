# Builds the portable release: dist/Vyu-<version>-win-x64.zip (self-contained, no .NET install needed).
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & "$PSScriptRoot\fetch-libmpv.ps1"
    $out = Join-Path $root 'out\Vyu'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish src/Vyu/Vyu.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md -Destination $out
    New-Item -ItemType Directory -Force dist | Out-Null
    $zip = Join-Path $root "dist\Vyu-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally { Pop-Location }
