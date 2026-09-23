# LHM 字段映射与采集校准手册

> 配套：`Collector.cs` + `Collector.Real.cs`（真实采集）| 目标：在 Windows + AMD 机器上补全并校准采集
> 适用：Libre Hardware Monitor **0.9.6+**（`LibreHardwareMonitorLib` NuGet 包）
> 版本：**V1.5（命名校准版）** —— 关键词已按 LHM 源码 `Amd17Cpu.cs` 真实命名修正

## ⚠️ 读我第一：V1.5 校准修正（重要）

V1.3/V1.4 手册里写的关键词是**经验性错误**，已在 V1.5 修正。若你按旧版写代码，**会在真机上采到错误数据还不自知**：

| 字段 | ❌ 旧（错） | ✅ 新（校准后，源码确认） |
|------|------------|------------------------|
| **VCore** | `Name.Contains("Core") && !"VID"` | **精确匹配 `Name == "Core (SVI2 TFN)"`** |
| 每核 VID | `"VID"`（靠 HWiNFO）| **`"^Core #\d+ VID$"`（LHM 原生支持）** |
| 当前频率 | `"Core #0"`（单核）| **`"Cores (Average)"`（平均，更稳）** |
| 有效频率 | `"Effective"`（靠 HWiNFO）| **`"^Core #\d+ \(Effective\)$"`** |
| 封装温度 | `"Package"` | **`"Core (Tctl/Tdie)"` / `"CCDx (Tdie)"`** |

> 💡 **关键认知更正**：**LHM 原生就能读到每核 VID / 有效频率 / CCD 温度**（之前手册误写成"只有 HWiNFO 才有"）。
> 因此 **HWiNFO SHM 增强的边际价值大幅下降**，仅作为冗余校验保留。详见 `LHM传感器命名校准.md`。

## 1. 前置条件（必读）

| 条件 | 说明 |
|------|------|
| **.NET 8 SDK** | 已就位 |
| **管理员权限** | **必需**——读电压/功耗需 Ring0 驱动访问 |
| **关闭「核心隔离 / 内存完整性」** | ⚠️ 否则电压归零 → 假大雕（见《频率折算勘误.md》）|
| **可选：HWiNFO** | 仅作双源冗余校验，非必需 |

## 2. 接入步骤（校准后版）

### Step 1：安装 NuGet 包

```powershell
dotnet add package LibreHardwareMonitorLib
```
并在 `Collector.csproj` 取消注释：
```xml
<PackageReference Include="LibreHardwareMonitorLib" Version="0.9.6" />
```

### Step 2：填充真实采集（**使用本版映射规则**）

对应 `Collector.Real.cs` 的 `RealCollector.Read()`。完整实现见该文件，**此处只列核心 switch（已校准）**：

```csharp
using LibreHardwareMonitor.Hardware;
using System.Text.RegularExpressions;

// ★ 括号已转义；命名来自 Amd17Cpu.cs 真实传感器名
private static readonly Regex CoreClockRe = new(@"^Core #\d+$", RegexOptions.Compiled);
private static readonly Regex CoreEffRe   = new(@"^Core #\d+ \(Effective\)$", RegexOptions.Compiled);
private static readonly Regex CoreVidRe   = new(@"^Core #\d+ VID$", RegexOptions.Compiled);
private static readonly Regex CcdTempRe   = new(@"^CCD\d", RegexOptions.Compiled);

// 在 Computer / CpuHardware.Sensors 遍历中：
foreach (var s in hw.Sensors)
{
    if (s.Value is null) continue;
    switch (s.SensorType)
    {
        // —— 电压 ——
        case SensorType.Voltage when s.Name == "Core (SVI2 TFN)":   // ★ 精确匹配
            vCore = s.Value.Value; break;
        case SensorType.Voltage when CoreVidRe.IsMatch(s.Name):     // Core #N VID
            perCoreVids.Add(s.Value.Value); break;

        // —— 频率 ——
        case SensorType.Clock when s.Name == "Cores (Average)":     // ★ 满载判定
            avgFreq = s.Value.Value; break;
        case SensorType.Clock when CoreEffRe.IsMatch(s.Name):       // Core #N (Effective)
            perCoreEffClocks.Add(s.Value.Value); break;
        case SensorType.Clock when CoreClockRe.IsMatch(s.Name):     // Core #N
            perCoreClocks.Add(s.Value.Value); break;

        // —— 温度 ——
        case SensorType.Temperature when s.Name.Contains("Tctl/Tdie"):
            pkgTemp = s.Value.Value; break;                         // Core (Tctl/Tdie)
        case SensorType.Temperature when CcdTempRe.IsMatch(s.Name) && s.Name.Contains("Max"):
            ccdMaxTemp = Math.Max(ccdMaxTemp, s.Value.Value); break;
    }
}
```

> ⚠️ **VCore 务必精确匹配 `"Core (SVI2 TFN)"`**。
> 若用 `Contains("SVI2 TFN")`，会**把 `"SoC (SVI2 TFN)"` 也采进来**，导致体质评分用 SoC 电压——这是静默错误（已通过 `verify_naming.py` 复现并修复）。

### Step 3：访问者（LHM 官方示例，无需改动）

```csharp
public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer c) => c.Traverse(this);
    public void VisitHardware(IHardware h) { h.Update(); foreach (var s in h.SubHardware) s.Accept(this); }
    public void VisitSensor(ISensor s) { }
    public void VisitParameter(IParameter p) { }
}
```

### Step 4：环境自检（防"假大雕"第一道闸门）

