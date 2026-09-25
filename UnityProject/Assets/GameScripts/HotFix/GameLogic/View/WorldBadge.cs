using GameLogic.UI.Common;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>ER8-CONTENT-01 / AC-ACC-002：3D 世界里的悬浮形状标记（建筑供电/损坏状态、敌人阵营）。
    /// 此前这些信息只靠占位几何体的颜色区分；标记用 <see cref="ContentIcons"/> 的图标贴图，类别与状态由
    /// 外形区分，颜色只是第二通道。
    ///
    /// 性能：没有碰撞体（不挡点选射线）；朝向在贴图就位时对准一次镜头——三个区域的镜头只平移/缩放、
    /// 从不旋转，不需要逐帧转向；只在“贴图还没加载好”期间每帧重试，赋值成功后组件自行停用，
    /// 常态下每帧零开销。</summary>
    public sealed class WorldBadge : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private string _iconId;
        private float _worldSize = 1f;
        private bool _visible = true;

        /// <summary>当前应显示的图标名（自检/测试读取）。</summary>
        public string IconId => _iconId;

        public bool IsShowing => _renderer != null && _renderer.enabled;

        /// <summary>在 <paramref name="parent"/> 下创建标记。<paramref name="parent"/> 必须是等比缩放
        /// （非等比缩放的父节点叠加朝向旋转会把贴图拉斜）；建筑这类非等比立方体请挂在区域根节点上。</summary>
        public static WorldBadge Create(Transform parent, string name, Vector3 worldPosition, float worldSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            var badge = go.AddComponent<WorldBadge>();
            badge._renderer = go.AddComponent<SpriteRenderer>();
            badge._renderer.enabled = false;
            badge._worldSize = worldSize;
            badge.enabled = false;
            return badge;
        }

        /// <summary>切换图标；null＝不显示。同一图标重复设置直接返回（调用方可以每帧调用）。</summary>
        public void SetIcon(string iconId)
        {
            if (_iconId == iconId)
            {
                return;
            }
            _iconId = iconId;
            if (_renderer != null)
            {
                _renderer.sprite = null;
                _renderer.enabled = false;
            }
            TryResolve();
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible)
            {
                return;
            }
            _visible = visible;
            if (_renderer != null)
            {
                _renderer.enabled = visible && _renderer.sprite != null;
            }
        }

        private void Update()
        {
            TryResolve();
        }

        private void TryResolve()
        {
            if (_renderer == null)
            {
                enabled = false;
                return;
            }
            if (string.IsNullOrEmpty(_iconId))
            {
                _renderer.enabled = false;
                enabled = false;
                return;
            }
            if (!ContentIcons.TryGetSprite(_iconId, out Sprite sprite))
            {
                enabled = true; // 贴图还在加载：下一帧再试。
                return;
            }
            _renderer.sprite = sprite;
            _renderer.enabled = _visible;
            float scale = sprite.bounds.size.x > 0f ? _worldSize / sprite.bounds.size.x : 1f;
            transform.localScale = Vector3.one * scale;
            Camera cam = Camera.main;
            if (cam != null)
            {
                transform.rotation = cam.transform.rotation;
            }
            enabled = false;
        }
    }
}
