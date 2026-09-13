using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;

namespace GameLogic.Control
{
    /// <summary>
    /// 把"这具身体现在过载了"镜像成内核的 <see cref="SimStatus.Overloaded"/> 位（M2-04b）。
    ///
    /// ── 在修什么（这是本段的全部意义）──
    /// M2-03c 立的规矩是"过载债按**身体**归属"：能按出什么、按得多勤，由这具身体决定。
    /// 但那本账（<see cref="UnitVitalsRegistry"/>）只挡得住玩家直控的释放入口
    /// （<see cref="DirectControlActions.TryRelease"/> 里的 <c>Evaluate</c>）。
    /// AI 单位的攻击走的是**内核** <c>SimWorld.ResolveMinionCombat</c>，那里的唯一限制是原型配的
    /// <c>AttackCooldown</c>，它完全不认识过载债。
    ///
    /// 于是有一条可利用的漏洞：**把一具身体打到过载 → 退出直控换一具 → 那具身体交给 AI 照常全速攻击**。
    /// 过载的惩罚被完全规避了，"按身体归属"在行为上只兑现了一半。
    /// GDD §7.3 的表里，"蓄力/过载"这一行 AI 侧写的是「只在安全阈值内使用」——
    /// 一具**已经过载**的身体，无论谁在开，都不在安全阈值内。
    ///
    /// ── 为什么是"内核只做被通知的位"，而不是把判定下沉内核 ──
    /// 仓规架构红线：判定不得下沉 <c>Main/Sim/</c>，内核不认识"器官"，更不认识由器官推导出来的过载债。
    /// 但"这具身体现在瘫痪了"是**身体状态**，内核本来就认识（<c>SimStatus.Stunned</c> 让单位动不了、
    /// <c>SimStatus.Feared</c> 让单位转 Flee，都是既有且已验收的「内核状态位改变 AI 行为」模式）。
    /// 所以分工是：
    ///
    /// | 谁 | 负责什么 |
    /// |---|---|
    /// | 热更层 <see cref="UnitVitalsRegistry"/> | **唯一真相**：债怎么涨、怎么衰减、何时进过载、何时滞回解除 |
    /// | 本类 | 变化那一刻把结论推给内核，**不含任何判定、不含任何计时** |
    /// | 内核 | 一条 <c>if 位</c> 的分支；**没有自己的过期逻辑** |
    ///
    /// 这直接回避了"两套过期逻辑并存必然漂移"：内核侧的 <c>SimStatus</c> 虽然有
    /// <c>StatusSystem</c> 那套限时机制，但本位**不登记任何限时条目**，
    /// 解除的唯一来源是账本自己衰减到清除线。
    ///
    /// ── 每帧代价 ──
    /// 本类**没有 Tick**。它只在 <see cref="UnitVitalsRegistry.OverloadChanged"/> 播报时动一下，
    /// 而那个播报只在过载态真正翻转的那一刻发生。解析实体槽位
    /// （<see cref="SimBridge.TryResolveUnitIndex"/>，内核内 O(单位数)）因此一次过载只付两回
    /// （进 + 出），与 M2-02 的 <c>QueryUnitsInRect</c>、M2-04a 的交还处理同一条约定。
    ///
    /// ── 为什么不特判玩家本体 ──
    /// 位照样写给玩家本体（<c>SimFaction.Player</c>）。内核那条分支只看
    /// <c>PlayerMinion</c> 阵营的召唤物，写给本体不改变任何结算；
    /// 可见后果只有 HUD 状态条上多一枚"过载"标记（<c>BattleHudToolkit</c> 按中性标记默认样式画），
    /// 与三个量面板上的过载提示说的是同一件事。
    /// 为此开一条"本体不写"的特例，只会多出一条将来必然漂移的分支。
    /// </summary>
    public sealed class OverloadSuppressionMirror
    {
        private SimBridge _sim;
        private UnitVitalsRegistry _vitals;

        /// <summary>当前被本类压上过载位的实体。既是拆台时的收尾清单，也是验收的直接读点。</summary>
        private readonly List<SimEntityId> _suppressed = new List<SimEntityId>(8);

        /// <summary>当前被压制的身体数。</summary>
        public int SuppressedCount => _suppressed.Count;

        /// <summary>累计推给内核的次数（进 + 出都算）。验收用，证明这条路真的走过。</summary>
        public int PushCount { get; private set; }

        /// <summary>这具身体此刻是否被本类压着。</summary>
        public bool IsSuppressed(SimEntityId id) => id.IsValid && _suppressed.Contains(id);

        /// <summary>
        /// 绑定到一副内核与一本账。重复绑定时先把上一次压下去的位收回来——
        /// 留着就是"上一局的某个槽位被永久钉住"。
        /// </summary>
        public void Bind(SimBridge sim, UnitVitalsRegistry vitals)
        {
            Unbind();

            _sim = sim;
            _vitals = vitals;
            PushCount = 0;

            if (_vitals != null)
            {
                _vitals.OverloadChanged += OnOverloadChanged;
            }
        }

        /// <summary>
        /// 退订并把已压下去的位全部放掉。
        ///
        /// **顺序要紧**：先退订再清位。反过来的话，清位过程中账本若再播报一次，
        /// 会往刚清空的清单里又塞一条，留下一个谁也不会再去清的悬挂压制。
        /// </summary>
        public void Unbind()
        {
            if (_vitals != null)
            {
                _vitals.OverloadChanged -= OnOverloadChanged;
                _vitals = null;
            }

            for (int i = 0; i < _suppressed.Count; i++)
            {
                Push(_suppressed[i], false);
            }
            _suppressed.Clear();

            _sim = null;
        }

        private void OnOverloadChanged(SimEntityId id, bool overloaded)
        {
            if (!id.IsValid)
            {
                return;
            }

            int at = _suppressed.IndexOf(id);
            if (overloaded)
            {
                if (at < 0)
                {
                    _suppressed.Add(id);
                }
            }
            else if (at >= 0)
            {
                _suppressed.RemoveAt(at);
            }
            else
            {
                // 没压过就不用放。照样往下走一次"清位"是无害的，但会让 PushCount 失去意义
                // （它要能回答"这条路真的走过几次"）。
                return;
            }

            Push(id, overloaded);
        }

        /// <summary>
        /// 写内核。**解析槽位与入队发生在同一帧的同一处**：
        /// <c>SimWorld.ApplyCommands</c> 里状态请求排在 Despawn 之前，
        /// 从解析到生效之间不会有任何槽位重排，所以按索引写不会写到别人身上。
        /// 单位已经死了 / 世界已经停了时 <see cref="SimBridge.TryResolveUnitIndex"/> 直接判假，
        /// 静默跳过即可——账本那边照常衰减，不留悬挂。
        /// </summary>
        private void Push(SimEntityId id, bool overloaded)
        {
            if (_sim == null || !_sim.Running || !_sim.TryResolveUnitIndex(id, out int unitIndex))
            {
                return;
            }

            _sim.ApplyStatusUnit(unitIndex, SimStatus.Overloaded, overloaded);
            PushCount++;
        }
    }
}
