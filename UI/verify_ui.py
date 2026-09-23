"""
verify_ui.py — WPF UI 层静态结构校验
说明：WPF 只能在 Windows 编译，沙盒（Linux）无 .NET SDK，
     故做结构性 preflight：文件齐全 / 命名空间 / 绑定属性存在 / XAML 引用一致。

通过标准：全部断言通过（exit 0）
"""
import os, re, sys

ROOT = os.path.dirname(os.path.abspath(__file__))
def read(p):
    full = os.path.join(ROOT, p)
    if not os.path.exists(full):
        return ""
    return open(full, encoding="utf-8").read()

P, F = 0, 0
def check(n, c):
    global P, F
    if c: P += 1; print(f"  ✅ {n}")
    else: F += 1; print(f"  ❌ {n}")

print("=" * 64)
print("UI 层静态结构校验（WPF）")
print("=" * 64)

# ---------- 1. 文件齐全 ----------
print("\n[1] 工程与文件")
FILES = [
    "SiliconScope.csproj", "App.xaml", "App.xaml.cs",
    "MainWindow.xaml", "MainWindow.xaml.cs",
    "Theme/Styles.xaml",
    "ViewModels/ViewModelBase.cs", "ViewModels/MainViewModel.cs",
    "ViewModels/RunViewModel.cs", "ViewModels/ResultViewModel.cs",
    "ViewModels/HistoryViewModel.cs", "ViewModels/SettingsViewModel.cs",
    "Views/RunView.xaml", "Views/RunView.xaml.cs",
    "Views/ResultView.xaml", "Views/ResultView.xaml.cs",
    "Views/HistoryView.xaml", "Views/HistoryView.xaml.cs",
    "Views/SettingsView.xaml", "Views/SettingsView.xaml.cs",
    "Controls/RingGauge.cs", "Controls/VfChart.cs", "Controls/CoreBars.cs",
    "Services/EvaluationService.cs", "Services/AppSettings.cs",
    "Services/HistoryStore.cs", "AppInfo.cs",
    "Models/EvaluationResult.cs", "Models/HistoryEntry.cs",
]
for f in FILES:
    check(f"存在 {f}", os.path.exists(os.path.join(ROOT, f)))

csproj = read("SiliconScope.csproj")
check("csproj: net8.0-windows + UseWPF（WPF 必需）",
      "net8.0-windows" in csproj and "<UseWPF>true</UseWPF>" in csproj)
check("csproj: 引用采集层", "Collector.csproj" in csproj)
check("csproj: 引用评分引擎", "ScoringEngine.csproj" in csproj)
check("csproj: baseline.json 复制到输出目录", "baseline.json" in csproj)
check("csproj: 产品更名 SiliconScope（程序集/根命名空间均为纯 ASCII）",
      "<AssemblyName>SiliconScope</AssemblyName>" in csproj
      and "<RootNamespace>SiliconScope.UI</RootNamespace>" in csproj)

# ---------- 1b. 更名彻底性：源码不得残留旧名 CpuScore ----------
import glob as _glob_rename
_old_name_hits = []
for _pat in (os.path.join(ROOT, "**", "*.cs"), os.path.join(ROOT, "**", "*.xaml"),
             os.path.join(ROOT, "*.csproj"), os.path.join(ROOT, "诊断启动.bat")):
    for _f in _glob_rename.glob(_pat, recursive=True):
        if "CpuScore" in open(_f, encoding="utf-8").read():
            _old_name_hits.append(os.path.relpath(_f, ROOT))
check("UI 源码/脚本无旧名 CpuScore 残留（已全部更名 SiliconScope）", not _old_name_hits)
if _old_name_hits:
    print("    残留：", _old_name_hits)

