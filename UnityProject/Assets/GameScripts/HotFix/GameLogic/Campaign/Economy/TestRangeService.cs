using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>FG5-RND-03：一次测试为什么结束（存进 <see cref="RangeResultRecord.EndReason"/>，文本键 range.end.*）。</summary>
    public enum RangeEndReason : byte
    {
        None = 0,
        Player = 1,
        TimeUp = 2,
        /// <summary>FGR-RND-031 / 第 5 章：存档时正在进行的测试直接结束（投影不进存档）。</summary>
        Saved = 3,
        /// <summary>靶场没电、被禁用、被毁、被拆除或搬走。</summary>
        RangeLost = 4,
        Unloaded = 5,
        /// <summary>投影都被移除了。</summary>
        Emptied = 6,
        /// <summary>第 5 章：投影被接入后、信号在投影里时静默夜开始 → 断链、弹回核心、投影消失（最后一个投影没了，测试随之结束）。</summary>
        SilentNight = 7,
    }

    /// <summary>FG5-RND-03：靶场操作被拒绝的原因（稳定码，测试与界面共用；文本见 range.reason.*）。</summary>
    public enum RangeFailure : byte
    {
        None = 0,
        NoCampaign,
        NoRange,
        NotWorking,
        NoBlueprint,
        BlueprintMissing,
        ProjectionCap,
        NotFound,
        NotRunning,
        RunningLayout,
        TargetLocked,
        TargetUnknown,
        Slot,
        SignalInMachine,
        SilentNight,
        Busy,
        AlreadyUplinked,
        NotUplinked,
        RigUplinked,
        FirmwareRaw,
        FirmwareUnknown,
        PresetFull,
        PresetMissing,
    }

    /// <summary>靶场操作的结果：成功 / 失败原因码 + 给玩家看的一句话（B06）。</summary>
    public readonly struct RangeOpResult
    {
        public readonly bool Success;
        public readonly RangeFailure Failure;
        public readonly string Text;
        /// <summary>相关的靶场建筑 ID（送到靶场时 = 选中的那座）。</summary>
        public readonly string BuildingId;
        /// <summary>相关的投影编号（投影 / 接入时）。</summary>
        public readonly int Serial;

        private RangeOpResult(bool ok, RangeFailure f, string text, string buildingId, int serial)
        {
            Success = ok;
            Failure = f;
            Text = text ?? string.Empty;
            BuildingId = buildingId;
            Serial = serial;
        }

        public static RangeOpResult Ok(string text, string buildingId = null, int serial = 0) => new RangeOpResult(true, RangeFailure.None, text, buildingId, serial);
        public static RangeOpResult Fail(RangeFailure f, string text, string buildingId = null) => new RangeOpResult(false, f, text, buildingId, 0);
    }

    /// <summary>
    /// FG5-RND-03（FG05 FGR-RND-030～033；FG13 FGU-24；FGT-RND-004）：靶场与仿真投影。
    /// - 靶场（FGR-RND-030）：8×8 的研发建筑。远端 8 个靶位放靶子；标准靶一开始就有，重甲 / 高速 / 集群 / 护盾按击败过的敌人解锁（<see cref="TestRangeState.DefeatedEnemyTypes"/>）。
    /// - 仿真投影（FGR-RND-031）：投影任意一张已保存的蓝图（或从固件详情送来的“固件试验台”），**不消耗任何资源**。每座正在测试的靶场有自己的战斗内核实例
    ///   （与真实机器同一套 Main/Sim 战斗内核、同一套弹道 / 反应 / 读法——<see cref="Combat.CombatSite"/>，只是不登记进 <see cref="Combat.CombatSites"/>）：
    ///   投影结构上就不会离开靶场、不会和靶场外的任何东西互动，也不进存档（存档前 <see cref="EndAllForSave"/> 结束正在进行的测试，结果记进测试记录）。
    ///   投影可以被接入（信号进入投影：按接入态编译、核心固件按信号规则冷却，冷却只在投影里模拟、不动真实信号），静默夜开始时断链、投影消失。
    /// - 读数（FGR-RND-032）：每秒伤害、各反应触发次数、各标签覆盖率、热量曲线、能耗；结束的测试进记录，两次并排对比。
    /// - 快捷入口（FGR-RND-033）：<see cref="SendBlueprint"/>（蓝图编辑器）、<see cref="SendFirmware"/>（固件库详情）。
    /// 推进：<see cref="WorldStep"/> 由世界模拟的每个固定步调用（与观察无关，暂停不走、倍速按步）；画面只在观察家园时由 <see cref="FrameRender"/> 画（全息）。
    /// 热更层开销：每步 O(进行中的测试 + 投影数（≤ 6）+ 到期的复位)；逐单位的移动、开火、弹道、状态位统计都在内核（AOT）里。
    /// </summary>
    public static partial class TestRangeService
    {
        public const string TypeId = TestRangeCatalog.TypeId;
        /// <summary>靶场仿真地点的 ID 前缀（“test_range:” + 建筑 ID）。反应日志 / 图鉴把它显示成“靶场”。</summary>
        public const string SitePrefix = "test_range:";

        /// <summary>界面刷新用的版本号（设置、记录、投影变化时 +1；读数每步变，面板按节流刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>最近一次给玩家看的反馈（面板消息行）。</summary>
        public static string LastFeedback { get; private set; } = string.Empty;

        /// <summary>最近一次打开 / 送到的靶场（送到靶场时优先用它）。</summary>
        public static string LastRangeId { get; set; }

        public static bool IsRangeSite(string siteId) => siteId != null && siteId.StartsWith(SitePrefix, StringComparison.Ordinal);

        /// <summary>靶场的仿真地点在家园里：反馈 / 弹字按家园算观察（其它地点原样返回）。</summary>
        public static string HostSiteOf(string siteId) => IsRangeSite(siteId) ? HomeValleyLayout.RegionId : siteId;

        private static void Touch() => Revision++;

        private static void Feedback(string text)
        {
            LastFeedback = text ?? string.Empty;
            Touch();
        }

        // ─────────────────────────────── 存档域 ───────────────────────────────

        /// <summary>读档 / 新档时补全靶场域（<see cref="CampaignFgStateDomains"/> 调）：数组补成空、旧档按已击毁的敌人记录补“击败过的敌人类型”、
        /// 清掉已经不存在的靶场的布置、不认识的靶子 ID 当空位（已移除内容，B10）。O(敌人记录 + 布置)。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Research == null)
            {
                return;
            }
            s.Research.Range ??= new TestRangeState();
            TestRangeState r = s.Research.Range;
            r.DefeatedEnemyTypes ??= Array.Empty<string>();
            r.Layouts ??= Array.Empty<RangeLayoutRecord>();
            r.Presets ??= Array.Empty<RangePresetRecord>();
            r.History ??= Array.Empty<RangeResultRecord>();
            r.SeenTargets ??= Array.Empty<string>();
            if (r.NextPresetSerial < 1)
            {
                r.NextPresetSerial = 1;
            }
            if (r.NextResultSerial < 1)
            {
                r.NextResultSerial = 1;
            }
            // 旧档 / 漏记：区域里已经击毁的敌人（记录 IsAlive = false）就是“击败过”（FGR-RND-030）。
            if (s.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord e in s.RegionEnemies)
                {
                    if (e != null && !e.IsAlive && !string.IsNullOrEmpty(e.EnemyTypeId) && e.EnemyTypeId != FoundryOutpostLayout.BossNodeTypeId)
                    {
                        // 主核心在区域记录里的类型是 boss_core，敌人表里是 enemy_core_boss（同一个敌人）。
                        AddDefeated(r, e.EnemyTypeId == FoundryOutpostLayout.BossCoreTypeId ? EnemyCatalog.CoreBossId : e.EnemyTypeId);
                    }
                }
            }
            // 已经不存在的靶场（拆除 / 被毁后记录已删）的布置一并清掉；搬迁 / 升级虚影与原建筑同一 ID，仍算存在。建筑表缺失时不清（不误删）。
            HashSet<string> rangeIds = null;
            if (s.BuildingRecords != null)
            {
                rangeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (BuildingRecord b in s.BuildingRecords)
                {
                    if (IsRange(b) && !string.IsNullOrEmpty(b.BuildingId))
                    {
                        rangeIds.Add(b.BuildingId);
                    }
                }
            }
            var kept = new List<RangeLayoutRecord>(r.Layouts.Length);
            foreach (RangeLayoutRecord l in r.Layouts)
            {
                if (l == null || (rangeIds != null && !rangeIds.Contains(l.BuildingId ?? string.Empty)))
                {
                    continue;
                }
                l.Slots = NormalizeSlots(l.Slots);
                kept.Add(l);
            }
            if (kept.Count != r.Layouts.Length)
            {
                r.Layouts = kept.ToArray();
            }
            foreach (RangePresetRecord p in r.Presets)
            {
                if (p != null)
                {
                    p.Slots = NormalizeSlots(p.Slots);
                    p.Name ??= string.Empty;
                }
            }
            foreach (RangeResultRecord h in r.History)
            {
                if (h != null)
                {
                    h.Projections ??= Array.Empty<string>();
                    h.Targets ??= Array.Empty<string>();
                    h.Reactions ??= Array.Empty<RangeCountRecord>();
                    h.Coverage ??= Array.Empty<RangeCountRecord>();
                    h.HeatCurve ??= Array.Empty<float>();
                }
            }
        }

        private static TestRangeState StateOf(CampaignState s)
        {
            if (s?.Research == null)
            {
                return null;
            }
            if (s.Research.Range == null)
            {
                EnsureState(s);
            }
            return s.Research.Range;
        }

        /// <summary>靶位数组补齐到 <see cref="TestRangeCatalog.SlotCount"/>，不认识的靶子当空位。</summary>
        private static string[] NormalizeSlots(string[] slots)
        {
            int n = TestRangeCatalog.SlotCount;
            var o = new string[n];
            for (int i = 0; i < n; i++)
            {
                string id = slots != null && i < slots.Length ? slots[i] : null;
                o[i] = !string.IsNullOrEmpty(id) && TestRangeCatalog.TryGet(id, out _) ? id : string.Empty;
            }
            return o;
        }

        private static void AddDefeated(TestRangeState r, string enemyTypeId)
        {
            if (Array.IndexOf(r.DefeatedEnemyTypes, enemyTypeId) >= 0)
            {
                return;
            }
            var list = new List<string>(r.DefeatedEnemyTypes) { enemyTypeId };
            list.Sort(StringComparer.Ordinal);
            r.DefeatedEnemyTypes = list.ToArray();
        }

        // ─────────────────────────────── 靶子解锁（FGR-RND-030）───────────────────────────────

        public static bool IsUnlocked(CampaignState s, RangeTargetDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (def.AlwaysUnlocked)
            {
                return true;
            }
            TestRangeState r = StateOf(s);
            if (r == null)
            {
                return false;
            }
            foreach (string e in def.UnlockEnemies)
            {
                if (Array.IndexOf(r.DefeatedEnemyTypes, e) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>“击败铸造护甲机或铸造步进炮后解锁”（B06：锁住的靶子写明怎么解锁）。</summary>
        public static string UnlockText(RangeTargetDef def)
        {
            if (def == null || def.AlwaysUnlocked)
            {
                return string.Empty;
            }
            var names = new List<string>(def.UnlockEnemies.Length);
            foreach (string e in def.UnlockEnemies)
            {
                string n = FeedbackCues.ContentName(e);
                names.Add(string.IsNullOrEmpty(n) ? e : n);
            }
            return GameText.Format("range.panel.unlock_by", string.Join(GameText.Get("range.panel.unlock_or"), names));
        }

        /// <summary>FGR-RND-030：一种敌人被击败（区域敌人阵亡结算的唯一处调用，O(靶子种类)）。新解锁的靶子发通知（B08）与引导钩子（B14）。</summary>
        public static void NoteEnemyDefeated(CampaignState s, string enemyTypeId)
        {
            TestRangeState r = StateOf(s);
            if (r == null || string.IsNullOrEmpty(enemyTypeId) || Array.IndexOf(r.DefeatedEnemyTypes, enemyTypeId) >= 0)
            {
                return;
            }
            var before = new List<RangeTargetDef>();
            foreach (RangeTargetDef d in TestRangeCatalog.Targets)
            {
                if (!IsUnlocked(s, d))
                {
                    before.Add(d);
                }
            }
            AddDefeated(r, enemyTypeId);
            foreach (RangeTargetDef d in before)
            {
                if (IsUnlocked(s, d))
                {
                    GuidanceHooks.Raise(GuidanceHooks.RangeFirstTargetUnlocked);
                    BuildingRecord range = FirstRange(s, usableOnly: false);
                    NotificationCenter.Post("range_target_unlocked", d.Name,
                        range != null ? new Vector3(range.Position.x, 0f, range.Position.y) : (Vector3?)null);
                }
            }
            Touch();
        }

        /// <summary>面板打开时：把已经解锁的靶子记为看过（下次不再标“新”）。</summary>
        public static void MarkTargetsSeen(CampaignState s)
        {
            TestRangeState r = StateOf(s);
            if (r == null)
            {
                return;
            }
            var seen = new List<string>(r.SeenTargets);
            bool changed = false;
            foreach (RangeTargetDef d in TestRangeCatalog.Targets)
            {
                if (IsUnlocked(s, d) && !seen.Contains(d.Id))
                {
                    seen.Add(d.Id);
                    changed = true;
                }
            }
            if (changed)
            {
                seen.Sort(StringComparer.Ordinal);
                r.SeenTargets = seen.ToArray();
                Touch();
            }
        }

        public static bool IsNewTarget(CampaignState s, RangeTargetDef d) =>
            d != null && !d.AlwaysUnlocked && IsUnlocked(s, d) && Array.IndexOf(StateOf(s)?.SeenTargets ?? Array.Empty<string>(), d.Id) < 0;

        // ─────────────────────────────── 靶场建筑 ───────────────────────────────

        public static bool IsRange(BuildingRecord b) => b != null && b.BuildingTypeId == TypeId;

        /// <summary>家园里的靶场（不含规划虚影、搬迁 / 升级虚影；按建筑 ID 排序）。</summary>
        public static List<BuildingRecord> Ranges(CampaignState s)
        {
            var list = new List<BuildingRecord>();
            if (s?.BuildingRecords == null)
            {
                return list;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (IsRange(b) && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b))
                {
                    list.Add(b);
                }
            }
            list.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
            return list;
        }

        public static BuildingRecord FindRange(CampaignState s, string buildingId)
        {
            if (s == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            return IsRange(b) && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b) ? b : null;
        }

        /// <summary>这座靶场现在能不能测试：建成、没禁用、没被毁、有电。不能时 <paramref name="reason"/> = 建筑面板同一句原因（B06）。</summary>
        public static bool IsUsable(CampaignState s, BuildingRecord b, out string reason)
        {
            reason = string.Empty;
            if (b == null)
            {
                reason = GameText.Get("range.reason.no_range");
                return false;
            }
            bool built = b.ConstructionState == BuildingConstructionState.Operational;
            bool powered = !(HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof) && prof.PowerDemand > 0f)
                           || b.PowerState == BuildingPowerState.Powered;
            if (built && powered)
            {
                return true;
            }
            reason = BuildingStatusService.Evaluate(s, b).Reason;
            if (string.IsNullOrEmpty(reason))
            {
                reason = GameText.Get("range.reason.no_range");
            }
            return false;
        }

        private static BuildingRecord FirstRange(CampaignState s, bool usableOnly)
        {
            foreach (BuildingRecord b in Ranges(s))
            {
                if (!usableOnly || IsUsable(s, b, out _))
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>送到靶场时用哪座：最近打开过且能用的那座，否则第一座能用的（按建筑 ID）。</summary>
        public static BuildingRecord PickRange(CampaignState s)
        {
            BuildingRecord last = FindRange(s, LastRangeId);
            if (last != null && IsUsable(s, last, out _))
            {
                return last;
            }
            return FirstRange(s, usableOnly: true);
        }

        /// <summary>电网结算之后（完工、启停、被毁都会结算一次，<c>HomeValleyPowerGrid</c> 调）：第一次有建成的靶场 → 引导钩子（图鉴条目随之解锁）。O(建筑数)，只在结算时。</summary>
        private static bool _builtHookRaised;

        public static void OnPowerApplied(CampaignState s, bool notify)
        {
            if (!notify || _builtHookRaised || s?.BuildingRecords == null)
            {
                return;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (IsRange(b) && b.ConstructionState == BuildingConstructionState.Operational && !HomeGridService.IsRelocationGhost(b))
                {
                    _builtHookRaised = true;
                    GuidanceHooks.Raise(GuidanceHooks.RangeFirstBuilt);
                    return;
                }
            }
        }

        // ─────────────────────────────── 靶子布置与预设 ───────────────────────────────

        /// <summary>这座靶场的靶子布置（只读：没改过时是默认布置——前排放 4 个标准靶，新靶场一放下就能打，B11；读不改存档）。</summary>
        public static string[] Layout(CampaignState s, string buildingId)
        {
            TestRangeState r = StateOf(s);
            RangeLayoutRecord l = r != null && !string.IsNullOrEmpty(buildingId) ? FindLayout(r, buildingId) : null;
            if (l == null)
            {
                return DefaultSlots();
            }
            return l.Slots != null && l.Slots.Length == TestRangeCatalog.SlotCount ? l.Slots : NormalizeSlots(l.Slots);
        }

        /// <summary>要改布置时才建记录（玩家第一次改这座靶场的靶位 / 应用预设）。</summary>
        private static string[] MutableLayout(CampaignState s, string buildingId)
        {
            TestRangeState r = StateOf(s);
            RangeLayoutRecord l = FindLayout(r, buildingId);
            if (l == null)
            {
                l = new RangeLayoutRecord { BuildingId = buildingId, Slots = DefaultSlots() };
                var list = new List<RangeLayoutRecord>(r.Layouts) { l };
                list.Sort((a, b) => string.CompareOrdinal(a.BuildingId, b.BuildingId));
                r.Layouts = list.ToArray();
            }
            if (l.Slots == null || l.Slots.Length != TestRangeCatalog.SlotCount)
            {
                l.Slots = NormalizeSlots(l.Slots);
            }
            return l.Slots;
        }

        private static RangeLayoutRecord FindLayout(TestRangeState r, string buildingId)
        {
            foreach (RangeLayoutRecord l in r.Layouts)
            {
                if (l != null && l.BuildingId == buildingId)
                {
                    return l;
                }
            }
            return null;
        }

        private static string[] DefaultSlots()
        {
            string[] slots = NormalizeSlots(null);
            string basic = null;
            foreach (RangeTargetDef d in TestRangeCatalog.Targets)
            {
                if (d.AlwaysUnlocked)
                {
                    basic = d.Id;
                    break;
                }
            }
            if (basic != null)
            {
                int front = Math.Min(4, slots.Length);
                for (int i = 0; i < front; i++)
                {
                    slots[i] = basic;
                }
            }
            return slots;
        }

        /// <summary>改一个靶位（<paramref name="targetId"/> 空 = 清空）。测试进行中拒绝（读数不会被中途改布置污染）；锁住的靶子拒绝并写明怎么解锁。</summary>
        public static RangeOpResult SetSlot(CampaignState s, string buildingId, int slot, string targetId)
        {
            if (s == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoCampaign, GameText.Get("range.reason.no_campaign"));
            }
            if (FindRange(s, buildingId) == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
            }
            if (IsRunning(buildingId))
            {
                return RangeOpResult.Fail(RangeFailure.RunningLayout, GameText.Get("range.reason.running_layout"), buildingId);
            }
            string[] slots = MutableLayout(s, buildingId);
            if (slot < 0 || slot >= slots.Length)
            {
                return RangeOpResult.Fail(RangeFailure.Slot, GameText.Get("range.reason.slot"), buildingId);
            }
            string name = GameText.Get("range.panel.slot_empty");
            if (!string.IsNullOrEmpty(targetId))
            {
                if (!TestRangeCatalog.TryGet(targetId, out RangeTargetDef d))
                {
                    return RangeOpResult.Fail(RangeFailure.TargetUnknown, GameText.Get("range.reason.target_unknown"), buildingId);
                }
                if (!IsUnlocked(s, d))
                {
                    return RangeOpResult.Fail(RangeFailure.TargetLocked, GameText.Format("range.reason.target_locked", d.Name, UnlockText(d)), buildingId);
                }
                name = d.Name;
            }
            slots[slot] = targetId ?? string.Empty;
            string text = GameText.Format("range.feedback.slot_set", slot + 1, name);
            Feedback(text);
            return RangeOpResult.Ok(text, buildingId);
        }

        public static RangeOpResult ClearSlots(CampaignState s, string buildingId)
        {
            if (s == null || FindRange(s, buildingId) == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
            }
            if (IsRunning(buildingId))
            {
                return RangeOpResult.Fail(RangeFailure.RunningLayout, GameText.Get("range.reason.running_layout"), buildingId);
            }
            string[] slots = MutableLayout(s, buildingId);
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = string.Empty;
            }
            Touch();
            return RangeOpResult.Ok(string.Empty, buildingId);
        }

        public static IReadOnlyList<RangePresetRecord> Presets(CampaignState s) => StateOf(s)?.Presets ?? Array.Empty<RangePresetRecord>();

        /// <summary>把这座靶场现在的布置存成预设（名字留空 = “预设 N”）。至多 range.presets.max 个。</summary>
        public static RangeOpResult SavePreset(CampaignState s, string buildingId, string name)
        {
            TestRangeState r = StateOf(s);
            if (r == null || FindRange(s, buildingId) == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
            }
            if (r.Presets.Length >= TestRangeCatalog.PresetCap)
            {
                return RangeOpResult.Fail(RangeFailure.PresetFull, GameText.Format("range.reason.preset_full", TestRangeCatalog.PresetCap), buildingId);
            }
            int serial = r.NextPresetSerial++;
            string clean = (name ?? string.Empty).Trim();
            if (clean.Length > 24)
            {
                clean = clean.Substring(0, 24);
            }
            var p = new RangePresetRecord
            {
                PresetId = "preset." + serial.ToString(CultureInfo.InvariantCulture),
                Name = clean.Length > 0 ? clean : GameText.Format("range.panel.preset_default", serial),
                Slots = (string[])Layout(s, buildingId).Clone(),
            };
            r.Presets = new List<RangePresetRecord>(r.Presets) { p }.ToArray();
            string text = GameText.Format("range.panel.preset_saved", p.Name);
            Feedback(text);
            return RangeOpResult.Ok(text, buildingId);
        }

        /// <summary>应用预设：锁住的靶子（例如别的存档里存的、或已移除的内容）留空位，不偷偷换成别的靶子。</summary>
        public static RangeOpResult ApplyPreset(CampaignState s, string buildingId, string presetId)
        {
            TestRangeState r = StateOf(s);
            if (r == null || FindRange(s, buildingId) == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
            }
            if (IsRunning(buildingId))
            {
                return RangeOpResult.Fail(RangeFailure.RunningLayout, GameText.Get("range.reason.running_layout"), buildingId);
            }
            RangePresetRecord p = Array.Find(r.Presets, x => x != null && x.PresetId == presetId);
            if (p == null)
            {
                return RangeOpResult.Fail(RangeFailure.PresetMissing, GameText.Get("range.reason.preset_missing"), buildingId);
            }
            string[] slots = MutableLayout(s, buildingId);
            string[] src = NormalizeSlots(p.Slots);
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = TestRangeCatalog.TryGet(src[i], out RangeTargetDef d) && IsUnlocked(s, d) ? d.Id : string.Empty;
            }
            string text = GameText.Format("range.panel.preset_applied", p.Name);
            Feedback(text);
            return RangeOpResult.Ok(text, buildingId);
        }

        public static RangeOpResult DeletePreset(CampaignState s, string presetId)
        {
            TestRangeState r = StateOf(s);
            RangePresetRecord p = r != null ? Array.Find(r.Presets, x => x != null && x.PresetId == presetId) : null;
            if (p == null)
            {
                return RangeOpResult.Fail(RangeFailure.PresetMissing, GameText.Get("range.reason.preset_missing"));
            }
            var list = new List<RangePresetRecord>(r.Presets);
            list.Remove(p);
            r.Presets = list.ToArray();
            string text = GameText.Format("range.panel.preset_deleted", p.Name);
            Feedback(text);
            return RangeOpResult.Ok(text);
        }

        // ─────────────────────────────── 测试记录与对比（FGR-RND-032）───────────────────────────────

        public static IReadOnlyList<RangeResultRecord> History(CampaignState s) => StateOf(s)?.History ?? Array.Empty<RangeResultRecord>();

        public static RangeResultRecord FindResult(CampaignState s, int serial)
        {
            foreach (RangeResultRecord h in History(s))
            {
                if (h != null && h.Serial == serial)
                {
                    return h;
                }
            }
            return null;
        }

        /// <summary>对比栏的两条：玩家选过且还在的那条，否则自动取最近两条（A = 较早、B = 最新）。不足两条返回 false。</summary>
        public static bool ComparePair(CampaignState s, out RangeResultRecord a, out RangeResultRecord b)
        {
            a = null;
            b = null;
            TestRangeState r = StateOf(s);
            if (r == null || r.History.Length < 2)
            {
                return false;
            }
            int n = r.History.Length;
            b = FindResult(s, r.CompareB) ?? r.History[n - 1];
            a = FindResult(s, r.CompareA) ?? (ReferenceEquals(b, r.History[n - 1]) ? r.History[n - 2] : r.History[n - 1]);
            if (ReferenceEquals(a, b))
            {
                a = ReferenceEquals(b, r.History[n - 1]) ? r.History[n - 2] : r.History[n - 1];
            }
            return true;
        }

        public static RangeOpResult SetCompare(CampaignState s, bool slotA, int serial)
        {
            TestRangeState r = StateOf(s);
            if (r == null || r.History.Length < 2)
            {
                return RangeOpResult.Fail(RangeFailure.NotFound, GameText.Get("range.reason.history_short"));
            }
            if (FindResult(s, serial) == null)
            {
                return RangeOpResult.Fail(RangeFailure.NotFound, GameText.Get("range.reason.not_found"));
            }
            if (slotA)
            {
                r.CompareA = serial;
            }
            else
            {
                r.CompareB = serial;
            }
            GuidanceHooks.Raise(GuidanceHooks.RangeFirstCompare);
            Touch();
            return RangeOpResult.Ok(string.Empty);
        }

        public static string EndReasonText(int reason) => GameText.Get(((RangeEndReason)reason) switch
        {
            RangeEndReason.Player => "range.end.player",
            RangeEndReason.TimeUp => "range.end.time_up",
            RangeEndReason.Saved => "range.end.saved",
            RangeEndReason.RangeLost => "range.end.range_lost",
            RangeEndReason.Unloaded => "range.end.unloaded",
            RangeEndReason.SilentNight => "range.end.silent_night",
            _ => "range.end.emptied",
        });

        // ─────────────────────────────── 快捷入口（FGR-RND-033）───────────────────────────────

        /// <summary>蓝图编辑器“送到靶场测试”：在最近用过 / 第一座能用的靶场投影这张蓝图（已保存的那一版）。</summary>
        public static RangeOpResult SendBlueprint(CampaignState s, string blueprintId)
        {
            if (s == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoCampaign, GameText.Get("range.reason.no_campaign"));
            }
            BuildingRecord range = PickRange(s);
            if (range == null)
            {
                return NoUsableRange(s);
            }
            RangeOpResult r = ProjectBlueprint(s, range.BuildingId, blueprintId);
            if (r.Success)
            {
                GuidanceHooks.Raise(GuidanceHooks.RangeFirstSend);
                LastRangeId = range.BuildingId;
                Feedback(GameText.Format("range.feedback.sent", ProjectionLabel(range.BuildingId, r.Serial)));
            }
            return r;
        }

        /// <summary>固件库“送到靶场测试”：投影一台“固件试验台”（默认底盘 + 默认主武器 + 这枚固件）。常规固件插进电路；
        /// 核心固件放不进机器电路（FGR-SIG-012），试验台按接入态编译（模拟信号核里只有这一枚），冷却按信号规则在投影里模拟。未破解的固件拒绝。</summary>
        public static RangeOpResult SendFirmware(CampaignState s, string firmwareId)
        {
            if (s == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoCampaign, GameText.Get("range.reason.no_campaign"));
            }
            if (string.IsNullOrEmpty(firmwareId) || !FirmwareKinds.IsFirmware(firmwareId))
            {
                return RangeOpResult.Fail(RangeFailure.FirmwareUnknown, GameText.Get("range.reason.firmware_unknown"));
            }
            string fwName = FirmwareName(firmwareId);
            if (FirmwareKinds.IsRaw(s, firmwareId))
            {
                return RangeOpResult.Fail(RangeFailure.FirmwareRaw, GameText.Format("range.reason.firmware_raw", fwName));
            }
            BuildingRecord range = PickRange(s);
            if (range == null)
            {
                return NoUsableRange(s);
            }
            bool core = FirmwareKinds.IsCore(firmwareId);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null,
                core ? Array.Empty<string>() : new[] { firmwareId });
            var spec = new ProjectionSpec
            {
                Label = GameText.Format("range.rig.name", fwName),
                Board = board,
                FirmwareId = firmwareId,
                CoreRig = core,
            };
            RangeOpResult r = AddProjection(s, range.BuildingId, spec);
            if (r.Success)
            {
                GuidanceHooks.Raise(GuidanceHooks.RangeFirstSend);
                LastRangeId = range.BuildingId;
                Feedback(GameText.Format("range.feedback.sent", spec.Label));
            }
            return r;
        }

        private static RangeOpResult NoUsableRange(CampaignState s)
        {
            BuildingRecord any = FirstRange(s, usableOnly: false);
            if (any != null && !IsUsable(s, any, out string why))
            {
                return RangeOpResult.Fail(RangeFailure.NotWorking, GameText.Format("range.reason.not_working", why), any.BuildingId);
            }
            return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
        }

        public static string FirmwareName(string firmwareId) =>
            FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row) ? GameText.Get(row.NameKey) : firmwareId;

        /// <summary>已保存、没有归档的蓝图（面板下拉的选项；按名字排序）。</summary>
        public static List<BlueprintRecord> SavedBlueprints(CampaignState s)
        {
            var list = new List<BlueprintRecord>();
            if (s?.BlueprintRecords == null)
            {
                return list;
            }
            foreach (BlueprintRecord b in s.BlueprintRecords)
            {
                if (b != null && !b.Archived && LatestVersion(b) != null)
                {
                    list.Add(b);
                }
            }
            list.Sort((a, c) => string.Compare(BlueprintName(a), BlueprintName(c), StringComparison.CurrentCulture));
            return list;
        }

        public static string BlueprintName(BlueprintRecord b) => b == null ? string.Empty : string.IsNullOrEmpty(b.DisplayName) ? b.BlueprintId : b.DisplayName;

        /// <summary>“已保存的蓝图”= 当前启用的版本（没有就取版本号最大的）。</summary>
        public static BlueprintVersionRecord LatestVersion(BlueprintRecord b)
        {
            if (b?.Versions == null || b.Versions.Length == 0)
            {
                return null;
            }
            BlueprintVersionRecord best = null;
            foreach (BlueprintVersionRecord v in b.Versions)
            {
                if (v == null)
                {
                    continue;
                }
                if (v.Version == b.ActiveVersion)
                {
                    return v;
                }
                if (best == null || v.Version > best.Version)
                {
                    best = v;
                }
            }
            return best;
        }
    }
}
