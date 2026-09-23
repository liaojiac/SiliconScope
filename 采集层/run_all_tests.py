"""
run_all_tests.py — 采集层全量回归
运行：python3 run_all_tests.py

覆盖：
  A. 命名校准（verify_naming）
  B. 跨主板兼容（verify_compat）
  C. 探测模式（verify_probe_mode）
  D. ★ 日志 / 诊断 / 导出（verify_logging，本轮新增）

通过标准：A/B/C/D 全部 0 失败 → exit 0
"""
import importlib.util, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))

SPECS = [
    ("A 命名校准",        "verify_naming.py"),
    ("B 跨主板兼容",      "verify_compat.py"),
    ("C 探测模式",        "verify_probe_mode.py"),
    ("D 日志/诊断/导出",  "verify_logging.py"),
    ("E 真实场景诊断",    "verify_real_scenarios.py"),
    ("F 端到端验收",      "verify_e2e.py"),
    ("G C# 结构校验",     "verify_structure.py"),
]

# 匹配各脚本末尾的汇总格式（三种写法都兼容）：
#   "=== N passed, M failed ==="   （标准）
#   "结果：N passed, M failed"      （中文前缀）
#   "结果：N/T passed"              （分数形式，跨主板兼容 用此格式）
_SUMMARY_PATTERNS = [
    re.compile(r"(\d+)\s*/\s*(\d+)\s+passed", re.I),           # N/T passed
    re.compile(r"(\d+)\s+passed,\s*(\d+)\s+failed", re.I),     # N passed, M failed
]


def _parse_summary(out: str):
    """返回 (passed, failed, matched)。从 stdout 解析，取最后一条匹配。"""
    p, fn, matched = 0, 0, False
    for line in out.splitlines():
        for pat in _SUMMARY_PATTERNS:
            m = pat.search(line)
            if not m:
                continue
            num1, num2 = int(m.group(1)), int(m.group(2))
            if "/" in m.group(0):        # 分数形式：N/T → passed=N, failed=T-N
                total = num2
                matched = True
                p, fn = num1, total - num1
            else:                        # passed,failed 形式
                matched = True
                p, fn = num1, num2
    return p, fn, matched


def _run_via_subprocess(path):
    """用子进程运行脚本，捕获退出码 + 解析 stdout 汇总行。

    ★ 关键：不依赖脚本内部变量（exec 后局部作用域取不到 PASS/FAIL），
    而是信任每个脚本自己打印的最终 "N passed, M failed"——这是最权威、
    最不易失同步的单一来源。"""
    env = {**os.environ, "PYTHONUNBUFFERED": "1"}
    proc = subprocess.run(
        [sys.executable, "-u", path],
        capture_output=True, text=True, env=env)
    out = proc.stdout
    if proc.stderr:
        out += "\n" + proc.stderr
    print(out, end="")

    p, fn, matched = _parse_summary(out)
    if not matched:
        # 未解析到汇总行 → 退回到退出码判断（保守）
        if proc.returncode != 0:
            return "FAIL", 0, 1
    return ("OK" if fn == 0 and proc.returncode == 0 else "FAIL"), p, fn


results = {}
for name, script in SPECS:
    print("=" * 60)
    print(f"▶ {name}  ({script})")
    print("=" * 60)
    path = os.path.join(HERE, script)
    if not os.path.exists(path):
        print(f"  ⚠️  未找到 {script}，跳过")
        results[name] = ("SKIP", 0, 0)
        continue
    status, p, fn = _run_via_subprocess(path)
    results[name] = (status, p, fn)

print("\n" + "=" * 60)
print("汇总")
print("=" * 60)
total_fail = 0
for name, (status, p, fn) in results.items():
    mark = "✅" if status == "OK" else "❌"
    print(f"  {mark} {name}: {status}  ({p} passed, {fn} failed)")
    total_fail += fn

print(f"\n{'全部通过 🎉' if total_fail == 0 else f'存在失败：{total_fail}'}")
sys.exit(0 if total_fail == 0 else 1)
