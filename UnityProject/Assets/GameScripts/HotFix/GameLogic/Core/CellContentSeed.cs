using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Cards;
using GameLogic.Stats;

namespace GameLogic.Core
{
    /// <summary>
    /// 细胞阶段内置兜底内容。
    ///
    /// 定位：**兜底**。cell.* Luban 表已生成（135 卡 / 30 敌人 / 28 技能），
    /// 正常情况下 DataRegistry.LoadFromLuban 会成功，根本走不到这里。
    ///
    /// 保留它的唯一理由：配置表缺失或损坏时（例如打包漏了 bytes、
    /// 或改表改坏了）不至于整局起不来，而是退化到一个能跑的最小内容集
    /// （8 原型 / 16 敌人 / 8 技能 / 24 卡 / 6 时期 / 8 事件）并在日志里告警。
    ///
    /// 所以本文件的内容量刻意不与 Luban 表同步——它不是"另一份内容"，
    /// 而是"出事时的降级形态"。真要改内容，改 tools/cell_tables/ 下的生成器。
    /// </summary>
    internal static class CellContentSeed
    {
        // 行为原型索引。顺序与 AddArchetype 调用顺序一致。
        private const int ArcDrift = 0;
        private const int ArcChase = 1;
        private const int ArcPatrol = 2;
        private const int ArcCharge = 3;
        private const int ArcRanged = 4;
        private const int ArcSwarm = 5;
        private const int ArcStationary = 6;
        private const int ArcOrbit = 7;
        private const int ArcBossEnrage = 8;
        private const int ArcBossFinal = 9;

        public static void Populate(DataRegistry reg)
        {
            Archetypes(reg);
            Enemies(reg);
            Abilities(reg);
            Cards(reg);
            Phases(reg);
            EcoEvents(reg);
            BossPhases(reg);
        }

        private static void Archetypes(DataRegistry reg)
        {
            // 漂浮：不索敌，随水流游走。基础食物。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Drift, Accel = 3f, TurnRate = 0f,
                AggroRange = 0f, AttackRange = 0.3f, AttackCooldown = 1.5f,
                AttackDamage = 0f, Separation = 0.6f, WanderStrength = 1f,
                ChargeSpeedMul = 1f,
            });

