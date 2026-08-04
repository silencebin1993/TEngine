using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 生物阶段。Spec §4.3：近战 / 冲刺 / 放电三个动作，2 种普通敌人 + 1 个精英，
    /// 构筑模块在此兑现为实际属性。8:00 起精英战，进入前生命值全满。
    /// </summary>
    public sealed class FPCreatureStage : IFPStage
    {
        private const float Half = FPTuning.CreatureArenaHalfSize;
        private const float MeleeArcDot = -0.35f;   // 约 140 度正面扇形

        private FPGame _game;
        private FPRunData _run;
        private FPStats _stats;
        private GameObject _root;

        private Transform _playerT;
        private Vector3 _facing = Vector3.forward;
        private Vector3 _vel;
        private float _hp;
        private float _stamina;
        private float _regenDelay;
        private float _dashTimer;
        private float _invuln;
        private float _meleeCd;
        private float _zapCd;
        private const float PlayerRadius = 0.5f;

        private readonly List<FPEnemy> _enemies = new List<FPEnemy>();
        private readonly List<GameObject> _fx = new List<GameObject>();
        private readonly List<float> _fxLife = new List<float>();

        private float _time;
        private float _spawnTimer;
        private bool _eliteSpawned;
        private FPEnemy _elite;
        private int _kills;
        private bool _ended;
        private float _hintTimer;
        private string _hint = "";

        private FPHud _hud;

        public void Enter(FPGame game)
        {
            _game = game;
            _run = game.Run;
            _stats = _run.ResolveStats();

            _root = new GameObject("FPCreatureStage");
            _root.transform.SetParent(game.transform, false);
            FPFactory.BuildArena(Half, _root.transform, "CreatureArena");

            _hp = _stats.MaxHp;
            _stamina = _stats.StaminaMax;

            GameObject player = FPFactory.Primitive(PrimitiveType.Capsule, "CreaturePlayer",
                FPFactory.ColPlayer, new Vector3(0f, PlayerRadius + 0.15f, 0f), 1f, _root.transform);
            _playerT = player.transform;

            // 朝向指示器，白模下用一个小方块表示正面
            GameObject nose = FPFactory.Primitive(PrimitiveType.Cube, "Facing",
                new Color(0.95f, 0.98f, 1f), Vector3.zero, 1f, _playerT);
            nose.transform.localScale = new Vector3(0.22f, 0.22f, 0.5f);
            nose.transform.localPosition = new Vector3(0f, 0.15f, 0.62f);

            for (int i = 0; i < 3; i++)
            {
                SpawnEnemy(i % 2 == 0 ? FPEnemyKind.Herbivore : FPEnemyKind.Predator);
            }

            _hud = new FPHud();
            _hud.Build(_root.transform, true, null);
            _hud.SetEvolveVisible(false);

            game.ConfigureCamera(_playerT, 12.5f, new Vector3(0f, 20f, -7f),
                new Vector3(68f, 0f, 0f));
            RefreshHud();
        }

        public void Tick(float dt)
        {
            if (_ended)
            {
                return;
            }

            _time += dt;

            UpdateTimers(dt);
            UpdatePlayer(dt);
            UpdateEnemies(dt);
            CheckContactDamage(dt);
            UpdateSpawning(dt);
            UpdateFx(dt);
            PollDebugKeys();
            CheckEnd();

            _hud.Tick();
            RefreshHud();

            if (_hintTimer > 0f)
            {
                _hintTimer -= dt;
                if (_hintTimer <= 0f)
                {
                    _hint = "";
                }
            }
        }

        private void UpdateTimers(float dt)
        {
            if (_meleeCd > 0f) _meleeCd -= dt;
            if (_zapCd > 0f) _zapCd -= dt;
            if (_invuln > 0f) _invuln -= dt;

            if (_dashTimer > 0f)
            {
                _dashTimer -= dt;
            }

            // Spec §4.3：停止消耗后延迟 1 秒开始回复
            if (_regenDelay > 0f)
            {
                _regenDelay -= dt;
            }
            else if (_stamina < _stats.StaminaMax)
            {
                _stamina = Mathf.Min(_stats.StaminaMax, _stamina + _stats.StaminaRegen * dt);
            }
        }

        private void UpdatePlayer(float dt)
        {
            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir.z += 1f;
            if (Input.GetKey(KeyCode.S)) dir.z -= 1f;
            if (Input.GetKey(KeyCode.D)) dir.x += 1f;
            if (Input.GetKey(KeyCode.A)) dir.x -= 1f;
            if (dir.sqrMagnitude > 1f)
            {
                dir.Normalize();
            }

            if (dir.sqrMagnitude > 0.0001f)
            {
                _facing = dir;
                _playerT.forward = dir;
            }

            TryDash(dir);
            TryMelee();
            TryZap();

            // 冲刺期间锁定方向并提速
            if (_dashTimer > 0f)
            {
                _vel = _facing * (_stats.Speed * FPTuning.DashSpeedMultiplier);
            }
            else
            {
                Vector3 want = dir * _stats.Speed;
                _vel = Vector3.MoveTowards(_vel, want, 26f * dt);
            }

            Vector3 pos = _playerT.position + _vel * dt;
            pos.y = PlayerRadius + 0.15f;
            _playerT.position = FPFactory.ClampToArena(pos, Half, PlayerRadius);
        }

        private void TryDash(Vector3 dir)
        {
            if (!Input.GetKeyDown(KeyCode.Space) || _dashTimer > 0f)
            {
                return;
            }
            if (_stamina < _stats.DashCost)
            {
                ShowHint("体力不足，无法冲刺");
                return;
            }

            _stamina -= _stats.DashCost;
            _regenDelay = FPTuning.StaminaRegenDelay;
            _dashTimer = FPTuning.DashDuration;
            _invuln = _stats.DashInvuln;
            if (dir.sqrMagnitude > 0.0001f)
            {
                _facing = dir;
            }
        }

        private void TryMelee()
        {
            if (_meleeCd > 0f || !Input.GetMouseButtonDown(0))
            {
                return;
            }
            _meleeCd = FPTuning.CreatureMeleeInterval;
            SpawnDisc(_playerT.position + _facing * 0.8f, FPTuning.CreatureMeleeRange * 1.4f,
                new Color(0.95f, 0.95f, 0.75f, 1f), 0.10f);

            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                FPEnemy e = _enemies[i];
                Vector3 d = e.T.position - _playerT.position;
                d.y = 0f;
                float reach = FPTuning.CreatureMeleeRange + e.Radius + PlayerRadius;
                if (d.sqrMagnitude > reach * reach)
                {
                    continue;
                }
                if (d.sqrMagnitude > 0.0001f && Vector3.Dot(d.normalized, _facing) < MeleeArcDot)
                {
                    continue;
                }
                ApplyDamageToEnemy(i, _stats.MeleeDamage);
            }
        }

        private void TryZap()
        {
            if (!_stats.HasZap || _zapCd > 0f || !Input.GetMouseButtonDown(1))
            {
                return;
            }

            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _enemies.Count; i++)
            {
                Vector3 d = _enemies[i].T.position - _playerT.position;
                d.y = 0f;
                float dist = d.magnitude - _enemies[i].Radius;
                if (dist > FPModuleTable.ZapRange || dist >= bestDist)
                {
                    continue;
                }
                bestDist = dist;
                best = i;
            }

            if (best < 0)
            {
                ShowHint($"放电射程 {FPModuleTable.ZapRange:0.#} 米内没有目标");
                return;
            }

            _zapCd = FPModuleTable.ZapCooldown;
            SpawnBeam(_playerT.position, _enemies[best].T.position);
            ApplyDamageToEnemy(best, FPModuleTable.ZapDamage);
        }

        /// <summary>统一的敌人伤害入口，处理击杀、回血与精英战胜利。</summary>
        private void ApplyDamageToEnemy(int index, float damage)
        {
            FPEnemy e = _enemies[index];
            if (!e.TakeDamage(damage))
            {
                return;
            }

            bool isElite = e.Kind == FPEnemyKind.Elite;
            e.Destroy();
            _enemies.RemoveAt(index);

            if (isElite)
            {
                _elite = null;
                Win();
                return;
            }

            _kills++;
            if (_stats.KillHeal > 0f)
            {
                _hp = Mathf.Min(_stats.MaxHp, _hp + _stats.KillHeal);
                ShowHint($"击杀回血 +{_stats.KillHeal:0}");
            }
        }

        private void SpawnDisc(Vector3 pos, float diameter, Color color, float life)
        {
            pos.y = 0.05f;
            GameObject go = FPFactory.Primitive(PrimitiveType.Cylinder, "MeleeFx", color,
                pos, 1f, _root.transform);
            go.transform.localScale = new Vector3(diameter, 0.02f, diameter);
            _fx.Add(go);
            _fxLife.Add(life);
        }

        private void SpawnBeam(Vector3 from, Vector3 to)
        {
            from.y = 0.7f;
            to.y = 0.7f;
            Vector3 mid = (from + to) * 0.5f;
            float len = Vector3.Distance(from, to);

            GameObject go = FPFactory.Primitive(PrimitiveType.Cylinder, "ZapFx",
                FPFactory.ColZap, mid, 1f, _root.transform);
            // Cylinder 默认沿 Y 轴、高 2 单位
            go.transform.localScale = new Vector3(0.12f, len * 0.5f, 0.12f);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, (to - from).normalized);
            _fx.Add(go);
            _fxLife.Add(0.12f);
        }

        private void UpdateFx(float dt)
        {
            for (int i = _fxLife.Count - 1; i >= 0; i--)
            {
                _fxLife[i] -= dt;
                if (_fxLife[i] > 0f)
                {
                    continue;
                }
                if (_fx[i] != null)
                {
                    Object.Destroy(_fx[i]);
                }
                _fx.RemoveAt(i);
                _fxLife.RemoveAt(i);
            }
        }

        private void UpdateEnemies(float dt)
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                _enemies[i].Tick(dt, _playerT.position, Half);
            }
        }

        private void CheckContactDamage(float dt)
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                FPEnemy e = _enemies[i];
                Vector3 d = _playerT.position - e.T.position;
                d.y = 0f;
                float sum = PlayerRadius + e.Radius;
                if (d.sqrMagnitude > sum * sum)
                {
                    continue;
                }

                // 冲刺无敌帧：所有路线通用的生存手段（Spec §4.3）
                if (_invuln > 0f || e.DamageCd > 0f)
                {
                    continue;
                }

                float dmg = e.IsCharging ? FPTuning.EliteChargeDamage : e.ContactDamage;
                _hp -= dmg;
                e.DamageCd = FPTuning.ContactDamageInterval;
                ShowHint(e.IsCharging
                    ? $"被精英冲撞：-{dmg:0} 生命值"
                    : $"接触伤害：-{dmg:0} 生命值");

                // 推开，避免持续重叠
                Vector3 push = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.right;
                _vel = push * Mathf.Max(3f, _vel.magnitude * 0.5f);
            }
        }

        /// <summary>Spec §4.3 时间线：0-4:00 探索，4:00-8:00 压力递增，8:00 精英战。</summary>
        private void UpdateSpawning(float dt)
        {
            if (_time >= FPTuning.CreaturePressureEnd)
            {
                if (!_eliteSpawned)
                {
                    SpawnElite();
                }
                return;
            }

            int cap = _time < FPTuning.CreatureExploreEnd
                ? FPTuning.CreatureEnemyCapPhase1
                : FPTuning.CreatureEnemyCapPhase2;
            if (_enemies.Count >= cap)
            {
                return;
            }

            float delay = _time < FPTuning.CreatureExploreEnd
                ? FPTuning.CreatureEnemyRespawnDelay
                : FPTuning.CreatureEnemyRespawnDelay * 0.6f;

            _spawnTimer += dt;
            if (_spawnTimer < delay)
            {
                return;
            }
            _spawnTimer = 0f;

            // 压力段提高掠食虫比例
            float predatorChance = _time < FPTuning.CreatureExploreEnd ? 0.35f : 0.6f;
            SpawnEnemy(Random.value < predatorChance ? FPEnemyKind.Predator : FPEnemyKind.Herbivore);
        }

        private void SpawnEnemy(FPEnemyKind kind)
        {
            Vector3 pos = FPFactory.RandomPointAwayFrom(Half,
                _playerT != null ? _playerT.position : Vector3.zero, 9f);
            _enemies.Add(FPEnemy.Spawn(kind, pos, _root.transform, _stats.AggroMul));
        }

        /// <summary>
        /// 精英战。Spec §4.3：进入前生命值全满，排除前段耗血的随机性，
        /// 让精英战成为对构筑本身的干净测试。同时清场，避免杂兵干扰。
        /// </summary>
        private void SpawnElite()
        {
            _eliteSpawned = true;
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                _enemies[i].Destroy();
            }
            _enemies.Clear();

            _hp = _stats.MaxHp;
            _stamina = _stats.StaminaMax;

            Vector3 pos = FPFactory.RandomPointAwayFrom(Half, _playerT.position, 12f, 4f);
            _elite = FPEnemy.Spawn(FPEnemyKind.Elite, pos, _root.transform, _stats.AggroMul);
            _enemies.Add(_elite);
            ShowHint("精英「顶级掠食者」出现，生命值已回满");
        }

        private void CheckEnd()
        {
            if (_eliteSpawned && _elite != null)
            {
                _run.EliteFightSeconds += Time.deltaTime;
            }

            if (_hp > 0f)
            {
                return;
            }

            _ended = true;
            _game.Result = new FPResultContext
            {
                FromStage = FPStage.Creature,
                Success = false,
                Title = _eliteSpawned ? "被精英击败" : "生命值归零",
                Message = _eliteSpawned
                    ? "顶级掠食者赢了这一轮。可保留构筑重试精英战，或整局重开。"
                    : "在精英战之前就倒下了。可保留构筑重试，或整局重开。",
                CanRestartRun = true,
                CanRetryCreature = true,
            };
            _game.GoTo(FPStage.Result);
        }

        private void Win()
        {
            _ended = true;
            _game.Result = new FPResultContext
            {
                FromStage = FPStage.Creature,
                Success = true,
                Title = "击败顶级掠食者",
                Message = "First Playable 全流程完成。三段继承链已验证：细胞微选择 → 器官模块 → 生物阶段能力。",
                CanRestartRun = true,
                CanRetryCreature = false,
            };
            _game.GoTo(FPStage.Result);
        }

        private void RefreshHud()
        {
            string phase;
            if (_eliteSpawned)
            {
                phase = "生物阶段 · 精英战：击败顶级掠食者";
            }
            else if (_time < FPTuning.CreatureExploreEnd)
            {
                phase = "生物阶段 · 生态区探索，熟悉新形态操作";
            }
            else
            {
                phase = "生物阶段 · 压力递增，撑到 8:00 的精英战";
            }
            _hud.SetObjective(phase);

            _hud.SetHp(_hp, _stats.MaxHp);
            _hud.SetStamina(_stamina, _stats.StaminaMax);

            string zap = _stats.HasZap
                ? $"放电 {FPModuleTable.ZapDamage:0}／{FPModuleTable.ZapRange:0.#}m"
                : "无远程";
            _hud.SetResource($"构筑 <b>{_run.ModuleSummary()}</b>\n" +
                             $"移速 {_stats.Speed:0.0}   近战 {_stats.MeleeDamage:0.#}   " +
                             $"无敌帧 {_stats.DashInvuln:0.00}s   冲刺耗力 {_stats.DashCost:0}   {zap}");

            string eliteInfo = _elite != null
                ? $"\n精英生命 {Mathf.CeilToInt(_elite.Hp)} / {_elite.MaxHp:0}"
                : (_eliteSpawned ? "" : $"\n精英战倒计时 {FormatTime(FPTuning.CreaturePressureEnd - _time)}");
            _hud.SetTimer($"{FormatTime(_time)}   击杀 {_kills}   敌人 {_enemies.Count}" +
                          $"   倍速 {_game.DebugSpeed:0.#}x{eliteInfo}");

            if (!string.IsNullOrEmpty(_hint))
            {
                _hud.SetHint(_hint);
            }
            else if (_stats.HasZap)
            {
                _hud.SetHint("左键近战    右键放电    空格冲刺（无敌帧）");
            }
            else
            {
                _hud.SetHint("左键近战    空格冲刺（无敌帧）");
            }
        }

        private static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60:0}:{s % 60:00}";
        }

        private void ShowHint(string text)
        {
            _hint = text;
            _hintTimer = 1.4f;
        }

        private void PollDebugKeys()
        {
            if (Input.GetKeyDown(KeyCode.F2))
            {
                _hp = _stats.MaxHp;
                _stamina = _stats.StaminaMax;
                ShowHint("[调试] 生命与体力已回满");
            }
            if (Input.GetKeyDown(KeyCode.F3) && !_eliteSpawned)
            {
                _time = FPTuning.CreaturePressureEnd;
                ShowHint("[调试] 跳到精英战");
            }
            if (Input.GetKeyDown(KeyCode.F4) && _elite != null)
            {
                int idx = _enemies.IndexOf(_elite);
                if (idx >= 0)
                {
                    ApplyDamageToEnemy(idx, 100f);
                    ShowHint("[调试] 对精英造成 100 伤害");
                }
            }
        }

        public void Exit()
        {
            _hud?.Destroy();
            _hud = null;
            for (int i = 0; i < _enemies.Count; i++)
            {
                _enemies[i].Destroy();
            }
            _enemies.Clear();
            _elite = null;
            _fx.Clear();
            _fxLife.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
