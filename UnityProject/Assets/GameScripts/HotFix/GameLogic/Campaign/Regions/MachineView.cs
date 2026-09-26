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
