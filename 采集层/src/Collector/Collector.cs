// Collector.cs — 硬件采集层（对接真实 LibreHardwareMonitor）
// 设计依据：《采集SDK设计 V1.1》§3-§6
// 真机对接：《LHM字段映射与采集校准手册.md》Step 1-4
// 依赖：LibreHardwareMonitorLib (NuGet) — MPL-2.0 免费商用
//
// ★ 本文件已补全 LhmCollector 真实实现（骨架时期的 return null 已替换为完整采集逻辑）。
//   采集到的 VCore 需经 QualityGate（电压/温度/满载判定）后才交给评分引擎。
//   【评分一律使用实测稳态电压，频率仅用于满载质控】—— 见《频率折算勘误.md》。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Collector;

#region ---- 领域模型 ----

/// <summary>采集层输出 → 评分引擎输入（对接 ScoreInput）</summary>
public record HardwareFeatures(
    string Model,
    string ZenGeneration,
    double VCore,           // 核心电压（稳态均值）
    double FrequencyMHz,    // 当前频率
    double ReferenceFreqMHz,  // 基础频率（仅展示；★不用于满载判定）
    double FullLoadFreqMHz,   // ★ 标称全核 MaxBoost —— 满载判定的正确基准
    double TemperatureC,
    double[]? PerCoreVIDs,
    double[]? PerCoreClocks,
    string Source,          // LhmOnly / DualSource / LhmOnly(Fallback)
    bool EnvironmentOk      // 环境自检是否通过
)
{
    /// <summary>
    /// 采集层输出 → 评分引擎输入（串起主链路）。
    /// 此前 Program.cs 缺这一步，评分被硬编码成 101.1，采到什么都输出同一分。
    /// </summary>
    public ScoringEngine.ScoreInput ToScoreInput()
        => new ScoringEngine.ScoreInput(
            Model, ZenGeneration, VCore, FrequencyMHz,
            ReferenceFreqMHz, TemperatureC, PerCoreVIDs, PerCoreClocks);
}

/// <summary>内置基准量表（对应 数据库设计 §6.1 + 内置基准量表.xlsx）</summary>
public record Baseline(
    string Model, string ZenGeneration, double VRef,
    double ReferenceFreqMHz, int Priority,
    double MaxBoostMHz = 0,        // 标称最大加速频率（满载判定基准 + 推算锚点）
    double AnchorFreqMHz = 0,      // 锚定频率；0 → 按 MaxBoost×0.94 推算
    double SlopeMvPerMhz = 0,      // V/F 斜率 mV/MHz；0 → 按 Zen 代际默认
    string CalibrationLevel = "Estimated");


#endregion

#region ---- 采集源接口 ----

public interface ICollector
{
    HardwareFeatures? Collect();
}

/// <summary>LHM 采集器（主采集源）—— 真实实现接回真机）</summary>
public class LhmCollector : ICollector, IDisposable
{
    private readonly IBaselineStore? _baseline;
    private LibreHardwareMonitor.Hardware.Computer? _computer;
    private readonly object _gate = new();

    public LhmCollector(IBaselineStore? baseline) => _baseline = baseline;

    /// <summary>最近一次采集的原始传感器 dump（供 DiagnosticBuilder 记录）</summary>
    internal IReadOnlyList<SensorDump> LastDump { get; private set; } = Array.Empty<SensorDump>();

    internal void SetLastDump(IReadOnlyList<SensorDump> dump) => LastDump = dump;

    /// <summary>最近一次遍历到的传感器数量（供 UI 环境自检展示，不暴露内部 dump 结构）。</summary>
    public int LastSensorCount => LastDump.Count;

