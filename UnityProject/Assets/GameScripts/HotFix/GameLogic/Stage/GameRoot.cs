using GameLogic.Core;
using GameLogic.Stage.CellStage;
using TEngine;
using UnityEngine;

namespace GameLogic.Stage
{
    /// <summary>
    /// 正式游戏根。持有 <see cref="StageDirector"/> 并驱动每帧更新。
    ///
    /// 接入方式（框架文档 §8）：GameApp.Entrance → GameRoot.Startup。
    /// 不再像 FP demo 那样挂独立场景与自己的 MonoBehaviour——
    /// 走 TEngine 的 Utility.Unity 更新驱动，与框架其余部分一致。
    /// </summary>
    public static class GameRoot
    {
        private static StageDirector _director;
        private static bool _started;

        public static StageDirector Director => _director;

        /// <summary>当前细胞阶段流程。UI 需要读它的状态。</summary>
        public static CellStageFlow CellStage =>
            _director?.Get<CellStageFlow>(StageId.Cell);

        public static void Startup()
        {
            if (_started)
            {
                return;
            }
            _started = true;

            // 编辑器失焦会冻结播放循环，长时间挂机验证会中断。
            // 见 memory/unity-mcp-headless-testing。
            Application.runInBackground = true;

            _director = new StageDirector();

            // 注册所有阶段。后续阶段在这里各加一行，StageDirector 本身不改。
            _director.Register(new CellStageFlow());

            Utility.Unity.AddUpdateListener(OnUpdate);
            Utility.Unity.AddDestroyListener(Shutdown);

            MountDebugHud();

            Log.Info("[GameRoot] 启动完成。已注册阶段：Cell");
        }

        /// <summary>开始一局细胞阶段。</summary>
        public static void StartCellStage()
        {
            StartCellStage(CellStageEntryMode.NewRun);
        }

        /// <summary>从已有局面恢复细胞阶段；与 <see cref="StartCellStage"/> 的新局语义分开。</summary>
        public static void ResumeCellStage()
        {
            StartCellStage(CellStageEntryMode.Resume);
        }

        /// <summary>结束当前局，回到无阶段状态。ER2-BOOT-01：唯一的"返回菜单"出口——不管调用方是
        /// 玩家主动退出、阶段自然结束（死亡/通关）还是调试/测试代码，一律重新打开正式主菜单，
        /// 不停在旧运行中枢首页或黑屏。</summary>
        public static void EndRun()
        {
            _director?.EndCurrent();
            GameModule.UI.ShowUIAsync<MainMenuUI>();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// LookDev 对照沙盒入口（story-006）：抑制刷怪/阶段推进/玩家真实网格装配 Tick。
        /// 只在编辑器/开发构建可用，正式包不出现该入口。
        /// </summary>
        public static void StartLookDevSandbox()
        {
            StartCellStage(CellStageEntryMode.LookDevSandbox);
        }

        /// <summary>M2-06：进入独立固定试玩门，不读取续局控制状态，也不继承 LookDev 抑制态。</summary>
        public static void StartConsciousnessPlaytest()
        {
            StartCellStage(CellStageEntryMode.ConsciousnessPlaytest);
        }
#endif

        private static void StartCellStage(CellStageEntryMode mode)
        {
            if (!_started)
            {
                Startup();
            }
            CellStage?.PrepareNextEnter(mode);
            _director.GoTo(StageId.Cell);
        }

        private static void OnUpdate()
        {
            if (_director == null)
            {
                return;
            }

            float dt = Time.deltaTime;
            _director.Update(dt);

            // 阶段自然结束（死亡或通关）时收摊并回主菜单。
            // 由 GameRoot 判断而不是阶段自己切换，保证阶段不需要知道 director。
            // 统一走 EndRun()（而不是直接 _director.EndCurrent()），使"自然结束"与"玩家/调试代码主动
            // 调用 EndRun()"两条路径共用同一套回菜单逻辑，不重复也不遗漏。
            CellStageFlow cell = CellStage;
            if (_director.CurrentId == StageId.Cell && cell != null && !cell.IsRunning)
            {
                EndRun();
            }
        }

        private static GameObject _hudHost;

        /// <summary>
        /// 挂载全构建都需要的装配状态桥；IMGUI/压力测试只保留在编辑器和开发构建，
        /// 正式玩家入口由 UI Toolkit 运行中枢与玩法面板负责。
        /// </summary>
        private static void MountDebugHud()
        {
            if (_hudHost != null)
            {
                return;
            }
            _hudHost = new GameObject("[GameUiSupport]");
            Object.DontDestroyOnLoad(_hudHost);
            _hudHost.AddComponent<UI.Battle.MetabolicSlicePanel>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 已被 UI Toolkit 覆盖的旧 IMGUI 调试 HUD 不得默认盖在玩家界面上；
            // 如需做历史对照，可在运行时显式启用该组件。
            UI.Battle.CellDebugHud debugHud = _hudHost.AddComponent<UI.Battle.CellDebugHud>();
            debugHud.enabled = false;
            _hudHost.AddComponent<Battle.StressTestToggle>();
#endif
        }

        private static void Shutdown()
        {
            Utility.Unity.RemoveUpdateListener(OnUpdate);
            if (_hudHost != null)
            {
                Object.Destroy(_hudHost);
                _hudHost = null;
            }
            _director?.Dispose();
            _director = null;
            Signals.Clear();
            _started = false;
            Log.Info("[GameRoot] 已关闭。");
        }
    }
}
