using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Command
{
    /// <summary>
    /// RTS 选择、编组与命令下达（M2-02）。
    ///
    /// **选择集与编组是纯热更层状态**，内核不认识"编队"这个概念——它只认识每个单位身上
    /// 那一条 <see cref="UnitCommand"/>。下令时把选择集翻译成一批命令一次性写进内核，
    /// 之后逐帧的事全归 AOT 作业，热更层这里不做任何按单位数的逐帧循环。
    ///
    /// **不是 GameModule**：与 <c>CameraDirector</c> 同理——它要在玩法暂停时继续工作
    /// （战略暂停下选人、排队下令正是它存在的理由），而 <c>_hub</c> 会被暂停早退整个冻住。
    /// </summary>
    public sealed class SquadCommandSystem
    {
        /// <summary>命令到达判定半径。给得比单位半径宽一些，避免挤在一起时反复微调位置。</summary>
        private const float DefaultArriveRadius = 1.2f;
        /// <summary>守备留守半径。</summary>
        private const float GuardRadius = 4f;
        /// <summary>框选最小边长（世界单位）。小于它按"点选"处理，否则手抖一下就变成空框。</summary>
        private const float MinDragWorldSize = 0.6f;
        /// <summary>点选的命中半径。</summary>
        private const float ClickPickRadius = 1.5f;

        private SimBridge _sim;
        private Camera _camera;

        private readonly List<SimEntityId> _selection = new List<SimEntityId>(SimConst.MaxSelectionSize);
        /// <summary>编组 1~9。值是稳定实体 ID——存索引会在槽位复用后指向别的单位。</summary>
        private readonly Dictionary<int, List<SimEntityId>> _groups = new Dictionary<int, List<SimEntityId>>(9);
        /// <summary>暂停期间排队的命令，恢复时按下达顺序依次兑现。</summary>
        private readonly List<PendingCommand> _queued = new List<PendingCommand>(8);

        private bool _dragging;
        private float2 _dragStartWorld;
        private float2 _dragCurrentWorld;

        /// <summary>一条排队中的命令。记录的是**当时的选择集快照**，不是引用——
        /// 否则暂停期间改了选择，恢复时会把命令下给另一批单位。</summary>
        private struct PendingCommand
        {
            public SimEntityId[] Targets;
            public UnitCommand Command;
        }

        public IReadOnlyList<SimEntityId> Selection => _selection;
        public int QueuedCommandCount => _queued.Count;
        public bool IsDragging => _dragging;
        public float2 DragStartWorld => _dragStartWorld;
        public float2 DragCurrentWorld => _dragCurrentWorld;

        /// <summary>本局累计成功下达的命令数（含排队后兑现的）。验收用。</summary>
        public int IssuedCommandCount { get; private set; }

        public void Bind(SimBridge sim, Camera camera)
        {
            _sim = sim;
            _camera = camera;
            _selection.Clear();
            _groups.Clear();
            _queued.Clear();
            _dragging = false;
            IssuedCommandCount = 0;
        }

        public void Unbind()
        {
            _sim = null;
            _camera = null;
            _selection.Clear();
            _groups.Clear();
            _queued.Clear();
            _dragging = false;
        }

        /// <summary>
        /// 每帧驱动。<paramref name="paused"/> 只决定命令是立即下达还是排队，
        /// 不决定这里跑不跑——战略暂停下必须能继续选人。
        /// </summary>
        public void Tick(bool paused)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            // 先剔除已经不存在的单位，再处理输入。否则这一帧的命令会下给尸体。
            PruneDeadSelection();

            if (!paused)
            {
                FlushQueuedCommands();
            }

            if (!InputRouter.Owns(InputScope.Strategy))
            {
                // 让位时结束拖拽，避免松手在别的域里发生、框选状态卡住。
                _dragging = false;
                return;
            }

            HandleSelectionInput();
            HandleGroupInput();
            HandleCommandInput(paused);
        }

        // ── 选择 ──

        private void HandleSelectionInput()
        {
            if (Input.GetMouseButtonDown(0) && TryScreenToWorld(Input.mousePosition, out float2 down))
            {
                _dragging = true;
                _dragStartWorld = down;
                _dragCurrentWorld = down;
            }

            if (_dragging && TryScreenToWorld(Input.mousePosition, out float2 move))
            {
                _dragCurrentWorld = move;
            }

            if (!_dragging || !Input.GetMouseButtonUp(0))
            {
                return;
            }

            _dragging = false;
            bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float2 lo = math.min(_dragStartWorld, _dragCurrentWorld);
            float2 hi = math.max(_dragStartWorld, _dragCurrentWorld);
            float2 size = hi - lo;

            // 拖得太小按点选处理：否则一次普通点击会变成零面积的框，什么都选不中。
            if (size.x < MinDragWorldSize && size.y < MinDragWorldSize)
            {
                float2 half = new float2(ClickPickRadius, ClickPickRadius);
                lo = _dragCurrentWorld - half;
                hi = _dragCurrentWorld + half;
            }

            SimUnitPick[] picks = _sim.QueryUnitsInRect(lo, hi);
            if (!additive)
            {
                _selection.Clear();
            }
            for (int i = 0; i < picks.Length; i++)
            {
                AddToSelection(picks[i].EntityId);
            }
        }

        private void AddToSelection(SimEntityId id)
        {
            if (!id.IsValid || _selection.Count >= SimConst.MaxSelectionSize)
            {
                return;
            }
            for (int i = 0; i < _selection.Count; i++)
            {
                if (_selection[i] == id) { return; }
            }
            _selection.Add(id);
        }

        /// <summary>
        /// 剔除已死亡/不存在的单位。O(选择集)，不是 O(敌人数)——选择集被
        /// <see cref="SimConst.MaxSelectionSize"/> 卡在 64 以内，与场上敌人规模无关。
        /// </summary>
        private void PruneDeadSelection()
        {
            for (int i = _selection.Count - 1; i >= 0; i--)
            {
                if (!IsSelectable(_selection[i]))
                {
                    _selection.RemoveAt(i);
                }
            }
        }

        private bool IsSelectable(SimEntityId id)
        {
            SimWorld world = _sim.World;
            return world != null &&
                   world.TryGetUnitControlState(id, out SimUnitControlState state) &&
                   state.IsAlive &&
                   state.IntentSource != IntentSource.Player;
        }

        // ── 编组 ──

        private void HandleGroupInput()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            for (int slot = 1; slot <= 9; slot++)
            {
                KeyCode key = KeyCode.Alpha0 + slot;
                if (!InputRouter.ConsumeKeyDown(key, InputScope.Strategy))
                {
                    continue;
                }

                if (ctrl)
                {
                    AssignGroup(slot);
                }
                else
                {
                    RecallGroup(slot);
                }
                return;
            }
        }

        public void AssignGroup(int slot)
        {
            if (slot < 1 || slot > 9)
            {
                return;
            }
            _groups[slot] = new List<SimEntityId>(_selection);
        }

        public void RecallGroup(int slot)
        {
            if (!_groups.TryGetValue(slot, out List<SimEntityId> members))
            {
                return;
            }

            _selection.Clear();
            for (int i = 0; i < members.Count; i++)
            {
                // 编组里的死人不该复活进选择集——编组存的是稳定 ID，但单位会死。
                if (IsSelectable(members[i]))
                {
                    AddToSelection(members[i]);
                }
            }
        }

        public int GroupSize(int slot)
        {
            return _groups.TryGetValue(slot, out List<SimEntityId> members) ? members.Count : 0;
        }

        // ── 命令 ──

        private void HandleCommandInput(bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }

            // 右键 = 智能命令：点在敌人身上就是攻击，点在空地就是移动。
            if (Input.GetMouseButtonDown(1) && TryScreenToWorld(Input.mousePosition, out float2 world))
            {
                if (TryPickHostile(world, out SimEntityId hostile))
                {
                    Issue(UnitCommandKind.Attack, world, hostile, paused);
                }
                else
                {
                    Issue(UnitCommandKind.Move, world, SimEntityId.None, paused);
                }
                return;
            }

            if (InputRouter.ConsumeKeyDown(KeyCode.G, InputScope.Strategy) &&
                TryScreenToWorld(Input.mousePosition, out float2 guardAt))
            {
                Issue(UnitCommandKind.Guard, guardAt, SimEntityId.None, paused);
                return;
            }

            if (InputRouter.ConsumeKeyDown(KeyCode.H, InputScope.Strategy) &&
                TryScreenToWorld(Input.mousePosition, out float2 retreatTo))
            {
                Issue(UnitCommandKind.Retreat, retreatTo, SimEntityId.None, paused);
            }
        }

        /// <summary>下达命令。暂停期间不立即执行而是排队，恢复后按下达顺序兑现。</summary>
        public int Issue(UnitCommandKind kind, float2 targetPosition, SimEntityId targetEntity, bool paused)
        {
            if (_sim == null || !_sim.Running || _selection.Count == 0)
            {
                return 0;
            }

            var command = new UnitCommand
            {
                Kind = kind,
                TargetPosition = targetPosition,
                TargetEntity = targetEntity,
                ArriveRadius = kind == UnitCommandKind.Guard ? GuardRadius : DefaultArriveRadius,
            };

            if (paused)
            {
                // 存选择集的**快照**而不是引用：暂停期间玩家还会继续改选择，
                // 恢复时照引用下发就会把命令下给另一批单位。
                _queued.Add(new PendingCommand
                {
                    Targets = _selection.ToArray(),
                    Command = command,
                });
                return _selection.Count;
            }

            int accepted = _sim.IssueCommand(_selection.ToArray(), command);
            if (accepted > 0)
            {
                IssuedCommandCount++;
            }
            return accepted;
        }

        /// <summary>把排队的命令按下达顺序兑现。顺序稳定是 M2-02 的验收项。</summary>
        public int FlushQueuedCommands()
        {
            if (_queued.Count == 0 || _sim == null || !_sim.Running)
            {
                return 0;
            }

            int flushed = 0;
            for (int i = 0; i < _queued.Count; i++)
            {
                PendingCommand pending = _queued[i];
                if (_sim.IssueCommand(pending.Targets, pending.Command) > 0)
                {
                    IssuedCommandCount++;
                    flushed++;
                }
            }
            _queued.Clear();
            return flushed;
        }

        public void ClearSelection()
        {
            _selection.Clear();
        }

        /// <summary>把外部选中的单位塞进选择集（验收与调试入口）。</summary>
        public void SelectExplicit(IReadOnlyList<SimEntityId> ids, bool additive = false)
        {
            if (!additive)
            {
                _selection.Clear();
            }
            if (ids == null)
            {
                return;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                AddToSelection(ids[i]);
            }
        }

        // ── 坐标与拾取 ──

        private bool TryPickHostile(float2 world, out SimEntityId hostile)
        {
            var half = new float2(ClickPickRadius, ClickPickRadius);
            // commandableOnly=false 才能选到敌人——可指挥过滤只放友军过。
            SimUnitPick[] picks = _sim.QueryUnitsInRect(world - half, world + half, commandableOnly: false);
            float bestSq = float.MaxValue;
            hostile = SimEntityId.None;
            for (int i = 0; i < picks.Length; i++)
            {
                if (picks[i].Faction != SimFaction.Hostile)
                {
                    continue;
                }
                float d = math.distancesq(picks[i].Position, world);
                if (d < bestSq)
                {
                    bestSq = d;
                    hostile = picks[i].EntityId;
                }
            }
            return hostile.IsValid;
        }

        /// <summary>屏幕坐标 → 世界 XZ。游戏是俯视，Y 是高度，所以打到 y=0 平面上。</summary>
        private bool TryScreenToWorld(Vector3 screenPosition, out float2 world)
        {
            world = float2.zero;
            if (_camera == null)
            {
                return false;
            }

            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = _camera.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out float enter))
            {
                return false;
            }

            Vector3 hit = ray.GetPoint(enter);
            world = new float2(hit.x, hit.z);
            return true;
        }
    }
}
