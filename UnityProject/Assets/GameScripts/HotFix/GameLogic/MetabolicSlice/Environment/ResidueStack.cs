using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Environment
{
    /// <summary>残留触发时机（§1.4）：本 story 只接 OnHit，OnTravel/OnExpire 声明类型不接调用点（已知缺口不修）。</summary>
    public enum ResidueTrigger
    {
        OnHit,
        OnTravel,
        OnExpire,
    }

    /// <summary>反应结果携带的「留下什么残留」指令，走 HitEvent.Payload["LeaveResidue"] 传给 World 层。</summary>
    public sealed class ResidueDeposit
    {
        public string Tag { get; }
        public float Amount { get; }
        public int Ttl { get; }
        public ResidueTrigger Trigger { get; }

        public ResidueDeposit(string tag, float amount, int ttl, ResidueTrigger trigger)
        {
            Tag = tag;
            Amount = amount;
            Ttl = ttl;
            Trigger = trigger;
        }
    }

    /// <summary>格子上已落地的残留：有寿命、可叠加、随 Tick 衰减。</summary>
    public sealed class ResidueStack
    {
        public string Tag { get; }
        public float Amount { get; set; }
        public int TtlTicks { get; set; }
        public string SourceId { get; }
        public Dictionary<string, object> Payload { get; } = new Dictionary<string, object>();

        public ResidueStack(string tag, float amount, int ttlTicks, string sourceId = null)
        {
            Tag = tag;
            Amount = amount;
            TtlTicks = ttlTicks;
            SourceId = sourceId;
        }

        public bool IsExpired => TtlTicks <= 0;
    }
}
