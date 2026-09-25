using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-012 / FGT-UX-003 / FG00 B02）：全部玩家动作的按键设置窗口（UI Toolkit）。
    /// 入口：游戏内暂停菜单“按键设置”、主菜单设置页“全部按键…”。
    /// - 标签页按上下文分：全部 / 战略 / 建造 / 接入 / 界面；搜索框按动作名、按键、分类过滤；
    /// - 虚拟化列表（动作数上百也只建可见行）；每行：动作名、生效范围、状态（后续开放）、当前按键、恢复默认；
    /// - 点按键 → 等玩家按新键（组合键 / 单独修饰键 / 滚轮 / 鼠标键，Esc 取消）→ 冲突时确认框（覆盖或取消）；
    /// - 全部恢复默认需二次确认（会丢失玩家改过的键）。
    /// 模态：打开时输入上下文为“界面”，Esc 关闭（在改键等待中 Esc 只取消改键）。
    /// </summary>
    public sealed class KeyBindingsPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30080;

        private static readonly InputContext[] TabContexts =
        {
            InputContext.None, InputContext.Strategy, InputContext.Build, InputContext.Uplink, InputContext.Interface,
        };

        private static readonly string[] TabLabelKeys =
        {
            "ui.keybind.tab_all", "input.context.strategy", "input.context.build", "input.context.uplink", "input.context.interface",
        };

        public static KeyBindingsPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        /// <summary>主菜单等在面板资源就绪前请求打开时记下，就绪后再打开。</summary>
        private static bool _openRequested;

        private readonly List<InputActionDef> _rows = new List<InputActionDef>();
        private readonly InputCapture _capture = new InputCapture();

        private VisualElement _window;
        private Label _title;
        private Label _status;
        private Label _feedback;
        private Button _close;
        private Button _resetAll;
        private UiTabs _tabs;
        private UiSearchBox _search;
        private UiVirtualList _list;
        private string _query = string.Empty;
        private int _seenBindings = -1;
        private int _seenFeedback = -1;
        private int _seenLanguage = -1;

        public static GameActionId? Listening { get; private set; }
        public IReadOnlyList<InputActionDef> VisibleRows => _rows;
        public int SelectedTab => _tabs?.Selected ?? 0;

        protected override string UxmlLocation => "KeyBindingsPanel";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        public static void Open()
        {
            if (Instance == null || !Instance.IsReady)
            {
                _openRequested = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _openRequested = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_openRequested)
            {
                _openRequested = false;
                SetOpen(true);
            }
        }

        /// <summary>绑定 UXML（运行时与自检共用：自检把同一份 UXML 挂到临时面板上直接调这里）。</summary>
        public void BindView(VisualElement root)
        {
            _window = root.Q<VisualElement>("KeyBindingsRoot");
            _title = root.Q<Label>("KeyBindingsTitle");
            _status = root.Q<Label>("KeyBindingsStatus");
            _feedback = root.Q<Label>("KeyBindingsFeedback");
            _close = root.Q<Button>("KeyBindingsClose");
            _resetAll = root.Q<Button>("KeyBindingsResetAll");
            _close.clicked += () => SetOpen(false);
            _resetAll.clicked += AskResetAll;
            _tabs = new UiTabs(new[]
            {
                root.Q<Button>("KeyTabAll"), root.Q<Button>("KeyTabStrategy"), root.Q<Button>("KeyTabBuild"),
                root.Q<Button>("KeyTabUplink"), root.Q<Button>("KeyTabInterface"),
            }, _ => Rebuild());
            _search = new UiSearchBox(root.Q<TextField>("KeyBindingsSearch"), root.Q<Label>("KeyBindingsSearchPlaceholder"),
                root.Q<Button>("KeyBindingsSearchClear"), "ui.keybind.search_placeholder", q =>
                {
                    _query = q ?? string.Empty;
                    Rebuild();
                });
            _list = new UiVirtualList(root.Q<ListView>("KeyBindingsList"), root.Q<Label>("KeyBindingsEmpty"), MakeRow, BindRow);
            ApplyTexts();
            _tabs.Select(0);
        }

        private void ApplyTexts()
        {
            _title.text = GameText.Get("ui.keybind.title");
            _close.text = GameText.Get("ui.common.close");
            _resetAll.text = GameText.Get("ui.keybind.reset_all");
            var labels = new List<string>();
            foreach (string key in TabLabelKeys)
            {
                labels.Add(GameText.Get(key));
            }
            _tabs.SetLabels(labels);
            _seenLanguage = GameSettings.Revision;
        }

        public void SetOpen(bool open)
        {
            if (_window == null)
            {
                return;
            }
            if (!open)
            {
                StopListening();
            }
            IsOpen = open;
            _window.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.KeyBindingsFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                KeyBindingFlow.SetFeedback(string.Empty);
                Rebuild();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        /// <summary>按当前标签页与搜索词过滤。纯函数，自检直接断言。</summary>
        public static void Filter(InputContext tab, string query, List<InputActionDef> into)
        {
            into.Clear();
            string q = (query ?? string.Empty).Trim();
            foreach (InputActionDef def in InputActionCatalog.All)
            {
                if (tab != InputContext.None && (def.Contexts & tab) == 0)
                {
                    continue;
                }
                if (q.Length > 0 && !Matches(def, q))
                {
                    continue;
                }
                into.Add(def);
            }
        }

        private static bool Matches(InputActionDef def, string q)
        {
            CompareInfo ci = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions opt = CompareOptions.IgnoreCase;
            return ci.IndexOf(def.DisplayName, q, opt) >= 0
                   || ci.IndexOf(GameText.Get(def.CategoryKey), q, opt) >= 0
                   || ci.IndexOf(InputDisplay.ForAction(def.Action), q, opt) >= 0;
        }

        private void Rebuild()
        {
            if (_list == null)
            {
                return;
            }
            int tab = Mathf.Clamp(_tabs.Selected, 0, TabContexts.Length - 1);
            Filter(TabContexts[tab], _query, _rows);
            _list.SetItems(_rows, GameText.Format("ui.keybind.empty", _query));
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_status != null)
            {
                _status.text = GameText.Format("ui.keybind.count",
                    InputActionCatalog.All.Count.ToString(CultureInfo.InvariantCulture),
                    GameSettings.KeyBindings.CustomizedCount.ToString(CultureInfo.InvariantCulture));
            }
            if (_feedback != null)
            {
                _feedback.text = KeyBindingFlow.LastFeedback;
            }
            _seenBindings = GameSettings.KeyBindings.Revision;
            _seenFeedback = KeyBindingFlow.FeedbackRevision;
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            var name = new Label { name = "Name" };
            name.AddToClassList("uk-keybind-name");
            var ctx = new Label { name = "Contexts" };
            ctx.AddToClassList("uk-keybind-context");
            var tag = new Label { name = "Tag" };
            tag.AddToClassList("uk-keybind-tag");
            var key = new Button { name = "Key" };
            key.AddToClassList("mw-btn");
            key.AddToClassList("uk-keybind-key");
            key.clicked += () =>
            {
                if (key.userData is GameActionId a)
                {
                    StartListening(a);
                }
            };
            var reset = new Button { name = "Reset" };
            reset.clicked += () =>
            {
                if (reset.userData is GameActionId a)
                {
                    ResetOne(a);
                }
            };
            reset.AddToClassList("mw-btn");
            reset.AddToClassList("uk-keybind-reset");
            row.Add(name);
            row.Add(ctx);
            row.Add(tag);
            row.Add(key);
            row.Add(reset);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _rows.Count)
            {
                return;
            }
            InputActionDef def = _rows[index];
            row.Q<Label>("Name").text = def.DisplayName;
            row.Q<Label>("Contexts").text = InputActionCatalog.ContextsDisplay(def.Contexts);
            var tag = row.Q<Label>("Tag");
            bool reserved = def.Status == InputActionStatus.Reserved;
            tag.text = reserved ? GameText.Get("input.status.reserved") : string.Empty;
            tag.tooltip = reserved ? GameText.Get("input.status.reserved_tip") : string.Empty;
            var key = row.Q<Button>("Key");
            InputChord chord = GameSettings.KeyBindings.GetChord(def.Action);
            bool listening = Listening == def.Action;
            key.text = listening ? GameText.Get("input.rebind.listening") : InputDisplay.Chord(chord);
            key.EnableInClassList("uk-keybind-listening", listening);
            key.EnableInClassList("uk-keycap-unbound", !chord.IsBound);
            key.tooltip = chord.IsBound ? string.Empty : GameText.Get("input.status.unbound_warning");
            key.userData = def.Action;
            var reset = row.Q<Button>("Reset");
            reset.text = GameText.Get("input.rebind.reset_one");
            reset.userData = def.Action;
            reset.SetEnabled(GameSettings.KeyBindings.IsCustomized(def.Action));
        }

        public void StartListening(GameActionId action)
        {
            Listening = action;
            _capture.Reset();
            InputRouter.SetRebindCapture(true);
            KeyBindingFlow.SetFeedback(GameText.Get("input.rebind.listening"));
            _list?.Refresh();
        }

        private void StopListening()
        {
            if (Listening == null)
            {
                return;
            }
            Listening = null;
            InputRouter.SetRebindCapture(false);
            _list?.Refresh();
        }

        public void ResetOne(GameActionId action)
        {
            var conflicts = new List<GameActionId>();
            RebindResult r = GameSettings.ResetKeyBinding(action, conflicts);
            KeyBindingFlow.SetFeedback(r == RebindResult.Ok
                ? GameText.Format("input.rebind.done", KeyBindingFlow.ActionName(action), InputDisplay.ForAction(action))
                : GameText.Format("input.rebind.reset_conflict", KeyBindingFlow.ActionName(action)));
            Rebuild();
        }

        private void AskResetAll()
        {
            int customized = GameSettings.KeyBindings.CustomizedCount;
            if (customized == 0)
            {
                return; // 没什么可丢的：不弹确认框（可逆 / 无代价的操作不打断，FGR-UX-002）。
            }
            var request = new ConfirmRequest
            {
                Title = GameText.Get("input.rebind.reset_all_title"),
                Irreversible = true,
                OnConfirm = () =>
                {
                    GameSettings.ResetKeyBindingsToDefault();
                    KeyBindingFlow.SetFeedback(GameText.Get("input.rebind.reset_all_title"));
                    Rebuild();
                },
            };
            request.Consequences.Add(GameText.Format("input.rebind.reset_all_line", customized.ToString(CultureInfo.InvariantCulture)));
            UiConfirmDialog.Show(request);
        }

        private void Update()
        {
            if (_window == null)
            {
                return;
            }
            if (_seenLanguage != GameSettings.Revision)
            {
                ApplyTexts();
                if (IsOpen)
                {
                    Rebuild();
                }
            }
            if (!IsOpen)
            {
                return;
            }
            if (Listening.HasValue && !UiConfirmDialog.IsOpen)
            {
                PollCapture();
            }
            if (_seenBindings != GameSettings.KeyBindings.Revision || _seenFeedback != KeyBindingFlow.FeedbackRevision)
            {
                _list.Refresh();
                RefreshStatus();
            }
        }

        /// <summary>读一次采集结果（运行时每帧；自检注入读取器后直接调用）。</summary>
        public void PollCapture()
        {
            if (!Listening.HasValue)
            {
                return;
            }
            GameActionId action = Listening.Value;
            InputCapture.Result result = _capture.Poll(out InputChord chord);
            if (result == InputCapture.Result.Waiting)
            {
                return;
            }
            StopListening();
            if (result == InputCapture.Result.Cancelled)
            {
                KeyBindingFlow.SetFeedback(GameText.Get("input.rebind.cancelled"));
                return;
            }
            KeyBindingFlow.Rebind(action, chord, _ => Rebuild());
        }

        protected override void OnDestroy()
        {
            if (IsOpen)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
