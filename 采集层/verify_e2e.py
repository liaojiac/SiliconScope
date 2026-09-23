"""
端到端验收：采集层 → 评分引擎 完整链路
（Python 等价验证，逻辑/接口/数据结构与 C# 完全一致，无需 .NET 即可验证逻辑）

对应 C# 流程：
    orchestrator.Collect() → HardwareFeatures
        → ScoreEngine.Score(ScoreInput) → ScoreResult
"""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src", "Collector")

# ---------- 1. 量表（对齐 Collector.cs 的 Baseline / JsonBaselineStore） ----------
with open(os.path.join(SRC, "baseline.json"), encoding="utf-8") as f:
    _baselines = json.load(f)
_exact = {b["Model"]: b for b in _baselines}


def get_by_keyword(model: str):
    """模糊匹配：'AMD Ryzen 7 7800X3D' → 命中 '7800X3D'"""
    for b in _baselines:
        if b["Model"] in model or model in b["Model"]:
            return b
    return None


# ---------- 2. 采集层（对齐 LhmCollector / CollectorOrchestrator） ----------
VCore_MIN, VCore_MAX = 0.2, 1.5
TEMP_MIN, TEMP_MAX = 0, 100
# 0.95 → 0.90。7800X3D MaxBoost=5000，0.95 门槛 4750MHz 会把
# OCCT 重负载下的 4526MHz 稳态误判"未达满载"；0.90 门槛 4500MHz 可容纳
# Prime95 AVX-512 / 撞功耗墙（低至 4.3GHz 量级）的重负载。
FULL_LOAD = 0.90


def zen_map(model: str) -> str:
    m = model.upper()
    if any(k in m for k in ["5800X3D", "5900X", "5950X", "5600X"]):
        return "Zen3"
    if any(k in m for k in ["7800X3D", "7950X", "7900X", "7700X"]):
        return "Zen4"
    if any(k in m for k in ["9950X", "9900X", "9700X", "9600X"]):
        return "Zen5"
    # 未知型号返回 Unknown，与评分引擎 ZenProfile 全局默认(0.40)对齐
    return "Unknown"


def lhm_collect(model="AMD Ryzen 7 7800X3D", vcore=1.046, freq=5050, temp=62):
    """对齐 LhmCollector.Collect()：返回 HardwareFeatures"""
    baseline = _exact.get(model.split()[-1]) or get_by_keyword(model)
    ref_freq = baseline["ReferenceFreqMHz"] if baseline else 4000
    # 满载判定基准必须是【全核 MaxBoost】，不是基础频率
    max_boost = baseline.get("MaxBoostMHz") or 0
    full_load_freq = max_boost if max_boost > 0 else ref_freq
    return {
        "Model": model, "ZenGeneration": zen_map(model),
        "VCore": vcore, "FrequencyMHz": freq, "ReferenceFreqMHz": ref_freq,
        "FullLoadFreqMHz": full_load_freq,
        "TemperatureC": temp, "PerCoreVIDs": None, "PerCoreClocks": None,
        "Source": "LhmOnly", "EnvironmentOk": True,
    }


def orchestrator_collect(features: dict) -> dict:
    """对齐 CollectorOrchestrator.Collect()：满载判定 + 质控门禁"""
    # 满载判定（防假大雕第二道闸门）
    fl_base = features.get("FullLoadFreqMHz") or features["ReferenceFreqMHz"]
    if features["FrequencyMHz"] < fl_base * FULL_LOAD:
        raise RuntimeError(
            f"未达满载：{features['FrequencyMHz']} < MaxBoost {fl_base}*{FULL_LOAD}")
    # 质控
    if not (VCore_MIN <= features["VCore"] <= VCore_MAX):
        raise RuntimeError(f"电压越界：{features['VCore']}")
    if not (TEMP_MIN <= features["TemperatureMHz"] if False else features["TemperatureC"] <= TEMP_MAX):
        raise RuntimeError("温度越界")
    return features


# ---------- 3. 评分引擎（对齐 ScoringEngine / SpScaler / BaselineResolver / ZenProfile） ----------
ANCHOR_TO_BOOST_RATIO = 0.94
_DEFAULT_SLOPE = {"Zen": 0.30, "Zen+": 0.30, "Zen2": 0.30,
                  "Zen3": 0.35, "Zen4": 0.41, "Zen5": 0.45}


def default_slope(zen: str) -> float:
    """代际默认 V/F 斜率；未知代际走全局中位 0.40（对齐 ZenProfile.DefaultSlope）"""
    return _DEFAULT_SLOPE.get(zen or "", 0.40)


