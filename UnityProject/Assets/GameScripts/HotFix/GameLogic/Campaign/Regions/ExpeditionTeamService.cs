using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Grid;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>FG8-EXP-01（FGR-EXP-001 / FGU-31）：远征队的名字、颜色与上次编成的名单。
    ///
    /// ── 唯一写入口 ──
    /// 数据在 <see cref="GridState.ExpeditionTeam"/>（只加字段、不升域版本；旧档没有 = 空队：默认名、颜色 0、空名单）。
    /// 本类是它唯一的读写入口，界面与派遣事务都不直接改字段。
    ///
    /// ── 复用，不重复实现 ──
    /// 名字的校验（非空、字数上限、禁 &lt; &gt;）与机器改名走同一份 <see cref="MachineNaming.Validate"/> /
    /// <see cref="MachineNaming.ErrorText"/>（同源，不另写一套规则）。
    ///
    /// ── 颜色 ──
    /// 色板是界面展示常量（8 色）；可选种数读 <c>exp.team.color_count</c>（Luban 调参，缺省 8）。颜色名走文本键 <c>exp.team.color.N</c>。
    /// </summary>
    public static class ExpeditionTeamService
    {
        private static readonly Color32[] Palette =
        {
            new Color32(0xE0, 0x4A, 0x4A, 0xFF), // 红
            new Color32(0xF0, 0x8A, 0x2E, 0xFF), // 橙
            new Color32(0xE8, 0xC5, 0x3A, 0xFF), // 黄
            new Color32(0x5C, 0xC0, 0x5C, 0xFF), // 绿
            new Color32(0x3C, 0xC4, 0xC4, 0xFF), // 青
            new Color32(0x4A, 0x7F, 0xE0, 0xFF), // 蓝
            new Color32(0xA8, 0x5C, 0xE0, 0xFF), // 紫
            new Color32(0x9A, 0x9A, 0x9A, 0xFF), // 灰
        };

        /// <summary>可选颜色种数（1～色板长度）。</summary>
        public static int ColorCount
        {
            get
            {
                float v = GridContent.TryGetTuning("exp.team.color_count", out float f) ? f : Palette.Length;
                return Math.Clamp((int)Math.Round(v), 1, Palette.Length);
            }
        }

        /// <summary>取远征队数据；存档没有时补空队（不改变其它字段）。<paramref name="s"/> 为空返回 null。</summary>
        public static ExpeditionTeamState StateOf(CampaignState s)
        {
            if (s?.Grid == null)
            {
                return null;
            }
            s.Grid.ExpeditionTeam ??= new ExpeditionTeamState();
            s.Grid.ExpeditionTeam.Name ??= string.Empty;
            s.Grid.ExpeditionTeam.Members ??= Array.Empty<int>();
            return s.Grid.ExpeditionTeam;
        }

        /// <summary>界面显示的队名（没改过 = 默认名文本键 <c>exp.team.default_name</c>）。</summary>
        public static string DisplayName(CampaignState s)
        {
            string name = StateOf(s)?.Name;
            return string.IsNullOrEmpty(name) ? Localization.GameText.Get("exp.team.default_name") : name;
        }

        /// <summary>改队名：先过机器改名同一套校验；失败时 <paramref name="message"/> 给出玩家可读的原因，队名不变。</summary>
        public static bool TryRename(CampaignState s, string raw, out string message)
        {
            ExpeditionTeamState team = StateOf(s);
            if (team == null)
            {
                message = Localization.GameText.Get("roster.err.unknown");
                return false;
            }
            MachineNaming.NameError err = MachineNaming.Validate(raw, out string clean);
            if (err != MachineNaming.NameError.None)
            {
                message = MachineNaming.ErrorText(err, clean);
                return false;
            }
            team.Name = clean;
            message = clean;
            return true;
        }

        /// <summary>恢复默认队名（空串）。</summary>
        public static void ResetName(CampaignState s)
        {
            ExpeditionTeamState team = StateOf(s);
            if (team != null)
            {
                team.Name = string.Empty;
            }
        }

        /// <summary>设颜色；下标越界（或超过可选种数）返回 false，颜色不变。</summary>
        public static bool TrySetColor(CampaignState s, int index)
        {
            ExpeditionTeamState team = StateOf(s);
            if (team == null || index < 0 || index >= ColorCount)
            {
                return false;
            }
            team.ColorIndex = index;
            return true;
        }

        public static int ColorIndexOf(CampaignState s) => Math.Clamp(StateOf(s)?.ColorIndex ?? 0, 0, ColorCount - 1);

        public static Color32 ColorOf(int index) => Palette[Math.Clamp(index, 0, Palette.Length - 1)];

        public static string ColorName(int index) =>
            Localization.GameText.Get("exp.team.color." + Math.Clamp(index, 0, Palette.Length - 1));

        /// <summary>记下这次编成 / 派出的名单（去重、保持先后、最多 <see cref="ExpeditionDepartureService.MaxRosterSize"/> 台）。
        /// 派遣事务成功后调用一次（只记名单，不改机器状态）。</summary>
        public static void RememberMembers(CampaignState s, IEnumerable<int> logicIds)
        {
            ExpeditionTeamState team = StateOf(s);
            if (team == null || logicIds == null)
            {
                return;
            }
            team.Members = logicIds.Distinct().Take(ExpeditionDepartureService.MaxRosterSize).ToArray();
        }

        /// <summary>上次记下的名单（副本；只返回仍是存活家园机器的 LogicId，已阵亡 / 已远征的不回填）。</summary>
        public static int[] RememberedMembers(CampaignState s)
        {
            int[] members = StateOf(s)?.Members ?? Array.Empty<int>();
            return members.Where(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r != null && r.IsAlive
                                       && r.RegionId == HomeValleyLayout.RegionId && !r.IsInFactory).ToArray();
        }
    }
}
