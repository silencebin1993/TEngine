using System.Collections.Generic;
using BinGames.Sim;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;
using GameLogic.UI.Battle;
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
            public string PartId;
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
        private MetabolicSliceBridge _metabolicBridge;
        private SignalScope _scope;

        private float2 _lastPlayerPos;
        private bool _havePos;

        /// <summary>当前生效钩子数，供 execute_code 验收断言用。</summary>
        public int ActiveCount => _hooks.Count;

        public void Bind(SimBridge sim, StatSheet stats, StatusSystem status, MetabolicSliceBridge metabolicBridge)
        {
            _sim = sim;
            _stats = stats;
            _status = status;
            _metabolicBridge = metabolicBridge;
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

        /// <summary>装备生效时调用一次（StructuralOrganService.Equip）。同 sourceId 重复注册直接覆盖。
        /// story-003：追加 partId（= PartInstance.PartId），供 <see cref="FireDamageTaken"/> 反查
        /// CarrierRegistry.GetCarrier 时用（sourceId 是 RuntimeSourceId，键类型对不上）。</summary>
        public void RegisterHook(int sourceId, string partId, TriggerHookSpec spec)
        {
            _hooks[sourceId] = new HookState { Spec = spec, PartId = partId, MoveAccum = 0f, TickAccum = 0f, CooldownLeft = 0f };
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
                        FireMove(in state.Spec, pos, state.PartId);
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
                        FireDamageTaken(in state.Spec, pos, s.Amount, state.PartId);
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
                        FireLowHealth(in state.Spec, state.PartId);
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
                    FireKill(in state.Spec, state.PartId);
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
                    FirePulse(in state.Spec, pos, state.PartId);
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

        private void FireDamageTaken(in TriggerHookSpec spec, float2 pos, float incomingDamage, string partId)
        {
            float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
            if (spec.ThornsRatio > 0f && _sim != null)
            {
                float ratio = ResolveThornsRatio(spec.ThornsRatio, partId, spec.Tag);
                _sim.DamageArea(pos, radius, incomingDamage * ratio, SimFaction.Hostile,
                    sourceLogicId: ThornsSourceLogicId);
            }
            // Tag 标记（非反伤器官）仍走既有易伤/异常状态管线；Vulnerable 已按 CATALOG §A 文案
            // 移除，不与真实扣血叠加（preflight-decisions.md #5）。
            ApplyAreaMarks(in spec, pos, includeThornsMark: false, partId);
            // reaction-depth-and-combat-feel story-003：钩子触发的最小可见反馈，不区分是否命中了
            // ThornsRatio>0 分支——只要 OnDamageTaken 钩子被调用就给个信号，让玩家知道"这件器官响应了"。
            Signals.Publish(new StructuralHookFiredSignal { Position = pos, Radius = radius, Kind = "Thorns" });
        }

        /// <summary>story-003（gene-organ-universal-reaction）：把 ThornsRatio 当种子 Energy，组一条
        /// 「EnergyCore(baseRatio) + 该结构器官槽位里的基因模块链」重算反伤倍率——让荆棘壳的反伤真的
        /// 读结构器官槽位里装的基因（preflight-decisions.md #7）。
        /// story-003（reaction-depth-and-combat-feel，2026-09-06）：改走
        /// <see cref="ResolveThroughReactionPipeline"/>，链尾加 Actuator 后过一遍 ApplyPipeline——
        /// 不只是基因数值叠乘，<paramref name="tag"/>（=spec.Tag）与装备基因的 tag 现在会一起参与
        /// Substance 混合/Phase 1 新增的命名反应，荆棘壳配一个带 Fire 标签的基因，反伤这一下也可能
        /// 命中"苛性灼烧"之类的反应。无标签/无匹配反应时数值与升级前完全一致（Actuator 只是把
        /// Energy 折算成 Damage，不改变数值本身）。拿不到任一环节时 Reject-to-Safe 直接回落
        /// baseRatio，行为等价 story-002 之前（Required 5）。</summary>
        private float ResolveThornsRatio(float baseRatio, string partId, string tag)
        {
            ComposeEngine.Core.HitEvent evt = ResolveThroughReactionPipeline(new EnergyCore(baseRatio), tag, partId);
            return evt?.Damage ?? baseRatio;
        }

        /// <summary>reaction-depth-and-combat-feel story-003：四个 Resolve* 共用的执行核心——种子模块
        /// （<see cref="EnergyCore"/> 读 Energy 或 <see cref="LingerModule"/> 读 Linger）+ 该结构器官
        /// 自身的 <paramref name="tag"/>（TagAttach，空字符串则不挂）+ 槽位里装的基因模块链，链尾加
        /// <see cref="Actuator"/> 折算成 HitEvent 后过一遍 <see cref="Engine.ApplyPipeline"/>——Substance
        /// 混合（<c>SubstanceMixSettle</c>）对每个 HitEvent 都自动跑，命名反应表按合并后的 tag 集合匹配，
        /// 结构器官由此真正加入"默认全能组合"而不再是与基因反应系统平行的独立小系统。
        /// 不传 <see cref="RuleVector"/>/<see cref="WorldState"/> 契约（结构器官钩子没有装备契约的概念，
        /// 与 <see cref="Carrier.CarrierCompiler"/> 处理攻击器官的路径不同，那边契约来自基因槽位）。
        /// 拿不到 CarrierRegistry/GeneReserve/Engine/该 partId 对应 CarrierInstance 任一环节，或装配链
        /// 未产出事件（理论上不会，Actuator 恒产出）时返回 null，调用方各自 Reject-to-Safe 回落基础值。</summary>
        private ComposeEngine.Core.HitEvent ResolveThroughReactionPipeline(IModule seedModule, string tag, string partId)
        {
            CarrierRegistry registry = MetabolicSlicePanel.Instance?.CarrierRegistry;
            GeneReserve reserve = MetabolicSlicePanel.Instance?.GeneReserve;
            Engine engine = _metabolicBridge?.GetEngine();
            CarrierInstance carrier = registry?.GetCarrier(partId);
            if (registry == null || reserve == null || engine == null || carrier == null)
            {
                return null;
            }

            var chain = new List<IModule> { seedModule };
            if (!string.IsNullOrEmpty(tag))
            {
                chain.Add(new TagAttach(tag));
            }
            foreach (CarrierSlot slot in carrier.Slots)
            {
                if (string.IsNullOrEmpty(slot.GeneInstanceId))
                {
                    continue;
                }
                GeneInstance gene = reserve.Find(slot.GeneInstanceId);
                if (gene == null)
                {
                    continue;
                }
                System.Func<IModule> createModule = GeneCatalog.GetModule(gene.GeneId);
                if (createModule == null)
                {
                    continue;
                }
                chain.Add(createModule());
            }
            chain.Add(new Actuator(ActuatorMode.Attack));

            System.Collections.Generic.IReadOnlyList<ComposeEngine.Core.HitEvent> raw =
                engine.RunAssembly(chain, ticks: 1, seed: 0);
            if (raw.Count == 0)
            {
                return null;
            }
            return engine.ApplyPipeline(raw[0], new RuleVector(), null);
        }

        /// <summary>story-004：与 <see cref="ResolveThornsRatio"/> 同构——把低血量无敌秒数当种子
        /// LingerModule（LingerModule.Step 是整段赋值，不是累加，见 preflight-decisions.md #3）。
        /// story-003（reaction-depth-and-combat-feel）：改走 <see cref="ResolveThroughReactionPipeline"/>，
        /// 读回 <c>HitEvent.Linger</c>（Actuator 原样透传 Packet.Linger，数值语义不变）。</summary>
        private float ResolveLingerSeconds(float baseSeconds, string partId, string tag)
        {
            ComposeEngine.Core.HitEvent evt = ResolveThroughReactionPipeline(new LingerModule(baseSeconds), tag, partId);
            return evt?.Linger ?? baseSeconds;
        }

        private void FireMove(in TriggerHookSpec spec, float2 pos, string partId)
        {
            ApplyAreaMarks(in spec, pos, includeThornsMark: false, partId);
            float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
            Signals.Publish(new StructuralHookFiredSignal { Position = pos, Radius = radius, Kind = "Move" });
        }

        /// <summary>story-009：基础回血量改读该器官自己的 <see cref="TriggerHookSpec.KillHealAmount"/>
        /// （不再读全局 StatSheet.KillHeal——那条路径会与 CellDevourSystem 重复结算，已一并删除），
        /// 再过该槽位的基因链得到最终值，与 003/004/005 同构。</summary>
        private void FireKill(in TriggerHookSpec spec, string partId)
        {
            if (_sim == null || _stats == null)
            {
                return;
            }
            float baseHeal = spec.KillHealAmount;
            if (baseHeal <= 0f)
            {
                return;
            }
            float heal = ResolveKillHeal(baseHeal, partId, spec.Tag);
            if (heal > 0f)
            {
                _sim.HealPlayer(heal, _stats.Get(StatId.MaxHealth));
                float2 pos = _sim.PlayerPosition;
                Signals.Publish(new StructuralHookFiredSignal { Position = pos, Radius = 1.5f, Kind = "Kill" });
            }
        }

        /// <summary>story-009：与 <see cref="ResolveThornsRatio"/> 完全同构——把基础回血量当种子
        /// <see cref="EnergyCore"/>（003 已验证 EnergyCore 对 Packet.Energy 是从 0 起步的累加，
        /// 等价赋值种子）。story-003（reaction-depth-and-combat-feel）：改走
        /// <see cref="ResolveThroughReactionPipeline"/>，读回 <c>HitEvent.Damage</c>（Actuator 默认分支
        /// 把 Energy 折算成 Damage，数值语义不变）。</summary>
        private float ResolveKillHeal(float baseHeal, string partId, string tag)
        {
            ComposeEngine.Core.HitEvent evt = ResolveThroughReactionPipeline(new EnergyCore(baseHeal), tag, partId);
            return evt?.Damage ?? baseHeal;
        }

        private void FireLowHealth(in TriggerHookSpec spec, string partId)
        {
            if (_status == null)
            {
                return;
            }
            // 玩家在内核中恒占索引 0（BinGames.Sim.SimFaction.Player 文档注释）
            float seconds = spec.LingerSeconds > 0f ? spec.LingerSeconds : DefaultLowHealthInvulnSeconds;
            _status.ApplyTimed(0, SimStatus.Invulnerable, ResolveLingerSeconds(seconds, partId, spec.Tag));
            if (_sim != null)
            {
                Signals.Publish(new StructuralHookFiredSignal { Position = _sim.PlayerPosition, Radius = 1.5f, Kind = "LowHealth" });
            }
        }

        private void FirePulse(in TriggerHookSpec spec, float2 pos, string partId)
        {
            ApplyAreaMarks(in spec, pos, includeThornsMark: true, partId);
            float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
            Signals.Publish(new StructuralHookFiredSignal { Position = pos, Radius = radius, Kind = "Pulse" });
        }

        /// <summary>共用的范围标记落点：ThornsRatio&gt;0 时挂易伤（Thorns/反伤的近似替身，见上方
        /// 实证纠偏说明），Tag 非空时另挂 TagAttach 等价标记。全程只调用 StatusSystem.ApplyTimedArea，
        /// 不碰 SimBridge.DamageArea/DamageUnit，天然不产出 HitEvent。story-005：标记秒数改走
        /// <see cref="ResolveLingerSeconds"/>，让 OnMove/PeriodicPulse/OnDamageTaken 三处调用都读该
        /// 结构器官槽位的基因链结果（preflight-decisions.md #2/#3），半径不模拟。</summary>
        private void ApplyAreaMarks(in TriggerHookSpec spec, float2 pos, bool includeThornsMark, string partId)
        {
            if (_status == null)
            {
                return;
            }
            float radius = spec.LingerRadius > 0f ? spec.LingerRadius : DefaultAreaRadius;
            float baseSeconds = spec.LingerSeconds > 0f ? spec.LingerSeconds : DefaultMarkSeconds;
            float seconds = ResolveLingerSeconds(baseSeconds, partId, spec.Tag);

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