    /// <summary>
    /// 单例 Computer：LHM 打开时会加载 PawnIO 内核驱动。反复 Open/Close 既慢，
    /// 又可能导致驱动资源释放/重载失败（单文件发布读数全 0 的同类问题），
    /// 因此整个进程只 Open 一次，每次 Collect 仅做 Update。
    /// </summary>
    internal LibreHardwareMonitor.Hardware.Computer EnsureComputer()
    {
        if (_computer is not null) return _computer;
        lock (_gate)
        {
            if (_computer is not null) return _computer;
            var c = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled         = true,
                IsMotherboardEnabled = true,   // SoC / VRM / 主板传感器
                IsMemoryEnabled      = false,
                IsGpuEnabled         = false,
            };
            c.Open();
            _computer = c;
            return c;
        }
    }

    public HardwareFeatures? Collect()
    {
        // 非 Windows（跨平台编译 / 单元测试环境）没有 Ring0 驱动，回退模拟数据；
        // Windows 上走真实采集，失败由 RealCollector 返回 null（编排器据此拒评，绝不给假分）。
        if (!OperatingSystem.IsWindows())
            return Simulate();
        return RealCollector.Read(this);
    }

    /// <summary>仅供无硬件环境（非 Windows）演示/测试的模拟数据，Source 明确标注，UI 不得当作真实体质。</summary>
    private HardwareFeatures Simulate()
    {
        const string model = "AMD Ryzen 7 7800X3D";
        return new HardwareFeatures(model, "Zen4", 1.046, 5050, GetRefFreq(model),
            GetFullLoadFreq(model), 62, null, null, "Simulated", EnvironmentOk: false);
    }

    private Baseline? Lookup(string model)
        => _baseline is null ? null : (_baseline.Get(model) ?? _baseline.GetByKeyword(model));

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { /* 进程退出时忽略 */ }
            _computer = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>查量表获取基础频率（仅用于展示，不参与满载判定）</summary>
    public double GetRefFreq(string model)
        => Lookup(model)?.ReferenceFreqMHz ?? 4000;

    /// <summary>
    /// ★ 查量表获取【全核 MaxBoost】—— 满载判定的正确基准。
    /// 修正前误用基础频率(ReferenceFreqMHz 4200)×0.95≈3990MHz，
    /// 轻载/打游戏都能越过，满载门禁形同虚设，会放行大量"假满载/假大雕"。
    /// </summary>
    public double GetFullLoadFreq(string model)
    {
        var b = Lookup(model);
        if (b is null) return 4000;
        // MaxBoost 缺失时退回基础频率（保守），但不至于像旧版那样错用基础频率当满载基准
        return b.MaxBoostMHz > 0 ? b.MaxBoostMHz : b.ReferenceFreqMHz;
    }

    /// <summary>
    /// 环境自检（操作系统兼容性）。
    /// 不再把「内存完整性 / 内核隔离(HVCI)」作为否决项。
    ///   LibreHardwareMonitor 0.9.6 使用的 PawnIO 驱动兼容 HVCI，真机与官方资料均证实：
    ///   开启内存完整性时 LHM 仍可正常读取传感器（CPU-Z / AIDA64 / OCCT 同理，均无需关闭安全功能）。
    ///   能否采集一律以「驱动是否成功打开、关键量是否真实读到」为准（RealCollector 读不到即返回 null）。
    ///   保留本方法签名以兼容诊断/测试；HVCI 状态改用 <see cref="IsMemoryIntegrityEnabled"/> 仅作信息展示。
    /// </summary>
    public static bool CheckEnvironment()
    {
        // PawnIO 兼容内核隔离；Windows 上不在环境层面预先否决（非 Windows 编译/测试环境返回 false）。
        return OperatingSystem.IsWindows();
    }

    /// <summary>内存完整性(HVCI)是否开启——仅用于诊断信息展示，不作为评分/启动否决项。</summary>
    public static bool IsMemoryIntegrityEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforceCodeIntegrity");
            return key?.GetValue("Enabled") is int enabled && enabled == 1;
        }
        catch { return false; }
    }

    /// <summary>管理员权限检测（读电压/功耗/MSR 必需）</summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>型号 → Zen 代际（单一来源，与评分引擎保持一致）</summary>
    public static string ZenMap(string model)
    {
        if (ContainsAny(model, "5800X3D", "5900X", "5950X", "5600X")) return "Zen3";
        if (ContainsAny(model, "7800X3D", "7950X", "7900X", "7700X")) return "Zen4";
        if (ContainsAny(model, "9950X", "9900X", "9700X", "9600X")) return "Zen5";
        // ★ 未知型号返回 "Unknown"，与评分引擎 ZenProfile 的全局中位默认(0.40)对齐；
        //   旧版错误地默认 Zen4(0.41)，会让未收录型号被悄悄按 Zen4 工艺评分。
        return "Unknown";
    }

    private static bool ContainsAny(string s, params string[] keys) =>
        keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>HWiNFO SHM 增强采集器（可选）。真正实现：读 HWiNFO 共享内存里的每核 VID。</summary>
