$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$out = Join-Path $root 'out\regressions'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe = Join-Path $root 'bin\regressiontests.exe'
& $csc -nologo -langversion:7.3 -target:exe "-out:$exe" -nostdlib+ "-r:$fw\mscorlib.dll" "-r:$fw\System.dll" "-r:$fw\System.Core.dll" "-r:$fw\System.Drawing.dll" "-r:$root\bin\NextScan.Engine.dll" "$root\src\App\StudioExport.cs" "$PSScriptRoot\RegressionTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Regression test compilation failed' }
$fake = Join-Path $out 'NextScan.Host32.exe'
& $csc -nologo -target:exe "-out:$fake" "$PSScriptRoot\FakeHost.cs"
if ($LASTEXITCODE -ne 0) { throw 'Fake host compilation failed' }
& $exe $out
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
