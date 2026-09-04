using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.MetabolicSlice.Structural
{
    /// <summary>
    /// story-010（DESIGN §9.6）：五种结构器官触发钩子的运行时执行者。完全独立于
    /// CarrierCompiler/TickCarrier/CarrierRegistry 遍历路径（Required 5）——结构器官
    /// 走自己的订阅+计时轨道，不进攻击器官链。反伤（<see cref="ThornsSourceLogicId"/>）
    /// story-013 起会产出真实 HitEvent 扣血，但 CellDevourSystem.PumpHits 按哨兵值过滤，
    /// 不广播 HitSignal，因此仍不进 CardTriggerBus.OnHit 攻击器官链。
    ///
    /// preflight-decisions.md #1：五种钩子零新增事件——OnDamageTaken/OnKill 订阅既有
    /// PlayerHurtSignal/KillSignal；PeriodicPulse 订阅既有 TickSignal（CardTriggerBus 每
    /// <see cref="TickPeriod"/> 秒 Publish 一次）；OnLowHealth 消费 PlayerHurtSignal.HealthPercent
    /// 字段本地判定；OnMove 在 OnUpdate 里逐帧读 SimBridge.PlayerPosition 累加位移，不新增信号。
    /// </summary>
    public sealed class StructuralHookRunner : GameModuleBase
    {
        public override int Priority => ModulePriority.Structural;

        private struct HookState
        {
            public TriggerHookSpec Spec;
            public float MoveAccum;
            public float TickAccum;
            public float CooldownLeft;
        }

        /// <summary>与 CardTriggerBus.TickPeriod 一致——PeriodicPulse 消费的是同一条 TickSignal。</summary>
        private const float TickPeriod = 0.5f;

        private const float DefaultAreaRadius = 3f;
        private const float DefaultMarkSeconds = 3f;
        private const float DefaultLowHealthCooldown = 20f;
        private const float DefaultLowHealthInvulnSeconds = 2f;

        private readonly Dictionary<int, HookState> _hooks = new Dictionary<int, HookState>(8);
        private readonly List<int> _scratchKeys = new List<int>(8);

        private SimBridge _sim;
        private StatSheet _stats;
        private StatusSystem _status;
        private SignalScope _scope;

        private float2 _lastPlayerPos;
        private bool _havePos;

        /// <summary>当前生效钩子数，供 execute_code 验收断言用。</summary>
        public int ActiveCount => _hooks.Count;

        public void Bind(SimBridge sim, StatSheet stats, StatusSystem status)
        {
            _sim = sim;
            _stats = stats;
            _status = status;
        }

        public override void OnEnter()
        {
            _hooks.Clear();
            _havePos = false;

            _scope = new SignalScope();
            _scope.On<PlayerHurtSignal>(OnPlayerHurt)
                  .On<KillSignal>(OnKill)
                  .On<TickSignal>(OnTick);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
            _hooks.Clear();
        }

        /// <summary>装备生效时调用一次（StructuralOrganService.Equip）。同 sourceId 重复注册直接覆盖。</summary>
        public void RegisterHook(int sourceId, TriggerHookSpec spec)
        {
            _hooks[sourceId] = new HookState { Spec = spec, MoveAccum = 0f, TickAccum = 0f, CooldownLeft = 0f };
        }

        /// <summary>卸下时调用一次（StructuralOrganService.Unequip，或 Equip 替换同槽旧件时）。
        /// 累加器/冷却随字典移除一起清空，不留悬空判定。</summary>
        public void UnregisterHook(int sourceId)
        {
            _hooks.Remove(sourceId);
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _hooks.Count == 0)
            {
                _havePos = false;
                return;
            }

            float2 pos = _sim.PlayerPosition;
            float moveDelta = _havePos ? math.distance(pos, _lastPlayerPos) : 0f;
            _lastPlayerPos = pos;
            _havePos = true;

            _scratchKeys.Clear();
            _scratchKeys.AddRange(_hooks.Keys);

            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                int sourceId = _scratchKeys[i];
                if (!_hooks.TryGetValue(sourceId, out HookState state))
                {
                    continue;
                }

                if (state.CooldownLeft > 0f)
                {
                    state.CooldownLeft = math.max(0f, state.CooldownLeft - dt);
                }

                if (state.Spec.Kind == TriggerHookKind.OnMove && state.Spec.MoveDistanceThreshold > 0f)
                {
                    state.MoveAccum += moveDelta;
                    if (state.MoveAccum >= state.Spec.MoveDistanceThreshold)
                    {
                        state.MoveAccum = 0f;
                        FireMove(in state.Spec, pos);
                    }
                }

                _hooks[sourceId] = state;
            }
        }

        // ── 信号处理 ──

        private void OnPlayerHurt(PlayerHurtSignal s)
        {
            if (_hooks.Count == 0)
            {
                return;
            }
            float2 pos = _sim != null ? _sim.PlayerPosition : float2.zero;

            _scratchKeys.Clear();
            _scratchKeys.AddRange(_hooks.Keys);

            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                int sourceId = _scratchKeys[i];
                if (!_hooks.TryGetValue(sourceId, out HookState state))
                {
                    continue;
                }

                if (state.Spec.Kind == TriggerHookKind.OnDamageTaken)
                {
                    if (state.CooldownLeft <= 0f && RollProbability(state.Spec.Probability))
                    {
                        FireDamageTaken(in state.Spec, pos, s.Amount);
                        if (state.Spec.Cooldown > 0f)
                        {
                            state.CooldownLeft = state.Spec.Cooldown;
                        }
                    }
                }
                else if (state.Spec.Kind == TriggerHookKind.OnLowHealth)
                {
                    if (s.HealthPercent <= state.Spec.LowHealthThreshold && state.CooldownLeft <= 0f)
                    {
                        FireLowHealth(in state.Spec);
                        state.CooldownLeft = state.Spec.Cooldown > 0f ? state.Spec.Cooldown : DefaultLowHealthCooldown;
                    }
                }

                _hooks[sourceId] = state;
            }
        }

        private void OnKill(KillSignal s)
        {
            if (_hooks.Count == 0)
            {
                return;
            }

            _scratchKeys.Clear();
            _scratchKeys.AddRange(_hooks.Keys);

            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                int sourceId = _scratchKeys[i];
                if (!_hooks.TryGetValue(sourceId, out HookState state))
                {
                    continue;
                }

                if (state.Spec.Kind == TriggerHookKind.OnKill && state.CooldownLeft <= 0f
                    && RollProbability(state.Spec.Probability))
                {
                    FireKill(in state.Spec);
                    if (state.Spec.Cooldown > 0f)
                    {
                        state.CooldownLeft = state.Spec.Cooldown;
                    }
                }

                _hooks[sourceId] = state;
            }
        }

        /// <summary>CardTriggerBus 每 <see cref="TickPeriod"/> 秒 Publish 一次的既有节拍，本 Runner
        /// 只消费不重复计时（preflight-decisions.md #1）。</summary>
        private void OnTick(TickSignal s)
        {
            if (_hooks.Count == 0)
            {
                return;
            }
            float2 pos = _sim != null ? _sim.PlayerPosition : float2.zero;

            _scratchKeys.Clear();
            _scratchKeys.AddRange(_hooks.Keys);

            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                int sourceId = _scratchKeys[i];
                if (!_hooks.TryGetValue(sourceId, out HookState state))
                {
                    continue;
                }

                if (state.Spec.Kind != TriggerHookKind.PeriodicPulse || state.Spec.TickRate <= 0f)
                {
                    continue;
                }

                state.TickAccum += TickPeriod;
                if (state.TickAccum >= state.Spec.TickRate)
                {
                    state.TickAccum -= state.Spec.TickRate;
                    FirePulse(in state.Spec, pos);
                }

                _hooks[sourceId] = state;
            }
        }

        // ── 效果执行 ──
        //
        // story-013：010 曾因 DamageArea/DamageUnit 提交的 DamageRequest 与投射物命中共用
        // JobDamage.TryDamage、无条件 Add 进 HitEvents，改用 Vulnerable 易伤标记近似"反伤"，
        // 没有真的扣血。本 story 改用 HitEvent/DamageRequest 自带的 SourceLogicId 字段
        // （preflight-decisions.md #3/#8）：反伤调用 DamageArea 时传哨兵值
        // ThornsSourceLogicId（-1，真实生成单位的 LogicId 恒 >=0，不冲突），CellDevourSystem.
        // PumpHits 按该哨兵值过滤，跳过 Signals.Publish(HitSignal)，命中依旧真实写入
        // Health（JobDamage.TryDamage 正常执行），但不再进 CardTriggerBus.OnHit 攻击链。

        /// <summary>反伤命中的 SourceLogicId 哨兵值——真实生成单位（玩家 0 / 敌方 SpawnDirector 分配）
        /// LogicId 恒 &gt;=0，取负数不会冲突（preflight-decisions.md #3）。</summary>
        internal const int ThornsSourceLogicId = -1;

        private void FireDamageTaken(in TriggerHookSpec spec, float2 pos, float incomingDamage)
        {
            if (spec.ThornsRatio > 0f && _sim != null)
            {
                float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
                _sim.DamageArea(pos, radius, incomingDamage * spec.ThornsRatio, SimFaction.Hostile,
                    sourceLogicId: ThornsSourceLogicId);
            }
            // Tag 标记（非反伤器官）仍走既有易伤/异常状态管线；Vulnerable 已按 CATALOG §A 文案
            // 移除，不与真实扣血叠加（preflight-decisions.md #5）。
            ApplyAreaMarks(in spec, pos, includeThornsMark: false);
        }

        private void FireMove(in TriggerHookSpec spec, float2 pos)
        {
            ApplyAreaMarks(in spec, pos, includeThornsMark: false);
        }

        private void FireKill(in TriggerHookSpec spec)
        {
            // 复用既有 KillHeal StatId（preflight-decisions.md #4），不新造回复数值口径
            if (_sim == null || _stats == null)
            {
                return;
            }
            float heal = _stats.Get(StatId.KillHeal);
            if (heal > 0f)
            {
                _sim.HealPlayer(heal, _stats.Get(StatId.MaxHealth));
            }
        }

        private void FireLowHealth(in TriggerHookSpec spec)
        {
            if (_status == null)
            {
                return;
            }
            // 玩家在内核中恒占索引 0（BinGames.Sim.SimFaction.Player 文档注释）
            float seconds = spec.LingerSeconds > 0f ? spec.LingerSeconds : DefaultLowHealthInvulnSeconds;
            _status.ApplyTimed(0, SimStatus.Invulnerable, seconds);
        }

        private void FirePulse(in TriggerHookSpec spec, float2 pos)
        {
            ApplyAreaMarks(in spec, pos, includeThornsMark: true);
        }

        /// <summary>共用的范围标记落点：ThornsRatio&gt;0 时挂易伤（Thorns/反伤的近似替身，见上方
        /// 实证纠偏说明），Tag 非空时另挂 TagAttach 等价标记。全程只调用 StatusSystem.ApplyTimedArea，
        /// 不碰 SimBridge.DamageArea/DamageUnit，天然不产出 HitEvent。</summary>
        private void ApplyAreaMarks(in TriggerHookSpec spec, float2 pos, bool includeThornsMark)
        {
            if (_status == null)
            {
                return;
            }
            float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
            float seconds = spec.LingerSeconds > 0f ? spec.LingerSeconds : DefaultMarkSeconds;

            if (includeThornsMark && spec.ThornsRatio > 0f)
            {
                _status.ApplyTimedArea(pos, radius, SimStatus.Vulnerable, seconds, SimFaction.Hostile);
            }
            if (!string.IsNullOrEmpty(spec.Tag))
            {
                _status.ApplyTimedArea(pos, radius, ParseTag(spec.Tag), seconds, SimFaction.Hostile);
            }
        }

        private static bool RollProbability(float p) => p >= 1f || UnityEngine.Random.value <= p;

        /// <summary>TriggerHookSpec.Tag 的物质名 → SimStatus 占位映射（本 story 只搭机制，§A2 24 条
        /// 正式取值由 011 定稿，届时如与此表冲突以 011 为准）。</summary>
        private static SimStatus ParseTag(string tag)
        {
            switch (tag)
            {
                case "Wet": return SimStatus.Slowed;
                case "Shock": return SimStatus.Stunned;
                case "Poison": return SimStatus.Corroded;
                case "Frostbite": return SimStatus.Slowed;
                case "Confused": return SimStatus.Feared;
                case "Ichor": return SimStatus.Vulnerable;
                case "Charged": return SimStatus.Conductive;
                default: return SimStatus.Marked;
            }
        }
    }
}
