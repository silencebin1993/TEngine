using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 运行时 UGUI 构建工具。不依赖任何 prefab，也不依赖 EventSystem：
    /// 点击一律走 <see cref="FPClickable"/> 的手动矩形命中检测，避免
    /// 旧版 Input 与 Input System 双输入模块下按钮无响应的问题。
    /// </summary>
    public static class FPUIKit
    {
        public static Canvas CreateCanvas(string name, Transform parent, int sortOrder)
        {
            GameObject go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(parent, false);

            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        /// <summary>纯色面板。anchor 用 0-1 归一化坐标，offset/size 用参考分辨率像素。</summary>
        public static RectTransform Panel(Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 offset, Vector2 size, Color color)
        {
            GameObject go = new GameObject("Panel", typeof(Image));
            go.transform.SetParent(parent, false);
            Image img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            return rt;
        }

        public static Text Label(Transform parent, string content, int fontSize, Color color,
            Vector2 anchor, Vector2 pivot, Vector2 offset, Vector2 size,
            TextAnchor align = TextAnchor.UpperLeft)
        {
            GameObject go = new GameObject("Label", typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = FPFactory.CjkFont;
            text.fontSize = fontSize;
            text.color = color;
            text.text = content;
            text.alignment = align;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.supportRichText = true;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            return text;
        }

        /// <summary>横向进度条。</summary>
        public sealed class Bar
        {
            public RectTransform Root;
            public RectTransform Fill;
            public Image FillImage;
            public Text Caption;

            public void SetRatio(float ratio)
            {
                ratio = Mathf.Clamp01(ratio);
                Vector2 max = Fill.anchorMax;
                max.x = ratio;
                Fill.anchorMax = max;
            }
        }

        public static Bar CreateBar(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 offset,
            Vector2 size, Color fillColor, Color backColor)
        {
            RectTransform root = Panel(parent, anchor, pivot, offset, size, backColor);
            root.name = "Bar";

            GameObject fillGo = new GameObject("Fill", typeof(Image));
            fillGo.transform.SetParent(root, false);
            Image fillImg = fillGo.GetComponent<Image>();
            fillImg.color = fillColor;
            fillImg.raycastTarget = false;

            RectTransform fill = fillGo.GetComponent<RectTransform>();
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.one;
            fill.pivot = new Vector2(0f, 0.5f);
            fill.offsetMin = new Vector2(2f, 2f);
            fill.offsetMax = new Vector2(-2f, -2f);

            Text caption = Label(root, "", 18, Color.white, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, size, TextAnchor.MiddleCenter);
            caption.rectTransform.anchorMin = Vector2.zero;
            caption.rectTransform.anchorMax = Vector2.one;
            caption.rectTransform.sizeDelta = Vector2.zero;

            return new Bar { Root = root, Fill = fill, FillImage = fillImg, Caption = caption };
        }

        /// <summary>按钮 = 背景面板 + 居中文字 + 手动命中区域。</summary>
        public static FPClickable Button(Transform parent, string caption, int fontSize,
            Vector2 anchor, Vector2 pivot, Vector2 offset, Vector2 size,
            Action onClick, KeyCode shortcut = KeyCode.None)
        {
            RectTransform rt = Panel(parent, anchor, pivot, offset, size,
                new Color(0.20f, 0.24f, 0.32f, 0.95f));
            rt.name = "Button";

            Text label = Label(rt, caption, fontSize, Color.white, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, size, TextAnchor.MiddleCenter);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.sizeDelta = new Vector2(-16f, 0f);

            FPClickable clickable = new FPClickable
            {
                Rect = rt,
                Background = rt.GetComponent<Image>(),
                Caption = label,
                OnClick = onClick,
                Shortcut = shortcut,
            };
            clickable.Refresh();
            return clickable;
        }
    }

    /// <summary>
    /// 手动命中检测的可点击区域。由所属 View 每帧调用 <see cref="Poll"/>。
    /// </summary>
    public sealed class FPClickable
    {
        public RectTransform Rect;
        public Image Background;
        public Text Caption;
        public Action OnClick;
        public bool Interactable = true;
        public KeyCode Shortcut = KeyCode.None;

        public Color NormalColor = new Color(0.20f, 0.24f, 0.32f, 0.95f);
        public Color HoverColor = new Color(0.30f, 0.38f, 0.50f, 0.98f);
        public Color DisabledColor = new Color(0.14f, 0.15f, 0.17f, 0.90f);

        private bool _hover;

        public void Refresh()
        {
            if (Background == null)
            {
                return;
            }
            Background.color = !Interactable ? DisabledColor : (_hover ? HoverColor : NormalColor);
        }

        /// <summary>返回 true 表示本帧被点击。</summary>
        public bool Poll()
        {
            if (Rect == null)
            {
                return false;
            }

            if (Interactable && Shortcut != KeyCode.None && Input.GetKeyDown(Shortcut))
            {
                OnClick?.Invoke();
                return true;
            }

            bool inside = Interactable && RectTransformUtility.RectangleContainsScreenPoint(
                Rect, Input.mousePosition, null);
            if (inside != _hover)
            {
                _hover = inside;
                Refresh();
            }

            if (!inside || !Input.GetMouseButtonDown(0))
            {
                return false;
            }
            OnClick?.Invoke();
            return true;
        }

        public static bool PollAll(List<FPClickable> list)
        {
            bool any = false;
            for (int i = 0; i < list.Count; i++)
            {
                any |= list[i].Poll();
            }
            return any;
        }
    }
}
