using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-04（FGR-FW-043“触发时弹出反应名”“大量触发时聚合显示，同一屏幕每秒弹字有上限”；承接 DEBT-FG2FW02-02“读法触发的弹字”）：
    /// 把 <see cref="ReactionPopups"/> 里的弹字画在目标头上（世界坐标投到屏幕，边飘边淡出）。
    /// - 只画，不决定：聚合、每秒上限、设置开关都在 <see cref="ReactionPopups"/>；这里固定一个标签池（reaction.popup_pool 条），每帧按存活的弹字摆位置。
    /// - 换观察地点 / 离开世界时清空（“同一屏幕”）；镜头背后的弹字不画。
    /// - 不拦截点击（整层 picking-mode = Ignore）；sortingOrder -1：所有 HUD 与面板之下，会被窗口盖住（ADR-FW-004 决策）。
    /// 每帧 O(弹字池大小)，与单位数、触发次数无关。
    /// </summary>
    public sealed class ReactionPopupHudUIToolkit : UiKitPanelHost
    {
        public const int Order = -1;

        public static ReactionPopupHudUIToolkit Instance { get; private set; }

        private VisualElement _layer;
        private readonly List<Label> _pool = new List<Label>();
        private readonly List<int> _poolSerial = new List<int>();
        private readonly List<int> _poolCount = new List<int>();
        private string _lastObserved;

        /// <summary>自检读点：本帧画出来的弹字条数与文字。</summary>
        public int VisibleCount { get; private set; }
        public string VisibleText(int i) => i >= 0 && i < _pool.Count && i < VisibleCount ? _pool[i].text : string.Empty;

        protected override string UxmlLocation => "ReactionPopupHud";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _layer = root.Q<VisualElement>("ReactionPopupLayer");
            _layer.pickingMode = PickingMode.Ignore;
            EnsurePool(ReactionPopups.PoolSize);
        }

        private void EnsurePool(int size)
        {
            while (_pool.Count < size)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("rp-popup");
                l.AddToClassList("uk-hidden");
                _layer.Add(l);
                _pool.Add(l);
                _poolSerial.Add(0);
                _poolCount.Add(0);
            }
        }

        private void Update()
        {
            Tick(Camera.main, ReactionFeedback.RealTime());
        }

        /// <summary>每帧（自检直接调，给定镜头与真实秒）。</summary>
        public void Tick(Camera camera, float now)
        {
            if (_layer == null)
            {
                return;
            }
            bool inWorld = CampaignSession.Current != null && GameRoot.AnyRegionActive;
            string observed = FeedbackCues.ObservedSiteProvider?.Invoke();
            if (observed != _lastObserved || !inWorld && !InWorldOverrideForTests)
            {
                if (ReactionPopups.Active.Count > 0 || ReactionPopups.PendingCount > 0)
                {
                    ReactionPopups.Clear(); // “同一屏幕”：换观察地点 / 离开世界，旧地点的弹字不带过来。
                }
                _lastObserved = observed;
            }
            ReactionPopups.Tick(now);
            EnsurePool(ReactionPopups.PoolSize);
            IReadOnlyList<ReactionPopups.Popup> active = ReactionPopups.Active;
            int shown = 0;
            float life = ReactionPopups.LifeSeconds;
            for (int i = 0; i < active.Count && shown < _pool.Count; i++)
            {
                ReactionPopups.Popup p = active[i];
                if (!TryPlace(camera, p, now, life, out Vector2 panelPos, out float alpha))
                {
                    continue;
                }
                Label l = _pool[shown];
                if (_poolSerial[shown] != p.Serial || _poolCount[shown] != p.Count)
                {
                    l.text = p.Text;
                    _poolSerial[shown] = p.Serial;
                    _poolCount[shown] = p.Count;
                    l.EnableInClassList("rp-popup-first", p.First);
                    l.EnableInClassList("rp-popup-reading", p.Reading);
                }
                // 位置 / 透明度是数据驱动的运行时数值（世界坐标投影），按 UI Toolkit 红线 2 允许直接写 style；其余外观全在 USS。
                l.style.left = panelPos.x;
                l.style.top = panelPos.y;
                l.style.opacity = alpha;
                l.RemoveFromClassList("uk-hidden");
                shown++;
            }
            for (int i = shown; i < _pool.Count; i++)
            {
                if (!_pool[i].ClassListContains("uk-hidden"))
                {
                    _pool[i].AddToClassList("uk-hidden");
                }
                _poolSerial[i] = 0;
            }
            VisibleCount = shown;
        }

        /// <summary>自检：编辑模式下没有载入的地点时不清空。</summary>
        public static bool InWorldOverrideForTests;

        private bool TryPlace(Camera camera, ReactionPopups.Popup p, float now, float life, out Vector2 panelPos, out float alpha)
        {
            panelPos = default;
            alpha = 1f;
            float age = Mathf.Max(0f, now - p.Born);
            float sinceBump = Mathf.Max(0f, now - p.LastBump);
            alpha = Mathf.Clamp01(1f - Mathf.Max(0f, sinceBump - life * 0.6f) / Mathf.Max(0.01f, life * 0.4f));
            if (camera == null || _layer.panel == null)
            {
                return camera == null && InWorldOverrideForTests; // 自检没有镜头时只数条数。
            }
            Vector3 world = p.Position + Vector3.up * Mathf.Min(1.2f, age * 0.6f);
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                return false;
            }
            panelPos = RuntimePanelUtils.ScreenToPanel(_layer.panel, new Vector2(screen.x, Screen.height - screen.y));
            return true;
        }
    }
}
