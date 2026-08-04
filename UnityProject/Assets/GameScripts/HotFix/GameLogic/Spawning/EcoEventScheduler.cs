using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stage;
using UnityEngine;
using Random = UnityEngine.Random;

namespace GameLogic.Spawning
{
    /// <summary>
    /// 生态事件调度。对应 Cell_Stage_Spec.md §10。
    ///
    /// 每 6-10 分钟触发一次，作为一小时局内的节奏变化与奖励节点。
    /// 中段（25-40 分钟）密度提高，这是"一小时中段乏味"风险项的对策（Spec §16）。
    /// </summary>
    public sealed class EcoEventScheduler : GameModuleBase
    {
        public override int Priority => ModulePriority.EcoEvent;

        private SpawnDirector _director;
        private PhaseTimeline _timeline;
        private ProgressionModule _progression;
        private ResourceWallet _wallet;

        private float _nextEventIn;
        private float _activeLeft;

        public EcoEventSpec Active { get; private set; }
        /// <summary>距下一次事件的秒数。HUD 显示倒计时用。</summary>
        public float NextEventCountdown => _nextEventIn;

        public void Bind(SpawnDirector director, PhaseTimeline timeline,
            ProgressionModule progression, ResourceWallet wallet)
        {
            _director = director;
            _timeline = timeline;
            _progression = progression;
            _wallet = wallet;
        }

        public override void OnEnter()
        {
            Active = null;
            _activeLeft = 0f;
            // 首个事件不要太早，让玩家先熟悉基础操作
            _nextEventIn = Random.Range(240f, 300f);
        }

        public override void OnUpdate(float dt)
        {
            if (Active != null)
            {
                _activeLeft -= dt;
                if (_activeLeft <= 0f)
                {
                    EndActive();
                }
                return;
            }

            _nextEventIn -= dt;
            if (_nextEventIn <= 0f)
            {
                TryStart();
            }
        }

        private void TryStart()
        {
            PhaseSpec phase = _timeline?.Current;
            if (phase?.EcoEventPool == null || phase.EcoEventPool.Length == 0)
            {
                // 本时期没有事件池（如霸主战），推迟再试
                _nextEventIn = 60f;
                return;
            }

            int id = phase.EcoEventPool[Random.Range(0, phase.EcoEventPool.Length)];
            EcoEventSpec spec = DataRegistry.Instance.GetEcoEvent(id);
            if (spec == null)
            {
                _nextEventIn = 60f;
                return;
            }

            Active = spec;
            _activeLeft = spec.Duration;

            if (_director != null)
            {
                _director.EventPressureMul = spec.PressureMul;
            }

            Signals.Publish(new EcoEventSignal { EventId = spec.Id, Started = true });
            TEngine.Log.Info($"[EcoEvent] 触发生态事件：{spec.Name}");
        }

        private void EndActive()
        {
            EcoEventSpec spec = Active;
            Active = null;

            if (_director != null)
            {
                _director.EventPressureMul = 1f;
            }

            if (spec == null)
            {
                _nextEventIn = NextInterval();
                return;
            }

            // 事件奖励
            if (spec.RewardKind != ResourceKind.None && spec.RewardAmount > 0f)
            {
                _wallet?.Add(spec.RewardKind, spec.RewardAmount);
            }
            if (spec.GrantsDraft)
            {
                _progression?.RequestDraft(spec.DraftKind);
            }

            Signals.Publish(new EcoEventSignal { EventId = spec.Id, Started = false });
            _nextEventIn = NextInterval();
        }

        /// <summary>
        /// 下一次事件间隔。基础 6-10 分钟，中段（25-40 分钟）压缩到 4-6 分钟。
        /// </summary>
        private float NextInterval()
        {
            float t = _timeline?.RunElapsed ?? 0f;
            bool midGame = t >= 1500f && t <= 2400f;
            return midGame
                ? Random.Range(240f, 360f)
                : Random.Range(360f, 600f);
        }

        /// <summary>当前事件对吞噬收益的倍率。</summary>
        public float DevourGainMul => Active?.DevourGainMul ?? 1f;
        /// <summary>当前事件对玩家移速的倍率。</summary>
        public float PlayerSpeedMul => Active?.PlayerSpeedMul ?? 1f;
        /// <summary>当前事件偏向的路线。抽卡权重可读它。</summary>
        public Cards.CardRoute FavoredRoute => Active?.FavoredRoute ?? Cards.CardRoute.None;

        public override void OnExit()
        {
            Active = null;
            if (_director != null)
            {
                _director.EventPressureMul = 1f;
            }
        }
    }
}
