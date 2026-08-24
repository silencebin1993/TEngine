using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>combat-identity-rework story-004：CATALOG §B 24 条 gene_* 的最小链冒烟。
    /// "无宿主不生效"在本 story 边界内的验证口径：链里没有攻击终端（Actuator）时零 HitEvent产出——
    /// CarrierCompiler 对 CarrierInstance.OrganelleId==null 的垫尾 Actuator 假开火修复留给
    /// 005-compiler（preflight-decisions R2/Dependencies 已定，本 story 不改 CarrierCompiler.cs）。
    /// "有宿主则字段变化"：装该基因 vs 空载基线，HitEvent 标量/Tags 至少一处可观察差异。
    /// 纯 C#，execute_code 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。</summary>
    public static class Gene04SmokeReport
    {
        /// <summary>Required 2 删除的空壳 + gene_delay（CATALOG §B 24 条无此 id，被 gene_echo 取代，见 GeneCatalog 注释）。</summary>
        private static readonly string[] RetiredIds = { "gene_double", "gene_mute", "gene_edge", "gene_share", "gene_delay" };

        private static readonly PropertyInfo[] HitEventScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>) && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        public static (bool Pass, string Reason) Run()
        {
            var idList = GeneCatalog.AllGeneIds.OrderBy(x => x).ToList();
            if (idList.Count != 24)
            {
                return (false, $"gene_* 总数应为 24，实际 {idList.Count}：{string.Join(",", idList)}");
            }

            foreach (var id in RetiredIds)
            {
                if (GeneCatalog.Get(id) != null || GeneCatalog.GetModule(id) != null)
                {
                    return (false, $"{id} 应已删除授予路径，但 Get/GetModule 仍命中");
                }
            }

            foreach (var id in idList)
            {
                var factory = GeneCatalog.GetModule(id);
                if (factory == null)
                {
                    return (false, $"{id} GetModule 返回 null");
                }
                var desc = GeneCatalog.GetDescription(id);
                if (desc == null || !desc.StartsWith("需要器官。改装："))
                {
                    return (false, $"{id} 描述未以「需要器官。改装：」开头：{desc}");
                }

                var engine = new Engine();

                // 无宿主：链里没有攻击终端（Actuator），只有 EnergyCore+前置装置+基因本身——应 0 HitEvent。
                var primer = Primer(id);
                var noHostChain = new List<IModule> { new EnergyCore(10f) };
                noHostChain.AddRange(primer);
                noHostChain.Add(factory());
                var noHostEvents = engine.RunAssembly(noHostChain);
                if (noHostEvents.Count != 0)
                {
                    return (false, $"{id} 无宿主（无 Actuator 终端）仍产出 {noHostEvents.Count} 条 HitEvent，基因不应在无宿主时进入编译产物");
                }

                // 有宿主：EnergyCore+前置装置+基因+Actuator vs EnergyCore+前置装置+Actuator 基线，断言字段有可观察差异。
                // gene_heatshock 只在 Packet.Heat 已过阈值时才分岔（见 HeatShockModule），需要一个前置产热
                // 模块把两条链都垫到同一高热基线，才能孤立出该基因自身的效果（同 IdSmokeReport.RunRadiator 先例）。
                var geneChain = new List<IModule> { new EnergyCore(10f) };
                geneChain.AddRange(primer);
                geneChain.Add(factory());
                geneChain.Add(new Actuator());
                var baseChain = new List<IModule> { new EnergyCore(10f) };
                baseChain.AddRange(primer);
                baseChain.Add(new Actuator());

                var geneEvents = engine.RunAssembly(geneChain);
                var baseEvents = engine.RunAssembly(baseChain);
                if (geneEvents.Count != 1 || baseEvents.Count != 1)
                {
                    return (false, $"{id} 有宿主链产出事件数异常：gene={geneEvents.Count} base={baseEvents.Count}");
                }

                if (!Differs(geneEvents[0], baseEvents[0]))
                {
                    return (false, $"{id} 有宿主时 HitEvent 无可观察差异（字段/Tags 与空载基线相同）");
                }
            }

            return (true, $"24/24 gene_* 可查询、描述合规、无宿主 0 事件、有宿主字段有差异；" +
                $"{RetiredIds.Length} 条旧基因（{string.Join(",", RetiredIds)}）Get/GetModule 均 null");
        }

        /// <summary>大多数基因的效果与入链前的 Packet 状态无关，空前置即可。gene_heatshock 只在
        /// Packet.Heat 已过阈值时才分岔（HeatShockModule 默认阈值 8），需要前置一个产热模块把两条链
        /// （测试链/基线链）都垫到同一高热状态，才能孤立出该基因自身的效果。</summary>
        private static IEnumerable<IModule> Primer(string id) =>
            id == "gene_heatshock" ? new IModule[] { new FocusLens(focusMult: 1f, heatPerFocus: 10f) } : System.Array.Empty<IModule>();

        private static bool Differs(HitEvent a, HitEvent b)
        {
            foreach (var prop in HitEventScalarProps)
            {
                var av = prop.GetValue(a);
                var bv = prop.GetValue(b);
                if (!Equals(av, bv)) return true;
            }
            var tagDiff = new HashSet<string>(a.Tags);
            tagDiff.SymmetricExceptWith(b.Tags);
            return tagDiff.Count > 0;
        }
    }
}
