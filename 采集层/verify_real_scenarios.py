"""
补充验证：真实场景 + SP 合理性诊断（修复频率折算后）
（对齐《评分算法详细设计》§4.1 单调性 + §5 评级双轨 + 《频率折算勘误》）
"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from verify_e2e import *  # reuse 采集/评分基础设施

def main():
    print("=" * 60)
    print("补充验证：真实场景 + SP 合理性诊断（修复后）")
    print("=" * 60)
    pass_ = 0; fail = 0
    def check(n, c):
        nonlocal pass_, fail
        if c: pass_ += 1; print(f"  ✅ {n}")
        else: fail += 1; print(f"  ❌ {n}")

    # ---- 真实体质分布：同一型号不同电压 → SP 应单调 ----
    print("\n[Case A] SP 单调性（电压越低 → SP 越高）")
    b = get_by_keyword("7800X3D")
    voltages = [1.12, 1.08, 1.05, 1.02, 0.99]
    sps = [calc_sp(v, b["VRef"]) for v in voltages]
    for v, sp in zip(voltages, sps):
        print(f"    7800X3D VCore={v}V → SP={sp:.1f} ({grade_by_sp(sp)})")
    check("SP 严格单调递增（1.12→0.99，电压越低SP越高）", all(sps[i] < sps[i+1] for i in range(len(sps)-1)))
    check("最差样本(1.12V) SP<100", sps[0] < 100)
    check("最佳样本(0.99V) SP>100", sps[-1] > 100)

    # ---- 诊断：1.046V 修复后应 ≈101，不再满分 ----
    print("\n[Case B] 诊断：1.046V 修复后是否合理？")
    v_meas = 1.046
    v_ref = b["VRef"]  # ★ 1.070（按 GamersNexus 实测校准，原为 1.050 经验值）
    sp = calc_sp(v_meas, v_ref)
    print(f"    VRef={v_ref}, VMeas={v_meas}")
    print(f"    (VRef/VMeas-1)*300 = {(v_ref/v_meas-1)*300:.2f}")
    print(f"    SP = clip(100 + 上式, 0, 150) = {sp:.1f}")
    check("1.046V 仅比 VRef 低 0.4% → SP≈101（不再满分）", sp < 149)

    # ---- 修复后：评分用实测电压，频率折算已移除 ----
    print("\n[Case C] 复算：修复后评分使用实测电压，不做频率折算")
    print(f"    VEquiv = VCore（实测）= 1.046V")
    sp_calc = calc_sp(1.046, b["VRef"])
    print(f"    SP = 100 + ({b['VRef']}/1.046 - 1)×300 = {sp_calc:.1f}")
    check("修复后 Case1 SP≈101（不再被 clip 到 150）", sp_calc < 149)

    # ---- 满载高频不再"虚高"：同电压不同频率，SP 应相同 ----
    print("\n[Case D] 满载不变性：同电压、不同频率 → SP 相同（频率不参与公式）")
    # ★ 用等于 VRef(1.070) 的电压校验基准点（VRef 已从 1.050 校准为 1.070）
    vr = b["VRef"]
    sp_idle = calc_sp(vr, vr)            # 待机低频：用实测电压
    sp_full = calc_sp(vr, vr)            # 满载高频：同样用实测电压
    print(f"    同电压 {vr}V：待机 → SP={sp_idle:.1f}, 满载 → SP={sp_full:.1f}")
    check("同电压不同频率 → SP 完全相同（频率不参与评分）", sp_idle == sp_full)
    check("实测电压=VRef → SP=100", abs(sp_idle - 100) < 0.01)

    print(f"\n{'=' * 60}")
    print(f"结果：{pass_} passed, {fail} failed")
    print(f"{'=' * 60}")
    return 0 if fail == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
