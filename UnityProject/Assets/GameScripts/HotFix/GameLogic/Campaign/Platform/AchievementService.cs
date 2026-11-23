using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GameConfig.fg;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>FG15-SYS-04：本战役里成就系统自己记的少量计数与判定（随存档；其余判据一律读存档里已有的统计 / 事件账本）。唯一写入口 <see cref="AchievementService"/>。
    /// 旧档没有这一域 = 新对象（Initialized = false），第一次判定时按存档事实补齐（第一幕无损按阵亡时刻与第二幕开始时刻核对，查不清就不发）。</summary>
    [Serializable]
    public sealed class AchievementCampaignState
    {
        public int DomainVersion = 1;
        public bool Initialized;
        /// <summary>从第一幕开始就在记“第一幕阵亡数”（旧档读入时已过第一幕且查不清 = false，不发“一台不少”）。</summary>
        public bool Act1Tracked;
        public int Act1Losses;
        /// <summary>第一幕已结束、结论已定（之后阵亡不再影响）。</summary>
        public bool Act1Decided;
        public bool Act1Flawless;
        public long Act2Tick = -1;
        /// <summary>反向接入成功次数（夺控结束原因 = 夺回）。</summary>
        public int ReverseHijacks;
        /// <summary>远征在外时家园击退（全歼 / 撤退）的突袭次数。</summary>
        public int RaidsRepelledAway;
        /// <summary>本战役里触发过的、被成就规则 hook 引用的引导钩子（钩子“第一次”是跨战役的本机记录，不能当本局事实）。</summary>
        public string[] Hooks = Array.Empty<string>();
        /// <summary>“产线无人介入”：连续多少个游戏分钟有产出且施工规划步号没变；上一次看到的规划步号。</summary>
        public int LineMinutes;
        public int LinePlanStep = -1;
        /// <summary>下一次按游戏分钟判定的世界步（-1 = 下一步）。</summary>
        public long NextEvalTick = -1;
    }
}

namespace GameLogic.Campaign.Platform
{
    /// <summary>成就账本（本机、跨存档）：只增不减，按成就 ID 取并集；Synced = 已推到平台（离线解锁的留着，联网后补推）。</summary>
    [Serializable]
    public sealed class AchievementLedgerEntry
    {
        public string Id;
        public string UnlockedUtc;
        public bool Synced;
        public string CampaignId;
    }

    [Serializable]
    internal sealed class AchievementLedgerFile
    {
        public int Version = 1;
        public AchievementLedgerEntry[] Entries = Array.Empty<AchievementLedgerEntry>();
    }

    /// <summary>
    /// FG15-SYS-04（FG15 FGR-SYS-080 / 081；FG19 FGR-EMG-183；验收 FGT-SYS-007 / FGT-SBX-005）：成就。
    /// - 清单与阈值在 fg.TbAchievement；判定 <see cref="Progress"/> 只读存档里的统计与事件账本（“通关”= 事件账本 endgame_ending，旧档读入同样补发）。
    /// - 判定时机：每个游戏分钟一次（世界步，O(成就数)；暂停不走、倍速按游戏时间），读入 / 新开战役后的第一步，以及结局 / 幕推进 / 相关计数变化时（标脏，下一步判）。
    /// - 剧情类（story）在沙盒存档里不解锁（<see cref="SandboxService.StoryAchievementsAllowed"/>）。
    /// - 解锁写本机账本（跨存档，云存档冲突怎么选都不影响已解锁的成就），平台可用时立刻推送，离线的留在账本里等 <see cref="SyncPending"/>。
    /// </summary>
    public static class AchievementService
    {
        public const string NotifyType = "achievement_unlocked";
        private const string FileName = "achievements.json";

        /// <summary>平台提供者（Steam / 本地模拟；null = 离线）。由 <see cref="PlatformServices"/> 注入。</summary>
        public static IPlatformProvider Provider;
        public static string FilePathOverrideForTests;
        public static Func<string> UtcNow = () => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        public static int Revision { get; private set; } = 1;
        public static int EvaluateCount { get; private set; }
        public static int UnlockCount { get; private set; }
        public static string LastUnlocked { get; private set; }
        public static event Action<string> Unlocked;

