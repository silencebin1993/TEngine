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
                case "warnSeconds": return row.WarnSeconds;
                case "lurkSeconds": return row.LurkSeconds;
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

    /// <summary>
    /// FG9-FAC-01（FGR-FAC-008“信号干扰 / 静默潜伏”之外的偏折）：偏折场——驻守开火 + 按 间隙(cycleSeconds) → 预警(warnSeconds) → 展开(effectSeconds) 循环的偏折场，
    /// 半径 effectRange，场内的己方射弹被反弹（伤害 × effectAmount 打回开火者）。磁暴棱柱；寂听主脑阶段二（展开开关由首领状态机控制）。
    /// </summary>
    public sealed class DeflectReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorDeflect;
        public override CombatBehavior Kernel => CombatBehavior.HoldFire;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "effectRange", "effectSeconds", "cycleSeconds", "effectAmount", "warnSeconds" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.DeflectRadius = row.EffectRange;
            p.DeflectWarn = row.WarnSeconds;
            p.DeflectUp = row.EffectSeconds;
            p.DeflectGap = row.CycleSeconds;
            p.DeflectReflect = row.EffectAmount;
        }
    }

    /// <summary>FG9-FAC-01（FGR-FAC-008“静默潜伏”）：伏击——在出生点潜伏，感知范围内出现机器（装有信号中继的优先）就现形预警、追击，瞄准线后出手（回声猎手）。</summary>
    public sealed class StalkReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorStalk;
        public override CombatBehavior Kernel => CombatBehavior.Stalker;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "leash", "weaponRange", "weaponDamage", "weaponCooldown", "weaponAim", "warnSeconds", "lurkSeconds" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.Speed = row.Speed;
            p.SenseRange = row.SenseRange;
            p.Leash = row.Leash;
        }
    }

    /// <summary>FG9-FAC-01（FGR-FAC-008“信号干扰”，寂听者“断链束”）：驻守开火 + 持续照射被接入的机器 effectSeconds 秒强制断链（射程 effectRange，冷却 cycleSeconds）。</summary>
    public sealed class SeverReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorSever;
        public override CombatBehavior Kernel => CombatBehavior.HoldFire;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "effectRange", "effectSeconds", "cycleSeconds", "weaponRange", "weaponDamage", "weaponCooldown" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.SeverRange = row.EffectRange;
            p.SeverSeconds = row.EffectSeconds;
            p.SeverCooldown = row.CycleSeconds;
        }
    }

    /// <summary>
    /// FG9-FAC-01：任何读法都可以叠加“静默潜伏”（表 lurkSeconds &gt; 0）：静止够久进入潜伏，现形预警 = warnSeconds，侦测距离 = revealRange。
    /// 与读法自己的参数合并进同一份内核行为参数（<see cref="EnemySpawnBuilder"/> 与攻城单位生成共用）。
    /// </summary>
    public static class EnemySilentParams
    {
        public static bool HasLurk(MechEnemy row) => row != null && row.LurkSeconds > 0f;

        public static void ApplyLurk(MechEnemy row, ref CombatBehaviorProfile p)
        {
            if (!HasLurk(row))
            {
                return;
            }
            p.LurkSeconds = row.LurkSeconds;
            p.RevealSeconds = row.WarnSeconds;
            p.RevealRange = row.RevealRange;
        }

        /// <summary>偏折场生成时就能运转（首领的偏折场由状态机按阶段打开，不在这里置位）。</summary>
        public static bool ArmsDeflect(MechEnemy row) =>
            row != null && row.Behavior == EnemyDefs.BehaviorDeflect && !EnemyDefs.HasTag(row, EnemyDefs.TagExternalHealth);
    }

    /// <summary>读法注册表（键 = fg.TbMechEnemy.behavior）。</summary>
    public static class EnemyBehaviorReadings
    {
        private static readonly Dictionary<string, IEnemyBehaviorReading> Map = Build(
            new StillReading(), new PatrolMarkReading(), new JamFieldReading(), new HoldFireReading(), new TelegraphReading(), new RepairReading(),
            new DeflectReading(), new StalkReading(), new SeverReading());

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
