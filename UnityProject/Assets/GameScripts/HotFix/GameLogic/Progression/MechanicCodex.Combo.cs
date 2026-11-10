using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Localization;

namespace GameLogic.Progression
{
    /// <summary>
    /// EMG4-CODEX-01（FG19 FGR-EMG-183 / 194 / 162；界面编号 FGU-49 = 图鉴 FGU-38 的“组合图鉴”页签）：组合图鉴。
    /// - 具名交互条目（“reaction:”，fg.TbReaction 生成）：两侧端口（fg.TbChannelRule 按 named 列归到同一条目的全部端口对）、主结果（规则行 mainKind + 产物）、
    ///   本存档首次发现时的因果链快照（出手的一方 / 固件 / 部件 / 地点与坐标）、本存档已触发次数、“极限”标记。未发现：剪影 + “由哪两类通道产生”（只写通道类别，不给配方）。
    /// - 语法条目（“grammar:”）：第一次组成第 1 / 2 代子弹药、第一次用到每种元程序、第一次由每种事件唤起程序（记录在 CampaignState.GrammarDiscoveries，解锁进跨存档图鉴文件）。
    /// - 解锁跨存档共享（D-17，沿用图鉴文件）；快照 / 次数 / 极限按存档。只记录发现，不提供任何数值加成。
    /// 开销：只在打开面板 / 选中条目时读记录（O(记录数)），不按帧，不读内核。
    /// </summary>
    public static partial class MechanicCodex
    {
        public const string GrammarPrefix = "grammar:";

        public static string GrammarEntryId(string grammarId) => string.IsNullOrEmpty(grammarId) ? null : GrammarPrefix + grammarId;

        /// <summary>界面编号（FG13 界面编号表，FGR-EMG-038）。</summary>
        public const string ComboUiId = "FGU-49";

        /// <summary>版本递增（极限标记等不改解锁集合、但条目正文变了的情况）。</summary>
        public static void Touch() => Revision++;

        /// <summary>某种条目已解锁几条（成就 / 结局统计计数接口用）。O(条目数)。</summary>
        public static int UnlockedCountOf(MechanicCodexKind kind)
        {
            EnsureEntries();
            EnsureUnlocked();
            int n = 0;
            foreach (MechanicCodexEntry e in _entries)
            {
                if (e.Kind == kind && _unlocked.Contains(e.Id))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>语法条目清单：gen.1～gen.MaxGen、meta.0～meta.(种类数 − 1)、evt.1～evt.EventKinds（与 ReactionFeedback.CheckGrammar 写入的键一致）。</summary>
        public static IEnumerable<string> GrammarIds()
        {
            for (int g = 1; g <= BinGames.Sim.Combat.EmgConst.MaxGen; g++)
            {
                yield return ReactionDiscoveryService.GenEntry(g);
            }
            for (int m = 0; m < BinGames.Sim.Combat.EmgMetaConst.Kinds; m++)
            {
                yield return ReactionDiscoveryService.MetaEntry(m);
            }
            for (int e = 1; e <= BinGames.Sim.Combat.EmgConst.EventKinds; e++)
            {
                yield return ReactionDiscoveryService.EventEntry(e);
            }
        }

        private static void AddGrammarEntries(ref int order)
        {
            foreach (string gid in GrammarIds())
            {
                string id = GrammarEntryId(gid);
                if (_byId.ContainsKey(id))
                {
                    continue;
                }
                var e = new MechanicCodexEntry { Kind = MechanicCodexKind.Grammar, ContentId = gid, Id = id, Tab = TabReaction, SortOrder = order++ };
                _entries.Add(e);
                _byId[e.Id] = e;
            }
        }

        // ── 语法条目文字 ──

        private static string GrammarArg(string gid, out string kind)
        {
            int dot = gid.IndexOf('.');
            kind = dot > 0 ? gid.Substring(0, dot) : gid;
            string rest = dot > 0 ? gid.Substring(dot + 1) : string.Empty;
            if (kind == "meta" && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mk))
            {
                return EmgCatalog.Current.PortIdByCode.TryGetValue((ushort)(BinGames.Sim.Combat.EmgConst.PortMetaBase + mk), out string pid) ? GameText.Get("emg.port." + pid + ".name") : rest;
            }
            if (kind == "evt" && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ev))
            {
                return EmgProgramTreeText.EventName((BinGames.Sim.Combat.EmgEventKind)ev);
            }
            return rest;
        }

        private static string GrammarTitle(MechanicCodexEntry e) => GameText.Format("codex.grammar." + GrammarKind(e.ContentId), GrammarArg(e.ContentId, out _));

        private static string GrammarKind(string gid)
        {
            GrammarArg(gid, out string kind);
            return kind == "gen" || kind == "meta" ? kind : "evt";
        }

