using BinGames.Sim;
using GameLogic.Localization;

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
                    return new FourPartFeedback(GameText.Format("control.feedback.switched_to_0", current.Value), GameText.Get("control.feedback.in_control"),
                        GameText.Format("control.feedback.0_more_candidates_in", candidateCount), GameText.Get("control.feedback.tab_cycles_to_the"));
                case ControlRequestResult.AlreadyControlled:
                    return new FourPartFeedback(GameText.Get("control.feedback.already_controlling_that_target"), GameText.Get("control.feedback.control_unchanged"),
                        GameText.Get("control.feedback.this_request_had_no"), GameText.Get("control.feedback.choose_another_ally_on"));
                case ControlRequestResult.SimulationNotRunning:
                    return new FourPartFeedback(GameText.Get("control.feedback.combat_simulation_is_not"), GameText.Get("control.feedback.cannot_switch"),
                        GameText.Get("control.feedback.this_request_was_ignored"), GameText.Get("control.feedback.wait_for_the_level"));
                case ControlRequestResult.InvalidTarget:
                    return new FourPartFeedback(GameText.Get("control.feedback.invalid_target"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.pick_a_valid_target"));
                case ControlRequestResult.TargetNotFound:
                    return new FourPartFeedback(GameText.Get("control.feedback.the_target_is_no"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.pick_an_ally_that"));
                case ControlRequestResult.TargetDead:
                    return new FourPartFeedback(GameText.Get("control.feedback.the_target_is_destroyed"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.pick_another_living_ally"));
                case ControlRequestResult.TargetNotFriendly:
                    return new FourPartFeedback(GameText.Get("control.feedback.the_target_is_not"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.only_allied_units_can"));
                case ControlRequestResult.OutOfSignalRange:
                    return new FourPartFeedback(GameText.Get("control.feedback.the_target_is_out"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.move_closer_or_wait"));
                case ControlRequestResult.CooldownActive:
                    return new FourPartFeedback(GameText.Get("control.feedback.switching_is_on_cooldown"), GameText.Get("control.feedback.cannot_switch_yet"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.wait_a_moment_and"));
                case ControlRequestResult.CurrentUnitUnavailable:
                    return new FourPartFeedback(GameText.Get("control.feedback.the_current_control_target"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.pick_a_living_ally"));
                default:
                    return new FourPartFeedback(GameText.Get("control.feedback.no_ally_in_range"), GameText.Get("control.feedback.switch_failed"),
                        GameText.Get("control.feedback.control_unchanged_2"), GameText.Get("control.feedback.move_closer_to_an"));
            }
        }

        /// <summary>非玩家主动请求触发的控制权变更（死亡回弹、载体消失、读档恢复、主动放下意识）。</summary>
        public static FourPartFeedback ForChange(ControlChangeReason reason, SimEntityId current)
        {
            switch (reason)
            {
                case ControlChangeReason.ControlledDeath:
                    return current.IsValid
                        ? new FourPartFeedback(GameText.Get("control.feedback.controlled_unit_destroyed"), GameText.Get("control.feedback.signal_returned"),
                            GameText.Format("control.feedback.took_over_0_automatically", current.Value), GameText.Get("control.feedback.keep_going_with_the"))
                        : new FourPartFeedback(GameText.Get("control.feedback.controlled_unit_destroyed"), GameText.Get("control.feedback.signal_has_nowhere_to"),
                            GameText.Get("control.feedback.no_ally_available_to"), GameText.Get("control.feedback.wait_for_a_new"));
                case ControlChangeReason.ControlledRemoved:
                    return current.IsValid
                        ? new FourPartFeedback(GameText.Get("control.feedback.controlled_unit_vanished"), GameText.Get("control.feedback.control_transferred"),
                            GameText.Format("control.feedback.took_over_0_automatically", current.Value), GameText.Get("control.feedback.keep_going_with_the"))
                        : new FourPartFeedback(GameText.Get("control.feedback.controlled_unit_vanished"), GameText.Get("control.feedback.unit_lost"),
                            GameText.Get("control.feedback.no_ally_available_to"), GameText.Get("control.feedback.wait_for_a_new"));
                case ControlChangeReason.Restored:
                    return current.IsValid
                        ? new FourPartFeedback(GameText.Get("control.feedback.restored_after_load_re"), GameText.Get("control.feedback.signal_restored"),
                            GameText.Format("control.feedback.took_over_0", current.Value), GameText.Get("control.feedback.keep_going_nothing_else"))
                        : new FourPartFeedback(GameText.Get("control.feedback.restored_after_load_re"), GameText.Get("control.feedback.restore_failed"),
                            GameText.Get("control.feedback.no_ally_available_to"), GameText.Get("control.feedback.take_over_any_allied"));
                case ControlChangeReason.Released:
                    // M1-06 既有缺口：此前这条落进 default，被显示成"控制目标丢失"——
                    // 但这是玩家主动放下意识进战略视角，身体还好好站在场上，不是异常，
                    // 见 ControlChangeReason.Released 本身的类型注释。
                    return new FourPartFeedback(GameText.Get("control.feedback.released_control"), GameText.Get("control.feedback.no_controlled_unit_normal"),
                        GameText.Get("control.feedback.you_can_command_all"), GameText.Get("control.feedback.take_over_any_allied_2"));
                default:
                    return current.IsValid
                        ? new FourPartFeedback(GameText.Get("control.feedback.tab_to_switch"), GameText.Get("control.feedback.in_control"),
                            GameText.Format("control.feedback.controlling_0", current.Value), GameText.Get("control.feedback.keep_going_or_press"))
                        : new FourPartFeedback(GameText.Get("control.feedback.no_source_for_the"), GameText.Get("control.feedback.no_controlled_unit"),
                            GameText.Get("control.feedback.no_ally_available_to"), GameText.Get("control.feedback.take_over_any_allied"));
            }
        }

        /// <summary>没有任何反馈计时正在播报时，"当前控制状态"这一行常驻显示什么。</summary>
        public static FourPartFeedback ForAvailability(ControlAvailability availability)
        {
            switch (availability)
            {
                case ControlAvailability.Suspended:
                    return new FourPartFeedback(GameText.Get("control.feedback.signal_reconnecting"), GameText.Get("control.feedback.waiting_out_the_grace"),
                        GameText.Get("control.feedback.the_controlled_unit_cannot"), GameText.Get("control.feedback.stay_put_no_action"));
                case ControlAvailability.Released:
                    // 同 ForChange(Released,...) 的既有 bug：此前与 None 共用"控制目标丢失"，
                    // 而这是玩家进战略视角后的**常态**，几乎每局都会频繁经过这个分支。
                    return new FourPartFeedback(GameText.Get("control.feedback.in_strategic_view"), GameText.Get("control.feedback.no_controlled_unit_normal"),
                        GameText.Get("control.feedback.you_can_command_all_2"), GameText.Get("control.feedback.take_over_any_allied_3"));
                default:
                    return new FourPartFeedback(GameText.Get("control.feedback.no_controllable_unit_available"), GameText.Get("control.feedback.no_controlled_unit"),
                        GameText.Get("control.feedback.direct_control_unavailable_comma"), GameText.Get("control.feedback.wait_for_a_new"));
            }
        }
    }
}
