using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.View;
using Luban;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools
{
    public static partial class FgLogisticsGateSelfCheck
    {
        // ── W 统计窗口进存档（DEBT-FG0ARCH02-10，内核格式 4）──────────────────────────────

        private static void CheckFormatAndStatsWindows()
        {
            BeltConfig cfg = BeltNetworkService.ReadConfig();
            using var a = new BeltKernel(cfg);
            FgBeltNodeSelfCheck.BuildHugeWithNodes(a, 0, 0);
            a.StepMany(cfg.BucketSteps * 2 + cfg.BucketSteps / 3); // 走满两桶多一点：统计桶轮转过、本桶累计到一半
            BeltSnapshot snap4 = a.Serialize();
            BeltSnapshot snap3 = a.Serialize(3);
            ulong h = a.ComputeStateHash();
            using var b = new BeltKernel(cfg);
            bool ok = b.Deserialize(snap4, out string err);
            bool hashAtLoad = b.ComputeStateHash() == h;
            string ra = Readings(a);
            string rb = Readings(b);
            a.StepMany(cfg.BucketSteps + 17);
            b.StepMany(cfg.BucketSteps + 17);
            string ra2 = Readings(a);
            string rb2 = Readings(b);
            string why = $"（读档 {ok}/{err ?? "无问题"}，哈希 {hashAtLoad}，格式 {snap4.FormatVersion}，桶 {snap4.CompletedBuckets}，读档瞬间读数一致 {ra == rb}，续跑读数一致 {ra2 == rb2}，" +
                         $"读数串长 {ra.Length}，续跑哈希一致 {a.ComputeStateHash() == b.ComputeStateHash()}，统计尾段丢弃 {b.StatsDroppedOnLoad}）";
            if (ra2 != rb2)
            {
                int at = 0;
                while (at < Math.Min(ra2.Length, rb2.Length) && ra2[at] == rb2[at])
                {
                    at++;
                }
                why += $" 续跑后第一处不同 @{at}：{ra2.Substring(Math.Max(0, at - 60), Math.Min(120, ra2.Length - Math.Max(0, at - 60)))} ≠ {rb2.Substring(Math.Max(0, at - 60), Math.Min(120, rb2.Length - Math.Max(0, at - 60)))}";
            }
            Expect(ok && err == null && hashAtLoad && snap4.FormatVersion == 4 && snap4.CompletedBuckets >= 2 && ra == rb && ra2 == rb2 && ra.Length > 10000
                   && a.ComputeStateHash() == b.ComputeStateHash() && b.StatsDroppedOnLoad == 0,
                $"W 统计窗口进存档（DEBT-FG0ARCH02-10）：15,045 格含 340 个节点的传送带走满 {snap4.CompletedBuckets} 桶后用格式 4 存读档，每格“实测通过”、每个网络“实测收下 / 推上”、" +
                $"每个端口“实测件数”、每个分流器“实测左右口”在读档瞬间与存档前逐位一致；两边再跑 {cfg.BucketSteps + 17} 步（跨一次桶轮转）读数与状态哈希仍一致" +
                why);

            // 旧格式 3：照常读，状态哈希一致，统计窗口从零累计（格式 4 之前的行为）。
            using var c = new BeltKernel(cfg);
            bool ok3 = c.Deserialize(snap3, out string err3);
            c.TryGetNetworkStats(0, out BeltNetworkStats n3);
            Expect(ok3 && err3 == null && c.ComputeStateHash() == h && n3.WindowSeconds == 0f && snap3.CompletedBuckets == 0,
                "W 旧存档（传送带格式 3）照常读：物品与状态逐位一致，统计窗口从零重新累计（与改之前一样）");

            // 统计尾段坏了（校验和仍对：模拟写入端的缺陷）：这一块的窗口清零并计数，物品与状态照常恢复，不丢网络。
            BeltSnapshot bad = a.Serialize();
            BeltSnapshot bad3 = a.Serialize(3);
            byte[] chunk = bad.Networks[0].Bytes;
            int trailer = bad3.Networks[0].Bytes.Length - 4; // 格式 4 的块 = 格式 3 的正文 + 统计尾段 + 校验和
            chunk[trailer] = (byte)'X';
            RewriteFnv(chunk);
            using var d = new BeltKernel(cfg);
            bool okBad = d.Deserialize(bad, out string errBad);
            Expect(okBad && errBad == null && d.StatsDroppedOnLoad == 1 && d.CorruptChunksDropped == 0 && d.ComputeStateHash() == a.ComputeStateHash() && d.CellCount == a.CellCount,
                $"W 统计尾段读不了（{d.StatsDroppedOnLoad} 段）：只把这一段的窗口从零累计，网络与物品照常恢复（丢弃 {d.CorruptChunksDropped} 块、状态哈希一致）");

            // 体积：格式 4 比格式 3 多出的字节（统计窗口的代价，FGR-SYS-005 体积预算里算进去）。
            double kb4 = snap4.TotalBytes / 1024.0;
            double kb3 = snap3.TotalBytes / 1024.0;
            PerfLines.Add($"传送带存档格式 4（含统计窗口）15,045 格 / {a.ItemCount:N0} 件：{kb4:F0} KB（格式 3 为 {kb3:F0} KB，多 {kb4 - kb3:F0} KB，每格 {(snap4.TotalBytes - snap3.TotalBytes) / (double)a.CellCount:F1} 字节，base64 后再 × 4/3）");
            Expect(snap4.TotalBytes - snap3.TotalBytes <= a.CellCount * 12L,
                $"W 统计窗口的体积代价：每格多 {(snap4.TotalBytes - snap3.TotalBytes) / (double)a.CellCount:F1} 字节（变长编码；上限 12 字节 / 格）");
        }

        /// <summary>传送带内核的全部读数（网络 / 端口 / 分流器 / 每格）拼成一个串；按坐标排序，与内部下标无关。</summary>
        private static string Readings(BeltKernel k)
        {
            var sb = new System.Text.StringBuilder(64 * 1024);
            var anchors = new List<int3>();
            k.CollectNetworkAnchors(anchors);
            foreach (int3 an in anchors.OrderBy(x => x.x).ThenBy(x => x.y))
            {
                k.TryGetNetworkStats(an.z, out BeltNetworkStats n);
                sb.Append('N').Append(an.x).Append(',').Append(an.y).Append('=').Append(n.DeliveredInWindow).Append('/').Append(n.EmittedInWindow).Append('/').Append(n.WindowSeconds).Append(';');
            }
            var ports = new List<int>();
            k.CollectPortIds(ports);
            foreach (int id in ports.OrderBy(x => x))
            {
                k.TryGetPortInfo(id, out BeltPortInfo p);
                sb.Append('P').Append(id).Append('=').Append(p.InWindow).Append('/').Append(p.WindowSeconds).Append(';');
            }
            var cells = new List<int3>();
            k.CollectCells(cells);
            foreach (int3 c in cells)
            {
                k.TryGetCellInfo(c.x, c.y, out BeltCellInfo ci);
                sb.Append(ci.PassedInWindow).Append(',');
                if (((c.z >> 16) & 0xFF) == (int)BeltNodeKind.Splitter && k.TryGetNodeInfo(c.x, c.y, out BeltNodeInfo ni))
                {
                    sb.Append('S').Append(ni.SentLInWindow).Append('/').Append(ni.SentRInWindow).Append('/').Append(ni.WindowSeconds).Append(';');
                }
            }
            return sb.ToString();
        }

        private static void RewriteFnv(byte[] b)
        {
            uint hsh = 2166136261u;
            for (int i = 0; i < b.Length - 4; i++)
            {
                hsh ^= b[i];
                hsh *= 16777619u;
            }
            Array.Copy(BitConverter.GetBytes(hsh), 0, b, b.Length - 4, 4);
        }

        // ── R 已移除的建筑类型（DEBT-FG0SAVE01-07；FGR-SYS-004 / FGT-SYS-002 建筑类）──────────────────

        private static TbRemovedContent RemovedTable(params (string id, string kind, string nameKey, int refund, int ver)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString(r.id);
                buf.WriteString(r.kind);
                buf.WriteString(r.nameKey);
                buf.WriteInt(r.refund);
                buf.WriteInt(r.ver);
            }
            return new TbRemovedContent(buf);
        }

        private static long ScrapTotal(CampaignState s)
        {
            long ground = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => (long)g.Amount);
            long cargo = MachineRegistry.AllRecords.Where(m => m != null).Sum(m => (long)HomeValleyConstruction.CargoScrap(m));
            long sites = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Sum(b => (long)b.ConstructionDelivered);
            return s.Scrap + ground + cargo + sites;
        }

        private static void CheckRemovedBuildingTypes()
        {
            const string removed = "generator_2";
            CampaignState s = NewWorld(90951, observe: true, scrap: 100);
            GridCell core = HomeGridService.CorePivot(s);
            SpawnRegistered(s, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, new Vector2(core.X - 6, core.Y - 6), 100f);
            WorldSimulation.StepMany(2);
            GridCell? spot1 = FindValid(s, removed, new GridCell(core.X - 10, core.Y + 30), 20);
            BuildingRecord b1 = spot1.HasValue ? FgPowerGridSelfCheck.AddBuilt(s, removed, "rm_b1", spot1.Value) : null;
            if (b1 == null)
            {
                Fail("R 放不下已建成的测试建筑");
                return;
            }
            b1.InvestedScrap = 60;
            b1.Inventory = new[] { new CargoEntry { ResourceType = CampaignEconomyLedger.ResourceScrap, Amount = 7 } };
            GridCell? spot2 = FindValid(s, removed, new GridCell(core.X + 10, core.Y + 30), 20);
            GridOpResult g1 = spot2.HasValue ? HomeGridService.TryPlace(s, removed, spot2.Value, 0) : default;
            if (!spot2.HasValue || !g1.Success || g1.BuildingId == null)
            {
                Fail("R 放不下测试虚影");
                return;
            }
            // 施工中途：搬运机取了料正在路上，或已送到现场一部分。
            bool mid = false;
            for (int i = 0; i < 60 * 120 && !mid; i += 15)
            {
                WorldSimulation.StepMany(15);
                BuildingRecord ghost = HomeGridService.FindBuilding(s, g1.BuildingId);
                mid = ghost != null && ghost.ConstructionDelivered > 0 && MachineRegistry.AllRecords.Any(m => m != null && HomeValleyConstruction.CargoScrap(m) > 0)
                      || ghost != null && ghost.ConstructionDelivered > 0 && ghost.ConstructionDelivered < ghost.ConstructionRequired;
            }
            BuildingRecord gRec = HomeGridService.FindBuilding(s, g1.BuildingId);
            int delivered = gRec?.ConstructionDelivered ?? 0;
            int cargo = MachineRegistry.AllRecords.Where(m => m != null).Sum(HomeValleyConstruction.CargoScrap);
            string orderId = HomeValleyWorkOrders.FindActiveBuild(s, g1.BuildingId)?.WorkOrderId;
            long totalBefore = ScrapTotal(s) + b1.InvestedScrap + 7;
            int expectedRefund = 60 + 7 + delivered + cargo;
            SaveTo(Slot);
            WorldSimulation.UnloadAll();
            // 读档后进家园会自动存档（进入家园）覆盖所在槽位：每次都从原始存档的一份拷贝读。
            RestoreResult RestorePristine()
            {
                File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
                string bakPath = CampaignSaveService.BakPath(RunSlot);
                if (File.Exists(bakPath))
                {
                    File.Delete(bakPath);
                }
                return CampaignRestoreOrchestrator.Restore(RunSlot);
            }

            string nameKey = FgContentTables.TryGetBuilding(removed, out Building removedRow) && GameText.Has(removedRow.NameKey) ? removedRow.NameKey : "building.generator.name";
            SaveContentReconciler.OverrideForTests(RemovedTable((removed, SaveContentReconciler.KindBuilding, nameKey, 25, 2)), isLiveBuilding: t => t != removed);
            try
            {
                RestoreResult rr = RestorePristine();
                CampaignState st = rr.State;
                SaveNoticeRecord notice = rr.Notices?.FirstOrDefault(n => n.TextKey == "save.notice.building_removed");
                bool gone = st != null && st.BuildingRecords.All(b => b.BuildingTypeId != removed);
                WorkOrderRecord order = st?.WorkOrders.FirstOrDefault(o => o.WorkOrderId == orderId);
                bool cargoBack = MachineRegistry.AllRecords.All(m => m == null || HomeValleyConstruction.CargoScrap(m) == 0);
                long totalAfter = st == null ? -1 : ScrapTotal(st);
                Expect(rr.Success && gone && notice != null && notice.Args[1] == "2" && notice.Args[2] == expectedRefund.ToString(CultureInfo.InvariantCulture)
                       && order != null && order.State == WorkOrderState.Cancelled && cargoBack && totalAfter == totalBefore
                       && st.SaveHistory.Notices.Any(n => n.TextKey == "save.notice.building_removed"),
                    $"R 游戏更新移除了建筑类型“{removed}”后读档（正式恢复入口，机器名册载入后执行）：已建成 1 座（投入 60 + 内部缓存 7）与施工中的虚影 1 座（已到现场 {delivered}、搬运机货舱 {cargo}）" +
                    $"全部拆掉并返还 {notice?.Args[2]} 废料（仓库放不下的留在地面）；施工单取消（{order?.State}）、货舱清空；废料总账 {totalBefore} → {totalAfter}（不多不少）；通知写入存档历史");
                string zh = SaveContentReconciler.Render(notice);
                GameSettings.SetLanguage(GameLanguage.En);
                string en = SaveContentReconciler.Render(notice);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(zh.Contains("×2") && zh.Contains(expectedRefund + " 废料") && en.Contains(expectedRefund + " scrap") && !GameText.ContainsMarker(zh + en),
                    $"R 通知文字：“{zh}”／“{en}”");
                // 读档后世界照常载入、电网 / 端口对账、跑 30 游戏秒不出错。
                CampaignSession.Set(RunSlot, st);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                WorldSimulation.StepMany(60 * 30);
                Expect(home.IsLoaded && HomeGridService.MapFor(st) != null && st.BuildingRecords.All(b => b.BuildingTypeId != removed),
                    "R 转换后的存档照常载入家园（格网占用、电网、端口按剩下的建筑重算），跑 30 游戏秒无异常");
                // 同一份文件再读一次：结果相同（转换由文件内容决定，不依赖上一次读档）。
                WorldSimulation.UnloadAll();
                RestoreResult again = RestorePristine();
                Expect(again.Success && again.State.BuildingRecords.All(b => b.BuildingTypeId != removed) && ScrapTotal(again.State) == totalBefore
                       && again.Notices.Count(n => n.TextKey == "save.notice.building_removed") == 1,
                    "R 同一份存档再读一次：同样转换、同样返还、同样一条通知（不重复发放）");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
            }

            // 表里没登记（开发删了建筑类型忘了登记）：照样按实际投入转换，通知里名称是“未知内容”（并记 Error）。
            SaveContentReconciler.OverrideForTests(RemovedTable(), isLiveBuilding: t => t != removed);
            try
            {
                RestoreResult rr = RestorePristine();
                SaveNoticeRecord notice = rr.Notices?.FirstOrDefault(n => n.TextKey == "save.notice.building_removed");
                Expect(rr.Success && notice != null && notice.Args[0] == "save.notice.unknown_content" && notice.Args[2] == expectedRefund.ToString(CultureInfo.InvariantCulture)
                       && rr.State.BuildingRecords.All(b => b.BuildingTypeId != removed),
                    $"R 表里没登记的已移除建筑：照样拆掉、按实际投入返还 {notice?.Args[2]}，通知名称为“未知内容”");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
            }

            // 已移除内容表读不出来：什么都不转换（不能把玩家的建筑换成 0）。
            SaveContentReconciler.OverrideForTests(null, isLiveBuilding: t => t != removed);
            try
            {
                RestoreResult rr = RestorePristine();
                Expect(rr.Success && rr.State.BuildingRecords.Count(b => b.BuildingTypeId == removed) == 2 && (rr.Notices?.All(n => n.TextKey != "save.notice.building_removed") ?? true),
                    "R 已移除内容表不可用：建筑原样保留、不发通知（与基元芯片同一失败策略）");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
            }

            // 正常情况（真表、真格网建筑表）：新战役没有任何建筑被误判为已移除。
            CampaignState fresh = NewWorld(90952, observe: false, scrap: 10);
            SaveTo(Slot);
            WorldSimulation.UnloadAll();
            RestoreResult normal = CampaignRestoreOrchestrator.Restore(Slot);
            Expect(normal.Success && (normal.Notices?.All(n => n.TextKey != "save.notice.building_removed") ?? true)
                   && normal.State.BuildingRecords.Length == fresh.BuildingRecords.Length,
                $"R 真表：新战役 {fresh.BuildingRecords.Length} 座开局建筑没有一座被误判为已移除");
        }

        // ── V 画面对账变化驱动（DEBT-FG0ARCH04-09）──────────────────────────────────

        private static readonly string[] FixtureTypes = { "power_pole", HomeValleyLayout.BuildingTypeGenerator2, HomeValleyLayout.BuildingTypeRepairBay, HomeValleyLayout.BuildingTypeAnalysisBench };

        /// <summary>核心南面按格铺已建成的夹具建筑到 <paramref name="target"/> 座（与性能基线同一做法：测“有 N 座建筑时”的开销）。返回新增的记录。</summary>
        private static List<BuildingRecord> AddFixtures(CampaignState s, int target, string prefix)
        {
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            var added = new List<BuildingRecord>(target);
            var list = new List<BuildingRecord>(s.BuildingRecords);
            var cells = new List<GridCell>(16);
            int serial = 0;
            for (int row = 0; row < 200 && list.Count < target; row++)
            {
                for (int col = 0; col < 60 && list.Count < target; col++)
                {
                    string type = FixtureTypes[serial % FixtureTypes.Length];
                    BuildingGrid g = GridContent.Building(type);
                    var pivot = new GridCell(core.X - 120 + col * 4, core.Y - 50 - row * 4);
                    cells.Clear();
                    GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, 0, cells);
                    if (cells.Any(c => map.OccupantAt(c) != null))
                    {
                        continue;
                    }
                    BuildingRecord r = Fixture(type, prefix + serial, pivot);
                    serial++;
                    list.Add(r);
                    added.Add(r);
                }
            }
            s.BuildingRecords = list.ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return added;
        }

        /// <summary>一座已建成的夹具建筑记录（与电网 / 诊断自检同一做法；电力优先级按表）。</summary>
        private static BuildingRecord Fixture(string typeId, string key, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":selfcheck_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
        }

        private static Transform BuildingVisual(string buildingId)
        {
            GameObject root = GameObject.Find("[HomeValley]");
            return root == null ? null : root.transform.Find("Building_" + HomeValleyController.LocalKey(buildingId));
        }

        private static void CheckVisualSyncEventDriven()
        {
            CampaignState s = NewWorld(90961, observe: true, scrap: 50);
            HomeValleyController home = WorldSimulation.Home;
            GameClock.SetPaused(true);
            List<BuildingRecord> added = AddFixtures(s, 800, "vis_");
            int full0 = home.VisualFullPasses;
            FrameOnce(1f / 60f);
            int visuals = VisualBuildingCount(out _);
            int records = s.BuildingRecords.Count(b => b.RegionId == HomeValleyLayout.RegionId);
            Expect(home.VisualFullPasses == full0 + 1 && visuals == records && records >= 800,
                $"V 建筑记录数组换了（新增 {added.Count} 座）：下一帧整份对账一次，画面建筑 {visuals} = 记录 {records}");

            // 稳态：什么都没变的 60 帧里一次都不写（外观签名没变），也不整份对账；每帧只轮询固定座数。
            long writes0 = home.VisualWrites;
            int full1 = home.VisualFullPasses;
            int maxRefresh = 0;
            for (int i = 0; i < 60; i++)
            {
                FrameOnce(1f / 60f);
                maxRefresh = Math.Max(maxRefresh, home.LastVisualRefreshCount);
            }
            int slice = GridContent.TuningInt("home.visual_slice_buildings");
            Expect(home.VisualWrites == writes0 && home.VisualFullPasses == full1 && maxRefresh <= slice + 4,
                $"V 稳态 60 帧：画面写入 {home.VisualWrites - writes0} 次、整份对账 {home.VisualFullPasses - full1} 次；每帧最多轮询 {maxRefresh} 座（上限 = home.visual_slice_buildings {slice} + 施工现场），与建筑总数 {records} 无关");

            // 模拟经正式入口改了外观（电塔被毁 → 断电）：变化集里的几座下一帧就重画，不整份对账。
            BuildingRecord pole = added.First(b => b.BuildingTypeId == "power_pole");
            long marks0 = BuildingVisualFeed.MarkCount;
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, pole.BuildingId);
            long marked = BuildingVisualFeed.MarkCount - marks0;
            writes0 = home.VisualWrites;
            full1 = home.VisualFullPasses;
            FrameOnce(1f / 60f);
            Transform pv = BuildingVisual(pole.BuildingId);
            Color want = HomeValleyController.ColorForBuilding(pole);
            bool poleDrawn = pv != null && pv.GetComponent<Renderer>().sharedMaterial.color == want;
            Expect(marked >= 1 && poleDrawn && home.VisualFullPasses == full1 && home.VisualWrites - writes0 <= marked + slice,
                $"V 电塔被毁（正式入口）：模拟侧报了 {marked} 座外观变化，下一帧就画成受损色，只写了 {home.VisualWrites - writes0} 次、没有整份对账");

            // 漏报的改动（直接改字段，不经变化集）：轮询兜底，最迟 建筑数 ÷ 每帧座数 帧内画出来。
            BuildingRecord quiet = added[added.Count / 2];
            quiet.ConstructionState = BuildingConstructionState.Damaged;
            Color quietWant = HomeValleyController.ColorForBuilding(quiet);
            int frames = 0;
            int bound = records / Math.Max(1, slice) + 2;
            while (frames < bound + 5 && BuildingVisual(quiet.BuildingId).GetComponent<Renderer>().sharedMaterial.color != quietWant)
            {
                FrameOnce(1f / 60f);
                frames++;
            }
            Expect(BuildingVisual(quiet.BuildingId).GetComponent<Renderer>().sharedMaterial.color == quietWant && frames <= bound,
                $"V 漏报的改动由轮询兜底：{frames} 帧后画出来（上限 {records} ÷ {slice} + 2 = {bound} 帧）");

            // 新增 / 移除（数组换了）：下一帧就有 / 就没有。
            GridCell? spot = FindValid(s, HomeValleyLayout.BuildingTypeGenerator2, new GridCell(HomeGridService.CorePivot(s).X + 30, HomeGridService.CorePivot(s).Y + 30), 30);
            BuildingRecord extra = spot.HasValue ? FgPowerGridSelfCheck.AddBuilt(s, HomeValleyLayout.BuildingTypeGenerator2, "vis_extra", spot.Value) : null;
            FrameOnce(1f / 60f);
            bool appear = extra != null && BuildingVisual(extra.BuildingId) != null;
            s.BuildingRecords = s.BuildingRecords.Where(b => b != extra).ToArray();
            FrameOnce(1f / 60f);
            bool vanish = extra != null && BuildingVisual(extra.BuildingId) == null;
            // 地面物：生成下一帧出现，拿走下一帧消失（按 ID 缓存表现对象）。
            GroundItemRecord gi = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(HomeGridService.CorePivot(s).X + 5, HomeGridService.CorePivot(s).Y + 5),
                CampaignEconomyLedger.ResourceScrap, 3, "fglog09-vis-ground");
            FrameOnce(1f / 60f);
            GameObject root = GameObject.Find("[HomeValley]");
            bool groundIn = gi != null && root.transform.Find("GroundItem_" + gi.GroundItemId) != null;
            s.GroundItems = s.GroundItems.Where(g => g.GroundItemId != gi?.GroundItemId).ToArray();
            FrameOnce(1f / 60f);
            bool groundOut = gi != null && root.transform.Find("GroundItem_" + gi.GroundItemId) == null;
            Expect(appear && vanish && groundIn && groundOut,
                "V 新增 / 移除建筑（记录数组换了）下一帧就画出 / 就消失；地面物生成 / 被拿走下一帧跟着变（不再逐帧扫根节点的全部子节点）");

            // 每帧开销与建筑数无关：60 座 vs 800 座的家园，暂停中逐帧（只有画面对账与界面，没有模拟步）。
            double small = VisualFrameMs(60, out double smallP95);
            double large = VisualFrameMs(800, out double largeP95);
            PerfLines.Add($"家园画面每帧（暂停中，只有对账与界面；Editor）：60 座 平均 {small:F3} / p95 {smallP95:F3} ms；800 座 平均 {large:F3} / p95 {largeP95:F3} ms（改前逐帧整份对账 O(建筑数)）");
            Expect(large <= small * 1.3 + 0.12,
                $"V 画面对账每帧开销与建筑数无关：60 座 {small:F3} ms、800 座 {large:F3} ms（允许 30% + 0.12 ms 噪声）");
        }

        private static double VisualFrameMs(int buildings, out double p95)
        {
            CampaignState s = NewWorld(90962, observe: true, scrap: 50);
            GameClock.SetPaused(true);
            AddFixtures(s, buildings, "visp_");
            for (int i = 0; i < 30; i++)
            {
                FrameOnce(1f / 60f);
            }
            var samples = new List<double>(300);
            var sw = new Stopwatch();
            GC.Collect();
            for (int i = 0; i < 300; i++)
            {
                sw.Restart();
                FrameOnce(1f / 60f);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            p95 = samples[(int)(samples.Count * 0.95)];
            GameClock.SetPaused(false);
            return samples.Average();
        }

        // ── I 接入计时按模拟步（DEBT-FG0ARCH01-09）──────────────────────────────────────

        private static void CheckInteractionTimersFollowSteps()
        {
            var results = new List<string>();
            var progresses = new List<float>();
            bool allPossessed = true;
            bool discriminates = false;
            foreach (int fps in new[] { 30, 50, 75, 144 })
            {
                CampaignState s = NewWorld(90971, observe: true, scrap: 50);
                HomeValleyController home = WorldSimulation.Home;
                GridCell core = HomeGridService.CorePivot(s);
                var at = new Vector2(core.X - 8, core.Y - 8);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
                WorldSimulation.StepMany(2);
                // 正式路径：机器列表里点这台 → 信号接入（0.35 秒过渡，镜头进直控）→ 提交接管。
                GameLogic.Campaign.Signal.UplinkRequestResult up = GameLogic.Campaign.Signal.SignalUplinkService.Request(r.LogicId, GameLogic.Campaign.Signal.UplinkSource.MachineList);
                for (int i = 0; i < 120 && home.PossessedMachineLogicId != r.LogicId; i++)
                {
                    FrameOnce(1f / 60f);
                }
                for (int i = 0; i < 30; i++)
                {
                    FrameOnce(1f / 60f); // 镜头落地
                }
                bool possessed = up.Accepted && home.PossessedMachineLogicId == r.LogicId;
                string possessWhy = possessed ? string.Empty : $"（接入 {up.Accepted}/{up.Failure}，受控 {home.PossessedMachineLogicId}，镜头 {WorldView.Director.Mode}，登记 {r.Success}/{r.Message}）";
                allPossessed &= possessed;
                Vector2 pos = home.LivePosition(r.LogicId) ?? at;
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, pos, CampaignEconomyLedger.ResourceScrap, 5, "fglog09-interact-" + fps);
                FrameOnce(0f);
                _reader.Held.Add(GameSettings.KeyBindings.GetKey(GameActionId.Interact));
                long t0 = GameClock.Ticks;
                long t1 = t0 + 20;
                double stepSeconds = 0;
                double scaledSeconds = 0;
                int frames = 0;
                while (GameClock.Ticks < t1 && frames < 10000)
                {
                    FrameOnce(1f / fps, t1);
                    stepSeconds += GameClock.FrameStepSeconds;
                    scaledSeconds += GameClock.FrameScaledDt;
                    frames++;
                }
                float progress = home.Interact.Progress01;
                discriminates |= Math.Abs(scaledSeconds - stepSeconds) > 1e-4;
                _reader.Held.Clear();
                progresses.Add(progress);
                results.Add($"{fps} fps：{frames} 帧走 {GameClock.Ticks - t0} 步，按步累计 {stepSeconds:F4} 秒 / 按帧累计 {scaledSeconds:F4} 秒，读条 {progress:F5}{possessWhy}");
            }
            float expect = 20f / 60f / 0.5f;
            Expect(allPossessed && discriminates && progresses.All(p => Math.Abs(p - progresses[0]) < 1e-6f) && Math.Abs(progresses[0] - expect) < 1e-4f,
                $"I 交互读条按本帧实际模拟的步推进（DEBT-FG0ARCH01-09）：按住交互键走同样的 20 个模拟步，30 / 50 / 75 / 144 fps 下读条完全相同 = {expect:F4}" +
                $"（50 / 75 fps 的帧时间不整除步长：按帧累计的游戏时间与按步累计不同，旧写法读条会跟着帧率变；{string.Join("；", results)}）");
        }

        // ── N 寻路桥接边界（DEBT-FG0ARCH06-07）────────────────────────────────────────

        private static void CheckNavBridgeBoundary()
        {
            string root = Path.Combine(Application.dataPath, "GameScripts", "HotFix", "GameLogic");
            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine("Campaign", "Nav", "NavService.cs"),
                Path.Combine("Campaign", "Combat", "CombatSite.cs"),
                Path.Combine("Campaign", "CampaignFgStateDomains.cs"),
            };
            var offenders = new List<string>();
            foreach (string f in files)
            {
                string rel = f.Substring(root.Length + 1);
                string text = File.ReadAllText(f);
                bool kernelType = text.Contains("NavKernel");
                bool kernelCalls = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b[kK]ernel\.(ResultPoint|RouteClear|Result|FindNow|Enqueue|TrySchedule)\(");
                if ((kernelType || kernelCalls) && !allowed.Contains(rel))
                {
                    offenders.Add(rel);
                }
            }
            Expect(files.Length > 300 && offenders.Count == 0,
                $"N 热更层碰寻路内核只经 NavService（DEBT-FG0ARCH06-07）：扫描 {files.Length} 个热更源文件，引用 NavKernel 的只有 NavService、CombatSite（战斗内核与寻路内核的 AOT 交接，由 NavService 在采纳窗口里调用）与存档域注释" +
                (offenders.Count > 0 ? "；越界：" + string.Join("、", offenders) : string.Empty));
        }

        // ── Z 存档体积随修改面积增长、与探索面积无关（FGR-SYS-005 / FGR-GEN-062）─────────────────────

        private static void CheckSaveSizeVsExploration()
        {
            CampaignState s = NewWorld(90981, observe: true, scrap: 50);
            SaveTo(Slot);
            long size0 = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            int cs = map.ChunkSize;
            int before = map.LoadedChunkCount;
            for (int dy = 0; dy < 20; dy++)
            {
                for (int dx = 0; dx < 20; dx++)
                {
                    map.ChunkAt(new GridCell(core.X + 1500 + dx * cs, core.Y + dy * cs), out _);
                }
            }
            int explored = map.LoadedChunkCount - before;
            SaveTo(Slot);
            long size1 = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            int diffs1 = CampaignSession.Current.World.ChunkDiffs.Length;
            for (int k = 0; k < 4; k++)
            {
                var pc = new GridCell(core.X + 1500 + k * cs * 3 + 3, core.Y + 3);
                map.SetPollution(pc, (byte)((map.GetPollution(pc) + 1) % 4)); // 在原值上改一档（远处的地块可能生成时就带污染）
            }
            SaveTo(Slot);
            long size2 = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            int diffs2 = CampaignSession.Current.World.ChunkDiffs.Length;
            PerfLines.Add($"存档体积：新战役 {size0 / 1024.0:F1} KB；再生成 {explored} 个区块（没改）后 {size1 / 1024.0:F1} KB；改了 4 个区块后 {size2 / 1024.0:F1} KB（每个区块差异约 {(size2 - size1) / 4.0 / 1024.0:F2} KB）");
            Expect(explored >= 300 && Math.Abs(size1 - size0) <= 4096 && diffs2 == diffs1 + 4 && size2 > size1,
                $"Z 存档体积随被修改的面积增长、与探索面积无关（FGR-SYS-005 / FGR-GEN-062）：多生成 {explored} 个区块体积变化 {size1 - size0} 字节（≤ 4 KB），改了 4 个区块后多 {diffs2 - diffs1} 条区块差异、{size2 - size1} 字节");
        }

        // ── P 规模性能门禁（FGT-LOG-012；FG03 第 7 节 / FGR-SYS-041 / 042）───────────────────────

        private sealed class ScaleNumbers
        {
            public int Buildings;
            public int BeltCells;
            public int BeltItems;
            public int PipeCells;
            public int Ghosts;
            public double WorldAvg;
            public double WorldP95;
            public double BeltPerKernelStep;
            public double BeltKernelP95;
            public double PipePerKernelStep;
            public double PipeKernelP95;
            public double Hot;
            public double Frame3xAvg;
            public double Frame3xP95;
            public double AllocPerStep;
            public int DirtyWindows;
            public int BurstSteps;
            public int BurstBuilt;
            public double BurstAvg;
            public double BurstWorst;
            /// <summary>施工高峰里没有虚影完工的步：世界步 − 传送带 / 管线 / 战斗内核。</summary>
            public double BurstHot;
        }

        /// <summary>
        /// 施工高峰：把材料放进仓库，施工队列里的虚影由搬运机取料、施工、完工，一直跑到队列清空（最多 3,600 步）。
        /// 每步记世界步耗时；没有虚影完工的步再扣掉三个内核的耗时，得到“施工进行中”的热更层开销。
        /// </summary>
        private static void MeasureBurst(CampaignState s, ScaleNumbers n)
        {
            BeltKernel bk = BeltNetworkService.Kernel;
            PipeKernel pk = PipeNetworkService.Kernel;
            List<BuildingRecord> ghosts = s.BuildingRecords.Where(HomeValleyController.IsPlannedGhost).ToList();
            int left = ghosts.Count;
            int start = left;
            s.Scrap = ghosts.Sum(g => Math.Max(0, g.ConstructionRequired)) + 50;
            HomeValleyWorkOrders.MarkAssignmentDirty();
            var all = new List<double>(1200);
            var hot = new List<double>(1200);
            var sw = new Stopwatch();
            GC.Collect();
            while (all.Count < 3600 && (left > 0 || HomeValleyWorkOrders.CountActiveConstruction(s) > 0))
            {
                long b0 = BeltNetworkService.KernelStepsThisSession;
                long p0 = PipeNetworkService.KernelStepsThisSession;
                long c0 = WorldSimulation.Home.Combat.Kernel.Steps;
                sw.Restart();
                WorldSimulation.StepMany(1);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                all.Add(ms);
                if (BeltNetworkService.KernelStepsThisSession > b0)
                {
                    ms -= bk.LastStepMs;
                }
                if (PipeNetworkService.KernelStepsThisSession > p0)
                {
                    ms -= pk.LastStepMs;
                }
                if (WorldSimulation.Home.Combat.Kernel.Steps > c0)
                {
                    ms -= WorldSimulation.Home.Combat.Kernel.LastStepMs;
                }
                int now = s.BuildingRecords.Count(HomeValleyController.IsPlannedGhost); // 计时之外数（测量本身的 O(建筑数) 不计入）
                if (now == left)
                {
                    hot.Add(Math.Max(0, ms));
                }
                left = now;
            }
            n.BurstSteps = all.Count;
            n.BurstBuilt = start - left;
            n.BurstAvg = all.Count > 0 ? all.Average() : double.NaN;
            n.BurstWorst = all.Count > 0 ? all.Max() : double.NaN;
            n.BurstHot = hot.Count > 0 ? hot.Average() : double.NaN;
        }

        /// <summary>
        /// 搭一个规模场景：<paramref name="full"/> = FG03 第 7 节规模（传送带 15,045 格 / 约 3.4 万件含 340 个节点、管线 3,000 格、建筑 800 座含电塔与发电 / 用电建筑、30 座虚影、20 台搬运机）；
        /// false = 约十分之一（1,600 格 / 300 格 / 80 座；虚影与机器相同）。传送带 / 管线直接写进正式内核（随存档），建筑是已建成夹具，虚影走正式放置入口。
        /// </summary>
        private static CampaignState BuildScale(bool full, int seed, out ScaleNumbers n)
        {
            n = new ScaleNumbers();
            CampaignState s = NewWorld(seed, observe: true, scrap: 0);
            GridCell core = HomeGridService.CorePivot(s);
            BeltKernel bk = BeltNetworkService.Kernel;
            if (full)
            {
                FgBeltNodeSelfCheck.BuildHugeWithNodes(bk, core.X - 160, core.Y + 60);
            }
            else
            {
                for (int u = 0; u < 40; u++)
                {
                    for (int x = 0; x < 40; x++)
                    {
                        bk.AddCell(core.X - 160 + x, core.Y + 60 + u * 3, BeltDir.East, u % 3);
                    }
                    bk.AddSource(95000 + u, core.X - 160, core.Y + 60 + u * 3, BeltItems.ScrapId, 1, BeltConst.Unlimited);
                    bk.AddSink(96000 + u, core.X - 120, core.Y + 60 + u * 3, BeltConst.Unlimited, 1);
                }
            }
            bk.EnsureTopology();
            BeltNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
            FgPipeSelfCheck.BuildPerfNetworks(PipeNetworkService.Kernel, core.X - 700, core.Y + 60, full ? 30 : 3);
            PipeNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
            AddFixtures(s, full ? 800 : 80, "perf_");
            for (int i = 0; i < 20; i++)
            {
                SpawnRegistered(s, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, new Vector2(core.X - 10 + (i % 5) * 2, core.Y - 10 - (i / 5) * 2), 100f);
            }
            WorldSimulation.StepMany(2);
            int ghosts = PlaceGhostRing(s, core, 30);
            n.Buildings = s.BuildingRecords.Length;
            n.BeltCells = bk.CellCount;
            n.BeltItems = bk.ItemCount;
            n.PipeCells = PipeNetworkService.Kernel.CellCount;
            n.Ghosts = ghosts;
            return s;
        }

        /// <summary>从核心由近及远（已探索区内）放 <paramref name="target"/> 座电塔虚影（正式放置入口；库存 0 = 缺料等待）。返回放上的座数。</summary>
        internal static int PlaceGhostRing(CampaignState s, GridCell core, int target)
        {
            int n = 0;
            for (int r = 6; r <= 40 && n < target; r++)
            {
                for (int dy = -r; dy <= r && n < target; dy += 2)
                {
                    for (int dx = -r; dx <= r && n < target; dx += 2)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, "power_pole", c, 0, checkCost: false).Ok && HomeGridService.TryPlace(s, "power_pole", c, 0).Success)
                        {
                            n++;
                        }
                    }
                }
            }
            return n;
        }

        private static void MeasureSteps(ScaleNumbers n)
        {
            BeltKernel bk = BeltNetworkService.Kernel;
            PipeKernel pk = PipeNetworkService.Kernel;
            WorldSimulation.StepMany(300);
            var world = new List<double>(900);
            var belt = new List<double>(300);
            var pipe = new List<double>(300);
            double combatSum = 0;
            var sw = new Stopwatch();
            GC.Collect();
            for (int i = 0; i < 900; i++)
            {
                long b0 = BeltNetworkService.KernelStepsThisSession;
                long p0 = PipeNetworkService.KernelStepsThisSession;
                long c0 = WorldSimulation.Home.Combat.Kernel.Steps;
                sw.Restart();
                WorldSimulation.StepMany(1);
                sw.Stop();
                world.Add(sw.Elapsed.TotalMilliseconds);
                if (BeltNetworkService.KernelStepsThisSession > b0)
                {
                    belt.Add(bk.LastStepMs);
                }
                if (PipeNetworkService.KernelStepsThisSession > p0)
                {
                    pipe.Add(pk.LastStepMs);
                }
                if (WorldSimulation.Home.Combat.Kernel.Steps > c0)
                {
                    combatSum += WorldSimulation.Home.Combat.Kernel.LastStepMs;
                }
            }
            world.Sort();
            belt.Sort();
            pipe.Sort();
            n.WorldAvg = world.Average();
            n.WorldP95 = world[(int)(world.Count * 0.95)];
            n.BeltPerKernelStep = belt.Count > 0 ? belt.Average() : 0;
            n.BeltKernelP95 = belt.Count > 0 ? belt[(int)(belt.Count * 0.95)] : 0;
            n.PipePerKernelStep = pipe.Count > 0 ? pipe.Average() : 0;
            n.PipeKernelP95 = pipe.Count > 0 ? pipe[(int)(pipe.Count * 0.95)] : 0;
            n.Hot = Math.Max(0, n.WorldAvg - belt.Sum() / world.Count - pipe.Sum() / world.Count - combatSum / world.Count);

            // 3x、120 帧：每帧 1.5 个模拟步 + 家园画面对账与绘制准备。
            var frame = new List<double>(600);
            GameClock.SetSpeed(3f);
            for (int i = 0; i < 600; i++)
            {
                sw.Restart();
                FrameOnce(1f / 120f);
                sw.Stop();
                frame.Add(sw.Elapsed.TotalMilliseconds);
            }
            GameClock.SetSpeed(1f);
            frame.Sort();
            n.Frame3xAvg = frame.Average();
            n.Frame3xP95 = frame[(int)(frame.Count * 0.95)];

            // 托管分配：稳态步的托管堆增量（丢掉发生 GC 的窗口）。
            long clean = 0;
            int cleanSteps = 0;
            for (int w = 0; w < 6; w++)
            {
                GC.Collect();
                int gc0 = GC.CollectionCount(0);
                long h0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                WorldSimulation.StepMany(100);
                long h1 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                if (GC.CollectionCount(0) != gc0)
                {
                    n.DirtyWindows++;
                    continue;
                }
                clean += Math.Max(0, h1 - h0);
                cleanSteps += 100;
            }
            n.AllocPerStep = cleanSteps > 0 ? clean / (double)cleanSteps : double.NaN;
        }

        private static void CheckScalePerformance()
        {
            CampaignState smallState = BuildScale(false, 90991, out ScaleNumbers small);
            MeasureSteps(small);
            MeasureBurst(smallState, small);
            CampaignState s = BuildScale(true, 90992, out ScaleNumbers large);
            MeasureSteps(large);
            MeasureBurst(s, large);
            Line($"    规模场景：大 = 建筑 {large.Buildings} 座（虚影 {large.Ghosts}）、传送带 {large.BeltCells:N0} 格 / {large.BeltItems:N0} 件、管线 {large.PipeCells:N0} 格；" +
                 $"小 = 建筑 {small.Buildings}、传送带 {small.BeltCells:N0} 格、管线 {small.PipeCells:N0} 格");
            foreach ((string name, ScaleNumbers n) in new[] { ("小", small), ("大", large) })
            {
                PerfLines.Add($"{name}规模：世界步平均 {n.WorldAvg:F3} / p95 {n.WorldP95:F3} ms；传送带内核每个内核步平均 {n.BeltPerKernelStep:F3} / p95 {n.BeltKernelP95:F3} ms；" +
                              $"管线内核 {n.PipePerKernelStep:F4} / p95 {n.PipeKernelP95:F4} ms；热更层每世界步 {n.Hot:F3} ms；3x 120 帧每帧平均 {n.Frame3xAvg:F3} / p95 {n.Frame3xP95:F3} ms；" +
                              $"每步托管堆增量 {n.AllocPerStep:F1} B（{n.DirtyWindows} 个窗口发生 GC 不计）；" +
                              $"施工高峰 {n.BurstSteps} 步完工 {n.BurstBuilt} 座：每步平均 {n.BurstAvg:F3} / 最慢 {n.BurstWorst:F2} ms，完工以外的步热更层平均 {n.BurstHot:F3} ms");
            }
            Expect(large.Buildings >= 800 && large.BeltCells >= 15000 && large.BeltItems >= 30000 && large.PipeCells >= 3000 && large.Ghosts >= 30 && small.Ghosts >= 30,
                $"P 场景达到 FG03 第 7 节规模：建筑 {large.Buildings}（其中施工队列里的虚影 {large.Ghosts}）、传送带 {large.BeltCells:N0} 格 / {large.BeltItems:N0} 件、管线 {large.PipeCells:N0} 格");
            Expect(large.BeltKernelP95 + large.PipeKernelP95 <= 2.0,
                $"P 物流内核单步 ≤ 2 ms（FG03 第 7 节）：传送带 p95 {large.BeltKernelP95:F3} + 管线 p95 {large.PipeKernelP95:F4} = {large.BeltKernelP95 + large.PipeKernelP95:F3} ms（Editor Burst，与真机同为原生）");
            Expect(large.Hot <= small.Hot * 1.3 + 0.1,
                $"P 热更层每步开销与数量无关（FGR-LOG-090 / FGR-SYS-042）：约十分之一规模 {small.Hot:F3} ms、FG03 第 7 节规模 {large.Hot:F3} ms（允许 30% + 0.1 ms 噪声）");
            // FG3-LOG-09：施工进行中（取料 → 施工 → 完工）的热更层同样与建筑数无关。改前派工 / 施工 / 赶路每步按建筑数线性找现场与取料点，
            // 800 座建筑时 28 座虚影同时施工每步平均 2.2 ms（117 座时 0.55 ms）；完工那一步的电网 / 格网重算是按事件的 O(建筑数)，单独报告不在此比。
            Expect(small.BurstBuilt >= 27 && large.BurstBuilt >= 27 && large.BurstHot <= small.BurstHot * 1.3 + 0.1,
                $"P 施工进行中热更层每步开销与建筑数无关（FGR-SYS-042）：30 座虚影同时施工到完工（小 {small.BurstBuilt} / 大 {large.BurstBuilt} 座），" +
                $"完工以外的步热更层平均 小 {small.BurstHot:F3} / 大 {large.BurstHot:F3} ms（允许 30% + 0.1 ms）；完工步另计，最慢一步 小 {small.BurstWorst:F2} / 大 {large.BurstWorst:F2} ms");
            Expect(large.Frame3xP95 <= 1000.0 / 120.0,
                $"P 3x 速度、120 帧下每帧（1.5 个模拟步 + 家园画面）p95 {large.Frame3xP95:F2} ms ≤ 8.33 ms（Editor；真机另测）");
            Expect(!double.IsNaN(large.AllocPerStep) && large.AllocPerStep <= Math.Max(small.AllocPerStep, 64) * 1.5 + 64,
                $"P 稳态每步托管堆增量不随规模增长：小 {small.AllocPerStep:F0} B、大 {large.AllocPerStep:F0} B");

            // 改线重建（DEBT-FG0ARCH02-09）：大网络上改一格，只在下一步前重建一次。
            BeltKernel bk = BeltNetworkService.Kernel;
            var edits = new List<double>();
            var sw = new Stopwatch();
            GridCell core = HomeGridService.CorePivot(s);
            for (int i = 0; i < 5; i++)
            {
                bk.AddCell(core.X - 170, core.Y + 60 + i * 5, BeltDir.East, 0);
                sw.Restart();
                bk.EnsureTopology();
                sw.Stop();
                edits.Add(sw.Elapsed.TotalMilliseconds);
            }
            PerfLines.Add($"改线重建（15,045 格上改一格后整图重建一次）：{string.Join(" / ", edits.Select(x => x.ToString("F2")))} ms");
            Expect(edits.Max() <= 4.0,
                $"P 大网络改一格后的拓扑重建 {edits.Max():F2} ms ≤ 4 ms（半个 120 帧；只在编辑后的下一步发生一次，同一帧编辑多少格都只重建一次）——整图重建保留，不做增量（DEBT-FG0ARCH02-09 关闭）");

            // 存档体积与存读时长（FGR-SYS-005：后期存档 ≤ 50 MB、存档 ≤ 2 秒、读档 ≤ 15 秒）。
            var sws = Stopwatch.StartNew();
            SaveTo(Slot);
            sws.Stop();
            long bytes = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            var swl = Stopwatch.StartNew();
            RestoreResult rr = LoadCopy(Slot, HomeValleyLayout.RegionId);
            swl.Stop();
            var swg = Stopwatch.StartNew();
            BeltNetworkService.ApplyGridLayer(CampaignSession.Current, HomeGridService.MapFor(CampaignSession.Current));
            swg.Stop();
            bool back = rr.Success && BeltNetworkService.Kernel.CellCount == large.BeltCells + 5 && PipeNetworkService.Kernel.CellCount == large.PipeCells;
            PerfLines.Add($"规模存档：{bytes / 1024.0 / 1024.0:F2} MB，写 {sws.Elapsed.TotalMilliseconds:F0} ms，读（恢复 + 载入家园）{swl.Elapsed.TotalMilliseconds:F0} ms；读档时格网传送带层套用 {swg.Elapsed.TotalMilliseconds:F2} ms");
            Expect(back && bytes <= 50L * 1024 * 1024 && sws.Elapsed.TotalMilliseconds <= 2000 && swl.Elapsed.TotalMilliseconds <= 15000,
                $"P FGR-SYS-005：FG03 第 7 节规模的存档 {bytes / 1024.0 / 1024.0:F2} MB ≤ 50 MB，存档 {sws.Elapsed.TotalMilliseconds:F0} ms ≤ 2 秒，读档 {swl.Elapsed.TotalMilliseconds:F0} ms ≤ 15 秒（Editor），读回规模不丢");
            Expect(swg.Elapsed.TotalMilliseconds <= 50,
                $"P 读档时热更层把 15,000 格写进格网传送带层 {swg.Elapsed.TotalMilliseconds:F2} ms ≤ 50 ms（装载期一次；DEBT-FG0ARCH02-11 的 Editor 数据，真机折算见证据）");
        }
    }
}
