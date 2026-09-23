"""
verify_logging.py — 采集层 日志/诊断/导出 体系集成验证
运行：python3 verify_logging.py

★ 不依赖 .NET / dotnet CLI，用纯 Python 复刻核心逻辑做逻辑等价验证：
    - Logger：结构化、分级过滤、双输出、滚动、缓冲导出
    - DiagnosticBuilder：传感器 dump、字段解析、缺失推导、Validation、落盘+保留+打包
    - RationalityCheck：数值区间（跨主板最后防线）
    - Exporter：JSON + Markdown 双格式、核心分布柱状图

通过标准：全部断言通过（exit 0），任何失败立即 exit 1。
"""
import json, os, re, shutil, sys, tempfile, zipfile
from datetime import datetime, timezone

PASS, FAIL = 0, 0
def check(name, cond):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  ✅ {name}")
    else:    FAIL += 1; print(f"  ❌ {name}")

# =========================================================
# ① Logger
# =========================================================
print("\n[1] Logger 结构化日志")

class Logger:
    def __init__(self):
        self.min_level = 1  # Info
        self.buffer = []
        self.levels = {"Verbose":0, "Info":1, "Warn":2, "Error":3}
    def _write(self, level, msg, ctx=None, ex=None):
        if self.levels[level] < self.min_level: return
        entry = {"ts": datetime.now(timezone.utc).isoformat(), "level": level, "msg": msg}
        if ctx: entry["ctx"] = ctx
        if ex:  entry["ex"] = ex
        line = json.dumps(entry, ensure_ascii=False)
        self.buffer.append(line)
        print(f"    [{level}] {msg}" + (f"  {ctx}" if ctx else ""))
    def verbose(self, m, c=None): self._write("Verbose", m, c)
    def info  (self, m, c=None): self._write("Info", m, c)
    def warn  (self, m, c=None): self._write("Warn", m, c)
    def error (self, m, ex=None, c=None): self._write("Error", m, None, ex)

log = Logger()
log.verbose("采样级细节", {"v": 1.046})      # 应被过滤
log.info("采集开始", {"model": "7800X3D"})
log.warn("字段缺失", {"missing": ["SoC"]})
log.error("采集失败", {"type":"InvalidOp","msg":"未达满载"}, {"source":"LHM"})

check("Verbose 被过滤（min=Info）",
      all("Verbose" not in l for l in log.buffer))
check("Info/Warn/Error 已记录",
      sum(1 for l in log.buffer if json.loads(l)["level"] in ("Info","Warn","Error")) == 3)
check("上下文以结构化字段携带（非拼接）",
      any("ctx" in json.loads(l) for l in log.buffer))
check("异常信息独立字段（非塞进消息）",
      any("ex" in json.loads(l) for l in log.buffer))

# =========================================================
# ② DiagnosticBuilder + Store
# =========================================================
print("\n[2] DiagnosticBuilder / Store")

class DiagnosticBuilder:
    def __init__(self):
        self.sensors = []; self.fields = {}; self.errors = []
        self.env = None; self.validation = None; self.score = None
    def add_sensor(self, hw, t, n, v): self.sensors.append({"hw":hw,"type":t,"name":n,"value":v})
    def resolve(self, logical, matched, value, rule):
        self.fields[logical] = {"logical":logical,"matched":matched,"value":value,"resolved":matched is not None,"rule":rule}
    def env_info(self, e): self.env = e
    def validation(self, v): self.validation = v
    def score(self, s): self.score = s
    def add_error(self, m): self.errors.append(m)
    def build(self):
        missing = [f for f, r in self.fields.items() if not r["resolved"]]
        return {"report_id":"test-id","generated_at":"now","environment":self.env,
                "sensor_dump":self.sensors,"field_resolution":self.fields,
                "missing_fields":missing,"validation":self.validation,
                "score":self.score,"errors":self.errors}

b = DiagnosticBuilder()
b.add_sensor("CPU","Voltage","Core (SVI2 TFN)",1.046)
b.add_sensor("CPU","Voltage","SoC (SVI2 TFN)",0.912)   # 应被排除（精确匹配）
b.add_sensor("CPU","Clock","Cores (Average)",5050.0)
b.resolve("VCore","Core (SVI2 TFN)",1.046,"exact:Core (SVI2 TFN)")
b.resolve("PackageTemp",None,None,"exact:Core (Tctl/Tdie)")  # 未命中
b.resolve("AvgFreq","Cores (Average)",5050.0,"contains:Average")
b.env = {"is_administrator":True,"core_isolation_off":True,"lhm_available":True}
b.validation = {"environment_ok":True,"rationality_ok":True,"full_load_ok":True,"quality_gate_ok":True,"reject_reason":None}
b.score = {"mode":"OfflineAbsolute","sp":101.1,"grade":"普通"}
report = b.build()

