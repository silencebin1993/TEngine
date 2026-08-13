using System;
using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.CardDefs
{
    /// <summary>最小占位卡定义；真正接游戏内容表时改走 tools/cell_tables/（本窗不建 Luban 表）。</summary>
    public sealed class CardDef
    {
        public string Id { get; }
        public string DisplayName { get; }
        public HashSet<SlotType> AllowedSlotTypes { get; }
        public bool IsSource { get; }
        public bool IsSink { get; }
        public Func<IModule> CreateModule { get; }

        /// <summary>美术占位字段（冻结总案 F9）；只留字符串占位，不引用真实资源路径。</summary>
        public string ArtId { get; }

        public CardDef(string id, string displayName, IEnumerable<SlotType> allowedSlotTypes,
            bool isSource, bool isSink, Func<IModule> createModule, string artId = null)
        {
            Id = id;
            DisplayName = displayName;
            AllowedSlotTypes = new HashSet<SlotType>(allowedSlotTypes);
            IsSource = isSource;
            IsSink = isSink;
            CreateModule = createModule;
            ArtId = artId;
        }
    }
}
