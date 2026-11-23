using System;
using System.Collections.Generic;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.Diagnostics
{
    /// <summary>
    /// FG15-SYS-03 同批（FG-GAP-012，IC-REQ-013、FG00 B06 / B11）：安装或热更包里的配置表损坏 / 缺失时，
    /// 冷启动不再带着坏表进主菜单（文本全是 ⟦key⟧、一读数值就抛异常），而是只挂浮层，弹“文件损坏，请校验完整性”：
    /// 写清哪个文件、怎么修（Steam 校验完整性 / 重新安装），“退出游戏”安全退出，“打开日志文件夹”看详情；错误同时写进运行日志（反馈包里有）。
    /// 文本表本身可能就是坏的，所以这里的文字不走 fg.TbLocText，用下面的小双语表（同 Procedure.LaunchText 的做法）。
    /// </summary>
    public static class ConfigIntegrity
    {
        public static readonly Dictionary<string, (string Zh, string En)> Text = new Dictionary<string, (string Zh, string En)>
        {
            ["title"] = ("文件损坏，请校验完整性", "Game files are damaged. Please verify integrity"), // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
            ["body"] = ("游戏的配置数据读取失败，无法正常进入游戏。", "The game's configuration data failed to load, so the game cannot start normally."), // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
            ["fix"] = ("在 Steam 库里右键本游戏 → 属性 → 已安装文件 → 验证游戏文件的完整性；仍不行请重新安装。", // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
                "In your Steam library, right-click the game → Properties → Installed Files → Verify integrity of game files. If that fails, reinstall."),
            ["detail_0"] = ("详情：{0}", "Details: {0}"), // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
            ["quit"] = ("退出游戏", "Quit game"), // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
            ["logs"] = ("打开日志文件夹", "Open log folder"), // loc-ignore: 配置损坏时文本表本身可能读不出，用内置双语表（FG15-SYS-03）
        };

        /// <summary>“退出游戏”的实际动作（自检替换成记录）。</summary>
        public static Action QuitHandler = Application.Quit;

        /// <summary>最近一次探测到的错误（正常 = null）。</summary>
        public static string LastError { get; private set; }

        /// <summary>启动探测：整套配置表能不能读出来（正式 = <see cref="ConfigSystem.Instance"/>；自检传带坏字节来源的实例）。返回错误文字（正常 = null）。</summary>
        public static string Probe(ConfigSystem config = null)
        {
            config ??= ConfigSystem.Instance;
            try
            {
                LastError = config.Tables == null ? "ConfigSystem.Tables == null" : null;
            }
            catch (Exception e)
            {
                LastError = Describe(e, config.LastRequestedFile);
            }
            return LastError;
        }

        /// <summary>只挂浮层并弹损坏提示（不进主菜单）。确认 / 回车 = 退出游戏；取消 / 点遮罩 / Esc = 打开日志文件夹并留在提示上（没有别的出路时不让玩家对着空屏幕）。
        /// 返回挂上的浮层宿主（已经挂过 = null）。新挂的宿主带 <see cref="CorruptScreenGuard"/>：这条路径没有 UiKitInputPump、按键表也可能是坏的，
        /// 所以 Esc / 回车直接读物理键；浮层 <see cref="CorruptScreenGuard.FallbackAfterSeconds"/> 秒还没加载出来（界面资源也坏了）就改用 IMGUI 画同一个提示。</summary>
        public static GameObject ShowCorruptScreen(string error)
        {
            Debug.LogError("[ConfigIntegrity] 配置表损坏或缺失：" + error); // 进运行日志，反馈包里能看到
            GameObject host = UiKitRuntime.MountOverlayOnly();
            host?.AddComponent<CorruptScreenGuard>();
            UiConfirmDialog.Show(BuildRequest(error));
            return host;
        }

        public static ConfirmRequest BuildRequest(string error)
        {
            bool en = IsEnglish();
            var req = new ConfirmRequest
            {
                Title = T("title", en),
                ConfirmText = T("quit", en),
                CancelText = T("logs", en),
                Irreversible = false,
            };
            req.Lines.Add(T("body", en));
            req.Lines.Add(T("fix", en));
            req.Lines.Add(string.Format(T("detail_0", en), CrashReporter.Scrub(error)));
            req.OnConfirm = () => QuitHandler?.Invoke();
            req.OnCancel = () =>
            {
                FeedbackPack.OpenFolderHandler?.Invoke(CrashReporter.LogDir);
                UiConfirmDialog.Show(BuildRequest(error));
            };
            return req;
        }

        private static string T(string key, bool en) => en ? Text[key].En : Text[key].Zh;

        /// <summary>语言直接读设置 JSON（GameSettings 读按键要查表，坏表时不能用）；解析不了回落简体中文。</summary>
        public static bool IsEnglish()
        {
            try
            {
                string json = PlayerPrefs.GetString(Settings.GameSettings.PrefsKey, string.Empty);
                return json.Replace(" ", string.Empty).IndexOf("\"Language\":\"en", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Describe(Exception e, string file)
        {
            Exception inner = e;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }
            return (string.IsNullOrEmpty(file) ? string.Empty : file + ".bytes — ") + inner.GetType().Name + ": " + inner.Message;
        }
    }

    /// <summary>坏表界面的按键与兜底（只挂在 <see cref="UiKitRuntime.MountOverlayOnly"/> 新建的宿主上）。</summary>
    public sealed class CorruptScreenGuard : MonoBehaviour
    {
        public const float FallbackAfterSeconds = 3f;

        private UiKitOverlayUIToolkit _overlay;
        private float _shownAt;

        /// <summary>Esc = 取消（打开日志文件夹、提示留着）；回车 = 确认（退出）。没有提示时什么都不做。</summary>
        public static void HandleKeys(bool esc, bool enter)
        {
            if (!UiConfirmDialog.IsOpen)
            {
                return;
            }
            if (esc)
            {
                UiConfirmDialog.Cancel();
            }
            else if (enter)
            {
                UiConfirmDialog.Confirm();
            }
        }

        /// <summary>浮层迟迟没加载出来（UXML / PanelSettings 也坏了）就改画 IMGUI 提示。</summary>
        public static bool NeedsFallback(bool overlayReady, float shownForSeconds) => !overlayReady && shownForSeconds >= FallbackAfterSeconds;

        private void Awake()
        {
            _overlay = GetComponent<UiKitOverlayUIToolkit>();
            _shownAt = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            // 回车同时可能被 UiConfirmDialog.Tick（按键表完好时）处理：先确认的那次把提示关掉，另一次看到没有提示就不动。
            Core.IInputReader keys = Core.InputRouter.Reader;
            HandleKeys(keys.GetKeyDown(KeyCode.Escape), keys.GetKeyDown(KeyCode.Return) || keys.GetKeyDown(KeyCode.KeypadEnter));
        }

        private void OnGUI()
        {
            ConfirmRequest req = UiConfirmDialog.Current;
            if (req == null || !NeedsFallback(_overlay != null && _overlay.IsReady, Time.realtimeSinceStartup - _shownAt))
            {
                return;
            }
            float w = Mathf.Min(720f, Screen.width - 32f), h = Mathf.Min(360f, Screen.height - 32f);
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h), GUI.skin.box);
            GUILayout.Label(req.Title);
            foreach (string line in req.Lines)
            {
                GUILayout.Label(line);
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(req.ConfirmText))
            {
                UiConfirmDialog.Confirm();
            }
            if (GUILayout.Button(req.CancelText))
            {
                UiConfirmDialog.Cancel();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
