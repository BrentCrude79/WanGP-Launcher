# Builds "WanGP Launcher.exe" next to this script using the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $PSScriptRoot 'WanGPLauncher.cs'
$ico = Join-Path $PSScriptRoot 'wangp.ico'
$out = Join-Path $PSScriptRoot 'WanGP Launcher.exe'
$refs = '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.Web.Extensions.dll'

if (-not (Test-Path $ico)) {
    # The launcher draws its own icon, so build it once without one to generate wangp.ico.
    $tmp = Join-Path $env:TEMP 'wangp-launcher-icon.exe'
    & $csc /nologo /target:winexe /optimize+ $refs "/out:$tmp" $src
    if ($LASTEXITCODE) { throw 'Compile failed' }
    Start-Process -FilePath $tmp -ArgumentList '--make-icon', "`"$ico`"" -Wait
    Remove-Item $tmp
}

& $csc /nologo /target:winexe /optimize+ $refs "/win32icon:$ico" "/out:$out" $src
if ($LASTEXITCODE) { throw 'Compile failed' }
"Built $out"
