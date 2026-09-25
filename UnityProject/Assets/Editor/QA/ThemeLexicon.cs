using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 题材审计词表的唯一来源（ER2-THEME-01 / ER8-CONTENT-01 静态审计 + FG0-DOC-01 的 0.2 名表扩展）。
    /// 两组词必须与 production/design/earth-reclamation/TERM-MIGRATION.md 的 §2.3 与 §6.7 逐词一致——
    /// <see cref="DesignDocsAuditSelfCheck"/> 会解析该文档核对两个方向，并用 §6 的全部正式名做反例，
    /// 保证词表既不漏词也不误伤正式名。玩家可见文字的各项扫描（字幕、目标、面板 UXML）都读 <see cref="Forbidden"/>。
    /// </summary>
    internal static class ThemeLexicon
    {
        /// <summary>TERM-MIGRATION §2.3（Demo 0.1 起）：旧生物题材词。</summary>
        internal static readonly string[] LegacyBioWords =
        {
            "基因", "器官", "吞噬", "代谢", "谱系", "萌生", "债兽", "细胞", "孢子", "菌丝",
        };

        /// <summary>TERM-MIGRATION §6.7（0.2 正式版名表）：内部代号（阵营 / 区域 / 首领）、废弃原名、旧资源名、旧反应与旧器官的生物名。</summary>
        internal static readonly string[] FgNameTableWords =
        {
            "化工", "无人舰群", "沉淀反应塔", "暂名",
            "苛性灼烧", "溶血", "糖浆", "泥泞", "败血",
            "叶绿体", "线粒体", "纤毛", "伪足", "鞭毛",
            "生物质",
            "登陆场", "静默领地", "铸造领地", "超频领地", "静默首领", "铸造首领", "超频首领",
        };

        internal static IEnumerable<string> AllWords => LegacyBioWords.Concat(FgNameTableWords);

        /// <summary>任一禁用词命中即为违规；玩家可见文字扫描统一用它。</summary>
        internal static readonly Regex Forbidden =
            new Regex(string.Join("|", LegacyBioWords.Concat(FgNameTableWords).Select(Regex.Escape)));

        /// <summary>返回 <paramref name="text"/> 里第一个命中的禁用词；没有命中返回 null。</summary>
        internal static string FirstHit(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            Match m = Forbidden.Match(text);
            return m.Success ? m.Value : null;
        }
    }
}
