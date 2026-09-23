# SiliconScope · CPU 体质评分
[English](README.md) | 简体中文

基于**稳态满载 V/F 工作点**的 AMD Ryzen CPU「体质（Silicon Quality）」评分桌面软件。
C# / .NET 8 + WPF，通过 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（PawnIO 驱动）
读取传感器，可选接入 HWiNFO 共享内存读取**每核 VID**；纯本地运行、不上传数据。

> 当前版本：**V1.0.0**（首个开源版本；开源前内部名 CpuScore；版本号唯一来源 `UI/AppInfo.cs`）
> 真机校准基线：Ryzen 7 7800X3D（Zen4 / SVI3 / Win10 22H2）。其余型号为规格估算，界面会明确标注。

---

## 它评的是什么

在**确认 CPU 持续满载**的前提下，采样约 10 秒的稳态核心电压与频率，把不同烤机负载下的工作点
用 V/F 斜率**线性折算到统一锚点频率**，再与同型号参考电压比较，得到 0–150 的体质分与五档评级
（大雕 / 小雕 / 普通 / 小雷 / 大雷）。频率只用于满载质控与 V/F 折算，**不直接参与分数**，
因此 OCCT（低频低压）与 CPU-Z（高频高压）两种负载能得到可重复、可比较的结果。

本分数是**自研、可复现、可校验**的稳态 V/F 体质分，**不与任何主板/芯片厂商的内部分数对标，也不复刻其闭源算法**。

## 主要功能

- 环境自检（管理员权限、LHM/PawnIO 驱动、传感器数量、内核隔离状态——**不需要关闭任何安全功能**）。
- 实时监测 + 满载稳定性确认：持续满载满 60 秒（滑窗占比 ≥90%）后**自动开始采样**，避免刚满载就测导致失真。
- 约 10 秒（500ms × 20 轮）多轮采样，满载占比不足直接判失败重测。
- 整颗核心电压（VDDCR/SVI3）参与主分；每核 VID 经质量门后用于核间均匀度/最佳最差核分布。
- 五档评级、V/F 拟合图、每核电压条形图、置信度与参数来源（型号标定 / 代际默认 / 规格推算）。
- 本机历史成绩（本地保存、可查看/删除），JSON + Markdown 报告导出。
- 可选 V/F 多点自标定（R²≥0.90 才采信）。

## 目录结构

```
SiliconScope.sln
├─ 评分引擎/   ScoringEngine：纯 net8.0 跨平台类库（V/F 折算、SP、五档、三级参数解析、自标定、导出）+ 测试
├─ 采集层/     Collector：net8.0-windows，LHM 真实采集、跨主板弹性命名、HWiNFO 共享内存、质控、日志、诊断 + 测试
├─ UI/         WPF（MVVM）：启动项目，产出 SiliconScope.exe；向导式评测、结果、历史、设置
├─ docs/交接文档/   ★ 完整开发/技术/Debug/构建交接文档（先看 README_交接总览）
├─ run_all.py  一键全量逻辑回归（采集 + 评分 + UI 静态校验）
└─ *_说明*.md  V1.3–V1.7 历史迭代/设计文档（归档，部分结论已被取代，见 docs 附录C）
```

## 最终用户（无需安装开发环境）

到 [Releases](../../releases) 下载 `SiliconScope_Vx.y.z_win-x64.zip`，**解压整个文件夹**后，
右键 `SiliconScope.exe` → 以管理员身份运行。只读传感器，不需要关闭内核隔离/杀毒。
需要每核 VID 分布时，按 `docs/.../03_Debug排查手册.md` 第 4 节开启 HWiNFO 共享内存支持（可选）。

## 开发者：构建与测试

```bash
# 逻辑回归（无需 Windows / 硬件，Python3）
python3 run_all.py

# 编译（Linux/macOS 交叉编译 Windows 目标需加 EnableWindowsTargeting）
dotnet build SiliconScope.sln -c Release -p:EnableWindowsTargeting=true

# C# 单元测试：采集层 40 项、评分引擎 50 项
dotnet run --project 采集层/tests/Collector.Tests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project 评分引擎/tests/ScoringEngine.Tests.csproj   -c Release
```
完整的 publish（**必须文件夹自包含、禁止 single-file**）、发布后处理、打包与验收清单见
**`docs/交接文档/04_构建发布与测试手册.md`**。

## 文档

完整交接文档在 [`docs/交接文档/`](docs/交接文档/)：
交接总览 / 开发文档 / 技术文档 / Debug 排查手册 / 构建发布与测试手册 / 已知限制与规划 /
源码清单 / 关键参数速查 / 历史资料索引。新接手者建议从 `README_交接总览.md` 的「30 分钟快速上手」开始。

## 技术栈与第三方

- .NET 8、WPF（MVVM）、C#；Python3（逻辑回归/静态校验）。
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6（MPL-2.0，内含 PawnIO 驱动）。
- HWiNFO 共享内存（SM2）**只读**访问（HWiNFO 为其各自所有者的商业软件，本项目不附带）。

## 免责声明与已知限制

- 电压/频率读数来自第三方传感器库与 HWiNFO，结果**仅供硬件体质参考**，不构成任何性能或稳定性承诺。
- 目前仅 7800X3D 为真机标定（Calibrated），其余型号为估算（Estimated），跨型号绝对分需更多样本收敛。
- WPF 渲染、真实传感器与 HWiNFO 连接需在 Windows 真机验证；CI 仅覆盖编译、纯逻辑单测与静态校验。
- 云端 / 排行榜 / 账号体系为预留接口，当前版本完全离线。
