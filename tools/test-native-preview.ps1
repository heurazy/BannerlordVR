$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native = (Join-Path $repo 'build/native/BannerlordVR.Native.dll').Replace('\', '\\')
$source = @"
using System;
using System.Runtime.InteropServices;
public static class BvrNativePreviewSmoke {
    [DllImport("$native", CallingConvention=CallingConvention.Cdecl)]
    public static extern int bvr_get_version(byte[] output, int length);
    [DllImport("$native", CallingConvention=CallingConvention.Cdecl)]
    public static extern int bvr_get_input_state(byte[] output);
    [DllImport("$native", CallingConvention=CallingConvention.Cdecl)]
    public static extern int bvr_trigger_haptic(int hand, float amplitude, float duration);
}
"@
Add-Type -TypeDefinition $source
$versionBuffer = New-Object byte[] 128
[void][BvrNativePreviewSmoke]::bvr_get_version($versionBuffer, $versionBuffer.Length)
$version = [Text.Encoding]::UTF8.GetString($versionBuffer).TrimEnd([char]0)
if (-not $version.Contains('openxr')) { throw "Expected real OpenXR build, got $version" }
$inputBuffer = New-Object byte[] 184
if ([BvrNativePreviewSmoke]::bvr_get_input_state($inputBuffer) -ne -5) { throw 'Inactive input must report session not ready' }
if ([BvrNativePreviewSmoke]::bvr_trigger_haptic(0, 0.5, 0.035) -ne -1) { throw 'Haptics must reject an uninitialized session' }
Write-Output "PASS: native DLL loads, OpenXR build identified, inactive input and haptic guards verified ($version)."
