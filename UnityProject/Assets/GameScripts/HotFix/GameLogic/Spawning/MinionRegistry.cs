using GameLogic.Core;

namespace GameLogic.Spawning
{
    /// <summary>
    /// 附属体存活计数登记。对应 Cell_Stage_Spec.md TR-cell-006。
    ///
    /// 内核不知道"附属体"这个概念，MinionCap 也只是一个热更层才关心的数值。
    /// 这里只做整数加减，不遍历 SimSnapshot——成本与场上单位数无关：
    ///   - <see cref="Reserve"/>：EffectSpawn 生成前申请配额，按剩余额度裁剪。
    ///   - <see cref="Release"/>：CellDevourSystem 结算死亡事件时归还配额。
    /// </summary>
    public sealed class MinionRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Spawning;

        private int _live;

        /// <summary>当前登记的存活附属体数。</summary>
        public int LiveCount => _live;

        public override void OnEnter()
        {
            _live = 0;
        }

        /// <summary>
        /// 申请生成配额。按 cap - 当前存活 裁剪到剩余额度（含 0），
        /// 裁剪后的数量立即登记为存活，调用方应只生成这么多。
        /// </summary>
        public int Reserve(int requested, int cap)
        {
            if (requested <= 0)
            {
                return 0;
            }

            int room = cap - _live;
            if (room <= 0)
            {
                return 0;
            }

            int granted = requested < room ? requested : room;
            _live += granted;
            return granted;
        }

        /// <summary>归还配额。上限 0 保护，避免计数漂到负数。</summary>
        public void Release(int count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            _live -= count;
            if (_live < 0)
            {
                _live = 0;
            }
        }
    }
}
