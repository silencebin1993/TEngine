using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.UI.Battle;

namespace GameLogic.MetabolicSlice.Digestion
{
    /// <summary>
    /// story-007 轴 C1 触点：把已验的 <see cref="DigestionChamber"/>/<see cref="Reagent"/> 接进真实局内循环。
    /// 此前二者只有类型定义，没有任何调用点——DevourSignal 从未喂料，Tick 从未跑，Completed 也从未消费
    /// （DigestionEvent 的既定协议本就是 Completed 走 BagInventory.TryAdd，本类只是把这条路径接上）。
    /// 不改 DigestionChamber/Reagent 的结算算法。
    /// </summary>
    public sealed class MetabolicDigestionSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Resolution;

        private const float TickInterval = 1f;
        private const int ChamberCap = 4;

        private readonly DigestionChamber _chamber = new DigestionChamber(ChamberCap);
        private readonly List<string> _log = new List<string>(6);
        private string[] _reagentIds;
        private float _timer;
        private int _feedCount;

        /// <summary>轴 C1 HUD 只读入口：消化泡当前占用/容量。</summary>
        public int ChamberCount => _chamber.Items.Count;
        public int ChamberCapacity => _chamber.Cap;
        public IReadOnlyList<string> RecentLog => _log;

        public override void OnEnter()
        {
            var ids = new List<string>(ReagentCatalog.AllIds);
            _reagentIds = ids.ToArray();
            _timer = 0f;
            _feedCount = 0;
            _log.Clear();
            Signals.Subscribe<DevourSignal>(OnDevour);
        }

        public override void OnExit()
        {
            Signals.Unsubscribe<DevourSignal>(OnDevour);
        }

        /// <summary>吞噬即"捕食"（DigestionChamber 文档 §C1），残块二次吞噬（IsCorpse）不产出试剂——没有真正的猎物来源。</summary>
        private void OnDevour(DevourSignal evt)
        {
            if (evt.IsCorpse || _reagentIds == null || _reagentIds.Length == 0)
            {
                return;
            }

            string reagentId = _reagentIds[_feedCount % _reagentIds.Length];
            _feedCount++;

            System.Func<Reagent> factory = ReagentCatalog.Get(reagentId);
            Reagent reagent = factory?.Invoke();
            if (reagent == null)
            {
                return;
            }

            InsertResult result = _chamber.Insert(reagent);
            if (result == InsertResult.ChamberFull)
            {
                Log($"消化泡已满（{_chamber.Cap}/{_chamber.Cap}），{reagent.ReagentId} 未能吞入");
                return;
            }
            Log($"吞噬进消化泡：{reagent.ReagentId}（{_chamber.Items.Count}/{_chamber.Cap}）");
        }

        public override void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < TickInterval)
            {
                return;
            }
            _timer = 0f;

            List<DigestionEvent> events = _chamber.Tick(1);
            for (int i = 0; i < events.Count; i++)
            {
                Consume(events[i]);
            }
        }

        private void Consume(DigestionEvent evt)
        {
            if (evt.Kind == DigestionEventKind.Failed)
            {
                Log($"消化失败：{evt.ReagentId} 毒性过高，被排出消化泡");
                return;
            }

            var part = new PartInstance(System.Guid.NewGuid().ToString("N"), evt.ResultCardDefId, PartLocation.Bag());
            AddResult added = MetabolicSlicePanel.Instance != null
                ? MetabolicSlicePanel.Instance.Bag.TryAdd(part)
                : AddResult.NeedDecision;

            Log(added == AddResult.Added
                ? $"消化完成：{evt.ReagentId} → 获得 {evt.ResultCardDefId}，已收进储备囊"
                : $"消化完成：{evt.ReagentId} → {evt.ResultCardDefId}，但储备囊已满，产物丢失");
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > 6)
            {
                _log.RemoveAt(0);
            }
            TEngine.Log.Info($"[MetabolicDigestionSystem] {line}");
        }
    }
}
