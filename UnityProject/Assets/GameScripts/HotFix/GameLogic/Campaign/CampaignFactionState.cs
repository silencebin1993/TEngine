using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG9-FAC-01（FG09 第 4 章）：阵营扩充的存档状态域。静默：寂听主脑的首领战状态机、电磁噪声发射器、阵营反制的每日锁定、伏笔地点。
    /// 加字段不升 SchemaVersion（JsonUtility 对缺失字段补默认值）；旧档读回由 <see cref="CampaignFgStateDomains.EnsureAll"/> 补空域。
    /// </summary>
    [Serializable]
    public sealed class FactionState
    {
        public int DomainVersion = 1;

        /// <summary>寂听主脑（唯一写入口 <c>Regions.SilentMindBoss</c>）。</summary>
        public SilentBossRecord SilentBoss = new SilentBossRecord();

        /// <summary>被摧毁的噪声发射器（危害区 ID = 领地 ID#序号；唯一写入口 <c>Env.EmNoiseService</c>）。</summary>
        public string[] NoiseEmittersDestroyed = Array.Empty<string>();

        /// <summary>已经提示过“进入电磁噪声区”的危害区（同一片只提示一次）。</summary>
        public string[] NoiseZonesNoticed = Array.Empty<string>();

        /// <summary>静默反制（FGR-FAC-004：每个游戏日更新一次并锁定；唯一写入口 <c>Regions.SilentCounterService</c>）。</summary>
        public int SilentCounterDay;
        public string SilentCounterId = string.Empty;
        /// <summary>核心固件发动的每日计数（反制“核心固件频繁发动”的依据）：当天 / 前一天。</summary>
        public int CoreFireDay;
        public int CoreFiresToday;
        public int CoreFiresYesterday;

        /// <summary>已读取的室内伏笔地点（fg.TbInteriorPoint.id）。</summary>
        public string[] PoisRead = Array.Empty<string>();
    }

    /// <summary>
    /// FG9-FAC-01：寂听主脑首领战的状态（阶段与计时全部用统一时钟的整数步，确定性、与帧率 / 倍速 / 观察无关）。
    /// <see cref="Phase"/> 取值见 <c>Regions.SilentMindPhase</c>（稳定码，只追加不重排）。
    /// </summary>
    [Serializable]
    public sealed class SilentBossRecord
    {
        public int Phase;
        /// <summary>当前阶段的到期步（静默预警 / 全频静默；其余阶段为 0）。</summary>
        public long PhaseEndTick;
        /// <summary>阶段一：主脑藏在第几个中继节点（0～5，按编成表顺序）；-1 = 还没定。</summary>
        public int RealNode = -1;
        /// <summary>阶段一：下一次中继跳转开始亮起的步；正在亮起时的目标节点与完成步（-1 / 0 = 没有）。</summary>
        public long NextJumpTick;
        public int JumpTarget = -1;
        public long JumpAtTick;
        public int Jumps;
        /// <summary>跳转被打断后主脑现形到这一步。</summary>
        public long RevealUntilTick;
        /// <summary>第几次挑战（每次重新开始 +1；真节点与跳转顺序按它派生，读档一致）。</summary>
        public int Attempt;
        /// <summary>上一次失败时止步的阶段（重试前显示）；历史最高阶段。</summary>
        public int LastReachedPhase;
        public int BestPhase;
        public long EngagedTick;
        public long DefeatedTick;
        public bool RewardsGranted;
        /// <summary>这一次挑战开始时锁定的静默反制（大厅里按它追加编成）。</summary>
        public string CounterId = string.Empty;
    }
}
