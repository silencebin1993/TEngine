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
                // FG9-FAC-02
                case "salvoCount": return row.SalvoCount;
                case "salvoGap": return row.SalvoGap;
                case "chargeSpeed": return row.ChargeSpeed;
                case "turnRate": return row.TurnRate;
                case "maxPlaced": return row.MaxPlaced;
                // FG10-FAC-01
                case "sprayCone": return row.SprayCone;
                case "poolRadius": return row.PoolRadius;
                case "poolSeconds": return row.PoolSeconds;
                case "poolDps": return row.PoolDps;
                case "chill": return row.Chill;
                case "statusSeconds": return row.StatusSeconds;
                case "deflagMult": return row.DeflagMult;
                case "splitCount": return row.SplitCount;
                case "splitHp": return row.SplitHp;
                case "splitSpeed": return row.SplitSpeed;
                case "splitLife": return row.SplitLife;
                case "splitDps": return row.SplitDps;
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

    /// <summary>FG9-FAC-02（FGR-FAC-008“齐射弹幕”）：对空高炮 / 空爆炮台——装填 → 装填完成灯（预警）→ 一轮多发；标签 anti_air = 只打空中目标，airburst = 在目标处空爆。</summary>
    public sealed class SalvoReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorSalvo;
        public override CombatBehavior Kernel => CombatBehavior.Salvo;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "weaponRange", "weaponDamage", "weaponCooldown", "cycleSeconds", "warnSeconds", "salvoCount", "salvoGap" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p) => EnemyFoundryParams.Apply(row, ref p);
    }

    /// <summary>FG9-FAC-02：布雷车——在牵引中心附近巡逻、受威胁后撤，每 cycleSeconds 秒铺一颗子母雷（effectRange / effectAmount / warnSeconds / maxPlaced）。</summary>
    public sealed class MinelayReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorMinelay;
        public override CombatBehavior Kernel => CombatBehavior.MineLayer;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "leash", "cycleSeconds", "effectRange", "effectAmount", "warnSeconds", "maxPlaced" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p) => EnemyFoundryParams.Apply(row, ref p);
    }

    /// <summary>FG9-FAC-02：装填机——effectRange 内友军火炮装填额外加快 effectAmount 倍；受威胁后撤、否则回到牵引中心。</summary>
    public sealed class ReloadReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorReload;
        public override CombatBehavior Kernel => CombatBehavior.Loader;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "speed", "leash", "effectRange", "effectAmount" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p) => EnemyFoundryParams.Apply(row, ref p);
    }

    /// <summary>FG9-FAC-02（FGR-FAC-008“正面推进”）：攻城重甲——转向受限、只沿正面推进；目标在正前方时蓄力 warnSeconds、以 chargeSpeed 冲锋 effectSeconds 秒（撞击 = 武器伤害、击退 effectAmount 米）、冷却 cycleSeconds；贴身用武器。</summary>
    public sealed class ChargeReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorCharge;
        public override CombatBehavior Kernel => CombatBehavior.Charger;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "cycleSeconds", "effectSeconds", "warnSeconds", "chargeSpeed", "turnRate", "weaponRange", "weaponDamage", "weaponCooldown" };
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p) => EnemyFoundryParams.Apply(row, ref p);
    }

    /// <summary>
    /// FG9-FAC-02：把敌人表里的铸造专属语法参数翻译成内核行为参数（齐射 / 布雷 / 装填 / 冲锋 / 架炮驻锄 / 盲区）。按列是否 &gt; 0 决定，不认识任何敌人 ID（IC-REQ-012）：
    /// 任何读法都可以叠加架炮驻锄（deploySeconds）、盲区（minRange）、齐射（salvoCount，首领的对空形态也读它）、布雷（maxPlaced）。
    /// 区域生成（<see cref="EnemySpawnBuilder"/>）与攻城单位生成（SiegeService.SpawnOne）共用。
    /// </summary>
    public static class EnemyFoundryParams
    {
        public static bool Has(MechEnemy row) =>
            row != null && (row.SalvoCount > 0f || row.MaxPlaced > 0f || row.DeploySeconds > 0f || row.MinRange > 0f
                            || row.Behavior == EnemyDefs.BehaviorReload || row.Behavior == EnemyDefs.BehaviorCharge);

        public static void Apply(MechEnemy row, ref CombatBehaviorProfile p)
        {
            if (row == null)
            {
                return;
            }
            if (row.SalvoCount > 0f)
            {
                p.SalvoCount = row.SalvoCount;
                p.SalvoGap = row.SalvoGap;
                p.SalvoReload = row.CycleSeconds;
                p.SalvoWarn = row.WarnSeconds;
                bool burst = EnemyDefs.HasTag(row, EnemyDefs.TagAirburst);
                p.SalvoRadius = burst ? row.EffectRange : 0f;
                p.SalvoMode = burst ? 2f : 1f;
            }
            if (row.MaxPlaced > 0f)
            {
                p.MineMax = row.MaxPlaced;
                p.MineCycle = row.CycleSeconds;
                p.MineRadius = row.EffectRange;
                p.MineDamage = row.EffectAmount;
                p.MineFuse = row.WarnSeconds;
                p.MineLife = MineLifeSeconds;
            }
            if (row.Behavior == EnemyDefs.BehaviorMinelay)
            {
                p.Speed = row.Speed;
                p.Leash = row.Leash;
                p.FleeTrigger = row.FleeTrigger;
                p.PatrolRadius = row.PatrolRadius;
                p.PatrolFreq = row.PatrolFreq;
            }
            if (row.Behavior == EnemyDefs.BehaviorReload)
            {
                p.Speed = row.Speed;
                p.Leash = row.Leash;
                p.FleeTrigger = row.FleeTrigger;
                p.ReloadRange = row.EffectRange;
                p.ReloadRate = row.EffectAmount;
            }
            if (row.Behavior == EnemyDefs.BehaviorCharge)
            {
                p.Speed = row.Speed;
                p.SenseRange = row.SenseRange;
                p.Leash = row.Leash;
                p.TurnRate = row.TurnRate;
                p.ChargeSpeed = row.ChargeSpeed;
                p.ChargeWarn = row.WarnSeconds;
                p.ChargeSeconds = row.EffectSeconds;
                p.ChargeCooldown = row.CycleSeconds;
                p.ChargeKnock = row.EffectAmount;
            }
            p.DeploySeconds = row.DeploySeconds;
            p.MinRange = row.MinRange;
        }

        /// <summary>子母雷存活秒数（fg.TbHomeTuning foundry.mine.life_seconds）。</summary>
        public static float MineLifeSeconds => Grid.GridContent.TryGetTuning("foundry.mine.life_seconds", out float v) && v > 0f ? v : 90f;
    }

    /// <summary>
    /// FG10-FAC-01（FGR-FAC-008 澄净专属语法）的五个读法共用：内核行为都是“突袭者”式追击（感知范围内找目标、逼近到武器射程），
    /// 专属攻击（喷淋 / 自爆 / 枢轴喷灌 / 投放）由 <see cref="EnemyClarityParams"/> 翻译成内核行为参数，在内核澄净阶段结算——武器三列是那一下本身，普通开火路径不出手。
    /// </summary>
    public abstract class ClarityChaseReading : EnemyBehaviorReading
    {
        public override CombatBehavior Kernel => CombatBehavior.Raider;
        public override bool NeedsProfile => true;

        public override void FillProfile(MechEnemy row, ref CombatBehaviorProfile p)
        {
            p.Speed = row.Speed;
            p.SenseRange = row.SenseRange;
        }
    }

    /// <summary>喷淋布区：喷嘴蓄压 warnSeconds → 锥形（weaponRange × sprayCone）喷出，命中挂 statusTag / 受寒，落地留池（喷洒车 / 液氮泵车）。</summary>
    public sealed class SprayReading : ClarityChaseReading
    {
        public override string Key => EnemyDefs.BehaviorSpray;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "weaponRange", "weaponDamage", "weaponCooldown", "warnSeconds", "sprayCone" };
    }

    /// <summary>自爆与油迹：进入触发距离 weaponRange → 引信 warnSeconds → 爆炸（半径 effectRange、伤害 weaponDamage）；一路留油迹；被火击毁爆燃（油污爬行者）。</summary>
    public sealed class CrawlBurstReading : ClarityChaseReading
    {
        public override string Key => EnemyDefs.BehaviorCrawlBurst;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "weaponRange", "weaponDamage", "weaponCooldown", "warnSeconds", "effectRange" };
    }

    /// <summary>枢轴喷灌：逼近到 weaponRange，蓄压 warnSeconds 后喷淋臂（臂长 effectRange、每秒伤害 effectAmount、转速 turnRate、半宽 sprayCone）绕本体扫（沉淀巨罐）。</summary>
    public sealed class PivotReading : ClarityChaseReading
    {
        public override string Key => EnemyDefs.BehaviorPivot;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "weaponRange", "weaponDamage", "weaponCooldown", "warnSeconds", "effectRange", "effectAmount", "turnRate", "sprayCone" };
    }

    /// <summary>FG10-BOSS-01 固定环形喷淋架：本体原地不动、不开火（内核 HoldFire），喷淋臂（臂长 effectRange、每秒伤害 effectAmount、转速 turnRate、半宽 sprayCone、
    /// 蓄压 warnSeconds）绕本体扫——与枢轴喷灌同一套内核结算（洗礼塔）。</summary>
    public sealed class RingSprayReading : EnemyBehaviorReading
    {
        public override string Key => EnemyDefs.BehaviorRingSpray;
        public override CombatBehavior Kernel => CombatBehavior.HoldFire;
        public override bool NeedsProfile => true;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "warnSeconds", "effectRange", "effectAmount", "turnRate", "sprayCone" };
    }

    /// <summary>投放（空中）：有投放目标时直飞过去，effectRange 内悬停预警 warnSeconds 后投放并自毁；没有目标时用武器扑向感知范围内的单位（灭活无人机）。</summary>
    public sealed class PolluteReading : ClarityChaseReading
    {
        public override string Key => EnemyDefs.BehaviorPollute;
        public override IReadOnlyList<string> RequiredColumns { get; } =
            new[] { "speed", "senseRange", "weaponRange", "weaponDamage", "weaponCooldown", "warnSeconds", "effectRange" };
    }

    /// <summary>集群：追击感知范围内的目标，贴近用武器腐蚀；anti_building 标签优先打建筑（腐蚀蜂群）。</summary>
    public sealed class SwarmReading : ClarityChaseReading
    {
        public override string Key => EnemyDefs.BehaviorSwarm;
        public override IReadOnlyList<string> RequiredColumns { get; } = new[] { "speed", "senseRange", "weaponRange", "weaponDamage", "weaponCooldown" };
    }

    /// <summary>
    /// FG10-FAC-01：把敌人表里的澄净专属语法参数翻译成内核行为参数（喷淋布区 / 枢轴喷灌 / 自爆与油迹 / 分裂 / 飞行与投放 / 抗性 / 建筑偏好）。
    /// 按读法与列是否 &gt; 0 决定，不认识任何敌人 ID（IC-REQ-012）；区域生成（<see cref="EnemySpawnBuilder"/>）与攻城单位生成（SiegeService.SpawnOne）共用，
    /// 伤害类参数乘调用方给的伤害倍率（难度 / 精英），分裂液耐久乘耐久倍率（与本体同一口径）。<paramref name="insulate"/> = 这次生成挂绝缘涂层（带 adapt_insulate 的单位挂不上电击类标签）。
    /// </summary>
    public static class EnemyClarityParams
    {
        /// <summary>“电击类标签”（绝缘涂层挡掉的状态标签）。</summary>
        public static readonly string[] InsulateTags = { "Shock", "Charged" };

        public static bool Has(MechEnemy row) =>
            row != null && (row.Behavior == EnemyDefs.BehaviorSpray || row.Behavior == EnemyDefs.BehaviorCrawlBurst || row.Behavior == EnemyDefs.BehaviorPivot
                            || row.Behavior == EnemyDefs.BehaviorRingSpray
                            || row.Behavior == EnemyDefs.BehaviorPollute || row.Behavior == EnemyDefs.BehaviorSwarm || row.SplitCount > 0f
                            || EnemyDefs.HasTag(row, EnemyDefs.TagAirborne) || EnemyDefs.HasTag(row, EnemyDefs.TagAcidProof)
                            || EnemyDefs.HasTag(row, EnemyDefs.TagAntiBuilding));

        /// <summary>这一行在“绝缘涂层”那天会不会挂绝缘（带 adapt_insulate 标签）。</summary>
        public static bool Insulates(MechEnemy row) => EnemyDefs.HasTag(row, EnemyDefs.TagAdaptInsulate);

        public static uint InsulateMask()
        {
            uint m = 0u;
            foreach (string t in InsulateTags)
            {
                m |= NamedReactionCatalog.BitOf(t);
            }
            return m;
        }

        public static void Apply(MechEnemy row, ref CombatBehaviorProfile p, float dmgMul, bool insulate, float hpMul = 1f)
        {
            if (row == null)
            {
                return;
            }
            float dm = Math.Max(0.05f, dmgMul);
            uint status = NamedReactionCatalog.BitOf(row.StatusTag);
            if (row.Behavior == EnemyDefs.BehaviorSpray)
            {
                p.SprayRange = row.WeaponRange;
                p.SprayCone = row.SprayCone;
                p.SprayWarn = Math.Max(0.8f, row.WarnSeconds);
                p.SprayCycle = row.WeaponCooldown;
                p.SprayDamage = row.WeaponDamage * dm;
                p.SprayChill = Math.Max(0f, row.Chill);
                p.SprayStatus = status;
                p.SprayStatusSeconds = row.StatusSeconds;
                p.PoolRadius = row.PoolRadius;
                p.PoolSeconds = row.PoolSeconds;
                p.PoolDps = row.PoolDps * dm;
                p.PoolChill = Math.Max(0f, row.Chill) * PoolChillFrac;
            }
            else if (row.Behavior == EnemyDefs.BehaviorPivot || row.Behavior == EnemyDefs.BehaviorRingSpray)
            {
                p.PivotRadius = row.EffectRange;
                p.PivotWidth = row.SprayCone * 2f; // 表 sprayCone 是半宽，内核 PivotWidth 是全宽（审查修复）
                p.PivotSpeed = row.TurnRate;
                p.PivotWarn = Math.Max(0.8f, row.WarnSeconds);
                p.PivotDps = row.EffectAmount * dm;
                p.SprayStatus = status;
                p.SprayStatusSeconds = row.StatusSeconds;
            }
            else if (row.Behavior == EnemyDefs.BehaviorCrawlBurst)
            {
                p.BurstRadius = row.EffectRange;
                p.BurstDamage = row.WeaponDamage * dm;
                p.BurstFuse = Math.Max(0.8f, row.WarnSeconds);
                p.BurstTrigger = row.WeaponRange;
                p.DeflagMult = Math.Max(0f, row.DeflagMult);
                p.TrailInterval = row.CycleSeconds;
                p.TrailRadius = row.PoolRadius;
                p.TrailSeconds = row.EffectSeconds;
                p.TrailStatus = status;
                p.TrailStatusSeconds = row.StatusSeconds; // 审查修复：表 statusSeconds 不再是死数据（油迹 / 溅油挂油的秒数）
            }
            else if (row.Behavior == EnemyDefs.BehaviorPollute)
            {
                p.PolluteRange = row.EffectRange;
                p.PolluteChannel = Math.Max(0.8f, row.WarnSeconds);
            }
            if (row.SplitCount > 0f)
            {
                p.SplitCount = row.SplitCount;
                p.SplitHp = row.SplitHp * Math.Max(0.05f, hpMul);
                p.SplitSpeed = row.SplitSpeed;
                p.SplitLife = row.SplitLife;
                p.SplitDps = row.SplitDps * dm;
                p.SplitRadius = row.PoolRadius > 0f ? row.PoolRadius : 1f;
            }
            if (EnemyDefs.HasTag(row, EnemyDefs.TagAirborne))
            {
                p.Airborne = 1f;
            }
            if (EnemyDefs.HasTag(row, EnemyDefs.TagAcidProof))
            {
                p.AcidResist = 1f;
                p.ImmuneMask |= NamedReactionCatalog.BitOf("Acid");
            }
            if (EnemyDefs.HasTag(row, EnemyDefs.TagAntiBuilding))
            {
                p.StructPref = 1f;
            }
            if (insulate && Insulates(row))
            {
                p.ImmuneMask |= InsulateMask();
            }
        }

        /// <summary>液氮池每节拍受寒 = 喷中受寒 × clarity.pool.chill_tick_frac。</summary>
        public static float PoolChillFrac => Grid.GridContent.TryGetTuning("clarity.pool.chill_tick_frac", out float v) && v > 0f ? v : 0.5f;
    }

    /// <summary>读法注册表（键 = fg.TbMechEnemy.behavior）。</summary>
    public static class EnemyBehaviorReadings
    {
        private static readonly Dictionary<string, IEnemyBehaviorReading> Map = Build(
            new StillReading(), new PatrolMarkReading(), new JamFieldReading(), new HoldFireReading(), new TelegraphReading(), new RepairReading(),
            new DeflectReading(), new StalkReading(), new SeverReading(),
            new SalvoReading(), new MinelayReading(), new ReloadReading(), new ChargeReading(),
            new SprayReading(), new CrawlBurstReading(), new PivotReading(), new PolluteReading(), new SwarmReading(), new RingSprayReading());

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
