using System;
using System.Collections.Generic;
using System.Text;
using GameConfig;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Localization;

namespace GameLogic.Progression
{
    /// <summary>
    /// FG9-UX-01（FG13 FGR-UX-050 / 051；FG09 FGR-FAC-005；验收 FGT-UX-006、FGT-FAC-008）：正式版图鉴的其余页签——
    /// 组件、底盘、建筑、状态标签、敌人、阵营、区域、事件（天气 + 区域危害 + 事件类系统说明）、资料库（剧情）。
    /// 全部由已有的内容目录 / 表生成（加一行表就多一条条目），标题 / 正文 / 获取提示走文本键，复用同一个图鉴文件（<see cref="CodexPersistence"/> 的方式）。
    /// - 机制条目跨存档共享（同一个 codex_mechanics.json）：首次接触时解锁——敌人 = 首次遭遇（交上火 / 在战场上看到 / 来袭家园），
    ///   阵营 = 遭遇它的敌人或进入它的领地，区域 = 第一次进入领地，天气 = 第一次经历，危害 = 第一次发现，状态标签 = 第一次在单位身上看到或拿到能施加它的固件，
    ///   组件 / 底盘 / 建筑 = 基础蓝图与“开局即可建造”的一开始就能看（规划要用），其余在内容解锁 / 研究完成时解锁。
    /// - 资料库（剧情）按存档：是否“解锁”只看当前存档有没有收到（<see cref="Narrative.NarrativeService.Has"/>），永远不写进跨存档文件——新存档里看不到别的存档的剧情。
    /// - 互链：组件 / 底盘 → 形变说明；建筑 → 解锁它的研究节点；状态标签 ↔ 参与的反应 ↔ 施加它的固件；敌人 ↔ 阵营、敌人 ↔ 弱点相关的反应 / 组件 / 机制（fg.TbMechEnemy.codexLinks，双向）；
    ///   区域 ↔ 阵营、区域 ↔ 危害。
    /// 开销：条目在表 / 语言版本变化时重建（O(条目数)）；遭遇解锁走本局去重集合，战斗事件里 O(1)。
    /// </summary>
    public static partial class MechanicCodex
    {
        public const string ComponentPrefix = "comp:";
        public const string ChassisPrefix = "chassis:";
        public const string BuildingPrefix = "building:";
        public const string StatusPrefix = "tag:";
        public const string EnemyPrefix = "enemy:";
        public const string FactionPrefix = "faction:";
        public const string RegionPrefix = "region:";
        public const string WeatherPrefix = "weather:";
        public const string HazardPrefix = "hazard:";
        public const string LorePrefix = "lore:";

        /// <summary>“新增图鉴条目”通知类型（fg.TbNotifyType，info 级，聚合）。</summary>
        public const string NotifyType = "codex_new";

        /// <summary>事件页签里的事件类系统说明（不是由表生成的条目，fg.TbCodexEntry 里 tab=event 的行）也在这一页。</summary>
        public static string ComponentEntryId(string id) => string.IsNullOrEmpty(id) ? null : ComponentPrefix + id;
        public static string ChassisEntryId(string id) => string.IsNullOrEmpty(id) ? null : ChassisPrefix + id;
        public static string BuildingEntryId(string id) => string.IsNullOrEmpty(id) ? null : BuildingPrefix + id;
        public static string StatusEntryId(string tag) => string.IsNullOrEmpty(tag) ? null : StatusPrefix + StatusTagCatalog.Canonical(tag);
        public static string EnemyEntryId(string id) => string.IsNullOrEmpty(id) ? null : EnemyPrefix + id;
        public static string FactionEntryId(string id) => string.IsNullOrEmpty(id) ? null : FactionPrefix + id;
        public static string RegionEntryId(string id) => string.IsNullOrEmpty(id) ? null : RegionPrefix + id;
        public static string WeatherEntryId(string id) => string.IsNullOrEmpty(id) ? null : WeatherPrefix + id;
        public static string HazardEntryId(string id) => string.IsNullOrEmpty(id) ? null : HazardPrefix + id;
        public static string LoreEntryId(string id) => string.IsNullOrEmpty(id) ? null : LorePrefix + id;

        public static bool IsLoreId(string id) => id != null && id.StartsWith(LorePrefix, StringComparison.Ordinal);

        /// <summary>自检：资料库条目按哪个存档判断（默认当前会话）。</summary>
        public static Func<CampaignState> LoreStateProvider = () => CampaignSession.Current;

        /// <summary>本局里第一次遭遇的计数（自检读点）。</summary>
        public static int EncounterCount { get; private set; }
        public static int NotifyCount { get; private set; }

        private static readonly HashSet<string> EncounterSeen = new HashSet<string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _titleIndex;
        private static int _titleIndexKey;

        private static int DerivedKey()
        {
            int a = HashCode.Combine(FirmwareCatalog.Revision, NamedReactionCatalog.Revision, Campaign.Economy.ItemCatalog.Revision, Campaign.Economy.ResearchCatalog.Revision);
            int b = HashCode.Combine(ComponentCatalog.Revision, ChassisCatalog.Revision, FgContentTables.Revision, Campaign.Grid.GridContent.Revision,
                StatusTagCatalog.Revision, Campaign.WorldGen.WorldGenContent.Revision, Narrative.NarrativeCatalog.Revision, TablesIdentity());
            return HashCode.Combine(a, b);
        }

        private static int TablesIdentity()
        {
            try
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ConfigSystem.Instance.Tables);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ── 生成 ──────────────────────────────────────────────────────────────────

        private static void AddContentEntries()
        {
            AddComponentEntries();
            AddBuildingEntries();
            AddStatusEntries();
            AddEnemyEntries();
            AddRegionEntries();
            AddEventEntries();
            AddLoreEntries();
        }

