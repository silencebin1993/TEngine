using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Objective
{
    /// <summary>ER8 收尾（DEBT-ER6LOOP01-01 / UI-14 任务日志 + UI-09 战役地图）：OBJ-01～10 状态与回看、
    /// 三个固定区域的状态/当前所在/出征次数/带回内容、核心门三灯逐项文案。任务日志键（默认 J，可重绑）
    /// 开关，取消键或关闭按钮收起。只读展示，文字全部由下面的 Compose* 从追踪器、目标表与区域记录算出。</summary>
    public sealed class MissionLogUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.5f;

        /// <summary>战役地图的三行，顺序固定（与 UXML 的 RegionRow0～2 对应）。</summary>
        public static readonly string[] MapRegionIds =
        {
            HomeValleyLayout.RegionId,
            FracturedCityLayout.RegionId,
            FoundryOutpostLayout.RegionId,
        };

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _window;
        private Label _status;
        private Label _gate;
        private readonly List<VisualElement> _objRows = new List<VisualElement>();
        private readonly List<Label> _objStates = new List<Label>();
        private readonly List<Label> _objTitles = new List<Label>();
        private readonly List<Label> _objRegions = new List<Label>();
        private readonly Label[] _regionNames = new Label[3];
        private readonly Label[] _regionStates = new Label[3];
        private readonly Label[] _regionDetails = new Label[3];
        private bool _open;
        private float _refreshTimer;

        public static bool IsOpen { get; private set; }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("MissionLog");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：覆盖面板(10)。
            _document.sortingOrder = 10;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null)
            {
                Log.Error("[MissionLogUIToolkit] rootVisualElement 等待超时，任务日志未初始化。");
                return;
            }

            _window = root.Q<VisualElement>("MissionLogRoot");
            _status = root.Q<Label>("MissionLogStatus");
            _gate = root.Q<Label>("CoreGateLabel");
            for (int i = 0; i < CampaignObjectiveCatalog.All.Length; i++)
            {
                _objRows.Add(root.Q<VisualElement>("ObjRow" + i));
                _objStates.Add(root.Q<Label>("ObjRowState" + i));
                _objTitles.Add(root.Q<Label>("ObjRowTitle" + i));
                _objRegions.Add(root.Q<Label>("ObjRowRegion" + i));
            }
            for (int i = 0; i < MapRegionIds.Length; i++)
            {
                _regionNames[i] = root.Q<Label>("RegionRowName" + i);
                _regionStates[i] = root.Q<Label>("RegionRowState" + i);
                _regionDetails[i] = root.Q<Label>("RegionRowDetail" + i);
            }
            root.Q<Button>("MissionLogClose").clicked += () => SetOpen(false);
            // 拖动、点击置顶、窗口下方世界输入拦截统一走 UiWindowFocus（UI 红线第 9 条）。
            UiWindowFocus.Attach(_document, _window, root.Q<VisualElement>("MissionLogHeader"), "mission-log");
        }

        private void Update()
        {
            if (_window == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null || !ObjectiveHudUIToolkit.AnyRegionActive())
            {
                if (_open)
                {
                    SetOpen(false);
                }
                return;
            }

            if (InputRouter.ConsumeGlobalAction(GameActionId.ToggleMissionLog))
            {
                SetOpen(!_open);
            }
            else if (_open && InputRouter.ConsumeGlobalAction(GameActionId.Cancel, allowDuringModal: true))
            {
                SetOpen(false);
            }

            if (!_open)
            {
                return;
            }
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;
            Render(state);
        }

        private void SetOpen(bool open)
        {
            _open = open;
            IsOpen = open;
            _window.EnableInClassList("mw-hidden", !open);
            if (open)
            {
                _refreshTimer = 0f;
                UiWindowFocus.BringToFront(_document, _window);
            }
        }

        private void Render(CampaignState state)
        {
            for (int i = 0; i < _objRows.Count; i++)
            {
                ComposeObjectiveRow(state, i, out ObjectiveState objState, out string stateText, out string title, out string region);
                VisualElement row = _objRows[i];
                row.EnableInClassList("ml-obj-done", objState == ObjectiveState.Completed);
                row.EnableInClassList("ml-obj-active", objState == ObjectiveState.Active);
                row.EnableInClassList("ml-obj-locked", objState == ObjectiveState.Locked);
                _objStates[i].text = stateText;
                _objTitles[i].text = title;
                _objRegions[i].text = region;
            }
            _status.text = ComposeStatus(state);
            for (int i = 0; i < MapRegionIds.Length; i++)
            {
                ComposeRegionRow(state, MapRegionIds[i], out string name, out string stateText, out string detail);
                _regionNames[i].text = name;
                _regionStates[i].text = stateText;
                _regionDetails[i].text = detail;
            }
            _gate.text = ComposeGateText(state);
        }

        // ── 纯文本合成（自检直接断言，不经 UIDocument）──────────────────────

        /// <summary>第 <paramref name="index"/> 个目标的一行。未解锁的后续目标不剧透内容，只给编号。</summary>
        public static void ComposeObjectiveRow(CampaignState state, int index, out ObjectiveState objState,
            out string stateText, out string title, out string region)
        {
            ObjectiveDef def = CampaignObjectiveCatalog.All[index];
            objState = CampaignObjectiveTracker.StateOf(state, def.Id);
            switch (objState)
            {
                case ObjectiveState.Completed:
                    ObjectiveRecord record = state?.ObjectiveRecords?.FirstOrDefault(r => r != null && r.ObjectiveId == def.Id);
                    stateText = "已完成 " + FormatClock(record?.CompletedAtPlaySeconds ?? 0f);
                    break;
                case ObjectiveState.Active:
                    stateText = "进行中";
                    break;
                default:
                    stateText = "未解锁";
                    break;
            }
            bool locked = objState == ObjectiveState.Locked;
            title = locked ? $"{index + 1}. ？？？" : $"{index + 1}. {def.Title}";
            region = locked ? string.Empty : CampaignObjectiveCatalog.RegionDisplayName(def.RegionId);
        }

        public static string ComposeStatus(CampaignState state)
        {
            int completed = 0;
            foreach (ObjectiveDef def in CampaignObjectiveCatalog.All)
            {
                if (CampaignObjectiveTracker.IsCompleted(state, def.Id))
                {
                    completed++;
                }
            }
            return $"已完成 {completed}/{CampaignObjectiveCatalog.All.Length}｜战役时间 {FormatClock(state?.PlaySeconds ?? 0f)}";
        }

        /// <summary>战役地图的一行（UI-09）：区域名（当前所在加 ◆）、状态、当前目标是否在这里、未解锁时的
        /// 解锁条件、出征次数与带回内容、主核心状态。</summary>
        public static void ComposeRegionRow(CampaignState state, string regionId, out string name, out string stateText, out string detail)
        {
            bool here = state != null && state.CurrentRegionId == regionId;
            name = CampaignObjectiveCatalog.RegionDisplayName(regionId) + (here ? " ◆" : string.Empty);
            var parts = new List<string>(5);
            ObjectiveDef current = CampaignObjectiveCatalog.Get(CampaignObjectiveTracker.CurrentObjectiveId(state));
            if (current != null && current.RegionId == regionId)
            {
                parts.Add("当前目标在这里：" + current.Title);
            }
            if (here)
            {
                parts.Add("你现在在这里（◆）");
            }

            if (regionId == HomeValleyLayout.RegionId)
            {
                stateText = "家园";
                if (!here)
                {
                    parts.Add("远征队的出发与撤离地");
                }
                detail = string.Join("｜", parts);
                return;
            }

            RegionRecord record = regionId == FracturedCityLayout.RegionId ? FracturedCityRegion.Find(state)
                : regionId == FoundryOutpostLayout.RegionId ? FoundryOutpostRegion.Find(state)
                : null;
            RegionState regionState = record?.State ?? RegionState.Locked;
            bool bossInitialized = regionId == FoundryOutpostLayout.RegionId && record != null && FoundryOutpostCoreBoss.IsInitialized(record);
            bool bossDestroyed = bossInitialized && FoundryOutpostCoreBoss.GetState(record) == CoreBossState.Destroyed;
            stateText = RegionStateText(regionId, regionState, here, bossDestroyed);

            if (regionState == RegionState.Locked)
            {
                // UI-09：破碎都市需信号塔，铸造外围需完成目标 6。
                parts.Add(regionId == FracturedCityLayout.RegionId
                    ? "解锁条件：修复归还谷地的信号塔"
                    : $"解锁条件：完成目标 6「{CampaignObjectiveCatalog.TitleOf(CampaignObjectiveTracker.Obj06)}」");
            }
            else
            {
                int departures = record?.ExpeditionCount ?? 0;
                parts.Add(departures > 0 ? $"已出征 {departures} 次" : "尚未出征");
            }
            string recovered = RecoveredItems(state, regionId);
            if (!string.IsNullOrEmpty(recovered))
            {
                parts.Add("已带回：" + recovered);
            }
            if (bossInitialized)
            {
                parts.Add("主核心：" + FoundryOutpostCoreBoss.DisplayPhaseText(record));
            }
            detail = string.Join("｜", parts);
        }

        /// <summary>UI-14：三灯分别写明缺什么（“重炮未解析”“未保存熔穿过载蓝图”“没有现役机实装”），完成的打勾。</summary>
        public static string ComposeGateText(CampaignState state)
        {
            FoundryOutpostRegion.CoreGateLights lights = FoundryOutpostRegion.ComputeCoreGateLights(state);
            return "核心分区封锁门：" +
                   (lights.CannonAnalyzed ? "√ 重炮已解析" : "○ 重炮未解析") + "｜" +
                   (lights.OverloadBlueprintSaved ? "√ 已保存熔穿过载蓝图" : "○ 未保存熔穿过载蓝图") + "｜" +
                   (lights.MachineEquipped ? "√ 已有现役机实装熔穿过载" : "○ 没有现役机实装熔穿过载") +
                   (lights.AllReady ? "——三灯全亮，核心分区已开放" : "——三灯全亮才能进入核心分区");
        }

        /// <summary>区域状态。Active 只表示“出征过、还没肃清”，人不在那里时不写“远征中”；铸造前哨外围肃清的
        /// 只是外围（带回重炮），主核心还在时不能写成整片已肃清。</summary>
        private static string RegionStateText(string regionId, RegionState state, bool here, bool bossDestroyed)
        {
            switch (state)
            {
                case RegionState.Available:
                    return here ? "远征中" : "可出征";
                case RegionState.Active:
                    return here ? "远征中" : "未肃清，可再出征";
                case RegionState.Cleared:
                    if (regionId != FoundryOutpostLayout.RegionId)
                    {
                        return "已肃清";
                    }
                    return bossDestroyed ? "主核心已摧毁" : here ? "核心进攻中" : "外围已肃清";
                default:
                    return "未解锁";
            }
        }

        private static string RecoveredItems(CampaignState state, string regionId)
        {
            if (state?.RegionQuestItems == null)
            {
                return string.Empty;
            }
            IEnumerable<string> names = state.RegionQuestItems
                .Where(q => q != null && q.RegionId == regionId && q.State == RegionQuestItemState.Recovered)
                .Select(q => q.ContentId == FoundryOutpostLayout.CoreDataContentId ? "核心数据" : Campaign.Feedback.FeedbackCues.QuestItemName(q.ContentId))
                .Distinct();
            return string.Join("、", names);
        }

        private static string FormatClock(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }

        private void OnDestroy()
        {
            IsOpen = false;
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
