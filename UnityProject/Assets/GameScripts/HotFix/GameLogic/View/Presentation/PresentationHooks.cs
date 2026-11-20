// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.View.Presentation
{
    /// <summary>
    /// PL 在现役代码里的接缝事件（SEAMS 登记；主线也会装订阅，只登记引用、零写入 = 主线行为不变（SEAMS S-04））。现役文件只调 <c>On…</c>，订阅者的异常在这里隔离、只记一次，不打断 FG 逻辑。
    /// </summary>
    public static class PresentationHooks
    {
        /// <summary>机器表现挂上（HomeValleyMachineMarker.AttachView 末尾，SignalLinkView / MachineMorphView 之后；四个地点控制器共用这一处）。</summary>
        public static event Action<HomeValleyMachineMarker> MachineViewAttached;

        /// <summary>机器表现摘下（HomeValleyMachineMarker.DetachView 末尾）：(LogicId, 被摘的那份表现)。</summary>
        public static event Action<int, MachineView> MachineViewDetached;

        private static bool _logged;

        public static void OnMachineViewAttached(HomeValleyMachineMarker marker)
        {
            try
            {
                MachineViewAttached?.Invoke(marker);
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        public static void OnMachineViewDetached(int logicId, MachineView view)
        {
            try
            {
                MachineViewDetached?.Invoke(logicId, view);
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private static void LogOnce(Exception e)
        {
            if (!_logged)
            {
                _logged = true;
                Debug.LogException(e);
            }
        }
    }
}
