using GameLogic.Core;
using GameLogic.Spawning;
using UnityEngine;

namespace GameLogic.Stage
{
    /// <summary>
    /// 阶段内的时期推进。细胞阶段有 6 个生态时期（Spec §3）。
    ///
    /// 完全数据驱动：时期定义在 PhaseSpec 里（时长、卡池解锁、敌人池、
    /// 事件池、压力曲线）。新增一个时期 = 加一行配置，本类不动。
    /// </summary>
    public sealed class PhaseTimeline : GameModuleBase
    {
        public override int Priority => ModulePriority.Timeline;

        private SpawnDirector _director;

        public int CurrentIndex { get; private set; } = -1;
        public PhaseSpec Current { get; private set; }
        /// <summary>当前时期已进行秒数。</summary>
        public float PhaseElapsed { get; private set; }
        /// <summary>本局总秒数。</summary>
        public float RunElapsed { get; private set; }
        /// <summary>是否已走完所有时期。</summary>
        public bool Finished { get; private set; }

        /// <summary>当前时期进度 0-1。HUD 画时期进度条用。</summary>
        public float PhaseProgress =>
            Current != null && Current.Duration > 0f
                ? Mathf.Clamp01(PhaseElapsed / Current.Duration)
                : 0f;

        /// <summary>本时期是否已刷过期末精英。</summary>
        private bool _eliteSpawned;

        /// <summary>story-006：LookDev 沙盒抑制时期推进/抽卡触发，避免选卡面板打断 A/B 对照。</summary>
        public bool Suppressed { get; set; }

        public void Bind(SpawnDirector director)
        {
            _director = director;
        }

        public override void OnEnter()
        {
            CurrentIndex = -1;
            Current = null;
            PhaseElapsed = 0f;
            RunElapsed = 0f;
            Finished = false;
            _eliteSpawned = false;
            Advance();
        }

        public override void OnUpdate(float dt)
        {
            if (Suppressed)
            {
                return;
            }
            if (Finished || Current == null)
            {
                return;
            }

            PhaseElapsed += dt;
            RunElapsed += dt;

            // 同步给导演，让压力预算能读到时期进度
            if (_director != null)
            {
                _director.ElapsedSeconds = PhaseElapsed;
            }

            // 期末精英：在时期剩余 15% 时刷，给玩家时间打完
            if (!_eliteSpawned && Current.SpawnEliteAtEnd
                && Current.EliteEnemyId > 0 && PhaseProgress >= 0.85f)
            {
                _eliteSpawned = true;
                _director?.SpawnElite(Current.EliteEnemyId);
            }

            if (PhaseElapsed >= Current.Duration)
            {
                Advance();
            }
        }

        /// <summary>推进到下一时期。走完则标记 Finished。</summary>
        public void Advance()
        {
            CurrentIndex++;
            PhaseElapsed = 0f;
            _eliteSpawned = false;

            PhaseSpec next = DataRegistry.Instance.GetPhase(CurrentIndex);
            if (next == null)
            {
                Finished = true;
                Current = null;
                return;
            }

            Current = next;

            if (_director != null)
            {
                _director.CurrentPhase = next;
                _director.CurrentPhaseIndex = CurrentIndex;
                _director.ElapsedSeconds = 0f;
            }

            Signals.Publish(new PhaseChangedSignal
            {
                PhaseIndex = CurrentIndex,
                PhaseId = next.Id,
                PhaseName = next.Name,
            });

            TEngine.Log.Info($"[PhaseTimeline] 进入生态时期 {CurrentIndex + 1}/6：{next.Name}");
        }

        /// <summary>最后一个时期（原核霸主战）的首领 id。0 表示无。</summary>
        public int FinalBossId =>
            Current != null && !Current.SpawnEliteAtEnd ? Current.EliteEnemyId : 0;

        public override void OnExit()
        {
            Current = null;
            Finished = false;
        }
    }
}