# ---------- 2. 主题与 App ----------
print("\n[2] 主题与入口")
theme = read("Theme/Styles.xaml")
check("主题: 深色背景 #0B0E14", "#0B0E14" in theme)
check("主题: AMD 橙强调色 #FF6B35", "#FF6B35" in theme)
check("主题: BoolToVis 转换器已声明", "BooleanToVisibilityConverter" in theme)
check("主题: 主要样式齐全（Card/Primary/Ghost/NavItem/Switch）",
      all(k in theme for k in ['x:Key="Card"', 'x:Key="Primary"', 'x:Key="Ghost"',
                               'x:Key="NavItem"', 'x:Key="Switch"']))
app = read("App.xaml.cs")
check("App: 启动初始化日志", "Logger.Instance.Configure" in app)
check("App: 全局异常兜底（不静默崩溃）", "DispatcherUnhandledException" in app)
check("App.xaml 引用主题", "Theme/Styles.xaml" in read("App.xaml"))

# ---------- 3. MVVM ----------
print("\n[3] MVVM 基础设施")
base = read("ViewModels/ViewModelBase.cs")
check("ViewModelBase: INotifyPropertyChanged", "INotifyPropertyChanged" in base)
check("RelayCommand: ICommand 实现", "ICommand" in base and "CanExecute" in base)
mainvm = read("ViewModels/MainViewModel.cs")
check("MainVM: 四页导航枚举（Run/Result/History/Settings）",
      all(k in mainvm for k in ["Run,", "Result,", "History,", "Settings"]))
check("MainVM: 导航可见性属性", all(k in mainvm for k in ["IsRun", "IsResult", "IsHistory", "IsSettings"]))
check("MainVM: 评测完成自动跳转结果页", "OnEvaluated" in mainvm)
check("MainVM: 评测完成落本地历史", "HistoryStore" in mainvm and ".Save(" in mainvm)

# ---------- 4. 评测向导 ----------
print("\n[4] 评测向导（4 步）")
runvm = read("ViewModels/RunViewModel.cs")
check("RunVM: 步骤字段 Step", "public int Step" in runvm)
check("RunVM: 环境自检命令", "DoEnvironment" in runvm)
check("RunVM: 等待满载（实时读数定时器）", "DispatcherTimer" in runvm)
check("RunVM: 满载门槛文案（MaxBoost×阈值）", "MaxBoost ×" in runvm)
check("RunVM: 多点采样进度上报", "Progress<" in runvm)
check("RunVM: 采样失败回到等待页（可重试）", "TipTitle = \"无法评分\"" in runvm)
check("RunVM: 采样前停止定时器（防资源泄漏）", "StopLive();" in runvm)

# ---------- 5. 结果页 ----------
print("\n[5] 结果页与诚实标注")
resvm = read("ViewModels/ResultViewModel.cs")
check("ResultVM: 空态支持", "NoResult" in resvm and "HasResult" in resvm)
check("ResultVM: 导出报告（复用 Exporter）", "Exporter.Build" in resvm)
check("ResultVM: 打包诊断包", "PackDiagnosticBundle" in resvm)
resx = read("Views/ResultView.xaml")
check("结果页: Estimated 水印（未标定型号）", "Result.IsEstimated" in resx)
check("结果页: 每核 VID 为 CPPC 请求值说明", "CPPC" in resx)
check("结果页: 参数来源表（不做黑盒）", "SlopeSource" in resx and "AnchorSource" in resx)
check("结果页: V/F 折算图", "VfChart" in resx)
check("结果页: 核心分布图", "CoreBars" in resx)
check("结果页: 环形 SP 仪表", "RingGauge" in resx)

# ---------- 6. 设置页 ----------
print("\n[6] 设置页")
setvm = read("ViewModels/SettingsViewModel.cs")
check("SettingsVM: 改动即生效（Apply 到 CollectorOptions）", "Settings.Apply()" in setvm)
check("SettingsVM: 持久化", "Settings.Save()" in setvm)
setx = read("Views/SettingsView.xaml")
check("设置页: HWiNFO 手动开启开关", "Setting.EnableHwinfo" in setx)
check("设置页: 探测模式下拉", "Setting.ProbeMode" in setx)
check("设置页: 多轮采样开关", "Setting.MultiSample" in setx)
check("设置页: 满载阈值可调", "Setting.FullLoadThreshold" in setx)
check("设置页: 明示不上传数据（本地闭环）", "本地闭环" in setx)
check("设置页: 五档评级说明", "大雕" in setx and "小雕" in setx and "小雷" in setx)

