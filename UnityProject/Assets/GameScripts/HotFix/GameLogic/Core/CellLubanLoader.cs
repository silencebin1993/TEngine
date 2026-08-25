using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;

namespace GameLogic.Core
{
    /// <summary>
    /// Luban cell.* 表 → 本层 Spec 类型的映射。
    ///
    /// **这是整个工程里唯一直接引用 GameConfig.cell.* 的地方。**
    /// 玩法代码只认 CardSpec/AbilitySpec/EnemySpec，所以配置源可以整体替换
    /// （Luban / JSON / 内置默认）而调用方一行不改。见框架文档 §6。
    ///
    /// 枚举全部用 (目标类型)(int) 强转：Luban 枚举与 C# 枚举的成员名和值
    /// 都由 tools/cell_tables/enums.py 保证逐一对齐，所以按值转是安全的。
    /// 若两边不同步，会在 DataRegistry.Validate 里以断链形式暴露。
    /// </summary>
    internal static class CellLubanLoader
    {
        public static bool TryLoad(DataRegistry reg)
        {
            var tables = ConfigSystem.Instance.Tables;
            if (tables == null)
            {
                return false;
            }

            // 任一核心表为空就视为表未就绪，回落内置内容
            if (tables.TbCard.DataList.Count == 0
                || tables.TbCellEnemy.DataList.Count == 0
                || tables.TbBehaviorArchetype.DataList.Count == 0
                || tables.TbPhase.DataList.Count == 0)
            {
                return false;
            }

            LoadArchetypes(reg, tables);
            LoadEnemies(reg, tables);
            LoadBossPhases(reg, tables);
            LoadAbilities(reg, tables);
            LoadCards(reg, tables);
            LoadPhases(reg, tables);
            LoadEcoEvents(reg, tables);
            return true;
        }

