using BinGames.Sim;
using GameLogic.MetabolicSlice.Blueprint;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.WildOrgan
{
    /// <summary>M3-07：一件野生器官/基因实物走到了哪一步。与 <see cref="Blueprint.BlueprintEntry"/>
    /// 的配方轨完全独立——本类型是实物轨（Lineage_Loadout_Mapping.md 的划界：拾取/携带/移植都是
    /// 实物操作，只有显式"解析"才触碰蓝图库）。</summary>
    public enum WildOrganState
    {
        /// <summary>物理战利品实体，躺在世界空间某处，尚未被任何人捡起。</summary>
        InField,
        /// <summary>已被某具身体捡起，占用其携带容量，尚未移植/解析/拆解。</summary>
        Carried,
        /// <summary>已接入某具身体的体细胞临时槽（GDD §6.7）。</summary>
        Installed,
        /// <summary>已在萌生腔解析：蓝图库已推进，实物消耗完毕（终态）。</summary>
        Resolved,
        /// <summary>已在萌生腔拆解：产出生物质，实物消耗完毕（终态）。</summary>
        Dismantled,
    }

    /// <summary>M3-07：一件野生器官/基因实物的记录——只存 id 引用 + 运行期状态，
    /// 不复制 <see cref="GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog"/>/
    /// <see cref="GameLogic.MetabolicSlice.ContentCatalog.GeneCatalog"/> 的定义内容（同 M3-02 蓝图库的
    /// 划界）。<see cref="Kind"/>=<see cref="BlueprintSourceKind.Gene"/> 时不能被临时移植
    /// （体细胞临时槽只接"器官"，见 GDD §6.7），只能被搬运/解析/拆解。</summary>
    public sealed class WildOrganInstance
    {
        public string InstanceId { get; }
        public string SourceId { get; }
        public BlueprintSourceKind Kind { get; }
        public WildOrganState State { get; internal set; }

        /// <summary>当前持有者——<see cref="WildOrganState.Carried"/>/<see cref="WildOrganState.Installed"/>
        /// 时有效；<see cref="WildOrganState.InField"/> 时为 <see cref="SimEntityId.None"/>。</summary>
        public SimEntityId OwnerEntityId { get; internal set; }

        /// <summary>仅 <see cref="WildOrganState.InField"/> 时有意义：物理战利品实体的世界坐标。</summary>
        public float2 FieldPosition { get; internal set; }

        /// <summary>移植/搬运带来的污染（GDD §6.7"有污染、失稳或额外代谢负担"），0..1。
        /// 解析时会把它作为负向 contaminationDelta 喂给 <see cref="BlueprintEntry.ApplyResolve"/>
        /// （GDD §6.4"重复样本...修复污染"——用这件实物解析，相当于替对应蓝图冲抵一部分污染）。</summary>
        public float Contamination { get; internal set; }

        public WildOrganInstance(string instanceId, string sourceId, BlueprintSourceKind kind)
        {
            InstanceId = instanceId;
            SourceId = sourceId;
            Kind = kind;
            State = WildOrganState.InField;
            OwnerEntityId = SimEntityId.None;
            Contamination = 0f;
        }
    }
}
