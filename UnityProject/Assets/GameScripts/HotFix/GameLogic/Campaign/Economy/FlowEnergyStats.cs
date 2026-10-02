using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using Unity.Mathematics;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-08（FG00 B19“本系统的关键产出进入统计面板”）：统计面板“物流与能源”页——把物流 / 能源各自已经在记的累计量与窗口读数汇到统计面板：
    /// - 电力（DEBT-FG3LOG06-04 / DEBT-FG4ECO04-03）：每个电网最近曲线（power.sample_seconds × power.curve_samples）里的平均发电（按发电类别）、平均需要与实际供上、缺电时长；储能存量与此刻充放电；
    ///   燃油发电机累计烧油、燃油耗尽次数（存档）。
    /// - 传送带（DEBT-FG3LOG03-08）：每个网络最近窗口的送达 / 推上速率、带上件数、堵塞格；清带送进仓库 / 丢弃的累计。
    /// - 分流器（DEBT-FG3LOG04-04）：每个分流器左右口的累计与最近窗口分出件数。
    /// - 管线（DEBT-FG3LOG05-06）：累计抽取、送达、冲洗、移除（升）。
    /// 全部读内核 / 存档里已有的累计与窗口（不新增逐帧统计）；只在面板刷新时组装文本，O(电网 × 曲线点 + 网络 + 分流器)。
    /// </summary>
    public static class FlowEnergyStats
    {
        private static readonly List<int> Subnets = new List<int>(4);
        private static readonly List<int2> Splitters = new List<int2>(8);

        public static void Build(CampaignState state, List<(string Text, string Cls)> lines)
        {
            lines.Clear();
            if (state == null)
            {
                return;
            }
            AppendPower(state, lines);
            AppendBelts(state, lines);
            AppendPipes(state, lines);
        }

        private static void AppendPower(CampaignState state, List<(string Text, string Cls)> lines)
        {
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            float sample = HomeValleyPowerGrid.SampleSeconds;
            lines.Add((GameText.Format("stats.flow.power_title", N(sample * HomeValleyPowerGrid.CurveSamples / 60f)), "st-row-title"));
            Subnets.Clear();
            if (k != null && ReferenceEquals(HomeValleyPowerGrid.BoundState, state))
            {
                HomeValleyPowerGrid.SubnetsBySerial(Subnets);
            }
            if (Subnets.Count == 0)
            {
                lines.Add((GameText.Get("stats.flow.no_grid"), "st-row-dim"));
            }
            int classes = Math.Min(HomeValleyPowerGrid.SourceClassCount, PowerKernel.MaxSourceClasses);
            foreach (int s in Subnets)
            {
                if (!HomeValleyPowerGrid.TryGetSubnetInfo(state, s, out PowerSubnetInfo info))
                {
                    continue;
                }
                string name = HomeValleyPowerGrid.SubnetName(info.Serial);
                double supply = 0, demand = 0, delivered = 0;
                int shortPoints = 0, n = 0;
                var perClass = new double[classes];
                if (k.TryGetCurve(info.Serial, out PowerCurve c) && c.Count > 0)
                {
                    n = c.Count;
                    for (int i = 0; i < n; i++)
                    {
                        c.Get(i, out float a, out float d, out float dl, out _);
                        supply += a;
                        demand += d;
                        delivered += dl;
                        if (dl + 0.5f < d)
                        {
                            shortPoints++;
                        }
                        for (int cls = 0; cls < classes; cls++)
                        {
                            perClass[cls] += c.GetClass(i, cls);
                        }
                    }
                }
                var parts = new List<string>(classes);
                for (int cls = 0; cls < classes && n > 0; cls++)
                {
                    if (perClass[cls] > 0.001)
                    {
                        parts.Add(GameText.Format("stats.flow.power_class", HomeValleyPowerGrid.SourceClassName(cls), N((float)(perClass[cls] / n))));
                    }
                }
                string mix = parts.Count > 0 ? string.Join(GameText.Get("stats.list_sep"), parts) : GameText.Get("stats.buildings.no_output");
                float shortMin = shortPoints * sample / 60f;
                lines.Add((GameText.Format("stats.flow.power_row", name, N((float)(n > 0 ? supply / n : info.Supply)), mix, N((float)(n > 0 ? demand / n : info.Demand)),
                    N((float)(n > 0 ? delivered / n : info.Delivered)), N(shortMin), N(n * sample / 60f)), shortPoints > 0 ? "st-row-warn" : null));
                if (info.StorageCapacity > 0)
                {
                    string flow = info.StorageFlow > 0.5f
                        ? GameText.Format("stats.flow.charging", N(info.StorageFlow))
                        : info.StorageFlow < -0.5f ? GameText.Format("stats.flow.discharging", N(-info.StorageFlow)) : GameText.Get("stats.flow.storage_idle");
                    lines.Add((GameText.Format("stats.flow.storage", N((float)info.Stored), N((float)info.StorageCapacity), flow), "st-row-sub"));
                }
            }
            long fuelMl = 0;
            int generators = 0;
            foreach (ProducerRecord r in state.Economy?.Producers ?? Array.Empty<ProducerRecord>())
            {
                if (r != null && r.FuelBurnedMl > 0)
                {
                    fuelMl += r.FuelBurnedMl;
                }
            }
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                if (p?.Def != null && p.Def.Mode == ProducerMode.Generator)
                {
                    generators++;
                }
            }
            if (generators > 0 || fuelMl > 0 || (state.Stats?.Production?.FuelOuts ?? 0) > 0)
            {
                lines.Add((GameText.Format("stats.flow.fuel", generators, N(fuelMl / 1000f), state.Stats?.Production?.FuelOuts ?? 0), null));
            }
        }

        private static void AppendBelts(CampaignState state, List<(string Text, string Cls)> lines)
        {
            lines.Add((GameText.Get("stats.flow.belt_title"), "st-row-title"));
            BeltKernel k = BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) ? BeltNetworkService.Kernel : null;
            int shown = 0;
            if (k != null)
            {
                for (int net = 0; net < k.NetworkCount; net++)
                {
                    if (!k.TryGetNetworkStats(net, out BeltNetworkStats st) || st.Cells <= 0)
                    {
                        continue;
                    }
                    shown++;
                    lines.Add((GameText.Format("stats.flow.belt_row", shown, N(st.DeliveredPerMinute), N(st.EmittedPerMinute), N(st.WindowSeconds), st.Items, st.BlockedCells, st.Cells),
                        st.BlockedCells > 0 ? "st-row-warn" : null));
                }
            }
            if (shown == 0)
            {
                lines.Add((GameText.Get("stats.flow.no_belt"), "st-row-dim"));
            }
            BeltItemState b = state.Belts;
            if (b != null && (b.ClearedToStorage > 0 || b.Discarded > 0 || shown > 0))
            {
                lines.Add((GameText.Format("stats.flow.belt_clear", b.ClearedToStorage, b.Discarded), null));
            }
            Splitters.Clear();
            k?.CollectSplitters(Splitters);
            if (Splitters.Count > 0)
            {
                lines.Add((GameText.Get("stats.flow.splitter_title"), "st-row-title"));
                foreach (int2 c in Splitters)
                {
                    if (k.TryGetNodeInfo(c.x, c.y, out BeltNodeInfo info))
                    {
                        lines.Add((GameText.Format("stats.flow.splitter_row", c.x.ToString(CultureInfo.InvariantCulture), c.y.ToString(CultureInfo.InvariantCulture),
                            info.SentL, info.SentR, N(info.WindowSeconds), info.SentLInWindow, info.SentRInWindow), "st-row-sub"));
                    }
                }
            }
        }

        private static void AppendPipes(CampaignState state, List<(string Text, string Cls)> lines)
        {
            lines.Add((GameText.Get("stats.flow.pipe_title"), "st-row-title"));
            PipeKernel k = PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state) ? PipeNetworkService.Kernel : null;
            long pumped = k?.TotalPumpedMl ?? state.Pipes?.TotalPumpedMl ?? 0;
            long delivered = k?.TotalDeliveredMl ?? state.Pipes?.TotalDeliveredMl ?? 0;
            long flushed = k?.TotalFlushedMl ?? state.Pipes?.TotalFlushedMl ?? 0;
            long removed = k?.TotalRemovedMl ?? state.Pipes?.TotalRemovedMl ?? 0;
            if (pumped + delivered + flushed + removed == 0 && (k == null || k.NetworkCount == 0))
            {
                lines.Add((GameText.Get("stats.flow.no_pipe"), "st-row-dim"));
                return;
            }
            lines.Add((GameText.Format("stats.flow.pipe_row", N(pumped / 1000f), N(delivered / 1000f), N(flushed / 1000f), N(removed / 1000f)), null));
        }

        private static string N(float v) => ProductionStats.UiNumber(v);
    }
}