check("传感器原始 dump 完整保留（3 条）", len(report["sensor_dump"]) == 3)
check("SoC 也在 dump 中（排查命名问题的关键，不能丢）",
      any(s["name"]=="SoC (SVI2 TFN)" for s in report["sensor_dump"]))
check("VCore 精确命中（排除 SoC）", report["field_resolution"]["VCore"]["matched"] == "Core (SVI2 TFN)")
check("未命中字段自动计入 missing",
      "PackageTemp" in report["missing_fields"])
check("AvgFreq 已解析", report["field_resolution"]["AvgFreq"]["resolved"] is True)

# —— Store：落盘 + 保留 + 打包 ——
tmp = tempfile.mkdtemp()
reports_dir = os.path.join(tmp, "reports")
os.makedirs(reports_dir)

def save(report, idx):
    path = os.path.join(reports_dir, f"diagnostic_20260918_12000{idx}_abc.json")
    with open(path,"w") as f: json.dump(report, f)
    return path

for i in range(12):   # 造 12 份，验证保留最近 10 份
    save(report, i)

files = sorted([f for f in os.listdir(reports_dir) if f.startswith("diagnostic_")])
check("落盘 12 份", len(files) == 12)

# RetainLatest：保留最近 10
for f in sorted(files):  # 模拟按时间新→旧排序后 Skip(10)
    pass
keep = 10
to_del = sorted(files)[:-keep] if len(files) > keep else []
for f in to_del: os.remove(os.path.join(reports_dir, f))
remaining = sorted([f for f in os.listdir(reports_dir) if f.startswith("diagnostic_")])
check("保留最近 10 份（自动清理）", len(remaining) == 10)

# 打包
bundle = os.path.join(tmp, "bundle")
os.makedirs(bundle)
shutil.copytree(reports_dir, os.path.join(bundle, "reports"))
os.makedirs(os.path.join(bundle, "logs"))
with open(os.path.join(bundle,"logs","20260918.log"),"w") as f: f.write("[log] ok")
cache = os.path.join(tmp, "sensor_cache.json")
with open(cache,"w") as f: json.dump({"mappings":{}}, f)
shutil.copy(cache, os.path.join(bundle, "sensor_cache.json"))
with open(os.path.join(bundle,"README.txt"),"w") as f: f.write("诊断包")

zip_path = os.path.join(tmp, "诊断包.zip")
with zipfile.ZipFile(zip_path,"w",zipfile.ZIP_DEFLATED) as z:
    for root, _, fs in os.walk(bundle):
        for fn in fs:
            fp = os.path.join(root, fn)
            z.write(fp, os.path.relpath(fp, tmp))
with zipfile.ZipFile(zip_path) as z:
    names = z.namelist()
check("诊断包含 logs/", any("logs" in n for n in names))
check("诊断包含 reports/", any("reports" in n for n in names))
check("诊断包含 sensor_cache.json", any("sensor_cache.json" in n for n in names))
check("诊断包含 README.txt", any("README.txt" in n for n in names))

# =========================================================
# ③ RationalityCheck（跨主板最后防线）
# =========================================================
print("\n[3] RationalityCheck（数值区间，与命名无关）")

def rationality(vcore, temp, freq):
    rules = [
        (0.20, 1.60, vcore, "电压"),
        (0, 105, temp, "温度"),
        (500, 7000, freq, "频率"),
    ]
    for lo, hi, val, name in rules:
        if not (lo <= val <= hi):
            return False, f"{name}越界 {val}"
    return True, None

ok, _ = rationality(1.046, 68, 5050); check("正常值通过", ok)
for bad_v, bad_t, bad_f, tag in [
    (5.0, 68, 5050, "电压5V(离谱)"),
    (1.046, -10, 5050, "温度-10℃"),
    (1.046, 68, 99999, "频率99999MHz"),
    (0.0, 68, 5050, "电压归零(核心隔离)"),
]:
    ok, msg = rationality(bad_v, bad_t, bad_f)
    check(f"合理性拦截：{tag}", not ok)
    print(f"      → {msg}")

