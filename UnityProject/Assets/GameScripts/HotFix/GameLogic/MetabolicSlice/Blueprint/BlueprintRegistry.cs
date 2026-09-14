using System.Collections.Generic;
using System.Linq;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.Blueprint
{
    /// <summary>
    /// M3-02：蓝图库领域模型。区分"野生实物"（<see cref="GameLogic.MetabolicSlice.Carrier.GeneReserve"/>/
    /// <see cref="GameLogic.MetabolicSlice.Carrier.CarrierGeneService"/> 管的实物轨，一律不动）与
    /// "已解析蓝图"（本模块，配方轨：只存 id + 解析进度，不持有任何实例）。
    ///
    /// 硬边界（Lineage_Loadout_Mapping.md §4.1 已定案）：本模块绝不引用 GeneInstance/PartInstance，
    /// 绝不调用 CarrierGeneService.EquipGene——那会把基因实例锁死在储备囊外，第二个同蓝图单位就
    /// 无基因可用，是这次映射里最容易做错的一步。
    ///
    /// 只在 <see cref="Resolve"/> 被显式调用时才会产生/推进蓝图（未来"解析"玩法动作的落点），
    /// 不订阅任何拾取/掉落信号——拾到器官/基因不会自动解锁蓝图（验收 1）。同理也不订阅拆解/丢弃
    /// 信号——拆解不会反向"送"出或补全蓝图（验收 3）。一旦 Completeness 达到 1，蓝图永久
    /// Unlocked、跨存档持久化，可无限次查询而不被消耗（验收 2：解析后能稳定复制）——本期不做
    /// "从蓝图派生实物"的具体产出管线（留给后续 M3-04/M3-05 的编译/生成故事），这里只保证蓝图
    /// 状态本身不会被任何查询操作改变。
    /// </summary>
    public sealed class BlueprintRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private readonly Dictionary<string, BlueprintEntry> _entries = new Dictionary<string, BlueprintEntry>();

        public IReadOnlyCollection<BlueprintEntry> AllEntries => _entries.Values;

        public override void OnEnter()
        {
            _entries.Clear();
            BlueprintHistory history = BlueprintPersistence.Load();
            foreach (BlueprintEntry entry in history.Entries)
            {
                _entries[entry.SourceId] = entry;
            }
        }

        public override void OnExit()
        {
            BlueprintPersistence.Save(_entries.Values);
        }

        public bool IsUnlocked(string sourceId)
        {
            return sourceId != null && _entries.TryGetValue(sourceId, out BlueprintEntry e) && e.Unlocked;
        }

        public BlueprintEntry GetEntry(string sourceId)
        {
            return sourceId != null && _entries.TryGetValue(sourceId, out BlueprintEntry e) ? e : null;
        }

        /// <summary>显式解析动作入口（未来"解析"玩法调用点，本期不接 UI/信号）。sourceId 必须能在
        /// 对应 Catalog 里查到（不复制定义，只引用 id），查无此 id 时 no-op 返回 null（Reject-to-Safe）。</summary>
        public BlueprintEntry Resolve(string sourceId, BlueprintSourceKind kind, float completenessGain, float contaminationDelta)
        {
            if (string.IsNullOrEmpty(sourceId) || !ExistsInCatalog(sourceId, kind))
            {
                return null;
            }

            if (!_entries.TryGetValue(sourceId, out BlueprintEntry entry))
            {
                entry = new BlueprintEntry(sourceId, kind);
                _entries[sourceId] = entry;
            }

            entry.ApplyResolve(completenessGain, contaminationDelta);
            return entry;
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
    }
}
