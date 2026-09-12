using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Stage.CellStage
{
    /// <summary>
    /// 玩家输入与本体状态。
    ///
    /// 只负责采集输入、写玩家意图、把属性同步给内核。
    /// 实际位移由内核积分（与其它单位走同一套 JobIntegrate），
    /// 这样玩家与敌人的移动手感一致，且分离力对玩家也生效。
    /// </summary>
    public sealed class CellPlayerController : GameModuleBase
    {
        public override int Priority => ModulePriority.Input;

        private const float ControlSwitchFeedbackDuration = 1.5f;

        private SimBridge _sim;
        private StatSheet _stats;
        private AbilitySystem _abilities;
        private ResourceWallet _wallet;
        private Camera _camera;

        private SignalScope _controlScope;

        public ControlRequestResult LastControlSwitchResult { get; private set; } =
            ControlRequestResult.AlreadyControlled;
        public float ControlSwitchFeedbackRemaining { get; private set; }
        public int LastControlCandidateCount { get; private set; }

        /// <summary>最近一次控制权变更的来源。M1-06：玩家按 Tab 换人和死亡后被迫回弹
        /// 要给完全不同的提示，UI 不能只看 <see cref="LastControlSwitchResult"/>。</summary>
        public ControlChangeReason LastControlChangeReason { get; private set; } = ControlChangeReason.None;

        /// <summary>最近一次变更后的受控实体。为 None 表示意识无处可去，控制权明确为无。</summary>
        public SimEntityId LastControlChangeUnitId { get; private set; } = SimEntityId.None;

        /// <summary>
        /// 技能槽快捷键。槽 0 恒为冲刺（空格）。
        /// 正式玩法槽位上限 5；末尾 T/G/C 留给 GM 一键解锁后的额外技能。
        /// </summary>
        private static readonly KeyCode[] SlotKeys =
        {
            KeyCode.Space, KeyCode.Q, KeyCode.E, KeyCode.R, KeyCode.F,
            KeyCode.T, KeyCode.G, KeyCode.C,
        };

        public void Bind(SimBridge sim, StatSheet stats, AbilitySystem abilities,
            ResourceWallet wallet, Camera cam)
        {
            _sim = sim;
            _stats = stats;
            _abilities = abilities;
            _wallet = wallet;
            _camera = cam;

            // M1-06：非玩家发起的控制权变更（死亡回弹、卸载、读档恢复）也要进同一个反馈窗口，
            // 否则玩家被弹到另一具躯体上时屏幕上什么都不说。
            _controlScope?.Dispose();
            _controlScope = new SignalScope()
                .On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
        }

        public override void OnExit()
        {
            _controlScope?.Dispose();
            _controlScope = null;
        }

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            LastControlChangeUnitId = signal.CurrentUnitId;
            LastControlChangeReason = signal.Reason;
            if (signal.Reason == ControlChangeReason.PlayerRequest)
            {
                // 玩家主动切换的反馈已经由 RequestNextControlCandidate 写过了，
                // 这里再写一次会把"可选 N 个"的候选数抹掉。
                return;
            }

            LastControlSwitchResult = ControlRequestResult.Success;
            LastControlCandidateCount = 0;
            ControlSwitchFeedbackRemaining = ControlSwitchFeedbackDuration;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _stats == null)
            {
                return;
            }

            ControlSwitchFeedbackRemaining = Mathf.Max(0f, ControlSwitchFeedbackRemaining - dt);

            // M2-01：直控输入一律走 InputRouter。战略视角与过渡期间本模块读不到任何输入，
            // "过渡中冻结冲突输入"因此是结构性的，不靠这里自己判断镜头状态。
            if (!InputRouter.Owns(InputScope.Direct))
            {
                // 让位时把意图清空，否则松手前的最后一帧移动方向会一直粘在内核里。
                _sim.SetControlledIntent(PlayerIntent.Idle);
                if (_abilities != null)
                {
                    _abilities.MoveDirection = float2.zero;
                }
                return;
            }

            if (InputRouter.ConsumeKeyDown(KeyCode.Tab, InputScope.Direct))
            {
                RequestNextControlCandidate();
            }

            float2 move = ReadMoveInput();
            float2 aim = ReadAimDirection(move);

            if (_abilities != null)
            {
                _abilities.MoveDirection = move;
                _abilities.AimDirection = aim;
            }

            PollAbilityInput();

            // 体积影响移速：变大让你能吃更多，但也更慢（Spec §5 的核心张力）
            float volume = _stats.Get(StatId.Volume);
            float volumePenalty = 1f / (1f + Mathf.Max(0f, volume - 1f) * 0.09f);
            float speed = _stats.Get(StatId.MoveSpeed) * volumePenalty;

            _sim.SetControlledIntent(new PlayerIntent
            {
                MoveDir = move,
                SpeedMul = 1f,
                RadiusOverride = volume,
                AddStatus = SimStatus.None,
                RemoveStatus = SimStatus.None,
            });

            // 属性同步。每帧同步是为了让卡牌的即时属性变化立刻生效。
            _sim.SetPlayerStats(
                _stats.Get(StatId.MaxHealth),
                _sim.PlayerHealth,
                volume,
                speed);

            ApplyRegen(dt);
        }

        /// <summary>
        /// 按稳定实体 ID 循环到下一个可控友军。候选查询仅发生在显式按键时，
        /// 避免逐帧分配；不按距离直接取第一个，防止两个最近单位之间来回跳。
        /// </summary>
        public ControlRequestResult RequestNextControlCandidate()
        {
            if (_sim == null || !_sim.Running)
            {
                return SetControlSwitchFeedback(ControlRequestResult.SimulationNotRunning, 0);
            }

            SimControlCandidate[] candidates = _sim.GetControlCandidates();
            if (candidates.Length == 0)
            {
                return SetControlSwitchFeedback(ControlRequestResult.TargetNotFound, 0);
            }

            ulong current = _sim.ControlledUnitId.Value;
            SimEntityId next = SimEntityId.None;
            SimEntityId first = candidates[0].EntityId;
            for (int i = 0; i < candidates.Length; i++)
            {
                SimEntityId id = candidates[i].EntityId;
                if (id.Value < first.Value)
                {
                    first = id;
                }
                if (id.Value > current && (!next.IsValid || id.Value < next.Value))
                {
                    next = id;
                }
            }

            if (!next.IsValid)
            {
                next = first;
            }
            return SetControlSwitchFeedback(_sim.RequestControlSwitch(next), candidates.Length);
        }

        private ControlRequestResult SetControlSwitchFeedback(ControlRequestResult result, int candidateCount)
        {
            LastControlSwitchResult = result;
            LastControlCandidateCount = candidateCount;
            LastControlChangeReason = ControlChangeReason.PlayerRequest;
            ControlSwitchFeedbackRemaining = ControlSwitchFeedbackDuration;
            return result;
        }

        private static float2 ReadMoveInput()
        {
            // WASD 在战略视角下是镜头平移，在直控下是移动——同一组键两种含义，
            // 靠 InputScope 互斥，不靠两边各自判断。
            float x = 0f;
            float y = 0f;
            if (InputRouter.GetKey(KeyCode.A, InputScope.Direct) ||
                InputRouter.GetKey(KeyCode.LeftArrow, InputScope.Direct)) { x -= 1f; }
            if (InputRouter.GetKey(KeyCode.D, InputScope.Direct) ||
                InputRouter.GetKey(KeyCode.RightArrow, InputScope.Direct)) { x += 1f; }
            if (InputRouter.GetKey(KeyCode.S, InputScope.Direct) ||
                InputRouter.GetKey(KeyCode.DownArrow, InputScope.Direct)) { y -= 1f; }
            if (InputRouter.GetKey(KeyCode.W, InputScope.Direct) ||
                InputRouter.GetKey(KeyCode.UpArrow, InputScope.Direct)) { y += 1f; }

            var v = new float2(x, y);
            return math.lengthsq(v) > 0.0001f ? math.normalize(v) : float2.zero;
        }

        /// <summary>
        /// 瞄准方向。鼠标位置投影到 XZ 平面（游戏是俯视，Y 是高度）。
        /// 鼠标不可用时退化为移动方向。
        /// </summary>
        private float2 ReadAimDirection(float2 fallback)
        {
            if (_camera == null)
            {
                return math.lengthsq(fallback) > 0.0001f ? fallback : new float2(1f, 0f);
            }

            if (!InputRouter.TryGetPointer(InputScope.Direct, out Vector3 pointer))
            {
                return math.lengthsq(fallback) > 0.0001f ? fallback : new float2(1f, 0f);
            }

            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = _camera.ScreenPointToRay(pointer);
            if (!plane.Raycast(ray, out float enter))
            {
                return math.lengthsq(fallback) > 0.0001f ? fallback : new float2(1f, 0f);
            }

            Vector3 hit = ray.GetPoint(enter);
            float2 p = _sim.PlayerPosition;
            var d = new float2(hit.x - p.x, hit.z - p.y);
            return math.normalizesafe(d, math.lengthsq(fallback) > 0.0001f
                ? fallback
                : new float2(1f, 0f));
        }

        /// <summary>
        /// 槽 0（冲刺/闪避）继续手动 Space 触发——它是位移/闪避，不是攻击，自动触发的
        /// 时机难以判断"何时该躲"。槽 1..N-1（攻击/技能）Ready 即自动施放，不再依赖按键，
        /// 方向由 AbilitySystem.TryCastAuto 自动锁最近敌人（见该方法注释）。
        /// </summary>
        private void PollAbilityInput()
        {
            if (_abilities == null)
            {
                return;
            }

            int slots = Mathf.Min(_abilities.SlotCount, SlotKeys.Length);

            if (slots > 0 && InputRouter.ConsumeKeyDown(SlotKeys[0], InputScope.Direct))
            {
                TryCastSlot(0, autoAim: false);
            }

            for (int i = 1; i < slots; i++)
            {
                AbilityRuntime rt = _abilities.GetSlot(i);
                if (rt?.Spec == null || !rt.Ready) // O(1) 每帧检查，非 O(敌人数)
                {
                    continue;
                }
                TryCastSlot(i, autoAim: true);
            }
        }

        private void TryCastSlot(int slotIndex, bool autoAim)
        {
            AbilityRuntime rt = _abilities.GetSlot(slotIndex);
            if (rt?.Spec == null)
            {
                return;
            }

            // 体力校验在这里做，而不是 AbilitySystem 里——
            // 因为体力属于资源系统，AbilitySystem 不该知道账本
            if (rt.Spec.StaminaCost > 0f && _wallet != null
                && !_wallet.TrySpend(ResourceKind.Stamina, rt.Spec.StaminaCost))
            {
                return;
            }

            if (autoAim)
            {
                _abilities.TryCastAuto(slotIndex);
            }
            else
            {
                _abilities.TryCast(slotIndex);
            }
        }

        private void ApplyRegen(float dt)
        {
            float regen = _stats.Get(StatId.HealthRegen);
            if (regen > 0f)
            {
                _sim.HealPlayer(regen * dt, _stats.Get(StatId.MaxHealth));
            }
        }
    }
}
