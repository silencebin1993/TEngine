using System.Text;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using BinGames.Sim.Combat;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.SilentMind
{
    /// <summary>
    /// FG9-FAC-01（FGR-FAC-006“血条上标出阶段分界”“阶段转换有明确的文字提示”、FG09 第 9 节“精英和首领的血条显示名称和阶段”“重试前显示上次止步于第几阶段”）：
    /// 寂听主脑首领 HUD（UI Toolkit：结构 SilentMindHud.uxml、样式 SilentMindHudStyle.uss；C# 只绑数据；只在镜头看着地下中继大厅时显示）。只读首领状态机 <see cref="SilentMindBoss"/> 与战斗门面，不做业务判断。
    /// 内容：名称 + 阶段、核心血条（阶段三门槛处画分界线）、中继节点（存活数、被标记节点的波形、监听站情报 / 现形指出的真节点、跳转预警）、
    /// 偏折场状态（预警 / 展开 / 间隙倒计时）、全频静默预警与倒计时、核心暴露倍率、上次止步阶段。每 0.2 真实秒刷新一次。
    /// </summary>
    public sealed class SilentMindHudUIToolkit : MonoBehaviour
    {
        private const float RefreshSeconds = 0.2f;
        private const string VisibleClass = "silent-boss-root-visible";

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;
        private VisualElement _panel;
        private Label _title;
        private VisualElement _hpFill;
        private VisualElement _hpMark;
        private Label _hp;
        private Label _lines;
        private float _timer;

        /// <summary>自检读：最近一次刷新写出的文本（标题 + 全部行）与是否可见。</summary>
        public static string LastText { get; private set; } = string.Empty;
        public static bool LastVisible { get; private set; }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("SilentMindHud");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }
            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：与铸造首领 HUD 同层（两者不会同时可见：各自只在自己的首领场地被观察时显示）。
            _document.sortingOrder = 9;
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null)
            {
                Log.Error("[SilentMindHudUIToolkit] rootVisualElement 等待超时，首领 HUD 未初始化。");
                return;
            }
            _panel = root.Q<VisualElement>("SilentMindHudRoot");
            _title = root.Q<Label>("SilentMindTitle");
            _hpFill = root.Q<VisualElement>("SilentMindHpFill");
            _hpMark = root.Q<VisualElement>("SilentMindPhaseMark");
            _hp = root.Q<Label>("SilentMindHp");
            _lines = root.Q<Label>("SilentMindLines");
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            CampaignState s = CampaignSession.Current;
            bool visible = Compose(s, out string title, out float hpPct, out float markPct, out string hp, out string lines);
            if (!visible)
            {
                // FG10-BOSS-01：同一套首领 HUD 结构，寂听主脑不显示时显示洗礼塔（两者不会同时进行：一个在地下大厅，一个在星球表面）。
                visible = BaptismTowerHud.Compose(s, out title, out hpPct, out markPct, out hp, out lines);
            }
            LastVisible = visible;
            _panel.EnableInClassList(VisibleClass, visible);
            if (!visible)
            {
                return;
            }
            _title.text = title;
            // 运行时数值（血条宽度 / 阶段分界位置的百分比）直接写 style，布局与外观在 USS。
            _hpFill.style.width = new Length(hpPct * 100f, LengthUnit.Percent);
            _hpMark.style.left = new Length(markPct * 100f, LengthUnit.Percent);
            _hp.text = hp;
            _lines.text = lines;
            LastText = title + "\n" + hp + "\n" + lines;
        }

        /// <summary>HUD 的全部内容（纯函数，只读状态；自检直接调它断言文案）。不可见时返回 false。</summary>
        public static bool Compose(CampaignState s, out string title, out float hpPct, out float markPct, out string hp, out string lines)
        {
            title = hp = lines = string.Empty;
            hpPct = 0f;
            markPct = SilentMindBoss.Phase2HpFraction;
            if (s == null || !WorldView.IsObserved(SilentMindBoss.SiteId))
            {
                return false;
            }
            SilentBossRecord r = SilentMindBoss.Record(s);
            SilentMindPhase p = SilentMindBoss.PhaseOf(s);
            if (r == null || (p == SilentMindPhase.Dormant && r.LastReachedPhase == 0))
            {
                return false;
            }
            title = GameText.Get("boss.silent.hud.title") + " · " + SilentMindBoss.PhaseName(p);
            RegionEnemyRecord core = SilentMindBoss.Core(s);
            if (core != null && core.MaxHealth > 0f)
            {
                hpPct = Mathf.Clamp01(core.Health / core.MaxHealth);
                hp = GameText.Format("boss.silent.hud.hp", Mathf.CeilToInt(core.Health).ToString(), Mathf.CeilToInt(core.MaxHealth).ToString());
            }
            var sb = new StringBuilder(256);
            long now = GameClock.Ticks;
            CombatSite site = CombatSites.Get(SilentMindBoss.SiteId);
            switch (p)
            {
                case SilentMindPhase.Hiding:
                {
                    int alive = 0;
                    for (int i = 0; i < SilentMindBoss.Nodes.Count; i++)
                    {
                        RegionEnemyRecord n = SilentMindBoss.Node(s, i);
                        if (n != null && n.IsAlive)
                        {
                            alive++;
                        }
                    }
                    bool known = SilentMindBoss.RealNodeKnown(s) || SilentMindBoss.IsRevealed(s);
                    sb.Append(GameText.Format("boss.silent.hud.nodes", alive.ToString(),
                        known ? GameText.Format("boss.silent.hud.nodes_intel", SilentMindBoss.NodeLabel(r.RealNode)) : GameText.Get("boss.silent.hud.nodes_hint")));
                    for (int i = 0; i < SilentMindBoss.Nodes.Count; i++)
                    {
                        RegionEnemyRecord n = SilentMindBoss.Node(s, i);
                        if (n == null || !n.IsAlive || site == null || !site.TryGetEnemyUnit(n.EnemyInstanceId, out int unit) || !site.IsUnitMarked(unit))
                        {
                            continue;
                        }
                        sb.Append('\n').Append(GameText.Format(i == r.RealNode ? "boss.silent.hud.wave_real" : "boss.silent.hud.wave_decoy", SilentMindBoss.NodeLabel(i)));
                    }
                    if (r.JumpTarget >= 0)
                    {
                        float left = Mathf.Max(0f, (r.JumpAtTick - now) / (float)GameClock.StepHz);
                        sb.Append('\n').Append(GameText.Format("boss.silent.hud.jump", SilentMindBoss.NodeLabel(r.JumpTarget), left.ToString("0.0")));
                    }
                    if (SilentMindBoss.IsRevealed(s))
                    {
                        float left = Mathf.Max(0f, (r.RevealUntilTick - now) / (float)GameClock.StepHz);
                        sb.Append('\n').Append(GameText.Format("boss.silent.hud.revealed", SilentMindBoss.NodeLabel(r.RealNode), Mathf.CeilToInt(left).ToString()));
                    }
                    break;
                }
                case SilentMindPhase.Magnetic:
                {
                    if (site != null && core != null && site.TryGetEnemyUnit(core.EnemyInstanceId, out int cu) && site.TryGetUnitSilent(cu, out CombatSilentUnit sl, out _))
                    {
                        string key = sl.Deflect == 1 ? "boss.silent.hud.deflect_warn" : sl.Deflect == 2 ? "boss.silent.hud.deflect_up" : "boss.silent.hud.deflect_gap";
                        sb.Append(GameText.Format(key, Mathf.Max(0f, sl.DeflectT).ToString("0.0")));
                    }
                    sb.Append('\n').Append(GameText.Format("boss.silent.hud.next_phase", Mathf.RoundToInt(SilentMindBoss.Phase2HpFraction * 100f).ToString(),
                        Mathf.RoundToInt(SilentMindBoss.SilenceSeconds).ToString()));
                    break;
                }
                case SilentMindPhase.SilenceWarn:
                    sb.Append(GameText.Format("boss.silent.hud.silence_warn", Mathf.Max(0f, (r.PhaseEndTick - now) / (float)GameClock.StepHz).ToString("0.0")));
                    break;
                case SilentMindPhase.Silence:
                    sb.Append(GameText.Format("boss.silent.hud.silence", Mathf.CeilToInt(Mathf.Max(0f, (r.PhaseEndTick - now) / (float)GameClock.StepHz)).ToString()));
                    break;
                case SilentMindPhase.Exposed:
                    sb.Append(GameText.Format("boss.silent.hud.exposed", SilentMindBoss.ExposedDamageMult.ToString("0.#")));
                    break;
            }
            // FG09 第 9 节“精英和首领的血条显示名称和阶段”：大厅里存活、没潜伏的精英（寂听者）写名字与耐久。
            if (s.RegionEnemies != null && site != null)
            {
                foreach (RegionEnemyRecord e in s.RegionEnemies)
                {
                    if (e == null || !e.IsAlive || e.RegionId != SilentMindBoss.SiteId || !EnemyDefs.TryGet(e.EnemyTypeId, out GameConfig.fg.MechEnemy row)
                        || !EnemyDefs.IsElite(row) || !site.TryGetEnemyUnit(e.EnemyInstanceId, out int eu) || site.UnitHasFlag(eu, CombatUnitFlags.Stealthed))
                    {
                        continue;
                    }
                    sb.Append('\n').Append(GameText.Format("boss.silent.hud.elite", GameText.Get(row.NameKey), Mathf.CeilToInt(e.Health).ToString(),
                        Mathf.CeilToInt(e.MaxHealth).ToString()));
                }
            }
            if (r.LastReachedPhase > 0 && p != SilentMindPhase.Defeated)
            {
                sb.Append('\n').Append(GameText.Format("boss.silent.hud.last_phase", SilentMindBoss.PhaseName((SilentMindPhase)r.LastReachedPhase)));
            }
            lines = sb.ToString();
            return true;
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
