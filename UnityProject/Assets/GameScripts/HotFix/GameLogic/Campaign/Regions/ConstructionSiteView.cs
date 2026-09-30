using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
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
        /// <summary>FG3-LOG-03：已建成传送带的悬停（FGR-LOG-081）。</summary>
        private const int BeltHoverKey = -732;

        private readonly Dictionary<string, WorkOrderRecord> _byTarget = new Dictionary<string, WorkOrderRecord>(StringComparer.Ordinal);
        private readonly List<GameObject> _beltTiles = new List<GameObject>(64);
        private GameObject _beltRoot;
        private Material _beltMaterial;
        /// <summary>FG3-LOG-03：被摧毁、等待确认重建的传送带虚影（红色调 + 更窄的断条，颜色之外有形状区分，B15）。</summary>
        private Material _destroyedMaterial;
        private int _beltRevision = -1;
        private CampaignState _hoverState;
        private GridCell _hoverCell;
        private bool _hovering;
        private bool _hoveringBelt;
        private readonly Func<TooltipContent> _provider;
        private readonly Func<TooltipContent> _beltProvider;

        public ConstructionSiteView()
        {
            _provider = ProvideHover;
            _beltProvider = ProvideBeltHover;
        }

        /// <summary>画面上被摧毁的传送带虚影块数（自检读）。</summary>
        public int ActiveDestroyedTiles { get; private set; }

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
                _destroyedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.95f, 0.35f, 0.25f, 0.6f) };
                _beltRevision = -1;
            }
            if (_beltRevision == HomeValleyConstruction.Revision)
            {
                return;
            }
            _beltRevision = HomeValleyConstruction.Revision;
            int n = 0;
            int destroyed = 0;
            int upgrading = 0;
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
                    // FG3-LOG-03：被摧毁的虚影更窄更短（“断条”），红色调；普通规划是淡蓝长条。
                    // FG3-LOG-04：分流器 / 合流器的虚影是方块，地下传送带两端是横着的短条（形状区分，不只靠颜色；占位 B22）。
                    // FG3-LOG-05：管线层的虚影——管线是细方块、泵是小方块、储罐是大方块、阀门是沿流向的短条，青色调（与传送带的淡蓝区分；占位 B22）。
                    // FG3-LOG-07：升级中的件——建成的件上面一块扁平的黄色大方板（形状 + 颜色都与虚影不同；占位 B22）。
                    tile.transform.localScale = p.Upgrade ? new Vector3(0.95f, 0.03f, 0.95f)
                        : p.Destroyed ? new Vector3(0.3f, 0.08f, 0.6f)
                        : p.PipePiece == 1 ? new Vector3(0.32f, 0.1f, 0.32f)
                        : p.PipePiece == 2 ? new Vector3(0.6f, 0.1f, 0.6f)
                        : p.PipePiece == 3 ? new Vector3(0.9f, 0.1f, 0.9f)
                        : p.PipePiece == 4 ? new Vector3(0.35f, 0.1f, 0.8f)
                        : p.NodeKind == 1 || p.NodeKind == 2 ? new Vector3(0.85f, 0.08f, 0.85f)
                        : p.NodeKind == 3 ? new Vector3(0.9f, 0.08f, 0.35f)
                        : new Vector3(0.55f, 0.08f, 0.9f);
                    if (p.Upgrade)
                    {
                        tile.transform.position = new Vector3(p.Xs[i], 0.3f, p.Ys[i]);
                    }
                    tile.GetComponent<Renderer>().sharedMaterial = p.Upgrade ? UpgradeMaterial() : p.Destroyed ? _destroyedMaterial : p.PipePiece > 0 ? PipeGhostMaterial() : _beltMaterial;
                    if (p.Destroyed)
                    {
                        destroyed++;
                    }
                    if (p.Upgrade)
                    {
                        upgrading++;
                    }
                }
            }
            ActiveDestroyedTiles = destroyed;
            ActiveUpgradeTiles = upgrading;
            for (int i = n; i < _beltTiles.Count; i++)
            {
                _beltTiles[i].SetActive(false);
            }
            ActiveBeltTiles = n;
        }

        private Material _pipeGhostMaterial;
        private Material _upgradeMaterial;

        private Material UpgradeMaterial() =>
            _upgradeMaterial != null ? _upgradeMaterial : _upgradeMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.95f, 0.8f, 0.2f, 0.5f) };

        /// <summary>FG3-LOG-07：当前画着的“升级中”标记格数（自检读）。</summary>
        public int ActiveUpgradeTiles { get; private set; }

        private Material PipeGhostMaterial() =>
            _pipeGhostMaterial != null ? _pipeGhostMaterial : _pipeGhostMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.25f, 0.85f, 0.8f, 0.55f) };

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
            bool overPower = false;
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
                            ReleaseBeltHover();
                            _hoverState = state;
                            _hoverCell = cell;
                            _hovering = true;
                            UiTooltip.HoverWorld(HoverKey, new Vector2(screen.x, screen.y), _provider);
                        }
                        else if (PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state) && PipeNetworkService.Kernel.HasCell(cell.X, cell.Y))
                        {
                            // FG3-LOG-05（FGR-LOG-042）：已建成的管线件——网络的供给、需求、储量、瓶颈与根因。
                            over = true;
                            if (_hovering && UiTooltip.WorldKey == HoverKey)
                            {
                                UiTooltip.LeaveWorld();
                            }
                            _hovering = false;
                            if (_hoveringBelt && UiTooltip.WorldKey == BeltHoverKey)
                            {
                                UiTooltip.LeaveWorld();
                            }
                            _hoveringBelt = false;
                            _hoverState = state;
                            _hoverCell = cell;
                            _hoveringPipe = true;
                            UiTooltip.HoverWorld(PipeHoverKey, new Vector2(screen.x, screen.y), _pipeProvider);
                        }
                        else if (BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) && BeltNetworkService.Kernel.HasCell(cell.X, cell.Y))
                        {
                            // FG3-LOG-03（FGR-LOG-081）：已建成的传送带——物品、速度、吞吐、状态与原因、耐久。
                            over = true;
                            if (_hovering && UiTooltip.WorldKey == HoverKey)
                            {
                                UiTooltip.LeaveWorld();
                            }
                            _hovering = false;
                            _hoverState = state;
                            _hoverCell = cell;
                            _hoveringBelt = true;
                            UiTooltip.HoverWorld(BeltHoverKey, new Vector2(screen.x, screen.y), _beltProvider);
                        }
                        else if (HomeGridService.BuildingAt(state, cell) is BuildingRecord pb
                                 && (pb.ConstructionState == BuildingConstructionState.Operational || pb.ConstructionState == BuildingConstructionState.Disabled
                                     || pb.ConstructionState == BuildingConstructionState.Damaged)
                                 && ((ReferenceEquals(HomeValleyPowerGrid.BoundState, state) && HomeValleyPowerGrid.IsPowerRelevantType(pb.BuildingTypeId))
                                     || GridContent.PortsOf(pb.BuildingTypeId).Count > 0 || pb.ConstructionState != BuildingConstructionState.Operational))
                        {
                            // FG3-LOG-06（FGR-LOG-060 / 061）：已建成、和电网有关的建筑——在哪个电网、有没有电、优先级、电网读数。
                            // FG3-LOG-08（FGR-LOG-081“建筑：状态和原因”/ 082）：再写“为什么不工作”的原因链（有端口的建筑、受损 / 关停的建筑也有悬停）。
                            over = true;
                            overPower = true;
                            if (_hovering && UiTooltip.WorldKey == HoverKey)
                            {
                                UiTooltip.LeaveWorld();
                            }
                            _hovering = false;
                            ReleaseBeltHover();
                            _hoverState = state;
                            _hoverCell = cell;
                            _powerHoverId = pb.BuildingId;
                            _hoveringPower = true;
                            UiTooltip.HoverWorld(PowerHoverKey, new Vector2(screen.x, screen.y), _powerProvider);
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
            if (!over)
            {
                ReleaseBeltHover();
            }
            if (!overPower && _hoveringPower)
            {
                _hoveringPower = false;
                _powerHoverId = null;
                if (UiTooltip.WorldKey == PowerHoverKey)
                {
                    UiTooltip.LeaveWorld();
                }
            }
            if ((!over || !_hoverOnPipeThisFrame()) && _hoveringPipe)
            {
                _hoveringPipe = false;
                if (UiTooltip.WorldKey == PipeHoverKey)
                {
                    UiTooltip.LeaveWorld();
                }
            }
        }

        private bool _hoverOnPipeThisFrame() =>
            _hoverState != null && PipeNetworkService.IsRunning && PipeNetworkService.Kernel.HasCell(_hoverCell.X, _hoverCell.Y) && !IsSiteCell(_hoverState, _hoverCell);

        private const int PipeHoverKey = -733;
        private const int PowerHoverKey = -734;
        private bool _hoveringPower;
        private string _powerHoverId;
        private Func<TooltipContent> _powerProviderCache;
        private Func<TooltipContent> _powerProvider => _powerProviderCache ??= ProvidePowerHover;

        /// <summary>悬停提示当前是否挂在和电网有关的建筑上（自检读）。</summary>
        public bool HoveringPower => _hoveringPower;

        private TooltipContent ProvidePowerHover()
        {
            if (!_hoveringPower || _hoverState == null)
            {
                return null;
            }
            BuildingRecord b = HomeGridService.FindBuilding(_hoverState, _powerHoverId);
            if (b == null)
            {
                return null;
            }
            string body = null;
            bool power = ReferenceEquals(HomeValleyPowerGrid.BoundState, _hoverState) && HomeValleyPowerGrid.TryDescribeBuilding(_hoverState, b, out body);
            // FG3-LOG-08：停工的建筑追加“为什么不工作”的原因链（症状 → 根源），并把快捷键换成“为什么不工作”面板。
            bool diag = Logistics.RootCauseDiagnosis.TryDescribeForHover(_hoverState, b, out string why);
            if (!power && !diag)
            {
                return null;
            }
            if (diag)
            {
                body = string.IsNullOrEmpty(body) ? why : body + "\n" + why;
            }
            return new TooltipContent
            {
                Title = HomeGridService.DisplayName(b.BuildingTypeId),
                Body = body,
                Shortcut = diag ? GameActionId.OpenDiagnosis : GameActionId.OpenPowerGrid,
                CodexEntryId = diag ? "codex.logistics.diagnosis" : "codex.logistics.power",
            };
        }
        private bool _hoveringPipe;
        private Func<TooltipContent> _pipeProviderCache;
        private Func<TooltipContent> _pipeProvider => _pipeProviderCache ??= ProvidePipeHover;

        /// <summary>悬停提示当前是否挂在已建成的管线件上（自检读）。</summary>
        public bool HoveringPipe => _hoveringPipe;

        private TooltipContent ProvidePipeHover()
        {
            if (!_hoveringPipe || _hoverState == null || !PipeNetworkService.TryDescribeHover(_hoverState, _hoverCell, out string title, out string body))
            {
                return null;
            }
            return new TooltipContent { Title = title, Body = body, Shortcut = GameActionId.OpenBuildMenu, CodexEntryId = "codex.logistics.fluid" };
        }

        private void ReleaseBeltHover()
        {
            if (!_hoveringBelt)
            {
                return;
            }
            _hoveringBelt = false;
            if (UiTooltip.WorldKey == BeltHoverKey)
            {
                UiTooltip.LeaveWorld();
            }
        }

        private TooltipContent ProvideBeltHover()
        {
            if (!_hoveringBelt || _hoverState == null || !BeltNetworkService.TryDescribeHover(_hoverState, _hoverCell, out string title, out string body))
            {
                return null;
            }
            // FG3-LOG-04：分流器 / 合流器 / 地下传送带的悬停链接到它们自己的图鉴条目。
            bool node = BeltNetworkService.TryGetPiece(_hoverCell, out BinGames.Sim.Logistics.BeltNodeKind kind, out _) && kind != BinGames.Sim.Logistics.BeltNodeKind.Belt;
            return new TooltipContent { Title = title, Body = body, Shortcut = GameActionId.ClearBeltMode, CodexEntryId = node ? "codex.logistics.splitter" : "codex.logistics.belt" };
        }

        /// <summary>悬停提示当前是否挂在已建成的传送带上（自检读）。</summary>
        public bool HoveringBelt => _hoveringBelt;

        private static bool IsSiteCell(CampaignState state, GridCell cell)
        {
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (b != null)
            {
                return HomeValleyController.IsPlannedGhost(b);
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            return HomeValleyConstruction.IsPlannedMarker(map.GetBelt(cell)) || HomeValleyConstruction.IsPlannedMarker(map.GetPipe(cell));
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
            ReleaseBeltHover();
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
            if (_destroyedMaterial != null)
            {
                GameLogic.View.UnityObjects.Release(_destroyedMaterial);
                _destroyedMaterial = null;
            }
            if (_pipeGhostMaterial != null)
            {
                GameLogic.View.UnityObjects.Release(_pipeGhostMaterial);
                _pipeGhostMaterial = null;
            }
            if (_upgradeMaterial != null)
            {
                GameLogic.View.UnityObjects.Release(_upgradeMaterial);
                _upgradeMaterial = null;
            }
            _beltTiles.Clear();
            _beltRevision = -1;
            ActiveBeltTiles = 0;
        }
    }
}
