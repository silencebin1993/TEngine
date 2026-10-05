using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-005“带接入口的炮塔可以被玩家接入，插入核心固件（FG01）。炮塔不能移动，但玩家可以亲自瞄准”）：信号接入炮塔。
    /// - 进入（<see cref="Request"/>）：炮塔建成、吃到电、没禁用、蓝图能用且有接入口；信号在归还核心（不在机器里、不在跳转 / 过渡中）；不是静默夜。
    ///   信号在靶场投影里时先离开投影（信号只在一处）。进入后炮塔按接入态编译（信号核里生效的固件插进接入口，与机器同一个 <see cref="Blueprint.UplinkCompiler"/>），
    ///   内核单位带 Possessed：不再按目标模式自动开火，由玩家在战略视角里用鼠标瞄准、左键开火（与机器直控同一个开火入口，补给 / 积热 / 读法 / 弹体都在内核）。
    /// - 离开（<see cref="Leave"/>）：接入 / 退出接入键、炮塔面板按钮；炮塔被摧毁 / 断电 / 禁用 / 拆除、静默夜开始、信号去了别的机器时自动离开并说明。离开后炮塔回到本地配置、按目标模式自动开火。
    /// - 核心固件的具名反应在炮塔上发动时按信号上的冷却结算（<see cref="SignalUplinkService.OnHostReactionFired"/>），冷却中不发动（与 FG1-SIG-03 同一口径）。
    /// - 存档：信号在哪座炮塔里（<see cref="TurretState.UplinkTurretId"/>）；读档后在第一次对账时核对，炮塔已不能接入就回到归还核心并说明。
    /// </summary>
    public static class TurretUplink
    {
        /// <summary>亲自瞄准时的瞄准锥半角（度）：鼠标指向附近的敌人算瞄准到（与机器直控同一量级，初值 FG16 调）。</summary>
        public const float AimHalfAngleDeg = 25f;

        public static int Revision { get; private set; }

        /// <summary>自检读：最近一次亲自开火的结果。</summary>
        public static CombatFireResult LastFireResult { get; private set; }

        /// <summary>自检 / 冒烟读：经正式输入入口（<see cref="HandleInput"/>）发起的亲自开火次数，与战略暂停中被吃掉的左键次数。</summary>
        public static int InputFires { get; private set; }
        public static int PausedClicks { get; private set; }

        public static string ActiveTurretId(CampaignState s) => TurretService.StateOf(s)?.UplinkTurretId ?? string.Empty;

        public static bool IsActive => !string.IsNullOrEmpty(ActiveTurretId(CampaignSession.Current));

        public static bool IsUplinkedTo(CampaignState s, string buildingId) =>
            !string.IsNullOrEmpty(buildingId) && ActiveTurretId(s) == buildingId;

        public static void ResetSession()
        {
            LastFireResult = CombatFireResult.Ok;
            Revision++;
        }

        private static void Say(string text)
        {
            Revision++;
            TurretService.Feedback(text);
        }

        /// <summary>能不能接入这座炮塔（逐条原因，B06）。</summary>
        public static TurretOpResult Validate(CampaignState s, string buildingId)
        {
            if (s == null)
            {
                return TurretOpResult.Fail(TurretFailure.NoCampaign, GameText.Get("turret.reason.no_campaign"));
            }
            if (!TurretService.TryGetReadout(s, buildingId, out TurretReadout ro))
            {
                return TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found"));
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            if (!ro.Built || !ro.HasUnit || b.ConstructionState != BuildingConstructionState.Operational || b.PowerState != BuildingPowerState.Powered)
            {
                return TurretOpResult.Fail(TurretFailure.NotOperational, GameText.Format("turret.reason.not_operational", ro.Status.Reason));
            }
            if (!ro.Valid)
            {
                return TurretOpResult.Fail(TurretFailure.BlueprintMissing, ro.InvalidReason);
            }
            if (!ro.HasPort)
            {
                return TurretOpResult.Fail(TurretFailure.NoPort, GameText.Format("turret.reason.no_port", ro.BlueprintName));
            }
            int machine = SignalUplinkService.CurrentMachine(s);
            if (machine != 0)
            {
                string label = Feedback_MachineLabel(machine);
                return TurretOpResult.Fail(TurretFailure.SignalInMachine,
                    GameText.Format("turret.reason.signal_in_machine", label, InputDisplay.ForAction(GameActionId.ToggleCameraView)));
            }
            if (SignalLinkService.IsSilentNight)
            {
                return TurretOpResult.Fail(TurretFailure.SilentNight, GameText.Get("turret.reason.silent_night"));
            }
            if (SignalUplinkService.IsPending || SignalUplinkService.IsJumpingHome)
            {
                return TurretOpResult.Fail(TurretFailure.SignalBusy, GameText.Get("turret.reason.signal_busy"));
            }
            if (!IsCovered(b))
            {
                // FG6-DEF-01 审查修复（FG01 FGR-SIG-053“覆盖网络之外的机器不能被接入”）：炮塔是固定底盘的机器，同一条规则。
                return TurretOpResult.Fail(TurretFailure.OutOfCoverage, GameText.Format("signal.uplink.reason.out_of_coverage", BuildingOps.NameOf(b)));
            }
            return TurretOpResult.Success(string.Empty);
        }

        /// <summary>炮塔座位置在不在与归还核心连通的信号覆盖里（<see cref="SignalCoverageService.Sample"/>，与机器接入同一个判定；O(覆盖源数)，只在接入 / 每步核对接入中的那一座时）。</summary>
        public static bool IsCovered(BuildingRecord b) =>
            b != null && SignalCoverageService.Sample(HomeValleyLayout.RegionId, b.Position).Covered;

        private static string Feedback_MachineLabel(int logicId)
        {
            string label = GameLogic.Campaign.Feedback.FeedbackCues.MachineLabel(logicId);
            return string.IsNullOrEmpty(label) ? logicId.ToString() : label;
        }

        /// <summary>接入一座炮塔（立即生效：炮塔按接入态编译、不再自动开火）。</summary>
        public static TurretOpResult Request(CampaignState s, string buildingId)
        {
            TurretOpResult v = Validate(s, buildingId);
            if (!v.Ok)
            {
                return v;
            }
            if (TestRangeService.TryGetUplinkedProjection(out _, out _, out _))
            {
                TestRangeService.LeaveUplink(s); // 信号只在一处：从靶场投影移过来。
            }
            string prev = ActiveTurretId(s);
            TurretService.StateOf(s).UplinkTurretId = buildingId;
            if (!string.IsNullOrEmpty(prev) && prev != buildingId)
            {
                TurretService.Refresh(s, prev);
            }
            TurretService.Refresh(s, buildingId);
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstUplink);
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            string text = GameText.Format("turret.feedback.uplinked", BuildingOps.NameOf(b));
            Say(text);
            return TurretOpResult.Success(text);
        }

        /// <summary>信号离开炮塔、回到归还核心（炮塔回到本地配置、按目标模式自动开火）。<paramref name="feedbackKey"/> = 给玩家看的原因。</summary>
        public static TurretOpResult Leave(CampaignState s, string feedbackKey = "turret.feedback.left")
        {
            string id = ActiveTurretId(s);
            if (string.IsNullOrEmpty(id))
            {
                return TurretOpResult.Fail(TurretFailure.NotUplinked, GameText.Get("turret.reason.not_uplinked"));
            }
            TurretService.StateOf(s).UplinkTurretId = string.Empty;
            TurretService.Refresh(s, id);
            string text = GameText.Get(feedbackKey);
            Say(text);
            return TurretOpResult.Success(text);
        }

        /// <summary>炮塔没了 / 失去作用（被摧毁、拆除）：信号在里面就回到归还核心并说明。</summary>
        public static void OnTurretGone(CampaignState s, string buildingId)
        {
            if (IsUplinkedTo(s, buildingId))
            {
                Leave(s, "turret.feedback.lost");
            }
        }

        /// <summary>每个固定步（与观察无关）：静默夜开始 → 断链；信号去了别的机器 → 离开；炮塔断电 / 禁用 / 不能接入了 → 离开。O(1)。</summary>
        public static void SimStep(CampaignState s)
        {
            string id = ActiveTurretId(s);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            if (SignalLinkService.IsSilentNight)
            {
                Leave(s, "turret.feedback.silent_night");
                return;
            }
            if (SignalUplinkService.CurrentMachine(s) != 0)
            {
                Leave(s, "turret.feedback.left");
                return;
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, id);
            if (b == null || b.ConstructionState != BuildingConstructionState.Operational || b.PowerState != BuildingPowerState.Powered
                || !TurretService.HasUsablePort(s, id))
            {
                Leave(s, "turret.feedback.lost");
                return;
            }
            if (!IsCovered(b))
            {
                // FG6-DEF-01 审查修复（FGR-SIG-053）：覆盖没了（信号塔断电 / 被拆、炮塔座搬到覆盖外）→ 信号回到归还核心并说明。
                Leave(s, "turret.feedback.out_of_coverage");
            }
        }

        /// <summary>
        /// 亲自瞄准开火（左键，战略视角）：从炮塔朝 <paramref name="worldPoint"/> 的瞄准锥里、射程内第一个敌人开一发（内核 FireAt：门槛、补给、积热、读法、弹体）。
        /// 没有目标 / 没打出去都给原因，不静默。
        /// </summary>
        public static TurretOpResult FireAt(CampaignState s, Vector2 worldPoint)
        {
            string id = ActiveTurretId(s);
            TurretRecord r = TurretService.Find(s, id);
            CombatSite site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (r == null || site == null || !site.TryGetTurretState(r.Serial, out TurretUnitState us) || !site.TryGetTurretWeapon(r.Serial, out CombatWeapon w))
            {
                return TurretOpResult.Fail(TurretFailure.NotUplinked, GameText.Get("turret.reason.not_uplinked"));
            }
            Vector2 dir = worldPoint - us.Position;
            int target = site.FindHostileInAim(us.Position, dir, w.Range, AimHalfAngleDeg);
            if (target == 0)
            {
                LastFireResult = CombatFireResult.TargetMissing;
                string none = GameText.Get("turret.reason.no_target");
                Say(none);
                return TurretOpResult.Fail(TurretFailure.NoTarget, none);
            }
            CombatFireResult res = site.TurretFireAt(r.Serial, target);
            LastFireResult = res;
            if (res == CombatFireResult.Ok || res == CombatFireResult.StillAiming)
            {
                Revision++;
                return TurretOpResult.Success(string.Empty);
            }
            string text = GameText.Format("turret.reason.fire_failed", FireFailureText(site, res, w));
            Say(text);
            return TurretOpResult.Fail(TurretFailure.FireFailed, text);
        }

        /// <summary>
        /// 开火结果 → 玩家可读原因（FG6-DEF-01 审查修复，B06 / B16）：与机器直控同一套映射（<see cref="CombatSite.FireReason"/>：文本键、没有占位符漏填）；
        /// 过热按这座炮塔武器自己的恢复线填数（不是机器的默认值）。映射里没有的结果（不该出现）写通用原因，不显示英文枚举名。
        /// </summary>
        public static string FireFailureText(CombatSite site, CombatFireResult res, in CombatWeapon w)
        {
            if (res == CombatFireResult.Overheated)
            {
                return GameText.Format("combat.fire.overheated", Mathf.RoundToInt(w.RecoverBelow).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            string why = site != null ? site.FireReason(res, 0, null) : string.Empty;
            return string.IsNullOrEmpty(why) ? GameText.Get("combat.fire.no_attacker") : why;
        }

        /// <summary>接入的炮塔打出了具名反应（内核玩法事件）：核心固件按信号上的冷却结算，炮塔随即重编译（冷却中这条反应不发动）。</summary>
        public static void OnReactionFired(CampaignState s, TurretRecord r, in CombatEvent e)
        {
            if (s == null || r == null)
            {
                return;
            }
            bool gated = e.Code2 == 1;
            if (!gated && !IsUplinkedTo(s, r.BuildingId))
            {
                return;
            }
            string reactionId = (CombatReaction)e.Code == CombatReaction.MeltOverload ? MechanicalReactionCatalog.ReactionMeltOverloadId
                : (CombatReaction)e.Code == CombatReaction.MarkJump ? MechanicalReactionCatalog.ReactionMarkJumpId : null;
            if (reactionId == null)
            {
                return;
            }
            SignalUplinkService.OnHostReactionFired(s, reactionId);
            TurretService.Refresh(s, r.BuildingId);
        }

        /// <summary>
        /// 家园被观察时每帧（<see cref="Regions.HomeValleyController"/>）：信号在炮塔里时，左键 = 亲自瞄准开火（不做框选 / 点选）。
        /// 鼠标被界面挡住 / 建造模式拿着鼠标时不开火。返回 true = 本帧的鼠标归炮塔接入（调用方跳过选中点击）。
        /// </summary>
        public static bool HandleInput(Camera camera, CampaignState s, bool pointerBlocked)
        {
            if (s == null || !IsActive)
            {
                return false;
            }
            if (pointerBlocked || camera == null)
            {
                return true;
            }
            if (InputRouter.GetMouseButtonDown(0, InputScope.Strategy) && InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 screen))
            {
                if (GameClock.Paused)
                {
                    // FG6-DEF-01 审查修复（暂停矩阵）：战略暂停时世界冻结——亲自瞄准的左键吃掉、不开火（与直控输入在暂停中被拦同一口径），
                    // 不在冻结的世界里结算伤害 / 积热 / 补给；给一句原因（不静默）。
                    PausedClicks++;
                    Say(GameText.Get("turret.fire.paused"));
                    return true;
                }
                Ray ray = camera.ScreenPointToRay(screen);
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter))
                {
                    Vector3 p = ray.GetPoint(enter);
                    InputFires++;
                    FireAt(s, new Vector2(p.x, p.z));
                }
            }
            return true;
        }

        /// <summary>HUD 信号状态行（<see cref="SignalUplinkService.StatusLine"/> 在信号不在机器里时问）：信号在炮塔里 → “信号在炮塔里：名字”。</summary>
        public static string StatusLine(CampaignState s)
        {
            string id = ActiveTurretId(s);
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, id);
            return GameText.Format("turret.signal.in_turret", BuildingOps.NameOf(b), InputDisplay.ForAction(GameActionId.ToggleCameraView));
        }
    }
}
