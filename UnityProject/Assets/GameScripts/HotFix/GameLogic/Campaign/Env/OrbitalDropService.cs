using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-04 轨道坠落（FG10 FGR-EVT-030 / 031，FGT-EVT-005；FG19 FGR-EMG-111 / 113 坠落残骸；承接 DEBT-FG12EVT01-02 的坠落部分）：事件导演的 orbital_drop 处理器。
    /// - 何时：导演按表（第 2 幕起、3～6 游戏日随机、重大威胁 2 日一次、不与静默夜 / 剧情突袭重叠）安排；处理器不另加门槛。
    /// - Prepare：在归还核心（<see cref="SignalUplinkService.CorePosition"/>，按种子生成的世界里核心建筑的实际位置）周围
    ///   [event.orbital_drop.min_core_distance, max_core_distance] 的环里，按（世界种子, 实例序号, 第几次）取点，圆心格要可走（不是悬崖 / 水面）；
    ///   都不合格这次放弃（导演重新抽间隔）。推迟后补上的实例沿用原落点（仍合格时）。落点写进实例记录（X / Y），随存档走（B25：只取决于种子与序号）。
    /// - 预警 60 秒（导演按整数世界步计时：暂停不走、倍速同比例、观察与否一致）：通知写落点、离核心几格、倒计时；地图 / 小地图画落点圈，主视图画火光轨迹与落点圈（<c>OrbitalDropViews</c>）。
    /// - 撞击（导演开始那一刻 OnStart）：圆心在己方护盾罩内 → 护盾按剩余值吸收撞击能量（上限 shield_absorb_max）；剩余比例乘到伤害上；
    ///   圈内（与圆相交的占地）每座建筑受最大耐久 × building_damage_frac 的伤害（走 <see cref="BuildingOps.ApplyDamage"/>：打到 0 = 摧毁留虚影、自动重建规则照常接手，
    ///   核心不会在圈内）；圈内机器与敌人受 unit_damage（内核统一伤害入口）；落点留一块坠落残骸（内核残骸实体：挡弹、减速、按实体档案可燃 / 导电、可被场推动，家园长期留存）。
    ///   然后以 success 结束（OnEnd 给奖励：坠落残骸处按 fg.TbLootDrop 来源 orbital 掉材料与资料；按种子概率有舰队探针 → 一段舰队加密片段）。
    /// 开销：每步 O(1)（导演负责计时）；撞击那一刻 O(建筑数 + 单位数 + 护盾数) 一次。
    /// </summary>
    public static class OrbitalDropService
    {
        public const string EventId = "orbital_drop";
        private const uint SaltPlace = 0x4F524231u; // "ORB1"
        private const uint SaltProbe = 0x4F524232u;

        // ── 自检读数（会话内） ──
        public static int Impacts { get; private set; }
        public static int Rewards { get; private set; }
        public static int Probes { get; private set; }
        public static int PlaceRejects { get; private set; }
        public static int LastBuildingsHit { get; private set; }
        public static int LastUnitsHit { get; private set; }
        public static float LastAbsorbed { get; private set; }
        public static float LastDamageMul { get; private set; } = 1f;
        public static int LastWreckId { get; private set; }

        public sealed class Handler : EventDirectorHandler
        {
            public override string EventId => OrbitalDropService.EventId;
            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => OrbitalDropService.Prepare(s, r);
            public override void OnWarn(CampaignState s, EventDirectorInstanceRecord r) => OrbitalDropService.OnWarn(s, r);
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => OrbitalDropService.OnStart(s, r);
            public override bool Instant => true; // 撞击当场结束：只发撞击结果那一条通知

            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) => OrbitalDropService.OnEnd(s, r, outcome);
        }

        public static void ResetSessionState()
        {
            Impacts = Rewards = Probes = PlaceRejects = 0;
            LastBuildingsHit = LastUnitsHit = LastWreckId = 0;
            LastAbsorbed = 0f;
            LastDamageMul = 1f;
        }

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static float Radius => Mathf.Max(0.5f, T("event.orbital_drop.radius", 3f));
        public static float MinCoreDistance => Mathf.Max(0f, T("event.orbital_drop.min_core_distance", 12f));
        public static float MaxCoreDistance => Mathf.Max(MinCoreDistance + Radius + 1f, T("event.orbital_drop.max_core_distance", 40f));
        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>预警中的轨道坠落实例（地图 / 小地图 / 主视图读）。O(进行中事件数)。</summary>
        public static void Warned(CampaignState s, List<EventDirectorInstanceRecord> into)
        {
            into.Clear();
            foreach (EventDirectorInstanceRecord r in EventDirectorService.Active(s))
            {
                if (r != null && r.EventId == EventId && r.State == EventDirectorService.StateWarned)
                {
                    into.Add(r);
                }
            }
        }

        /// <summary>撞击前还剩几秒（按世界步；暂停不走）。</summary>
        public static double SecondsLeft(EventDirectorInstanceRecord r) =>
            r == null ? 0 : Math.Max(0, (r.StartTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz));

        /// <summary>预警进度 0（刚预警）～1（撞击）。</summary>
        public static float Progress(EventDirectorInstanceRecord r) =>
            r == null || r.StartTick <= r.WarnTick ? 1f : Mathf.Clamp01((float)((GameClock.Ticks - r.WarnTick) / (double)(r.StartTick - r.WarnTick)));

        // ── 选落点（FGR-EVT-030“落点永远不会在归还核心附近”，FGT-EVT-005；B25） ──

        /// <summary>落点合格：离归还核心 ≥ 最小距离、≤ 最远距离，圆心格是生成的可走地面（不是悬崖 / 水面）。</summary>
        public static bool ValidLanding(CampaignState s, Vector2 p)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            float d = Vector2.Distance(p, core);
            return d >= MinCoreDistance && d <= MaxCoreDistance + 1e-3f && float.IsFinite(p.x) && float.IsFinite(p.y)
                   && WorldGenQuery.IsWalkableGenerated(s, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
        }

        /// <summary>按（世界种子, 实例序号, 第几次）在核心周围的环里取点（面积均匀），返回第一个合格的；都不合格 = false。</summary>
        public static bool PickLanding(CampaignState s, int serial, out Vector2 at)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int tries = Mathf.Clamp(Mathf.RoundToInt(T("event.orbital_drop.place_attempts", 32f)), 1, 256);
            float r0 = MinCoreDistance, r1 = MaxCoreDistance;
            for (int k = 0; k < tries; k++)
            {
                double a = WorldGenMath.Hash(seed, serial, 2 * k, SaltPlace) / 4294967296.0 * Math.PI * 2.0;
                double u = WorldGenMath.Hash(seed, serial, 2 * k + 1, SaltPlace) / 4294967296.0;
                double d = Math.Sqrt(r0 * (double)r0 + u * (r1 * (double)r1 - r0 * (double)r0));
                var p = new Vector2(core.x + (float)(Math.Cos(a) * d), core.y + (float)(Math.Sin(a) * d));
                if (ValidLanding(s, p))
                {
                    at = p;
                    return true;
                }
            }
            at = default;
            return false;
        }

        // ── 处理器 ─────────────────────────────────────────────────────────────

        private static bool Prepare(CampaignState s, EventDirectorInstanceRecord r)
        {
            r.RegionId = HomeValleyLayout.RegionId; // 落在星球表面（家园所在的战斗内核）
            if (ValidLanding(s, new Vector2(r.X, r.Y)))
            {
                return true; // 推迟后补上：沿用原落点（导演默认给的归还核心位置必然不合格，会走下面重新取）
            }
            if (!PickLanding(s, r.Serial, out Vector2 at))
            {
                PlaceRejects++;
                return false;
            }
            r.X = at.x;
            r.Y = at.y;
            return true;
        }

        private static void OnWarn(CampaignState s, EventDirectorInstanceRecord r)
        {
            Vector2 at = new Vector2(r.X, r.Y);
            int dist = Mathf.RoundToInt(Vector2.Distance(at, SignalUplinkService.CorePosition(s)));
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, at,
                GameText.Format("orbital_drop.notify.warn", Mathf.RoundToInt(r.X), Mathf.RoundToInt(r.Y), dist, CalendarService.ShortDuration(SecondsLeft(r))));
        }

        private static void OnStart(CampaignState s, EventDirectorInstanceRecord r)
        {
            Impact(s, r);
            EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeSuccess); // 撞击即结束；奖励在 OnEnd
        }

        /// <summary>撞击结算（一次性）：护盾吸收 → 圈内建筑重创 → 圈内单位受击 → 落点留坠落残骸 → 通知。</summary>
        private static void Impact(CampaignState s, EventDirectorInstanceRecord r)
        {
            var at = new Vector2(r.X, r.Y);
            float radius = Radius;
            CombatSite site = HomeSite;
            float energy = Mathf.Max(1f, T("event.orbital_drop.impact_energy", 1000f));
            float cap = Mathf.Clamp01(T("event.orbital_drop.shield_absorb_max", 0.75f));
            float absorbed = site?.AbsorbImpact(at, energy * cap) ?? 0f;
            float mul = Mathf.Clamp01(1f - absorbed / energy);
            int buildings = HitBuildings(s, at, radius, Mathf.Clamp01(T("event.orbital_drop.building_damage_frac", 0.8f)) * mul);
            int units = site?.StrikeCircle(at, radius, Mathf.Max(0f, T("event.orbital_drop.unit_damage", 300f)) * mul) ?? 0;
            int wreck = site?.SpawnWreckAt(at, Mathf.Max(0.2f, T("event.orbital_drop.wreck_radius", 2f)), Mathf.Max(1f, T("event.orbital_drop.wreck_hp", 1500f)),
                Mathf.Max(0f, T("event.orbital_drop.wreck_mass", 60f)), T("event.orbital_drop.wreck_temp", 400f)) ?? 0;
            Impacts++;
            LastBuildingsHit = buildings;
            LastUnitsHit = units;
            LastAbsorbed = absorbed;
            LastDamageMul = mul;
            LastWreckId = wreck;
            string shield = absorbed > 0f ? GameText.Format("orbital_drop.shield", Mathf.RoundToInt(100f * absorbed / energy)) : string.Empty;
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, at,
                GameText.Format("orbital_drop.notify.impact", Mathf.RoundToInt(r.X), Mathf.RoundToInt(r.Y), buildings, units, shield));
        }

        /// <summary>占地与落点圈相交的建筑（含炮塔 / 防御建筑；施工中 / 虚影 / 已毁的不受，ApplyDamage 自己判）各受最大耐久 × <paramref name="frac"/>。返回受创座数。O(建筑数)，一次性。</summary>
        public static int HitBuildings(CampaignState s, Vector2 at, float radius, float frac)
        {
            if (s?.BuildingRecords == null || frac <= 0f)
            {
                return 0;
            }
            var ids = new List<string>();
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b == null)
                {
                    continue;
                }
                StandingRuleService.BoundsOf(b, out GridCell min, out GridCell max);
                float cx = Mathf.Clamp(at.x, min.X, max.X + 1f);
                float cy = Mathf.Clamp(at.y, min.Y, max.Y + 1f);
                if ((cx - at.x) * (cx - at.x) + (cy - at.y) * (cy - at.y) <= radius * radius)
                {
                    ids.Add(b.BuildingId);
                }
            }
            int hits = 0;
            foreach (string id in ids) // 先收集再结算：摧毁会改 BuildingRecords
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, id);
                if (b == null)
                {
                    continue;
                }
                float hp0 = Defense.RepairDroneService.DurabilityOf(s, b);
                if (BuildingOps.ApplyDamage(s, id, frac * BuildingOps.MaxDurability(b.BuildingTypeId), notify: false) || b.Health < hp0 - 0.001f)
                {
                    hits++;
                }
            }
            return hits;
        }

        private static void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
        {
            if (outcome != EventDirectorService.OutcomeSuccess)
            {
                return; // 预警中被撤回（静默夜 / 剧情突袭后排上）：预警只发过通知，没有要收回的效果；补上时重新 Prepare（沿用落点）
            }
            var at = new Vector2(r.X, r.Y);
            FieldLootService.OnOrbitalDrop(s, at, r.Serial); // FGR-EVT-031：坠落残骸处的稀有材料 / 组件 / 旧时代资料（派机器回收）
            IntelFragmentDef frag = null;
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            double roll = WorldGenMath.Hash(seed, r.Serial, 0, SaltProbe) / 4294967296.0;
            if (roll < Mathf.Clamp01(T("event.orbital_drop.probe_chance", 0.35f)))
            {
                frag = IntelService.GrantFleetFragment(s); // “有时是舰队探针（剧情伏笔）”
                if (frag != null)
                {
                    Probes++;
                    FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, at, GameText.Get("orbital_drop.notify.probe"));
                }
            }
            EventDirectorService.SetHistoryReward(s, r.Serial, 0, frag?.Id);
            Endgame.PostgameService.OnOrbitalDrop(s, at, r.Serial); // FG14-SBX-01（FGR-SBX-004）：后日谈的坠落残骸常带来人类遗产
            Rewards++;
        }

        /// <summary>自检：这个实例（序号）按种子会不会有舰队探针。</summary>
        public static bool RollsProbe(CampaignState s, int serial) =>
            WorldGenMath.Hash(unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed)), serial, 0, SaltProbe) / 4294967296.0 < Mathf.Clamp01(T("event.orbital_drop.probe_chance", 0.35f));
    }
}
