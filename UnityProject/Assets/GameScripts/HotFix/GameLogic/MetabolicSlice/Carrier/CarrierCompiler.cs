using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Lineage;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>story-003：把一个激活 Carrier 的槽内基因编译成 RuleVector + Module 链，跑出 HitEvent。
    /// 与旧 PathCompiler+MetabolicSliceRunner.Tick(grid, …) 并行新增，不改旧路径（W12/D5）。
    /// 内建 EnergyCore 是链固定头，不占玩家插槽（D1/D2）；story-005 起链尾恒为激活器官自身的攻击模块
    /// （AttackMethod==true 才有链尾），不再垫 Actuator 当非攻击回退。
    /// M3-04：实物轨（<see cref="Compile"/>）与配方轨（<see cref="CompileFromRecipe"/>）共用同一条
    /// 化学链路——Compile 只做"从实例解出有序 GeneId 列表"这一步，剩下全部转调 CompileFromRecipe。</summary>
    public static class CarrierCompiler
    {
        public static List<HitEvent> Compile(Engine engine, CarrierInstance carrier, GeneReserve reserve, WorldState world, int seed, string cellId = null)
        {
            if (carrier == null)
            {
                return new List<HitEvent>();
            }

            var geneIds = new List<string>();
            for (int i = 0; i < carrier.Slots.Count; i++)
            {
                var slot = carrier.Slots[i];
                if (slot.GeneInstanceId == null)
                {
                    continue;
                }

                var gene = reserve.Find(slot.GeneInstanceId);
                if (gene == null)
                {
                    TEngine.Log.Warning($"[CarrierCompiler] 悬空基因实例引用: slot={i} geneInstanceId={slot.GeneInstanceId}");
                    continue;
                }

                geneIds.Add(gene.GeneId);
            }

            return CompileFromRecipe(engine, carrier.OrganelleId, geneIds, world, seed, cellId);
        }

        /// <summary>M3-04：模板/配方轨编译入口，与 <see cref="Compile"/> 逐字同逻辑，唯一差别是基因来源
        /// 直接收 GeneId 有序列表（不经 GeneReserve 按实例查）。静态相位（NormalizeContracts 产出的
        /// 规则向量 + 模块工厂委托）按装配签名缓存在 <see cref="CompiledRecipeCache"/>；动态相位
        /// （RunAssembly/ApplyPipeline）每次调用都重新跑，不跨单位共用 seed/世界状态产物
        /// （Lineage_Loadout_Mapping.md §2.4：否则 100 个同模板单位会共用第一次编译那一刻的结果）。</summary>
        public static List<HitEvent> CompileFromRecipe(Engine engine, string organelleId, IReadOnlyList<string> geneIds, WorldState world, int seed, string cellId = null)
        {
            string signature = PhenotypeTemplateSignature.Compute(organelleId, geneIds);
            CompiledRecipe recipe = CompiledRecipeCache.GetOrBuild(signature, () => BuildRecipe(engine, organelleId, geneIds));

            if (recipe.IsEmpty)
            {
                return new List<HitEvent>();
            }

            var chain = new List<IModule> { new EnergyCore(10f) };
            foreach (var factory in recipe.ModuleGeneFactories)
            {
                chain.Add(factory());
            }
            chain.Add(recipe.TailModuleFactory());

            var raw = engine.RunAssembly(chain, ticks: 1, seed: seed);
            var events = new List<HitEvent>();
            foreach (var evt in raw)
            {
                if (cellId != null) evt.TargetId = cellId;
                events.Add(engine.ApplyPipeline(evt, recipe.Rules, world));
            }
            return events;
        }

        /// <summary>静态相位：只依赖 OrganelleId + 有序 GeneIds，不吃 seed/WorldState/cellId——
        /// 缓存的是链配方（规则向量 + 模块工厂委托），不是已 new 好的 IModule 实例（链上每件都是
        /// 现造的，如 new EnergyCore(10f)/tailDef.CreateModule()，跨单位共用实例等于共用模块内部状态）。</summary>
        private static CompiledRecipe BuildRecipe(Engine engine, string organelleId, IReadOnlyList<string> geneIds)
        {
            var tailDef = organelleId != null ? OrganelleCatalog.Get(organelleId) : null;
            if (tailDef == null || !tailDef.AttackMethod)
            {
                return CompiledRecipe.Empty;
            }

            var contracts = new List<IContract>();
            var moduleGeneFactories = new List<Func<IModule>>();

            foreach (var geneId in geneIds)
            {
                var createContract = GeneCatalog.Get(geneId);
                if (createContract != null)
                {
                    contracts.Add(createContract());
                    continue;
                }

                var createModule = GeneCatalog.GetModule(geneId);
                if (createModule != null)
                {
                    moduleGeneFactories.Add(createModule);
                    continue;
                }

                TEngine.Log.Warning($"[CarrierCompiler] 基因 {geneId} 未在 GeneCatalog 命中（004 迁徙前占位，或拼写错误）");
            }

            var rules = engine.NormalizeContracts(contracts);
            return new CompiledRecipe(rules, moduleGeneFactories, tailDef.CreateModule);
        }
    }
}
