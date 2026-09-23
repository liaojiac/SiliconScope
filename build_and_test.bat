@echo off
chcp 65001 >nul
echo ============================================
echo  CPU 体质评分软件 — 构建与测试
echo ============================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 未检测到 .NET 8 SDK，请先安装：
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/4] 还原依赖...
dotnet restore
if errorlevel 1 exit /b 1

echo.
echo [2/4] 构建全部项目...
dotnet build --configuration Release --no-restore
if errorlevel 1 exit /b 1

echo.
echo [3/4] 运行采集层验证程序...
dotnet run --project 采集层\src\Collector --configuration Release --no-build
echo.

echo [4/4] 运行单元测试...
dotnet run --project 采集层\tests --configuration Release --no-build
echo.

echo ============================================
echo  完成。如需真机采集，请确保：
echo   1. 以管理员身份运行
echo   2. 关闭「核心隔离 / 内存完整性」
echo   3. 在 Collector.cs 中取消注释真实采集代码块
echo ============================================
pause
