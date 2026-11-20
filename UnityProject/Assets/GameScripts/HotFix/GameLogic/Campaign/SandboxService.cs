using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG14-SBX-02 自定义沙盒（FG12 FGR-SBX-010 入口、FGR-SBX-011 设置项、第 3 节“开局后可以部分修改”、第 4 节负向、第 5 节存档；验收 FGT-SBX-004、005）。
    /// 沙盒设置的唯一写入口；各系统只读这里的判定（O(1)）：
    /// - 突袭：关闭 = 突袭导演丢掉一切触发、取消还没出发的计划（<see cref="Defense.RaidDirectorService"/>）；低 / 高 = 突袭频率与规模同乘 sandbox.raid_mul_*（<see cref="RaidMul"/>）。
    /// - 静默夜 / 天气：关 = 不排、不发生（进行中的立即结束，<see cref="SilentNightService"/> / <see cref="WeatherService"/>）。
    /// - 事件频率：直接用事件导演的四档（<see cref="EventDirectorService.SetFrequency"/>，唯一真相在导演状态里）。
    /// - 起始资源 丰富 / 研发 全部解锁：开局一次性生效。区域 全部开放：阵营领地全部显示、酸沼防腐涂层视为已有（<see cref="TerritoryAct"/>、<see cref="AllRegionsOpen"/>）。
    /// - 敌方强度：所有敌人的耐久 / 伤害再乘 sandbox.enemy_mul_*（<see cref="EnemyMul"/>，经难度服务的敌人倍率生效）。
    /// - 起始区“关闭保证”：<see cref="WorldGenState.NoStartGuarantee"/>（世界生成读，开局后不能改）。
    /// - 没有剧情（<see cref="StoryOff"/>）：叙事节点 / 演出、主线任务、揭示与终局链路都不触发；关键材料改为在研发树里用研究点换取（<see cref="TryExchange"/>）。
    /// - 成就：沙盒存档不计入剧情类成就（<see cref="StoryAchievementsAllowed"/>，FG15-SYS-04 读它）。
    /// 开局后只有突袭 / 静默夜 / 天气 / 事件频率可以改（FG12 第 3 节），每次修改记一条记录（随存档）。
    /// </summary>
    public static class SandboxService
    {
        public const string Raids = "raids";
        public const string SilentNight = "silent_night";
        public const string Weather = "weather";
        public const string Events = "events";
        public const string StartResources = "start_resources";
        public const string Research = "research";
        public const string Regions = "regions";
        public const string Enemy = "enemy";

        public const int RaidsOff = 0;
        public const int RaidsLow = 1;
        public const int RaidsStandard = 2;
        public const int RaidsHigh = 3;

        /// <summary>一项沙盒设置：ID、选项数、默认选项、开局后能不能改。选项顺序 = FG12 FGR-SBX-011 表里的顺序。</summary>
        public readonly struct Def
        {
            public readonly string Id;
            public readonly int Options;
            public readonly int Default;
            public readonly bool Runtime;

            public Def(string id, int options, int def, bool runtime)
            {
                Id = id;
                Options = options;
                Default = def;
                Runtime = runtime;
            }

            public string NameKey => "sandbox.setting." + Id;
            public string OptionKey(int i) => "sandbox.opt." + Id + "." + i.ToString(CultureInfo.InvariantCulture);
        }

        public static readonly Def[] Defs =
        {
            new Def(Raids, 4, RaidsStandard, true),
            new Def(SilentNight, 2, 0, true),
            new Def(Weather, 2, 0, true),
            new Def(Events, 4, EventDirectorService.FrequencyStandard, true),
            new Def(StartResources, 2, 0, false),
            new Def(Research, 2, 0, false),
            new Def(Regions, 2, 0, false),
            new Def(Enemy, 3, 1, false),
        };

        /// <summary>沙盒设置或修改记录变化时 +1（面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static int SuppressedRaidTriggers { get; private set; }

        public static void ResetCountersForTests() => SuppressedRaidTriggers = 0;

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static int HistoryMax => Math.Max(8, (int)Math.Round(Tuning("sandbox.history_max", 64f)));
        public static int RichExtraScrap => Math.Max(0, (int)Math.Round(Tuning("sandbox.rich_extra_scrap", 360f)));
        public static int RichExtraTechData => Math.Max(0, (int)Math.Round(Tuning("sandbox.rich_extra_tech_data", 60f)));
        public static int ExchangePointsPerAct => Math.Max(1, (int)Math.Round(Tuning("sandbox.exchange_points_per_act", 40f)));

        public static bool TryGetDef(string id, out Def def)
        {
            foreach (Def d in Defs)
            {
                if (d.Id == id)
                {
                    def = d;
                    return true;
                }
            }
            def = default;
            return false;
        }

        // ── 判定（各系统读，O(1)）─────────────────────────────────────────────

        public static bool IsSandbox(CampaignState s) => s?.Progress != null && s.Progress.IsSandbox;

        private static SandboxState StateOf(CampaignState s) => IsSandbox(s) ? s.Progress.Sandbox : null;

        /// <summary>沙盒没有剧情（FGR-SBX-010）：叙事节点 / 演出、主线任务、揭示与终局链路都不触发。</summary>
        public static bool StoryOff(CampaignState s) => IsSandbox(s);

        /// <summary>FGT-SBX-005：剧情类成就只认剧情档（FG15-SYS-04 解锁剧情类成就前读它）。</summary>
        public static bool StoryAchievementsAllowed(CampaignState s) => s != null && !IsSandbox(s);

        public static bool RaidsDisabled(CampaignState s) => StateOf(s)?.Raids == RaidsOff;

        /// <summary>突袭频率与规模的沙盒倍率（剧情档 = 1）。关闭时返回 1（关闭由 <see cref="RaidsDisabled"/> 判定，不靠倍率）。</summary>
        public static float RaidMul(CampaignState s)
        {
            SandboxState d = StateOf(s);
            if (d == null)
            {
                return 1f;
            }
            switch (d.Raids)
            {
                case RaidsLow: return Math.Max(0.05f, Tuning("sandbox.raid_mul_low", 0.5f));
                case RaidsHigh: return Math.Max(0.05f, Tuning("sandbox.raid_mul_high", 1.5f));
                default: return 1f;
            }
        }

        public static bool SilentNightOn(CampaignState s) => (StateOf(s)?.SilentNight ?? 0) == 0;

        public static bool WeatherOn(CampaignState s) => (StateOf(s)?.Weather ?? 0) == 0;

        public static bool AllRegionsOpen(CampaignState s) => (StateOf(s)?.Regions ?? 0) == 1;

        /// <summary>领地显示用的幕，与 <see cref="EffectiveAct"/> 同一口径（白潮滩头预留区另按揭示判定，沙盒不会揭示）。</summary>
        public static int TerritoryAct(CampaignState s) => EffectiveAct(s);

        /// <summary>
        /// 按幕门控的非剧情内容（领地显示、突袭阵营与预算、世界事件、天气、商队货品、情报、诱饵伏击）读的幕。
        /// 剧情档 = 当前幕。沙盒没有剧情推幕（ADR-SBX-002 第 8 条）：区域“全部开放”= 3；
        /// “按进度解锁”= 玩家亲自揭开过危害的阵营领地里最大的幕（至少 1），即探索到哪一幕的领地，哪一幕的内容就开放。O(领地数)。
        /// </summary>
        public static int EffectiveAct(CampaignState s)
        {
            int act = Math.Max(1, s?.Progress?.Act ?? 1);
            if (!IsSandbox(s))
            {
                return act;
            }
            if (AllRegionsOpen(s))
            {
                return 3;
            }
            WorldGen.WorldPlan plan = WorldGen.WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return act;
            }
            foreach (WorldGen.PlannedTerritory t in plan.Territories)
            {
                if (t != null && t.IsFaction && t.Act > act)
                {
                    Hazard h = HomeExplorationService.HazardForTerritory(t.Id);
                    if (h != null && HomeExplorationService.HazardSeen(s, h.Id))
                    {
                        act = t.Act;
                    }
                }
            }
            return act;
        }

        /// <summary>敌方强度倍率（耐久与伤害都乘；剧情档 = 1）。</summary>
        public static float EnemyMul(CampaignState s)
        {
            SandboxState d = StateOf(s);
            if (d == null)
            {
                return 1f;
            }
            switch (d.EnemyStrength)
            {
                case 0: return Math.Max(0.05f, Tuning("sandbox.enemy_mul_low", 0.7f));
                case 2: return Math.Max(0.05f, Tuning("sandbox.enemy_mul_high", 1.4f));
                default: return 1f;
            }
        }

        /// <summary>突袭导演丢掉一次触发时记数（自检读：关闭突袭确实什么都没排）。</summary>
        public static void NoteSuppressedRaid() => SuppressedRaidTriggers++;

        // ── 设置值 ─────────────────────────────────────────────────────────

        public static int Get(CampaignState s, string id)
        {
            if (id == Events)
            {
                return EventDirectorService.Frequency(s);
            }
            SandboxState d = s?.Progress?.Sandbox;
            if (d == null)
            {
                return TryGetDef(id, out Def def) ? def.Default : 0;
            }
            switch (id)
            {
                case Raids: return d.Raids;
                case SilentNight: return d.SilentNight;
                case Weather: return d.Weather;
                case StartResources: return d.StartResources;
                case Research: return d.Research;
                case Regions: return d.Regions;
                case Enemy: return d.EnemyStrength;
                default: return 0;
            }
        }

        private static void Write(SandboxState d, string id, int v)
        {
            switch (id)
            {
                case Raids: d.Raids = v; break;
                case SilentNight: d.SilentNight = v; break;
                case Weather: d.Weather = v; break;
                case StartResources: d.StartResources = v; break;
                case Research: d.Research = v; break;
                case Regions: d.Regions = v; break;
                case Enemy: d.EnemyStrength = v; break;
            }
        }

        // ── 开局 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 沙盒新档（主菜单“沙盒模式”的“开始”；自检同一入口 <c>MainMenuUI.BuildNewCampaignState</c>）：在难度、世界、研究开局之后调用。
        /// 写沙盒标记与设置（选项越界按默认）、事件频率、起始区关闭保证；起始资源“丰富”加废料与技术数据；研发“全部解锁”把所有已就绪的研究节点记为完成。
        /// </summary>
        public static void ApplyNewGame(CampaignState s, SandboxSetup setup, bool noStartGuarantee)
        {
            if (s == null || setup == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(s);
            s.Progress.IsSandbox = true;
            var d = new SandboxState();
            s.Progress.Sandbox = d;
            foreach (Def def in Defs)
            {
                int v = setup.Get(def.Id);
                if (v < 0 || v >= def.Options)
                {
                    v = def.Default;
                }
                if (def.Id == Events)
                {
                    EventDirectorService.SetFrequency(s, v);
                    continue;
                }
                Write(d, def.Id, v);
            }
            if (noStartGuarantee)
            {
                s.World.NoStartGuarantee = true;
                WorldGen.WorldGenService.Invalidate();
            }
            if (d.StartResources == 1)
            {
                s.Scrap += RichExtraScrap;
                s.TechData += RichExtraTechData;
            }
            if (d.Research == 1)
            {
                ResearchService.CompleteAllReady(s);
            }
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.SandboxFirstStart); // 图鉴“自定义沙盒”随钩子解锁（引导内容在 FG15-UX-04）
            Log.Info($"[SandboxService] 沙盒开局：{Code(s)}（起始区关闭保证 {noStartGuarantee}）");
        }

        // ── 开局后修改（FG12 第 3 节）──────────────────────────────────────────

        public enum ChangeResult
        {
            Changed,
            Unchanged,
            NotSandbox,
            Locked,
            Invalid,
        }

        /// <summary>
        /// 开局后改一项沙盒设置（沙盒面板与暂停菜单“事件频率”都走这里）：只有突袭 / 静默夜 / 天气 / 事件频率能改；改了立即生效并记一条修改记录（随存档）。
        /// 突袭：按新倍率缩放还没预警的计划与周期计时；改成关闭时取消一切还没出发的突袭（已出发的照常抵达）。静默夜 / 天气改成关：进行中的立即结束、之后不再排。
        /// </summary>
        public static ChangeResult TryChange(CampaignState s, string id, int value)
        {
            if (!IsSandbox(s))
            {
                return ChangeResult.NotSandbox;
            }
            if (!TryGetDef(id, out Def def) || value < 0 || value >= def.Options)
            {
                return ChangeResult.Invalid;
            }
            if (!def.Runtime)
            {
                return ChangeResult.Locked;
            }
            CampaignFgStateDomains.EnsureAll(s);
            int from = Get(s, id);
            if (from == value)
            {
                return ChangeResult.Unchanged;
            }
            float oldFreq = Defense.DifficultyService.RaidFrequency(s);
            float oldScale = Defense.DifficultyService.RaidScale(s);
            if (id == Events)
            {
                EventDirectorService.SetFrequency(s, value);
            }
            else
            {
                Write(s.Progress.Sandbox, id, value);
            }
            int cancelled = 0;
            if (id == Raids)
            {
                cancelled = Defense.RaidDirectorService.OnDifficultyChanged(s, oldFreq, oldScale);
            }
            else if (id == SilentNight || id == Weather)
            {
                // 进行中的静默夜 / 天气在下一个世界步按“关”收尾（两边的世界步读同一个判定，结果与观察无关）。
            }
            SandboxState d = s.Progress.Sandbox;
            var list = new List<SandboxChangeRecord>(d.Changes.Length + 1);
            list.AddRange(d.Changes);
            list.Add(new SandboxChangeRecord
            {
                Tick = GameClock.Ticks,
                Day = s.Clock != null && s.Clock.Day > 0 ? s.Clock.Day : 1,
                Setting = id,
                From = from,
                To = value,
            });
            if (list.Count > HistoryMax)
            {
                list.RemoveRange(0, list.Count - HistoryMax);
            }
            d.Changes = list.ToArray();
            d.ChangeCount++;
            Revision++;
            string text = cancelled > 0
                ? GameText.Format("sandbox.notify.changed_cancelled", SettingName(id), OptionName(id, from), OptionName(id, value), cancelled)
                : GameText.Format("sandbox.notify.changed", SettingName(id), OptionName(id, from), OptionName(id, value));
            NotificationCenter.Post("sandbox_changed", text);
            Log.Info($"[SandboxService] 沙盒设置 {id}：{from} → {value}（第 {GameClock.Ticks} 步，取消突袭 {cancelled} 波）");
            return ChangeResult.Changed;
        }

        public static IReadOnlyList<SandboxChangeRecord> Changes(CampaignState s) =>
            s?.Progress?.Sandbox?.Changes ?? Array.Empty<SandboxChangeRecord>();

        // ── 关键材料换取（FGR-SBX-011“关掉了剧情，关键材料改为在研发树里用研究点换取”）─────────────

        /// <summary>换一件关键材料要的研究点 = sandbox.exchange_points_per_act × 这件材料所在的幕。</summary>
        public static int ExchangeCost(KeyMaterial k) => k == null ? 0 : ExchangePointsPerAct * Math.Max(1, k.Act);

        public static int Points(CampaignState s) => Math.Max(0, s?.Research?.Points ?? 0);

        /// <summary>能不能换（null = 能；否则是文本键）：剧情档不能换；蓝图拿到过就不再换；物品核心保管库里还有就不再换（投进建筑后为 0，可以再换）；研究点不够。</summary>
        public static string ExchangeBlock(CampaignState s, KeyMaterial k)
        {
            if (!StoryOff(s))
            {
                return "sandbox.exchange.deny.story";
            }
            if (k == null)
            {
                return "sandbox.exchange.deny.unknown";
            }
            if (k.Kind == "blueprint" ? CampaignEventLedger.Contains(s, CampaignObjectiveTracker.BlueprintEventId(k.Id)) : HomeInventory.Stock(s, k.Id) > 0)
            {
                return "sandbox.exchange.deny.owned";
            }
            if (Points(s) < ExchangeCost(k))
            {
                return "sandbox.exchange.deny.points";
            }
            return null;
        }

        /// <summary>用研究点换一件关键材料：扣研究点（研究服务的同一个池），物品进核心保管库、蓝图记进事件账本（与主线奖励同一份判据）。</summary>
        public static bool TryExchange(CampaignState s, string keyId, out string reason)
        {
            KeyMaterial k = QuestCatalog.KeyMaterialOf(keyId);
            reason = ExchangeBlock(s, k);
            if (reason != null)
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(s);
            int cost = ExchangeCost(k);
            if (!ItemCatalog.TryGet(ItemCatalog.ResearchPointsId, out ItemDef rp) || !HomeInventory.TryRemove(s, rp, cost))
            {
                reason = "sandbox.exchange.deny.points";
                return false;
            }
            if (k.Kind == "blueprint")
            {
                CampaignEventLedger.TryGrant(s, CampaignObjectiveTracker.BlueprintEventId(k.Id), "SandboxExchange", s.PlaySeconds, k.Id);
                AcidMarshService.InvalidateCoating(); // 防腐涂层蓝图：与主线奖励同一处失效
            }
            else
            {
                HomeInventory.Add(s, k.Id, 1, clampToSpace: false);
            }
            s.Progress.Sandbox.ExchangeCount++;
            Revision++;
            ResearchService.Touch();
            NotificationCenter.Post("sandbox_exchange", GameText.Format("sandbox.notify.exchanged", GameText.Get(k.NameKey), cost));
            return true;
        }

        // ── 显示 ───────────────────────────────────────────────────────────

        public static string SettingName(string id) => TryGetDef(id, out Def d) ? GameText.Get(d.NameKey) : id;

        public static string OptionName(string id, int v) => TryGetDef(id, out Def d) && v >= 0 && v < d.Options ? GameText.Get(d.OptionKey(v)) : v.ToString(CultureInfo.InvariantCulture);

        /// <summary>存档卡用的紧凑代码（与语言无关）：每项一位选项序号，按 <see cref="Defs"/> 顺序；末位 1 = 起始区关闭保证。</summary>
        public static string Code(CampaignState s)
        {
            if (!IsSandbox(s))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(Defs.Length + 1);
            foreach (Def d in Defs)
            {
                sb.Append((char)('0' + Math.Max(0, Math.Min(9, Get(s, d.Id)))));
            }
            sb.Append(s.World != null && s.World.NoStartGuarantee ? '1' : '0');
            return sb.ToString();
        }

        /// <summary>“突袭 关闭 · 天气 关 · 起始区 关闭保证”（只列非默认项；全部默认 = “全部标准”）。存档卡与暂停菜单共用。</summary>
        public static string Describe(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.Empty;
            }
            var parts = new List<string>();
            for (int i = 0; i < Defs.Length && i < code.Length; i++)
            {
                int v = code[i] - '0';
                if (v != Defs[i].Default)
                {
                    parts.Add(GameText.Format("world.setting.pair", GameText.Get(Defs[i].NameKey), OptionName(Defs[i].Id, v)));
                }
            }
            if (code.Length > Defs.Length && code[Defs.Length] == '1')
            {
                parts.Add(GameText.Format("world.setting.pair", GameText.Get("world.setting.axis.start"), GameText.Get("sandbox.opt.start_off")));
            }
            return parts.Count == 0 ? GameText.Get("world.setting.all_standard") : string.Join(GameText.Get("world.setting.sep"), parts);
        }

        public static string ChangeRow(SandboxChangeRecord r) => r == null ? string.Empty
            : GameText.Format("sandbox.panel.change_row", r.Day, GameClock.FormatHhMm(Math.Max(0, r.Tick) / (double)Math.Max(1, GameClock.StepHz)), SettingName(r.Setting), OptionName(r.Setting, r.From), OptionName(r.Setting, r.To));

        public static void ResetForTests() => Revision++;
    }

    /// <summary>FG14-SBX-02：新游戏面板交给主菜单的沙盒选择（每项一个选项序号；没给的项 = 默认）。</summary>
    public sealed class SandboxSetup
    {
        private readonly Dictionary<string, int> _values = new Dictionary<string, int>(StringComparer.Ordinal);

        public static SandboxSetup Defaults() => new SandboxSetup();

        public int Get(string id) =>
            _values.TryGetValue(id, out int v) ? v : SandboxService.TryGetDef(id, out SandboxService.Def d) ? d.Default : 0;

        public SandboxSetup Set(string id, int v)
        {
            _values[id] = v;
            return this;
        }
    }
}
