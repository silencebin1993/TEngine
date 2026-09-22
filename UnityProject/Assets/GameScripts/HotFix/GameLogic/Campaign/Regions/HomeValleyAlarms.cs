using System.Collections.Generic;
using GameLogic.Campaign;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER3-WRK-02 STORY-EXECUTION-CARDS.md 第3条"目标失效、仓满、断电、优先级变化即时
    /// 更新机器面板/警报而非只有日志"与 AC-UI-003"警报去重并按威胁排序；点击能定位对象或打开
    /// 恢复面板"的最小落地。UI-AND-ONBOARDING-SPEC.md 危险优先级全序是"核心受威胁 > 电力 > 仓满 >
    /// 工厂堵塞 > 机器重伤 > 工作受阻"六级；本类目前只有两级有真实数据源（仓满/工作受阻，均来自
    /// <see cref="HomeValleyWorkOrders"/> 的 Waiting 状态），其余四级背后的系统（威胁判定、电网告警、
    /// 工厂队列、伤势系统）尚未实现或尚未接线告警——这不是本 Story 的遗漏，是诚实的范围裁剪
    /// （见 evidence 文档 DEBT 登记），<see cref="Severity"/> 预留了完整六级数值位置，未来哪个系统
    /// 落地就在那个位置接入 <see cref="Collect"/>，不需要改调用方（UI）签名。
    ///
    /// "去重"：同一 <see cref="WorkOrderRecord.WorkOrderId"/> 持续处于同一种告警状态期间只在首次
    /// <see cref="Collect"/> 调用时写一条 <see cref="Log.Warning"/>，不随每帧 UI 刷新反复刷屏；
    /// 离开该状态后从记忆里移除，允许未来再次进入时重新告警一次。</summary>
    public static class HomeValleyAlarms
    {
        public enum Severity
        {
            CoreThreatened = 0,  // 尚无真实数据源（无战斗/威胁判定）。
            PowerCritical = 1,   // 尚无真实数据源（ER3-PWR-01 只有只读展示，未定义"告警"阈值）。
            StorageFull = 2,     // 真实数据源：Haul 订单 Waiting + storage-full。
            FactoryJammed = 3,   // 尚无真实数据源（工厂队列是 ER4-FAC-01 的范围）。
            MachineWounded = 4,  // 尚无真实数据源（归还谷地无战斗，机器不会受伤）。
            WorkBlocked = 5,     // 真实数据源：任意订单 Waiting + path-blocked。
        }

        public readonly struct AlertRecord
        {
            public readonly string Key;
            public readonly Severity Severity;
            public readonly string Message;
            /// <summary>0＝当前没有可定位的具体机器（例如 Waiting 期间订单已释放机器绑定）。</summary>
            public readonly int MachineLogicId;

            public AlertRecord(string key, Severity severity, string message, int machineLogicId)
            {
                Key = key;
                Severity = severity;
                Message = message;
                MachineLogicId = machineLogicId;
            }
        }

        private static readonly HashSet<string> _seen = new HashSet<string>(4);

        /// <summary>每次 UI 刷新调用：返回当前应显示的告警（已去重、已按威胁排序）。归还谷地工作单
        /// 数量恒定个位数（同 <see cref="HomeValleyWorkOrders.Tick"/> 的既定性能纪律），全量扫描
        /// 不违反 AC-PER-006 的"无每帧大量分配"——那条约束的是分配算法本身的运行频率，不是这种
        /// 恒定小规模的只读汇总。</summary>
        public static List<AlertRecord> Collect(CampaignState state)
        {
            var result = new List<AlertRecord>();
            if (state?.WorkOrders == null || state.WorkOrders.Length == 0)
            {
                _seen.Clear();
                return result;
            }

            var stillActive = new HashSet<string>(4);
            foreach (WorkOrderRecord order in state.WorkOrders)
            {
                if (order.State != WorkOrderState.Waiting)
                {
                    continue;
                }

                Severity? severity = null;
                string message = null;
                if (order.FailureReason == "path-blocked")
                {
                    severity = Severity.WorkBlocked;
                    message = $"{order.Kind} 受阻：路径卡住，等待重试";
                }
                else if (order.FailureReason != null && order.FailureReason.StartsWith("storage-full"))
                {
                    severity = Severity.StorageFull;
                    message = "仓储已满：搬运暂停，腾出空间后自动继续";
                }

                if (severity == null)
                {
                    continue; // 其余 Waiting 原因（如 no-power，本 Story 未落地）暂不告警，仅面板展示。
                }

                stillActive.Add(order.WorkOrderId);
                if (_seen.Add(order.WorkOrderId))
                {
                    Log.Warning($"[HomeValleyAlarms] {message}（{order.WorkOrderId}）。");
                }
                result.Add(new AlertRecord(order.WorkOrderId, severity.Value, message, order.AssignedMachineLogicId));
            }

            _seen.IntersectWith(stillActive); // 离开告警状态的订单从记忆移除，允许未来重新告警。

            result.Sort((a, b) => a.Severity != b.Severity
                ? a.Severity.CompareTo(b.Severity)
                : string.CompareOrdinal(a.Key, b.Key));
            return result;
        }
    }
}
