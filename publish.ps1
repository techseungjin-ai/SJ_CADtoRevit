$ErrorActionPreference = "Stop"

Write-Host "1. Building plugin project..."
dotnet build -c Release SJ_CADtoRevit\SJ_CADtoRevit.csproj

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

Write-Host "2. Zipping plugin bundle..."
$zipPath = "SJ_CADtoRevitInstaller\bundle.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path ".\SJ_CADtoRevit\SJ_CADtoRevit.bundle" -DestinationPath $zipPath -Force

Write-Host "3. Building C# Setup Installer..."
if (Test-Path "SJ_CADtoRevit_Setup.exe") {
    Remove-Item "SJ_CADtoRevit_Setup.exe" -Force
}

$publishOutputDir = ".\publish_output"
if (Test-Path $publishOutputDir) {
    Remove-Item $publishOutputDir -Recurse -Force
}

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true .\SJ_CADtoRevitInstaller\SJ_CADtoRevitInstaller.csproj -o $publishOutputDir

if (Test-Path "$publishOutputDir\SJ_CADtoRevitInstaller.exe") {
    Move-Item -Path "$publishOutputDir\SJ_CADtoRevitInstaller.exe" -Destination ".\SJ_CADtoRevit_Setup.exe" -Force
}

if (Test-Path $publishOutputDir) {
    Remove-Item $publishOutputDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

if (-not (Test-Path "SJ_CADtoRevit_Setup.exe")) {
    Write-Error "Setup.exe generation failed!"
    exit 1
}

Write-Host "Success: Setup.exe generated successfully."
