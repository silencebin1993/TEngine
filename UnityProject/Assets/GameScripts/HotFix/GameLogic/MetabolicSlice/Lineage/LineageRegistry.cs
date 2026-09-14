using System.Collections.Generic;
using System.Linq;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Blueprint;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>
    /// M3-03：谱系与表型模板领域模型。架在 M3-02 蓝图库之上——模板槎位只存蓝图 id，提交前必须
    /// 校验每个 id 在 <see cref="BlueprintRegistry"/> 里已 Unlocked（Lineage_Loadout_Mapping.md §4.1
    /// 的硬边界：模板层不持有实例、不调用 CarrierGeneService.EquipGene，本模块同样不引用
    /// GeneInstance/PartInstance）。
    ///
    /// 原型期槎位（GDD §6.5"原型期只开放主器官+功能器官+2基因，验证闭环后再逐步放开"）：本期落地
    /// 为 1 主器官（<see cref="OrganelleDef.AttackMethod"/>=true）+ 2–4 个有序基因节点——现有
    /// <see cref="OrganelleCatalog"/> 尚无独立的"功能器官"分类字段（只有 Attack/Structural/
    /// EnergySource），故本期不强制第二个器官槎位，避免校验一个目录里不存在的分类；底盘/结构器官/
    /// 完整 AI 教义留给后续故事逐步放开，模型上只是"以后再加字段"，不是本期要伪造的空接口。
    ///
    /// 提交语义（GDD §6.6）：每次 CommitTemplate 只追加一个新 <see cref="PhenotypeTemplateVersion"/>，
    /// 旧版本对象没有任何 setter，物理上不可写。
    /// </summary>
    public sealed class LineageRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        public const int MinGeneSlots = 2;
        public const int MaxGeneSlots = 4;

        private const float OrganelleBiomassWeight = 3f;
        private const float GeneBiomassWeight = 1f;

        private readonly Dictionary<string, Lineage> _lineages = new Dictionary<string, Lineage>();
        private BlueprintRegistry _blueprints;

        /// <summary>依赖注入蓝图库（同 CellStageFlow 里其它模块 Bind 的节奏），不用静态单例——
        /// 保持本模块可在自检里脱离 CellStageFlow 单独 new 出来测（同 BlueprintRegistry 自检口径）。</summary>
        public void Bind(BlueprintRegistry blueprints)
        {
            _blueprints = blueprints;
        }

        public Lineage GetOrCreateLineage(string lineageId, string displayName = null)
        {
            if (!_lineages.TryGetValue(lineageId, out Lineage lineage))
            {
                lineage = new Lineage(lineageId, displayName ?? lineageId);
                _lineages[lineageId] = lineage;
            }

            return lineage;
        }

        public Lineage GetLineage(string lineageId)
        {
            return lineageId != null && _lineages.TryGetValue(lineageId, out Lineage lineage) ? lineage : null;
        }

        /// <summary>提交一次表型模板版本。校验失败时 Reject-to-Safe：不创建任何新版本，
        /// 返回 null 并把原因写进 <paramref name="error"/>。</summary>
        public PhenotypeTemplateVersion CommitTemplate(
            string lineageId,
            string templateName,
            string organelleId,
            IReadOnlyList<string> geneIds,
            string doctrineTag,
            out string error)
        {
            error = ValidateCommit(organelleId, geneIds);
            if (error != null)
            {
                return null;
            }

            Lineage lineage = GetOrCreateLineage(lineageId);
            int nextVersion = lineage.GetHistory(templateName).Count + 1;
            string[] geneIdsCopy = geneIds.ToArray();
            string signature = PhenotypeTemplateSignature.Compute(organelleId, geneIdsCopy);
            float biomassCost = OrganelleBiomassWeight + geneIdsCopy.Length * GeneBiomassWeight;

            var version = new PhenotypeTemplateVersion(nextVersion, organelleId, geneIdsCopy, doctrineTag, biomassCost, signature);
            lineage.AppendVersion(templateName, version);
            return version;
        }

        private string ValidateCommit(string organelleId, IReadOnlyList<string> geneIds)
        {
            if (string.IsNullOrEmpty(organelleId))
            {
                return "主器官 id 不能为空";
            }

            if (geneIds == null || geneIds.Count < MinGeneSlots || geneIds.Count > MaxGeneSlots)
            {
                return $"基因节点数需在 {MinGeneSlots}-{MaxGeneSlots} 之间（GDD §6.5）";
            }

            OrganelleDef organelle = OrganelleCatalog.Get(organelleId);
            if (organelle == null)
            {
                return $"主器官 id 在 OrganelleCatalog 查无：{organelleId}";
            }

            if (!organelle.AttackMethod || organelle.IsRetired)
            {
                return $"主器官槎位只能放攻击方式器官（AttackMethod=true 且未退役）：{organelleId}";
            }

            foreach (string geneId in geneIds)
            {
                if (string.IsNullOrEmpty(geneId) || !GeneCatalog.AllGeneIds.Contains(geneId))
                {
                    return $"基因 id 在 GeneCatalog 查无：{geneId}";
                }
            }

            if (_blueprints == null)
            {
                return "谱系模块未绑定蓝图库（Bind 未调用）";
            }

            if (!_blueprints.IsUnlocked(organelleId))
            {
                return $"主器官蓝图未解锁，不能进模板：{organelleId}";
            }

            foreach (string geneId in geneIds)
            {
                if (!_blueprints.IsUnlocked(geneId))
                {
                    return $"基因蓝图未解锁，不能进模板：{geneId}";
                }
            }

            return null;
        }
    }
}
