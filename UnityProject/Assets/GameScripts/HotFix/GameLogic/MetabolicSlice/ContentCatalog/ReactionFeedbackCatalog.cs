using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.Localization;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 旧反应引擎的具名反应 id（<c>HitEvent.Payload["Reaction"]</c> 里的字符串，如 "Conduct"）→ 玩家可读的播报文案与图鉴说明。
    ///
    /// FG2-FW-03（FG02 FGR-FW-041；FG-GAP-010）：名字与说明不再手抄在代码里（旧表里是 Demo 的生物名：溶血、糖浆、泥泞、败血……），
    /// 一律来自具名反应表 fg.TbReaction 的机械名文本键（经行为探针核实的触发配对与效果写在说明里），随语言切换；题材审计扫描的就是这些文本键。
    /// 查不到的 id 显示“未知反应”，不把内部字符串漏给玩家。
    /// </summary>
    public static class ReactionFeedbackCatalog
    {
        /// <summary>战斗飘字用的短标签（“短路！”）。</summary>
        public static string GetLabel(string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId))
            {
                return "";
            }
            return NamedReactionCatalog.TryGetByLegacyName(reactionId, out GameConfig.fg.Reaction row)
                ? GameText.Format("reaction.cue", GameText.Get(row.NameKey))
                : GameText.Get("reaction.unnamed");
        }

        /// <summary>全部已知反应的旧 id（图鉴“已发现反应”页遍历全量目录用；与 <see cref="GetLabel"/> / <see cref="GetDescription"/> 同一 key 空间）。</summary>
        public static IEnumerable<string> AllReactionIds =>
            NamedReactionCatalog.Rows.Where(r => r != null && r.Kind == NamedReactionCatalog.KindTag && !string.IsNullOrEmpty(r.LegacyName) && r.LegacyName != "none")
                .Select(r => r.LegacyName);

        /// <summary>图鉴用的完整说明（触发条件与效果）。</summary>
        public static string GetDescription(string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId))
            {
                return "";
            }
            return NamedReactionCatalog.TryGetByLegacyName(reactionId, out GameConfig.fg.Reaction row)
                ? GameText.Get(row.NameKey) + "：" + GameText.Get(row.DescKey)
                : GameText.Get("reaction.unnamed");
        }
    }
}
