using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BinGames.Sim;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Environment;
using GameLogic.Stats;
using Unity.Mathematics;
// BinGames.Sim 里也有一个同名的 HitEvent（内核命中事件），这里说的一律是 ComposeEngine 的世界出口事件。
using HitEvent = ComposeEngine.Core.HitEvent;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// FG2-FW-03（FG02 FGR-FW-040“先核实，再命名”）：旧反应引擎的行为探针。
    ///
    /// 旧引擎（ComposeEngine）的反应规则藏在预编译 DLL 里，触发配对与效果不能凭名字或说明猜。这里对**运行时真实注册表**
    /// （与 <see cref="MetabolicSliceBridge"/> 构造时同一套：<see cref="ReactionCatalog.RegisterDefaults"/> + <see cref="EnvironmentReactionCatalog.Register"/>）
    /// 里的每一条规则做三层探针：
    /// 1. 规则层（<see cref="ProbeRule"/>）：只注册这一条规则的引擎，对同一发基础事件分别贴“两个标签 / 只贴第一个 / 只贴第二个 / 一个在事件上一个在目标格上”，
    ///    与完全没有反应的引擎比对输出——输出变了 = 触发了；记下伤害倍率、去掉 / 加上的标签、其余字段变化、残留。
    /// 2. 内容层（<see cref="ProbeCompiled"/>）：用真实器官 + 真实基因（固件表 legacyId）走 <see cref="CarrierCompiler"/> 编译，
    ///    完整注册表 vs 空注册表：哪几条规则真的一起触发、合起来的伤害倍率与标签变化。
    /// 3. 世界层：把两份编译结果分别交给真起的 SimWorld（<see cref="SimBridge"/> + <see cref="MetabolicSliceBridge.ApplyEvent"/>）打一片静止敌人，
    ///    比对敌人实际掉的血——字段有差异不等于游戏里有变化，倍率要在世界里兑现才算数。
    /// 结果由 FgReactionProbeSelfCheck 与正式版反应表（fg.TbReaction）逐条比对。
    /// </summary>
    public static class LegacyReactionProbe
    {
        /// <summary>规则层探针结果（只注册这一条规则）。</summary>
        public sealed class RuleProbe
        {
            public string RuleId;
            public string[] Required = Array.Empty<string>();
            public string Description;
            /// <summary>事件同时带两个标签时触发。</summary>
            public bool FiresWithBoth;
            /// <summary>只带第一个 / 第二个标签时触发（对照组，期望 false）。</summary>
            public bool FiresWithOnlyFirst;
            public bool FiresWithOnlySecond;
            /// <summary>器官本身就会贴上的配料标签（旧引擎每一发都自带“动能 Physical”）。</summary>
            public string[] Inherent = Array.Empty<string>();
            /// <summary>第一个标签在事件上、第二个在目标格（环境 / 被命中方）上时触发。</summary>
            public bool FiresWhenTargetCarries;
            /// <summary>事件上一个标签都没有、两个都在目标格上时触发。</summary>
            public bool FiresWhenTargetCarriesBoth;
            public string ReactionName;
            public float DamageRatio = 1f;
            public string[] Removed = Array.Empty<string>();
            public string[] Added = Array.Empty<string>();
            public string[] Residue = Array.Empty<string>();
            /// <summary>其余标量字段的变化（“Wet 1→0”），只作记录。</summary>
            public string FieldChanges = string.Empty;
        }

        /// <summary>内容层 + 世界层探针结果（真实器官 + 真实基因）。</summary>
        public sealed class CompiledProbe
        {
            public string Organ;
            public string[] Genes = Array.Empty<string>();
            public bool Compiled;
            public string Error;
            /// <summary>反应前（空注册表编译）事件上的标签。</summary>
            public string[] TagsBefore = Array.Empty<string>();
            /// <summary>完整注册表下真的触发了的规则（注册表自己的匹配，按注册顺序）。</summary>
            public string[] FiredRules = Array.Empty<string>();
            public string ReactionName;
            public float DamageBefore;
            public float DamageAfter;
            public float DamageRatio = 1f;
            public string[] Removed = Array.Empty<string>();
            public string[] Added = Array.Empty<string>();
            public string[] Residue = Array.Empty<string>();
            /// <summary>世界层：同一片敌人被这一发（两拍）打掉的总血量（完整注册表 / 空注册表）。</summary>
            public float WorldLossWith;
            public float WorldLossWithout;
            public float WorldRatio => WorldLossWithout > 1e-3f ? WorldLossWith / WorldLossWithout : (WorldLossWith > 1e-3f ? float.PositiveInfinity : 1f);
        }

        private static readonly PropertyInfo[] ScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(float) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(int))
            .ToArray();

        private const string ProbeCell = "probe_cell";

        /// <summary>与 <see cref="MetabolicSliceBridge"/> 构造时一模一样的反应注册（内置 + 环境）。</summary>
        public static Engine NewRuntimeEngine()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            EnvironmentReactionCatalog.Register(engine);
            return engine;
        }

        /// <summary>运行时注册表里的全部规则（注册顺序）。</summary>
        public static IReadOnlyList<ReactionRule> RuntimeRules() => NewRuntimeEngine().Reactions.Rules;

        // ─────────────────────────────── 1. 规则层 ───────────────────────────────

        /// <summary>对一条规则做规则层探针。<paramref name="organ"/> = 基础事件的器官（默认连射器等价的 org_emitter）。</summary>
        public static RuleProbe ProbeRule(ReactionRule rule, string organ = "org_emitter")
        {
            var r = new RuleProbe
            {
                RuleId = rule.Id,
                Required = rule.RequiredTags.ToArray(),
                Description = rule.Description,
            };
            HitEvent baseEvt = BaseEvent(organ);
            if (baseEvt == null || r.Required.Length == 0)
            {
                return r;
            }
            // 对照组要干净：器官自带的配料标签（旧引擎每一发都带“动能 Physical”）先记下再从基础事件上拿掉，
            // 否则“只贴冻结”也会因为自带动能而触发脆裂，对照失去意义。
            r.Inherent = r.Required.Where(t => baseEvt.Tags.Contains(t)).ToArray();
            foreach (string t in r.Required)
            {
                baseEvt.Tags.Remove(t);
            }
            var fired = new List<string>();
            var isolated = new Engine();
            isolated.RegisterReaction(Instrument(rule, fired));
            var none = new Engine();

            HitEvent both = WithTags(baseEvt, r.Required);
            HitEvent outWith = Apply(isolated, both, null);
            HitEvent outNone = Apply(none, both, null);
            r.FiresWithBoth = fired.Count > 0;
            r.ReactionName = outWith.Payload.TryGetValue("Reaction", out object name) ? name as string : null;
            r.DamageRatio = outNone.Damage > 1e-6f ? outWith.Damage / outNone.Damage : 1f;
            r.Removed = outNone.Tags.Except(outWith.Tags).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            r.Added = outWith.Tags.Except(outNone.Tags).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            r.Residue = ResidueOf(outWith);
            r.FieldChanges = FieldChanges(outNone, outWith);

            string first = r.Required[0];
            string second = r.Required.Length > 1 ? r.Required[1] : r.Required[0];
            r.FiresWithOnlyFirst = FiresOn(isolated, fired, WithTags(baseEvt, first), null);
            r.FiresWithOnlySecond = FiresOn(isolated, fired, WithTags(baseEvt, second), null);

            // 一个在事件上、一个在目标格上（旧引擎的“目标”是战场格：地形 + 残留）。
            HitEvent carried = WithTags(baseEvt, first);
            carried.TargetId = ProbeCell;
            var world = new WorldState();
            world.AddTag(ProbeCell, second);
            r.FiresWhenTargetCarries = FiresOn(isolated, fired, carried, world);

            HitEvent bare = baseEvt.Clone();
            bare.TargetId = ProbeCell;
            var world2 = new WorldState();
            foreach (string t in r.Required)
            {
                world2.AddTag(ProbeCell, t);
            }
            r.FiresWhenTargetCarriesBoth = FiresOn(isolated, fired, bare, world2);
            return r;
        }

        // ─────────────────────────────── 2 + 3. 内容层 + 世界层 ───────────────────────────────

        /// <summary>真实器官 + 真实基因：完整注册表 vs 空注册表编译，再各自交给真起的 SimWorld 打一片敌人。</summary>
        public static CompiledProbe ProbeCompiled(string organ, IReadOnlyList<string> genes, bool world = true)
        {
            var p = new CompiledProbe { Organ = organ, Genes = genes?.ToArray() ?? Array.Empty<string>() };
            try
            {
                var fired = new List<string>();
                var full = new Engine();
                foreach (ReactionRule rule in RuntimeRules())
                {
                    full.RegisterReaction(Instrument(rule, fired));
                }
                var none = new Engine();
                List<HitEvent> a = CarrierCompiler.CompileFromRecipe(full, organ, p.Genes, new WorldState(), 1);
                List<HitEvent> b = CarrierCompiler.CompileFromRecipe(none, organ, p.Genes, new WorldState(), 1);
                if (a.Count == 0 || b.Count == 0)
                {
                    p.Error = "编译未产出 HitEvent";
                    return p;
                }
                HitEvent withRx = a[0];
                HitEvent without = b[0];
                p.Compiled = true;
                p.TagsBefore = without.Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
                p.FiredRules = fired.Distinct().ToArray();
                p.ReactionName = withRx.Payload.TryGetValue("Reaction", out object name) ? name as string : null;
                p.DamageBefore = without.Damage;
                p.DamageAfter = withRx.Damage;
                p.DamageRatio = without.Damage > 1e-6f ? withRx.Damage / without.Damage : 1f;
                p.Removed = without.Tags.Except(withRx.Tags).OrderBy(t => t, StringComparer.Ordinal).ToArray();
                p.Added = withRx.Tags.Except(without.Tags).OrderBy(t => t, StringComparer.Ordinal).ToArray();
                p.Residue = ResidueOf(withRx);
                if (world)
                {
                    p.WorldLossWith = WorldHpLoss(withRx);
                    p.WorldLossWithout = WorldHpLoss(without);
                }
            }
            catch (Exception ex)
            {
                p.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return p;
        }

        /// <summary>器官单独编译（不装基因）时事件上的标签——某些标签（例如格斗器官的“动能 Physical”）来自器官本身，不来自固件。</summary>
        public static string[] OrganTags(string organ)
        {
            List<HitEvent> evts = CarrierCompiler.CompileFromRecipe(new Engine(), organ, Array.Empty<string>(), new WorldState(), 1);
            return evts.Count > 0 ? evts[0].Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        }

        /// <summary>基因单独装在器官上（空注册表）时事件上的标签。</summary>
        public static string[] GeneTags(string organ, string gene)
        {
            List<HitEvent> evts = CarrierCompiler.CompileFromRecipe(new Engine(), organ, new[] { gene }, new WorldState(), 1);
            return evts.Count > 0 ? evts[0].Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        }

        // ─────────────────────────────── 内部 ───────────────────────────────

        private static HitEvent BaseEvent(string organ)
        {
            OrganelleDef def = OrganelleCatalog.Get(organ);
            if (def == null)
            {
                return null;
            }
            var engine = new Engine();
            var chain = new List<IModule> { new ComposeEngine.Builtin.Modules.EnergyCore(10f), def.CreateModule() };
            IReadOnlyList<HitEvent> raw = engine.RunAssembly(chain, ticks: 1, seed: 1);
            return raw.Count > 0 ? raw[0] : null;
        }

        /// <summary>原规则套一层记录：真的被引擎调用（= 触发）时把规则 ID 记进 <paramref name="fired"/>，效果照旧由原规则计算。</summary>
        private static ReactionRule Instrument(ReactionRule rule, List<string> fired) =>
            new ReactionRule(rule.Id, rule.RequiredTags, (e, c) =>
            {
                fired.Add(rule.Id);
                return rule.Resolve(e, c);
            }, rule.Description);

        private static bool FiresOn(Engine isolated, List<string> fired, HitEvent evt, WorldState world)
        {
            fired.Clear();
            Apply(isolated, evt, world);
            bool yes = fired.Count > 0;
            fired.Clear();
            return yes;
        }

        private static HitEvent WithTags(HitEvent evt, params string[] tags)
        {
            HitEvent c = evt.Clone();
            foreach (string t in tags)
            {
                c.Tags.Add(t);
            }
            return c;
        }

        private static HitEvent Apply(Engine engine, HitEvent evt, WorldState world)
        {
            RuleVector rules = engine.NormalizeContracts(new List<IContract>());
            return engine.ApplyPipeline(evt.Clone(), rules, world ?? new WorldState());
        }

        private static string[] ResidueOf(HitEvent evt)
        {
            if (evt.Payload.TryGetValue("LeaveResidue", out object raw) && raw is List<ResidueDeposit> deposits)
            {
                return deposits.Select(d => d.Tag + ":" + d.Ttl).ToArray();
            }
            return Array.Empty<string>();
        }

        private static string FieldChanges(HitEvent before, HitEvent after)
        {
            var parts = new List<string>();
            foreach (PropertyInfo prop in ScalarProps)
            {
                if (prop.Name == nameof(HitEvent.Damage))
                {
                    continue;
                }
                object x = prop.GetValue(before);
                object y = prop.GetValue(after);
                if (!Equals(x, y))
                {
                    parts.Add($"{prop.Name} {x}→{y}");
                }
            }
            return string.Join("，", parts);
        }

        private static SimConfig ProbeConfig => new SimConfig
        {
            UnitCapacity = 64,
            ProjectileCapacity = 64,
            ArenaHalfExtent = 90f,
            HashCellSize = 4f,
            MaxDeathEventsPerFrame = 64,
            MaxHitEventsPerFrame = 64,
            RandomSeed = 0x5F3759DFu,
            BreachedDiscount = 0.7f,
            CorrodedDiscount = 0.85f,
        };

        /// <summary>世界层：真起 SimWorld，摆一片静止敌人（布局同 <see cref="ChassisPrimitiveMatrixSmokeReport"/>：避开默认瞄准方向、贴脸到 16 单位、另加一列几乎正前方），
        /// 两拍真实出手、推满时间，返回敌人掉血总量。</summary>
        private static float WorldHpLoss(HitEvent evt)
        {
            var sim = new SimBridge();
            try
            {
                sim.Begin(ProbeConfig, Array.Empty<BehaviorArchetype>());
                const float hp = 100000f;
                int spawned = 0;
                for (int ring = 0; ring < 5; ring++)
                {
                    float dist = 2.2f + ring * 3.5f;
                    for (int i = 0; i < 6; i++)
                    {
                        float a = 0.37f + 2f * math.PI * i / 6f;
                        Spawn(sim, new float2(math.cos(a), math.sin(a)) * dist, hp);
                        spawned++;
                    }
                    Spawn(sim, new float2(math.cos(0.09f), math.sin(0.09f)) * dist, hp);
                    spawned++;
                }
                sim.OnUpdate(0.02f);
                var bridge = new MetabolicSliceBridge();
                bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                bridge.OnEnter();
                for (int cast = 0; cast < 2; cast++)
                {
                    bridge.ApplyEvent(evt.Clone());
                    for (int f = 0; f < 8; f++)
                    {
                        sim.OnUpdate(0.05f);
                        bridge.OnUpdate(0.05f);
                    }
                }
                sim.OnUpdate(0.05f);
                float left = 0f;
                SimSnapshot snap = sim.Snapshot;
                for (int i = 0; i < snap.Count; i++)
                {
                    if (snap.Faction[i] == (byte)SimFaction.Hostile)
                    {
                        left += snap.Alive[i] == 0 ? 0f : snap.Health[i];
                    }
                }
                return spawned * hp - left;
            }
            finally
            {
                sim.End();
                sim.OnDispose();
            }
        }

        private static void Spawn(SimBridge sim, float2 pos, float hp)
        {
            sim.Spawn(new SpawnRequest
            {
                Position = pos,
                Velocity = float2.zero,
                Health = hp,
                Radius = 0.4f,
                MaxSpeed = 0f,
                ArchetypeId = 0,
                Faction = SimFaction.Hostile,
                LogicId = sim.NextLogicId(),
                VisualId = 0,
            });
        }
    }
}
