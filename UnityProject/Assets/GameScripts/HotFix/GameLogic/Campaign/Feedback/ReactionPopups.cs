using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>FG2-FW-04（FGR-FW-043“触发时弹出反应名；大量触发时聚合显示（燃爆 ×12），同一屏幕每秒弹字有上限（初值 6 条）”；
    /// 承接 DEBT-FG2FW02-02“读法触发的弹字”）：打在世界位置上的弹字队列（纯逻辑，时间由调用方给真实秒；界面 <c>ReactionPopupHudUIToolkit</c> 只负责画）。
    ///
    /// - 聚合：同一个键（同一条反应 / 同一种读法）已有弹字还活着，就把次数加到它上面（“短路 ×12”）并续命，不另开一条；续命有总上限，
    ///   持续不断的触发每隔一段时间换一条新的（数字不会一直涨到看不清）。
    /// - 每秒上限：任意 1 真实秒内新开的弹字不超过 reaction.popup_per_second 条；超出的次数不丢，按键攒进“待显示”，名额空出来时先放反应、再放读法。
    /// - 只放镜头正在观察的地点（“同一屏幕”）：调用方判断；换观察地点时 <see cref="Clear"/>。
    /// - 设置“反应弹字”关闭：不进队列（字幕、日志、统计照常）。
    /// 开销：每次 Push O(存活弹字数)，每帧 Tick O(存活 + 待显示)，两者都有上限，与单位数、触发次数无关。</summary>
    public static class ReactionPopups
    {
        public sealed class Popup
        {
            public int Serial;
            public string Key;
            public string Name;
            public int Count;
            public Vector3 Position;
            public float Born;
            public float LastBump;
            public bool First;
            public bool Reading;

            /// <summary>显示的文字：1 次 = “短路！”；多次 = “短路 ×12”；本存档第一次触发 = “新反应：短路！”。</summary>
            public string Text
            {
                get
                {
                    if (First)
                    {
                        return GameText.Format("reaction.popup.first", Name);
                    }
                    return Count > 1 ? GameText.Format("reaction.popup.many", Name, Count) : GameText.Format("reaction.cue", Name);
                }
            }
        }

        private struct Pending
        {
            public string Key;
            public string Name;
            public int Count;
            public Vector3 Position;
            public bool First;
            public bool Reading;
        }

        private static readonly List<Popup> ActiveList = new List<Popup>(16);
        private static readonly List<Pending> PendingList = new List<Pending>(16);
        private static readonly Queue<float> SpawnTimes = new Queue<float>(16);
        private static int _serial;

        public static IReadOnlyList<Popup> Active => ActiveList;
        public static int PendingCount => PendingList.Count;

        /// <summary>自检读点：累计推进来的次数、被设置关掉而丢弃的次数、新开的弹字条数、合并进已有弹字的次数。</summary>
        public static long PushedCount { get; private set; }
        public static long DroppedBySetting { get; private set; }
        public static long SpawnedCount { get; private set; }
        public static long MergedCount { get; private set; }
        /// <summary>任意 1 秒窗口里新开弹字的最大条数（自检断言不超过上限）。</summary>
        public static int PeakPerSecond { get; private set; }
        public static int Revision { get; private set; }

        public static int PerSecondCap => Mathf.Max(1, TuningInt("reaction.popup_per_second", 6));
        public static float LifeSeconds => Mathf.Max(0.2f, Tuning("reaction.popup_life_seconds", 1.2f));
        public static float MaxLifeSeconds => Mathf.Max(LifeSeconds, Tuning("reaction.popup_max_life_seconds", 3f));
        public static int PoolSize => Mathf.Max(PerSecondCap, TuningInt("reaction.popup_pool", 12));

        /// <summary>推进 <paramref name="count"/> 次同键的触发（<paramref name="now"/> = 真实秒）。返回 false = 设置关闭，没进队列。</summary>
        public static bool Push(string key, string name, int count, Vector3 position, bool first, bool reading, float now)
        {
            if (count <= 0 || string.IsNullOrEmpty(key))
            {
                return false;
            }
            PushedCount += count;
            if (!GameSettings.ReactionPopupsEnabled)
            {
                DroppedBySetting += count;
                return false;
            }
            Expire(now);
            // 首次触发的那一条总是单独开（“新反应：短路！”），不并进已有的普通弹字。
            if (!first && TryMerge(key, count, position, now))
            {
                return true;
            }
            if (CanSpawn(now))
            {
                Spawn(key, name, count, position, first, reading, now);
                return true;
            }
            AddPending(key, name, count, position, first, reading);
            return true;
        }

        /// <summary>每帧：过期的弹字移除；名额空出来时放“待显示”（先反应后读法、先首次触发）。</summary>
        public static void Tick(float now)
        {
            Expire(now);
            while (PendingList.Count > 0 && CanSpawn(now))
            {
                int pick = PickPending();
                Pending p = PendingList[pick];
                PendingList.RemoveAt(pick);
                if (p.First || !TryMerge(p.Key, p.Count, p.Position, now))
                {
                    Spawn(p.Key, p.Name, p.Count, p.Position, p.First, p.Reading, now);
                }
            }
            // 待显示里的键在已有弹字上合并（不占名额）：例如名额用完时同一条反应继续触发。
            for (int i = PendingList.Count - 1; i >= 0; i--)
            {
                Pending p = PendingList[i];
                if (!p.First && TryMerge(p.Key, p.Count, p.Position, now))
                {
                    PendingList.RemoveAt(i);
                }
            }
        }

        /// <summary>当前所有弹字 + 待显示里的次数总和（自检：聚合与限流不丢次数）。</summary>
        public static long VisibleAndPendingCount
        {
            get
            {
                long n = 0;
                foreach (Popup p in ActiveList)
                {
                    n += p.Count;
                }
                foreach (Pending p in PendingList)
                {
                    n += p.Count;
                }
                return n;
            }
        }

        /// <summary>换观察地点 / 离开世界 / 关掉设置：全部清掉（不补放）。</summary>
        public static void Clear()
        {
            ActiveList.Clear();
            PendingList.Clear();
            SpawnTimes.Clear();
            Revision++;
        }

        public static void ResetForTests()
        {
            Clear();
            PushedCount = 0;
            DroppedBySetting = 0;
            SpawnedCount = 0;
            MergedCount = 0;
            PeakPerSecond = 0;
            _serial = 0;
        }

        private static bool TryMerge(string key, int count, Vector3 position, float now)
        {
            for (int i = 0; i < ActiveList.Count; i++)
            {
                Popup p = ActiveList[i];
                if (p.Key == key && !p.First && now - p.Born < MaxLifeSeconds)
                {
                    p.Count += count;
                    p.Position = position;
                    p.LastBump = now;
                    MergedCount += count;
                    Revision++;
                    return true;
                }
            }
            return false;
        }

        private static bool CanSpawn(float now)
        {
            while (SpawnTimes.Count > 0 && now - SpawnTimes.Peek() >= 1f)
            {
                SpawnTimes.Dequeue();
            }
            return SpawnTimes.Count < PerSecondCap && ActiveList.Count < PoolSize;
        }

        private static void Spawn(string key, string name, int count, Vector3 position, bool first, bool reading, float now)
        {
            ActiveList.Add(new Popup
            {
                Serial = ++_serial,
                Key = key,
                Name = name,
                Count = count,
                Position = position,
                Born = now,
                LastBump = now,
                First = first,
                Reading = reading,
            });
            SpawnTimes.Enqueue(now);
            SpawnedCount++;
            PeakPerSecond = Mathf.Max(PeakPerSecond, SpawnTimes.Count);
            Revision++;
        }

        private static void AddPending(string key, string name, int count, Vector3 position, bool first, bool reading)
        {
            for (int i = 0; i < PendingList.Count; i++)
            {
                Pending p = PendingList[i];
                if (p.Key == key && p.First == first)
                {
                    p.Count += count;
                    p.Position = position;
                    PendingList[i] = p;
                    return;
                }
            }
            PendingList.Add(new Pending { Key = key, Name = name, Count = count, Position = position, First = first, Reading = reading });
        }

        private static int PickPending()
        {
            int best = 0;
            for (int i = 1; i < PendingList.Count; i++)
            {
                Pending a = PendingList[i];
                Pending b = PendingList[best];
                int ra = a.First ? 0 : a.Reading ? 2 : 1;
                int rb = b.First ? 0 : b.Reading ? 2 : 1;
                if (ra < rb)
                {
                    best = i;
                }
            }
            return best;
        }

        private static void Expire(float now)
        {
            float life = LifeSeconds;
            float maxLife = MaxLifeSeconds;
            for (int i = ActiveList.Count - 1; i >= 0; i--)
            {
                Popup p = ActiveList[i];
                if (now - p.LastBump >= life || now - p.Born >= maxLife + life)
                {
                    ActiveList.RemoveAt(i);
                    Revision++;
                }
            }
        }

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>();

        /// <summary>界面调参（fg.TbUiTuning）；缺失时用规格初值并告警一次（不静默，也不在每帧抛异常）。</summary>
        internal static float Tuning(string id, float fallback)
        {
            if (UiTuningValues.TryGet(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                TEngine.Log.Warning($"[ReactionPopups] 界面调参 {id} 缺失（fg.TbUiTuning），暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_ux.py 后重新生成）。");
            }
            return fallback;
        }

        internal static int TuningInt(string id, int fallback) => Mathf.RoundToInt(Tuning(id, fallback));
    }
}
