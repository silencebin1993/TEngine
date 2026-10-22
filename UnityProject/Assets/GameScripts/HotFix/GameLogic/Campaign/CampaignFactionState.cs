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

        /// <summary>FG10-BOSS-01：洗礼塔（唯一写入口 <c>Regions.BaptismTowerBoss</c>）。</summary>
        public ClarityBossRecord ClarityBoss = new ClarityBossRecord();

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

        /// <summary>FG9-FAC-02：铸造反制（FGR-FAC-004：每个游戏日更新一次并锁定；唯一写入口 <c>Regions.FoundryCounterService</c>）。</summary>
        public int FoundryCounterDay;
        public string FoundryCounterId = string.Empty;
        /// <summary>FG9-FAC-02：已经提示过“进入熔渣热区”的危害区（同一片只提示一次）。</summary>
        public string[] SlagZonesNoticed = Array.Empty<string>();

        /// <summary>FG10-FAC-01：澄净反制（FGR-FAC-004：每个游戏日更新一次并锁定；唯一写入口 <c>Regions.ClarityCounterService</c>）。</summary>
        public int ClarityCounterDay;
        public string ClarityCounterId = string.Empty;
        /// <summary>FG10-FAC-01：已经提示过“进入酸沼”的酸沼区域（外圈带 = 领地 ID#belt，领地里的酸沼 = 领地 ID#序号；同一片只提示一次）。</summary>
        public string[] AcidZonesNoticed = Array.Empty<string>();
        /// <summary>FG10-REG-01：已经发过阵营介绍的领地（第一次进入时一条；唯一写入口 <c>Regions.TerritoryIntroService</c>）。</summary>
        public string[] TerritoriesIntroduced = Array.Empty<string>();

        /// <summary>FG9-ACT1-01（DEBT-FG9FAC01-02）：远征队侦测到的静默信号点（据点 ID / 诱饵 ID，按序；唯一写入口 <c>WorldSim.SignalContactService</c>）。</summary>
        public string[] ContactsDetected = Array.Empty<string>();
        /// <summary>FG9-ACT1-01：监听站“反制预览”情报顺带甄别过的信号点（一直有效）。</summary>
        public string[] ContactsIdentified = Array.Empty<string>();
        /// <summary>FG9-ACT1-01：远征地点里已经提示过的熔渣热区（地点 ID#序号）。</summary>
        public string[] SiteSlagNoticed = Array.Empty<string>();
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

    /// <summary>FG10-BOSS-01：洗礼塔场上的一片流体（泵站灌的油 / 酸 / 水；战斗内核里是一块中立区域，这里只记位置与到期，供主动反应挑目标与 HUD）。</summary>
    [Serializable]
    public sealed class ClarityBossFill
    {
        public int Serial;
        public float X;
        public float Y;
        public float R;
        public string Tag = string.Empty;
        public long UntilTick;
    }

    /// <summary>FG10-BOSS-01：洗礼塔首领战（阶段存档稳定码见 <c>Regions.BaptismPhase</c>）。全部计时是统一时钟的步。</summary>
    [Serializable]
    public sealed class ClarityBossRecord
    {
        public int Phase;
        /// <summary>第几次挑战（每次开战 +1；流体落点按（种子, 第几次, 第几片）派生，读档一致）。</summary>
        public int Attempt;
        public long EngagedTick;
        public long DefeatedTick;
        public bool RewardsGranted;
        /// <summary>上一次复位时止步的阶段（重试前显示）；历史最高阶段。</summary>
        public int LastReachedPhase;
        public int BestPhase;
        /// <summary>灌注：下一次开始预警的步；正在预警的泵站（-1 = 没有）、落点与落下的步；已灌片数；轮到第几座泵站。</summary>
        public long NextFillTick;
        public int FillPump = -1;
        public float FillX;
        public float FillY;
        public long FillAtTick;
        public int Fills;
        public int PumpCursor;
        /// <summary>主动反应：下一次开始预警的步；正在预警的那片流体（Serial，-1 = 没有）与引发的步；已引发次数。</summary>
        public long NextReactTick;
        public int ReactFill = -1;
        public long ReactAtTick;
        public int Reactions;
        /// <summary>泄压：本阶段循环的起点步；当前段（0 封闭 / 1 预警 / 2 泄压）。</summary>
        public long VentCycleTick;
        public int VentState;
        /// <summary>场地：己方机器全部离开场地的那一步（-1 = 有机器在场）。</summary>
        public long OutsideSinceTick = -1;
        /// <summary>阶段二召来的单位（战斗内核单位编号，复位时撤走）。</summary>
        public bool WaveSpawned;
        public int[] WaveUnits = Array.Empty<int>();
        public ClarityBossFill[] Active = Array.Empty<ClarityBossFill>();
    }
}
