# LHM 跨主板传感器命名兼容设计

> 问题：不同主板/BIOS 的 LHM 传感器命名存在差异，单靠"真机实测校准一次"无法在他人电脑上稳定使用。
> 目标：**同一份代码，换主板/BIOS 也能自动找到正确的传感器，无需改代码、无需用户手动配置**。
> 版本：V1.6（承接 V1.5 命名校准）

## 一、先厘清：差异到底有多大？（避免过度设计）

**好消息：差异被限制在一个很小的范围内。**

LHM 的传感器名来自**两处**，稳定性完全不同：

| 来源 | 命名稳定性 | 说明 |
|------|-----------|------|
| **LHM 内置定义**（`Amd17Cpu.cs` 等源码硬编码）| **几乎不变** | Zen 代际决定，与主板无关。`Core (SVI2 TFN)`、`Core #N VID`、`Core (Tctl/Tdie)`、`Cores (Average)` 都属此类 |
| **主板/BIOS 特有传感器**（SuperIO、VRM、EC）| **会变** | 如 `CPU VCore`（VRM 读数，主板决定）、风扇、VRM 温度 |

→ **体质评分的核心字段（VCore SVI2、每核 VID、频率、温度）几乎全部来自"内置定义"，跨主板稳定。**
→ **真正会变的是"主板 VRM 读数"——而它恰好只是 V1.5 新增的"交叉校验"辅助字段，不是评分主干。**

**所以：V1.5 校准的核心匹配规则（`Core (SVI2 TFN)` 等）在 95% 机器上是对的，不需要"探测模式"来兜底。**
**需要兼容层的是那 5% 的边缘情况 + 交叉校验字段。**

> 但这 5% 如果不管，就会出现"你机器上正常、用户机器上报错/假大雕"——**正是你担心的稳定性问题**。所以下面设计是**必要的防御层**，而非过度设计。

## 二、兼容策略：四层防御（核心设计）

### 第 1 层：弹性匹配（代码内置，无需探测）

**不用单一 `Name == "xxx"` 精确匹配，改用"候选列表 + 优先级"**，自动适配命名变体。

```csharp
// SensorNameResolver.cs —— 每个字段维护一组候选名（按优先级）
public static class SensorNameRules
{
    // VCore：精确优先，含常见变体。LHM 内置名在最前
    public static readonly NameRule VCore = new("VCore",
        exact:   new[] { "Core (SVI2 TFN)" },          // ★ 首选（Zen2+ 内置）
        contains: new[] { "SVI2", "Core Voltage" });    // 变体兜底

    public static readonly NameRule PackageTemp = new("PackageTemp",
        exact:    new[] { "Core (Tctl/Tdie)" },
        contains: new[] { "Tctl", "Tdie", "Package" }); // "Package" 兜底老 Zen

    public static readonly NameRule AvgFreq = new("AvgFreq",
        exact:    new[] { "Cores (Average)" },
        contains: new[] { "Average", "Core (Avg)" });

    // ★ 每核用正则（V1.5 已校准，跨主板稳定）
    public static readonly RegexRule PerCoreVid = new("PerCoreVID", @"^Core #\d+ VID$");
    public static readonly RegexRule PerCoreClock = new("PerCoreClock", @"^Core #\d+$");
    public static readonly RegexRule PerCoreEffClock = new("PerCoreEff", @"^Core #\d+ \(Effective\)$");
}
```

**匹配逻辑**：先试精确，再试 Contains，再试正则。第一个命中的**且数值在合理范围内的**胜出。

→ **即使某主板把 `Core (SVI2 TFN)` 叫成 `CPU Core Voltage`，也能自动命中兜底。**

### 第 2 层：合理性校验（任何匹配都必须过这关）

**这是防"假大雕"的真正核心，比命名精确与否更重要。** 即使匹配错传感器，只要数值离谱就丢弃：

```csharp
static bool IsPlausible(Field field, double v) => field switch
{
    Field.Voltage   => v is >= 0.20 and <= 1.55,   // VCore 合理区间
    Field.FreqMHz  => v is >= 1500 and <= 6500,    // 1.5G~6.5G
    Field.TempC    => v is >= 20   and <= 105,     // ℃
    Field.PowerW   => v is >= 0    and <= 400,
    _              => true
};

// 使用：匹配到候选后先校验
if (IsPlausible(rule.Field, sensor.Value.Value))
    candidates.Add(sensor);   // 通过才入候选
// 最后取"最匹配名 + 合理值"的传感器
```

