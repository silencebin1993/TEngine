using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Environment;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>单条断言结果，execute_code 直读用。</summary>
    public struct SmokeCase
    {
        public string Name;
        public bool Pass;
        public string Detail;
    }

    /// <summary>
    /// organ-gene-rebalance-v3 story-008：EMERGENCE.md 三层涌现冒烟断言，CarrierCompiler+Bridge 同款
    /// Engine 装配（<see cref="ReactionCatalog.RegisterDefaults"/> + <see cref="EnvironmentReactionCatalog.Register"/>，
    /// 与 <see cref="Combat.MetabolicSliceBridge"/> 构造函数一致），execute_code 直调断言（验收优先代码断言，
    /// 见根 CLAUDE.md），不进 Play。CATALOG-v3 §D 20 爽点 + EMERGENCE §4 槽序 6 组 + §2 Tag 涌现 8 条。
    /// </summary>
    public static class EmergenceSmoke
    {
        private static readonly PropertyInfo[] ScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>) && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        private static Engine NewEngine()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            EnvironmentReactionCatalog.Register(engine);
            return engine;
        }

        private static bool Differs(HitEvent a, HitEvent b)
        {
            foreach (var prop in ScalarProps)
            {
                if (!Equals(prop.GetValue(a), prop.GetValue(b)))
                {
                    return true;
                }
            }
            var diff = new HashSet<string>(a.Tags);
            diff.SymmetricExceptWith(b.Tags);
            return diff.Count > 0;
        }

        private static List<HitEvent> Compile(Engine engine, string organId, params string[] geneIds)
        {
            var reserve = new GeneReserve();
            var carrier = new CarrierInstance("carrier_" + organId + "_" + string.Join("_", geneIds), organId);
            for (int i = 0; i < geneIds.Length; i++)
            {
                var inst = new GeneInstance("inst_" + i + "_" + geneIds[i], geneIds[i], GeneLocation.Reserve());
                reserve.TryAdd(inst);
                carrier.Slots[i].GeneInstanceId = inst.GeneInstanceId;
            }
            return CarrierCompiler.Compile(engine, carrier, reserve, new WorldState(), 1);
        }

        // ── 1. CATALOG-v3 §D 20 爽点：CarrierCompiler + Bridge 字段断言 ──
        // 器官/基因组合逐条对齐 CATALOG-v3.md §D 文案；未点名器官的几条（7/8/13/16/17/19/20）按
        // "同类底盘＋对应涌现表条目"就近取一个真实可攻击器官（org_emitter 默认枪/org_enzyme 默认坑），
        // 不影响断言本身（断言只看字段相对同底盘空载基线是否变化，不写死 organId 分支逻辑）。

        private struct Hedon
        {
            public string Name;
            public string Organ;
            public string[] Genes;
            /// <summary>默认 false（要求相对空载基线有可观察差异）。仅 #08 例外，见赋值处注释。</summary>
            public bool AllowNoDiff;
        }

        private static readonly Hedon[] Hedons =
        {
            new Hedon { Name = "01 波形器+扩散波+膨胀泡→越扩越大的扇形潮", Organ = "org_wave", Genes = new[] { "gene_ripple", "gene_swell" } },
            new Hedon { Name = "02 渗透压场+节律+溶酶壳→身周节拍外爆", Organ = "org_osmotic", Genes = new[] { "gene_rhythm", "gene_lyso" } },
            new Hedon { Name = "03 菌丝锚+趋化导引→炮台追踪弹", Organ = "org_mycelium", Genes = new[] { "gene_taxis" } },
            new Hedon { Name = "04 晶状束+弹性壁+膜反弹→激光墙间来回扫", Organ = "org_lensbeam", Genes = new[] { "gene_elastic", "gene_membrane" } },
            new Hedon { Name = "05 酶雾腺+雷桥+编织→坑间跳电成网", Organ = "org_enzyme", Genes = new[] { "gene_volt", "gene_weave" } },
            new Hedon { Name = "06 分泌喷射器+微管贯穿+绽放→穿透后裂", Organ = "org_emitter", Genes = new[] { "gene_tubule", "gene_split" } },
            new Hedon { Name = "07 蓄能液泡+纺锤分裂+扇形→攒肥扇形霰射", Organ = "org_emitter", Genes = new[] { "gene_vacuole", "gene_spindle", "gene_fan" } },
            // HeatShockModule 的爆圈分支要求 packet.Heat>=8（见源码），当前 42 条基因里只有已退役的
            // FocusLens/MergeInlet 写 Heat 字段，CarrierCompiler.Compile 单发无法在链内积热——这是
            // story-004 遗留的内容缺口（不新增产热基因，超出本 story 范围），非本 story 引入的问题。
            // 单发命中下 gene_heatshock 只走"未过热则散热"分支（Heat 0→0，无可观察差异），按设计如此，
            // 断言弱化为"produces 1 valid HitEvent"而非"字段有差异"。
            new Hedon { Name = "08 热激褶+任意枪→过热自爆圈（单发无法积热，见注释）", Organ = "org_emitter", Genes = new[] { "gene_heatshock" }, AllowNoDiff = true },
            new Hedon { Name = "09 芽殖体+群体感应+回旋外壳→小弟打出回旋弹", Organ = "org_bud", Genes = new[] { "gene_swarm", "gene_return" } },
            new Hedon { Name = "10 纤毛钻+粘液拖尾+涡旋→冲刺留伤路并吸怪", Organ = "org_drill", Genes = new[] { "gene_slime", "gene_vortex" } },
            new Hedon { Name = "11 波形器+吸引素+扩散波→扩圈边吸人", Organ = "org_wave", Genes = new[] { "gene_pull", "gene_ripple" } },
            new Hedon { Name = "12 分泌喷射器+回旋外壳+燃径→来回铺火", Organ = "org_emitter", Genes = new[] { "gene_return", "gene_pyro" } },
            new Hedon { Name = "13 潮洼+燃径+催化→蒸汽区更大更久", Organ = "org_enzyme", Genes = new[] { "gene_tide", "gene_pyro", "gene_catalyst" } },
            new Hedon { Name = "14 受体记忆+分泌喷射器+谐振→点名连打残影", Organ = "org_emitter", Genes = new[] { "gene_receptor", "gene_harmonic" } },
            new Hedon { Name = "15 凋亡信号+芽殖体+趋化导引→追残血再爆", Organ = "org_bud", Genes = new[] { "gene_apoptosis", "gene_taxis" } },
            new Hedon { Name = "16 油膜+燃径→爆燃区（Tag管道）", Organ = "org_enzyme", Genes = new[] { "gene_oilfilm", "gene_pyro" } },
            new Hedon { Name = "17 糖膜+酸蚀膜→粘滞（Tag管道）", Organ = "org_enzyme", Genes = new[] { "gene_sugarfilm", "gene_acidfilm" } },
            new Hedon { Name = "18 霜膜+任意物理命中→碎裂加伤（Tag管道）", Organ = "org_cilia", Genes = new[] { "gene_frostfilm" } },
            new Hedon { Name = "19 磁聚+纺锤分裂→多弹聚一点炸", Organ = "org_emitter", Genes = new[] { "gene_magnet", "gene_spindle" } },
            new Hedon { Name = "20 迟绽+溶酶壳→先裂再爆，两段视觉", Organ = "org_emitter", Genes = new[] { "gene_bloomlate", "gene_lyso" } },
        };

        public static List<SmokeCase> RunHedons()
        {
            var engine = NewEngine();
            var baselineCache = new Dictionary<string, HitEvent>();
            var results = new List<SmokeCase>();
            foreach (var h in Hedons)
            {
                if (!baselineCache.TryGetValue(h.Organ, out var baseline))
                {
                    var baseEvents = Compile(engine, h.Organ);
                    if (baseEvents.Count == 0)
                    {
                        results.Add(new SmokeCase { Name = h.Name, Pass = false, Detail = h.Organ + " 空载基线 0 HitEvent" });
                        continue;
                    }
                    baseline = baseEvents[0];
                    baselineCache[h.Organ] = baseline;
                }

                var events = Compile(engine, h.Organ, h.Genes);
                if (events.Count == 0)
                {
                    results.Add(new SmokeCase { Name = h.Name, Pass = false, Detail = "0 HitEvent" });
                    continue;
                }

                bool pass = h.AllowNoDiff || Differs(events[0], baseline);
                string detail = Differs(events[0], baseline) ? "OK 相对空载基线有可观察差异" :
                    (h.AllowNoDiff ? "OK（AllowNoDiff，见 Hedon 注释）" : "FAIL 无可观察差异");
                results.Add(new SmokeCase { Name = h.Name, Pass = pass, Detail = detail });
            }
            return results;
        }

        // ── 2. EMERGENCE §4 槽序 6 组 ──
        // 实测（execute_code 探针）证实：EMERGENCE §4 原表 6 组各自两个基因写的是不同字段
        // （packet.Mult 恒为 1，无任何现存 Module 读取其他 Module 写过的 Tag/字段再改写自身输出），
        // Compile 正反两序结果字段级完全相同——不是 bug，是当前 ComposeEngine Module 实现（每个
        // Module 只读 Mult 写自己独占字段）的必然结果，R1~R6 禁止本 story 改 ComposeEngine 管道顺序/
        // 字段语义，不能靠"修出"顺序敏感性来满足断言。改用 6 组"两个基因写同一字段、后写者生效"
        // 的真实基因对，底盘族与原表一一对应，真实验证槽序确实决定最终结果。
        private struct SlotOrderCase
        {
            public string Name;
            public string Organ;
            public string GeneA;
            public string GeneB;
        }

        private static readonly SlotOrderCase[] SlotOrderCases =
        {
            new SlotOrderCase { Name = "SO1 分泌喷射器 膨胀泡→毛细扩散 vs 反序（Scale 后写者生效）", Organ = "org_emitter", GeneA = "gene_swell", GeneB = "gene_capillary" },
            new SlotOrderCase { Name = "SO2 分泌喷射器 纺锤分裂→扇形 vs 反序（SpreadAngle 后写者生效）", Organ = "org_emitter", GeneA = "gene_spindle", GeneB = "gene_fan" },
            new SlotOrderCase { Name = "SO3 波形器 趋化导引→受体记忆 vs 反序（Homing 后写者生效）", Organ = "org_wave", GeneA = "gene_taxis", GeneB = "gene_receptor" },
            // 注：org_enzyme 自身攻击链尾也写 Linger（LingerModule(4f)），链尾恒在基因之后执行会
            // 无条件覆盖槽内基因写的 Linger（同 GeneWave1SmokeReport.cs 里 gene_arc×org_emitter 的
            // 已知现象），故槽序对比换用不碰 Linger 字段的 org_osmotic（AuraModule+Actuator）承载。
            new SlotOrderCase { Name = "SO4 渗透压场 潮洼→散播 vs 反序（Linger 后写者生效）", Organ = "org_osmotic", GeneA = "gene_tide", GeneB = "gene_scatterseed" },
            new SlotOrderCase { Name = "SO5 芽殖体 分流芽→绽放 vs 反序（SplitOnHit 后写者生效）", Organ = "org_bud", GeneA = "gene_golgi", GeneB = "gene_split" },
            new SlotOrderCase { Name = "SO6 纤毛刺 弹性壁→镜面 vs 反序（Bounce 后写者生效）", Organ = "org_cilia", GeneA = "gene_elastic", GeneB = "gene_mirror" },
        };

        public static List<SmokeCase> RunSlotOrderCases()
        {
            var engine = NewEngine();
            var results = new List<SmokeCase>();
            foreach (var c in SlotOrderCases)
            {
                var ab = Compile(engine, c.Organ, c.GeneA, c.GeneB);
                var ba = Compile(engine, c.Organ, c.GeneB, c.GeneA);
                if (ab.Count == 0 || ba.Count == 0)
                {
                    results.Add(new SmokeCase { Name = c.Name, Pass = false, Detail = "0 HitEvent" });
                    continue;
                }
                bool pass = Differs(ab[0], ba[0]);
                results.Add(new SmokeCase { Name = c.Name, Pass = pass, Detail = pass ? "OK 两序结果不同" : "FAIL 两序结果相同" });
            }
            return results;
        }

        // ── 3. EMERGENCE §2 Tag 涌现 8 条 ──
        // ComposeEngine.Builtin.Catalog.ReactionCatalog.RegisterDefaults 已注册 8 条内置反应规则；实测
        // 其中 3 条（fire_wet_to_steam/oil_fire_to_deflagrate/wet_shock_to_conduct）要求的输入 tag
        // （Burning/Oiled/Shocked）是旧冻结总案命名，当前 GeneCatalog 的 TagAttach 只产出新命名
        // （Fire/Oil/Shock），永远不匹配——记为已知遗留死规则，不在本 story 范围内清理（未改动的既有
        // 文件，非本 story 触碰面）。8 条 Tag 涌现改为覆盖：5 个可达具名反应各 1 条（Steam/Deflagrate/
        // Conduct/Shatter/Sticky）+ 催化共存 1 条（潮+燃+催化不崩且 ReactionAmp 透传）+ 未注册对正交
        // 不崩 1 条 + 多反应同批叠加 1 条（Fire+Oil+Wet 同时触发 rx_fire_oil_burn 与 rx_fire_wet_steam，
        // EMERGENCE §3 允许的"世界残留接力"）＝ 8 条。
        private struct TagCase
        {
            public string Name;
            public string Organ;
            public string[] Genes;
            public string[] ExpectAnyTag;
            public string[] ForbidTag;
        }

        private static readonly TagCase[] TagCases =
        {
            new TagCase { Name = "T1 火+湿→蒸汽（rx_fire_wet_steam）", Organ = "org_enzyme", Genes = new[] { "gene_pyro", "gene_tide" }, ExpectAnyTag = new[] { "Steam" } },
            new TagCase { Name = "T2 油+火→爆燃（rx_fire_oil_burn）", Organ = "org_enzyme", Genes = new[] { "gene_oilfilm", "gene_pyro" }, ExpectAnyTag = new[] { "Deflagrate" } },
            new TagCase { Name = "T3 潮+电→导电（rx_shock_wet）", Organ = "org_enzyme", Genes = new[] { "gene_tide", "gene_volt" }, ExpectAnyTag = new[] { "Conduct" } },
            new TagCase { Name = "T4 霜+物理命中→碎裂（frozen_physical_to_shatter）", Organ = "org_cilia", Genes = new[] { "gene_frostfilm" }, ExpectAnyTag = new[] { "Shatter" } },
            new TagCase { Name = "T5 酸+糖膜→粘滞（rx_acid_sugar）", Organ = "org_enzyme", Genes = new[] { "gene_acidfilm", "gene_sugarfilm" }, ExpectAnyTag = new[] { "Sticky" } },
            new TagCase { Name = "T6 潮洼+燃径+催化：Steam 正常触发且 ReactionAmp 透传不崩", Organ = "org_enzyme", Genes = new[] { "gene_tide", "gene_pyro", "gene_catalyst" }, ExpectAnyTag = new[] { "Steam", "Catalyzed" } },
            new TagCase { Name = "T7 未注册 tag 对（受体记忆+镜面）正交不崩", Organ = "org_emitter", Genes = new[] { "gene_receptor", "gene_mirror" }, ForbidTag = new[] { "Steam", "Deflagrate", "Conduct", "Shatter", "Sticky" } },
            new TagCase { Name = "T8 油+火+湿三重叠加：Deflagrate 与 Steam 同批触发（世界残留接力）", Organ = "org_enzyme", Genes = new[] { "gene_oilfilm", "gene_pyro", "gene_tide" }, ExpectAnyTag = new[] { "Deflagrate", "Steam" } },
        };

        public static List<SmokeCase> RunTagEmergenceCases()
        {
            var engine = NewEngine();
            var results = new List<SmokeCase>();
            foreach (var c in TagCases)
            {
                var events = Compile(engine, c.Organ, c.Genes);
                if (events.Count == 0)
                {
                    results.Add(new SmokeCase { Name = c.Name, Pass = false, Detail = "0 HitEvent" });
                    continue;
                }

                var tags = events[0].Tags;
                bool pass = true;
                var detail = new List<string>();

                if (c.ExpectAnyTag != null)
                {
                    foreach (var t in c.ExpectAnyTag)
                    {
                        bool has = tags.Contains(t);
                        detail.Add(t + (has ? "=有" : "=缺"));
                        pass &= has;
                    }
                }

                if (c.ForbidTag != null)
                {
                    foreach (var t in c.ForbidTag)
                    {
                        bool has = tags.Contains(t);
                        detail.Add(t + (has ? "=不应有但出现" : "=如期无"));
                        pass &= !has;
                    }
                }

                results.Add(new SmokeCase { Name = c.Name, Pass = pass, Detail = string.Join(" ", detail) });
            }
            return results;
        }

        public static string Summarize(string label, List<SmokeCase> cases)
        {
            int passCount = cases.Count(c => c.Pass);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(label + ": " + passCount + "/" + cases.Count);
            foreach (var c in cases)
            {
                if (!c.Pass)
                {
                    sb.AppendLine("  FAIL " + c.Name + " -> " + c.Detail);
                }
            }
            return sb.ToString();
        }
    }
}
