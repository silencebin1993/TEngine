using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// EMG2-PHS-01（FG19 FGR-EMG-101，B05 / B07 / B15）：相态的表现与读数。
    /// - 内核提示 <see cref="CombatEventKind.PhaseCue"/> → 声音 + 字幕（FeedbackCues，进入 / 被打断各一条，名称走端口名文本键）；画面 = 头顶相态图标（内核图标作业按 fg.TbPhaseRule 的形状 / 颜色画，
    ///   相态满 3 点、累积中 1～2 点进度）；第一次有单位进入相态发引导钩子 <see cref="GuidanceHooks.EmgFirstPhase"/>（引导内容在 FG15-UX-04）。
    /// - 悬停读数：相态剩余秒、累积量百分比；某列有效抗性满（不能进入该相态）时写原因（“寒抗性 100%：不会冻结”，卡片负向“首领按抗性”）。
    /// 只处理取走的提示事件（O(本步事件)），不遍历单位。计数只给自检读。
    /// </summary>
    public static class EmgPhaseFeedback
    {
        public const byte CodeArc = 64;

        public static int EnterCues { get; private set; }
        public static int ExitCues { get; private set; }
        public static int BrokenCues { get; private set; }
        public static int ArcCues { get; private set; }
        public static int LastPhase { get; private set; } = -1;

        public static void ResetForTests()
        {
            EnterCues = ExitCues = BrokenCues = ArcCues = 0;
            LastPhase = -1;
        }

        public static string PhaseName(int k) => k >= 0 && k < EmgPhaseCatalog.PhasePorts.Length ? GameText.Get("emg.port." + EmgPhaseCatalog.PhasePorts[k] + ".name") : string.Empty;

        public static string AccName(int k) => k >= 0 && k < EmgPhaseCatalog.AccPorts.Length ? GameText.Get("emg.port." + EmgPhaseCatalog.AccPorts[k] + ".name") : string.Empty;

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            if (e.Code == CodeArc)
            {
                ArcCues++;
                return; // 电弧连线只是画面（内核电荷累积已结算），不另出字幕
            }
            int k = e.Code & 15;
            bool exit = (e.Code & EmgPhaseConst.CodeExit) != 0;
            bool broken = (e.Code & EmgPhaseConst.CodeBroken) != 0;
            string who = site != null && site.TryGetEnemyOfUnit(e.Unit, out string enemyType) ? EnemyDefs.NameOf(enemyType) ?? GameText.Get("tag.hover.title") : GameText.Get("emg.phase.unit");
            if (!exit)
            {
                EnterCues++;
                LastPhase = k;
                if (k != EmgPhaseConst.Chill) // 冻结进入由内核同一步的 ClarityCue/7（ClarityFreeze“机器被冻结…”+ 音效）出声音和字幕，这里不再出第二条
                {
                    FeedbackCues.RaiseAt(FeedbackCueId.EmgPhaseEnter, at, GameText.Format("emg.phase.cue.enter", who, PhaseName(k)));
                }
                GuidanceHooks.Raise(GuidanceHooks.EmgFirstPhase);
                return;
            }
            ExitCues++;
            if (broken)
            {
                BrokenCues++;
                FeedbackCues.RaiseAt(FeedbackCueId.EmgPhaseBroken, at, GameText.Format("emg.phase.cue.exit", who, PhaseName(k)));
            }
        }

        /// <summary>fg.TbPhaseRule 的头顶图标（形状序号, 打包颜色）；表里形状 / 颜色认不出的那一行不画并报错（不静默）。</summary>
        public static float2[] BuildVisuals()
        {
            var v = new float2[EmgPhaseConst.Count];
            var rows = ConfigSystem.Instance.Tables?.TbPhaseRule?.DataList;
            for (int k = 0; k < v.Length; k++)
            {
                v[k] = new float2(-1f, 0f);
                GameConfig.fg.PhaseRule r = rows != null && k < rows.Count ? rows[k] : null;
                if (r == null)
                {
                    continue;
                }
                int shape = Content.NamedReactionCatalog.ShapeIndex(r.IconShape);
                if (shape < 0 || !Content.NamedReactionCatalog.TryParseColor(r.IconColor, out uint rgb))
                {
                    TEngine.Log.Error($"[EmgPhaseFeedback] 相态 {r.Id} 的图标形状 {r.IconShape} / 颜色 {r.IconColor} 无法识别，头顶不显示它。");
                    continue;
                }
                v[k] = new float2(shape, rgb);
            }
            return v;
        }

        /// <summary>悬停读数的相态段：在相态里的（剩余秒）、正在累积的（百分比）、抗性满不能进入的（原因）。没有任何一行返回 false。</summary>
        public static bool AppendLines(CombatKernel kernel, int unitId, StringBuilder sb)
        {
            if (kernel == null || !kernel.TryGetUnitEmg(unitId, out _, out EmgUnitState s))
            {
                return false;
            }
            int before = sb.Length;
            var rows = ConfigSystem.Instance.Tables?.TbPhaseRule?.DataList;
            for (int k = 0; k < EmgPhaseConst.Hack; k++)
            {
                EmgPhaseRule r = kernel.PhaseRuleAt(k);
                string glyph = rows != null && k < rows.Count ? rows[k].IconShape : string.Empty;
                float acc = BinGames.Sim.Combat.CombatLogic.AccOf(s, k);
                float t = BinGames.Sim.Combat.CombatLogic.PhsTOf(s, k);
                string line = null;
                if ((s.PhaseMask & (1 << k)) != 0)
                {
                    line = GameText.Format("tag.hover.line", glyph, PhaseName(k), math.max(0f, t).ToString("0.0", CultureInfo.InvariantCulture), "3");
                }
                else if (acc > 0.001f && r.Enter > 0f)
                {
                    line = glyph + " " + GameText.Format("emg.phase.meter", AccName(k), Mathf.RoundToInt(100f * acc / r.Enter).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    float res = kernel.EffectiveResist(unitId, r.Resist);
                    if (res >= 0.999f)
                    {
                        line = GameText.Format("emg.phase.immune", GameText.Get("emg.phase.resist." + EmgPhaseCatalog.ResistCols[r.Resist]),
                            Mathf.RoundToInt(res * 100f).ToString(CultureInfo.InvariantCulture), PhaseName(k));
                    }
                }
                if (line != null)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(line);
                }
            }
            // EMG2-RECL-01（FGR-EMG-115 负向）：回收修复最近一次的结果（没拆的写原因：没有残骸 / 满耐久 / 冷却 / 不可回收）
            string note = ReclaimNoteKey(s.ReclaimNote);
            if (note != null)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(GameText.Get(note));
            }
            return sb.Length > before;
        }

        /// <summary>EMG2-RECL-01：回收修复结果码 → 悬停文本键（0 = 没有记录）。</summary>
        public static string ReclaimNoteKey(byte note)
        {
            switch (note)
            {
                case EmgReclaimConst.NoteRepaired: return "emg.reclaim.note.repaired";
                case EmgReclaimConst.NoteNoWreck: return "emg.reclaim.note.no_wreck";
                case EmgReclaimConst.NoteFull: return "emg.reclaim.note.full";
                case EmgReclaimConst.NoteCooldown: return "emg.reclaim.note.cooldown";
                case EmgReclaimConst.NoteNoSalvage: return "emg.reclaim.note.no_salvage";
                default: return null;
            }
        }
    }
}
