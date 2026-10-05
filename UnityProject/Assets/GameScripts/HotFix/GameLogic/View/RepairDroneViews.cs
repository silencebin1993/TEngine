using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-014 / 015）：维修无人机与重建区域的画面（纯表现，只在家园被观察时由 <see cref="HomeValleyController"/> 每帧调用；读存档与内核，不改任何模拟数据）。
    /// - 无人机本体是战斗内核里的实例化单位（<see cref="BinGames.Sim.Combat.CombatRenderer"/> 画成己方圆片 + 血量环，位置按步插值）；这里只画修理光束（修理中的无人机 → 目标）。
    /// - 覆盖范围：建造模式选中维修无人机站时在鼠标处画范围圈；打开某座站的建筑面板时画它的范围圈。
    /// - 重建区域：建造模式打开时（含重建区域模式）、或常驻规则面板正在编辑一条自动重建规则时，地面画出区域边框——开着的绿色粗线、关着的灰色细线（颜色之外线宽不同，B15）。
    /// 开销：光束 / 区域边框只在结构键（服务版本号）变化时重画，平时每帧一次比较（热更层每帧 O(1)；修理中的无人机悬停不动，光束不需要逐帧更新）。占位表现（B22），在 DEBT 登记。
    /// </summary>
    public static class RepairDroneViews
    {
        private const float Height = 0.2f;

        private static readonly List<LineRenderer> Beams = new List<LineRenderer>(8);
        private static readonly List<LineRenderer> Zones = new List<LineRenderer>(4);
        private static readonly List<(Vector2 Pos, Vector2 Target, bool Repairing)> DroneScratch = new List<(Vector2, Vector2, bool)>(8);
        private static LineRenderer _range;
        private static Transform _root;
        private static int _beamKey;
        private static int _zoneKey;
        private static int _rangeKey;

        /// <summary>自检 / 冒烟读：画着的光束数、区域边框数（开着的）、范围圈是否显示。</summary>
        public static int BeamCount { get; private set; }
        public static int ZoneCount { get; private set; }
        public static int ZoneOnCount { get; private set; }
        public static bool RangeShown => _range != null && _range.enabled;

        public static void FrameUpdate(CampaignState state, Transform root, HomeValleyBuildMode mode)
        {
            if (state == null || root == null)
            {
                return;
            }
            if (!ReferenceEquals(root, _root))
            {
                Clear();
                _root = root;
            }
            SyncBeams(state);
            SyncZones(state, mode);
            SyncRange(state, mode);
        }

        private static void SyncBeams(CampaignState state)
        {
            int key = HashCode.Combine(RepairDroneService.Revision, 17);
            if (key == _beamKey)
            {
                return;
            }
            _beamKey = key;
            RepairDroneService.DronesForView(state, DroneScratch);
            int used = 0;
            foreach ((Vector2 pos, Vector2 target, bool repairing) in DroneScratch)
            {
                if (!repairing)
                {
                    continue;
                }
                LineRenderer lr = Line(Beams, used++, "RepairBeam", false);
                lr.widthMultiplier = 0.12f;
                lr.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.4f, 1f, 0.55f, 0.85f));
                lr.positionCount = 2;
                lr.SetPosition(0, new Vector3(pos.x, 1.2f, pos.y));
                lr.SetPosition(1, new Vector3(target.x, Height, target.y));
                lr.enabled = true;
            }
            for (int i = used; i < Beams.Count; i++)
            {
                Beams[i].enabled = false;
            }
            BeamCount = used;
        }

        private static void SyncZones(CampaignState state, HomeValleyBuildMode mode)
        {
            bool buildOpen = mode != null && mode.IsOpen;
            int editing = RulesPanelUIToolkit.IsOpen && RulesPanelUIToolkit.Instance != null ? RulesPanelUIToolkit.Instance.SelectedSerial : 0;
            int key = HashCode.Combine(StandingRuleService.Revision, buildOpen, editing, mode != null ? mode.ZoneRuleSerial : 0);
            if (key == _zoneKey)
            {
                return;
            }
            _zoneKey = key;
            int used = 0;
            int on = 0;
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(state))
            {
                if (r.Kind != StandingRuleService.KindRebuild || r.Zones == null || r.Zones.Length == 0)
                {
                    continue;
                }
                bool show = buildOpen || r.Serial == editing;
                if (!show)
                {
                    continue;
                }
                foreach (RebuildZoneRecord z in r.Zones)
                {
                    LineRenderer lr = Line(Zones, used++, "RebuildZone", true);
                    lr.widthMultiplier = z.Enabled ? 0.3f : 0.1f;
                    lr.sharedMaterial = ViewMaterials.Get("Sprites/Default", z.Enabled ? new Color(0.35f, 0.9f, 0.45f, 0.85f) : new Color(0.6f, 0.6f, 0.62f, 0.6f));
                    lr.positionCount = 4;
                    float x0 = z.X0 - 0.5f, x1 = z.X1 + 0.5f, y0 = z.Y0 - 0.5f, y1 = z.Y1 + 0.5f;
                    lr.SetPosition(0, new Vector3(x0, Height, y0));
                    lr.SetPosition(1, new Vector3(x1, Height, y0));
                    lr.SetPosition(2, new Vector3(x1, Height, y1));
                    lr.SetPosition(3, new Vector3(x0, Height, y1));
                    lr.enabled = true;
                    on += z.Enabled ? 1 : 0;
                }
            }
            for (int i = used; i < Zones.Count; i++)
            {
                Zones[i].enabled = false;
            }
            ZoneCount = used;
            ZoneOnCount = on;
        }

        private static void SyncRange(CampaignState state, HomeValleyBuildMode mode)
        {
            Vector2 center = default;
            bool show = false;
            int key = 0;
            string type = mode != null && mode.IsOpen && mode.HasHover ? mode.SelectedTypeId : null;
            if (type == RepairDroneCatalog.StationTypeId && GridContent.TryGetBuilding(type, out GameConfig.fg.BuildingGrid g))
            {
                center = GridMath.FootprintCenter(mode.HoverCell, g.FootprintW, g.FootprintH, mode.GhostRotation);
                show = true;
                key = HashCode.Combine(1, mode.HoverCell.X, mode.HoverCell.Y, mode.GhostRotation);
            }
            else if (ProductionPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.BuildingId is string id
                     && HomeGridService.FindBuilding(state, id) is BuildingRecord b && RepairDroneService.IsStation(b))
            {
                center = b.Position;
                show = true;
                key = HashCode.Combine(2, id, b.GridX, b.GridY);
            }
            if (!show)
            {
                if (_range != null)
                {
                    _range.enabled = false;
                }
                _rangeKey = 0;
                return;
            }
            if (_range == null)
            {
                _range = Line(null, 0, "RepairRange", true);
                _range.widthMultiplier = 0.22f;
                _range.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.4f, 1f, 0.55f, 0.8f));
                _rangeKey = 0;
            }
            if (key != _rangeKey)
            {
                _rangeKey = key;
                int n = DefenseCatalog.ShieldRingSegments;
                float radius = RepairDroneCatalog.Range;
                _range.positionCount = n;
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    _range.SetPosition(i, new Vector3(center.x + Mathf.Cos(a) * radius, Height, center.y + Mathf.Sin(a) * radius));
                }
            }
            _range.enabled = true;
        }

        private static LineRenderer Line(List<LineRenderer> pool, int index, string name, bool loop)
        {
            if (pool != null && index < pool.Count)
            {
                return pool[index];
            }
            var go = new GameObject(name + (pool?.Count ?? 0));
            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            pool?.Add(lr);
            return lr;
        }

        /// <summary>离开家园 / 自检收尾：线随 root 销毁（这里清登记；材质是共享的 ViewMaterials）。</summary>
        public static void Clear()
        {
            foreach (LineRenderer lr in Beams)
            {
                if (lr != null)
                {
                    UnityObjects.Release(lr.gameObject);
                }
            }
            foreach (LineRenderer lr in Zones)
            {
                if (lr != null)
                {
                    UnityObjects.Release(lr.gameObject);
                }
            }
            Beams.Clear();
            Zones.Clear();
            if (_range != null)
            {
                UnityObjects.Release(_range.gameObject);
            }
            _range = null;
            _root = null;
            _beamKey = 0;
            _zoneKey = 0;
            _rangeKey = 0;
            BeamCount = 0;
            ZoneCount = 0;
            ZoneOnCount = 0;
        }
    }
}
