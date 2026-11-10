using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Campaign.Feedback;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-TER-01 熔坑（FG19 FGR-EMG-152、154，FG19B V06 / V07，ADR-EMG-001 ⑯；FGT-EMG-060 熔坑部分）：
    /// 内核段起真实 CombatKernel（与 CombatSite 同一套写表）断言：熔融金属凝固 / 酸到期在可熔地面“同一位置”累计到阈值才成坑、可熔地面以外不成坑、
    /// 熔坑不可进入（生成时推出、之后绕行、离地不受影响）、上限满不生效、快照段 28～30 往返与旧 20 版快照 = 没有熔坑；
    /// 世界段起真实家园（3 个种子）断言可熔地面 = 生成规则标记（与格网生成地形逐格一致）、熔坑写进寻路镜像、家园上不能建造（原因文本）且已有建筑不受影响、
    /// “清除场地物”填平工作单（预留 / 取消全额返还 / 完工）、上限满提示、真文件存读档与镜像重放、暂停与 0.5x～3x、观察一致。
    /// 冻结桥（FGR-EMG-151）依赖的水面地形未落地，随 FG18-SEA-01（DEBT-EMG4TER01-01），本段只断言其规则行仍为 deferred。
    /// </summary>
    public static class FgEmgTerrainSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static readonly int[] Seeds = { 24101, 24102, 24103 };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

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
            Line("\n[熔坑] 可熔地面累计成坑、不可进入、上限、快照段、家园寻路镜像与建造、填平工作单、存读档、暂停倍速与观察一致");
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
            bool gates = ResearchGate.LegacyGatesOpenForTests;
            ResearchGate.LegacyGatesOpenForTests = true;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgterrain-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckMoltenPit);
                Step(CheckAcidPit);
                Step(CheckBlock);
                Step(CheckCap);
                Step(CheckSnapshot);
                Step(CheckSeeds);
                Step(CheckHomeBuildAndFill);
                Step(CheckBlockWarning);
                Step(CheckWorldSave);
                Step(CheckTimingAndObservation);
            }
            catch (Exception e)
            {
                Fail($"熔坑自检抛异常：{e}");
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
                ResearchGate.LegacyGatesOpenForTests = gates;
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
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                SoftlockService.ResetSessionState();
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
            Line($"  · [熔坑] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────

        private static void Line(string s) => _report.AppendLine(s);

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("    ✗ " + what);
        }

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        private static CombatKernel NewKernel(bool persist = false, int pitMax = 0)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgPersist = (byte)(persist ? 1 : 0);
            if (pitMax > 0)
            {
                c.EmgPitMax = pitMax;
            }
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyContent(k);
            return k;
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int i = 0; i < n; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static void Seconds(CombatKernel k, float s) => StepN(k, Mathf.RoundToInt(s / Dt));

        private static int Unit(CombatKernel k, double2 p, bool air = false) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live,
                Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = 5000f, MaxHealth = 5000f, Weapon = -1,
                BehaviorProfile = air ? k.AddProfile(new CombatBehaviorProfile { Airborne = 1f }) : -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });

        private static double2 PosOf(CombatKernel k, int id)
        {
            k.TryGetUnit(id, out CombatUnitView v);
            return new double2(v.Position.x, v.Position.y);
        }

        private static int MeltKey => NamedReactionCatalog.StableKey("reaction_meltpit");

        private static float MoltenThreshold => Row("reaction_meltpit")?.C1 ?? 30f;
        private static float AcidThreshold => Row("reaction_meltpit_acid")?.C1 ?? 40f;

        private static GameConfig.fg.ChannelRule Row(string id) => ConfigSystem.Instance.Tables.TbChannelRule.DataList.FirstOrDefault(r => r.Id == id);

        private static int NotifyCount(string type) => NotificationCenter.History.Where(e => e.Type?.Id == type).Sum(e => e.Count);

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            NotificationCenter.ResetForTests();
            NotificationCenter.Bind(s);
            FeedbackCues.ResetForTests();
            return s;
        }

        private static CombatSite HomeSite => CombatSites.Get(HomeValleyLayout.RegionId);
        private static CombatKernel HomeKernel => HomeSite.Kernel;

        private static bool MeltableCode(byte code) => GridContent.TerrainByCode(code)?.Meltable == 1;

        /// <summary>离核心 <paramref name="from"/>～<paramref name="to"/> 格、本格与 4 邻格都是可熔地面、能走、没有建筑的格（按到核心距离由近到远，确定性）。</summary>
        private static GridCell? FindGround(CampaignState s, bool meltable, int from = 10, int to = 90)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = from; r <= to; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        foreach (GridCell n in new[] { c, new GridCell(c.X + 1, c.Y), new GridCell(c.X - 1, c.Y), new GridCell(c.X, c.Y + 1), new GridCell(c.X, c.Y - 1) })
                        {
                            byte t = map.GetTerrain(n);
                            ok &= MeltableCode(t) == meltable && (GridContent.TerrainByCode(t)?.NavCost ?? 0) > 0 && map.OccupantAt(n) == null;
                        }
                        if (ok && NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer))
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>在 <paramref name="at"/> 放一块熔融金属，走到它凝成冷渣（世界模拟，真实路径）。</summary>
        private static void Molten(CombatKernel k, double2 at, float amount, Action<int> step)
        {
            k.SpawnPatch(EmgMatterConst.Molten, at, 1f, amount);
            step(Mathf.RoundToInt(14f / Dt));
        }

        // ── A 数据 ───────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 规则行、调参、可熔地面标记");
            EmgRuleCatalog.Compiled c = EmgRuleCatalog.Current;
            int Op(string id) => c.Ids.IndexOf(id) is int i && i >= 0 ? c.Rules[i].Op : -1;
            Expect(Op("reaction_meltpit") == EmgRuleConst.OpMeltPit && Op("reaction_meltpit_acid") == EmgRuleConst.OpMeltPit && Op("reaction_icebridge") == EmgRuleConst.OpDeferred && Op("reaction_scorch") == EmgRuleConst.OpScorch && c.Ok,
                $"A1（V06 {Op("reaction_meltpit")} / V07 {Op("reaction_meltpit_acid")} / V08 {Op("reaction_icebridge")} / V03 {Op("reaction_scorch")}，编译 {c.Ok}）V06 / V07 熔坑改为内核结算（meltpit）；V08 冻结桥仍 deferred（水面未落地，DEBT-EMG4TER01-01 → FG18-SEA-01），V03 焦土由 EMG4-TER-02 接上（scorch）");
            Expect(Math.Abs(MoltenThreshold - 30f) < 1e-4f && Math.Abs(AcidThreshold - 40f) < 1e-4f && GridContent.TuningInt("emg.terrain.pit_max") == 8
                   && GridContent.Tuning("emg.terrain.pit_radius") > 0f && GridContent.Tuning("emg.terrain.pit_gather") > 0f,
                $"A2 阈值与上限来自表：熔融金属 ≥ {MoltenThreshold}、酸 ≥ {AcidThreshold}、每地点 ≤ {GridContent.TuningInt("emg.terrain.pit_max")}、半径 {GridContent.Tuning("emg.terrain.pit_radius")} 米、合并半径 {GridContent.Tuning("emg.terrain.pit_gather")} 米");
            string melts = string.Join(",", GridContent.Terrains.Where(t => t.Meltable == 1).Select(t => t.Id));
            Expect(melts == "ruin" && CombatSite.MeltCodes() == 1ul << GridContent.TerrainCode("ruin"),
                $"A3 可熔地面由地形表标记（fg.TbGridTerrain meltable）：{melts}（废墟里的工业铁板 / 混凝土楼板），内核拿到的地形码位与表一致");
        }

        // ── B V06 熔融金属 ─────────────────────────────────────────────────

        private static void CheckMoltenPit()
        {
            Line("  · B. V06：熔融金属在可熔地面凝固，同一位置累计 ≥ 30 才成坑");
            using CombatKernel k = NewKernel();
            k.SetMeltGround(new List<float3> { new float3(0f, 0f, 6f) });
            int key0 = k.EmgRuleCountOfKey(MeltKey);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 1f, 20f);
            Seconds(k, 14f);
            bool slag = Enumerable.Range(0, k.PatchSlots).Any(i => k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Slag);
            Expect(slag && k.PitCount == 0 && k.PitAccCount == 1, $"B1 负向：份量 20 凝成冷渣后不到阈值，不成坑（累计点 {k.PitAccCount}）");
            k.SpawnPatch(EmgMatterConst.Molten, new double2(1, 0), 1f, 15f);
            Seconds(k, 14f);
            EmgPit p = k.PitAt(0);
            Expect(k.PitCount == 1 && math.distance(p.Pos, new double2(0.43, 0)) < 0.2 && p.Source == EmgPitConst.SourceMolten && k.PitAccCount == 0 && k.PitCounts.Born == 1,
                $"B2 1 米外又凝了 15 份（累计 35 ≥ 30）→ 形成熔坑，落在按份量加权的位置（{p.Pos.x:0.00}, {p.Pos.y:0.00}）、半径 {p.Radius}，累计点结算掉");
            Expect(k.EmgRuleCountOfKey(MeltKey) - key0 == 1, "B3 具名交互“熔坑”计数 +1（图鉴 / 因果链按规则稳定键记，B20）");
            k.SpawnPatch(EmgMatterConst.Molten, new double2(20, 0), 1f, 40f);
            Seconds(k, 14f);
            Expect(k.PitCount == 1 && k.PitCounts.OffGround >= 1, $"B4 负向：可熔地面以外凝 40 份不成坑（地面外 {k.PitCounts.OffGround} 次，不计入）");
            k.SpawnPatch(EmgMatterConst.Molten, new double2(0.5, 0.5), 1f, 40f);
            Seconds(k, 14f);
            Expect(k.PitCount == 1, "B5 已有熔坑的地方不再累计（同一处不叠坑）");
            k.SpawnPatch(EmgMatterConst.Molten, new double2(4, 4), 1f, 32f);
            Seconds(k, 14f);
            Expect(k.PitCount == 2 && k.PitCounts.Born == 2, "B6 另一处一次凝 32 份（≥ 30）直接成坑；两处各自累计、互不并账");
        }

        // ── C V07 酸 ─────────────────────────────────────────────────────

        private static void CheckAcidPit()
        {
            Line("  · C. V07：酸在可熔地面到期，同一位置累计 ≥ 40 才成坑");
            using CombatKernel k = NewKernel();
            k.SetMeltGround(new List<float3> { new float3(0f, 0f, 6f) });
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 1.5f, 30f);
            Seconds(k, 16f);
            Expect(k.PitCount == 0 && k.PitAccCount == 1, "C1 负向：30 份酸到期不到 40，不成坑");
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0.5, 0), 1.5f, 12f);
            Seconds(k, 16f);
            Expect(k.PitCount == 1 && k.PitAt(0).Source == EmgPitConst.SourceAcid, "C2 再到期 12 份（累计 42）→ 熔坑（来源 = 酸）");
            Expect(k.PitAccCount == 0, "C3 熔融金属与酸分开累计：酸的阈值单独判断");
        }

        // ── D 不可进入 ───────────────────────────────────────────────────

        private static void CheckBlock()
        {
            Line("  · D. 熔坑不可进入：生成时推出、之后绕行、离地单位不受影响");
            using CombatKernel k = NewKernel();
            k.SetMeltGround(new List<float3> { new float3(0f, 0f, 6f) });
            int inside = Unit(k, new double2(0.2, 0.3));
            k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 1f, 35f);
            Seconds(k, 14f);
            EmgPit p = k.PitAt(0);
            Expect(k.PitCount == 1 && math.distance(PosOf(k, inside), p.Pos) >= p.Radius, $"D1 熔坑生成时圈内的单位被推到边缘外（离坑心 {math.distance(PosOf(k, inside), p.Pos):0.00} 米 ≥ 半径 {p.Radius}）、没有阵亡");
            int walker = Unit(k, new double2(-8, 0));
            int flyer = Unit(k, new double2(-8, 0.1), air: true);
            k.IssueCommand(walker, CombatCommandKind.Move, new double2(8, 0), 0, 0.5f, 0f, 0f, false);
            k.IssueCommand(flyer, CombatCommandKind.Move, new double2(8, 0.1), 0, 0.5f, 0f, 0f, false);
            bool entered = false, flew = false;
            for (int i = 0; i < 60 * 10; i++)
            {
                StepN(k, 1);
                entered |= math.distance(PosOf(k, walker), p.Pos) < p.Radius;
                flew |= math.distance(PosOf(k, flyer), p.Pos) < p.Radius;
            }
            Expect(!entered && PosOf(k, walker).x > 6.0, $"D2 地面单位穿过去：没有一步踏进坑里（局部绕行），到了对面（x = {PosOf(k, walker).x:0.0}）");
            Expect(flew, "D3 离地（悬浮 / 飞行）单位不受熔坑影响，直线飞过坑口");
            Expect(k.BlockSig != 0u, "D4 熔坑进阻路集合签名（家园寻路镜像据此增量更新）");
            uint sig = k.BlockSig;
            Expect(k.ClearPersist(EmgPersistItem.KindPit, p.Id) && k.PitCount == 0 && k.PitCounts.Filled == 1 && k.BlockSig != sig && !k.ClearPersist(EmgPersistItem.KindPit, p.Id),
                "D5 填平：熔坑移除、计数“填平 1”、签名变化（镜像随之恢复）；再填一次目标已不在（返回 false）");
        }

        // ── E 上限 ───────────────────────────────────────────────────────

        private static void CheckCap()
        {
            Line("  · E. 每地点上限满：不生效并计数");
            using CombatKernel k = NewKernel(pitMax: 2);
            k.SetMeltGround(new List<float3> { new float3(0f, 0f, 40f) });
            for (int i = 0; i < 3; i++)
            {
                k.SpawnPatch(EmgMatterConst.Molten, new double2(i * 10, 0), 1f, 35f);
            }
            Seconds(k, 14f);
            Expect(k.PitCount == 2 && k.PitCounts.Capped == 1 && k.PitAccCount == 0, $"E1 上限 2 时第 3 处不形成熔坑（上限满 {k.PitCounts.Capped} 次，累计点不留着反复触发）");
            using CombatKernel d = NewKernel();
            Expect(GridContent.TuningInt("emg.terrain.pit_max") == 8 && d.Config.EmgPitMax == 8, "E2 正式上限 emg.terrain.pit_max = 8 写进内核配置");
        }

        // ── F 快照段 28～30 ────────────────────────────────────────────────

        private static void CheckSnapshot()
        {
            Line("  · F. 存读档：熔坑与累计点随快照 20 内追加段往返；读不含该段的 20 版快照 = 没有熔坑");
            using CombatKernel a = NewKernel();
            a.SetMeltGround(new List<float3> { new float3(0f, 0f, 30f) });
            byte[] plain = a.Serialize(); // 还没有任何熔坑状态：快照不写熔坑段（与本卡之前逐字相同的形状）
            a.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 1f, 35f);
            a.SpawnPatch(EmgMatterConst.Molten, new double2(12, 0), 1f, 20f);
            Seconds(a, 14f);
            byte[] data = a.Serialize();
            ulong h0 = a.StateHash();
            using CombatKernel b = NewKernel();
            b.SetMeltGround(new List<float3> { new float3(0f, 0f, 30f) });
            CombatLoadResult r = b.Load(data);
            Expect(r == CombatLoadResult.Ok && b.StateHash() == h0 && b.PitCount == 1 && b.PitAccCount == 1 && b.PitAt(0).Id == a.PitAt(0).Id && b.PitCounts == a.PitCounts && b.BlockSig == a.BlockSig,
                $"F1 中途存读档（1 个熔坑 + 1 处累计 20 份）：StateHash 一致，熔坑 / 累计点 / 计数 / 阻路签名往返");
            a.SpawnPatch(EmgMatterConst.Molten, new double2(12, 0), 1f, 15f);
            b.SpawnPatch(EmgMatterConst.Molten, new double2(12, 0), 1f, 15f);
            Seconds(a, 14f);
            Seconds(b, 14f);
            Expect(a.PitCount == 2 && b.PitCount == 2 && a.StateHash() == b.StateHash() && a.PitAt(1).Id == b.PitAt(1).Id, "F2 读档后接着累计：两边同一步形成第 2 个熔坑，哈希一致（发号器接续不重号）");
            using CombatKernel c = NewKernel();
            Expect(c.Load(plain) == CombatLoadResult.Ok && c.PitCount == 0 && c.PitAccCount == 0 && c.PitCounts.Born == 0, "F3 读不含熔坑段的 20 版快照 = 没有熔坑（不升 FormatVersion，只追加段）");
            using CombatKernel d = NewKernel();
            d.Load(data);
            d.Load(plain);
            Expect(d.PitCount == 0 && d.BlockSig == 0u, "F4 有熔坑的内核读回没有熔坑段的旧档：熔坑清掉、阻路签名归零（镜像随之恢复）");
            Expect(CombatConst.FormatVersion == 20, $"F5 快照版本仍是 20（FormatVersion = {CombatConst.FormatVersion}）");
        }

        // ── G 种子无关：可熔地面 = 生成规则标记 ──────────────────────────────────

        private static void CheckSeeds()
        {
            Line("  · G. 任意种子（3 个）：可熔地面由生成规则标记，熔坑只在可熔地面上形成，并写进家园寻路镜像");
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorld(seed);
                CombatKernel k = HomeKernel;
                HomeGridMap map = HomeGridService.MapFor(s);
                GridCell core = HomeGridService.CorePivot(s);
                int agree = 0, total = 0, meltCells = 0;
                for (int dy = -60; dy <= 60; dy += 3)
                {
                    for (int dx = -60; dx <= 60; dx += 3)
                    {
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        bool expect = MeltableCode(map.GetTerrain(c));
                        meltCells += expect ? 1 : 0;
                        agree += k.MeltableAt(new double2(c.X, c.Y)) == expect ? 1 : 0;
                        total++;
                    }
                }
                GridCell? ruin = FindGround(s, true);
                GridCell? plain = FindGround(s, false, 8, 40);
                Expect(agree == total && meltCells > 0 && ruin != null && plain != null,
                    $"G1 种子 {seed}：内核可熔判断与格网生成地形逐格一致（{agree}/{total}，其中可熔 {meltCells} 格），找得到可熔地面 {ruin} 与普通地面 {plain}");
                if (ruin == null || plain == null)
                {
                    continue;
                }
                GridCell g = ruin.Value;
                bool passBefore = NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer);
                GridCell probe = new[] { new GridCell(g.X + 4, g.Y), new GridCell(g.X - 4, g.Y), new GridCell(g.X, g.Y + 4), new GridCell(g.X, g.Y - 4) }
                    .FirstOrDefault(q => NavService.PassableNow(q.X, q.Y, NavConst.ClassPlayer)); // 坑外 4 格、原本就能走的一格（对照）
                Molten(k, new double2(g.X, g.Y), 35f, n => WorldSimulation.StepMany(n));
                Molten(k, new double2(plain.Value.X, plain.Value.Y), 35f, n => WorldSimulation.StepMany(n));
                WorldSimulation.StepMany(2);
                bool onRuin = k.PitCount == 1 && math.distance(k.PitAt(0).Pos, new double2(g.X, g.Y)) < 0.5;
                bool passAfter = NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer), edge = NavService.PassableNow(probe.X, probe.Y, NavConst.ClassPlayer);
                Expect(onRuin && passBefore && !passAfter && edge,
                    $"G2（坑 {k.PitCount}、坑心 {(k.PitCount > 0 ? k.PitAt(0).Pos.ToString() : "-")}、原可走 {passBefore}、成坑后可走 {passAfter}、4 格外 {edge}、地面外 {k.PitCounts.OffGround}）种子 {seed}：废墟上凝 35 份成坑、普通地面上凝 35 份不成坑；坑心格进寻路镜像不可走（ADR ⑯ 增量更新），4 格外照常");
                // 修复轮 1（审查 P1）：拆完的废墟（回收站 DepleteCell 同一个写入口 SetTerrain → buildable）不再是可熔地面
                GridCell core0 = HomeGridService.CorePivot(s);
                GridCell? gone = FindGround(s, true, Math.Max(Math.Abs(g.X - core0.X), Math.Abs(g.Y - core0.Y)) + 6);
                if (gone == null)
                {
                    Fail($"G3 种子 {seed}：找不到第二块可熔地面");
                }
                else
                {
                    GridCell d = gone.Value;
                    bool meltBefore = k.MeltableAt(new double2(d.X, d.Y));
                    map.SetTerrain(d, GridContent.TerrainCode("buildable"));
                    WorldSimulation.StepMany(1);
                    int pits = k.PitCount, off = k.PitCounts.OffGround;
                    Molten(k, new double2(d.X, d.Y), 35f, n => WorldSimulation.StepMany(n));
                    Expect(meltBefore && !k.MeltableAt(new double2(d.X, d.Y)) && k.PitCount == pits && k.PitCounts.OffGround > off
                           && k.MeltableAt(new double2(d.X + 1, d.Y)) == MeltableCode(map.GetTerrain(new GridCell(d.X + 1, d.Y))),
                        $"G3 种子 {seed}：拆完的废墟格（{d}）变空地后不再可熔：凝 35 份不成坑、计入可熔地面以外（{off} → {k.PitCounts.OffGround}），相邻没改的格照旧按生成规则");
                }
            }
        }

        // ── H 家园：建造、填平工作单、上限提示 ─────────────────────────────────────

        private static void ClickClear(CampaignState s, GridCell at)
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            mode.Open();
            mode.SetClearSiteMode(true);
            mode.PointerDown(s, at);
            mode.PointerUp(s, at);
            mode.SetClearSiteMode(false);
            mode.Close();
        }

        private static void CheckHomeBuildAndFill()
        {
            Line("  · H. 家园：熔坑格不能建造（原因文本）、已有建筑不受影响、“清除场地物”填平（预留 / 取消返还 / 完工）、堵路口径、悬停、上限提示");
            CampaignState s = NewWorld(Seeds[0], scrap: 100);
            CombatKernel k = HomeKernel;
            GridCell? ruin = FindGround(s, true);
            if (ruin == null)
            {
                Fail("H 找不到可熔地面");
                return;
            }
            GridCell g = ruin.Value;
            GridCell? side = FindGround(s, true, Math.Max(10, Math.Max(Math.Abs(g.X - HomeGridService.CorePivot(s).X), Math.Abs(g.Y - HomeGridService.CorePivot(s).Y)) + 6));
            BuildingRecord existing = side != null ? FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "pit-store", side.Value) : null;
            GridCell? plainSpot = FindGround(s, false, 8, 40);
            Molten(k, new double2(g.X, g.Y), 35f, n => WorldSimulation.StepMany(n));
            if (existing != null)
            {
                Molten(k, new double2(side.Value.X, side.Value.Y), 35f, n => WorldSimulation.StepMany(n));
            }
            WorldSimulation.StepMany(2);
            EmgPit pit = Enumerable.Range(0, k.PitCount).Select(k.PitAt).FirstOrDefault(p => math.distance(p.Pos, new double2(g.X, g.Y)) < 0.5);
            GridPlacementResult place = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, g, 0);
            string why = place.Reasons.Where(r => r.Code == GridBlockReason.MeltPit).Select(r => r.Describe()).FirstOrDefault() ?? string.Empty;
            Expect(pit.Id > 0 && place.Has(GridBlockReason.MeltPit) && why.Contains("熔坑") && why.Contains("清除场地物"),
                $"H1 熔坑上的空格不能建造，原因文本写明怎么办（“{why}”）");
            // 修复轮 1（审查 P1）：传送带与管线件走自己的逐格校验，同样拦、同一条原因
            GridPlacementResult belt = HomeGridService.ValidateBeltCell(s, g);
            GridPlacementResult pipe = HomeGridService.ValidatePipeCell(s, g, BinGames.Sim.Logistics.PipePieceKind.Pipe);
            string beltWhy = belt.Reasons.Where(r => r.Code == GridBlockReason.MeltPit).Select(r => r.Describe()).FirstOrDefault();
            string pipeWhy = pipe.Reasons.Where(r => r.Code == GridBlockReason.MeltPit).Select(r => r.Describe()).FirstOrDefault();
            Expect(!belt.Ok && !pipe.Ok && beltWhy == why && pipeWhy == why, $"H1b 熔坑格铺传送带 / 管线被拒，原因文本与建筑相同（带“{beltWhy}”、管“{pipeWhy}”）");
            Expect(existing == null || (HomeGridService.FindBuilding(s, existing.BuildingId) != null && k.PitCount == 2),
                $"H2 熔坑出现在已有建筑格：建筑不受影响（{(existing == null ? "附近找不到第二块可熔地面，跳过" : "仓库仍在")}）");
            var items = new List<EmgPersistItem>();
            k.CollectPersist(items);
            Expect(items.Any(i => i.Kind == EmgPersistItem.KindPit && i.Id == pit.Id && i.Blocks), "H3 熔坑进长期留存物清单且算阻路（堵路警告泛洪与固态斑块同一口径，FGR-EMG-154）");
            TooltipContent tip = EmgChainView.PitContent(HomeSite, s, pit.Id);
            Expect(tip != null && tip.Title == "熔坑" && tip.Body.Contains("不能建造") && tip.Body.Contains("清除场地物"), $"H4 悬停：熔坑是什么、为什么过不去 / 不能建、怎么填平（“{tip?.Body}”）");
            string target = SitePersistService.TargetOf(EmgPersistItem.KindPit, pit.Id);
            int before = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            ClickClear(s, g);
            WorkOrderRecord o = SitePersistService.ActiveOrder(s, target);
            int reserved = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            int orders = items.Count(i => SitePersistService.ActiveOrder(s, SitePersistService.TargetOf(i.Kind, i.Id)) != null);
            Expect(o != null && o.Kind == WorkOrderKind.Salvage && before - reserved == SitePersistService.ClearScrap * orders && HomeValleyWorkOrders.DescribeTarget(s, o).Contains("熔坑"),
                $"H5 建造模式“清除场地物”点熔坑 → 开填平工作单（复用清除工作单；盖在坑上的冷渣同格一并下单，共 {orders} 张），预留废料 {before} → {reserved}，工单目标写“{HomeValleyWorkOrders.DescribeTarget(s, o)}”");
            int beforeCancel = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            ClickClear(s, g); // 修复轮 1（审查 P1）：走正式入口“再点一次取消”，同格的熔坑 + 冷渣两张单一起退
            int refunded = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            int left = items.Count(i => SitePersistService.ActiveOrder(s, SitePersistService.TargetOf(i.Kind, i.Id)) != null);
            Expect(orders >= 1 && left == 0 && SitePersistService.ActiveOrder(s, target) == null && refunded - beforeCancel == SitePersistService.ClearScrap * orders && k.PitCount >= 1,
                $"H6 B03 执行前在同一格再点一次“清除场地物”：{orders} 张单全部取消（剩 {left}），全额返还（{beforeCancel} → {refunded}），熔坑还在");
            SitePersistService.OrderClear(s, target);
            bool filled = FgProductionSelfCheck.StepUntil(() => !k.TryGetPersist(EmgPersistItem.KindPit, pit.Id, out _), 300);
            WorldSimulation.StepMany(3);
            Expect(filled && k.PitCounts.Filled == 1 && NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer) && !HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, g, 0).Has(GridBlockReason.MeltPit),
                "H7 空闲机器领单到现场填平：熔坑消失、寻路镜像恢复可走、这一格又能建造");
            // 上限满提示（正式上限 8，用真实路径凝满 9 处）
            int notes = NotifyCount("pit_capped");
            GridCell core = HomeGridService.CorePivot(s);
            var spots = new List<GridCell>();
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int r = 10; r <= 120 && spots.Count < 12; r++)
            {
                for (int dx = -r; dx <= r && spots.Count < 12; dx += 2)
                {
                    foreach (int dy in new[] { -r, r })
                    {
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (MeltableCode(map.GetTerrain(c)) && spots.All(q => Math.Abs(q.X - c.X) + Math.Abs(q.Y - c.Y) > 8))
                        {
                            spots.Add(c);
                        }
                    }
                }
            }
            foreach (GridCell c in spots)
            {
                k.SpawnPatch(EmgMatterConst.Molten, new double2(c.X, c.Y), 1f, 35f);
            }
            WorldSimulation.StepMany(Mathf.RoundToInt(16f / Dt));
            Expect(spots.Count >= 9 + 1 && k.PitCount == 8 && k.PitCounts.Capped >= 1 && NotifyCount("pit_capped") > notes,
                $"H8 正式上限 8：{spots.Count} 处凝满后熔坑 {k.PitCount} 个，多出的不形成（{k.PitCounts.Capped} 次）并发一条信息级提示（“熔坑已到上限”）");
            // 修复轮 1（审查 P1）：搬迁 / 挪规划（asPlayerPlacement = false）也不能落到熔坑上（另建一座完好的仓库来搬：上面那座压在熔坑上已被烫坏，受损建筑本来就不能搬）
            EmgPit far = Enumerable.Range(0, k.PitCount).Select(k.PitAt).FirstOrDefault(p => side == null || math.distance(p.Pos, new double2(side.Value.X, side.Value.Y)) > 8);
            GridCell pc = NavService.CellOf(far.Pos.x, far.Pos.y);
            GridCell? Spot(string ignore, GridCell? avoid)
            {
                for (int r = 0; r <= 16 && plainSpot != null; r++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            var c = new GridCell(plainSpot.Value.X + dx, plainSpot.Value.Y + dy);
                            if ((avoid == null || Math.Max(Math.Abs(c.X - avoid.Value.X), Math.Abs(c.Y - avoid.Value.Y)) > 4)
                                && HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, c, 0, asPlayerPlacement: false, ignoreBuildingId: ignore, checkCost: false).Ok)
                            {
                                return c;
                            }
                        }
                    }
                }
                return null;
            }
            GridCell? home = Spot(null, null);
            BuildingRecord mover = home != null ? FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "pit-mover", home.Value) : null;
            GridOpResult? direct = mover != null ? HomeGridService.TryRelocate(s, mover.BuildingId, pc, 0) : (GridOpResult?)null;
            Expect(far.Id > 0 && direct != null && !direct.Value.Success && direct.Value.Placement != null && direct.Value.Placement.Has(GridBlockReason.MeltPit),
                $"H9 已建成的仓库（{home}）搬到熔坑格（{pc}）被拒，原因含熔坑（“{direct?.Describe()}”）");
            GridCell? okSpot = mover != null ? Spot(mover.BuildingId, home) : null;
            GridOpResult? planned = okSpot != null ? HomeGridService.TryRelocate(s, mover.BuildingId, okSpot.Value, 0) : (GridOpResult?)null;
            GridOpResult? moved = planned?.Success == true ? HomeGridService.TryRelocate(s, mover.BuildingId, pc, 0) : (GridOpResult?)null;
            Expect(planned?.Success == true && moved != null && !moved.Value.Success && moved.Value.Placement != null && moved.Value.Placement.Has(GridBlockReason.MeltPit),
                $"H10 先规划搬到合法位置（{okSpot}，{planned?.Outcome}），再把规划拖到熔坑格被拒（“{moved?.Describe()}”）");
        }

        // ── K 堵路警告（B11）：熔坑补上胶块围墙的唯一缺口 ─────────────────────────────

        private static void CheckBlockWarning()
        {
            Line("  · K. B11：熔坑堵死核心到仓库的唯一通路 → 警告级通知写明是熔坑、带位置、告警栏可定位；填平后解除");
            CampaignState s = NewWorld(Seeds[2], scrap: 200);
            CombatKernel k = HomeKernel;
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? ruin = FindGround(s, true, 12, 90);
            if (ruin == null)
            {
                Fail("K 找不到可熔地面");
                return;
            }
            GridCell g = ruin.Value;
            // 仓库放在熔坑位置背离核心 5 格处；以仓库为心、半径 5 围一圈固化胶块，只在离核心最近的那一点（= 可熔地面 g）留缺口——
            // 这样熔坑补上缺口后，它就是“两侧都碰得到、离核心最近”的那件（PickBlocker 规则），警告应当点名熔坑
            double2 dir = math.normalizesafe(new double2(g.X - core.X, g.Y - core.Y), new double2(1, 0));
            var c = new GridCell((int)Math.Round(g.X + 5 * dir.x), (int)Math.Round(g.Y + 5 * dir.y));
            BuildingRecord store = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "pit-store", c);
            WorldSimulation.StepMany(5);
            for (int i = 0; i < 22; i++)
            {
                double ang = i * Math.PI * 2 / 22;
                var q = new double2(c.X + 5 * Math.Cos(ang), c.Y + 5 * Math.Sin(ang));
                if (math.distance(q, new double2(g.X, g.Y)) > 2.6)
                {
                    k.SpawnPatch(EmgMatterConst.Cured, q, 1.5f);
                }
            }
            WorldSimulation.StepMany(240);
            SitePersistService.Check(s);
            SoftlockState st = SoftlockService.StateOf(s);
            bool openBefore = st.PersistBlocks.All(r => r.Key != store.BuildingId);
            int notes = NotifyCount(SitePersistService.NotifyBlocked);
            Molten(k, new double2(g.X, g.Y), 35f, n => WorldSimulation.StepMany(n));
            WorldSimulation.StepMany(3);
            SitePersistService.Check(s);
            PersistBlockRecord rec = st.PersistBlocks.FirstOrDefault(r => r.Key == store.BuildingId);
            string last = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == SitePersistService.NotifyBlocked)?.Text ?? string.Empty;
            HomeValleyAlarms.AlertRecord alarm = HomeValleyAlarms.Collect(s).FirstOrDefault(a => a.Key == "persist:" + store.BuildingId);
            Expect(openBefore && k.PitCount == 1 && rec != null && rec.Kind == EmgPersistItem.KindPit && rec.Blocks && NotifyCount(SitePersistService.NotifyBlocked) > notes
                   && last.Contains("熔坑") && alarm.Key != null && alarm.HasLocation,
                $"K1 缺口开着时不报；熔坑补上缺口 → 警告“{last}”、记录种类 = 熔坑、位置（{rec?.X:0.0},{rec?.Y:0.0}）、告警栏可定位");
            // EMG4-TER-02：熔坑同时写进家园格网（熔坑地形不可通行），填平走正式完工入口（内核熔坑与格网熔坑一起填平），不再直接删内核熔坑
            SitePersistService.TryClearTarget(s, SitePersistService.TargetOf(EmgPersistItem.KindPit, k.PitAt(0).Id));
            WorldSimulation.StepMany(3);
            SitePersistService.Check(s);
            Expect(st.PersistBlocks.All(r => r.Key != store.BuildingId), "K2 填平熔坑（完工入口：内核熔坑 + 格网熔坑一起填平）后下一次检查警告解除（缺口重新打通）");
        }

        // ── I 真文件存读档 ─────────────────────────────────────────────────

        private static string Digest(CampaignState s)
        {
            CombatKernel k = HomeKernel;
            string pits = string.Join(",", Enumerable.Range(0, k.PitCount).Select(i => $"{k.PitAt(i).Id}@{k.PitAt(i).Pos.x:0.00},{k.PitAt(i).Pos.y:0.00}"));
            return $"pits:{pits}|acc:{k.PitAccCount}|counts:{k.PitCounts}";
        }

        private static void CheckWorldSave()
        {
            Line("  · I. 真文件存读档：熔坑与累计点往返，读档后第一步重放进寻路镜像");
            CampaignState s = NewWorld(Seeds[1], scrap: 100);
            CombatKernel k = HomeKernel;
            GridCell? ruin = FindGround(s, true);
            if (ruin == null)
            {
                Fail("I 找不到可熔地面");
                return;
            }
            GridCell g = ruin.Value;
            Molten(k, new double2(g.X, g.Y), 35f, n => WorldSimulation.StepMany(n));
            GridCell? second = FindGround(s, true, Math.Max(Math.Abs(g.X - HomeGridService.CorePivot(s).X), Math.Abs(g.Y - HomeGridService.CorePivot(s).Y)) + 6);
            if (second != null)
            {
                Molten(k, new double2(second.Value.X, second.Value.Y), 18f, n => WorldSimulation.StepMany(n)); // 半程累计
            }
            WorldSimulation.SyncAllForSave();
            string before = Digest(s);
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("I 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NotificationCenter.Bind(rr.State);
            string loaded = Digest(rr.State);
            WorldSimulation.StepMany(2);
            Expect(save.Success && loaded == before && HomeKernel.PitCount >= 1 && (second == null || HomeKernel.PitAccCount >= 1),
                $"I1 存读档熔坑与半程累计一致（{before}）");
            Expect(!NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer), "I2 读档后第一步重放：熔坑覆盖格回到寻路镜像（覆盖格不进存档，由快照推出）");
            if (second != null)
            {
                Molten(HomeKernel, new double2(second.Value.X, second.Value.Y), 15f, n => WorldSimulation.StepMany(n));
                Expect(HomeKernel.PitCount == 2, "I3 读档后同一处再凝 15 份（累计 33）→ 接着成坑");
            }
        }

        // ── J 暂停、倍速、观察一致 ─────────────────────────────────────────

        private static string Scenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe, 100);
            pausedHeld = true;
            CombatKernel k = HomeKernel;
            GridCell g = FindGround(s, true) ?? HomeGridService.CorePivot(s);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(g.X, g.Y), 1f, 20f);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(g.X + 1, g.Y), 1f, 15f);
            WorldSimulation.StepMany(2);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Digest(s) + "|t:" + GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Digest(s) + "|t:" + GameClock.Ticks == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 20;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Digest(s);
        }

        private static void CheckTimingAndObservation()
        {
            Line("  · J. 暂停与 0.5x～3x、家园不被观察时结果一致");
            string reference = null;
            bool paused = true, same = true;
            foreach (float speed in new[] { 0.5f, 1f, 3f })
            {
                string d = Scenario(Seeds[2], true, speed, true, out bool held);
                paused &= held;
                reference ??= d;
                same &= d == reference;
            }
            Expect(paused && same && reference.Contains("pits:") && !reference.StartsWith("pits:|"),
                $"J1 暂停 120 帧熔融金属不凝、不成坑；0.5x / 1x / 3x 跑同样 20 游戏秒结果逐字一致（{reference}）");
            string seen = Scenario(Seeds[2], true, 1f, false, out _);
            string unseen = Scenario(Seeds[2], false, 1f, false, out _);
            Expect(seen == unseen, $"J2 B24 家园不被观察时结果一致（{unseen}）");
        }
    }
}
