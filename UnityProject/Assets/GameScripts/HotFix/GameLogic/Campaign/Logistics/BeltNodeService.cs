using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG3-LOG-04（FGR-LOG-022；卡片“必须同时交付：过滤器预设”）：分流器 / 合流器设置的服务端——
    /// - 过滤与优先口的玩家名（悬停、节点面板、堵塞原因共用）；
    /// - 过滤器预设：内置预设来自表 fg.TbBeltFilterPreset（均分、溢流、2:1、分拣……），自定义预设由玩家在节点面板“存为自定义预设”，
    ///   跟着存档（<see cref="BeltItemState.FilterPresets"/>，最多 logistics.splitter.custom_presets_max 个）；选中即套用到分流器。
    /// 设置真正生效在内核（<see cref="BeltKernel.SetSplitter"/> / <see cref="BeltKernel.SetMergerPriority"/>），这里只做翻译与存档，全部 O(预设数)，只在玩家操作时发生。
    /// </summary>
    public static class BeltNodeService
    {
        /// <summary>一个预设（内置或自定义）。</summary>
        public sealed class Preset
        {
            public string Id;
            public string Name;
            public string Desc;
            public int RatioL = 1;
            public int RatioR = 1;
            public BeltSide Priority;
            public ushort FilterL;
            public ushort FilterR;
            public bool Custom;
            /// <summary>自定义预设的编号（“自定义 N”）；内置为 0。</summary>
            public int Serial;
        }

        /// <summary>自定义预设上限（表 logistics.splitter.custom_presets_max，初值 8）。</summary>
        public static int CustomMax => Math.Max(1, GridContent.TuningInt("logistics.splitter.custom_presets_max"));

        /// <summary>最近一次套用 / 保存 / 删除预设的结果文字（状态行、自检）。</summary>
        public static string LastMessage { get; private set; }

        // ── 名称 ───────────────────────────────────────────────────────────────

        /// <summary>过滤的玩家名：全部物品 / 关闭（不出） / 只放 X。</summary>
        public static string FilterName(ushort filter)
        {
            if (filter == BeltConst.FilterAny)
            {
                return GameText.Get("logistics.filter.any");
            }
            if (filter == BeltConst.FilterNone)
            {
                return GameText.Get("logistics.filter.none");
            }
            return GameText.Format("logistics.filter.item", BeltItems.Name(filter));
        }

        /// <summary>左 / 右口的玩家名（不设 = “不设”）。</summary>
        public static string SideName(BeltSide side) =>
            GameText.Get(side == BeltSide.Left ? "logistics.side.left" : side == BeltSide.Right ? "logistics.side.right" : "logistics.side.none");

        /// <summary>过滤下拉框的选项（顺序固定）：全部物品、每种可存物品、关闭；当前值是别的物品编号时也列进去（不丢玩家的设置）。</summary>
        public static void CollectFilterChoices(List<ushort> into, ushort current)
        {
            into.Clear();
            into.Add(BeltConst.FilterAny);
            var storable = new List<ushort>(4);
            BeltItems.CollectStorable(storable);
            into.AddRange(storable);
            if (current != BeltConst.FilterAny && current != BeltConst.FilterNone && !into.Contains(current))
            {
                into.Add(current);
            }
            into.Add(BeltConst.FilterNone);
        }

        /// <summary>“1:1、优先不设、左口全部物品、右口只放废料”（自定义预设的说明与名字后缀）。</summary>
        public static string Summary(int ratioL, int ratioR, BeltSide priority, ushort filterL, ushort filterR) =>
            GameText.Format("logistics.nodepanel.preset_summary", ratioL, ratioR, SideName(priority), FilterName(filterL), FilterName(filterR));

        // ── 表值解析 ─────────────────────────────────────────────────────────────

        /// <summary>表里的过滤写法（any / none / scrap）→ 内核过滤值。</summary>
        public static ushort ParseFilter(string text)
        {
            switch ((text ?? string.Empty).Trim())
            {
                case "none":
                    return BeltConst.FilterNone;
                case "scrap":
                    return BeltItems.ScrapId;
                default:
                    return BeltConst.FilterAny;
            }
        }

        /// <summary>表里的优先口写法（none / left / right）。</summary>
        public static BeltSide ParseSide(string text)
        {
            switch ((text ?? string.Empty).Trim())
            {
                case "left":
                    return BeltSide.Left;
                case "right":
                    return BeltSide.Right;
                default:
                    return BeltSide.None;
            }
        }

        // ── 预设 ───────────────────────────────────────────────────────────────

        /// <summary>全部预设：内置（按表的排序）在前，自定义（按保存先后）在后。</summary>
        public static void CollectPresets(CampaignState state, List<Preset> into)
        {
            into.Clear();
            TbBeltFilterPreset table = ConfigSystem.Instance?.Tables?.TbBeltFilterPreset;
            if (table != null)
            {
                var rows = new List<BeltFilterPreset>(table.DataList);
                rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                foreach (BeltFilterPreset r in rows)
                {
                    into.Add(new Preset
                    {
                        Id = r.Id,
                        Name = GameText.Get(r.NameKey),
                        Desc = GameText.Get(r.DescKey),
                        RatioL = Math.Max(1, Math.Min(BeltConst.RatioMax, r.RatioL)),
                        RatioR = Math.Max(1, Math.Min(BeltConst.RatioMax, r.RatioR)),
                        Priority = ParseSide(r.Priority),
                        FilterL = ParseFilter(r.FilterL),
                        FilterR = ParseFilter(r.FilterR),
                    });
                }
            }
            foreach (BeltFilterPresetRecord c in state?.Belts?.FilterPresets ?? Array.Empty<BeltFilterPresetRecord>())
            {
                if (c == null)
                {
                    continue;
                }
                into.Add(FromRecord(c));
            }
        }

        private static Preset FromRecord(BeltFilterPresetRecord c)
        {
            int rl = Math.Max(1, Math.Min(BeltConst.RatioMax, c.RatioL));
            int rr = Math.Max(1, Math.Min(BeltConst.RatioMax, c.RatioR));
            var side = (BeltSide)Math.Max(0, Math.Min(2, c.Priority));
            var fl = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, c.FilterL));
            var fr = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, c.FilterR));
            string summary = Summary(rl, rr, side, fl, fr);
            return new Preset
            {
                Id = "custom." + c.Serial.ToString(CultureInfo.InvariantCulture),
                Name = GameText.Format("logistics.nodepanel.preset_custom", c.Serial, summary),
                Desc = summary,
                RatioL = rl,
                RatioR = rr,
                Priority = side,
                FilterL = fl,
                FilterR = fr,
                Custom = true,
                Serial = c.Serial,
            };
        }

        /// <summary>把预设套用到 <paramref name="cell"/> 上的分流器（立刻生效）。</summary>
        public static bool TryApplyPreset(CampaignState state, GridCell cell, Preset preset, out string reason)
        {
            reason = null;
            if (preset == null)
            {
                reason = GameText.Get("logistics.reason.invalid_argument");
                return false;
            }
            BeltOpResult r = BeltNetworkService.TrySetSplitter(state, cell, preset.RatioL, preset.RatioR, preset.Priority, preset.FilterL, preset.FilterR);
            if (!r.Ok)
            {
                reason = r.Describe();
                return false;
            }
            LastMessage = GameText.Format("logistics.nodepanel.preset_applied", preset.Name);
            return true;
        }

        /// <summary>把 <paramref name="cell"/> 上分流器的当前设置存成自定义预设（满了给原因，不覆盖旧的）。</summary>
        public static bool TrySaveCustom(CampaignState state, GridCell cell, out Preset saved, out string reason)
        {
            saved = null;
            reason = null;
            if (state?.Belts == null || !BeltNetworkService.TryGetNode(cell, out BeltNodeInfo n) || n.Kind != BeltNodeKind.Splitter)
            {
                reason = GameText.Get("logistics.reason.not_found");
                return false;
            }
            BeltFilterPresetRecord[] old = state.Belts.FilterPresets ?? Array.Empty<BeltFilterPresetRecord>();
            if (old.Length >= CustomMax)
            {
                reason = GameText.Format("logistics.nodepanel.preset_full", CustomMax);
                return false;
            }
            var rec = new BeltFilterPresetRecord
            {
                Serial = Math.Max(1, state.Belts.NextFilterPresetSerial),
                RatioL = n.RatioL,
                RatioR = n.RatioR,
                Priority = (int)n.PriorityOut,
                FilterL = n.FilterL,
                FilterR = n.FilterR,
            };
            state.Belts.NextFilterPresetSerial = rec.Serial + 1;
            var next = new BeltFilterPresetRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = rec;
            state.Belts.FilterPresets = next;
            saved = FromRecord(rec);
            LastMessage = GameText.Format("logistics.nodepanel.preset_saved", saved.Name);
            return true;
        }

        /// <summary>删除编号为 <paramref name="serial"/> 的自定义预设（已经套用过它的分流器不受影响）。</summary>
        public static bool TryDeleteCustom(CampaignState state, int serial, out string reason)
        {
            reason = null;
            BeltFilterPresetRecord[] old = state?.Belts?.FilterPresets ?? Array.Empty<BeltFilterPresetRecord>();
            var list = new List<BeltFilterPresetRecord>(old.Length);
            string name = null;
            foreach (BeltFilterPresetRecord r in old)
            {
                if (r != null && r.Serial == serial)
                {
                    name = FromRecord(r).Name;
                    continue;
                }
                if (r != null)
                {
                    list.Add(r);
                }
            }
            if (name == null)
            {
                reason = GameText.Get("logistics.nodepanel.preset_none_custom");
                return false;
            }
            state.Belts.FilterPresets = list.ToArray();
            LastMessage = GameText.Format("logistics.nodepanel.preset_deleted", name);
            return true;
        }
    }
}
