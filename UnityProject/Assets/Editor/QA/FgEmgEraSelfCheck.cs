using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameConfig.fg;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FW-01（FG19 FGR-EMG-233；FGT-EMG-073 阶段 1 部分）：爆炸反应装甲的块数机制。起真实系统：真实声明表 / 调参表（emg.stage 覆盖为 1 / 0）、
    /// 真实蓝图编出的“主武器 + 爆炸反应装甲”武器（CombatSite.MachineWeaponFrom）、真实内核（FireAt 即时命中、Step 补块）、真实战斗地点的悬停与通知。
    /// A 满块 12：被触及范围内的敌人即时命中 12 次，每次扣 1 块、反伤 = 旧公式（4 + 伤害 × 0.5）、各放 3 片破片（2 代 / Prog −1 / 归宿主 / 每片 4 伤害）；第 13 次不反伤、不放破片；
    /// B 选块：攻击者在左前方时左裙从前往后起爆，左裙空了改用右裙；因果链写宿主、攻击者与块位，文字“左裙第 N 块”；
    /// C 补块：每 3 秒补回 1 块后能再起爆 1 次；宿主阵亡后不补；暂停（不步进）不走；步长 1/120、1/60、1/20（对应 0.5x～3x 每帧步数不同）补块时刻一致；
    /// D 对照：弹体命中、触及范围外的即时命中、反伤命中都不扣块；同一步 3 名攻击者只剩 2 块 → 只有 2 名吃反伤；
    /// E 链深 3 时扣块并反伤、不放破片（计 DepthCut）；弹体池满：扣块与反伤当步完成、破片计 FragLost（顺延归 DEBT-EMG1ENT01-03）；
    /// F 存读档：块位掩码与补块计时往返一致、之后续跑哈希一致；旧档（短段，单位段只有 7 个字段）读入为满块；
    /// G emg.stage = 0 回旧轨：反伤不限次数、不放破片、块位不变；
    /// H 表现：块数只在变化的步进脏列表；己方降到 0 块发一次信息级通知（可定位）；悬停“7 / 12 · 下一块 N 秒”+ 占位刻度条；描述文本与调参键一致；
    ///   飘字“反应装甲起爆”走读法进给（同键聚合、每秒上限 reaction.popup_per_second）。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgEraSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgEraSelfCheck。
    /// </summary>
    public static class FgEmgEraSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const string SiteId = "emg-era-site";

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/QA/自检/爆炸反应装甲块数")]
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
            Line("\n[爆炸反应装甲块数] FGR-EMG-233 / FGT-EMG-073（阶段 1 部分）：12 块、每次起爆扣 1 块 + 反伤 + 3 片破片、每 3 秒补 1 块、块为 0 不反伤（EMG1-FW-01）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                UseStage(1);
                Step(CheckTwelve);
                Step(CheckPick);
                Step(CheckRefill);
                Step(CheckControls);
                Step(CheckDepthAndPool);
                Step(CheckSave);
                Step(CheckStageZero);
                Step(CheckPresentation);
                Step(CheckFriendlyFire);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            return _fail;
        }

        // ── 准备 ─────────────────────────────────────────────────────────────────

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            return c;
        }

        private static CombatKernel NewKernel(int capacity = 16)
        {
            var k = new CombatKernel(Cfg(), capacity);
            CombatSite.ApplyEntityProfiles(k);
            return k;
        }

        private static CombatWeapon Armored()
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncSpikesId, null, Array.Empty<string>());
            return CombatSite.MachineWeaponFrom(BlueprintCircuitCompiler.CompilePreview(board));
        }

        private static readonly CombatWeapon Claws = new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 2.5f, Damage = 10f, Cooldown = 1f, HasOutput = 1 };
        private static readonly CombatWeapon Gun = new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 14f, Damage = 10f, Cooldown = 1f, HasOutput = 1 };

        private static CombatSpawn Machine(double2 at, int weapon) => new CombatSpawn
        {
            Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
            Position = at, Home = at, Radius = 0.6f, Speed = 4f, Health = 5000f, MaxHealth = 5000f, Weapon = weapon, BehaviorProfile = -1,
        };

        private static CombatSpawn Hostile(double2 at, int weapon, CombatBehavior behavior = CombatBehavior.None, float hp = 5000f) => new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = behavior,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (weapon >= 0 ? CombatUnitFlags.WeaponEnabled : 0),
            Position = at, Home = at, Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
        };

        private static float Lost(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.MaxHealth - v.Health : 0f;

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static int Intact(CombatKernel k, int id) => k.TryGetEra(id, out int n, out _, out _, out _) ? n : -1;

        private static ushort Miss(CombatKernel k, int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState s) ? s.EraMiss : (ushort)0xFFFF;

        private static List<CombatProjectile> Frags(CombatKernel k)
        {
            var list = new List<CombatProjectile>();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (p.Flags & EmgProjFlags.Fragment) != 0)
                {
                    list.Add(p);
                }
            }
            return list;
        }

        private static void Run(CombatKernel k, float seconds, float dt = Dt)
        {
            int n = (int)Math.Round(seconds / dt);
            for (int i = 0; i < n; i++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static float Expected(float hit)
        {
            CarrierReadings.TryGetComponent(ComponentCatalog.FuncSpikesId, out CombatComponent sp);
            return sp.Damage + hit * sp.Thorns;
        }

        // ── A. 12 块 ────────────────────────────────────────────────────────────

        private static void CheckTwelve()
        {
            Line("  · A. 满块 12：即时命中 12 次各扣 1 块、反伤 = 旧公式、各放 3 片破片；第 13 次不反伤、不放破片");
            using CombatKernel k = NewKernel(64);
            CombatWeapon armored = Armored();
            int me = k.Spawn(Machine(double2.zero, k.AddWeapon(armored)));
            int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
            Expect(k.Config.EmgEraBlocks == 12 && Mathf.Approximately(k.Config.EmgEraRefill, 3f) && armored.Reading.Thorns > 0f && Intact(k, me) == 12 && Intact(k, h) == -1,
                $"emg.stage = 1：块数机制开（{k.Config.EmgEraBlocks} 块 / 每 {k.Config.EmgEraRefill} 秒）；真实蓝图（连射器 + 爆炸反应装甲）编出的武器带反伤读法 → 宿主满块 {Intact(k, me)}；没装的敌人不算宿主");
            float per = Expected(10f);
            bool allOk = true;
            int fragTotal = 0;
            for (int i = 1; i <= 12; i++)
            {
                float before = Lost(k, h);
                int fBefore = Frags(k).Count;
                k.FireAt(h, me, k.Time);
                int added = Frags(k).Count - fBefore;
                fragTotal += added;
                allOk &= Intact(k, me) == 12 - i && Mathf.Abs(Lost(k, h) - before - per) < 0.01f && added == 3;
            }
            List<CombatProjectile> frags = Frags(k);
            Expect(allOk && fragTotal == 36 && frags.All(p => p.Gen == EmgConst.MaxGen && p.Prog == -1 && p.Owner == me && Mathf.Approximately(p.Damage, 4f) && p.Depth == 1),
                $"12 次：每次扣 1 块、敌人吃反伤 {per:0.##}（= 4 + 10 × 0.5，旧公式）、放 3 片；共 {fragTotal} 片，全部 2 代 / Prog −1 / 归宿主 / 每片 4 伤害 / 链深 1");
            float lost12 = Lost(k, h);
            int f12 = Frags(k).Count;
            k.FireAt(h, me, k.Time);
            Expect(Intact(k, me) == 0 && Mathf.Approximately(Lost(k, h), lost12) && Frags(k).Count == f12 && Lost(k, me) >= 130f,
                $"第 13 次（块为 0）：机器照常挨打（共掉 {Lost(k, me):0}），敌人不再吃反伤、不放破片");
            Run(k, 0.5f);
            Expect(Lost(k, h) > lost12, $"破片随后飞向攻击者一侧并命中（敌人再掉 {Lost(k, h) - lost12:0.##}）：破片是普通破片，命中按弹体池破片结算");
        }

        // ── B. 选块与因果链 ───────────────────────────────────────────────────────

        private static void CheckPick()
        {
            Line("  · B. 选块：攻击者在左前方 → 左裙从前往后起爆，左裙空了改用右裙；因果链写宿主、攻击者与块位");
            using CombatKernel k = NewKernel(64);
            int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
            k.SetFacing(me, new float2(0f, 1f));
            int h = k.Spawn(Hostile(new double2(-1.2, 0.8), k.AddWeapon(Claws))); // 朝向 +Z 时 −X 是左侧，+Z 半侧是前半
            var order = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                ushort before = Miss(k, me);
                k.FireAt(h, me, k.Time);
                int diff = Miss(k, me) & ~before;
                order.Add(diff != 0 ? math.tzcnt(diff) : -1);
            }
            Expect(order.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }),
                $"起爆块位顺序 {string.Join(",", order)}：左裙 0～5 从前往后，左裙空了改右裙从前往后（6、7）");
            EmgChainRec era = default;
            bool found = false;
            for (int i = 0; i < k.EmgChainRecordCount && !found; i++)
            {
                found = k.TryGetEmgChainRecent(i, out era) && era.Kind == EmgChainNode.Era;
            }
            string text = found ? EmgChainView.EraNodeText(null, null, era) : string.Empty;
            Expect(found && era.SrcUnit == me && era.Other == h && era.Aux == 8 && era.Chain > 0 && text.Contains(GameText.Get("emg.era.side.right")) && text.Contains("2"),
                $"因果链最新的起爆节点：宿主 {era.SrcUnit} / 攻击者 {era.Other} / 块位 {era.Aux - 1}（链 {era.Chain}），文字“{text}”");
            using CombatKernel k2 = NewKernel(64);
            int me2 = k2.Spawn(Machine(double2.zero, k2.AddWeapon(Armored())));
            k2.SetFacing(me2, new float2(0f, 1f));
            int hb = k2.Spawn(Hostile(new double2(1.2, -0.8), k2.AddWeapon(Claws))); // 右后
            k2.FireAt(hb, me2, k2.Time);
            Expect(Miss(k2, me2) == 1 << 11, $"对照：攻击者在右后方 → 右裙最后一块（位 11）起爆（掩码 {Miss(k2, me2)}）");
        }

        // ── C. 补块 ─────────────────────────────────────────────────────────────

        private static void CheckRefill()
        {
            Line("  · C. 补块：每 3 秒补 1 块（之后能再起爆 1 次）；宿主阵亡后不补；暂停不走；步长不同（0.5x～3x 每帧步数不同）补块时刻一致");
            using (CombatKernel k = NewKernel(64))
            {
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
                for (int i = 0; i < 12; i++)
                {
                    k.FireAt(h, me, k.Time);
                }
                k.TryGetEra(me, out _, out _, out _, out float next0);
                Expect(Intact(k, me) == 0 && Mathf.Approximately(next0, 3f), $"打空 12 块：悬停读数“下一块 {next0:0.0} 秒”");
                k.TryGetEra(me, out _, out _, out _, out float paused);
                Expect(Mathf.Approximately(paused, next0), "暂停（不步进）：补块计时不走");
                Run(k, 2.9f);
                int at29 = Intact(k, me);
                Run(k, 0.15f);
                int at305 = Intact(k, me);
                float lost = Lost(k, h);
                k.FireAt(h, me, k.Time);
                Expect(at29 == 0 && at305 == 1 && Lost(k, h) > lost + 8.9f && Intact(k, me) == 0,
                    $"2.9 秒仍 {at29} 块、3.05 秒补回 {at305} 块；再被即时命中能起爆 1 次（反伤 {Lost(k, h) - lost:0.##}）后又是 0 块");
                Run(k, 3.05f);
                int before = Intact(k, me);
                k.Kill(me, h);
                Run(k, 7f);
                k.TryGetUnitEmg(me, out _, out EmgUnitState st);
                Expect(before == 1 && st.EraMiss != 0 && math.countbits((int)st.EraMiss) == 11, $"宿主阵亡后不补：7 秒后缺块仍 {math.countbits((int)st.EraMiss)} 块");
            }
            var hit = new List<double>();
            foreach (float dt in new[] { 1f / 120f, 1f / 60f, 1f / 20f })
            {
                using CombatKernel k = NewKernel(16);
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
                k.FireAt(h, me, k.Time);
                double t0 = k.Time;
                while (Intact(k, me) < 12 && k.Time - t0 < 5)
                {
                    k.Step(dt, k.Time + dt);
                }
                hit.Add(k.Time - t0);
            }
            Expect(hit.All(t => t >= 3.0 - 1e-3 && t <= 3.0 + 1f / 20f + 1e-3),
                $"步长 1/120、1/60、1/20 推进：补回时刻 {string.Join(" / ", hit.Select(t => t.ToString("0.###")))} 秒（都在 3 秒的同一步内；补块全在内核固定步里算，倍速只改每帧步数）");
        }

        // ── D. 对照 ─────────────────────────────────────────────────────────────

        private static void CheckControls()
        {
            Line("  · D. 对照：弹体命中、触及范围外的即时命中、反伤命中都不扣块；同一步 3 名攻击者只剩 2 块 → 只有 2 名吃反伤");
            using (CombatKernel k = NewKernel(64))
            {
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                var shell = new CombatWeapon { Mode = CombatWeaponMode.Projectile, Range = 6f, Damage = 10f, Cooldown = 1f, HasOutput = 1, ProjectileSpeed = 20f, ProjectileRadius = 0.2f };
                int hp = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(shell)));
                k.FireAt(hp, me, k.Time);
                Run(k, 0.5f);
                int hg = k.Spawn(Hostile(new double2(8, 0), k.AddWeapon(Gun)));
                k.FireAt(hg, me, k.Time);
                Expect(Lost(k, me) >= 19.9f && Intact(k, me) == 12 && Lost(k, hp) == 0f && Lost(k, hg) == 0f,
                    $"弹体命中（贴身 1.5 米）与 8 米外的即时射击：机器挨了 {Lost(k, me):0}，块数仍 {Intact(k, me)}、两名攻击者都没吃反伤");
            }
            using (CombatKernel k = NewKernel(64))
            {
                CombatWeapon ram = CombatSite.MachineWeaponFrom(BlueprintCircuitCompiler.CompilePreview(
                    BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompRamId, ComponentCatalog.FuncSpikesId, null, Array.Empty<string>())));
                CombatWeapon foeThorns = Claws;
                foeThorns.Reading.Thorns = 0.5f;
                foeThorns.Reading.ThornsFlat = 2f;
                foeThorns.Reading.ThornsReach = 1.5f;
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(ram)));
                int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(foeThorns)));
                k.FireAt(me, h, k.Time);
                Expect(Intact(k, h) == 11 && Intact(k, me) == 12 && Lost(k, me) > 0f,
                    $"反伤命中不扣块：机器（刺锤 + 爆炸反应装甲）打带反伤的敌人——敌人起爆 1 块（剩 {Intact(k, h)}），机器吃了反伤 {Lost(k, me):0.##} 但自己的块不动（{Intact(k, me)}）");
            }
            using (CombatKernel k = NewKernel(64))
            {
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                k.TryGetUnitEmg(me, out short prof, out EmgUnitState st);
                st.EraMiss = (ushort)(0xFFF & ~0b11); // 只剩位 0、1 两块
                k.SetUnitEmg(me, prof, st);
                int w = k.AddWeapon(Claws);
                int h1 = k.Spawn(Hostile(new double2(1.5, 0), w, CombatBehavior.HoldFire));
                int h2 = k.Spawn(Hostile(new double2(-1.5, 0), w, CombatBehavior.HoldFire));
                int h3 = k.Spawn(Hostile(new double2(0, 1.5), w, CombatBehavior.HoldFire));
                double t0 = k.Time;
                while (Lost(k, me) < 29.9f && k.Time - t0 < 1.5)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                int reflected = new[] { h1, h2, h3 }.Count(x => Lost(k, x) > 0f);
                Expect(Lost(k, me) >= 29.9f && reflected == 2 && Intact(k, me) == 0 && Lost(k, h3) == 0f,
                    $"3 名敌人驻守开火（真实行为步进）各打一下、只剩 2 块：按出手先后前 2 名吃反伤（{reflected} 名），第 3 名（h3）不吃，块数 {Intact(k, me)}");
            }
        }

        // ── E. 链深 3 / 弹体池满 ───────────────────────────────────────────────────

        private static void CheckDepthAndPool()
        {
            Line("  · E. 链深 3 时扣块并反伤、不放破片（计 DepthCut）；弹体池满时扣块与反伤当步完成，破片按 FGR-EMG-063 顺延（之后补放、扇角不变，不计 FragLost）");
            using (CombatKernel k = NewKernel(64))
            {
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
                long cut = k.EmgBusStats.DepthCut;
                k.EraDetonateForTests(h, me, 9f, EmgConst.MaxDepth);
                Expect(Intact(k, me) == 11 && Mathf.Approximately(Lost(k, h), 9f) && Frags(k).Count == 0 && k.EmgBusStats.DepthCut == cut + 1,
                    $"链深 {EmgConst.MaxDepth}：扣 1 块（剩 {Intact(k, me)}）、反伤 {Lost(k, h)}，不放破片，DepthCut +1");
                k.EraDetonateForTests(h, me, 9f, (byte)(EmgConst.MaxDepth - 1));
                Expect(Frags(k).Count == 3 && Frags(k).All(p => p.Depth == EmgConst.MaxDepth), "对照：链深 2 的命中照常放 3 片（链深 3）");
            }
            {
                CombatConfig c = Cfg();
                c.ProjectileCapacity = 1;
                using var small = new CombatKernel(c, 16);
                CombatSite.ApplyEntityProfiles(small);
                int me = small.Spawn(Machine(double2.zero, small.AddWeapon(Armored())));
                int h = small.Spawn(Hostile(new double2(1.5, 0), small.AddWeapon(Claws)));
                int t = small.Spawn(Hostile(new double2(0, 10), -1));
                var shell = new CombatWeapon { Mode = CombatWeaponMode.Projectile, Range = 20f, Damage = 1f, Cooldown = 1f, HasOutput = 1, ProjectileSpeed = 20f, ProjectileRadius = 0.1f };
                int sh = small.Spawn(Machine(new double2(5, 0), small.AddWeapon(shell)));
                small.FireAt(sh, t, small.Time); // 占满弹体池（容量 1）
                long lost0 = small.EmgBusStats.FragLost, spawned0 = small.EmgBusStats.FragSpawned;
                small.FireAt(h, me, small.Time);
                int queued = small.EmgPendingCount;
                Expect(small.ProjectileCount == 1 && Intact(small, me) == 11 && Lost(small, h) > 8.9f && queued == 3 && small.EmgBusStats.FragLost == lost0,
                    $"弹体池满：扣块（剩 {Intact(small, me)}）与反伤（{Lost(small, h):0.##}）当步完成，3 片进待生成表顺延（{queued}），不计 FragLost");
                // 弹体池腾出空位后逐片补放：方向仍是以攻击者方向为中心的 −30° / 0° / +30°
                var angles = new List<float>();
                var seen = new HashSet<int>();
                for (int s = 0; s < 240 && angles.Count < 3; s++)
                {
                    small.Step(Dt, small.Time + Dt);
                    foreach (CombatProjectile f in Frags(small))
                    {
                        if (seen.Add(f.Id))
                        {
                            angles.Add(Mathf.Round(math.degrees(math.atan2(f.Vel.y, f.Vel.x))));
                        }
                    }
                }
                angles.Sort();
                Expect(angles.Count == 3 && angles[0] == -30f && angles[1] == 0f && angles[2] == 30f && small.EmgBusStats.FragSpawned == spawned0 + 3
                       && small.EmgBusStats.FragLost == lost0 && small.EmgPendingCount == 0,
                    $"池里有空位后 3 片按序补放（FragSpawned +{small.EmgBusStats.FragSpawned - spawned0}，FragLost +{small.EmgBusStats.FragLost - lost0}），扇角 {string.Join(" / ", angles)}°，待生成表清空");
            }
        }

        // ── F. 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSave()
        {
            Line("  · F. 存读档：块位掩码与补块计时往返一致、续跑哈希一致；旧档（短段）读入为满块");
            using CombatKernel a = NewKernel(64);
            int me = a.Spawn(Machine(double2.zero, a.AddWeapon(Armored())));
            int h = a.Spawn(Hostile(new double2(1.5, 0), a.AddWeapon(Claws)));
            for (int i = 0; i < 5; i++)
            {
                a.FireAt(h, me, a.Time);
            }
            Run(a, 1.3f);
            byte[] bytes = a.Serialize();
            using CombatKernel b = NewKernel(64);
            CombatLoadResult r = b.Load(bytes);
            a.TryGetUnitEmg(me, out _, out EmgUnitState sa);
            b.TryGetUnitEmg(me, out _, out EmgUnitState sb);
            Expect(r == CombatLoadResult.Ok && sa.EraMiss == sb.EraMiss && sa.EraRefillT == sb.EraRefillT && sa.EraMiss != 0 && sa.EraRefillT > 1f,
                $"读档：缺块掩码 {sb.EraMiss}、补块计时 {sb.EraRefillT:0.###} 与存档前一致");
            Run(a, 4f);
            Run(b, 4f);
            Expect(a.StateHash() == b.StateHash() && Intact(a, me) == Intact(b, me) && Intact(a, me) == 8,
                $"连续跑与读档后续跑 4 秒：块数 {Intact(a, me)} / {Intact(b, me)}、状态哈希一致");
            byte[] shortSeg = a.SerializeEmgShapeForTests(EmgConst.SegUnits, 7, 0);
            using CombatKernel c = NewKernel(64);
            CombatLoadResult rc = c.Load(shortSeg);
            Expect(rc == CombatLoadResult.Ok && Miss(c, me) == 0 && Intact(c, me) == 12,
                $"旧档（单位段只有 7 个字段，本卡之前写出的短段）读入：{rc}，满块 {Intact(c, me)}");
        }

        // ── G. emg.stage = 0 ────────────────────────────────────────────────────

        private static void CheckStageZero()
        {
            Line("  · G. emg.stage = 0 回旧轨：反伤不限次数、不放破片、块位不变");
            try
            {
                UseStage(0);
                using CombatKernel k = NewKernel(64);
                int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
                int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
                for (int i = 0; i < 15; i++)
                {
                    k.FireAt(h, me, k.Time);
                }
                Expect(k.Config.EmgEraBlocks == 0 && Mathf.Abs(Lost(k, h) - 15 * Expected(10f)) < 0.05f && Frags(k).Count == 0 && Miss(k, me) == 0 && Intact(k, me) == -1,
                    $"15 次即时命中：每次都反伤（共 {Lost(k, h):0.##}）、没有破片、没有块数读数（旧轨，正式流程逐字不变）");
            }
            finally
            {
                UseStage(1);
            }
        }

        // ── H. 表现 ─────────────────────────────────────────────────────────────

        /// <summary>审查修复（第 1 轮，FGR-EMG-184 / 233 第 3 条、FG19 负向矩阵“反应装甲的破片伤到近旁己方单位”）：破片不分阵营，伤到己方照常结算、发警告级友伤通知（可定位）、因果链记“友伤”。</summary>
        private static void CheckFriendlyFire()
        {
            Line("  · I. 起爆破片伤到近旁己方单位：照常结算，警告级友伤通知（可定位），因果链记“友伤”；对照：没有己方单位在扇面里不发");
            CampaignState s = CampaignState.CreateNew("emgera-ff", "Standard", 4472);
            CampaignSession.Set(0, s);
            CampaignFgStateDomains.EnsureAll(s);
            s.RegionRecords = new[] { new RegionRecord { RegionId = SiteId, ExpeditionCount = 1 } };
            NotificationCenter.ResetForTests();
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            CombatSite.ApplyEntityProfiles(k);
            int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
            site.SetUnitLabel(me, "reaction.log.machine_n", "A");
            // 攻击者站在触及边缘（2.4 米，触及 1.5 + 双方半径），±30° 两片从它身旁掠过；己方 B 车站在 +30° 那片的路线上
            int h = k.Spawn(Hostile(new double2(2.4, 0), k.AddWeapon(Claws)));
            int ally = k.Spawn(Machine(new double2(2.7, 1.15), -1));
            site.SetUnitLabel(ally, "reaction.log.machine_n", "B");
            site.Step(Dt, k.Time + Dt);
            long posted0 = ReactionFeedback.FriendlyPosted;
            float allyHp0 = Hp(k, ally);
            k.FireAt(h, me, k.Time);
            int listed = 0;
            for (int i = 0; i < 30; i++)
            {
                site.Step(Dt, k.Time + Dt);
                for (int j = 0; j < k.EmgFriendlyCount; j++)
                {
                    listed += k.EmgFriendlyAt(j).x == ally && k.EmgFriendlyAt(j).y == me ? 1 : 0;
                }
            }
            bool chained = false;
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                chained |= k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Friendly && r.SrcUnit == ally && r.Other == me;
            }
            NotificationEntry n = NotificationCenter.History.Concat(NotificationCenter.Toasts).LastOrDefault(e => e.Type?.Id == "friendly_fire");
            Expect(Hp(k, ally) < allyHp0 - 0.5f && listed >= 1 && chained && n != null && n.Level == NotifyLevel.Warning && n.HasAnyLocation
                   && ReactionFeedback.FriendlyPosted > posted0,
                $"破片打到己方 B 车（耐久 {allyHp0:0} → {Hp(k, ally):0.##}）：友伤列表 {listed} 条、因果链“友伤”节点 {(chained ? "有" : "无")}、" +
                $"警告级通知“{n?.Text}”（可定位 {n?.HasAnyLocation}，本段发出 {ReactionFeedback.FriendlyPosted - posted0} 条，同类聚合）；链节点文字“{(chained ? EmgChainView.NodeText(site, s, FriendlyRec(k, ally), 1) : string.Empty)}”");

            // 对照：同样起爆，扇面里没有己方单位 → 不进友伤列表、不发通知
            long posted1 = ReactionFeedback.FriendlyPosted;
            k.Despawn(ally);
            for (int i = 0; i < 200; i++)
            {
                site.Step(Dt, k.Time + Dt); // 等块补回、破片飞完
            }
            k.FireAt(h, me, k.Time);
            int listed2 = 0;
            for (int i = 0; i < 30; i++)
            {
                site.Step(Dt, k.Time + Dt);
                listed2 += k.EmgFriendlyCount;
            }
            Expect(listed2 == 0 && ReactionFeedback.FriendlyPosted == posted1, $"对照：扇面里没有己方单位时友伤列表 {listed2} 条、不发通知");
        }

        private static EmgChainRec FriendlyRec(CombatKernel k, int ally)
        {
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Friendly && r.SrcUnit == ally)
                {
                    return r;
                }
            }
            return default;
        }

        private static void CheckPresentation()
        {
            Line("  · H. 块数只在变化的步进脏列表；己方降到 0 块发一次信息级通知；悬停 7 / 12 + 占位刻度条；飘字走读法进给；描述文本与调参键一致");
            CampaignState s = CampaignState.CreateNew("emgera", "Standard", 4471);
            CampaignSession.Set(0, s);
            CampaignFgStateDomains.EnsureAll(s);
            s.RegionRecords = new[] { new RegionRecord { RegionId = SiteId, ExpeditionCount = 1 } };
            NotificationCenter.ResetForTests();
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            CombatSite.ApplyEntityProfiles(k);
            int me = k.Spawn(Machine(double2.zero, k.AddWeapon(Armored())));
            site.SetUnitLabel(me, "reaction.log.machine_n", "A");
            int h = k.Spawn(Hostile(new double2(1.5, 0), k.AddWeapon(Claws)));
            site.Step(Dt, k.Time + Dt);
            long era0 = k.ReadingFeedCountOf(CombatConst.ReadingFeedEra);
            k.FireAt(h, me, k.Time);
            site.Step(Dt, k.Time + Dt);
            bool dirtyOnHit = Enumerable.Range(0, k.EmgEraDirtyCount).Any(i => k.EmgEraDirtyAt(i) == me);
            site.Step(Dt, k.Time + Dt);
            int quiet = k.EmgEraDirtyCount;
            Expect(dirtyOnHit && quiet == 0 && k.ReadingFeedCountOf(CombatConst.ReadingFeedEra) == era0 + 1
                   && ReactionFeedback.ReadingNameKey(CombatConst.ReadingFeedEra) == "reading.popup.era" && GameText.Get("reading.popup.era") == "反应装甲起爆",
                $"起爆那一步宿主进脏列表，下一步没变化时列表为空（{quiet}）；读法进给“{GameText.Get("reading.popup.era")}” +1（弹字同键聚合、每秒上限沿用 reaction.popup_per_second）");

            for (int i = 0; i < 4; i++)
            {
                k.FireAt(h, me, k.Time);
            }
            site.Step(Dt, k.Time + Dt);
            k.TryGetPosition(me, out double2 mp);
            StatusTagHover.HoverGroundForTests(site, mp);
            TooltipContent c = StatusTagHover.HoveredContentForTests();
            string body = c?.Body ?? string.Empty;
            Expect(body.Contains("爆炸反应装甲 7 / 12") && body.Contains("每 3 秒补 1 块") && body.Contains("下一块") && body.Contains("■") && body.Contains("□") && body.Contains("▣")
                   && body.Contains("占位"),
                $"悬停宿主：“{body.Replace("\n", " / ")}”");
            StatusTagHover.Leave(site);
            Expect(EmgChainView.EraGauge(12, 0b1000_0000_0011) == GameText.Format("emg.era.gauge", "▣□■■■■", "■■■■■□"),
                $"刻度条以形状区分完整 / 缺块 / 补充中：{EmgChainView.EraGauge(12, 0b1000_0000_0011)}");

            long posted0 = ReactionFeedback.EraDepletedPosted, seen0 = ReactionFeedback.EraChangesSeen;
            for (int i = 0; i < 7; i++)
            {
                k.FireAt(h, me, k.Time);
            }
            site.Step(Dt, k.Time + Dt);
            k.FireAt(h, me, k.Time); // 0 块再挨打：块数不变，不再进脏列表、不重复通知（site.Step 末尾每步调一次 ReactionFeedback.Process）
            site.Step(Dt, k.Time + Dt);
            NotificationEntry n = NotificationCenter.History.Concat(NotificationCenter.Toasts).LastOrDefault(e => e.Type?.Id == "era_depleted");
            Expect(n != null && ReactionFeedback.EraDepletedPosted == posted0 + 1 && n.Level == NotifyLevel.Info && n.HasAnyLocation
                   && n.Text.Contains("反应装甲耗尽") && n.Text.Contains("秒后补回 1 块"),
                $"块数降到 0：发一次信息级通知“{n?.Text}”（可定位）；0 块再挨打不重复发（发出 {ReactionFeedback.EraDepletedPosted - posted0} 条，脏列表变化 {ReactionFeedback.EraChangesSeen - seen0} 次，块数 {Intact(k, me)}，纪元 {k.FeedEpoch}，反应修订 {site.ReactionRevision}/{NamedReactionCatalog.Revision}）");

            string desc = GameText.Get("component.func_spikes.desc");
            Expect(desc.Contains($"共 {k.Config.EmgEraBlocks} 块") && desc.Contains($"每 {k.Config.EmgEraRefill:0.#} 秒补回 1 块") && desc.Contains("块用完时不反伤") && desc.Contains("破片"),
                $"描述文本与行为一致（块数 / 补块秒数取自调参键，fgdata_emg.validate 生成前核对）：{desc}");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

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
