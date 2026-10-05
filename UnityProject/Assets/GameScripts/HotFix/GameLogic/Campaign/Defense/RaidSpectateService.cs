using System;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-06（FG06 FGR-DEF-042“突袭时可以开 2x 或 3x 观战”）：倍速观战。
    /// - <b>复用</b>统一时钟的暂停与 0.5x～3x 倍速（<see cref="GameClock.SetSpeed"/>，与世界时间条 / 1～4 键同一个落点），不另写倍速逻辑；观战只改速度与镜头，不改模拟——
    ///   同样的输入在观战 / 不观战下逐步一致（自检 D 段）。
    /// - 开始：有到达的突袭时才能开；记下原来的速度，切到 2x（raid.spectate.default_speed）或玩家点的档位；镜头飞到战斗重心。
    /// - 跟随：每 raid.spectate.follow_seconds 真实秒看一次还活着的敌人的重心（<see cref="RaidHudService.FightFocus"/>，内核一次查询），离镜头焦点超过
    ///   raid.spectate.follow_min_cells 才飞过去；有面板（模态）开着时不飞（不在面板底下乱动）；玩家自己把镜头移开超过 raid.spectate.manual_cells 就暂停跟随，点“跟随战斗”恢复。
    /// - 结束：Esc（按层级：先关面板，再退出观战，再开暂停菜单；FGR-UX-001）、“停止观战”、突袭结束（自动）、离开世界。结束时恢复开始前的速度；暂停状态不动（玩家自己暂停的保持暂停）。
    /// 观战状态不进存档（镜头与速度都是本机的观看方式；读档后统一时钟回到 1x、不暂停，与读档的既有规则一致）。
    /// </summary>
    public static class RaidSpectateService
    {
        private static readonly object EscOwner = new object();

        public static bool Active { get; private set; }
        public static bool Following { get; private set; }
        /// <summary>玩家自己移开了镜头而暂停了跟随（区别于玩家点“跟随：关”）。</summary>
        public static bool FollowPausedByCamera { get; private set; }
        public static float PreviousSpeed { get; private set; } = 1f;
        public static int StartCount { get; private set; }
        public static int FollowFlights { get; private set; }
        public static int AutoEnds { get; private set; }
        public static int Revision { get; private set; }
        public static string LastMessage { get; private set; } = string.Empty;
        public static Vector2 LastTarget { get; private set; }

        private static float _followTimer;
        private static float _sinceFly;
        private static bool _hasTarget;

        /// <summary>自检：编辑模式下没有模态面板检测的宿主，用它模拟“有面板开着”。</summary>
        public static Func<bool> ModalOpenProvider;

        public static float DefaultSpeed => Mathf.Clamp(Tuning("raid.spectate.default_speed", 2f), 2f, 3f);
        public static float FollowSeconds => Math.Max(0.1f, Tuning("raid.spectate.follow_seconds", 1f));
        public static float FollowMinCells => Math.Max(0f, Tuning("raid.spectate.follow_min_cells", 6f));
        public static float ManualCells => Math.Max(1f, Tuning("raid.spectate.manual_cells", 14f));

        public static void ResetSession()
        {
            if (Active)
            {
                UI.Kit.UiEscapeStack.Remove(EscOwner);
            }
            Active = false;
            Following = false;
            FollowPausedByCamera = false;
            _hasTarget = false;
            _followTimer = 0f;
            _sinceFly = 0f;
            LastMessage = string.Empty;
            Revision++;
        }

        /// <summary>
        /// 开始观战（<paramref name="speed"/> ≤ 0 = 默认 2x）。没有到达的突袭时返回 false 并给出原因（不静默）。
        /// 审查修复（ADR-DEF-006 决策 11）：从“没在观战”开始观战时，世界正暂停着（最常见：前 3 次突袭到达时自动暂停）就一并继续，提示里写明；
        /// 已在观战中切档不动暂停状态（观战中暂停不退出观战，决策 2）。
        /// </summary>
        public static bool TryStart(CampaignState s, float speed, out string message)
        {
            if (!RaidHudService.AnyActive(s))
            {
                message = GameText.Get("raid.spec.reason.no_raid");
                LastMessage = message;
                return false;
            }
            float target = speed > 0f ? speed : DefaultSpeed;
            bool unpaused = false;
            if (!Active)
            {
                PreviousSpeed = GameClock.Speed;
                Active = true;
                StartCount++;
                UI.Kit.UiEscapeStack.Push(EscOwner, () => Stop(restoreSpeed: true));
                GuidanceHooks.Raise(GuidanceHooks.RaidFirstSpectate);
                if (GameClock.Paused)
                {
                    GameClock.SetPaused(false); // 统一时钟（与 HUD 暂停按钮同一落点）
                    unpaused = true;
                    UnpauseStarts++;
                }
            }
            GameClock.SetSpeed(target);
            Following = true;
            FollowPausedByCamera = false;
            _followTimer = 0f;
            FlyToFight(s, force: true);
            message = GameText.Format(unpaused ? "raid.spec.started_unpaused" : "raid.spec.started", GameText.Format("raid.spec.speed", FormatSpeed(GameClock.Speed)));
            LastMessage = message;
            Revision++;
            return true;
        }

        /// <summary>开始观战时顺带解除暂停的次数（自检）。</summary>
        public static int UnpauseStarts { get; private set; }

        /// <summary>
        /// 观战栏“观战 / 停止观战”按钮与快捷键（默认 Alt+V，FG00 B02）的同一入口：观战中 = 停止并恢复原速度；否则开始观战（没有到达的突袭时返回 false，原因在 <see cref="LastMessage"/>）。
        /// </summary>
        public static bool Toggle(CampaignState s)
        {
            if (Active)
            {
                Stop(restoreSpeed: true);
                return true;
            }
            return TryStart(s, 0f, out _);
        }

        /// <summary>
        /// 观战栏“跟随战斗”按钮与快捷键（默认 Ctrl+F，FG00 B02）的同一入口：观战中 = 开 / 关跟随；没在观战时 = 开始观战并跟随（没有突袭时返回 false，原因在 <see cref="LastMessage"/>）。
        /// </summary>
        public static bool ToggleFollow(CampaignState s)
        {
            if (!Active)
            {
                return TryStart(s, 0f, out _);
            }
            SetFollow(s, !Following);
            return true;
        }

        /// <summary>结束观战：恢复开始前的速度（<paramref name="restoreSpeed"/>），暂停状态不动。</summary>
        public static void Stop(bool restoreSpeed)
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            Following = false;
            FollowPausedByCamera = false;
            _hasTarget = false;
            UI.Kit.UiEscapeStack.Remove(EscOwner);
            if (restoreSpeed)
            {
                GameClock.SetSpeed(PreviousSpeed);
            }
            Revision++;
        }

        /// <summary>观战中切档（复用统一时钟的档位，0.5x～3x）。没在观战时就是普通的改速度。</summary>
        public static void SetSpeed(float speed)
        {
            GameClock.SetSpeed(speed);
            Revision++;
        }

        /// <summary>开 / 关镜头跟随（关掉后镜头完全由玩家控制；打开立即飞到战斗重心）。</summary>
        public static void SetFollow(CampaignState s, bool on)
        {
            Following = on;
            FollowPausedByCamera = false;
            _followTimer = 0f;
            if (on && Active)
            {
                FlyToFight(s, force: true);
            }
            Revision++;
        }

        private static bool ModalOpen()
        {
            if (ModalOpenProvider != null)
            {
                return ModalOpenProvider();
            }
            foreach (object owner in InputRouter.ModalOwnerList)
            {
                if (owner != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 每帧（突袭 HUD 的 Update 调用，真实时间）：突袭结束 → 自动结束并恢复速度；跟随中按间隔看一次战斗重心；玩家移开镜头 → 暂停跟随。
        /// 只读内核（一次查询）与镜头，不改模拟。
        /// </summary>
        public static void Tick(CampaignState s, float realDt)
        {
            if (!Active)
            {
                return;
            }
            if (s == null || !RaidHudService.AnyActive(s))
            {
                float restore = PreviousSpeed;
                Stop(restoreSpeed: true);
                AutoEnds++;
                LastMessage = GameText.Format("raid.spec.ended", GameText.Format("raid.spec.speed", FormatSpeed(restore)));
                return;
            }
            _sinceFly += realDt;
            if (!Following)
            {
                return;
            }
            if (_hasTarget && _sinceFly > GameClock.TuningOr("camera.fly_seconds", 0.5f) + 0.3f && WorldView.Director != null && WorldView.Director.IsBound)
            {
                Vector2 now = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
                if (Vector2.Distance(now, LastTarget) > ManualCells)
                {
                    Following = false;
                    FollowPausedByCamera = true;
                    LastMessage = GameText.Get("raid.spec.follow_paused");
                    Revision++;
                    return;
                }
            }
            _followTimer -= realDt;
            if (_followTimer > 0f)
            {
                return;
            }
            _followTimer = FollowSeconds;
            if (ModalOpen())
            {
                return; // 面板开着：不在面板底下移动镜头（关掉面板后下一次接着跟）
            }
            FlyToFight(s, force: false);
        }

        private static void FlyToFight(CampaignState s, bool force)
        {
            if (!RaidHudService.FightFocus(s, out Vector2 focus))
            {
                return;
            }
            IWorldSite home = WorldSimulation.Home;
            if (home == null || !home.IsLoaded)
            {
                return;
            }
            bool far = true;
            if (!force && WorldView.IsObserved(home.SiteId) && WorldView.Director != null && WorldView.Director.IsBound)
            {
                Vector2 now = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
                far = Vector2.Distance(now, focus) > FollowMinCells;
            }
            LastTarget = focus;
            _hasTarget = true;
            if (!far)
            {
                return;
            }
            if (WorldView.FlyTo(home.SiteId, focus))
            {
                FollowFlights++;
                _sinceFly = 0f;
            }
        }

        public static string FormatSpeed(float speed) => speed.ToString(speed < 1f ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "x";

        /// <summary>HUD 观战栏的状态文字。</summary>
        public static string StatusText()
        {
            if (!Active)
            {
                return GameText.Format("raid.spec.status_off", RaidHudService.TotalAlive(CampaignSession.Current));
            }
            string speed = GameClock.Paused ? GameText.Get("raid.spec.status_paused") : FormatSpeed(GameClock.Speed);
            string follow = FollowPausedByCamera ? GameText.Get("raid.spec.follow_paused")
                : !Following ? GameText.Get("raid.spec.follow_state_off")
                : ModalOpen() ? GameText.Get("raid.spec.follow_panel")
                : GameText.Get("raid.spec.follow_state_on");
            return GameText.Format("raid.spec.status_on", speed, follow);
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
