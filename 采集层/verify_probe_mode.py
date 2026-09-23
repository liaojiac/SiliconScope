"""verify_probe_mode.py — 探测模式决策验证（逻辑等价）"""
import sys
P, F = 0, 0
def check(n, c):
    global P, F
    if c: P += 1; print(f"  ✅ {n}")
    else: F += 1; print(f"  ❌ {n}")

class ProbeState:
    def __init__(self, has_probed=False, fail=0, missing=None):
        self.HasEverProbed = has_probed; self.FailureCount = fail
        self.MissingFields = missing or []

def should_probe(mode, state, force, current_missing):
    if force: return True
    if mode == "AlwaysAuto": return True
    if mode == "OffUntilError":
        # ★ 已探测过 且 当前仍有缺失字段 → 才触发探测
        if not state.HasEverProbed: return False
        missing = current_missing if current_missing is not None else state.MissingFields
        return len(missing) > 0
    # AutoOnFirstRun（默认）
    if not state.HasEverProbed: return True
    if current_missing and len(current_missing) > 0: return True
    return False
    # AutoOnFirstRun（默认）
    if not state.HasEverProbed: return True
    if current_missing and len(current_missing) > 0: return True
    return False

print("[探测模式] ShouldProbe 判定")
s0 = ProbeState()
check("AutoOnFirstRun 首次→探测", should_probe("AutoOnFirstRun", s0, False, None) is True)
s1 = ProbeState(has_probed=True)
check("AutoOnFirstRun 后续无缺失→不探测", should_probe("AutoOnFirstRun", s1, False, []) is False)
check("AutoOnFirstRun 有缺失→重探", should_probe("AutoOnFirstRun", s1, False, ["VCore"]) is True)
check("AlwaysAuto 总是探测", should_probe("AlwaysAuto", s0, False, None) is True)
check("OffUntilError 默认不探测", should_probe("OffUntilError", s0, False, None) is False)
check("OffUntilError 出错触发", should_probe("OffUntilError", s1, False, ["VCore"]) is True)
check("force 强制探测", should_probe("OffUntilError", s0, True, None) is True)

print("\n[探测结果] AutoMatch + 精确匹配排除 SoC")
def auto_match(rule, entries):
    exact = [e for e in entries if e[2] == rule]
    if exact: return exact[0][2], exact[0][3]
    contains = [e for e in entries if rule.replace("(SVI2 TFN)","").strip() in e[2]]
    if contains: return contains[0][2], contains[0][3]
    return None, None

sensors = [
    ("CPU","Voltage","Core (SVI2 TFN)",1.046),
    ("CPU","Voltage","SoC (SVI2 TFN)",0.912),
    ("CPU","Clock","Cores (Average)",5050.0),
]
name, val = auto_match("Core (SVI2 TFN)", sensors)
check("VCore 精确匹配 Core (SVI2 TFN)", name == "Core (SVI2 TFN)" and abs(val - 1.046) < 1e-9)
soc_matches = [e for e in sensors if "SoC" in e[2]]
check("SoC 不被当作 VCore（精确匹配排除）", name != "SoC (SVI2 TFN)")
check("SoC 确实存在于原始 dump（供排查）", len(soc_matches) == 1)

print(f"\n=== {P} passed, {F} failed ===")
sys.exit(0 if F == 0 else 1)
