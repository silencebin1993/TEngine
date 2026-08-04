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

        private SimBridge _sim;
        private StatSheet _stats;
        private AbilitySystem _abilities;
        private ResourceWallet _wallet;
        private Camera _camera;

        /// <summary>技能槽快捷键。槽 0 恒为冲刺（空格）。</summary>
        private static readonly KeyCode[] SlotKeys =
        {
            KeyCode.Space, KeyCode.Q, KeyCode.E, KeyCode.R, KeyCode.F,
        };

        public void Bind(SimBridge sim, StatSheet stats, AbilitySystem abilities,
            ResourceWallet wallet, Camera cam)
        {
            _sim = sim;
            _stats = stats;
            _abilities = abilities;
            _wallet = wallet;
            _camera = cam;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _stats == null)
            {
                return;
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

            _sim.Intent = new PlayerIntent
            {
                MoveDir = move,
                SpeedMul = 1f,
                RadiusOverride = volume,
                AddStatus = SimStatus.None,
                RemoveStatus = SimStatus.None,
            };

            // 属性同步。每帧同步是为了让卡牌的即时属性变化立刻生效。
            _sim.SetPlayerStats(
                _stats.Get(StatId.MaxHealth),
                _sim.PlayerHealth,
                volume,
                speed);

            ApplyRegen(dt);
        }

        private static float2 ReadMoveInput()
        {
            float x = 0f;
            float y = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) { x -= 1f; }
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) { x += 1f; }
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) { y -= 1f; }
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) { y += 1f; }

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

            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
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

        private void PollAbilityInput()
        {
            if (_abilities == null)
            {
                return;
            }

            int slots = Mathf.Min(_abilities.SlotCount, SlotKeys.Length);
            for (int i = 0; i < slots; i++)
            {
                if (!Input.GetKeyDown(SlotKeys[i]))
                {
                    continue;
                }

                AbilityRuntime rt = _abilities.GetSlot(i);
                if (rt?.Spec == null)
                {
                    continue;
                }

                // 体力校验在这里做，而不是 AbilitySystem 里——
                // 因为体力属于资源系统，AbilitySystem 不该知道账本
                if (rt.Spec.StaminaCost > 0f && _wallet != null
                    && !_wallet.TrySpend(ResourceKind.Stamina, rt.Spec.StaminaCost))
                {
                    continue;
                }

                _abilities.TryCast(i);
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
