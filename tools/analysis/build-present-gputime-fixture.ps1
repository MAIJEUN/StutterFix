param(
    [string]$TaskMsvc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207',
    [string]$TaskSdk = 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0',
    [string]$TaskSdkLib = 'C:\SFBundle\winsdk\microsoft.windows.sdk.cpp.x64\c'
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskOut=Join-Path $taskRoot 'out/present-gputime'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
$taskOldPath=$env:PATH; $taskOldInclude=$env:INCLUDE; $taskOldLib=$env:LIB
try {
    $env:PATH="$TaskMsvc\bin\Hostx64\x64;$env:PATH"
    $env:INCLUDE="$TaskMsvc\include;$TaskSdk\ucrt;$TaskSdk\um;$TaskSdk\shared;$TaskSdk\winrt"
    $env:LIB="$TaskMsvc\lib\x64;$TaskSdkLib\ucrt\x64;$TaskSdkLib\um\x64"
    & cl /nologo /std:c++17 /EHsc /W4 /WX "$PSScriptRoot/present-gputime-fixture.cpp" "/Fo$taskOut/fixture.obj" "/Fe$taskOut/fixture.exe" *> "$taskOut/fixture-build.txt"
    if($LASTEXITCODE) {Get-Content "$taskOut/fixture-build.txt";throw 'Fixture compilation failed'}
    & "$taskOut/fixture.exe" *> "$taskOut/fixture.txt"
    if($LASTEXITCODE) {Get-Content "$taskOut/fixture.txt";throw 'Fixture failed'}
    Get-Content "$taskOut/fixture.txt"
} finally {$env:PATH=$taskOldPath;$env:INCLUDE=$taskOldInclude;$env:LIB=$taskOldLib}
