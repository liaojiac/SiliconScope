# 03 · Debug 排查手册（启动崩溃 / 传感器 / HWiNFO / 评分）

> 本软件排障史几乎都在「Windows 真机双击 exe」这一环。沙盒只能交叉编译，渲染/传感器必须真机复现。
> 本篇把**已踩过并修复的坑**全部固化，接手者遇到现象先查这里与文末「症状速查表」，不要重复试错。

---

## 1. 排查体系（四类产物，各司其职）

| 产物 | 路径 | 内容 | 何时用 |
|---|---|---|---|
| 结构化日志 | 程序目录 `logs/yyyyMMdd.log` | JSON 行：时间(UTC)/级别/消息/context/异常 | 绝大多数问题先看它 |
| 诊断报告 | `reports/diagnostic_时间戳_短id.json` | 环境信息 + **全传感器原始 dump** + 字段匹配 + 校验结果 | 传感器命名/匹配/质控问题 |
| 本机历史 | `history/score_*.json` | 每次成功评测的完整数据 | 评分结果复盘 |
| 宿主跟踪 | `hosttrace.txt`（由诊断启动.bat 生成） | .NET host/apphost 级跟踪 + 退出码 | **双击一闪而过、logs 都没生成** |
| 诊断包 | `诊断包/诊断包_*.zip` | logs+reports+缓存打包 | 控制台链路一键收集 |

**启动都起不来（无 logs）时**：运行发布目录里的 `诊断启动.bat`（已 `cd /d %~dp0`、开
`COREHOST_TRACE=1`、`start /wait` 留住窗口并打印退出码）。让用户回传：
① 诊断窗口截图 ② `hosttrace.txt` ③ `logs/` 目录（若有）。

**能启动但读数/评分异常**：让用户回传当天 `logs/yyyyMMdd.log` + 最新一份 `reports/diagnostic_*.json`
（里面有全量 SensorDump，无需在你本机复现）。HWiNFO 标签不匹配时，日志里有
`DumpCoreLikeVoltages` 打出的全部 core/vid/vddcr 电压标签原始名。

---

## 2. 启动崩溃专题：「五连修」

共同外在表现：**双击 `SiliconScope.exe`，黑色 cmd 窗口一闪而过，主窗口不出现，往往连 logs 都没有。**
五个根因彼此独立、是逐次真机暴露的，按下面顺序理解。

### 2.1 过早打开传感器/驱动
- 根因：早期在 `MainViewModel` 构造函数里同步 `CheckEnvironment()→Collect()→Computer.Open()`，
  会立刻加载 PawnIO 内核驱动。该构造发生在主窗口创建极早期，全局异常处理还没挂；驱动若被安全组件
  拦截/加载失败，异常直接终止进程，表现为闪退、无日志无弹窗。
- 修复：构造函数只初始化对象、状态置「正在检测运行环境…」；主窗口 `Loaded` 后用 `Task.Run`
  调 `StartBackgroundProbe()` 后台自检，失败只更新状态条；用户点「开始评测」时还会再检一次。
- 代码位置：`UI/ViewModels/MainViewModel.cs`（构造函数注释 + StartBackgroundProbe）、
  `UI/MainWindow.xaml.cs`（`Loaded += StartBackgroundProbe`）。
- 识别：日志若停在很早、或无日志而事件查看器有驱动/.NET 异常，往这个方向查。**铁律：构造期不碰硬件。**

### 2.2 中文程序集名导致 apphost 静默失败
- 根因：程序集曾用中文名。简中 Win10（非 UTF-8、代码页 936/GBK）下，apphost/hostfxr 定位入口程序集
  失败，进程在宿主层就退出——**这发生在托管代码之前，所以 try/catch、全局异常、Logger 全都接不到，
  也没有 logs**。cmd 里直接运行也是无输出静默返回。
