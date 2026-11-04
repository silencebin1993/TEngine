using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
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
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.View;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-PERSIST-01 场地物长期留存与残骸掩体（FG19 FGR-EMG-150、154；FGT-EMG-060 阶段 2、FGT-EMG-040、FGT-EMG-036 第 2 条）：
    /// 内核段起真实 CombatKernel（与 CombatSite 同一套写表）断言长期留存 / 野外对照、满额崩解、摧毁 / 融化 / 清除、固态挡弹、抛射落地再弹、不可进入与推出、存读档；
    /// 世界段起真实家园（WorldSimulation + 寻路镜像 + 工作单）走建造模式“清除场地物”的点选 / 框选 / 取消、堵路警告、寻路镜像重放、真文件存读档、离家报告、暂停倍速与观察一致。
    /// </summary>
    public static class FgEmgPersistSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/场地物长期留存")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            bool gates = ResearchGate.LegacyGatesOpenForTests;
            ResearchGate.LegacyGatesOpenForTests = true;
            int fail;
            try
            {
                fail = Run(report);
            }
            finally
            {
                ResearchGate.LegacyGatesOpenForTests = gates;
            }
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
            Line("\n[场地物长期留存] 残骸与固态斑块长期留存、满额崩解、固态挡弹、抛射再弹、清除工作单、堵路警告、寻路镜像、存读档、离家报告");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpersist-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Step(CheckLifetimes);
                Step(CheckCaps);
                Step(CheckEndings);
                Step(CheckAbsorb);
                Step(CheckLobRebound);
                Step(CheckBlock);
                Step(CheckCarriers);
                Step(CheckKernelSave);
                Step(CheckSiteScope);
                Step(CheckOrders);
                Step(CheckBlockWarning);
                Step(CheckBlockThick);
                Step(CheckNavMirror);
                Step(CheckWorldSave);
                Step(CheckRaidReport);
                Step(CheckTimingAndObservation);
                Step(CheckHud);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"场地物长期留存自检抛异常：{e}");
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
            Line($"  · [场地物长期留存] 断言通过 {_pass}，失败 {_fail}");
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

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int i = 0; i < n; i++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 500f, CombatUnitKind kind = CombatUnitKind.Machine, float speed = 4f, bool commanded = false) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = f, Behavior = f == CombatFaction.Player || commanded ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = speed, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });

        private static CombatWeapon Instant(float damage, float range = 60f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Flight(float damage, float speed = 20f, float life = 3f, int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = damage,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life, Reading = new CombatReading { Carrier = CombatCarrier.Projectile }, Program = program,
        };

        private static int Lob(CombatKernel k)
        {
            var instrs = new List<EmgInstr>
            {
                new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Expire), Role = 3, Ordinal = 0 },
                new EmgInstr { Port = 110, Role = 6, Verb = (byte)EmgVerb.Lob, Amount = 4f, A = 1f, Ordinal = 1 },
            };
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            return k.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Expire, Flags = EmgProgramFlags.NeedsFlight }, nodes, instrs);
        }

        private static int Tally(CombatKernel k, byte matter)
        {
            int n = 0;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                n += k.TryGetPatch(i, out CombatZone z) && z.Matter == matter ? 1 : 0;
            }
            return n;
        }

        private static CombatZone FirstOf(CombatKernel k, byte matter)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    return z;
                }
            }
            return default;
        }

        private static int NotifyCount(string type) => NotificationCenter.History.Where(e => e.Type?.Id == type).Sum(e => e.Count);

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            NotificationCenter.ResetForTests();
            NotificationCenter.Bind(s);
            FeedbackCues.ResetForTests();
            return s;
        }

        private static CombatKernel HomeKernel => CombatSites.Get(HomeValleyLayout.RegionId).Kernel;

        private static int AddWreckOf(CombatKernel k, double2 at, short profile, float temp = 0f, float radius = 0.7f)
        {
            k.TryAddWreck(new EmgWreck { Pos = at, Radius = radius, Hp = 40f, MaxHp = 40f, Mass = 6f, Temp = temp, Born = k.Time, Until = double.MaxValue, Profile = profile }, out int id);
            return id;
        }

        private static int AddWreck(CombatKernel k, double2 at, float radius = 0.7f)
        {
            k.TryAddWreck(new EmgWreck { Pos = at, Radius = radius, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = double.MaxValue, Profile = -1 }, out int id);
            return id;
        }

        // ── A 数据 ───────────────────────────────────────────────────────

        private static void CheckData()
        {
            float patchMax = GridContent.Tuning("emg.persist.patch_max"), seconds = GridContent.Tuning("emg.persist.clear_seconds"), scrap = GridContent.Tuning("emg.persist.clear_scrap");
            EmgFieldCatalog.Compiled c = EmgFieldCatalog.Current;
            EmgFieldRule absorb = c.Rules[EmgFieldConst.SolidAbsorb];
            Expect(patchMax == 128f && seconds > 0f && scrap >= 0f && absorb.Impl == EmgFieldConst.ImplKernel && Mathf.Approximately(absorb.Param, 1.5f),
                $"A1 调参入表：长期留存固态上限 {patchMax}、清除工时 {seconds} 秒、预留废料 {scrap}；fieldrule_solid_absorb 落内核结算，冷渣掩护 {absorb.Param} 米（DEBT-EMG2FLD01-01）");
            string[] keys = { "emg.persist.tool_btn", "emg.persist.blocked_solid", "emg.persist.blocked_wreck", "emg.persist.raid_left", "emg.persist.alarm", "emg.persist.collapsed" };
            Expect(keys.All(GameText.Has), "A2 玩家可见文本走文本键（工具按钮、堵路警告、告警、崩解、离家报告）");
        }

        // ── B 长期留存 / 野外对照 ─────────────────────────────────────────

        private static void CheckLifetimes()
        {
            (int wrecks, int solids, int debris, bool infinite, int aged) Run(bool persist)
            {
                using CombatKernel k = NewKernel(persist);
                byte[] matters = { EmgMatterConst.Cured, EmgMatterConst.Slag, EmgMatterConst.Ice, EmgMatterConst.Glass };
                for (int i = 0; i < matters.Length; i++)
                {
                    k.SpawnPatch(matters[i], new double2(i * 12, 30), 2f, 1f, 5f);
                }
                int victim = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                k.Kill(victim, 0);
                k.TryAddWreck(new EmgWreck { Pos = new double2(10, 0), Radius = 0.7f, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = k.Time + 5.0, Profile = -1 }, out _);
                StepN(k, 2);
                bool inf = k.TryGetWreck(0, out EmgWreck w) && w.Until == double.MaxValue;
                StepN(k, 60 * 100);
                (int wr, int so) = k.PersistTally();
                return (wr, so, Tally(k, EmgMatterConst.Debris), inf, k.WreckCounts.Aged);
            }
            var home = Run(true);
            var wild = Run(false);
            Expect(home.wrecks == 2 && home.solids == 4 && home.infinite && home.aged == 0,
                $"B1 长期留存地点（家园内核）：100 秒后 2 块残骸（阵亡生成的不设寿命、带有限寿命的旧档残骸也留存）与冰 / 玻化层 / 固化胶块 / 冷渣 4 块固态都还在（{home}）");
            Expect(wild.wrecks == 0 && wild.solids == 0 && wild.aged == 2 && !wild.infinite,
                $"B2 负向·野外地点（远征区域）：同样的东西按各自寿命消失、残骸崩解计数 2（{wild}）");
        }

        // ── C 满额 ───────────────────────────────────────────────────────

        private static void CheckCaps()
        {
            using (CombatKernel k = NewKernel(true))
            {
                // 三种物质铺满 40 + 48 + 40 = 128，再来 1 块玻化层 = 129 → 总额崩解最早的（第一块冷渣）
                int n = 0;
                void Fill(byte matter, int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        k.SpawnPatch(matter, new double2((n % 20) * 6, (n / 20) * 6), 1f);
                        n++;
                        StepN(k, 1);
                    }
                }
                Fill(EmgMatterConst.Slag, 48);
                Fill(EmgMatterConst.Cured, 40);
                Fill(EmgMatterConst.Ice, 40);
                double2 oldest = FirstOf(k, EmgMatterConst.Slag).Pos;
                int before = k.PersistCounts.Capped;
                Fill(EmgMatterConst.Glass, 1);
                StepN(k, 3);
                bool debrisAtOldest = false;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    debrisAtOldest |= k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Debris && math.distance(z.Pos, oldest) < 1e-3;
                }
                Expect(k.PersistTally().Solids == 128 && k.PersistCounts.Capped == before + 1 && debrisAtOldest && Tally(k, EmgMatterConst.Slag) == 47 && Tally(k, EmgMatterConst.Glass) == 1,
                    $"C1 长期留存固态满 128：再来一块，最早的在原处崩解成碎屑（可见）、计数 +1，总数仍 128（{k.PersistTally().Solids}，崩解 {k.PersistCounts.Capped}）");
                Fill(EmgMatterConst.Ice, 1);
                StepN(k, 3);
                Expect(Tally(k, EmgMatterConst.Ice) == 40 && k.PersistTally().Solids <= 128,
                    $"C2 同时计入各自软上限（FG19B §8）：冰满 40 再来一块 → 最早的冰崩解，冰仍 {Tally(k, EmgMatterConst.Ice)} 块");
            }
            using (CombatKernel k = NewKernel(true))
            {
                for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
                {
                    AddWreck(k, new double2(i % 32 * 3, i / 32 * 3));
                }
                k.TryGetWreck(0, out EmgWreck first);
                int v = Unit(k, CombatFaction.Hostile, new double2(-30, -30));
                k.Kill(v, 0);
                StepN(k, 2);
                bool oldestGone = !k.TryGetPersist(EmgPersistItem.KindWreck, first.Id, out _);
                Expect(k.WreckCount == EmgConst.DefaultWreckCapacity && k.WreckCounts.Capped == 1 && k.PatchStats.WrecksToDebris == 1 && oldestGone && Tally(k, EmgMatterConst.Debris) == 1,
                    $"C3 残骸满 256：再阵亡一个，最早的崩解为碎屑（计数 {k.WreckCounts.Capped}），仍 256");
            }
            using (CombatKernel k = NewKernel(true))
            {
                // 总容量挤压不碰长期留存固态：战斗斑块把容量挤满，固态一块不少
                k.SpawnPatch(EmgMatterConst.Cured, new double2(-40, -40), 1f);
                StepN(k, 1);
                for (int i = 0; i < 1100; i++)
                {
                    k.SpawnPatch(i % 2 == 0 ? EmgMatterConst.Oil : EmgMatterConst.Smoke, new double2(i % 40 * 7, i / 40 * 7), 0.5f, 1f, 200f);
                    if (i % 30 == 29)
                    {
                        StepN(k, 1);
                    }
                }
                StepN(k, 40);
                Expect(Tally(k, EmgMatterConst.Cured) == 1, "C4 负向：地点斑块总上限挤压只挤普通斑块，长期留存固态不被提前到期");
            }
        }

        // ── D 摧毁 / 融化 / 清除 ───────────────────────────────────────────

        private static bool MatterAlive(CombatKernel k, byte matter)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckEndings()
        {
            // EMG2-E2E-01：熔融金属被反复加热，寿命（10 秒）到时还高于凝固线：同样凝成冷渣，不能凭空消失（突袭里连续熔穿实测发现）
            using (CombatKernel melt = NewKernel(true))
            {
                melt.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 1f, 4f, 10f);
                for (int i = 0; i < 60 * 9; i++)
                {
                    if (i % 15 == 0)
                    {
                        melt.InjectPatchEnergy(new double2(0, 0), 1f, EmgConst.PortHeat, 30f);
                    }
                    melt.Step(Dt, melt.Time + Dt);
                }
                bool hot = MatterAlive(melt, EmgMatterConst.Molten);
                for (int i = 0; i < 60 * 2; i++)
                {
                    melt.Step(Dt, melt.Time + Dt);
                }
                Expect(hot && !MatterAlive(melt, EmgMatterConst.Molten) && MatterAlive(melt, EmgMatterConst.Slag),
                    $"熔融金属一直被加热（9 秒时还是熔融 {hot}）：10 秒寿命到凝成冷渣（{MatterAlive(melt, EmgMatterConst.Slag)}），没有凭空消失");
            }

            using CombatKernel k = NewKernel(true);
            k.SpawnPatch(EmgMatterConst.Ice, new double2(0, 0), 2f);
            k.SpawnPatch(EmgMatterConst.Cured, new double2(30, 0), 2f);
            k.SpawnPatch(EmgMatterConst.Slag, new double2(60, 0), 2f);
            int w = AddWreck(k, new double2(90, 0));
            StepN(k, 2);
            int iceId = FirstOf(k, EmgMatterConst.Ice).EmgId, slagId = FirstOf(k, EmgMatterConst.Slag).EmgId;
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortHeat, 2f); // +20 度：冰 −10 → 10 度，持续 1 秒解冻（E13）
            k.InjectPatchEnergy(new double2(30, 0), 0.5f, EmgConst.PortHeat, 30f); // +300 度：胶块 ≥ 250 度软化（E03）
            bool softened = false; // 软化后的聚合胶还带着 320 度，下一节拍就可能烧起来：抓胶块消失那一刻的产物
            for (int i = 0; i < 90; i++)
            {
                StepN(k, 1);
                softened |= Tally(k, EmgMatterConst.Cured) == 0 && Tally(k, EmgMatterConst.Polymer) >= 1;
            }
            Expect(Tally(k, EmgMatterConst.Ice) == 0 && Tally(k, EmgMatterConst.Water) >= 1, $"D1 融化：持续加热的冰化成水（E13），长期留存不拦规则（水 {Tally(k, EmgMatterConst.Water)}）");
            Expect(Tally(k, EmgMatterConst.Cured) == 0 && softened, "D2 融化：加热的固化胶块软化回聚合胶（E03）");
            bool cleared = k.ClearPersist(EmgPersistItem.KindPatch, slagId) && k.ClearPersist(EmgPersistItem.KindWreck, w);
            bool again = k.ClearPersist(EmgPersistItem.KindWreck, w) || k.ClearPersist(EmgPersistItem.KindPatch, iceId);
            StepN(k, 2);
            Expect(cleared && !again && Tally(k, EmgMatterConst.Slag) == 0 && k.WreckCount == 0 && Tally(k, EmgMatterConst.Debris) == 0 && k.PersistCounts.Cleared == 2,
                $"D3 工作单清除：冷渣与残骸消失、不留碎屑、计数 {k.PersistCounts.Cleared}；目标已没了 / 不是固态的再清一次返回 false（不重复计数）");
        }

        // ── E 挡弹 ───────────────────────────────────────────────────────

        private static void CheckAbsorb()
        {
            // 即时命中：胶块 / 冷渣在两者之间
            (float hurt, float patchHp, int absorbs, CombatFireResult result) Shot(byte matter, double targetY, bool fly)
            {
                using CombatKernel k = NewKernel(true);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(fly ? Flight(25f) : Instant(25f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, targetY), -1, 1000f, CombatUnitKind.Enemy, 0f);
                if (matter != 0)
                {
                    k.SpawnPatch(matter, new double2(0, 10), 2f);
                    StepN(k, 1);
                }
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                StepN(k, 90);
                k.TryGetUnit(foe, out CombatUnitView v);
                return (1000f - v.Health, matter != 0 ? FirstOf(k, matter).Hp : 0f, k.PersistCounts.Absorbs, r);
            }
            float curedHp = HomeRow(EmgMatterConst.Cured).Hp, slagHp = HomeRow(EmgMatterConst.Slag).Hp;
            var cFar = Shot(EmgMatterConst.Cured, 20, false);
            var sBehind = Shot(EmgMatterConst.Slag, 13, false);
            var sInside = Shot(EmgMatterConst.Slag, 10, false);
            var sFar = Shot(EmgMatterConst.Slag, 20, false);
            var none = Shot(0, 20, false);
            Expect(cFar.hurt == 0f && Mathf.Approximately(cFar.patchHp, curedHp - 25f) && cFar.absorbs == 1, $"E1 即时命中：固化胶块挡所有穿过的（目标不掉血，胶块耐久 {curedHp} → {cFar.patchHp}）");
            Expect(sBehind.hurt == 0f && sInside.hurt == 0f && Mathf.Approximately(sBehind.patchHp, slagHp - 25f), $"E2 冷渣低矮掩体：目标在其中 / 其后 1.5 米内 → 挡下（耐久 → {sBehind.patchHp}）");
            Expect(sFar.hurt > 0f && Mathf.Approximately(sFar.patchHp, slagHp) && none.hurt == sFar.hurt, $"E3 负向：目标在冷渣后 8 米（掩护范围外）→ 打得中、冷渣不掉耐久，与没有冷渣同伤害 {none.hurt}");
            var fCured = Shot(EmgMatterConst.Cured, 20, true);
            var fBehind = Shot(EmgMatterConst.Slag, 13, true);
            var fFar = Shot(EmgMatterConst.Slag, 20, true);
            var fNone = Shot(0, 20, true);
            Expect(fCured.hurt == 0f && fCured.absorbs == 1 && Mathf.Approximately(fCured.patchHp, curedHp - 25f), $"E4 飞行弹体：固化胶块吸收（evt.absorb、扣耐久 → {fCured.patchHp}）");
            Expect(fBehind.hurt == 0f && fBehind.absorbs == 1 && fFar.hurt > 0f && fFar.hurt == fNone.hurt, $"E5 飞行弹体 × 冷渣：其后 1.5 米内的单位被掩护；8 米外照常命中（{fFar.hurt}）");
            // 打穿：耐久归零碎掉，掩体消失，后面的打得中
            using (CombatKernel k = NewKernel(true))
            {
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(500f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 20), -1, 5000f, CombatUnitKind.Enemy, 0f);
                k.SpawnPatch(EmgMatterConst.Cured, new double2(0, 10), 2f);
                StepN(k, 1);
                k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                k.FireAt(me, foe, k.Time + 10);
                StepN(k, 2);
                k.TryGetUnit(foe, out CombatUnitView v);
                Expect(Tally(k, EmgMatterConst.Cured) == 0 && v.Health < 5000f, "E6 胶块耐久打光就碎，后面的目标打得中（摧毁路径）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(500f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 13), -1, 5000f, CombatUnitKind.Enemy, 0f);
                k.SpawnPatch(EmgMatterConst.Slag, new double2(0, 10), 2f);
                StepN(k, 1);
                k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                Expect(Tally(k, EmgMatterConst.Slag) == 0 && Tally(k, EmgMatterConst.Debris) >= 1 && Tally(k, EmgMatterConst.Cured) == 0, "E8 冷渣被直射打碎同样走 E20 出金属碎屑（与抛射落地同一条规则；胶块仍无产物）");
            }
            // 远征：同一条挡弹规则，地点无关
            using (CombatKernel k = NewKernel(false))
            {
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(25f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 13), -1, 1000f, CombatUnitKind.Enemy, 0f);
                k.SpawnPatch(EmgMatterConst.Slag, new double2(0, 10), 2f);
                StepN(k, 1);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                k.TryGetUnit(foe, out CombatUnitView v);
                Expect(v.Health == 1000f, "E7 野外的冷渣同样挡弹（挡弹按端口，不按地点）");
            }
        }

        private static EmgMatter HomeRow(byte matter)
        {
            using CombatKernel k = NewKernel(true);
            return k.MatterRow(matter);
        }

        // ── F 抛射落地再弹（FGT-EMG-036 第 2 条）───────────────────────────────

        private static void CheckLobRebound()
        {
            (double flown, int bounces, int proj) Shot(byte matter)
            {
                using CombatKernel k = NewKernel(false);
                int prog = Lob(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(5f, 10f, 2f, prog)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 10), -1, 1000f, CombatUnitKind.Structure, 0f);
                if (matter != 0)
                {
                    k.SpawnPatch(matter, new double2(0, 10), 2f);
                    StepN(k, 1);
                }
                k.FireAt(me, foe, k.Time);
                k.Kill(foe, 0); // 目标没了（结构单位不留残骸）：弹体落在空地上（落到人身上是命中，不是落地）
                double far = 0;
                for (int i = 0; i < 300; i++)
                {
                    StepN(k, 1);
                    if (k.ProjectileCount > 0 && k.TryGetProjectile(0, out CombatProjectile p))
                    {
                        far = Math.Max(far, p.Pos.y);
                    }
                }
                return (far, k.PatchStats.ProjPatchBounce, k.ProjectileCount);
            }
            var plain = Shot(0);
            var glass = Shot(EmgMatterConst.Glass);
            var ice = Shot(EmgMatterConst.Ice);
            Expect(plain.bounces == 0 && plain.flown <= 10.2, $"F1 对照：没有反射面，抛射弹飞到目标脚下（{plain.flown:0.0} 米）就落地");
            Expect(glass.bounces == 1 && Math.Abs(glass.flown - 14.0) < 0.5 && glass.proj == 0, $"F2 落在玻化层：再弹一段 = 原射程 × 0.4（飞到 {glass.flown:0.0} 米，每发 1 次，最后照常落地）");
            Expect(ice.bounces == 1 && Math.Abs(ice.flown - 12.5) < 0.5 && ice.proj == 0, $"F3 落在冰面：再弹一段 = 原射程 × 0.25（飞到 {ice.flown:0.0} 米）");
        }

        // ── G 不可进入与推出 ───────────────────────────────────────────────

        private static void CheckBlock()
        {
            using CombatKernel k = NewKernel(true);
            int inside = Unit(k, CombatFaction.Player, new double2(0.5, 0));
            int walker = Unit(k, CombatFaction.Player, new double2(-8, 0));
            k.SpawnPatch(EmgMatterConst.Cured, new double2(0, 0), 3f);
            StepN(k, 2);
            k.TryGetUnit(inside, out CombatUnitView a);
            Expect(math.distance(new double2(a.Position.x, a.Position.y), new double2(0, 0)) >= 3f, $"G1 生成时圈内单位被推到边缘（离圆心 {math.distance(new double2(a.Position.x, a.Position.y), new double2(0, 0)):0.00} 米）");
            k.IssueCommand(walker, CombatCommandKind.Move, new double2(8, 0), 0, 0.5f, 0f, 0f, false);
            bool entered = false;
            for (int i = 0; i < 60 * 8; i++)
            {
                StepN(k, 1);
                k.TryGetUnit(walker, out CombatUnitView w);
                entered |= math.distance(new double2(w.Position.x, w.Position.y), new double2(0, 0)) < 3f;
            }
            Expect(!entered, "G2 之后不可进入：穿过胶块去对面的单位没有一步踏进圆内（局部绕行）");
            k.Step(Dt, k.Time + Dt);
            Expect(k.BlockSig != 0u, "G3 活着的阻路斑块有集合签名（寻路镜像据此增量更新）");
            int id = FirstOf(k, EmgMatterConst.Cured).EmgId;
            k.ClearPersist(EmgPersistItem.KindPatch, id);
            StepN(k, 1);
            Expect(k.BlockSig == 0u, "G4 最后一块阻路斑块消失那一步签名归零（寻路镜像随之恢复）");
        }

        // ── H 导电 / 可燃 / 减速（残骸与冷渣照第 5、6 节规则参与）──────────────────────

        private static void CheckCarriers()
        {
            using (CombatKernel k = NewKernel(true))
            {
                k.SpawnPatch(EmgMatterConst.Slag, new double2(0, 0), 3f);
                StepN(k, 1);
                int id = FirstOf(k, EmgMatterConst.Slag).EmgId;
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 5f);
                StepN(k, 30);
                float charge = k.TryGetPatchEnergy(id, out _, out float ch, out _) ? ch : 0f;
                Expect(charge > 0f, $"H1 可导电：长期留存的冷渣能带电（电荷 {charge:0.00}）");
                int runner = Unit(k, CombatFaction.Hostile, new double2(-6, 0), -1, 500f, CombatUnitKind.Enemy);
                k.IssueCommand(runner, CombatCommandKind.Move, new double2(6, 0), 0, 0.5f, 0f, 0f, false);
                StepN(k, 60);
                k.TryGetUnit(runner, out CombatUnitView v);
                Expect(math.abs(v.Position.x + 6f) < 3.9f * 1f, $"H2 减速：穿过冷渣的单位比满速慢（1 秒走 {math.abs(v.Position.x + 6f):0.0} 米，满速 4 米）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                k.SpawnPatch(EmgMatterConst.Cured, new double2(0, 0), 3f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(2.5, 0), 3f, 1f, 60f);
                StepN(k, 60 * 20);
                Expect(Tally(k, EmgMatterConst.Cured) == 0, "H3 可点燃 / 受热：明火烧着的固化胶块不再挡路（E03 软化）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                int runner = Unit(k, CombatFaction.Player, new double2(-1, 0));
                AddWreck(k, new double2(0, 0), 1.5f);
                k.IssueCommand(runner, CombatCommandKind.Move, new double2(20, 0), 0, 0.5f, 0f, 0f, false);
                StepN(k, 30);
                k.TryGetUnit(runner, out CombatUnitView v);
                using CombatKernel c = NewKernel(true);
                int cr = Unit(c, CombatFaction.Player, new double2(-1, 0));
                c.IssueCommand(cr, CombatCommandKind.Move, new double2(20, 0), 0, 0.5f, 0f, 0f, false);
                StepN(c, 30);
                c.TryGetUnit(cr, out CombatUnitView cv);
                Expect(v.Position.x < cv.Position.x && v.Position.x > -1f, $"H4 残骸减速不阻挡：单位能减速穿过（{v.Position.x:0.0} 米，对照 {cv.Position.x:0.0} 米）");
            }
            // 残骸进传导图：两块不相连的水洼，中间的残骸把电传过去；复合材质（导电 0.2 < 0.3）不传
            short composite = EmgEntityProfiles.ForEnemy("material.composite", false);
            float Bridge(bool wreck, short profile)
            {
                using CombatKernel k = NewKernel(true);
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 2f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(7, 0), 2f);
                if (wreck)
                {
                    AddWreckOf(k, new double2(3.5, 0), profile, 0f, 1.8f);
                }
                StepN(k, 1);
                int far = -1;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Water && z.Pos.x > 3)
                    {
                        far = z.EmgId;
                    }
                }
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 5f);
                float best = 0f;
                for (int i = 0; i < 30; i++)
                {
                    StepN(k, 1);
                    best = k.TryGetPatchEnergy(far, out _, out float ch, out _) ? math.max(best, ch) : best;
                }
                return best;
            }
            float steelBridge = Bridge(true, -1), noBridge = Bridge(false, -1), compositeBridge = Bridge(true, composite);
            Expect(steelBridge > 0f && noBridge == 0f && compositeBridge == 0f, $"H5 可导电：钢残骸把电传到隔开的水洼（{steelBridge:0.00}），没有残骸 / 复合残骸传不过去（{noBridge} / {compositeBridge}）");
            // 残骸点燃
            float HpOf(CombatKernel k, int id) => k.TryGetWreck(k.WreckIndexOf(id), out EmgWreck w) ? w.Hp : 0f;
            using (CombatKernel k = NewKernel(true))
            {
                int a = AddWreckOf(k, new double2(0, 0), composite), near = AddWreckOf(k, new double2(1.2, 0), composite), far = AddWreckOf(k, new double2(30, 0), composite), steel = AddWreck(k, new double2(60, 0));
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 0.5f, 1f, 0.5f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(60, 0), 1f, 1f, 2f);
                StepN(k, 60);
                float nearAt1 = HpOf(k, near);
                Expect(HpOf(k, a) < 40f && HpOf(k, a) > 30f && HpOf(k, far) == 40f && HpOf(k, steel) == 40f, $"H6 可点燃：复合残骸碰到明火就着、掉耐久（{HpOf(k, a):0.0} / 40），钢残骸与没碰到火的不受影响");
                StepN(k, 60 * 3);
                Expect(k.WreckFires >= 1 && HpOf(k, near) < nearAt1 && HpOf(k, far) == 40f, $"H7 起火的残骸脚下起明火、引燃贴着的残骸（起火 {k.WreckFires} 次，相邻 {HpOf(k, near):0.0} / 40）");
                StepN(k, 60 * 14);
                Expect(k.WreckIndexOf(a) < 0 && k.WreckIndexOf(near) < 0 && HpOf(k, far) == 40f && HpOf(k, steel) == 40f, "H8 烧到耐久归零就没了（被摧毁）；火不会自己蔓延到隔得远的残骸");
            }
            using (CombatKernel k = NewKernel(true))
            {
                int a = AddWreckOf(k, new double2(0, 0), composite);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 1f, 1f, 1f);
                StepN(k, 60);
                k.TryGetWreck(k.WreckIndexOf(a), out EmgWreck lit);
                bool burning = k.WreckBurning(lit);
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 3f);
                StepN(k, 60);
                float hp1 = HpOf(k, a);
                StepN(k, 120);
                k.TryGetWreck(k.WreckIndexOf(a), out EmgWreck wet);
                Expect(burning && !k.WreckBurning(wet) && hp1 == HpOf(k, a) && hp1 > 0f, $"H9 水能浇灭：耐久不再下降（{hp1:0.0}）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                int a = AddWreckOf(k, new double2(0, 0), composite, 200f);
                int b = AddWreck(k, new double2(9, 0));
                k.TryGetWreck(k.WreckIndexOf(b), out EmgWreck steelHot);
                steelHot.Temp = 200f;
                k.SetWreckForTests(k.WreckIndexOf(b), steelHot);
                StepN(k, 60);
                Expect(HpOf(k, a) < 40f && k.WreckFires == 1 && Tally(k, EmgMatterConst.Fire) == 1 && HpOf(k, b) == 40f, "H10 热达燃点（导线传来 / 带热阵亡）也会着，场上没有斑块也照常烧；钢残骸再热也不着");
            }
        }

        // ── I 内核存读档 ─────────────────────────────────────────────────

        private static void CheckKernelSave()
        {
            using CombatKernel a = NewKernel(true);
            byte[] matters = { EmgMatterConst.Cured, EmgMatterConst.Slag, EmgMatterConst.Ice, EmgMatterConst.Glass };
            for (int i = 0; i < matters.Length; i++)
            {
                a.SpawnPatch(matters[i], new double2(i * 12, 30), 2f);
            }
            AddWreck(a, new double2(5, 5));
            AddWreck(a, new double2(9, 5));
            AddWreckOf(a, new double2(5, 40), EmgEntityProfiles.ForEnemy("material.composite", false), 200f); // 烧着的：I2 连带验证燃烧状态往返确定性
            StepN(a, 3);
            a.ClearPersist(EmgPersistItem.KindWreck, 1);
            StepN(a, 3);
            byte[] data = a.Serialize();
            ulong h0 = a.StateHash();
            using CombatKernel b = NewKernel(true);
            CombatLoadResult r = b.Load(data);
            Expect(r == CombatLoadResult.Ok && b.StateHash() == h0 && b.PersistTally() == a.PersistTally() && b.PersistCounts == a.PersistCounts,
                $"I1 FGT-EMG-040：存读档 StateHash 一致，残骸 / 固态不复制不丢失（{a.PersistTally()}），计数往返（{a.PersistCounts}）");
            StepN(a, 120);
            StepN(b, 120);
            Expect(a.StateHash() == b.StateHash() && b.PersistSignature() == a.PersistSignature(), "I2 读档后接着跑 2 秒与不存档一致");
            byte[] old = a.SerializeEmgShapeForTests(EmgConst.SegScalars, 23, 0);
            using CombatKernel c = NewKernel(true);
            Expect(c.Load(old) == CombatLoadResult.Ok && c.PersistCounts.Capped == 0 && c.PersistCounts.Cleared == 0 && c.PersistTally() == a.PersistTally(),
                "I3 旧档（标量段 23 字段，EMG2-REACT-02 之前）读入：新计数取 0，留存物照常（格式 17 不升版，只尾部追加）");
            int fires = a.WreckFires;
            ulong h1 = a.StateHash();
            using CombatKernel d = NewKernel(true);
            Expect(fires > 0 && d.Load(a.Serialize()) == CombatLoadResult.Ok && d.WreckFires == fires && d.StateHash() == h1,
                $"I4 残骸起火计数随快照往返（标量段第 27 字段，{fires} 次）");
            using CombatKernel e = NewKernel(true);
            Expect(e.Load(a.SerializeEmgShapeForTests(EmgConst.SegScalars, 26, 0)) == CombatLoadResult.Ok && e.WreckFires == 0 && e.PersistCounts == a.PersistCounts && e.StateHash() != h1,
                "I5 标量段 26 字段的短档读入：起火计数取 0、其余计数照常，且哈希覆盖这个字段（取 0 后哈希变了）");
        }

        // ── J 地点口径 ───────────────────────────────────────────────────

        private static void CheckSiteScope()
        {
            CampaignState s = NewWorld(21001);
            bool home = CombatSites.Get(HomeValleyLayout.RegionId).Kernel.PersistSite;
            CombatSite other = CombatSites.Open("emg-persist-away", s, false, new InteriorCombatRules(), out _);
            bool away = other.Kernel.PersistSite;
            CombatSites.Close("emg-persist-away", s, dropRecord: true);
            Expect(home && !away, "J1 T1 核实结论落地：家园内核长期留存；其它地点（远征 / 室内，派遣时重建）只按野外寿命");
            // 己方前哨不是单独内核（FG8-OUT-01），远处的残骸同样在家园内核里留存
            int far = AddWreck(HomeKernel, new double2(320, 320));
            WorldSimulation.StepMany(60 * 100);
            Expect(HomeKernel.TryGetPersist(EmgPersistItem.KindWreck, far, out _), "J2 前哨距离（核心 300 格外）的残骸在家园内核里同样长期留存，100 游戏秒后还在");
        }

        // ── K 清除工作单（走建造模式真实入口）──────────────────────────────────────

        private static GridCell Clear(CampaignState s, GridCell at)
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            mode.Open();
            mode.SetClearSiteMode(true);
            mode.PointerDown(s, at);
            mode.PointerUp(s, at);
            mode.SetClearSiteMode(false);
            mode.Close();
            return at;
        }

        private static string Box(CampaignState s, GridCell a, GridCell b)
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            mode.Open();
            mode.SetClearSiteMode(true);
            mode.PointerDown(s, a);
            mode.SetHover(s, b);
            mode.RefreshPreview(s);
            bool preview = mode.ClearPlan != null && mode.ClearPlan.Cells.Count > 0;
            mode.PointerUp(s, b);
            string status = mode.StatusText;
            mode.SetClearSiteMode(false);
            mode.Close();
            return (preview ? "preview:" : "nopreview:") + status;
        }

        private static GridCell? Open(CampaignState s, int w = 9, int h = 9, float from = 14f, float to = 30f) => FgProductionSelfCheck.FindArea(s, w, h, from, to);

        private static void CheckOrders()
        {
            CampaignState s = NewWorld(21101, scrap: 100);
            GridCell? area = Open(s);
            if (area == null)
            {
                Fail("K 找不到空地");
                return;
            }
            GridCell o = area.Value;
            CombatKernel k = HomeKernel;
            int a = AddWreck(k, new double2(o.X + 1, o.Y + 1)), b = AddWreck(k, new double2(o.X + 3, o.Y + 1)), c = AddWreck(k, new double2(o.X + 5, o.Y + 1));
            string ta = SitePersistService.TargetOf(EmgPersistItem.KindWreck, a), tb = SitePersistService.TargetOf(EmgPersistItem.KindWreck, b), tc = SitePersistService.TargetOf(EmgPersistItem.KindWreck, c);
            int cost = SitePersistService.ClearScrap;
            int before = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            string boxText = Box(s, new GridCell(o.X, o.Y), new GridCell(o.X + 6, o.Y + 2));
            int reserved = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            WorkOrderRecord oa = SitePersistService.ActiveOrder(s, ta);
            Expect(oa != null && SitePersistService.ActiveOrder(s, tb) != null && SitePersistService.ActiveOrder(s, tc) != null && oa.Kind == WorkOrderKind.Salvage
                   && before - reserved == cost * 3 && boxText.StartsWith("preview:") && boxText.Contains("3"),
                $"K1 B02 框选批量：拖框 3 件各开一张工作单（复用工作单与优先级），预留废料 {before} → {reserved}，有框预览，状态行“{boxText}”");
            // 取消：点已有单子的那一件
            Clear(s, new GridCell(o.X + 1, o.Y + 1));
            int afterCancel = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            Expect(SitePersistService.ActiveOrder(s, ta) == null && afterCancel - reserved == cost && k.TryGetPersist(EmgPersistItem.KindWreck, a, out _),
                $"K2 B03 执行前取消：点一下已下单的那件，单子取消，废料全额退回（{reserved} → {afterCancel}），残骸还在");
            // 完工：机器真去干
            bool done = FgProductionSelfCheck.StepUntil(() => !k.TryGetPersist(EmgPersistItem.KindWreck, b, out _) && !k.TryGetPersist(EmgPersistItem.KindWreck, c, out _), 240);
            int afterDone = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            Expect(done && afterDone <= afterCancel && k.TryGetPersist(EmgPersistItem.KindWreck, a, out _) && k.PersistCounts.Cleared == 2,
                $"K3 空闲机器按优先级领单、到现场干完：两块残骸消失（清除 {k.PersistCounts.Cleared}），取消的那块还在（废料 {afterCancel} → {afterDone}，预留已消耗不再扣）");
            // 目标先没了：全额退
            int d = AddWreck(k, new double2(o.X + 7, o.Y + 7));
            string td = SitePersistService.TargetOf(EmgPersistItem.KindWreck, d);
            // 单子进池后立刻把目标拆掉（回收修复 / 被打掉同一口径）
            int s0 = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            HomeValleyWorkOrders.WorkOrderOpResult rd = SitePersistService.OrderClear(s, td);
            k.ClearPersist(EmgPersistItem.KindWreck, d);
            SitePersistService.Check(s);
            WorkOrderRecord od = HomeValleyWorkOrders.Find(s, rd.WorkOrderId);
            Expect(rd.Success && od != null && od.State == WorkOrderState.Cancelled && HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId)) == s0,
                $"K4 负向：单子还没开工目标就没了 → 单子取消、废料全额退回（{s0}），不白扣");
            // 没料也能清（B11）
            CampaignState t = NewWorld(21102, scrap: 0);
            GridCell? t0 = Open(t);
            CombatKernel tk = HomeKernel;
            int e = AddWreck(tk, new double2(t0.Value.X + 2, t0.Value.Y + 2));
            HomeValleyWorkOrders.WorkOrderOpResult re = SitePersistService.OrderClear(t, SitePersistService.TargetOf(EmgPersistItem.KindWreck, e));
            bool cleanedBroke = re.Success && FgProductionSelfCheck.StepUntil(() => !tk.TryGetPersist(EmgPersistItem.KindWreck, e, out _), 240);
            Expect(cleanedBroke, "K5 B11 保底：废料为 0 也能下单并完工（堵路不会因为没料清不掉）");
            // 工作面板读法 + 空框
            WorkOrderRecord anyOrder = HomeValleyWorkOrders.Find(t, re.WorkOrderId);
            CampaignState u = NewWorld(21103, scrap: 50);
            GridCell? u0 = Open(u);
            string empty = Box(u, new GridCell(u0.Value.X, u0.Value.Y), new GridCell(u0.Value.X + 1, u0.Value.Y + 1));
            Expect(HomeValleyAlarms.WorkKindName(anyOrder) == GameText.Get("emg.persist.work_kind") && empty.Contains(GameText.Get("emg.persist.clear_none")),
                $"K6 工单种类写“{HomeValleyAlarms.WorkKindName(anyOrder)}”；框里什么也没有 → 写明原因（B06：“{empty}”）");
            // 悬停
            int hw = AddWreck(HomeKernel, new double2(u0.Value.X + 2, u0.Value.Y + 2));
            CombatSite homeSite = CombatSites.Get(HomeValleyLayout.RegionId);
            TooltipContent tip = EmgChainView.WreckContent(homeSite, u, HomeKernel.WreckIndexOf(hw));
            SitePersistService.OrderClear(u, SitePersistService.TargetOf(EmgPersistItem.KindWreck, hw));
            TooltipContent tip2 = EmgChainView.WreckContent(homeSite, u, HomeKernel.WreckIndexOf(hw));
            Expect(tip.Body.Contains("清除场地物") && tip2.Body.Contains("已下清除工作单"), "K7 悬停写明“长期留存、怎么清除”；已下单时写“已下单、再点取消”");
            // 工单目标显示名（机器详情“当前：…”、到不了的通知都读它）：不露内部 ID
            HomeKernel.SpawnPatch(EmgMatterConst.Cured, new double2(u0.Value.X + 6, u0.Value.Y + 6), 1.5f);
            WorldSimulation.StepMany(2);
            string gelTarget = SitePersistService.TargetOf(EmgPersistItem.KindPatch, FirstOf(HomeKernel, EmgMatterConst.Cured).EmgId);
            SitePersistService.OrderClear(u, gelTarget);
            string wreckName = HomeValleyWorkOrders.DescribeTarget(u, SitePersistService.ActiveOrder(u, SitePersistService.TargetOf(EmgPersistItem.KindWreck, hw)));
            string gelName = HomeValleyWorkOrders.DescribeTarget(u, SitePersistService.ActiveOrder(u, gelTarget));
            Expect(wreckName.Contains("残骸") && gelName.Contains("固化胶块") && !wreckName.Contains(SitePersistService.TargetPrefix) && !gelName.Contains(SitePersistService.TargetPrefix),
                $"K8 B06 工单目标写“{wreckName}”“{gelName}”，不露内部 ID");
            int burner = AddWreckOf(HomeKernel, new double2(u0.Value.X + 2, u0.Value.Y + 6), EmgEntityProfiles.ForEnemy("material.composite", false), 200f);
            Expect(EmgChainView.WreckContent(homeSite, u, HomeKernel.WreckIndexOf(burner)).Body.Contains("正在燃烧"), "K9 悬停写明残骸正在燃烧");
        }

        // ── L 堵路警告 ───────────────────────────────────────────────────

        private static void Ring(CombatKernel k, GridCell center, int r, bool wrecks, double step = 0)
        {
            step = step > 0 ? step : wrecks ? 1.0 : 1.5;
            for (double a = -r; a < r; a += step)
            {
                Put(center.X + a, center.Y - r);
                Put(center.X + r, center.Y + a);
                Put(center.X - a, center.Y + r);
                Put(center.X - r, center.Y - a);
            }
            void Put(double x, double y)
            {
                if (wrecks)
                {
                    AddWreck(k, new double2(x, y), 0.7f);
                }
                else
                {
                    // 固化胶块是规则产物：斑块生成入口直接放（规则路径由 FgEmgReactSelfCheck 验）
                    k.SpawnPatch(EmgMatterConst.Cured, new double2(x, y), 1.5f);
                }
            }
        }

        private static void CheckBlockWarning()
        {
            CampaignState s = NewWorld(21201, scrap: 200);
            GridCell? area = Open(s, 3, 3, 16f, 40f);
            if (area == null)
            {
                Fail("L 找不到空地");
                return;
            }
            GridCell c = new GridCell(area.Value.X + 1, area.Value.Y + 1);
            BuildingRecord store = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "persist-store", c);
            WorldSimulation.StepMany(5);
            CombatKernel k = HomeKernel;
            SitePersistService.Check(s);
            SoftlockState st = SoftlockService.StateOf(s);
            int floods0 = SitePersistService.Floods;
            Expect(st.PersistBlocks.Length == 0 && NotifyCount(SitePersistService.NotifyBlocked) == 0, "L1 负向：什么都没堵时不警告");
            // EMG2-E2E-01：框外（远处野地）的残骸堵不到框内的路，不用泛洪（后期基地施工高峰曾被这一次泛洪拖慢）
            AddWreck(k, new double2(c.X + 400, c.Y + 400));
            WorldSimulation.StepMany(2);
            SitePersistService.Check(s);
            Expect(SitePersistService.Floods == floods0 && st.PersistBlocks.Length == 0, $"L1b 框外的残骸不泛洪（{SitePersistService.Floods - floods0} 次）、不警告");
            // 一圈固化胶块围住仓库（核心到仓库唯一通路被堵死）
            Ring(k, c, 5, wrecks: false);
            WorldSimulation.StepMany(240); // 每步生成额度 32，顺延铺完
            SitePersistService.Check(s);
            PersistBlockRecord rec = st.PersistBlocks.FirstOrDefault(r => r.Key == store.BuildingId);
            HomeValleyAlarms.AlertRecord alarm = HomeValleyAlarms.Collect(s).FirstOrDefault(a => a.Key == "persist:" + store.BuildingId);
            Expect(rec != null && rec.Blocks && NotifyCount(SitePersistService.NotifyBlocked) >= 1 && alarm.Key != null && alarm.HasLocation,
                $"L2 固化胶块堵死核心与仓库唯一通路：警告级通知（带位置）、告警栏可定位、记录写明位置（{rec?.X:0.0},{rec?.Y:0.0}）");
            int notes = NotifyCount(SitePersistService.NotifyBlocked);
            int floods1 = SitePersistService.Floods;
            SitePersistService.Check(s);
            SitePersistService.Check(s);
            Expect(SitePersistService.Floods == floods1 && NotifyCount(SitePersistService.NotifyBlocked) == notes && floods1 > floods0, "L3 没变化不重算、不重复通知（泛洪只在留存物 / 建筑变了时）");
            // 打通一块：警告解除（清掉所有胶块最直接；真实清除路径）
            var items = new List<EmgPersistItem>();
            k.CollectPersist(items);
            foreach (EmgPersistItem it in items.Where(i => i.Blocks))
            {
                k.ClearPersist(it.Kind, it.Id);
            }
            WorldSimulation.StepMany(2);
            SitePersistService.Check(s);
            Expect(st.PersistBlocks.Length == 0 && !HomeValleyAlarms.Collect(s).Any(a => a.Key.StartsWith("persist:")), "L4 清除后下一次检查警告与告警消失");
            // 残骸堆满出入口：警告但能减速穿过
            CampaignState w = NewWorld(21202, scrap: 200);
            CombatKernel wk = HomeKernel;
            HomeGridService.TryGetCoreBounds(w, out GridCell coreMin, out GridCell coreMax);
            var core = new GridCell((coreMin.X + coreMax.X) / 2, (coreMin.Y + coreMax.Y) / 2);
            int r2 = Math.Max(coreMax.X - coreMin.X, coreMax.Y - coreMin.Y) / 2 + 6;
            Ring(wk, core, r2, wrecks: true);
            WorldSimulation.StepMany(2);
            SitePersistService.Check(w);
            SoftlockState wst = SoftlockService.StateOf(w);
            PersistBlockRecord exit = wst.PersistBlocks.FirstOrDefault(r => r.Key == SitePersistService.ExitKey);
            bool passable = exit != null && NavService.PassableNow(NavService.CellOf(exit.X, exit.Y).X, NavService.CellOf(exit.X, exit.Y).Y, NavConst.ClassPlayer);
            bool textOk = NotificationCenter.History.Any(e => e.Type?.Id == SitePersistService.NotifyBlocked && e.Members.Any(m => (m.DetailText ?? string.Empty).Contains("减速")));
            Expect(exit != null && !exit.Blocks && passable && textOk, $"L5 残骸堆满家园出入口（一圈 {wk.WreckCount} 块）：警告 + 标位置，寻路镜像仍可走（单位减速穿过，不是软锁）");
            // 留一个缺口：不算堵
            CampaignState g = NewWorld(21203, scrap: 200);
            CombatKernel gk = HomeKernel;
            HomeGridService.TryGetCoreBounds(g, out coreMin, out coreMax);
            core = new GridCell((coreMin.X + coreMax.X) / 2, (coreMin.Y + coreMax.Y) / 2);
            for (double a = -r2; a < r2 - 3; a += 1.0)
            {
                AddWreck(gk, new double2(core.X + a, core.Y - r2), 0.7f);
            }
            WorldSimulation.StepMany(2);
            SitePersistService.Check(g);
            Expect(SoftlockService.StateOf(g).PersistBlocks.Length == 0, "L6 负向：残骸没有把路堵死（还有别的通路）→ 不警告");
        }

        // ── L（续）厚墙、贴身包围、地形负向 ─────────────────────────────────

        private static void WallCells(GridCell a, GridCell b, Action<int, int> put)
        {
            for (int x = a.X - 1; x <= b.X + 1; x++)
            {
                put(x, a.Y - 1);
                put(x, b.Y + 1);
            }
            for (int y = a.Y; y <= b.Y; y++)
            {
                put(a.X - 1, y);
                put(b.X + 1, y);
            }
        }

        private static void CheckBlockThick()
        {
            (CampaignState s, SoftlockState st, GridCell min, GridCell max, GridCell core, int r) World(int seed)
            {
                CampaignState w = NewWorld(seed, scrap: 200);
                HomeGridService.TryGetCoreBounds(w, out GridCell a, out GridCell b);
                return (w, SoftlockService.StateOf(w), a, b, new GridCell((a.X + b.X) / 2, (a.Y + b.Y) / 2), Math.Max(b.X - a.X, b.Y - a.Y) / 2 + 5);
            }
            bool Located(CampaignState w, PersistBlockRecord rec) =>
                rec != null && HomeValleyAlarms.Collect(w).FirstOrDefault(x => x.Key == "persist:" + rec.Key).HasLocation;
            // 两层、三层残骸墙（单件两侧都碰不到）
            var two = World(21204);
            Ring(HomeKernel, two.core, two.r, wrecks: true);
            Ring(HomeKernel, two.core, two.r + 3, wrecks: true);
            WorldSimulation.StepMany(2);
            SitePersistService.Check(two.s);
            PersistBlockRecord twoRec = two.st.PersistBlocks.FirstOrDefault(x => x.Key == SitePersistService.ExitKey);
            Expect(twoRec != null && !twoRec.Blocks && Located(two.s, twoRec), $"L7 两层残骸墙（{HomeKernel.WreckCount} 块，单件两侧都碰不到）：照样警告并标位置（{twoRec?.X:0.0},{twoRec?.Y:0.0}）");
            var three = World(21205);
            Ring(HomeKernel, three.core, three.r, wrecks: true);
            Ring(HomeKernel, three.core, three.r + 2, wrecks: true);
            Ring(HomeKernel, three.core, three.r + 4, wrecks: true);
            WorldSimulation.StepMany(2);
            SitePersistService.Check(three.s);
            Expect(three.st.PersistBlocks.Any(x => x.Key == SitePersistService.ExitKey), $"L8 三层残骸墙（{HomeKernel.WreckCount} 块）：照样警告");
            // 固化胶块两层围住仓库
            var gel = World(21206);
            GridCell? ga = Open(gel.s, 3, 3, 16f, 40f);
            GridCell gc = new GridCell(ga.Value.X + 1, ga.Value.Y + 1);
            BuildingRecord gs = FgProductionSelfCheck.Built(gel.s, HomeValleyLayout.BuildingTypeWarehouse, "persist-store2", gc);
            WorldSimulation.StepMany(5);
            Ring(HomeKernel, gc, 4, wrecks: false, step: 2.5);
            Ring(HomeKernel, gc, 7, wrecks: false, step: 2.5);
            WorldSimulation.StepMany(240);
            SitePersistService.Check(gel.s);
            PersistBlockRecord gelRec = gel.st.PersistBlocks.FirstOrDefault(x => x.Key == gs.BuildingId);
            Expect(gelRec != null && gelRec.Blocks && Located(gel.s, gelRec), $"L9 固化胶块两层围住仓库（{Tally(HomeKernel, EmgMatterConst.Cured)} 块）：照样警告（胶块在寻路镜像里本来就不通，基线要掀开它才判得出来）");
            // 核心贴身被盖满：最严重的软锁，不能把已有警告清掉
            var wrap = World(21207);
            Ring(HomeKernel, wrap.core, wrap.r + 1, wrecks: true);
            WorldSimulation.StepMany(2);
            SitePersistService.Check(wrap.s);
            bool warned = wrap.st.PersistBlocks.Any(x => x.Key == SitePersistService.ExitKey);
            CombatKernel wk = HomeKernel;
            WallCells(wrap.min, wrap.max, (x, y) => AddWreck(wk, new double2(x, y)));
            WorldSimulation.StepMany(2);
            SitePersistService.Check(wrap.s);
            PersistBlockRecord wrapRec = wrap.st.PersistBlocks.FirstOrDefault(x => x.Key == SitePersistService.ExitKey);
            bool nearCore = wrapRec != null && Math.Abs(wrapRec.X - wrap.core.X) <= wrap.r && Math.Abs(wrapRec.Y - wrap.core.Y) <= wrap.r;
            Expect(warned && wrapRec != null && nearCore && Located(wrap.s, wrapRec), $"L10 核心贴身被残骸盖满：警告不清掉，位置标在核心旁（{wrapRec?.X:0.0},{wrapRec?.Y:0.0}）");
            // 仓库贴身被盖满（没有工作单也要报）
            var cover = World(21208);
            GridCell? ca = Open(cover.s, 3, 3, 16f, 40f);
            GridCell cc = new GridCell(ca.Value.X + 1, ca.Value.Y + 1);
            BuildingRecord cs = FgProductionSelfCheck.Built(cover.s, HomeValleyLayout.BuildingTypeWarehouse, "persist-store3", cc);
            WorldSimulation.StepMany(5);
            GridContent.TryGetBuilding(cs.BuildingTypeId, out BuildingGrid cg);
            GridMath.FootprintBounds(new GridCell(cs.GridX, cs.GridY), cg.FootprintW, cg.FootprintH, GridMath.NormalizeRotation(cs.Rotation), out GridCell cmin, out GridCell cmax);
            CombatKernel ck = HomeKernel;
            WallCells(cmin, cmax, (x, y) => AddWreck(ck, new double2(x, y)));
            WorldSimulation.StepMany(2);
            SitePersistService.Check(cover.s);
            PersistBlockRecord coverRec = cover.st.PersistBlocks.FirstOrDefault(x => x.Key == cs.BuildingId);
            Expect(coverRec != null && Math.Abs(coverRec.X - cc.X) <= cmax.X - cmin.X + 3 && Math.Abs(coverRec.Y - cc.Y) <= cmax.Y - cmin.Y + 3 && Located(cover.s, coverRec),
                $"L11 仓库贴身被残骸盖满、没有任何工作单：照样警告，位置标在仓库旁（{coverRec?.X:0.0},{coverRec?.Y:0.0}）");
            // 负向：围墙（建筑）围死的仓库，胶块压在墙上：去掉胶块也到不了，不归留存物
            var wall = World(21209);
            GridCell? wa = Open(wall.s, 5, 5, 16f, 40f);
            GridCell wc = new GridCell(wa.Value.X + 2, wa.Value.Y + 2);
            BuildingRecord ws = FgProductionSelfCheck.Built(wall.s, HomeValleyLayout.BuildingTypeWarehouse, "persist-store4", wc);
            GridContent.TryGetBuilding(ws.BuildingTypeId, out BuildingGrid wg);
            GridMath.FootprintBounds(new GridCell(ws.GridX, ws.GridY), wg.FootprintW, wg.FootprintH, GridMath.NormalizeRotation(ws.Rotation), out GridCell wmin, out GridCell wmax);
            int wn = 0;
            CampaignState wallState = wall.s;
            WallCells(wmin, wmax, (x, y) => FgProductionSelfCheck.Built(wallState, "barrier_t1", "persist-wall" + wn++, new GridCell(x, y)));
            WorldSimulation.StepMany(5);
            HomeKernel.SpawnPatch(EmgMatterConst.Cured, new double2(wmin.X - 1, wmin.Y - 1), 1.5f);
            WorldSimulation.StepMany(3);
            SitePersistService.Check(wall.s);
            bool walled = !NavService.PassableNow(wmin.X - 1, wmax.Y + 1, NavConst.ClassPlayer);
            Expect(walled && !wall.st.PersistBlocks.Any(x => x.Key == ws.BuildingId), "L12 负向：建筑围死的仓库 + 胶块压在墙上：去掉胶块也到不了，不算场地物堵路（基线只掀开胶块底下本来能走的格）");
            // 残骸被推动：签名变了才会重算（位置也跟着更新）
            CombatKernel sk = HomeKernel;
            int sw = AddWreck(sk, new double2(wa.Value.X - 20, wa.Value.Y));
            uint sig = sk.PersistSignature();
            sk.TryGetWreck(sk.WreckIndexOf(sw), out EmgWreck moved);
            moved.Pos.x += 3;
            sk.SetWreckForTests(sk.WreckIndexOf(sw), moved);
            Expect(sk.PersistSignature() != sig, "L13 残骸被推动换了格：堵路签名跟着变（滑进隘口会重算，推开了会解除）");
            // 崩解通知：涨了通知一次；旧档（−1）首次只记基线
            var cap = World(21210);
            SitePersistService.Check(cap.s);
            int n0 = NotifyCount(SitePersistService.NotifyCollapsed);
            CombatKernel ck2 = HomeKernel;
            for (int i = 0; i < HomeRow(EmgMatterConst.Slag).Quota + 3; i++)
            {
                ck2.SpawnPatch(EmgMatterConst.Slag, new double2(cap.core.X + 20 + i % 10 * 5, cap.core.Y + 20 + i / 10 * 5), 1f);
            }
            WorldSimulation.StepMany(10);
            SitePersistService.Check(cap.s);
            int n1 = NotifyCount(SitePersistService.NotifyCollapsed);
            Expect(ck2.PersistCounts.Capped > 0 && n1 > n0, $"L14 超软上限崩解：通知（崩解 {ck2.PersistCounts.Capped} 件）");
            cap.st.PersistCappedSeen = -1;
            SitePersistService.Check(cap.s);
            Expect(NotifyCount(SitePersistService.NotifyCollapsed) == n1 && cap.st.PersistCappedSeen == CombatSites.Get(HomeValleyLayout.RegionId).PersistCappedTotal, "L15 旧档首次检查只记基线，不补发历史崩解通知");
        }

        // ── M 寻路镜像 ───────────────────────────────────────────────────

        private static void CheckNavMirror()
        {
            CampaignState s = NewWorld(21301, scrap: 100);
            GridCell? area = Open(s);
            GridCell c = new GridCell(area.Value.X + 4, area.Value.Y + 4);
            CombatKernel k = HomeKernel;
            bool before = NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer);
            k.SpawnPatch(EmgMatterConst.Cured, new double2(c.X, c.Y), 3f);
            WorldSimulation.StepMany(3);
            bool blocked = !NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer);
            bool edgeFree = NavService.PassableNow(c.X + 4, c.Y, NavConst.ClassPlayer);
            bool slagFree;
            k.SpawnPatch(EmgMatterConst.Slag, new double2(c.X + 10, c.Y), 2f);
            WorldSimulation.StepMany(3);
            slagFree = NavService.PassableNow(c.X + 10, c.Y, NavConst.ClassPlayer);
            Expect(before && blocked && edgeFree && slagFree, "M1 固化胶块生成：覆盖格进寻路镜像，圈外照常；冷渣只减速不挡路（FG19 第 5.3 节，DEBT-EMG2MAT01-06）");
            // 区块被格网变化重推，覆盖格不丢
            NavService.SyncDynamicBlocks(new List<float3> { new float3(c.X, c.Y, 3f) });
            bool stillBlocked = !NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer);
            k.ClearPersist(EmgPersistItem.KindPatch, FirstOf(k, EmgMatterConst.Cured).EmgId);
            WorldSimulation.StepMany(3);
            Expect(stillBlocked && NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer), "M2 胶块消失：覆盖格恢复可走（区块重算 + 覆盖层，不残留）");
        }

        // ── N 真文件存读档 ─────────────────────────────────────────────────

        private static string Digest(CampaignState s)
        {
            CombatKernel k = HomeKernel;
            var items = new List<EmgPersistItem>();
            k.CollectPersist(items);
            SoftlockState st = SoftlockService.StateOf(s);
            return string.Join(",", items.Select(i => $"{i.Kind}:{i.Id}:{i.Matter}")) + $"|orders:{(s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o != null && SitePersistService.IsClearTarget(o.TargetId) && HomeValleyWorkOrders.IsActive(o))}"
                   + $"|blocks:{st.PersistBlocks.Length}|cap:{k.PersistCounts.Capped}|clr:{k.PersistCounts.Cleared}|scrap:{HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId))}";
        }

        private static void CheckWorldSave()
        {
            CampaignState s = NewWorld(21401, scrap: 100);
            GridCell? area = Open(s);
            GridCell c = new GridCell(area.Value.X + 4, area.Value.Y + 4);
            CombatKernel k = HomeKernel;
            k.SpawnPatch(EmgMatterConst.Cured, new double2(c.X, c.Y), 3f);
            k.SpawnPatch(EmgMatterConst.Slag, new double2(c.X + 8, c.Y), 2f);
            int w = AddWreck(k, new double2(c.X - 6, c.Y));
            WorldSimulation.StepMany(3);
            SitePersistService.OrderClear(s, SitePersistService.TargetOf(EmgPersistItem.KindWreck, w));
            int stockReserved = HomeInventory.Stock(s, ItemCatalog.Find(ItemCatalog.ScrapId));
            WorldSimulation.SyncAllForSave();
            string before = Digest(s);
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("N 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NotificationCenter.Bind(rr.State);
            string loaded = Digest(rr.State);
            WorldSimulation.StepMany(2);
            bool replay = !NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer);
            WorkOrderRecord o = (rr.State.WorkOrders ?? Array.Empty<WorkOrderRecord>()).FirstOrDefault(x => x != null && SitePersistService.IsClearTarget(x.TargetId) && HomeValleyWorkOrders.IsActive(x));
            Expect(save.Success && loaded == before && o != null && o.ReservedItemAmount == SitePersistService.ClearScrap,
                $"N1 真文件存读档：残骸、固态斑块、清除单与预留材料往返一致，不复制不丢失（{before}）");
            Expect(replay, "N2 读档后第一步重放阻路斑块进寻路镜像（覆盖格不进存档，由快照推出）");
            int back = HomeInventory.Stock(rr.State, ItemCatalog.Find(ItemCatalog.ScrapId));
            SitePersistService.CancelClear(rr.State, o.TargetId);
            Expect(HomeInventory.Stock(rr.State, ItemCatalog.Find(ItemCatalog.ScrapId)) - back == SitePersistService.ClearScrap && stockReserved == back, "N3 读档后取消同样全额返还");
            // 旧档没有堵路字段
            string json = JsonUtility.ToJson(rr.State.Economy.Softlock).Replace("\"PersistBlocks\":", "\"PersistBlocksAbsent\":").Replace("\"PersistCappedSeen\":", "\"PersistCappedAbsent\":");
            SoftlockState legacy = JsonUtility.FromJson<SoftlockState>(json);
            Expect(legacy.PersistBlocks != null && legacy.PersistBlocks.Length == 0 && legacy.PersistCappedSeen == -1, "N4 v2 旧档（没有新字段）读入取缺省：空列表、−1 = 首次检查只记基线（不升 schema）");
            // 时钟不走时反复下单 / 取消再存读档：重复工单 ID 会让读档整档拒绝
            CampaignState p = NewWorld(21402, scrap: 100);
            GridCell? pa = Open(p);
            GridCell pc = new GridCell(pa.Value.X + 2, pa.Value.Y + 2);
            string pt = SitePersistService.TargetOf(EmgPersistItem.KindWreck, AddWreck(HomeKernel, new double2(pc.X, pc.Y)));
            int stock0 = HomeInventory.Stock(p, ItemCatalog.Find(ItemCatalog.ScrapId));
            long tick = GameClock.Ticks;
            Clear(p, pc);
            string firstId = SitePersistService.ActiveOrder(p, pt)?.WorkOrderId;
            Clear(p, pc);
            bool refunded = SitePersistService.ActiveOrder(p, pt) == null && HomeInventory.Stock(p, ItemCatalog.Find(ItemCatalog.ScrapId)) == stock0;
            Clear(p, pc);
            string secondId = SitePersistService.ActiveOrder(p, pt)?.WorkOrderId;
            Expect(tick == GameClock.Ticks && firstId != null && secondId != null && firstId != secondId && refunded, $"N5 时钟不走时下单 → 取消 → 再下单：两张工单 ID 不同（{firstId} / {secondId}），取消全额退回");
            WorldSimulation.SyncAllForSave();
            SaveResult ps = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult pr = CampaignRestoreOrchestrator.Restore(Slot);
            Expect(ps.Success && pr.Success, $"N6 这样的存档读得回来（{pr.Message}）");
            if (!pr.Success)
            {
                return;
            }
            CampaignSession.Set(Slot, pr.State);
            HomeValleyController ph = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(ph.SiteId);
            NotificationCenter.Bind(pr.State);
            Clear(pr.State, pc);
            Expect(SitePersistService.ActiveOrder(pr.State, pt) == null && HomeInventory.Stock(pr.State, ItemCatalog.Find(ItemCatalog.ScrapId)) == stock0, "N7 读档后再点：活单真的取消，废料全额退回（不会命中已取消的同名单）");
        }

        // ── O 离家报告（B19）──────────────────────────────────────────────

        private static void CheckRaidReport()
        {
            CampaignState s = NewWorld(21501, scrap: 100);
            CombatKernel k = HomeKernel;
            var g = new TransitGroupRecord { GroupId = "persist-raid", PlanId = string.Empty, Faction = RaidCatalog.FactionChemical, TargetKind = "home", UnitCount = 1 };
            RaidResultService.Begin(s, g, CombatSites.Get(HomeValleyLayout.RegionId));
            AddWreck(k, new double2(40, 40));
            AddWreck(k, new double2(44, 40));
            k.SpawnPatch(EmgMatterConst.Slag, new double2(48, 40), 2f);
            StepN(k, 2); // 只推内核：这支队伍是测试捏的，世界步里的攻城对账会把它当孤儿关掉
            RaidResultRecord end = RaidResultService.End(s, g);
            var lines = new List<AwayLine>();
            RaidResultService.AppendAway(s, 0, GameClock.Ticks + 1, lines);
            Expect(end != null && end.LeftWrecks == 2 && end.LeftSolids == 1 && lines.Any(l => l.Text != null && l.Text.Contains("残骸 2") && l.Text.Contains("固态斑块 1")),
                $"O1 离家报告写明这次突袭留下的残骸与固态斑块（基线之后的增量 {end?.LeftWrecks} / {end?.LeftSolids}）");
            // 下一次突袭还在（FGT-EMG-060）
            WorldSimulation.StepMany(60 * 100);
            var g2 = new TransitGroupRecord { GroupId = "persist-raid-2", PlanId = string.Empty, Faction = RaidCatalog.FactionChemical, TargetKind = "home", UnitCount = 1 };
            RaidResultRecord r2 = RaidResultService.Begin(s, g2, CombatSites.Get(HomeValleyLayout.RegionId));
            Expect(r2 != null && r2.BasePersistWrecks == 2 && r2.BasePersistSolids == 1, "O2 下一次突袭开始时上次留下的残骸墙、冷渣堆仍在（100 游戏秒后基线 2 / 1）");
            RaidResultRecord e2 = RaidResultService.End(s, g2);
            Expect(e2 != null && e2.LeftWrecks == 0 && e2.LeftSolids == 0, "O3 负向：这一次什么都没留下 → 报告不重复计上一次的");
            var g3 = new TransitGroupRecord { GroupId = "persist-raid-3", PlanId = string.Empty, Faction = RaidCatalog.FactionChemical, TargetKind = "home", UnitCount = 1 };
            RaidResultRecord r3 = RaidResultService.Begin(s, g3, CombatSites.Get(HomeValleyLayout.RegionId));
            AddWreck(k, new double2(52, 40));
            WorldSimulation.StepMany(60 * 30); // 这支队伍没有真的在攻城：走孤儿对账关闭
            Expect(r3 != null && r3.EndTick >= 0 && r3.LeftWrecks == 1, $"O4 不走正常结束（孤儿对账关闭）的突袭报告同样写留下的残骸（{r3?.LeftWrecks}）");
        }

        // ── P 暂停、倍速、观察一致 ─────────────────────────────────────────

        private static string Scenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe, 100);
            pausedHeld = true;
            GridCell? area = Open(s);
            CombatKernel k = HomeKernel;
            GridCell c = area.Value;
            k.SpawnPatch(EmgMatterConst.Cured, new double2(c.X + 2, c.Y + 2), 1.5f, 1f, 5f);
            k.SpawnPatch(EmgMatterConst.Ice, new double2(c.X + 6, c.Y + 2), 1.5f, 1f, 5f);
            int w = AddWreck(k, new double2(c.X + 2, c.Y + 7));
            int w2 = AddWreck(k, new double2(c.X + 5, c.Y + 7));
            WorldSimulation.StepMany(2);
            SitePersistService.OrderClear(s, SitePersistService.TargetOf(EmgPersistItem.KindWreck, w));
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Digest(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Digest(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 60;
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
            string reference = null;
            bool paused = true, same = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string d = Scenario(21601, true, speed, true, out bool held);
                paused &= held;
                reference ??= d;
                same &= d == reference;
            }
            Expect(paused && same && reference.Contains("orders:0") && reference.Contains("clr:1"),
                $"P1 暂停 120 帧清除单不动；0.5x / 1x / 2x / 3x 跑同样 60 游戏秒结果逐字一致：单子完工、残骸与固态都按长期留存留下（{reference}）");
            string seen = Scenario(21602, true, 1f, false, out _);
            string unseen = Scenario(21602, false, 1f, false, out _);
            Expect(seen == unseen, $"P2 B24 家园不被观察时结果一致（{unseen}）");
        }

        // ── R 性能（只测一次；Editor batchmode：Mono JIT、Burst 开，真机差别见证据）──────────

        private static void CheckPerformance()
        {
            double Avg(CombatKernel k, int steps)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                StepN(k, steps);
                return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / steps;
            }
            using CombatKernel full = NewKernel(true);
            for (int i = 0; i < 128; i++)
            {
                full.SpawnPatch(i % 2 == 0 ? EmgMatterConst.Slag : EmgMatterConst.Cured, new double2(i % 16 * 8, i / 16 * 8), 2f);
                StepN(full, 1);
            }
            for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
            {
                AddWreck(full, new double2(i % 32 * 4, 80 + i / 32 * 4));
            }
            int me = Unit(full, CombatFaction.Player, new double2(0, -10), full.AddWeapon(Flight(1f, 30f, 3f)));
            int farFoe = Unit(full, CombatFaction.Hostile, new double2(0, 140), -1, 1e6f, CombatUnitKind.Enemy, 0f);
            for (int i = 0; i < 300; i++)
            {
                full.FireAt(me, farFoe, full.Time);
                StepN(full, 1);
            }
            double loaded = Avg(full, 300);
            using CombatKernel empty = NewKernel(true);
            double idle = Avg(empty, 300);
            CampaignState s = NewWorld(21801, scrap: 100);
            CombatKernel home = HomeKernel;
            for (int i = 0; i < 64; i++)
            {
                AddWreck(home, new double2(40 + i, 40));
                home.SpawnPatch(EmgMatterConst.Cured, new double2(40 + i * 2, 50), 1.5f);
            }
            WorldSimulation.StepMany(5);
            SitePersistService.Check(s);
            Line($"    · 性能（Editor batchmode，Burst 开）：满额（128 固态 + 256 残骸 + 持续开火）内核每步 {loaded:0.000} 毫秒，空内核每步 {idle:0.000} 毫秒；堵路泛洪 {SitePersistService.LastFloodMs:0.00} 毫秒（只在留存物 / 建筑变化后每 10 游戏秒最多一次）");
            Expect(loaded < 3.0 * 2 && SitePersistService.LastFloodMs < 16.0 * 2, $"R1 满额内核每步 {loaded:0.000} 毫秒（战斗内核预算 3.0 毫秒 / 帧的 2 倍内），单次堵路泛洪 {SitePersistService.LastFloodMs:0.00} 毫秒（低频）");
        }

        // ── Q 正式界面 ───────────────────────────────────────────────────

        private static void CheckHud()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
            Button b = tree != null ? tree.CloneTree().Q<Button>("BuildClearSite") : null;
            CampaignState s = NewWorld(21701, scrap: 50);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            mode.Open();
            mode.SetClearSiteMode(true);
            bool active = mode.ClearSiteActive && mode.ClearMode;
            string hint = BuildModeHudUIToolkit.HintFor(mode);
            mode.SetClearMode(true); // 切回清带：不再是清除场地物
            bool belts = !mode.ClearSiteActive && mode.ClearMode;
            mode.SetClearSiteMode(true);
            mode.SetClearMode(!mode.ClearBeltActive); // 清带按钮 / 快捷键同一路径：从清除场地物是切到清带，不是退出
            belts &= mode.ClearBeltActive;
            mode.SetClearMode(!mode.ClearBeltActive); // 清带下再按才退出
            belts &= !mode.ClearMode;
            mode.SetClearMode(false);
            mode.Close();
            Expect(b != null && active && belts && hint == GameText.Get("emg.persist.tool_mode"), $"Q1 建造模式 HUD 有“清除场地物”按钮，开模式状态行写用法；与清带互斥（{hint}）");
        }
    }
}
