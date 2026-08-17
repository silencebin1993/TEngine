using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.Cards;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Battle;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版进化选卡面板（battle-ui-toolkit/story-003）。事件触发式：进化能充满时
    /// （<c>cell.Paused &amp;&amp; cell.PendingOptions.Count>0</c>）自动弹出，不需要按键开关。
    /// 不接 [Window]/CellStageFlow._hub，照抄 <see cref="BattleHudToolkit"/>/<see cref="BattleMetabolicUIToolkit"/>
    /// 的"常驻单例、每帧轮询状态自控显隐"模式（D2）。旧 IMGUI <see cref="CellDebugHud.DrawDraft"/> 改绑
    /// K 键作对照入口，默认关闭（D10）。字段文案唯一数据源是 <see cref="CellStageFlow.PendingOptions"/>，
    /// 文案拼接复用 <see cref="CellDebugHud"/> 新暴露的 RarityLabel/RouteName/TriggerText/SynergyHint，
    /// 不新写一份平行的文案拼接逻辑（D7）。
    /// </summary>
    public class BattleDraftUIToolkit : MonoBehaviour
    {
        private const int CardCount = 3;

        private static readonly string[] RarityClasses =
        {
            "rarity-common", "rarity-rare", "rarity-epic", "rarity-aberrant", "rarity-legacy",
        };

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Button _btnSkip;

        private readonly VisualElement[] _cardRoot = new VisualElement[CardCount];
        private readonly Label[] _rarityChip = new Label[CardCount];
        private readonly Label[] _routeChip = new Label[CardCount];
        private readonly Label[] _cardName = new Label[CardCount];
        private readonly Label[] _mainDesc = new Label[CardCount];
        private readonly Label[] _triggerText = new Label[CardCount];
        private readonly Label[] _synergyText = new Label[CardCount];
        private readonly Label[] _drawbackText = new Label[CardCount];
        private readonly Label[] _pollutionCost = new Label[CardCount];
        private readonly Label[] _stackText = new Label[CardCount];
        private readonly Button[] _btnAbsorb = new Button[CardCount];

        private bool _visible;

        /// <summary>供 execute_code 验收探针只读访问，不参与显示逻辑本身。</summary>
        public bool IsVisible => _visible;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleDraftUI");
            // D3：PanelSettings 复用 story-001 已实证 ScaleWithScreenSize 的资源，本 story 零新建 .asset。
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 多个 UIDocument 共用同一份 PanelSettings 时，兄弟节点绘制顺序取决于
            // 各自异步加载完成的竞态顺序（非确定性）。显式给一个数值表锁定的
            // sortingOrder，让四个控制器的叠放关系确定（story-004 已验证的手法）。
            _document.sortingOrder = 6;

            // UIDocument.rootVisualElement 在刚赋值 panelSettings 后偶发仍为 null
            // （面板尚未在本帧完成挂载，实测复现），有限帧数轮询等它就绪，避免
            // CacheNodes() 对 null 根节点查询直接崩溃、控制器永久半初始化。
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleDraftUIToolkit] rootVisualElement 等待超时，选卡面板未初始化。");
                return;
            }
            CacheNodes();
            _root.style.display = DisplayStyle.None;
        }

        private void CacheNodes()
        {
            _btnSkip = _root.Q<Button>("BtnSkip");
            if (_btnSkip != null)
            {
                _btnSkip.clicked += () => GameRoot.CellStage?.SkipDraft();
            }

            for (int i = 0; i < CardCount; i++)
            {
                // D6：DraftCard0/1/2 是 <ui:Instance> 生成的 TemplateContainer，真正承载
                // rarity-* class 与全部字段的节点在其内部，命名固定为 "DraftCard"（同 DraftCard.uxml 根节点名）。
                VisualElement container = _root.Q<VisualElement>("DraftCard" + i);
                VisualElement card = container?.Q<VisualElement>("DraftCard");
                _cardRoot[i] = card;
                if (card == null)
                {
                    continue;
                }

                _rarityChip[i] = card.Q<Label>("RarityChip");
                _routeChip[i] = card.Q<Label>("RouteChip");
                _cardName[i] = card.Q<Label>("CardName");
                _mainDesc[i] = card.Q<Label>("MainDesc");
                _triggerText[i] = card.Q<Label>("TriggerText");
                _synergyText[i] = card.Q<Label>("SynergyText");
                _drawbackText[i] = card.Q<Label>("DrawbackText");
                _pollutionCost[i] = card.Q<Label>("PollutionCost");
                _stackText[i] = card.Q<Label>("StackText");
                _btnAbsorb[i] = card.Q<Button>("BtnAbsorb");

                int slot = i;
                if (_btnAbsorb[i] != null)
                {
                    _btnAbsorb[i].clicked += () => OnAbsorbClicked(slot);
                }
            }
        }

        /// <summary>D8：点击瞬间读最新 PendingOptions，不缓存卡片数据本身——槽位序号稳定，内容随每次进化能满而换新。</summary>
        private void OnAbsorbClicked(int slot)
        {
            CellStageFlow cell = GameRoot.CellStage;
            var opts = cell?.PendingOptions;
            if (opts == null || slot >= opts.Count)
            {
                return;
            }
            cell.ConfirmDraft(opts[slot].Id);
        }

        private void Update()
        {
            CellStageFlow cell = GameRoot.CellStage;
            bool show = cell != null && cell.IsRunning && cell.Paused
                        && cell.PendingOptions != null && cell.PendingOptions.Count > 0;
            _visible = show;

            if (_root == null)
            {
                return;
            }
            _root.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            RefreshCards(cell);
        }

        /// <summary>D9：逐字段对齐 CellDebugHud.BuildCardText，不新发明字段含义。</summary>
        private void RefreshCards(CellStageFlow cell)
        {
            var opts = cell.PendingOptions;

            for (int i = 0; i < CardCount; i++)
            {
                VisualElement card = _cardRoot[i];
                if (card == null)
                {
                    continue;
                }

                if (i >= opts.Count)
                {
                    card.style.display = DisplayStyle.None;
                    continue;
                }
                card.style.display = DisplayStyle.Flex;

                CardSpec c = opts[i];

                for (int rc = 0; rc < RarityClasses.Length; rc++)
                {
                    card.RemoveFromClassList(RarityClasses[rc]);
                }
                card.AddToClassList("rarity-" + c.Rarity.ToString().ToLowerInvariant());

                if (_rarityChip[i] != null)
                {
                    _rarityChip[i].text = CellDebugHud.RarityLabel(c.Rarity);
                }
                if (_routeChip[i] != null)
                {
                    _routeChip[i].text = CellDebugHud.RouteName(c.Route);
                }
                if (_cardName[i] != null)
                {
                    _cardName[i].text = c.Name;
                }
                if (_mainDesc[i] != null)
                {
                    _mainDesc[i].text = string.IsNullOrEmpty(c.Desc) ? "（无说明，检查表）" : c.Desc;
                }
                if (_triggerText[i] != null)
                {
                    _triggerText[i].text = $"触发：{CellDebugHud.TriggerText(c.Trigger)}";
                }

                string synergy = CellDebugHud.SynergyHint(c, cell);
                if (_synergyText[i] != null)
                {
                    if (string.IsNullOrEmpty(synergy))
                    {
                        _synergyText[i].style.display = DisplayStyle.None;
                    }
                    else
                    {
                        _synergyText[i].style.display = DisplayStyle.Flex;
                        _synergyText[i].text = $"联动：{synergy}";
                    }
                }

                if (_drawbackText[i] != null)
                {
                    if (string.IsNullOrEmpty(c.DrawbackDesc))
                    {
                        _drawbackText[i].style.display = DisplayStyle.None;
                    }
                    else
                    {
                        _drawbackText[i].style.display = DisplayStyle.Flex;
                        _drawbackText[i].text = $"代价：{c.DrawbackDesc}";
                    }
                }

                if (_pollutionCost[i] != null)
                {
                    if (c.PollutionCost > 0f)
                    {
                        _pollutionCost[i].style.display = DisplayStyle.Flex;
                        _pollutionCost[i].text = $"污染度 +{c.PollutionCost:F0}";
                    }
                    else
                    {
                        _pollutionCost[i].style.display = DisplayStyle.None;
                    }
                }

                if (_stackText[i] != null)
                {
                    if (c.MaxStack > 1)
                    {
                        _stackText[i].style.display = DisplayStyle.Flex;
                        _stackText[i].text = $"可叠加 {cell.Deck.StackOf(c.Id)}/{c.MaxStack}";
                    }
                    else
                    {
                        _stackText[i].style.display = DisplayStyle.None;
                    }
                }

                if (_btnAbsorb[i] != null)
                {
                    // 覆盖 uxml 静态占位"吸收突变"——文案已 pivot 成"领取奖励"（同 CellDebugHud.DrawDraft）。
                    _btnAbsorb[i].text = "领取奖励";
                }
            }
        }

        private void OnDestroy()
        {
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
