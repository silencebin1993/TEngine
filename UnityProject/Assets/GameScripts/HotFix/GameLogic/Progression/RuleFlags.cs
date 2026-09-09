using System.Collections.Generic;

namespace GameLogic.Progression
{
    /// <summary>
    /// 规则开关容器。存放 <see cref="Ability.RuleFlag"/> 的当前生效集合。
    ///
    /// 权衡说明：RuleFlags 本应像 StatSheet 一样挂在 ModuleHub 上，按类型解析。
    /// 但它现在只是"改几个布尔开关"，还没有生命周期、没有按来源移除的需求，
    /// 引入 IGameModule 只是空壳仪式。所以先做成一个静态单例字段（<see cref="Current"/>），
    /// 等规则数量增多或需要跨局重置的生命周期钩子时，再迁回 ModuleHub 正式注册。
    /// </summary>
    public sealed class RuleFlags
    {
        /// <summary>
        /// 当前局的规则开关实例。局开始时由 GameApp/AbilitySystem 负责重建或 ClearAll，
        /// 避免上一局的开关残留到下一局。
        /// </summary>
        public static RuleFlags Current = new RuleFlags();

        private readonly HashSet<Ability.RuleFlag> _flags = new HashSet<Ability.RuleFlag>();

        /// <summary>当前生效的规则开关集合，供 UI 展示（ui-visual-overhaul story-007）。
        /// 只读视图，判定路径仍走 <see cref="Has"/>。</summary>
        public IReadOnlyCollection<Ability.RuleFlag> Active => _flags;

        /// <summary>集合变更次数。UI 靠它判断"要不要重建那行文案"，
        /// 避免每帧拼串产生 GC——热更层每帧只允许 O(1)，逐帧遍历 12 个 flag 拼字符串是白烧。
        /// 只在真正发生增删时自增，不参与任何判定。</summary>
        public int Version { get; private set; }

        public void Set(Ability.RuleFlag flag)
        {
            if (flag == Ability.RuleFlag.None)
            {
                return;
            }
            if (_flags.Add(flag))
            {
                Version++;
            }
        }

        public void Clear(Ability.RuleFlag flag)
        {
            if (_flags.Remove(flag))
            {
                Version++;
            }
        }

        public bool Has(Ability.RuleFlag flag)
        {
            return flag != Ability.RuleFlag.None && _flags.Contains(flag);
        }

        public void ClearAll()
        {
            if (_flags.Count > 0)
            {
                _flags.Clear();
                Version++;
            }
        }
    }
}
