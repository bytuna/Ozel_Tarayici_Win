$ErrorActionPreference = "Stop"
$sourceExe = Join-Path $PSScriptRoot "publish\OzelTarayici.exe"
if (-not (Test-Path $sourceExe)) {
    Write-Error "OzelTarayici.exe bulunamadi: $sourceExe"
    exit 1
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\OzelTarayici"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$targetExe = Join-Path $installDir "OzelTarayici.exe"
Copy-Item $sourceExe $targetExe -Force

$wsh = New-Object -ComObject WScript.Shell
$desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Özel Tarayıcı.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$startLnk = Join-Path $startMenuDir "Özel Tarayıcı.lnk"

foreach ($lnkPath in @($desktopLnk, $startLnk)) {
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = $targetExe
    $sc.WorkingDirectory = $installDir
    $sc.IconLocation = $targetExe
    $sc.Description = "Özel Tarayıcı"
    $sc.Save()
}

Write-Host "Kurulum tamamlandi: $targetExe" -ForegroundColor Green
