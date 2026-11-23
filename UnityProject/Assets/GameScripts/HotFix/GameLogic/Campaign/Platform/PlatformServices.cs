using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Localization;

namespace GameLogic.Campaign.Platform
{
    /// <summary>
    /// FG15-SYS-04（FGR-SYS-080）：发行平台（Steam）的成就、富状态与覆盖层。Steam 版在接入 Steamworks.NET 后实现（<see cref="SteamPlatform"/>，待用户：Steamworks 账号与 AppID）；
    /// 没有提供者 = 离线，游戏照常：成就在本机账本解锁，联网后由 <see cref="AchievementService.SyncPending"/> 补推。
    /// </summary>
    public interface IPlatformProvider
    {
        bool Available { get; }
        /// <summary>解锁一个成就（Steam：SetAchievement + StoreStats）。成功 = 平台已记下。</summary>
        bool Unlock(string apiName);
        /// <summary>平台上已解锁的成就 API 名（别的机器 / 之前解锁的）。</summary>
        string[] ListUnlocked();
        /// <summary>富状态键值（Steam：SetRichPresence）。</summary>
        bool SetRichPresence(string key, string value);
        /// <summary>覆盖层打开 / 关闭（Steam：GameOverlayActivated_t）。</summary>
        event Action<bool> OverlayChanged;
    }

    /// <summary>本地模拟的平台（自检与开发期用）：Online = false 模拟断网，解锁失败、列表为空。</summary>
    public sealed class LocalPlatformProvider : IPlatformProvider
    {
        public bool Online = true;
        public readonly HashSet<string> Achievements = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Presence = new Dictionary<string, string>(StringComparer.Ordinal);
        public int UnlockCalls;
        public int PresenceCalls;

        public bool Available => Online;
        public event Action<bool> OverlayChanged;

        public bool Unlock(string apiName)
        {
            UnlockCalls++;
            if (!Online || string.IsNullOrEmpty(apiName))
            {
                return false;
            }
            Achievements.Add(apiName);
            return true;
        }

        public string[] ListUnlocked()
        {
            if (!Online)
            {
                return Array.Empty<string>();
            }
            var a = new string[Achievements.Count];
            Achievements.CopyTo(a);
            return a;
        }

        public bool SetRichPresence(string key, string value)
        {
            PresenceCalls++;
            if (!Online)
            {
                return false;
            }
            Presence[key] = value ?? string.Empty;
            return true;
        }

        public void RaiseOverlay(bool active) => OverlayChanged?.Invoke(active);
    }

    /// <summary>
    /// Steam 接入点。shortcut: 工程里还没有 Steamworks.NET（要用户本人的 Steamworks 账号、AppID 与上架配置），TryCreate 一律返回 null（= 离线）；
    /// 接入时在这里实现 IPlatformProvider（SteamUserStats / SteamFriends.SetRichPresence / GameOverlayActivated_t）与 ICloudSaveProvider（SteamRemoteStorage），
    /// 成就 API 名照 fg.TbAchievement.apiName 在 Steam 后台建（DEBT-FG15SYS04-02，待用户）。
    /// </summary>
    public static class SteamPlatform
    {
        public static IPlatformProvider TryCreate() => null;
        public static ICloudSaveProvider TryCreateCloud() => null;
    }

    /// <summary>平台服务的总入口：启动时注入提供者、每帧 O(1) 推进（富状态只在幕 / 区域 / 模式 / 语言变化时推一次；离线留下的成就每 10 秒真实时间试一次补推）。</summary>
    public static class PlatformServices
    {
        public const float SyncRetrySeconds = 10f;

        public static bool OverlayActive { get; private set; }
        public static string LastPresence { get; private set; } = string.Empty;
        public static int PresencePushes { get; private set; }
        private static IPlatformProvider _bound;
        private static int _presenceKey;
        private static float _retry;

        /// <summary>游戏启动时调一次：有 Steam 就用 Steam，没有就离线（成就本机解锁、云存档不可用）。</summary>
        public static void Init()
        {
            AchievementService.Install();
            if (AchievementService.Provider == null)
            {
                Bind(SteamPlatform.TryCreate());
            }
            if (CloudSaveService.Provider == null)
            {
                CloudSaveService.Provider = SteamPlatform.TryCreateCloud();
            }
        }

        /// <summary>换提供者（自检注入本地模拟；null = 离线）。</summary>
        public static void Bind(IPlatformProvider p)
        {
            if (_bound != null)
            {
                _bound.OverlayChanged -= OnOverlay;
            }
            _bound = p;
            AchievementService.Provider = p;
            if (p != null)
            {
                p.OverlayChanged += OnOverlay;
            }
            OverlayActive = false;
            _presenceKey = 0;
            _retry = 0f;
        }

        private static void OnOverlay(bool active) => OverlayActive = active;

        /// <summary>每帧（GameRoot.Update，主菜单里也跑）。</summary>
        public static void Tick(float unscaledDt, CampaignState state, bool inWorld)
        {
            IPlatformProvider p = AchievementService.Provider;
            if (p == null || !p.Available)
            {
                return;
            }
            _retry -= unscaledDt;
            if (_retry <= 0f)
            {
                _retry = SyncRetrySeconds;
                if (AchievementService.PendingSyncCount > 0)
                {
                    AchievementService.SyncPending();
                }
            }
            PushPresence(state, inWorld);
        }

        /// <summary>富状态（FGR-SYS-080“显示当前幕和区域”）：主菜单 / 第 N 幕 · 区域 / 沙盒 · 区域 / 后日谈 · 区域。只在内容变化时推送。</summary>
        public static void PushPresence(CampaignState s, bool inWorld)
        {
            IPlatformProvider p = AchievementService.Provider;
            if (p == null || !p.Available)
            {
                return;
            }
            bool world = inWorld && s != null;
            int act = world ? s.Progress?.Act ?? 1 : 0;
            string region = world ? s.CurrentRegionId ?? string.Empty : string.Empty;
            int mode = !world ? 0 : SandboxService.IsSandbox(s) ? 2 : s.Progress != null && s.Progress.IsPostgame ? 3 : 1;
            int key = HashCode.Combine(act, region, mode, (int)GameText.Language) | 1;
            if (key == _presenceKey)
            {
                return;
            }
            _presenceKey = key;
            LastPresence = PresenceText(s, world);
            p.SetRichPresence("status", LastPresence);
            p.SetRichPresence("act", world ? act.ToString(CultureInfo.InvariantCulture) : string.Empty);
            p.SetRichPresence("region", world ? CampaignSlotText.RegionName(region) : string.Empty);
            PresencePushes++;
        }

        public static string PresenceText(CampaignState s, bool inWorld)
        {
            if (!inWorld || s == null)
            {
                return GameText.Get("presence.menu");
            }
            string place = CampaignSlotText.RegionName(s.CurrentRegionId);
            if (SandboxService.IsSandbox(s))
            {
                return GameText.Format("presence.sandbox", place);
            }
            if (s.Progress != null && s.Progress.IsPostgame)
            {
                return GameText.Format("presence.postgame", place);
            }
            return GameText.Format("presence.campaign", (s.Progress?.Act ?? 1).ToString(CultureInfo.InvariantCulture), place);
        }
    }
}
