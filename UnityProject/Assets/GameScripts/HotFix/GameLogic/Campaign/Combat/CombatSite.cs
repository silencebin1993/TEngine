using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// 地点的战斗规则挂点（热更层，事件驱动）：内核把逐单位的循环跑完，只把“需要玩法层结算”的事情作为事件交回来——
    /// 具名敌人阵亡（掉落、目标、反馈）、对血量在热更层的单位（机器、首领）的伤害 / 治疗请求、标记、兴趣点、训练靶交战。
    /// 每步事件数有上限（combat.events.gameplay_per_step），与单位数无关。各地点按自己的 Demo 规则实现子类。
    /// </summary>
    public abstract class CombatSiteRules
    {
        public virtual void OnEnemyDamaged(CombatSite site, RegionEnemyRecord enemy, float damage) { }
        public virtual void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy) { }
        /// <summary>血量在热更层的敌人（首领）受到伤害。返回 false = 结算被拒（阶段不可伤等）。</summary>
        public virtual bool ApplyExternalEnemyDamage(CombatSite site, RegionEnemyRecord enemy, float damage) => false;
        public virtual bool ApplyExternalEnemyHeal(CombatSite site, RegionEnemyRecord enemy, float amount) => false;
        /// <summary>血量在热更层、但不是区域敌人记录的目标（家园训练靶）受到编队攻击。<paramref name="attackerLogicId"/> 0 = 不是机器。</summary>
        public virtual void ApplyExternalDamageByKey(CombatSite site, string key, float damage, int attackerLogicId) { }
        /// <summary>FG6-DEF-01（DEBT-FG2FW02-07）：血量在内核、不是区域敌人记录的具名目标（家园训练靶）受了伤 / 被打空——内核结算完（载体投送与固件读法都生效）交回记录。
        /// <paramref name="attackerLogicId"/> 0 = 不是机器（持续伤害等没有来源时）。</summary>
        public virtual void OnKeyedTargetDamaged(CombatSite site, string key, float damage, float healthAfter, int attackerLogicId, bool killed) { }
        public virtual void OnMachineMarked(CombatSite site, int logicId, float seconds, RegionEnemyRecord scout) { }
        public virtual void OnMarkCleared(CombatSite site, int logicId, RegionEnemyRecord jammer) { }
        public virtual void OnMarkMissed(CombatSite site, RegionEnemyRecord scout) { }
        public virtual void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition) { }
        public virtual void OnEngageRequest(CombatSite site, int logicId) { }
        /// <summary>FG0-ARCH-06：工作赶路寻路失败（目标无法到达）。<paramref name="at"/> = 机器所在位置。</summary>
        public virtual void OnWorkBlocked(CombatSite site, int logicId, NavFailReason reason, Vector2 at) { }
        /// <summary>目标“当前不可伤”时给玩家看的原因（阶段名）。</summary>
        public virtual string InvulnerableDetail(RegionEnemyRecord enemy) => string.Empty;
    }

    /// <summary>机器武器参数的来源信息（开火提示音按主武器区分；装配解析失败时的原因）。</summary>
    public struct MachineWeaponInfo
    {
        public string PrimaryId;
        public string FailureReason;
        public int WeaponIndex;
        /// <summary>FG1-SIG-03：这组参数是按“信号接入”结算的（插入了信号核固件）。</summary>
        public bool Uplinked;
        /// <summary>FG1-SIG-03：核心固件冷却中，本该有的反应暂不发动。</summary>
        public bool ReactionSuppressed;
        /// <summary>FG1-SIG-03：这组参数的具名反应来自信号带进来的核心固件（冷却 &gt; 0）——内核发动一次后当场压住（<see cref="CombatUnitFlags.ReactionGated"/>）。</summary>
        public bool ReactionGated;
        /// <summary>FG1-SIG-06（FGR-SIG-061）：接入口里插着信号裸跑的未破解常规固件、信号上的计次间隔已过——下一发算一次“发动”（<see cref="CombatUnitFlags.RawGated"/>）。</summary>
        public bool RawGated;
        /// <summary>FG1-VFX-01：功能组件（func_dash / func_marker，没有为 null）——机身形变挑形变部件用。</summary>
        public string UtilityId;
        /// <summary>FG1-VFX-01（FGR-FW-021）：这组参数的编译结果里生效固件的类别集合 → 机身状态（<see cref="Blueprint.MachineMorph.MaskOf(Blueprint.BlueprintCircuitPreview)"/>）。
        /// 与武器参数同一次解析得出，表现层（<c>MachineMorphView</c>）只读这里，不另算。</summary>
        public Blueprint.MorphMask Morph;
        /// <summary>FG2-FW-04：这组参数的编译结果里生效的固件（反应日志“参与的固件”从这里按反应配料筛）。</summary>
        public string[] FirmwareIds;
    }

    /// <summary>
    /// FG0-ARCH-03（FG14 FGR-ARC-003）：一个地点的战斗内核门面（热更层）。一个地点一份，与地点同生命周期（载入即建、卸载即释放）。
    ///
    /// - 己方机器、具名敌人、首领、训练靶、突袭者、炮塔都是内核单位；本类只维护“LogicId / 敌人实例 ID ↔ 单位 ID”的映射。
    /// - 真相划分：位置、移动、编队命令、冷却、热量、标记、弹体 → 内核；机器与首领的血量、存在性 → 记录（内核是镜像，事件结算后回写）；
    ///   具名普通敌人的血量 → 内核（受伤 / 阵亡事件同步到记录）；突袭规模的匿名单位 → 只在内核。
    /// - 每个模拟步：<see cref="Step"/> = 内核一步（Burst）+ 排空至多 N 条事件（玩法事件与提示事件按序号合并，保持 Demo 的先后顺序）。
    /// - 存档：<see cref="Snapshot"/> / <see cref="TryRestore"/>（CombatState 域）；另把位置、热量、冷却写回记录（其它系统仍读记录）。
    /// 热更层每步开销 = 常数次内核调用 + 至多 N 条事件（FGR-SYS-042）。
    /// </summary>
    public sealed partial class CombatSite : IDisposable
    {
        public string SiteId { get; }

        /// <summary>本地点的内核。**只给桥接层（Campaign/Combat/）与 Editor 自检 / 性能探针用**：热更层玩法代码一律经本类的门面方法碰内核
        /// （FG14 §5 硬约束 3），“战斗内核”自检的扫描段断言 Campaign/Combat/ 以外的热更代码没有直接访问它。</summary>
        public CombatKernel Kernel { get; private set; }
        public CombatSiteRules Rules { get; set; }
        public RegionSquadCommandSystem Squad { get; set; }
        public CombatTransformSync Sync { get; private set; }
        public CombatRenderer Renderer { get; private set; }

        /// <summary>FG5-RND-03：全息画法（靶场的仿真投影与投影靶，FGR-RND-031）。在第一次绘制前设置；渲染器按它画扫描线全息。</summary>
        public bool HologramRender { get; set; }

        /// <summary>FG5-RND-03：本地点自己的事件旁听者（靶场读数：开火、过热、阵亡、装配反应）。在 <see cref="ProcessEvents"/> 里逐条先于结算调用，
        /// O(本步事件数)；不改变结算本身。普通地点为空。</summary>
        public Action<CombatSite, CombatEvent> EventObserver { get; set; }

        // FG5-RND-03（FG-GAP-061）：不登记成机器 / 敌人的内核单位（训练靶、靶场投影与投影靶）在反应日志里的名字（文本键 + 参数）与参与的固件。
        private readonly Dictionary<int, (string Key, string Arg, bool ArgIsKey, string[] Firmware)> _unitLabels = new Dictionary<int, (string, string, bool, string[])>();

        /// <summary>FG5-RND-03（FG-GAP-061）：给一个内核单位起日志里的名字（例如“训练靶”“投影靶·重甲靶”“投影·突击型”）；<paramref name="firmware"/> = 它生效的固件（反应日志“参与的固件”）。</summary>
        /// <paramref name="argIsTextKey"/> = true 时参数本身也是文本键（例如靶子名），日志显示时再翻译，切语言后旧日志跟着换。
        public void SetUnitLabel(int unitId, string textKey, string arg = null, string[] firmware = null, bool argIsTextKey = false)
        {
            if (unitId <= 0 || string.IsNullOrEmpty(textKey))
            {
                return;
            }
            _unitLabels[unitId] = (textKey, arg, argIsTextKey, firmware);
        }

        public void ClearUnitLabel(int unitId) => _unitLabels.Remove(unitId);

        public bool TryGetUnitLabel(int unitId, out string textKey, out string arg, out string[] firmware) =>
            TryGetUnitLabel(unitId, out textKey, out arg, out _, out firmware);

        public bool TryGetUnitLabel(int unitId, out string textKey, out string arg, out bool argIsTextKey, out string[] firmware)
        {
            if (_unitLabels.TryGetValue(unitId, out (string Key, string Arg, bool ArgIsKey, string[] Firmware) v))
            {
                textKey = v.Key;
                arg = v.Arg;
                argIsTextKey = v.ArgIsKey;
                firmware = v.Firmware;
                return true;
            }
            textKey = null;
            arg = null;
            argIsTextKey = false;
            firmware = null;
            return false;
        }

        /// <summary>玩家机器记录的阵营标识（MachineRecord.FactionId；Demo 敌人命中机器入口的阵营校验）。</summary>
        public const string MachinePlayerFaction = "Player";

        /// <summary>每步处理的玩法事件上限、提示事件上限（来自 combat.events.*）。</summary>
        public int MaxEventsPerStep { get; }

        /// <summary>最近一步的耗时（内核 / 事件处理，毫秒）与本步处理的事件数（性能证据）。</summary>
        public double LastKernelMs => Kernel?.LastStepMs ?? 0;
        public double LastEventsMs { get; private set; }
        public int LastEventsProcessed { get; private set; }
        public long TotalEventsProcessed { get; private set; }

        private readonly Dictionary<int, int> _machineUnit = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _unitMachine = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _enemyUnit = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _unitEnemy = new Dictionary<int, string>();
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, int> _keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, HomeValleyMachineMarker> _markerByUnit = new Dictionary<int, HomeValleyMachineMarker>();
        private readonly Dictionary<string, int> _weaponIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, MachineWeaponInfo> _machineWeapons = new Dictionary<int, MachineWeaponInfo>();
        private readonly List<CombatEvent> _merge = new List<CombatEvent>(64);
        private static readonly Comparison<CombatEvent> BySeq = (a, b) => a.Seq.CompareTo(b.Seq);
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        public CombatSite(string siteId, in CombatConfig config)
        {
            SiteId = siteId;
            Kernel = new CombatKernel(config, 64);
            ConfigureReactions();
            Sync = new CombatTransformSync(16);
            MaxEventsPerStep = Math.Max(1, config.MaxGameplayEventsPerStep);
            _unitHeight = Tuning("combat.render.unit_height", 0.6f);
        }

        private readonly float _unitHeight;

        /// <summary>FG2-FW-03：把具名标签反应规则（fg.TbReaction，按 priority）与状态位效果（fg.TbStatusTag）登记进内核。
        /// 规则是内容：建地点时登记一次；表重载（<see cref="NamedReactionCatalog.Revision"/> 变了）后由 <see cref="Step"/> 自动重新登记（计数按反应的稳定键保留）；读档沿用。</summary>
        public void ConfigureReactions()
        {
            if (IsDisposed)
            {
                return;
            }
            Kernel.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            Kernel.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            ReactionRevision = NamedReactionCatalog.Revision;
            // FG2-FW-04：规则下标变了（纪元 +1）→ 当场重取反馈基线，下一步起的反应照常进日志 / 归因（不把第一步吞成基线）。
            ReactionFeedback.Prime(Kernel, FeedCursor);
        }

        /// <summary>最近一次登记时反应表的版本号（与 <see cref="NamedReactionCatalog.Revision"/> 不同 = 表重载过，下一步重新登记）。</summary>
        public int ReactionRevision { get; private set; }

        private bool _statusTagHookRaised;

        /// <summary>FG2-FW-03：最近一次具名标签反应（自检 / 冒烟读）：反应 ID、是否报出了名字、累计处理的次数（提示事件，超上限会丢）。</summary>
        public string LastTagReactionId { get; private set; }
        public bool LastTagReactionNamed { get; private set; }
        public int TagReactionCuesHandled { get; private set; }

        public bool IsDisposed => Kernel == null || Kernel.IsDisposed;

        /// <summary>B22：原型单位（突袭者 / 炮塔）的画面是占位（按阵营着色的圆片 + 血量环，弹体是发光短线）。
        /// 第一次有原型单位生成到本地点时，在调试层标记一次（文本键 combat.placeholder；正式模型随 FG6 替换）。</summary>
        public bool PlaceholderMarked { get; private set; }

        public void MarkPlaceholderVisuals()
        {
            if (PlaceholderMarked)
            {
                return;
            }
            PlaceholderMarked = true;
            Log.Info($"[CombatSite] {SiteId}：{GameText.Get("combat.placeholder")}");
        }

        public void Dispose()
        {
            StatusTagHover.Leave(this);
            Renderer?.Dispose();
            Renderer = null;
            Sync?.Dispose();
            Sync = null;
            Kernel?.Dispose();
            Kernel = null;
            _machineUnit.Clear();
            _unitMachine.Clear();
            _enemyUnit.Clear();
            _unitEnemy.Clear();
            _markerByUnit.Clear();
            _enemyIndex.Clear();
            _enemyIndexArray = null;
            _unitLabels.Clear();
            ClearTurretMaps();
            EventObserver = null;
        }

        // ─────────────────────────────── 配置 ───────────────────────────────

        public static CombatConfig ConfigFromTuning()
        {
            CombatConfig c = CombatConfig.Default;
            c.GridCell = Tuning("combat.grid_cell_m", c.GridCell);
            c.MaxGameplayEventsPerStep = (int)Math.Round(Tuning("combat.events.gameplay_per_step", c.MaxGameplayEventsPerStep));
            c.MaxCueEventsPerStep = (int)Math.Round(Tuning("combat.events.cues_per_step", c.MaxCueEventsPerStep));
            c.ProjectileCapacity = (int)Math.Round(Tuning("combat.projectile_capacity", c.ProjectileCapacity));
            c.CompactRatio = Tuning("combat.compact_ratio", c.CompactRatio);
            // FG2-FW-02：读法生成的区域 / 回波 / 无人机容量与节拍（fg.TbHomeTuning reading.*）。
            c.ZoneCapacity = (int)Math.Round(Tuning("reading.capacity.zones", CombatConst.DefaultZoneCapacity));
            c.EchoCapacity = (int)Math.Round(Tuning("reading.capacity.echoes", CombatConst.DefaultEchoCapacity));
            c.DroneCapacity = (int)Math.Round(Tuning("reading.capacity.drones", CombatConst.DefaultDroneCapacity));
            c.DroneSpeed = Tuning("reading.drone.speed", 9f);
            c.DroneReach = Tuning("reading.drone.reach", 1.2f);
            c.StatusTick = Tuning("reading.tick.status", 0.5f);
            c.ZoneTick = Tuning("reading.tick.zone", 0.5f);
            c.ZoneStatusSeconds = Tuning("reading.zone.status_seconds", 2f);
            c.WeaveMargin = Tuning("reading.weave.margin", 0.6f);
            // FG2-FW-03：状态标签叠层上限（fg.TbHomeTuning status.stack_cap）。
            c.StatusStackCap = (int)Math.Round(Tuning("status.stack_cap", 3f));
            // FG2-E2E-01（FG-GAP-043）：引信弹迹 / 炮口装定闪光停留的游戏秒。
            c.FuseTraceSeconds = Math.Max(0f, Tuning("combat.fuse_trace_seconds", c.FuseTraceSeconds));
            // FG6-DEF-01（FGR-DEF-002）：有转速的炮塔对准到这个夹角以内才开火。
            c.TurretAimToleranceDeg = Math.Max(0.5f, Tuning("turret.aim_tolerance_deg", 6f));
            return c;
        }

        public static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Error($"[CombatSite] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_combat.py 后重新生成）。");
            return fallback;
        }

        // ─────────────────────────────── 机器 ───────────────────────────────

        /// <summary>把一台机器放进本地点的内核（出生 / 进场 / 读档重建）。已在内核里时返回原句柄。</summary>
        public HomeValleyMachineMarker SpawnMachine(CampaignState state, MachineRecord rec, Vector2 position, bool autoEngage)
        {
            if (rec == null || IsDisposed)
            {
                return null;
            }
            if (_machineUnit.TryGetValue(rec.LogicId, out int existing) && _markerByUnit.TryGetValue(existing, out HomeValleyMachineMarker m))
            {
                return m;
            }
            MachineWeaponInfo info = ResolveMachineWeapon(state, rec.LogicId);
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.ExternalHealth | CombatUnitFlags.WeaponEnabled;
            if (rec.IsWeaponOverheated)
            {
                flags |= CombatUnitFlags.Overheated;
            }
            if (HasHeatSink(state, rec.LogicId))
            {
                flags |= CombatUnitFlags.HeatSink;
            }
            if (rec.IsInFactory)
            {
                flags |= CombatUnitFlags.EngageHold;
            }
            if (info.ReactionGated)
            {
                flags |= CombatUnitFlags.ReactionGated;
            }
            if (info.RawGated)
            {
                flags |= CombatUnitFlags.RawGated;
            }
            if (!rec.IsAlive)
            {
                flags &= ~(CombatUnitFlags.Alive | CombatUnitFlags.Targetable);
            }
            int unit = Kernel.Spawn(new CombatSpawn
            {
                ExtKey = rec.LogicId,
                Kind = CombatUnitKind.Machine,
                Faction = CombatFaction.Player,
                Behavior = autoEngage ? CombatBehavior.AutoEngage : CombatBehavior.Commanded,
                Flags = flags,
                Position = new double2(position.x, position.y),
                Home = new double2(position.x, position.y),
                Radius = 0.9f,
                Speed = HomeValleyMachineMarker.MoveSpeed,
                Health = rec.Health,
                MaxHealth = rec.MaxHealth,
                Weapon = info.WeaponIndex,
                BehaviorProfile = -1,
                Priority = 1,
                Heat = rec.WeaponHeat,
                AimReadyAt = rec.CannonAimReadyAtPlaySeconds,
                NextFireAt = rec.NextCannonActionAtPlaySeconds,
            });
            return AttachMachine(rec, unit);
        }

        private HomeValleyMachineMarker AttachMachine(MachineRecord rec, int unit)
        {
            _machineUnit[rec.LogicId] = unit;
            _unitMachine[unit] = rec.LogicId;
            var marker = new HomeValleyMachineMarker(rec.LogicId, rec.ChassisId, this, unit);
            _markerByUnit[unit] = marker;
            return marker;
        }

        /// <summary>从内核移除一台机器（离开本地点 / 被派遣 / 卸载）。<paramref name="export"/> 时先把位置、热量、冷却写回记录。</summary>
        public void RemoveMachine(int logicId, bool export)
        {
            if (!_machineUnit.TryGetValue(logicId, out int unit))
            {
                return;
            }
            if (export)
            {
                ExportMachine(logicId, includePosition: true);
            }
            Sync?.Unbind(unit);
            Kernel?.Despawn(unit);
            _machineUnit.Remove(logicId);
            _unitMachine.Remove(unit);
            _markerByUnit.Remove(unit);
            _machineWeapons.Remove(logicId);
        }

        /// <summary>把内核里的实时状态写回机器记录（位置、武器热量与冷却；血量真相本来就在记录里）。</summary>
        public void ExportMachine(int logicId, bool includePosition)
        {
            if (!_machineUnit.TryGetValue(logicId, out int unit) || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec)
                || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return;
            }
            if (includePosition && rec.IsAlive)
            {
                rec.WorldPosition = new Vector2((float)v.Position.x, (float)v.Position.y);
            }
            rec.WeaponHeat = v.Heat;
            rec.IsWeaponOverheated = (v.Flags & CombatUnitFlags.Overheated) != 0;
            rec.CannonAimReadyAtPlaySeconds = (float)v.AimReadyAt;
            rec.NextCannonActionAtPlaySeconds = (float)v.NextFireAt;
        }

        public bool TryGetMachineUnit(int logicId, out int unitId) => _machineUnit.TryGetValue(logicId, out unitId);

        /// <summary>FG1-SIG-05：这台机器此刻的武器热量与是否过热（内核实时状态）。过热是机体状态——谁在开都一样，
        /// 交还 AI 时照样要等散热到恢复线以下才能开火（原 Demo OverloadSuppressionMirror 的“按身体归属”规则在正式版的落点）。</summary>
        public bool TryGetMachineHeat(int logicId, out float heat, out bool overheated)
        {
            heat = 0f;
            overheated = false;
            if (!_machineUnit.TryGetValue(logicId, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            heat = v.Heat;
            overheated = (v.Flags & CombatUnitFlags.Overheated) != 0;
            return true;
        }

        public bool TryGetMachineMarker(int logicId, out HomeValleyMachineMarker marker)
        {
            marker = null;
            return _machineUnit.TryGetValue(logicId, out int unit) && _markerByUnit.TryGetValue(unit, out marker);
        }

        public bool TryGetMachineOfUnit(int unitId, out int logicId) => _unitMachine.TryGetValue(unitId, out logicId);

        private readonly List<int2> _coverageChanges = new List<int2>(8);

        /// <summary>
        /// FG1-SIG-07（FGR-SIG-053）：按“与归还核心连通的覆盖圆”评估本地点每台己方机器在不在覆盖里（逐单位在内核 Burst 作业里做），
        /// 把状态变了的机器（LogicId，true = 回到覆盖）追加到 <paramref name="changes"/>。世界模拟按游戏时间定期调用，与是否被观察无关。
        /// </summary>
        public int EvaluateCoverage(IReadOnlyList<float3> sources, List<(int LogicId, bool Covered)> changes)
        {
            if (IsDisposed)
            {
                return 0;
            }
            Kernel.EvaluateCoverage(sources, _coverageChanges);
            int n = 0;
            for (int i = 0; i < _coverageChanges.Count; i++)
            {
                int2 c = _coverageChanges[i];
                if (_unitMachine.TryGetValue(c.x, out int logicId))
                {
                    changes?.Add((logicId, c.y != 0));
                    n++;
                }
            }
            return n;
        }

        /// <summary>FG1-SIG-07：这台机器是否被标为在信号覆盖之外（最近一次定期评估的结果；O(1)，机器列表与冒烟读取）。</summary>
        public bool IsMachineOutOfCoverage(int logicId) =>
            !IsDisposed && _machineUnit.TryGetValue(logicId, out int unit) && Kernel.IsOutOfCoverage(unit);

        public IEnumerable<int> MachineLogicIds => _machineUnit.Keys;
        public int MachineCount => _machineUnit.Count;

        public bool TryGetMachinePosition(int logicId, out Vector2 position)
        {
            if (_machineUnit.TryGetValue(logicId, out int unit) && Kernel.TryGetPosition(unit, out double2 p))
            {
                position = new Vector2((float)p.x, (float)p.y);
                return true;
            }
            position = default;
            return false;
        }

        public bool TryGetUnitPosition(int unitId, out double2 position)
        {
            if (Kernel == null || Kernel.IsDisposed)
            {
                position = default;
                return false;
            }
            return Kernel.TryGetPosition(unitId, out position);
        }

        public void SetUnitPosition(int unitId, Vector2 position) => Kernel?.SetPosition(unitId, new double2(position.x, position.y));

        /// <summary>工作赶路命令的“目标”字段在内核里不参与规则（WorkMove 只看目标点），用来记“这条赶路带到达回调”：
        /// 回调本身只在内存里，读档后家园据此按在办的工作订单重挂（否则机器走到了却不开工，只能等停滞看门狗）。</summary>
        public const int WorkMoveArrivalTag = 1;

        public void IssueWorkMove(int unitId, Vector2 target, bool hasArrivalAction)
        {
            Kernel?.IssueCommand(unitId, CombatCommandKind.WorkMove, new double2(target.x, target.y), hasArrivalAction ? WorkMoveArrivalTag : 0,
                HomeValleyMachineMarker.ArrivalDistance, 0f, 0f, false);
        }

        /// <summary>接入状态：受控机不执行编队命令，位移来自直控输入。</summary>
        public void SetPossessed(int logicId, bool possessed)
        {
            if (!_machineUnit.TryGetValue(logicId, out int unit))
            {
                return;
            }
            Kernel.SetFlag(unit, CombatUnitFlags.Possessed, possessed);
            if (!possessed)
            {
                Kernel.SetDirectInput(unit, float2.zero);
            }
        }

        /// <summary>机器进出工厂（MachineRecord.IsInFactory 变化）：厂内机器不参与自动交战、不消耗交战冷却。</summary>
        public void SetMachineInFactory(int logicId, bool inFactory)
        {
            if (!IsDisposed && _machineUnit.TryGetValue(logicId, out int unit))
            {
                Kernel.SetFlag(unit, CombatUnitFlags.EngageHold, inFactory);
            }
        }

        /// <summary>机器阵亡（记录已经翻转）：内核单位标为阵亡、清命令、不再可被选中。</summary>
        public void OnMachineDied(int logicId)
        {
            if (_machineUnit.TryGetValue(logicId, out int unit))
            {
                Kernel.Kill(unit, 0);
            }
        }

        /// <summary>血量在记录里的机器被维修 / 改造后回写镜像。</summary>
        public void SyncMachineHealth(MachineRecord rec)
        {
            if (rec != null && _machineUnit.TryGetValue(rec.LogicId, out int unit))
            {
                Kernel.SetHealth(unit, rec.Health, rec.MaxHealth, rec.IsAlive);
            }
        }

        // ── 武器（装配变化时才解析，不在每次开火时编译装配）──

        public bool TryGetMachineWeapon(int logicId, out MachineWeaponInfo info) => _machineWeapons.TryGetValue(logicId, out info);

        /// <summary>装配登记变了：重算这台机器的武器参数（O(1) 次装配解析）。</summary>
        public void RefreshMachineWeapon(CampaignState state, int logicId)
        {
            if (!_machineUnit.TryGetValue(logicId, out int unit))
            {
                return;
            }
            MachineWeaponInfo info = ResolveMachineWeapon(state, logicId);
            Kernel.SetUnitWeapon(unit, info.WeaponIndex);
            Kernel.SetFlag(unit, CombatUnitFlags.HeatSink, HasHeatSink(state, logicId));
            // FG1-SIG-03：门控只跟着“信号带进来的核心固件反应”走。新参数不带门控反应（冷却中压住的行、离开后的本地配置）时，
            // 内核里“已发动、等冷却下发”的标记一并清掉；仍是门控行时保留（发动事件还没被处理的那几步里不能再发动）。
            Kernel.SetFlag(unit, CombatUnitFlags.ReactionGated, info.ReactionGated);
            if (!info.ReactionGated)
            {
                Kernel.SetFlag(unit, CombatUnitFlags.ReactionSpent, false);
            }
            // FG1-SIG-06：裸跑计次同一套“门控 / 已发动”：计次间隔内下发不带门控的参数时清掉“已发动”，间隔到了重新带上门控。
            Kernel.SetFlag(unit, CombatUnitFlags.RawGated, info.RawGated);
            if (!info.RawGated)
            {
                Kernel.SetFlag(unit, CombatUnitFlags.RawSpent, false);
            }
            MachineWeaponRefreshed?.Invoke(this, logicId);
        }

        /// <summary>FG1-VFX-01：某台机器按新的编译结果重算了武器参数（接入 / 离开 / 装配变更 / 冷却起止）之后触发——参数是地点与 LogicId，
        /// 新的 <see cref="MachineWeaponInfo.Morph"/> 已写好。机身形变表现订阅它（O(1)，不按帧、不扫机器表）。</summary>
        public static event Action<CombatSite, int> MachineWeaponRefreshed;

        private MachineWeaponInfo ResolveMachineWeapon(CampaignState state, int logicId)
        {
            var info = new MachineWeaponInfo { WeaponIndex = -1 };
            if (state == null)
            {
                info.FailureReason = GameText.Get("combat.fire.no_weapon");
                _machineWeapons[logicId] = info;
                return info;
            }
            // FG1-SIG-03：信号接入这台机器时按接入结算（信号核固件插进接入口），否则是 AI 驾驶的本地配置——离开时自动回到本地配置。
            MachineCombatResolution resolution = MachineLoadoutRegistry.ResolveForPilot(state, logicId, state.RandomSeed);
            if (!resolution.Success)
            {
                info.FailureReason = resolution.FailureReason;
                _machineWeapons[logicId] = info;
                return info;
            }
            BlueprintCircuitPreview p = resolution.Preview;
            info.PrimaryId = p.PrimaryId;
            info.UtilityId = p.UtilityId;
            info.Morph = MachineMorph.MaskOf(p); // FG1-VFX-01：机身状态 = 本次编译结果里生效固件的类别集合。
            info.FirmwareIds = p.FirmwareIds ?? Array.Empty<string>();
            info.Uplinked = SignalUplinkService.IsUplinked(state, logicId);
            // FGR-SIG-033：信号带进来的核心固件正在冷却（冷却属于信号）时，这条反应暂不发动；固件照样插着。
            info.ReactionSuppressed = SignalUplinkService.IsReactionSuppressed(state, logicId, p.ReactionId);
            info.ReactionGated = !info.ReactionSuppressed && info.Uplinked
                                 && SignalUplinkService.IsCoreGatedReaction(p.ReactionId, p.UplinkFirmwareIds);
            // FG1-SIG-06（FGR-SIG-061）：接入口里有信号裸跑的未破解常规固件、且信号上的计次间隔已过——下一发计一次暴露。
            info.RawGated = info.Uplinked && RawFirmwareService.ShouldArmCharge(state, p.RawFirmwareIds);
            CombatWeapon w = MachineWeaponFrom(p, info.ReactionSuppressed);
            info.WeaponIndex = WeaponIndex(w);
            _machineWeapons[logicId] = info;
            return info;
        }

        private static bool HasHeatSink(CampaignState state, int logicId)
        {
            if (state == null || !MachineLoadoutRegistry.IsRegistered(logicId))
            {
                return false;
            }
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(state, logicId, state.RandomSeed);
            return r.Success && r.Preview.HasHeatSinkStructure;
        }

        /// <summary>
        /// 装配预览 → 内核武器参数（Demo 规则的唯一翻译处）：
        /// 铸造重炮 = 两段式（1 秒瞄准线、3 秒冷却、55 基础伤害、积热 40、熔穿过载 +25 积热与 30% 穿甲、100 过热 / 60 恢复、散热 10（散热鳍 +5））；
        /// 其余 = 即时命中，伤害 = 装配编译出的 TotalNormalizedDamage；装了标记器命中打 10 秒标记；标记跳转 8 米内至多 2 个、每跳 ×0.6。
        /// FG2-FW-02：载体与固件读法由 <see cref="CarrierReadings.Build"/> 按“主组件的载体 × 生效固件的读法字段”查表翻译（<see cref="CombatWeapon.Reading"/>）；
        /// FG2-FW-02 新建的作战组件（格斗 / 无人机 / 力场 / 布区）用表里的基础伤害；即时命中武器也按固件积热（DEBT-FG1SIG06-02）。
        /// </summary>
        public static CombatWeapon MachineWeaponFrom(BlueprintCircuitPreview p) => MachineWeaponFrom(p, false);

        /// <summary>FG2-FW-02：机器的基础出手间隔（游戏秒，fg.TbHomeTuning combat.machine.attack_interval）——编队攻击命令的攻击间隔与即时命中武器的冷却同一个数
        /// （驻守开火的炮塔、直控蓄力门槛都按它 × 读法的出手间隔倍率）。</summary>
        public static float MachineAttackInterval => Tuning("combat.machine.attack_interval", 1.2f);

        /// <summary>FG2-FW-02（B13）：这套装配每发实际打出的基础伤害（与 <see cref="MachineWeaponFrom(BlueprintCircuitPreview)"/> 同源：新作战组件用表里的伤害、
        /// 重炮 55、连射器 / 切割束按电路编译伤害，再乘读法的伤害倍率；不含装甲 / 侧后 / 状态这些按目标结算的部分）。
        /// 电路编辑器、双态预览、远征预测、家园低威胁靶都读它，玩家看到的数就是内核打出的数。没有攻击出口为 0。</summary>
        public static float MachineHitDamage(BlueprintCircuitPreview p)
        {
            // 重炮不看电路出口（内核 FireCannon 也不看 HasOutput，与 Demo 一致）；其余武器没有攻击出口就打不出伤害。
            if (p == null || (!p.HasCannonPrimary && !p.HasCombatOutput))
            {
                return 0f;
            }
            CombatWeapon w = MachineWeaponFrom(p);
            float scale = w.Reading.DamageScale > 0f ? w.Reading.DamageScale : 1f;
            return Mathf.Max(0f, w.Damage) * scale;
        }

        /// <summary><paramref name="suppressReaction"/>：FG1-SIG-03 核心固件冷却中——反应不发动（熔穿过载不额外积热、不穿甲；标记跳转不跳），其余参数不变。</summary>
        public static CombatWeapon MachineWeaponFrom(BlueprintCircuitPreview p, bool suppressReaction)
        {
            var w = new CombatWeapon
            {
                Dissipation = FracturedCityLayout.WeaponHeatDissipationPerSecond,
                HeatSinkBonus = FracturedCityLayout.HeatSinkBonusDissipationPerSecond,
                OverheatAt = FracturedCityLayout.WeaponHeatOverheatThreshold,
                RecoverBelow = FracturedCityLayout.WeaponHeatRecoverThreshold,
                HasOutput = (byte)(p.HasCombatOutput ? 1 : 0),
                TargetMode = CombatTargetMode.Nearest,
            };
            if (p.HasCannonPrimary)
            {
                w.Mode = CombatWeaponMode.Cannon;
                w.Range = FracturedCityLayout.CannonRange;
                w.Damage = FracturedCityLayout.CannonBaseDamage;
                w.Cooldown = FracturedCityLayout.CannonCooldownSeconds;
                w.AimSeconds = FracturedCityLayout.CannonAimSeconds;
                // FG1-SIG-06（FGR-SIG-061）：信号裸跑未破解固件时积热 ×1.5（编译结果里的倍率，与双态预览的热量预算同一个数）。
                float rawHeat = p.RawHeatMultiplier > 0f ? p.RawHeatMultiplier : 1f;
                // FG2-FW-01（FGR-FW-001“热量”）：生效固件自己的每发积热（表 heat 列）叠在基础积热上，与热量预算同一规则（熔穿过载时过载一项由 OverloadExtraHeat 取代）。
                w.HeatPerShot = (FracturedCityLayout.CannonBaseHeatPerShot + Mathf.Max(0f, p.FirmwareHeatPerShot)) * rawHeat;
                w.OverloadExtraHeat = FracturedCityLayout.OverloadExtraHeatPerShot * rawHeat;
                w.PierceBonus = FracturedCityLayout.OverloadArmorPierceBonus;
                w.Reaction = !suppressReaction && p.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId ? CombatReaction.MeltOverload : CombatReaction.None;
                // FG2-FW-02：重炮是射弹载体；伤害是固定值（不经电路编译），“伤害倍率”读法（电容蓄力）按表乘上。
                w.Reading = CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, ReadingFirmware(p), damageFromCompile: false);
                w.FuseTrace = FuseTraceOf(p);
                return w;
            }
            w.Mode = CombatWeaponMode.Instant;
            // FG2-FW-02：新建作战组件用表里的基础伤害；Demo 的连射器 / 切割束仍按电路编译伤害（固件的能量改动已经算在里面）。
            bool fixedDamage = CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent comp) && comp.Damage > 0f;
            w.Damage = fixedDamage ? comp.Damage : Mathf.Max(0f, p.TotalNormalizedDamage);
            w.Range = FracturedCityLayout.DirectAttackRange;
            // FG2-FW-02：即时命中武器也有自己的冷却（与编队攻击间隔同一个数）——驻守开火的炮塔按它出手，蓄力读法（电容蓄力）按它 × 倍率蓄满。
            w.Cooldown = MachineAttackInterval;
            w.MarkSeconds = p.HasMarkerFunction ? FracturedCityLayout.EnemyMarkDurationSeconds : 0f;
            // DEBT-FG1SIG06-02：即时命中武器也按生效固件积热（与热量预算同一个数；裸跑 ×1.5），过热停火、降到恢复线以下再开火。
            float rawHeatI = p.RawHeatMultiplier > 0f ? p.RawHeatMultiplier : 1f;
            w.HeatPerShot = Mathf.Max(0f, p.FirmwareHeatPerShot) * rawHeatI;
            w.Reading = CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, ReadingFirmware(p), damageFromCompile: !fixedDamage);
            if (!suppressReaction && p.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId)
            {
                w.Reaction = CombatReaction.MarkJump;
                w.JumpRange = FracturedCityLayout.MarkJumpRange;
                w.JumpFalloff = FracturedCityLayout.MarkJumpDamageFalloff;
                w.JumpMax = FracturedCityLayout.MarkJumpMaxTargets;
            }
            w.FuseTrace = FuseTraceOf(p);
            return w;
        }

        /// <summary>FG2-E2E-01（FG-GAP-043）：这套装配的生效固件里有引信类（按表 category 列，不按 ID 写特例）→ 开火带引信弹迹与炮口装定闪光。
        /// 与机身形变同一个“生效固件”集合（<see cref="BlueprintCircuitPreview.FirmwareIds"/>：AI 本地配置或接入时插进接入口的都算）。</summary>
        public static byte FuseTraceOf(BlueprintCircuitPreview p)
        {
            if (p?.FirmwareIds == null)
            {
                return 0;
            }
            foreach (string fw in p.FirmwareIds)
            {
                if (!string.IsNullOrEmpty(fw) && FirmwareKinds.CategoryOf(fw) == FirmwareCategory.Fuse)
                {
                    return 1;
                }
            }
            return 0;
        }

        /// <summary>FG2-FW-02：参与普通读法的固件——具名反应的触发固件（熔穿过载 ← 过载、标记跳转 ← 标记跳转）在这套装配上由反应取代它的普通读法
        /// （反应冷却中也不回退成普通读法，与“冷却中不发动”一致）。</summary>
        public static IReadOnlyList<string> ReadingFirmware(BlueprintCircuitPreview p)
        {
            string trigger = MechanicalReactionCatalog.TriggerFirmwareOf(p.ReactionId);
            if (trigger == null || p.FirmwareIds == null)
            {
                return p.FirmwareIds;
            }
            var list = new List<string>(p.FirmwareIds.Length);
            foreach (string f in p.FirmwareIds)
            {
                if (f != trigger)
                {
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>武器表去重：同样的参数只占一行（机器反复进出、装配反复刷新不会让表无限增长）。</summary>
        public int WeaponIndex(in CombatWeapon w)
        {
            // FG2-FW-02：键取武器参数的完整字节（含读法），新增字段自动算进去——不会把两套不同读法的武器并成一行。
            string key = CombatKernel.WeaponKey(w);
            if (_weaponIndex.TryGetValue(key, out int idx) && Kernel.TryGetWeapon(idx, out CombatWeapon existing) && existing.Equals(w))
            {
                return idx;
            }
            idx = Kernel.AddWeapon(w);
            _weaponIndex[key] = idx;
            return idx;
        }

        private readonly Dictionary<string, int> _profileIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>行为参数表去重。</summary>
        public int ProfileIndex(in CombatBehaviorProfile p)
        {
            string key = string.Join("|", p.Speed, p.FleeTrigger, p.Leash, p.PatrolRadius, p.PatrolFreq, p.SenseRange, p.CycleSeconds,
                p.EffectSeconds, p.EffectAmount, p.EffectRange);
            if (_profileIndex.TryGetValue(key, out int idx) && idx < Kernel.ProfileCount)
            {
                return idx;
            }
            idx = Kernel.AddProfile(p);
            _profileIndex[key] = idx;
            return idx;
        }

        // ─────────────────────────────── 敌人 ───────────────────────────────

        /// <summary>把一个具名敌人记录放进内核（进场 / 首领初始化 / 召唤 / 读档重建）。<paramref name="template"/> 由地点的 Demo 内容翻译给出。</summary>
        public int SpawnEnemy(RegionEnemyRecord rec, CombatSpawn template)
        {
            if (rec == null || IsDisposed)
            {
                return 0;
            }
            if (_enemyUnit.TryGetValue(rec.EnemyInstanceId, out int existing) && Kernel.Exists(existing))
            {
                return existing;
            }
            template.ExtKey = KeyIndex(rec.EnemyInstanceId);
            template.Position = new double2(rec.Position.x, rec.Position.y);
            template.Health = rec.Health;
            template.MaxHealth = rec.MaxHealth;
            template.Cycle = rec.CycleCooldownRemaining;
            template.Secondary = rec.SecondaryTimer;
            if (!rec.IsAlive)
            {
                template.Flags &= ~(CombatUnitFlags.Alive | CombatUnitFlags.Targetable);
            }
            int unit = Kernel.Spawn(template);
            _enemyUnit[rec.EnemyInstanceId] = unit;
            _unitEnemy[unit] = rec.EnemyInstanceId;
            return unit;
        }

        public void RemoveEnemy(string enemyInstanceId)
        {
            if (enemyInstanceId == null || !_enemyUnit.TryGetValue(enemyInstanceId, out int unit))
            {
                return;
            }
            Sync?.Unbind(unit);
            Kernel?.Despawn(unit);
            _enemyUnit.Remove(enemyInstanceId);
            _unitEnemy.Remove(unit);
        }

        private int KeyIndex(string key)
        {
            if (_keyIndex.TryGetValue(key, out int i))
            {
                return i;
            }
            i = _keys.Count;
            _keys.Add(key);
            _keyIndex[key] = i;
            return i;
        }

        public bool TryGetEnemyUnit(string enemyInstanceId, out int unitId)
        {
            unitId = 0;
            return enemyInstanceId != null && _enemyUnit.TryGetValue(enemyInstanceId, out unitId);
        }

        public bool TryGetEnemyOfUnit(int unitId, out string enemyInstanceId) => _unitEnemy.TryGetValue(unitId, out enemyInstanceId);

        public IEnumerable<string> EnemyIds => _enemyUnit.Keys;

        public bool TryGetEnemyPosition(string enemyInstanceId, out Vector2 position)
        {
            if (TryGetEnemyUnit(enemyInstanceId, out int unit) && Kernel.TryGetPosition(unit, out double2 p))
            {
                position = new Vector2((float)p.x, (float)p.y);
                return true;
            }
            position = default;
            return false;
        }

        public bool IsEnemyAlive(string enemyInstanceId) => TryGetEnemyUnit(enemyInstanceId, out int unit) && Kernel.IsAlive(unit);

        /// <summary>血量真相在记录里的敌人（首领）结算后回写镜像；也用于首领重置、记录被其它系统改写后。</summary>
        public void SyncEnemyFromRecord(RegionEnemyRecord rec)
        {
            if (rec != null && TryGetEnemyUnit(rec.EnemyInstanceId, out int unit))
            {
                Kernel.SetHealth(unit, rec.Health, rec.MaxHealth, rec.IsAlive);
            }
        }

        /// <summary>把内核里的敌人实时状态写回记录（位置、行为计时；内核扣血、仍存活的敌人还有血量）。
        ///
        /// 记录的“存活 → 阵亡”只由 Killed 事件翻转（<see cref="CombatSiteRules.OnEnemyKilled"/>：清除、掉落、击毁反馈），这里不翻：
        /// 每步玩法事件有上限（combat.events.gameplay_per_step），大量同时阵亡时一部分 Killed 还在队列里；若在存档写回时先把记录写成阵亡，
        /// 这些事件稍后（或读档后）结算时会被当成重复而跳过，掉落与反馈就丢了。队列随内核快照进存档，读档后照常结算；
        /// 地点卸载 / 撤离前由 <see cref="FlushPendingEvents"/> 排空。</summary>
        public void ExportEnemies(CampaignState state)
        {
            if (state?.RegionEnemies == null || IsDisposed)
            {
                return;
            }
            foreach (RegionEnemyRecord rec in state.RegionEnemies)
            {
                if (rec == null || !TryGetEnemyUnit(rec.EnemyInstanceId, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
                {
                    continue;
                }
                rec.Position = new Vector2((float)v.Position.x, (float)v.Position.y);
                rec.CycleCooldownRemaining = v.Cycle;
                rec.SecondaryTimer = v.Secondary;
                if ((v.Flags & CombatUnitFlags.ExternalHealth) == 0 && rec.IsAlive && v.Alive)
                {
                    rec.Health = v.Health;
                }
            }
        }

        /// <summary>内核队列里还没结算的玩法事件条数（大量同时阵亡时可能跨几步）。</summary>
        public int PendingGameplayEvents => IsDisposed ? 0 : Kernel.GameplayPending;

        /// <summary>
        /// 把积压的玩法事件全部结算（地点卸载 / 撤离前调用：内核随后释放，队列不能跟着丢）。每轮至多 <see cref="MaxEventsPerStep"/> 条，
        /// 与步内结算同一条路径；结算里产生的新事件也一并排空。只在卸载时发生，O(积压数)。返回处理的轮数。
        /// 存档时不调用：队列随快照进存档，保证“存档不改变结果”。
        /// </summary>
        public int FlushPendingEvents()
        {
            int rounds = 0;
            while (!IsDisposed && Kernel.GameplayPending > 0 && rounds < 100000)
            {
                ProcessEvents();
                rounds++;
            }
            return rounds;
        }

        /// <summary>敌人标记（Demo 的 MarkedEnemies / MarkedMachines 记录）写回：内核是真相，记录是存档与离线查询用的镜像。</summary>
        public void ExportMarks(RegionRecord region, double now)
        {
            if (region == null || IsDisposed)
            {
                return;
            }
            var enemyMarks = new List<MarkedEnemyRecord>();
            foreach (KeyValuePair<string, int> kv in _enemyUnit)
            {
                if (Kernel.TryGetUnit(kv.Value, out CombatUnitView v) && v.MarkedUntil > now && v.Alive)
                {
                    enemyMarks.Add(new MarkedEnemyRecord { EnemyInstanceId = kv.Key, ExpireAtPlaySeconds = (float)v.MarkedUntil });
                }
            }
            enemyMarks.Sort((a, b) => string.CompareOrdinal(a.EnemyInstanceId, b.EnemyInstanceId));
            region.MarkedEnemies = enemyMarks.ToArray();
            var machineMarks = new List<MarkedMachineRecord>();
            foreach (KeyValuePair<int, int> kv in _machineUnit)
            {
                if (Kernel.TryGetUnit(kv.Value, out CombatUnitView v) && v.MarkedUntil > now && v.Alive)
                {
                    machineMarks.Add(new MarkedMachineRecord { MachineLogicId = kv.Key, ExpireAtPlaySeconds = (float)v.MarkedUntil });
                }
            }
            machineMarks.Sort((a, b) => a.MachineLogicId.CompareTo(b.MachineLogicId));
            region.MarkedMachines = machineMarks.ToArray();
        }

        /// <summary>从记录导入标记（没有内核快照、按记录重建时）。</summary>
        public void ImportMarks(RegionRecord region)
        {
            if (region == null || IsDisposed)
            {
                return;
            }
            if (region.MarkedEnemies != null)
            {
                foreach (MarkedEnemyRecord m in region.MarkedEnemies)
                {
                    if (m != null && TryGetEnemyUnit(m.EnemyInstanceId, out int unit))
                    {
                        Kernel.SetMarkedUntil(unit, m.ExpireAtPlaySeconds);
                    }
                }
            }
            if (region.MarkedMachines != null)
            {
                foreach (MarkedMachineRecord m in region.MarkedMachines)
                {
                    if (m != null && _machineUnit.TryGetValue(m.MachineLogicId, out int unit))
                    {
                        Kernel.SetMarkedUntil(unit, m.ExpireAtPlaySeconds);
                    }
                }
            }
        }

        // ─────────────────────────────── 寻路（FG0-ARCH-06）───────────────────────────────

        /// <summary>本地点在星球格网上、移动走层级寻路（家园）。</summary>
        public bool NavEnabled => !IsDisposed && Kernel.Config.NavEnabled != 0;
        public bool NavBound => !IsDisposed && Kernel.NavBound;

        /// <summary>绑定寻路内核的镜像（碰撞、路线失效检查读它）。</summary>
        public void BindNav(NavKernel nav)
        {
            if (!IsDisposed && nav != null && !nav.IsDisposed)
            {
                Kernel.BindNav(nav.Mirror);
            }
        }

        public void UnbindNav()
        {
            if (!IsDisposed)
            {
                Kernel.UnbindNav();
            }
        }

        /// <summary>寻路内核把本批结果交给本地点的单位（AOT，O(1) 次调用）。</summary>
        public int DeliverRoutes(NavKernel nav, int ownerTag) => IsDisposed || nav == null ? 0 : nav.DeliverCombat(Kernel, ownerTag);

        /// <summary>本步新发出的寻路请求整体转交寻路内核（AOT，O(1) 次调用）。</summary>
        public int CollectNavRequests(NavKernel nav, int ownerTag, long tick) => IsDisposed || nav == null ? 0 : nav.CollectFromCombat(Kernel, ownerTag, tick);

        /// <summary>地形变化后：剩余路线被挡的单位重新要路线。</summary>
        public int InvalidateBlockedRoutes() => IsDisposed ? 0 : Kernel.InvalidateBlockedRoutes();

        /// <summary>寻路快照读不了时：所有在等路线的单位重新发请求（不会永远等下去）。</summary>
        public int ReissueAwaitingRoutes() => IsDisposed ? 0 : Kernel.ReissueAwaiting();

        public bool TryGetNavState(int unitId, out CombatNavState state, out NavFailReason fail)
        {
            state = CombatNavState.None;
            fail = NavFailReason.None;
            return !IsDisposed && Kernel.TryGetNavState(unitId, out state, out fail);
        }

        /// <summary>沿路线剩余的长度（米）；不在沿路线走返回 -1（赶路看门狗按它判断“有没有在推进”，绕路时直线距离会变大）。</summary>
        public double RemainingRoute(int unitId) => IsDisposed ? -1 : Kernel.RemainingRoute(unitId);

        /// <summary>剩余路线（从下一个路点起，末项 = 终点），画路线指示用。</summary>
        public int CopyRoute(int unitId, List<double2> into)
        {
            if (IsDisposed)
            {
                into.Clear();
                return 0;
            }
            return Kernel.CopyRoute(unitId, into);
        }

        // ─────────────────────────────── 步与事件 ───────────────────────────────

        /// <summary>一个模拟步：内核一步 + 排空事件。<paramref name="time"/> = 这一步开始时的游戏秒。</summary>
        public void Step(float dt, double time)
        {
            if (IsDisposed)
            {
                return;
            }
            if (ReactionRevision != NamedReactionCatalog.Revision)
            {
                ConfigureReactions();
            }
            Kernel.Step(dt, time);
            ProcessEvents();
            // FG2-FW-04：反应反馈与伤害归因（按内核累计计数的增量，O(反应条数)；与是否观察无关的部分照常结算）。
            ReactionFeedback.Process(this, CampaignSession.Current);
        }

        /// <summary>FG2-FW-04：反馈进给的基线（<see cref="ReactionFeedback"/> 专用）。</summary>
        public ReactionFeedCursor FeedCursor { get; } = new ReactionFeedCursor();

        /// <summary>FG2-FW-04：具名敌人的类型内容 ID（反应日志显示敌人名）；查不到返回 null。</summary>
        public string EnemyTypeOf(CampaignState state, string enemyInstanceId)
        {
            if (!TryGetEnemyUnit(enemyInstanceId, out int unit))
            {
                return null;
            }
            return FindEnemyRecord(state, unit)?.EnemyTypeId;
        }

        /// <summary>
        /// 排空至多 <see cref="MaxEventsPerStep"/> 条玩法事件与本批提示事件，按序号合并后逐条结算。即时开火之后也调用。
        /// FG6-DEF-01 审查修复（P0：玩法事件永不丢弃，CombatTypes 玩法事件约定）：事件结算里的规则回调可能再触发一次即时开火 / 伤害
        /// （<see cref="TryFireAtEnemy"/> / <see cref="TryDamageEnemy"/> / 炮塔开火都会接着调本方法）。重入时不再在这里排空——
        /// 那会清掉外层正在遍历的同一批（<see cref="_merge"/>），外层后面的事件丢失、内层的事件被处理两次；改为记下“还有新事件”，
        /// 由外层这一批处理完后再排一遍（至多 <see cref="MaxDrainPasses"/> 遍）。自动交战请求（<see cref="CombatEventKind.EngageRequest"/>）
        /// 本身要同步开火并当场拿到命中结果，所以延后到这一轮全部处理完、不在遍历中时再交给规则（<see cref="RunDeferredEngage"/>）。
        /// </summary>
        public void ProcessEvents()
        {
            if (IsDisposed)
            {
                return;
            }
            if (_processing)
            {
                _drainAgain = true;
                return;
            }
            _processing = true;
            LastEventsProcessed = 0;
            LastEventsMs = 0;
            try
            {
                int passes = 0;
                do
                {
                    _drainAgain = false;
                    ProcessBatch();
                    passes++;
                }
                while (_drainAgain && !IsDisposed && passes < MaxDrainPasses);
                LastDrainPasses = passes;
            }
            finally
            {
                _processing = false;
            }
            RunDeferredEngage();
        }

        /// <summary>一次排空里最多再排几遍（重入记下的新事件；超过的留给下一次排空，不丢）。</summary>
        private const int MaxDrainPasses = 4;

        private bool _processing;
        private bool _drainAgain;
        private bool _runningDeferred;
        private readonly List<int> _deferredEngage = new List<int>(4);
        private readonly List<int> _engageScratch = new List<int>(4);

        /// <summary>自检读：最近一次排空走了几遍（重入时 &gt; 1）。</summary>
        public int LastDrainPasses { get; private set; }

        /// <summary>自检读：累计延后结算的自动交战请求数。</summary>
        public long DeferredEngageCount { get; private set; }

        /// <summary>
        /// 延后的自动交战请求：这一轮事件全部处理完后逐条交给规则（规则里同步开火 → 本方法外的 <see cref="ProcessEvents"/> 正常排空、拿到命中结果）。
        /// 结算中又产生的请求（极少）接着处理，至多 <see cref="MaxDrainPasses"/> 轮，剩下的留到下一次排空。O(请求数)。
        /// </summary>
        private void RunDeferredEngage()
        {
            if (_runningDeferred || _deferredEngage.Count == 0)
            {
                return;
            }
            _runningDeferred = true;
            try
            {
                int rounds = 0;
                while (_deferredEngage.Count > 0 && !IsDisposed && rounds++ < MaxDrainPasses)
                {
                    _engageScratch.Clear();
                    _engageScratch.AddRange(_deferredEngage);
                    _deferredEngage.Clear();
                    for (int i = 0; i < _engageScratch.Count; i++)
                    {
                        try
                        {
                            Rules?.OnEngageRequest(this, _engageScratch[i]);
                        }
                        catch (Exception e)
                        {
                            Log.Error($"[CombatSite] {SiteId} 结算自动交战（机器 {_engageScratch[i]}）异常：{e}");
                        }
                    }
                }
            }
            finally
            {
                _runningDeferred = false;
            }
        }

        private void ProcessBatch()
        {
            _watch.Restart();
            CombatEvent[] drained = Kernel.DrainGameplay(MaxEventsPerStep, out int count);
            _merge.Clear();
            for (int i = 0; i < count; i++)
            {
                _merge.Add(drained[i]);
            }
            Unity.Collections.NativeArray<CombatEvent> cues = Kernel.Cues;
            for (int i = 0; i < cues.Length; i++)
            {
                _merge.Add(cues[i]);
            }
            Kernel.ClearCues();
            if (_merge.Count > 1)
            {
                _merge.Sort(BySeq);
            }
            CampaignState state = CampaignSession.Current;
            Action<CombatSite, CombatEvent> observer = EventObserver;
            for (int i = 0; i < _merge.Count; i++)
            {
                try
                {
                    observer?.Invoke(this, _merge[i]);
                    Handle(state, _merge[i]);
                }
                catch (Exception e)
                {
                    Log.Error($"[CombatSite] {SiteId} 处理内核事件 {_merge[i].Kind} 异常：{e}");
                }
            }
            LastEventsProcessed += _merge.Count;
            TotalEventsProcessed += _merge.Count;
            _watch.Stop();
            LastEventsMs += _watch.Elapsed.TotalMilliseconds;
        }

        // 敌人实例 ID → 记录数组下标。记录数组整体替换（首领初始化、召唤、读档）时按引用失效重建；
        // 命中时再核对该下标上的对象确实是这个 ID（数组被原位改写也不会读到旧对象）。
        private RegionEnemyRecord[] _enemyIndexArray;
        private readonly Dictionary<string, int> _enemyIndex = new Dictionary<string, int>();

        /// <summary>内核单位 → 敌人记录。每条事件 O(1)（FG0-ARCH-03：热更层开销与敌人数无关，只在记录数组变化后重建一次索引）。</summary>
        private RegionEnemyRecord FindEnemyRecord(CampaignState state, int unitId)
        {
            RegionEnemyRecord[] all = state?.RegionEnemies;
            if (all == null || !_unitEnemy.TryGetValue(unitId, out string id))
            {
                return null;
            }
            if (!ReferenceEquals(_enemyIndexArray, all))
            {
                RebuildEnemyIndex(all);
            }
            if (TryIndexedEnemy(all, id, out RegionEnemyRecord found))
            {
                return found;
            }
            RebuildEnemyIndex(all); // 数组被原位改写过：重建一次再查。
            return TryIndexedEnemy(all, id, out found) ? found : null;
        }

        private bool TryIndexedEnemy(RegionEnemyRecord[] all, string id, out RegionEnemyRecord record)
        {
            record = null;
            if (!_enemyIndex.TryGetValue(id, out int i) || i < 0 || i >= all.Length)
            {
                return false;
            }
            RegionEnemyRecord r = all[i];
            if (r == null || r.EnemyInstanceId != id)
            {
                return false;
            }
            record = r;
            return true;
        }

        private void RebuildEnemyIndex(RegionEnemyRecord[] all)
        {
            _enemyIndex.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                RegionEnemyRecord r = all[i];
                if (r != null && !string.IsNullOrEmpty(r.EnemyInstanceId) && !_enemyIndex.ContainsKey(r.EnemyInstanceId))
                {
                    _enemyIndex.Add(r.EnemyInstanceId, i); // 与原线性扫描一致：重复 ID 取第一个。
                }
            }
            _enemyIndexArray = all;
        }

        private void Handle(CampaignState state, in CombatEvent e)
        {
            // FG6-DEF-01：炮塔的阵亡 / 击毁 / 具名反应发动交给炮塔服务（CombatSite.Turrets）。
            if (TryHandleTurretEvent(e))
            {
                return;
            }
            switch (e.Kind)
            {
                case CombatEventKind.Killed:
                {
                    if (_unitMachine.ContainsKey(e.Unit))
                    {
                        return; // 机器阵亡由记录（MachineRegistry.MarkDeadByLogicId）驱动，这里只是内核的回声。
                    }
                    // 记录的 IsAlive = “阵亡是否已结算”（只有这里与未载入时的记录路径会翻它），据此保证每个敌人的阵亡副作用恰好一次。
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec == null && _unitEnemy.TryGetValue(e.Unit, out string killedKey)
                        && Kernel.TryGetUnit(e.Unit, out CombatUnitView kv) && (kv.Flags & CombatUnitFlags.ExternalHealth) == 0)
                    {
                        // FG6-DEF-01（DEBT-FG2FW02-07）：血量在内核的具名目标（家园训练靶）被打空。
                        Rules?.OnKeyedTargetDamaged(this, killedKey, 0f, 0f, _unitMachine.TryGetValue(e.Other, out int keyedKiller) ? keyedKiller : 0, true);
                        return;
                    }
                    if (rec == null || !rec.IsAlive || !Kernel.TryGetUnit(e.Unit, out CombatUnitView v) || (v.Flags & CombatUnitFlags.ExternalHealth) != 0)
                    {
                        return;
                    }
                    rec.Health = 0f;
                    rec.IsAlive = false;
                    rec.Position = new Vector2((float)e.Pos.x, (float)e.Pos.y);
                    // FG4-ECO-07（FGR-ECO-041 机器详情“击杀”）：内核的阵亡事件带着致命一击的来源单位（Other）；来源是己方机器时记一次击杀。O(1)。
                    if (_unitMachine.TryGetValue(e.Other, out int killerLogicId))
                    {
                        MachineRegistry.RecordKill(killerLogicId);
                    }
                    Rules?.OnEnemyKilled(this, rec);
                    return;
                }
                case CombatEventKind.Damaged:
                {
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec == null)
                    {
                        if (_unitEnemy.TryGetValue(e.Unit, out string damagedKey))
                        {
                            // FG6-DEF-01（DEBT-FG2FW02-07）：血量在内核的具名目标（家园训练靶）受伤——伤害已按载体与读法结算，交回记录。
                            Rules?.OnKeyedTargetDamaged(this, damagedKey, e.Value, e.Value2, _unitMachine.TryGetValue(e.Other, out int keyedAttacker) ? keyedAttacker : 0, false);
                        }
                        return;
                    }
                    rec.Health = e.Value2;
                    if (e.Value2 > 0f)
                    {
                        rec.Position = new Vector2((float)e.Pos.x, (float)e.Pos.y);
                        Rules?.OnEnemyDamaged(this, rec, e.Value);
                    }
                    return;
                }
                case CombatEventKind.Healed:
                {
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec != null)
                    {
                        rec.Health = e.Value2;
                    }
                    return;
                }
                case CombatEventKind.DamageRequest:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        // Demo TryEnemyAttackMachine 的校验照旧：只结算仍在本地点、玩家阵营的存活机器（已被派走 / 已切场的旧单位不许“隔空打击”）。
                        if (MachineRegistry.TryGetRecord(logicId, out MachineRecord mrec) && mrec.IsAlive && mrec.RegionId == SiteId
                            && mrec.FactionId == MachinePlayerFaction)
                        {
                            MachineRegistry.ApplyDamage(logicId, e.Value);
                            SyncMachineHealth(mrec);
                        }
                        return;
                    }
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec != null && Rules != null)
                    {
                        Rules.ApplyExternalEnemyDamage(this, rec, e.Value);
                        SyncEnemyFromRecord(rec);
                    }
                    else if (rec == null && Rules != null && _unitEnemy.TryGetValue(e.Unit, out string key))
                    {
                        Rules.ApplyExternalDamageByKey(this, key, e.Value, _unitMachine.TryGetValue(e.Other, out int attackerLogicId) ? attackerLogicId : 0);
                    }
                    return;
                }
                case CombatEventKind.HealRequest:
                {
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec != null && Rules != null)
                    {
                        Rules.ApplyExternalEnemyHeal(this, rec, e.Value);
                        SyncEnemyFromRecord(rec);
                    }
                    return;
                }
                case CombatEventKind.CommandEnded:
                case CombatEventKind.CommandStuckStrike:
                case CombatEventKind.AttackOutcome:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        string hostile = e.Kind == CombatEventKind.AttackOutcome || e.Kind == CombatEventKind.CommandEnded
                            ? (_unitEnemy.TryGetValue(e.Other, out string h) ? h : null)
                            : null;
                        Squad?.OnKernelEvent(e, logicId, hostile, this);
                    }
                    return;
                }
                case CombatEventKind.WorkArrived:
                {
                    if (_markerByUnit.TryGetValue(e.Unit, out HomeValleyMachineMarker marker))
                    {
                        marker.OnWorkArrived();
                    }
                    return;
                }
                case CombatEventKind.WorkBlocked:
                {
                    // FG0-ARCH-06：工作地点无法到达——到达回调作废，交给地点规则（工单转等待 + 带原因的通知），机器不会原地发呆等看门狗。
                    if (_markerByUnit.TryGetValue(e.Unit, out HomeValleyMachineMarker marker))
                    {
                        marker.OnWorkBlocked();
                    }
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        Rules?.OnWorkBlocked(this, logicId, (NavFailReason)e.Code, new Vector2((float)e.Pos.x, (float)e.Pos.y));
                    }
                    return;
                }
                case CombatEventKind.MachineMarked:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        Rules?.OnMachineMarked(this, logicId, e.Value, FindEnemyRecord(state, e.Other));
                    }
                    return;
                }
                case CombatEventKind.MarkCleared:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        Rules?.OnMarkCleared(this, logicId, FindEnemyRecord(state, e.Other));
                    }
                    return;
                }
                case CombatEventKind.MarkMissed:
                    Rules?.OnMarkMissed(this, FindEnemyRecord(state, e.Unit));
                    return;
                case CombatEventKind.PoiReached:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        Vector2 p = TryGetMachinePosition(logicId, out Vector2 mp) ? mp : Vector2.zero;
                        Rules?.OnPoiReached(this, e.Other, logicId, p);
                    }
                    return;
                }
                case CombatEventKind.EngageRequest:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId) && Rules != null)
                    {
                        // FG6-DEF-01 审查修复（P0）：规则会同步开火（训练靶走内核开火入口）——延后到这一轮事件处理完再结算，不在遍历中重入排空。
                        _deferredEngage.Add(logicId);
                        DeferredEngageCount++;
                    }
                    return;
                }
                case CombatEventKind.RawFirmwareFired:
                {
                    // FG1-SIG-06（FGR-SIG-061）：信号裸跑的未破解常规固件发动一次——暴露按它结算（不丢的玩法事件）。
                    if (_unitMachine.TryGetValue(e.Unit, out int rawLogicId) && state != null)
                    {
                        RawFirmwareService.OnRawFired(state, rawLogicId);
                    }
                    return;
                }
                case CombatEventKind.ReactionFired:
                {
                    // FG1-SIG-03：反应发动走不丢的玩法事件（同名提示事件只管声音 / 特效，打满每步上限时可以丢，冷却不能跟着丢）。
                    string reactionId = ReactionIdOf((CombatReaction)e.Code);
                    if (reactionId != null)
                    {
                        NotifyReactionFired(state, e.Unit, reactionId, e.Code2 != 0);
                        // FG2-FW-04：装配反应（标记跳转 / 熔穿过载）同样进反应日志、伤害归因的次数与首次触发（慢放 / 图鉴 / 弹字）。
                        ReactionFeedback.OnAssemblyReaction(this, state, reactionId, e.Unit, e.Other, new Vector2((float)e.Pos.x, (float)e.Pos.y));
                    }
                    return;
                }
                default:
                    HandleCue(state, e);
                    return;
            }
        }

        /// <summary>提示事件 → 反馈（声音 / 字幕 / 特效）。映射与 Demo 各结算点原来的 FeedbackCues 调用一一对应。</summary>
        private void HandleCue(CampaignState state, in CombatEvent e)
        {
            var at = new Vector2((float)e.Pos.x, (float)e.Pos.y);
            switch (e.Kind)
            {
                case CombatEventKind.Fired:
                {
                    string primary = _unitMachine.TryGetValue(e.Unit, out int logicId) && _machineWeapons.TryGetValue(logicId, out MachineWeaponInfo info)
                        ? info.PrimaryId : null;
                    FeedbackCues.RaiseAt(FeedbackCueId.WeaponFire, at, null, primary != null ? FeedbackCues.ContentSfx(primary) : null);
                    return;
                }
                case CombatEventKind.CannonCharge:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId) && MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
                    {
                        FeedbackCues.Raise(FeedbackCueId.CannonCharge, MachineNaming.Short(rec));
                    }
                    return;
                }
                case CombatEventKind.CannonFire:
                {
                    string primary = _unitMachine.TryGetValue(e.Unit, out int logicId) && _machineWeapons.TryGetValue(logicId, out MachineWeaponInfo info)
                        ? info.PrimaryId : null;
                    FeedbackCues.RaiseAt(FeedbackCueId.CannonFire, at, null, primary != null ? FeedbackCues.ContentSfx(primary) : null);
                    return;
                }
                case CombatEventKind.MeltOverload:
                    FeedbackCues.RaiseAt(FeedbackCueId.ReactionMeltOverload, at, "穿甲强化，热量额外上升",
                        FeedbackCues.ContentSfx(MechanicalReactionCatalog.ReactionMeltOverloadId));
                    return;
                case CombatEventKind.Overheat:
                {
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId) && MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
                    {
                        Log.Info($"[CombatSite] 机器 {logicId} 武器过热（{e.Value:F0}），停火直到降到 {FracturedCityLayout.WeaponHeatRecoverThreshold:F0} 以下。");
                        // DEBT-FG1SIG06-02：即时命中武器也会过热，提示不再写死“重炮”，走文本键。
                        FeedbackCues.Raise(FeedbackCueId.WeaponOverheat, GameText.Format("combat.overheat.notice", MachineNaming.Short(rec),
                            FracturedCityLayout.WeaponHeatRecoverThreshold.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    return;
                }
                case CombatEventKind.ArmorHit:
                    FeedbackCues.RaiseAt(FeedbackCueId.ArmorHit, at);
                    return;
                case CombatEventKind.TagReaction:
                {
                    // FG2-FW-03（FGR-FW-041 / 042）：开放命名的反应报出机械名；未开放的照样生效，只是不显示名字。
                    string rid = NamedReactionCatalog.IdOfRule(e.Code);
                    bool named = rid != null && NamedReactionCatalog.IsNamed(state, rid);
                    LastTagReactionId = rid;
                    LastTagReactionNamed = named;
                    TagReactionCuesHandled++;
                    if (named)
                    {
                        // FG2-FW-04：音效 / 声音字幕 / 弹字改由 ReactionFeedback 按内核计数的增量每步聚合发出（提示事件超上限会丢，计数不会）；这里只留引导钩子。
                        GuidanceHooks.Raise(GuidanceHooks.ReactionFirstNamed);
                    }
                    return;
                }
                case CombatEventKind.MarkJump:
                    FeedbackCues.RaiseAt(FeedbackCueId.ReactionMarkJump, at, $"跳转 {(int)e.Value} 个目标",
                        FeedbackCues.ContentSfx(MechanicalReactionCatalog.ReactionMarkJumpId));
                    return;
                case CombatEventKind.EnemyFired:
                {
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec != null)
                    {
                        FeedbackCues.RaiseAt(FeedbackCueId.EnemyAttack, at, null, FeedbackCues.ContentSfx(rec.EnemyTypeId));
                    }
                    return;
                }
                case CombatEventKind.Telegraph:
                {
                    string who = _unitEnemy.TryGetValue(e.Unit, out string id) ? id : e.Unit.ToString();
                    Log.Info(e.Code == 0 ? $"[CombatSite] {who} 发现目标，进入瞄准线。"
                        : e.Code == 1 ? $"[CombatSite] {who} 瞄准线结束，但目标已脱离射程 / 视线，本次落空。"
                        : $"[CombatSite] {who} 瞄准线结束，开火。");
                    return;
                }
            }
        }

        /// <summary>FG1-SIG-03（FGR-SIG-033）：己方机器打出了一条反应（玩法事件 <see cref="CombatEventKind.ReactionFired"/>）——交给信号接入服务判断是不是
        /// 信号带进来的核心固件发动（是就开始冷却，冷却记在信号上）。<paramref name="gatedAtFire"/> = 发动那一刻内核认定它是门控反应。O(1)。</summary>
        private void NotifyReactionFired(CampaignState state, int unit, string reactionId, bool gatedAtFire)
        {
            if (state != null && _unitMachine.TryGetValue(unit, out int logicId))
            {
                SignalUplinkService.OnReactionFired(state, logicId, reactionId, gatedAtFire);
            }
        }

        /// <summary>内核反应码 → 反应内容 ID（与 <see cref="MachineWeaponFrom(BlueprintCircuitPreview, bool)"/> 的翻译互逆）。</summary>
        public static string ReactionIdOf(CombatReaction r) => r switch
        {
            CombatReaction.MeltOverload => MechanicalReactionCatalog.ReactionMeltOverloadId,
            CombatReaction.MarkJump => MechanicalReactionCatalog.ReactionMarkJumpId,
            _ => null,
        };

        // ─────────────────────────────── 开火 ───────────────────────────────

        /// <summary>己方机器对具名敌人开一次火（直控点击的正式入口；编队攻击命令在内核里走同一段规则）。
        /// 返回 Demo 语义：Success（含“仍在瞄准”）/ 失败原因文本。</summary>
        public bool TryFireAtEnemy(int attackerLogicId, string enemyInstanceId, out CombatFireResult result, out string reason)
        {
            reason = null;
            if (IsDisposed || !_machineUnit.TryGetValue(attackerLogicId, out int attacker))
            {
                result = CombatFireResult.NoAttacker;
                reason = FireReason(result, attackerLogicId, enemyInstanceId);
                return false;
            }
            if (!TryGetEnemyUnit(enemyInstanceId, out int target))
            {
                result = CombatFireResult.TargetMissing;
                reason = FireReason(result, attackerLogicId, enemyInstanceId);
                return false;
            }
            result = Kernel.FireAt(attacker, target, GameClock.GameSeconds);
            ProcessEvents();
            bool ok = result == CombatFireResult.Ok || result == CombatFireResult.StillAiming;
            if (!ok)
            {
                reason = FireReason(result, attackerLogicId, enemyInstanceId);
            }
            return ok;
        }

        /// <summary>开火结果 → 玩家可读原因（文本键；装配解析失败沿用解析器给出的原因）。</summary>
        public string FireReason(CombatFireResult r, int attackerLogicId, string enemyInstanceId)
        {
            switch (r)
            {
                case CombatFireResult.NoAttacker:
                case CombatFireResult.AttackerDead:
                    return GameText.Get("combat.fire.no_attacker");
                case CombatFireResult.NoWeapon:
                    return _machineWeapons.TryGetValue(attackerLogicId, out MachineWeaponInfo info) && !string.IsNullOrEmpty(info.FailureReason)
                        ? info.FailureReason : GameText.Get("combat.fire.no_weapon");
                case CombatFireResult.NoCombatOutput:
                    return GameText.Get("combat.fire.no_output");
                case CombatFireResult.Overheated:
                    return GameText.Format("combat.fire.overheated", FracturedCityLayout.WeaponHeatRecoverThreshold.ToString("0"));
                case CombatFireResult.Cooldown:
                    return GameText.Get("combat.fire.cooldown");
                case CombatFireResult.TargetDead:
                    return GameText.Get("combat.fire.target_dead");
                case CombatFireResult.TargetMissing:
                    return GameText.Get("combat.fire.target_missing");
                case CombatFireResult.OutOfRange:
                    return GameText.Get("combat.fire.out_of_range");
                case CombatFireResult.NotHostile:
                    return GameText.Get("combat.fire.not_hostile");
                case CombatFireResult.Invulnerable:
                {
                    RegionEnemyRecord rec = null;
                    CampaignState state = CampaignSession.Current;
                    if (state?.RegionEnemies != null)
                    {
                        foreach (RegionEnemyRecord x in state.RegionEnemies)
                        {
                            if (x != null && x.EnemyInstanceId == enemyInstanceId)
                            {
                                rec = x;
                                break;
                            }
                        }
                    }
                    return GameText.Format("combat.fire.invulnerable", Rules != null && rec != null ? Rules.InvulnerableDetail(rec) : string.Empty);
                }
                case CombatFireResult.NoAmmo:
                    return GameText.Get("turret.fire.no_ammo"); // FG6-DEF-01：每发要补给的武器（炮塔的流体类固件）补给不够一发
                default:
                    return string.Empty;
            }
        }

        // ─────────────────────────────── 门面：热更层玩法代码只经这里碰内核 ───────────────────────────────
        // FG14 §5 硬约束 3：热更层碰内核的唯一入口是桥接层——战斗内核的桥接层就是 Campaign/Combat/（CombatSite / CombatSites / CombatBench），
        // 对应细胞阶段的 SimBridge、传送带的 BeltNetworkService。控制器、区域规则、编队命令、机器句柄只调下面这些方法；
        // Kernel 属性只留给 Editor 自检与性能探针做白盒断言，“战斗内核”自检的扫描段守护这条边界。

        /// <summary>本地点内核里存活的己方机器数（全灭检测、画面对账；O(1) 次调用）。</summary>
        public int CountAliveMachines() => IsDisposed ? 0 : Kernel.CountAlive(CombatFaction.Player, CombatUnitKind.Machine);

        /// <summary>存活己方机器的中心（“飞到远征地点”的落点）。</summary>
        public bool TryMachineCentroid(out Vector2 centroid)
        {
            if (!IsDisposed && Kernel.TryCentroid(CombatFaction.Player, CombatUnitKind.Machine, out double2 c))
            {
                centroid = new Vector2((float)c.x, (float)c.y);
                return true;
            }
            centroid = default;
            return false;
        }

        /// <summary>是否有存活的己方机器越过 y 线（铸造前哨核心分区封锁线）。</summary>
        public bool AnyMachineBeyondY(double y) => !IsDisposed && Kernel.AnyAliveBeyondY(CombatFaction.Player, CombatUnitKind.Machine, y);

        /// <summary>直控移动的钳制线（铸造前哨两条封锁线；每帧写两个数）。</summary>
        public void SetDirectClamp(double minY, double maxY)
        {
            if (!IsDisposed)
            {
                Kernel.SetDirectClamp(minY, maxY);
            }
        }

        /// <summary>点选：离点击点最近的存活敌方单位对应的具名敌人（并列取靠后者，同 Demo）。</summary>
        public bool TryFindNearestEnemy(Vector2 point, float radius, out string enemyInstanceId)
        {
            enemyInstanceId = null;
            if (IsDisposed)
            {
                return false;
            }
            int unit = Kernel.FindNearest(new double2(point.x, point.y), radius, CombatFaction.Hostile);
            return unit > 0 && _unitEnemy.TryGetValue(unit, out enemyInstanceId);
        }

        /// <summary>直控瞄准：锥形范围内第一个存活敌方单位对应的具名敌人（逐单位扫描在内核）。</summary>
        public bool TryFindEnemyInCone(Vector2 origin, Vector2 aimDirection, float range, float halfAngleDeg, out string enemyInstanceId)
        {
            enemyInstanceId = null;
            if (IsDisposed)
            {
                return false;
            }
            int unit = Kernel.FindFirstInCone(new double2(origin.x, origin.y), new float2(aimDirection.x, aimDirection.y), range, halfAngleDeg,
                CombatFaction.Hostile);
            return unit > 0 && _unitEnemy.TryGetValue(unit, out enemyInstanceId);
        }

        /// <summary>障碍（锚点净空圈：x, y, 半径）。进场时写一次。</summary>
        public void SetObstacles(IReadOnlyList<float3> obstacles)
        {
            if (!IsDisposed)
            {
                Kernel.SetObstacles(obstacles);
            }
        }

        /// <summary>兴趣点（x, y, 发现半径）与已发现标记。进场时写一次（读档从快照恢复）。</summary>
        public void SetPois(IReadOnlyList<float3> pois, IReadOnlyList<bool> alreadyReached)
        {
            if (!IsDisposed)
            {
                Kernel.SetPois(pois, alreadyReached);
            }
        }

        /// <summary>对内核扣血的具名敌人造成伤害并立即结算事件（受伤 / 阵亡 → 记录、掉落、反馈）。
        /// 返回 false = 这个敌人不在本地点的内核里（调用方走记录路径）。</summary>
        public bool TryDamageEnemy(string enemyInstanceId, float damage)
        {
            if (IsDisposed || !TryGetEnemyUnit(enemyInstanceId, out int unit))
            {
                return false;
            }
            Kernel.Damage(unit, Mathf.Max(0f, damage), 0);
            ProcessEvents();
            return true;
        }

        /// <summary>自动交战的目标、射程与间隔（家园训练靶）。</summary>
        public void SetEngage(int targetUnitId, float range, float interval)
        {
            if (!IsDisposed)
            {
                Kernel.SetEngage(targetUnitId, range, interval);
            }
        }

        /// <summary>血量真相在热更层的单位（训练靶）回写镜像。</summary>
        public void SetUnitHealth(int unitId, float health, float maxHealth, bool alive)
        {
            if (!IsDisposed)
            {
                Kernel.SetHealth(unitId, health, maxHealth, alive);
            }
        }

        public bool UnitExists(int unitId) => !IsDisposed && Kernel.Exists(unitId);

        public bool UnitHasFlag(int unitId, CombatUnitFlags flag) =>
            !IsDisposed && Kernel.TryGetUnit(unitId, out CombatUnitView v) && (v.Flags & flag) == flag;

        public void SetUnitFlag(int unitId, CombatUnitFlags flag, bool on)
        {
            if (!IsDisposed)
            {
                Kernel.SetFlag(unitId, flag, on);
            }
        }

        /// <summary>侧后命中加成（主核心阶段二 +20%）。</summary>
        public void SetBackHitBonus(int unitId, float bonus)
        {
            if (!IsDisposed)
            {
                Kernel.SetBackHitBonus(unitId, bonus);
            }
        }

        /// <summary>下达编队命令（移动 / 攻击 / 守备 / 撤退；守备传 NaN 位置 = 守在当前位置）。</summary>
        public bool IssueCommand(int unitId, CombatCommandKind kind, Vector2 position, int targetUnitId, float arrive, float attackRange,
            float attackCooldown, bool pending) =>
            !IsDisposed && Kernel.IssueCommand(unitId, kind, new double2(position.x, position.y), targetUnitId, arrive, attackRange, attackCooldown, pending);

        public bool TryGetCommand(int unitId, out CombatCommand command)
        {
            command = default;
            return !IsDisposed && Kernel.TryGetCommand(unitId, out command);
        }

        public bool ClearCommand(int unitId) => !IsDisposed && Kernel.ClearCommand(unitId);

        /// <summary>直控移动输入（每帧由被观察的地点写入；零向量 = 停）。</summary>
        public void SetDirectInput(int unitId, Vector2 direction)
        {
            if (!IsDisposed)
            {
                Kernel.SetDirectInput(unitId, new float2(direction.x, direction.y));
            }
        }

        // ─────────────────────────────── 画面（只在被观察时）───────────────────────────────

        public void BindView(int unitId, Transform transform, float height)
        {
            Sync?.Bind(unitId, transform, height);
        }

        /// <summary>FG6-DEF-01 审查修复：绑定一个按内核朝向转动的表现对象（炮塔头：位置 + 炮口朝向都由 <see cref="FrameRender"/> 的 Burst 作业写入）。</summary>
        public void BindOrientedView(int unitId, Transform transform, float height)
        {
            Sync?.Bind(unitId, transform, height, true);
        }

        /// <summary>画面同步里绑定了几个表现对象（炮塔头据此发现“绑定被整体清掉了”需要重绑；O(1)）。</summary>
        public int ViewBindingCount => Sync?.Count ?? 0;

        public void UnbindView(int unitId) => Sync?.Unbind(unitId);

        public void ClearViews() => Sync?.Clear();

        /// <summary>每帧（被观察时）：插值同步表现对象，实例化绘制突袭者 / 炮塔 / 弹体。两次常数调用。</summary>
        public void FrameRender(Camera camera, float alpha)
        {
            if (IsDisposed)
            {
                return;
            }
            Sync?.Apply(Kernel, alpha, double2.zero);
            if (Renderer == null)
            {
                Renderer = new CombatRenderer();
                Renderer.Hologram = HologramRender;
                // FG2-FW-03（FGR-FW-031）：头顶状态标签图标的形状 / 颜色来自 fg.TbStatusTag。
                Renderer.SetStatusVisuals(NamedReactionCatalog.BuildStatusVisuals());
            }
            Renderer.Draw(Kernel, camera, alpha, double2.zero, _unitHeight);
            if (!_statusTagHookRaised && Renderer.LastIconInstances > 0)
            {
                _statusTagHookRaised = true;
                GuidanceHooks.Raise(GuidanceHooks.StatusTagFirstSeen);
            }
            // FG2-FW-03（FGR-FW-031）：光标下带标签的单位 → 悬停读数（名称 / 剩余时间 / 叠层）。
            StatusTagHover.Tick(this, camera);
        }

        /// <summary>离开观察：释放 GPU 资源（表现对象由地点自己销毁）。</summary>
        public void ReleaseRender()
        {
            StatusTagHover.Leave(this);
            Sync?.Clear();
            Renderer?.Dispose();
            Renderer = null;
        }

        // ─────────────────────────────── 存档 ───────────────────────────────

        public CombatSiteRecord Snapshot()
        {
            if (IsDisposed)
            {
                return null;
            }
            byte[] bytes = Kernel.Serialize();
            return new CombatSiteRecord
            {
                SiteId = SiteId,
                FormatVersion = CombatConst.FormatVersion,
                KernelSteps = Kernel.Steps,
                Units = Kernel.SlotCount,
                Projectiles = Kernel.ProjectileCount,
                Payload = Convert.ToBase64String(bytes),
                Keys = _keys.ToArray(),
            };
        }

        /// <summary>从快照恢复内核与映射。失败时内核保持空，返回原因文本键（调用方按记录重建并发通知）。
        /// 恢复后调用方负责与记录对账（记录是机器 / 敌人存在性的真相）。</summary>
        public bool TryRestore(CampaignState state, CombatSiteRecord rec, out string reasonKey)
        {
            reasonKey = null;
            if (rec == null || string.IsNullOrEmpty(rec.Payload))
            {
                reasonKey = "combat.load.reason.bad_payload";
                return false;
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(rec.Payload);
            }
            catch (FormatException)
            {
                reasonKey = "combat.load.reason.bad_payload";
                return false;
            }
            CombatLoadResult r = Kernel.Load(bytes);
            if (r != CombatLoadResult.Ok)
            {
                reasonKey = r switch
                {
                    CombatLoadResult.BadMagic => "combat.load.reason.bad_magic",
                    CombatLoadResult.UnknownFormat => "combat.load.reason.unknown_format",
                    CombatLoadResult.BadChecksum => "combat.load.reason.bad_checksum",
                    CombatLoadResult.Truncated => "combat.load.reason.truncated",
                    _ => "combat.load.reason.invalid_value",
                };
                return false;
            }
            _keys.Clear();
            _keyIndex.Clear();
            if (rec.Keys != null)
            {
                for (int i = 0; i < rec.Keys.Length; i++)
                {
                    string k = rec.Keys[i] ?? string.Empty;
                    _keys.Add(k);
                    _keyIndex[k] = i;
                }
            }
            _machineUnit.Clear();
            _unitMachine.Clear();
            _enemyUnit.Clear();
            _unitEnemy.Clear();
            _markerByUnit.Clear();
            _unitLabels.Clear();
            _weaponIndex.Clear();
            ClearTurretMaps();
            for (int w = 0; w < Kernel.WeaponCount; w++)
            {
                if (Kernel.TryGetWeapon(w, out CombatWeapon cw))
                {
                    // FG2-FW-02：与 WeaponIndex 同一把键（武器参数的完整字节，含读法）——两边不一致时读档后会给同一把武器再开一行，单位的武器下标跟着变。
                    _weaponIndex[CombatKernel.WeaponKey(cw)] = w;
                }
            }
            _profileIndex.Clear();
            var orphans = new List<int>();
            for (int slot = 0; slot < Kernel.SlotCount; slot++)
            {
                CombatUnitView v = Kernel.ViewAt(slot);
                if (v.Kind == CombatUnitKind.Machine)
                {
                    if (v.ExtKey > 0 && MachineRegistry.TryGetRecord(v.ExtKey, out MachineRecord mrec) && !_machineUnit.ContainsKey(v.ExtKey))
                    {
                        AttachMachine(mrec, v.Id);
                        _machineWeapons[v.ExtKey] = new MachineWeaponInfo { WeaponIndex = v.Weapon };
                    }
                    else
                    {
                        orphans.Add(v.Id); // 记录已不存在的机器：内核单位移除（记录是存在性的真相）。
                    }
                }
                else if (v.Kind == CombatUnitKind.Enemy || v.Kind == CombatUnitKind.Structure)
                {
                    if (v.ExtKey >= 0 && v.ExtKey < _keys.Count && !string.IsNullOrEmpty(_keys[v.ExtKey]))
                    {
                        _enemyUnit[_keys[v.ExtKey]] = v.Id;
                        _unitEnemy[v.Id] = _keys[v.ExtKey];
                    }
                }
                else if (v.Kind == CombatUnitKind.Turret)
                {
                    RestoreTurretMap(v); // FG6-DEF-01：记录在案的炮塔（外部键 > 0）；原型炮塔（-1）照旧是匿名单位。
                }
            }
            foreach (int id in orphans)
            {
                Kernel.Despawn(id);
            }
            // FG2-FW-04：读档后内核的累计值换了一份（纪元 +1）→ 当场重取反馈基线：读档前后的差不算新触发，读档后的第一步照常结算。
            ReactionFeedback.Prime(Kernel, FeedCursor);
            return true;
        }

        /// <summary>内核里有、但记录说“已不在本地点 / 不存在”的机器（读档对账用）。</summary>
        public List<int> MachinesNotIn(Func<int, bool> belongsHere)
        {
            var list = new List<int>();
            foreach (int logicId in _machineUnit.Keys)
            {
                if (!belongsHere(logicId))
                {
                    list.Add(logicId);
                }
            }
            return list;
        }

        /// <summary>读档对账后刷新全部机器的武器参数（装配可能在存档后版本迁移过）。</summary>
        public void RefreshAllMachineWeapons(CampaignState state)
        {
            foreach (int logicId in new List<int>(_machineUnit.Keys))
            {
                RefreshMachineWeapon(state, logicId);
            }
        }
    }
}
