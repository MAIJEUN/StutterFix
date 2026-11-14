param(
    [string]$TaskMsvc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207',
    [string]$TaskSdk = 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0',
    [string]$TaskSdkLib = 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp.x64\c'
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskOut=Join-Path $taskRoot 'out/fillcensus'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
$taskOldPath=$env:PATH; $taskOldInclude=$env:INCLUDE; $taskOldLib=$env:LIB
try {
    $env:PATH="$TaskMsvc\bin\Hostx64\x64;$env:PATH"
    $env:INCLUDE="$TaskMsvc\include;$TaskSdk\ucrt;$TaskSdk\um;$TaskSdk\shared;$TaskSdk\winrt"
    $env:LIB="$TaskMsvc\lib\x64;$TaskSdkLib\ucrt\x64;$TaskSdkLib\um\x64"
    & cl /nologo /std:c++17 /EHsc /W4 /WX "$PSScriptRoot/fill-pipeline-fixture.cpp" "/Fo$taskOut/pipeline.obj" "/Fe$taskOut/pipeline.exe" *> "$taskOut/pipeline-build.txt"
    if($LASTEXITCODE) {Get-Content "$taskOut/pipeline-build.txt";throw 'Fixture compilation failed'}
    & "$taskOut/pipeline.exe" *> "$taskOut/pipeline-fixture.txt"
    if($LASTEXITCODE) {Get-Content "$taskOut/pipeline-fixture.txt";throw 'Fixture failed'}
    Get-Content "$taskOut/pipeline-fixture.txt"
} finally {$env:PATH=$taskOldPath;$env:INCLUDE=$taskOldInclude;$env:LIB=$taskOldLib}