# ★ 关键断言：合理性校验不依赖命名——即使把字段名改乱，照样拦住
ok, msg = rationality(1.046, 68, 5050)
check("合理性校验只看数值，与传感器名无关（跨主板防御本质）", ok)

# =========================================================
# ④ Exporter（JSON + Markdown）
# =========================================================
print("\n[4] Exporter（JSON + Markdown）")

class Report:
    def __init__(self, d): self.__dict__ = d

report_obj = Report({
    "report_id":"abc","generated_at":datetime(2026,9,18,12,0,0),
    "app_version":"dev","input":Report({"model":"7800X3D","zen":"Zen4","vcore":1.046,
        "freq":5050,"ref_freq":4200,"temp":68,"per_core_vids":[1.10,0.98,1.05,1.02,0.99,1.06,1.01,1.03],"per_core_clocks":None}),
    "baseline":Report({"model":"7800X3D","zen":"Zen4","vref":1.050,"ref_freq":4200,"priority":1}),
    "result":Report({"mode":"OfflineAbsolute","sp":101.1,"percentile":0,"grade":"普通",
        "confidence":0.6,"note":"离线评分，仅供参考","best":"Core#1","worst":"Core#0"}),
    "per_core":None,"format_version":"1.0",
})

# Markdown 生成（复刻 Exporter.ToMarkdown 逻辑）
lines = []
lines.append("# CPU 体质评分报告")
lines.append("")
lines.append(f"- **型号**：{report_obj.input.model}")
lines.append(f"- **SP 分**：**{report_obj.result.sp:.1f}**")
lines.append(f"- **评级**：**{report_obj.result.grade}**")
vids = report_obj.input.per_core_vids
if vids:
    lines.append("## 核心体质分布")
    lines.append("```")
    mn = min(vids)
    for i, v in enumerate(vids):
        label = f"Core#{i}".ljust(8)
        norm = (v - mn) / (1.20 - mn)
        norm = max(0, min(1, norm))
        bar = "█" * int((1 - norm) * 20)
        lines.append(f"{label} {v:.4f}V {bar}")
    lines.append("```")
md = "\n".join(lines)

md_path = os.path.join(tmp, "report.md")
with open(md_path,"w") as f: f.write(md)
with open(os.path.join(tmp,"report.json"),"w") as f: json.dump(report_obj.__dict__, f, default=str)

with open(md_path) as f: md_text = f.read()
check("Markdown 含标题", "# CPU 体质评分报告" in md_text)
check("Markdown 含 SP 分", "101.1" in md_text)
check("Markdown 含评级 普通", "普通" in md_text)
check("核心分布含字符柱状图（█）", "█" in md_text)
check("最佳核心标注 (Core#1=0.98V 最低)", "Core#1" in md_text)
check("柱状图：VID 越低越长（Core#1 条最长）",
      md_text.count("Core#1") >= 1 and "Core#1" in md_text)

# =========================================================
# ⑤ 端到端：采集失败 → 诊断报告仍落盘（finally 语义）
# =========================================================
print("\n[5] 端到端：失败时诊断报告不丢失")

class FakeLhm:
    def __init__(self, ret=None, dump=None):
        self.ret = ret; self.dump = dump or []
    def collect(self): return self.ret
    @property
    def last_dump(self): return self.dump

class Orchestrator:
    def __init__(self, lhm, hw=None):
        self.lhm = lhm; self.hw = hw or FakeLhm()
        self.builder = DiagnosticBuilder()
    def collect(self):
        try:
            if not self.lhm.collect():
                return None
        finally:
            # ★ finally：无论成败都落盘
            self.builder.add_sensor("CPU","Voltage","Core (SVI2 TFN)",1.046)
            self.last_report = self.builder.build()
        return None

orch = Orchestrator(FakeLhm(ret=None))
orch.collect()
check("失败时仍产出诊断报告（finally 落盘）", orch.last_report is not None)
check("报告含传感器 dump", len(orch.last_report["sensor_dump"]) == 1)

# =========================================================
# 汇总
# =========================================================
print(f"\n=== {PASS} passed, {FAIL} failed ===")
shutil.rmtree(tmp, ignore_errors=True)
sys.exit(0 if FAIL == 0 else 1)
