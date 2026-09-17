using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.MetabolicSlice.Lineage;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Common;
using TEngine;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版萌生腔面板（M4-R00-02 队列②号项第6条，M3-R05）。
    ///
    /// 范围裁定：M3-R05 原文（`05_M1_M4_Backfill_And_Acceptance.md:141-145`）只提"模板"一词，
    /// 队列文档把"模板/萌生/回巢/野生器官"归为一组现状描述，不是四个独立验收项。四者信息量差异大——
    /// 模板编辑需要多选 toggle+冲突预览+多谱系改造，回巢需要过期检测+筛选控件，野生器官要从零建
    /// 交互链路（依赖队列条目 5 的 Interact 接线，尚未落地）——本次只落地信息量最小的"萌生"一块：
    /// 模板列表 + 成本 + 排队按钮，逐字照抄 <c>CellDebugHud.DrawLineageTemplateSection</c> 的既有
    /// 只读展示+转发操作口径（不重新实现任何后端校验，提交/萌生一律走
    /// <see cref="LineageRegistry"/>/<see cref="GerminationChamberRegistry"/> 既有真实入口）。
    /// 模板编辑、回巢、野生器官三块转正登记为独立后续故事，见返工队列文档同条目。
    ///
    /// 照抄 <see cref="BattleCarrierUIToolkit"/> 的"常驻单例、轮询自控显隐"模式，不接 [Window]/
    /// CellStageFlow._hub（本仓 UI Toolkit 无框架先例）。X 键默认关闭切换（该键位未被
    /// InputRouter/CellPlayerController/CameraDirector 占用）。
    /// </summary>
    public class BattleGerminationUIToolkit : MonoBehaviour
    {
        /// <summary>与 <see cref="Battle.CellDebugHud"/> 同一谱系 id——本期原型只有玩家一条谱系，
        /// 多谱系分组结构留钩子（见 <see cref="RefreshList"/>），不在本切片实现谱系切换 UI。</summary>
        private const string PlayerLineageId = "player";

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Label _balanceLabel;
        private ScrollView _templateList;
        private Label _noTemplateHint;
        private Label _feedbackLabel;

        private bool _panelOpen;

        /// <summary>供 execute_code 断言只读访问，同 BattleCarrierUIToolkit.Instance 先例。</summary>
        public static BattleGerminationUIToolkit Instance { get; private set; }

        /// <summary>只读探针：面板当前是否处于打开态。</summary>
        public bool IsPanelOpen => _panelOpen;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleGerminationUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 实读现况 HUD=0/Metabolic=3/Carrier=4/Sandbox=5/Draft=6/Overlay=10/Result=12，7 是空档。
            _document.sortingOrder = 7;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleGerminationUIToolkit] rootVisualElement 等待超时，萌生面板未初始化。");
                return;
            }
            CacheNodes();
            _root.style.display = DisplayStyle.None;
        }

        private void CacheNodes()
        {
            _balanceLabel = _root.Q<Label>("BalanceLabel");
            _templateList = _root.Q<ScrollView>("TemplateList");
            _noTemplateHint = _root.Q<Label>("NoTemplateHint");
            _feedbackLabel = _root.Q<Label>("FeedbackLabel");

            VisualElement panel = _root.Q<VisualElement>("BattleGerminationUI");
            Label titleBar = _root.Q<Label>("GerminationTitleBar");
            if (panel != null && titleBar != null)
            {
                var drag = new PanelDragManipulator(titleBar, panel, "germination");
                titleBar.AddManipulator(drag);
                drag.ApplyPersistedPosition();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.X))
            {
                _panelOpen = !_panelOpen;
            }

            if (_root == null)
            {
                return;
            }

            CellStageFlow cell = GameRoot.CellStage;
            bool visible = _panelOpen && cell != null && cell.IsRunning;
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible)
            {
                return;
            }

            RefreshList(cell);
        }

        /// <summary>逐字对齐 <c>CellDebugHud.DrawLineageTemplateSection</c> 的数据口径：只读
        /// <see cref="Lineage.GetLatest"/> 与 <see cref="GerminationChamberRegistry.PendingCount"/>，
        /// 从不遍历触发批量改造——提交模板与个体是否换装配物理上是两条互不联动的路径（M3-05/M3-06
        /// 起的结构性边界，本面板照旧展示，不新增任何联动）。</summary>
        private void RefreshList(CellStageFlow cell)
        {
            if (_templateList == null)
            {
                return;
            }

            _templateList.Clear();

            if (cell.Lineages == null || cell.GerminationChambers == null)
            {
                SetHintVisible(_noTemplateHint, true, "谱系或萌生腔模块未就绪");
                if (_balanceLabel != null)
                {
                    _balanceLabel.text = string.Empty;
                }
                return;
            }

            Lineage lineage = cell.Lineages.GetOrCreateLineage(PlayerLineageId, "玩家谱系");
            float balance = cell.BiomassLedger?.GetBalance(PlayerLineageId) ?? 0f;
            if (_balanceLabel != null)
            {
                _balanceLabel.text = $"{lineage.DisplayName}　生物质 {balance:F0}";
            }

            int count = 0;
            foreach (string templateName in lineage.TemplateNames)
            {
                PhenotypeTemplateVersion latest = lineage.GetLatest(templateName);
                if (latest == null)
                {
                    continue;
                }
                count++;

                var row = new VisualElement();
                row.AddToClassList("germination-row");

                var info = new Label(
                    $"{templateName}　最新 V{latest.Version}　主器官 {latest.OrganelleId}　基因 x{latest.GeneIds.Count}");
                info.AddToClassList("germination-row-info");
                row.Add(info);

                int pending = cell.GerminationChambers.PendingCount(PlayerLineageId);
                var meta = new Label($"萌生成本 {latest.BiomassCost:F0}　队列中 {pending}");
                meta.AddToClassList("dim");
                meta.AddToClassList("germination-row-meta");
                row.Add(meta);

                var btn = new Button { text = $"萌生（{latest.BiomassCost:F0} 生物质）" };
                btn.AddToClassList("germination-row-btn");
                string capturedName = templateName;
                btn.clicked += () => OnEnqueueClicked(capturedName);
                row.Add(btn);

                _templateList.Add(row);
            }

            SetHintVisible(_noTemplateHint, count == 0, "还没有提交过任何模板，请先在模板编辑入口提交");
        }

        private void OnEnqueueClicked(string templateName)
        {
            CellStageFlow cell = GameRoot.CellStage;
            if (cell?.GerminationChambers == null || _feedbackLabel == null)
            {
                return;
            }

            int ticket = cell.GerminationChambers.Enqueue(PlayerLineageId, templateName, out _, out string error);
            _feedbackLabel.text = ticket != 0
                ? $"已排入萌生腔（票据 {ticket}）"
                : $"萌生失败：{error}";
        }

        private static void SetHintVisible(Label hint, bool visible, string text = null)
        {
            if (hint == null)
            {
                return;
            }
            if (visible && text != null)
            {
                hint.text = text;
            }
            hint.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
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
