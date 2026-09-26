using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Notifications
{
    /// <summary>聚合通知里的一条成员。</summary>
    public sealed class NotificationMember
    {
        public string Detail = string.Empty;
        public bool HasLocation;
        public string RegionId = string.Empty;
        public Vector3 Location;
        public string CreatedAtUtc = string.Empty;
        public int GameDay;
        public double GameSeconds;

        /// <summary>细节的玩家可见文字：细节本身是文本键时按当前语言解析。</summary>
        public string DetailText => string.IsNullOrEmpty(Detail) ? string.Empty : GameText.Has(Detail) ? GameText.Get(Detail) : Detail;
    }

    /// <summary>一条（可能聚合过的）通知。运行时对象，同时持有它在存档里的记录（写穿）。</summary>
    public sealed class NotificationEntry
    {
        public long Id;
        public NotifyTypeDef Type;
        public NotifyLevel Level;
        public int Count = 1;
        public readonly List<NotificationMember> Members = new List<NotificationMember>();
        public string SourceKey = string.Empty;
        public string SourceArg = string.Empty;
        /// <summary>最后一次并入的真实时间；从存档读回的通知为负无穷（不再与新通知聚合）。</summary>
        public float LastRealtime = float.NegativeInfinity;
        public float ToastShownAt;
        public float ToastExpiresAt;
        /// <summary>存档里对应的记录（不进历史的类型为 null）。</summary>
        internal NotificationRecord Record;

        public NotificationMember Latest => Members.Count > 0 ? Members[Members.Count - 1] : null;

        /// <summary>显示正文：聚合时“3 处缺电”，单条时“电力不足：发电机”，没有细节时显示类型名。</summary>
        public string Text
        {
            get
            {
                if (Count > 1)
                {
                    return GameText.Format(Type.ManyKey, Count.ToString(CultureInfo.InvariantCulture));
                }
                string detail = Latest?.DetailText ?? string.Empty;
                return string.IsNullOrEmpty(detail) ? GameText.Get(Type.NameKey) : GameText.Format(Type.SingleKey, detail);
            }
        }

        /// <summary>来源说明（FGR-BASE-020：由规则 / 系统触发的动作可追溯）。空 = 没有来源说明。</summary>
        public string SourceText =>
            string.IsNullOrEmpty(SourceKey) ? string.Empty : GameText.Format(SourceKey, GameText.Has(SourceArg) ? GameText.Get(SourceArg) : SourceArg);

        public bool HasAnyLocation
        {
            get
            {
                for (int i = 0; i < Members.Count; i++)
                {
                    if (Members[i].HasLocation)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    /// <summary>
    /// FG0-UX-01（FGR-UX-020/021/022、FG00 B08、FGT-UX-004）：通知中心。
    ///
    /// - **分级**：紧急 / 警告 / 信息三级，等级与类型都来自 fg.TbNotifyTier / fg.TbNotifyType。
    /// - **聚合**：同类通知在该类型的聚合窗口（真实秒）内合并为一条（“3 座建筑缺电”），成员逐条保留细节与位置，点开可展开。
    /// - **定位**：成员带位置时可让镜头飞过去（<see cref="LocateHandler"/> 由 GameRoot 注入，接到当前区域的镜头）。
    /// - **历史**：按等级、类型筛选；随战役存档（<see cref="CampaignState.Notifications"/>，写穿），读档后继续；
    ///   读档变更（FG0-SAVE-01 的 SaveHistory.Notices）自动转进历史（DEBT-FG0SAVE01-04）。
    /// - **自动暂停**：按类型勾选（默认突袭到达、静默夜开始、首领阶段转换），触发后发一条“由通知「X」触发”的说明（FGR-BASE-020）。
    /// - **声音**：等级提示音带冷却；由反馈时刻转来的通知不再重复发声（反馈时刻自己已有专属音）。
    /// - 所有计时用真实时间（<see cref="Clock"/>），不受暂停和 0.5x～3x 倍速影响。
    ///
    /// 开销：发一条通知 O(1)（聚合查找按类型字典）+ 写穿存档 O(历史条数) 的数组追加；每帧 <see cref="Tick"/> 只看弹出条（≤5）。
    /// 与实体数无关。
    /// </summary>
    public static class NotificationCenter
    {
        private static readonly List<NotificationEntry> HistoryList = new List<NotificationEntry>();
        private static readonly List<NotificationEntry> ToastList = new List<NotificationEntry>();
        private static readonly Dictionary<string, NotificationEntry> LatestByType = new Dictionary<string, NotificationEntry>(StringComparer.Ordinal);
        private static readonly float[] LastTierSound = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        private static CampaignState _bound;
        private static bool _boundOnce;
        private static long _nextTransientId = -1;
        private static SaveNoticeRecord[] _lastCheckedNotices;

        /// <summary>真实时间来源（秒）。测试可注入以精确控制聚合窗口与弹出条过期。</summary>
        public static Func<float> Clock = () => Time.realtimeSinceStartup;

        /// <summary>自动暂停的执行者：成功让世界进入暂停时返回 true（已经暂停或没有可暂停的世界时返回 false）。GameRoot 注入。</summary>
        public static Func<bool> AutoPauseHandler;

        /// <summary>定位执行者：（区域 ID，世界坐标）→ 是否成功，失败时给出原因文本键。GameRoot 注入。</summary>
        public delegate bool LocateDelegate(string regionId, Vector3 position, out string failureKey);

        public static LocateDelegate LocateHandler;

        /// <summary>当前区域 ID（通知产生时记录在成员上，定位时与当前区域比对）。GameRoot 注入。</summary>
        public static Func<string> RegionProvider;

        /// <summary>每次有变化 +1，界面据此刷新（O(1) 比较）。</summary>
        public static int Revision { get; private set; }

        public static int AutoPauseCount { get; private set; }
        public static int TierSoundCount { get; private set; }
        public static string LastTierSoundId { get; private set; }

        public static IReadOnlyList<NotificationEntry> History => HistoryList;
        public static IReadOnlyList<NotificationEntry> Toasts => ToastList;

        /// <summary>未读的紧急通知条数（HUD 角标用）。</summary>
        public static int UnreadUrgent { get; private set; }

        public static void MarkAllRead()
        {
            if (UnreadUrgent != 0)
            {
                UnreadUrgent = 0;
                Revision++;
            }
        }

        // ── 发通知 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 发一条通知。<paramref name="detail"/> 可以是文本键（显示时按当前语言解析）或已经拼好的细节。
        /// <paramref name="sourceKey"/>/<paramref name="sourceArg"/>：由规则或系统触发时的来源说明（FGR-BASE-020）。
        /// 未知类型返回 null 并记 Warning（不静默吞掉，但也不打断玩法）。
        /// </summary>
        public static NotificationEntry Post(string typeId, string detail = null, Vector3? location = null,
            string sourceKey = null, string sourceArg = null)
        {
            return PostInternal(typeId, detail, location, sourceKey, sourceArg, fromCue: false);
        }

        /// <summary>反馈时刻 → 通知（在 <see cref="FeedbackCues"/> 里调用）。没有映射的时刻（开火、命中……）直接返回。</summary>
        public static void OnCue(FeedbackCueId cue, string detail, Vector3? location)
        {
            NotifyTypeDef def = NotificationCatalog.TypeForCue(cue);
            if (def == null)
            {
                return;
            }
            PostInternal(def.Id, detail, location, null, null, fromCue: true);
        }

        private static NotificationEntry PostInternal(string typeId, string detail, Vector3? location, string sourceKey,
            string sourceArg, bool fromCue)
        {
            if (!NotificationCatalog.TryGetType(typeId, out NotifyTypeDef def))
            {
                TEngine.Log.Warning($"[NotificationCenter] 未登记的通知类型 {typeId ?? "null"}，已忽略（在 fg.TbNotifyType 登记后再发）");
                return null;
            }
            EnsureBound();
            float now = Clock();
            var member = new NotificationMember
            {
                Detail = detail ?? string.Empty,
                HasLocation = location.HasValue,
                Location = location ?? Vector3.zero,
                RegionId = RegionProvider?.Invoke() ?? string.Empty,
                CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                GameDay = _bound?.Clock?.Day ?? 0,
                GameSeconds = _bound?.Clock?.GameSeconds ?? 0,
            };

            NotificationEntry entry;
            if (LatestByType.TryGetValue(def.Id, out NotificationEntry latest)
                && now - latest.LastRealtime <= def.AggregateWindowSeconds
                && latest.SourceKey == (sourceKey ?? string.Empty))
            {
                entry = latest;
                entry.Count++;
                AddMember(entry, member);
            }
            else
            {
                entry = new NotificationEntry
                {
                    Type = def,
                    Level = def.Tier,
                    SourceKey = sourceKey ?? string.Empty,
                    SourceArg = sourceArg ?? string.Empty,
                };
                entry.Members.Add(member);
                LatestByType[def.Id] = entry;
                if (def.KeepInHistory)
                {
                    entry.Id = NextId();
                    HistoryList.Add(entry);
                    TrimHistory();
                }
                else
                {
                    entry.Id = _nextTransientId--;
                }
            }
            entry.LastRealtime = now;
            WriteThrough(entry);
            ShowToast(entry, now);
            if (entry.Level == NotifyLevel.Urgent)
            {
                UnreadUrgent++;
                GuidanceHooks.Raise(GuidanceHooks.FirstUrgentNotification);
            }
            if (!fromCue)
            {
                PlayTierSound(entry.Level, now);
            }
            Revision++;
            TryAutoPause(def);
            return entry;
        }

        private static void AddMember(NotificationEntry entry, NotificationMember member)
        {
            entry.Members.Add(member);
            int cap = Math.Max(1, UiTuningValues.GetInt("notify.aggregate_members_max"));
            while (entry.Members.Count > cap)
            {
                entry.Members.RemoveAt(0);
            }
        }

        private static void TryAutoPause(NotifyTypeDef def)
        {
            if (!GameSettings.IsNotifyAutoPauseEnabled(def.Id, def.AutoPauseDefault) || AutoPauseHandler == null)
            {
                return;
            }
            if (!AutoPauseHandler())
            {
                return;
            }
            AutoPauseCount++;
            GuidanceHooks.Raise(GuidanceHooks.FirstAutoPause);
            // 可追溯：暂停是哪条通知触发的（FGR-BASE-020 / FGR-UX-021）。
            PostInternal("auto_paused", def.NameKey, null, "ui.notify.source_system", def.NameKey, fromCue: true);
        }

        // ── 弹出条 ─────────────────────────────────────────────────────────

        private static void ShowToast(NotificationEntry entry, float now)
        {
            NotifyTierDef tier = NotificationCatalog.Tier(entry.Level);
            float seconds = (tier?.ToastSeconds ?? 4f) * GameSettings.NotificationToastScale;
            entry.ToastShownAt = now;
            entry.ToastExpiresAt = now + seconds;
            ToastList.Remove(entry);
            ToastList.Insert(0, entry);
            int max = Math.Max(1, UiTuningValues.GetInt("notify.toast_max_visible"));
            while (ToastList.Count > max)
            {
                // 挤掉最不紧急、最早出现的一条；紧急通知不会被信息通知挤掉。
                int victim = 0;
                for (int i = 1; i < ToastList.Count; i++)
                {
                    NotificationEntry a = ToastList[i];
                    NotificationEntry b = ToastList[victim];
                    if (a.Level > b.Level || (a.Level == b.Level && a.ToastShownAt < b.ToastShownAt))
                    {
                        victim = i;
                    }
                }
                ToastList.RemoveAt(victim);
            }
        }

        public static void DismissToast(NotificationEntry entry)
        {
            if (ToastList.Remove(entry))
            {
                Revision++;
            }
        }

        /// <summary>每帧由 GameRoot 调用：过期弹出条、跟随战役切换重新绑定历史、取走设置迁移的提示。</summary>
        public static void Tick()
        {
            EnsureBound();
            // 旧设置迁移的提示要等玩家真的进了游戏世界（已绑定战役、在区域里）才发：主菜单阶段通知条不显示，
            // 而且新建 / 读档时 Bind 会重建历史，提前发的这条会被清掉（设置已写回新格式，以后也不会再发）。
            if (GameSettings.PendingKeyMigration.Migrated && _bound != null && !string.IsNullOrEmpty(RegionProvider?.Invoke()))
            {
                InputBindingSet.LegacyMigrationReport report = GameSettings.PendingKeyMigration;
                GameSettings.ClearPendingKeyMigration();
                // 没改过键的老玩家也要知道默认键变了（接入、任务日志、命令、编组都换了键）。
                Post("settings_changed", report.KeptCount > 0 || report.DroppedCount > 0
                    ? GameText.Format("input.rebind.migrated",
                        report.KeptCount.ToString(CultureInfo.InvariantCulture), report.DroppedCount.ToString(CultureInfo.InvariantCulture))
                    : "input.rebind.migrated_defaults");
            }
            if (ToastList.Count == 0)
            {
                return;
            }
            float now = Clock();
            bool changed = false;
            for (int i = ToastList.Count - 1; i >= 0; i--)
            {
                if (now >= ToastList[i].ToastExpiresAt)
                {
                    ToastList.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed)
            {
                Revision++;
            }
        }

        private static void PlayTierSound(NotifyLevel level, float now)
        {
            NotifyTierDef tier = NotificationCatalog.Tier(level);
            if (tier == null || string.IsNullOrEmpty(tier.SfxId))
            {
                return;
            }
            int i = (int)level;
            if (now - LastTierSound[i] < tier.SoundCooldownSeconds)
            {
                return;
            }
            LastTierSound[i] = now;
            TierSoundCount++;
            LastTierSoundId = tier.SfxId;
            FeedbackCues.PlayNotificationSound(tier.SfxId);
        }

        // ── 查询与定位 ─────────────────────────────────────────────────────

        /// <summary>按等级 / 类型筛选历史，最新的在前。<paramref name="level"/>/<paramref name="typeId"/> 为 null 表示不筛。</summary>
        public static void Query(NotifyLevel? level, string typeId, List<NotificationEntry> into)
        {
            into.Clear();
            for (int i = HistoryList.Count - 1; i >= 0; i--)
            {
                NotificationEntry e = HistoryList[i];
                if (level.HasValue && e.Level != level.Value)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(typeId) && e.Type.Id != typeId)
                {
                    continue;
                }
                into.Add(e);
            }
        }

        /// <summary>镜头定位到通知的某个成员（默认最新一条带位置的）。失败时给出原因文本键（不静默）。</summary>
        public static bool Locate(NotificationEntry entry, int memberIndex, out string failureKey)
        {
            failureKey = "ui.notify.no_location";
            if (entry == null)
            {
                return false;
            }
            NotificationMember member = null;
            if (memberIndex >= 0 && memberIndex < entry.Members.Count)
            {
                member = entry.Members[memberIndex];
            }
            else
            {
                for (int i = entry.Members.Count - 1; i >= 0; i--)
                {
                    if (entry.Members[i].HasLocation)
                    {
                        member = entry.Members[i];
                        break;
                    }
                }
            }
            if (member == null || !member.HasLocation)
            {
                return false;
            }
            if (LocateHandler == null)
            {
                failureKey = "ui.notify.no_camera";
                return false;
            }
            return LocateHandler(member.RegionId, member.Location, out failureKey);
        }

        /// <summary>显示用的时间：统一时钟接入后为“第 N 日 HH:MM”，之前显示本地时间（FGR-UX-004）。</summary>
        public static string TimeText(NotificationMember m)
        {
            if (m == null)
            {
                return string.Empty;
            }
            if (m.GameDay > 0)
            {
                // FG0-ARCH-01：统一时钟接入后按游戏日换算（1x 下 1 游戏日 = clock.day_seconds 真实秒），与世界时间条同一公式。
                return GameText.Format("ui.notify.time_day", m.GameDay.ToString(CultureInfo.InvariantCulture),
                    GameLogic.Core.GameClock.FormatHhMm(m.GameSeconds));
            }
            if (DateTime.TryParse(m.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime utc))
            {
                return GameText.Format("ui.notify.time_real", utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
            }
            return string.Empty;
        }

        // ── 存档绑定（写穿）──────────────────────────────────────────────

        private static void EnsureBound()
        {
            CampaignState current = CampaignSession.Current;
            if (_boundOnce && ReferenceEquals(current, _bound))
            {
                ImportSaveNotices();
                return;
            }
            Bind(current);
        }

        /// <summary>换战役（新建、读档、回主菜单）时重建运行时历史：从存档读回通知，并把尚未转入的读档变更转进来。</summary>
        public static void Bind(CampaignState state)
        {
            _boundOnce = true;
            _bound = state;
            _lastCheckedNotices = null;
            HistoryList.Clear();
            ToastList.Clear();
            LatestByType.Clear();
            UnreadUrgent = 0;
            if (state != null)
            {
                CampaignFgStateDomains.EnsureAll(state);
                foreach (NotificationRecord r in state.Notifications.Entries)
                {
                    if (r == null || !NotificationCatalog.TryGetType(r.TypeId, out NotifyTypeDef def))
                    {
                        continue; // 类型已从表里删除：不显示，但记录原样留在存档里（下一次写穿会随截断淘汰）。
                    }
                    var entry = new NotificationEntry
                    {
                        Id = r.Id,
                        Type = def,
                        Level = def.Tier,
                        Count = Math.Max(1, r.Count),
                        SourceKey = r.SourceKey ?? string.Empty,
                        SourceArg = r.SourceArg ?? string.Empty,
                        Record = r,
                    };
                    foreach (NotificationMemberRecord m in r.Members)
                    {
                        entry.Members.Add(new NotificationMember
                        {
                            Detail = m.Detail ?? string.Empty,
                            HasLocation = m.HasLocation,
                            RegionId = m.RegionId ?? string.Empty,
                            Location = new Vector3(m.X, m.Y, m.Z),
                            CreatedAtUtc = m.CreatedAtUtc ?? string.Empty,
                            GameDay = m.GameDay,
                            GameSeconds = m.GameSeconds,
                        });
                    }
                    HistoryList.Add(entry);
                }
                HistoryList.Sort((a, b) => a.Id.CompareTo(b.Id));
                ImportSaveNotices();
            }
            Revision++;
        }

        /// <summary>DEBT-FG0SAVE01-04：读档变更通知（SaveHistory.Notices）转入通知历史，每条只转一次（按 NoticeId 去重，去重表进存档）。</summary>
        private static void ImportSaveNotices()
        {
            CampaignState s = _bound;
            if (s?.SaveHistory?.Notices == null || s.SaveHistory.Notices.Length == 0)
            {
                return;
            }
            if (ReferenceEquals(s.SaveHistory.Notices, _lastCheckedNotices))
            {
                return; // 读档变更数组没换过（SaveContentReconciler 追加时会换新数组）：不用再查，发通知保持 O(1)。
            }
            _lastCheckedNotices = s.SaveHistory.Notices;
            string[] imported = s.Notifications.ImportedSaveNoticeIds ?? Array.Empty<string>();
            if (imported.Length >= s.SaveHistory.Notices.Length && AllImported(s, imported))
            {
                return;
            }
            var seen = new HashSet<string>(imported, StringComparer.Ordinal);
            var added = new List<string>();
            foreach (SaveNoticeRecord notice in s.SaveHistory.Notices)
            {
                if (notice == null || string.IsNullOrEmpty(notice.NoticeId) || !seen.Add(notice.NoticeId))
                {
                    continue;
                }
                added.Add(notice.NoticeId);
                // 存档里只有文本键与参数：此刻按当前语言渲染成细节（与读档字幕同一渲染函数）。
                string text = SaveContentReconciler.Render(notice);
                PostHistoryOnly("save_migrated", text, notice.CreatedAtUtc);
            }
            if (added.Count > 0)
            {
                var all = new List<string>(imported);
                all.AddRange(added);
                s.Notifications.ImportedSaveNoticeIds = all.ToArray();
                Revision++;
            }
        }

        private static bool AllImported(CampaignState s, string[] imported)
        {
            var set = new HashSet<string>(imported, StringComparer.Ordinal);
            foreach (SaveNoticeRecord n in s.SaveHistory.Notices)
            {
                if (n != null && !string.IsNullOrEmpty(n.NoticeId) && !set.Contains(n.NoticeId))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>读档变更只进历史、不弹出条（读档时已有字幕逐条弹出，这里不重复打扰）。</summary>
        private static void PostHistoryOnly(string typeId, string detail, string createdAtUtc)
        {
            if (!NotificationCatalog.TryGetType(typeId, out NotifyTypeDef def))
            {
                return;
            }
            var entry = new NotificationEntry { Type = def, Level = def.Tier, Id = NextId() };
            entry.Members.Add(new NotificationMember { Detail = detail ?? string.Empty, CreatedAtUtc = createdAtUtc ?? string.Empty });
            HistoryList.Add(entry);
            TrimHistory();
            WriteThrough(entry);
        }

        private static long NextId()
        {
            if (_bound == null)
            {
                return HistoryList.Count == 0 ? 1 : HistoryList[HistoryList.Count - 1].Id + 1;
            }
            long id = Math.Max(_bound.Notifications.NextId, 1);
            _bound.Notifications.NextId = id + 1;
            return id;
        }

        private static void TrimHistory()
        {
            int cap = Math.Max(1, UiTuningValues.GetInt("notify.history_capacity"));
            if (HistoryList.Count <= cap)
            {
                return;
            }
            int remove = HistoryList.Count - cap;
            for (int i = 0; i < remove; i++)
            {
                NotificationEntry old = HistoryList[i];
                if (LatestByType.TryGetValue(old.Type.Id, out NotificationEntry latest) && ReferenceEquals(latest, old))
                {
                    LatestByType.Remove(old.Type.Id);
                }
                ToastList.Remove(old);
            }
            HistoryList.RemoveRange(0, remove);
            if (_bound != null)
            {
                long oldestKept = HistoryList[0].Id;
                _bound.Notifications.Entries = Array.FindAll(_bound.Notifications.Entries, r => r != null && r.Id >= oldestKept);
            }
        }

        private static void WriteThrough(NotificationEntry entry)
        {
            if (_bound == null || !entry.Type.KeepInHistory || entry.Id <= 0)
            {
                return;
            }
            NotificationRecord r = entry.Record;
            if (r == null)
            {
                r = new NotificationRecord { Id = entry.Id, TypeId = entry.Type.Id };
                entry.Record = r;
                NotificationRecord[] old = _bound.Notifications.Entries ?? Array.Empty<NotificationRecord>();
                var grown = new NotificationRecord[old.Length + 1];
                Array.Copy(old, grown, old.Length);
                grown[old.Length] = r;
                _bound.Notifications.Entries = grown;
            }
            r.Count = entry.Count;
            r.SourceKey = entry.SourceKey;
            r.SourceArg = entry.SourceArg;
            var members = new NotificationMemberRecord[entry.Members.Count];
            for (int i = 0; i < members.Length; i++)
            {
                NotificationMember m = entry.Members[i];
                members[i] = new NotificationMemberRecord
                {
                    Detail = m.Detail,
                    HasLocation = m.HasLocation,
                    RegionId = m.RegionId,
                    X = m.Location.x,
                    Y = m.Location.y,
                    Z = m.Location.z,
                    CreatedAtUtc = m.CreatedAtUtc,
                    GameDay = m.GameDay,
                    GameSeconds = m.GameSeconds,
                };
            }
            r.Members = members;
        }

        /// <summary>自检用：清空运行时状态（不动存档）。</summary>
        public static void ResetForTests()
        {
            HistoryList.Clear();
            ToastList.Clear();
            LatestByType.Clear();
            for (int i = 0; i < LastTierSound.Length; i++)
            {
                LastTierSound[i] = float.NegativeInfinity;
            }
            _bound = null;
            _boundOnce = false;
            _lastCheckedNotices = null;
            _nextTransientId = -1;
            AutoPauseCount = 0;
            TierSoundCount = 0;
            LastTierSoundId = null;
            UnreadUrgent = 0;
            Clock = () => Time.realtimeSinceStartup;
            Revision++;
        }
    }
}
