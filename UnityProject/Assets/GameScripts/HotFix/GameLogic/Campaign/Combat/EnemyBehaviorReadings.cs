using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Content;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG9-DATA-01（FGR-FAC-001 / IC-REQ-012）：敌人“行为读法”接口——表 fg.TbMechEnemy.behavior 的每个取值对应一个读法，
    /// 读法决定内核行为（<see cref="CombatBehavior"/>）、需要表里哪些参数（缺了就是“表里缺字段”）、以及把表行翻译成内核行为参数。
    /// 读法只读表行，不认识任何敌人 ID；新敌人选一个读法、填好参数就能生成。新增读法 = 新类 + 在 <see cref="EnemyBehaviorReadings"/> 注册，
    /// 并在 tools/cell_tables/fgdata_enemy.py 的 BEHAVIORS / BEHAVIOR_REQUIRES 登记（自检对照两边一致）。
    /// </summary>
    public interface IEnemyBehaviorReading
    {
        /// <summary>表里的读法键（fg.TbMechEnemy.behavior）。</summary>
        string Key { get; }

        /// <summary>对应的战斗内核行为。</summary>
        CombatBehavior Kernel { get; }

        /// <summary>这个读法必须 &gt; 0 的表列名（与 fgdata_enemy.BEHAVIOR_REQUIRES 一致）。</summary>
        IReadOnlyList<string> RequiredColumns { get; }

        /// <summary>需要内核行为参数（巡逻 / 后撤 / 周期 / 效果）时返回 true，并由 <see cref="FillProfile"/> 填。</summary>
        bool NeedsProfile { get; }

        void FillProfile(MechEnemy row, ref CombatBehaviorProfile profile);

        /// <summary>检查表行；缺字段把列名加进 <paramref name="problems"/>。</summary>
        void Validate(MechEnemy row, List<string> problems);
    }

    /// <summary>读法的公共实现：按列名取值、按 <see cref="RequiredColumns"/> 校验。</summary>
    public abstract class EnemyBehaviorReading : IEnemyBehaviorReading
    {
        public abstract string Key { get; }
        public abstract CombatBehavior Kernel { get; }
        public abstract IReadOnlyList<string> RequiredColumns { get; }
        public virtual bool NeedsProfile => false;

        public virtual void FillProfile(MechEnemy row, ref CombatBehaviorProfile profile)
        {
        }

        public void Validate(MechEnemy row, List<string> problems)
        {
            foreach (string col in RequiredColumns)
            {
                if (!(Column(row, col) > 0f))
                {
                    problems.Add(col);
                }
            }
        }

        /// <summary>按表列名取数值列（只覆盖读法会用到的列）。</summary>
        public static float Column(MechEnemy row, string col)
        {
            switch (col)
            {
                case "speed": return row.Speed;
                case "weaponRange": return row.WeaponRange;
                case "weaponDamage": return row.WeaponDamage;
                case "weaponCooldown": return row.WeaponCooldown;
                case "weaponAim": return row.WeaponAim;
                case "senseRange": return row.SenseRange;
                case "cycleSeconds": return row.CycleSeconds;
                case "effectSeconds": return row.EffectSeconds;
                case "effectRange": return row.EffectRange;
                case "effectAmount": return row.EffectAmount;
                case "fleeTrigger": return row.FleeTrigger;
                case "leash": return row.Leash;
                case "patrolRadius": return row.PatrolRadius;
                case "patrolFreq": return row.PatrolFreq;
                default: throw new ArgumentException("EnemyBehaviorReading.Column：未知列 " + col);
            }
        }
    }

    /// <summary>不动、不开火（首领的供能节点）。</summary>
    public sealed class StillReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorNone;
        public override CombatBehavior Kernel => CombatBehavior.None;
        public override IReadOnlyList<string> RequiredColumns { get; } = Array.Empty<string>();
    }

    /// <summary>巡逻标记：受威胁后撤、出生点附近摆动巡逻、按周期标记视线内最近的机器（静默侦察机）。</summary>
    public sealed class PatrolMarkReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorPatrolMark;
        public override CombatBehavior Kernel => CombatBehavior.Scout;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "speed", "senseRange", "cycleSeconds", "effectSeconds", "leash" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.Speed = row.Speed;
            p.FleeTrigger = row.FleeTrigger;
            p.Leash = row.Leash;
            p.PatrolRadius = row.PatrolRadius;
            p.PatrolFreq = row.PatrolFreq;
            p.SenseRange = row.SenseRange;
            p.CycleSeconds = row.CycleSeconds;
            p.EffectSeconds = row.EffectSeconds;
        }
    }

    /// <summary>干扰场：清除半径内的标记 + 驻守开火；信号干扰场由 <see cref="EnemyJamFields"/> 按同一半径判定（静默干扰机）。</summary>
    public sealed class JamFieldReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorJamField;
        public override CombatBehavior Kernel => CombatBehavior.Jammer;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "weaponRange", "weaponDamage", "weaponCooldown", "effectRange" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.EffectRange = row.EffectRange;
        }
    }

    /// <summary>驻守开火：冷却到点打射程内最近的可见目标（铸造护甲机、主核心）。</summary>
    public sealed class HoldFireReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorHoldFire;
        public override CombatBehavior Kernel => CombatBehavior.HoldFire;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "weaponRange", "weaponDamage", "weaponCooldown" };
    }

    /// <summary>瞄准线两段式：先瞄准 weaponAim 秒再开火（铸造步进炮）。</summary>
    public sealed class TelegraphReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorTelegraph;
        public override CombatBehavior Kernel => CombatBehavior.Telegraph;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "weaponRange", "weaponDamage", "weaponCooldown", "weaponAim" };
    }

    /// <summary>维修：受威胁后撤，否则走向血量百分比最低的友军，进入范围按周期治疗（铸造维修机）。</summary>
    public sealed class RepairReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorRepair;
        public override CombatBehavior Kernel => CombatBehavior.Repair;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "speed", "effectRange", "effectAmount", "cycleSeconds", "leash" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.Speed = row.Speed;
            p.FleeTrigger = row.FleeTrigger;
            p.Leash = row.Leash;
            p.EffectRange = row.EffectRange;
            p.EffectAmount = row.EffectAmount;
            p.CycleSeconds = row.CycleSeconds;
        }
    }

    /// <summary>读法注册表（键 = fg.TbMechEnemy.behavior）。</summary>
    public static class EnemyBehaviorReadings
    {
        private static readonly Dictionary<string, IEnemyBehaviorReading> Map = Build(
            new StillReading(), new PatrolMarkReading(), new JamFieldReading(), new HoldFireReading(), new TelegraphReading(), new RepairReading());

        private static Dictionary<string, IEnemyBehaviorReading> Build(params IEnemyBehaviorReading[] readings)
        {
            var map = new Dictionary<string, IEnemyBehaviorReading>(StringComparer.Ordinal);
            foreach (IEnemyBehaviorReading r in readings)
            {
                map.Add(r.Key, r);
            }
            return map;
        }

        public static IEnumerable<IEnemyBehaviorReading> All => Map.Values;

        public static bool TryGet(string key, out IEnemyBehaviorReading reading)
        {
            reading = null;
            return key != null && Map.TryGetValue(key, out reading);
        }
    }
}
