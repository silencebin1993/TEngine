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
    /// - 只有装了作战组件（Demo 已有的 6 个：连射器 / 切割束 / 维修束 / 重炮 / 冲刺器 / 标记器）的机器才有机身状态；搬运机等没有作战组件的不变形。
    /// - 过渡时长 0.3 秒（fg.TbHomeTuning morph.transition_seconds），按真实时间计、不随倍速、战略暂停中不走（与接入过渡同一口径）。
    /// 纯计算，O(本次生效的固件数)；不写任何游戏状态。
    /// </summary>
    public static class MachineMorph
    {
        public const float DefaultTransitionSeconds = 0.3f;
        private const string TransitionTuningId = "morph.transition_seconds";

        /// <summary>三类状态，按挂点组顺序（下标 0/1/2 = 形变态 / 喷口态 / 线圈态）。</summary>
        public static readonly MorphMask[] Categories = { MorphMask.Limiter, MorphMask.Fluid, MorphMask.Electromagnetic };

        /// <summary>有机身状态的作战组件（Demo 已有的全部 6 个；主组件 4 个 + 功能组件 2 个）。新增组件时在这里登记，并在形变部件库补三套状态。</summary>
        public static readonly IReadOnlyList<string> MorphComponents = new[]
        {
            ComponentCatalog.CompGunId,
            ComponentCatalog.CompBeamId,
            ComponentCatalog.CompRepairBeamId,
            ComponentCatalog.CompCannonId,
            ComponentCatalog.FuncDashId,
            ComponentCatalog.FuncMarkerId,
        };

        /// <summary>FG2-FW-02 新建的作战组件（每种载体至少一个）：三套机身状态由 FG2-VFX-02“形变全量”补（DEBT-FG2FW02-01）。
        /// 在那之前它们装上形变类固件时不显示机身状态（引信类本来就不改机身），其余表现照常；形变自检要求“组件目录 = 有机身状态的 + 这里登记的”，
        /// 新增组件忘了登记会被拦下。</summary>
        public static readonly IReadOnlyList<string> PendingMorphComponents = new[]
        {
            ComponentCatalog.CompRamId,
            ComponentCatalog.CompShovelId,
            ComponentCatalog.CompDroneBayId,
            ComponentCatalog.CompCoronaId,
            ComponentCatalog.CompSprayerId,
        };

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
