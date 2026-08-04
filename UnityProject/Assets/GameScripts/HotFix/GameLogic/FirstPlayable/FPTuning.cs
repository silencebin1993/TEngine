namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// First Playable 全部玩法数值。唯一来源：DesignDocs/First_Playable_Spec.md §5 / §7。
    /// 改数值只改这里，不要散落到各 Controller。
    /// </summary>
    public static class FPTuning
    {
        // ---------- §5.1 吞噬判定 ----------
        /// <summary>体积吞噬阈值 k。Vp >= Vt * k 可吞噬。</summary>
        public const float EngulfRatio = 1.15f;

        // ---------- §5.2 体积增长与移速衰减 ----------
        public const float VolumeGainRatio = 0.30f;
        public const float SpeedVolumePenalty = 0.18f;
        public const float SpeedFloorRatio = 0.55f;

        // ---------- §5.4 伤害 ----------
        /// <summary>同一敌人对玩家的伤害间隔，防止贴身瞬间掉光血。</summary>
        public const float ContactDamageInterval = 0.8f;

        // ---------- §7.1 细胞阶段 ----------
        public const float CellPlayerHp = 60f;
        public const float CellPlayerStartVolume = 1.0f;
        public const float CellPlayerMaxVolume = 3.0f;
        public const float CellPlayerBaseSpeed = 4.0f;
        public const float CellAccel = 12f;
        public const float CellDrag = 4f;

        public const float FoodAVolume = 0.5f;
        public const int FoodAEvoPoint = 3;
        public const int FoodABiomass = 8;

        public const float FoodBVolume = 1.2f;
        public const int FoodBEvoPoint = 7;
        public const int FoodBBiomass = 18;

        public const float ThreatVolume = 1.8f;
        public const float ThreatContactDamage = 12f;
        public const float ThreatSpeed = 3.2f;
        /// <summary>
        /// 威胁被玩家吞噬时的收益。Spec 未规定此情况。刻意低于 B 型食物的单位时间收益，
        /// 使"围堵威胁"是可选打法而非主导策略，食物仍是主要来源。
        /// </summary>
        public const int ThreatEvoPoint = 6;
        public const int ThreatBiomass = 14;

        public const float HazardDamagePerSecond = 6f;

        public const int EvoPointThreshold = 100;
        public const float MicroChoiceTime = 270f;   // 4:30
        public const float WaveInterval = 270f;      // 每 4:30 一波
        public const int WaveCount = 3;
        public const float ForceEvolveTime = 810f;   // 13:30

        /// <summary>各波威胁数量倍率 1x / 2x / 4x。</summary>
        public static readonly float[] WaveThreatMultiplier = { 1f, 2f, 4f };

        // ---------- 细胞阶段场景密度（Spec 未规定，由 9 分钟达标反推）----------
        // 反推：100 EP / 540s ≈ 11 EP/分钟。A 型 3 EP、B 型 7 EP，混合均值约 4.2 EP，
        // 即约每 22 秒获得一个食物。因此场地要大、食物要少，时间由"移动 + 躲威胁"填充。
        // 若实测觉得过于空旷，优先调这三个值（调大 FoodConcurrent / 调小 ArenaSize）。
        public const float ArenaHalfSize = 28f;      // 56 x 56 平面
        public const int FoodConcurrent = 6;         // 场上同时存在的食物数
        public const float FoodRespawnDelay = 4.5f;  // 补充一个食物的间隔
        public const float FoodBRatio = 0.3f;        // B 型食物占比
        public const int ThreatBaseCount = 5;        // 第 1 波威胁数
        public const int HazardCount = 4;            // 危险区域数量
        public const float HazardRadius = 3.5f;

        // ---------- §4.1 细胞阶段 3 选 1 微选择 ----------
        public const float MicroGluttonyBiomassBonus = 0.25f;  // 贪食囊：生物质 +25%
        public const float MicroPhototaxisSpeedBonus = 0.20f;  // 趋光纤毛：移速 +20%
        public const float MicroMetabolicHealPerEat = 3f;      // 代谢泡：每次吞噬回 3 血

        // ---------- §7.2 生物阶段 ----------
        public const float CreatureBaseMaxHp = 140f;
        public const float CreatureBaseSpeed = 5.0f;
        public const float CreatureBaseMelee = 15f;
        public const float CreatureMeleeInterval = 0.6f;
        public const float CreatureMeleeRange = 1.5f;

        public const float StaminaMax = 100f;
        public const float DashCost = 30f;
        public const float StaminaRegen = 12f;
        public const float DashInvulnTime = 0.3f;
        public const float StaminaRegenDelay = 1.0f;
        public const float DashSpeedMultiplier = 3.2f;
        public const float DashDuration = 0.18f;

        public const float HerbivoreHp = 40f;
        public const float HerbivoreContactDamage = 8f;
        public const float HerbivoreSpeed = 3.0f;

        public const float PredatorHp = 70f;
        public const float PredatorContactDamage = 14f;
        public const float PredatorSpeed = 4.5f;

        public const float EliteHp = 250f;
        public const float EliteContactDamage = 22f;
        public const float EliteSpeed = 4.2f;
        public const float EliteChargeDamage = 38f;
        public const float EliteChargeCooldown = 7f;

        /// <summary>敌人基础察觉范围。Spec 只给了 -35% 修正，基数由此处定义。</summary>
        public const float EnemyBaseAggroRange = 12f;

        // ---------- 生物阶段时间线 §4.3 ----------
        public const float CreatureExploreEnd = 240f;   // 4:00
        public const float CreaturePressureEnd = 480f;  // 8:00
        public const float CreatureArenaHalfSize = 22f;
        public const float CreatureEnemyRespawnDelay = 6f;
        public const int CreatureEnemyCapPhase1 = 5;
        public const int CreatureEnemyCapPhase2 = 9;
    }
}
