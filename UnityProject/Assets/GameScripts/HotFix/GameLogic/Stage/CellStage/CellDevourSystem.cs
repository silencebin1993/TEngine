using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Structural;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stats;
using GameLogic.UI.Battle;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Stage.CellStage
{
    /// <summary>
    /// 吞噬结算。细胞阶段的核心动词（Spec §2）。
    ///
    /// 内核只负责筛出"体积门槛内且接触到的候选"（JobDevourScan），
    /// 本模块负责玩法结算：资源、体积成长、连吃层数、信号广播。
    /// 这个分工是刻意的——内核不该知道"营养质"是什么。
    /// </summary>
    public sealed class CellDevourSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Resolution;

        private SimBridge _sim;
        private StatSheet _stats;
        private ResourceWallet _wallet;
        private EcoEventScheduler _events;
        private StageStatistics _stats2;
        private AreaZoneSystem _zones;
        private MinionRegistry _minions;

        /// <summary>连吃层数。断连后清空（除非 ComboNeverResets 规则开启）。</summary>
        public int Combo { get; private set; }
        private float _comboTimer;

        /// <summary>连吃断连时间。</summary>
        private const float ComboWindow = 2.5f;

        /// <summary>体积成长系数：吞噬目标体积的多少比例转为自身体积。</summary>
        private const float VolumeGrowthRatio = 0.055f;

        /// <summary>吞噬现在门控于该器官（人直接指示，2026-08-25，非 sprint-027 范围）：
        /// 未装备并激活时，接触到的候选不结算，敌人不受影响。</summary>
        private const string DevourOrganId = "org_phago";

        public void Bind(SimBridge sim, StatSheet stats, ResourceWallet wallet,
            EcoEventScheduler events, StageStatistics statistics, AreaZoneSystem zones,
            MinionRegistry minions)
        {
            _sim = sim;
            _stats = stats;
            _wallet = wallet;
            _events = events;
            _stats2 = statistics;
            _zones = zones;
            _minions = minions;
        }

        public override void OnEnter()
        {
            Combo = 0;
            _comboTimer = 0f;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            // 连吃计时
            if (Combo > 0)
            {
                _comboTimer -= dt;
                if (_comboTimer <= 0f && !RuleFlags.Current.Has(Ability.RuleFlag.ComboNeverResets))
                {
                    Combo = 0;
                }
            }

            SimSnapshot snap = _sim.Snapshot;

            // 处理本帧吞噬候选。内核已按体积门槛筛过，这里直接结算——
            // 但只有当前激活器官是吞噬体时才生效，否则候选照常产出但不消费。
            bool devourActive = GameLogic.UI.Battle.MetabolicSlicePanel.Instance
                ?.CarrierRegistry?.ActiveCarrier?.OrganelleId == DevourOrganId;
            if (devourActive)
            {
                int n = snap.DevourCandidateCount;
                for (int i = 0; i < n; i++)
                {
                    int idx = snap.DevourCandidates[i];
                    if (idx < 0 || idx >= snap.Count || snap.Alive[idx] == 0)
                    {
                        continue;
                    }
                    Consume(idx, in snap);
                }
            }

            // 玩家受到的接触伤害结算
            float contact = snap.PlayerContactDamage;
            if (contact > 0f)
            {
                float taken = contact * (_stats?.Get(StatId.DamageTaken) ?? 1f);
                _sim.DamagePlayer(taken);

                if (_stats2 != null)
                {
                    _stats2.TotalDamageTaken += taken;
                }

                float maxHp = _stats?.Get(StatId.MaxHealth) ?? 100f;
                float hp = _sim.PlayerHealth;
                Signals.Publish(new PlayerHurtSignal
                {
                    Amount = taken,
                    HealthAfter = hp,
                    HealthPercent = maxHp > 0f ? hp / maxHp : 0f,
                });
            }

            // 命中事件广播（卡牌 OnHit 触发、命中反馈；story-002 修复：此前从未 Publish）
            PumpHits(in snap);

            // 死亡事件结算（击杀奖励、卡牌 OnKill）
            ResolveDeaths(in snap);
        }

        /// <summary>把内核本帧命中事件广播为 <see cref="HitSignal"/>。紧邻 Resolution 层其它广播，不放 SimBridge（该类不循环）。</summary>
        private void PumpHits(in SimSnapshot snap)
        {
            int n = snap.HitCount;
            for (int i = 0; i < n; i++)
            {
                HitEvent h = snap.Hits[i];
                Signals.Publish(new HitSignal
                {
                    TargetLogicId = h.TargetLogicId,
                    Position = h.Position,
                    Damage = h.Damage,
                    Lethal = h.Lethal,
                });
            }
        }

        private void Consume(int idx, in SimSnapshot snap)
        {
            float targetVolume = snap.Radius[idx];
            SimFaction faction = snap.FactionOf(idx);
            int enemyId = SpawnDirector.DecodeEnemyId(snap.LogicId[idx]);
            EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);

            bool isCorpse = faction == SimFaction.Pickup;

            // 连吃层数
            Combo++;
            _comboTimer = ComboWindow;
            if (_stats2 != null && Combo > _stats2.MaxDevourCombo)
            {
                _stats2.MaxDevourCombo = Combo;
            }

            // 收益。连吃层数给递增加成（吞噬路线的资源引擎）
            float comboMul = 1f + Mathf.Min(Combo - 1, 10) * 0.06f;
            float gainMul = (_stats?.Get(StatId.DevourGain) ?? 1f)
                            * comboMul
                            * (_events?.DevourGainMul ?? 1f);

            // 菌毯加成：领地分泌卡开启后，站在自己的菌毯上吞噬收益更高。
            // 这是菌毯路线（Spec §7.5）与吞噬路线的联动兑现点。
            if (RuleFlags.Current.Has(Ability.RuleFlag.MyceliumBoostsDevour)
                && _zones != null
                && _zones.PlayerInZone(AreaZoneSystem.ZoneKind.Mycelium))
            {
                gainMul *= 1.35f;
            }

            if (spec != null)
            {
                _wallet?.Add(ResourceKind.EvoEnergy, spec.EvoEnergy * gainMul);
                _wallet?.Add(ResourceKind.Nutrient, spec.Nutrient * gainMul);
            }
            else
            {
                // 没有配置的目标（如脱壳留下的旧壳）给保底收益
                _wallet?.Add(ResourceKind.EvoEnergy, 1f * gainMul);
            }

            // 体积成长
            GrowVolume(targetVolume);

            if (_stats2 != null)
            {
                _stats2.FoodDevoured++;
            }

            // 位置必须在 ConsumeUnit 之前读：snap.Position 是内核 NativeArray 的实时视图，
            // ConsumeUnit → KillUnit → ReleaseSlot 会把该槽位坐标改写成越界哨兵值，
            // 顺序颠倒会让 DevourSignal.Position 变成垃圾值（越界 worldAABB 的根因）。
            float2 devourPosition = snap.Position[idx];

            // 从内核移除
            _sim.ConsumeUnit(idx);

            Signals.Publish(new DevourSignal
            {
                UnitIndex = idx,
                Position = devourPosition,
                TargetVolume = targetVolume,
                TargetFaction = faction,
                ComboCount = Combo,
                IsCorpse = isCorpse,
                EnemyId = enemyId,
            });
        }

        /// <summary>
        /// 体积成长。体积是细胞阶段独有的核心状态量：
        /// 它同时是攻击力、防御力和移动惩罚（Spec §5）。
        /// </summary>
        private void GrowVolume(float targetVolume)
        {
            if (_stats == null)
            {
                return;
            }

            // 改 base 而不是加修正器：体积成长是永久的，而修正器是给卡牌/状态用的。
            // 混用会让"卡牌 +20% 体积"在每次吞噬后被重复放大。
            float old = _stats.Get(StatId.Volume);
            _stats.SetBase(StatId.Volume, _stats.GetBase(StatId.Volume)
                                          + targetVolume * VolumeGrowthRatio);

            float now = _stats.Get(StatId.Volume);
            if (_stats2 != null && now > _stats2.PeakVolume)
            {
                _stats2.PeakVolume = now;
            }

            if (!Mathf.Approximately(old, now))
            {
                Signals.Publish(new VolumeChangedSignal { OldVolume = old, NewVolume = now });
            }
        }

        /// <summary>
        /// 死亡事件结算。非吞噬致死（放电、投射物、毒区）走这里。
        /// </summary>
        private void ResolveDeaths(in SimSnapshot snap)
        {
            int n = snap.DeathCount;
            for (int i = 0; i < n; i++)
            {
                DeathEvent d = snap.Deaths[i];

                if (d.Faction == SimFaction.PlayerMinion)
                {
                    // 附属体死亡（战损或自然消亡）：归还 MinionCap 配额，不结算奖励。
                    _minions?.Release();
                    continue;
                }

                if (d.Faction != SimFaction.Hostile)
                {
                    continue;
                }

                int enemyId = SpawnDirector.DecodeEnemyId(d.LogicId);
                EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);

                bool elite = (d.StatusAtDeath & SimStatus.Elite) != 0;
                bool boss = (d.StatusAtDeath & SimStatus.Boss) != 0;

                if (spec != null)
                {
                    float mul = _stats?.Get(StatId.DevourGain) ?? 1f;
                    _wallet?.Add(ResourceKind.EvoEnergy, spec.EvoEnergy * mul);
                    _wallet?.Add(ResourceKind.Nutrient, spec.Nutrient * mul);
                    if (spec.Mutagen > 0f)
                    {
                        _wallet?.Add(ResourceKind.Mutagen, spec.Mutagen);
                    }
                }

                // 结构器官掉落（organelle-structural-tier story-002 Required 1）：任意 Hostile 死亡
                // 均判定一次，紧跟资源结算之后、KillSignal 广播之前（preflight-decisions.md #1）。
                TryDropStructuralOrgan();

                // 击杀回复
                float killHeal = _stats?.Get(StatId.KillHeal) ?? 0f;
                if (killHeal > 0f)
                {
                    _sim.HealPlayer(killHeal, _stats.Get(StatId.MaxHealth));
                }

                if (_stats2 != null)
                {
                    _stats2.EnemiesKilled++;
                    if (elite)
                    {
                        _stats2.ElitesKilled++;
                    }
                    // 致死来源分流（DeathEvent.CauseKind）：吞噬清除 vs 伤害耗尽。
                    if (d.CauseKind == DeathCauseKind.Devour)
                    {
                        _stats2.EnemiesKilledByDevour++;
                    }
                }

                // 尸体可食规则：留下一个可吞噬的残块
                if (RuleFlags.Current.Has(Ability.RuleFlag.CorpseEdible) && d.Radius > 0.3f)
                {
                    SpawnCorpse(d);
                }

                Signals.Publish(new KillSignal
                {
                    LogicId = d.LogicId,
                    ArchetypeId = d.ArchetypeId,
                    Position = d.Position,
                    StatusAtDeath = d.StatusAtDeath,
                    WasElite = elite,
                    WasBoss = boss,
                });
            }
        }

        /// <summary>结构器官掉落触发概率（preflight-decisions.md #2：8%，非平衡终值）。</summary>
        private const float StructuralDropChance = 0.08f;

        /// <summary>结构器官掉落表：8 条等权重（CATALOG.md §B 的 id→槽标签映射，非平衡终值）。</summary>
        private static readonly (string Id, VisualSlotTag Tag)[] StructuralDropTable =
        {
            ("org_carapace", VisualSlotTag.Armor),
            ("org_flagellum_boost", VisualSlotTag.Motility),
            ("org_thick_membrane", VisualSlotTag.Armor),
            ("org_regen_gland", VisualSlotTag.Vital),
            ("org_chemoreceptor", VisualSlotTag.Appendage),
            ("org_efficient_gut", VisualSlotTag.Vital),
            ("org_calm_membrane", VisualSlotTag.Armor),
            ("org_stamina_sac", VisualSlotTag.Motility),
        };

        /// <summary>拾取即装备，不进抽卡池/消化泡（DESIGN §7）。8% 概率、等权重取一条，直接调用
        /// <see cref="StructuralOrganService.Equip"/>；同标签有旧件按替换语义处理并推送提示（preflight #6）。</summary>
        private void TryDropStructuralOrgan()
        {
            if (UnityEngine.Random.Range(0f, 1f) > StructuralDropChance)
            {
                return;
            }

            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                // 非战斗中不掉落（防御性判空，preflight #1）。
                return;
            }

            (string organId, VisualSlotTag tag) = StructuralDropTable[UnityEngine.Random.Range(0, StructuralDropTable.Length)];

            PartInstance old = panel.Structural.Get(tag);
            var part = new PartInstance(System.Guid.NewGuid().ToString("N"), organId, PartLocation.Bag());
            if (panel.Bag.TryAdd(part) != AddResult.Added)
            {
                return;
            }

            StructuralOrganResult result = StructuralOrganService.Equip(panel.Bag, panel.Structural, _stats, part.PartId, tag);
            if (result != StructuralOrganResult.Ok)
            {
                return;
            }

            if (old != null)
            {
                string oldName = OrganelleCatalog.Get(old.CardDefId)?.DisplayName ?? old.CardDefId;
                string newName = OrganelleCatalog.Get(organId)?.DisplayName ?? organId;
                panel.PendingStructuralReplaceHint = $"{StructuralTagDisplayName(tag)}槽已替换：{oldName} → {newName}";
            }

            GameEvent.Send(MetabolicSlicePanel.InventoryChangedEvent);
        }

        /// <summary>4 槽中文短名（preflight #7），仅本掉落提示用，不改枚举本身。</summary>
        private static string StructuralTagDisplayName(VisualSlotTag tag) => tag switch
        {
            VisualSlotTag.Armor => "护甲",
            VisualSlotTag.Motility => "运动",
            VisualSlotTag.Vital => "生机",
            VisualSlotTag.Appendage => "附肢",
            _ => tag.ToString(),
        };

        /// <summary>生成可二次吞噬的组织残块。吞噬路线与尸体联动的基础。</summary>
        private void SpawnCorpse(in DeathEvent d)
        {
            _sim.Spawn(new SpawnRequest
            {
                Position = d.Position,
                Velocity = Unity.Mathematics.float2.zero,
                Health = 1f,
                Radius = d.Radius * 0.45f,
                MaxSpeed = 0f,
                ArchetypeId = 0,
                Faction = SimFaction.Pickup,
                InitialStatus = SimStatus.None,
                LogicId = SpawnDirector.EncodeLogicId(1),
                VisualId = 20,
            });
        }
    }
}
