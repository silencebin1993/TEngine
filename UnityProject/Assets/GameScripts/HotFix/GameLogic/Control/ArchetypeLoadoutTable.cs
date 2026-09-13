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
                    // 孢子：分泌喷射器（"朝瞄准方向射出代谢弹"）当主手，冲刺孢子当功能位。没有交互位。
                    //
                    // M2-03c 第 0 步换掉了原来的 org_confusion_spore：那是一件 Structural 器官，
                    // 没有 CreateModule、没有 AttackFamily，TriggerHook 的 ThornsRatio 恒 0
                    // （目录里写死"只挂 Confused 标记，不造成伤害"）。它在 OrganKernelActionTable
                    // 里必然落成零伤害的 Status，于是"主器官"按下去只挂个标记——那不是一次攻击。
                    // org_emitter 是现役攻击器官（AttackMethod=true / IsRetired=false / Family=Projectile），
                    // 打出的是内核真弹体，与菌丝体的区域形态互为反证。
                    buffer.Add(new UnitLoadoutOrgan("org_emitter", LoadoutAction.Primary));
                    buffer.Add(new UnitLoadoutOrgan("org_dash_spore", LoadoutAction.Utility));
                    break;

                case MyceliumArchetypeId:
                    // 菌丝体：菌丝锚当主手（SummonAnchor → 钉在瞄准点的持续区域），纤毛刺当交互位。
                    // 与孢子刻意取不同的槽位组合 —— M2-03 的验收项就是
                    // "两个不同装配单位被接管时动作集不同"，同槽换个 id 是看不出来的。
                    //
                    // M2-03c 第 0 步换掉了原来的 org_hook：它在目录里 IsRetired=true，
                    // 效果已迁到 `org_cilia + gene_return`（CATALOG-v3 §C）。引用一个退役 id
                    // 本身就是 bug——它永远只会落到 NoKernelAction，等交互内容进来时必然要返工。
                    // 这里换成目录自己指定的那个继任者 org_cilia（现役、Melee → 扇形），
                    // 只换 id，不新增器官。
                    buffer.Add(new UnitLoadoutOrgan("org_mycelium", LoadoutAction.Primary));
                    buffer.Add(new UnitLoadoutOrgan("org_cilia", LoadoutAction.Interact));
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
