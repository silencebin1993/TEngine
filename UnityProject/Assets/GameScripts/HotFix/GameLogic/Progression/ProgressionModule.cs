using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>
    /// 升级曲线。对应 Cell_Stage_Spec.md §6.3。
    ///
    /// threshold(n) = base * pow(growth, n) + linear * n
    /// 目标频率：0-10 分钟 60-90 秒一次，之后逐步放缓到 210 秒以上。
    /// </summary>
    public static class LevelCurve
    {
        public const float Base = 100f;
        public const float Growth = 1.115f;
        public const float Linear = 18f;

        public static float Threshold(int level)
        {
            int n = Mathf.Max(0, level);
            return Base * Mathf.Pow(Growth, n) + Linear * n;
        }
    }

    /// <summary>
    /// 成长模块。管理进化能累积、升级触发与选卡请求。
    ///
    /// 关键设计（Spec §6）：升级只触发**局内突变选择**，不切换阶段。
    /// 这是本次从 demo 转正式版的核心语义变更。
    /// </summary>
    public sealed class ProgressionModule : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private ResourceWallet _wallet;
        private SignalScope _scope;

        public int Level { get; private set; }
        /// <summary>待处理的选卡请求数。可能因精英+事件同时触发而排队。</summary>
        public int PendingDrafts { get; private set; }
        public DraftKind PendingKind { get; private set; } = DraftKind.Normal;

        public float CurrentThreshold => LevelCurve.Threshold(Level);

        /// <summary>进化能进度 0-1，供 HUD 画经验条。</summary>
        public float Progress
        {
            get
            {
                float t = CurrentThreshold;
                return t <= 0f ? 0f : Mathf.Clamp01(_wallet.EvoEnergy / t);
            }
        }

        public void Bind(ResourceWallet wallet)
        {
            _wallet = wallet;
        }

        public override void OnEnter()
        {
            Level = 0;
            PendingDrafts = 0;
            PendingKind = DraftKind.Normal;

            _scope = new SignalScope();
            _scope.On<KillSignal>(OnKill)
                  .On<DevourSignal>(OnDevour);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
        }

        public override void OnUpdate(float dt)
        {
            if (_wallet == null)
            {
                return;
            }

            // 一帧内可能连升多级（吞噬大目标时），循环处理
            int guard = 0;
            while (_wallet.EvoEnergy >= CurrentThreshold && guard++ < 8)
            {
                float cost = CurrentThreshold;
                _wallet.ConsumeEvoEnergy(cost);
                Level++;
                RequestDraft(DraftKind.Normal);

                Signals.Publish(new LevelUpSignal
                {
                    NewLevel = Level,
                    DraftKind = DraftKind.Normal,
                });
            }
        }

        private void OnDevour(DevourSignal s)
        {
            // 吞噬的进化能收益由 CellDevourSystem 结算后入账，这里不重复给
        }

        private void OnKill(KillSignal s)
        {
            // 精英击杀直接触发一次精英进化，不受常规阈值限制（Spec §6.1）
            if (s.WasElite)
            {
                RequestDraft(DraftKind.Elite);
            }
        }

        /// <summary>
        /// 请求一次选卡。稀有度更高的请求会覆盖等待中的类型，
        /// 保证玩家先看到更好的那次（精英 > 污染 > 常规）。
        /// </summary>
        public void RequestDraft(DraftKind kind)
        {
            PendingDrafts++;
            if (Rank(kind) > Rank(PendingKind))
            {
                PendingKind = kind;
            }
        }

        private static int Rank(DraftKind kind)
        {
            switch (kind)
            {
                case DraftKind.Legacy: return 4;
                case DraftKind.Elite: return 3;
                case DraftKind.Corrupt: return 2;
                case DraftKind.Normal: return 1;
                case DraftKind.Repair: return 0;
                default: return 0;
            }
        }

        /// <summary>取出一次待处理选卡。返回 false 表示没有待处理。</summary>
        public bool TryDequeueDraft(out DraftKind kind)
        {
            if (PendingDrafts <= 0)
            {
                kind = DraftKind.Normal;
                return false;
            }
            PendingDrafts--;
            kind = PendingKind;
            if (PendingDrafts == 0)
            {
                PendingKind = DraftKind.Normal;
            }
            return true;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// GM：不消耗进化能，直接升级并请求一次选卡。
        /// 会清空排队中的旧选卡请求，保证立刻弹出的就是本次类型。
        /// 仅 Editor / Development Build。
        /// </summary>
        public void DebugForceDraft(DraftKind kind)
        {
            PendingDrafts = 0;
            PendingKind = DraftKind.Normal;
            Level++;
            RequestDraft(kind);
            Signals.Publish(new LevelUpSignal
            {
                NewLevel = Level,
                DraftKind = kind,
            });
        }
#endif
    }
}
