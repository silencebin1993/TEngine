// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using BinGames.Presentation;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.View.Presentation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL 功能开 / 关 sim 哈希夹具：新开局（种子 9201，建家园入口同 FgMachineMorphSelfCheck.NewHome）跑 N 步，
    /// 一次全部 PL 功能关、一次全部开（每步后经 <see cref="PresentationHostBridge.TickManual"/> 驱动宿主），
    /// 比较四个只读哈希：CombatKernel.StateHash、CombatKernel.EmgStateHash、BeltKernel.ComputeStateHash、CampaignFingerprint.Compute。
    /// 后续每张 PL 卡在自己的自检段里一行调用：<c>Expect(PresentationSimHash.CheckOnOff(out d, new MyModule()), "开关 sim 哈希一致：" + d)</c>。
    /// 灵敏度边界：只能抓到“改了 sim 状态”的功能（四个哈希覆盖的范围）；探针本身不读 sim，所以 PL-INF-01 的“开 = 关”只证明夹具能跑通。
    /// 读 sim 的卡必须把自己的模块传进来（别只传探针）。开的那次另断言每个传入模块都真的启用、宿主真的 Tick 了 steps 帧，否则判失败（防模块 Id 漏登记 / 抛异常被停用后空转通过）。
    /// </summary>
    public static class PresentationSimHash
    {
        public const int DefaultSeed = 9201;
        public const int DefaultSteps = 600;
        private const int Slot = 7;

        public struct Hashes
        {
            public ulong Combat;
            public ulong Emg;
            public string Belt;
            public string Campaign;
            public long KernelSteps;
            /// <summary>整份 CampaignState 的 JSON（不参与比较，只在不一致时定位第一处差异；差异前先抹掉真实时间与随机 GUID，见 <see cref="Canonical"/>）。</summary>
            public string StateJson;
            /// <summary>夹具自身的问题（开的那次模块没启用、宿主没 Tick 够帧数等）；非空即判失败。</summary>
            public string Problem;

            public bool SameAs(Hashes o) => Combat == o.Combat && Emg == o.Emg && Belt == o.Belt && Campaign == o.Campaign && KernelSteps == o.KernelSteps;

            public override string ToString() =>
                $"内核第 {KernelSteps} 步 战斗 {Combat:X16} 涌现 {Emg:X16} 传送带 {Belt} 战役指纹 {Campaign}";
        }

        /// <summary>全部 PL 功能关 / 开各跑一次；四个哈希逐个相等返回 true。<paramref name="modules"/> 是本次要登记进宿主的模块（关的那次也登记，只是开关关着）。</summary>
        public static bool CheckOnOff(out string detail, params IPresentationModule[] modules) =>
            CheckOnOff(out detail, DefaultSeed, DefaultSteps, modules);

        public static bool CheckOnOff(out string detail, int seed, int steps, params IPresentationModule[] modules)
        {
            var sw = Stopwatch.StartNew();
            Hashes off = Run(seed, steps, false, modules);
            Hashes on = Run(seed, steps, true, modules);
            bool same = off.SameAs(on) && off.KernelSteps > 0;
            string problem = off.Problem ?? on.Problem;
            detail = $"种子 {seed}、{steps} 步、模块 {modules.Length} 个；关：{off}；开：{on}";
            if (problem != null)
            {
                detail += "；夹具判失败：" + problem;
            }
            if (!same)
            {
                // 再关着跑一次，区分“夹具本身两次不一致”（关 ≠ 关）与“PL 功能改了 sim”（关 = 关 ≠ 开）
                Hashes off2 = Run(seed, steps, false, modules);
                string dir = Path.Combine(Path.GetTempPath(), "bingames-pl-simhash-diff");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "off.json"), Canonical(off.StateJson ?? ""));
                File.WriteAllText(Path.Combine(dir, "on.json"), Canonical(on.StateJson ?? ""));
                File.WriteAllText(Path.Combine(dir, "off2.json"), Canonical(off2.StateJson ?? ""));
                detail += $"；再关一次：{off2}（{(off2.SameAs(off) ? "关 = 关，差异来自 PL 功能" : "关 ≠ 关，夹具 / 主线本身两次不一致")}）；" +
                          $"关 vs 开第一处差异：{FirstDiff(Canonical(off.StateJson), Canonical(on.StateJson))}；关 vs 再关：{FirstDiff(Canonical(off.StateJson), Canonical(off2.StateJson))}；JSON 在 {dir}";
            }
            detail += $"；耗时 {sw.ElapsedMilliseconds} ms";
            return same && problem == null;
        }

        /// <param name="inWorld">可选：哈希算完后、世界还载着时调用（例如在有战役的路径上量 TickManual 的分配）。</param>
        public static Hashes Run(int seed, int steps, bool featuresOn, IList<IPresentationModule> modules, Action inWorld = null)
        {
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            string dir = Path.Combine(Path.GetTempPath(), "bingames-pl-simhash-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.Reload();
                GameClock.ReloadTuning();
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                Directory.CreateDirectory(dir);
                GameRoot.BindWorldProviders();

                ResetWorld();
                CampaignState s = CampaignState.CreateNew("plsimhash-" + seed, "Standard", seed);
                CampaignSession.Set(Slot, s);
                HomeValleyController home = WorldSimulation.LoadHome(resume: false);
                WorldView.Observe(home.SiteId);

                PresentationFeatureRegistry.SetAll(featuresOn);
                foreach (IPresentationModule m in modules)
                {
                    PresentationHost.Register(m);
                }
                long frame0 = PresentationHost.FrameCount;
                for (int i = 0; i < steps; i++)
                {
                    WorldSimulation.StepMany(1);
                    PresentationHostBridge.TickManual(GameClock.StepSeconds, GameClock.StepSeconds);
                }
                long ticked = PresentationHost.FrameCount - frame0;
                var problems = new List<string>();
                foreach (IPresentationModule m in modules)
                {
                    if (PresentationHost.IsActive(m) != featuresOn)
                    {
                        problems.Add($"{(featuresOn ? "开" : "关")}的那次模块 {m.Id}（{m.GetType().Name}）{(PresentationHost.IsActive(m) ? "启用着" : "没启用")}{(PresentationHost.IsFaulted(m) ? "（抛过异常被宿主停用）" : "")}");
                    }
                }
                if (ticked != (featuresOn ? steps : 0))
                {
                    problems.Add($"{(featuresOn ? "开" : "关")}的那次宿主 Tick 了 {ticked} 帧（应 {(featuresOn ? steps : 0)}）");
                }

                WorldSimulation.SyncAllForSave();
                var h = new Hashes
                {
                    Combat = home.Combat.Kernel.StateHash(),
                    Emg = home.Combat.Kernel.EmgStateHash(),
                    Belt = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel.ComputeStateHash().ToString("X16") : "-",
                    KernelSteps = home.Combat.Kernel.Steps,
                };
                h.StateJson = JsonUtility.ToJson(s);
                h.Campaign = CampaignFingerprint.Compute(JsonUtility.FromJson<CampaignState>(Canonical(h.StateJson)));
                h.Problem = problems.Count > 0 ? string.Join("；", problems) : null;
                inWorld?.Invoke();
                return h;
            }
            finally
            {
                foreach (IPresentationModule m in modules)
                {
                    PresentationHost.Unregister(m);
                }
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                try
                {
                    WorldSimulation.UnloadAll();
                }
                finally
                {
                    ResetWorld();
                    GridContent.ResetForTests();
                    WorldGenContent.ResetForTests();
                    FirmwareKinds.ResetForTests();
                    BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                    CampaignSaveService.SaveDirectoryOverrideForTests = null;
                    if (!hadCamera && Camera.main != null)
                    {
                        Object.DestroyImmediate(Camera.main.gameObject);
                    }
                    if (originalSession != null)
                    {
                        CampaignSession.Set(originalSlot, originalSession);
                    }
                    else
                    {
                        CampaignSession.Clear();
                    }
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch
                    {
                        // 临时目录清理失败不影响结论。
                    }
                }
            }
        }

        // 只抹已知的随机 ID 前缀（基元芯片 PartId = "pchip_" + Guid；打印交易号 "*-print:" + Guid），别的十六进制串原样参与比较
        private static readonly Regex Guid32 = new Regex("(pchip_|-print:)[0-9a-f]{32}", RegexOptions.Compiled);
        private static readonly Regex UtcStamp = new Regex(@"""(\w*AtUtc)"":""[^""]*""", RegexOptions.Compiled);

        /// <summary>
        /// 主线本身两次新开局就不一致的两处（11-20 实测，与 PL 无关）：事件账本 CreatedAtUtc 是真实时间、基元芯片 PartId（"pchip_" + Guid.NewGuid）与打印交易号是随机 GUID。
        /// 比战役指纹前把它们抹成常量，再对抹平后的副本调 CampaignFingerprint.Compute。
        /// shortcut: 抹成同一常量后，按 PartId 排序的数组若有多条，顺序可能随随机 GUID 变；出现这种假失败时改成按其余字段排序后再比。
        /// </summary>
        private static string Canonical(string json) => UtcStamp.Replace(Guid32.Replace(json, "${1}guid"), "\"$1\":\"\"");

        private static string FirstDiff(string a, string b)
        {
            a ??= "";
            b ??= "";
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i])
            {
                i++;
            }
            if (i == n && a.Length == b.Length)
            {
                return "无";
            }
            int from = Math.Max(0, i - 80);
            string Cut(string x) => x.Substring(from, Math.Min(x.Length - from, 200));
            return $"第 {i} 字符：…{Cut(a)}… ↔ …{Cut(b)}…";
        }

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            SignalUplinkService.ResetForTests();
            SignalCoreService.ResetForTests();
            SignalPresence.ResetForTests();
        }
    }
}
