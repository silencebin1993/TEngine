using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.MetabolicSlice.Transfer;
using GameLogic.UI.Common;
using UnityEngine;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// §9 验收用 IMGUI 演示：囊满强制丢弃、装/卸、画箭头、Tick 打印 HitEvent。不接正式 UI（本窗不做）。
    /// 调试挂法：GameApp 入口 gameObject.AddComponent&lt;MetabolicSliceDebugGUI&gt;()，不进正式流程。
    /// </summary>
    public sealed class MetabolicSliceDebugGUI : MonoBehaviour
    {
        private BagInventory _bag;
        private SlotGrid _grid;
        private Engine _engine;
        private readonly List<string> _log = new List<string>();
        private PartInstance _pendingOverflow;
        private Rect _windowRect;

        private void Awake()
        {
            _bag = new BagInventory(8);
            _grid = new SlotGrid(SlotType.Cytoplasm);
            _engine = new Engine();
        }

        private void OnGUI()
        {
            if (_windowRect.width <= 0f)
            {
                _windowRect = new Rect(10, 10, 420, 680);
            }
            ImguiDragUtil.DrawDraggable(110, ref _windowRect, "MetabolicSlice Debug (§9)", "metabolic_debug", DrawContent);
        }

        private void DrawContent(int windowId)
        {

            if (_pendingOverflow != null)
            {
                GUILayout.Label($"囊已满，新元素 {_pendingOverflow.CardDefId} 待抉择：");
                if (GUILayout.Button("丢弃新件")) _pendingOverflow = null;
                if (GUILayout.Button("丢弃囊内第一件后收下") && _bag.Items.Count > 0)
                {
                    _bag.Remove(_bag.Items[0].PartId);
                    _bag.TryAdd(_pendingOverflow);
                    _pendingOverflow = null;
                }
            }
            else
            {
                if (GUILayout.Button("掉落 organ_core")) AddDrop("organ_core");
                if (GUILayout.Button("掉落 organ_focus")) AddDrop("organ_focus");
                if (GUILayout.Button("掉落 organ_actuator")) AddDrop("organ_actuator");
            }

            GUILayout.Space(8);
            GUILayout.Label($"储备囊 {_bag.Items.Count}/{_bag.Cap}");
            foreach (var p in _bag.Items)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{p.PartId.Substring(0, 6)} {p.CardDefId}");
                if (GUILayout.Button("装0", GUILayout.Width(50))) Equip(p.PartId, 0);
                if (GUILayout.Button("装1", GUILayout.Width(50))) Equip(p.PartId, 1);
                if (GUILayout.Button("装4", GUILayout.Width(50))) Equip(p.PartId, 4);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("切片 3x3（0,1,2 / 3,4,5 / 6,7,8）");
            for (int y = 0; y < SlotGrid.Height; y++)
            {
                GUILayout.BeginHorizontal();
                for (int x = 0; x < SlotGrid.Width; x++)
                {
                    int id = y * SlotGrid.Width + x;
                    var node = _grid.Slots[id];
                    string label = node.IsEmpty ? $"[{id}] 空" : $"[{id}] {node.Part.CardDefId}";
                    if (GUILayout.Button(label, GUILayout.Width(130))) Unequip(id);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("画箭头 0→1")) TryEdge(0, 1);
            if (GUILayout.Button("画箭头 1→4")) TryEdge(1, 4);
            if (GUILayout.Button("拆边 0→1")) _grid.RemoveEdge(0, 1);
            if (GUILayout.Button("拆边 1→4")) _grid.RemoveEdge(1, 4);

            GUILayout.Space(8);
            if (GUILayout.Button("Tick → ComposeEngine")) RunTick();

            GUILayout.Space(8);
            foreach (var line in _log) GUILayout.Label(line);
        }

        private void AddDrop(string cardDefId)
        {
            var part = new PartInstance(System.Guid.NewGuid().ToString("N"), cardDefId, PartLocation.Bag());
            var result = _bag.TryAdd(part);
            if (result == AddResult.NeedDecision) _pendingOverflow = part;
        }

        private void Equip(string partId, int slotId) =>
            Log(TransferService.Equip(_bag, _grid, partId, slotId).ToString());

        private void Unequip(int slotId) =>
            Log(TransferService.Unequip(_bag, _grid, slotId).ToString());

        private void TryEdge(int from, int to) =>
            Log(_grid.TryAddEdge(from, to) ? $"箭头 {from}->{to} 已画" : $"箭头 {from}->{to} 被拒");

        private void RunTick()
        {
            var runner = new MetabolicSliceRunner(_engine);
            var events = runner.Tick(_grid, System.Array.Empty<IContract>(), new WorldState(), seed: 0);
            Log($"Tick 产出 {events.Count} 个 HitEvent");
            foreach (var evt in events)
                Log($"  dmg={evt.Damage:0.##} tags=[{string.Join(",", evt.Tags)}]");
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > 20) _log.RemoveAt(0);
        }
    }
}
