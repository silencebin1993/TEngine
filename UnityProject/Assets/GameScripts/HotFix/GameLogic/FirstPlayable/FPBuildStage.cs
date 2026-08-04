using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 器官/组织阶段。Spec §4.2：选择菜单而非可玩关卡。6 个模块 / 2 个槽位，
    /// 生物质作货币，买不起置灰，支持退款，与微选择路线匹配的模块高亮。
    /// </summary>
    public sealed class FPBuildStage : IFPStage
    {
        private sealed class Card
        {
            public FPClickable Click;
            public FPModuleId Id;
            public Text Title;
            public Text Price;
            public Text State;
            public bool Highlight;
        }

        private FPGame _game;
        private FPRunData _run;
        private GameObject _root;
        private Canvas _canvas;
        private readonly List<Card> _cards = new List<Card>();
        private readonly List<FPClickable> _footer = new List<FPClickable>();

        private Text _balance;
        private Text _hint;
        private Canvas _confirmCanvas;
        private readonly List<FPClickable> _confirmClicks = new List<FPClickable>();

        public void Enter(FPGame game)
        {
            _game = game;
            _run = game.Run;

            _root = new GameObject("FPBuildStage");
            _root.transform.SetParent(game.transform, false);
            game.ConfigureCamera(null, 16f, new Vector3(0f, 30f, 0f), new Vector3(90f, 0f, 0f));

            _canvas = FPUIKit.CreateCanvas("FPBuildCanvas", _root.transform, 30);
            FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1920f, 1080f), new Color(0.05f, 0.06f, 0.09f, 1f));

            FPUIKit.Label(_canvas.transform, "器官 / 组织阶段 · 构筑选择", 52,
                new Color(0.6f, 0.92f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -48f), new Vector2(1400f, 66f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform,
                "用细胞阶段积累的生物质购买最多 2 个模块。它们会转化为生物阶段的实际属性与能力。",
                26, new Color(0.72f, 0.78f, 0.86f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -112f), new Vector2(1500f, 40f), TextAnchor.MiddleCenter);

            _balance = FPUIKit.Label(_canvas.transform, "", 32, new Color(0.95f, 0.97f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -166f),
                new Vector2(1500f, 44f), TextAnchor.MiddleCenter);

            BuildCards();

            _footer.Add(FPUIKit.Button(_canvas.transform, "确认并进入生物阶段（Enter）", 32,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 62f),
                new Vector2(560f, 74f), OnConfirm, KeyCode.Return));

            _hint = FPUIKit.Label(_canvas.transform, "", 25, new Color(1f, 0.90f, 0.55f),
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 154f),
                new Vector2(1500f, 36f), TextAnchor.MiddleCenter);

            ApplyPoorRunSafety();
            Refresh();
        }

        /// <summary>
        /// Spec §8 保底：达门槛时本应有约 260 生物质，最便宜模块 120。
        /// 若调参破坏该前提导致买不起任何模块，则赠予最便宜的一个。
        /// </summary>
        private void ApplyPoorRunSafety()
        {
            FPModuleDef cheapest = null;
            for (int i = 0; i < FPModuleTable.All.Count; i++)
            {
                if (cheapest == null || FPModuleTable.All[i].Price < cheapest.Price)
                {
                    cheapest = FPModuleTable.All[i];
                }
            }
            if (cheapest == null || _run.Biomass >= cheapest.Price || _run.Modules.Count > 0)
            {
                return;
            }
            _run.Modules.Add(cheapest.Id);
            SetHint($"生物质不足以购买任何模块，已按保底规则赠予 {cheapest.Name}");
        }

        private void BuildCards()
        {
            const float w = 480f;
            const float h = 244f;
            const float gapX = 30f;
            const float gapY = 26f;
            float startX = -(w * 3f + gapX * 2f) * 0.5f + w * 0.5f;

            FPRoute microRoute = FPModuleTable.MicroChoiceRoute(_run.MicroChoice);

            for (int i = 0; i < FPModuleTable.All.Count; i++)
            {
                FPModuleDef def = FPModuleTable.All[i];
                int row = i / 3;
                int col = i % 3;
                float x = startX + col * (w + gapX);
                float y = 40f - row * (h + gapY);

                FPModuleId id = def.Id;
                FPClickable click = FPUIKit.Button(_canvas.transform, "", 26,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, y),
                    new Vector2(w, h), () => Toggle(id), KeyCode.Alpha1 + i);
                click.Caption.text = "";

                Transform t = click.Rect;
                bool highlight = microRoute != FPRoute.None && def.Route == microRoute;

                Text title = FPUIKit.Label(t, def.Name, 36, Color.white,
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -18f),
                    new Vector2(w - 48f, 44f));

                FPUIKit.Label(t, $"{FPModuleTable.RouteName(def.Route)}{(highlight ? "  ◆ 与微选择匹配" : "")}",
                    23, highlight ? new Color(1f, 0.86f, 0.42f) : new Color(0.60f, 0.66f, 0.76f),
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -62f),
                    new Vector2(w - 48f, 32f));

                FPUIKit.Label(t, def.Desc, 24, new Color(0.78f, 0.86f, 0.94f),
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -100f),
                    new Vector2(w - 48f, 76f));

                Text price = FPUIKit.Label(t, "", 30, new Color(0.75f, 0.95f, 0.78f),
                    new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(24f, 18f),
                    new Vector2(240f, 38f), TextAnchor.LowerLeft);

                Text state = FPUIKit.Label(t, "", 26, Color.white,
                    new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-24f, 18f),
                    new Vector2(230f, 38f), TextAnchor.LowerRight);

                _cards.Add(new Card
                {
                    Click = click, Id = def.Id, Title = title,
                    Price = price, State = state, Highlight = highlight,
                });
            }
        }

        private void Toggle(FPModuleId id)
        {
            if (_confirmCanvas != null)
            {
                return;
            }

            FPModuleDef def = FPModuleTable.Get(id);
            if (_run.HasModule(id))
            {
                _run.RefundModule(id);
                SetHint($"已取消 {def.Name}，退款 {def.Price} 生物质");
            }
            else if (_run.SlotFull)
            {
                SetHint($"槽位已满（上限 {FPRunData.SlotLimit}）。先点击已选模块取消。");
            }
            else if (!_run.CanAfford(def))
            {
                SetHint($"生物质不足：{def.Name} 需 {def.Price}，当前 {_run.Biomass}");
            }
            else
            {
                _run.BuyModule(id);
                SetHint($"已购买 {def.Name}");
            }
            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                Card card = _cards[i];
                FPModuleDef def = FPModuleTable.Get(card.Id);
                bool owned = _run.HasModule(card.Id);
                bool affordable = _run.CanAfford(def);
                bool blocked = !owned && (_run.SlotFull || !affordable);

                card.Click.Interactable = owned || !blocked;
                card.Price.text = $"{def.Price} 生物质";

                if (owned)
                {
                    card.State.text = "已装备 · 点击退款";
                    card.State.color = new Color(0.55f, 1f, 0.65f);
                    card.Click.NormalColor = new Color(0.13f, 0.34f, 0.24f, 0.98f);
                    card.Click.HoverColor = new Color(0.19f, 0.46f, 0.33f, 1f);
                    card.Title.color = Color.white;
                    card.Price.color = new Color(0.75f, 0.95f, 0.78f);
                }
                else if (!affordable)
                {
                    card.State.text = "生物质不足";
                    card.State.color = new Color(0.85f, 0.45f, 0.45f);
                    card.Title.color = new Color(0.52f, 0.55f, 0.60f);
                    card.Price.color = new Color(0.72f, 0.42f, 0.42f);
                }
                else if (_run.SlotFull)
                {
                    card.State.text = "槽位已满";
                    card.State.color = new Color(0.70f, 0.62f, 0.42f);
                    card.Title.color = new Color(0.60f, 0.63f, 0.68f);
                    card.Price.color = new Color(0.62f, 0.68f, 0.62f);
                }
                else
                {
                    card.State.text = $"点击购买（{i + 1}）";
                    card.State.color = new Color(0.72f, 0.78f, 0.88f);
                    card.Click.NormalColor = card.Highlight
                        ? new Color(0.20f, 0.20f, 0.13f, 0.98f)
                        : new Color(0.13f, 0.16f, 0.22f, 0.98f);
                    card.Click.HoverColor = new Color(0.24f, 0.32f, 0.44f, 1f);
                    card.Title.color = Color.white;
                    card.Price.color = new Color(0.75f, 0.95f, 0.78f);
                }
                card.Click.Refresh();
            }

            FPRoute route = _run.DominantRoute(out bool mixed);
            string routeText = mixed ? "混合型" : FPModuleTable.RouteName(route);
            _balance.text = $"生物质余额 <b>{_run.Biomass}</b>     " +
                            $"槽位 <b>{_run.Modules.Count}</b> / {FPRunData.SlotLimit}     " +
                            $"当前路线判定 <b>{routeText}</b>";
        }

        private void SetHint(string text)
        {
            _hint.text = text;
        }

        /// <summary>Spec §8：买 0 个模块直接确认需二次确认弹窗。</summary>
        private void OnConfirm()
        {
            if (_confirmCanvas != null)
            {
                return;
            }
            if (_run.Modules.Count > 0)
            {
                _game.GoTo(FPStage.Creature);
                return;
            }
            ShowZeroModuleConfirm();
        }

        private void ShowZeroModuleConfirm()
        {
            _confirmCanvas = FPUIKit.CreateCanvas("FPBuildConfirmCanvas", _root.transform, 50);
            FPUIKit.Panel(_confirmCanvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1920f, 1080f), new Color(0.02f, 0.03f, 0.05f, 0.88f));

            RectTransform box = FPUIKit.Panel(_confirmCanvas.transform, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 300f),
                new Color(0.11f, 0.13f, 0.18f, 1f));

            FPUIKit.Label(box, "未购买任何模块", 42, new Color(1f, 0.82f, 0.45f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f),
                new Vector2(760f, 54f), TextAnchor.MiddleCenter);

            FPUIKit.Label(box, "将以基础形态进入生物阶段：生命值 140、移速 5.0、无路线专属能力。\n" +
                              "确定继续吗？",
                27, new Color(0.82f, 0.86f, 0.92f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, 8f), new Vector2(740f, 90f), TextAnchor.MiddleCenter);

            _confirmClicks.Add(FPUIKit.Button(box, "继续", 30, new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(-150f, 30f), new Vector2(260f, 62f),
                () => _game.GoTo(FPStage.Creature)));

            _confirmClicks.Add(FPUIKit.Button(box, "返回选择（Esc）", 30, new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(150f, 30f), new Vector2(260f, 62f),
                CloseConfirm, KeyCode.Escape));
        }

        private void CloseConfirm()
        {
            _confirmClicks.Clear();
            if (_confirmCanvas != null)
            {
                Object.Destroy(_confirmCanvas.gameObject);
                _confirmCanvas = null;
            }
        }

        public void Tick(float dt)
        {
            if (_confirmCanvas != null)
            {
                FPClickable.PollAll(_confirmClicks);
                return;
            }

            for (int i = 0; i < _cards.Count; i++)
            {
                _cards[i].Click.Poll();
            }
            FPClickable.PollAll(_footer);
        }

        public void Exit()
        {
            CloseConfirm();
            _cards.Clear();
            _footer.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            _canvas = null;
        }
    }
}
