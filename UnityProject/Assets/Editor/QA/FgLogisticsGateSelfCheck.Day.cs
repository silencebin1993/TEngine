using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.EditorTools
{
    public static partial class FgLogisticsGateSelfCheck
    {
        /// <summary>1 个游戏日 = clock.day_seconds（1x 下 20 分钟）= 72,000 个 60 Hz 世界步。</summary>
        private static long DayTicks => (long)Math.Round(GameClock.DaySeconds * GameClock.StepHz);

        // ── D 后台一致性（FGT-LOG-011；FGR-BASE-021 / B24）──────────────────────────────

        /// <summary>
        /// 同一存档（满载产线 + 一支真实派遣出去的远征队）跑 1 个游戏日，三遍：
        /// O 镜头一直看家园（帧驱动 3x）；L 离开再返回（镜头切到远征地点 → 回家园 → 飞到星球表面 900 格外 → 回核心 → 暂停两次、1x / 2x / 3x →
        ///   再去远征地点 → 回家园）；H 完全不经过镜头（无头推进）。三遍在同样的步上发生同样的世界事件（放新虚影、仓库到货），
        ///   镜头事件只在 L。三遍终态全状态逐字段一致；家园在镜头离开期间真的在生产与施工；每次回到家园，画面按当前状态重建（含施工中的虚影）。
        /// </summary>
        private static void CheckObservedLeaveReturnOneDay()
        {
            CampaignState s0 = NewWorld(90931, observe: true, scrap: 400);
            Line3 L = BuildFullLine(s0, withExpeditionMachines: true);
            if (L.Failure != null)
            {
                Fail("D 满载产线搭不起来：" + L.Failure);
                return;
            }
            RegionRecord ruins = FracturedCityRegion.Find(s0);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            int[] roster = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                Fail($"D 真实派遣远征失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            WorldSimulation.StepMany(60 * 5);
            if (!SaveTo(Slot))
            {
                return;
            }
            long start = GameClock.Ticks;
            long end = start + DayTicks;
            string home = HomeValleyLayout.RegionId;
            string away = FracturedCityLayout.RegionId;

            // 世界事件（三遍都在同一步发生；全部经正式入口，只按种子地形现找位置）。
            GridCell c0 = HomeGridService.CorePivot(s0);
            var world = new SortedDictionary<long, Action<CampaignState>>
            {
                [start + 3600] = s => PlaceDayGhost(s, new GridCell(c0.X - 30, c0.Y + 12), 150),   // 第一次离开前放下，离开期间应当建完
                [start + 17000] = s => PlaceDayGhost(s, new GridCell(c0.X + 14, c0.Y - 34), 60),   // 第一次回来前 1,000 步放下：回来时施工中
                [start + 50000] = s => PlaceDayGhost(s, new GridCell(c0.X - 34, c0.Y - 20), 60),   // 第二次回来前 600 步放下：回来时施工中
            };

            // O：一直看家园（3x，帧驱动：每帧跑家园的输入 / 画面对账 / 绘制准备）。
            var sw = Stopwatch.StartNew();
            LoadCopy(Slot, home);
            var cueO = CueCounts();
            var noteO = NoteCounts();
            int oFrames = 0;
            int oHomeFrames = 0;
            GameClock.SetSpeed(3f);
            RunDayFrames(end, world, null, (tick, frame) =>
            {
                if (WorldView.ObservedSiteId != home)
                {
                    WorldView.Observe(home); // 远征队覆没时镜头会自动回家园；这一遍始终拉回家园。
                }
                oFrames++;
            }, () => oHomeFrames += WorldView.ObservedSiteId == home ? 1 : 0);
            GameClock.SetSpeed(1f);
            Dictionary<string, string> o = Snap(cueO, noteO, L);
            double oMs = sw.Elapsed.TotalMilliseconds;

            // L：离开再返回。
            sw.Restart();
            LoadCopy(Slot, home);
            var cueL = CueCounts();
            var noteL = NoteCounts();
            int lFrames = 0;
            int awayFrames = 0;
            int returns = 0;
            int pauses = 0;
            bool visualsRebuilt = true;
            var visualLines = new List<string>();
            long awayDelivered = 0;
            int awayBuilt = 0;
            long awayStartDelivered = 0;
            int awayStartBuilt = 0;
            GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
            void Leave()
            {
                if (WorldView.Observe(away))
                {
                    awayStartDelivered = PortTotal(L.WarehouseInPort);
                    awayStartBuilt = BuiltCount();
                }
            }
            void Return()
            {
                long delivered = PortTotal(L.WarehouseInPort) - awayStartDelivered;
                int built = BuiltCount() - awayStartBuilt;
                awayDelivered += Math.Max(0, delivered);
                awayBuilt += Math.Max(0, built);
                WorldView.Observe(home);
                FrameOnce(0f);
                returns++;
                int visuals = VisualBuildingCount(out int ghostsDrawnFlat);
                int records = CampaignSession.Current.BuildingRecords.Count(b => b != null && b.RegionId == home);
                int ghosts = CampaignSession.Current.BuildingRecords.Count(b => b != null && b.RegionId == home && HomeValleyController.IsPlannedGhost(b));
                visualsRebuilt &= visuals == records && ghostsDrawnFlat == ghosts && ghosts > 0;
                visualLines.Add($"第 {returns} 次回来（第 {GameClock.Ticks - start} 步）：画面建筑 {visuals} / 记录 {records}、扁平虚影 {ghostsDrawnFlat} / 虚影记录 {ghosts}；离开期间仓库收货 {delivered} 件、完工 {built} 座");
            }
            void PauseFrames()
            {
                GameClock.SetPaused(true);
                pauses++;
                for (int i = 0; i < 200; i++)
                {
                    FrameOnce(1f / 60f);
                    lFrames++;
                    awayFrames += WorldView.ObservedSiteId != home ? 1 : 0;
                }
                GameClock.SetPaused(false);
            }
            var camera = new SortedDictionary<long, Action<CampaignState>>
            {
                [start + 4500] = s => Leave(),
                [start + 18000] = s => Return(),
                [start + 21000] = s => WorldView.FlyTo(home, new Vector2(core.X + 900, core.Y + 40)), // 同一表面飞到 900 格外（活跃区块窗口跟着镜头走）
                [start + 30000] = s => WorldView.FocusHomeCore(),
                [start + 33000] = s => PauseFrames(),
                [start + 36000] = s => GameClock.SetSpeed(1f),
                [start + 38000] = s => { PauseFrames(); GameClock.SetSpeed(2f); },
                [start + 42000] = s => GameClock.SetSpeed(3f),
                [start + 45000] = s => Leave(),
                [start + 50600] = s => Return(),
            };
            GameClock.SetSpeed(3f);
            RunDayFrames(end, world, camera, (tick, frame) => lFrames++, () => awayFrames += WorldView.ObservedSiteId != home ? 1 : 0);
            GameClock.SetSpeed(1f);
            Dictionary<string, string> l = Snap(cueL, noteL, L);
            double lMs = sw.Elapsed.TotalMilliseconds;

            // H：无头推进（不经过镜头与输入）。
            sw.Restart();
            LoadCopy(Slot, null);
            var cueH = CueCounts();
            var noteH = NoteCounts();
            foreach (KeyValuePair<long, Action<CampaignState>> e in world)
            {
                WorldSimulation.StepMany((int)(e.Key - GameClock.Ticks));
                e.Value(CampaignSession.Current);
            }
            WorldSimulation.StepMany((int)(end - GameClock.Ticks));
            Dictionary<string, string> h = Snap(cueH, noteH, L);
            double hMs = sw.Elapsed.TotalMilliseconds;

            List<string> ol = FgWorldSimSelfCheck.DiffKeys(o, l);
            List<string> oh = FgWorldSimSelfCheck.DiffKeys(o, h);
            PerfLines.Add($"1 个游戏日（{DayTicks:N0} 步）：O 一直看家园 3x 帧驱动 {oFrames:N0} 帧 {oMs / 1000:F1} 秒；L 离开再返回 {lFrames:N0} 帧 {lMs / 1000:F1} 秒；H 无头 {hMs / 1000:F1} 秒（Editor）");
            foreach (string v in visualLines)
            {
                Line("    " + v);
            }
            Expect(oHomeFrames == oFrames && awayFrames > 5000 && returns == 2 && pauses == 2 && o.Count > 800,
                $"D 三遍都跑满 1 个游戏日：O 全程看家园（{oHomeFrames}/{oFrames} 帧）；L 离开家园 {awayFrames} 帧（两次去远征地点、一次飞到 900 格外）、回来 {returns} 次、暂停 {pauses} 次、1x / 2x / 3x 切换；H 无头；" +
                "三遍在同样的步上放了 3 座新虚影、仓库到货 3 次");
            Expect(ol.Count == 0 && oh.Count == 0,
                $"D 观察不改变结果（FGT-LOG-011 / FGR-BASE-021）：一直看家园、离开再返回、无头三遍的终态全状态逐字段一致（{o.Count} 个字段：格网与建筑、施工、传送带与统计、流体、电网与曲线、机器、远征、时钟、反馈与通知条数）" +
                (ol.Count + oh.Count > 0 ? $"；O≠L {Sample(ol, 6)}{CombatUnitDiffs(o, l)}；O≠H {Sample(oh, 6)}{CombatUnitDiffs(o, h)}" : string.Empty));
            Expect(awayDelivered > 0 && awayBuilt > 0,
                $"D 镜头离开期间家园真的在生产与施工：两次离开合计仓库输入口收货 {awayDelivered} 件、完工 {awayBuilt} 座（不是离开就停、回来再补算）");
            Expect(visualsRebuilt,
                "D 每次回到家园，画面按当前状态整份重建：建筑方块数 = 家园建筑记录数，施工中的虚影画成扁平方块（随进度长高）、已建成的是完整方块，与记录一致");
        }

        /// <summary>
        /// 帧驱动推进到 <paramref name="end"/>：在世界事件 / 镜头事件的步上精确停下执行（tickLimit），每帧前回调 <paramref name="beforeFrame"/>、每帧后回调 <paramref name="afterFrame"/>。
        /// </summary>
        private static void RunDayFrames(long end, SortedDictionary<long, Action<CampaignState>> world, SortedDictionary<long, Action<CampaignState>> camera,
            Action<long, int> beforeFrame, Action afterFrame)
        {
            var stops = new SortedSet<long>(world.Keys);
            if (camera != null)
            {
                stops.UnionWith(camera.Keys);
            }
            stops.Add(end);
            int frame = 0;
            foreach (long stop in stops)
            {
                while (GameClock.Ticks < stop && frame < 2_000_000)
                {
                    beforeFrame?.Invoke(GameClock.Ticks, frame);
                    FrameOnce(1f / 60f, stop);
                    afterFrame?.Invoke();
                    frame++;
                }
                if (GameClock.Ticks != stop || stop == end)
                {
                    continue;
                }
                // 同一步：先世界事件（三遍都一样），再镜头事件。
                if (world.TryGetValue(stop, out Action<CampaignState> w))
                {
                    w(CampaignSession.Current);
                }
                if (camera != null && camera.TryGetValue(stop, out Action<CampaignState> c))
                {
                    c(CampaignSession.Current);
                }
            }
        }

        /// <summary>世界事件：仓库到货 <paramref name="scrap"/>（测试捷径：代表回收所得），并在 <paramref name="near"/> 附近按格网规则找位置放一座发电机 2 虚影（正式放置入口）。</summary>
        private static void PlaceDayGhost(CampaignState s, GridCell near, int scrap)
        {
            s.Scrap += scrap;
            GridCell? spot = FindValid(s, HomeValleyLayout.BuildingTypeGenerator2, near, 20);
            if (spot.HasValue)
            {
                HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, spot.Value, 0);
            }
        }

        private static int BuiltCount() =>
            CampaignSession.Current.BuildingRecords.Count(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational);

        /// <summary>家园画面里的建筑方块数（名字前缀 Building_），以及其中按“虚影”画成扁平方块（高 &lt; 1.9）的个数。测试读数，只在断言时扫一次。</summary>
        private static int VisualBuildingCount(out int flatGhosts)
        {
            flatGhosts = 0;
            GameObject root = GameObject.Find("[HomeValley]");
            if (root == null)
            {
                return -1;
            }
            int n = 0;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                Transform c = root.transform.GetChild(i);
                if (!c.name.StartsWith("Building_", StringComparison.Ordinal))
                {
                    continue;
                }
                n++;
                if (c.localScale.y < 1.9f)
                {
                    flatGhosts++;
                }
            }
            return n;
        }
    }
}
