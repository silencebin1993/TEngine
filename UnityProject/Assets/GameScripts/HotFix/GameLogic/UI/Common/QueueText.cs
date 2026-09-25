using GameLogic.Campaign;

namespace GameLogic.UI.Common
{
    /// <summary>ER8-CONTENT-01 AC-THEME-001：生产/解析/合成队列的玩家文字。
    /// 队列记录里的 <c>BlockedReason</c> 是给逻辑与日志用的原因码（assembly-station-unpowered 等），
    /// 装配站状态是枚举——此前面板直接把它们显示给玩家。原因码仍原样保留在记录里，只在显示时翻译；
    /// 新增原因码忘了登记时回退“暂时受阻”，绝不把原因码本身露出去（自检逐个核对已知码）。</summary>
    public static class QueueText
    {
        /// <summary>已知的队列原因码（自检用）。</summary>
        public static readonly string[] KnownReasonCodes =
        {
            "assembly-station-unpowered", "assembly-station-destroyed", "analysis-bench-unpowered",
            "analysis-bench-destroyed", "target-lost", "target-left-home-valley", "unknown-blueprint",
            "spawn-failed:MissingChassis", "quest-item-missing", "exit-blocked", "blueprint-version-missing",
            "material-missing",
        };

        public static string Reason(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.Empty;
            }
            if (code.StartsWith("spawn-failed", System.StringComparison.Ordinal))
            {
                return "出厂失败，材料已退还";
            }
            switch (code)
            {
                case "assembly-station-unpowered": return "装配站断电，恢复供电后继续（进度保留）";
                case "assembly-station-destroyed": return "装配站被毁";
                case "analysis-bench-unpowered": return "解析台断电，恢复供电后继续（进度保留）";
                case "analysis-bench-destroyed": return "解析台被毁";
                case "target-lost": return "目标机器已不在场";
                case "target-left-home-valley": return "目标机器已离开归还谷地";
                case "unknown-blueprint": return "蓝图无效";
                case "quest-item-missing": return "待解析的模块已不在仓库";
                case "exit-blocked": return "装配站出口被占用，清空后自动出厂";
                case "blueprint-version-missing": return "蓝图版本已不存在";
                case "material-missing": return "缺少材料芯片";
                default: return "暂时受阻";
            }
        }

        public static string FactoryState(FactoryQueueState state)
        {
            switch (state)
            {
                case FactoryQueueState.Queued: return "排队中";
                case FactoryQueueState.WaitingResources: return "缺材料";
                case FactoryQueueState.WaitingPower: return "缺电力";
                case FactoryQueueState.WaitingTarget: return "等待目标";
                case FactoryQueueState.Running: return "进行中";
                case FactoryQueueState.OutputBlocked: return "出口堵塞";
                case FactoryQueueState.Completed: return "已完成";
                case FactoryQueueState.Cancelled: return "已取消";
                case FactoryQueueState.Failed: return "失败";
                default: return string.Empty;
            }
        }
    }
}
