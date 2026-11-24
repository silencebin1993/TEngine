// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System;
using System.Collections.Generic;

namespace BinGames.Presentation
{
    /// <summary>当前分支用哪套默认值（D24）：PL 分支 = AI 评审通过即开；合进主线时把这里改成 false，全部回到“合主线默认”（一律关）。</summary>
    public static class PresentationBranch
    {
        public const bool IsPL = true;
    }

    /// <summary>一条功能登记：ID、开关名、两套默认值、档位键（档位文件归 PL-INF-02，暂为 "-"）。</summary>
    public sealed class PresentationFeature
    {
        public readonly string Id;
        public readonly string Switch;
        public readonly bool DefaultPL;
        public readonly bool DefaultMain;
        public readonly string TierKey;

        public PresentationFeature(string id, string switchName, bool defaultPL, bool defaultMain, string tierKey)
        {
            Id = id;
            Switch = switchName;
            DefaultPL = defaultPL;
            DefaultMain = defaultMain;
            TierKey = tierKey;
        }

        public bool Default => PresentationBranch.IsPL ? DefaultPL : DefaultMain;
    }

    /// <summary>
    /// PL 功能登记表（唯一真相；FEATURES.md 与它逐行一致，PresentationValidate.FeaturesDoc 断言）。
    /// 新功能在 <see cref="Table"/> 里加一行；运行时开关只经 <see cref="Set"/> / <see cref="SetAll"/>，宿主每帧按它启停模块。
    /// </summary>
    public static class PresentationFeatureRegistry
    {
        /// <summary>宿主总开关：关 = 全部 PL 模块停用、画面回到现状。</summary>
        public const string HostId = "pl.host";
        /// <summary>宿主自检探针（每帧建 / 移 64 个方块）；只在自检里登记进宿主，默认两套都关。</summary>
        public const string ProbeId = "pl.probe";
        /// <summary>PL-QA-01 采集标定靶（编辑器里的 PresentationCaptureTarget）：只在采集 / 自检里登记进宿主，默认两套都关。</summary>
        public const string CaptureId = "pl.capture";
        /// <summary>PL-RP-01 渲染管线试验垫片（编辑器里的 RpTrialShim：URP 试验影子里把 Standard / 地形 surface shader 换成 URP 能画的）：只在试验采集 / 自检里登记，默认两套都关。</summary>
        public const string RpTrialId = "pl.rptrial";
        /// <summary>PL-VEH-02 机器表现 Rig（VehicleRigModule：掉头先转车身、起步抬头、急停前倾、转弯侧倾、悬浮浮动）：正式游戏登记，PL 分支开、合主线关。</summary>
        public const string VehRigId = "pl.vehrig";

        private static readonly PresentationFeature[] Table =
        {
            new PresentationFeature(HostId, "PresentationHost.Enabled", true, false, "-"),
            new PresentationFeature(ProbeId, "PresentationProbeModule", false, false, "-"),
            new PresentationFeature(CaptureId, "PresentationCaptureTarget", false, false, "-"),
            new PresentationFeature(RpTrialId, "RpTrialShim", false, false, "-"),
            new PresentationFeature(VehRigId, "VehicleRigModule", true, false, "VehicleRig"),
        };

        private static readonly Dictionary<string, bool> State = new Dictionary<string, bool>(StringComparer.Ordinal);

        static PresentationFeatureRegistry()
        {
            ResetToDefaults();
        }

        public static IReadOnlyList<PresentationFeature> Features => Table;

        /// <summary>每次开关变化 +1（宿主据此判断要不要对账，零分配）。</summary>
        public static int Revision { get; private set; }

        public static bool IsRegistered(string id) => id != null && State.ContainsKey(id);

        /// <summary>未登记的 ID 一律当关。</summary>
        public static bool IsOn(string id) => id != null && State.TryGetValue(id, out bool on) && on;

        public static void Set(string id, bool on)
        {
            if (!IsRegistered(id))
            {
                throw new ArgumentException($"PL 功能 {id} 没在 PresentationFeatureRegistry 登记");
            }
            if (State[id] != on)
            {
                State[id] = on;
                Revision++;
            }
        }

        public static void SetAll(bool on)
        {
            foreach (PresentationFeature f in Table)
            {
                Set(f.Id, on);
            }
        }

        public static void ResetToDefaults()
        {
            foreach (PresentationFeature f in Table)
            {
                State[f.Id] = f.Default;
            }
            Revision++;
        }
    }
}