def resolve(baseline: dict):
    """三级参数解析（对齐 BaselineResolver.Resolve）：返回 (v_ref, anchor, slope, level)"""
    slope = baseline.get("SlopeMvPerMhz") or 0.0
    if slope <= 0:
        slope = default_slope(baseline.get("ZenGeneration", ""))
    anchor = baseline.get("AnchorFreqMHz") or 0.0
    if anchor <= 0:
        mb = baseline.get("MaxBoostMHz") or 0.0
        if mb > 0:
            anchor = round(mb * ANCHOR_TO_BOOST_RATIO / 25.0) * 25.0
    return baseline["VRef"], anchor, slope, baseline.get("CalibrationLevel", "Estimated")


def anchor_voltage(v_meas, f_meas, anchor_freq, slope_mv_per_mhz):
    """沿 V/F 直线线性折算到锚定频率（非等比乘法）"""
    if slope_mv_per_mhz <= 0 or anchor_freq <= 0:
        return v_meas
    return v_meas + (slope_mv_per_mhz / 1000.0) * (anchor_freq - f_meas)


def calc_sp(v_meas: float, v_ref: float, scale: float = 300.0) -> float:
    """不折算的绝对 SP（旧口径，保留兼容）"""
    return max(0.0, min(150.0, 100 + (v_ref / v_meas - 1) * scale))


def calc_sp_anchored(v_meas, f_meas, v_ref, anchor_freq, slope, scale=300.0):
    """★ 折算后的绝对 SP（主口径）"""
    return calc_sp(anchor_voltage(v_meas, f_meas, anchor_freq, slope), v_ref, scale)


def grade_by_sp(sp: float) -> str:
    # 五档：大雕≥112 / 小雕≥104 / 普通≥96 / 小雷≥88 / 大雷<88
    if sp >= 112: return "大雕"
    if sp >= 104: return "小雕"
    if sp >= 96:  return "普通"
    if sp >= 88:  return "小雷"
    return "大雷"


def grade_by_pct(pct: float) -> str:
    # 百分位五档：90 / 70 / 30 / 10
    if pct >= 90: return "大雕"
    if pct >= 70: return "小雕"
    if pct >= 30: return "普通"
    if pct >= 10: return "小雷"
    return "大雷"


def score(features: dict, cloud_available: bool = False) -> dict:
    baseline = get_by_keyword(features["Model"]) or _exact.get(features["Model"].split()[-1])
    if not baseline:
        raise RuntimeError(f"未收录型号：{features['Model']}")

    # 主口径：把实测点沿 V/F 直线折算到锚定频率后再与 VRef 比较
    v_ref, anchor, slope, level = resolve(baseline)
    v_anchor = anchor_voltage(features["VCore"], features["FrequencyMHz"], anchor, slope)

    if cloud_available:
        # 在线模式：相对分（此处用模拟分位表，真实由云端 ICloudStats 提供）
        mu, sigma = 100.0, 6.0
        sp = calc_sp(v_anchor, v_ref)
        z = (sp - mu) / sigma
        pct = max(0.0, min(100.0, 50 + z * 16))
        mode, note, conf = "OnlineRelative", "", 0.9
        grade = grade_by_pct(pct)
    elif v_ref > 0:
        sp = calc_sp(v_anchor, v_ref)
        pct = 0.0
        mode = "OfflineAbsolute"
        # 置信度/说明随标定等级变化（对齐 ScoreEngine.Score）
        if level == "Calibrated":
            note, conf = "离线评分，仅供参考（样本库未积累）", 0.6
        else:
            note, conf = ("本型号参考参数为估计值，SP 绝对分不可直接对标华硕，"
                          "仅可用于同型号横向比较"), 0.4
        grade = grade_by_sp(sp)
    else:
        return {"Mode": "CurveOnly", "Grade": "—", "Note": "缺少参考电压，仅显示原始曲线"}

    return {
        "Mode": mode, "SpScore": round(sp, 1), "Percentile": round(pct, 1),
        "Grade": grade, "Confidence": conf, "Note": note,
    }