            // 追猎：锁定玩家直线追逐。基础威胁。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Chase, Accel = 6f, TurnRate = 4f,
                AggroRange = 26f, AttackRange = 0.4f, AttackCooldown = 1.1f,
                AttackDamage = 6f, Separation = 1f, WanderStrength = 0.8f,
                ChargeSpeedMul = 1f,
            });

            // 巡逻：横向来回，逼迫绕行。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Patrol, Accel = 4f, TurnRate = 2f,
                AggroRange = 0f, AttackRange = 0.5f, AttackCooldown = 1.3f,
                AttackDamage = 5f, Separation = 0.8f, WanderStrength = 0f,
                ChargeSpeedMul = 1f,
            });

            // 冲撞：蓄力后高速直线，冲刺中不转向。给玩家反应窗口。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Charge, Accel = 10f, TurnRate = 1.2f,
                AggroRange = 20f, AttackRange = 0.6f, AttackCooldown = 2.4f,
                AttackDamage = 12f, Separation = 0.7f, WanderStrength = 0.5f,
                ChargeTelegraph = 0.8f, ChargeSpeedMul = 2.6f,
            });

            // 远程：保持距离投射。远程压力。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Ranged, Accel = 5f, TurnRate = 3f,
                AggroRange = 24f, AttackRange = 12f, AttackCooldown = 2f,
                AttackDamage = 7f, Separation = 1.1f, PreferredRange = 10f,
                WanderStrength = 0.6f, ChargeSpeedMul = 1f,
            });

            // 群体：松散推进而非直线列队。AOE 与连锁的测试目标。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Swarm, Accel = 7f, TurnRate = 5f,
                AggroRange = 30f, AttackRange = 0.35f, AttackCooldown = 0.9f,
                AttackDamage = 3f, Separation = 0.45f, WanderStrength = 0.7f,
                ChargeSpeedMul = 1f,
            });

            // 固守：不动，占据空间。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Stationary, Accel = 1f, TurnRate = 0f,
                AggroRange = 0f, AttackRange = 2f, AttackCooldown = 1.6f,
                AttackDamage = 8f, Separation = 0f, WanderStrength = 0f,
                ChargeSpeedMul = 1f,
            });

            // 环绕：保持半径绕圈，逼迫玩家处理机动目标。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Orbit, Accel = 9f, TurnRate = 6f,
                AggroRange = 28f, AttackRange = 0.5f, AttackCooldown = 1f,
                AttackDamage = 9f, Separation = 0.9f, PreferredRange = 7f,
                WanderStrength = 0.4f, ChargeSpeedMul = 1f,
            });

            // 首领专属：狂暴猎杀。阶段 1 用，追猎的强化版——更快、更凶。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Chase, Accel = 8f, TurnRate = 6f,
                AggroRange = 30f, AttackRange = 0.45f, AttackCooldown = 0.85f,
                AttackDamage = 14f, Separation = 1f, WanderStrength = 0.6f,
                ChargeSpeedMul = 1f,
            });

            // 首领专属：终焉冲撞。阶段 2 用，濒死转入更具威胁的冲撞形态。
            reg.AddArchetype(new BehaviorArchetype
            {
                Kind = BehaviorKind.Charge, Accel = 12f, TurnRate = 1.5f,
                AggroRange = 26f, AttackRange = 0.65f, AttackCooldown = 1.6f,
                AttackDamage = 20f, Separation = 0.9f,
                ChargeTelegraph = 0.6f, ChargeSpeedMul = 3f,
            });
        }

        private static void Enemies(DataRegistry reg)
        {
            // 拾取物/食物：无威胁，纯资源。
            reg.AddEnemy(new EnemySpec
            {
                Id = 1, Name = "浮游食团", ArchetypeIndex = ArcDrift,
                Health = 4f, Radius = 0.35f, MaxSpeed = 1.2f,
                SpawnCost = 1f, MinPhase = 0,
                EvoEnergy = 2f, Nutrient = 1f, VisualId = 1,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 2, Name = "刺膜细胞", ArchetypeIndex = ArcDrift,
                Health = 8f, Radius = 0.5f, MaxSpeed = 1f,
                SpawnCost = 2f, MinPhase = 0,
                EvoEnergy = 3f, Nutrient = 2f, VisualId = 2,
                // 不可直接吞噬，逼玩家先破体——这是"教玩家判断目标"的实现
                InitialStatus = SimStatus.Unedible,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 3, Name = "扫尾纤毛体", ArchetypeIndex = ArcPatrol,
                Health = 12f, Radius = 0.6f, MaxSpeed = 4.5f,
                SpawnCost = 3f, MinPhase = 0,
                EvoEnergy = 4f, Nutrient = 2f, VisualId = 3,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 4, Name = "追猎原虫", ArchetypeIndex = ArcChase,
                Health = 16f, Radius = 0.55f, MaxSpeed = 5.2f,
                SpawnCost = 4f, MinPhase = 0,
                EvoEnergy = 5f, Nutrient = 3f, VisualId = 4,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 5, Name = "噬菌群", ArchetypeIndex = ArcSwarm,
                Health = 6f, Radius = 0.3f, MaxSpeed = 6f,
                SpawnCost = 1.5f, MinPhase = 1,
                EvoEnergy = 2f, Nutrient = 1f, VisualId = 5,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 6, Name = "硬壳核胞", ArchetypeIndex = ArcChase,
                Health = 55f, Radius = 1.4f, MaxSpeed = 2.2f,
                SpawnCost = 8f, MinPhase = 1,
                EvoEnergy = 12f, Nutrient = 8f, VisualId = 6,
                InitialStatus = SimStatus.Unedible | SimStatus.Hardened,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 7, Name = "导电水母体", ArchetypeIndex = ArcDrift,
                Health = 14f, Radius = 0.7f, MaxSpeed = 1.8f,
                SpawnCost = 3f, MinPhase = 1,
                EvoEnergy = 5f, Nutrient = 3f, VisualId = 7,
                // 天生导电：电化 build 的机会目标
                InitialStatus = SimStatus.Conductive,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 8, Name = "腐败孢团", ArchetypeIndex = ArcDrift,
                Health = 10f, Radius = 0.5f, MaxSpeed = 1.4f,
                SpawnCost = 2.5f, MinPhase = 1,
                EvoEnergy = 4f, Nutrient = 3f, VisualId = 8,
                InitialStatus = SimStatus.Polluted,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 9, Name = "游隼纤毛", ArchetypeIndex = ArcCharge,
                Health = 22f, Radius = 0.6f, MaxSpeed = 4f,
                SpawnCost = 6f, MinPhase = 3,
                EvoEnergy = 8f, Nutrient = 4f, VisualId = 9,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 10, Name = "毒棘漂虫", ArchetypeIndex = ArcRanged,
                Health = 18f, Radius = 0.55f, MaxSpeed = 3f,
                SpawnCost = 6f, MinPhase = 3,
                EvoEnergy = 8f, Nutrient = 5f, VisualId = 10,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 11, Name = "簇生菌丝", ArchetypeIndex = ArcStationary,
                Health = 40f, Radius = 1f, MaxSpeed = 0f,
                SpawnCost = 4f, MinPhase = 2,
                EvoEnergy = 6f, Nutrient = 4f, VisualId = 11,
            });

            // 精英：慢速追踪，尝试吞噬玩家。检验吞噬阈值与风筝。
            reg.AddEnemy(new EnemySpec
            {
                Id = 50, Name = "巨噬吞食者", ArchetypeIndex = ArcChase,
                Health = 320f, Radius = 3.2f, MaxSpeed = 3.4f,
                SpawnCost = 40f, MinPhase = 1,
                EvoEnergy = 80f, Nutrient = 40f, Mutagen = 3f, VisualId = 50,
                InitialStatus = SimStatus.Unedible | SimStatus.Elite,
                IsElite = true,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 51, Name = "裂鞭纤毛王", ArchetypeIndex = ArcOrbit,
                Health = 260f, Radius = 2.2f, MaxSpeed = 7.5f,
                SpawnCost = 40f, MinPhase = 2,
                EvoEnergy = 80f, Nutrient = 40f, Mutagen = 3f, VisualId = 51,
                InitialStatus = SimStatus.Unedible | SimStatus.Elite,
                IsElite = true,
            });

            reg.AddEnemy(new EnemySpec
            {
                Id = 52, Name = "电泳猎核", ArchetypeIndex = ArcRanged,
                Health = 280f, Radius = 2.4f, MaxSpeed = 4f,
                SpawnCost = 42f, MinPhase = 3,
                EvoEnergy = 90f, Nutrient = 45f, Mutagen = 4f, VisualId = 52,
                InitialStatus = SimStatus.Unedible | SimStatus.Elite | SimStatus.Conductive,
                IsElite = true,
            });

            // 首领
            reg.AddEnemy(new EnemySpec
            {
                Id = 90, Name = "原核霸主", ArchetypeIndex = ArcChase,
                Health = 2400f, Radius = 5f, MaxSpeed = 4.2f,
                SpawnCost = 200f, MinPhase = 5,
                EvoEnergy = 300f, Nutrient = 150f, Mutagen = 12f, VisualId = 90,
                InitialStatus = SimStatus.Unedible | SimStatus.Boss,
                IsBoss = true,
            });
        }

        private static void Abilities(DataRegistry reg)
        {
            // 冲刺：初始技能，全路线通用保命手段
            var dash = new AbilitySpec
            {
                Id = 1, Name = "冲刺", Desc = "快速位移，短暂无敌。",
                Cooldown = 2.2f, Charges = 2, StaminaCost = 25f,
                TargetMode = TargetMode.MoveDirection,
                Tags = new[] { AbilityTag.Mobility, AbilityTag.Survival },
                IsStarter = true,
            };
            dash.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Dash, Shape = EffectShape.Line,
                Value = 4.5f, Duration = 0.15f, ScaleWithPower = false,
            });
            reg.AddAbility(dash);

            // 放电：电化路线核心
            var zap = new AbilitySpec
            {
                Id = 2, Name = "放电", Desc = "对最近敌人放电，施加导电。",
                Cooldown = 1.6f, Charges = 1, StaminaCost = 12f, CastRange = 9f,
                TargetMode = TargetMode.NearestEnemy,
                Tags = new[] { AbilityTag.Electric },
            };
            zap.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Damage, Shape = EffectShape.Target,
                Value = 18f, Count = 1, Radius = 4f,
                Status = SimStatus.Conductive,
                Affixes = new[] { AffixKind.Conductive },
            });
            reg.AddAbility(zap);

            // 酸雾：吞噬/污染路线的区域手段
            var mist = new AbilitySpec
            {
                Id = 3, Name = "酸雾", Desc = "喷出腐蚀雾区，降低目标吞噬门槛。",
                Cooldown = 6f, Charges = 1, StaminaCost = 20f,
                TargetMode = TargetMode.MoveDirection,
                Tags = new[] { AbilityTag.Devour, AbilityTag.Area },
            };
            mist.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Area, Shape = EffectShape.Cone,
                Value = 6f, Radius = 5f, Duration = 4f,
                Status = SimStatus.Corroded,
                Affixes = new[] { AffixKind.Corrosive, AffixKind.Lingering },
            });
            reg.AddAbility(mist);

            // 脱壳：机动/生存
            var molt = new AbilitySpec
            {
                Id = 4, Name = "脱壳", Desc = "留下旧壳诱敌，自身短暂无敌。",
                Cooldown = 12f, Charges = 1, StaminaCost = 15f,
                TargetMode = TargetMode.Self,
                Tags = new[] { AbilityTag.Survival, AbilityTag.Mobility },
            };
            molt.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Status, Shape = EffectShape.Self,
                Status = SimStatus.Invulnerable, Duration = 1.2f,
                TargetFaction = SimFaction.Player, ScaleWithPower = false,
            });
            molt.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Spawn, Shape = EffectShape.Self,
                Count = 1, SpawnEnemyId = 1, Radius = 0.5f, Duration = 6f,
            });
            reg.AddAbility(molt);

            // 孢子爆发：孢子路线
            var spore = new AbilitySpec
            {
                Id = 5, Name = "孢子爆发", Desc = "向四周释放孢子附属体。",
                Cooldown = 9f, Charges = 1, StaminaCost = 22f,
                TargetMode = TargetMode.Self,
                Tags = new[] { AbilityTag.Spore, AbilityTag.Summon },
            };
            spore.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Spawn, Shape = EffectShape.Circle,
                Count = 4, SpawnEnemyId = 5, Radius = 2.5f, Duration = 12f,
                Affixes = new[] { AffixKind.Proliferate },
            });
            reg.AddAbility(spore);

            // 骨刺投射：吞噬路线的远程手段，消耗体积
            var spine = new AbilitySpec
            {
                Id = 6, Name = "骨刺投射", Desc = "消耗体积发射穿刺骨刺。",
                Cooldown = 3f, Charges = 2, StaminaCost = 10f,
                TargetMode = TargetMode.Cursor,
                Tags = new[] { AbilityTag.Devour, AbilityTag.Projectile },
            };
            spine.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Projectile, Shape = EffectShape.Line,
                Value = 22f, Count = 1, Radius = 0.3f, Duration = 2f,
                Affixes = new[] { AffixKind.Pierce },
            });
            reg.AddAbility(spine);

            // 硬化：生存
            var harden = new AbilitySpec
            {
                Id = 7, Name = "硬化", Desc = "短时间大幅降低受到的伤害。",
                Cooldown = 14f, Charges = 1, StaminaCost = 18f,
                TargetMode = TargetMode.Self,
                Tags = new[] { AbilityTag.Survival },
            };
            harden.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Status, Shape = EffectShape.Self,
                Status = SimStatus.Hardened, Duration = 4f,
                TargetFaction = SimFaction.Player, ScaleWithPower = false,
            });
            reg.AddAbility(harden);

            // 导电爆破：电化 build 的兑现手段
            var burst = new AbilitySpec
            {
                Id = 8, Name = "导电爆破", Desc = "引爆所有导电目标。",
                Cooldown = 11f, Charges = 1, StaminaCost = 25f,
                TargetMode = TargetMode.Self,
                Tags = new[] { AbilityTag.Electric, AbilityTag.Area },
            };
            burst.Effects.Add(new EffectSpec
            {
                Kind = EffectKind.Damage, Shape = EffectShape.Circle,
                Value = 40f, Radius = 14f,
                RequireStatus = SimStatus.Conductive,
                Affixes = new[] { AffixKind.Conductive, AffixKind.Chain },
                Count = 2,
            });
            reg.AddAbility(burst);
        }

        private static void Cards(DataRegistry reg)
        {
            // ── 吞噬扩张 ──
            AddCard(reg, 1001, "裂齿口器", CardRoute.Devour, CardRarity.Common, 0, 3,
                "近战命中施加破体，降低目标被吞噬的体积门槛。",
                CardTrigger.OnHit, new[] { SynergyTag.Devour, SynergyTag.Execute },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Status, Shape = EffectShape.Circle,
                    Status = SimStatus.Breached, Radius = 1.8f, Duration = 4f,
                });

            AddCard(reg, 1002, "腐解胃袋", CardRoute.Devour, CardRarity.Common, 0, 1,
                "敌人死亡后留下可二次吞噬的组织残块。",
                CardTrigger.Passive, new[] { SynergyTag.Corpse, SynergyTag.Devour },
                rules: new[] { RuleFlag.CorpseEdible });

            AddCard(reg, 1003, "贪食腺", CardRoute.Devour, CardRarity.Common, 0, 3,
                "每次吞噬获得额外营养质，连吃越多收益越高。",
                CardTrigger.OnDevour, new[] { SynergyTag.Combo, SynergyTag.Devour },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Resource, Shape = EffectShape.Self,
                    Resource = ResourceKind.Nutrient, Value = 2f,
                });

            AddCard(reg, 1004, "酸蚀消化液", CardRoute.Devour, CardRarity.Rare, 2, 2,
                "吞噬失败也会腐蚀目标，使其逐渐变成可吞噬状态。",
                CardTrigger.Passive, new[] { SynergyTag.Devour },
                rules: new[] { RuleFlag.FailedDevourCorrodes });

            AddCard(reg, 1005, "分食囊", CardRoute.Devour, CardRarity.Rare, 2, 1,
                "大型目标死亡时裂成多个小食物块。",
                CardTrigger.Passive, new[] { SynergyTag.Corpse, SynergyTag.Combo },
                rules: new[] { RuleFlag.LargeTargetsSplit });

            AddCard(reg, 1006, "吞噬回响", CardRoute.Devour, CardRarity.Epic, 3, 2,
                "每次吞噬后，下一次技能伤害提高。",
                CardTrigger.OnDevour, new[] { SynergyTag.Devour, SynergyTag.Burst },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Stat, Shape = EffectShape.Self,
                    Stat = StatId.AbilityPower, Op = ModifierOp.PctAdd,
                    Value = 0.08f, Duration = 5f, ScaleWithPower = false,
                });

            AddCard(reg, 1007, "食物链宣告", CardRoute.Devour, CardRarity.Aberrant, 4, 1,
                "处决目标后附近同类恐惧逃散，但精英会更快锁定你。",
                CardTrigger.Passive, new[] { SynergyTag.Execute, SynergyTag.Risk },
                rules: new[] { RuleFlag.ExecuteCausesFear },
                drawback: "敌人仇恨 +25%。",
                drawbackMod: new StatModifier(StatId.AggroScale, ModifierOp.PctAdd, 0.25f));

            // ── 机动猎食 ──
            AddCard(reg, 2001, "感知纤毛", CardRoute.Agile, CardRarity.Common, 0, 3,
                "自动标记附近可猎食目标。",
                CardTrigger.OnTick, new[] { SynergyTag.Mark },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Status, Shape = EffectShape.Circle,
                    Status = SimStatus.Marked, Radius = 7f, Duration = 3f,
                },
                interval: 2f);

            AddCard(reg, 2002, "协同神经束", CardRoute.Agile, CardRarity.Common, 0, 2,
                "冲刺穿过敌人时施加短暂麻痹。",
                CardTrigger.OnDash, new[] { SynergyTag.Dash },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Status, Shape = EffectShape.Circle,
                    Status = SimStatus.Stunned, Radius = 2.2f, Duration = 0.8f,
                });

            AddCard(reg, 2003, "穿体脉冲", CardRoute.Agile, CardRarity.Common, 1, 3,
                "冲刺穿过敌人时附加导电。",
                CardTrigger.OnDash, new[] { SynergyTag.Dash, SynergyTag.Conductive },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Damage, Shape = EffectShape.Line,
                    Value = 12f, Radius = 4.5f, Status = SimStatus.Conductive,
                    Affixes = new[] { AffixKind.Conductive },
                });

            AddCard(reg, 2004, "轨迹电痕", CardRoute.Agile, CardRarity.Rare, 2, 1,
                "冲刺路径留下短暂电流地带。",
                CardTrigger.Passive, new[] { SynergyTag.Dash, SynergyTag.Electric },
                rules: new[] { RuleFlag.DashLeavesCurrent });

            AddCard(reg, 2005, "捕猎扑跃", CardRoute.Agile, CardRarity.Rare, 2, 2,
                "对已标记目标造成的伤害提高。",
                CardTrigger.Passive, new[] { SynergyTag.Mark, SynergyTag.Dash },
                statMod: new StatModifier(StatId.MeleeDamage, ModifierOp.PctAdd, 0.2f));

            AddCard(reg, 2006, "深潜本能", CardRoute.Agile, CardRarity.Aberrant, 4, 1,
                "濒死时自动脱战，但清空当前连击与标记。",
                CardTrigger.Passive, new[] { SynergyTag.Survival },
                rules: new[] { RuleFlag.AutoEscapeOnLowHp },
                drawback: "触发后 8 秒内吞噬收益减半。");

            // ── 电化统治 ──
            AddCard(reg, 3001, "原始放电囊", CardRoute.Electric, CardRarity.Common, 0, 1,
                "获得主动技能：放电。",
                CardTrigger.Passive, new[] { SynergyTag.Electric },
                grantAbility: 2);

            AddCard(reg, 3002, "能量导流组织", CardRoute.Electric, CardRarity.Common, 1, 3,
                "体力回复提高，电系伤害提高。",
                CardTrigger.Passive, new[] { SynergyTag.Electric },
                statMod: new StatModifier(StatId.ElectricPower, ModifierOp.PctAdd, 0.15f),
                isPureStat: true);

            AddCard(reg, 3003, "分叉神经弧", CardRoute.Electric, CardRarity.Rare, 2, 3,
                "放电命中后弹射到附近目标。",
                CardTrigger.Passive, new[] { SynergyTag.Chain, SynergyTag.Electric },
                statMod: new StatModifier(StatId.ChainBonus, ModifierOp.Flat, 1f));

            AddCard(reg, 3004, "导电黏液", CardRoute.Electric, CardRarity.Rare, 2, 2,
                "被放电命中的目标周围也变为导电。",
                CardTrigger.OnHit, new[] { SynergyTag.Conductive, SynergyTag.Area },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Status, Shape = EffectShape.Circle,
                    Status = SimStatus.Conductive, Radius = 3f, Duration = 5f,
                });

            AddCard(reg, 3005, "腐尸导线", CardRoute.Electric, CardRarity.Rare, 3, 1,
                "尸体与组织残块可作为放电跳板。",
                CardTrigger.Passive, new[] { SynergyTag.Corpse, SynergyTag.Chain },
                rules: new[] { RuleFlag.CorpseConducts });

            AddCard(reg, 3006, "雷暴共振结", CardRoute.Electric, CardRarity.Epic, 3, 1,
                "多个导电目标会互相连接并周期性共振放电。",
                CardTrigger.OnTick, new[] { SynergyTag.Chain, SynergyTag.Electric },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Damage, Shape = EffectShape.Circle,
                    Value = 20f, Radius = 12f, Count = 3,
                    RequireStatus = SimStatus.Conductive,
                    Affixes = new[] { AffixKind.Resonate, AffixKind.Conductive },
                },
                interval: 3f);

            AddCard(reg, 3007, "过载腔", CardRoute.Electric, CardRarity.Aberrant, 4, 1,
                "电系伤害大幅提高，但每次放电自损生命。",
                CardTrigger.Passive, new[] { SynergyTag.Electric, SynergyTag.Risk },
                statMod: new StatModifier(StatId.ElectricPower, ModifierOp.PctMul, 0.45f),
                drawback: "受到的伤害 +15%。",
                drawbackMod: new StatModifier(StatId.DamageTaken, ModifierOp.PctAdd, 0.15f));

            // ── 孢子繁殖 ──
            AddCard(reg, 4001, "孢子囊", CardRoute.Spore, CardRarity.Common, 1, 3,
                "周期性释放小孢子，自动撞击弱敌。",
                CardTrigger.OnTick, new[] { SynergyTag.Minion },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Spawn, Shape = EffectShape.Circle,
                    Count = 2, SpawnEnemyId = 5, Radius = 2f, Duration = 10f,
                },
                interval: 5f);

            AddCard(reg, 4002, "神经副脑", CardRoute.Spore, CardRarity.Rare, 2, 2,
                "可同时存在的附属体上限提高。",
                CardTrigger.Passive, new[] { SynergyTag.Minion },
                statMod: new StatModifier(StatId.MinionCap, ModifierOp.Flat, 3f));

            AddCard(reg, 4003, "猎群协议", CardRoute.Spore, CardRarity.Epic, 3, 1,
                "附属体优先攻击标记目标，且伤害提高。",
                CardTrigger.Passive, new[] { SynergyTag.Minion, SynergyTag.Mark },
                rules: new[] { RuleFlag.MinionsFocusMarked },
                statMod: new StatModifier(StatId.MinionPower, ModifierOp.PctAdd, 0.3f));

            // ── 菌毯筑巢 ──
            AddCard(reg, 5001, "菌毯扩散", CardRoute.Nest, CardRarity.Common, 2, 3,
                "停留区域逐渐生成菌毯，站在菌毯上回复更快。",
                CardTrigger.OnTick, new[] { SynergyTag.Mycelium, SynergyTag.Territory },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Area, Shape = EffectShape.Circle,
                    Value = 0f, Radius = 4f, Duration = 12f,
                    Status = SimStatus.OnMycelium,
                    TargetFaction = SimFaction.None,
                    Affixes = new[] { AffixKind.Lingering },
                },
                interval: 4f);

            AddCard(reg, 5002, "领地分泌", CardRoute.Nest, CardRarity.Rare, 3, 1,
                "菌毯区域内吞噬收益提高。",
                CardTrigger.Passive, new[] { SynergyTag.Mycelium, SynergyTag.Devour },
                rules: new[] { RuleFlag.MyceliumBoostsDevour });

            // ── 异化污染 ──
            AddCard(reg, 6001, "污染腺体", CardRoute.Corrupt, CardRarity.Common, 3, 3,
                "释放技能会积累污染度，污染度提高技能伤害。",
                CardTrigger.OnAbilityCast, new[] { SynergyTag.Pollution },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Resource, Shape = EffectShape.Self,
                    Resource = ResourceKind.Pollution, Value = 1.5f,
                });

            AddCard(reg, 6002, "组织透支", CardRoute.Corrupt, CardRarity.Rare, 3, 2,
                "立刻获得大量进化能，之后进入偿还期。",
                CardTrigger.OnLevelUp, new[] { SynergyTag.Risk, SynergyTag.Burst },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Resource, Shape = EffectShape.Self,
                    Resource = ResourceKind.EvoEnergy, Value = 40f,
                },
                drawback: "最大生命 -10%。",
                drawbackMod: new StatModifier(StatId.MaxHealth, ModifierOp.PctAdd, -0.1f));

            AddCard(reg, 6003, "原核王冠", CardRoute.Corrupt, CardRarity.Legacy, 4, 1,
                "污染度满时不再失败，而是进入霸主形态，持续消耗生命。",
                CardTrigger.Passive, new[] { SynergyTag.Pollution, SynergyTag.Risk },
                rules: new[] { RuleFlag.PollutionBecomesOverlord },
                grantAbility: 8,
                drawback: "霸主形态每秒消耗 2% 最大生命。");

            // ── 跨路线联动 ──
            AddCard(reg, 7001, "电解脊刺", CardRoute.Hybrid, CardRarity.Rare, 3, 2,
                "近战命中导电目标时引爆电荷。",
                CardTrigger.OnHit, new[] { SynergyTag.Electric, SynergyTag.Devour },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Damage, Shape = EffectShape.Circle,
                    Value = 26f, Radius = 3.5f,
                    RequireStatus = SimStatus.Conductive,
                    Affixes = new[] { AffixKind.Conductive },
                });

            AddCard(reg, 7002, "异化突变", CardRoute.Hybrid, CardRarity.Legacy, 4, 1,
                "每次进化随机强化一个系统并弱化另一个。",
                CardTrigger.OnLevelUp, new[] { SynergyTag.Risk },
                effect: new EffectSpec
                {
                    Kind = EffectKind.Stat, Shape = EffectShape.Self,
                    Stat = StatId.AbilityPower, Op = ModifierOp.PctAdd, Value = 0.12f,
                    ScaleWithPower = false,
                },
                drawback: "每次进化同时降低一项其它属性。");
        }

        /// <summary>
        /// 卡牌构造辅助。参数多但集中在一处，比每张卡写 15 行初始化可读。
        /// </summary>
        private static void AddCard(DataRegistry reg, int id, string name,
            CardRoute route, CardRarity rarity, int unlockPhase, int maxStack,
            string desc, CardTrigger trigger, string[] synergy,
            EffectSpec effect = null, StatModifier? statMod = null,
            RuleFlag[] rules = null, int grantAbility = 0,
            string drawback = null, StatModifier? drawbackMod = null,
            float interval = 1f, bool isPureStat = false)
        {
            var card = new CardSpec
            {
                Id = id, Name = name, Desc = desc,
                Route = route, Rarity = rarity,
                UnlockPhase = unlockPhase, MaxStack = maxStack,
                Trigger = trigger, TriggerInterval = interval,
                SynergyTags = synergy,
                GrantAbilityId = grantAbility,
                RuleFlags = rules,
                DrawbackDesc = drawback,
                IsPureStatCard = isPureStat,
            };

            if (effect != null)
            {
                card.Effects.Add(effect);
            }
            if (statMod.HasValue)
            {
                card.StatMods.Add(statMod.Value);
            }
            if (drawbackMod.HasValue)
            {
                card.DrawbackMods = new System.Collections.Generic.List<StatModifier>(1)
                {
                    drawbackMod.Value,
                };
            }

            reg.AddCard(card);
        }

        private static void Phases(DataRegistry reg)
        {
            reg.AddPhase(new PhaseSpec
            {
                Id = 1, Name = "原生漂流期", Duration = 480f,
                FlavorText = "你仍只是水流中的一点微光。先学会吞噬，再学会不被吞噬。",
                PressureBase = 35f, PressureFloor = 12f,
                EnemyPool = new[] { 1, 2, 3, 4 },
                EcoEventPool = new[] { 1, 3 },
            });

            reg.AddPhase(new PhaseSpec
            {
                Id = 2, Name = "初次突变期", Duration = 600f,
                FlavorText = "细胞膜开始记住危险。每一次吞噬都在把你推向某种形态。",
                PressureBase = 45f, PressureFloor = 26f,
                EnemyPool = new[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                EcoEventPool = new[] { 1, 2, 3, 4 },
                SpawnEliteAtEnd = true, EliteEnemyId = 50,
            });

            reg.AddPhase(new PhaseSpec
            {
                Id = 3, Name = "生态争夺期", Duration = 720f,
                FlavorText = "这片水域不再只是食物场。其他生命也在学习你的节奏。",
                PressureBase = 85f, PressureFloor = 50f,
                EnemyPool = new[] { 2, 3, 4, 5, 6, 7, 8, 11 },
                EcoEventPool = new[] { 1, 2, 3, 5, 6 },
                SpawnEliteAtEnd = true, EliteEnemyId = 51,
            });

            reg.AddPhase(new PhaseSpec
            {
                Id = 4, Name = "异化扩张期", Duration = 900f,
                FlavorText = "你开始改变周围的环境。水流、尸体、菌毯和电荷都可以成为器官的延伸。",
                PressureBase = 140f, PressureFloor = 85f,
                EnemyPool = new[] { 4, 5, 6, 7, 8, 9, 10, 11 },
                EcoEventPool = new[] { 2, 4, 5, 6, 7 },
                SpawnEliteAtEnd = true, EliteEnemyId = 52,
            });

            reg.AddPhase(new PhaseSpec
            {
                Id = 5, Name = "微观灾变期", Duration = 660f,
                FlavorText = "生态正在反扑。你已经不是猎物，但还不是唯一的答案。",
                PressureBase = 220f, PressureFloor = 140f,
                EnemyPool = new[] { 5, 6, 7, 8, 9, 10, 11 },
                EcoEventPool = new[] { 2, 5, 6, 7, 8 },
            });

            reg.AddPhase(new PhaseSpec
            {
                Id = 6, Name = "原核霸主战", Duration = 240f,
                FlavorText = "这片微观海域只允许一个意志留下。",
                PressureBase = 180f, PressureFloor = 120f,
                EnemyPool = new[] { 5, 9, 10 },
                EcoEventPool = new int[0],
                SpawnEliteAtEnd = false, EliteEnemyId = 90,
            });
        }

        private static void EcoEvents(DataRegistry reg)
        {
            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 1, Name = "营养潮汐", Duration = 50f,
                Desc = "地图出现大量食物，但大型威胁同步增加。",
                PressureMul = 1.35f, DevourGainMul = 1.3f,
                RewardKind = ResourceKind.Nutrient, RewardAmount = 30f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 2, Name = "酸雨漂流", Duration = 45f,
                Desc = "随机区域持续腐蚀。",
                PressureMul = 1.1f,
                RewardKind = ResourceKind.Mutagen, RewardAmount = 2f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 3, Name = "低氧窗口", Duration = 40f,
                Desc = "敌我移动变慢，吞噬收益提高。",
                PlayerSpeedMul = 0.8f, EnemySpeedMul = 0.6f, DevourGainMul = 1.5f,
                RewardKind = ResourceKind.EvoEnergy, RewardAmount = 40f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 4, Name = "孢子爆季", Duration = 55f,
                Desc = "孢子类敌人与资源同时出现。",
                PressureMul = 1.25f, FavoredRoute = CardRoute.Spore,
                RewardKind = ResourceKind.EvoEnergy, RewardAmount = 30f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 5, Name = "电荷风暴", Duration = 50f,
                Desc = "地图生成导电区。",
                PressureMul = 1.2f, FavoredRoute = CardRoute.Electric,
                RewardKind = ResourceKind.EvoEnergy, RewardAmount = 30f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 6, Name = "尸潮", Duration = 45f,
                Desc = "场上尸体不再消失，可持续吞噬。",
                DevourGainMul = 1.4f, FavoredRoute = CardRoute.Devour,
                RewardKind = ResourceKind.Nutrient, RewardAmount = 25f,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 7, Name = "污染裂隙", Duration = 50f,
                Desc = "异化卡出现率提高，但污染度上升。",
                PressureMul = 1.3f, FavoredRoute = CardRoute.Corrupt,
                GrantsDraft = true, DraftKind = DraftKind.Corrupt,
            });

            reg.AddEcoEvent(new EcoEventSpec
            {
                Id = 8, Name = "原核遗响", Duration = 30f,
                Desc = "出现一次原核遗产卡选择。",
                GrantsDraft = true, DraftKind = DraftKind.Legacy,
            });
        }

        /// <summary>首领三阶段（TR-cell-011）。与 tools/cell_tables/step3_enemy_ability.py 的 BOSSPHASE 同构。</summary>
        private static void BossPhases(DataRegistry reg)
        {
            reg.AddBossPhase(new BossPhaseSpec
            {
                Id = 900, BossEnemyId = 90, PhaseIndex = 0, Name = "蛰伏",
                HpThreshold = 1.0f, ArchetypeIndex = ArcChase,
            });
            reg.AddBossPhase(new BossPhaseSpec
            {
                Id = 901, BossEnemyId = 90, PhaseIndex = 1, Name = "狂暴",
                HpThreshold = 0.66f, ArchetypeIndex = ArcBossEnrage,
            });
            reg.AddBossPhase(new BossPhaseSpec
            {
                Id = 902, BossEnemyId = 90, PhaseIndex = 2, Name = "终焉",
                HpThreshold = 0.33f, ArchetypeIndex = ArcBossFinal,
            });
        }
    }
}
