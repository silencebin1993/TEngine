using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.EditorTools.CellArt;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-011：功能绑定页只读反显 Cell Art 概念图/三视图（CELLART-LINK.md 映射表）。
    /// 只读 <see cref="CellArtRegistryService"/>；不写 registry、不把 Art 路径写进 feature-art-catalog.json。
    /// FeatureArt 目录无 asmdef（Assembly-CSharp-Editor 默认程序集），CellArt 的 asmdef
    /// autoReferenced=true 故本类可直接引用 CellArt 类型，未出现 CELLART-LINK 提到的隔离问题。</summary>
    public static class FeatureArtCellArtBridge
    {
        public const string PlayerCellArtId = "player_base_core";

        static readonly Dictionary<string, string> OrganMap = new Dictionary<string, string>
        {
            ["org_phago"] = "mod_devour_maw",
            ["org_cilia"] = "mod_agile_flagella",
            ["org_spine"] = "mod_corrupt_thorn_patch",
            ["org_bud"] = "mod_spore_cluster",
            ["org_mycelium"] = "mod_nest_anchor",
        };

        static readonly Dictionary<string, string> ShapeMap = new Dictionary<string, string>
        {
            ["Bolt"] = "proj_spike_bolt",
            ["Beam"] = "proj_volt_orb",
            ["Spore"] = "proj_spore_bud",
            ["Field"] = "proj_acid_drop",
        };

        static readonly Dictionary<string, string> SummonMap = new Dictionary<string, string>
        {
            ["spore"] = "minion_spore_bud",
            ["mycelium"] = "mod_nest_anchor",
        };

        static readonly Dictionary<string, string> EnemyMap = new Dictionary<string, string>
        {
            ["vis_1"] = "enemy_blob_food",
            ["vis_2"] = "enemy_spiky_cell",
            ["vis_3"] = "enemy_cilia_sweeper",
            ["vis_4"] = "enemy_hunter",
            ["vis_6"] = "enemy_hardshell",
            ["vis_7"] = "enemy_jelly_conductive",
            ["vis_8"] = "enemy_spore_rot",
            ["vis_10"] = "enemy_spine_shooter",
            ["vis_11"] = "enemy_mycelium_pad",
            ["vis_20"] = "enemy_corpse_chunk",
            ["vis_50"] = "elite_devourer",
            ["vis_51"] = "elite_whip_king",
            ["vis_52"] = "elite_volt_hunter",
            ["vis_90"] = "boss_prokaryote_p1",
        };

        public static string OrganCellArtId(string organId) => OrganMap.TryGetValue(organId, out var id) ? id : null;
        public static string ShapeCellArtId(string shapeKey) => ShapeMap.TryGetValue(shapeKey, out var id) ? id : null;
        public static string SummonCellArtId(string key) => SummonMap.TryGetValue(key, out var id) ? id : null;
        public static string EnemyCellArtId(string visKey) => EnemyMap.TryGetValue(visKey, out var id) ? id : null;

        /// <summary>展示顺序（CELLART-LINK）：concept（与 views.hero 同路径只画一次）→ front/left/right/back → 其余 views。
        /// 无映射 / 文件缺：空态 +「打开源文件板」，不报错、不写假图。</summary>
        public static void DrawViews(string cellArtId)
        {
            if (string.IsNullOrEmpty(cellArtId))
            {
                DrawEmpty();
                return;
            }

            CellArtRegistry registry;
            try
            {
                registry = CellArtRegistryService.Load();
            }
            catch (Exception)
            {
                DrawEmpty();
                return;
            }

            var asset = registry.assets?.FirstOrDefault(a => a.id == cellArtId);
            if (asset == null)
            {
                DrawEmpty();
                return;
            }

            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<(string label, string rel)>();
            if (!string.IsNullOrEmpty(asset.concept) && shown.Add(asset.concept))
            {
                ordered.Add(("concept", asset.concept));
            }

            foreach (var key in new[] { "front", "left", "right", "back" })
            {
                if (asset.views != null && asset.views.TryGetValue(key, out var rel) && !string.IsNullOrEmpty(rel) && shown.Add(rel))
                {
                    ordered.Add((key, rel));
                }
            }

            if (asset.views != null)
            {
                foreach (var kv in asset.views)
                {
                    if (!string.IsNullOrEmpty(kv.Value) && shown.Add(kv.Value))
                    {
                        ordered.Add((kv.Key, kv.Value));
                    }
                }
            }

            if (ordered.Count == 0)
            {
                DrawEmpty();
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (var (label, rel) in ordered)
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(CellArtRegistryService.AssetPathOf(rel));
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(78)))
                    {
                        EditorGUILayout.LabelField(label, EditorStyles.centeredGreyMiniLabel);
                        Sirenix.Utilities.Editor.SirenixEditorFields.UnityPreviewObjectField(
                            GUIContent.none, tex, typeof(Texture2D), false, 72,
                            Sirenix.Utilities.Editor.ObjectFieldAlignment.Center);
                    }
                }
            }
        }

        static void DrawEmpty()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("源文件板无此图。", MessageType.None);
                if (GUILayout.Button("打开源文件板", GUILayout.Width(90)))
                {
                    CellArtBoardWindow.Open();
                }
            }
        }
    }
}