# ---------- 7. 图表控件 ----------
print("\n[7] 自绘图表控件（零第三方依赖）")
ring = read("Controls/RingGauge.cs")
check("RingGauge: SP 环形 + 评级色", "OnRender" in ring and "GradeColor" in ring)
vf = read("Controls/VfChart.cs")
check("VfChart: 散点 + 拟合直线 + 锚点竖线",
      all(k in vf for k in ["Points", "Slope", "AnchorFreq"]))
check("VfChart: Points 为 IEnumerable（兼容 ObservableCollection 绑定）",
      "IEnumerable<(double freq, double volt)>" in vf)
core = read("Controls/CoreBars.cs")
check("CoreBars: 按本次 min/max 归一化（非固定上界）",
      "v.Min()" in core and "v.Max()" in core)
check("CoreBars: 最优/最弱标记", "最优" in core and "最弱" in core)

# ---------- 8. 服务层 ----------
print("\n[8] 服务层（界面不碰硬件/算法细节）")
svc = read("Services/EvaluationService.cs")
check("EvaluationService: 环境自检", "CheckEnvironment" in svc)
check("EvaluationService: 多点采样循环", "CollectorOptions.SampleRounds" in svc)
check("EvaluationService: 每核 VID 取峰值", "Max(s => s.PerCoreVIDs![i])" in svc)
check("EvaluationService: 复用编排器质控（Validate）", "orch.Validate(main)" in svc)
check("EvaluationService: 未收录型号友好报错", "未收录型号" in svc)
check("EvaluationService: 输出参数来源（SlopeSource/AnchorSource）",
      "rb.SlopeSource" in svc and "rb.AnchorSource" in svc)

# ---------- 9. 五档评级（新增要求）----------
print("\n[9] ★ 五档评级（大雷/小雷/普通/小雕/大雕）")
se = read("../评分引擎/src/ScoringEngine/ScoringEngine.cs")
check("评分引擎: 五档齐全", all(g in se for g in ["大雕", "小雕", "普通", "小雷", "大雷"]))
check("评分引擎: 旧四档已清除（优质/偏弱不存在）",
      "优质" not in se and "偏弱" not in se)
check("评分引擎: 阈值对称（112/104/96/88，每档 8 分）",
      all(k in se for k in ["112", "104", "96", "88"]))
check("评分引擎: GradeColor 供 UI 取色", "GradeColor" in se)

# ---------- 10. XAML 资源完整性（编译期查不出、运行期加载才崩的问题）----------
print("\n[10] XAML 资源完整性（重复 key / 无法解析的 StaticResource）")
import glob as _glob
def _keys(t):
    return re.findall(r'x:Key="([^"]+)"', t)
_global_keys = set(_keys(read("Theme/Styles.xaml")))
_dup_global = [k for k in _keys(read("Theme/Styles.xaml")) if _keys(read("Theme/Styles.xaml")).count(k) > 1]
check("Styles.xaml 资源 key 无重复（Color 与 Style 不可同名）", len(_dup_global) == 0)

_bad_dup, _bad_ref = [], []
for _xf in sorted(_glob.glob(os.path.join(ROOT, "**", "*.xaml"), recursive=True)):
    _t = open(_xf, encoding="utf-8").read()
    _rel = os.path.relpath(_xf, ROOT)
    _lk = _keys(_t)
    _dup = {k for k in _lk if _lk.count(k) > 1}
    if _dup:
        _bad_dup.append(f"{_rel}:{sorted(_dup)}")
    _local = set(_lk)
    for _r in set(re.findall(r'(?:Static|Dynamic)Resource\s+([A-Za-z0-9_]+)', _t)):
        if _r not in _global_keys and _r not in _local:
            _bad_ref.append(f"{_rel}->{_r}")
