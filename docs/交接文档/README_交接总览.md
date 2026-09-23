# SiliconScope · 交接总览

> 面向接手开发的工程师。读完本篇 + 按图索骥打开对应文档，即可在不依赖原作者口述的情况下
> 完成「看懂工程 → 编译 → 测试 → 发布 → 真机排障 → 二次开发」全流程。
>
> **当前公开版本：V1.0.0**（首个开源版本）。版本号唯一来源是 `UI/AppInfo.cs` 的 `Version`。
>
> 版本号说明：本项目首次公开发布即 **V1.0.0**；开源前的内部原型未公开发布，公开版本号
> 从 1.0.0 起算。开源前的开发阶段见上一级 **`版本与更名说明.md`** 与 `01_开发文档`。
>
> 产品更名：开源前产品内部名 `CpuScore` 已统一更名为 **`SiliconScope`**（程序集、根命名空间、
> exe、解决方案/UI 工程文件、窗口标题同步；纯 ASCII，不影响 GBK 启动结论）。构建命令以
> `SiliconScope.sln` / `UI/SiliconScope.csproj` 为准。

---

## 1. 这是什么

一款 Windows 桌面软件（WPF / .NET 8 / C#），在 **CPU 全核满载稳态**下采集频率、核心电压、
温度与每核请求电压（VID），通过 **V/F（电压-频率）线性折算**给出一个 0–150 的「体质分（SP）」
与五档评级（大雕/小雕/普通/小雷/大雷），并展示核间 VID 分布、保存本机历史成绩。

