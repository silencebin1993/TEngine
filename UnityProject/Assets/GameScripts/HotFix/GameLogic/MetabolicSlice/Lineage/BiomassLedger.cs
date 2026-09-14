using System.Collections.Generic;
using GameLogic.Core;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>
    /// M3-05：按谱系记账的生物质账本。萌生腔扣的是这本账，不是 <c>Strain</c>（过载债）——
    /// 两者语义不同（<c>AUTHORITY_AND_CONFLICTS.md</c> §C7 已经因为混淆 Heat 三义吃过一次教训，
    /// 这里刻意不复用任何现有"债"概念）。
    ///
    /// 起始余额是占位数值（<see cref="DefaultStartingBalance"/>），本期没有经济系统喂账，
    /// 只保证"扣得动、扣不动就拒绝"这条最小可用语义，真实数值来源留给后续经济向故事接线。
    /// </summary>
    public sealed class BiomassLedger : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        public const float DefaultStartingBalance = 100f;

        private readonly Dictionary<string, float> _balances = new Dictionary<string, float>();

        public override void OnEnter()
        {
            _balances.Clear();
        }

        /// <summary>纯读取，无副作用：从未记过账的谱系按占位起始余额读取，不会因为查询而"开户"
        /// （查询与扣款/存款共用这份默认值，行为不因调用顺序而分叉）。</summary>
        public float GetBalance(string lineageId)
        {
            if (lineageId == null)
            {
                return 0f;
            }
            return _balances.TryGetValue(lineageId, out float balance) ? balance : DefaultStartingBalance;
        }

        public void Deposit(string lineageId, float amount)
        {
            if (string.IsNullOrEmpty(lineageId) || amount <= 0f)
            {
                return;
            }
            _balances[lineageId] = GetBalance(lineageId) + amount;
        }

        /// <summary>Reject-to-Safe：余额不足时不扣、返回 false，且**不产生任何副作用**——
        /// 判断阶段只读 <see cref="GetBalance"/>，只有真的要扣款时才写 <c>_balances</c>。</summary>
        public bool TryDeduct(string lineageId, float amount)
        {
            if (string.IsNullOrEmpty(lineageId) || amount < 0f)
            {
                return false;
            }

            float balance = GetBalance(lineageId);
            if (balance < amount)
            {
                return false;
            }

            _balances[lineageId] = balance - amount;
            return true;
        }

        /// <summary>取消/腔体被毁时的退款——原样加回，不做二次校验（调用方保证幂等，见
        /// <see cref="GerminationChamberRegistry"/> 的一次性状态位）。</summary>
        public void Refund(string lineageId, float amount)
        {
            Deposit(lineageId, amount);
        }
    }
}
