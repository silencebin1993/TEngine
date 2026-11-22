using System;
using System.Collections.Generic;
using GameLogic.Localization;
using GameLogic.Settings;

namespace GameLogic.Core
{
    /// <summary>
    /// FG15-UX-04（FG13 FGR-UX-040～041，FGT-UX-005）：引导展示队列。订阅 <see cref="GuidanceHooks.FirstRaised"/>，
    /// 把“第一次接触某系统”的引导排队，一次只展示一条（同时触发的多条依次显示，不叠加）。
    /// - “只出现一次”与总开关沿用 <see cref="GuidanceHooks.Raise"/>：已看过不再广播；开关关着时不广播也不记已看，重新打开后第一次接触照样触发。
    /// - 内容按约定取文本键：guide.&lt;钩子&gt;.title / .body（可选 .step2 / .step3 = 演习式的后续几步）；只写了 guide.&lt;钩子&gt;.none 的钩子不出卡片（理由见 fgdata_guidance.NONE）。
    /// - 重看（<see cref="Replay"/>）直接按钩子展示内容，不经过 Raise，不改已看记录；关闭引导时只清掉排队中的首次引导，玩家主动重看的那条照常显示。
    /// - 与游戏时间无关：暂停、0.5x～3x 都不影响排队与展示；不自动消失，玩家点“知道了 / 跳过 / 关闭引导”才前进（非阻塞：卡片不拦截卡片以外的点击、不占输入）。
    /// - 队列只在内存里：已看记录本身在本机设置（跨战役）；退出游戏时还没轮到的条目已记为看过，可在“引导与帮助”里重看。
    /// 每次触发 O(排队数)（排队数 ≤ 钩子总数），与单位数无关。
    /// </summary>
    public static class GuidanceQueue
    {
        public const int MaxSteps = 3;

        private static readonly List<string> PendingList = new List<string>(8);
        private static bool _subscribed;

        public static string Current { get; private set; }
        /// <summary>当前这条是玩家从“引导与帮助”主动重看的。</summary>
        public static bool CurrentIsReplay { get; private set; }
        /// <summary>当前展示到第几步（1 起）。</summary>
        public static int CurrentStep { get; private set; }
        public static IReadOnlyList<string> Pending => PendingList;
        public static int Revision { get; private set; }

        // ── 自检读点 ──
        public static int ShownCount { get; private set; }
        public static int NoContentCount { get; private set; }
        public static int DedupedCount { get; private set; }
        public static int ClearedByDisableCount { get; private set; }

        public static string TitleKey(string hook) => "guide." + hook + ".title";
        public static string BodyKey(string hook) => "guide." + hook + ".body";
        public static string StepKey(string hook, int step) => step <= 1 ? BodyKey(hook) : "guide." + hook + ".step" + step;
        public static string NoneKey(string hook) => "guide." + hook + ".none";

        public static bool HasContent(string hook) => !string.IsNullOrEmpty(hook) && GameText.Has(TitleKey(hook)) && GameText.Has(BodyKey(hook));

        public static bool IsExempt(string hook) => !string.IsNullOrEmpty(hook) && !HasContent(hook) && GameText.Has(NoneKey(hook));

        public static int StepCount(string hook)
        {
            int n = 1;
            while (n < MaxSteps && GameText.Has(StepKey(hook, n + 1)))
            {
                n++;
            }
            return n;
        }

        /// <summary>当前这条的文字（当前语言）。</summary>
        public static string CurrentTitle => Current != null ? GameText.Get(TitleKey(Current)) : string.Empty;
        public static string CurrentBody => Current != null ? GameText.Get(StepKey(Current, CurrentStep)) : string.Empty;
        public static int CurrentStepCount => Current != null ? StepCount(Current) : 0;

        /// <summary>当前这条在图鉴里的系统说明（以该钩子解锁的第一条系统条目；没有则为 null）。</summary>
        public static string CurrentCodexId => CodexIdFor(Current);

        public static string CodexIdFor(string hook)
        {
            if (string.IsNullOrEmpty(hook))
            {
                return null;
            }
            foreach (Progression.MechanicCodexEntry e in Progression.MechanicCodex.Entries)
            {
                if (e.Kind == Progression.MechanicCodexKind.System && Array.IndexOf(e.Hooks, hook) >= 0)
                {
                    return e.Id;
                }
            }
            return null;
        }