```csharp
public static bool CheckEnvironment()
{
    // 读注册表：核心隔离 / 内存完整性
    // HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforceCodeIntegrity
    // Enabled=1 → 返回 false（LHM 电压会归零）
    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforceCodeIntegrity");
        if (key?.GetValue("Enabled") is int enabled && enabled == 1)
            return false;
    }
    catch { /* 读不到视为通过 */ }
    return true;
}

public static bool IsAdministrator()
{
    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
```

## 3. 字段映射表（校准后，**权威版**）

> 来源：LHM 源码 `Amd17Cpu.cs`（`Processor` + `Core` 类）+ 实测校准。
> 全部字段 LHM 原生提供，**HWiNFO 仅作可选冗余校验**。

| 你要的数据 | `SensorType` | **校准后 Name 规则** | 示例（Zen4 8核） |
|-----------|-------------|---------------------|------------------|
| **VCore（命脉）** | Voltage | **精确 `== "Core (SVI2 TFN)"`** | `Core (SVI2 TFN)` = 1.046V |
| SoC 电压（别采） | Voltage | `"SoC (SVI2 TFN)"` | 1.05V（**已排除**）|
| **每核 VID** ★ | Voltage | `^Core #\d+ VID$` | `Core #0 VID` ... `Core #7 VID` |
| **平均频率**（★满载判定）| Clock | **精确 `== "Cores (Average)"`** | 5050 MHz |
| 每核瞬时频率 | Clock | `^Core #\d+$` | `Core #0` ... `Core #7` |
| 每核有效频率 | Clock | `^Core #\d+ \(Effective\)$` | `Core #0 (Effective)` |
| 总线频率 | Clock | `"Bus Speed"` | 100 MHz |
| **封装温度** | Temperature | **`"Tctl/Tdie"`**（Zen2+）| `Core (Tctl/Tdie)` = 62℃ |
| CCD 最高温 | Temperature | `^CCD\d` + `"Max"` | `CCD1 (Tdie) Max` |
| CCD 平均温 | Temperature | `^CCD\d` + `"Average"` | `CCDs Average (Tdie)` |
| 封装功耗 | Power | `"Package"` | 45W |

### 温度命名版本差异（务必知悉）

| 代际 | 温度 Name |
|------|-----------|
| Zen / Zen+ | `Core (Tctl)` 或 `Core (Tdie)` |
| **Zen2+（Zen3/4/5）** | `Core (Tctl/Tdie)`（合并名）|
| Zen2+ 多 CCD | 另出 `CCD1 (Tdie)` / `CCD2 (Tdie)` ... |

→ 匹配用 `Contains("Tctl/Tdie")` 可兼容 Zen2+；若要兼容 Zen/Zen+，再补 `Contains("Tdie") || Contains("Tctl")`。

## 4. 真机校准流程（含双源交叉校验）

### 4.1 基准读数采集

1. BIOS 恢复默认（无超频、无 PBO）
2. 进桌面等 2 分钟稳定
3. 轻载（浏览器）+ 满载（OCCT/Prime95）各 5 分钟
4. 记录稳态段 `VCore` 均值

### 4.2 ★ 双源交叉校验（V1.5 新增，防假大雕加固）

LHM 的 VCore 来自 **SVI2 遥测**，主板同时提供 **VRM 读数**（`CPU VCore` / SuperIO）。
两者应**偏差 < 0.05V**，否则视为传感器异常：

```csharp
// 采集时同时取：
double svi2 = ...; // "Core (SVI2 TFN)"
double vrm  = ...; // 主板传感器 "CPU VCore"

if (vrm > 0 && Math.Abs(svi2 - vrm) > 0.05)
    throw new InvalidOperationException("SVI2 与 VRM 电压偏差 > 0.05V，传感器异常，拒绝评分");
```

> 这比单靠 `QualityGate` 的电压范围校验**多一道独立防线**，进一步杜绝假大雕。

### 4.3 V_ref 校准

- 首版 V_ref 为**规格反推估算值**（见 `内置基准量表.xlsx`），绝对 SP 标"仅供参考"
- 样本库积累后切云端中位数（同型号样本电压中位数）
- 校准流程见《评分算法详细设计》§6

## 5. 自检清单（真机首次运行）

- [ ] 管理员身份运行（无 → 电压/功耗读不到）
- [ ] 关闭核心隔离（未关 → 电压归零 → `QualityGate` 拦截）
- [ ] VCore 读数 ≈ `Core (SVI2 TFN)` 面板值（偏差 < 0.02V）
- [ ] **VCore ≠ SoC 电压**（误采则评分全错，用精确匹配避免）
- [ ] 满载频率 ≥ 额定×95%（否则 `CollectorOrchestrator` 拦截）
- [ ] 每核 VID 数量 = 物理核心数（如 7800X3D = 8）
- [ ] SVI2 与 VRM 偏差 < 0.05V（交叉校验通过）
- [ ] 跑 `verify_naming.py` 逻辑验证（无需 .NET，纯逻辑）

## 6. 参考文件

- `Collector.Real.cs`：校准后真实采集实现（含完整 switch + 交叉校验）
- `LHM传感器命名校准.md`：命名来源、版本差异、与 HWiNFO 关系重定位
- `verify_naming.py`：**命名匹配规则的单元测试**（10/10，模拟 LHM 真实输出）
- 《频率折算勘误.md》：评分用实测电压、频率仅质控

---
*V1.5：关键词从经验写法更正为 LHM 源码真实命名；新增双源交叉校验；确认 LHM 原生支持每核 VID/有效频率/CCD 温度，重定位 HWiNFO 角色。*
