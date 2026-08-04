using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;
// Unity.Mathematics 也有 Random，与 UnityEngine.Random 冲突。本文件要的是后者。
using Random = UnityEngine.Random;

namespace GameLogic.Spawning
{
    /// <summary>
    /// 压力预算刷怪导演。对应 Cell_Stage_Spec.md §9.1。
    ///
    /// 不写死"第 N 分钟刷 M 只"，而是：
    ///   budget(t) = phaseBase * difficultyCurve(t) * playerPowerFactor
    /// 导演从当前时期的敌人池按 cost 采购，直到填满预算。
    ///
    /// 关键约束（Spec §16 风险项）：playerPowerFactor **只影响上浮部分**，
    /// 下限随时间硬性抬升。否则玩家会故意压制 build 换低难度。
    /// </summary>
    public sealed class SpawnDirector : GameModuleBase
    {
        public override int Priority => ModulePriority.Spawning;

        private SimBridge _sim;
        private StatSheet _stats;
        private Deck _deck;

        private readonly List<int> _candidates = new List<int>(16);
        private float _spawnAccum;
        private float _currentSpend;

        /// <summary>采购间隔。不必每帧刷，0.4 秒一批足够密。</summary>
        private const float SpawnInterval = 0.4f;

        /// <summary>当前生态时期（由 PhaseTimeline 写入）。</summary>
        public PhaseSpec CurrentPhase { get; set; }
        /// <summary>当前时期序号。</summary>
        public int CurrentPhaseIndex { get; set; }
        /// <summary>生态事件的压力倍率。</summary>
        public float EventPressureMul { get; set; } = 1f;
        /// <summary>本局已进行的秒数。</summary>
        public float ElapsedSeconds { get; set; }

        /// <summary>当前存活的敌对单位数（读快照得到，供 UI 显示"敌人规模"）。</summary>
        public int LiveHostiles { get; private set; }
        /// <summary>当前压力占用。</summary>
        public float CurrentPressure => _currentSpend;
        /// <summary>本帧的压力预算。</summary>
        public float Budget { get; private set; }

        public void Bind(SimBridge sim, StatSheet stats, Deck deck)
        {
            _sim = sim;
            _stats = stats;
            _deck = deck;
        }

        public override void OnEnter()
        {
            _spawnAccum = 0f;
            _currentSpend = 0f;
            EventPressureMul = 1f;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || CurrentPhase == null)
            {
                return;
            }

            // 存活数与压力占用从快照重算，避免自己维护计数导致漂移
            RecountFromSnapshot();

            Budget = ComputeBudget();

            _spawnAccum += dt;
            if (_spawnAccum < SpawnInterval)
            {
                return;
            }
            _spawnAccum = 0f;

            Purchase();
        }

        private void RecountFromSnapshot()
        {
            SimSnapshot snap = _sim.Snapshot;
            int live = 0;
            float spend = 0f;

            // 这是热更层唯一的每帧 O(N) 遍历，但 N 是**槽位数**而非活跃敌人数，
            // 且只读两个 byte 数组，实测 16k 容量下 < 0.1ms。
            // 若成为热点，应把计数下沉到内核的 JobCollectDeaths 里顺便算出来。
            for (int i = 1; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0)
                {
                    continue;
                }
                byte f = snap.Faction[i];
                if (f != (byte)SimFaction.Hostile)
                {
                    continue;
                }
                live++;
                // 用半径近似 cost，避免为每个单位存一份 cost
                spend += math.max(1f, snap.Radius[i] * 3f);
            }

