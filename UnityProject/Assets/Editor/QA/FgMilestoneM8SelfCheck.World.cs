using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
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
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// [M8 出口] 世界段：IC-REQ-022 六类反向（R1～R6，含 FGT-EXP-011 存读档、FGT-EXP-012 后台一致）与三段承接片段（L1 领地 / L2 领地控制 / L3 遗产）。
    /// 每段新开一个按种子生成的世界，起真实系统；夹具（写在每条断言里）：投送站研发与装舱 / 燃油（FgDropPodSelfCheck.NewWorld）、远征机器按正式出厂入口放在核心旁、
    /// 区域开放、管线燃油源、悬崖圈、阵亡（MachineRegistry.ApplyDamage 正式阵亡入口代替敌人火力）、覆盖注入（L1 / L2）、首领被击败与其余据点摧毁（L2）、遗迹揭雾与中继链（L3）。
    /// </summary>
    public static partial class FgMilestoneM8SelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const int KeepSlot = 9;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static string _dir;

        private static CampaignState S => CampaignSession.Current;
        private static int Hz => Math.Max(1, GameClock.StepHz);
        private static ExpeditionMarchState March => ExpeditionMarchService.Current(S);
        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static void WorldChecks()
        {
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgm8-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 1f / 30f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程）；" +
                     "世界段全部起真实系统（世界模拟、家园战斗内核与层级寻路、远征出发 / 行军 / 命令、投送舱、管线、领地控制、遗迹工单、真文件存读档）；夹具只搭场景");
                Step(CheckSignalLossAndFuel);
                Step(CheckRecallTargetDeath);
                Step(CheckPauseSpeedObserved);
                Step(CheckSaveLoad);
                Step(CheckPathFailure);
                Step(CheckTerritorySeeds);
                Step(CheckTerritoryControlChain);
                Step(CheckHeritageChain);
            }
            catch (Exception e)
            {
                Fail($"[M8 出口] 世界段抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.Reset();
                HomeValleyPowerGrid.ResetForTests();
                ResearchService.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
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
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
        }

        // ── 场景工具 ──────────────────────────────────────────────────────────────

        /// <summary>按种子新开一个有电的家园（FgDropPodSelfCheck.NewWorld：研发“投送站”后核心旁一座建成、有电的投送站），破碎都市 / 铸造前哨开放（Demo 解锁条件由 FGJ-M1 覆盖）。</summary>
        private static CampaignState World(int seed, out BuildingRecord station, bool withStation = true, float fuelLiters = 1000f)
        {
            SignalCoverageService.OverrideForTests = null;
            InputRouter.DebugSetReader(null);
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = FgDropPodSelfCheck.NewWorld(seed, out station, observe: true, pods: withStation ? 4 : 0, fuelLiters: fuelLiters, research: withStation);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            if (foundry != null && foundry.State == RegionState.Locked)
            {
                foundry.State = RegionState.Available;
            }
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>按正式出厂入口在核心旁放一台机器（装配站生产由 FGJ-M4 / M6 覆盖）。</summary>
        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset)
        {
            GridCell core = HomeGridService.CorePivot(s);
            GridCell g = SiegeService.NearestPassable(new GridCell(core.X + Mathf.RoundToInt(offset.x), core.Y + Mathf.RoundToInt(offset.y)), 10);
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, new Vector2(g.X, g.Y), 120f, 120f);
            if (!r.Success)
            {
                Fail("夹具：放机器失败：" + r.Message);
                return 0;
            }
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec) && !string.IsNullOrEmpty(rec.BlueprintId))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.LogicId;
        }

        /// <summary>开局修复（与 FGJ-M8 开局“右键修复信号塔”同一结果，修复流程由 FGJ-M8 repair_tw 覆盖）：信号塔修好通电 → 带宽 3 + 5、核心周围有覆盖。</summary>
        private static void RepairTower(CampaignState s)
        {
            BuildingRecord tower = s.BuildingRecords.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);
            if (tower != null)
            {
                tower.ConstructionState = BuildingConstructionState.Operational;
                tower.PowerState = BuildingPowerState.Powered;
            }
            HomeValleyPowerGrid.Recompute(s);
            SignalCoverageService.Invalidate();
            WorldSimulation.StepMany(Hz * 2);
        }

        /// <summary>FGJ-M8 同样的远征队编成：1 台工程机 + <paramref name="fighters"/> 台战斗机（带宽 1 + 2×N）。</summary>
        private static int[] MixedTeam(CampaignState s, int fighters)
        {
            var ids = new List<int> { Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(5f, -6f)) };
            for (int i = 0; i < fighters; i++)
            {
                ids.Add(Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(7f + 2f * i, -6f)));
            }
            WorldSimulation.StepMany(3);
            return ids.ToArray();
        }

        private static int[] Team(CampaignState s, int n, bool combat)
        {
            string chassis = combat ? HomeValleyLayout.Erc003ChassisId : HomeValleyLayout.Erc001ChassisId;
            string bp = combat ? HomeValleyLayout.BlueprintErc003Id : HomeValleyLayout.BlueprintErc001Id;
            int[] ids = Enumerable.Range(0, n).Select(i => Spawn(s, chassis, bp, new Vector2(5f + 2f * i, -6f))).ToArray();
            WorldSimulation.StepMany(3);
            return ids;
        }

        /// <summary>正式出发事务（派遣准备面板“出发”同一入口）；锚点先按正式流程解析完。</summary>
        private static bool Depart(ExpeditionDepartureService.ExpeditionTarget target, string siteId, int[] ids)
        {
            CampaignState s = S;
            for (int i = 0; i < 30 && !ExpeditionSiteAnchors.IsResolved(s, siteId); i++)
            {
                WorldSimulation.StepMany(Hz);
            }
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(ids, true, target);
            WorldSimulation.StepMany(3);
            if (!dep.Success || !ids.All(id => ExpeditionMarchService.IsTeamMember(S, id)))
            {
                Fail($"夹具：出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return false;
            }
            return true;
        }

        private static bool StepUntil(Func<bool> done, int maxSteps, int chunk = 10)
        {
            for (int i = 0; i < maxSteps; i += chunk)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(chunk);
            }
            return done();
        }

        private static void StepTo(long tick)
        {
            while (GameClock.Ticks < tick)
            {
                WorldSimulation.StepMany((int)Math.Min(int.MaxValue, Math.Max(1, tick - GameClock.Ticks)));
            }
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static bool Alive(int id) => Rec(id) is MachineRecord m && m.IsAlive;

        private static Vector2 PosOf(int id) => MachineRegistry.TryGetLivePosition(id, out Vector2 p) ? p : Rec(id)?.WorldPosition ?? Vector2.zero;

        private static bool OnPlanet(int id) => Rec(id) is MachineRecord m && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId;

        private static Vector2 Center(IEnumerable<int> ids)
        {
            List<Vector2> ps = ids.Where(Alive).Select(PosOf).ToList();
            return ps.Count == 0 ? Vector2.zero : new Vector2(ps.Average(p => p.x), ps.Average(p => p.y));
        }

        private static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        private static int NotifyMembers(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static string LatestDetail(string typeId) => NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId)?.Latest?.DetailText ?? string.Empty;

        /// <summary>离 <paramref name="from"/> 不超过 <paramref name="maxD"/> 格、能降落的点（先揭开那一片再按着陆条件查；按种子地形找，B25）；<paramref name="within"/> 里的机器都要在回收半径内。</summary>
        private static Vector2? LandingNear(CampaignState s, Vector2 from, float maxD, IReadOnlyList<int> within = null)
        {
            HomeGridService.RevealArea(s, from, maxD + 12f);
            for (float d = 0f; d <= maxD; d += 1f)
            {
                int n = d < 0.5f ? 1 : 24;
                for (int a = 0; a < n; a++)
                {
                    float ang = a * (360f / n) * Mathf.Deg2Rad;
                    var p = new Vector2(Mathf.Round(from.x + Mathf.Cos(ang) * d), Mathf.Round(from.y + Mathf.Sin(ang) * d));
                    if (DropPodService.CheckLanding(s, p.x, p.y, out _) != null)
                    {
                        continue;
                    }
                    if (within != null && within.Any(id => Vector2.Distance(PosOf(id), p) > DropPodService.RecallRadius - 1f))
                    {
                        continue;
                    }
                    return p;
                }
            }
            return null;
        }

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        /// <summary>整体指纹：行军域、投送域、机器（区域 / 生命 / 位置）、地上货物、领地控制；<paramref name="kernel"/> 时加家园战斗内核与寻路的状态哈希（同一份内存推进时比较用）。</summary>
        private static string Digest(bool kernel)
        {
            CampaignState s = S;
            var sb = new StringBuilder();
            sb.Append("tick=").Append(GameClock.Ticks);
            if (kernel)
            {
                sb.Append(";home=").Append(HomeSite?.Kernel.StateHash().ToString("X16")).Append(";nav=").Append(NavService.Kernel?.PendingHash().ToString("X16"));
            }
            sb.Append(";march=").Append(Json(ExpeditionMarchService.Current(s)));
            sb.Append(";drop=").Append(Json(DropPodService.StateOf(s)));
            foreach (MachineRecord m in (s.MachineRecords ?? Array.Empty<MachineRecord>()).OrderBy(x => x.LogicId))
            {
                Vector2 p = PosOf(m.LogicId);
                sb.Append(";m=").Append(m.LogicId).Append('@').Append(m.RegionId).Append(':').Append(m.Health.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(':').Append(F1(p.x)).Append(',').Append(F1(p.y));
            }
            foreach (GroundItemRecord g in (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null).OrderBy(g => g.SalvageInstanceId, StringComparer.Ordinal))
            {
                sb.Append(";g=").Append(g.SalvageInstanceId).Append('=').Append(g.ResourceType).Append('x').Append(g.Amount);
            }
            sb.Append(";inv=").Append(HomeInventory.Stock(s, "structural")).Append('/').Append(HomeInventory.Stock(s, ItemCatalog.ScrapId));
            return sb.ToString();
        }

        private static bool Save()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("存档写入失败：" + r.Message);
            }
            return r.Success;
        }

        /// <summary>读 <paramref name="fromSlot"/> 那份真文件（复制到运行槽位再读，原文件不被读档后的自动存档改写）：卸载世界 → 正式读档编排 → 家园续载。</summary>
        private static bool Restore(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
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
                return false;
            }
            CampaignSession.Set(RunSlot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return true;
        }

        private static void Select(HomeValleyController home, IReadOnlyList<int> ids)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                home.SquadCommands.SelectSingle(ids[i], i > 0);
            }
        }

        /// <summary>真实交战：每 0.5 游戏秒给没在攻击的存活成员下“右键敌人 = 攻击”（玩家同一个编队命令入口，目标 = 最近的内核敌方单位），直到 <paramref name="done"/>。</summary>
        private static readonly List<string> FightLog = new List<string>();
        private const float ApproachCells = 9f;

        private static bool Fight(IReadOnlyList<int> team, Vector2 at, Func<bool> done, int maxSeconds)
        {
            HomeValleyController home = WorldSimulation.Home;
            FightLog.Clear();
            for (int t = 0; t < maxSeconds * 2; t++)
            {
                if (t % 6 == 0 && FightLog.Count < 10)
                {
                    FightLog.Add($"[{t / 2}s] " + Sample(team, at) + " 事件“" + home.SquadCommands.RecentEvents.LastOrDefault() + "” 自卫 " + ExpeditionMarchService.SelfDefenseCount);
                }
                if (done())
                {
                    return true;
                }
                CombatSite site = HomeSite;
                if (site == null || team.All(id => !Alive(id)))
                {
                    return done();
                }
                int u = site.FindNearestHostile(at, 60f);
                if (u > 0 && site.Kernel.TryGetUnit(u, out BinGames.Sim.Combat.CombatUnitView hu))
                {
                    var hp = new Vector2((float)hu.Position.x, (float)hu.Position.y);
                    // 玩家的做法：离得远（攻击命令按直线逼近，地形挡住会停在射程外，FG-GAP-140）先右键地面走过去（移动走层级寻路），进了射程再右键敌人攻击。
                    var far = new List<int>();
                    var near = new List<int>();
                    foreach (int id in team.Where(Alive))
                    {
                        if (!site.TryGetMachineUnit(id, out int mu) || !site.TryGetCommand(mu, out BinGames.Sim.Combat.CombatCommand c))
                        {
                            continue;
                        }
                        float dist = Vector2.Distance(PosOf(id), hp);
                        if (dist > ApproachCells && c.Kind != BinGames.Sim.Combat.CombatCommandKind.Move)
                        {
                            far.Add(id);
                        }
                        else if (dist <= ApproachCells && c.Kind != BinGames.Sim.Combat.CombatCommandKind.Attack)
                        {
                            near.Add(id);
                        }
                    }
                    if (far.Count > 0)
                    {
                        Select(home, far);
                        home.SquadCommands.IssueMoveTo(hp, false);
                    }
                    if (near.Count > 0)
                    {
                        Select(home, near);
                        home.SquadCommands.IssueAttack("unit:" + u, false);
                    }
                }
                WorldSimulation.StepMany(Math.Max(1, Hz / 2));
            }
            return done();
        }

        private static string Sample(IReadOnlyList<int> team, Vector2 at)
        {
            CombatSite site = HomeSite;
            var sb = new StringBuilder();
            foreach (int id in team)
            {
                if (site != null && site.TryGetMachineUnit(id, out int mu) && site.Kernel.TryGetUnit(mu, out BinGames.Sim.Combat.CombatUnitView v))
                {
                    int h = site.FindNearestHostile(new Vector2((float)v.Position.x, (float)v.Position.y), 80f);
                    float d = h > 0 && site.Kernel.TryGetUnit(h, out BinGames.Sim.Combat.CombatUnitView hv) ? Vector2.Distance(new Vector2((float)hv.Position.x, (float)hv.Position.y), new Vector2((float)v.Position.x, (float)v.Position.y)) : -1f;
                    sb.Append(MachineNaming.Short(id)).Append(' ').Append(v.Health.ToString("0", CultureInfo.InvariantCulture)).Append(' ').Append(v.Command).Append("→").Append(v.CommandTarget).Append(" 敌距 ").Append(F1(d)).Append("；");
                }
            }
            return sb.ToString();
        }

        private static string FightDiag(IReadOnlyList<int> team, Vector2 at, int key)
        {
            CombatSite site = HomeSite;
            if (site == null)
            {
                return "没有家园战斗内核";
            }
            var sb = new StringBuilder();
            sb.Append("队伍键 ").Append(key).Append(" 剩 ").Append(site.CountSiegeGroup(key)).Append(" 台；");
            int u = site.FindNearestHostile(at, 200f);
            if (u > 0 && site.Kernel.TryGetUnit(u, out BinGames.Sim.Combat.CombatUnitView hv))
            {
                sb.Append("最近敌人 #").Append(u).Append(' ').Append(hv.Health.ToString("0", CultureInfo.InvariantCulture)).Append('/').Append(hv.MaxHealth.ToString("0", CultureInfo.InvariantCulture))
                    .Append(" 命令 ").Append(hv.Command).Append(" 位置 ").Append(F1((float)hv.Position.x)).Append(',').Append(F1((float)hv.Position.y)).Append("；");
            }
            foreach (int id in team)
            {
                if (site.TryGetMachineUnit(id, out int mu) && site.Kernel.TryGetUnit(mu, out BinGames.Sim.Combat.CombatUnitView v))
                {
                    sb.Append(MachineNaming.Short(id)).Append(' ').Append(v.Health.ToString("0", CultureInfo.InvariantCulture)).Append(" 命令 ").Append(v.Command).Append("→").Append(v.CommandTarget)
                        .Append(" 位置 ").Append(F1((float)v.Position.x)).Append(',').Append(F1((float)v.Position.y)).Append("；");
                }
                else
                {
                    sb.Append(MachineNaming.Short(id)).Append(Alive(id) ? " 无单位；" : " 阵亡；");
                }
            }
            sb.Append(" 过程：").Append(string.Join(" ", FightLog));
            return sb.ToString();
        }

        private sealed class SilentReader : IInputReader
        {
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        // ── R3 断网 / 失联 + R1 资源不足 ─────────────────────────────────────────────

        private static void CheckSignalLossAndFuel()
        {
            Line("  · R3 断网 / 失联（远征队走出覆盖）→ R1 资源不足（投送站没有燃油）：同一个世界（种子 8801），夹具 = 投送站（研发 + 4 舱、0 升油）、2 台工程机");
            CampaignState s = World(8801, out BuildingRecord st, true, 0f);
            if (st == null)
            {
                Fail("R3 / R1 夹具：没有投送站");
                return;
            }
            int[] team = Team(s, 2, false);
            if (!Depart(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, FracturedCityLayout.RegionId, team))
            {
                return;
            }
            ExpeditionSiteAnchors.TryGet(S, FracturedCityLayout.RegionId, out Vector2 anchor);
            int edge0 = ExpeditionMarchService.EdgeCueCount;
            int out0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchOutOfCoverage);
            bool edgeWhileCovered = false;
            bool left = StepUntil(() =>
            {
                if (!edgeWhileCovered && ExpeditionMarchService.EdgeCueCount > edge0)
                {
                    edgeWhileCovered = March.InCoverage;
                }
                return !March.InCoverage;
            }, 12000);
            int rejects = ExpeditionMarchService.CommandRejectCount;
            bool returnRejected = !ExpeditionMarchService.IssueCommand(S, ExpeditionMarchService.Command.Return, out string why) && why == "out-of-coverage"
                                  && ExpeditionMarchService.CommandRejectCount == rejects + 1 && March.Command == (int)ExpeditionMarchService.Command.Advance
                                  && March.Phase == ExpeditionMarchService.PhaseOutbound;
            int outId = team.FirstOrDefault(id => !SignalCoverageService.CanReceiveCommand(id));
            UplinkRequestResult up = SignalUplinkService.Request(outId, UplinkSource.MachineList, startCamera: false);
            bool noUplink = outId != 0 && !up.Accepted && up.Failure == UplinkFailure.OutOfCoverage;
            if (up.Accepted)
            {
                SignalUplinkService.CancelByPlayer();
            }
            float d0 = Vector2.Distance(Center(team), anchor);
            WorldSimulation.StepMany(Hz * 6);
            float d1 = Vector2.Distance(Center(team), anchor);
            string tip = GameText.Get("exp.cmd.tip.coverage");
            Expect(left && edgeWhileCovered && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchOutOfCoverage) > out0 && returnRejected && noUplink && d1 < d0 - 5f && !GameText.ContainsMarker(tip),
                $"R3 断网 / 失联（FGR-SIG-053）：走出覆盖前先提醒（提醒时还在覆盖里 {edgeWhileCovered}）、走出后提示“{GameText.Get("exp.cue.out_of_coverage")}”；覆盖外“返回”被拒（原因 {why}，行军条说明“{tip}”），" +
                $"接入 #{outId} 被拒（{up.Failure}）；队伍照最后的“前进”继续走（6 游戏秒又近了 {F1(d0 - d1)} 格）——不失控、不原地卡死");

            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(S, ExpeditionSiteAnchors.Route(S, FracturedCityLayout.RegionId), "m8r3_relay");
            int back0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchBackInCoverage);
            bool back = StepUntil(() => March.InCoverage, 300);
            UplinkRequestResult up2 = SignalUplinkService.Request(outId, UplinkSource.MachineList, startCamera: false);
            bool uplinkBack = up2.Accepted;
            SignalUplinkService.CancelByPlayer();
            bool hold = ExpeditionMarchService.IssueCommand(S, ExpeditionMarchService.Command.Hold, out string holdWhy);
            WorldSimulation.StepMany(Hz);
            bool standby = ExpeditionMarchService.StatusOf(S) == ExpeditionMarchService.TeamStatus.Standby;
            Expect(relays > 0 && back && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchBackInCoverage) > back0 && uplinkBack && hold && standby,
                $"R3 恢复：沿路线铺 {relays} 座中继塔（夹具登记建筑记录，覆盖服务按正式规则建覆盖源）→ 回到覆盖提示、能接入（{uplinkBack}{(uplinkBack ? string.Empty : "：" + up2.Failure)}）、" +
                $"“待命”生效、队伍状态待命（{hold && standby}{(hold ? string.Empty : "：" + holdWhy)}）");

            // R1：队伍在离家几百格的地方待命；投送站没有燃油 → 呼叫回收被拒。
            Vector2? point = LandingNear(S, Center(team), 6f, team);
            if (point == null)
            {
                Fail("R1 夹具：待命的远征队旁找不到能降落、两台都在回收半径里的点");
                return;
            }
            DropStationRecord rec = DropPodService.FindStation(S, st.BuildingId);
            string noFuelStatus = BuildingStatusService.Evaluate(S, st).Reason;
            string before = Digest(false);
            int rej0 = DropPodService.LastRejectCount;
            DropPreview p = DropPodService.PreviewRecall(S, st.BuildingId, point.Value);
            bool refused = !DropPodService.TryRecall(S, st.BuildingId, point.Value, out DropPreview p2);
            string reason = GameText.Format("drop.reason.fuel", DropPodService.Liters(p.FuelMl), DropPodService.Liters(p.FuelHaveMl));
            Expect(!p.Ok && p.Code == "fuel" && p.Machines.Count == 2 && p.Reason == reason && refused && p2.Code == "fuel" && DropPodService.LastRejectCount == rej0 + 1
                   && Digest(false) == before && noFuelStatus.Contains("没有燃油"),
                $"R1 资源不足（FGT-EXP-003 燃油）：投送站没有燃油时呼叫回收（预览要接 {p.Machines.Count} 台）被拒，原因“{p.Reason}”、拒绝音 1 次、世界逐字段不变；站状态写“{noFuelStatus.Split('\n')[0]}”");

            // 恢复：站边接上装燃油的管线（燃油源是测试捷径：管线内核直接给；精炼塔出油由 FgProductionSelfCheck 覆盖）。
            bool piped = ConnectFuel(S, st, out int src);
            long need = p.FuelMl;
            bool filled = piped && StepUntil(() => DropPodService.FuelMl(rec) >= need + 1000, Hz * 60, Math.Max(1, Hz / 4));
            if (src > 0)
            {
                PipeNetworkService.Kernel.RemoveProducer(src, out _);
            }
            int podReturns = ExpeditionMarchService.PodReturnCount;
            long filledMl = DropPodService.FuelMl(rec);
            DropPreview ok = DropPodService.PreviewRecall(S, st.BuildingId, point.Value);
            bool called = ok.Ok && DropPodService.TryRecall(S, st.BuildingId, point.Value, out _);
            List<DropPodRecord> flying = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateOutbound).ToList();
            if (flying.Count > 0)
            {
                StepTo(flying.Max(x => x.ArriveTick) + 2);
            }
            bool boarded = team.All(id => DropPodService.IsInPod(S, id));
            List<DropPodRecord> back2 = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateReturning).ToList();
            if (back2.Count > 0)
            {
                StepTo(back2.Max(x => x.ArriveTick) + 2);
            }
            WorldSimulation.StepMany(Hz * 2);
            bool home = team.All(id => OnPlanet(id) && Vector2.Distance(PosOf(id), st.Position) < 16f) && March.Phase == ExpeditionMarchService.PhaseReturned
                        && ExpeditionMarchService.PodReturnCount == podReturns + 1;
            Expect(piped && filled && called && boarded && home,
                $"R1 恢复：站边接上装燃油的管线，油箱进油到 {DropPodService.Liters(filledMl)} 升（要 {DropPodService.Liters(need)} 升）→ 再呼叫回收成功（{ok.Machines.Count} 舱）、" +
                $"舱到点接上两台（{boarded}）、回到站旁下舱，远征按“随投送舱返回”结算（阶段 {March.Phase}，{home}）");
        }

        /// <summary>站边铺两格管线、远端挂一个燃油源（与 FgDropPodSelfCheck I1 同一写法）。</summary>
        private static bool ConnectFuel(CampaignState s, BuildingRecord st, out int src)
        {
            src = 0;
            if (!PipeNetworkService.IsRunning)
            {
                Fail("R1 夹具：管线内核没在跑");
                return false;
            }
            GridCell? near = null, far = null;
            var foot = new List<GridCell>();
            HomeGridService.FootprintOf(st, foot);
            foreach (GridCell f in foot)
            {
                foreach (GridCell d in new[] { new GridCell(1, 0), new GridCell(-1, 0), new GridCell(0, 1), new GridCell(0, -1) })
                {
                    var n = new GridCell(f.X + d.X, f.Y + d.Y);
                    var n2 = new GridCell(n.X + d.X, n.Y + d.Y);
                    if (near == null && !foot.Contains(n) && HomeGridService.ValidatePipeCell(s, n, PipePieceKind.Pipe).Ok && HomeGridService.ValidatePipeCell(s, n2, PipePieceKind.Pipe).Ok)
                    {
                        near = n;
                        far = n2;
                    }
                }
            }
            if (near == null)
            {
                Fail("R1 夹具：投送站旁找不到能铺管线的两格");
                return false;
            }
            bool placed = PipeNetworkService.TryPlace(s, near.Value, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, far.Value, PipePieceKind.Pipe, 0, 0).Ok;
            src = PipeNetworkService.Kernel.AddProducer(far.Value.X, far.Value.Y, PipeNetworkService.FluidId("fuel"), 2_000_000, 2_000_000);
            return placed && src > 0;
        }

        // ── R2 目标死亡 ────────────────────────────────────────────────────────────

        private static void CheckRecallTargetDeath()
        {
            Line("  · R2 目标死亡：回收舱在飞时，它要接的那台阵亡（种子 8802；单台远征、附近没有队友能捡；阵亡走 MachineRegistry.ApplyDamage 正式阵亡入口，代替敌人火力）");
            CampaignState s = World(8802, out BuildingRecord st);
            if (st == null)
            {
                Fail("R2 夹具：没有投送站");
                return;
            }
            int[] team = Team(s, 1, false);
            if (!Depart(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, FracturedCityLayout.RegionId, team))
            {
                return;
            }
            WorldSimulation.StepMany(Hz * 25);
            ExpeditionMarchService.IssueCommand(S, ExpeditionMarchService.Command.Hold, out _);
            WorldSimulation.StepMany(Hz * 2);
            int a = team[0];
            LootPacketState lp = LootPickService.StateOf(S);
            const string heldId = "m8:r2-alloy";
            lp.Held = (lp.Held ?? Array.Empty<LootHeldRecord>()).Concat(new[]
            {
                new LootHeldRecord { HeldId = heldId, Machine = a, Kind = LootPickCatalog.KindItem, ItemId = "alloy", TargetId = string.Empty, Amount = 15, Space = LootPickCatalog.SpaceOf("alloy", 15) },
            }).ToArray();
            Vector2? point = LandingNear(S, Center(team), 6f, team);
            if (point == null)
            {
                Fail("R2 夹具：待命的远征队旁找不到能降落、机器在回收半径里的点");
                return;
            }
            int wiped0 = ExpeditionMarchService.WipedCount;
            bool called = DropPodService.TryRecall(S, st.BuildingId, point.Value, out DropPreview p);
            DropPodRecord forA = DropPodService.Pods(S).FirstOrDefault(x => x.State == DropPodService.StateOutbound && x.Mission == DropPodService.MissionRecall && x.RecallMachineId == a);
            WorldSimulation.StepMany(Hz);
            Vector2 death = PosOf(a);
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(3);
            GroundItemRecord dropped = HomeValleyCargo.FindGroundItemBySalvageId(S, heldId);
            bool droppedHere = !Alive(a) && dropped != null && Vector2.Distance(dropped.Position, death) < 2f && dropped.Amount == 15;
            if (forA != null)
            {
                StepTo(forA.ArriveTick + 2);
            }
            string lineA = forA != null ? DropPodService.PodLine(S, forA, GameClock.Ticks) : string.Empty;
            bool emptyBack = forA != null && forA.State == DropPodService.StateReturning && forA.MachineId == 0 && forA.CargoAmount == 0
                             && lineA.Contains(GameText.Get("drop.note.nothing_here")) && HomeValleyCargo.FindGroundItemBySalvageId(S, heldId) != null;
            if (forA != null)
            {
                StepTo(forA.ArriveTick + 2);
            }
            WorldSimulation.StepMany(Hz * 2);
            bool wiped = ExpeditionMarchService.WipedCount == wiped0 + 1 && !ExpeditionMarchService.IsAway(S);
            Expect(called && p.Machines.Count == 1 && forA != null && droppedHere && emptyBack && wiped,
                $"R2 目标死亡：舱去接 {MachineNaming.Short(a)}，舱在飞时它阵亡，它货舱里的 15 合金掉在原地（{droppedHere}）；舱到点不接别的、不装货、空返，舱列表写“{lineA}”（{emptyBack}）；" +
                $"远征按全灭结算（{wiped}）");

            // 恢复：再呼叫一次回收——回收点只剩地上的货，派一个货物舱捡回家园。
            int alloy0 = HomeInventory.Stock(S, "alloy");
            DropPreview again = DropPodService.PreviewRecall(S, st.BuildingId, point.Value);
            bool called2 = again.Ok && again.Machines.Count == 0 && again.GroundCargo >= 15 && DropPodService.TryRecall(S, st.BuildingId, point.Value, out _);
            List<DropPodRecord> cargoPods = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateOutbound).ToList();
            if (cargoPods.Count > 0)
            {
                StepTo(cargoPods.Max(x => x.ArriveTick) + 2);
            }
            List<DropPodRecord> cargoBack = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateReturning).ToList();
            if (cargoBack.Count > 0)
            {
                StepTo(cargoBack.Max(x => x.ArriveTick) + 2);
            }
            WorldSimulation.StepMany(Hz * 2);
            int got = HomeInventory.Stock(S, "alloy") - alloy0 + (S.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => g != null && g.ResourceType == "alloy" && Vector2.Distance(g.Position, st.Position) < 16f).Sum(g => g.Amount);
            Expect(called2 && HomeValleyCargo.FindGroundItemBySalvageId(S, heldId) == null && got >= 15,
                $"R2 恢复：再呼叫一次回收，预览“{DropPodService.PreviewText(again).Replace("\n", " / ")}”（只剩地上的货：{again.GroundCargo} 个）→ 货物舱把掉在原地的合金捡回家园（入库 / 站旁 {got} 个）");
        }

        // ── R4 暂停、倍速、观察（FGT-EXP-012）────────────────────────────────────────

        /// <summary>行军 + 投送舱在飞的存档：2 台工程机出发后走到进入领地（或 40 游戏秒），投送站往 300～460 格外投 20 结构材（远征队与投送舱同时在途）。</summary>
        private static bool BuildMixedSave(int seed, bool untilTerritory, out int[] team)
        {
            team = Array.Empty<int>();
            CampaignState s = World(seed, out BuildingRecord st);
            if (st == null)
            {
                Fail("夹具：没有投送站");
                return false;
            }
            team = Team(s, 2, false);
            if (!Depart(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, FracturedCityLayout.RegionId, team))
            {
                return false;
            }
            if (untilTerritory)
            {
                StepUntil(() => !string.IsNullOrEmpty(March.TerritoryId) || March.Phase != ExpeditionMarchService.PhaseOutbound, 20000);
            }
            else
            {
                WorldSimulation.StepMany(Hz * 40);
            }
            if (March.Phase != ExpeditionMarchService.PhaseOutbound)
            {
                Fail($"夹具：存档前远征队已经不在行军中（阶段 {March.Phase}）");
                return false;
            }
            Vector2? target = null;
            for (float d = 300f; d <= 460f && target == null; d += 20f)
            {
                for (int a = 0; a < 18 && target == null; a++)
                {
                    float ang = a * 20f * Mathf.Deg2Rad;
                    Vector2 p = st.Position + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    target = LandingNear(S, new Vector2(Mathf.Round(p.x), Mathf.Round(p.y)), 0f);
                }
            }
            int runner = Spawn(S, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, Vector2.zero);
            HomeInventory.Add(S, ItemCatalog.Find("structural"), 20, clampToSpace: false);
            bool launched = target.HasValue && DropPodService.TryLaunch(S, st.BuildingId, target.Value, Array.Empty<int>(), "structural", 20, out DropPreview lp) && lp.Ok;
            WorldSimulation.StepMany(Hz);
            if (!launched || !DropPodService.Pods(S).Any(x => x.State == DropPodService.StateOutbound))
            {
                Fail($"夹具：投送没有起飞（落点 {target}，机器 {runner}）");
                return false;
            }
            return Save();
        }

        private static void CheckPauseSpeedObserved()
        {
            Line("  · R4 暂停与倍速 + FGT-EXP-012 后台一致：远征队行军 + 投送舱在飞同时进行（种子 8804，从同一份真文件存档出发）");
            if (!BuildMixedSave(8804, false, out _))
            {
                return;
            }
            int steps = Hz * 20;
            Restore(Slot);
            long target = GameClock.Ticks + steps;
            WorldSimulation.StepMany(steps);
            string headless = Digest(true);
            bool landed = DropPodService.Pods(S).All(x => x.State != DropPodService.StateOutbound);

            Restore(Slot);
            InputRouter.DebugSetReader(new SilentReader());
            int frame = 0;
            bool followed = WorldView.ToggleFollowExpedition();
            while (GameClock.Ticks < target && frame < 20000)
            {
                if (frame % 120 == 60)
                {
                    WorldView.FocusHomeCore();
                }
                else if (frame % 120 == 0 && frame > 0)
                {
                    WorldView.ToggleFollowExpedition();
                }
                WorldSimulation.Frame(1f / 60f, target);
                frame++;
            }
            InputRouter.DebugSetReader(null);
            string observed = Digest(true);

            var frames = new Dictionary<float, int>();
            var bySpeed = new List<string>();
            foreach (float speed in new[] { 0.5f, 3f })
            {
                Restore(Slot);
                GameClock.SetSpeed(speed);
                int n = 0;
                while (GameClock.Ticks < target && n < 40000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                    n++;
                }
                GameClock.SetSpeed(1f);
                frames[speed] = n;
                bySpeed.Add(Digest(true));
            }
            Restore(Slot);
            string beforePause = Digest(true);
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            string paused = Digest(true);
            GameClock.SetPaused(false);
            float ratio = frames[3f] > 0 ? frames[0.5f] / (float)frames[3f] : 0f;
            Expect(landed && followed && observed == headless && bySpeed.All(x => x == headless) && beforePause == paused && ratio > 5.5f && ratio < 6.5f,
                $"R4 / FGT-EXP-012：20 游戏秒里投送舱着陆放下货物（{landed}）、远征队继续行军；镜头跟随远征队、飞回家园、再跟上（{frame} 帧）与从不观察的无头推进逐字段一致（{observed == headless}）；" +
                $"0.5x / 3x 跑到同一步结果相同（{bySpeed.All(x => x == headless)}），真实帧 {frames[0.5f]} / {frames[3f]}（比 {ratio:0.00}，应为 6）；暂停 120 帧整体不动（{beforePause == paused}）");
        }

        // ── R5 存读档（FGT-EXP-011）─────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · R5 存读档（FGT-EXP-011）：远征队在领地里行军、投送舱在飞时真文件存档（种子 8805）");
            if (!BuildMixedSave(8805, true, out int[] team))
            {
                return;
            }
            string territory = March.TerritoryId;
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(KeepSlot), true);
            string atSave = Digest(false);
            int steps = Hz * 30;
            WorldSimulation.StepMany(steps);
            string live = Digest(false);
            Restore(KeepSlot);
            string atLoad = Digest(false);
            bool resumed = March.Phase == ExpeditionMarchService.PhaseOutbound && March.TerritoryId == territory && team.All(id => ExpeditionMarchService.IsTeamMember(S, id))
                           && DropPodService.Pods(S).Any(x => x.State == DropPodService.StateOutbound);
            WorldSimulation.StepMany(steps);
            string loaded = Digest(false);
            Restore(KeepSlot);
            WorldSimulation.StepMany(steps);
            string loadedAgain = Digest(false);
            Expect(!string.IsNullOrEmpty(territory) && atSave == atLoad && resumed && live == loaded && loaded == loadedAgain,
                $"R5 / FGT-EXP-011：存档时远征队在领地 {territory} 里行军、投送舱在飞；读档后行军域 / 投送域 / 机器 / 地上货物逐字段一致（{atSave == atLoad}）、接着行军（{resumed}）；" +
                $"读档接着跑 30 游戏秒 = 不读档一路跑（{live == loaded}），再读一次结果相同（{loaded == loadedAgain}）" +
                (live == loaded ? string.Empty : $"\n      不读档：{live}\n      读档后：{loaded}") + (atSave == atLoad ? string.Empty : $"\n      存档时：{atSave}\n      读档时：{atLoad}"));
        }

        // ── R6 路径失败 ────────────────────────────────────────────────────────────

        private static void CheckPathFailure()
        {
            Line("  · R6 路径失败：锚点被悬崖围住（场景夹具，改的是真实格网），种子 8806");
            CampaignState s = World(8806, out BuildingRecord st);
            if (st == null)
            {
                Fail("R6 夹具：没有投送站");
                return;
            }
            int[] team = Team(s, 2, false);
            ExpeditionSiteAnchors.EnsureResolved(s);
            for (int i = 0; i < 30 && !ExpeditionSiteAnchors.IsResolved(s, FracturedCityLayout.RegionId); i++)
            {
                WorldSimulation.StepMany(Hz);
            }
            ExpeditionSiteAnchors.TryGet(s, FracturedCityLayout.RegionId, out Vector2 anchor);
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            int ringR = Mathf.CeilToInt(ExpeditionMarchService.ArriveRadius) + 4;
            var ring = new List<(GridCell Cell, byte Was)>();
            try
            {
                FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId), "m8r6_relay");
                if (!Depart(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, FracturedCityLayout.RegionId, team))
                {
                    return;
                }
                // 出发走一段（检查单按出发时的地形放行）之后，锚点四周塌成悬崖（场景夹具，改的是真实格网）：路上才发现走不进去。
                WorldSimulation.StepMany(Hz * 20);
                for (int dx = -ringR - 3; dx <= ringR + 3; dx++)
                {
                    for (int dy = -ringR - 3; dy <= ringR + 3; dy++)
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d < ringR || d > ringR + 2.5f)
                        {
                            continue;
                        }
                        var cell = new GridCell(Mathf.RoundToInt(anchor.x) + dx, Mathf.RoundToInt(anchor.y) + dy);
                        ring.Add((cell, map.GetTerrain(cell)));
                        map.SetTerrain(cell, cliff);
                    }
                }
                WorldSimulation.StepMany(2);
                float leftHome = Vector2.Distance(Center(team), new Vector2(HomeGridService.CorePivot(S).X, HomeGridService.CorePivot(S).Y));
                int halt0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted);
                int arrivals = ExpeditionMarchService.ArriveCount;
                bool halted = StepUntil(() => March.Halted == ExpeditionMarchService.HaltRouteTruncated, 15000);
                bool outside = team.All(id => OnPlanet(id) && Vector2.Distance(PosOf(id), anchor) > ringR - 1f);
                bool explained = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted) == halt0 + 1 && ExpeditionMarchService.StatusOf(S) == ExpeditionMarchService.TeamStatus.Standby
                                 && ExpeditionMarchService.EtaSeconds(S) < 0 && ExpeditionMarchService.ArriveCount == arrivals && March.Phase == ExpeditionMarchService.PhaseOutbound;
                Vector2 c0 = Center(team);
                WorldSimulation.StepMany(Hz * 5);
                bool stays = Vector2.Distance(Center(team), c0) < 3f;
                float haltFromHome = Vector2.Distance(Center(team), new Vector2(HomeGridService.CorePivot(S).X, HomeGridService.CorePivot(S).Y));
                Expect(halted && outside && explained && stays && leftHome > 50f && haltFromHome >= leftHome - 1f,
                    $"R6 路径失败：出发 20 游戏秒后（离核心 {F1(leftHome)} 格）锚点被悬崖围住，下一次路线检查就在离核心 {F1(haltFromHome)} 格处停下，行军“{GameText.Get("exp.cue.halted")}”一次、成员停在圈外（{outside}）、状态待命、没有预计到达时间、没有到达、原地等候不乱走（{explained && stays}）");

                // 恢复：从截断点用投送舱把队伍接回家园（FGR-EXP-015 回收是正式的撤离方式）。
                Vector2? point = LandingNear(S, Center(team), 6f, team);
                int podReturns = ExpeditionMarchService.PodReturnCount;
                bool called = point.HasValue && DropPodService.TryRecall(S, st.BuildingId, point.Value, out _);
                List<DropPodRecord> flying = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateOutbound).ToList();
                if (flying.Count > 0)
                {
                    StepTo(flying.Max(x => x.ArriveTick) + 2);
                }
                List<DropPodRecord> back = DropPodService.Pods(S).Where(x => x.State == DropPodService.StateReturning).ToList();
                if (back.Count > 0)
                {
                    StepTo(back.Max(x => x.ArriveTick) + 2);
                }
                WorldSimulation.StepMany(Hz * 2);
                bool home = team.All(id => OnPlanet(id) && Vector2.Distance(PosOf(id), st.Position) < 16f) && March.Phase == ExpeditionMarchService.PhaseReturned
                            && ExpeditionMarchService.PodReturnCount == podReturns + 1;
                Expect(called && home, $"R6 恢复：在截断点旁呼叫投送舱（落点 {point}）→ 两台随舱回到家园、远征按“随投送舱返回”结算（{home}）——截断不软锁");
            }
            finally
            {
                foreach ((GridCell cell, byte was) in ring)
                {
                    map.SetTerrain(cell, was);
                }
            }
        }

        // ── L1 领地片段（DEBT-FG8GEN02-08）──────────────────────────────────────────

        private static readonly int[] TerritorySeeds = { 1, 42, 20261017 };

        private static void CheckTerritorySeeds()
        {
            Line("  · L1 领地片段（DEBT-FG8GEN02-08）：种子 1 / 42 / 20261017 各一次——真实出发行军 → 进入领地名称提示 → 远征队到室内入口被拒（行军中）→ 家园机器走到入口进入 → 走到出口离开（覆盖注入：远程命令处处可下，覆盖网本身由 R3 / FGJ-M8 验证）");
            var ok = new List<string>();
            var bad = new List<string>();
            foreach (int seed in TerritorySeeds)
            {
                string why;
                string info = string.Empty;
                try
                {
                    why = TerritorySegment(seed, out info);
                }
                catch (Exception e)
                {
                    why = "抛异常：" + e.Message;
                }
                finally
                {
                    SignalCoverageService.OverrideForTests = null;
                }
                (why == null ? ok : bad).Add(why == null ? $"{seed}（{info}）" : $"{seed}：{why}");
            }
            Expect(bad.Count == 0, "L1 三个种子都成立：" + string.Join("；", ok) + (bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad)));
        }

        private static void CoverEverywhere()
        {
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            SignalCoverageService.Invalidate();
        }

        private static string TerritorySegment(int seed, out string info)
        {
            info = string.Empty;
            CampaignState s = World(seed, out _, withStation: false);
            RepairTower(s);
            CoverEverywhere();
            int[] team = MixedTeam(s, 1);
            int cue0 = ExpeditionMarchService.TerritoryCueCount;
            int fb0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchTerritory);
            if (!Depart(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, FracturedCityLayout.RegionId, team))
            {
                return "出发失败";
            }
            bool entered = StepUntil(() => !string.IsNullOrEmpty(March.TerritoryId) || March.Phase != ExpeditionMarchService.PhaseOutbound, 20000);
            string tid = March.TerritoryId;
            PlannedTerritory t = WorldGenService.PlanFor(S).Find(tid);
            Vector2 c = Center(team);
            PlannedTerritory at = WorldGenQuery.TerritoryAt(S, Mathf.FloorToInt(c.x), Mathf.FloorToInt(c.y));
            string name = t != null ? GameText.Get(t.NameKey) : string.Empty;
            if (!entered || t == null || !t.IsFaction || at?.Id != tid || ExpeditionMarchService.TerritoryCueCount != cue0 + 1 || FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchTerritory) != fb0 + 1
                || string.IsNullOrEmpty(name) || GameText.ContainsMarker(name) || !GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstTerritoryEntered))
            {
                return $"进入领地提示不对（领地 {tid}，提示 {ExpeditionMarchService.TerritoryCueCount - cue0} 次，名称“{name}”，阶段 {March.Phase}）";
            }
            ExpeditionMarchService.IssueCommand(S, ExpeditionMarchService.Command.Hold, out _);
            PlannedAnchor gate = InteriorSiteService.EntranceFor(S, "relay_hall");
            if (gate == null)
            {
                return "这个世界没有地下中继大厅入口";
            }
            Vector2 e = TerritoryAnchorChecks.EntryOf(S, gate);
            HomeGridService.RevealArea(S, e, 30f);
            HomeValleyController home = WorldSimulation.Home;
            Select(home, team);
            home.SquadCommands.IssueMoveTo(e, false);
            float dist0 = Vector2.Distance(Center(team), e);
            bool atGate = StepUntil(() => team.All(id => Alive(id) && Vector2.Distance(PosOf(id), e) <= gate.EntryRadius - 0.5f), Hz * 400, Hz / 2);
            string refuse = InteriorSiteService.CheckEnter(S, gate, out List<int> ready);
            if (!atGate || refuse == null || !refuse.Contains(GameText.Format("interior.reason.marching", team.Length).Trim()) || ready.Count != 0)
            {
                return $"行军中的远征队走到室内入口（{F1(dist0)} 格，到了 {atGate}）应被拒并写明“先到达目的地或返回家园”，实际“{refuse}”（可进 {ready?.Count}）";
            }
            // 正式规则（FG8-GEN-02）：行军中的远征队不能进室内；进室内的是家园机器——右键地面派一台战斗机从核心走到入口。
            int walker = Spawn(S, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(-6f, -6f));
            WorldSimulation.StepMany(3);
            Select(home, new[] { walker });
            home.SquadCommands.IssueMoveTo(e + new Vector2(1.5f, 1.5f), false);
            float walk0 = Vector2.Distance(PosOf(walker), e);
            bool walked = StepUntil(() => Alive(walker) && Vector2.Distance(PosOf(walker), e) <= gate.EntryRadius - 0.5f, Hz * 600, Hz / 2);
            string refuse2 = InteriorSiteService.CheckEnter(S, gate, out List<int> ready2);
            if (!walked || refuse2 != null || !ready2.Contains(walker) || team.Any(ready2.Contains))
            {
                return $"家园机器右键地面走到入口（{F1(walk0)} 格，到了 {walked}）后不能进入（{refuse2}；可进 {ready2?.Count}）";
            }
            int notes0 = NotifyMembers("interior_transit");
            bool inOk = InteriorSiteService.TryEnter(S, gate.Id, out string why);
            InteriorSiteController site = WorldSimulation.Interior("relay_hall");
            if (!inOk || site == null || Rec(walker)?.RegionId != "relay_hall" || team.Any(id => Rec(id)?.RegionId == "relay_hall") || NotifyMembers("interior_transit") != notes0 + 1)
            {
                return $"进入室内失败（{why}）";
            }
            site.SquadCommands.SelectSingle(walker, false);
            site.SquadCommands.IssueMoveTo(site.ExitPoint + new Vector2(0f, 1f), false);
            string exitWhy = null;
            bool outOk = false;
            for (int i = 0; i < 240 && !outOk; i++)
            {
                WorldSimulation.StepMany(Math.Max(1, Hz / 2));
                outOk = InteriorSiteService.TryExit(S, "relay_hall", out exitWhy);
            }
            WorldSimulation.StepMany(3);
            bool back = OnPlanet(walker) && Vector2.Distance(Rec(walker).WorldPosition, e) < 15f && WorldSimulation.Interior("relay_hall") == null;
            if (!outOk || !back)
            {
                return $"走到出口离开失败（{exitWhy}；回到地表入口旁 {back}）";
            }
            info = $"进入“{name}”提示 1 次；远征队走 {F1(dist0)} 格到入口被拒“{refuse}”；家园机器右键地面走 {F1(walk0)} 格到入口，进入 / 走到出口离开各 1 次";
            return null;
        }

        // ── L2 领地控制片段（DEBT-FG8EXP03-05）─────────────────────────────────────────

        private static void CheckTerritoryControlChain()
        {
            Line("  · L2 领地控制片段（DEBT-FG8EXP03-05，种子 42、驻军最少的那片阵营领地）：行军进入领地 → 走近据点、驻军迎战 → 走近后右键攻击真实交战摧毁、控制度下降 → 平定（领地首领被击败）→ 收复 → 精英残部刷新、醒来迎战");
            try
            {
                ControlChain();
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
            }
        }

        private static void ControlChain()
        {
            CampaignState s = World(42, out _, withStation: false);
            RepairTower(s);
            CoverEverywhere();
            // 目标：两片阵营领地（铸造 / 静默）里驻军最少的那个据点（M8 开局四台机器打得下来的；按种子世界挑，B25）。
            GridCell core0 = HomeGridService.CorePivot(s);
            var candidates = new List<(OutpostRecord O, int G)>();
            foreach (OutpostRecord x in WorldOutpostSystem.Outposts(s).Where(x => x.Kind == WorldOutpostSystem.KindTerritory && (x.TerritoryId == "foundry" || x.TerritoryId == "silent") && !x.Destroyed))
            {
                WorldOutpostSystem.Peek(s, x, out int g, out _);
                if (g > 0)
                {
                    candidates.Add((x, g));
                }
            }
            if (candidates.Count == 0)
            {
                Fail("L2 种子 42 的铸造 / 静默领地里没有带驻军的据点");
                return;
            }
            (OutpostRecord o, int g0) = candidates.OrderBy(x => x.G).ThenBy(x => Vector2.Distance(new Vector2(x.O.CellX, x.O.CellY), new Vector2(core0.X, core0.Y)))
                .ThenBy(x => x.O.OutpostId, StringComparer.Ordinal).First();
            string tid = o.TerritoryId;
            Line($"    L2 目标据点：{o.OutpostId}（{tid}，驻军 {g0}；候选 {candidates.Count} 个，驻军 {candidates.Min(x => x.G)}～{candidates.Max(x => x.G)}）");
            int[] team = MixedTeam(s, 3);
            bool foundry = tid == "foundry";
            if (!Depart(foundry ? ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost : ExpeditionDepartureService.ExpeditionTarget.SilentRuins,
                    foundry ? FoundryOutpostLayout.RegionId : FracturedCityLayout.RegionId, team))
            {
                return;
            }
            bool entered = StepUntil(() => March.TerritoryId == tid || March.Phase != ExpeditionMarchService.PhaseOutbound, 30000);
            ExpeditionMarchService.IssueCommand(S, ExpeditionMarchService.Command.Hold, out _);
            WorldSimulation.StepMany(Hz);
            Vector2 c = Center(team);
            if (!entered || March.TerritoryId != tid)
            {
                Fail($"L2 行军没有进入领地 {tid}（领地 {March.TerritoryId}，阶段 {March.Phase}）");
                return;
            }
            WorldOutpostSystem.Peek(S, o, out int garrison, out _);
            var op = new Vector2(o.CellX, o.CellY);
            HomeValleyController home = WorldSimulation.Home;
            int[] fighters = team.Skip(1).ToArray(); // 工程机没有主武器，留在待命处
            Select(home, fighters);
            home.SquadCommands.IssueMoveTo(op + (c - op).normalized * 8f, false);
            bool engaged = StepUntil(() => o.Engaged, Hz * 200, Hz / 2);
            int units = engaged ? HomeSite.CountSiegeGroup(o.EngageKey) : 0;
            int before = TerritoryControlService.Find(S, tid).Control;
            int destroyed0 = TerritoryControlService.StateOf(S).OutpostsDestroyed;
            bool killed = engaged && Fight(fighters, op, () => o.Destroyed, 240);
            if (!killed)
            {
                WorldOutpostSystem.Peek(S, o, out int gLeft, out _);
                Line($"    L2 诊断：据点 ({F1(op.x)},{F1(op.y)}) 迎战 {o.Engaged} 驻军 {gLeft}；" + FightDiag(fighters, op, o.EngageKey));
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            int after = TerritoryControlService.Find(S, tid).Control;
            int alive = fighters.Count(Alive);
            Expect(engaged && units == garrison && killed && after < before && after == TerritoryControlService.ComputeControl(S, tid, out _, out _)
                   && TerritoryControlService.StateOf(S).OutpostsDestroyed == destroyed0 + 1,
                $"L2 ① 行军进入领地 {tid} → 右键地面走近据点 {o.OutpostId}（离队伍 {F1(Vector2.Distance(op, c))} 格）：驻军就地展开 {units} / {garrison} 台迎战；" +
                $"右键敌人 = 攻击 + 远征队自卫，真实交战摧毁据点（{killed}，{fighters.Length} 台战斗机剩 {alive} 台；工程机留在待命处）；控制度 {before}% → {after}%（与公式一致）");

            // ② 平定：领地首领被击败——铸造 = 主核心被击败（Demo 铸造首领战的正式结果状态）；静默 = 寂听主脑的首领 Story 入口 MarkLeaderDefeated（首领战在 FG9-FAC-01）。
            int status0 = NotifyMembers(TerritoryControlService.NotifyStatus);
            if (foundry)
            {
                FoundryOutpostRegion.Find(S).CoreState = CoreBossState.Destroyed.ToString();
            }
            else
            {
                TerritoryControlService.MarkLeaderDefeated(S, tid);
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            TerritoryControlRecord r = TerritoryControlService.Find(S, tid);
            bool pacifying = r.Status == TerritoryControlService.StatusPacifying && r.LeaderDefeated && NotifyMembers(TerritoryControlService.NotifyStatus) > status0
                             && LatestDetail(TerritoryControlService.NotifyStatus).Contains(GameText.Get("territory.leader." + tid));
            // ③ 收复：其余据点按正式摧毁入口摧毁到控制度 < 收复线（逐个真实交战已在 ① 证明，夹具省掉重复的战斗）。
            foreach (OutpostRecord x in WorldOutpostSystem.Outposts(S).Where(x => x.Kind == WorldOutpostSystem.KindTerritory && x.TerritoryId == tid && !x.Destroyed)
                         .OrderBy(x => x.OutpostId, StringComparer.Ordinal).ToList())
            {
                if (TerritoryControlService.ComputeControl(S, tid, out _, out _) < TerritoryControlService.ReclaimBelow)
                {
                    break;
                }
                WorldOutpostSystem.MarkDestroyed(S, x.OutpostId);
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            string label = TerritoryControlService.MapLabel(S, WorldGenService.PlanFor(S).Find(tid));
            bool reclaimed = r.Status == TerritoryControlService.StatusReclaimed && LatestDetail(TerritoryControlService.NotifyStatus).Contains("已收复")
                             && label.Contains(GameText.Get("territory.status.reclaimed")) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstTerritoryReclaimed);
            Expect(pacifying && reclaimed,
                $"L2 ② {(foundry ? "铸造主核心被击败" : "寂听主脑的首领 Story 入口")} → 领地 {tid} 进入平定（通知写首领名：{pacifying}）；③ 控制度降到 {r.Control}%（< {TerritoryControlService.ReclaimBelow}）→ 已收复：通知 + 地图“{label}”（{reclaimed}）");

            // ④ 精英残部：7 个游戏日后刷新（夹具：把下一批的时刻拉到下一步，刷新本身走正式的每步判定）→ 走近迎战 → 右键攻击击溃 → 精英留下数据包。
            r.NextEliteTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            OutpostRecord rem = WorldOutpostSystem.Find(S, r.RemnantId);
            if (rem == null || rem.Kind != WorldOutpostSystem.KindRemnant)
            {
                Fail("L2 ④ 收复后到刷新点没出精英残部");
                return;
            }
            var ra = new Vector2(rem.CellX, rem.CellY);
            List<int> living = fighters.Where(Alive).ToList();
            bool remEngaged = false;
            int eliteUnits = 0;
            if (living.Count > 0)
            {
                Select(home, living);
                home.SquadCommands.IssueMoveTo(ra + (Center(living) - ra).normalized * 8f, false);
                remEngaged = StepUntil(() => rem.Engaged, Hz * 300, Hz / 2);
                eliteUnits = remEngaged ? HomeSite.CountSiegeGroup(rem.EngageKey) : 0;
            }
            Expect(remEngaged && eliteUnits == TerritoryControlService.Row(tid).EliteCount && rem.ReinforceStopped && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstEliteRemnant),
                $"L2 ④ 已收复领地里精英残部刷新（{rem.OutpostId}，离队伍 {F1(Vector2.Distance(ra, c))} 格、不增援）：右键地面走近，醒来 {eliteUnits} 台精英迎战" +
                "（击溃精英、留下数据包的玩家路径由 FGJ-M8 elite_move / elite_killed 实跑，同一个残部机制）");
        }

        // ── L3 遗产片段（DEBT-FG8EXP04-04）──────────────────────────────────────────

        private static void CheckHeritageChain()
        {
            Line("  · L3 遗产片段（DEBT-FG8EXP04-04，种子 8901）：按正式放置结果去第一幕范围（250～900 格）的一处遗迹——夹具 = 那一片揭雾（代替侦察）、沿直线铺中继链（覆盖）");
            CampaignState s = World(8901, out _, withStation: false);
            RepairTower(s);
            HeritageService.EnsurePlaced(s);
            GridCell core = HomeGridService.CorePivot(s);
            var picks = new List<(string Id, string Relic, HomeExplorationService.RelicInfo Info)>();
            foreach (var h in HeritageService.All)
            {
                string relic = HeritageService.RelicOf(s, h.Id);
                if (relic != null && HomeExplorationService.TryGetRelic(s, relic, out HomeExplorationService.RelicInfo info) && info.Distance >= 250 && info.Distance <= 900)
                {
                    picks.Add((h.Id, relic, info));
                }
            }
            if (picks.Count == 0)
            {
                Fail("L3 正式放置结果里没有第一幕范围（250～900 格）的遗产");
                return;
            }
            (string hid, string relicId, HomeExplorationService.RelicInfo ri) = picks.OrderBy(x => x.Info.Distance).ThenBy(x => x.Id, StringComparer.Ordinal).First();
            var relicPos = new Vector2(ri.X, ri.Y);
            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, new List<Vector2> { new Vector2(core.X, core.Y), relicPos }, "m8l3_relay");
            HomeGridService.RevealArea(s, relicPos, 30f);
            WorldSimulation.StepMany(Hz * 2);
            HomeValleyController home = WorldSimulation.Home;
            HomeValleyMachineMarker m = home.MachineMarkersForTests.Where(x => x != null && x.IsValid && MachineRegistry.TryGetRecord(x.LogicId, out MachineRecord r)
                                                                                && (r.ChassisId == HomeValleyLayout.Erc001ChassisId || r.ChassisId == HomeValleyLayout.Erc002ChassisId))
                .OrderBy(x => x.LogicId).FirstOrDefault();
            if (m == null)
            {
                Fail("L3 夹具：家园里没有搬运机");
                return;
            }
            int found0 = NotifyMembers(HeritageService.NotifyType);
            bool handled = home.ContextRelicForTests(m, new Ray(new Vector3(ri.X + 0.6f, 40f, ri.Y), Vector3.down));
            bool recovered = StepUntil(() => HomeExplorationService.IsRecovered(s, relicId), Hz * 1500, Hz / 2);
            RegionQuestItemRecord q = HeritageService.FindItem(s, hid);
            bool carried = q != null && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == m.LogicId && HeritageService.StatusOf(s, hid) == HeritageStatus.Carried;
            var lines = new List<CargoLine>();
            CargoService.LinesOf(s, m.LogicId, lines);
            bool inHold = lines.Any(l => l.Kind == CargoService.LineQuest);
            Expect(relays > 0 && handled && recovered && carried && inHold && NotifyMembers(HeritageService.NotifyType) > found0,
                $"L3 ① 正式放置：{hid} 在 {ri.Ring?.Id} 遗迹 {relicId}（离核心 {ri.Distance} 格，第一幕范围）；选中 {MachineNaming.Short(m.LogicId)} 右键遗迹派工（{handled}）→ 走过去回收完（{recovered}）→ " +
                $"遗产装进它的货舱（{carried}，货舱面板列出 {inHold}）、通知“{LatestDetail(HeritageService.NotifyType)}”");

            GridCell homeCell = HomeGridService.CorePivot(s);
            m.CommandMoveTo(new Vector3(homeCell.X + 9f, 0f, homeCell.Y + 1f));
            bool obtained = StepUntil(() => HeritageService.IsObtained(s, hid), Hz * 1500, Hz / 2);
            HeritageService.Counts counts = HeritageService.CountsOf(s);
            string status = HeritageService.StatusText(s);
            Expect(obtained && HeritageService.Showcase(s).Contains(hid) && q.State == RegionQuestItemState.Recovered && counts.Obtained == 1 && status.Contains($"1 / {HeritageService.All.Count}"),
                $"L3 ② 右键地面把它开回核心附近：遗产存入核心保管库（展柜里有它 {HeritageService.Showcase(s).Contains(hid)}），收集进度“{status.Split('\n')[0]}”");
        }
    }
}