        private static void LoadArchetypes(DataRegistry reg, GameConfig.Tables t)
        {
            // 按 id 升序注册，使注册后的索引与配置里的 id 一致——
            // 敌人表用 archetypeIndex 引用它，两者必须对得上。
            var list = new List<GameConfig.cell.BehaviorArchetype>(t.TbBehaviorArchetype.DataList);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));

            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.Id != i)
                {
                    TEngine.Log.Error(
                        $"[CellLuban] 行为原型 id 必须从 0 连续递增，实际第 {i} 项 id={a.Id}。" +
                        "敌人表的 archetypeIndex 会指错。");
                }
                reg.AddArchetype(new BehaviorArchetype
                {
                    Kind = (BehaviorKind)(int)a.Kind,
                    Accel = a.Accel,
                    TurnRate = a.TurnRate,
                    AggroRange = a.AggroRange,
                    AttackRange = a.AttackRange,
                    AttackCooldown = a.AttackCooldown,
                    AttackDamage = a.AttackDamage,
                    Separation = a.Separation,
                    PreferredRange = a.PreferredRange,
                    ChargeTelegraph = a.ChargeTelegraph,
                    ChargeSpeedMul = a.ChargeSpeedMul,
                    WanderStrength = a.WanderStrength,
                });
            }
        }

        private static void LoadEnemies(DataRegistry reg, GameConfig.Tables t)
        {
            foreach (var e in t.TbCellEnemy.DataList)
            {
                reg.AddEnemy(new EnemySpec
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = e.Desc,
                    ArchetypeIndex = e.ArchetypeIndex,
                    Health = e.Health,
                    Radius = e.Radius,
                    MaxSpeed = e.MaxSpeed,
                    SpawnCost = e.SpawnCost,
                    MinPhase = e.MinPhase,
                    MaxPhase = e.MaxPhase,
                    EvoEnergy = e.EvoEnergy,
                    Nutrient = e.Nutrient,
                    Mutagen = e.Mutagen,
                    InitialStatus = ToStatus(e.InitialStatus),
                    VisualId = e.VisualId,
                    IsElite = e.IsElite,
                    IsBoss = e.IsBoss,
                });
            }
        }

        private static void LoadBossPhases(DataRegistry reg, GameConfig.Tables t)
        {
            foreach (var p in t.TbBossPhase.DataList)
            {
                reg.AddBossPhase(new BossPhaseSpec
                {
                    Id = p.Id,
                    BossEnemyId = p.BossEnemyId,
                    PhaseIndex = p.PhaseIndex,
                    Name = p.Name,
                    HpThreshold = p.HpThreshold,
                    ArchetypeIndex = p.ArchetypeId,
                });
            }
        }

        private static void LoadAbilities(DataRegistry reg, GameConfig.Tables t)
        {
            // 先按 abilityId 分组效果，避免每个技能都全表扫一遍
            var byAbility = new Dictionary<int, List<GameConfig.cell.AbilityEffect>>(64);
            foreach (var ef in t.TbAbilityEffect.DataList)
            {
                if (!byAbility.TryGetValue(ef.AbilityId, out var list))
                {
                    list = new List<GameConfig.cell.AbilityEffect>(3);
                    byAbility[ef.AbilityId] = list;
                }
                list.Add(ef);
            }

            foreach (var a in t.TbAbility.DataList)
            {
                var spec = new AbilitySpec
                {
                    Id = a.Id,
                    Name = a.Name,
                    Desc = a.Desc,
                    Cooldown = a.Cooldown,
                    Charges = a.Charges,
                    StaminaCost = a.StaminaCost,
                    CastRange = a.CastRange,
                    TargetMode = (TargetMode)(int)a.TargetMode,
                    Tags = a.Tags != null ? a.Tags.ToArray() : null,
                    IsStarter = a.IsStarter,
                };

                if (byAbility.TryGetValue(a.Id, out var effects))
                {
                    effects.Sort((x, y) => x.Order.CompareTo(y.Order));
                    for (int i = 0; i < effects.Count; i++)
                    {
                        spec.Effects.Add(ToEffect(effects[i]));
                    }
                }

                reg.AddAbility(spec);
            }
        }

        private static void LoadCards(DataRegistry reg, GameConfig.Tables t)
        {
            var effectsBy = new Dictionary<int, List<GameConfig.cell.CardEffect>>(160);
            foreach (var ef in t.TbCardEffect.DataList)
            {
                if (!effectsBy.TryGetValue(ef.CardId, out var list))
                {
                    list = new List<GameConfig.cell.CardEffect>(2);
                    effectsBy[ef.CardId] = list;
                }
                list.Add(ef);
            }

            var statsBy = new Dictionary<int, List<GameConfig.cell.CardStat>>(160);
            foreach (var st in t.TbCardStat.DataList)
            {
                if (!statsBy.TryGetValue(st.CardId, out var list))
                {
                    list = new List<GameConfig.cell.CardStat>(2);
                    statsBy[st.CardId] = list;
                }
                list.Add(st);
            }

            foreach (var c in t.TbCard.DataList)
            {
                // combat-identity-rework story-007（Required 4）：卡表（Luban，tools/cell_tables/）
                // 仍在引用已迁 gene_*/已退役的旧 id（本机 Smart App Control 拦截 Luban codegen，
                // 表内容改不动，见 qa/evidence 006），在这唯一的 cell.* → CardSpec 翻译口子上过滤，
                // 使旧 id 不产出 CardSpec，天然不进抽卡池/图鉴，不依赖表重生。
                if (IsRetiredOrUnknownContent((ContentKind)(int)c.ContentKind, c.ContentId))
                {
                    continue;
                }

                var spec = new CardSpec
                {
                    Id = c.Id,
                    Name = c.Name,
                    Desc = c.Desc,
                    Route = (CardRoute)(int)c.Route,
                    Rarity = (CardRarity)(int)c.Rarity,
                    UnlockPhase = c.UnlockPhase,
                    MaxStack = c.MaxStack,
                    Trigger = (CardTrigger)(int)c.Trigger,
                    TriggerInterval = c.TriggerInterval,
                    TriggerChance = c.TriggerChance,
                    TriggerCooldown = c.TriggerCooldown,
                    GrantAbilityId = c.GrantAbilityId,
                    SynergyTags = c.SynergyTags != null ? c.SynergyTags.ToArray() : null,
                    PollutionCost = c.PollutionCost,
                    DrawbackDesc = c.DrawbackDesc,
                    IsPureStatCard = c.IsPureStatCard,
                    ContentKind = (ContentKind)(int)c.ContentKind,
                    ContentId = c.ContentId,
                };

                if (c.RuleFlags != null && c.RuleFlags.Count > 0)
                {
                    var flags = new RuleFlag[c.RuleFlags.Count];
                    for (int i = 0; i < c.RuleFlags.Count; i++)
                    {
                        flags[i] = (RuleFlag)(int)c.RuleFlags[i];
                    }
                    spec.RuleFlags = flags;
                }

                if (effectsBy.TryGetValue(c.Id, out var effects))
                {
                    effects.Sort((x, y) => x.Order.CompareTo(y.Order));
                    for (int i = 0; i < effects.Count; i++)
                    {
                        spec.Effects.Add(ToEffect(effects[i]));
                    }
                }

                if (statsBy.TryGetValue(c.Id, out var stats))
                {
                    for (int i = 0; i < stats.Count; i++)
                    {
                        var mod = new StatModifier(
                            (StatId)(int)stats[i].Stat,
                            (ModifierOp)(int)stats[i].Op,
                            stats[i].Value,
                            c.Id);
                        if (stats[i].IsDrawback)
                        {
                            spec.DrawbackMods ??= new List<StatModifier>(2);
                            spec.DrawbackMods.Add(mod);
                        }
                        else
                        {
                            spec.StatMods.Add(mod);
                        }
                    }
                }

                reg.AddCard(spec);
            }
        }

        /// <summary>combat-identity-rework story-007（Required 4）：Organelle 内容缺目录条目或
        /// <see cref="OrganelleDef.IsRetired"/> 时不可抽/不可授予；Gene 内容不在 Contract/Module
        /// 两张子表任一（例如已删除的 gene_double/mute/edge/share）同样视为退役。</summary>
        private static bool IsRetiredOrUnknownContent(ContentKind kind, string contentId)
        {
            switch (kind)
            {
                case ContentKind.Organelle:
                    OrganelleDef organelle = OrganelleCatalog.Get(contentId);
                    return organelle == null || organelle.IsRetired;
                case ContentKind.Gene:
                    return GeneCatalog.Get(contentId) == null && GeneCatalog.GetModule(contentId) == null;
                default:
                    return false;
            }
        }

        private static void LoadPhases(DataRegistry reg, GameConfig.Tables t)
        {
            var list = new List<GameConfig.cell.Phase>(t.TbPhase.DataList);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));

            foreach (var p in list)
            {
                reg.AddPhase(new PhaseSpec
                {
                    Id = p.Id,
                    Name = p.Name,
                    FlavorText = p.FlavorText,
                    Duration = p.Duration,
                    PressureBase = p.PressureBase,
                    PressureFloor = p.PressureFloor,
                    EnemyPool = p.EnemyPool != null ? p.EnemyPool.ToArray() : new int[0],
                    EcoEventPool = p.EcoEventPool != null ? p.EcoEventPool.ToArray() : new int[0],
                    SpawnEliteAtEnd = p.SpawnEliteAtEnd,
                    EliteEnemyId = p.EliteEnemyId,
                });
            }
        }

        private static void LoadEcoEvents(DataRegistry reg, GameConfig.Tables t)
        {
            foreach (var ev in t.TbEcoEvent.DataList)
            {
                reg.AddEcoEvent(new EcoEventSpec
                {
                    Id = ev.Id,
                    Name = ev.Name,
                    Desc = ev.Desc,
                    Duration = ev.Duration,
                    PressureMul = ev.PressureMul,
                    PlayerSpeedMul = ev.PlayerSpeedMul,
                    EnemySpeedMul = ev.EnemySpeedMul,
                    DevourGainMul = ev.DevourGainMul,
                    FavoredRoute = (CardRoute)(int)ev.FavoredRoute,
                    RewardKind = (ResourceKind)(int)ev.RewardKind,
                    RewardAmount = ev.RewardAmount,
                    GrantsDraft = ev.GrantsDraft,
                    DraftKind = (DraftKind)(int)ev.DraftKind,
                });
            }
        }

        /// <summary>AbilityEffect 与 CardEffect 字段同构，各自转一次。</summary>
        private static EffectSpec ToEffect(GameConfig.cell.AbilityEffect e)
        {
            return new EffectSpec
            {
                Kind = (EffectKind)(int)e.Kind,
                Shape = (EffectShape)(int)e.Shape,
                Value = e.Value,
                Radius = e.Radius,
                Duration = e.Duration,
                Count = e.Count,
                Status = ToStatus(e.Status),
                RequireStatus = ToStatus(e.RequireStatus),
                TargetFaction = (SimFaction)(int)e.TargetFaction,
                Stat = (StatId)(int)e.Stat,
                Op = (ModifierOp)(int)e.Op,
                Resource = (ResourceKind)(int)e.Resource,
                SpawnEnemyId = e.SpawnEnemyId,
                Rule = (RuleFlag)(int)e.Rule,
                Affixes = ToAffixes(e.Affixes),
                ScaleWithPower = e.ScaleWithPower,
            };
        }

        private static EffectSpec ToEffect(GameConfig.cell.CardEffect e)
        {
            return new EffectSpec
            {
                Kind = (EffectKind)(int)e.Kind,
                Shape = (EffectShape)(int)e.Shape,
                Value = e.Value,
                Radius = e.Radius,
                Duration = e.Duration,
                Count = e.Count,
                Status = ToStatus(e.Status),
                RequireStatus = ToStatus(e.RequireStatus),
                TargetFaction = (SimFaction)(int)e.TargetFaction,
                Stat = (StatId)(int)e.Stat,
                Op = (ModifierOp)(int)e.Op,
                Resource = (ResourceKind)(int)e.Resource,
                SpawnEnemyId = e.SpawnEnemyId,
                Rule = (RuleFlag)(int)e.Rule,
                Affixes = ToAffixes(e.Affixes),
                ScaleWithPower = e.ScaleWithPower,
            };
        }

        /// <summary>状态列表 → 位掩码。配置里写 "Unedible|Elite"，这里 OR 起来。</summary>
        private static SimStatus ToStatus(List<GameConfig.cell.EStatus> list)
        {
            if (list == null || list.Count == 0)
            {
                return SimStatus.None;
            }
            uint mask = 0u;
            for (int i = 0; i < list.Count; i++)
            {
                mask |= (uint)list[i];
            }
            return (SimStatus)mask;
        }

        private static AffixKind[] ToAffixes(List<GameConfig.cell.EAffix> list)
        {
            if (list == null || list.Count == 0)
            {
                return null;
            }
            var arr = new AffixKind[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                arr[i] = (AffixKind)(int)list[i];
            }
            return arr;
        }
    }
}
