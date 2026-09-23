"""
评分引擎验证（Python 等价，逻辑/接口/阈值与 C# ScoringEngine 完全一致）

重大修正（真机数据驱动，见 docs/archive/真机修正说明_V1.6.6.md）：
  旧版[3]断言「频率不参与评分」（废弃一切折算）。真机推翻该假设：
    7800X3D 实测两点：OCCT  4526MHz@0.975V → 旧算法 SP≈123（大雕）
                     CPU-Z 4896MHz@1.127V → 旧算法 SP≈79 （偏弱）
    同一颗 CPU 仅因负载不同差 44 分 → 不可重复，算法不成立。
  现改为【V/F 线性折算】：沿实测 V/F 直线把工作点归一化到统一锚定频率再比较。
  折算后两点电压 1.0873/1.0876V（差 0.3mV），SP 均 ≈95，分差收敛到 <2 分。
"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))

# ---------- 核心公式（与 C# SpScaler 一致）----------

def calc_sp(v_meas, v_ref, scale=300.0):
    """不折算的绝对 SP（旧口径，保留兼容）"""
    if v_ref <= 0:
        raise ValueError("参考电压未设置（CurveOnly 模式）")
    if v_meas <= 0:
        raise ValueError("电压必须为正")
    return max(0.0, min(150.0, 100 + (v_ref / v_meas - 1) * scale))

def anchor_voltage(v_meas, f_meas, anchor_freq, slope_mv_per_mhz):
    """★ 沿 V/F 直线归一化到锚定频率（线性、带截距，非等比乘法）"""
    if slope_mv_per_mhz <= 0 or anchor_freq <= 0:
        return v_meas                      # 无参数 → 不折算（向后兼容）
    return v_meas + (slope_mv_per_mhz / 1000.0) * (anchor_freq - f_meas)

def calc_sp_anchored(v_meas, f_meas, v_ref, anchor_freq, slope_mv_per_mhz, scale=300.0):
    return calc_sp(anchor_voltage(v_meas, f_meas, anchor_freq, slope_mv_per_mhz), v_ref, scale)

# ---------- 通用性（与 C# ZenProfile / BaselineResolver / VfFit 一致）----------

ANCHOR_TO_BOOST_RATIO = 0.94

def default_slope(zen):
    return {"Zen": 0.30, "Zen+": 0.30, "Zen2": 0.30,
            "Zen3": 0.35, "Zen4": 0.41, "Zen5": 0.45}.get(zen, 0.40)

def resolve(v_ref, zen, max_boost=0, anchor=0, slope=0, level="Estimated"):
    """三级参数解析（型号精确 → 代际默认/规格推算 → 全局默认）"""
    if slope > 0:
        s, s_src = slope, "型号标定"
    else:
        known = zen in ("Zen", "Zen+", "Zen2", "Zen3", "Zen4", "Zen5")
        s, s_src = default_slope(zen), (f"代际默认({zen})" if known else "全局默认")
    if anchor > 0:
        a, a_src = anchor, "型号标定"
    elif max_boost > 0:
        a = round(max_boost * ANCHOR_TO_BOOST_RATIO / 25.0) * 25.0
        a_src = "规格推算(Boost×0.94)"
    else:
        a, a_src = 0, "无(不折算)"
    return {"VRef": v_ref, "Anchor": a, "Slope": s,
            "SlopeSource": s_src, "AnchorSource": a_src, "Level": level}

def vf_fit(points):
    """最小二乘拟合 V/F 斜率 → (slope_mv_per_mhz, intercept, r2) 或 None"""
    pts = [(f, v) for f, v in points if f > 0 and v > 0]
    if len(pts) < 3:
        return None
    if len({f for f, _ in pts}) < 2:
        return None                        # 频率无方差
    n = len(pts)
    sx = sum(f for f, _ in pts); sy = sum(v for _, v in pts)
    sxx = sum(f * f for f, _ in pts); sxy = sum(f * v for f, v in pts)
    denom = n * sxx - sx * sx
    if abs(denom) < 1e-9:
        return None
    k = (n * sxy - sx * sy) / denom
    b = (sy - k * sx) / n
    y_mean = sy / n
    ss_tot = sum((v - y_mean) ** 2 for _, v in pts)
    ss_res = sum((v - (k * f + b)) ** 2 for f, v in pts)
    r2 = 1.0 if ss_tot < 1e-12 else 1.0 - ss_res / ss_tot
    return (k * 1000.0, b, r2)

# 五档评级（与 C# SpScaler 一致）
def grade_by_sp(sp):
    if sp >= 112: return "大雕"
    if sp >= 104: return "小雕"
    if sp >= 96:  return "普通"
    if sp >= 88:  return "小雷"
    return "大雷"

def grade_by_pct(pct):
    if pct >= 90: return "大雕"
    if pct >= 70: return "小雕"
    if pct >= 30: return "普通"
    if pct >= 10: return "小雷"
    return "大雷"

def main():
    print("=" * 60)
    print("评分引擎验证（Python 等价）")
    print("=" * 60)
    pass_ = 0; fail = 0
    def check(n, c):
        nonlocal pass_, fail
        if c: pass_ += 1; print(f"  ✅ {n}")
        else: fail += 1; print(f"  ❌ {n}")

    # 1. SP 单调性
    print("\n[1] SP 单调性")
    voltages = [1.12, 1.08, 1.05, 1.02, 0.99]
    sps = [calc_sp(v, 1.070) for v in voltages]
    for v, sp in zip(voltages, sps):
        print(f"    {v}V → SP={sp:.1f} ({grade_by_sp(sp)})")
    for i in range(len(sps)-1):
        check(f"单调：{sps[i]:.1f} < {sps[i+1]:.1f}（电压越低SP越高）", sps[i] < sps[i+1])

    # 2. 评级双轨
    print("\n[2] 评级双轨")
    check("SP≥112 → 大雕", grade_by_sp(112) == "大雕")
    check("SP 104-112 → 小雕", grade_by_sp(108) == "小雕")
    check("SP 96-104 → 普通", grade_by_sp(100) == "普通")
    check("SP 88-96 → 小雷", grade_by_sp(92) == "小雷")
    check("SP<88 → 大雷", grade_by_sp(80) == "大雷")
    check("百分制≥90 → 大雕", grade_by_pct(90) == "大雕")
    check("百分制 70-90 → 小雕", grade_by_pct(75) == "小雕")
    check("百分制 30-70 → 普通", grade_by_pct(50) == "普通")
    check("百分制 10-30 → 小雷", grade_by_pct(20) == "小雷")
    check("百分制<10 → 大雷", grade_by_pct(5) == "大雷")
    check("★ 五档齐全且不重叠",
          len({grade_by_sp(v) for v in (120, 108, 100, 92, 70)}) == 5)
    check("评级边界对称（相对 100）：112/104/96/88 每档 8 分",
          grade_by_sp(112) == "大雕" and grade_by_sp(111.9) == "小雕"
          and grade_by_sp(96) == "普通" and grade_by_sp(95.9) == "小雷")

    # 3. ★★ V/F 线性折算：真机双点收敛（核心修正）
    print("\n[3] ★ V/F 线性折算（真机双点，VRef=1.07 锚点4800 斜率0.41）")
    ANCHOR, SLOPE, VREF = 4800.0, 0.41, 1.070
    p_occt  = (4526.0, 0.975)   # OCCT 重负载：低频低压
    p_cpuz  = (4896.0, 1.127)   # CPU-Z 轻负载：高频高压

    sp_raw_occt = calc_sp(p_occt[1], VREF)
    sp_raw_cpuz = calc_sp(p_cpuz[1], VREF)
    gap_raw = abs(sp_raw_occt - sp_raw_cpuz)
    print(f"    不折算：OCCT SP={sp_raw_occt:.1f} / CPU-Z SP={sp_raw_cpuz:.1f} → 差 {gap_raw:.1f} 分")
    check(f"不折算时两点发散 > 30 分（证明旧算法不成立）", gap_raw > 30)

    sp_a_occt = calc_sp_anchored(p_occt[1], p_occt[0], VREF, ANCHOR, SLOPE)
    sp_a_cpuz = calc_sp_anchored(p_cpuz[1], p_cpuz[0], VREF, ANCHOR, SLOPE)
    gap_a = abs(sp_a_occt - sp_a_cpuz)
    print(f"    折算后：OCCT SP={sp_a_occt:.1f} / CPU-Z SP={sp_a_cpuz:.1f} → 差 {gap_a:.2f} 分")
    check(f"折算后两点收敛 < 2 分（可重复）", gap_a < 2)
    check(f"折算后 SP 落在 93–97（OCCT {sp_a_occt:.1f}）", 93 <= sp_a_occt <= 97)
    check(f"折算后 SP 落在 93–97（CPU-Z {sp_a_cpuz:.1f}）", 93 <= sp_a_cpuz <= 97)
    check("★ 修正前后结论相反：旧算法误报大雕，新算法为普通",
          sp_raw_occt >= 112 and sp_a_occt < 96)
    # 锚点电压本身应几乎相等（这才是物理事实）
    v_a1 = anchor_voltage(p_occt[1], p_occt[0], ANCHOR, SLOPE)
    v_a2 = anchor_voltage(p_cpuz[1], p_cpuz[0], ANCHOR, SLOPE)
    print(f"    锚点电压：{v_a1:.4f}V vs {v_a2:.4f}V（差 {abs(v_a1-v_a2)*1000:.1f}mV）")
    check("两点折算到锚点后电压差 < 5mV（同一颗CPU的物理一致性）", abs(v_a1 - v_a2) < 0.005)
    # 斜率为 0 时自动退回不折算（向后兼容旧量表）
    check("slope=0 → 退回不折算（向后兼容）",
          abs(calc_sp_anchored(1.0, 5000, VREF, ANCHOR, 0) - calc_sp(1.0, VREF)) < 1e-9)

    # 4. 三级降级
    print("\n[4] 三级降级")
    check("离线有 VRef → 可出分", calc_sp(1.05, 1.070) > 0)
    try:
        calc_sp(1.05, 0)
        check("VRef=0 需保护（避免除零）", False)
    except (ValueError, ZeroDivisionError):
        check("VRef=0 → 抛出保护（CurveOnly 分支）", True)

    # 5. 核心体质分布
    print("\n[5] 核心体质分布（最佳=最低VID，最差=最高VID）")
    vids = [1.10, 0.98, 1.05, 1.02, 0.99, 1.06, 1.01, 1.03]
    best = min(range(len(vids)), key=lambda i: vids[i])
    worst = max(range(len(vids)), key=lambda i: vids[i])
    print(f"    VIDs: {vids}")
    print(f"    最佳=Core#{best}({vids[best]}V), 最差=Core#{worst}({vids[worst]}V)")
    check("最佳核心 = Core#1 (0.98V)", best == 1)
    check("最差核心 = Core#0 (1.10V)", worst == 0)

    # 5b. ★ 每核 VID 有效性（SVI3 最低档占位值必须判无效，镜像 PerCoreVidQuality）
    print("\n[5b] 每核 VID 有效性判定（防 SVI3 占位值造出假核间排名）")

    def vid_plausible(vids, vcore, min_vid=0.75, margin=0.25):
        if not vids:
            return False
        mx = max(vids)
        if mx < min_vid:
            return False
        if vcore > 0.90 and mx < vcore - margin:
            return False
        return True

    bad = [0.4375, 0.4375, 0.4375, 0.43125, 0.43125, 0.45, 0.4375, 0.4375]
    check("真机占位值(全0.43V,VDDCR1.135) → 无效", vid_plausible(bad, 1.135) is False)
    check("空/None → 无效", vid_plausible([], 1.1) is False and vid_plausible(None, 1.1) is False)
    good = [1.10, 0.98, 1.05, 1.02, 0.99, 1.06, 1.01, 1.03]
    check("正常满载 VID(0.98~1.10) → 有效", vid_plausible(good, 1.05) is True)
    check("VID 与 VDDCR 脱节>0.25V → 无效", vid_plausible([0.60, 0.61], 1.10) is False)
    check("全相等但电压合理(1.05) → 仍有效", vid_plausible([1.05, 1.05], 1.05) is True)

    # 6. ★ 通用性（三级参数解析 + 代际兜底 + 自标定）
    print("\n[6] 通用性：未标定型号也能出分且诚实标注")
    # 7800X3D：已标定，保留精确值
    r = resolve(1.070, "Zen4", max_boost=5000, anchor=4800, slope=0.41, level="Calibrated")
    check("Calibrated 型号保留精确斜率 0.41", abs(r["Slope"] - 0.41) < 1e-9)
    check("Calibrated 型号保留精确锚点 4800", abs(r["Anchor"] - 4800) < 1e-9)
    check("Calibrated 标记正确", r["Level"] == "Calibrated")
    # 7950X：斜率缺省 → 回退 Zen4 代际默认 0.41
    r = resolve(1.060, "Zen4", max_boost=5700, anchor=5200, slope=0, level="Estimated")
    check("斜率缺省 → 回退 Zen4 代际默认 0.41", abs(r["Slope"] - 0.41) < 1e-9)
    check("斜率来源标记为代际默认", r["SlopeSource"] == "代际默认(Zen4)")
    # 5800X3D：Zen3 → 0.35
    r = resolve(1.120, "Zen3", max_boost=4500, anchor=4250, slope=0)
    check("Zen3 斜率缺省 → 0.35", abs(r["Slope"] - 0.35) < 1e-9)
    # 9950X：Zen5 → 0.45
    r = resolve(1.030, "Zen5", max_boost=5700, anchor=5400, slope=0)
    check("Zen5 斜率缺省 → 0.45", abs(r["Slope"] - 0.45) < 1e-9)
    # 锚点缺省 → 按 Boost×0.94 推算（取整到 25MHz）
    r = resolve(1.060, "Zen4", max_boost=5000, anchor=0)
    check("锚点缺省 → 按 Boost×0.94 推算 (5000→4700)", abs(r["Anchor"] - 4700) < 1e-9)
    check("锚点来源标记为规格推算", r["AnchorSource"] == "规格推算(Boost×0.94)")
    # 未知代际 → 全局默认 0.40
    r = resolve(1.05, "Unknown", max_boost=4000, anchor=0)
    check("未知代际 → 全局默认斜率 0.40", abs(r["Slope"] - 0.40) < 1e-9)
    check("未知代际标记为全局默认", r["SlopeSource"] == "全局默认")
    # 无锚点无Boost → 不折算
    r = resolve(1.05, "Zen4", max_boost=0, anchor=0)
    check("无锚点 → 不折算（Anchor=0）", r["Anchor"] == 0)
    # 自标定：最小二乘还原已知斜率
    pts = [(f, 0.975 + 0.00041 * (f - 4526)) for f in (3000, 3500, 4000, 4500, 5000, 5200)]
    fit = vf_fit(pts)
    check("自标定返回拟合结果（6 点）", fit is not None)
    if fit:
        k, _, r2 = fit
        print(f"    拟合斜率={k:.4f} mV/MHz, R²={r2:.4f}")
        check("最小二乘精确还原 0.41 mV/MHz", abs(k - 0.41) < 1e-6)
        check("线性度 R² ≈ 1.0", abs(r2 - 1.0) < 1e-6)
    # 自标定边界：样本不足 / 频率无方差 → null
    check("样本不足(<3点) → 返回 None", vf_fit([(3000, 0.9), (4000, 1.0)]) is None)
    check("频率无方差 → 返回 None",
          vf_fit([(4000, 0.9), (4000, 0.95), (4000, 1.0)]) is None)

    print(f"\n{'=' * 60}")
    print(f"结果：{pass_} passed, {fail} failed")
    print(f"{'=' * 60}")
    return 0 if fail == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