check("所有 XAML 内部无重复 key", not _bad_dup)
check("所有 StaticResource/DynamicResource 引用均可解析", not _bad_ref)
if _bad_dup: print("    重复:", _bad_dup)
if _bad_ref: print("    未解析:", _bad_ref)

# ---------- 11. 启动期事件安全（InitializeComponent 顺序 → 空引用，真机崩溃模式）----------
print("\n[11] 启动期事件安全（静态初始选中项触发的 SelectionChanged 必须有就绪守卫）")
import re as _re
for _xf in sorted(_glob.glob(os.path.join(ROOT, "**", "*.xaml"), recursive=True)):
    _t = open(_xf, encoding="utf-8").read()
    _rel = os.path.relpath(_xf, ROOT)
    for _m in _re.finditer(r'<(?:ListBox|TabControl|DataGrid|ListView)\b([^>]*?SelectionChanged="(\w+)"[^>]*?)>', _t, _re.S):
        _tag, _handler = _m.group(1), _m.group(2)
        # 仅静态设置初始选中项（SelectedIndex/SelectedItem）才会在 EndInit 阶段触发事件
        if 'SelectedIndex=' not in _tag and 'SelectedItem=' not in _tag:
            continue
        _csf = _xf.replace('.xaml', '.xaml.cs')
        _cs = open(_csf, encoding='utf-8').read() if os.path.exists(_csf) else ''
        _hm = _re.search(r'\bvoid\s+' + _re.escape(_handler) + r'\s*\([^)]*\)\s*\{', _cs)
        _body = _cs[_hm.end():_hm.end() + 800] if _hm else ''
        check(f"{_rel}: {_handler} 对初始化期触发有就绪守卫(_ready/IsLoaded)",
              bool(_hm) and ('_ready' in _body or 'IsLoaded' in _body))

# ---------- 12. 数据绑定路径可解析（防 {Binding 不存在属性} 静默失效；值元组不得作绑定源）----------
print("\n[12] 数据绑定路径校验（属性真实存在 / ItemsControl 模板项可解析）")
_propre = _re.compile(r'public\s+(?:static\s+)?(?:override\s+|sealed\s+)?([\w\.\<\>\[\]\?,\s\(\)]+?)\s+(\w+)\s*[\{=]')
_ttypes = {}
for _cf in _glob.glob(os.path.join(ROOT, "**", "*.cs"), recursive=True):
    _src = open(_cf, encoding='utf-8').read()
    for _tm in _re.finditer(r'\b(?:sealed\s+|abstract\s+|partial\s+|static\s+)*(class|struct|record)\s+(\w+)', _src):
        _kind, _tn = _tm.group(1), _tm.group(2)
        _nxt = _re.search(r'\b(?:class|struct|record)\s+\w+', _src[_tm.end():])
        _blk = _src[_tm.end(): _tm.end() + _nxt.start()] if _nxt else _src[_tm.end():]
        _d = _ttypes.setdefault(_tn, {})
        if _kind == 'record':
            _pm = _re.search(r'\(([^)]*)\)', _src[_tm.end():_tm.end() + 400])
            if _pm:
                for _arg in _pm.group(1).split(','):
                    _w = _arg.strip().split()
                    if len(_w) >= 2: _d[_w[-1].lstrip('?')] = ' '.join(_w[:-1])
        for _pm in _propre.finditer(_blk):
            _d[_pm.group(2)] = _pm.group(1).strip()

def _elem_type(type_str):
    _g = _re.search(r'(?:ObservableCollection|List|IEnumerable|ICollection)\s*<\s*([\w\.\<\>\?]+)\s*>', type_str or '')
    if _g: return _g.group(1).split('.')[-1]
    if type_str and type_str.strip().endswith('[]'): return type_str.strip()[:-2]
    return None

