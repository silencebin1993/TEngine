using System.Collections.Generic;
using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Core
{
    // ─────────────────────────────────────────────────────────────
    // 局内信号定义。全部是 struct，零装箱。
    // 卡牌触发器（CardTriggerBus）订阅这些信号，把游戏事件路由到卡牌效果。
    // 新增一种触发时机 = 加一个 struct + 在产生点 Publish 一次。
    // ─────────────────────────────────────────────────────────────

    /// <summary>吞噬完成。细胞阶段的核心动词，最重要的触发点。</summary>
    public struct DevourSignal
    {
        public int UnitIndex;
        public float2 Position;
        /// <summary>被吞噬目标的体积。</summary>
        public float TargetVolume;
        public SimFaction TargetFaction;
        /// <summary>连吃层数（本次吞噬后的值）。</summary>
        public int ComboCount;
        /// <summary>是否是尸体/残块的二次吞噬。</summary>
        public bool IsCorpse;
        /// <summary>被吞噬目标的敌人 id（尸体/残块时为无效配置 id，消费方应结合 IsCorpse 判断）。</summary>
        public int EnemyId;
    }

    /// <summary>击杀（非吞噬致死，如放电、投射物、毒区）。</summary>
    public struct KillSignal
    {
        public int LogicId;
        public int ArchetypeId;
        public float2 Position;
        public SimStatus StatusAtDeath;
        public bool WasElite;
        public bool WasBoss;
    }

    /// <summary>玩家造成伤害命中。</summary>
    public struct HitSignal
    {
        public int TargetLogicId;
        public float2 Position;
        public float Damage;
        public bool Lethal;
    }

    /// <summary>玩家受伤。</summary>
    public struct PlayerHurtSignal
    {
        public float Amount;
        public float HealthAfter;
        public float HealthPercent;
    }

    /// <summary>玩家施放主动技能。</summary>
    public struct AbilityCastSignal
    {
        public int AbilityId;
        public float2 Origin;
        public float2 Direction;
    }

    /// <summary>
    /// 组合出口形态。<see cref="MetabolicSlice.Combat.MetabolicSliceBridge.ApplyEvent"/> 每次
    /// 把 HitEvent 应用出可观察效果时 Publish 一次，把 Shape/Scale/Count/Spin/Orbit/ExplodeOnHit
    /// 等一等字段交给表现层（story-002）。与 <see cref="AbilityCastSignal"/> 并列，不合并。
    /// </summary>
    public struct ComposeCastSignal
    {
        public string Shape;
        public float Scale;
        public float Count;
        public float Spin;
        public float Orbit;
        public bool ExplodeOnHit;
        /// <summary>引用源 HitEvent 的 Tags，非拷贝。Signals.Publish 是同步分发（Signals.cs 内循环内直接
        /// Invoke，非跨帧队列），订阅者若要跨帧持有该信号数据必须自行拷贝这个集合。</summary>
        public HashSet<string> Tags;
        public float2 Origin;
        public float2 Direction;
        public bool HasProjectile;
    }

    /// <summary>冲刺开始。机动路线的核心触发点。</summary>
    public struct DashSignal
    {
        public float2 Direction;
        public float Distance;
    }

    /// <summary>升级（进化能达阈值）。</summary>
    public struct LevelUpSignal
    {
        public int NewLevel;
        public DraftKind DraftKind;
    }

    /// <summary>卡牌获得。</summary>
    public struct CardAcquiredSignal
    {
        public int CardId;
        public int NewStack;
    }

    /// <summary>资源变化。</summary>
    public struct ResourceChangedSignal
    {
        public ResourceKind Kind;
        public float Delta;
        public float Current;
    }

    /// <summary>玩家体积变化。体积是细胞阶段独有的核心状态量。</summary>
    public struct VolumeChangedSignal
    {
        public float OldVolume;
        public float NewVolume;
    }

    /// <summary>生态时期推进。</summary>
    public struct PhaseChangedSignal
    {
        public int PhaseIndex;
        public int PhaseId;
        public string PhaseName;
    }

    /// <summary>生态事件开始/结束。</summary>
    public struct EcoEventSignal
    {
        public int EventId;
        public bool Started;
    }

    /// <summary>首领阶段切换（按血量阈值，数据驱动）。</summary>
    public struct BossPhaseChangedSignal
    {
        public int BossEnemyId;
        public int PhaseIndex;
        public string PhaseName;
    }

    /// <summary>周期性 tick。给"每 N 秒触发"类卡牌用，避免它们各自计时。</summary>
    public struct TickSignal
    {
        public float ElapsedInRun;
        public int TickIndex;
    }

    /// <summary>一局结束。</summary>
    public struct RunEndedSignal
    {
        public bool Victory;
        public float DurationSeconds;
        public string DeathCause;
    }

    /// <summary>局内资源种类。对应 Cell_Stage_Spec.md §5。</summary>
    public enum ResourceKind
    {
        None = 0,
        /// <summary>生命值。</summary>
        Health = 1,
        /// <summary>营养质：局内货币。</summary>
        Nutrient = 2,
        /// <summary>突变质：稀有构筑资源。</summary>
        Mutagen = 3,
        /// <summary>进化能：经验条。</summary>
        EvoEnergy = 4,
        /// <summary>污染度：高风险强度资源。</summary>
        Pollution = 5,
        /// <summary>体积。</summary>
        Volume = 6,
        /// <summary>体力（冲刺等技能消耗）。</summary>
        Stamina = 7,
    }

    /// <summary>进化选择形式。对应 Cell_Stage_Spec.md §6.2。</summary>
    public enum DraftKind
    {
        /// <summary>常规升级：3 选 1。</summary>
        Normal = 0,
        /// <summary>精英进化：4 选 1，至少 1 张稀有。</summary>
        Elite = 1,
        /// <summary>污染进化：3 选 1，卡强但有代价。</summary>
        Corrupt = 2,
        /// <summary>修复进化：2 选 1，低血保底补偿。</summary>
        Repair = 3,
        /// <summary>遗产进化：3 选 1，原核遗产卡。</summary>
        Legacy = 4,
    }
}