- 目标硬件：AMD Ryzen Zen3 / Zen4 / Zen5 全大核桌面 CPU（未知型号有通用兜底）。
- 传感器来源：[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
  0.9.6（NuGet，MPL-2.0，其 Ring0/PawnIO 内核驱动读 MSR/传感器）。
- 每核 VID 增强来源（可选）：HWiNFO 的**共享内存**（只读、不装驱动、不改硬件）。
- 纯本地、单机、便携：不装运行时（self-contained）、不写注册表、不联网、不上传数据。

> 重要产品口径：本软件**不依赖、也不对标任何厂商（含华硕）的闭源内置分数**。分数仍叫 SP，
> 但它是「满载稳态工作点 + V/F 折算」的自研、可复现评分，绝对分仅供**同型号 CPU 之间横向比较**。

---

## 2. 当前状态

- 首个公开版本 **V1.0.0**，解决方案 Release 编译 **0 错误 0 警告**。
- 自动化验证：采集层 C# 单测 **40**、评分引擎 C# 单测 **50**、UI 静态校验 **132** 项、
  采集层/评分 Python 镜像回归全部通过。
- 真机（Win10 22H2 + Ryzen7 7800X3D + HWiNFO v8.34）已验证：启动、LHM 读 74 传感器、
  HWiNFO 每核 VID（SM2）、满载自动采样、出分、历史、去华硕文案。
- **沙盒（Linux）无法运行 WPF / 真机传感器 / HWiNFO**，这部分只能交叉编译 + 静态/逻辑校验，
  最终渲染与读数必须在 Windows 真机确认（见 `05_已知限制与后续规划.md`）。

---

## 3. 文档地图

| 文档 | 给谁看 | 解决什么 |
|---|---|---|
| **README_交接总览.md**（本文） | 所有人 | 项目是什么、怎么快速上手、铁律 |
| **01_开发文档.md** | 产品/开发 | 功能需求、用户流程、评分口径、关键产品决策、版本演进、术语 |
| **02_技术文档.md** | 开发 | 架构、工程结构、数据流、各模块/关键类型、HWiNFO 协议、跨平台编译 |
| **03_Debug排查手册.md** | 开发/测试/客服 | 启动崩溃五连修、传感器/HWiNFO/评分各类问题的定位 SOP、症状速查表、死路清单 |
| **04_构建发布与测试手册.md** | 开发/打包 | 环境、build/test/publish/打包命令、版本号改哪里、发布验收清单 |
| **05_已知限制与后续规划.md** | 负责人/开发 | 沙盒边界、真机待验证项、校准现状、悬置方向、路线图 |
| **附录A_源码文件清单.md** | 开发 | 每个源文件一句话职责 |
| **附录B_关键参数速查.md** | 开发 | 阈值/路径/公式/协议偏移/默认值一页查 |
| **附录C_历史文档与资料索引.md** | 开发 | 仓库内自带的 V1.3~V1.7 迭代文档与外部权威资料，哪些现行、哪些归档 |

---

## 4. 30 分钟快速上手

### 4.1 拿到代码
唯一权威工作副本（不要反复解压旧 zip 覆盖）：

```
SiliconScope_1.0.0/           # 源码（公开版本 V1.0.0）
├─ SiliconScope.sln
├─ run_all.py                 # 一键全量逻辑回归（无需 .NET/Windows 也能跑大部分）
├─ UI/                        # WPF 界面（启动项目，产出 SiliconScope.exe）
├─ 采集层/                    # Collector：LHM + HWiNFO 采集、质控、日志、诊断
└─ 评分引擎/                  # ScoringEngine：纯跨平台类库，V/F 折算与评分
```

### 4.2 不装环境也能验证逻辑（任意有 Python3 的机器）
```bash
cd SiliconScope_1.0.0
python3 run_all.py           # 期望末尾：🎉 全量回归通过
```

### 4.3 编译（Windows 原生，或 Linux 交叉编译）
需要 .NET 8 SDK。Linux 交叉编译 Windows 目标要加 `-p:EnableWindowsTargeting=true`：
```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build SiliconScope.sln -c Release -p:EnableWindowsTargeting=true   # 期望 0 Error 0 Warning
```
完整 publish / 打包 / 验收见 `04_构建发布与测试手册.md`。**严禁 single-file 发布**（原因见下铁律 6）。

### 4.4 真机运行
解压发布包整个文件夹 → 右键 `SiliconScope.exe` 以管理员身份运行 → 自检过 → 开烤机满载 →
持续满载约 1 分钟自动采样、约 10 秒出分。打不开就跑文件夹里的 `诊断启动.bat`。

---

## 5. 给下一位开发的铁律（都是真机踩坑换来的，勿再踩）

1. **版本号唯一来源是 `UI/AppInfo.cs` 的 `Version`**；改版本同步改 `UI/MainWindow.xaml`
   侧栏显示与 `UI/verify_ui.py` 的版本断言。报告抬头通过 `Exporter.Build(..., appVersion)` 传入，
   不要再让报告显示 `dev`。
2. **程序集名/产物名必须纯 ASCII（`SiliconScope`）**。中文程序集名会让 apphost 在简中 Win10
   （代码页 936）定位入口程序集失败，表现为双击 exe 静默退出、无日志无弹窗。窗口标题可中文。
3. **构造阶段绝不打开传感器/驱动**。`Computer.Open()`（加载 PawnIO 驱动）只能在窗口 `Loaded`
   后的后台线程、或用户点「开始评测」后进行；主窗口构造早期全局异常兜底还没挂，驱动被拦截会直接闪退。
4. **不要让用户关内核隔离 / 内存完整性(HVCI) / 杀毒**。LHM 0.9.6 的 PawnIO 驱动兼容 HVCI，
   CPU-Z/AIDA64/OCCT 都不需要关安全功能，本软件也不需要。能否读数只看「驱动是否真打开、关键量是否真读到」。
5. **必须文件夹自包含发布，禁止 `PublishSingleFile`**：单文件会让 LHM 内嵌的 PawnIO 驱动资源
   释放失败、传感器读数全 0（真机复现过两次）。发布后还要删掉误生成的 `Collector.exe`（只留 `Collector.dll`）。
6. **宁可拒评，绝不给假分**。三道质控门（合理性区间 → 满载判定 → 电压/温度窗口）任一不过都拒绝出分；
   每核 VID 坏数据（如 Zen4 上 LHM 解出的 ~0.43V 占位值）必须置 null，不输出假的核间排名。
7. **满载判定基准是「全核 MaxBoost × 0.90」，不是基础频率**。阈值 0.90 是真机校准
   （7800X3D OCCT 重负载全核稳态约 4.5GHz，0.95 会误判未满载）。阈值只管「能不能评」，不进分数。
8. **主 SP 分只用整颗 VDDCR（真实供电）**；HWiNFO 的每核 VID 是 CPPC **请求值**、非真实每核电压，
   只用于核间相对排名，绝不混入主分，且要过同一道 `PerCoreVidQuality`。
9. **HWiNFO 共享内存名是 `Global\HWiNFO_SENS_SM2`**（互斥体 `Global\HWiNFO_SM2_MUTEX`）；
   旧 `HWiNFO_SENS_SF/SF1..8` 是废弃私有格式、布局不同，**禁止改回去**。v7 起共享内存默认关闭。
10. **XAML 绑定只读属性要显式 `Mode=OneWay`**（`Run.Text` 默认 TwoWay 绑只读属性会启动即崩）；
    导航 `ListBox` 在 `InitializeComponent` 期就会触发选择事件，访问命名控件要用 `_ready` 门控。
11. **改任何逻辑都要补/跑校验**：核心算法尽量放进可跨平台单测的纯类（评分引擎不依赖 Windows），
    UI 改动加 `UI/verify_ui.py` 静态断言，并做「故意改错能报红」的反向验证。
12. **数值/结论必须有出处**，不拍脑袋；真机证据与代码注释、本文档结论要对得上。改动阈值在
    代码注释里写真机依据。

---

## 6. 技术栈与目录一览

- .NET 8 / C#（`LangVersion=latest`、`Nullable=enable`）、WPF（仅 UI 与采集层用 `net8.0-windows`；
  评分引擎是纯 `net8.0` 跨平台类库）。
- 零 UI 框架依赖：MVVM 基类、RelayCommand、主题样式、图表/仪表控件全部手写（便于离线编译）。
- 解决方案 5 个工程：`Collector`（Exe，net8.0-windows）、`Collector.Tests`、
  `ScoringEngine`（dll，net8.0）、`ScoringEngine.Tests`、`SiliconScope`（WinExe，WPF）。
- 测试/校验：C# 两个 xUnit 风格自研轻量测试工程 + 一套 Python 镜像回归（`run_all.py` 总入口）。

更细的架构与数据流见 `02_技术文档.md`，文件级职责见 `附录A`。

---

## 7. 真机目标环境（已知基线）

- Windows 10 22H2（10.0.19045），简体中文、系统代码页 GBK/936。
- AMD Ryzen 7 7800X3D（Zen4 / SVI3 供电），HWiNFO64 v8.34。
- 内核隔离 / 内存完整性（HVCI）**开启**，管理员运行，解压在非系统盘普通目录。
- 烤机工具：OCCT（重负载，全核约 4.5GHz）、CPU-Z（较轻，全核约 4.9GHz）等均可。

> 同一份发布包在该环境 LHM 可读到 74 个传感器；不同主板传感器数量/命名不同，靠弹性匹配 +
> 合理性校验兜底（见技术文档「跨主板兼容」）。
