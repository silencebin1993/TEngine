using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameLogic.Campaign.Content;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.MetabolicSlice.Graph;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>通用装/卸/画边结果，镜像仓库既有 <c>FactoryOpResult</c>/<c>MachineOpResult</c> 写法
    /// （Success + 稳定 Code + 人读 Message），不静默失败（IC-REQ-013 Reject-to-safe 也要可见）。</summary>
    public readonly struct CircuitOpResult
    {
        public readonly bool Success;
        public readonly string Code;
        public readonly string Message;

        private CircuitOpResult(bool success, string code, string message)
        {
            Success = success;
            Code = code;
            Message = message;
        }

        public static CircuitOpResult Ok() => new CircuitOpResult(true, null, null);
        public static CircuitOpResult Fail(string code, string message) => new CircuitOpResult(false, code, message);
    }

    /// <summary>PRIMITIVE-FULL-DEMO-SPEC.md §3.4/§3.5 点名的保存期校验问题，供 UI 逐项高亮。</summary>
    public enum CircuitIssueCode
    {
        HasCycle,
        NoValidPath,
        TooManyPaths,
        IsolatedChip,
        LoadExceeded,
        FirmwareTooMany,
        FirmwareUnknown,
        UnknownOrIllegalChip,
    }

    public sealed class CircuitIssue
    {
        public CircuitIssueCode Code;
        public string Message;
        public int[] Slots;
    }

    public sealed class CircuitValidationResult
    {
        public readonly List<CircuitIssue> Issues = new List<CircuitIssue>();
        public int PathCount;
        public bool IsValid => Issues.Count == 0;

        public void Add(CircuitIssueCode code, string message, params int[] slots)
        {
            Issues.Add(new CircuitIssue { Code = code, Message = message, Slots = slots });
        }
    }

    /// <summary>ER4-PRIM-02 STORY-EXECUTION-CARDS.md 第1/2条：正式 3×3 电路板的可变草稿——装/卸芯片、
    /// 画/删有向边、有序固件槽、撤销/重做、校验、签名、与既有 <see cref="SlotGrid"/>/<see cref="PathCompiler"/>
    /// 的投影互转。
    ///
    /// 这是 UI 层（<c>CircuitBoardPanelUIToolkit</c>）与自动化测试共用的唯一编辑模型；UI 不得绕过本类
    /// 直接改 <see cref="BlueprintVersionRecord"/> 字段。编辑的只是草稿——<see cref="ToVersion"/>
    /// 显式调用才落成新版本，不改场上机器（PRIMITIVE-FULL-DEMO-SPEC.md §3.2 第2条）。</summary>
    public sealed class BlueprintCircuitBoard
    {
        public string ChassisId;
        public string PrimaryId;
        public string UtilityId;
        public string StructureId;

        /// <summary>固定长度 2，index0/1 对应两个独立有序固件槽；null/空串＝空槽。</summary>
        public string[] FirmwareSlots = { null, null };

        /// <summary>固定长度 9，index0/8 由 <see cref="SyncFixedSlots"/> 自动维护，玩家不可直接改。</summary>
        public string[] SlotContentIds = new string[BlueprintCircuitLayout.SlotCount];

        /// <summary>ER4-PRIM-03：与 <see cref="SlotContentIds"/> 平行的 9 槽 <c>PrimitiveChipRecord.PartId</c>
        /// 对应表——记录"这个槽具体是仓里哪一个物理实例"，与"槽里装的是什么内容"分开。0/8 固定槽与空闲槽
        /// 恒为 null。本类不直接维护实例账本身（不引用 <c>PrimitiveInventory</c>，避免电路板模型反向依赖
        /// 仓储层）——<see cref="TryPlaceChip"/>/<see cref="TryRemoveChip"/> 只负责"内容变了就清空旧的实例
        /// 关联"这一自愈规则，真正把某实例绑定到某槽是调用方（<c>PrimitiveInventory.TryMoveToDraft</c>）
        /// 在装/卸成功后显式赋值的职责。</summary>
        public string[] SlotPartIds = new string[BlueprintCircuitLayout.SlotCount];

        private readonly List<(int From, int To)> _edges = new List<(int, int)>();
        public IReadOnlyList<(int From, int To)> Edges => _edges;

        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private readonly Stack<Snapshot> _redo = new Stack<Snapshot>();

        public int UndoDepth => _undo.Count;
        public int RedoDepth => _redo.Count;

        // ── 构造 ─────────────────────────────────────────────────────────────────

        public static BlueprintCircuitBoard CreateDefault(string chassisId, string primaryId, string utilityId,
            string structureId, IReadOnlyList<string> orderedFirmwareIds)
        {
            var board = new BlueprintCircuitBoard
            {
                ChassisId = chassisId,
                PrimaryId = primaryId,
                UtilityId = utilityId,
                StructureId = structureId,
            };
            if (orderedFirmwareIds != null)
            {
                for (int i = 0; i < orderedFirmwareIds.Count && i < 2; i++)
                {
                    if (!string.IsNullOrEmpty(orderedFirmwareIds[i]))
                    {
                        board.FirmwareSlots[i] = orderedFirmwareIds[i];
                    }
                }
            }
            board.SyncFixedSlots();
            foreach ((int from, int to) in BlueprintCircuitLayout.DefaultLine)
            {
                board._edges.Add((from, to));
            }
            return board;
        }

        /// <summary>从已保存版本还原草稿。旧 schema（<see cref="BlueprintVersionRecord.CircuitSlotContentIds"/>
        /// 为空）时按 <see cref="CreateDefault"/> 迁入默认合法板——迁移入口见
        /// <see cref="BlueprintCircuitDefaults.EnsureCircuitDataSeeded"/>，本方法本身只做"给定完整数据就还原、
        /// 数据缺失就退回默认"这一件事，不做落盘。</summary>
        public static BlueprintCircuitBoard FromVersion(BlueprintVersionRecord version)
        {
            if (version == null)
            {
                return CreateDefault(null, null, null, null, Array.Empty<string>());
            }

            if (version.CircuitSlotContentIds == null || version.CircuitSlotContentIds.Length != BlueprintCircuitLayout.SlotCount)
            {
                return CreateDefault(version.ChassisId, version.PrimaryId, version.UtilityId, version.StructureId,
                    version.OrderedFirmwareIds);
            }

            var board = new BlueprintCircuitBoard
            {
                ChassisId = version.ChassisId,
                PrimaryId = version.PrimaryId,
                UtilityId = version.UtilityId,
                StructureId = version.StructureId,
            };
            // JsonUtility 对 string[] 里的 null 元素落盘/读回后会变成 ""（无法表达数组内 null 的既有
            // 局限），必须在读入时立即把 "" 规整回 null，否则 ComputeSignature 等处的 `?? "_"` 判断
            // 会把序列化前(null)与序列化后(空串)当成两种不同状态，导致签名在"第一次存读档"后就漂移
            // （execute_code 20 次往返回归测得的真实 bug，已修复）。
            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                string raw = version.CircuitSlotContentIds[i];
                board.SlotContentIds[i] = string.IsNullOrEmpty(raw) ? null : raw;
            }

            string[] fw = version.OrderedFirmwareIds ?? Array.Empty<string>();
            for (int i = 0; i < fw.Length && i < 2; i++)
            {
                board.FirmwareSlots[i] = string.IsNullOrEmpty(fw[i]) ? null : fw[i];
            }

            if (version.CircuitEdges != null)
            {
                foreach (BlueprintCircuitEdgeRecord e in version.CircuitEdges)
                {
                    board._edges.Add((e.From, e.To));
                }
            }

            // ER4-PRIM-03：同样的 JsonUtility null→"" 规整问题，且旧档（本字段落地前保存的版本）该
            // 数组本身可能是 null——两种情况都保持全 null（board.SlotPartIds 字段初始值），不臆造实例。
            if (version.CircuitSlotPartIds != null && version.CircuitSlotPartIds.Length == BlueprintCircuitLayout.SlotCount)
            {
                for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
                {
                    string raw = version.CircuitSlotPartIds[i];
                    board.SlotPartIds[i] = string.IsNullOrEmpty(raw) ? null : raw;
                }
            }

            board.SyncFixedSlots();
            return board;
        }

        /// <summary>把草稿写成一条新版本记录（追加语义，version 号由调用方决定，通常是
        /// <c>BlueprintRecord.Versions.Length+1</c>）。不做校验——调用方（UI 的"保存"按钮）必须先
        /// <see cref="Validate"/> 通过才允许调用本方法，这里只负责忠实序列化当前草稿状态。</summary>
        public BlueprintVersionRecord ToVersion(int version, float createdAtPlaySeconds)
        {
            SyncFixedSlots();
            var sortedEdges = _edges.OrderBy(e => e.From).ThenBy(e => e.To)
                .Select(e => new BlueprintCircuitEdgeRecord(e.From, e.To)).ToArray();

            return new BlueprintVersionRecord
            {
                Version = version,
                ChassisId = ChassisId,
                PrimaryId = PrimaryId,
                UtilityId = UtilityId,
                StructureId = StructureId,
                OrderedFirmwareIds = new[] { FirmwareSlots[0] ?? string.Empty, FirmwareSlots[1] ?? string.Empty },
                WorkPriorityTemplate = null,
                DoctrineId = null,
                ScrapCost = ComputeScrapCost(),
                PowerCost = 0,
                BandwidthCost = ComputeBandwidthCost(),
                HeatBudget = 0f,
                FactionTags = Array.Empty<string>(),
                CircuitSlotTypes = BlueprintCircuitLayout.BuildSlotTypes(),
                CircuitSlotContentIds = (string[])SlotContentIds.Clone(),
                CircuitSlotPartIds = (string[])SlotPartIds.Clone(),
                CircuitEdges = sortedEdges,
                ContentVersionAtCompile = CampaignSaveService.CurrentContentVersion,
                CompileSignature = ComputeSignature(),
                CreatedAtPlaySeconds = createdAtPlaySeconds,
            };
        }

        // ── 固定槽同步 ───────────────────────────────────────────────────────────

        /// <summary>0 号源槽保持玩家最后一次选择（默认 <see cref="BlueprintCircuitChipCatalog.DefaultSourceContentId"/>，
        /// 非法/为空时回退默认，不留空）；8 号汇槽永远由 <see cref="PrimaryId"/> 派生，玩家装/卸操作
        /// 完全不影响它——每次改动后调用本方法保持两者与外层装配一致。</summary>
        public void SyncFixedSlots()
        {
            if (!BlueprintCircuitChipCatalog.IsValidSourceContent(SlotContentIds[BlueprintCircuitLayout.SourceSlot]))
            {
                SlotContentIds[BlueprintCircuitLayout.SourceSlot] = BlueprintCircuitChipCatalog.DefaultSourceContentId;
            }
            SlotContentIds[BlueprintCircuitLayout.SinkSlot] = BlueprintCircuitChipCatalog.ResolveSinkContentId(PrimaryId);
            // ER4-PRIM-03：0/8 号固定槽由底盘电源/主组件派生，从不占用基元仓实例——防御性清空，
            // 避免旧数据或调用方误写留下的悬空 PartId 关联。
            SlotPartIds[BlueprintCircuitLayout.SourceSlot] = null;
            SlotPartIds[BlueprintCircuitLayout.SinkSlot] = null;
        }

        /// <summary>玩家在正式电路板 UI 里对 0 号槽做的唯一操作——从已知合法源内容中选择
        /// （当前 CardCatalog 有 organ_core/org_mito/org_chloro 三条，见 DEBT-ER4PRIM01-01 裁决）。</summary>
        public CircuitOpResult TrySetSource(string contentId)
        {
            if (!BlueprintCircuitChipCatalog.IsValidSourceContent(contentId))
            {
                return CircuitOpResult.Fail("unknown_source", $"'{contentId}' 不是合法的能源内容。");
            }
            CaptureUndo();
            SlotContentIds[BlueprintCircuitLayout.SourceSlot] = contentId;
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        // ── 装/卸芯片（1～7）─────────────────────────────────────────────────────

        public CircuitOpResult TryPlaceChip(int slot, string contentId)
        {
            if (BlueprintCircuitLayout.IsFixedSlot(slot))
            {
                return CircuitOpResult.Fail("fixed_slot", "0 号源槽/8 号汇槽不可直接装卸，由外层装配或 TrySetSource 决定。");
            }
            if (slot < 0 || slot >= BlueprintCircuitLayout.SlotCount)
            {
                return CircuitOpResult.Fail("slot_out_of_range", $"槽位 {slot} 越界。");
            }
            SlotType slotType = BlueprintCircuitLayout.SlotTypeAt(slot);
            if (!BlueprintCircuitChipCatalog.IsValidChipContent(contentId, slotType))
            {
                return CircuitOpResult.Fail("illegal_chip",
                    $"'{contentId}' 不能装入 {slot} 号槽（类型 {slotType}）——未解锁、非法内容或槽型不匹配。");
            }
            // ER4-PRIM-03 execute_code 实测发现的真实缺陷：本方法此前对已占用槽位无条件覆盖写
            // SlotContentIds[slot]，不做"槽位已空"校验。ER4-PRIM-02 自身校验从未测过这个场景（当时
            // 槽内容只是字符串，覆盖不产生可观察后果），但基元仓接入真实实例账后，覆盖会让旧实例的
            // PrimitiveChipRecord 停留在 Draft/DraftSlot 却再也没有真正占着这个槽——实例账"仓/草稿/
            // 待领取恰处一地"的不变量被打破（AC-PRM-006）。必须先卸下已占用内容（TryRemoveChip）才能
            // 装新内容，与 UI 上"装/卸是两个显式动作"的既有设计一致，不是回归而是补齐一直缺失的校验。
            if (!string.IsNullOrEmpty(SlotContentIds[slot]))
            {
                return CircuitOpResult.Fail("slot_occupied", $"{slot} 号槽已装有内容，请先卸下再装入新内容。");
            }
            CaptureUndo();
            SlotContentIds[slot] = contentId;
            // ER4-PRIM-03：新内容落进这个槽，旧的实例关联（如果有）不再有效——真正把新内容绑定到某个
            // 仓内实例是调用方（PrimitiveInventory.TryMoveToDraft）在本方法返回 Success 后显式赋值
            // SlotPartIds[slot] 的职责，这里只负责不留悬空引用。
            SlotPartIds[slot] = null;
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        public CircuitOpResult TryRemoveChip(int slot)
        {
            if (BlueprintCircuitLayout.IsFixedSlot(slot))
            {
                return CircuitOpResult.Fail("fixed_slot", "0 号源槽/8 号汇槽不可移除。");
            }
            if (slot < 0 || slot >= BlueprintCircuitLayout.SlotCount)
            {
                return CircuitOpResult.Fail("slot_out_of_range", $"槽位 {slot} 越界。");
            }
            if (string.IsNullOrEmpty(SlotContentIds[slot]))
            {
                return CircuitOpResult.Fail("slot_already_empty", $"{slot} 号槽已经是空的。");
            }
            CaptureUndo();
            SlotContentIds[slot] = null;
            SlotPartIds[slot] = null;
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        // ── 画/删边 ──────────────────────────────────────────────────────────────

        public CircuitOpResult TryAddEdge(int from, int to)
        {
            SlotGrid probe = ToSlotGrid();
            if (!probe.TryAddEdge(from, to))
            {
                string reason = DescribeEdgeRejection(from, to, probe);
                return CircuitOpResult.Fail("illegal_edge", reason);
            }
            CaptureUndo();
            _edges.Add((from, to));
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        public CircuitOpResult TryRemoveEdge(int from, int to)
        {
            int before = _edges.Count;
            var kept = _edges.Where(e => !(e.From == from && e.To == to)).ToList();
            if (kept.Count == before)
            {
                return CircuitOpResult.Fail("edge_not_found", $"不存在 {from}→{to} 这条边。");
            }
            CaptureUndo();
            _edges.Clear();
            _edges.AddRange(kept);
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        private static string DescribeEdgeRejection(int from, int to, SlotGrid grid)
        {
            if (from == to) return "不能连接槽位到自己。";
            if (from < 0 || from >= BlueprintCircuitLayout.SlotCount || to < 0 || to >= BlueprintCircuitLayout.SlotCount)
                return "槽位越界。";
            if (!SlotGrid.IsAdjacent(from, to)) return $"{from} 号与 {to} 号槽不是四邻，不能连接。";
            foreach (DirectedEdge e in grid.Edges)
            {
                if (e.From == from && e.To == to) return $"{from}→{to} 这条边已存在，不能重复添加。";
            }
            return "已达到边数软帽（非空槽数×2），请先移除一条边或装更多芯片。";
        }

        // ── 固件 ─────────────────────────────────────────────────────────────────

        public CircuitOpResult TrySetFirmware(int index, string firmwareId)
        {
            if (index != 0 && index != 1)
            {
                return CircuitOpResult.Fail("firmware_index_out_of_range", "固件槽只有 0/1 两个。");
            }
            if (!FirmwareCatalog.TryGet(firmwareId, out MechanicalContentDef def) || def.LegacyFacadeId == null)
            {
                return CircuitOpResult.Fail("firmware_unknown", $"'{firmwareId}' 不是已知固件或无可编译等价实现。");
            }
            CaptureUndo();
            FirmwareSlots[index] = firmwareId;
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        public CircuitOpResult TryClearFirmware(int index)
        {
            if (index != 0 && index != 1)
            {
                return CircuitOpResult.Fail("firmware_index_out_of_range", "固件槽只有 0/1 两个。");
            }
            CaptureUndo();
            FirmwareSlots[index] = null;
            CommitUndo();
            return CircuitOpResult.Ok();
        }

        // ── 撤销/重做 ────────────────────────────────────────────────────────────

        private sealed class Snapshot
        {
            public string[] Slots;
            public string[] PartIds;
            public List<(int, int)> Edges;
            public string[] Firmware;
        }

        private Snapshot _pendingUndo;

        private void CaptureUndo()
        {
            _pendingUndo = new Snapshot
            {
                Slots = (string[])SlotContentIds.Clone(),
                PartIds = (string[])SlotPartIds.Clone(),
                Edges = new List<(int, int)>(_edges),
                Firmware = (string[])FirmwareSlots.Clone(),
            };
        }

        private void CommitUndo()
        {
            if (_pendingUndo == null)
            {
                return;
            }
            _undo.Push(_pendingUndo);
            _pendingUndo = null;
            while (_undo.Count > BlueprintCircuitLayout.MaxUndoRedoSteps)
            {
                // Stack 没有 RemoveOldest；转数组裁剪最旧的一条（栈底）后重建，20 步以内代价可忽略。
                var arr = _undo.ToArray(); // [0]=最新...[n-1]=最旧
                _undo.Clear();
                for (int i = arr.Length - 2; i >= 0; i--) _undo.Push(arr[i]);
            }
            _redo.Clear();
        }

        public bool Undo()
        {
            if (_undo.Count == 0)
            {
                return false;
            }
            var redoSnap = new Snapshot
            {
                Slots = (string[])SlotContentIds.Clone(),
                PartIds = (string[])SlotPartIds.Clone(),
                Edges = new List<(int, int)>(_edges),
                Firmware = (string[])FirmwareSlots.Clone(),
            };
            Snapshot prev = _undo.Pop();
            Apply(prev);
            _redo.Push(redoSnap);
            while (_redo.Count > BlueprintCircuitLayout.MaxUndoRedoSteps)
            {
                var arr = _redo.ToArray();
                _redo.Clear();
                for (int i = arr.Length - 2; i >= 0; i--) _redo.Push(arr[i]);
            }
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0)
            {
                return false;
            }
            var undoSnap = new Snapshot
            {
                Slots = (string[])SlotContentIds.Clone(),
                PartIds = (string[])SlotPartIds.Clone(),
                Edges = new List<(int, int)>(_edges),
                Firmware = (string[])FirmwareSlots.Clone(),
            };
            Snapshot next = _redo.Pop();
            Apply(next);
            _undo.Push(undoSnap);
            return true;
        }

        private void Apply(Snapshot s)
        {
            SlotContentIds = (string[])s.Slots.Clone();
            SlotPartIds = (string[])s.PartIds.Clone();
            _edges.Clear();
            _edges.AddRange(s.Edges);
            FirmwareSlots = (string[])s.Firmware.Clone();
        }

        // ── 校验 ─────────────────────────────────────────────────────────────────

        /// <summary>PRIMITIVE-FULL-DEMO-SPEC.md §3.4/§3.5：环/不可达/>4路径/孤立芯片/负载/固件数量与合法性
        /// 全量校验，一次返回所有问题供 UI 同时高亮（不是遇到第一个就停）。</summary>
        public CircuitValidationResult Validate()
        {
            SyncFixedSlots();
            var result = new CircuitValidationResult();

            if (HasCycle(out int[] cycleSlots))
            {
                result.Add(CircuitIssueCode.HasCycle, "电路存在环，不能保存。", cycleSlots);
            }

            for (int slot = 1; slot < BlueprintCircuitLayout.SlotCount - 1; slot++)
            {
                if (BlueprintCircuitLayout.IsFixedSlot(slot)) continue;
                if (string.IsNullOrEmpty(SlotContentIds[slot])) continue;
                bool touched = _edges.Any(e => e.From == slot || e.To == slot);
                if (!touched)
                {
                    result.Add(CircuitIssueCode.IsolatedChip, $"{slot} 号槽装了芯片但未连接任何导线（未通电）。", slot);
                }
            }

            bool hasResolvableSink = !string.IsNullOrEmpty(SlotContentIds[BlueprintCircuitLayout.SinkSlot]);
            if (hasResolvableSink)
            {
                SlotGrid grid = ToSlotGrid();
                List<PathCompiler.CompiledPath> paths = PathCompiler.Compile(grid);
                result.PathCount = paths.Count;
                if (paths.Count == 0)
                {
                    result.Add(CircuitIssueCode.NoValidPath, "没有从源到汇的合法路径，无法保存。");
                }
                else if (paths.Count > BlueprintCircuitLayout.MaxPaths)
                {
                    result.Add(CircuitIssueCode.TooManyPaths,
                        $"有效路径 {paths.Count} 条，超过上限 {BlueprintCircuitLayout.MaxPaths} 条。");
                }
            }

            if (!string.IsNullOrEmpty(FirmwareSlots[0]) && !FirmwareCatalog.TryGet(FirmwareSlots[0], out _))
            {
                result.Add(CircuitIssueCode.FirmwareUnknown, $"固件槽0 内容 '{FirmwareSlots[0]}' 未知。");
            }
            if (!string.IsNullOrEmpty(FirmwareSlots[1]) && !FirmwareCatalog.TryGet(FirmwareSlots[1], out _))
            {
                result.Add(CircuitIssueCode.FirmwareUnknown, $"固件槽1 内容 '{FirmwareSlots[1]}' 未知。");
            }

            if (TryComputeLoad(out int totalLoad, out int? capacity) && capacity.HasValue && totalLoad > capacity.Value)
            {
                result.Add(CircuitIssueCode.LoadExceeded, $"负载 {totalLoad} 超过底盘容量 {capacity.Value}。");
            }

            return result;
        }

        private bool HasCycle(out int[] cycleSlots)
        {
            var color = new int[BlueprintCircuitLayout.SlotCount]; // 0=white,1=gray,2=black
            var adjacency = new List<int>[BlueprintCircuitLayout.SlotCount];
            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++) adjacency[i] = new List<int>();
            foreach ((int from, int to) in _edges) adjacency[from].Add(to);

            var stack = new List<int>();
            for (int start = 0; start < BlueprintCircuitLayout.SlotCount; start++)
            {
                if (color[start] != 0) continue;
                if (Visit(start))
                {
                    cycleSlots = stack.ToArray();
                    return true;
                }
            }
            cycleSlots = Array.Empty<int>();
            return false;

            bool Visit(int node)
            {
                color[node] = 1;
                stack.Add(node);
                foreach (int next in adjacency[node])
                {
                    if (color[next] == 1) return true;
                    if (color[next] == 0 && Visit(next)) return true;
                }
                stack.RemoveAt(stack.Count - 1);
                color[node] = 2;
                return false;
            }
        }

        private bool TryComputeLoad(out int totalLoad, out int? capacity)
        {
            totalLoad = 0;
            capacity = null;
            string archetype = ChassisCatalog.ResolveArchetype(ChassisId) ?? ChassisId;
            if (archetype != null && ChassisCatalog.TryGet(archetype, out MechanicalContentDef chassisDef))
            {
                capacity = chassisDef.Load;
            }
            totalLoad += SumLoad(PrimaryId) + SumLoad(UtilityId) + SumLoad(StructureId)
                + SumLoad(FirmwareSlots[0]) + SumLoad(FirmwareSlots[1]);
            return true;
        }

        private static int SumLoad(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return 0;
            return MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def) ? def.Load : 0;
        }

        private int ComputeScrapCost()
        {
            int cost = 0;
            string archetype = ChassisCatalog.ResolveArchetype(ChassisId) ?? ChassisId;
            if (archetype != null && ChassisCatalog.TryGet(archetype, out MechanicalContentDef chassisDef))
            {
                cost += chassisDef.ScrapCost;
            }
            cost += SumScrap(PrimaryId) + SumScrap(UtilityId) + SumScrap(StructureId)
                + SumScrap(FirmwareSlots[0]) + SumScrap(FirmwareSlots[1]);
            return cost;
        }

        private static int SumScrap(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return 0;
            return MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def) ? def.ScrapCost : 0;
        }

        private int ComputeBandwidthCost()
        {
            // DEMO-CONTENT-LOCK.md §2.4："信号中继...机体带宽需求+1"——目前唯一有明确带宽数字的组件。
            return StructureId == ComponentCatalog.StructRelayId ? 1 : 0;
        }

        // ── 签名 ─────────────────────────────────────────────────────────────────

        /// <summary>PRIMITIVE-FULL-DEMO-SPEC.md §3.5 第5条："同一内容版本、9 槽、边方向、固件顺序和
        /// 外层装配必须得到同一签名"。按槽号/边/固件稳定排序后拼接，人读字符串即签名（不需要不透明哈希，
        /// 仓库既有 <c>CompileSignature = blueprintId + ":v1"</c> 先例同样是可读字符串）。</summary>
        public string ComputeSignature()
        {
            SyncFixedSlots();
            var sb = new StringBuilder();
            sb.Append("CS|");
            sb.Append(ChassisId).Append('|').Append(PrimaryId).Append('|').Append(UtilityId).Append('|').Append(StructureId);
            sb.Append("|FW:").Append(FirmwareSlots[0] ?? "_").Append(',').Append(FirmwareSlots[1] ?? "_");
            sb.Append("|SLOTS:");
            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(SlotContentIds[i] ?? "_");
            }
            sb.Append("|EDGES:");
            foreach ((int from, int to) in _edges.OrderBy(e => e.From).ThenBy(e => e.To))
            {
                sb.Append(from).Append('-').Append(to).Append(';');
            }
            sb.Append("|CV").Append(CampaignSaveService.CurrentContentVersion);
            return sb.ToString();
        }

        // ── 投影：复用既有 SlotGrid/PathCompiler ────────────────────────────────────

        /// <summary>把草稿投影成既有 <see cref="SlotGrid"/> 实例，复用其 <c>TryAddEdge</c>（邻接+软帽校验）
        /// 与 <see cref="PathCompiler"/> 的路径枚举——PRIMITIVE-FULL-DEMO-SPEC.md §2 原文"运行时投影为既有
        /// SlotGrid，不能两套图各自结算"。每次调用都重建一份新实例（校验/预览用，非共享可变状态）。</summary>
        public SlotGrid ToSlotGrid()
        {
            var grid = new SlotGrid(BlueprintCircuitLayout.SlotTypeAt(0));
            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                grid.Slots[i].SlotType = BlueprintCircuitLayout.SlotTypeAt(i);
                string contentId = SlotContentIds[i];
                if (!string.IsNullOrEmpty(contentId) && CardCatalog.Get(contentId) != null)
                {
                    grid.Slots[i].Part = new PartInstance("circuit_slot_" + i, contentId, PartLocation.Slot(i));
                }
            }
            foreach ((int from, int to) in _edges)
            {
                grid.TryAddEdge(from, to);
            }
            return grid;
        }
    }
}