        /// <summary>挂上 <see cref="GuidanceHooks.FirstRaised"/>（UI 基础件挂载时调用，进程内一次）。</summary>
        public static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;
            GuidanceHooks.FirstRaised += OnFirstRaised;
        }

        private static void OnFirstRaised(string hook)
        {
            if (!HasContent(hook))
            {
                NoContentCount++; // none 理由的钩子（另一个同时触发的钩子承担引导）或缺内容（FgGuidanceSelfCheck A 段拦）
                return;
            }
            Enqueue(hook);
        }

        private static void Enqueue(string hook)
        {
            if (hook == Current || PendingList.Contains(hook))
            {
                DedupedCount++;
                return;
            }
            PendingList.Add(hook);
            if (Current == null)
            {
                ShowNext();
            }
            Revision++;
        }

        private static void ShowNext()
        {
            Current = null;
            CurrentIsReplay = false;
            CurrentStep = 0;
            if (PendingList.Count == 0)
            {
                return;
            }
            Current = PendingList[0];
            PendingList.RemoveAt(0);
            CurrentStep = 1;
            ShownCount++;
        }

        /// <summary>“下一步 / 知道了”：多步的先翻到下一步，最后一步关掉、轮到下一条。</summary>
        public static void Advance()
        {
            if (Current == null)
            {
                return;
            }
            if (CurrentStep < StepCount(Current))
            {
                CurrentStep++;
            }
            else
            {
                ShowNext();
            }
            Revision++;
        }

        /// <summary>“跳过”：关掉当前这条，并丢掉排队中的其余条目（都已记为看过，可在“引导与帮助”里重看）。</summary>
        public static void SkipAll()
        {
            PendingList.Clear();
            ShowNext();
            Revision++;
        }

        /// <summary>“关闭引导”：关总开关（与设置 › 游戏性“引导”同一个开关），清空队列。</summary>
        public static void DisableAll()
        {
            GameSettings.SetGuidanceEnabled(false);
            SkipAll();
        }

        /// <summary>重看一条看过的引导：插到最前面立即展示，原来正在展示的首次引导退回队首，不丢。不改已看记录。</summary>
        public static bool Replay(string hook)
        {
            if (!HasContent(hook))
            {
                return false;
            }
            PendingList.Remove(hook);
            if (Current != null && Current != hook && !CurrentIsReplay)
            {
                PendingList.Insert(0, Current);
            }
            Current = hook;
            CurrentIsReplay = true;
            CurrentStep = 1;
            ShownCount++;
            Revision++;
            return true;
        }

        /// <summary>每帧（UI 层调用）：总开关被关掉时清掉排队中的首次引导（主动重看的那条保留）。O(1)。</summary>
        public static void Tick()
        {
            if (GameSettings.GuidanceEnabled || (PendingList.Count == 0 && (Current == null || CurrentIsReplay)))
            {
                return;
            }
            ClearedByDisableCount += PendingList.Count + (Current != null && !CurrentIsReplay ? 1 : 0);
            PendingList.Clear();
            if (Current != null && !CurrentIsReplay)
            {
                ShowNext();
            }
            Revision++;
        }

        /// <summary>“引导与帮助”列表：看过、且有卡片的钩子（按看到的先后，最近的在后）。</summary>
        public static void History(List<string> into)
        {
            into.Clear();
            foreach (string h in GameSettings.SeenGuidanceHooks)
            {
                if (HasContent(h))
                {
                    into.Add(h);
                }
            }
        }

        /// <summary>有卡片的钩子总数（Known 里扣掉 none 理由的）。</summary>
        public static int ContentHookCount()
        {
            int n = 0;
            foreach (string h in GuidanceHooks.Known)
            {
                if (HasContent(h))
                {
                    n++;
                }
            }
            return n;
        }

        public static void ResetForTests()
        {
            PendingList.Clear();
            Current = null;
            CurrentIsReplay = false;
            CurrentStep = 0;
            ShownCount = 0;
            NoContentCount = 0;
            DedupedCount = 0;
            ClearedByDisableCount = 0;
            Revision++;
        }
    }
}
