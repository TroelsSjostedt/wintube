# Fetches the pinned libmpv build WinTube links against and unpacks libmpv-2.dll
# into libs/mpv/ (gitignored). The archive is OUR mirror of an upstream
# zhongfly/mpv-winbuild build (upstream prunes old releases) — upgrading mpv =
# playback-test the new build, run tools/mirror-libmpv.ps1 on the upstream URL,
# then change $Url and $Sha256 together to what it prints.
$ErrorActionPreference = "Stop"

$Url = "https://github.com/TroelsSjostedt/wintube/releases/download/libmpv/mpv-dev-x86_64-20261103-git-8ca245f213.7z"
$Sha256 = "CC4A030DC9C3F17763C6898DF644F304651C83AD7ECAF732B8972741C5A5B7FC"

$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "libs/mpv"
$dll = Join-Path $dest "libmpv-2.dll"
$marker = Join-Path $dest "version.txt"

if ((Test-Path $dll) -and (Test-Path $marker) -and (Get-Content $marker) -eq $Sha256) {
    Write-Host "libmpv already present and pinned; nothing to do."
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null
$archive = Join-Path $dest "libmpv.7z"
Invoke-WebRequest $Url -OutFile $archive

$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw "libmpv archive hash mismatch: expected $Sha256, got $actual" }

# Windows has no built-in .7z extractor; require 7-Zip with a clear message.
$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) {
    # Some installs put 7z.exe here without ever adding it to PATH.
    $fallback = "C:\Program Files\7-Zip\7z.exe"
    if (Test-Path $fallback) { $sevenZip = $fallback }
}
if (-not $sevenZip) { throw "7z not found on PATH. Install with: winget install 7zip.7zip" }
& $sevenZip e $archive -o"$dest" "libmpv-2.dll" -r -y | Out-Null

if (-not (Test-Path $dll)) { throw "libmpv-2.dll not found in archive" }
Set-Content $marker $Sha256
Remove-Item $archive
Write-Host "libmpv-2.dll unpacked to libs/mpv/"
