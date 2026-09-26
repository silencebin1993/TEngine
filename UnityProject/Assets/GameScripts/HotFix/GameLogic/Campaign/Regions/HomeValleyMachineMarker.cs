using System;
using GameLogic.Campaign.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// 一台己方机器在某个地点里的**逻辑句柄**（FG0-ARCH-03 起不再是 MonoBehaviour）。
    ///
    /// 机器的位置、移动（工作赶路 / 编队命令 / 直控）、武器热量与冷却的真相都在该地点的战斗内核（<see cref="CombatSite"/>）里，
    /// 本类只是按 LogicId 找到内核单位的门面。画面对象 <see cref="MachineView"/> 只在地点被观察时存在（DEBT-FG0ARCH01-03 收口）：
    /// 地点不被观察时没有任何渲染器、碰撞体、材质，模拟照常。
    /// 类名沿用 Demo（选择、接管、交互、编队系统都以它为机器的引用），语义从“带 Transform 的表现对象”变为“内核单位句柄”。
    /// </summary>
    public sealed class HomeValleyMachineMarker
    {
        /// <summary>工作赶路的速度与到达半径（Demo HomeValleyMachineMarker 常量）。</summary>
        public const float MoveSpeed = 6f;
        public const float ArrivalDistance = 1.2f;

        public int LogicId { get; }
        public string ChassisId { get; }
        public CombatSite Site { get; }
        public int UnitId { get; }

        /// <summary>画面对象（被观察时才有；不被观察时为 null）。</summary>
        public MachineView View { get; private set; }

        public bool IsSelected { get; private set; }

        /// <summary>工作赶路到达时要执行的回调（Demo 语义：到达那一步执行一次）。</summary>
        public Action PendingArrivalAction { get; private set; }

        public HomeValleyMachineMarker(int logicId, string chassisId, CombatSite site, int unitId)
        {
            LogicId = logicId;
            ChassisId = chassisId;
            Site = site;
            UnitId = unitId;
        }

        /// <summary>机器还在内核里（地点没卸载、单位没被移除）。</summary>
        public bool IsValid => Site != null && Site.UnitExists(UnitId);

        /// <summary>内核里的当前位置（x, z）。单位已不存在时返回最后已知位置。</summary>
        public Vector2 Position
        {
            get
            {
                if (Site != null && Site.TryGetUnitPosition(UnitId, out double2 p))
                {
                    _lastKnown = new Vector2((float)p.x, (float)p.y);
                }
                return _lastKnown;
            }
        }

        /// <summary>内核位置的三维形式（y = 1，与 Demo 表现对象的高度一致；距离比较只看 x / z）。</summary>
        public Vector3 Position3
        {
            get
            {
                Vector2 p = Position;
                return new Vector3(p.x, 1f, p.y);
            }
        }

        /// <summary>双精度位置（远离原点也不丢精度）。</summary>
        public double2 PositionD => Site != null && Site.TryGetUnitPosition(UnitId, out double2 p) ? p : new double2(_lastKnown.x, _lastKnown.y);

        private Vector2 _lastKnown;

        /// <summary>是否在工作赶路中。</summary>
        public bool IsMoving => Site != null && Site.TryGetCommand(UnitId, out var cmd)
                                && cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.WorkMove;

        /// <summary>内核里的工作赶路是带到达回调下达的（<see cref="CombatSite.WorkMoveArrivalTag"/>，随快照进存档）而回调还没挂上——
        /// 读档后由家园按在办的工作订单重挂（<see cref="ResumeArrivalAction"/>），走到目的地照常开工。</summary>
        public bool AwaitsArrivalAction => PendingArrivalAction == null && Site != null && Site.TryGetCommand(UnitId, out var cmd)
                                           && cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.WorkMove && cmd.Target == CombatSite.WorkMoveArrivalTag;

        /// <summary>直接放置（出生、传送）。</summary>
        public void SetPosition(Vector2 position) => Site?.SetUnitPosition(UnitId, position);

        public void CommandMoveTo(Vector3 worldTarget, Action onArrive = null)
        {
            PendingArrivalAction = onArrive;
            Site?.IssueWorkMove(UnitId, new Vector2(worldTarget.x, worldTarget.z), onArrive != null);
        }

        /// <summary>读档后重挂到达回调（不重新下达移动：内核里的赶路原样继续，只补上内存里的回调）。</summary>
        public void ResumeArrivalAction(Action onArrive)
        {
            PendingArrivalAction = onArrive;
        }

        /// <summary>取消工作赶路（只取消工作赶路；编队命令由编队系统自己取消）。</summary>
        public void CancelCommandMove()
        {
            PendingArrivalAction = null;
            if (IsMoving)
            {
                Site.ClearCommand(UnitId);
            }
        }

        /// <summary>直控移动输入（每帧由被观察的地点写入；内核在模拟步里按机器速度移动）。零向量 = 停。</summary>
        public void SetDirectInput(Vector2 direction) => Site?.SetDirectInput(UnitId, direction);

        /// <summary>内核报告“工作赶路到达”：执行一次回调（Demo 到达即回调，回调里可能立刻下一段赶路）。</summary>
        internal void OnWorkArrived()
        {
            Action action = PendingArrivalAction;
            PendingArrivalAction = null;
            action?.Invoke();
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            View?.SetSelected(selected);
        }

        public void AttachView(MachineView view)
        {
            View = view;
            view?.Bind(this);
            view?.SetSelected(IsSelected);
        }

        public void DetachView()
        {
            View = null;
        }
    }
}
