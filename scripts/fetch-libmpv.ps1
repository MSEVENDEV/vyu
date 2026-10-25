# Downloads libmpv-2.dll (shinchiro's mpv-winbuild-cmake, x86_64) into deps/mpv.
# The build is the one linked from mpv.io's installation page.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'deps\mpv'
if (Test-Path (Join-Path $dest 'libmpv-2.dll')) { Write-Host "libmpv already in $dest"; return }

$releases = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases?per_page=3' -Headers @{ 'User-Agent' = 'vyu-build' }
$asset = $releases | ForEach-Object { $_.assets } | Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z$' } | Select-Object -First 1
if (-not $asset) { throw "No mpv-dev-x86_64 build found" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("vyu-mpv-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
$archive = Join-Path $tmp $asset.name
Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $archive
Push-Location $tmp
try {
    $7z = Get-Command 7z -ErrorAction SilentlyContinue
    if ($7z) { & $7z.Source x -y $asset.name libmpv-2.dll | Out-Null } else { tar -xf $asset.name libmpv-2.dll }
}
finally { Pop-Location }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $tmp 'libmpv-2.dll') $dest
Remove-Item $tmp -Recurse -Force
Write-Host "libmpv ready in $dest"