# ---------- 4. 运行验收 ----------
def main():
    print("=" * 60)
    print("端到端验收：采集层 → 评分引擎")
    print("=" * 60)

    pass_ = 0
    fail = 0

    def check(name, cond):
        nonlocal pass_, fail
        if cond:
            pass_ += 1
            print(f"  ✅ {name}")
        else:
            fail += 1
            print(f"  ❌ {name}")

    # ---- Case 1：7800X3D 满载正常 → 应出分（离线模式）----
    print("\n[Case 1] 7800X3D 满载采集 → 离线评分")
    try:
        f = orchestrator_collect(lhm_collect(vcore=1.046, freq=5050))
        r = score(f, cloud_available=False)
        print(f"    SP={r['SpScore']}  Grade={r['Grade']}  Mode={r['Mode']}")
        check("满载判定通过（5050 ≥ MaxBoost 5000×0.90）",
              f["FrequencyMHz"] >= f["FullLoadFreqMHz"] * FULL_LOAD)
        check("离线模式出分", r["Mode"] == "OfflineAbsolute" and r["SpScore"] > 0)
        check("标注仅供参考", "仅供参考" in r["Note"])
    except Exception as e:
        check("Case1 未抛异常", False)
        print(f"    ERR: {e}")

    # ---- Case 2：未达满载 → 应被拦截（防假大雕）----
    print("\n[Case 2] 待机低频率 → 满载判定拦截")
    try:
        orchestrator_collect(lhm_collect(vcore=0.9, freq=2000))  # 待机 2GHz
        check("未满载被拦截", False)
    except RuntimeError as e:
        check("未满载被拦截（防假大雕）", "未达满载" in str(e))
        print(f"    → {e}")

    # ---- Case 3：电压归零（核心隔离）→ 质控拦截 ----
    print("\n[Case 3] 电压归零（核心隔离）→ 质控拦截")
    try:
        orchestrator_collect(lhm_collect(vcore=0.05, freq=5050))
        check("电压越界被拦截", False)
    except RuntimeError as e:
        check("电压越界被拦截（防假大雕）", "电压越界" in str(e))
        print(f"    → {e}")

    # ---- Case 4：在线模式 → 相对分 ----
    print("\n[Case 4] 在线模式 → 相对分 + 分位数")
    try:
        f = orchestrator_collect(lhm_collect(vcore=1.02, freq=5050))
        r = score(f, cloud_available=True)
        print(f"    SP={r['SpScore']}  Pct={r['Percentile']}  Grade={r['Grade']}")
        check("在线模式", r["Mode"] == "OnlineRelative")
        check("有分位数", r["Percentile"] > 0)
    except Exception as e:
        check("Case4 未抛异常", False)
        print(f"    ERR: {e}")

    # ---- Case 5：跨代参数解析（Zen3/4/5 斜率/锚点）与 V/F 折算收敛 ----
    print("\n[Case 5] 跨代参数解析（代际斜率 + 锚点 + 折算）")
    # 7800X3D 型号标定 slope=0.41；5800X3D 无 slope 走 Zen3 代际默认 0.35；9950X 走 Zen5 0.45
    _, a78, s78, lvl78 = resolve(get_by_keyword("7800X3D"))
    _, a58, s58, _ = resolve(get_by_keyword("5800X3D"))
    _, a99, s99, _ = resolve(get_by_keyword("9950X"))
    check("7800X3D 斜率=型号标定 0.41 且 Calibrated", abs(s78 - 0.41) < 1e-9 and lvl78 == "Calibrated")
    check("5800X3D 斜率=Zen3 代际默认 0.35", abs(s58 - 0.35) < 1e-9)
    check("9950X 斜率=Zen5 代际默认 0.45", abs(s99 - 0.45) < 1e-9)
    check("各型号锚点均为正", a78 > 0 and a58 > 0 and a99 > 0)

    # 同一颗 7800X3D 两个不同负载点（真机数据），折算到锚点后 SP 应收敛
    vref, anchor, slope, _ = resolve(get_by_keyword("7800X3D"))
    sp_occt = calc_sp_anchored(0.975, 4526, vref, anchor, slope)
    sp_cpuz = calc_sp_anchored(1.127, 4896, vref, anchor, slope)
    print(f"    OCCT 点 SP={sp_occt:.1f}  CPU-Z 点 SP={sp_cpuz:.1f}（折算后应收敛）")
    check("不同负载点折算后 SP 收敛(差<3)", abs(sp_occt - sp_cpuz) < 3)
    check("折算 SP 均在有效区间(0-150)", 0 <= sp_occt <= 150 and 0 <= sp_cpuz <= 150)

    # ---- Case 6：型号模糊匹配 ----
    print("\n[Case 6] 型号模糊匹配（完整名 → 量表关键字）")
    b = get_by_keyword("AMD Ryzen 7 7800X3D")
    check("完整型号名匹配到 7800X3D", b is not None and b["Model"] == "7800X3D")
    check("Priority=1（7800X3D 第一）", b["Priority"] == 1)

    print(f"\n{'=' * 60}")
    print(f"结果：{pass_} passed, {fail} failed")
    print(f"{'=' * 60}")
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
