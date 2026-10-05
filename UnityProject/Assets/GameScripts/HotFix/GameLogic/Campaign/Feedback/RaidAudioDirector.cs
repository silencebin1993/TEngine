using System;
using System.Collections.Generic;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Settings;
using TEngine;
using UnityEngine;
using AudioType = TEngine.AudioType;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>
    /// FG6-DEF-06（卡片“必须同时交付：预警和突袭的音乐与音效变化”；FG06 第 4 节“预警和突袭都有明确的音乐和音效变化”；承接 DEBT-FG6DEF04-07 的音效部分）：
    /// 按家园的突袭阶段切换音乐层与提示音。三个阶段：平静（没有音乐层）→ 预警（有已预警、还没到达的突袭：低沉的脉冲循环）→ 突袭（有已到达的突袭：急促的鼓点循环）；
    /// 进入预警 / 突袭时各响一声提示音，突袭结束时响一声“结束”。音乐是占位素材（程序生成的循环，美术批次替换，DEBT-FG6DEF06-01）。
    /// - 阶段只读导演与行进队伍（O(计划数 + 队伍数)），每 0.5 真实秒判一次；只在阶段变化时调音频模块（<see cref="GameModule.Audio"/>，音乐通道，循环、淡出）。
    /// - 编辑器非 Play（自检）不碰音频模块，只记录“本应播放”的音乐 / 提示音，供断言（同 <see cref="FeedbackCues"/> 的做法）。
    /// - 远征中家园遇袭同样切换（整个世界同时运行；家园危险时玩家需要听到）。
    /// </summary>
    public static class RaidAudioDirector
    {
        public const int PhaseCalm = 0;
        public const int PhaseWarning = 1;
        public const int PhaseRaid = 2;

        public const string MusicWarning = "mus_raid_warning";
        public const string MusicRaid = "mus_raid_battle";
        public const string StingWarning = "sfx_alarm_warning";
        public const string StingRaid = "sfx_boss_phase";
        public const string StingEnd = "sfx_objective_complete";

        public static int Phase { get; private set; }
        public static int PhaseChanges { get; private set; }
        public static string CurrentMusic { get; private set; } = string.Empty;
        public static string LastSting { get; private set; } = string.Empty;
        public static int StingCount { get; private set; }
        /// <summary>本会话阶段变化的记录（自检读：平静 → 预警 → 突袭 → 平静）。</summary>
        public static IReadOnlyList<int> History => HistoryList;

        private static readonly List<int> HistoryList = new List<int>(16);
        private static float _timer;

        public static float MusicVolume => Mathf.Clamp01(GridContent.TryGetTuning("raid.music.volume", out float v) ? v : 0.6f);

        public static void ResetSession()
        {
            if (Phase != PhaseCalm)
            {
                StopMusic();
            }
            Phase = PhaseCalm;
            CurrentMusic = string.Empty;
            HistoryList.Clear();
            _timer = 0f;
        }

        /// <summary>这一刻家园的突袭阶段（只读）。</summary>
        public static int Evaluate(CampaignState s)
        {
            if (s == null)
            {
                return PhaseCalm;
            }
            if (Economy.StandingRuleService.DefaultRaidActive(s))
            {
                return PhaseRaid;
            }
            foreach (RaidWaveView w in RaidDirectorService.IncomingWaves(s, GameClock.Ticks))
            {
                if (!w.PlannedOnly && !w.Arrived && w.Lead != null && w.Lead.TargetKind != RaidDirectorService.TargetOutpost)
                {
                    return PhaseWarning;
                }
            }
            return PhaseCalm;
        }

        /// <summary>每帧（GameRoot.OnUpdate；真实时间）。<paramref name="force"/> = 立即判定（自检）。</summary>
        public static void Tick(CampaignState s, bool inWorld, float realDt, bool force = false)
        {
            _timer -= realDt;
            if (!force && _timer > 0f)
            {
                return;
            }
            _timer = 0.5f;
            int next = inWorld ? Evaluate(s) : PhaseCalm;
            if (next != Phase)
            {
                Apply(Phase, next); // 音乐 / 音效的音量设置由音频模块的通道音量统一作用（FeedbackCues.ApplyAudioSettings），这里不重放
            }
        }

        private static void Apply(int from, int to)
        {
            Phase = to;
            PhaseChanges++;
            HistoryList.Add(to);
            if (HistoryList.Count > 32)
            {
                HistoryList.RemoveAt(0);
            }
            switch (to)
            {
                case PhaseWarning:
                    if (from == PhaseCalm)
                    {
                        Sting(StingWarning);
                    }
                    PlayMusic(MusicWarning);
                    break;
                case PhaseRaid:
                    Sting(StingRaid);
                    PlayMusic(MusicRaid);
                    break;
                default:
                    if (from == PhaseRaid)
                    {
                        Sting(StingEnd);
                    }
                    StopMusic();
                    break;
            }
        }

        private static void Sting(string id)
        {
            LastSting = id;
            StingCount++;
            if (!Application.isPlaying)
            {
                return;
            }
            GameModule.Audio?.Play(AudioType.Sound, id, false, 1f, true, true); // 音效通道音量由设置统一作用在音频模块上（与 FeedbackCues 同一做法）
        }

        private static void PlayMusic(string id)
        {
            CurrentMusic = id ?? string.Empty;
            if (!Application.isPlaying || string.IsNullOrEmpty(id))
            {
                return;
            }
            IAudioModule audio = GameModule.Audio;
            if (audio == null)
            {
                return;
            }
            audio.Stop(AudioType.Music, true);
            audio.Play(AudioType.Music, id, true, MusicVolume, true, false);
        }

        private static void StopMusic()
        {
            CurrentMusic = string.Empty;
            if (!Application.isPlaying)
            {
                return;
            }
            GameModule.Audio?.Stop(AudioType.Music, true);
        }
    }
}
