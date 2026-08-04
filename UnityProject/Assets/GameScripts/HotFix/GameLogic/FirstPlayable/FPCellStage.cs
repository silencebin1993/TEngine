using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 细胞阶段。Spec §4.1：移动（惯性）、吞噬（体积判定）、规避威胁与危险区域、
    /// 4:30 微选择、波次递增、进化时机由玩家决定、13:30 强制结束。
    /// 全部判定走距离计算，不使用物理与碰撞体。
    /// </summary>
    public sealed class FPCellStage : IFPStage
    {
        private sealed class Food
        {
            public GameObject Go;
            public Transform T;
            public float Volume;
            public bool IsB;
            public int Evo;
            public int Biomass;
        }

        private sealed class Threat
        {
            public GameObject Go;
            public Transform T;
            public float Volume;
            public Vector3 Vel;
            public float DamageCd;
        }

        private const float Half = FPTuning.ArenaHalfSize;

        private FPGame _game;
        private FPRunData _run;
        private GameObject _root;
        private Transform _playerT;
        private float _volume;
        private float _hp;
        private Vector3 _vel;

        private readonly List<Food> _foods = new List<Food>();
        private readonly List<Threat> _threats = new List<Threat>();
        private readonly List<Transform> _hazards = new List<Transform>();

        private float _time;
        private int _waveSpawned;
        private float _foodTimer;
        private bool _microDone;
        private bool _ended;
        private float _hintTimer;
        private string _hint = "";

        private FPHud _hud;
        private FPMicroChoiceView _microView;

        public void Enter(FPGame game)
        {
            _game = game;
            _run = game.Run;
            _run.CellSeconds = 0f;
            _run.WaveReached = 1;

            _root = new GameObject("FPCellStage");
            _root.transform.SetParent(game.transform, false);
            FPFactory.BuildArena(Half, _root.transform, "CellArena");

            _volume = FPTuning.CellPlayerStartVolume;
            _hp = FPTuning.CellPlayerHp;
            GameObject player = FPFactory.Sphere("CellPlayer", FPFactory.ColPlayer,
                new Vector3(0f, 0.5f, 0f), _volume, _root.transform);
            _playerT = player.transform;

            for (int i = 0; i < FPTuning.HazardCount; i++)
            {
                SpawnHazard();
            }
            for (int i = 0; i < FPTuning.FoodConcurrent; i++)
            {
                SpawnFood();
            }
            SpawnWave(0);

            _hud = new FPHud();
            _hud.Build(_root.transform, false, TryEvolve);

            game.ConfigureCamera(_playerT, 17f, new Vector3(0f, 34f, 0f), new Vector3(90f, 0f, 0f));
            RefreshHud();
        }

        public void Tick(float dt)
        {
            if (_microView != null)
            {
                _microView.Tick();
                return;
            }
            if (_ended)
            {
                return;
            }

            _time += dt;
            _run.CellSeconds = _time;

            UpdatePlayer(dt);
            UpdateThreats(dt);
            CheckFood();
            CheckThreatContact(dt);
            CheckHazard(dt);
            UpdateWaves();
            UpdateFoodRespawn(dt);
            PollDebugKeys();

            if (_time >= FPTuning.MicroChoiceTime && !_microDone)
            {
                ShowMicroChoice();
                return;
            }

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

        private float PlayerRadius => _volume * 0.5f;

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

            // Spec §5.2 移速衰减 + 微选择 趋光纤毛 加成
            float speed = FPTuning.CellPlayerBaseSpeed;
            if (_run.MicroChoice == FPMicroChoice.Phototaxis)
            {
                speed *= 1f + FPTuning.MicroPhototaxisSpeedBonus;
            }
            float penalty = 1f - (_volume - 1f) * FPTuning.SpeedVolumePenalty;
            speed *= Mathf.Max(FPTuning.SpeedFloorRatio, penalty);

            // 惯性：有输入时以 CellAccel 逼近目标速度，松手后以 CellDrag 衰减
            Vector3 want = dir * speed;
            float rate = dir.sqrMagnitude > 0.0001f ? FPTuning.CellAccel : FPTuning.CellDrag;
            _vel = Vector3.MoveTowards(_vel, want, rate * dt);

            Vector3 pos = _playerT.position + _vel * dt;
            pos.y = PlayerRadius;
            _playerT.position = FPFactory.ClampToArena(pos, Half, PlayerRadius);
            _playerT.localScale = Vector3.one * _volume;
        }

        private void CheckFood()
        {
            float pr = PlayerRadius;
            for (int i = _foods.Count - 1; i >= 0; i--)
            {
                Food f = _foods[i];
                float fr = f.Volume * 0.5f;
                Vector3 d = f.T.position - _playerT.position;
                d.y = 0f;
                if (d.sqrMagnitude > (pr + fr) * (pr + fr))
                {
                    continue;
                }

                if (_volume >= f.Volume * FPTuning.EngulfRatio)
                {
                    Eat(f.Evo, f.Biomass, f.Volume);
                    _run.FoodEaten++;
                    Object.Destroy(f.Go);
                    _foods.RemoveAt(i);
                }
                else
                {
                    float need = f.Volume * FPTuning.EngulfRatio;
                    ShowHint($"体积不足，吃不下这个（需要体积 {need:F2}，当前 {_volume:F2}）");
                }
            }
        }

        /// <summary>吞噬结算：进化点、生物质（含贪食囊加成）、代谢泡回血、体积增长。</summary>
        private void Eat(int evo, int biomass, float targetVolume)
        {
            _run.EvoPoint += evo;

            float gain = biomass;
            if (_run.MicroChoice == FPMicroChoice.Gluttony)
            {
                gain *= 1f + FPTuning.MicroGluttonyBiomassBonus;
            }
            _run.Biomass += Mathf.RoundToInt(gain);

            if (_run.MicroChoice == FPMicroChoice.Metabolic)
            {
                _hp = Mathf.Min(FPTuning.CellPlayerHp, _hp + FPTuning.MicroMetabolicHealPerEat);
            }

            _volume = Mathf.Min(FPTuning.CellPlayerMaxVolume,
                _volume + targetVolume * FPTuning.VolumeGainRatio);
        }

        private void UpdateThreats(float dt)
        {
            for (int i = 0; i < _threats.Count; i++)
            {
                Threat t = _threats[i];
                if (t.DamageCd > 0f)
                {
                    t.DamageCd -= dt;
                }

                Vector3 to = _playerT.position - t.T.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                // 玩家已能吞噬它时改为逃离，避免后期威胁沦为免费食物
                bool flee = _volume >= t.Volume * FPTuning.EngulfRatio;
                Vector3 dir = (flee ? -to : to).normalized;

                Vector3 pos = t.T.position + dir * (FPTuning.ThreatSpeed * dt);
                pos.y = t.Volume * 0.5f;
                t.T.position = FPFactory.ClampToArena(pos, Half, t.Volume * 0.5f);
            }
        }

        private void CheckThreatContact(float dt)
        {
            float pr = PlayerRadius;
            for (int i = _threats.Count - 1; i >= 0; i--)
            {
                Threat t = _threats[i];
                float tr = t.Volume * 0.5f;
                Vector3 d = _playerT.position - t.T.position;
                d.y = 0f;
                float sum = pr + tr;
                if (d.sqrMagnitude > sum * sum)
                {
                    continue;
                }

                // Spec §5.1 三分支
                if (_volume >= t.Volume * FPTuning.EngulfRatio)
                {
                    Eat(FPTuning.ThreatEvoPoint, FPTuning.ThreatBiomass, t.Volume);
                    _run.ThreatEaten++;
                    ShowHint($"吞噬威胁：+{FPTuning.ThreatEvoPoint} 进化点");
                    Object.Destroy(t.Go);
                    _threats.RemoveAt(i);
                    continue;
                }

                if (t.Volume >= _volume * FPTuning.EngulfRatio && t.DamageCd <= 0f)
                {
                    _hp -= FPTuning.ThreatContactDamage;
                    t.DamageCd = FPTuning.ContactDamageInterval;
                    ShowHint($"被威胁撞击：-{FPTuning.ThreatContactDamage:F0} 生命值");
                }

                // 中间地带与受伤后都做弹开处理
                Vector3 push = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.right;
                _vel = push * Mathf.Max(2.5f, _vel.magnitude * 0.4f);
                Vector3 back = t.T.position - push * 0.35f;
                back.y = tr;
                t.T.position = FPFactory.ClampToArena(back, Half, tr);
            }
        }

        private void CheckHazard(float dt)
        {
            float pr = PlayerRadius;
            for (int i = 0; i < _hazards.Count; i++)
            {
                Vector3 d = _playerT.position - _hazards[i].position;
                d.y = 0f;
                float r = FPTuning.HazardRadius + pr;
                if (d.sqrMagnitude > r * r)
                {
                    continue;
                }
                _hp -= FPTuning.HazardDamagePerSecond * dt;
                ShowHint("处于危险区域，持续掉血");
                break;
            }
        }

        /// <summary>波次刷新。Spec §7.1：每 4:30 一波，共 3 波，数量倍率 1x / 2x / 4x。</summary>
        private void UpdateWaves()
        {
            if (_waveSpawned >= FPTuning.WaveCount)
            {
                return;
            }
            if (_time < _waveSpawned * FPTuning.WaveInterval)
            {
                return;
            }
            SpawnWave(_waveSpawned);
        }

        private void SpawnWave(int waveIndex)
        {
            float mul = FPTuning.WaveThreatMultiplier[
                Mathf.Clamp(waveIndex, 0, FPTuning.WaveThreatMultiplier.Length - 1)];
            int target = Mathf.RoundToInt(FPTuning.ThreatBaseCount * mul);
            for (int i = _threats.Count; i < target; i++)
            {
                SpawnThreat();
            }
            _waveSpawned = waveIndex + 1;
            _run.WaveReached = _waveSpawned;
        }

        private void UpdateFoodRespawn(float dt)
        {
            if (_foods.Count >= FPTuning.FoodConcurrent)
            {
                return;
            }
            _foodTimer += dt;
            if (_foodTimer < FPTuning.FoodRespawnDelay)
            {
                return;
            }
            _foodTimer = 0f;
            SpawnFood();
        }

        private void SpawnFood()
        {
            bool isB = Random.value < FPTuning.FoodBRatio;
            float vol = isB ? FPTuning.FoodBVolume : FPTuning.FoodAVolume;
            Vector3 pos = FPFactory.RandomPointAwayFrom(Half, _playerT != null
                ? _playerT.position : Vector3.zero, 6f);
            pos.y = vol * 0.5f;

            GameObject go = FPFactory.Sphere(isB ? "FoodB" : "FoodA",
                isB ? FPFactory.ColFoodB : FPFactory.ColFoodA, pos, vol, _root.transform);
            _foods.Add(new Food
            {
                Go = go,
                T = go.transform,
                Volume = vol,
                IsB = isB,
                Evo = isB ? FPTuning.FoodBEvoPoint : FPTuning.FoodAEvoPoint,
                Biomass = isB ? FPTuning.FoodBBiomass : FPTuning.FoodABiomass,
            });
        }

        private void SpawnThreat()
        {
            float vol = FPTuning.ThreatVolume;
            Vector3 pos = FPFactory.RandomPointAwayFrom(Half, _playerT != null
                ? _playerT.position : Vector3.zero, 14f);
            pos.y = vol * 0.5f;

            GameObject go = FPFactory.Primitive(PrimitiveType.Cube, "Threat",
                FPFactory.ColThreat, pos, vol, _root.transform);
            _threats.Add(new Threat { Go = go, T = go.transform, Volume = vol });
        }

        private void SpawnHazard()
        {
            Vector3 pos = FPFactory.RandomPointAwayFrom(Half, Vector3.zero, 10f, 5f);
            pos.y = 0.03f;
            GameObject go = FPFactory.Primitive(PrimitiveType.Cylinder, "Hazard",
                FPFactory.ColHazard, pos, 1f, _root.transform);
            go.transform.localScale = new Vector3(FPTuning.HazardRadius * 2f, 0.03f,
                FPTuning.HazardRadius * 2f);
            _hazards.Add(go.transform);
        }

        /// <summary>Spec §4.1 胜负条件 + §9 失败规则。</summary>
        private void CheckEnd()
        {
            if (_hp <= 0f)
            {
                Fail("生命值归零", "细胞被更大的个体吞噬了。整局重开，回到细胞阶段 0:00。");
                return;
            }

            if (_time < FPTuning.ForceEvolveTime)
            {
                return;
            }

            if (_run.CanEvolve)
            {
                _game.GoTo(FPStage.Build);
                return;
            }
            Fail("13:30 到达，进化点不足 100",
                $"时限内只积累了 {_run.EvoPoint} 进化点，未达进化门槛。整局重开。");
        }

        private void Fail(string title, string message)
        {
            _ended = true;
            _game.Result = new FPResultContext
            {
                FromStage = FPStage.Cell,
                Success = false,
                Title = title,
                Message = message,
                CanRestartRun = true,
                CanRetryCreature = false,
            };
            _game.GoTo(FPStage.Result);
        }

        private void TryEvolve()
        {
            if (!_run.CanEvolve)
            {
                return;
            }
            _game.GoTo(FPStage.Build);
        }

        private void RefreshHud()
        {
            _hud.SetObjective(_run.CanEvolve
                ? "细胞阶段 · 进化门槛已达成，何时进化由你决定"
                : $"细胞阶段 · 吞噬积累进化点，达到 {FPTuning.EvoPointThreshold} 解锁进化");

            _hud.SetHp(_hp, FPTuning.CellPlayerHp);
            _hud.SetResource($"生物质 <b>{_run.Biomass}</b>    " +
                             $"进化点 <b>{_run.EvoPoint}</b> / {FPTuning.EvoPointThreshold}    " +
                             $"体积 <b>{_volume:F2}</b> / {FPTuning.CellPlayerMaxVolume:F1}");

            float left = Mathf.Max(0f, FPTuning.ForceEvolveTime - _time);
            string micro = _run.MicroChoice == FPMicroChoice.None
                ? "未选择"
                : FPModuleTable.RouteName(FPModuleTable.MicroChoiceRoute(_run.MicroChoice));
            _hud.SetTimer($"{FormatTime(_time)}   第 {_waveSpawned} / {FPTuning.WaveCount} 波" +
                          $"   威胁 {_threats.Count}\n强制进化倒计时 {FormatTime(left)}" +
                          $"   微选择：{micro}   倍速 {_game.DebugSpeed:0.#}x");

            _hud.SetEvolveVisible(_run.CanEvolve);

            if (!string.IsNullOrEmpty(_hint))
            {
                _hud.SetHint(_hint);
            }
            else if (_run.CanEvolve)
            {
                _hud.SetHint("可以进化了。留场越久生物质越多（最便宜的纯路线构筑需 260），但威胁每波翻倍");
            }
            else
            {
                _hud.SetHint($"还需 {FPTuning.EvoPointThreshold - _run.EvoPoint} 进化点");
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
            _hintTimer = 1.6f;
        }

        private void PollDebugKeys()
        {
            if (Input.GetKeyDown(KeyCode.F2))
            {
                _run.EvoPoint += 50;
                _run.Biomass += 150;
                ShowHint("[调试] +50 进化点 +150 生物质");
            }
            if (Input.GetKeyDown(KeyCode.F3))
            {
                _run.EvoPoint = Mathf.Max(_run.EvoPoint, FPTuning.EvoPointThreshold);
                _run.Biomass = Mathf.Max(_run.Biomass, 260);
                if (_run.MicroChoice == FPMicroChoice.None)
                {
                    _run.MicroChoice = FPMicroChoice.Gluttony;
                    _microDone = true;
                }
                _game.GoTo(FPStage.Build);
            }
        }

        private void ShowMicroChoice()
        {
            _microDone = true;
            _game.Paused = true;
            _microView = new FPMicroChoiceView();
            _microView.Show(_root.transform, OnMicroPicked);
        }

        private void OnMicroPicked(FPMicroChoice choice)
        {
            _run.MicroChoice = choice;
            _microView.Destroy();
            _microView = null;
            _game.Paused = false;
            ShowHint($"已选择：{ChoiceName(choice)}");
        }

        private static string ChoiceName(FPMicroChoice choice)
        {
            switch (choice)
            {
                case FPMicroChoice.Gluttony: return "贪食囊（生物质 +25%）";
                case FPMicroChoice.Phototaxis: return "趋光纤毛（移速 +20%）";
                case FPMicroChoice.Metabolic: return "代谢泡（吞噬回血 3）";
                default: return "无";
            }
        }

        public void Exit()
        {
            _hud?.Destroy();
            _hud = null;
            _microView?.Destroy();
            _microView = null;
            _foods.Clear();
            _threats.Clear();
            _hazards.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
