# LHM 传感器命名校准表（V1.4 → V1.5）

> 依据：LibreHardwareMonitor 源码 `LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs`
> 用途：校准 `Collector.cs` 中 `Name.Contains(...)` 匹配关键词
> ⚠️ 这些是**真实传感器名**，之前的 `"Package"` / `"CPU Core VID"` 等关键词**经验性错误**，本轮修正

## 一、LHM 真实命名（AMD Zen，源码确认）

### 1.1 处理器级（Processor 类，Package 维度）

| 字段 | LHM 真实 `Name` | `SensorType` | 说明 |
|------|-----------------|--------------|------|
| 封装电压（★VCore） | **`Core (SVI2 TFN)`** | Voltage | SVI2 遥测核心电压，**体质评分用这个** |
| SoC 电压 | `SoC (SVI2 TFN)` | Voltage | 不要误采为 VCore |
| 封装温度 | **`Core (Tctl/Tdie)`** | Temperature | Zen2+ 单 CCD 的封装温度 |
| CCD 最高温 | `CCDs Max (Tdie)` | Temperature | 多 CCD |
| CCD 平均温 | `CCDs Average (Tdie)` | Temperature | 多 CCD |
| 平均频率 | **`Cores (Average)`** | Clock | ★满载判定用（比单采 Core#0 更稳）|
| 平均有效频率 | `Cores (Average Effective)` | Clock | APERF/MPERF 有效频率 |
| 总线频率 | `Bus Speed` | Clock | BCLK |
| 封装功耗 | `Package` | Power | RAPL 类 |

### 1.2 每核级（Core 类，`CoreId` 从 0 起）

| 字段 | LHM 真实 `Name` | `SensorType` |
|------|-----------------|--------------|
| 每核瞬时频率 | **`Core #0`** / `Core #1` / ... | Clock |
| 每核有效频率 | **`Core #0 (Effective)`** / ... | Clock |
| 每核倍频 | `Core #0`（另一 Index）| Factor |
| 每核功耗 | `Core #0 (SMU)` | Power |
| **每核 VID（★HWiNFO差异化）** | **`Core #0 VID`** / ... | Voltage |

> 💡 **关键纠正**：每核 VID 命名是 `"Core #N VID"`（带空格），**不是** `"CPU Core VID"`。
> 而且 LHM **确实能读到每核 VID**（之前手册写"LHM 读不到、靠 HWiNFO"是错的，本轮更正）。

## 二、命名版本差异（务必知悉）

| 代际 | 温度名差异 |
|------|-----------|
| Zen / Zen+ | `Core (Tctl)` 或 `Core (Tdie)`（有 offset）|
| **Zen2+（含 Zen3/4/5）** | `Core (Tctl/Tdie)`（无 offset，合并名）|
| Zen2+ 多 CCD | 另出 `CCD1 (Tdie)` / `CCD2 (Tdie)` / ... |

→ 代码里**不要写死 `"Package"`**，应优先匹配 `Tctl/Tdie` / `CCD`。

## 三、校准后的匹配规则（用于 `Collector.cs`）

```csharp
// ★ V1.5 校准：基于真实 LHM 命名
case SensorType.Voltage when sensor.Name.Contains("SVI2 TFN"):
    vCore = sensor.Value.Value; break;          // Core (SVI2 TFN)
case SensorType.Clock when sensor.Name == "Cores (Average)":
    avgFreq = sensor.Value.Value; break;        // 平均频率（满载判定）
case SensorType.Clock when Regex.IsMatch(sensor.Name, @"^Core #\d+ \(Effective\)$"):
    perCoreEffClocks.Add(...); break;           // 每核有效频率
case SensorType.Clock when Regex.IsMatch(sensor.Name, @"^Core #\d+$"):
    perCoreClocks.Add(...); break;              // 每核瞬时频率
case SensorType.Voltage when Regex.IsMatch(sensor.Name, @"^Core #\d+ VID$"):
    perCoreVIDs.Add(...); break;                // ★ 每核 VID（LHM 原生支持）
case SensorType.Temperature when sensor.Name.Contains("Tctl/Tdie"):
    packageTemp = sensor.Value.Value; break;    // 封装温度
case SensorType.Temperature when sensor.Name.Contains("CCD") && sensor.Name.Contains("Max"):
    ccdMaxTemp = sensor.Value.Value; break;     // CCD 最高温（Zen3+）
```

## 四、与 HWiNFO 的关系（校准后重定位）

| 字段 | LHM | HWiNFO SHM | 谁更准 |
|------|-----|-----------|--------|
| VCore (SVI2 TFN) | ✅ | ✅ | **两者同源 SVI2，一致** |
| 每核 VID | ✅（`Core #N VID`）| ✅（更细）| HWiNFO 略全，但 LHM 够用 |
| 有效频率 | ✅（APERF/MPERF）| ✅ | 一致 |
| CCD 温度 | ✅（`CCDx (Tdie)`）| ✅ | 一致 |

**结论：LHM 已能覆盖体质评分全部必需字段（含每核 VID）**。
HWiNFO SHM 增强的边际价值大幅下降——但仍保留（每核 VID 的极端情况 / 用户已装 HWiNFO 时的冗余校验）。

## 五、★ 必须双源交叉校验（防假大雕加固）

既然 LHM 的 VCore 来自 SVI2 遥测，可加一道**合理性校验**：
- 同一时刻 `Core (SVI2 TFN)` 与主板传感器里的 `CPU VCore`（SuperIO/VRM 读）**偏差应 < 0.05V**
- 偏差过大 → 疑似传感器异常 → 拒绝评分或降级为"仅供参考"

> 这是本轮校准顺带发现的可加固点：SVI2 与 VRM 读数互相印证，进一步杜绝假大雕。

---
*V1.5 校准：关键词从经验写法更正为 LHM 源码真实命名；确认 LHM 原生支持每核 VID，重定位 HWiNFO 角色。*
