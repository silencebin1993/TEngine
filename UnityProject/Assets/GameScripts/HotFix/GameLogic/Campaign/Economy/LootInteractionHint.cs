using System;
using System.Collections.Generic;
using System.Diagnostics;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// EMG4-LOOT-01（FG19 FGR-EMG-191 / 192；FG08 FGR-LOOT-020 末条）：三选一每个候选“与你现有的固件 / 部件可产生 N 种新交互（已发现 M 种）”。
    /// - 候选的通道源 = 固件候选的固件、未解析模块候选的组件 / 结构模块（fg.TbChannelDecl 里有声明的 owner；材料、数据核心、通用组件不是通道源，不显示）。
    /// - “拥有” = 已解锁（已破解或直接可用）的固件 / 部件 / 底盘特性，固件库里的芯片（未破解的可由核心裸跑），以及世界常驻源（fg.TbFluidLeak 的泄漏流体）；
    ///   提供的端口 = 这些 owner 已生效声明里的写端 + 泄漏流体。
    /// - N = fg.TbChannelRule 里一侧端口是候选的读 / 写端、另一侧端口是“拥有”提供的端口、且归属的具名交互（named 列，fg.TbReaction 有行）本存档还没发现的种数；
    ///   已发现的计 M。同一种具名交互的多条规则行只算一种（与组合图鉴条目同口径）。
    /// - 候选带来玩家没有的触发事件种类（读端 trigger）或元程序（写 meta.*）另给“新语法”标签，不计入 N。
    /// - 只给信息：三个候选同样展示，不排序、不高亮、不推荐（不做智能延续）。
    /// 开销：打开 / 换到一个数据包时算一次，O(声明数 + 候选 × (声明数 + 规则数))，不按帧（面板缓存到数据包换了或重新打开）。规则只认端口 × 端口（FGR-EMG-013）。
    /// </summary>
    public static class LootInteractionHint
    {
        public sealed class Hint
        {
            /// <summary>候选对应的通道源 owner；null = 不是通道源（不显示这一行）。</summary>
            public string Owner;
            public int New;
            public int Found;
            /// <summary>悬停列表：已发现的写交互名；未发现的只写两侧端口所属的通道类别。</summary>
            public readonly List<string> Names = new List<string>();
            /// <summary>“新语法”标签（不计入 N）。</summary>
            public readonly List<string> Grammar = new List<string>();
        }

        public static long Computes { get; private set; }
        public static double LastMs { get; private set; }
        public static double MaxMs { get; private set; }

        public static void ResetSessionState()
        {
            Computes = 0;
            LastMs = 0;
            MaxMs = 0;
        }

        private static readonly HashSet<string> Provided = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> OwnedTriggers = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> OwnedMeta = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Chips = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Cand = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Counted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>候选的通道源（固件 / 加密固件 / 原型刻录数据 = 固件；未解析模块 = 它的组件身份）；其余 null。</summary>
        public static string OwnerOf(LootCandidateRecord c)
        {
            if (c == null || string.IsNullOrEmpty(c.TargetId))
            {
                return null;
            }
            return c.Kind == LootPickCatalog.KindFirmware || c.Kind == LootPickCatalog.KindCracked || c.Kind == LootPickCatalog.KindModule ? c.TargetId : null;
        }

        /// <summary>算一个数据包全部候选的提示（下标与候选一一对应，顺序不变）。</summary>
        public static Hint[] For(CampaignState s, LootPackageRecord p)
        {
            long t0 = Stopwatch.GetTimestamp();
            LootCandidateRecord[] cands = p?.Candidates ?? Array.Empty<LootCandidateRecord>();
            var result = new Hint[cands.Length];
            EmgCompiled c = EmgCatalog.Current;
            CollectOwned(s, c);
            IReadOnlyList<GameConfig.fg.ChannelRule> rules = Rules();
            for (int i = 0; i < cands.Length; i++)
            {
                result[i] = Build(s, c, rules, OwnerOf(cands[i]));
            }
            LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            MaxMs = Math.Max(MaxMs, LastMs);
            Computes++;
            return result;
        }

        /// <summary>候选行上的一句话（N = 0 时“暂无新交互”，B12）。</summary>
        public static string LineOf(Hint h)
        {
            if (h == null || h.Owner == null)
            {
                return string.Empty;
            }
            string line = h.New > 0 ? GameText.Format("loot.pick.hint", h.New, h.Found) : GameText.Format("loot.pick.hint.none", h.Found);
            return h.Grammar.Count == 0 ? line : line + "\n" + string.Join(GameText.Get("emg.chain.list_sep"), h.Grammar);
        }

        /// <summary>悬停展开的交互列表（没有能接上的交互时写“没有能接上的交互”）。</summary>
        public static string ListOf(Hint h)
        {
            if (h == null || h.Owner == null)
            {
                return string.Empty;
            }
            return h.Names.Count == 0 ? GameText.Get("loot.pick.hint.list_empty") : GameText.Get("loot.pick.hint.list_title") + "\n" + string.Join("\n", h.Names);
        }

        private static IReadOnlyList<GameConfig.fg.ChannelRule> Rules()
        {
            try
            {
                return global::ConfigSystem.Instance.Tables?.TbChannelRule?.DataList ?? (IReadOnlyList<GameConfig.fg.ChannelRule>)Array.Empty<GameConfig.fg.ChannelRule>();
            }
            catch (Exception)
            {
                return Array.Empty<GameConfig.fg.ChannelRule>(); // 表读不到：N = 0（EmgRuleCatalog 已记 Error）
            }
        }

        private static void CollectOwned(CampaignState s, EmgCompiled c)
        {
            Provided.Clear();
            OwnedTriggers.Clear();
            OwnedMeta.Clear();
            Chips.Clear();
            foreach (PrimitiveChipRecord r in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (r != null && !string.IsNullOrEmpty(r.CardDefId))
                {
                    Chips.Add(r.CardDefId);
                }
            }
            var owned = new bool[c.OwnerIds.Length];
            for (int o = 0; o < owned.Length; o++)
            {
                string id = c.OwnerIds[o];
                owned[o] = c.OwnerActive(o) && (Chips.Contains(id) || MechanicalContentUnlock.IsUnlocked(s, id));
            }
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Owner < 0 || d.Owner >= owned.Length || !owned[d.Owner] || !c.PortIdByCode.TryGetValue(d.Port, out string port))
                {
                    continue;
                }
                if ((d.Dir & EmgDirs.Write) != 0)
                {
                    Provided.Add(port);
                    if (EmgCatalog.FamilyOf(port) == EmgFamily.Meta)
                    {
                        OwnedMeta.Add(port);
                    }
                }
                else if (d.Role == EmgRole.Trigger)
                {
                    OwnedTriggers.Add(port);
                }
            }
            try
            {
                foreach (GameConfig.fg.FluidLeak l in global::ConfigSystem.Instance.Tables?.TbFluidLeak?.DataList ?? (IReadOnlyList<GameConfig.fg.FluidLeak>)Array.Empty<GameConfig.fg.FluidLeak>())
                {
                    if (l != null && !string.IsNullOrEmpty(l.Matter) && l.Matter != "none")
                    {
                        Provided.Add(l.Matter); // 世界常驻源：泄漏流体（FGR-EMG-191“世界常驻源”）
                    }
                }
            }
            catch (Exception)
            {
                // 泄漏表读不到：只按玩家拥有的内容算
            }
        }

        private static Hint Build(CampaignState s, EmgCompiled c, IReadOnlyList<GameConfig.fg.ChannelRule> rules, string owner)
        {
            var h = new Hint();
            if (owner == null || !c.OwnerIndex.TryGetValue(owner, out int oi))
            {
                return h; // 不是通道源（材料 / 数据核心 / 没有声明的内容）
            }
            h.Owner = owner;
            if (!c.OwnerActive(oi))
            {
                return h; // 声明还没生效（stageGate 高于当前阶段）：能接上的交互 = 0
            }
            Cand.Clear();
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Owner != oi || !c.PortIdByCode.TryGetValue(d.Port, out string port))
                {
                    continue;
                }
                Cand.Add(port);
                string tag = null;
                if ((d.Dir & EmgDirs.Write) != 0)
                {
                    if (EmgCatalog.FamilyOf(port) == EmgFamily.Meta && !OwnedMeta.Contains(port))
                    {
                        tag = GameText.Format("loot.pick.hint.grammar.meta", PortName(port));
                    }
                }
                else if (d.Role == EmgRole.Trigger && !OwnedTriggers.Contains(port))
                {
                    tag = GameText.Format("loot.pick.hint.grammar.evt", PortName(port));
                }
                if (tag != null && !h.Grammar.Contains(tag))
                {
                    h.Grammar.Add(tag);
                }
            }
            Counted.Clear();
            int stage = EmgCatalog.EffectiveStage;
            foreach (GameConfig.fg.ChannelRule r in rules)
            {
                if (r == null || r.Stage > stage)
                {
                    continue;
                }
                bool hit = (Cand.Contains(r.A) && Provided.Contains(r.B)) || (Cand.Contains(r.B) && Provided.Contains(r.A));
                if (!hit)
                {
                    continue;
                }
                string entry = r.Named != "none" ? r.Named : r.Id;
                if (!NamedReactionCatalog.TryGet(entry, out GameConfig.fg.Reaction row) || !Counted.Add(entry))
                {
                    continue; // 只算具名交互；同一种只算一次
                }
                if (Combat.ReactionFeedback.IsFirstTriggered(s, entry))
                {
                    h.Found++;
                    h.Names.Add(GameText.Format("loot.pick.hint.found", GameText.Get(row.NameKey)));
                }
                else
                {
                    h.New++;
                    h.Names.Add(GameText.Format("loot.pick.hint.pair", Progression.MechanicCodex.ChannelName(r.A), Progression.MechanicCodex.ChannelName(r.B)));
                }
            }
            return h;
        }

        private static string PortName(string port)
        {
            string key = "emg.port." + port + ".name";
            return GameText.Has(key) ? GameText.Get(key) : port;
        }
    }
}
