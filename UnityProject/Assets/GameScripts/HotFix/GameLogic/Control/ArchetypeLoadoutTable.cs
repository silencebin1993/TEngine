using System.Collections.Generic;

namespace GameLogic.Control
{
    /// <summary>
    /// 行为原型 → 装配的显式映射（M2-03a）。
    ///
    /// **为什么是代码里的一张小表而不是 Luban 表**：M1 固定房间里真正会被接管的非玩家友军
    /// 只有两名（<see cref="SporeArchetypeId"/> / <see cref="MyceliumArchetypeId"/>，
    /// 见 <c>CellStageFlow.SpawnControlAllies</c>）。为两行数据新开一张配置表、走一遍
    /// codegen 与热更包，成本远大于收益；等友军种类真的展开（M2-04 之后）再搬表也不迟，
    /// 届时只需换掉本类的实现，调用方一行都不用改——这正是把它收敛成单一入口的理由。
    ///
    /// **本表只用既有器官 id**，不新增器官/基因/美术内容（M2-03a 非目标）。
    /// </summary>
    public static class ArchetypeLoadoutTable
    {
        /// <summary>孢子仆从。行为原型 id 直接复用为 VisualId（见 <c>CellStageFlow.BuildVisuals</c> case 13）。</summary>
        public const int SporeArchetypeId = 13;

        /// <summary>菌丝体。同上，case 15。</summary>
        public const int MyceliumArchetypeId = 15;

        /// <summary>
        /// 把该原型的器官写进 <paramref name="buffer"/>（调用方保证已清空）。
        ///
        /// 未登记的原型什么都不写 —— 落到"只有移动"的空装配。这是刻意的：
        /// 给未知原型编一套默认动作，会让"这个单位为什么能放这一招"变得无从追查，
        /// 而 M2-03 的整个立论就是"动作集必须从实际装配读出来"。
        /// </summary>
        public static void Collect(int archetypeId, List<UnitLoadoutOrgan> buffer)
        {
            if (buffer == null)
            {
                return;
            }

            switch (archetypeId)
            {
                case SporeArchetypeId:
                    // 孢子：喷孢子当主手，冲刺孢子当功能位。没有交互位。
                    buffer.Add(new UnitLoadoutOrgan("org_confusion_spore", LoadoutAction.Primary));
                    buffer.Add(new UnitLoadoutOrgan("org_dash_spore", LoadoutAction.Utility));
                    break;

                case MyceliumArchetypeId:
                    // 菌丝体：菌丝当主手，钩取当交互位（搬运/拉拽）。没有功能位。
                    // 与孢子刻意取不同的槽位组合 —— M2-03 的验收项就是
                    // "两个不同装配单位被接管时动作集不同"，同槽换个 id 是看不出来的。
                    buffer.Add(new UnitLoadoutOrgan("org_mycelium", LoadoutAction.Primary));
                    buffer.Add(new UnitLoadoutOrgan("org_hook", LoadoutAction.Interact));
                    break;

                default:
                    break;
            }
        }

        /// <summary>该原型是否有显式装配。false 表示会落到只有移动的空装配。</summary>
        public static bool IsMapped(int archetypeId)
        {
            return archetypeId == SporeArchetypeId || archetypeId == MyceliumArchetypeId;
        }
    }
}