- 修复：`AssemblyName` 改纯 ASCII **`SiliconScope`**（窗口标题、界面文字仍可中文）。
- 代码位置：`UI/SiliconScope.csproj` 的 `<AssemblyName>SiliconScope</AssemblyName>`（注释写明原因）。
- 识别：`诊断启动.bat` 的 hosttrace 指向入口程序集解析失败；改回中文名必现。**铁律：产物名纯 ASCII。**

### 2.3 样式资源颜色键同名冲突
- 根因：`Theme/Styles.xaml` 里 `Color` 与 `SolidColorBrush` 曾用了同一个 `x:Key`，WPF 资源解析在
  加载 App.xaml/Styles.xaml（`new App()→InitializeComponent`，主窗口创建之前）阶段抛异常，主窗口创建失败。
- 修复：颜色与画刷分别命名（`BgColor`/`BgBrush`、`TextColor`/`TextBrush` …）。
- 代码位置：`UI/Theme/Styles.xaml` 顶部 Color/Brush 定义。
- 识别：异常堆栈在 `InitializeComponent` / ResourceDictionary / StaticResource；改资源后启动即崩优先查键名。

### 2.4 导航 ListBox 初始化期触发选择事件
- 根因：`MainWindow.xaml` 导航 `ListBox` 设了 `SelectedIndex="0"`，`InitializeComponent` 的 EndInit
  阶段就触发 `SelectionChanged`；此时排在它后面 XAML 创建的命名控件（如 TitleText）**尚未实例化**，
  事件处理里访问命名控件 → NullReferenceException → 整个主窗口在 InitializeComponent 阶段创建失败。
- 修复：code-behind 用 `_ready` 门控，初始化未完成直接忽略选择事件，组件树建好后 `SyncNav()` 同步一次；
  所有命名控件访问加空值保护（`TitleText is null` 直接 return）。
- 代码位置：`UI/MainWindow.xaml.cs`（`_ready`、Nav_SelectionChanged、SyncNav、UpdateTitle）。
- 识别：堆栈在主窗口 InitializeComponent、SelectionChanged、命名控件 NRE。

### 2.5 Run.Text 默认 TwoWay 绑只读属性
- 根因：`<Run Text="{Binding ...}"/>` 的 `Text` 属性默认绑定模式是 **TwoWay**，绑到只有 getter 的
  只读属性时，WPF 尝试回写即抛异常，启动加载该视图就崩。
- 修复：所有 `Run.Text` 绑定显式 `Mode=OneWay`（RunView/ResultView 里所有数值 Run 均已标 OneWay）。
- 代码位置：`UI/Views/RunView.xaml`、`ResultView.xaml`（grep `Run Text=` 可见均带 OneWay）。
- 识别：异常与 TwoWay binding / readonly property / `Run.Text` 有关。

### 2.6 启动兜底（最后防线）
即使上面都漏了，也要能看到错误而不是一闪而过：
- `App` 静态构造（早于 Application/窗口创建、Logger 只依赖文件 IO）初始化日志，挂
  `AppDomain.CurrentDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`；
- `OnStartup` 挂 `DispatcherUnhandledException`（`e.Handled=true` 尽量续命），再手动建主窗口；
- 任何失败 `TryLog` + Win32 `MessageBox.Show`（MessageBox 不依赖 Application.Current）。
- 位置：`UI/App.xaml.cs`。新增启动早期逻辑时务必保持这条兜底链不被破坏。

---

## 3. 传感器 / 读数专题

### 3.1 传感器全 0 / 读不到（常见：单文件发布）
- 现象：自检「LHM 驱动实测」标红或 0 传感器，关键量缺失。
- 头号原因：**用了 single-file 发布**。LHM 的 PawnIO 驱动固件内嵌在 `LibreHardwareMonitorLib.dll`，
  运行时要释放到临时目录；单文件打包释放失败 → 驱动起不来、读数全 0（真机复现两次）。
- 处置：必须文件夹自包含发布（`PublishSingleFile=false`），发布目录要含
  `SiliconScope.exe / LibreHardwareMonitorLib.dll / HidSharp.dll / Collector.dll / ScoringEngine.dll /
  baseline.json` 等整套。让用户**解压整个文件夹**再运行，不要只拖出 exe、不要在压缩包内双击。
