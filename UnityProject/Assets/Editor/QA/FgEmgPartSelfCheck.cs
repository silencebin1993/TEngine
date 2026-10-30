using System;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FW-01（FG19 FGR-EMG-193 第 2、3 条；FGT-EMG-062 阶段 1 的两项）：部件声明生效后的两条装配约束。
    /// 起真实系统：真实蓝图（BlueprintCircuitBoard 的装配接口与校验）、真实声明表（emg.stage = 1 覆盖）、真实 EmgCatalog.PartPropModify（预览与运行时同一函数）、真实内核写单位质量。
    /// A 同名重甲装第二件被拒（原因文本 emg.compile.part_duplicate）；自身位换同一件不算重复；
    /// B 旧存档蓝图里两个部件位装了同名部件：读档后蓝图校验标红（PartDuplicate）、字段与库存原样不删；
    /// C 货舱 +20%、重甲 +50% 各自生效；再加一件同写 prop.mass 的部件，合计超 +100% 的部分无效，预览写明“超出的 +X% 无效”；同名部件只算一次；
    /// D 运行时：机器质量 = 底盘档案质量 × (1 + 封顶后的加成)，写进内核单位段；emg.stage = 0 时部件声明不生效、质量回到按底盘（逐字同 EMG1-ENT-01）。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgPartSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgPartSelfCheck。
    /// </summary>
    public static class FgEmgPartSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/QA/自检/涌现部件装配约束")]
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
            Line("\n[涌现部件装配约束] FGR-EMG-193 第 2、3 条：同名部件限装 1 件、prop.* 相加封顶（EMG1-FW-01，FGT-EMG-062 阶段 1）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                EmgCatalog.ResetForTests();
                Step(CheckDuplicate);
                Step(CheckOldSave);
                Step(CheckPropSum);
                Step(CheckRuntimeMass);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            return _fail;
        }

        private static void UseStage(int stage, Action<EmgSource> edit = null)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            edit?.Invoke(src);
            EmgCatalog.OverrideForTests(src);
        }

        private static void CheckDuplicate()
        {
            Line("  · A. 同名部件每台限装 1 件（蓝图装配接口）");
            CampaignState s = CampaignState.CreateNew("emgpart-a", "Standard", 1);
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, "struct_armor", Array.Empty<string>());
            CircuitOpResult second = b.TrySetUtility(s, "struct_armor");
            Expect(!second.Success && second.Code == BlueprintCircuitBoard.PartDuplicateCode && second.Message == GameText.Get(BlueprintCircuitBoard.PartDuplicateKey)
                   && b.UtilityId == null && b.StructureId == "struct_armor",
                $"已装重甲的机器再往功能位装一件重甲：被拒（{second.Code}：{second.Message}），两个部件位原样");
            Expect(b.IsPartDuplicate("struct_armor", 2) == false && b.IsPartDuplicate("struct_armor", 1),
                "对照：重甲在自己的结构位上不算重复（换同一件不被拒）；放到别的部件位才算重复");
            Expect(GameText.Get(BlueprintCircuitBoard.PartDuplicateKey) == "同名部件每台机器限装 1 件" && GameText.Get(BlueprintCircuitBoard.PartDuplicateKey + ".fix").Length > 0,
                $"原因文本与解决办法走文本键：{GameText.Get(BlueprintCircuitBoard.PartDuplicateKey)}；{GameText.Get(BlueprintCircuitBoard.PartDuplicateKey + ".fix")}");
        }

        private static void CheckOldSave()
        {
            Line("  · B. 旧存档蓝图里两个部件位装了同名部件：读档后标红、拒绝再保存，不删库存（处理方式见证据）");
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, "struct_armor", Array.Empty<string>());
            b.UtilityId = "struct_armor"; // 模拟旧档 / 外部改动直接写进字段（正常装配接口已拒）
            CircuitValidationResult v = b.Validate();
            Expect(!v.IsValid && v.Issues.Any(i => i.Code == CircuitIssueCode.PartDuplicate) && b.UtilityId == "struct_armor" && b.StructureId == "struct_armor"
                   && b.DuplicatePartCount() == 1,
                $"校验报 {string.Join("、", v.Issues.Select(i => i.Code))}（{v.Issues.FirstOrDefault(i => i.Code == CircuitIssueCode.PartDuplicate)?.Message}），两个部件位与库存都不被删（玩家自己卸下一件即恢复）");
            b.UtilityId = null;
            Expect(!b.Validate().Issues.Any(i => i.Code == CircuitIssueCode.PartDuplicate), "卸下多出来的一件后校验不再报重复");
        }

        private static void CheckPropSum()
        {
            Line("  · C. 不同部件写同一 prop.* 的 modify 相加，合计上限 emg.part.prop_sum_cap（+100%），超出部分无效且预览可见");
            try
            {
                UseStage(1);
                float cargo = EmgCatalog.PartPropModify("prop.mass", new[] { null, "struct_cargo" }, out float rc);
                float armor = EmgCatalog.PartPropModify("prop.mass", new[] { null, "struct_armor" }, out float ra);
                float twice = EmgCatalog.PartPropModify("prop.mass", new[] { "struct_armor", "struct_armor" }, out float rt);
                float none = EmgCatalog.PartPropModify("prop.mass", new[] { "func_marker", null }, out float rn);
                Expect(Mathf.Approximately(cargo, 0.2f) && Mathf.Approximately(armor, 0.5f) && Mathf.Approximately(twice, 0.5f) && none == 0f && rn == 0f,
                    $"货舱 prop.mass +{cargo * 100:0}%、重甲 +{armor * 100:0}%；同名重甲两件只算一次 +{twice * 100:0}%；不写 prop.mass 的标记器 0");
                // 再加一件同写 prop.mass 的部件（功能位：用冲刺器代入一条 +60% 声明，FGT-EMG-062“货舱 + 重甲 + 另一件”）
                UseStage(1, src =>
                {
                    src.Decls.Add(new EmgSource.Decl
                    {
                        Id = "func_dash.9", Owner = "func_dash", OwnerKind = "component", Seq = 9, Dir = "write", Port = "prop.mass", Role = "modify", Amount = 0.6f,
                        Carriers = "all", TextKey = "emg.decl.func_dash.1", StageGate = 1,
                    });
                });
                float capped = EmgCatalog.PartPropModify("prop.mass", new[] { "func_dash", "struct_armor" }, out float raw);
                string line = EmgProgramTreeText.PartPropLine("prop.mass", "func_dash", "struct_armor");
                float cap = GridContent.TryGetTuning(EmgCatalog.PartPropCapKey, out float c) ? c : -1f;
                Expect(Mathf.Approximately(raw, 1.1f) && Mathf.Approximately(capped, 1.0f) && Mathf.Approximately(cap, 1f) && line != null && line.Contains("110") && line.Contains("10%") && line.Contains("无效"),
                    $"重甲 +50% + 另一件 +60% = +{raw * 100:0}%，封顶 +{capped * 100:0}%（emg.part.prop_sum_cap = {cap}）；装配预览：{line}");
                Expect(EmgProgramTreeText.PartPropLine("prop.mass", null, "struct_cargo") == GameText.Format("emg.part.prop_sum", GameText.Get("emg.port.prop.mass.name"), "20"),
                    $"未超上限时预览只写合计：{EmgProgramTreeText.PartPropLine("prop.mass", null, "struct_cargo")}");
                UseStage(0);
                Expect(EmgCatalog.PartPropModify("prop.mass", new[] { null, "struct_armor" }, out _) == 0f && EmgProgramTreeText.PartPropLine("prop.mass", null, "struct_armor") == null,
                    "emg.stage = 0：部件声明不生效，没有加成、预览不显示（正式流程逐字不变）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        private static void CheckRuntimeMass()
        {
            Line("  · D. 运行时：机器质量 = 底盘档案质量 × (1 + 封顶后的加成)，写进内核单位段（与预览同一函数）");
            try
            {
                UseStage(1);
                using var k = new CombatKernel(CombatSite.ConfigFromTuning(), 8);
                CombatSite.ApplyEntityProfiles(k);
                short wheel = EmgEntityProfiles.ForChassis("chassis_wheel");
                float bonus = EmgCatalog.PartPropModify("prop.mass", new[] { null, "struct_armor" }, out _);
                int u = k.Spawn(new CombatSpawn
                {
                    Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Radius = 0.9f, Speed = 4f, Health = 100f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1,
                });
                float baseMass = k.EntityProfileAt(wheel).Mass;
                k.SetUnitProfile(u, wheel, CombatSite.MachineMass(k, wheel, bonus));
                k.TryGetUnitEmg(u, out _, out EmgUnitState st);
                Expect(wheel >= 0 && baseMass > 0f && Mathf.Approximately(st.Mass, baseMass * 1.5f) && CombatSite.MachineMass(k, wheel, 0f) == 0f,
                    $"轮式底盘 {baseMass} × (1 + 重甲 {bonus * 100:0}%) = 单位段质量 {st.Mass}；没有加成时写 0（按档案推算，逐字同 EMG1-ENT-01）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
