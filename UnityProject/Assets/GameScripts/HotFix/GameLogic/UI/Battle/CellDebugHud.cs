using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.DebugTools;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;
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
        private bool _showDeck;
        private bool _showShop;
        private bool _showCodex;

        /// <summary>
        /// §12.1 常驻信息已迁移到 <see cref="GameLogic.BattleMainUI"/>（UIWindow + prefab）。
        /// 本面板降级为开发开关，默认不绘制，F10 临时唤出用于和正式 HUD 比对数值
        /// （Preflight battle-ui/story-001 决策 G1）。
        /// </summary>
        private bool _showDebugHud;

        /// <summary>story-006：LookDev 沙盒是否激活。始终存在（不用 #if 包），正式包里永远是 false——
        /// 真正的入口开关（菜单按钮/GameRoot.StartLookDevSandbox）才是 #if UNITY_EDITOR || DEVELOPMENT_BUILD。</summary>
        private bool _lookDevActive;
        private int _lookDevFixtureIndex;

        /// <summary>story-003 D10：新 UI Toolkit 选卡面板（<see cref="BattleDraftUIToolkit"/>）默认事件触发式显示，
        /// 本旧 IMGUI 选卡面板改为按 K 键打开的对照入口，默认关闭避免与新 UI 同屏重复。</summary>
        private bool _showLegacyDraft;

        /// <summary>story-004 D8：新 UI Toolkit 覆盖面板（<see cref="BattleOverlayUIToolkit"/>）接管默认
        /// Tab/B/V 三键，本旧 IMGUI 卡组/商店/图鉴面板需要额外按 J 键打开对照模式才响应同一批按键。</summary>
        private bool _showLegacyOverlays;

        private static readonly string[] RouteNames =
        {
            "无", "吞噬扩张", "机动猎食", "电化统治", "孢子繁殖", "菌毯筑巢", "异化污染", "跨路线",
        };

        private void OnGUI()
        {
            EnsureStyles();

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F10)
            {
                _showDebugHud = !_showDebugHud;
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
        }

        private void DrawMenu()
        {
            StageOutcome last = GameRoot.Director?.LastOutcome;
            bool hasResult = last != null && last.StageId != StageId.None;
            // 有结算内容时加高面板——固定 240 装不下头图+统计行，会被 BeginArea 裁掉（story-005 AC 要求可见）。
            float h = hasResult ? 400f : 240f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            h += 50f; // LookDev 沙盒按钮（story-006），只在编辑器/开发构建加高，避免正式包菜单多空白
#endif
            var r = new Rect(Screen.width * 0.5f - 200f, Screen.height * 0.5f - h * 0.5f, 400f, h);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 20f, r.width - 40f, r.height - 40f));

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
                _lookDevFixtureIndex = 0;
                _lookDevActive = true;
                GameRoot.StartLookDevSandbox();
            }
