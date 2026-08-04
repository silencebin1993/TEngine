using GameLogic.Config;
using GameConfig.fp;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// First Playable 玩法数值门面。数据来自 Luban 表（fp.Tb*），改数值请改 Excel 后导表。
    /// </summary>
    public static class FPTuning
    {
        private static Global G => FPConfig.Global;
        private static CellArena CA => FPConfig.CellArena;
        private static CreatureArena CRA => FPConfig.CreatureArena;
        private static PlayerForm Cell => FPConfig.CellForm;
        private static PlayerForm Creature => FPConfig.CreatureForm;
        private static Food FoodACfg => FPConfig.FoodA;
        private static Food FoodBCfg => FPConfig.FoodB;
        private static Enemy ThreatCfg => FPConfig.Threat;
        private static Enemy HerbivoreCfg => FPConfig.Herbivore;
        private static Enemy PredatorCfg => FPConfig.Predator;
        private static Enemy EliteCfg => FPConfig.Elite;

        // ---------- §5.1 吞噬判定 ----------
        public static float EngulfRatio => G.EngulfRatio;

        // ---------- §5.2 体积增长与移速衰减 ----------
        public static float VolumeGainRatio => G.VolumeGainRatio;
        public static float SpeedVolumePenalty => G.SpeedVolumePenalty;
        public static float SpeedFloorRatio => G.SpeedFloorRatio;

        // ---------- §5.4 伤害 ----------
        public static float ContactDamageInterval => G.ContactDamageInterval;

        // ---------- §7.1 细胞阶段 ----------
        public static float CellPlayerHp => Cell.MaxHp;
        public static float CellPlayerStartVolume => Cell.StartVolume;
        public static float CellPlayerMaxVolume => Cell.MaxVolume;
        public static float CellPlayerBaseSpeed => Cell.BaseSpeed;
        public static float CellAccel => Cell.Accel;
        public static float CellDrag => Cell.Drag;

        public static float FoodAVolume => FoodACfg.Volume;
        public static int FoodAEvoPoint => FoodACfg.EvoPoint;
        public static int FoodABiomass => FoodACfg.Biomass;

        public static float FoodBVolume => FoodBCfg.Volume;
        public static int FoodBEvoPoint => FoodBCfg.EvoPoint;
        public static int FoodBBiomass => FoodBCfg.Biomass;

        public static float ThreatVolume => ThreatCfg.Volume;
        public static float ThreatContactDamage => ThreatCfg.ContactDamage;
        public static float ThreatSpeed => ThreatCfg.Speed;
        public static int ThreatEvoPoint => ThreatCfg.EvoPoint;
        public static int ThreatBiomass => ThreatCfg.Biomass;

        public static float HazardDamagePerSecond => CA.HazardDamagePerSecond;

        public static int EvoPointThreshold => G.EvoPointThreshold;
        public static float MicroChoiceTime => CA.MicroChoiceTime;
        public static float WaveInterval => G.WaveInterval;
        public static int WaveCount => G.WaveCount;
        public static float ForceEvolveTime => CA.ForceEvolveTime;

        private static float[] _waveThreatMultiplier;

        /// <summary>各波威胁数量倍率。</summary>
        public static float[] WaveThreatMultiplier
        {
            get
            {
                if (_waveThreatMultiplier == null)
                {
                    _waveThreatMultiplier = FPConfig.BuildWaveThreatMultipliers();
                }

                return _waveThreatMultiplier;
            }
        }

        public static float ArenaHalfSize => CA.ArenaHalfSize;
        public static int FoodConcurrent => CA.FoodConcurrent;
        public static float FoodRespawnDelay => CA.FoodRespawnDelay;
        public static float FoodBRatio => CA.FoodBRatio;
        public static int ThreatBaseCount => CA.ThreatBaseCount;
        public static int HazardCount => CA.HazardCount;
        public static float HazardRadius => CA.HazardRadius;

        // ---------- §4.1 细胞阶段 3 选 1 微选择 ----------
        public static float MicroGluttonyBiomassBonus =>
            FPConfig.GetMicroChoice(EMicroChoice.Gluttony)?.BiomassBonus ?? 0f;

        public static float MicroPhototaxisSpeedBonus =>
            FPConfig.GetMicroChoice(EMicroChoice.Phototaxis)?.SpeedBonus ?? 0f;

        public static float MicroMetabolicHealPerEat =>
            FPConfig.GetMicroChoice(EMicroChoice.Metabolic)?.HealPerEat ?? 0f;

        // ---------- §7.2 生物阶段 ----------
        public static float CreatureBaseMaxHp => Creature.MaxHp;
        public static float CreatureBaseSpeed => Creature.BaseSpeed;
        public static float CreatureBaseMelee => Creature.MeleeDamage;
        public static float CreatureMeleeInterval => Creature.MeleeInterval;
        public static float CreatureMeleeRange => Creature.MeleeRange;

        public static float StaminaMax => Creature.StaminaMax;
        public static float DashCost => Creature.DashCost;
        public static float StaminaRegen => Creature.StaminaRegen;
        public static float DashInvulnTime => Creature.DashInvulnTime;
        public static float StaminaRegenDelay => Creature.StaminaRegenDelay;
        public static float DashSpeedMultiplier => Creature.DashSpeedMultiplier;
        public static float DashDuration => Creature.DashDuration;

        public static float HerbivoreHp => HerbivoreCfg.Hp;
        public static float HerbivoreContactDamage => HerbivoreCfg.ContactDamage;
        public static float HerbivoreSpeed => HerbivoreCfg.Speed;

        public static float PredatorHp => PredatorCfg.Hp;
        public static float PredatorContactDamage => PredatorCfg.ContactDamage;
        public static float PredatorSpeed => PredatorCfg.Speed;

        public static float EliteHp => EliteCfg.Hp;
        public static float EliteContactDamage => EliteCfg.ContactDamage;
        public static float EliteSpeed => EliteCfg.Speed;

        public static float EliteChargeDamage
        {
            get
            {
                var skill = FPConfig.GetSkill(EliteCfg.SkillId);
                return skill != null ? skill.Damage : 0f;
            }
        }

        public static float EliteChargeCooldown
        {
            get
            {
                var skill = FPConfig.GetSkill(EliteCfg.SkillId);
                return skill != null ? skill.Cooldown : 0f;
            }
        }

        public static float EnemyBaseAggroRange => G.EnemyBaseAggroRange;

        public static float CreatureExploreEnd => CRA.ExploreEnd;
        public static float CreaturePressureEnd => CRA.PressureEnd;
        public static float CreatureArenaHalfSize => CRA.ArenaHalfSize;
        public static float CreatureEnemyRespawnDelay => CRA.EnemyRespawnDelay;
        public static int CreatureEnemyCapPhase1 => CRA.EnemyCapPhase1;
        public static int CreatureEnemyCapPhase2 => CRA.EnemyCapPhase2;
    }
}
