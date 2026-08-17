using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>story-006：LookDev 沙盒对照夹具。冻结自 story-001 §4（qa/evidence/compose-visual-verify-001-contract.md）。</summary>
    public sealed class LookDevFixture
    {
        public string Name { get; }
        public string AxisLabel { get; }
        public HitEvent A { get; }
        public HitEvent B { get; }

        public LookDevFixture(string name, string axisLabel, HitEvent a, HitEvent b)
        {
            Name = name;
            AxisLabel = axisLabel;
            A = a;
            B = b;
        }
    }

    /// <summary>
    /// 6 组对照夹具懒加载缓存一次（Decision D7）。①⑤（Shape/Orbit）当前零 producer，手写 HitEvent；
    /// ②③④⑥ 走真实链——直接用 ComposeEngine.Builtin.Modules 拼装 Engine.Fire（与 org_mito/org_scatter/
    /// org_flagella/org_lyso/org_emitter 在 <see cref="ContentCatalog.OrganelleCatalog"/> 里注册的模块类型
    /// 完全一致），不手写 HitEvent。
    ///
    /// ② Scale 例外：<c>org_swell</c> 的 <see cref="Grow"/> 默认构造 <c>BaseGrowRate=1</c>，
    /// Step 里 <c>packet.Scale = 1 * packet.Mult(1) = 1</c>，与未装 Grow 的基线（Packet.Scale 默认也是 1）
    /// 数值完全相同——不是"截图后发现难辨"，是代数上恒等，装了也白装。按 Preflight 的例外条款
    /// （截图/推导发现默认值无肉眼差异时允许覆盖）改用 <c>Grow(3f)</c>，对齐 story-001 §4 "Scale=1 vs 3"。
    /// 其余三条真实链（Count/Spin/Explode）用目录默认构造即可产出可辨差异，未覆盖。
    /// </summary>
    public static class LookDevFixtures
    {
        private static IReadOnlyList<LookDevFixture> _cache;

        public static IReadOnlyList<LookDevFixture> All => _cache ??= Build();

        private static List<LookDevFixture> Build()
        {
            return new List<LookDevFixture>
            {
                BuildShapeFixture(),
                BuildScaleFixture(),
                BuildCountFixture(),
                BuildSpinFixture(),
                BuildOrbitFixture(),
                BuildExplodeFixture(),
                BuildTagFixture(),
            };
        }

        // ① Shape 差：手写 HitEvent（Shape 全仓库零 producer）
        private static LookDevFixture BuildShapeFixture()
        {
            HitEvent a = HandWritten(shape: "Bolt");
            HitEvent b = HandWritten(shape: "Field");
            return new LookDevFixture("① Shape 差", "org_lens 目标态占位：Shape Bolt→Field", a, b);
        }

        // ⑤ Orbit 差：手写 HitEvent（Orbit 全仓库零 producer）
        private static LookDevFixture BuildOrbitFixture()
        {
            HitEvent a = HandWritten("Bolt", orbit: 0f);
            HitEvent b = HandWritten("Bolt", orbit: 2f);
            return new LookDevFixture("⑤ Orbit 差", "运动轴：Orbit 0→2", a, b);
        }

        private static HitEvent HandWritten(string shape, float orbit = 0f)
        {
            var evt = new HitEvent
            {
                Damage = 10f,
                Scale = 1f,
                Count = 1f,
                Shape = shape,
                Orbit = orbit,
            };
            evt.Tags.Add("Physical");
            return evt;
        }

        // ② Scale 差：真实链，org_mito→org_swell→org_emitter（Grow 覆盖 growRate=3，理由见类注释）
        private static LookDevFixture BuildScaleFixture()
        {
            HitEvent a = RunChain(new EnergyCore(), new Actuator());
            HitEvent b = RunChain(new EnergyCore(), new Grow(3f), new Actuator());
            return new LookDevFixture("② Scale 差", "org_swell：Scale 1→3（growRate 覆盖，默认1与基线代数恒等）", a, b);
        }

        // ③ Count 差：真实链，org_mito→org_scatter→org_emitter（默认 BaseCount=2 足以体现 Count≥2）
        private static LookDevFixture BuildCountFixture()
        {
            HitEvent a = RunChain(new EnergyCore(), new Actuator());
            HitEvent b = RunChain(new EnergyCore(), new Scatterer(), new Actuator());
            return new LookDevFixture("③ Count 差", "org_scatter：Count 1→2（默认值）", a, b);
        }

        // ④ Spin 差：真实链，org_mito→org_flagella→org_emitter（默认 AngularSpeed=90）
        private static LookDevFixture BuildSpinFixture()
        {
            HitEvent a = RunChain(new EnergyCore(), new Actuator());
            HitEvent b = RunChain(new EnergyCore(), new OrbitSpin(), new Actuator());
            return new LookDevFixture("④ Spin 差", "org_flagella：Spin 0→90（默认值）", a, b);
        }

        // ⑥ Explode 差：真实链，org_mito→org_lyso→org_emitter
        private static LookDevFixture BuildExplodeFixture()
        {
            HitEvent a = RunChain(new EnergyCore(), new Actuator());
            HitEvent b = RunChain(new EnergyCore(), new ExplodeOnHit(), new Actuator());
            return new LookDevFixture("⑥ Explode 差", "org_lyso：ExplodeOnHit false→true", a, b);
        }

        // ⑦ Tag 染色差：真实链，org_mito→org_perox→org_emitter（Fire）vs org_mito→org_aqua→org_emitter（Wet）
        private static LookDevFixture BuildTagFixture()
        {
            HitEvent a = RunChain(new EnergyCore(), new TagAttach("Fire"), new Actuator());
            HitEvent b = RunChain(new EnergyCore(), new TagAttach("Wet"), new Actuator());
            return new LookDevFixture("⑦ Tag 染色差", "org_perox vs org_aqua：Fire→橙红 / Wet→蓝", a, b);
        }

        /// <summary>纯 C# 最小链路：Engine.Fire 一次跑完 RunAssembly→NormalizeContracts→ApplyPipeline，
        /// 返回的是引擎真实产出的 HitEvent（非手写），不进 Play/不碰玩家真实网格。</summary>
        private static HitEvent RunChain(params IModule[] modules)
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            FireResult result = engine.Fire(modules, Array.Empty<IContract>(), new WorldState(), seed: 1);
            if (result.Events.Count == 0)
            {
                throw new InvalidOperationException(
                    $"LookDevFixtures: 装配链未产出 HitEvent（modules=[{string.Join(",", Array.ConvertAll(modules, m => m.Id))}]）");
            }
            return result.Events[0];
        }
    }
}
