using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Stage;
using GameLogic.View;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.DebugTools;
using GameLogic.Settings;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.SignalCore;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using FwRow = GameConfig.fg.FirmwareKind;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-02 读法矩阵与固定底盘兼容的自动验收（FG02 FGR-FW-010～012；FGT-FW-001；卡片负向“不兼容的组件放上炮塔蓝图（拒绝并说明）”）。
    /// 起真实系统跑、断言行为（不是“字段存在”）：
    /// A 源数据 = 运行时表（fg.TbCarrierReading / fg.TbCombatComponent 逐字段；读法字段 5 种载体全覆盖）；
    /// B 旧引擎：扩展的 ChassisPrimitiveMatrixSmokeReport（42 条带旧基因的固件 × 5 载体器官零死对 + 命名实证）；每条固件声明的读法字段 = 旧引擎模块真实写出的字段；
    /// C 正式版战斗内核 44 条 × 5 载体零死对（FGT-FW-001）：同一把武器“读法开 / 读法关”在带正面装甲、标记、状态标签的场地里真打 4 游戏秒，行为指纹必须不同；
    /// D 读法逐条实证（破甲、额外目标、穿透、连锁、溅射、区域、牵引、击退、跃击、处决、修复、无人机、力场、布区、回波、标记跳转、增幅、过热爆发、蓄力、连网、减速）；
    /// E 固定底盘兼容表（FGR-FW-012）：冲刺器拒绝并说明（装配入口 / 换底盘 / 保存校验 / 真实保存一致）、推铲读作击退铲（内核真推 3 米）、其余直接兼容、移动底盘不受影响、固定底盘未解锁；
    /// F 读法说明对玩家可见（FGR-FW-011）：220 句 = 表的短语 × 幅度，与内核参数同源；信号核悬停列 5 条、电路编辑器只显示当前载体那一条；中英文、无内部 ID；
    /// G 存读档：内核快照格式 3 带区域 / 回波 / 无人机 / 状态往返逐位一致、续跑一致；格式 2 旧快照照样读；
    /// H 正式流程：破碎都市里带读法的机器（喷洒器 + 冷却液、连射器 + 拖尾 + 霰射）经编队攻击命令真打，0.5x～3x 与暂停、观察 / 不观察、真实存档读档后续跑结果一致；
    /// I 负向（容量满、母机阵亡、目标消失、不可伤、未知组件）；J 性能。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgReadingMatrixSelfCheck
    {
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const float Dt = 1f / 60f;
        private const int UplinkSlot = 2;

        /// <summary>每种载体用来跑矩阵的作战组件（设计案 5.6）。</summary>
        private static readonly (FirmwareCarrier Carrier, string Component)[] CarrierComponents =
        {
            (FirmwareCarrier.Projectile, ComponentCatalog.CompGunId),
            (FirmwareCarrier.Melee, ComponentCatalog.CompRamId),
            (FirmwareCarrier.Summon, ComponentCatalog.CompDroneBayId),
            (FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId),
            (FirmwareCarrier.Field, ComponentCatalog.CompSprayerId),
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 读法矩阵与固定底盘兼容")]
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
            Line("\n[读法矩阵] 读法矩阵与固定底盘兼容（FG2-FW-02）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfw02-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "战斗内核是 AOT + Burst，热更层在 Editor 下是 Mono JIT、真机走 HybridCLR 解释执行（热更侧数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckSourceParity);
                Step(CheckLegacyMatrix);
                Step(CheckDeclaredFields);
                Step(CheckKernelMatrix);
                Step(CheckNamedReadings);
                Step(CheckFixedChassis);
                Step(CheckReadingTexts);
                Step(CheckSaveLoad);
                Step(CheckWorldPipeline);
                Step(CheckNegatives);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"读法矩阵自检抛异常：{e}");
            }
            finally
            {
                WorldSimulation.UnloadAll();
                FirmwareKinds.ResetForTests();
                CarrierReadings.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
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
            Line($"  · [读法矩阵] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 源数据 = 运行时表 ─────────────────────────────────────────────────

        private static void CheckSourceParity()
        {
            Line("  · A. 源数据 fgdata_reading.py 与运行时 fg.TbCarrierReading / fg.TbCombatComponent 逐字段一致；27 个读法字段 × 5 种载体每一对都有行（FGR-FW-010 的数据前提）");
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var cr = new List<string[]>();
            var cc = new List<string[]>();
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f[0] == "CR")
                {
                    cr.Add(f);
                }
                else if (f[0] == "CC")
                {
                    cc.Add(f);
                }
            }
            var rows = CarrierReadings.Rows;
            var diffs = new List<string>();
            foreach (string[] f in cr)
            {
                CarrierReading r = rows.FirstOrDefault(x => x.Id == f[1]);
                string[] mine = r == null ? null : new[] { r.Id, r.Field, r.Carrier, r.Verb, F(r.A), F(r.B), F(r.C), r.Mag, r.Place, r.TextKey };
                Compare(f, mine, diffs);
            }
            Expect(code == 0 && cr.Count > 0 && cr.Count == rows.Count && diffs.Count == 0 && CarrierReadings.LoadError == null,
                $"fg.TbCarrierReading {rows.Count} 行与源 {cr.Count} 行逐字段一致{Detail(diffs)}");
            var comps = CarrierReadings.Components;
            var cdiff = new List<string>();
            foreach (string[] f in cc)
            {
                CombatComponent c = comps.FirstOrDefault(x => x.Id == f[1]);
                string[] mine = c == null ? null : new[]
                {
                    c.Id, c.Slot, c.Carrier, c.SubtypeKey, c.NameKey, c.DescKey, c.LegacyOrgan, F(c.Damage), c.Turret, c.TurretReasonKey, c.TurretReadingKey,
                    F(c.Approach), F(c.Cone), F(c.Area), F(c.FieldSeconds), F(c.FieldDpsRatio), c.Drones.ToString(CultureInfo.InvariantCulture), F(c.DroneSeconds),
                    F(c.DroneRatio), F(c.DroneCooldown), F(c.DroneLeash), F(c.Knockback), F(c.TurretKnockback), c.Scrap.ToString(CultureInfo.InvariantCulture),
                    c.Load.ToString(CultureInfo.InvariantCulture), c.Source, c.Icon,
                };
                Compare(f, mine, cdiff);
            }
            Expect(cc.Count == comps.Count && comps.Count >= 11 && cdiff.Count == 0, $"fg.TbCombatComponent {comps.Count} 行与源 {cc.Count} 行逐字段一致{Detail(cdiff)}");

            var fields = rows.Select(r => r.Field).Distinct().ToList();
            var missing = new List<string>();
            foreach (string field in fields)
            {
                foreach (FirmwareCarrier c in CarrierReadings.AllCarriers)
                {
                    if (!rows.Any(r => r.Field == field && r.Carrier == CarrierReadings.CarrierKey(c)))
                    {
                        missing.Add(field + "." + c);
                    }
                }
            }
            var fwFields = FirmwareKinds.Rows.SelectMany(r => CarrierReadings.ParseFields(r.ReadFields).Select(x => x.Field)).Distinct().ToList();
            var undeclared = fwFields.Where(f => !fields.Contains(f)).ToList();
            var noFields = FirmwareKinds.Rows.Where(r => CarrierReadings.ParseFields(r.ReadFields).Count == 0).Select(r => r.Id).ToList();
            Expect(fields.Count == 27 && missing.Count == 0 && undeclared.Count == 0 && noFields.Count == 0 && FirmwareKinds.Rows.Count == FirmwareCatalog.ExpectedCount,
                $"{fields.Count} 个读法字段在 5 种载体上都有行；44 条固件都声明了读法字段、字段都在表里{Detail(missing.Concat(undeclared).Concat(noFields).ToList())}");
            var carriersWithMain = comps.Where(c => c.Slot == "main").Select(c => c.Carrier).Distinct().ToList();
            Expect(CarrierReadings.AllCarriers.All(c => carriersWithMain.Contains(CarrierReadings.CarrierKey(c))),
                $"5 种载体都有主组件（{string.Join("、", comps.Where(c => c.Slot == "main").Select(c => c.Id + "=" + c.Carrier))}）");
        }

        // ── B. 旧引擎：扩展现有的零死对断言 ────────────────────────────────────────

        private static void CheckLegacyMatrix()
        {
            Line("  · B1. 旧引擎读法层：扩展 ChassisPrimitiveMatrixSmokeReport 到固件表——42 条带旧基因的固件 × 5 种载体器官真起 SimWorld 零死对 + 命名实证（跃击 / 击退 / 修复 / 增殖 / 连网 / 回波）");
            string[] legacy = FirmwareKinds.Rows.Where(r => r.LegacyId != "none").Select(r => r.LegacyId).ToArray();
            var sw = Stopwatch.StartNew();
            (bool pass, string reason) = ChassisPrimitiveMatrixSmokeReport.Run(legacy);
            sw.Stop();
            Expect(legacy.Length == 42 && pass, $"旧引擎 {legacy.Length} 条 × 5 载体：{reason}（{sw.ElapsedMilliseconds} 毫秒）");
            string[] native = FirmwareKinds.Rows.Where(r => r.LegacyId == "none").Select(r => r.Id).OrderBy(x => x).ToArray();
            Expect(native.SequenceEqual(new[] { FirmwareCatalog.FwArmorPierceId, FirmwareCatalog.FwMarkTagId }),
                $"没有旧基因的 {native.Length} 条（装甲击穿、标记跳转）只有原生读法字段，它们的零死对由 C 段正式内核矩阵证明");
        }

        /// <summary>读法字段 → 旧引擎 HitEvent 上看得见的一等字段（与 fgdata_firmware.FIRMWARE_READ_FIELDS 的文件头约定一致）。</summary>
        private static readonly Dictionary<string, string> FieldToEngine = new Dictionary<string, string>
        {
            ["homing"] = "Homing", ["count"] = "Count", ["spread"] = "SpreadAngle", ["split"] = "SplitOnHit", ["echo"] = "Delay", ["pierce"] = "Pierce",
            ["bounce"] = "Bounce", ["return"] = "Return", ["scale"] = "Scale", ["explode"] = "ExplodeOnHit", ["orbit"] = "Spin", ["trail"] = "Trail",
            ["linger"] = "Linger", ["chain"] = "Chain", ["pull"] = "Pull", ["lob"] = "Gravity", ["grow"] = "GrowthRate", ["rhythm"] = "RhythmRate",
            ["amp"] = "ReactionAmp", ["weave"] = "Weave", ["tick"] = "TickRate", ["capacitor"] = "Damage", ["feedback"] = "Damage",
            ["heatburst"] = "AuraRadius", ["inherit"] = "tag:InheritPattern",
        };

        private static void CheckDeclaredFields()
        {
            Line("  · B2. 每条固件声明的读法字段 = 旧引擎等价模块真实写出的字段（旧引擎真实编译 EnergyCore → 基因 → 连射器，与只有连射器时对比；过载加产热底料）；幅度一致");
            var bad = new List<string>();
            int checkedCount = 0;
            foreach (FwRow r in FirmwareKinds.Rows.Where(x => x.LegacyId != "none"))
            {
                var declared = CarrierReadings.ParseFields(r.ReadFields);
                bool primer = declared.Any(d => d.Field == "heatburst");
                var delta = ChassisPrimitiveMatrixSmokeReport.ProbeFieldDeltas("org_emitter", r.LegacyId, primer);
                foreach ((string field, float mag) in declared)
                {
                    if (!FieldToEngine.TryGetValue(field, out string engine))
                    {
                        bad.Add($"{r.Id}.{field} 没有旧引擎对应字段");
                        continue;
                    }
                    string key = engine;
                    if (key == "Delay" && !delta.ContainsKey("Delay") && delta.ContainsKey("Payload.Delay"))
                    {
                        key = "Payload.Delay";
                    }
                    if (!delta.TryGetValue(key, out (float Base, float With) v))
                    {
                        bad.Add($"{r.Id} 声明 {field} 但模块没写 {engine}（实际改了 {string.Join("/", delta.Keys)}）");
                        continue;
                    }
                    float observed = field switch
                    {
                        "count" => v.With - Math.Max(1f, v.Base),
                        "capacitor" => v.Base > 0f ? v.With / v.Base : 0f,
                        "feedback" => v.Base > 0f ? v.With / v.Base - 1f : 0f,
                        "return" or "explode" or "inherit" or "heatburst" => 1f,
                        _ => v.With,
                    };
                    if (Math.Abs(observed - mag) > 0.02f * Math.Max(1f, Math.Abs(mag)))
                    {
                        bad.Add($"{r.Id}.{field} 声明幅度 {mag:0.###}，旧引擎是 {observed:0.###}（{engine} {v.Base:0.###}→{v.With:0.###}）");
                    }
                }
                // 反方向：模块写出、却没声明的一等字段（标签除外：标签的效果走 fg.TbStatusTag）
                foreach (string k in delta.Keys.Where(k => !k.StartsWith("tag:", StringComparison.Ordinal)))
                {
                    string expectField = FieldToEngine.FirstOrDefault(kv => kv.Value == k || (k == "Payload.Delay" && kv.Value == "Delay")).Key;
                    bool coveredBySibling = (k == "RadialRequested" && declared.Any(d => d.Field == "count"))
                                            || (k == "Orbit" && declared.Any(d => d.Field == "orbit"))
                                            || (k == "Scale" && declared.Any(d => d.Field == "capacitor"))
                                            || (k == "Speed" || k == "Lifetime") && declared.Any(d => d.Field == "lob")
                                            || (k == "Damage" && declared.Any(d => d.Field == "capacitor" || d.Field == "feedback"))
                                            || (k == "AuraRadius" && !primer);
                    if (!coveredBySibling && (expectField == null || declared.All(d => d.Field != expectField)))
                    {
                        bad.Add($"{r.Id} 的模块还写了 {k}（{delta[k].Base:0.###}→{delta[k].With:0.###}），但没声明对应的读法字段");
                    }
                }
                checkedCount++;
            }
            Expect(checkedCount == 42 && bad.Count == 0, $"42 条带旧基因的固件：声明字段 = 模块真实写出的字段，幅度一致{Detail(bad)}");
        }

        // ── C. 正式内核 44 × 5 零死对（FGT-FW-001）────────────────────────────────

        private static void CheckKernelMatrix()
        {
            Line("  · C. FGT-FW-001：正式版战斗内核 44 条固件 × 5 种载体（连射器 / 液压刺锤 / 蜂群无人机舱 / 电晕场 / 喷洒器）零死对——同一把武器“读法开 / 读法关”" +
                 "（伤害、积热完全相同，只差读法）在带正面装甲、预先标记、预先挂状态标签的场地里经编队攻击命令真打 4 游戏秒，行为指纹（敌方耐久与位置、本机耐久与位置、" +
                 "开火次数、区域 / 回波 / 无人机）必须不同；指纹不含积热与状态位（“字段有差异”不算数）");
            CampaignState s = NewState(9101, unlockAll: true);
            var dead = new List<string>();
            int combos = 0;
            var sw = Stopwatch.StartNew();
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                CombatWeapon plain = WeaponFor(comp, HomeValleyLayout.Erc003ChassisId);
                foreach (FwRow r in FirmwareKinds.Rows)
                {
                    combos++;
                    CombatWeapon with = WeaponFor(comp, HomeValleyLayout.Erc003ChassisId, r.Id);
                    CombatWeapon off = with;
                    off.Reading = plain.Reading; // 读法关：投送参数与伤害、积热都一样
                    if (with.Reading.Carrier != (CombatCarrier)(byte)carrier)
                    {
                        dead.Add($"{carrier}/{r.Id}（载体不对：{with.Reading.Carrier}）");
                        continue;
                    }
                    string a = RunArena(with, 4f, prepared: true).Digest;
                    string b = RunArena(off, 4f, prepared: true).Digest;
                    if (a == b)
                    {
                        dead.Add($"{carrier}/{r.Id}");
                    }
                }
            }
            sw.Stop();
            _ = s;
            Expect(combos == 220 && dead.Count == 0,
                $"{combos} 对 (载体, 固件) 在正式内核里全部有可观察行为差异，死对 {dead.Count} 对（{sw.ElapsedMilliseconds} 毫秒）{Detail(dead)}");
        }

        // ── D. 读法逐条实证 ────────────────────────────────────────────────────

        private static void CheckNamedReadings()
        {
            Line("  · D. 读法逐条实证：差异就是短语说的那件事（内核里真打，读法开 / 关对照）");
            NewState(9102, unlockAll: true);
            string gun = ComponentCatalog.CompGunId, ram = ComponentCatalog.CompRamId, bay = ComponentCatalog.CompDroneBayId;
            string corona = ComponentCatalog.CompCoronaId, sprayer = ComponentCatalog.CompSprayerId;
            string ch = HomeValleyLayout.Erc003ChassisId;

            (Obs on, Obs off) Pair(string comp, string fw, bool prepared = true, float seconds = 4f)
            {
                CombatWeapon w = WeaponFor(comp, ch, fw);
                CombatWeapon o = w;
                o.Reading = WeaponFor(comp, ch).Reading;
                return (RunArena(w, seconds, prepared), RunArena(o, seconds, prepared));
            }

            (Obs on, Obs off) = Pair(gun, FirmwareCatalog.FwHomingId);
            Expect(Dmg(on, ArenaP) > Dmg(off, ArenaP) * 1.3f, $"寻的 · 射弹 = 破甲：带正面装甲的主目标吃伤害 {Dmg(on, ArenaP):0} > 读法关 {Dmg(off, ArenaP):0}（绕开正面装甲）");
            (on, off) = Pair(gun, "fw_scatter");
            Expect(Damaged(on, ArenaP) >= Damaged(off, ArenaP) + 2, $"霰射 · 射弹 = 额外目标：主目标以外被打到的敌人 {Damaged(on, ArenaP)} 个 ≥ 读法关 {Damaged(off, ArenaP)} + 2");
            (on, off) = Pair(gun, "fw_pierce");
            Expect(Dmg(on, ArenaBehind) > 0f && Dmg(off, ArenaBehind) == 0f, $"贯穿 · 射弹 = 穿透：主目标正后方的敌人吃到 {Dmg(on, ArenaBehind):0} 伤害（读法关 0）");
            (on, off) = Pair(gun, "fw_arcchain");
            Expect(Damaged(on, ArenaP) >= 2 && Damaged(off, ArenaP) == 0, $"电弧 · 射弹 = 连锁：跳到 {Damaged(on, ArenaP)} 个其他敌人（读法关 0）");
            (on, off) = Pair(gun, "fw_corrode");
            Expect(Dmg(on, ArenaNear) > 0f && Dmg(off, ArenaNear) == 0f, $"腐蚀弹头 · 射弹 = 命中爆开：主目标旁边的敌人吃到溅射 {Dmg(on, ArenaNear):0}（读法关 0）");
            (on, off) = Pair(gun, "fw_coolant");
            Expect(on.Zones > 0 && off.Zones == 0 && on.HostileHp < off.HostileHp, $"冷却液 · 射弹 = 命中处留区域：生成 {on.Zones} 块（读法关 0），敌方总耐久更低");
            (on, off) = Pair(gun, "fw_tractor");
            Expect(Dist(on, ArenaNear, ArenaP) < Dist(off, ArenaNear, ArenaP) - 0.5f, $"牵引 · 射弹：附近敌人被拉向命中点（距离 {Dist(on, ArenaNear, ArenaP):0.0} < 读法关 {Dist(off, ArenaNear, ArenaP):0.0}）");
            (on, off) = Pair(ram, "fw_ricochet");
            Expect(math.length(on.Pos[ArenaP]) > math.length(off.Pos[ArenaP]) + 0.5, $"跳弹 · 格斗 = 击退：主目标被推到离原点 {math.length(on.Pos[ArenaP]):0.0} 米（读法关 {math.length(off.Pos[ArenaP]):0.0}）");
            (on, off) = Pair(ram, "fw_arc");
            Expect(on.AttackerPos.x > off.AttackerPos.x + 0.3, $"曲射 · 格斗 = 跃击：本机扑到 x={on.AttackerPos.x:0.00}（读法关 {off.AttackerPos.x:0.00}）");
            (on, off) = Pair(corona, FirmwareCatalog.FwExecuteId);
            Expect(!on.Alive[ArenaLow] && off.Alive[ArenaLow], "处决 · 力场：剩余耐久 6% 的敌人被一击击毁（读法关仍存活）");
            (on, off) = Pair(gun, "fw_reclaim");
            Expect(on.AttackerHp > off.AttackerHp + 1f, $"回收修复 · 射弹：本机耐久 {on.AttackerHp:0.0} > 读法关 {off.AttackerHp:0.0}（伤害转成维修量）");
            Obs drone = RunArena(WeaponFor(bay, ch), 4f, true);
            Obs swarm = RunArena(WeaponFor(bay, ch, FirmwareCatalog.FwSwarmId), 4f, true);
            Obs escort = RunArena(WeaponFor(gun, ch, FirmwareCatalog.FwSwarmId), 4f, true);
            Expect(drone.MaxDrones == 2 && drone.HostileHp < drone.StartHp && swarm.MaxDrones == 4 && escort.MaxDrones == 1,
                $"无人机载体：蜂群舱挂 {drone.MaxDrones} 架并真的打掉敌人耐久；集群协议 · 无人机 = 多 2 架（{swarm.MaxDrones}）；集群协议 · 射弹 = 1 架伴飞（{escort.MaxDrones}）");
            Obs aura = RunArena(WeaponFor(corona, ch), 3f, true);
            Expect(aura.DamagedCount >= 3, $"力场载体：电晕场一圈脉冲打到 {aura.DamagedCount} 个敌人（半径 4 米内全部）");
            Obs field = RunArena(WeaponFor(sprayer, ch), 3f, true);
            Expect(field.Zones >= 1 && field.MaxZonesAlive >= 1, $"布区载体：喷洒器在目标脚下铺区域（共 {field.Zones} 块，同时最多 {field.MaxZonesAlive} 块）");
            (on, off) = Pair(gun, "fw_echo");
            Expect(on.Echoes > 0 && Dmg(on, ArenaP) > Dmg(off, ArenaP), $"回波 · 射弹：排了 {on.Echoes} 次回波，主目标伤害 {Dmg(on, ArenaP):0} > {Dmg(off, ArenaP):0}");
            (on, off) = Pair(ram, FirmwareCatalog.FwMarkTagId);
            (Obs onU, Obs offU) = Pair(ram, FirmwareCatalog.FwMarkTagId, prepared: false);
            float MarkedDmg(Obs o) => Dmg(o, ArenaNear) + Dmg(o, ArenaMarked) + Dmg(o, ArenaFlank);
            Expect(MarkedDmg(on) > MarkedDmg(off) && onU.Digest == offU.Digest,
                $"标记跳转 · 格斗：命中已标记的主目标时跳到附近已标记的敌人（它们共吃 {MarkedDmg(on):0} > 读法关 {MarkedDmg(off):0}）；场上没有标记时读法开 / 关完全相同（条件读法，不凭空跳）");
            (on, off) = Pair(gun, FirmwareCatalog.FwAmplifyId);
            (onU, offU) = Pair(gun, FirmwareCatalog.FwAmplifyId, prepared: false);
            Expect(Dmg(on, ArenaP) > Dmg(off, ArenaP) * 1.3f && Math.Abs(Dmg(onU, ArenaP) - Dmg(offU, ArenaP)) < 0.01f,
                $"反应增幅 · 射弹：主目标带状态标签时伤害 +50%（{Dmg(on, ArenaP):0} vs {Dmg(off, ArenaP):0}）；不带标签时不变");
            (on, off) = Pair(gun, FirmwareCatalog.FwOverloadId, seconds: 6f);
            Obs early = RunArena(WeaponFor(gun, ch, FirmwareCatalog.FwOverloadId), 0.9f, true);
            Obs earlyOff = RunArena(WithReading(WeaponFor(gun, ch, FirmwareCatalog.FwOverloadId), WeaponFor(gun, ch).Reading), 0.9f, true);
            Expect(Damaged(on, ArenaP) > Damaged(off, ArenaP) && Damaged(early, ArenaP) == Damaged(earlyOff, ArenaP),
                $"过载 · 射弹 = 过热爆发：开火前 1 秒积热不到一半、不爆（旁边被打到 {Damaged(early, ArenaP)} = {Damaged(earlyOff, ArenaP)}）；积热过半后每发在命中点再爆一圈（6 秒后 {Damaged(on, ArenaP)} > {Damaged(off, ArenaP)}）");
            (on, off) = Pair(ComponentCatalog.CompCannonId, FirmwareCatalog.FwCapacitorId, seconds: 8f);
            Obs gunCap = RunArena(WeaponFor(gun, ch, FirmwareCatalog.FwCapacitorId), 4f, true);
            Obs gunCapOff = RunArena(WithReading(WeaponFor(gun, ch, FirmwareCatalog.FwCapacitorId), WeaponFor(gun, ch).Reading), 4f, true);
            // 单发伤害比（总伤害 / 出手次数）：出手间隔也乘了倍率之后，8 秒里读法开可能少打一发，所以比单发，不比总量。
            float perOn = on.Shots > 0 ? Dmg(on, ArenaP) / on.Shots : 0f, perOff = off.Shots > 0 ? Dmg(off, ArenaP) / off.Shots : 0f;
            Expect(gunCap.Shots < gunCapOff.Shots && on.Shots > 0 && off.Shots > 0 && perOn > perOff * 1.5f,
                $"电容蓄力：连射器出手次数 {gunCap.Shots} < {gunCapOff.Shots}（少发）；重炮单发伤害 × 1.6（8 秒主目标单发 {perOn:0} > {perOff:0}，出手 {on.Shots} / {off.Shots} 次）");
            // 重炮自己的冷却也乘出手间隔倍率（此前只乘编队命令的冷却，重炮净赚 60% 输出）：12 秒里读法关打 3 发（瞄准 1 + 冷却 3），读法开只打 2 发（冷却 × 1.6）。
            (Obs cOn, Obs cOff) = Pair(ComponentCatalog.CompCannonId, FirmwareCatalog.FwCapacitorId, seconds: 12f);
            CombatWeapon cannonCap = WeaponFor(ComponentCatalog.CompCannonId, ch, FirmwareCatalog.FwCapacitorId);
            Expect(cannonCap.Reading.CooldownScale > 1.5f && cOn.Shots < cOff.Shots && cOff.Shots >= 3,
                $"电容蓄力 · 重炮：出手间隔 × {cannonCap.Reading.CooldownScale:0.##}——12 秒出手 {cOn.Shots} 次 < 读法关 {cOff.Shots} 次（不是白拿单发伤害）");
            // 显示 / 预测 / 家园靶用的每发伤害与内核同源（B13）：新组件 = 表里的伤害，重炮 = 55 × 伤害倍率，连射器 = 电路编译伤害。
            {
                BlueprintCircuitPreview Prev(string comp, params string[] fws)
                {
                    BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(ch, comp, null, null, Array.Empty<string>());
                    b.TrySetUplink(UplinkSlot);
                    return UplinkCompiler.CompileUplinked(b, fws);
                }
                CarrierReadings.TryGetComponent(ram, out CombatComponent ramRow);
                BlueprintCircuitPreview ramP = Prev(ram), gunP = Prev(gun), capP = Prev(ComponentCatalog.CompCannonId, FirmwareCatalog.FwCapacitorId);
                float ramHit = CombatSite.MachineHitDamage(ramP), gunHit = CombatSite.MachineHitDamage(gunP), capHit = CombatSite.MachineHitDamage(capP);
                CombatWeapon capW = CombatSite.MachineWeaponFrom(capP);
                Expect(Mathf.Approximately(ramHit, ramRow.Damage) && Mathf.Approximately(ramHit, CombatSite.MachineWeaponFrom(ramP).Damage)
                       && Mathf.Approximately(gunHit, gunP.TotalNormalizedDamage)
                       && Mathf.Approximately(capHit, capW.Damage * capW.Reading.DamageScale) && capHit > FracturedCityLayout.CannonBaseDamage * 1.5f
                       && CombatSite.MachineHitDamage(null) == 0f,
                    $"界面 / 远征预测 / 家园靶显示的每发伤害与内核同源：液压刺锤 {ramHit:0.#}（表 {ramRow.Damage:0.#}，不是旧器官编译的 {ramP.TotalNormalizedDamage:0.#}）；连射器 {gunHit:0.#}；重炮 + 电容蓄力 {capHit:0.#}");
            }
            // 直控点击：非重炮武器一次点击一发（Demo 语义）；蓄力读法要等蓄满（基础出手间隔 × 倍率）才能再点出。
            {
                CombatWeapon gunC = WeaponFor(gun, ch, FirmwareCatalog.FwCapacitorId);
                CombatWeapon gunPlain = WeaponFor(gun, ch);
                float charge = gunC.Cooldown * gunC.Reading.CooldownScale;
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                using var k = new CombatKernel(cfg, 8);
                int wc = k.AddWeapon(gunC);
                int wn = k.AddWeapon(gunPlain);
                int mc = k.Spawn(Machine(double2.zero, wc));
                int mn = k.Spawn(Machine(new double2(0, 4), wn));
                int e = k.Spawn(Hostile(new double2(3, 0), 5000f, 5000f));
                CombatFireResult c1 = k.FireAt(mc, e, 0.0);
                CombatFireResult c2 = k.FireAt(mc, e, 0.5);
                CombatFireResult c3 = k.FireAt(mc, e, charge + 0.01);
                CombatFireResult n1 = k.FireAt(mn, e, charge + 0.02);
                CombatFireResult n2 = k.FireAt(mn, e, charge + 0.03);
                Expect(charge > 1.5f && c1 == CombatFireResult.Ok && c2 == CombatFireResult.Cooldown && c3 == CombatFireResult.Ok
                       && n1 == CombatFireResult.Ok && n2 == CombatFireResult.Ok,
                    $"直控 + 电容蓄力：点一发后 0.5 秒再点 = {c2}（蓄力中），{charge:0.00} 秒后 = {c3}；不带蓄力的连射器照旧一次点击一发（{n1} / {n2}）");
            }
            (on, off) = Pair(gun, "fw_bridge");
            CombatWeapon bridge = WeaponFor(gun, ch, "fw_bridge");
            CarrierReading weaveRow = CarrierReadings.Rows.First(r => r.Field == "weave" && r.Carrier == "projectile");
            string bridgeText = FirmwareKinds.Reading("fw_bridge", FirmwareCarrier.Projectile);
            Expect(on.Zones > 0 && off.Zones == 0 && on.SlowedCount > 0
                   && Mathf.Approximately(bridge.Reading.WeaveSeconds, weaveRow.B) && Mathf.Approximately(bridge.Reading.WeaveSlow, weaveRow.C)
                   && Mathf.Approximately(bridge.Reading.WeaveDpsRatio, CombatSite.Tuning("reading.weave.dps_ratio", -1f))
                   && bridgeText.Contains(Num(weaveRow.B)) && bridgeText.Contains(Num(weaveRow.C * 100.0)),
                $"桥接 · 射弹 = 连网：在主目标与旁边敌人之间拉出 {on.Zones} 块减速网（挂减速 {on.SlowedCount} 个）；持续 {bridge.Reading.WeaveSeconds} 秒 / 减速 {bridge.Reading.WeaveSlow * 100:0}% / 伤害比例 {bridge.Reading.WeaveDpsRatio:0.##} 都来自表（fg.TbCarrierReading weave 行、reading.weave.dps_ratio），说明“{bridgeText}”写的是同一个数");
            (on, off) = Pair(gun, FirmwareCatalog.FwArmorPierceId);
            Expect(Dmg(on, ArenaP) > Dmg(off, ArenaP) * 1.5f, $"装甲击穿 · 射弹：带正面装甲的主目标吃伤害 {Dmg(on, ArenaP):0} > {Dmg(off, ArenaP):0}（DEBT-FG1SIG06-03 关闭）");

            // 具名反应取代触发固件的普通读法：重炮 + 过载 = 熔穿过载（Demo 数值不变），连射器 + 过载 = 过热爆发 + 燃烧。
            CombatWeapon cannonOverload = WeaponFor(ComponentCatalog.CompCannonId, ch, FirmwareCatalog.FwOverloadId);
            CombatWeapon gunOverload = WeaponFor(gun, ch, FirmwareCatalog.FwOverloadId);
            Expect(cannonOverload.Reaction == CombatReaction.MeltOverload && cannonOverload.Reading.HeatBurstAt == 0f && cannonOverload.Reading.StatusMask == 0u
                   && gunOverload.Reaction == CombatReaction.None && gunOverload.Reading.HeatBurstAt > 0f && gunOverload.Reading.StatusDps > 0f,
                "具名反应取代触发固件的普通读法：重炮 + 过载只有熔穿过载（不叠过热爆发 / 燃烧，Demo 数值不变）；连射器 + 过载是过热爆发 + 燃烧");

            // 减速：区域挂上的“减速”真的让敌人走得慢（内核规则）。
            float moved = RaiderTravel(slow: 0f);
            float slowed = RaiderTravel(slow: 0.4f);
            Expect(slowed < moved * 0.7f && slowed > moved * 0.5f, $"减速状态：同样 2 游戏秒，突袭者走 {slowed:0.0} 米（正常 {moved:0.0} 米，-40%）；到期后恢复");
            (on, off) = Pair(gun, FirmwareCatalog.FwTrailId);
            Expect(on.SlowedCount > 0 && off.SlowedCount == 0 && on.Zones > 0,
                $"拖尾 · 射弹：飞行路径上的区域给站在里面的敌人挂减速（{on.SlowedCount} 个；读法关 0）");
        }

        // ── E. 固定底盘兼容表（FGR-FW-012）─────────────────────────────────────

        private static void CheckFixedChassis()
        {
            Line("  · E. 固定底盘（炮塔）兼容表：冲刺器不兼容（拒绝并说明，装配入口 / 换底盘 / 保存校验 / 真实保存一致）；推铲读作击退铲（内核真推开 3 米）；其余直接兼容；移动底盘不受影响；未解锁时换不上");
            CampaignState s = NewState(9103, unlockAll: true);
            s.UnlockedContentIds = s.UnlockedContentIds.Concat(ComponentCatalog.All.Keys).Append(ChassisCatalog.ChassisFixedId).Distinct().ToArray();
            string fixedId = ChassisCatalog.ChassisFixedId;
            var table = new List<string>();
            foreach (CombatComponent c in CarrierReadings.Components)
            {
                bool ok = CarrierReadings.CanMount(fixedId, c.Id, out string reason, out string adjusted);
                bool mobileOk = CarrierReadings.CanMount(HomeValleyLayout.Erc003ChassisId, c.Id, out string mr, out string ma) && mr == null && ma == null;
                bool expect = c.Turret switch
                {
                    "no" => !ok && reason == c.TurretReasonKey && adjusted == null,
                    "adjusted" => ok && adjusted == c.TurretReadingKey && reason == null,
                    _ => ok && reason == null && adjusted == null,
                };
                if (!expect || !mobileOk)
                {
                    table.Add($"{c.Id}（{c.Turret}：固定 {ok}/{reason}/{adjusted}；移动 {mobileOk}）");
                }
            }
            Expect(table.Count == 0 && CarrierReadings.Components.Count(c => c.Turret == "no") == 1
                   && CarrierReadings.TryGetComponent(ComponentCatalog.FuncDashId, out CombatComponent dash) && dash.Turret == "no"
                   && CarrierReadings.TryGetComponent(ComponentCatalog.CompShovelId, out CombatComponent sh) && sh.Turret == "adjusted",
                $"兼容表 {CarrierReadings.Components.Count} 个作战组件：冲刺器不兼容、推铲兼容但读法调整、其余直接兼容；移动底盘全部可装{Detail(table)}");

            string dashName = ComponentCatalog.All[ComponentCatalog.FuncDashId].DisplayName;
            BlueprintCircuitBoard turret = BlueprintCircuitBoard.CreateDefault(fixedId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult setDash = turret.TrySetUtility(s, ComponentCatalog.FuncDashId);
            CircuitOpResult setShovel = turret.TrySetPrimary(s, ComponentCatalog.CompShovelId);
            CircuitOpResult setMarker = turret.TrySetUtility(s, ComponentCatalog.FuncMarkerId);
            Expect(!setDash.Success && setDash.Code == BlueprintCircuitBoard.TurretIncompatibleCode && setDash.Message == GameText.Format("component.turret.reason.dash", dashName)
                   && setDash.Message.Contains(dashName) && setShovel.Success && setMarker.Success && turret.UtilityId == ComponentCatalog.FuncMarkerId,
                $"炮塔蓝图装冲刺器：拒绝，原因“{setDash.Message}”；推铲、标记器照常装上");

            BlueprintCircuitBoard mobile = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncDashId, null, Array.Empty<string>());
            CircuitOpResult switchFixed = mobile.TrySetChassis(s, fixedId);
            Expect(!switchFixed.Success && switchFixed.Code == BlueprintCircuitBoard.TurretIncompatibleCode && switchFixed.Message.Contains(dashName)
                   && mobile.ChassisId == HomeValleyLayout.Erc003ChassisId && mobile.UtilityId == ComponentCatalog.FuncDashId,
                $"装着冲刺器的战斗履带换固定底盘：拒绝并点名（“{switchFixed.Message}”），不偷偷替玩家卸组件");
            CircuitOpResult removeDash = mobile.TrySetUtility(s, null);
            CircuitOpResult switchAgain = mobile.TrySetChassis(s, fixedId);
            Expect(removeDash.Success && switchAgain.Success && mobile.ChassisId == fixedId, "先卸下冲刺器再换固定底盘：成功");

            BlueprintCircuitBoard legacy = BlueprintCircuitBoard.CreateDefault(fixedId, ComponentCatalog.CompGunId, ComponentCatalog.FuncDashId, null, Array.Empty<string>());
            CircuitValidationResult v = legacy.Validate();
            BlueprintSaveResult save = BlueprintEditorService.TrySave(s, legacy, "bp_fw02_turret_dash", "turret dash", saveAsNewRecord: true);
            Expect(v.Issues.Any(i => i.Code == CircuitIssueCode.ComponentNotOnFixedChassis && i.Message.Contains(dashName)) && !save.Success
                   && s.BlueprintRecords.All(b => b.BlueprintId != "bp_fw02_turret_dash"),
                $"旧草稿（固定底盘 + 冲刺器）：保存校验报出原因、真实保存被拒、没有写进蓝图库");

            CampaignState fresh = NewState(9104);
            BlueprintCircuitBoard lockedBoard = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult locked = lockedBoard.TrySetChassis(fresh, fixedId);
            Expect(!locked.Success && locked.Code == "chassis_locked" && locked.Message.Contains(GameText.Get("chassis.fixed.locked"))
                   && !MechanicalContentUnlock.IsUnlocked(fresh, fixedId) && ChassisCatalog.All.ContainsKey(fixedId),
                $"新开局固定底盘未解锁（炮塔随家园防御 FG6-DEF-01 开放）：换不上，原因“{locked.Message}”");

            // 外层槽的撤销（B03）：换主组件 / 换底盘可以撤销、重做，主组件与底盘一起回来（此前只还原内层）。
            BlueprintCircuitBoard undoBoard = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult toRam = undoBoard.TrySetPrimary(s, ComponentCatalog.CompRamId);
            CircuitOpResult toFixed = undoBoard.TrySetChassis(s, fixedId);
            bool undo1 = undoBoard.Undo() && undoBoard.ChassisId == HomeValleyLayout.Erc003ChassisId && undoBoard.PrimaryId == ComponentCatalog.CompRamId;
            bool undo2 = undoBoard.Undo() && undoBoard.PrimaryId == ComponentCatalog.CompGunId;
            bool redo = undoBoard.Redo() && undoBoard.PrimaryId == ComponentCatalog.CompRamId;
            Expect(toRam.Success && toFixed.Success && undo1 && undo2 && redo,
                "蓝图编辑器撤销 / 重做包括外层槽：换固定底盘、换主组件都能一步步撤回再重做");

            // 推铲在炮塔上读作击退铲：内核真推。
            NewState(9105, unlockAll: true);
            CombatWeapon onTurret = WeaponFor(ComponentCatalog.CompShovelId, fixedId);
            CombatWeapon onMobile = WeaponFor(ComponentCatalog.CompShovelId, HomeValleyLayout.Erc003ChassisId);
            TurretObs nearT = TurretProbe(onTurret, 1.6);
            TurretObs nearM = TurretProbe(onMobile, 1.6);
            float pushTurret = nearT.Push;
            float pushMobile = nearM.Push;
            Expect(Mathf.Approximately(onTurret.Reading.Knockback, 3f) && Mathf.Approximately(onMobile.Reading.Knockback, 1f) && onTurret.Reading.Carrier == CombatCarrier.Melee
                   && onTurret.Range > 10f && Mathf.Approximately(onTurret.Cooldown, CombatSite.MachineAttackInterval)
                   && pushTurret > 2.5f && pushMobile > 0.5f && pushMobile < 1.5f && nearT.Shots == 1,
                $"推铲 · 炮塔 = 击退铲（MachineWeaponFrom 原样输出，射程 {onTurret.Range:0} 米、冷却 {onTurret.Cooldown:0.0} 秒，不手改）：驻守开火的炮塔把近身敌人推开 {pushTurret:0.0} 米（移动底盘上的推铲 {pushMobile:0.0} 米）");
            // 负向：6 米外（射程 12 米以内、触及 3 米以外）的敌人——炮塔不出手、不推、不伤（不是“12 米内的敌人全推开”）。
            TurretObs farT = TurretProbe(onTurret, 6.0);
            Expect(farT.Shots == 0 && Math.Abs(farT.Push) < 0.01f && farT.Damage < 0.01f,
                $"击退铲只推近身敌人：6 米外（射程内、触及外）的敌人不被选为目标、不挨打、不被推（开火 {farT.Shots} 次，位移 {farT.Push:0.00} 米，伤害 {farT.Damage:0.0}）");
            // 驻守开火也按固件积热、过热停火（与编队攻击 / 直控同一道门槛）。
            CombatWeapon hot = onTurret;
            hot.HeatPerShot = 60f;
            TurretObs heated = TurretProbe(hot, 1.6);
            TurretObs overheated = TurretProbe(hot, 1.6, preHeat: 200f);
            Expect(heated.Shots == 1 && heated.Heat > 40f && overheated.Shots == 0 && Math.Abs(overheated.Push) < 0.01f,
                $"炮塔驻守开火也积热：一发后热量 {heated.Heat:0}；已过热（热量 200，高于恢复线）时不出手（开火 {overheated.Shots} 次）");
            BlueprintCircuitBoard shovelTurret = BlueprintCircuitBoard.CreateDefault(fixedId, ComponentCatalog.CompShovelId, null, null, Array.Empty<string>());
            string text = CircuitBoardPanelUIToolkit.FirmwareReadingText(shovelTurret);
            Expect(text.Contains(GameText.Get("component.comp_shovel.turret_reading")), $"电路编辑器写明炮塔读法：“{text}”");

            // 核心 / 未破解固件放进炮塔（FGR-SIG-012、FGR-SIG-060，与本表同一入口）。
            bool coreTurret = !FirmwareKinds.CanInstall(s, FirmwareCatalog.FwOverloadId, FirmwareHost.Turret, out string ck) && ck == "signal.reason.core_turret";
            Expect(coreTurret, "核心固件放进炮塔：拒绝（signal.reason.core_turret），与兼容表同一套炮塔判定");
        }

        // ── F. 读法说明对玩家可见（FGR-FW-011）───────────────────────────────────

        private static void CheckReadingTexts()
        {
            Line("  · F. 读法说明（FGR-FW-011）：220 句 = 表里 字段 × 载体 的短语按幅度填数（与内核参数同源）；信号核悬停逐条列 5 种载体；电路编辑器只显示当前主组件载体那一条");
            NewState(9106, unlockAll: true);
            var bad = new List<string>();
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                foreach (FirmwareCarrier c in CarrierReadings.AllCarriers)
                {
                    string text = FirmwareKinds.Reading(r.Id, c);
                    if (string.IsNullOrEmpty(text) || GameText.ContainsMarker(text) || FgFirmwareMigrationSelfCheck.InternalContentId.IsMatch(text))
                    {
                        bad.Add($"{r.Id}.{c} 文本缺失或漏内部 ID：{text}");
                        continue;
                    }
                    foreach ((string field, double mag) in ParseFieldsExact(r.ReadFields))
                    {
                        string phrase = Phrase(field, c, mag);
                        if (phrase == null || !text.Contains(phrase))
                        {
                            bad.Add($"{r.Id}.{c} 缺字段 {field} 的短语“{phrase}”（实际“{text}”）");
                        }
                    }
                }
            }
            Expect(bad.Count == 0, $"44 × 5 = 220 句读法说明逐句由 字段 × 载体 的短语与真实幅度组成{Detail(bad)}");

            // 数值与内核同源：抽几条直接比数。
            CombatWeapon homing = WeaponFor(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, FirmwareCatalog.FwHomingId);
            string homingText = FirmwareKinds.Reading(FirmwareCatalog.FwHomingId, FirmwareCarrier.Projectile);
            CombatWeapon chain = WeaponFor(ComponentCatalog.CompRamId, HomeValleyLayout.Erc003ChassisId, "fw_arcchain");
            string chainText = FirmwareKinds.Reading("fw_arcchain", FirmwareCarrier.Melee);
            Expect(homingText.Contains(Num(homing.Reading.ArmorPierce * 100.0)) && chain.Reading.ChainHits == 2 && chainText.Contains(Num(chain.Reading.ChainRange))
                   && chainText.Contains(Num(chain.Reading.ChainFalloff * 100.0)),
                $"说明里的数就是内核用的数：寻的 · 射弹 破甲 {homing.Reading.ArmorPierce * 100:0.#}%；电弧 · 格斗 跳 {chain.Reading.ChainHits} 个 / {chain.Reading.ChainRange} 米 / 每跳 {chain.Reading.ChainFalloff * 100:0}%");

            string tip = SignalCoreHudUIToolkit.ReadingTip(FirmwareCatalog.FwHomingId);
            int lines = CarrierReadings.AllCarriers.Count(c => tip.Contains(GameText.Format("reading.detail.line", CarrierReadings.CarrierName(c), FirmwareKinds.Reading(FirmwareCatalog.FwHomingId, c))));
            Expect(lines == 5 && tip.Contains(GameText.Get("reading.detail.title")) && SignalCoreHudUIToolkit.ReadingTip("organ_focus") == string.Empty,
                $"信号核槽位 / 基元仓悬停（固件详情）逐条列出 5 种载体的读法；基元芯片不列");

            BlueprintCircuitBoard gunBoard = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { FirmwareCatalog.FwHomingId });
            BlueprintCircuitBoard ramBoard = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompRamId, null, null, new[] { FirmwareCatalog.FwHomingId });
            BlueprintCircuitBoard noPrimary = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, null, null, null, new[] { FirmwareCatalog.FwHomingId });
            string g = CircuitBoardPanelUIToolkit.FirmwareReadingText(gunBoard);
            string m = CircuitBoardPanelUIToolkit.FirmwareReadingText(ramBoard);
            string n = CircuitBoardPanelUIToolkit.FirmwareReadingText(noPrimary);
            string projText = FirmwareKinds.Reading(FirmwareCatalog.FwHomingId, FirmwareCarrier.Projectile);
            string meleeText = FirmwareKinds.Reading(FirmwareCatalog.FwHomingId, FirmwareCarrier.Melee);
            Expect(g.Contains(projText) && !g.Contains(meleeText) && m.Contains(meleeText) && !m.Contains(projText) && n == GameText.Get("reading.circuit.none"),
                $"电路编辑器：连射器上只显示射弹读法、液压刺锤上只显示格斗读法；没有主组件时说明原因（“{n}”）");

            GameSettings.SetLanguage(GameLanguage.En);
            string en = FirmwareKinds.Reading(FirmwareCatalog.FwHomingId, FirmwareCarrier.Aura);
            string enTip = SignalCoreHudUIToolkit.ReadingTip(FirmwareCatalog.FwTrailId);
            bool noHan = !en.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF) && !enTip.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(noHan && en.Length > 10 && !FgFirmwareMigrationSelfCheck.EnglishBioWords.IsMatch(en + enTip), $"英文：“{en}”，不夹中文、不含生物词");
        }

        /// <summary>按与 fgdata_reading.phrase 相同的规则生成一个字段在一种载体上的短语（验证表里生成的文本与运行时表、幅度一致）。</summary>
        /// <summary>与 python 同精度（double）解析幅度：说明文本是 python 按 double 算出来的，float 会在末位四舍五入上差一位。</summary>
        private static List<(string, double)> ParseFieldsExact(string spec)
        {
            var list = new List<(string, double)>();
            foreach (string part in (spec ?? string.Empty).Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon > 0 && double.TryParse(part.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    list.Add((part.Substring(0, colon).Trim(), v));
                }
            }
            return list;
        }

        private static string Phrase(string field, FirmwareCarrier c, double mag)
        {
            CarrierReading first = CarrierReadings.Rows.FirstOrDefault(r => r.Field == field && r.Carrier == CarrierReadings.CarrierKey(c));
            if (first == null)
            {
                return null;
            }
            double a = first.A, b = first.B, cc = first.C;
            switch (first.Mag)
            {
                case "a": a *= mag; break;
                case "b": b *= mag; break;
                case "c": cc *= mag; break;
            }
            return GameText.Get(first.TextKey)
                .Replace("{ap}", Num(a * 100)).Replace("{bp}", Num(b * 100)).Replace("{cp}", Num(cc * 100))
                .Replace("{a}", Num(a)).Replace("{b}", Num(b)).Replace("{c}", Num(cc));
        }

        private static string Num(double v)
        {
            // 与 python f"{v:.2f}" 同一取舍（按 double 的真实值舍入；Mono 的 "0.00" 先按 15 位有效数字取整会把 1.2749999… 舍成 1.28）。
            double r = Math.Round(v * 100.0, MidpointRounding.ToEven) / 100.0;
            string s = r.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return s == "-0" || s.Length == 0 ? "0" : s;
        }

        // ── G. 存读档 ──────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · G. 存读档：内核快照格式 3 带区域 / 回波 / 无人机 / 状态标签往返逐位一致，续跑与不存档连续跑一致；格式 2 旧快照照样读（读法与三张表为空）；坏值整份拒绝");
            NewState(9107, unlockAll: true);
            CombatWeapon w = WeaponFor(ComponentCatalog.CompDroneBayId, HomeValleyLayout.Erc003ChassisId, FirmwareCatalog.FwTrailId, "fw_echo");
            using (Arena a = Arena.Build(w, prepared: true))
            {
                a.Run(2.5f);
                CombatKernel k = a.K;
                bool mid = k.ZoneCount > 0 && k.DroneCount > 0 && k.EchoCount >= 0;
                k.TryGetStatus(a.Ids[ArenaP], out uint mask0, out _, out _, out _, out _);
                byte[] snap = k.Serialize();
                using var b = new CombatKernel(k.Config, 64);
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && b.StateHash() == k.StateHash() && b.ZoneCount == k.ZoneCount && b.DroneCount == k.DroneCount
                            && b.EchoCount == k.EchoCount && b.TryGetStatus(a.Ids[ArenaP], out uint mask1, out _, out _, out _, out _) && mask1 == mask0
                            && CombatKernel.PeekFormat(snap) == 3;
                for (int i = 0; i < 90; i++)
                {
                    double t = k.Time + Dt;
                    k.Step(Dt, t);
                    b.Step(Dt, t);
                }
                Expect(mid && same && b.StateHash() == k.StateHash(),
                    $"战斗中途（区域 {k.ZoneCount} 块、无人机 {k.DroneCount} 架、回波 {k.EchoCount} 次、主目标状态位 0x{mask0:X}）存快照：读回逐位一致，再跑 1.5 秒仍一致");

                byte[] v2 = k.SerializeFormatForTests(2);
                using var c2 = new CombatKernel(k.Config, 64);
                CombatLoadResult l2 = c2.Load(v2);
                bool v2ok = l2 == CombatLoadResult.Ok && CombatKernel.PeekFormat(v2) == 2 && c2.SlotCount == k.SlotCount && c2.ZoneCount == 0 && c2.DroneCount == 0
                            && c2.TryGetUnit(a.Ids[ArenaP], out CombatUnitView pv) && k.TryGetUnit(a.Ids[ArenaP], out CombatUnitView kv) && Math.Abs(pv.Health - kv.Health) < 1e-3f
                            && c2.TryGetWeapon(0, out CombatWeapon ow) && !ow.Reading.HasFirmwareReading && ow.Reading.Carrier == CombatCarrier.Projectile;
                Expect(v2ok, "格式 2（FG2-FW-02 之前）的旧快照：照样读回单位与血量，读法取“无”、区域 / 无人机为空（不崩、不丢单位）");

                byte[] bad = (byte[])snap.Clone();
                using var c3 = new CombatKernel(k.Config, 64);
                int before = c3.SlotCount;
                // 改坏无人机表里的一个数（校验和同时更新，逼到数值校验这一层）。
                bool corrupted = CorruptLastDroneCooldown(bad);
                CombatLoadResult l3 = c3.Load(bad);
                Expect(corrupted && l3 == CombatLoadResult.InvalidValue && c3.SlotCount == before, $"无人机出手间隔写成 0 的快照：整份拒绝（{l3}），内核保持原样");
            }
        }

        // ── H. 正式流程（破碎都市、编队攻击、倍速暂停、观察、存读档）──────────────────

        private const string BpSprayer = "bp_fw02_sprayer";
        private const string BpGunReading = "bp_fw02_gun_trail";

        private static void CheckWorldPipeline()
        {
            Line("  · H. 正式流程：破碎都市里喷洒器 + 冷却液、连射器 + 拖尾 + 霰射两台机器经编队攻击命令打侦察机；0.5x / 1x / 2x / 3x 与暂停、观察 / 不观察、真实存档读档后续跑逐位一致；读法真的在地点内核里生效");
            BuildWorldSave(9108);
            CombatSite city = CombatSites.Get(FracturedCityLayout.RegionId);
            long zones0 = city?.Kernel.Counters.ZonesSpawned ?? -1;
            var hashes = new List<ulong>();
            var zoneCounts = new List<long>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                RestoreWorld();
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + 60 * 8;
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                hashes.Add(SiteHash());
                zoneCounts.Add(CombatSites.Get(FracturedCityLayout.RegionId)?.Kernel.Counters.ZonesSpawned ?? -1);
                GameClock.SetSpeed(1f);
            }
            RestoreWorld();
            ulong before = SiteHash();
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            ulong paused = SiteHash();
            GameClock.SetPaused(false);
            Expect(zones0 > 0 && hashes.Distinct().Count() == 1 && zoneCounts.All(z => z > zones0) && before == paused,
                $"读法在正式地点内核里生效（存档前已生成区域 {zones0} 块，续跑 8 游戏秒后 {string.Join("/", zoneCounts)} 块）；0.5x / 1x / 2x / 3x 哈希一致（{hashes.Distinct().Count()} 种）；暂停 2 秒真实时间不走步（{(before == paused ? "相同" : "不同")}）");

            RestoreWorld();
            WorldView.Observe(FracturedCityLayout.RegionId);
            WorldSimulation.StepMany(240);
            ulong observed = SiteHash();
            RestoreWorld();
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(240);
            ulong unobserved = SiteHash();
            Expect(observed == unobserved, $"观察破碎都市与不观察（看家园）同样跑 4 游戏秒：地点内核哈希一致（{observed:X16}）");

            RestoreWorld();
            WorldSimulation.StepMany(120);
            WorldSimulation.SyncAllForSave();
            SaveResult mid = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(120);
            ulong continuous = SiteHash();
            RestoreWorld();
            WorldSimulation.StepMany(120);
            ulong resumed = SiteHash();
            Expect(mid.Success && continuous == resumed, $"战斗中途真实存档（CampaignAutoSaveService）→ 读档 → 续跑 2 游戏秒 = 不存档连续跑（区域、回波、无人机、状态都进了存档；{continuous:X16} / {resumed:X16}）");

            RestoreWorld();
            CombatSite site = CombatSites.Get(FracturedCityLayout.RegionId);
            CampaignState s = CampaignSession.Current;
            int sprayer = s.MachineRecords.First(m => m.BlueprintId == BpSprayer).LogicId;
            bool hasReading = site != null && site.TryGetMachineUnit(sprayer, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView uv)
                              && site.Kernel.TryGetWeapon(uv.Weapon, out CombatWeapon kw) && kw.Reading.Carrier == CombatCarrier.Field && kw.Reading.ZoneSeconds > 0f;
            Expect(hasReading, "读档后机器的内核武器仍是布区载体 + 冷却液的驻留读法（装配结算重算，不靠旧参数）");
        }

        private static void BuildWorldSave(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgfw02-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            AddBlueprint(s, BpSprayer, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompSprayerId, null, null, new[] { "fw_coolant" }));
            AddBlueprint(s, BpGunReading, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null,
                new[] { FirmwareCatalog.FwTrailId, "fw_scatter" }));
            WorldSimulation.LoadHome(resume: false);
            int a = SpawnMachine(FracturedCityLayout.RegionId, BpSprayer, FracturedCityLayout.Scout2Spawn.Position + new Vector2(-3f, -3f));
            int b = SpawnMachine(FracturedCityLayout.RegionId, BpGunReading, FracturedCityLayout.Scout2Spawn.Position + new Vector2(2f, -3f));
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { a, b }, resume: false);
            WorldView.Observe(HomeValleyLayout.RegionId);
            city.SquadCommands.DebugSelectMany(new[] { a, b });
            city.SquadCommands.IssueAttack(FracturedCityLayout.Scout2SpawnId, paused: false);
            city.SquadCommands.ClearSelection();
            WorldSimulation.StepMany(60);
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("读法战斗存档写入失败：" + r.Message);
            }
        }

        private static void RestoreWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读读法战斗存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            int[] ids = r.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
            WorldSimulation.LoadFracturedCity(ids, resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
        }

        private static ulong SiteHash() => CombatSites.Get(FracturedCityLayout.RegionId)?.Kernel.StateHash() ?? 0;

        private static int SpawnMachine(string region, string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, 200f, 200f, "Player", 1);
            if (!r.Success)
            {
                Fail($"登记测试机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        // ── I. 负向 ────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · I. 负向：区域 / 无人机容量满（不生成、计数、不抛异常）；母机阵亡无人机一起坠毁；回波目标消失不结算；目标不可伤时读法不触发；未知组件 / 空固件安全退化");
            NewState(9109, unlockAll: true);
            CombatWeapon w = WeaponFor(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, "fw_coolant", "fw_scatter");
            CombatConfig tight = CombatSite.ConfigFromTuning();
            tight.ZoneCapacity = 2;
            using (Arena a = Arena.Build(w, prepared: true, tight))
            {
                a.Run(4f);
                Expect(a.K.ZoneCount <= 2 && a.K.Counters.ReadingRefused > 0, $"区域上限 2：同时最多 {a.K.ZoneCount} 块，拒绝 {a.K.Counters.ReadingRefused} 次（不抛异常）");
            }
            CombatWeapon bay = WeaponFor(ComponentCatalog.CompDroneBayId, HomeValleyLayout.Erc003ChassisId);
            using (Arena a = Arena.Build(bay, prepared: true))
            {
                a.Run(1.5f);
                int before = a.K.DroneCount;
                a.K.Kill(a.Ids[ArenaAttacker], 0);
                a.Run(0.1f);
                Expect(before == 2 && a.K.DroneCount == 0, $"母机阵亡：{before} 架无人机下一步一起坠毁（0 架）");
            }
            CombatWeapon echo = WeaponFor(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, "fw_delayfuse");
            using (Arena a = Arena.Build(echo, prepared: true))
            {
                a.Run(0.6f);
                int pending = a.K.EchoCount;
                a.K.Despawn(a.Ids[ArenaP]);
                a.Run(1.5f);
                Expect(pending > 0 && a.K.EchoCount == 0, $"回波目标被移除：{pending} 次待结算的回波直接作废，不报错");
            }
            using (Arena a = Arena.Build(WeaponFor(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, "fw_coolant"), prepared: true))
            {
                a.K.SetFlag(a.Ids[ArenaP], CombatUnitFlags.Invulnerable, true);
                a.Run(2f);
                Expect(a.K.Counters.ZonesSpawned == 0, "主目标当前不可伤（首领阶段）：开火照常但读法不触发（不留区域），与 Demo“不打标记、不跳转”一致");
            }
            // 格斗 / 力场在触及范围外直控开火：拒绝（射程外），不算开火、不积热。
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                using var k = new CombatKernel(cfg, 8);
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompRamId, HomeValleyLayout.Erc003ChassisId, FirmwareCatalog.FwOverloadId));
                int me = k.Spawn(Machine(double2.zero, wi));
                int far = k.Spawn(Hostile(new double2(6, 0), 100f, 100f));
                int near = k.Spawn(Hostile(new double2(2, 0), 100f, 100f));
                CombatFireResult rFar = k.FireAt(me, far, 0);
                long shots = k.Counters.ShotsFired;
                k.TryGetUnit(me, out CombatUnitView before);
                CombatFireResult rNear = k.FireAt(me, near, 0);
                Expect(rFar == CombatFireResult.OutOfRange && shots == 0 && before.Heat == 0f && rNear == CombatFireResult.Ok && k.Counters.ShotsFired == 1,
                    "液压刺锤直控点 6 米外的敌人：射程外（不算开火、不积热）；2 米内正常出手");
            }
            // 集群协议伴飞无人机继承格斗读法（曲射 · 格斗 = 跃击）：无人机自己挑的目标不能把母机拖过去（FGR-BASE-020）。
            // 母机正在撤退（移动命令），伴飞无人机在 10 米牵引绳内打北边的敌人；带跃击与不带跃击两局，母机位置完全一样、北边敌人都挨了无人机的打。
            {
                (double2 pos, float hitB, int drones) Retreat(params string[] fws)
                {
                    CombatWeapon rw = WeaponFor(ComponentCatalog.CompRamId, HomeValleyLayout.Erc003ChassisId, fws);
                    CombatConfig cfg = CombatSite.ConfigFromTuning();
                    cfg.NavEnabled = 0;
                    using var k = new CombatKernel(cfg, 8);
                    int wi = k.AddWeapon(rw);
                    int me = k.Spawn(Machine(double2.zero, wi));
                    int a = k.Spawn(Hostile(new double2(2, 0), 1f, 100f));
                    int b = k.Spawn(Hostile(new double2(0, 7), 5000f, 5000f));
                    k.FireAt(me, a, 0.0);
                    int launched = k.DronesOf(me);
                    k.IssueCommand(me, CombatCommandKind.Move, new double2(-3, 0), 0, 0.3f, 0f, 1f, false);
                    for (int i = 0; i < 240; i++)
                    {
                        k.Step(Dt, k.Time + Dt);
                    }
                    k.TryGetUnit(me, out CombatUnitView mv);
                    k.TryGetUnit(b, out CombatUnitView bv);
                    return (mv.Position, 5000f - bv.Health, launched);
                }
                (double2 lungePos, float lungeHit, int lungeDrones) = Retreat("fw_arc", FirmwareCatalog.FwSwarmId);
                (double2 plainPos, float plainHit, int plainDrones) = Retreat(FirmwareCatalog.FwSwarmId);
                Expect(lungeDrones >= 1 && plainDrones >= 1 && lungeHit > 0f && plainHit > 0f && math.distance(lungePos, plainPos) < 1e-4,
                    $"伴飞无人机继承跃击：母机撤退中，无人机打北边的敌人（挨打 {lungeHit:0} / {plainHit:0}），母机停在 ({lungePos.x:0.00},{lungePos.y:0.00})，与不带跃击时 ({plainPos.x:0.00},{plainPos.y:0.00}) 相同——没被拖向玩家没指定的敌人");
                (double2 pullPos, _, _) = Retreat("fw_tractor", FirmwareCatalog.FwSwarmId);
                Expect(math.distance(pullPos, plainPos) < 1e-4, "伴飞无人机继承回拉 / 牵引：以无人机为中心，母机位置不受影响");
            }
            CombatReading unknown = CarrierReadings.Build("comp_nope", HomeValleyLayout.Erc003ChassisId, new[] { "fw_nope", null, "organ_focus" }, true);
            CombatReading none = CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, true);
            Expect(unknown.Carrier == CombatCarrier.Projectile && !unknown.HasFirmwareReading && !none.HasFirmwareReading
                   && CarrierReadings.FieldsOf("fw_nope").Count == 0 && !CarrierReadings.HasReading("organ_focus")
                   && CarrierReadings.CanMount(null, ComponentCatalog.FuncDashId, out _, out _) && CarrierReadings.CanMount(CarrierReadings.FixedChassisId, ComponentCatalog.StructCargoId, out _, out _),
                "未知组件按射弹、未知 / 不是固件 / 空的固件没有读法；空底盘不拦；结构模块不在作战组件兼容表里、固定底盘照常可装");
        }

        // ── J. 性能 ────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 性能：200 台带读法的机器（射弹 + 驻留 + 霰射、力场、无人机舱、布区）对 200 个敌人，内核单步（Burst）在预算内；装配结算的读法翻译不按帧");
            NewState(9110, unlockAll: true);
            CombatWeapon[] weapons =
            {
                WeaponFor(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, "fw_coolant", "fw_scatter"),
                WeaponFor(ComponentCatalog.CompCoronaId, HomeValleyLayout.Erc003ChassisId, "fw_arcchain"),
                WeaponFor(ComponentCatalog.CompDroneBayId, HomeValleyLayout.Erc003ChassisId, FirmwareCatalog.FwTrailId),
                WeaponFor(ComponentCatalog.CompSprayerId, HomeValleyLayout.Erc003ChassisId, "fw_acid"),
            };
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            using var k = new CombatKernel(cfg, 512);
            int[] wi = weapons.Select(w => k.AddWeapon(w)).ToArray();
            var enemies = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                float ang = i * 0.618f * 2f * Mathf.PI;
                double2 p = new double2(Math.Cos(ang), Math.Sin(ang)) * (6 + (i % 20));
                enemies.Add(k.Spawn(Hostile(p, 50000f, 50000f)));
            }
            for (int i = 0; i < 200; i++)
            {
                float ang = i * 0.618f * 2f * Mathf.PI + 0.3f;
                double2 p = new double2(Math.Cos(ang), Math.Sin(ang)) * (3 + (i % 20));
                int id = k.Spawn(Machine(p, wi[i % wi.Length]));
                k.IssueCommand(id, CombatCommandKind.Attack, p, enemies[i], 0.5f, 6f, 0.5f, false);
            }
            var samples = new List<double>();
            double t = 0;
            for (int i = 0; i < 300; i++)
            {
                t += Dt;
                var sw = Stopwatch.StartNew();
                k.Step(Dt, t);
                sw.Stop();
                if (i >= 60)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            samples.Sort();
            double avg = samples.Average();
            double p95 = samples[(int)(samples.Count * 0.95)];
            double budget = CombatSite.Tuning("combat.perf.step_budget_ms", 6f);
            PerfLines.Add($"内核单步 200 读法机器 + 200 敌人：平均 {avg:0.000} 毫秒、p95 {p95:0.000} 毫秒（区域 {k.ZoneCount}、无人机 {k.DroneCount}、回波 {k.EchoCount}；预算 {budget} 毫秒，Editor batchmode、同步 Burst）");
            Expect(avg < budget && k.Counters.ZonesSpawned > 0 && k.DroneCount > 0, $"单步平均 {avg:0.000} 毫秒 < 预算 {budget} 毫秒（读法确实在跑：区域 {k.Counters.ZonesSpawned} 块、无人机 {k.DroneCount} 架）");

            var swb = Stopwatch.StartNew();
            int builds = 0;
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                foreach ((FirmwareCarrier _, string comp) in CarrierComponents)
                {
                    CarrierReadings.Build(comp, HomeValleyLayout.Erc003ChassisId, new[] { r.Id }, true);
                    builds++;
                }
            }
            swb.Stop();
            double per = swb.Elapsed.TotalMilliseconds / builds;
            PerfLines.Add($"读法翻译 CarrierReadings.Build：{builds} 次共 {swb.Elapsed.TotalMilliseconds:0.0} 毫秒，每次 {per * 1000:0.0} 微秒（只在接入 / 离开 / 装配变更时调用，不按帧；Editor Mono JIT）");
            Expect(per < 0.5, $"读法翻译每次 {per * 1000:0.0} 微秒（装配结算时一次，不按帧）");
        }

        // ── 场地与工具 ─────────────────────────────────────────────────────────

        private const int ArenaAttacker = 0;
        private const int ArenaP = 1;
        private const int ArenaNear = 2;
        private const int ArenaMarked = 3;
        private const int ArenaBehind = 4;
        private const int ArenaLow = 5;
        private const int ArenaBack = 6;
        private const int ArenaSide = 7;
        private const int ArenaFar = 8;
        /// <summary>液压刺锤触及范围内、偏出基础扇形（25°）的敌人：扇形加宽的读法（寻的 / 广角 / 乱流）要有它才看得见。</summary>
        private const int ArenaFlank = 9;
        private const int ArenaCount = 10;

        private sealed class Obs
        {
            public string Digest;
            public float HostileHp;
            public float StartHp;
            public float[] Hp;
            public float[] StartUnitHp;
            public bool[] Alive;
            public double2[] Pos;
            public double2 AttackerPos;
            public float AttackerHp;
            public long Zones;
            public long Echoes;
            public long Shots;
            public int MaxDrones;
            public int MaxZonesAlive;
            public int DamagedCount;
            public int SlowedCount;
        }

        private static float Dmg(Obs o, int idx) => o.StartUnitHp[idx] - o.Hp[idx];

        /// <summary>除 <paramref name="except"/> 以外被打到过的敌人个数。</summary>
        private static int Damaged(Obs o, int except)
        {
            int n = 0;
            for (int i = 1; i < o.Hp.Length; i++)
            {
                if (i != except && o.Hp[i] < o.StartUnitHp[i] - 0.01f)
                {
                    n++;
                }
            }
            return n;
        }

        private static float Dist(Obs o, int i, int j) => (float)math.distance(o.Pos[i], o.Pos[j]);

        private static CombatWeapon WithReading(CombatWeapon w, CombatReading r)
        {
            w.Reading = r;
            return w;
        }

        private sealed class Arena : IDisposable
        {
            public CombatKernel K;
            public readonly int[] Ids = new int[ArenaCount];
            public int MaxDrones;
            public int MaxZones;

            public static Arena Build(CombatWeapon w, bool prepared, CombatConfig? config = null)
            {
                CombatConfig cfg = config ?? CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                var a = new Arena { K = new CombatKernel(cfg, 32) };
                int wi = a.K.AddWeapon(w);
                a.Ids[ArenaAttacker] = a.K.Spawn(Machine(double2.zero, wi, 300f, 500f));
                // 主目标：正面装甲朝着攻击者（减伤 50%）
                CombatSpawn p = Hostile(new double2(3.0, 0.3), 5000f, 5000f);
                p.ArmorFraction = 0.5f;
                p.ArmorHalfAngleDeg = 70f;
                p.ArmorFacing = new float2(-1f, 0f);
                a.Ids[ArenaP] = a.K.Spawn(p);
                a.Ids[ArenaNear] = a.K.Spawn(Hostile(new double2(3.3, 1.7), 5000f, 5000f));
                a.Ids[ArenaMarked] = a.K.Spawn(Hostile(new double2(4.4, -1.1), 5000f, 5000f));
                a.Ids[ArenaBehind] = a.K.Spawn(Hostile(new double2(6.4, 0.64), 5000f, 5000f));
                a.Ids[ArenaLow] = a.K.Spawn(Hostile(new double2(3.7, 1.0), 60f, 1000f));
                a.Ids[ArenaBack] = a.K.Spawn(Hostile(new double2(-2.8, 0.8), 5000f, 5000f));
                a.Ids[ArenaSide] = a.K.Spawn(Hostile(new double2(0.6, 3.4), 5000f, 5000f));
                a.Ids[ArenaFar] = a.K.Spawn(Hostile(new double2(8.5, 3.2), 5000f, 5000f));
                CombatSpawn flank = Hostile(new double2(2.2, 1.35), 5000f, 5000f);
                flank.ArmorFraction = 0.5f;
                flank.ArmorHalfAngleDeg = 70f;
                flank.ArmorFacing = new float2(-1f, -0.6f);
                a.Ids[ArenaFlank] = a.K.Spawn(flank);
                if (prepared)
                {
                    // 场上已有标记（别的机器的标记器打的）与状态标签（“浸湿”）：条件读法（标记跳转、反应增幅）要有这些才看得见。
                    foreach (int idx in new[] { ArenaP, ArenaNear, ArenaMarked, ArenaFlank })
                    {
                        a.K.SetMarkedUntil(a.Ids[idx], 1e9);
                    }
                    CarrierReadings.TryGetTagEffect("Wet", out uint wet, out _, out _);
                    foreach (int idx in new[] { ArenaP, ArenaNear, ArenaFlank })
                    {
                        a.K.ApplyStatus(a.Ids[idx], wet, 1e7f, 0f, 0f, 0f, 0);
                    }
                }
                a.K.IssueCommand(a.Ids[ArenaAttacker], CombatCommandKind.Attack, new double2(3.0, 0.3), a.Ids[ArenaP], 0.5f, 6f, 0.5f, false);
                return a;
            }

            public void Run(float seconds)
            {
                int steps = Mathf.RoundToInt(seconds / Dt);
                for (int i = 0; i < steps; i++)
                {
                    K.Step(Dt, K.Time + Dt);
                    MaxDrones = Math.Max(MaxDrones, K.DronesOf(Ids[ArenaAttacker]));
                    MaxZones = Math.Max(MaxZones, K.ZoneCount);
                }
            }

            public void Dispose() => K?.Dispose();
        }

        private static CombatSpawn Machine(double2 at, int weapon, float hp = 400f, float max = 400f) => new CombatSpawn
        {
            ExtKey = 0,
            Kind = CombatUnitKind.Machine,
            Faction = CombatFaction.Player,
            Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
            Position = at,
            Home = at,
            Radius = 0.6f,
            Speed = 4f,
            Health = hp,
            MaxHealth = max,
            Weapon = weapon,
            BehaviorProfile = -1,
        };

        private static CombatSpawn Hostile(double2 at, float hp, float max) => new CombatSpawn
        {
            ExtKey = 0,
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.5f,
            Speed = 0f,
            Health = hp,
            MaxHealth = max,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        };

        private static Obs RunArena(CombatWeapon w, float seconds, bool prepared)
        {
            using Arena a = Arena.Build(w, prepared);
            var o = new Obs { StartUnitHp = new float[ArenaCount] };
            for (int i = 0; i < ArenaCount; i++)
            {
                a.K.TryGetUnit(a.Ids[i], out CombatUnitView v);
                o.StartUnitHp[i] = v.Health;
                if (i > 0)
                {
                    o.StartHp += v.Health;
                }
            }
            a.Run(seconds);
            o.Hp = new float[ArenaCount];
            o.Alive = new bool[ArenaCount];
            o.Pos = new double2[ArenaCount];
            var sb = new StringBuilder();
            for (int i = 0; i < ArenaCount; i++)
            {
                bool ok = a.K.TryGetUnit(a.Ids[i], out CombatUnitView v);
                o.Hp[i] = ok ? v.Health : 0f;
                o.Alive[i] = ok && v.Alive;
                o.Pos[i] = ok ? v.Position : double2.zero;
                if (i > 0)
                {
                    o.HostileHp += o.Hp[i];
                    if (o.Hp[i] < o.StartUnitHp[i] - 0.01f)
                    {
                        o.DamagedCount++;
                    }
                    if (a.K.TryGetStatus(a.Ids[i], out _, out _, out _, out float slow, out _) && slow > 0f)
                    {
                        o.SlowedCount++;
                    }
                }
                sb.Append(o.Hp[i].ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(o.Pos[i].x.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append(o.Pos[i].y.ToString("F2", CultureInfo.InvariantCulture)).Append(o.Alive[i] ? 'A' : 'D').Append('|');
            }
            CombatCounters c = a.K.Counters;
            o.AttackerPos = o.Pos[ArenaAttacker];
            o.AttackerHp = o.Hp[ArenaAttacker];
            o.Zones = c.ZonesSpawned;
            o.Echoes = c.EchoesQueued;
            o.Shots = c.ShotsFired;
            o.MaxDrones = a.MaxDrones;
            o.MaxZonesAlive = a.MaxZones;
            sb.Append("S").Append(c.ShotsFired).Append("Z").Append(c.ZonesSpawned).Append("E").Append(c.EchoesQueued).Append("D").Append(c.DronesLaunched)
              .Append("M").Append(a.MaxDrones);
            o.Digest = sb.ToString();
            return o;
        }

        /// <summary>突袭者 2 游戏秒走多远（带 / 不带减速状态）。</summary>
        private static float RaiderTravel(float slow)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 8);
            int w = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 1f, Damage = 0f, Cooldown = 99f });
            int prof = k.AddProfile(new CombatBehaviorProfile { Speed = 3f, SenseRange = 0.1f });
            CombatSpawn r = Hostile(double2.zero, 100f, 100f);
            r.Behavior = CombatBehavior.Raider;
            r.Home = new double2(100, 0);
            r.Weapon = w;
            r.BehaviorProfile = prof;
            int id = k.Spawn(r);
            if (slow > 0f)
            {
                CarrierReadings.TryGetTagEffect("Slow", out uint bit, out _, out _);
                k.ApplyStatus(id, bit, 10f, 0f, slow, 0f, 0);
            }
            for (int i = 0; i < 120; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            k.TryGetUnit(id, out CombatUnitView v);
            return (float)v.Position.x;
        }

        private struct TurretObs
        {
            public float Push;
            public float Damage;
            public float Heat;
            public long Shots;
        }

        /// <summary>驻守开火的炮塔（固定底盘，HoldFire）用这把武器（原样，不改射程 / 冷却）1 游戏秒：x = <paramref name="enemyX"/> 的敌人被推开多远、挨了多少伤害，
        /// 炮塔的热量与开火次数。<paramref name="preHeat"/> &gt; 0：开局就是过热状态。</summary>
        private static TurretObs TurretProbe(CombatWeapon w, double enemyX, float preHeat = 0f)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 8);
            int wi = k.AddWeapon(w);
            CombatSpawn t = Machine(double2.zero, wi);
            t.Kind = CombatUnitKind.Turret;
            t.Behavior = CombatBehavior.HoldFire;
            t.Speed = 0f;
            int turret = k.Spawn(t);
            if (preHeat > 0f)
            {
                k.SetWeaponState(turret, preHeat, true, 0, 0);
            }
            int enemy = k.Spawn(Hostile(new double2(enemyX, 0), 5000f, 5000f));
            for (int i = 0; i < 60; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            k.TryGetUnit(enemy, out CombatUnitView v);
            k.TryGetUnit(turret, out CombatUnitView tv);
            return new TurretObs
            {
                Push = (float)(v.Position.x - enemyX),
                Damage = 5000f - v.Health,
                Heat = tv.Heat,
                Shots = k.Counters.ShotsFired,
            };
        }

        /// <summary>把快照里最后一架无人机的出手间隔改成 0，并重算校验和（逼到数值校验）。无人机表在快照末尾：…出手间隔(4) 阵营(1) | 校验和(4)。</summary>
        private static bool CorruptLastDroneCooldown(byte[] data)
        {
            int body = data.Length - 4;
            int at = body - 1 - 4;
            if (at < 12)
            {
                return false;
            }
            data[at] = data[at + 1] = data[at + 2] = data[at + 3] = 0;
            uint h = 2166136261u;
            for (int i = 0; i < body; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            data[body] = (byte)h;
            data[body + 1] = (byte)(h >> 8);
            data[body + 2] = (byte)(h >> 16);
            data[body + 3] = (byte)(h >> 24);
            return true;
        }

        /// <summary>正式装配链：蓝图（主组件 + 2 号格接入口）→ 你接入时插入固件的编译 → CombatSite.MachineWeaponFrom（唯一翻译处）。</summary>
        private static CombatWeapon WeaponFor(string component, string chassis, params string[] firmware)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(chassis, component, null, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(UplinkSlot);
            if (!up.Success)
            {
                Fail($"测试准备：{component} 蓝图 {UplinkSlot} 号格标接入口失败：{up.Message}");
            }
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, firmware ?? Array.Empty<string>());
            if (firmware != null && firmware.Length > 0 && !firmware.All(f => p.FirmwareIds.Contains(f)))
            {
                Fail($"测试准备：{component} 接入 {string.Join("/", firmware)} 没全部生效（生效 {string.Join("/", p.FirmwareIds)}）");
            }
            return CombatSite.MachineWeaponFrom(p);
        }

        private static CampaignState NewState(int seed, bool unlockAll = false)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgfw02-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (unlockAll)
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            }
            return s;
        }

        private static void Compare(string[] src, string[] mine, List<string> diffs)
        {
            if (mine == null || src.Length - 1 != mine.Length)
            {
                diffs.Add(src.Length > 1 ? src[1] + "（缺行或列数不同）" : "空行");
                return;
            }
            for (int i = 0; i < mine.Length; i++)
            {
                if (!SameValue(src[i + 1], mine[i]))
                {
                    diffs.Add($"{src[1]}.第{i + 1}列 源={src[i + 1]} 表={mine[i]}");
                }
            }
        }

        private static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

        private static bool SameValue(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return true;
            }
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                   && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && Math.Abs(x - y) < 1e-4;
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (Exception e)
            {
                return (-1, "启动 python 失败：" + e.Message);
            }
        }

        private static string Detail(List<string> items) =>
            items == null || items.Count == 0 ? string.Empty : "；问题：" + string.Join("；", items.Take(12)) + (items.Count > 12 ? $"……共 {items.Count} 条" : string.Empty);

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

        private static void Expect(bool condition, string message)
        {
            if (condition)
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
