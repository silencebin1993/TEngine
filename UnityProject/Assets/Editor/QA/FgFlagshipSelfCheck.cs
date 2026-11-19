using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-05 先遣旗舰“归乡号”（FG11 FGR-END-022、FG19 FGR-EMG-161 / 165；验收 FGT-END-006 / 007、FGT-EMG-061）：起真实世界（新战役 + 星球战斗内核 + 存档）断言——
    /// 切断投送后开战；阶段一延时轰炸（落点散布随摧毁的校准信标变大、旗舰不在场上）；阶段二要塞 + 四类护盾节点（每类只被 FGR-EMG-161 表里一族端口削减、其余端口无效的对照矩阵）；
    /// 阶段三根权限每 20 秒夺取离中继最近的机器（共用夺控状态机、不夺取玩家所在的那台、击倒瘫痪不阵亡、反向接入夺回、反夺控广播）；中继全毁核心暴露、摧毁核心击退；
    /// 每阶段开始自动存档；负向：只有 1 台机器、全灭后从阶段开始重试、核心被毁读档从阶段开始；暂停与 0.5x～3x；存读档；观察一致；性能。
    /// </summary>
    public static class FgFlagshipSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string BpGun = "bp_selfcheck_flagship_gun";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _phaseEvents;
        private static int _defeatedEvents;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("QA/自检/FG 先遣旗舰")]
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
            PerfLines.Clear();
            Line("\n[先遣旗舰] 三阶段：校准信标轰炸、四类护盾节点（端口闸门）、根权限压制；每阶段自动存档、全灭重试、只有 1 台机器（FG13-END-05）");
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
            Func<SaveResult> originalLandSave = BeachheadService.LandedSaveHandler;
            Func<SaveResult> originalPhaseSave = FlagshipService.PhaseSaveHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgflag-selfcheck-" + Guid.NewGuid().ToString("N"));
            GameEvent.AddEventListener(FlagshipService.PhaseEvent, OnPhase);
            GameEvent.AddEventListener(FlagshipService.DefeatedEvent, OnDefeated);
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
                BeachheadService.LandedSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.BeachheadLanded, toast: false);
                FlagshipService.PhaseSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.FlagshipPhase, toast: false);
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckSeeds);
                Step(CheckOrbitalAndFortress);
                Step(CheckGateMatrix);
                Step(CheckRootAndFinish);
                Step(CheckFailsafe);
                Step(CheckOneMachine);
                Step(CheckWipeRetry);
                Step(CheckCoreLostRetry);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance);
            }
            finally
            {
                GameEvent.RemoveEventListener(FlagshipService.PhaseEvent, OnPhase);
                GameEvent.RemoveEventListener(FlagshipService.DefeatedEvent, OnDefeated);
                BeachheadService.LandedSaveHandler = originalLandSave;
                FlagshipService.PhaseSaveHandler = originalPhaseSave;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                SignalSeizeService.ResetForTests();
                SignalUplinkService.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
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
            foreach (string p in PerfLines)
            {
                Line("  [性能] " + p);
            }
            Line(_fail == 0 ? $"  先遣旗舰：全部 {_pass} 项通过" : $"  先遣旗舰：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static void OnPhase() => _phaseEvents++;
        private static void OnDefeated() => _defeatedEvents++;

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            List<EnemyEncounter> b = FlagshipService.Encounters(EnemyDefs.ConditionBase);
            List<EnemyEncounter> f = FlagshipService.Encounters(EnemyDefs.ConditionBoss);
            List<EnemyEncounter> r = FlagshipService.Encounters(EnemyDefs.ConditionBossWave);
            var fams = f.Where(FlagshipService.IsNodeRow).Select(e => EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) ? EnemyFleetParams.GateFamily(row) : (byte)0).OrderBy(x => x).ToArray();
            bool fourFams = fams.SequenceEqual(new[] { CombatFleetConst.GateEm, CombatFleetConst.GateFluid, CombatFleetConst.GateFuse, CombatFleetConst.GateLimiter });
            int cores = f.Count(FlagshipService.IsCoreRow);
            // 只有节点行带闸门：其余敌人行（包括普通白潮单位）都是 none（闸门按表列，不认 ID）
            int gated = EnemyDefs.Rows.Count(x => EnemyFleetParams.GateFamily(x) != CombatFleetConst.GateNone);
            string[] keys =
            {
                "flagship.phase.1", "flagship.phase.2", "flagship.phase.3", "flagship.phase.4", "flagship.notify.phase.1", "flagship.notify.phase.2", "flagship.notify.phase.3",
                "flagship.notify.phase.4", "flagship.notify.defeated", "flagship.notify.wipe", "flagship.notify.part_down", "flagship.notify.no_target", "flagship.notify.bomb",
                "flagship.hud.1", "flagship.hud.2", "flagship.hud.3", "flagship.hud.4", "flagship.hud.done", "flagship.hud.node", "flagship.hud.node_down",
                "flagship.weak.em", "flagship.weak.fluid", "flagship.weak.fuse", "flagship.weak.limiter", "flagship.invuln.nodes", "flagship.invuln.relays", "flagship.invuln.gate",
                "quest.prog.flagship", "failure.flagship_retry", "quest.main.flagship.title", "quest.main.flagship.goal", "enemy.flagship.name", "enemy.calib_beacon.name",
                "enemy.suppress_relay.name", "enemy.node_em.name", "enemy.node_fluid.name", "enemy.node_fuse.name", "enemy.node_limiter.name", "enemy.flagship_battery.name",
            };
            var missing = keys.Where(k => !GameText.Has(k)).ToList();
            var q = QuestCatalog.Get("main.flagship");
            Expect(b.Count == 4 && r.Count == 3 && cores == 1 && f.Count(FlagshipService.IsNodeRow) == 4 && f.Count(e => !FlagshipService.IsNodeRow(e) && !FlagshipService.IsCoreRow(e)) >= 2
                   && fourFams && gated == 4 && Mathf.Approximately(FlagshipService.SeizeInterval, 20f) && FlagshipService.BombWarn >= 0.8f && q != null && missing.Count == 0,
                $"A1 数据：校准信标 {b.Count}、要塞 {cores} + 炮位、护盾节点四类（{string.Join(",", fams)}）、压制中继 {r.Count}；带闸门的敌人行只有 {gated} 行；夺取间隔 {FlagshipService.SeizeInterval} 秒；" +
                $"主线 main.flagship {(q != null)}；文本缺 {missing.Count}：{string.Join(",", missing)}");
        }

        // ─────────────────────────────── B25 多种子 ───────────────────────────────

        private static void CheckSeeds()
        {
            var bad = new List<string>();
            int[] seeds = { 13051, -11, 990023 };
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed, observe: false);
                WorldPlan plan = WorldGenService.PlanFor(s);
                PlannedTerritory t = plan?.Find("white_tide");
                if (t == null || !FlagshipService.TryZone(s, out Vector2 zone))
                {
                    bad.Add(seed + " 没有预留区 / 降落区");
                    continue;
                }
                foreach (EnemyEncounter e in FlagshipService.AllEncounters())
                {
                    if (!BeachheadService.TryPoint(s, e.AnchorId, out Vector2 p))
                    {
                        bad.Add($"{seed}:{e.Id} 点位解析不了");
                        continue;
                    }
                    if (Vector2.Distance(p, new Vector2(t.CenterX, t.CenterY)) > t.Radius || Vector2.Distance(p, zone) > FlagshipService.ArenaRadius)
                    {
                        bad.Add($"{seed}:{e.Id} 不在预留区 / 竞技场里");
                    }
                    if (!WorldGenQuery.IsWalkableGenerated(s, Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)))
                    {
                        bad.Add($"{seed}:{e.Id} 在悬崖 / 水面上");
                    }
                }
            }
            Expect(bad.Count == 0, $"B0 B25：{seeds.Length} 个种子（含负数）下全部 {FlagshipService.AllEncounters().Count()} 个旗舰点位按规划层锚点落在白潮滩头预留区与竞技场里、在可走格上" +
                                   (bad.Count > 0 ? "：" + string.Join("；", bad.Take(8)) : string.Empty));
        }

        // ─────────────────────────────── B / C 阶段一、阶段二 ───────────────────────────────

        private static void CheckOrbitalAndFortress()
        {
            CampaignState s = Start(13052, true, 3, out List<int> m, cut: false);
            CombatSite site = Site;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool before = !FlagshipService.Started(s) && Records(s).Count == 0;
            int saves0 = FlagshipService.PhaseSaves;
            _phaseEvents = 0;
            CutBeachhead(s);
            StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseOrbital, 4);
            WorldSimulation.StepMany(2);
            List<RegionEnemyRecord> recs = Records(s);
            int beaconsIn = FlagshipService.Encounters(EnemyDefs.ConditionBase).Count(e => InKernel(e.Id));
            string hud = FlagshipService.HudText(s, GameClock.Ticks);
            ObjectiveRecord q = CampaignObjectiveTracker.FindQuest(s, "main.flagship");
            string prog = q != null ? CampaignObjectiveTracker.ProgressText(s, q) : string.Empty;
            bool map = MapModel(s).Items.Any(i => i.Id == "flagship:fs_beacon_1");
            Expect(before && FlagshipService.PhaseOf(s) == 1 && recs.Count == 4 && beaconsIn == 4 && FlagshipService.Find(s, "fs_core") == null && FlagshipService.PhaseSaves == saves0 + 1
                   && s.LastSaveReason == SaveReason.FlagshipPhase && _phaseEvents == 1 && Notified("flagship.notify.phase.1") && hud.Contains("4/4") && q != null && q.State != ObjectiveState.Completed
                   && prog.Contains("0/4") && map,
                $"B1 切断投送之前没有开战（{before}）；之后下一步进入阶段一：4 座校准信标（{beaconsIn} 个在内核）、旗舰不在场上无法攻击（没有 fs_core 记录）；阶段开始自动存档（{s.LastSaveReason}）、" +
                $"阶段事件 {_phaseEvents}、阶段转换通知；首领 HUD“{hud}”；主线“{prog}”；地图标出信标 {map}");

            // B2 延时轰炸：落点在竞技场里离降落区最近的机器附近，散布 = base
            int bombs0 = FlagshipService.BombsScheduled;
            StepSec(FlagshipService.BombFirst + 0.5f);
            Vector2 near = NearestMachine(s, FlagshipService.LastBombAt);
            float off1 = Vector2.Distance(near, FlagshipService.LastBombAt);
            bool strike = site.Kernel.FleetStrikeCount > 0;
            Expect(FlagshipService.BombsScheduled == bombs0 + 1 && Mathf.Approximately(FlagshipService.LastScatter, FlagshipService.ScatterBase) && off1 <= FlagshipService.ScatterBase + 0.05f && strike
                   && Notified("flagship.notify.bomb"),
                $"B2 阶段一延时轰炸：开始 {FlagshipService.BombFirst} 秒后标出第一次（内核轨道打击排队 {strike}、倒计时 {FlagshipService.BombWarn} 秒），散布 {FlagshipService.LastScatter} 米、落点离最近的机器 {off1:0.##} 米");
            // B3 摧毁 2 座信标 → 散布变大
            Kill(site, "fs_beacon_1");
            Kill(site, "fs_beacon_2");
            StepUntil(() => FlagshipService.BombsScheduled == bombs0 + 2, FlagshipService.BombInterval + 1f);
            float want = FlagshipService.ScatterBase + 2 * FlagshipService.ScatterPerBeacon;
            float off2 = Vector2.Distance(NearestMachine(s, FlagshipService.LastBombAt), FlagshipService.LastBombAt);
            hud = FlagshipService.HudText(s, GameClock.Ticks);
            Expect(FlagshipService.BombsScheduled == bombs0 + 2 && Mathf.Approximately(FlagshipService.LastScatter, want) && off2 <= want + 0.05f && hud.Contains("2/4") && Notified("flagship.notify.part_down"),
                $"B3 每摧毁一座信标轰炸更不准：2 座被毁后散布 {FlagshipService.LastScatter} 米（应 {want}），落点离机器 {off2:0.##} 米；HUD“{hud}”；击毁通知");

            // C1 信标全毁 → 阶段二
            saves0 = FlagshipService.PhaseSaves;
            Kill(site, "fs_beacon_3");
            Kill(site, "fs_beacon_4");
            StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseFortress, 3);
            WorldSimulation.StepMany(2);
            int fortIn = FlagshipService.Encounters(EnemyDefs.ConditionBoss).Count(e => InKernel(e.Id));
            int total = FlagshipService.Encounters(EnemyDefs.ConditionBoss).Count;
            site.TryGetEnemyUnit("fs_core", out int core);
            float h0 = Health(core);
            site.Kernel.Damage(core, 500f, 0);
            WorldSimulation.StepMany(1);
            float h1 = Health(core);
            RegionEnemyRecord coreRec = FlagshipService.Find(s, "fs_core");
            string coreWhy = FlagshipService.InvulnerableDetail(s, coreRec);
            string nodeWhy = FlagshipService.InvulnerableDetail(s, FlagshipService.Find(s, "fs_node_fuse"));
            hud = FlagshipService.HudText(s, GameClock.Ticks);
            bool weak = new[] { "flagship.weak.em", "flagship.weak.fluid", "flagship.weak.fuse", "flagship.weak.limiter" }.All(k => hud.Contains(GameText.Get(k)));
            Expect(FlagshipService.PhaseOf(s) == 2 && fortIn == total && FlagshipService.PhaseSaves == saves0 + 1 && h0 > 0f && Mathf.Approximately(h0, h1) && coreWhy.Contains("4/4")
                   && nodeWhy.Contains(GameText.Get("flagship.weak.fuse")) && weak && Notified("flagship.notify.phase.2") && FlagshipService.Find(s, "fs_relay_1") == null,
                $"C1 4 座信标全毁 → 阶段二·登陆要塞：要塞 + 炮位 + 四个节点 {fortIn}/{total} 在内核、阶段开始自动存档；节点在时要塞不可伤（耐久 {h0:0} → {h1:0}，悬停“{coreWhy}”）；" +
                $"节点悬停写弱点“{nodeWhy}”；HUD 逐个列出节点护盾与弱点 {weak}：“{hud}”");
        }

        // ─────────────────────────────── C2 端口闸门对照矩阵（FGT-EMG-061）───────────────────────────────

        private static readonly string[] NodeIds = { "fs_node_em", "fs_node_fluid", "fs_node_fuse", "fs_node_limiter" };
        private static readonly byte[] NodeFams = { CombatFleetConst.GateEm, CombatFleetConst.GateFluid, CombatFleetConst.GateFuse, CombatFleetConst.GateLimiter };
        private static readonly string[] StimNames = { "电荷 en.elec", "磁化 en.mag", "场 fld.pull", "物质 mat.water", "子载荷 grm.trig", "积热 en.heat", "直接伤害" };
        private static readonly byte[] StimFams =
        {
            CombatFleetConst.GateEm, CombatFleetConst.GateEm, CombatFleetConst.GateEm, CombatFleetConst.GateFluid, CombatFleetConst.GateFuse, CombatFleetConst.GateLimiter, CombatFleetConst.GateNone,
        };

        private static void CheckGateMatrix()
        {
            CampaignState s = Start(13053, true, 2, out _);
            ToPhase(s, FlagshipService.PhaseFortress);
            CombatSite site = Site;
            CombatKernel k = site.Kernel;
            int shooter = SpawnShooter(k, Pos("fs_node_em") + new Vector2(0f, -30f), out _);
            var rows = new List<string>();
            bool ok = true;
            // 先做全部“错族”刺激（含直接伤害），节点闸门与耐久都不得变化；再做本族刺激，闸门下降、耐久不变。
            for (int pass = 0; pass < 2; pass++)
            {
                for (int n = 0; n < NodeIds.Length; n++)
                {
                    for (int st = 0; st < StimNames.Length; st++)
                    {
                        bool match = StimFams[st] == NodeFams[n];
                        if (match != (pass == 1))
                        {
                            continue;
                        }
                        if (!site.TryGetEnemyUnit(NodeIds[n], out int u) || !k.TryGetGate(u, out _, out float g0, out float max))
                        {
                            ok = false;
                            rows.Add($"{NodeIds[n]} 不在场 / 没有闸门");
                            continue;
                        }
                        float hp0 = Health(u);
                        string why = Stimulate(k, site, st, u, NodeIds[n], shooter);
                        k.TryGetGate(u, out _, out float g1, out _);
                        float hp1 = Health(u);
                        bool cell = match ? g1 < g0 - 0.5f && Mathf.Approximately(hp0, hp1) && k.IsAlive(u) : Mathf.Approximately(g0, g1) && Mathf.Approximately(hp0, hp1);
                        if (!cell || match)
                        {
                            rows.Add($"{NodeIds[n]}×{StimNames[st]}：闸门 {g0:0.#} → {g1:0.#}/{max:0}、耐久 {hp0:0} → {hp1:0}{why}{(cell ? string.Empty : " ✗")}");
                        }
                        ok &= cell;
                    }
                }
            }
            Expect(ok, "C2 FGT-EMG-061 / FGR-EMG-161 端口闸门对照矩阵（4 节点 × 7 种刺激）：只有本族端口削减闸门、任何伤害都不扣节点耐久，其余端口无效；本族行：" + string.Join("；", rows));

            // C3 本族端口削到 0 → 当场击毁（击毁提示、击毁归属），全部破掉 → 阶段三
            int breaks0 = FleetCombatFeedback.GateBreakCues;
            site.TryGetEnemyUnit("fs_node_em", out int em);
            Vector2 ep = Pos("fs_node_em");
            k.SpawnField(EmgFieldConst.PortPull, ep.x, ep.y, 2.5f, 30f, shooter, CombatFaction.Player);
            bool broke = StepUntil(() => !k.IsAlive(em), 25);
            WorldSimulation.StepMany(GameClock.StepHz + 1);
            string hud = FlagshipService.HudText(s, GameClock.Ticks);
            Expect(broke && FlagshipService.Find(s, "fs_node_em")?.IsAlive == false && FleetCombatFeedback.GateBreakCues > breaks0 && hud.Contains(GameText.Format("flagship.hud.node_down", GameText.Get("enemy.node_em.name")))
                   && FlagshipService.PhaseOf(s) == FlagshipService.PhaseFortress,
                $"C3 电磁节点被敌对场持续覆盖、闸门削到 0 → 当场击毁（记录阵亡、破盾提示 {FleetCombatFeedback.GateBreakCues - breaks0}）；HUD 写“已破”；其余节点还在 = 仍是阶段二：“{hud}”");
            int saves0 = FlagshipService.PhaseSaves;
            foreach (string id in NodeIds.Skip(1))
            {
                Kill(site, id);
            }
            StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseRoot, 3);
            WorldSimulation.StepMany(2);
            int relaysIn = FlagshipService.Encounters(EnemyDefs.ConditionBossWave).Count(e => InKernel(e.Id));
            site.TryGetEnemyUnit("fs_core", out int core);
            float h0 = Health(core);
            k.Damage(core, 500f, 0);
            WorldSimulation.StepMany(1);
            string why2 = FlagshipService.InvulnerableDetail(s, FlagshipService.Find(s, "fs_core"));
            Expect(FlagshipService.PhaseOf(s) == 3 && relaysIn == 3 && FlagshipService.PhaseSaves == saves0 + 1 && Mathf.Approximately(h0, Health(core)) && why2.Contains("3/3") && Notified("flagship.notify.phase.3"),
                $"C4 四个节点全破 → 阶段三·根权限压制：3 座压制中继在内核、自动存档、要塞仍不可伤（“{why2}”）、阶段转换通知");
        }

        /// <summary>对节点 u 施加第 st 种刺激并走 2 秒；返回说明。</summary>
        private static string Stimulate(CombatKernel k, CombatSite site, int st, int u, string nodeId, int shooter)
        {
            Vector2 p = Pos(nodeId);
            switch (st)
            {
                case 0:
                    k.AddAccum(u, EmgPhaseConst.Charge, 0.4f, shooter);
                    break;
                case 1:
                    k.AddAccum(u, EmgPhaseConst.Magnetize, 0.4f, shooter);
                    break;
                case 2:
                    k.SpawnField(EmgFieldConst.PortPull, p.x, p.y, 2.5f, 1.5f, shooter, CombatFaction.Player);
                    break;
                case 3:
                    k.SpawnPatch(EmgMatterConst.Water, new double2(p.x, p.y), 2.5f, 1f, 1.5f, shooter);
                    break;
                case 4:
                {
                    long sub0 = k.EmgBusStats.SubSpawned;
                    int decoy = SpawnDecoy(k, p + new Vector2(0f, -2.5f));
                    int gun = SpawnShooter(k, p + new Vector2(0f, -10f), out int prog, "fw_split");
                    site.IssueCommand(gun, CombatCommandKind.Attack, p + new Vector2(0f, -2.5f), decoy, 0.5f, 14f, 0.5f, false);
                    StepSec(2f);
                    k.Kill(gun, 0);
                    k.Kill(decoy, 0);
                    WorldSimulation.StepMany(2);
                    return $"（子载荷 {k.EmgBusStats.SubSpawned - sub0} 份）";
                }
                case 5:
                    k.AddAccum(u, EmgPhaseConst.Overheat, 0.4f, shooter);
                    break;
                default:
                    k.Damage(u, 400f, shooter);
                    break;
            }
            StepSec(2f);
            return string.Empty;
        }

        // ─────────────────────────────── D / E 阶段三、核心暴露、击退 ───────────────────────────────

        private static void CheckRootAndFinish()
        {
            CampaignState s = Start(13054, true, 4, out List<int> m);
            ToPhase(s, FlagshipService.PhaseRoot);
            CombatSite site = Site;
            CombatKernel k = site.Kernel;
            // 玩家接入离中继最近的那台：根权限必须跳过它、夺取次近的
            List<int> order = ByRelayDistance(s, m);
            int mine = order[0];
            s.SignalCore.UplinkMachineLogicId = mine; // 测试捷径：信号在这台里（接入流程由 FgSignalUplinkSelfCheck 覆盖）
            int seize0 = FlagshipService.Seizes;
            StepSec(FlagshipService.SeizeFirst + 0.3f);
            int first = FlagshipService.StateOf(s).Seized.FirstOrDefault();
            bool firstOk = SignalSeizeService.TryGetSeize(first, out bool down0, out int src) && src == SignalSeizeService.SourceRoot && !down0 && first == order[1]
                           && FactionOf(first) == CombatFaction.Hostile && !SignalSeizeService.IsSeized(mine);
            string hud = FlagshipService.HudText(s, GameClock.Ticks);
            Expect(FlagshipService.Seizes == seize0 + 1 && firstOk && hud.Contains("已夺取 1 台"),
                $"D1 FGT-END-006 / 007 阶段三开始 {FlagshipService.SeizeFirst} 秒后根权限夺取一台：离中继最近的是玩家所在的那台（不夺取），夺取了次近的 {SignalPresence.MachineLabel(first)}（共用夺控状态机，来源 = 根权限、" +
                $"转为敌对）；HUD“{hud}”");
            StepSec(FlagshipService.SeizeInterval);
            int second = FlagshipService.StateOf(s).Seized.Skip(1).FirstOrDefault();
            Expect(FlagshipService.Seizes == seize0 + 2 && second == order[2] && SignalSeizeService.IsSeized(second) && !SignalSeizeService.IsSeized(mine),
                $"D2 每 {FlagshipService.SeizeInterval} 秒再夺取一台（第二台 {SignalPresence.MachineLabel(second)}，仍不碰玩家所在的那台）");
            // D3 击倒 = 瘫痪不阵亡
            site.TryGetMachineUnit(first, out int u1);
            k.Damage(u1, 1e6f, 0);
            WorldSimulation.StepMany(2);
            MachineRegistry.TryGetRecord(first, out MachineRecord r1);
            bool downed = SignalSeizeService.TryGetSeize(first, out bool dn, out _) && dn && r1.IsAlive;
            Expect(downed, $"D3 被夺取的机器被击倒后瘫痪（记录活着 {r1.IsAlive}、耐久 {r1.Health:0.#}），不阵亡");
            // D4 反向接入夺回（正式入口 SignalUplinkService.Request → 覆盖判定 → BeginReclaim）
            // 竞技场在白潮滩头预留区，默认不在归还核心的信号覆盖里：没建中继时反向接入按 FG01 覆盖判定被拒、仍被夺取（负向）；有中继塔覆盖时 3 秒夺回
            UplinkRequestResult refused = SignalUplinkService.Request(second, UplinkSource.MachineList, startCamera: false);
            WorldSimulation.StepMany(2);
            bool stillSeized = SignalSeizeService.IsSeized(second);
            foreach (EnemyEncounter e in BeachheadService.Encounters().Where(e => EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) && row.Role == "countersignal"))
            {
                Kill(site, e.Id); // 白潮屏蔽塔（滩头遗留）拒绝它 14 米内的接入：玩家先把塔拆掉
            }
            WorldSimulation.StepMany(2);
            SignalCoverageService.OverrideForTests = (sid, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower); // 测试捷径：代表玩家在竞技场边建了中继塔
            UplinkRequestResult rr = SignalUplinkService.Request(second, UplinkSource.MachineList, startCamera: false);
            StepSec(SignalSeizeService.ReclaimSeconds + 0.5f);
            SignalCoverageService.OverrideForTests = null;
            Expect(!refused.Accepted && refused.Failure == UplinkFailure.OutOfCoverage && stillSeized && rr.Accepted && !SignalSeizeService.IsSeized(second) && FactionOf(second) == CombatFaction.Player,
                $"D4 反向接入（正式入口 SignalUplinkService.Request）：覆盖外被拒（“{refused.Text}”）仍被夺取 {stillSeized}；有中继塔覆盖时 {SignalSeizeService.ReclaimSeconds} 秒后夺回（受理 {rr.Accepted}/{rr.Failure}“{rr.Text}”、回到己方 {FactionOf(second)}）");
            // E1 3 座中继全毁 → 核心暴露：被夺取的机器归还（瘫痪的恢复），要塞可伤
            int saves0 = FlagshipService.PhaseSaves;
            foreach (EnemyEncounter e in FlagshipService.Encounters(EnemyDefs.ConditionBossWave))
            {
                Kill(site, e.Id);
            }
            StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseBurst, 3);
            WorldSimulation.StepMany(2);
            MachineRegistry.TryGetRecord(first, out r1);
            site.TryGetEnemyUnit("fs_core", out int core);
            float h0 = Health(core);
            k.Damage(core, 300f, 0);
            WorldSimulation.StepMany(1);
            float h1 = Health(core);
            hud = FlagshipService.HudText(s, GameClock.Ticks);
            Expect(FlagshipService.PhaseOf(s) == 4 && !m.Any(SignalSeizeService.IsSeized) && r1.Health > 0f && FactionOf(first) == CombatFaction.Player && h1 < h0 && FlagshipService.PhaseSaves == saves0 + 1
                   && FlagshipService.StateOf(s).Seized.Length == 0 && Notified("flagship.notify.phase.4") && hud.Contains("%"),
                $"E1 3 座中继全毁 → 核心暴露·爆发窗口：被夺取的机器全部归还（瘫痪的恢复到 {r1.Health:0.#}）、要塞可伤（{h0:0} → {h1:0}）、自动存档；HUD“{hud}”");
            // E2 摧毁核心 = 击退旗舰（只授予一次）
            _defeatedEvents = 0;
            Kill(site, "fs_core");
            StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseDone, 3);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool again = CampaignEventLedger.TryGrant(s, FlagshipService.DefeatedEventId, "Endgame", s.PlaySeconds, "test");
            Expect(FlagshipService.Done(s) && FlagshipService.PhaseOf(s) == 5 && _defeatedEvents == 1 && !again && QuestState(s, "main.flagship") == "Completed"
                   && FlagshipService.HudText(s, GameClock.Ticks) == GameText.Get("flagship.hud.done") && Notified("flagship.notify.defeated") && !FlagshipService.Active(s),
                $"E2 摧毁暴露的核心 = 击退先遣旗舰：事件账本写入一次（再授予被拒 {!again}）、GameEvent {_defeatedEvents} 次、主线 main.flagship {QuestState(s, "main.flagship")}、HUD“已被击退”");
        }

        // ─────────────────────────────── D5 反夺控（DEBT-EMG4FW01-01 承接用例）───────────────────────────────

        private static void CheckFailsafe()
        {
            CampaignState s = Start(13055, true, 2, out List<int> m);
            ToPhase(s, FlagshipService.PhaseRoot);
            CombatSite c = Site;
            CombatKernel k = c.Kernel;
            List<int> order = ByRelayDistance(s, m);
            int a = order[0], b = order[1];
            c.TryGetMachineUnit(a, out int ua);
            c.TryGetMachineUnit(b, out int ub);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { "fw_failsafe" });
            k.SetUnitWeapon(ua, c.WeaponIndex(c.WithProgram(p, CombatSite.MachineWeaponFrom(p), null, out _, out _)));
            int listen = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_pingfire" }, FirmwareCarrier.Projectile, 0));
            k.SetUnitWeapon(ub, k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Cooldown = 0.5f, Program = listen,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            }));
            k.SetUnitRelay(ub, true);
            c.TryGetMachinePosition(b, out Vector2 pb);
            SpawnDecoy(k, pb + new Vector2(0f, 8f));
            long sent0 = k.EmgBusStats.PingsSent, delivered0 = k.EmgBusStats.PingsDelivered, sub0 = k.EmgBusStats.SubSpawned;
            StepUntil(() => SignalSeizeService.IsSeized(a), FlagshipService.SeizeFirst + 1f);
            WorldSimulation.StepMany(6);
            long sent = k.EmgBusStats.PingsSent - sent0, delivered = k.EmgBusStats.PingsDelivered - delivered0, sub = k.EmgBusStats.SubSpawned - sub0;
            Expect(SignalSeizeService.TryGetSeize(a, out _, out int src) && src == SignalSeizeService.SourceRoot && sent >= 1 && delivered >= 1 && sub >= 1,
                $"D5 DEBT-EMG4FW01-01 根权限正式来源：装反夺控（fw_failsafe）的机器被根权限夺取 → 向中继组广播 0 号频道 {sent} 次、装中继应答的友机收到 {delivered} 次并开火、放出子载荷 {sub} 份");
        }

        // ─────────────────────────────── F 负向：只有 1 台机器 ───────────────────────────────

        private static void CheckOneMachine()
        {
            CampaignState s = Start(13056, true, 0, out _);
            ToPhase(s, FlagshipService.PhaseRoot);
            // 玩家只剩 1 台：留一台出厂搬运机（ERC-001，家园保底认它是正式搬运机、不派紧急救援机），其余打掉。
            // 测试捷径：记为今天已应急打印（FGR-ECO-070 每日打印一台，保底本身由 FgSoftlockSelfCheck 覆盖），否则“只有 1 台”撑不过几秒
            int a = MachineRegistry.AllRecords.Where(r => r.IsAlive && r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == HomeValleyLayout.Erc001ChassisId && Site.TryGetMachineUnit(r.LogicId, out _))
                .Select(r => r.LogicId).DefaultIfEmpty(0).Min();
            GameLogic.Campaign.Economy.SoftlockService.StateOf(s).LastPrintDay = GameClock.DayOf(GameClock.GameSeconds) + 1000;
            KillMachines(Site, MachineRegistry.AllRecords.Where(r => r.IsAlive && r.LogicId != a).Select(r => r.LogicId).ToList());
            StepSec(3f);
            bool onlyOne = MachineRegistry.AllRecords.Count(r => r.IsAlive) == 1;
            s.SignalCore.UplinkMachineLogicId = a;
            int seize0 = FlagshipService.Seizes, tries0 = FlagshipService.StateOf(s).SeizeCount;
            StepSec(FlagshipService.SeizeFirst + FlagshipService.SeizeInterval + 0.5f);
            int tries1 = FlagshipService.StateOf(s).SeizeCount - tries0;
            bool noTarget = Notified("flagship.notify.no_target");
            bool uplinkedSafe = !SignalSeizeService.IsSeized(a) && FlagshipService.Seizes == seize0 && tries1 == 2 && noTarget;
            s.SignalCore.UplinkMachineLogicId = 0;
            StepSec(FlagshipService.SeizeInterval + 0.5f);
            bool lastSafe = !SignalSeizeService.IsSeized(a) && FlagshipService.Seizes == seize0 && FlagshipService.PhaseOf(s) == FlagshipService.PhaseRoot && FlagshipService.StateOf(s).Retries == 0;
            onlyOne &= MachineRegistry.AllRecords.Count(r => r.IsAlive) == 1;
            Expect(onlyOne && uplinkedSafe && lastSafe,
                $"F1 负向：玩家只有 1 台机器——接入在它里面时根权限每次都空转（{uplinkedSafe}：只剩 1 台 {onlyOne}（活着 {string.Join(",", MachineRegistry.AllRecords.Where(r => r.IsAlive).Select(r => r.LogicId + "@" + r.RegionId))}）、尝试 {tries1} 次、夺取 {FlagshipService.Seizes - seize0}（{string.Join(",", FlagshipService.StateOf(s).Seized)}）、通知 {noTarget}）；没接入时也不夺取最后一台（{lastSafe}：阶段 {FlagshipService.PhaseOf(s)}、重试 {FlagshipService.StateOf(s).Retries}）");
        }

        // ─────────────────────────────── G 负向：全灭后从阶段开始重试 ───────────────────────────────

        private static void CheckWipeRetry()
        {
            CampaignState s = Start(13057, true, 2, out List<int> m);
            ToPhase(s, FlagshipService.PhaseFortress);
            CombatSite site = Site;
            Kill(site, "fs_node_fluid");
            Kill(site, "fs_battery_1");
            WorldSimulation.StepMany(GameClock.StepHz + 1);
            bool brokeOne = FlagshipService.Find(s, "fs_node_fluid")?.IsAlive == false;
            int saves0 = FlagshipService.PhaseSaves, wipes0 = FlagshipService.Wipes;
            KillMachines(site, m);
            StepUntil(() => FlagshipService.Wipes > wipes0, 3);
            WorldSimulation.StepMany(2);
            bool restored = FlagshipService.Find(s, "fs_node_fluid")?.IsAlive == true && InKernel("fs_node_fluid") && site.TryGetEnemyUnit("fs_node_fluid", out int nu)
                            && site.Kernel.TryGetGate(nu, out _, out float g, out float max) && Mathf.Approximately(g, max) && InKernel("fs_battery_1");
            Expect(brokeOne && FlagshipService.Wipes == wipes0 + 1 && FlagshipService.PhaseOf(s) == FlagshipService.PhaseFortress && FlagshipService.StateOf(s).Retries == 1 && restored
                   && FlagshipService.PhaseSaves == saves0 + 1 && Notified("flagship.notify.wipe"),
                $"G1 负向：阶段二中竞技场里的机器全灭（存活 {m.Count(id => MachineRegistry.TryGetRecord(id, out MachineRecord mr) && mr.IsAlive)}）→ 从阶段二开始重试（重试 {FlagshipService.StateOf(s).Retries} 次）：已破的流体节点与炮位复原（满闸门 {restored}）、再自动存档一次、通知“从…开始重试”");
            // G2 阶段三全灭：被夺取的机器归还
            CampaignState s2 = Start(13058, true, 3, out List<int> m2);
            ToPhase(s2, FlagshipService.PhaseRoot);
            StepSec(FlagshipService.SeizeFirst + 0.3f);
            int seized = FlagshipService.StateOf(s2).Seized.FirstOrDefault();
            CombatSite site2 = Site;
            KillMachines(site2, m2.Where(x => x != seized));
            int w0 = FlagshipService.Wipes;
            StepUntil(() => FlagshipService.Wipes > w0, 3);
            WorldSimulation.StepMany(2);
            Expect(seized != 0 && FlagshipService.Wipes == w0 + 1 && !SignalSeizeService.IsSeized(seized) && FactionOf(seized) == CombatFaction.Player && FlagshipService.PhaseOf(s2) == FlagshipService.PhaseRoot
                   && FlagshipService.Encounters(EnemyDefs.ConditionBossWave).All(e => InKernel(e.Id)),
                $"G2 负向：阶段三中可用机器全灭（只剩被夺取的那台）→ 从阶段三开始重试（全灭 {FlagshipService.Wipes - w0}）：被夺取的 {SignalPresence.MachineLabel(seized)} 归还（{!SignalSeizeService.IsSeized(seized)}）、3 座中继立着");
        }

        // ─────────────────────────────── H 负向：家园核心被毁 → 读档从阶段开始 ───────────────────────────────

        private static void CheckCoreLostRetry()
        {
            CampaignState s = Start(13059, true, 2, out _);
            ToPhase(s, FlagshipService.PhaseFortress);
            WorldSimulation.StepMany(GameClock.StepHz);
            Kill(Site, "fs_node_em");
            WorldSimulation.StepMany(GameClock.StepHz + 1);
            HomeValleySoftlockGuard.DebugDestroyCore(s);
            long phaseTick = FlagshipService.StateOf(s).PhaseTick;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string retry = FlagshipService.FailureRetryText(s);
            bool frozen = FlagshipService.PhaseOf(s) == FlagshipService.PhaseFortress && FlagshipService.StateOf(s).PhaseTick == phaseTick;
            Expect(frozen && retry.Contains(FlagshipService.PhaseName(FlagshipService.PhaseFortress)),
                $"H1 负向：旗舰战中家园核心被摧毁 → 旗舰战不再推进；失败页写“{retry}”");
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("H2 读档失败：" + fail);
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Expect(!HomeValleySoftlockGuard.IsCoreDestroyed(l) && FlagshipService.PhaseOf(l) == FlagshipService.PhaseFortress && FlagshipService.Find(l, "fs_node_em")?.IsAlive == true && InKernel("fs_node_em")
                   ,
                $"H2 读取最近自动存档（阶段二开始时）：核心完好、从阶段二开始（存档后被破的电磁节点还立着、在内核里）——阶段 {FlagshipService.PhaseOf(l)}、节点记录 {FlagshipService.Find(l, "fs_node_em")?.IsAlive}、内核 {InKernel("fs_node_em")}、存档原因 {l.LastSaveReason}、核心毁 {HomeValleySoftlockGuard.IsCoreDestroyed(l)}");
        }

        // ─────────────────────────────── J 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = Start(13060, false, 3, out _);
                ToPhase(s, FlagshipService.PhaseRoot);
                FlagshipState st = FlagshipService.StateOf(s);
                long target = st.NextSeizeTick;
                GameClock.SkipForTests(s, target - GameClock.StepHz - GameClock.Ticks);
                long t0 = GameClock.Ticks;
                int seize0 = st.SeizeCount;
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool paused = GameClock.Ticks == t0 && st.SeizeCount == seize0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 20000 && GameClock.Ticks <= target + 1; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                bool exact = st.SeizeCount == seize0 + 1 && st.NextSeizeTick == target + GameClock.TicksFor(FlagshipService.SeizeInterval);
                ok &= paused && exact;
                rows.Add($"{speed}x 暂停 6 秒不走（{paused}）、夺取在第 {st.NextSeizeTick - GameClock.TicksFor(FlagshipService.SeizeInterval)} 步（应 {target}）");
            }
            Expect(ok, "J1 根权限夺取间隔按游戏时间：" + string.Join("；", rows));
        }

        // ─────────────────────────────── I 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = Start(13061, true, 3, out List<int> m);
            ToPhase(s, FlagshipService.PhaseRoot);
            StepSec(FlagshipService.SeizeFirst + 0.5f);
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string sig0 = Sig(s);
            StepSec(FlagshipService.SeizeInterval + 2f);
            string sigA = Sig(s);
            long t1 = GameClock.Ticks;
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("I1 读档失败：" + fail);
                return;
            }
            string sigL = Sig(l);
            WorldSimulation.StepMany((int)(t1 - GameClock.Ticks));
            string sigB = Sig(l);
            Expect(r.Success && FlagshipService.StateOf(l).Seized.Length >= 1 && sigL == sig0 && sigA == sigB,
                $"I1 存读档：阶段三已夺取 1 台时存档；读回的旗舰状态、夺取表、目标与节点 = 存档时（{sigL == sig0}），再走 22 秒（含下一次夺取）与不读档逐项一致（{sigA == sigB}）"
                + (sigA != sigB ? $"\n不读档：{sigA}\n读档后：{sigB}" : string.Empty) + (sigL != sig0 ? $"\n存档时：{sig0}\n读回：{sigL}" : string.Empty));
            // 旧档（没有旗舰域）：补成“还没开战”
            CampaignState old = CampaignState.CreateNew("fgflag-old", "Standard", 5);
            old.Progress.Flagship = null;
            CampaignFgStateDomains.EnsureAll(old);
            Expect(old.Progress.Flagship != null && old.Progress.Flagship.Phase == 0 && old.Progress.Flagship.Seized != null, "I2 旧存档没有旗舰域：读入后补成“还没开战”");
        }

        // ─────────────────────────────── K 观察一致 ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observe)
            {
                CampaignState s = Start(13062, observe, 3, out _);
                StepSec(FlagshipService.BombFirst + FlagshipService.BombInterval * 3 + 1f);
                Kill(Site, "fs_beacon_2");
                StepSec(12f);
                return Sig(s);
            }
            string a = Run(true);
            string b = Run(false);
            Expect(a == b, $"K1 家园不被观察时旗舰轰炸、落点与信标的结果与观察时一致（{a.Length} 字签名）" + (a != b ? $"\n观察：{a}\n不观察：{b}" : string.Empty));
        }

        // ─────────────────────────────── P 性能 ───────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = Start(13063, false, 3, out _);
            ToPhase(s, FlagshipService.PhaseFortress);
            const int n = 2000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                FlagshipService.WorldStep(s, GameClock.Ticks + 1 + (i % (GameClock.StepHz - 1)));
            }
            sw.Stop();
            double stepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                FlagshipService.HudText(s, GameClock.Ticks);
            }
            sw.Stop();
            double hudUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 200;
            PerfLines.Add($"FlagshipService.WorldStep 非整秒步 {stepUs:0.###} µs；HudText（阶段二，逐节点读闸门）{hudUs:0.#} µs（Editor batchmode）");
            Expect(stepUs < 20.0 && hudUs < 500.0, $"P1 热更层每步 O(1)：非整秒步 {stepUs:0.###} µs（< 20）、首领 HUD 一行 {hudUs:0.#} µs（< 500）");
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        /// <summary>新世界：揭示 → 撑过清洗 → 滩头着陆；竞技场边上（离降落区 45 米，炮位射程外）刷 <paramref name="machines"/> 台带枪的 ERC-003；<paramref name="cut"/> = 直接切断投送并进入阶段一。</summary>
        private static CampaignState Start(int seed, bool observe, int machines, out List<int> ids, bool cut = true)
        {
            SignalSeizeService.ResetForTests();
            SignalUplinkService.ResetForTests();
            FlagshipService.ResetCountersForTests();
            CampaignState s = NewWorld(seed, observe);
            SignalCoreService.EnsureInitialized(s);
            BlueprintVersionRecord version = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()).ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != BpGun)
                .Append(new BlueprintRecord { BlueprintId = BpGun, DisplayName = BpGun, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            Reveal(s);
            ClearPurge(s);
            WorldSimulation.StepMany(2);
            ids = new List<int>();
            FlagshipService.TryZone(s, out Vector2 zone);
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 dir = (core - zone).sqrMagnitude > 1f ? (core - zone).normalized : Vector2.down;
            Vector2 side = new Vector2(-dir.y, dir.x);
            for (int i = 0; i < machines; i++)
            {
                Vector2 at = zone + dir * 45f + side * (4f * i - 2f * (machines - 1));
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpGun, HomeValleyLayout.RegionId, at, 5000f, 5000f, "Player", 1);
                if (!r.Success)
                {
                    Fail($"测试准备：登记机器失败：{r.Message}");
                    continue;
                }
                MachineLoadoutRegistry.Register(s, r.LogicId, BpGun, 1);
                ids.Add(r.LogicId);
            }
            WorldSimulation.StepMany(2);
            if (cut)
            {
                CutBeachhead(s);
                StepUntil(() => FlagshipService.PhaseOf(s) == FlagshipService.PhaseOrbital, 4);
            }
            return s;
        }

        private static void CutBeachhead(CampaignState s)
        {
            foreach (EnemyEncounter e in BeachheadService.Encounters().Where(BeachheadService.IsTargetRow))
            {
                Kill(Site, e.Id);
            }
            StepUntil(() => BeachheadService.Done(s), 4);
        }

        /// <summary>测试捷径：把当前阶段的目标直接击毁（各阶段自身的机制由对应用例覆盖），推进到 <paramref name="phase"/>。</summary>
        private static void ToPhase(CampaignState s, int phase)
        {
            for (int guard = 0; guard < 6 && FlagshipService.PhaseOf(s) < phase; guard++)
            {
                int cur = FlagshipService.PhaseOf(s);
                foreach (EnemyEncounter e in FlagshipService.AllEncounters().Where(e => FlagshipService.IsGoalRow(cur, e)))
                {
                    Kill(Site, e.Id);
                }
                StepUntil(() => FlagshipService.PhaseOf(s) != cur, 3);
            }
            WorldSimulation.StepMany(2);
            if (FlagshipService.PhaseOf(s) != phase)
            {
                Fail($"测试准备：没能推进到阶段 {phase}（现在 {FlagshipService.PhaseOf(s)}）");
            }
        }

        private static void Reveal(CampaignState s)
        {
            s.Progress.Act = 3;
            if (CampaignObjectiveTracker.FindQuest(s, "main.homecoming") == null)
            {
                var list = new List<ObjectiveRecord>(s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                {
                    new ObjectiveRecord { ObjectiveId = "main.homecoming", DefId = "main.homecoming", State = ObjectiveState.Completed, EventId = string.Empty },
                };
                s.ObjectiveRecords = list.ToArray();
            }
            CampaignEventLedger.TryGrant(s, RevealService.RevealedEventId, "Endgame", s.PlaySeconds, "test");
            CampaignObjectiveTracker.Recompute(s);
        }

        private static void ClearPurge(CampaignState s)
        {
            PurgeWaveState p = PurgeWaveService.StateOf(s);
            p.StartTick = Math.Max(0, GameClock.Ticks - 1);
            p.Launched = p.Cleared = PurgeWaveService.WaveCount;
            p.DoneTick = GameClock.Ticks;
            p.NextStrikeTick = -1;
            CampaignEventLedger.TryGrant(s, PurgeWaveService.ClearedEventId, "Endgame", s.PlaySeconds, "test");
            CampaignObjectiveTracker.Recompute(s);
            CampaignObjectiveTracker.Recompute(s);
        }

        private static List<RegionEnemyRecord> Records(CampaignState s) =>
            (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(r => r != null && FlagshipService.IsFlagshipInstance(r.EnemyInstanceId)).ToList();

        private static bool InKernel(string id) => Site != null && Site.TryGetEnemyUnit(id, out int u) && Site.UnitExists(u) && Site.Kernel.IsAlive(u);

        private static Vector2 Pos(string id) => Site != null && Site.TryGetEnemyPosition(id, out Vector2 p) ? p : default;

        private static float Health(int unit) => Site != null && Site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v.Health : -1f;

        /// <summary>机器是外部血量单位：走内核伤害请求（与正常挨打同一路径）打到阵亡。</summary>
        private static void KillMachines(CombatSite site, IEnumerable<int> ids)
        {
            foreach (int id in ids)
            {
                if (site.TryGetMachineUnit(id, out int u))
                {
                    site.Kernel.Damage(u, 1e7f, 0);
                }
            }
            WorldSimulation.StepMany(2);
        }

        private static void Kill(CombatSite site, string id)
        {
            if (site != null && site.TryGetEnemyUnit(id, out int u))
            {
                site.Kernel.Kill(u, 0);
            }
        }

        private static CombatFaction FactionOf(int logicId) =>
            Site != null && Site.TryGetMachineUnit(logicId, out int u) && Site.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Faction : CombatFaction.Neutral;

        private static Vector2 MachinePos(int logicId) => MachineRegistry.TryGetLivePosition(logicId, out Vector2 p) ? p : MachineRegistry.TryGetRecord(logicId, out MachineRecord r) ? r.WorldPosition : default;

        private static Vector2 NearestMachine(CampaignState s, Vector2 at)
        {
            Vector2 best = new Vector2(1e6f, 1e6f);
            foreach (MachineRecord r in MachineRegistry.AllRecords.Where(x => x.IsAlive && x.RegionId == HomeValleyLayout.RegionId))
            {
                Vector2 p = MachinePos(r.LogicId);
                if ((p - at).sqrMagnitude < (best - at).sqrMagnitude)
                {
                    best = p;
                }
            }
            return best;
        }

        /// <summary>按到最近存活压制中继的距离排序（与 FlagshipService.TrySeize 同一口径）。</summary>
        private static List<int> ByRelayDistance(CampaignState s, List<int> ids)
        {
            var relays = FlagshipService.Encounters(EnemyDefs.ConditionBossWave).Where(e => InKernel(e.Id)).Select(e => Pos(e.Id)).ToList();
            return ids.OrderBy(id => relays.Min(r => (MachinePos(id) - r).sqrMagnitude)).ThenBy(id => id).ToList();
        }

        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        /// <summary>内核里的己方射手（不进机器登记 = 不算全灭判定的机器）；<paramref name="firmware"/> 给武器编程序。</summary>
        private static int SpawnShooter(CombatKernel k, Vector2 at, out int prog, params string[] firmware)
        {
            prog = firmware.Length > 0 ? CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), firmware, FirmwareCarrier.Projectile, 0)) : -1;
            int gun = k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 14f, Damage = 30f, Cooldown = 0.4f, Program = prog,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            });
            return k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live | CombatUnitFlags.Report,
                Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.5f, Speed = 0f, Health = 1e6f, MaxHealth = 1e6f, Weapon = firmware.Length > 0 ? gun : -1,
                BehaviorProfile = -1, Priority = 1,
            });
        }

        private static int SpawnDecoy(CombatKernel k, Vector2 at) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.6f, Speed = 0f, Health = 1e6f, MaxHealth = 1e6f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
        });

        private static WorldMapModel MapModel(CampaignState s)
        {
            FlagshipService.TryZone(s, out Vector2 c);
            WorldMapFilters.ResetAll();
            var model = new WorldMapModel();
            var view = new WorldMapView { CenterX = c.x, CenterY = c.y, HalfWidth = 400, CanvasWidth = 800, CanvasHeight = 800 };
            model.Collect(s, view, 512);
            return model;
        }

        private static string Sig(CampaignState s)
        {
            FlagshipState st = FlagshipService.StateOf(s);
            var sb = new StringBuilder();
            sb.Append($"T{GameClock.Ticks}|P{st.Phase},S{st.StartTick},PT{st.PhaseTick},D{st.DoneTick},NB{st.NextBombTick},B{st.BombCount},NS{st.NextSeizeTick},SC{st.SeizeCount},SI{st.SeizeIdle},A{st.ArenaSeen},R{st.Retries}");
            sb.Append("|Z").Append(string.Join(",", st.Seized));
            CombatSite site = Site;
            foreach (RegionEnemyRecord r in Records(s).OrderBy(r => r.EnemyInstanceId, StringComparer.Ordinal))
            {
                bool live = site != null && site.TryGetEnemyUnit(r.EnemyInstanceId, out int u) && site.Kernel.IsAlive(u);
                string gate = site != null && site.TryGetEnemyUnit(r.EnemyInstanceId, out int gu) && site.Kernel.TryGetGate(gu, out _, out float g, out _) ? $"g{g:F1}" : string.Empty;
                sb.Append($"|{r.EnemyInstanceId}:{r.IsAlive}/{live}{gate}");
            }
            foreach (MachineRecord m in MachineRegistry.AllRecords.OrderBy(x => x.LogicId))
            {
                SignalSeizeService.TryGetSeize(m.LogicId, out bool dn, out int src);
                sb.Append($"|m{m.LogicId}:{m.IsAlive},{SignalSeizeService.IsSeized(m.LogicId)},{dn},{src}");
            }
            return sb.ToString();
        }

        private static string QuestState(CampaignState s, string id) => CampaignObjectiveTracker.FindQuest(s, id)?.State.ToString() ?? "无";

        private static bool Notified(string key)
        {
            string frag = System.Text.RegularExpressions.Regex.Split(GameText.Get(key), @"\{\d+\}").OrderByDescending(x => x.Length).First();
            foreach (NotificationEntry n in NotificationCenter.History)
            {
                if (n?.Type == null || n.Type.Id != FlagshipService.NotifyType)
                {
                    continue;
                }
                foreach (NotificationMember m in n.Members)
                {
                    if (m?.DetailText != null && m.DetailText.Contains(frag))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static void StepSec(float s) => WorldSimulation.StepMany((int)Math.Max(1, GameClock.TicksFor(s)));

        private static bool StepUntil(Func<bool> done, float maxSeconds)
        {
            long end = GameClock.Ticks + GameClock.TicksFor(maxSeconds);
            while (GameClock.Ticks < end)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(1);
            }
            return done();
        }

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
            SignalSeizeService.ResetForTests();
            SignalUplinkService.ResetForTests();
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
            CampaignState s = CampaignState.CreateNew("fgflag-" + seed, "Standard", seed);
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
                Debug.Log($"[FgFlagshipSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
