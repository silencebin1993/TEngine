using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>远征中家园遇袭的一次判定结果（<see cref="HomeRaidAlertService.Evaluate"/>）。</summary>
    public struct HomeRaidAlert
    {
        /// <summary>玩家在远征（镜头不在家园、有远征队在外）。</summary>
        public bool Away;
        /// <summary>有目标是家园、已发预警或已到达的突袭（远征 HUD 要显示紧急通知 / 家园状态小窗）。</summary>
        public bool Show;
        /// <summary>有还没做选择的一波：弹出紧急通知（带两个选择）；全部做过选择后只剩家园状态小窗。</summary>
        public bool Popup;
        /// <summary>弹窗对应的波次（第一波还没做选择的；0 = 没有计划的突袭队伍）。</summary>
        public int PopupWave;
        /// <summary>最紧急的一波（已到达的优先，否则最早抵达的），倒计时按它写。</summary>
        public RaidWaveView Lead;
        public bool HasLead;
        /// <summary>只有没有计划的突袭队伍（调试 / 旧档）在打家园。</summary>
        public bool Unplanned;
        /// <summary>目标是家园、已预警或已到达的波数（没有计划的队伍算 1 波）。</summary>
        public int Waves;
        /// <summary>归还核心已被摧毁（按失败规则，战役失败页接管）。</summary>
        public bool CoreLost;
    }

    /// <summary>家园状态小窗的一次汇总（<see cref="HomeRaidAlertService.CollectHome"/>）。</summary>
    public sealed class HomeRaidStatus
    {
        public const int KeyPower = 0;
        public const int KeySignal = 1;
        public const int KeyDefense = 2;
        public const int KeyListening = 3;

        public bool CoreFound;
        public bool CoreDestroyed;
        /// <summary>核心耐久已打到下限（战斗内核里不阵亡，停在极小值；被打空的后果在 FG6-DEF-08）。</summary>
        public bool CoreFloor;
        public float CoreHp;
        public float CoreMax;
        /// <summary>电力 / 信号 / 防御 / 监听四类关键建筑（建成的；施工中的虚影不算）：总数、完好（没被摧毁且耐久满）、被摧毁。</summary>
        public readonly int[] Total = new int[4];
        public readonly int[] Intact = new int[4];
        public readonly int[] Destroyed = new int[4];
        /// <summary>没被摧毁的关键建筑里耐久比例最低的一座（没有受损的为空）。</summary>
        public string WorstName = string.Empty;
        public int WorstPct = -1;
        public Vector2 WorstPosition;
        public bool EnemiesArrived;
        public int EnemiesAlive;
        public int EnemiesMax;
        public int Assault;
        public int Sabotage;
        public int Siege;
        public int Retreating;
        /// <summary>还在路上（没到达家园）的来袭敌人。</summary>
        public int IncomingUnits;
        /// <summary>这次汇总扫过的建筑数（性能记录）。</summary>
        public int Scanned;

        public void Clear()
        {
            CoreFound = CoreDestroyed = CoreFloor = false;
            CoreHp = CoreMax = 0f;
            Array.Clear(Total, 0, 4);
            Array.Clear(Intact, 0, 4);
            Array.Clear(Destroyed, 0, 4);
            WorstName = string.Empty;
            WorstPct = -1;
            WorstPosition = Vector2.zero;
            EnemiesArrived = false;
            EnemiesAlive = EnemiesMax = Assault = Sabotage = Siege = Retreating = IncomingUnits = Scanned = 0;
        }
    }

    /// <summary>
    /// FG6-DEF-07（FG06 FGR-DEF-041“远征时家园遇袭：远征 HUD 弹出紧急通知（带倒计时）；家园状态小窗（归还核心和关键建筑的耐久、剩余敌人）；
    /// 玩家可以把信号跳回家园（FGR-SIG-051，远征队交给 AI），也可以让信号留在远征队那边、家园自己守”；验收 FGT-DEF-006 / 007）。
    /// - 只读判定：玩家在远征（镜头不在家园、有远征队在外）且有目标是家园、已发预警或已到达的突袭 → 显示；倒计时、方向、剩余敌人都读突袭导演 / 战斗内核（<see cref="RaidDirectorService.IncomingWaves"/>、<see cref="RaidHudService.TryWaveFight"/>）。
    /// - 两个选择：<see cref="TryJumpHome"/> 走 FG1-SIG-07 的跳回家园（<see cref="SignalUplinkService.RequestJumpHome"/>，不另写一套）；<see cref="Stay"/> 只收起弹窗。
    ///   选择按波记进突袭导演域（<see cref="RaidDirectorState.AwayDecisions"/>，存档），读档后同一波不再弹出。
    /// - 不重建后台模拟：家园在远征期间本来就在完整运行（FG0-ARCH-01 / FG0-ARCH-06）；本服务不改任何模拟状态（选择记录只由玩家点击写入），所以两种选择下家园的突袭结果与无人观察时逐位一致（FGR-BASE-021，自检 C / D 段证明）。
    /// - 开销：判定 O(计划数 + 队伍数)；家园小窗汇总 O(建筑数)（热更层，只在小窗显示时每 raid.away.home_refresh_seconds 真实秒一次；逐单位计数在战斗内核，AOT）。
    /// </summary>
    public static class HomeRaidAlertService
    {
        public const int ChoiceNone = 0;
        public const int ChoiceJumpHome = 1;
        public const int ChoiceStay = 2;

        /// <summary>自检：替代“玩家在不在远征”的判定（null = 按镜头与远征地点判定）。</summary>
        public static Func<bool> AwayOverrideForTests;

        public static int JumpCount { get; private set; }
        public static int StayCount { get; private set; }
        public static int RejectedCount { get; private set; }
        public static string LastMessage { get; private set; } = string.Empty;
        /// <summary>选择变化 +1（界面据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        private static readonly List<RaidWaveView> Threats = new List<RaidWaveView>(8);
        private static readonly List<int> WaveScratch = new List<int>(8);
        private static readonly int[] RoleScratch = new int[4];

        public static float RefreshSeconds => Mathf.Clamp(Tuning("raid.away.refresh_seconds", 0.25f), 0.05f, 1f);
        public static float HomeRefreshSeconds => Mathf.Clamp(Tuning("raid.away.home_refresh_seconds", 0.5f), 0.1f, 2f);
        public static float PerfBudgetMs => Mathf.Max(0.01f, Tuning("raid.away.perf_ms", 0.5f));
        public static int DecisionKeep => Mathf.Max(2, Mathf.RoundToInt(Tuning("raid.away.decision_keep", 8f)));
        /// <summary>小窗里选择 / 被拒提示的显示时长（真实秒）。</summary>
        public static float StatusSeconds => Mathf.Clamp(Tuning("raid.away.status_seconds", 6f), 1f, 30f);
        /// <summary>核心耐久条变红的比例。</summary>
        public static float CoreLowRatio => Mathf.Clamp01(Tuning("raid.away.core_low_ratio", 0.3f));
        /// <summary>关键建筑算“完好”的耐久比例（浮点误差内的满耐久）。</summary>
        public static float IntactRatio => Mathf.Clamp(Tuning("raid.away.intact_ratio", 0.999f), 0.5f, 1f);

        // 跳回家园有 1.5 秒过渡（FG1-SIG-07，可被 Esc 取消）：过渡期间选择只记在内存里（弹窗照样收起），信号真正离开远征机器才写进存档；被取消就撤销。
        private static readonly List<int> PendingJumpWaves = new List<int>(8);
        private static long _pendingJumpTick;
        private static string _pendingJumpSite = string.Empty;
        private static int _pendingJumpFrom;

        /// <summary>“跳回家园”的过渡还没结束、这一选择还没写进存档。</summary>
        public static bool JumpPending => PendingJumpWaves.Count > 0;
        /// <summary>过渡被取消、撤销了“跳回家园”的次数（自检读）。</summary>
        public static int JumpCancelledCount { get; private set; }

        public static void ResetSession()
        {
            JumpCount = StayCount = RejectedCount = JumpCancelledCount = 0;
            LastMessage = string.Empty;
            Revision++;
            Threats.Clear();
            ClearPendingJump();
        }

        private static void ClearPendingJump()
        {
            PendingJumpWaves.Clear();
            _pendingJumpTick = 0;
            _pendingJumpSite = string.Empty;
            _pendingJumpFrom = 0;
        }

        /// <summary>
        /// 远征 HUD 每次刷新时调用（不论小窗显不显示）：
        /// - “跳回家园”的过渡结束：信号离开了发起时的远征机器 → 把选择写进存档（时刻 / 地点按发起时）；信号还在那台机器里（Esc 取消等）→ 撤销，这一波重新弹出。
        /// - 没有计划的突袭队伍（波次 0，调试 / 旧档）都结束后清掉波次 0 的选择，下一支无计划的队伍照常弹出。
        /// 只改选择记录，不改任何模拟。O(计划数 + 队伍数)。
        /// </summary>
        public static void Poll(CampaignState s)
        {
            if (PendingJumpWaves.Count > 0 && !SignalUplinkService.IsJumpingHome)
            {
                if (s != null && _pendingJumpFrom != 0 && SignalPresence.CurrentMachineLogicId == _pendingJumpFrom)
                {
                    JumpCancelledCount++;
                    ClearPendingJump();
                    Revision++;
                }
                else
                {
                    var waves = new List<int>(PendingJumpWaves);
                    long tick = _pendingJumpTick;
                    string site = _pendingJumpSite;
                    ClearPendingJump();
                    Commit(s, waves, ChoiceJumpHome, tick, site);
                }
            }
            if (s != null && HasRecord(s, 0) && !UnplannedActive(s))
            {
                RaidDirectorState d = RaidDirectorService.StateOf(s);
                var list = new List<RaidAwayDecisionRecord>(d.AwayDecisions);
                list.RemoveAll(r => r == null || r.Wave == 0);
                d.AwayDecisions = list.ToArray();
                Revision++;
            }
        }

        private static bool HasRecord(CampaignState s, int wave)
        {
            RaidAwayDecisionRecord[] list = RaidDirectorService.StateOf(s)?.AwayDecisions;
            if (list == null)
            {
                return false;
            }
            foreach (RaidAwayDecisionRecord r in list)
            {
                if (r != null && r.Wave == wave)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 判定 ───────────────────────────────

        /// <summary>玩家在不在远征：家园在运行、有远征地点载入、镜头不在家园。</summary>
        public static bool IsAway(CampaignState s)
        {
            if (AwayOverrideForTests != null)
            {
                return AwayOverrideForTests();
            }
            IWorldSite home = WorldSimulation.Home;
            if (s == null || home == null || !home.IsLoaded || WorldSimulation.ActiveExpedition == null)
            {
                return false;
            }
            return !WorldView.IsObserved(home.SiteId);
        }

        /// <summary>目标是家园、已预警或已到达的各波（按“已到达优先、再按抵达时间”排序；共享列表，调用方不要留着）。O(计划数)。</summary>
        public static IReadOnlyList<RaidWaveView> HomeThreats(CampaignState s, long now)
        {
            Threats.Clear();
            if (s == null)
            {
                return Threats;
            }
            foreach (RaidWaveView w in RaidDirectorService.IncomingWaves(s, now))
            {
                if (w.PlannedOnly || w.Lead == null || w.Lead.TargetKind == RaidDirectorService.TargetOutpost)
                {
                    continue;
                }
                Threats.Add(w);
            }
            // 已到达的排前面（同一类里保持 IncomingWaves 的抵达时间顺序）。
            Threats.Sort((a, b) => a.Arrived != b.Arrived ? (a.Arrived ? -1 : 1) : a.ArrivalTick != b.ArrivalTick ? a.ArrivalTick.CompareTo(b.ArrivalTick) : a.Wave.CompareTo(b.Wave));
            return Threats;
        }

        /// <summary>有没有没有计划、正在打家园的突袭队伍（调试 / 旧档；与突袭条“没有计划的队伍到达时也显示”同一口径）。O(队伍数)。</summary>
        public static bool UnplannedActive(CampaignState s)
        {
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (RaidHudService.IsActiveGroup(g) && g.TargetKind != RaidDirectorService.TargetOutpost && RaidDirectorService.FindPlan(s, g.PlanId) == null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>这一刻远征 HUD 该显示什么。只读。</summary>
        public static bool Evaluate(CampaignState s, long now, out HomeRaidAlert a)
        {
            a = default;
            a.Away = IsAway(s);
            if (!a.Away)
            {
                return false;
            }
            a.CoreLost = HomeValleySoftlockGuard.IsCoreDestroyed(s);
            IReadOnlyList<RaidWaveView> threats = HomeThreats(s, now);
            bool unplanned = UnplannedActive(s);
            a.Waves = threats.Count + (unplanned ? 1 : 0);
            if (a.Waves == 0)
            {
                return false;
            }
            a.Show = true;
            if (threats.Count > 0)
            {
                a.Lead = threats[0];
                a.HasLead = true;
            }
            a.Unplanned = threats.Count == 0;
            a.PopupWave = -1;
            if (unplanned && DecisionOf(s, 0) == ChoiceNone && (threats.Count == 0 || !threats[0].Arrived))
            {
                a.PopupWave = 0; // 没有计划的队伍已经在打家园：排在还没到达的波次前面
            }
            for (int i = 0; i < threats.Count && a.PopupWave < 0; i++)
            {
                if (DecisionOf(s, threats[i].Wave) == ChoiceNone)
                {
                    a.PopupWave = threats[i].Wave;
                }
            }
            if (a.PopupWave < 0 && unplanned && DecisionOf(s, 0) == ChoiceNone)
            {
                a.PopupWave = 0;
            }
            a.Popup = a.PopupWave >= 0 && !a.CoreLost;
            return true;
        }

        // ─────────────────────────────── 选择（只由玩家点击写入）───────────────────────────────

        /// <summary>这一波做过的选择（没有 = <see cref="ChoiceNone"/>；“跳回家园”的过渡中也算已选“跳回家园”）。</summary>
        public static int DecisionOf(CampaignState s, int wave)
        {
            if (PendingJumpWaves.Contains(wave))
            {
                return ChoiceJumpHome;
            }
            RaidAwayDecisionRecord[] list = RaidDirectorService.StateOf(s)?.AwayDecisions;
            if (list == null)
            {
                return ChoiceNone;
            }
            for (int i = list.Length - 1; i >= 0; i--)
            {
                if (list[i] != null && list[i].Wave == wave)
                {
                    return list[i].Choice;
                }
            }
            return ChoiceNone;
        }

        /// <summary>
        /// “跳回家园”：走 FG1-SIG-07 的跳回家园（信号回到归还核心、镜头飞回家园；远距离 / 跨地点有过渡；被离开的远征机器按最后的命令和 AI 教义继续）。
        /// 被拒（面板开着、正在过渡、核心已被摧毁……）时不记选择，<paramref name="message"/> 写明原因。成功时把这一刻所有还没做选择的波记为“跳回家园”
        /// （有过渡时先记在内存里，过渡完成才写进存档、被取消就撤销，见 <see cref="Poll"/>）。
        /// </summary>
        public static bool TryJumpHome(CampaignState s, out string message) => TryJumpHome(s, out message, out _);

        /// <param name="noRaid">被拒的原因是“家园现在没有遇袭”（快捷键据此回退到普通的跳回家园）。</param>
        public static bool TryJumpHome(CampaignState s, out string message, out bool noRaid)
        {
            noRaid = false;
            if (!Evaluate(s, GameClock.Ticks, out HomeRaidAlert a) || !a.Show)
            {
                noRaid = true;
                message = GameText.Get("raid.away.err_no_raid");
                RejectedCount++;
                LastMessage = message;
                return false;
            }
            if (a.CoreLost)
            {
                // 与禁用的“跳回家园”按钮一致：核心已被摧毁，按失败规则由失败页接管。
                message = GameText.Format("raid.away.err_jump", GameText.Get("raid.away.core_lost"));
                RejectedCount++;
                LastMessage = message;
                return false;
            }
            int from = SignalPresence.CurrentMachineLogicId;
            UplinkRequestResult r = SignalUplinkService.RequestJumpHome();
            if (!r.Accepted)
            {
                message = GameText.Format("raid.away.err_jump", string.IsNullOrEmpty(r.Text) ? r.Failure.ToString() : r.Text);
                RejectedCount++;
                LastMessage = message;
                Revision++;
                return false;
            }
            if (SignalUplinkService.IsJumpingHome)
            {
                CollectUndecided(s);
                PendingJumpWaves.Clear();
                PendingJumpWaves.AddRange(WaveScratch);
                _pendingJumpTick = GameClock.Ticks;
                _pendingJumpSite = WorldView.ObservedSiteId ?? string.Empty;
                _pendingJumpFrom = from;
                Revision++;
            }
            else
            {
                RecordAll(s, ChoiceJumpHome);
            }
            JumpCount++;
            message = GameText.Get("raid.away.ok_jump");
            LastMessage = message;
            GuidanceHooks.Raise(GuidanceHooks.RaidAwayFirstChoice);
            return true;
        }

        /// <summary>“留在远征队”：家园自己守。收起这一刻所有还没做选择的波的弹窗（家园状态小窗留着）；不改任何模拟。</summary>
        public static bool Stay(CampaignState s, out string message)
        {
            if (!Evaluate(s, GameClock.Ticks, out HomeRaidAlert a) || !a.Show)
            {
                message = GameText.Get("raid.away.err_no_raid");
                RejectedCount++;
                LastMessage = message;
                return false;
            }
            RecordAll(s, ChoiceStay);
            StayCount++;
            message = GameText.Format("raid.away.ok_stay", InputDisplay.ForAction(GameActionId.JumpHome));
            LastMessage = message;
            GuidanceHooks.Raise(GuidanceHooks.RaidAwayFirstChoice);
            return true;
        }

        /// <summary>这一刻所有还没做选择的波（写进 <see cref="WaveScratch"/>）。</summary>
        private static void CollectUndecided(CampaignState s)
        {
            WaveScratch.Clear();
            foreach (RaidWaveView w in HomeThreats(s, GameClock.Ticks))
            {
                if (DecisionOf(s, w.Wave) == ChoiceNone && !WaveScratch.Contains(w.Wave))
                {
                    WaveScratch.Add(w.Wave);
                }
            }
            if (UnplannedActive(s) && DecisionOf(s, 0) == ChoiceNone)
            {
                WaveScratch.Add(0);
            }
        }

        private static void RecordAll(CampaignState s, int choice)
        {
            CollectUndecided(s);
            Commit(s, WaveScratch, choice, GameClock.Ticks, WorldView.ObservedSiteId ?? string.Empty);
        }

        private static void Commit(CampaignState s, List<int> waves, int choice, long tick, string site)
        {
            if (s == null || waves.Count == 0)
            {
                return;
            }
            RaidDirectorService.EnsureState(s);
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            if (d == null)
            {
                return;
            }
            var list = new List<RaidAwayDecisionRecord>(d.AwayDecisions ?? Array.Empty<RaidAwayDecisionRecord>());
            foreach (int wave in waves)
            {
                list.Add(new RaidAwayDecisionRecord { Wave = wave, Choice = choice, Tick = tick, SiteId = site ?? string.Empty });
            }
            int keep = DecisionKeep;
            if (list.Count > keep)
            {
                list.RemoveRange(0, list.Count - keep);
            }
            d.AwayDecisions = list.ToArray();
            Revision++;
        }

        // ─────────────────────────────── 家园状态小窗 ───────────────────────────────

        /// <summary>
        /// 汇总家园状态：归还核心耐久（攻城期间读内核）、四类关键建筑的完好数 / 被摧毁数与最危险的一座、剩余敌人（各波到达的读内核计数，没到达的按队伍人数）。
        /// O(建筑数 + 计划数 + 队伍数)，只在小窗显示时按 <see cref="HomeRefreshSeconds"/> 调用。
        /// </summary>
        public static void CollectHome(CampaignState s, HomeRaidStatus st)
        {
            st.Clear();
            if (s == null)
            {
                return;
            }
            float worstRatio = 2f;
            float intactRatio = IntactRatio;
            BuildingRecord worst = null;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                st.Scanned++;
                bool destroyed = b.ConstructionState == BuildingConstructionState.Destroyed;
                bool built = b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Damaged
                             || b.ConstructionState == BuildingConstructionState.Disabled;
                if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    st.CoreFound = true;
                    st.CoreDestroyed = destroyed;
                    st.CoreMax = Mathf.Max(1f, BuildingOps.MaxDurability(b.BuildingTypeId));
                    st.CoreHp = destroyed ? 0f : Mathf.Clamp(RepairDroneService.DurabilityOf(s, b), 0f, st.CoreMax);
                    st.CoreFloor = !destroyed && st.CoreHp <= CombatSiegeConst.HealthFloor + 1e-4f;
                    continue;
                }
                if (!built && !destroyed)
                {
                    continue; // 规划 / 运料 / 施工中的虚影不算关键建筑
                }
                int key = KeyIndex(SiegeCatalog.CategoryOf(b.BuildingTypeId));
                if (key < 0)
                {
                    continue;
                }
                st.Total[key]++;
                if (destroyed)
                {
                    st.Destroyed[key]++;
                    continue;
                }
                float max = Mathf.Max(1f, BuildingOps.MaxDurability(b.BuildingTypeId));
                float ratio = Mathf.Clamp01(RepairDroneService.DurabilityOf(s, b) / max);
                if (ratio >= intactRatio)
                {
                    st.Intact[key]++;
                }
                else if (ratio < worstRatio)
                {
                    worstRatio = ratio;
                    worst = b;
                }
            }
            if (worst != null)
            {
                st.WorstName = BuildingOps.NameOf(worst);
                st.WorstPct = Mathf.Clamp(Mathf.FloorToInt(worstRatio * 100f), 0, 99);
                st.WorstPosition = worst.Position;
            }
            long now = GameClock.Ticks;
            bool any = false;
            foreach (RaidWaveView w in HomeThreats(s, now))
            {
                if (w.Arrived && RaidHudService.TryWaveFight(s, w.Wave, out RaidWaveFight f))
                {
                    AddFight(st, f);
                    any = true;
                }
                else
                {
                    st.IncomingUnits += w.Units;
                }
            }
            if (!any && UnplannedActive(s) && RaidHudService.TryWaveFight(s, 0, out RaidWaveFight all))
            {
                AddFight(st, all); // 没有计划的队伍（调试 / 旧档）：按全部到达的突袭合计
            }
        }

        private static void AddFight(HomeRaidStatus st, in RaidWaveFight f)
        {
            st.EnemiesArrived = true;
            st.EnemiesAlive += f.Alive;
            st.EnemiesMax += Math.Max(f.Unfolded, f.Alive);
            st.Assault += f.Assault;
            st.Sabotage += f.Sabotage;
            st.Siege += f.Siege;
            st.Retreating += f.Retreating;
        }

        private static int KeyIndex(byte category)
        {
            switch (category)
            {
                case CombatSiegeConst.CatPower: return HomeRaidStatus.KeyPower;
                case CombatSiegeConst.CatSignal: return HomeRaidStatus.KeySignal;
                case CombatSiegeConst.CatDefense: return HomeRaidStatus.KeyDefense;
                case CombatSiegeConst.CatListening: return HomeRaidStatus.KeyListening;
                default: return -1;
            }
        }

        // ─────────────────────────────── 文字（全部走文本键）───────────────────────────────

        public static string WaveName(in HomeRaidAlert a) =>
            !a.HasLead ? GameText.Get("raid.away.wave_unplanned")
            : a.Lead.Forces > 1 ? GameText.Format("raid.warning.wave_merged", a.Lead.Wave, a.Lead.Forces) : GameText.Format("raid.warning.wave", a.Lead.Wave);

        /// <summary>
        /// 倒计时一行（游戏时间，暂停时不动、倍速下按游戏时间走）：还没到达 =“第 1 波 · 2:30 后抵达家园 · 来自东北”（集结中另写多久后出发）；
        /// 到达后 =“正在攻打家园 · 最晚 0:58 后撤退”；撤退中写还剩几台在离开。多波时加“共 n 波”。
        /// </summary>
        public static string CountdownText(CampaignState s, in HomeRaidAlert a, long now)
        {
            string name = WaveName(a);
            string text;
            if (a.HasLead && !a.Lead.Arrived)
            {
                RaidWaveView v = a.Lead;
                string dir = RaidDirectorService.WaveDirectionText(v);
                text = v.Assembling
                    ? GameText.Format("raid.away.countdown_assembling", name, RaidDirectorService.Duration(v.DepartTick - now), RaidDirectorService.Duration(v.ArrivalTick - now), dir)
                    : GameText.Format("raid.away.countdown_incoming", name, RaidDirectorService.Duration(v.ArrivalTick - now), dir);
            }
            else if (RaidHudService.TryWaveFight(s, a.HasLead ? a.Lead.Wave : 0, out RaidWaveFight f))
            {
                if (f.AnyRetreatOrder && f.Retreating > 0)
                {
                    text = GameText.Format("raid.away.countdown_retreat", name, f.Retreating);
                }
                else if (f.LimitTick >= 0)
                {
                    text = GameText.Format("raid.away.countdown_arrived", name, RaidDirectorService.Duration(f.LimitTick - now));
                }
                else
                {
                    text = GameText.Format("raid.away.countdown_fighting", name);
                }
            }
            else
            {
                text = GameText.Format("raid.away.countdown_fighting", name);
            }
            return a.Waves > 1 ? text + GameText.Format("raid.away.more", a.Waves) : text;
        }

        public static string CoreLine(HomeRaidStatus st)
        {
            if (!st.CoreFound)
            {
                return GameText.Get("raid.away.core_missing");
            }
            if (st.CoreDestroyed)
            {
                return GameText.Get("raid.away.core_lost");
            }
            int hp = Mathf.CeilToInt(st.CoreHp - 1e-3f);
            int max = Mathf.RoundToInt(st.CoreMax);
            if (st.CoreFloor)
            {
                return GameText.Format("raid.away.core_floor", 0, max);
            }
            int pct = Mathf.Clamp(Mathf.FloorToInt(st.CoreHp / Mathf.Max(1f, st.CoreMax) * 100f), 0, 100);
            return GameText.Format("raid.away.core", Mathf.Max(0, hp), max, pct);
        }

        public static string KeysLine(HomeRaidStatus st)
        {
            string line = GameText.Format("raid.away.keys", Count(st, HomeRaidStatus.KeyPower), Count(st, HomeRaidStatus.KeySignal), Count(st, HomeRaidStatus.KeyDefense),
                Count(st, HomeRaidStatus.KeyListening));
            int destroyed = st.Destroyed[0] + st.Destroyed[1] + st.Destroyed[2] + st.Destroyed[3];
            if (destroyed > 0)
            {
                line += GameText.Get("intel.list_sep") + GameText.Format("raid.away.worst_destroyed", destroyed);
            }
            if (st.WorstPct >= 0)
            {
                line += GameText.Get("intel.list_sep") + GameText.Format("raid.away.worst", st.WorstName, st.WorstPct);
            }
            return line;
        }

        private static string Count(HomeRaidStatus st, int key) => GameText.Format("raid.away.key_count", st.Intact[key], st.Total[key]);

        public static string EnemyLine(HomeRaidStatus st)
        {
            if (st.EnemiesArrived)
            {
                string line = GameText.Format("raid.away.enemies_arrived", st.EnemiesAlive, Math.Max(st.EnemiesMax, st.EnemiesAlive), st.Assault, st.Sabotage, st.Siege);
                return st.IncomingUnits > 0 ? line + GameText.Get("intel.list_sep") + GameText.Format("raid.away.enemies_incoming", st.IncomingUnits) : line;
            }
            return GameText.Format("raid.away.enemies_incoming", st.IncomingUnits);
        }

        /// <summary>弹窗收起后小窗里的一行：这一波（最紧急的一波）你选过什么。没选过为空串。</summary>
        public static string ChosenLine(CampaignState s, in HomeRaidAlert a)
        {
            int c = DecisionOf(s, a.HasLead ? a.Lead.Wave : 0);
            return c == ChoiceJumpHome ? GameText.Get("raid.away.chosen_jump") : c == ChoiceStay ? GameText.Get("raid.away.chosen_stay") : string.Empty;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
