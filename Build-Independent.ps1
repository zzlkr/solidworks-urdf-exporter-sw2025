param(
    [string]$MSBuild = 'D:\Desktop\workplace\VS2022Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$SolidWorksDir = 'D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS',
    [string]$InnoCompiler,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'release')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'packages\xunit.core.2.4.1\build\xunit.core.props'))) {
    throw 'Restore packages first: nuget restore SW2URDF.sln'
}
& $MSBuild (Join-Path $PSScriptRoot 'SW2URDF\SW2URDF.csproj') /t:Build /p:Configuration=Release /p:Platform=x64 "/p:SolidWorksDir=$SolidWorksDir" /p:RunCodeAnalysis=false /p:BuildProjectReferences=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'SW2URDF compilation failed.' }
if ($InnoCompiler) {
    & $InnoCompiler --no-ide-signtools "--output-dir=$OutputDirectory" (Join-Path $PSScriptRoot 'INSTALL\IndependentWindow.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
