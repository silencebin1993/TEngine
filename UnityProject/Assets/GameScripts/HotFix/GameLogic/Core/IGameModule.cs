namespace GameLogic.Core
{
    /// <summary>
    /// 玩法模块统一接口。
    ///
    /// 框架的核心纪律：模块之间不直接持有彼此的引用，而是通过 <see cref="ModuleHub"/>
    /// 解析或 <see cref="Signals"/> 通信。这样"新增功能不改老代码"才成立——
    /// 加一个系统就是注册一个新模块，老模块完全不动。
    ///
    /// 详见 DesignDocs/Game_Framework_Design.md §5.1。
    /// </summary>
    public interface IGameModule
    {
        /// <summary>更新优先级，小的先更新。用 <see cref="ModulePriority"/> 里的常量。</summary>
        int Priority { get; }

        /// <summary>注册后立刻调用一次。此时可以解析其它模块，但不要假设它们已 Enter。</summary>
        void OnInit(ModuleHub hub);

        /// <summary>一局开始。所有模块 OnInit 完成后统一调用。</summary>
        void OnEnter();

        void OnUpdate(float dt);

        /// <summary>一局结束。释放局内资源，但模块实例仍可复用。</summary>
        void OnExit();

        /// <summary>彻底销毁。</summary>
        void OnDispose();
    }

    /// <summary>
    /// 更新优先级常量。集中定义避免各模块自己拍数字导致顺序错乱。
    ///
    /// 顺序设计原则：输入 → 决策 → 提交内核 → 读结果 → 表现。
    /// </summary>
    public static class ModulePriority
    {
        /// <summary>时间与阶段推进，最先跑。</summary>
        public const int Timeline = 0;
        /// <summary>玩家输入采集。</summary>
        public const int Input = 100;
        /// <summary>属性重算（在输入之后、能力之前，保证本帧属性是最新的）。</summary>
        public const int Stats = 200;
        /// <summary>能力施放与冷却。</summary>
        public const int Ability = 300;
        /// <summary>卡牌触发派发。</summary>
        public const int Cards = 400;
        /// <summary>结构器官触发钩子（story-010）：与 Cards 相邻的独立轨道，不进攻击器官链。</summary>
        public const int Structural = 410;
        /// <summary>刷怪导演决策。</summary>
        public const int Spawning = 500;
        /// <summary>状态效果时间管理。</summary>
        public const int Status = 600;
        /// <summary>代谢切片桥：把 ComposeEngine 出口事件转伤害命令，须在 Status 结算之后、Simulation 提交内核之前。</summary>
        public const int MetabolicBridge = 650;
        /// <summary>提交命令并推进内核。这之后快照才有效。</summary>
        public const int Simulation = 700;
        /// <summary>读快照做玩法结算（吞噬、掉落、击杀）。</summary>
        public const int Resolution = 800;
        /// <summary>成长与资源结算。</summary>
        public const int Progression = 900;
        /// <summary>生态事件调度。</summary>
        public const int EcoEvent = 1000;
        /// <summary>表现层：渲染、VFX、镜头。</summary>
        public const int Presentation = 1100;
        /// <summary>UI 数据推送，最后跑。</summary>
        public const int UI = 1200;
    }

    /// <summary>
    /// 模块基类。省掉每个模块都写一遍空实现。
    /// </summary>
    public abstract class GameModuleBase : IGameModule
    {
        protected ModuleHub Hub { get; private set; }

        public abstract int Priority { get; }

        public virtual void OnInit(ModuleHub hub)
        {
            Hub = hub;
        }

        public virtual void OnEnter() { }
        public virtual void OnUpdate(float dt) { }
        public virtual void OnExit() { }
        public virtual void OnDispose() { }
    }
}
