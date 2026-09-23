# WPF 界面开发说明（V1.7.0）

> 范围：按评审确认的四条决策（深色 / WPF / 首版三块 / 不做托盘）完成 UI 开发
> 新增：**五档评级**（大雷 / 小雷 / 普通 / 小雕 / 大雕）
> 交付：可编译的 WPF 工程（23 个文件）+ UI 静态校验 78 项 + 全量回归全绿

---

## 一、本次决策落地

| 你的决策 | 落地 |
|---------|------|
| 深色主题 | `Theme/Styles.xaml` 全套深色资源（#0B0E14 底 + AMD 橙 #FF6B35 强调色） |
| WPF（由我定） | `net8.0-windows` + `UseWPF=true`；零第三方 NuGet，图表全部自绘 |
| 首版三块 | 仅「开始评测 / 评分详情 / 设置」，去掉概览与历史（后者可后续加） |
| 不做托盘常驻 | 无开机自启、无最小化托盘，聚焦单次评测 |
| ★ 五档评级 | 大雕 ≥112 · 小雕 ≥104 · 普通 ≥96 · 小雷 ≥88 · 大雷 <88 |

**评级阈值设计**：以 SP=100（=参考电压）为中心，每档 8 分上下对称。
百分制轨另设阈值（90/70/30/10），按正态分布合理分档。
`SpScaler.GradeColor()` 直接给出展示色，避免界面层重复定义阈值。

---

## 二、工程结构（23 个文件）

```
UI/
├─ CPU体质评分.UI.csproj       net8.0-windows + UseWPF，引用采集层与评分引擎
├─ App.xaml / .cs              主题挂载 + 日志初始化 + 全局异常兜底
├─ MainWindow.xaml / .cs       导航壳（侧栏三项 + 顶栏状态）
├─ Theme/Styles.xaml           深色主题全套资源
├─ ViewModels/
│  ├─ ViewModelBase.cs         INotifyPropertyChanged + RelayCommand（零依赖）
│  ├─ MainViewModel.cs         导航 + 共享状态
│  ├─ RunViewModel.cs          4 步评测向导
│  ├─ ResultViewModel.cs       结果展示 + 导出
│  └─ SettingsViewModel.cs     设置（改动即生效 + 持久化）
├─ Views/                      RunView / ResultView / SettingsView（XAML + cs）
├─ Controls/                   RingGauge / VfChart / CoreBars（自绘）
├─ Services/
│  ├─ EvaluationService.cs     采集→质控→评分 编排
│  └─ AppSettings.cs           设置持久化 + 应用到 CollectorOptions
└─ Models/EvaluationResult.cs  UI 展示模型
```

---

## 三、架构要点

### 3.1 界面层不碰硬件/算法

评分引擎与采集层**已是纯 C# 类库**，UI 只做调用与展示：

```
View → ViewModel → EvaluationService → { Collector, ScoringEngine }
```

`EvaluationService` 是唯一编排点：环境自检 → 多点采样 → 质控 → 评分 → 组装 `EvaluationResult`。

### 3.2 质控逻辑复用，不重复实现

为支持"先多点采样、再对聚合结果质控"，在 `CollectorOrchestrator` 上**抽出 `Validate(HardwareFeatures)` 方法**（V1.7.0 新增），
UI 服务层直接调用，判定顺序与编排器内部一致（合理性 → 满载 → 电压温度门禁），避免两处逻辑漂移。

### 3.3 异步不阻塞 UI

- 采样与评分走 `Task.Run` + `IProgress<T>` 上报进度
- 实时读数用 `DispatcherTimer`（800ms 刷新）
- **烤机时 UI 卡住会被当成程序死了**，这是硬性要求

### 3.4 图表零第三方依赖

V/F 散点、拟合直线、锚点竖线、核心柱状、SP 环形全部继承 `Control` 重写 `OnRender`，
用 `DrawingContext` 直接绘制。原型（HTML/SVG）里的坐标换算逻辑已 1:1 移植。

---

