using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Blueprint;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.DebugTools;
using GameLogic.MetabolicSlice.Lineage;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;
using GameLogic.UI.Common;
using UnityEngine;

namespace GameLogic.UI.Battle
{
    /// <summary>
    /// IMGUI 调试 HUD + 进化选择界面。
    ///
    /// 为什么是 IMGUI 而不是 TEngine UIWindow：
    /// UIWindow 需要配套的 UI Prefab（`[Window(UILayer.UI, location:"XXX")]` 会去
    /// 加载同名 prefab），而 prefab 必须在 Unity 编辑器里做。本文件用 IMGUI 保证
    /// **框架落地当天就能跑起来、能验证数值与节奏**，不被美术资源阻塞。
    ///
    /// 正式 HUD（Spec §12）应做成 UIWindow + prefab 替换本文件，
    /// 数据来源（CellStageFlow 的公开属性）不变。
    /// </summary>
    public sealed class CellDebugHud : MonoBehaviour
    {
        private GUIStyle _label;
        private GUIStyle _big;
        private GUIStyle _cardBox;
        /// <summary>story-003（UI 重设计）：沙盒 7 维度覆盖的灰字二级说明样式，字号小于 <see cref="_label"/>，
        /// 不抢主控件的视觉优先级。</summary>
        private GUIStyle _hint;
        private bool _showDeck;
        private bool _showShop;
        private bool _showCodex;

        /// <summary>M3-08：谱系/表型模板面板（Y 键开关）。原型期信息面板，走同款 IMGUI 调试风格，
        /// 不做正式美术——玩家用它看清"改了什么/影响谁/花多少/谁仍是旧版"（Milestones.md M3-08）。
        /// 单谱系 MVP：本面板固定操作 <see cref="PlayerLineageId"/> 这一条谱系，多谱系 UI 留给后续故事。</summary>
        private bool _showLineage;
        private string _lineageTemplateNameInput = "assault";
        private string _lineageDoctrineInput = "keep_distance";
        private string _lineageSelectedOrganelle;
        private readonly List<string> _lineageSelectedGenes = new List<string>();
        private string _lineageCommitFeedback;
        private Vector2 _lineageScroll;

        /// <summary>
        /// §12.1 常驻信息已迁移到 <see cref="GameLogic.BattleMainUI"/>（UIWindow + prefab）。
        /// 本面板降级为开发开关，默认不绘制，F10 临时唤出用于和正式 HUD 比对数值
        /// （Preflight battle-ui/story-001 决策 G1）。
        /// </summary>
        private bool _showDebugHud;

        /// <summary>story-006：旧 IMGUI LookDev 沙盒是否激活。任务四（UI 重设计）后正式入口改为
        /// <see cref="BattleSandboxUIToolkit"/>，本字段降级为"对照模式"——运行中按 L 键手动切换，
        /// 菜单按钮不再置位它。始终存在（不用 #if 包），正式包里永远是 false。</summary>
        private bool _lookDevActive;

        /// <summary>story-003：自由装配沙盒状态——基元池多选 + 7 维度覆盖，逻辑全在
        /// <see cref="SandboxAssembler"/>（不依赖 UnityEngine，可被 execute_code 直接断言）。</summary>
        private readonly List<string> _sandboxGeneIds = new List<string>();
        private readonly List<string> _sandboxOrganelleIds = new List<string>();
        private SandboxOverrides _sandboxOverrides;
        private Vector2 _sandboxGeneScroll;
        private Vector2 _sandboxOrganScroll;
        private int _sandboxFireSeed = 1;
        /// <summary>沙盒"自动连发"——用户要求正式战斗与沙盒都自动攻击，沙盒侧保留手动开火按钮做精确单次测试，
        /// 额外加这个开关按间隔自动重复开火，复用同一条 Compose+ApplyEvent，不改 DPS 统计管线。</summary>
        private bool _sandboxAutoFire;
        private float _sandboxAutoFireInterval = 1f;
        private float _sandboxAutoFireTimer;

        /// <summary>story-003 D10：新 UI Toolkit 选卡面板（<see cref="BattleDraftUIToolkit"/>）默认事件触发式显示，
        /// 本旧 IMGUI 选卡面板改为按 K 键打开的对照入口，默认关闭避免与新 UI 同屏重复。</summary>
        private bool _showLegacyDraft;

        /// <summary>story-004 D8：新 UI Toolkit 覆盖面板（<see cref="BattleOverlayUIToolkit"/>）接管默认
        /// Tab/B/V 三键，本旧 IMGUI 卡组/商店/图鉴面板需要额外按 J 键打开对照模式才响应同一批按键。</summary>
        private bool _showLegacyOverlays;

        /// <summary>battle-ui-polish/story-003 D7：新 UI Toolkit 结算面板（<see cref="BattleResultUIToolkit"/>）
        /// 默认启用，本旧 IMGUI 结算块改按 I 键打开对照模式，默认关闭。</summary>
        private bool _showLegacyResult;

        private static readonly string[] RouteNames =
        {
            "无", "吞噬扩张", "机动猎食", "电化统治", "孢子繁殖", "菌毯筑巢", "异化污染", "跨路线",
        };

        /// <summary>全部面板可拖拽（用户要求）：位置持久化到 PlayerPrefs，windowId 100-106+110 互不冲突。
        /// 用 width&lt;=0 判断"尚未初始化"，首次绘制时才用 Screen 尺寸算默认居中位置。</summary>
        private Rect _menuRect;
        private Rect _hudRect;
        private Rect _lookDevRect;
        private Rect _draftRect;
        private Rect _deckRect;
        private Rect _shopRect;
        private Rect _codexRect;
        private Rect _playtestRect;
        private Rect _lineageRect;

        /// <summary>M3-08：单谱系 MVP 固定用这个 id——本仓尚无"创建/选择谱系"的玩法流程，
        /// 真实多谱系归属留给后续故事接线，不在本面板范围内。</summary>
        private const string PlayerLineageId = "player";

        /// <summary>fix(perf)：<see cref="DrawLineageUnitsSection"/>/<see cref="DrawLineageTemplateSection"/>
        /// 共用的一次遍历结果。原实现对 <c>GerminationChambers.Bindings</c> 循环两遍，且每条绑定都调
        /// <c>SimSnapshot.TryResolve</c>（对全体 Sim 实体线性扫描，见其源码），两者相乘 = O(绑定数×战场实体数)，
        /// 而这套计算原先直接摆在 OnGUI 里——IMGUI 的 Layout/Repaint/输入事件会让 OnGUI 每帧触发好几次，
        /// 于是这套 O(n) 又被乘了一遍。现在只在 <see cref="Update"/>（每帧恰好一次）里重算一次，OnGUI 只读缓存。</summary>
        private readonly List<LineageBindingRow> _lineageBindingRows = new List<LineageBindingRow>();

        /// <summary>模板名 → 该模板在玩家谱系下过期（非最新版本）的绑定数，随 <see cref="_lineageBindingRows"/> 同一趟算出。</summary>
        private readonly Dictionary<string, int> _lineageOutdatedCounts = new Dictionary<string, int>();

        private struct LineageBindingRow
        {
            public BinGames.Sim.SimEntityId EntityId;
            public GerminationChamberRegistry.UnitBinding Binding;
            public PhenotypeTemplateVersion LatestForBinding;
            public bool Outdated;
        }

        /// <summary>沙盒"自动连发"计时器——OnGUI 每帧可能因 Layout/Repaint 事件触发多次，计时放 Update 更可靠。</summary>
        private void Update()
        {
            if (_showLineage)
            {
                CellStageFlow lineageCell = GameRoot.CellStage;
                if (lineageCell != null && lineageCell.IsRunning)
                {
                    RefreshLineageBindingCache(lineageCell);
                }
            }

            if (!_lookDevActive || !_sandboxAutoFire)
            {
                return;
            }
            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                return;
            }
            _sandboxAutoFireTimer -= Time.deltaTime;
            if (_sandboxAutoFireTimer > 0f)
            {
                return;
            }
            _sandboxAutoFireTimer = Mathf.Max(0.05f, _sandboxAutoFireInterval);
            FireSandbox(cell);
        }

