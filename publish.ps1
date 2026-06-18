# AutoCAD_ 프로젝트 폴더 기준 빌드 및 배포 자동화 스크립트

Write-Host "1. Release 모드로 프로젝트 빌드 중..." -ForegroundColor Cyan
dotnet build -c Release SJ_CADtoRevit\SJ_CADtoRevit.csproj

if ($LASTEXITCODE -ne 0) {
    Write-Error "빌드 실패!"
    exit 1
}

Write-Host "2. 7z SFX 설치 프로그램 구성을 위한 config.txt 생성 중..." -ForegroundColor Cyan
$configContent = @'
;!@Install@!UTF-8!
Title="SJ_CADtoRevit 플러그인 설치"
BeginPrompt="AutoCAD 2026/2027용 SJ_CADtoRevit 플러그인을 설치하시겠습니까?"
ExtractPath="%%APPDATA%%\Autodesk\ApplicationPlugins"
ExtractTitle="설치 파일을 대상 경로로 복사하는 중..."
;!@InstallEnd@!
'@

# SFX용 config.txt는 UTF-8(BOM 없음)로 저장하는 것이 한글이 안 깨지도록 보장하는 가장 확실한 방법입니다.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines("SJ_CADtoRevit\config.txt", $configContent, $utf8NoBom)

Write-Host "3. 플러그인 번들을 7z로 압축 중..." -ForegroundColor Cyan
if (Test-Path "SJ_CADtoRevit\SJ_CADtoRevit.7z") {
    Remove-Item "SJ_CADtoRevit\SJ_CADtoRevit.7z" -Force
}

# 7-zip 압축 실행
& "C:\Program Files\7-Zip\7z.exe" a -t7z "SJ_CADtoRevit\SJ_CADtoRevit.7z" ".\SJ_CADtoRevit\SJ_CADtoRevit.bundle"

if ($LASTEXITCODE -ne 0) {
    Write-Error "7z 압축 실패!"
    exit 1
}

Write-Host "4. 자가 해제 실행 파일 (SJ_CADtoRevit_Setup.exe) 생성 중..." -ForegroundColor Cyan
if (Test-Path "SJ_CADtoRevit_Setup.exe") {
    Remove-Item "SJ_CADtoRevit_Setup.exe" -Force
}

# 7z.sfx + config.txt + 7z파일 결합
cmd.exe /c "copy /b `"C:\Program Files\7-Zip\7z.sfx`" + SJ_CADtoRevit\config.txt + SJ_CADtoRevit\SJ_CADtoRevit.7z SJ_CADtoRevit_Setup.exe"

# 임시 파일 제거
Remove-Item "SJ_CADtoRevit\config.txt" -Force
Remove-Item "SJ_CADtoRevit\SJ_CADtoRevit.7z" -Force

if (-not (Test-Path "SJ_CADtoRevit_Setup.exe")) {
    Write-Error "Setup.exe 생성 실패!"
    exit 1
}
Write-Host "자가 해제 설치 파일 생성 완료: SJ_CADtoRevit_Setup.exe" -ForegroundColor Green

Write-Host "5. Git 저장소 검사 및 커밋 수행..." -ForegroundColor Cyan
if (-not (Test-Path ".git")) {
    git init
    git branch -M main
}

git add .
git commit -m "AutoCAD 2026/2027 Multi-DWG Extractor Release v1.0.0"

Write-Host "6. GitHub 원격 저장소 생성 및 푸시 중..." -ForegroundColor Cyan
$repoExists = $true
gh repo view techseungjin-ai/SJ_CADtoRevit 2>$null
if ($LASTEXITCODE -ne 0) {
    $repoExists = $false
}

if (-not $repoExists) {
    # 비대화형으로 GitHub 저장소 생성 및 최초 푸시
    gh repo create SJ_CADtoRevit --public --source=. --remote=origin --push
} else {
    Write-Host "GitHub 저장소가 이미 존재합니다. 변경 사항을 원격 저장소에 푸시합니다." -ForegroundColor Yellow
    $remotes = git remote
    if ($remotes -notcontains "origin") {
        git remote add origin https://github.com/techseungjin-ai/SJ_CADtoRevit.git
    }
    git push -u origin main --force
}

Write-Host "7. GitHub Release 생성 및 Setup.exe 업로드 중..." -ForegroundColor Cyan
gh release view v1.0.0 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "기존 Release v1.0.0을 삭제하고 재생성합니다." -ForegroundColor Yellow
    gh release delete v1.0.0 --yes
    git push --delete origin v1.0.0 2>$null
}

gh release create v1.0.0 .\SJ_CADtoRevit_Setup.exe --title "v1.0.0 - AutoCAD 2026/2027 지원 정식 릴리즈" --notes "AutoCAD 2026 및 2027을 동시 지원하는 다중 도곽 추출 및 크롭 자동화 도구입니다. 다운로드받은 EXE 파일을 실행하면 플러그인이 즉시 자동 설치됩니다."

Write-Host "최종 릴리즈 배포가 완료되었습니다!" -ForegroundColor Green
