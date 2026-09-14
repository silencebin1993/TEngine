using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>M3-03：一个谱系——由同一萌生腔维持的繁殖家族（GDD §5）。一个谱系可维护多个表型模板，
    /// 每个模板名下是一串不可变版本历史。本类型只做存取，不做提交校验（校验在
    /// <see cref="LineageRegistry.CommitTemplate"/>），保证「历史只追加、旧版本永不被就地改写」。</summary>
    public sealed class Lineage
    {
        public string LineageId { get; }
        public string DisplayName { get; }

        private readonly Dictionary<string, List<PhenotypeTemplateVersion>> _templateHistory =
            new Dictionary<string, List<PhenotypeTemplateVersion>>();

        private static readonly PhenotypeTemplateVersion[] Empty = new PhenotypeTemplateVersion[0];

        public Lineage(string lineageId, string displayName)
        {
            LineageId = lineageId;
            DisplayName = displayName;
        }

        /// <summary>M3-08：UI 只读查询——枚举该谱系下已经提交过至少一个版本的模板名，
        /// 用于"展示模板预览"面板列出可选模板，不影响提交语义。</summary>
        public IReadOnlyCollection<string> TemplateNames => _templateHistory.Keys;

        public IReadOnlyList<PhenotypeTemplateVersion> GetHistory(string templateName)
        {
            return templateName != null && _templateHistory.TryGetValue(templateName, out List<PhenotypeTemplateVersion> list)
                ? list
                : Empty;
        }

        public PhenotypeTemplateVersion GetLatest(string templateName)
        {
            IReadOnlyList<PhenotypeTemplateVersion> history = GetHistory(templateName);
            return history.Count > 0 ? history[history.Count - 1] : null;
        }

        /// <summary>只追加，不接受覆盖已存在下标——调用方（<see cref="LineageRegistry"/>）保证
        /// Version 号严格递增，这里不重复校验递增性，只保证"append-only"这条物理约束。</summary>
        internal void AppendVersion(string templateName, PhenotypeTemplateVersion version)
        {
            if (!_templateHistory.TryGetValue(templateName, out List<PhenotypeTemplateVersion> list))
            {
                list = new List<PhenotypeTemplateVersion>();
                _templateHistory[templateName] = list;
            }

            list.Add(version);
        }
    }
}
