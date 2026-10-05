using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
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
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-02 屏障、闸门、护盾、陷阱的自动验收（FG06 FGR-DEF-010～013；FGT-DEF-002；DEBT-FG6DEF01-07、DEBT-FG3LOG01-03、DEBT-FG5RND01-01 防御三个节点）。
    /// 全部起真实系统：真实家园（世界模拟、电网、管线、施工机器、寻路镜像）、真实建造模式（选中 → 悬停预览 → 按住拖拽 → 松开）、家园战斗内核（护盾逐弹体吸收、
    /// 场地节拍、标签反应、结构单位受伤都在 Main/Sim）、真文件存读档、真 UXML 面板。
    /// A 数据；B 屏障拖拽（全有或全无、一步撤销、长度上限、研究门控）与放置时的敌方来路预览；C 寻路挡路位（屏障挡全部、闸门只挡敌方、虚影 / 被毁不挡）与施工完工进内核；
    /// D 己方屏障不挡己方炮塔弹道、敌方弹体打在墙上（DEBT-FG6DEF01-07）；E 护盾（充能 → 展开 → 吸收 → 耗电上升 → 过载 20 秒 → 重启，面板与状态行显示护盾值和倒计时，断电离线）；
    /// F 陷阱（固件负向、缺流体三种原因与通知、流体逐毫升守恒、线 / 区域、电磁场只用电）；G FGT-DEF-002 阵地反应（油膜带 + 燃迹炮塔 = 爆燃 → 燃烧区）；
    /// H 复制设置；I 真文件存读档（内核格式 10 往返、格式 9 旧快照、旧档无域、坏值）；J 读档接着跑 = 不存档；K 暂停与 0.5x～3x；L 观察 / 不观察一致；M 种子集；N 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgDefenseStructuresSelfCheck）。
    /// </summary>
    public static class FgDefenseStructuresSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 6;
        private const string B1 = DefenseCatalog.BarrierT1;
        private const string B2 = DefenseCatalog.BarrierT2;
        private const string B3 = DefenseCatalog.BarrierT3;
        private const string Gate = DefenseCatalog.GateTypeId;
        private const string Shield = DefenseCatalog.ShieldTypeId;
        private const string Trap = DefenseCatalog.TrapTypeId;
        private const string Oil = "fw_oilleak";
        private const string Burn = "fw_burntrail";
        private const string Arc = "fw_arcchain";
        private const string BpBurn = "bp_df_burn";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 屏障闸门护盾陷阱")]
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
            Line("\n[屏障闸门护盾陷阱] 拖拽建墙 / 来路预览 / 闸门只放己方 / 护盾过载重启 / 陷阱场地与阵地反应，逐弹体与区域在战斗内核（FG6-DEF-02）");
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
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdefense-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                TestRangeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；护盾逐弹体吸收、场地节拍、来路距离场在 AOT（Burst），对账 / 状态机 / 面板在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckWallDragAndPreview);
                Step(CheckNavAndBuild);
                Step(CheckBarrierProjectiles);
                Step(CheckShield);
                Step(CheckTrap);
                Step(CheckPositionalReaction);
                Step(CheckCopySettings);
                Step(CheckPanelsAndProbe);
                Step(CheckSaveLoad);
                Step(CheckLoadContinuation);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSeedSet);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"防御建筑自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                DefenseService.ResetSessionState();
                DefenseViews.Clear();
                TurretService.ResetSessionState();
                TurretViews.Clear();
                TestRangeService.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
                FirmwareKinds.ResetForTests();
                SignalCoreService.ResetForTests();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                HomeValleyCombatTargets.ResetSessionState();
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                DefensePanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
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
                if (originalSession != null)
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
            Line($"  · [屏障闸门护盾陷阱] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>一个有电的家园（发电机 2 一座），研发树防御三个节点已研究，测试用的固件已解锁。按种子地形找空地，不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 1500, bool research = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            SignalCoreService.ResetForTests();
            TestRangeService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            SignalUplinkService.SilentNightProvider = null;
            _seq = 900;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (g.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "df_gen", g.Value);
            }
            GridCell? g3 = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (g3.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "df_gen3", g3.Value);
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (research)
            {
                ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            }
            foreach (string fw in new[] { Oil, Burn, Arc, "fw_coolant", "fw_trail" })
            {
                if (!MechanicalContentUnlock.IsUnlocked(s, fw))
                {
                    s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(fw).ToArray();
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：直接登记一座建成、满耐久的建筑（真实放置 / 机器施工由 B / C 段覆盖）。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c, int rotation = 0)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, rotation),
                Health = BuildingOps.MaxDurability(type),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return r;
        }

        private static void Unregister(CampaignState s, BuildingRecord b)
        {
            s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
        }

        /// <summary>在核心附近按种子地形找空地登记一座（用电的要接得上电网且吃到电）。<paramref name="awayFrom"/>：尽量远离这个点的方向（护盾测试让射手在外侧）。</summary>
        private static BuildingRecord Place(CampaignState s, string type, float from = 6f, float to = 24f, int rotation = 0)
        {
            GridCell core = HomeGridService.CorePivot(s);
            bool powered = HomeValleyLayout.PowerProfile.ContainsKey(type);
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, type, c, rotation, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = Register(s, type, c, rotation);
                    if (powered && (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered))
                    {
                        Unregister(s, b);
                        continue;
                    }
                    DefenseService.Sync(s);
                    return b;
                }
            }
            Fail($"测试准备：家园里找不到能放 {type} 的空地");
            return null;
        }

        private static DefenseRecord Rec(CampaignState s, BuildingRecord b) => b != null ? DefenseService.Find(s, b.BuildingId) : null;

        private static int Unit(CampaignState s, BuildingRecord b)
        {
            DefenseRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetDefenseUnit(r.Serial, out int u) ? u : 0;
        }

        private static float UnitHp(CampaignState s, BuildingRecord b)
        {
            DefenseRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetDefenseHealth(r.Serial, out float hp, out _, out bool alive) && alive ? hp : 0f;
        }

        private static CombatShield ShieldOf(CampaignState s, BuildingRecord b)
        {
            DefenseRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetShield(r.Serial, out CombatShield sh) ? sh : default;
        }

        private static ShieldReadout ShieldRo(CampaignState s, BuildingRecord b)
        {
            DefenseService.TryGetShieldReadout(s, b.BuildingId, out ShieldReadout ro);
            return ro;
        }

        private static TrapReadout TrapRo(CampaignState s, BuildingRecord b)
        {
            DefenseService.TryGetTrapReadout(s, b.BuildingId, out TrapReadout ro);
            return ro;
        }

        /// <summary>一个敌方目标单位（不动、不开火）。外部键 -1（原型单位，测试结束清场）。</summary>
        private static int Hostile(CombatSite site, Vector2 at, float hp)
        {
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
                Position = new double2(at.x, at.y),
                Home = new double2(at.x, at.y),
                Radius = 0.5f,
                Health = hp,
                MaxHealth = hp,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            });
        }

        /// <summary>一个敌方射手：驻守开火，打真实飞行的弹体（与突袭者同一开火方式），射程内最近的己方单位。</summary>
        private static int Shooter(CombatSite site, Vector2 at, float damage, float cooldown, float range = 20f)
        {
            int w = site.WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile,
                HasOutput = 1,
                Range = range,
                Damage = damage,
                Cooldown = cooldown,
                ProjectileSpeed = 30f,
                ProjectileRadius = 0.2f,
                ProjectileLife = 2f,
            });
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath | CombatUnitFlags.WeaponEnabled,
                Position = new double2(at.x, at.y),
                Home = new double2(at.x, at.y),
                Radius = 0.5f,
                Health = 100000f,
                MaxHealth = 100000f,
                Weapon = w,
                BehaviorProfile = -1,
                Priority = 1,
            });
        }

        private static float Hp(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) && v.Alive ? v.Health : 0f;

        private static void Clear(CombatSite site) => CombatBench.ClearPrototypeUnits(site);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static string Fl(float v)
        {
            string t = v.ToString("R", CultureInfo.InvariantCulture);
            return t.Contains(".") || t.Contains("E") ? t : t + ".0";
        }

        private static int Fluid(string key) => PipeNetworkService.FluidId(key);

        private static int ZonesWith(CombatSite site, uint bit, out int near, Vector2 at, float within)
        {
            int n = 0;
            near = 0;
            for (int i = 0; i < site.Kernel.ZoneCount; i++)
            {
                if (site.Kernel.TryGetZone(i, out CombatZone z) && (z.StatusMask & bit) != 0)
                {
                    n++;
                    if (Vector2.Distance(new Vector2((float)z.Pos.x, (float)z.Pos.y), at) <= within)
                    {
                        near++;
                    }
                }
            }
            return n;
        }

        private static int RuleIndex(string reactionId)
        {
            for (int i = 0; i < NamedReactionCatalog.TagRules.Count; i++)
            {
                if (NamedReactionCatalog.TagRules[i].Id == reactionId)
                {
                    return i;
                }
            }
            return -1;
        }

        private static GridCell CoreCenterCell(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new GridCell((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        }

        private static Vector2 AwayFromCore(CampaignState s, Vector2 p)
        {
            GridCell c = CoreCenterCell(s);
            Vector2 d = p - new Vector2(c.X, c.Y);
            return d.sqrMagnitude < 1e-4f ? Vector2.right : d.normalized;
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] ss = lines.Where(l => l.StartsWith("SS\t", StringComparison.Ordinal)).ToArray();
            string[] tr = lines.Where(l => l.StartsWith("TR\t", StringComparison.Ordinal)).ToArray();
            string[] rss = t.TbShieldState.DataList.Select(r => string.Join("\t", "SS", r.Id, r.Code.ToString(CultureInfo.InvariantCulture), r.NameKey, r.DescKey,
                r.Initial.ToString(CultureInfo.InvariantCulture), r.Absorbs.ToString(CultureInfo.InvariantCulture), Fl(r.Seconds), r.Next, r.OnDepleted, r.OnPowerLost, r.OnPowerBack,
                Fl(r.RegenPerSec), Fl(r.PowerMul), Fl(r.EnterHp), r.StatusKey, r.StatusKind)).ToArray();
            string[] rtr = t.TbTrapProfile.DataList.Select(r => string.Join("\t", "TR", r.FirmwareId, r.Tag, r.Fluid, Fl(r.LitersPerLay), Fl(r.Dps), r.NameKey,
                r.SortOrder.ToString(CultureInfo.InvariantCulture))).ToArray();
            Expect(code == 0 && ss.Length == 5 && ss.SequenceEqual(rss) && tr.Length == 8 && tr.SequenceEqual(rtr) && DefenseCatalog.Problems.Count == 0,
                $"A1 两张新表 fg.TbShieldState（{ss.Length} 行，护盾状态机）/ fg.TbTrapProfile（{tr.Length} 行）与源数据 fgdata_structures 逐字段一致；目录无问题（{string.Join("；", DefenseCatalog.Problems)}）");

            bool sizes = new[] { B1, B2, B3, Gate, Trap }.All(x => GridContent.TryGetBuilding(x, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 1 && g.FootprintH == 1 && g.Category == "defense")
                         && GridContent.TryGetBuilding(Shield, out GameConfig.fg.BuildingGrid gs) && gs.FootprintW == 2 && gs.Category == "defense";
            float d1 = BuildingOps.MaxDurability(B1), d2 = BuildingOps.MaxDurability(B2), d3 = BuildingOps.MaxDurability(B3);
            bool tiers = d1 < d2 && d2 < d3 && GridContent.TryGetUpgrade(B1, out string up1) && up1 == B2 && GridContent.TryGetUpgrade(B2, out string up2) && up2 == B3;
            bool power = !HomeValleyLayout.PowerProfile.ContainsKey(B1) && !HomeValleyLayout.PowerProfile.ContainsKey(Gate)
                         && HomeValleyLayout.PowerProfile.ContainsKey(Shield) && HomeValleyLayout.PowerProfile.ContainsKey(Trap);
            ShieldStateDef over = DefenseCatalog.ShieldStates.FirstOrDefault(x => x.Id == "overloaded");
            bool machine = DefenseCatalog.InitialState != null && DefenseCatalog.InitialState.Id == "charging" && over != null && Mathf.Approximately(over.Seconds, 20f)
                           && DefenseCatalog.ShieldStates.Where(x => x.Absorbs).All(x => DefenseCatalog.TryGetState(x.OnDepleted, out _));
            bool research = new[] { "defense.barrier", "defense.shield", "defense.trap" }.All(id => ResearchCatalog.TryGet(id, out ResearchNodeDef n) && n.IsReady)
                            && ResearchCatalog.TryGet("defense.barrier", out ResearchNodeDef nb) && nb.Unlocks.Contains("build:barrier_t1") && nb.Unlocks.Contains("build:gate")
                            && ResearchCatalog.TryGet("logistics.heat_trace", out ResearchNodeDef rd) && !rd.IsReady // FG6-DEF-03 防御分支已全部开放，“后续版本”改用伴热管（FG7-ENV-03）
                            && ResearchService.MigratedNodes().All(n => !n.Id.StartsWith("defense.", StringComparison.Ordinal));
            Expect(sizes && tiers && power && machine && research && CombatConst.FormatVersion >= 10, // FG6-DEF-05 起内核快照升到格式 11（攻城属性）；格式 10 往返在 I3 单独测
                $"A2 屏障 T1 / T2 / T3、闸门、陷阱 1×1、护盾 2×2（防御页签）；屏障耐久 {d1}/{d2}/{d3} 递增、升级路线 T1→T2→T3；屏障 / 闸门不用电、护盾 / 陷阱用电；" +
                $"护盾状态机初始“充能”、过载 {over?.Seconds} 秒、吸收状态都有耗尽去向；研发 defense.barrier / shield / trap 已就绪并解锁（旧档迁移不送；“后续版本”的对照改用伴热管）；内核快照格式 {CombatConst.FormatVersion}" +
                $"（{sizes}/{tiers}/{power}/{machine}/{research}）");

            string[] keys = t.TbLocText.DataList.Select(r => r.Key).Where(k => k.StartsWith("defense.", StringComparison.Ordinal) || k.StartsWith("shield.", StringComparison.Ordinal)
                || k.StartsWith("trap.", StringComparison.Ordinal) || k.StartsWith("build.wall.", StringComparison.Ordinal) || k.StartsWith("codex.defense.barrier", StringComparison.Ordinal)
                || k.StartsWith("codex.defense.shield", StringComparison.Ordinal) || k.StartsWith("codex.defense.trap", StringComparison.Ordinal)).ToArray();
            var badText = keys.Where(k => GameText.ContainsMarker(GameText.Get(k)) || GameText.Get(k).Length == 0).ToList();
            GameSettings.SetLanguage(GameLanguage.En);
            badText.AddRange(keys.Where(k => GameText.ContainsMarker(GameText.Get(k)) || GameText.Get(k).Length == 0 || GameText.Get(k).Any(c => c >= 0x4E00 && c <= 0x9FFF)));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool codex = new[] { "codex.defense.barrier", "codex.defense.shield", "codex.defense.trap" }.All(id => t.TbCodexEntry.DataList.Any(r => r.Id == id && r.Hooks.Length > 0))
                         && BuildingOps.Service(Shield)?.CodexId == "codex.defense.shield" && BuildingOps.Service(B1)?.CodexId == "codex.defense.barrier";
            bool hooks = new[] { GuidanceHooks.DefenseBarrierFirstBuilt, GuidanceHooks.DefenseShieldFirstOverload, GuidanceHooks.DefenseTrapFirstSupplyShort, GuidanceHooks.DefenseRouteFirstPreview }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType("shield_overload", out NotifyTypeDef n1) && n1.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("trap_supply", out NotifyTypeDef n2) && n2.Tier == NotifyLevel.Warning;
            Expect(keys.Length > 60 && badText.Count == 0 && codex && hooks && notify,
                $"A3 文本中英齐全（{keys.Length} 条，缺 {string.Join(",", badText.Take(5))}）；三条图鉴条目带钩子；引导钩子已登记；通知类型“护盾过载 / 陷阱缺流体”为警告级（B08 / B14 / B16）");
        }

        // ── B 屏障拖拽与来路预览 ─────────────────────────────────────────────────────

        private static void CheckWallDragAndPreview()
        {
            CampaignState s = NewWorld(6201, scrap: 2000, research: false);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FgProductionSelfCheck.FindArea(s, 8, 3, 9f, 22f);
            if (!area.HasValue || mode == null)
            {
                Fail("B 测试准备：找不到 8×3 的空地或家园建造模式");
                return;
            }
            GridCell a = FgProductionSelfCheck.At(area.Value, 0, 1);
            GridCell b = FgProductionSelfCheck.At(area.Value, 7, 1);
            // 研究门控：没研究“防御 · 屏障与闸门”时不能放，原因写明去研发树（B06）。全量自检里旧段按“研发树开放前”跑，这里按开放后的真实规则判。
            Func<bool> treeBefore = ResearchGate.TreeAvailableOverrideForTests;
            ResearchGate.TreeAvailableOverrideForTests = () => true;
            GridPlacementResult locked = HomeGridService.ValidatePlacement(s, B1, a, 0);
            bool gated = !locked.Ok && locked.Has(GridBlockReason.Locked) && locked.Describe().Contains("屏障与闸门");
            WallPlan lockedPlan = DefenseService.PlanWall(s, B1, a, b);
            ResearchGate.TreeAvailableOverrideForTests = treeBefore;
            Expect(gated && !lockedPlan.Ok, $"B0 研发门控：没研究“防御 · 屏障与闸门”时放不下屏障（“{locked.Describe()}”），拖拽整段也被拒（{lockedPlan.Reason?.Describe()}）");
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");

            GameObject hudGo = null;
            var rig = new GameObject("__fgdefense_views") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "BuildModeHud.uxml", out hudGo);
                BuildModeHudUIToolkit hud = hudGo.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                mode.Open();
                mode.Select(B1);
                // 悬停：单格预览 + 放下之后敌人会改走哪里（预览线 + 状态行一句）。
                mode.SetHover(s, a);
                mode.RefreshPreview(s);
                mode.RefreshVisuals(s);
                hud.Refresh();
                bool hover = mode.Preview != null && mode.Preview.Ok && mode.HoverRoute != null && mode.HoverRoute.HasRoutes && mode.ActiveRouteLineCount > 0
                             && mode.Preview.Notes.Any(n => n.Contains("敌人")) && hud.StatusLabelText.Contains("敌人")
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseRouteFirstPreview);
                Expect(hover, $"B1 选中屏障悬停一格：放置预览合法，地面画出 {mode.ActiveRouteLineCount} 条敌方来路预览线，状态行写“{mode.HoverRoute?.Summary()}”，首次预览钩子");

                // 按住拖拽：自动转角路径、逐格合法、格数与总造价、来路预览；松开全部放下（虚影），整段是撤销栈里的一步。
                int undo0 = PlanHistory.UndoSteps(s);
                int buildings0 = s.BuildingRecords.Length;
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                mode.RefreshVisuals(s);
                hud.Refresh();
                WallPlan plan = mode.WallPlan;
                bool dragging = mode.Drag == HomeValleyBuildMode.DragKind.Wall && plan != null && plan.Ok && plan.Cells.Count == 8 && plan.TotalCost == 8 * plan.ScrapPerCell
                                && mode.ActiveTileCount >= 8 && hud.DragInfoText.Contains("8 格") && hud.DragInfoText.Contains("敌人");
                string dragInfo = hud.DragInfoText;
                mode.PointerUp(s, b);
                BuildingRecord[] ghosts = s.BuildingRecords.Where(x => x.BuildingTypeId == B1).ToArray();
                bool placed = ghosts.Length == 8 && s.BuildingRecords.Length == buildings0 + 8 && ghosts.All(HomeValleyController.IsPlannedGhost)
                              && PlanHistory.UndoSteps(s) == undo0 + 1 && mode.StatusText.Contains("8 座");
                Expect(dragging && placed,
                    $"B2 按住拖拽一段墙：8 格、总造价 {plan?.TotalCost}，建造栏“{dragInfo.Replace("\n", " / ")}”；松开放下 8 座屏障虚影（机器取料施工），整段是撤销栈里的一步（{PlanHistory.UndoSteps(s) - undo0}）；状态“{mode.StatusText.Replace("\n", " / ")}”");
                PlanStepResult undone = PlanHistory.Undo(s);
                bool undoOk = s.BuildingRecords.Count(x => x.BuildingTypeId == B1) == 0;
                Expect(undoOk, $"B3 撤销（Ctrl+Z）一次撤掉整段 8 座虚影（全额返还）（{undone}）");

                // 全有或全无：路径穿过已有的建筑 → 一格都不放，原因写明是哪一格为什么，第一处不合法叠叉。
                BuildingRecord blocker = Register(s, "power_pole", FgProductionSelfCheck.At(area.Value, 4, 1));
                int count0 = s.BuildingRecords.Length;
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                mode.RefreshVisuals(s);
                WallPlan bad = mode.WallPlan;
                bool crossed = mode.GhostCrossVisible;
                mode.PointerUp(s, b);
                bool none = s.BuildingRecords.Length == count0 && bad != null && !bad.Ok && bad.FirstBadIndex == 4 && bad.Reason.HasValue && bad.Reason.Value.Code == GridBlockReason.Occupied
                            && !mode.LastResult.Success && mode.StatusIsError;
                Expect(none && crossed, $"B4 负向：拖过一座电塔 → 整段不放（全有或全无），第 5 格叠叉，原因“{bad?.Reason?.Describe()}”");
                Unregister(s, blocker);

                // 长度上限：超过 grid.drag_max_cells 被拒并写明上限。
                int max = GridContent.TuningInt("grid.drag_max_cells");
                WallPlan tooLong = DefenseService.PlanWall(s, B1, a, new GridCell(a.X + max + 3, a.Y));
                Expect(!tooLong.Ok && tooLong.Reason.HasValue && tooLong.Reason.Value.Code == GridBlockReason.DragTooLong,
                    $"B5 负向：一次拖超过 {max} 格被拒（“{tooLong.Reason?.Describe()}”）");

                // 右键取消拖拽：什么都不放。
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                mode.RightClick();
                bool cancelled = mode.Drag == HomeValleyBuildMode.DragKind.None && mode.WallPlan == null && s.BuildingRecords.Length == count0 - 1;
                Expect(cancelled, "B6 拖拽中右键取消：不放任何东西（B02 右键取消）");

                // 闸门同样可以拖拽；T2 / T3 直接拖。
                GridOpResult gates = DefenseService.TryPlaceWall(s, Gate, a, FgProductionSelfCheck.At(area.Value, 2, 1), out int gn);
                GridOpResult t3 = DefenseService.TryPlaceWall(s, B3, FgProductionSelfCheck.At(area.Value, 0, 2), FgProductionSelfCheck.At(area.Value, 7, 2), out int t3n);
                Expect(gates.Success && gn == 3 && t3.Success && t3n == 8, $"B7 闸门 3 格、屏障 T3 8 格都能直接拖拽铺下（{gn} / {t3n}）");
            }
            finally
            {
                if (hudGo != null)
                {
                    Object.DestroyImmediate(hudGo);
                }
                mode?.Close();
                Object.DestroyImmediate(rig);
            }

            // 来路预览的语义：把核心外一整圈都围上 = 完全堵死（预览线画到第一段新墙为止）；不围 = 来路能走到核心。
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            int ringR = GridContent.TuningInt("grid.core_reserve_ring") + 2;
            var ring = new List<GridCell>();
            for (int x = cmin.X - ringR; x <= cmax.X + ringR; x++)
            {
                ring.Add(new GridCell(x, cmin.Y - ringR));
                ring.Add(new GridCell(x, cmax.Y + ringR));
            }
            for (int y = cmin.Y - ringR + 1; y <= cmax.Y + ringR - 1; y++)
            {
                ring.Add(new GridCell(cmin.X - ringR, y));
                ring.Add(new GridCell(cmax.X + ringR, y));
            }
            var open = new RoutePreview();
            DefenseService.PreviewRoutes(s, null, open);
            var sealedRoute = new RoutePreview();
            DefenseService.PreviewRoutes(s, ring, sealedRoute);
            var ringSet = new HashSet<GridCell>(ring);
            bool openOk = open.HasRoutes && !open.Sealed && open.Routes.All(r => r.Count > 1 && Chebyshev(r[r.Count - 1], cmin, cmax) <= 1);
            bool sealedOk = sealedRoute.HasRoutes && sealedRoute.Sealed && sealedRoute.Routes.All(r => r.Count > 0 && ringSet.Contains(r[r.Count - 1]))
                            && sealedRoute.Summary().Contains("堵死");
            Expect(openOk && sealedOk,
                $"B8 敌方来路预览：不放墙时 {open.Routes.Count} 条来路都走到核心边（{openOk}）；假设把核心外一整圈（{ring.Count} 格）都围上 = 完全堵死，预览线停在新墙上、写明“{sealedRoute.Summary()}”（{sealedOk}）");

            // B9 复审修复（P1）：分几次规划——先把一圈里除了一个 3 格缺口以外的格子都放成屏障虚影（真实放置入口，机器还没施工），
            // 再拖一段封口：预览把已规划的虚影也算上 = 完全堵死（修复前虚影当空地，封口时只写“来路变长 / 不变”）。
            var sides = new List<List<GridCell>>();
            for (int side = 0; side < 4; side++)
            {
                var run = new List<GridCell>();
                if (side < 2)
                {
                    int y = side == 0 ? cmax.Y + ringR : cmin.Y - ringR;
                    for (int x = cmin.X - ringR; x <= cmax.X + ringR; x++)
                    {
                        run.Add(new GridCell(x, y));
                    }
                }
                else
                {
                    int x = side == 2 ? cmax.X + ringR : cmin.X - ringR;
                    for (int y = cmin.Y - ringR; y <= cmax.Y + ringR; y++)
                    {
                        run.Add(new GridCell(x, y));
                    }
                }
                sides.Add(run);
            }
            Func<GridCell, bool> placeable = c => HomeGridService.ValidatePlacement(s, B1, c, 0, asPlayerPlacement: false, checkCost: false).Ok;
            GridCell gapA = default, gapC = default;
            bool gapFound = false;
            foreach (List<GridCell> run in sides)
            {
                for (int i = 2; i + 3 < run.Count && !gapFound; i++)
                {
                    if (placeable(run[i]) && placeable(run[i + 1]) && placeable(run[i + 2]))
                    {
                        gapA = run[i];
                        gapC = run[i + 2];
                        gapFound = true;
                    }
                }
                if (gapFound)
                {
                    break;
                }
            }
            if (!gapFound)
            {
                Fail("B9 测试准备：核心外一圈找不到连续 3 格可放的缺口");
                return;
            }
            var gapSet = new HashSet<GridCell>();
            for (int x = Math.Min(gapA.X, gapC.X); x <= Math.Max(gapA.X, gapC.X); x++)
            {
                for (int y = Math.Min(gapA.Y, gapC.Y); y <= Math.Max(gapA.Y, gapC.Y); y++)
                {
                    gapSet.Add(new GridCell(x, y));
                }
            }
            int ghostsPlaced = 0, skipped = 0;
            foreach (GridCell c in ring)
            {
                if (gapSet.Contains(c))
                {
                    continue;
                }
                if (placeable(c) && PlanHistory.Place(s, B1, c, 0).Success)
                {
                    ghostsPlaced++;
                }
                else
                {
                    skipped++;
                }
            }
            // 修复前的口径（只把封口这一段当墙、虚影当空地）：不会堵死——对照，证明这条断言测的是“规划中的虚影”。
            WallPlan closing = DefenseService.PlanWall(s, B1, gapA, gapC);
            int plannedCells = DefenseService.LastPlannedCells;
            bool stillGhosts = s.BuildingRecords.Count(x => x.BuildingTypeId == B1 && HomeValleyController.IsPlannedGhost(x)) >= ghostsPlaced;
            bool closeSealed = closing.Ok && closing.Cells.Count == 3 && closing.Route.HasRoutes && closing.Route.Sealed && closing.Route.Summary().Contains("堵死")
                               && plannedCells >= ghostsPlaced && stillGhosts;
            Expect(closeSealed,
                $"B9 分段规划：先放 {ghostsPlaced} 座屏障虚影（另有 {skipped} 格已被占 / 不可放）围住核心、留 3 格缺口，再拖一段封口：预览把规划中的 {plannedCells} 格虚影算上，写明“{closing.Route.Summary()}”（{closeSealed}）");

            // B10 复审修复（P2，FGR-LOG-012）：拖一段墙会让建筑机器到不了 → 警告；闸门放行己方，不警告。
            GridCell? box = FgProductionSelfCheck.FindArea(s, 3, 3, 9f, 26f);
            if (box.HasValue)
            {
                GridCell mid = FgProductionSelfCheck.At(box.Value, 1, 1);
                BuildingRecord pole = Register(s, "power_pole", mid);
                var around = new List<GridCell>();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx != 0 || dy != 0)
                        {
                            around.Add(new GridCell(mid.X + dx, mid.Y + dy));
                        }
                    }
                }
                var names = new List<string>();
                int cut = NavService.PlacementCutsOff(s, around, names);
                var gateNames = new List<string>();
                int gateCut = NavService.PlacementCutsOff(s, Gate, FgProductionSelfCheck.At(box.Value, 1, 0), 0, gateNames);
                WallPlan side = DefenseService.PlanWall(s, B1, FgProductionSelfCheck.At(box.Value, 0, 0), FgProductionSelfCheck.At(box.Value, 2, 0));
                Expect(cut > 0 && names.Count > 0 && gateCut == 0 && side.Ok && side.CutsOff.Count == 0,
                    $"B10 机器可达性警告：一圈墙围住电塔 → 放下后机器到不了 {cut} 座（{string.Join("、", names)}）；拖一段闸门不警告（{gateCut}）；只挡一边的墙不误报（{side.CutsOff.Count}）");
                Unregister(s, pole);
            }
            else
            {
                Fail("B10 测试准备：找不到 3×3 空地");
            }
        }

        private static int Chebyshev(GridCell c, GridCell min, GridCell max)
        {
            int dx = c.X < min.X ? min.X - c.X : c.X > max.X ? c.X - max.X : 0;
            int dy = c.Y < min.Y ? min.Y - c.Y : c.Y > max.Y ? c.Y - max.Y : 0;
            return Math.Max(dx, dy);
        }

        // ── C 寻路挡路位与施工 ───────────────────────────────────────────────────────

        private static void CheckNavAndBuild()
        {
            CampaignState s = NewWorld(6202, scrap: 2000);
            CombatSite site = Site;
            GridCell? area = FgProductionSelfCheck.FindArea(s, 4, 3, 9f, 20f);
            if (!area.HasValue)
            {
                Fail("C 测试准备：找不到空地");
                return;
            }
            GridCell wc = FgProductionSelfCheck.At(area.Value, 1, 1);
            GridCell gc = FgProductionSelfCheck.At(area.Value, 3, 1);
            // 机器施工：放下屏障虚影 → 机器运料施工 → 建成后进内核、挡路。
            GridOpResult placed = DefenseService.TryPlaceWall(s, B1, wc, wc, out _);
            BuildingRecord wall = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
            NavService.SyncGridChanges();
            bool ghostOpen = wall != null && NavService.Kernel.Passable(wc.X, wc.Y, NavConst.ClassHostile) && NavService.Kernel.Passable(wc.X, wc.Y, NavConst.ClassPlayer);
            bool built = wall != null && StepUntil(() => wall.ConstructionState == BuildingConstructionState.Operational, 240);
            Seconds(1f);
            NavService.SyncGridChanges();
            bool blocks = built && Unit(s, wall) > 0 && !NavService.Kernel.Passable(wc.X, wc.Y, NavConst.ClassHostile) && !NavService.Kernel.Passable(wc.X, wc.Y, NavConst.ClassPlayer)
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseBarrierFirstBuilt) && wall.InvestedScrap > 0
                          && BuildingStatusService.Evaluate(s, wall).Reason.Contains("挡路中");
            Expect(ghostOpen && blocks,
                $"C1 屏障虚影不挡路（{ghostOpen}）；机器运料施工完工（投入 {wall?.InvestedScrap}）后进战斗内核（己方结构单位）、寻路格网里挡住敌我双方，状态“{(wall != null ? BuildingStatusService.Evaluate(s, wall).Reason : "无")}”，首次建成钩子");

            // 闸门：只挡敌方（写死的规则），己方机器通过。
            BuildingRecord gate = Register(s, Gate, gc);
            DefenseService.Sync(s);
            NavService.SyncGridChanges();
            bool gateRule = Unit(s, gate) > 0 && !NavService.Kernel.Passable(gc.X, gc.Y, NavConst.ClassHostile) && NavService.Kernel.Passable(gc.X, gc.Y, NavConst.ClassPlayer)
                            && BuildingStatusService.Evaluate(s, gate).Reason.Contains("只放行你的单位") && DefenseService.NavBlockBitsOf(gate) == (byte)(1 << NavConst.ClassHostile);
            Expect(gateRule, $"C2 FGR-DEF-011 闸门：敌方类别被挡、己方类别可过（写死的规则）；状态“{BuildingStatusService.Evaluate(s, gate).Reason}”");

            // 三个等级：升级规划（U 拖框）按升级路线把建成的 T1 原地升到 T2（差额 = T2 − T1 造价）。
            UpgradeBoxPlan up = UpgradePlanner.Plan(s, wc, wc);
            int diff = (HomeValleyLayout.BuildProfile.TryGetValue(B2, out (int ScrapCost, float Seconds) c2) ? c2.ScrapCost : 0)
                       - (HomeValleyLayout.BuildProfile.TryGetValue(B1, out (int ScrapCost, float Seconds) c1) ? c1.ScrapCost : 0);
            Expect(up.Count == 1 && up.Cost == diff && diff > 0, $"C2b 升级规划框住建成的屏障 T1：可原地升到 T2（{up.Count} 座，差额 {up.Cost} 废料）");

            // 被摧毁的屏障不挡路（敌人打穿墙就能过去），留下虚影可以重建；耐久在内核里扣（敌人弹体同一入口）。
            DefenseRecord wr = Rec(s, wall);
            float hp0 = UnitHp(s, wall);
            site.DamageDefense(wr.Serial, 30f);
            Seconds(0.6f);
            bool hurt = Mathf.Abs(wall.Health - (hp0 - 30f)) < 0.5f && Mathf.Abs(UnitHp(s, wall) - (hp0 - 30f)) < 0.5f;
            wall.Health = BuildingOps.MaxDurability(B1); // 维修（建筑记录被别处改）→ 对账推回内核
            Seconds(0.6f);
            bool repaired = Mathf.Abs(UnitHp(s, wall) - BuildingOps.MaxDurability(B1)) < 0.5f;
            site.DamageDefense(wr.Serial, 10000f);
            Seconds(0.6f);
            NavService.SyncGridChanges();
            bool destroyed = wall.ConstructionState == BuildingConstructionState.Damaged && Unit(s, wall) == 0 && NavService.Kernel.Passable(wc.X, wc.Y, NavConst.ClassHostile)
                             && HomeGridService.FindBuilding(s, wall.BuildingId) != null && Rec(s, wall) != null;
            Expect(hurt && repaired && destroyed,
                $"C3 耐久双向对账：内核扣 30 → 建筑 {wall.Health:F0}（{hurt}）；维修改建筑记录 → 推回内核（{repaired}）；打到 0 → 屏障被摧毁、内核单位拿掉、这一格不再挡敌人（留下虚影可重建）（{destroyed}）");

            // C4 复审修复（P1，FGR-DEF-010 耐久递增）：原地升级真正施工完工——满耐久的 T1 升完是满耐久的 T2（内核上限 = T2，不显示“受损”）；
            // 掉了 25% 的 T1 升完仍是 75%（按比例换算到新上限）。走真实的升级规划 → 撤销栈一步 → 机器运差额施工 → 完工。
            BuildingRecord full = Register(s, B1, FgProductionSelfCheck.At(area.Value, 0, 0));
            BuildingRecord hurtWall = Register(s, B1, FgProductionSelfCheck.At(area.Value, 0, 2));
            DefenseService.Sync(s);
            float t1Max = BuildingOps.MaxDurability(B1);
            float t2Max = BuildingOps.MaxDurability(B2);
            site.DamageDefense(Rec(s, hurtWall).Serial, t1Max * 0.25f);
            Seconds(0.6f);
            UpgradeBoxPlan upFull = UpgradePlanner.Plan(s, FgProductionSelfCheck.At(area.Value, 0, 0), FgProductionSelfCheck.At(area.Value, 0, 2));
            int ordered = PlanHistory.Upgrade(s, upFull, out GridReason? upFirst);
            string fullId = full.BuildingId, hurtId = hurtWall.BuildingId;
            bool upgraded = ordered == 2 && StepUntil(() =>
            {
                BuildingRecord a = HomeGridService.FindBuilding(s, fullId);
                BuildingRecord h = HomeGridService.FindBuilding(s, hurtId);
                return a != null && h != null && a.BuildingTypeId == B2 && h.BuildingTypeId == B2
                       && !s.BuildingRecords.Any(x => x.RelocateFromId == fullId || x.RelocateFromId == hurtId);
            }, 300);
            Seconds(0.6f); // 一次对账
            BuildingRecord fullNow = HomeGridService.FindBuilding(s, fullId);
            BuildingRecord hurtNow = HomeGridService.FindBuilding(s, hurtId);
            float kMax = 0f, kHp = 0f, hMax = 0f, hHp = 0f;
            bool k1 = fullNow != null && site.TryGetDefenseHealth(Rec(s, fullNow).Serial, out kHp, out kMax, out _);
            bool k2 = hurtNow != null && site.TryGetDefenseHealth(Rec(s, hurtNow).Serial, out hHp, out hMax, out _);
            bool fullOk = upgraded && k1 && Mathf.Abs(kMax - t2Max) < 0.5f && Mathf.Abs(kHp - t2Max) < 0.5f && !BuildingOps.IsWorn(fullNow)
                          && BuildingStatusService.Evaluate(s, fullNow).Reason.Contains($"{Mathf.RoundToInt(t2Max)}/{Mathf.RoundToInt(t2Max)}");
            bool ratioOk = upgraded && k2 && Mathf.Abs(hMax - t2Max) < 0.5f && Mathf.Abs(hHp - t2Max * 0.75f) < 1.5f && BuildingOps.IsWorn(hurtNow);
            Expect(fullOk && ratioOk,
                $"C4 屏障 T1 → T2 原地升级施工完工（{ordered} 座，{upFirst?.Describe()}）：满耐久的升完内核 {kHp:F0}/{kMax:F0}（T2 上限 {t2Max:F0}），状态“{(fullNow != null ? BuildingStatusService.Evaluate(s, fullNow).Reason : "无")}”不显示受损（{fullOk}）；" +
                $"掉了 25% 的升完 {hHp:F0}/{hMax:F0}（按比例 75%）（{ratioOk}）");
        }

        // ── D 己方屏障不挡己方炮塔弹道、敌方弹体打在墙上（DEBT-FG6DEF01-07）─────────────────

        private static void CheckBarrierProjectiles()
        {
            CampaignState s = NewWorld(6203, scrap: 2000);
            CombatSite site = Site;
            GridCell? area = FgProductionSelfCheck.FindArea(s, 9, 3, 8f, 18f);
            if (!area.HasValue)
            {
                Fail("D 测试准备：找不到空地");
                return;
            }
            BuildingRecord turret = Register(s, TurretCatalog.LightTypeId, FgProductionSelfCheck.At(area.Value, 0, 0));
            HomeValleyPowerGrid.Recompute(s);
            TurretService.Sync(s);
            var wallCells = new List<BuildingRecord>();
            for (int y = 0; y < 3; y++)
            {
                wallCells.Add(Register(s, B3, FgProductionSelfCheck.At(area.Value, 4, y)));
            }
            DefenseService.Sync(s);
            Seconds(1f);
            if (turret.PowerState != BuildingPowerState.Powered)
            {
                Fail("D 测试准备：炮塔没吃到电");
                return;
            }
            float wallHp0 = wallCells.Sum(b => UnitHp(s, b));
            // 敌人在墙外：炮塔隔着墙开火打到它（己方弹体只与敌对阵营碰撞），墙一点不掉血。
            int target = Hostile(site, new Vector2(area.Value.X + 8f, area.Value.Y + 1f), 40f);
            bool killed = StepUntil(() => Hp(site, target) <= 0f, 20);
            float wallHp1 = wallCells.Sum(b => UnitHp(s, b));
            Expect(killed && Mathf.Abs(wallHp1 - wallHp0) < 0.01f,
                $"D1 DEBT-FG6DEF01-07 己方屏障不挡己方炮塔的弹道：屏障后面的炮塔隔着 T3 墙打掉墙外的敌人（{killed}），墙的耐久 {wallHp0:F0}→{wallHp1:F0} 不变");
            Clear(site);
            // 敌方射手在墙外：弹体打在墙上（离它最近的己方单位就是墙），墙后的炮塔不掉血。
            float turretHp0 = TurretService.TryGetReadout(s, turret.BuildingId, out TurretReadout tr0) ? tr0.Health : 0f;
            Shooter(site, new Vector2(area.Value.X + 9f, area.Value.Y + 1f), 20f, 0.5f, 12f);
            Seconds(4f);
            float wallHp2 = wallCells.Sum(b => UnitHp(s, b));
            float turretHp1 = TurretService.TryGetReadout(s, turret.BuildingId, out TurretReadout tr1) ? tr1.Health : 0f;
            Expect(wallHp2 < wallHp1 - 1f && Mathf.Abs(turretHp1 - turretHp0) < 0.01f,
                $"D2 敌方弹体打在墙上：墙的耐久 {wallHp1:F0}→{wallHp2:F0}，墙后的炮塔 {turretHp0:F0}→{turretHp1:F0} 不掉血");
            Clear(site);
        }

        // ── E 护盾 ─────────────────────────────────────────────────────────────────

        private static void CheckShield()
        {
            CampaignState s = NewWorld(6204, scrap: 2000);
            CombatSite site = Site;
            BuildingRecord gen = Place(s, Shield, 12f, 22f);
            if (gen == null)
            {
                return;
            }
            float cap = DefenseCatalog.ShieldCapacity;
            // E0：刚建成、状态机还没走一步（暂停中打开面板）——读数退回表里的初始状态，名字 / 倒计时不为空，不吸收（冒烟里暂停打开防御面板的场景）。
            ShieldReadout pre = ShieldRo(s, gen);
            ShieldStateDef init0 = DefenseCatalog.InitialState;
            Expect(init0 != null && pre.StateId == init0.Id && !string.IsNullOrEmpty(pre.StateName) && !pre.Absorbs
                   && (init0.Seconds <= 0f || pre.SecondsLeft > 0f),
                $"E0 刚建成未推进时读数 = 初始状态（{pre.StateId} / {pre.StateName}，倒计时 {pre.SecondsLeft:F1} 秒，吸收 {pre.Absorbs}）");
            Seconds(0.2f);
            ShieldReadout ro0 = ShieldRo(s, gen);
            CombatShield sh0 = ShieldOf(s, gen);
            bool charging = ro0.StateId == "charging" && sh0.Active == 0 && ro0.SecondsLeft > 5f && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseShieldFirstBuilt);
            Seconds(6.2f);
            ShieldReadout ro1 = ShieldRo(s, gen);
            bool active = ro1.StateId == "active" && ShieldOf(s, gen).Active == 1 && ro1.Hp > cap * 0.65f && ro1.Hp < cap * 0.8f;
            Seconds(15f);
            bool full = Mathf.Abs(ShieldRo(s, gen).Hp - cap) < 0.5f;
            Expect(charging && active && full,
                $"E1 新建成的护盾先充能（{ro0.StateName}，{ro0.SecondsLeft:F1} 秒后展开，收起时不吸收）→ 展开（护盾值 {ro1.Hp:F0}/{cap:F0}）→ 没受攻击时慢慢回满（{full}）；首次建成钩子");

            // 吸收：敌方射手在护盾圈外朝发生器开火，弹体在入圈点被吸收，发生器不掉血；耗电随承受的伤害上升。
            Vector2 dir = AwayFromCore(s, gen.Position);
            float genHp0 = UnitHp(s, gen);
            int notes0 = NotifyCount("shield_overload");
            float baseDemand = HomeValleyPowerGrid.DemandOf(gen);
            Shooter(site, gen.Position + dir * 13f, 25f, 0.4f, 20f);
            float maxExtra = 0f;
            float maxDemand = 0f;
            bool absorbing = StepUntil(() =>
            {
                maxExtra = Mathf.Max(maxExtra, ShieldRo(s, gen).ExtraPower);
                maxDemand = Mathf.Max(maxDemand, HomeValleyPowerGrid.DemandOf(gen));
                return ShieldOf(s, gen).Hits >= 3;
            }, 10);
            float genHpWhileUp = UnitHp(s, gen);
            CombatShield mid = ShieldOf(s, gen);
            bool absorbed = absorbing && mid.Hp < cap - 1f && Mathf.Abs(genHpWhileUp - genHp0) < 0.01f && mid.Absorbed > 0;
            bool overloaded = StepUntil(() =>
            {
                maxExtra = Mathf.Max(maxExtra, ShieldRo(s, gen).ExtraPower);
                maxDemand = Mathf.Max(maxDemand, HomeValleyPowerGrid.DemandOf(gen));
                return ShieldRo(s, gen).StateId == "overloaded";
            }, 30);
            ShieldReadout ro2 = ShieldRo(s, gen);
            string status = BuildingStatusService.Evaluate(s, gen).Reason;
            bool overloadOk = overloaded && ShieldOf(s, gen).Active == 0 && ro2.Overloads == 1 && ro2.SecondsLeft > 18f && ro2.SecondsLeft <= 20.01f
                              && status.Contains("护盾过载") && NotifyCount("shield_overload") == notes0 + 1 && LastNotify("shield_overload").Contains("秒后重启")
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseShieldFirstOverload);
            Expect(absorbed, $"E2 护盾吸收飞进护盾圈的敌方弹体：吸收 {mid.Hits} 发 / {mid.Absorbed:F0} 点，护盾值 {mid.Hp:F0}/{cap:F0}，发生器耐久 {genHp0:F0}→{genHpWhileUp:F0} 不掉");
            Expect(overloadOk, $"E3 负向“护盾过载”：护盾值耗尽 → 过载、关闭（{ro2.StateName}），倒计时 {ro2.SecondsLeft:F1} 秒（表里 20 秒）后重启；状态“{status}”；警告通知“{LastNotify("shield_overload")}”；首次过载钩子");
            Expect(maxExtra > 0f && maxDemand > baseDemand + 0.5f,
                $"E4 FGR-DEF-012 耗电随承受的伤害上升：基础 {baseDemand:F1}，受攻击时额外 {maxExtra:F1}、最高耗电 {maxDemand:F1}（按档位取整后重新结算电网）");

            // 面板：护盾值条 + 数值、状态与重启倒计时（卡片“护盾值和重启倒计时的显示”）。
            GameObject panelGo = null;
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "DefensePanel.uxml", out panelGo);
                DefensePanelUIToolkit panel = panelGo.AddComponent<DefensePanelUIToolkit>();
                panel.BindView(root);
                DefensePanelUIToolkit.InWorldOverrideForTests = true;
                DefensePanelUIToolkit.Open(gen.BuildingId);
                panel.Refresh(force: true);
                bool panelOk = panel.PanelVisible && panel.ShieldSectionVisible && !panel.TrapSectionVisible && panel.ShieldValueText.Contains("护盾值")
                               && panel.ShieldCountdownText.Contains("还有") && panel.ShieldFillDown && panel.StatusText.Contains("护盾过载")
                               && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefensePanelFirstOpen);
                Expect(panelOk, $"E5 防御面板：护盾值“{panel.ShieldValueText}”、倒计时“{panel.ShieldCountdownText}”、值条收起变灰（{panel.ShieldFillDown}）、耗电“{panel.ShieldPowerText}”、统计“{panel.ShieldStatsText}”");

                // 过载期间护盾收起：发生器本身挨打（射手撤走后在飞的弹体照常命中，所以只挨 1 秒）。
                Seconds(1f);
                float genHpDown = UnitHp(s, gen);
                Clear(site);
                Seconds(0.6f);
                bool restarting = StepUntil(() => ShieldRo(s, gen).StateId == "restarting", 22);
                bool backUp = StepUntil(() => ShieldRo(s, gen).StateId == "active", 7);
                ShieldReadout ro3 = ShieldRo(s, gen);
                panel.Refresh(force: true);
                bool restartOk = genHpDown < genHp0 - 1f && restarting && backUp && ShieldOf(s, gen).Active == 1 && ro3.Hp > cap * 0.45f && ro3.Hp < cap * 0.6f
                                 && !panel.ShieldFillDown && Mathf.Abs(panel.ShieldFillPercent - ro3.Hp / ro3.MaxHp * 100f) < 1f;
                Expect(restartOk,
                    $"E6 过载期间护盾收起、发生器自己挨打（{genHp0:F0}→{genHpDown:F0}）；倒计时结束 → 重启充能 → 重新展开（护盾值 {ro3.Hp:F0}，表里重启 5 秒 × 10%/秒），面板值条跟着变（{panel.ShieldFillPercent:F0}%）");

                // 断电 / 禁用：离线（不吸收），来电后重启。
                BuildingOps.TrySetEnabled(s, gen.BuildingId, false, out _);
                Seconds(0.5f);
                bool offline = ShieldRo(s, gen).StateId == "offline" && ShieldOf(s, gen).Active == 0;
                BuildingOps.TrySetEnabled(s, gen.BuildingId, true, out _);
                Seconds(0.5f);
                bool resumed = ShieldRo(s, gen).StateId == "restarting";
                Expect(offline && resumed, $"E7 禁用（等同断电）→ 离线不吸收（{offline}）；重新启用 → 按表进入重启（{resumed}）");
            }
            finally
            {
                DefensePanelUIToolkit.Close();
                DefensePanelUIToolkit.InWorldOverrideForTests = false;
                if (panelGo != null)
                {
                    Object.DestroyImmediate(panelGo);
                }
            }
            // 画面：护盾圈（展开实圈 / 收起细圈）。
            var rig = new GameObject("__fgdefense_ring") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                StepUntil(() => ShieldRo(s, gen).StateId == "active", 7);
                DefenseViews.FrameUpdate(s, site, rig.transform, null);
                Expect(DefenseViews.RingCount == 1 && DefenseViews.ActiveRingCount == 1, $"E8 地面画出护盾圈（{DefenseViews.RingCount} 个，展开 {DefenseViews.ActiveRingCount} 个；占位表现）");
            }
            finally
            {
                DefenseViews.Clear();
                Object.DestroyImmediate(rig);
            }
            Clear(site);
        }

        // ── F 陷阱 ─────────────────────────────────────────────────────────────────

        /// <summary>陷阱发射器（朝东）+ 西边一格可铺管线 + 东边一条空地；按种子地形找（不写死坐标）。</summary>
        private static BuildingRecord TrapWithRoom(CampaignState s, out GridCell pipe, out GridCell pipe2)
        {
            pipe = default;
            pipe2 = default;
            for (float d = 9f; d <= 22f; d += 1f)
            {
                GridCell? o = FgProductionSelfCheck.FindArea(s, 11, 1, d, d);
                if (!o.HasValue)
                {
                    continue;
                }
                GridCell at = FgProductionSelfCheck.At(o.Value, 2, 0);
                BuildingRecord b = Register(s, Trap, at, 90);
                if (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered)
                {
                    Unregister(s, b);
                    continue;
                }
                pipe = FgProductionSelfCheck.At(o.Value, 1, 0);
                pipe2 = FgProductionSelfCheck.At(o.Value, 0, 0);
                DefenseService.Sync(s);
                return b;
            }
            Fail("测试准备：找不到能放陷阱发射器并在旁边铺管线的空地");
            return null;
        }

        private static void CheckTrap()
        {
            CampaignState s = NewWorld(6205, scrap: 2000);
            CombatSite site = Site;
            BuildingRecord trap = TrapWithRoom(s, out GridCell near, out GridCell far);
            if (trap == null)
            {
                return;
            }
            Seconds(1f);
            BuildingStatus st0 = BuildingStatusService.Evaluate(s, trap);
            long lays0 = DefenseService.LayCount;
            bool idle = st0.ReasonCode == "defense.trap.no_firmware" && st0.Reason.Contains("没有装固件") && DefenseService.LayCount == lays0;
            Expect(idle, $"F1 没装固件：不铺任何东西，状态“{st0.Reason}”（不做玩家没要求的事，FGR-BASE-020）");

            // 固件负向：不在陷阱参数表里 / 核心固件 / 没解锁 都被拒并写明原因（B06）。
            DefenseOpResult noProfile = DefenseService.TrySetTrapFirmware(s, trap.BuildingId, "fw_homing");
            string core = FirmwareKinds.Rows.FirstOrDefault(r => FirmwareKinds.IsCore(r.Id))?.Id;
            DefenseOpResult coreRefused = core != null ? DefenseService.TrySetTrapFirmware(s, trap.BuildingId, core) : DefenseOpResult.Fail(DefenseFailure.BadFirmware, "-");
            s.UnlockedContentIds = s.UnlockedContentIds.Where(x => x != "fw_acid").ToArray();
            DefenseOpResult locked = DefenseService.TrySetTrapFirmware(s, trap.BuildingId, "fw_acid");
            DefenseOpResult notTrap = DefenseService.TrySetTrapPattern(s, HomeGridService.CorePivot(s).ToString(), 1);
            DefenseOpResult badPattern = DefenseService.TrySetTrapPattern(s, trap.BuildingId, 7);
            bool negatives = !noProfile.Ok && noProfile.Failure == DefenseFailure.NoProfile && noProfile.Message.Contains("不能装进陷阱发射器")
                             && !coreRefused.Ok && coreRefused.Failure == DefenseFailure.BadFirmware && coreRefused.Message.Contains("陷阱发射器")
                             && (MechanicalContentUnlock.IsUnlocked(s, "fw_acid") || (!locked.Ok && (locked.Failure == DefenseFailure.FirmwareLocked || locked.Failure == DefenseFailure.BadFirmware)))
                             && !notTrap.Ok && !badPattern.Ok && badPattern.Failure == DefenseFailure.PatternUnknown;
            Expect(negatives, $"F2 固件负向：寻的“{noProfile.Message}”；核心固件“{coreRefused.Message}”；没解锁“{locked.Message}”；铺设方式不认识“{badPattern.Message}”");

            // 装上漏油：没接管线 → 停止铺设，原因“没接管线”，可定位的警告通知 + 首次缺流体钩子。
            int notes0 = NotifyCount("trap_supply");
            DefenseOpResult set = DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Oil);
            Seconds(3f);
            BuildingStatus noPipe = BuildingStatusService.Evaluate(s, trap);
            bool noPipeOk = set.Ok && noPipe.Kind == BuildingStatusKind.NoFluid && noPipe.ReasonCode == "defense.trap.no_supply.nopipe" && noPipe.Reason.Contains("没接管线")
                            && NotifyCount("trap_supply") > notes0 && LastNotify("trap_supply").Contains(PipeNetworkService.FluidName(Fluid("fuel")))
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseTrapFirstSupplyShort) && TrapRo(s, trap).Lays == 0;
            Expect(noPipeOk, $"F3 负向“陷阱缺流体”（没接管线）：不铺，状态“{noPipe.Reason}”，通知“{LastNotify("trap_supply")}”");

            // 错流体：边上的管线里是水。
            bool pipes = PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0).Ok;
            int water = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, Fluid("water"), 20000, 20000);
            Seconds(3f);
            BuildingStatus wrong = BuildingStatusService.Evaluate(s, trap);
            bool wrongOk = pipes && wrong.ReasonCode == "defense.trap.no_supply.wrongfluid" && wrong.Reason.Contains(PipeNetworkService.FluidName(Fluid("water")));
            Expect(wrongOk, $"F4 负向：边上的管线里是别的流体，状态“{wrong.Reason}”");
            PipeNetworkService.Kernel.RemoveProducer(water, out _);
            PipeNetworkService.TryRemove(s, near, out _);
            PipeNetworkService.TryRemove(s, far, out _);
            Seconds(0.5f);

            // 接上燃油：消费者挂上，每轮从缓存取 1 升，沿朝向（东）铺一串挂“油”标签的场地；守恒。
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            const long Supply = 12000;
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, Fluid("fuel"), Supply, Supply);
            long lay0 = TrapRo(s, trap).Lays;
            bool laying = StepUntil(() => TrapRo(s, trap).Lays >= lay0 + 3, 20);
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            var centers = new List<Vector2>();
            DefenseService.FieldCenters(trap, DefenseCatalog.PatternLine, centers);
            int oilZones = ZonesWith(site, oilBit, out int nearEnd, centers[centers.Count - 1], 0.01f);
            bool east = centers.All(c => c.x > trap.Position.x + 0.4f && Mathf.Abs(c.y - trap.Position.y) < 0.01f) && centers.Count >= 5;
            BuildingStatus layingSt = BuildingStatusService.Evaluate(s, trap);
            Expect(laying && oilZones >= centers.Count && nearEnd >= 1 && east && layingSt.Kind == BuildingStatusKind.Working && layingSt.Reason.Contains("油膜带"),
                $"F5 FGR-DEF-013 接上燃油：每 {DefenseCatalog.TrapLaySeconds} 秒铺一轮，沿朝向（东）一条线 {centers.Count} 块“油”标签场地（内核里 {oilZones} 块）；状态“{layingSt.Reason}”");

            // 一片区域：按圈铺。
            DefenseService.TrySetTrapPattern(s, trap.BuildingId, DefenseCatalog.PatternArea);
            var areaCenters = new List<Vector2>();
            DefenseService.FieldCenters(trap, DefenseCatalog.PatternArea, areaCenters);
            Seconds(DefenseCatalog.TrapLaySeconds * 2f);
            ZonesWith(site, oilBit, out int atCenter, trap.Position, 0.01f);
            bool area = areaCenters.Count > centers.Count && atCenter >= 1 && areaCenters.All(c => Vector2.Distance(c, trap.Position) <= DefenseCatalog.TrapAreaRadius + 0.01f);
            Expect(area, $"F6 改成“一片区域”：以发射器为圆心铺 {areaCenters.Count} 块（半径 {DefenseCatalog.TrapAreaRadius} 米内）");
            DefenseService.TrySetTrapPattern(s, trap.BuildingId, DefenseCatalog.PatternLine);

            // 断供：拿掉供给点，存着的铺完后停，原因“管线里没有燃油了”；燃油逐毫升守恒。
            PipeNetworkService.Kernel.RemoveProducer(feed, out long left);
            bool dry = StepUntil(() => BuildingStatusService.Evaluate(s, trap).ReasonCode == "defense.trap.no_supply.dry", 120);
            long laysTotal = TrapRo(s, trap).Lays - lay0;
            long stored = (long)Math.Round(DefenseService.TrapStoredLiters(Rec(s, trap), Fluid("fuel")) * 1000f);
            long accounted = left + stored + laysTotal * 1000 + PipeNetworkService.Kernel.TotalStoredSlow();
            Expect(dry && accounted == Supply && laysTotal > 3,
                $"F7 负向：断供后铺完存着的就停（“{BuildingStatusService.Evaluate(s, trap).Reason}”）；燃油逐毫升守恒：供给 {Supply / 1000f:F0} 升 = 剩余 {left / 1000f:F1} + 发射器存着 {stored / 1000f:F1} + 铺了 {laysTotal} 轮 × 1 升");

            // 电磁场只用电：不需要管线。
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Arc);
            long l0 = TrapRo(s, trap).Lays;
            Seconds(3f);
            uint shock = NamedReactionCatalog.BitOf("Shock");
            Expect(TrapRo(s, trap).Lays > l0 && TrapRo(s, trap).SupplyLine.Contains("只用电") && ZonesWith(site, shock, out _, trap.Position, 99f) > 0,
                $"F8 电弧链铺电磁场：只用电，不接管线也铺（{TrapRo(s, trap).Lays - l0} 轮，“{TrapRo(s, trap).SupplyLine}”）");

            // 缺电：不铺（通用状态先报缺电）。
            BuildingOps.TrySetEnabled(s, trap.BuildingId, false, out _);
            long l1 = TrapRo(s, trap).Lays;
            Seconds(3f);
            Expect(TrapRo(s, trap).Lays == l1 && BuildingStatusService.Evaluate(s, trap).Kind == BuildingStatusKind.Disabled,
                $"F9 禁用后不铺（状态 {BuildingStatusService.Evaluate(s, trap).Kind}）");
            BuildingOps.TrySetEnabled(s, trap.BuildingId, true, out _);

            // 面板：固件下拉只列能装的、选中即生效；铺设方式按钮。
            GameObject panelGo = null;
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "DefensePanel.uxml", out panelGo);
                DefensePanelUIToolkit panel = panelGo.AddComponent<DefensePanelUIToolkit>();
                panel.BindView(root);
                DefensePanelUIToolkit.InWorldOverrideForTests = true;
                DefensePanelUIToolkit.Open(trap.BuildingId);
                panel.Refresh(force: true);
                int oilAt = panel.FirmwareIds.ToList().IndexOf(Oil);
                panel.SelectFirmware(oilAt);
                panel.ClickPattern(DefenseCatalog.PatternArea);
                panel.Refresh(force: true);
                bool panelOk = panel.TrapSectionVisible && !panel.ShieldSectionVisible && oilAt >= 0 && !panel.FirmwareIds.Contains("fw_homing")
                               && Rec(s, trap).TrapFirmware == Oil && Rec(s, trap).TrapPattern == DefenseCatalog.PatternArea
                               && panel.PatternAreaButton.text.StartsWith("●", StringComparison.Ordinal) && panel.TrapSupplyText.Contains("补给");
                Expect(panelOk, $"F10 陷阱面板：固件下拉 {panel.FirmwareChoices.Count} 项（只列能装进陷阱发射器的），选中漏油即生效；铺设方式按钮“{panel.PatternAreaButton.text}”；补给“{panel.TrapSupplyText}”");
            }
            finally
            {
                DefensePanelUIToolkit.Close();
                DefensePanelUIToolkit.InWorldOverrideForTests = false;
                if (panelGo != null)
                {
                    Object.DestroyImmediate(panelGo);
                }
            }
        }

        // ── G FGT-DEF-002 阵地反应 ─────────────────────────────────────────────────

        private static void CheckPositionalReaction()
        {
            CampaignState s = NewWorld(6206, scrap: 3000);
            CombatSite site = Site;
            BuildingRecord trap = TrapWithRoom(s, out GridCell near, out GridCell far);
            if (trap == null)
            {
                return;
            }
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Oil);
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.Kernel.AddProducer(far.X, far.Y, Fluid("fuel"), 50000, 50000);
            // 燃迹炮塔：固定底盘 + 连射器 + 燃迹（命中挂“火”标签）。炮塔的燃油补给由 FG6-DEF-01 自检覆盖，这里用测试捷径装满一匣（不是本 Story 的行为）。
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(CarrierReadings.FixedChassisId, ComponentCatalog.CompGunId, null, null, new[] { Burn });
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != BpBurn)
                .Append(new BlueprintRecord { BlueprintId = BpBurn, DisplayName = "燃迹炮塔", ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            BuildingRecord turret = null;
            GridCell core = HomeGridService.CorePivot(s);
            var centers = new List<Vector2>();
            DefenseService.FieldCenters(trap, DefenseCatalog.PatternLine, centers);
            Vector2 enemyAt = centers[2];
            for (int dy = -6; dy <= 6 && turret == null; dy++)
            {
                for (int dx = -6; dx <= 6 && turret == null; dx++)
                {
                    var c = new GridCell(Mathf.RoundToInt(enemyAt.x) + dx, Mathf.RoundToInt(enemyAt.y) + dy);
                    if (Mathf.Abs(dy) < 3 || !HomeGridService.ValidatePlacement(s, TurretCatalog.LightTypeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord t = Register(s, TurretCatalog.LightTypeId, c);
                    if (t.PowerState != BuildingPowerState.Powered)
                    {
                        Unregister(s, t);
                        continue;
                    }
                    turret = t;
                }
            }
            if (turret == null)
            {
                Fail("G 测试准备：油膜带旁边放不下有电的炮塔");
                return;
            }
            TurretService.Sync(s);
            TurretService.TryAssignBlueprint(s, turret.BuildingId, BpBurn);
            TurretService.Sync(s);
            int serial = TurretService.Find(s, turret.BuildingId).Serial;
            // 先让油膜带铺起来，再放敌人（站在油膜带上）。
            StepUntil(() => TrapRo(s, trap).Lays >= 2, 10);
            int ri = RuleIndex("reaction_deflagrate");
            int c0 = ri >= 0 ? site.Kernel.ReactionCountOf(ri) : 0;
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            uint fireBit = NamedReactionCatalog.BitOf("Fire");
            int enemy = Hostile(site, enemyAt, 100000f);
            bool oiled = StepUntil(() => site.Kernel.TryGetStatus(enemy, out uint mask, out _, out _, out _, out _) && (mask & oilBit) != 0, 6);
            int residue0 = 0;
            for (int i = 0; i < site.Kernel.ZoneCount; i++)
            {
                if (site.Kernel.TryGetZone(i, out CombatZone z) && z.Look == CombatZoneLook.Residue && (z.StatusMask & fireBit) != 0)
                {
                    residue0++;
                }
            }
            site.SetTurretAmmo(serial, 10f);
            float hp0 = Hp(site, enemy);
            bool reacted = StepUntil(() => site.Kernel.ReactionCountOf(ri) > c0, 20);
            int burning = 0;
            for (int i = 0; i < site.Kernel.ZoneCount; i++)
            {
                if (site.Kernel.TryGetZone(i, out CombatZone z) && (z.StatusMask & fireBit) != 0 && z.Faction == CombatFaction.Player
                    && Vector2.Distance(new Vector2((float)z.Pos.x, (float)z.Pos.y), enemyAt) < 3f)
                {
                    burning++;
                }
            }
            float dmg = hp0 - Hp(site, enemy);
            Expect(ri >= 0 && oiled && reacted && burning > residue0 && dmg > 0f,
                $"G FGT-DEF-002 阵地反应：陷阱发射器沿来路铺油膜带，站在上面的敌人带上“油”标签（{oiled}）；燃迹炮塔的弹体命中挂“火”→ 爆燃（反应次数 {c0}→{site.Kernel.ReactionCountOf(ri)}），" +
                $"脚下留下燃烧区（含火标签的己方区域 {burning} 块），敌人掉血 {dmg:F0}");

            // G2 复审修复（P1，B12“满”）：陷阱场地有自己的上限（trap.capacity.fields），铺满后不挤掉炮塔的读法区域——燃迹炮塔照样打出爆燃、留下燃烧区；
            // 陷阱这边状态行 / 面板写明“场地已满，上一轮有 N 块没铺”。
            int cap = DefenseCatalog.TrapFieldCapacity;
            Vector2 farAway = enemyAt + AwayFromCore(s, enemyAt) * 30f;
            int filled = 0;
            for (int i = 0; i < cap + 8; i++)
            {
                if (!site.SpawnTrapField(farAway + new Vector2(i % 24, i / 24) * 0.05f, 0.3f, 40f, 0f, 0u, 0f, 0f, 0f))
                {
                    break;
                }
                filled++;
            }
            int fieldsNow = 0;
            for (int i = 0; i < site.Kernel.ZoneCount; i++)
            {
                if (site.Kernel.TryGetZone(i, out CombatZone z) && z.Kind == CombatConst.ZoneKindField)
                {
                    fieldsNow++;
                }
            }
            bool poolFull = fieldsNow == cap && !site.SpawnTrapField(farAway, 0.3f, 40f, 0f, 0u, 0f, 0f, 0f);
            CombatCounters before = site.Kernel.Counters;
            int c1 = site.Kernel.ReactionCountOf(ri);
            site.Kernel.ApplyStatus(enemy, oilBit, 8f, 0f, 0f, 0f, 0); // 敌人身上还带着油（场地已满时新的油膜带铺不出来，这里直接挂上，只测读法区域不被挤掉）
            site.SetTurretAmmo(serial, 10f);
            bool reacted2 = StepUntil(() => site.Kernel.ReactionCountOf(ri) > c1, 20);
            CombatCounters after = site.Kernel.Counters;
            bool readingOk = reacted2 && after.ZonesSpawned > before.ZonesSpawned && after.ReadingRefused == before.ReadingRefused;
            StepUntil(() => TrapRo(s, trap).FieldsRefused > 0, 4);
            TrapReadout full = TrapRo(s, trap);
            string reason = BuildingStatusService.Evaluate(s, trap).Reason;
            bool told = full.FieldsRefused > 0 && reason.Contains("场地已满") && DefenseService.LastRefusedFields > 0;
            Expect(poolFull && readingOk && told,
                $"G2 陷阱场地铺满（上限 {cap}，已铺 {fieldsNow}）不挤掉读法区域：燃迹炮塔照样爆燃（反应 {c1}→{site.Kernel.ReactionCountOf(ri)}），读法区域照常生成" +
                $"（{before.ZonesSpawned}→{after.ZonesSpawned}，被拒 {before.ReadingRefused}→{after.ReadingRefused}）；陷阱状态行写明“{reason.Replace("\n", " / ")}”");
            Clear(site);
        }

        // ── H 复制设置 ───────────────────────────────────────────────────────────

        private static void CheckCopySettings()
        {
            CampaignState s = NewWorld(6207);
            BuildingRecord a = Place(s, Trap, 8f, 22f);
            BuildingRecord b = Place(s, Trap, 8f, 22f);
            if (a == null || b == null)
            {
                return;
            }
            DefenseService.TrySetTrapFirmware(s, a.BuildingId, Oil);
            DefenseService.TrySetTrapPattern(s, a.BuildingId, DefenseCatalog.PatternArea);
            PlanSettings.BuildingSettings(s, a, out int s0, out int s1, out int s2);
            string desc = PlanSettings.Describe(PlanSettings.Family.Power, s0, s1, s2);
            bool applied = PlanSettings.ApplyBuildingSettings(s, b, s0, s1, s2, out bool recipe);
            DefenseRecord rb = Rec(s, b);
            Expect(applied && recipe && rb.TrapFirmware == Oil && rb.TrapPattern == DefenseCatalog.PatternArea && desc.Contains(FirmwareKinds.DisplayName(Oil) ?? "?") && desc.Contains("一片区域"),
                $"H 复制设置（吸管 / 复制设置 / 布局粘贴同一编码）：陷阱的固件 + 铺设方式写到另一座（“{desc}”）");
        }

        // ── H2 面板入口、英文、布局探针 ─────────────────────────────────────────────────

        private static void CheckPanelsAndProbe()
        {
            CampaignState s = NewWorld(6214);
            BuildingRecord gen = Place(s, Shield, 10f, 22f);
            BuildingRecord trap = Place(s, Trap, 8f, 22f, 90);
            BuildingRecord wall = Place(s, B1, 8f, 20f);
            if (gen == null || trap == null || wall == null)
            {
                return;
            }
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Arc);
            Seconds(1f);
            GameObject pgo = null;
            GameObject dgo = null;
            try
            {
                // 建筑面板：“护盾… / 陷阱… / 屏障…”按种类出现，点它打开防御面板（与玩家点击同一回调）。
                VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out pgo);
                ProductionPanelUIToolkit.InWorldOverrideForTests = true;
                ProductionPanelUIToolkit prod = pgo.AddComponent<ProductionPanelUIToolkit>();
                prod.BindView(proot);
                VisualElement droot = FgProductionSelfCheck.MountUxml(UiKitFolder + "DefensePanel.uxml", out dgo);
                DefensePanelUIToolkit panel = dgo.AddComponent<DefensePanelUIToolkit>();
                panel.BindView(droot);
                DefensePanelUIToolkit.InWorldOverrideForTests = true;
                var texts = new List<string>();
                bool entries = true;
                foreach (BuildingRecord b in new[] { gen, trap, wall })
                {
                    ProductionPanelUIToolkit.Open(b.BuildingId);
                    prod.Refresh();
                    bool visible = ProductionPanelUIToolkit.Visible(prod.DefenseButton);
                    texts.Add(prod.DefenseButton.text);
                    prod.OpenDefense();
                    panel.Refresh(force: true);
                    entries &= visible && DefensePanelUIToolkit.IsOpen && DefensePanelUIToolkit.BuildingId == b.BuildingId && !ProductionPanelUIToolkit.IsOpen;
                    DefensePanelUIToolkit.Close();
                }
                BuildingRecord other = s.BuildingRecords.First(x => x.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
                ProductionPanelUIToolkit.Open(other.BuildingId);
                prod.Refresh();
                bool hiddenElsewhere = !ProductionPanelUIToolkit.Visible(prod.DefenseButton);
                ProductionPanelUIToolkit.Close();
                Expect(entries && texts.SequenceEqual(new[] { "护盾…", "陷阱…", "屏障…" }) && hiddenElsewhere,
                    $"H2 建筑面板按种类出现“{string.Join(" / ", texts)}”按钮，点它关掉建筑面板、打开防御面板；不是防御建筑时不出现");

                // 切到英文：面板文字换成英文（不留中文）。
                DefensePanelUIToolkit.Open(gen.BuildingId);
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh(force: true);
                bool en = panel.ShieldValueText.Length > 0 && !panel.ShieldValueText.Any(c => c >= 0x4E00 && c <= 0x9FFF) && !panel.FooterText.Any(c => c >= 0x4E00 && c <= 0x9FFF)
                          && !panel.TitleText.Any(c => c >= 0x4E00 && c <= 0x9FFF);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.Refresh(force: true);
                DefensePanelUIToolkit.Close();
                Expect(en, $"H3 切到英文：防御面板文字全部换成英文（“{panel.ShieldValueText}”）");

                string probeShield = UiToolkitLayoutProbe.Probe(UiKitFolder + "DefensePanel.uxml", "DefenseWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("DefenseRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("DefenseTrapSection")?.AddToClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("DefenseBarrierSection")?.AddToClassList("uk-hidden");
                });
                string probeTrap = UiToolkitLayoutProbe.Probe(UiKitFolder + "DefensePanel.uxml", "DefenseWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("DefenseRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("DefenseShieldSection")?.AddToClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("DefenseBarrierSection")?.AddToClassList("uk-hidden");
                });
                bool pass = probeShield.StartsWith("PASS", StringComparison.Ordinal) && probeTrap.StartsWith("PASS", StringComparison.Ordinal);
                Expect(pass, "H4 布局探针（四种分辨率 + 超长文字 + USS 体检）：防御面板护盾段 " + (probeShield.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probeShield)
                             + "；陷阱段 " + (probeTrap.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probeTrap));
            }
            finally
            {
                DefensePanelUIToolkit.Close();
                DefensePanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                if (dgo != null)
                {
                    Object.DestroyImmediate(dgo);
                }
                if (pgo != null)
                {
                    Object.DestroyImmediate(pgo);
                }
            }
        }

        // ── I 存读档 ─────────────────────────────────────────────────────────────

        private static string DefenseSignature(CampaignState s, CombatSite site)
        {
            var sb = new StringBuilder();
            foreach (DefenseRecord r in DefenseService.All(s).OrderBy(x => x.Serial))
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, r.BuildingId);
                sb.Append(r.Serial).Append(':').Append(r.ShieldState).Append(':').Append(r.ShieldUntilTick).Append(':').Append(r.TrapLays).Append(':');
                if (site.TryGetDefenseHealth(r.Serial, out float hp, out _, out bool alive))
                {
                    sb.Append(hp.ToString("R", CultureInfo.InvariantCulture)).Append(alive ? "A" : "D");
                }
                if (site.TryGetShield(r.Serial, out CombatShield sh))
                {
                    sb.Append('/').Append(sh.Hp.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(sh.Active).Append('/').Append(sh.Hits).Append('/').Append(sh.Depletions);
                }
                sb.Append('/').Append(b != null ? b.Health.ToString("R", CultureInfo.InvariantCulture) : "-").Append('|');
            }
            return sb.Append("zones=").Append(site.Kernel.ZoneCount).ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(6208, scrap: 3000);
            CombatSite site = Site;
            BuildingRecord gen = Place(s, Shield, 12f, 22f);
            BuildingRecord trap = TrapWithRoom(s, out GridCell near, out GridCell far);
            BuildingRecord wall = Place(s, B2, 8f, 20f);
            BuildingRecord gate = Place(s, Gate, 8f, 20f);
            if (gen == null || trap == null || wall == null || gate == null)
            {
                return;
            }
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Oil);
            DefenseService.TrySetTrapPattern(s, trap.BuildingId, DefenseCatalog.PatternArea);
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, Fluid("fuel"), 20000, 20000);
            Seconds(8f);
            Site.DamageDefense(Rec(s, wall).Serial, 55f);
            Shooter(site, gen.Position + AwayFromCore(s, gen.Position) * 13f, 25f, 0.4f, 20f);
            StepUntil(() => ShieldRo(s, gen).StateId == "overloaded", 30);
            Clear(site);
            Seconds(1.5f);
            PipeNetworkService.Kernel.RemoveProducer(feed, out _); // 测试供给点不进存档
            Seconds(0.4f);
            WorldSimulation.SyncAllForSave();
            string domain = Json(DefenseService.StateOf(s));
            string sig = DefenseSignature(s, site);
            float stored = DefenseService.TrapStoredLiters(Rec(s, trap), Fluid("fuel"));
            byte[] snap10 = site.Kernel.SerializeFormatForTests(10); // FG6-DEF-05 起当前格式是 11：格式 10 当作旧快照往返
            byte[] snapCur = site.Kernel.Serialize();
            byte[] snap9 = site.Kernel.SerializeFormatForTests(9);
            ulong hash = site.Kernel.StateHash();
            int shields = site.ShieldCount;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("I1 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            CampaignState l = rr.State;
            CombatSite ls = Site;
            string domainAfter = Json(DefenseService.StateOf(l));
            string sigAfter = DefenseSignature(l, ls);
            float storedAfter = DefenseService.TrapStoredLiters(DefenseService.Find(l, trap.BuildingId), Fluid("fuel"));
            Expect(save.Success && domain == domainAfter && sig == sigAfter && Mathf.Abs(stored - storedAfter) < 0.001f && sig.Contains("overloaded"),
                $"I1 真文件存读档：防御域逐字段一致（护盾状态 / 到期步 / 陷阱固件 / 铺设方式 / 消费者 / 留存流体）（{domain == domainAfter}）；内核里结构单位耐久、护盾值 / 展开 / 吸收数一致（{sig == sigAfter}）；" +
                $"发射器存着的燃油 {stored:F1}→{storedAfter:F1} 升" + (sig == sigAfter ? string.Empty : $"\n存档前：{sig}\n读档后：{sigAfter}"));

            Seconds(25f);
            bool continues = ShieldRo(l, HomeGridService.FindBuilding(l, gen.BuildingId)).StateId == "active" && DefenseService.SyncCount > 0;
            Expect(continues, "I2 读档后接着跑：过载倒计时接着走，到点重启并重新展开");

            CombatConfig cfg = ls.Kernel.Config;
            using var k10 = new CombatKernel(cfg, 256);
            CombatLoadResult r10 = k10.Load(snap10);
            using var k9 = new CombatKernel(cfg, 256);
            CombatLoadResult r9 = k9.Load(snap9);
            using var kCur = new CombatKernel(cfg, 256);
            CombatLoadResult rCur = kCur.Load(snapCur);
            Expect(CombatKernel.PeekFormat(snap10) == 10 && r10 == CombatLoadResult.Ok && k10.StateHash() == hash && k10.ShieldCount == shields && shields == 1
                   && CombatKernel.PeekFormat(snap9) == 9 && r9 == CombatLoadResult.Ok && k9.ShieldCount == 0
                   && CombatKernel.PeekFormat(snapCur) == CombatConst.FormatVersion && rCur == CombatLoadResult.Ok && kCur.StateHash() == hash && kCur.ShieldCount == shields,
                $"I3 战斗内核快照：当前格式 {CombatConst.FormatVersion} 往返（{rCur}）与格式 10 往返（{r10}）状态哈希一致、护盾 {k10.ShieldCount} 座；格式 9 的旧快照照常读（{r9}，没有护盾表 → 热更层按记录重新登记）");

            // 旧快照没有护盾表：状态机按记录重新登记（读档后的第一步）。
            ls.RemoveShield(DefenseService.Find(l, gen.BuildingId).Serial);
            Seconds(0.2f);
            Expect(ls.ShieldCount == 1, "I4 内核里没有护盾（旧快照）时，状态机按记录的护盾值与状态重新登记");

            CampaignState old = CampaignState.CreateNew("fgdefense-old", "Standard", 6209);
            old.Raids.Defense = null;
            CampaignFgStateDomains.EnsureAll(old);
            bool empty = old.Raids.Defense != null && old.Raids.Defense.Records.Length == 0;
            old.Raids.Defense.Records = new[]
            {
                new DefenseRecord { BuildingId = "home:trap_emitter#1", Serial = 3, TrapPattern = 9, ShieldHp = float.NaN, HeldFluids = new[] { 1 }, HeldMl = Array.Empty<long>() },
                new DefenseRecord { BuildingId = "home:trap_emitter#1", Serial = 4 },
                new DefenseRecord { BuildingId = "home:barrier_t1#2", Serial = 3, ConsumerId = -5 },
                null,
            };
            old.Raids.Defense.NextSerial = 1;
            DefenseService.EnsureState(old);
            DefenseRecord[] recs = old.Raids.Defense.Records;
            bool clamped = empty && recs.Length == 2 && recs[0].TrapPattern == DefenseCatalog.PatternLine && recs[0].ShieldHp == 0f && recs[0].HeldFluids.Length == 0
                           && recs[1].Serial != recs[0].Serial && recs[1].ConsumerId == 0 && old.Raids.Defense.NextSerial > recs.Max(x => x.Serial);
            Expect(clamped, "I5 旧档没有防御域补成空域；坏值：铺设方式钳回、护盾值不是数归 0、留存流体不配对清空、重复建筑只留第一条、重复序号重编、负的消费者句柄清零");
        }

        /// <summary>J：同一份存档，“存完接着跑”与“读档后跑”的防御状态逐字段一致（护盾状态机按步序号、陷阱按整拍铺设，读档不改变结果）。</summary>
        private static void CheckLoadContinuation()
        {
            CampaignState s = NewWorld(6210, scrap: 3000);
            CombatSite site = Site;
            BuildingRecord gen = Place(s, Shield, 12f, 22f);
            BuildingRecord trap = TrapWithRoom(s, out _, out _);
            if (gen == null || trap == null)
            {
                return;
            }
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Arc);
            Seconds(7f);
            Shooter(site, gen.Position + AwayFromCore(s, gen.Position) * 13f, 25f, 0.5f, 20f);
            Seconds(3.37f); // 停在两拍之间存档
            WorldSimulation.SyncAllForSave();
            long tickAtSave = GameClock.Ticks;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(8f);
            string straight = DefenseSignature(s, site);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("J 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            bool sameTick = GameClock.Ticks == tickAtSave;
            Seconds(8f);
            string loaded = DefenseSignature(rr.State, Site);
            Expect(save.Success && sameTick && straight == loaded,
                $"J 读档接着跑 = 不存档一路跑：存档停在两拍之间（步 {tickAtSave}），之后各跑 8 游戏秒，护盾状态 / 到期步 / 护盾值 / 吸收数 / 结构耐久 / 铺设轮数 / 区域数逐字段一致"
                + (straight == loaded ? string.Empty : $"\n不存档：{straight}\n读档后：{loaded}"));
            Clear(Site);
        }

        // ── K / L 暂停、倍速、观察 ─────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            CombatSite site = Site;
            BuildingRecord gen = Place(s, Shield, 12f, 22f);
            BuildingRecord trap = Place(s, Trap, 8f, 22f, 90);
            BuildingRecord wall = Place(s, B1, 8f, 20f);
            if (gen == null || trap == null || wall == null)
            {
                return "no-defense";
            }
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Arc);
            WorldSimulation.StepMany(GameClock.StepHz * 7);
            Shooter(site, gen.Position + AwayFromCore(s, gen.Position) * 13f, 25f, 0.4f, 20f);
            Hostile(site, trap.Position + new Vector2(3f, 0f), 5000f);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Json(DefenseService.StateOf(s)) + site.Kernel.StateHash() + DefenseService.SyncCount + DefenseService.LayCount;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Json(DefenseService.StateOf(s)) + site.Kernel.StateHash() + DefenseService.SyncCount + DefenseService.LayCount == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            string result = Json(DefenseService.StateOf(s)) + "|" + site.Kernel.StateHash().ToString("X16") + "|" + gen.Health.ToString("F2", CultureInfo.InvariantCulture);
            Clear(site);
            return result;
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(6211, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != null && !reference.StartsWith("no-defense", StringComparison.Ordinal) && reference.Contains("\"ShieldOverloads\":1"),
                "K 暂停中（120 帧）防御记录、内核状态、对账与铺设次数都不动；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒（护盾吸收 → 过载 → 重启、陷阱铺电磁场），防御记录（护盾状态 / 到期步 / 过载次数 / 铺设轮数）、" +
                "家园内核状态哈希与发生器耐久逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6212, true, 1f, false, out _);
            string unseen = RunScenario(6212, false, 1f, false, out _);
            Expect(seen == unseen && !seen.StartsWith("no-defense", StringComparison.Ordinal),
                "L 同一场护盾 / 陷阱攻防在观察与不观察家园时跑 30 游戏秒，防御记录、内核状态哈希与耐久逐字段一致（FGR-BASE-021：远征时家园防御照常运转）"
                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        /// <summary>B25：种子测试集里几张不同地形——按地形找空地放屏障 / 护盾 / 陷阱，都能进内核、护盾展开、陷阱铺设、来路预览有来路，不依赖固定坐标。</summary>
        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(4).ToArray();
            var bad = new List<string>();
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed);
                BuildingRecord wall = Place(s, B1, 8f, 20f);
                BuildingRecord gen = Place(s, Shield, 10f, 22f);
                BuildingRecord trap = Place(s, Trap, 8f, 22f, 90);
                if (wall == null || gen == null || trap == null)
                {
                    bad.Add($"{seed}：放不下");
                    continue;
                }
                DefenseService.TrySetTrapFirmware(s, trap.BuildingId, Arc);
                Seconds(7f);
                var route = new RoutePreview();
                DefenseService.PreviewRoutes(s, new[] { new GridCell(wall.GridX + 1, wall.GridY) }, route);
                if (Unit(s, wall) == 0 || ShieldRo(s, gen).StateId != "active" || TrapRo(s, trap).Lays == 0 || !route.HasRoutes)
                {
                    bad.Add($"{seed}：内核 {Unit(s, wall)} / 护盾 {ShieldRo(s, gen).StateId} / 铺设 {TrapRo(s, trap).Lays} / 来路 {route.HasRoutes}");
                }
            }
            Expect(seeds.Length == 4 && bad.Count == 0,
                $"B25 种子测试集 {string.Join(" / ", seeds)}：每张地形都按地形找到空地放下屏障 / 护盾 / 陷阱，进内核、护盾展开、陷阱铺设、来路预览找到来路" + (bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad)));
        }

        // ── N 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6213, scrap: 9000);
            CombatSite site = Site;
            GridCell core = HomeGridService.CorePivot(s);
            var walls = new List<BuildingRecord>();
            for (int ringR = GridContent.TuningInt("grid.core_reserve_ring") + 8; ringR <= 60 && walls.Count < 200; ringR += 3)
            {
                for (int k = -ringR; k <= ringR && walls.Count < 200; k++)
                {
                    foreach (GridCell c in new[] { new GridCell(core.X + k, core.Y - ringR), new GridCell(core.X + k, core.Y + ringR), new GridCell(core.X - ringR, core.Y + k), new GridCell(core.X + ringR, core.Y + k) })
                    {
                        if (walls.Count < 200 && HomeGridService.ValidatePlacement(s, B1, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            walls.Add(Register(s, B1, c));
                        }
                    }
                }
            }
            var gens = new List<BuildingRecord>();
            var traps = new List<BuildingRecord>();
            for (int i = 0; i < 8; i++)
            {
                BuildingRecord g = Place(s, Shield, 10f, 30f);
                if (g != null)
                {
                    gens.Add(g);
                }
            }
            for (int i = 0; i < 20; i++)
            {
                BuildingRecord t = Place(s, Trap, 8f, 30f, 90);
                if (t != null)
                {
                    DefenseService.TrySetTrapFirmware(s, t.BuildingId, Arc);
                    traps.Add(t);
                }
            }
            // 测试捷径：电力由电网自检覆盖，这里让全部护盾 / 陷阱吃到电（测的是防御对账、状态机与内核，不是电网分配）。
            foreach (BuildingRecord b in gens.Concat(traps))
            {
                b.PowerState = BuildingPowerState.Powered;
            }
            DefenseService.Sync(s);
            Seconds(7f);
            CombatBench.Spec spec = CombatBench.PerfSpec();
            Vector2 center = new Vector2(core.X, core.Y);
            for (int g = 0, spawned = 0; g < 4 && spawned < 200; g++)
            {
                float ang = g * Mathf.PI * 0.5f + 0.4f;
                spawned += CombatBench.SpawnRaidGroup(site, center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 55f, center, 50, spec).Count;
            }
            int hz = GameClock.StepHz;
            var stepMs = new List<double>(600);
            var syncMs = new List<double>(64);
            var sw = new Stopwatch();
            for (int i = 0; i < 600; i++)
            {
                sw.Restart();
                WorldSimulation.StepMany(1);
                sw.Stop();
                stepMs.Add(sw.Elapsed.TotalMilliseconds);
                if (i % 30 == 0)
                {
                    sw.Restart();
                    DefenseService.Sync(s);
                    sw.Stop();
                    syncMs.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            // 防御服务每步（护盾状态机）的开销：单独测 600 步只调 WorldStep 的非整拍部分。
            var shieldMs = new List<double>(600);
            long tick = GameClock.Ticks;
            for (int i = 0; i < 600; i++)
            {
                long t = tick * 7 + 1 + i * 7; // 避开对账 / 铺设整拍（只测每步的状态机）
                if (t % Math.Max(1, (long)Math.Round(DefenseCatalog.SyncSeconds * hz)) == 0 || t % Math.Max(1, (long)Math.Round(DefenseCatalog.TrapLaySeconds * hz)) == 0)
                {
                    t++;
                }
                sw.Restart();
                DefenseService.WorldStep(s, t, hz);
                sw.Stop();
                shieldMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            stepMs.Sort();
            double stepAvg = stepMs.Average();
            double stepP95 = stepMs[(int)(stepMs.Count * 0.95)];
            double syncAvg = syncMs.Average();
            double shieldAvg = shieldMs.Average();
            // 放置预览：40 格一段墙的规划（逐格校验 + Burst 距离场两次）。
            GridCell? area = FgProductionSelfCheck.FindArea(s, 10, 2, 12f, 30f);
            var pw = new Stopwatch();
            int previews = 0;
            if (area.HasValue)
            {
                pw.Start();
                for (int i = 0; i < 20; i++)
                {
                    DefenseService.PlanWall(s, B1, area.Value, new GridCell(area.Value.X + 9, area.Value.Y + (i % 2)));
                    previews++;
                }
                pw.Stop();
            }
            double previewMs = previews > 0 ? pw.Elapsed.TotalMilliseconds / previews : 0;
            var rig = new GameObject("__fgdefense_perf_views") { hideFlags = HideFlags.HideAndDontSave };
            double viewMs;
            try
            {
                DefenseViews.FrameUpdate(s, site, rig.transform, null);
                var fsw = Stopwatch.StartNew();
                for (int i = 0; i < 600; i++)
                {
                    DefenseViews.FrameUpdate(s, site, rig.transform, null);
                }
                fsw.Stop();
                viewMs = fsw.Elapsed.TotalMilliseconds / 600.0;
            }
            finally
            {
                DefenseViews.Clear();
                Object.DestroyImmediate(rig);
            }
            PerfLines.Add($"家园 {walls.Count} 段屏障 + {gens.Count} 座护盾 + {traps.Count} 座陷阱 + 200 突袭者：世界单步 平均 {stepAvg:F3} ms / p95 {stepP95:F3} ms；防御对账 {syncAvg:F3} ms/次；" +
                          $"护盾状态机每步 {shieldAvg:F4} ms；40 格墙的放置预览（逐格校验 + 放下后的来路距离场；放下前按镜像版本缓存）{previewMs:F2} ms/次；防御画面每帧 {viewMs:F4} ms");
            PerfGate.Expect(walls.Count >= 150 && gens.Count >= 6 && traps.Count >= 15,
                $"N 性能：{walls.Count} 段屏障 + {gens.Count} 护盾 + {traps.Count} 陷阱 + 200 突袭者，世界单步平均 {stepAvg:F3} ms / p95 {stepP95:F3} ms（阈值 8 ms，含全部系统）；防御对账 {syncAvg:F3} ms/次（阈值 1.5 ms）；" +
                $"护盾状态机每步 {shieldAvg:F4} ms（阈值 0.05 ms）；放置预览 {previewMs:F2} ms/次（阈值 8 ms，只在拖拽换格时）；画面每帧 {viewMs:F4} ms（阈值 0.05 ms）（Editor batchmode，真机另测 FG15-SYS-02）",
                new[]
                {
                    PerfGate.Le(stepAvg, 8, "世界单步平均 ms"), PerfGate.Le(syncAvg, 1.5, "防御对账 ms/次"), PerfGate.Le(shieldAvg, 0.05, "护盾状态机每步 ms"),
                    PerfGate.Le(previewMs, 8, "放置预览 ms/次"), PerfGate.Le(viewMs, 0.05, "防御画面每帧 ms"),
                }, Expect, Line);
            Clear(site);
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
