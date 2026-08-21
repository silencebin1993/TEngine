using System.Collections.Generic;
using TEngine;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>玩家所有 Carrier 实例的持有容器 + 单激活（D8/D9/D10）。
    /// 同一 IsCarrier 器官抽到两份即产生两个独立 CarrierInstance，互不影响。</summary>
    public sealed class CarrierRegistry
    {
        private readonly Dictionary<string, CarrierInstance> _carriers = new Dictionary<string, CarrierInstance>();

        /// <summary>null = 零 Carrier（D10，Reject-to-Safe，不用哨兵 id）。</summary>
        public string ActiveCarrierId { get; private set; }

        public IReadOnlyDictionary<string, CarrierInstance> All => _carriers;

        /// <summary>当前激活 Carrier；零 Carrier 或激活 id 不存在时返回 null（D10）。</summary>
        public CarrierInstance ActiveCarrier =>
            ActiveCarrierId != null && _carriers.TryGetValue(ActiveCarrierId, out var c) ? c : null;

        /// <summary>story-010 J4：装配变更版本号，每次装/卸基因时递增。
        /// 供 <see cref="GameLogic.Battle.Feedback.ComposeAimIndicatorPresenter"/> 判断是否需要刷新指示器预览。</summary>
        public int AssemblyVersion { get; private set; }

        /// <summary>已存在则原样返回；不存在则新建（默认 3 空槽，插槽数动态可增长）。第一个建出的 Carrier 自动置为激活（D9），
        /// 之后新增不抢占当前激活。organelleId 透传给 CarrierInstance（story-004 D9，供 CarrierCompiler 反查 Shape）。</summary>
        public CarrierInstance EnsureCarrier(string carrierId, string organelleId = null)
        {
            if (_carriers.TryGetValue(carrierId, out var existing))
            {
                return existing;
            }

            var carrier = new CarrierInstance(carrierId, organelleId);
            _carriers[carrierId] = carrier;
            if (ActiveCarrierId == null)
            {
                ActiveCarrierId = carrierId;
            }
            return carrier;
        }

        public CarrierInstance GetCarrier(string carrierId) =>
            carrierId != null && _carriers.TryGetValue(carrierId, out var c) ? c : null;

        /// <summary>切换激活 Carrier；找不到该 id 时 no-op（D10，Reject-to-Safe）。
        /// 成功后发 GameEvent（D11），供 005 UI 插槽条订阅刷新。</summary>
        public void SetActive(string carrierId)
        {
            if (!_carriers.ContainsKey(carrierId))
            {
                return;
            }
            if (ActiveCarrierId == carrierId)
            {
                return;
            }
            ActiveCarrierId = carrierId;
            GameEvent.Send(CarrierActivatedEvent);
        }

        /// <summary>story-010 J4：装/卸基因后递增装配版本号。</summary>
        internal void IncrementAssemblyVersion()
        {
            AssemblyVersion++;
        }

        public const string CarrierActivatedEvent = "MetabolicSlice.CarrierActivated";
    }
}
