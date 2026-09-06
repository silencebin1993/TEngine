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

    /// <summary>反应图鉴条目（reaction-depth-and-combat-feel story-004）：全量已知具名反应 + 是否
    /// 已发现（本局曾触发过或历史累计触发过）。</summary>
    public readonly struct ReactionCodexEntry
    {
        public readonly string Id;
        public readonly string Description;
        public readonly bool Discovered;

        public ReactionCodexEntry(string id, string description, bool discovered)
        {
            Id = id;
            Description = description;
            Discovered = discovered;
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
    /// 图鉴发现记录（TR-cell-013）。已做跨局持久化（codex-cross-run-persistence story-001）：
    /// <see cref="_enemies"/>/<see cref="_cards"/> 在 <see cref="OnEnter"/> 里从独立 JSON 存档
    /// （<see cref="CodexPersistence"/>，<c>persistentDataPath/codex_discovered.json</c>）载入历史累计值，
    /// 在 <see cref="OnExit"/> 时把"历史 ∪ 本局新发现"整份覆盖写回——仅退出细胞阶段时批量落盘一次，
    /// 局内不写盘。本期只持久化"发现过"状态，不含元进度经济（货币/永久解锁），也不做存档版本迁移。
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
        private readonly HashSet<string> _reactions = new HashSet<string>();
        private SignalScope _scope;

        public IReadOnlyCollection<int> DiscoveredEnemyIds => _enemies;
        public IReadOnlyCollection<int> DiscoveredCardIds => _cards;
        public IReadOnlyCollection<string> DiscoveredReactionIds => _reactions;

        public override void OnEnter()
        {
            _enemies.Clear();
            _cards.Clear();
            _reactions.Clear();

            // 跨局持久化（story-001）：本局起点 = 历史累计。Load 永不 throw，缺档/坏档回落空集合。
            CodexHistory history = CodexPersistence.Load();
            _enemies.UnionWith(history.EnemyIds);
            _cards.UnionWith(history.CardIds);
            _reactions.UnionWith(history.ReactionIds);

            _scope = new SignalScope()
                .On<KillSignal>(OnKill)
                .On<DevourSignal>(OnDevour)
                .On<CardAcquiredSignal>(OnCardAcquired)
                // reaction-depth-and-combat-feel story-004：ComposeCastSignal.ReactionName 是 story-002
                // 已经在广播的既有信号，这里只是多订阅一次登记发现，不新开事件源。
                .On<ComposeCastSignal>(OnComposeCast);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;

            // 跨局持久化（story-001）：退出时批量落盘一次，整份覆盖（历史 ∪ 本局新发现）。
            CodexPersistence.Save(_enemies, _cards, _reactions);
        }

        private void OnComposeCast(ComposeCastSignal s)
        {
            if (!string.IsNullOrEmpty(s.ReactionName))
            {
                _reactions.Add(s.ReactionName);
            }
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
        /// 含能量核心与已退役旧修饰，仅供图鉴归档查阅）。
        /// structural-organ-codex-tab story-001：结构器官已拆到 <see cref="AllStructuralOrganelleEntries"/>
        /// 独立分类，这里排除，避免"代谢模块"标签下混入一等结构器官内容。</summary>
        public IEnumerable<OrganelleCodexEntry> AllMetabolicModuleEntries()
        {
            foreach (OrganelleDef def in OrganelleCatalog.All.Values)
            {
                if (!def.AttackMethod && def.Category != OrganelleCategory.Structural)
                {
                    yield return new OrganelleCodexEntry(def.Id, def.DisplayName, def.Description, def.Role);
                }
            }
        }

        /// <summary>结构器官（structural-organ-codex-tab story-001）：<c>Category == Structural</c> 的
        /// 一等内容，有专属装备槽与掉落/商店/抽卡三条获取路径，图鉴里独立成类。</summary>
        public IEnumerable<OrganelleCodexEntry> AllStructuralOrganelleEntries()
        {
            foreach (OrganelleDef def in OrganelleCatalog.All.Values)
            {
                if (def.Category == OrganelleCategory.Structural)
                {
                    yield return new OrganelleCodexEntry(def.Id, def.DisplayName, def.Description, def.Role);
                }
            }
        }

        /// <summary>全量已知具名反应（reaction-depth-and-combat-feel story-004）+ 本局/历史是否已发现。
        /// 目录来源是 <see cref="ReactionFeedbackCatalog"/> 的 key 空间（player 可读的短名，如
        /// "CausticBurn"），不是 ComposeEngine 内部 <c>ReactionRule.Id</c>（如 "rx_fire_acid_
        /// causticburn"）——同一个玩家可感知效果在引擎里可能有新旧 tag 命名两条规则注册（如 Steam
        /// 同时对应 "fire_wet_to_steam" 与 "rx_fire_wet_steam"），按短名去重才是玩家视角的"一种反应"。</summary>
        public IEnumerable<ReactionCodexEntry> AllReactionEntries()
        {
            foreach (string id in ReactionFeedbackCatalog.AllReactionIds)
            {
                yield return new ReactionCodexEntry(id, ReactionFeedbackCatalog.GetDescription(id), _reactions.Contains(id));
            }
        }
    }
}