public class HwinfoCollector : ICollector
{
    public bool Enabled { get; set; }

    /// <summary>最近一次是否成功取到 HWiNFO 数据（供 UI/诊断展示数据源状态）。</summary>
    public bool LastAvailable { get; private set; }

    public HardwareFeatures? Collect()
    {
        if (!Enabled) return null;
        try
        {
            if (!HwinfoShm.TryRead(out var readings, out string? status))
            {
                LastAvailable = false;
                Logger.Instance.Info("HWiNFO 增强不可用，回退 LHM", new { reason = status });
                return null;
            }

            double[]? vids = HwinfoShm.ParseCoreVids(readings);
            if (vids is null || vids.Length < 2)
            {
                LastAvailable = false;
                // 把 HWiNFO 实际的核心电压标签全部记录下来——真机标签命名若与预期不同，
                // 用户回传 logs 即可据此补充匹配规则（不靠猜）。
                var labels = HwinfoShm.DumpCoreLikeVoltages(readings)
                    .Select(x => new { x.Label, v = Math.Round(x.Value, 4) })
                    .Take(40).ToList();
                Logger.Instance.Warn("HWiNFO 已连接但未解析到每核 VID（≥2 核才算成功）", new { labels });
                return null;
            }

            LastAvailable = true;
            Logger.Instance.Info("HWiNFO 每核 VID 已获取", new
            {
                cores = vids.Length,
                min = Math.Round(vids.Min(), 4),
                max = Math.Round(vids.Max(), 4),
            });

            // 仅提供每核 VID；型号/整颗电压/频率/温度仍以 LHM 为准（见 CollectorOrchestrator 合并）。
            return new HardwareFeatures(
                Model: "", ZenGeneration: "",
                VCore: 0, FrequencyMHz: 0,
                ReferenceFreqMHz: 0, FullLoadFreqMHz: 0, TemperatureC: 0,
                PerCoreVIDs: vids, PerCoreClocks: null,
                Source: "HwinfoShm", EnvironmentOk: false);
        }
        catch (Exception ex)
        {
            LastAvailable = false;
            Logger.Instance.Warn("HWiNFO 共享内存读取异常，回退 LHM", new { msg = ex.Message });
            return null;
        }
    }
}

#endregion

#region ---- 编排器 + 质控 ----

/// <summary>采集编排器：双源合并 + 满载判定 + 质控门禁</summary>
public class CollectorOrchestrator
{
    private readonly LhmCollector _lhm;
    private readonly HwinfoCollector _hw;

    public CollectorOrchestrator(LhmCollector lhm, HwinfoCollector hw)
    {
        _lhm = lhm; _hw = hw;
    }

    private readonly string _baselineTag = ""; // 仅用于日志上下文标识

    public CollectorOrchestrator(LhmCollector lhm, HwinfoCollector hw, string baselineTag = "")
    {
        _lhm = lhm; _hw = hw;
        _baselineTag = baselineTag;
    }

