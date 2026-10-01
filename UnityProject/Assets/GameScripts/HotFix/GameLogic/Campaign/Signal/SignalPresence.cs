using System;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Localization;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// FG1-SIG-01（FG01 FGR-SIG-001 玩家是信号、FGR-SIG-002 同一时刻只有一个焦点）：信号现在在哪里。
    ///
    /// 信号同一时刻只在一处：归还核心，或者玩家正在接入的那一台机器。FG1-SIG-03 起读信号核状态域里的
    /// <see cref="SignalCoreState.UplinkMachineLogicId"/>（存档；唯一写入口 <see cref="SignalUplinkService"/>），
    /// 接入过渡中信号还在原处（过渡结束才算接入）。
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
                return SignalUplinkService.CurrentMachine(CampaignSession.Current);
            }
        }

        public static bool AtCore => CurrentMachineLogicId == 0;

        /// <summary>机器的玩家可见标识：起了名 = “名字 #编号”，没起名 = 型号 + 编号（“ERC-003 #5”）。FG4-ECO-07 起与名册同源（<see cref="MachineNaming.Long(int)"/>）。</summary>
        public static string MachineLabel(int logicId) => MachineNaming.Long(logicId);

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
