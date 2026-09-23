# 附录 B · 关键参数、常量与协议速查

> 改任何数值前先看这里的出处与含义；无新真机证据不要改评分/质控系数。代码为准，本表为索引。

## 1. 采样与满载（CollectorOptions / AppSettings / FullLoadStability）
| 参数 | 默认值 | 位置 | 含义 |
|---|---|---|---|
| FullLoadThreshold | 0.90 | CollectorOptions / settings | 满载频率门槛 = MaxBoost × 0.90（只管能否评，不进分数） |
| SampleDurationMs | 10000 | CollectorOptions | 采样窗口约 10 秒 |
| SampleIntervalMs | 500 | CollectorOptions | 每轮间隔 500ms |
| SampleRounds | 0→自动=20 | CollectorOptions | 0 时 = 时长/间隔 = 20 轮；设 1 关闭多轮 |
| 采样窗口满载占比下限 | 0.80 | EvaluationService.Evaluate | loadedRounds/rounds < 0.80 判失败重测 |
| FullLoadHoldSeconds | 60 | CollectorOptions | 自动采样前需持续满载秒数 |
| FullLoadHoldRatio | 0.90 | CollectorOptions | 60s 窗口满载占比门槛 |
| 稳定窗口覆盖要求 | ≥0.98 | FullLoadStability.IsStable | 窗口跨度 ≥ 60s×98% 且占比达标 |
| UI 实时刷新间隔 | 800ms | RunViewModel.StartLive | DispatcherTimer |
| StableSampleCount | 5 | CollectorOptions | 稳态采样次数（早期参数） |
| DiagnosticRetainCount | 10 | CollectorOptions / DiagnosticStore | 诊断报告保留份数 |
| RetainHistory | 100 | AppSettings / HistoryStore | 本机历史成绩保留份数 |

## 2. 质控区间
| 检查 | 字段 | 区间 | 位置 |
|---|---|---|---|
| 合理性（跨主板最后防线） | VCore | 0.20–1.60 V | RationalityCheck |
| 合理性 | 温度 | 0–105 ℃ | RationalityCheck |
| 合理性 | 平均频率 | 500–7000 MHz | RationalityCheck |
| 质控门禁 | VCore | 0.2–1.5 V | QualityGate |
| 质控门禁 | 温度 | 0–100 ℃ | QualityGate |
| 命名匹配 Sanity | 电压/频率/温度/功率 | 0.20–1.55V / 1500–6500MHz / 20–105℃ / 0–400W | Compat.Sanity |
| 每核 VID 满载合理下限 | MinPlausibleFullLoadVid | 0.75 V | PerCoreVidQuality |
| 每核 VID 与 VDDCR 脱节裕量 | DetachMargin | 0.25 V（且 VDDCR>0.90 时判定） | PerCoreVidQuality |

## 3. 评分公式与档位（SpScaler / ZenProfile / ScoreEngine）
- 锚点电压：`V_anchor = V_meas + (slope_mV/MHz / 1000) × (AnchorFreq − F_meas)`（线性带截距）。
- SP：`SP = clip(100 + (VRef / V_anchor − 1) × 300, 0, 150)`。
- 锚点频率：量表 AnchorFreqMHz 优先；否则 `round(MaxBoost × 0.94 / 25) × 25`（AnchorToBoostRatio=0.94）。
- 代际 V/F 斜率默认（mV/MHz）：Zen/Zen+/Zen2=0.30，Zen3=0.35，**Zen4=0.41**，Zen5=0.45，未知=0.40。
- 五档（SP）：大雕 ≥112 / 小雕 ≥104 / 普通 ≥96 / 小雷 ≥88 / 大雷 <88。
- 五档（百分位，在线预留）：大雕 ≥90 / 小雕 ≥70 / 普通 ≥30 / 小雷 ≥10。
- 评级颜色：大雕 #FFB020、小雕 #3FB950、普通 #58A6FF、小雷 #D29922、大雷 #F85149。
- 置信度：在线 0.9；离线 Calibrated 0.6；离线 Estimated 0.4。
- 自标定：VfFit 要求 R² ≥ 0.90 且斜率为正，才用本机拟合斜率覆盖量表。
- 模式：`OnlineRelative` / `OfflineAbsolute` / `CurveOnly`（无 VRef）。

