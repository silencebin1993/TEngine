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
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
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
    /// EMG4-TER-02 战痕写入家园格网（FG19 FGR-EMG-153、154，FG19B V03，ADR-EMG-001 ⑩；FGT-EMG-060 战痕部分、FGT-EMG-041 迁移部分）：
    /// 内核段起真实 CombatKernel 断言：家园明火“同一位置”累计燃烧 ≥ 20 秒才发焦土地面标记、远征地点不发、长期留存玻化层 / 冰发玻化地面 / 冻土、熔坑成坑同时发、
    /// 地面标记走玩法队列每步 ≤ 64 条顺延不丢、焦土圈里点燃阈值下调、快照段 31 往返；
    /// 世界段起真实家园断言：格网地面材质按格写入（只盖空地 / 可熔地面，不碰建筑）、同一格反复标记不增处、≤ 64 处超出合并、悬停、“清除场地物”下单 / 取消返还 / 完工恢复空地、
    /// 熔坑战痕与内核熔坑同填平、真文件存读档还原（格网差异 + 战痕 + 内核焦土圈 + 熔坑不可通行）、旧档表外地形字节按生成值处理并记 Error、暂停与 0.5x～3x、观察一致。
    /// </summary>
    public static class FgEmgGroundMarkSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static readonly int[] Seeds = { 24201, 24202, 24203 };

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
            Line("\n[战痕] 家园焦土 / 玻化地面 / 冻土 / 熔坑写进格网差异存档、64 / 步顺延、≤ 64 处合并、悬停、清除工作单、存读档还原、旧档表外字节、暂停倍速与观察一致");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgmark-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Step(CheckKernelScorch);
                Step(CheckKernelSolidAndBudget);
                Step(CheckIgnite);
                Step(CheckSnapshot);
                Step(CheckHomeWrite);
                Step(CheckMergeCap);
                Step(CheckUnregistered);
                Step(CheckPitBlocks);
                Step(CheckWorldSave);
                Step(CheckTimingAndObservation);
            }
            catch (Exception e)
            {
                Fail($"战痕自检抛异常：{e}");
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
            Line($"  · [战痕] 断言通过 {_pass}，失败 {_fail}");
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

        private static CombatKernel NewKernel(bool persist)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgPersist = (byte)(persist ? 1 : 0);
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyContent(k);
            return k;
        }

        private static void Seconds(CombatKernel k, float s)
        {
            int n = Mathf.RoundToInt(s / Dt);
            for (int i = 0; i < n; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static void WorldSeconds(float s) => WorldSimulation.StepMany(Mathf.RoundToInt(GameClock.StepHz * s));

        /// <summary>取走内核积着的玩法事件里的地面标记（种类, 位置），其余丢掉。</summary>
        private static List<(byte Kind, double2 Pos, float R)> DrainMarks(CombatKernel k, int max = 64)
        {
            var list = new List<(byte, double2, float)>();
            CombatEvent[] ev = k.DrainGameplay(max, out int n);
            for (int i = 0; i < n; i++)
            {
                if (ev[i].Kind == CombatEventKind.GroundMark)
                {
                    list.Add((ev[i].Code, ev[i].Pos, ev[i].Value));
                }
            }
            return list;
        }

        private static byte Code(string id) => GridContent.TerrainCode(id);

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

        /// <summary>离核心 from～to 格、半径 3 格内都是 <paramref name="terrain"/> 码、能走、没有建筑的格（按到核心距离由近到远，确定性）；<paramref name="avoid"/> 里的格 8 格内不取。</summary>
        private static GridCell? FindSpot(CampaignState s, Func<byte, bool> terrain, int from = 10, int to = 90, List<GridCell> avoid = null, int reach = 3)
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
                        if (avoid != null && avoid.Any(a => Math.Max(Math.Abs(a.X - c.X), Math.Abs(a.Y - c.Y)) < 9))
                        {
                            continue;
                        }
                        bool ok = true;
                        for (int oy = -reach; oy <= reach && ok; oy++)
                        {
                            for (int ox = -reach; ox <= reach && ok; ox++)
                            {
                                var n = new GridCell(c.X + ox, c.Y + oy);
                                ok &= terrain(map.GetTerrain(n)) && map.OccupantAt(n) == null;
                            }
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

        private static bool Open(byte t) => t == 0;

        private static bool Meltable(byte t) => GridContent.TerrainByCode(t)?.Meltable == 1;

        /// <summary>两块明火先后在同一处烧完（各 18 秒 &lt; 20 秒，合计 36 秒 ≥ 20 秒）：只有第二块到期时才成焦土。</summary>
        private static void BurnTwice(CombatKernel k, double2 at, Action<float> run, Action between = null)
        {
            k.SpawnPatch(EmgMatterConst.Fire, at, 1.5f, 40f);
            run(19.5f);
            between?.Invoke();
            k.SpawnPatch(EmgMatterConst.Fire, at, 1.5f, 40f);
            run(19.5f);
        }

        private static string Marks(CampaignState s) =>
            string.Join(";", (s.World.GroundMarks ?? Array.Empty<GroundMarkRecord>()).Select(r => $"{r.Id}:{r.Kind}@{r.X:0.00},{r.Y:0.00}r{r.Radius:0.00}x{r.Hits}"));

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

        // ── A 数据 ───────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 地形层取值表登记、规则行、调参");
            string[] ids = { "scorched", "glazed", "frozen", "meltpit" };
            var rows = ids.Select(id => GridContent.TryTerrainCode(id, out byte c) ? GridContent.TerrainByCode(c) : null).ToArray();
            Expect(rows.All(r => r != null) && rows.Select(r => r.Code).SequenceEqual(new[] { 7, 8, 9, 10 })
                   && rows.Take(3).All(r => r.Buildable == 1 && r.NavCost == 1 && r.Meltable == 0) && rows[3].Buildable == 0 && rows[3].NavCost == 0
                   && rows.All(r => GameText.Get(r.NameKey) != r.NameKey),
                $"A1 四种战痕地面材质登记进 fg.TbGridTerrain（码 {string.Join("/", rows.Select(r => r?.Code))}，只追加在 0～6 之后）：焦土 / 玻化地面 / 冻土可建可走，熔坑不可建不可走，名称有文本");
            EmgRuleCatalog.Compiled c = EmgRuleCatalog.Current;
            int i = c.Ids.IndexOf("reaction_scorch");
            Expect(c.Ok && i >= 0 && c.Rules[i].Op == EmgRuleConst.OpScorch && c.Rules[i].Cond == EmgRuleConst.CondHomeBurn && Math.Abs(c.Rules[i].C1 - 20f) < 1e-4f,
                $"A2 V03 焦土接上内核结算（op scorch、cond home_burn、阈值 {(i >= 0 ? c.Rules[i].C1 : -1)} 秒）");
            Expect(GridContent.TuningInt("emg.terrain.marks_max") == 64 && Math.Abs(GridContent.Tuning("emg.terrain.scorch_ignite") - 40f) < 1e-4f && CombatSite.ConfigFromTuning().EmgScorchIgnite == 40f,
                $"A3 每地点 ≤ {GridContent.TuningInt("emg.terrain.marks_max")} 处、焦土点燃阈值下调 {GridContent.Tuning("emg.terrain.scorch_ignite")} 度（来自表，内核配置同值）");
            Expect(CombatConst.FormatVersion == 20, $"A4 快照版本仍是 20（段 31 只追加，FormatVersion = {CombatConst.FormatVersion}）");
        }

        // ── B 内核：V03 焦土累计 ──────────────────────────────────────────────

        private static void CheckKernelScorch()
        {
            Line("  · B. 内核 V03：家园明火同一位置累计燃烧 ≥ 20 秒才成焦土；远征不发");
            using CombatKernel k = NewKernel(true);
            var at = new double2(0, 0);
            k.SpawnPatch(EmgMatterConst.Fire, at, 1.5f, 40f);
            Seconds(k, 19.5f);
            List<(byte Kind, double2 Pos, float R)> first = DrainMarks(k);
            Expect(first.Count == 0 && k.ScorchAccCount == 1 && k.GroundMarkCounts.ScorchBorn == 0, $"B1 一块明火烧 18 秒到期（< 20 秒）：不成焦土，只记 1 处累计（{k.ScorchAccCount}）");
            k.SpawnPatch(EmgMatterConst.Fire, at + new double2(1.2, 0), 1.5f, 40f);
            Seconds(k, 19.5f);
            List<(byte Kind, double2 Pos, float R)> second = DrainMarks(k);
            Expect(second.Count == 1 && second[0].Kind == EmgGroundMarkConst.KindScorch && math.distance(second[0].Pos, at) < 1.5 && k.ScorchAccCount == 0 && k.GroundMarkCounts.ScorchBorn == 1
                   && k.EmgRuleCountOfKey(NamedReactionCatalog.StableKey("reaction_scorch")) >= 1,
                $"B2 合并半径内再烧一块（累计 36 秒 ≥ 20）：发 1 条焦土地面标记（位置按燃烧秒加权 {second.FirstOrDefault().Pos}、半径 {second.FirstOrDefault().R:0.0}），累计点结算掉，焦土反应计数 +1");
            k.SpawnPatch(EmgMatterConst.Fire, new double2(30, 0), 1.5f, 40f);
            Seconds(k, 19.5f);
            Expect(DrainMarks(k).Count == 0 && k.ScorchAccCount == 1, "B3 远处另一块（不在合并半径内）单独累计，不成焦土");
            using CombatKernel away = NewKernel(false);
            BurnTwice(away, at, s => Seconds(away, s));
            Expect(DrainMarks(away).Count == 0 && away.ScorchAccCount == 0 && away.GroundMarkCounts.Marks == 0,
                "B4 远征 / 室内地点（不由家园格网承载，FG19 第 14 节 #15）：同样烧 36 秒不累计、不发地面标记");
        }

        // ── C 内核：玻化 / 冻土 / 熔坑、64 / 步顺延 ────────────────────────────

        private static void CheckKernelSolidAndBudget()
        {
            Line("  · C. 内核：长期留存玻化层 / 冰 → 玻化地面 / 冻土；地面标记每步 ≤ 64 条、顺延不丢");
            using CombatKernel k = NewKernel(true);
            k.SpawnPatch(EmgMatterConst.Glass, new double2(0, 0), 1.5f, 10f);
            k.SpawnPatch(EmgMatterConst.Ice, new double2(20, 0), 1.5f, 10f);
            Seconds(k, 0.2f);
            List<(byte Kind, double2 Pos, float R)> m = DrainMarks(k);
            Expect(m.Count == 2 && m.Any(x => x.Kind == EmgGroundMarkConst.KindGlazed && math.distance(x.Pos, new double2(0, 0)) < 0.1) && m.Any(x => x.Kind == EmgGroundMarkConst.KindFrozen),
                $"C1 家园生成长期留存的玻化层与冰：各发 1 条（玻化地面 / 冻土），共 {m.Count}");
            using CombatKernel b = NewKernel(true);
            for (int i = 0; i < 70; i++)
            {
                b.SpawnPatch(EmgMatterConst.Glass, new double2(i * 4, 50), 1f, 5f);
            }
            Seconds(b, 2f);
            int pending = b.GameplayPending;
            int got1 = DrainMarks(b, 64).Count;
            int left = b.GameplayPending;
            int got2 = DrainMarks(b, 64).Count;
            Expect(b.GroundMarkCounts.Marks == 70 && got1 + got2 == 70 && got1 <= 64 && left >= 6 && b.GameplayPending == 0,
                $"C2 一口气 70 块玻化层：发 {b.GroundMarkCounts.Marks} 条地面标记，热更层每步取 64（第一步拿 {got1}、留 {left}，下一步拿 {got2}），一条不丢（排队 {pending}）");
        }

        // ── D 内核：焦土上点燃阈值下调 ─────────────────────────────────────────

        private static void CheckIgnite()
        {
            Line("  · D. V03：焦土圈里可燃斑块点燃阈值下调 40 度（圈外不变）");
            using CombatKernel k = NewKernel(true);
            var inside = new double2(0, 0);
            var outside = new double2(40, 0);
            k.SetScorchGround(new List<float3> { new float3(0f, 0f, 3f) });
            k.SpawnPatch(EmgMatterConst.Oil, inside, 1.5f, 10f);
            k.SpawnPatch(EmgMatterConst.Oil, outside, 1.5f, 10f);
            Seconds(k, 0.1f);
            // 先用一小份热量量出温升系数（注热 ΔT = 量 × 系数 ÷ 份量，FG19B §1.4 第 4 条），再一次加到 160 度：在 140～180 之间（只有焦土圈里够点燃）
            float Temp(double2 p) => k.PickPatch(p) is int ix && ix >= 0 && k.TryGetPatch(ix, out CombatZone z) ? z.Temp : float.NaN;
            byte MatterAt(double2 p) => k.PickPatch(p) is int ix && ix >= 0 && k.TryGetPatch(ix, out CombatZone z) ? z.Matter : (byte)0;
            float t0 = Temp(inside);
            k.InjectPatchEnergy(inside, 0.5f, 501, 1f);
            k.InjectPatchEnergy(outside, 0.5f, 501, 1f);
            float per = Temp(inside) - t0;
            float need = per > 1e-4f ? (160f - Temp(inside)) / per : 0f;
            k.InjectPatchEnergy(inside, 0.5f, 501, need);
            k.InjectPatchEnergy(outside, 0.5f, 501, need);
            float lastIn = Temp(inside), lastOut = Temp(outside);
            Seconds(k, 0.6f);
            bool litIn = MatterAt(inside) == EmgMatterConst.Fire, litOut = MatterAt(outside) == EmgMatterConst.Fire;
            Expect(k.ScorchedAt(inside) && !k.ScorchedAt(outside) && litIn && !litOut && lastIn < 180f && lastIn >= 140f - 1f && lastOut < 180f,
                $"D1 两块油同样加热：焦土圈里的在 {lastIn:0} 度（< 180、≥ 140）点燃，圈外的 {lastOut:0} 度仍是油");
            using CombatKernel none = NewKernel(true);
            Expect(none.ScorchGroundCount == 0 && !none.ScorchedAt(inside), "D2 没有焦土圈时判断恒假（点燃阈值逐字不变）");
        }

        // ── E 快照段 31 ─────────────────────────────────────────────────

        private static void CheckSnapshot()
        {
            Line("  · E. 存读档：焦土累计点随快照段 31、计数随段 30 尾部往返；读不含这些段的 20 版快照 = 没有累计");
            using CombatKernel a = NewKernel(true);
            byte[] plain = a.Serialize();
            a.SpawnPatch(EmgMatterConst.Fire, new double2(5, 5), 1.5f, 40f);
            a.SpawnPatch(EmgMatterConst.Glass, new double2(20, 5), 1.5f, 10f);
            Seconds(a, 19.5f);
            DrainMarks(a);
            byte[] data = a.Serialize();
            using CombatKernel b = NewKernel(true);
            CombatLoadResult r = b.Load(data);
            Expect(r == CombatLoadResult.Ok && b.ScorchAccCount == 1 && b.GroundMarkCounts == a.GroundMarkCounts && a.GroundMarkCounts.Marks == 1,
                $"E1 中途存读档（半程累计 18 秒 + 1 次玻化地面）：累计点 {b.ScorchAccCount}、计数 {b.GroundMarkCounts} 往返一致");
            a.SpawnPatch(EmgMatterConst.Fire, new double2(5, 5), 1.5f, 40f);
            b.SpawnPatch(EmgMatterConst.Fire, new double2(5, 5), 1.5f, 40f);
            Seconds(a, 19.5f);
            Seconds(b, 19.5f);
            List<(byte Kind, double2 Pos, float R)> ma = DrainMarks(a), mb = DrainMarks(b);
            Expect(ma.Count == 1 && mb.Count == 1 && ma[0].Kind == EmgGroundMarkConst.KindScorch && math.distance(ma[0].Pos, mb[0].Pos) < 1e-6 && a.StateHash() == b.StateHash(),
                "E2 读档后同一处接着烧：两边同时成焦土、位置一致、哈希一致");
            using CombatKernel c = NewKernel(true);
            Expect(c.Load(plain) == CombatLoadResult.Ok && c.ScorchAccCount == 0 && c.GroundMarkCounts.Marks == 0, "E3 读没有地形段的 20 版快照：没有累计、计数为 0（旧档 FGT-EMG-041）");
            // 修复轮 1（ADR 短段条款）：EMG4-TER-01 写出的快照 = 段 28～30、段 30 只有 5 个字段、没有段 31
            byte[] ter01 = a.SerializeTer01ShapeForTests();
            using CombatKernel d = NewKernel(true);
            CombatLoadResult rd = d.Load(ter01);
            Expect(rd == CombatLoadResult.Ok && a.GroundMarkCounts.Marks > 0 && d.GroundMarkCounts.ScorchBorn == 0 && d.GroundMarkCounts.Marks == 0 && d.ScorchAccCount == 0
                   && d.PitCounts == a.PitCounts && d.PitCount == a.PitCount && d.PitAccCount == a.PitAccCount,
                $"E4 读 EMG4-TER-01 写出的短段快照（段 30 五个字段、无段 31）：{rd}，熔坑 / 累计 / 前 5 项计数一致（{d.PitCounts}），尾部两项取 0（{d.GroundMarkCounts}）、没有焦土累计");
        }

        // ── F 家园：写格网、悬停、清除 ─────────────────────────────────────────

        private static void CheckHomeWrite()
        {
            Line("  · F. 家园：战痕按格写进格网（只盖空地 / 可熔地面，不碰建筑）、同一格反复标记、悬停、清除工作单、熔坑同填平");
            CampaignState s = NewWorld(Seeds[0], scrap: 100);
            CombatKernel k = HomeKernel;
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell? spot = FindSpot(s, Open);
            if (spot == null)
            {
                Fail("F 找不到空地");
                return;
            }
            GridCell g = spot.Value;
            var at = new double2(g.X, g.Y);
            BurnTwice(k, at, WorldSeconds);
            WorldSeconds(0.5f);
            GroundMarkRecord rec = s.World.GroundMarks.FirstOrDefault(r => r.Kind == EmgGroundMarkConst.KindScorch);
            var overrides = new List<Unity.Mathematics.int4>();
            map.CollectCellOverrides(overrides);
            bool onBaseMap = overrides.Any(o => o.x == g.X && o.y == g.Y && o.z == Code("scorched")); // DEBT-FG3GEN01-08：战略地图底图覆盖表含这一格
            int scorchedCells = Enumerable.Range(-3, 7).SelectMany(dy => Enumerable.Range(-3, 7).Select(dx => map.GetTerrain(new GridCell(g.X + dx, g.Y + dy)))).Count(t => t == Code("scorched"));
            Expect(rec != null && map.GetTerrain(g) == Code("scorched") && scorchedCells >= 3 && map.StateOf(GridMath.Address(g, map.ChunkSize).ChunkX, GridMath.Address(g, map.ChunkSize).ChunkY) == ChunkState.Modified && onBaseMap,
                $"F1 家园同一处烧满 20 秒：格网 ({g.X},{g.Y}) 一带 {scorchedCells} 格写成焦土（区块进差异存档、地图底图覆盖表含这一格 {onBaseMap}），记 1 处战痕（{Marks(s)}）");
            WorldSeconds(0.2f);
            Expect(k.ScorchGroundCount == 1 && k.ScorchedAt(at), "F2 焦土战痕写回内核焦土圈（下一步起圈里点燃阈值下调）");
            WorldSeconds(12f); // 明火排的烟（气体斑块）飘在上面时悬停先说烟；等它散了再看地面
            int key = StatusTagHover.HoverEmgGroundForTests(HomeSite, at);
            TooltipContent tip = StatusTagHover.HoveredEmgContentForTests();
            Expect(rec != null && key == -(StatusTagHover.MarkHoverKeyBase + rec.Id) && tip != null && tip.Title == "战痕 · 焦土" && tip.Body.Contains("40 度") && tip.Body.Contains("清除场地物"),
                $"F3 B05 悬停：“{tip?.Title}”——{tip?.Body}");
            int cellsBefore = GroundMarkService.CellsWritten;
            BurnTwice(k, at, WorldSeconds);
            WorldSeconds(0.5f);
            Expect(s.World.GroundMarks.Count(r => r.Kind == EmgGroundMarkConst.KindScorch) == 1 && rec.Hits == 2 && GroundMarkService.CellsWritten - cellsBefore <= 2,
                $"F4 负向“同一格反复标记”：同一处再烧满 20 秒仍是 1 处（命中 {rec?.Hits} 次），已是焦土的格不再改（新写 {GroundMarkService.CellsWritten - cellsBefore} 格）");
            // 玻化地面 / 冻土（真实路径：家园生成长期留存的玻化层 / 冰）；挨着一座建筑——建筑格不改
            GridCell? glassSpot = FindSpot(s, Open, 10, 90, new List<GridCell> { g });
            GridCell? iceSpot = glassSpot != null ? FindSpot(s, Open, 10, 90, new List<GridCell> { g, glassSpot.Value }) : null;
            if (glassSpot == null || iceSpot == null)
            {
                Fail("F 找不到第二 / 三块空地");
                return;
            }
            BuildingRecord shed = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "mark-store", new GridCell(glassSpot.Value.X + 3, glassSpot.Value.Y));
            List<GridCell> shedCells = shed != null ? Enumerable.Range(-3, 9).SelectMany(dy => Enumerable.Range(0, 9).Select(dx => new GridCell(glassSpot.Value.X + dx, glassSpot.Value.Y + dy)))
                .Where(c => map.OccupantAt(c) == shed.BuildingId).ToList() : new List<GridCell>();
            List<byte> shedBefore = shedCells.Select(map.GetTerrain).ToList();
            k.SpawnPatch(EmgMatterConst.Glass, new double2(glassSpot.Value.X, glassSpot.Value.Y), 3f, 10f);
            k.SpawnPatch(EmgMatterConst.Ice, new double2(iceSpot.Value.X, iceSpot.Value.Y), 1.5f, 10f);
            WorldSeconds(0.5f);
            Expect(map.GetTerrain(glassSpot.Value) == Code("glazed") && map.GetTerrain(iceSpot.Value) == Code("frozen")
                   && shedCells.Count > 0 && shedCells.Select(map.GetTerrain).SequenceEqual(shedBefore) && HomeGridService.FindBuilding(s, shed.BuildingId) != null,
                $"F5 玻化层 / 冰在家园生成：格网写成玻化地面 / 冻土；挨着的仓库占的 {shedCells.Count} 格地面不改、仓库不受影响");
            Expect(HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, new GridCell(iceSpot.Value.X, iceSpot.Value.Y), 0).Reasons.All(r => r.Code != GridBlockReason.Terrain)
                   && NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer),
                "F6 焦土 / 冻土照常可建、可走（地形原因不拦）");
            // 熔坑：真实路径凝渣成坑 → 格网熔坑（不可建、不可走），清除列表里只出内核熔坑一张
            GridCell? ruin = FindSpot(s, Meltable, 10, 120, new List<GridCell> { g, glassSpot.Value, iceSpot.Value }, 1);
            GridCell pitCell = default;
            if (ruin != null)
            {
                pitCell = ruin.Value;
                k.SpawnPatch(EmgMatterConst.Molten, new double2(pitCell.X, pitCell.Y), 1f, 35f);
                WorldSeconds(14f);
                GridPlacementResult place = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, pitCell, 0);
                var box = new List<EmgPersistItem>();
                SitePersistService.CollectInBox(s, pitCell, pitCell, box);
                Expect(k.PitCount == 1 && map.GetTerrain(pitCell) == Code("meltpit") && s.World.GroundMarks.Any(r => r.Kind == EmgGroundMarkConst.KindPit)
                       && place.Has(GridBlockReason.Terrain) && box.Count(i => i.Kind == EmgPersistItem.KindPit) == 1 && box.All(i => i.Kind != EmgPersistItem.KindMark),
                    $"F7 废墟上凝渣成坑：格网 ({pitCell.X},{pitCell.Y}) 写成熔坑（地形原因不能建造），清除列表只出内核熔坑 1 张（{box.Count} 件，不重复列战痕）");
            }
            else
            {
                Fail("F7 找不到可熔地面");
            }
            // 清除工作单：下单预留 → 再点取消全额返还 → 下单完工恢复空地
            string target = SitePersistService.TargetOf(EmgPersistItem.KindMark, rec?.Id ?? 0);
            int before = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            ClickClear(s, g);
            WorkOrderRecord o = SitePersistService.ActiveOrder(s, target);
            int reserved = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            Expect(o != null && before - reserved == SitePersistService.ClearScrap && HomeValleyWorkOrders.DescribeTarget(s, o).Contains("焦土"),
                $"F8 建造模式“清除场地物”点焦土 → 开清除工作单、预留废料 {before} → {reserved}，工单目标“{(o != null ? HomeValleyWorkOrders.DescribeTarget(s, o) : "")}”");
            int beforeCancel = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            ClickClear(s, g);
            int refunded = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            Expect(SitePersistService.ActiveOrder(s, target) == null && refunded - beforeCancel == SitePersistService.ClearScrap && map.GetTerrain(g) == Code("scorched"),
                $"F9 负向“清除工作单取消后返还”：同一格再点一次取消，全额返还（{beforeCancel} → {refunded}），焦土还在");
            SitePersistService.OrderClear(s, target);
            bool cleared = FgProductionSelfCheck.StepUntil(() => GroundMarkService.Find(s, rec?.Id ?? 0) == null, 300);
            WorldSeconds(0.2f);
            Expect(cleared && map.GetTerrain(g) == 0 && k.ScorchGroundCount == 0,
                "F10 空闲机器领单完工：这一处的格回到空地、战痕记录删除、内核焦土圈撤掉");
            if (ruin != null && k.PitCount == 1)
            {
                string pitTarget = SitePersistService.TargetOf(EmgPersistItem.KindPit, k.PitAt(0).Id);
                SitePersistService.OrderClear(s, pitTarget);
                bool filled = FgProductionSelfCheck.StepUntil(() => k.PitCount == 0, 300);
                WorldSimulation.StepMany(3);
                Expect(filled && map.GetTerrain(pitCell) == 0 && s.World.GroundMarks.All(r => r.Kind != EmgGroundMarkConst.KindPit) && NavService.PassableNow(pitCell.X, pitCell.Y, NavConst.ClassPlayer)
                       && !HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, pitCell, 0).Has(GridBlockReason.Terrain),
                    "F11 填平内核熔坑（k: 工作单）：格网上同处的熔坑一并填平成空地、战痕删除、又能走能建");
            }
            // 修复轮 1：清玻化层斑块（p:）连带清同处的玻化地面战痕
            var glassBox = new List<EmgPersistItem>();
            SitePersistService.CollectInBox(s, glassSpot.Value, glassSpot.Value, glassBox);
            EmgPersistItem glassPatch = glassBox.FirstOrDefault(i => i.Kind == EmgPersistItem.KindPatch);
            bool glassCleared = glassPatch.Id > 0 && SitePersistService.TryClearTarget(s, SitePersistService.TargetOf(EmgPersistItem.KindPatch, glassPatch.Id));
            Expect(glassCleared && map.GetTerrain(glassSpot.Value) == 0 && s.World.GroundMarks.All(r => r.Kind != EmgGroundMarkConst.KindGlazed) && map.GetTerrain(iceSpot.Value) == Code("frozen"),
                $"F12 清除玻化层斑块（p: 完工入口）：同处的玻化地面战痕一并清掉、格回空地；别处的冻土不动（{Marks(s)}）");
            // 修复轮 1：传送带 / 管线占着的格不改
            var beltCell = new GridCell(iceSpot.Value.X + 1, iceSpot.Value.Y - 2);
            var pipeCell = new GridCell(iceSpot.Value.X - 1, iceSpot.Value.Y - 2);
            byte beltBefore = map.GetTerrain(beltCell), pipeBefore = map.GetTerrain(pipeCell);
            map.SetBelt(beltCell, 1);
            map.SetPipe(pipeCell, 1);
            GroundMarkService.Apply(s, map, EmgGroundMarkConst.KindScorch, new double2(iceSpot.Value.X, iceSpot.Value.Y - 2), 2f);
            bool around = map.GetTerrain(new GridCell(iceSpot.Value.X, iceSpot.Value.Y - 2)) == Code("scorched");
            map.SetBelt(beltCell, 0);
            map.SetPipe(pipeCell, 0);
            Expect(around && map.GetTerrain(beltCell) == beltBefore && map.GetTerrain(pipeCell) == pipeBefore,
                $"F13 负向“传送带 / 管线占着的格”：焦土写在周围，带下（{beltCell.X},{beltCell.Y}）与管线下（{pipeCell.X},{pipeCell.Y}）的地面不改");
        }

        // ── K 堵路警告（B11 / FGR-EMG-154）：格网熔坑单独堵死唯一通路 ─────────────────

        private static void CheckPitBlocks()
        {
            Line("  · K. B11 回归：格网熔坑（地形本身不可通行、没有内核熔坑代表）围死仓库 → 堵路警告点名熔坑；清掉点名的那处后警告解除");
            CampaignState s = NewWorld(Seeds[2], scrap: 100);
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell? spot = FindSpot(s, Open, 24, 100, null, 3);
            if (spot == null)
            {
                Fail("K 找不到空地");
                return;
            }
            GridCell c = spot.Value;
            BuildingRecord store = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "pit-wall-store", c);
            WorldSimulation.StepMany(5);
            SoftlockState st = SoftlockService.StateOf(s);
            SitePersistService.Check(s);
            bool openBefore = store != null && st.PersistBlocks.All(r => r.Key != store.BuildingId);
            // 以仓库为心、半径 8 一圈 16 处熔坑（相邻圆心 3.1 米 > 合并半径，各记一处；半径 3 → 带宽约 5 格，没有缺口）
            for (int i = 0; i < 16; i++)
            {
                double ang = i * Math.PI * 2 / 16;
                GroundMarkService.Apply(s, map, EmgGroundMarkConst.KindPit, new double2(c.X + 8 * Math.Cos(ang), c.Y + 8 * Math.Sin(ang)), 3f);
            }
            WorldSimulation.StepMany(3);
            int notes = NotifyCount(SitePersistService.NotifyBlocked);
            SitePersistService.Check(s);
            PersistBlockRecord rec = store != null ? st.PersistBlocks.FirstOrDefault(r => r.Key == store.BuildingId) : null;
            string last = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == SitePersistService.NotifyBlocked)?.Text ?? string.Empty;
            Expect(openBefore && HomeKernel.PitCount == 0 && s.World.GroundMarks.Count(r => r.Kind == EmgGroundMarkConst.KindPit) >= 8 && rec != null && rec.Kind == EmgPersistItem.KindMark && rec.Blocks
                   && NotifyCount(SitePersistService.NotifyBlocked) > notes && last.Contains("熔坑"),
                $"K1 只有格网熔坑（{s.World.GroundMarks.Length} 处、内核熔坑 {HomeKernel.PitCount}）围死仓库：报警“{last}”，记录种类 = 战痕（g:{rec?.ItemId}）");
            if (rec != null)
            {
                SitePersistService.TryClearTarget(s, SitePersistService.TargetOf(EmgPersistItem.KindMark, rec.ItemId));
            }
            WorldSimulation.StepMany(3);
            SitePersistService.Check(s);
            Expect(rec != null && GroundMarkService.Find(s, rec.ItemId) == null && st.PersistBlocks.All(r => r.Key != store.BuildingId),
                "K2 清掉点名的那处熔坑（g: 完工入口）：缺口打通，下一次检查警告解除");
        }

        private static int NotifyCount(string type) => NotificationCenter.History.Count(e => e.Type?.Id == type);

        // ── G ≤ 64 处、超出合并 ───────────────────────────────────────────

        private static void CheckMergeCap()
        {
            Line("  · G. 负向“标记超 64 处时合并”：每地点 ≤ emg.terrain.marks_max，超出并进最近的同种");
            CampaignState s = CampaignState.CreateNew("fgmark-cap", "Standard", Seeds[1]);
            CampaignFgStateDomains.EnsureAll(s);
            int max = GroundMarkService.MaxMarks;
            int merged0 = GroundMarkService.Merged;
            for (int i = 0; i < max + 6; i++)
            {
                GroundMarkService.Apply(s, null, EmgGroundMarkConst.KindScorch, new double2(i * 10, 0), 1.5f);
            }
            GroundMarkRecord last = s.World.GroundMarks.OrderBy(r => math.distance(new double2(r.X, r.Y), new double2((max + 5) * 10, 0))).First();
            Expect(s.World.GroundMarks.Length == max && GroundMarkService.Merged - merged0 == 6 && s.World.GroundMarks.Sum(r => r.Hits) == max + 6 && last.Radius <= GroundMarkService.MaxRadius,
                $"G1 {max + 6} 处焦土：记录停在 {s.World.GroundMarks.Length} 处，多出的 6 处并进最近的同种（命中合计 {s.World.GroundMarks.Sum(r => r.Hits)}，外接圆 ≤ {GroundMarkService.MaxRadius} 米）");
            GroundMarkService.Apply(s, null, EmgGroundMarkConst.KindPit, new double2(-500, 0), 1.5f);
            Expect(s.World.GroundMarks.Length == max && s.World.GroundMarks.All(r => r.Kind == EmgGroundMarkConst.KindScorch),
                "G2 满额时来了没有同种的熔坑：并进最近的一处（它仍是焦土），记录数不超上限");
            CampaignState fresh = CampaignState.CreateNew("fgmark-same", "Standard", Seeds[1]);
            CampaignFgStateDomains.EnsureAll(fresh);
            GroundMarkService.Apply(fresh, null, EmgGroundMarkConst.KindGlazed, new double2(0, 0), 1.5f);
            GroundMarkService.Apply(fresh, null, EmgGroundMarkConst.KindGlazed, new double2(1, 0), 1.5f);
            GroundMarkService.Apply(fresh, null, EmgGroundMarkConst.KindFrozen, new double2(1, 0), 1.5f);
            Expect(fresh.World.GroundMarks.Length == 2 && fresh.World.GroundMarks[0].Hits == 2,
                "G3 同一位置同种标记并进原记录（2 次 → 1 处），不同种另记一处");
            CampaignState old = CampaignState.CreateNew("fgmark-old", "Standard", Seeds[1]);
            old.World.GroundMarks = null;
            CampaignFgStateDomains.EnsureAll(old);
            Expect(old.World.GroundMarks != null && old.World.GroundMarks.Length == 0, "G4 FGT-EMG-041：旧档没有战痕字段 → 读成空（新增字段缺省为空，不升 schema）");
        }

        // ── H 旧档表外地形字节 ────────────────────────────────────────────

        private static void CheckUnregistered()
        {
            Line("  · H. 负向“旧档读到未登记的地面材质值”：按原生成值处理并记 Error，不坏档");
            CampaignState s = NewWorld(Seeds[2], observe: true);
            HomeGridMap live = HomeGridService.MapFor(s);
            var probe = new HomeGridMap(live.ChunkSize, live.TerrainSource, live.SurfaceId);
            GridCell core = HomeGridService.CorePivot(s);
            ChunkAddress a = GridMath.Address(core, live.ChunkSize);
            int baseX = a.ChunkX * live.ChunkSize, baseY = a.ChunkY * live.ChunkSize;
            var c0 = new GridCell(baseX + 1, baseY + 1);
            var c1 = new GridCell(baseX + 2, baseY + 1);
            var c2 = new GridCell(baseX + 3, baseY + 1);
            byte gen0 = probe.GetTerrain(c0), gen2 = probe.GetTerrain(c2);
            string payload = WorldDiffCodec.Encode(live.ChunkSize, new List<ChunkCellDiff>
            {
                new ChunkCellDiff((ushort)(1 * live.ChunkSize + 1), ChunkCellDiff.TerrainBit, 200, 0),
                new ChunkCellDiff((ushort)(1 * live.ChunkSize + 2), ChunkCellDiff.TerrainBit, Code("scorched"), 0),
                new ChunkCellDiff((ushort)(1 * live.ChunkSize + 3), (byte)(ChunkCellDiff.TerrainBit | ChunkCellDiff.PollutionBit), 201, 2),
            });
            var loaded = new HomeGridMap(live.ChunkSize, live.TerrainSource, live.SurfaceId);
            loaded.SetSavedDiffs(new[] { new ChunkDiffRecord { SurfaceId = live.SurfaceId, ChunkX = a.ChunkX, ChunkY = a.ChunkY, DiffPayload = payload } });
            Expect(loaded.UnregisteredTerrainCells == 2 && loaded.GetTerrain(c0) == gen0 && loaded.GetTerrain(c1) == Code("scorched") && loaded.GetTerrain(c2) == gen2 && loaded.GetPollution(c2) == 2,
                $"H1 差异里 200 / 201 两个表外字节：这两格按生成值（{gen0} / {gen2}）处理、记 Error，登记过的焦土照常还原，同格的污染 2 级照留");
            var lonely = new HomeGridMap(live.ChunkSize, live.TerrainSource, live.SurfaceId);
            lonely.SetSavedDiffs(new[] { new ChunkDiffRecord { SurfaceId = live.SurfaceId, ChunkX = a.ChunkX, ChunkY = a.ChunkY,
                DiffPayload = WorldDiffCodec.Encode(live.ChunkSize, new List<ChunkCellDiff> { new ChunkCellDiff(5, ChunkCellDiff.TerrainBit, 250, 0) }) } });
            Expect(lonely.SavedDiffCount == 0 && lonely.GetTerrain(new GridCell(baseX + 5, baseY)) == probe.GetTerrain(new GridCell(baseX + 5, baseY)),
                "H2 整块只有表外字节：这个区块按生成重来（不留空差异），存档不坏");
        }

        // ── I 真文件存读档 ─────────────────────────────────────────────────

        private static string Digest(CampaignState s, GridCell[] cells)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            return $"marks:{Marks(s)}|cells:{string.Join(",", cells.Select(c => map.GetTerrain(c)))}|next:{s.World.NextGroundMarkId}";
        }

        private static void CheckWorldSave()
        {
            Line("  · I. FGT-EMG-060：真文件存读档——格网差异还原地面材质、战痕索引往返、内核焦土圈重建、熔坑仍不可通行");
            CampaignState s = NewWorld(Seeds[1], scrap: 100);
            CombatKernel k = HomeKernel;
            GridCell? spot = FindSpot(s, Open);
            GridCell? ruin = spot != null ? FindSpot(s, Meltable, 10, 120, new List<GridCell> { spot.Value }, 1) : null;
            if (spot == null || ruin == null)
            {
                Fail("I 找不到空地 / 可熔地面");
                return;
            }
            GridCell g = spot.Value, p = ruin.Value;
            BurnTwice(k, new double2(g.X, g.Y), WorldSeconds);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(p.X, p.Y), 1f, 35f);
            WorldSeconds(14f);
            WorldSimulation.SyncAllForSave();
            GridCell[] cells = { g, new GridCell(g.X + 1, g.Y), p, new GridCell(p.X, p.Y + 1) };
            string before = Digest(s, cells);
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
            string loaded = Digest(rr.State, cells);
            WorldSimulation.StepMany(3);
            Expect(save.Success && loaded == before && before.Contains(Code("scorched").ToString()) && before.Contains(Code("meltpit").ToString()),
                $"I1 存读档后格网地面材质与战痕一致（{before}）");
            Expect(HomeKernel.ScorchGroundCount == 1 && HomeKernel.ScorchedAt(new double2(g.X, g.Y)) && !NavService.PassableNow(p.X, p.Y, NavConst.ClassPlayer),
                "I2 下次载入：焦土转为内核焦土圈（点燃阈值下调）、熔坑格仍不可通行（寻路镜像）");
        }

        // ── J 暂停、倍速、观察一致 ─────────────────────────────────────────

        private static string Scenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe, 100);
            pausedHeld = true;
            CombatKernel k = HomeKernel;
            // 明火自带 22 秒寿命：单块就跨过焦土 20 秒阈值（两块相邻会并成一块，只记一次燃烧秒）。原先按默认 18 秒，靠站在火里的机器烧毁排放给明火续时
            // 才过 20 秒，焦土出不出取决于持续伤害节拍（FG-GAP-160 修前双结算烧死两台机器续到 21.8 秒）；现在机器续时只会推迟到期，窗口 30 秒留足余量。
            GridCell g = FindSpot(s, Open) ?? HomeGridService.CorePivot(s);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(g.X, g.Y), 1.5f, 40f, 22f);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(g.X + 1, g.Y), 1.5f, 30f);
            k.SpawnPatch(EmgMatterConst.Glass, new double2(g.X - 6, g.Y), 1.5f, 10f);
            WorldSimulation.StepMany(2);
            GridCell[] cells = { g, new GridCell(g.X + 1, g.Y), new GridCell(g.X - 6, g.Y) };
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Digest(s, cells) + "|t:" + GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Digest(s, cells) + "|t:" + GameClock.Ticks == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Digest(s, cells);
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
            Expect(paused && same && reference.Contains(":1@") && reference.Contains(":2@"),
                $"J1 暂停 120 帧不烧、不写格；0.5x / 1x / 3x 跑同样 30 游戏秒结果逐字一致（{reference}）");
            string seen = Scenario(Seeds[2], true, 1f, false, out _);
            string unseen = Scenario(Seeds[2], false, 1f, false, out _);
            Expect(seen == unseen, $"J2 B24 家园不被观察时结果一致（{unseen}）");
        }
    }
}
