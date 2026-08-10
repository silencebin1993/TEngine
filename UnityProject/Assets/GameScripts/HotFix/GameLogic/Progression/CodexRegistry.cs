using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Spawning;

namespace GameLogic.Progression
{
    /// <summary>
    /// 图鉴发现记录（TR-cell-013）。窄口径实现（Preflight C1）：本局内存态，
    /// 未做跨会话持久化——图鉴系统真正的设计意图是跨局解锁（GDD §12.3），
    /// 但这是本仓库第一次涉及玩法存档，留给专门 story/ADR 讨论存档格式时再做。
    ///
    /// 监听现有信号登记发现，不新开一套平行的事件系统：
    ///   - <see cref="KillSignal"/>/<see cref="DevourSignal"/> → 敌人发现
    ///   - <see cref="CardAcquiredSignal"/> → 卡牌发现
    /// </summary>
    public sealed class CodexRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private readonly HashSet<int> _enemies = new HashSet<int>();
        private readonly HashSet<int> _cards = new HashSet<int>();
        private SignalScope _scope;

        public IReadOnlyCollection<int> DiscoveredEnemyIds => _enemies;
        public IReadOnlyCollection<int> DiscoveredCardIds => _cards;

        public override void OnEnter()
        {
            _enemies.Clear();
            _cards.Clear();
            _scope = new SignalScope()
                .On<KillSignal>(OnKill)
                .On<DevourSignal>(OnDevour)
                .On<CardAcquiredSignal>(OnCardAcquired);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
        }

        private void OnKill(KillSignal s)
        {
            RegisterEnemy(SpawnDirector.DecodeEnemyId(s.LogicId));
        }

        private void OnDevour(DevourSignal s)
        {
            if (s.IsCorpse)
            {
                // 尸体/残块的二次吞噬不是真实敌人条目，不登记。
                return;
            }
            RegisterEnemy(s.EnemyId);
        }

        private void OnCardAcquired(CardAcquiredSignal s)
        {
            _cards.Add(s.CardId);
        }

        private void RegisterEnemy(int enemyId)
        {
            if (enemyId > 0)
            {
                _enemies.Add(enemyId);
            }
        }
    }
}
