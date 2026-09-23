// HwinfoShm.cs — HWiNFO 共享内存（Shared Memory）读取器
//
// 为什么需要它：
//   LibreHardwareMonitor 0.9.x（含当前 master）对每核 VID 的实现，是读 MSR 0xC0010293 的
//   CurCpuVid[21:14]，再用 Zen1~Zen3 的 SVI2 公式  Vcc = 1.550 - 0.00625*code  解码；
//   但 Zen4/Zen5 已改用 SVI3 供电编码，字段含义/步进都变了，LHM 仍套 SVI2 公式，
//   于是 Zen4 满载时 8 个核都被解到 ~0.43V 的最低档占位值（真机 dump 已证实），无法反映核间体质。
//   HWiNFO 用自己的内核驱动正确解码了 SVI3 的每核请求 VID（"Core VIDs"）。
//   本读取器通过 HWiNFO 官方公开的【共享内存协议】只读地拿到这些读数——纯托管 MemoryMappedFile，
//   不装任何驱动、不改硬件、不要求关闭内核隔离。
//
// 前置条件（用户侧）：
//   1) 安装并运行 HWiNFO（传感器窗口在后台即可）；
//   2) HWiNFO 设置 →「通用/安全」里勾选 “Shared Memory Support（共享内存支持）”。
//   不满足时 TryRead 返回 false，主流程自动回退 LHM（每核分布按规则判无效，主分不受影响）。
//
// 协议布局（pack=1, little-endian），与公开实现 hwinfo-go / HWiNFO Shared Memory Viewer 一致：
//   Header(前 48B)：Status[4]="HWiS"激活 / "DAED"过期；Version u32；Revision u32；LastUpdate 8B；
//                   SensorSectionOff/Size/Count u32；ReadingSectionOff/Size/Count u32；PollingPeriod u32。
//   Reading 元素（大小由 Header.ReadingSize 给出，跨版本兼容，勿写死）：
//                   Type u32 @0（None0/Temp1/Volt2/Fan3/Current4/Power5/Clock6/Usage7/Other8）；
//                   SensorIndex u32 @4；Id u32 @8；
//                   OrigLabel[128] @12；UserLabel[128] @140；Unit[16] @268；
//                   ValueCurrent f64 @284；Min/Max/Avg f64 紧随；v2 尾部追加 UTF-8 串，不影响前部偏移。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Collector;

/// <summary>HWiNFO 共享内存只读访问 + 每核 VID 解析（解析部分为纯逻辑，可单测）。</summary>
public static class HwinfoShm
{
    public sealed record Reading(int Type, int SensorIndex, int Id, string OrigLabel, string UserLabel, string Unit, double Value);

    // 读数类型枚举（HWiNFO 共享内存 SENSOR_READING_TYPE，从 0 连续）
    public const int TypeNone = 0, TypeTemp = 1, TypeVolt = 2, TypeFan = 3, TypeCurrent = 4,
        TypePower = 5, TypeClock = 6, TypeUsage = 7, TypeOther = 8;

    // —— Header 偏移 ——
    private const int OffStatus = 0;          // 4 字节 ASCII
    private const int OffReadingOff = 32;
    private const int OffReadingSize = 36;
    private const int OffReadingCount = 40;

    // —— Reading 元素内偏移 ——
    private const int RType = 0;
    private const int RSensorIndex = 4;
    private const int RId = 8;
    private const int RLabelOrig = 12;
    private const int RLabelUser = 140;
    private const int RUnit = 268;
    private const int RValue = 284;
    private const int LabelLen = 128;
    private const int UnitLen = 16;

    private static readonly string[] MapNames =
    {
        // HWiNFO 2021 年起公开的共享内存接口固定名为 SM2（真机 HWiNFO v8.x 即此名）。
        // 旧的 SF / SF1..SF8 是更早的私有格式、内存布局不同，不能混用。
        @"Global\HWiNFO_SENS_SM2",   // 官方默认（全局命名空间，只读打开通常无需管理员）
        @"HWiNFO_SENS_SM2",          // 个别以非提权运行时落在当前会话命名空间的兜底
    };

    private static readonly string[] MutexNames =
    {
        @"Global\HWiNFO_SM2_MUTEX",
        @"HWiNFO_SM2_MUTEX",
    };