- 次因：未管理员运行（读 MSR/电压必须提权）；杀软拦截驱动（不要让用户关 HVCI，但若第三方杀软拦截
  需放行 LHM/PawnIO）。

### 3.2 核心电压采不到 / 采错轨（SVI2 vs SVI3 / VDDCR）
- 背景：AM5/Zen4 走 SVI3，LHM 核心电压传感器名是 **`VDDCR CPU`**；旧代码写死 SVI2 时代的
  `Core (SVI2 TFN)`，精确匹配永远落空 → VCore 恒空。
- 处置：用 `Compat.SensorNameRules.VCore` 弹性匹配（精确 VDDCR CPU / Core (SVI2 TFN)，包含
  VDDCR/SVI2/SVI3/Core Voltage/CPU Core，排除 SoC/Misc/LDO/SMU），并要求值在合理区间。
- 若新主板/新架构又出现新名字：看 `reports/diagnostic_*.json` 的 SensorDump 里实际电压传感器名，
  往 `Exact/Contains` 加，同时确认排除词不会误杀；`RationalityCheck` 仍会兜底拦离谱值。

### 3.3 每核 VID 全是 ~0.43V、最佳=最差（LHM 误解码 SVI3）
- 根因：LHM 0.9.x（含 master）对 Zen4/5 仍用 SVI2 公式解 MSR 0xC0010293，满载 8 核都解到 ~0.43–0.45V
  最低档占位值且彼此相同，与整颗 VDDCR（真机已 1.13V）完全脱节。
- 处置（已内置）：`PerCoreVidQuality` 把这种坏数据整组置 null，不输出核间排名/分布，避免假结论；
  要正确每核 VID 走 HWiNFO（见 §4）。**不要试图靠「升级 LHM」解决**——master 至今仍是 SVI2 公式。
- 想彻底摆脱 HWiNFO：需自研解码（借 PawnIO 读 MSR raw code，再按 SVI3 编码表换算），属未启动方向，
  见 `05_已知限制与后续规划.md`，需先核实编码表与 PawnIO 能否取到 raw code 并多轮真机校准。

### 3.4 满载判定异常（误判未满载 / 假满载）
- 基准必须是**全核 MaxBoost**，不是基础频率（早期错用 ReferenceFreq 4200×0.95≈3990，轻载都能越过，
  门禁形同虚设，会放行假大雕）。阈值 0.90 是真机校准（7800X3D OCCT 全核稳态约 4526MHz，门槛 4500）。
- 重负载（Prime95 AVX-512、撞功耗墙）频率可低至 4.3GHz 量级仍应算满载——这由 0.90 阈值 + V/F 折算容纳。
- 若新型号满载被误判：核对 `baseline.json` 的 `MaxBoostMHz` 是否正确；阈值是「能否评」的门，不进分数，
  不要为了让某次测试通过而随意调低阈值（会放进非满载样本）。
- 采样窗口内满载轮次 <80% 直接失败重测（`EvaluationService.Evaluate`），防止中途掉载仍出分。

### 3.5 内核隔离 / 内存完整性（HVCI）——明确不是问题
- 真机 HVCI 开启时 LHM 经 PawnIO 仍读到 74 个传感器。**不要让用户关内核隔离/内存完整性/杀毒**，
  这既不安全也无必要（CPU-Z/AIDA64/OCCT 同样不需要）。自检里该项恒通过、仅展示状态。
- 代码：`LhmCollector.IsMemoryIntegrityEnabled()` 仅读注册表展示；`CheckEnvironment()` 不因 HVCI 否决。

### 3.6 深色背景下文字发黑看不清
- 根因：部分 TextBlock/数值没显式给前景，WPF 默认黑字落在深色卡上不可读（真机红框反馈过）。
- 处置：所有文本显式用 TextBrush/MutedBrush/DimBrush 等浅色；根节点设 `TextElement.Foreground`。
  新增界面务必沿用 Styles.xaml 画刷，不要依赖默认前景。

