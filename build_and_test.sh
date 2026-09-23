#!/usr/bin/env bash
# build_and_test.sh — Linux / macOS 构建脚本（CI 友好）
set -euo pipefail

echo "============================================"
echo "  CPU 体质评分软件 — 构建与测试"
echo "============================================"
echo ""

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[错误] 未检测到 .NET 8 SDK"
    echo "  请安装: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
fi

echo "[1/4] 还原依赖..."
dotnet restore

echo "[2/4] 构建全部项目..."
dotnet build --configuration Release --no-restore

echo "[3/4] 运行采集层验证程序..."
dotnet run --project 采集层/src/Collector --configuration Release --no-build

echo "[4/4] 运行单元测试..."
dotnet run --project 采集层/tests --configuration Release --no-build

echo ""
echo "============================================"
echo "  完成。"
echo "============================================"
