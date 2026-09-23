# 评分引擎（ScoringEngine）

SiliconScope 的评分核心：纯 .NET 8 跨平台类库（**不依赖 Windows**），在 CPU 满载稳态下把实测工作点
经 **V/F 线性折算**到统一锚点，给出 **0–150 的体质分（SP）**与五档评级。

- 工程：`src/ScoringEngine/ScoringEngine.csproj`（net8.0）；测试：`tests/ScoringEngine.Tests.csproj`（50 项）。
- Python 等价镜像：`verify_scoring.py`（无 .NET 环境也能核对逻辑）。

## 评分口径
1. 满载稳态采样（约 10s）得到 V_meas、F_meas（由采集层负责）。
2. 沿 V/F 直线折算到锚定频率 AnchorFreq：
   `V_anchor = V_meas + (slope/1000) × (AnchorFreq − F_meas)`
3. 与同型号参考电压 VRef 比较：
   `SP = clip(100 + (VRef / V_anchor − 1) × 300, 0, 150)`
4. 五档：**大雕 ≥112 / 小雕 ≥104 / 普通 ≥96 / 小雷 ≥88 / 大雷 <88**。

频率只用于满载门控与 V/F 折算，**不进入分数**；因此重负载（低频低压）与轻负载（高频高压）折算后
结果可重复。SP 是自研、可复现的稳态 V/F 指标，**不对标、不复刻任何主板/芯片厂商的闭源分数**。

## 参数来源（三级，置信度随等级）
| 等级 | 来源 | 置信度 |
|---|---|---|
| Calibrated | 同型号真机校准（`baseline.json`） | 0.9 |
| 代际默认 | Zen 代际默认 V/F 斜率（Zen3 0.35 / Zen4 0.41 / Zen5 0.45 等） | 0.6 |
| Estimated | 按规格估算 | 0.4 |

- 锚点频率：`AnchorFreq = round(MaxBoost × 0.94 / 25) × 25`。
- 多点自标定 `VfFit`：最小二乘拟合，仅当 R² ≥ 0.90 才采用。
- 每核 VID 经 `PerCoreVidQuality` 质量门（占位/脱节值置 null），**只用于核间相对分布，绝不混入主分**。

## 接口（摘要，完整以 `ScoringEngine.cs` 为准）
```csharp
public record ScoreInput(
    string Model, string ZenGeneration,
    double VCore, double FrequencyMHz, double ReferenceFreqMHz,
    double TemperatureC, double[]? PerCoreVIDs, double[]? PerCoreClocks);

public record ScoreResult(
    string Mode, double SpScore, double Percentile, string Grade,
    double Confidence, string ReferenceNote,
    string? BestCore, string? WorstCore);
```
关键类型：`SpScaler`（折算 / SP / 五档 / 颜色）、`ZenProfile`（代际斜率）、
`BaselineResolver`（三级解析）、`VfFit`（自标定）、`ScoreEngine`（编排）、
`PerCoreVidQuality`（每核 VID 质量门）、`Exporter`（JSON / Markdown 导出）。

## 验证
```bash
dotnet run --project tests/ScoringEngine.Tests.csproj -c Release   # 50 项
python3 verify_scoring.py                                          # 逻辑镜像
```

## 说明
- 目前仅 7800X3D 为真机校准，其余型号为估算，UI 会明确标注。
- 绝对 SP 仅供**同型号 CPU 之间横向比较**。
