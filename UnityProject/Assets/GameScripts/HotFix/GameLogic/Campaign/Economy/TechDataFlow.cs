using System;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG5-E2E-01（FG05 FGR-RND-070“来源：解析、残骸处理、遗迹终端、数据核心、黑匣子；去处：研究点、模拟熔合、正式熔合、首次保存跨派系蓝图……统计面板里可以看到技术数据的收入和支出”；
    /// DEBT-FG5RND06-04）：技术数据按来源 / 去处的累计收支，存在 <see cref="ResearchState.TechFlow"/>（唯一写入口）。
    ///
    /// 记账点（都是技术数据真正进出库存的那一刻，不另起账）：
    /// - 经济账本的生产 / 消费事务确认（<see cref="CampaignEconomyLedger"/> 的 Commit，按事务编号前缀分类）：解析台（敌方物品、Demo 区域任务物、数据核心）、残骸处理、黑匣子、数据复原、蓝图首次保存；
    /// - 不走账本的数字资源扣取：仿真实验室每周期取 1 件（<see cref="ResearchService"/>）、模拟熔合 / 正式熔合（<see cref="FusionService"/>）；取消 / 回滚 / 拆除退回的从支出里扣回，不算收入。
    /// 每次记账 O(来源数)（≤ 9），零分配（记录数组只在第一次见到某个键时扩一次）。
    /// </summary>
    public static class TechDataFlow
    {
        public const string Analysis = "analysis";
        public const string Wreck = "wreck";
        public const string BlackBox = "blackbox";
        public const string Lab = "lab";
        public const string FusionSim = "fusion_sim";
        public const string Fusion = "fusion";
        public const string Restore = "restore";
        public const string Blueprint = "blueprint";
        public const string Other = "other";

        /// <summary>收入的来源（统计面板按这个顺序列）。</summary>
        public static readonly string[] IncomeSources = { Analysis, Wreck, BlackBox, Other };

        /// <summary>支出的去处（统计面板按这个顺序列）。</summary>
        public static readonly string[] ExpenseSinks = { Lab, FusionSim, Fusion, Restore, Blueprint, Other };

        /// <summary>收支变化计数（统计面板据此判断要不要重建）。</summary>
        public static int Revision { get; private set; }

        /// <summary>经济账本事务编号 → 来源 / 去处（编号写法见各服务：解析台 “…:techdata”、残骸 “analysis-wreck:…”、黑匣子 “blackbox:…”、
        /// 数据复原 “firmware_restore_tx_…”、蓝图首次保存 “blueprint_reaction_charge_tx_…”）。认不出的归“其他”。</summary>
        public static string ClassifyTransaction(string transactionId)
        {
            if (string.IsNullOrEmpty(transactionId))
            {
                return Other;
            }
            if (transactionId.StartsWith("analysis-wreck:", StringComparison.Ordinal))
            {
                return Wreck;
            }
            if (transactionId.EndsWith(":techdata", StringComparison.Ordinal))
            {
                return Analysis;
            }
            if (transactionId.StartsWith(BlackBoxService.InstancePrefix, StringComparison.Ordinal))
            {
                return BlackBox;
            }
            if (transactionId.StartsWith("firmware_restore_tx_", StringComparison.Ordinal))
            {
                return Restore;
            }
            if (transactionId.StartsWith("blueprint_reaction_charge_tx_", StringComparison.Ordinal))
            {
                return Blueprint;
            }
            return Other;
        }

        /// <summary>经济账本确认了一笔技术数据事务（<paramref name="produced"/> = 生产型 / 收入）。</summary>
        public static void OnLedgerCommit(CampaignState state, string transactionId, long amount, bool produced)
        {
            if (amount <= 0)
            {
                return;
            }
            string key = ClassifyTransaction(transactionId);
            if (produced)
            {
                Income(state, key, amount);
            }
            else
            {
                Spend(state, key, amount);
            }
        }

        public static void Income(CampaignState state, string source, long amount)
        {
            TechFlowRecord r = Find(state, source, create: amount > 0);
            if (r != null && amount > 0)
            {
                r.Income += amount;
                Revision++;
            }
        }

        public static void Spend(CampaignState state, string sink, long amount)
        {
            TechFlowRecord r = Find(state, sink, create: amount > 0);
            if (r != null && amount > 0)
            {
                r.Expense += amount;
                Revision++;
            }
        }

        /// <summary>退回（取消熔合、拆除实验室退回本周期已取的那一件……）：从这个去处的支出里扣回，不低于 0、不算收入。</summary>
        public static void Unspend(CampaignState state, string sink, long amount)
        {
            TechFlowRecord r = Find(state, sink, create: false);
            if (r != null && amount > 0)
            {
                r.Expense = Math.Max(0, r.Expense - amount);
                Revision++;
            }
        }

        public static long IncomeOf(CampaignState state, string source) => Find(state, source, create: false)?.Income ?? 0;

        public static long ExpenseOf(CampaignState state, string sink) => Find(state, sink, create: false)?.Expense ?? 0;

        public static long TotalIncome(CampaignState state)
        {
            long n = 0;
            foreach (TechFlowRecord r in state?.Research?.TechFlow ?? Array.Empty<TechFlowRecord>())
            {
                n += r?.Income ?? 0;
            }
            return n;
        }

        public static long TotalExpense(CampaignState state)
        {
            long n = 0;
            foreach (TechFlowRecord r in state?.Research?.TechFlow ?? Array.Empty<TechFlowRecord>())
            {
                n += r?.Expense ?? 0;
            }
            return n;
        }

        private static TechFlowRecord Find(CampaignState state, string key, bool create)
        {
            ResearchState rs = state?.Research;
            if (rs == null || string.IsNullOrEmpty(key))
            {
                return null;
            }
            TechFlowRecord[] all = rs.TechFlow ?? Array.Empty<TechFlowRecord>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].Key == key)
                {
                    return all[i];
                }
            }
            if (!create)
            {
                return null;
            }
            var rec = new TechFlowRecord { Key = key };
            var next = new TechFlowRecord[all.Length + 1];
            Array.Copy(all, next, all.Length);
            next[all.Length] = rec;
            rs.TechFlow = next;
            return rec;
        }
    }
}