_PREFIX = {'Run': 'RunViewModel', 'Result': 'ResultViewModel',
           'History': 'HistoryViewModel', 'Setting': 'SettingsViewModel'}
_ROOT = 'MainViewModel'
def _paths(text):
    for _m in _re.finditer(r'\{Binding([^}]*)\}', text):
        inner = _m.group(1).strip()
        if not inner or 'RelativeSource' in inner or 'ElementName' in inner: continue
        _pm = _re.search(r'Path=([^,}]+)', inner)
        if _pm: path = _pm.group(1).strip()
        else:
            tok = inner.split(',')[0].strip()
            if '=' in tok or not tok: continue
            path = tok
        if _re.match(r'^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$', path): yield path, _m.start()

_bad_bind = []
for _xf in sorted(_glob.glob(os.path.join(ROOT, "**", "*.xaml"), recursive=True)):
    _t = open(_xf, encoding='utf-8').read()
    _rel = os.path.relpath(_xf, ROOT)
    _spans = []
    for _dm in _re.finditer(r'<DataTemplate>(.*?)</DataTemplate>', _t, _re.S):
        _head = _t[:_dm.start()]
        _is = list(_re.finditer(r'ItemsSource="\{Binding\s+([\w\.]+)', _head))
        _etype = None
        if _is:
            chain = _is[-1].group(1).split('.')
            _ty = _ROOT
            if chain[0] in _PREFIX: _ty = _PREFIX[chain[0]]; chain = chain[1:]
            for seg in chain:
                _ty = _elem_type(_ttypes.get(_ty, {}).get(seg)) or _ty
            _etype = _ty
        _spans.append((_dm.start(1), _dm.end(1), _etype))
    for path, pos in _paths(_t):
        seg = path.split('.')
        in_tpl = next((e for (a, b, e) in _spans if a <= pos < b), None)
        if in_tpl:
            if in_tpl not in _ttypes or seg[0] not in _ttypes[in_tpl]:
                _bad_bind.append(f"{_rel} 模板项<{in_tpl}>.{path}")
            continue
        ty = _ROOT
        if seg[0] in _PREFIX:
            ty = _PREFIX[seg[0]]; seg = seg[1:]
        for s in seg:
            props = _ttypes.get(ty, {})
            if s not in props:
                _bad_bind.append(f"{_rel} {ty}.{path}（缺属性 {s}）"); break
            et = _elem_type(props[s])
            if et: ty = et
check("所有 Binding 路径属性均存在（含 ItemsControl 模板项）", not _bad_bind)
if _bad_bind:
    for x in sorted(set(_bad_bind)): print("    ", x)

