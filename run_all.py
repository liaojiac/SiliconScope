"""
run_all.py — SiliconScope · 全量回归入口
用法：python3 run_all.py

覆盖：
  1) 采集层 7 项验证（命名校准 / 跨主板兼容 / 探测模式 / 日志诊断导出 / 真实场景 / 端到端 / C# 结构）
  2) 评分引擎算法验证
  3) WPF UI 层静态结构校验

通过标准：全部 0 失败 → exit 0
"""
import os, subprocess, sys

ROOT = os.path.dirname(os.path.abspath(__file__))

SUITES = [
    ("采集层回归", os.path.join(ROOT, "采集层", "run_all_tests.py")),
    ("评分引擎",   os.path.join(ROOT, "评分引擎", "verify_scoring.py")),
    ("WPF UI 层",  os.path.join(ROOT, "UI", "verify_ui.py")),
]


def main():
    overall = 0
    print("=" * 64)
    print("SiliconScope · 全量回归")
    print("=" * 64)

    for name, path in SUITES:
        print()
        print("-" * 64)
        print(f"▶ {name}")
        print(f"  ({path})")
        print("-" * 64)
        if not os.path.exists(path):
            print(f"  ⚠️  未找到，跳过：{path}")
            overall += 1
            continue
        env = {**os.environ, "PYTHONUNBUFFERED": "1"}
        rc = subprocess.run([sys.executable, "-u", path], env=env).returncode
        if rc != 0:
            overall += 1

    print()
    print("=" * 64)
    if overall == 0:
        print("🎉 全量回归通过")
    else:
        print(f"❌ 存在失败套件：{overall}")
    print("=" * 64)
    sys.exit(0 if overall == 0 else 1)


if __name__ == "__main__":
    main()
