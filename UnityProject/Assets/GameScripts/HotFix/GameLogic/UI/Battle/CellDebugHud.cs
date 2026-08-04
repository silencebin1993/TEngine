using System.Collections.Generic;
using GameLogic.Cards;
using GameLogic.Core;
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

        private readonly string[] _routeNames =
        {
            "无", "吞噬扩张", "机动猎食", "电化统治", "孢子繁殖", "菌毯筑巢", "异化污染", "跨路线",
        };

        private void OnGUI()
        {
            EnsureStyles();

            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                DrawMenu();
                return;
            }

            DrawHud(cell);

            if (cell.Paused && cell.PendingOptions != null && cell.PendingOptions.Count > 0)
            {
                DrawDraft(cell);
            }

            if (_showDeck)
            {
                DrawDeck(cell);
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
            var r = new Rect(Screen.width * 0.5f - 200f, Screen.height * 0.5f - 120f, 400f, 240f);
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

            StageOutcome last = GameRoot.Director?.LastOutcome;
            if (last != null && last.StageId != StageId.None)
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
            return $"{head}\n\n" +
                   $"存活 {m:00}:{s:00}　等级 {o.Level}　卡牌 {o.AllCards.Count}\n" +
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

        private string RouteName(CardRoute r)
        {
            int i = (int)r;
            return i >= 0 && i < _routeNames.Length ? _routeNames[i] : "无";
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
            string[] keys = { "空格", "Q", "E", "R", "F" };
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
            GUILayout.Label("Tab 查看卡组　FPS " + (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("F0"), _label);

            GUILayout.EndArea();

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
            {
                _showDeck = !_showDeck;
            }
        }

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

            GUI.Label(new Rect(0f, y0 - 96f, Screen.width, 30f), "细胞内进化", _big);
            GUI.Label(new Rect(Screen.width * 0.5f - 340f, y0 - 62f, 680f, 44f),
                "进化能已充满。选择一种突变。你仍留在细胞阶段，但这次变化会改写\n接下来的吞噬、移动、战斗或生态关系。",
                new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter });

            for (int i = 0; i < opts.Count; i++)
            {
                CardSpec c = opts[i];
                var r = new Rect(x0 + i * (cardW + gap), y0, cardW, cardH);
                GUI.Box(r, BuildCardText(c, cell), _cardBox);

                if (GUI.Button(new Rect(r.x + 10f, r.yMax - 42f, r.width - 20f, 32f), "吸收突变"))
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

            string s = $"<b>{c.Name}</b>\n" +
                       $"{rarity}　{route}\n\n" +
                       $"{c.Desc}\n";

            if (c.MaxStack > 1)
            {
                int owned = cell.Deck.StackOf(c.Id);
                s += $"\n可叠加 {owned}/{c.MaxStack}";
            }
            if (!string.IsNullOrEmpty(synergy))
            {
                s += $"\n\n<b>联动</b>：{synergy}";
            }
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

        /// <summary>推荐联动：与已有卡的 SynergyTags 交集（Spec §12.2）。</summary>
        private static string SynergyHint(CardSpec c, CellStageFlow cell)
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

        private static string RarityText(CardRarity r)
        {
            switch (r)
            {
                case CardRarity.Common: return "<color=#c8c8c8>普通</color>";
                case CardRarity.Rare: return "<color=#5599ff>稀有</color>";
                case CardRarity.Epic: return "<color=#bb66ff>史诗</color>";
                case CardRarity.Aberrant: return "<color=#ff7733>异化</color>";
                case CardRarity.Legacy: return "<color=#ffcc33>原核遗产</color>";
                default: return "普通";
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
                    GUILayout.Label($"{_routeNames[i]}　{counts[i]}", _label);
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
    }
}
