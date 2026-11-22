using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace GameLogic.Localization
{
    /// <summary>
    /// FG15-UX-03（FGR-SYS-090“字体覆盖完整的中日韩字符集，并有后备字体”）：主字体是动态的 Noto Sans SC（文本表里的字全部有字形，自检 J 段核），
    /// 玩家自己输入的文字（机器名、蓝图名）可能有它没有的日文假名 / 韩文 / 生僻字 / 符号——给共享 PanelTextSettings 挂一串系统字体做后备。
    /// 后备字体运行时按系统字体创建（不打进包、不改资产文件）；编辑器里退出 Play 时摘掉，不把运行时对象留在资产上。
    /// </summary>
    public static class UiFontFallback
    {
        /// <summary>按顺序尝试的系统字体族（Windows 优先，其余平台的同类字体随后）；本机没有的跳过。</summary>
        public static readonly string[] OsFamilies =
        {
            "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Segoe UI Symbol",
            "PingFang SC", "Hiragino Sans", "Apple SD Gothic Neo", "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR",
        };

        private static readonly List<FontAsset> Created = new List<FontAsset>();
        private static readonly List<TextSettings> Patched = new List<TextSettings>();
        private static bool _quitHooked;

        /// <summary>给文本设置补上系统后备字体（已补过的不重复补）。返回本次新挂上的个数。</summary>
        public static int Ensure(TextSettings settings)
        {
            if (settings == null)
            {
                return 0;
            }
            settings.fallbackFontAssets ??= new List<FontAsset>();
            if (Patched.Contains(settings))
            {
                return 0;
            }
            if (Created.Count == 0)
            {
                foreach (string family in OsFamilies)
                {
                    FontAsset f = FontAsset.CreateFontAsset(family, "Regular");
                    if (f != null)
                    {
                        f.name = "OsFallback " + family;
                        f.hideFlags = HideFlags.DontSave;
                        Created.Add(f);
                    }
                }
            }
            int added = 0;
            foreach (FontAsset f in Created)
            {
                if (!settings.fallbackFontAssets.Contains(f))
                {
                    settings.fallbackFontAssets.Add(f);
                    added++;
                }
            }
            Patched.Add(settings);
            if (!_quitHooked && Application.isPlaying)
            {
                _quitHooked = true;
                Application.quitting += Restore;
            }
            return added;
        }

        /// <summary>把挂上的后备字体从各文本设置里摘掉（编辑器退出 Play / 自检收尾）。</summary>
        public static void Restore()
        {
            foreach (TextSettings s in Patched)
            {
                if (s != null && s.fallbackFontAssets != null)
                {
                    s.fallbackFontAssets.RemoveAll(f => Created.Contains(f));
                }
            }
            Patched.Clear();
        }
    }
}
