using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
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
        public virtual void OnMachineMarked(CombatSite site, int logicId, float seconds, RegionEnemyRecord scout) { }
        public virtual void OnMarkCleared(CombatSite site, int logicId, RegionEnemyRecord jammer) { }
        public virtual void OnMarkMissed(CombatSite site, RegionEnemyRecord scout) { }
        public virtual void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition) { }
        public virtual void OnEngageRequest(CombatSite site, int logicId) { }
        /// <summary>目标“当前不可伤”时给玩家看的原因（阶段名）。</summary>
        public virtual string InvulnerableDetail(RegionEnemyRecord enemy) => string.Empty;
    }

    /// <summary>机器武器参数的来源信息（开火提示音按主武器区分；装配解析失败时的原因）。</summary>
    public struct MachineWeaponInfo
    {
        public string PrimaryId;
        public string FailureReason;
        public int WeaponIndex;
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
    public sealed class CombatSite : IDisposable
    {
        public string SiteId { get; }

        /// <summary>本地点的内核。**只给桥接层（Campaign/Combat/）与 Editor 自检 / 性能探针用**：热更层玩法代码一律经本类的门面方法碰内核
        /// （FG14 §5 硬约束 3），“战斗内核”自检的扫描段断言 Campaign/Combat/ 以外的热更代码没有直接访问它。</summary>
        public CombatKernel Kernel { get; private set; }
        public CombatSiteRules Rules { get; set; }
        public RegionSquadCommandSystem Squad { get; set; }
        public CombatTransformSync Sync { get; private set; }
        public CombatRenderer Renderer { get; private set; }

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
            Sync = new CombatTransformSync(16);
            MaxEventsPerStep = Math.Max(1, config.MaxGameplayEventsPerStep);
            _unitHeight = Tuning("combat.render.unit_height", 0.6f);
        }

        private readonly float _unitHeight;

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

        public bool TryGetMachineMarker(int logicId, out HomeValleyMachineMarker marker)
        {
            marker = null;
            return _machineUnit.TryGetValue(logicId, out int unit) && _markerByUnit.TryGetValue(unit, out marker);
        }

        public bool TryGetMachineOfUnit(int unitId, out int logicId) => _unitMachine.TryGetValue(unitId, out logicId);

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
        }

        private MachineWeaponInfo ResolveMachineWeapon(CampaignState state, int logicId)
        {
            var info = new MachineWeaponInfo { WeaponIndex = -1 };
            if (state == null)
            {
                info.FailureReason = GameText.Get("combat.fire.no_weapon");
                _machineWeapons[logicId] = info;
                return info;
            }
            MachineCombatResolution resolution = MachineLoadoutRegistry.ResolveForAi(state, logicId, state.RandomSeed);
            if (!resolution.Success)
            {
                info.FailureReason = resolution.FailureReason;
                _machineWeapons[logicId] = info;
                return info;
            }
            BlueprintCircuitPreview p = resolution.Preview;
            info.PrimaryId = p.PrimaryId;
            CombatWeapon w = MachineWeaponFrom(p);
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
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForAi(state, logicId, state.RandomSeed);
            return r.Success && r.Preview.HasHeatSinkStructure;
        }

        /// <summary>
        /// 装配预览 → 内核武器参数（Demo 规则的唯一翻译处）：
        /// 铸造重炮 = 两段式（1 秒瞄准线、3 秒冷却、55 基础伤害、积热 40、熔穿过载 +25 积热与 30% 穿甲、100 过热 / 60 恢复、散热 10（散热鳍 +5））；
        /// 其余 = 即时命中，伤害 = 装配编译出的 TotalNormalizedDamage；装了标记器命中打 10 秒标记；标记跳转 8 米内至多 2 个、每跳 ×0.6。
        /// </summary>
        public static CombatWeapon MachineWeaponFrom(BlueprintCircuitPreview p)
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
                w.HeatPerShot = FracturedCityLayout.CannonBaseHeatPerShot;
                w.OverloadExtraHeat = FracturedCityLayout.OverloadExtraHeatPerShot;
                w.PierceBonus = FracturedCityLayout.OverloadArmorPierceBonus;
                w.Reaction = p.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId ? CombatReaction.MeltOverload : CombatReaction.None;
                return w;
            }
            w.Mode = CombatWeaponMode.Instant;
            w.Damage = Mathf.Max(0f, p.TotalNormalizedDamage);
            w.Range = FracturedCityLayout.DirectAttackRange;
            w.MarkSeconds = p.HasMarkerFunction ? FracturedCityLayout.EnemyMarkDurationSeconds : 0f;
            if (p.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId)
            {
                w.Reaction = CombatReaction.MarkJump;
                w.JumpRange = FracturedCityLayout.MarkJumpRange;
                w.JumpFalloff = FracturedCityLayout.MarkJumpDamageFalloff;
                w.JumpMax = FracturedCityLayout.MarkJumpMaxTargets;
            }
            return w;
        }

        /// <summary>武器表去重：同样的参数只占一行（机器反复进出、装配反复刷新不会让表无限增长）。</summary>
        public int WeaponIndex(in CombatWeapon w)
        {
            string key = string.Join("|", (int)w.Mode, (int)w.Reaction, w.HasOutput, (int)w.TargetMode, w.Range, w.Damage, w.Cooldown, w.AimSeconds,
                w.ProjectileSpeed, w.ProjectileRadius, w.ProjectileLife, w.HeatPerShot, w.OverloadExtraHeat, w.OverheatAt, w.RecoverBelow, w.Dissipation,
                w.HeatSinkBonus, w.PierceBonus, w.MarkSeconds, w.JumpRange, w.JumpFalloff, w.JumpMax);
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

        // ─────────────────────────────── 步与事件 ───────────────────────────────

        /// <summary>一个模拟步：内核一步 + 排空事件。<paramref name="time"/> = 这一步开始时的游戏秒。</summary>
        public void Step(float dt, double time)
        {
            if (IsDisposed)
            {
                return;
            }
            Kernel.Step(dt, time);
            ProcessEvents();
        }

        /// <summary>排空至多 <see cref="MaxEventsPerStep"/> 条玩法事件与本批提示事件，按序号合并后逐条结算。即时开火之后也调用。</summary>
        public void ProcessEvents()
        {
            if (IsDisposed)
            {
                return;
            }
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
            for (int i = 0; i < _merge.Count; i++)
            {
                try
                {
                    Handle(state, _merge[i]);
                }
                catch (Exception e)
                {
                    Log.Error($"[CombatSite] {SiteId} 处理内核事件 {_merge[i].Kind} 异常：{e}");
                }
            }
            LastEventsProcessed = _merge.Count;
            TotalEventsProcessed += _merge.Count;
            _watch.Stop();
            LastEventsMs = _watch.Elapsed.TotalMilliseconds;
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
                    if (rec == null || !rec.IsAlive || !Kernel.TryGetUnit(e.Unit, out CombatUnitView v) || (v.Flags & CombatUnitFlags.ExternalHealth) != 0)
                    {
                        return;
                    }
                    rec.Health = 0f;
                    rec.IsAlive = false;
                    rec.Position = new Vector2((float)e.Pos.x, (float)e.Pos.y);
                    Rules?.OnEnemyKilled(this, rec);
                    return;
                }
                case CombatEventKind.Damaged:
                {
                    RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
                    if (rec == null)
                    {
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
                    if (_unitMachine.TryGetValue(e.Unit, out int logicId))
                    {
                        Rules?.OnEngageRequest(this, logicId);
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
                        FeedbackCues.Raise(FeedbackCueId.CannonCharge, "#" + rec.DisplayNumber);
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
                        Log.Info($"[CombatSite] 机器 {logicId} 重炮过热（{e.Value:F0}），停火直到降到 {FracturedCityLayout.WeaponHeatRecoverThreshold:F0} 以下。");
                        FeedbackCues.Raise(FeedbackCueId.WeaponOverheat, "#" + rec.DisplayNumber + $"，降到 {FracturedCityLayout.WeaponHeatRecoverThreshold:F0} 以下才能再开火");
                    }
                    return;
                }
                case CombatEventKind.ArmorHit:
                    FeedbackCues.RaiseAt(FeedbackCueId.ArmorHit, at);
                    return;
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
            Renderer ??= new CombatRenderer();
            Renderer.Draw(Kernel, camera, alpha, double2.zero, _unitHeight);
        }

        /// <summary>离开观察：释放 GPU 资源（表现对象由地点自己销毁）。</summary>
        public void ReleaseRender()
        {
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
            _weaponIndex.Clear();
            for (int w = 0; w < Kernel.WeaponCount; w++)
            {
                if (Kernel.TryGetWeapon(w, out CombatWeapon cw))
                {
                    string key = string.Join("|", (int)cw.Mode, (int)cw.Reaction, cw.HasOutput, (int)cw.TargetMode, cw.Range, cw.Damage, cw.Cooldown, cw.AimSeconds,
                        cw.ProjectileSpeed, cw.ProjectileRadius, cw.ProjectileLife, cw.HeatPerShot, cw.OverloadExtraHeat, cw.OverheatAt, cw.RecoverBelow, cw.Dissipation,
                        cw.HeatSinkBonus, cw.PierceBonus, cw.MarkSeconds, cw.JumpRange, cw.JumpFalloff, cw.JumpMax);
                    _weaponIndex[key] = w;
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
            }
            foreach (int id in orphans)
            {
                Kernel.Despawn(id);
            }
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
