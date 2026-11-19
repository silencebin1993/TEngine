using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-04 白潮滩头（登陆场；FG11 FGR-END-021、FG17 FGR-GEN-041；验收 FGT-END-005）：起真实世界（新战役 + 星球战斗内核 + 突袭导演 + 存档）断言——
    /// 揭示 / 清洗前没有任何东西；撑过清洗后在规划层预留区按手工锚点着陆（多种子都在预留区里）、着陆后自动存档；锚点目标在地图上标出；护卫无人机的防空覆盖拦住投送舱、打掉后放行；
    /// 屏蔽塔拒绝接入；持续投送（间隔随存活目标变化、上限不堆积）；3 个轨道锚点 + 登陆信标全毁 = 切断投送（只授予一次）；这一段期间突袭只有 1 级（负向：家园被攻破按失败规则读档重试）；
    /// 暂停与 0.5x～3x；存读档；观察与不观察一致。
    /// </summary>
    public static class FgBeachheadSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _clearedEvents;
        private static int _landedEvents;

        [MenuItem("QA/自检/FG 白潮滩头")]
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
            Line("\n[白潮滩头] 撑过清洗后在预留区按手工锚点着陆、防空覆盖、屏蔽塔、持续投送、切断投送、期间家园只有 1 级骚扰（FG13-END-04）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbeach-selfcheck-" + Guid.NewGuid().ToString("N"));
            GameEvent.AddEventListener(BeachheadService.ClearedEvent, OnCleared);
            GameEvent.AddEventListener(BeachheadService.LandedEvent, OnLanded);
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
                GameRoot.BindWorldProviders();
                BeachheadService.ResetCountersForTests();
                CombatSite.ResetFleetCountersForTests();

                Step(CheckData);
                Step(CheckSeeds);
                Step(CheckLandingFlow);
                Step(CheckHarassCap);
                Step(CheckCoreLostRetry);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservedEqualsUnobserved);
            }
            finally
            {
                GameEvent.RemoveEventListener(BeachheadService.ClearedEvent, OnCleared);
                GameEvent.RemoveEventListener(BeachheadService.LandedEvent, OnLanded);
                BeachheadService.LandedSaveHandler = originalLandSave;
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
            Line(_fail == 0 ? $"  白潮滩头：全部 {_pass} 项通过" : $"  白潮滩头：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static void OnCleared() => _clearedEvents++;
        private static void OnLanded() => _landedEvents++;

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            List<EnemyEncounter> encs = BeachheadService.Encounters();
            int targets = encs.Count(BeachheadService.IsTargetRow);
            int anchors = encs.Count(e => BeachheadService.IsTargetRow(e) && e.AnchorId != BeachheadService.BeaconPoint);
            bool beacon = encs.Any(e => BeachheadService.IsTargetRow(e) && e.AnchorId == BeachheadService.BeaconPoint);
            int escorts = encs.Count(e => EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy r) && r.Role == BeachheadService.RoleEscort);
            int towers = encs.Count(e => EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy r) && r.Role == "countersignal");
            var pts = WorldGenContent.TemplatePoints(BeachheadService.TemplateId);
            bool zone = pts.Any(p => p.PointId == BeachheadService.ZonePoint && p.Kind == "zone" && p.Radius > 0f);
            bool resolvable = encs.All(e => RegionEnemySites.TrySpawnPosition(e.RegionId, e.AnchorId, out _));
            bool pod = EnemyDefs.Rows.Any(r => r.Faction == EnemyDefs.FactionFleet && r.Behavior == BeachheadService.BehaviorAnchorDrop);
            // 预留区锚点行没有模板（as3 / as4 已发布、不改）：手工点位不进生成输入。
            bool notInInputs = WorldGenContent.AnchorRows.Where(a => a.Id == BeachheadService.AnchorId).All(a => a.TemplateId == "none");
            string[] keys =
            {
                "beachhead.notify.landed", "beachhead.notify.part_down", "beachhead.notify.cleared", "beachhead.hud.active", "beachhead.hud.done", "beachhead.map.label",
                "drop.antiair.escort", "quest.prog.beachhead", "failure.beachhead_retry", "quest.main.beachhead.title", "enemy.orbital_anchor.name", "enemy.landing_beacon.name",
                "world.anchor.point.orbital_anchor", "world.anchor.point.landing_beacon", "world.anchor.point.flagship_zone",
            };
            var missing = keys.Where(k => !GameText.Has(k)).ToList();
            Expect(targets == 4 && anchors == 3 && beacon && escorts >= 2 && towers >= 1 && zone && resolvable && pod && notInInputs && BeachheadService.HarassCapLevel == 1
                   && QuestCatalog.Get("main.beachhead") != null && missing.Count == 0,
                $"A1 数据：投送目标 {targets}（轨道锚点 {anchors} + 登陆信标 {beacon}）、护卫无人机 {escorts}、屏蔽塔 {towers}、旗舰降落区 {zone}、编成锚点都能解析 {resolvable}、登陆舱行 {pod}、" +
                $"手工点位不进生成输入 {notInInputs}、骚扰上限 {BeachheadService.HarassCapLevel} 级、主线 main.beachhead；文本缺 {missing.Count}：{string.Join(",", missing)}");
        }

        // ─────────────────────────────── B25 多种子 ───────────────────────────────

        private static void CheckSeeds()
        {
            var bad = new List<string>();
            int[] seeds = { 13041, -7, 990017, 424242 };
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed, observe: false);
                WorldPlan plan = WorldGenService.PlanFor(s);
                PlannedTerritory t = plan?.Find("white_tide");
                if (t == null || !BeachheadService.TryAnchor(s, out _))
                {
                    bad.Add(seed + " 没有预留区 / 锚点");
                    continue;
                }
                GridCell core = HomeGridService.CorePivot(s);
                foreach (string id in BeachheadService.PointIds(s))
                {
                    BeachheadService.TryPoint(s, id, out Vector2 p);
                    BeachheadService.TryPointRow(s, id, out AnchorTemplatePoint row);
                    float dt = Vector2.Distance(p, new Vector2(t.CenterX, t.CenterY));
                    float dc = Vector2.Distance(p, new Vector2(core.X, core.Y));
                    if (dt > t.Radius || dc <= plan.HomeZoneRadius)
                    {
                        bad.Add($"{seed}:{id} 离预留区中心 {dt:F0}（半径 {t.Radius}）/ 离核心 {dc:F0}");
                    }
                    if (row.Kind != "zone" && !WorldGenQuery.IsWalkableGenerated(s, Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)))
                    {
                        bad.Add($"{seed}:{id} 在悬崖 / 水面上");
                    }
                }
                foreach (PlannedTerritory o in plan.Territories.Where(x => x.IsFaction))
                {
                    if (Vector2.Distance(new Vector2(o.CenterX, o.CenterY), new Vector2(t.CenterX, t.CenterY)) < o.OuterRadius + t.OuterRadius)
                    {
                        bad.Add($"{seed}: 预留区与 {o.Id} 重叠");
                    }
                }
            }
            Expect(bad.Count == 0, $"B0 FGR-GEN-041 / B25：{seeds.Length} 个种子（含负数）下手工点位都按规划层锚点落在白潮滩头预留区里、不进家园区、目标点在可走格上，预留区不与阵营领地重叠" +
                                   (bad.Count > 0 ? "：" + string.Join("；", bad.Take(8)) : string.Empty));
        }

        // ─────────────────────────────── B～F 正式流程 ───────────────────────────────

        private static void CheckLandingFlow()
        {
            CampaignState s = NewWorld(13042);
            CombatSite site = Site;
            BeachheadService.TryPoint(s, BeachheadService.BeaconPoint, out Vector2 beaconAt);
            // B1 揭示之前 / 清洗没撑过：预留区没有任何东西。
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool before = !BeachheadService.Landed(s) && Records(s).Count == 0 && !MapHasBeachhead(s) && DropPodService.AntiAirAt(s, beaconAt.x, beaconAt.y)?.OutpostId != DropPodService.EscortOutpostId;
            Reveal(s);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool duringPurge = !BeachheadService.Landed(s) && Records(s).Count == 0 && !MapHasBeachhead(s);
            Expect(before && duringPurge, $"B1 揭示之前（{before}）与清洗波次进行中（{duringPurge}）：预留区只是普通地形，没有滩头单位、地图上没有滩头、没有防空覆盖");

            int land0 = BeachheadService.LandCount;
            int saves0 = BeachheadService.LandSaveCount;
            _landedEvents = 0;
            ClearPurge(s);
            WorldSimulation.StepMany(2);
            BeachheadState st = BeachheadService.StateOf(s);
            List<RegionEnemyRecord> recs = Records(s);
            int inKernel = recs.Count(r => site.TryGetEnemyUnit(r.EnemyInstanceId, out int u) && site.UnitExists(u) && site.Kernel.IsAlive(u));
            bool placed = recs.All(r => BeachheadService.TryPoint(s, BeachheadService.Encounters().First(e => e.Id == r.EnemyInstanceId).AnchorId, out Vector2 p) && (p - r.Position).sqrMagnitude < 1e-4f);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            bool saved = BeachheadService.LandSaveCount == saves0 + 1 && s.LastSaveReason == SaveReason.BeachheadLanded && meta.State == CampaignSlotState.Ready;
            int bx = Mathf.RoundToInt(beaconAt.x), by = Mathf.RoundToInt(beaconAt.y);
            bool scorch = site.InertAtCell(bx + 1, by) && site.InertAtCell(bx, by - 2) && !site.InertAtCell(bx + 9, by);
            bool blocked = site.AnyUplinkBlockAt(beaconAt, out float blockR);
            string quest = QuestState(s, "main.beachhead");
            ObjectiveRecord q = CampaignObjectiveTracker.FindQuest(s, "main.beachhead");
            string prog = q != null ? CampaignObjectiveTracker.ProgressText(s, q) : string.Empty;
            string hud = BeachheadService.HudText(s);
            Expect(BeachheadService.LandCount == land0 + 1 && _landedEvents == 1 && recs.Count == BeachheadService.Encounters().Count && inKernel == recs.Count && placed && saved && scorch && blocked
                   && quest != "无" && quest != "Completed" && prog.Contains("0/4") && hud.Contains("3/3") && hud.Contains("1 级") && Notified("beachhead.notify.landed", out _),
                $"B2 撑过清洗后下一步着陆：{recs.Count} 个滩头单位（{inKernel} 个在星球战斗内核里）按手工锚点落位（{placed}）；着陆后自动存档（原因 {s.LastSaveReason}：{saved}）；" +
                $"信标周围是灭活焦土（{scorch}）；屏蔽塔拒绝接入（信标处在 {blockR} 米范围内：{blocked}）；主线“切断投送” {quest}、进度“{prog}”；顶栏“{hud}”；着陆通知");

            // B3 地图：锚点目标标出。
            var model = MapModel(s, beaconAt);
            int targetIcons = model.Items.Count(i => i.Id != null && i.Id.StartsWith("beachhead:wt_") && i.Kind == WorldMapItemKind.Anchor && i.Id != "beachhead:" + BeachheadService.EntryPoint);
            bool circle = model.Circles.Any(c => c.Label == GameText.Get("beachhead.map.label")) && model.Circles.Any(c => c.Label == GameText.Get("world.anchor.point.flagship_zone"));
            bool towerCircle = model.Circles.Any(c => c.UplinkBlock);
            Expect(targetIcons == 4 && circle && towerCircle,
                $"B3 锚点目标在地图上标出：{targetIcons} 个目标图标（3 个轨道锚点 + 登陆信标）、滩头范围圈与旗舰降落区（{circle}）、屏蔽塔范围（{towerCircle}）");

            // C 防空：护卫无人机存活 → 投送舱不能落进滩头；打掉后放行。
            OutpostRecord aa = DropPodService.AntiAirAt(s, beaconAt.x, beaconAt.y);
            OutpostRecord far = DropPodService.AntiAirAt(s, beaconAt.x + 400f, beaconAt.y + 400f);
            string aaLabel = aa != null ? DropPodService.OutpostLabel(s, aa) : string.Empty;
            foreach (RegionEnemyRecord r in recs.Where(r => EnemyDefs.TryGet(r.EnemyTypeId, out MechEnemy row) && row.Role == BeachheadService.RoleEscort).ToList())
            {
                if (r != recs.Last(x => EnemyDefs.TryGet(x.EnemyTypeId, out MechEnemy rr) && rr.Role == BeachheadService.RoleEscort))
                {
                    Kill(site, r.EnemyInstanceId);
                }
            }
            WorldSimulation.StepMany(GameClock.StepHz);
            OutpostRecord aaOne = DropPodService.AntiAirAt(s, beaconAt.x, beaconAt.y);
            Kill(site, recs.Last(x => EnemyDefs.TryGet(x.EnemyTypeId, out MechEnemy rr) && rr.Role == BeachheadService.RoleEscort).EnemyInstanceId);
            WorldSimulation.StepMany(GameClock.StepHz);
            OutpostRecord aaNone = DropPodService.AntiAirAt(s, beaconAt.x, beaconAt.y);
            Expect(aa?.OutpostId == DropPodService.EscortOutpostId && far == null && aaLabel == GameText.Get("drop.antiair.escort") && aaOne?.OutpostId == DropPodService.EscortOutpostId
                   && (aaNone == null || aaNone.OutpostId != DropPodService.EscortOutpostId),
                $"C1 FG-GAP-130：护卫无人机存活时滩头在防空覆盖里（{aa?.OutpostId}，拒绝原因写“{aaLabel}”），远处不在（{far == null}）；剩 1 台仍覆盖（{aaOne?.OutpostId}）；全部打掉后防空消失（{aaNone?.OutpostId ?? "无"}）");

            // E 持续投送：间隔随存活目标变化、舱落地投放、上限不堆积。
            long drop0 = st.NextDropTick;
            bool firstAt = drop0 == st.LandedTick + GameClock.TicksFor(BeachheadService.DropFirstSeconds);
            GameClock.SkipForTests(s, Math.Max(0, drop0 - 1 - GameClock.Ticks));
            WorldSimulation.StepMany(2);
            RegionEnemyRecord pod0 = BeachheadService.Find(s, BeachheadService.PodPrefix + "0");
            bool falling = pod0 != null && site.TryGetEnemyUnit(pod0.EnemyInstanceId, out int pu) && site.Kernel.TryGetFleet(pu, out CombatFleet pf) && pf.State == CombatFleetConst.StatePodFalling;
            bool nearBeacon = pod0 != null && Vector2.Distance(pod0.Position, beaconAt) <= BeachheadService.DropRing + 0.01f;
            long gap = st.NextDropTick - drop0;
            bool interval4 = gap == BeachheadService.IntervalTicks(4, 4);
            F.StepUntil(() => s.RegionEnemies.Count(r => r.EnemyInstanceId.StartsWith(BeachheadService.PodPrefix + "0.c") && r.IsAlive) >= 4, 30);
            int cargo = s.RegionEnemies.Count(r => r.EnemyInstanceId.StartsWith(BeachheadService.PodPrefix + "0.c") && r.IsAlive);
            Expect(firstAt && falling && nearBeacon && interval4 && cargo >= 4,
                $"E1 持续投送：着陆后 {BeachheadService.DropFirstSeconds} 秒落第一个登陆舱（{firstAt}），落在信标旁（{nearBeacon}）、落地前在下落（{falling}）；落地投放 {cargo} 台；" +
                $"4 个目标都立着时下一舱隔 {gap / (double)GameClock.StepHz:F1} 秒（{interval4}）");
            int skipped0 = st.DropsSkipped;
            int count0 = st.DropCount;
            for (int i = 0; i < 4 && st.DropsSkipped == skipped0; i++)
            {
                GameClock.SkipForTests(s, Math.Max(0, st.NextDropTick - 1 - GameClock.Ticks));
                WorldSimulation.StepMany(2);
                F.StepUntil(() => !Records(s, includePods: true).Any(r => r.IsAlive && BeachheadService.IsPodInstance(r.EnemyInstanceId) && site.TryGetEnemyUnit(r.EnemyInstanceId, out int u)
                                                        && site.Kernel.TryGetFleet(u, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling), 20);
            }
            int alive = BeachheadService.DeliveredAlive(s);
            Expect(st.DropsSkipped > skipped0 && alive <= BeachheadService.DropAliveCap,
                $"E2 不无限堆积：滩头投送来的单位活着 {alive} 台（上限 {BeachheadService.DropAliveCap}）时这一次跳过（跳过 {st.DropsSkipped - skipped0} 次，共落 {st.DropCount - count0} 舱）");

            // F 摧毁目标：信标先倒 → 落点改到锚点旁、间隔变长、焦土开始恢复；全毁 → 切断投送（只一次）。
            foreach (RegionEnemyRecord r in Records(s, includePods: true).Where(r => r.IsAlive && BeachheadService.IsPodInstance(r.EnemyInstanceId)).ToList())
            {
                Kill(site, r.EnemyInstanceId);
            }
            WorldSimulation.StepMany(GameClock.StepHz);
            int scrap0 = BeachheadService.ScrapDrops;
            Kill(site, BeachheadService.BeaconPoint);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool partNote = Notified("beachhead.notify.part_down", out string partText);
            bool scrap = BeachheadService.ScrapDrops > scrap0;
            int marks0 = site.Kernel.FleetMarkCount;
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            int marks1 = site.Kernel.FleetMarkCount;
            foreach (RegionEnemyRecord r in Records(s, includePods: true).Where(r => r.IsAlive && BeachheadService.IsPodInstance(r.EnemyInstanceId)).ToList())
            {
                Kill(site, r.EnemyInstanceId); // 清掉期间投送来的单位，免得这一次因上限跳过
            }
            WorldSimulation.StepMany(GameClock.StepHz);
            int beforeDrop = st.DropCount;
            GameClock.SkipForTests(s, Math.Max(0, st.NextDropTick - 1 - GameClock.Ticks));
            long dropTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany(2);
            RegionEnemyRecord podB = BeachheadService.Find(s, BeachheadService.PodPrefix + beforeDrop);
            var anchorPos = Records(s).Where(r => r.IsAlive && BeachheadService.IsTargetRow(BeachheadService.Encounters().FirstOrDefault(e => e.Id == r.EnemyInstanceId))).Select(r => r.Position).ToList();
            bool nearAnchor = podB != null && anchorPos.Any(p => Vector2.Distance(p, podB.Position) <= BeachheadService.DropRing + 0.01f);
            bool interval3 = st.NextDropTick - dropTick == BeachheadService.IntervalTicks(3, 4);
            string podDiag = $"（诊断：舱 {podB?.EnemyInstanceId ?? "无"} @ {podB?.Position}，锚点 {string.Join(" ", anchorPos)}，跳过 {st.DropsSkipped}，滩头投送单位活着 {BeachheadService.DeliveredAlive(s)}）";
            Expect(partNote && partText.Contains("3/4") && scrap && marks1 < marks0 && nearAnchor && interval3 && BeachheadService.HudText(s).Contains("已毁"),
                $"F1 登陆信标被毁：通知“{partText}”、原地掉废料（{scrap}）、焦土开始恢复（{marks0} → {marks1} 格）；之后登陆舱落在剩下的锚点旁（{nearAnchor}），间隔拉长到 3 个目标的节奏（{interval3}）；顶栏“{BeachheadService.HudText(s)}”" + (nearAnchor ? string.Empty : podDiag));

            _clearedEvents = 0;
            int grant0 = BeachheadService.GrantCount;
            foreach (EnemyEncounter e in BeachheadService.Encounters().Where(BeachheadService.IsTargetRow))
            {
                if (BeachheadService.Find(s, e.Id)?.IsAlive == true)
                {
                    Kill(site, e.Id);
                }
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool done = BeachheadService.Done(s) && st.DoneTick >= 0 && st.NextDropTick < 0 && _clearedEvents == 1 && BeachheadService.GrantCount == grant0 + 1
                        && QuestState(s, "main.beachhead") == "Completed" && BeachheadService.HudText(s) == GameText.Get("beachhead.hud.done") && Notified("beachhead.notify.cleared", out _)
                        && BeachheadService.HarassLevelCap(s) == 0;
            int drops = st.DropCount;
            GameClock.SkipForTests(s, RaidDirectorService.DayTicks(0.5));
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool again = !CampaignEventLedger.TryGrant(s, BeachheadService.ClearedEventId, "Endgame", s.PlaySeconds, "test");
            Expect(done && st.DropCount == drops && again && _clearedEvents == 1 && !MapModel(s, beaconAt).Items.Any(i => i.Id != null && i.Id.StartsWith("beachhead:wt_anchor")),
                $"F2 3 个轨道锚点 + 登陆信标全毁 = 切断投送：事件账本写入一次、GameEvent 一次（{_clearedEvents}）、主线“切断投送” {QuestState(s, "main.beachhead")}、骚扰上限解除；" +
                $"之后半天不再投送（{drops} → {st.DropCount}）、再授予被拒（{again}）、地图上不再标已毁目标");
        }

        // ─────────────────────────────── G 家园 1 级骚扰 ───────────────────────────────

        private static void CheckHarassCap()
        {
            CampaignState s = NewWorld(13043);
            Reveal(s);
            WorldSimulation.StepMany(2);
            // 着陆前已排下、还没预警的 3 级计划 → 着陆时降到 1 级。
            RaidDirectorService.RequestStoryRaid(s, EnemyDefs.FactionFoundry, 3, "test.pre");
            F.StepUntil(() => RaidDirectorService.Plans(s).Any(p => p != null && p.StoryId == "test.pre"), 10);
            RaidPlanRecord pre = RaidDirectorService.Plans(s).FirstOrDefault(p => p != null && p.StoryId == "test.pre");
            int preLevel0 = pre?.Level ?? -1;
            int budget0 = pre?.Budget ?? -1;
            ClearPurge(s);
            WorldSimulation.StepMany(2);
            bool preCapped = pre != null && preLevel0 == 3 && pre.Level == 1 && (pre.State >= RaidDirectorService.StateWarned || pre.Budget < budget0);
            RaidDirectorService.RequestStoryRaid(s, EnemyDefs.FactionFleet, 4, "test.during");
            F.StepUntil(() => RaidDirectorService.Plans(s).Any(p => p != null && p.StoryId == "test.during"), 10);
            RaidPlanRecord during = RaidDirectorService.Plans(s).FirstOrDefault(p => p != null && p.StoryId == "test.during");
            Expect(BeachheadService.Active(s) && preCapped && during != null && during.Level == 1,
                $"G1 FGR-END-021 登陆场期间家园只有 1 级骚扰：着陆前排下的 3 级计划着陆时降到 {pre?.Level} 级（预算 {budget0} → {pre?.Budget}）；期间请求 4 级的剧情突袭建成 {during?.Level} 级");
            // 切断投送后上限解除。
            CombatSite site = Site;
            foreach (EnemyEncounter e in BeachheadService.Encounters().Where(BeachheadService.IsTargetRow))
            {
                Kill(site, e.Id);
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            RaidDirectorService.RequestStoryRaid(s, EnemyDefs.FactionFleet, 4, "test.after");
            F.StepUntil(() => RaidDirectorService.Plans(s).Any(p => p != null && p.StoryId == "test.after"), 10);
            RaidPlanRecord after = RaidDirectorService.Plans(s).FirstOrDefault(p => p != null && p.StoryId == "test.after");
            Expect(BeachheadService.Done(s) && after != null && after.Level == 4, $"G2 切断投送后上限解除：请求 4 级的剧情突袭建成 {after?.Level} 级");
        }

        // ─────────────────────────────── H 负向：家园被攻破 ───────────────────────────────

        private static void CheckCoreLostRetry()
        {
            CampaignState s = NewWorld(13044);
            Reveal(s);
            ClearPurge(s);
            WorldSimulation.StepMany(2);
            CombatSite site = Site;
            Kill(site, "wt_anchor_1");
            WorldSimulation.StepMany(GameClock.StepHz);
            SaveResult saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string sigSaved = Sig(s);
            BeachheadState st = BeachheadService.StateOf(s);
            HomeValleySoftlockGuard.DebugDestroyCore(s);
            int drop0 = st.DropCount;
            GameClock.SkipForTests(s, Math.Max(0, st.NextDropTick + GameClock.StepHz * 5 - GameClock.Ticks));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            SaveResult refused = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            string retry = BeachheadService.FailureRetryText(s);
            bool frozen = st.DropCount == drop0 && !BeachheadService.Done(s);
            Expect(saved.Success && refused.Outcome == SaveOutcome.CampaignLost && frozen && retry.Contains("1/4"),
                $"H1 负向：登陆场中家园核心被摧毁 → 存档被拒（{refused.Outcome}）、滩头不再推进（投送 {drop0} → {st.DropCount}）；失败页写“{retry}”");
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("H2 读档失败：" + fail);
                return;
            }
            bool restored = !HomeValleySoftlockGuard.IsCoreDestroyed(l) && BeachheadService.Active(l) && BeachheadService.Find(l, "wt_anchor_1")?.IsAlive == false
                            && BeachheadService.Find(l, "wt_anchor_2")?.IsAlive == true && Sig(l) == sigSaved;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool inKernel = Site != null && Site.TryGetEnemyUnit("wt_anchor_2", out int u) && Site.Kernel.IsAlive(u);
            Expect(restored && inKernel, $"H2 读取最近存档：核心完好、登陆场接着打（存档时已摧毁的锚点保留、其余仍立在内核里 {inKernel}；状态与存档时一致 {Sig(l) == sigSaved}）"
                                         + (Sig(l) != sigSaved ? $"\n存档时：{sigSaved}\n读回：{Sig(l)}" : string.Empty));
        }

        // ─────────────────────────────── J 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(13045, observe: false);
                Reveal(s);
                ClearPurge(s);
                WorldSimulation.StepMany(2);
                BeachheadState st = BeachheadService.StateOf(s);
                long target = st.NextDropTick;
                GameClock.SkipForTests(s, target - GameClock.StepHz - GameClock.Ticks);
                long t0 = GameClock.Ticks;
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool paused = GameClock.Ticks == t0 && st.DropCount == 0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 20000 && GameClock.Ticks <= target + 1; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                bool exact = st.DropCount == 1 && st.NextDropTick == target + BeachheadService.IntervalTicks(4, 4);
                ok &= paused && exact;
                rows.Add($"{speed}x 暂停 6 秒不走（{paused}）、第一舱在第 {st.NextDropTick - BeachheadService.IntervalTicks(4, 4)} 步落（应 {target}）");
            }
            Expect(ok, "J1 持续投送按游戏时间：" + string.Join("；", rows));
        }

        // ─────────────────────────────── I 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(13046);
            Reveal(s);
            ClearPurge(s);
            WorldSimulation.StepMany(2);
            BeachheadState st = BeachheadService.StateOf(s);
            GameClock.SkipForTests(s, st.NextDropTick - 1 - GameClock.Ticks);
            WorldSimulation.StepMany(2);
            Kill(Site, BeachheadService.BeaconPoint);
            WorldSimulation.StepMany(GameClock.StepHz);
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string sig0 = Sig(s);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
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
            Expect(r.Success && st.DropCount == 1 && sigL == sig0 && sigA == sigB,
                $"I1 存读档：存档时已投送 1 舱、登陆信标已毁；读回的滩头状态 = 存档时（{sigL == sig0}），再走 20 秒与不读档逐项一致（{sigA == sigB}）"
                + (sigA != sigB ? $"\n不读档：{sigA}\n读档后：{sigB}" : string.Empty) + (sigL != sig0 ? $"\n存档时：{sig0}\n读回：{sigL}" : string.Empty));
        }

        // ─────────────────────────────── K 观察一致 ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observe)
            {
                CampaignState s = NewWorld(13047, observe);
                Reveal(s);
                ClearPurge(s);
                WorldSimulation.StepMany(2);
                BeachheadState st = BeachheadService.StateOf(s);
                GameClock.SkipForTests(s, st.NextDropTick - 1 - GameClock.Ticks);
                WorldSimulation.StepMany(GameClock.StepHz * 40);
                return Sig(s);
            }
            string a = Run(true);
            string b = Run(false);
            Expect(a == b, $"K1 家园不被观察时滩头着陆、投送与舱落地投放的结果与观察时一致（{a.Length} 字签名）" + (a != b ? $"\n观察：{a}\n不观察：{b}" : string.Empty));
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private static void Reveal(CampaignState s)
        {
            // 测试捷径：揭示状态直接写入（揭示正式流程由 FgRevealSelfCheck 覆盖），补齐它推进的主线前置。
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

        /// <summary>测试捷径：撑过清洗波次（正式流程由 FgPurgeWaveSelfCheck 覆盖）——清洗状态记为已完成、事件账本写入，主线“守住家园”按事件完成。</summary>
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

        private static List<RegionEnemyRecord> Records(CampaignState s, bool includePods = false) =>
            (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(r => r != null && BeachheadService.IsBeachheadInstance(r.EnemyInstanceId)
                                                                         && (includePods || !BeachheadService.IsPodInstance(r.EnemyInstanceId))).ToList();

        private static void Kill(CombatSite site, string id)
        {
            if (site != null && site.TryGetEnemyUnit(id, out int u))
            {
                site.Kernel.Kill(u, 0);
            }
        }

        private static WorldMapModel MapModel(CampaignState s, Vector2 center)
        {
            WorldMapFilters.ResetAll();
            var model = new WorldMapModel();
            var view = new WorldMapView { CenterX = center.x, CenterY = center.y, HalfWidth = 400, CanvasWidth = 800, CanvasHeight = 800 };
            model.Collect(s, view, 512);
            return model;
        }

        private static bool MapHasBeachhead(CampaignState s)
        {
            BeachheadService.TryPoint(s, BeachheadService.BeaconPoint, out Vector2 c);
            WorldMapModel m = MapModel(s, c);
            return m.Items.Any(i => i.Id != null && i.Id.StartsWith("beachhead:")) || m.Circles.Any(c2 => c2.Label == GameText.Get("beachhead.map.label"));
        }

        private static string Sig(CampaignState s)
        {
            BeachheadState st = BeachheadService.StateOf(s);
            var sb = new StringBuilder();
            sb.Append($"T{GameClock.Ticks}|L{st.LandedTick},D{st.DoneTick},N{st.NextDropTick},C{st.DropCount},K{st.DropsSkipped}");
            CombatSite site = Site;
            foreach (RegionEnemyRecord r in (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(r => r != null && BeachheadService.IsBeachheadInstance(r.EnemyInstanceId)).OrderBy(r => r.EnemyInstanceId, StringComparer.Ordinal))
            {
                bool live = site != null && site.TryGetEnemyUnit(r.EnemyInstanceId, out int u) && site.Kernel.IsAlive(u);
                Vector2 p = site != null && site.TryGetEnemyPosition(r.EnemyInstanceId, out Vector2 lp) ? lp : r.Position;
                sb.Append($"|{r.EnemyInstanceId}:{r.IsAlive}/{live}@{p.x:F1},{p.y:F1}");
            }
            return sb.ToString();
        }

        private static string QuestState(CampaignState s, string id) => CampaignObjectiveTracker.FindQuest(s, id)?.State.ToString() ?? "无";

        /// <summary>同类通知会按真实时间聚合：逐条成员找格式里最长的那段固定文字。</summary>
        private static bool Notified(string key, out string text)
        {
            string frag = System.Text.RegularExpressions.Regex.Split(GameText.Get(key), @"\{\d+\}").OrderByDescending(x => x.Length).First();
            text = string.Empty;
            foreach (NotificationEntry n in NotificationCenter.History)
            {
                if (n?.Type == null || n.Type.Id != BeachheadService.NotifyType)
                {
                    continue;
                }
                foreach (NotificationMember m in n.Members)
                {
                    if (m?.DetailText != null && m.DetailText.Contains(frag))
                    {
                        text = m.DetailText;
                    }
                }
            }
            return text.Length > 0;
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
            CampaignState s = CampaignState.CreateNew("fgbeach-" + seed, "Standard", seed);
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
                Debug.Log($"[FgBeachheadSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
