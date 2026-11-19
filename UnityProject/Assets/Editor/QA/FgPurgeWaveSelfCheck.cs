using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.HomeValleyFailure;
using GameLogic.UI.Kit;
using TEngine;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-03 清洗波次（FG11 FGR-END-020，验收 FGT-END-004）自检：起真实世界（家园内核、突袭导演、行进、攻城、存档）跑——
    /// A 数据（触发 purge、登陆舱编成行与攻城参数、调参 30 秒、文本）；B 揭示后开始、三波由白潮从轨道登陆点发动（每波有登陆舱，攻城队伍里的舱落地投放同一队伍的单位）、
    /// 每波预警时自动存档、2 个游戏日内 3 波、撑过 3 波写事件账本（只一次）；C 负向：核心被毁 → 失败页写明从第几波重试、不再写盘、不再推进，读档回到那一波开始前、不重复奖励；
    /// D 轨道打击：落点提前 30 秒在地图标出、离核心够远、按时落下伤建筑、与建筑排列顺序无关、没有建筑时落在核心外；E 暂停与 0.5x～3x；F 存读档；G 观察一致；H 建造者难度照样来、旧档。
    /// </summary>
    public static class FgPurgeWaveSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string FailureUxml = "Assets/GameRes/Raw/UI/HomeValleyFailure/HomeValleyFailure.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _clearedEvents;

        [MenuItem("QA/自检/FG 清洗波次")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[清洗波次] 揭示后 2 个游戏日内 3 波登陆舱突袭、每波前自动存档、轨道打击提前 30 秒标出、核心被毁重试（FG13-END-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpurge-selfcheck-" + Guid.NewGuid().ToString("N"));
            GameEvent.AddEventListener(PurgeWaveService.ClearedEvent, OnCleared);
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                BuildingOps.Reload();
                BuildMaterials.Reload();
                ResearchCatalog.Reload();
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                SiegeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                QuestCatalog.Reload();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                PurgeWaveService.ResetCountersForTests();
                CombatSite.ResetFleetCountersForTests();

                Step(CheckData);
                Step(CheckWavesAndRetry);
                Step(CheckStrike);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckBuilderAndLegacy);
            }
            finally
            {
                GameEvent.RemoveEventListener(PurgeWaveService.ClearedEvent, OnCleared);
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  清洗波次：全部 {_pass} 项通过" : $"  清洗波次：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static void OnCleared() => _clearedEvents++;

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            bool trig = RaidCatalog.TryGetTrigger(RaidCatalog.TriggerPurge, out RaidTriggerDef t) && t.Exempt && t.LevelRule == "request";
            bool pod = RaidCatalog.TryGetUnit("fleet.drop_pod", out RaidUnitDef u) && u.Faction == EnemyDefs.FactionFleet && u.EnemyTypeId == "enemy_drop_pod"
                       && u.HasTag(RaidCatalog.TagPod) && u.Points > 0 && SiegeCatalog.TryGetUnit("enemy_drop_pod", out _);
            bool problems = RaidCatalog.Problems.Count == 0 && SiegeCatalog.Problems.Count == 0;
            bool warn = Math.Abs(PurgeWaveService.StrikeWarnSeconds - 30f) < 1e-4f && PurgeWaveService.WaveCount == 3;
            double lastArrivalDays = PurgeWaveService.WaveDays(3) + RaidCatalog.StoryPlanLeadDays + RaidCatalog.MinWarningHours / 24.0;
            string[] keys =
            {
                "raid.trigger.purge.name", "raid.trigger.purge.desc", "raid.origin.orbit", "purge.hud.wait", "purge.hud.active", "purge.hud.done", "purge.hud.strike",
                "purge.hud.tip", "purge.notify.start", "purge.notify.wave", "purge.notify.wave_cleared", "purge.notify.cleared", "purge.notify.strike",
                "purge.strike.open_ground", "failure.purge_retry", "quest.prog.purge",
            };
            var missing = keys.Where(k => !GameText.Has(k)).ToList();
            Expect(trig && pod && problems && warn && lastArrivalDays <= 2.0 && missing.Count == 0,
                $"A1 数据：触发 purge 不受最短间隔（{trig}）；白潮编成表有登陆舱行 fleet.drop_pod（标签 pod、点数 {u?.Points}、有攻城参数：{pod}；DEBT-FG6DEF04-04 攻城分支）；目录无问题（{problems}）；" +
                $"落点提前 {PurgeWaveService.StrikeWarnSeconds} 秒、{PurgeWaveService.WaveCount} 波；第 3 波最早到达 ≤ {lastArrivalDays:F2} 日（窗口 2 日）；文本缺 {missing.Count}：{string.Join(",", missing)}"
                + (RaidCatalog.Problems.Count + SiegeCatalog.Problems.Count > 0 ? "\n目录问题：" + string.Join("；", RaidCatalog.Problems.Concat(SiegeCatalog.Problems)) : string.Empty));
        }

        // ─────────────────────────────── B / C 三波、存档、重试 ───────────────────────────────

        private static void CheckWavesAndRetry()
        {
            CampaignState s = NewWorld(13031);
            int start0 = PurgeWaveService.StartCount;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool notBefore = !PurgeWaveService.Started(s) && PurgeWaveService.StartCount == start0;
            Reveal(s);
            WorldSimulation.StepMany(1);
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            ObjectiveRecord quest0 = CampaignObjectiveTracker.FindQuest(s, "main.purge");
            string prog0 = quest0 != null ? CampaignObjectiveTracker.ProgressText(s, quest0) : string.Empty;
            bool started = st.StartTick >= 0 && st.NextStrikeTick > st.StartTick && Notified("purge.notify.start", out _) && quest0 != null && quest0.State != ObjectiveState.Completed && prog0.Contains("0/3");
            Expect(notBefore && started,
                $"B1 揭示之前清洗不开始（{notBefore}）；揭示状态落定后下一步开始（第 {st.StartTick} 步），排好第一次轨道打击、发“清洗开始”通知，主线“守住家园”写“{prog0}”（{started}）");
            long startTick = st.StartTick;
            _clearedEvents = 0;
            int grant0 = PurgeWaveService.GrantCount;

            // 第 1 波：完整跑一遍（发动 → 计划 → 预警存档 → 行进 → 攻城（登陆舱落地投放）→ 全歼 → 打完）。
            if (!RunWave(s, 1, out long arrive1))
            {
                return;
            }
            // 第 2 波：到预警存档为止，然后核心被毁（负向）→ 读档重试。
            RaidPlanRecord p2 = LaunchToWarn(s, 2);
            if (p2 == null)
            {
                return;
            }
            string sigSaved = PurgeSig(s);
            HomeValleySoftlockGuard.DebugDestroyCore(s);
            int blocked0 = CampaignAutoSaveService.BlockedSaves;
            SaveResult refused = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            string retry = PurgeWaveService.FailureRetryText(s);
            long skip = PurgeWaveService.EarliestLaunchTick(st, 2) + RaidDirectorService.DayTicks(0.2) - GameClock.Ticks;
            GameClock.SkipForTests(s, Math.Max(1, skip));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool frozen = st.Launched == 2 && st.Cleared == 1 && !CampaignEventLedger.Contains(s, PurgeWaveService.ClearedEventId);
            string pageSave = string.Empty;
            bool pageOk = false;
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FailureUxml).CloneTree();
            var go = new GameObject("__FgPurgeFailure") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                HomeValleyFailureUIToolkit page = go.AddComponent<HomeValleyFailureUIToolkit>();
                page.BindView(tree);
                page.Apply(true);
                pageSave = page.LastSaveText;
                pageOk = page.Shown && pageSave.Contains("第 2/3 波") && page.LoadButton != null && page.LoadButton.enabledSelf && !GameText.ContainsMarker(pageSave);
                page.Apply(false);
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
            Expect(refused.Outcome == SaveOutcome.CampaignLost && CampaignAutoSaveService.BlockedSaves == blocked0 + 1 && retry.Contains("第 2/3 波") && frozen && pageOk,
                $"C1 负向：第 2 波来袭时核心被摧毁 → 存档被拒（{refused.Outcome}），清洗不再推进（过了第 3 波的时刻仍停在 已发动 {st.Launched} / 打完 {st.Cleared}：{frozen}）；" +
                $"失败页写明“{pageSave.Replace('\n', ' ')}”（{pageOk}）");

            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("C2 读取最近自动存档失败：" + fail);
                return;
            }
            PurgeWaveState lt = PurgeWaveService.StateOf(l);
            int wave2Plans = RaidDirectorService.Plans(l).Count(x => x != null && x.StoryId == PurgeWaveService.WaveId(2));
            int wave1Plans = RaidDirectorService.Plans(l).Count(x => x != null && x.StoryId == PurgeWaveService.WaveId(1));
            bool restored = !HomeValleySoftlockGuard.IsCoreDestroyed(l) && lt.Cleared == 1 && lt.Launched == 2 && wave2Plans == 1 && wave1Plans == 0
                            && !CampaignEventLedger.Contains(l, PurgeWaveService.ClearedEventId) && PurgeSig(l) == sigSaved;
            Expect(restored,
                $"C2 读取最近自动存档：回到第 2 波开始前（核心完好、打完 {lt.Cleared}、第 2 波计划 {wave2Plans} 个、第 1 波不再来 {wave1Plans}、清洗状态与存档时一致：{PurgeSig(l) == sigSaved}）");

            st = lt;
            s = l;
            RaidPlanRecord rp2 = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            if (rp2 == null || !RunFromWarn(s, 2, rp2, out long arrive2))
            {
                Fail($"C3 读档后第 2 波没能接着打完（计划 {st.ActivePlanId}）");
                return;
            }
            if (!RunWave(s, 3, out long arrive3))
            {
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            double days = (Math.Max(arrive1, Math.Max(arrive2, arrive3)) - startTick) / (double)RaidDirectorService.DayTicks(1);
            bool granted = CampaignEventLedger.Contains(s, PurgeWaveService.ClearedEventId) && PurgeWaveService.GrantCount == grant0 + 1 && _clearedEvents == 1
                           && st.DoneTick >= 0 && st.NextStrikeTick < 0 && Notified("purge.notify.cleared", out _);
            bool again = !CampaignEventLedger.TryGrant(s, PurgeWaveService.ClearedEventId, "Endgame", s.PlaySeconds, "test");
            string quest = QuestState(s, "main.purge");
            Expect(granted && again && days <= 2.0 && quest == "Completed",
                $"B5 撑过第 3 波：事件账本 endgame_purge_cleared 写入一次、GameEvent 一次（{_clearedEvents}）、轨道打击停止；再授予被拒（{again}）；" +
                $"三波到达都在开始后 {days:F2} 个游戏日内（≤ 2）；主线“守住家园” = {quest}");

            int plans0 = RaidDirectorService.Plans(s).Count(x => x != null && x.Trigger == RaidCatalog.TriggerPurge);
            int strikes0 = st.StrikeCount;
            GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1));
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            int plans1 = RaidDirectorService.Plans(s).Count(x => x != null && x.Trigger == RaidCatalog.TriggerPurge);
            Expect(st.Launched == 3 && plans1 == plans0 && st.StrikeCount == strikes0 && PurgeWaveService.HudText(s, GameClock.Ticks).Contains("已撑过"),
                $"C4 负向：撑过之后再过 1 个游戏日不再有清洗波次（计划 {plans0} → {plans1}）、不再排轨道打击（{strikes0} → {st.StrikeCount}）；顶栏写“{PurgeWaveService.HudText(s, GameClock.Ticks)}”");
        }

        /// <summary>第 <paramref name="wave"/> 波：发动 → 预警存档 → 行进到达 → 登陆舱落地投放 → 全歼 → 打完。</summary>
        private static bool RunWave(CampaignState s, int wave, out long arrive)
        {
            arrive = -1;
            RaidPlanRecord p = LaunchToWarn(s, wave);
            return p != null && RunFromWarn(s, wave, p, out arrive);
        }

        private static RaidPlanRecord LaunchToWarn(CampaignState s, int wave)
        {
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            long at = PurgeWaveService.EarliestLaunchTick(st, wave - 1);
            if (GameClock.Ticks < at - 2)
            {
                GameClock.SkipForTests(s, at - 2 - GameClock.Ticks);
            }
            bool notEarly = st.Launched == wave - 1;
            WorldSimulation.StepMany(4);
            bool launched = st.Launched == wave && st.LaunchTicks[wave - 1] == at;
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            if (p == null)
            {
                Fail($"B2 第 {wave} 波没有建出突袭计划（已发动 {st.Launched}，计划 {st.ActivePlanId}）");
                return null;
            }
            GridCell core = HomeGridService.CorePivot(s);
            double dist = Math.Sqrt((p.OriginX - core.X) * (double)(p.OriginX - core.X) + (p.OriginY - core.Y) * (double)(p.OriginY - core.Y));
            bool shape = p.Trigger == RaidCatalog.TriggerPurge && p.Faction == EnemyDefs.FactionFleet && p.TargetKind == RaidDirectorService.TargetHome
                         && p.OriginKind == RaidDirectorService.OriginOrbit && dist > 20 && dist < 80 && p.Level == PurgeWaveService.WaveLevel(wave)
                         && p.UnitIds.Any(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.HasTag(RaidCatalog.TagPod))
                         && p.UnitIds.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.Faction == EnemyDefs.FactionFleet);
            string hudWait = PurgeWaveService.HudText(s, GameClock.Ticks);
            bool scheduled = F.StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 60);
            if (scheduled && p.WarnTick - 2 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, p.WarnTick - 2 - GameClock.Ticks);
            }
            bool warned = scheduled && F.StepUntil(() => p.State >= RaidDirectorService.StateWarned, 10);
            int pending = RaidResultService.PendingAutosaveWave;
            bool saved = warned && RaidResultService.FlushAutosave(s);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            bool save = saved && pending == p.Wave && s.LastSaveReason == SaveReason.RaidWarning && meta.State == CampaignSlotState.Ready && st.SavedRetryWave == wave
                        && p.ArrivalTick > GameClock.Ticks;
            Expect(notEarly && launched && shape && save && hudWait.Contains($"第 {wave}/3 波"),
                $"B2 第 {wave} 波：第 {at} 步（开始后 {PurgeWaveService.WaveDays(wave)} 日、上一波打完 + {PurgeWaveService.WaveGapSeconds} 秒）准时交给导演（{launched}）；" +
                $"计划 {p.PlanId}：触发 {p.Trigger}、阵营 {p.Faction}、目标 {p.TargetKind}、从 {p.OriginKind} 登陆点（离核心 {dist:F0} 格）、{p.Level} 级、编成 {string.Join("/", p.UnitIds.Select((id, i) => id + "×" + p.UnitCounts[i]))}（含登陆舱：{shape}）；" +
                $"预警时（到达前 {(p.ArrivalTick - GameClock.Ticks) / (double)GameClock.StepHz:F0} 秒）自动存档（原因 {s.LastSaveReason}，读档重试第 {st.SavedRetryWave} 波：{save}）；顶栏“{hudWait}”");
            return save ? p : null;
        }

        private static bool RunFromWarn(CampaignState s, int wave, RaidPlanRecord p, out long arrive)
        {
            arrive = -1;
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            if (p.DepartTick - 1 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, p.DepartTick - 1 - GameClock.Ticks);
            }
            int key = 0;
            bool arrived = F.StepUntil(() =>
            {
                TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
                key = g != null ? SiegeService.KeyOf(g) : 0;
                return key != 0 && GroupUnits(key).Count > 0;
            }, 240);
            if (!arrived)
            {
                Fail($"B3 第 {wave} 波没有到达展开（计划状态 {p.State}，队伍 {p.GroupId}）");
                return false;
            }
            arrive = GameClock.Ticks;
            string hudActive = PurgeWaveService.HudText(s, GameClock.Ticks);
            CombatSite site = Site;
            List<int> units0 = GroupUnits(key);
            int pods = units0.Count(x => site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling);
            int cargo0 = CombatSite.PodCargoSpawned;
            // 测试捷径：先清掉随舱一起到的机动单位（这里不考防守强度），只看舱落地投放。
            foreach (int x in units0)
            {
                if (!(site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling))
                {
                    site.Kernel.Kill(x, 0);
                }
            }
            double kt0 = site.Kernel.Time;
            // 等到舱全部落地（按内核时间：首次反应会慢放，同样的世界步数里内核时间可能走得少）。
            F.StepUntil(() => GroupUnits(key).All(x => !(site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling)) && site.Kernel.Time - kt0 >= 4.0, 60);
            List<int> units1 = GroupUnits(key);
            int stillFalling = units1.Count(x => site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling);
            int podsAlive = units0.Count(x => units1.Contains(x) && site.Kernel.TryGetFleet(x, out _));
            string diag = $"（诊断：内核走了 {site.Kernel.Time - kt0:F2} 秒，仍在下落 {stillFalling}、舱仍在 {podsAlive}，队伍 {units0.Count} → {units1.Count}）";
            var fresh = units1.Except(units0).ToList();
            bool sameGroup = fresh.Count >= pods * 4 && fresh.All(x => site.DamageSourceOf(x, s).Contains("enemy_purge_drone"));
            Expect(pods >= 1 && CombatSite.PodCargoSpawned - cargo0 >= pods * 4 && sameGroup && hudActive.Contains($"第 {wave}/3 波来袭"),
                $"B3 第 {wave} 波到达：{units0.Count} 台展开、其中登陆舱 {pods} 个在下落；落地后投放 {CombatSite.PodCargoSpawned - cargo0} 台、都进同一支攻城队伍（{fresh.Count} 台清洗无人机：{sameGroup}；攻城分支）；顶栏“{hudActive}”" + (fresh.Count >= pods * 4 && pods >= 1 ? string.Empty : diag));
            F.StepUntil(() =>
            {
                List<int> left = GroupUnits(key);
                foreach (int x in left)
                {
                    site.Kernel.Kill(x, 0);
                }
                return left.Count == 0;
            }, 120);
            bool cleared = F.StepUntil(() => st.Cleared == wave, 60);
            bool note = Notified("purge.notify.wave_cleared", out string text) && text.Contains($"{wave}/3");
            Expect(cleared && note && st.ClearTicks[wave - 1] >= arrive && (p.State >= RaidDirectorService.StateEnded || RaidDirectorService.FindPlan(s, p.PlanId) == null),
                $"B4 第 {wave} 波全歼 → 导演计划结束（{p.EndReason}）→ 清洗记打完 {st.Cleared}/3、通知“{text}”"
                + (cleared ? string.Empty : $"（诊断：核心被毁 {HomeValleySoftlockGuard.IsCoreDestroyed(s)}、计划 {RaidDirectorService.FindPlan(s, p.PlanId)?.State}、清洗计划 {st.ActivePlanId}）"));
            return cleared;
        }

        // ─────────────────────────────── D 轨道打击 ───────────────────────────────

        private static void CheckStrike()
        {
            CampaignState s = NewWorld(13032);
            Reveal(s);
            WorldSimulation.StepMany(1);
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            if (st.NextStrikeTick - 1 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, st.NextStrikeTick - 1 - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            CombatSite site = Site;
            int idx = FindStrike(site, st.LastStrikeId);
            if (idx < 0 || st.StrikeCount != 1)
            {
                Fail($"D1 第一次轨道打击没有排进家园内核（次数 {st.StrikeCount}，编号 {st.LastStrikeId}）");
                return;
            }
            CombatFleetStrike x = site.Kernel.FleetStrikeAt(idx);
            double left = x.At - site.Kernel.Time;
            var at = new Vector2(st.LastStrikeX, st.LastStrikeY);
            Vector2 core = CoreCenter(s);
            var model = new GameLogic.UI.Kit.WorldMapModel();
            GameLogic.UI.Kit.WorldMapFilters.ResetAll();
            model.Collect(s, new GameLogic.UI.Kit.WorldMapView { CenterX = at.x, CenterY = at.y, HalfWidth = 40f, CanvasWidth = 400f, CanvasHeight = 400f }, 400);
            GameLogic.UI.Kit.WorldMapCircle circle = model.Circles.FirstOrDefault(c => c.Impact && Math.Abs(c.X - at.x) < 0.1 && Math.Abs(c.Y - at.y) < 0.1);
            bool mapped = circle.Impact && circle.Label != null && (circle.Label.Contains("30") || circle.Label.Contains("29"));
            bool note = Notified("purge.notify.strike", out string ntext, out bool located) && located;
            float clear = (at - core).magnitude;
            BuildingRecord target = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore && Inside(b, at));
            float hp0 = target?.Health ?? -1f;
            string hud = PurgeWaveService.HudText(s, GameClock.Ticks);
            Expect(left > 29.5 && left <= 30.01 && Math.Abs(x.Radius - PurgeWaveService.StrikeRadius) < 1e-3f && mapped && note && clear >= PurgeWaveService.StrikeCoreClearance - 0.01f && target != null && hud.Contains("轨道打击"),
                $"D1 轨道打击落点提前 {left:F2} 秒标出：地图落点圈“{circle.Label}”（{mapped}）、通知“{ntext}”；落点 {target?.BuildingTypeId} 离核心 {clear:F1} 米（≥ {PurgeWaveService.StrikeCoreClearance}）；顶栏“{hud}”");

            F.Seconds(29f);
            bool pending = FindStrike(site, st.LastStrikeId) >= 0 && Math.Abs((HomeGridService.FindBuilding(s, target.BuildingId)?.Health ?? -1f) - hp0) < 0.01f;
            F.Seconds(1.5f);
            float hp1 = HomeGridService.FindBuilding(s, target.BuildingId)?.Health ?? 0f;
            bool landed = FindStrike(site, st.LastStrikeId) < 0 && hp1 < hp0;
            Expect(pending && landed, $"D2 打击在第 29 秒还没落（{pending}）、第 30.5 秒落下：{target.BuildingTypeId} 耐久 {hp0:F0} → {hp1:F0}；下一次排在 {PurgeWaveService.StrikeIntervalSeconds} 秒后（{st.NextStrikeTick - GameClock.Ticks > 0}）");

            // 与建筑记录的排列顺序无关（存读档后记录按 ID 重排）；没有可打的建筑时落在核心外 clearance 米。
            PurgeWaveService.PickStrikeTarget(s, 5, out Vector2 a1, out string l1);
            BuildingRecord[] orig = s.BuildingRecords;
            s.BuildingRecords = orig.Reverse().ToArray();
            PurgeWaveService.PickStrikeTarget(s, 5, out Vector2 a2, out string l2);
            s.BuildingRecords = orig.Where(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore).ToArray();
            PurgeWaveService.PickStrikeTarget(s, 5, out Vector2 a3, out string l3);
            s.BuildingRecords = orig;
            float d3 = (a3 - core).magnitude;
            Expect((a1 - a2).sqrMagnitude < 1e-6f && l1 == l2 && Math.Abs(d3 - PurgeWaveService.StrikeCoreClearance) < 0.6f && l3 == GameText.Get("purge.strike.open_ground"),
                $"D3 落点与建筑记录顺序无关（{l1} = {l2}）；家园只剩核心时落在核心外 {d3:F1} 米的“{l3}”（负向：不直接轰核心）");

            // 负向：规划虚影（还没开工）不当落点。
            var states = orig.Select(b => b?.ConstructionState ?? BuildingConstructionState.Destroyed).ToArray();
            foreach (BuildingRecord b in orig)
            {
                if (b != null && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore)
                {
                    b.ConstructionState = BuildingConstructionState.Planned;
                }
            }
            PurgeWaveService.PickStrikeTarget(s, 5, out _, out string l4);
            for (int i = 0; i < orig.Length; i++)
            {
                if (orig[i] != null)
                {
                    orig[i].ConstructionState = states[i];
                }
            }

            // 撑过第 3 波时还在倒计时的那一次打击一并撤掉；顶栏打击倒计时与通知 / 落点圈同一单位（秒）。
            GameClock.SkipForTests(s, Math.Max(0, st.NextStrikeTick - GameClock.Ticks));
            WorldSimulation.StepMany(2);
            int pendingId = st.LastStrikeId;
            bool pendingNow = FindStrike(site, pendingId) >= 0;
            double lands = PurgeWaveService.NextStrikeLandsIn(s, st, GameClock.Ticks);
            string hud4 = PurgeWaveService.HudText(s, GameClock.Ticks);
            bool hudSec = hud4.Contains($"轨道打击 {Math.Ceiling(lands):0} 秒后");
            st.Launched = PurgeWaveService.WaveCount;
            st.Cleared = PurgeWaveService.WaveCount;
            WorldSimulation.StepMany(1);
            bool cancelled = st.DoneTick >= 0 && FindStrike(site, pendingId) < 0;
            Expect(l4 == GameText.Get("purge.strike.open_ground") && pendingNow && hudSec && cancelled,
                $"D4 负向：建筑都只是规划虚影时不锁定它们（落到“{l4}”）；顶栏“{hud4}”与通知同用秒（{hudSec}）；撑过全部波次时还在倒计时的打击 {pendingId} 被撤掉（{pendingNow} → {cancelled}）");
        }

        // ─────────────────────────────── E 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(13033);
                Reveal(s);
                WorldSimulation.StepMany(1);
                PurgeWaveState st = PurgeWaveService.StateOf(s);
                long target = PurgeWaveService.EarliestLaunchTick(st, 0);
                GameClock.SkipForTests(s, target - 2 * GameClock.StepHz - GameClock.Ticks);
                long t0 = GameClock.Ticks;
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool paused = GameClock.Ticks == t0 && st.Launched == 0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 20000 && GameClock.Ticks <= target + 1; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                bool exact = st.Launched == 1 && st.LaunchTicks[0] == target;
                ok &= paused && exact;
                rows.Add($"{speed}x 暂停 6 秒不走（{paused}）、在第 {st.LaunchTicks[0]} 步发动（应 {target}）");
            }
            Expect(ok, "E1 清洗波次按游戏时间：" + string.Join("；", rows));
        }

        // ─────────────────────────────── F 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(13034);
            Reveal(s);
            WorldSimulation.StepMany(1);
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            GameClock.SkipForTests(s, st.NextStrikeTick + 2 * GameClock.StepHz - GameClock.Ticks);
            WorldSimulation.StepMany(2);
            bool pendingAtSave = FindStrike(Site, st.LastStrikeId) >= 0 && st.Launched == 1;
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long t0 = GameClock.Ticks;
            string sig0 = PurgeSig(s);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            string sigA = PurgeSig(s);
            long t1 = GameClock.Ticks;
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("F1 读档失败：" + fail);
                return;
            }
            string sigL = PurgeSig(l);
            WorldSimulation.StepMany((int)(t1 - GameClock.Ticks));
            string sigB = PurgeSig(l);
            Expect(r.Success && pendingAtSave && GameClock.Ticks == t1 && sigL == sig0 && sigA == sigB,
                $"F1 存读档：存档时第 1 波已发动、一次轨道打击还在倒计时（{pendingAtSave}）；读回的清洗状态与打击倒计时 = 存档时（{sigL == sig0}），再走 20 秒与不读档逐项一致（{sigA == sigB}）"
                + (sigA != sigB ? $"\n不读档：{sigA}\n读档后：{sigB}" : string.Empty) + (sigL != sig0 ? $"\n存档时：{sig0}\n读回：{sigL}" : string.Empty));
        }

        // ─────────────────────────────── G 观察一致 ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observe)
            {
                CampaignState s = NewWorld(13035, observe);
                Reveal(s);
                WorldSimulation.StepMany(1);
                PurgeWaveState st = PurgeWaveService.StateOf(s);
                GameClock.SkipForTests(s, PurgeWaveService.EarliestLaunchTick(st, 0) - 1 - GameClock.Ticks);
                WorldSimulation.StepMany(GameClock.StepHz * 40);
                return PurgeSig(s);
            }
            string a = Run(true);
            string b = Run(false);
            Expect(a == b, $"G1 家园不被观察时清洗波次与轨道打击的结果与观察时一致（{a.Length} 字签名）" + (a != b ? $"\n观察：{a}\n不观察：{b}" : string.Empty));
        }

        // ─────────────────────────────── H 建造者难度、旧档 ───────────────────────────────

        private static void CheckBuilderAndLegacy()
        {
            CampaignState s = NewWorld(13036);
            s.DifficultyId = "Builder";
            Reveal(s);
            WorldSimulation.StepMany(1);
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            GameClock.SkipForTests(s, PurgeWaveService.EarliestLaunchTick(st, 0) - GameClock.Ticks);
            WorldSimulation.StepMany(4);
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            Expect(DifficultyService.StoryOnly(s) && p != null && p.Trigger == RaidCatalog.TriggerPurge,
                $"H1 建造者难度（只有剧情突袭）：清洗波次照样来（计划 {p?.PlanId} 触发 {p?.Trigger}）");

            CheckMidGameBuilderAndCancel();

            var old = new CampaignState();
            old.Progress = new CampaignProgressState { Purge = null };
            CampaignFgStateDomains.EnsureAll(old);
            PurgeWaveState o = old.Progress.Purge;
            Expect(o != null && o.StartTick < 0 && o.Launched == 0 && o.LaunchTicks.Length == 3 && o.ClearTicks.Length == 3 && PurgeWaveService.RetryWave(old) == 0
                   && PurgeWaveService.FailureRetryText(old) == string.Empty,
                "H2 旧存档没有清洗状态 = 还没开始（揭示过的旧档载入后下一步开始，B1 同一入口）；失败页不加清洗那一行");
        }

        /// <summary>
        /// H3 负向（复审 P1）：第 1 波还在筹备 / 已排定（没预警）时在暂停菜单把难度改成建造者 → 清洗计划不取消、不记打完；
        /// H4 负向：计划被导演取消（派不出队伍时 Plan.cs 写的同一组状态）→ 这一波不计入打完、不授予，隔 wave_gap 秒重新发动。
        /// </summary>
        private static void CheckMidGameBuilderAndCancel()
        {
            CampaignState s = NewWorld(13037);
            Reveal(s);
            WorldSimulation.StepMany(1);
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            GameClock.SkipForTests(s, PurgeWaveService.EarliestLaunchTick(st, 0) - GameClock.Ticks);
            WorldSimulation.StepMany(4);
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            if (p == null || p.State >= RaidDirectorService.StateWarned)
            {
                Fail($"H3 夹具：第 1 波计划没在预警前（计划 {st.ActivePlanId} 状态 {p?.State}）");
                return;
            }
            int before = RaidDirectorService.StoryOnlyCancelCount(p);
            DifficultyChangeResult res = DifficultyService.Change(s, DifficultyService.Preset("Builder"), out DifficultyChangeRecord rec);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool kept = DifficultyService.StoryOnly(s) && before == 0 && p.State != RaidDirectorService.StateCancelled && p.EndReason != RaidDirectorService.EndCancelled
                        && st.Launched == 1 && st.Cleared == 0 && st.ActivePlanId == p.PlanId && st.ClearTicks[0] < 0;
            Expect(kept, $"H3 负向：第 1 波筹备中改成建造者（{res}，取消 {rec?.CancelledRaids} 波）→ 清洗计划 {p.PlanId} 照常（状态 {p.State}），没有记打完（已发动 {st.Launched} / 打完 {st.Cleared}）");

            int retry0 = PurgeWaveService.RetryCount;
            p.State = RaidDirectorService.StateCancelled;
            p.EndReason = RaidDirectorService.EndCancelled;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool notCleared = st.Cleared == 0 && st.Launched == 0 && st.ClearTicks[0] < 0 && st.RetryTick > GameClock.Ticks && PurgeWaveService.RetryCount == retry0 + 1
                              && !CampaignEventLedger.Contains(s, PurgeWaveService.ClearedEventId) && PurgeWaveService.HudText(s, GameClock.Ticks).Contains("0/3");
            long retryAt = st.RetryTick;
            GameClock.SkipForTests(s, Math.Max(0, retryAt - GameClock.Ticks));
            WorldSimulation.StepMany(4);
            RaidPlanRecord p2 = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            bool relaunched = st.Launched == 1 && st.Cleared == 0 && p2 != null && p2.PlanId != p.PlanId && p2.State != RaidDirectorService.StateCancelled && p2.StoryId == PurgeWaveService.WaveId(1);
            Expect(notCleared && relaunched,
                $"H4 负向：第 1 波计划被取消 → 不计入打完（打完 {st.Cleared}、不授予）、第 {retryAt} 步重新发动（{notCleared}）；重新发动后跟踪新计划 {p2?.PlanId}（{relaunched}）");
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private static void Reveal(CampaignState s)
        {
            // 测试捷径：揭示状态（事件账本 endgame_revealed）直接写入——揭示本身的正式流程由 FgRevealSelfCheck 覆盖；这里补齐它会推进的主线任务。
            s.Progress.Act = 3;
            if (CampaignObjectiveTracker.FindQuest(s, "main.homecoming") == null)
            {
                // 主线前置链（返航工程）记为已完成，主线“守住家园”按正式的前置规则出现。
                var list = new List<ObjectiveRecord>(s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                {
                    new ObjectiveRecord { ObjectiveId = "main.homecoming", DefId = "main.homecoming", State = ObjectiveState.Completed, EventId = string.Empty },
                };
                s.ObjectiveRecords = list.ToArray();
            }
            CampaignEventLedger.TryGrant(s, RevealService.RevealedEventId, "Endgame", s.PlaySeconds, "test");
            CampaignObjectiveTracker.Recompute(s);
        }

        private static string PurgeSig(CampaignState s)
        {
            PurgeWaveState st = PurgeWaveService.StateOf(s);
            var sb = new StringBuilder();
            sb.Append($"T{GameClock.Ticks}|S{st.StartTick},L{st.Launched},C{st.Cleared},A{st.ActivePlanId},N{st.NextStrikeTick},K{st.StrikeCount},I{st.LastStrikeId},{st.LastStrikeX:F2},{st.LastStrikeY:F2},R{st.SavedRetryWave},LC{st.LastClearTick},RT{st.RetryTick}");
            sb.Append("|LT").Append(string.Join(",", st.LaunchTicks)).Append("|CT").Append(string.Join(",", st.ClearTicks));
            foreach (RaidPlanRecord p in RaidDirectorService.Plans(s).Where(x => x != null && x.Trigger == RaidCatalog.TriggerPurge))
            {
                sb.Append($"|P{p.PlanId},{p.State},{p.StoryId},{p.Level},{p.OriginX},{p.OriginY},{p.WarnTick},{p.ArrivalTick},{string.Join("/", p.UnitIds)}:{string.Join("/", p.UnitCounts)}");
            }
            CombatSite site = Site;
            for (int i = 0; site != null && i < site.Kernel.FleetStrikeCount; i++)
            {
                CombatFleetStrike x = site.Kernel.FleetStrikeAt(i);
                sb.Append($"|K{x.Id},{x.At:F3},{x.Center.x:F2},{x.Center.y:F2},{x.Radius:F2}");
            }
            return sb.ToString();
        }

        private static int FindStrike(CombatSite site, int id)
        {
            for (int i = 0; site != null && i < site.Kernel.FleetStrikeCount; i++)
            {
                if (site.Kernel.FleetStrikeAt(i).Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool Inside(BuildingRecord b, Vector2 p)
        {
            StandingRuleService.BoundsOf(b, out GridCell min, out GridCell max);
            return p.x >= min.X && p.x <= max.X + 1 && p.y >= min.Y && p.y <= max.Y + 1;
        }

        private static Vector2 CoreCenter(CampaignState s)
        {
            BuildingRecord core = s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            StandingRuleService.BoundsOf(core, out GridCell min, out GridCell max);
            return new Vector2((min.X + max.X + 1) * 0.5f, (min.Y + max.Y + 1) * 0.5f);
        }

        private static string QuestState(CampaignState s, string id) => CampaignObjectiveTracker.FindQuest(s, id)?.State.ToString() ?? "无";

        /// <summary>同类通知会按真实时间聚合（自检里跳时间，几条会并成一条）：逐条成员找细节文字。</summary>
        private static bool Notified(string key, out string text) => Notified(key, out text, out _);

        private static bool Notified(string key, out string text, out bool located)
        {
            string prefix = GameText.Get(key);
            int cut = prefix.IndexOf('{');
            prefix = cut > 0 ? prefix.Substring(0, cut) : prefix;
            NotificationMember hit = null;
            foreach (NotificationEntry n in NotificationCenter.History)
            {
                if (n?.Type == null || n.Type.Id != PurgeWaveService.NotifyType)
                {
                    continue;
                }
                foreach (NotificationMember m in n.Members)
                {
                    if (m?.DetailText != null && m.DetailText.Contains(prefix))
                    {
                        hit = m;
                    }
                }
            }
            text = hit?.DetailText ?? string.Empty;
            located = hit != null && hit.HasLocation;
            return hit != null;
        }

        private static List<int> GroupUnits(int key)
        {
            var list = new List<int>();
            CombatKernel k = Site?.Kernel;
            for (int i = 0; k != null && i < k.SlotCount; i++)
            {
                CombatUnitView v = k.ViewAt(i);
                if (v.Alive && v.Siege.Role != 0 && v.Siege.Group == key)
                {
                    list.Add(v.Id);
                }
            }
            return list;
        }

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                fail = rr.Message;
                return null;
            }
            fail = null;
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return rr.State;
        }

        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgpurge-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgPurgeWaveSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
