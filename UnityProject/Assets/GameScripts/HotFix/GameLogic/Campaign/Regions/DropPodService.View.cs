using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    public static partial class DropPodService
    {
        // ─────────────────────────────── 读数（面板 / 状态行 / 地图读同一份）───────────────────────────────

        /// <summary>建筑状态行（建筑面板 / 悬停）：待命（舱 在站 / 冷却 / 在飞，燃油）/ 没有投送舱 / 没有燃油。缺电 / 禁用 / 虚影 / 被毁由通用状态先报。</summary>
        public static BuildingStatus StatusOf(CampaignState state, BuildingRecord b)
        {
            DropStationRecord st = FindStation(state, b?.BuildingId);
            if (st == null)
            {
                // 刚建成、还没到下一次对账（drop.sync_seconds 内）：站里还没有舱，照实写。
                return new BuildingStatus(BuildingStatusKind.NoMaterial, "drop.no_pods", GameText.Get("bs.reason.drop_no_pods"));
            }
            string summary = SummaryText(state, st, b);
            if (PodCount(state, st.BuildingId) == 0)
            {
                return new BuildingStatus(BuildingStatusKind.NoMaterial, "drop.no_pods", GameText.Get("bs.reason.drop_no_pods") + "\n" + summary);
            }
            if (FuelMl(st) <= 0)
            {
                string why = WrongFluidNear(b) ? GameText.Get("bs.reason.drop_wrong_fluid") : GameText.Get("bs.reason.drop_no_fuel");
                return new BuildingStatus(BuildingStatusKind.NoFluid, "drop.no_fuel", why + "\n" + summary);
            }
            int flying = CountState(state, st.BuildingId, StateOutbound) + CountState(state, st.BuildingId, StateReturning);
            return new BuildingStatus(flying > 0 ? BuildingStatusKind.Working : BuildingStatusKind.Idle, flying > 0 ? "drop.flying" : "drop.idle", summary);
        }

        /// <summary>“投送舱 在站 2 · 冷却 1 · 在飞 1 / 容纳 4；燃油 340 / 1200 升；投送距离 3000 格”。</summary>
        public static string SummaryText(CampaignState state, DropStationRecord st, BuildingRecord b)
        {
            int docked = CountState(state, st.BuildingId, StateDocked);
            int cooling = CountState(state, st.BuildingId, StateCooling);
            int flying = CountState(state, st.BuildingId, StateOutbound) + CountState(state, st.BuildingId, StateReturning);
            string line = GameText.Format("drop.summary.pods", docked, cooling, flying, Capacity(b));
            line += "\n" + GameText.Format("drop.summary.fuel", Liters(FuelMl(st)), Mathf.RoundToInt(TankLiters), Mathf.RoundToInt(RangeCells));
            double soon = SoonestReadySeconds(state, st.BuildingId);
            if (cooling > 0 && soon >= 0)
            {
                line += "\n" + GameText.Format("drop.summary.cooling", DropPodService.Duration(soon));
            }
            int stock = HomeInventory.Stock(state, PodItemId);
            line += "\n" + GameText.Format(st.AutoFill ? "drop.summary.autofill_on" : "drop.summary.autofill_off", stock);
            return line;
        }

        /// <summary>在飞 / 冷却中的舱，每舱一行（面板“在飞的投送舱”）。</summary>
        public static void PodLines(CampaignState state, string stationId, List<string> into)
        {
            into.Clear();
            long now = GameClock.Ticks;
            var pods = new List<DropPodRecord>();
            foreach (DropPodRecord p in Pods(state))
            {
                if (p.StationId == stationId && p.State != StateDocked)
                {
                    pods.Add(p);
                }
            }
            pods.Sort((a, b) => a.Serial.CompareTo(b.Serial));
            foreach (DropPodRecord p in pods)
            {
                into.Add(PodLine(state, p, now));
            }
        }

        public static string PodLine(CampaignState state, DropPodRecord p, long now)
        {
            string load = LoadText(p);
            switch (p.State)
            {
                case StateCooling:
                    return GameText.Format("drop.pod.cooling", p.Serial, DropPodService.Duration(Math.Max(0, p.CooldownUntilTick - now) / (double)Math.Max(1, GameClock.StepHz)));
                case StateOutbound:
                    return GameText.Format(p.Mission == MissionRecall ? "drop.pod.outbound_recall" : "drop.pod.outbound", p.Serial, load, Coord(p.TargetX, p.TargetY), DropPodService.Duration(SecondsLeft(p, now)));
                case StateReturning:
                    string line = GameText.Format("drop.pod.returning", p.Serial, load, DropPodService.Duration(SecondsLeft(p, now)));
                    if (!string.IsNullOrEmpty(p.Note))
                    {
                        line += GameText.Get("drop.note." + p.Note.Replace('-', '_'));
                    }
                    return line;
                default:
                    return GameText.Format("drop.pod.docked", p.Serial);
            }
        }

        public static string LoadText(DropPodRecord p)
        {
            if (p.MachineId > 0)
            {
                return MachineNaming.Short(p.MachineId);
            }
            if (p.CargoAmount > 0 && ItemCatalog.TryGet(p.CargoItem, out ItemDef d))
            {
                return GameText.Format("drop.load.cargo", GameText.Get(d.NameKey), p.CargoAmount);
            }
            return GameText.Get("drop.load.empty");
        }

        /// <summary>投送的时长（1x 下的游戏秒：飞行 60 格/秒、着陆 5 秒、冷却 60 秒都按秒给）——“13 秒”“1 分 20 秒”。</summary>
        public static string Duration(double seconds)
        {
            int s = Math.Max(0, (int)Math.Ceiling(seconds - 1e-6));
            return s < 60 ? GameText.Format("drop.time.seconds", s) : GameText.Format("drop.time.minutes", s / 60, s % 60);
        }

        public static string Coord(float x, float y) => "(" + Mathf.RoundToInt(x).ToString(CultureInfo.InvariantCulture) + ", " + Mathf.RoundToInt(y).ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>预览的一段话（面板与地图悬停读同一份）：距离 · 单程飞行（含着陆）· 燃油（往返）· 舱；被拒时第一行是原因。</summary>
        public static string PreviewText(DropPreview p)
        {
            if (p == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            if (!p.Ok)
            {
                sb.Append(GameText.Format("drop.preview.rejected", p.Reason));
            }
            if (p.Distance > 0f || p.Ok)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(GameText.Format("drop.preview.line", Mathf.RoundToInt(p.Distance), DropPodService.Duration(p.LegSeconds), Mathf.RoundToInt(LandingSeconds),
                    Liters(p.FuelMl > 0 ? p.FuelMl : FuelPerPodMl(p.Distance) * Math.Max(1, p.PodsNeeded)), Math.Max(1, p.PodsNeeded), p.PodsReady, p.PodsTotal));
            }
            if (p.Recall && p.Ok)
            {
                sb.Append('\n').Append(GameText.Format("drop.preview.recall", p.Machines.Count, p.GroundCargo));
                if (p.Leftover > 0)
                {
                    sb.Append('\n').Append(GameText.Format("drop.preview.leftover", p.Leftover));
                }
            }
            if (p.BusyMachines > 0)
            {
                sb.Append('\n').Append(GameText.Format("drop.preview.busy", p.BusyMachines));
            }
            if (p.AntiAir)
            {
                sb.Append('\n').Append(GameText.Get("drop.preview.antiair_hint"));
            }
            return sb.ToString();
        }

        /// <summary>世界画面读：在飞的舱（位置、终点、剩余游戏秒）。不生成标签、一次遍历，每帧无字符串分配。<paramref name="into"/> 先清空。O(舱数)。</summary>
        public static void FlyingForWorld(CampaignState state, List<(Vector2 Pos, Vector2 To, double SecondsLeft)> into)
        {
            into.Clear();
            long now = GameClock.Ticks;
            foreach (DropPodRecord p in Pods(state))
            {
                if (p.State == StateOutbound || p.State == StateReturning)
                {
                    into.Add((PositionOf(state, p, now), new Vector2(p.ToX, p.ToY), SecondsLeft(p, now)));
                }
            }
        }

        /// <summary>画面 / 地图读：在飞的舱（位置、终点、是否返程、标签）。<paramref name="into"/> 先清空。O(舱数)。</summary>
        public static void FlyingForView(CampaignState state, List<(int Serial, Vector2 Pos, Vector2 To, bool Returning, string Label)> into)
        {
            into.Clear();
            long now = GameClock.Ticks;
            foreach (DropPodRecord p in Pods(state))
            {
                if (p.State == StateOutbound || p.State == StateReturning)
                {
                    into.Add((p.Serial, PositionOf(state, p, now), new Vector2(p.ToX, p.ToY), p.State == StateReturning,
                        GameText.Format("drop.map.pod", p.Serial, LoadText(p), DropPodService.Duration(SecondsLeft(p, now)))));
                }
            }
        }
    }
}
