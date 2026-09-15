using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-01：编队领域模型的注册表。与 <see cref="GameLogic.MetabolicSlice.Lineage.LineageRegistry"/>
    /// 同一范式——纯内存 <see cref="Dictionary{TKey,TValue}"/>，无持久化层。
    ///
    /// ── 死亡信号现状（技术债，先例见 <c>WildOrganRegistry.HandleBodyDeath</c>）──
    /// <see cref="HandleMemberDeath"/> 是真实入口，行为完全正确（含清理 <see cref="Formation.DetachedMembers"/>
    /// 与跨编队遍历），但本 story 未接任何死亡信号——本仓现有 <c>DeathEvent</c>/<c>KillSignal</c>
    /// 语义都是"击杀"而非"友方个体阵亡"，贸然订阅需要新增信号或改动战斗结算路径，风险收益比不划算，
    /// 留给后续故事（M4-02/M4-05 附近）按需接线。
    /// </summary>
    public sealed class FormationRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private readonly Dictionary<string, Formation> _formations = new Dictionary<string, Formation>();
        private int _nextFormationSeq = 1;

        public IReadOnlyCollection<Formation> AllFormations => _formations.Values;

        public Formation CreateFormation()
        {
            string id = "formation_" + _nextFormationSeq++;
            var formation = new Formation(id);
            _formations[id] = formation;
            return formation;
        }

        public Formation GetFormation(string id)
        {
            return id != null && _formations.TryGetValue(id, out Formation formation) ? formation : null;
        }

        /// <summary>M4-05：按成员反查所属编队——直控接管/退出信号处理只知道
        /// <see cref="SimEntityId"/>，不知道它属于哪个编队，需要这个反查入口。遍历所有编队找
        /// <see cref="Formation.IsMember"/> 为真的第一个；查询频率是人类操作级（信号触发），
        /// 不是逐帧调用，不需要建反向索引。找不到返回 null——调用方（<see cref="FormationMovementDriver"/>）
        /// 据此 no-op，不属于任何编队的实体（玩家本体、非编队敌人）不受影响。</summary>
        public Formation FindFormationContaining(SimEntityId entity)
        {
            foreach (Formation formation in _formations.Values)
            {
                if (formation.IsMember(entity))
                {
                    return formation;
                }
            }
            return null;
        }

        /// <summary>真实入口：遍历所有编队，把这个已死亡的成员从成员集与脱队集里彻底清掉；随后（M4-02）
        /// 再检查是否有编队正带着 Attack/OrganCategory 命令瞄着这个刚死的目标——有则自动
        /// <see cref="FormationCommandFailReason.InvalidTarget"/> 失败（见 D4/D5，
        /// <see cref="FormationCommand.CommandKind"/> 文档）。
        /// 接线时机见类型注释——查无此成员的编队直接跳过，对不存在的 id 整体调用是安全的 no-op。</summary>
        public void HandleMemberDeath(SimEntityId entity)
        {
            foreach (Formation formation in _formations.Values)
            {
                formation.RemoveMember(entity);
            }

            foreach (Formation formation in _formations.Values)
            {
                FormationCommandEntry active = formation.ActiveCommand;
                if (active == null || active.State != FormationCommandState.Active)
                {
                    continue;
                }

                FormationCommand.CommandKind kind = active.Command.Kind;
                bool isTargetedAttack = kind == FormationCommand.CommandKind.Attack
                    || kind == FormationCommand.CommandKind.OrganCategory;
                if (isTargetedAttack && active.Command.TargetEntity == entity)
                {
                    formation.FailActiveCommand(FormationCommandFailReason.InvalidTarget);
                }
            }
        }
    }
}
