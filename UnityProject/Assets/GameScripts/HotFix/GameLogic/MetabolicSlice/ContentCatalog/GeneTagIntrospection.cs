using System.Collections.Generic;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// reaction-depth-and-combat-feel story-004：从基因/器官 CreateModule 产出的实例里反射式地读出
    /// 挂了哪些 TagAttach，供卡面/tooltip 自动生成"标签："文案——不需要每条基因手写一遍，也不会因为
    /// 后续改动 GeneCatalog.cs 而漏更新文案（文案与实际生效的 Tag 永远同步）。
    /// </summary>
    public static class GeneTagIntrospection
    {
        public static List<string> FindAttachedTags(IModule module)
        {
            var result = new List<string>();
            Collect(module, result);
            return result;
        }

        private static void Collect(IModule module, List<string> result)
        {
            if (module is TagAttach tagAttach)
            {
                result.Add(tagAttach.Tag);
            }
            else if (module is CompositeModule composite)
            {
                foreach (IModule step in composite.Steps)
                {
                    Collect(step, result);
                }
            }
        }
    }
}
