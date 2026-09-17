using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace BinGames.EditorTools.QA
{
    /// <summary>
    /// QA 菜单：编辑器快捷键屏蔽开关，供自动化验收（模拟键鼠输入）时避免误触
    /// Unity 内置快捷键（Ctrl+S/Ctrl+Z/Delete 等）。是否屏蔽由用户在菜单里自行决定，
    /// 状态存 EditorPrefs，重启编辑器后维持上次选择。
    /// 用 <see cref="ShortcutManager"/> 官方 API 清空/恢复绑定，不用事件消费 hack ——
    /// 恢复时会把所有快捷键（含用户此前的自定义绑定）重置为 Unity 默认值，这是已知代价。
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorShortcutToggle
    {
        private const string MenuPath = "QA/屏蔽编辑器快捷键";
        private const string PrefsKey = "BinGames.QA.ShortcutsBlocked";

        static EditorShortcutToggle()
        {
            if (IsBlocked)
            {
                Apply(true);
            }
        }

        private static bool IsBlocked
        {
            get => EditorPrefs.GetBool(PrefsKey, false);
            set => EditorPrefs.SetBool(PrefsKey, value);
        }

        [MenuItem(MenuPath, false, 1)]
        private static void Toggle()
        {
            bool next = !IsBlocked;
            Apply(next);
            IsBlocked = next;
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, IsBlocked);
            return true;
        }

        private static void Apply(bool block)
        {
            var manager = ShortcutManager.instance;
            int count = 0;
            foreach (var id in manager.GetAvailableShortcutIds())
            {
                if (block)
                {
                    manager.RebindShortcut(id, ShortcutBinding.empty);
                }
                else
                {
                    manager.ClearShortcutOverride(id);
                }
                count++;
            }

            Debug.Log(block
                ? $"[QA] 已屏蔽编辑器全部快捷键（{count} 项，通过 QA/屏蔽编辑器快捷键 菜单可恢复）"
                : $"[QA] 已恢复编辑器快捷键为 Unity 默认值（{count} 项）");
        }
    }
}
