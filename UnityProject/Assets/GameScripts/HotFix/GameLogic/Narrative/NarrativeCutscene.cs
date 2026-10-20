using System;
using GameLogic.Localization;

namespace GameLogic.Narrative
{
    /// <summary>
    /// FG9-NAR-01（FG11 FGR-NAR-002、第 8 节“文本显示速度”）：一段演出的播放模型（纯逻辑，界面只负责画出来）。
    /// 逐行字幕按“每真实秒几个字”逐字出现（速度拉到最大 = 整行立即出现），整行出完后停留该行的 holdSeconds 再自动下一行；
    /// 点击 = 先把这一行补全，已经补全再点 = 下一行。时间用真实秒（由播放器传入不受倍速影响的帧间隔），与 0.5x～3x 倍速、世界暂停无关。
    /// 没有字幕行的演出把正文当成一行播放。
    /// </summary>
    public sealed class NarrativeCutscene
    {
        public readonly NarrativeEntryDef Def;
        /// <summary>资料库里的重看（跳过不需要确认、不改存档）。</summary>
        public readonly bool Replay;

        public int LineIndex { get; private set; }
        public float LineElapsed { get; private set; }
        public bool Finished { get; private set; }

        public NarrativeCutscene(NarrativeEntryDef def, bool replay)
        {
            Def = def;
            Replay = replay;
        }

        public int LineCount => Def == null ? 0 : Math.Max(1, Def.Lines.Count);

        /// <summary>当前行的完整文字（按当前语言）。</summary>
        public string LineText
        {
            get
            {
                if (Def == null || Finished)
                {
                    return string.Empty;
                }
                return Def.Lines.Count == 0 ? GameText.Get(Def.BodyKey) : GameText.Get(Def.Lines[Math.Min(LineIndex, Def.Lines.Count - 1)].TextKey);
            }
        }

        private float Hold => Def == null || Def.Lines.Count == 0 ? Core.UiTuningValues.Get("narrative.fallback_hold_seconds") : Def.Lines[Math.Min(LineIndex, Def.Lines.Count - 1)].HoldSeconds;

        /// <summary>按文本速度，整行打完需要的真实秒（立即显示 = 0）。</summary>
        public float TypeSeconds(float charsPerSecond, bool instant) =>
            instant || charsPerSecond <= 0f ? 0f : LineText.Length / charsPerSecond;

        /// <summary>当前已经显示出来的字数。</summary>
        public int VisibleChars(float charsPerSecond, bool instant)
        {
            int len = LineText.Length;
            if (instant || charsPerSecond <= 0f)
            {
                return len;
            }
            return Math.Min(len, (int)Math.Floor(LineElapsed * charsPerSecond + 1e-4f));
        }

        /// <summary>当前显示的文字（逐字出现的那一部分）。</summary>
        public string VisibleText(float charsPerSecond, bool instant)
        {
            string text = LineText;
            int n = VisibleChars(charsPerSecond, instant);
            if (n >= text.Length)
            {
                return text;
            }
            if (n > 0 && char.IsHighSurrogate(text[n - 1]))
            {
                n--;
            }
            return text.Substring(0, n);
        }

        public bool LineComplete(float charsPerSecond, bool instant) => VisibleChars(charsPerSecond, instant) >= LineText.Length;

        /// <summary>推进真实时间：整行出完且停留够了自动下一行；最后一行结束 = 演出结束。</summary>
        public void Tick(float realDeltaSeconds, float charsPerSecond, bool instant)
        {
            if (Finished || realDeltaSeconds <= 0f)
            {
                return;
            }
            LineElapsed += realDeltaSeconds;
            if (LineElapsed >= TypeSeconds(charsPerSecond, instant) + Hold)
            {
                NextLine();
            }
        }

        /// <summary>点击：没打完先补全这一行，打完了进下一行。</summary>
        public void Advance(float charsPerSecond, bool instant)
        {
            if (Finished)
            {
                return;
            }
            if (!LineComplete(charsPerSecond, instant))
            {
                LineElapsed = TypeSeconds(charsPerSecond, instant);
                return;
            }
            NextLine();
        }

        private void NextLine()
        {
            LineIndex++;
            LineElapsed = 0f;
            if (LineIndex >= LineCount)
            {
                Finished = true;
            }
        }
    }
}
