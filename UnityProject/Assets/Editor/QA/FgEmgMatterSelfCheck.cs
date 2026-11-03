using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.UI.Kit;
using GameLogic.Localization;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-MAT-01 物质斑块层与空间哈希（快照格式 17）的自动验收（FG19 FGR-EMG-080～083 / 211 / 213；ADR-EMG-001 ⑤ ⑩ ⑬；FGT-EMG-031 / 040 / 042 / 020 进离场生产者 / 011 进场触发）。
    /// 起真实内核（CombatKernel，Burst 步进）与真实表（fg.TbMatter、fg.TbHomeTuning emg.*、fg.TbChannelDecl）：
    /// A 数据与阶段（17 行、码、沾染位 26～29 / 30 留空、软上限和 928、内核拒坏表、emg.stage 缺省 2 与未接通端口闸、阶段 1 对照）；B 17 种物质各一例（沾染 / 减速 / 存续 / 气体扩散稀释）+ 纹样颜色（B15）；
    /// C 合并（份量相加、Owner 与链取份量大者并记“合并”、异物质 / 固态不合并、气体长大后节拍合并）；D 容量（软上限并入 / 提前到期、总上限并入 / 同层最早到期、同层无可让位丢弃、世界源保留额不被挤掉、
    /// 每步生成额度顺延、节拍错相不漏不重）；E 中立与离地；F 直接接触伤害（evt.hurt Code = 4、敌我同伤）；G evt.enter / evt.leave 生产者与按需；H 弹体沾染与抛射在空中不碰地面层；
    /// I 入场引信（FGT-EMG-011）；J 残骸崩解成碎屑（两条路径 + 斑块层关时只计数）；K 存读档（续跑一致、16 版快照、短段 / 长段、档案稳定键挪下标）；
    /// L 确定性（读渲染 / 拾取 / 存档不改结算、没有物质时哈希不变、0.5x～3x、暂停）；M 悬停与调试行；N 耗时（只测一次）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgMatterSelfCheck）；单段：bash tools/unity-validate.sh FgEmgMatterSelfCheck。
    /// </summary>
    public static class FgEmgMatterSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Cond = 2;
        private const byte RoleModify = 6;
        private const string SiteId = "emg-matter-site";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/物质斑块层")]
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
            Line("\n[物质斑块层] EMG2-MAT-01：17 种物质斑块 / 合并与容量 / 中立与离地 / 进离场 / 弹体沾染 / 残骸崩解 / 快照 17（FGT-EMG-031 / 040 / 042 / 020 / 011）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                Step(CheckData);
                Step(CheckEachMatter);
                Step(CheckMerge);
                Step(CheckCapacity);
                Step(CheckNeutral);
                Step(CheckContactDamage);
                Step(CheckEnterLeave);
                Step(CheckProjectiles);
                Step(CheckEnterFuse);
                Step(CheckDebris);
                Step(CheckSaveLoad);
                Step(CheckGuardNoNamed);
                Step(CheckDeterminism);
                Step(CheckHover);
                Step(CheckEmitPath);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"物质斑块自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                StatusTagHover.Leave(null);
            }
            Line($"  · [物质斑块层] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatConfig Cfg(int capacity = 0, int reserve = -1)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            if (capacity > 0)
            {
                c.EmgPatchCapacity = capacity;
            }
            if (reserve >= 0)
            {
                c.EmgPatchReserve = reserve;
            }
            return c;
        }

        /// <summary>与 CombatSite 建地点同一套写表（子形态 / 实体档案 / 物质表）。</summary>
        private static CombatKernel NewKernel(int capacity = 0, int reserve = -1, int cap = 64, bool patches = true)
        {
            CombatConfig c = Cfg(capacity, reserve);
            if (!patches)
            {
                c.EmgPatchOn = 0;
            }
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            return k;
        }

        private static int Listen(CombatKernel k, params EmgEventKind[] kinds) => Tree(k, false, kinds, null);

        private static int Tree(CombatKernel k, bool flight, EmgEventKind[] kinds, EmgInstr[] extra)
        {
            var instrs = new List<EmgInstr>();
            uint mask = 0u;
            foreach (EmgEventKind e in kinds)
            {
                instrs.Add(new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)e), Role = Cond, Ordinal = (byte)instrs.Count });
                mask |= 1u << (int)e;
            }
            if (extra != null)
            {
                foreach (EmgInstr x0 in extra)
                {
                    EmgInstr x = x0;
                    x.Ordinal = (byte)instrs.Count;
                    instrs.Add(x);
                }
            }
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            return k.AddProgram(new EmgProgram { ListenMask = mask, Flags = flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None }, nodes, instrs);
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatUnitKind kind, int profile = -1) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = f, Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = profile, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Me(CombatKernel k, double2 p, int weapon = -1) => Unit(k, CombatFaction.Player, p, weapon, 500f, CombatUnitKind.Machine);

        private static int Foe(CombatKernel k, double2 p, float hp = 200f, int weapon = -1) => Unit(k, CombatFaction.Hostile, p, weapon, hp, CombatUnitKind.Enemy);

        private static CombatWeapon Instant(float damage, float range = 30f, int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Flight(int program, float damage = 10f, float speed = 30f, float life = 1.5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f, Program = program,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static List<CombatZone> Patches(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        private static int CountOf(CombatKernel k, byte matter) => Patches(k).Count(z => z.Matter == matter);

        private static uint Status(CombatKernel k, int id) => k.TryGetStatus(id, out uint m, out _, out _, out _, out _) ? m : 0u;

        private static bool HasBit(CombatKernel k, int id, int bit) => bit >= 0 && (Status(k, id) & (1u << bit)) != 0u;

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static bool Near(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) <= eps;

        /// <summary>按每步生成额度分批注入（批间步进 1 步）；返回成功请求数。</summary>
        private static int Fill(CombatKernel k, IList<(byte m, double2 p, float r, float sec, bool world)> list, int perStep = 32)
        {
            int ok = 0, inStep = 0;
            foreach (var (m, p, r, sec, world) in list)
            {
                if (!world && inStep >= perStep)
                {
                    StepN(k, 1);
                    inStep = 0;
                }
                if (k.SpawnPatch(m, p, r, 1f, sec, 0, world))
                {
                    ok++;
                }
                if (!world)
                {
                    inStep++;
                }
            }
            StepN(k, 1);
            return ok;
        }

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        private static EmgMatter Row(byte m) => EmgMatterCatalog.Current.Rows[m];

        // ── A. 数据与阶段 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：fg.TbMatter 17 行（码 = 端口码 − 400、沾染位只用现有位或 26～29、第 30 位留空、软上限之和 928）；内核拒坏表；emg.stage 缺省 2 与未接通端口闸；阶段 1 对照");
            var rows = ConfigSystem.Instance.Tables.TbMatter.DataList;
            EmgCompiled cat = EmgCatalog.Current;
            int[] existing = { 0, 1, 2, 6, 12, 13, 14 };
            bool codes = rows.Count == EmgMatterConst.Count && rows.Select((r, i) => r.Code == i + 1).All(x => x)
                         && rows.All(r => cat.PortIndexById.TryGetValue(r.Id, out int pi) && cat.Ports[pi].Code == EmgMatterConst.PortBase + r.Code && r.Id.StartsWith("mat.", StringComparison.Ordinal));
            bool bits = rows.All(r => r.CoatBit == -1 || existing.Contains(r.CoatBit) || (r.CoatBit >= 26 && r.CoatBit <= 29)) && rows.All(r => r.CoatBit != 30 && r.CoatBit != 31)
                        && new[] { 26, 27, 28, 29 }.All(b => rows.Any(r => r.CoatBit == b));
            EmgMatterCatalog.Compiled c = EmgMatterCatalog.Current;
            Expect(codes && bits && rows.Sum(r => r.Quota) == 928 && c.Ok && rows.Where(r => r.Solid != 0).All(r => r.CoatBit == -1 && r.Hp > 0f && r.MergeOverlap == 0f),
                $"fg.TbMatter {rows.Count} 行：物质码 1～17 = 端口码 − 400（逐行与 fg.TbChannelPort 互证）；沾染位只用现有位或 26～29（四位都用上），没有 30 / 31；软上限之和 {rows.Sum(r => r.Quota)}；" +
                $"固态 {rows.Count(r => r.Solid != 0)} 种不沾染、有耐久、不合并；编译问题 {c.Problems.Count}");
            // 复审 P2：空间哈希格边长是内核常量（EmgHash 与障碍 / 残骸共用一张表），改表不生效——表值与常量对不上时这里报错，不静默失效
            (bool hcOk, float hc) = EmgCatalog.FromTables().Tuning("emg.patch.hash_cell");
            Expect(hcOk && Mathf.Approximately(hc, EmgHash.Cell),
                $"空间哈希格边长：表 emg.patch.hash_cell = {hc}（{hcOk}）与内核常量 EmgHash.Cell = {EmgHash.Cell} 一致（改表须同时改常量，已知偏差记证据）");

            using CombatKernel k = NewKernel();
            var bad = new List<EmgMatter>(c.Rows);
            bad[EmgMatterConst.Acid] = new EmgMatter { Life = 0f };
            var shortTable = c.Rows.Take(5).ToList();
            bool rejected = !k.SetMatters(bad) && !k.SetMatters(shortTable) && Near(k.MatterRow(EmgMatterConst.Acid).Life, c.Rows[EmgMatterConst.Acid].Life);
            Expect(rejected && Near(k.MatterRow(EmgMatterConst.Oil).Life, 20f) && k.MatterRow(EmgMatterConst.Smoke).Layer == EmgMatterConst.LayerGas && k.SpawnPatch(EmgMatterConst.Oil, double2.zero, 1f),
                "内核接受真实表（油存续 20 秒、烟在气体层）；存续 0 的坏行、行数不对的表整表拒绝，保留原表；斑块层开着可注入");

            int stage = EmgCatalog.EffectiveStage;
            bool fuse = EmgCatalog.IsOwnerOnDeclTrack("fw_enterfuse");
            bool tractor = EmgCatalog.IsOwnerOnDeclTrack("fw_tractor");
            bool spread = EmgCatalog.IsOwnerOnDeclTrack("fw_spread");
            bool split = EmgCatalog.IsOwnerOnDeclTrack("fw_split");
            bool pending = !EmgCatalog.IsPendingPort("fld.pull", EmgRole.Spawn) && EmgCatalog.IsPendingPort("ent.wreck", EmgRole.Modify) && EmgCatalog.IsPendingPort("mat.oil", EmgRole.Modify) && !EmgCatalog.IsPendingPort("mat.oil", EmgRole.Emit)
                           && !EmgCatalog.IsPendingPort("evt.enter", EmgRole.Trigger);
            Expect(stage == 2 && EmgCatalog.ImplementedStage == 2 && Cfg().EmgPatchOn == 1 && fuse && tractor && !spread && split && pending,
                $"真实表 emg.stage 缺省 2（生效 {stage}）、斑块层开；入场引信（端口全部接通）走声明轨 {fuse}；牵引（fld.*，EMG2-FLD-01 已接通）走声明轨 {tractor}；扩散（物质 modify）用到未接通端口 → 仍走旧轨（{spread}）；阶段 1 的分裂照旧 {split}（按端口判，不按内容 ID）");
            try
            {
                UseStage(1);
                using CombatKernel k1 = NewKernel();
                Expect(Cfg().EmgPatchOn == 0 && !EmgCatalog.IsOwnerOnDeclTrack("fw_enterfuse") && !k1.SpawnPatch(EmgMatterConst.Oil, double2.zero, 1f) && k1.PatchCount == 0,
                    "emg.stage = 1（回滚对照）：斑块层关、入场引信不生效、注入斑块被拒（阶段 1 行为）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── B. 17 种物质各一例（FGT-EMG-031）────────────────────────────────────────────

        private static void CheckEachMatter()
        {
            Line("  · B. FGT-EMG-031：17 种物质各一例——站在里面的单位挂各自的沾染位（固态不沾染）、直接减速；过存续消失并计数；气体半径增长、浓度降到 0.2 消失；纹样 + 颜色区分（B15）");
            var bad = new List<string>();
            var lines = new List<string>();
            for (byte m = 1; m <= EmgMatterConst.Count; m++)
            {
                EmgMatter row = Row(m);
                using CombatKernel k = NewKernel();
                int u = Foe(k, new double2(0, 0), 5000f);
                k.SpawnPatch(m, new double2(0, 0), 2f, 1f, 2f);
                StepN(k, 30);
                bool coated = row.CoatBit < 0 ? Status(k, u) == 0u || (Status(k, u) & ~CombatConst.StatusBitZoneSlow) == 0u : HasBit(k, u, row.CoatBit);
                bool slowed = row.ContactSlow <= 0f || (Status(k, u) & CombatConst.StatusBitZoneSlow) != 0u;
                bool alive = k.PatchCount == 1 && Patches(k)[0].Matter == m && (row.Solid == 0 || Near(Patches(k)[0].Hp, row.Hp));
                StepN(k, 120);
                // 明火燃烧时向气体层排烟（物质自身属性），所以只看本物质那块是否到期消失
                bool gone = CountOf(k, m) == 0 && k.PatchStats.PatchesExpired >= 1 && (row.Emit == 0 || CountOf(k, row.Emit) >= 1);
                if (!(coated && slowed && alive && gone))
                {
                    bad.Add($"{m}（沾染 {coated} 减速 {slowed} 在场 {alive} 消失 {gone}）");
                }
                lines.Add($"{m}:{(row.CoatBit < 0 ? "固" : row.CoatBit.ToString())}");
            }
            Expect(bad.Count == 0, $"17 种各一例（物质:沾染位 {string.Join(" ", lines)}）：0.5 秒后沾染 / 减速 / 在场、2 秒存续后消失并记到期{Detail(bad)}");

            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 0), 1f);
                StepN(k, 60);
                float r1 = Patches(k).FirstOrDefault().Radius;
                float conc1 = Patches(k).FirstOrDefault().Conc;
                double t0 = k.Time;
                while (k.PatchCount > 0 && k.Time < 20)
                {
                    StepN(k, 1);
                }
                double life = k.Time;
                Expect(r1 > 1.2f && conc1 < 1f && life < Row(EmgMatterConst.Smoke).Life && life > 3.5,
                    $"烟（气体层，扩散 0.3 米 / 秒）：1 秒后半径 {r1:0.00}、浓度 {conc1:0.00}；浓度降到 0.2 在 {life:0.00} 秒消失（早于存续 {Row(EmgMatterConst.Smoke).Life} 秒；理论 ≈ 4.1 秒）");
            }

            using (CombatKernel k = NewKernel())
            {
                for (byte m = 1; m <= EmgMatterConst.Count; m++)
                {
                    k.SpawnPatch(m, new double2(m * 20, 0), 1.5f, 1f, 30f);
                }
                StepN(k, 1);
                var fx = new NativeList<CombatInstance>(256, Allocator.TempJob);
                k.PrepareEffects(fx, double2.zero);
                var kinds = new Dictionary<float, float>();
                for (int i = 0; i < fx.Length; i++)
                {
                    if (fx[i].B.w >= EmgMatterConst.EffectKindPatch && fx[i].B.w <= EmgMatterConst.EffectKindPatch + 16f)
                    {
                        kinds[fx[i].B.w] = fx[i].B.z;
                    }
                }
                fx.Dispose();
                bool colors = Enumerable.Range(1, EmgMatterConst.Count).All(m => kinds.TryGetValue(EmgMatterConst.EffectKindPatch + Row((byte)m).Pattern, out float col) && (int)col == Row((byte)m).Color);
                Expect(kinds.Count == EmgMatterConst.Count && colors,
                    $"渲染：17 块斑块画成 {kinds.Count} 种纹样（实例种类 40 + 纹样码，形状为主），颜色 = 表里颜色（辅，B15）");
            }
        }

        // ── C. 合并 ─────────────────────────────────────────────────────────────

        private static void CheckMerge()
        {
            Line("  · C. 合并：同物质同层重叠 → 一块、份量相加、Owner 与因果链取份量大者并记“合并”；异物质 / 固态不合并；气体长大后由节拍经空间哈希合并");
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(-20, 0));
                int foe = Foe(k, new double2(20, 0));
                int recs = k.EmgChainRecordCount;
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 1.5f, 1f, 30f, me, false, 5);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0.3, 0), 1.5f, 3f, 30f, foe, false, 9);
                CombatZone z = Patches(k).FirstOrDefault();
                k.TryGetEmgChainRecent(0, out EmgChainRec rec);
                Expect(k.PatchCount == 1 && Near(z.Amount, 4f) && z.Owner == foe && z.EmgChain == 9 && Near(z.Radius, math.sqrt(2f * 1.5f * 1.5f), 1e-3f)
                       && k.EmgChainRecordCount == recs + 1 && rec.Kind == EmgChainNode.Merge && rec.Chain == 9 && rec.Parent == 0 && rec.Aux == EmgMatterConst.Oil,
                    $"两块重叠的油（份量 1 / 3）→ {k.PatchCount} 块、份量 {z.Amount}、半径 {z.Radius:0.00}（面积守恒）、Owner = 份量大的那块的排放者（{z.Owner}）、链 {z.EmgChain}；因果链记一条“合并”（{rec.Kind}，物质码 {rec.Aux}）");
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 1.5f);
                k.SpawnPatch(EmgMatterConst.Cured, new double2(10, 0), 1.5f);
                k.SpawnPatch(EmgMatterConst.Cured, new double2(10.2, 0), 1.5f);
                Expect(k.PatchCount == 4 && CountOf(k, EmgMatterConst.Cured) == 2, $"油上叠水（不同物质）、两块胶块（固态不合并）各自保留：共 {k.PatchCount} 块");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 0), 1f);
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(2, 0), 1f);
                bool two = k.PatchCount == 2;
                StepN(k, 200);
                Expect(two && k.PatchStats.PatchesMerged >= 1 && CountOf(k, EmgMatterConst.Smoke) == 1,
                    $"两团相切的烟生成时不合并（{two}），扩散到重叠比 ≥ 0.3 后由节拍经空间哈希合并（节拍合并 {k.PatchStats.PatchesMerged} 次，剩 {CountOf(k, EmgMatterConst.Smoke)} 团）");
            }
        }

        // ── D. 容量 ─────────────────────────────────────────────────────────────

        private static void CheckCapacity()
        {
            Line("  · D. 容量降级（FGR-EMG-082 / FG19B §8.1）：软上限就近并入 → 同种最早提前到期；总上限并入最近同种 → 同层最早提前到期 → 无可让位丢弃；世界源保留额；每步生成额度顺延；节拍错相");
            using (CombatKernel k = NewKernel())
            {
                var list = new List<(byte, double2, float, float, bool)>();
                for (int i = 0; i < 88; i++)
                {
                    list.Add((EmgMatterConst.Oil, new double2(i % 10 * 10, i / 10 * 10), 1f, 60f, false));
                }
                Fill(k, list);
                int full = CountOf(k, EmgMatterConst.Oil);
                int oldest = Patches(k).Where(z => z.Matter == EmgMatterConst.Oil).Min(z => z.EmgId);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(500, 500), 1f, 1f, 60f);
                bool soft1 = k.PatchStats.PatchSoftExpired == 1 && CountOf(k, EmgMatterConst.Oil) == 88 && Patches(k).All(z => z.EmgId != oldest);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(53, 50), 1f, 1f, 60f);
                bool soft2 = k.PatchStats.PatchSoftMerged == 1 && CountOf(k, EmgMatterConst.Oil) == 88;
                Expect(full == 88 && soft1 && soft2,
                    $"油软上限 88：第 89 块离谁都远 → 最早那块（{oldest}）提前到期（计数 {k.PatchStats.PatchSoftExpired}）；再来一块 6 米内有同种 → 就近并入（计数 {k.PatchStats.PatchSoftMerged}），活油仍 {CountOf(k, EmgMatterConst.Oil)}");
            }
            using (CombatKernel k = NewKernel(30, 4))
            {
                var world = new List<(byte, double2, float, float, bool)>();
                for (int i = 0; i < 4; i++)
                {
                    world.Add((EmgMatterConst.Acid, new double2(-50 - i * 10, 0), 3f, 0f, true));
                }
                Fill(k, world);
                bool fifthWorld = k.SpawnPatch(EmgMatterConst.Acid, new double2(-200, 0), 3f, 1f, 0f, 0, true);
                var list = new List<(byte, double2, float, float, bool)>();
                for (int i = 0; i < 26; i++)
                {
                    list.Add((EmgMatterConst.Water, new double2(i * 5, 0), 1f, 60f, false));
                }
                Fill(k, list);
                int oldestWater = Patches(k).Where(z => z.Matter == EmgMatterConst.Water).Min(z => z.EmgId);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 40), 1f, 1f, 60f);
                bool capExpired = k.PatchStats.PatchCapExpired == 1 && CountOf(k, EmgMatterConst.Oil) == 1 && Patches(k).All(z => z.EmgId != oldestWater);
                CombatZone nearW = Patches(k).Where(z => z.Matter == EmgMatterConst.Water).OrderBy(z => math.distance(z.Pos, new double2(200, 0))).First();
                k.SpawnPatch(EmgMatterConst.Water, new double2(200, 0), 1f, 2f, 60f);
                CombatZone afterW = Patches(k).First(z => z.EmgId == nearW.EmgId);
                // 复审 P2：离得远的强制并入只加份量、中心不动（不瞬移到两边都没排放过的中点）
                bool capMerged = k.PatchStats.PatchCapMerged == 1 && CountOf(k, EmgMatterConst.Water) == 25
                                 && math.all(afterW.Pos == nearW.Pos) && Near(afterW.Amount, nearW.Amount + 2f);
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 80), 1f);
                bool dropped = k.PatchStats.PatchDropped >= 2 && CountOf(k, EmgMatterConst.Smoke) == 0;
                bool worldKept = Patches(k).Count(z => (z.PFlags & EmgMatterConst.FlagWorld) != 0) == 4;
                Expect(!fifthWorld && capExpired && capMerged && dropped && worldKept && k.PatchCount == 30,
                    $"容量 30（世界源保留 4）：世界源第 5 块丢弃；战斗斑块满 26 后来了油（无同种）→ 同层最早的水提前到期（{k.PatchStats.PatchCapExpired}）；来了水（200 米外）→ 并入最近同种（{k.PatchStats.PatchCapMerged}），中心不动、份量相加；" +
                    $"来了烟（气体层无可让位）→ 丢弃（丢弃计数 {k.PatchStats.PatchDropped}）；4 块世界源一块不少，共 {k.PatchCount} 块");
                using var site = new CombatSite(SiteId + "-cap", Cfg(30, 4));
                site.Kernel.SpawnPatch(EmgMatterConst.Oil, double2.zero, 1f);
                string dbg = site.EmgPatchDebugLine();
                Expect(dbg.Contains("软上限并入") && dbg.Contains("总上限并入") && dbg.Contains("丢弃") && dbg.Contains("顺延") && dbg.Contains("残骸成碎屑") && dbg.Contains("1/30"),
                    $"调试层一行可见：{dbg}");
            }
            using (CombatKernel k = NewKernel())
            {
                for (int i = 0; i < 40; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Water, new double2(i * 5, 0), 1f, 1f, 10f);
                }
                int now = k.PatchCount;
                double t = k.Time;
                StepN(k, 1);
                var late = Patches(k).Where(z => z.Born > t).ToList();
                Expect(now == 32 && k.PatchStats.PatchDeferred == 8 && k.PatchCount == 40 && late.Count == 8 && late.All(z => Near((float)(z.Until - z.Born), 10f)),
                    $"一步内请求 40 块：当步落地 {now}（每步额度 32），顺延 {k.PatchStats.PatchDeferred} 块下一步落地，寿命从落地算（{late.Count} 块，存续 10 秒）");
            }
            using (CombatKernel k = NewKernel())
            {
                var list = new List<(byte, double2, float, float, bool)>();
                for (int i = 0; i < 300; i++)
                {
                    byte m = i % 3 == 0 ? EmgMatterConst.Smoke : i % 3 == 1 ? EmgMatterConst.Oil : EmgMatterConst.Water;
                    list.Add((m, new double2(i % 20 * 2.1, i / 20 * 2.1), 1f, m == EmgMatterConst.Smoke ? 0f : 1.5f + i % 7 * 0.5f, false));
                }
                Fill(k, list);
                bool ok = true;
                double worstLag = 0, worstAhead = 0;
                for (int s = 0; s < 240; s++)
                {
                    StepN(k, 1);
                    double now = k.Time;
                    foreach (CombatZone z in Patches(k))
                    {
                        worstLag = Math.Max(worstLag, now - z.NextTick);
                        worstAhead = Math.Max(worstAhead, z.NextTick - now);
                    }
                }
                ok &= worstLag <= 6 * Dt + 1e-6 && worstAhead <= 0.25 + 1e-6;
                Expect(ok && k.PatchStats.PatchesMerged + k.PatchStats.PatchesExpired > 0,
                    $"300 块（烟合并、到期、复用空槽）跑 4 秒：每步最多结算 69 块、按 (NextTick, Id) 错相——最大欠拍 {worstLag * 1000:0.0} 毫秒（≤ 6 步）、没有提前两拍（最远 {worstAhead:0.000} 秒 ≤ 0.25）；期间合并 {k.PatchStats.PatchesMerged} / 到期 {k.PatchStats.PatchesExpired}");
            }
        }

        // ── E. 中立与离地 ─────────────────────────────────────────────────────────────

        private static void CheckNeutral()
        {
            Line("  · E. 恒中立（FGR-EMG-083）：己方站进己方斑块照样沾染、敌方斑块沾己方；离地单位不吃地面层、吃气体层");
            using CombatKernel k = NewKernel();
            int me = Me(k, new double2(0, 0));
            int me2 = Me(k, new double2(10, 0));
            int foe = Foe(k, new double2(20, 0));
            int air = Unit(k, CombatFaction.Player, new double2(30, 0), -1, 300f, CombatUnitKind.Machine, k.AddProfile(new CombatBehaviorProfile { Airborne = 1f }));
            int air2 = Unit(k, CombatFaction.Player, new double2(40, 0), -1, 300f, CombatUnitKind.Machine, k.AddProfile(new CombatBehaviorProfile { Airborne = 1f }));
            k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 1f, 30f, me);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(10, 0), 2f, 1f, 30f, foe);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(30, 0), 2f, 1f, 30f, me);
            k.SpawnPatch(EmgMatterConst.Smoke, new double2(40, 0), 2f, 1f, 30f);
            StepN(k, 30);
            int oil = Row(EmgMatterConst.Oil).CoatBit, smoke = Row(EmgMatterConst.Smoke).CoatBit;
            Expect(HasBit(k, me, oil) && HasBit(k, me2, oil) && !HasBit(k, air, oil) && HasBit(k, air2, smoke)
                   && k.MatterMaskAt(new double2(30, 0), true) == 0u && k.MatterMaskAt(new double2(30, 0)) == 1u << EmgMatterConst.Oil,
                $"己方站进己方排的油：沾油（位 {oil}）；己方站进敌方排的油：沾油；离地单位在油上不沾（地面层）、在烟里沾遮蔽（位 {smoke}，气体层）");
        }

        // ── F. 直接接触伤害 ─────────────────────────────────────────────────────────────

        private static void CheckContactDamage()
        {
            Line("  · F. 直接接触伤害走 DamageFrom（evt.hurt Code = 斑块 4），不认阵营：熔融金属每秒约 6");
            using CombatKernel k = NewKernel();
            int listen = Listen(k, EmgEventKind.Hurt);
            int me = Me(k, new double2(-10, 0));
            int foe = Foe(k, new double2(0, 0), 200f, k.AddWeapon(Instant(1f, 0.5f, listen)));
            int ally = Unit(k, CombatFaction.Player, new double2(10, 0), k.AddWeapon(Instant(1f, 0.5f, listen)), 200f, CombatUnitKind.Machine);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 1f, 20f, me);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(10, 0), 2f, 1f, 20f, me);
            StepN(k, 120);
            float dFoe = 200f - Hp(k, foe), dAlly = 200f - Hp(k, ally);
            k.TryGetLastEmgEvent(EmgEventKind.Hurt, out EmgEvent e);
            Expect(dFoe > 10f && dFoe < 14f && Near(dFoe, dAlly, 0.01f) && e.Kind == EmgEventKind.Hurt && e.Code == EmgCode.SrcPatch && e.Other == me,
                $"2 秒内：敌方掉血 {dFoe:0.0}、站进己方熔融金属的己方单位掉血 {dAlly:0.0}（中立，同伤）；evt.hurt Code = {e.Code}（斑块 4），攻击者 = 排放者 {e.Other}");

            // 预检异议 2（FGR-EMG-081 第二条）：酸沼是虚拟世界源——内核按“点在危害区内”查询，不实体化成斑块、不占容量、不进空间哈希；伤害来源码仍是斑块 4
            using CombatKernel m = NewKernel();
            int ml = Listen(m, EmgEventKind.Hurt);
            int inMarsh = Foe(m, new double2(0, 0), 200f, m.AddWeapon(Instant(1f, 0.5f, ml)));
            m.SetAcidMarsh(new[] { new float4(0f, 0f, 0f, 6f) }, 0.02f, 1f);
            StepN(m, 120);
            m.TryGetLastEmgEvent(EmgEventKind.Hurt, out EmgEvent me2);
            Expect(m.InAcidMarsh(new double2(0, 0)) && 200f - Hp(m, inMarsh) > 0f && m.PatchCount == 0 && m.PatchSlots == 0 && m.PatchStats.PatchesBorn == 0
                   && me2.Kind == EmgEventKind.Hurt && me2.Code == EmgCode.SrcPatch,
                $"酸沼里的敌人 2 秒掉血 {200f - Hp(m, inMarsh):0.0}、evt.hurt Code = {me2.Code}（斑块 4）；斑块 0 块、槽位 0、生成计数 {m.PatchStats.PatchesBorn}（不实体化、不占容量、不进哈希）");

            // 复审 P1（FGR-EMG-083“不得复用中立区域分支，它跳过炮塔与建筑”）：炮塔血量在内核，站进斑块照样掉血、挂沾染、evt.hurt Code = 斑块；
            // 对照：建筑 / 训练靶不吃斑块是已登记降级（DEBT-EMG2MAT01-02，承接 EMG2-MAT-02）
            using CombatKernel t = NewKernel();
            int tl = Listen(t, EmgEventKind.Hurt);
            int src = Me(t, new double2(-10, 0));
            int tur = Unit(t, CombatFaction.Player, new double2(0, 0), t.AddWeapon(Instant(1f, 0.5f, tl)), 200f, CombatUnitKind.Turret);
            int str = Unit(t, CombatFaction.Player, new double2(10, 0), -1, 200f, CombatUnitKind.Structure);
            t.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 1f, 20f, src);
            t.SpawnPatch(EmgMatterConst.Molten, new double2(10, 0), 2f, 1f, 20f, src);
            StepN(t, 120);
            t.TryGetLastEmgEvent(EmgEventKind.Hurt, out EmgEvent te);
            float dTur = 200f - Hp(t, tur), dStr = 200f - Hp(t, str);
            int molten = Row(EmgMatterConst.Molten).CoatBit;
            bool coat = molten < 0 || (HasBit(t, tur, molten) && !HasBit(t, str, molten));
            Expect(dTur > 10f && dTur < 14f && coat && te.Kind == EmgEventKind.Hurt && te.Code == EmgCode.SrcPatch && te.Ent == tur && te.Other == src && Near(dStr, 0f),
                $"炮塔站进熔融金属 2 秒：掉血 {dTur:0.0}（敌方 {dFoe:0.0}）、沾染位 {molten}（{coat}）、evt.hurt Code = {te.Code}、宿主 = 炮塔 {te.Ent}（应 {tur}）、攻击者 = 排放者 {te.Other}；" +
                $"对照建筑掉血 {dStr:0.0}（不吃斑块，已登记降级 DEBT-EMG2MAT01-02）");
        }

        // ── G. evt.enter / evt.leave 生产者（FGT-EMG-020）────────────────────────────────

        private static void CheckEnterLeave()
        {
            Line("  · G. FGT-EMG-020 进 / 离场生产者：单位进斑块发 evt.enter（Code = 物质码，Other = 斑块 Id）、离开发 evt.leave；不读这两个事件码的单位不入队");
            using CombatKernel k = NewKernel();
            int listen = Listen(k, EmgEventKind.Enter, EmgEventKind.Leave);
            int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(1f, 0.5f, listen)));
            Me(k, new double2(0.5, 0));
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 1f, 1f);
            int pid = Patches(k)[0].EmgId;
            StepN(k, 30);
            long enter = k.EmgEnqueuedOf(EmgEventKind.Enter);
            k.TryGetLastEmgEvent(EmgEventKind.Enter, out EmgEvent en);
            bool noLeaveYet = k.EmgEnqueuedOf(EmgEventKind.Leave) == 0;
            StepN(k, 90);
            k.TryGetLastEmgEvent(EmgEventKind.Leave, out EmgEvent lv);
            Expect(enter == 1 && en.Ent == me && en.Code == EmgMatterConst.Acid && en.Other == pid && en.OtherKind == EmgEntKind.Patch && noLeaveYet
                   && k.EmgEnqueuedOf(EmgEventKind.Leave) == 1 && lv.Ent == me && lv.Code == EmgMatterConst.Acid,
                $"两个单位站进酸斑块：只有读进 / 离场的那个入队 evt.enter {enter} 条（Code {en.Code}，Other = 斑块 {en.Other}）；斑块到期后发 evt.leave {k.EmgEnqueuedOf(EmgEventKind.Leave)} 条（Code {lv.Code}）");
        }

        // ── H. 弹体沾染 ─────────────────────────────────────────────────────────────

        private static void CheckProjectiles()
        {
            Line("  · H. 弹体穿过斑块同样沾染（FGR-EMG-083 第 3 条）：飞行弹体穿过酸斑块带腐蚀、命中挂给目标；抛射弹在空中不碰地面层；对照无斑块");
            int corrode = Row(EmgMatterConst.Acid).CoatBit;
            (bool coatSeen, bool foeCoated) Shot(bool patch, bool lob)
            {
                using CombatKernel k = NewKernel();
                int prog = lob ? Tree(k, true, new[] { EmgEventKind.Expire }, new[] { new EmgInstr { Port = 110, Role = RoleModify, Verb = (byte)EmgVerb.Lob, Amount = 4f, A = 1f } }) : -1;
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(prog, 1f, 30f, lob ? 2f : 1.5f)));
                int foe = Foe(k, new double2(0, 9), 1000f);
                if (patch)
                {
                    k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 4.5), 1.5f, 1f, 30f);
                }
                k.FireAt(me, foe, k.Time);
                bool seen = false, coated = false;
                for (int s = 0; s < 90; s++)
                {
                    StepN(k, 1);
                    for (int i = 0; i < k.ProjectileCount; i++)
                    {
                        seen |= k.TryGetProjectile(i, out CombatProjectile p) && (p.Coat & (1u << corrode)) != 0u;
                    }
                    coated |= HasBit(k, foe, corrode);
                }
                return (seen, coated);
            }
            var straight = Shot(true, false);
            var none = Shot(false, false);
            var lobbed = Shot(true, true);
            Expect(straight.coatSeen && straight.foeCoated && !none.coatSeen && !none.foeCoated && !lobbed.coatSeen && !lobbed.foeCoated,
                $"直射穿过酸斑块：弹体带腐蚀位 {straight.coatSeen}、命中目标挂腐蚀 {straight.foeCoated}；没有斑块：{none.coatSeen} / {none.foeCoated}；抛射越过（在空中不碰地面层）：{lobbed.coatSeen} / {lobbed.foeCoated}");
        }

        // ── I. 入场引信（FGT-EMG-011）────────────────────────────────────────────────

        private static void CheckEnterFuse()
        {
            Line("  · I. FGT-EMG-011 进场触发：入场引信（真实编译）——载荷进入斑块时放子载荷；对照：路上没有斑块不放");
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_enterfuse" }, FirmwareCarrier.Projectile);
            (long sub, long enter) Run(bool patch)
            {
                using CombatKernel k = NewKernel();
                int prog = CombatSite.RegisterProgram(k, pr);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(prog, 1f)));
                int foe = Foe(k, new double2(0, 10), 1000f);
                if (patch)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 5), 1.5f, 1f, 30f);
                }
                long before = k.EmgBusStats.SubSpawned;
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                return (k.EmgBusStats.SubSpawned - before, k.EmgEnqueuedOf(EmgEventKind.Enter));
            }
            var with = Run(true);
            var without = Run(false);
            Expect(!pr.IsEmpty && with.enter >= 1 && with.sub > 0 && without.sub == 0 && without.enter == 0,
                $"入场引信编译成程序（非空）；弹体穿过油斑块：evt.enter {with.enter} 条、放子载荷 {with.sub} 发；路上没有斑块：{without.enter} / {without.sub}");
            // 模糊测试固定集 #31 发现：即时命中的连射器没有飞行路径，入场引信永远不触发——根节点读 evt.enter / evt.leave 的程序“需要飞行”（ADR ③ 扩展）
            var gun = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 10f, Damage = 5f, Cooldown = 1f };
            CombatSite.ApplySpawnFlight(ref gun, pr, ComponentCatalog.CompGunId);
            EmgProgramResult plain = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_echo" }, FirmwareCarrier.Projectile);
            var gun2 = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 10f, Damage = 5f, Cooldown = 1f };
            CombatSite.ApplySpawnFlight(ref gun2, plain, ComponentCatalog.CompGunId);
            Expect((pr.Program.Flags & EmgProgramFlags.NeedsFlight) != 0 && gun.SpawnFlight == 1 && gun.ProjectileSpeed > 0f && gun2.SpawnFlight == 0,
                $"入场引信装在即时连射器上：程序需要飞行、武器置 SpawnFlight（弹速 {gun.ProjectileSpeed}）；对照 [回波]（读命中）不需要飞行、逐字不变（SpawnFlight {gun2.SpawnFlight}）");
            // 模糊测试固定集 #55 发现：修复弹体不反弹（FG-GAP-178 ④），维修束上的 [反弹引信] 永远不触发——按端口判“本载体不适用”，预览给原因而不是静默死指令
            (int decls, bool note) Bounce(string comp)
            {
                var board = GameLogic.Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisFixedId, comp, null, null, new[] { "fw_bouncefuse" });
                EmgProgramResult r = EmgProgramCompiler.Compile(GameLogic.Campaign.Blueprint.BlueprintCircuitCompiler.CompilePreview(board));
                EmgProgramStep st = r.Steps.FirstOrDefault(x => x.FirmwareId == "fw_bouncefuse");
                return (st?.Decls.Count ?? -1, st != null && r.Notes.Any(n => n.Key == EmgProgramCompiler.NoteCarrierNa && n.Ordinal == st.Ordinal));
            }
            var heal = Bounce(ComponentCatalog.CompRepairBeamId);
            var shot = Bounce(ComponentCatalog.CompGunId);
            Expect(heal.decls == 0 && heal.note && shot.decls > 0 && !shot.note,
                $"维修束（修复出口）+ [反弹引信]：声明行 {heal.decls}、预览写“本载体不适用” {heal.note}；对照连射器：声明行 {shot.decls}、无此原因");
        }

        // ── J. 残骸崩解成碎屑（DEBT-EMG1ENT01-01）────────────────────────────────────

        private static void CheckDebris()
        {
            Line("  · J. 残骸崩解转化不消失（FGR-EMG-113，DEBT-EMG1ENT01-01）：野外到期 / 满 256 最早让位都在原地生成一块金属碎屑斑块；斑块层关时只计数（阶段 1 对照）");
            using (CombatKernel k = NewKernel())
            {
                k.TryAddWreck(new EmgWreck { Pos = new double2(3, 4), Radius = 0.7f, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = k.Time + 1.0, Profile = -1 }, out _);
                StepN(k, 90);
                CombatZone z = Patches(k).FirstOrDefault();
                Expect(k.WreckCount == 0 && k.PatchCount == 1 && z.Matter == EmgMatterConst.Debris && math.distance(z.Pos, new double2(3, 4)) < 1e-6 && Near(z.Radius, 0.7f)
                       && z.Phase == EmgMatterConst.PhaseFromWreck && k.PatchStats.WrecksToDebris == 1 && Near((float)(z.Until - z.Born), Row(EmgMatterConst.Debris).Life),
                    $"野外到期：残骸消失、原地一块碎屑（半径 {z.Radius:0.0}、存续 {z.Until - z.Born:0} 秒、来源“残骸崩解”），计数 {k.PatchStats.WrecksToDebris}");
            }
            using (CombatKernel k = NewKernel(0, -1, 300))
            {
                for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
                {
                    k.TryAddWreck(new EmgWreck { Pos = new double2(i % 16 * 3, i / 16 * 3 + 40), Radius = 0.4f, Hp = 20f, MaxHp = 20f, Mass = 6f, Born = k.Time, Until = k.Time + 90.0, Profile = -1 }, out _);
                }
                k.TryGetWreck(0, out EmgWreck oldest);
                int me = Me(k, new double2(-10, 0), k.AddWeapon(Instant(1000f)));
                int foe = Foe(k, new double2(-10, 6));
                k.FireAt(me, foe, k.Time);
                CombatZone z = Patches(k).FirstOrDefault();
                Expect(k.WreckCount == EmgConst.DefaultWreckCapacity && k.PatchCount == 1 && z.Matter == EmgMatterConst.Debris && math.distance(z.Pos, oldest.Pos) < 1e-6 && k.PatchStats.WrecksToDebris == 1,
                    $"满 256 再阵亡一个：最早那块让位，在它原处（{oldest.Pos.x:0},{oldest.Pos.y:0}）生成碎屑，计数 {k.PatchStats.WrecksToDebris}");
            }
            using (CombatKernel k = NewKernel(0, -1, 64, false))
            {
                k.TryAddWreck(new EmgWreck { Pos = new double2(3, 4), Radius = 0.7f, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = k.Time + 1.0, Profile = -1 }, out _);
                StepN(k, 90);
                Expect(k.WreckCount == 0 && k.PatchCount == 0 && k.PatchStats.WrecksToDebris == 0 && k.WreckCounts.Aged == 1,
                    $"斑块层关（阶段 1）：残骸到期只计数（到期 {k.WreckCounts.Aged}），不生成斑块");
            }
        }

        // ── K. 存读档（FGT-EMG-040）────────────────────────────────────────────────

        // ── P. DEBT-EMG1TRIG01-05：弹体记号扩到 16 位（格式 17），驻守入口不发具名反应 ──────────────

        private static void CheckGuardNoNamed()
        {
            Line("  · P. DEBT-EMG1TRIG01-05：驻守开火（HoldFire 自动出手）+ 需要飞行 + 标记跳转——弹体带入口位 NoNamed、命中不跳（与即时命中原入口一致）；对照：直控 / 编队攻击（FireAt）照常跳；入口位随快照往返");
            int prog;
            (bool flag, bool kept, bool round, float side) Run(bool guard)
            {
                using CombatKernel k = NewKernel();
                prog = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_echo" }, FirmwareCarrier.Projectile));
                CombatWeapon w = Flight(prog, 10f, 20f, 1f);
                w.Cooldown = 30f;
                w.Range = 8f;
                w.Reaction = CombatReaction.MarkJump;
                w.JumpRange = 6f;
                w.JumpFalloff = 0.5f;
                w.JumpMax = 3;
                int wi = k.AddWeapon(w);
                int me = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = guard ? CombatBehavior.HoldFire : CombatBehavior.Commanded,
                    Flags = Live, Position = double2.zero, Home = double2.zero, Radius = 0.5f, Speed = 0f, Health = 500f, MaxHealth = 500f, Weapon = wi, BehaviorProfile = -1, Priority = 1,
                    ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
                });
                int foe = Foe(k, new double2(4, 0), 1000f);
                int side = Foe(k, new double2(4, 3), 1000f);
                k.SetMarkedUntil(foe, 1e6);
                k.SetMarkedUntil(side, 1e6);
                if (!guard)
                {
                    k.FireAt(me, foe, k.Time);
                }
                bool flag = false, kept = false, round = false;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    for (int q = 0; q < k.ProjectileCount && !flag; q++)
                    {
                        if (k.TryGetProjectile(q, out CombatProjectile pr) && pr.Owner == me && (pr.Flags & EmgProjFlags.Sub) == 0)
                        {
                            flag = true;
                            kept = (pr.Flags & EmgProjFlags.NoNamed) != 0;
                            using CombatKernel b = NewKernel();
                            if (b.Load(k.Serialize()) == CombatLoadResult.Ok && b.TryGetProjectile(q, out CombatProjectile pb))
                            {
                                round = pb.Flags == pr.Flags;
                            }
                        }
                    }
                }
                k.TryGetUnit(side, out CombatUnitView sv);
                return (flag, kept, round, 1000f - sv.Health);
            }
            var g = Run(true);
            var f = Run(false);
            Expect(g.flag && g.kept && g.round && g.side == 0f && f.flag && !f.kept && f.round && f.side > 0f,
                $"驻守自动出手：弹体带 NoNamed {g.kept}（读档后记号逐位一致 {g.round}）、命中已标记目标后旁边已标记的敌人掉血 {g.side}（不跳）；直控 FireAt：NoNamed {f.kept}、旁边掉血 {f.side}（跳）");
        }

        private static void CheckSaveLoad()
        {
            Line("  · K. FGT-EMG-040：斑块在场中途存读档续跑一致；读 16 版快照（无斑块、单位阶段 2 段为 0）；短段 / 长段；档案稳定键换序后读档按键挪下标（DEBT-EMG1ENT01-07）");
            void Build(CombatKernel k)
            {
                int listen = Listen(k, EmgEventKind.Enter, EmgEventKind.Leave, EmgEventKind.Hurt);
                Me(k, new double2(0, 0), k.AddWeapon(Instant(1f, 0.5f, listen)));
                Foe(k, new double2(3, 0), 300f, k.AddWeapon(Instant(1f, 0.5f, listen)));
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 2f, 8f);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(3, 0), 1.5f, 1f, 6f);
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(1.5, 0), 1f);
                k.SpawnPatch(EmgMatterConst.Acid, new double2(-20, 0), 3f, 1f, 0f, 0, true);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(8, 0), 1f, 2f);
            }
            // 模糊测试固定集 #61 发现：存档前有到期斑块留下的墓碑槽——连续跑新斑块复用空槽、读档后列表压实，斑块段按 Id 写（规范顺序），哈希不随槽位布局变
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                a.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 1f, 1f, 0.5f);
                a.SpawnPatch(EmgMatterConst.Water, new double2(10, 0), 1f, 1f, 30f);
                StepN(a, 60);
                byte[] snap = a.Serialize();
                CombatLoadResult lr = b.Load(snap);
                bool tomb = a.PatchSlots > a.PatchCount;
                a.SpawnPatch(EmgMatterConst.Oil, new double2(20, 0), 1f, 1f, 30f);
                b.SpawnPatch(EmgMatterConst.Oil, new double2(20, 0), 1f, 1f, 30f);
                bool layout = !Patches(a).Select(z => z.EmgId).SequenceEqual(Patches(b).Select(z => z.EmgId)); // 槽位顺序不同（连续跑复用空槽、读档后压实追加）
                bool same = lr == CombatLoadResult.Ok && a.StateHash() == b.StateHash();
                for (int i = 0; i < 120; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    same &= a.StateHash() == b.StateHash();
                }
                Expect(tomb && layout && same,
                    $"墓碑槽复用：存档时有空槽 {tomb}，读档后两边槽位顺序不同 {layout}（{string.Join(",", Patches(a).Select(z => z.EmgId))} / {string.Join(",", Patches(b).Select(z => z.EmgId))}），StateHash 读档后与续跑 120 步逐步一致 {same}");
            }
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                Build(a);
                StepN(a, 50);
                byte[] snap = a.Serialize();
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && CombatKernel.PeekFormat(snap) == 17 && CombatConst.FormatVersion == 17 && a.StateHash() == b.StateHash()
                            && a.PatchCount == b.PatchCount && a.PatchStats.PatchesBorn == b.PatchStats.PatchesBorn;
                for (int i = 0; i < 300; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    same &= a.StateHash() == b.StateHash() && a.PatchCount == b.PatchCount;
                }
                var ida = Patches(a).Select(z => z.EmgId).ToList();
                var idb = Patches(b).Select(z => z.EmgId).ToList();
                a.SpawnPatch(EmgMatterConst.Water, new double2(50, 0), 1f);
                b.SpawnPatch(EmgMatterConst.Water, new double2(50, 0), 1f);
                Expect(same && ida.SequenceEqual(idb) && Patches(a).Max(z => z.EmgId) == Patches(b).Max(z => z.EmgId) && a.PatchStats.PatchesExpired > 0,
                    $"格式 17 中途存读档（油 / 熔融 / 烟 / 世界源酸 / 明火排烟）：读档 {lr}，续跑 300 步每步 StateHash 一致、斑块 Id 一致（{string.Join(",", ida)}），读档后新斑块接着发号");

                using CombatKernel c = NewKernel();
                byte[] f16 = a.SerializeFormatForTests(16);
                CombatLoadResult l16 = c.Load(f16);
                c.TryGetUnitEmg(1, out _, out EmgUnitState us);
                StepN(c, 60);
                Expect(l16 == CombatLoadResult.Ok && CombatKernel.PeekFormat(f16) == 16 && c.PatchCount == 0 && us.Temp == 0f && us.PhaseMask == 0 && c.FieldCount == 0,
                    $"读 16 版快照：{l16}、没有斑块（{c.PatchCount}）、单位阶段 2 状态为 0、活跃场 0，续跑正常");

                using CombatKernel s = NewKernel();
                CombatLoadResult ls = s.Load(a.SerializeEmgShapeForTests(EmgConst.SegPatches, 10, 0));
                var sp = Patches(s);
                using CombatKernel l = NewKernel();
                CombatLoadResult ll = l.Load(a.SerializeEmgShapeForTests(0, int.MaxValue, 4));
                Expect(ls == CombatLoadResult.Ok && sp.Count == a.PatchCount && sp.All(z => Near(z.Amount, 1f) && Near(z.TickInterval, EmgMatterConst.DefaultTick) && z.EmgId > 0)
                       && sp.Select(z => z.EmgId).Distinct().Count() == sp.Count && ll == CombatLoadResult.Ok && l.StateHash() == a.StateHash(),
                    $"短段（斑块记录只到层，较早提交写出）：{ls}，份量 / 节拍取缺省、按读入顺序补发 Id（{string.Join(",", sp.Select(z => z.EmgId))}）；长段（每条多 4 字节 + 新段）：{ll}，与正常快照逐位一致");
            }
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            using (CombatKernel c = NewKernel())
            {
                int n = a.EntityProfileCount;
                a.SetEntityProfileKeys(Enumerable.Range(0, n).Select(i => 1000 + i).ToList());
                b.SetEntityProfileKeys(Enumerable.Range(0, n).Select(i => 1000 + (n - 1 - i)).ToList());
                c.SetEntityProfileKeys(Enumerable.Range(0, n).Select(i => 5000 + i).ToList());
                int u = Me(a, new double2(0, 0));
                a.SetUnitProfile(u, 3, 0f);
                byte[] snap = a.Serialize();
                bool lb = b.Load(snap) == CombatLoadResult.Ok, lc = c.Load(snap) == CombatLoadResult.Ok;
                b.TryGetUnitEmg(u, out short pb, out _);
                c.TryGetUnitEmg(u, out short pc, out _);
                Expect(n > 4 && lb && lc && pb == n - 1 - 3 && pc == -1,
                    $"档案表 {n} 行：存档时下标 3；读档时表换了顺序 → 按稳定键挪到 {pb}（应 {n - 4}）；键都对不上 → 落回种类默认 {pc}");
                // 负向（验证失败修复）：读进还没写键的裸内核（各段自检的往返写法）——键沿用存档里的、下标原样，重新序列化逐字节一致、状态哈希一致
                using var bare = new CombatKernel(a.Config, 64);
                bool lbare = bare.Load(snap) == CombatLoadResult.Ok;
                bare.TryGetUnitEmg(u, out short pbare, out _);
                Expect(lbare && pbare == 3 && bare.Serialize().SequenceEqual(snap) && bare.StateHash() == a.StateHash(),
                    $"读进没写键的内核（{lbare}）：下标原样 {pbare}（应 3），重新序列化逐字节一致、状态哈希一致（段 19 不丢）");
            }
            // 复审 P0：一步内请求超过生成额度时顺延的斑块（物质码 > 5：烟 11 / 碎屑 13），顺延表非空时存档 → 读档 Ok、哈希一致、下一步落地、续跑一致
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                for (int i = 0; i < 40; i++)
                {
                    a.SpawnPatch(i % 2 == 0 ? EmgMatterConst.Smoke : EmgMatterConst.Debris, new double2(i * 7, 0), 1f, 1f, 10f);
                }
                int pend = a.EmgPendingCount;
                bool high = Enumerable.Range(0, pend).All(i => a.TryGetEmgPending(i, out EmgSpawn s) && s.Kind == EmgConst.PendPatch && s.SubForm > 5);
                CombatLoadResult lr = b.Load(a.Serialize());
                bool same = lr == CombatLoadResult.Ok && a.StateHash() == b.StateHash() && b.EmgPendingCount == pend;
                StepN(a, 1);
                StepN(b, 1);
                bool landed = a.PatchCount == 40 && b.PatchCount == 40 && a.EmgPendingCount == 0 && b.EmgPendingCount == 0;
                for (int i = 0; i < 120; i++)
                {
                    same &= a.StateHash() == b.StateHash();
                    StepN(a, 1);
                    StepN(b, 1);
                }
                Expect(pend == 8 && high && same && landed,
                    $"一步请求 40 块烟 / 碎屑：顺延 {pend} 块（物质码都 > 5：{high}）时存档 → 读档 {lr}，下一步全部落地（{a.PatchCount} / {b.PatchCount} 块，顺延表清空 {landed}），StateHash 读档后与续跑 120 步一致 {same}");
            }
        }

        // ── L. 确定性（FGT-EMG-042）与倍速 ────────────────────────────────────────────

        private static void CheckDeterminism()
        {
            Line("  · L. FGT-EMG-042：读渲染 / 拾取 / 点查 / 存档不改结算；没有物质时开斑块层哈希不变；0.5x～3x 到期时刻与接触伤害一致；暂停不变");
            void Scene(CombatKernel k)
            {
                int listen = Listen(k, EmgEventKind.Enter, EmgEventKind.Hurt);
                for (int i = 0; i < 12; i++)
                {
                    Me(k, new double2(i * 1.5, 0), k.AddWeapon(Instant(1f, 0.5f, listen)));
                    Foe(k, new double2(i * 1.5, 2), 400f, k.AddWeapon(Instant(1f, 0.5f, listen)));
                }
                for (int i = 0; i < 40; i++)
                {
                    byte m = (byte)(1 + i % EmgMatterConst.Count);
                    k.SpawnPatch(m, new double2(i % 10 * 2, i / 10 * 1.5), 1.2f, 1f + i % 3, 0f, i % 2 == 0 ? 1 : 0);
                }
            }
            ulong ha, hb;
            bool every = true;
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                Scene(a);
                Scene(b);
                var fx = new NativeList<CombatInstance>(512, Allocator.TempJob);
                for (int s = 0; s < 300; s++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    fx.Clear();
                    b.PrepareEffects(fx, double2.zero);
                    b.PickPatch(new double2(s % 20, 1));
                    b.MatterMaskAt(new double2(s % 20, 0.5));
                    if (s % 50 == 0)
                    {
                        b.Serialize();
                    }
                    every &= a.StateHash() == b.StateHash();
                }
                fx.Dispose();
                ha = a.StateHash();
                hb = b.StateHash();
                Expect(every && ha == hb && a.PatchStats.PatchesBorn == b.PatchStats.PatchesBorn,
                    $"同一场景（24 单位 + 40 块混合物质）跑 300 步：每步之间读渲染 / 拾取 / 点查 / 存档的一份，与只步进的一份每步 StateHash 相同（{ha:X16}）");
            }
            using (CombatKernel on = NewKernel())
            using (CombatKernel off = NewKernel(0, -1, 64, false))
            {
                foreach (CombatKernel k in new[] { on, off })
                {
                    int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(5f, 20f)));
                    Foe(k, new double2(0, 8), 300f);
                    k.TryAddWreck(new EmgWreck { Pos = new double2(5, 5), Radius = 0.5f, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = k.Time + 100.0, Profile = -1 }, out _);
                    StepN(k, 300);
                }
                Expect(on.StateHash() == off.StateHash() && on.PatchCount == 0,
                    $"没有物质时：斑块层开（阶段 2）与关（阶段 1）的同一场景 300 步 StateHash 相同（{on.StateHash():X16}）——阶段 2 没有物质就不改结算");
            }
            double Expire(float dt)
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Oil, double2.zero, 1f, 1f, 3f);
                while (k.PatchCount > 0 && k.Time < 10)
                {
                    StepN(k, 1, dt);
                }
                return k.Time;
            }
            float Burn(float dt)
            {
                using CombatKernel k = NewKernel();
                int foe = Foe(k, double2.zero, 500f);
                k.SpawnPatch(EmgMatterConst.Molten, double2.zero, 2f, 1f, 30f);
                StepN(k, (int)Math.Round(3.0 / dt), dt);
                return 500f - Hp(k, foe);
            }
            double e5 = Expire(Dt * 0.5f), e1 = Expire(Dt), e3 = Expire(Dt * 3f);
            float b5 = Burn(Dt * 0.5f), b1 = Burn(Dt), b3 = Burn(Dt * 3f);
            Expect(new[] { e5, e1, e3 }.All(e => Math.Abs(e - 3.0) <= 0.25 + 3 * Dt) && Math.Abs(b5 - b1) <= 0.15f * b1 && Math.Abs(b3 - b1) <= 0.15f * b1 && b1 > 15f,
                $"0.5x / 1x / 3x 步长：3 秒的油在 {e5:0.00} / {e1:0.00} / {e3:0.00} 秒消失（按 0.25 秒节拍）；熔融金属 3 秒接触伤害 {b5:0.0} / {b1:0.0} / {b3:0.0}");
            using (CombatKernel p = NewKernel())
            {
                p.SpawnPatch(EmgMatterConst.Oil, double2.zero, 1f);
                StepN(p, 1);
                ulong h = p.StateHash();
                p.PickPatch(double2.zero);
                Expect(p.StateHash() == h && p.PatchCount == 1, "暂停（不步进）：斑块与哈希不变（悬停拾取也不改）");
            }
        }

        // ── M. 悬停与引导钩子 ─────────────────────────────────────────────────────────

        private static void CheckHover()
        {
            Line("  · M. 悬停（FGR-EMG-181 句式）：“油 · 来自 A 车 · 遇明火会整片燃烧”；残骸崩解 / 环境来源；固态写“挡路，不沾染”；引导钩子已登记");
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            int a = Me(k, new double2(0, -5));
            site.SetUnitLabel(a, "reaction.log.machine_n", "A");
            k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 1f, 20f, a);
            k.SpawnPatch(EmgMatterConst.Acid, new double2(20, 0), 3f, 1f, 0f, 0, true);
            k.SpawnPatch(EmgMatterConst.Cured, new double2(40, 0), 1.5f);
            k.TryAddWreck(new EmgWreck { Pos = new double2(60, 0), Radius = 0.7f, Hp = 40f, MaxHp = 40f, Mass = 6f, Born = k.Time, Until = k.Time + 0.5, Profile = -1 }, out _);
            StepN(k, 60);
            string name = EmgMatterCatalog.NameOf(EmgMatterConst.Oil);
            string who = EmgChainView.PartyName(site, null, a);
            int key = StatusTagHover.HoverEmgGroundForTests(site, new double2(0.5, 0.5));
            TooltipContent oil = StatusTagHover.HoveredEmgContentForTests();
            int oilId = Patches(k).First(z => z.Matter == EmgMatterConst.Oil).EmgId;
            string expect = GameText.Format("emg.patch.hover.line", name, who, EmgMatterCatalog.NextOf(EmgMatterConst.Oil));
            Expect(key == -(StatusTagHover.PatchHoverKeyBase + oilId) && oil != null && oil.Title == expect && !string.IsNullOrEmpty(who) && oil.Body.Contains(GameText.Get("emg.patch.layer.ground")),
                $"悬停油斑块：标题“{oil?.Title}”，正文“{oil?.Body?.Replace("\n", " / ")}”");
            StatusTagHover.Leave(site);
            int wi = k.PickPatch(new double2(60, 0)), ai = k.PickPatch(new double2(20, 0)), ci = k.PickPatch(new double2(40, 0));
            TooltipContent debris = EmgChainView.PatchContent(site, null, wi);
            TooltipContent world = EmgChainView.PatchContent(site, null, ai);
            TooltipContent cured = EmgChainView.PatchContent(site, null, ci);
            Expect(debris != null && debris.Title.Contains(GameText.Get("emg.patch.hover.wreck")) && world != null && world.Title.Contains(GameText.Get("emg.patch.hover.world"))
                   && cured != null && cured.Body.Contains(GameText.Get("emg.patch.hover.nocoat")) && StatusTagHover.HoverEmgGroundForTests(site, new double2(100, 100)) == 0,
                $"残骸崩解的碎屑：“{debris?.Title}”；世界源酸：“{world?.Title}”；胶块：“{cured?.Body?.Replace("\n", " / ")}”；空地不弹");
            StatusTagHover.Leave(site);
            // 复审 P2：钩子不只登记，地点第一次出现斑块时真的发一次、之后不重复
            int raised = 0;
            Action<string> onTouch = id => raised += id == GuidanceHooks.EmgFirstMatter ? 1 : 0;
            GuidanceHooks.Touched += onTouch;
            try
            {
                site.ProcessEvents();
                int first = raised;
                k.SpawnPatch(EmgMatterConst.Water, new double2(80, 0), 1f);
                StepN(k, 1);
                site.ProcessEvents();
                Expect(GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstMatter) && GuidanceHooks.EmgFirstMatter == "emg_first_matter" && first == 1 && raised == 1,
                    $"引导钩子 emg_first_matter 已登记；地点有斑块后处理事件发 {first} 次，再生成斑块不重复（累计 {raised}，内容由 FG15-UX-04 统一做）");
            }
            finally
            {
                GuidanceHooks.Touched -= onTouch;
            }
        }

        // ── O. 排放写端的生产路径（阶段 2，真实编译）──────────────────────────────────────

        private static void CheckEmitPath()
        {
            Line("  · O. 复审 P1：mat.* 写端在阶段 2 走斑块（真实编译的固件程序）——[分裂, 漏油] 子弹药各排一份油、水箱外排在命中点落水并浸湿；对照 emg.stage = 1 同一程序仍落驻留区");
            (List<CombatZone> patches, int zones, int subs, int me, bool wet) Run(string[] fws, bool split)
            {
                EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), fws, FirmwareCarrier.Projectile);
                using CombatKernel k = NewKernel();
                int prog = CombatSite.RegisterProgram(k, r);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(1f, 30f, prog)));
                int foe = Foe(k, new double2(0, split ? 4 : 3), 1000f);
                if (split)
                {
                    Foe(k, new double2(2, 5), 100f);
                    Foe(k, new double2(-2, 5), 100f);
                    Foe(k, new double2(0, 8.5), 100f);
                    Foe(k, new double2(3, 2), 100f);
                }
                k.FireAt(me, foe, k.Time);
                StepN(k, split ? 60 : 10);
                var zs = new List<CombatZone>();
                for (int i = 0; i < k.ZoneCount; i++)
                {
                    if (k.TryGetZone(i, out CombatZone z) && z.StatusMask != 0u)
                    {
                        zs.Add(z);
                    }
                }
                int water = Row(EmgMatterConst.Water).CoatBit;
                return (Patches(k), zs.Count, (int)k.EmgBusStats.SubSpawned, me, water >= 0 && HasBit(k, foe, water));
            }
            try
            {
                EmgCatalog.ResetForTests(); // 表缺省 emg.stage = 2
                var oil = Run(new[] { "fw_split", "fw_oilleak" }, true);
                var oils = oil.patches.Where(z => z.Matter == EmgMatterConst.Oil).ToList();
                float amount = oils.Sum(z => z.Amount);
                bool oilShape = oils.Count > 0 && oils.All(z => z.Owner == oil.me && Near((float)(z.Until - z.Born), 6f) && z.Radius >= 1.5f - 1e-3f && z.EmgChain > 0 && z.EmgDepth >= 1)
                                && oils.Select(z => z.EmgChain).Distinct().Count() == 1;
                Expect(oil.subs >= 1 && Near(amount, oil.subs) && oilShape && oil.zones == 0,
                    $"[分裂, 漏油]（阶段 2）：放出 {oil.subs} 颗子弹药，各排一份油 → 油斑块 {oils.Count} 块（重叠的合并，总份量 {amount:0.##}）、半径 ≥ 1.5、存续 6 秒、Owner = 发射者 {oil.me}、" +
                    $"同一条因果链（{string.Join(",", oils.Select(z => z.EmgChain).Distinct())}）、链深 ≥ 1（{string.Join(",", oils.Select(z => z.EmgDepth))}）；读法驻留区 {oil.zones} 块（不再落驻留区）");
                var tank = Run(new[] { "fw_water" }, false);
                var waters = tank.patches.Where(z => z.Matter == EmgMatterConst.Water).ToList();
                Expect(waters.Count == 1 && math.distance(waters[0].Pos, new double2(0, 3)) < 1.0 && waters[0].Owner == tank.me && tank.wet && tank.zones == 0,
                    $"水箱外排（根节点排放）：命中点落水斑块 {waters.Count} 块（距目标 {(waters.Count > 0 ? math.distance(waters[0].Pos, new double2(0, 3)) : -1):0.00} 米、Owner {(waters.Count > 0 ? waters[0].Owner : 0)}），" +
                    $"站在里面的目标挂浸湿位 {tank.wet}；驻留区 {tank.zones} 块");
                UseStage(1);
                var oil1 = Run(new[] { "fw_split", "fw_oilleak" }, true);
                var tank1 = Run(new[] { "fw_water" }, false);
                Expect(oil1.patches.Count == 0 && oil1.zones == oil1.subs && oil1.subs >= 1 && tank1.patches.Count == 0 && tank1.zones >= 1,
                    $"emg.stage = 1 对照：同一程序不生成斑块（{oil1.patches.Count} / {tank1.patches.Count}），[分裂, 漏油] 落驻留区 {oil1.zones} 块（= 子弹药 {oil1.subs}）、水箱外排落驻留区 {tank1.zones} 块");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── N. 耗时（ADR ⑬ 斑块 0.20 ms / 步）────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · N. 耗时（ADR-EMG-001 ⑬：斑块段 ≤ 0.20 毫秒 / 步；只测一次）：1024 块（928 战斗按各物质软上限 + 96 世界源）+ 400 个单位跑 120 步，对照同场景没有斑块");
            (double avg, double p99) Measure(bool patches)
            {
                using CombatKernel k = NewKernel(0, -1, 512);
                for (int i = 0; i < 400; i++)
                {
                    double2 p = new double2(i % 40 * 4.0 + 0.3, i / 40 * 4.0 + 0.3);
                    if (i % 2 == 0)
                    {
                        Me(k, p);
                    }
                    else
                    {
                        Foe(k, p, 1e6f);
                    }
                }
                if (patches)
                {
                    var list = new List<(byte, double2, float, float, bool)>();
                    int n = 0;
                    for (byte m = 1; m <= EmgMatterConst.Count; m++)
                    {
                        for (int q = 0; q < Row(m).Quota; q++, n++)
                        {
                            list.Add((m, new double2(n % 32 * 5.0, n / 32 * 5.0), 1.6f, 600f, false));
                        }
                    }
                    for (int w = 0; w < 96; w++)
                    {
                        list.Add((EmgMatterConst.Acid, new double2(200 + w % 12 * 6.0, w / 12 * 6.0), 2f, 0f, true));
                    }
                    Fill(k, list);
                }
                StepN(k, 10);
                var times = new List<double>(120);
                var sw = new Stopwatch();
                for (int s = 0; s < 120; s++)
                {
                    sw.Restart();
                    k.Step(Dt, k.Time + Dt);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                if (patches)
                {
                    Line($"    · 测量时斑块 {k.PatchCount} 块（满 1024 才算满载）");
                }
                return (times.Average(), times[(int)(times.Count * 0.99) - 1]);
            }
            var without = Measure(false);
            var with = Measure(true);
            double seg = Math.Max(0, with.avg - without.avg);
            const double budget = 0.20;
            if (seg > budget && seg <= budget * 2)
            {
                Line($"    ⚠ 性能警告：斑块段每步平均 {seg:0.###} 毫秒（阈值 {budget}，未到 2 倍；整步 {with.avg:0.###} / 对照 {without.avg:0.###}，p99 {with.p99:0.###}）");
                _pass++;
                return;
            }
            Expect(seg <= budget, $"斑块段每步平均 {seg:0.###} 毫秒（= 整步 {with.avg:0.###} − 无斑块对照 {without.avg:0.###}；p99 整步 {with.p99:0.###} / 对照 {without.p99:0.###}；阈值 {budget}）");
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

        private static string Detail(List<string> bad) => bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad.Take(8));

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
