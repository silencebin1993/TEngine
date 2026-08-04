using System.Collections.Generic;
using GameLogic.Cards;

namespace GameLogic.Stage
{
    /// <summary>游戏大阶段标识。八阶段的骨架，当前只实现 Cell。</summary>
    public enum StageId
    {
        None = 0,
        /// <summary>细胞阶段。</summary>
        Cell = 1,
        /// <summary>器官/组织阶段。</summary>
        Organ = 2,
        /// <summary>生物阶段。</summary>
        Creature = 3,
        /// <summary>远古竞争阶段。</summary>
        Ancient = 4,
        /// <summary>文明崛起阶段。</summary>
        Civilization = 5,
        /// <summary>废土重建阶段。</summary>
        Wasteland = 6,
        /// <summary>星海扩张阶段。</summary>
        Stellar = 7,
        /// <summary>宇宙终局阶段。</summary>
        Cosmic = 8,
    }

    /// <summary>
    /// 阶段产物。跨阶段继承的载荷。对应 Cell_Stage_Spec.md §14。
    ///
    /// 重要：细胞阶段**不依赖任何后续阶段存在**。当前版本此结构只用于
    /// 结算展示与图鉴解锁；器官/生物阶段接入后才会真正读它决定起始能力池。
    ///
    /// 这是"支柱一：连续继承"的技术接口（GDD §1.12）。
    /// </summary>
    public sealed class StageOutcome
    {
        public StageId StageId;
        public bool Victory;
        public float DurationSeconds;
        /// <summary>失败原因。用于区分死因文案（Spec §13）。</summary>
        public string DeathCause;

        /// <summary>主导路线。</summary>
        public CardRoute DominantRoute;
        /// <summary>六路线得分分布，索引对应 CardRoute。</summary>
        public int[] RouteScores = new int[8];

        /// <summary>定义性卡牌（史诗及以上）。</summary>
        public List<CardSpec> KeyCards = new List<CardSpec>(6);
        /// <summary>全部已获得卡牌及层数，用于结算回顾。</summary>
        public List<(int cardId, int stack)> AllCards = new List<(int, int)>(32);

        /// <summary>最终属性快照。键是 StatId，值是最终值。</summary>
        public Dictionary<int, float> FinalStats = new Dictionary<int, float>(32);

        public float PollutionLevel;
        public int Level;

        /// <summary>局内统计。</summary>
        public StageStatistics Statistics = new StageStatistics();

        /// <summary>局内达成。跨局解锁用。</summary>
        public List<string> Achievements = new List<string>(4);

        public void Reset()
        {
            StageId = StageId.None;
            Victory = false;
            DurationSeconds = 0f;
            DeathCause = null;
            DominantRoute = CardRoute.None;
            for (int i = 0; i < RouteScores.Length; i++)
            {
                RouteScores[i] = 0;
            }
            KeyCards.Clear();
            AllCards.Clear();
            FinalStats.Clear();
            PollutionLevel = 0f;
            Level = 0;
            Statistics.Reset();
            Achievements.Clear();
        }
    }

    /// <summary>局内统计。结算界面与图鉴用。</summary>
    public sealed class StageStatistics
    {
        public int FoodDevoured;
        public int EnemiesKilled;
        public int ElitesKilled;
        public int PhasesReached;
        public float PeakVolume;
        public int PeakEnemyCount;
        public float TotalDamageDealt;
        public float TotalDamageTaken;
        public int LevelsGained;
        public int MaxDevourCombo;
        public float NutrientEarned;
        public float MutagenEarned;

        public void Reset()
        {
            FoodDevoured = 0;
            EnemiesKilled = 0;
            ElitesKilled = 0;
            PhasesReached = 0;
            PeakVolume = 0f;
            PeakEnemyCount = 0;
            TotalDamageDealt = 0f;
            TotalDamageTaken = 0f;
            LevelsGained = 0;
            MaxDevourCombo = 0;
            NutrientEarned = 0f;
            MutagenEarned = 0f;
        }
    }

    /// <summary>
    /// 阶段流程接口。
    ///
    /// StageDirector 只认这个接口。后续器官/生物/文明阶段各自实现它，
    /// **StageDirector 不需要改动**——这是"新增阶段不改老代码"的落点
    /// （框架文档 §7 最后一行）。
    /// </summary>
    public interface IStageFlow
    {
        StageId Id { get; }

        /// <summary>进入阶段。inherited 是上一阶段的产物，首个阶段传 null。</summary>
        void Enter(StageOutcome inherited);

        void Update(float dt);

        /// <summary>退出阶段并产出本阶段结果。</summary>
        StageOutcome Exit();
    }
}
