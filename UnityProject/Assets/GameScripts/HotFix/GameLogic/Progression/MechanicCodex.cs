using System;
using System.Collections.Generic;
using System.IO;
using GameConfig;
using GameConfig.fg;
using GameLogic.Settings;
using TEngine;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>机制图鉴解锁记录的磁盘布局（v1）。全 public 字段以兼容 <see cref="JsonUtility"/>；只存“解锁过的条目 ID”，条目内容在 fg.TbCodexEntry。</summary>
    [Serializable]
    public sealed class MechanicCodexSaveData
    {
        public int Version = MechanicCodex.FileVersion;
        public string[] Unlocked = Array.Empty<string>();
    }

    /// <summary>条目来源：系统说明（fg.TbCodexEntry）/ 固件（fg.TbFirmwareKind 生成）/ 反应（fg.TbReaction 生成）。</summary>
    public enum MechanicCodexKind : byte
    {
        System = 0,
        Firmware = 1,
        Reaction = 2,
        /// <summary>FG4-ECO-01：物品（fg.TbEcoItem 生成）。</summary>
        Item = 3,
        /// <summary>FG4-ECO-01：配方（fg.TbRecipe 生成）。</summary>
        Recipe = 4,
    }

    /// <summary>一条机制图鉴条目（fg.TbCodexEntry 的运行时视图；links / hooks 已拆好）。FG2-FW-05：固件 / 反应条目由各自的表生成，
    /// 标题 / 正文 / 获取途径按 <see cref="Kind"/> 取（<see cref="MechanicCodex.Title"/> 等），不在 fg.TbCodexEntry 重复一份。</summary>
    public sealed class MechanicCodexEntry
    {
        public MechanicCodexKind Kind;
        /// <summary>固件 / 反应条目对应的内容 ID（fw_* / reaction_*）；系统说明条目为空。</summary>
        public string ContentId;
        public string Id;
        public string Tab;
        public string TitleKey;
        public string BodyKey;
        public string HintKey;
        public int SortOrder;
        public string[] Links = Array.Empty<string>();
        public string[] Hooks = Array.Empty<string>();
        /// <summary>FG4-ECO-01：一开始就能看（原料 / 中间品 / 成品 / 流体 / 数字资源与全部配方：规划产线要用）；远征物、关键材料、终局第一次拿到时解锁。</summary>
        public bool AlwaysOpen;
    }

    /// <summary>
    /// FG1-HUD-01（FG13 FGR-UX-050“系统说明”页签、FGR-UX-051；FG01 第 4 章“图鉴有信号接入 / 接入口 / 核心固件 / 安全模式 / 裸跑条目”）：机制图鉴。
    /// - 条目内容：fg.TbCodexEntry（数据源 tools/cell_tables/fgdata_hud.py），标题 / 正文 / 获取提示走文本键。
    /// - 解锁：条目的 hooks = 首次接触它的引导钩子；<see cref="Core.GuidanceHooks.Raise"/> 每一次触发都调 <see cref="OnHook"/>（不只第一次），
    ///   所以图鉴文件丢失后，下一次接触同一机制仍会解锁。载入时把本机设置里已经见过的钩子补解锁（老玩家不会面对一片剪影）。
    ///   “?”按钮打开某条目 = 玩家正在这个机制的界面上，直接解锁（<see cref="Open"/>）。
    /// - 持久化：机制条目跨存档共享（沿用 <see cref="CodexPersistence"/> 的方式：独立 JSON 文件、整份覆盖写、读失败回落空集合不抛异常）。
    /// - FG2-FW-05（FGU-38 固件 / 反应页签、FGR-UX-051 搜索与互链、FGR-UX-030 悬停跳转）：固件条目（44 条，“fw:”前缀）按 fg.TbFirmwareKind 生成，
    ///   第一次拿到芯片或内容解锁时解锁；反应条目（“reaction:”前缀）按 fg.TbReaction 生成，第一次打出时解锁；二者都跨存档（同一个图鉴文件）。
    ///   敌人 / 阵营等其余页签是 FG9-UX-01 的交付。
    /// 开销：只在钩子触发 / 解锁 / 打开面板时工作，O(条目数)；不按帧。
    /// </summary>
    public static class MechanicCodex
    {
        public const int FileVersion = 1;
        private const string FileName = "codex_mechanics.json";

        public const string TabSystem = "system";
        public const string TabFirmware = "firmware";
        public const string TabReaction = "reaction";
        public const string TabItem = "item";
        public const string TabRecipe = "recipe";

        /// <summary>页签顺序（界面按这个顺序画标签）。</summary>
        public static readonly string[] Tabs = { TabSystem, TabFirmware, TabReaction, TabItem, TabRecipe };

        public const string FirmwarePrefix = "fw:";
        public const string ReactionPrefix = "reaction:";
        public const string ItemPrefix = "item:";
        public const string RecipePrefix = "recipe:";

        /// <summary>FG4-ECO-01：物品条目 ID（“item:” + 物品 ID）。</summary>
        public static string ItemEntryId(string itemId) => string.IsNullOrEmpty(itemId) ? null : ItemPrefix + itemId;

        /// <summary>FG4-ECO-01：配方条目 ID（“recipe:” + 配方 ID）。</summary>
        public static string RecipeEntryId(string recipeId) => string.IsNullOrEmpty(recipeId) ? null : RecipePrefix + recipeId;

        public static string FirmwareEntryId(string firmwareId) => string.IsNullOrEmpty(firmwareId) ? null : FirmwarePrefix + firmwareId;

        public static string ReactionEntryId(string reactionId) => string.IsNullOrEmpty(reactionId) ? null : ReactionPrefix + reactionId;

        /// <summary>条目属于哪个页签（查不到时 system）。</summary>
        public static string TabOf(string id) => Find(id)?.Tab ?? TabSystem;

        /// <summary>“打开某条目”的请求（界面订阅；参数为条目 ID）。</summary>
        public static event Action<string> OpenRequested;

        /// <summary>自检把文件放到临时目录。</summary>
        public static string FilePathOverrideForTests;

        private static List<MechanicCodexEntry> _entries;
        private static Dictionary<string, MechanicCodexEntry> _byId;
        private static HashSet<string> _unlocked;
        private static string _loadError;

        public static int Revision { get; private set; } = 1;
        public static int SaveCount { get; private set; }
        public static int OpenCount { get; private set; }
        public static string LastOpenedId { get; private set; }

        public static string FilePath => FilePathOverrideForTests ?? Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>配置表不可用时的原因（界面显示错误态）；可用时为 null。</summary>
        public static string LoadError
        {
            get
            {
                EnsureEntries();
                return _loadError;
            }
        }

        /// <summary>全部条目（按 sortOrder）。</summary>
        public static IReadOnlyList<MechanicCodexEntry> Entries
        {
            get
            {
                EnsureEntries();
                return _entries;
            }
        }

        /// <summary>某页签的条目（按 sortOrder）；<paramref name="search"/> 非空时只留匹配的：已解锁的按标题与正文、未解锁的按获取途径（不剧透名字）。</summary>
        public static void EntriesIn(string tab, string search, List<MechanicCodexEntry> into, Campaign.CampaignState state = null)
        {
            into.Clear();
            EnsureEntries();
            EnsureUnlocked();
            string q = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
            foreach (MechanicCodexEntry e in _entries)
            {
                if (e.Tab != tab)
                {
                    continue;
                }
                if (q != null)
                {
                    bool open = _unlocked.Contains(e.Id);
                    string hay = open ? Title(e) + "\n" + Body(e, state) : Hint(e, state);
                    if ((hay ?? string.Empty).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }
                }
                into.Add(e);
            }
        }

        public static int CountIn(string tab)
        {
            EnsureEntries();
            int n = 0;
            foreach (MechanicCodexEntry e in _entries)
            {
                if (e.Tab == tab)
                {
                    n++;
                }
            }
            return n;
        }

        public static int UnlockedCountIn(string tab)
        {
            EnsureEntries();
            EnsureUnlocked();
            int n = 0;
            foreach (MechanicCodexEntry e in _entries)
            {
                if (e.Tab == tab && _unlocked.Contains(e.Id))
                {
                    n++;
                }
            }
            return n;
        }

        // ── 条目文字（按来源取；全部走文本键）─────────────────────────────────────

        public static string Title(MechanicCodexEntry e)
        {
            if (e == null)
            {
                return string.Empty;
            }
            switch (e.Kind)
            {
                case MechanicCodexKind.Firmware:
                    return Campaign.Signal.FirmwareKinds.DisplayName(e.ContentId) ?? e.ContentId;
                case MechanicCodexKind.Reaction:
                    return Campaign.Content.NamedReactionCatalog.NameOf(e.ContentId) ?? e.ContentId;
                case MechanicCodexKind.Item:
                case MechanicCodexKind.Recipe:
                    return GameLogic.Localization.GameText.Get(e.TitleKey);
                default:
                    return GameLogic.Localization.GameText.Get(e.TitleKey);
            }
        }

        /// <summary>已解锁时的正文。固件：与固件库详情页同一份（各载体读法、标签、参与的反应、获取途径、持有数量）；反应：说明、配料、能提供配料的固件、本存档首次触发。</summary>
        public static string Body(MechanicCodexEntry e, Campaign.CampaignState state)
        {
            if (e == null)
            {
                return string.Empty;
            }
            switch (e.Kind)
            {
                case MechanicCodexKind.Firmware:
                    return Campaign.Signal.FirmwareLibrary.BuildDetail(state, e.ContentId);
                case MechanicCodexKind.Reaction:
                    return ReactionBody(e.ContentId, state);
                case MechanicCodexKind.Item:
                    return Campaign.Economy.EconomyCodex.ItemBody(e.ContentId, state);
                case MechanicCodexKind.Recipe:
                    return Campaign.Economy.EconomyCodex.RecipeBody(e.ContentId, state);
                default:
                    return GameLogic.Localization.GameText.Get(e.BodyKey);
            }
        }

        /// <summary>未解锁时的获取提示（剪影下面那一行）。</summary>
        public static string Hint(MechanicCodexEntry e, Campaign.CampaignState state)
        {
            if (e == null)
            {
                return string.Empty;
            }
            switch (e.Kind)
            {
                case MechanicCodexKind.Firmware:
                    return GameLogic.Localization.GameText.Format("codex.firmware.locked_body", Campaign.Signal.FirmwareKinds.AcquireText(e.ContentId));
                case MechanicCodexKind.Reaction:
                    if (!Campaign.Content.NamedReactionCatalog.TryGet(e.ContentId, out GameConfig.fg.Reaction row))
                    {
                        return string.Empty;
                    }
                    if (row.Reach != Campaign.Content.NamedReactionCatalog.Reachable)
                    {
                        return GameLogic.Localization.GameText.Get("reaction.codex.unreachable");
                    }
                    if (state != null && !Campaign.Content.NamedReactionCatalog.IsBatchOpen(state, row.Batch))
                    {
                        return GameLogic.Localization.GameText.Format("reaction.codex.closed", GameLogic.Localization.GameText.Get("reaction.batch." + row.Batch));
                    }
                    return GameLogic.Localization.GameText.Get("reaction.codex.locked");
                case MechanicCodexKind.Item:
                    // 没拿到的物品不剧透名字与用途，只写获取途径（表里的来源文字）。
                    return GameLogic.Localization.GameText.Format("codex.item.locked_hint", GameLogic.Localization.GameText.Get(e.HintKey));
                default:
                    return GameLogic.Localization.GameText.Format("codex.panel.locked_body", GameLogic.Localization.GameText.Get(e.HintKey));
            }
        }

        /// <summary>固件条目的图标 ID（剪影 = 同一张图标着黑色，不另出资源）；其它条目为空。</summary>
        public static string IconOf(MechanicCodexEntry e) =>
            e != null && e.Kind == MechanicCodexKind.Firmware && Campaign.Content.FirmwareCatalog.TryGet(e.ContentId, out Campaign.Content.MechanicalContentDef def) ? def.IconId : null;

        private static string ReactionBody(string reactionId, Campaign.CampaignState state)
        {
            if (!Campaign.Content.NamedReactionCatalog.TryGet(reactionId, out GameConfig.fg.Reaction row))
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(GameLogic.Localization.GameText.Get(row.DescKey));
            if (row.Kind == Campaign.Content.NamedReactionCatalog.KindAssembly)
            {
                string fw = Campaign.Content.MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
                sb.AppendLine(GameLogic.Localization.GameText.Format("codex.reaction.recipe_assembly", Campaign.Signal.FirmwareKinds.DisplayName(fw) ?? fw));
            }
            else
            {
                sb.AppendLine(GameLogic.Localization.GameText.Format("codex.reaction.recipe",
                    Campaign.Content.StatusTagCatalog.NameOf(row.TagA) ?? row.TagA, Campaign.Content.StatusTagCatalog.NameOf(row.TagB) ?? row.TagB));
            }
            var names = new List<string>();
            foreach (string fw in Campaign.Signal.FirmwareLibrary.FirmwareForReaction(reactionId))
            {
                // 没拿到的固件不剧透名字（与固件页签的剪影一致）。
                names.Add(IsUnlocked(FirmwareEntryId(fw)) ? Campaign.Signal.FirmwareKinds.DisplayName(fw) : GameLogic.Localization.GameText.Get("codex.panel.locked_title"));
            }
            if (names.Count > 0)
            {
                sb.AppendLine(GameLogic.Localization.GameText.Format("codex.reaction.firmware", string.Join(Campaign.Signal.FirmwareLibrary.Sep, names)));
            }
            Campaign.ReactionFirstTriggerRecord first = Campaign.Combat.ReactionFeedback.FirstRecordOf(state, reactionId);
            sb.AppendLine(first != null
                ? GameLogic.Localization.GameText.Format("reaction.codex.first", GameLogic.UI.Kit.ReactionAttributionView.TickText(first.Tick),
                    Campaign.Combat.CombatSites.SiteName(first.SiteId))
                : GameLogic.Localization.GameText.Get("codex.reaction.elsewhere"));
            return sb.ToString().TrimEnd();
        }

        public static MechanicCodexEntry Find(string id)
        {
            EnsureEntries();
            return id != null && _byId.TryGetValue(id, out MechanicCodexEntry e) ? e : null;
        }

        public static bool IsUnlocked(string id)
        {
            EnsureUnlocked();
            return id != null && _unlocked.Contains(id);
        }

        public static int UnlockedCount
        {
            get
            {
                EnsureEntries();
                EnsureUnlocked();
                int n = 0;
                foreach (MechanicCodexEntry e in _entries)
                {
                    if (_unlocked.Contains(e.Id))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>引导钩子被触发（每一次）：解锁以它为钩子的条目。新解锁时写盘一次。</summary>
        public static void OnHook(string hookId)
        {
            if (string.IsNullOrEmpty(hookId))
            {
                return;
            }
            EnsureEntries();
            EnsureUnlocked();
            bool changed = false;
            foreach (MechanicCodexEntry e in _entries)
            {
                if (Array.IndexOf(e.Hooks, hookId) >= 0 && _unlocked.Add(e.Id))
                {
                    changed = true;
                }
            }
            if (changed)
            {
                Revision++;
                Save();
            }
        }

        /// <summary>解锁一条（“?”按钮打开时）。返回 true = 这次才解锁。</summary>
        public static bool Unlock(string id)
        {
            if (Find(id) == null)
            {
                return false;
            }
            EnsureUnlocked();
            if (!_unlocked.Add(id))
            {
                return false;
            }
            Revision++;
            Save();
            return true;
        }

        /// <summary>请求打开图鉴并定位到 <paramref name="id"/>（“?”按钮、暂停菜单“图鉴”）。<paramref name="unlock"/> = 从该机制的界面上打开，顺带解锁。</summary>
        public static void Open(string id, bool unlock = true)
        {
            if (unlock && id != null)
            {
                Unlock(id);
            }
            OpenCount++;
            LastOpenedId = id;
            OpenRequested?.Invoke(id);
        }

        // ── 载入 / 保存 ──────────────────────────────────────────────────────────

        public static void Reload()
        {
            _entries = null;
            _byId = null;
            _unlocked = null;
            _loadError = null;
            Revision++;
        }

        public static void ResetForTests()
        {
            Reload();
            _loggedError = false;
            FilePathOverrideForTests = null;
            OpenRequested = null;
            SaveCount = 0;
            OpenCount = 0;
            LastOpenedId = null;
        }

        private static bool _loggedError;

        private static int _derivedKey;

        private static void EnsureEntries()
        {
            // 读表失败（配置还没载入 / 表缺失）时不缓存失败结果：下一次访问再试，配置载入后自动恢复。
            // FG2-FW-05：固件 / 反应表重载（或测试注入）后重建派生条目。
            int derivedKey = HashCode.Combine(Campaign.Content.FirmwareCatalog.Revision, Campaign.Content.NamedReactionCatalog.Revision, Campaign.Economy.ItemCatalog.Revision);
            if (_entries != null && _loadError == null && derivedKey == _derivedKey)
            {
                return;
            }
            if (_entries != null && _loadError == null)
            {
                _unlocked = null; // 条目集合变了：解锁集合按新条目重新过滤
                Revision++;
            }
            _derivedKey = derivedKey;
            _loadError = null;
            _unlocked = null;
            _entries = new List<MechanicCodexEntry>();
            _byId = new Dictionary<string, MechanicCodexEntry>(StringComparer.Ordinal);
            try
            {
                TbCodexEntry table = ConfigSystem.Instance.Tables?.TbCodexEntry;
                if (table == null)
                {
                    _loadError = "配置表 fg.TbCodexEntry 不存在";
                }
                else
                {
                    foreach (CodexEntry row in table.DataList)
                    {
                        var e = new MechanicCodexEntry
                        {
                            Id = row.Id,
                            Tab = row.Tab,
                            TitleKey = row.TitleKey,
                            BodyKey = row.BodyKey,
                            HintKey = row.HintKey,
                            SortOrder = row.SortOrder,
                            Links = Split(row.Links),
                            Hooks = Split(row.Hooks),
                        };
                        _entries.Add(e);
                        _byId[e.Id] = e;
                    }
                    AddDerivedEntries();
                    _entries.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                }
            }
            catch (Exception ex)
            {
                _loadError = "配置表读取失败：" + ex.Message;
            }
            if (_loadError != null && !_loggedError)
            {
                _loggedError = true;
                Log.Error($"[MechanicCodex] {_loadError}（改 tools/cell_tables/fgdata_hud.py 后重新生成）");
            }
        }

        /// <summary>FG2-FW-05：按固件表与反应表生成固件 / 反应页签的条目（排序沿用表顺序），并建立固件 ↔ 反应的互链。</summary>
        private static void AddDerivedEntries()
        {
            int order = 100000;
            var fwIds = new List<string>();
            foreach (GameConfig.fg.FirmwareKind row in Campaign.Signal.FirmwareKinds.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || _byId.ContainsKey(FirmwareEntryId(row.Id)))
                {
                    continue;
                }
                List<string> reactions = Campaign.Signal.FirmwareLibrary.ReactionsOf(row.Id);
                var links = new string[reactions.Count];
                for (int i = 0; i < reactions.Count; i++)
                {
                    links[i] = ReactionEntryId(reactions[i]);
                }
                var e = new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Firmware,
                    ContentId = row.Id,
                    Id = FirmwareEntryId(row.Id),
                    Tab = TabFirmware,
                    TitleKey = row.NameKey,
                    BodyKey = row.DescKey,
                    HintKey = row.AcquireKey,
                    SortOrder = order++,
                    Links = links,
                };
                _entries.Add(e);
                _byId[e.Id] = e;
                fwIds.Add(row.Id);
            }
            order = 200000;
            foreach (GameConfig.fg.Reaction row in Campaign.Content.NamedReactionCatalog.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || _byId.ContainsKey(ReactionEntryId(row.Id)))
                {
                    continue;
                }
                List<string> providers = Campaign.Signal.FirmwareLibrary.FirmwareForReaction(row.Id);
                var links = new string[providers.Count];
                for (int i = 0; i < providers.Count; i++)
                {
                    links[i] = FirmwareEntryId(providers[i]);
                }
                var e = new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Reaction,
                    ContentId = row.Id,
                    Id = ReactionEntryId(row.Id),
                    Tab = TabReaction,
                    TitleKey = row.NameKey,
                    BodyKey = row.DescKey,
                    HintKey = "reaction.codex.locked",
                    SortOrder = order++,
                    Links = links,
                };
                _entries.Add(e);
                _byId[e.Id] = e;
            }
            AddEconomyEntries();
        }

        /// <summary>
        /// FG4-ECO-01（卡片“每种物品和配方都有图鉴条目（来源和用途）”；FG04 第 4 节）：按物品表与配方表生成物品 / 配方页签的条目，
        /// 物品 ↔ 产出它 / 用到它的配方互相链接。物品表读不到时不生成（表错误已由 ItemCatalog 记 Error），其余页签照常。
        /// </summary>
        private static void AddEconomyEntries()
        {
            // 读的是 ItemCatalog 自己的 Revision 之后的内容；访问 Items 会触发载入（第一次时 Revision 前进），这里再取一次让派生键对上。
            IReadOnlyList<Campaign.Economy.ItemDef> items = Campaign.Economy.ItemCatalog.Items;
            _derivedKey = HashCode.Combine(Campaign.Content.FirmwareCatalog.Revision, Campaign.Content.NamedReactionCatalog.Revision, Campaign.Economy.ItemCatalog.Revision);
            int order = 300000;
            foreach (Campaign.Economy.ItemDef item in items)
            {
                string id = ItemEntryId(item.Id);
                if (_byId.ContainsKey(id))
                {
                    continue;
                }
                var links = new List<string>();
                foreach (Campaign.Economy.RecipeDef r in Campaign.Economy.ItemCatalog.ProducedBy(item.Id))
                {
                    links.Add(RecipeEntryId(r.Id));
                }
                foreach (Campaign.Economy.RecipeDef r in Campaign.Economy.ItemCatalog.ConsumedBy(item.Id))
                {
                    string rid = RecipeEntryId(r.Id);
                    if (!links.Contains(rid))
                    {
                        links.Add(rid);
                    }
                }
                var e = new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Item,
                    ContentId = item.Id,
                    Id = id,
                    Tab = TabItem,
                    TitleKey = item.NameKey,
                    BodyKey = item.DescKey,
                    HintKey = item.SourceKey,
                    SortOrder = order + item.SortOrder,
                    Links = links.ToArray(),
                    AlwaysOpen = item.CodexAlwaysOpen,
                };
                _entries.Add(e);
                _byId[e.Id] = e;
            }
            order = 400000;
            foreach (Campaign.Economy.RecipeDef r in Campaign.Economy.ItemCatalog.Recipes)
            {
                string id = RecipeEntryId(r.Id);
                if (_byId.ContainsKey(id))
                {
                    continue;
                }
                var links = new List<string>();
                foreach (Campaign.Economy.RecipeLine l in r.Lines)
                {
                    string iid = ItemEntryId(l.Item.Id);
                    if (!links.Contains(iid))
                    {
                        links.Add(iid);
                    }
                }
                var e = new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Recipe,
                    ContentId = r.Id,
                    Id = id,
                    Tab = TabRecipe,
                    TitleKey = r.NameKey,
                    BodyKey = r.NameKey,
                    HintKey = r.BuildingNameKey,
                    SortOrder = order + r.SortOrder,
                    Links = links.ToArray(),
                    AlwaysOpen = true,
                };
                _entries.Add(e);
                _byId[e.Id] = e;
            }
        }

        private static void EnsureUnlocked()
        {
            if (_unlocked != null)
            {
                return;
            }
            EnsureEntries();
            _unlocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in LoadFile())
            {
                if (_byId.ContainsKey(id))
                {
                    _unlocked.Add(id);
                }
            }
            // 老玩家：本机设置里已经见过的钩子（本 Story 之前触发过的）补解锁对应条目，不写盘（下一次真正解锁时一起写）。
            // FG4-ECO-01：一开始就能看的物品 / 配方条目直接算解锁（不写盘，也不算“新解锁”）。
            foreach (MechanicCodexEntry e in _entries)
            {
                if (e.AlwaysOpen)
                {
                    _unlocked.Add(e.Id);
                    continue;
                }
                foreach (string h in e.Hooks)
                {
                    if (GameSettings.HasSeenGuidanceHook(h))
                    {
                        _unlocked.Add(e.Id);
                        break;
                    }
                }
            }
        }

        private static IEnumerable<string> LoadFile()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return Array.Empty<string>();
                }
                MechanicCodexSaveData data = JsonUtility.FromJson<MechanicCodexSaveData>(File.ReadAllText(path));
                if (data == null || data.Version != FileVersion)
                {
                    Log.Warning($"[MechanicCodex] 图鉴文件版本不认识（{data?.Version}），按空图鉴继续：{path}");
                    return Array.Empty<string>();
                }
                return data.Unlocked ?? Array.Empty<string>();
            }
            catch (Exception e)
            {
                Log.Warning($"[MechanicCodex] 图鉴文件读取失败，按空图鉴继续：{e.Message}");
                return Array.Empty<string>();
            }
        }

        private static void Save()
        {
            // 编辑模式下（自检 / 编辑器工具触发钩子）不写玩家本机的图鉴文件：只有游戏运行时或自检指定了临时路径才落盘。
            if (FilePathOverrideForTests == null && Application.isEditor && !Application.isPlaying)
            {
                return;
            }
            try
            {
                var list = new List<string>(_unlocked);
                list.Sort(StringComparer.Ordinal);
                var data = new MechanicCodexSaveData { Version = FileVersion, Unlocked = list.ToArray() };
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(path, JsonUtility.ToJson(data));
                SaveCount++;
            }
            catch (Exception e)
            {
                Log.Warning($"[MechanicCodex] 图鉴文件写入失败（本局内仍然解锁）：{e.Message}");
            }
        }

        private static string[] Split(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return Array.Empty<string>();
            }
            var parts = new List<string>();
            foreach (string p in csv.Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0)
                {
                    parts.Add(t);
                }
            }
            return parts.ToArray();
        }
    }
}