        private static string GrammarBody(MechanicCodexEntry e, CampaignState state)
        {
            string kind = GrammarKind(e.ContentId);
            var sb = new StringBuilder();
            sb.AppendLine(GameText.Format("codex.grammar.body." + kind, GrammarArg(e.ContentId, out _)));
            GrammarDiscoveryRecord first = null;
            foreach (GrammarDiscoveryRecord r in state?.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>())
            {
                if (r != null && r.EntryId == e.ContentId)
                {
                    first = r;
                    break;
                }
            }
            sb.Append(first != null
                ? GameText.Format("codex.grammar.first", UI.Kit.ReactionAttributionView.TickText(first.Tick), Campaign.Combat.CombatSites.SiteName(first.SiteId))
                : GameText.Get("codex.grammar.elsewhere"));
            return sb.ToString();
        }

        private static string GrammarHint(MechanicCodexEntry e) => GameText.Get("codex.grammar.hint." + GrammarKind(e.ContentId));

        // ── 具名交互条目：端口 / 主结果 / 快照 / 次数 / 极限 ──

        /// <summary>归到这个条目的规则行（named 列 = 条目 ID；named = none 的行用自己的 ID）。</summary>
        private static List<GameConfig.fg.ChannelRule> RuleRowsOf(string reactionId)
        {
            var rows = new List<GameConfig.fg.ChannelRule>(4);
            IReadOnlyList<GameConfig.fg.ChannelRule> all = null;
            try
            {
                all = global::ConfigSystem.Instance.Tables?.TbChannelRule?.DataList;
            }
            catch (Exception)
            {
                all = null; // 表读不到：条目照常显示说明，只是不列端口（EmgRuleCatalog 已记 Error）
            }
            foreach (GameConfig.fg.ChannelRule r in all ?? Array.Empty<GameConfig.fg.ChannelRule>())
            {
                if (r != null && (r.Named != "none" ? r.Named : r.Id) == reactionId)
                {
                    rows.Add(r);
                }
            }
            return rows;
        }

        /// <summary>端口 / 沾染位的玩家名（“coat:位”= 状态标签名）。</summary>
        private static string PortName(string portId)
        {
            if (string.IsNullOrEmpty(portId))
            {
                return string.Empty;
            }
            if (portId.StartsWith("coat:", StringComparison.Ordinal) && int.TryParse(portId.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit))
            {
                string tag = NamedReactionCatalog.TagOfBit(bit);
                return tag != null ? StatusTagCatalog.NameOf(tag) ?? tag : portId;
            }
            string key = "emg.port." + portId + ".name";
            return GameText.Has(key) ? GameText.Get(key) : portId;
        }

        /// <summary>规则产物的玩家名：不是端口的 ter.* / bhv.* / topo.* 走 emg.product.&lt;id&gt;.name，其余（mat.*、ter.cover）同端口名。</summary>
        public static string ProductName(string productId)
        {
            string key = "emg.product." + productId + ".name";
            return !string.IsNullOrEmpty(productId) && GameText.Has(key) ? GameText.Get(key) : PortName(productId);
        }

        /// <summary>端口所属通道类别名（物质 / 能量 / 事件……；沾染位 = “沾染”）。</summary>
        public static string ChannelName(string portId)
        {
            if (string.IsNullOrEmpty(portId))
            {
                return string.Empty;
            }
            if (portId.StartsWith("coat:", StringComparison.Ordinal))
            {
                return GameText.Get("emg.family.coat");
            }
            EmgFamily f = EmgCatalog.FamilyOf(portId);
            return f == EmgFamily.None ? portId : GameText.Get("emg.family." + f.ToString().ToLowerInvariant());
        }

        /// <summary>具名交互条目追加的组合图鉴段（接在 <see cref="ReactionBody"/> 的说明之后）。</summary>
        private static void AppendCombo(StringBuilder sb, string reactionId, CampaignState state)
        {
            string sep = GameText.Get("emg.chain.list_sep");
            List<GameConfig.fg.ChannelRule> rows = RuleRowsOf(reactionId);
            var pairs = new List<string>();
            var results = new List<string>();
            foreach (GameConfig.fg.ChannelRule r in rows)
            {
                string pair = GameText.Format("codex.combo.pair", PortName(r.A), PortName(r.B));
                if (!pairs.Contains(pair))
                {
                    pairs.Add(pair);
                }
                string res = GameText.Format("codex.combo.result_item", ProductName(r.Product), GameText.Get("codex.combo.main." + (r.MainKind ?? string.Empty).ToLowerInvariant()));
                if (!results.Contains(res))
                {
                    results.Add(res);
                }
            }
            if (pairs.Count > 0)
            {
                sb.AppendLine(GameText.Format("codex.combo.ports", string.Join(sep, pairs)));
                sb.AppendLine(GameText.Format("codex.combo.result", string.Join(sep, results)));
            }
            ReactionFirstTriggerRecord first = Campaign.Combat.ReactionFeedback.FirstRecordOf(state, reactionId);
            if (first == null)
            {
                return; // 本存档还没有：ReactionBody 已写“在别的存档里发现”
            }
            if (first.Source.Known)
            {
                sb.AppendLine(GameText.Format("codex.combo.first_by", Campaign.Combat.ReactionLog.PartyLabel(first.Source), NamesOf(first.FirmwareIds, true), NamesOf(first.PartIds, false)));
            }
            sb.AppendLine(GameText.Format("codex.combo.count", Math.Max(1L, first.Count).ToString(CultureInfo.InvariantCulture)));
            int reward = ReactionDiscoveryService.ExtremeReward;
            sb.Append(first.Extreme
                ? GameText.Format("codex.combo.extreme", UI.Kit.ReactionAttributionView.TickText(first.ExtremeTick), Campaign.Combat.CombatSites.SiteName(first.ExtremeSiteId), reward)
                : GameText.Format("codex.combo.extreme_none", ExtremeCount, reward));
        }

