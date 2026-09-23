// Collector.Real.cs — LHM 真实采集实现（从真机验证版移植并接回主链路）
//
// 真机校准结论（见 docs/archive/真机修正说明_V1.6.6.md）：
//   1) AM5 / Zen4 主板走 SVI3 供电协议，核心电压传感器名是 "VDDCR CPU"，不是 "Core (SVI2 TFN)"。
//      → 必须弹性匹配（复用 Compat.SensorNameRules.VCore），并排除 SoC/Misc/LDO/SMU。
//   2) 单次 Update 可能抓到 P-State 切换瞬时值（每核 VID 出现 0.43/0.63V 离谱低值）。
//      → 本方法做单次稳态读取；多轮 ×150ms 聚合由上层 EvaluationService.Aggregate 负责
//        （电压/频率/温度取均值、每核 VID 取峰值），与 UI 的多点采样一致。
//   3) Computer 全进程只 Open 一次（host.EnsureComputer），避免反复加载 PawnIO 驱动导致释放失败。
//   4) 必须【文件夹发布】，禁止 --single-file，否则内嵌 PawnIO 驱动资源释放失败、读数全 0。
//
// 跨平台：采集层 TFM 为 net8.0-windows；非 Windows 由 LhmCollector.Collect() 直接回退模拟数据，
//        不会进入本文件（本文件使用 LibreHardwareMonitor 的 Windows 实现）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace Collector;

/// <summary>真实采集实现（LHM 命名校准 + 弹性匹配）</summary>
public static class RealCollector
{
    private static readonly Regex CoreIndexRe = new(@"^Core #(\d+)", RegexOptions.Compiled);

    public static HardwareFeatures? Read(LhmCollector host)
    {
        var dump = new List<SensorDump>();
        try
        {
            var computer = host.EnsureComputer();
            computer.Accept(new UpdateVisitorImpl());   // 全量 Update 一次，让传感器拿到最新值

            string? cpuModel = null;
            var coreVoltages = new List<(string Name, double Value)>();
            double avgFreq = 0, pkgTemp = 0;
            var vidByCore = new SortedDictionary<int, double>();
            var clockByCore = new SortedDictionary<int, double>();

            var vcoreRule = SensorNameRules.VCore;
            var freqRule  = SensorNameRules.AvgFreq;
            var tempRule  = SensorNameRules.PackageTemp;
            var vidRegex    = SensorNameRules.PerCore[0].RegexRule!; // ^Core #N VID$
            var effClkRegex = SensorNameRules.PerCore[1].RegexRule!; // ^Core #N (Effective)$
            var stdClkRegex = SensorNameRules.PerCore[2].RegexRule!; // ^Core #N$

            foreach (var hw in computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu)
                {
                    // 主板等非 CPU 节点（SoC/VRM/风扇…）也进 dump，便于诊断命名问题
                    foreach (var s in hw.Sensors)
                        dump.Add(new SensorDump(hw.HardwareType.ToString(), s.SensorType.ToString(), s.Name ?? "", s.Value));
                    continue;
                }

                cpuModel = hw.Name;
                foreach (var s in hw.Sensors)
                {
                    dump.Add(new SensorDump(hw.HardwareType.ToString(), s.SensorType.ToString(), s.Name ?? "", s.Value));
                    if (s.Value is null) continue;
                    double vv = s.Value.Value;
                    string nm = s.Name ?? "";

                    if (s.SensorType == SensorType.Voltage)
                    {
                        // 全部核心域电压先收集，稍后按"精确优先 → 包含兜底 → 排除词拦截"统一裁决
                        coreVoltages.Add((nm, vv));

                        // 每核 VID（CPPC 请求值）：记录核号，上层多轮取峰值
                        if (vidRegex.IsMatch(nm))
                        {
                            var mi = CoreIndexRe.Match(nm);
                            if (mi.Success)
                            {
                                int idx = int.Parse(mi.Groups[1].Value);
                                if (!vidByCore.TryGetValue(idx, out var old) || vv > old) vidByCore[idx] = vv;
                            }
                        }
                    }
                    else if (s.SensorType == SensorType.Clock)
                    {
                        if (avgFreq <= 0 && NameMatches(nm, freqRule)) avgFreq = vv;

                        var mi = CoreIndexRe.Match(nm);
                        if (mi.Success && (effClkRegex.IsMatch(nm) || stdClkRegex.IsMatch(nm)))
                        {
                            int idx = int.Parse(mi.Groups[1].Value);
                            // Effective 优先：仅当尚未记录 Effective 时才允许瞬时频率占位
                            if (!clockByCore.ContainsKey(idx) || effClkRegex.IsMatch(nm))
                                clockByCore[idx] = vv;
                        }
                    }
                    else if (s.SensorType == SensorType.Temperature)
                    {
                        if (pkgTemp <= 0 && NameMatches(nm, tempRule)) pkgTemp = vv;
                    }
                }
            }

            // 核心电压裁决：精确名（VDDCR CPU / Core (SVI2 TFN)）优先，其次包含关键词，排除 SoC/Misc/LDO/SMU
            double vcore = MatchCoreVoltage(coreVoltages, vcoreRule);

            double[]? perCoreVids = vidByCore.Count > 0
                ? vidByCore.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray() : null;
            double[]? perCoreClocks = clockByCore.Count > 0
                ? clockByCore.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray() : null;

            // ★ 不管成功失败都把 dump 交出去——失败时这是定位命名/驱动问题的唯一线索
            host.SetLastDump(dump);

            if (vcore <= 0 || avgFreq <= 0 || pkgTemp <= 0)
            {
                Logger.Instance.Warn("LHM 关键量缺失（可能未满载/驱动未加载/核心隔离开启）", new
                {
                    vcore, freq = avgFreq, temp = pkgTemp,
                    vidCores = vidByCore.Count, totalSensors = dump.Count,
                });
                return null;
            }

            string model = cpuModel ?? "Unknown";
            string zen = LhmCollector.ZenMap(model);

            return new HardwareFeatures(
                model, zen,
                vcore, avgFreq,
                host.GetRefFreq(model),
                host.GetFullLoadFreq(model),
                pkgTemp,
                perCoreVids,
                perCoreClocks,
                "LhmOnly", EnvironmentOk: true);
        }
        catch (Exception ex)
        {
            host.SetLastDump(dump);
            Logger.Instance.Error("LHM 采集异常", ex, context: new { where = "RealCollector.Read" });
            return null;
        }
    }

    /// <summary>核心电压匹配：精确名优先 → 包含关键词兜底，全程排除 SoC/Misc/LDO/SMU，并要求值在合理区间。</summary>
    private static double MatchCoreVoltage(List<(string Name, double Value)> sensors, NameRule rule)
    {
        bool Ok(string name, double v)
            => !rule.IsExcluded(name) && Sanity.IsPlausible(Field.Voltage, v);

        foreach (var (n, v) in sensors)
            if (Array.IndexOf(rule.Exact, n) >= 0 && Ok(n, v)) return v;
        foreach (var (n, v) in sensors)
            if (rule.Contains.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)) && Ok(n, v)) return v;
        return 0;
    }

    /// <summary>温度/频率的简单命中：精确名或包含关键词，且值合理。</summary>
    private static bool NameMatches(string name, NameRule rule)
    {
        if (rule.IsExcluded(name)) return false;
        if (Array.IndexOf(rule.Exact, name) >= 0) return true;
        return rule.Contains.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>LHM 标准 UpdateVisitor：遍历所有硬件并 Update，让传感器拿到最新值。</summary>
    private sealed class UpdateVisitorImpl : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            hardware.Traverse(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