#endif

            if (hasResult)
            {
                GUILayout.Space(10f);
                GUILayout.Label(BuildResultText(last), _label);
            }

            GUILayout.EndArea();
        }

        private string BuildResultText(StageOutcome o)
        {
            string head = o.Victory
                ? "水流开始绕着你走。在这一寸微观海域里，你就是选择压力本身。"
                : DeathText(o.DeathCause);

            int m = (int)(o.DurationSeconds / 60f);
            int s = (int)(o.DurationSeconds % 60f);
            int phase = Mathf.Min(o.Statistics.PhasesReached, 6);
            return $"{head}\n\n" +
                   $"存活 {m:00}:{s:00}　到达时期 {phase}/6　等级 {o.Level}　卡牌 {o.AllCards.Count}\n" +
                   $"主导路线 {RouteName(o.DominantRoute)}\n" +
                   $"吞噬 {o.Statistics.FoodDevoured}　击杀 {o.Statistics.EnemiesKilled}　" +
                   $"精英 {o.Statistics.ElitesKilled}\n" +
                   $"峰值体积 {o.Statistics.PeakVolume:F1}　峰值敌人 {o.Statistics.PeakEnemyCount}";
        }

        private static string DeathText(string cause)
        {
            switch (cause)
            {
                case "pollution": return "你追求的力量终于认不出宿主。";
                case "devoured": return "更大的口器找到了你。这片水域从不记得失败者的形状。";
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

            GUILayout.BeginArea(new Rect(12f, 12f, 340f, 320f));

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
            float hp = cell.Sim.PlayerHealth;
            GUILayout.Label($"生命 {hp:F0}/{maxHp:F0}　体积 {st.Get(StatId.Volume):F2}", _label);

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
                "GM: F4 资源　F5 选卡　F6 精英选卡　F7 下一时期　F8 通关　` 全技能　FPS " +
                (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("F0"),
                _label);
            GUILayout.Label("M 代谢切片面板　装/卸　画/删有向边", _label);

            GUILayout.EndArea();
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
                    cell.DebugUnlockAllAbilities();
                    Event.current.Use();
                    break;
                case KeyCode.F12: // 相机验证态开关（story-005）：默认俯视 ↔ 透视景深
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
        /// LookDev 对照沙盒面板（story-006）：跳过正常战斗 HUD/Deck/Shop/Codex/Draft 绘制。
        /// 渲染 100% 复用 002 信号 + 004 Presenter + 003 运动，本方法唯一的新代码是调用
        /// <see cref="MetabolicSliceBridge.ApplyEvent"/>，不另起 Publish/Feedback 实现。
        /// </summary>
        private void DrawLookDevSandbox(CellStageFlow cell)
        {
            IReadOnlyList<LookDevFixture> fixtures = LookDevFixtures.All;
            if (fixtures.Count == 0)
            {
                return;
            }
            _lookDevFixtureIndex = ((_lookDevFixtureIndex % fixtures.Count) + fixtures.Count) % fixtures.Count;
            LookDevFixture fixture = fixtures[_lookDevFixtureIndex];

            GUILayout.BeginArea(new Rect(12f, 12f, 480f, 250f));
            GUI.Box(new Rect(0f, 0f, 480f, 250f), "");
            GUILayout.Space(6f);
            GUILayout.Label("<b>LookDev 对照沙盒</b>", _big);
            GUILayout.Label($"[{_lookDevFixtureIndex + 1}/{fixtures.Count}] {fixture.Name}　{fixture.AxisLabel}", _label);
            GUILayout.Space(4f);
            GUILayout.Label($"A: {FieldSummary(fixture.A)}", _label);
            GUILayout.Label($"B: {FieldSummary(fixture.B)}", _label);
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("发射 A", GUILayout.Height(30f)))
            {
                cell.MetabolicBridge?.ApplyEvent(fixture.A);
            }
            if (GUILayout.Button("发射 B", GUILayout.Height(30f)))
            {
                cell.MetabolicBridge?.ApplyEvent(fixture.B);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一组", GUILayout.Height(26f)))
            {
                _lookDevFixtureIndex--;
            }
            if (GUILayout.Button("下一组", GUILayout.Height(26f)))
            {
                _lookDevFixtureIndex++;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            if (GUILayout.Button("退出沙盒", GUILayout.Height(28f)))
            {
                _lookDevActive = false;
                GameRoot.EndRun();
            }

            GUILayout.EndArea();
        }

        private static string FieldSummary(HitEvent e) =>
            $"Shape={e.Shape}　Scale={e.Scale:0.#}　Count={e.Count:0.#}　Spin={e.Spin:0.#}　Orbit={e.Orbit:0.#}　Explode={e.ExplodeOnHit}";
#else
        private void DrawLookDevSandbox(CellStageFlow cell) { }
#endif

        private void DrawDraft(CellStageFlow cell)
        {
            List<CardSpec> opts = cell.PendingOptions;

            // 半透明遮罩，强调这是暂停态
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float cardW = 300f;
            float cardH = 250f;
            float gap = 16f;
            float totalW = opts.Count * cardW + (opts.Count - 1) * gap;
            float x0 = (Screen.width - totalW) * 0.5f;
            float y0 = (Screen.height - cardH) * 0.5f;

            GUI.Label(new Rect(0f, y0 - 96f, Screen.width, 30f), "选择奖励", _big);
            GUI.Label(new Rect(Screen.width * 0.5f - 340f, y0 - 62f, 680f, 44f),
                "进化能已充满，从下列奖励中选一项，为你的旅程注入新的变化。\n你仍留在细胞阶段，选择与是否吞噬无关。",
                new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter });

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

            if (GUI.Button(new Rect(Screen.width * 0.5f - 60f, y0 + cardH + 20f, 120f, 28f), "跳过"))
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
            var r = new Rect(Screen.width - 380f, 12f, 368f, Screen.height - 24f);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 10f, r.width - 20f, r.height - 20f));

            GUILayout.Label("<b>已获得卡牌</b>", _label);

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

            GUILayout.EndArea();
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
            float x0 = (Screen.width - totalW) * 0.5f;
            float y0 = Screen.height * 0.5f - cardH * 0.5f;

            GUI.Box(new Rect(x0 - 20f, y0 - 60f, totalW + 40f, cardH + 140f), "");
            GUI.Label(new Rect(x0, y0 - 46f, totalW, 28f),
                $"<b>局内商店</b>　营养质 {cell.Wallet.Nutrient:F0}", _big);

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

            if (GUI.Button(new Rect(x0 + totalW * 0.5f - 70f, y0 + cardH + 16f, 140f, 30f),
                    $"刷新（{ShopSystem.RefreshCost:F0}）"))
            {
                shop.TryRefresh();
            }

            if (GUI.Button(new Rect(x0 + totalW * 0.5f - 40f, y0 + cardH + 54f, 80f, 26f), "关闭"))
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

            var r = new Rect(Screen.width * 0.5f - 220f, 12f, 440f, Screen.height - 24f);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 10f, r.width - 20f, r.height - 20f));

            GUILayout.Label("<b>图鉴（本局发现，未跨局保存）</b>", _label);
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

            GUILayout.EndArea();
        }
    }
}
