using GameLogic.View;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// 机器的画面对象（被观察的地点才有）：负责点选命中（碰撞体上挂着本组件）与选中高亮。位置由内核的 Transform 同步作业每帧写入（插值）。
    /// </summary>
    public sealed class MachineView : MonoBehaviour
    {
        private static readonly Color SelectedColor = new Color(1f, 0.85f, 0.2f, 1f);

        private Renderer _renderer;
        private Color _baseColor;

        public HomeValleyMachineMarker Marker { get; private set; }
        public int LogicId => Marker != null ? Marker.LogicId : 0;

        public void Initialize(Renderer renderer, Color baseColor)
        {
            _renderer = renderer;
            _baseColor = baseColor;
        }

        internal void Bind(HomeValleyMachineMarker marker) => Marker = marker;

        /// <summary>
        /// 能不能被左键点中（碰撞体开关）。阵亡的机器机身留在原地但不再接点选：否则活着的机器停在它身上时，左键先打到这具机身，
        /// 玩家点不中活机器、还会选中一台阵亡的（EMG4-CLEAN-01 修复轮 5，FGJ-M6R 实跑发现）。只在阵亡 / 挂表现时调，不逐帧。
        /// </summary>
        public void SetPickable(bool pickable)
        {
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
            {
                c.enabled = pickable;
            }
        }

        public void SetSelected(bool selected)
        {
            if (_renderer == null)
            {
                return;
            }
            ViewMaterials.Recolor(_renderer, selected ? SelectedColor : _baseColor);
        }
    }
}