            LiveHostiles = live;
            _currentSpend = spend;
        }

        /// <summary>
        /// 压力预算。
        ///
        /// 结构：floor + (base - floor) * powerFactor
        /// floor 部分不受玩家强度影响，随时间抬升；只有上浮部分才由强度调节。
        /// </summary>
        private float ComputeBudget()
        {
            float floor = CurrentPhase.PressureFloor;
            float baseline = CurrentPhase.PressureBase;

            // 时期内的时间曲线：从 0.75 抬到 1.15，让每个时期内部也有渐强感
            float phaseProgress = CurrentPhase.Duration > 0f
                ? Mathf.Clamp01(ElapsedSeconds / CurrentPhase.Duration)
                : 0f;
            float timeCurve = Mathf.Lerp(0.75f, 1.15f, phaseProgress);

            float powerFactor = PlayerPowerFactor();

            float budget = (floor + (baseline - floor) * powerFactor) * timeCurve;
            return budget * Mathf.Max(0.1f, EventPressureMul);
        }

        /// <summary>
        /// 玩家强度因子 0.6-1.6。
        /// 由卡牌数、技能强度与当前生命推导——强 build 遇到更多敌人（动态难度）。
        /// </summary>
        private float PlayerPowerFactor()
        {
            if (_stats == null)
            {
                return 1f;
            }

            float cards = _deck != null ? _deck.TotalCards : 0f;
            // 24-30 张卡是一局的目标持有量（Spec §6.3），据此归一化
            float cardTerm = Mathf.Clamp01(cards / 26f);

            float power = _stats.Get(StatId.AbilityPower);
            float powerTerm = Mathf.Clamp01((power - 1f) / 1.5f);

            float hp = _stats.Get(StatId.MaxHealth);
            float hpTerm = Mathf.Clamp01((hp - 100f) / 300f);

            float raw = cardTerm * 0.5f + powerTerm * 0.3f + hpTerm * 0.2f;

            // 濒死时下调，避免死亡螺旋
            float hpPct = hp > 0f ? _sim.PlayerHealth / hp : 1f;
            if (hpPct < 0.25f)
            {
                raw *= 0.7f;
            }

            return Mathf.Lerp(0.6f, 1.6f, raw);
        }

        /// <summary>
        /// 按 cost 采购敌人直到填满预算。
        ///
        /// 生态多样性约束：一次采购里同一种敌人不超过 60%，
        /// 避免出现"整屏都是同一个敌人"的廉价感。
        /// </summary>
        private void Purchase()
        {
            float room = Budget - _currentSpend;
            if (room <= 0f)
            {
                return;
            }

            BuildCandidates();
            if (_candidates.Count == 0)
            {
                return;
            }

            int guard = 0;
            int sameKind = 0;
            int lastId = -1;
            int spawnedThisBatch = 0;
            // 单批上限，防止预算突然放大时一帧塞满
            const int BatchCap = 48;

            while (room > 0f && guard++ < 200 && spawnedThisBatch < BatchCap)
            {
                int enemyId = _candidates[Random.Range(0, _candidates.Count)];

                if (enemyId == lastId)
                {
                    sameKind++;
                    if (sameKind > BatchCap * 6 / 10)
                    {
                        continue;
                    }
                }
                else
                {
                    lastId = enemyId;
                    sameKind = 1;
                }

                EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);
                if (spec == null || spec.SpawnCost > room)
                {
                    // 预算不够买这只，试试别的（可能有更便宜的）
                    if (!AnyAffordable(room))
                    {
                        break;
                    }
                    continue;
                }

                SpawnOne(spec);
                room -= spec.SpawnCost;
                _currentSpend += spec.SpawnCost;
                spawnedThisBatch++;
            }
        }

        private bool AnyAffordable(float room)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                EnemySpec s = DataRegistry.Instance.GetEnemy(_candidates[i]);
                if (s != null && s.SpawnCost <= room)
                {
                    return true;
                }
            }
            return false;
        }

        private void BuildCandidates()
        {
            _candidates.Clear();
            int[] pool = CurrentPhase.EnemyPool;
            if (pool == null)
            {
                return;
            }

            for (int i = 0; i < pool.Length; i++)
            {
                EnemySpec s = DataRegistry.Instance.GetEnemy(pool[i]);
                if (s == null || s.IsElite || s.IsBoss)
                {
                    continue;
                }
                if (s.MinPhase > CurrentPhaseIndex)
                {
                    continue;
                }
                if (s.MaxPhase >= 0 && s.MaxPhase < CurrentPhaseIndex)
                {
                    continue;
                }
                _candidates.Add(s.Id);
            }
        }

        /// <summary>
        /// 生成一只。位置在玩家视野外的环上——玩家不应看到敌人凭空出现。
        /// </summary>
        private void SpawnOne(EnemySpec spec)
        {
            float2 center = _sim.PlayerPosition;
            float half = _sim.ArenaHalfExtent;

            // 视野外环：24-34 单位。镜头 orthoSize 16 时屏幕半对角约 22。
            float ang = Random.value * Mathf.PI * 2f;
            float dist = Random.Range(24f, 34f);
            float2 pos = center + new float2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;

            // 夹回场地内，并避免正好贴边生成
            pos = math.clamp(pos, -half + 1f, half - 1f);

            _sim.Spawn(new SpawnRequest
            {
                Position = pos,
                Velocity = float2.zero,
                Health = spec.Health,
                Radius = spec.Radius,
                MaxSpeed = spec.MaxSpeed,
                ArchetypeId = spec.ArchetypeIndex,
                Faction = SimFaction.Hostile,
                InitialStatus = spec.InitialStatus,
                LogicId = EncodeLogicId(spec.Id),
                VisualId = spec.VisualId,
            });
        }

        /// <summary>
        /// 精英/首领生成。位置固定在玩家前方稍远处，给玩家反应时间。
        /// </summary>
        public void SpawnElite(int enemyId)
        {
            EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);
            if (spec == null || _sim == null || !_sim.Running)
            {
                return;
            }

            float2 center = _sim.PlayerPosition;
            float half = _sim.ArenaHalfExtent;
            float ang = Random.value * Mathf.PI * 2f;
            float2 pos = center + new float2(Mathf.Cos(ang), Mathf.Sin(ang)) * 26f;
            pos = math.clamp(pos, -half + 2f, half - 2f);

            _sim.Spawn(new SpawnRequest
            {
                Position = pos,
                Velocity = float2.zero,
                Health = spec.Health,
                Radius = spec.Radius,
                MaxSpeed = spec.MaxSpeed,
                ArchetypeId = spec.ArchetypeIndex,
                Faction = SimFaction.Hostile,
                InitialStatus = spec.InitialStatus,
                LogicId = EncodeLogicId(spec.Id),
                VisualId = spec.VisualId,
            });
        }

        /// <summary>
        /// 逻辑 id 编码。内核只回传这个 int，所以把敌人配置 id 编进去，
        /// 死亡结算时才能知道该给多少进化能/营养质。
        /// 高 16 位留给实例序号（当前未用），低 16 位是敌人配置 id。
        /// </summary>
        public static int EncodeLogicId(int enemyId) => enemyId & 0xFFFF;

        public static int DecodeEnemyId(int logicId) => logicId & 0xFFFF;
    }
}
