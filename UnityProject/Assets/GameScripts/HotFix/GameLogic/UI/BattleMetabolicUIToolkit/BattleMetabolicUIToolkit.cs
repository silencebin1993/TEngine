using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 006 摘空保壳（F6b，preflight-decisions.md）：旧 3×3 装配渲染与拖拽手势已随本 story 下线，
    /// 本类只保留 <see cref="Instance"/>/<see cref="SetVisible"/>/<see cref="IsPanelVisible"/> 三个成员——
    /// <see cref="UI.BattleOverlayUIToolkit.BattleOverlayUIToolkit"/>（Esc 暂停菜单）硬依赖它们做 M×Esc 互斥判断，
    /// 删掉会让暂停菜单编译失败。旧面板已不存在，<see cref="IsPanelVisible"/> 恒为 false，
    /// <see cref="SetVisible"/> 退化为无操作。彻底删除本控制器须连
    /// <see cref="UI.BattleOverlayUIToolkit.BattleOverlayUIToolkit"/> 的互斥逻辑一起收口，属独立 story。
    /// </summary>
    public class BattleMetabolicUIToolkit : MonoBehaviour
    {
        /// <summary>供 execute_code 验收探针只读访问；旧面板已不存在，恒为 false。</summary>
        public bool IsPanelVisible => false;

        /// <summary>供 BattleOverlayUIToolkit（Pause 面板"打开代谢"按钮）跨控制器调用。</summary>
        public static BattleMetabolicUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>旧面板已不存在，无操作。</summary>
        public void SetVisible(bool visible)
        {
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
