using UnityEngine;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// IMGUI 面板拖拽复用工具，照抄 TEngine.Debugger 的 GUILayout.Window + GUI.DragWindow 模式
    /// （Assets/TEngine/Runtime/Module/DebugerModule/Debugger.cs），把面板从"每帧现算的局部 Rect"
    /// 升级为"可拖拽、可持久化的实例字段"。
    /// </summary>
    public static class ImguiDragUtil
    {
        private static readonly Rect TitleBarDragRect = new Rect(0f, 0f, float.MaxValue, 22f);

        /// <summary>
        /// 绘制一个可拖拽窗口。rect 由调用方以 ref 传入的持久化字段承载；prefsKey 非空时，
        /// 每次调用前会用存档坐标覆盖 rect.x/y（若存在），拖拽导致位置变化时立即落盘，
        /// 不用等应用退出。
        /// </summary>
        public static void DrawDraggable(int windowId, ref Rect rect, string title, string prefsKey, GUI.WindowFunction drawContent)
        {
            if (!string.IsNullOrEmpty(prefsKey))
            {
                string xKey = prefsKey + "_x";
                string yKey = prefsKey + "_y";
                if (PlayerPrefs.HasKey(xKey) && PlayerPrefs.HasKey(yKey))
                {
                    rect.x = PlayerPrefs.GetFloat(xKey, rect.x);
                    rect.y = PlayerPrefs.GetFloat(yKey, rect.y);
                }
            }

            Rect before = rect;
            rect = GUILayout.Window(windowId, rect, id =>
            {
                GUI.DragWindow(TitleBarDragRect);
                drawContent(id);
            }, title);

            if (!string.IsNullOrEmpty(prefsKey) && (!Mathf.Approximately(rect.x, before.x) || !Mathf.Approximately(rect.y, before.y)))
            {
                PlayerPrefs.SetFloat(prefsKey + "_x", rect.x);
                PlayerPrefs.SetFloat(prefsKey + "_y", rect.y);
            }
        }
    }
}