# ---------- 13. 默认 TwoWay 属性不得绑定只读源（真机：Run.Text TwoWay 绑只读 ConfidenceText 崩溃）----------
print("\n[13] TwoWay 默认绑定不得指向只读属性（Run.Text/ProgressBar.Value/CheckBox.IsChecked/ComboBox.Selected*）")
_twoway = {
    'Run': {'Text'}, 'TextBox': {'Text'},
    'ProgressBar': {'Value'}, 'Slider': {'Value'}, 'ScrollBar': {'Value'},
    'CheckBox': {'IsChecked'}, 'RadioButton': {'IsChecked'}, 'ToggleButton': {'IsChecked'},
    'ComboBox': {'SelectedValue', 'SelectedItem', 'SelectedIndex'},
    'ListBox': {'SelectedItem', 'SelectedIndex'}, 'TabControl': {'SelectedItem', 'SelectedIndex'},
}
# 建立 类型 -> 只读属性集合（表达式体属性 / 无 set 的自动属性 / record 主构造参数）
_ro = {}
for _cf in _glob.glob(os.path.join(ROOT, "**", "*.cs"), recursive=True):
    _src = open(_cf, encoding='utf-8').read()
    for _tm in _re.finditer(r'\b(?:sealed\s+|abstract\s+|partial\s+|static\s+)*(class|struct|record)\s+(\w+)', _src):
        _kind, _tn = _tm.group(1), _tm.group(2)
        _nxt = _re.search(r'\b(?:class|struct|record)\s+\w+', _src[_tm.end():])
        _blk = _src[_tm.end(): _tm.end() + _nxt.start()] if _nxt else _src[_tm.end():]
        s = _ro.setdefault(_tn, set())
        if _kind == 'record':
            _pm = _re.search(r'\(([^)]*)\)', _src[_tm.end():_tm.end() + 400])
            if _pm:
                for _arg in _pm.group(1).split(','):
                    _w = _arg.strip().split()
                    if len(_w) >= 2: s.add(_w[-1].lstrip('?'))
        for _pm in _re.finditer(r'public\s+(?:static\s+)?[\w\.\<\>\[\]\?,\s\(\)]+?\s+(\w+)\s*(=>|\{)', _blk):
            _pn, _lead = _pm.group(1), _pm.group(2)
            if _lead == '=>':
                s.add(_pn)                       # 表达式体属性 => 只读
                continue
            _i = _pm.end() - 1                   # 指向 '{'，做花括号配对取整个访问器块
            _depth, _j = 0, _i
            while _j < len(_blk):
                if _blk[_j] == '{': _depth += 1
                elif _blk[_j] == '}':
                    _depth -= 1
                    if _depth == 0: break
                _j += 1
            _accessors = _blk[_i:_j + 1]
            if 'set' not in _accessors and 'init' not in _accessors:
                s.add(_pn)                       # { get; } / { get => … } 且无 set/init → 只读

_bad_tw = []
for _xf in sorted(_glob.glob(os.path.join(ROOT, "**", "*.xaml"), recursive=True)):
    _t = open(_xf, encoding='utf-8').read()
    _rel = os.path.relpath(_xf, ROOT)
    for _em in _re.finditer(r'<(' + '|'.join(_twoway) + r')\b([^>]*)>', _t, _re.S):
        _el, _body = _em.group(1), _em.group(2)
        for _am in _re.finditer(r'(\w+)="\{Binding([^}]*)\}"', _body):
            _attr, _inner = _am.group(1), _am.group(2)
            if _attr not in _twoway[_el]:
                continue
            if 'Mode=OneWay' in _inner or 'Mode=OneTime' in _inner:
                continue
            _pm = _re.search(r'Path=([^,}]+)', _inner)
            if _pm:
                _path = _pm.group(1).strip()
            else:
                _tok = _inner.split(',')[0].strip()
                if '=' in _tok or not _tok:
                    continue
                _path = _tok
            if not _re.match(r'^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$', _path):
                continue
            _seg = _path.split('.')
            _ty = _PREFIX.get(_seg[0], _ROOT)
            _last = _seg[-1]
            if _last in _ro.get(_ty, set()):
                _bad_tw.append(f"{_rel} <{_el} {_attr}> 绑定只读 {_ty}.{_path}（须显式 Mode=OneWay）")
check("默认 TwoWay 绑定未指向只读属性", not _bad_tw)
for x in sorted(set(_bad_tw)):
    print("    ", x)

# ---------- 14. 深色主题文本可读性：根元素设 TextElement.Foreground 浅色兜底（真机：裸 TextBlock/Run 黑字）----------
print("\n[14] 深色主题文本可读性：窗口/页面根元素设置 TextElement.Foreground 浅色兜底")
# 用附加属性继承兜底（优先级低于样式/本地值/控件模板），既让裸 TextBlock、未着色 Run 变浅，
# 又不覆盖 Muted 样式、显式 Foreground、按钮/导航模板的强调色。
_root_xamls = [os.path.join(ROOT, "MainWindow.xaml")] + \
              sorted(_glob.glob(os.path.join(ROOT, "Views", "*.xaml")))
