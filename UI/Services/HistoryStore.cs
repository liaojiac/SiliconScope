using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Collector;
using SiliconScope.UI.Models;

namespace SiliconScope.UI.Services;

/// <summary>
/// 本机历史成绩存储：评测完成后把结果以 JSON 落到程序目录 history/，
/// 完全本地、不上传；列表查看 / 重新打开详情 / 删除 / 打开目录。
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly int _retain;

    public HistoryStore(int retain = 100)
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "history");
        _retain = retain is > 1 and <= 1000 ? retain : 100;
    }

    public string DirectoryPath => _dir;

    /// <summary>保存一次评测结果；回填 Id/时间，返回生成的条目。任何 IO 失败都不抛出（不阻断主流程）。</summary>
    public HistoryEntry? Save(EvaluationResult r)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            if (string.IsNullOrEmpty(r.Id))
                r.Id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (string.IsNullOrEmpty(r.AppVersion))
                r.AppVersion = AppInfo.Version;

            var entry = ToEntry(r);
            string file = Path.Combine(_dir, $"score_{entry.Id}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(entry, JsonOpts), Encoding.UTF8);
            Prune();
            return entry;
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("历史成绩保存失败", ex);
            return null;
        }
    }

    /// <summary>读取全部历史，按时间倒序（最新在前）。</summary>
    public List<HistoryEntry> LoadAll()
    {
        var list = new List<HistoryEntry>();
        try
        {
            if (!Directory.Exists(_dir)) return list;
            foreach (var file in Directory.EnumerateFiles(_dir, "score_*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(file, Encoding.UTF8));
                    if (e is not null) list.Add(e);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Warn("历史记录读取失败，已跳过", new { file, msg = ex.Message });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("历史成绩目录读取失败", ex);
        }
        return list.OrderByDescending(e => e.Time).ThenByDescending(e => e.Id).ToList();
    }

    public void Delete(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            string safe = Path.GetFileName($"score_{id}.json");
            string file = Path.Combine(_dir, safe);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("历史记录删除失败", new { id, msg = ex.Message });
        }
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            System.Diagnostics.Process.Start("explorer.exe", _dir);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("打开历史目录失败", ex);
        }
    }

    private void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(_dir, "score_*.json")
                .OrderByDescending(f => f).ToList();   // 文件名含时间戳，字典序即时间序
            foreach (var old in files.Skip(_retain))
            {
                try { File.Delete(old); } catch { /* 忽略单个删除失败 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("历史记录清理失败", new { msg = ex.Message });
        }
    }

    public static HistoryEntry ToEntry(EvaluationResult r) => new()
    {
        Id = r.Id,
        AppVersion = r.AppVersion,
        Time = r.Time,
        Model = r.Model,
        Zen = r.Zen,
        VCore = r.VCore,
        FreqMHz = r.FreqMHz,
        ReferenceFreqMHz = r.ReferenceFreqMHz,
        FullLoadFreqMHz = r.FullLoadFreqMHz,
        TempC = r.TempC,
        DataSource = r.DataSource,
        SpScore = r.SpScore,
        Percentile = r.Percentile,
        Grade = r.Grade,
        GradeColor = r.GradeColor,
        Mode = r.Mode,
        Confidence = r.Confidence,
        Note = r.Note,
        VAnchor = r.VAnchor,
        AnchorFreqMHz = r.AnchorFreqMHz,
        SlopeMvPerMhz = r.SlopeMvPerMhz,
        VRef = r.VRef,
        SlopeSource = r.SlopeSource,
        AnchorSource = r.AnchorSource,
        CalibrationLevel = r.CalibrationLevel,
        PerCoreVIDs = r.PerCoreVIDs,
        BestCoreIndex = r.BestCoreIndex,
        WorstCoreIndex = r.WorstCoreIndex,
        VfPoints = r.VfPoints.Select(p => new[] { p.freq, p.volt }).ToArray(),
        EnvChecks = r.EnvironmentChecks.Select(c => new EnvCheckDto(c.name, c.ok, c.detail)).ToList(),
    };

    public static EvaluationResult ToResult(HistoryEntry e) => new()
    {
        Id = e.Id,
        AppVersion = e.AppVersion,
        Time = e.Time,
        Model = e.Model,
        Zen = e.Zen,
        VCore = e.VCore,
        FreqMHz = e.FreqMHz,
        ReferenceFreqMHz = e.ReferenceFreqMHz,
        FullLoadFreqMHz = e.FullLoadFreqMHz,
        TempC = e.TempC,
        DataSource = string.IsNullOrEmpty(e.DataSource) ? "LHM" : e.DataSource,
        SpScore = e.SpScore,
        Percentile = e.Percentile,
        Grade = e.Grade,
        GradeColor = e.GradeColor,
        Mode = e.Mode,
        Confidence = e.Confidence,
        Note = e.Note,
        VAnchor = e.VAnchor,
        AnchorFreqMHz = e.AnchorFreqMHz,
        SlopeMvPerMhz = e.SlopeMvPerMhz,
        VRef = e.VRef,
        SlopeSource = e.SlopeSource,
        AnchorSource = e.AnchorSource,
        CalibrationLevel = string.IsNullOrEmpty(e.CalibrationLevel) ? "Estimated" : e.CalibrationLevel,
        PerCoreVIDs = e.PerCoreVIDs,
        BestCoreIndex = e.BestCoreIndex,
        WorstCoreIndex = e.WorstCoreIndex,
        VfPoints = e.VfPoints?.Where(p => p.Length >= 2)
            .Select(p => (p[0], p[1])).ToList() ?? new(),
        EnvironmentChecks = e.EnvChecks?
            .Select(c => (c.Name, c.Ok, c.Detail)).ToList() ?? new(),
    };
}