### 3.7 额定/满载频率显示
- 界面「基础频率（仅展示）」取 `ReferenceFreqMHz`，满载门槛与实时满载判定取 `FullLoadFreqMHz`
  （MaxBoost）。两者不要混用（曾因混用导致门槛错误，见 3.4）。

---

## 4. HWiNFO 每核 VID 专题（已修复，真机确认成功）

### 4.1 连不上的根因：共享内存名用错（SF vs SM2）
- 现象：开着 HWiNFO，日志反复 `HWiNFO 增强不可用，回退 LHM：未找到 HWiNFO 共享内存`。
- 根因：旧实现用了已废弃私有格式名 `HWiNFO_SENS_SF`（布局也不同），`OpenExisting` 必然失败、
  一次都连不上。HWiNFO v7+（真机 v8.34）真名是 **`Global\HWiNFO_SENS_SM2`**，互斥体
  **`Global\HWiNFO_SM2_MUTEX`**。
- 修复：`HwinfoShm.MapNames/MutexNames` 用 SM2（含无前缀兜底）、加官方互斥体、按表头 ReadingSize 步进。
  **禁止再改回 SF。** 权威实现：hwinfo-go（见附录C 链接）。

### 4.2 用户侧开关（v7 起默认关闭）
右键任务栏托盘 HWiNFO 图标 → 设置(Settings)（**主设置，不是传感器窗口的设置**）→
「通用/用户界面(General/User Interface)」标签**右列** → 勾选「共享内存支持(Shared Memory Support)」
→ 确定后**重启 HWiNFO**。免费版每运行 12 小时该接口失效（状态魔数变 DAED），重启 HWiNFO 即可。
- 只读共享内存：不需要管理员、不装驱动、不关安全功能。
- 安装版开关注册表：`HKCU\Software\HWiNFO64\Sensors\SensorsSM`=1（另有 HWiNFO32 键）；便携版写 HWiNFO64.INI。
  `NotFoundHint()` 据此区分「开关没开 / 开了但没运行 / 过期」，读不到给通用指引。

### 4.3 连上但解析不到每核 VID（标签差异）
- 真机标签（简中 HWiNFO v8.34，英文 OrigLabel）：汇总 `Core VIDs`（无数字，必须排除）、
  每核 **`Core 0 VID`…`Core 7 VID`（0 起始、无 # 号）**。正则已覆盖 `Core N VID`/`Core #N VID`/
  `VID ... Core N`，核号前导零容错（`0*(\d+)`，避免 16 核 `Core 10` 被前导零规则吃坏），≥2 核才成功。
- 解析失败时日志 Warn「HWiNFO 已连接但未解析到每核 VID」并带 `labels`（全部 core/vid/vddcr 电压项）。
  新标签语种/命名不匹配时，让用户回传这段 labels，据实改正则/排除词，并把真机标签写进
  `Collector.Tests` 单测（现有用例含 8 核真实值与 16 核核号断言）。

### 4.4 不开 HWiNFO 的预期行为
主 SP 分照出（整颗 VDDCR 由 LHM 正确读取），仅结果页/报告没有每核 VID 分布图，数据源标 LHM-only。
这是设计行为，不是 bug；设置页与「使用前必读」已写明。

---

## 5. 评分异常专题

