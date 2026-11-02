using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG2-PHS-01（FG19 FGR-EMG-100～103；ADR-EMG-001 ⑥）：把 fg.TbPhaseRule 编成内核 <see cref="EmgPhaseRule"/>（7 行，下标 = 累积量下标）。只在地点载入 / 表重载时编译。
    /// 内核只拿阈值与参数，按下标读，不拿规则 ID（FGR-EMG-013）。行序 / 抗性列 / 退出方式不对时整表拒绝（内核保留缺省表并 Log.Error，不静默）。
    /// </summary>
    public static class EmgPhaseCatalog
    {
        public sealed class Compiled
        {
            public readonly List<EmgPhaseRule> Rules = new List<EmgPhaseRule>();
            public readonly List<string> Ids = new List<string>();
            public readonly List<string> Problems = new List<string>();
            public bool Ok => Problems.Count == 0;
        }

        /// <summary>累积量端口（行序）与相态端口；头顶图标 / 悬停文本用端口名文本键 emg.port.&lt;端口&gt;.name。</summary>
        public static readonly string[] AccPorts = { "phs.chill", "phs.overheat", "phs.charge", "phs.corrode", "phs.magnetize", "phs.ignite", "phs.hack" };
        public static readonly string[] PhasePorts = { "phs.frozen", "phs.meltdown", "phs.stun", "phs.breach", "phs.magnetized", "phs.burning", "phs.hijacked" };
        public static readonly string[] ResistCols = { "cold", "heat", "elec", "corrode", "mag", "hack" };
        private static readonly string[] ExitModes = { "timed", "threshold", "death" };

        private static Compiled _current;

        public static Compiled Current => _current ??= Compile(Load());

        public static void Reload() => _current = null;

        private static IReadOnlyList<GameConfig.fg.PhaseRule> Load()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbPhaseRule?.DataList;
            }
            catch (Exception e)
            {
                Log.Error("[EmgPhaseCatalog] 读取 fg.TbPhaseRule 失败：" + e.Message);
                return null;
            }
        }

        public static Compiled Compile(IReadOnlyList<GameConfig.fg.PhaseRule> rows)
        {
            var c = new Compiled();
            if (rows == null || rows.Count != EmgPhaseConst.Count)
            {
                c.Problems.Add($"fg.TbPhaseRule 应为 {EmgPhaseConst.Count} 行，实际 {rows?.Count ?? 0}");
                return c;
            }
            for (int k = 0; k < rows.Count; k++)
            {
                GameConfig.fg.PhaseRule r = rows[k];
                int res = Array.IndexOf(ResistCols, r?.Resist);
                int mode = Array.IndexOf(ExitModes, r?.ExitMode);
                if (r == null || r.Acc != AccPorts[k] || r.Phase != PhasePorts[k] || res < 0 || mode < 0)
                {
                    c.Problems.Add($"fg.TbPhaseRule 第 {k + 1} 行应为 {AccPorts[k]} → {PhasePorts[k]}，抗性列 / 退出方式要在封闭清单（{r?.Id}）");
                    continue;
                }
                c.Ids.Add(r.Id);
                c.Rules.Add(new EmgPhaseRule
                {
                    Enter = r.Enter, Exit = r.Exit, Decay = r.Decay, Cap = r.Cap, MinDwell = r.MinDwell, Guard = r.Guard, TagGain = r.TagGain, ContactGain = r.ContactGain,
                    P1 = r.P1, P2 = r.P2, P3 = r.P3, P4 = r.P4, Resist = (byte)res, ExitMode = (byte)mode,
                });
            }
            return c;
        }
    }
}
