using System.Collections.Generic;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Spawning;

namespace GameLogic.Progression
{
    /// <summary>敌人图鉴条目（story-002 D1）：全量敌人 + 本局是否已发现。</summary>
    public readonly struct EnemyCodexEntry
    {
        public readonly int Id;
        public readonly string Name;
        public readonly string Description;
        public readonly bool Discovered;

        public EnemyCodexEntry(int id, string name, string description, bool discovered)
        {
            Id = id;
            Name = name;
            Description = description;
            Discovered = discovered;
        }
    }

    /// <summary>卡牌图鉴条目（story-002 D1）：全量卡牌 + 本局是否已发现。</summary>
    public readonly struct CardCodexEntry
    {
        public readonly int Id;
        public readonly string Name;
        public readonly string Description;
        public readonly bool Discovered;

        public CardCodexEntry(int id, string name, string description, bool discovered)
        {
            Id = id;
            Name = name;
            Description = description;
            Discovered = discovered;
        }
    }

    /// <summary>基因图鉴条目（story-002 D1）。GeneCatalog 无本局发现态跟踪，只出全量目录。</summary>
    public readonly struct GeneCodexEntry
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string Description;

        public GeneCodexEntry(string id, string displayName, string description)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
        }
    }

    /// <summary>器官图鉴条目（story-002 D1）。OrganelleCatalog 无本局发现态跟踪，只出全量目录。</summary>
    public readonly struct OrganelleCodexEntry
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string Description;
        public readonly OrganelleRole Role;

        public OrganelleCodexEntry(string id, string displayName, string description, OrganelleRole role)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Role = role;
        }
    }

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

        // ── 图鉴数据源出口（story-002 D1，供 005 图鉴 UI / 003/004 tooltip 共用）──

        /// <summary>全量敌人 + 本局是否已发现。</summary>
        public IEnumerable<EnemyCodexEntry> AllEnemyEntries()
        {
            foreach (EnemySpec e in DataRegistry.Instance.AllEnemies)
            {
                yield return new EnemyCodexEntry(e.Id, e.Name, e.Description, _enemies.Contains(e.Id));
            }
        }

        /// <summary>全量卡牌 + 本局是否已发现。Description 复用既有 CardSpec.Desc（不新增字段）。</summary>
        public IEnumerable<CardCodexEntry> AllCardEntries()
        {
            foreach (CardSpec c in DataRegistry.Instance.AllCards)
            {
                yield return new CardCodexEntry(c.Id, c.Name, c.Desc, _cards.Contains(c.Id));
            }
        }

        /// <summary>全量基因（Contract 11 + Module 19）。GeneCatalog 无发现态跟踪，不臆造。</summary>
        public IEnumerable<GeneCodexEntry> AllGeneEntries()
        {
            foreach (string id in GeneCatalog.AllGeneIds)
            {
                yield return new GeneCodexEntry(id, GeneCatalog.GetDisplayName(id), GeneCatalog.GetDescription(id));
            }
        }

        /// <summary>全量器官（24）。OrganelleCatalog 无发现态跟踪，不臆造。</summary>
        public IEnumerable<OrganelleCodexEntry> AllOrganelleEntries()
        {
            foreach (OrganelleDef def in OrganelleCatalog.All.Values)
            {
                yield return new OrganelleCodexEntry(def.Id, def.DisplayName, def.Description, def.Role);
            }
        }

        /// <summary>combat-identity-rework story-007（R1/Required 1）：器官栏改按 AttackMethod==true
        /// 过滤（24 个独立开火测试通过的攻击方式），不再用已收敛但语义次要的 IsCarrier。</summary>
        public IEnumerable<OrganelleCodexEntry> AllCarrierOrganelleEntries()
        {
            foreach (OrganelleDef def in OrganelleCatalog.All.Values)
            {
                if (def.AttackMethod)
                {
                    yield return new OrganelleCodexEntry(def.Id, def.DisplayName, def.Description, def.Role);
                }
            }
        }

        /// <summary>代谢模块（AttackMethod==false，非攻击方式的器官条目，不在 Carrier 器官栏展示；
        /// 含能量核心与已退役旧修饰，仅供图鉴归档查阅）。</summary>
        public IEnumerable<OrganelleCodexEntry> AllMetabolicModuleEntries()
        {
            foreach (OrganelleDef def in OrganelleCatalog.All.Values)
            {
                if (!def.AttackMethod)
                {
                    yield return new OrganelleCodexEntry(def.Id, def.DisplayName, def.Description, def.Role);
                }
            }
        }
    }
}
