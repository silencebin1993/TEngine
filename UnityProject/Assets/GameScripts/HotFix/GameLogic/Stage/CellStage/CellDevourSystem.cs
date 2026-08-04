using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stats;
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

        /// <summary>连吃层数。断连后清空（除非 ComboNeverResets 规则开启）。</summary>
        public int Combo { get; private set; }
        private float _comboTimer;

        /// <summary>连吃断连时间。</summary>
        private const float ComboWindow = 2.5f;

        /// <summary>体积成长系数：吞噬目标体积的多少比例转为自身体积。</summary>
        private const float VolumeGrowthRatio = 0.055f;

        public void Bind(SimBridge sim, StatSheet stats, ResourceWallet wallet,
            EcoEventScheduler events, StageStatistics statistics)
        {
            _sim = sim;
            _stats = stats;
            _wallet = wallet;
            _events = events;
            _stats2 = statistics;
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

            // 处理本帧吞噬候选。内核已按体积门槛筛过，这里直接结算。
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

            // 死亡事件结算（击杀奖励、卡牌 OnKill）
            ResolveDeaths(in snap);
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

            // 从内核移除
            _sim.ConsumeUnit(idx);

            Signals.Publish(new DevourSignal
            {
                UnitIndex = idx,
                Position = snap.Position[idx],
                TargetVolume = targetVolume,
                TargetFaction = faction,
                ComboCount = Combo,
                IsCorpse = isCorpse,
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

            float old = _stats.Get(StatId.Volume);
            float grown = old + targetVolume * VolumeGrowthRatio;
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