→ **即使某主板命名偏离，采到一个 "SoC 电压" 之类，数值区间校验也会拦一道。**
→ **这一层让"命名错配"的危害从"评出假大雕"降级为"该字段缺失（走降级）"，是稳定性的安全网。**

### 第 3 层：自省探测（你要求的"探测模式"）

**首次运行 / 未命中时，自动 dump 全部传感器名到日志 + 本地缓存**，供分析、也供后续众包。

```csharp
public static class SensorProbe
{
    /// <summary>
    /// 探测模式：遍历所有传感器，输出 (Name, SensorType, 当前值) 清单
    /// 触发：首次运行 / 某字段未匹配 / 用户手动"重新探测"
    /// </summary>
    public static ProbeReport Run(Computer computer)
    {
        var report = new ProbeReport { GeneratedAt = DateTime.UtcNow };
        foreach (var hw in computer.Hardware)
        {
            foreach (var s in hw.Sensors)
                report.Entries.Add(new ProbeEntry(hw.HardwareType, s.SensorType, s.Name, s.Value));
        }
        return report;
    }

    /// <summary>按命名规则自动匹配 + 合理性校验，返回匹配结果（供 Orchestrator 用）</summary>
    public static MatchResult AutoMatch(ProbeReport report)
    {
        // 对每个 NameRule：在 report 中找"最匹配名 + 合理值"的传感器
        ...
    }
}
```

**探测模式的三用**：
1. **首次运行自动建缓存** `sensor_cache.json`（主板指纹 → 传感器映射），下次免探测直接复用
2. **用户机器上报错时**，日志里的 dump 让用户/开发者一眼看出"哪个字段没匹配上"
3. **为第 4 层众包提供数据**

→ **这直接回应你的担忧：即使命名有差异，程序自己能"看"到所有传感器并自动选，不依赖预先写死的校准表。**

### 第 4 层：主板指纹 + 映射缓存 / 众包（规模化兜底）

**核心洞察：同一款主板 + BIOS 版本的命名是固定的。** 所以：

```
主板指纹 = 主板厂商 + 型号 + BIOS 版本（+ LHM 版本）
   ↓
查 sensor_cache.json（本地）
   ├─ 命中 → 直接用缓存的传感器映射（无需探测，稳定）
   └─ 未命中 → 跑探测模式 → 建缓存 → （可选）匿名上报
```

`sensor_cache.json` 结构：
```json
{
  "fingerprints": {
    "ASUS_ROG-STRIX-X670E-E-GAMING-WIFI_BIOS-1801_LHM-0.9.6": {
      "VCore": "Core (SVI2 TFN)",
      "PackageTemp": "Core (Tctl/Tdie)",
      "AvgFreq": "Cores (Average)",
      "VRM_Core": "CPU VCore",
      "probed_at": "2026-09-18T...",
      "confidence": "high"
    }
  }
}
```

**众包（后续版本，可选启用）**：
- 用户**主动勾选**"匿名贡献传感器映射" → 上报指纹 + 映射（**不上报任何电压/温度数值**，仅命名）
- 服务端聚合 → 同指纹映射置信度提升 → 新用户直接命中高置信度映射
- **这是唯一能"在其他人电脑上稳定使用"的根本解法**：靠真实设备矩阵覆盖，而非靠一个人在一台机器上校准

## 三、降级链（兼容层的最终兜底）

即使以上全失败，评分**仍能降级运行**（沿用《评分算法》三级降级）：

```
第1层命中（弹性匹配 + 合理性校验通过）
   → 完整评分（在线/离线 SP）✅
   ↓ 某核心字段缺失
第3层探测后仍缺
   → 用众包映射重试 / 提示用户"该字段不支持"
   ↓
核心字段（VCore / AvgFreq）仍缺失
   → 拒绝评分（QualityGate），明确提示原因（防假大雕）
```

→ **关键点：宁可"不给分"，绝不"给假分"。** 这正是兼容层要守住的底线。

## 四、对你的担忧的直接回答

> **Q：就算真机实测了，也没法在其他人电脑稳定用？**

**A：单个真机实测确实不够，但本设计的四层合起来就够了：**

| 你的担忧 | 本设计的应对 |
|---------|------------|
| 命名因主板/BIOS 而异 | ① 弹性匹配（候选+优先级）覆盖变体 |
| 某些机器命名完全没想到 | ③ 探测模式自省 + ④ 众包映射库 |
| 匹配错传感器导致假大雕 | ② **合理性校验**（数值区间，与命名无关）|
| 首次运行/新主板 | ③ 探测自动建缓存，下次免探测 |
| 长尾主板没人测过 | ④ 众包 + **宁可拒评不给假分** |

