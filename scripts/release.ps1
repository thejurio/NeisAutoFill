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

# ── 3. Velopack 패키징 ────────────────────────────────────────
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

Get-ChildItem Releases | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize

if ($DryRun) { Write-Host "▶ DryRun — 업로드 생략" -ForegroundColor Yellow; exit 0 }

# ── 4. GitHub 릴리스 업로드 ───────────────────────────────────
Write-Host "▶ GitHub 업로드 (v$version)" -ForegroundColor Cyan
vpk upload github `
    --repoUrl https://github.com/thejurio/NeisAutoFill `
    --tag "v$version" `
    --releaseName "v$version" `
    --merge --publish
if ($LASTEXITCODE -ne 0) { throw "업로드 실패" }

Write-Host "✅ 릴리스 완료: https://github.com/thejurio/NeisAutoFill/releases/tag/v$version" -ForegroundColor Green
