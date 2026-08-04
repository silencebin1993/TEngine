using GameLogic.Core;
using GameLogic.Stats;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>
    /// 局内资源账本。对应 Cell_Stage_Spec.md §5。
    ///
    /// 所有资源变化都经过这里并广播 <see cref="ResourceChangedSignal"/>，
    /// 这样 UI 与卡牌不需要各自轮询数值。
    /// </summary>
    public sealed class ResourceWallet : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private StatSheet _stats;
        private SignalScope _scope;

        public float Nutrient { get; private set; }
        public float Mutagen { get; private set; }
        public float EvoEnergy { get; private set; }
        public float Pollution { get; private set; }
        public float Stamina { get; private set; }

        /// <summary>污染度是否已达上限。原核王冠卡会改变到达上限的后果。</summary>
        public bool PollutionFull =>
            _stats != null && Pollution >= _stats.Get(StatId.PollutionCap);

        public void Bind(StatSheet stats)
        {
            _stats = stats;
        }

        public override void OnEnter()
        {
            Nutrient = 0f;
            Mutagen = 0f;
            EvoEnergy = 0f;
            Pollution = 0f;
            Stamina = _stats?.Get(StatId.StaminaMax) ?? 100f;

            // 效果执行器通过信号申报资源变化，由本模块统一落账
            _scope = new SignalScope();
            _scope.On<ResourceChangedSignal>(OnResourceRequested);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
        }

        /// <summary>
        /// 处理来自效果执行器的资源变化申报。
        ///
        /// 注意：执行器发的信号里 Current 是 0（它不知道账本状态），
        /// 本模块落账后会再发一次带正确 Current 的信号供 UI 消费。
        /// 用 <see cref="_applying"/> 防止自己的广播被自己再次处理。
        /// </summary>
        private bool _applying;

        private void OnResourceRequested(ResourceChangedSignal s)
        {
            if (_applying)
            {
                return;
            }
            Add(s.Kind, s.Delta);
        }

        public float Get(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Nutrient: return Nutrient;
                case ResourceKind.Mutagen: return Mutagen;
                case ResourceKind.EvoEnergy: return EvoEnergy;
                case ResourceKind.Pollution: return Pollution;
                case ResourceKind.Stamina: return Stamina;
                default: return 0f;
            }
        }

        public void Add(ResourceKind kind, float delta)
        {
            if (Mathf.Approximately(delta, 0f))
            {
                return;
            }

            // 获取倍率只作用于正向收益，不影响消耗
            if (delta > 0f && _stats != null)
            {
                if (kind == ResourceKind.EvoEnergy)
                {
                    delta *= _stats.Get(StatId.EvoGain);
                }
                else if (kind == ResourceKind.Nutrient)
                {
                    delta *= _stats.Get(StatId.NutrientGain);
                }
            }

            switch (kind)
            {
                case ResourceKind.Nutrient:
                    Nutrient = Mathf.Max(0f, Nutrient + delta);
                    break;
                case ResourceKind.Mutagen:
                    Mutagen = Mathf.Max(0f, Mutagen + delta);
                    break;
                case ResourceKind.EvoEnergy:
                    EvoEnergy = Mathf.Max(0f, EvoEnergy + delta);
                    break;
                case ResourceKind.Pollution:
                {
                    float cap = _stats?.Get(StatId.PollutionCap) ?? 100f;
                    Pollution = Mathf.Clamp(Pollution + delta, 0f, cap);
                    break;
                }
                case ResourceKind.Stamina:
                {
                    float max = _stats?.Get(StatId.StaminaMax) ?? 100f;
                    Stamina = Mathf.Clamp(Stamina + delta, 0f, max);
                    break;
                }
                default:
                    return;
            }

            _applying = true;
            Signals.Publish(new ResourceChangedSignal
            {
                Kind = kind,
                Delta = delta,
                Current = Get(kind),
            });
            _applying = false;
        }

        /// <summary>尝试消费。不足则不扣并返回 false。</summary>
        public bool TrySpend(ResourceKind kind, float amount)
        {
            if (amount <= 0f)
            {
                return true;
            }
            if (Get(kind) < amount)
            {
                return false;
            }
            Add(kind, -amount);
            return true;
        }

        /// <summary>清空进化能（升级时调用）。</summary>
        public void ConsumeEvoEnergy(float amount)
        {
            Add(ResourceKind.EvoEnergy, -Mathf.Min(amount, EvoEnergy));
        }

        public override void OnUpdate(float dt)
        {
            if (_stats == null)
            {
                return;
            }
            float regen = _stats.Get(StatId.StaminaRegen);
            if (regen > 0f && Stamina < _stats.Get(StatId.StaminaMax))
            {
                Add(ResourceKind.Stamina, regen * dt);
            }
        }
    }
}
