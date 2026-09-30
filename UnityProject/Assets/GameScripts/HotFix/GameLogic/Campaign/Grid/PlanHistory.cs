using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>FG3-LOG-07：撤销栈里一个操作的种类（存档里按整数存：只追加，不重排）。</summary>
    public enum PlanOpKind
    {
        None = 0,
        /// <summary>放下一座建筑虚影（撤销 = 还没建成就取消、已经建成就生成拆除任务）。</summary>
        PlaceBuilding = 1,
        /// <summary>取消了一座建筑虚影（撤销 = 在原处重放）。</summary>
        CancelBuilding = 2,
        /// <summary>标记拆除（撤销 = 取消标记；已经拆掉了就在原处放回虚影）。</summary>
        DemolishMark = 3,
        /// <summary>取消了拆除标记（撤销 = 重新标记）。</summary>
        DemolishUnmark = 4,
        /// <summary>搬迁一座已建成的建筑（撤销 = 取消搬迁；已经搬完了就生成搬回原位的搬迁任务）。</summary>
        Relocate = 5,
        /// <summary>挪动一个还没开工的规划（撤销 = 挪回去）。</summary>
        MovePlan = 6,
        /// <summary>原地旋转一座建筑（撤销 = 转回去）。</summary>
        Rotate = 7,
        /// <summary>放下了一批物流件虚影（撤销 = 还是虚影的取消、已经建成的立即拆掉全额返还）。</summary>
        PiecesPlaced = 8,
        /// <summary>拆掉 / 取消了一批物流件（撤销 = 在原处按原设置放回虚影）。</summary>
        PiecesRemoved = 9,
        /// <summary>升级一座建筑（撤销 = 取消升级；已经完工的不能撤销）。</summary>
        UpgradeBuilding = 10,
        /// <summary>升级一组物流件（撤销 = 取消还没升完的部分，已到的差额材料退回）。</summary>
        UpgradePieces = 11,
        /// <summary>复制设置写到了一件东西上（撤销 = 写回原来的设置）。</summary>
        Settings = 12,
        /// <summary>原地反转一格传送带（撤销 = 再反转一次）。</summary>
        ReverseBelt = 13,
        /// <summary>原地转了一个分流器 / 合流器 90°（撤销 = 再转三次）。</summary>
        RotateNode = 14,
        /// <summary>阀门调头（撤销 = 再调一次）。</summary>
        ReverseValve = 15,
    }

    /// <summary>FG3-LOG-07：一步的名字（撤销 / 重做提示写“已撤销：粘贴”）。</summary>
    public enum PlanStepKind
    {
        None = 0,
        Place = 1,
        Demolish = 2,
        Relocate = 3,
        Rotate = 4,
        Belts = 5,
        Remove = 6,
        Batch = 7,
        Paste = 8,
        Upgrade = 9,
        Settings = 10,
        Cancel = 11,
    }

    /// <summary>一次撤销 / 重做的结果（状态行与自检读）。</summary>
    public sealed class PlanStepResult
    {
        public bool Done;
        public bool Redo;
        public PlanStepKind Step;
        public int Ops;
        public int Changed;
        public int Skipped;
        /// <summary>撤销碰到已经建成的建筑：生成的拆除任务数（FGR-LOG-009“生成拆除任务，并提示玩家”）。</summary>
        public int DemolishTasks;
        /// <summary>撤销已经搬完的搬迁：生成的“搬回原位”任务数。</summary>
        public int RelocateTasks;
        /// <summary>撤销已经建成的物流件：立即拆掉的件数（全额返还）。</summary>
        public int RemovedBuilt;
        public string FirstSkip;
        public Vector2 At;
        public string Text = string.Empty;
    }

    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-009“规划类操作可以撤销和重做：放置虚影、取消虚影、拆除标记、搬迁；已经完工的建筑被撤销时生成拆除任务并提示玩家；撤销栈至少 50 步”；
    /// FG13 FGR-UX-003；FGT-LOG-004）：建造规划的撤销 / 重做栈，唯一写入口。
    ///
    /// 纪律：
    /// - 建造模式的每个规划操作都经本类的包装入口（<see cref="Place"/>、<see cref="ToggleDemolish"/>、<see cref="Relocate"/>、<see cref="PlaceBeltPath"/>、
    ///   <see cref="RemoveCells"/>、<see cref="Paste"/>、<see cref="Upgrade"/>……），它们调用正式服务并在成功后记一步；复合操作（粘贴、框选拆除、升级）整体算一步。
    /// - 撤销 / 重做按“当下的状态”执行，不回放快照：还是虚影的取消、已经建成的生成拆除任务（不会瞬间消失，FG03 第 5 节负向）、已经不在的跳过并写明原因。
    ///   这样撤销永远不会凭空变出或吞掉材料——一切都走同一套全额返还 / 施工规则。
    /// - 栈在存档里（<see cref="GridState.PlanUndo"/> / <see cref="GridState.PlanRedo"/>），读档后接着撤销；保留 plan.undo_depth（50）步，
    ///   单步超过 plan.layout_max_entries 件（例如框选拆掉上万格传送带）的操作不进栈，调用方先确认并写明“不能撤销”（B04）。
    /// 每一步 O(这一步的件数)；不在每帧跑。
    /// </summary>
    public static class PlanHistory
    {
        private static int _openStep;
        private static int _openDepth;
        private static PlanStepKind _openKind;
        private static int _openCount;
        /// <summary>正在撤销 / 重做的那一步（已经从栈里取出、还没放进另一个栈；重映射 ID 时也要改它）。</summary>
        private static List<PlanOpRecord> _inflight;

        public static int Depth => Math.Max(1, GridContent.TuningInt("plan.undo_depth"));

        /// <summary>单步最多记多少件（超过就不进栈，见类说明）。</summary>
        public static int StepMaxEntries => PlanningService.MaxEntries;

        /// <summary>最近一次记录被丢弃（单步太大）。</summary>
        public static bool LastStepTooLarge { get; private set; }

        /// <summary>栈内容变化时 +1（建造栏据此刷新撤销按钮）。</summary>
        public static int Revision { get; private set; }

        public static void ResetSession()
        {
            _openStep = 0;
            _openDepth = 0;
            _openKind = PlanStepKind.None;
            _openCount = 0;
            LastStepTooLarge = false;
            Revision++;
        }

        private static List<PlanOpRecord> UndoList(CampaignState state) => state.Grid.PlanUndo ??= new List<PlanOpRecord>();

        private static List<PlanOpRecord> RedoList(CampaignState state) => state.Grid.PlanRedo ??= new List<PlanOpRecord>();

        /// <summary>撤销栈里有几步。</summary>
        public static int UndoSteps(CampaignState state) => CountSteps(state?.Grid?.PlanUndo);

        public static int RedoSteps(CampaignState state) => CountSteps(state?.Grid?.PlanRedo);

        private static int CountSteps(List<PlanOpRecord> list)
        {
            if (list == null || list.Count == 0)
            {
                return 0;
            }
            int n = 0;
            int last = int.MinValue;
            foreach (PlanOpRecord op in list)
            {
                if (op.Step != last)
                {
                    n++;
                    last = op.Step;
                }
            }
            return n;
        }

        /// <summary>撤销栈里快照的件数合计（存档体积的上界估计用）。</summary>
        public static int EntryCount(CampaignState state)
        {
            int n = 0;
            foreach (PlanOpRecord op in state?.Grid?.PlanUndo ?? new List<PlanOpRecord>())
            {
                n += 1 + PlanEntries.CountOf(op.Entries) + PlanEntries.CountOf(op.Before);
            }
            foreach (PlanOpRecord op in state?.Grid?.PlanRedo ?? new List<PlanOpRecord>())
            {
                n += 1 + PlanEntries.CountOf(op.Entries) + PlanEntries.CountOf(op.Before);
            }
            return n;
        }

        /// <summary>下一次撤销 / 重做是哪一步（HUD 写“撤销：粘贴”）。没有时为 None。</summary>
        public static PlanStepKind PeekUndo(CampaignState state) => Peek(state?.Grid?.PlanUndo);

        public static PlanStepKind PeekRedo(CampaignState state) => Peek(state?.Grid?.PlanRedo);

        private static PlanStepKind Peek(List<PlanOpRecord> list) => list == null || list.Count == 0 ? PlanStepKind.None : (PlanStepKind)list[list.Count - 1].StepKind;

        public static string StepName(PlanStepKind k) => GameText.Get("plan.step." + k.ToString().ToLowerInvariant());

        // ── 记录 ────────────────────────────────────────────────────────────────

        /// <summary>开始一步复合操作（嵌套时沿用最外层）。必须与 <see cref="EndStep"/> 成对。</summary>
        public static void BeginStep(CampaignState state, PlanStepKind kind)
        {
            if (state?.Grid == null)
            {
                return;
            }
            if (_openDepth++ == 0)
            {
                _openStep = state.Grid.NextPlanStep++;
                _openKind = kind;
                _openCount = 0;
                LastStepTooLarge = false;
            }
        }

        public static void EndStep(CampaignState state)
        {
            if (_openDepth <= 0)
            {
                return;
            }
            if (--_openDepth > 0)
            {
                return;
            }
            int step = _openStep;
            _openStep = 0;
            if (state?.Grid == null)
            {
                return;
            }
            List<PlanOpRecord> undo = UndoList(state);
            int entries = 0;
            int first = undo.Count;
            while (first > 0 && undo[first - 1].Step == step)
            {
                first--;
                entries += 1 + PlanEntries.CountOf(undo[first].Entries) + PlanEntries.CountOf(undo[first].Before);
            }
            if (first == undo.Count)
            {
                return; // 这一步什么也没成：不进栈、不清重做
            }
            if (entries > StepMaxEntries + 64)
            {
                undo.RemoveRange(first, undo.Count - first);
                LastStepTooLarge = true;
                RedoList(state).Clear();
                Revision++;
                return;
            }
            RedoList(state).Clear();
            Trim(state);
            Revision++;
        }

        private static void Add(CampaignState state, PlanOpRecord op)
        {
            if (state?.Grid == null || op == null)
            {
                return;
            }
            bool standalone = _openDepth == 0;
            if (standalone)
            {
                BeginStep(state, KindForSingle((PlanOpKind)op.Kind));
            }
            op.Step = _openStep;
            op.StepKind = (int)_openKind;
            UndoList(state).Add(op);
            _openCount++;
            if (standalone)
            {
                EndStep(state);
            }
        }

        private static PlanStepKind KindForSingle(PlanOpKind k)
        {
            switch (k)
            {
                case PlanOpKind.PlaceBuilding: return PlanStepKind.Place;
                case PlanOpKind.CancelBuilding: return PlanStepKind.Cancel;
                case PlanOpKind.DemolishMark:
                case PlanOpKind.DemolishUnmark: return PlanStepKind.Demolish;
                case PlanOpKind.Relocate:
                case PlanOpKind.MovePlan: return PlanStepKind.Relocate;
                case PlanOpKind.Rotate:
                case PlanOpKind.ReverseBelt:
                case PlanOpKind.RotateNode:
                case PlanOpKind.ReverseValve: return PlanStepKind.Rotate;
                case PlanOpKind.PiecesPlaced: return PlanStepKind.Belts;
                case PlanOpKind.PiecesRemoved: return PlanStepKind.Remove;
                case PlanOpKind.UpgradeBuilding:
                case PlanOpKind.UpgradePieces: return PlanStepKind.Upgrade;
                case PlanOpKind.Settings: return PlanStepKind.Settings;
                default: return PlanStepKind.None;
            }
        }

        private static void Trim(CampaignState state)
        {
            List<PlanOpRecord> undo = UndoList(state);
            int steps = CountSteps(undo);
            int depth = Depth;
            while (steps > depth && undo.Count > 0)
            {
                int step = undo[0].Step;
                int k = 0;
                while (k < undo.Count && undo[k].Step == step)
                {
                    k++;
                }
                undo.RemoveRange(0, k);
                steps--;
            }
        }

        private static PlanOpRecord BuildingOp(PlanOpKind kind, BuildingRecord b) => new PlanOpRecord
        {
            Kind = (int)kind,
            BuildingId = b.BuildingId,
            TypeId = b.BuildingTypeId,
            X = b.GridX,
            Y = b.GridY,
            Rot = GridMath.NormalizeRotation(b.Rotation),
            S0 = PlanSettings.FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) == PlanSettings.Family.Power ? Math.Max(1, b.PowerPriority) : 0,
        };

        private sealed class Snapshot
        {
            public string Id;
            public string Type;
            public int X;
            public int Y;
            public int Rot;
            public int S0;
        }

        private static Snapshot Snap(CampaignState state, string id)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, id);
            return b == null ? null : new Snapshot
            {
                Id = b.BuildingId,
                Type = b.BuildingTypeId,
                X = b.GridX,
                Y = b.GridY,
                Rot = GridMath.NormalizeRotation(b.Rotation),
                S0 = PlanSettings.FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) == PlanSettings.Family.Power ? Math.Max(1, b.PowerPriority) : 0,
            };
        }

        private static PlanOpRecord SnapOp(PlanOpKind kind, Snapshot s) => new PlanOpRecord
        {
            Kind = (int)kind, BuildingId = s.Id, TypeId = s.Type, X = s.X, Y = s.Y, Rot = s.Rot, S0 = s.S0,
        };

        // ── 包装入口（建造模式经这些做规划操作）────────────────────────────────────

        /// <summary>放下一座建筑虚影（<paramref name="s0"/> = 吸管带来的电力优先级，0 = 默认）。</summary>
        public static GridOpResult Place(CampaignState state, string typeId, GridCell pivot, int rotation, int s0 = 0)
        {
            GridOpResult r = HomeGridService.TryPlace(state, typeId, pivot, rotation);
            if (r.Success)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                if (b != null)
                {
                    if (s0 > 0 && PlanSettings.FamilyOf(PlanEntryKind.Building, b.BuildingTypeId) == PlanSettings.Family.Power)
                    {
                        b.PowerPriority = Math.Max(1, Math.Min(4, s0));
                    }
                    Add(state, BuildingOp(PlanOpKind.PlaceBuilding, b));
                }
            }
            return r;
        }

        /// <summary>拆除模式点一座建筑（切换语义：标记 / 取消标记 / 取消虚影 / 取消升级），成功后按结果记一步。</summary>
        public static GridOpResult ToggleDemolish(CampaignState state, string buildingId)
        {
            Snapshot before = Snap(state, buildingId);
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, buildingId);
            Snapshot ghostSnap = ghost != null ? Snap(state, ghost.BuildingId) : null;
            GridOpResult r = HomeGridService.TryToggleDemolish(state, buildingId);
            if (before == null || !r.Success)
            {
                return r;
            }
            switch (r.Outcome)
            {
                case GridOpResult.Kind.DemolishMarked:
                    Add(state, SnapOp(PlanOpKind.DemolishMark, before));
                    break;
                case GridOpResult.Kind.DemolishUnmarked:
                    Add(state, SnapOp(PlanOpKind.DemolishUnmark, before));
                    break;
                case GridOpResult.Kind.PlanCancelled:
                    string cancelled = r.BuildingId ?? buildingId;
                    if (cancelled.EndsWith(HomeGridService.UpgradeGhostSuffix, StringComparison.Ordinal))
                    {
                        // 取消升级（拆除模式点升级中的建筑 / 施工队列里取消升级虚影）：撤销 = 重新升级。
                        string source = cancelled.Substring(0, cancelled.Length - HomeGridService.UpgradeGhostSuffix.Length);
                        Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.UpgradeBuilding, BuildingId = source, TypeId = ghostSnap?.Type ?? before.Type, Flag = 1 });
                    }
                    else if (cancelled.EndsWith(HomeGridService.RelocationGhostSuffix, StringComparison.Ordinal))
                    {
                        // 取消搬迁（点的是搬迁目标虚影）：撤销 = 重新搬过去。
                        string origin = cancelled.Substring(0, cancelled.Length - HomeGridService.RelocationGhostSuffix.Length);
                        Snapshot src = Snap(state, origin);
                        if (src != null)
                        {
                            Add(state, new PlanOpRecord
                            {
                                Kind = (int)PlanOpKind.Relocate, BuildingId = origin, TypeId = src.Type, X = src.X, Y = src.Y, Rot = src.Rot,
                                X2 = before.X, Y2 = before.Y, Rot2 = before.Rot, Flag = 1,
                            });
                        }
                    }
                    else
                    {
                        Add(state, SnapOp(PlanOpKind.CancelBuilding, before));
                    }
                    break;
            }
            return r;
        }

        /// <summary>搬迁（已建成：组合任务；还没开工的规划：直接挪）。</summary>
        public static GridOpResult Relocate(CampaignState state, string buildingId, GridCell pivot, int rotation)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            BuildingRecord ghost = b != null ? HomeGridService.FindRelocationGhost(state, b.BuildingId) : null;
            BuildingRecord moving = ghost ?? b;
            Snapshot before = moving != null ? Snap(state, moving.BuildingId) : null;
            GridOpResult r = HomeGridService.TryRelocate(state, buildingId, pivot, rotation);
            if (!r.Success || before == null || b == null)
            {
                return r;
            }
            int rot = GridMath.NormalizeRotation(rotation);
            if (r.Outcome == GridOpResult.Kind.RelocationPlanned)
            {
                Add(state, new PlanOpRecord
                {
                    Kind = (int)PlanOpKind.Relocate, BuildingId = b.BuildingId, TypeId = b.BuildingTypeId, X = before.X, Y = before.Y, Rot = before.Rot,
                    X2 = pivot.X, Y2 = pivot.Y, Rot2 = rot,
                });
            }
            else if (r.Outcome == GridOpResult.Kind.PlanMoved)
            {
                Add(state, new PlanOpRecord
                {
                    Kind = (int)PlanOpKind.MovePlan, BuildingId = r.BuildingId, TypeId = before.Type, X = before.X, Y = before.Y, Rot = before.Rot,
                    X2 = pivot.X, Y2 = pivot.Y, Rot2 = rot,
                });
            }
            return r;
        }

        public static GridOpResult Rotate(CampaignState state, string buildingId)
        {
            Snapshot before = Snap(state, buildingId);
            GridOpResult r = HomeGridService.TryRotate(state, buildingId);
            if (r.Success && before != null)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
                PlanOpRecord op = SnapOp(PlanOpKind.Rotate, before);
                op.Rot2 = GridMath.NormalizeRotation(b?.Rotation ?? before.Rot);
                Add(state, op);
            }
            return r;
        }

        /// <summary>拖拽 / 单击铺设物流件；吸管带来的设置（<paramref name="s0"/>…）写进新规划，建成时生效。</summary>
        public static GridOpResult PlaceBeltPath(CampaignState state, string toolId, GridCell from, GridCell to, BeltDir dir, int s0 = 0, int s1 = 0, int s2 = 0)
        {
            GridOpResult r = HomeGridService.TryPlaceBeltPath(state, toolId, from, to, dir);
            if (!r.Success || HomeGridService.LastPlanId == null)
            {
                return r;
            }
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, HomeValleyConstruction.BeltPlanPrefix + HomeGridService.LastPlanId);
            if (p == null)
            {
                return r;
            }
            PlanEntryKind kind = PlanEntries.KindOf(toolId, out _);
            if (s0 != 0 || s1 != 0 || s2 != 0)
            {
                PlanSettings.ApplyToPlan(p, kind, s0, s1, s2);
            }
            var b = new PlanEntries.Builder();
            if (kind == PlanEntryKind.Underground && p.Xs.Length >= 2)
            {
                b.Add(toolId, p.Xs[0], p.Ys[0], p.Dirs[0], p.Xs[1], p.Ys[1]);
            }
            else
            {
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    b.Add(toolId, p.Xs[i], p.Ys[i], p.Dirs[i], s0: s0, s1: s1, s2: s2);
                }
            }
            Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesPlaced, PlanId = p.PlanId, Entries = b.Build() });
            return r;
        }

        /// <summary>拆掉一批物流件格（建成的全额返还、虚影取消）；先记下它们，撤销时按原设置放回虚影。</summary>
        public static GridOpResult RemoveCells(CampaignState state, List<GridCell> cells)
        {
            PlanEntryBlock snap = PlanningService.SnapshotCells(state, cells);
            GridOpResult r = HomeGridService.TryRemoveBelts(state, cells);
            if (r.Success && snap.Count > 0)
            {
                Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesRemoved, Entries = snap });
            }
            return r;
        }

        /// <summary>框选拆除（一步：逐座标记 / 取消虚影 + 拆掉物流件）。</summary>
        public static GridOpResult ExecuteDemolishBox(CampaignState state, DemolishBoxPlan plan)
        {
            if (state == null || plan == null)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            BeginStep(state, PlanStepKind.Batch);
            try
            {
                var marks = new List<Snapshot>();
                foreach (string id in plan.ToMark)
                {
                    if (!HomeGridService.IsMarkedForDemolish(state, id) && Snap(state, id) is Snapshot s)
                    {
                        marks.Add(s);
                    }
                }
                var cancels = new List<Snapshot>();
                foreach (string id in plan.ToCancel)
                {
                    if (Snap(state, id) is Snapshot s)
                    {
                        cancels.Add(s);
                    }
                }
                PlanEntryBlock pieces = plan.Belts.Count > 0 ? PlanningService.SnapshotCells(state, plan.Belts) : null;
                GridOpResult r = HomeGridService.ExecuteDemolishBox(state, plan);
                if (!r.Success)
                {
                    return r;
                }
                foreach (Snapshot s in marks)
                {
                    if (HomeGridService.IsMarkedForDemolish(state, s.Id))
                    {
                        Add(state, SnapOp(PlanOpKind.DemolishMark, s));
                    }
                }
                foreach (Snapshot s in cancels)
                {
                    if (HomeGridService.FindBuilding(state, s.Id) == null)
                    {
                        Add(state, SnapOp(PlanOpKind.CancelBuilding, s));
                    }
                }
                if (pieces != null && pieces.Count > 0 && HomeGridService.LastBatchBelts > 0)
                {
                    Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesRemoved, Entries = pieces });
                }
                return r;
            }
            finally
            {
                EndStep(state);
            }
        }

        public static BeltOpResult ReverseBelt(CampaignState state, GridCell cell)
        {
            BeltOpResult r = BeltNetworkService.TryReverse(state, cell);
            if (r.Ok)
            {
                Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.ReverseBelt, X = cell.X, Y = cell.Y });
            }
            return r;
        }

        public static BeltOpResult RotateNode(CampaignState state, GridCell cell)
        {
            BeltOpResult r = BeltNetworkService.TryRotateNode(state, cell);
            if (r.Ok)
            {
                Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.RotateNode, X = cell.X, Y = cell.Y });
            }
            return r;
        }

        public static PipeOpResult ReverseValve(CampaignState state, GridCell cell)
        {
            PipeOpResult r = PipeNetworkService.TryReverseValve(state, cell);
            if (r.Ok)
            {
                Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.ReverseValve, X = cell.X, Y = cell.Y });
            }
            return r;
        }

        /// <summary>粘贴（一步）：放下规划里合法的件，记下放了什么。</summary>
        public static PlanApplyResult Paste(CampaignState state, PastePlan plan)
        {
            BeginStep(state, PlanStepKind.Paste);
            try
            {
                PlanApplyResult r = PlanningService.Apply(state, plan);
                RecordApply(state, r);
                if (r.Placed > 0)
                {
                    Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstPaste);
                }
                return r;
            }
            finally
            {
                EndStep(state);
            }
        }

        private static void RecordApply(CampaignState state, PlanApplyResult r)
        {
            foreach (string id in r.BuildingIds)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                if (b != null)
                {
                    Add(state, BuildingOp(PlanOpKind.PlaceBuilding, b));
                }
            }
            if (r.Pieces.Count > 0)
            {
                Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesPlaced, Entries = r.Pieces.Build() });
            }
        }

        /// <summary>升级规划（一步）：建筑逐座生成升级任务，物流件每组一份升级规划。返回生成的件数。</summary>
        public static int Upgrade(CampaignState state, UpgradeBoxPlan plan, out GridReason? firstFailure)
        {
            firstFailure = null;
            int done = 0;
            BeginStep(state, PlanStepKind.Upgrade);
            try
            {
                foreach (string id in plan.Buildings)
                {
                    BuildingRecord b = HomeGridService.FindBuilding(state, id);
                    string from = b?.BuildingTypeId;
                    GridOpResult r = HomeGridService.TryUpgrade(state, id);
                    if (!r.Success)
                    {
                        firstFailure ??= r.Placement != null && r.Placement.Reasons.Count > 0 ? r.Placement.Reasons[0] : r.Reason;
                        continue;
                    }
                    done++;
                    BuildingRecord ghost = HomeGridService.FindBuilding(state, r.BuildingId);
                    Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.UpgradeBuilding, BuildingId = id, TypeId = ghost?.BuildingTypeId, PlanId = from });
                }
                foreach (UpgradeGroup g in plan.Groups)
                {
                    string planId = UpgradePlanner.PlanGroup(state, g);
                    done += g.Pieces;
                    var e = new PlanEntries.Builder();
                    for (int i = 0; i < g.Cells.Count; i++)
                    {
                        if (g.Kind == PlanEntryKind.Underground && i % 2 == 1)
                        {
                            continue;
                        }
                        GridCell exit = g.Kind == PlanEntryKind.Underground ? g.Cells[i + 1] : default;
                        e.Add(g.FromId, g.Cells[i].X, g.Cells[i].Y, g.Dirs[i], exit.X, exit.Y);
                    }
                    Add(state, new PlanOpRecord
                    {
                        Kind = (int)PlanOpKind.UpgradePieces, PlanId = planId, TypeId = g.ToId, X = g.FromTier, Y = g.ToTier, S0 = g.DiffPerUnit, Entries = e.Build(),
                    });
                }
                if (done > 0)
                {
                    Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstUpgrade);
                }
                return done;
            }
            finally
            {
                EndStep(state);
            }
        }

        /// <summary>复制设置写到 (cell) 上（一步；不兼容时不记）。</summary>
        public static bool ApplySettings(CampaignState state, GridCell cell, PlanSettings.Clip clip, out string targetName, out GridReason reason)
        {
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (!PlanSettings.TryApply(state, cell, clip, out PlanSettings.Clip before, out targetName, out reason))
            {
                return false;
            }
            var after = new PlanEntries.Builder();
            after.Add(clip.Family.ToString(), cell.X, cell.Y, 0, s0: clip.S0, s1: clip.S1, s2: clip.S2);
            var old = new PlanEntries.Builder();
            old.Add(before.Family.ToString(), cell.X, cell.Y, 0, s0: before.S0, s1: before.S1, s2: before.S2);
            Add(state, new PlanOpRecord
            {
                Kind = (int)PlanOpKind.Settings, BuildingId = b?.BuildingId, X = cell.X, Y = cell.Y, Flag = (int)clip.Family, Entries = after.Build(), Before = old.Build(),
            });
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstCopySettings);
            return true;
        }

        /// <summary>施工队列里点“取消”（取消虚影 / 传送带规划 / 升级）：也是规划操作，进撤销栈。</summary>
        public static bool CancelSite(CampaignState state, WorkOrderRecord order)
        {
            if (state == null || order == null)
            {
                return false;
            }
            if (HomeValleyConstruction.IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, order.TargetId);
                PlanEntryBlock snap = null;
                if (p != null && !p.Upgrade)
                {
                    var cells = new List<GridCell>();
                    for (int i = 0; i < p.Xs.Length; i++)
                    {
                        if (p.CellState[i] == 0)
                        {
                            cells.Add(new GridCell(p.Xs[i], p.Ys[i]));
                        }
                    }
                    snap = PlanningService.SnapshotCells(state, cells);
                }
                bool ok = HomeValleyConstruction.CancelSite(state, order); // 取消物流件的升级规划不进撤销栈（FGR-LOG-009 的范围是放置 / 取消虚影、拆除标记、搬迁）
                if (ok && snap != null && snap.Count > 0)
                {
                    Add(state, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesRemoved, Entries = snap });
                }
                return ok;
            }
            BuildingRecord ghost = HomeGridService.FindBuilding(state, order.TargetId);
            if (ghost != null)
            {
                return ToggleDemolish(state, ghost.BuildingId).Success;
            }
            return HomeValleyConstruction.CancelSite(state, order);
        }

        // ── 撤销 / 重做 ──────────────────────────────────────────────────────────

        public static PlanStepResult Undo(CampaignState state) => Replay(state, redo: false);

        public static PlanStepResult Redo(CampaignState state) => Replay(state, redo: true);

        private static PlanStepResult Replay(CampaignState state, bool redo)
        {
            var result = new PlanStepResult { Redo = redo };
            if (state?.Grid == null)
            {
                result.Text = GameText.Get(redo ? "plan.redo.none" : "plan.undo.none");
                return result;
            }
            List<PlanOpRecord> from = redo ? RedoList(state) : UndoList(state);
            List<PlanOpRecord> to = redo ? UndoList(state) : RedoList(state);
            if (from.Count == 0)
            {
                result.Text = GameText.Get(redo ? "plan.redo.none" : "plan.undo.none");
                return result;
            }
            int step = from[from.Count - 1].Step;
            int first = from.Count;
            while (first > 0 && from[first - 1].Step == step)
            {
                first--;
            }
            var ops = from.GetRange(first, from.Count - first);
            from.RemoveRange(first, from.Count - first);
            _inflight = ops;
            result.Step = (PlanStepKind)ops[0].StepKind;
            result.Ops = ops.Count;
            if (redo)
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    Forward(state, ops[i], result);
                }
            }
            else
            {
                for (int i = ops.Count - 1; i >= 0; i--)
                {
                    Backward(state, ops[i], result);
                }
            }
            to.AddRange(ops);
            _inflight = null;
            if (!redo)
            {
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstUndo);
            }
            Trim(state);
            result.Done = true;
            result.Text = Describe(result);
            Revision++;
            HomeValleyConstruction.Touch();
            if (result.DemolishTasks > 0 || result.RelocateTasks > 0 || result.RemovedBuilt > 0)
            {
                // FGR-LOG-009“已经完工的建筑被撤销时，生成拆除任务，并提示玩家”：通知带定位，同类聚合。
                Notifications.NotificationCenter.Post("plan_undo", result.Text, new Vector3(result.At.x, 0f, result.At.y));
            }
            return result;
        }

        private static string Describe(PlanStepResult r)
        {
            string head = GameText.Format(r.Redo ? "plan.redo.done" : "plan.undo.done", StepName(r.Step));
            var parts = new List<string>(4);
            if (r.DemolishTasks > 0)
            {
                parts.Add(GameText.Format("plan.undo.demolish_tasks", r.DemolishTasks));
            }
            if (r.RelocateTasks > 0)
            {
                parts.Add(GameText.Format("plan.undo.relocate_tasks", r.RelocateTasks));
            }
            if (r.RemovedBuilt > 0)
            {
                parts.Add(GameText.Format("plan.undo.removed_built", r.RemovedBuilt));
            }
            if (r.Skipped > 0)
            {
                parts.Add(GameText.Format("plan.undo.skipped", r.Skipped, r.FirstSkip ?? string.Empty));
            }
            return parts.Count == 0 ? head : head + GameText.Get("plan.undo.sep") + string.Join(GameText.Get("plan.undo.sep"), parts);
        }

        private static void SkipOp(PlanStepResult r, string reason)
        {
            r.Skipped++;
            r.FirstSkip ??= reason;
        }

        private static void Mark(PlanStepResult r, Vector2 at)
        {
            if (r.Changed == 0)
            {
                r.At = at;
            }
            r.Changed++;
        }

        /// <summary>撤销一个操作（把它做过的事反过来）。</summary>
        private static void Backward(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            switch ((PlanOpKind)op.Kind)
            {
                case PlanOpKind.PlaceBuilding:
                case PlanOpKind.DemolishUnmark:
                    EnsureRemoved(state, op, r);
                    break;
                case PlanOpKind.CancelBuilding:
                case PlanOpKind.DemolishMark:
                    EnsurePresent(state, op, r);
                    break;
                case PlanOpKind.Relocate:
                    RelocateBack(state, op, r);
                    break;
                case PlanOpKind.MovePlan:
                    MoveTo(state, op, op.X, op.Y, op.Rot, r);
                    break;
                case PlanOpKind.Rotate:
                    RotateTo(state, op, op.Rot, r);
                    break;
                case PlanOpKind.PiecesPlaced:
                    RemoveEntries(state, op, r);
                    break;
                case PlanOpKind.PiecesRemoved:
                    PlaceEntries(state, op, r);
                    break;
                case PlanOpKind.UpgradeBuilding:
                    if (op.Flag == 1)
                    {
                        UpgradeAgain(state, op, r); // 那一步是“取消升级”：撤销 = 重新升级
                    }
                    else
                    {
                        CancelUpgrade(state, op, r);
                    }
                    break;
                case PlanOpKind.UpgradePieces:
                    CancelPieceUpgrade(state, op, r);
                    break;
                case PlanOpKind.Settings:
                    ApplySettingsBlock(state, op, op.Before, r);
                    break;
                case PlanOpKind.ReverseBelt:
                    Cell(r, BeltNetworkService.TryReverse(state, new GridCell(op.X, op.Y)).Ok, op, "plan.reason.piece_gone_plain");
                    break;
                case PlanOpKind.RotateNode:
                    var c = new GridCell(op.X, op.Y);
                    bool ok = true;
                    for (int i = 0; i < 3 && ok; i++)
                    {
                        ok = BeltNetworkService.TryRotateNode(state, c).Ok;
                    }
                    Cell(r, ok, op, "plan.reason.piece_gone_plain");
                    break;
                case PlanOpKind.ReverseValve:
                    Cell(r, PipeNetworkService.TryReverseValve(state, new GridCell(op.X, op.Y)).Ok, op, "plan.reason.piece_gone_plain");
                    break;
            }
        }

        /// <summary>重做一个操作（再做一遍）。</summary>
        private static void Forward(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            switch ((PlanOpKind)op.Kind)
            {
                case PlanOpKind.PlaceBuilding:
                case PlanOpKind.DemolishUnmark:
                    EnsurePresent(state, op, r);
                    break;
                case PlanOpKind.CancelBuilding:
                case PlanOpKind.DemolishMark:
                    EnsureRemoved(state, op, r);
                    break;
                case PlanOpKind.Relocate:
                    RelocateAgain(state, op, r);
                    break;
                case PlanOpKind.MovePlan:
                    MoveTo(state, op, op.X2, op.Y2, op.Rot2, r);
                    break;
                case PlanOpKind.Rotate:
                    RotateTo(state, op, op.Rot2, r);
                    break;
                case PlanOpKind.PiecesPlaced:
                    PlaceEntries(state, op, r);
                    break;
                case PlanOpKind.PiecesRemoved:
                    RemoveEntries(state, op, r);
                    break;
                case PlanOpKind.UpgradeBuilding:
                    if (op.Flag == 1)
                    {
                        CancelUpgrade(state, op, r);
                    }
                    else
                    {
                        UpgradeAgain(state, op, r);
                    }
                    break;
                case PlanOpKind.UpgradePieces:
                    PieceUpgradeAgain(state, op, r);
                    break;
                case PlanOpKind.Settings:
                    ApplySettingsBlock(state, op, op.Entries, r);
                    break;
                case PlanOpKind.ReverseBelt:
                    Cell(r, BeltNetworkService.TryReverse(state, new GridCell(op.X, op.Y)).Ok, op, "plan.reason.piece_gone_plain");
                    break;
                case PlanOpKind.RotateNode:
                    Cell(r, BeltNetworkService.TryRotateNode(state, new GridCell(op.X, op.Y)).Ok, op, "plan.reason.piece_gone_plain");
                    break;
                case PlanOpKind.ReverseValve:
                    Cell(r, PipeNetworkService.TryReverseValve(state, new GridCell(op.X, op.Y)).Ok, op, "plan.reason.piece_gone_plain");
                    break;
            }
        }

        private static void Cell(PlanStepResult r, bool ok, PlanOpRecord op, string failKey)
        {
            if (ok)
            {
                Mark(r, new Vector2(op.X, op.Y));
            }
            else
            {
                SkipOp(r, GameText.Format(failKey, op.X, op.Y));
            }
        }

        /// <summary>记录里的建筑现在是不是“还是那一座”：ID 对得上、类型对得上、还在记录的位置（ID 可能被同类的新建筑复用）。</summary>
        private static BuildingRecord Same(CampaignState state, PlanOpRecord op)
        {
            BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
            return b != null && b.BuildingTypeId == op.TypeId && b.GridX == op.X && b.GridY == op.Y ? b : null;
        }

        private static string Gone(PlanOpRecord op) => GameText.Format("plan.reason.building_gone", HomeGridService.DisplayName(op.TypeId), op.X, op.Y);

        /// <summary>让这座建筑“不在”：还是虚影的取消（已到材料全额退回）；已经建成的生成拆除任务（机器上门拆，全额返还），不会瞬间消失。</summary>
        private static void EnsureRemoved(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            BuildingRecord b = Same(state, op);
            if (b == null)
            {
                SkipOp(r, Gone(op));
                return;
            }
            if (HomeValleyController.IsPlannedGhost(b))
            {
                GridOpResult c = HomeGridService.TryToggleDemolish(state, b.BuildingId);
                if (c.Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    Mark(r, b.Position);
                }
                else
                {
                    SkipOp(r, c.Describe());
                }
                return;
            }
            if (HomeGridService.IsMarkedForDemolish(state, b.BuildingId))
            {
                Mark(r, b.Position);
                return;
            }
            if (HomeGridService.FindRelocationGhost(state, b.BuildingId) is BuildingRecord moving)
            {
                SkipOp(r, GridReason.Of(HomeGridService.IsUpgradeGhost(moving) ? GridBlockReason.Upgrading : GridBlockReason.Relocating).Describe());
                return;
            }
            GridOpResult m = HomeGridService.TryToggleDemolish(state, b.BuildingId);
            if (m.Outcome == GridOpResult.Kind.DemolishMarked)
            {
                Mark(r, b.Position);
                r.DemolishTasks++;
            }
            else
            {
                SkipOp(r, m.Describe());
            }
        }

        /// <summary>让这座建筑“在”：被标记拆除的取消标记；已经不在了就在原处放回虚影（新 ID 写回记录）。</summary>
        private static void EnsurePresent(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            BuildingRecord b = Same(state, op);
            if (b != null)
            {
                if (HomeGridService.IsMarkedForDemolish(state, b.BuildingId))
                {
                    HomeGridService.TryToggleDemolish(state, b.BuildingId);
                }
                Mark(r, b.Position);
                return;
            }
            GridOpResult p = HomeGridService.TryPlace(state, op.TypeId, new GridCell(op.X, op.Y), op.Rot);
            if (!p.Success)
            {
                SkipOp(r, GameText.Format("plan.reason.cannot_restore", HomeGridService.DisplayName(op.TypeId), p.Describe()));
                return;
            }
            Remap(state, op.BuildingId, p.BuildingId);
            op.BuildingId = p.BuildingId;
            BuildingRecord placed = HomeGridService.FindBuilding(state, p.BuildingId);
            if (placed != null && op.S0 > 0)
            {
                placed.PowerPriority = Math.Max(1, Math.Min(4, op.S0));
            }
            Mark(r, placed?.Position ?? new Vector2(op.X, op.Y));
        }

        /// <summary>
        /// 放回的虚影拿到的是新 ID（同类建筑的实例序号往后编）：栈里所有还指着旧 ID 的操作改指新 ID，
        /// 否则“放回 → 再撤销 / 重做后面那几步（挪动、旋转、取消……）”会找不到它。O(栈长)，只在放回时。
        /// </summary>
        private static void Remap(CampaignState state, string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId) || oldId == newId)
            {
                return;
            }
            foreach (List<PlanOpRecord> list in new[] { state.Grid.PlanUndo, state.Grid.PlanRedo, _inflight })
            {
                if (list == null)
                {
                    continue;
                }
                foreach (PlanOpRecord o in list)
                {
                    if (o.BuildingId == oldId)
                    {
                        o.BuildingId = newId;
                    }
                }
            }
        }

        private static void RelocateBack(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
            if (b == null)
            {
                SkipOp(r, Gone(op));
                return;
            }
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, b.BuildingId);
            if (op.Flag == 1)
            {
                // 那一步是“取消搬迁”：撤销 = 重新搬过去。
                GridOpResult again = HomeGridService.TryRelocate(state, b.BuildingId, new GridCell(op.X2, op.Y2), op.Rot2);
                if (again.Success)
                {
                    Mark(r, b.Position);
                }
                else
                {
                    SkipOp(r, again.Describe());
                }
                return;
            }
            if (ghost != null && !HomeGridService.IsUpgradeGhost(ghost))
            {
                GridOpResult c = HomeGridService.TryToggleDemolish(state, ghost.BuildingId);
                if (c.Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    Mark(r, b.Position);
                }
                else
                {
                    SkipOp(r, c.Describe()); // 已经开工的搬迁不能取消
                }
                return;
            }
            if (b.GridX == op.X2 && b.GridY == op.Y2)
            {
                // 已经搬完：生成搬回原位的搬迁任务（不会瞬移）。
                GridOpResult back = HomeGridService.TryRelocate(state, b.BuildingId, new GridCell(op.X, op.Y), op.Rot);
                if (back.Success)
                {
                    Mark(r, b.Position);
                    r.RelocateTasks++;
                }
                else
                {
                    SkipOp(r, back.Describe());
                }
                return;
            }
            SkipOp(r, Gone(op));
        }

        private static void RelocateAgain(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
            if (b == null)
            {
                SkipOp(r, Gone(op));
                return;
            }
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, b.BuildingId);
            if (op.Flag == 1)
            {
                // 那一步是“取消搬迁”：重做 = 再取消一次。
                if (ghost != null && !HomeGridService.IsUpgradeGhost(ghost) && HomeGridService.TryToggleDemolish(state, ghost.BuildingId).Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    Mark(r, b.Position);
                }
                else
                {
                    SkipOp(r, Gone(op));
                }
                return;
            }
            if (ghost != null && !HomeGridService.IsUpgradeGhost(ghost) && ghost.GridX == op.X && ghost.GridY == op.Y && b.GridX == op.X2 && b.GridY == op.Y2)
            {
                // 撤销时生成的“搬回原位”还没开工：取消它就回到了重做后的样子。
                if (HomeGridService.TryToggleDemolish(state, ghost.BuildingId).Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    Mark(r, b.Position);
                    return;
                }
            }
            GridOpResult again = HomeGridService.TryRelocate(state, b.BuildingId, new GridCell(op.X2, op.Y2), op.Rot2);
            if (again.Success)
            {
                Mark(r, b.Position);
            }
            else
            {
                SkipOp(r, again.Describe());
            }
        }

        private static void MoveTo(CampaignState state, PlanOpRecord op, int x, int y, int rot, PlanStepResult r)
        {
            GridOpResult m = HomeGridService.TryRelocate(state, op.BuildingId, new GridCell(x, y), rot);
            if (m.Success || m.FirstReason == GridBlockReason.RelocateSame)
            {
                Mark(r, new Vector2(x, y));
            }
            else
            {
                SkipOp(r, m.Describe());
            }
        }

        private static void RotateTo(CampaignState state, PlanOpRecord op, int rot, PlanStepResult r)
        {
            GridOpResult m = HomeGridService.TryRotateTo(state, op.BuildingId, rot);
            if (m.Success)
            {
                Mark(r, new Vector2(op.X, op.Y));
            }
            else
            {
                SkipOp(r, m.Describe());
            }
        }

        private static void RemoveEntries(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            int n = PlanEntries.CountOf(op.Entries);
            if (n == 0)
            {
                return;
            }
            PlanningService.RemovePieces(state, op.Entries);
            int changed = PlanningService.LastRemovedBuilt + PlanningService.LastCancelledPlanned;
            if (changed > 0)
            {
                Mark(r, new Vector2(op.Entries.Xs[0], op.Entries.Ys[0]));
                r.Changed += changed - 1;
            }
            r.RemovedBuilt += PlanningService.LastRemovedBuilt;
            if (PlanningService.LastRemoveSkipped > 0)
            {
                r.Skipped += PlanningService.LastRemoveSkipped - 1;
                SkipOp(r, PlanningService.LastRemoveSkipReason?.Describe() ?? string.Empty);
            }
        }

        private static readonly PastePlan ReplayPlan = new PastePlan();

        private static void PlaceEntries(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            if (PlanEntries.CountOf(op.Entries) == 0)
            {
                return;
            }
            PastePlan plan = PlanningService.PlanPaste(state, op.Entries, default, 0, absolute: true, into: ReplayPlan);
            PlanApplyResult a = PlanningService.Apply(state, plan);
            if (a.Placed > 0)
            {
                Mark(r, new Vector2(op.Entries.Xs[0], op.Entries.Ys[0]));
                r.Changed += a.Placed - 1;
            }
            int bad = plan.BadCount + a.Failed;
            if (bad > 0)
            {
                r.Skipped += bad - 1;
                SkipOp(r, plan.BadCount > 0 ? plan.DescribeFirstBad() : GameText.Format("plan.reason.cannot_restore", a.FirstFailureName, a.FirstFailure?.Describe() ?? string.Empty));
            }
        }

        private static void CancelUpgrade(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
            BuildingRecord ghost = b != null ? HomeGridService.FindRelocationGhost(state, b.BuildingId) : null;
            if (ghost != null && HomeGridService.IsUpgradeGhost(ghost))
            {
                GridOpResult c = HomeGridService.TryToggleDemolish(state, ghost.BuildingId);
                if (c.Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    Mark(r, b.Position);
                    return;
                }
                SkipOp(r, c.Describe());
                return;
            }
            SkipOp(r, b != null && b.BuildingTypeId == op.TypeId
                ? GameText.Format("plan.reason.upgrade_done", HomeGridService.DisplayName(op.TypeId))
                : Gone(op));
        }

        private static void UpgradeAgain(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            GridOpResult u = HomeGridService.TryUpgrade(state, op.BuildingId);
            BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
            if (u.Success)
            {
                Mark(r, b?.Position ?? Vector2.zero);
            }
            else
            {
                SkipOp(r, u.Describe());
            }
        }

        private static void CancelPieceUpgrade(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            WorkOrderRecord order = op.PlanId != null ? HomeValleyWorkOrders.FindActiveBuild(state, HomeValleyConstruction.BeltPlanPrefix + op.PlanId) : null;
            if (order == null)
            {
                SkipOp(r, GameText.Format("plan.reason.upgrade_done", PlanEntries.NameOf(op.TypeId)));
                return;
            }
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, order.TargetId);
            int built = HomeValleyConstruction.BuiltCells(p);
            if (HomeValleyConstruction.CancelSite(state, order))
            {
                Mark(r, new Vector2(op.Entries.Xs.Length > 0 ? op.Entries.Xs[0] : 0, op.Entries.Ys.Length > 0 ? op.Entries.Ys[0] : 0));
                if (built > 0)
                {
                    SkipOp(r, GameText.Format("plan.reason.upgrade_partly_done", built));
                }
            }
            else
            {
                SkipOp(r, GameText.Format("plan.reason.upgrade_done", PlanEntries.NameOf(op.TypeId)));
            }
        }

        private static void PieceUpgradeAgain(CampaignState state, PlanOpRecord op, PlanStepResult r)
        {
            int n = PlanEntries.CountOf(op.Entries);
            PlanEntryKind kind = PlanEntries.KindOf(n > 0 ? op.Entries.Ids[0] : null, out _);
            var cells = new List<GridCell>(n);
            var dirs = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                var c = new GridCell(op.Entries.Xs[i], op.Entries.Ys[i]);
                bool still = kind == PlanEntryKind.Pipe
                    ? PipeNetworkService.TryGetPiece(c, out PipePieceKind pk, out int pt) && pk == PipePieceKind.Pipe && pt == op.X
                    : BeltNetworkService.TryGetPiece(c, out BeltNodeKind bk, out int bt) && bt == op.X
                      && (kind == PlanEntryKind.Underground ? bk == BeltNodeKind.UndergroundIn : bk == BeltNodeKind.Belt)
                      && !HomeValleyConstruction.TryFindUpgradeCell(state, c, out _, out _);
                if (!still)
                {
                    SkipOp(r, GameText.Format("plan.reason.piece_gone", PlanEntries.NameOf(op.Entries.Ids[i]), c.X, c.Y));
                    continue;
                }
                cells.Add(c);
                dirs.Add(op.Entries.Rots[i]);
                if (kind == PlanEntryKind.Underground)
                {
                    cells.Add(new GridCell(op.Entries.X2s[i], op.Entries.Y2s[i]));
                    dirs.Add(op.Entries.Rots[i]);
                    if (cells.Count == 2)
                    {
                        break; // 地下传送带一条一份
                    }
                }
            }
            if (cells.Count == 0)
            {
                return;
            }
            op.PlanId = HomeValleyConstruction.PlanUpgrade(state, cells, dirs, kind == PlanEntryKind.Underground ? BeltNodeKind.UndergroundIn : BeltNodeKind.Belt,
                kind == PlanEntryKind.Pipe, op.X, op.Y, op.S0);
            Mark(r, new Vector2(cells[0].X, cells[0].Y));
        }

        private static void ApplySettingsBlock(CampaignState state, PlanOpRecord op, PlanEntryBlock block, PlanStepResult r)
        {
            if (PlanEntries.CountOf(block) == 0)
            {
                return;
            }
            var clip = new PlanSettings.Clip { Family = (PlanSettings.Family)op.Flag, S0 = block.S0[0], S1 = block.S1[0], S2 = block.S2[0] };
            var cell = new GridCell(op.X, op.Y);
            bool ok;
            if (clip.Family == PlanSettings.Family.Power)
            {
                BuildingRecord b = op.BuildingId != null ? HomeGridService.FindBuilding(state, op.BuildingId) : null;
                ok = b != null && HomeValleyPowerGrid.TrySetPriority(state, b.BuildingId, Math.Max(1, Math.Min(4, clip.S0))).Success;
            }
            else
            {
                ok = PlanSettings.TryReadPiece(state, cell, out PlanEntryKind kind, out _, out _, out _, out _)
                     && PlanSettings.ApplyToPiece(state, cell, kind, clip.S0, clip.S1, clip.S2);
            }
            if (ok)
            {
                Mark(r, new Vector2(cell.X, cell.Y));
            }
            else
            {
                SkipOp(r, GameText.Format("plan.reason.piece_gone_plain", cell.X, cell.Y));
            }
        }
    }
}
