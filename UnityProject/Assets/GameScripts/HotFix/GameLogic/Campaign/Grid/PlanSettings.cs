using System;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Grid
{
    /// <summary>
    /// FG3-LOG-07（FGR-LOG-005“复制和粘贴（包括建筑的设置，例如配方、过滤器、优先级）”；FGR-LOG-011 吸管 / 复制设置）：一件东西的“设置”怎么编码、
    /// 从建成的件 / 虚影里读出来、写到另一件上。编码是三个整数（<see cref="PlanEntryBlock"/> 的 S0 / S1 / S2），0 = 默认：
    /// - 建筑：S0 = 电力优先级（1～4；只对用电建筑有意义）。配方要等 FG4 的生产建筑，届时在这里加（同一个 S1 / S2 位置）。
    /// - 分流器：S0 = 左比例 | 右比例 &lt;&lt; 8 | 优先输出口 &lt;&lt; 16（1 左 / 2 右），S1 / S2 = 左 / 右口过滤（0 全部 / 65535 关闭 / 物品编号）。
    /// - 合流器：S0 = 优先输入口（1 左 / 2 右）。
    /// - 储罐：S0 = 1 | 模式 &lt;&lt; 1 | 优先级 &lt;&lt; 4；阀门：S0 = 1 | 开着 &lt;&lt; 1（最低位 1 = “有设置”，区分“关着的阀门”和“默认”）。
    /// “兼容”：建筑之间只要都用电就能复制优先级（同类或兼容类型）；物流件必须同一种（分流器 → 分流器……）。
    /// </summary>
    public static class PlanSettings
    {
        /// <summary>设置的“家族”：只有同一家族之间能复制。</summary>
        public enum Family : byte
        {
            None = 0,
            Power,
            Splitter,
            Merger,
            Tank,
            Valve,
        }

        /// <summary>一份复制下来的设置（复制设置的剪贴板；不进存档）。</summary>
        public sealed class Clip
        {
            public Family Family;
            public string SourceName;
            public int S0;
            public int S1;
            public int S2;
        }

        public static int PackSplitter(int ratioL, int ratioR, int priorityOut) =>
            Math.Max(1, Math.Min(BeltConst.RatioMax, ratioL)) | (Math.Max(1, Math.Min(BeltConst.RatioMax, ratioR)) << 8) | ((priorityOut & 3) << 16);

        public static void UnpackSplitter(int s0, out int ratioL, out int ratioR, out int priorityOut)
        {
            ratioL = Math.Max(1, Math.Min(BeltConst.RatioMax, s0 & 0xFF));
            ratioR = Math.Max(1, Math.Min(BeltConst.RatioMax, (s0 >> 8) & 0xFF));
            priorityOut = (s0 >> 16) & 3;
            if (priorityOut > 2)
            {
                priorityOut = 0;
            }
        }

        public static int PackTank(PipeTankMode mode, int priority) => 1 | (((int)mode & 7) << 1) | ((Math.Max(0, Math.Min(15, priority))) << 4);

        public static int PackValve(bool open) => 1 | (open ? 2 : 0);

        /// <summary>这件条目（按种类）读哪个家族的设置。建筑只有用电的才有设置。</summary>
        public static Family FamilyOf(PlanEntryKind kind, string typeId)
        {
            switch (kind)
            {
                case PlanEntryKind.Building:
                    return typeId != null && HomeValleyLayout.PowerProfile.ContainsKey(typeId) ? Family.Power : Family.None;
                case PlanEntryKind.Splitter: return Family.Splitter;
                case PlanEntryKind.Merger: return Family.Merger;
                case PlanEntryKind.Tank: return Family.Tank;
                case PlanEntryKind.Valve: return Family.Valve;
                default: return Family.None;
            }
        }

        /// <summary>设置的一行说明（复制 / 粘贴设置的状态行用，当前语言）。</summary>
        public static string Describe(Family f, int s0, int s1, int s2)
        {
            switch (f)
            {
                case Family.Power:
                    return GameText.Format("plan.settings.power", s0 > 0 ? s0 : 1);
                case Family.Splitter:
                    UnpackSplitter(s0, out int l, out int r, out int p);
                    return BeltNodeService.Summary(l, r, (BeltSide)p, (ushort)Math.Max(0, Math.Min(ushort.MaxValue, s1)), (ushort)Math.Max(0, Math.Min(ushort.MaxValue, s2)));
                case Family.Merger:
                    return GameText.Format("plan.settings.merger", BeltNodeService.SideName((BeltSide)Math.Max(0, Math.Min(2, s0))));
                case Family.Tank:
                    int mode = (s0 >> 1) & 7;
                    return GameText.Format("plan.settings.tank", GameText.Get(mode == 1 ? "plan.settings.tank_in" : mode == 2 ? "plan.settings.tank_out" : "plan.settings.tank_both"), (s0 >> 4) & 15);
                case Family.Valve:
                    return GameText.Get((s0 & 2) != 0 ? "plan.settings.valve_open" : "plan.settings.valve_closed");
                default:
                    return string.Empty;
            }
        }

        // ── 读：建成的件 / 虚影 ──────────────────────────────────────────────────

        /// <summary>
        /// 读 (cell) 上那一件的设置：建筑（建成的或虚影）、分流器 / 合流器 / 储罐 / 阀门（建成的或规划中的）。没有设置可读时返回 false 并给原因。
        /// </summary>
        public static bool TryRead(CampaignState state, GridCell cell, out Clip clip, out GridReason reason)
        {
            clip = null;
            reason = GridReason.Of(GridBlockReason.NoBuilding);
            if (state == null)
            {
                return false;
            }
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (b != null)
            {
                if (FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) != Family.Power)
                {
                    reason = new GridReason(GridBlockReason.NoSettings, "plan.reason.no_settings", HomeGridService.DisplayName(b.BuildingTypeId));
                    return false;
                }
                clip = new Clip { Family = Family.Power, SourceName = HomeGridService.DisplayName(b.BuildingTypeId), S0 = Math.Max(1, b.PowerPriority) };
                return true;
            }
            if (TryReadPiece(state, cell, out PlanEntryKind kind, out string id, out int s0, out int s1, out int s2))
            {
                Family f = FamilyOf(kind, null);
                if (f == Family.None)
                {
                    reason = new GridReason(GridBlockReason.NoSettings, "plan.reason.no_settings", PlanEntries.NameOf(id));
                    return false;
                }
                clip = new Clip { Family = f, SourceName = PlanEntries.NameOf(id), S0 = s0, S1 = s1, S2 = s2 };
                return true;
            }
            return false;
        }

        /// <summary>(cell) 上的物流件（建成的优先，其次规划中的虚影）：种类、工具 ID 与设置。</summary>
        public static bool TryReadPiece(CampaignState state, GridCell cell, out PlanEntryKind kind, out string id, out int s0, out int s1, out int s2)
        {
            kind = PlanEntryKind.Unknown;
            id = null;
            s0 = s1 = s2 = 0;
            if (BeltNetworkService.IsRunning && BeltNetworkService.TryGetPiece(cell, out BeltNodeKind bk, out int tier))
            {
                id = PlanEntries.ToolIdForBelt(bk, tier);
                kind = bk == BeltNodeKind.Splitter ? PlanEntryKind.Splitter : bk == BeltNodeKind.Merger ? PlanEntryKind.Merger
                    : bk == BeltNodeKind.UndergroundIn || bk == BeltNodeKind.UndergroundOut ? PlanEntryKind.Underground : PlanEntryKind.Belt;
                if ((bk == BeltNodeKind.Splitter || bk == BeltNodeKind.Merger) && BeltNetworkService.TryGetNode(cell, out BeltNodeInfo node))
                {
                    if (bk == BeltNodeKind.Splitter)
                    {
                        s0 = PackSplitter(node.RatioL, node.RatioR, (int)node.PriorityOut);
                        s1 = node.FilterL;
                        s2 = node.FilterR;
                    }
                    else
                    {
                        s0 = (int)node.PriorityIn;
                    }
                }
                return id != null;
            }
            if (PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo pi))
            {
                id = PlanEntries.ToolIdForPipe(pi.Kind, pi.Tier);
                kind = pi.Kind == PipePieceKind.Pump ? PlanEntryKind.Pump : pi.Kind == PipePieceKind.Tank ? PlanEntryKind.Tank
                    : pi.Kind == PipePieceKind.Valve ? PlanEntryKind.Valve : pi.Kind == PipePieceKind.Underground ? PlanEntryKind.PipeUnderground : PlanEntryKind.Pipe;
                if (pi.Kind == PipePieceKind.Tank)
                {
                    s0 = PackTank(pi.TankMode, pi.Priority);
                }
                else if (pi.Kind == PipePieceKind.Valve)
                {
                    s0 = PackValve(pi.ValveOpen);
                }
                return id != null;
            }
            if (HomeValleyConstruction.TryFindPlannedCell(state, cell, out PlannedBeltRecord p, out _) && !p.Upgrade)
            {
                if (p.PipePiece > 0)
                {
                    var pk = (PipePieceKind)Math.Max(0, Math.Min((int)PipePieceKind.Underground, p.PipePiece - 1));
                    id = PlanEntries.ToolIdForPipe(pk, p.Tier);
                    kind = pk == PipePieceKind.Pump ? PlanEntryKind.Pump : pk == PipePieceKind.Tank ? PlanEntryKind.Tank : pk == PipePieceKind.Valve ? PlanEntryKind.Valve : pk == PipePieceKind.Underground ? PlanEntryKind.PipeUnderground : PlanEntryKind.Pipe;
                    s0 = p.PipeSettings;
                }
                else
                {
                    var nk = (BeltNodeKind)p.NodeKind;
                    id = PlanEntries.ToolIdForBelt(nk, p.Tier);
                    kind = nk == BeltNodeKind.Splitter ? PlanEntryKind.Splitter : nk == BeltNodeKind.Merger ? PlanEntryKind.Merger
                        : nk == BeltNodeKind.UndergroundIn ? PlanEntryKind.Underground : PlanEntryKind.Belt;
                    if (nk == BeltNodeKind.Splitter)
                    {
                        s0 = PackSplitter(Math.Max(1, p.RatioL), Math.Max(1, p.RatioR), p.PriorityOut);
                        s1 = p.FilterL;
                        s2 = p.FilterR;
                    }
                    else if (nk == BeltNodeKind.Merger)
                    {
                        s0 = p.PriorityIn;
                    }
                }
                return id != null;
            }
            return false;
        }

        // ── 写 ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把 <paramref name="clip"/> 写到 (cell) 上那一件（建成的或虚影）。不兼容时返回 false 并写明原因；成功时 <paramref name="before"/> 是改之前的设置（撤销用）。
        /// </summary>
        public static bool TryApply(CampaignState state, GridCell cell, Clip clip, out Clip before, out string targetName, out GridReason reason)
        {
            before = null;
            targetName = null;
            reason = GridReason.Of(GridBlockReason.NoBuilding);
            if (state == null || clip == null)
            {
                return false;
            }
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (b != null)
            {
                targetName = HomeGridService.DisplayName(b.BuildingTypeId);
                if (clip.Family != Family.Power || FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) != Family.Power)
                {
                    reason = new GridReason(GridBlockReason.SettingsIncompatible, "plan.reason.incompatible", clip.SourceName ?? string.Empty, targetName);
                    return false;
                }
                before = new Clip { Family = Family.Power, SourceName = targetName, S0 = Math.Max(1, b.PowerPriority) };
                int priority = Math.Max(1, Math.Min(4, clip.S0));
                HomeValleyPowerGrid.TrySetPriority(state, b.BuildingId, priority);
                return b.PowerPriority == priority;
            }
            if (!TryReadPiece(state, cell, out PlanEntryKind kind, out string id, out int s0, out int s1, out int s2))
            {
                return false;
            }
            targetName = PlanEntries.NameOf(id);
            Family f = FamilyOf(kind, null);
            if (f == Family.None || f != clip.Family)
            {
                reason = new GridReason(GridBlockReason.SettingsIncompatible, "plan.reason.incompatible", clip.SourceName ?? string.Empty, targetName);
                return false;
            }
            before = new Clip { Family = f, SourceName = targetName, S0 = s0, S1 = s1, S2 = s2 };
            return ApplyToPiece(state, cell, kind, clip.S0, clip.S1, clip.S2);
        }

        /// <summary>把设置写到 (cell) 上的物流件：建成的走正式入口（分流器 / 合流器 / 储罐 / 阀门的设置接口），规划中的虚影写进规划（建成时生效）。</summary>
        public static bool ApplyToPiece(CampaignState state, GridCell cell, PlanEntryKind kind, int s0, int s1, int s2)
        {
            if (BeltNetworkService.IsRunning && BeltNetworkService.TryGetPiece(cell, out BeltNodeKind bk, out _))
            {
                if (bk == BeltNodeKind.Splitter && kind == PlanEntryKind.Splitter)
                {
                    UnpackSplitter(s0, out int l, out int r, out int p);
                    return BeltNetworkService.TrySetSplitter(state, cell, l, r, (BeltSide)p, (ushort)Math.Max(0, Math.Min(ushort.MaxValue, s1)),
                        (ushort)Math.Max(0, Math.Min(ushort.MaxValue, s2))).Ok;
                }
                if (bk == BeltNodeKind.Merger && kind == PlanEntryKind.Merger)
                {
                    return BeltNetworkService.TrySetMergerPriority(state, cell, (BeltSide)Math.Max(0, Math.Min(2, s0))).Ok;
                }
                return false;
            }
            if (PipeNetworkService.IsRunning && PipeNetworkService.TryGetPiece(cell, out PipePieceKind pk, out _))
            {
                if (pk == PipePieceKind.Tank && kind == PlanEntryKind.Tank && (s0 & 1) != 0)
                {
                    bool m = PipeNetworkService.TrySetTankMode(state, cell, (PipeTankMode)Math.Min(2, (s0 >> 1) & 7)).Ok;
                    int prio = (s0 >> 4) & 15;
                    return m && (prio == 0 || PipeNetworkService.TrySetTankPriority(state, cell, prio).Ok);
                }
                if (pk == PipePieceKind.Valve && kind == PlanEntryKind.Valve && (s0 & 1) != 0)
                {
                    return PipeNetworkService.TrySetValveOpen(state, cell, (s0 & 2) != 0).Ok;
                }
                return false;
            }
            if (HomeValleyConstruction.TryFindPlannedCell(state, cell, out PlannedBeltRecord plan, out _) && !plan.Upgrade)
            {
                return ApplyToPlan(plan, kind, s0, s1, s2);
            }
            return false;
        }

        /// <summary>把设置写进一份规划（虚影建成时按它生效，见 HomeValleyConstruction.ApplyNodeSettings / ApplyPipeSettings）。</summary>
        public static bool ApplyToPlan(PlannedBeltRecord plan, PlanEntryKind kind, int s0, int s1, int s2)
        {
            if (plan == null)
            {
                return false;
            }
            switch (kind)
            {
                case PlanEntryKind.Splitter when plan.NodeKind == (int)BeltNodeKind.Splitter && plan.PipePiece == 0:
                    if (s0 == 0)
                    {
                        return true;
                    }
                    UnpackSplitter(s0, out int l, out int r, out int p);
                    plan.RatioL = l;
                    plan.RatioR = r;
                    plan.PriorityOut = p;
                    plan.FilterL = s1;
                    plan.FilterR = s2;
                    return true;
                case PlanEntryKind.Merger when plan.NodeKind == (int)BeltNodeKind.Merger && plan.PipePiece == 0:
                    plan.PriorityIn = Math.Max(0, Math.Min(2, s0));
                    return true;
                case PlanEntryKind.Tank when plan.PipePiece == (int)PipePieceKind.Tank + 1:
                case PlanEntryKind.Valve when plan.PipePiece == (int)PipePieceKind.Valve + 1:
                    plan.PipeSettings = s0;
                    return true;
                default:
                    return false;
            }
        }
    }
}
