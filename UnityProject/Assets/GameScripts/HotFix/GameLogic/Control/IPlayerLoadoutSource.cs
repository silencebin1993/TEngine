using System.Collections.Generic;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.UI.Battle;

namespace GameLogic.Control
{
    /// <summary>
    /// 玩家本体"当下真实装配"的取值方式（M2-03a）。
    ///
    /// 为什么要有这层接口，而不是让 <see cref="UnitLoadoutRegistry"/> 直接抓
    /// <see cref="MetabolicSlicePanel.Instance"/>：
    /// 1. 那是个 <c>MonoBehaviour</c> 面板。热更层的核心玩法数据依赖一个 UI 实例是错误耦合——
    ///    面板哪天换实现、换挂载点，装配系统就跟着塌。
    /// 2. Edit 模式起不了那个面板。不可注入就意味着"玩家装配"这条路径永远没法写真断言，
    ///    只能靠进 Play 手点，而那正是本仓明令要避免的验收方式。
    ///
    /// 实现必须是**当下读取**：每次调用都返回此刻的装配，不得缓存生成时的快照。
    /// </summary>
    public interface IPlayerLoadoutSource
    {
        /// <summary>把玩家本体此刻的器官写进 <paramref name="buffer"/>（调用方保证已清空）。
        /// 没有任何装配时什么都不写——那就是"明确的空装配"，不是错误。</summary>
        void CollectOrgans(List<UnitLoadoutOrgan> buffer);
    }

    /// <summary>
    /// 默认实现：从既有单例链路 <see cref="MetabolicSlicePanel.Instance"/> →
    /// <see cref="CarrierRegistry"/> 读玩家本体的实际装配。
    ///
    /// 映射口径（产品决策，可推翻）：
    /// - 当前激活 Carrier → <see cref="LoadoutAction.Primary"/>（它就是玩家此刻的主要输出手段）；
    /// - 其余 Carrier → <see cref="LoadoutAction.Utility"/>；
    /// - <see cref="LoadoutAction.Interact"/> 本段对玩家本体留空——"交互"要绑到哪件器官上
    ///   属于 M2-03b 的输入设计，这里凭空指定一件只会把错误决策固化下来。
    ///
    /// 结构器官（<c>StructuralSlots</c>）刻意不参与：它们是被动的壳/甲，不产出动作，
    /// 塞进动作集只会让"动作集 = 能按出来的东西"这条口径失效。
    ///
    /// 单例缺席（面板未挂载，例如 Edit 模式回归）时收集为空 —— Reject-to-Safe，不抛。
    /// </summary>
    public sealed class MetabolicSlicePlayerLoadoutSource : IPlayerLoadoutSource
    {
        public void CollectOrgans(List<UnitLoadoutOrgan> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            CarrierRegistry carriers = panel.CarrierRegistry;
            if (carriers == null)
            {
                return;
            }

            string activeId = carriers.ActiveCarrierId;
            foreach (KeyValuePair<string, CarrierInstance> pair in carriers.All)
            {
                CarrierInstance carrier = pair.Value;
                if (carrier == null)
                {
                    continue;
                }

                // OrganelleId 是器官定义 id（story-004 起才有），缺失时退回 CarrierId——
                // CarrierId 本身就是该器官 PartInstance 的 id，至少是可寻址的稳定字符串。
                string organId = string.IsNullOrEmpty(carrier.OrganelleId) ? carrier.CarrierId : carrier.OrganelleId;
                if (string.IsNullOrEmpty(organId))
                {
                    continue;
                }

                LoadoutAction action = pair.Key == activeId ? LoadoutAction.Primary : LoadoutAction.Utility;
                buffer.Add(new UnitLoadoutOrgan(organId, action));
            }
        }
    }
}