| 现象 | 可能原因 | 定位/处置 |
|---|---|---|
| 同一颗 CPU 不同烤机分数差很多 | 未走 V/F 折算 / 采样未满载 | 确认用的是锚点折算口径；核对两负载点折算后 V_anchor 是否接近；查日志满载轮次占比 |
| 分数普遍偏高（「假大雕」） | 满载门槛用了基础频率、阈值过低、采到非满载 | 见 3.4；确认 baseline 的 MaxBoost；查 reports 校验段 FullLoadOk |
| 每核最佳=最差 / 每核 0.43V | LHM 误解 SVI3 | 见 3.3，应被置 null；要分布开 HWiNFO（§4） |
| 提示「未收录型号」 | baseline.json 无该型号且关键字不命中 | 加量表条目（VRef/MaxBoost/代际/锚点/斜率/标定等级），未知型号先 Estimated |
| Estimated 水印 | 该型号非 Calibrated | 正常；积累真机/众包样本后改 Calibrated 并补 VRef/锚点/斜率出处 |
| 「采样 N 秒内未持续满载」 | 采样窗口满载轮次<80% | 让用户保持烤机满载；确认烤机确实全核，而非瞬时 |
| 报告抬头版本是 dev | Exporter.Build 没传 appVersion | UI 已传 AppInfo.Version；命令行链路自行传入 |
| 评测完成后按钮全灰、只能重启 | Step=3 无复位入口 | 已修（RestartCmd/ResetWizard）；若复现查命令 CanExecute 与 RefreshCommands |

评分系数（scale=300、五档阈值、代际斜率、锚点比例 0.94、质量门限）**无新校准证据不要改**；
改动必须在代码注释与 `01_开发文档` 写真机/数据依据，并同步 Python 镜像与 C# 单测。

---

## 6. 日志关键字速查（grep 当天 logs）

- 启动：`应用启动`（带 version）；致命：`致命未处理异常` / `主窗口创建失败` / `未处理异常`。
- LHM：`开始主采集 (LHM)` / `主采集完成` / `LHM 关键量缺失` / `LHM 打开失败` / `LHM 采集异常`。
- HWiNFO：`HWiNFO 每核 VID 已获取`（成功）/ `HWiNFO 增强不可用，回退 LHM`（连不上，带 reason）/
  `HWiNFO 已连接但未解析到每核 VID`（带 labels）/ `已采用 HWiNFO 每核 VID` /
  `HWiNFO 每核 VID 未通过有效性判定`。
- 质控：`合理性校验未通过` / `未达满载，拒绝评分` / `质控门禁未通过`。
- 每核：`每核 VID 读数无效（该平台 LHM 返回最低档占位值）`。
- 采样：`采样的约 10 秒内未持续满载`（UI 错误文案）。

---

## 7. 新增/修改代码时的排障守则

1. 任何启动早期（App 静态构造、OnStartup、MainWindow 构造、XAML 资源）改动，都要在真机冷启动验证，
   且保留 App.xaml.cs 的异常兜底；XAML 资源键唯一、Run.Text 显式 OneWay、导航事件 _ready 门控。
2. 任何硬件访问只能在窗口 Loaded 后的后台线程或用户显式操作中发生。
3. 新传感器字段：先在 SensorDump 确认真名 → 加弹性匹配 + 排除词 + 合理性区间 → 加单测/静态断言。
4. 新阈值/公式：写入代码注释（真机依据）+ 同步 Python 镜像回归 + C# 单测；跑「故意改错能报红」反向验证。
5. 发布相关改动后，按 `04_构建发布与测试手册` 的发布验收清单逐项核对（尤其 single-file、Collector.exe、
   编码 CRLF、UTF-16 strings 核对）。

---

## 8. 已验证死路（不要再试）

- ❌ single-file / 单 exe 发布：PawnIO 内嵌资源释放失败、读数全 0（两次真机验证）。必须文件夹自包含。
- ❌ 让用户关内核隔离/内存完整性(HVCI)：错误前提，PawnIO 兼容 HVCI，且不安全。
- ❌ 中文程序集名：GBK 环境 apphost 静默失败、无日志。永久用 SiliconScope。
- ❌ 升级 LHM 修 Zen4 每核 VID：master 仍套 SVI2 公式，无解；要正确值走 HWiNFO 或自研 SVI3。
- ❌ HWiNFO 映射名用 SF/SF1..8：废弃私有格式，必须 SM2。
- ❌ Wine 跑 WPF 冒烟：缺 .NET8 桌面 API，跑不起来；沙盒只做交叉编译 + 静态/逻辑校验。
- ❌ 用基础频率当满载基准、或为单次测试随意调低满载阈值：会放行非满载样本、产生假大雕。
