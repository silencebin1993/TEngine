using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG3-GEN-01（FG17 第 4 节“战略地图支持玩家自定义标记和备注”、第 6 节“存档：玩家标记”）：战略地图上的玩家标记。
    /// 唯一写入口：添加 / 改备注 / 删除；状态在 <see cref="WorldGenState.MapMarkers"/>（进存档）。数量上限 map.marker_max，超出时如实拒绝。
    /// 每次操作 O(标记数)（上限 64），只在玩家点击时发生。
    /// </summary>
    public static class WorldMapMarkers
    {
        public const int NoteMaxLength = 80;

        public static int Revision { get; private set; } = 1;

        public static int Limit => Math.Max(1, (int)Math.Round(GridContent.Tuning("map.marker_max")));

        public static IReadOnlyList<MapMarkerRecord> All(CampaignState state) =>
            (IReadOnlyList<MapMarkerRecord>)state?.World?.MapMarkers ?? Array.Empty<MapMarkerRecord>();

        public static MapMarkerRecord Find(CampaignState state, string markerId)
        {
            foreach (MapMarkerRecord m in All(state))
            {
                if (m != null && m.MarkerId == markerId)
                {
                    return m;
                }
            }
            return null;
        }

        /// <summary>在 <paramref name="surfaceId"/> 的格 (x, y) 加一个标记；到上限返回 null。</summary>
        public static MapMarkerRecord Add(CampaignState state, string surfaceId, int x, int y)
        {
            if (state == null)
            {
                return null;
            }
            CampaignFgStateDomains.EnsureAll(state);
            WorldGenState w = state.World;
            if (w.MapMarkers.Length >= Limit)
            {
                return null;
            }
            int serial = Math.Max(1, w.NextMapMarkerSerial);
            w.NextMapMarkerSerial = serial + 1;
            var m = new MapMarkerRecord
            {
                MarkerId = "marker-" + serial,
                SurfaceId = surfaceId ?? WorldGenContent.EarthSurfaceId,
                CellX = x,
                CellY = y,
                Serial = serial,
                Note = string.Empty,
            };
            var list = new List<MapMarkerRecord>(w.MapMarkers) { m };
            w.MapMarkers = list.ToArray();
            Revision++;
            return m;
        }

        /// <summary>备注的规范形式（去掉首尾空白、截到 <see cref="NoteMaxLength"/> 个字符）。存档里只存规范形式；
        /// 输入框正在输入的文字（例如末尾刚打的空格）与存档的规范形式相同时，界面不要回写输入框，否则多词备注打不出空格。</summary>
        public static string NormalizeNote(string note)
        {
            string n = (note ?? string.Empty).Trim();
            if (n.Length > NoteMaxLength)
            {
                n = n.Substring(0, NoteMaxLength).TrimEnd();
            }
            return n;
        }

        /// <summary>改备注（存规范形式，见 <see cref="NormalizeNote"/>）。</summary>
        public static bool SetNote(CampaignState state, string markerId, string note)
        {
            MapMarkerRecord m = Find(state, markerId);
            if (m == null)
            {
                return false;
            }
            string n = NormalizeNote(note);
            if (m.Note == n)
            {
                return true;
            }
            m.Note = n;
            Revision++;
            return true;
        }

        public static bool Remove(CampaignState state, string markerId)
        {
            if (state?.World?.MapMarkers == null)
            {
                return false;
            }
            var list = new List<MapMarkerRecord>(state.World.MapMarkers.Length);
            bool removed = false;
            foreach (MapMarkerRecord m in state.World.MapMarkers)
            {
                if (m != null && m.MarkerId == markerId)
                {
                    removed = true;
                    continue;
                }
                list.Add(m);
            }
            if (removed)
            {
                state.World.MapMarkers = list.ToArray();
                Revision++;
            }
            return removed;
        }
    }
}