    /// <summary>
    /// 把质控逻辑抽为可复用方法（供 UI 层对「已聚合的多轮采样」做校验）。
    /// 判定顺序与 Collect() 内一致：合理性 → 满载 → 电压/温度门禁。
    /// 返回 RejectReason = null 表示通过。
    /// </summary>
    public ValidationResults Validate(HardwareFeatures main)
    {
        // 1) 合理性校验（跨主板最后防线：只看数值区间，与命名无关）
        if (!RationalityCheck.Pass(main, out var rationalMsg))
        {
            Logger.Instance.Error("合理性校验未通过", context: new { reason = rationalMsg });
            return new ValidationResults(true, false, true, true, rationalMsg);
        }

        // 2) 满载判定（★ 基准 = 全核 MaxBoost，非基础频率）
        var fullLoadTarget = main.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold;
        if (main.FrequencyMHz < fullLoadTarget)
        {
            var msg = $"未达满载：当前 {main.FrequencyMHz:F0}MHz < MaxBoost " +
                      $"{main.FullLoadFreqMHz:F0}×{CollectorOptions.FullLoadThreshold:P0}" +
                      $"（门槛 {fullLoadTarget:F0}MHz）。请运行 OCCT/Prime95 等满载工具后再采集。";
            Logger.Instance.Error("未达满载，拒绝评分", context: new
            {
                current = main.FrequencyMHz, maxBoost = main.FullLoadFreqMHz, required = fullLoadTarget,
            });
            return new ValidationResults(true, true, false, true, msg);
        }

        // 3) 质控门禁（电压 / 温度区间）
        if (!QualityGate.Pass(main))
        {
            var msg = $"质控未通过：电压 {main.VCore:F3}V / 温度 {main.TemperatureC:F1}℃ 超出合理范围";
            Logger.Instance.Error("质控门禁未通过", context: new
            {
                vcore = main.VCore, temp = main.TemperatureC,
            });
            return new ValidationResults(true, true, true, false, msg);
        }

        return new ValidationResults(true, true, true, true, null);
    }

