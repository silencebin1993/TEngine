using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Control;
using GameLogic.Core;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>
    /// M3-06：回巢改造。地图上已存在的个体（M3-05 萌生腔新生，或之前几局遗留）只有满足
    /// 位置/交战/携带物/网络四项校验、并支付生物质成本后，才能把绑定从旧版本换成谱系当前最新版本。
    ///
    /// 目标个体的"旧版本"信息复用 <see cref="GerminationChamberRegistry"/> 已有的绑定表
    /// （不新开第二张平行表）；完成后调用 <see cref="GerminationChamberRegistry.UpdateBinding"/>
    /// 原地替换，同一个 <see cref="SimEntityId"/>，不产出第二个实体、不复制任何蓝图/器官/基因实例。
    ///
    /// ── 锁定语义（实施第 2/5 条）──
    /// "锁定目标模板版本"在 <see cref="TryBeginRetrofit"/> 那一刻定死：之后谱系哪怕再提交新版本，
    /// 这张已经开始的改造票也不会跟着换版本，与 <see cref="GerminationChamberRegistry.Enqueue"/>
    /// 锁定入队时刻版本的口径完全一致（"锁定"就该是那一刻钉死，不是持续追新）。
    ///
    /// ── 位置/交战/携带物/网络 ── 四项占位判据
    /// 本仓尚未实现锚点/网络连通系统、战斗交战状态、携带物系统（均为更后期里程碑或压根没有的概念，
    /// 已逐一 Grep 确认查无）。网络判据接到 <see cref="GerminationChamberRegistry.IsNetworked"/>
    /// （默认联网）；交战与携带物在本类内部各留一张占位登记表，默认 false，<see cref="SetEngaged"/>/
    /// <see cref="SetCarryingKeyItem"/> 是真实调用点，不是注释掉的假校验——将来战斗/携带物系统落地后
    /// 直接喂真值，回巢改造的校验流程不必重写。位置判据用 <see cref="GerminationChamberRegistry"/>
    /// 生成新个体时同款占位口径（GDD 没有"萌生腔物理坐标"概念）：以玩家当前位置为腔体有效位置。
    ///
    /// ── 中断 ──
    /// 已扣的生物质在"锁定成本"那一步扣（与入队即扣费同口径）；<see cref="CancelRetrofit"/>
    /// 全额退款——没交付结果就不该扣钱。改造进行中不产出第二份状态，物理上不可能：
    /// 全程只操作同一个 <see cref="SimEntityId"/> 的同一条绑定记录，从未 new 过第二个实体或器官实例。
    /// </summary>
    public sealed class HomecomingRetrofitService : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        /// <summary>占位"腔体范围"半径（米）。与 <see cref="GerminationChamberRegistry"/> 的生成偏移
        /// 量级对齐（基准偏移 (6,-2) + 最多 (4,4) 错位，实际距玩家 ≤ ~12），真实腔体坐标/范围设计
        /// 留给后续故事——GDD 没有"萌生腔物理坐标"概念。</summary>
        public const float ChamberRadius = 12f;

        public enum RetrofitRejectReason
        {
            None = 0,
            AlreadyInProgress,
            NotBound,
            NoNewerVersion,
            NotNetworked,
            Engaged,
            CarryingKeyItem,
            TooFarFromChamber,
            InsufficientBiomass,
        }

        private sealed class RetrofitTicket
        {
            public SimEntityId EntityId;
            public string LineageId;
            public string TemplateName;
            public PhenotypeTemplateVersion LockedVersion;
            public float Cost;
        }

        private readonly Dictionary<SimEntityId, RetrofitTicket> _inProgress = new Dictionary<SimEntityId, RetrofitTicket>();
        private readonly HashSet<SimEntityId> _engaged = new HashSet<SimEntityId>();
        private readonly HashSet<SimEntityId> _carryingKeyItem = new HashSet<SimEntityId>();

        private SimBridge _sim;
        private LineageRegistry _lineages;
        private BiomassLedger _biomass;
        private UnitLoadoutRegistry _unitLoadouts;
        private GerminationChamberRegistry _chambers;

        public void Bind(SimBridge sim, LineageRegistry lineages, BiomassLedger biomass,
            UnitLoadoutRegistry unitLoadouts, GerminationChamberRegistry chambers)
        {
            _sim = sim;
            _lineages = lineages;
            _biomass = biomass;
            _unitLoadouts = unitLoadouts;
            _chambers = chambers;
        }

        public override void OnEnter()
        {
            _inProgress.Clear();
            _engaged.Clear();
            _carryingKeyItem.Clear();
        }

        /// <summary>占位交战判据的真实写入口（本仓无战斗状态系统可查，见类型注释）。</summary>
        public void SetEngaged(SimEntityId entityId, bool engaged)
        {
            if (!entityId.IsValid)
            {
                return;
            }

            if (engaged)
            {
                _engaged.Add(entityId);
            }
            else
            {
                _engaged.Remove(entityId);
            }
        }

        /// <summary>占位携带物判据的真实写入口（本仓无携带物系统可查，见类型注释）。</summary>
        public void SetCarryingKeyItem(SimEntityId entityId, bool carrying)
        {
            if (!entityId.IsValid)
            {
                return;
            }

            if (carrying)
            {
                _carryingKeyItem.Add(entityId);
            }
            else
            {
                _carryingKeyItem.Remove(entityId);
            }
        }

        public bool IsRetrofitting(SimEntityId entityId) => _inProgress.ContainsKey(entityId);

        /// <summary>校验位置/交战/携带物/网络，锁定目标版本与成本，扣费。全部通过才返回
        /// <see cref="RetrofitRejectReason.None"/> 并把该实体标记为"改造中"——M4-R00-02 队列⑤-21
        /// （M3-R03-RETURN-REAL-COMBAT-EXIT）起真的从战斗调度摘除：叠加 <see cref="SimStatus.Stunned"/>
        /// （AI/命令双路径当帧只出 Idle 意图，不再继续自动开火/移动）+ <see cref="SimStatus.Invulnerable"/>
        /// （<c>JobDamage</c> 跳过伤害结算，不再"原地挨打"），<see cref="CompleteRetrofit"/>/
        /// <see cref="CancelRetrofit"/> 对称摘掉。任一校验失败 Reject-to-Safe：不扣费、不产生任何副作用。</summary>
        public RetrofitRejectReason TryBeginRetrofit(SimEntityId entityId)
        {
            if (!entityId.IsValid)
            {
                return RetrofitRejectReason.NotBound;
            }

            if (_inProgress.ContainsKey(entityId))
            {
                return RetrofitRejectReason.AlreadyInProgress;
            }

            if (_chambers == null || !_chambers.TryGetBinding(entityId, out GerminationChamberRegistry.UnitBinding binding))
            {
                return RetrofitRejectReason.NotBound;
            }

            Lineage lineage = _lineages?.GetLineage(binding.LineageId);
            PhenotypeTemplateVersion latest = lineage?.GetLatest(binding.TemplateName);
            if (latest == null || latest == binding.Version)
            {
                return RetrofitRejectReason.NoNewerVersion;
            }

            if (_chambers.IsNetworked(binding.LineageId) == false)
            {
                return RetrofitRejectReason.NotNetworked;
            }

            if (_engaged.Contains(entityId))
            {
                return RetrofitRejectReason.Engaged;
            }

            if (_carryingKeyItem.Contains(entityId))
            {
                return RetrofitRejectReason.CarryingKeyItem;
            }

            if (!IsWithinChamberRange(entityId))
            {
                return RetrofitRejectReason.TooFarFromChamber;
            }

            float cost = latest.BiomassCost;
            if (_biomass == null || !_biomass.TryDeduct(binding.LineageId, cost))
            {
                return RetrofitRejectReason.InsufficientBiomass;
            }

            _inProgress[entityId] = new RetrofitTicket
            {
                EntityId = entityId,
                LineageId = binding.LineageId,
                TemplateName = binding.TemplateName,
                LockedVersion = latest,
                Cost = cost,
            };

            SetCombatExitStatus(entityId, exiting: true);

            return RetrofitRejectReason.None;
        }

        /// <summary>中断：全额退款，不留任何痕迹（幂等——重复取消同一实体的第二次调用直接返回
        /// false，不重复退款）。该实体的绑定与装配全程没被动过，不存在"复制资源或器官"的可能。</summary>
        public bool CancelRetrofit(SimEntityId entityId)
        {
            if (!_inProgress.TryGetValue(entityId, out RetrofitTicket ticket))
            {
                return false;
            }

            _inProgress.Remove(entityId);
            _biomass?.Refund(ticket.LineageId, ticket.Cost);
            SetCombatExitStatus(entityId, exiting: false);
            return true;
        }

        /// <summary>M4-R00-02 队列⑤-21：战斗调度摘除/重入的唯一写口——<see cref="SimBridge.TryResolveUnitIndex"/>
        /// 查不到（已死亡/未落地）时安全 no-op，不是失败：摘除的对象已经不在战斗里，重入的对象
        /// 死活都不需要再改状态。</summary>
        private void SetCombatExitStatus(SimEntityId entityId, bool exiting)
        {
            if (_sim == null || !_sim.TryResolveUnitIndex(entityId, out int unitIndex))
            {
                return;
            }

            _sim.ApplyStatusUnit(unitIndex, SimStatus.Stunned | SimStatus.Invulnerable, add: exiting);
        }

        /// <summary>完成：把绑定原地替换成锁定版本，并用新版本的主器官+有序基因重挂
        /// <see cref="UnitLoadoutRegistry"/> 装配（这就是"替换装配签名"的实际含义——AI 自动开火与
        /// 直控释放路径读的正是这份显式装配，见 <see cref="GerminationChamberRegistry"/> 类注释；
        /// 基因随新版本一起换新，M4-R00-02 队列②号项）。个体的 <see cref="UnitVitalsRegistry"/>
        /// 运行期状态（生命/伤势/过载债）不在本类职责范围内——那本账按 <see cref="SimEntityId"/> 归属，
        /// 本方法从未触碰它，整个流程操作的都是同一个 id，状态天然原样保留。</summary>
        public bool CompleteRetrofit(SimEntityId entityId)
        {
            if (!_inProgress.TryGetValue(entityId, out RetrofitTicket ticket))
            {
                return false;
            }

            _inProgress.Remove(entityId);
            SetCombatExitStatus(entityId, exiting: false);

            _chambers?.UpdateBinding(entityId, ticket.LineageId, ticket.TemplateName, ticket.LockedVersion);

            var organs = new List<UnitLoadoutOrgan>(1)
            {
                new UnitLoadoutOrgan(ticket.LockedVersion.OrganelleId, LoadoutAction.Primary, geneIds: ticket.LockedVersion.GeneIds),
            };
            _unitLoadouts?.RegisterExplicit(entityId, organs);

            return true;
        }

        private bool IsWithinChamberRange(SimEntityId entityId)
        {
            if (_sim == null || !_sim.Running)
            {
                return false;
            }

            SimSnapshot snapshot = _sim.Snapshot;
            if (!snapshot.TryResolve(entityId, out int index))
            {
                return false;
            }

            Unity.Mathematics.float2 chamberPos = _sim.PlayerPosition;
            Unity.Mathematics.float2 delta = snapshot.Position[index] - chamberPos;
            float distSq = delta.x * delta.x + delta.y * delta.y;
            return distSq <= ChamberRadius * ChamberRadius;
        }
    }
}
