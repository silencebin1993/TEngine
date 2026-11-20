using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>
    /// FG15-UX-01（FGR-SYS-020 游戏性“伤害数字”）：被观察地点里单位受伤时在头上飘的伤害数字（数据层；画在 <c>ReactionPopupHudUIToolkit</c> 同一层）。
    /// - 设置关着（默认关）时一条都不收；
    /// - 同一单位在合并窗口内连续受伤合成一条（数字累加），不刷屏；同时存在的条数有上限，满了挤掉最旧的；
    /// - 只是画面：不影响战斗结果、统计与存档（不进存档，换观察地点 / 离开世界时清空）。
    /// 每条内核“受伤”事件 O(条数上限)，与单位数无关。
    /// </summary>
    public static class DamageNumbers
    {
        public sealed class Entry
        {
            public int Serial;
            public int Unit;
            public float Amount;
            public Vector3 Position;
            public float Born;
            public float LastBump;

            public string Text => Mathf.Max(1, Mathf.RoundToInt(Amount)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static readonly List<Entry> ActiveList = new List<Entry>(16);
        private static int _serial;

        public static IReadOnlyList<Entry> Active => ActiveList;
        public static long PushedCount { get; private set; }
        public static long MergedCount { get; private set; }
        public static long DroppedBySetting { get; private set; }

        private static float Tune(string id, float fallback) => UiTuningValues.TryGet(id, out float v) && v > 0f ? v : fallback;
        public static float LifeSeconds => Tune("settings.damage_number_life_seconds", 0.9f);
        public static float MergeSeconds => Tune("settings.damage_number_merge_seconds", 0.35f);
        public static int Capacity => Mathf.Max(1, Mathf.RoundToInt(Tune("settings.damage_number_pool", 16f)));

        /// <summary>收一条伤害。返回是否被接收（设置关着 / 伤害 ≤ 0 不收）。</summary>
        public static bool Push(int unit, float amount, Vector3 position, float now)
        {
            if (!GameSettings.DamageNumbersEnabled)
            {
                DroppedBySetting++;
                return false;
            }
            if (amount <= 0f)
            {
                return false;
            }
            PushedCount++;
            for (int i = 0; i < ActiveList.Count; i++)
            {
                Entry e = ActiveList[i];
                if (e.Unit == unit && now - e.LastBump <= MergeSeconds)
                {
                    e.Amount += amount;
                    e.Position = position;
                    e.LastBump = now;
                    MergedCount++;
                    return true;
                }
            }
            if (ActiveList.Count >= Capacity)
            {
                ActiveList.RemoveAt(0); // 挤掉最旧的（列表按出生时间排列）。
            }
            ActiveList.Add(new Entry { Serial = ++_serial, Unit = unit, Amount = amount, Position = position, Born = now, LastBump = now });
            return true;
        }

        /// <summary>过期清理（真实秒）。</summary>
        public static void Tick(float now)
        {
            float life = LifeSeconds;
            for (int i = ActiveList.Count - 1; i >= 0; i--)
            {
                if (now - ActiveList[i].LastBump > life)
                {
                    ActiveList.RemoveAt(i);
                }
            }
        }

        public static void Clear() => ActiveList.Clear();

        public static void ResetForTests()
        {
            ActiveList.Clear();
            PushedCount = 0;
            MergedCount = 0;
            DroppedBySetting = 0;
        }
    }
}
