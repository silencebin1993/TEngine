using UnityEngine;

namespace GameLogic.FirstPlayable
{
    public enum FPEnemyKind
    {
        /// <summary>草食虫：巡逻，进入察觉范围后靠近。</summary>
        Herbivore,
        /// <summary>掠食虫：追踪。</summary>
        Predator,
        /// <summary>精英「顶级掠食者」：追踪 + 冲撞技能。</summary>
        Elite,
    }

    /// <summary>
    /// 生物阶段敌人。Spec §7.2 数值 / §4.3 行为。判定同样走距离计算，不用物理。
    /// </summary>
    public sealed class FPEnemy
    {
        public const float TelegraphTime = 0.9f;
        public const float ChargeSpeedMultiplier = 3.4f;
        public const float ChargeDuration = 0.55f;

        public FPEnemyKind Kind;
        public GameObject Go;
        public Transform T;
        public float Hp;
        public float MaxHp;
        public float Speed;
        public float ContactDamage;
        public float Radius;
        public float AggroRange;

        public float DamageCd;
        public float HitFlash;
        private float _baseScaleY;
        private Vector3 _baseScale;

        private Vector3 _wanderDir;
        private float _wanderTimer;

        private float _chargeCd;
        private float _telegraph;
        private float _charging;
        private Vector3 _chargeDir;

        public bool IsCharging => _charging > 0f;
        public bool IsTelegraphing => _telegraph > 0f;
        public bool Alive => Hp > 0f;

        public static FPEnemy Spawn(FPEnemyKind kind, Vector3 pos, Transform parent, float aggroMul)
        {
            float hp, speed, dmg, scale;
            Color color;
            PrimitiveType shape = PrimitiveType.Capsule;

            switch (kind)
            {
                case FPEnemyKind.Herbivore:
                    hp = FPTuning.HerbivoreHp;
                    speed = FPTuning.HerbivoreSpeed;
                    dmg = FPTuning.HerbivoreContactDamage;
                    scale = 0.9f;
                    color = FPFactory.ColHerbivore;
                    break;
                case FPEnemyKind.Predator:
                    hp = FPTuning.PredatorHp;
                    speed = FPTuning.PredatorSpeed;
                    dmg = FPTuning.PredatorContactDamage;
                    scale = 1.05f;
                    color = FPFactory.ColPredator;
                    break;
                default:
                    hp = FPTuning.EliteHp;
                    speed = FPTuning.EliteSpeed;
                    dmg = FPTuning.EliteContactDamage;
                    scale = 1.8f;
                    color = FPFactory.ColElite;
                    shape = PrimitiveType.Cube;
                    break;
            }

            GameObject go = FPFactory.Primitive(shape, kind.ToString(), color, pos, scale, parent);
            FPEnemy e = new FPEnemy
            {
                Kind = kind,
                Go = go,
                T = go.transform,
                Hp = hp,
                MaxHp = hp,
                Speed = speed,
                ContactDamage = dmg,
                Radius = scale * 0.5f,
                AggroRange = FPTuning.EnemyBaseAggroRange * aggroMul,
                _baseScale = Vector3.one * scale,
                _wanderDir = Random.insideUnitSphere,
            };
            e._wanderDir.y = 0f;
            e._wanderDir = e._wanderDir.sqrMagnitude < 0.01f ? Vector3.forward : e._wanderDir.normalized;
            e._chargeCd = FPTuning.EliteChargeCooldown * 0.5f;
            e.PlaceY();
            return e;
        }

        private void PlaceY()
        {
            Vector3 p = T.position;
            p.y = Kind == FPEnemyKind.Elite ? Radius : Radius + 0.15f;
            T.position = p;
        }

        public void Tick(float dt, Vector3 playerPos, float arenaHalf)
        {
            if (DamageCd > 0f)
            {
                DamageCd -= dt;
            }

            UpdateHitFlash(dt);

            if (Kind == FPEnemyKind.Elite)
            {
                TickElite(dt, playerPos, arenaHalf);
                return;
            }
            TickNormal(dt, playerPos, arenaHalf);
        }

        private void UpdateHitFlash(float dt)
        {
            if (HitFlash <= 0f)
            {
                T.localScale = _baseScale;
                return;
            }
            HitFlash -= dt;
            float k = 1f + Mathf.Max(0f, HitFlash) * 1.2f;
            T.localScale = _baseScale * k;
        }

        private void TickNormal(float dt, Vector3 playerPos, float arenaHalf)
        {
            Vector3 to = playerPos - T.position;
            to.y = 0f;
            float dist = to.magnitude;

            Vector3 dir;
            if (dist <= AggroRange && dist > 0.01f)
            {
                dir = to / dist;
            }
            else
            {
                _wanderTimer -= dt;
                if (_wanderTimer <= 0f)
                {
                    _wanderTimer = Random.Range(1.6f, 3.4f);
                    Vector3 r = Random.insideUnitSphere;
                    r.y = 0f;
                    _wanderDir = r.sqrMagnitude < 0.01f ? Vector3.forward : r.normalized;
                }
                dir = _wanderDir;
            }

            Move(dir, Speed, dt, arenaHalf);
        }

        private void TickElite(float dt, Vector3 playerPos, float arenaHalf)
        {
            Vector3 to = playerPos - T.position;
            to.y = 0f;

            if (_charging > 0f)
            {
                _charging -= dt;
                Move(_chargeDir, Speed * ChargeSpeedMultiplier, dt, arenaHalf);
                return;
            }

            if (_telegraph > 0f)
            {
                _telegraph -= dt;
                if (to.sqrMagnitude > 0.0001f)
                {
                    T.forward = to.normalized;
                }
                // 蓄力期间放大，作为冲撞预警
                T.localScale = _baseScale * 1.25f;
                if (_telegraph <= 0f)
                {
                    _charging = ChargeDuration;
                    _chargeDir = to.sqrMagnitude > 0.0001f ? to.normalized : T.forward;
                    T.localScale = _baseScale;
                }
                return;
            }

            _chargeCd -= dt;
            if (_chargeCd <= 0f && to.magnitude <= 14f)
            {
                _chargeCd = FPTuning.EliteChargeCooldown;
                _telegraph = TelegraphTime;
                return;
            }

            Vector3 dir = to.sqrMagnitude > 0.0001f ? to.normalized : Vector3.forward;
            Move(dir, Speed, dt, arenaHalf);
        }

        private void Move(Vector3 dir, float speed, float dt, float arenaHalf)
        {
            Vector3 pos = T.position + dir * (speed * dt);
            T.position = FPFactory.ClampToArena(pos, arenaHalf, Radius);
            PlaceY();
        }

        /// <summary>返回本次是否击杀。</summary>
        public bool TakeDamage(float amount)
        {
            if (!Alive)
            {
                return false;
            }
            Hp -= amount;
            HitFlash = 0.12f;
            return Hp <= 0f;
        }

        public void Destroy()
        {
            if (Go != null)
            {
                Object.Destroy(Go);
                Go = null;
            }
        }
    }
}
