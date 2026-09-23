using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Collector;
using ScoringEngine;
using SiliconScope.UI.Models;

namespace SiliconScope.UI.Services;

/// <summary>环境自检结果（纯数据；UI 显示用的图标/颜色由 RunViewModel.EnvItem 计算）</summary>
public sealed record EnvCheck(string Name, bool Ok, string Detail);

/// <summary>
/// 评测服务：把「采集 → 质控 → 评分」编排起来，供 UI 调用。
/// ★ 界面层不碰任何硬件/算法细节，全部委托给已有的 Collector 与 ScoringEngine。
/// 单例 LhmCollector（Computer/PawnIO 驱动只打开一次）、真实环境自检、
///   两套 Baseline 经适配器桥接、设置接线、可选本机 V/F 自标定。
/// </summary>
public sealed class EvaluationService : IDisposable
{
    private readonly JsonBaselineStore? _store;
    private readonly AppSettings _settings;
    private readonly LhmCollector _collector;

    public EvaluationService() : this(AppSettings.Load()) { }

    /// <summary>注入同一份设置实例（MainViewModel 持有，设置页改动即时生效）。</summary>
    public EvaluationService(AppSettings settings)
    {
        // 量表从 baseline.json 读取；缺失时降级（环境自检会提示，不崩溃）
        var path = Path.Combine(AppContext.BaseDirectory, "baseline.json");
        _store = File.Exists(path) ? new JsonBaselineStore(path) : null;
        _settings = settings;
        _settings.Apply();
        _collector = new LhmCollector(_store);
    }

    /// <summary>环境自检：管理员 / 内核隔离兼容性 / LHM 驱动实测 / 量表</summary>
    public List<EnvCheck> CheckEnvironment()
    {
        var list = new List<EnvCheck>();

        bool admin = LhmCollector.IsAdministrator();
        list.Add(new EnvCheck("管理员权限", admin, admin ? "已获取" : "需右键以管理员身份运行"));

        // 内核隔离/内存完整性不再是否决项。PawnIO 驱动兼容 HVCI，
        //   无需关闭该安全功能；此项恒为通过，仅展示状态，真正判据是下方"LHM 驱动实测"。
        bool hvci = LhmCollector.IsMemoryIntegrityEnabled();
        list.Add(new EnvCheck("内核隔离兼容性", true,
            hvci ? "内存完整性已开启：PawnIO 兼容，可正常读取，无需关闭安全功能" : "内存完整性未开启，正常"));

        bool lhm = TryOpenLhm(out int sensorCount);
        list.Add(new EnvCheck("LHM 驱动（PawnIO）实测", lhm,
            lhm ? $"{sensorCount} 个传感器" : "未能读到传感器 → 请以管理员运行，并确认使用的是完整文件夹（勿用单文件）"));

        bool hasTable = _store is not null;
        list.Add(new EnvCheck("基准量表", hasTable,
            hasTable ? "baseline.json 已加载" : "未找到 baseline.json"));

        return list;
    }

