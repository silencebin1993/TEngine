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

        // ── FG4-ECO-05（DEBT-FG3LOG07-01）：建筑设置带上配方 / 刻录目标 ──────────────────────────────────
        // 建筑：S0 = 电力优先级，S1 = 配方（多配方生产建筑）的稳定编号，S2 = 刻录目标（固件刻录台）的稳定编号；0 = 没有这项设置 / 不改，-1 = “没选”（写到目标上 = 清掉）。
        // 稳定编号 = ID 的 FNV-1a 哈希（正数）：布局库跨存档保存，不依赖表的行顺序；读回时按当前表反查（表里没有了就当没有）。

        /// <summary>ID → 稳定编号（空 = 0；结果恒为正）。</summary>
        public static int StableId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return 0;
            }
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in id)
                {
                    h = (h ^ c) * 16777619;
                }
                int v = (int)(h & 0x7FFFFFFF);
                return v == 0 ? 1 : v;
            }
        }

        public static string RecipeIdOf(int s1)
        {
            if (s1 <= 0)
            {
                return null;
            }
            foreach (Economy.RecipeDef r in Economy.ItemCatalog.Recipes)
            {
                if (StableId(r.Id) == s1)
                {
                    return r.Id;
                }
            }
            return null;
        }

        public static string FirmwareIdOf(int s2)
        {
            if (s2 <= 0)
            {
                return null;
            }
            foreach (GameConfig.fg.FirmwareKind row in Signal.FirmwareKinds.Rows)
            {
                if (row != null && StableId(row.Id) == s2)
                {
                    return row.Id;
                }
            }
            return null;
        }

        /// <summary>一座建筑（建成的或虚影）的设置：S0 优先级（用电建筑）、S1 配方、S2 刻录目标。</summary>
        public static void BuildingSettings(CampaignState state, BuildingRecord b, out int s0, out int s1, out int s2)
        {
            s0 = s1 = s2 = 0;
            if (b == null)
            {
                return;
            }
            s0 = FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) == Family.Power ? Math.Max(1, b.PowerPriority) : 0;
            if (Defense.TurretCatalog.IsTurretType(b.BuildingTypeId))
            {
                // FG6-DEF-01（FG06 第 4 节“复制炮塔设置”）：炮塔的 S1 = 蓝图稳定编号，S2 = 目标模式 + 1。
                Defense.TurretService.SettingsOf(state, b, out s1, out s2);
                return;
            }
            if (Economy.ProductionService.TryGet(state, b.BuildingId, out Economy.ProductionService.Producer p) && Economy.ProductionService.HasCopyableSettings(p))
            {
                if (p.IsBurner)
                {
                    s2 = string.IsNullOrEmpty(p.Rec.BurnTarget) ? -1 : StableId(p.Rec.BurnTarget);
                }
                else
                {
                    s1 = p.Recipe == null ? -1 : StableId(p.Recipe.Id);
                }
            }
        }

        /// <summary>
        /// 把建筑设置写到一座建筑（建成的或虚影）上：优先级（用电建筑）、配方（目标允许这条配方时）、刻录目标（目标是刻录台时）。
        /// 配方 / 目标对这座不适用时不写（返回 false 的 <paramref name="recipeApplied"/>），优先级照写。返回有没有任何改动。
        /// </summary>
        public static bool ApplyBuildingSettings(CampaignState state, BuildingRecord b, int s0, int s1, int s2, out bool recipeApplied)
        {
            recipeApplied = false;
            bool changed = false;
            if (b == null)
            {
                return false;
            }
            if (s0 > 0 && FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) == Family.Power)
            {
                int priority = Math.Max(1, Math.Min(4, s0));
                if (b.PowerPriority != priority)
                {
                    if (Regions.HomeValleyController.IsPlannedGhost(b))
                    {
                        b.PowerPriority = priority; // 虚影还没进电网：直接写记录，建成接入时按它仲裁。
                    }
                    else
                    {
                        HomeValleyPowerGrid.TrySetPriority(state, b.BuildingId, priority);
                    }
                    changed = true;
                }
            }
            if (Defense.TurretCatalog.IsTurretType(b.BuildingTypeId))
            {
                // FG6-DEF-01：炮塔座（建成的或虚影）：补炮塔记录，写蓝图（同一种炮塔座才写）与目标模式。
                Defense.TurretService.EnsureRecord(state, b);
                if (s1 != 0 || s2 != 0)
                {
                    recipeApplied = Defense.TurretService.ApplySettings(state, b, s1, s2);
                    changed |= recipeApplied;
                }
                return changed;
            }
            if ((s1 != 0 || s2 != 0) && Economy.ProductionService.TryGet(state, b.BuildingId, out Economy.ProductionService.Producer p)
                && Economy.ProductionService.HasCopyableSettings(p))
            {
                if (p.IsBurner && s2 != 0)
                {
                    string fw = s2 < 0 ? null : FirmwareIdOf(s2);
                    if (fw != null || s2 < 0)
                    {
                        recipeApplied = true;
                        if ((p.Rec.BurnTarget ?? string.Empty) != (fw ?? string.Empty))
                        {
                            changed |= Economy.ProductionService.TrySetBurnTarget(state, b.BuildingId, fw, out _);
                        }
                    }
                }
                else if (!p.IsBurner && s1 != 0)
                {
                    if (s1 < 0)
                    {
                        recipeApplied = true;
                        if (p.Recipe != null)
                        {
                            changed |= Economy.ProductionService.TrySetRecipe(state, b.BuildingId, null, out _);
                        }
                    }
                    else
                    {
                        string rid = RecipeIdOf(s1);
                        if (rid != null && Economy.ItemCatalog.TryGetRecipe(rid, out Economy.RecipeDef r) && p.Def.AllowsRecipe(r))
                        {
                            recipeApplied = true;
                            if (!ReferenceEquals(p.Recipe, r))
                            {
                                changed |= Economy.ProductionService.TrySetRecipe(state, b.BuildingId, rid, out _);
                            }
                        }
                    }
                }
            }
            return changed;
        }

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
                {
                    string line = GameText.Format("plan.settings.power", s0 > 0 ? s0 : 1);
                    // FG6-DEF-01：炮塔设置（蓝图 + 目标模式）。
                    string turret = Defense.TurretService.DescribeSettings(CampaignSession.Current, s1, s2);
                    if (turret != null)
                    {
                        return line + (GameText.Language == GameLanguage.En ? ", " : "，") + turret;
                    }
                    string rid = RecipeIdOf(s1);
                    if (rid != null && Economy.ItemCatalog.TryGetRecipe(rid, out Economy.RecipeDef rd))
                    {
                        line += (GameText.Language == GameLanguage.En ? ", " : "，") + GameText.Format("plan.settings.recipe", rd.Name);
                    }
                    string fw = FirmwareIdOf(s2);
                    if (fw != null)
                    {
                        line += (GameText.Language == GameLanguage.En ? ", " : "，") + GameText.Format("plan.settings.burn", Signal.FirmwareKinds.DisplayName(fw) ?? fw);
                    }
                    return line;
                }
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
                BuildingSettings(state, b, out int bs0, out int bs1, out int bs2);
                clip = new Clip { Family = Family.Power, SourceName = HomeGridService.DisplayName(b.BuildingTypeId), S0 = Math.Max(1, bs0), S1 = bs1, S2 = bs2 };
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
                BuildingSettings(state, b, out int os0, out int os1, out int os2);
                before = new Clip { Family = Family.Power, SourceName = targetName, S0 = Math.Max(1, os0), S1 = os1, S2 = os2 };
                int priority = Math.Max(1, Math.Min(4, clip.S0));
                // FG4-ECO-05（DEBT-FG3LOG07-01）：配方 / 刻录目标随复制设置一起写（目标不适用就只写优先级）。
                ApplyBuildingSettings(state, b, priority, clip.S1, clip.S2, out _);
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
