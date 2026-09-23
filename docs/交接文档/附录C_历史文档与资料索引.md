# 附录 C · 历史文档与外部资料索引

> 代码仓库自带大量 V1.3–V1.7 迭代文档，是理解「为什么这么设计」的第一手资料，但部分结论/命令已被后续
> 版本取代。本附录标注每份文档的状态，避免被旧结论误导。**冲突时一律以代码现状 + 本交接文档集为准。**

## 1. 本交接文档集（现行权威，位于 `docs/交接文档/`）
- `README_交接总览.md`、`01_开发文档.md`、`02_技术文档.md`、`03_Debug排查手册.md`、
  `04_构建发布与测试手册.md`、`05_已知限制与后续规划.md`、`附录A/B/C`。

## 2. 开源前历史文档（已归档至 `docs/archive/`）
> 均为开源（V1.0.0）前的内部档案，命令/结论可能过时；归档说明见 `docs/archive/README.md`。

| 文档（位于 docs/archive/ 下） | 状态 | 说明 |
|---|---|---|
| `推进说明_V1.3.md` | 归档 | 项目早期范围与基线 |
| `代码补全说明_V1.4.md` | 归档 | 早期代码补全记录 |
| `校准迭代说明_V1.5.md` | 归档（仍有参考价值） | LHM 真实命名校准、修正两个静默 bug 的过程 |
| `兼容迭代说明_V1.6.md` | 归档（仍有参考价值） | 跨主板四层兼容的设计来源 |
| `决策补丁说明_V1.6.1.md` `_V1.6.2.md` | 归档 | WMI 路线否决、众包暂不做等决策 |
| `探测模式迭代说明_V1.6.3.md` | 归档 | 探测模式 AutoOnFirstRun |
| `日志诊断导出迭代说明_V1.6.4.md` | 归档（仍有参考价值） | 日志/诊断报告/诊断包/三层排查体系的来源 |
| `频率折算勘误.md` | 归档（结论已吸收） | 「评分用实测稳态电压、频率仅作满载质控」与 V/F 折算的勘误，结论已在引擎落地 |
| `真机修正说明_V1.6.6.md` | ★ 重要归档 | 真机第一轮修正：SVI3/VDDCR、MaxBoost 基准、0.90 阈值、V/F 折算、打通主链路 |
| `界面设计说明_V1.0.md` | 参考 | WPF 深色界面设计规范（配色/布局/控件） |
| `UI开发说明_V1.7.0.md` | 参考 | UI 工程开发约定（MVVM、页面、绑定），部分实现已演进，以代码为准 |
| `LHM字段映射与采集校准手册.md` | ★ 真机对接必看 | LHM 传感器字段映射、Step1-4 校准流程 |
| `LHM传感器命名校准.md` | 参考 | 命名来源、Zen 代际差异、与 HWiNFO 的关系 |
| `LHM跨主板兼容设计.md` | ★ 参考 | 弹性匹配/合理性/探测/指纹四层防御设计 |
| `WMI选项决策.md` `众包映射库决策.md` `探测模式决策.md` | 归档 | 各路线取舍记录 |

## 3. 各模块 README
- `采集层/README.md`：采集层说明（命令以 `04_构建发布与测试手册` 为准）。
- `评分引擎/README.md`：评分口径说明；权威实现以 `ScoringEngine.cs` 与本交接集 01/02 为准。

## 4. 外部权威资料（修复时实际查证过）
- **HWiNFO 共享内存协议（SM2）**：hwinfo-go
  `https://pkg.go.dev/github.com/MatthiasKunnen/hwinfo-go/pkg/hwinfoshmem`
  —— 映射名 `Global\HWiNFO_SENS_SM2`、互斥体、Header/Reading 布局、状态魔数的权威依据。
  旁证：sensorwatch（PyPI，ctypes、no admin）、公开逆向 gist。
- **LibreHardwareMonitor 每核 VID 解码源码（SVI2 公式来源）**：
  `https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/master/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs`
  —— 读 MSR 0xC0010293 CurCpuVid[21:14] 套 `1.550−0.00625×code`；Zen4 model 0x61 未做 SVI3 修正，
  raw code 不对外暴露，故 LHM 之上无法重解码（每核 VID 改走 HWiNFO 的根因）。
- LibreHardwareMonitor 项目：`https://github.com/LibreHardwareMonitor/LibreHardwareMonitor`（MPL-2.0）。

## 5. 用户提供的原始材料（需求/设计来源，按文件名留存）
- 开源前各阶段的完整交付 / 代码 zip（内部迭代基线，未公开发布）。
- 原始 UI / 界面 / 真机修正设计文档（已归档至 `docs/archive/`）。
- 多轮真机诊断：`logs/yyyyMMdd.log`、`diagnostic_*.json`、`诊断包_*.zip`、HWiNFO 传感器截图、
  `score_*.md` 历史成绩（用于核对满载频率、电压、每核 VID 真值与评分口径）。

## 6. 版本交付物命名规律（公开版本，自 V1.0.0 起）
- Windows 包：`SiliconScope_V1.0.0_win-x64.zip`（解压后顶层 `SiliconScope/`）。
- 源码包：`SiliconScope_源码_V1.0.0.zip`（顶层 `SiliconScope_1.0.0/`，已排除 bin/obj/.vs）。
- 变更/更名说明：`docs/版本与更名说明.md`（CpuScore→SiliconScope、版本号定为 1.0.0）。
- 判断版本一律看 `UI/AppInfo.cs` 的 `Version`；`docs/archive/` 文件名中的编号为开源前内部编号。
