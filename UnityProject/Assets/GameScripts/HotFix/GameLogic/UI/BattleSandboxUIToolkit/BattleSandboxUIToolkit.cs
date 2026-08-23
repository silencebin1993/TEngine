using System.Collections.Generic;
using ComposeEngine.Core;
using Cysharp.Threading.Tasks;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.DebugTools;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic
{
    /// <summary>
    /// 任务四（UI 重设计）：LookDev 自由装配沙盒的 UI Toolkit 正式面板，替换
    /// <see cref="GameLogic.UI.Battle.CellDebugHud"/> 里固定尺寸的 IMGUI 沙盒
    /// （<c>DrawLookDevSandbox</c> 一系列方法保留为对照，默认关闭）。
    ///
    /// 常驻单例 + rootVisualElement 轮询保护，照抄 <see cref="BattleCarrierUIToolkit"/>
    /// 的建面板模式（D2/D3）。数据/逻辑全部复用既有静态方法，不重写：
    /// <see cref="SandboxAssembler.Compose"/>、<see cref="SandboxAssembler.OverridesFromEvent"/>、
    /// <see cref="LookDevFixtures.All"/>、<see cref="MetabolicSliceBridge.ApplyEvent"/>。
    /// sortingOrder=5：现况 HUD=0/Carrier=4/Draft=6/Overlay=10/Result=12，5 是空档。
    /// </summary>
    public class BattleSandboxUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private VisualElement _panelRoot;

        private ScrollView _geneList;
        private ScrollView _organList;
        private ScrollView _overrideList;
        private ScrollView _presetList;
        private Label _previewText;
        private Button _fireButton;
        private Toggle _autoFireToggle;
        private Slider _autoFireIntervalSlider;
        private Label _autoFireIntervalLabel;
        private Label _combatReadout;
        private Button _compareStandButton;
        private Button _compareStandClearButton;
        private Button _exitButton;
        private Button _clearSelectionButton;

        private readonly List<string> _geneIds = new List<string>();
        private readonly List<string> _organelleIds = new List<string>();
        private SandboxOverrides _overrides;
        private int _fireSeed = 1;
        private bool _autoFire;
        private float _autoFireInterval = 1f;
        private float _autoFireTimer;
        private bool _visible;

        public static BattleSandboxUIToolkit Instance { get; private set; }

        public bool IsVisible => _visible;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleSandboxUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 5;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleSandboxUIToolkit] rootVisualElement 等待超时，沙盒面板未初始化。");
                return;
            }

            CacheNodes();
            ResetAssembler();
            BuildStaticLists();
            SetVisible(false);
        }

        private void CacheNodes()
        {
            _panelRoot = _root.Q<VisualElement>("BattleSandboxUI");
            _geneList = _root.Q<ScrollView>("SandboxGeneList");
            _organList = _root.Q<ScrollView>("SandboxOrganList");
            _overrideList = _root.Q<ScrollView>("SandboxOverrideList");
            _presetList = _root.Q<ScrollView>("SandboxPresetList");
            _previewText = _root.Q<Label>("SandboxPreviewText");
            _fireButton = _root.Q<Button>("SandboxFireButton");
            _autoFireToggle = _root.Q<Toggle>("SandboxAutoFireToggle");
            _autoFireIntervalSlider = _root.Q<Slider>("SandboxAutoFireInterval");
            _autoFireIntervalLabel = _root.Q<Label>("SandboxAutoFireIntervalLabel");
            _combatReadout = _root.Q<Label>("SandboxCombatReadout");
            _compareStandButton = _root.Q<Button>("SandboxCompareStandButton");
            _compareStandClearButton = _root.Q<Button>("SandboxCompareStandClearButton");
            _exitButton = _root.Q<Button>("SandboxExitButton");
            _clearSelectionButton = _root.Q<Button>("SandboxClearSelection");

            if (_fireButton != null) { _fireButton.clicked += OnFireClicked; }
            if (_exitButton != null) { _exitButton.clicked += OnExitClicked; }
            if (_clearSelectionButton != null) { _clearSelectionButton.clicked += OnClearSelectionClicked; }
            if (_compareStandButton != null) { _compareStandButton.clicked += OnCompareStandClicked; }
            if (_compareStandClearButton != null) { _compareStandClearButton.clicked += OnCompareStandClearClicked; }

            if (_autoFireToggle != null)
            {
                _autoFireToggle.RegisterValueChangedCallback(evt => { _autoFire = evt.newValue; _autoFireTimer = 0f; });
            }
            if (_autoFireIntervalSlider != null)
            {
                _autoFireIntervalSlider.value = _autoFireInterval;
                _autoFireIntervalSlider.RegisterValueChangedCallback(evt =>
                {
                    _autoFireInterval = evt.newValue;
                    if (_autoFireIntervalLabel != null) { _autoFireIntervalLabel.text = $"间隔 {_autoFireInterval:0.0}s"; }
                });
            }
            if (_autoFireIntervalLabel != null) { _autoFireIntervalLabel.text = $"间隔 {_autoFireInterval:0.0}s"; }
        }

        /// <summary>基因/器官多选列表 + 7 维度覆盖行 + 预设模板按钮——都是"本会话固定内容"，
        /// 只建一次，之后只切 selected 视觉态，不每帧重建（不同于图鉴那种随数据变化的列表）。</summary>
        private void BuildStaticLists()
        {
            if (_geneList != null)
            {
                _geneList.Clear();
                foreach (string id in GeneCatalog.AllGeneIds)
                {
                    string display = GeneCatalog.GetDisplayName(id) ?? id;
                    var btn = new Button { text = $"{display}（{id}）" };
                    btn.AddToClassList("gene-item");
                    btn.AddToClassList("list-row");
                    btn.clicked += () => ToggleSelection(_geneIds, id, btn);
                    _geneList.Add(btn);
                }
            }

            if (_organList != null)
            {
                _organList.Clear();
                foreach (KeyValuePair<string, OrganelleDef> kv in OrganelleCatalog.All)
                {
                    var btn = new Button { text = $"{kv.Value.DisplayName}（{kv.Key}　{kv.Value.Role}）" };
                    btn.AddToClassList("carrier-item");
                    btn.AddToClassList("list-row");
                    btn.clicked += () => ToggleSelection(_organelleIds, kv.Key, btn);
                    _organList.Add(btn);
                }
            }

            RefreshOverrideControls();

            if (_presetList != null)
            {
                _presetList.Clear();
                IReadOnlyList<LookDevFixture> fixtures = LookDevFixtures.All;
                for (int i = 0; i < fixtures.Count; i++)
                {
                    LookDevFixture fixture = fixtures[i];
                    var btn = new Button { text = fixture.Name };
                    btn.clicked += () =>
                    {
                        ClearSelectionVisuals();
                        _geneIds.Clear();
                        _organelleIds.Clear();
                        _overrides = SandboxAssembler.OverridesFromEvent(fixture.A);
                        RefreshOverrideControls();
                        RefreshPreview();
                    };
                    _presetList.Add(btn);
                }
            }
        }

        private void ToggleSelection(List<string> selection, string id, Button btn)
        {
            if (selection.Contains(id))
            {
                selection.Remove(id);
                btn.RemoveFromClassList("codex-tab-active");
            }
            else
            {
                selection.Add(id);
                btn.AddToClassList("codex-tab-active");
            }
            RefreshPreview();
        }

        private void ClearSelectionVisuals()
        {
            _geneList?.Query<Button>().ForEach(b => b.RemoveFromClassList("codex-tab-active"));
            _organList?.Query<Button>().ForEach(b => b.RemoveFromClassList("codex-tab-active"));
        }

        private void OnClearSelectionClicked()
        {
            _geneIds.Clear();
            _organelleIds.Clear();
            ClearSelectionVisuals();
            RefreshPreview();
        }

        // ── 7 维度覆盖行构建 ──

        private void AddSliderOverrideRow(string label, string hint, float min, float max,
            System.Func<bool> getEnable, System.Action<bool> setEnable,
            System.Func<float> getValue, System.Action<float> setValue)
        {
            var row = new VisualElement();
            row.AddToClassList("sandbox-override-row");

            var toggle = new Toggle(label) { value = getEnable() };
            var slider = new Slider(min, max) { value = getValue() };
            var valueLabel = new Label(getValue().ToString("0.#"));

            toggle.RegisterValueChangedCallback(evt => { setEnable(evt.newValue); RefreshPreview(); });
            slider.RegisterValueChangedCallback(evt =>
            {
                setValue(evt.newValue);
                valueLabel.text = evt.newValue.ToString("0.#");
                RefreshPreview();
            });

            row.Add(toggle);
            row.Add(slider);
            row.Add(valueLabel);

            var hintLabel = new Label(hint);
            hintLabel.AddToClassList("dim");
            var wrapper = new VisualElement();
            wrapper.Add(row);
            wrapper.Add(hintLabel);
            _overrideList.Add(wrapper);
        }

        private void AddTextOverrideRow(string label, string hint,
            System.Func<bool> getEnable, System.Action<bool> setEnable,
            System.Func<string> getValue, System.Action<string> setValue)
        {
            var row = new VisualElement();
            row.AddToClassList("sandbox-override-row");

            var toggle = new Toggle(label) { value = getEnable() };
            var field = new TextField { value = getValue() ?? string.Empty };

            toggle.RegisterValueChangedCallback(evt => { setEnable(evt.newValue); RefreshPreview(); });
            field.RegisterValueChangedCallback(evt => { setValue(evt.newValue); RefreshPreview(); });

            row.Add(toggle);
            row.Add(field);

            var hintLabel = new Label(hint);
            hintLabel.AddToClassList("dim");
            var wrapper = new VisualElement();
            wrapper.Add(row);
            wrapper.Add(hintLabel);
            _overrideList.Add(wrapper);
        }

        private void AddBoolOverrideRow(string label, string hint,
            System.Func<bool> getEnable, System.Action<bool> setEnable,
            System.Func<bool> getValue, System.Action<bool> setValue)
        {
            var row = new VisualElement();
            row.AddToClassList("sandbox-override-row");

            var toggle = new Toggle(label) { value = getEnable() };
            var valueToggle = new Toggle("True/False") { value = getValue() };

            toggle.RegisterValueChangedCallback(evt => { setEnable(evt.newValue); RefreshPreview(); });
            valueToggle.RegisterValueChangedCallback(evt => { setValue(evt.newValue); RefreshPreview(); });

            row.Add(toggle);
            row.Add(valueToggle);

            var hintLabel = new Label(hint);
            hintLabel.AddToClassList("dim");
            var wrapper = new VisualElement();
            wrapper.Add(row);
            wrapper.Add(hintLabel);
            _overrideList.Add(wrapper);
        }

        /// <summary>预设模板一键载入后，覆盖行的 Toggle/Slider/TextField 视觉值要跟着刷新——
        /// 简单粗暴重建整个覆盖区（7 行，成本可忽略），避免维护一份控件引用表。</summary>
        private void RefreshOverrideControls()
        {
            if (_overrideList == null)
            {
                return;
            }
            _overrideList.Clear();
            AddTextOverrideRow("Shape",
                "弹体基础形态（远程 Bolt / 近战 Melee），由 Carrier 出口器官决定；全仓库无中间产出，仅本面板覆盖可预览。",
                () => _overrides.EnableShape, v => _overrides.EnableShape = v,
                () => _overrides.Shape, v => _overrides.Shape = v);
            AddSliderOverrideRow("Count", "命中次数（分裂/多段），典型产出：纺锤散射（org_scatter）。", 1f, 10f,
                () => _overrides.EnableCount, v => _overrides.EnableCount = v,
                () => _overrides.Count, v => _overrides.Count = v);
            AddSliderOverrideRow("Scale", "弹体尺度/命中范围倍率，典型产出：膨胀泡（org_swell）。", 0.1f, 5f,
                () => _overrides.EnableScale, v => _overrides.EnableScale = v,
                () => _overrides.Scale, v => _overrides.Scale = v);
            AddSliderOverrideRow("Spin", "弹体自旋角速度，改变弹道/绕轨轨迹，典型产出：鞭毛环（org_flagella）。", -180f, 180f,
                () => _overrides.EnableSpin, v => _overrides.EnableSpin = v,
                () => _overrides.Spin, v => _overrides.Spin = v);
            AddSliderOverrideRow("Orbit", "绕轨半径；全仓库无器官/基因真实产出，仅本面板覆盖可预览。", -5f, 5f,
                () => _overrides.EnableOrbit, v => _overrides.EnableOrbit = v,
                () => _overrides.Orbit, v => _overrides.Orbit = v);
            AddTextOverrideRow("Tag", "命中附加的属性标记，典型产出：过氧化物酶/水合泡/离子泵。",
                () => _overrides.EnableTag, v => _overrides.EnableTag = v,
                () => _overrides.Tag, v => _overrides.Tag = v);
            AddBoolOverrideRow("Explode", "命中后是否触发爆炸效果，典型产出：溶酶爆（org_lyso）。",
                () => _overrides.EnableExplode, v => _overrides.EnableExplode = v,
                () => _overrides.ExplodeOnHit, v => _overrides.ExplodeOnHit = v);
        }

        // ── 预览 / 开火 ──

        private void RefreshPreview()
        {
            if (_previewText == null)
            {
                return;
            }
            HitEvent preview = SandboxAssembler.Compose(_geneIds, _organelleIds, _overrides, seed: 1);
            _previewText.text =
                $"Damage {preview.Damage:0.#}　Heal {preview.Heal:0.#}\n" +
                $"Shape {preview.Shape}\n" +
                $"Scale {preview.Scale:0.#}　Count {preview.Count:0.#}\n" +
                $"Spin {preview.Spin:0.#}　Orbit {preview.Orbit:0.#}\n" +
                $"Explode {preview.ExplodeOnHit}\n" +
                $"Tags {(preview.Tags != null ? string.Join(",", preview.Tags) : string.Empty)}";
        }

        private void OnFireClicked()
        {
            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null)
            {
                return;
            }
            FireSandbox(cell);
        }

        private void FireSandbox(CellStageFlow cell)
        {
            _fireSeed++;
            HitEvent fireEvent = SandboxAssembler.Compose(_geneIds, _organelleIds, _overrides, _fireSeed);
            cell.MetabolicBridge?.ApplyEvent(fireEvent);
        }

        private void OnExitClicked()
        {
            SetVisible(false);
            GameRoot.EndRun();
        }

        private void OnCompareStandClicked()
        {
            int count = VisualCompareStand.Spawn(new Vector3(-30f, 0f, -30f));
            if (_combatReadout != null)
            {
                _combatReadout.text = $"造型对比台已生成 {count} 项，位于场景 (-30,0,-30) 附近。";
            }
        }

        private void OnCompareStandClearClicked()
        {
            VisualCompareStand.Dispose();
            if (_combatReadout != null)
            {
                _combatReadout.text = "造型对比台已清除。";
            }
        }

        private void ResetAssembler()
        {
            _geneIds.Clear();
            _organelleIds.Clear();
            _overrides = new SandboxOverrides { Scale = 1f, Count = 1f, Shape = "Bolt", Tag = string.Empty };
            _fireSeed = 1;
            _autoFire = false;
            _autoFireTimer = 0f;
            if (_autoFireToggle != null) { _autoFireToggle.SetValueWithoutNotify(false); }
        }

        public void Show()
        {
            ResetAssembler();
            ClearSelectionVisuals();
            RefreshOverrideControls();
            RefreshPreview();
            SetVisible(true);
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_panelRoot != null)
            {
                _panelRoot.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void Update()
        {
            if (!_visible)
            {
                return;
            }
            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                return;
            }

            MetabolicSliceBridge bridge = cell.MetabolicBridge;
            if (bridge != null && _combatReadout != null)
            {
                _combatReadout.text =
                    $"累计 DPS　总伤害 {bridge.SandboxTotalDamage:0.#}　命中 {bridge.SandboxHitCount}　" +
                    $"击杀 {bridge.SandboxKillCount}　耗时 {bridge.SandboxElapsedSinceFirstHit:0.#}s\n" +
                    $"均值DPS {bridge.SandboxAverageDps:0.#}　近{MetabolicSliceBridge.SandboxRollingWindowSeconds:0}s DPS {bridge.SandboxRollingDps:0.#}";
            }

            if (!_autoFire)
            {
                return;
            }
            _autoFireTimer -= Time.deltaTime;
            if (_autoFireTimer > 0f)
            {
                return;
            }
            _autoFireTimer = Mathf.Max(0.05f, _autoFireInterval);
            FireSandbox(cell);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
