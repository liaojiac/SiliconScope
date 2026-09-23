# 附录 A · 源码文件清单与职责

> 路径相对代码根。约 6100 行 C#/XAML。★=排障/改动高频文件。

## 解决方案根
| 文件 | 职责 |
|---|---|
| `SiliconScope.sln` | 5 工程解决方案 |
| `run_all.py` | 一键全量回归入口（采集/评分/UI 三套） |
| `build_and_test.sh/.bat/.ps1` | 早期构建脚本（以 04 手册命令为准） |
| `.gitignore` | 忽略 logs/reports/导出/诊断包/bin/obj 等 |
| `README.md` / `README.zh-CN.md` | 根 README（英文，开源首页）/ 中文版 |

## UI 工程（`UI/`，启动项目，产出 SiliconScope.exe）
| 文件 | 职责 |
|---:|---|
| `SiliconScope.csproj` | WPF/net8.0-windows；AssemblyName=**SiliconScope**(ASCII)；引用 Collector+ScoringEngine；复制 baseline.json/诊断启动.bat |
| `AppInfo.cs` ★ | **版本号唯一来源** `Version` |
| `App.xaml` / `App.xaml.cs` ★ | 应用入口；静态构造最早挂全局异常 + 初始化日志；OnStartup 挂 UI 异常并手动建主窗口 |
| `MainWindow.xaml` / `.xaml.cs` ★ | 侧边导航 + 内容区；`_ready` 门控初始化期导航事件；Loaded 后后台探测；副标题/版本号 |
| `Theme/Styles.xaml` ★ | 深色主题；`*Color` 与 `*Brush` 分别命名；Card/Primary/Ghost/NavItem/Stat/Hint 等样式 |
| `Views/RunView.xaml(.cs)` ★ | 向导四步界面（自检/等待满载/采样/完成）、实时读数、60s 稳定进度、完成页重测按钮 |
| `Views/ResultView.xaml(.cs)` | 结果页：环形分、五档、V/F 图、每核 VID 条、折算参数、Estimated 水印、物理事实说明 |
| `Views/HistoryView.xaml(.cs)` | 本机历史列表/详情/删除/打开目录 |
| `Views/SettingsView.xaml(.cs)` | HWiNFO 开关、多轮采样、满载阈值、自标定、日志级别、保留份数、关于 |
| `ViewModels/ViewModelBase.cs` | INotifyPropertyChanged 的 Set/Raise；无参 RelayCommand（不支持 CommandParameter） |
| `ViewModels/MainViewModel.cs` ★ | 导航枚举/Page、持有 Settings+Service+四子 VM；StartBackgroundProbe；Go；OnEvaluated(存历史+跳结果) |
| `ViewModels/RunViewModel.cs` ★★ | **向导状态机**：Step0-3、各命令、ResetWizard/Restart(重测)、800ms 定时器、满载稳定判定、DoSample |
| `ViewModels/ResultViewModel.cs` | 结果展示 VM（Load(EvaluationResult)、折算/分布/文案属性） |
| `ViewModels/HistoryViewModel.cs` | 历史加载/刷新/删除/打开目录/查看详情 |
| `ViewModels/SettingsViewModel.cs` | 设置项双向绑定、保存、应用到 CollectorOptions |
| `Services/EvaluationService.cs` ★★ | 编排采集→质控→评分；CheckEnvironment/Peek/Evaluate/Aggregate；HWiNFO 跨轮取峰；满载占比保护；自标定 |
| `Services/AppSettings.cs` | settings.json 读写；Apply() 写入 CollectorOptions（间隔/时长/轮数/阈值/日志级别） |
| `Services/HistoryStore.cs` | history/*.json 保存/倒序加载/删除/清理(默认100)/打开目录；Entry↔Result 转换 |
| `Services/BaselineAdapter.cs` | 采集层 Baseline → 引擎 Baseline 的桥接 |
| `Models/EvaluationResult.cs` | UI 评测结果全字段（含 IsEstimated、VfPoints、EnvChecks） |
| `Models/HistoryEntry.cs` | 历史持久化形态（VfPoints 为 double[][]） |
| `Controls/RingGauge.cs` | 手写环形仪表（SP 总分） |
| `Controls/VfChart.cs` | 手写 V/F 散点 + 拟合线控件（采样时绑 LivePoints） |
| `Controls/CoreBars.cs` | 手写每核 VID 条形控件 |
| `verify_ui.py` ★ | UI 静态结构/绑定/命名/版本/去厂商对标/重测等 132 项断言 |
| `诊断启动.bat` ★ | 启动失败抓取：COREHOST_TRACE、start /wait、打印退出码、生成 hosttrace.txt（需 CRLF） |

## 采集层（`采集层/`，Collector，net8.0-windows，依赖 LHM 0.9.6）
| 文件 | 职责 |
|---:|---|
| `Program.cs` | 独立控制台演示链路（采集→质控→评分→导出），用 FakeLhm 模拟数据；非 UI 启动项 |
| `src/Collector/Collector.csproj` | Exe/net8.0-windows；PackageReference LHM0.9.6；引用 ScoringEngine；显式编入 Program.cs；复制 baseline.json |
| `src/Collector/Collector.cs` ★★ | 领域 record、ICollector、**LhmCollector**(单例 Computer/环境检测/ZenMap)、**HwinfoCollector**、**CollectorOrchestrator**(双源合并+三道质控)、CollectorOptions、QualityGate、RationalityCheck、JsonBaselineStore |
| `src/Collector/Collector.Real.cs` ★★ | **RealCollector.Read**：LHM UpdateVisitor 遍历、VCore/频率/温度/每核弹性匹配、产出 SensorDump |
| `src/Collector/HwinfoShm.cs` ★★ | HWiNFO **SM2 共享内存**只读、互斥体、状态魔数、按 ReadingSize 步进、ParseCoreVids 正则、标签 dump、注册表开关提示 |
| `src/Collector/FullLoadStability.cs` ★ | 60s 滑窗满载稳定性（Add/CurrentRatio/StableSeconds/IsStable），纯逻辑可单测 |
| `src/Collector/Compat.cs` | 跨主板兼容：Field/NameRule、SensorNameRules(VCore/温度/频率/每核正则)、Sanity、SensorProbe、BoardCache |
| `src/Collector/ProbeMode.cs` | 探测模式枚举/状态/会话（AutoOnFirstRun 等）与字段映射结果 |
| `src/Collector/Logger.cs` ★ | 进程单例 Logger：JSON 行、按天 logs/yyyyMMdd.log、双输出、四级、线程安全、内存缓冲 |
| `src/Collector/Diagnostic.cs` ★ | 诊断 report record、DiagnosticBuilder、DiagnosticStore（落 reports、保留10、打诊断包 zip） |
| `src/Collector/baseline.json` ★ | 9 型号量表（随包复制到输出，可热更新） |
| `tests/Collector.Tests.cs` / `.csproj` | 采集层单测（40 项，含 SM2 真机标签、滑窗、质控） |
| `run_all_tests.py` | 采集层 7 个 verify_* 套件总入口 |
| `verify_naming.py / verify_compat.py / verify_probe_mode.py / verify_logging.py / verify_real_scenarios.py / verify_e2e.py / verify_structure.py` | 采集层各专项 Python 校验 |
| `regression.py` | 早期回归脚本 |
| `LHM字段映射与采集校准手册.md` ★ | 真机对接 LHM 的完整指引（命名/映射/校准 Step1-4） |
| `LHM传感器命名校准.md` `LHM跨主板兼容设计.md` | 命名来源/Zen 差异；四层兼容设计 |
| `WMI选项决策.md` `众包映射库决策.md` `探测模式决策.md` `README.md` | 采集层决策与说明（归档参考） |

## 评分引擎（`评分引擎/`，ScoringEngine，纯 net8.0 跨平台）
| 文件 | 职责 |
|---:|---|
| `src/ScoringEngine/ScoringEngine.csproj` | 纯类库 net8.0（真机修正阶段起正式参与编译） |
| `src/ScoringEngine/ScoringEngine.cs` ★★ | ScoreInput/ScoreResult/Baseline；Calibration；**SpScaler**(折算/SP/五档/颜色)；ZenProfile(代际斜率)；BaselineResolver(三级解析)；VfFit(最小二乘/R²)；**ScoreEngine**(三模式)；PerCoreVidQuality；ICloudStats 预留 |
| `src/ScoringEngine/Exporter.cs` | ScoreReport/CoreRow；JSON+Markdown 导出；Build(...,appVersion)；每核条形图/水印/口径说明 |
| `tests/ScoringEngine.Tests.cs` / `.csproj` | 引擎单测（50 项） |
| `verify_scoring.py` | 算法 Python 镜像校验 |
| `README.md` | 引擎说明 |
