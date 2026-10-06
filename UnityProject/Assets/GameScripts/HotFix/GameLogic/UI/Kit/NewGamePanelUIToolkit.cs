using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-GEN-01（FGR-GEN-001 种子输入、FGR-GEN-070 世界设置、FGR-GEN-071 分享短码；FG17 第 4 节）：新游戏设置面板（UI Toolkit）。
    ///
    /// 主菜单“新建”选好存档槽后打开（<see cref="Open"/>）。内容：
    /// - 世界种子：输入框（数字原样用；任意文字按固定规则换算成种子，并提示换算结果）+ “随机种子”；空白时“开始”不可用并说明原因。
    /// - 世界设置：资源丰度 / 敌方据点密度 / 污染强度（低、标准、高）、领地距离（近、标准、远）、起始区（标准、宽松），每项一排按钮，
    ///   当前档有“▸”前缀（颜色之外的标记，B15）。剧情模式始终保证起始区四级保证（说明文字）。
    /// - 分享短码：随种子与设置实时更新，“复制短码”放进剪贴板；粘贴别人的短码后“导入短码”——种子、设置、生成器版本一起换成短码里的，
    ///   世界完全相同（FGT-GEN-008）。格式错、校验错、版本更新、设置不认识各有说明，不静默失败。
    /// - “开始” / “返回”（Esc 同返回，不创建任何存档）。
    /// 模态：打开时输入上下文为“界面”，键盘输入只进输入框。
    /// </summary>
    public sealed class NewGamePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30082;

        public static NewGamePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private static bool _pendingOpen;
        private static Action<int, WorldSettings, DifficultyChoice> _pendingStart;
        private static Action _pendingBack;

        private VisualElement _root;
        private Label _title;
        private Label _generator;
        private Label _seedTitle;
        private TextField _seed;
        private Button _random;
        private Label _seedHint;
        private Label _settingsTitle;
        private VisualElement _axes;
        private Label _storyNote;
        private Label _shareTitle;
        private TextField _share;
        private Button _copy;
        private Button _import;
        private Label _shareHint;
        private Label _feedback;
        private Button _back;
        private Button _start;

        private Action<int, WorldSettings, DifficultyChoice> _onStart;
        private Action _onBack;
        private Label _difficultyTitle;
        private readonly DifficultyPickerView _difficulty = new DifficultyPickerView();
        private int _version;
        private int[] _levels;
        private WorldSettings _importedLegacy;
        private readonly List<List<Button>> _levelButtons = new List<List<Button>>();
        private readonly List<Label> _axisLabels = new List<Label>();

        protected override string UxmlLocation => "NewGamePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string SeedFieldText => _seed?.value ?? string.Empty;
        public string ShareFieldText => _share?.value ?? string.Empty;
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public string SeedHintText => _seedHint?.text ?? string.Empty;
        public string GeneratorText => _generator?.text ?? string.Empty;
        public int Version => _version;
        public int[] Levels => (int[])_levels?.Clone();
        public Button StartButton => _start;
        public Button BackButton => _back;
        public Button RandomButton => _random;
        public Button CopyButton => _copy;
        public Button ImportButton => _import;
        public TextField SeedField => _seed;
        public TextField ShareField => _share;
        public Button LevelButton(int axis, int level) => axis >= 0 && axis < _levelButtons.Count && level >= 0 && level < _levelButtons[axis].Count ? _levelButtons[axis][level] : null;
        public int AxisRowCount => _levelButtons.Count;
        /// <summary>FG6-DEF-09：难度选择（预设按钮 / 自定义滑条 / 公开说明）。</summary>
        public DifficultyPickerView Difficulty => _difficulty;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开面板。<paramref name="onStart"/>（种子, 世界设置）= 玩家点“开始”；<paramref name="onBack"/> = “返回” / Esc。（不关心难度的旧调用方）</summary>
        public static void Open(Action<int, WorldSettings> onStart, Action onBack) =>
            Open(onStart == null ? null : new Action<int, WorldSettings, DifficultyChoice>((seed, settings, _) => onStart(seed, settings)), onBack);

        /// <summary>FG6-DEF-09：打开面板，“开始”时连同玩家选的难度（预设或自定义三个倍率）一起交回。</summary>
        public static void Open(Action<int, WorldSettings, DifficultyChoice> onStart, Action onBack)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingStart = onStart;
                _pendingBack = onBack;
                return;
            }
            Instance._onStart = onStart;
            Instance._onBack = onBack;
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                _onStart = _pendingStart;
                _onBack = _pendingBack;
                SetOpen(true);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("NewGameRoot");
            _title = root.Q<Label>("NewGameTitle");
            _generator = root.Q<Label>("NewGameGenerator");
            _seedTitle = root.Q<Label>("NewGameSeedTitle");
            _seed = root.Q<TextField>("NewGameSeed");
            _random = root.Q<Button>("NewGameRandom");
            _seedHint = root.Q<Label>("NewGameSeedHint");
            _settingsTitle = root.Q<Label>("NewGameSettingsTitle");
            _axes = root.Q<VisualElement>("NewGameAxes");
            _storyNote = root.Q<Label>("NewGameStoryNote");
            _shareTitle = root.Q<Label>("NewGameShareTitle");
            _share = root.Q<TextField>("NewGameShareCode");
            _copy = root.Q<Button>("NewGameCopyCode");
            _import = root.Q<Button>("NewGameImportCode");
            _shareHint = root.Q<Label>("NewGameShareHint");
            _feedback = root.Q<Label>("NewGameFeedback");
            _back = root.Q<Button>("NewGameBack");
            _start = root.Q<Button>("NewGameStart");
            _difficultyTitle = root.Q<Label>("NewGameDifficultyTitle");
            _difficulty.Bind(root, "NewGameDiff");
            _random.clicked += RandomSeed;
            _copy.clicked += CopyCode;
            _import.clicked += () => ImportCode();
            _back.clicked += Back;
            _start.clicked += StartGame;
            _seed.RegisterValueChangedCallback(_ => OnInputChanged());
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.NewGameSetupFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, Back);
                ResetDefaults();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        /// <summary>每次打开：随机种子（旅程 / 自检的固定测试种子照样生效）、当前生成器版本、全部标准档。</summary>
        private void ResetDefaults()
        {
            _version = WorldGenVersions.Current;
            _importedLegacy = null;
            _levels = WorldSettings.DefaultLevels(WorldGenContent.Version(_version));
            _feedback.text = string.Empty;
            BuildTexts();
            BuildAxes();
            _difficulty.BuildPresets();
            _difficulty.Set(DifficultyService.Preset(DifficultyService.Standard)); // FG6-DEF-09：默认标准难度
            _seed.SetValueWithoutNotify(CampaignRandomService.GenerateSeed().ToString(CultureInfo.InvariantCulture));
            OnInputChanged();
        }

        private void BuildTexts()
        {
            _title.text = GameText.Get("ui.newgame.title");
            _seedTitle.text = GameText.Get("ui.newgame.seed");
            _random.text = GameText.Get("ui.newgame.random_seed");
            _settingsTitle.text = GameText.Get("ui.newgame.settings");
            _storyNote.text = GameText.Get("ui.newgame.story_note");
            if (_difficultyTitle != null)
            {
                _difficultyTitle.text = GameText.Get("ui.newgame.difficulty");
            }
            _shareTitle.text = GameText.Get("ui.newgame.share_code");
            _copy.text = GameText.Get("ui.newgame.copy_code");
            _import.text = GameText.Get("ui.newgame.import_code");
            _shareHint.text = GameText.Get("ui.newgame.share_hint");
            _back.text = GameText.Get("ui.newgame.back");
            _start.text = GameText.Get("ui.newgame.start");
        }

        /// <summary>按当前版本的分项表建每一排按钮（v1 没有分项：整排隐藏，显示预设名）。</summary>
        private void BuildAxes()
        {
            _axes.Clear();
            _levelButtons.Clear();
            _axisLabels.Clear();
            WorldGenVersion v = WorldGenContent.Version(_version);
            if (!WorldGenContent.HasSet(v.SettingSet))
            {
                return;
            }
            for (int a = 0; a < WorldSettings.Axes.Length; a++)
            {
                var row = new VisualElement();
                row.AddToClassList("wg-axis-row");
                var label = new Label(GameText.Get(WorldSettings.AxisNameKey(a)));
                label.AddToClassList("wg-axis-label");
                row.Add(label);
                _axisLabels.Add(label);
                var buttons = new List<Button>();
                foreach (WorldSettingAxis lv in WorldGenContent.AxisLevels(v, WorldSettings.Axes[a]))
                {
                    int axis = a;
                    int level = lv.Level;
                    var b = new Button(() => SelectLevel(axis, level)) { name = $"NewGameLevel_{WorldSettings.Axes[a]}_{lv.Level}" };
                    b.AddToClassList("mw-btn");
                    b.AddToClassList("wg-level");
                    row.Add(b);
                    buttons.Add(b);
                }
                _levelButtons.Add(buttons);
                _axes.Add(row);
            }
            RefreshLevelButtons();
        }

        private void RefreshLevelButtons()
        {
            WorldGenVersion v = WorldGenContent.Version(_version);
            for (int a = 0; a < _levelButtons.Count; a++)
            {
                List<Button> row = _levelButtons[a];
                for (int l = 0; l < row.Count; l++)
                {
                    bool selected = _levels != null && _levels[a] == l;
                    string name = WorldSettings.LevelName(v, a, l);
                    row[l].text = selected ? "▸ " + name : name;
                    row[l].EnableInClassList("wg-level-selected", selected);
                }
            }
        }

        /// <summary>选某分项的某档（按钮与自检同一入口）。导入的旧版本（没有分项）短码被改设置时回到当前版本。</summary>
        public void SelectLevel(int axis, int level)
        {
            if (_importedLegacy != null || _version != WorldGenVersions.Current)
            {
                _version = WorldGenVersions.Current;
                _importedLegacy = null;
                _levels = WorldSettings.DefaultLevels(WorldGenContent.Version(_version));
                BuildAxes();
            }
            if (axis < 0 || axis >= _levels.Length || level < 0 || level >= WorldSettings.LevelCount(WorldGenContent.Version(_version), axis))
            {
                return;
            }
            _levels[axis] = level;
            RefreshLevelButtons();
            OnInputChanged();
        }

        public void SetSeedText(string text)
        {
            _seed.value = text ?? string.Empty;
            OnInputChanged();
        }

        public void SetShareText(string text)
        {
            _share.SetValueWithoutNotify(text ?? string.Empty);
        }

        /// <summary>“随机种子”：换一颗新的强随机种子（按钮与自检同一入口）。</summary>
        public void RandomSeed()
        {
            _seed.value = CampaignRandomService.GenerateFreshSeed().ToString(CultureInfo.InvariantCulture);
            OnInputChanged();
        }

        /// <summary>当前的世界设置（按当前版本解析）。</summary>
        public WorldSettings CurrentSettings()
        {
            if (_importedLegacy != null)
            {
                return _importedLegacy;
            }
            return WorldSettings.FromLevels(WorldGenContent.Version(_version), _levels);
        }

        public bool TryCurrentSeed(out int seed, out bool fromText) => WorldSettings.TryParseSeed(_seed.value, out seed, out fromText);

        private void OnInputChanged()
        {
            if (_seed == null)
            {
                return;
            }
            bool ok = TryCurrentSeed(out int seed, out bool fromText);
            _start.SetEnabled(ok);
            _copy.SetEnabled(ok);
            if (!ok)
            {
                _seedHint.text = GameText.Get("ui.newgame.seed_empty");
                _seedHint.AddToClassList("wg-error");
                _share.SetValueWithoutNotify(string.Empty);
            }
            else
            {
                _seedHint.RemoveFromClassList("wg-error");
                _seedHint.text = fromText
                    ? GameText.Format("ui.newgame.seed_from_text", _seed.value.Trim(), seed.ToString(CultureInfo.InvariantCulture))
                    : GameText.Get("ui.newgame.seed_hint");
                _share.SetValueWithoutNotify(WorldSettings.EncodeShareCode(seed, CurrentSettings()));
            }
            _generator.text = _version == WorldGenVersions.Current
                ? GameText.Format("ui.newgame.generator", _version)
                : GameText.Format("ui.newgame.generator_old", _version, WorldGenVersions.Current);
        }

        /// <summary>“复制短码”（按钮与自检同一入口）。</summary>
        public void CopyCode()
        {
            if (!TryCurrentSeed(out int seed, out _))
            {
                return;
            }
            string code = WorldSettings.EncodeShareCode(seed, CurrentSettings());
            UnityEngine.GUIUtility.systemCopyBuffer = code;
            _feedback.RemoveFromClassList("wg-error");
            _feedback.text = GameText.Format("ui.newgame.code_copied", code);
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.UiClick); // FG00 B07 / B17：声音 + 文字
        }

        /// <summary>“导入短码”：按短码里的版本解释设置，种子 / 设置 / 版本一起换（按钮与自检同一入口）。失败时说明原因，原输入不变。</summary>
        public bool ImportCode()
        {
            WorldSettings.ShareError err = WorldSettings.TryDecodeShareCode(_share.value, out int seed, out WorldSettings s, out int version);
            if (err != WorldSettings.ShareError.None)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied); // 拒绝音 + 下面的原因文字
                _feedback.AddToClassList("wg-error");
                switch (err)
                {
                    case WorldSettings.ShareError.BadChecksum:
                        _feedback.text = GameText.Get("ui.newgame.code_bad_checksum");
                        break;
                    case WorldSettings.ShareError.Newer:
                        _feedback.text = GameText.Format("ui.newgame.code_newer", version, WorldGenVersions.Current);
                        break;
                    case WorldSettings.ShareError.BadSettings:
                        _feedback.text = GameText.Format("ui.newgame.code_bad_settings", version);
                        break;
                    default:
                        _feedback.text = GameText.Get("ui.newgame.code_bad_format");
                        break;
                }
                return false;
            }
            _version = s.Version;
            if (s.IsAxisBased)
            {
                _importedLegacy = null;
                _levels = (int[])s.Levels.Clone();
            }
            else
            {
                _importedLegacy = s;
                _levels = null;
            }
            BuildAxes();
            _seed.SetValueWithoutNotify(seed.ToString(CultureInfo.InvariantCulture));
            OnInputChanged();
            _feedback.RemoveFromClassList("wg-error");
            _feedback.text = GameText.Format("ui.newgame.code_imported", seed.ToString(CultureInfo.InvariantCulture), s.DisplayName());
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck);
            return true;
        }

        /// <summary>“开始”：种子有效时交回主菜单创建战役（按钮与自检同一入口）。</summary>
        public void StartGame()
        {
            if (!TryCurrentSeed(out int seed, out _))
            {
                _seedHint.text = GameText.Get("ui.newgame.seed_empty");
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                return;
            }
            WorldSettings settings = CurrentSettings();
            DifficultyChoice difficulty = _difficulty.Choice;
            Action<int, WorldSettings, DifficultyChoice> start = _onStart;
            _onStart = null;
            _onBack = null;
            SetOpen(false);
            start?.Invoke(seed, settings, difficulty);
        }

        /// <summary>“返回” / Esc：关面板，不创建任何存档。</summary>
        public void Back()
        {
            Action back = _onBack;
            _onStart = null;
            _onBack = null;
            SetOpen(false);
            back?.Invoke();
        }
    }
}
