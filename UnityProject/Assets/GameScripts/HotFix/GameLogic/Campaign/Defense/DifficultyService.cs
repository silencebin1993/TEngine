using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>FG6-DEF-09：一次难度选择（预设 ID，或“自定义” + 三个倍率）。</summary>
    public struct DifficultyChoice
    {
        public string Id;
        public float Frequency;
        public float Scale;
        public float Warning;

        public DifficultyChoice(string id, float frequency = 1f, float scale = 1f, float warning = 1f)
        {
            Id = id;
            Frequency = frequency;
            Scale = scale;
            Warning = warning;
        }
    }

    /// <summary>FG6-DEF-09：难度修改的结果。</summary>
    public enum DifficultyChangeResult
    {
        Changed = 0,
        Unchanged = 1,
        NoCampaign = 2,
    }

    /// <summary>
    /// FG6-DEF-09 难度与突袭强度（FG06 FGR-DEF-060；FG16 第 3 节；FG09 FGR-FAC-003“难度对敌人的加成公开写在难度说明里”；FG15 FGR-SYS-081；FGT-DEF-011）。
    ///
    /// - 预设（fg.TbRaidDifficulty）：建造者（只有剧情突袭、规模 ×0.5）/ 标准 / 严酷；“自定义”= 突袭频率 / 规模 / 预警时间三个滑条（存档 <see cref="RaidDifficultyState"/>）。
    /// - 读取口径统一走这里（突袭导演、攻城展开、说明文本都读它）：自定义时取存档里的三个倍率，其余参数按表里的“自定义”行（= 标准）。
    /// - 新建战役（<see cref="ApplyNewGame"/>）写开局难度；游戏中途修改（<see cref="Change"/>）记一条修改记录（开局难度 + 是否离开过开局难度永久保留），
    ///   通知突袭导演：还没发预警的计划按规模比例重新编成，改成“只有剧情突袭”时还没预警的非剧情突袭取消（<see cref="RaidDirectorService.OnDifficultyChanged"/>）。
    /// - 确定性：修改只在界面操作时发生、按当时的步序号记录；之后的效果全部来自存档里的状态（观察 / 不观察、暂停与倍速、存读档一致）。
    /// 开销：O(1)；修改时 O(计划数)。
    /// </summary>
    public static class DifficultyService
    {
        public const string Builder = "Builder";
        public const string Standard = "Standard";
        public const string Harsh = "Harsh";
        public const string Custom = "Custom";

        /// <summary>难度变了 +1（面板 / 暂停菜单据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        // ── 调参（fg.TbHomeTuning difficulty.*）──
        public static float CustomMin => Math.Max(0.05f, T("difficulty.custom_min", 0.5f));
        public static float CustomMax => Math.Max(CustomMin + 0.05f, T("difficulty.custom_max", 2f));
        public static float CustomStep => Math.Max(0.01f, T("difficulty.custom_step", 0.1f));
        public static int HistoryMax => Math.Max(4, (int)Math.Round(T("difficulty.history_max", 32f)));

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        private static float T(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[DifficultyService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_difficulty.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>滑条值钳进范围并按步长取整（存档里的值同一设置同一字节；NaN / 无穷 = 1）。</summary>
        public static float Snap(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                v = 1f;
            }
            float lo = CustomMin;
            float hi = CustomMax;
            float step = CustomStep;
            v = Math.Max(lo, Math.Min(hi, v));
            double k = Math.Round((v - lo) / step);
            double snapped = Math.Min(hi, lo + k * step);
            return (float)Math.Round(snapped, 4);
        }

        // ── 状态 ──────────────────────────────────────────────────────────────

        public static RaidDifficultyState StateOf(CampaignState s) => s?.Raids?.Difficulty;

        /// <summary>难度域补成空域（旧档没有 = 开局难度按当前难度、没有修改记录）；坏值钳回滑条范围。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Raids == null)
            {
                return;
            }
            RaidDifficultyState d = s.Raids.Difficulty ??= new RaidDifficultyState();
            if (string.IsNullOrEmpty(s.DifficultyId))
            {
                s.DifficultyId = Standard;
            }
            if (string.IsNullOrEmpty(d.StartDifficultyId))
            {
                d.StartDifficultyId = s.DifficultyId;
            }
            d.Changes ??= Array.Empty<DifficultyChangeRecord>();
            if (Array.IndexOf(d.Changes, null) >= 0)
            {
                d.Changes = Array.FindAll(d.Changes, x => x != null);
            }
            foreach (DifficultyChangeRecord r in d.Changes)
            {
                r.FromId ??= string.Empty;
                r.ToId ??= string.Empty;
            }
            d.CustomFrequency = SnapSafe(d.CustomFrequency);
            d.CustomScale = SnapSafe(d.CustomScale);
            d.CustomWarning = SnapSafe(d.CustomWarning);
            if (d.ChangeCount < d.Changes.Length)
            {
                d.ChangeCount = d.Changes.Length;
            }
        }

        // 表还没载入（极早期的旧档迁移）时不钳：0 / NaN 补成 1。
        private static float SnapSafe(float v) => float.IsNaN(v) || float.IsInfinity(v) || v <= 0f ? 1f : Snap(v);

        // ── 读取（突袭导演 / 攻城 / 界面统一口径）────────────────────────────────

        /// <summary>当前难度的表行（存档里认不出的 ID 按“标准”）。</summary>
        public static RaidDifficulty Row(CampaignState s) => RaidCatalog.Difficulty(s?.DifficultyId);

        public static RaidDifficulty Row(string id) => RaidCatalog.Difficulty(id);

        /// <summary>当前生效的难度 ID（认不出的按 Standard）。</summary>
        public static string EffectiveId(CampaignState s) => Row(s)?.Id ?? Standard;

        public static bool IsCustom(CampaignState s) => (Row(s)?.Custom ?? 0) == 1;

        public static bool IsCustomId(string id) => (Row(id)?.Custom ?? 0) == 1;

        /// <summary>突袭频率倍率（最短间隔 ÷ 本值；自定义 = 存档滑条）。</summary>
        public static float Frequency(CampaignState s) => Math.Max(0.05f, IsCustom(s) && StateOf(s) != null ? StateOf(s).CustomFrequency : Row(s)?.Frequency ?? 1f);

        /// <summary>突袭规模倍率（乘到预算上）。</summary>
        public static float Scale(CampaignState s) => Math.Max(0.05f, IsCustom(s) && StateOf(s) != null ? StateOf(s).CustomScale : Row(s)?.Scale ?? 1f);

        /// <summary>预警时间倍率（最短预警 × 本值）。</summary>
        public static float Warning(CampaignState s) => Math.Max(0.05f, IsCustom(s) && StateOf(s) != null ? StateOf(s).CustomWarning : Row(s)?.Warning ?? 1f);

        /// <summary>只有剧情突袭（建造者，FG16 第 3 节；FGT-DEF-011）。</summary>
        public static bool StoryOnly(CampaignState s) => (Row(s)?.StoryOnly ?? 0) == 1;

        /// <summary>突袭敌人的耐久倍率（展开成战斗单位时乘到生命上）。</summary>
        public static float EnemyHealthMul(CampaignState s) => Math.Max(0.05f, Row(s)?.EnemyHp ?? 1f);

        /// <summary>突袭敌人的伤害倍率（展开时乘到武器伤害上）。</summary>
        public static float EnemyDamageMul(CampaignState s) => Math.Max(0.05f, Row(s)?.EnemyDamage ?? 1f);

        /// <summary>静默夜间隔（游戏日；静默夜系统 FG7-ENV-02 读取）。</summary>
        public static int SilentNightDays(CampaignState s) => Math.Max(1, Row(s)?.SilentNightDays ?? 5);

        /// <summary>事件频率倍率（事件导演 FG12-EVT-01 读取）。</summary>
        public static float EventRate(CampaignState s) => Math.Max(0.05f, Row(s)?.EventRate ?? 1f);

        /// <summary>当前的难度选择（自定义带存档里的三个倍率；预设带表里的倍率）。</summary>
        public static DifficultyChoice Current(CampaignState s) => new DifficultyChoice(EffectiveId(s), Frequency(s), Scale(s), Warning(s));

        /// <summary>某个预设（或自定义行的初值）的选择。</summary>
        public static DifficultyChoice Preset(string id)
        {
            RaidDifficulty r = Row(id);
            return r == null ? new DifficultyChoice(Standard) : new DifficultyChoice(r.Id, r.Frequency, r.Scale, r.Warning);
        }

        /// <summary>规范化：认不出的 ID → 标准；预设的倍率取表里的值；自定义的倍率钳进滑条范围并按步长取整。</summary>
        public static DifficultyChoice Normalize(DifficultyChoice c)
        {
            RaidDifficulty r = Row(string.IsNullOrEmpty(c.Id) ? Standard : c.Id);
            if (r == null)
            {
                return new DifficultyChoice(Standard);
            }
            if (r.Custom == 1)
            {
                return new DifficultyChoice(r.Id, Snap(c.Frequency), Snap(c.Scale), Snap(c.Warning));
            }
            return new DifficultyChoice(r.Id, r.Frequency, r.Scale, r.Warning);
        }

        private static bool Same(DifficultyChoice a, DifficultyChoice b) =>
            a.Id == b.Id && Math.Abs(a.Frequency - b.Frequency) < 1e-4f && Math.Abs(a.Scale - b.Scale) < 1e-4f && Math.Abs(a.Warning - b.Warning) < 1e-4f;

        // ── 写入 ──────────────────────────────────────────────────────────────

        /// <summary>新建战役（新游戏界面“开始”）：写开局难度与自定义倍率；不算“修改”，不记修改记录。</summary>
        public static void ApplyNewGame(CampaignState s, DifficultyChoice choice)
        {
            if (s?.Raids == null)
            {
                return;
            }
            EnsureState(s);
            DifficultyChoice c = Normalize(choice);
            RaidDifficultyState d = s.Raids.Difficulty;
            s.DifficultyId = c.Id;
            d.StartDifficultyId = c.Id;
            if (IsCustomId(c.Id))
            {
                d.CustomFrequency = c.Frequency;
                d.CustomScale = c.Scale;
                d.CustomWarning = c.Warning;
            }
            d.EverLeftStart = false;
            d.ChangeCount = 0;
            d.Changes = Array.Empty<DifficultyChangeRecord>();
            Revision++;
        }

        /// <summary>
        /// 改成 <paramref name="choice"/> 时会取消几波还没发预警的非剧情突袭（二次确认框里写明；不改任何状态）。
        /// 与导演取消同一口径（<see cref="RaidDirectorService.StoryOnlyCancelCount(CampaignState)"/>：触发不是剧情的整条 + 剧情计划吸收的普通计划）。
        /// </summary>
        public static int PendingNormalRaidsToCancel(CampaignState s, DifficultyChoice choice)
        {
            if (s == null || (Row(Normalize(choice).Id)?.StoryOnly ?? 0) != 1 || StoryOnly(s))
            {
                return 0;
            }
            return RaidDirectorService.StoryOnlyCancelCount(s);
        }

        /// <summary>
        /// 游戏中途修改难度（难度面板二次确认后调用；自检同一入口）：与当前相同 → 不改、不记；否则改当前难度、记一条修改记录（超过上限丢最早的，
        /// 开局难度与“离开过开局难度”永久保留），通知突袭导演按新难度处理还没预警的计划，发“难度修改”通知。
        /// </summary>
        public static DifficultyChangeResult Change(CampaignState s, DifficultyChoice choice, out DifficultyChangeRecord record)
        {
            record = null;
            if (s?.Raids == null)
            {
                return DifficultyChangeResult.NoCampaign;
            }
            EnsureState(s);
            DifficultyChoice to = Normalize(choice);
            DifficultyChoice from = Current(s);
            if (Same(from, to))
            {
                return DifficultyChangeResult.Unchanged;
            }
            RaidDifficultyState d = s.Raids.Difficulty;
            float oldFrequency = Frequency(s);
            float oldScale = Scale(s);
            s.DifficultyId = to.Id;
            if (IsCustomId(to.Id))
            {
                d.CustomFrequency = to.Frequency;
                d.CustomScale = to.Scale;
                d.CustomWarning = to.Warning;
            }
            record = new DifficultyChangeRecord
            {
                Tick = GameClock.Ticks,
                FromId = from.Id,
                ToId = to.Id,
                FromFrequency = from.Frequency,
                FromScale = from.Scale,
                FromWarning = from.Warning,
                ToFrequency = to.Frequency,
                ToScale = to.Scale,
                ToWarning = to.Warning,
            };
            // “全程保持某难度”：离开开局难度（或在自定义里改了倍率）就永久记下，改回去也不清除（FG15 FGR-SYS-081）。
            if (to.Id != d.StartDifficultyId || IsCustomId(to.Id))
            {
                d.EverLeftStart = true;
            }
            record.CancelledRaids = RaidDirectorService.OnDifficultyChanged(s, oldFrequency, oldScale);
            var list = new List<DifficultyChangeRecord>(d.Changes.Length + 1);
            list.AddRange(d.Changes);
            list.Add(record);
            int max = HistoryMax;
            if (list.Count > max)
            {
                list.RemoveRange(0, list.Count - max);
            }
            d.Changes = list.ToArray();
            d.ChangeCount++;
            string fromName = Label(from);
            string toName = Label(to);
            NotificationCenter.Post("difficulty_changed", record.CancelledRaids > 0
                ? GameText.Format("ui.difficulty.notify_cancelled", fromName, toName, record.CancelledRaids)
                : GameText.Format("ui.difficulty.notify", fromName, toName));
            GuidanceHooks.Raise(GuidanceHooks.DifficultyFirstChanged);
            Log.Info($"[DifficultyService] 难度 {from.Id} → {to.Id}（频率 ×{Num(to.Frequency)} 规模 ×{Num(to.Scale)} 预警 ×{Num(to.Warning)}），第 {record.Tick} 步，取消 {record.CancelledRaids} 波普通突袭");
            Revision++;
            return DifficultyChangeResult.Changed;
        }

        /// <summary>FG15 FGR-SYS-081：这个战役是否从开局到现在一直保持 <paramref name="id"/>（“严酷难度通关”成就按它判定）。</summary>
        public static bool KeptThroughout(CampaignState s, string id)
        {
            RaidDifficultyState d = StateOf(s);
            return d != null && !string.IsNullOrEmpty(id) && d.StartDifficultyId == id && s.DifficultyId == id && !d.EverLeftStart;
        }

        // ── 文本（公开写出对敌人的全部加成，FGR-FAC-003）──────────────────────────

        public static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public static string Name(string id) => GameText.Get(Row(id)?.NameKey ?? "difficulty.standard.name");

        /// <summary>“严酷”；自定义写出三个倍率：“自定义（频率 ×1.2 · 规模 ×0.8 · 预警 ×1.5）”。</summary>
        public static string Label(DifficultyChoice c) =>
            IsCustomId(c.Id) ? GameText.Format("ui.difficulty.custom_values", Name(c.Id), Num(c.Frequency), Num(c.Scale), Num(c.Warning)) : Name(c.Id);

        public static string Label(CampaignState s) => Label(Current(s));

        /// <summary>难度说明：一句话 + 逐行列出对突袭与敌人的全部影响（新游戏界面 / 难度面板 / 图鉴同一口径）。</summary>
        public static List<string> DescribeLines(DifficultyChoice choice)
        {
            DifficultyChoice c = Normalize(choice);
            RaidDifficulty r = Row(c.Id);
            var lines = new List<string>(10);
            if (r == null)
            {
                return lines;
            }
            lines.Add(GameText.Get(r.DescKey));
            if (r.StoryOnly == 1)
            {
                lines.Add(GameText.Get("difficulty.line.story_only"));
            }
            else
            {
                lines.Add(GameText.Format("difficulty.line.frequency", Num(c.Frequency)));
            }
            lines.Add(GameText.Format("difficulty.line.scale", Num(c.Scale)));
            // 先按游戏分钟取整再显示（float 的 0.8 × 1.5 = 1.2000000x 小时，直接向上取整会显示成 73 分）。
            double warnMinutes = Math.Round(RaidCatalog.MinWarningHours * (double)c.Warning * 60.0, 2);
            lines.Add(GameText.Format("difficulty.line.warning", Num(c.Warning), GameClock.FormatGameDuration(warnMinutes * GameClock.DaySeconds / 1440.0)));
            lines.Add(GameText.Format("difficulty.line.enemy", Num(r.EnemyHp), Num(r.EnemyDamage)));
            lines.Add(GameText.Format("difficulty.line.silent_night", r.SilentNightDays));
            lines.Add(GameText.Format("difficulty.line.events", Num(r.EventRate)));
            lines.Add(GameText.Get("difficulty.line.first_raid"));
            lines.Add(GameText.Get("difficulty.line.core_loss"));
            lines.Add(GameText.Get("difficulty.line.record"));
            return lines;
        }

        public static string Describe(DifficultyChoice c) => string.Join("\n", DescribeLines(c));

        /// <summary>修改记录的一行：“第 3 天 08:30：标准 → 严酷”。</summary>
        public static string HistoryRow(DifficultyChangeRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            double sec = r.Tick / (double)Math.Max(1, GameClock.StepHz);
            return GameText.Format("ui.difficulty.history_row", GameClock.DayOf(sec), GameClock.FormatHhMm(sec),
                Label(new DifficultyChoice(r.FromId, r.FromFrequency, r.FromScale, r.FromWarning)), Label(new DifficultyChoice(r.ToId, r.ToFrequency, r.ToScale, r.ToWarning)));
        }

        /// <summary>存读档比对用的字段快照（自检）。</summary>
        public static string Snapshot(CampaignState s)
        {
            RaidDifficultyState d = StateOf(s);
            if (d == null)
            {
                return "(无难度域)";
            }
            var sb = new System.Text.StringBuilder(128);
            sb.Append(s.DifficultyId).Append('|').Append(d.StartDifficultyId).Append('|').Append(Num(d.CustomFrequency)).Append(',').Append(Num(d.CustomScale)).Append(',')
                .Append(Num(d.CustomWarning)).Append('|').Append(d.EverLeftStart ? 1 : 0).Append('|').Append(d.ChangeCount);
            foreach (DifficultyChangeRecord r in d.Changes)
            {
                sb.Append("|C").Append(r.Tick).Append(':').Append(r.FromId).Append('>').Append(r.ToId).Append(':').Append(Num(r.ToFrequency)).Append(',').Append(Num(r.ToScale))
                    .Append(',').Append(Num(r.ToWarning)).Append(':').Append(r.CancelledRaids);
            }
            return sb.ToString();
        }
    }
}
