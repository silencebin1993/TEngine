using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 细胞 / 生物两阶段共用 HUD。Spec §10：阶段目标、生命值、生物质、进化点（当前/100）、
    /// 进化按钮、体力条。
    /// </summary>
    public sealed class FPHud
    {
        private Canvas _canvas;
        private FPUIKit.Bar _hpBar;
        private FPUIKit.Bar _staminaBar;
        private Text _objective;
        private Text _resource;
        private Text _timer;
        private Text _hint;
        private FPClickable _evolveButton;
        private bool _evolveVisible;

        private readonly List<FPClickable> _clicks = new List<FPClickable>();

        private static readonly Color BackPanel = new Color(0.08f, 0.09f, 0.12f, 0.82f);
        private static readonly Color BarBack = new Color(0.16f, 0.17f, 0.20f, 0.95f);

        public void Build(Transform parent, bool withStamina, Action onEvolve)
        {
            _canvas = FPUIKit.CreateCanvas("FPHudCanvas", parent, 20);

            FPUIKit.Panel(_canvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, new Vector2(1920f, 132f), BackPanel);

            _objective = FPUIKit.Label(_canvas.transform, "", 27, new Color(0.9f, 0.93f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -16f),
                new Vector2(1000f, 36f));

            _hpBar = FPUIKit.CreateBar(_canvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(28f, -62f), new Vector2(360f, 30f),
                new Color(0.85f, 0.28f, 0.30f), BarBack);

            _resource = FPUIKit.Label(_canvas.transform, "", 25, new Color(0.85f, 0.90f, 0.96f),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(414f, -60f),
                new Vector2(760f, 34f));

            _timer = FPUIKit.Label(_canvas.transform, "", 25, new Color(0.78f, 0.82f, 0.88f),
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -16f),
                new Vector2(560f, 34f), TextAnchor.UpperRight);

            if (withStamina)
            {
                _staminaBar = FPUIKit.CreateBar(_canvas.transform, new Vector2(0f, 1f),
                    new Vector2(0f, 1f), new Vector2(28f, -98f), new Vector2(360f, 24f),
                    new Color(0.30f, 0.72f, 0.92f), BarBack);
            }

            _hint = FPUIKit.Label(_canvas.transform, "", 26, new Color(1f, 0.90f, 0.55f),
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 118f),
                new Vector2(1400f, 36f), TextAnchor.LowerCenter);

            _evolveButton = FPUIKit.Button(_canvas.transform, "进化（E）", 32,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f),
                new Vector2(300f, 68f), onEvolve, KeyCode.E);
            _evolveButton.NormalColor = new Color(0.16f, 0.44f, 0.30f, 0.96f);
            _evolveButton.HoverColor = new Color(0.24f, 0.62f, 0.42f, 0.98f);
            _evolveButton.Refresh();
            SetEvolveVisible(false);
            _clicks.Add(_evolveButton);
        }

        public void SetObjective(string text)
        {
            _objective.text = text;
        }

        public void SetHint(string text)
        {
            _hint.text = text;
        }

        public void SetTimer(string text)
        {
            _timer.text = text;
        }

        public void SetHp(float hp, float maxHp)
        {
            _hpBar.SetRatio(maxHp <= 0f ? 0f : hp / maxHp);
            _hpBar.Caption.text = $"生命 {Mathf.CeilToInt(Mathf.Max(0f, hp))} / {Mathf.RoundToInt(maxHp)}";
            _hpBar.FillImage.enabled = hp > 0.01f;
        }

        public void SetStamina(float value, float max)
        {
            if (_staminaBar == null)
            {
                return;
            }
            _staminaBar.SetRatio(max <= 0f ? 0f : value / max);
            _staminaBar.Caption.text = $"体力 {Mathf.FloorToInt(value)} / {Mathf.RoundToInt(max)}";
            _staminaBar.FillImage.enabled = value > 0.01f;
        }

        public void SetResource(string text)
        {
            _resource.text = text;
        }

        public void SetEvolveVisible(bool visible)
        {
            if (_evolveVisible == visible)
            {
                return;
            }
            _evolveVisible = visible;
            _evolveButton.Rect.gameObject.SetActive(visible);
            _evolveButton.Interactable = visible;
        }

        public void Tick()
        {
            FPClickable.PollAll(_clicks);
        }

        public Transform Root => _canvas != null ? _canvas.transform : null;

        public void Destroy()
        {
            _clicks.Clear();
            if (_canvas != null)
            {
                UnityEngine.Object.Destroy(_canvas.gameObject);
                _canvas = null;
            }
        }
    }
}
