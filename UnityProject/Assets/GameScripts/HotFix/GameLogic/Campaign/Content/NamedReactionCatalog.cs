using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Localization;
using TEngine;
using Unity.Mathematics;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG2-FW-03（FG02 FGR-FW-040～042；FGT-FW-003）：具名反应表 fg.TbReaction 的运行时入口（数据源 tools/cell_tables/fgdata_reaction.py）。
    ///
    /// - 表里的触发配对与效果全部来自旧反应引擎的行为探针（<see cref="MetabolicSlice.DebugTools.LegacyReactionProbe"/>），自检逐条比对“表 = 探针”。
    /// - 标签反应（kind = tag）翻译成战斗内核的 <see cref="CombatReactionRule"/>（<see cref="BuildKernelRules"/>，按 priority 排序，规则下标 = 内核事件的 Code），
    ///   内核只认状态位与数，不认识反应名；装配反应（标记跳转、熔穿过载）照旧由装配识别，这里只统一名字与开放批次。
    /// - 命名按阵营分批开放（FGR-FW-042）：第一幕（act1）开局就开放，其余批次由阵营推进时 <see cref="OpenBatch"/>（记在存档 <see cref="CampaignState.OpenReactionBatches"/>）。
    ///   没开放的反应照样按规则触发，只是不显示名字；机械内容下永远不会触发的（reach = unreachable）不开放命名。
    /// - 表加载失败时记 Error 并视为空表：没有标签反应（IC-REQ-013 可见失败，不崩）。
    /// </summary>
    public static class NamedReactionCatalog
    {
        public const string BatchAct1 = "act1";
        public const string KindTag = "tag";
        public const string KindAssembly = "assembly";
        public const string Reachable = "reachable";

        private static TbReaction _table;
        private static TbReaction _override;
        private static bool _loaded;
        private static string _loadError;
        private static List<Reaction> _tagRules = new List<Reaction>();
        private static readonly Dictionary<string, Reaction> _byLegacy = new Dictionary<string, Reaction>(StringComparer.Ordinal);

        /// <summary>重载时 +1（地点据此重新登记内核规则）。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<Reaction> Rows
        {
            get
            {
                EnsureLoaded();
                return _table?.DataList ?? (IReadOnlyList<Reaction>)Array.Empty<Reaction>();
            }
        }

        /// <summary>标签反应，按 priority 排序（下标 = 内核规则下标 = <see cref="CombatEventKind.TagReaction"/> 事件的 Code）。</summary>
        public static IReadOnlyList<Reaction> TagRules
        {
            get
            {
                EnsureLoaded();
                return _tagRules;
            }
        }

        public static bool TryGet(string reactionId, out Reaction row)
        {
            EnsureLoaded();
            row = null;
            return !string.IsNullOrEmpty(reactionId) && _table != null && _table.DataMap.TryGetValue(reactionId, out row) && row != null;
        }

        /// <summary>旧引擎反应名（HitEvent.Payload["Reaction"]，如 "Conduct"）→ 表行。</summary>
        public static bool TryGetByLegacyName(string legacyName, out Reaction row)
        {
            EnsureLoaded();
            row = null;
            return !string.IsNullOrEmpty(legacyName) && _byLegacy.TryGetValue(legacyName, out row);
        }

        /// <summary>内核规则下标 → 反应 ID；越界返回 null。</summary>
        public static string IdOfRule(int ruleIndex)
        {
            EnsureLoaded();
            return ruleIndex >= 0 && ruleIndex < _tagRules.Count ? _tagRules[ruleIndex].Id : null;
        }

        /// <summary>机械名（当前语言）。查不到返回 null。</summary>
        public static string NameOf(string reactionId) => TryGet(reactionId, out Reaction row) ? GameText.Get(row.NameKey) : null;

        /// <summary>说明（触发条件与效果，当前语言）。</summary>
        public static string DescriptionOf(string reactionId) => TryGet(reactionId, out Reaction row) ? GameText.Get(row.DescKey) : null;

        // ─────────────────────────────── 开放批次（FGR-FW-042）───────────────────────────────

        /// <summary>这一批的命名开放了没有：第一幕开局即开放；其余看存档里的开放记录。</summary>
        public static bool IsBatchOpen(CampaignState state, string batch)
        {
            if (batch == BatchAct1)
            {
                return true;
            }
            return state?.OpenReactionBatches != null && Array.IndexOf(state.OpenReactionBatches, batch) >= 0;
        }

        /// <summary>这条反应此刻对玩家显示名字吗：机械内容下可触发 + 所在批次已开放。</summary>
        public static bool IsNamed(CampaignState state, string reactionId) =>
            TryGet(reactionId, out Reaction row) && row.Reach == Reachable && IsBatchOpen(state, row.Batch);

        /// <summary>开放一批反应的命名（阵营推进时调用：化工 = FG10-REACT-01，超频 = FG11-REACT-01，跨阵营 = 第二幕后期）。
        /// 已开放 / 批次不存在返回 false。进存档。</summary>
        public static bool OpenBatch(CampaignState state, string batch)
        {
            if (state == null || string.IsNullOrEmpty(batch) || batch == BatchAct1 || IsBatchOpen(state, batch) || !Rows.Any(r => r.Batch == batch))
            {
                return false;
            }
            state.OpenReactionBatches = (state.OpenReactionBatches ?? Array.Empty<string>()).Concat(new[] { batch })
                .OrderBy(b => b, StringComparer.Ordinal).ToArray();
            return true;
        }

        // ─────────────────────────────── 翻译给内核 ───────────────────────────────

        /// <summary>标签反应 → 内核规则（与 <see cref="TagRules"/> 同序）。标签查不到内核位的规则 Pair = 0（永不触发）并记 Error。</summary>
        public static CombatReactionRule[] BuildKernelRules()
        {
            EnsureLoaded();
            var rules = new CombatReactionRule[_tagRules.Count];
            for (int i = 0; i < _tagRules.Count; i++)
            {
                Reaction row = _tagRules[i];
                uint a = BitOf(row.TagA);
                uint b = BitOf(row.TagB);
                if (a == 0u || b == 0u || a == b)
                {
                    Log.Error($"[NamedReactionCatalog] 反应 {row.Id} 的配料 {row.TagA} / {row.TagB} 在 fg.TbStatusTag 里没有内核位，这条反应不会触发。");
                }
                rules[i] = new CombatReactionRule
                {
                    Pair = a != 0u && b != 0u && a != b ? a | b : 0u,
                    Consume = BitsOf(row.Consume),
                    Grant = BitsOf(row.Grant),
                    DamageMult = row.DamageMult > 0f ? row.DamageMult : 1f,
                    ResidueBit = BitOf(row.Residue),
                    ResidueSeconds = Math.Max(0f, row.ResidueSeconds),
                    ResidueRadius = Math.Max(0f, row.ResidueRadius),
                    Key = StableKey(row.Id),
                };
            }
            return rules;
        }

        /// <summary>反应 ID 的稳定键（FNV-1a 32 位，非零）：内核按它把触发次数 / 伤害存进快照，表里 priority 顺序改了也不会记到别的反应上。</summary>
        public static int StableKey(string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId))
            {
                return 0;
            }
            uint h = 2166136261u;
            foreach (char c in reactionId)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h == 0u ? 1 : unchecked((int)h);
        }

        /// <summary>每个状态位的效果（32 格；fg.TbStatusTag 主标签的 effect / amount）。</summary>
        public static CombatStatusFx[] BuildStatusFx()
        {
            var fx = new CombatStatusFx[32];
            foreach (StatusTag row in StatusTagCatalog.Rows)
            {
                if (row == null || row.Kind != "status" || row.Bit < 0 || row.Bit > 30 || (row.AliasOf != null && row.AliasOf != "none"))
                {
                    continue;
                }
                fx[row.Bit] = new CombatStatusFx { Effect = EffectCode(row.Effect), Amount = row.Amount };
            }
            return fx;
        }

        /// <summary>头顶图标：每个状态位的形状序号与打包颜色（fg.TbStatusTag 的 shape / color；形状为主、颜色为辅）。没有图标的位 x = -1。</summary>
        public static float2[] BuildStatusVisuals()
        {
            var v = new float2[32];
            for (int b = 0; b < 32; b++)
            {
                v[b] = new float2(-1f, 0f);
            }
            foreach (StatusTag row in StatusTagCatalog.Rows)
            {
                if (row == null || row.Kind != "status" || row.Bit < 0 || row.Bit > 30 || (row.AliasOf != null && row.AliasOf != "none"))
                {
                    continue;
                }
                int shape = ShapeIndex(row.Shape);
                if (shape < 0 || !TryParseColor(row.Color, out uint rgb))
                {
                    Log.Error($"[NamedReactionCatalog] 状态标签 {row.Id} 的图标形状 {row.Shape} / 颜色 {row.Color} 无法识别，头顶不显示它。");
                    continue;
                }
                v[row.Bit] = new float2(shape, rgb);
            }
            return v;
        }

        /// <summary>头顶图标支持的形状字形（顺序 = 着色器里的形状序号）。</summary>
        public static readonly string[] Shapes = { "▲", "●", "◆", "◇", "★", "▼", "■", "☆", "◎", "○", "※", "△", "□", "▽" };

        public static int ShapeIndex(string glyph) => string.IsNullOrEmpty(glyph) ? -1 : Array.IndexOf(Shapes, glyph);

        public static bool TryParseColor(string hex, out uint rgb)
        {
            rgb = 0u;
            return !string.IsNullOrEmpty(hex) && hex.Length == 7 && hex[0] == '#'
                && uint.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
        }

        public static byte EffectCode(string effect) => effect switch
        {
            "dot" => CombatStatusFx.Dot,
            "slow" => CombatStatusFx.Slow,
            "vuln" => CombatStatusFx.Vuln,
            "leech" => CombatStatusFx.Leech,
            "execute" => CombatStatusFx.Execute,
            _ => CombatStatusFx.None,
        };

        /// <summary>主状态标签 → 内核位（查不到 / none 为 0）。</summary>
        public static uint BitOf(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "none")
            {
                return 0u;
            }
            return CarrierReadings.TryGetTagEffect(tag, out uint bit, out _, out _) ? bit : 0u;
        }

        public static uint BitsOf(string tags)
        {
            uint m = 0u;
            if (string.IsNullOrEmpty(tags))
            {
                return m;
            }
            foreach (string t in tags.Split(';'))
            {
                m |= BitOf(t.Trim());
            }
            return m;
        }

        /// <summary>内核位 → 主状态标签 ID（查不到返回 null）。</summary>
        public static string TagOfBit(int bit)
        {
            foreach (StatusTag row in StatusTagCatalog.Rows)
            {
                if (row != null && row.Kind == "status" && row.Bit == bit && (row.AliasOf == null || row.AliasOf == "none"))
                {
                    return row.Id;
                }
            }
            return null;
        }

        // ─────────────────────────────── 加载 / 测试注入 ───────────────────────────────

        public static void Reload()
        {
            _loaded = false;
            _table = null;
            _loadError = null;
            _tagRules = new List<Reaction>();
            _byLegacy.Clear();
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：替换反应表（null = 真实表）。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbReaction table)
        {
            _override = table;
            Reload();
        }

        public static void ResetForTests()
        {
            _override = null;
            Reload();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                _table = _override ?? ConfigSystem.Instance.Tables?.TbReaction;
                if (_table == null)
                {
                    _loadError = "配置表 fg.TbReaction 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[NamedReactionCatalog] {_loadError}");
                return;
            }
            _tagRules = _table.DataList.Where(r => r != null && r.Kind == KindTag)
                .OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).Take(CombatConst.MaxReactions).ToList();
            foreach (Reaction r in _table.DataList)
            {
                if (r != null && !string.IsNullOrEmpty(r.LegacyName) && r.LegacyName != "none")
                {
                    _byLegacy[r.LegacyName] = r;
                }
            }
        }
    }
}
