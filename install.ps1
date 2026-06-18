$ErrorActionPreference = "Stop"

# Define paths
$sourceBundle = Join-Path $PSScriptRoot "SJ_CADtoRevit\SJ_CADtoRevit.bundle"
$appDataFolder = [System.Environment]::GetFolderPath('ApplicationData')
$autodeskPluginsFolder = Join-Path $appDataFolder "Autodesk\ApplicationPlugins"
$targetBundle = Join-Path $autodeskPluginsFolder "SJ_CADtoRevit.bundle"

Write-Host "AutoCAD용 SJ_CADtoRevit 플러그인 설치를 시작합니다..." -ForegroundColor Cyan

# Create Autodesk plugins folder if it doesn't exist
if (-not (Test-Path $autodeskPluginsFolder)) {
    New-Item -ItemType Directory -Force -Path $autodeskPluginsFolder | Out-Null
    Write-Host "자동 로드 디렉토리 생성 완료: $autodeskPluginsFolder" -ForegroundColor Gray
}

# Remove existing installation if present
if (Test-Path $targetBundle) {
    Write-Host "기존 설치 버전 제거 중..." -ForegroundColor Yellow
    Remove-Item -Path $targetBundle -Recurse -Force
}

# Copy the bundle
Write-Host "플러그인 파일 복사 중..." -ForegroundColor Gray
Copy-Item -Path $sourceBundle -Destination $targetBundle -Recurse -Force

Write-Host "--------------------------------------------------------" -ForegroundColor Green
Write-Host "설치 성공! SJ_CADtoRevit 플러그인이 성공적으로 등록되었습니다." -ForegroundColor Green
Write-Host "AutoCAD 2027을 시작하면 자동으로 메뉴 상단에 [SJ_CADtoRevit] 리본 탭이 나타납니다." -ForegroundColor White
Write-Host "  1. 리본 탭에서 [Crop & Clean] 버튼 클릭" -ForegroundColor Yellow
Write-Host "  2. 또는 명령 창에 'SJ_CROP_TRIM' 입력" -ForegroundColor Yellow
Write-Host "--------------------------------------------------------" -ForegroundColor Green
