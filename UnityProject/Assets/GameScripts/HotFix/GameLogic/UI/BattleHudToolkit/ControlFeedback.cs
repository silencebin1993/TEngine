using BinGames.Sim;

namespace GameLogic.UI.Battle
{
    /// <summary>
    /// M4-R00-02 队列⑥-28（`IC-REQ-013` HUD 文案分支）：把控制权切换/变更/不可用三类场景的
    /// 原始枚举翻成 <see cref="FourPartFeedback"/>。原先 `BattleHudToolkit` 里的
    /// `DescribeControlSwitch`/`DescribeControlChange`/`DescribeNoControl` 三个私有方法搬到这里、
    /// 改成公开纯函数——纯函数才能被 `CellFrameworkValidate` 直接断言文案分支，不用假装点 HUD。
    /// </summary>
    public static class ControlFeedback
    {
        /// <summary>玩家主动请求切换控制目标（Tab/点选）的结果。</summary>
        public static FourPartFeedback ForSwitch(ControlRequestResult result, int candidateCount, SimEntityId current)
        {
            switch (result)
            {
                case ControlRequestResult.Success:
                    return new FourPartFeedback($"已切换至 #{current.Value}", "控制中",
                        $"信号范围内还有 {candidateCount} 个候选", "Tab 循环切换下一个");
                case ControlRequestResult.AlreadyControlled:
                    return new FourPartFeedback("目标已是当前控制对象", "控制未变",
                        "本次请求没有产生任何效果", "选择场上另一名友军作为目标");
                case ControlRequestResult.SimulationNotRunning:
                    return new FourPartFeedback("战斗模拟尚未运行", "无法切换",
                        "本次请求被忽略", "等待关卡加载完成后重试");
                case ControlRequestResult.InvalidTarget:
                    return new FourPartFeedback("目标实体无效", "切换失败",
                        "控制权保持原状", "重新用 Tab/点选选择一个有效目标");
                case ControlRequestResult.TargetNotFound:
                    return new FourPartFeedback("目标已不在场上", "切换失败",
                        "控制权保持原状", "选择当前存活的友军");
                case ControlRequestResult.TargetDead:
                    return new FourPartFeedback("目标已死亡", "切换失败",
                        "控制权保持原状", "选择其它存活友军");
                case ControlRequestResult.TargetNotFriendly:
                    return new FourPartFeedback("目标不是友军", "切换失败",
                        "控制权保持原状", "只能选择友军阵营的单位");
                case ControlRequestResult.OutOfSignalRange:
                    return new FourPartFeedback("目标超出信号范围", "切换失败",
                        "控制权保持原状", "靠近目标，或等待信号范围内出现新候选");
                case ControlRequestResult.CooldownActive:
                    return new FourPartFeedback("切换冷却中", "暂时不可切换",
                        "控制权保持原状", "稍候片刻后再次尝试切换");
                case ControlRequestResult.CurrentUnitUnavailable:
                    return new FourPartFeedback("当前控制目标不可用", "切换失败",
                        "控制权保持原状", "重新选择一个存活的友军");
                default:
                    return new FourPartFeedback("范围内没有可切换友军", "切换失败",
                        "控制权保持原状", "靠近友军，或等待新的候选出现");
            }
        }

        /// <summary>非玩家主动请求触发的控制权变更（死亡回弹、载体消失、读档恢复、主动放下意识）。</summary>
        public static FourPartFeedback ForChange(ControlChangeReason reason, SimEntityId current)
        {
            switch (reason)
            {
                case ControlChangeReason.ControlledDeath:
                    return current.IsValid
                        ? new FourPartFeedback("受控身体死亡", "意识已回弹",
                            $"已自动接管 #{current.Value}", "继续操作新身体，或按 Tab 换人")
                        : new FourPartFeedback("受控身体死亡", "意识无处可去",
                            "暂时没有可接管的友军", "等待新的友军出现后再接管");
                case ControlChangeReason.ControlledRemoved:
                    return current.IsValid
                        ? new FourPartFeedback("受控载体消失", "已转移控制",
                            $"已自动接管 #{current.Value}", "继续操作新身体，或按 Tab 换人")
                        : new FourPartFeedback("受控载体消失", "失去载体",
                            "暂时没有可接管的友军", "等待新的友军出现后再接管");
                case ControlChangeReason.Restored:
                    return current.IsValid
                        ? new FourPartFeedback("读档/重进场景后恢复", "意识已恢复",
                            $"已接管 #{current.Value}", "继续操作，无需额外动作")
                        : new FourPartFeedback("读档/重进场景后恢复", "恢复失败",
                            "暂时没有可接管的友军", "接管任意一具友军身体");
                case ControlChangeReason.Released:
                    // M1-06 既有缺口：此前这条落进 default，被显示成"控制目标丢失"——
                    // 但这是玩家主动放下意识进战略视角，身体还好好站在场上，不是异常，
                    // 见 ControlChangeReason.Released 本身的类型注释。
                    return new FourPartFeedback("主动放下意识", "无受控实体（正常）",
                        "可在战略视角指挥全军", "接管任意一具友军身体重新直控");
                default:
                    return current.IsValid
                        ? new FourPartFeedback("Tab 切换", "控制中",
                            $"当前控制 #{current.Value}", "继续操作，或按 Tab 换人")
                        : new FourPartFeedback("无控制权变更来源信息", "无受控实体",
                            "暂时没有可接管的友军", "接管任意一具友军身体");
            }
        }

        /// <summary>没有任何反馈计时正在播报时，"当前控制状态"这一行常驻显示什么。</summary>
        public static FourPartFeedback ForAvailability(ControlAvailability availability)
        {
            switch (availability)
            {
                case ControlAvailability.Suspended:
                    return new FourPartFeedback("信号重连中", "宽限期等待恢复",
                        "受控身体暂时解析不到，稍后会自动恢复或回弹", "原地等待，不需要手动操作");
                case ControlAvailability.Released:
                    // 同 ForChange(Released,...) 的既有 bug：此前与 None 共用"控制目标丢失"，
                    // 而这是玩家进战略视角后的**常态**，几乎每局都会频繁经过这个分支。
                    return new FourPartFeedback("处于战略视角", "无受控实体（正常）",
                        "可指挥全军但不能直接操作单个身体", "接管任意一具友军身体切回直控");
                default:
                    return new FourPartFeedback("没有可用的受控身体", "无受控实体",
                        "无法直控操作，仅能用战略视角指挥", "等待新的友军出现后再接管");
            }
        }
    }
}
