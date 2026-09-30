using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG3-LOG-02（FGR-LOG-006“虚影外观与完工建筑明显不同”“虚影显示缺什么”；FG00 B05 / B15 / B22）：施工现场的画面（纯表现，镜头在家园时才有）。
    /// - 建筑虚影：扁平的淡蓝方块随施工进度长高（<see cref="FractionOf"/>）；等材料 / 没有劳动力时头顶挂“受阻”形状标记（颜色之外有形状）。
    /// - 规划中的传送带：每格一块半透明的扁平条，沿传送带方向摆（占位表现，B22；建成后由传送带内核的实例化绘制接手）。
    ///   只在规划变化（<see cref="HomeValleyConstruction.Revision"/>）时重摆，每帧 O(1)。
    /// - 悬停：战略视角下光标停在虚影 / 规划格上时，世界悬停提示写明施工状态、缺什么、进度、优先级（与施工队列同一写法）。
    /// 施工单索引（目标 → 施工单）只在工单数组或施工修订号变化时重建，O(工单数)；其余帧与虚影的进度查询 O(1)。
    /// </summary>
    public sealed class ConstructionSiteView
    {
        private const int HoverKey = -731;

        private readonly Dictionary<string, WorkOrderRecord> _byTarget = new Dictionary<string, WorkOrderRecord>(StringComparer.Ordinal);
        private readonly List<GameObject> _beltTiles = new List<GameObject>(64);
        private GameObject _beltRoot;
        private Material _beltMaterial;
        private int _beltRevision = -1;
        private CampaignState _hoverState;
        private GridCell _hoverCell;
        private bool _hovering;
        private readonly Func<TooltipContent> _provider;

        public ConstructionSiteView()
        {
            _provider = ProvideHover;
        }

        /// <summary>当前摆出来的规划传送带格数（自检读）。</summary>
        public int ActiveBeltTiles { get; private set; }

        private WorkOrderRecord[] _indexedOrders;
        private int _indexedRevision = -1;

        /// <summary>自检用：索引实际重建的次数。</summary>
        public int IndexRebuilds { get; private set; }

        /// <summary>每帧开头：需要时重建“现场 → 未结束施工单”的索引。
        /// 工单只会追加（数组换新）或在原记录上改状态（结束的单在查询时按 <see cref="HomeValleyWorkOrders.IsActive"/> 过滤），
        /// 所以只在工单数组换了或施工修订号变了时重建（O(工单数)），其余帧 O(1)——工单历史越积越多也不会每帧全扫（审查修复）。</summary>
        public void BeginFrame(CampaignState state)
        {
            WorkOrderRecord[] orders = state?.WorkOrders;
            int revision = HomeValleyConstruction.Revision;
            if (ReferenceEquals(orders, _indexedOrders) && revision == _indexedRevision)
            {
                return;
            }
            _indexedOrders = orders;
            _indexedRevision = revision;
            IndexRebuilds++;
            _byTarget.Clear();
            foreach (WorkOrderRecord o in orders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o) && o.TargetId != null)
                {
                    _byTarget[o.TargetId] = o;
                }
            }
        }

        private bool TryActive(string targetId, out WorkOrderRecord o) =>
            _byTarget.TryGetValue(targetId, out o) && HomeValleyWorkOrders.IsActive(o);

        /// <summary>虚影的完成度（0～1，没有施工单时 0）。</summary>
        public float FractionOf(CampaignState state, string buildingId) =>
            TryActive(buildingId, out WorkOrderRecord o) ? HomeValleyConstruction.Fraction(state, o) : 0f;

        /// <summary>虚影头顶的形状标记：等材料 / 没有劳动力 / 路径受阻 = “受阻”标记；正常施工不挂。</summary>
        public string IconOf(BuildingRecord building)
        {
            if (!TryActive(building.BuildingId, out WorkOrderRecord o))
            {
                return null;
            }
            bool stalled = o.State == WorkOrderState.Waiting || (HomeValleyConstruction.NoLabor && o.State == WorkOrderState.Ready);
            return stalled ? ContentIcons.StateBlocked : null;
        }

        /// <summary>规划中的传送带：规划变化时按格重摆（池化，O(规划格数)，只在变化时）。</summary>
        public void SyncPlannedBelts(CampaignState state, Transform root)
        {
            if (root == null || state == null)
            {
                return;
            }
            if (_beltRoot == null)
            {
                _beltRoot = new GameObject("[PlannedBelts]");
                _beltRoot.transform.SetParent(root, false);
                _beltMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.45f, 0.65f, 0.95f, 0.55f) };
                _beltRevision = -1;
            }
            if (_beltRevision == HomeValleyConstruction.Revision)
            {
                return;
            }
            _beltRevision = HomeValleyConstruction.Revision;
            int n = 0;
            foreach (PlannedBeltRecord p in state.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] != 0)
                    {
                        continue;
                    }
                    GameObject tile = Tile(n++);
                    tile.transform.position = new Vector3(p.Xs[i], 0.06f, p.Ys[i]);
                    tile.transform.rotation = Quaternion.Euler(0f, p.Dirs[i] * 90f, 0f);
                }
            }
            for (int i = n; i < _beltTiles.Count; i++)
            {
                _beltTiles[i].SetActive(false);
            }
            ActiveBeltTiles = n;
        }

        private GameObject Tile(int index)
        {
            while (_beltTiles.Count <= index)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "PlannedBelt" + _beltTiles.Count;
                Collider c = go.GetComponent<Collider>();
                if (c != null)
                {
                    GameLogic.View.UnityObjects.Release(c); // 不挡选中射线。
                }
                go.transform.SetParent(_beltRoot.transform, false);
                // 窄而长：沿传送带方向（形状表达方向，不只靠颜色）。
                go.transform.localScale = new Vector3(0.55f, 0.08f, 0.9f);
                go.GetComponent<Renderer>().sharedMaterial = _beltMaterial;
                _beltTiles.Add(go);
            }
            GameObject t = _beltTiles[index];
            t.SetActive(true);
            return t;
        }

        /// <summary>战略视角每帧：光标下是施工现场就给世界悬停提示，否则离开（只在自己挂上过时才离开，不打扰别的悬停）。O(1) + 规划格查找。</summary>
        public void TickHover(CampaignState state, Camera camera, bool allowed)
        {
            bool over = false;
            if (allowed && state != null && camera != null && !InputRouter.IsUiPointerBlocked())
            {
                Vector3 screen = InputRouter.Reader.MousePosition;
                Ray ray = camera.ScreenPointToRay(screen);
                if (Mathf.Abs(ray.direction.y) > 1e-4f)
                {
                    float t = -ray.origin.y / ray.direction.y;
                    if (t > 0f)
                    {
                        Vector3 hit = ray.origin + ray.direction * t;
                        GridCell cell = GridCell.FromWorld(new Vector2(hit.x, hit.z));
                        if (IsSiteCell(state, cell))
                        {
                            over = true;
                            _hoverState = state;
                            _hoverCell = cell;
                            _hovering = true;
                            UiTooltip.HoverWorld(HoverKey, new Vector2(screen.x, screen.y), _provider);
                        }
                    }
                }
            }
            if (!over && _hovering)
            {
                _hovering = false;
                if (UiTooltip.WorldKey == HoverKey)
                {
                    UiTooltip.LeaveWorld();
                }
            }
        }

        private static bool IsSiteCell(CampaignState state, GridCell cell)
        {
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (b != null)
            {
                return HomeValleyController.IsPlannedGhost(b);
            }
            return HomeValleyConstruction.IsPlannedMarker(HomeGridService.MapFor(state).GetBelt(cell));
        }

        private TooltipContent ProvideHover()
        {
            if (!_hovering || _hoverState == null || !HomeValleyConstruction.TryDescribeSite(_hoverState, _hoverCell, out string title, out string body))
            {
                return null;
            }
            return new TooltipContent { Title = title, Body = body, Shortcut = GameActionId.ConstructionQueue, CodexEntryId = "codex.build.construction" };
        }

        /// <summary>悬停提示当前是否挂在施工现场上（自检读）。</summary>
        public bool Hovering => _hovering;

        public void Release()
        {
            if (_hovering && UiTooltip.WorldKey == HoverKey)
            {
                UiTooltip.LeaveWorld();
            }
            _hovering = false;
            if (_beltRoot != null)
            {
                GameLogic.View.UnityObjects.Release(_beltRoot);
                _beltRoot = null;
            }
            if (_beltMaterial != null)
            {
                GameLogic.View.UnityObjects.Release(_beltMaterial);
                _beltMaterial = null;
            }
            _beltTiles.Clear();
            _beltRevision = -1;
            ActiveBeltTiles = 0;
        }
    }
}