        private static List<Achievement> _rows;
        private static Dictionary<string, Achievement> _byId;
        private static Dictionary<string, string> _idByApi;
        private static HashSet<string> _hookArgs;
        private static List<AchievementLedgerEntry> _ledger;
        private static Dictionary<string, AchievementLedgerEntry> _ledgerById;
        private static bool _installed;
        private static bool _dirty;
        /// <summary>主账本和 .bak 都读不出：本次运行不写回，免得用近乎空的账本覆盖好文件（离线未同步的解锁会丢）。</summary>
        private static bool _ledgerLoadFailed;
        private static CampaignState _lastState;
        private static readonly List<ProductionStats.ItemRate> Rates = new List<ProductionStats.ItemRate>();

        public static IReadOnlyList<Achievement> Rows
        {
            get
            {
                EnsureRows();
                return _rows;
            }
        }

        public static Achievement Find(string id)
        {
            EnsureRows();
            return id != null && _byId.TryGetValue(id, out Achievement a) ? a : null;
        }

        /// <summary>判定与解锁是否启用：游戏运行时（含 Play 冒烟 / 旅程）与指定了临时账本的自检启用；编辑模式里别的自检起世界时不判（不往通知中心插“成就解锁”、不动本机账本）。
        /// 本战役计数（反向接入、阵亡等）不受影响，照常进存档。</summary>
        public static bool Enabled => FilePathOverrideForTests != null || !Application.isEditor || Application.isPlaying;

        public static string FilePath => FilePathOverrideForTests ?? Path.Combine(Application.persistentDataPath, FileName);

        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            GuidanceHooks.Touched += OnHookTouched;
            GameEvent.AddEventListener(EndingService.EndingReachedEvent, OnEndingReached);
        }

        /// <summary>自检用：清掉进程内状态（账本按 <see cref="FilePath"/> 重新读）。</summary>
        public static void ResetForTests()
        {
            _ledger = null;
            _ledgerLoadFailed = false;
            _ledgerById = null;
            _rows = null;
            _dirty = false;
            _lastState = null;
            EvaluateCount = UnlockCount = 0;
            LastUnlocked = null;
            Revision++;
        }

        // ── 账本 ──────────────────────────────────────────────────────────────

        public static bool IsUnlocked(string id)
        {
            EnsureLedger();
            return id != null && _ledgerById.ContainsKey(id);
        }

        public static AchievementLedgerEntry Entry(string id)
        {
            EnsureLedger();
            return id != null && _ledgerById.TryGetValue(id, out AchievementLedgerEntry e) ? e : null;
        }

