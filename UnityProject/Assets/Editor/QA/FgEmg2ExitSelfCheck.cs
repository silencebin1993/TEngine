using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-E2E-01 阶段 2 出口（LANDING-PLAN 6.3）：
    /// A 额外门禁（批次服务退役、豁免 0、编译器不按内容 ID 判断、schema 仍 2、快照格式 ≥ 17、正式阶段 2）；
    /// R IC-REQ-022 反向场景 + 斑块容量满 + 旧档读入：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；
    /// C FG-GAP-169：标记器 / 重炮 / 散热鳍按声明与组件行判，回滚（stage 0）同样认得；
    /// D FG-GAP-175：右键受伤友军 = 修复（真家园、真内核，只派维修机；没受伤 / 没维修机 / 暂停的负向）。
    /// </summary>
    public static class FgEmg2ExitSelfCheck
    {
        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private static void Line(string s) => _report.AppendLine(s);

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                _fail++;
                Line("  ✗ " + what);
            }
        }

        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[阶段 2 出口] EMG2-E2E-01：额外门禁 / 反向场景映射 / 编译器按声明判定 / 右键修复");
            foreach (Action a in new Action[] { CheckGates, CheckReverseScenarios, CheckDeclaredKinds, CheckRepairCommand })
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Expect(false, $"{a.Method.Name} 抛异常：{e}");
                }
            }
            Line($"  · [阶段 2 出口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void CheckGates()
        {
            Line("  · A. 额外门禁（LANDING-PLAN 6.3 阶段 2）");
            Type batch = typeof(CombatSite).Assembly.GetTypes().FirstOrDefault(t => t.Name == "ReactionBatchService");
            Expect(batch == null, "FG10-REACT-01 回改：ReactionBatchService 已删除（批次开放 → 首次发现）");
            Expect(FgEmergenceRuleAuditSelfCheck.Exemptions.Length == 0, "装配反应豁免数 = 0");
            Expect(EmgCatalog.EffectiveStage >= 2, $"正式表 emg.stage 生效 ≥ 2（现 {EmgCatalog.EffectiveStage}；EMG3-CHS-01 起缺省 3）");
            Expect(CampaignSaveService.CurrentSchemaVersion == 2, $"战役存档 schema 保持 2（FGR-EMG-212，现 {CampaignSaveService.CurrentSchemaVersion}）");
            Expect(CombatConst.FormatVersion >= 17, $"内核快照格式 ≥ 17（现 {CombatConst.FormatVersion}）");
        }

        private static void CheckReverseScenarios()
        {
            Line("  · R. 反向场景：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var map = new (string What, string Segment, string File, string Label)[]
            {
                ("斑块容量满：软上限 / 总上限降级、计数、调试层可见", "FgEmgMatterSelfCheck", "FgEmgMatterSelfCheck.cs", "容量 30（世界源保留 4）"),
                ("旧档读入：v2 存档 schema 仍 2、新字段取缺省", "FgEmgDiscoverySelfCheck", "FgEmgDiscoverySelfCheck.cs", "D1 FGT-EMG-041"),
                ("旧档读入：旧快照 14 / 15 / 16 读入", "FgEmgMigrationSelfCheck", "FgEmgMigrationSelfCheck.cs", "G. 旧快照 14 / 15 / 16"),
                ("旧档读入：没有留存字段的旧档", "FgEmgPersistSelfCheck", "FgEmgPersistSelfCheck.cs", "N4 v2 旧档"),
                ("迁移等价：液洼遇燃烧的敌人整片反应", "FgEmgMigrationSelfCheck", "FgEmgMigrationSelfCheck.cs", "B1 液洼遇燃烧"),
                ("迁移等价：洗礼塔灌注", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "B2b FGT-EMG-035"),
                ("资源不足：刻印废料不足零扣款", "FgSignalCoreSelfCheck", "FgSignalCoreSelfCheck.cs", "废料不足“"),
                ("目标死亡：寻的目标阵亡重选 / 直飞", "FgEmgGrammarSelfCheck", "FgEmgGrammarSelfCheck.cs", "目标阵亡重选"),
                ("目标死亡：洗礼塔场上全灭复位", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "F2 负向“全灭”"),
                ("断链：桥接导线一端阵亡立即断开", "FgEmgReclaimSelfCheck", "FgEmgReclaimSelfCheck.cs", "一端（目标单位）阵亡"),
                ("暂停与倍速：区域节拍 0.5x～3x", "FgEmgMigrationSelfCheck", "FgEmgMigrationSelfCheck.cs", "J. 0.5x～3x 与暂停"),
                ("路径失败：固化胶块堵死通路 → 警告可定位", "FgEmgPersistSelfCheck", "FgEmgPersistSelfCheck.cs", "L2 固化胶块堵死"),
                ("存读档与确定性（FGT-EMG-040、042）", "FgEmergenceFuzzSelfCheck", "FgEmergenceFuzzSelfCheck.cs", "零崩溃"),
                ("规则表形状（FGT-EMG-030）", "FgEmgReact2SelfCheck", "FgEmgReact2SelfCheck.cs", "FGT-EMG-030"),
            };
            string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            foreach (var m in map)
            {
                string path = Path.Combine(repo, Qa + m.File);
                // 影子工程没有根仓路径时退回工程内的同一文件
                string text = File.Exists(path) ? File.ReadAllText(path) : File.ReadAllText(Path.Combine(Application.dataPath, "Editor/QA", m.File));
                Expect(segments.Contains(m.Segment) && text.Contains(m.Label), $"{m.What}（{m.Segment}：“{m.Label}”）");
            }
        }

        private static void CheckDeclaredKinds()
        {
            Line("  · C. FG-GAP-169：编译器按声明与组件行判定，不认内容 ID");
            Expect(EmgCatalog.OwnerDeclaresWrite("func_marker", "evt.marked") && !EmgCatalog.OwnerDeclaresWrite("func_dash", "evt.marked"),
                "写 evt.marked 的功能组件算标记器；冲刺器不算");
            Expect(EmgRuleCatalog.IsHeavyProjectile(ComponentCatalog.CompCannonId) && !EmgRuleCatalog.IsHeavyProjectile(ComponentCatalog.CompGunId),
                "细分 = 重型射弹的主组件走重炮状态机；连射器不走");
            Expect(EmgCatalog.OwnerDeclaresWrite("struct_fin", "prop.heat") && !EmgCatalog.OwnerDeclaresWrite("struct_armor", "prop.heat"),
                "写 prop.heat 的结构模块算散热；装甲不算");
            Expect(!EmgCatalog.OwnerDeclaresWrite(null, "evt.marked") && !EmgCatalog.OwnerDeclaresWrite(string.Empty, "prop.heat"), "负向：空槽不算");
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            BlueprintCircuitPreview r = BlueprintCircuitCompiler.CompilePreview(board);
            Expect(r.HasCannonPrimary && !r.HasMarkerFunction && !r.HasHeatSinkStructure,
                $"真编译一块重炮电路板：重炮 {r.HasCannonPrimary}、标记 {r.HasMarkerFunction}、散热 {r.HasHeatSinkStructure}");
        }

        private static int Spawn(CampaignState s, string blueprint, Vector2 at, float hp, float maxHp)
        {
            BlueprintVersionRecord v = s.BlueprintRecords.First(b => b.BlueprintId == blueprint).Versions[0];
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, blueprint, HomeValleyLayout.RegionId, at, hp, maxHp, "Player", 1, v.CompileSignature);
            if (!r.Success)
            {
                throw new InvalidOperationException($"夹具出厂失败：{r.Message}");
            }
            MachineLoadoutRegistry.Register(s, r.LogicId, blueprint, 1);
            return r.LogicId;
        }

        private static void AddBlueprint(CampaignState s, string id, string primary)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, primary, null, null, Array.Empty<string>());
            var rec = new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } };
            s.BlueprintRecords = s.BlueprintRecords.Where(b => b.BlueprintId != id).Append(rec).ToArray();
        }

        private static bool HasTarget(CombatSite home, int logicId, CombatCommandKind kind, int target) =>
            home.TryGetMachineUnit(logicId, out int u) && home.TryGetCommand(u, out CombatCommand c) && c.Kind == kind && (target == 0 || c.Target == target);

        private static float Hp(CombatSite home, int logicId) =>
            home.TryGetMachineUnit(logicId, out int u) && home.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Health : -1f;

        private static void CheckRepairCommand()
        {
            Line("  · D. FG-GAP-175：编队右键受伤友军 = 修复");
            CampaignState s = F.NewWorld(9202);
            AddBlueprint(s, "emg2x-repair", ComponentCatalog.CompRepairBeamId);
            AddBlueprint(s, "emg2x-gun", ComponentCatalog.CompGunId);
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 c = core + new Vector2(-6f, -8f);
            int repair = Spawn(s, "emg2x-repair", c, 100f, 100f);
            int gun = Spawn(s, "emg2x-gun", c + new Vector2(1.5f, 0f), 100f, 100f);
            int hurt = Spawn(s, "emg2x-gun", c + new Vector2(0f, 3f), 40f, 100f);
            int whole = Spawn(s, "emg2x-gun", c + new Vector2(5f, 3f), 100f, 100f);
            WorldSimulation.StepMany(3);
            HomeValleyController hv = WorldSimulation.Home;
            CombatSite home = hv.Combat;
            home.TryGetMachineUnit(hurt, out int uHurt);
            home.TryGetMachinePosition(hurt, out Vector2 pHurt);
            home.TryGetMachinePosition(whole, out Vector2 pWhole);
            Expect(home.CanMachineRepair(repair) && !home.CanMachineRepair(gun), "维修束机器算修复出口，连射器不算");

            // 负向：选中里没有维修机 → 不接修复令，右键友军所在的地面还是移动
            hv.SquadCommands.DebugSelectMany(new[] { gun });
            hv.SquadCommands.IssueContextCommand(Vector3.zero, pHurt, false);
            Expect(!HasTarget(home, gun, CombatCommandKind.Attack, 0) && HasTarget(home, gun, CombatCommandKind.Move, 0), "负向：只选连射器右键受伤友军 → 移动，不攻击友军");
            hv.SquadCommands.ClearSelection();

            // 负向：友军没受伤 → 维修机移动过去，不算修复
            hv.SquadCommands.DebugSelectMany(new[] { repair });
            hv.SquadCommands.IssueContextCommand(Vector3.zero, pWhole, false);
            Expect(HasTarget(home, repair, CombatCommandKind.Move, 0), "负向：友军满耐久 → 维修机移动");
            home.TryGetMachineUnit(repair, out int uRepair);
            home.ClearCommand(uRepair);
            hv.SquadCommands.ClearSelection();

            // 暂停中下令 = 待执行，恢复后才修
            float hp0 = Hp(home, hurt);
            hv.SquadCommands.DebugSelectMany(new[] { repair, gun });
            home.TryGetMachineUnit(gun, out int uGun);
            home.ClearCommand(uGun);
            hv.SquadCommands.IssueContextCommand(Vector3.zero, pHurt, true);
            Expect(HasTarget(home, repair, CombatCommandKind.Attack, uHurt), "暂停中右键受伤友军：维修机写进修复命令（目标 = 友军单位）");
            Expect(!HasTarget(home, gun, CombatCommandKind.Attack, 0), "同时选中的连射器不接这条令（不朝友军开火）");
            Expect(Mathf.Approximately(Hp(home, hurt), hp0), "还没走步：耐久不变");

            // 恢复后维修机开过去修（走真实内核）
            bool healed = F.StepUntil(() => Hp(home, hurt) > hp0, 30);
            Expect(healed, $"恢复后维修机把友军从 {hp0:0.#} 修到 {Hp(home, hurt):0.#}");
            hv.SquadCommands.ClearSelection();

            WorldSimulation.UnloadAll();
        }
    }
}
