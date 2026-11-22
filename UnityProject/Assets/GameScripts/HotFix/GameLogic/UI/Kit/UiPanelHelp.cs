using System.Collections.Generic;
using GameLogic.Localization;
using GameLogic.Progression;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-UX-04（FG13 FGR-UX-042“每个面板右上角都有‘?’，链接到对应的图鉴系统说明”）：共用的“?”控件。
    /// <see cref="UiKitPanelHost"/> 在 UXML 实例化、子类绑定完之后调用 <see cref="Attach"/>：面板在 <see cref="CodexByPanel"/> 里、
    /// 标题栏（.mw-header）还没有自己的“?”（名字以 Help 结尾的按钮，FG1～FG8 各面板早先自带的那些）时，在关闭按钮前插一个“?”，
    /// 点它打开对应的图鉴系统说明（从机制界面打开 = 顺带解锁，与各面板自带的“?”一致），层级高于图鉴的面板会让图鉴临时盖到它上面。
    /// 直接持有 UIDocument、不走 UiKitPanelHost 的早期正式版面板（电路编辑、解析台、远征准备……）在绑定完视图后自己调 <see cref="Attach"/>；
    /// 它们没有 .mw-header 时，“?”钉在标题（名为 Title 的 Label）所在容器的右上角（<see cref="HeaderOf"/>）。
    /// 不在表里的面板必须在 <see cref="Exempt"/> 写明理由（FgGuidanceSelfCheck G1 按 UiKitPanelHost 全集 ∪ UXML 里带标题栏 / 关闭按钮 / Title 的界面全集核对：每个面板二者必居其一）。
    /// </summary>
    public static class UiPanelHelp
    {
        public const string ButtonClass = "mw-help-btn";

        /// <summary>UXML 名 → 图鉴系统说明条目（fg.TbCodexEntry.id）。</summary>
        public static readonly IReadOnlyDictionary<string, string> CodexByPanel = new Dictionary<string, string>
        {
            // 早先已自带“?”的面板（这里登记它们指向的条目，Attach 见到自带按钮就不再插）。
            { "BlackBoxPanel", "codex.blackbox.gallery" },
            { "CargoPanel", "codex.expedition.cargo" },
            { "DefenseOverviewPanel", "codex.defense.overview" },
            { "DefensePanel", "codex.defense.shield" },
            { "DropStationPanel", "codex.expedition.drop" },
            { "FusionPanel", "codex.research.fusion" },
            { "HeritageVaultPanel", "codex.heritage" },
            { "IntelPanel", "codex.intel.listening_post" },
            { "LootPickPanel", "codex.expedition.pick" },
            { "NarrativeLibraryPanel", "codex.narrative" },
            { "OutpostOverviewPanel", "codex.expedition.outpost" },
            { "ProductionPanel", "codex.build.building" },
            { "SignalCorePanel", "codex.signal.core_firmware" },
            { "TestRangePanel", "codex.research.range" },
            { "TurretPanel", "codex.defense.turret" },
            // FG15-UX-04 统一补上的。
            { "AwayReportPanel", "codex.economy.away" },
            { "BeltNodePanel", "codex.logistics.splitter" },
            { "BeltPortPanel", "codex.logistics.belt" },
            { "BuildModeHud", "codex.build.grid" },
            { "CalendarPanel", "codex.env.daynight" },
            { "CaravanTradePanel", "codex.event_director.caravan" },
            { "ConstructionQueuePanel", "codex.build.construction" },
            { "DiagnosisPanel", "codex.logistics.diagnosis" },
            { "DifficultyPanel", "codex.raid.difficulty" },
            { "EventHistoryPanel", "codex.event_director" },
            { "FirmwareLibraryPanel", "codex.firmware.library" },
            { "ItemsPanel", "codex.economy.items" },
            { "LayoutLibraryPanel", "codex.build.planning" },
            { "OverlayHud", "codex.logistics.diagnosis" },
            { "PipePanel", "codex.logistics.fluid" },
            { "PostgamePanel", "codex.postgame" },
            { "PowerPanel", "codex.logistics.power" },
            { "RaidResultPanel", "codex.raid.result" },
            { "ResearchTreePanel", "codex.research.tree" },
            { "RosterPanel", "codex.economy.roster" },
            { "RulesPanel", "codex.economy.rules" },
            { "SandboxPanel", "codex.sandbox" },
            { "StatsPanel", "codex.economy.stats" },
            { "StrategicMap", "codex.world.territories" },
            { "WorldPlacesHud", "codex.world.interior" },
            // FG15-UX-04 修复轮：直接持有 UIDocument 的正式版面板（各自在绑定后调 Attach，层级都低于图鉴）。
            { "AnalysisPanel", "codex.research.analysis" },
            { "BeaconLaunchPanel", "codex.mega.site" },
            { "CircuitBoardPanel", "codex.signal.uplink_port" },
            { "ExpeditionPrepPanel", "codex.expedition.protocol" },
            { "ExpeditionReturnPanel", "codex.expedition.protocol" },
            { "FactoryPanel", "codex.economy.assembly" },
            { "MissionLog", "codex.expedition.quests" },
            { "PrimitiveCraftPanel", "codex.economy.processing" },
        };

        /// <summary>不放“?”的界面（理由写在每行注释里：不是机制面板，或面板本身就是说明）。</summary>
        public static readonly IReadOnlyCollection<string> Exempt = new HashSet<string>
        {
            "MechanicCodexPanel", // 图鉴本身
            "GuidanceHelpPanel", // 引导与帮助本身就是帮助入口
            "GuidanceCard", // 引导卡片自带“在图鉴中查看”
            "PauseMenu", // 菜单：自带“图鉴”“引导与帮助”入口
            "SettingsPanel", // 设置：每一项都有悬停说明（FG15-UX-01），没有对应的机制条目
            "KeyBindingsPanel", // 按键：每个动作都有名称与说明，没有对应的机制条目
            "NewGamePanel", // 新游戏设置：每一项都有悬停说明，引导 world.newgame.first_open 负责首次说明
            "EndingSettlement", // 结局结算：一次性总结页，引导 ending.first_settlement 负责首次说明
            "ReactionLogPanel", // 反应记录：面板自带“反应图鉴”页签
            "NotificationHud", // 通知 HUD / 通知中心：通知本身就是说明，没有对应的机制条目
            "UiKitOverlay", // 确认框 / 浮层基础件，不是面板
            "UiKitGallery", // 开发用样例页（只在编辑器 / 开发包）
            "Minimap", // HUD：小地图，没有标题栏
            "NarrativeCutscene", // 演出播放器：全屏演出，不是面板
            "RaidWarningHud", // HUD：预警条（点它镜头飞到抵达点；图鉴入口在突袭 HUD / 结算面板）
            "ReactionPopupHud", // HUD：战场弹字，不接收点击
            "WorldBar", // HUD：时间条，没有标题栏
            "WorkOrderPanel", // HUD 层（sortingOrder 0）只读工单条，没有关闭按钮；图鉴入口在对应建筑面板
            "VictoryPage", // 胜利页：一次性全屏结算，只有“继续”，不是机制面板
            "HomeValleyFailure", // 家园失守页：一次性全屏阻断页（读档 / 回主菜单），不是机制面板
            "BattleCarrierUI", // Demo 0.1 细胞战斗旧界面（GameApp 保壳挂载），0.2 正式流程不打开
            "BattleGerminationUI", // 同上（Demo 0.1 萌生腔）
            "BattleOverlays", // 同上（Demo 0.1 卡组 / 商店 / 旧图鉴）
            "BattleResultUI", // 同上（Demo 0.1 结算回顾）
        };

        public static int AttachedCount { get; private set; }

        /// <summary>按面板名插“?”。返回新插的按钮（已有自带的、不在表里、没有标题栏时返回 null）。</summary>
        public static Button Attach(string panel, VisualElement root, int hostLayer = 0)
        {
            if (root == null || panel == null || !CodexByPanel.TryGetValue(panel, out string codexId))
            {
                return null;
            }
            VisualElement header = HeaderOf(root, out bool corner);
            if (header == null || CountHelp(header) > 0)
            {
                return null;
            }
            var b = new Button { name = panel + "HelpAuto", text = GameText.Get("ui.help.button") };
            b.AddToClassList("mw-btn");
            b.AddToClassList(ButtonClass);
            b.clicked += () => Open(codexId, hostLayer);
            UiTooltip.Attach(b, () =>
            {
                MechanicCodexEntry e = MechanicCodex.Find(codexId);
                string title = e != null ? GameText.Get(e.TitleKey) : codexId;
                return new TooltipContent { Title = GameText.Get("ui.help.button"), Body = GameText.Format("ui.help.tip", title) };
            });
            Button close = header.Q<Button>(className: "mw-close-btn");
            if (corner)
            {
                // 没有标题栏的早期面板：钉在面板右上角（FGR-UX-042“右上角”），不挤占原有布局。
                b.style.position = Position.Absolute;
                b.style.top = 4;
                b.style.right = 4;
                header.Add(b);
            }
            else if (close != null && close.parent != null)
            {
                close.parent.Insert(close.parent.IndexOf(close), b);
            }
            else
            {
                header.Add(b);
            }
            AttachedCount++;
            return b;
        }

        /// <summary>“?”的落点：.mw-header；没有时取名为 Title 的 Label 所在容器（<paramref name="corner"/> = 钉在右上角）。</summary>
        public static VisualElement HeaderOf(VisualElement root, out bool corner)
        {
            corner = false;
            VisualElement header = root?.Q(className: "mw-header");
            if (header == null && root != null)
            {
                header = root.Q<Label>("Title")?.parent;
                corner = header != null;
            }
            return header;
        }

        public static void Open(string codexId, int hostLayer)
        {
            MechanicCodexPanelUIToolkit.RequestAbove(hostLayer);
            MechanicCodex.Open(codexId);
        }

        /// <summary>标题栏里“?”的个数（自带的 *Help 按钮 + 统一插的）。</summary>
        public static int CountHelp(VisualElement header)
        {
            int n = 0;
            header.Query<Button>().ForEach(x =>
            {
                if (x.ClassListContains(ButtonClass) || (x.name != null && x.name.EndsWith("Help")))
                {
                    n++;
                }
            });
            return n;
        }
    }
}
