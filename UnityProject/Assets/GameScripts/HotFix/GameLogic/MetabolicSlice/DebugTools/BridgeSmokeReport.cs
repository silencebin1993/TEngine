using System.Collections.Generic;
using System.Linq;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-005：Bridge 非 demo 链抽查。MetabolicSliceBridge 的调试按钮固定摆
    /// organ_core→organ_focus→organ_actuator 三件套（"demo"），本条刻意换一条真实内容组合
    /// （org_mito→org_emitter）证明 SlotGrid/PathCompiler/MetabolicSliceRunner 对非 demo 卡也能走通。
    /// 全部纯 C#（无 MonoBehaviour/Unity API），execute_code 直接调 Run()，不进 Play。
    /// </summary>
    public static class BridgeSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            var grid = new SlotGrid(SlotType.Cytoplasm);
            grid.Slots[0].Part = new PartInstance("part_mito_1", "org_mito", PartLocation.Slot(0));
            grid.Slots[1].Part = new PartInstance("part_emitter_1", "org_emitter", PartLocation.Slot(1));

            if (!grid.TryAddEdge(0, 1))
                return (false, "TryAddEdge(0,1) 失败：slot0/slot1 不相邻或超软上限");

            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var runner = new MetabolicSliceRunner(engine);

            List<HitEvent> events = runner.Tick(grid, System.Array.Empty<IContract>(), new WorldState(), seed: 1);

            if (events.Count == 0)
                return (false, "Tick 返回 0 条 HitEvent（org_mito→org_emitter 未编译出路径）");

            var evt = events[0];
            if (evt.Damage <= 0f)
                return (false, $"HitEvent.Damage={evt.Damage:0.##}，未出伤害");

            return (true, $"events={events.Count}, Damage={evt.Damage:0.##}, Tags=[{string.Join(",", evt.Tags)}]");
        }
    }
}