        public static int UnlockedTotal
        {
            get
            {
                EnsureRows();
                EnsureLedger();
                int n = 0;
                foreach (Achievement a in _rows)
                {
                    if (_ledgerById.ContainsKey(a.Id))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public static int PendingSyncCount
        {
            get
            {
                EnsureLedger();
                int n = 0;
                foreach (AchievementLedgerEntry e in _ledger)
                {
                    if (!e.Synced)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        // ── 判定 ──────────────────────────────────────────────────────────────

        /// <summary>沙盒存档里剧情类成就不计入。</summary>
        public static bool Counts(Achievement a, CampaignState s) => a != null && (!a.Story || SandboxService.StoryAchievementsAllowed(s));

        /// <summary>一条成就在这个战役里的进度（当前值 / 目标）。O(1)，个别规则 O(芯片数 / 机器数 / 图鉴条目)。</summary>
        public static int Progress(Achievement a, CampaignState s, out int target)
        {
            target = Math.Max(1, a?.Threshold ?? 1);
            if (a == null || s == null)
            {
                return 0;
            }
            AchievementCampaignState st = s.Achievements;
            switch (a.Rule)
            {
                case "act_reached": return s.Progress?.Act ?? 1;
                case "boss_defeated": return IntelCatalog.TryGetBoss(a.Arg, out IntelBossDef b) && IntelService.BossDefeated(s, b) ? 1 : 0;
                case "ledger_event": return CampaignEventLedger.Contains(s, a.Arg) ? 1 : 0;
                case "uplink_count":
                {
                    int n = HasHook(st, GuidanceHooks.SignalFirstUplink) ? 1 : 0;
                    int sum = 0;
                    foreach (MachineRecord m in s.MachineRecords ?? Array.Empty<MachineRecord>())
                    {
                        sum += m != null ? m.SignalUplinkCount : 0;
                    }
                    return Math.Max(n, sum);
                }
                case "core_firmware_all":
                {
                    int n = 0;
                    foreach (string id in FirmwareKinds.CoreRosterIds)
                    {
                        if (FirmwareLibrary.IsObtained(s, id))
                        {
                            n++;
                        }
                    }
                    return n;
                }
                case "fusion_discovered": return s.Research?.Fusion?.Discovered?.Length ?? 0;
                case "codex_interactions": return MechanicCodex.UnlockedCountOf(MechanicCodexKind.Reaction);
                case "analysis_completed": return (int)Math.Min(int.MaxValue, s.Research?.Analysis?.Completed ?? 0);
                case "hook": return HasHook(st, a.Arg) ? 1 : 0;
                case "item_rate": return (int)Math.Floor(BestRatePerMinute(s));
                case "raid_repelled_away": return st?.RaidsRepelledAway ?? 0;
                case "line_unattended_minutes": return st?.LineMinutes ?? 0;
                case "purify_milestone": return PurificationService.IsReached(s, a.Arg) ? 1 : 0;
                case "purify_achievement": return PurificationService.AchievementReached(s) ? 1 : 0;
                case "harsh_clear": return CampaignEventLedger.Contains(s, EndingService.EndingEventId) && DifficultyService.KeptThroughout(s, DifficultyService.Harsh) ? 1 : 0;
                case "act1_no_loss": return st != null && st.Act1Decided && st.Act1Flawless ? 1 : 0;
                case "worm_kills": return VibrationService.StateOf(s)?.Kills ?? 0;
                case "reverse_hijacks": return st?.ReverseHijacks ?? 0;
                case "silent_nights":
                {
                    SilentNightState sn = s.Weather?.SilentNight;
                    return sn == null ? 0 : Math.Max(0, sn.Count - (sn.Active ? 1 : 0)); // 正在进行的那一次还没撑过
                }
                case "postgame_hold_days": return PostgameService.HoldDays(s, GameClock.Ticks);
                case "postgame_purge_level": return PostgameService.StateOf(s)?.HighestCleared ?? 0;
                default:
                    Log.Warning($"[AchievementService] 成就 {a.Id} 的规则 {a.Rule} 不认识"); // loc-ignore: 开发者诊断
                    return 0;
            }
        }

        /// <summary>把这个战役能解锁的成就全部判一遍（O(成就数)）。返回这次新解锁的数量。</summary>
        public static int Evaluate(CampaignState s)
        {
            if (s == null || !Enabled)
            {
                return 0;
            }
            EnsureRows();
            EnsureLedger();
            Init(s);
            EvaluateCount++;
            _dirty = false;
            int n = 0;
            foreach (Achievement a in _rows)
            {
                if (_ledgerById.ContainsKey(a.Id) || !Counts(a, s))
                {
                    continue;
                }
                if (Progress(a, s, out int target) >= target && Unlock(a, s))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>世界步（WorldSimulation 每步调，平时 O(1)）：换了战役（新开 / 读档）的第一步、标脏后、每个游戏分钟各判一次；“产线无人介入”按游戏分钟推进。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || !Enabled)
            {
                return;
            }
            AchievementCampaignState st = s.Achievements ??= new AchievementCampaignState();
            bool minute = st.NextEvalTick < 0 || tick >= st.NextEvalTick;
            if (minute)
            {
                bool first = st.NextEvalTick < 0;
                st.NextEvalTick = tick + Math.Max(1L, GameClock.TicksFor(60));
                if (!first)
                {
                    StepLine(s, st);
                }
            }
            if (minute || _dirty || !ReferenceEquals(_lastState, s))
            {
                _lastState = s;
                Evaluate(s);
            }
        }

        /// <summary>“产线连续 30 分钟无人介入”：这一游戏分钟里有产出（统计面板 1 分钟窗口）且施工规划步号没变 → +1，否则清零。</summary>
        private static void StepLine(CampaignState s, AchievementCampaignState st)
        {
            int step = s.Grid?.NextPlanStep ?? 0;
            bool produced = BestRatePerMinute(s) > 0.0;
            if (step != st.LinePlanStep || !produced)
            {
                st.LinePlanStep = step;
                st.LineMinutes = 0;
                return;
            }
            st.LineMinutes++;
        }

        private static double BestRatePerMinute(CampaignState s)
        {
            ProductionStats.CollectRates(s, 0, Rates); // 1 分钟窗口；每游戏分钟最多两次
            double best = 0;
            foreach (ProductionStats.ItemRate r in Rates)
            {
                best = Math.Max(best, r.ProducedPerMinute);
            }
            return best;
        }

        // ── 事件入口（各系统只调这里，O(1)） ───────────────────────────────────

        /// <summary>MachineRegistry.MarkDeadByLogicId（存活 → 阵亡的唯一翻转点）之后调：第一幕里阵亡计入“第一幕损失”。</summary>
        public static void OnMachineDied(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            AchievementCampaignState st = s.Achievements ??= new AchievementCampaignState();
            if (!st.Initialized)
            {
                Init(s); // 初始化按存档里的阵亡记录现数（已含这一台）
                return;
            }
            if (!st.Act1Decided && (s.Progress?.Act ?? 1) <= 1)
            {
                st.Act1Losses++;
            }
        }

        /// <summary>NarrativeService 推进幕之后调：第一幕结束那一刻定“一台不少”的结论，并立刻判一次。</summary>
        public static void OnActChanged(CampaignState s, int act)
        {
            if (s == null)
            {
                return;
            }
            Init(s);
            AchievementCampaignState st = s.Achievements;
            if (act >= 2 && !st.Act1Decided)
            {
                st.Act1Decided = true;
                st.Act2Tick = GameClock.Ticks;
                st.Act1Flawless = st.Act1Tracked && st.Act1Losses == 0;
            }
            _dirty = true;
            Evaluate(s);
        }

        /// <summary>夺控结束且原因 = 反向接入夺回。</summary>
        public static void OnReverseHijack(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            (s.Achievements ??= new AchievementCampaignState()).ReverseHijacks++;
            _dirty = true;
        }

        /// <summary>突袭结算（RaidResultService.Close）：远征在外时家园全歼 / 打退了突袭。</summary>
        public static void OnRaidClosed(CampaignState s, string outcome)
        {
            if (s == null || (outcome != RaidResultService.OutcomeDestroyed && outcome != RaidResultService.OutcomeWithdrawn) || !AnyTeamAway(s))
            {
                return;
            }
            (s.Achievements ??= new AchievementCampaignState()).RaidsRepelledAway++;
            _dirty = true;
        }

        /// <summary>主队或任一副队的离家报告开着（FG11-REG-01 多支远征队）。O(队伍数)，只在突袭结算时调。</summary>
        private static bool AnyTeamAway(CampaignState s)
        {
            if (s.Stats?.AwayReports != null && s.Stats.AwayReports.HasOpen)
            {
                return true;
            }
            foreach (ExpeditionExtraTeam x in s.Grid?.ExpeditionTeam?.Extras ?? Array.Empty<ExpeditionExtraTeam>())
            {
                if (x != null && x.AwayHasOpen)
                {
                    return true;
                }
            }
            return false;
        }

        private static void OnHookTouched(string hookId)
        {
            EnsureRows();
            if (hookId == null || (!_hookArgs.Contains(hookId) && hookId != GuidanceHooks.SignalFirstUplink))
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                return;
            }
            AchievementCampaignState st = s.Achievements ??= new AchievementCampaignState();
            if (!HasHook(st, hookId))
            {
                var list = new string[st.Hooks.Length + 1];
                Array.Copy(st.Hooks, list, st.Hooks.Length);
                list[st.Hooks.Length] = hookId;
                st.Hooks = list;
                _dirty = true; // 只在本局第一次记下时标脏：之后每放一格传送带都会再触发钩子
            }
        }

        private static void OnEndingReached()
        {
            // 结局落盘后结算画面打开、世界可能不再走步：当场判（通关、严酷通关）。
            Evaluate(CampaignSession.Current);
        }

        private static bool HasHook(AchievementCampaignState st, string hookId) => st?.Hooks != null && Array.IndexOf(st.Hooks, hookId) >= 0;

        /// <summary>第一次判定时补齐本域（旧档读入 / 新战役）。第一幕阵亡：还在第一幕 = 现数阵亡记录；已过第一幕 = 按“第二幕开始时刻”（资料库第一条第二幕及以后的记录）核对阵亡时刻，查不清就不发。</summary>
        private static void Init(CampaignState s)
        {
            AchievementCampaignState st = s.Achievements ??= new AchievementCampaignState();
            if (st.Initialized)
            {
                return;
            }
            st.Initialized = true;
            int act = s.Progress?.Act ?? 1;
            if (act <= 1)
            {
                st.Act1Tracked = true;
                st.Act1Losses = CountDead(s, long.MaxValue);
                return;
            }
            long act2 = -1;
            foreach (NarrativeEntryRecord r in s.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>())
            {
                if (r != null && r.Act >= 2 && r.Tick > 0 && (act2 < 0 || r.Tick < act2))
                {
                    act2 = r.Tick;
                }
            }
            st.Act1Decided = true;
            st.Act2Tick = act2;
            st.Act1Tracked = act2 > 0;
            st.Act1Losses = act2 > 0 ? CountDead(s, act2) : 0;
            st.Act1Flawless = st.Act1Tracked && st.Act1Losses == 0;
        }

        /// <summary>阵亡时刻早于 <paramref name="before"/> 的机器数；没有阵亡时刻的（很旧的档）一律算第一幕阵亡（宁可不发）。</summary>
        private static int CountDead(CampaignState s, long before)
        {
            int n = 0;
            foreach (MachineRecord m in s.MachineRecords ?? Array.Empty<MachineRecord>())
            {
                if (m != null && !m.IsAlive && (m.DeathTick <= 0 || m.DeathTick < before))
                {
                    n++;
                }
            }
            return n;
        }

        // ── 解锁与同步 ─────────────────────────────────────────────────────────

        private static bool Unlock(Achievement a, CampaignState s)
        {
            if (_ledgerById.ContainsKey(a.Id))
            {
                return false;
            }
            var e = new AchievementLedgerEntry { Id = a.Id, UnlockedUtc = UtcNow(), Synced = false, CampaignId = s?.CampaignId ?? string.Empty };
            _ledger.Add(e);
            _ledgerById[a.Id] = e;
            UnlockCount++;
            LastUnlocked = a.Id;
            Revision++;
            if (Provider != null && Provider.Available && Provider.Unlock(a.ApiName))
            {
                e.Synced = true;
            }
            SaveLedger();
            Notifications.NotificationCenter.Post(NotifyType, GameText.Get(a.NameKey));
            Log.Info($"[AchievementService] 成就解锁：{a.Id}（{(e.Synced ? "已同步" : "离线，等待同步")}）"); // loc-ignore: 开发者诊断
            Unlocked?.Invoke(a.Id);
            return true;
        }

        /// <summary>平台可用时：把平台上已有的成就并进账本（别处解锁的），再把离线解锁、还没推送的补推上去。只增不减。返回补推成功的数量。</summary>
        public static int SyncPending()
        {
            if (Provider == null || !Provider.Available)
            {
                return 0;
            }
            EnsureRows();
            EnsureLedger();
            bool changed = false;
            string[] onPlatform = Provider.ListUnlocked() ?? Array.Empty<string>();
            foreach (string api in onPlatform)
            {
                if (api != null && _idByApi.TryGetValue(api, out string id) && !_ledgerById.ContainsKey(id))
                {
                    var e = new AchievementLedgerEntry { Id = id, UnlockedUtc = UtcNow(), Synced = true, CampaignId = string.Empty };
                    _ledger.Add(e);
                    _ledgerById[id] = e;
                    changed = true;
                }
            }
            int pushed = 0;
            foreach (AchievementLedgerEntry e in _ledger)
            {
                Achievement a = Find(e.Id);
                // 标了已同步、平台上却没有（换了平台账号 / 平台被重置）：重新推（SetAchievement 幂等）。
                if (e.Synced && (a == null || Array.IndexOf(onPlatform, a.ApiName) >= 0))
                {
                    continue;
                }
                if (a != null && Provider.Unlock(a.ApiName))
                {
                    e.Synced = true;
                    pushed++;
                    changed = true;
                }
            }
            if (changed)
            {
                Revision++;
                SaveLedger();
            }
            return pushed;
        }

        private static void EnsureRows()
        {
            if (_rows != null)
            {
                return;
            }
            _rows = new List<Achievement>();
            _byId = new Dictionary<string, Achievement>(StringComparer.Ordinal);
            _idByApi = new Dictionary<string, string>(StringComparer.Ordinal);
            _hookArgs = new HashSet<string>(StringComparer.Ordinal);
            TbAchievement table = ConfigSystem.Instance.Tables?.TbAchievement;
            if (table == null)
            {
                Log.Error("[AchievementService] 配置表 fg.TbAchievement 不存在（改 tools/cell_tables/fgdata_achievement.py 后重新生成）"); // loc-ignore: 开发者诊断
                return;
            }
            foreach (Achievement a in table.DataList)
            {
                if (a == null || string.IsNullOrEmpty(a.Id))
                {
                    continue;
                }
                _rows.Add(a);
                _byId[a.Id] = a;
                _idByApi[a.ApiName] = a.Id;
                if (a.Rule == "hook")
                {
                    _hookArgs.Add(a.Arg);
                }
            }
            _rows.Sort((x, y) => x.SortOrder.CompareTo(y.SortOrder));
        }

        private static void EnsureLedger()
        {
            if (_ledger != null)
            {
                return;
            }
            _ledger = new List<AchievementLedgerEntry>();
            _ledgerById = new Dictionary<string, AchievementLedgerEntry>(StringComparer.Ordinal);
            string path = FilePath;
            if (TryReadLedger(path, out string err) || !File.Exists(path))
            {
                return;
            }
            Log.Warning($"[AchievementService] 成就账本读取失败，改读备份：{err}"); // loc-ignore: 开发者诊断
            if (TryReadLedger(path + ".bak", out string bakErr) && File.Exists(path + ".bak"))
            {
                return;
            }
            _ledgerLoadFailed = true;
            Log.Warning($"[AchievementService] 成就账本和备份都读不出：本次运行按空账本继续、不写回账本文件（平台上的成就联网后会并回来）：{bakErr}"); // loc-ignore: 开发者诊断
        }

        /// <summary>读一份账本文件并入内存（文件不存在算成功、什么也不并）。内容损坏 / 读不出返回 false。</summary>
        private static bool TryReadLedger(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }
                AchievementLedgerFile f = JsonUtility.FromJson<AchievementLedgerFile>(File.ReadAllText(path));
                if (f?.Entries == null)
                {
                    error = "内容不是成就账本"; // loc-ignore: 开发者诊断
                    return false;
                }
                foreach (AchievementLedgerEntry e in f.Entries)
                {
                    if (e != null && !string.IsNullOrEmpty(e.Id) && !_ledgerById.ContainsKey(e.Id))
                    {
                        _ledger.Add(e);
                        _ledgerById[e.Id] = e;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void SaveLedger()
        {
            // 编辑模式（自检 / 编辑器工具）不写玩家本机的账本：只有游戏运行时或自检指定了临时路径才落盘（同机制图鉴）。
            if (FilePathOverrideForTests == null && Application.isEditor && !Application.isPlaying)
            {
                return;
            }
            if (_ledgerLoadFailed)
            {
                return; // 账本和备份都读不出：不覆盖，留给玩家 / 下次启动
            }
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                SaveFileCommit.WriteAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(JsonUtility.ToJson(new AchievementLedgerFile { Entries = _ledger.ToArray() })), keepBackup: true);
            }
            catch (Exception ex)
            {
                Log.Warning($"[AchievementService] 成就账本写入失败（本次运行内仍算解锁）：{ex.Message}"); // loc-ignore: 开发者诊断
            }
        }
    }
}
