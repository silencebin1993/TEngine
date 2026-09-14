using System.Collections.Generic;
using System.Linq;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Control;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Blueprint;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Lineage;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.WildOrgan
{
    public enum WildOrganPickupResult { Ok, NotFound, NotInField, TooFar, CarryFull }

    public enum WildOrganInstallResult
    {
        Ok, NotFound, NotAnOrganelle, NotCarried, OrganelleInvalid,
        AlreadyHasTemporary, SlotConflict, BodyNotRegistered,
    }

    public enum WildOrganChamberActionResult { Ok, NotFound, NotCarried, TooFarFromChamber }

    /// <summary>
    /// M3-07：野生器官与单体临时移植。一件未归档的器官/基因实物能被搬运、拆解、解析，或（仅器官）
    /// 只装到一个身体——全部走**实物轨**（Lineage_Loadout_Mapping.md 已定案的划界），不碰
    /// <see cref="Blueprint.BlueprintRegistry"/> 的配方轨语义，只在显式"解析"那一步去调用它
    /// （同蓝图库自己的边界：拾取绝不产生/推进蓝图）。
    ///
    /// ── 现状核查结论 ──
    /// 本仓没有"物理战利品实体躺在地上"的既有机制（结构器官掉落是击杀即时进背包），所以本类型的
    /// "物理战利品实体"是新建的最小数据模型：<see cref="WildOrganInstance.FieldPosition"/> + 距离判定，
    /// 不是一个真正的可碰撞 GameObject/Sim 实体（非目标"不做多临时槽和复杂手术动画"同精神下，
    /// 表现层的拾取交互留给后续故事）。
    ///
    /// ── 双花防护 ──
    /// <see cref="WildOrganState"/> 是单一状态机，任一时刻只处于一种状态：解析/拆解只接受
    /// <see cref="WildOrganState.Carried"/>（不接受 <see cref="WildOrganState.Installed"/>），
    /// 临时移植只接受 <see cref="WildOrganState.Carried"/>——同一实物物理上不可能同时被解析和装备
    /// （验收 2）。
    ///
    /// ── 临时移植不改模板 ──
    /// 装/卸临时器官只经 <see cref="UnitLoadoutRegistry.SetTemporaryOrgan"/>/
    /// <see cref="UnitLoadoutRegistry.ClearTemporaryOrgan"/>（M3-07 新增，独立于 <c>Rebuild</c> 的常规
    /// 装配重建流程），本类型从未引用、从未调用 <see cref="LineageRegistry"/>/
    /// <see cref="PhenotypeTemplateVersion"/> 的任何写入口（验收 1）。
    ///
    /// ── 身体死亡 ──
    /// <see cref="HandleBodyDeath"/> 是真实入口（GDD §6.7"身体死亡时通常丢失"），但尚未接到任何
    /// 死亡信号——与 M3-06"暂时移出战斗"同等风险取舍：本仓现有 <c>KillSignal</c> 只带 <c>LogicId</c>
    /// 且语义是"击杀"而非"友方个体阵亡"，贸然订阅需要新增信号或改动战斗结算路径，风险收益比不划算，
    /// 留给后续故事按需接线，这里保证调用后行为正确。
    /// </summary>
    public sealed class WildOrganRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        /// <summary>拾取判定半径（占位——本仓没有"战场拾取物"的既有尺度可抄，取与
        /// <see cref="HomecomingRetrofitService.ChamberRadius"/> 同数量级但明显更小的近战级距离）。</summary>
        public const float PickupRadius = 2f;

        /// <summary>携带容量（占位——GDD 没有给出具体数字，"实现携带容量"这条实施项要的是"有上限、
        /// 满了会拒绝"这个机制本身，不是数值平衡）。</summary>
        public const int CarryCapacity = 3;

        /// <summary>首次解析即解锁基础蓝图（GDD §6.4），MVP 简化为一次到位，不做"多次解析累积完整度"
        /// 的渐进曲线——那是数值平衡的事，非本 story 目标。</summary>
        public const float ResolveCompletenessGain = 1f;

        /// <summary>解析/拆解的生物质产出占位值。GDD §6.4："拆解 生物质最高"/"解析 生物质少量"——
        /// 两个常量的相对大小体现这条排序，具体数值留给经济向故事替换。</summary>
        public const float ResolveBiomassYield = 2f;
        public const float DismantleBiomassYield = 10f;

        /// <summary>每次临时移植带来的污染增量占位（GDD §6.7"有污染、失稳或额外代谢负担"）。</summary>
        public const float InstallContaminationPerUse = 0.15f;

        private readonly Dictionary<string, WildOrganInstance> _instances = new Dictionary<string, WildOrganInstance>();
        private readonly Dictionary<SimEntityId, List<string>> _carried = new Dictionary<SimEntityId, List<string>>();
        private readonly Dictionary<SimEntityId, string> _installed = new Dictionary<SimEntityId, string>();
        private int _nextInstanceSeq = 1;

        private SimBridge _sim;
        private UnitLoadoutRegistry _unitLoadouts;

        public void Bind(SimBridge sim, UnitLoadoutRegistry unitLoadouts)
        {
            _sim = sim;
            _unitLoadouts = unitLoadouts;
        }

        public override void OnEnter()
        {
            _instances.Clear();
            _carried.Clear();
            _installed.Clear();
            _nextInstanceSeq = 1;
        }

        public WildOrganInstance GetInstance(string instanceId)
        {
            return instanceId != null && _instances.TryGetValue(instanceId, out WildOrganInstance instance) ? instance : null;
        }

        public int CarriedCount(SimEntityId owner)
        {
            return owner.IsValid && _carried.TryGetValue(owner, out List<string> list) ? list.Count : 0;
        }

        public bool TryGetInstalled(SimEntityId body, out string instanceId)
        {
            return _installed.TryGetValue(body, out instanceId);
        }

        /// <summary>建立一件物理战利品实体（sourceId 必须能在对应 Catalog 查到，不存在时
        /// Reject-to-Safe 返回 null，不产生任何记录）。</summary>
        public string DropInField(string sourceId, BlueprintSourceKind kind, float2 position)
        {
            if (string.IsNullOrEmpty(sourceId) || !ExistsInCatalog(sourceId, kind))
            {
                return null;
            }

            string instanceId = "wo_" + _nextInstanceSeq++;
            var instance = new WildOrganInstance(instanceId, sourceId, kind) { FieldPosition = position };
            _instances[instanceId] = instance;
            return instanceId;
        }

        /// <summary>拾取：需在 <see cref="PickupRadius"/> 内、且拾取者携带未满。</summary>
        public WildOrganPickupResult TryPickup(SimEntityId picker, string instanceId)
        {
            if (!_instances.TryGetValue(instanceId, out WildOrganInstance instance))
            {
                return WildOrganPickupResult.NotFound;
            }
            if (instance.State != WildOrganState.InField)
            {
                return WildOrganPickupResult.NotInField;
            }
            if (!TryGetPosition(picker, out float2 pickerPos))
            {
                return WildOrganPickupResult.TooFar;
            }

            float2 delta = pickerPos - instance.FieldPosition;
            if (delta.x * delta.x + delta.y * delta.y > PickupRadius * PickupRadius)
            {
                return WildOrganPickupResult.TooFar;
            }
            if (CarriedCount(picker) >= CarryCapacity)
            {
                return WildOrganPickupResult.CarryFull;
            }

            instance.State = WildOrganState.Carried;
            instance.OwnerEntityId = picker;
            AddToCarried(picker, instanceId);
            return WildOrganPickupResult.Ok;
        }

        /// <summary>单体临时移植：只接受本人携带、状态为 Carried 的器官（基因不行，见类型注释）。
        /// 目标动作槽按 <see cref="OrganelleDef.AttackMethod"/> 分派（true→Primary，否则→Utility，
        /// 同 M3-03 主器官槎位校验口径）；若该槽已被占用（无论是模板器官还是另一件临时器官）一律拒绝
        /// ——结构上保证全局至多一个临时槽（非目标"不做多临时槽"）。底盘兼容性：本仓尚未给个体建立
        /// 底盘类型字段（M3-03 已明确留白），当前只校验器官本身在 Catalog 里存在且未退役，
        /// 真正的底盘白名单校验留给底盘系统落地后的后续故事。</summary>
        public WildOrganInstallResult TryInstallTemporary(SimEntityId body, string instanceId)
        {
            if (!_instances.TryGetValue(instanceId, out WildOrganInstance instance))
            {
                return WildOrganInstallResult.NotFound;
            }
            if (instance.Kind != BlueprintSourceKind.Organelle)
            {
                return WildOrganInstallResult.NotAnOrganelle;
            }
            if (instance.State != WildOrganState.Carried || instance.OwnerEntityId != body)
            {
                return WildOrganInstallResult.NotCarried;
            }
            if (_installed.ContainsKey(body))
            {
                return WildOrganInstallResult.AlreadyHasTemporary;
            }

            OrganelleDef organelle = OrganelleCatalog.Get(instance.SourceId);
            if (organelle == null || organelle.IsRetired)
            {
                return WildOrganInstallResult.OrganelleInvalid;
            }

            LoadoutAction action = organelle.AttackMethod ? LoadoutAction.Primary : LoadoutAction.Utility;
            UnitLoadout loadout = _unitLoadouts?.Get(body);
            if (loadout == null || loadout == UnitLoadout.Empty)
            {
                return WildOrganInstallResult.BodyNotRegistered;
            }
            if (loadout.HasOrganInSlot(action))
            {
                return WildOrganInstallResult.SlotConflict;
            }

            if (_unitLoadouts == null || !_unitLoadouts.SetTemporaryOrgan(body, new UnitLoadoutOrgan(instance.SourceId, action)))
            {
                return WildOrganInstallResult.BodyNotRegistered;
            }

            instance.State = WildOrganState.Installed;
            instance.Contamination = Clamp01(instance.Contamination + InstallContaminationPerUse);
            _installed[body] = instanceId;
            return WildOrganInstallResult.Ok;
        }

        /// <summary>显式卸下临时器官：回到 Carried，继续占携带容量。查无临时器官返回 false。</summary>
        public bool UninstallTemporary(SimEntityId body)
        {
            if (!_installed.TryGetValue(body, out string instanceId))
            {
                return false;
            }

            _installed.Remove(body);
            _unitLoadouts?.ClearTemporaryOrgan(body);
            if (_instances.TryGetValue(instanceId, out WildOrganInstance instance))
            {
                instance.State = WildOrganState.Carried;
            }
            return true;
        }

        /// <summary>身体死亡：临时器官"通常丢失"（GDD §6.7）——不回到 Carried，直接从记录里彻底移除，
        /// 不进任何终态。接线时机见类型注释。</summary>
        public bool HandleBodyDeath(SimEntityId body)
        {
            if (!_installed.TryGetValue(body, out string instanceId))
            {
                return false;
            }

            _installed.Remove(body);
            _unitLoadouts?.ClearTemporaryOrgan(body);
            _instances.Remove(instanceId);
            RemoveFromCarried(body, instanceId);
            return true;
        }

        /// <summary>解析：推进对应蓝图（首次即解锁），实物进入终态。只接受 Carried（Installed 时必须
        /// 先卸下——验收 2 的边界）。</summary>
        public WildOrganChamberActionResult TryResolveAtChamber(SimEntityId owner, string instanceId, BlueprintRegistry blueprints)
        {
            WildOrganChamberActionResult check = ValidateChamberAction(owner, instanceId, out WildOrganInstance instance);
            if (check != WildOrganChamberActionResult.Ok)
            {
                return check;
            }

            blueprints?.Resolve(instance.SourceId, instance.Kind, ResolveCompletenessGain, -instance.Contamination);
            instance.State = WildOrganState.Resolved;
            RemoveFromCarried(owner, instanceId);
            return WildOrganChamberActionResult.Ok;
        }

        /// <summary>拆解：只产出生物质，不产生/推进蓝图，实物进入终态。</summary>
        public WildOrganChamberActionResult TryDismantleAtChamber(SimEntityId owner, string instanceId, BiomassLedger biomass, string lineageId)
        {
            WildOrganChamberActionResult check = ValidateChamberAction(owner, instanceId, out WildOrganInstance instance);
            if (check != WildOrganChamberActionResult.Ok)
            {
                return check;
            }

            biomass?.Deposit(lineageId, DismantleBiomassYield);
            instance.State = WildOrganState.Dismantled;
            RemoveFromCarried(owner, instanceId);
            return WildOrganChamberActionResult.Ok;
        }

        private WildOrganChamberActionResult ValidateChamberAction(SimEntityId owner, string instanceId, out WildOrganInstance instance)
        {
            instance = null;
            if (!_instances.TryGetValue(instanceId, out WildOrganInstance found))
            {
                return WildOrganChamberActionResult.NotFound;
            }
            if (found.State != WildOrganState.Carried || found.OwnerEntityId != owner)
            {
                return WildOrganChamberActionResult.NotCarried;
            }
            if (!IsWithinChamberRange(owner))
            {
                return WildOrganChamberActionResult.TooFarFromChamber;
            }

            instance = found;
            return WildOrganChamberActionResult.Ok;
        }

        /// <summary>与 <see cref="HomecomingRetrofitService"/> 同款占位口径（GDD 没有"萌生腔物理坐标"
        /// 概念）：以玩家当前位置为腔体有效位置，半径复用同一个公开常量，避免两处各定义一个数字。</summary>
        private bool IsWithinChamberRange(SimEntityId entityId)
        {
            if (!TryGetPosition(entityId, out float2 pos))
            {
                return false;
            }

            float2 chamberPos = _sim.PlayerPosition;
            float2 delta = pos - chamberPos;
            float distSq = delta.x * delta.x + delta.y * delta.y;
            return distSq <= HomecomingRetrofitService.ChamberRadius * HomecomingRetrofitService.ChamberRadius;
        }

        private bool TryGetPosition(SimEntityId entityId, out float2 position)
        {
            position = default;
            if (!entityId.IsValid || _sim == null || !_sim.Running)
            {
                return false;
            }

            SimSnapshot snapshot = _sim.Snapshot;
            if (!snapshot.TryResolve(entityId, out int index))
            {
                return false;
            }

            position = snapshot.Position[index];
            return true;
        }

        private void AddToCarried(SimEntityId owner, string instanceId)
        {
            if (!_carried.TryGetValue(owner, out List<string> list))
            {
                list = new List<string>(CarryCapacity);
                _carried[owner] = list;
            }
            if (!list.Contains(instanceId))
            {
                list.Add(instanceId);
            }
        }

        private void RemoveFromCarried(SimEntityId owner, string instanceId)
        {
            if (_carried.TryGetValue(owner, out List<string> list))
            {
                list.Remove(instanceId);
            }
        }

        private static bool ExistsInCatalog(string sourceId, BlueprintSourceKind kind)
        {
            switch (kind)
            {
                case BlueprintSourceKind.Organelle:
                    return OrganelleCatalog.Get(sourceId) != null;
                case BlueprintSourceKind.Gene:
                    return GeneCatalog.AllGeneIds.Contains(sourceId);
                default:
                    return false;
            }
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
