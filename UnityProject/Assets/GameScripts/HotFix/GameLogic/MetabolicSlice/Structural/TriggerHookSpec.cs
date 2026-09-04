namespace GameLogic.MetabolicSlice.Structural
{
    /// <summary>organelle-structural-tier story-009（DESIGN §9.6）：五种结构器官触发钩子类型。
    /// 本 story 只声明类型，不接执行逻辑（010 消费）。</summary>
    public enum TriggerHookKind
    {
        OnDamageTaken,
        OnMove,
        OnKill,
        OnLowHealth,
        PeriodicPulse,
    }

    /// <summary>story-009（Preflight D6）：触发钩子数据，覆盖 CATALOG §A2 24 条逐条效果描述用到的
    /// 参数类别，不做通用引擎，够用即可。值语义 struct，字段默认值见各字段注释。</summary>
    public struct TriggerHookSpec
    {
        public TriggerHookKind Kind;

        /// <summary>命中率（受击时小概率触发类，如脓液腺）。默认 1（必定触发）。</summary>
        public float Probability;

        /// <summary>反伤比例（荆棘壳 18%）。</summary>
        public float ThornsRatio;

        /// <summary>转分摊比例（结痂甲 30%）。</summary>
        public float AbsorbRatio;

        /// <summary>命中/触发时挂的 Substance 标记名（如 Wet/Shock/Poison/Frostbite/Confused/Ichor/Charged）。
        /// 空串=不挂标记。</summary>
        public string Tag;

        /// <summary>世界残留半径（粘液壁垒/粘液尾/油腺）。</summary>
        public float LingerRadius;

        /// <summary>世界残留秒数（粘液壁垒/粘液尾/油腺）。</summary>
        public float LingerSeconds;

        /// <summary>血量阈值类的触发线（0-1）。默认 0.3。</summary>
        public float LowHealthThreshold;

        /// <summary>一次性长冷却（秒）。</summary>
        public float Cooldown;

        /// <summary>PeriodicPulse 的周期秒数。</summary>
        public float TickRate;

        /// <summary>OnMove 的累计位移阈值。</summary>
        public float MoveDistanceThreshold;
    }
}
