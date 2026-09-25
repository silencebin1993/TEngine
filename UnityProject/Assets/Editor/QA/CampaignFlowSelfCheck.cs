using System;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8 收尾：战役流程层面的回归闸门（读档恢复到哪个区域等“玩家正常操作就会碰到”的流程缺陷）。
    /// 并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class CampaignFlowSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：战役流程")]
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
            Line("\n[流程] 战役流程（ER8 收尾）");
            try
            {
                CheckResumeRegion();
            }
            catch (Exception e)
            {
                Fail($"流程自检抛异常：{e}");
            }
            return _fail;
        }

        /// <summary>DEBT-ER6REGION01-01：在远征区域存档退出，读档后必须回到该区域。</summary>
        private static void CheckResumeRegion()
        {
            CampaignState state = CampaignState.CreateNew("flow-selfcheck", "Standard", 3);

            state.CurrentRegionId = HomeValleyLayout.RegionId;
            state.MachineRecords = new[] { Machine(1, HomeValleyLayout.RegionId, true) };
            Expect(GameRoot.ResolveResumeRegion(state) == HomeValleyLayout.RegionId, "在归还谷地存档 → 读档回归还谷地");

            state.CurrentRegionId = FracturedCityLayout.RegionId;
            state.MachineRecords = new[] { Machine(1, FracturedCityLayout.RegionId, true), Machine(2, HomeValleyLayout.RegionId, true) };
            Expect(GameRoot.ResolveResumeRegion(state) == FracturedCityLayout.RegionId,
                "在破碎都市存档（远征队还活着）→ 读档回到破碎都市，而不是被送回归还谷地");

            state.CurrentRegionId = FoundryOutpostLayout.RegionId;
            state.MachineRecords = new[] { Machine(3, FoundryOutpostLayout.RegionId, true) };
            Expect(GameRoot.ResolveResumeRegion(state) == FoundryOutpostLayout.RegionId, "在铸造前哨外围存档 → 读档回到铸造前哨外围");

            state.MachineRecords = new[] { Machine(3, FoundryOutpostLayout.RegionId, false) };
            Expect(GameRoot.ResolveResumeRegion(state) == HomeValleyLayout.RegionId,
                "远征区域里已无存活机器 → 安全回归还谷地，不进一个没人可控的区域");

            state.CurrentRegionId = null;
            Expect(GameRoot.ResolveResumeRegion(state) == HomeValleyLayout.RegionId, "旧档没有当前区域 → 回归还谷地");
        }

        private static MachineRecord Machine(int logicId, string regionId, bool alive)
        {
            return new MachineRecord { LogicId = logicId, DisplayNumber = logicId, RegionId = regionId, IsAlive = alive, ChassisId = HomeValleyLayout.Erc001ChassisId };
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
