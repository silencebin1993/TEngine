using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Digestion
{
    /// <summary>掉落的可消化材料；PotentialCrafts 首项为消化成功结算产物（简化，非空校验留给调用方）。</summary>
    public sealed class Reagent
    {
        public string ReagentId { get; }
        public string SourceEnemyArchetype { get; }
        public HashSet<string> Tags { get; }
        public float Toxicity { get; }
        public int Progress { get; set; }
        public int MaxTicks { get; }
        public List<string> PotentialCrafts { get; }

        public Reagent(string reagentId, string sourceEnemyArchetype, HashSet<string> tags, float toxicity, int maxTicks, List<string> potentialCrafts)
        {
            ReagentId = reagentId;
            SourceEnemyArchetype = sourceEnemyArchetype;
            Tags = tags;
            Toxicity = toxicity;
            Progress = 0;
            MaxTicks = maxTicks;
            PotentialCrafts = potentialCrafts;
        }
    }
}
