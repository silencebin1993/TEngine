using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMGR-CMD-01（FG-GAP-142，GDD §8.3“非接入单位按自身装配和教义战斗”，FGR-BASE-020 例外“AI 教义范围内的战斗行为”）：非接入星球机器按教义原地自卫。
    /// 内核段：移动 / 赶路途中射程内有敌人原地开火、路线与没有标记时逐步一致、仍走到目的地；空闲 / 施工原地打、不追击射程外的；没武器 / 被接入 / 打不到空 / 潜伏 / 子母雷 / 过热不开火；
    /// 玩家攻击命令与驻防守点优先、不叠开火；标记随快照往返、续跑哈希一致，旧档（短段）读成 0。
    /// 家园段（正式入口）：右键派出的非远征编队路过敌人还手且走到目的地、远征行军途中还手且走到地点、施工中的机器原地开火工单不中断、覆盖外 / 静默夜照样自卫、
    /// 被接入的不交给教义、悬停写“由教义触发：自卫交战”、交战中存档读档 / 0.5x～3x / 逐帧观察 / 暂停结果一致。
    /// </summary>
    public static partial class FgNavSelfCheck
    {
        public static int RunSelfDefense(StringBuilder report) => RunSteps(report,
            "\n[教义自卫] 非接入机器原地还手、不追击不改命令、负向矩阵、悬停来源、存读档 / 倍速 / 观察 / 暂停（EMGR-CMD-01）", "[教义自卫]",
            CheckSdMoveKeepsRoute, CheckSdIdleInPlace, CheckSdDeployAndStealth, CheckSdNegatives, CheckSdPriority, CheckSdSnapshot,
            CheckSdHomeSquad, CheckSdHomeBuild, CheckSdHomeSignal, CheckSdHomeExpedition, CheckSdHomeMatrix);

        private const float SdDt = 1f / 60f;

        private static CombatKernel SdKernel()
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            return k;
        }

        private static CombatWeapon SdGun(float range = 6f, float heat = 0f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = 1f, Cooldown = 0.5f, Program = -1,
            HeatPerShot = heat, OverheatAt = heat > 0f ? heat * 2.5f : 0f, RecoverBelow = heat > 0f ? heat * 0.5f : 0f, Dissipation = heat > 0f ? 1f : 0f,
        };

        private static int SdMachine(CombatKernel k, double2 p, int weapon, int profile = -1) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled, Position = p, Home = p, Radius = 0.5f, Speed = 4f,
            Health = 5000f, MaxHealth = 5000f, Weapon = weapon, BehaviorProfile = profile, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
        });

        private static int SdHostile(CombatKernel k, double2 p, float hp = 200f, CombatUnitFlags extra = 0, int profile = -1) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | extra, Position = p, Home = p, Radius = 0.8f, Speed = 0f,
            Health = hp, MaxHealth = hp, Weapon = -1, BehaviorProfile = profile, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
        });

        private static void SdStep(CombatKernel k, int n = 1)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(SdDt, k.Time + SdDt);
            }
        }

        private static float SdHp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static double2 SdPos(CombatKernel k, int id) => k.TryGetPosition(id, out double2 p) ? p : new double2(double.NaN, double.NaN);

        private static byte SdBits(CombatKernel k, int id) => k.TryGetSiegeUnit(id, out CombatSiegeUnit su) ? su.SelfDefense : (byte)255;

        private static bool SdEngaged(CombatKernel k, int id) => (SdBits(k, id) & CombatSiegeConst.SelfDefenseEngaged) != 0;

        // ── 内核：移动 / 赶路途中原地还手，路线逐步不变，仍走到目的地 ─────────────────────────────

        private static void CheckSdMoveKeepsRoute()
        {
            foreach (CombatCommandKind kind in new[] { CombatCommandKind.Move, CombatCommandKind.WorkMove })
            {
                (List<double2> path, float lost, bool kept, bool arrived, long shots, bool engagedSeen) Run(bool flag)
                {
                    using CombatKernel k = SdKernel();
                    int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                    int foe = SdHostile(k, new double2(3, 15));
                    k.IssueCommand(me, kind, new double2(0, 30), 0, 0.5f, 0f, 0f, false);
                    if (flag)
                    {
                        k.SetMachineSelfDefense(me, true);
                    }
                    var path = new List<double2>();
                    bool kept = true;
                    bool engagedSeen = false;
                    bool arrived = false;
                    for (int s = 0; s < 900 && !arrived; s++)
                    {
                        SdStep(k);
                        path.Add(SdPos(k, me));
                        engagedSeen |= SdEngaged(k, me);
                        bool has = k.TryGetCommand(me, out CombatCommand c) && c.Kind != CombatCommandKind.None;
                        kept &= !has || c.Kind == kind; // 途中命令一直是原来那条（不被换成攻击 / 追击）
                        arrived = !has;
                    }
                    double2 end = SdPos(k, me);
                    return (path, 200f - SdHp(k, foe), kept, arrived && math.distance(end, new double2(0, 30)) <= 0.6, k.SiegeStats.SelfDefenseShots, engagedSeen);
                }
                var on = Run(true);
                var off = Run(false);
                double dev = 0;
                for (int i = 0; i < Math.Min(on.path.Count, off.path.Count); i++)
                {
                    dev = Math.Max(dev, math.distance(on.path[i], off.path[i]));
                }
                Expect(on.lost > 0f && on.shots > 0 && on.engagedSeen && on.kept && on.arrived && off.lost == 0f && off.shots == 0
                       && on.path.Count == off.path.Count && dev < 1e-9,
                    $"{kind} 途中（带教义自卫标记）：路边 3 米外的敌人进射程即原地开火（{on.shots} 发、掉血 {on.lost:0.#}），命令始终是 {kind}（{on.kept}）、走到目的地（{on.arrived}）；" +
                    $"路线与没有标记时逐步一致（{on.path.Count} / {off.path.Count} 步、最大偏差 {dev:0.###} 米）；对照没有标记不开火（{off.shots} 发、掉血 {off.lost:0.#}）");
            }
        }

        // ── 内核：空闲 / 施工中原地打射程内的、不追击射程外的 ─────────────────────────────

        private static void CheckSdIdleInPlace()
        {
            using CombatKernel k = SdKernel();
            int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun(6f)));
            int near = SdHostile(k, new double2(4, 0), 3f);
            int far = SdHostile(k, new double2(9, 0));
            k.SetMachineSelfDefense(me, true);
            double2 p0 = SdPos(k, me);
            bool engagedWhileNear = false;
            double moved = 0;
            for (int s = 0; s < 300; s++)
            {
                SdStep(k);
                engagedWhileNear |= SdEngaged(k, me) && k.IsAlive(near);
                moved = Math.Max(moved, math.distance(SdPos(k, me), p0));
            }
            bool nearDead = !k.IsAlive(near);
            bool notEngagedAfter = !SdEngaged(k, me);
            Expect(nearDead && engagedWhileNear && notEngagedAfter && moved == 0 && SdHp(k, far) == 200f && !k.TryGetCommand(me, out _),
                $"空闲（施工中同为没有内核命令）的机器：射程 6 米内的敌人被原地打掉（{nearDead}），射程外 9 米的不追击、不掉血（{SdHp(k, far):0}）；全程位置不动（最大位移 {moved:0.###} 米），" +
                $"没有命令被加上；交战时“正在自卫交战”位亮（{engagedWhileNear}），射程内没敌人后熄灭（{notEngagedAfter}）");
        }

        // ── 内核（修复轮 1）：驻锄曲射炮移动途中不架炮、停着架好再打；潜伏中不自己现形 ─────────────────────────────

        private static void CheckSdDeployAndStealth()
        {
            foreach (CombatCommandKind kind in new[] { CombatCommandKind.Move, CombatCommandKind.WorkMove })
            {
                (List<double2> path, bool arrived, long shots, bool engagedSeen, bool deployed) Run(bool flag)
                {
                    using CombatKernel k = SdKernel();
                    int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()), k.AddProfile(new CombatBehaviorProfile { DeploySeconds = 1f }));
                    SdHostile(k, new double2(3, 15));
                    k.IssueCommand(me, kind, new double2(0, 30), 0, 0.5f, 0f, 0f, false);
                    if (flag)
                    {
                        k.SetMachineSelfDefense(me, true);
                    }
                    var path = new List<double2>();
                    bool engagedSeen = false, deployed = false, arrived = false;
                    for (int s = 0; s < 900 && !arrived; s++)
                    {
                        SdStep(k);
                        path.Add(SdPos(k, me));
                        engagedSeen |= SdEngaged(k, me);
                        deployed |= k.TryGetFoundry(me, out CombatFoundryUnit f) && f.Deploy != 0;
                        arrived = !(k.TryGetCommand(me, out CombatCommand c) && c.Kind != CombatCommandKind.None);
                    }
                    return (path, arrived && math.distance(SdPos(k, me), new double2(0, 30)) <= 0.6, k.SiegeStats.SelfDefenseShots, engagedSeen, deployed);
                }
                var on = Run(true);
                var off = Run(false);
                double dev = 0;
                for (int i = 0; i < Math.Min(on.path.Count, off.path.Count); i++)
                {
                    dev = Math.Max(dev, math.distance(on.path[i], off.path[i]));
                }
                Expect(on.arrived && on.shots == 0 && !on.engagedSeen && !on.deployed && on.path.Count == off.path.Count && dev < 1e-9,
                    $"驻锄曲射炮 {kind} 途中（带标记）：不架炮（架设过 {on.deployed}）、不计开火（{on.shots}）、不亮“自卫交战”（{on.engagedSeen}），" +
                    $"路线与没有标记时逐步一致（{on.path.Count} / {off.path.Count} 步、最大偏差 {dev:0.###} 米）、走到目的地（{on.arrived}）");
            }
            {
                using CombatKernel k = SdKernel();
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun(6f)), k.AddProfile(new CombatBehaviorProfile { DeploySeconds = 1f }));
                int foe = SdHostile(k, new double2(4, 0));
                k.SetMachineSelfDefense(me, true);
                int firstHit = -1;
                bool deployedAtHit = false;
                long shotsAtHit = -1;
                double moved = 0;
                for (int s = 0; s < 300; s++)
                {
                    SdStep(k);
                    moved = Math.Max(moved, math.distance(SdPos(k, me), new double2(0, 0)));
                    if (firstHit < 0 && SdHp(k, foe) < 200f)
                    {
                        firstHit = s;
                        deployedAtHit = k.TryGetFoundry(me, out CombatFoundryUnit f) && f.Deploy == 2;
                        shotsAtHit = k.SiegeStats.SelfDefenseShots;
                    }
                }
                Expect(firstHit >= 55 && deployedAtHit && shotsAtHit == 1 && k.SiegeStats.SelfDefenseShots > 1 && moved == 0,
                    $"驻锄曲射炮停着（带标记）：先架驻锄（约 1 秒）再开火（第 {firstHit} 步首次命中、此时已架好 {deployedAtHit}），架设中的“瞄准中”不计一发（首中时计数 {shotsAtHit}），" +
                    $"之后持续开火（{k.SiegeStats.SelfDefenseShots} 发），全程不动（{moved:0.###} 米）");
            }
            {
                using CombatKernel k = SdKernel();
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun(6f)), k.AddProfile(new CombatBehaviorProfile { LurkSeconds = 2f, RevealSeconds = 0.8f })); // 潜伏时长 > 预警时长（实际内容同样如此；反过来预警一结束就又潜伏，永远打不出）
                k.SetMachineSelfDefense(me, true);
                bool Hidden() => k.TryGetUnit(me, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Stealthed) != 0;
                SdStep(k, 150);
                bool lurked = Hidden();
                int foe = SdHostile(k, new double2(4, 0));
                bool engagedHidden = false;
                for (int s = 0; s < 180; s++)
                {
                    SdStep(k);
                    engagedHidden |= SdEngaged(k, me);
                }
                bool stillHidden = Hidden();
                long shotsHidden = k.SiegeStats.SelfDefenseShots;
                float hpHidden = SdHp(k, foe);
                k.IssueCommand(me, CombatCommandKind.Move, new double2(0, 1), 0, 0.3f, 0f, 0f, false); // 玩家挪一下 → 现形
                SdStep(k, 180);
                Expect(lurked && stillHidden && shotsHidden == 0 && hpHidden == 200f && !engagedHidden && !Hidden() && SdHp(k, foe) < 200f && k.SiegeStats.SelfDefenseShots > 0,
                    $"吸波伪装罩潜伏中（带标记）：敌人进射程 3 秒不自己现形（静止 2.5 秒后潜伏 {lurked}、仍潜伏 {stillHidden}、开火 {shotsHidden}、敌人血 {hpHidden:0}、交战位 {engagedHidden}）；" +
                    $"玩家挪动现形后照常还手（敌人血 {SdHp(k, foe):0}、开火 {k.SiegeStats.SelfDefenseShots}）");
            }
        }

        // ── 内核：负向矩阵 ─────────────────────────────

        private static void CheckSdNegatives()
        {
            // 没武器：带了标记也不交战。
            using (CombatKernel k = SdKernel())
            {
                int me = SdMachine(k, new double2(0, 0), -1);
                int foe = SdHostile(k, new double2(2, 0));
                k.SetMachineSelfDefense(me, true);
                SdStep(k, 120);
                Expect(SdHp(k, foe) == 200f && !SdEngaged(k, me) && k.SiegeStats.SelfDefenseShots == 0, "没武器的机器带着标记也不交战（敌人不掉血、没有交战位、自卫开火 0 发）");
            }
            // 还在工厂里（EngageHold，与自动交战同一口径）：不交战；驶出后照常。
            using (CombatKernel k = SdKernel())
            {
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                int foe = SdHostile(k, new double2(2, 0));
                k.SetMachineSelfDefense(me, true);
                k.SetFlag(me, CombatUnitFlags.EngageHold, true);
                SdStep(k, 120);
                float held = SdHp(k, foe);
                k.SetFlag(me, CombatUnitFlags.EngageHold, false);
                SdStep(k, 60);
                Expect(held == 200f && SdHp(k, foe) < 200f, $"厂内机器（EngageHold）不按教义自卫（{held:0}），驶出工厂后照常还手（{SdHp(k, foe):0.#}）");
            }
            // 交战中被接入：这一步起停教义（不再开火、交战位清掉），命令保留。
            using (CombatKernel k = SdKernel())
            {
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                int foe = SdHostile(k, new double2(3, 0));
                k.SetMachineSelfDefense(me, true);
                SdStep(k, 60);
                float hpBefore = SdHp(k, foe);
                bool wasEngaged = SdEngaged(k, me);
                k.SetFlag(me, CombatUnitFlags.Possessed, true);
                SdStep(k);
                bool clearedNow = !SdEngaged(k, me);
                float hp1 = SdHp(k, foe);
                SdStep(k, 120);
                Expect(wasEngaged && hpBefore < 200f && clearedNow && SdHp(k, foe) == hp1 && hp1 == hpBefore,
                    $"交战中被接入：之前在打（掉到 {hpBefore:0.#}），接入那一步交战位即清掉（{clearedNow}），之后 2 秒不再开火（{SdHp(k, foe):0.#}）");
            }
            // 射程内只有空中单位，格斗武器打不到空：不锁定；射弹武器对同一架照打（对照）。地面目标格斗照样锁定（对照）。
            {
                bool Engages(bool melee, bool air, out float lost)
                {
                    using CombatKernel k = SdKernel();
                    CombatWeapon w = melee
                        ? new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 2.5f, Damage = 1f, Cooldown = 0.5f, Program = -1,
                            Reading = new CombatReading { Carrier = CombatCarrier.Melee, Area = 2f } }
                        : SdGun();
                    int me = SdMachine(k, new double2(0, 0), k.AddWeapon(w));
                    int prof = air ? k.AddProfile(new CombatBehaviorProfile { Airborne = 1f }) : -1;
                    int foe = SdHostile(k, new double2(1.6, 0), 200f, 0, prof);
                    k.SetMachineSelfDefense(me, true);
                    bool seen = false;
                    for (int s = 0; s < 120; s++)
                    {
                        SdStep(k);
                        seen |= SdEngaged(k, me);
                    }
                    lost = 200f - SdHp(k, foe);
                    return seen;
                }
                bool meleeAir = Engages(true, true, out float lostMa);
                bool gunAir = Engages(false, true, out float lostGa);
                bool meleeGround = Engages(true, false, out _);
                Expect(!meleeAir && lostMa == 0f && gunAir && lostGa > 0f && meleeGround,
                    $"射程内只有空中单位、格斗武器打不到空：不锁定、不开火（{meleeAir} / 掉血 {lostMa:0}）；对照：射弹武器照打同一架（{gunAir} / 掉血 {lostGa:0.#}），格斗对地面目标照样锁定（{meleeGround}）");
            }
            // 潜伏中 / 子母雷（只吃范围伤害）不锁定：更远的普通敌人照打（驻防守点按卡片保持原选敌，不在本段）。
            foreach (CombatUnitFlags hidden in new[] { CombatUnitFlags.Stealthed, CombatUnitFlags.AreaOnly })
            {
                using CombatKernel k = SdKernel();
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                int sneaky = SdHostile(k, new double2(2, 0), 200f, hidden);
                int open = SdHostile(k, new double2(5, 0));
                k.SetMachineSelfDefense(me, true);
                SdStep(k, 120);
                Expect(SdHp(k, sneaky) == 200f && SdHp(k, open) < 200f && SdPos(k, me).Equals(new double2(0, 0)),
                    $"教义自卫：近处 2 米的{(hidden == CombatUnitFlags.Stealthed ? "潜伏" : "子母雷（AreaOnly）")}目标不锁定（{SdHp(k, sneaky):0}），5 米外的普通敌人照打（{SdHp(k, open):0.#}），原地不动");
            }
            // 过热停火：过热期间一发不打，散热到恢复线以下再打。
            using (CombatKernel k = SdKernel())
            {
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun(6f, 4f)));
                int foe = SdHostile(k, new double2(3, 0), 5000f);
                k.SetMachineSelfDefense(me, true);
                int hotSteps = 0;
                int hotHits = 0;
                bool resumed = false;
                bool wasHot = false;
                bool prevHot = false;
                float last = SdHp(k, foe);
                for (int s = 0; s < 60 * 16; s++)
                {
                    SdStep(k);
                    k.TryGetUnit(me, out CombatUnitView v);
                    bool hot = (v.Flags & CombatUnitFlags.Overheated) != 0;
                    float hp = SdHp(k, foe);
                    if (prevHot && hot)
                    {
                        hotSteps++;
                        hotHits += hp < last ? 1 : 0; // 这一步开始时已过热（推过过热线的那一发不算）
                    }
                    resumed |= wasHot && !hot && hp < last;
                    wasHot |= hot;
                    prevHot = hot;
                    last = hp;
                }
                Expect(hotSteps > 60 && hotHits == 0 && resumed,
                    $"过热停火：过热 {hotSteps} 步里命中 {hotHits} 次（应为 0），恢复后接着打（{resumed}）");
            }
        }

        // ── 内核：玩家攻击命令、驻防守点优先，不叠开火 ─────────────────────────────

        private static void CheckSdPriority()
        {
            // 玩家对同一台下攻击命令：只按攻击命令打指定目标，旁边更近的敌人不被教义顺手打（带不带标记逐步一致）。
            (List<float> t, List<float> h) Attack(bool flag)
            {
                using CombatKernel k = SdKernel();
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                int target = SdHostile(k, new double2(5, 0));
                int other = SdHostile(k, new double2(2, 2));
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(5, 0), target, 0.5f, 6f, 0.5f, false);
                if (flag)
                {
                    k.SetMachineSelfDefense(me, true);
                }
                var t = new List<float>();
                var h = new List<float>();
                for (int s = 0; s < 240; s++)
                {
                    SdStep(k);
                    t.Add(SdHp(k, target));
                    h.Add(SdHp(k, other));
                }
                return (t, h);
            }
            var a = Attack(true);
            var b = Attack(false);
            Expect(a.t.SequenceEqual(b.t) && a.h.SequenceEqual(b.h) && a.t.Last() < 200f && a.h.Last() == 200f,
                $"玩家攻击命令优先：带标记与不带逐步一致（目标 {a.t.Last():0.#} / {b.t.Last():0.#}），旁边更近的敌人不被顺手打（{a.h.Last():0}）");

            // 驻防守点之下（空闲 + 守点半径）：走守点路径、不再叠一份自卫开火；守点计数照旧，自卫计数 0。
            (List<float> hp, long guardShots, long sdShots) Guard(bool flag)
            {
                using CombatKernel k = SdKernel();
                int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
                int foe = SdHostile(k, new double2(4, 0));
                k.SetMachineGuard(me, new double2(0, 0), 10f);
                if (flag)
                {
                    k.SetMachineSelfDefense(me, true);
                }
                var hp = new List<float>();
                for (int s = 0; s < 240; s++)
                {
                    SdStep(k);
                    hp.Add(SdHp(k, foe));
                }
                return (hp, k.SiegeStats.GuardShots, k.SiegeStats.SelfDefenseShots);
            }
            var g1 = Guard(true);
            var g0 = Guard(false);
            Expect(g1.hp.SequenceEqual(g0.hp) && g1.guardShots == g0.guardShots && g1.guardShots > 0 && g1.sdShots == 0,
                $"驻防守点之下：带标记与不带逐步一致（守点开火 {g1.guardShots} / {g0.guardShots} 发），自卫不叠开火（{g1.sdShots} 发）——同一步同一台只走一条路径");
        }

        // ── 内核：标记进快照 ─────────────────────────────

        private static void CheckSdSnapshot()
        {
            using CombatKernel k = SdKernel();
            int me = SdMachine(k, new double2(0, 0), k.AddWeapon(SdGun()));
            int foe = SdHostile(k, new double2(3, 12), 5000f);
            k.IssueCommand(me, CombatCommandKind.Move, new double2(0, 30), 0, 0.5f, 0f, 0f, false);
            k.SetMachineSelfDefense(me, true);
            int saveAt = -1;
            for (int s = 0; s < 400 && saveAt < 0; s++)
            {
                SdStep(k);
                if (SdEngaged(k, me))
                {
                    saveAt = s;
                }
            }
            byte bits = SdBits(k, me);
            byte[] snap = k.Serialize();
            using CombatKernel k2 = SdKernel();
            CombatLoadResult lr = k2.Load(snap);
            byte bits2 = SdBits(k2, me);
            ulong h0 = k.StateHash();
            ulong h1 = k2.StateHash();
            SdStep(k, 300);
            SdStep(k2, 300);
            bool same = k.StateHash() == k2.StateHash();
            bool kept = SdHp(k2, foe) < 5000f;
            Expect(saveAt >= 0 && lr == CombatLoadResult.Ok && bits == (CombatSiegeConst.SelfDefenseOn | CombatSiegeConst.SelfDefenseEngaged) && bits2 == bits && h0 == h1 && same && kept,
                $"移动途中自卫交战时（第 {saveAt} 步）存档：标记与交战位随快照往返（{bits} → {bits2}，读档 {lr}），读档后哈希一致、各续跑 300 步仍一致（{same}）");

            // 旧档（本卡之前写出的版本 20 单位段：36 个字段，没有标记）：读成 0（没交给教义自卫）。
            byte[] shortUnits = k.SerializeEmgShapeForTests(EmgConst.SegUnits, 36, 0);
            using CombatKernel k3 = SdKernel();
            CombatLoadResult ls = k3.Load(shortUnits);
            Expect(ls == CombatLoadResult.Ok && SdBits(k3, me) == 0,
                $"旧档（单位段只有 36 个字段，本卡之前写出的短段）照读（{ls}）：教义自卫标记取 0（{SdBits(k3, me)}），之后由热更层对账按判定重写");
            // 没有任何标记的状态：哈希与修改前同一口径（标记为 0 时不混入）。
            using CombatKernel k4 = SdKernel();
            int m4 = SdMachine(k4, new double2(0, 0), k4.AddWeapon(SdGun()));
            ulong before = k4.StateHash();
            k4.SetMachineSelfDefense(m4, true);
            ulong flagged = k4.StateHash();
            k4.SetMachineSelfDefense(m4, false);
            Expect(flagged != before && k4.StateHash() == before, "标记进状态哈希（设了标记哈希变化，清掉后回到原值；没有标记的旧状态哈希逐字不变）");
        }

        // ── 家园正式入口 ─────────────────────────────

        private static HomeValleyController SdHome(int seed, out CampaignState s, out GridCell center)
        {
            s = NewCampaign(seed);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            center = default;
            HomeGridService.RevealArea(s, new Vector2(HomeGridService.CorePivot(s).X, HomeGridService.CorePivot(s).Y), 90f);
            GridCell? open = FindOpenArea(s, 17, 30, 70);
            if (open == null)
            {
                Fail("家园找不到空地做教义自卫" + _openAreaMiss);
                return null;
            }
            center = open.Value;
            return home;
        }

        private static int SdUnit(CombatSite site, int logicId) => site.TryGetMachineUnit(logicId, out int unit) ? unit : 0;

        private static string SdHover(CombatSite site, int logicId)
        {
            if (!site.TryGetMachinePosition(logicId, out Vector2 p))
            {
                return string.Empty;
            }
            StatusTagHover.HoverGroundForTests(site, new double2(p.x, p.y));
            TooltipContent c = StatusTagHover.HoveredContentForTests();
            return c?.Body ?? string.Empty;
        }

        /// <summary>右键派出的非远征编队：路边的敌对单位（与驻军 / 巡逻同一阵营、同一套选敌）进射程就还手，仍按移动命令走到目的地；悬停写来源、日志可追溯。</summary>
        private static void CheckSdHomeSquad()
        {
            HomeValleyController home = SdHome(9101, out CampaignState s, out GridCell center);
            if (home == null)
            {
                return;
            }
            CombatSite site = home.Combat;
            int id = SpawnHomeMachine(s, new Vector2(center.X - 14, center.Y), combat: true);
            int foe = HomeHostile(home, new Vector2(center.X, center.Y + 4), 60f);
            WorldSimulation.StepMany(GameClock.StepHz); // 攻城服务对账写标记（siege.sync_seconds）
            var dest = new Vector2(center.X + 14, center.Y);
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(dest, paused: false);
            int unit = SdUnit(site, id);
            long engagements0 = site.SelfDefenseEngagements;
            bool kept = true;
            bool engaged = false;
            string hover = null;
            bool arrived = false;
            for (int step = 0; step < GameClock.StepHz * 40 && !arrived; step++)
            {
                WorldSimulation.StepMany(1);
                bool has = site.TryGetCommand(unit, out CombatCommand c) && c.Kind != CombatCommandKind.None;
                kept &= !has || c.Kind == CombatCommandKind.Move;
                if (site.IsSelfDefending(unit))
                {
                    engaged = true;
                    hover ??= SdHover(site, id);
                }
                arrived = !has && site.TryGetMachinePosition(id, out Vector2 p) && Vector2.Distance(p, dest) <= 2.5f;
            }
            float hp = site.Kernel.TryGetUnit(foe, out CombatUnitView fv) ? fv.Health : 0f;
            string want = GameText.Get("doctrine.selfdefense");
            Expect(engaged && hp < 60f && kept && arrived && hover != null && hover.Contains(want) && want == "由教义触发：自卫交战" && site.SelfDefenseEngagements > engagements0,
                $"家园右键派出的非远征编队（选中 → 右键地面移动）：路边 4 格外的敌对单位进射程就还手（掉到 {hp:0.#}），命令始终是移动（{kept}），走到目的地（{arrived}）；" +
                $"悬停写来源“{want}”（{hover?.Replace("\n", " / ")}）；对账日志记了 {site.SelfDefenseEngagements - engagements0} 次开始自卫" +
                Why((engaged, "没有进入自卫交战"), (hp < 60f, "敌人没掉血"), (kept, "命令被改"), (arrived, "没走到"), (hover != null && hover.Contains(want), "悬停没写来源")));

            // 被接入的不交给教义：接入标志一亮，对账就清掉标记，内核当步停火。
            site.Kernel.SetFlag(unit, CombatUnitFlags.Possessed, true);
            bool can = site.SyncDoctrineSelfDefense(id);
            bool cleared = site.Kernel.TryGetSiegeUnit(unit, out CombatSiegeUnit su) && su.SelfDefense == 0;
            site.Kernel.SetFlag(unit, CombatUnitFlags.Possessed, false);
            bool back = site.SyncDoctrineSelfDefense(id);
            Expect(!can && cleared && back, $"被接入（直控）的机器不交给教义：共用判定返回 {can}、内核标记清零（{cleared}）；释放后恢复（{back}）");
        }

        /// <summary>施工中的机器（玩家给搬运底盘装了机枪）：在工地原地开火，工单不中断、位置不变、进度照涨。</summary>
        private static void CheckSdHomeBuild()
        {
            HomeValleyController home = SdHome(9102, out CampaignState s, out GridCell center);
            if (home == null)
            {
                return;
            }
            CombatSite site = home.Combat;
            s.Scrap = 500;
            const string bp = "bp_emgrcmd01_armed_hauler";
            s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
            {
                BlueprintId = bp, DisplayName = bp, ActiveVersion = 1,
                Versions = new[] { new BlueprintVersionRecord { Version = 1, ChassisId = HomeValleyLayout.Erc002ChassisId, PrimaryId = ComponentCatalog.CompGunId, OrderedFirmwareIds = Array.Empty<string>() } },
            }).ToArray();
            foreach (MachineRecord other in MachineRegistry.AllRecords.Where(m => m != null).ToList())
            {
                MachineRegistry.TrySetWorkPriority(other.LogicId, WorkOrderKind.Build, 0); // 只让这一台施工
            }
            GridCell core = HomeGridService.CorePivot(s);
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, bp, HomeValleyLayout.RegionId, new Vector2(core.X - 6, core.Y - 6), 100f, 100f);
            if (!r.Success)
            {
                Fail("施工自卫前提：登记带枪搬运机失败：" + r.Message);
                return;
            }
            int id = r.LogicId;
            MachineLoadoutRegistry.Register(s, id, bp, 1);
            WorldSimulation.StepMany(2);
            GridCell? spot = null;
            for (int rr = 0; rr <= 14 && spot == null; rr++)
            {
                for (int dy = -rr; dy <= rr && spot == null; dy++)
                {
                    for (int dx = -rr; dx <= rr && spot == null; dx++)
                    {
                        var c = new GridCell(core.X - 8 + dx, core.Y + 22 + dy);
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == rr && HomeGridService.ValidatePlacement(s, "generator_2", c, 0, checkCost: false).Ok)
                        {
                            spot = c;
                        }
                    }
                }
            }
            if (spot == null)
            {
                Fail("施工自卫前提：核心附近找不到能放 generator_2 的格");
                return;
            }
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", spot.Value, 0);
            WorkOrderRecord order = placed.Success ? HomeValleyWorkOrders.FindActiveBuild(s, placed.BuildingId) : null;
            int unit = SdUnit(site, id);
            bool working = order != null && StepUntilSd(() => order.AssignedMachineLogicId == id && order.State == WorkOrderState.InProgress && order.Progress > 0f
                                                                && !site.TryGetCommand(unit, out _), GameClock.StepHz * 180);
            if (!working)
            {
                Fail($"施工自卫前提：带枪搬运机没在工地开工（放置 {placed.Describe()}，工单 {order?.State} 指派 {order?.AssignedMachineLogicId} 进度 {order?.Progress:0.##}，能开火 {site.CanMachineAttack(id)}）");
                return;
            }
            site.TryGetMachinePosition(id, out Vector2 p0);
            float prog0 = order.Progress;
            string orderId = order.WorkOrderId;
            int foe = HomeHostile(home, p0 + new Vector2(3f, 0f), 5000f);
            double moved = 0;
            bool engaged = false;
            for (int step = 0; step < GameClock.StepHz * 3; step++)
            {
                WorldSimulation.StepMany(1);
                site.TryGetMachinePosition(id, out Vector2 p);
                moved = Math.Max(moved, Vector2.Distance(p, p0));
                engaged |= site.IsSelfDefending(unit);
            }
            WorkOrderRecord now = HomeValleyWorkOrders.FindActiveBuild(s, placed.BuildingId);
            float hp = site.Kernel.TryGetUnit(foe, out CombatUnitView fv) ? fv.Health : 0f;
            bool same = (now == null && order.State == WorkOrderState.Completed) || (now != null && now.WorkOrderId == orderId && now.AssignedMachineLogicId == id && now.State == WorkOrderState.InProgress);
            Expect(engaged && hp < 5000f && moved < 1e-3 && same && order.Progress > prog0 && !site.TryGetCommand(unit, out _),
                $"施工中的机器（搬运底盘装机枪，能开火 {site.CanMachineAttack(id)}）：工地旁 3 格的敌人原地开火（掉到 {hp:0.#}），3 秒位置不变（最大位移 {moved:0.####} 格）、" +
                $"工单不中断（{same}，{orderId}）、进度照涨（{prog0:0.##} → {order.Progress:0.##}）、没有被加上命令");
        }

        /// <summary>覆盖外 / 静默夜：教义是本地规则，照样原地自卫（FGR-SIG-053、FGR-ENV-011“所有机器只跑本地常规固件”）。</summary>
        private static void CheckSdHomeSignal()
        {
            foreach (bool night in new[] { false, true })
            {
                HomeValleyController home = SdHome(night ? 9104 : 9103, out CampaignState s, out GridCell center);
                if (home == null)
                {
                    return;
                }
                CombatSite site = home.Combat;
                bool installedHere = night && !SilentNightService.Installed;
                if (installedHere)
                {
                    SilentNightService.Install(); // 正式入口 GameRoot.Startup 接上的那一步（批处理自检里没有）
                }
                if (night)
                {
                    SilentNightService.ScheduleForTests(s, 1.0, 120.0, raid: false);
                    StepUntilSd(SilentNightService.IsActiveNow, GameClock.StepHz * 10);
                }
                else
                {
                    SignalCoverageService.OverrideForTests = (_, _) => new SignalCoverageSample(false, false, 0f, Vector2.zero, 0f, SignalCoverageSourceKind.None);
                }
                int id = SpawnHomeMachine(s, new Vector2(center.X, center.Y), combat: true);
                int foe = HomeHostile(home, new Vector2(center.X + 4, center.Y), 5000f);
                WorldSimulation.StepMany(GameClock.StepHz * 3);
                float hp = site.Kernel.TryGetUnit(foe, out CombatUnitView fv) ? fv.Health : 0f;
                bool cond = night ? SilentNightService.IsActiveNow() : !SignalCoverageService.Sample(HomeValleyLayout.RegionId, new Vector2(center.X, center.Y)).Covered;
                if (!night)
                {
                    SignalCoverageService.OverrideForTests = (_, _) => new SignalCoverageSample(true, true, float.PositiveInfinity, Vector2.zero, 0f, SignalCoverageSourceKind.None);
                }
                if (installedHere)
                {
                    SilentNightService.ResetForTests();
                }
                Expect(cond && hp < 5000f && site.IsSelfDefending(SdUnit(site, id)),
                    $"{(night ? "静默夜期间（接入与远程命令不可用）" : "信号覆盖外（收不到远程命令）")}：教义是本地规则，空闲机器对 4 格外的敌人照样原地自卫（掉到 {hp:0.#}，前提成立 {cond}）");
            }
        }

        /// <summary>远征队行军途中（执行“前进”与“返回”）：路边的敌人进射程原地还手，仍按行军命令走到远征地点 / 走回家园。</summary>
        private static void CheckSdHomeExpedition()
        {
            SdExpedition(9105, false);
            SdExpedition(9107, true); // 修复轮 1（审查 P2）：卡片②“前进 / 返回”——返程同样实跑
        }

        private static void SdExpedition(int seed, bool returnLeg)
        {
            CampaignState s = NewCampaign(seed);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            GridCell core = HomeGridService.CorePivot(s);
            int a = SpawnHomeMachine(s, new Vector2(core.X + 4, core.Y - 6), combat: true);
            ExpeditionSiteAnchors.EnsureResolved(s);
            FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId), "fgsd_relay");
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"远征自卫前提：出发失败（{dep.Outcome}：{string.Join("；", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            CombatSite site = CombatSites.Get(HomeValleyLayout.RegionId);
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            WorldSimulation.StepMany(GameClock.StepHz * (returnLeg ? 20 : 3));
            if (returnLeg && !ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Return, out string why))
            {
                Fail($"远征返程自卫前提：“返回家园”被拒（{why}）");
                return;
            }
            site.TryGetMachinePosition(a, out Vector2 pa);
            Vector2 goal = returnLeg && ExpeditionMarchService.TryGetGoal(now, out Vector2 g) ? g : anchor;
            Vector2 dir = (goal - pa).normalized;
            int foe = HomeHostile(home, pa + dir * 10f + new Vector2(-dir.y, dir.x) * 3f, 5000f); // 前方 10 格、偏 3 格
            int unit = SdUnit(site, a);
            bool marching = true;
            bool engaged = false;
            bool atSite = false;
            for (int step = 0; step < 24000 && !atSite; step += 10)
            {
                WorldSimulation.StepMany(10);
                ExpeditionMarchState m = ExpeditionMarchService.Current(CampaignSession.Current);
                atSite = m.Phase == (returnLeg ? ExpeditionMarchService.PhaseReturned : ExpeditionMarchService.PhaseAtSite);
                if (!atSite && site.IsUnitAlive(unit))
                {
                    engaged |= site.IsSelfDefending(unit);
                    bool has = site.TryGetCommand(unit, out CombatCommand c) && c.Kind != CombatCommandKind.None;
                    marching &= !has || c.Kind == CombatCommandKind.Move; // 行军命令不被换成攻击
                }
            }
            float hp = site.Kernel.TryGetUnit(foe, out CombatUnitView fv) ? fv.Health : 0f;
            Expect(engaged && hp < 5000f && marching && atSite,
                $"远征队{(returnLeg ? "返程" : "行军")}途中（执行“{(returnLeg ? "返回" : "前进")}”）：前方路边的敌人进射程原地还手（掉到 {hp:0.#}），命令始终是移动（{marching}），" +
                $"仍走到{(returnLeg ? "家园（已返回）" : "远征地点")}（{atSite}）");
        }

        /// <summary>交战中存档：读档无头跑、逐帧观察、0.5x / 2x / 3x、暂停结果一致（标记随档）。</summary>
        private static void CheckSdHomeMatrix()
        {
            HomeValleyController home = SdHome(9106, out CampaignState s, out GridCell center);
            if (home == null)
            {
                return;
            }
            CombatSite site = home.Combat;
            int id = SpawnHomeMachine(s, new Vector2(center.X - 14, center.Y), combat: true);
            HomeHostile(home, new Vector2(center.X, center.Y + 4), 400f);
            WorldSimulation.StepMany(GameClock.StepHz);
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(center.X + 14, center.Y), paused: false);
            int unit = SdUnit(site, id);
            bool engaged = StepUntilSd(() => site.IsSelfDefending(unit), GameClock.StepHz * 20);
            byte bits = site.Kernel.TryGetSiegeUnit(unit, out CombatSiegeUnit su0) ? su0.SelfDefense : (byte)0;
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("教义自卫存档写入失败：" + r.Message);
                return;
            }
            const int steps = 900;
            RestoreSave(Slot);
            CombatSite loaded = CombatSites.Get(HomeValleyLayout.RegionId);
            byte bitsLoaded = loaded.Kernel.TryGetSiegeUnit(SdUnit(loaded, id), out CombatSiegeUnit su1) ? su1.SelfDefense : (byte)0;
            long target = GameClock.Ticks + steps;
            WorldSimulation.StepMany(steps);
            string headless = Digest(CampaignSession.Current);
            RestoreSave(Slot);
            var reader = new SilentReader();
            InputRouter.DebugSetReader(reader);
            int frame = 0;
            while (GameClock.Ticks < target && frame < 5000)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frame++;
            }
            InputRouter.DebugSetReader(null);
            string observed = Digest(CampaignSession.Current);
            var bySpeed = new List<string>();
            foreach (float speed in new[] { 0.5f, 2f, 3f })
            {
                RestoreSave(Slot);
                GameClock.SetSpeed(speed);
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                bySpeed.Add(Digest(CampaignSession.Current));
            }
            RestoreSave(Slot);
            string beforePause = Digest(CampaignSession.Current);
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            string paused = Digest(CampaignSession.Current);
            GameClock.SetPaused(false);
            Expect(engaged && bits == (CombatSiegeConst.SelfDefenseOn | CombatSiegeConst.SelfDefenseEngaged) && bitsLoaded == bits && observed == headless
                   && bySpeed.All(x => x == headless) && beforePause == paused,
                $"移动途中自卫交战时存档（标记 {bits} → 读档 {bitsLoaded}）：读档无头跑 {steps} 步、逐帧观察、0.5x / 2x / 3x 跑到同一步结果逐字段一致；暂停 2 秒真实时间不走步、不开火" +
                (observed == headless ? string.Empty : "\n    无头=" + headless + "\n    观察=" + observed));
        }

        private static bool StepUntilSd(Func<bool> done, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i += 5)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(5);
            }
            return done();
        }
    }
}
