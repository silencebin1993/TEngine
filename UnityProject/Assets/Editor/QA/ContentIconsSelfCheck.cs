using System;
using System.Collections.Generic;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Settings;
using GameLogic.UI.Common;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8-CONTENT-01 / DEBT-ER8CONTENT01-01 图标半边 / AC-ACC-002：内容图标与 3D 状态标记的回归闸门——
    /// 每个 IconId 都有真实贴图且导入设置正确、查表覆盖机型编号/旧等价 ID/基元芯片、各面板行模板与蓝图
    /// 编辑器槽位真的留了图标位、建筑状态 → 标记映射覆盖全部状态且正常运转不显示、色盲安全配色真的换色。
    /// 并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class ContentIconsSelfCheck
    {
        private const string IconFolder = "Assets/GameRes/Raw/UI/Icons/";

        private static readonly (string Uxml, string[] Names)[] IconSlots =
        {
            ("Assets/GameRes/Raw/UI/Factory/templates/FactoryQueueRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/Analysis/templates/AnalysisQueueRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/Expedition/templates/ExpeditionMachineRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/Expedition/templates/ExpeditionReturnRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/Victory/templates/VictoryMachineRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/PrimitiveCraft/templates/CraftQueueRow.uxml", new[] { "Icon" }),
            ("Assets/GameRes/Raw/UI/CircuitBoard/CircuitBoardPanel.uxml",
                new[] { "Slot0Icon", "Slot1Icon", "Slot2Icon", "Slot3Icon", "Slot4Icon", "Slot5Icon", "Slot6Icon", "Slot7Icon", "Slot8Icon" }),
        };

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：内容图标与状态标记")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[图标] 内容图标与 3D 状态标记（ER8-CONTENT-01 / AC-ACC-002）");
            bool savedCvd = GameSettings.ColorblindSafeIconsEnabled;
            try
            {
                CheckIconAssets();
                CheckIconLookup();
                CheckIconSlots();
                CheckBuildingStateBadges();
                CheckColorblindPalette();
            }
            catch (Exception e)
            {
                Fail($"图标自检抛异常：{e}");
            }
            finally
            {
                if (GameSettings.ColorblindSafeIconsEnabled != savedCvd)
                {
                    GameSettings.SetColorblindSafeIconsEnabled(savedCvd);
                }
            }
            return _fail;
        }

        private static void CheckIconAssets()
        {
            List<string> ids = ContentIcons.AllIconIds();
            var missing = new List<string>();
            var badImport = new List<string>();
            foreach (string id in ids)
            {
                string path = IconFolder + id + ".png";
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null)
                {
                    missing.Add(id);
                    continue;
                }
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.mipmapEnabled || importer.wrapMode != TextureWrapMode.Clamp || !importer.alphaIsTransparency)
                {
                    badImport.Add(id);
                }
            }
            Expect(missing.Count == 0, missing.Count == 0
                ? $"全部 {ids.Count} 个图标（35 条内容 + 2 件基元芯片 + 4 个建筑状态）都有真实贴图"
                : "图标贴图缺失：" + string.Join("、", missing));
            Expect(badImport.Count == 0, badImport.Count == 0
                ? "图标导入设置统一（无 mipmap、边缘钳制、透明通道）"
                : "图标导入设置不对：" + string.Join("、", badImport));

            var noIcon = new List<string>();
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                if (string.IsNullOrEmpty(def.IconId))
                {
                    noIcon.Add(def.Id);
                }
            }
            Expect(noIcon.Count == 0, noIcon.Count == 0
                ? $"内容目录 {MechanicalContentFacade.All.Count} 条都登记了 IconId"
                : "内容缺 IconId：" + string.Join("、", noIcon));
        }

        private static void CheckIconLookup()
        {
            (string Input, string Expected)[] cases =
            {
                (HomeValleyLayout.Erc001ChassisId, "icon_chassis_wheel"),
                (HomeValleyLayout.Erc003ChassisId, "icon_chassis_track"),
                (ChassisCatalog.ChassisHoverId, "icon_chassis_hover"),
                (ComponentCatalog.CompCannonId, "icon_comp_cannon"),
                ("organ_focus", "icon_chip_focus"),
                ("organ_focus_plus", "icon_chip_focus_plus"),
                (EnemyCatalog.JammerId, "icon_enemy_jammer"),
                ("not_a_content_id", null),
            };
            var wrong = new List<string>();
            foreach ((string input, string expected) in cases)
            {
                string actual = ContentIcons.IconIdFor(input);
                if (actual != expected)
                {
                    wrong.Add($"{input}→{actual ?? "null"}（应为 {expected ?? "null"}）");
                }
            }
            // 旧基元等价 ID（LegacyFacadeId）也要能查到对应机械内容的图标。
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                if (!string.IsNullOrEmpty(def.LegacyFacadeId) && ContentIcons.IconIdFor(def.LegacyFacadeId) == null)
                {
                    wrong.Add($"旧等价 ID {def.LegacyFacadeId} 查不到图标");
                }
            }
            Expect(wrong.Count == 0, wrong.Count == 0
                ? "图标查表覆盖机型编号、内容 ID、旧等价 ID、基元芯片；未知内容返回空（只显示文字，不出破图）"
                : "图标查表错误：" + string.Join("；", wrong));
        }

        private static void CheckIconSlots()
        {
            var problems = new List<string>();
            foreach ((string uxml, string[] names) in IconSlots)
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxml);
                if (vta == null)
                {
                    problems.Add($"{uxml} 加载失败");
                    continue;
                }
                VisualElement tree = vta.Instantiate();
                foreach (string name in names)
                {
                    VisualElement icon = tree.Q<VisualElement>(name);
                    if (icon == null || !icon.ClassListContains("mw-icon"))
                    {
                        problems.Add($"{System.IO.Path.GetFileName(uxml)} 缺图标位 {name}");
                    }
                    else if (icon.pickingMode != PickingMode.Ignore)
                    {
                        problems.Add($"{System.IO.Path.GetFileName(uxml)} 的 {name} 会拦截点击");
                    }
                }
            }
            Expect(problems.Count == 0, problems.Count == 0
                ? "装配站/解析/合成/远征出发/撤离/胜利页行模板与蓝图编辑器 9 个槽位都有不拦截点击的图标位"
                : "图标位问题：" + string.Join("；", problems));
        }

        private static void CheckBuildingStateBadges()
        {
            var wrong = new List<string>();

            void Case(string label, string typeId, BuildingConstructionState construction, BuildingPowerState power, string expected)
            {
                var b = new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + typeId,
                    BuildingTypeId = typeId,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = construction,
                    PowerState = power,
                };
                string actual = HomeValleyController.StateIconFor(b);
                if (actual != expected)
                {
                    wrong.Add($"{label}：{actual ?? "无"}（应为 {expected ?? "无"}）");
                }
            }

            string station = HomeValleyLayout.BuildingTypeAssemblyStation;
            Case("装配站损坏", station, BuildingConstructionState.Damaged, BuildingPowerState.Powered, ContentIcons.StateDamaged);
            Case("装配站欠电", station, BuildingConstructionState.Operational, BuildingPowerState.Brownout, ContentIcons.StateBrownout);
            Case("装配站出口堵塞", station, BuildingConstructionState.Operational, BuildingPowerState.OutputBlocked, ContentIcons.StateBlocked);
            Case("装配站未接电", station, BuildingConstructionState.Operational, BuildingPowerState.Unpowered, ContentIcons.StateUnpowered);
            Case("装配站主动关停", station, BuildingConstructionState.Disabled, BuildingPowerState.Powered, ContentIcons.StateUnpowered);
            Case("装配站正常运转", station, BuildingConstructionState.Operational, BuildingPowerState.Powered, null);
            Case("发电机运转（电源不标断电）", HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable, null);
            Case("发电机损坏", HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Damaged, BuildingPowerState.NotApplicable, ContentIcons.StateDamaged);

            var distinct = new HashSet<string> { ContentIcons.StateDamaged, ContentIcons.StateBrownout, ContentIcons.StateBlocked, ContentIcons.StateUnpowered };
            Expect(wrong.Count == 0 && distinct.Count == 4, wrong.Count == 0
                ? "建筑状态 → 头顶形状标记：损坏/欠电/出口堵塞/断电四种互不相同，正常运转与电源建筑不打扰（AC-ACC-002 供电不只靠颜色）"
                : "建筑状态标记错误：" + string.Join("；", wrong));
        }

        private static void CheckColorblindPalette()
        {
            var damaged = new BuildingRecord { BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation, ConstructionState = BuildingConstructionState.Damaged };
            var powered = new BuildingRecord { BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation, ConstructionState = BuildingConstructionState.Operational, PowerState = BuildingPowerState.Powered };
            var generator = new BuildingRecord { BuildingTypeId = HomeValleyLayout.BuildingTypeGenerator, ConstructionState = BuildingConstructionState.Operational, PowerState = BuildingPowerState.NotApplicable };

            GameSettings.SetColorblindSafeIconsEnabled(false);
            Color damagedNormal = HomeValleyController.ColorForBuilding(damaged);
            Color poweredNormal = HomeValleyController.ColorForBuilding(powered);
            GameSettings.SetColorblindSafeIconsEnabled(true);
            Color damagedCvd = HomeValleyController.ColorForBuilding(damaged);
            Color poweredCvd = HomeValleyController.ColorForBuilding(powered);
            Color generatorCvd = HomeValleyController.ColorForBuilding(generator);

            Expect(damagedCvd != damagedNormal && poweredCvd != poweredNormal,
                "色盲安全图标开启 → 建筑状态改用 Okabe-Ito 色板（损坏朱红 / 运转蓝绿），不再是红绿对立");
            Expect(generatorCvd == poweredCvd, "运转中的发电机显示“在工作”色，不再被涂成“未接电”的灰色");
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