    public HardwareFeatures? Collect()
    {
        var log = Logger.Instance;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = new DiagnosticBuilder();

        // ★ 诊断：记录环境信息（HVCI 仅作信息记录，不再作为否决项——PawnIO 兼容内核隔离）
        builder.WithEnvironment(new EnvironmentInfo(
            IsAdministrator: LhmCollector.IsAdministrator(),
            CoreIsolationOff: !LhmCollector.IsMemoryIntegrityEnabled(), // 仅信息：内存完整性是否关闭
            OsDescription: RuntimeInformation.OSDescription,
            DotNetVersion: System.Environment.Version.ToString(),
            LhmAvailable: true));

        try
        {
            // 1) 环境自检：管理员权限（HVCI 不再否决，驱动能否读取以实测为准）
            if (!LhmCollector.IsAdministrator())
            {
                log.Error("未以管理员身份运行");
                builder.WithValidation(new ValidationResults(
                    EnvironmentOk: false, RationalityOk: false, FullLoadOk: false, QualityGateOk: false,
                    RejectReason: "未以管理员身份运行"));
                throw new InvalidOperationException("需以管理员身份运行。");
            }

            // 2) 主采集（LHM）
            log.Info("开始主采集 (LHM)", new { model = _baselineTag });
            var main = _lhm.Collect();
            if (main is null)
            {
                log.Warn("LHM 采集返回空（未启用或传感器不可用）");
                builder.AddError("LHM 采集返回空");
                builder.WithValidation(new ValidationResults(
                    EnvironmentOk: true, RationalityOk: false, FullLoadOk: false, QualityGateOk: false,
                    RejectReason: "LHM 未返回采集数据"));
                return null;
            }

            // ★ dump 原始传感器到诊断报告（排查命名问题的关键）
            //   LastDump 由 LhmCollector 在 Collect() 内填充
            builder.AddSensors(_lhm.LastDump);

            log.Info("主采集完成", new
            {
                model = main.Model,
                zen = main.ZenGeneration,
                vcore = main.VCore,
                freq = main.FrequencyMHz,
                temp = main.TemperatureC,
            });

            // 3) 增强采集（HWiNFO，手动启用时叠加每核 VID 等）
            if (_hw.Enabled)
            {
                var enh = _hw.Collect();
                if (enh is not null)
                {
                    main = main with
                    {
                        PerCoreVIDs = enh.PerCoreVIDs ?? main.PerCoreVIDs,
                        PerCoreClocks = enh.PerCoreClocks ?? main.PerCoreClocks,
                        Source = "DualSource"
                    };
                    log.Info("HWiNFO 增强已叠加 (DualSource)", new
                    {
                        perCore = main.PerCoreVIDs?.Length,
                        perCoreClocks = main.PerCoreClocks?.Length,
                    });
                }
                else
                {
                    main = main with { Source = "LhmOnly(Fallback)" };
                    log.Warn("HWiNFO 启用但采集失败，降级为 LhmOnly(Fallback)");
                }
            }

            // 4) 合理性校验 —— 跨主板无条件生效的最后防线（见《跨主板兼容设计.md》）
            //    ★ 不依赖命名，只看数值区间；即使匹配错也能拦住假大雕
            if (!RationalityCheck.Pass(main, out var rationalMsg))
            {
                log.Error("合理性校验未通过", context: new { reason = rationalMsg });
                builder.WithValidation(new ValidationResults(
                    EnvironmentOk: true, RationalityOk: false, FullLoadOk: true, QualityGateOk: true,
                    RejectReason: rationalMsg));
                throw new InvalidOperationException($"数据不合理：{rationalMsg}");
            }

            // 5) 满载判定 —— 防"假大雕"第二道闸门
            //    ★ 基准必须用【全核 MaxBoost】而非基础频率修正）
            var fullLoadTarget = main.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold;
            if (main.FrequencyMHz < fullLoadTarget)
            {
                var fullLoadMsg = $"未达满载：当前 {main.FrequencyMHz:F0}MHz < MaxBoost {main.FullLoadFreqMHz:F0}×{CollectorOptions.FullLoadThreshold:P0}（门槛 {fullLoadTarget:F0}MHz）";
                log.Error("未达满载，拒绝评分", context: new
                {
                    current = main.FrequencyMHz,
                    maxBoost = main.FullLoadFreqMHz,
                    required = fullLoadTarget,
                });
                builder.WithValidation(new ValidationResults(
                    EnvironmentOk: true, RationalityOk: true, FullLoadOk: false, QualityGateOk: true,
                    RejectReason: fullLoadMsg));
                throw new InvalidOperationException(
                    fullLoadMsg + "。请运行 OCCT/Prime95 等满载工具后再采集。");
            }

            // 6) 质控门禁
            if (!QualityGate.Pass(main))
            {
                var qcMsg = $"质控未通过：电压 {main.VCore:F3}V / 温度 {main.TemperatureC:F1}℃ 超出合理范围";
                log.Error("质控门禁未通过（电压/温度异常）", context: new
                {
                    vcore = main.VCore, temp = main.TemperatureC,
                });
                builder.WithValidation(new ValidationResults(
                    EnvironmentOk: true, RationalityOk: true, FullLoadOk: true, QualityGateOk: false,
                    RejectReason: qcMsg));
                throw new InvalidOperationException(qcMsg);
            }

            log.Info("采集流程全部通过", new { durationMs = sw.ElapsedMilliseconds });

            // ★ 诊断报告：全部校验通过（评分由上层 ScoreEngine 填充，此处仅记录采集侧）
            builder.WithValidation(new ValidationResults(
                EnvironmentOk: true, RationalityOk: true, FullLoadOk: true, QualityGateOk: true,
                RejectReason: null));

            return main with { EnvironmentOk = true };
        }
        catch (InvalidOperationException)
        {
            // 业务拒绝（环境/满载/质控）已在各步骤记录，此处直接透传
            throw;
        }
        catch (Exception ex)
        {
            // ★ 兜底：未预期异常也记录到诊断报告，不让排查漏掉
            builder.WithValidation(new ValidationResults(
                EnvironmentOk: true, RationalityOk: false, FullLoadOk: false, QualityGateOk: false,
                RejectReason: $"未预期异常：{ex.GetType().Name} - {ex.Message}"));
            log.Error("采集流程异常", ex, context: new { stage = "orchestrate" });
            throw;
        }
        finally
        {
            // ★ 无论成功失败，都落盘一份诊断报告（Q2=A：自动生成）
            //   这样出问题时"直接拿报告"即可复现，无需用户再操作
            try
            {
                var report = builder.Build();
                DiagnosticStore.Save(report);
                DiagnosticStore.RetainLatest(); // 保留最近 10 份
            }
            catch (Exception ex)
            {
                log.Warn("诊断报告落盘失败（不影响主流程）", new { error = ex.Message });
            }
        }
    }
}

