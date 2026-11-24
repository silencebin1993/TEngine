using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
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
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG11-REG-01 热寂数据城领地模板与锚点（FG09 第 7 章；FG17 FGR-GEN-021～024、042）：
    /// A 数据；B 规划层（多种子：冷却塔遗址、数据中心入口、领地模板）与生成器 v4（湖心平台水环 + 堤道，v3 不变）；
    /// C 冷却塔遗址（星球内核热量上限恢复、接入 HUD、地图范围圈；负向：塔被摧毁 → 不再恢复、圈消失、存读档一致）；
    /// D 中继涂层规则（FG-GAP-164 第 3 项）与护送任务文本（FG-GAP-165）。
    /// </summary>
    public static class FgHeatDataCitySelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string BpGun = "bp_fg11reg01_gun";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("QA/自检/FG 热寂数据城领地")]
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
            Line("\n[热寂数据城领地] 冷却塔遗址、数据中心入口、生成器 v4 湖心水环、中继涂层规则（FG11-REG-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgheatcity-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;

                Step(CheckData);
                Step(CheckPlan);
                Step(CheckLakeRing);
                Step(CheckTowers);
                Step(CheckTowerBurn);
                Step(CheckRelayCoating);
            }
            finally
            {
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                SignalUplinkService.RealTimeForTests = null;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                AcidMarshService.InvalidateCoating();
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
            Line(_fail == 0 ? $"  热寂数据城领地：全部 {_pass} 项通过" : $"  热寂数据城领地：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            TerritoryTemplate tpl = WorldGenContent.TemplateRows.FirstOrDefault(r => r != null && r.TerritoryId == EnemyDefs.FactionOverclock);
            bool template = tpl != null && tpl.HazardZones > 0 && tpl.SideChance > 0f && tpl.RelicFactor > 0f && tpl.CacheChance > 0f && tpl.TerminalChance > 0f && GameText.Has(tpl.IntroKey);
            bool v4 = WorldGenVersions.Current >= 4 && WorldGenContent.Version(WorldGenVersions.Current).AnchorSet == "as4" /* FG15-QA-03：v5 只多锚点粗粒度连通性判定，仍用 as4 */ && WorldGenContent.TryGetVersion(4, out WorldGenVersion row4) && row4.AnchorSet == "as4" && row4.TemplateSet == "tt3"
                      && WorldGenContent.TryGetVersion(3, out WorldGenVersion row3) && row3.AnchorSet == "as3";
            List<AnchorSite> as4 = WorldGenContent.AnchorsFor(WorldGenContent.Version(4));
            List<AnchorSite> as3 = WorldGenContent.AnchorsFor(WorldGenContent.Version(3));
            bool sameOrder = as4.Count == as3.Count && as4.Select(a => a.Id).SequenceEqual(as3.Select(a => a.Id)) && as3.All(a => a.WaterRing == 0 && a.CausewayWidth == 0);
            AnchorSite plat = as4.FirstOrDefault(a => a.Id == "baptism_platform");
            AnchorSite gate = as4.FirstOrDefault(a => a.Id == "data_center_gate");
            bool ring = plat != null && plat.WaterRing > 0 && plat.CausewayWidth > 0 && as4.Count(a => a.WaterRing > 0) == 1;
            bool gateRow = gate != null && gate.TerritoryId == EnemyDefs.FactionOverclock && gate.Kind == TerritoryPlanner.KindIndoor && gate.SurfaceId == "data_center" && gate.ReachCheck == 1;
            string[] keys = { "cooling.hud.restored", "cooling.hud.radiation", "cooling.map.label", "cooling.notify.destroyed", "grid.note.relay_coated", "grid.note.relay_needs_coating" };
            string[] missing = keys.Where(k => !GameText.Has(k)).ToArray();
            bool tuning = CoolingTowerService.Radius > 0f && GridContent.TuningInt("grid.relay_coated_block_level") == 3 && GridContent.TuningInt("grid.pollution_block_level") == 2
                && TerritoryPlanner.CausewayPollutionCap < GridContent.TuningInt("grid.relay_coated_block_level");
            Expect(template && v4 && sameOrder && ring && gateRow && missing.Length == 0 && tuning,
                $"A1 超频领地模板（危害区 {tpl?.HazardZones}、支线 {tpl?.SideChance}、遗迹倍率 {tpl?.RelicFactor}、缓存 / 终端、介绍）；生成器当前 v{WorldGenVersions.Current}（v4 锚点集合 as4、模板 tt3，{v4}）；" +
                $"as4 与 as3 锚点逐个同序、as3 没有水环（{sameOrder}）；只有湖心平台有水环 {plat?.WaterRing} / 堤道半宽 {plat?.CausewayWidth}；数据中心入口在超频领地、通往室内 data_center、开局校验可达（{gateRow}）；" +
                $"文本齐全（缺 {string.Join(",", missing)}）；冷却塔半径 {CoolingTowerService.Radius}、涂层后中继阻挡等级 {GridContent.TuningInt("grid.relay_coated_block_level")}、v4 堤道污染上限 {TerritoryPlanner.CausewayPollutionCap}（须低于阻挡等级）");
        }

        // ─────────────────────────────── B 规划层（B25 多种子） ───────────────────────────────

        private static void CheckPlan()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 42, 1, 20261117 })
            {
                CampaignState s = NewWorldState(seed);
                WorldPlan plan = WorldGenService.PlanFor(s);
                PlannedTerritory t = plan?.Find(EnemyDefs.FactionOverclock);
                List<CoolingTowerService.Tower> towers = CoolingTowerService.Towers(s).ToList();
                int zones = plan?.HazardZones.Count(z => z.TerritoryId == EnemyDefs.FactionOverclock) ?? 0;
                int belt = Mathf.RoundToInt(GridContent.Tuning("cooling.tower.belt_count"));
                bool count = t != null && towers.Count == zones + belt && zones > 0;
                // 领地里的塔都在高温辐射区里（塔圈内恢复才有意义）；辐射带上的塔在外圈带里、朝家园一侧。
                bool inRad = towers.All(tw => HeatRadiationService.ZoneAt(s, new Vector2(tw.X, tw.Y)) != null);
                bool beltSide = towers.Where(tw => tw.Id.Contains("#belt")).All(tw =>
                {
                    float d = Vector2.Distance(new Vector2(tw.X, tw.Y), new Vector2(t.CenterX, t.CenterY));
                    float toCore = Vector2.Distance(new Vector2(tw.X, tw.Y), new Vector2(plan.CoreX, plan.CoreY));
                    return d > t.Radius && d < t.OuterRadius && toCore < Vector2.Distance(new Vector2(t.CenterX, t.CenterY), new Vector2(plan.CoreX, plan.CoreY));
                });
                PlannedAnchor gate = plan?.FindAnchor("data_center_gate");
                bool gateIn = gate != null && plan.TerritoryAt(gate.EntryX, gate.EntryY)?.Id == EnemyDefs.FactionOverclock && !gate.Fallback;
                // 同一种子再算一次结果相同（只由规划层决定）。
                CoolingTowerService.ResetForTests();
                List<CoolingTowerService.Tower> again = CoolingTowerService.Towers(s).ToList();
                bool det = again.Count == towers.Count && again.Select(x => (x.Id, x.X, x.Y)).SequenceEqual(towers.Select(x => (x.Id, x.X, x.Y)).ToList());
                all &= count && inRad && beltSide && gateIn && det;
                lines.Add($"种子 {seed}：{towers.Count} 座（辐射区 {zones} + 辐射带 {belt}，{count}）、都在辐射区里 {inRad}、带上的朝家园 {beltSide}、数据中心入口在领地里 {gateIn}@({gate?.EntryX},{gate?.EntryY})、确定 {det}");
            }
            Expect(all, "B1 冷却塔遗址与数据中心入口按种子放在热寂数据城里（B25 不依赖固定坐标）——" + string.Join("；", lines));
        }

        private static void CheckLakeRing()
        {
            byte water = GridContent.TerrainCode("water");
            byte cliff = GridContent.TerrainCode("cliff");
            byte buildable = GridContent.TerrainCode("buildable");
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 42, 7 })
            {
                WorldGenContext v4 = WorldGenContext.Build(seed, 4, WorldSettings.Resolve(4, WorldGenContent.DefaultPresetId), new GridCell(0, 0), 32);
                WorldGenContext v3 = WorldGenContext.Build(seed, 3, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId), new GridCell(0, 0), 32);
                PlannedAnchor a = v4.Plan.FindAnchor("baptism_platform");
                PlannedAnchor a3 = v3.Plan.FindAnchor("baptism_platform");
                int ring = a.Row.WaterRing, half = a.Row.CausewayWidth, fp = a.FootprintRadius, outer = fp + ring;
                int ringN = 0, ringWater = 0, padN = 0, padOk = 0;
                for (int dy = -outer; dy <= outer; dy++)
                {
                    for (int dx = -outer; dx <= outer; dx++)
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        v4.Source.Sample(a.OriginX + dx, a.OriginY + dy, out byte tt, out _);
                        if (d <= fp - 1)
                        {
                            padN++;
                            padOk += tt == buildable ? 1 : 0;
                        }
                        else if (d >= fp + 2 && d <= outer - 1)
                        {
                            ringN++;
                            ringWater += tt == water ? 1 : 0;
                        }
                    }
                }
                // 可达：从平台中心出发、只走非水面非悬崖的格，能走出水环外缘（堤道）。
                bool Walk(int x, int y)
                {
                    v4.Source.Sample(x, y, out byte tt, out _);
                    return tt != water && tt != cliff;
                }
                int span = outer + 4;
                var seen = new HashSet<long>();
                var q = new Queue<(int, int)>();
                q.Enqueue((a.OriginX, a.OriginY));
                seen.Add(((long)a.OriginX << 32) ^ (uint)a.OriginY);
                bool escaped = false;
                while (q.Count > 0 && !escaped)
                {
                    (int x, int y) = q.Dequeue();
                    if ((x - a.OriginX) * (long)(x - a.OriginX) + (y - a.OriginY) * (long)(y - a.OriginY) > (long)(outer + 2) * (outer + 2))
                    {
                        escaped = true;
                        break;
                    }
                    foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    {
                        if (Math.Abs(nx - a.OriginX) > span || Math.Abs(ny - a.OriginY) > span || !seen.Add(((long)nx << 32) ^ (uint)ny) || !Walk(nx, ny))
                        {
                            continue;
                        }
                        q.Enqueue((nx, ny));
                    }
                }
                // v3 不变：同一种子 v3 / v4 规划层的锚点位置相同；离湖心平台远的区块逐格相同；水环里 v3 没有被强制成水。
                bool samePlace = a3 != null && a3.OriginX == a.OriginX && a3.OriginY == a.OriginY;
                int diffFar = 0, diffNear = 0;
                for (int k = 0; k < 400; k++)
                {
                    int fx = (k * 37) % 400 - 200, fy = (k * 91) % 400 - 200;
                    v3.Source.Sample(fx, fy, out byte t3, out byte p3);
                    v4.Source.Sample(fx, fy, out byte t4, out byte p4);
                    diffFar += t3 != t4 || p3 != p4 ? 1 : 0;
                }
                for (int k = 0; k < 64; k++)
                {
                    double ang = k * Math.PI * 2 / 64;
                    int x = a.OriginX + (int)Math.Round(Math.Cos(ang) * (fp + ring / 2));
                    int y = a.OriginY + (int)Math.Round(Math.Sin(ang) * (fp + ring / 2));
                    v3.Source.Sample(x, y, out byte t3, out _);
                    v4.Source.Sample(x, y, out byte t4, out _);
                    diffNear += t3 != t4 ? 1 : 0;
                }
                // FG11-E2E-01（FG-GAP-164）：堤道中线从平台边缘铺到领地圈外，每点可建、污染 ≤ 有涂层中继可建的最高级（湖区内部多为 3 级）；v3 同点超过上限的点数证明上限起了作用。
                int cap = TerritoryPlanner.CausewayPollutionCap, cwN = 0, cwOk = 0, cwCapped = 0;
                long cfx = WorldPlan.Cos(a.ForwardDeg + 180), cfy = WorldPlan.Cos(a.ForwardDeg + 90);
                int cwLen = outer + 2 + (a.Territory != null ? a.Territory.Radius : 0);
                for (int dd = fp; dd <= cwLen; dd += 6)
                {
                    int cx = a.OriginX + (int)((dd * cfx + 32768) >> 16), cy = a.OriginY + (int)((dd * cfy + 32768) >> 16);
                    v4.Source.Sample(cx, cy, out byte tc, out byte pc);
                    v3.Source.Sample(cx, cy, out _, out byte pc3);
                    cwN++;
                    cwOk += tc == buildable && pc <= cap ? 1 : 0;
                    cwCapped += pc3 > cap ? 1 : 0;
                }
                bool ok = ringN > 0 && ringWater >= ringN * 0.85f && padOk == padN && escaped && samePlace && diffFar == 0 && diffNear > 0 && cwN > 0 && cwOk == cwN;
                all &= ok;
                lines.Add($"种子 {seed}：平台 {padOk}/{padN} 格可建、水环 {ringWater}/{ringN} 格是水、堤道走得出去 {escaped}；v3 同位置 {samePlace}、家园附近 400 格 v3/v4 差异 {diffFar}、水环中线 64 点里 {diffNear} 点变了；" +
                          $"堤道中线 {cwOk}/{cwN} 点可建且污染 ≤ {cap} 级（v3 同点有 {cwCapped} 点超过）");
            }
            Expect(all, "B2 生成器 v4（DEBT-FG10BOSS01-02）：湖心平台四周是水、堤道可达且污染不超过有涂层中继可建的等级（FG-GAP-164）；v3 世界按旧版本生成不变（FGT-GEN-006）——" + string.Join("；", lines));
        }

        // ─────────────────────────────── C 冷却塔遗址（星球内核 + HUD + 地图 + 负向） ───────────────────────────────

        private static void CheckTowers()
        {
            CampaignState s = NewWorld(42);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            WorldPlan plan = WorldGenService.PlanFor(s);
            List<CoolingTowerService.Tower> towers = CoolingTowerService.Towers(s).ToList();
            CoolingTowerService.Tower tw = towers.First(x => !x.Id.Contains("#belt"));
            PlannedHazardZone z = plan.HazardZones.First(h => h.TerritoryId == tw.TerritoryId && tw.Id == h.TerritoryId + "#" + h.Index);
            string key = CoolingTowerService.KeyOf(tw.Id);
            bool spawned = towers.All(x => home.TryGetEnemyUnit(CoolingTowerService.KeyOf(x.Id), out int u) && home.UnitExists(u)) && home.Kernel.CoolingZoneCount == towers.Count;

            // 三台装枪的机器：塔旁（辐射区里、塔圈里）、同一辐射区里塔圈外、辐射区外。
            int near = SpawnIn(s, new Vector2(tw.X + 2f, tw.Y));
            int far = SpawnIn(s, new Vector2(z.CenterX + (CoolingTowerService.Radius + z.Radius) * 0.5f, z.CenterY));
            GridCell core = HomeGridService.CorePivot(s);
            int safe = SpawnIn(s, new Vector2(core.X + 12f, core.Y + 12f));
            WorldSimulation.StepMany(2);
            Place(home, near, new Vector2(tw.X + 2f, tw.Y));
            Place(home, far, new Vector2(z.CenterX + (CoolingTowerService.Radius + z.Radius) * 0.5f, z.CenterY));
            WorldSimulation.StepMany(1);
            home.TryGetMachineOverheatAt(near, out float capNear);
            home.TryGetMachineOverheatAt(far, out float capFar);
            home.TryGetMachineOverheatAt(safe, out float capSafe);
            var hudNear = new UplinkHudSnapshot();
            var hudFar = new UplinkHudSnapshot();
            var hudSafe = new UplinkHudSnapshot();
            UplinkHudModel.BuildVitals(s, near, hudNear);
            UplinkHudModel.BuildVitals(s, far, hudFar);
            UplinkHudModel.BuildVitals(s, safe, hudSafe);
            bool kernel = capSafe > 0f && Mathf.Approximately(capNear, capSafe) && Mathf.Approximately(capFar, capSafe * HeatRadiationService.CapMult);
            bool hud = hudNear.HeatCapNote == GameText.Get("cooling.hud.restored") && Mathf.Approximately(hudNear.HeatMax, capSafe)
                       && hudFar.HeatCapNote.Contains("25") && Mathf.Approximately(hudFar.HeatMax, capSafe * HeatRadiationService.CapMult)
                       && hudSafe.HeatCapNote.Length == 0 && Mathf.Approximately(hudSafe.HeatMax, capSafe);
            // 地图范围圈：第二幕、塔所在处已探索 → 有一个冷却塔圈，半径 = 恢复半径。
            s.Progress.Act = Math.Max(2, s.Progress.Act);
            HomeGridService.RevealArea(s, new Vector2(tw.X, tw.Y), 30f);
            bool CircleOn()
            {
                var model = new WorldMapModel();
                var view = new WorldMapView { CenterX = tw.X, CenterY = tw.Y, HalfWidth = 600, CanvasWidth = 900, CanvasHeight = 700 };
                model.Collect(s, view, 200);
                return model.Circles.Any(c => c.HazardId == "cooling_tower" && Math.Abs(c.X - tw.X) < 0.5 && Math.Abs(c.Y - tw.Y) < 0.5 && Mathf.Approximately(c.Radius, CoolingTowerService.Radius));
            }
            bool mapBefore = CircleOn();
            Expect(spawned && kernel && hud && mapBefore,
                $"C1 冷却塔遗址（FG09 第 7 章“站在运转中的冷却塔附近可以恢复热量上限”）：{towers.Count} 座都在星球内核里、塔圈 {home.Kernel.CoolingZoneCount} 个（{spawned}）；" +
                $"过热点 塔旁 {capNear:0.#} / 同一辐射区塔圈外 {capFar:0.#} / 辐射区外 {capSafe:0.#}（{kernel}）；接入 HUD“{hudNear.HeatCapNote}”{hudNear.HeatMax:0.#}、“{hudFar.HeatCapNote}”{hudFar.HeatMax:0.#}、" +
                $"区外 {hudSafe.HeatMax:0.#}（{hud}）；地图范围圈 {mapBefore}");

            // 负向（卡片“冷却塔被摧毁”）：打空 → 记进存档、塔圈从内核撤掉、塔旁回到 -25%、HUD 改写、范围圈消失、通知一次；存读档后仍是已摧毁且不再生成。
            int notes0 = CoolingTowerService.DestroyedCount;
            home.TryDamageEnemy(key, 99999f);
            WorldSimulation.StepMany(2);
            home.TryGetMachineOverheatAt(near, out float capAfter);
            var hudAfter = new UplinkHudSnapshot();
            UplinkHudModel.BuildVitals(s, near, hudAfter);
            bool destroyed = CoolingTowerService.IsDestroyed(s, tw.Id) && home.Kernel.CoolingZoneCount == towers.Count - 1
                             && Mathf.Approximately(capAfter, capSafe * HeatRadiationService.CapMult) && hudAfter.HeatCapNote.Contains("25") && !CircleOn()
                             && CoolingTowerService.DestroyedCount == notes0 + 1;
            int zonesAfter = home.Kernel.CoolingZoneCount;
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            CombatSite home2 = CombatSites.Get(HomeValleyLayout.RegionId);
            bool persisted = sv.Success && back != null && CoolingTowerService.IsDestroyed(back, tw.Id) && home2 != null
                             && !(home2.TryGetEnemyUnit(key, out int gone) && home2.IsUnitAlive(gone)) && home2.Kernel.CoolingZoneCount == towers.Count - 1
                             && CoolingTowerService.ActiveCircles(back).Count == towers.Count - 1;
            // 其余运转中的塔读档后照常（同一口径）。
            bool othersOk = persisted && towers.Where(x => x.Id != tw.Id).All(x => home2.TryGetEnemyUnit(CoolingTowerService.KeyOf(x.Id), out int u) && home2.UnitExists(u));
            Expect(destroyed && persisted && othersOk,
                $"C2 负向（冷却塔被摧毁）：塔旁过热点回到 {capAfter:0.#}（-25%）、HUD“{hudAfter.HeatCapNote}”、内核塔圈 {zonesAfter}、范围圈消失、通知一次（{destroyed}）；" +
                $"存读档后仍是已摧毁、不再生成、塔圈 {home2?.Kernel.CoolingZoneCount}（{persisted}）；其余 {towers.Count - 1} 座照常（{othersOk}）");

            // 暂停：暂停时打空另一座塔，结果与不暂停一致（阵亡回调按内核事件，与倍速 / 暂停无关；效果只按位置判定、没有计时）。
            CoolingTowerService.Tower tw2 = towers.First(x => x.Id != tw.Id);
            GameClock.SetPaused(true);
            home2.TryDamageEnemy(CoolingTowerService.KeyOf(tw2.Id), 99999f);
            WorldSimulation.StepMany(2);
            bool pausedNoChange = !CoolingTowerService.IsDestroyed(back, tw2.Id);
            GameClock.SetPaused(false);
            GameClock.SetSpeed(3f);
            WorldSimulation.StepMany(2);
            bool afterResume = CoolingTowerService.IsDestroyed(back, tw2.Id) && home2.Kernel.CoolingZoneCount == towers.Count - 2;
            GameClock.SetSpeed(1f);
            Expect(afterResume,
                $"C3 暂停 / 倍速：暂停中打空另一座塔（暂停中已结算 {!pausedNoChange}），恢复并切到 3x 后结算一致、内核塔圈 {home2.Kernel.CoolingZoneCount}（{afterResume}）；效果只按位置判定、没有计时");
        }

        /// <summary>
        /// C4 FG-GAP-238（ADR-REG-002 §2.1“燃迹真明火打空即毁”）：结构单位按占地半径接触斑块——塔旁 3.5 格、半径 2 的明火（盖不到塔心、压到塔身）会烧塔；
        /// 负向：同样的明火放在 7 格外（斑块圆与塔身不相交）不烧。走真实内核斑块接触，不直接扣血。
        /// </summary>
        private static void CheckTowerBurn()
        {
            CampaignState s = NewWorld(42);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            List<CoolingTowerService.Tower> towers = CoolingTowerService.Towers(s).ToList();
            CoolingTowerService.Tower touch = towers[0], apart = towers[1];
            float Hp(CoolingTowerService.Tower t) =>
                home.TryGetEnemyUnit(CoolingTowerService.KeyOf(t.Id), out int u) && home.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Health : -1f;
            float hp0 = Hp(touch), hpApart0 = Hp(apart);
            bool laid = home.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(touch.X + 3.5, touch.Y), 2f, 1f, 4f)
                        & home.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(apart.X + 7.0, apart.Y), 2f, 1f, 4f);
            WorldSimulation.StepMany(40);
            float hp1 = Hp(touch), hpApart1 = Hp(apart);
            Expect(laid && hp0 > 0f && hp1 < hp0 && Mathf.Approximately(hpApart1, hpApart0),
                $"C4 冷却塔被明火烧（FG-GAP-238）：塔身半径 2.5；塔旁 3.5 格的明火（半径 2，不盖塔心）{hp0:0.#} → {hp1:0.#}；负向 7 格外的明火 {hpApart0:0.#} → {hpApart1:0.#}（放置 {laid}）");
        }

        // ─────────────────────────────── D 中继涂层（FG-GAP-164）与护送文本（FG-GAP-165） ───────────────────────────────

        private static void CheckRelayCoating()
        {
            CampaignState s = NewWorld(42);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            string relay = HomeValleyLayout.BuildingTypeSignalRelay;
            GridCell? spot = null;
            for (int r = 16; r < 60 && !spot.HasValue; r += 4)
            {
                foreach (GridCell c in new[] { new GridCell(core.X + r, core.Y), new GridCell(core.X - r, core.Y), new GridCell(core.X, core.Y + r), new GridCell(core.X, core.Y - r) })
                {
                    if (HomeGridService.ValidatePlacement(s, relay, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        spot = c;
                        break;
                    }
                }
            }
            if (!spot.HasValue)
            {
                Fail("D1 夹具：核心附近找不到能放中继的空地");
                return;
            }
            GridPlacementResult probe = HomeGridService.ValidatePlacement(s, relay, spot.Value, 0, asPlayerPlacement: false, checkCost: false);
            List<GridCell> cells = probe.Cells.ToList();
            void SetLevel(byte level)
            {
                foreach (GridCell c in cells)
                {
                    map.SetPollution(c, level);
                }
            }
            SetLevel(2);
            AcidMarshService.InvalidateCoating();
            GridPlacementResult noCoat = HomeGridService.ValidatePlacement(s, relay, spot.Value, 0, asPlayerPlacement: false, checkCost: false);
            bool refused = !noCoat.Ok && noCoat.Has(GridBlockReason.Pollution) && noCoat.Notes.Any(n => n.Contains("防腐涂层"));
            CampaignEventLedger.TryGrant(s, CampaignObjectiveTracker.BlueprintEventId(AcidMarshService.CoatingBlueprintId), "QuestBlueprint", s.PlaySeconds, "main.anticorrosion");
            AcidMarshService.InvalidateCoating();
            GridPlacementResult coated = HomeGridService.ValidatePlacement(s, relay, spot.Value, 0, asPlayerPlacement: false, checkCost: false);
            bool allowed = AcidMarshService.HasCoating(s) && coated.Ok && coated.Notes.Any(n => n.Contains("防腐涂层"));
            GridPlacementResult other = HomeGridService.ValidatePlacement(s, "generator_2", spot.Value, 0, asPlayerPlacement: false, checkCost: false);
            bool otherBlocked = !other.Ok && other.Has(GridBlockReason.Pollution);
            SetLevel(3);
            GridPlacementResult level3 = HomeGridService.ValidatePlacement(s, relay, spot.Value, 0, asPlayerPlacement: false, checkCost: false);
            bool l3Blocked = !level3.Ok && level3.Has(GridBlockReason.Pollution);
            // FG-GAP-239：3 级上被拦时写明正式出路（外缘有电的 T2 信号塔 / 机器结构槽的信号中继模块）；1 级（可建）不写
            bool l3Hint = level3.Notes.Any(n => n.Contains("信号塔") && n.Contains("信号中继") && n.Contains("500"));
            SetLevel(1);
            bool l1NoHint = HomeGridService.ValidatePlacement(s, relay, spot.Value, 0, asPlayerPlacement: false, checkCost: false).Notes.All(n => !n.Contains("信号塔"));
            SetLevel(0);
            Expect(refused && allowed && otherBlocked && l3Blocked && l3Hint && l1NoHint,
                $"D1 FG-GAP-164（第 3 项，按推荐默认执行，待用户确认）：2 级污染上没有防腐涂层时中继被拦并写明涂层（“{string.Join(" ", noCoat.Notes)}”，{refused}）；" +
                $"拿到涂层后可建（“{string.Join(" ", coated.Notes)}”，{allowed}）；其它建筑照旧被拦 {otherBlocked}；3 级仍不能建 {l3Blocked}，并写明出路（FG-GAP-239）“{string.Join(" ", level3.Notes)}”（{l3Hint}，1 级不写 {l1NoHint}）");

            string[] escortKeys = { "quest.notify.escort", "quest.main.anticorrosion.goal", "quest.side.rescue.goal", "quest.side.convoy.goal" };
            string[] wrong = escortKeys.Where(k => !GameText.Has(k) || GameText.Get(k).Contains("前哨站") || !GameText.Get(k).Contains("回家园") && !GameText.Get(k).Contains("开回家园")).ToArray();
            Expect(wrong.Length == 0,
                $"D2 FG-GAP-165：护送任务文本只写“回家园”（仓库不能新建，不承诺“带仓库的前哨站”）——不合格 {string.Join(",", wrong)}；“{GameText.Get("quest.main.anticorrosion.goal")}”");
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private static CampaignState NewWorldState(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            CoolingTowerService.ResetForTests();
            HeatRadiationService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgheatcity-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CampaignState NewWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            HeatRadiationService.ResetForTests();
            CoolingTowerService.ResetForTests();
            AcidMarshService.InvalidateCoating();
            CampaignState s = CampaignState.CreateNew("fgheatcity-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            if (!s.BlueprintRecords.Any(b => b.BlueprintId == BpGun))
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = BpGun, DisplayName = BpGun, ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) },
                }).ToArray();
            }
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            NotificationCenter.Bind(s);
            return s;
        }

        private static int SpawnIn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpGun, HomeValleyLayout.RegionId, at, 400f, 400f, "Player", 1);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                return r.LogicId;
            }
            Fail("夹具：登记机器失败 " + r.Message);
            return 0;
        }

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int u))
            {
                site.SetUnitPosition(u, at);
            }
        }

        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            CoolingTowerService.ResetForTests();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(1);
            return r.State;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgHeatDataCitySelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
