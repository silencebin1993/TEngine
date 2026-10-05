using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-002“放置时预览射程范围”；DEBT-FG2VFX02-03 炮塔的机身状态表现）：炮塔的画面（纯表现，只在家园被观察时由
    /// <see cref="HomeValleyController"/> 每帧调用；读内核状态与编译结果，不改任何模拟数据）。
    /// - 射程圈：建造模式选中炮塔座时，在鼠标指着的位置画出（按建造栏选的蓝图的主组件射程）；打开某座炮塔的面板 / 建筑面板时，画出它现在的射程（含等级倍率）。
    /// - 炮塔头：每座在内核里的炮塔一个占位炮塔头（圆柱 + 炮管，B22 占位：节点名带 [placeholder]），按内核里的炮口朝向转动（转速由组件决定）；
    ///   炮塔头上挂机器同一套形变部件（<see cref="MachineMorphView.BuildRig"/>：底盘件 + 主组件件 + 功能组件件），按生效固件的类别展开（机身状态 = 编译结果）。
    /// 开销（FG6-DEF-01 审查修复，架构红线“热更层每帧 O(1)”）：
    /// - 炮塔头的位置与朝向绑进地点的画面同步（<see cref="CombatSite.BindOrientedView"/>）：每帧由 <see cref="CombatSite.FrameRender"/> 的一次 Burst 作业写入，热更层不逐炮塔写 Transform；
    /// - 炮塔头的增删、换绑与形变部件只在“结构键”变了时对账（炮塔服务 / 信号核 / 固件类别 / 炮塔表的版本号、内核里的炮塔数、画面绑定数），平时每帧一次比较；
    /// - 放置预览的射程半径按（炮塔座种类、建造栏选的蓝图、炮塔服务版本号、蓝图库……）缓存，键不变时最多每 <see cref="RingRecheckFrames"/> 帧复核一次（蓝图版本原位更新的兜底），不再每帧编译两次电路。
    /// </summary>
    public static class TurretViews
    {
        public const string HeadPrefix = "TurretHead_";
        private const float HeadHeight = 1.7f;
        private const float RingHeight = 0.12f;
        private const int RingRecheckFrames = 60;

        private sealed class Head
        {
            public Transform Root;
            public Transform Rig;
            public readonly Transform[] Groups = new Transform[3];
            public string Primary;
            public string Utility;
            public MorphMask Mask;
            public int Parts;
            public int Unit;
            public int Stamp;
        }

        private static readonly Dictionary<string, Head> Heads = new Dictionary<string, Head>();
        private static readonly List<string> Scratch = new List<string>(8);
        private static LineRenderer _ring;
        private static Vector2 _ringCenter;
        private static float _ringRadius = -1f;
        private static CombatSite _site;
        private static Transform _root;
        private static int _structKey;
        private static bool _structValid;
        private static int _stamp;
        private static int _placementKey;
        private static int _placementFrame = int.MinValue;
        private static float _placementRadius;

        /// <summary>自检 / 冒烟读：射程圈此刻是否显示、圆心与半径。</summary>
        public static bool RingShown => _ring != null && _ring.enabled;
        public static Vector2 RingCenter => _ringCenter;
        public static float RingRadius => _ringRadius;
        public static int HeadCount => Heads.Count;

        /// <summary>自检读：炮塔头结构对账的次数（键不变的帧不对账）、放置预览半径真正重算（编译蓝图）的次数。</summary>
        public static int ResyncCount { get; private set; }
        public static int PlacementRangeComputes { get; private set; }

        public static bool TryGetHead(string buildingId, out Transform head)
        {
            head = Heads.TryGetValue(buildingId ?? string.Empty, out Head h) ? h.Root : null;
            return head != null;
        }

        /// <summary>自检读：这座炮塔的炮塔头绑在内核的哪个单位上（0 = 没绑）。</summary>
        public static int BoundUnitOf(string buildingId) => Heads.TryGetValue(buildingId ?? string.Empty, out Head h) ? h.Unit : 0;

        public static MorphMask MaskOf(string buildingId) => Heads.TryGetValue(buildingId ?? string.Empty, out Head h) ? h.Mask : MorphMask.None;

        public static int PartCountOf(string buildingId) => Heads.TryGetValue(buildingId ?? string.Empty, out Head h) ? h.Parts : 0;

        public static void FrameUpdate(CampaignState state, CombatSite site, Transform root, HomeValleyBuildMode mode)
        {
            if (state == null || root == null)
            {
                return;
            }
            UpdateRing(state, mode);
            SyncHeads(state, site, root);
        }

        private static void UpdateRing(CampaignState state, HomeValleyBuildMode mode)
        {
            Vector2 center = default;
            float radius = 0f;
            if (mode != null && mode.IsOpen && TurretCatalog.IsTurretType(mode.SelectedTypeId) && mode.HasHover
                && GridContent.TryGetBuilding(mode.SelectedTypeId, out GameConfig.fg.BuildingGrid g))
            {
                radius = PlacementRadius(state, mode.SelectedTypeId);
                center = GridMath.FootprintCenter(mode.HoverCell, g.FootprintW, g.FootprintH, mode.GhostRotation);
            }
            else
            {
                string id = TurretPanelUIToolkit.IsOpen ? TurretPanelUIToolkit.BuildingId
                    : ProductionPanelUIToolkit.IsOpen ? ProductionPanelUIToolkit.BuildingId : null;
                BuildingRecord b = id != null ? HomeGridService.FindBuilding(state, id) : null;
                if (b != null && TurretCatalog.IsTurretType(b.BuildingTypeId))
                {
                    radius = TurretService.RangeOfTurret(state, id); // O(1)：记录按索引查，编译结果按输入缓存
                    center = b.Position;
                }
            }
            if (radius <= 0f)
            {
                if (_ring != null)
                {
                    _ring.enabled = false;
                }
                return;
            }
            if (_ring == null)
            {
                var go = new GameObject("TurretRangeRing");
                _ring = go.AddComponent<LineRenderer>();
                _ring.useWorldSpace = true;
                _ring.loop = true;
                _ring.widthMultiplier = 0.25f;
                _ring.numCapVertices = 0;
                _ring.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 平铺在地面上（与信号覆盖预警圈同一做法）
                _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _ring.receiveShadows = false;
                _ring.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.47f, 0.86f, 0.94f, 0.85f));
                _ringRadius = -1f;
            }
            if (!Mathf.Approximately(radius, _ringRadius) || (center - _ringCenter).sqrMagnitude > 1e-4f)
            {
                _ringCenter = center;
                _ringRadius = radius;
                int n = TurretCatalog.RingSegments;
                _ring.positionCount = n;
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    _ring.SetPosition(i, new Vector3(center.x + Mathf.Cos(a) * radius, RingHeight, center.y + Mathf.Sin(a) * radius));
                }
            }
            _ring.enabled = true;
        }

        /// <summary>放置预览的射程半径（建造栏选的 / 默认的蓝图的主组件射程）。按输入缓存：键不变时最多每 <see cref="RingRecheckFrames"/> 帧复核一次。</summary>
        private static float PlacementRadius(CampaignState state, string typeId)
        {
            TurretState st = TurretService.StateOf(state);
            string size = TurretCatalog.SizeOfType(typeId);
            string pick = st == null ? null : (size == TurretCatalog.SizeHeavy ? st.PlacementHeavy : st.PlacementLight);
            BlueprintRecord[] library = state.BlueprintRecords;
            int key = HashCode.Combine(size, pick, TurretService.Revision, FirmwareKinds.Revision, TurretCatalog.Revision,
                library != null ? RuntimeHelpers.GetHashCode(library) : 0, library?.Length ?? 0, RuntimeHelpers.GetHashCode(state));
            int frame = Time.frameCount;
            if (key == _placementKey && _placementFrame != int.MinValue && frame - _placementFrame < RingRecheckFrames && frame >= _placementFrame)
            {
                return _placementRadius;
            }
            string bp = TurretService.DefaultBlueprintFor(state, size);
            _placementRadius = bp != null ? TurretService.RangeOf(state, bp) : 0f;
            _placementKey = key;
            _placementFrame = frame;
            PlacementRangeComputes++;
            return _placementRadius;
        }

        private static int StructureKey(CombatSite site, Transform root) =>
            HashCode.Combine(TurretService.Revision, site != null ? site.TurretUnitCount : -1, site != null ? site.ViewBindingCount : -1,
                SignalCoreService.Revision, FirmwareKinds.Revision, TurretCatalog.Revision,
                site != null ? RuntimeHelpers.GetHashCode(site) : 0, root != null ? root.GetInstanceID() : 0);

        private static void SyncHeads(CampaignState state, CombatSite site, Transform root)
        {
            if (site != null && site.IsDisposed)
            {
                site = null;
            }
            bool siteChanged = !ReferenceEquals(site, _site) || root != _root;
            if (_structValid && !siteChanged && StructureKey(site, root) == _structKey)
            {
                return; // 结构没变：位置 / 朝向由地点的画面同步（Burst）每帧写入，这里不逐炮塔做事
            }
            ResyncCount++;
            if (siteChanged)
            {
                ReleaseHeads();
                _site = site;
                _root = root;
            }
            _stamp++;
            if (site != null)
            {
                foreach (TurretRecord r in TurretService.All(state))
                {
                    if (r == null || !site.TryGetTurretUnit(r.Serial, out int unit) || !site.TryGetTurretState(r.Serial, out TurretUnitState us) || !us.Alive)
                    {
                        continue;
                    }
                    if (!Heads.TryGetValue(r.BuildingId, out Head h) || h.Root == null)
                    {
                        h = CreateHead(root, r.BuildingId);
                        Heads[r.BuildingId] = h;
                    }
                    h.Stamp = _stamp;
                    if (h.Unit != unit)
                    {
                        if (h.Unit > 0)
                        {
                            site.UnbindView(h.Unit);
                        }
                        // 先摆到当前位置 / 朝向（本帧画面同步之前不在原点闪一下），之后每帧由画面同步写入。
                        h.Root.position = new Vector3(us.Position.x, HeadHeight, us.Position.y);
                        if (us.Facing.sqrMagnitude > 1e-6f)
                        {
                            h.Root.rotation = Quaternion.LookRotation(new Vector3(us.Facing.x, 0f, us.Facing.y), Vector3.up);
                        }
                        site.BindOrientedView(unit, h.Root, HeadHeight);
                        h.Unit = unit;
                    }
                    SyncMorph(state, r, h);
                }
            }
            Scratch.Clear();
            foreach (KeyValuePair<string, Head> kv in Heads)
            {
                if (kv.Value.Stamp != _stamp || kv.Value.Root == null)
                {
                    Scratch.Add(kv.Key);
                }
            }
            foreach (string gone in Scratch)
            {
                if (Heads.TryGetValue(gone, out Head h))
                {
                    if (h.Unit > 0 && site != null)
                    {
                        site.UnbindView(h.Unit);
                    }
                    if (h.Root != null)
                    {
                        UnityObjects.Release(h.Root.gameObject);
                    }
                }
                Heads.Remove(gone);
            }
            _structKey = StructureKey(site, root); // 换绑改了画面绑定数：按对账之后的状态记键
            _structValid = true;
        }

        private static Head CreateHead(Transform root, string buildingId)
        {
            var go = new GameObject(HeadPrefix + buildingId + MachineMorphView.PlaceholderTag);
            go.transform.SetParent(root, false);
            GameObject dome = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            dome.name = "Dome" + MachineMorphView.PlaceholderTag;
            UnityObjects.Release(dome.GetComponent<Collider>()); // 编辑模式（自检）下 Destroy 会报错
            dome.transform.SetParent(go.transform, false);
            dome.transform.localScale = new Vector3(0.9f, 0.25f, 0.9f);
            dome.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(new Color(0.55f, 0.62f, 0.68f));
            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrel.name = "Barrel" + MachineMorphView.PlaceholderTag;
            UnityObjects.Release(barrel.GetComponent<Collider>());
            barrel.transform.SetParent(go.transform, false);
            barrel.transform.localPosition = new Vector3(0f, 0.1f, 0.6f);
            barrel.transform.localScale = new Vector3(0.18f, 0.18f, 0.9f);
            barrel.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(new Color(0.3f, 0.34f, 0.38f));
            return new Head { Root = go.transform };
        }

        /// <summary>形变部件：按这座炮塔生效的编译结果（接入时含接入口插入的固件）——主 / 功能组件变了重建部件，生效固件的类别决定展开哪几组（直接到位）。</summary>
        private static void SyncMorph(CampaignState state, TurretRecord r, Head h)
        {
            if (!TurretService.TryGetMorph(state, r, out string primary, out string utility, out MorphMask mask))
            {
                primary = null;
                utility = null;
                mask = MorphMask.None;
            }
            if (h.Rig == null || primary != h.Primary || utility != h.Utility)
            {
                if (h.Rig != null)
                {
                    UnityObjects.Release(h.Rig.gameObject);
                }
                h.Primary = primary;
                h.Utility = utility;
                h.Rig = MachineMorphView.BuildRig(h.Root, primary, utility, out int parts, h.Groups);
                h.Parts = parts;
                h.Mask = (MorphMask)255; // 强制下面按新部件重新展开
            }
            if (h.Mask == mask)
            {
                return;
            }
            h.Mask = mask;
            for (int i = 0; i < 3; i++)
            {
                if (h.Groups[i] != null)
                {
                    bool show = (mask & MachineMorph.Categories[i]) != 0;
                    h.Groups[i].gameObject.SetActive(show);
                    h.Groups[i].localScale = Vector3.one;
                }
            }
        }

        /// <summary>解绑并销毁全部炮塔头（地点 / 根节点换了）。</summary>
        private static void ReleaseHeads()
        {
            foreach (Head h in Heads.Values)
            {
                if (h.Unit > 0 && _site != null && !_site.IsDisposed)
                {
                    _site.UnbindView(h.Unit);
                }
                if (h.Root != null)
                {
                    UnityObjects.Release(h.Root.gameObject);
                }
            }
            Heads.Clear();
        }

        /// <summary>家园表现销毁（离开观察 / 卸载）时：炮塔头随区域根节点一起销毁，这里解绑画面同步、清登记；射程圈单独释放。</summary>
        public static void Clear()
        {
            foreach (Head h in Heads.Values)
            {
                if (h.Unit > 0 && _site != null && !_site.IsDisposed)
                {
                    _site.UnbindView(h.Unit);
                }
            }
            Heads.Clear();
            _site = null;
            _root = null;
            _structValid = false;
            _placementFrame = int.MinValue;
            if (_ring != null)
            {
                UnityObjects.Release(_ring.gameObject);
                _ring = null;
            }
            _ringRadius = -1f;
        }
    }
}