    /// <summary>
    /// 打开 HWiNFO 传感器共享内存并读出全部读数。
    /// 返回 false 时 <paramref name="status"/> 说明原因（未运行 / 未开共享内存 / 已过期）。
    /// </summary>
    public static bool TryRead(out List<Reading> readings, out string status)
    {
        readings = new List<Reading>();
        status = "";
        bool openedAny = false;

        foreach (var name in MapNames)
        {
            byte[]? buf;
            try
            {
                buf = Snapshot(name);
                openedAny = true;
            }
            catch (FileNotFoundException)
            {
                continue; // 该命名空间下不存在，尝试下一个候选名
            }
            catch (Exception ex)
            {
                status = $"打开 HWiNFO 共享内存失败：{ex.GetType().Name} {ex.Message}";
                continue;
            }

            if (buf is null || buf.Length < OffReadingCount + 4) continue;

            string sig = Encoding.ASCII.GetString(buf, OffStatus, 4);
            if (sig != "HWiS")
            {
                // "DAED" 表示共享内存超过 HWiNFO 非专业版 12 小时有效期等，重启 HWiNFO 即可
                status = $"HWiNFO 共享内存未激活（标记={sig}），请重启 HWiNFO 后重试";
                continue;
            }

            int readOff = BitConverter.ToInt32(buf, OffReadingOff);
            int readSize = BitConverter.ToInt32(buf, OffReadingSize);
            int readCount = BitConverter.ToInt32(buf, OffReadingCount);

            if (readOff <= 0 || readSize < RValue + 8 || readCount <= 0 || readCount > 100_000)
            {
                status = "HWiNFO 共享内存结构异常（表头偏移非法）";
                continue;
            }

            var list = new List<Reading>(readCount);
            for (int i = 0; i < readCount; i++)
            {
                int b = readOff + i * readSize;
                if (b + RValue + 8 > buf.Length) break;

                int type = BitConverter.ToInt32(buf, b + RType);
                int sidx = BitConverter.ToInt32(buf, b + RSensorIndex);
                int id = BitConverter.ToInt32(buf, b + RId);
                string orig = ReadZString(buf, b + RLabelOrig, LabelLen);
                string user = ReadZString(buf, b + RLabelUser, LabelLen);
                string unit = ReadZString(buf, b + RUnit, UnitLen);
                double val = BitConverter.ToDouble(buf, b + RValue);
                if (double.IsNaN(val) || double.IsInfinity(val)) continue;
                list.Add(new Reading(type, sidx, id, orig, user, unit, val));
            }

            if (list.Count > 0)
            {
                readings = list;
                status = "ok";
                return true;
            }
        }

        // 所有候选名都没打开 → 大概率是 HWiNFO 未运行或未开启共享内存（v7 起默认关闭）
        if (!openedAny) status = NotFoundHint();
        else if (string.IsNullOrEmpty(status)) status = "HWiNFO 共享内存已打开但没有任何读数";
        return false;
    }

    private const string SharedMemoryHowTo =
        "请：① 运行 HWiNFO 并打开传感器窗口；② 右键任务栏托盘 HWiNFO 图标→设置(Settings)→" +
        "“通用/用户界面(General/User Interface)”标签右列，勾选“共享内存支持(Shared Memory Support)”" +
        "（HWiNFO v7 起默认关闭，注意不是传感器窗口里的设置），确定后重启 HWiNFO；" +
        "③ 免费版共享内存每运行 12 小时会失效，重启 HWiNFO 即可恢复。";