_missing_fg = []
for _xf in _root_xamls:
    _t = open(_xf, encoding='utf-8').read()
    _rm = _re.search(r'<(?:Window|UserControl)\b.*?>', _t, _re.S)
    _root = _rm.group(0) if _rm else ""
    if not _re.search(r'TextElement\.Foreground="\{StaticResource\s+\w+\}"', _root):
        _missing_fg.append(os.path.relpath(_xf, ROOT))
check("所有窗口/页面根元素均设置 TextElement.Foreground 浅色兜底", not _missing_fg)
for x in _missing_fg:
    print("     缺少根级 TextElement.Foreground：", x)

# ---------- 15. 10s 采样 / 满载稳定 1 分钟自动 / HWiNFO 每核 VID / 历史成绩 ----------
print("\n[15] 采样与自动触发（10s 采样 · 满载稳定自动触发 · HWiNFO 每核 VID · 历史成绩）")
_coll = read("../采集层/src/Collector/Collector.cs")
_shm = read("../采集层/src/Collector/HwinfoShm.cs")
_stab = read("../采集层/src/Collector/FullLoadStability.cs")
check("采集层: HwinfoShm.cs 存在（共享内存读取）", os.path.exists(os.path.join(ROOT, "../采集层/src/Collector/HwinfoShm.cs")))
check("采集层: FullLoadStability.cs 存在（滑窗去抖）", os.path.exists(os.path.join(ROOT, "../采集层/src/Collector/FullLoadStability.cs")))
check("采样窗口约 10s（SampleDurationMs=10000）", "SampleDurationMs { get; set; } = 10_000" in _coll)
check("采样间隔 500ms（20 轮）", "SampleIntervalMs { get; set; } = 500" in _coll)
check("满载稳定门槛 60s", "FullLoadHoldSeconds { get; set; } = 60" in _coll)
check("HwinfoCollector 已接共享内存（不再是 TODO 空桩）",
      "HwinfoShm.TryRead" in _coll and "TODO: 通过 HWiNFO 共享内存" not in _coll)
check("HwinfoShm: 每核 VID 解析为纯逻辑（可单测）", "ParseCoreVids" in _shm)
check("HwinfoShm: 映射名为当前协议 SM2（真机 v8.x）",
      r"Global\HWiNFO_SENS_SM2" in _shm and "HWiNFO_SENS_SM2" in _shm)
check("HwinfoShm: 不得使用已废弃旧映射名 SF（真机连不上的根因）",
      "HWiNFO_SENS_SF" not in _shm)
check("HwinfoShm: 读取受官方互斥体保护", "HWiNFO_SM2_MUTEX" in _shm)
check("HwinfoShm: 连不上时给注册表/开关精确提示", "SensorsSM" in _shm and "NotFoundHint" in _shm)
check("HwinfoShm: 排除 SoC/Effective/整轨", '"soc"' in _shm and '"effective"' in _shm)
check("FullLoadStability: 滑窗占比判定 IsStable", "IsStable" in _stab and "CurrentRatio" in _stab)

check("RunVM: 使用 FullLoadStability 滑窗", "FullLoadStability" in runvm and "IsStable" in runvm)
check("RunVM: 稳定达标后自动 DoSample", "DoSample();   // 持续满载" in runvm)
check("RunVM: 手动采样也要求稳定满 1 分钟（FullLoadStable）",
      "Step == 1 && FullLoadStable" in runvm)
check("RunVM: 暴露稳定进度/倒计时", "StableSeconds" in runvm and "StableTarget" in runvm)

runx2 = read("Views/RunView.xaml")
check("RunView: 稳定确认进度条绑定 OneWay",
      'Value="{Binding Run.StableSeconds, Mode=OneWay}"' in runx2 and
      'Maximum="{Binding Run.StableTarget, Mode=OneWay}"' in runx2)