**真正让"跨机器稳定"成立的，是第 ②③④ 层的组合**——尤其是 ② 合理性校验：
**它不依赖任何命名知识，只看数值是否合理，所以是跨主板无条件生效的最后防线。**

> 💡 还有一个**更稳的工程选择**（供你决策）：
> **不自己解析 LHM 传感器名，而是直接读取 LHM 导出的传感器清单/用其 WMI 接口**——
> **〔已决策 · 否决〕** ~~LHM 官方提供的 `root/LibreHardwareMonitor` WMI 命名空间……~~
> 见 [WMI 选项决策记录](./WMI选项决策.md)。**最终采用「方式 A：直接嵌入 LibreHardwareMonitorLib NuGet 包」**：
> - ✅ 用户**无需安装、无需运行任何 LHM 程序**，也**无后台服务/后台进程**
> - ✅ 兼容层（V1.6 四层防御）已覆盖命名差异，WMI 的核心卖点（命名统一）不再有优势
> - ✅ 无自启进程、无权限/生命周期管理负担
> - ❌ 因此**本设计文档中不再保留「改用 WMI」作为待确认项**，第 ①②③ 层维持现状

## 五、探测模式：交付物与行为

`SensorProbe.cs`（本轮新增）：

- `ProbeReport Run(Computer)`：dump 全部传感器 → JSON
- `MatchResult AutoMatch(ProbeReport)`：按 NameRule 自动匹配 + 合理性校验
- 输出文件：
  - `sensor_dump.json`（原始探测，供调试）
  - `sensor_cache.json`（匹配后的映射缓存，供复用）
- 触发时机：**首次运行 / 字段未匹配 / 用户手动"重新探测"**（不会每次都跑，避免开销）

**用户视角**：正常情况下零感知（首次自动探测一次即缓存）；只有出错时才看到"已生成探测日志"。

## 六、验证（本轮已落地 + 回归）

`verify_compat.py`（本轮新增）：覆盖——
- ✅ 弹性匹配：内置名 / 变体名 / 正则 全部命中
- ✅ 合理性校验：离谱值被拒（防假大雕，与命名无关）
- ✅ 探测 + 自省：能枚举出全部传感器并正确识别核心字段
- ✅ 降级：字段缺失时正确拒评
- ✅ 主板指纹缓存命中/未命中分支

已并入 `regression_all.py`，**全量回归 5/5 通过**。

## 七、已决策 / 待确认

### 〔已决策〕

1. **~~是否改用 LHM 的 WMI 接口~~ → 否决**（见 [WMI 选项决策记录](./WMI选项决策.md)）
   - 采用「方式 A：直接嵌入 `LibreHardwareMonitorLib` NuGet 包」
   - **用户无需安装/运行 LHM，无后台服务**（这正是当前 V1.6 的实现方式）
   - 兼容层已覆盖命名差异，WMI 收益被抵消、代价（后台进程/权限）不划算

2. **~~众包映射库~~ → 不做**（见 [众包映射库决策记录](./众包映射库决策.md)）
   - 首版 + 可预见阶段均**仅本地缓存**，`BoardCache` 只在本机复用映射
   - **不建服务端聚合、不上传任何映射数据**
   - 长尾主板靠**弹性匹配 + 合理性校验 + 探测自省**三件套兜底，不依赖群体数据
   - 仅本地覆盖不到时 → 触发一次探测（`SensorProbe`）自动建映射，对单用户透明

3. **~~探测日志默认行为~~ → `AutoOnFirstRun`（首次自动探测 + 出错提示手动重探）**（见 [探测模式决策记录](./探测模式决策.md)）
   - **默认 = 首次运行自动探测一次 → 映射写缓存 → 后续启动直接复用（零打扰、零开销）**
   - **仅当探测失败 / 必需字段缺失时才弹出提示**，引导用户"点击重新探测"
   - 可选 `AlwaysAuto`（调试用，每次重探）与 `OffUntilError`（默认关闭、仅出错触发）
   - **核心原则：探测尽力而为，宁可缺字段也不抛异常；是否弹窗由 `ShouldPromptUser` 决定，核心层不耦合 UI**

### 〔待确认〕

_（无。全部决策项已落地。）_

---
*V1.6：解决"跨主板/BIOS 命名差异导致无法稳定使用"的核心稳定性问题，四层防御 + 探测模式。*