    /// <summary>连不上时，best-effort 读注册表开关状态给出针对性提示；便携版写 INI、读不到则给通用指引。</summary>
    private static string NotFoundHint()
    {
        try
        {
            foreach (var root in new[] { "HWiNFO64", "HWiNFO" })
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\{root}\Sensors");
                if (key?.GetValue("SensorsSM") is int v)
                {
                    return v == 1
                        ? "已检测到 HWiNFO“共享内存支持”为开启，但仍未找到共享内存：请确认 HWiNFO 正在运行、"
                          + "传感器窗口已打开；若刚勾选请重启 HWiNFO；免费版每 12 小时需重启一次。"
                        : $"检测到 HWiNFO“共享内存支持”当前为关闭（注册表 SensorsSM={v}）。" + SharedMemoryHowTo;
                }
            }
        }
        catch (Exception) { /* 便携版写 HWiNFO64.INI 或非 Windows：退回通用提示 */ }
        return "未找到 HWiNFO 共享内存。" + SharedMemoryHowTo;
    }

    /// <summary>在 HWiNFO 官方互斥体保护下对共享内存做一次只读快照，避免读到更新中的半截数据。</summary>
    private static byte[]? Snapshot(string mapName)
    {
        using var mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);

        System.Threading.Mutex? mux = null;
        bool held = false;
        foreach (var mn in MutexNames)
        {
            try { mux = System.Threading.Mutex.OpenExisting(mn); break; }
            catch (Exception) { /* 互斥体不可用则无锁读取（torn read 概率极低），不因此失败 */ }
        }

        try
        {
            if (mux != null)
            {
                try { held = mux.WaitOne(250); }
                catch (AbandonedMutexException) { held = true; } // HWiNFO 异常退出遗留，仍可进入
                catch (Exception) { held = false; }
            }

            using var view = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            using var ms = new MemoryStream();
            view.CopyTo(ms);
            return ms.ToArray();
        }
        finally
        {
            if (held && mux != null) { try { mux.ReleaseMutex(); } catch (Exception) { } }
            mux?.Dispose();
        }
    }

    private static string ReadZString(byte[] buf, int offset, int max)
    {
        if (offset + max > buf.Length) max = buf.Length - offset;
        if (max <= 0) return "";
        int n = Array.IndexOf(buf, (byte)0, offset, max);
        int len = n < 0 ? max : n - offset;
        // HWiNFO 的原始标签为英文 ASCII；个别高位字符按系统 ANSI 也不影响核号/VID 关键字匹配。
        return Encoding.ASCII.GetString(buf, offset, len).Trim();
    }

    // —— 每核 VID 解析（纯逻辑）——
    // 形如 "Core #1 VID (SVI3N)"、"Core 1 VID"、"VID Core #2" 等；核号 1 位以上。
    private static readonly Regex CoreVidRe = new(
        @"(?:core\s*#?\s*0*(\d+)[^\r\n]{0,24}vid)|(?:vid[^\r\n]{0,24}core\s*#?\s*0*(\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 这些词出现说明不是“每核”电压：整轨/SoC/有效汇总/其它电源轨。
    private static readonly string[] ExcludeWords =
    {
        "soc", "misc", "ldo", "smu", "effective", "vddio", "vddp", "vdd25", "vdd18",
        "vdd mem", "vddg", "vddcr soc", "1.8", "mem", "svi3 tfn", "svi2 tfn",
    };

    /// <summary>
    /// 从 HWiNFO 电压读数里解析每核请求 VID，按核号升序返回；无法可靠解析（&lt;2 个核）返回 null。
    /// 核号一律以【原始英文标签】为准（用户自定义改名可能去掉核号）。
    /// </summary>
    public static double[]? ParseCoreVids(IEnumerable<Reading> readings)
    {
        var byCore = new SortedDictionary<int, double>();

        foreach (var r in readings)
        {
            if (r.Type != TypeVolt) continue;
            string label = r.OrigLabel ?? "";
            if (string.IsNullOrWhiteSpace(label)) continue;
            string low = label.ToLowerInvariant();
            if (ExcludeWords.Any(w => low.Contains(w))) continue;

            var m = CoreVidRe.Match(low);
            if (!m.Success) continue;

            string num = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!int.TryParse(num, out int core) || core < 0) continue;

            // 同一核多条（极少见）取较大值，与 LHM 多轮取峰口径一致
            if (!byCore.TryGetValue(core, out var old) || r.Value > old)
                byCore[core] = r.Value;
        }

        return byCore.Count >= 2 ? byCore.Values.ToArray() : null;
    }

    /// <summary>取出所有“看起来像核心电压/VID”的电压标签与数值，供日志诊断标签命名差异。</summary>
    public static List<(string Label, double Value)> DumpCoreLikeVoltages(IEnumerable<Reading> readings)
        => readings
            .Where(r => r.Type == TypeVolt)
            .Where(r => { var l = (r.OrigLabel ?? "").ToLowerInvariant();
                          return l.Contains("core") || l.Contains("vid") || l.Contains("vddcr"); })
            .Select(r => (string.IsNullOrWhiteSpace(r.OrigLabel) ? r.UserLabel : r.OrigLabel, r.Value))
            .ToList();
}