/// <summary>采集配置（可由 JSON 配置覆盖）</summary>
public static class CollectorOptions
{
    /// <summary>
    /// 满载判定阈值：频率 ≥ 【全核 MaxBoost】×该比例 才视为满载。
    ///
    /// ★ 0.90 的真机依据：7800X3D MaxBoost=5000MHz
    ///   0.95 → 门槛 4750MHz，但 OCCT 重负载下受功耗/电流约束，
    ///   全核稳态仅约 4526MHz（8核100% / 89W / 63℃），被误判"未达满载"；
    ///   0.90 → 门槛 4500MHz，可容纳 Prime95 AVX-512 等重指令集/撞功耗墙
    ///   （可低至 4.3GHz 量级），又不至于放行轻载。
    /// ★ 该阈值只是"是否允许进入评分"的质控门槛，不参与分数计算；
    ///   不同负载造成的工作点差异，由评分引擎的 V/F 折算统一处理。
    /// </summary>
    public static double FullLoadThreshold { get; set; } = 0.90;

    /// <summary>稳态采样次数（消除瞬时抖动，取均值）</summary>
    public static int StableSampleCount { get; set; } = 5;

    // —— 多轮采样聚合采样窗口由 ~0.75s 加长到 ~10s，避免短窗口失真）——
    /// <summary>
    /// 采样总时长（毫秒）。用户反馈原 5×150ms≈0.75s 太短、易受瞬时波动影响，
    /// 改为约 10s 稳态窗口；实际轮数 = SampleDurationMs / SampleIntervalMs。
    /// </summary>
    public static int SampleDurationMs { get; set; } = 10_000;
    /// <summary>每轮采样间隔毫秒：500ms × 20 轮 = 10s。</summary>
    public static int SampleIntervalMs { get; set; } = 500;
    /// <summary>采样轮数（由时长/间隔推导；保留属性以便测试与外部覆盖）。</summary>
    public static int SampleRounds
    {
        get => _sampleRounds > 0 ? _sampleRounds : Math.Max(1, SampleDurationMs / Math.Max(1, SampleIntervalMs));
        set => _sampleRounds = value;
    }
    private static int _sampleRounds;

    // —— 满载稳定确认防止“刚满载就采样”，要求持续满载一段时间再自动触发）——
    /// <summary>进入自动采样前，需要持续保持满载的秒数。</summary>
    public static int FullLoadHoldSeconds { get; set; } = 60;
    /// <summary>稳定窗口内允许的满载判定占比（&lt;1 可容忍极个别掉帧）；低于它视为未稳定、计时回退。</summary>
    public static double FullLoadHoldRatio { get; set; } = 0.90;

    // —— 日志 / 诊断配置决策 A/A）——
    /// <summary>日志最低级别：Verbose=全量 / Info=流程 / Warn=降级 / Error=失败</summary>
    public static LogLevel LogMinLevel { get; set; } = LogLevel.Info;
    /// <summary>诊断报告保留份数（Q2=A：自动生成 + 保留最近 N 份）</summary>
    public static int DiagnosticRetainCount { get; set; } = DiagnosticStore.RetainCount;
}

/// <summary>数据质控规则</summary>
public static class QualityGate
{
    public static double VCoreMin { get; set; } = 0.2;
    public static double VCoreMax { get; set; } = 1.5;
    public static double TempMin { get; set; } = 0;
    public static double TempMax { get; set; } = 100;

