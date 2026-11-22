using GameLogic.Campaign;
using GameLogic.Localization;

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
                return GameText.Get("queue.release_failed_materials_refunde");
            }
            string shortfall = Campaign.CampaignEconomyLedger.DescribeShortfall(code);
            if (shortfall != null)
            {
                return shortfall + GameText.Get("queue.starts_automatically_once_restoc");
            }
            switch (code)
            {
                case "assembly-station-unpowered": return GameText.Get("queue.assembly_station_unpowered_resum");
                case "assembly-station-destroyed": return GameText.Get("queue.assembly_station_destroyed");
                case "analysis-bench-unpowered": return GameText.Get("queue.analysis_bench_unpowered_resumes");
                case "analysis-bench-destroyed": return GameText.Get("queue.analysis_bench_destroyed");
                case "target-lost": return GameText.Get("queue.the_target_machine_is");
                case "target-left-home-valley": return GameText.Get("queue.the_target_machine_has");
                case "unknown-blueprint": return GameText.Get("queue.invalid_blueprint");
                case "quest-item-missing": return GameText.Get("queue.the_module_to_analyze");
                case "exit-blocked": return GameText.Get("queue.assembly_station_exit_blocked");
                case "blueprint-version-missing": return GameText.Get("queue.the_blueprint_version_no");
                case "material-missing": return GameText.Get("queue.missing_material_chips");
                default: return GameText.Get("queue.temporarily_blocked");
            }
        }

        public static string FactoryState(FactoryQueueState state)
        {
            switch (state)
            {
                case FactoryQueueState.Queued: return GameText.Get("analysis.state.queued");
                case FactoryQueueState.WaitingResources: return GameText.Get("asm.state.waiting_materials");
                case FactoryQueueState.WaitingPower: return GameText.Get("queue.no_power");
                case FactoryQueueState.WaitingTarget: return GameText.Get("queue.waiting_for_target");
                case FactoryQueueState.Running: return GameText.Get("queue.in_progress");
                case FactoryQueueState.OutputBlocked: return GameText.Get("queue.exit_blocked");
                case FactoryQueueState.Completed: return GameText.Get("analysis.state.completed");
                case FactoryQueueState.Cancelled: return GameText.Get("analysis.state.cancelled");
                case FactoryQueueState.Failed: return GameText.Get("queue.failed");
                default: return string.Empty;
            }
        }
    }
}
