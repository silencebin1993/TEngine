using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using TEngine;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>FG1-VFX-01：机身形变的三类状态（可叠加，每类一位）。引信与弹芯类不改机身，没有位。</summary>
    [Flags]
    public enum MorphMask : byte
    {
        None = 0,
        /// <summary>形变态：限制器与击发逻辑（散热鳍展开、枪管发红、天线升起）。</summary>
        Limiter = 1,
        /// <summary>喷口态：流体改道（机身开出喷口、管线外露）。</summary>
        Fluid = 2,
        /// <summary>线圈态：电磁场控（线圈发光）。</summary>
        Electromagnetic = 4,
        All = Limiter | Fluid | Electromagnetic,
    }

    /// <summary>
    /// FG1-VFX-01 机身形变的纯逻辑（FG02 FGR-FW-020～022）：
    /// - 形变只读**正式编译结果**（<see cref="BlueprintCircuitPreview.FirmwareIds"/> = 本次编译实际生效的固件，AI 驾驶时是机器自己的常规固件，
    ///   你接入时再加上接入口里插进去的），按固件类别（fg.TbFirmwareKind category）取并集，不另算一套（FGR-FW-021、IC-REQ-010）。
    ///   调用方是战斗内核桥接层 <c>CombatSite.ResolveMachineWeapon</c>——与武器参数同一次解析，形变与战斗结果不会分叉。
    /// - 只有装了作战组件（设计案 5.6 名表全部 16 个，FG2-VFX-02 形变全量）的机器才有机身状态；搬运机等没有作战组件的不变形。
    /// - 过渡时长 0.3 秒（fg.TbHomeTuning morph.transition_seconds），按真实时间计、不随倍速、战略暂停中不走（与接入过渡同一口径）。
    /// 纯计算，O(本次生效的固件数)；不写任何游戏状态。
    /// </summary>
    public static class MachineMorph
    {
        public const float DefaultTransitionSeconds = 0.3f;
        private const string TransitionTuningId = "morph.transition_seconds";

        /// <summary>三类状态，按挂点组顺序（下标 0/1/2 = 形变态 / 喷口态 / 线圈态）。</summary>
        public static readonly MorphMask[] Categories = { MorphMask.Limiter, MorphMask.Fluid, MorphMask.Electromagnetic };

        /// <summary>有机身状态的作战组件：设计案 5.6 名表 16 个全部（主组件 13 个 + 功能组件 3 个；炮塔能装的都在里面，冲刺器装不上炮塔但机器上照样有）。
        /// 新增组件时在这里登记，并在形变部件库（MachineMorphLibrary）补三套状态；形变自检要求“组件目录 = 这里”，漏登记会被拦下。</summary>
        public static readonly IReadOnlyList<string> MorphComponents = new[]
        {
            ComponentCatalog.CompGunId,
            ComponentCatalog.CompBeamId,
            ComponentCatalog.CompRepairBeamId,
            ComponentCatalog.CompCannonId,
            ComponentCatalog.FuncDashId,
            ComponentCatalog.FuncMarkerId,
            // FG2-FW-02 新建（每种载体至少一个）：三套机身状态 FG2-VFX-02 补齐（DEBT-FG2FW02-01 关闭）
            ComponentCatalog.CompRamId,
            ComponentCatalog.CompShovelId,
            ComponentCatalog.CompDroneBayId,
            ComponentCatalog.CompCoronaId,
            ComponentCatalog.CompSprayerId,
            // FG2-VFX-02 新建（设计案 5.6 其余 5 个，FG-GAP-051）
            ComponentCatalog.CompOrbitId,
            ComponentCatalog.CompSentryId,
            ComponentCatalog.CompPulserId,
            ComponentCatalog.CompClawId,
            ComponentCatalog.FuncSpikesId,
        };

        /// <summary>还没有机身状态的作战组件（FG2-VFX-02 起为空：名表 16 个都有三套状态）。保留这个登记口：以后新加组件时先登记在这里、同时登记 DEBT，
        /// 形变自检要求“组件目录 = 有机身状态的 + 这里登记的”。</summary>
        public static readonly IReadOnlyList<string> PendingMorphComponents = Array.Empty<string>();

        /// <summary>
        /// FG2-VFX-02（卡片负向“多类叠加时的遮挡”）：三类同时展开时谁压在谁上面的优先级（大者在上）。
        /// 规则：几何上三类先按方位分开（形变态向两侧、喷口态向后、线圈态一圈环）；真的重叠时按优先级把整组抬高几厘米，俯视下高优先级的盖住低优先级的。
        /// 形变态（限制器，接入的爽点、带信号光柱）最高，线圈态（细环，被盖住就看不见）其次，喷口态（向后拉长的最大一块）最低——它被压掉一角仍然认得出。
        /// </summary>
        public static int OverlayPriority(MorphMask category) => category switch
        {
            MorphMask.Limiter => 2,
            MorphMask.Electromagnetic => 1,
            _ => 0,
        };

        /// <summary>按 <see cref="OverlayPriority"/> 抬高挂点组的高度（米）：每级 3 厘米。</summary>
        public const float OverlayLiftPerLevel = 0.03f;

        private static bool _warnedTuning;

        public static bool IsMorphComponent(string componentId)
        {
            if (string.IsNullOrEmpty(componentId))
            {
                return false;
            }
            for (int i = 0; i < MorphComponents.Count; i++)
            {
                if (MorphComponents[i] == componentId)
                {
                    return true;
                }
            }
            return false;
        }

        public static int IndexOf(MorphMask single)
        {
            switch (single)
            {
                case MorphMask.Limiter: return 0;
                case MorphMask.Fluid: return 1;
                case MorphMask.Electromagnetic: return 2;
                default: return -1;
            }
        }

        /// <summary>单个固件对应的形变位（引信类、未知类别、不是固件时为 None）。FG2-FW-01：读固件表的“形变状态”列（FGR-FW-001），
        /// check_luban R24 保证它与类别对应（引信 = none、限制器 = limiter、流体 = fluid、电磁 = em）。</summary>
        public static MorphMask BitOf(string firmwareId)
        {
            switch (FirmwareKinds.MorphOf(firmwareId))
            {
                case "limiter": return MorphMask.Limiter;
                case "fluid": return MorphMask.Fluid;
                case "em": return MorphMask.Electromagnetic;
                default: return MorphMask.None;
            }
        }

        /// <summary>一组生效固件的类别并集。</summary>
        public static MorphMask MaskOf(IReadOnlyList<string> firmwareIds)
        {
            MorphMask mask = MorphMask.None;
            for (int i = 0; firmwareIds != null && i < firmwareIds.Count; i++)
            {
                mask |= BitOf(firmwareIds[i]);
            }
            return mask;
        }

        /// <summary>编译结果 → 机身状态。没有作战组件（主组件与功能组件都不是有机身状态的组件）时为 None。</summary>
        public static MorphMask MaskOf(BlueprintCircuitPreview preview)
        {
            if (preview == null || (!IsMorphComponent(preview.PrimaryId) && !IsMorphComponent(preview.UtilityId)))
            {
                return MorphMask.None;
            }
            return MaskOf(preview.FirmwareIds);
        }

        /// <summary>形变过渡秒数（FGR-FW-021 初值 0.3；表缺行时记一次 Error 并用初值）。</summary>
        public static float TransitionSeconds
        {
            get
            {
                if (GridContent.TryGetTuning(TransitionTuningId, out float v) && v >= 0f)
                {
                    return v;
                }
                if (!_warnedTuning)
                {
                    _warnedTuning = true;
                    Log.Error($"[MachineMorph] fg.TbHomeTuning 缺少 {TransitionTuningId}，暂用规格初值 {DefaultTransitionSeconds}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
                }
                return DefaultTransitionSeconds;
            }
        }

        /// <summary>掩码的调试代号（日志 / 自检报告用，不是玩家可见文本；玩家可见的状态名随接入 HUD 走文本键，FG1-HUD-01）。</summary>
        public static string Describe(MorphMask mask)
        {
            if (mask == MorphMask.None)
            {
                return "none";
            }
            var parts = new List<string>(3);
            if ((mask & MorphMask.Limiter) != 0)
            {
                parts.Add("limiter");
            }
            if ((mask & MorphMask.Fluid) != 0)
            {
                parts.Add("fluid");
            }
            if ((mask & MorphMask.Electromagnetic) != 0)
            {
                parts.Add("em");
            }
            return string.Join("+", parts);
        }
    }
}