    public static bool Pass(HardwareFeatures f)
    {
        if (f.VCore is < 0.2 or > 1.5) return false;      // 电压有效性
        if (f.TemperatureC is < 0 or > 100) return false;  // 温度窗口
        // TODO: 同步性 ≤16ms、稳态 ≥30s（需采集时长统计，后续版本补充）
        return true;
    }
}

/// <summary>
/// 合理性校验 —— 跨主板无条件生效的最后防线（见《LHM跨主板兼容设计.md》§三 ②）
///
/// ★ 设计意图：
///   弹性匹配（①）依赖命名，命名错了可能"静默采错"；而本校验只看【数值区间】，
///   与传感器叫什么名字完全无关。因此即使①匹配错，离谱值照样被拒 ——
///   "命名错配"的危害从"评出假大雕"降级为"字段缺失 → 拒评"。
///
///   这是"宁可拒评、绝不给假分"原则在采集层的落地。
/// </summary>
public static class RationalityCheck
{
    // —— 合理性区间（与型号无关，覆盖全代际物理极限）——
    public static double VCoreAbsMin { get; set; } = 0.20;  // 低于此值视为电压归零（核心隔离/断线）
    public static double VCoreAbsMax { get; set; } = 1.60;  // 桌面 Zen 满载 VCore 物理上限
    public static double TempAbsMin  { get; set; } = 0;
    public static double TempAbsMax  { get; set; } = 105;   // Zen4/5 结温上限 95-105℃
    public static double AvgFreqAbsMin { get; set; } = 500;  // MHz，明显低于此即异常
    public static double AvgFreqAbsMax { get; set; } = 7000; // MHz，明显高此即异常

    public static bool Pass(HardwareFeatures f, out string? reason)
    {
        reason = null;
        if (!Between(f.VCore, VCoreAbsMin, VCoreAbsMax))
            { reason = $"电压越界 {f.VCore:F3}V (允许 {VCoreAbsMin}-{VCoreAbsMax}V)"; return false; }
        if (!Between(f.TemperatureC, TempAbsMin, TempAbsMax))
            { reason = $"温度越界 {f.TemperatureC:F1}℃ (允许 {TempAbsMin}-{TempAbsMax}℃)"; return false; }
        if (!Between(f.FrequencyMHz, AvgFreqAbsMin, AvgFreqAbsMax))
            { reason = $"频率越界 {f.FrequencyMHz:F0}MHz (允许 {AvgFreqAbsMin}-{AvgFreqAbsMax}MHz)"; return false; }
        return true;
    }

    private static bool Between(double v, double lo, double hi) => v >= lo && v <= hi;
}

#endregion

#region ---- 量表存储（JSON 配置，可热更新） ----

/// <summary>量表数据源接口</summary>
public interface IBaselineStore
{
    Baseline? Get(string model);      // 精确匹配
    Baseline? GetByKeyword(string model); // 模糊匹配（含型号关键字）
}

/// <summary>JSON 文件量表（内置基准量表.json，可由 数据库设计 的导出生成）</summary>
public class JsonBaselineStore : IBaselineStore
{
    private readonly Dictionary<string, Baseline> _exact;
    private readonly List<Baseline> _all;

    public JsonBaselineStore(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        _all = JsonSerializer.Deserialize<List<Baseline>>(json) ?? new();
        _exact = _all.ToDictionary(b => b.Model, StringComparer.OrdinalIgnoreCase);
    }

    public Baseline? Get(string model)
    {
        _exact.TryGetValue(model, out var b);
        return b;
    }

    public Baseline? GetByKeyword(string model)
    {
        // 按型号关键字匹配（如 "Ryzen 7 7800X3D" → 命中 "7800X3D"）
        return _all.FirstOrDefault(b =>
            model.Contains(b.Model, StringComparison.OrdinalIgnoreCase) ||
            b.Model.Contains(model, StringComparison.OrdinalIgnoreCase));
    }
}

#endregion
