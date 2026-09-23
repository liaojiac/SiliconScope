# build_and_test.ps1 — Windows PowerShell 构建与测试脚本
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  CPU 体质评分软件 — 构建与测试 (PowerShell)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# 检查 .NET SDK
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[错误] 未检测到 .NET 8 SDK" -ForegroundColor Red
    Write-Host "  请安装: https://dotnet.microsoft.com/download/dotnet/8.0"
    pause
    exit 1
}

Write-Host "[1/4] 还原依赖..." -ForegroundColor Green
dotnet restore
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "[2/4] 构建全部项目..." -ForegroundColor Green
dotnet build --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "[3/4] 运行采集层验证程序..." -ForegroundColor Green
dotnet run --project 采集层\src\Collector --configuration Release --no-build
Write-Host ""

Write-Host "[4/4] 运行单元测试..." -ForegroundColor Green
dotnet run --project 采集层\tests --configuration Release --no-build
Write-Host ""

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  完成。" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
pause
