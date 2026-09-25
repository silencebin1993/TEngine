using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Feedback;
using TEngine;

namespace GameLogic.Notifications
{
    /// <summary>通知等级（FGR-UX-020）。数值即排序：越小越紧急。</summary>
    public enum NotifyLevel : byte
    {
        Urgent = 0,
        Warning = 1,
        Info = 2,
    }

    /// <summary>等级定义（fg.TbNotifyTier 一行）。</summary>
    public sealed class NotifyTierDef
    {
        public NotifyLevel Tier;
        public string Id;
        public string NameKey;
        public string SfxId;
        public float SoundCooldownSeconds;
        public float ToastSeconds;
    }

    /// <summary>通知类型定义（fg.TbNotifyType 一行）。</summary>
    public sealed class NotifyTypeDef
    {
        public string Id;
        public NotifyLevel Tier;
        public string NameKey;
        public string SingleKey;
        public string ManyKey;
        public FeedbackCueId[] Cues = Array.Empty<FeedbackCueId>();
        public bool AutoPauseDefault;
        public float AggregateWindowSeconds;
        public bool KeepInHistory;
        public int SortOrder;
    }

    /// <summary>
    /// FG0-UX-01：通知等级与类型的唯一登记表，数据源 fg.TbNotifyTier / fg.TbNotifyType（tools/cell_tables/fgdata_ux.py）。
    /// 反馈时刻（<see cref="FeedbackCueId"/>）→ 通知类型的映射也在表里（cues 列），<see cref="TypeForCue"/> 按数组下标 O(1) 查。
    /// </summary>
    public static class NotificationCatalog
    {
        private static readonly Dictionary<string, NotifyTypeDef> Types = new Dictionary<string, NotifyTypeDef>(StringComparer.Ordinal);
        private static readonly List<NotifyTypeDef> Ordered = new List<NotifyTypeDef>();
        private static readonly NotifyTierDef[] Tiers = new NotifyTierDef[3];
        private static readonly NotifyTypeDef[] ByCue = new NotifyTypeDef[(int)FeedbackCueId.Max];
        private static readonly List<string> Errors = new List<string>();
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<string> ValidationErrors
        {
            get
            {
                EnsureLoaded();
                return Errors;
            }
        }

        public static IReadOnlyList<NotifyTypeDef> AllTypes
        {
            get
            {
                EnsureLoaded();
                return Ordered;
            }
        }

        public static bool TryGetType(string id, out NotifyTypeDef def)
        {
            EnsureLoaded();
            return Types.TryGetValue(id ?? string.Empty, out def);
        }

        public static NotifyTierDef Tier(NotifyLevel tier)
        {
            EnsureLoaded();
            int i = (int)tier;
            return i >= 0 && i < Tiers.Length ? Tiers[i] : null;
        }

        /// <summary>某反馈时刻自动转成的通知类型；没有映射时为 null（高频战斗音等不进通知）。</summary>
        public static NotifyTypeDef TypeForCue(FeedbackCueId cue)
        {
            EnsureLoaded();
            int i = (int)cue;
            return i > 0 && i < ByCue.Length ? ByCue[i] : null;
        }

        public static bool TryParseTier(string text, out NotifyLevel tier)
        {
            switch (text)
            {
                case "urgent": tier = NotifyLevel.Urgent; return true;
                case "warning": tier = NotifyLevel.Warning; return true;
                case "info": tier = NotifyLevel.Info; return true;
                default: tier = NotifyLevel.Info; return false;
            }
        }

        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            EnsureLoaded();
        }