## 4. baseline.json（9 型号；仅 7800X3D 为 Calibrated）
| 型号 | 代际 | VRef | RefFreq | MaxBoost | Anchor | slope | 标定 |
|---|---|---|---|---|---|---|---|
| 7800X3D | Zen4 | 1.070 | 4200 | 5000 | 4800 | 0.41 | **Calibrated** |
| 7950X | Zen4 | 1.060 | 4500 | 5700 | 5200 | (代际默认) | Estimated |
| 7900X | Zen4 | 1.065 | 4700 | 5600 | 5100 | (代际默认) | Estimated |
| 7700X | Zen4 | 1.070 | 4500 | 5400 | 5000 | (代际默认) | Estimated |
| 5800X3D | Zen3 | 1.120 | 3400 | 4500 | 4250 | (代际默认) | Estimated |
| 5900X | Zen3 | 1.130 | 3700 | 4800 | 4550 | (代际默认) | Estimated |
| 9950X | Zen5 | 1.030 | 4300 | 5700 | 5400 | (代际默认) | Estimated |
| 9900X | Zen5 | 1.035 | 4400 | 5500 | 5250 | (代际默认) | Estimated |
| 9700X | Zen5 | 1.040 | 3600 | 5500 | 5200 | (代际默认) | Estimated |

真机参考点（回归用）：7800X3D OCCT ≈4526MHz@0.975V；CPU-Z ≈4896MHz@1.127V→SP≈95.2；4896@1.1305→94.2。

## 5. HWiNFO 共享内存（SM2）协议要点
- 映射名：`Global\HWiNFO_SENS_SM2`（兜底 `HWiNFO_SENS_SM2`）；互斥体 `Global\HWiNFO_SM2_MUTEX`（同兜底）。
  旧 `HWiNFO_SENS_SF/SF1..8` 废弃、布局不同，禁用。
- Header（pack=1，小端）：Status[4]@0（`HWiS` 激活 / `DAED` 过期=免费版 12h）；Version u32@4；Revision u32@8；
  LastUpdate 8B@12；SensorSectionOff@20/Size@24/Count@28；**ReadingSectionOff@32 / ReadingSize@36 / ReadingCount@40**；
  PollingPeriodMs@44。
- Reading 元素（步进用表头 ReadingSize，勿写死）：Type u32@0（None0/Temp1/**Volt2**/Fan3/Current4/Power5/Clock6/Usage7/Other8）、
  SensorIndex@4、Id@8、OrigLabel[128]@12、UserLabel[128]@140、Unit[16]@268、**ValueCurrent f64@284**、Min@292/Max@300/Avg@308。
- 每核 VID 正则（IgnoreCase/Compiled，取 OrigLabel 英文）：
  `(?:core\s*#?\s*0*(\d+)[^\r\n]{0,24}vid)|(?:vid[^\r\n]{0,24}core\s*#?\s*0*(\d+))`
- 排除词：soc/misc/ldo/smu/effective/vddio/vddp/vdd25/vdd18/vdd mem/vddg/vddcr soc/1.8/mem/svi3 tfn/svi2 tfn；
  ≥2 核才返回，按核号升序、同核取大。
- 开关注册表（安装版）：`HKCU\Software\HWiNFO64\Sensors\SensorsSM`=1（另有 HWiNFO32 键）；便携版写 HWiNFO64.INI。
- 真机标签（v8.34 简中，英文 OrigLabel）：汇总 `Core VIDs`（无数字，排除）、每核 `Core 0 VID`…`Core 7 VID`（0 起始无 #）。

## 6. LHM 传感器命名规则（Compat.SensorNameRules）
- VCore 精确：`VDDCR CPU`、`Core (SVI2 TFN)`；包含：VDDCR/SVI2/SVI3/Core Voltage/CPU Core；排除：SoC/Misc/LDO/SMU。
- 封装温度精确：`Core (Tctl/Tdie)`；包含 Tctl/Tdie/Package/CPU Temperature。
- 平均频率精确：`Cores (Average)`；包含 Average/Core (Avg)/CPU Clock。
- 每核正则（LHM，核号 1 起始）：`^Core #\d+ VID$`、`^Core #\d+ \(Effective\)$`、`^Core #\d+$`。
- LHM 每核 VID 解码：MSR 0xC0010293 CurCpuVid[21:14] 套 SVI2 公式 `1.550−0.00625×code`；Zen4/5 实为 SVI3，
  故解出 ~0.43V 占位错值（被 PerCoreVidQuality 丢弃）。

## 7. 运行时路径（均在程序目录，便携）
`baseline.json`（随包）、`settings.json`、`logs/yyyyMMdd.log`、`reports/diagnostic_yyyyMMdd_HHmmss_<6hex>.json`、
`history/score_yyyyMMdd_HHmmss.json`、`诊断包/诊断包_*.zip`、`hosttrace.txt`（诊断启动.bat 生成）、`sensor_cache.json`。

## 8. 主题色（Theme/Styles.xaml，深色）
背景 Bg #0B0E14 / Panel #111721 / Card #171E29 / Card2 #1C242F；边框 #2A3441/#384656；
文本 Text #E6EDF3 / Muted #8B949E / Dim #6E7681；强调橙 Accent #FF6B35、Accent2 #FFB020；
成功 #3FB950、警告 #D29922、错误 #F85149、信息 #58A6FF、紫 #BC8CFF。
