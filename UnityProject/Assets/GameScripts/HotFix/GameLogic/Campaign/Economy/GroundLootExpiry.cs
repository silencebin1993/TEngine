using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG8-EXP-05（FG08 FGR-EXP-032“没被带走的普通战利品留在地上，1 个游戏日后被敌方回收并消失（初值）”“关键材料永远不会消失”；负向“普通战利品到期消失前的提示”）。
    /// <para>落地时盖章（<see cref="Stamp"/>）：野外掉落（<see cref="FieldLootService"/>）、机器阵亡 / 丢弃掉在地上的货舱物品（<see cref="LootPickService"/>）
    /// 写 <see cref="GroundItemRecord.ExpireTick"/> = 现在 + loot.expire.days 游戏日；关键材料按 <see cref="KeyItemRetention.OfItem"/> + <see cref="KeyItemRetention.NeverExpires"/> 不盖章（永不消失）。
    /// 黑匣子 / 区域关键物 / 遗产 / 数据包不是地面物，本来就不会到期。家园物流自己的地面物（施工退料、拆除、搬运掉落）不盖章，不会消失。</para>
    /// <para>到期（<see cref="WorldStep"/>）：在家园 / 带仓库的前哨站范围内的不回收（那里是自己的地盘，由搬运单收走；章清掉）；其余从地上移走、身份表移走、还没装车的搬运单取消，
    /// 按地点汇总发一条通知（写最近一处的坐标，可点击定位）。到期前 loot.expire.warn_hours 游戏小时提示一次（每堆一次，进存档）。</para>
    /// 只看统一时钟步（与观察 / 倍速 / 读档无关；暂停时不推进）。平时 O(1)：只在地面物数组换了或盖了新章时重算一次“最近的到期步”（O(地面物)），到点才扫一遍。
    /// </summary>
    public static class GroundLootExpiry
    {
        public const string NotifyType = "loot_expiry";

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;

        /// <summary>普通战利品在地上留多久（游戏日，FG08 初值 1）。</summary>
        public static float LifeDays => Tuning("loot.expire.days", 1f);

        /// <summary>到期前多久提示（游戏小时）。</summary>
        public static float WarnHours => Tuning("loot.expire.warn_hours", 3f);

        public static long LifeTicks => Math.Max(1L, GameClock.TicksFor(LifeDays * GameClock.DaySeconds));
        public static long WarnTicks => Math.Max(1L, GameClock.TicksFor(WarnHours * GameClock.HourSeconds));

        // ── 自检读点 ──
        public static int Expired { get; private set; }
        public static int Warned { get; private set; }
        public static int Scans { get; private set; }
        public static int KeptInDepot { get; private set; }
        public static double LastScanMs { get; private set; }
        public static double MaxScanMs { get; private set; }

        private static GroundItemRecord[] _seen;
        private static bool _dirty = true;
        private static long _nextDue;
        private static readonly List<Vector2> Depots = new List<Vector2>(16);
        private static readonly HashSet<string> Removed = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetSessionState()
        {
            _seen = null;
            _dirty = true;
            _nextDue = 0;
            Expired = 0;
            Warned = 0;
            Scans = 0;
            KeptInDepot = 0;
            LastScanMs = 0;
            MaxScanMs = 0;
        }

        public static void ResetScanStats() => MaxScanMs = 0;

        /// <summary>地面物有变化（盖章 / 改了到期步）：下一步重算最近的到期步。</summary>
        public static void Touch() => _dirty = true;

        /// <summary>这件物品落在地上会不会被回收（普通战利品会；关键材料不会）。</summary>
        public static bool Expires(string itemId) => !KeyItemRetention.NeverExpires(KeyItemRetention.OfItem(itemId));

        /// <summary>
        /// 一份战利品落在地上：普通战利品盖章（现在 + 1 个游戏日），关键材料不盖（永不消失）。已经盖过的（并进同一堆）保留原来的到期步——
        /// 不因为后来又掉了一点就续命（与“1 日后被回收”的取舍一致，见 ADR-EXP-007）。
        /// </summary>
        public static void Stamp(CampaignState s, GroundItemRecord g, string itemId)
        {
            if (s == null || g == null)
            {
                return;
            }
            if (!Expires(itemId))
            {
                if (g.ExpireTick != 0)
                {
                    g.ExpireTick = 0; // 关键材料并进了普通战利品的堆：整堆不再消失（关键材料优先，D-13）
                    _dirty = true;
                }
                return;
            }
            if (g.ExpireTick > 0)
            {
                return;
            }
            g.ExpireTick = GameClock.Ticks + LifeTicks;
            g.ExpireWarned = false;
            _dirty = true;
        }

        /// <summary>还剩多少游戏秒被回收（不会消失 = -1）。</summary>
        public static double RemainingSeconds(long expireTick) =>
            expireTick <= 0 ? -1 : Math.Max(0L, expireTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz);

        /// <summary>倒计时文字：“还剩 X 小时 Y 分被敌方回收” / “永不消失”。</summary>
        public static string CountdownText(long expireTick) =>
            expireTick <= 0 ? GameText.Get("settle.left.never") : GameText.Format("settle.left.countdown", GameClock.FormatGameDuration(RemainingSeconds(expireTick)));

        /// <summary>每个模拟步（家园载入时）：平时 O(1)；到了最近的到期 / 提示步才扫一遍地面物。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            GroundItemRecord[] arr = s?.GroundItems;
            if (arr == null || arr.Length == 0)
            {
                _seen = arr;
                _nextDue = 0;
                return;
            }
            if (_dirty || !ReferenceEquals(arr, _seen))
            {
                _seen = arr;
                _dirty = false;
                _nextDue = NextDue(arr);
            }
            if (_nextDue <= 0 || tick < _nextDue)
            {
                return;
            }
            Process(s, tick);
            _seen = s.GroundItems;
            _nextDue = NextDue(_seen ?? Array.Empty<GroundItemRecord>());
            _dirty = false;
        }

        private static long NextDue(GroundItemRecord[] arr)
        {
            long warn = WarnTicks;
            long best = 0;
            foreach (GroundItemRecord g in arr)
            {
                if (g == null || g.ExpireTick <= 0)
                {
                    continue;
                }
                long due = g.ExpireWarned ? g.ExpireTick : Math.Max(1L, g.ExpireTick - warn);
                if (best == 0 || due < best)
                {
                    best = due;
                }
            }
            return best;
        }

        /// <summary>通知：家园所在星球上的带位置（可点击定位）；远征地点里的不带（通知定位按星球坐标，避免飞错地方）。</summary>
        private static void Post(string text, string region, Vector2 at)
        {
            if (string.IsNullOrEmpty(region) || region == HomeValleyLayout.RegionId)
            {
                NotificationCenter.Post(NotifyType, text, new Vector3(at.x, 0f, at.y));
            }
            else
            {
                NotificationCenter.Post(NotifyType, text);
            }
        }

        /// <summary>到期 / 提示（O(地面物 + 搬运单)，只在到点那一步）。自检直接调。</summary>
        public static void Process(CampaignState s, long tick)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Scans++;
            GroundItemRecord[] arr = s.GroundItems ?? Array.Empty<GroundItemRecord>();
            LootPickService.CollectDepotPositions(s, Depots);
            float r2 = LootPickCatalog.DepotRadius * LootPickCatalog.DepotRadius;
            long warn = WarnTicks;
            Removed.Clear();
            int warnCount = 0;
            Vector2 warnAt = default;
            string warnRegion = null;
            long warnSoonest = 0;
            int goneCount = 0;
            Vector2 goneAt = default;
            string goneRegion = null;
            foreach (GroundItemRecord g in arr)
            {
                if (g == null || g.ExpireTick <= 0)
                {
                    continue;
                }
                bool inDepot = false;
                if (g.RegionId == HomeValleyLayout.RegionId)
                {
                    foreach (Vector2 d in Depots)
                    {
                        if ((d - g.Position).sqrMagnitude <= r2)
                        {
                            inDepot = true;
                            break;
                        }
                    }
                }
                if (inDepot && tick >= g.ExpireTick - warn)
                {
                    // 在家园 / 带仓库的前哨站范围内：自己的地盘，敌方不会来回收——章清掉，交给搬运单。
                    g.ExpireTick = 0;
                    KeptInDepot++;
                    continue;
                }
                if (tick >= g.ExpireTick)
                {
                    Removed.Add(g.GroundItemId);
                    goneCount++;
                    goneAt = g.Position;
                    goneRegion = g.RegionId;
                    continue;
                }
                if (!g.ExpireWarned && tick >= g.ExpireTick - warn)
                {
                    g.ExpireWarned = true;
                    warnCount++;
                    if (warnSoonest == 0 || g.ExpireTick < warnSoonest)
                    {
                        warnSoonest = g.ExpireTick;
                        warnAt = g.Position;
                        warnRegion = g.RegionId;
                    }
                }
            }
            if (Removed.Count > 0)
            {
                var keep = new List<GroundItemRecord>(arr.Length);
                var salvage = new List<string>(Removed.Count);
                foreach (GroundItemRecord g in arr)
                {
                    if (g != null && Removed.Contains(g.GroundItemId))
                    {
                        salvage.Add(g.SalvageInstanceId);
                        continue;
                    }
                    keep.Add(g);
                }
                s.GroundItems = keep.ToArray();
                foreach (string sid in salvage)
                {
                    FieldLootService.TakeTag(s, sid);
                }
                CargoService.CancelHauls(s, new List<string>(Removed));
                Expired += Removed.Count;
                LootPickService.Touch();
                Post(GameText.Format("settle.expire.gone", goneCount, Mathf.RoundToInt(goneAt.x), Mathf.RoundToInt(goneAt.y)), goneRegion, goneAt);
                Log.Info($"[GroundLootExpiry] {goneCount} 份地上的普通战利品到期，被敌方回收（第 {tick} 步）。");
            }
            if (warnCount > 0)
            {
                Warned += warnCount;
                Post(GameText.Format("settle.expire.warn", warnCount, GameClock.FormatGameDuration(RemainingSeconds(warnSoonest)),
                    Mathf.RoundToInt(warnAt.x), Mathf.RoundToInt(warnAt.y)), warnRegion, warnAt);
            }
            Removed.Clear();
            Depots.Clear();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastScanMs = ms;
            MaxScanMs = Math.Max(MaxScanMs, ms);
        }
    }
}