        /// <summary>测试注入。</summary>
        public static void OverrideForTests(IEnumerable<NotifyTierDef> tiers, IEnumerable<NotifyTypeDef> types)
        {
            _overridden = true;
            _loaded = true;
            ClearAll();
            foreach (NotifyTierDef t in tiers ?? Array.Empty<NotifyTierDef>())
            {
                Tiers[(int)t.Tier] = t;
            }
            foreach (NotifyTypeDef t in types ?? Array.Empty<NotifyTypeDef>())
            {
                AddType(t);
            }
            Ordered.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void ClearAll()
        {
            Types.Clear();
            Ordered.Clear();
            Errors.Clear();
            Array.Clear(Tiers, 0, Tiers.Length);
            Array.Clear(ByCue, 0, ByCue.Length);
            _loadError = null;
        }

        private static void AddType(NotifyTypeDef def)
        {
            if (Types.ContainsKey(def.Id))
            {
                Errors.Add($"通知类型 {def.Id} 重复");
                return;
            }
            Types[def.Id] = def;
            Ordered.Add(def);
            foreach (FeedbackCueId cue in def.Cues)
            {
                int i = (int)cue;
                if (i <= 0 || i >= ByCue.Length)
                {
                    continue;
                }
                if (ByCue[i] != null)
                {
                    Errors.Add($"反馈时刻 {cue} 同时映射到通知类型 {ByCue[i].Id} 与 {def.Id}");
                    continue;
                }
                ByCue[i] = def;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            ClearAll();
            try
            {
                GameConfig.Tables tables = ConfigSystem.Instance.Tables;
                TbNotifyTier tierTable = tables?.TbNotifyTier;
                TbNotifyType typeTable = tables?.TbNotifyType;
                if (tierTable == null || typeTable == null)
                {
                    _loadError = "配置表 fg.TbNotifyTier / fg.TbNotifyType 不存在";
                }
                else
                {
                    foreach (GameConfig.fg.NotifyTier row in tierTable.DataList)
                    {
                        if (!TryParseTier(row.Id, out NotifyLevel tier))
                        {
                            Errors.Add($"通知等级 {row.Id} 无法识别");
                            continue;
                        }
                        Tiers[(int)tier] = new NotifyTierDef
                        {
                            Tier = tier,
                            Id = row.Id,
                            NameKey = row.NameKey,
                            SfxId = row.SfxId,
                            SoundCooldownSeconds = row.SoundCooldownSeconds,
                            ToastSeconds = row.ToastSeconds,
                        };
                    }
                    foreach (NotifyType row in typeTable.DataList)
                    {
                        if (!TryParseTier(row.Tier, out NotifyLevel tier))
                        {
                            Errors.Add($"通知类型 {row.Id} 的等级 {row.Tier} 无法识别");
                            continue;
                        }
                        var cues = new List<FeedbackCueId>();
                        if (row.Cues != "none")
                        {
                            foreach (string name in row.Cues.Split('|'))
                            {
                                if (Enum.TryParse(name.Trim(), false, out FeedbackCueId cue) && Enum.IsDefined(typeof(FeedbackCueId), cue))
                                {
                                    cues.Add(cue);
                                }
                                else
                                {
                                    Errors.Add($"通知类型 {row.Id} 的反馈时刻 {name} 不是 FeedbackCueId 成员");
                                }
                            }
                        }
                        AddType(new NotifyTypeDef
                        {
                            Id = row.Id,
                            Tier = tier,
                            NameKey = row.NameKey,
                            SingleKey = row.SingleKey,
                            ManyKey = row.ManyKey,
                            Cues = cues.ToArray(),
                            AutoPauseDefault = row.AutoPauseDefault == 1,
                            AggregateWindowSeconds = row.AggregateWindowSeconds,
                            KeepInHistory = row.KeepInHistory == 1,
                            SortOrder = row.SortOrder,
                        });
                    }
                    Ordered.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
                    for (int i = 0; i < Tiers.Length; i++)
                    {
                        if (Tiers[i] == null)
                        {
                            Errors.Add($"缺少通知等级 {(NotifyLevel)i}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _loadError = $"通知配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[NotificationCatalog] {_loadError}");
            }
            foreach (string e in Errors)
            {
                Log.Error($"[NotificationCatalog] {e}");
            }
            Revision++;
        }
    }
}