## 四、评测流程（4 步向导）

| 步 | 名称 | 界面 | 关键行为 |
|----|------|------|---------|
| 1 | 环境自检 | 4 项 ✓/✕ 清单 | 任一失败 → 停在原步并说明修复方法，**不放行** |
| 2 | 等待满载 | 实时四项读数 + 门槛文案 | 显示 `≥ MaxBoost × 0.90`；未达满载也可尝试（由质控最终拒绝） |
| 3 | 多点采样 | 实时 V/F 散点图逐点出现 | 进度条 + 拟合直线 |
| 4 | 完成 | SP + 评级 | 自动跳结果页 |

**第 3 步的实时 V/F 图是关键设计**：把抽象的折算变成看得见的直线，用户能直观理解"为什么不同烤机软件结果一致"。

---

## 五、保留的诚实标注（不允许被"优化"掉）

1. **每核 VID = CPPC 请求值**：Zen3/4/5 统一供电，不存在每核独立电压；核间排名仅供参考；不声称能复刻华硕每核 SP
2. **口径误差**：VRef 用公开评测 VID 口径，实测 VDDCR 口径，约 ±10mV → SP ±3~5 分
3. **Estimated 水印**：未标定型号顶部黄色警示，声明"不可直接对标华硕，仅供同型号横向比较"
4. **参数来源表**：V_ref / 锚点 / 斜率 / 标定等级逐条标来源，不做黑盒

---

## 六、真机构建（Windows）

```powershell
# 1) 还原 + 构建（需 .NET 8 SDK，含 Windows Desktop 工作负载）
dotnet build UI\CPU体质评分.UI.csproj -c Release

# 2) 运行（★ 必须管理员，否则读不到电压）
dotnet run --project UI\CPU体质评分.UI.csproj -c Release

# 3) 发布（★ 文件夹发布，禁止 --single-file）
dotnet publish UI\CPU体质评分.UI.csproj -c Release -r win-x64 --self-contained true -o publish
```

> ⚠️ **禁止 `--single-file`**：LHM 0.9.6 依赖 PawnIO 内核驱动，驱动资源以嵌入资源形式释放到磁盘；
> 单文件发布会导致驱动释放失败、传感器读数全 0（已在真机验证的死路）。

若需启用真实 LHM 采集：
```powershell
dotnet add UI\CPU体质评分.UI.csproj package LibreHardwareMonitorLib --version 0.9.6
# 并取消 Collector.Real.cs 中的真实采集块注释，编译时加 -p:ENABLE_LHM_REAL=true
```

---

## 七、验证

```
$ python3 run_all.py
▶ 采集层回归   ✅（7 项 107 用例）
▶ 评分引擎     ✅（44 用例，含五档评级 11 项）
▶ WPF UI 层    ✅（78 项结构校验）
🎉 全量回归通过
```

UI 校验覆盖：文件齐全、csproj 配置、主题资源、MVVM 基础设施、4 步向导行为、
结果页诚实标注、设置项绑定、自绘控件、服务层编排、五档评级。

---

## 八、诚实提示（务必知悉）

1. **WPF 只能在 Windows 编译，沙盒是 Linux 且无 .NET SDK** —— 本轮做的是**静态结构校验**（78 项），
   **不是编译期验证**。你在 `dotnet build` 时仍可能遇到编译错误（XAML 绑定路径、命名空间、
   `SelectedValue` 类型转换、`InitializeComponent` 生成等）。**请把报错贴来，我据此修正。**

2. **当前采集走骨架模拟数据**（`LhmCollector.Collect()` 默认返回 `Simulate()`），
   UI 流程完整可跑，但读数不是真机值。启用真实 LHM 需按 §六 操作。

3. **XAML 中 ComboBox 的 `SelectedValue` 用 Tag 字符串绑定数值属性**（如 `0.90` → double），
   依赖 WPF 的类型转换；若绑定失败请改为在 VM 中暴露数值集合。

4. **首版无历史记录与多型号对比**（按决策裁剪），如需可后续加回。
