using System;
using GameLogic.Campaign.Grid;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-014）：维修无人机站的调参（drone.*，fg.TbHomeTuning；数据源 tools/cell_tables/fgdata_repair.py）。只读；表里没有的键按初值（与表一致），不抛异常。
    /// </summary>
    public static class RepairDroneCatalog
    {
        public const string StationTypeId = "repair_drone_station";

        public static bool IsStationType(string typeId) => typeId == StationTypeId;

        public static int Count => Math.Max(1, Math.Min(16, (int)Math.Round(T("drone.count", 3f))));
        public static float Range => Math.Max(1f, T("drone.range", 16f));
        public static float Speed => Math.Max(0.1f, T("drone.speed", 7f));
        public static float RepairPerSecond => Math.Max(0.01f, T("drone.repair_per_second", 10f));
        public static float Hp => Math.Max(1f, T("drone.hp", 30f));
        public static float Radius => Mathf.Clamp(T("drone.radius", 0.4f), 0.1f, 2f);
        public static float Reach => Math.Max(0.1f, T("drone.reach", 1.2f));
        public static float RespawnSeconds => Math.Max(0.1f, T("drone.respawn_seconds", 20f));
        public static int RespawnScrap => Math.Max(0, (int)Math.Round(T("drone.respawn_scrap", 6f)));
        public static float ScanSeconds => Math.Max(0.05f, T("drone.scan_seconds", 1f));
        public static float AttackedWindowSeconds => Math.Max(0.1f, T("drone.attacked_window_seconds", 8f));
        public static float BeltRepairKits => Math.Max(0f, T("drone.belt_repair_kits", 0.25f));
        public static float NotifyCooldownSeconds => Math.Max(1f, T("drone.notify.cooldown_seconds", 30f));

        private static float T(string key, float def) => GridContent.TryGetTuning(key, out float v) ? v : def;
    }
}
