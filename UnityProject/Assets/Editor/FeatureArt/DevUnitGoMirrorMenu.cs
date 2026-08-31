#if UNITY_EDITOR
using GameLogic.Battle.Feedback;
using UnityEditor;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>开关 <see cref="DevUnitGoMirror"/>：Play 时把 Instanced 单位+弹道镜像成可点选 GO。</summary>
    public static class DevUnitGoMirrorMenu
    {
        const string MenuPath = "BinGames/功能美术/Dev 单位 GO 镜像";

        [MenuItem(MenuPath, false, 55)]
        static void Toggle()
        {
            DevUnitGoMirror.Enabled = !DevUnitGoMirror.Enabled;
            bool on = DevUnitGoMirror.Enabled;
            EditorUtility.DisplayDialog(
                "Dev GO 镜像（调试）",
                on
                    ? "已开启。\nHierarchy 根节点 __DevUnitGoMirror：\n" +
                      "  Units/ —— 全部存活单位（无上限）\n" +
                      "  Projectiles/ —— 全部内核弹道\n" +
                      "同时关掉单位+弹道的 RenderMeshInstanced。\n" +
                      "区/障碍/血条/组合弹道本就是 GO，无需镜像。\n" +
                      "仅调试用，性能不管；要压测时关掉本开关。"
                    : "已关闭。恢复纯 Graphics.RenderMeshInstanced 路径。",
                "OK");
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, DevUnitGoMirror.Enabled);
            return true;
        }
    }
}
#endif