    /// <summary>读取一次实时读数（用于"等待满载"界面的实时显示）</summary>
    public HardwareFeatures? Peek()
    {
        try
        {
            return _collector.Collect();
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("实时读数失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 执行完整评测：多点采样 → 质控 → 评分。
    /// 返回 (结果, 错误信息)；失败时 result 为 null 且 error 说明原因。
    /// </summary>
    public (EvaluationResult? result, string? error) Evaluate(
        IProgress<(int done, int total, double freq, double volt)>? progress = null)
    {
        var checks = CheckEnvironment();
        if (checks.Any(c => !c.Ok))
        {
            var bad = string.Join("；", checks.Where(c => !c.Ok).Select(c => c.Name));
            return (null, $"环境自检未通过：{bad}");
        }

        // —— 1) 多点采样窗口约 10s，消除 P-State 瞬时值并让稳态电压更可信）——
        //   底层单次 Update，外层按 SampleDurationMs/SampleIntervalMs 推得的轮数循环聚合。
        var samples = new List<HardwareFeatures>();
        var hwVidRounds = new List<double[]>();
        var hw = new HwinfoCollector { Enabled = _settings.EnableHwinfo };

        int rounds = Math.Max(1, CollectorOptions.SampleRounds);
        int loadedRounds = 0;
        for (int i = 0; i < rounds; i++)
        {
            var f = Peek();
            if (f is null) return (null, $"第 {i + 1} 轮采样失败，未取到传感器数据");
            samples.Add(f);
            if (f.FrequencyMHz >= f.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold)
                loadedRounds++;

            // HWiNFO 增强：每轮顺带读一次每核 VID（正确的 SVI3 解码），跨轮取峰。
            if (_settings.EnableHwinfo)
            {
                try
                {
                    var enh = hw.Collect();
                    if (enh?.PerCoreVIDs is { Length: >= 2 })
                        hwVidRounds.Add(enh.PerCoreVIDs);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Warn("HWiNFO 单轮读取异常（忽略，继续 LHM 流程）", new { msg = ex.Message });
                }
            }

            progress?.Report((i + 1, rounds, f.FrequencyMHz, f.VCore));
            if (i < rounds - 1)
                System.Threading.Thread.Sleep(CollectorOptions.SampleIntervalMs);
        }

        // 采样窗口内必须绝大多数轮次处于满载，防止采样中途掉载仍出分（呼应“持续满载”要求）
        double loadRatio = (double)loadedRounds / rounds;
        if (loadRatio < 0.80)
            return (null, $"采样的约 {CollectorOptions.SampleDurationMs / 1000} 秒内未持续满载（仅 {loadRatio:P0} 轮次达标），请保持烤机满载后重试。");

        var main = Aggregate(samples);

        // —— 1b) HWiNFO 每核 VID 合并：HWiNFO 正确解码 SVI3，优先于 LHM 的每核 VID；
        //   仍要过同一道有效性判定，坏数据照样丢弃。无 HWiNFO 时维持 Aggregate 的 LHM 结果（Zen4 已置 null）。
        if (hwVidRounds.Count > 0)
        {
            int n = hwVidRounds.Max(r => r.Length);
            var hwPeak = new double[n];
            for (int i = 0; i < n; i++)
            {
                double best = double.NaN;
                foreach (var r in hwVidRounds)
                    if (i < r.Length && !double.IsNaN(r[i]) && (double.IsNaN(best) || r[i] > best)) best = r[i];
                hwPeak[i] = best;
            }
            if (hwPeak.All(v => !double.IsNaN(v)) && PerCoreVidQuality.IsPlausible(hwPeak, main.VCore))
            {
                main = main with { PerCoreVIDs = hwPeak, Source = "LHM+HWiNFO" };
                Logger.Instance.Info("已采用 HWiNFO 每核 VID（SVI3 正确解码）",
                    new { cores = n, min = hwPeak.Min(), max = hwPeak.Max() });
            }
            else
            {
                Logger.Instance.Warn("HWiNFO 每核 VID 未通过有效性判定，不输出核间排名",
                    new { min = hwPeak.Min(), max = hwPeak.Max(), vcore = main.VCore });
            }
        }

        // —— 2) 质控（编排器统一判定：满载 / 合理性 / 电压温度区间）——
        var orch = new CollectorOrchestrator(_collector, hw);
        var validated = orch.Validate(main);
        if (validated.RejectReason is not null)
            return (null, validated.RejectReason);

        // —— 3) 评分 ——
        if (_store is null)
            return (null, "未找到 baseline.json，无法评分");

        var baseline = _store.Get(main.Model) ?? _store.GetByKeyword(main.Model);
        if (baseline is null)
            return (null, $"未收录型号：{main.Model}（请在 baseline.json 补充）");

        // 量表参数（VRef / 锚点 / 标定等级展示用）
        var rb = BaselineResolver.Resolve(BaselineAdapter.ToEngine(baseline));

        // 默认量表（经适配器）；开启自标定且多点确实存在频率方差时，用本机拟合斜率覆盖
        ScoringEngine.IBaselineStore engineStore = new EngineBaselineStore(_store);
        double slopeForDisplay = rb.SlopeMvPerMhz;
        string slopeSource = rb.SlopeSource;
        if (_settings.SelfFit)
        {
            var fit = VfFit.Fit(samples.Select(s => (s.FrequencyMHz, s.VCore)));
            if (fit is not null && fit.RSquared >= 0.90 && fit.SlopeMvPerMhz > 0)
            {
                var fitted = BaselineAdapter.ToEngine(baseline) with { SlopeMvPerMhz = fit.SlopeMvPerMhz };
                engineStore = new FixedBaselineStore(fitted);
                slopeForDisplay = fit.SlopeMvPerMhz;
                slopeSource = $"本机自标定(R²={fit.RSquared:F2}, n={fit.PointCount})";
            }
        }

        var engine = new ScoreEngine(engineStore, cloud: null);
        var input = main.ToScoreInput();

        ScoreResult sr;
        try
        {
            sr = engine.Score(input);
        }
        catch (Exception ex)
        {
            return (null, $"评分失败：{ex.Message}");
        }

        var vAnchor = SpScaler.AnchorVoltage(main.VCore, main.FrequencyMHz,
            rb.AnchorFreqMHz, slopeForDisplay);

        return (new EvaluationResult
        {
            Model = main.Model,
            Zen = main.ZenGeneration,
            VCore = main.VCore,
            FreqMHz = main.FrequencyMHz,
            ReferenceFreqMHz = main.ReferenceFreqMHz,
            FullLoadFreqMHz = main.FullLoadFreqMHz,
            TempC = main.TemperatureC,
            DataSource = main.Source,
            AppVersion = AppInfo.Version,

            SpScore = sr.SpScore,
            Percentile = sr.Percentile,
            Grade = sr.Grade,
            GradeColor = SpScaler.GradeColor(sr.Grade),
            Mode = sr.Mode,
            Confidence = sr.Confidence,
            Note = sr.ReferenceNote,

            VAnchor = vAnchor,
            VRef = rb.VRef,
            AnchorFreqMHz = rb.AnchorFreqMHz,
            SlopeMvPerMhz = slopeForDisplay,
            SlopeSource = slopeSource,
            AnchorSource = rb.AnchorSource,
            CalibrationLevel = rb.CalibrationLevel,

            PerCoreVIDs = main.PerCoreVIDs,
            BestCoreIndex = ParseIndex(sr.BestCore),
            WorstCoreIndex = ParseIndex(sr.WorstCore),

            VfPoints = samples.Select(s => (s.FrequencyMHz, s.VCore)).ToList(),
            EnvironmentChecks = checks
                .Select(c => (c.Name, c.Ok, c.Detail)).ToList(),
        }, null);
    }

    private static int ParseIndex(string? core)
    {
        if (string.IsNullOrEmpty(core)) return -1;
        var digits = new string(core.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int i) ? i : -1;
    }

    /// <summary>
    /// 多轮聚合：电压/频率/温度取均值（稳态），每核 VID 取峰值。
    /// ★ 单次 Update 会抓到 P-State 切换瞬时值（真机曾出现 0.43V 离谱低值）。
    /// </summary>
    private static HardwareFeatures Aggregate(List<HardwareFeatures> samples)
    {
        var first = samples[0];
        double vcore = samples.Average(s => s.VCore);
        double freq = samples.Average(s => s.FrequencyMHz);
        double temp = samples.Average(s => s.TemperatureC);

        double[]? perCore = null;
        var withCores = samples.Where(s => s.PerCoreVIDs is { Length: > 0 }).ToList();
        if (withCores.Count > 0)
        {
            int n = withCores[0].PerCoreVIDs!.Length;
            perCore = new double[n];
            for (int i = 0; i < n; i++)
                perCore[i] = withCores.Max(s => s.PerCoreVIDs![i]);   // 每核取峰值
        }

        // 每核 VID 有效性判定（真机证据：7800X3D/SVI3 满载时 VDDCR 已 1.135V，
        //   而 LHM 的 Core #N VID 恒为 0.43~0.45V 最低档占位值，与真实供电完全脱节）。
        //   判定逻辑在可跨平台单测的 ScoringEngine.PerCoreVidQuality；坏数据置 null，不输出假核间排名。
        if (perCore is { Length: > 0 } && !PerCoreVidQuality.IsPlausible(perCore, vcore))
        {
            Logger.Instance.Warn("每核 VID 读数无效（该平台 LHM 返回最低档占位值），不输出核间排名",
                new { mx = perCore.Max(), mn = perCore.Min(), vcore });
            perCore = null;
        }

        return new HardwareFeatures(
            first.Model, first.ZenGeneration, vcore, freq,
            first.ReferenceFreqMHz, first.FullLoadFreqMHz, temp,
            perCore, first.PerCoreClocks, first.Source, first.EnvironmentOk);
    }

    /// <summary>真实打开一次 LHM 并返回传感器数（复用单例采集器，不再写死 74）。</summary>
    private bool TryOpenLhm(out int sensorCount)
    {
        sensorCount = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var f = _collector.Collect();
            sensorCount = _collector.LastSensorCount;   // 真实遍历到的传感器数量
            return f is not null;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("LHM 打开失败", new { msg = ex.Message });
            return false;
        }
    }

    public void Dispose() => _collector.Dispose();

    /// <summary>自标定命中时，用固定（已覆盖斜率）的量表供本次评分。</summary>
    private sealed class FixedBaselineStore : ScoringEngine.IBaselineStore
    {
        private readonly ScoringEngine.Baseline _b;
        public FixedBaselineStore(ScoringEngine.Baseline b) => _b = b;
        public ScoringEngine.Baseline? Get(string model) => _b;
    }
}
