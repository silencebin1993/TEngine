using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Settings;
using TEngine;
using UnityEngine;
using AudioType = TEngine.AudioType;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>字幕条上的一行（<see cref="FeedbackCues.ActiveCaptions"/>）。</summary>
    public sealed class FeedbackCaption
    {
        public FeedbackCueId Cue;
        public string Tag;
        public string Text;
        public FeedbackTone Tone;

        /// <summary>同一行在显示期间被重复触发的次数（≥1），字幕条显示为“×N”而不是刷屏。</summary>
        public int Count;

        public float ExpiresAtRealtime;
        internal string Key;
    }

    /// <summary>ER8-CONTENT-01 / AC-AUD-001：玩家反馈时刻的**唯一**出口——一次调用同时产出
    /// 声音（<c>GameModule.Audio</c>）与非声音反馈（字幕条，<c>FeedbackCaptionHudUIToolkit</c> 只读
    /// <see cref="ActiveCaptions"/> 渲染）。玩法代码在状态真正变化的那一刻调用 <see cref="Raise"/>，
    /// 不自己播声音、不自己拼提示条。
    ///
    /// 性能纪律（热更层每帧与敌人数无关）：本类只在“事件发生”时被调用，每次 O(1)；
    /// 高频战斗音按 <see cref="FeedbackCueDef.MinIntervalSeconds"/> 节流，每秒发声数是常数级，
    /// 与单位数量无关。<see cref="Tick"/> 每帧 O(字幕条行数≤5)。
    ///
    /// 可测性：编辑器非 Play（自检/测试）下不碰音频模块，只记录“本应发声”的 sfx id 与字幕，
    /// 供断言 <see cref="LastSfxId"/>/<see cref="CountOf"/>/<see cref="ActiveCaptions"/>；
    /// 这与 <c>WhiteboxComposeProjectileFeedback.LastVfxPrefabName</c> 是同一种做法。</summary>
    public static class FeedbackCues
    {
        public const int MaxVisibleCaptions = 5;

        // 战斗音按与镜头落点的水平距离衰减：近处满音量，远处保底，不会完全听不见自己的部队。
        private const float FullVolumeDistance = 25f;
        private const float FloorVolumeDistance = 70f;
        private const float FloorVolume = 0.3f;

        private static readonly float[] LastPlayRealtime = CreateTimes();
        private static readonly int[] RaiseCounts = new int[(int)FeedbackCueId.Max];
        private static readonly List<FeedbackCaption> Captions = new List<FeedbackCaption>(MaxVisibleCaptions);

        private static int _appliedSettingsRevision = -1;
        private static bool _appliedWhilePlaying;
        private static bool _clipsPreloaded;

        /// <summary>最近一次被触发的时刻（不论是否因节流而没有真正发声）。</summary>
        public static FeedbackCueId LastCue { get; private set; }

        /// <summary>最近一次真正请求播放的音效资源名（节流挡掉的不算）。</summary>
        public static string LastSfxId { get; private set; }

        /// <summary>最近一次写进字幕条的完整文字（含“【标签】”）。</summary>
        public static string LastCaptionText { get; private set; }

        /// <summary>本进程累计的发声请求数。</summary>
        public static int SfxRequestCount { get; private set; }

        /// <summary>字幕条内容每变化一次 +1；HUD 只在它变化时重建显示，平时零开销。</summary>
        public static int CaptionRevision { get; private set; }

        public static IReadOnlyList<FeedbackCaption> ActiveCaptions => Captions;

        /// <summary>本进程内某个时刻被触发的累计次数（含被节流的）。</summary>
        public static int CountOf(FeedbackCueId id)
        {
            int index = (int)id;
            return index > 0 && index < RaiseCounts.Length ? RaiseCounts[index] : 0;
        }

        public static void Raise(FeedbackCueId cue)
        {
            RaiseInternal(cue, null, null, 1f);
        }

        /// <param name="detail">补充说明（机器编号、失败原因、内容名……），拼在默认正文后。</param>
        public static void Raise(FeedbackCueId cue, string detail)
        {
            RaiseInternal(cue, detail, null, 1f);
        }

        /// <param name="sfxOverride">内容专属音色（通常是 <c>MechanicalContentDef.SfxId</c>）；空则用默认音。</param>
        public static void Raise(FeedbackCueId cue, string detail, string sfxOverride)
        {
            RaiseInternal(cue, detail, sfxOverride, 1f);
        }

        /// <summary>带世界坐标的战斗时刻：音量按与镜头落点的距离衰减（保底 30%）。</summary>
        public static void RaiseAt(FeedbackCueId cue, Vector3 worldPosition, string detail = null, string sfxOverride = null)
        {
            RaiseInternal(cue, detail, sfxOverride, DistanceAttenuation(worldPosition));
        }

        /// <summary>区域逻辑坐标版本：区域记录里的 <c>Vector2</c> 是地面坐标（x, z）。</summary>
        public static void RaiseAt(FeedbackCueId cue, Vector2 groundPosition, string detail = null, string sfxOverride = null)
        {
            RaiseInternal(cue, detail, sfxOverride, DistanceAttenuation(new Vector3(groundPosition.x, 0f, groundPosition.y)));
        }

        /// <summary>字幕里的机器称呼，与各面板一致的“#编号”；找不到记录返回空串。</summary>
        public static string MachineLabel(int logicId)
        {
            return MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) ? "#" + rec.DisplayNumber : string.Empty;
        }

        /// <summary>这台机器底盘的专属音色（ChassisCatalog.SfxId，命令确认音按底盘区分）；找不到返回 null。</summary>
        public static string MachineChassisSfx(int logicId)
        {
            // MachineRecord.ChassisId 是机型编号（erc_001 等），先归到底盘类别（chassis_wheel 等）再查音色。
            return MachineRegistry.TryGetRecord(logicId, out MachineRecord rec)
                ? ContentSfx(ChassisCatalog.ResolveArchetype(rec.ChassisId))
                : null;
        }

        /// <summary>家园建筑展示名（字幕用），“home_valley:generator”→“发电机”；查不到返回空串。</summary>
        public static string BuildingLabel(string buildingId)
        {
            string label = MechanicalContentFacade.ResolveWorkOrderTargetLabel(buildingId);
            return label == buildingId ? string.Empty : label;
        }

        /// <summary>建筑类型对应的专属音色（BuildingCatalog.SfxId，完工音按建筑区分）；查不到返回 null。</summary>
        public static string BuildingTypeSfx(string buildingTypeId)
        {
            return ContentSfx(BuildingCatalog.ResolveByBuildingTypeId(buildingTypeId));
        }

        /// <summary>关键物展示名（解析产出表里登记的名字）；查不到回退“关键物”。</summary>
        public static string QuestItemName(string contentId)
        {
            return !string.IsNullOrEmpty(contentId)
                   && Regions.HomeValleyAnalysis.YieldTable.TryGetValue(contentId, out Regions.HomeValleyAnalysis.YieldInfo info)
                   && !string.IsNullOrEmpty(info.DisplayName)
                ? info.DisplayName
                : "关键物";
        }

        /// <summary>内容展示名（字幕用）；找不到返回空串，绝不回退成内部 ID。</summary>
        public static string ContentName(string contentId)
        {
            return !string.IsNullOrEmpty(contentId) && MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def)
                ? def.DisplayName
                : string.Empty;
        }

        /// <summary>取内容目录里登记的专属音色；内容不存在或刻意不发声（空串）返回 null，
        /// 调用方据此回退到时刻的默认音。</summary>
        public static string ContentSfx(string contentId)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return null;
            }
            return MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def) && !string.IsNullOrEmpty(def.SfxId)
                ? def.SfxId
                : null;
        }

        private static void RaiseInternal(FeedbackCueId cue, string detail, string sfxOverride, float volumeScale)
        {
            FeedbackCueDef def = FeedbackCueCatalog.Get(cue);
            if (def == null)
            {
                return;
            }

            int index = (int)cue;
            RaiseCounts[index]++;
            LastCue = cue;

            float now = Time.realtimeSinceStartup;
            if (def.Shake > 0f)
            {
                // 远处的爆炸震得轻（与音量同一距离衰减）；设置关闭时 ScreenShake 自己拒收。
                ScreenShake.AddTrauma(def.Shake * volumeScale);
            }
            string sfx = string.IsNullOrEmpty(sfxOverride) ? def.SfxId : sfxOverride;
            if (!string.IsNullOrEmpty(sfx) && now - LastPlayRealtime[index] >= def.MinIntervalSeconds)
            {
                LastPlayRealtime[index] = now;
                PlayClip(sfx, def.Channel, def.Volume * volumeScale);
            }

            bool showCaption = def.CaptionMode == FeedbackCaptionMode.Always
                || (def.CaptionMode == FeedbackCaptionMode.SubtitlesOnly && GameSettings.SubtitlesEnabled);
            if (showCaption)
            {
                PushCaption(def, ComposeText(def.Caption, detail), now);
            }
        }

        private static string ComposeText(string caption, string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return caption ?? string.Empty;
            }
            return string.IsNullOrEmpty(caption) ? detail : caption + "：" + detail;
        }

        private static void PushCaption(FeedbackCueDef def, string text, float now)
        {
            string key = def.Tag + "|" + text;
            LastCaptionText = string.IsNullOrEmpty(text) ? "【" + def.Tag + "】" : "【" + def.Tag + "】" + text;

            for (int i = 0; i < Captions.Count; i++)
            {
                FeedbackCaption existing = Captions[i];
                if (existing.Key == key)
                {
                    existing.Count++;
                    existing.ExpiresAtRealtime = now + def.CaptionSeconds;
                    CaptionRevision++;
                    return;
                }
            }

            if (Captions.Count >= MaxVisibleCaptions)
            {
                Captions.RemoveAt(0);
            }
            Captions.Add(new FeedbackCaption
            {
                Cue = def.Id,
                Tag = def.Tag,
                Text = text,
                Tone = def.Tone,
                Count = 1,
                ExpiresAtRealtime = now + def.CaptionSeconds,
                Key = key,
            });
            CaptionRevision++;
        }

        private static void PlayClip(string sfxId, AudioType channel, float volume)
        {
            LastSfxId = sfxId;
            SfxRequestCount++;
            if (!Application.isPlaying)
            {
                return;
            }
            IAudioModule audio = GameModule.Audio;
            audio?.Play(channel, sfxId, false, Mathf.Clamp01(volume), true, true);
        }

        /// <summary>每帧由 <c>GameRoot.OnUpdate</c> 调用：设置变了就把音量推给音频模块、首帧预加载全部音效、
        /// 清掉过期字幕。</summary>
        public static void Tick()
        {
            // 同时比较“当时是否在 Play”：关闭域重载进 Play 时静态字段会从编辑器态带过来，
            // 编辑器里（自检）记下的版本号不代表音频模块真的被设置过。
            if (_appliedSettingsRevision != GameSettings.Revision || _appliedWhilePlaying != Application.isPlaying)
            {
                ApplyAudioSettings();
            }
            if (!_clipsPreloaded)
            {
                PreloadClips();
            }

            if (Captions.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            for (int i = Captions.Count - 1; i >= 0; i--)
            {
                if (Captions[i].ExpiresAtRealtime <= now)
                {
                    Captions.RemoveAt(i);
                    CaptionRevision++;
                }
            }
        }

        /// <summary>设置层（<see cref="GameSettings"/>）是音量的唯一真相：主音量→AudioListener，
        /// 音乐/音效/界面音量→对应混音分类。框架启动流程读的是它自己的一套 PlayerPrefs 键，
        /// 这里在其之后覆盖，保证“设置面板拖滑条”真实改变听到的音量。</summary>
        private static void ApplyAudioSettings()
        {
            // 先读值（首次读会触发设置读盘并让 Revision 前进），再记下已应用的版本号，
            // 否则首帧会因为“读盘让版本号 +1”而白白多应用一次。
            float master = GameSettings.MasterVolume;
            float music = GameSettings.MusicVolume;
            float sfx = GameSettings.SfxVolume;
            float ui = GameSettings.UiVolume;
            _appliedSettingsRevision = GameSettings.Revision;
            _appliedWhilePlaying = Application.isPlaying;
            if (!Application.isPlaying)
            {
                return;
            }
            IAudioModule audio = GameModule.Audio;
            if (audio == null)
            {
                return;
            }
            audio.Volume = master;
            audio.MusicVolume = music;
            audio.SoundVolume = sfx;
            audio.UISoundVolume = ui;
        }

        /// <summary>自检用：当前已推给音频模块的设置版本号是否追上了设置层。</summary>
        public static bool AudioSettingsInSync =>
            _appliedSettingsRevision == GameSettings.Revision && _appliedWhilePlaying == Application.isPlaying;

        private static void PreloadClips()
        {
            if (!Application.isPlaying)
            {
                return; // 编辑器非 Play 不置标记，进 Play 后照常预加载。
            }
            IAudioModule audio = GameModule.Audio;
            if (audio == null)
            {
                return;
            }
            _clipsPreloaded = true;
            audio.PutInAudioPool(CollectAllSfxIds());
        }

        /// <summary>本表与六大内容目录里全部非空音效名（去重）。自检用它逐个核对音频文件存在。</summary>
        public static List<string> CollectAllSfxIds()
        {
            var ids = new List<string>();
            foreach (FeedbackCueDef def in FeedbackCueCatalog.All)
            {
                AddUnique(ids, def.SfxId);
            }
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                AddUnique(ids, def.SfxId);
            }
            return ids;
        }

        private static void AddUnique(List<string> ids, string id)
        {
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        private static float DistanceAttenuation(Vector3 worldPosition)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return 1f;
            }
            Transform t = cam.transform;
            Vector3 origin = t.position;
            Vector3 forward = t.forward;
            Vector3 focus = origin;
            if (forward.y < -0.01f)
            {
                float along = -origin.y / forward.y;
                focus = origin + forward * along;
            }
            float dx = worldPosition.x - focus.x;
            float dz = worldPosition.z - focus.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (distance <= FullVolumeDistance)
            {
                return 1f;
            }
            float k = Mathf.Clamp01((distance - FullVolumeDistance) / (FloorVolumeDistance - FullVolumeDistance));
            return Mathf.Lerp(1f, FloorVolume, k);
        }

        private static float[] CreateTimes()
        {
            var times = new float[(int)FeedbackCueId.Max];
            for (int i = 0; i < times.Length; i++)
            {
                times[i] = -9999f;
            }
            return times;
        }

        /// <summary>测试/自检专用：清空计数、节流与字幕，保证用例之间互不影响。</summary>
        public static void ResetForTests()
        {
            for (int i = 0; i < RaiseCounts.Length; i++)
            {
                RaiseCounts[i] = 0;
                LastPlayRealtime[i] = -9999f;
            }
            Captions.Clear();
            CaptionRevision++;
            LastCue = FeedbackCueId.None;
            LastSfxId = null;
            LastCaptionText = null;
            SfxRequestCount = 0;
        }
    }
}
