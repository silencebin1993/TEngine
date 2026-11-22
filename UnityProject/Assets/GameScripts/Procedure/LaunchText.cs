using System;
using System.Collections.Generic;
using UnityEngine;

namespace Procedure
{
    /// <summary>
    /// FG15-UX-03（FGR-ARC-006）：启动流程的双语文本。这些界面出现在热更程序集与 Luban 文本表（fg.TbLocText）加载之前，
    /// 用不了 GameText，所以单独放一张小表；语言读热更层 GameSettings 存在 PlayerPrefs 里的同一份 JSON（键与字段名必须一致）。
    /// 新增启动期文本只能加在这里，键与两种语言必须都填（FgLocalizationSelfCheck 核对）。
    /// </summary>
    public static class LaunchText
    {
        /// <summary>与 GameLogic.Settings.GameSettings.PrefsKey 一致。</summary>
        public const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        [Serializable]
        private class SettingsLanguageOnly
        {
            public string Language = "zh-CN";
        }

        public static readonly Dictionary<string, (string Zh, string En)> Table = new Dictionary<string, (string Zh, string En)>
        {
            ["launch.cache.clearing"] = ("清理未使用的缓存文件...", "Clearing unused cache files..."),
            ["launch.cache.cleared"] = ("清理完成 即将进入游戏...", "Cleanup done. Entering the game..."),
            ["launch.downloader.creating"] = ("创建补丁下载器...", "Creating patch downloader..."),
            ["launch.download.start"] = ("开始下载更新文件...", "Downloading update files..."),
            ["launch.download.progress_0_1_2"] = ("正在更新，已更新 {0}/{1} ({2:F2}%)", "Updating: {0}/{1} ({2:F2}%)"),
            ["launch.download.size_0_1"] = ("已更新大小 {0}MB/{1}MB", "Downloaded {0}MB/{1}MB"),
            ["launch.download.speed_0_1"] = ("当前网速 {0}/s，剩余时间 {1}", "Speed {0}/s, time left {1}"),
            ["launch.download.done"] = ("下载完成...", "Download complete..."),
            ["launch.init.failed"] = ("资源初始化失败！", "Resource initialization failed!"),
            ["launch.init.failed_retry_0"] = ("资源初始化失败！点击确认重试 \n \n <color=#FF0000>原因{0}</color>",
                "Resource initialization failed! Click OK to retry. \n \n <color=#FF0000>Reason: {0}</color>"),
            ["launch.init.check_manifest"] = ("请检查StreamingAssets/package/DefaultPackage/PackageManifest_DefaultPackage.version是否存在",
                "Check that StreamingAssets/package/DefaultPackage/PackageManifest_DefaultPackage.version exists"),
            ["launch.init.retrying"] = ("重新初始化资源中...", "Re-initializing resources..."),
            ["launch.init.loading"] = ("初始化资源中...", "Initializing resources..."),
            ["launch.manifest.updating"] = ("更新清单文件...", "Updating manifest..."),
            ["launch.remote_version.failed_0"] = ("获取远程版本失败！点击确认重试\n <color=#FF0000>{0}</color>",
                "Failed to get the remote version! Click OK to retry\n <color=#FF0000>{0}</color>"),
            ["launch.init.failed_retry_short_0"] = ("初始化资源失败！点击确认重试 \n <color=#FF0000>{0}</color>",
                "Resource initialization failed! Click OK to retry \n <color=#FF0000>{0}</color>"),
            ["launch.preload.progress_0"] = ("正在载入...{0}%", "Loading... {0}%"),
            ["launch.preload.done"] = ("载入完成", "Loading complete"),
            ["launch.optional_update.notice"] = ("检测到可选资源更新，推荐完成更新提升游戏体验", "An optional resource update is available; updating is recommended"),
            ["launch.net.unreachable"] = ("当前网络不可用，请检查本地网络设置后点击确认进行重试", "No network connection. Check your network settings, then click OK to retry"),
            ["launch.button.confirm"] = ("确定", "OK"),
            ["launch.button.cancel"] = ("取消", "Cancel"),
            ["launch.button.update"] = ("更新", "Update"),
            ["launch.local_version.missing"] = ("没有找到本地版本记录，需要更新资源！", "No local version record found. Resources need an update!"),
            ["launch.optional_update.failed"] = ("更新失败，检测到可选资源更新，推荐完成更新提升游戏体验！ \\n \\n 确定再试一次，取消进入游戏",
                "Update failed. An optional resource update is available and recommended. \\n \\n OK to retry, Cancel to enter the game"),
        };

        private static bool? _english;

        /// <summary>按设置里的语言代码判定；没有设置或解析失败回落简体中文（与 GameLanguageCodes.Parse 同一规则）。</summary>
        public static bool IsEnglish
        {
            get
            {
                if (_english == null)
                {
                    try
                    {
                        string json = PlayerPrefs.GetString(SettingsPrefsKey, string.Empty);
                        string code = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<SettingsLanguageOnly>(json)?.Language;
                        _english = string.Equals(code, "en", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        _english = false;
                    }
                }
                return _english.Value;
            }
        }

        /// <summary>测试用：清掉缓存的语言判定（改了 PlayerPrefs 后重新读）。</summary>
        public static void ResetCache() => _english = null;

        public static string Get(string key, bool english) =>
            Table.TryGetValue(key, out var pair) ? (english ? pair.En : pair.Zh) : "⟦" + key + "⟧";

        public static string Get(string key) => Get(key, IsEnglish);

        public static string Format(string key, params object[] args) => string.Format(Get(key), args);
    }
}
