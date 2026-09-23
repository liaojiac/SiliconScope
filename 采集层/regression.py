"""
回归验证：修复频率折算后，重跑全部用例
确保：SP 单调性 / 满载拦截 / 质控 / 跨代归一化 全部通过
"""
import sys, os, subprocess
sys.path.insert(0, os.path.dirname(__file__))

def run_script(path):
    r = subprocess.run([sys.executable, path], capture_output=True, text=True)
    return r.returncode, r.stdout, r.stderr

def main():
    print("=" * 60)
    print("回归验证套件")
    print("=" * 60)

    # 用例 1：端到端主流程
    print("\n▶ 运行 verify_e2e.py ...")
    code, out, err = run_script(os.path.join(os.path.dirname(__file__), "verify_e2e.py"))
    print(out)
    if err.strip(): print("STDERR:", err)
    e2e_pass = (code == 0)

    # 用例 2：真实场景 + 单调性
    print("▶ 运行 verify_real_scenarios.py ...")
    code2, out2, err2 = run_script(os.path.join(os.path.dirname(__file__), "verify_real_scenarios.py"))
    print(out2)
    if err2.strip(): print("STDERR:", err2)
    real_pass = (code2 == 0)

    # 用例 3：SP 单调性（独立断言，修复后必须严格递减）
    print("▶ 独立 SP 单调性校验 ...")
    from verify_e2e import get_by_keyword, calc_sp
    b = get_by_keyword("7800X3D")
    voltages = [1.12, 1.08, 1.05, 1.02, 0.99]
    sps = [calc_sp(v, b["VRef"]) for v in voltages]
    mono = all(sps[i] > sps[i+1] for i in range(len(sps)-1))
    print(f"    SP 序列: {[f'{s:.1f}' for s in sps]}")
    print(f"    {'✅' if mono else '❌'} 严格单调递减: {mono}")

    # 用例 4：修复后的 Case1 不应再得满分 150
    print("\n▶ 修复验证：7800X3D 1.046V 不应再满分 ...")
    sp_now = calc_sp(1.046, b["VRef"])
    print(f"    SP(1.046V) = {sp_now:.1f}")
    no_cap = sp_now < 149
    print(f"    {'✅' if no_cap else '❌'} 未被 clip 到 150: {no_cap}")

    print("\n" + "=" * 60)
    all_ok = e2e_pass and real_pass and mono and no_cap
    print("总结果:", "ALL PASS ✅" if all_ok else "HAS FAIL ❌")
    print("=" * 60)
    return 0 if all_ok else 1

if __name__ == "__main__":
    sys.exit(main())
