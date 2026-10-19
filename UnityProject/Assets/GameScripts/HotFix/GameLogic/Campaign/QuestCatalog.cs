using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig;
using GameConfig.fg;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG8-QST-01（FGR-QST-001～005、020）：fg.TbQuest（主线 / 支线 / 家园指引的定义）与 fg.TbKeyMaterial（关键材料清单）的只读访问。
    /// 判定与状态全部在 <see cref="CampaignObjectiveTracker"/>（同一个重算入口、同一份 ObjectiveRecord）；本类不写任何战役状态。
    /// </summary>
    public static class QuestCatalog
    {
        public const string CatMain = "main";
        public const string CatSide = "side";
        public const string CatGuide = "guide";

        public const string KindLeader = "leader_defeated";
        public const string KindBuildType = "build_type";
        public const string KindBuildBelt = "build_belt";
        public const string KindSilentNight = "silent_night";
        public const string KindOwnOutpost = "own_outpost";
        public const string KindReach = "reach";
        public const string KindHold = "hold";
        public const string KindEscort = "escort";
        public const string KindDestroy = "destroy_outpost";

        public const string AnchorNone = "none";
        public const string AnchorCore = "core";
        public const string AnchorTerritoryCenter = "territory_center";
        public const string AnchorSideSite = "side_site";
        public const string AnchorSideNearest = "side_nearest";
        public const string AnchorSideCenter = "side_center";

        public const string UnlockStart = "start";
        public const string UnlockTerritoryVisible = "territory_visible";
        public const string UnlockPrereq = "prereq";
        public const string UnlockSideFound = "side_found";

        public const string None = "none";

        private static readonly List<Quest> _rows = new List<Quest>();
        private static readonly List<Quest> _sideRows = new List<Quest>();
        private static readonly Dictionary<string, Quest> _byId = new Dictionary<string, Quest>(StringComparer.Ordinal);
        private static readonly List<KeyMaterial> _keys = new List<KeyMaterial>();
        private static bool _loaded;

        /// <summary>读表失败的原因（自检 / 诊断）；null = 正常。</summary>
        public static string LoadError { get; private set; }

        public static IReadOnlyList<Quest> Rows
        {
            get
            {
                EnsureLoaded();
                return _rows;
            }
        }

        /// <summary>支线池（按 weight 抽取）。</summary>
        public static IReadOnlyList<Quest> SideRows
        {
            get
            {
                EnsureLoaded();
                return _sideRows;
            }
        }

        public static IReadOnlyList<KeyMaterial> KeyMaterials
        {
            get
            {
                EnsureLoaded();
                return _keys;
            }
        }

        public static bool Loaded
        {
            get
            {
                EnsureLoaded();
                return _rows.Count > 0;
            }
        }

        public static Quest Get(string id)
        {
            EnsureLoaded();
            return id != null && _byId.TryGetValue(id, out Quest q) ? q : null;
        }

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            _rows.Clear();
            _sideRows.Clear();
            _byId.Clear();
            _keys.Clear();
            LoadError = null;
            try
            {
                Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbQuest == null)
                {
                    LoadError = "fg.TbQuest 没有加载";
                    return;
                }
                foreach (Quest q in t.TbQuest.DataList)
                {
                    if (q == null || string.IsNullOrEmpty(q.Id))
                    {
                        continue;
                    }
                    _rows.Add(q);
                    _byId[q.Id] = q;
                    if (q.Category == CatSide && q.Weight > 0)
                    {
                        _sideRows.Add(q);
                    }
                }
                _rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                _sideRows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                if (t.TbKeyMaterial != null)
                {
                    foreach (KeyMaterial k in t.TbKeyMaterial.DataList)
                    {
                        if (k != null)
                        {
                            _keys.Add(k);
                        }
                    }
                    _keys.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
                }
            }
            catch (Exception e)
            {
                LoadError = "fg.TbQuest 读不到：" + e.Message;
                _rows.Clear();
                _sideRows.Clear();
                _byId.Clear();
            }
        }

        // ── 文字 ──────────────────────────────────────────────────────────

        public static string CategoryName(string category) => GameText.Get("quest.cat." + (string.IsNullOrEmpty(category) ? CatMain : category));

        public static string SourceName(string source) => GameText.Get("quest.src." + (string.IsNullOrEmpty(source) ? "core_memo" : source));

        public static string Title(Quest q) => q != null ? GameText.Get(q.TitleKey) : string.Empty;

        public static string Goal(Quest q) => q != null ? GameText.Get(q.GoalKey) : string.Empty;

        public static bool HasTimeLimit(Quest q) => q != null && q.TimeHours > 0f;

        public static bool IsRestartable(Quest q) => q != null && q.Restartable == 1;

        public static bool CanAbandon(Quest q) => q != null && q.Category != CatMain;

        /// <summary>奖励预览（FGR-QST-003）：“熔炉心 ×1”/“技术数据 ×30”/“防腐涂层蓝图”；护送类附“货运车留在你这里”。</summary>
        public static string RewardText(Quest q)
        {
            if (q == null)
            {
                return string.Empty;
            }
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(q.RewardItem) && q.RewardItem != None && q.RewardAmount > 0)
            {
                parts.Add(GameText.Format("quest.reward.item", Economy.ItemCatalog.NameOf(q.RewardItem), q.RewardAmount.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrEmpty(q.RewardBlueprint) && q.RewardBlueprint != None)
            {
                KeyMaterial k = KeyMaterialOf(q.RewardBlueprint);
                parts.Add(k != null ? GameText.Get(k.NameKey) : q.RewardBlueprint);
            }
            if (q.Kind == KindEscort)
            {
                parts.Add(GameText.Get("quest.reward.escort"));
            }
            return string.Join(" + ", parts);
        }

        public static KeyMaterial KeyMaterialOf(string id)
        {
            foreach (KeyMaterial k in KeyMaterials)
            {
                if (k.Id == id)
                {
                    return k;
                }
            }
            return null;
        }

        /// <summary>“X 小时 Y 分”/“X 天 Y 小时”（游戏时间）。</summary>
        public static string FormatRemaining(double seconds)
        {
            double hour = Math.Max(1e-6, GameClock.HourSeconds);
            int totalMinutes = Math.Max(0, (int)Math.Ceiling(Math.Max(0.0, seconds) / (hour / 60.0) - 1e-6));
            int hours = totalMinutes / 60;
            if (hours >= 24)
            {
                return GameText.Format("quest.time.d", (hours / 24).ToString(CultureInfo.InvariantCulture), (hours % 24).ToString(CultureInfo.InvariantCulture));
            }
            return GameText.Format("quest.time.hm", hours.ToString(CultureInfo.InvariantCulture), (totalMinutes % 60).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>稳定哈希（FNV-1a，与运行时 / 平台无关），支线按（世界种子, 地点）抽取用。</summary>
        public static uint StableHash(int seed, string text)
        {
            unchecked
            {
                uint h = 2166136261u ^ (uint)seed;
                h *= 16777619u;
                if (text != null)
                {
                    foreach (char c in text)
                    {
                        h ^= c;
                        h *= 16777619u;
                    }
                }
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return h;
            }
        }
    }
}