        private static int ExtremeCount => ReactionDiscoveryService.ExtremeCount;

        private static string NamesOf(string[] ids, bool firmware)
        {
            if (ids == null || ids.Length == 0)
            {
                return GameText.Get("codex.combo.none");
            }
            var names = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                names[i] = firmware ? Campaign.Signal.FirmwareKinds.DisplayName(ids[i]) ?? ids[i]
                    : MechanicalContentFacade.TryGet(ids[i], out MechanicalContentDef d) ? d.DisplayName : ids[i];
            }
            return string.Join(GameText.Get("emg.chain.list_sep"), names);
        }

        /// <summary>首次发现那一行（有坐标的写坐标）。</summary>
        private static string FirstLine(ReactionFirstTriggerRecord first) => first.HasPos
            ? GameText.Format("codex.combo.first", UI.Kit.ReactionAttributionView.TickText(first.Tick), Campaign.Combat.CombatSites.SiteName(first.SiteId),
                first.X.ToString("0", CultureInfo.InvariantCulture), first.Y.ToString("0", CultureInfo.InvariantCulture))
            : GameText.Format("codex.combo.first_nopos", UI.Kit.ReactionAttributionView.TickText(first.Tick), Campaign.Combat.CombatSites.SiteName(first.SiteId));

        /// <summary>未发现的具名交互：剪影下面只写由哪两类通道产生（取第一条规则行的两侧端口的通道类别；不写端口名与条件，不给配方）。没有规则行返回 null。</summary>
        private static string ComboHint(string reactionId)
        {
            List<GameConfig.fg.ChannelRule> rows = RuleRowsOf(reactionId);
            if (rows.Count == 0)
            {
                return null;
            }
            string a = ChannelName(rows[0].A), b = ChannelName(rows[0].B);
            return a == b ? GameText.Format("codex.combo.locked_hint_one", a) : GameText.Format("codex.combo.locked_hint", a, b);
        }

        /// <summary>本存档这条具名交互记过“极限”吗（列表条目前加 ◆，形状为主 B15）。</summary>
        public static bool IsExtreme(MechanicCodexEntry e, CampaignState state) =>
            e != null && e.Kind == MechanicCodexKind.Reaction && Campaign.Combat.ReactionFeedback.FirstRecordOf(state, e.ContentId)?.Extreme == true;

        /// <summary>列表里的条目名：未解锁 = 剪影名；记过极限的前面加 ◆。</summary>
        public static string ListTitle(MechanicCodexEntry e, CampaignState state)
        {
            if (!IsUnlocked(e?.Id))
            {
                return GameText.Get("codex.panel.locked_title");
            }
            string t = Title(e);
            return IsExtreme(e, state) ? "◆ " + t : t;
        }

        /// <summary>组合图鉴页签一条都没发现时的空状态提示（第一次打开）；其余情况 null。</summary>
        public static string EmptyHint(string tab) =>
            tab == TabReaction && UnlockedCountIn(TabReaction) == 0 ? GameText.Get("codex.combo.empty") : null;

        /// <summary>按存档补解锁具名交互与语法条目（旧档 / 换机：图鉴文件里没有、存档里有记录的）。返回补了几条（不写盘，由 SyncContent 统一写）。</summary>
        private static int SyncCombo(CampaignState s)
        {
            int n = 0;
            foreach (ReactionFirstTriggerRecord r in s?.ReactionFirstTriggers ?? Array.Empty<ReactionFirstTriggerRecord>())
            {
                if (r != null && AddUnlocked(ReactionEntryId(r.ReactionId)))
                {
                    n++;
                }
            }
            foreach (GrammarDiscoveryRecord g in s?.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>())
            {
                if (g != null && AddUnlocked(GrammarEntryId(g.EntryId)))
                {
                    n++;
                }
            }
            return n;
        }
    }
}
