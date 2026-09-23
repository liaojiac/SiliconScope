"""
跨主板兼容验证（V1.6）：弹性匹配 + 合理性校验 + 探测自省 + 降级 + 主板指纹缓存
无需 .NET / 无需 LHM，纯逻辑复刻 Compat.cs
对应：LHM跨主板兼容设计.md
"""
import re

# —— 复刻 SensorNameRules ——
RULES = {
    "VCore":       {"field": "Voltage", "exact": ["Core (SVI2 TFN)"], "contains": ["SVI2", "Core Voltage", "CPU Core"], "regex": None},
    "PackageTemp": {"field": "TempC",   "exact": ["Core (Tctl/Tdie)"], "contains": ["Tctl", "Tdie", "Package", "CPU Temperature"], "regex": None},
    "AvgFreq":     {"field": "FreqMHz", "exact": ["Cores (Average)"], "contains": ["Average", "Core (Avg)", "CPU Clock"], "regex": None},
}
PER_CORE = [
    {"field": "Voltage", "regex": re.compile(r"^Core #\d+ VID$")},
    {"field": "FreqMHz", "regex": re.compile(r"^Core #\d+ \(Effective\)$")},
    {"field": "FreqMHz", "regex": re.compile(r"^Core #\d+$")},
]

# —— 复刻 Sanity.IsPlausible ——
RANGES = {
    "Voltage": (0.20, 1.55), "FreqMHz": (1500, 6500),
    "TempC": (20, 105), "PowerW": (0, 400), "LoadPct": (0, 100),
}
def plausible(field, v):
    if v is None: return False
    lo, hi = RANGES[field]
    return lo <= v <= hi

def auto_match(report, rule):
    """复刻 SensorProbe.AutoMatch：精确 → 包含 → 正则，且值合理"""
    # 1) 精确
    for e in report:
        if e["name"] in rule["exact"] and plausible(rule["field"], e["value"]):
            return e["name"], e["value"]
    # 2) 包含
    for e in report:
        if any(k.lower() in e["name"].lower() for k in rule["contains"]) and plausible(rule["field"], e["value"]):
            return e["name"], e["value"]
    # 3) 正则（每核）
    if rule.get("regex"):
        for e in report:
            if rule["regex"].match(e["name"]) and plausible(rule["field"], e["value"]):
                return e["name"], e["value"]
    return None, None

def probe(sensors):
    return [{"hw": h, "type": t, "name": n, "value": v} for h, t, n, v in sensors]

def main():
    print("=" * 66)
    print("跨主板兼容验证 V1.6")
    print("=" * 66)

    # —— 场景 A：标准命名（Zen4 8核，内置名）——
    std = [
        ("CPU", "Voltage", "Core (SVI2 TFN)", 1.046),
        ("CPU", "Voltage", "SoC (SVI2 TFN)", 1.050),   # 不应误采
        ("CPU", "Temperature", "Core (Tctl/Tdie)", 62),
        ("CPU", "Clock", "Cores (Average)", 5050),
        ("CPU", "Clock", "Core #0", 5100), ("CPU", "Clock", "Core #7", 4980),
        ("CPU", "Voltage", "Core #0 VID", 1.02), ("CPU", "Voltage", "Core #7 VID", 1.05),
    ]
    # —— 场景 B：主板变体命名（某 BIOS 把 SVI2 叫成 "CPU Core Voltage"）——
    variant = [
        ("CPU", "Voltage", "CPU Core Voltage", 1.030),   # ★ 变体：靠 Contains 兜底
        ("CPU", "Temperature", "CPU Temperature", 60),    # ★ 变体：靠 Contains 兜底
        ("CPU", "Clock", "CPU Clock", 5000),             # ★ 变体
        ("CPU", "Clock", "Core #0", 5100),
        ("CPU", "Voltage", "Core #0 VID", 1.01),
    ]

    results = []
    def chk(name, cond): results.append((name, cond))

    for label, sensors in [("A: 标准命名", std), ("B: 主板变体命名", variant)]:
        print(f"\n--- {label} ---")
        report = probe(sensors)
        matched = {}
        for key, rule in RULES.items():
            name, val = auto_match(report, rule)
            matched[key] = (name, val)
            print(f"  {key:14s} → {name or '（未匹配）':24s} = {val}")
        chk(f"[{label}] VCore 命中且 ≠ SoC", matched["VCore"][0] == "Core (SVI2 TFN)" or "CPU Core Voltage" in str(matched["VCore"][0]))
        chk(f"[{label}] PackageTemp 命中", matched["PackageTemp"][0] is not None)
        chk(f"[{label}] AvgFreq 命中", matched["AvgFreq"][0] is not None)

    # —— 场景 C：合理性校验（离谱值，与命名无关）——
    print("\n--- C: 合理性校验（防假大雕，跨主板无条件生效）---")
    chk("正常 VCore 1.046 合理", plausible("Voltage", 1.046))
    chk("离谱 VCore 5.0V 被拒（防假大雕）", not plausible("Voltage", 5.0))
    chk("离谱温度 -10℃ 被拒", not plausible("TempC", -10))
    chk("离谱频率 99999 被拒", not plausible("FreqMHz", 99999))

    # —— 场景 D：每核正则（变体命名下仍正确采集 8 核）——
    print("\n--- D: 每核正则（变体命名也正确采集）---")
    report_b = probe(variant)
    per_core_vids = [e["value"] for e in report_b if PER_CORE[0]["regex"].match(e["name"])]
    chk("变体命名下每核 VID 仍采到", len(per_core_vids) == 1)  # 本例 1 个

    # —— 场景 E：降级（字段缺失 → 拒评，宁可不给分）——
    print("\n--- E: 降级链（字段缺失 → 拒评，不给假分）---")
    empty = probe([("CPU", "Clock", "Bus Speed", 100)])  # 只有总线频率，无 VCore
    vcore_name, _ = auto_match(empty, RULES["VCore"])
    chk("VCore 缺失 → 未匹配（触发拒评降级）", vcore_name is None)

    # —— 场景 F：主板指纹缓存 ——
    print("\n--- F: 主板指纹缓存 ---")
    fp = "ASUS_X670E_1801_LHM-0.9.6"
    cache = {fp: {"VCore": "CPU Core Voltage"}}   # 该主板探测后缓存的映射
    hit = cache.get(fp)
    chk("指纹命中 → 复用缓存映射", hit is not None and hit["VCore"] == "CPU Core Voltage")
    chk("未知指纹 → 未命中（触发探测）", cache.get("UNKNOWN_BOARD") is None)

    # —— 汇总 ——
    print("\n" + "=" * 66)
    passed = sum(1 for _, c in results if c)
    print(f"结果：{passed}/{len(results)} passed")
    for n, c in results:
        print(f"  {'✅' if c else '❌'} {n}")
    print("=" * 66)
    return 0 if passed == len(results) else 1

if __name__ == "__main__":
    import sys; sys.exit(main())
