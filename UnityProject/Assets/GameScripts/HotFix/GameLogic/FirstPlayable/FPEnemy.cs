using Nebukam.ORCA;
using UnityEngine;

namespace GameLogic.FirstPlayable
{
    public enum FPEnemyKind
    {
        /// <summary>草食虫：巡逻，进入察觉范围后靠近并保持停距。</summary>
        Herbivore,
        /// <summary>掠食虫：侧翼追踪。</summary>
        Predator,
        /// <summary>精英「顶级掠食者」：追踪 + 冲撞技能。</summary>
        Elite,
    }

    /// <summary>
    /// 生物阶段敌人。Spec §7.2 数值 / §4.3 行为。
    /// 位移由 ORCA 积分；本类只输出 prefVelocity 并写回 Transform。
    /// </summary>
    public sealed class FPEnemy
    {
        public const float TelegraphTime = 0.9f;
        public const float ChargeSpeedMultiplier = 3.4f;
        public const float ChargeDuration = 0.55f;
        private const float PlayerRadiusApprox = 0.5f;
        private const float HerbivoreStopPadding = 0.4f;
        private const float FlankBlend = 0.35f;

        private static int s_spawnSerial;

        public FPEnemyKind Kind;
        public GameObject Go;
        public Transform T;
        public float Hp;
        public float MaxHp;
        public float Speed;
        public float ContactDamage;
        public float Radius;
        public float AggroRange;
        public Agent Orca;
        public int FlankSign = 1;

