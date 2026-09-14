using System.Collections.Generic;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.Blueprint
{
    /// <summary>M3-02 实施第 5 条：旧存档默认蓝图迁移。
    /// M3-02 前，<see cref="OrganelleCatalog"/>/<see cref="GeneCatalog"/> 全目录对所有玩家隐式开放——
    /// 抽卡/掉落/装配从不检查"蓝图是否解锁"。首次读到"没有蓝图库存档文件"时必须让老玩家维持
    /// "什么都能用"，否则蓝图库上线当天就是一次隐性内容倒退。默认迁移 = 全目录逐条标记为
    /// 已解锁满完整度，不是"什么都没有"。</summary>
    public static class BlueprintMigration
    {
        public static BlueprintHistory BuildLegacyDefaults()
        {
            var list = new List<BlueprintEntry>();
            foreach (string id in OrganelleCatalog.All.Keys)
            {
                list.Add(new BlueprintEntry(id, BlueprintSourceKind.Organelle, completeness: 1f));
            }
            foreach (string id in GeneCatalog.AllGeneIds)
            {
                list.Add(new BlueprintEntry(id, BlueprintSourceKind.Gene, completeness: 1f));
            }
            return new BlueprintHistory(list);
        }
    }
}
