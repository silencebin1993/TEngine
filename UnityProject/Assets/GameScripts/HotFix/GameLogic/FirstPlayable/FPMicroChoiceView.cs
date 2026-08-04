using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 细胞阶段 4:30 的 3 选 1 微选择。Spec §4.1 / §8：暂停玩法，不选不继续，无超时跳过。
    /// </summary>
    public sealed class FPMicroChoiceView
    {
        private Canvas _canvas;
        private readonly List<FPClickable> _clicks = new List<FPClickable>();
        private Action<FPMicroChoice> _onPick;

        private struct Option
        {
            public FPMicroChoice Choice;
            public string Name;
            public string Effect;
            public string Route;
        }

        private static readonly Option[] Options =
        {
            new Option
            {
                Choice = FPMicroChoice.Gluttony, Name = "贪食囊",
                Effect = "吞噬获得生物质 +25%", Route = "吞噬扩张型",
            },
            new Option
            {
                Choice = FPMicroChoice.Phototaxis, Name = "趋光纤毛",
                Effect = "移速 +20%", Route = "功能特化型",
            },
            new Option
            {
                Choice = FPMicroChoice.Metabolic, Name = "代谢泡",
                Effect = "每次吞噬回复 3 生命值", Route = "科技统治型",
            },
        };

        public void Show(Transform parent, Action<FPMicroChoice> onPick)
        {
            _onPick = onPick;
            _canvas = FPUIKit.CreateCanvas("FPMicroChoiceCanvas", parent, 40);

            FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1920f, 1080f), new Color(0.03f, 0.04f, 0.06f, 0.90f));

            FPUIKit.Label(_canvas.transform, "细胞微选择", 62, new Color(0.6f, 0.92f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f),
                new Vector2(1200f, 80f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform,
                "这次选择只作用于细胞阶段，但会决定你能留场多久、攒到多少生物质，\n" +
                "从而决定器官阶段买得起哪两个模块。它也计 1 点路线倾向。",
                26, new Color(0.72f, 0.78f, 0.86f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -240f), new Vector2(1300f, 80f), TextAnchor.UpperCenter);

            const float cardW = 380f;
            const float cardH = 300f;
            const float gap = 40f;
            float totalW = cardW * 3f + gap * 2f;
            float startX = -totalW * 0.5f + cardW * 0.5f;

            for (int i = 0; i < Options.Length; i++)
            {
                CreateCard(Options[i], i, startX + i * (cardW + gap), cardW, cardH);
            }

            FPUIKit.Label(_canvas.transform, "点击卡片或按 1 / 2 / 3 选择", 26,
                new Color(0.65f, 0.70f, 0.78f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 150f), new Vector2(900f, 40f), TextAnchor.MiddleCenter);
        }

        private void CreateCard(Option opt, int index, float x, float w, float h)
        {
            FPMicroChoice choice = opt.Choice;
            FPClickable card = FPUIKit.Button(_canvas.transform, "", 26,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, -40f),
                new Vector2(w, h), () => Pick(choice),
                KeyCode.Alpha1 + index);
            card.Caption.text = "";
            card.NormalColor = new Color(0.13f, 0.16f, 0.22f, 0.98f);
            card.HoverColor = new Color(0.22f, 0.30f, 0.42f, 1f);
            card.Refresh();
            _clicks.Add(card);

            Transform t = card.Rect;
            FPUIKit.Label(t, $"{index + 1}", 30, new Color(0.45f, 0.55f, 0.68f),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -16f),
                new Vector2(60f, 36f));

            FPUIKit.Label(t, opt.Name, 44, new Color(0.95f, 0.97f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -72f),
                new Vector2(w - 40f, 56f), TextAnchor.MiddleCenter);

            FPUIKit.Label(t, opt.Effect, 28, new Color(0.72f, 0.92f, 0.75f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 6f),
                new Vector2(w - 50f, 70f), TextAnchor.MiddleCenter);

            FPUIKit.Label(t, $"路线倾向：{opt.Route}", 24, new Color(0.62f, 0.68f, 0.78f),
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 28f),
                new Vector2(w - 40f, 36f), TextAnchor.MiddleCenter);
        }

        private void Pick(FPMicroChoice choice)
        {
            Action<FPMicroChoice> cb = _onPick;
            _onPick = null;
            cb?.Invoke(choice);
        }

        public void Tick()
        {
            FPClickable.PollAll(_clicks);
        }

        public void Destroy()
        {
            _clicks.Clear();
            _onPick = null;
            if (_canvas != null)
            {
                UnityEngine.Object.Destroy(_canvas.gameObject);
                _canvas = null;
            }
        }
    }
}
