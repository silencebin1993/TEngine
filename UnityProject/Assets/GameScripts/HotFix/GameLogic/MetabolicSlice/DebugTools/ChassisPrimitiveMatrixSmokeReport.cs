using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim;
using ComposeEngine;
using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;
using Unity.Mathematics;
// BinGames.Sim 里也有一个同名的 HitEvent（内核命中事件），这里说的一律是 ComposeEngine 的世界出口事件。
using HitEvent = ComposeEngine.Core.HitEvent;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// chassis-native-primitives 的核心回归守卫：**没有任何 (底盘, 基因) 组合是完全没反应的。**
    ///
    /// 与 <see cref="ChassisFallbackSmokeReport"/> 的区别很关键：那个报告断言的是
    /// "HitEvent 字段有差异"——但字段有差异不等于游戏里有变化。一条基因可以把 Bounce 写成 2，
    /// 而近战底盘的执行路径压根不读 Bounce，于是玩家装上去什么都不会发生。
    /// 改动前实测：完全无反应的组合有 <b>29</b> 对（近战 8 / 召唤 8 / 光环 11 / 弹道 1 / 场地 1）。
    ///
    /// 所以这里断言的是**行为**：真起一个 SimWorld、真摆一圈敌人、真调 ApplyEvent + Step，
    /// 然后比对一整束可观察量（挂起的坑/残影/节拍/挥击窗口、场上弹体数、近战扇形与旋风/跃击/击退、
    /// 光环半径、召唤数量与小弟属性、玩家坐标、敌人总血量）。空载基线与挂基因后完全一致 = 这条基因
    /// 在这个底盘上是空装，直接 FAIL 并点名。
    ///
    /// 纯 C# 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。
    /// 每个组合用小容量 <see cref="SimConfig"/> 单独 Begin/End，组合之间不互相污染。
    /// </summary>
    public static class ChassisPrimitiveMatrixSmokeReport
    {
        private static readonly string[] ChassisOrgans =
        {
            "org_emitter",  // Projectile
            "org_cilia",    // Melee
            "org_enzyme",   // Field
            "org_osmotic",  // Aura
            "org_bud",      // Summon
        };

        private static readonly string[] ChassisNames =
        {
            "Projectile", "Melee", "Field", "Aura", "Summon",
        };

        /// <summary>
        /// 这三条基因的效果是**条件触发**的，不产生静态可观察差异，属于设计使然而非缺陷：
        /// <list type="bullet">
        /// <item>gene_catalyst：写 ReactionAmp，由 ComposeEngine 的 Pipeline 在**有 Tag 反应发生时**放大伤害；
        /// 空场景里没有反应可放大。</item>
        /// <item>gene_heatshock：只在 Packet.Heat 越过阈值时分岔（需前置产热模块，见
        /// <see cref="ChassisFallbackSmokeReport"/> 的 primer 手法）。</item>
        /// <item>gene_swarm：贴 InheritPattern，由 ApplySwarmInherit 让**已存活的召唤物**补一次结算；
        /// 场上没有小弟时自然没有可观察量。</item>
        /// <item>gene_weave：它是个**连接器**——「把留下的坑/迹跟附近的连起来」。装在自身不产坑的
        /// 底盘上（org_emitter/org_cilia/org_osmotic/org_bud 都没有 Linger）时没有东西可连，
        /// 这是玩家读得懂的条件性，不是空装。机制本身由 <see cref="WeaveProof"/> 单独实证。</item>
        /// </list>
        /// 它们仍然逐一验证"不抛异常"，只是豁免"必须有可观察差异"这一条。
        /// </summary>
        private static readonly HashSet<string> ConditionalGenes = new HashSet<string>
        {
            "gene_catalyst", "gene_heatshock", "gene_swarm", "gene_weave",
        };

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

        public static (bool Pass, string Reason) Run()
        {
            string[] geneIds = GeneCatalog.AllModuleIds.OrderBy(x => x).ToArray();
            if (geneIds.Length < 42)
            {
                return (false, $"GeneCatalog.AllModuleIds 应至少 42 条，实际 {geneIds.Length}");
            }

            var engine = new Engine();
            var world = new WorldState();
            var inert = new List<string>();
            int combos = 0;
            int observable = 0;

            for (int ci = 0; ci < ChassisOrgans.Length; ci++)
            {
                string organId = ChassisOrgans[ci];
                string baseline = Fingerprint(organId, null, engine, world);

                for (int gi = 0; gi < geneIds.Length; gi++)
                {
                    combos++;
                    string geneId = geneIds[gi];
                    string withGene = Fingerprint(organId, geneId, engine, world);

                    if (withGene.StartsWith("ERR"))
                    {
                        return (false, $"{ChassisNames[ci]}/{geneId} 探测失败：{withGene}");
                    }

                    if (withGene != baseline)
                    {
                        observable++;
                    }
                    else if (!ConditionalGenes.Contains(geneId))
                    {
                        inert.Add($"{ChassisNames[ci]}/{geneId}");
                    }
                }
            }

            if (inert.Count > 0)
            {
                return (false,
                    $"以下 {inert.Count} 对 (底盘,基因) 装上去毫无可观察变化（= 空装）：{string.Join(", ", inert)}");
            }

            // ── 正向实证：光有"差异"不够，还得证明差异**就是设计说的那件事** ──
            string named = NamedReadings(engine, world);
            if (named != null)
            {
                return (false, named);
            }

            var sb = new StringBuilder();
            sb.Append($"{combos} 对 (底盘,基因) 组合全部有可观察行为差异，空装组合 0 对");
            sb.Append($"（{observable} 对产出差异指纹，其余 {combos - observable} 对来自 {ConditionalGenes.Count} 条条件触发基因：");
            sb.Append(string.Join("/", ConditionalGenes.ToArray()));
            sb.Append("，已按设计豁免且逐一验证不抛异常）");
            sb.Append("；正向实证：跃击/击退/吸血/召唤增殖/毒网连桥 逐条按设计读法生效");
            return (true, sb.ToString());
        }

        /// <summary>
        /// 逐条实证"轨迹基元在这个底盘上的读法"确实是设计说的那件事。
        /// 返回 null 表示全部通过，否则返回第一条失败原因。
        /// </summary>
        private static string NamedReadings(Engine engine, WorldState world)
        {
            // ① Gravity 抛投 → 近战**跃击**：玩家真的往前挪了。
            Observation lunge = Observe("org_cilia", "gene_arc", engine, world);
            Observation lungeBase = Observe("org_cilia", null, engine, world);
            if (lunge.MeleeLunge <= 0.5f)
            {
                return $"①跃击 org_cilia+gene_arc 的 LastMeleeLungeDistance 应 >0.5，实际 {lunge.MeleeLunge:0.##}";
            }
            if (math.distance(lunge.PlayerPos, lungeBase.PlayerPos) <= 0.5f)
            {
                return $"①跃击 玩家应真的前移，实际基线 {lungeBase.PlayerPos} → 跃击 {lunge.PlayerPos}";
            }

            // ② Bounce 反弹 → 近战**击退**：内核真的推动了单位。
            Observation knock = Observe("org_cilia", "gene_elastic", engine, world);
            if (knock.KnockbackUnits <= 0)
            {
                return $"②击退 org_cilia+gene_elastic 应推动 >0 个单位，实际 {knock.KnockbackUnits}";
            }

            // ③ Return+Blood → 近战**吸血**：真的回血了。
            Observation leech = Observe("org_cilia", "gene_blood", engine, world);
            if (leech.Lifesteal <= 0f)
            {
                return $"③吸血 org_cilia+gene_blood 的 LifestealHealed 应 >0，实际 {leech.Lifesteal:0.###}";
            }

            // ④ Count 发数 → 召唤**数量**：小弟真的变多了。
            Observation brood = Observe("org_bud", "gene_spindle", engine, world);
            Observation broodBase = Observe("org_bud", null, engine, world);
            if (brood.SummonRequested <= broodBase.SummonRequested)
            {
                return $"④增殖 org_bud+gene_spindle 召唤数应 >{broodBase.SummonRequested}，实际 {brood.SummonRequested}";
            }

            // ⑤ Weave 编织 → **毒网**：坑与坑之间真的连出了桥（连接器机制的正向实证，
            //    对应它在 ConditionalGenes 里的豁免理由）。
            Observation web = Observe("org_enzyme", "gene_weave", engine, world);
            if (web.WeaveBridges <= 0)
            {
                return $"⑤毒网 org_enzyme+gene_weave 应连出 >0 条桥，实际 {web.WeaveBridges}";
            }

            // ⑥ Delay → **残影**：命中点稍后真的又打了一次（此前 evt.Delay 全仓无人读取）。
            Observation echo = Observe("org_enzyme", "gene_echo", engine, world);
            Observation echoBase = Observe("org_enzyme", null, engine, world);
            if (echo.HostileHp >= echoBase.HostileHp)
            {
                return $"⑥残影 org_enzyme+gene_echo 应打出额外伤害（敌人总血更低），"
                    + $"实际 基线 {echoBase.HostileHp:0.#} vs 残影 {echo.HostileHp:0.#}";
            }

            return null;
        }

        /// <summary>一次真实开火之后能观察到的全部东西。<see cref="Digest"/> 用于矩阵比对，
        /// 其余具名字段用于 <see cref="NamedReadings"/> 的逐条实证。</summary>
        private struct Observation
        {
            public string Digest;
            public float MeleeLunge;
            public int KnockbackUnits;
            public float Lifesteal;
            public int SummonRequested;
            public int WeaveBridges;
            public float HostileHp;
            public float2 PlayerPos;
        }

        private static string Fingerprint(string organId, string geneId, Engine engine, WorldState world)
            => Observe(organId, geneId, engine, world).Digest;

        /// <summary>
        /// 跑一次真实开火（两拍，推满时间），把能观察到的一切收集起来。
        /// 指纹不同 = 这条基因在这个底盘上确实改变了游戏里发生的事。
        /// </summary>
        private static Observation Observe(string organId, string geneId, Engine engine, WorldState world)
        {
            var sim = new SimBridge();
            try
            {
                sim.Begin(ProbeConfig, System.Array.Empty<BehaviorArchetype>());

                // 摆一片静止的敌人。布局是有讲究的，早期版本因为布得太稀导致大量假"空装"：
                //  · 角度**故意错开默认瞄准方向 (1,0)**——否则 Homing 软锁定算出来的方向
                //    和原方向恰好相同，"追踪"看起来毫无作用；
                //  · 每个角度上从贴脸到 16 单位各摆一个——扇角/触及/光环半径/射程这些
                //    几何差异必须有东西可打才看得见；
                //  · 覆盖到 Field 底盘 11 单位外的布场点，否则布场永远落在空地上。
                for (int ring = 0; ring < 5; ring++)
                {
                    float dist = 2.2f + ring * 3.5f;
                    for (int i = 0; i < 6; i++)
                    {
                        float a = 0.37f + 2f * math.PI * i / 6f;
                        Spawn(sim, new float2(math.cos(a), math.sin(a)) * dist);
                    }
                    // 另加一列**几乎正前方**（5°）的目标：org_cilia 的扇形只有 ±20°，
                    // 上面那圈按 0.37rad(21°) 错开后一个都进不了扇形，
                    // 于是"近战打了多少伤害"这类差异全部读不出来（实测漏判 2 对）。
                    // 5° 仍不与默认瞄准方向 (1,0) 重合，Homing 软锁定照样有可观察偏转。
                    Spawn(sim, new float2(math.cos(0.09f), math.sin(0.09f)) * dist);
                }
                sim.OnUpdate(0.02f);

                var bridge = new MetabolicSliceBridge();
                var abilities = new AbilitySystem();
                bridge.Bind(sim, new StatSheet(), abilities);
                bridge.OnEnter();

                HitEvent evt = Compile(engine, world, organId, geneId);
                if (evt == null)
                {
                    return new Observation { Digest = "ERR 编译未产出 HitEvent" };
                }

                // 开**两拍**，中间把时间推满。理由：
                //  · Weave 是"把新坑连到旧坑上"，第一拍场上还没有旧坑，必须有第二拍才连得起来；
                //  · Echo/Rhythm/挥击窗口都要时间才落地；
                //  · 游戏里本来就是每 TickInterval 打一拍，单拍不是真实节奏。
                for (int cast = 0; cast < 2; cast++)
                {
                    bridge.ApplyEvent(evt);
                    for (int f = 0; f < 8; f++)
                    {
                        sim.OnUpdate(0.05f);
                        bridge.OnUpdate(0.05f);
                    }
                }
                sim.OnUpdate(0.05f);

                float hostileHp = 0f;
                int minions = 0;
                uint statusBits = 0u;
                int statusUnits = 0;
                SimSnapshot snap = sim.Snapshot;
                for (int i = 0; i < snap.Count; i++)
                {
                    if (snap.Alive[i] == 0) { continue; }
                    if (snap.Faction[i] == (byte)SimFaction.Hostile)
                    {
                        hostileHp += snap.Health[i];
                        // Pull/Slow 这类"不掉血但改变战场"的效果只在 Status 位上看得见。
                        if (snap.Status[i] != 0u) { statusUnits++; statusBits |= snap.Status[i]; }
                    }
                    else if (snap.Faction[i] == (byte)SimFaction.PlayerMinion) { minions++; }
                }

                // 主体是 bridge 自己的完整挂起状态摘要（内容级，不是条数级），
                // 再补上只有从世界里才看得到的三样：场上弹体数、小弟数、敌人总血量、玩家坐标。
                var sb = new StringBuilder();
                sb.Append(bridge.DebugStateDigest())
                  .Append("|W:").Append(sim.LiveProjectileCount)
                  .Append(',').Append(minions)
                  .Append(',').Append(F(hostileHp))
                  .Append(',').Append(statusUnits).Append(',').Append(statusBits)
                  .Append(',').Append(F(sim.PlayerPosition.x))
                  .Append(',').Append(F(sim.PlayerPosition.y));

                return new Observation
                {
                    Digest = sb.ToString(),
                    MeleeLunge = bridge.LastMeleeLungeDistance,
                    KnockbackUnits = bridge.LastKnockbackUnitsMoved,
                    Lifesteal = bridge.LifestealHealed,
                    SummonRequested = bridge.LastSummonRequested,
                    WeaveBridges = bridge.WeaveBridgesSpawned,
                    HostileHp = hostileHp,
                    PlayerPos = sim.PlayerPosition,
                };
            }
            catch (System.Exception ex)
            {
                return new Observation { Digest = "ERR " + ex.Message };
            }
            finally
            {
                sim.End();
                sim.OnDispose();
            }
        }

        /// <summary>指纹里的浮点一律定点两位——避免不同机器上的末位噪声造成假差异。</summary>
        private static string F(float v) => v.ToString("F2");

        private static void Spawn(SimBridge sim, float2 pos)
        {
            sim.Spawn(new SpawnRequest
            {
                Position = pos,
                Velocity = float2.zero,
                Health = 100000f,
                Radius = 0.4f,
                MaxSpeed = 0f,
                ArchetypeId = 0,
                Faction = SimFaction.Hostile,
                LogicId = sim.NextLogicId(),
                VisualId = 0,
            });
        }

        private static HitEvent Compile(Engine engine, WorldState world, string organId, string geneId)
        {
            var reserve = new GeneReserve();
            var carrier = new CarrierInstance($"probe_{organId}_{geneId ?? "base"}", organId);
            if (geneId != null)
            {
                var inst = new GeneInstance($"inst_{organId}_{geneId}", geneId, GeneLocation.Reserve());
                reserve.TryAdd(inst);
                carrier.Slots[0].GeneInstanceId = inst.GeneInstanceId;
            }
            List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
            return events.Count > 0 ? events[0] : null;
        }
    }
}
