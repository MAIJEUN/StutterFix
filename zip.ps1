# dist/<edition>/StutterFix 폴더를 zip 으로 묶는다. 경로 구분자를 '/' 로 쓰려고 .NET ZipFile 을 쓴다
# (PowerShell 5.1 의 Compress-Archive 는 '\' 로 넣어서 일부 압축 해제기가 폴더로 못 읽는다).
param([string]$Version)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = (Resolve-Path "$PSScriptRoot/dist").Path
foreach ($e in 'player', 'developer') {
    $zip = Join-Path $dist "StutterFix-$Version-$e.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    $src = Join-Path $dist "$e/StutterFix"
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($f in Get-ChildItem $src -File) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, "StutterFix/" + $f.Name, 'Optimal') | Out-Null
        }
    } finally { $archive.Dispose() }
}
