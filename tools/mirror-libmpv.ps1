# Mirrors an upstream libmpv archive into our own "libmpv" GitHub release, so CI never
# depends on zhongfly/mpv-winbuild's release retention. Run after deciding on a bump
# (playback-tested!), then point get-libmpv.ps1's $Url at the printed mirror URL with the
# printed SHA256. Requires gh (authenticated with push access to TroelsSjostedt/wintube).
param(
    [Parameter(Mandatory = $true)]
    [string]$UpstreamUrl   # e.g. https://github.com/zhongfly/mpv-winbuild/releases/download/<tag>/mpv-dev-x86_64-<date>-git-<sha>.7z
)
$ErrorActionPreference = "Stop"
$repo = "TroelsSjostedt/wintube"

$name = Split-Path $UpstreamUrl -Leaf
$file = Join-Path $env:TEMP $name
Invoke-WebRequest $UpstreamUrl -OutFile $file
$hash = (Get-FileHash $file -Algorithm SHA256).Hash

# One rolling "libmpv" release holds every mirrored archive; create it on first use.
gh release view libmpv --repo $repo *> $null
if ($LASTEXITCODE -ne 0) {
    gh release create libmpv --repo $repo --title "libmpv mirror" --latest=false --notes `
        "Mirrored libmpv dev archives so CI never depends on upstream release retention. Source: https://github.com/zhongfly/mpv-winbuild (GPL, like WinTube itself). Current pin: tools/get-libmpv.ps1."
}
gh release upload libmpv $file --repo $repo

Remove-Item $file
Write-Host ""
Write-Host "Mirrored. Update tools/get-libmpv.ps1 to:"
Write-Host "  `$Url = `"https://github.com/$repo/releases/download/libmpv/$name`""
Write-Host "  `$Sha256 = `"$hash`""
