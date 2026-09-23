# 采集层

> 📘 真机对接完整指引见同级文件 `LHM字段映射与采集校准手册.md`（含字段映射表、校准流程、自检清单）。

（Collector）

硬件数据采集层，**对接真实 LibreHardwareMonitor (LHM)**，对应《采集SDK设计 V1.1》§3-§6。

## 定位

「**LHM 主采集 + HWiNFO SHM 增强（双源）**」，产出 `HardwareFeatures` → 喂给评分引擎 `ScoreInput`。
这是「硬件 → 可评分数据」的入口，也是**最容易出 bug 的一层**（假大雕几乎都源于此）。

## 核心机制（均已用 Python 验收脚本 + C# 单测验证）

| 机制 | 说明 | 防什么 |
|------|------|--------|
| **环境自检** | 检测「核心隔离/内存完整性」 | LHM 电压归零 → 假大雕 |
| **管理员检测** | 读电压/功耗必需提权 | 传感器缺失 |
| **质控门禁** | 电压 0.2-1.5V、温度 0-100℃ | 异常值污染评分 |
| **双源合并** | LHM 基础 + HWiNFO 补充每核 VID/有效频率/CCD 温度 | 数据不全 |
| **HWiNFO 降级** | 未启用/未安装时仅 LHM | 依赖可选 |

## 对接真实 LHM（下一步落地要点）

`Collector.cs` 的 `LhmCollector.Collect()` 目前是**可编译骨架**（返回 null），需在有 AMD CPU 的 Windows 机器上补全：

```csharp
// 1. 安装 NuGet 包
//    dotnet add package LibreHardwareMonitorLib

// 2. 遍历传感器（参考 scoring_collect.py 字段映射）
var computer = new Computer
{
    IsCpuEnabled = true,
    IsMotherboardEnabled = true,
    IsGpuEnabled = true
};
computer.Open();
computer.Accept(new UpdateVisitor());
foreach (var hw in computer.Hardware) { /* 按 SensorType + Name 匹配 */ }
```

**字段映射**（详见《采集SDK设计》§3.2）：
- `SensorType.Voltage` + Name 含 "Core" → VCore
- `SensorType.Clock` + Name 含 "Core #" → 每核频率
- `SensorType.Temperature` + Name 含 "Package"/"CCD" → 温度
- HWiNFO SHM：每核 VID / 有效频率 / CCD 温度（LHM 不完整的差异化字段）

## 环境自检实现要点

```csharp
// 检测「内存完整性 / 核心隔离」是否已开启
// 注册表路径（Windows 10/11）：
// HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios
//      \HypervisorEnforceCodeIntegrity\Enabled  = 1 → 已开启
//
// 若 Enabled=1，LHM 的 Ring0 驱动被阻，CPU 电压/功耗会归零。
// 此时 Collect() 必须抛异常并提示用户关闭，绝不能继续评分。
```

## 快速验证（无需 .NET / Windows）

> 一键全量回归（推荐，覆盖采集层 + 评分引擎共 125 用例）：
```bash
python run_all.py
```
预期：`🎉 全量回归通过`

也可以只跑采集层：
```bash
cd 采集层
python run_all_tests.py
```
预期 7 项全绿：命名校准 / 跨主板兼容 / 探测模式 / **日志·诊断·导出** / 真实场景 / 端到端 / C# 结构校验。

## 日志 / 诊断 / 导出

出问题靠日志定位——三层职责分离：

| 层 | 输出 | 位置 |
|----|------|------|
| 结构化日志 | 按天切分的 JSON 行（含级别/上下文/异常） | `logs/yyyyMMdd.log` |
| 诊断报告 | 单次采集完整快照（传感器原始 dump + 字段匹配 + 评分） | `reports/diagnostic_*.json`（保留最近 10） |
| 评分解导出 | JSON（程序用）+ Markdown（人读，含核心分布柱状图） | `导出/` |
| 诊断包 | 一键打包：日志 + 报告 + 缓存 | `诊断包_时间戳.zip` |

**真机出问题时**：菜单「导出诊断包」→ 把 `诊断包_*.zip` 发来即可定位，无需复现。
详见 `../docs/archive/日志诊断导出迭代说明_V1.6.4.md`。

## 在 Windows 上编译运行

```powershell
# 需管理员权限运行 PowerShell
dotnet build src/Collector/Collector.csproj -c Release
dotnet test tests/Collector.Tests.csproj
dotnet run --project src/Collector/Collector.csproj
```

## ⚠️ 诚实提示

- 当前 `Collector.cs` 为**骨架**，真实传感器读取需在 Windows + AMD 硬件上补全并校准
- HWiNFO SHM 接口为**用户自备、手动启用**，不捆绑 SDK（规避授权）
- 「核心隔离检测」注册表路径需在真实系统上验证兼容性

---
*配套：《采集SDK设计 V1.1》《评分算法详细设计 V1.0》《内置基准量表 V1.0.xlsx》*
