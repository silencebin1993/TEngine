using System;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG7-ENV-03（FG07 FGR-ENV-020“沙暴：画面能见度下降”、第 4 节“天气视觉效果强度的设置”）：天气画面——占位表现（B22，美术阶段整体替换，DEBT-FG7ENV03-01）。
    /// - 酸雨：黄绿色斜落雨丝 + 淡雾；沙暴：横向飞沙 + 浓的土黄色指数雾（能见度下降）；寒潮：缓落雪片 + 淡蓝雾。晴：全部关掉、雾恢复场景原值。
    /// - 强度 = 玩家设置（<see cref="GameSettings.WeatherFx"/>，暂停菜单“画面”）× 淡入淡出（env.weather.fx_ramp_hours）；粒子数上限 env.weather.particles_max（ART-PERF-BUDGET）。
    /// - 只在家园被观察时画（粒子跟着镜头的地面焦点）；纯表现，读存档里的天气，不影响模拟；每帧 O(1)，变化小于阈值不写雾与发射率。
    /// </summary>
    public static class WeatherView
    {
        private static GameObject _root;
        private static ParticleSystem _ps;
        private static ParticleSystemRenderer _psr;
        private static string _psKind;
        private static bool _fogCaptured;
        private static bool _fogBase;
        private static Color _fogColorBase;
        private static float _fogDensityBase;
        private static FogMode _fogModeBase;
        private static bool _fogApplied;
        private static float _lastDensity = -1f;
        private static float _lastRate = -1f;

        /// <summary>当前画面强度（0～1：设置 × 淡入淡出；晴 / 不在家园 = 0）。自检读。</summary>
        public static float Strength { get; private set; }
        /// <summary>当前画面对应的天气（晴 = clear）。</summary>
        public static string Kind { get; private set; } = WeatherService.Clear;
        /// <summary>当前写进场景的雾浓度（自检读：沙暴最浓，强度设置会改变它）。</summary>
        public static float FogDensity { get; private set; }
        /// <summary>当前粒子发射率（每秒；自检读）。</summary>
        public static float EmissionRate { get; private set; }
        public static int ApplyCount { get; private set; }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        /// <summary>每帧（<c>GameRoot.OnUpdate</c>）。</summary>
        public static void Tick(bool inWorld)
        {
            CampaignState s = CampaignSession.Current;
            HomeValleyController home = WorldSimulation.Home;
            bool observed = inWorld && s != null && home != null && home.IsLoaded && WorldView.IsObserved(HomeValleyLayout.RegionId);
            if (!observed)
            {
                Apply(WeatherService.Clear, 0f);
                return;
            }
            string kind = WeatherService.ActiveKind(s);
            float fade = kind == WeatherService.Clear ? 0f : FadeFactor(s, GameClock.Ticks);
            Apply(kind, fade * GameSettings.WeatherFx);
            Follow();
        }

        /// <summary>淡入淡出系数（开始后 / 结束前 env.weather.fx_ramp_hours 内线性过渡；纯函数，读存档里那一段的起止步）。</summary>
        public static float FadeFactor(CampaignState s, long ticks)
        {
            WeatherFrontRecord f = WeatherService.ActiveFront(s);
            if (f == null)
            {
                return 0f;
            }
            double ramp = Math.Max(1.0, GameClock.TicksFor(Math.Max(0.01, Tuning("env.weather.fx_ramp_hours", 0.3f)) * GameClock.HourSeconds));
            double inF = (ticks - f.StartTick) / ramp;
            double outF = (f.EndTick - ticks) / ramp;
            return (float)Math.Max(0.0, Math.Min(1.0, Math.Min(inF, outF)));
        }

        /// <summary>按天气与强度写雾与粒子（自检直接调）。</summary>
        public static void Apply(string kind, float strength)
        {
            strength = Mathf.Clamp01(strength);
            Kind = kind ?? WeatherService.Clear;
            Strength = Kind == WeatherService.Clear ? 0f : strength;
            ApplyFog();
            ApplyParticles();
        }

        private static void ApplyFog()
        {
            float baseDensity = Mathf.Max(0f, Tuning("env.weather.sand_fog_density", 0.018f));
            float density;
            Color color;
            switch (Kind)
            {
                case WeatherService.Sandstorm:
                    density = baseDensity * Strength;
                    color = new Color(0.72f, 0.58f, 0.38f, 1f);
                    break;
                case WeatherService.AcidRain:
                    density = baseDensity * 0.35f * Strength;
                    color = new Color(0.55f, 0.62f, 0.45f, 1f);
                    break;
                case WeatherService.ColdSnap:
                    density = baseDensity * 0.3f * Strength;
                    color = new Color(0.78f, 0.86f, 0.95f, 1f);
                    break;
                default:
                    density = 0f;
                    color = Color.gray;
                    break;
            }
            if (density <= 1e-5f)
            {
                RestoreFog();
                FogDensity = 0f;
                return;
            }
            if (!_fogCaptured)
            {
                _fogBase = RenderSettings.fog;
                _fogColorBase = RenderSettings.fogColor;
                _fogDensityBase = RenderSettings.fogDensity;
                _fogModeBase = RenderSettings.fogMode;
                _fogCaptured = true;
            }
            if (_fogApplied && Mathf.Abs(density - _lastDensity) < 1e-4f)
            {
                return;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = color;
            RenderSettings.fogDensity = density;
            _fogApplied = true;
            _lastDensity = density;
            FogDensity = density;
            ApplyCount++;
        }

        private static void RestoreFog()
        {
            if (_fogApplied && _fogCaptured)
            {
                RenderSettings.fog = _fogBase;
                RenderSettings.fogColor = _fogColorBase;
                RenderSettings.fogDensity = _fogDensityBase;
                RenderSettings.fogMode = _fogModeBase;
            }
            _fogApplied = false;
            _fogCaptured = false;
            _lastDensity = -1f;
        }

        private static void ApplyParticles()
        {
            float max = Mathf.Max(0f, Tuning("env.weather.particles_max", 600f));
            float rate = Kind == WeatherService.Clear ? 0f : max * Strength;
            EmissionRate = rate;
            if (rate <= 0.5f)
            {
                if (_ps != null && _ps.isPlaying)
                {
                    _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                _lastRate = 0f;
                return;
            }
            EnsureParticles();
            if (_ps == null)
            {
                return;
            }
            if (_psKind != Kind)
            {
                ConfigureFor(Kind);
                _psKind = Kind;
                _lastRate = -1f;
            }
            if (Mathf.Abs(rate - _lastRate) > 1f)
            {
                ParticleSystem.EmissionModule em = _ps.emission;
                em.rateOverTime = rate;
                ParticleSystem.MainModule main = _ps.main;
                main.maxParticles = Mathf.Max(16, Mathf.CeilToInt(max));
                _lastRate = rate;
            }
            if (!_ps.isPlaying)
            {
                _ps.Play(true);
            }
        }

        private static void EnsureParticles()
        {
            if (_ps != null)
            {
                return;
            }
            _root = new GameObject("WeatherParticles");
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            _ps = _root.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = _ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 2.5f;
            ParticleSystem.ShapeModule shape = _ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(70f, 1f, 70f);
            _psr = _root.GetComponent<ParticleSystemRenderer>();
            _psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _psr.receiveShadows = false;
        }

        private static void ConfigureFor(string kind)
        {
            ParticleSystem.MainModule main = _ps.main;
            ParticleSystem.VelocityOverLifetimeModule vel = _ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            Color c;
            switch (kind)
            {
                case WeatherService.Sandstorm:
                    c = new Color(0.78f, 0.62f, 0.38f, 0.75f);
                    main.startSize = 0.35f;
                    main.startLifetime = 3f;
                    main.startSpeed = 0f;
                    vel.x = new ParticleSystem.MinMaxCurve(14f, 20f);
                    vel.y = new ParticleSystem.MinMaxCurve(-1.5f, 0.5f);
                    vel.z = new ParticleSystem.MinMaxCurve(-3f, 3f);
                    _psr.renderMode = ParticleSystemRenderMode.Billboard;
                    break;
                case WeatherService.ColdSnap:
                    c = new Color(0.95f, 0.97f, 1f, 0.9f);
                    main.startSize = 0.22f;
                    main.startLifetime = 6f;
                    main.startSpeed = 0f;
                    vel.x = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);
                    vel.y = new ParticleSystem.MinMaxCurve(-3.2f, -2.2f);
                    vel.z = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);
                    _psr.renderMode = ParticleSystemRenderMode.Billboard;
                    break;
                default:
                    c = new Color(0.72f, 0.9f, 0.35f, 0.7f);
                    main.startSize = 0.12f;
                    main.startLifetime = 1.2f;
                    main.startSpeed = 0f;
                    vel.x = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
                    vel.y = new ParticleSystem.MinMaxCurve(-22f, -18f);
                    vel.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
                    _psr.renderMode = ParticleSystemRenderMode.Stretch;
                    _psr.velocityScale = 0.06f;
                    break;
            }
            main.startColor = c;
            _psr.sharedMaterial = ViewMaterials.Get("Sprites/Default", Color.white);
        }

        private static void Follow()
        {
            if (_root == null || _ps == null || !_ps.isPlaying)
            {
                return;
            }
            Camera cam = WorldView.Camera != null ? WorldView.Camera : Camera.main;
            if (cam == null)
            {
                return;
            }
            Vector3 o = cam.transform.position;
            Vector3 d = cam.transform.forward;
            Vector3 focus = o;
            if (Mathf.Abs(d.y) > 1e-4f)
            {
                float t = -o.y / d.y;
                if (t > 0f)
                {
                    focus = o + d * t;
                }
            }
            float height = Kind == WeatherService.Sandstorm ? 3f : 24f;
            _root.transform.position = new Vector3(focus.x - (Kind == WeatherService.Sandstorm ? 30f : 0f), height, focus.z);
        }

        /// <summary>恢复场景原样（回主菜单、自检收尾）。</summary>
        public static void ResetForTests()
        {
            Apply(WeatherService.Clear, 0f);
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }
            _root = null;
            _ps = null;
            _psr = null;
            _psKind = null;
            ApplyCount = 0;
        }
    }
}
