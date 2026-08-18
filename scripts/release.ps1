<#
  로컬 릴리스 스크립트 (Velopack) — v1.6.7~

  하는 일: 테스트 → 게시 → Velopack 패키징(설치형 Setup.exe + 포터블 zip) → GitHub 릴리스 업로드.
  델타 패키지는 vpk 가 직전 릴리스와 비교해 자동 생성한다(GitHub 에서 옛 패키지를 내려받아 비교).

  사용법:
      pwsh scripts/release.ps1              # csproj 의 <Version> 을 그대로 사용
      pwsh scripts/release.ps1 -DryRun      # 업로드 없이 패키지까지만 (검증용)

  사전 준비(1회): dotnet tool install -g vpk   ·   gh auth login
#>
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

$csproj = 'src/NeisAutoFill.App/NeisAutoFill.App.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "csproj 에서 <Version> 을 찾지 못했습니다." }
Write-Host "▶ 버전 $version" -ForegroundColor Cyan

# ── 1. 테스트 (깨진 상태로 릴리스하지 않는다) ─────────────────
Write-Host "▶ 테스트" -ForegroundColor Cyan
dotnet test tests/NeisAutoFill.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "테스트 실패 — 릴리스 중단" }

# ── 2. 게시 (self-contained, 다중 파일 — 단일 exe 금지) ───────
$pub = "publish/NeisAutoFill"
Write-Host "▶ 게시 → $pub" -ForegroundColor Cyan
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish src/NeisAutoFill.App -c Release -r win-x64 --self-contained true -o $pub
if ($LASTEXITCODE -ne 0) { throw "게시 실패" }

# 나이스 연결의 생명줄 — 없으면 배포해봐야 연결이 안 된다 (v1.3.1~1.6.5 사고)
$driver = Join-Path $pub '.playwright/node/win32_x64/node.exe'
if (-not (Test-Path $driver)) { throw "Playwright 드라이버 누락: $driver — 배포 중단" }
Write-Host "  ✓ 드라이버 확인" -ForegroundColor Green

# ── 3. 토큰 (gh 로그인에서 가져옴 — 별도 PAT 관리 불필요) ────
$token = (gh auth token).Trim()
if (-not $token) { throw "GitHub 토큰을 얻지 못했습니다 — 'gh auth login' 을 먼저 하세요." }
$repoUrl = 'https://github.com/thejurio/NeisAutoFill'

# ── 4. 이전 릴리스 받아오기 (델타 생성의 전제) ────────────────
# vpk 는 Releases/ 안의 직전 버전 패키지와 비교해 델타를 만든다. 없으면 전체 패키지만 나온다.
Write-Host "▶ 이전 릴리스 내려받기 (델타 기준)" -ForegroundColor Cyan
if (Test-Path Releases) { Remove-Item Releases -Recurse -Force }
vpk download github --repoUrl $repoUrl --token $token
if ($LASTEXITCODE -ne 0) { Write-Host "  (이전 릴리스 없음 — 전체 패키지만 생성)" -ForegroundColor Yellow }

# ── 5. Velopack 패키징 ────────────────────────────────────────
Write-Host "▶ Velopack 패키징" -ForegroundColor Cyan
vpk pack `
    --packId NeisAutoFill `
    --packVersion $version `
    --packDir $pub `
    --mainExe NeisAutoFill.App.exe `
    --packTitle "NEIS 교과평가 자동입력기" `
    --packAuthors "thejurio" `
    --icon assets/app.ico
if ($LASTEXITCODE -ne 0) { throw "vpk pack 실패" }

Get-ChildItem Releases | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize

if ($DryRun) { Write-Host "▶ DryRun — 업로드 생략" -ForegroundColor Yellow; exit 0 }

# ── 6. GitHub 릴리스 업로드 ───────────────────────────────────
Write-Host "▶ GitHub 업로드 (v$version)" -ForegroundColor Cyan
vpk upload github `
    --repoUrl $repoUrl `
    --token $token `
    --tag "v$version" `
    --releaseName "v$version" `
    --merge --publish
if ($LASTEXITCODE -ne 0) { throw "업로드 실패" }

Write-Host "✅ 릴리스 완료: https://github.com/thejurio/NeisAutoFill/releases/tag/v$version" -ForegroundColor Green