        private void OnGUI()
        {
            EnsureStyles();

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F10)
            {
                _showDebugHud = !_showDebugHud;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.I)
            {
                _showLegacyResult = !_showLegacyResult;
            }

            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                DrawMenu();
                return;
            }

            if (_lookDevActive)
            {
                DrawLookDevSandbox(cell);
                return;
            }

            if (cell.IsConsciousnessPlaytest)
            {
                DrawConsciousnessPlaytestGuide(cell);
                if (!cell.IsRunning)
                {
                    return;
                }
            }

            HandleQuickPanelHotkeys();
            HandleGmHotkeys(cell);
            if (_showDebugHud)
            {
                DrawHud(cell);
            }

            if (_showLegacyDraft && cell.Paused && cell.PendingOptions != null && cell.PendingOptions.Count > 0)
            {
                DrawDraft(cell);
            }

            if (_showDeck)
            {
                DrawDeck(cell);
            }

            if (_showShop)
            {
                DrawShop(cell);
            }

            if (_showCodex)
            {
                DrawCodex(cell);
            }

            if (_showLineage)
            {
                DrawLineage(cell);
            }
        }

        private void EnsureStyles()
        {
            if (_label != null)
            {
                return;
            }
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            _big = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            _cardBox = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13, alignment = TextAnchor.UpperLeft, wordWrap = true,
                padding = new RectOffset(10, 10, 10, 10), richText = true,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, wordWrap = true, richText = true,
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
            };
        }

        private void DrawMenu()
        {
            StageOutcome last = GameRoot.Director?.LastOutcome;
            bool hasResult = last != null && last.StageId != StageId.None;
            // 有结算内容时加高面板——固定 240 装不下头图+统计行，会被裁掉（story-005 AC 要求可见）。
            // battle-ui-polish/story-003 D7：新 UI Toolkit 结算面板默认接管展示，旧块仅在 _showLegacyResult 时加高。
            float h = (hasResult && _showLegacyResult) ? 400f : 240f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            h += 86f; // 两个开发入口，只在编辑器/开发构建加高，避免正式包菜单多空白
#endif
            if (_menuRect.width <= 0f)
            {
                _menuRect = new Rect(Screen.width * 0.5f - 200f, Screen.height * 0.5f - h * 0.5f, 400f, h);
            }
            else
            {
                _menuRect.height = h;
            }

            ImguiDragUtil.DrawDraggable(100, ref _menuRect, "细胞纪元", "gm_menu", id =>
            {
                GUILayout.Label("细胞纪元", _big);
                GUILayout.Space(6f);
                GUILayout.Label("从一个漂流细胞开始，在一小时内吞噬、突变、筑巢，\n成为这片微观海域的原核霸主。", _label);
                GUILayout.Space(14f);

                if (GUILayout.Button("开始漂流", GUILayout.Height(38f)))
                {
                    GameRoot.StartCellStage();
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (GUILayout.Button("LookDev 沙盒", GUILayout.Height(30f)))
                {
                    // 任务四（UI 重设计）：正式入口改唤起 UI Toolkit 面板；旧 IMGUI 面板
                    // 保留为对照，默认关闭，运行中按 L 键切换（见 HandleQuickPanelHotkeys）。
                    ResetSandboxAssembler();
                    GameRoot.StartLookDevSandbox();
                    BattleSandboxUIToolkit.Instance?.Show();
                }
                if (GUILayout.Button("M2-06 固定意识传递试玩", GUILayout.Height(30f)))
                {
                    GameRoot.StartConsciousnessPlaytest();
                }
#endif

                if (hasResult && _showLegacyResult)
                {
                    GUILayout.Space(10f);
                    GUILayout.Label(BuildResultText(last), _label);
                }
            });
        }

        private void DrawConsciousnessPlaytestGuide(CellStageFlow cell)
        {
            if (_playtestRect.width <= 0f)
            {
                // 230 而不是 184：多了「当前受控 / 候选数 / 上次 Tab 失败原因」两三行，
                // 按 184 画会把「结束试玩」按钮挤出面板，主持人收不了尾。
                _playtestRect = new Rect(Screen.width - 450f, 12f, 438f, 296f);
            }
            ImguiDragUtil.DrawDraggable(106, ref _playtestRect, "M2-06 固定试玩", "m2_06_playtest", id =>
            {
                GUILayout.Label("目标：在 8–12 分钟内自然完成四步（主持人不要提示键位）", _label);
                GUILayout.Label("1. 切到战略视角，选择友军并下达命令", _label);
                GUILayout.Label("2. 接管一名友军，使用它自身的动作", _label);
                GUILayout.Label("3. 对固定大目标的 P / S 接点完成一次精准切离", _label);
                GUILayout.Label("4. 退出接管并回到战略层继续下令", _label);
                GUILayout.Label("应急键位卡：M 视角　Tab 接管　鼠标左/右键 操作　P 接点类别", _hint);
                DrawControlSwitchDiagnostics(cell);

                // 2026-09-14 调试需求：屏幕上每个单位头顶标 UID，Scene 里的 GameObject 也用同一个号，
                // 这样玩家可以直接说「#7 那只移速不对」，不必再描述"左边那个"。
                bool mirror = GameLogic.Battle.Feedback.DevUnitGoMirror.Enabled;
                if (GUILayout.Button(mirror
                        ? "Hierarchy 真身：开（点此切回 GPU 实例化）"
                        : "Hierarchy 真身：关（点此开启，才能在 Hierarchy 里选中单位）",
                    GUILayout.Height(24f)))
                {
                    GameLogic.Battle.Feedback.DevUnitGoMirror.Enabled = !mirror;
                }
                GUILayout.Label(mirror
                    ? "Hierarchy → __DevUnitGoMirror/Units：名字以 #UID 开头，点中看 DevUnitProbe"
                    : "GPU 实例化不建 GameObject，Hierarchy 里选不中任何单位", _hint);
                if (GUILayout.Button("结束试玩并返回菜单", GUILayout.Height(26f)))
                {
                    cell.MarkAbandoned();
                    GameRoot.EndRun();
                }
            });

            DrawConsciousnessTargetMarkers(cell);
        }

        /// <summary>
        /// 2026-09-13 试玩反馈：Tab 按下去没反应时**玩家得不到任何信息**——
        /// 接管候选只取「距当前受控单位 `SimBridge.ControlSignalRange`(18) 以内的存活友军」
        /// （`SimWorld.GetControlCandidates`），跑远了候选就是空集，`RequestNextControlCandidate`
        /// 返回 TargetNotFound 后静默收场。试玩门面板不画常规调试 HUD，所以这条反馈必须自带。
        /// </summary>
        private void DrawControlSwitchDiagnostics(CellStageFlow cell)
        {
            CellPlayerController player = cell.PlayerController;
            if (player == null || cell.Sim == null)
            {
                return;
            }

            string body = cell.Sim.ControllingPlayerBody ? "玩家本体" : "友军（已接管）";
            // 信号范围 2026-09-14 暂时放开成全场，就别再显示那个米数了（会写成"1000000 米内"）。
            string scope = cell.Sim.ControlSignalRange > 9999f
                ? "全场友军循环"
                : $"{cell.Sim.ControlSignalRange:F0} 米内";
            GUILayout.Label($"当前受控：{body}　可接管候选：{player.LastControlCandidateCount} 个（{scope}）", _hint);

            if (player.LastControlSwitchResult != BinGames.Sim.ControlRequestResult.Success)
            {
                GUILayout.Label($"<color=#FFB060>上次 Tab 未生效：{ControlResultLabel(player.LastControlSwitchResult)}</color>", _hint);
            }
        }

        private static string ControlResultLabel(BinGames.Sim.ControlRequestResult result)
        {
            switch (result)
            {
                case BinGames.Sim.ControlRequestResult.TargetNotFound:
                    return "信号范围内没有可接管的友军——走近一点再按";
                case BinGames.Sim.ControlRequestResult.OutOfSignalRange:
                    return "目标超出接管信号范围";
                case BinGames.Sim.ControlRequestResult.TargetDead:
                    return "目标已死亡";
                case BinGames.Sim.ControlRequestResult.TargetNotFriendly:
                    return "目标不是友军";
                case BinGames.Sim.ControlRequestResult.CooldownActive:
                    return "接管冷却中";
                case BinGames.Sim.ControlRequestResult.CurrentUnitUnavailable:
                    return "当前受控单位已失效";
                case BinGames.Sim.ControlRequestResult.SimulationNotRunning:
                    return "模拟未运行";
                default:
                    return result.ToString();
            }
        }

        private void DrawConsciousnessTargetMarkers(CellStageFlow cell)
        {
            Camera cam = Camera.main;
            if (cam == null || cell.Sim == null || cell.Sim.World == null)
            {
                return;
            }

            BinGames.Sim.SimSnapshot snapshot = cell.Sim.Snapshot;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (!snapshot.IsAlive(i) ||
                    snapshot.LogicId[i] != CellStageFlow.ConsciousnessPlaytestTargetLogicId)
                {
                    continue;
                }

                BinGames.Sim.SimEntityId id = snapshot.EntityId[i];
                DrawConsciousnessPartMarker(cam, snapshot.Position[i], id,
                    BinGames.Sim.SimBodyPartSlot.Primary, "P", cell);
                DrawConsciousnessPartMarker(cam, snapshot.Position[i], id,
                    BinGames.Sim.SimBodyPartSlot.Secondary, "S", cell);
                return;
            }
        }

        private static void DrawConsciousnessPartMarker(Camera cam, Unity.Mathematics.float2 bodyPosition,
            BinGames.Sim.SimEntityId entityId, BinGames.Sim.SimBodyPartSlot slot,
            string label, CellStageFlow cell)
        {
            if (!cell.Sim.World.TryGetBodyPart(entityId, slot, out BinGames.Sim.SimBodyPart part))
            {
                return;
            }

            Unity.Mathematics.float2 point = bodyPosition + part.AimOffset;
            Vector3 screen = cam.WorldToScreenPoint(new Vector3(point.x, 0.4f, point.y));
            if (screen.z <= 0f)
            {
                return;
            }

            string state = part.Destroyed != 0 ? "已切离" : $"{part.Health:F0}/{part.MaxHealth:F0}";
            Rect rect = new Rect(screen.x - 42f, Screen.height - screen.y - 15f, 84f, 30f);
            Color before = GUI.color;
            GUI.color = part.Destroyed != 0
                ? new Color(0.45f, 0.45f, 0.45f, 0.9f)
                : new Color(0.3f, 1f, 0.9f, 0.95f);
            GUI.Box(rect, $"{label}  {state}");
            GUI.color = before;
        }

        /// <summary>battle-ui-polish/story-003 D4 最小暴露：新 UI Toolkit 结算面板复用同一份文案，不重写拼接逻辑。</summary>
        public static string BuildResultText(StageOutcome o)
        {
            string head = o.Victory
                ? "水流开始绕着你走。在这一寸微观海域里，你就是选择压力本身。"
                : DeathText(o.DeathCause);

            int m = (int)(o.DurationSeconds / 60f);
            int s = (int)(o.DurationSeconds % 60f);
            int phase = Mathf.Min(o.Statistics.PhasesReached, 6);
            // stage-outcome-lifetime-stats story-001：生涯统计只读展示。
            // o.Lifetime 是值类型，未产出快照（首次运行/未走退出结算）时为全零，照常显示 0 与 00:00。
            int bm = (int)(o.Lifetime.BestDurationSeconds / 60f);
            int bs = (int)(o.Lifetime.BestDurationSeconds % 60f);
            return $"{head}\n\n" +
                   $"存活 {m:00}:{s:00}　到达时期 {phase}/6　等级 {o.Level}　卡牌 {o.AllCards.Count}\n" +
                   $"主导路线 {RouteName(o.DominantRoute)}\n" +
                   $"吞噬 {o.Statistics.FoodDevoured}　击杀 {o.Statistics.EnemiesKilled}　" +
                   $"精英 {o.Statistics.ElitesKilled}\n" +
                   $"峰值体积 {o.Statistics.PeakVolume:F1}　峰值敌人 {o.Statistics.PeakEnemyCount}\n" +
                   $"生涯记录：累计击杀 {o.Lifetime.TotalKills}　累计吞噬 {o.Lifetime.TotalDevoured}　" +
                   $"最长存活 {bm:00}:{bs:00}　最高等级 {o.Lifetime.BestLevel}";
        }

        private static string DeathText(string cause)
        {
            switch (cause)
            {
                case "pollution": return "你追求的力量终于认不出宿主。";
                case "devoured": return "更大的口器找到了你。这片水域从不记得失败者的形状。";
                case "abandoned": return "你收起伪足，退回安全的水流——这场漂流的痕迹已经留在体内。";
                default: return "细胞膜再也无法维持形状。";
            }
        }

        /// <summary>story-003 D7 最小暴露：新 UI Toolkit 选卡面板复用同一份路线文案，不新建平行表。</summary>
        public static string RouteName(CardRoute r)
        {
            int i = (int)r;
            return i >= 0 && i < RouteNames.Length ? RouteNames[i] : "无";
        }

        private void DrawHud(CellStageFlow cell)
        {
            StatSheet st = cell.Stats;
            PhaseTimeline tl = cell.Timeline;

            if (_hudRect.width <= 0f)
            {
                _hudRect = new Rect(12f, 12f, 340f, 320f);
            }
            ImguiDragUtil.DrawDraggable(101, ref _hudRect, "调试HUD", "debug_hud", id => DrawHudContent(cell, st, tl));
        }

        private void DrawHudContent(CellStageFlow cell, StatSheet st, PhaseTimeline tl)
        {
            // 生态时期与进度
            if (tl?.Current != null)
            {
                GUILayout.Label(
                    $"<b>{tl.Current.Name}</b>　{tl.CurrentIndex + 1}/6　" +
                    $"{tl.PhaseProgress:P0}", _label);
                int rm = (int)(tl.RunElapsed / 60f);
                int rs = (int)(tl.RunElapsed % 60f);
                GUILayout.Label($"本局 {rm:00}:{rs:00}", _label);
            }

            float maxHp = st.Get(StatId.MaxHealth);
            BinGames.Sim.SimControlledUnitView controlled = default;
            bool hasControlled = cell.Sim != null &&
                cell.Sim.TryGetControlledPresentation(out controlled);
            string healthText = hasControlled ? $"{controlled.Health:F0}/{maxHp:F0}" : "--（无控制目标）";
            GUILayout.Label($"生命 {healthText}　体积 {st.Get(StatId.Volume):F2}", _label);

            // 进化能进度
            var prog = cell.Progression;
            GUILayout.Label(
                $"等级 {prog.Level}　进化能 {cell.Wallet.EvoEnergy:F0}/{prog.CurrentThreshold:F0}　" +
                $"({prog.Progress:P0})", _label);

            GUILayout.Label(
                $"营养质 {cell.Wallet.Nutrient:F0}　突变质 {cell.Wallet.Mutagen:F0}", _label);

            // 污染度只在有污染卡时显示（Spec §12.1）
            if (cell.Wallet.Pollution > 0f)
            {
                GUILayout.Label(
                    $"污染度 {cell.Wallet.Pollution:F0}/{st.Get(StatId.PollutionCap):F0}", _label);
            }

            GUILayout.Label(
                $"卡牌 {cell.Deck.TotalCards}　连吃 {cell.Devour.Combo}", _label);

            // 敌人规模——"规模抬升"本身就是表达
            GUILayout.Label(
                $"敌人 {cell.Director.LiveHostiles}　" +
                $"压力 {cell.Director.CurrentPressure:F0}/{cell.Director.Budget:F0}", _label);

            // 生态事件
            if (cell.Events.Active != null)
            {
                GUILayout.Label($"<b>生态事件：{cell.Events.Active.Name}</b>", _label);
            }
            else
            {
                GUILayout.Label($"下次事件 {cell.Events.NextEventCountdown:F0}s", _label);
            }

            // 技能槽与冷却
            GUILayout.Space(4f);
            var slots = cell.Abilities.Slots;
            string[] keys = { "空格", "Q", "E", "R", "F", "T", "G", "C" };
            for (int i = 0; i < slots.Count; i++)
            {
                var rt = slots[i];
                string key = i < keys.Length ? keys[i] : "?";
                string state = rt.Ready
                    ? $"就绪 x{rt.ChargesLeft}"
                    : $"{rt.CooldownLeft:F1}s";
                GUILayout.Label($"[{key}] {rt.Spec.Name}　{state}", _label);
            }

            GUILayout.Space(4f);
            GUILayout.Label(
                "F10 调试HUD　Tab 卡组　B 商店　V 图鉴\n" +
                "GM: F4 资源　F5 选卡　F6 精英选卡　F7 下一时期　F8 通关　` 全道具+技能　FPS " +
                (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("F0"),
                _label);
            GUILayout.Label("左下角：Carrier 器官栏（拖基因进插槽）", _label);
        }

        /// <summary>
        /// Tab/B/V 面板开关。独立于 <see cref="_showDebugHud"/>——DrawHud 默认降级隐藏，
        /// 但卡组/商店/图鉴入口不受影响（Preflight G1：这些界面本 story 不动）。
        /// </summary>
        private void HandleQuickPanelHotkeys()
        {
            if (Event.current.type != EventType.KeyDown)
            {
                return;
            }
            if (Event.current.keyCode == KeyCode.J)
            {
                _showLegacyOverlays = !_showLegacyOverlays;
            }
            else if (_showLegacyOverlays && Event.current.keyCode == KeyCode.Tab)
            {
                _showDeck = !_showDeck;
            }
            else if (_showLegacyOverlays && Event.current.keyCode == KeyCode.B)
            {
                _showShop = !_showShop;
            }
            else if (_showLegacyOverlays && Event.current.keyCode == KeyCode.V)
            {
                _showCodex = !_showCodex;
            }
            else if (Event.current.keyCode == KeyCode.K)
            {
                _showLegacyDraft = !_showLegacyDraft;
            }
            else if (Event.current.keyCode == KeyCode.Y)
            {
                _showLineage = !_showLineage;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            else if (Event.current.keyCode == KeyCode.L)
            {
                // sandbox-panel-dismiss R6：沙盒局内 L 由 BattleSandboxUIToolkit toggle UITK，
                // 不再打开 IMGUI 全屏对照（避免双遮罩）。
                CellStageFlow cell = GameRoot.CellStage;
                if (cell != null && cell.IsSandboxMode)
                {
                    return;
                }
                // 任务四：旧 IMGUI LookDev 沙盒对照模式，默认关闭；正式入口见 DrawMenu 的
                // "LookDev 沙盒" 按钮（唤起 BattleSandboxUIToolkit）。
                _lookDevActive = !_lookDevActive;
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// GM 热键：不消耗资源测选卡 / 跨生态时期 / 通关。
        /// 器官等后续游戏阶段尚未注册，F7/F8 只推进细胞阶段内时期与结算。
        /// </summary>
        private void HandleGmHotkeys(CellStageFlow cell)
        {
            if (Event.current.type != EventType.KeyDown)
            {
                return;
            }

            switch (Event.current.keyCode)
            {
                case KeyCode.F4:
                    cell.DebugGrantResources();
                    Event.current.Use();
                    break;
                case KeyCode.F5:
                    cell.DebugForceDraft(DraftKind.Normal);
                    Event.current.Use();
                    break;
                case KeyCode.F6:
                    cell.DebugForceDraft(DraftKind.Elite);
                    Event.current.Use();
                    break;
                case KeyCode.F7:
                    cell.DebugAdvancePhase();
                    Event.current.Use();
                    break;
                case KeyCode.F8:
                    cell.DebugFinishTimeline();
                    Event.current.Use();
                    break;
                case KeyCode.BackQuote: // ` / ~ ：避开 Unity F9「查找资产引用」
                    cell.DebugGrantAllMetabolicItems();
                    Event.current.Use();
                    break;
                case KeyCode.F12: // 相机验证态开关（topdown-hud-projectile-fix 语义对调）：默认正交俯视 ↔ 调试透视
                    cell.DebugToggleCameraVerifyMode();
                    Event.current.Use();
                    break;
            }
        }
#else
        private void HandleGmHotkeys(CellStageFlow cell) { }
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// story-003：自由装配沙盒面板——替换 story-006 的固定 7 组 A/B 夹具回放。左侧基因/器官多选
        /// 基元池 + 7 维度覆盖滑杆，右侧实时（R3）展示 <see cref="SandboxAssembler.Compose"/> 产出的
        /// HitEvent；预设模板按钮一键把原 <see cref="LookDevFixtures"/> 7 组载入覆盖值（Required 3，
        /// 不删除 LookDevFixtures.cs）。"开火"仍复用 <see cref="MetabolicSliceBridge.ApplyEvent"/>。
        /// </summary>
        private void DrawLookDevSandbox(CellStageFlow cell)
        {
            if (_lookDevRect.width <= 0f)
            {
                // 900x680（原 860x600）：加宽/加高以容纳每基元的灰字说明行（Preflight R2）。
                _lookDevRect = new Rect(12f, 12f, 900f, 680f);
            }
            ImguiDragUtil.DrawDraggable(102, ref _lookDevRect, "自由装配沙盒", "sandbox", id => DrawLookDevSandboxContent(cell));
        }

        private void DrawLookDevSandboxContent(CellStageFlow cell)
        {
            HitEvent preview = SandboxAssembler.Compose(_sandboxGeneIds, _sandboxOrganelleIds, _sandboxOverrides, seed: 1);

            GUILayout.Space(6f);
            GUILayout.Label("基因/器官多选 + 7 维度覆盖　右侧实时预览 HitEvent", _label);
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(320f));
            GUILayout.Label($"<b>基因</b>（已选 {_sandboxGeneIds.Count}）", _label);
            _sandboxGeneScroll = GUILayout.BeginScrollView(_sandboxGeneScroll, GUILayout.Height(190f));
            foreach (string id in GeneCatalog.AllGeneIds)
            {
                bool sel = _sandboxGeneIds.Contains(id);
                bool now = GUILayout.Toggle(sel, $"{GeneCatalog.GetDisplayName(id)}（{id}）", _label);
                if (now != sel)
                {
                    if (now) _sandboxGeneIds.Add(id); else _sandboxGeneIds.Remove(id);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Label($"<b>器官</b>（已选 {_sandboxOrganelleIds.Count}）", _label);
            _sandboxOrganScroll = GUILayout.BeginScrollView(_sandboxOrganScroll, GUILayout.Height(190f));
            foreach (KeyValuePair<string, OrganelleDef> kv in OrganelleCatalog.All)
            {
                // combat-identity-rework story-007（同 CodexRegistry.AllCarrierOrganelleEntries）：
                // 器官栏只出 AttackMethod==true 的攻击方式，已退役修饰/能量核心不应再出现在沙盒选择里。
                if (!kv.Value.AttackMethod)
                {
                    continue;
                }
                bool sel = _sandboxOrganelleIds.Contains(kv.Key);
                bool now = GUILayout.Toggle(sel, $"{kv.Value.DisplayName}（{kv.Key}　{kv.Value.Role}）", _label);
                if (now != sel)
                {
                    if (now) _sandboxOrganelleIds.Add(kv.Key); else _sandboxOrganelleIds.Remove(kv.Key);
                }
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("清空基元选择", GUILayout.Height(24f)))
            {
                _sandboxGeneIds.Clear();
                _sandboxOrganelleIds.Clear();
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.Width(300f));
            GUILayout.Label("<b>7 维度覆盖</b>（勾选启用，未勾选走真实链）", _label);

            GUILayout.Space(4f);
            GUILayout.Label("<b>形态区</b>", _label);
            DrawSandboxTextOverride("Shape", ref _sandboxOverrides.EnableShape, ref _sandboxOverrides.Shape);
            DrawSandboxDimensionHint(
                "弹体基础形态（远程 Bolt / 近战 Melee），由 Carrier 出口器官决定" +
                $"（{OrganExampleLabel("org_emitter")}→Bolt，{OrganExampleLabel("org_cilia")}→Melee）；" +
                "当前全仓库无中间器官/基因可产出，仅本面板覆盖可预览。");

            GUILayout.Space(4f);
            GUILayout.Label("<b>数量区</b>", _label);
            DrawSandboxSliderOverride("Count", ref _sandboxOverrides.EnableCount, ref _sandboxOverrides.Count, 1f, 10f);
            DrawSandboxDimensionHint($"命中次数（分裂/多段），典型产出：{OrganExampleLabel("org_scatter")}。");
            DrawSandboxSliderOverride("Scale", ref _sandboxOverrides.EnableScale, ref _sandboxOverrides.Scale, 0.1f, 5f);
            DrawSandboxDimensionHint($"弹体尺度/命中范围倍率，典型产出：{OrganExampleLabel("org_swell")}。");

            GUILayout.Space(4f);
            GUILayout.Label("<b>轨迹区</b>", _label);
            DrawSandboxSliderOverride("Spin", ref _sandboxOverrides.EnableSpin, ref _sandboxOverrides.Spin, -180f, 180f);
            DrawSandboxDimensionHint($"弹体自旋角速度，改变弹道/绕轨轨迹，典型产出：{OrganExampleLabel("org_flagella")}。");
            DrawSandboxSliderOverride("Orbit", ref _sandboxOverrides.EnableOrbit, ref _sandboxOverrides.Orbit, -5f, 5f);
            DrawSandboxDimensionHint("绕轨半径；当前全仓库无器官/基因真实产出，仅本面板覆盖可预览。");

            GUILayout.Space(4f);
            GUILayout.Label("<b>属性区</b>", _label);
            DrawSandboxTextOverride("Tag", ref _sandboxOverrides.EnableTag, ref _sandboxOverrides.Tag);
            DrawSandboxDimensionHint(
                "命中附加的属性标记，典型产出：" +
                $"{OrganExampleLabel("org_perox")}/{OrganExampleLabel("org_aqua")}/{OrganExampleLabel("org_ion")}。");
            DrawSandboxBoolOverride("Explode", ref _sandboxOverrides.EnableExplode, ref _sandboxOverrides.ExplodeOnHit);
            DrawSandboxDimensionHint($"命中后是否触发爆炸效果，典型产出：{OrganExampleLabel("org_lyso")}。");

            GUILayout.Space(6f);
            GUILayout.Label("<b>预设模板</b>（一键载入原 7 组 LookDev 夹具）", _label);
            IReadOnlyList<LookDevFixture> fixtures = LookDevFixtures.All;
            for (int i = 0; i < fixtures.Count; i++)
            {
                if (GUILayout.Button(fixtures[i].Name, GUILayout.Height(20f)))
                {
                    _sandboxGeneIds.Clear();
                    _sandboxOrganelleIds.Clear();
                    _sandboxOverrides = SandboxAssembler.OverridesFromEvent(fixtures[i].A);
                }
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.Width(240f));
            GUILayout.Label("<b>HitEvent 预览</b>", _label);
            GUILayout.Label($"Damage {preview.Damage:0.#}　Heal {preview.Heal:0.#}", _label);
            GUILayout.Label($"Shape {preview.Shape}", _label);
            GUILayout.Label($"Scale {preview.Scale:0.#}　Count {preview.Count:0.#}", _label);
            GUILayout.Label($"Spin {preview.Spin:0.#}　Orbit {preview.Orbit:0.#}", _label);
            GUILayout.Label($"Explode {preview.ExplodeOnHit}", _label);
            GUILayout.Label($"Tags {string.Join(",", preview.Tags)}", _label);

            GUILayout.Space(10f);
            if (GUILayout.Button("开火（打木桩）", GUILayout.Height(36f)))
            {
                FireSandbox(cell);
            }
            GUILayout.BeginHorizontal();
            _sandboxAutoFire = GUILayout.Toggle(_sandboxAutoFire, "自动连发", GUILayout.Width(70f));
            GUILayout.Label("间隔(s)", GUILayout.Width(48f));
            _sandboxAutoFireInterval = GUILayout.HorizontalSlider(_sandboxAutoFireInterval, 0.1f, 3f, GUILayout.Width(90f));
            GUILayout.Label(_sandboxAutoFireInterval.ToString("0.0"), GUILayout.Width(30f));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            DrawSandboxCombatReadout(cell);

            GUILayout.Space(6f);
            if (GUILayout.Button("退出沙盒", GUILayout.Height(28f)))
            {
                _lookDevActive = false;
                GameRoot.EndRun();
            }
        }

        /// <summary>story-004：累计 DPS/击杀读数。数据全在 <see cref="MetabolicSliceBridge"/>（沙盒态本地累加，
        /// 退出/重进即重置，Decision D3），本方法只读不持有状态。仅沙盒面板内绘制，不进正常战斗 HUD。</summary>
        private void DrawSandboxCombatReadout(CellStageFlow cell)
        {
            MetabolicSliceBridge bridge = cell.MetabolicBridge;
            if (bridge == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(
                $"<b>累计 DPS</b>　总伤害 {bridge.SandboxTotalDamage:0.#}　命中 {bridge.SandboxHitCount}　" +
                $"击杀 {bridge.SandboxKillCount}　耗时 {bridge.SandboxElapsedSinceFirstHit:0.#}s", _label);
            GUILayout.Label(
                $"均值DPS {bridge.SandboxAverageDps:0.#}　近{MetabolicSliceBridge.SandboxRollingWindowSeconds:0}s DPS {bridge.SandboxRollingDps:0.#}",
                _label);
            GUILayout.EndHorizontal();
        }

        /// <summary>story-003（UI 重设计）Preflight R2：每维度旁的灰字二级说明，靠 <see cref="_hint"/> 样式压低
        /// 视觉优先级，不与滑杆/开关抢注意力。</summary>
        private void DrawSandboxDimensionHint(string text)
        {
            GUILayout.Label(text, _hint);
        }

        /// <summary>把器官 id 转成"中文名（id）"展示文案，说明文字里引用真实产出者时用此复用
        /// <see cref="OrganelleCatalog"/> 的 DisplayName，避免手写文案与目录漂移。</summary>
        private static string OrganExampleLabel(string organelleId)
        {
            OrganelleDef def = OrganelleCatalog.Get(organelleId);
            return def != null ? $"{def.DisplayName}（{organelleId}）" : organelleId;
        }

        private void DrawSandboxSliderOverride(string label, ref bool enable, ref float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            enable = GUILayout.Toggle(enable, label, GUILayout.Width(64f));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(110f));
            GUILayout.Label(value.ToString("0.#"), GUILayout.Width(36f));
            GUILayout.EndHorizontal();
        }

        private void DrawSandboxTextOverride(string label, ref bool enable, ref string value)
        {
            GUILayout.BeginHorizontal();
            enable = GUILayout.Toggle(enable, label, GUILayout.Width(64f));
            value = GUILayout.TextField(value ?? string.Empty, GUILayout.Width(150f));
            GUILayout.EndHorizontal();
        }

        private void DrawSandboxBoolOverride(string label, ref bool enable, ref bool value)
        {
            GUILayout.BeginHorizontal();
            enable = GUILayout.Toggle(enable, label, GUILayout.Width(64f));
            value = GUILayout.Toggle(value, value ? "True" : "False", GUILayout.Width(80f));
            GUILayout.EndHorizontal();
        }

        /// <summary>单次开火，手动按钮与自动连发计时器共用同一条 Compose+ApplyEvent。</summary>
        private void FireSandbox(CellStageFlow cell)
        {
            _sandboxFireSeed++;
            HitEvent fireEvent = SandboxAssembler.Compose(_sandboxGeneIds, _sandboxOrganelleIds, _sandboxOverrides, _sandboxFireSeed);
            cell.MetabolicBridge?.ApplyEvent(fireEvent);
        }

        /// <summary>入沙盒前重置装配状态，避免带着上一局的选择/覆盖残留（story-003）。</summary>
        private void ResetSandboxAssembler()
        {
            _sandboxGeneIds.Clear();
            _sandboxOrganelleIds.Clear();
            _sandboxOverrides = new SandboxOverrides { Scale = 1f, Count = 1f, Shape = "Bolt", Tag = string.Empty };
            _sandboxFireSeed = 1;
            _sandboxAutoFire = false;
            _sandboxAutoFireTimer = 0f;
        }
#else
        private void DrawLookDevSandbox(CellStageFlow cell) { }
        private void FireSandbox(CellStageFlow cell) { }
#endif

        private void DrawDraft(CellStageFlow cell)
        {
            List<CardSpec> opts = cell.PendingOptions;

            // 半透明遮罩，强调这是暂停态——遮罩本身铺满全屏、不参与拖拽，只有下面的卡牌选择区可拖。
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float cardW = 300f;
            float cardH = 250f;
            float gap = 16f;
            float totalW = opts.Count * cardW + Mathf.Max(0, opts.Count - 1) * gap;
            float panelW = totalW + 40f;
            float panelH = cardH + 150f;

            if (_draftRect.width <= 0f)
            {
                _draftRect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);
            }
            else
            {
                _draftRect.width = panelW;
                _draftRect.height = panelH;
            }

            ImguiDragUtil.DrawDraggable(103, ref _draftRect, "选择奖励", "draft",
                id => DrawDraftContent(cell, opts, cardW, cardH, gap, totalW, panelW));
        }

        private void DrawDraftContent(CellStageFlow cell, List<CardSpec> opts, float cardW, float cardH, float gap, float totalW, float panelW)
        {
            GUI.Label(new Rect(0f, 4f, panelW, 44f),
                "进化能已充满，从下列奖励中选一项，为你的旅程注入新的变化。\n你仍留在细胞阶段，选择与是否吞噬无关。",
                new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter });

            float x0 = Mathf.Max(0f, (panelW - totalW) * 0.5f);
            float y0 = 52f;
            for (int i = 0; i < opts.Count; i++)
            {
                CardSpec c = opts[i];
                var r = new Rect(x0 + i * (cardW + gap), y0, cardW, cardH);
                GUI.Box(r, BuildCardText(c, cell), _cardBox);

                if (GUI.Button(new Rect(r.x + 10f, r.yMax - 42f, r.width - 20f, 32f), "领取奖励"))
                {
                    cell.ConfirmDraft(c.Id);
                    return;
                }
            }

            if (GUI.Button(new Rect(panelW * 0.5f - 60f, y0 + cardH + 14f, 120f, 28f), "跳过"))
            {
                cell.SkipDraft();
            }
        }

        private string BuildCardText(CardSpec c, CellStageFlow cell)
        {
            string rarity = RarityText(c.Rarity);
            string route = RouteName(c.Route);
            string synergy = SynergyHint(c, cell);

            string desc = string.IsNullOrEmpty(c.Desc) ? "（无说明，检查表）" : c.Desc;
            string s = $"<b>{c.Name}</b>\n" +
                       $"{rarity}　{route}\n\n" +
                       $"{desc}\n";

            if (c.MaxStack > 1)
            {
                int owned = cell.Deck.StackOf(c.Id);
                s += $"\n可叠加 {owned}/{c.MaxStack}";
            }
            if (!string.IsNullOrEmpty(synergy))
            {
                s += $"\n\n<b>联动</b>：{synergy}";
            }

            s += $"\n\n<b>触发</b>：{TriggerText(c.Trigger)}";

            if (!string.IsNullOrEmpty(c.DrawbackDesc))
            {
                s += $"\n\n<b>代价</b>：{c.DrawbackDesc}";
            }
            if (c.PollutionCost > 0f)
            {
                s += $"\n污染度 +{c.PollutionCost:F0}";
            }
            return s;
        }

        /// <summary>推荐联动：与已有卡的 SynergyTags 交集（Spec §12.2）。story-003 D7 最小暴露。</summary>
        public static string SynergyHint(CardSpec c, CellStageFlow cell)
        {
            if (c.SynergyTags == null)
            {
                return null;
            }
            var hits = new List<string>(3);
            IReadOnlyList<DeckEntry> owned = cell.Deck.Entries;
            for (int i = 0; i < owned.Count && hits.Count < 3; i++)
            {
                CardSpec o = owned[i].Spec;
                if (o?.SynergyTags == null)
                {
                    continue;
                }
                for (int t = 0; t < c.SynergyTags.Length; t++)
                {
                    if (o.HasSynergyTag(c.SynergyTags[t]) && !hits.Contains(o.Name))
                    {
                        hits.Add(o.Name);
                        break;
                    }
                }
            }
            return hits.Count == 0 ? null : string.Join("、", hits);
        }

        /// <summary>story-003 D7 最小暴露。</summary>
        public static string TriggerText(CardTrigger t)
        {
            switch (t)
            {
                case CardTrigger.Passive: return "获得即生效";
                case CardTrigger.OnDevour: return "吞噬时";
                case CardTrigger.OnKill: return "击杀时";
                case CardTrigger.OnHit: return "命中时";
                case CardTrigger.OnHurt: return "受伤时";
                case CardTrigger.OnDash: return "冲刺时";
                case CardTrigger.OnAbilityCast: return "施放技能时";
                case CardTrigger.OnLevelUp: return "升级时";
                case CardTrigger.OnLowHealth: return "生命过低时";
                case CardTrigger.OnPhaseStart: return "时期开始时";
                case CardTrigger.OnTick: return "周期触发";
                case CardTrigger.OnVolumeChanged: return "体积变化时";
                case CardTrigger.OnEcoEvent: return "生态事件时";
                default: return "获得即生效";
            }
        }

        private static string RarityText(CardRarity r)
        {
            return $"<color={RarityColor(r)}>{RarityLabel(r)}</color>";
        }

        /// <summary>纯文字稀有度（无富文本标记）。story-003 D7 最小暴露：新 UI Toolkit 选卡面板
        /// 只需要文字，配色交给 USS rarity-* class，不需要 IMGUI 富文本颜色标记。</summary>
        public static string RarityLabel(CardRarity r)
        {
            switch (r)
            {
                case CardRarity.Common: return "普通";
                case CardRarity.Rare: return "稀有";
                case CardRarity.Epic: return "史诗";
                case CardRarity.Aberrant: return "异化";
                case CardRarity.Legacy: return "原核遗产";
                default: return "普通";
            }
        }

        /// <summary>story-004 D8 最小暴露：UI Toolkit 覆盖面板（Deck/Codex）按稀有度上色复用同一色表。</summary>
        public static string RarityColor(CardRarity r)
        {
            switch (r)
            {
                case CardRarity.Common: return "#c8c8c8";
                case CardRarity.Rare: return "#5599ff";
                case CardRarity.Epic: return "#bb66ff";
                case CardRarity.Aberrant: return "#ff7733";
                case CardRarity.Legacy: return "#ffcc33";
                default: return "#c8c8c8";
            }
        }

        private void DrawDeck(CellStageFlow cell)
        {
            if (_deckRect.width <= 0f)
            {
                _deckRect = new Rect(Screen.width - 380f, 12f, 368f, Screen.height - 24f);
            }
            ImguiDragUtil.DrawDraggable(104, ref _deckRect, "已获得卡牌", "deck", id => DrawDeckContent(cell));
        }

        private void DrawDeckContent(CellStageFlow cell)
        {
            // 路线分布，让玩家看清自己在走哪条路（Spec §16 可读性对策）
            var counts = new int[8];
            cell.Deck.CopyRouteCounts(counts);
            for (int i = 1; i < counts.Length; i++)
            {
                if (counts[i] > 0)
                {
                    GUILayout.Label($"{RouteNames[i]}　{counts[i]}", _label);
                }
            }

            GUILayout.Space(8f);
            IReadOnlyList<DeckEntry> entries = cell.Deck.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                DeckEntry e = entries[i];
                string stack = e.Stack > 1 ? $" x{e.Stack}" : "";
                GUILayout.Label($"{RarityText(e.Spec.Rarity)} {e.Spec.Name}{stack}", _label);
            }
        }

        /// <summary>
        /// 临时商店面板（B 键开关，AC「可用临时 UI」）。窄口径实现（Preflight H2）：
        /// 固定商品目录，不阻塞玩法推进——只是覆盖层，不像 <see cref="DrawDraft"/> 那样暂停局内时间。
        /// </summary>
        private void DrawShop(CellStageFlow cell)
        {
            ShopSystem shop = cell.Shop;
            if (shop == null)
            {
                return;
            }

            float cardW = 220f;
            float cardH = 170f;
            float gap = 16f;
            float totalW = ShopSystem.SlotCount * cardW + (ShopSystem.SlotCount - 1) * gap;
            float panelW = totalW + 40f;
            float panelH = cardH + 150f;

            if (_shopRect.width <= 0f)
            {
                _shopRect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);
            }
            ImguiDragUtil.DrawDraggable(105, ref _shopRect, "局内商店", "shop",
                id => DrawShopContent(cell, shop, cardW, cardH, gap, totalW, panelW));
        }

        private void DrawShopContent(CellStageFlow cell, ShopSystem shop, float cardW, float cardH, float gap, float totalW, float panelW)
        {
            float x0 = (panelW - totalW) * 0.5f;
            float y0 = 30f;

            GUI.Label(new Rect(x0, 0f, totalW, 24f), $"营养质 {cell.Wallet.Nutrient:F0}", _label);

            for (int i = 0; i < ShopSystem.SlotCount; i++)
            {
                var r = new Rect(x0 + i * (cardW + gap), y0, cardW, cardH);
                ShopItemSpec item = shop.GetSlot(i);
                bool soldOut = shop.IsSoldOut(i);

                string text = $"<b>{item.Name}</b>\n{item.Desc}\n\n价格 {item.Cost:F0}";
                if (soldOut)
                {
                    text += "\n<color=#888888>已售出</color>";
                }
                GUI.Box(r, text, _cardBox);

                GUI.enabled = !soldOut;
                if (GUI.Button(new Rect(r.x + 10f, r.yMax - 36f, r.width - 20f, 28f),
                        soldOut ? "已售出" : "购买"))
                {
                    shop.TryBuy(i);
                }
                GUI.enabled = true;
            }

            if (GUI.Button(new Rect(panelW * 0.5f - 70f, y0 + cardH + 16f, 140f, 30f),
                    $"刷新（{ShopSystem.RefreshCost:F0}）"))
            {
                shop.TryRefresh();
            }

            if (GUI.Button(new Rect(panelW * 0.5f - 40f, y0 + cardH + 54f, 80f, 26f), "关闭"))
            {
                _showShop = false;
            }
        }

        /// <summary>
        /// 图鉴查看面板（V 键开关，AC「至少一种查看入口」）。窄口径实现（Preflight C1）：
        /// 只读本局内存态发现记录，不做跨会话持久化，因此上一局的发现在新的一局不会保留。
        /// </summary>
        private void DrawCodex(CellStageFlow cell)
        {
            CodexRegistry codex = cell.Codex;
            if (codex == null)
            {
                return;
            }

            if (_codexRect.width <= 0f)
            {
                _codexRect = new Rect(Screen.width * 0.5f - 220f, 12f, 440f, Screen.height - 24f);
            }
            ImguiDragUtil.DrawDraggable(106, ref _codexRect, "图鉴（发现进度跨局保存）", "codex", id => DrawCodexContent(cell, codex));
        }

        private void DrawCodexContent(CellStageFlow cell, CodexRegistry codex)
        {
            GUILayout.Space(6f);

            GUILayout.Label($"<b>敌人</b>　{codex.DiscoveredEnemyIds.Count}", _label);
            foreach (int id in codex.DiscoveredEnemyIds)
            {
                EnemySpec e = DataRegistry.Instance.GetEnemy(id);
                GUILayout.Label(e != null ? $"　{e.Name}" : $"　#{id}", _label);
            }

            GUILayout.Space(8f);
            GUILayout.Label($"<b>已解锁器官/基因</b>　{codex.DiscoveredCardIds.Count}", _label);
            foreach (int id in codex.DiscoveredCardIds)
            {
                CardSpec c = DataRegistry.Instance.GetCard(id);
                if (c == null)
                {
                    GUILayout.Label($"　#{id}", _label);
                    continue;
                }
                string kind = c.ContentKind == ContentKind.Organelle ? "器官"
                    : c.ContentKind == ContentKind.Gene ? "基因" : "卡牌";
                GUILayout.Label($"　[{kind}] {RarityText(c.Rarity)} {c.Name}", _label);
                GUILayout.Label($"　　{c.Desc}", _label);
            }
        }

        /// <summary>
        /// M3-08 模板编辑与传播 UI（Y 键开关）。纯信息展示+转发操作，不重新实现任何后端校验——
        /// 提交/萌生/回巢一律调用 <see cref="LineageRegistry"/>/<see cref="GerminationChamberRegistry"/>/
        /// <see cref="HomecomingRetrofitService"/> 的既有真实入口。验收核心「新玩家不会误以为一次提交
        /// 瞬间改变整队」：文案明写"旧版本个体不会自动更新"，且"已提交模板"区块只读 <c>lineage.GetLatest</c>
        /// 与既有绑定表，从不遍历触发批量改造——提交动作与个体是否换装配物理上是两条互不联动的路径
        /// （这条边界从 M3-05/M3-06 起就是结构性的，本面板只是如实展示，不新增任何联动）。
        /// </summary>
        private void DrawLineage(CellStageFlow cell)
        {
            if (_lineageRect.width <= 0f)
            {
                _lineageRect = new Rect(12f, 12f, 480f, Screen.height - 24f);
            }
            ImguiDragUtil.DrawDraggable(108, ref _lineageRect, "谱系与表型模板（M3-08）", "lineage",
                id => DrawLineageContent(cell));
        }

        /// <summary>fix(perf)：见 <see cref="_lineageBindingRows"/> 类型注释——每帧恰好算一次，
        /// 不放 OnGUI。只过滤存活实体（<c>TryResolve</c>+<c>IsAlive</c>），不改变原有展示口径：
        /// 个体列表不按谱系过滤（保持原 <see cref="DrawLineageUnitsSection"/> 行为），
        /// 过期计数只统计 <see cref="PlayerLineageId"/> 谱系下的绑定（保持原 <c>CountOutdatedBindings</c> 口径）。</summary>
        private void RefreshLineageBindingCache(CellStageFlow cell)
        {
            _lineageBindingRows.Clear();
            _lineageOutdatedCounts.Clear();

            if (cell.Sim == null || !cell.Sim.Running || cell.GerminationChambers == null)
            {
                return;
            }

            BinGames.Sim.SimSnapshot snap = cell.Sim.Snapshot;
            foreach (KeyValuePair<BinGames.Sim.SimEntityId, GerminationChamberRegistry.UnitBinding> pair in cell.GerminationChambers.Bindings)
            {
                BinGames.Sim.SimEntityId entityId = pair.Key;
                GerminationChamberRegistry.UnitBinding binding = pair.Value;
                if (!snap.TryResolve(entityId, out int index) || !snap.IsAlive(index))
                {
                    continue;
                }

                Lineage boundLineage = cell.Lineages.GetLineage(binding.LineageId);
                PhenotypeTemplateVersion latestForBinding = boundLineage?.GetLatest(binding.TemplateName);
                bool outdated = latestForBinding != null && !ReferenceEquals(latestForBinding, binding.Version);

                _lineageBindingRows.Add(new LineageBindingRow
                {
                    EntityId = entityId,
                    Binding = binding,
                    LatestForBinding = latestForBinding,
                    Outdated = outdated,
                });

                if (outdated && binding.LineageId == PlayerLineageId)
                {
                    _lineageOutdatedCounts.TryGetValue(binding.TemplateName, out int count);
                    _lineageOutdatedCounts[binding.TemplateName] = count + 1;
                }
            }
        }

        private void DrawLineageContent(CellStageFlow cell)
        {
            if (cell.Lineages == null || cell.Blueprints == null)
            {
                GUILayout.Label("谱系/蓝图模块未就绪", _label);
                return;
            }

            Lineage lineage = cell.Lineages.GetOrCreateLineage(PlayerLineageId, "玩家谱系");
            float balance = cell.BiomassLedger?.GetBalance(PlayerLineageId) ?? 0f;

            _lineageScroll = GUILayout.BeginScrollView(_lineageScroll);

            GUILayout.Label($"<b>{lineage.DisplayName}</b>　生物质 {balance:F0}", _label);
            GUILayout.Space(6f);

            DrawLineageBlueprintSection(cell);
            GUILayout.Space(10f);
            DrawLineageEditSection(cell);
            GUILayout.Space(10f);
            DrawLineageTemplateSection(cell, lineage);
            GUILayout.Space(10f);
            DrawLineageUnitsSection(cell);

            GUILayout.EndScrollView();
        }

        /// <summary>实施第 1 条：器官/基因来源与用途——读 <see cref="BlueprintRegistry.AllEntries"/>，
        /// 名称/说明查现有 Catalog（不复制目录内容）。</summary>
        private void DrawLineageBlueprintSection(CellStageFlow cell)
        {
            GUILayout.Label("<b>蓝图库（来源 / 用途）</b>", _label);
            int count = 0;
            foreach (BlueprintEntry entry in cell.Blueprints.AllEntries)
            {
                count++;
                bool isOrganelle = entry.Kind == BlueprintSourceKind.Organelle;
                string name = isOrganelle ? OrganelleCatalog.Get(entry.SourceId)?.DisplayName : GeneCatalog.GetDisplayName(entry.SourceId);
                string desc = isOrganelle ? OrganelleCatalog.Get(entry.SourceId)?.Description : GeneCatalog.GetDescription(entry.SourceId);
                string kindText = isOrganelle ? "器官" : "基因";
                string state = entry.Unlocked ? "已解锁" : $"解析中 {entry.Completeness:P0}";
                GUILayout.Label($"　[{kindText}] {name ?? entry.SourceId}　{state}　污染 {entry.Contamination:P0}", _label);
                if (!string.IsNullOrEmpty(desc))
                {
                    GUILayout.Label($"　　{desc}", _hint);
                }
            }
            if (count == 0)
            {
                GUILayout.Label("　（还没有解析过任何器官/基因，先在场上解析一件野生器官）", _hint);
            }
        }

        /// <summary>实施第 2 条：模板预览和冲突——选择已解锁主器官（单选）+ 2-4 个已解锁基因，
        /// 用 <see cref="LineageRegistry.PreviewCommit"/> 只读校验展示冲突原因，不产生任何版本；
        /// 提交按钮才真正调用 <see cref="LineageRegistry.CommitTemplate"/>。</summary>
        private void DrawLineageEditSection(CellStageFlow cell)
        {
            GUILayout.Label("<b>编辑表型模板</b>", _label);
            GUILayout.Label("模板名", _hint);
            _lineageTemplateNameInput = GUILayout.TextField(_lineageTemplateNameInput, GUILayout.Width(180f));
            GUILayout.Label("AI 教义（占位标签，行为决策树留给后续故事）", _hint);
            _lineageDoctrineInput = GUILayout.TextField(_lineageDoctrineInput, GUILayout.Width(180f));

            GUILayout.Label("主器官（已解锁，单选）", _hint);
            foreach (BlueprintEntry entry in cell.Blueprints.AllEntries)
            {
                if (entry.Kind != BlueprintSourceKind.Organelle || !entry.Unlocked)
                {
                    continue;
                }
                OrganelleDef def = OrganelleCatalog.Get(entry.SourceId);
                if (def == null || !def.AttackMethod || def.IsRetired)
                {
                    continue;
                }
                bool selected = _lineageSelectedOrganelle == entry.SourceId;
                if (GUILayout.Toggle(selected, def.DisplayName ?? entry.SourceId) != selected)
                {
                    _lineageSelectedOrganelle = selected ? null : entry.SourceId;
                }
            }

            GUILayout.Label("基因节点（已解锁，2-4 个，按点选顺序排列）", _hint);
            foreach (BlueprintEntry entry in cell.Blueprints.AllEntries)
            {
                if (entry.Kind != BlueprintSourceKind.Gene || !entry.Unlocked)
                {
                    continue;
                }
                int idx = _lineageSelectedGenes.IndexOf(entry.SourceId);
                bool selected = idx >= 0;
                string label = selected ? $"{idx + 1}. {GeneCatalog.GetDisplayName(entry.SourceId)}" : GeneCatalog.GetDisplayName(entry.SourceId);
                if (GUILayout.Toggle(selected, label) != selected)
                {
                    if (selected)
                    {
                        _lineageSelectedGenes.RemoveAt(idx);
                    }
                    else if (_lineageSelectedGenes.Count < LineageRegistry.MaxGeneSlots)
                    {
                        _lineageSelectedGenes.Add(entry.SourceId);
                    }
                }
            }

            string previewError = cell.Lineages.PreviewCommit(_lineageSelectedOrganelle, _lineageSelectedGenes);
            GUILayout.Label(previewError != null
                ? $"<color=#FFB060>冲突：{previewError}</color>"
                : "<color=#80FF80>预览：现在提交会成功</color>", _hint);

            GUI.enabled = previewError == null && !string.IsNullOrEmpty(_lineageTemplateNameInput);
            if (GUILayout.Button("提交新版本（旧版本个体不会自动更新）", GUILayout.Height(26f)))
            {
                PhenotypeTemplateVersion committed = cell.Lineages.CommitTemplate(
                    PlayerLineageId, _lineageTemplateNameInput, _lineageSelectedOrganelle,
                    _lineageSelectedGenes, _lineageDoctrineInput, out string commitError);
                _lineageCommitFeedback = committed != null
                    ? $"已提交「{_lineageTemplateNameInput}」V{committed.Version}——只有之后新生/完成回巢的个体会表达它"
                    : $"提交失败：{commitError}";
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_lineageCommitFeedback))
            {
                GUILayout.Label(_lineageCommitFeedback, _hint);
            }
        }

        /// <summary>实施第 3 条：新生数量、可改造数量与总成本——按模板名读最新版本 + 统计绑定表里
        /// 版本落后于最新的个体数，成本直接用 <see cref="PhenotypeTemplateVersion.BiomassCost"/>。</summary>
        private void DrawLineageTemplateSection(CellStageFlow cell, Lineage lineage)
        {
            GUILayout.Label("<b>已提交模板</b>", _label);
            int templateCount = 0;
            foreach (string templateName in lineage.TemplateNames)
            {
                PhenotypeTemplateVersion latest = lineage.GetLatest(templateName);
                if (latest == null)
                {
                    continue;
                }
                templateCount++;

                int outdatedCount = _lineageOutdatedCounts.TryGetValue(templateName, out int cachedOutdated) ? cachedOutdated : 0;
                int pending = cell.GerminationChambers?.PendingCount(PlayerLineageId) ?? 0;

                GUILayout.Label($"　{templateName}　最新 V{latest.Version}　主器官 {latest.OrganelleId}　基因 x{latest.GeneIds.Count}", _label);
                GUILayout.Label(
                    $"　　签名 {latest.Signature}　萌生成本 {latest.BiomassCost:F0}　" +
                    $"待回巢 {outdatedCount} 个（合计成本 {latest.BiomassCost * outdatedCount:F0}）　队列中 {pending}",
                    _hint);

                if (cell.GerminationChambers != null &&
                    GUILayout.Button($"萌生一个「{templateName}」（{latest.BiomassCost:F0} 生物质）", GUILayout.Height(22f)))
                {
                    int ticket = cell.GerminationChambers.Enqueue(PlayerLineageId, templateName, out _, out string enqueueError);
                    _lineageCommitFeedback = ticket != 0
                        ? $"已排入萌生腔（票据 {ticket}）"
                        : $"萌生失败：{enqueueError}";
                }
            }
            if (templateCount == 0)
            {
                GUILayout.Label("　（还没有提交过任何模板，先在上面编辑并提交）", _hint);
            }
        }

        /// <summary>实施第 4/5 条：个体面板显示谱系/表型/版本 + 筛选旧版个体并下达回巢命令——
        /// 只转发到 <see cref="HomecomingRetrofitService"/> 的既有入口，不自己判断能不能改造。</summary>
        private void DrawLineageUnitsSection(CellStageFlow cell)
        {
            GUILayout.Label("<b>已绑定个体（谱系 / 表型 / 版本）</b>", _label);

            if (cell.Sim == null || !cell.Sim.Running || cell.GerminationChambers == null)
            {
                GUILayout.Label("　（模拟未运行）", _hint);
                return;
            }

            int shown = 0;
            foreach (LineageBindingRow row in _lineageBindingRows)
            {
                shown++;

                string tag = row.Outdated ? "<color=#FFB060>待回巢（不会自动更新）</color>" : "最新版本";
                GUILayout.Label($"　#{row.EntityId.Value}　{row.Binding.LineageId}/{row.Binding.TemplateName} V{row.Binding.Version.Version}　{tag}", _label);

                if (!row.Outdated)
                {
                    continue;
                }

                if (cell.HomecomingRetrofit.IsRetrofitting(row.EntityId))
                {
                    GUILayout.Label("　　改造中…", _hint);
                    continue;
                }

                if (GUILayout.Button($"　回巢改造到 V{row.LatestForBinding.Version}", GUILayout.Height(20f)))
                {
                    HomecomingRetrofitService.RetrofitRejectReason reason = cell.HomecomingRetrofit.TryBeginRetrofit(row.EntityId);
                    if (reason == HomecomingRetrofitService.RetrofitRejectReason.None)
                    {
                        // 本服务没有建模"改造耗时"（M3-06 未做定时器，见其类型注释），
                        // 对玩家呈现为一次原子的"回巢改造"操作，不额外发明进度条。
                        cell.HomecomingRetrofit.CompleteRetrofit(row.EntityId);
                        _lineageCommitFeedback = $"#{row.EntityId.Value} 回巢改造完成";
                    }
                    else
                    {
                        _lineageCommitFeedback = $"#{row.EntityId.Value} 回巢改造被拒绝：{RetrofitReasonLabel(reason)}";
                    }
                }
            }

            if (shown == 0)
            {
                GUILayout.Label("　（还没有任何萌生腔生成的个体存活在场上）", _hint);
            }
        }

        private static string RetrofitReasonLabel(HomecomingRetrofitService.RetrofitRejectReason reason)
        {
            switch (reason)
            {
                case HomecomingRetrofitService.RetrofitRejectReason.AlreadyInProgress: return "已经在改造中";
                case HomecomingRetrofitService.RetrofitRejectReason.NotBound: return "该个体没有绑定记录";
                case HomecomingRetrofitService.RetrofitRejectReason.NoNewerVersion: return "已经是最新版本";
                case HomecomingRetrofitService.RetrofitRejectReason.NotNetworked: return "萌生腔未联网";
                case HomecomingRetrofitService.RetrofitRejectReason.Engaged: return "正在交战";
                case HomecomingRetrofitService.RetrofitRejectReason.CarryingKeyItem: return "携带关键物";
                case HomecomingRetrofitService.RetrofitRejectReason.TooFarFromChamber: return "距萌生腔太远";
                case HomecomingRetrofitService.RetrofitRejectReason.InsufficientBiomass: return "生物质不足";
                default: return reason.ToString();
            }
        }
    }
}
