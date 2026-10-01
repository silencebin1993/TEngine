using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Grid
{
    /// <summary>FG3-LOG-07：规划条目是什么（由条目 ID 查建造菜单得到；认不出来的 = Unknown，粘贴时标“未知的建筑”）。</summary>
    public enum PlanEntryKind : byte
    {
        Unknown = 0,
        Building,
        Belt,
        Splitter,
        Merger,
        Underground,
        Pipe,
        Pump,
        Tank,
        Valve,
        /// <summary>FG4-ECO-04（FG-GAP-082）：地下管线口（单击放置，旋转定朝地下的方向；与同一直线上跨度以内朝回来的另一口配对）。</summary>
        PipeUnderground,
    }

    /// <summary>
    /// FG3-LOG-07：<see cref="PlanEntryBlock"/> 的读写辅助——追加一件、按条目 ID 认种类、找工具 ID、旋转、复制。
    /// 只是数据结构操作，不碰战役状态。
    /// </summary>
    public static class PlanEntries
    {
        /// <summary>空块（新数组）。</summary>
        public static PlanEntryBlock New(int capacity = 0)
        {
            return new PlanEntryBlock
            {
                Ids = new string[capacity],
                Xs = new int[capacity],
                Ys = new int[capacity],
                Rots = new int[capacity],
                X2s = new int[capacity],
                Y2s = new int[capacity],
                S0 = new int[capacity],
                S1 = new int[capacity],
                S2 = new int[capacity],
            };
        }

        /// <summary>列表形式的构造器（追加完再 <see cref="Build"/> 成块，避免反复扩容数组）。</summary>
        public sealed class Builder
        {
            public readonly List<string> Ids = new List<string>();
            public readonly List<int> Xs = new List<int>();
            public readonly List<int> Ys = new List<int>();
            public readonly List<int> Rots = new List<int>();
            public readonly List<int> X2s = new List<int>();
            public readonly List<int> Y2s = new List<int>();
            public readonly List<int> S0 = new List<int>();
            public readonly List<int> S1 = new List<int>();
            public readonly List<int> S2 = new List<int>();

            public int Count => Ids.Count;

            public void Add(string id, int x, int y, int rot, int x2 = 0, int y2 = 0, int s0 = 0, int s1 = 0, int s2 = 0)
            {
                Ids.Add(id);
                Xs.Add(x);
                Ys.Add(y);
                Rots.Add(rot);
                X2s.Add(x2);
                Y2s.Add(y2);
                S0.Add(s0);
                S1.Add(s1);
                S2.Add(s2);
            }

            public void AddFrom(PlanEntryBlock b, int i) => Add(b.Ids[i], b.Xs[i], b.Ys[i], b.Rots[i], b.X2s[i], b.Y2s[i], b.S0[i], b.S1[i], b.S2[i]);

            public void Clear()
            {
                Ids.Clear();
                Xs.Clear();
                Ys.Clear();
                Rots.Clear();
                X2s.Clear();
                Y2s.Clear();
                S0.Clear();
                S1.Clear();
                S2.Clear();
            }

            public PlanEntryBlock Build() => new PlanEntryBlock
            {
                Ids = Ids.ToArray(),
                Xs = Xs.ToArray(),
                Ys = Ys.ToArray(),
                Rots = Rots.ToArray(),
                X2s = X2s.ToArray(),
                Y2s = Y2s.ToArray(),
                S0 = S0.ToArray(),
                S1 = S1.ToArray(),
                S2 = S2.ToArray(),
            };
        }

        /// <summary>块是否完整（各列等长）。存档 / 布局文件被手改坏了时按空块处理，不越界。</summary>
        public static bool IsValid(PlanEntryBlock b)
        {
            if (b?.Ids == null)
            {
                return false;
            }
            int n = b.Ids.Length;
            return b.Xs?.Length == n && b.Ys?.Length == n && b.Rots?.Length == n && b.X2s?.Length == n && b.Y2s?.Length == n
                   && b.S0?.Length == n && b.S1?.Length == n && b.S2?.Length == n;
        }

        public static int CountOf(PlanEntryBlock b) => IsValid(b) ? b.Ids.Length : 0;

        /// <summary>条目 ID → 种类（建造菜单工具按 kind；建筑按格网表）。<paramref name="tool"/> 为工具行（建筑时为 null）。</summary>
        public static PlanEntryKind KindOf(string id, out BuildTool tool)
        {
            tool = null;
            if (string.IsNullOrEmpty(id))
            {
                return PlanEntryKind.Unknown;
            }
            if (GridContent.TryGetTool(id, out tool))
            {
                switch (tool.Kind)
                {
                    case "belt": return PlanEntryKind.Belt;
                    case "splitter": return PlanEntryKind.Splitter;
                    case "merger": return PlanEntryKind.Merger;
                    case "underground": return PlanEntryKind.Underground;
                    case "pipe": return PlanEntryKind.Pipe;
                    case "pump": return PlanEntryKind.Pump;
                    case "tank": return PlanEntryKind.Tank;
                    case "valve": return PlanEntryKind.Valve;
                    case "pipe_underground": return PlanEntryKind.PipeUnderground;
                    default:
                        tool = null;
                        return PlanEntryKind.Unknown;
                }
            }
            return GridContent.TryGetBuilding(id, out _) ? PlanEntryKind.Building : PlanEntryKind.Unknown;
        }

        public static bool IsPipeLayer(PlanEntryKind k) => k == PlanEntryKind.Pipe || k == PlanEntryKind.Pump || k == PlanEntryKind.Tank || k == PlanEntryKind.Valve
                                                           || k == PlanEntryKind.PipeUnderground;

        public static bool IsBeltLayer(PlanEntryKind k) => k == PlanEntryKind.Belt || k == PlanEntryKind.Splitter || k == PlanEntryKind.Merger || k == PlanEntryKind.Underground;

        public static PipePieceKind PipeKindOf(PlanEntryKind k) =>
            k == PlanEntryKind.Pump ? PipePieceKind.Pump : k == PlanEntryKind.Tank ? PipePieceKind.Tank : k == PlanEntryKind.Valve ? PipePieceKind.Valve
            : k == PlanEntryKind.PipeUnderground ? PipePieceKind.Underground : PipePieceKind.Pipe;

        public static BeltNodeKind NodeKindOf(PlanEntryKind k) =>
            k == PlanEntryKind.Splitter ? BeltNodeKind.Splitter : k == PlanEntryKind.Merger ? BeltNodeKind.Merger
            : k == PlanEntryKind.Underground ? BeltNodeKind.UndergroundIn : BeltNodeKind.Belt;

        /// <summary>建造菜单里（kind, 等级）对应的工具 ID：传送带 / 地下传送带 / 管线按等级，其余只看 kind。找不到返回 null。</summary>
        public static string ToolIdFor(string kind, int tier)
        {
            bool byTier = kind == "belt" || kind == "underground" || kind == "pipe" || kind == "pipe_underground";
            foreach (BuildTool t in GridContent.Tools)
            {
                if (t.Kind == kind && (!byTier || t.Tier == tier))
                {
                    return t.Id;
                }
            }
            return null;
        }

        /// <summary>内核里的物流件 → 工具 ID。</summary>
        public static string ToolIdForBelt(BeltNodeKind kind, int tier)
        {
            switch (kind)
            {
                case BeltNodeKind.Splitter: return ToolIdFor("splitter", tier);
                case BeltNodeKind.Merger: return ToolIdFor("merger", tier);
                case BeltNodeKind.UndergroundIn:
                case BeltNodeKind.UndergroundOut: return ToolIdFor("underground", tier);
                default: return ToolIdFor("belt", tier);
            }
        }

        public static string ToolIdForPipe(PipePieceKind kind, int tier)
        {
            switch (kind)
            {
                case PipePieceKind.Pump: return ToolIdFor("pump", 0);
                case PipePieceKind.Tank: return ToolIdFor("tank", 0);
                case PipePieceKind.Valve: return ToolIdFor("valve", 0);
                case PipePieceKind.Underground: return ToolIdFor("pipe_underground", tier);
                default: return ToolIdFor("pipe", tier);
            }
        }

        /// <summary>条目的显示名（当前语言；认不出来时写 ID 本身）。</summary>
        public static string NameOf(string id)
        {
            if (GridContent.TryGetTool(id, out BuildTool t))
            {
                return GameText.Get(t.NameKey);
            }
            return GridContent.TryGetBuilding(id, out _) ? HomeGridService.DisplayName(id) : id ?? string.Empty;
        }

        /// <summary>这件条目的造价（建筑按建造表；物流件按工具每格 / 每座 / 地下两端）。认不出来为 0。</summary>
        public static int CostOf(string id)
        {
            PlanEntryKind k = KindOf(id, out BuildTool tool);
            if (k == PlanEntryKind.Building)
            {
                return HomeValleyLayout.BuildProfile.TryGetValue(id, out (int ScrapCost, float Seconds) p) ? p.ScrapCost : 0;
            }
            if (tool == null)
            {
                return 0;
            }
            return k == PlanEntryKind.Underground ? tool.ScrapPerCell * 2 : tool.ScrapPerCell;
        }

        /// <summary>
        /// 把相对坐标 (x, y)（相对布局的中心格）顺时针转 <paramref name="quarter"/> 个 90°，再平移到 <paramref name="anchor"/>。
        /// 与格网旋转同一约定（(x, y) → (y, -x)，北 → 东），所以建筑朝向 + 90° × quarter 后占地正好是整体旋转后的格子。
        /// </summary>
        public static GridCell Place(int x, int y, GridCell anchor, int quarter)
        {
            var off = GridMath.RotateOffset(x, y, (quarter & 3) * 90);
            return new GridCell(anchor.X + off.x, anchor.Y + off.y);
        }

        /// <summary>布局的中心格（相对坐标里的整数中点；粘贴时这一格落在光标下）。</summary>
        public static void Bounds(PlanEntryBlock b, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = minY = int.MaxValue;
            maxX = maxY = int.MinValue;
            int n = CountOf(b);
            for (int i = 0; i < n; i++)
            {
                Extend(b.Xs[i], b.Ys[i], ref minX, ref minY, ref maxX, ref maxY);
                if (KindOf(b.Ids[i], out _) == PlanEntryKind.Underground)
                {
                    Extend(b.X2s[i], b.Y2s[i], ref minX, ref minY, ref maxX, ref maxY);
                }
                else if (GridContent.TryGetBuilding(b.Ids[i], out BuildingGrid g))
                {
                    GridMath.FootprintBounds(new GridCell(b.Xs[i], b.Ys[i]), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rots[i]),
                        out GridCell mn, out GridCell mx);
                    Extend(mn.X, mn.Y, ref minX, ref minY, ref maxX, ref maxY);
                    Extend(mx.X, mx.Y, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
            if (n == 0)
            {
                minX = minY = maxX = maxY = 0;
            }
        }

        private static void Extend(int x, int y, ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        /// <summary>布局里有几件本局还没解锁（或本版本不认识）的条目。</summary>
        public static int LockedCount(CampaignState state, PlanEntryBlock b, out int unknown)
        {
            unknown = 0;
            int locked = 0;
            int n = CountOf(b);
            for (int i = 0; i < n; i++)
            {
                PlanEntryKind k = KindOf(b.Ids[i], out BuildTool tool);
                if (k == PlanEntryKind.Unknown)
                {
                    unknown++;
                    continue;
                }
                string rule = tool != null ? tool.UnlockRule : GridContent.TryGetBuilding(b.Ids[i], out BuildingGrid g) ? g.UnlockRule : "always";
                if (!BuildCatalog.IsUnlocked(state, rule))
                {
                    locked++;
                }
            }
            return locked;
        }

        public static PlanEntryBlock Clone(PlanEntryBlock b)
        {
            if (!IsValid(b))
            {
                return New();
            }
            return new PlanEntryBlock
            {
                Ids = (string[])b.Ids.Clone(),
                Xs = (int[])b.Xs.Clone(),
                Ys = (int[])b.Ys.Clone(),
                Rots = (int[])b.Rots.Clone(),
                X2s = (int[])b.X2s.Clone(),
                Y2s = (int[])b.Y2s.Clone(),
                S0 = (int[])b.S0.Clone(),
                S1 = (int[])b.S1.Clone(),
                S2 = (int[])b.S2.Clone(),
            };
        }
    }
}
