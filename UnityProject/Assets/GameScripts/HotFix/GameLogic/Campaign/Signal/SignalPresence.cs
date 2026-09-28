using System;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Localization;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// FG1-SIG-01（FG01 FGR-SIG-001 玩家是信号、FGR-SIG-002 同一时刻只有一个焦点）：信号现在在哪里。
    ///
    /// 信号同一时刻只在一处：归还核心，或者玩家正在接入的那一台机器。本 Story 读的是 Demo 已有的接管
    /// （被观察地点里唯一的受控机器，<see cref="HomeValleyController.PossessedMachineLogicId"/> 等）；
    /// FG1-SIG-03 把接管升级为“接入”（插入固件、重编译）时，信号位置的真相与存档字段在那条 Story 加到信号核状态域里，
    /// 本类改为读那个字段，HUD 不用改。
    /// 每帧只读 O(1) 个字段，与机器数量无关。
    /// </summary>
    public static class SignalPresence
    {
        /// <summary>自检注入“信号在哪台机器里”（0 = 归还核心）；为 null 时读真实世界。</summary>
        public static Func<int> MachineOverrideForTests;

        /// <summary>信号所在机器的 LogicId；0 = 在归还核心。</summary>
        public static int CurrentMachineLogicId
        {
            get
            {
                if (MachineOverrideForTests != null)
                {
                    return MachineOverrideForTests();
                }
                int? id = WorldView.ObservedSite switch
                {
                    HomeValleyController h => h.PossessedMachineLogicId,
                    FracturedCityController f => f.PossessedMachineLogicId,
                    FoundryOutpostController o => o.PossessedMachineLogicId,
                    _ => null,
                };
                return id ?? 0;
            }
        }

        public static bool AtCore => CurrentMachineLogicId == 0;

        /// <summary>机器的玩家可见标识：型号 + 编号（“ERC-003 #5”）。</summary>
        public static string MachineLabel(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return "#" + logicId;
            }
            string model = string.IsNullOrEmpty(rec.ChassisId) ? string.Empty : rec.ChassisId.ToUpperInvariant().Replace('_', '-') + " ";
            return model + "#" + rec.DisplayNumber;
        }

        /// <summary>HUD 文字：“信号：归还核心” / “信号：ERC-003 #5”。</summary>
        public static string LocationText()
        {
            int id = CurrentMachineLogicId;
            return id == 0 ? GameText.Get("signal.hud.at_core") : GameText.Format("signal.hud.in_machine", MachineLabel(id));
        }

        public static void ResetForTests()
        {
            MachineOverrideForTests = null;
        }
    }
}
