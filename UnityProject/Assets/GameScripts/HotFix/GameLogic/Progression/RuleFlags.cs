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

        public void Set(Ability.RuleFlag flag)
        {
            if (flag == Ability.RuleFlag.None)
            {
                return;
            }
            _flags.Add(flag);
        }

        public void Clear(Ability.RuleFlag flag)
        {
            _flags.Remove(flag);
        }

        public bool Has(Ability.RuleFlag flag)
        {
            return flag != Ability.RuleFlag.None && _flags.Contains(flag);
        }

        public void ClearAll()
        {
            _flags.Clear();
        }
    }
}
