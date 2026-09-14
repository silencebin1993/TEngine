using System;
using System.Collections.Generic;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>M3-04：静态相位编译产物。只存"配方"——规则向量与模块工厂委托，不存已 new 好的
    /// <see cref="IModule"/> 实例（链上每件都是现造的，跨单位共用实例等于共用模块内部状态）。
    /// 命中缓存后仍各自调用工厂现造模块链，动态相位（RunAssembly/ApplyPipeline）不进本类型。</summary>
    public sealed class CompiledRecipe
    {
        public static readonly CompiledRecipe Empty = new CompiledRecipe(null, null, null);

        public readonly RuleVector Rules;
        public readonly IReadOnlyList<Func<IModule>> ModuleGeneFactories;
        public readonly Func<IModule> TailModuleFactory;

        /// <summary>链尾器官不是攻击方式（或查无 id）——对应旧 Compile 早退返回空 HitEvent 列表的分支。</summary>
        public bool IsEmpty => TailModuleFactory == null;

        public CompiledRecipe(RuleVector rules, IReadOnlyList<Func<IModule>> moduleGeneFactories, Func<IModule> tailModuleFactory)
        {
            Rules = rules;
            ModuleGeneFactories = moduleGeneFactories;
            TailModuleFactory = tailModuleFactory;
        }
    }

    /// <summary>M3-04：静态相位编译结果缓存，key = 装配签名（<see cref="GameLogic.MetabolicSlice.Lineage.PhenotypeTemplateSignature"/>，
    /// 只认 OrganelleId + 有序 GeneIds，不认 seed/WorldState）。
    ///
    /// 内容地址式缓存：同一签名永远对应同一份静态编译结果，模板提交新版本只是换了新签名，
    /// 不需要主动追踪失效旧签名——"模板失效时安全淘汰缓存"落地成简单 LRU 容量控制，
    /// 防止长会话里签名无界堆积，不是版本号驱动的主动失效（Lineage_Loadout_Mapping.md §2.4：
    /// 动态相位吃 seed/WorldState/cellId，绝不进本缓存，否则 100 个同模板单位会共用第一次编译
    /// 那一刻的世界状态与随机种子）。</summary>
    public static class CompiledRecipeCache
    {
        private const int Capacity = 256;

        private static readonly Dictionary<string, CompiledRecipe> _entries = new Dictionary<string, CompiledRecipe>();
        private static readonly LinkedList<string> _lru = new LinkedList<string>();
        private static readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new Dictionary<string, LinkedListNode<string>>();

        /// <summary>自检埋点：静态相位真实被构建（未命中缓存）的次数——验收"100 个同模板单位只编译一次"
        /// 就是断言同一签名重复调用后本计数只增 1。</summary>
        public static int BuildCount { get; private set; }

        public static CompiledRecipe GetOrBuild(string signature, Func<CompiledRecipe> build)
        {
            if (_entries.TryGetValue(signature, out var cached))
            {
                Touch(signature);
                return cached;
            }

            var recipe = build();
            BuildCount++;
            _entries[signature] = recipe;
            _lruNodes[signature] = _lru.AddLast(signature);
            EvictIfNeeded();
            return recipe;
        }

        private static void Touch(string signature)
        {
            if (!_lruNodes.TryGetValue(signature, out var node))
            {
                return;
            }

            _lru.Remove(node);
            _lruNodes[signature] = _lru.AddLast(signature);
        }

        private static void EvictIfNeeded()
        {
            while (_entries.Count > Capacity && _lru.Count > 0)
            {
                var oldest = _lru.First.Value;
                _lru.RemoveFirst();
                _lruNodes.Remove(oldest);
                _entries.Remove(oldest);
            }
        }

        /// <summary>自检专用：清空缓存与计数，避免不同自检小节互相污染彼此的 BuildCount。</summary>
        public static void ResetForTest()
        {
            _entries.Clear();
            _lru.Clear();
            _lruNodes.Clear();
            BuildCount = 0;
        }
    }
}
