using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>story-003：沙盒自由装配 7 维度覆盖值。Enable=true 时用面板值覆盖 Engine.Fire 真实产出——
    /// Shape/Orbit 全仓库零 producer（见 LookDevFixtures 类注释），必须靠覆盖才能预览；其余维度默认走真实链，
    /// 只在需要单独验证某一维度时才勾选覆盖。</summary>
    public struct SandboxOverrides
    {
        public bool EnableShape;
        public string Shape;
        public bool EnableScale;
        public float Scale;
        public bool EnableCount;
        public float Count;
        public bool EnableSpin;
        public float Spin;
        public bool EnableOrbit;
        public float Orbit;
        public bool EnableExplode;
        public bool ExplodeOnHit;
        public bool EnableTag;
        public string Tag;
    }

    /// <summary>
    /// story-003：沙盒自由装配器核心逻辑（不依赖 UnityEngine/IMGUI，可被 execute_code 直接调用断言）。
    /// 复用 <see cref="LookDevFixtures"/> 的 <c>RunChain</c> 同款 <c>Engine.Fire</c> 链路（Preflight R4）：
    /// 选中的基因/器官拼成 modules+contracts 两路输入；器官按 <see cref="OrganelleRole"/> 归位——
    /// Source 进链头、Sink（Carrier 执行器）进链尾、其余进链身；未选 Source/Sink 时回落
    /// <c>EnergyCore(10f)</c>/<c>Actuator()</c>，与 <see cref="Carrier.CarrierCompiler"/> 固定头尾同一口径。
    /// </summary>
    public static class SandboxAssembler
    {
        public static HitEvent Compose(IReadOnlyList<string> geneIds, IReadOnlyList<string> organelleIds,
            in SandboxOverrides overrides, int seed = 1)
        {
            var contracts = new List<IContract>();
            var head = new List<IModule>();
            var body = new List<IModule>();
            var tail = new List<IModule>();

            if (geneIds != null)
            {
                foreach (string id in geneIds)
                {
                    Func<IContract> createContract = GeneCatalog.Get(id);
                    if (createContract != null)
                    {
                        contracts.Add(createContract());
                        continue;
                    }
                    Func<IModule> createModule = GeneCatalog.GetModule(id);
                    if (createModule != null)
                    {
                        body.Add(createModule());
                    }
                }
            }

            if (organelleIds != null)
            {
                foreach (string id in organelleIds)
                {
                    OrganelleDef def = OrganelleCatalog.Get(id);
                    if (def == null || def.CreateModule == null)
                    {
                        continue;
                    }
                    if (def.Role == OrganelleRole.Source)
                    {
                        head.Add(def.CreateModule());
                    }
                    else if (def.Role == OrganelleRole.Sink)
                    {
                        tail.Add(def.CreateModule());
                    }
                    else
                    {
                        body.Add(def.CreateModule());
                    }
                }
            }

            if (head.Count == 0)
            {
                head.Add(new EnergyCore(10f));
            }
            if (tail.Count == 0)
            {
                tail.Add(new Actuator());
            }

            var modules = new List<IModule>(head.Count + body.Count + tail.Count);
            modules.AddRange(head);
            modules.AddRange(body);
            modules.AddRange(tail);

            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            FireResult result = engine.Fire(modules, contracts, new WorldState(), seed);
            HitEvent evt = result.Events.Count > 0 ? result.Events[0] : new HitEvent();

            ApplyOverrides(evt, overrides);
            return evt;
        }

        private static void ApplyOverrides(HitEvent evt, in SandboxOverrides overrides)
        {
            if (overrides.EnableShape)
            {
                evt.Shape = overrides.Shape;
            }
            if (overrides.EnableScale)
            {
                evt.Scale = overrides.Scale;
            }
            if (overrides.EnableCount)
            {
                evt.Count = overrides.Count;
            }
            if (overrides.EnableSpin)
            {
                evt.Spin = overrides.Spin;
            }
            if (overrides.EnableOrbit)
            {
                evt.Orbit = overrides.Orbit;
            }
            if (overrides.EnableExplode)
            {
                evt.ExplodeOnHit = overrides.ExplodeOnHit;
            }
            if (overrides.EnableTag && !string.IsNullOrEmpty(overrides.Tag))
            {
                evt.Tags.Add(overrides.Tag);
            }
        }

        /// <summary>story-003 Required 3：预设模板一键载入——把 <see cref="LookDevFixtures"/> 某组的
        /// A 侧 HitEvent 转成覆盖值集合，不反推是哪些基因/器官产出（原夹具本就含手写占位轴，
        /// 见 LookDevFixtures 类注释）。载入后清空基元池选择，靠 7 维度覆盖复现该组数值。</summary>
        public static SandboxOverrides OverridesFromEvent(HitEvent evt)
        {
            return new SandboxOverrides
            {
                EnableShape = true,
                Shape = evt.Shape,
                EnableScale = true,
                Scale = evt.Scale,
                EnableCount = true,
                Count = evt.Count,
                EnableSpin = true,
                Spin = evt.Spin,
                EnableOrbit = true,
                Orbit = evt.Orbit,
                EnableExplode = true,
                ExplodeOnHit = evt.ExplodeOnHit,
                EnableTag = false,
                Tag = string.Empty,
            };
        }
    }
}
