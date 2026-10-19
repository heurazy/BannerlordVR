[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$output = Join-Path $repo 'dist'
$stage = Join-Path $output ('physics-preview-' + $stamp)
$bin = Join-Path $stage 'Modules/BannerlordVR/bin/Win64_Shipping_Client'

# Stage an allowlist from this build, without touching the installed game.
$artifacts = @('build/managed/BannerlordVR.dll', 'build/native/BannerlordVR.Native.dll')
foreach ($relative in $artifacts) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $relative))) { throw "Missing build artifact: $relative" }
}
New-Item -ItemType Directory -Path $bin -Force | Out-Null
foreach ($relative in $artifacts) { Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $bin }
Copy-Item -LiteralPath (Join-Path $repo 'module/SubModule.xml') -Destination (Join-Path $stage 'Modules/BannerlordVR')
foreach ($relative in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'docs/physical-enemies.md', 'docs/native-ragdoll-findings.md', 'config/EnemyPhysics.preview.cfg')) {
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $stage
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output ('BannerlordVR-physics-preview-' + $stamp + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Write-Output "Experimental preview (not validated in VR): $zip"
