using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG8-LOOT-03（FG-GAP-104）：地上的战利品的地面外观（占位几何体，美术阶段整体替换，DEBT-FG8LOOT03-01）——
    /// 地上的三选一数据包（青色扁方块）、地上的黑匣子（深灰方块 + 橙色顶灯）；远征地点里另画地面物（那里 Demo 没有地面物外观）。
    /// 家园星球的地面物（含阵亡 / 丢弃掉落的货）沿用 <c>HomeValleyController</c> 自己的地面物外观，不重复画。
    /// 只在数据包 / 黑匣子 / 地面物版本变了时对账（O(数据包 + 关键物 + 本地点地面物)），按 ID 缓存表现对象，不每帧扫。
    /// </summary>
    public sealed class GroundLootVisuals
    {
        private readonly Dictionary<string, Transform> _views = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly HashSet<string> _live = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _gone = new List<string>();
        private int _key = int.MinValue;

        public int Count => _views.Count;

        public bool Has(string id) => _views.TryGetValue(id, out Transform t) && t != null;

        public static string PacketId(LootPackageRecord p) => "LootPacket_" + p.PacketId;

        public static string BoxId(RegionQuestItemRecord q) => "LootBlackBox_" + q.SalvageInstanceId;

        public static string GroundId(GroundItemRecord g) => "LootGround_" + g.GroundItemId;

        /// <summary>按当前存档对账 <paramref name="regionId"/> 地点的地面战利品外观。<paramref name="includeGround"/> = 也画地面物（远征地点用）。</summary>
        public void Sync(CampaignState s, string regionId, Transform root, bool includeGround)
        {
            if (s == null || root == null)
            {
                return;
            }
            // 关键物数组按引用参与哈希；数组里某条记录的状态被就地改掉（如远征地点里“装载”黑匣子：OnGround → Carried）时引用不变，
            // 所以再带上本地点地上黑匣子的条数（O(关键物条数)，只有几条到几十条），装走 / 掉下都会触发重画。
            int boxesDown = 0;
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.State == RegionQuestItemState.OnGround && q.RegionId == regionId)
                {
                    boxesDown++;
                }
            }
            int key = HashCode.Combine(LootPickService.Revision, BlackBoxService.Revision, s.RegionQuestItems, s.LootPackets?.Packets, includeGround ? s.GroundItems : null, root, boxesDown);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _live.Clear();
            foreach (LootPackageRecord p in s.LootPackets?.Packets ?? Array.Empty<LootPackageRecord>())
            {
                if (p == null || p.Holder > 0 || (string.IsNullOrEmpty(p.RegionId) ? Campaign.Regions.HomeValleyLayout.RegionId : p.RegionId) != regionId)
                {
                    continue;
                }
                Ensure(PacketId(p), root, new Vector3(p.X, 0.3f, p.Y), PrimitiveType.Cube, new Vector3(0.7f, 0.35f, 0.7f), new Color(0.35f, 0.85f, 0.9f));
            }
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q == null || q.State != RegionQuestItemState.OnGround || q.RegionId != regionId || !BlackBoxService.IsBlackBox(q))
                {
                    continue;
                }
                string id = BoxId(q);
                if (Ensure(id, root, new Vector3(q.Position.x, 0.3f, q.Position.y), PrimitiveType.Cube, new Vector3(0.6f, 0.6f, 0.6f), new Color(0.18f, 0.18f, 0.2f)))
                {
                    GameObject beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    beacon.name = id + "_Beacon";
                    beacon.transform.SetParent(_views[id], false);
                    beacon.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                    beacon.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                    beacon.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(new Color(1f, 0.55f, 0.15f));
                    UnityObjects.Release(beacon.GetComponent<Collider>());
                }
            }
            if (includeGround)
            {
                foreach (GroundItemRecord g in s.GroundItems ?? Array.Empty<GroundItemRecord>())
                {
                    if (g != null && g.RegionId == regionId && g.Amount > 0)
                    {
                        Ensure(GroundId(g), root, new Vector3(g.Position.x, 0.3f, g.Position.y), PrimitiveType.Cube, new Vector3(0.8f, 0.6f, 0.8f), new Color(0.85f, 0.75f, 0.35f));
                    }
                }
            }
            _gone.Clear();
            foreach (KeyValuePair<string, Transform> kv in _views)
            {
                if (!_live.Contains(kv.Key))
                {
                    _gone.Add(kv.Key);
                }
            }
            foreach (string id in _gone)
            {
                if (_views.TryGetValue(id, out Transform t) && t != null)
                {
                    UnityObjects.Release(t.gameObject);
                }
                _views.Remove(id);
            }
            _gone.Clear();
        }

        /// <summary>有就留着（位置跟着记录走），没有就建一个；返回是不是新建的。</summary>
        private bool Ensure(string id, Transform root, Vector3 at, PrimitiveType shape, Vector3 scale, Color color)
        {
            _live.Add(id);
            if (_views.TryGetValue(id, out Transform t) && t != null)
            {
                t.position = at;
                return false;
            }
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = id;
            go.transform.SetParent(root, false);
            go.transform.position = at;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(color);
            UnityObjects.Release(go.GetComponent<Collider>());
            _views[id] = go.transform;
            return true;
        }

        /// <summary>地点卸载时释放全部外观。</summary>
        public void Clear()
        {
            foreach (KeyValuePair<string, Transform> kv in _views)
            {
                if (kv.Value != null)
                {
                    UnityObjects.Release(kv.Value.gameObject);
                }
            }
            _views.Clear();
            _key = int.MinValue;
        }
    }
}
