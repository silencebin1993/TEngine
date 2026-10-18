using System;
using System.Collections.Generic;
using System.Text;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>FG8-OUT-01：前哨站的供电是否连通（总览一目了然，卡片“必须同时交付”）。</summary>
    public enum OutpostPowerLink
    {
        /// <summary>没有用电的建筑。</summary>
        NoDemand = 0,
        /// <summary>用电建筑都接在家园电网上（电塔接回来的）。</summary>
        HomeGrid = 1,
        /// <summary>用电建筑都接在自己的电网上（本地发电）。</summary>
        OwnGrid = 2,
        /// <summary>一部分接家园电网、一部分接本地电网。</summary>
        Mixed = 3,
        /// <summary>有用电建筑没接上有电源的电网（没接进任何电网，或所在电网既不是家园电网、也没有发电 / 储能——例如接回家园的电塔线被拆断）。</summary>
        Disconnected = 4,
    }

    /// <summary>FG8-OUT-01：前哨站的信号覆盖是否连通。</summary>
    public enum OutpostCoverageLink
    {
        /// <summary>前哨站在家园信号网络的覆盖里（能远程指挥、接入）。</summary>
        Connected = 0,
        /// <summary>前哨站有自己的信号塔，但没接进网络（断开处在覆盖叠加层里高亮）。</summary>
        LocalCut = 1,
        /// <summary>前哨站不在任何覆盖里。</summary>
        None = 2,
    }

    /// <summary>FG8-OUT-01：前哨站总览的一行（FGU-47：建筑、供电、防御、补给、覆盖是否连通）。界面只读。</summary>
    public sealed class OutpostView
    {
        public string Id;
        public string Name;
        public bool CustomNamed;
        public Vector2 Center;
        public int Distance;
        public int Buildings;
        public int Standing;
        public int Damaged;
        public int Ruins;
        public int Ghosts;
        public int Defense;
        public int Turrets;
        public int Warehouses;
        public int Production;
        public int Generators;
        public int PowerUsers;
        public int OnHomeGrid;
        public int OnOwnGrid;
        public int Unconnected;
        public int Unpowered;
        public OutpostPowerLink Power;
        public OutpostCoverageLink Coverage;
        public int Labor;
        public int MaterialNeed;
        public string NeedResource;
        public int Inlets;
        public bool Wiped;
        public bool RaidIncoming;
        public bool Problem;
    }

    public static partial class OwnOutpostService
    {
        private static readonly Dictionary<string, int> NeedScratch = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// 总览数据：每个前哨站一行（按编号）。O(前哨站的建筑数 + 机器数 + 突袭计划数)，只在总览打开时按 outpost.overview_refresh_seconds 刷新。
        /// </summary>
        public static void CollectViews(CampaignState s, List<OutpostView> into)
        {
            into.Clear();
            if (s == null)
            {
                return;
            }
            EnsureIndex(s);
            string coreId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeCore;
            int coreSubnet = HomeValleyPowerGrid.TryGetBuildingPower(s, coreId, out BuildingPowerInfo ci) ? ci.Subnet : -1;
            foreach (OwnOutpostRecord o in SortedBySerial(s))
            {
                into.Add(BuildView(s, o, coreSubnet));
            }
        }

        public static OutpostView ViewOf(CampaignState s, string outpostId)
        {
            OwnOutpostRecord o = Find(s, outpostId);
            if (o == null)
            {
                return null;
            }
            string coreId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeCore;
            int coreSubnet = HomeValleyPowerGrid.TryGetBuildingPower(s, coreId, out BuildingPowerInfo ci) ? ci.Subnet : -1;
            return BuildView(s, o, coreSubnet);
        }

        private static OutpostView BuildView(CampaignState s, OwnOutpostRecord o, int coreSubnet)
        {
            var v = new OutpostView
            {
                Id = o.OutpostId,
                Name = NameOf(o),
                CustomNamed = !string.IsNullOrEmpty(o.CustomName),
                Center = new Vector2(o.X, o.Y),
                Distance = Mathf.RoundToInt(DistanceFromCore(s, o)),
            };
            NeedScratch.Clear();
            foreach (BuildingRecord b in Members(s, o.OutpostId))
            {
                v.Buildings++;
                bool ghost = HomeValleyController.IsPlannedGhost(b);
                bool standing = IsStanding(b);
                if (ghost)
                {
                    v.Ghosts++;
                    AddNeed(b, CampaignEconomyLedger.ResourceScrap);
                    int n = Economy.BuildMaterials.SiteCount(b);
                    for (int i = 0; i < n; i++)
                    {
                        AddNeed(b, b.ExtraMaterialIds[i]);
                    }
                    v.Inlets += Logistics.BeltPortService.SiteInletCount(b.BuildingId);
                    continue;
                }
                if (IsRuin(b))
                {
                    v.Ruins++;
                }
                else if (b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    v.Damaged++;
                }
                if (standing)
                {
                    v.Standing++;
                }
                string category = GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g) ? g.Category : string.Empty;
                if (category == "defense")
                {
                    v.Defense++;
                    if (Defense.TurretService.IsTurret(b))
                    {
                        v.Turrets++;
                    }
                }
                else if (category == "manufacturing" || category == "research")
                {
                    v.Production++;
                }
                if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && standing)
                {
                    v.Warehouses++;
                }
                if (HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId))
                {
                    v.Generators++;
                }
                if (standing && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
                {
                    v.PowerUsers++;
                    int subnet = HomeValleyPowerGrid.TryGetBuildingPower(s, b.BuildingId, out BuildingPowerInfo info) ? info.Subnet : -1;
                    // 审查修复（负向“前哨站与家园电网断开”）：接回家园的电塔线被拆断后，前哨这头还连着几座电塔、自成一个没有电源的电网——
                    // 那不是“本地电网”，与没接进任何电网同样写成断开、标为有问题（O(1) 读上一次结算的子网读数）。
                    bool sourceless = subnet >= 0 && subnet != coreSubnet && HomeValleyPowerGrid.TryGetSubnetInfo(s, subnet, out BinGames.Sim.Logistics.PowerSubnetInfo sub)
                                      && sub.Producers == 0 && sub.StorageUnits == 0;
                    if (subnet < 0 || sourceless)
                    {
                        v.Unconnected++;
                    }
                    else if (subnet == coreSubnet)
                    {
                        v.OnHomeGrid++;
                    }
                    else
                    {
                        v.OnOwnGrid++;
                    }
                    if (b.PowerState != BuildingPowerState.Powered && b.ConstructionState == BuildingConstructionState.Operational)
                    {
                        v.Unpowered++;
                    }
                }
            }
            foreach (KeyValuePair<string, int> kv in NeedScratch)
            {
                v.MaterialNeed += kv.Value;
                if (v.NeedResource == null || kv.Value > NeedScratch[v.NeedResource] || (kv.Value == NeedScratch[v.NeedResource] && string.CompareOrdinal(kv.Key, v.NeedResource) < 0))
                {
                    v.NeedResource = kv.Key;
                }
            }
            v.Power = v.PowerUsers == 0 ? OutpostPowerLink.NoDemand
                : v.Unconnected > 0 ? OutpostPowerLink.Disconnected
                : v.OnHomeGrid > 0 && v.OnOwnGrid > 0 ? OutpostPowerLink.Mixed
                : v.OnHomeGrid > 0 ? OutpostPowerLink.HomeGrid : OutpostPowerLink.OwnGrid;
            v.Coverage = CoverageOf(s, o);
            v.Labor = LaborCount(s, o.OutpostId);
            v.Wiped = v.Ruins > 0 && v.Standing == 0;
            foreach (var p in Defense.RaidDirectorService.Plans(s))
            {
                if (p != null && p.TargetKind == Defense.RaidDirectorService.TargetOutpost && p.TargetId == o.OutpostId && p.State < Defense.RaidDirectorService.StateEnded)
                {
                    v.RaidIncoming = true;
                    break;
                }
            }
            v.Problem = v.Wiped || v.Power == OutpostPowerLink.Disconnected || v.Unpowered > 0 || v.Coverage != OutpostCoverageLink.Connected || v.RaidIncoming || v.Ruins > 0;
            return v;
        }

        private static void AddNeed(BuildingRecord b, string resource)
        {
            int need = HomeValleyConstruction.SiteNeed(b, resource);
            if (need > 0)
            {
                NeedScratch[resource] = (NeedScratch.TryGetValue(resource, out int n) ? n : 0) + need;
            }
        }

        /// <summary>覆盖是否连通：群中心在网络覆盖里 = 连通；不在、但前哨站里有自己的覆盖源没接进网络 = 断开；都没有 = 无覆盖。</summary>
        public static OutpostCoverageLink CoverageOf(CampaignState s, OwnOutpostRecord o)
        {
            if (o == null)
            {
                return OutpostCoverageLink.None;
            }
            if (Signal.SignalCoverageService.Sample(HomeValleyLayout.RegionId, new Vector2(o.X, o.Y)).Covered)
            {
                return OutpostCoverageLink.Connected;
            }
            for (int i = 0; Signal.SignalCoverageService.TryGetSource(i, out Vector2 c, out float _, out Signal.SignalCoverageSourceKind _, out bool connected); i++)
            {
                if (!connected && OutpostIdAt(s, c) == o.OutpostId)
                {
                    return OutpostCoverageLink.LocalCut;
                }
            }
            return OutpostCoverageLink.None;
        }

        // ── 文字 ─────────────────────────────────────────────────────────────

        public static string PowerText(OutpostView v)
        {
            switch (v.Power)
            {
                case OutpostPowerLink.NoDemand: return GameText.Get("outpost.power.none");
                case OutpostPowerLink.HomeGrid: return GameText.Format("outpost.power.home", v.OnHomeGrid);
                case OutpostPowerLink.OwnGrid: return GameText.Format("outpost.power.own", v.OnOwnGrid, v.Generators);
                case OutpostPowerLink.Mixed: return GameText.Format("outpost.power.mixed", v.OnHomeGrid, v.OnOwnGrid);
                default: return GameText.Format("outpost.power.cut", v.Unconnected, v.PowerUsers);
            }
        }

        public static string CoverageText(OutpostCoverageLink c)
        {
            switch (c)
            {
                case OutpostCoverageLink.Connected: return GameText.Get("outpost.cover.ok");
                case OutpostCoverageLink.LocalCut: return GameText.Get("outpost.cover.cut");
                default: return GameText.Get("outpost.cover.none");
            }
        }

        /// <summary>总览一行的正文（状态除颜色外都有符号与文字：● 正常 / ⚠ 有问题 / ✕ 全毁，B15）。</summary>
        public static string RowText(OutpostView v)
        {
            var sb = new StringBuilder(160);
            string mark = v.Wiped ? GameText.Get("outpost.mark.wiped") : v.Problem ? GameText.Get("outpost.mark.problem") : GameText.Get("outpost.mark.ok");
            sb.Append(GameText.Format("outpost.row.head", mark, v.Name, v.Distance));
            sb.Append('\n').Append(GameText.Format("outpost.row.buildings", v.Buildings, v.Standing, v.Damaged, v.Ruins, v.Ghosts, v.Production, v.Generators));
            sb.Append('\n').Append(GameText.Format("outpost.row.power", PowerText(v), v.Unpowered));
            sb.Append('\n').Append(GameText.Format("outpost.row.defense", v.Defense, v.Turrets, CoverageText(v.Coverage)));
            string need = v.MaterialNeed > 0 ? GameText.Format("outpost.row.need", v.MaterialNeed, HomeValleyConstruction.MaterialName(v.NeedResource)) : GameText.Get("outpost.row.need_none");
            sb.Append('\n').Append(GameText.Format("outpost.row.supply", v.Warehouses, v.Labor, need, v.Inlets));
            if (v.Labor > 0 && v.Coverage != OutpostCoverageLink.Connected)
            {
                // FGR-SIG-053：覆盖外的机器收不到家园派工——前哨劳动要靠信号覆盖接活（写明原因与办法，B06）。
                sb.Append('\n').Append(GameText.Get("outpost.row.labor_uncovered"));
            }
            if (v.RaidIncoming)
            {
                sb.Append('\n').Append(GameText.Get("outpost.row.raid"));
            }
            if (v.Wiped)
            {
                sb.Append('\n').Append(GameText.Get("outpost.row.wiped"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// FG8-OUT-01 审查修复（B15 / B18）：总览列表一行的精简摘要。列表行高固定（虚拟化 ListView），完整 5～8 行正文放不下，
        /// 最要紧的警告会被截掉——行里只放行头、供电、防御 / 覆盖和最要紧的一条警告（全毁 &gt; 突袭 &gt; 覆盖外的前哨劳动），完整内容在详情里（<see cref="RowText"/>）。
        /// </summary>
        public static string RowBrief(OutpostView v)
        {
            var sb = new StringBuilder(120);
            string mark = v.Wiped ? GameText.Get("outpost.mark.wiped") : v.Problem ? GameText.Get("outpost.mark.problem") : GameText.Get("outpost.mark.ok");
            sb.Append(GameText.Format("outpost.row.head", mark, v.Name, v.Distance));
            sb.Append('\n').Append(GameText.Format("outpost.row.power", PowerText(v), v.Unpowered));
            sb.Append('\n').Append(GameText.Format("outpost.row.defense", v.Defense, v.Turrets, CoverageText(v.Coverage)));
            string warn = v.Wiped ? "outpost.row.wiped"
                : v.RaidIncoming ? "outpost.row.raid"
                : v.Labor > 0 && v.Coverage != OutpostCoverageLink.Connected ? "outpost.row.labor_uncovered"
                : null;
            if (warn != null)
            {
                sb.Append('\n').Append(GameText.Get(warn));
            }
            return sb.ToString();
        }

        /// <summary>总览顶部汇总：前哨站数、建筑、有问题的、统计（建立 / 全毁 / 施工包 / 传送带送进现场）。</summary>
        public static string SummaryText(CampaignState s, List<OutpostView> views)
        {
            int buildings = 0, problems = 0, labor = 0;
            foreach (OutpostView v in views)
            {
                buildings += v.Buildings;
                problems += v.Problem ? 1 : 0;
                labor += v.Labor;
            }
            OwnOutpostState d = StateOf(s);
            return GameText.Format("outpost.summary", views.Count, buildings, problems, labor, d?.Founded ?? 0, d?.WipedOut ?? 0, d?.PacksDelivered ?? 0,
                d?.PackUnitsPoured ?? 0, d?.BeltUnitsPoured ?? 0);
        }

        // ── 施工包发射（总览“投送施工包”按钮）──────────────────────────────────

        internal static void CountPackLanded(CampaignState s)
        {
            OwnOutpostState d = StateOf(s);
            if (d != null)
            {
                d.PacksDelivered++;
            }
        }

        /// <summary>
        /// 总览“投送施工包”：给这个前哨站缺得最多、仓库里有的那种材料发一批施工包——从能用的投送站里（离前哨站近的先）挑第一座能发射的，
        /// 落点在那个施工现场旁边按着陆条件找（按种子地形，B25），数量 = min(还缺, 库存, 在站的舱 × 每舱载量)。走投送站正式的发射入口（燃油、防空、静默夜、舱数都照常检查）。
        /// 成功返回 true；失败什么都不改，<paramref name="message"/> 写原因。
        /// </summary>
        public static bool TryLaunchPack(CampaignState s, string outpostId, out string message)
        {
            OwnOutpostRecord o = Find(s, outpostId);
            if (o == null)
            {
                message = GameText.Get("outpost.err.unknown");
                return false;
            }
            // 1) 缺得最多、仓库里有货（固体）的材料，和缺它最多的那个现场。
            NeedScratch.Clear();
            BuildingRecord site = null;
            string pick = null;
            int pickNeed = 0;
            bool anyNeed = false;
            foreach (BuildingRecord b in Members(s, o.OutpostId))
            {
                if (!HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                AddNeed(b, CampaignEconomyLedger.ResourceScrap);
                int n = Economy.BuildMaterials.SiteCount(b);
                for (int i = 0; i < n; i++)
                {
                    AddNeed(b, b.ExtraMaterialIds[i]);
                }
            }
            foreach (KeyValuePair<string, int> kv in NeedScratch)
            {
                anyNeed = true;
                if (!Economy.ItemCatalog.TryGetByResource(kv.Key, out Economy.ItemDef def) || def.Form != Economy.ItemForm.Solid || Economy.HomeInventory.Stock(s, def) <= 0)
                {
                    continue;
                }
                if (pick == null || kv.Value > pickNeed || (kv.Value == pickNeed && string.CompareOrdinal(kv.Key, pick) < 0))
                {
                    pick = kv.Key;
                    pickNeed = kv.Value;
                }
            }
            if (!anyNeed)
            {
                message = GameText.Format("outpost.pack.err_no_need", NameOf(o));
                return false;
            }
            if (pick == null)
            {
                message = GameText.Format("outpost.pack.err_no_stock", NameOf(o));
                return false;
            }
            int best = 0;
            foreach (BuildingRecord b in Members(s, o.OutpostId))
            {
                int need = HomeValleyController.IsPlannedGhost(b) ? HomeValleyConstruction.SiteNeed(b, pick) : 0;
                if (need > best)
                {
                    best = need;
                    site = b;
                }
            }
            Economy.ItemCatalog.TryGetByResource(pick, out Economy.ItemDef item);
            // 2) 落点：现场旁边能着陆的点（在施工包倒料半径内）。
            Vector2? landing = FindLanding(s, site.Position);
            if (!landing.HasValue)
            {
                message = GameText.Format("outpost.pack.err_no_landing", NameOf(o), Mathf.RoundToInt(PackRadius));
                return false;
            }
            // 3) 投送站：近的先，第一座预览通过的。
            var stations = new List<(DropStationRecord St, float D)>();
            foreach (DropStationRecord st in DropPodService.Stations(s))
            {
                BuildingRecord sb = HomeGridService.FindBuilding(s, st.BuildingId);
                if (sb != null)
                {
                    stations.Add((st, (sb.Position - landing.Value).sqrMagnitude));
                }
            }
            if (stations.Count == 0)
            {
                message = GameText.Get("outpost.pack.err_no_station");
                return false;
            }
            stations.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : string.CompareOrdinal(a.St.BuildingId, b.St.BuildingId));
            string firstReason = null;
            foreach ((DropStationRecord st, float _) in stations)
            {
                int ready = DropPodService.CountState(s, st.BuildingId, DropPodService.StateDocked);
                int amount = Math.Min(pickNeed, Math.Min(Economy.HomeInventory.Stock(s, item), Math.Max(1, ready) * DropPodService.CargoUnits));
                DropPreview p = DropPodService.Preview(s, st.BuildingId, landing.Value, null, item.Id, amount);
                if (!p.Ok)
                {
                    firstReason ??= p.Reason;
                    continue;
                }
                if (DropPodService.TryLaunch(s, st.BuildingId, landing.Value, null, item.Id, amount, true, out p))
                {
                    message = GameText.Format("outpost.pack.ok", NameOf(o), amount, GameText.Get(item.NameKey), DropPodService.Duration(p.LegSeconds));
                    return true;
                }
                firstReason ??= p.Reason;
            }
            message = GameText.Format("outpost.pack.err_station", firstReason ?? string.Empty);
            return false;
        }

        /// <summary>现场旁边能着陆的点：由近到远一圈圈找（2～施工包半径 − 2 格），按 <see cref="DropPodService.CheckLanding(CampaignState, float, float, out string, bool)"/>。找不到返回 null。</summary>
        public static Vector2? FindLanding(CampaignState s, Vector2 near)
        {
            float maxR = Math.Max(2f, PackRadius - 2f);
            for (float r = 4f; r <= maxR; r += 2f)
            {
                for (int a = 0; a < 16; a++)
                {
                    float ang = (a * 22.5f + r * 7f) * Mathf.Deg2Rad;
                    var p = new Vector2(Mathf.Round(near.x + Mathf.Cos(ang) * r), Mathf.Round(near.y + Mathf.Sin(ang) * r));
                    if (DropPodService.CheckLanding(s, p.x, p.y, out _) == null)
                    {
                        return p;
                    }
                }
            }
            return null;
        }
    }
}
