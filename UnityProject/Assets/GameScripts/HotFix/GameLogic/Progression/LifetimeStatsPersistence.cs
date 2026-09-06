using System;
using System.IO;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>stage-outcome-lifetime-stats story-001：生涯统计存档的磁盘布局（v1）。
    /// 全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>；只存 4 项聚合数值，
    /// 不复制任何单局明细（单局明细仍由 <c>StageStatistics</c> 承载，下一局照常清零）。</summary>
    [Serializable]
    public sealed class LifetimeStatsSaveData
    {
        public int Version = LifetimeStatsPersistence.CurrentVersion;
        /// <summary>历史累计击杀数（求和）。</summary>
        public int TotalKills;
        /// <summary>历史累计吞噬数（求和）。</summary>
        public int TotalDevoured;
        /// <summary>历史最长存活秒数（取最大值，不是求和）。</summary>
        public float BestDurationSeconds;
        /// <summary>历史最高等级（取最大值，不是求和）。</summary>
        public int BestLevel;
    }

    /// <summary>stage-outcome-lifetime-stats story-001：一次 Load/RecordRun 得到的生涯统计快照。
    /// 值类型 —— <c>default</c> 即全零，所以 <c>StageOutcome</c> 上的字段"从未被赋值"时
    /// 结算文案照样能显示 0/00:00，不会 NRE。</summary>
    public readonly struct LifetimeStats
    {
        /// <summary>累计击杀数（跨局求和）。</summary>
        public readonly int TotalKills;
        /// <summary>累计吞噬数（跨局求和）。</summary>
        public readonly int TotalDevoured;
        /// <summary>历史最长存活秒数（跨局取最大值）。</summary>
        public readonly float BestDurationSeconds;
        /// <summary>历史最高等级（跨局取最大值）。</summary>
        public readonly int BestLevel;

        public LifetimeStats(int totalKills, int totalDevoured, float bestDurationSeconds, int bestLevel)
        {
            TotalKills = totalKills;
            TotalDevoured = totalDevoured;
            BestDurationSeconds = bestDurationSeconds;
            BestLevel = bestLevel;
        }

        public static LifetimeStats Empty()
        {
            return new LifetimeStats(0, 0, 0f, 0);
        }
    }

    /// <summary>
    /// stage-outcome-lifetime-stats story-001：生涯统计（累计击杀 / 累计吞噬 / 最长存活 / 最高等级）
    /// 的跨局持久化 IO。独立 JSON 文件（<see cref="Application.persistentDataPath"/>/<c>lifetime_stats.json</c>），
    /// 只在离开细胞阶段产出 <c>StageOutcome</c> 时由 <see cref="RecordRun"/> 批量整份覆盖写入一次。
    ///
    /// 聚合方式**两种不能混**：击杀/吞噬是跨局**求和**，存活时长/等级是跨局**取最大值**。
    ///
    /// Reject-to-Safe：<see cref="Load"/>/<see cref="Save"/> 永不 throw——文件缺失/损坏/磁盘异常
    /// 一律降级为"全零快照 + 一条 warning 日志"，绝不能因为存档问题挡住退出结算流程。
    /// 只做 v1，不做版本迁移框架（未知 Version 直接按损坏处理，回落全零）。
    /// </summary>
    public static class LifetimeStatsPersistence
    {
        public const int CurrentVersion = 1;
        private const string FileName = "lifetime_stats.json";

        /// <summary>存档绝对路径。测试/验收可直接读写这个文件。</summary>
        public static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, FileName); }
        }

        /// <summary>读取历史生涯统计。文件不存在 → 全零（首次运行，静默）；
        /// 内容损坏/版本未知 → warning 日志 + 全零。</summary>
        public static LifetimeStats Load()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return LifetimeStats.Empty();
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[LifetimeStatsPersistence] 读取存档路径失败，按空生涯统计继续：{e.Message}");
                return LifetimeStats.Empty();
            }

            LifetimeStatsSaveData data;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    TEngine.Log.Warning("[LifetimeStatsPersistence] 存档为空文件，按空生涯统计继续。");
                    return LifetimeStats.Empty();
                }

                data = JsonUtility.FromJson<LifetimeStatsSaveData>(json);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[LifetimeStatsPersistence] 存档损坏或无法读取，按空生涯统计继续：{e.Message}");
                return LifetimeStats.Empty();
            }

            if (data == null)
            {
                TEngine.Log.Warning("[LifetimeStatsPersistence] 存档反序列化为 null，按空生涯统计继续。");
                return LifetimeStats.Empty();
            }

            if (data.Version != CurrentVersion)
            {
                TEngine.Log.Warning(
                    $"[LifetimeStatsPersistence] 存档版本 {data.Version} 不是当前版本 {CurrentVersion}（本期不做迁移），按空生涯统计继续。");
                return LifetimeStats.Empty();
            }

            return new LifetimeStats(data.TotalKills, data.TotalDevoured, data.BestDurationSeconds, data.BestLevel);
        }

        /// <summary>整份覆盖写入（不是追加）。磁盘异常只记日志，不抛出。</summary>
        public static void Save(LifetimeStats stats)
        {
            try
            {
                LifetimeStatsSaveData data = new LifetimeStatsSaveData
                {
                    Version = CurrentVersion,
                    TotalKills = stats.TotalKills,
                    TotalDevoured = stats.TotalDevoured,
                    BestDurationSeconds = stats.BestDurationSeconds,
                    BestLevel = stats.BestLevel,
                };
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[LifetimeStatsPersistence] 生涯统计写入失败（本局记录将丢失）：{e.Message}");
            }
        }

        /// <summary>
        /// 退出细胞阶段时的唯一持久化入口：读历史 → 按各自聚合方式并入本局 → 整份覆盖写回，
        /// 并返回**含本局**的最终快照供结算文案直接读（调用方不必再做一次文件 IO）。
        ///
        /// 击杀 / 吞噬 = 求和；存活时长 / 等级 = 取最大值。
        /// </summary>
        public static LifetimeStats RecordRun(int kills, int devoured, float durationSeconds, int level)
        {
            LifetimeStats previous = Load();
            LifetimeStats merged = new LifetimeStats(
                previous.TotalKills + kills,
                previous.TotalDevoured + devoured,
                Mathf.Max(previous.BestDurationSeconds, durationSeconds),
                Mathf.Max(previous.BestLevel, level));
            Save(merged);
            return merged;
        }
    }
}
