using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
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
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using S = GameLogic.EditorTools.FgSiegeSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG11-RAID-01 超频突袭（FG09 第 7 章“突袭特点：高速突击。猎犬直扑发电设施，熔断者冲墙自爆”；FG06 FGR-DEF-021 预算与编成 / 阵营选择；
    /// 卡片“预警情报写明‘高速突击’的特点”、负向“熔断者在屏障前集体自爆”；承接 DEBT-FG6DEF04-04 超频部分、DEBT-FG9DATA01-04、FG-GAP-185）。
    /// 全部起真实系统：真实家园（世界模拟、寻路、战斗内核攻城剧场与流场）、暴露账本、突袭导演、攻城服务、突袭结算、真文件存读档。
    /// A 数据：猎犬突袭职能 = 破坏；超频在场的职能表（破坏职能发电偏好 0、其余后移，其它职能不变）；四阵营突袭特点文本；攻城数值与敌人表同源（覆盖只给旧单位、不得与敌人表重复）。
    /// B 导演：第二幕超频暴露贡献最多 → 超频发动、编成全是超频单位、反制按当天锁定；第一幕超频领地没出现 → 不选超频（负向）；预警行有情报写“高速突击”，没情报不写；到达展开的全是带超频语法的单位。
    /// C 猎犬直扑发电设施：展开当步内核职能表就是超频表；第一座挨打的建筑是发电设施（信号设施更近也不先打）。
    /// D 熔断者冲墙自爆（负向“在屏障前集体自爆”）：离墙远时没有一台进熔断；顶到墙才熔断（耐久仍高于 30%）、引信 ≥ 2 游戏秒、预警照发；
    ///   集体自爆每台至多爆一次，屏障吃到泄爆，结算减员 = 自毁 + 被同伴炸死 = 全队，剧场关闭。
    /// E 暂停与 0.5x / 3x：接触熔断的引信按游戏秒，暂停不爆，两档倍速都在 2 游戏秒时爆。
    /// F 熔断中真文件存档 → 读档：接着跑与不存档连续跑逐位一致；损失明细的来源码随存档往返。
    /// G FG-GAP-185：伤害来源码（无主 / 突袭单位 / 己方 / 已不在 / 不属于突袭队伍的攻城组）；突袭中机器被突袭单位打死、余波里被无主伤害打死，损失明细与余波日志都带来源；
    ///   应急打印（紧急救援机与正式应急打印）避开燃烧斑块；G5 按 FGJ-M6R 第 3 波形态确定性复现余波：结算后无攻城组残留、无敌方单位，保底打印的机器 60 游戏秒不死、屏障不掉耐久。
    /// H 观察 / 不观察家园跑同一场熔断者 + 猎犬突袭逐位一致（FGR-BASE-021）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOverclockRaidSelfCheck）。
    /// </summary>
    public static class FgOverclockRaidSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string Wall = "barrier_t1";
        private const string Overclock = EnemyDefs.FactionOverclock;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/FG 超频突袭")]
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
            Line("\n[超频突袭] 猎犬直扑发电设施、熔断者冲墙自爆、预警写明特点、超频发动与反制、攻城数值同源、伤害来源与应急打印避险（FG11-RAID-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgocraid-selfcheck-" + Guid.NewGuid().ToString("N"));
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

                Step(CheckData);
                Step(CheckDirector);
                Step(CheckHoundPower);
                Step(CheckFuserWall);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckDamageSource);
                Step(CheckAftermath);
                Step(CheckObservedEqualsUnobserved);
            }
            finally
            {
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
            Line(_fail == 0 ? $"  超频突袭：全部 {_pass} 项通过" : $"  超频突袭：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            bool role = RaidCatalog.TryGetUnit("overclock.oc_hound", out RaidUnitDef hound) && SiegeCatalog.RoleOf(hound.Role) == CombatSiegeRole.Sabotage
                        && RaidCatalog.TryGetUnit("overclock.oc_fuser", out RaidUnitDef fuser) && SiegeCatalog.RoleOf(fuser.Role) == CombatSiegeRole.Siege;
            int n = CombatSiegeConst.CategoryCount;
            int power = math.tzcnt((int)CombatSiegeConst.CatPower);
            int sab = (int)CombatSiegeRole.Sabotage - 1;
            int shift = Mathf.RoundToInt(SiegeCatalog.OverclockOtherShiftMeters * 10f);
            IReadOnlyList<int> b0 = SiegeCatalog.RoleBiasFor(false, false);
            IReadOnlyList<int> b1 = SiegeCatalog.RoleBiasFor(false, true);
            IReadOnlyList<int> c0 = SiegeCatalog.RoleBiasFor(true, false);
            IReadOnlyList<int> c1 = SiegeCatalog.RoleBiasFor(true, true);
            int purify = math.tzcnt((int)CombatSiegeConst.CatPurify);
            // 叠加表（澄净 + 超频）：破坏职能的发电偏好是该行最小值（猎犬仍直扑发电），净化塔保持澄净的 0（审查 P2：不能让净化塔排到发电前面）。
            bool rowMin = Enumerable.Range(0, n).All(c => c1[sab * n + power] <= c1[sab * n + c]);
            bool primary = b1[sab * n + power] == 0 && c1[sab * n + power] == 0 && c1[sab * n + purify] == c0[sab * n + purify] && rowMin;
            bool shifted = true, others = true;
            for (int r = 0; r < CombatSiegeConst.RoleCount; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    int i = r * n + c;
                    if (r == sab && c != power)
                    {
                        shifted &= b0[i] >= CombatSiegeConst.Inf ? b1[i] == b0[i] : b1[i] == Math.Min(CombatSiegeConst.Inf, b0[i] + shift);
                        if (c != purify)
                        {
                            shifted &= c0[i] >= CombatSiegeConst.Inf ? c1[i] == c0[i] : c1[i] == Math.Min(CombatSiegeConst.Inf, c0[i] + shift);
                        }
                    }
                    else
                    {
                        others &= b1[i] == b0[i] && (r == sab && c == power || c1[i] == c0[i]); // 叠加表破坏职能的发电由 primary 断言（= 0）
                    }
                }
            }
            Expect(role && primary && shifted && others && shift > 0 && SiegeCatalog.Problems.Count == 0,
                $"A1 猎犬突袭职能 = 破坏、熔断者 = 攻城（{role}）；超频在场：破坏职能发电设施偏好 0（{primary}），信号 / 监听 / 核心 / 其它整体后移 {shift / 10} 米（{shifted}），" +
                $"突击 / 攻城 / 撤退不变（{others}）；与澄净表叠加：发电 = 该行最小（{rowMin}）、净化塔保持澄净的 {c1[sab * n + purify] / 10} 米、其余同样后移；攻城表无问题（{SiegeCatalog.Problems.Count}）");

            var factions = RaidCatalog.Units.Select(u => u.Faction).Distinct().ToList();
            var missing = factions.Where(f => !GameText.Has(RaidCatalog.TraitKey(f))).ToList();
            string oc = GameText.Has(RaidCatalog.TraitKey(Overclock)) ? GameText.Get(RaidCatalog.TraitKey(Overclock)) : string.Empty;
            Expect(missing.Count == 0 && factions.Contains(Overclock) && oc.Contains("高速突击") && oc.Contains("发电") && oc.Contains("冲墙") && GameText.Has("raid.warning.trait"),
                $"A2 编成表里的 {factions.Count} 个阵营都有“突袭特点”文本（缺 {string.Join(",", missing)}）；超频写明“{oc}”");

            // DEBT-FG9DATA01-04：攻城数值与敌人表同源。
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            var bad = new List<string>();
            int same = 0;
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                GameConfig.fg.SiegeUnit row = t.TbSiegeUnit.GetOrDefault(u.EnemyTypeId);
                GameConfig.fg.MechEnemy e = t.TbMechEnemy.GetOrDefault(u.EnemyTypeId);
                if (row == null || e == null || !SiegeCatalog.TryGetUnit(u.EnemyTypeId, out SiegeUnitDef d))
                {
                    bad.Add(u.EnemyTypeId + " 缺行");
                    continue;
                }
                (float ov, float ev, float got, string col)[] cols =
                {
                    (row.Speed, e.Speed, d.Speed, "speed"), (row.Radius, e.Radius, d.Radius, "radius"), (row.Range, e.WeaponRange, d.Range, "range"),
                    (row.Damage, e.WeaponDamage, d.Damage, "damage"), (row.Cooldown, e.WeaponCooldown, d.Cooldown, "cooldown"),
                };
                bool newFaction = e.Faction == Overclock || e.Faction == EnemyDefs.FactionClarity;
                foreach ((float ov, float ev, float got, string col) in cols)
                {
                    float want = ov > 0f ? ov : ev;
                    if (Math.Abs(got - Math.Max(col == "radius" ? 0.2f : 0f, want)) > 1e-4f || (ov > 0f && Math.Abs(ov - ev) < 1e-6f) || (newFaction && ov != 0f))
                    {
                        bad.Add($"{u.EnemyTypeId}.{col}（覆盖 {ov} / 敌人表 {ev} / 生效 {got}）");
                    }
                    else if (ov == 0f)
                    {
                        same++;
                    }
                }
            }
            Expect(bad.Count == 0 && same > 0,
                $"A3 DEBT-FG9DATA01-04 攻城数值同源：{RaidCatalog.Units.Count} 种突袭单位的速度 / 半径 / 射程 / 伤害 / 冷却 = 攻城覆盖（只有旧单位、且与敌人表不同）或敌人表；" +
                $"澄净 / 超频全部取敌人表；{same} 项直接取敌人表" + (bad.Count > 0 ? "；不一致：" + string.Join("、", bad.Take(6)) : string.Empty));
        }

        // ─────────────────────────────── B 导演 ───────────────────────────────

        private static void CheckDirector()
        {
            CampaignState s = NewWorld(11071);
            PlannedTerritory terr = WorldGenService.PlanFor(s)?.Find(Overclock);
            s.Progress.Act = Math.Max(2, Math.Max(s.Progress.Act, terr?.Act ?? 2));
            OverclockCounterService.Locked(s); // 当天锁定一次（写入锁定日）
            s.Factions.OverclockCounterId = OverclockCounterService.CounterLimiter; // 测试捷径：当天的对策锁定为“镜像体”（与玩家当天限制器类程序占多数时一致，锁定规则本身见 FgOverclockFactionSelfCheck）
            int plans0 = Plans(s).Length;
            Raise(s, Overclock, 31f);
            RaidPlanRecord p = Plans(s).Skip(plans0).OrderByDescending(x => x.Serial).FirstOrDefault();
            string stim = RaidDirectorService.MostStimulatedFaction(s);
            bool own = p != null && p.UnitIds.Length > 0 && p.UnitIds.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef u) && u.Faction == Overclock);
            GameConfig.fg.RaidCounter locked = OverclockCounterService.RaidCounterFor(OverclockCounterService.Locked(s));
            bool counter = p != null && locked != null && locked.Id == "overclock.limiter" && p.Counter == locked.Id && p.CounterCategory == locked.Category;
            Expect(terr != null && stim == Overclock && p != null && p.Faction == Overclock && own && counter,
                $"B1 第二幕（超频领地第 {terr?.Act} 幕出现）超频暴露贡献最多 → 最受刺激 = {stim}，暴露越过 30 排出的突袭由 {p?.Faction} 发动；" +
                $"编成全是超频单位（{(p != null ? string.Join("/", p.UnitIds.Select((id, i) => id + "×" + p.UnitCounts[i])) : "无")}）；反制 = 当天锁定 {locked?.Id ?? "（没有锁定）"}（计划 {p?.Counter}）");

            if (p == null)
            {
                return;
            }
            // 预警情报写明“高速突击”：有情报写、没情报不写（与编成同一可见性）。
            var known = new RaidWaveView { Wave = 1, Lead = p, Forces = 1, Units = p.UnitTotal, IntelKnown = true, ArrivalTick = GameClock.Ticks + 3600, DepartTick = GameClock.Ticks + 600 };
            RaidWaveView unknown = known;
            unknown.IntelKnown = false;
            string rowK = RaidDirectorService.WaveRowText(s, known, GameClock.Ticks);
            string rowU = RaidDirectorService.WaveRowText(s, unknown, GameClock.Ticks);
            string tipK = RaidDirectorService.WaveTip(s, known, GameClock.Ticks);
            string tipU = RaidDirectorService.WaveTip(s, unknown, GameClock.Ticks);
            Expect(rowK.Contains("高速突击") && tipK.Contains("高速突击") && !rowU.Contains("高速突击") && !tipU.Contains("高速突击"),
                $"B3 预警：有情报时行与悬停都写“高速突击”特点（“{Short(rowK.Replace('\n', ' '))}”）；没情报时不写（只写编成未知）");

            // 计划的编成到达展开：全是带超频语法的单位（内核里有超频运行状态）。
            TransitGroupRecord g = S.Arrive(s, S.OutsidePoint(s, 34f), Overclock, p.UnitIds, p.UnitCounts, p.EliteCounts);
            int key = SiegeService.KeyOf(g);
            List<int> units = GroupUnits(key);
            int oc = units.Count(u => Site.Kernel.TryGetOverclock(u, out _));
            bool sources = units.All(u => Site.DamageSourceOf(u, s).StartsWith("raid:" + key + ":enemy_oc_", StringComparison.Ordinal));
            Expect(units.Count == p.UnitTotal && oc == units.Count && sources,
                $"B4 超频编成到达后展开 {units.Count} 台（计划 {p.UnitTotal}），全部带超频运行状态（{oc}）、种类都是超频敌人（{sources}）");

            // 负向：超频领地还没出现（第一幕）时，即使贡献全是超频也不选超频。
            CampaignState s1 = NewWorld(11072);
            PlannedTerritory t1 = WorldGenService.PlanFor(s1)?.Find(Overclock);
            s1.Progress.Act = 1;
            int p10 = Plans(s1).Length;
            Raise(s1, Overclock, 31f);
            RaidPlanRecord q = Plans(s1).Skip(p10).OrderByDescending(x => x.Serial).FirstOrDefault();
            bool act1 = t1 != null && t1.Act >= 2 ? q != null && q.Faction != Overclock && RaidDirectorService.MostStimulatedFaction(s1) != Overclock : t1 != null;
            Expect(act1, $"B2 负向：第一幕超频领地还没出现（领地第 {t1?.Act} 幕）→ 贡献全是超频也不由超频发动（这次是 {q?.Faction ?? "无"}）");

        }

        // ─────────────────────────────── C 猎犬直扑发电设施 ───────────────────────────────

        private static void CheckHoundPower()
        {
            CampaignState s = NewWorld(11073);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord pole = S.PlaceNear(s, "power_pole", at, S.CoreCenter(s), 0.12f, 0.3f);
            BuildingRecord gen = S.PlaceNear(s, "generator", at, S.CoreCenter(s), 0.45f, 0.75f);
            if (pole == null || gen == null)
            {
                Fail($"C 测试准备：放不下电塔（{pole != null}）/ 发电机（{gen != null}）");
                return;
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            List<BuildingRecord> all = s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId).ToList();
            var dur0 = all.ToDictionary(b => b.BuildingId, b => Dur(s, b));
            TransitGroupRecord g = S.Arrive(s, at, Overclock, new[] { "overclock.oc_hound" }, new[] { 3 });
            IReadOnlyList<int> want = SiegeCatalog.RoleBiasFor(false, true);
            bool table = Site != null;
            for (int i = 0; table && i < want.Count && i < CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount; i++)
            {
                table &= Site.Kernel.SiegeRoleBias(i) == Math.Max(0, Math.Min(CombatSiegeConst.Inf, want[i]));
            }
            BuildingRecord first = null;
            F.StepUntil(() =>
            {
                first = all.FirstOrDefault(b => Dur(s, b) < dur0[b.BuildingId] - 0.01f);
                return first != null;
            }, 150);
            bool isPower = first != null && SiegeCatalog.CategoryOf(first.BuildingTypeId) == CombatSiegeConst.CatPower;
            float dPole = Vector2.Distance(at, pole.Position), dGen = Vector2.Distance(at, gen.Position);
            Expect(table && isPower && Dur(s, pole) >= dur0[pole.BuildingId] - 0.01f,
                $"C1 猎犬直扑发电设施：展开当步内核职能表就是超频表（{table}）；第一座挨打的是 {first?.BuildingTypeId ?? "无"}（发电类 {isPower}），" +
                $"更近的电塔（{dPole:0} 米 vs 发电机 {dGen:0} 米，信号类）没挨打（{dur0[pole.BuildingId]:0} → {Dur(s, pole):0}）");
            // 超频队伍走光后职能表恢复（其它阵营不会一直被超频表带着走）。
            Site.DespawnSiegeGroup(SiegeService.KeyOf(g));
            F.StepUntil(() => !SiegeService.StateOf(s).TheaterActive, 30);
            S.Arrive(s, at, "silent", new[] { "silent.jammer" }, new[] { 1 });
            IReadOnlyList<int> plain = SiegeCatalog.RoleBiasFor(false, false);
            bool back = true;
            for (int i = 0; i < plain.Count && i < CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount; i++)
            {
                back &= Site.Kernel.SiegeRoleBias(i) == Math.Max(0, Math.Min(CombatSiegeConst.Inf, plain[i]));
            }
            Expect(back, $"C2 超频队伍走光、只有静默队伍攻城时职能表恢复普通表（{back}）");
        }

        // ─────────────────────────────── D 熔断者冲墙自爆（负向：在屏障前集体自爆） ───────────────────────────────

        private static void CheckFuserWall()
        {
            CampaignState s = NewWorld(11074);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord wall = S.PlaceNear(s, Wall, at, S.CoreCenter(s), 0.35f, 0.6f);
            if (wall == null)
            {
                Fail("D 测试准备：放不下屏障");
                return;
            }
            float w0 = Dur(s, wall);
            int bursts0 = OverclockCombatFeedback.BurstCues;
            int warns0 = OverclockCombatFeedback.MeltWarnCues;
            TransitGroupRecord g = S.Arrive(s, at, Overclock, new[] { "overclock.oc_fuser" }, new[] { 4 });
            int key = SiegeService.KeyOf(g);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            List<int> fusers = GroupUnits(key);
            EnemyDefs.TryGet("enemy_oc_fuser", out GameConfig.fg.MechEnemy row);
            float meltHp = row?.OcMeltHp ?? 0.3f;
            float reach = 4.5f; // 墙格中心到熔断者中心：半格墙 + 攻击距离 1.6 + 半径 0.8 + 取整余量
            var meltAt = new Dictionary<int, long>();
            var boomAt = new Dictionary<int, long>();
            int farMelts = 0, lowHpMelts = 0;
            float minDistAtMelt = float.MaxValue, maxDistAtMelt = 0f;
            int maxTicks = 150 * GameClock.StepHz;
            for (int i = 0; i < maxTicks && boomAt.Count < fusers.Count; i++)
            {
                WorldSimulation.StepMany(1);
                foreach (int u in fusers)
                {
                    if (boomAt.ContainsKey(u))
                    {
                        continue;
                    }
                    if (!Site.Kernel.IsAlive(u))
                    {
                        boomAt[u] = GameClock.Ticks;
                        continue;
                    }
                    if (!meltAt.ContainsKey(u) && Site.Kernel.TryGetOverclock(u, out CombatOverclock oc) && oc.State == CombatOverclockConst.StateMelting)
                    {
                        meltAt[u] = GameClock.Ticks;
                        Site.Kernel.TryGetUnit(u, out CombatUnitView v);
                        float dist = Vector2.Distance(new Vector2((float)v.Position.x, (float)v.Position.y), wall.Position);
                        minDistAtMelt = Math.Min(minDistAtMelt, dist);
                        maxDistAtMelt = Math.Max(maxDistAtMelt, dist);
                        bool lowHp = v.MaxHealth > 0f && v.Health <= v.MaxHealth * meltHp;
                        if (lowHp)
                        {
                            lowHpMelts++; // 被同伴泄爆打到 30% 以下也会熔断（耐久触发，规格原文）
                        }
                        else if (dist > reach)
                        {
                            farMelts++; // 负向：离墙远、耐久还高却进了熔断
                        }
                    }
                }
            }
            bool allGone = fusers.Count == 4 && boomAt.Count == fusers.Count;
            int bursts = OverclockCombatFeedback.BurstCues - bursts0;
            int warns = OverclockCombatFeedback.MeltWarnCues - warns0;
            long fuseTicks = (long)Math.Round((row?.OcMeltFuse ?? 2f) * GameClock.StepHz);
            long firstMelt = meltAt.Count > 0 ? meltAt.Values.Min() : -1;
            long firstBoom = boomAt.Count > 0 ? boomAt.Values.Min() : -1;
            Expect(meltAt.Count >= 1 && farMelts == 0 && maxDistAtMelt <= reach && firstBoom - firstMelt >= fuseTicks - 1 && warns >= meltAt.Count && (row?.OcMeltFuse ?? 0f) >= 2f,
                $"D1 负向：离墙远时没有一台进熔断（远处熔断 {farMelts} 台）；顶到墙才熔断（{meltAt.Count} 台进熔断，熔断时离墙 {minDistAtMelt:0.0}～{maxDistAtMelt:0.0} 米 ≤ {reach}，" +
                $"其中 {lowHpMelts} 台是被同伴泄爆打到 30% 以下）；第一声预警到第一次爆炸 {(firstBoom - firstMelt) / (float)GameClock.StepHz:0.00} 游戏秒（≥ 表里引信 {row?.OcMeltFuse} 秒），预警 {warns} 次");
            bool settled = F.StepUntil(() => r != null && r.Outcome != "open", 30);
            Expect(allGone && bursts >= 1 && bursts <= fusers.Count && Dur(s, wall) < w0 - 1f && settled
                   && r.SelfDestructed + r.Killed == fusers.Count && r.SelfDestructed == bursts,
                $"D2 集体自爆：4 台全部没了（{allGone}），泄爆 {bursts} 次（每台至多一次）、其余被同伴泄爆炸死；屏障吃到泄爆（{w0:0} → {Dur(s, wall):0}）；" +
                $"结算 {r?.Outcome}：自毁 {r?.SelfDestructed} + 击毁 {r?.Killed} = 全队 {fusers.Count}，剧场关闭（{!SiegeService.StateOf(s).TheaterActive}）");
        }

        // ─────────────────────────────── E 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 3f })
            {
                CampaignState s = NewWorld(11075);
                Vector2 at = S.OutsidePoint(s, 34f);
                BuildingRecord wall = S.PlaceNear(s, Wall, at, S.CoreCenter(s), 0.35f, 0.6f);
                TransitGroupRecord g = wall != null ? S.Arrive(s, at, Overclock, new[] { "overclock.oc_fuser" }, new[] { 1 }) : null;
                int u = g != null ? GroupUnits(SiegeService.KeyOf(g)).FirstOrDefault() : 0;
                long melt = -1;
                for (int i = 0; u != 0 && i < 150 * GameClock.StepHz && melt < 0; i++)
                {
                    WorldSimulation.StepMany(1);
                    if (Site.Kernel.TryGetOverclock(u, out CombatOverclock oc) && oc.State == CombatOverclockConst.StateMelting)
                    {
                        melt = GameClock.Ticks;
                    }
                }
                if (melt < 0)
                {
                    Fail($"E 准备（{speed}x）：熔断者没顶到墙进熔断（墙 {wall != null}，单位 {u}）");
                    return;
                }
                GameClock.SetPaused(true);
                for (int i = 0; i < 40; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool held = GameClock.Ticks == melt && Site.Kernel.IsAlive(u);
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 6000 && Site.Kernel.IsAlive(u); i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                long ticks = GameClock.Ticks - melt;
                long want = (long)Math.Round(2f * GameClock.StepHz);
                bool timing = !Site.Kernel.IsAlive(u) && ticks >= want - 1 && ticks <= want + (long)Math.Ceiling(speed * GameClock.StepHz / 60f) + 1;
                ok &= held && timing;
                rows.Add($"{speed}x：暂停 4 秒不动（{held}），熔断后 {ticks / (float)GameClock.StepHz:0.00} 游戏秒爆");
            }
            Expect(ok, $"E1 接触熔断的引信按游戏秒：{string.Join("；", rows)}（目标 2 游戏秒）");
        }

        // ─────────────────────────────── F 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(11076);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord wall = S.PlaceNear(s, Wall, at, S.CoreCenter(s), 0.35f, 0.6f);
            if (wall == null)
            {
                Fail("F 测试准备：放不下屏障");
                return;
            }
            TransitGroupRecord g = S.Arrive(s, at, Overclock, new[] { "overclock.oc_fuser" }, new[] { 3 });
            int key = SiegeService.KeyOf(g);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            int serial = r?.Serial ?? -1;
            bool melting = F.StepUntil(() => GroupUnits(key).Any(u => Site.Kernel.TryGetOverclock(u, out CombatOverclock oc) && oc.State == CombatOverclockConst.StateMelting), 150);
            if (!melting || r == null)
            {
                Fail($"F 测试准备：熔断者没进熔断（{melting}）/ 没有结算（{r != null}）");
                return;
            }
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long t0 = GameClock.Ticks;
            F.Seconds(8f);
            long elapsed = GameClock.Ticks - t0;
            string continuous = Sig(s, serial);
            CampaignState l = RestoreSlot(out string fail);
            if (l != null)
            {
                StepTicks(elapsed);
            }
            string after = l != null ? Sig(l, serial) : "读档失败：" + fail;
            RaidResultRecord lr = l != null ? RaidResultService.Find(l, serial) : null;
            bool sourced = lr != null && lr.Losses.Any(x => x != null && x.Kind == RaidResultService.KindDefense && !string.IsNullOrEmpty(x.Source));
            Expect(save.Success && after == continuous && sourced,
                $"F1 熔断中真文件存档 → 读档：接着跑 {elapsed / (float)GameClock.StepHz:0.0} 游戏秒与不存档连续跑一致（{after == continuous}）；屏障损失明细带来源码随存档往返（{sourced}：" +
                $"{string.Join("/", lr?.Losses.Select(x => x.Kind + "=" + x.Source) ?? Array.Empty<string>())}）" +
                (after == continuous ? string.Empty : $"\n连续：{Short(continuous)}\n读档：{Short(after)}"));
        }

        // ─────────────────────────────── G FG-GAP-185 伤害来源与应急打印避险 ───────────────────────────────

        private static void CheckDamageSource()
        {
            CampaignState s = NewWorld(11077);
            Vector2 at = S.OutsidePoint(s, 40f);
            TransitGroupRecord g = S.Arrive(s, at, Overclock, new[] { "overclock.oc_node" }, new[] { 2 });
            int key = SiegeService.KeyOf(g);
            int raider = GroupUnits(key).FirstOrDefault();
            MachineRecord m = MachineRegistry.AllRecords.FirstOrDefault(x => x.RegionId == HomeValleyLayout.RegionId && x.IsAlive && Site.TryGetMachineUnit(x.LogicId, out _));
            int mu = 0;
            if (raider == 0 || m == null || !Site.TryGetMachineUnit(m.LogicId, out mu))
            {
                Fail($"G 测试准备：突袭单位 {raider} / 家园机器 {m?.LogicId}");
                return;
            }
            string sField = Site.DamageSourceOf(0, s);
            string sRaid = Site.DamageSourceOf(raider, s);
            string sPlayer = Site.DamageSourceOf(mu, s);
            string sGone = Site.DamageSourceOf(int.MaxValue - 7, s);
            // 不属于突袭行进队伍的攻城组（无主残留 / 迎战据点 / 首领波次）不混记成 raid（审查 P2）。
            const int strayKey = 777;
            int stray = Site.SpawnSiegeRaider(strayKey, at, at, 0.5f, 2f, 50f, 0f, -1, -1, CombatSiegeRole.Assault, false, 1f);
            string sStray = Site.DamageSourceOf(stray, s);
            Site.DespawnSiegeGroup(strayKey);
            Expect(sField == "field" && sRaid == $"raid:{key}:enemy_oc_node" && sPlayer.StartsWith("player:", StringComparison.Ordinal) && sGone.StartsWith("gone:", StringComparison.Ordinal)
                   && sStray == $"hostile:orphan:{strayKey}:unknown",
                $"G1 伤害来源码：无主 = {sField}；突袭单位 = {sRaid}；己方误伤 = {sPlayer}；已不在内核 = {sGone}；不属于突袭队伍的攻城组 = {sStray}");

            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            int n0 = r?.Losses.Length ?? 0;
            Site.Kernel.Damage(mu, 1e6f, raider);
            WorldSimulation.StepMany(2);
            RaidLossRecord loss = r?.Losses.Skip(n0).FirstOrDefault(x => x.Kind == RaidResultService.KindMachine);
            Expect(!m.IsAlive && loss != null && loss.Source == sRaid,
                $"G2 攻城中机器 #{m.LogicId} 被突袭单位打死：损失明细记来源 {loss?.Source ?? "无"}（应为 {sRaid}）");

            // 余波：队伍走光、结算关闭之后，又一台机器被无主伤害打死——记进刚结束那一份、来源 = field，日志写明来源。
            Site.DespawnSiegeGroup(key);
            bool closed = F.StepUntil(() => r != null && r.Outcome != "open", 30);
            MachineRecord m2 = MachineRegistry.AllRecords.FirstOrDefault(x => x.RegionId == HomeValleyLayout.RegionId && x.IsAlive && Site.TryGetMachineUnit(x.LogicId, out _));
            int n1 = r?.Losses.Length ?? 0;
            bool logged = false;
            void OnLog(string msg, string stack, LogType type)
            {
                logged |= msg.Contains("余波损失") && msg.Contains("来源 field");
            }
            Application.logMessageReceived += OnLog;
            try
            {
                if (m2 != null && Site.TryGetMachineUnit(m2.LogicId, out int mu2))
                {
                    Site.Kernel.Damage(mu2, 1e6f, 0);
                    WorldSimulation.StepMany(2);
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            RaidLossRecord after = r?.Losses.Skip(n1).FirstOrDefault(x => x.Kind == RaidResultService.KindMachine);
            Expect(closed && m2 != null && after != null && after.Source == "field" && logged,
                $"G3 余波（结算 {r?.Outcome} 之后）机器 #{m2?.LogicId} 被无主伤害打死：记进刚结束那一份、来源 {after?.Source ?? "无"}，余波日志写明来源（{logged}）");

            // 应急打印避开仍有危险的位置：核心旁铺一块燃烧斑块 → 出生点挪到圈外；没有危险时就是核心位置。
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 clean = HomeValleySoftlockGuard.RescueSpawnPoint();
            bool fire = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(core.x, core.y), 3f, 1f, 60f);
            WorldSimulation.StepMany(1);
            Vector2 moved = HomeValleySoftlockGuard.RescueSpawnPoint();
            Expect(fire && clean == core && HomeValleySoftlockGuard.InHazard(core) && Vector2.Distance(moved, core) > 3f && !HomeValleySoftlockGuard.InHazard(moved),
                $"G4 应急打印避开危险：没有危险时出生在核心（{clean == core}）；核心铺燃烧斑块（{fire}）后挪到 ({moved.x:0.0},{moved.y:0.0})、离核心 {Vector2.Distance(moved, core):0.0} 米、不在危险里");

            // 正式应急打印（SoftlockService，FGR-ECO-070）的落点：出生锚点铺燃烧斑块后落点不在危险里（审查 P2：此前只测了 Demo 紧急救援机）。
            MethodInfo printPos = typeof(SoftlockService).GetMethod("PrintPosition", BindingFlags.NonPublic | BindingFlags.Static);
            Vector2 anchor = HomeValleyLayout.Erc002Spawn.Position;
            bool fire2 = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(anchor.x, anchor.y), 3f, 1f, 60f);
            WorldSimulation.StepMany(1);
            Vector2 printed = printPos != null ? (Vector2)printPos.Invoke(null, new object[] { s }) : anchor;
            Expect(printPos != null && fire2 && HomeValleySoftlockGuard.InHazard(anchor) && !HomeValleySoftlockGuard.InHazard(printed),
                $"G4b 正式应急打印避开危险：出生锚点铺燃烧斑块（{fire2}，锚点在危险里 {HomeValleySoftlockGuard.InHazard(anchor)}）后落点 ({printed.x:0.0},{printed.y:0.0})、离锚点 {Vector2.Distance(printed, anchor):0.0} 米、不在危险里");
        }

        // ─────────────────────────────── G5 FG-GAP-185 余波复现（FGJ-M6R 第 3 波形态） ───────────────────────────────

        /// <summary>
        /// FG-GAP-185 定位来源（无旅程的确定性复现）：静默阵营 15 台攻城、交战 12 游戏秒（开火、在飞弹体、斑块都有）后击毁 5 台、其余按减员撤离，结算关闭（withdrawn）；
        /// 断言内核里没有任何攻城组残留、家园里没有敌方单位；随后家园机器全灭（测试手段，记下排除）→ 正式保底（应急打印 / 紧急救援机）在核心出机器，
        /// 再跑 60 游戏秒：出来的机器全部活着、屏障不再掉耐久，余波损失为 0；若有余波损失则来源码必须可解释（不得是 raid / gone / hostile）。
        /// </summary>
        private static void CheckAftermath()
        {
            CampaignState s = NewWorld(11078);
            Vector2 core = S.CoreCenter(s);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord wall = S.PlaceNear(s, "barrier_t2", at, core, 0.35f, 0.6f) ?? S.PlaceNear(s, Wall, at, core, 0.35f, 0.6f);
            TransitGroupRecord g = wall != null
                ? S.Arrive(s, at, EnemyDefs.FactionSilent, new[] { "silent.scout", "silent.jammer", "silent.echo_hunter", "silent.prism" }, new[] { 6, 4, 3, 2 })
                : null;
            int key = g != null ? SiegeService.KeyOf(g) : 0;
            RaidResultRecord r = g != null ? RaidResultService.OpenFor(s, g.GroupId) : null;
            List<int> units = GroupUnits(key);
            if (r == null || units.Count != 15)
            {
                Fail($"G5 测试准备：屏障 {wall?.BuildingTypeId ?? "无"} / 结算 {r != null} / 展开 {units.Count} 台（应 15）");
                return;
            }
            F.Seconds(12f);
            units = GroupUnits(key);
            foreach (int u in units.Take(5))
            {
                Site.Kernel.Damage(u, 1e6f, 0);
            }
            WorldSimulation.StepMany(2);
            int alive = Site.CountSiegeGroup(key);
            SiegeService.OrderRetreat(s, Site, g, SiegeService.ReasonLosses, alive, 15 - alive);
            bool closed = F.StepUntil(() => r.Outcome != "open", 180);
            var groups = new List<int>();
            int residual = Site.CollectSiegeGroups(groups);
            List<string> hostiles = HostileSources(s, out int armed);
            int n0 = r.Losses.Length;

            var mine = new HashSet<string>();
            s.Scrap = 0;
            foreach (MachineRecord m in HomeMachines())
            {
                if (Site.TryGetMachineUnit(m.LogicId, out int mu))
                {
                    mine.Add(m.LogicId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Site.Kernel.Damage(mu, 1e6f, 0);
                }
            }
            WorldSimulation.StepMany(2);
            bool printed = F.StepUntil(() => HomeMachines().Count > 0, 40);
            List<int> printedIds = HomeMachines().Select(m => m.LogicId).ToList();
            float w0 = Dur(s, wall);
            F.Seconds(60f);
            List<int> dead = printedIds.Where(id => !HomeMachines().Any(m => m.LogicId == id)).ToList();
            List<RaidLossRecord> aftermath = r.Losses.Skip(n0).Where(x => x != null && !(x.Kind == RaidResultService.KindMachine && mine.Contains(x.Id))).ToList();
            bool explained = aftermath.All(x => !x.Source.StartsWith("raid:", StringComparison.Ordinal) && !x.Source.StartsWith("gone:", StringComparison.Ordinal)
                                                && !x.Source.StartsWith("hostile:", StringComparison.Ordinal));
            Expect(closed && r.Outcome == RaidResultService.OutcomeWithdrawn && residual == 0 && armed == 0 && printed && dead.Count == 0
                   && Dur(s, wall) >= w0 - 0.01f && aftermath.Count == 0 && explained,
                $"G5 余波复现（静默 15 台、击毁 5、其余撤离，结算 {r.Outcome}）：结算后内核攻城组残留 {residual}（{string.Join(",", groups)}）、家园敌方单位 {hostiles.Count} 个其中带武器 {armed} 个（{string.Join(",", hostiles)}；emi = 静默领地电磁噪声发射器，无武器）；" +
                $"机器全灭后保底在核心出了 {printedIds.Count} 台（{printed}），60 游戏秒后阵亡 {dead.Count} 台；屏障 {wall.BuildingTypeId} 耐久 {w0:0} → {Dur(s, wall):0}；" +
                $"余波损失 {aftermath.Count} 条（{string.Join("、", aftermath.Select(x => x.Kind + "#" + x.Id + "=" + x.Source))}）");
        }

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(x => x.RegionId == HomeValleyLayout.RegionId && x.IsAlive && Site.TryGetMachineUnit(x.LogicId, out _)).ToList();

        /// <summary>家园内核里还活着的敌方单位的来源码；<paramref name="armed"/> = 其中带武器、能开火的个数（余波伤害只可能来自这些）。</summary>
        private static List<string> HostileSources(CampaignState s, out int armed)
        {
            armed = 0;
            var list = new List<string>();
            CombatKernel k = Site?.Kernel;
            for (int i = 0; k != null && i < k.SlotCount; i++)
            {
                CombatUnitView v = k.ViewAt(i);
                if (v.Alive && v.Faction == CombatFaction.Hostile)
                {
                    list.Add(Site.DamageSourceOf(v.Id, s));
                    if (v.Weapon >= 0 && (v.Flags & CombatUnitFlags.WeaponEnabled) != 0)
                    {
                        armed++;
                    }
                }
            }
            return list;
        }

        // ─────────────────────────────── H 观察 / 不观察一致（FGR-BASE-021） ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = FuserRun(true);
            string unseen = FuserRun(false);
            Expect(seen == unseen && !seen.StartsWith("准备失败", StringComparison.Ordinal),
                "H1 熔断者顶墙 + 猎犬攻城的同一场突袭，观察与不观察家园跑 30 游戏秒逐位一致（攻城快照 + 内核哈希 + 结算）"
                + (seen == unseen ? string.Empty : "\n" + DiffAt(seen, unseen, "观察", "不观察")));
        }

        private static string FuserRun(bool observe)
        {
            CampaignState s = NewWorld(11079, observe);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord wall = S.PlaceNear(s, Wall, at, S.CoreCenter(s), 0.35f, 0.6f);
            TransitGroupRecord g = wall != null ? S.Arrive(s, at, Overclock, new[] { "overclock.oc_fuser", "overclock.oc_hound" }, new[] { 2, 2 }) : null;
            RaidResultRecord r = g != null ? RaidResultService.OpenFor(s, g.GroupId) : null;
            if (r == null)
            {
                return $"准备失败（屏障 {wall != null}）";
            }
            StepTicks(30L * GameClock.StepHz);
            // 屏障编号后缀（#sgN）取自会话内递增的计数器，同一会话里第二次建世界会顺延一号——与观察无关，比较前去掉（其余逐位比）。
            return System.Text.RegularExpressions.Regex.Replace(Sig(s, r.Serial), "#sg[0-9]+", "#sg");
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static RaidPlanRecord[] Plans(CampaignState s) => RaidDirectorService.StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>();

        /// <summary>按真实账本把暴露抬到 ≥ <paramref name="target"/>（贡献记在 <paramref name="faction"/> 的敌方固件上），再走两步让导演处理触发。</summary>
        private static void Raise(CampaignState s, string faction, float target)
        {
            string fw = ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.First(r => r != null && r.Faction == faction).Id;
            for (int guard = 0; guard < 200 && s.SignalExposure < target; guard++)
            {
                CampaignExposureLedger.GrantRawFire(s, fw);
            }
            WorldSimulation.StepMany(2);
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

        private static string Sig(CampaignState s, int serial)
        {
            RaidResultRecord r = RaidResultService.Find(s, serial);
            string losses = r == null ? "-" : string.Join(",", r.Losses.Select(x => x.Kind + ":" + x.Id + ":" + x.Source + ":" + x.Tick));
            return SiegeService.Snapshot(s) + "|H" + (Site != null ? Site.Kernel.StateHash().ToString("X16") : "-")
                   + $"|R{r?.Outcome},{r?.SelfDestructed},{r?.Killed}|L{losses}";
        }

        private static void StepTicks(long n)
        {
            int q = Math.Max(1, GameClock.StepHz / 4);
            while (n > 0)
            {
                int k = (int)Math.Min(q, n);
                WorldSimulation.StepMany(k);
                n -= k;
            }
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

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static float Dur(CampaignState s, BuildingRecord b) => RepairDroneService.DurabilityOf(s, b);

        /// <summary>两段签名第一处不同附近的片段（各 ±120 字）。</summary>
        private static string DiffAt(string a, string b, string la, string lb)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 120);
            string Cut(string x) => from >= x.Length ? string.Empty : x.Substring(from, Math.Min(240, x.Length - from));
            return $"第 {i} 字起不同：\n{la}：…{Cut(a)}\n{lb}：…{Cut(b)}";
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length > 200 ? s.Substring(0, 200) + "…" : s;

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
            CampaignState s = CampaignState.CreateNew("fgocraid-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 3000;
            F.PowerUp(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgOverclockRaidSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
