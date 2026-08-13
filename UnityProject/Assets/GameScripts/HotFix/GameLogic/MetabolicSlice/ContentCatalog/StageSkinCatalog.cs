using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 宪法 §5 阶段皮层级。刻意与 <see cref="GameLogic.Stage"/> 真实阶段 FSM 命名空间区分——
    /// 本类型只做展示名/ArtId 换皮，不代表游戏已支持机械/宇宙阶段的真实关卡。
    /// </summary>
    public enum SkinTier { Cell, Mech, Cosmic }

    /// <summary>
    /// story-005：`PrimitiveId → { Cell, Mech, Cosmic }` 展示名/ArtId 映射表。
    /// Key 空间 = 24 个 <see cref="OrganelleCatalog"/> 的 org_* Id + 2 个合成轴级 key
    /// （axis_displace / axis_void：宪法 §5 示例行里没有对应 <see cref="OrganelleDef"/> 的两行）。
    /// Cell 层不重复存字符串：org_* 委派 <see cref="OrganelleCatalog"/>，防止两处漂移；
    /// axis_displace 的 Cell 名是宪法给的固定值；axis_void 的 Cell 层按宪法"（二期）"标注返回 null
    /// （尚未启用，不得编造假名字）。反应表（ReactionCatalog）本类零关联，禁止为 M/U 复制一套。
    /// </summary>
    public static class StageSkinCatalog
    {
        private sealed class SkinEntry
        {
            public string CellName;
            public string CellArtId;
            public string MechName;
            public string MechArtId;
            public string CosmicName;
            public string CosmicArtId;
        }

        private static readonly Dictionary<string, SkinEntry> _skins = new Dictionary<string, SkinEntry>
        {
            // 宪法 §5 示例行（4 条与既有器官逐字重合 + 2 条合成轴级 key）——逐字抄，禁止改字。
            ["org_perox"] = new SkinEntry
            {
                MechName = "喷燃嘴", MechArtId = "org/perox@mech",
                CosmicName = "恒星风", CosmicArtId = "org/perox@cosmic",
            },
            ["org_scatter"] = new SkinEntry
            {
                MechName = "霰射阵列", MechArtId = "org/scatter@mech",
                CosmicName = "星屑裂解", CosmicArtId = "org/scatter@cosmic",
            },
            ["org_flagella"] = new SkinEntry
            {
                MechName = "陀螺稳定", MechArtId = "org/flagella@mech",
                CosmicName = "轨道环", CosmicArtId = "org/flagella@cosmic",
            },
            ["org_swell"] = new SkinEntry
            {
                MechName = "胀缩装甲", MechArtId = "org/swell@mech",
                CosmicName = "引力透镜放大", CosmicArtId = "org/swell@cosmic",
            },
            ["axis_displace"] = new SkinEntry
            {
                CellName = "纤毛推挤", CellArtId = "axis/displace@cell",
                MechName = "推进喷嘴", MechArtId = "axis/displace@mech",
                CosmicName = "跃迁位移", CosmicArtId = "axis/displace@cosmic",
            },
            ["axis_void"] = new SkinEntry
            {
                CellName = null, CellArtId = null,
                MechName = "湮灭舱", MechArtId = "axis/void@mech",
                CosmicName = "事件视界", CosmicArtId = "axis/void@cosmic",
            },

            // 其余 20 个器官：无宪法先例，Worker 命名（Preflight D5 授权）。
            // 中文 2~5 字，贴合既有 Role/Cell 名意境；机械向=装置/机构意象，宇宙向=天体/尺度意象。
            ["org_mito"] = new SkinEntry
            {
                MechName = "核聚变堆", MechArtId = "org/mito@mech",
                CosmicName = "恒星核", CosmicArtId = "org/mito@cosmic",
            },
            ["org_chloro"] = new SkinEntry
            {
                MechName = "光能采集板", MechArtId = "org/chloro@mech",
                CosmicName = "星云采光帆", CosmicArtId = "org/chloro@cosmic",
            },
            ["org_vacuole"] = new SkinEntry
            {
                MechName = "蓄能罐", MechArtId = "org/vacuole@mech",
                CosmicName = "星云聚能云", CosmicArtId = "org/vacuole@cosmic",
            },
            ["org_golgi"] = new SkinEntry
            {
                MechName = "分流阀组", MechArtId = "org/golgi@mech",
                CosmicName = "星流分岔口", CosmicArtId = "org/golgi@cosmic",
            },
            ["org_merge"] = new SkinEntry
            {
                MechName = "汇流集管", MechArtId = "org/merge@mech",
                CosmicName = "星流汇聚点", CosmicArtId = "org/merge@cosmic",
            },
            ["org_lens"] = new SkinEntry
            {
                MechName = "聚焦透镜阵", MechArtId = "org/lens@mech",
                CosmicName = "引力透镜", CosmicArtId = "org/lens@cosmic",
            },
            ["org_lyso"] = new SkinEntry
            {
                MechName = "引爆舱", MechArtId = "org/lyso@mech",
                CosmicName = "超新星芯", CosmicArtId = "org/lyso@cosmic",
            },
            ["org_aqua"] = new SkinEntry
            {
                MechName = "液冷夹层", MechArtId = "org/aqua@mech",
                CosmicName = "彗尾水雾", CosmicArtId = "org/aqua@cosmic",
            },
            ["org_ion"] = new SkinEntry
            {
                MechName = "离子推进泵", MechArtId = "org/ion@mech",
                CosmicName = "磁暴电离层", CosmicArtId = "org/ion@cosmic",
            },
            ["org_radiator"] = new SkinEntry
            {
                MechName = "散热鳍片", MechArtId = "org/radiator@mech",
                CosmicName = "星际辐射屏", CosmicArtId = "org/radiator@cosmic",
            },
            ["org_breaker"] = new SkinEntry
            {
                MechName = "过载保险闸", MechArtId = "org/breaker@mech",
                CosmicName = "超载湮灭阀", CosmicArtId = "org/breaker@cosmic",
            },
            ["org_synapse"] = new SkinEntry
            {
                MechName = "反馈电路", MechArtId = "org/synapse@mech",
                CosmicName = "因果回响场", CosmicArtId = "org/synapse@cosmic",
            },
            ["org_emitter"] = new SkinEntry
            {
                MechName = "主炮发射口", MechArtId = "org/emitter@mech",
                CosmicName = "星炬喷发口", CosmicArtId = "org/emitter@cosmic",
            },
            ["org_cilia"] = new SkinEntry
            {
                MechName = "近战撞角", MechArtId = "org/cilia@mech",
                CosmicName = "彗核尖刺", CosmicArtId = "org/cilia@cosmic",
            },
            ["org_spine"] = new SkinEntry
            {
                MechName = "反击棘刺甲", MechArtId = "org/spine@mech",
                CosmicName = "陨石棘刺环", CosmicArtId = "org/spine@cosmic",
            },
            ["org_slime"] = new SkinEntry
            {
                MechName = "润滑油膜", MechArtId = "org/slime@mech",
                CosmicName = "星际油雾层", CosmicArtId = "org/slime@cosmic",
            },
            ["org_receptor"] = new SkinEntry
            {
                MechName = "感应基座", MechArtId = "org/receptor@mech",
                CosmicName = "引力感应场", CosmicArtId = "org/receptor@cosmic",
            },
            ["org_insulate"] = new SkinEntry
            {
                MechName = "隔热管道", MechArtId = "org/insulate@mech",
                CosmicName = "星际绝热层", CosmicArtId = "org/insulate@cosmic",
            },
            ["org_valve"] = new SkinEntry
            {
                MechName = "单向闸阀", MechArtId = "org/valve@mech",
                CosmicName = "虫洞单向门", CosmicArtId = "org/valve@cosmic",
            },
            ["org_filter"] = new SkinEntry
            {
                MechName = "筛选滤仓", MechArtId = "org/filter@mech",
                CosmicName = "星尘筛选场", CosmicArtId = "org/filter@cosmic",
            },
        };

        public static IReadOnlyCollection<string> Keys => _skins.Keys;

        /// <summary>id 未收录、或 Cell 层遇 axis_void（宪法"二期"未启用）时返回 null。</summary>
        public static string GetDisplayName(string id, SkinTier tier)
        {
            if (tier == SkinTier.Cell)
            {
                if (id != null && id.StartsWith("org_"))
                {
                    return OrganelleCatalog.Get(id)?.DisplayName;
                }
                return _skins.TryGetValue(id, out var axisEntry) ? axisEntry.CellName : null;
            }

            if (!_skins.TryGetValue(id, out var entry))
            {
                return null;
            }
            return tier == SkinTier.Mech ? entry.MechName : entry.CosmicName;
        }

        /// <summary>id 未收录、或 Cell 层遇 axis_void 时返回 null。占位字符串，不接 LoadAssetAsync。</summary>
        public static string GetArtId(string id, SkinTier tier)
        {
            if (tier == SkinTier.Cell)
            {
                if (id != null && id.StartsWith("org_"))
                {
                    return OrganelleCatalog.Get(id)?.ArtId;
                }
                return _skins.TryGetValue(id, out var axisEntry) ? axisEntry.CellArtId : null;
            }

            if (!_skins.TryGetValue(id, out var entry))
            {
                return null;
            }
            return tier == SkinTier.Mech ? entry.MechArtId : entry.CosmicArtId;
        }
    }
}