        private static void Add(MechanicCodexEntry e)
        {
            if (e.Id == null || _byId.ContainsKey(e.Id))
            {
                return;
            }
            _entries.Add(e);
            _byId[e.Id] = e;
        }

        private static void AddLink(string fromId, string toId)
        {
            if (fromId == null || toId == null || !_byId.TryGetValue(fromId, out MechanicCodexEntry from) || Array.IndexOf(from.Links, toId) >= 0)
            {
                return;
            }
            var links = new string[from.Links.Length + 1];
            Array.Copy(from.Links, links, from.Links.Length);
            links[links.Length - 1] = toId;
            from.Links = links;
        }

        private static void AddComponentEntries()
        {
            int order = 700000;
            foreach (KeyValuePair<string, MechanicalContentDef> kv in ComponentCatalog.All)
            {
                MechanicalContentDef d = kv.Value;
                if (d == null || (d.Category != MechanicalContentCategory.MainComponent && d.Category != MechanicalContentCategory.FunctionComponent
                                  && d.Category != MechanicalContentCategory.Structure))
                {
                    continue;
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Component, ContentId = d.Id, Id = ComponentEntryId(d.Id), Tab = TabComponent,
                    SortOrder = order + (int)d.Category * 1000 + _entries.Count % 1000, Links = new[] { "codex.firmware.morph" },
                    AlwaysOpen = d.Source == MechanicalContentSource.BaseBlueprint,
                });
            }
            order = 710000;
            foreach (KeyValuePair<string, MechanicalContentDef> kv in ChassisCatalog.All)
            {
                MechanicalContentDef d = kv.Value;
                if (d == null)
                {
                    continue;
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Chassis, ContentId = d.Id, Id = ChassisEntryId(d.Id), Tab = TabChassis, SortOrder = order++,
                    Links = new[] { "codex.firmware.morph" }, AlwaysOpen = d.Source == MechanicalContentSource.BaseBlueprint,
                });
            }
        }

        private static void AddBuildingEntries()
        {
            int order = 720000;
            foreach (Campaign.Grid.BuildEntry b in Campaign.Grid.BuildCatalog.All)
            {
                if (b == null || string.IsNullOrEmpty(b.Id))
                {
                    continue;
                }
                string node = Campaign.Economy.ResearchGate.NodeOf(b.UnlockRule);
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Building, ContentId = b.Id, Id = BuildingEntryId(b.Id), Tab = TabBuilding, SortOrder = order++,
                    TitleKey = b.DescKey, HintKey = b.UnlockHintKey,
                    Links = BuildingLinks(node),
                    AlwaysOpen = b.UnlockRule == "always",
                });
                if (node != null)
                {
                    AddLink(ResearchEntryId(node), BuildingEntryId(b.Id));
                }
            }
        }

        private static bool IsMainStatus(GameConfig.fg.StatusTag row) =>
            row != null && row.Kind == "status" && (string.IsNullOrEmpty(row.AliasOf) || row.AliasOf == "none");

        private static void AddStatusEntries()
        {
            int order = 730000;
            foreach (GameConfig.fg.StatusTag row in StatusTagCatalog.Rows)
            {
                if (!IsMainStatus(row))
                {
                    continue;
                }
                var links = new List<string>();
                foreach (GameConfig.fg.Reaction r in NamedReactionCatalog.Rows)
                {
                    if (r != null && (StatusTagCatalog.Canonical(r.TagA) == row.Id || StatusTagCatalog.Canonical(r.TagB) == row.Id) && _byId.ContainsKey(ReactionEntryId(r.Id)))
                    {
                        links.Add(ReactionEntryId(r.Id));
                    }
                }
                foreach (string fw in FirmwareApplying(row.Id))
                {
                    links.Add(FirmwareEntryId(fw));
                }
                string id = StatusPrefix + row.Id;
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.StatusTag, ContentId = row.Id, Id = id, Tab = TabStatus, TitleKey = row.NameKey, SortOrder = order++, Links = links.ToArray(),
                });
                foreach (string l in links)
                {
                    AddLink(l, id); // 反应 / 固件 → 状态标签（双向）
                }
            }
        }

        /// <summary>固件表里会施加这个标签的固件（tags 列，按主标签合并）。</summary>
        private static List<string> FirmwareApplying(string mainTag)
        {
            var list = new List<string>();
            foreach (GameConfig.fg.FirmwareKind fw in Campaign.Signal.FirmwareKinds.Rows)
            {
                if (fw == null || string.IsNullOrEmpty(fw.Tags) || fw.Tags == "none")
                {
                    continue;
                }
                foreach (string t in Split(fw.Tags))
                {
                    if (StatusTagCatalog.Canonical(t) == mainTag)
                    {
                        list.Add(fw.Id);
                        break;
                    }
                }
            }
            return list;
        }

        private static void AddEnemyEntries()
        {
            int order = 740000;
            var factions = new List<string>();
            foreach (GameConfig.fg.MechEnemy row in EnemyDefs.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Id))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(row.Faction) && row.Faction != "none" && !factions.Contains(row.Faction))
                {
                    factions.Add(row.Faction);
                }
                var links = new List<string> { FactionEntryId(row.Faction) };
                foreach (string l in Split(row.CodexLinks))
                {
                    if (l != "none")
                    {
                        links.Add(l);
                    }
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Enemy, ContentId = row.Id, Id = EnemyEntryId(row.Id), Tab = TabEnemy, TitleKey = row.NameKey, BodyKey = row.DescKey,
                    HintKey = row.WeaknessKey, SortOrder = order + FactionOrder(row.Faction) * 100 + TierOrder(row.Tier) * 30 + _entries.Count % 30, Links = links.ToArray(),
                });
            }
            // 领地里的阵营（还没有敌人入表的阵营也有条目：进入它的领地即解锁）。
            foreach (GameConfig.fg.Territory t in Campaign.WorldGen.WorldGenContent.Territories)
            {
                if (t != null && t.Faction == 1 && !factions.Contains(t.Id))
                {
                    factions.Add(t.Id);
                }
            }
            order = 750000;
            foreach (string f in factions)
            {
                var links = new List<string>();
                foreach (GameConfig.fg.MechEnemy row in EnemyDefs.Rows)
                {
                    if (row != null && row.Faction == f)
                    {
                        links.Add(EnemyEntryId(row.Id));
                    }
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Faction, ContentId = f, Id = FactionEntryId(f), Tab = TabFaction, TitleKey = "faction." + f,
                    BodyKey = "codex.faction." + f + ".body", SortOrder = order + FactionOrder(f), Links = links.ToArray(),
                });
            }
            // 敌人弱点的互链是双向的：被链接的反应 / 组件 / 机制条目也链回敌人（不存在的 ID 由自检报出，这里跳过）。
            foreach (GameConfig.fg.MechEnemy row in EnemyDefs.Rows)
            {
                foreach (string l in Split(row?.CodexLinks))
                {
                    if (l != "none" && _byId.ContainsKey(l) && !l.StartsWith(FactionPrefix, StringComparison.Ordinal))
                    {
                        AddLink(l, EnemyEntryId(row.Id));
                    }
                }
            }
        }

        private static int FactionOrder(string f) => f switch
        {
            "silent" => 1,
            "foundry" => 2,
            "clarity" => 3,
            "overclock" => 4,
            "fleet" => 5,
            _ => 9,
        };

        private static int TierOrder(string tier) => tier == EnemyDefs.TierBoss ? 2 : tier == "elite" ? 1 : 0;

        private static void AddRegionEntries()
        {
            int order = 760000;
            foreach (GameConfig.fg.Territory t in Campaign.WorldGen.WorldGenContent.Territories)
            {
                if (t == null || string.IsNullOrEmpty(t.Id) || _byId.ContainsKey(RegionEntryId(t.Id)))
                {
                    continue;
                }
                var links = new List<string>();
                if (t.Faction == 1 && _byId.ContainsKey(FactionEntryId(t.Id)))
                {
                    links.Add(FactionEntryId(t.Id));
                    AddLink(FactionEntryId(t.Id), RegionEntryId(t.Id));
                }
                links.Add("codex.world.territories");
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Region, ContentId = t.Id, Id = RegionEntryId(t.Id), Tab = TabRegion, TitleKey = t.NameKey,
                    SortOrder = order + t.Act * 100 + t.MinDistance / 100, Links = links.ToArray(),
                });
            }
        }

        private static IReadOnlyList<GameConfig.fg.Weather> WeatherRows()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbWeather?.DataList ?? (IReadOnlyList<GameConfig.fg.Weather>)Array.Empty<GameConfig.fg.Weather>();
            }
            catch (Exception)
            {
                return Array.Empty<GameConfig.fg.Weather>();
            }
        }

        private static void AddEventEntries()
        {
            int order = 770000;
            foreach (GameConfig.fg.Weather w in WeatherRows())
            {
                if (w == null || string.IsNullOrEmpty(w.Id) || w.Id == Campaign.Env.WeatherService.Clear)
                {
                    continue; // 晴天是常态，不算事件
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Weather, ContentId = w.Id, Id = WeatherEntryId(w.Id), Tab = TabEvent, TitleKey = w.NameKey, BodyKey = w.EffectKey,
                    SortOrder = order + w.SortOrder, Links = _byId.ContainsKey("codex.env.weather") ? new[] { "codex.env.weather" } : Array.Empty<string>(),
                });
                AddLink("codex.env.weather", WeatherEntryId(w.Id));
            }
            order = 780000;
            foreach (GameConfig.fg.Hazard h in Campaign.Env.HomeExplorationService.Hazards)
            {
                if (h == null || string.IsNullOrEmpty(h.Id))
                {
                    continue;
                }
                var links = new List<string>();
                if (_byId.ContainsKey(RegionEntryId(h.TerritoryId)))
                {
                    links.Add(RegionEntryId(h.TerritoryId));
                    AddLink(RegionEntryId(h.TerritoryId), HazardEntryId(h.Id));
                }
                if (_byId.ContainsKey("codex.env.hazard"))
                {
                    links.Add("codex.env.hazard");
                    AddLink("codex.env.hazard", HazardEntryId(h.Id));
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Hazard, ContentId = h.Id, Id = HazardEntryId(h.Id), Tab = TabEvent, TitleKey = h.NameKey, BodyKey = h.EffectKey,
                    HintKey = h.CounterKey, SortOrder = order + h.SortOrder, Links = links.ToArray(),
                });
            }
        }

        private static void AddLoreEntries()
        {
            int order = 800000;
            foreach (Narrative.NarrativeEntryDef d in Narrative.NarrativeCatalog.All)
            {
                if (d == null)
                {
                    continue;
                }
                Add(new MechanicCodexEntry
                {
                    Kind = MechanicCodexKind.Lore, ContentId = d.Id, Id = LoreEntryId(d.Id), Tab = TabLore, TitleKey = d.TitleKey, BodyKey = d.BodyKey,
                    SortOrder = order + d.SortOrder, Links = new[] { "codex.narrative" },
                });
            }
        }

        // ── 文字 ──────────────────────────────────────────────────────────────────

        private static string ContentTitle(MechanicCodexEntry e)
        {
            switch (e.Kind)
            {
                case MechanicCodexKind.Component:
                case MechanicCodexKind.Chassis:
                    return MechanicalContentFacade.TryGet(e.ContentId, out MechanicalContentDef d) ? d.DisplayName : e.ContentId;
                case MechanicCodexKind.Building:
                    return Campaign.Grid.BuildCatalog.TryGet(e.ContentId, out Campaign.Grid.BuildEntry b) ? b.Name : e.ContentId;
                case MechanicCodexKind.StatusTag:
                    return StatusTagCatalog.NameOf(e.ContentId) ?? e.ContentId;
                case MechanicCodexKind.Enemy:
                    return EnemyDefs.NameOf(e.ContentId) ?? e.ContentId;
                case MechanicCodexKind.Faction:
                    return GameText.Has(e.TitleKey) ? GameText.Get(e.TitleKey) : e.ContentId;
                case MechanicCodexKind.Lore:
                    return Narrative.NarrativeService.TitleOf(Narrative.NarrativeCatalog.Find(e.ContentId));
                default:
                    return GameText.Get(e.TitleKey);
            }
        }

        private static string Line(string key, params object[] args) => GameText.Format(key, args);

        private static string ContentBody(MechanicCodexEntry e, CampaignState state)
        {
            var sb = new StringBuilder();
            switch (e.Kind)
            {
                case MechanicCodexKind.Component:
                    ComponentBody(sb, e.ContentId);
                    break;
                case MechanicCodexKind.Chassis:
                    ChassisBody(sb, e.ContentId);
                    break;
                case MechanicCodexKind.Building:
                    BuildingBody(sb, e.ContentId, state);
                    break;
                case MechanicCodexKind.StatusTag:
                    StatusBody(sb, e);
                    break;
                case MechanicCodexKind.Enemy:
                    EnemyBody(sb, e.ContentId);
                    break;
                case MechanicCodexKind.Faction:
                    FactionBody(sb, e);
                    break;
                case MechanicCodexKind.Region:
                    RegionBody(sb, e.ContentId);
                    break;
                case MechanicCodexKind.Weather:
                    sb.AppendLine(Line("codex.label.glyph", Campaign.Env.WeatherService.GlyphOf(e.ContentId)));
                    sb.AppendLine(Line("codex.label.effect", Campaign.Env.WeatherService.EffectText(e.ContentId)));
                    sb.AppendLine(GameText.Get("codex.weather.body_tail"));
                    break;
                case MechanicCodexKind.Hazard:
                    HazardBody(sb, e);
                    break;
                case MechanicCodexKind.Lore:
                    LoreBody(sb, e.ContentId, state ?? LoreStateProvider?.Invoke());
                    break;
            }
            return sb.ToString().TrimEnd();
        }

        private static void ComponentBody(StringBuilder sb, string id)
        {
            if (!MechanicalContentFacade.TryGet(id, out MechanicalContentDef d))
            {
                return;
            }
            sb.AppendLine(d.Description);
            string artNote = ComponentCatalog.ArtPlaceholderNote(id);
            if (artNote != null)
            {
                sb.AppendLine(artNote); // EMG1-TXT-01：占位外观角标（B22，DEBT-EMG1TXT01-01）
            }
            sb.AppendLine(Line("codex.label.slot", GameText.Get(SlotKey(d.Category))));
            string subtype = CarrierReadings.SubtypeName(id);
            if (subtype != null)
            {
                sb.AppendLine(Line("codex.label.carrier", subtype));
            }
            if (!string.IsNullOrEmpty(d.ValuesSummary))
            {
                sb.AppendLine(Line("codex.label.values", d.ValuesSummary));
            }
            sb.AppendLine(Line("codex.label.load", d.Load, d.ScrapCost));
            if (!CarrierReadings.CanMount(CarrierReadings.FixedChassisId, id, out string reasonKey, out string adjustedKey))
            {
                sb.AppendLine(Line("codex.label.turret_no", GameText.Format(reasonKey, d.DisplayName)));
            }
            else if (adjustedKey != null)
            {
                sb.AppendLine(Line("codex.label.turret_adjusted", GameText.Get(adjustedKey)));
            }
            else
            {
                sb.AppendLine(GameText.Get("codex.label.turret_ok"));
            }
            sb.AppendLine(Line("codex.label.source", GameText.Get(SourceKey(d.Source))));
            AppendMorph(sb);
        }

        private static void ChassisBody(StringBuilder sb, string id)
        {
            if (!MechanicalContentFacade.TryGet(id, out MechanicalContentDef d))
            {
                return;
            }
            sb.AppendLine(d.Description);
            if (!string.IsNullOrEmpty(d.ValuesSummary))
            {
                sb.AppendLine(Line("codex.label.values", d.ValuesSummary));
            }
            sb.AppendLine(Line("codex.label.source", GameText.Get(SourceKey(d.Source))));
            if (CarrierReadings.IsFixedChassis(id))
            {
                sb.AppendLine(GameText.Get("codex.chassis.fixed"));
            }
            AppendMorph(sb);
        }

        /// <summary>三套机身状态：外形 + 由哪类固件引起 + 已拿到的这类固件（没拿到的不剧透名字）。</summary>
        private static void AppendMorph(StringBuilder sb)
        {
            sb.AppendLine(GameText.Get("codex.morph.title"));
            AppendMorphLine(sb, Campaign.Blueprint.MorphMask.Limiter, "limiter");
            AppendMorphLine(sb, Campaign.Blueprint.MorphMask.Fluid, "fluid");
            AppendMorphLine(sb, Campaign.Blueprint.MorphMask.Electromagnetic, "em");
        }

        private static void AppendMorphLine(StringBuilder sb, Campaign.Blueprint.MorphMask mask, string key)
        {
            var owned = new List<string>();
            int total = 0;
            foreach (GameConfig.fg.FirmwareKind fw in Campaign.Signal.FirmwareKinds.Rows)
            {
                if (fw == null || Campaign.Blueprint.MachineMorph.BitOf(fw.Id) != mask)
                {
                    continue;
                }
                total++;
                if (IsUnlocked(FirmwareEntryId(fw.Id)))
                {
                    owned.Add(Campaign.Signal.FirmwareKinds.DisplayName(fw.Id));
                }
            }
            string have = owned.Count > 0 ? string.Join(Campaign.Signal.FirmwareLibrary.Sep, owned) : GameText.Get("codex.morph.none_owned");
            sb.AppendLine(Line("codex.morph.line", GameText.Get("morph.state." + key), GameText.Get("codex.morph.look." + key),
                GameText.Get("firmware.category." + key), have, total));
        }

        private static string SlotKey(MechanicalContentCategory c) => c switch
        {
            MechanicalContentCategory.MainComponent => "codex.slot.main",
            MechanicalContentCategory.FunctionComponent => "codex.slot.function",
            MechanicalContentCategory.Structure => "codex.slot.structure",
            _ => "codex.slot.chassis",
        };

        /// <summary>来源枚举的文本键（写全键名，文本键扫描能核对到）。</summary>
        private static string SourceKey(MechanicalContentSource s) => s switch
        {
            MechanicalContentSource.BaseBlueprint => "codex.source.base_blueprint",
            MechanicalContentSource.SilentRuinsSalvage => "codex.source.silent_ruins_salvage",
            MechanicalContentSource.FoundryMandatory => "codex.source.foundry_mandatory",
            MechanicalContentSource.FoundryOptionalCache => "codex.source.foundry_optional_cache",
            MechanicalContentSource.RegionEncounter => "codex.source.region_encounter",
            MechanicalContentSource.EliteDrop => "codex.source.elite_drop",
            MechanicalContentSource.FactionBoss => "codex.source.faction_boss",
            MechanicalContentSource.Fusion => "codex.source.fusion",
            _ => "codex.source.other",
        };

        private static void BuildingBody(StringBuilder sb, string id, CampaignState state)
        {
            if (!Campaign.Grid.BuildCatalog.TryGet(id, out Campaign.Grid.BuildEntry b))
            {
                return;
            }
            sb.AppendLine(GameText.Get(b.DescKey));
            if (Campaign.Grid.GridContent.TryGetCategory(b.CategoryId, out GameConfig.fg.BuildCategory cat))
            {
                sb.AppendLine(Line("codex.label.category", GameText.Get(cat.NameKey)));
            }
            if (b.IsTool)
            {
                sb.AppendLine(GameText.Get("codex.building.tool"));
            }
            else if (b.Building != null)
            {
                sb.AppendLine(Line("codex.label.footprint", b.Building.FootprintW, b.Building.FootprintH));
                if (b.Building.MaxCount > 0)
                {
                    sb.AppendLine(Line("codex.label.max_count", b.Building.MaxCount));
                }
                if (!string.IsNullOrEmpty(b.Building.RequiredTerrain) && b.Building.RequiredTerrain != "none"
                    && Campaign.Grid.GridContent.TryTerrainCode(b.Building.RequiredTerrain, out byte code))
                {
                    GameConfig.fg.GridTerrain terrain = Campaign.Grid.GridContent.TerrainByCode(code);
                    sb.AppendLine(Line("codex.label.terrain", terrain != null ? GameText.Get(terrain.NameKey) : b.Building.RequiredTerrain));
                }
            }
            sb.AppendLine(Line("codex.label.unlock", b.UnlockRule == "always" ? GameText.Get("codex.unlock.always") : Campaign.Economy.ResearchGate.Describe(state, b.UnlockRule)));
        }

        private static void StatusBody(StringBuilder sb, MechanicCodexEntry e)
        {
            string shape = StatusTagCatalog.ShapeOf(e.ContentId);
            if (shape != null)
            {
                sb.AppendLine(Line("codex.label.shape", shape));
            }
            string effectKey = "tag." + e.ContentId.ToLowerInvariant() + ".effect";
            sb.AppendLine(GameText.Has(effectKey) ? Line("codex.label.effect", GameText.Get(effectKey)) : GameText.Get("codex.status.no_effect"));
            var reactions = new List<string>();
            var firmware = new List<string>();
            foreach (string l in e.Links)
            {
                MechanicCodexEntry t = Find(l);
                if (t == null)
                {
                    continue;
                }
                string name = IsUnlocked(l) ? Title(t) : GameText.Get("codex.panel.locked_title");
                if (t.Kind == MechanicCodexKind.Reaction)
                {
                    reactions.Add(name);
                }
                else if (t.Kind == MechanicCodexKind.Firmware)
                {
                    firmware.Add(name);
                }
            }
            if (reactions.Count > 0)
            {
                sb.AppendLine(Line("codex.label.reactions", string.Join(Campaign.Signal.FirmwareLibrary.Sep, reactions)));
            }
            if (firmware.Count > 0)
            {
                sb.AppendLine(Line("codex.label.applied_by", string.Join(Campaign.Signal.FirmwareLibrary.Sep, firmware)));
            }
        }

        private static void EnemyBody(StringBuilder sb, string id)
        {
            if (!EnemyDefs.TryGet(id, out GameConfig.fg.MechEnemy row))
            {
                return;
            }
            sb.AppendLine(EnemyDefs.DescText(row));
            sb.AppendLine(Line("codex.label.role", GameText.Get("enemy.role." + row.Role), GameText.Get("enemy.tier." + row.Tier)));
            sb.AppendLine(Line("codex.label.weakness", GameText.Get(row.WeaknessKey)));
            sb.AppendLine(Line("codex.label.faction", IsUnlocked(FactionEntryId(row.Faction)) ? GameText.Get("faction." + row.Faction) : GameText.Get("codex.panel.locked_title")));
            var loot = new List<string>();
            if (row.ScrapLoot > 0)
            {
                loot.Add(Line("codex.loot.scrap", row.ScrapLoot));
            }
            if (row.LootSource == "elite" || row.LootSource == "minion")
            {
                loot.Add(GameText.Get(row.LootSource == "elite" ? "codex.loot.elite" : "codex.loot.minion"));
            }
            if (!string.IsNullOrEmpty(row.KeyDrop) && row.KeyDrop != "none")
            {
                Campaign.Economy.ItemDef item = Campaign.Economy.ItemCatalog.Find(row.KeyDrop);
                loot.Add(Line("codex.loot.key", item != null ? GameText.Get(item.NameKey) : GameText.Get("codex.loot.key_item")));
            }
            sb.AppendLine(Line("codex.label.loot", loot.Count > 0 ? string.Join(Campaign.Signal.FirmwareLibrary.Sep, loot) : GameText.Get("codex.loot.none")));
            sb.AppendLine(Line("codex.label.hp", row.MaxHp.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            string trait = EnemyDefs.TraitText(row);
            if (trait != null)
            {
                sb.AppendLine(Line("codex.label.trait", trait));
            }
        }

        private static void FactionBody(StringBuilder sb, MechanicCodexEntry e)
        {
            sb.AppendLine(GameText.Has(e.BodyKey) ? GameText.Get(e.BodyKey) : string.Empty);
            var members = new List<string>();
            var leaders = new List<string>();
            foreach (string l in e.Links)
            {
                MechanicCodexEntry t = Find(l);
                if (t == null || t.Kind != MechanicCodexKind.Enemy)
                {
                    continue;
                }
                string name = IsUnlocked(l) ? Title(t) : GameText.Get("codex.panel.locked_title");
                if (EnemyDefs.TryGet(t.ContentId, out GameConfig.fg.MechEnemy row) && EnemyDefs.IsBoss(row) && row.Role != EnemyDefs.RoleStructure)
                {
                    leaders.Add(name);
                }
                else
                {
                    members.Add(name);
                }
            }
            sb.AppendLine(Line("codex.label.members", members.Count > 0 ? string.Join(Campaign.Signal.FirmwareLibrary.Sep, members) : GameText.Get("codex.faction.no_members")));
            if (leaders.Count > 0)
            {
                sb.AppendLine(Line("codex.label.leader", string.Join(Campaign.Signal.FirmwareLibrary.Sep, leaders)));
            }
        }

        private static GameConfig.fg.Territory TerritoryRow(string id)
        {
            foreach (GameConfig.fg.Territory t in Campaign.WorldGen.WorldGenContent.Territories)
            {
                if (t != null && t.Id == id)
                {
                    return t;
                }
            }
            return null;
        }

        private static void RegionBody(StringBuilder sb, string id)
        {
            GameConfig.fg.Territory t = TerritoryRow(id);
            if (t == null)
            {
                return;
            }
            foreach (GameConfig.fg.TerritoryTemplate tpl in Campaign.WorldGen.WorldGenContent.TemplateRows)
            {
                if (tpl != null && tpl.TerritoryId == id && GameText.Has(tpl.IntroKey))
                {
                    sb.AppendLine(GameText.Get(tpl.IntroKey));
                    break;
                }
            }
            sb.AppendLine(Line("codex.label.act", t.Act));
            sb.AppendLine(t.Faction == 1
                ? Line("codex.label.region_faction", IsUnlocked(FactionEntryId(id)) ? GameText.Get("faction." + id) : GameText.Get("codex.panel.locked_title"))
                : GameText.Get("codex.region.reserved"));
            sb.AppendLine(Line("codex.label.distance", t.MinDistance, t.MaxDistance));
            if (t.HazardKind != "none")
            {
                sb.AppendLine(Line("codex.label.hazard_belt", GameText.Get(t.HazardKey)));
            }
        }

        private static void HazardBody(StringBuilder sb, MechanicCodexEntry e)
        {
            sb.AppendLine(Line("codex.label.effect", GameText.Get(e.BodyKey)));
            sb.AppendLine(Line("codex.label.counter", GameText.Get(e.HintKey)));
            foreach (GameConfig.fg.Hazard h in Campaign.Env.HomeExplorationService.Hazards)
            {
                if (h != null && h.Id == e.ContentId)
                {
                    GameConfig.fg.Territory t = TerritoryRow(h.TerritoryId);
                    if (t != null)
                    {
                        sb.AppendLine(Line("codex.label.where", IsUnlocked(RegionEntryId(t.Id)) ? GameText.Get(t.NameKey) : GameText.Get("codex.panel.locked_title")));
                    }
                    break;
                }
            }
        }

        private static void LoreBody(StringBuilder sb, string id, CampaignState s)
        {
            Narrative.NarrativeEntryDef def = Narrative.NarrativeCatalog.Find(id);
            NarrativeEntryRecord rec = Narrative.NarrativeService.Find(s, id);
            if (def == null || rec == null)
            {
                return;
            }
            sb.AppendLine(Line("codex.lore.meta", Narrative.NarrativeService.SourceName(def.Source), rec.Act, rec.Day));
            sb.AppendLine(Narrative.NarrativeService.BodyOf(def));
            sb.AppendLine(GameText.Get("codex.lore.library_hint"));
        }

        private static string ContentHint(MechanicCodexEntry e, CampaignState state)
        {
            switch (e.Kind)
            {
                case MechanicCodexKind.Component:
                case MechanicCodexKind.Chassis:
                    return MechanicalContentFacade.TryGet(e.ContentId, out MechanicalContentDef d)
                        ? Line("codex.content.locked_hint", GameText.Get(SourceKey(d.Source)))
                        : string.Empty;
                case MechanicCodexKind.Building:
                    return GameText.Has(e.HintKey) ? Line("codex.building.locked_hint", GameText.Get(e.HintKey)) : GameText.Get("codex.building.locked_default");
                case MechanicCodexKind.StatusTag:
                    return GameText.Get("codex.status.locked_hint");
                case MechanicCodexKind.Enemy:
                {
                    // 没遭遇过的敌人不剧透名字与弱点：只写在哪个阵营（阵营已解锁时）出没。
                    string faction = EnemyDefs.TryGet(e.ContentId, out GameConfig.fg.MechEnemy row) && IsUnlocked(FactionEntryId(row.Faction))
                        ? GameText.Get("faction." + row.Faction)
                        : GameText.Get("codex.panel.locked_title");
                    return Line("codex.enemy.locked_hint", faction);
                }
                case MechanicCodexKind.Faction:
                    return GameText.Get("codex.faction.locked_hint");
                case MechanicCodexKind.Region:
                {
                    GameConfig.fg.Territory t = TerritoryRow(e.ContentId);
                    return t != null ? Line("codex.region.locked_hint", t.MinDistance, t.MaxDistance) : string.Empty;
                }
                case MechanicCodexKind.Weather:
                    return GameText.Get("codex.weather.locked_hint");
                case MechanicCodexKind.Hazard:
                    return GameText.Get("codex.hazard.locked_hint");
                case MechanicCodexKind.Lore:
                {
                    Narrative.NarrativeEntryDef def = Narrative.NarrativeCatalog.Find(e.ContentId);
                    return Line("codex.lore.locked_hint", def != null ? Narrative.NarrativeService.SourceName(def.Source) : string.Empty);
                }
                default:
                    return string.Empty;
            }
        }

        private static string ContentIcon(MechanicCodexEntry e)
        {
            if (e == null)
            {
                return null;
            }
            switch (e.Kind)
            {
                case MechanicCodexKind.Component:
                case MechanicCodexKind.Chassis:
                case MechanicCodexKind.Enemy:
                    return MechanicalContentFacade.TryGet(e.ContentId, out MechanicalContentDef d) && !string.IsNullOrEmpty(d.IconId) && d.IconId != "none" ? d.IconId : null;
                default:
                    return null;
            }
        }

        private static bool LoreUnlocked(string id)
        {
            CampaignState s = LoreStateProvider?.Invoke();
            return s != null && Narrative.NarrativeService.Has(s, id.Substring(LorePrefix.Length));
        }

        // ── 解锁入口 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 首次遭遇某种敌人（FGR-FAC-005 / FGT-FAC-008）：战斗内核报告它受伤 / 被击毁 / 打中己方机器、光标停在它身上、或它作为突袭攻城单位来到家园。
        /// 解锁敌人条目与所属阵营。本局去重集合保证战斗事件里 O(1)；跨存档的“第一次”由图鉴文件判断（新解锁时发一条“图鉴新增”通知）。
        /// </summary>
        public static void OnEnemyEncountered(string enemyTypeId)
        {
            if (string.IsNullOrEmpty(enemyTypeId) || !EncounterSeen.Add(enemyTypeId))
            {
                return;
            }
            EncounterCount++;
            UnlockAndNotify(EnemyEntryId(enemyTypeId));
            if (EnemyDefs.TryGet(enemyTypeId, out GameConfig.fg.MechEnemy row))
            {
                UnlockAndNotify(FactionEntryId(row.Faction));
                // FG10-FAC-01（审查修复）：第一次遭遇空中敌人（按 airborne 标签，不认敌人 ID）→ 引导钩子，解锁“空中单位”机制条目。
                if (EnemyDefs.HasTag(row, EnemyDefs.TagAirborne))
                {
                    Core.GuidanceHooks.Raise("clarity.first_air");
                }
            }
        }

        /// <summary>第一次进入某片领地（远征队伍中心进入 / 镜头焦点进入）：解锁区域条目；阵营领地顺带解锁阵营。</summary>
        public static void OnTerritoryEntered(string territoryId)
        {
            if (string.IsNullOrEmpty(territoryId))
            {
                return;
            }
            UnlockAndNotify(RegionEntryId(territoryId));
            UnlockAndNotify(FactionEntryId(territoryId));
        }

        /// <summary>一段天气开始（家园天气前线）：解锁该天气的事件条目。</summary>
        public static void OnWeatherStarted(string weatherId) => UnlockAndNotify(WeatherEntryId(weatherId));

        /// <summary>第一次揭开某种区域危害：解锁危害条目。</summary>
        public static void OnHazardSeen(string hazardId) => UnlockAndNotify(HazardEntryId(hazardId));

        /// <summary>在单位身上看到状态标签（悬停读数）：解锁状态条目（同义标签按主标签）。</summary>
        public static void OnStatusSeen(string engineTag)
        {
            if (StatusTagCatalog.IsStatus(engineTag))
            {
                Unlock(StatusEntryId(engineTag));
            }
        }

        /// <summary>建筑条目的相关条目：解锁它的研究、建造说明；世界里悬停建筑跳到本条后，电网与“为什么不工作”系统说明一键可达（FG9-UX-01 复审 P2）。</summary>
        private static string[] BuildingLinks(string node)
        {
            var links = new List<string>(4);
            if (node != null && _byId.ContainsKey(ResearchEntryId(node)))
            {
                links.Add(ResearchEntryId(node));
            }
            links.Add("codex.build.building");
            foreach (string sys in BuildingSystemLinks)
            {
                if (_byId.ContainsKey(sys))
                {
                    links.Add(sys);
                }
            }
            return links.ToArray();
        }

        private static readonly string[] BuildingSystemLinks = { "codex.logistics.power", "codex.logistics.diagnosis" };

        private static void UnlockAndNotify(string id)
        {
            if (id == null || !Unlock(id))
            {
                return;
            }
            NotifyCount++;
            MechanicCodexEntry e = Find(id);
            Notifications.NotificationCenter.Post(NotifyType, GameText.Format("codex.notify.detail", GameText.Get(TabTitleKey(e.Tab)), Title(e)));
        }

        /// <summary>页签名的文本键（写全键名，文本键扫描能核对到）。界面与通知共用。</summary>
        public static string TabTitleKey(string tab) => tab switch
        {
            TabFirmware => "codex.tab.firmware",
            TabReaction => "codex.tab.reaction",
            TabItem => "codex.tab.item",
            TabRecipe => "codex.tab.recipe",
            TabResearch => "codex.tab.research",
            TabHeritage => "codex.tab.heritage",
            TabComponent => "codex.tab.component",
            TabChassis => "codex.tab.chassis",
            TabBuilding => "codex.tab.building",
            TabStatus => "codex.tab.status",
            TabEnemy => "codex.tab.enemy",
            TabFaction => "codex.tab.faction",
            TabRegion => "codex.tab.region",
            TabEvent => "codex.tab.event",
            TabLore => "codex.tab.lore",
            _ => "codex.tab.system",
        };

        /// <summary>
        /// 按存档补解锁（进入战役、打开图鉴 / 固件库时由 <see cref="Campaign.Signal.FirmwareLibrary.SyncCodex"/> 调）：已解锁的组件 / 底盘内容、已解锁的建筑、
        /// 已揭开的危害、拿到过的固件能施加的状态标签、打出过的反应涉及的标签、本存档已击毁的敌人。旧存档 / 换机后补齐。O(条目数 + 敌人记录数)。返回新解锁条数。
        /// </summary>
        public static int SyncContent(CampaignState s)
        {
            if (s == null)
            {
                return 0;
            }
            EnsureEntries();
            int n = 0;
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameConfig.fg.FirmwareKind fw in Campaign.Signal.FirmwareKinds.Rows)
            {
                if (fw != null && IsUnlocked(FirmwareEntryId(fw.Id)))
                {
                    foreach (string t in Split(fw.Tags))
                    {
                        tags.Add(StatusTagCatalog.Canonical(t));
                    }
                }
            }
            foreach (ReactionFirstTriggerRecord r in s.ReactionFirstTriggers ?? Array.Empty<ReactionFirstTriggerRecord>())
            {
                if (r != null && NamedReactionCatalog.TryGet(r.ReactionId, out GameConfig.fg.Reaction row))
                {
                    tags.Add(StatusTagCatalog.Canonical(row.TagA));
                    tags.Add(StatusTagCatalog.Canonical(row.TagB));
                }
            }
            var entries = new List<MechanicCodexEntry>(_entries);
            foreach (MechanicCodexEntry e in entries)
            {
                if (e.AlwaysOpen || IsUnlocked(e.Id))
                {
                    continue;
                }
                bool open = e.Kind switch
                {
                    MechanicCodexKind.Component => MechanicalContentUnlock.IsUnlocked(s, e.ContentId),
                    MechanicCodexKind.Chassis => MechanicalContentUnlock.IsUnlocked(s, e.ContentId),
                    MechanicCodexKind.Building => Campaign.Grid.BuildCatalog.TryGet(e.ContentId, out Campaign.Grid.BuildEntry b) && Campaign.Grid.BuildCatalog.IsUnlocked(s, b),
                    MechanicCodexKind.Hazard => Campaign.Env.HomeExplorationService.HazardSeen(s, e.ContentId),
                    MechanicCodexKind.StatusTag => tags.Contains(e.ContentId),
                    _ => false,
                };
                if (open && AddUnlocked(e.Id))
                {
                    n++;
                }
            }
            bool extra = false;
            foreach (RegionEnemyRecord r in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (r != null && !r.IsAlive && AddUnlocked(EnemyEntryId(r.EnemyTypeId)))
                {
                    n++;
                    if (EnemyDefs.TryGet(r.EnemyTypeId, out GameConfig.fg.MechEnemy row))
                    {
                        extra |= AddUnlocked(FactionEntryId(row.Faction));
                    }
                }
            }
            // B18：旧档第一次进入战役可能一次补几十条——只写一次盘、只递增一次版本（标题索引只重建一次）。
            CommitBatch(n > 0 || extra);
            return n;
        }

        /// <summary>
        /// FGR-UX-051“从任何悬停提示按一个键跳到对应条目”的兜底：提示没有显式写 <see cref="UI.Kit.TooltipContent.CodexEntryId"/> 时，按提示标题（当前语言的条目名）
        /// 找同名条目——建筑、物品、组件、敌人、状态标签等的悬停提示标题就是它们的名字。资料库条目不参与（剧情标题不当作链接）。
        /// 索引在条目 / 语言版本变化时重建（O(条目数)），查询 O(1)。
        /// </summary>
        public static string ResolveTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return null;
            }
            EnsureEntries();
            int key = HashCode.Combine(Revision, (int)GameText.Language, GameText.Revision);
            if (_titleIndex == null || key != _titleIndexKey)
            {
                var index = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (MechanicCodexEntry e in _entries)
                {
                    if (e.Kind == MechanicCodexKind.Lore)
                    {
                        continue;
                    }
                    string t = Title(e);
                    if (!string.IsNullOrEmpty(t) && !index.ContainsKey(t))
                    {
                        index[t] = e.Id;
                    }
                }
                _titleIndex = index;
                _titleIndexKey = HashCode.Combine(Revision, (int)GameText.Language, GameText.Revision);
            }
            return _titleIndex.TryGetValue(title.Trim(), out string id) ? id : null;
        }

        /// <summary>自检：清掉本局遭遇去重与计数。</summary>
        public static void ResetContentForTests()
        {
            EncounterSeen.Clear();
            EncounterCount = 0;
            NotifyCount = 0;
            _titleIndex = null;
            LoreStateProvider = () => CampaignSession.Current;
        }

        /// <summary>换战役时（新建 / 读档）清本局遭遇去重：跨存档的解锁仍在图鉴文件里。</summary>
        public static void OnCampaignChanged()
        {
            EncounterSeen.Clear();
        }
    }
}
