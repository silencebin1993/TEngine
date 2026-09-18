using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace BinGames.EditorTools.QA
{
    /// <summary>
    /// QA 菜单：编辑器快捷键屏蔽开关，供自动化验收（模拟键鼠输入）时避免误触。
    /// 只屏蔽带修饰键的组合快捷键（Ctrl/Alt/Shift/Command），不影响 Del、鼠标按键和普通单键。
    /// 状态存 EditorPrefs，重启编辑器后维持上次选择。
    ///
    /// 屏蔽状态使用独立的快捷键配置文件承载。取消屏蔽时切回 Unity 默认配置文件，
    /// 避免逐条 ClearShortcutOverride 导致编辑器卡顿，也能清除旧实现遗留的空覆盖。
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorShortcutToggle
    {
        private const string MenuPath = "QA/屏蔽编辑器快捷键";
        private const string PrefsKey = "BinGames.QA.ShortcutsBlocked";
        private const string BlockedProfileId = "BinGames.QA.ShortcutsBlocked";

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

            if (!block)
            {
                // 切回默认配置文件会一次性恢复所有 Unity 默认绑定。
                // 这也能绕过旧实现写入当前用户配置文件的空覆盖。
                manager.activeProfileId = ShortcutManager.defaultProfileId;

                if (ProfileExists(manager, BlockedProfileId))
                {
                    manager.DeleteProfile(BlockedProfileId);
                }

                Debug.Log("[QA] 已恢复编辑器快捷键为 Unity 默认值");
                return;
            }

            if (ProfileExists(manager, BlockedProfileId))
            {
                manager.activeProfileId = BlockedProfileId;
                return;
            }

            // 从默认配置文件创建，避免继承旧实现可能写入的全量空覆盖。
            manager.activeProfileId = ShortcutManager.defaultProfileId;
            manager.CreateProfile(BlockedProfileId);
            manager.activeProfileId = BlockedProfileId;

            int count = 0;
            foreach (var id in manager.GetAvailableShortcutIds())
            {
                if (!HasModifier(manager.GetShortcutBinding(id)))
                {
                    continue;
                }

                manager.RebindShortcut(id, ShortcutBinding.empty);
                count++;
            }

            Debug.Log($"[QA] 已屏蔽编辑器组合快捷键（{count} 项，通过 QA/屏蔽编辑器快捷键 菜单可恢复）");
        }

        private static bool ProfileExists(IShortcutManager manager, string profileId)
        {
            foreach (var availableProfileId in manager.GetAvailableProfileIds())
            {
                if (availableProfileId == profileId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasModifier(ShortcutBinding binding)
        {
            foreach (var combination in binding.keyCombinationSequence)
            {
                if (combination.modifiers != ShortcutModifiers.None)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
