using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG6-DEF-02（FG06 FGR-DEF-012 / 013；卡片“护盾值和重启倒计时的显示”）：防御建筑的画面（纯表现，只在家园被观察时由 <see cref="HomeValleyController"/> 每帧调用；
    /// 读内核护盾与防御记录，不改任何模拟数据）。
    /// - 护盾圈：每座建成的护盾发生器在地面上画出护盾范围——展开时青色实圈、收起（充能 / 过载 / 重启 / 离线）时灰色细圈（颜色之外线宽也不同，B15）；
    ///   护盾值与倒计时写在建筑状态行 / 面板（同一份读数）。
    /// - 放置预览：建造模式选中护盾发生器时在鼠标处画护盾范围；选中陷阱发射器时画这一朝向下“一条线”的铺设范围（场地圆心连线）。打开陷阱面板时画它现在的铺设范围。
    /// 开销：圈的增删与重画只在“结构键”（防御服务版本号、内核护盾数、语言无关）变了时做，平时每帧一次比较（热更层每帧 O(1)）。占位表现（B22），在 DEBT 登记。
    /// </summary>
    public static class DefenseViews
    {
        private const float RingHeight = 0.15f;

        private static readonly List<LineRenderer> Rings = new List<LineRenderer>(4);
        private static readonly List<Vector2> Centers = new List<Vector2>(32);
        private static LineRenderer _preview;
        private static Transform _root;
        private static int _structKey;
        private static bool _structValid;
        private static int _previewKey;

        /// <summary>自检 / 冒烟读：画着的护盾圈数、展开的圈数、预览线是否显示、预览的点数。</summary>
        public static int RingCount { get; private set; }
        public static int ActiveRingCount { get; private set; }
        public static bool PreviewShown => _preview != null && _preview.enabled;
        public static int PreviewPoints => _preview != null && _preview.enabled ? _preview.positionCount : 0;

        public static void FrameUpdate(CampaignState state, CombatSite site, Transform root, HomeValleyBuildMode mode)
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
            SyncRings(state, site);
            UpdatePreview(state, mode);
        }

        private static void SyncRings(CampaignState state, CombatSite site)
        {
            int key = HashCode.Combine(DefenseService.Revision, site != null && !site.IsDisposed ? site.ShieldCount : -1, DefenseCatalog.Revision);
            if (_structValid && key == _structKey)
            {
                return;
            }
            _structKey = key;
            _structValid = true;
            int used = 0;
            int active = 0;
            if (site != null && !site.IsDisposed)
            {
                foreach (DefenseRecord r in DefenseService.All(state))
                {
                    if (r == null || !site.TryGetShield(r.Serial, out CombatShield sh))
                    {
                        continue;
                    }
                    LineRenderer lr = Ring(used++);
                    bool on = sh.Active != 0;
                    active += on ? 1 : 0;
                    lr.widthMultiplier = on ? 0.3f : 0.12f;
                    lr.sharedMaterial = ViewMaterials.Get("Sprites/Default", on ? new Color(0.35f, 0.85f, 1f, 0.8f) : new Color(0.6f, 0.6f, 0.65f, 0.6f));
                    Circle(lr, new Vector2((float)sh.Pos.x, (float)sh.Pos.y), sh.Radius);
                    lr.enabled = true;
                }
            }
            for (int i = used; i < Rings.Count; i++)
            {
                Rings[i].enabled = false;
            }
            RingCount = used;
            ActiveRingCount = active;
        }

        private static void UpdatePreview(CampaignState state, HomeValleyBuildMode mode)
        {
            Centers.Clear();
            bool ring = false;
            Vector2 center = default;
            string type = mode != null && mode.IsOpen && mode.HasHover ? mode.SelectedTypeId : null;
            int key;
            if (type == DefenseCatalog.ShieldTypeId && GridContent.TryGetBuilding(type, out GameConfig.fg.BuildingGrid g))
            {
                center = GridMath.FootprintCenter(mode.HoverCell, g.FootprintW, g.FootprintH, mode.GhostRotation);
                ring = true;
                key = HashCode.Combine(1, mode.HoverCell.X, mode.HoverCell.Y, mode.GhostRotation);
            }
            else if (type == DefenseCatalog.TrapTypeId)
            {
                var ghost = new BuildingRecord
                {
                    BuildingTypeId = type,
                    GridX = mode.HoverCell.X,
                    GridY = mode.HoverCell.Y,
                    Rotation = mode.GhostRotation,
                    Position = GridMath.FootprintCenter(mode.HoverCell, 1, 1, mode.GhostRotation),
                };
                DefenseService.FieldCenters(ghost, DefenseCatalog.PatternLine, Centers);
                key = HashCode.Combine(2, mode.HoverCell.X, mode.HoverCell.Y, mode.GhostRotation);
            }
            else
            {
                string id = DefensePanelUIToolkit.IsOpen ? DefensePanelUIToolkit.BuildingId : null;
                BuildingRecord b = id != null ? HomeGridService.FindBuilding(state, id) : null;
                DefenseRecord r = b != null ? DefenseService.Find(state, id) : null;
                if (b != null && r != null && DefenseCatalog.KindOf(b.BuildingTypeId) == DefenseKind.Trap)
                {
                    DefenseService.FieldCenters(b, r.TrapPattern, Centers);
                    key = HashCode.Combine(3, id, r.TrapPattern, b.GridX, b.GridY, b.Rotation);
                }
                else
                {
                    key = 0;
                }
            }
            if (!ring && Centers.Count == 0)
            {
                if (_preview != null)
                {
                    _preview.enabled = false;
                }
                _previewKey = 0;
                return;
            }
            if (_preview == null)
            {
                var go = new GameObject("DefensePreview");
                if (_root != null)
                {
                    go.transform.SetParent(_root, false);
                }
                _preview = go.AddComponent<LineRenderer>();
                _preview.useWorldSpace = true;
                _preview.numCapVertices = 2;
                _preview.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _preview.receiveShadows = false;
                _preview.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.95f, 0.85f, 0.3f, 0.85f));
                _previewKey = 0;
            }
            if (key != _previewKey)
            {
                _previewKey = key;
                if (ring)
                {
                    _preview.loop = true;
                    _preview.widthMultiplier = 0.25f;
                    Circle(_preview, center, DefenseCatalog.ShieldRadius);
                }
                else
                {
                    // 陷阱的铺设范围：沿场地圆心的折线（区域模式按圈的顺序连起来，看得出覆盖范围），线宽 = 场地直径的一半（占位）。
                    _preview.loop = false;
                    _preview.widthMultiplier = DefenseCatalog.TrapZoneRadius;
                    _preview.positionCount = Centers.Count;
                    for (int i = 0; i < Centers.Count; i++)
                    {
                        _preview.SetPosition(i, new Vector3(Centers[i].x, RingHeight, Centers[i].y));
                    }
                }
            }
            _preview.enabled = true;
        }

        private static LineRenderer Ring(int index)
        {
            while (Rings.Count <= index)
            {
                var go = new GameObject("ShieldRing" + Rings.Count);
                if (_root != null)
                {
                    go.transform.SetParent(_root, false);
                }
                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.loop = true;
                lr.numCapVertices = 0;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                Rings.Add(lr);
            }
            return Rings[index];
        }

        private static void Circle(LineRenderer lr, Vector2 c, float radius)
        {
            int n = DefenseCatalog.ShieldRingSegments;
            lr.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                lr.SetPosition(i, new Vector3(c.x + Mathf.Cos(a) * radius, RingHeight, c.y + Mathf.Sin(a) * radius));
            }
        }

        /// <summary>离开家园 / 自检收尾：圈与预览线随 root 销毁（这里清登记；材质是共享的 ViewMaterials，随世界释放）。</summary>
        public static void Clear()
        {
            foreach (LineRenderer lr in Rings)
            {
                if (lr != null)
                {
                    UnityObjects.Release(lr.gameObject);
                }
            }
            Rings.Clear();
            if (_preview != null)
            {
                UnityObjects.Release(_preview.gameObject);
            }
            _preview = null;
            _root = null;
            _structValid = false;
            _previewKey = 0;
            RingCount = 0;
            ActiveRingCount = 0;
        }
    }
}
