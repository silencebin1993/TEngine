using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using Unity.Mathematics;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FW-02（FG19A 第 6 节 N09～N13 的条件型新固件；FG19A 第 1.8 节“固定用例先造出条件”）：在条件场景里跑真实编队攻击，给 FgEmgAddonFirmwareSelfCheck 用。
    /// 起真实系统：真实声明表（调用方覆盖 emg.stage）→ 真实蓝图编译（UplinkCompiler）→ 真实程序编译器 → 真实 CombatSite 读法 / 程序登记 / 飞行实体化 → 真实内核。
    /// 条件：阵亡 = 1 秒时按热更层机器阵亡的同一入口判死（外部血量单位 + Kernel.Kill(unit, 0)）；过热 / 冷却完成 = 开局积热贴着过热阈值；
    /// 反弹 / 穿透 = 程序最前面先放跳弹 / 贯穿（条件提供者，不参与首位 / 末位比较）+ 主目标旁一个敌方偏折单位。
    /// </summary>
    public static partial class FgReadingMatrixSelfCheck
    {
        internal enum AddonCond
        {
            Death,
            Heat,
            Bounce,
            Pierce,
        }

        internal sealed class AddonObs
        {
            public string Digest;
            /// <summary>触发器放出子载荷的次数（evt.split 入队数）。</summary>
            public long Splits;
            public long Bounces;
            public long Pierces;
            public long Deaths;
            public long Overheats;
            public long Cooleds;
            /// <summary>最后一次放出子载荷的位置。</summary>
            public double2 LastSplitPos;
            public bool HasSplit;
            /// <summary>攻击者判死 / 过热那一刻的位置。</summary>
            public double2 AttackerPosAtEvent;
            /// <summary>场上出现过的区域：(状态位掩码, 位置, 出现时离攻击者 1 米内)。</summary>
            public readonly List<(uint Mask, double2 Pos, bool AtAttacker)> Zones = new List<(uint, double2, bool)>();
            /// <summary>内核实际生成的子载荷份数（EmgBusStats.SubSpawned）。</summary>
            public long SubSpawned;
            /// <summary>判死之后新出现在阵亡处 1 米内的子载荷（弹体或子区）份数。</summary>
            public int SubsAtDeath;
            /// <summary>结束时攻击者积热。</summary>
            public float EndHeat;
        }

        /// <summary>按正式路径编出武器（接入口装 <paramref name="fws"/>）并在条件场景里跑；<paramref name="seconds"/> 游戏秒。</summary>
        internal static AddonObs RunAddonCondition(string comp, string[] fws, AddonCond cond, float seconds, float dt = Dt, string[] local = null)
        {
            using Arena a = BuildAddon(comp, fws, cond, out CombatWeapon w, local);
            return RunAddonArena(a, w, cond, seconds, dt);
        }

        private static Arena BuildAddon(string comp, string[] fws, AddonCond cond, out CombatWeapon w, string[] local = null)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, local ?? Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            Arena a = Arena.Build(w, prepared: true, null, program, comp, conditions: cond == AddonCond.Bounce || cond == AddonCond.Pierce);
            a.Trace = true;
            CombatKernel k = a.K;
            int me = a.Ids[ArenaAttacker];
            if (cond == AddonCond.Bounce)
            {
                // 主目标旁的敌方偏折单位（与 FgEmgBusSelfCheck 同一造法）：射向主目标的弹被偏折 = 世界源的 evt.bounce
                CombatSpawn prism = Hostile(new double2(2.6, -0.4), 5000f, 5000f);
                prism.BehaviorProfile = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f });
                int pid = k.Spawn(prism);
                k.SetFlag(pid, CombatUnitFlags.DeflectArmed, true);
                k.SetDeflectPhase(pid, 2, 60f);
            }
            if (cond == AddonCond.Death)
            {
                k.SetFlag(me, CombatUnitFlags.ExternalHealth, true); // 己方机器的血量在热更层（ADR-EMG-001 ⑦），判死走 CombatSite.OnMachineDied → Kernel.Kill(unit, 0)
            }
            if (cond == AddonCond.Heat && w.OverheatAt > 0f)
            {
                k.SetWeaponState(me, w.OverheatAt - 0.01f, false, 0, 0); // 下一发就过热；停火散热回落到阈值 × emg.heat.cool_ratio 以下 = 冷却完成
            }
            return a;
        }

        private static AddonObs RunAddonArena(Arena a, CombatWeapon w, AddonCond cond, float seconds, float dt)
        {
            CombatKernel k = a.K;
            int me = a.Ids[ArenaAttacker];
            var o = new AddonObs();
            var seen = new HashSet<(double, double, double)>();
            int steps = (int)math.round(seconds / dt);
            int killAt = (int)math.round(1f / dt);
            for (int i = 0; i < steps; i++)
            {
                if (cond == AddonCond.Death && i == killAt)
                {
                    k.TryGetUnit(me, out CombatUnitView mv);
                    o.AttackerPosAtEvent = mv.Position;
                    k.Kill(me, 0);
                }
                long subs = k.EmgBusStats.SubSpawned;
                k.Step(dt, k.Time + dt);
                k.TryGetUnit(me, out CombatUnitView hv);
                bool afterDeath = cond == AddonCond.Death && i >= killAt;
                bool newSubs = afterDeath && k.EmgBusStats.SubSpawned > subs;
                for (int z = 0; z < k.ZoneCount; z++)
                {
                    if (k.TryGetZone(z, out CombatZone zone) && seen.Add((zone.Born, zone.Pos.x, zone.Pos.y)))
                    {
                        o.Zones.Add((zone.StatusMask, zone.Pos, math.distance(zone.Pos, hv.Position) < 1.0));
                        if (newSubs && math.distance(zone.Pos, o.AttackerPosAtEvent) < 1.0)
                        {
                            o.SubsAtDeath++;
                        }
                    }
                }
                for (int pi = 0; pi < k.ProjectileCount; pi++)
                {
                    if (k.TryGetProjectile(pi, out CombatProjectile pr))
                    {
                        if (newSubs && math.distance(pr.Pos, o.AttackerPosAtEvent) < 1.0)
                        {
                            o.SubsAtDeath++;
                        }
                        a.TraceHash = (a.TraceHash ^ (ulong)(long)math.round(pr.Pos.x * 10)) * 1099511628211UL;
                        a.TraceHash = (a.TraceHash ^ (ulong)(long)math.round(pr.Pos.y * 10)) * 1099511628211UL;
                    }
                }
            }
            o.Splits = k.EmgBusStats.Splits;
            o.SubSpawned = k.EmgBusStats.SubSpawned;
            k.TryGetUnit(me, out CombatUnitView ev);
            o.EndHeat = ev.Heat;
            o.Bounces = k.EmgEnqueuedOf(EmgEventKind.Bounce);
            o.Pierces = k.EmgEnqueuedOf(EmgEventKind.Pierce);
            o.Deaths = k.EmgEnqueuedOf(EmgEventKind.Death);
            o.Overheats = k.EmgEnqueuedOf(EmgEventKind.Overheat);
            o.Cooleds = k.EmgEnqueuedOf(EmgEventKind.Cooled);
            if (k.TryGetLastEmgEvent(EmgEventKind.Split, out EmgEvent sp))
            {
                o.HasSplit = true;
                o.LastSplitPos = sp.Pos;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < ArenaCount; i++)
            {
                bool ok = k.TryGetUnit(a.Ids[i], out CombatUnitView v);
                sb.Append((ok ? v.Health : 0f).ToString("F1", CultureInfo.InvariantCulture)).Append(ok && v.Alive ? 'A' : 'D').Append('|');
            }
            sb.Append("S").Append(o.SubSpawned).Append("Z").Append(o.Zones.Count).Append("T").Append(a.TraceHash.ToString("X16"));
            foreach ((uint mask, double2 pos, bool _) in o.Zones)
            {
                sb.Append('[').Append(mask).Append('@').Append(pos.x.ToString("F1", CultureInfo.InvariantCulture)).Append(',').Append(pos.y.ToString("F1", CultureInfo.InvariantCulture)).Append(']');
            }
            o.Digest = sb.ToString();
            return o;
        }

        /// <summary>
        /// 存读档：条件场景跑 <paramref name="before"/> 秒后快照 → 新内核读档，两边再各跑 <paramref name="after"/> 秒，逐步比状态哈希与涌现哈希、区域数（“过热过”记号、在场驻留区都在快照里）。
        /// 返回 null = 一致，否则写差异。
        /// </summary>
        internal static string AddonSaveLoad(string comp, string[] fws, AddonCond cond, float before, float after, out long zonesAfter)
        {
            zonesAfter = 0;
            using Arena a = BuildAddon(comp, fws, cond, out CombatWeapon w);
            RunAddonArena(a, w, cond, before, Dt);
            byte[] snap = a.K.Serialize();
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var b = new CombatKernel(cfg, 32);
            CombatLoadResult lr = b.Load(snap);
            if (lr != CombatLoadResult.Ok)
            {
                return "读档失败 " + lr;
            }
            int steps = (int)math.round(after / Dt);
            for (int i = 0; i < steps; i++)
            {
                a.K.Step(Dt, a.K.Time + Dt);
                b.Step(Dt, b.Time + Dt);
                if (a.K.StateHash() != b.StateHash() || a.K.EmgStateHash() != b.EmgStateHash() || a.K.ZoneCount != b.ZoneCount)
                {
                    return $"续跑第 {i} 步分叉（区域 {a.K.ZoneCount} / {b.ZoneCount}）";
                }
            }
            zonesAfter = b.Counters.ZonesSpawned;
            return null;
        }

        /// <summary>五种载体各自用来跑矩阵的作战组件（与 C 段同一组）。</summary>
        internal static IReadOnlyList<(FirmwareCarrier Carrier, string Component)> AddonCarriers => CarrierComponents;

        /// <summary>零死对填充（与 C2 同一条：效果类、阶段 1 走声明轨）。</summary>
        internal const string AddonFiller = DeclFiller;
    }
}
