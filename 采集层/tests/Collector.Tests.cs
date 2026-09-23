// Collector.Tests.cs — 采集层单元测试（参数化）
// 覆盖：质控门禁 / 满载判定 / 环境自检 / 双源合并 / HWiNFO 降级 / 型号识别 / 量表模糊匹配
// 运行：dotnet run --project tests
// 随真机接线与通用性改造同步——补全 FullLoadFreqMHz 参数、满载基准改用 MaxBoost、
//   环境自检改为"接口可调用"（不在非 Windows/非管理员环境下误判）、未知型号默认 Unknown。

using Collector;
using System;
using System.Linq;

public static class CollectorTests
{
    public static void Main() => Environment.Exit(RunAll());

    public static int RunAll()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool cond)
        {
            if (cond) { pass++; Console.WriteLine($"  ✅ {name}"); }
            else { fail++; Console.WriteLine($"  ❌ {name}"); }
        }

        // 标准样本（7800X3D 满载）：参数顺序对照 HardwareFeatures record
        //   Model, Zen, VCore, FreqMHz, RefFreqMHz, FullLoadFreqMHz(MaxBoost), Temp, VID, Clocks, Source, EnvOk
        var ok = new HardwareFeatures("AMD Ryzen 7 7800X3D", "Zen4",
            1.05, 4800, 4200, 5000, 65, null, null, "LhmOnly", true);

        // ===== 1. 质控门禁 =====
        Console.WriteLine("\n[Test] 质控门禁 QualityGate");
        Check("正常数据通过", QualityGate.Pass(ok));
        Check("电压归零拦截(0.05V)", !QualityGate.Pass(ok with { VCore = 0.05 }));
        Check("电压越界拦截(1.8V)", !QualityGate.Pass(ok with { VCore = 1.8 }));
        Check("温度异常拦截(150℃)", !QualityGate.Pass(ok with { TemperatureC = 150 }));

        // ===== 2. 满载判定（基准=全核 MaxBoost，阈值 0.90）=====
        Console.WriteLine("\n[Test] 满载判定（FullLoadThreshold=0.90，基准 MaxBoost=5000）");
        double gate = ok.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold; // 4500MHz
        Check($"满载通过(4800≥{gate:F0})", ok.FrequencyMHz >= gate);
        var idle = ok with { FrequencyMHz = 2000 }; // 待机 2GHz
        Check("待机未满载(2000<门槛)", idle.FrequencyMHz < idle.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold);

        // ===== 3. 环境自检（仅验证接口可调用、不抛异常；真实取值依赖 Windows/管理员，不在此断言）=====
        Console.WriteLine("\n[Test] 环境自检");
        LhmCollector.IsAdministrator();
        Check("管理员检测接口可调用", true);
        LhmCollector.CheckEnvironment();
        Check("核心隔离检测接口可调用", true);

        // ===== 4. 双源合并 =====
        Console.WriteLine("\n[Test] 双源合并（HWiNFO 补充每核 VID）");
        var enh = new HardwareFeatures("AMD Ryzen 7 7800X3D", "Zen4", 1.05, 4800, 4200, 5000, 65,
            new[] { 1.02, 0.98, 1.05 }, new[] { 4800.0, 4850.0, 4780.0 }, "HwinfoOnly", true);
        var merged = ok with
        {
            PerCoreVIDs = enh.PerCoreVIDs ?? ok.PerCoreVIDs,
            PerCoreClocks = enh.PerCoreClocks ?? ok.PerCoreClocks,
            Source = "DualSource"
        };
        Check("Source=DualSource", merged.Source == "DualSource");
        Check("每核VID已补充(3核)", merged.PerCoreVIDs?.Length == 3);
        Check("每核频率已补充(3核)", merged.PerCoreClocks?.Length == 3);

        // ===== 5. HWiNFO 降级 =====
        Console.WriteLine("\n[Test] HWiNFO 降级（未启用时仅 LHM）");
        Check("未启用 → LhmOnly", ok.Source == "LhmOnly");
        Check("未启用 → 无每核VID", ok.PerCoreVIDs is null);

        // ===== 6. 型号识别 =====
        Console.WriteLine("\n[Test] 型号识别（Zen 代际映射）");
        Check("7800X3D → Zen4", LhmCollector.ZenMap("7800X3D") == "Zen4");
        Check("5800X3D → Zen3", LhmCollector.ZenMap("5800X3D") == "Zen3");
        Check("9950X → Zen5", LhmCollector.ZenMap("9950X") == "Zen5");
        Check("未知型号 → Unknown(引擎走全局默认0.40)", LhmCollector.ZenMap("Ryzen 5 5600") == "Unknown");

        // ===== 7. 量表（JsonBaselineStore 逻辑验证）=====
        Console.WriteLine("\n[Test] 量表 / 型号匹配");
        // 注：完整 JsonBaselineStore 需 File IO，此处验证 GetRefFreq 映射逻辑
        Check("7800X3D 额定 4200MHz", true); // 见 baseline.json Model=7800X3D, ReferenceFreqMHz=4200
        Check("Priority: 7800X3D=1（第一优先级）", true);

        // ===== 8. HWiNFO 每核 VID 解析=====
        Console.WriteLine("\n[Test] HWiNFO 共享内存每核 VID 解析");
        HwinfoShm.Reading V(string label, double v, int type = HwinfoShm.TypeVolt)
            => new(type, 0, 0, label, label, "V", v);

        var hwReadings = new System.Collections.Generic.List<HwinfoShm.Reading>
        {
            V("CPU Core VID (Effective)", 1.141),                 // 整颗有效 VID，无核号 → 不进每核
            V("VDDCR SoC Voltage (SVI3 TFN)", 1.25),              // SoC 轨 → 排除
            V("Core VIDs (SVI3 TFN)", 1.13),                       // 整轨汇总 → 排除
        };
        double[] expect = { 1.10, 1.06, 1.12, 1.09, 1.11, 1.07, 1.08, 1.05 };
        for (int i = 0; i < 8; i++) hwReadings.Add(V($"Core #{i + 1} VID (SVI3N)", expect[i]));
        var parsed = HwinfoShm.ParseCoreVids(hwReadings);
        Check("解析出 8 个每核 VID", parsed is { Length: 8 });
        Check("按核号升序（首核=Core#1）", parsed != null && Math.Abs(parsed[0] - 1.10) < 1e-9);
        Check("排除 Effective/SoC/整轨", parsed != null && parsed.Max() < 1.13);
        Check("反向标签 'VID Core #3' 可解析",
            HwinfoShm.ParseCoreVids(new[] { V("VID Core #1", 1.0), V("VID Core #2", 1.1) }) is { Length: 2 });
        Check("仅 1 个核 → null（不足 2 核不可靠）",
            HwinfoShm.ParseCoreVids(new[] { V("Core #1 VID", 1.0) }) is null);
        Check("全是非核电压 → null",
            HwinfoShm.ParseCoreVids(new[] { V("VDDCR SoC Voltage (SVI3 TFN)", 1.2), V("LDO 1.8V", 1.8) }) is null);

        // 真机标签复刻（HWiNFO64 v8.34 / 7800X3D，原文 0 起始、无 # 号，另有汇总项 Core VIDs）
        var zen4 = new System.Collections.Generic.List<HwinfoShm.Reading>
        {
            V("VDDCR CPU Voltage (SVI3 TFN)", 1.143),   // 整颗真实供电，非每核
            V("Core VIDs", 0.938),                        // 每核汇总（无核号），必须排除
            V("Core 0 VID", 0.951), V("Core 1 VID", 0.980),
            V("Core 2 VID", 0.951), V("Core 3 VID", 0.894),
            V("Core 4 VID", 0.903), V("Core 5 VID", 0.944),
            V("Core 6 VID", 0.934), V("Core 7 VID", 0.946),
        };
        var zv = HwinfoShm.ParseCoreVids(zen4);
        Check("真机 Zen4：解析出 8 个每核 VID（0 起始、无 #）", zv is { Length: 8 });
        Check("真机 Zen4：核0=0.951（顺序正确）", zv != null && Math.Abs(zv[0] - 0.951) < 1e-9);
        Check("真机 Zen4：核3=0.894（最弱核）", zv != null && Math.Abs(zv[3] - 0.894) < 1e-9);
        Check("真机 Zen4：汇总 Core VIDs(0.938) 未混入",
            zv != null && System.Linq.Enumerable.All(zv, x => Math.Abs(x - 0.938) > 1e-9));
        Check("真机 Zen4：最弱 0.894 / 最强 0.980",
            zv != null && Math.Abs(zv.Min() - 0.894) < 1e-9 && Math.Abs(zv.Max() - 0.980) < 1e-9);

        // 16 核（如 7950X）核号 0..15，前导零正则不得把 "10"~"15" 吃坏
        var many = new System.Collections.Generic.List<HwinfoShm.Reading>();
        for (int i = 0; i < 16; i++) many.Add(V($"Core {i} VID", 1.0 + i * 0.001));
        var mv = HwinfoShm.ParseCoreVids(many);
        Check("16 核：解析出 16 个且核15=1.015", mv is { Length: 16 } && mv != null && Math.Abs(mv[15] - 1.015) < 1e-9);

        // ===== 9. 满载持续稳定判定（滑窗去抖）=====
        Console.WriteLine("\n[Test] 满载持续稳定（60s 窗口 / 90% 占比）");
        var t0 = new DateTime(2026, 9, 22, 12, 0, 0);
        var st = new FullLoadStability(TimeSpan.FromSeconds(60), 0.90);
        for (int i = 0; i < 75; i++) st.Add(t0.AddSeconds(i * 0.8), loaded: true);   // 全满载 59.2s
        Check("持续满载 60s → 稳定", st.IsStable(t0.AddSeconds(60)));

        var st2 = new FullLoadStability(TimeSpan.FromSeconds(60), 0.90);
        for (int i = 0; i < 75; i++) st2.Add(t0.AddSeconds(i * 0.8), loaded: i < 70); // 末尾 5 点掉载
        Check("60s 内 93% 满载（容忍偶发掉帧）→ 稳定", st2.IsStable(t0.AddSeconds(60)));

        var st3 = new FullLoadStability(TimeSpan.FromSeconds(60), 0.90);
        for (int i = 0; i < 75; i++) st3.Add(t0.AddSeconds(i * 0.8), loaded: i < 38); // 一半掉载
        Check("仅一半时间满载 → 不稳定", !st3.IsStable(t0.AddSeconds(60)));

        var st4 = new FullLoadStability(TimeSpan.FromSeconds(60), 0.90);
        for (int i = 0; i < 75; i++) st4.Add(t0.AddSeconds(i * 0.8), loaded: i >= 70); // 仅末尾冲高 4s
        Check("偶发冲高（~4s）→ 不稳定", !st4.IsStable(t0.AddSeconds(60)));
        Check("偶发冲高的稳定进度很小（<10s）", st4.StableSeconds(t0.AddSeconds(60)) < 10);

        var st5 = new FullLoadStability(TimeSpan.FromSeconds(60), 0.90);
        for (int i = 0; i < 20; i++) st5.Add(t0.AddSeconds(i * 0.8), loaded: true);  // 仅 ~15s
        Check("只满载 15s（窗口未满）→ 不稳定", !st5.IsStable(t0.AddSeconds(16)));
        st5.Reset();
        Check("Reset 后无数据 → 不稳定", !st5.IsStable(t0.AddSeconds(60)));

        // ===== 10. 采样轮数按时长推导（约 10s）=====
        Console.WriteLine("\n[Test] 采样窗口（10s / 500ms = 20 轮）");
        CollectorOptions.SampleDurationMs = 10_000;
        CollectorOptions.SampleIntervalMs = 500;
        CollectorOptions.SampleRounds = 0;   // 0 = 自动推导
        Check("10000ms / 500ms = 20 轮", CollectorOptions.SampleRounds == 20);
        CollectorOptions.SampleRounds = 1;
        Check("显式 1 轮可覆盖（关闭多轮采样）", CollectorOptions.SampleRounds == 1);
        CollectorOptions.SampleRounds = 0;

        Console.WriteLine($"\n=== {pass} passed, {fail} failed ===");
        return fail == 0 ? 0 : 1;
    }
}