        public float DamageCd;
        public float HitFlash;
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
                FlankSign = (s_spawnSerial++ & 1) == 0 ? 1 : -1,
            };
            e._wanderDir.y = 0f;
            e._wanderDir = e._wanderDir.sqrMagnitude < 0.01f ? Vector3.forward : e._wanderDir.normalized;
            e._chargeCd = FPTuning.EliteChargeCooldown * 0.5f;
            e.PlaceY();
            return e;
        }

        public void BindOrca(Agent agent)
        {
            Orca = agent;
            if (Orca == null)
            {
                return;
            }
            Orca.radius = Radius;
            Orca.radiusObst = Radius * 1.05f;
            Orca.maxSpeed = Speed;
            SyncOrcaPosFromTransform();
        }

        public void SyncOrcaPosFromTransform()
        {
            if (Orca == null || T == null)
            {
                return;
            }
            Vector3 p = T.position;
            Orca.pos = new Unity.Mathematics.float3(p.x, 0f, p.z);
        }

        /// <summary>更新 AI 计时与期望速度，不直接改 Transform。</summary>
        public void TickDesire(float dt, Vector3 playerPos)
        {
            if (DamageCd > 0f)
            {
                DamageCd -= dt;
            }

            UpdateHitFlash(dt);

            if (Kind == FPEnemyKind.Elite)
            {
                TickEliteDesire(dt, playerPos);
                return;
            }
            TickNormalDesire(dt, playerPos);
        }

        /// <summary>从 ORCA agent 写回地面位置。</summary>
        public void ApplyOrcaPos()
        {
            if (Orca == null || T == null)
            {
                return;
            }
            Unity.Mathematics.float3 p = Orca.pos;
            T.position = new Vector3(p.x, 0f, p.z);
            PlaceY();
        }

        private void PlaceY()
        {
            Vector3 p = T.position;
            p.y = Kind == FPEnemyKind.Elite ? Radius : Radius + 0.15f;
            T.position = p;
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

        private void TickNormalDesire(float dt, Vector3 playerPos)
        {
            Vector3 to = playerPos - T.position;
            to.y = 0f;
            float dist = to.magnitude;

            Vector3 pref;
            float maxSpeed = Speed;

            if (dist <= AggroRange && dist > 0.01f)
            {
                Vector3 chase = to / dist;
                if (Kind == FPEnemyKind.Herbivore)
                {
                    float stopDist = Radius + PlayerRadiusApprox + HerbivoreStopPadding;
                    if (dist <= stopDist)
                    {
                        // 停距内绕行散开，避免叠在玩家身上
                        Vector3 tangent = new Vector3(-chase.z, 0f, chase.x) * FlankSign;
                        pref = tangent * (Speed * 0.55f);
                    }
                    else
                    {
                        Vector3 tangent = new Vector3(-chase.z, 0f, chase.x) * FlankSign;
                        pref = (chase + tangent * 0.25f).normalized * Speed;
                    }
                }
                else
                {
                    // 掠食：追击 + 侧翼偏置，避免全员同向
                    Vector3 tangent = new Vector3(-chase.z, 0f, chase.x) * FlankSign;
                    pref = (chase + tangent * FlankBlend).normalized * Speed;
                }

                if (pref.sqrMagnitude > 0.0001f)
                {
                    T.forward = pref.normalized;
                }
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
                pref = _wanderDir * (Speed * 0.65f);
                maxSpeed = Speed * 0.65f;
            }

            WritePref(pref, maxSpeed, FPOrcaSim.DefaultNeighborDist, FPOrcaSim.DefaultMaxNeighbors,
                FPOrcaSim.DefaultTimeHorizon);
        }

        private void TickEliteDesire(float dt, Vector3 playerPos)
        {
            Vector3 to = playerPos - T.position;
            to.y = 0f;

            if (_charging > 0f)
            {
                _charging -= dt;
                // 冲撞：提高限速、缩短邻居视野，保留“硬冲”手感
                WritePref(_chargeDir * (Speed * ChargeSpeedMultiplier),
                    Speed * ChargeSpeedMultiplier, 3.5f, 4, 0.35f);
                return;
            }

            if (_telegraph > 0f)
            {
                _telegraph -= dt;
                if (to.sqrMagnitude > 0.0001f)
                {
                    T.forward = to.normalized;
                }
                T.localScale = _baseScale * 1.25f;
                if (_telegraph <= 0f)
                {
                    _charging = ChargeDuration;
                    _chargeDir = to.sqrMagnitude > 0.0001f ? to.normalized : T.forward;
                    T.localScale = _baseScale;
                }
                WritePref(Vector3.zero, Speed, FPOrcaSim.DefaultNeighborDist, FPOrcaSim.DefaultMaxNeighbors,
                    FPOrcaSim.DefaultTimeHorizon);
                return;
            }

            _chargeCd -= dt;
            if (_chargeCd <= 0f && to.magnitude <= 14f)
            {
                _chargeCd = FPTuning.EliteChargeCooldown;
                _telegraph = TelegraphTime;
                WritePref(Vector3.zero, Speed, FPOrcaSim.DefaultNeighborDist, FPOrcaSim.DefaultMaxNeighbors,
                    FPOrcaSim.DefaultTimeHorizon);
                return;
            }

            Vector3 dir = to.sqrMagnitude > 0.0001f ? to.normalized : Vector3.forward;
            Vector3 tangent = new Vector3(-dir.z, 0f, dir.x) * FlankSign;
            Vector3 pref = (dir + tangent * 0.2f).normalized * Speed;
            T.forward = dir;
            WritePref(pref, Speed, FPOrcaSim.DefaultNeighborDist, FPOrcaSim.DefaultMaxNeighbors,
                FPOrcaSim.DefaultTimeHorizon);
        }

        private void WritePref(Vector3 pref, float maxSpeed, float neighborDist, int maxNeighbors, float timeHorizon)
        {
            if (Orca == null)
            {
                return;
            }
            Orca.maxSpeed = Mathf.Max(0.1f, maxSpeed);
            Orca.neighborDist = neighborDist;
            Orca.maxNeighbors = maxNeighbors;
            Orca.timeHorizon = timeHorizon;
            Orca.prefVelocity = new Unity.Mathematics.float3(pref.x, 0f, pref.z);
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
            Orca = null;
            if (Go != null)
            {
                Object.Destroy(Go);
                Go = null;
            }
        }
    }
}
