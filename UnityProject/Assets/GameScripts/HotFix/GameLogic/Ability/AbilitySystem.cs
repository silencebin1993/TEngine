using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Ability
{
    /// <summary>
    /// 技能系统。管理槽位、冷却、施放，并把效果派发给执行器。
    ///
    /// 执行器注册表是"新增效果不改老代码"的枢纽：
    /// 加一种效果只需 <see cref="RegisterExecutor"/> 一行，本类其余部分不动。
    /// </summary>
    public sealed class AbilitySystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Ability;

        private readonly Dictionary<EffectKind, IEffectExecutor> _executors =
            new Dictionary<EffectKind, IEffectExecutor>(16);

        private readonly List<AbilityRuntime> _slots = new List<AbilityRuntime>(5);

        private SimBridge _sim;
        private StatSheet _stats;

        /// <summary>本帧的瞄准方向（由输入模块写入）。</summary>
        public float2 AimDirection = new float2(1f, 0f);
        /// <summary>本帧的移动方向（由输入模块写入）。</summary>
        public float2 MoveDirection;

        public IReadOnlyList<AbilityRuntime> Slots => _slots;

        public void Bind(SimBridge sim, StatSheet stats)
        {
            _sim = sim;
            _stats = stats;
        }

        /// <summary>注册效果执行器。重复注册同一 Kind 会覆盖并告警。</summary>
        public void RegisterExecutor(IEffectExecutor executor)
        {
            if (executor == null)
            {
                return;
            }
            if (_executors.ContainsKey(executor.Kind))
            {
                TEngine.Log.Warning($"[AbilitySystem] 效果执行器 {executor.Kind} 重复注册，已覆盖。");
            }
            _executors[executor.Kind] = executor;
        }

        public int SlotCount => _slots.Count;

        /// <summary>
        /// 授予技能。槽位满则失败（返回 false），由调用方决定是否替换。
        /// 槽位上限来自 StatId.AbilitySlots，可被卡牌扩展到 5。
        /// </summary>
        public bool Grant(AbilitySpec spec)
        {
            if (spec == null)
            {
                return false;
            }
            if (HasAbility(spec.Id))
            {
                return false;
            }

            int cap = _stats != null ? _stats.GetInt(StatId.AbilitySlots) : 2;
            if (_slots.Count >= cap)
            {
                return false;
            }

            _slots.Add(new AbilityRuntime(spec));
            return true;
        }

        /// <summary>
        /// GM / 调试用：忽略槽位上限强制授予。已拥有则失败。
        /// 正式流程请用 <see cref="Grant"/>。
        /// </summary>
        public bool ForceGrant(AbilitySpec spec)
        {
            if (spec == null || HasAbility(spec.Id))
            {
                return false;
            }

            _slots.Add(new AbilityRuntime(spec));
            return true;
        }

        public bool HasAbility(int abilityId)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Spec != null && _slots[i].Spec.Id == abilityId)
                {
                    return true;
                }
            }
            return false;
        }

        public AbilityRuntime GetSlot(int index)
        {
            return index >= 0 && index < _slots.Count ? _slots[index] : null;
        }

        public override void OnUpdate(float dt)
        {
            float cdr = _stats != null ? _stats.Get(StatId.CooldownReduction) : 0f;
            for (int i = 0; i < _slots.Count; i++)
            {
                AbilityRuntime rt = _slots[i];
                if (rt.Spec == null)
                {
                    continue;
                }
                _slots[i].Tick(dt, EffectiveCooldown(rt.Spec, cdr), rt.Spec.Charges);
            }
        }

        private static float EffectiveCooldown(AbilitySpec spec, float cdr)
        {
            return Mathf.Max(0.05f, spec.Cooldown * (1f - cdr));
        }

        /// <summary>尝试施放槽位技能。返回是否成功。</summary>
        public bool TryCast(int slotIndex) => TryCastInternal(slotIndex, autoAimCursor: false);

        /// <summary>
        /// 供自动攻击触发路径调用（<see cref="GameLogic.Stage.CellStage.CellPlayerController"/>
        /// 的 PollAbilityInput 在槽位 Ready 时直接调用，不再依赖按键）。
        /// TargetMode.Cursor 的技能在自动施放时按 NearestEnemy 解析方向/目标——没有手动瞄准输入了，
        /// 复用同一次 ResolveTarget 调用，仍只在施放瞬间执行一次 O(N) 扫描，不新增每帧扫描。
        /// Self/MoveDirection/NearestEnemy/MarkedEnemy 不受影响。
        /// </summary>
        public bool TryCastAuto(int slotIndex) => TryCastInternal(slotIndex, autoAimCursor: true);

        private bool TryCastInternal(int slotIndex, bool autoAimCursor)
        {
            AbilityRuntime rt = GetSlot(slotIndex);
            if (rt == null || rt.Spec == null || !rt.Ready || _sim == null || !_sim.Running)
            {
                return false;
            }

            // 此处只做冷却与存在性检查。体力校验与扣除在 CellPlayerController
            // .PollAbilityInput 里做——因为体力属于资源系统，AbilitySystem 不该
            // 知道账本。注意：这意味着绕过输入层直接调 TryCast 不会扣体力，
            // 这是给卡牌触发施放（不消耗体力）留的口子。

            float cdr = _stats != null ? _stats.Get(StatId.CooldownReduction) : 0f;
            rt.Consume(EffectiveCooldown(rt.Spec, cdr));

            TargetMode resolveMode = (autoAimCursor && rt.Spec.TargetMode == TargetMode.Cursor)
                ? TargetMode.NearestEnemy
                : rt.Spec.TargetMode;

            float2 origin = _sim.PlayerPosition;
            float2 dir = ResolveDirection(resolveMode);
            int target = ResolveTarget(resolveMode, origin, rt.Spec.CastRange);

            var ctx = new EffectContext
            {
                Hub = Hub,
                Sim = _sim,
                Stats = _stats,
                Origin = origin,
                Direction = dir,
                TargetIndex = target,
                SourceId = rt.Spec.Id,
                Stack = 1,
                TriggerMagnitude = 0f,
            };

            RunEffects(rt.Spec.Effects, in ctx);

            Signals.Publish(new AbilityCastSignal
            {
                AbilityId = rt.Spec.Id,
                Origin = origin,
                Direction = dir,
            });
            return true;
        }

        /// <summary>
        /// 执行一组效果。卡牌触发也走这里，保证卡牌与技能共用同一套效果管线。
        /// </summary>
        public void RunEffects(List<EffectSpec> effects, in EffectContext ctx)
        {
            if (effects == null)
            {
                return;
            }
            for (int i = 0; i < effects.Count; i++)
            {
                RunEffect(effects[i], in ctx);
            }
        }

        public void RunEffect(EffectSpec spec, in EffectContext ctx)
        {
            if (spec == null || spec.Kind == EffectKind.None)
            {
                return;
            }
            if (!_executors.TryGetValue(spec.Kind, out IEffectExecutor exec))
            {
                TEngine.Log.Warning($"[AbilitySystem] 效果 {spec.Kind} 无执行器，已跳过。");
                return;
            }

            // 单个效果抛异常不应中断整条效果链——一张卡的 bug 不该让整次施放失败
            try
            {
                exec.Execute(spec, in ctx);
            }
            catch (System.Exception e)
            {
                TEngine.Log.Error($"[AbilitySystem] 效果 {spec.Kind} 执行异常: {e}");
            }
        }

        private float2 ResolveDirection(TargetMode mode)
        {
            switch (mode)
            {
                case TargetMode.MoveDirection:
                    return math.lengthsq(MoveDirection) > 0.0001f
                        ? math.normalize(MoveDirection)
                        : AimDirection;
                case TargetMode.Cursor:
                    return AimDirection;
                case TargetMode.NearestEnemy:
                case TargetMode.MarkedEnemy:
                {
                    int t = ResolveTarget(mode, _sim.PlayerPosition, 0f);
                    if (t < 0)
                    {
                        return AimDirection;
                    }
                    float2 d = _sim.Snapshot.Position[t] - _sim.PlayerPosition;
                    return math.normalizesafe(d, AimDirection);
                }
                default:
                    return AimDirection;
            }
        }

        /// <summary>
        /// 目标解析。遍历快照找最近/已标记敌人。
        ///
        /// 这里是热更层唯一允许的 O(N) 遍历，因为它只在施放瞬间执行一次，
        /// 不是每帧。若将来技能数量导致这成为热点，应下沉到内核的 JobQuery。
        /// </summary>
        private int ResolveTarget(TargetMode mode, float2 origin, float range)
        {
            if (mode != TargetMode.NearestEnemy && mode != TargetMode.MarkedEnemy)
            {
                return SimConst.InvalidIndex;
            }

            SimSnapshot snap = _sim.Snapshot;
            float maxSq = range > 0f ? range * range : float.MaxValue;
            int best = SimConst.InvalidIndex;
            float bestSq = maxSq;
            int bestMarked = SimConst.InvalidIndex;
            float bestMarkedSq = maxSq;

            for (int i = 1; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0 || snap.Faction[i] != (byte)SimFaction.Hostile)
                {
                    continue;
                }
                float dSq = math.distancesq(snap.Position[i], origin);
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = i;
                }
                if (mode == TargetMode.MarkedEnemy
                    && (snap.Status[i] & (uint)SimStatus.Marked) != 0u
                    && dSq < bestMarkedSq)
                {
                    bestMarkedSq = dSq;
                    bestMarked = i;
                }
            }

            return bestMarked >= 0 ? bestMarked : best;
        }

        public override void OnExit()
        {
            _slots.Clear();
        }
    }
}
