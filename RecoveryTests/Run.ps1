param(
    [string]$Compiler = 'D:\Desktop\workplace\VS2022Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('results-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '..\SW2URDF\bin\x64\Release'
if (!(Test-Path -LiteralPath (Join-Path $bin 'SW2URDF.dll'))) { throw 'Run Build-Independent.ps1 first.' }
$testSources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*Tests.cs' -File
& $Compiler /nologo /target:exe /platform:x64 "/out:$bin\DraftRecoveryTests.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll "/r:$bin\SW2URDF.dll" $testSources.FullName
if ($LASTEXITCODE -ne 0) { throw 'Recovery test compilation failed.' }
& "$bin\DraftRecoveryTests.exe" $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Recovery tests failed.' }
