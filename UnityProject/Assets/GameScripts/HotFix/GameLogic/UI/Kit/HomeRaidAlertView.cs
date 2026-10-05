using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-07（FG06 FGR-DEF-041）：远征 HUD 的家园遇袭紧急通知 + 家园状态小窗。挂在左上角突袭条（<see cref="RaidWarningHudUIToolkit"/>，同一个 UXML / 宿主）的最上面，
    /// 由突袭条每 raid.hud.refresh_seconds 真实秒调用 <see cref="Refresh"/>。判定、文字与两个选择都在 <see cref="HomeRaidAlertService"/>，这里只做显示与按钮。
    /// - 弹窗（还有没做选择的一波）：⚠ 紧急、两个选择的说明、“跳回家园（H）”“留在远征队（Esc）”；Esc 在取消栈里压一层 = 留在远征队。
    /// - 小窗：倒计时（游戏时间）、归还核心耐久条、关键建筑完好数与最危险的一座、剩余敌人；做过选择后写“这一波你选择了……”，“跳回家园”一直可用。
    /// - 弹窗第一次出现发引导钩子（内容在 FG15-UX-04）；音乐阶段没变时补一声预警提示音（阶段变化本身会响）。
    /// </summary>
    public sealed class HomeRaidAlertView
    {
        private VisualElement _panel;
        private VisualElement _alert;
        private Label _alertTitle;
        private Label _hint;
        private Label _countdown;
        private Label _homeTitle;
        private VisualElement _coreFill;
        private Label _core;
        private Label _keys;
        private Label _enemies;
        private Label _chosen;
        private Button _jump;
        private Button _stay;
        private Label _status;

        private readonly HomeRaidStatus _home = new HomeRaidStatus();
        private readonly HashSet<int> _alertedWaves = new HashSet<int>();
        private float _collectTimer;
        private float _lastRealTime = -1f;
        private bool _prevPhaseActive;
        private string _statusText = string.Empty;
        private float _statusUntil;
        private float _evalTimer;
        private long _lastTicks = -1;
        private bool _sessionLive;
        private bool _wasShown;
        private readonly Action _escStay;

        public HomeRaidAlertView()
        {
            // 复审 P1：只有取消键正好关的是这一层才算“留在远征队”；被别的途径撤层只撤层，下次刷新重新压层。
            _escStay = () =>
            {
                if (UiEscapeStack.IsCancelClosing(this))
                {
                    ClickStay();
                }
            };
        }

        /// <summary>自检注入真实时间（为 null 时读 <see cref="Time.unscaledTime"/>）。</summary>
        public static Func<float> RealTimeForTests;
        private static float RealNow => RealTimeForTests?.Invoke() ?? Time.unscaledTime;

        // ── 自检读点 ──
        public bool PanelVisible => Visible(_panel);
        public bool PopupVisible => Visible(_panel) && Visible(_alert);
        public bool StayVisible => Visible(_stay);
        public string AlertTitleText => _alertTitle?.text ?? string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public string CountdownText => _countdown?.text ?? string.Empty;
        public string HomeTitleText => _homeTitle?.text ?? string.Empty;
        public string CoreText => _core?.text ?? string.Empty;
        public string KeysText => _keys?.text ?? string.Empty;
        public string EnemiesText => _enemies?.text ?? string.Empty;
        public string ChosenText => Visible(_chosen) ? _chosen.text : string.Empty;
        public string StatusText => Visible(_status) ? _status.text : string.Empty;
        public Button JumpButton => _jump;
        public Button StayButton => _stay;
        public VisualElement PanelElement => _panel;
        public float CoreFillPercent { get; private set; }
        public int Collects { get; private set; }
        public int Stings { get; private set; }
        public int PopupShows { get; private set; }

        public void Bind(VisualElement root)
        {
            _panel = root.Q<VisualElement>("RaidAwayPanel");
            _alert = root.Q<VisualElement>("RaidAwayAlert");
            _alertTitle = root.Q<Label>("RaidAwayAlertTitle");
            _hint = root.Q<Label>("RaidAwayHint");
            _countdown = root.Q<Label>("RaidAwayCountdown");
            _homeTitle = root.Q<Label>("RaidAwayHomeTitle");
            _coreFill = root.Q<VisualElement>("RaidAwayCoreFill");
            _core = root.Q<Label>("RaidAwayCore");
            _keys = root.Q<Label>("RaidAwayKeys");
            _enemies = root.Q<Label>("RaidAwayEnemies");
            _chosen = root.Q<Label>("RaidAwayChosen");
            _jump = root.Q<Button>("RaidAwayJump");
            _stay = root.Q<Button>("RaidAwayStay");
            _status = root.Q<Label>("RaidAwayStatus");
            if (_panel == null)
            {
                return;
            }
            _jump.clicked += () => ClickJump();
            _stay.clicked += () => ClickStay();
            UiTooltip.Attach(_jump, () => new TooltipContent { Title = GameText.Format("raid.away.jump", InputDisplay.ForAction(GameActionId.JumpHome)), Body = GameText.Get("raid.away.jump_tip"), Shortcut = GameActionId.JumpHome });
            UiTooltip.Attach(_stay, () => new TooltipContent { Title = GameText.Format("raid.away.stay", InputDisplay.ForAction(GameActionId.Cancel)), Body = GameText.Get("raid.away.stay_tip"), Shortcut = GameActionId.Cancel });
            UiTooltip.Attach(_keys, () => new TooltipContent { Title = GameText.Get("raid.away.home_title"), Body = GameText.Get("raid.away.keys_tip") });
            ResetSessionLocal();
        }

        /// <summary>
        /// 宿主每帧调用：按 raid.away.refresh_seconds（<see cref="HomeRaidAlertService.RefreshSeconds"/>）节流，到点返回 true（宿主随即调 <see cref="Refresh"/>）。
        /// </summary>
        public bool Due(float realDt)
        {
            _evalTimer -= Mathf.Max(0f, realDt);
            if (_evalTimer > 0f)
            {
                return false;
            }
            _evalTimer = HomeRaidAlertService.RefreshSeconds;
            return true;
        }

        /// <summary>新会话 / 读档 / 回主菜单：清掉界面的会话状态（“这一波已经弹过”、阶段记忆、汇总计时、提示行）。</summary>
        private void ResetSessionLocal()
        {
            _alertedWaves.Clear();
            _prevPhaseActive = false;
            _collectTimer = 0f;
            _statusText = string.Empty;
            _statusUntil = 0f;
            _wasShown = false;
        }

        /// <summary>
        /// 刷新（<paramref name="allowed"/> = 在世界里；<paramref name="force"/> = 立即重新汇总家园状态，自检用）。
        /// 离开世界、或游戏时间倒退（读了更早的档 / 新开一局）时先清掉会话状态（界面与 <see cref="HomeRaidAlertService.ResetSession"/>）。
        /// 判定 O(计划数 + 队伍数)；家园汇总 O(建筑数) 每 raid.away.home_refresh_seconds 真实秒最多一次。
        /// </summary>
        public void Refresh(CampaignState s, bool allowed, long now, bool force = false)
        {
            if (_panel == null)
            {
                return;
            }
            float real = RealNow;
            float dt = _lastRealTime < 0f ? 0f : Mathf.Max(0f, real - _lastRealTime);
            _lastRealTime = real;
            if ((!allowed && _sessionLive) || (allowed && _lastTicks >= 0 && now < _lastTicks))
            {
                ResetSessionLocal();
                HomeRaidAlertService.ResetSession();
            }
            _sessionLive = allowed;
            _lastTicks = allowed ? now : -1;
            if (allowed)
            {
                HomeRaidAlertService.Poll(s);
            }
            bool phaseActive = RaidAudioDirector.Phase != RaidAudioDirector.PhaseCalm;
            HomeRaidAlert a = default;
            bool show = allowed && HomeRaidAlertService.Evaluate(s, now, out a) && a.Show;
            SetVisible(_panel, show);
            bool popup = show && a.Popup;
            // Esc = 留在远征队（弹窗收起后 Esc 照常）。根浮层：不挂在当时的页面下面，页面关闭 / 切换时不会被连带关闭（复审 P1）。
            UiEscapeStack.SyncOverlay(this, popup, _escStay);
            if (!show)
            {
                SetVisible(_alert, false);
                _prevPhaseActive = phaseActive;
                _wasShown = false;
                return;
            }
            if (!_wasShown)
            {
                force = true; // 小窗从隐藏变为显示：立即重新汇总，不沿用上一波的读数
                _wasShown = true;
            }
            SetVisible(_alert, popup);
            SetVisible(_stay, popup);
            SetVisible(_hint, popup); // 两个选择的说明在小窗主体（可滚动）的最上面，弹窗收起后不再占地方
            if (popup && _alertedWaves.Add(a.PopupWave))
            {
                PopupShows++;
                GuidanceHooks.Raise(GuidanceHooks.RaidAwayFirstAlert);
                if (_prevPhaseActive && phaseActive)
                {
                    // 音乐阶段没有变化（突袭早就预警了，玩家这时才到远征地点）：补一声预警提示音；阶段变化时导演自己会响。
                    RaidAudioDirector.AlertSting();
                    Stings++;
                }
            }
            _prevPhaseActive = phaseActive;
            SetText(_alertTitle, GameText.Get("raid.away.title"));
            SetText(_hint, GameText.Get("raid.away.hint"));
            string countdown = HomeRaidAlertService.CountdownText(s, a, now);
            SetText(_countdown, countdown);
            SetText(_jump, SignalUplinkService.IsJumpingHome
                ? GameText.Format("signal.jump.pending_home", SignalUplinkService.JumpHomeRemaining.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                : GameText.Format("raid.away.jump", InputDisplay.ForAction(GameActionId.JumpHome)));
            _jump.SetEnabled(!a.CoreLost && !SignalUplinkService.IsJumpingHome);
            SetText(_stay, GameText.Format("raid.away.stay", InputDisplay.ForAction(GameActionId.Cancel)));
            string chosen = popup ? string.Empty : HomeRaidAlertService.ChosenLine(s, a);
            SetVisible(_chosen, chosen.Length > 0);
            SetText(_chosen, chosen);
            SetText(_homeTitle, popup ? GameText.Get("raid.away.home_title") : GameText.Format("raid.away.home_title_wave", HomeRaidAlertService.WaveName(a)));
            _collectTimer -= dt;
            if (force || _collectTimer <= 0f)
            {
                _collectTimer = HomeRaidAlertService.HomeRefreshSeconds;
                HomeRaidAlertService.CollectHome(s, _home);
                Collects++;
                SetText(_core, HomeRaidAlertService.CoreLine(_home));
                float pct = _home.CoreFound && _home.CoreMax > 0f ? Mathf.Clamp01(_home.CoreHp / _home.CoreMax) : 0f;
                CoreFillPercent = pct * 100f;
                _coreFill.style.width = Length.Percent(CoreFillPercent);
                SetClass(_coreFill, "ra-core-low", pct < HomeRaidAlertService.CoreLowRatio);
                SetText(_keys, HomeRaidAlertService.KeysLine(_home));
                SetText(_enemies, HomeRaidAlertService.EnemyLine(_home));
            }
            bool status = _statusText.Length > 0 && real <= _statusUntil;
            SetVisible(_status, status);
            if (status)
            {
                SetText(_status, _statusText);
            }
        }

        /// <summary>“跳回家园”（按钮；快捷键 H 走同一个 <see cref="SignalUplinkService.RequestJumpHome"/>）。被拒时写明原因并给“不行”的提示音。</summary>
        public bool ClickJump()
        {
            bool ok = HomeRaidAlertService.TryJumpHome(CampaignSession.Current, out string message);
            ShowStatus(message);
            if (!ok)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied);
            }
            return ok;
        }

        /// <summary>
        /// 小窗显示时按“跳回家园”快捷键（H）：与按钮同一入口；小窗读数最多滞后一个刷新周期，突袭刚结束时按 H 会被判“家园现在没有遇袭”——
        /// 这时回退到普通的跳回家园（<see cref="SignalUplinkService.RequestJumpHome"/>），不吞掉这次按键。核心已被摧毁时与禁用的按钮一致：被拒。
        /// </summary>
        public bool PressJumpKey()
        {
            bool ok = HomeRaidAlertService.TryJumpHome(CampaignSession.Current, out string message, out bool noRaid);
            if (!ok && noRaid)
            {
                return SignalUplinkService.RequestJumpHome().Accepted;
            }
            ShowStatus(message);
            if (!ok)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied);
            }
            return ok;
        }

        /// <summary>“留在远征队”（按钮 / Esc）：收起弹窗，家园自己守。</summary>
        public bool ClickStay()
        {
            bool ok = HomeRaidAlertService.Stay(CampaignSession.Current, out string message);
            ShowStatus(message);
            FeedbackCues.Raise(ok ? FeedbackCueId.CommandAck : FeedbackCueId.Denied);
            return ok;
        }

        private void ShowStatus(string text)
        {
            _statusText = text ?? string.Empty;
            _statusUntil = RealNow + HomeRaidAlertService.StatusSeconds;
            SetVisible(_status, _statusText.Length > 0);
            SetText(_status, _statusText);
        }

        /// <summary>宿主销毁 / 离开世界时撤掉取消栈里的层。</summary>
        public void Release()
        {
            UiEscapeStack.Remove(this);
        }

        private static bool Visible(VisualElement e) => e != null && !e.ClassListContains("uk-hidden");

        private static void SetText(TextElement e, string text)
        {
            if (e != null && e.text != text)
            {
                e.text = text;
            }
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e == null)
            {
                return;
            }
            e.EnableInClassList("uk-hidden", !visible);
        }

        private static void SetClass(VisualElement e, string cls, bool on)
        {
            e?.EnableInClassList(cls, on);
        }
    }
}
