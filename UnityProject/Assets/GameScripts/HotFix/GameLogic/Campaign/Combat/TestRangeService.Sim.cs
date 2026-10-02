using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameConfig.fg;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>FG5-RND-03：要投影的东西（蓝图的某一版，或固件试验台）。</summary>
    public sealed class ProjectionSpec
    {
        public string Label;
        public BlueprintCircuitBoard Board;
        public string BlueprintId;
        public int Version;
        /// <summary>固件试验台的那枚固件（蓝图投影为空）。</summary>
        public string FirmwareId;
        /// <summary>核心固件试验台：按接入态编译（模拟信号核里只有这一枚），不需要、也不能再接入。</summary>
        public bool CoreRig;
    }

    /// <summary>FG5-RND-03：一个仿真投影的只读视图（面板 / 自检读它）。</summary>
    public readonly struct RangeProjectionView
    {
        public readonly int Serial;
        public readonly string Label;
        public readonly bool Uplinked;
        public readonly bool CoreRig;
        public readonly int UnitId;
        public readonly int Shots;
        public readonly float Heat;
        public readonly bool CoreCooling;

        public RangeProjectionView(int serial, string label, bool uplinked, bool coreRig, int unitId, int shots, float heat, bool coreCooling)
        {
            Serial = serial;
            Label = label;
            Uplinked = uplinked;
            CoreRig = coreRig;
            UnitId = unitId;
            Shots = shots;
            Heat = heat;
            CoreCooling = coreCooling;
        }
    }

    public static partial class TestRangeService
    {
        // ─────────────────────────────── 运行时（不存档）───────────────────────────────

        private sealed class Projection
        {
            public int Serial;
            public ProjectionSpec Spec;
            public int Unit;
            public BlueprintCircuitPreview Local;
            public BlueprintCircuitPreview Uplink;
            public bool Uplinked;
            public bool EverUplinked;
            /// <summary>当前生效的编译结果带“信号带进来、有冷却的核心固件”反应（接入的投影 / 核心固件试验台）。</summary>
            public bool CoreGated;
            /// <summary>核心固件冷却结束的内核时间（&lt; 0 = 没在冷却）。冷却只在投影里模拟，不动真实信号核。</summary>
            public double CoreReadyAt = -1;
            public int Shots;
            public int Overheats;
            public float Energy;
            public Vector2 Home;
            public int Lane;
            public double NextRetarget;

            public BlueprintCircuitPreview Active => (Uplinked || Spec.CoreRig) && Uplink != null ? Uplink : Local;
        }

        private sealed class TargetUnit
        {
            public int Slot;
            public int Index;
            public RangeTargetDef Def;
            public int Unit;
            public double RespawnAt = -1;
            public Vector2 Pos;
            public Vector2 Facing;
        }

        private sealed class Session
        {
            public string BuildingId;
            public CombatSite Site;
            public int PivotX;
            public int PivotY;
            public int Rotation;
            public Vector2 Center;
            public float Half;
            public readonly List<Projection> Projections = new List<Projection>(6);
            public readonly List<TargetUnit> Targets = new List<TargetUnit>(16);
            public readonly Dictionary<int, Projection> ByUnit = new Dictionary<int, Projection>();
            public readonly Dictionary<int, TargetUnit> TargetByUnit = new Dictionary<int, TargetUnit>();
            public string[] Layout;
            public double StartTime;
            public long StartTick;
            public double BaseDamage;
            public long BaseShots;
            public long BaseKills;
            public readonly int[] BaseReaction = new int[CombatConst.MaxReactions];
            public readonly Dictionary<string, int> AssemblyReactions = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<string> ProjectionLabels = new List<string>(6);
            public double NextSample;
            public long SampleAlive;
            public readonly long[] SampleBits = new long[32];
            public readonly List<float> HeatCurve = new List<float>(360);
            public float PeakHeat;
            public bool AnyUplinked;
            /// <summary>这次测试里有投影被接入过（记录写“·接入”；被移除 / 静默夜消失的投影也算）。</summary>
            public bool EverUplinked;
            /// <summary>已经移除的投影的发数 / 过热 / 能耗（读数是整次测试的合计，不因投影中途消失而少算）。</summary>
            public int DroppedShots;
            public int DroppedOverheats;
            public float DroppedEnergy;
            /// <summary>等着复位的投影靶（按阵亡先后；到期的才处理，O(待复位数)，不逐靶扫描）。</summary>
            public readonly List<TargetUnit> Respawns = new List<TargetUnit>(8);
        }

        private static readonly List<Session> Sessions = new List<Session>(2);
        private static readonly int[] BitScratch = new int[32];
        private static int _nextSerial = 1;

        /// <summary>自检读点：进行中的测试数、投影总数、内核步数。</summary>
        public static int ActiveSessions => Sessions.Count;
        public static int StepsRun { get; private set; }

        public static int ProjectionCount
        {
            get
            {
                int n = 0;
                foreach (Session s in Sessions)
                {
                    n += s.Projections.Count;
                }
                return n;
            }
        }

        public static bool IsRunning(string buildingId) => FindSession(buildingId) != null;

        private static Session FindSession(string buildingId)
        {
            foreach (Session s in Sessions)
            {
                if (s.BuildingId == buildingId)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>自检用：靶场的仿真地点（没有在测试时为 null）。不登记进 <see cref="CombatSites"/>（结构上不进存档）。</summary>
        public static CombatSite SiteOf(string buildingId) => FindSession(buildingId)?.Site;

        public static List<RangeProjectionView> ProjectionsOf(string buildingId)
        {
            var list = new List<RangeProjectionView>();
            Session s = FindSession(buildingId);
            if (s == null)
            {
                return list;
            }
            foreach (Projection p in s.Projections)
            {
                float heat = s.Site.Kernel.TryGetUnit(p.Unit, out CombatUnitView v) ? v.Heat : 0f;
                list.Add(new RangeProjectionView(p.Serial, p.Spec.Label, p.Uplinked, p.Spec.CoreRig, p.Unit, p.Shots, heat, p.CoreReadyAt >= 0));
            }
            return list;
        }

        public static string ProjectionLabel(string buildingId, int serial)
        {
            Session s = FindSession(buildingId);
            Projection p = s?.Projections.Find(x => x.Serial == serial);
            return p?.Spec.Label ?? string.Empty;
        }

        /// <summary>信号现在在哪个投影里（建筑 ID + 编号；不在投影里返回 false）。</summary>
        public static bool TryGetUplinkedProjection(out string buildingId, out int serial, out string label)
        {
            foreach (Session s in Sessions)
            {
                foreach (Projection p in s.Projections)
                {
                    if (p.Uplinked)
                    {
                        buildingId = s.BuildingId;
                        serial = p.Serial;
                        label = p.Spec.Label;
                        return true;
                    }
                }
            }
            buildingId = null;
            serial = 0;
            label = null;
            return false;
        }

        // ─────────────────────────────── 投影（FGR-RND-031）───────────────────────────────

        /// <summary>投影一张已保存的蓝图（当前启用的版本）。不消耗任何资源；靶场没在测试时随之开始一次测试。</summary>
        public static RangeOpResult ProjectBlueprint(CampaignState s, string buildingId, string blueprintId)
        {
            if (s == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoCampaign, GameText.Get("range.reason.no_campaign"));
            }
            if (string.IsNullOrEmpty(blueprintId))
            {
                return RangeOpResult.Fail(RangeFailure.NoBlueprint, GameText.Get("range.reason.no_blueprint"), buildingId);
            }
            BlueprintRecord rec = BlueprintEditorService.Find(s, blueprintId);
            BlueprintVersionRecord ver = rec != null && !rec.Archived ? LatestVersion(rec) : null;
            if (ver == null)
            {
                return RangeOpResult.Fail(RangeFailure.BlueprintMissing, GameText.Get("range.reason.blueprint_missing"), buildingId);
            }
            return AddProjection(s, buildingId, new ProjectionSpec
            {
                Label = BlueprintName(rec),
                Board = BlueprintCircuitBoard.FromVersion(ver),
                BlueprintId = blueprintId,
                Version = ver.Version,
            });
        }

        public static RangeOpResult AddProjection(CampaignState s, string buildingId, ProjectionSpec spec)
        {
            if (s == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoCampaign, GameText.Get("range.reason.no_campaign"));
            }
            BuildingRecord b = FindRange(s, buildingId);
            if (b == null)
            {
                return RangeOpResult.Fail(RangeFailure.NoRange, GameText.Get("range.reason.no_range"));
            }
            if (!IsUsable(s, b, out string why))
            {
                return RangeOpResult.Fail(RangeFailure.NotWorking, GameText.Format("range.reason.not_working", why), buildingId);
            }
            if (spec?.Board == null)
            {
                return RangeOpResult.Fail(RangeFailure.BlueprintMissing, GameText.Get("range.reason.blueprint_missing"), buildingId);
            }
            int cap = TestRangeCatalog.ProjectionCap;
            if (ProjectionCount >= cap)
            {
                return RangeOpResult.Fail(RangeFailure.ProjectionCap, GameText.Format("range.reason.cap", cap), buildingId);
            }
            Session session = FindSession(buildingId) ?? StartSession(s, b);
            var p = new Projection { Serial = _nextSerial++, Spec = spec };
            p.Local = BlueprintCircuitCompiler.CompilePreview(spec.Board, s.RandomSeed);
            if (spec.CoreRig)
            {
                // 核心固件放不进机器电路：试验台按接入态编译（模拟信号核 = 这一枚），与双态预览同一套 UplinkCompiler。
                p.Uplink = UplinkCompiler.CompileUplinked(spec.Board, new[] { spec.FirmwareId }, s.RandomSeed);
            }
            p.Lane = FreeLane(session);
            p.Home = LocalToWorld(session, LaneX(session, p.Lane), -(session.Half - TestRangeCatalog.EdgeMargin - 0.4f));
            int weapon = WeaponOf(session.Site, p, suppressed: false);
            BlueprintCircuitPreview active = p.Active;
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.Instanced;
            if (active != null && active.HasHeatSinkStructure)
            {
                flags |= CombatUnitFlags.HeatSink;
            }
            p.CoreGated = IsCoreGated(active);
            if (p.CoreGated)
            {
                flags |= CombatUnitFlags.ReactionGated;
            }
            p.Unit = session.Site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.Commanded,
                Flags = flags,
                Position = new double2(p.Home.x, p.Home.y),
                Home = new double2(p.Home.x, p.Home.y),
                Radius = 0.9f,
                Speed = HomeValleyMachineMarker.MoveSpeed,
                Health = 100f,
                MaxHealth = 100f,
                Weapon = weapon,
                BehaviorProfile = -1,
                Priority = 1,
                AimReadyAt = session.Site.Kernel.Time,
                NextFireAt = session.Site.Kernel.Time,
            });
            session.Site.SetUnitLabel(p.Unit, "reaction.log.projection", spec.Label, active?.FirmwareIds);
            session.Projections.Add(p);
            session.ByUnit[p.Unit] = p;
            session.ProjectionLabels.Add(spec.Label);
            p.NextRetarget = session.Site.Kernel.Time;
            GuidanceHooks.Raise(GuidanceHooks.RangeFirstTest);
            LastRangeId = buildingId;
            string text = GameText.Format("range.feedback.projected", spec.Label);
            Feedback(text);
            return RangeOpResult.Ok(text, buildingId, p.Serial);
        }

        /// <summary>移除一个投影（最后一个没了，测试随之结束并记下读数）。</summary>
        public static RangeOpResult RemoveProjection(CampaignState s, string buildingId, int serial)
        {
            Session session = FindSession(buildingId);
            Projection p = session?.Projections.Find(x => x.Serial == serial);
            if (p == null)
            {
                return RangeOpResult.Fail(RangeFailure.NotFound, GameText.Get("range.reason.not_found"), buildingId);
            }
            DropProjection(session, p);
            string text = GameText.Format("range.feedback.removed", p.Spec.Label);
            Feedback(text);
            if (session.Projections.Count == 0)
            {
                End(s, session, RangeEndReason.Emptied);
            }
            return RangeOpResult.Ok(text, buildingId);
        }

        private static void DropProjection(Session session, Projection p)
        {
            session.DroppedShots += p.Shots;
            session.DroppedOverheats += p.Overheats;
            session.DroppedEnergy += p.Energy;
            // 名字留着：它已经射出的弹体照常飞、照常触发反应，日志里触发者仍写“投影·…”（FG-GAP-061；ID 不复用，随地点 Dispose 清）。
            session.Site.Kernel.Despawn(p.Unit);
            session.ByUnit.Remove(p.Unit);
            session.Projections.Remove(p);
            session.AnyUplinked = session.Projections.Exists(q => q.Uplinked); // 移除的是被接入的那个：信号回到归还核心
            Touch();
        }

        /// <summary>手动结束测试（读数进测试记录）。</summary>
        public static RangeOpResult EndTest(CampaignState s, string buildingId)
        {
            Session session = FindSession(buildingId);
            if (session == null)
            {
                return RangeOpResult.Fail(RangeFailure.NotRunning, GameText.Get("range.reason.not_running"), buildingId);
            }
            RangeResultRecord r = End(s, session, RangeEndReason.Player);
            return RangeOpResult.Ok(GameText.Format("range.feedback.ended", ResultSummary(r)), buildingId);
        }

        // ─────────────────────────────── 接入投影（FGR-RND-031“可以被玩家接入”）───────────────────────────────

        /// <summary>信号进入这个投影：按接入态重新编译（信号核按槽位顺序插进接入口，与真实接入同一套 <see cref="UplinkCompiler"/>），
        /// 核心固件的冷却按信号规则在投影里模拟（不动真实信号核的冷却）。投影仍自动打靶（读数可复现）。</summary>
        public static RangeOpResult Uplink(CampaignState s, string buildingId, int serial)
        {
            Session session = FindSession(buildingId);
            Projection p = session?.Projections.Find(x => x.Serial == serial);
            if (s == null || p == null)
            {
                return RangeOpResult.Fail(RangeFailure.NotFound, GameText.Get("range.reason.not_found"), buildingId);
            }
            if (p.Spec.CoreRig)
            {
                return RangeOpResult.Fail(RangeFailure.RigUplinked, GameText.Get("range.reason.rig_uplinked"), buildingId);
            }
            if (p.Uplinked)
            {
                return RangeOpResult.Fail(RangeFailure.AlreadyUplinked, GameText.Get("range.reason.already_uplinked"), buildingId);
            }
            int machine = SignalUplinkService.CurrentMachine(s);
            if (machine != 0)
            {
                string label = FeedbackCues.MachineLabel(machine);
                return RangeOpResult.Fail(RangeFailure.SignalInMachine,
                    GameText.Format("range.reason.signal_in_machine", string.IsNullOrEmpty(label) ? machine.ToString() : label, InputDisplay.ForAction(GameActionId.ToggleCameraView)), buildingId);
            }
            if (SignalLinkService.IsSilentNight)
            {
                return RangeOpResult.Fail(RangeFailure.SilentNight, GameText.Get("range.reason.silent_night"), buildingId);
            }
            if (SignalUplinkService.IsPending || SignalUplinkService.IsJumpingHome)
            {
                return RangeOpResult.Fail(RangeFailure.Busy, GameText.Get("range.reason.busy"), buildingId);
            }
            // 信号只在一处：从别的投影移过来。
            foreach (Session other in Sessions)
            {
                foreach (Projection q in other.Projections)
                {
                    if (q.Uplinked)
                    {
                        SetUplinked(other, q, false);
                    }
                }
            }
            p.Uplink = UplinkCompiler.CompileUplinked(p.Spec.Board, SignalCoreService.ActiveContentIds(s), s.RandomSeed);
            SetUplinked(session, p, true);
            p.EverUplinked = true;
            session.EverUplinked = true;
            StateOf(s).UplinksRun++;
            GuidanceHooks.Raise(GuidanceHooks.RangeFirstUplink);
            string text = GameText.Format("range.feedback.uplinked", p.Spec.Label);
            Feedback(text);
            return RangeOpResult.Ok(text, buildingId, p.Serial);
        }

        /// <summary>信号离开投影、回到归还核心（投影回到本地配置，继续打靶）。</summary>
        public static RangeOpResult LeaveUplink(CampaignState s)
        {
            foreach (Session session in Sessions)
            {
                foreach (Projection p in session.Projections)
                {
                    if (p.Uplinked)
                    {
                        SetUplinked(session, p, false);
                        string text = GameText.Get("range.feedback.left");
                        Feedback(text);
                        return RangeOpResult.Ok(text, session.BuildingId, p.Serial);
                    }
                }
            }
            return RangeOpResult.Fail(RangeFailure.NotUplinked, GameText.Get("range.reason.not_uplinked"));
        }

        private static void SetUplinked(Session session, Projection p, bool on)
        {
            p.Uplinked = on;
            p.CoreReadyAt = -1; // 冷却属于信号：信号离开 / 进来时按新的编译结果重新下发（投影里的模拟冷却不带走）。
            ApplyWeapon(session, p, suppressed: false);
            session.AnyUplinked = false;
            foreach (Projection q in session.Projections)
            {
                session.AnyUplinked |= q.Uplinked;
            }
            Touch();
        }

        private static bool IsCoreGated(BlueprintCircuitPreview p) =>
            p != null && SignalUplinkService.IsCoreGatedReaction(p.ReactionId, p.UplinkFirmwareIds);

        private static int WeaponOf(CombatSite site, Projection p, bool suppressed)
        {
            BlueprintCircuitPreview active = p.Active;
            if (active == null)
            {
                return -1;
            }
            return site.WeaponIndex(CombatSite.MachineWeaponFrom(active, suppressed));
        }

        /// <summary>按当前生效的编译结果重下发武器参数与门控位（接入 / 离开 / 冷却起止，O(1)）。</summary>
        private static void ApplyWeapon(Session session, Projection p, bool suppressed)
        {
            CombatKernel k = session.Site.Kernel;
            BlueprintCircuitPreview active = p.Active;
            k.SetUnitWeapon(p.Unit, WeaponOf(session.Site, p, suppressed));
            k.SetFlag(p.Unit, CombatUnitFlags.HeatSink, active != null && active.HasHeatSinkStructure);
            p.CoreGated = IsCoreGated(active);
            bool gated = p.CoreGated && !suppressed;
            k.SetFlag(p.Unit, CombatUnitFlags.ReactionGated, gated);
            if (!gated)
            {
                k.SetFlag(p.Unit, CombatUnitFlags.ReactionSpent, false);
            }
            session.Site.SetUnitLabel(p.Unit, "reaction.log.projection", p.Spec.Label, active?.FirmwareIds);
        }

        // ─────────────────────────────── 会话 ───────────────────────────────

        private static Session StartSession(CampaignState s, BuildingRecord b)
        {
            var site = new CombatSite(SitePrefix + b.BuildingId, CombatSite.ConfigFromTuning())
            {
                HologramRender = true,
            };
            site.Kernel.SetDirectClamp(double.MinValue, double.MaxValue);
            BuildingGrid g = GridContent.Building(TypeId);
            int rot = GridMath.NormalizeRotation(b.Rotation);
            var session = new Session
            {
                BuildingId = b.BuildingId,
                Site = site,
                PivotX = b.GridX,
                PivotY = b.GridY,
                Rotation = rot,
                Center = GridMath.FootprintCenter(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, rot),
                Half = Mathf.Min(g.FootprintW, g.FootprintH) * 0.5f,
                Layout = (string[])Layout(s, b.BuildingId).Clone(),
                StartTick = GameClock.Ticks,
            };
            // FGR-RND-031 / FGT-RND-004：投影、投影靶、无人机与弹体都不出靶场。边界由内核每步末尾钳住（击退 / 牵引 / 追击 / 巡逻同一处），
            // 不靠摆位；靶场只按 90° 旋转、取正方形 2×Half，所以世界里是轴对齐矩形。
            site.Kernel.SetArena(
                new double2(session.Center.x - session.Half, session.Center.y - session.Half),
                new double2(session.Center.x + session.Half, session.Center.y + session.Half));
            site.EventObserver = (cs, e) => Observe(session, e);
            // 内核时钟与家园同一条（每步传入 GameClock.GameSeconds；暂停不走、倍速按步）：测试从现在的游戏时间算起。
            // 内核在第一步之前时间是 0（Step 的 dt = 0 不推进），所以起点取游戏时钟，不取内核时间（游戏已经过了很久时不会一开始就“到时”）。
            session.StartTime = GameClock.GameSeconds;
            for (int i = 0; i < session.Layout.Length; i++)
            {
                if (TestRangeCatalog.TryGet(session.Layout[i], out RangeTargetDef def) && IsUnlocked(s, def))
                {
                    for (int n = 0; n < def.Count; n++)
                    {
                        var t = new TargetUnit { Slot = i, Index = n, Def = def };
                        PlaceTarget(session, t);
                        SpawnTarget(session, t);
                        session.Targets.Add(t);
                    }
                }
                else
                {
                    session.Layout[i] = string.Empty;
                }
            }
            CombatKernel k = site.Kernel;
            session.BaseDamage = k.DamageDealtToHostile;
            session.BaseShots = k.Counters.ShotsFired;
            session.BaseKills = k.Counters.KillsHostile;
            for (int i = 0; i < session.BaseReaction.Length; i++)
            {
                session.BaseReaction[i] = k.ReactionCountOf(i);
            }
            session.NextSample = session.StartTime + TestRangeCatalog.SampleSeconds;
            Sessions.Add(session);
            Sessions.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId)); // 多座靶场按建筑 ID 推进：与创建先后无关。
            StateOf(s).TestsRun++;
            Touch();
            return session;
        }

        private static float LaneX(Session session, int lane)
        {
            int lanes = Math.Max(1, TestRangeCatalog.ProjectionCap);
            float width = 2f * (session.Half - TestRangeCatalog.EdgeMargin);
            return -width * 0.5f + width * (lane + 0.5f) / lanes;
        }

        private static int FreeLane(Session session)
        {
            int lanes = Math.Max(1, TestRangeCatalog.ProjectionCap);
            for (int lane = 0; lane < lanes; lane++)
            {
                if (!session.Projections.Exists(p => p.Lane == lane))
                {
                    return lane;
                }
            }
            return 0;
        }

        /// <summary>靶场局部坐标（中心为原点，+y 朝远端靶位）→ 世界 XZ，按建筑朝向顺时针旋转（与 <see cref="GridMath.RotateOffset"/> 同一约定）。</summary>
        private static Vector2 LocalToWorld(Session session, float x, float y)
        {
            Vector2 d = RotateLocal(new Vector2(x, y), session.Rotation);
            return session.Center + d;
        }

        private static Vector2 RotateLocal(Vector2 v, int rotation)
        {
            int steps = GridMath.RotationSteps(rotation);
            for (int i = 0; i < steps; i++)
            {
                v = new Vector2(v.y, -v.x);
            }
            return v;
        }

        /// <summary>靶位 → 世界位置：远端 2 排 × 4 列；集群在靶位里围一圈；会摆动的靶子把中心往里收，摆动不出靶场。</summary>
        private static void PlaceTarget(Session session, TargetUnit t)
        {
            int slots = TestRangeCatalog.SlotCount;
            int cols = Math.Max(1, (slots + 1) / 2);
            int row = t.Slot / cols;
            int col = t.Slot % cols;
            float inner = session.Half - TestRangeCatalog.EdgeMargin;
            float x = -inner + 2f * inner * (col + 0.5f) / cols;
            float y = row == 0 ? inner * 0.25f : inner * 0.8f;
            if (t.Def.Count > 1)
            {
                float ang = t.Index * Mathf.PI * 2f / t.Def.Count;
                x += Mathf.Cos(ang) * TestRangeCatalog.SwarmSpread;
                y += Mathf.Sin(ang) * TestRangeCatalog.SwarmSpread;
            }
            float reach = t.Def.PatrolRadius + t.Def.Radius;
            x = Mathf.Clamp(x, -inner + reach, inner - reach);
            y = Mathf.Clamp(y, -inner + t.Def.Radius, inner - t.Def.Radius);
            t.Pos = LocalToWorld(session, x, y);
            t.Facing = RotateLocal(new Vector2(0f, -1f), session.Rotation); // 正面朝投影区（重甲靶要绕侧面打）
        }

        private static void SpawnTarget(Session session, TargetUnit t)
        {
            RangeTargetDef d = t.Def;
            var spawn = new CombatSpawn
            {
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = d.Speed > 0f ? CombatBehavior.Scout : CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Report | CombatUnitFlags.Instanced,
                Position = new double2(t.Pos.x, t.Pos.y),
                Home = new double2(t.Pos.x, t.Pos.y),
                Radius = d.Radius,
                Speed = d.Speed,
                Health = d.Hp,
                MaxHealth = d.Hp,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
                ArmorFraction = d.Armor,
                ArmorHalfAngleDeg = d.Armor > 0f ? d.ArmorHalfAngle : 90f,
                ArmorFacing = new float2(t.Facing.x, t.Facing.y),
            };
            if (d.Speed > 0f)
            {
                // 高速靶：在靶位附近左右摆动（不标记、不逃跑；摆动半径已经收在靶场里）。
                spawn.BehaviorProfile = session.Site.ProfileIndex(new CombatBehaviorProfile
                {
                    Speed = d.Speed,
                    FleeTrigger = 0f,
                    Leash = d.PatrolRadius,
                    PatrolRadius = d.PatrolRadius,
                    PatrolFreq = 2.4f,
                    SenseRange = 0f,
                    CycleSeconds = 1e6f,
                });
                spawn.Cycle = 1e6f;
            }
            t.Unit = session.Site.Kernel.Spawn(spawn);
            t.RespawnAt = -1;
            session.TargetByUnit[t.Unit] = t;
            session.Site.SetUnitLabel(t.Unit, "reaction.log.range_target", d.NameKey, argIsTextKey: true); // 参数存文本键，显示时再翻译（切语言后旧日志也跟着换）
        }

        // ─────────────────────────────── 推进（每个固定模拟步）───────────────────────────────

        /// <summary>世界模拟的每个固定步调用（<c>WorldSimulation.StepOnce</c>，家园载入时）：与观察无关、暂停不走、倍速按步。O(测试数 + 投影数（≤ 6）+ 到期复位)。</summary>
        public static void WorldStep(CampaignState state, float dt)
        {
            if (Sessions.Count == 0 || state == null)
            {
                return;
            }
            StepsRun++;
            bool silent = SignalLinkService.IsSilentNight;
            bool signalInMachine = SignalUplinkService.CurrentMachine(state) != 0;
            for (int si = Sessions.Count - 1; si >= 0; si--)
            {
                if (si >= Sessions.Count)
                {
                    continue;
                }
                Session session = Sessions[si];
                BuildingRecord b = FindRange(state, session.BuildingId);
                if (b == null || !IsUsable(state, b, out _) || b.GridX != session.PivotX || b.GridY != session.PivotY
                    || GridMath.NormalizeRotation(b.Rotation) != session.Rotation)
                {
                    End(state, session, RangeEndReason.RangeLost);
                    continue;
                }
                if (session.AnyUplinked && (silent || signalInMachine))
                {
                    if (silent)
                    {
                        if (BreakForSilentNight(state, session))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        foreach (Projection p in session.Projections)
                        {
                            if (p.Uplinked)
                            {
                                SetUplinked(session, p, false);
                                Feedback(GameText.Get("range.feedback.signal_moved"));
                            }
                        }
                    }
                }
                session.Site.Step(dt, GameClock.GameSeconds);
                AfterStep(state, session);
            }
        }

        /// <summary>静默夜开始（<see cref="SignalLinkService.OnSilentNightStarted"/> 调，或步内判定）：信号在投影里 → 断链、弹回核心、那个投影消失。</summary>
        public static bool OnSilentNightStarted()
        {
            CampaignState s = CampaignSession.Current;
            bool any = false;
            for (int i = Sessions.Count - 1; i >= 0; i--)
            {
                if (i < Sessions.Count && Sessions[i].AnyUplinked)
                {
                    BreakForSilentNight(s, Sessions[i]);
                    any = true;
                }
            }
            return any;
        }

        /// <summary>返回这次测试是否因此结束。</summary>
        private static bool BreakForSilentNight(CampaignState s, Session session)
        {
            Projection up = session.Projections.Find(p => p.Uplinked);
            if (up == null)
            {
                return false;
            }
            up.Uplinked = false;
            session.AnyUplinked = false;
            DropProjection(session, up);
            Feedback(GameText.Get("range.feedback.silent_night"));
            if (session.Projections.Count == 0)
            {
                End(s, session, RangeEndReason.SilentNight);
                return true;
            }
            return false;
        }

        private static void AfterStep(CampaignState state, Session session)
        {
            CombatKernel k = session.Site.Kernel;
            double now = k.Time;
            // 投影靶复位：待复位表按阵亡先后排（复位间隔都一样），到期的从表头取（没有待复位时 O(1)）。
            while (session.Respawns.Count > 0 && now >= session.Respawns[0].RespawnAt)
            {
                TargetUnit t = session.Respawns[0];
                session.Respawns.RemoveAt(0);
                SpawnTarget(session, t);
            }
            foreach (Projection p in session.Projections)
            {
                // 核心固件冷却结束：重新下发带门控的参数（与真实接入同一规则，FGR-SIG-033）。
                if (p.CoreReadyAt >= 0 && now >= p.CoreReadyAt)
                {
                    p.CoreReadyAt = -1;
                    ApplyWeapon(session, p, suppressed: false);
                }
                if (now >= p.NextRetarget)
                {
                    p.NextRetarget = now + TestRangeCatalog.RetargetSeconds;
                    Retarget(session, p);
                }
            }
            if (now >= session.NextSample)
            {
                session.NextSample = now + TestRangeCatalog.SampleSeconds;
                Sample(session);
            }
            if (now - session.StartTime >= TestRangeCatalog.MaxTestSeconds - 1e-6)
            {
                End(state, session, RangeEndReason.TimeUp);
            }
        }

        /// <summary>投影手上没有命令时，对最近的投影靶下攻击命令（射程 / 冷却与远征编队攻击同一组数）。最近目标的查找在内核里。</summary>
        private static void Retarget(Session session, Projection p)
        {
            CombatSite site = session.Site;
            if (site.TryGetCommand(p.Unit, out CombatCommand cmd) && cmd.Kind != CombatCommandKind.None)
            {
                return;
            }
            if (!site.Kernel.TryGetUnit(p.Unit, out CombatUnitView v))
            {
                return;
            }
            int target = site.Kernel.FindNearest(v.Position, session.Half * 4f, CombatFaction.Hostile);
            if (target == 0)
            {
                return;
            }
            site.IssueCommand(p.Unit, CombatCommandKind.Attack, new Vector2((float)v.Position.x, (float)v.Position.y), target, 0.5f,
                TestRangeCatalog.AttackRange, CombatSite.MachineAttackInterval, false);
        }

        private static void Sample(Session session)
        {
            CombatKernel k = session.Site.Kernel;
            int alive = k.CountStatusBits(CombatFaction.Hostile, BitScratch);
            session.SampleAlive += alive;
            for (int b = 0; b < 32; b++)
            {
                session.SampleBits[b] += BitScratch[b];
            }
            float heat = 0f;
            foreach (Projection p in session.Projections)
            {
                if (k.TryGetUnit(p.Unit, out CombatUnitView v))
                {
                    heat = Mathf.Max(heat, v.Heat);
                }
            }
            session.PeakHeat = Mathf.Max(session.PeakHeat, heat);
            session.HeatCurve.Add(heat);
        }

        /// <summary>本地点事件旁听（<see cref="CombatSite.EventObserver"/>）：开火 / 过热 / 投影靶阵亡 / 装配反应 / 命令结束。每条 O(1)。</summary>
        private static void Observe(Session session, CombatEvent e)
        {
            switch (e.Kind)
            {
                case CombatEventKind.Killed:
                    if (session.TargetByUnit.TryGetValue(e.Unit, out TargetUnit t))
                    {
                        session.TargetByUnit.Remove(e.Unit);
                        // 名字不在这里清（FG-GAP-061）：同一步里反应日志在事件旁听之后才写（CombatSite.Step：ProcessEvents → ReactionFeedback.Process），
                        // 反应收尾击杀时目标仍要写成“投影靶·…”。内核单位 ID 不复用，旧名字留到测试结束随地点 Dispose 一起清（最多一场测试的击杀数）。
                        t.RespawnAt = session.Site.Kernel.Time + TestRangeCatalog.RespawnSeconds;
                        session.Respawns.Add(t);
                    }
                    return;
                case CombatEventKind.Fired:
                case CombatEventKind.CannonFire:
                    if (session.ByUnit.TryGetValue(e.Unit, out Projection shooter))
                    {
                        shooter.Shots++;
                        BlueprintCircuitPreview a = shooter.Active;
                        shooter.Energy += a != null ? Mathf.Max(0, a.PowerCost) : 0f;
                    }
                    return;
                case CombatEventKind.Overheat:
                    if (session.ByUnit.TryGetValue(e.Unit, out Projection hot))
                    {
                        hot.Overheats++;
                    }
                    return;
                case CombatEventKind.CommandEnded:
                    if (session.ByUnit.TryGetValue(e.Unit, out Projection idle))
                    {
                        idle.NextRetarget = session.Site.Kernel.Time; // 目标打空：下一步就换最近的靶子。
                    }
                    return;
                case CombatEventKind.ReactionFired:
                {
                    string rid = CombatSite.ReactionIdOf((CombatReaction)e.Code);
                    if (rid == null || !session.ByUnit.TryGetValue(e.Unit, out Projection src))
                    {
                        return;
                    }
                    session.AssemblyReactions.TryGetValue(rid, out int c);
                    session.AssemblyReactions[rid] = c + 1;
                    // FGR-SIG-033：信号带进来的核心固件发动一次 → 冷却（按表的冷却秒数）；冷却期间反应不发动，固件照样插着。
                    if (src.CoreGated && e.Code2 != 0)
                    {
                        string fw = MechanicalReactionCatalog.TriggerFirmwareOf(rid);
                        float cd = fw != null ? FirmwareKinds.CoreCooldownSeconds(fw) : 0f;
                        if (cd > 0f)
                        {
                            src.CoreReadyAt = session.Site.Kernel.Time + cd;
                            ApplyWeapon(session, src, suppressed: true);
                        }
                    }
                    return;
                }
            }
        }

        // ─────────────────────────────── 结束与读数 ───────────────────────────────

        private static RangeResultRecord End(CampaignState state, Session session, RangeEndReason reason)
        {
            RangeResultRecord r = BuildResult(session, reason);
            Sessions.Remove(session);
            session.Site.EventObserver = null;
            session.Site.Dispose();
            TestRangeState rs = StateOf(state);
            if (rs != null && reason != RangeEndReason.Unloaded)
            {
                r.Serial = rs.NextResultSerial++;
                var list = new List<RangeResultRecord>(rs.History) { r };
                int keep = TestRangeCatalog.HistoryKeep;
                if (list.Count > keep)
                {
                    list.RemoveRange(0, list.Count - keep);
                }
                rs.History = list.ToArray();
                if (FindResult(state, rs.CompareA) == null)
                {
                    rs.CompareA = 0;
                }
                if (FindResult(state, rs.CompareB) == null)
                {
                    rs.CompareB = 0;
                }
                BuildingRecord b = FindRange(state, session.BuildingId);
                string detail = GameText.Format("range.notify.ended", ResultSummary(r), EndReasonText(r.EndReason), Num(r.Dps));
                NotificationCenter.Post("range_ended", detail, b != null ? new Vector3(b.Position.x, 0f, b.Position.y) : (Vector3?)null);
                Feedback(GameText.Format("range.feedback.ended", EndReasonText(r.EndReason)));
            }
            Touch();
            return r;
        }

        /// <summary>存档前（<c>WorldSimulation.SyncAllForSave</c>）：正在进行的测试直接结束，结果记进测试记录（投影不进存档，读档后靶场空闲）。</summary>
        public static int EndAllForSave(CampaignState state)
        {
            int n = 0;
            while (Sessions.Count > 0)
            {
                End(state, Sessions[Sessions.Count - 1], RangeEndReason.Saved);
                n++;
            }
            return n;
        }

        /// <summary>世界卸载 / 新会话：丢掉全部运行时投影（不记结果——对局已经离开）。</summary>
        public static void ResetSessionState()
        {
            foreach (Session s in Sessions.ToArray())
            {
                s.Site.EventObserver = null;
                s.Site.Dispose();
            }
            Sessions.Clear();
            LastFeedback = string.Empty;
            _builtHookRaised = false;
            Touch();
        }

        public static void ResetForTests()
        {
            ResetSessionState();
            StepsRun = 0;
            LastRangeId = null;
            TestRangeCatalog.Reload();
        }

        /// <summary>只在观察家园时（<c>HomeValleyController</c> 的画面帧）：全息画出投影、投影靶与弹体（每座两三次常数绘制，与单位数无关）。</summary>
        public static void FrameRender(Camera camera, float alpha)
        {
            foreach (Session s in Sessions)
            {
                s.Site.FrameRender(camera, alpha);
            }
        }

        public static void ReleaseRender()
        {
            foreach (Session s in Sessions)
            {
                s.Site.ReleaseRender();
            }
        }

        /// <summary>进行中的测试此刻的读数（面板实时显示；结束时同一函数生成记录）。</summary>
        public static RangeResultRecord LiveReadings(string buildingId)
        {
            Session s = FindSession(buildingId);
            return s != null ? BuildResult(s, RangeEndReason.None) : null;
        }

        public static float ElapsedSeconds(string buildingId)
        {
            Session s = FindSession(buildingId);
            return s != null ? (float)Math.Max(0.0, s.Site.Kernel.Time - s.StartTime) : 0f;
        }

        private static RangeResultRecord BuildResult(Session session, RangeEndReason reason)
        {
            CombatKernel k = session.Site.Kernel;
            float seconds = (float)Math.Max(0.0, k.Time - session.StartTime);
            float damage = (float)Math.Max(0.0, k.DamageDealtToHostile - session.BaseDamage);
            var reactions = new Dictionary<string, int>(session.AssemblyReactions, StringComparer.Ordinal);
            int rules = Math.Min(k.ReactionRuleCount, CombatConst.MaxReactions);
            for (int i = 0; i < rules; i++)
            {
                int d = k.ReactionCountOf(i) - session.BaseReaction[i];
                string rid = NamedReactionCatalog.IdOfRule(i);
                if (d > 0 && rid != null)
                {
                    reactions.TryGetValue(rid, out int c);
                    reactions[rid] = c + d;
                }
            }
            var rlist = new List<RangeCountRecord>();
            foreach (KeyValuePair<string, int> kv in reactions)
            {
                rlist.Add(new RangeCountRecord { Id = kv.Key, Value = kv.Value });
            }
            rlist.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Id, b.Id));
            var clist = new List<RangeCountRecord>();
            if (session.SampleAlive > 0)
            {
                for (int bit = 0; bit < 32; bit++)
                {
                    if (session.SampleBits[bit] <= 0)
                    {
                        continue;
                    }
                    string tag = NamedReactionCatalog.TagOfBit(bit);
                    if (tag == null)
                    {
                        continue;
                    }
                    clist.Add(new RangeCountRecord { Id = tag, Value = (int)Math.Round(1000.0 * session.SampleBits[bit] / session.SampleAlive) });
                }
            }
            clist.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Id, b.Id));
            int shots = session.DroppedShots;
            int overheats = session.DroppedOverheats;
            float energy = session.DroppedEnergy;
            bool uplinked = session.EverUplinked;
            foreach (Projection p in session.Projections)
            {
                shots += p.Shots;
                overheats += p.Overheats;
                energy += p.Energy;
                uplinked |= p.EverUplinked;
            }
            var labels = new List<string>(session.ProjectionLabels);
            int points = TestRangeCatalog.CurvePoints;
            float step = TestRangeCatalog.SampleSeconds;
            float[] curve;
            if (session.HeatCurve.Count <= points)
            {
                curve = session.HeatCurve.ToArray();
            }
            else
            {
                curve = new float[points];
                for (int i = 0; i < points; i++)
                {
                    int idx = (int)Math.Round(i * (session.HeatCurve.Count - 1) / (double)(points - 1));
                    curve[i] = session.HeatCurve[idx];
                }
                step = step * (session.HeatCurve.Count - 1) / (points - 1);
            }
            return new RangeResultRecord
            {
                BuildingId = session.BuildingId,
                StartTick = session.StartTick,
                EndTick = GameClock.Ticks,
                Seconds = seconds,
                Projections = labels.ToArray(),
                Targets = (string[])session.Layout.Clone(),
                Damage = damage,
                Dps = seconds > 0.01f ? damage / seconds : 0f,
                Kills = (int)Math.Max(0, k.Counters.KillsHostile - session.BaseKills),
                Shots = Math.Max(shots, (int)Math.Max(0, k.Counters.ShotsFired - session.BaseShots)),
                Energy = energy,
                PeakHeat = session.PeakHeat,
                Overheats = overheats,
                Reactions = rlist.ToArray(),
                Coverage = clist.ToArray(),
                HeatCurve = curve,
                CurveStepSeconds = step,
                EndReason = (int)reason,
                Uplinked = uplinked,
            };
        }

        public static string Num(float v) => v.ToString(v >= 100f ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>“第 2 日 08:14 · 突击型”这样的一句话（对比下拉、通知）。</summary>
        public static string ResultSummary(RangeResultRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            string who = r.Projections != null && r.Projections.Length > 0 ? string.Join(GameText.Get("range.read.sep"), r.Projections) : GameText.Get("range.compare.none");
            return who;
        }

        public static string ReactionName(string rid) =>
            NamedReactionCatalog.NameOf(rid) ?? GameText.Get("range.read.unknown_reaction");

        public static string TagName(string tag) => StatusTagCatalog.NameOf(tag) ?? tag;
    }
}
