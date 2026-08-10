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
            if (!_started)
            {
                Startup();
            }
            _director.GoTo(StageId.Cell);
        }

        /// <summary>结束当前局，回到无阶段状态。</summary>
        public static void EndRun()
        {
            _director?.EndCurrent();
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
            CellStageFlow cell = CellStage;
            if (_director.CurrentId == StageId.Cell && cell != null && !cell.IsRunning)
            {
                _director.EndCurrent();
            }
        }

        private static GameObject _hudHost;

        /// <summary>
        /// 挂载 IMGUI 调试 HUD。正式 UI（UIWindow + prefab）就绪后应移除本方法。
        /// 见 UI/Battle/CellDebugHud.cs 的说明。
        /// </summary>
        private static void MountDebugHud()
        {
            if (_hudHost != null)
            {
                return;
            }
            _hudHost = new GameObject("[CellDebugHud]");
            Object.DontDestroyOnLoad(_hudHost);
            _hudHost.AddComponent<UI.Battle.CellDebugHud>();
            _hudHost.AddComponent<Battle.StressTestToggle>();
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
