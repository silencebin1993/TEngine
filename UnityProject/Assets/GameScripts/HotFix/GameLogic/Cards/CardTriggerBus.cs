using System.Collections.Generic;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Cards
{
    /// <summary>
    /// 卡牌触发派发。把游戏信号路由到卡牌效果。
    ///
    /// 性能纪律：按触发类型分桶（<see cref="_byTrigger"/>），
    /// 一次信号只遍历订阅了该时机的卡，而不是整个卡组。
    /// 24-30 张卡时差别不大，但这保证了卡组扩大后成本不失控。
    /// </summary>
    public sealed class CardTriggerBus : GameModuleBase
    {
        public override int Priority => ModulePriority.Cards;

        private readonly Dictionary<CardTrigger, List<DeckEntry>> _byTrigger =
            new Dictionary<CardTrigger, List<DeckEntry>>(16);

        private Deck _deck;
        private AbilitySystem _abilities;
        private SimBridge _sim;
        private StatSheet _stats;
        private SignalScope _scope;

        private int _tickIndex;
        private float _tickAccum;

        /// <summary>OnTick 的基础节拍。卡牌各自的 TriggerInterval 在此之上累加。</summary>
        private const float TickPeriod = 0.5f;

        public void Bind(Deck deck, AbilitySystem abilities, SimBridge sim, StatSheet stats)
        {
            _deck = deck;
            _abilities = abilities;
            _sim = sim;
            _stats = stats;
        }

        public override void OnEnter()
        {
            _byTrigger.Clear();
            _tickIndex = 0;
            _tickAccum = 0f;

            // 用 SignalScope 统一退订，避免手写成对的 Unsubscribe 漏掉一个
            _scope = new SignalScope();
            _scope.On<DevourSignal>(OnDevour)
                  .On<KillSignal>(OnKill)
                  .On<HitSignal>(OnHit)
                  .On<PlayerHurtSignal>(OnHurt)
                  .On<DashSignal>(OnDash)
                  .On<AbilityCastSignal>(OnAbilityCast)
                  .On<LevelUpSignal>(OnLevelUp)
                  .On<PhaseChangedSignal>(OnPhaseStart)
                  .On<VolumeChangedSignal>(OnVolumeChanged)
                  .On<EcoEventSignal>(OnEcoEvent)
                  .On<CardAcquiredSignal>(OnCardAcquired);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
            _byTrigger.Clear();
        }

        /// <summary>
        /// 卡牌获得时一次性注册到对应触发桶，并应用被动部分。
        /// 关键：注册一次，之后每帧不再扫卡组。
        /// </summary>
        public void RegisterCard(DeckEntry entry)
        {
            if (entry?.Spec == null)
            {
                return;
            }

            CardSpec spec = entry.Spec;

            // 属性修正与规则开关是"获得即生效"，与 Trigger 无关
            ApplyPassive(entry);

            if (spec.Trigger == CardTrigger.Passive)
            {
                return;
            }

            if (!_byTrigger.TryGetValue(spec.Trigger, out List<DeckEntry> list))
            {
                list = new List<DeckEntry>(8);
                _byTrigger[spec.Trigger] = list;
            }
            if (!list.Contains(entry))
            {
                list.Add(entry);
            }
        }

        /// <summary>
        /// 应用被动部分。叠层时只应用增量的那一层，避免重复叠加。
        /// </summary>
        private void ApplyPassive(DeckEntry entry)
        {
            CardSpec spec = entry.Spec;

            if (_stats != null && spec.StatMods != null)
            {
                for (int i = 0; i < spec.StatMods.Count; i++)
                {
                    StatModifier m = spec.StatMods[i];
                    m.SourceId = spec.Id;
                    _stats.Add(m);
                }
            }

            if (_stats != null && spec.DrawbackMods != null)
            {
                for (int i = 0; i < spec.DrawbackMods.Count; i++)
                {
                    StatModifier m = spec.DrawbackMods[i];
                    m.SourceId = spec.Id;
                    _stats.Add(m);
                }
            }

            if (spec.RuleFlags != null)
            {
                for (int i = 0; i < spec.RuleFlags.Length; i++)
                {
                    RuleFlags.Current.Set(spec.RuleFlags[i]);
                }
            }

            if (spec.GrantAbilityId > 0 && _abilities != null)
            {
                AbilitySpec ab = DataRegistry.Instance.GetAbility(spec.GrantAbilityId);
                if (ab != null)
                {
                    _abilities.Grant(ab);
                }
            }

            // Passive 触发的卡在获得时也执行一次效果（一次性效果，如立刻回满血）
            if (spec.Trigger == CardTrigger.Passive && spec.Effects != null && spec.Effects.Count > 0)
            {
                Fire(entry, _sim != null ? _sim.PlayerPosition : float2.zero, float2.zero, -1, 0f);
            }
        }

        public override void OnUpdate(float dt)
        {
            _tickAccum += dt;
            if (_tickAccum < TickPeriod)
            {
                DecayCooldowns(dt);
                return;
            }
            _tickAccum -= TickPeriod;
            _tickIndex++;

            Signals.Publish(new TickSignal
            {
                ElapsedInRun = Time.time,
                TickIndex = _tickIndex,
            });

            // OnTick 卡各自按 TriggerInterval 计时
            if (_byTrigger.TryGetValue(CardTrigger.OnTick, out List<DeckEntry> ticks))
            {
                float2 origin = _sim != null ? _sim.PlayerPosition : float2.zero;
                for (int i = 0; i < ticks.Count; i++)
                {
                    DeckEntry e = ticks[i];
                    e.TickTimer += TickPeriod;
                    if (e.TickTimer < e.Spec.TriggerInterval)
                    {
                        continue;
                    }
                    e.TickTimer = 0f;
                    Fire(e, origin, float2.zero, -1, 0f);
                }
            }

            DecayCooldowns(dt);
        }

        private void DecayCooldowns(float dt)
        {
            foreach (var kv in _byTrigger)
            {
                List<DeckEntry> list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].TriggerCooldownLeft > 0f)
                    {
                        list[i].TriggerCooldownLeft -= dt;
                    }
                }
            }
        }

        // ── 信号处理 ──

        private void OnDevour(DevourSignal s)
        {
            Dispatch(CardTrigger.OnDevour, s.Position, float2.zero, s.UnitIndex, s.TargetVolume);
        }

        private void OnKill(KillSignal s)
        {
            Dispatch(CardTrigger.OnKill, s.Position, float2.zero, -1, 0f);
        }

        private void OnHit(HitSignal s)
        {
            Dispatch(CardTrigger.OnHit, s.Position, float2.zero, -1, s.Damage);
        }

        private void OnHurt(PlayerHurtSignal s)
        {
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnHurt, p, float2.zero, -1, s.Amount);

            // 低血触发是独立时机，阈值 30%（与 Spec §8.5 的低血保底一致）
            if (s.HealthPercent <= 0.3f)
            {
                Dispatch(CardTrigger.OnLowHealth, p, float2.zero, -1, s.HealthPercent);
            }
        }

        private void OnDash(DashSignal s)
        {
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnDash, p, s.Direction, -1, s.Distance);
        }

        private void OnAbilityCast(AbilityCastSignal s)
        {
            Dispatch(CardTrigger.OnAbilityCast, s.Origin, s.Direction, -1, s.AbilityId);
        }

        private void OnLevelUp(LevelUpSignal s)
        {
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnLevelUp, p, float2.zero, -1, s.NewLevel);
        }

        private void OnPhaseStart(PhaseChangedSignal s)
        {
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnPhaseStart, p, float2.zero, -1, s.PhaseIndex);
        }

        private void OnVolumeChanged(VolumeChangedSignal s)
        {
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnVolumeChanged, p, float2.zero, -1, s.NewVolume);
        }

        private void OnEcoEvent(EcoEventSignal s)
        {
            if (!s.Started)
            {
                return;
            }
            float2 p = _sim != null ? _sim.PlayerPosition : float2.zero;
            Dispatch(CardTrigger.OnEcoEvent, p, float2.zero, -1, s.EventId);
        }

        private void OnCardAcquired(CardAcquiredSignal s)
        {
            DeckEntry e = _deck?.Find(s.CardId);
            if (e != null)
            {
                RegisterCard(e);
            }
        }

        private void Dispatch(CardTrigger trigger, float2 origin, float2 dir,
            int targetIndex, float magnitude)
        {
            if (!_byTrigger.TryGetValue(trigger, out List<DeckEntry> list))
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Fire(list[i], origin, dir, targetIndex, magnitude);
            }
        }

        private void Fire(DeckEntry entry, float2 origin, float2 dir,
            int targetIndex, float magnitude)
        {
            CardSpec spec = entry.Spec;
            if (spec?.Effects == null || spec.Effects.Count == 0 || _abilities == null)
            {
                return;
            }

            if (entry.TriggerCooldownLeft > 0f)
            {
                return;
            }
            if (spec.TriggerChance < 1f && UnityEngine.Random.value > spec.TriggerChance)
            {
                return;
            }

            var ctx = new EffectContext
            {
                Hub = Hub,
                Sim = _sim,
                Stats = _stats,
                Origin = origin,
                Direction = math.lengthsq(dir) > 0.0001f ? math.normalize(dir) : new float2(1f, 0f),
                TargetIndex = targetIndex,
                SourceId = spec.Id,
                Stack = entry.Stack,
                TriggerMagnitude = magnitude,
            };

            _abilities.RunEffects(spec.Effects, in ctx);

            if (spec.TriggerCooldown > 0f)
            {
                entry.TriggerCooldownLeft = spec.TriggerCooldown;
            }
        }
    }
}
