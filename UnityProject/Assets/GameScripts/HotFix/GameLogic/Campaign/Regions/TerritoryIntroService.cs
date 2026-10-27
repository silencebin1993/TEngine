using System;
using System.Collections.Generic;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG10-REG-01（卡片“必须同时交付：第一次进入时的阵营介绍”）：信号所在的机器第一次走进一个有阵营介绍文本
    /// （world.territory.&lt;领地&gt;.faction_intro）的阵营领地时，发一条阵营介绍通知——是谁、怎么打、门槛与应对。每个领地一次，进存档
    /// （<see cref="CampaignFactionState.TerritoriesIntroduced"/>）。进入领地的顶部名称提示（WorldPlacesHud）与行军提示照旧，这条是能回看的那一份。
    /// 判定按规划层（TerritoryAt），与镜头和是否被观察无关（FGR-BASE-021）；每游戏秒一次 O(1)。
    /// </summary>
    public static class TerritoryIntroService
    {
        public const string NotifyType = "territory_intro";

        /// <summary>本次运行发出的介绍条数（自检用）。</summary>
        public static int Posted { get; private set; }

        public static void ResetForTests() => Posted = 0;

        public static string IntroKey(string territoryId) => "world.territory." + territoryId + ".faction_intro";

        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            int id = Signal.SignalUplinkService.CurrentMachine(s);
            if (id == 0 || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || m.RegionId != HomeValleyLayout.RegionId)
            {
                return;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
            TryIntroduce(s, pos);
        }

        /// <summary>机器在 <paramref name="pos"/>：在一个还没介绍过、有介绍文本的阵营领地里 → 发介绍并记下。返回是否发了。</summary>
        public static bool TryIntroduce(CampaignState s, Vector2 pos)
        {
            PlannedTerritory t = WorldGenService.PlanFor(s)?.TerritoryAt(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
            if (t == null || !t.IsFaction)
            {
                return false;
            }
            string key = IntroKey(t.Id);
            if (!GameText.Has(key))
            {
                return false;
            }
            string[] done = s.Factions?.TerritoriesIntroduced;
            if (done == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
                done = s.Factions.TerritoriesIntroduced;
            }
            if (Array.IndexOf(done, t.Id) >= 0)
            {
                return false;
            }
            var list = new List<string>(done) { t.Id };
            list.Sort(StringComparer.Ordinal);
            s.Factions.TerritoriesIntroduced = list.ToArray();
            Posted++;
            NotificationCenter.Post(NotifyType, GameText.Get(key), new Vector3(pos.x, 0f, pos.y));
            return true;
        }
    }
}
