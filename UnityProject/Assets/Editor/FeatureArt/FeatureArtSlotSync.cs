using System;
using GameLogic.ArtBinding;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>补入数据中的未呈现槽位。资源定义仅来自序列化数据，不再生成题材专用清单。</summary>
    public static class FeatureArtSlotSync
    {
        public static int Sync(FeatureArtCatalogData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return FeatureArtWorkspaceStore.Current.IncludeCatalog(data);
        }
    }
}