check("EvaluationService: 采样窗口轮数（SampleRounds 推导）", "CollectorOptions.SampleRounds" in svc)
check("EvaluationService: 合并 HWiNFO 每核 VID（LHM+HWiNFO）",
      "LHM+HWiNFO" in svc and "hwVidRounds" in svc)

histvm = read("ViewModels/HistoryViewModel.cs")
histx = read("Views/HistoryView.xaml")
histstore = read("Services/HistoryStore.cs")
mainx = read("MainWindow.xaml")
check("历史: HistoryStore 落盘/加载/删除",
      all(k in histstore for k in ["Save(", "LoadAll(", "Delete(", "history"]))
check("历史: HistoryEntry 用 JSON 友好类型（double[][] 非值元组）",
      "double[][]" in read("Models/HistoryEntry.cs"))
check("历史: HistoryVM 列表/查看/删除/打开目录",
      all(k in histvm for k in ["ObservableCollection<HistoryItem>", "OpenSelected", "DeleteSelected", "Refresh()"]))
check("历史: HistoryView 列表绑定", "History.Items" in histx and "History.Selected" in histx)
check("历史: 侧边导航含“历史成绩”", "历史成绩" in mainx and "Tag=\"History\"" in mainx)
check("历史: 主区挂载 HistoryView", "<v:HistoryView" in mainx)
check("MainWindow.code: History 路由与标题", "NavPage.History" in read("MainWindow.xaml.cs"))

appinfo = read("AppInfo.cs")
check("版本: AppInfo.Version=1.0.0", 'Version = "1.0.0"' in appinfo)
check("版本: 报告抬头取真实版本（Exporter.Build 传参）",
      "appVersion" in read("../评分引擎/src/ScoringEngine/Exporter.cs") and "AppInfo.Version" in resvm)
check("版本: 侧栏显示 V1.0.0", "V1.0.0" in mainx)

# ---------- 16. 去除厂商对标表述 + 评测完成可重新评测 ----------
print("\n[16] 去厂商对标 · 评测完成可重新评测")
runx182 = read("Views/RunView.xaml")
_bad_asus = []
for _pat in (os.path.join(ROOT, "**", "*.xaml"), os.path.join(ROOT, "**", "*.cs"),
             os.path.join(ROOT, "..", "评分引擎", "src", "**", "*.cs"),
             os.path.join(ROOT, "..", "采集层", "src", "**", "*.cs")):
    for _f in _glob.glob(_pat, recursive=True):
        try:
            _t = open(_f, encoding="utf-8").read()
        except Exception:
            continue
        if ("华硕" in _t) or ("asus" in _t.lower()):
            _bad_asus.append(os.path.relpath(_f, ROOT))
check("UI/引擎/采集层源码不含“华硕/ASUS”对标表述", not _bad_asus)
if _bad_asus:
    print("    残留文件：", _bad_asus)
check("主副标题不含“对标华硕”", "对标华硕" not in mainx)
check("RunVM: 重新评测命令 RestartCmd + 完整复位 ResetWizard",
      "RestartCmd" in runvm and "ResetWizard" in runvm)
check("RunVM: RestartCmd 仅完成态(Step==3)可执行", "() => !Busy && Step == 3" in runvm)
check("RunVM: 复位清理稳定判定/进度/集合",
      all(k in runvm for k in ["_stability = null", "LivePoints.Clear()", "EnvItems.Clear()"]))
check("RunVM: 完成态查看结果命令 ViewResultCmd",
      "ViewResultCmd" in runvm and "_main.Go(NavPage.Result)" in runvm)
check("RunView: 完成页有“重新评测”按钮并绑 RestartCmd",
      "重新评测" in runx182 and "Run.RestartCmd" in runx182)
check("RunView: 完成页有“查看评分详情”并绑 ViewResultCmd",
      "查看评分详情" in runx182 and "Run.ViewResultCmd" in runx182)

print(f"\n{'=' * 64}")
print(f"结果：{P} passed, {F} failed")
print(f"{'=' * 64}")
sys.exit(0 if F == 0 else 1)
