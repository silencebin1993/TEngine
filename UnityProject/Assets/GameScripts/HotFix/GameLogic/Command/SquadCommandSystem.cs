using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
// 命名空间 GameLogic.Command.Formation 与其中的类 Formation 同名，且 SquadCommandSystem
// 本身就在 GameLogic.Command 下——嵌套命名空间对裸标识符 "Formation" 的解析优先级高于
// using 别名，起别名也压不掉，所以本文件内一律用 SquadFormation 这个别名，不裸写 Formation。
using SquadFormation = GameLogic.Command.Formation.Formation;
using FormationRegistry = GameLogic.Command.Formation.FormationRegistry;
using FormationCommand = GameLogic.Command.Formation.FormationCommand;
using FormationCommandPriority = GameLogic.Command.Formation.FormationCommandPriority;
using FormationCommandIssueResult = GameLogic.Command.Formation.FormationCommandIssueResult;
using FormationCommandState = GameLogic.Command.Formation.FormationCommandState;

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

        /// <summary>M4-R02 最小桥接：编队注册表。为 null 时本类完全不接触 Formation，
        /// 行为与桥接前逐字相同（部分测试关心的是与 Formation 无关的机制，传 null 保持隔离）。</summary>
        private FormationRegistry _formations;

        /// <summary>
        /// surgical-window（M2-05b 实施第 2 条）：右键下达 Attack 命令时要顺带指定的接点类别。
        /// 调试级输入——按 P 键在 None → Primary → Secondary → None 之间循环，没有正式 UI/美术，
        /// 标签沿用 M2-05a 的占位（"PrimaryOrgan"/"SecondaryOrgan"，只在注释与契约文档里出现）。
        /// 只影响之后新下达的 Attack 命令；Move/Guard/Retreat 与它无关，恒定 None。
        /// **刻意不用 O**：<c>BattleCarrierUIToolkit</c> 已经把 O 键钉死给了运载器面板开关
        /// （直接读 <see cref="UnityEngine.Input"/>，不经 <c>InputRouter</c>），撞键会让两个系统
        /// 同一次按键都触发，P 键在全仓键位里未被占用。</summary>
        private SimBodyPartSlot _pendingAttackPart = SimBodyPartSlot.None;

        private readonly List<SimEntityId> _selection = new List<SimEntityId>(SimConst.MaxSelectionSize);
        /// <summary>编组 1~9。值是稳定实体 ID——存索引会在槽位复用后指向别的单位。</summary>
        private readonly Dictionary<int, List<SimEntityId>> _groups = new Dictionary<int, List<SimEntityId>>(9);
        /// <summary>M4-R02 最小桥接：编组槽位 → 对应 Formation 的 Id。数字编组=编队槽位，
        /// 1~9 号槽位与 1~9 支 Formation 一一对应（见 DESIGN.md 2.1/2.9）。</summary>
        private readonly Dictionary<int, string> _groupFormationIds = new Dictionary<int, string>(9);
        /// <summary>M4-R02 最小桥接：当前是否正在操作一支刚被 <see cref="RecallGroup"/> 出来、
        /// 且未被后续任何选择/编组动作弄脏的编队。null 表示当前按裸选择集处理。</summary>
        private string _activeFormationId;
        /// <summary>暂停期间排队的命令，恢复时按下达顺序依次兑现。</summary>
        private readonly List<PendingCommand> _queued = new List<PendingCommand>(8);

        /// <summary>M4-R02 最小桥接：最近一次真正执行命令分流（立即下达或 flush 处理某条排队
        /// 命令）的结果。只读，纯数据层，见 <see cref="SquadFormationDispatchOutcome"/>。</summary>
        public SquadFormationDispatchOutcome LastFormationDispatchOutcome { get; private set; }
            = SquadFormationDispatchOutcome.NotFormationRouted;

        private bool _dragging;
        private float2 _dragStartWorld;
        private float2 _dragCurrentWorld;

        /// <summary>一条排队中的命令。记录的是**当时的选择集快照**，不是引用——
        /// 否则暂停期间改了选择，恢复时会把命令下给另一批单位。</summary>
        private struct PendingCommand
        {
            public SimEntityId[] Targets;
            public UnitCommand Command;
            /// <summary>M4-R02 最小桥接：入队那一刻是否走编队路径，null=否。只在入队时打标签，
            /// 不在入队时执行分流——分流只在真正执行命令的时刻（flush）发生。</summary>
            public string FormationId;
        }

        public IReadOnlyList<SimEntityId> Selection => _selection;
        public int QueuedCommandCount => _queued.Count;

        /// <summary>当前待用的接点类别（M2-05b 调试输入）。验收/调试可读，不是正式 UI 状态。</summary>
        public SimBodyPartSlot PendingAttackPart => _pendingAttackPart;
        public bool IsDragging => _dragging;
        public float2 DragStartWorld => _dragStartWorld;
        public float2 DragCurrentWorld => _dragCurrentWorld;

        /// <summary>本局累计成功下达的命令数（含排队后兑现的）。验收用。</summary>
        public int IssuedCommandCount { get; private set; }

        public void Bind(SimBridge sim, Camera camera, FormationRegistry formations)
        {
            _sim = sim;
            _camera = camera;
            _formations = formations;
            _selection.Clear();
            _groups.Clear();
            _groupFormationIds.Clear();
            _activeFormationId = null;
            LastFormationDispatchOutcome = SquadFormationDispatchOutcome.NotFormationRouted;
            _queued.Clear();
            _dragging = false;
            _pendingAttackPart = SimBodyPartSlot.None;
            IssuedCommandCount = 0;
        }

        public void Unbind()
        {
            _sim = null;
            _camera = null;
            _formations = null;
            _selection.Clear();
            _groups.Clear();
            _groupFormationIds.Clear();
            _activeFormationId = null;
            LastFormationDispatchOutcome = SquadFormationDispatchOutcome.NotFormationRouted;
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
            if (InputRouter.Reader.GetMouseButtonDown(0) &&
                TryScreenToWorld(InputRouter.Reader.MousePosition, out float2 down))
            {
                _dragging = true;
                _dragStartWorld = down;
                _dragCurrentWorld = down;
            }

            if (_dragging && TryScreenToWorld(InputRouter.Reader.MousePosition, out float2 move))
            {
                _dragCurrentWorld = move;
            }

            if (!_dragging || !InputRouter.Reader.GetMouseButtonUp(0))
            {
                return;
            }

            _dragging = false;
            // M4-R02 最小桥接：任何拖框/点选都结束"正在操作某支已召回编队"的状态——不尝试
            // 判断这次选中的人是不是恰好等于某支编队的成员，简单规则、没有歧义（见 DESIGN.md 2.3）。
            _activeFormationId = null;
            bool additive = InputRouter.Reader.GetKey(KeyCode.LeftShift) || InputRouter.Reader.GetKey(KeyCode.RightShift);
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
            bool ctrl = InputRouter.Reader.GetKey(KeyCode.LeftControl) || InputRouter.Reader.GetKey(KeyCode.RightControl);
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

        /// <summary>
        /// M4-R02 最小桥接：数字编组=编队槽位。除既有的 <c>_groups</c> 快照外，同步创建/更新
        /// 一支对应的 <see cref="SquadFormation"/>（DESIGN.md 2.2）：
        /// ① 结束"正在操作已召回编队"状态；② 空选择不为从未用过的槽位新建空编队；
        /// ③ 槽位互斥——新成员如果属于别的槽位，先从那边摘除（Formation 成员 + 对应 `_groups`
        /// 列表同步），保证任意时刻一个单位最多属于一支编队；④ 同一槽位复用同一个 FormationId，
        /// 不是每次都开新的；⑤ 与新选择集做差集同步（移除多余成员、加入新成员）。
        /// </summary>
        public void AssignGroup(int slot)
        {
            if (slot < 1 || slot > 9)
            {
                return;
            }
            _groups[slot] = new List<SimEntityId>(_selection);
            _activeFormationId = null;

            if (_formations == null)
            {
                return;
            }
            if (_selection.Count == 0 && !_groupFormationIds.ContainsKey(slot))
            {
                return;
            }

            foreach (SimEntityId member in _selection)
            {
                foreach (KeyValuePair<int, string> other in _groupFormationIds)
                {
                    if (other.Key == slot)
                    {
                        continue;
                    }
                    SquadFormation otherFormation = _formations.GetFormation(other.Value);
                    if (otherFormation != null && otherFormation.IsMember(member))
                    {
                        otherFormation.RemoveMember(member);
                        if (_groups.TryGetValue(other.Key, out List<SimEntityId> otherGroupList))
                        {
                            otherGroupList.Remove(member);
                        }
                    }
                }
            }

            if (!_groupFormationIds.TryGetValue(slot, out string formationId))
            {
                formationId = _formations.CreateFormation().Id;
                _groupFormationIds[slot] = formationId;
            }

            SquadFormation formation = _formations.GetFormation(formationId);
            if (formation == null)
            {
                return;
            }

            var toRemove = new List<SimEntityId>();
            foreach (SimEntityId existing in formation.Members)
            {
                if (!_selection.Contains(existing))
                {
                    toRemove.Add(existing);
                }
            }
            foreach (SimEntityId remove in toRemove)
            {
                formation.RemoveMember(remove);
            }
            foreach (SimEntityId add in _selection)
            {
                formation.AddMember(add);
            }
        }

        public void RecallGroup(int slot)
        {
            if (!_groups.TryGetValue(slot, out List<SimEntityId> members))
            {
                // 槽位不存在：完全 no-op，不 touch 任何既有状态（含 _activeFormationId）。
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

            // M4-R02 最小桥接：标记"当前操作的是哪支编队"，供 Issue 判断要不要走编队路径。
            _activeFormationId = _formations != null && _groupFormationIds.TryGetValue(slot, out string fid)
                ? fid
                : null;
        }

        public int GroupSize(int slot)
        {
            return _groups.TryGetValue(slot, out List<SimEntityId> members) ? members.Count : 0;
        }

        /// <summary>移除数字编队及其 Formation 成员关系；不会影响单位当前正在执行的命令。</summary>
        public void ClearGroup(int slot)
        {
            if (!_groups.TryGetValue(slot, out List<SimEntityId> members))
            {
                return;
            }

            if (_groupFormationIds.TryGetValue(slot, out string formationId))
            {
                SquadFormation formation = _formations?.GetFormation(formationId);
                if (formation != null)
                {
                    for (int i = 0; i < members.Count; i++)
                    {
                        formation.RemoveMember(members[i]);
                    }
                }
                _groupFormationIds.Remove(slot);
                if (_activeFormationId == formationId)
                {
                    _activeFormationId = null;
                }
            }

            _groups.Remove(slot);
        }

        /// <summary>
        /// 某个编组的成员（只读，不含存活过滤）。M2-04a 的交还延续要在交还那一刻记下
        /// "这个单位属于哪些编队"，而编队归属只存在这里——内核不认识编队。
        /// 返回内部列表本身而不是拷贝：调用方（<c>AiHandoffSystem</c>）只读一次就丢，
        /// 每次交还拷一份 64 项的数组是白付的代价。
        /// </summary>
        public IReadOnlyList<SimEntityId> GroupMembers(int slot)
        {
            return _groups.TryGetValue(slot, out List<SimEntityId> members)
                ? members
                : System.Array.Empty<SimEntityId>();
        }

        // ── 命令 ──

        private void HandleCommandInput(bool paused)
        {
            // P 键循环接点类别，不依赖选择集——即便这一刻没选中任何单位，也该能提前定好
            // "下一次 Attack 打哪个接点"，与右键命令解耦，避免手抖顺序把状态搞乱。
            if (InputRouter.ConsumeKeyDown(KeyCode.P, InputScope.Strategy))
            {
                CyclePendingAttackPart();
            }

            if (_selection.Count == 0)
            {
                return;
            }

            // 右键 = 智能命令：点在敌人身上就是攻击，点在空地就是移动。
            // ⑥-25（FC-REQ-022"插队/追加"）：按住 Shift 时不覆盖当前命令，排到编队队列末尾。
            if (InputRouter.Reader.GetMouseButtonDown(1) &&
                TryScreenToWorld(InputRouter.Reader.MousePosition, out float2 world))
            {
                bool queueBehindActive = InputRouter.Reader.GetKey(KeyCode.LeftShift)
                    || InputRouter.Reader.GetKey(KeyCode.RightShift);
                if (TryPickHostile(world, out SimEntityId hostile))
                {
                    Issue(UnitCommandKind.Attack, world, hostile, paused, _pendingAttackPart, queueBehindActive);
                }
                else
                {
                    Issue(UnitCommandKind.Move, world, SimEntityId.None, paused, queueBehindActive: queueBehindActive);
                }
                return;
            }

            if (InputRouter.ConsumeKeyDown(KeyCode.G, InputScope.Strategy) &&
                TryScreenToWorld(InputRouter.Reader.MousePosition, out float2 guardAt))
            {
                Issue(UnitCommandKind.Guard, guardAt, SimEntityId.None, paused);
                return;
            }

            if (InputRouter.ConsumeKeyDown(KeyCode.H, InputScope.Strategy) &&
                TryScreenToWorld(InputRouter.Reader.MousePosition, out float2 retreatTo))
            {
                Issue(UnitCommandKind.Retreat, retreatTo, SimEntityId.None, paused);
            }
        }

        /// <summary>
        /// 下达命令。暂停期间不立即执行而是排队，恢复后按下达顺序兑现。
        ///
        /// M4-R02 最小桥接：若当前选择集恰好等于一支刚被 <see cref="RecallGroup"/> 出来、
        /// 未被弄脏的编队，Move/Retreat 只交给 <see cref="SquadFormation.IssueCommand"/>（内核下发
        /// 交给 <c>FormationMovementDriver</c> 走共享路径）；Attack/Guard 两条腿并存（既记录
        /// 编队状态也照旧直接下内核）。裸选择集/未编组时行为与桥接前逐字相同。见 DESIGN.md 2.4/2.5。
        /// </summary>
        /// <param name="targetPart">M2-05b：只有 <see cref="UnitCommandKind.Attack"/> 会消费它，
        /// 其它命令类型传了也没有意义（内核侧 <c>ResolveMinionCombat</c> 只在 Attack 分支读它）。</param>
        /// <param name="queueBehindActive">⑥-25（FC-REQ-022"插队/追加"）：玩家显式请求排队
        /// （Shift+右键）而不是覆盖。只在能解析出编队时生效（裸选择集没有排队容器，见
        /// <see cref="EnqueueToFormation"/> 与 M4-R02 桥接范围）；<paramref name="paused"/> 为 true
        /// 时本参数不起作用——暂停期间一律走既有的 <see cref="_queued"/> 机制，恢复时按下达顺序兑现。</param>
        public int Issue(UnitCommandKind kind, float2 targetPosition, SimEntityId targetEntity, bool paused,
            SimBodyPartSlot targetPart = SimBodyPartSlot.None, bool queueBehindActive = false)
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
                TargetPart = targetPart,
            };

            SquadFormation formation = ResolveActiveFormationForCurrentSelection();

            if (paused)
            {
                // 存选择集的**快照**而不是引用：暂停期间玩家还会继续改选择/重新编组，
                // 恢复时照引用下发就会把命令下给另一批单位——只打标签，不在这里执行分流
                // （见 DESIGN.md 2.7/2.10：分流只在真正执行命令的时刻发生）。
                _queued.Add(new PendingCommand
                {
                    Targets = _selection.ToArray(),
                    Command = command,
                    FormationId = formation?.Id,
                });
                return _selection.Count;
            }

            if (formation != null)
            {
                if (queueBehindActive)
                {
                    return EnqueueToFormation(formation, kind, command, _selection);
                }
                return IssueToFormation(formation, kind, command, _selection);
            }

            LastFormationDispatchOutcome = SquadFormationDispatchOutcome.NotFormationRouted;
            int accepted = _sim.IssueCommand(_selection.ToArray(), command);
            if (accepted > 0)
            {
                IssuedCommandCount++;
            }
            return accepted;
        }

        /// <summary>只有"选择集恰好等于当前活跃编队的现存成员"才返回非空——任何拖框/点选/
        /// 重新编组都会让 <see cref="_activeFormationId"/> 提前清空或这里的匹配失败，回退到
        /// 既有的裸选择集直发路径，不做模糊匹配（见 DESIGN.md 2.3/2.4）。</summary>
        private SquadFormation ResolveActiveFormationForCurrentSelection()
        {
            if (_formations == null || _activeFormationId == null)
            {
                return null;
            }
            SquadFormation formation = _formations.GetFormation(_activeFormationId);
            return formation != null && MembersMatch(formation, _selection) ? formation : null;
        }

        private static bool MembersMatch(SquadFormation formation, IReadOnlyList<SimEntityId> targets)
        {
            if (formation.Members.Count != targets.Count)
            {
                return false;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                if (!formation.IsMember(targets[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static FormationCommand.CommandKind MapKind(UnitCommandKind kind)
        {
            switch (kind)
            {
                case UnitCommandKind.Attack: return FormationCommand.CommandKind.Attack;
                case UnitCommandKind.Guard: return FormationCommand.CommandKind.Guard;
                case UnitCommandKind.Retreat: return FormationCommand.CommandKind.Retreat;
                // Move 与「不会发生」的 None 共用兜底：SquadCommandSystem 现有输入只产生
                // Move/Attack/Guard/Retreat 四种，None 从不会走到这里。
                default: return FormationCommand.CommandKind.Move;
            }
        }

        /// <summary>把命令交给编队领域模型 + （视命令类型）内核。见 DESIGN.md 2.5 的分工表：
        /// Move/Retreat 只记编队状态、不重复下内核（交给 FormationMovementDriver）；
        /// Attack/Guard 两条腿并存。<paramref name="targets"/> 是本次真正要下发的目标集合——
        /// 立即下达时是 <see cref="_selection"/>，flush 排队命令时是过滤过死亡成员的快照。</summary>
        private int IssueToFormation(SquadFormation formation, UnitCommandKind kind, UnitCommand rawCommand,
            IReadOnlyList<SimEntityId> targets)
        {
            var formationCommand = new FormationCommand(MapKind(kind), rawCommand.TargetEntity,
                rawCommand.TargetPosition, priority: FormationCommandPriority.NormalPlayerCommand);
            FormationCommandIssueResult result = formation.IssueCommand(formationCommand);
            LastFormationDispatchOutcome = result == FormationCommandIssueResult.Activated
                ? SquadFormationDispatchOutcome.Activated
                : SquadFormationDispatchOutcome.QueuedBehindHigherPriority;

            if (result != FormationCommandIssueResult.Activated)
            {
                // 排队等待中：不重复下内核命令，避免"内核已经在执行/移动，编队层却说排队中"的
                // 不一致。今天在本设计范围内不可达——见 DESIGN.md 2.5"已知限制"。
                return 0;
            }

            if (kind == UnitCommandKind.Attack || kind == UnitCommandKind.Guard)
            {
                var targetArray = new SimEntityId[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    targetArray[i] = targets[i];
                }
                int accepted = _sim.IssueCommand(targetArray, rawCommand);
                if (accepted > 0)
                {
                    IssuedCommandCount++;
                }
                return accepted;
            }

            IssuedCommandCount++;
            return targets.Count;
        }

        /// <summary>
        /// ⑥-25（FC-REQ-022"插队/追加"）：玩家显式请求排队（Shift+右键），永远走
        /// <see cref="Formation.Formation.EnqueueCommand"/>，不经 <see cref="Formation.Formation.IssueCommand"/>
        /// 的覆盖/优先级判定——这正是"追加"与"覆盖"的语义区别。
        ///
        /// 编队原本空闲（没有 Active 命令）时必须在这里主动提升一次：<see cref="Formation.Formation"/>
        /// 只在 Complete/Fail/Interrupt 时联动提升下一条，纯 Enqueue 不会触发任何终结事件，
        /// 空队列变成一条 Pending 之后如果没人主动提升就会永远卡住，玩家会以为排队按了没反应。
        /// 提升出来的必然是我们刚加的这一条（队列原本是空的），因此复用调用方传入的
        /// <paramref name="kind"/>/<paramref name="rawCommand"/> 补发内核命令是安全的，不需要
        /// 反查 <see cref="Formation.FormationCommandEntry.Command"/> 再映射回 <see cref="UnitCommandKind"/>。
        /// </summary>
        private int EnqueueToFormation(SquadFormation formation, UnitCommandKind kind, UnitCommand rawCommand,
            IReadOnlyList<SimEntityId> targets)
        {
            var formationCommand = new FormationCommand(MapKind(kind), rawCommand.TargetEntity,
                rawCommand.TargetPosition, priority: FormationCommandPriority.NormalPlayerCommand);
            formation.EnqueueCommand(formationCommand);
            LastFormationDispatchOutcome = SquadFormationDispatchOutcome.QueuedByPlayerRequest;

            bool wasIdle = formation.ActiveCommand == null
                || formation.ActiveCommand.State != FormationCommandState.Active;
            if (!wasIdle)
            {
                // 编队正在执行别的命令：纯追加，排在后面，前面终结后由既有的
                // TryActivateNextPending 联动自动提升，这里不用管。
                return targets.Count;
            }

            if (!formation.TryActivateNextPending())
            {
                return 0;
            }

            if (kind == UnitCommandKind.Attack || kind == UnitCommandKind.Guard)
            {
                var targetArray = new SimEntityId[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    targetArray[i] = targets[i];
                }
                int accepted = _sim.IssueCommand(targetArray, rawCommand);
                if (accepted > 0)
                {
                    IssuedCommandCount++;
                }
                return accepted;
            }

            IssuedCommandCount++;
            return targets.Count;
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
                if (pending.FormationId == null)
                {
                    if (_sim.IssueCommand(pending.Targets, pending.Command) > 0)
                    {
                        IssuedCommandCount++;
                        flushed++;
                    }
                    continue;
                }

                if (FlushFormationPendingCommand(pending))
                {
                    flushed++;
                }
            }
            _queued.Clear();
            return flushed;
        }

        /// <summary>M4-R02 最小桥接（DESIGN.md 2.10）：编队路径的排队命令在兑现前必须核对
        /// 编队成员是否与排队时的快照一致（死亡属于正常损耗、已被过滤，不算数）。不一致
        /// （多半是暂停期间玩家重新编组）就整条取消，不下发任何命令、不静默退化为普通移动。</summary>
        private bool FlushFormationPendingCommand(PendingCommand pending)
        {
            var aliveTargets = new List<SimEntityId>(pending.Targets.Length);
            foreach (SimEntityId id in pending.Targets)
            {
                if (IsSelectable(id))
                {
                    aliveTargets.Add(id);
                }
            }

            SquadFormation formation = _formations?.GetFormation(pending.FormationId);
            if (formation == null || !MembersMatch(formation, aliveTargets))
            {
                LastFormationDispatchOutcome = SquadFormationDispatchOutcome.CancelledStaleMembership;
                return false;
            }

            return IssueToFormation(formation, pending.Command.Kind, pending.Command, aliveTargets) > 0;
        }

        public void ClearSelection()
        {
            _selection.Clear();
            _activeFormationId = null;
        }

        /// <summary>把外部选中的单位塞进选择集（验收与调试入口）。同 <see cref="HandleSelectionInput"/>
        /// 一样无条件清空 <see cref="_activeFormationId"/>——这也是一次"改变选择集"的动作，
        /// 不能因为它走的是调试入口就绕过 DESIGN.md 2.3 的规则。</summary>
        public void SelectExplicit(IReadOnlyList<SimEntityId> ids, bool additive = false)
        {
            _activeFormationId = null;
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

        /// <summary>正式战术 UI 与 P 键共用同一套攻击接点循环，避免双份状态机。</summary>
        public SimBodyPartSlot CyclePendingAttackPart()
        {
            _pendingAttackPart = NextAttackPart(_pendingAttackPart);
            return _pendingAttackPart;
        }

        /// <summary>M2-05b 调试输入：None → Primary → Secondary → None 循环。</summary>
        private static SimBodyPartSlot NextAttackPart(SimBodyPartSlot current)
        {
            switch (current)
            {
                case SimBodyPartSlot.None: return SimBodyPartSlot.Primary;
                case SimBodyPartSlot.Primary: return SimBodyPartSlot.Secondary;
                default: return SimBodyPartSlot.None;
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
