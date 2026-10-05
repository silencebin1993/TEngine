using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032；FGT-DEF-005）：攻城行为的唯一业务入口（热更层）。
    ///
    /// - <b>到达展开</b>（DEBT-FG6DEF04-02 / DEBT-FG0ARCH03-01）：突袭队伍到达家园（或前哨站）后，按编成（fg.TbRaidUnit：单位种类 × 数量、其中精英、职能）
    ///   在集结点附近按确定性的螺旋排布生成战斗内核单位（攻城参数 fg.TbSiegeUnit、耐久 fg.TbMechEnemy、精英 ×siege.elite_*），置 <c>Engaged</c>，
    ///   开反应归因的突袭场次（FG-GAP-056）、发“突袭部队展开”通知。聚合体本身停在集结点。
    /// - <b>攻城剧场</b>：目标点周围 siege.theater_radius_cells 内的己方建筑外接矩形（含集结点）+ 边距，截到 siege.theater_max_cells；
    ///   职能表（fg.TbSiegeTarget）与集结点交给内核。剧场开着时，炮塔 / 防御建筑之外的建筑进内核当攻城目标（<see cref="SyncStructures"/>），
    ///   炮塔 / 防御结构单位标上类别与占地；耐久与建筑记录双向对账（受伤 = 内核 → 建筑，维修 = 建筑 → 内核）；阵亡 = 建筑被摧毁（留虚影、电网重算、废墟不再挡路）。
    /// - <b>按职能选目标、流场、破墙、撤退的逐单位逻辑全部在内核</b>（Main/Sim/Combat/CombatSiege.cs，Burst）。
    /// - <b>撤退</b>（FGR-DEF-032）：到达后超过 raid.time_limit_hours，或损失超过 siege.loss_retreat_ratio，全队改走撤退流场回集结点；走到的离场
    ///   （内核事件 SiegeExited）；全部离场 / 阵亡后：有幸存者 → 并回行进队伍、沿原路回据点（途中照常可被发现 / 拦截）；没有 → 被全歼（计划记 destroyed，队伍移除）。
    /// - <b>溅射</b>（承接 DEBT-FG3LOG03-04 / DEBT-FG3LOG02-03 / DEBT-FG3LOG05-12）：敌人打中建筑时，内核在被打的那一侧记命中格；每步取至多 N 格，
    ///   命中格附近的传送带 / 管线挨打、施工中的虚影累计伤害（打满 = 施工中被摧毁）。
    /// 推进只看步序号，与观察无关（FGR-BASE-021）；热更层每步 O(队伍数 + 溅射格数上限)，每 siege.sync_seconds O(剧场里的建筑数)；逐单位 / 逐格在 AOT。
    /// </summary>
    public static partial class SiegeService
    {
        public const string ReasonTime = "time";
        public const string ReasonLosses = "losses";

        /// <summary>本会话统计（自检 / 性能证据）。</summary>
        public static int UnfoldCount { get; private set; }
        public static int RetreatOrders { get; private set; }
        public static int Regroups { get; private set; }
        public static int Annihilations { get; private set; }
        /// <summary>撤退兜底触发的次数（下撤退令满 siege.retreat_max_seconds 还有单位走不回集结点 → 就地离场）。</summary>
        public static int RetreatTimeouts { get; private set; }
        public static int SyncCount { get; private set; }
        public static double LastUnfoldMs { get; private set; }
        public static double LastSyncMs { get; private set; }
        public static double MaxSyncMs { get; private set; }
        public static string LastProblem { get; private set; } = string.Empty;

        private static readonly HashSet<string> HookedOnce = new HashSet<string>(StringComparer.Ordinal);
        private static readonly int[] RoleScratch = new int[4];
        private static readonly List<int2> ExitScratch = new List<int2>(4);

        public static SiegeState StateOf(CampaignState s) => s?.Raids?.Siege;

        public static void EnsureState(CampaignState s)
        {
            if (s?.Raids == null)
            {
                return;
            }
            SiegeState st = s.Raids.Siege ??= new SiegeState();
            st.Structures ??= Array.Empty<SiegeStructureRecord>();
            st.SiteDamage ??= Array.Empty<SiegeSiteDamageRecord>();
            st.NotifiedBreaches ??= Array.Empty<string>();
            if (st.NextSerial < 1)
            {
                st.NextSerial = 1;
            }
            foreach (SiegeStructureRecord r in st.Structures)
            {
                if (r != null)
                {
                    r.BuildingId ??= string.Empty;
                }
            }
        }

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时缓存清空（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            Rt.Clear();
            HookedOnce.Clear();
            UnfoldCount = 0;
            RetreatOrders = 0;
            Regroups = 0;
            Annihilations = 0;
            RetreatTimeouts = 0;
            SyncCount = 0;
            LastSyncMs = 0;
            MaxSyncMs = 0;
            LastProblem = string.Empty;
            CombatSite.SiegeRoleVisuals = SiegeCatalog.BuildRoleVisuals();
        }

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static void Hook(string id)
        {
            if (HookedOnce.Add(id))
            {
                GuidanceHooks.Raise(id);
            }
        }

        private static void BindEvents()
        {
            CombatSite.SiegeStructEvent ??= OnStructKilled;
            CombatSite.SiegeExitEvent ??= OnUnitExited;
            if (CombatSite.SiegeRoleVisuals == null)
            {
                CombatSite.SiegeRoleVisuals = SiegeCatalog.BuildRoleVisuals();
            }
        }

        public static int KeyOf(TransitGroupRecord g)
        {
            if (g == null)
            {
                return 0;
            }
            if (g.NavKey > 0)
            {
                return g.NavKey;
            }
            if (g.GroupId != null && g.GroupId.StartsWith("transit-", StringComparison.Ordinal)
                && int.TryParse(g.GroupId.Substring("transit-".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                g.NavKey = n;
            }
            return g.NavKey;
        }

        /// <summary>到达家园 / 前哨站、已展开攻城、还没走完的队伍（不含拦截交战）。</summary>
        public static bool IsSieging(TransitGroupRecord g) =>
            g != null && g.Kind == TransitGroupKind.Raid && g.State == TransitGroupState.Arrived && g.Engaged && !g.Intercepted;

        public static IEnumerable<TransitGroupRecord> SiegingGroups(CampaignState state)
        {
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (IsSieging(g))
                {
                    yield return g;
                }
            }
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>
        /// 世界模拟的每个固定步（家园载入时，在防御 / 维修无人机之后）：到达的队伍展开；溅射命中每步结算至多 N 格；
        /// 每 siege.sync_seconds 对账一次（建筑结构单位、撤退条件、全歼 / 收拢、破墙通知、剧场开关）。暂停不走，与观察无关。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || worldHz <= 0)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (site == null || site.IsDisposed)
            {
                return;
            }
            EnsureState(state);
            BindEvents();
            TransitGroupRecord[] groups = state.Raids?.InTransit;
            if (groups != null)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    TransitGroupRecord g = groups[i];
                    if (g != null && g.Kind == TransitGroupKind.Raid && g.State == TransitGroupState.Arrived && !g.Engaged && !g.Intercepted && g.UnitCount > 0)
                    {
                        Unfold(state, site, g);
                    }
                }
            }
            StepIntercepts(state, site, ticksBefore, worldHz);
            DrainCollateral(state, site);
            long every = Math.Max(1, (long)Math.Round(SiegeCatalog.SyncSeconds * worldHz));
            if (ticksBefore % every == 0)
            {
                Sync(state, site);
            }
        }

        /// <summary>对账（每 siege.sync_seconds）：剧场开关、建筑结构单位、撤退条件、全歼 / 收拢、破墙通知。</summary>
        public static void Sync(CampaignState state, CombatSite site)
        {
            if (state == null || site == null || site.IsDisposed)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SyncCount++;
            SiegeState st = StateOf(state);
            CheckGroups(state, site);
            DespawnOrphans(state, site);
            bool any = false;
            foreach (TransitGroupRecord g in SiegingGroups(state))
            {
                any = true;
                break;
            }
            if (st.TheaterActive && !any)
            {
                EndTheater(state, site);
            }
            if (st.TheaterActive)
            {
                SyncStructures(state, site);
                NotifyBreaches(state, site);
            }
            StepGuards(state, site);
            LastSyncMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxSyncMs = Math.Max(MaxSyncMs, LastSyncMs);
        }

        // ─────────────────────────────── 展开 ───────────────────────────────

        private static readonly List<(RaidUnitDef Def, bool Elite)> UnitScratch = new List<(RaidUnitDef, bool)>(64);

        /// <summary>
        /// 一支到达的队伍展开成战斗内核单位：集结点 = 队伍位置附近第一个敌方可走格；剧场覆盖目标与集结点；按编成逐台生成（职能、精英、攻城参数）。
        /// 返回生成的台数。编成为空（测试 / 调试派出的老队伍）时按人数生成该阵营编成表里第一种单位。
        /// </summary>
        public static int Unfold(CampaignState state, CombatSite site, TransitGroupRecord g)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int key = KeyOf(g);
            if (key <= 0 || site == null || site.IsDisposed)
            {
                return 0;
            }
            GridCell gather = NearestPassable(NavService.CellOf(g.PosX, g.PosY), 12);
            g.GatherX = gather.X;
            g.GatherY = gather.Y;
            var target = new Vector2((float)g.TargetX, (float)g.TargetY);
            ExtendTheater(state, site, new GridCell(gather.X, gather.Y), NavService.CellOf(g.TargetX, g.TargetY));
            BuildUnitList(g);
            int n = SpawnUnits(site, key, gather, target, g.Faction);
            g.Engaged = true;
            g.UnfoldedCount = n;
            g.ExitedCount = 0;
            g.SiegeRetreat = false;
            g.SiegeRetreatReason = string.Empty;
            g.SiegeRetreatTick = -1;
            StateOf(state).TotalUnfolded += n;
            ApplyExits(state, site);
            SyncStructures(state, site);
            ReactionAttribution.BeginRaid(state, g.GroupId);
            UnfoldCount++;
            TEngine.Log.Info($"[SiegeService] 突袭 {g.GroupId}（{g.Faction}）在 ({gather.X},{gather.Y}) 展开 {n} 台，第 {GameClock.Ticks} 步");
            Hook(GuidanceHooks.SiegeFirstUnfold);
            int a = 0, b = 0, c = 0;
            foreach ((RaidUnitDef def, bool _) in UnitScratch)
            {
                CombatSiegeRole r = SiegeCatalog.RoleOf(def?.Role);
                if (r == CombatSiegeRole.Sabotage)
                {
                    b++;
                }
                else if (r == CombatSiegeRole.Siege)
                {
                    c++;
                }
                else
                {
                    a++;
                }
            }
            string where = GameText.Get(g.TargetKind == RaidDirectorService.TargetOutpost ? "siege.notify.unfold_outpost" : "siege.notify.unfold_home");
            NotificationCenter.Post("raid_siege", GameText.Format("siege.notify.unfold", n, where, a, b, c), new Vector3(gather.X, 0f, gather.Y));
            LastUnfoldMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return n;
        }

        private static void BuildUnitList(TransitGroupRecord g)
        {
            UnitScratch.Clear();
            int kinds = Math.Min(g.UnitIds?.Length ?? 0, Math.Min(g.UnitCounts?.Length ?? 0, g.EliteCounts?.Length ?? 0));
            for (int k = 0; k < kinds; k++)
            {
                if (!RaidCatalog.TryGetUnit(g.UnitIds[k], out RaidUnitDef def))
                {
                    continue;
                }
                int count = Math.Max(0, g.UnitCounts[k]);
                int elites = Math.Max(0, Math.Min(count, g.EliteCounts[k]));
                for (int j = 0; j < count; j++)
                {
                    UnitScratch.Add((def, j < elites));
                }
            }
            if (UnitScratch.Count > 0)
            {
                return;
            }
            RaidUnitDef fallback = null;
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (fallback == null || (!string.IsNullOrEmpty(g.Faction) && u.Faction == g.Faction && fallback.Faction != g.Faction))
                {
                    fallback = u;
                }
            }
            for (int j = 0; j < Math.Max(1, g.UnitCount); j++)
            {
                UnitScratch.Add((fallback, false));
            }
        }

        private static int SpawnUnits(CombatSite site, int key, GridCell gather, Vector2 target, string faction)
        {
            int step = Math.Max(1, Mathf.RoundToInt(SiegeCatalog.SpawnSpacing));
            int placed = 0;
            int next = 0;
            int ring = 0;
            int want = UnitScratch.Count;
            var used = new HashSet<long>();
            while (next < want && ring <= 48)
            {
                for (int dy = -ring; dy <= ring && next < want; dy++)
                {
                    for (int dx = -ring; dx <= ring && next < want; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }
                        int x = gather.X + dx * step;
                        int y = gather.Y + dy * step;
                        long k = ((long)x << 32) ^ (uint)y;
                        if (used.Contains(k) || !NavService.PassableNow(x, y, NavConst.ClassHostile))
                        {
                            continue;
                        }
                        used.Add(k);
                        // 复审修复（P2）：编成下标与成功台数分开记——生成失败（内核满）丢的是这一台本身、下标照样前进，不再缩短编成、总丢掉表尾那台（精英 / 维修机）。
                        (RaidUnitDef def, bool elite) = UnitScratch[next++];
                        if (SpawnOne(site, key, new Vector2(x, y), target, def, elite) > 0)
                        {
                            placed++;
                        }
                        else
                        {
                            LastProblem = "攻城单位生成失败（战斗内核已满？）：" + (def?.EnemyTypeId ?? "(无编成)");
                        }
                    }
                }
                ring++;
            }
            return placed;
        }

        /// <summary>生成一台攻城单位（按 fg.TbSiegeUnit；表里没有这种敌人时按原型参数，并记问题）。</summary>
        public static int SpawnOne(CombatSite site, int key, Vector2 at, Vector2 target, RaidUnitDef def, bool elite)
        {
            CombatSiegeRole role = SiegeCatalog.RoleOf(def?.Role);
            if (role == CombatSiegeRole.None)
            {
                role = CombatSiegeRole.Assault;
            }
            float hpScale = SiegeCatalog.HpScale * (elite ? SiegeCatalog.EliteHpScale : 1f);
            float dmgScale = elite ? SiegeCatalog.EliteDamageScale : 1f;
            if (def == null || !SiegeCatalog.TryGetUnit(def.EnemyTypeId, out SiegeUnitDef u))
            {
                CombatBench.Spec s = CombatBench.FromTuning();
                u = new SiegeUnitDef
                {
                    Hp = s.RaiderHp, Speed = s.RaiderSpeed, Radius = s.RaiderRadius, Range = s.RaiderRange, Damage = s.RaiderDamage, Cooldown = s.RaiderCooldown,
                    ProjectileSpeed = s.RaiderProjectileSpeed, ProjectileRadius = s.RaiderProjectileRadius, StructureMult = 1f,
                };
                LastProblem = "fg.TbSiegeUnit 缺 " + (def?.EnemyTypeId ?? "(无编成)");
            }
            int weapon = site.WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile,
                HasOutput = 1,
                TargetMode = CombatTargetMode.Nearest,
                Range = u.Range,
                Damage = u.Damage * dmgScale,
                Cooldown = u.Cooldown,
                ProjectileSpeed = u.ProjectileSpeed,
                ProjectileRadius = u.ProjectileRadius,
            });
            int profile = site.ProfileIndex(new CombatBehaviorProfile
            {
                Speed = u.Speed,
                SenseRange = u.Range,
                EffectAmount = u.HealAmount,
                EffectRange = u.HealRange,
                CycleSeconds = u.HealCooldown,
            });
            return site.SpawnSiegeRaider(key, at, target, u.Radius, u.Speed, u.Hp * hpScale, u.FrontalReduction, weapon, profile, role, elite, u.StructureMult);
        }

        /// <summary>离 <paramref name="from"/> 最近的敌方可走格（按切比雪夫环、固定顺序；找不到 = 原格）。</summary>
        public static GridCell NearestPassable(GridCell from, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        if (NavService.PassableNow(from.X + dx, from.Y + dy, NavConst.ClassHostile))
                        {
                            return new GridCell(from.X + dx, from.Y + dy);
                        }
                    }
                }
            }
            return from;
        }

        // ─────────────────────────────── 剧场 ───────────────────────────────

        private static readonly List<GridCell> FootScratch = new List<GridCell>(32);

        /// <summary>剧场覆盖 <paramref name="gather"/> 与目标 <paramref name="target"/> 周围的己方建筑（已开着时取并集），截到最大边长；写进内核与存档。</summary>
        public static void ExtendTheater(CampaignState state, CombatSite site, GridCell gather, GridCell target)
        {
            SiegeState st = StateOf(state);
            int minX = Math.Min(gather.X, target.X), minY = Math.Min(gather.Y, target.Y);
            int maxX = Math.Max(gather.X, target.X), maxY = Math.Max(gather.Y, target.Y);
            float r = SiegeCatalog.TheaterRadius;
            float r2 = r * r;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                float dx = b.Position.x - target.X;
                float dy = b.Position.y - target.Y;
                if (dx * dx + dy * dy > r2)
                {
                    continue;
                }
                FootScratch.Clear();
                HomeGridService.FootprintOf(b, FootScratch);
                foreach (GridCell c in FootScratch)
                {
                    minX = Math.Min(minX, c.X);
                    minY = Math.Min(minY, c.Y);
                    maxX = Math.Max(maxX, c.X);
                    maxY = Math.Max(maxY, c.Y);
                }
            }
            int m = SiegeCatalog.TheaterMargin;
            minX -= m;
            minY -= m;
            maxX += m;
            maxY += m;
            if (st.TheaterActive)
            {
                minX = Math.Min(minX, st.MinX);
                minY = Math.Min(minY, st.MinY);
                maxX = Math.Max(maxX, st.MaxX);
                maxY = Math.Max(maxY, st.MaxY);
            }
            int cap = SiegeCatalog.TheaterMaxCells;
            Clamp(ref minX, ref maxX, target.X, cap);
            Clamp(ref minY, ref maxY, target.Y, cap);
            st.TheaterActive = true;
            st.MinX = minX;
            st.MinY = minY;
            st.MaxX = maxX;
            st.MaxY = maxY;
            ApplyTheater(state, site);
        }

        private static void Clamp(ref int lo, ref int hi, int center, int cap)
        {
            if (hi - lo + 1 <= cap)
            {
                return;
            }
            int half = cap / 2;
            int nlo = Math.Max(lo, center - half);
            int nhi = nlo + cap - 1;
            if (nhi > hi)
            {
                nhi = hi;
                nlo = nhi - cap + 1;
            }
            lo = nlo;
            hi = nhi;
        }

        /// <summary>把存档里的剧场（矩形 / 职能表 / 破墙代价）写进内核（展开时、读档后第一步之前）。</summary>
        private static void ApplyTheater(CampaignState state, CombatSite site)
        {
            SiegeState st = StateOf(state);
            if (site == null || site.IsDisposed)
            {
                return;
            }
            site.SetSiegeRoles(SiegeCatalog.RoleMasks, SiegeCatalog.RoleBias);
            site.SetSiegeTheater(st.TheaterActive, new int2(st.MinX, st.MinY), new int2(st.MaxX, st.MaxY), SiegeCatalog.BreachBaseCost,
                SiegeCatalog.BreachCostPerBand, SiegeCatalog.BreachHpBand, SiegeCatalog.RetargetSeconds, SiegeCatalog.ExitRadius);
        }

        /// <summary>集结点（撤退流场的终点）= 正在攻城的队伍的集结点（按队伍顺序）。</summary>
        private static void ApplyExits(CampaignState state, CombatSite site)
        {
            ExitScratch.Clear();
            foreach (TransitGroupRecord g in SiegingGroups(state))
            {
                ExitScratch.Add(new int2(g.GatherX, g.GatherY));
            }
            site.SetSiegeExits(ExitScratch);
        }

        /// <summary>没有队伍在攻城了：建筑结构单位的耐久写回建筑、移出内核；炮塔 / 防御结构单位清掉攻城类别；关剧场；结束反应归因的突袭场次。</summary>
        private static void EndTheater(CampaignState state, CombatSite site)
        {
            SiegeState st = StateOf(state);
            WriteTo(state);
            foreach (SiegeStructureRecord r in st.Structures)
            {
                if (r != null)
                {
                    site.RemoveSiegeStructure(r.Serial);
                    Rt.Remove(r.Serial);
                }
            }
            st.Structures = Array.Empty<SiegeStructureRecord>();
            ClearDefenseTargets(state, site);
            st.TheaterActive = false;
            st.SiteDamage = Array.Empty<SiegeSiteDamageRecord>();
            st.NotifiedBreaches = Array.Empty<string>();
            site.SetSiegeExits(null);
            ApplyTheater(state, site);
            ReactionAttribution.EndRaid(state);
        }

        private static readonly List<int> OrphanScratch = new List<int>(8);

        /// <summary>本会话清掉的无主攻城单位队伍数（自检读）。</summary>
        public static int OrphansRemoved { get; private set; }

        /// <summary>内核里还有单位、但行进队伍已经不在（被别的流程移除）的攻城 / 拦截单位：一并移除，不留无主的敌人（它们没有队伍就永远不会撤退或收拢）。</summary>
        private static void DespawnOrphans(CampaignState state, CombatSite site)
        {
            if (site.CollectSiegeGroups(OrphanScratch) == 0)
            {
                return;
            }
            TransitGroupRecord[] groups = state.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>();
            foreach (int key in OrphanScratch)
            {
                bool owned = false;
                foreach (TransitGroupRecord g in groups)
                {
                    if (g != null && (g.Engaged || g.Intercepted) && KeyOf(g) == key)
                    {
                        owned = true;
                        break;
                    }
                }
                if (!owned)
                {
                    site.DespawnSiegeGroup(key);
                    OrphansRemoved++;
                }
            }
        }
        // ─────────────────────────────── 撤退、收拢、全歼 ───────────────────────────────

        private static readonly List<TransitGroupRecord> FinishScratch = new List<TransitGroupRecord>(4);

        private static void CheckGroups(CampaignState state, CombatSite site)
        {
            FinishScratch.Clear();
            long limit = WorldTransitSystem.TimeLimitTicks;
            float ratio = SiegeCatalog.LossRetreatRatio;
            foreach (TransitGroupRecord g in SiegingGroups(state))
            {
                int key = KeyOf(g);
                int alive = site.CountSiegeGroup(key, RoleScratch);
                int lost = Math.Max(0, g.UnfoldedCount - alive - g.ExitedCount);
                if (!g.SiegeRetreat && alive > 0)
                {
                    if (limit > 0 && g.ArrivedAtTick >= 0 && GameClock.Ticks - g.ArrivedAtTick >= limit)
                    {
                        OrderRetreat(state, site, g, ReasonTime, alive, lost);
                    }
                    else if (g.UnfoldedCount > 0 && lost * 1000L > (long)Mathf.RoundToInt(ratio * 1000f) * g.UnfoldedCount) // 整数比较（千分比），0.7f × 10 不会因浮点误差在恰好 70% 时触发
                    {
                        OrderRetreat(state, site, g, ReasonLosses, alive, lost);
                    }
                }
                else if (g.SiegeRetreat && alive > 0)
                {
                    alive -= RetreatFallback(state, site, g, alive);
                }
                if (alive == 0)
                {
                    FinishScratch.Add(g);
                }
            }
            foreach (TransitGroupRecord g in FinishScratch)
            {
                Finish(state, site, g);
            }
            if (FinishScratch.Count > 0)
            {
                ApplyExits(state, site);
                StandingRuleService.NotifyRaidArrived(state);
            }
        }

        /// <summary>FGR-DEF-032：全队改走撤退流场回集结点（重选目标立即生效）。</summary>
        public static void OrderRetreat(CampaignState state, CombatSite site, TransitGroupRecord g, string reason, int alive, int lost)
        {
            if (g == null || g.SiegeRetreat)
            {
                return;
            }
            site.SetSiegeGroupRetreat(KeyOf(g));
            g.SiegeRetreat = true;
            g.SiegeRetreatReason = reason ?? string.Empty;
            g.SiegeRetreatTick = GameClock.Ticks;
            RetreatOrders++;
            Hook(GuidanceHooks.SiegeFirstRetreat);
            int pct = g.UnfoldedCount > 0 ? Mathf.RoundToInt(100f * lost / g.UnfoldedCount) : 0;
            double stayed = g.ArrivedAtTick >= 0 ? Math.Max(0, GameClock.Ticks - g.ArrivedAtTick) / (double)GameClock.StepHz : 0;
            string text = reason == ReasonLosses
                ? GameText.Format("siege.notify.retreat_losses", g.UnfoldedCount, pct, alive)
                : GameText.Format("siege.notify.retreat_time", g.UnfoldedCount, GameClock.FormatGameDuration(stayed), alive);
            NotificationCenter.Post("raid_withdrawn", text, new Vector3(g.GatherX, 0f, g.GatherY));
        }

        /// <summary>
        /// 复审修复（P1，B11 软锁保底 / FGR-DEF-032）：撤退的兜底。正常情况下撤退单位沿撤退流场走回集结点，路被重新堵死时沿撤退破墙场拆出去（内核）；
        /// 只有连墙都拆不出去（例如被地形困住）时，下撤退令满 siege.retreat_max_seconds 后还在家园里的单位就地离场（算离场、不算阵亡），队伍随即并回行进队伍原路返回——
        /// 撤退一定能收尾，剧场 / 战时预案 / 反应归因场次不会因为几台卡住的敌人永远开着。返回离场的台数。
        /// </summary>
        private static int RetreatFallback(CampaignState state, CombatSite site, TransitGroupRecord g, int alive)
        {
            if (g.SiegeRetreatTick <= 0)
            {
                g.SiegeRetreatTick = GameClock.Ticks; // 旧档没有这一项：从现在起算
                return 0;
            }
            if (GameClock.Ticks - g.SiegeRetreatTick < GameClock.TicksFor(SiegeCatalog.RetreatMaxSeconds))
            {
                return 0;
            }
            site.DespawnSiegeGroup(KeyOf(g));
            g.ExitedCount += alive;
            StateOf(state).TotalExited += alive;
            RetreatTimeouts++;
            TEngine.Log.Info($"[SiegeService] 突袭 {g.GroupId} 撤退 {SiegeCatalog.RetreatMaxSeconds:F0} 游戏秒仍有 {alive} 台走不回集结点（被困住、拆不出去），按离场处理，第 {GameClock.Ticks} 步");
            return alive;
        }

        /// <summary>内核里这支队伍没有单位了：有离场的 → 并回行进队伍沿原路回据点；没有 → 被全歼。</summary>
        private static void Finish(CampaignState state, CombatSite site, TransitGroupRecord g)
        {
            if (g.ExitedCount > 0)
            {
                g.UnitCount = g.ExitedCount;
                g.PosX = g.GatherX;
                g.PosY = g.GatherY;
                Regroups++;
                string where = WorldTransitSystem.OriginName(g.OriginId);
                WorldTransitSystem.BeginRetreat(state, g, GameText.Format("siege.notify.regrouped", g.ExitedCount,
                    string.IsNullOrEmpty(where) ? GameText.Get("raid.withdrawn.home") : where));
                return;
            }
            RaidPlanRecord p = RaidDirectorService.FindPlan(state, g.PlanId);
            if (p != null)
            {
                p.EndReason = RaidDirectorService.EndDestroyed;
            }
            Annihilations++;
            Hook(GuidanceHooks.SiegeFirstDestroyed);
            NotificationCenter.Post("raid_destroyed", GameText.Format("siege.notify.destroyed", g.UnfoldedCount), new Vector3(g.GatherX, 0f, g.GatherY));
            WorldTransitSystem.RemoveDestroyed(state, g);
        }

        // ─────────────────────────────── 内核事件 ───────────────────────────────

        /// <summary>攻城单位走到集结点离场：记一台（全部离场 / 阵亡后在对账里收拢）。</summary>
        private static void OnUnitExited(CombatSite site, int groupKey, CombatEvent e)
        {
            CampaignState state = CampaignSession.Current;
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (g != null && KeyOf(g) == groupKey)
                {
                    g.ExitedCount++;
                    StateOf(state).TotalExited++;
                    return;
                }
            }
        }

        // ─────────────────────────────── 存档与读档 ───────────────────────────────

        /// <summary>家园战斗内核刚建好 / 从快照恢复之后（第一步之前）：绑定事件，按存档重设剧场、职能表、集结点（内核快照不存这些配置）。</summary>
        public static void RestoreAfterLoad(CampaignState state, CombatSite site)
        {
            EnsureState(state);
            BindEvents();
            SiegeState st = StateOf(state);
            if (site == null || site.IsDisposed)
            {
                return;
            }
            ApplyTheater(state, site);
            ApplyExits(state, site);
            if (!st.TheaterActive)
            {
                return;
            }
            // 读档时内核快照里没有的建筑结构单位（例如旧快照）：下一次对账补上；记录在案、内核里没有的单位不在这里补，免得改变读档那一刻的内核状态。
        }

        /// <summary>存档前：建筑结构单位在内核里的耐久写回建筑（单位本身随内核快照）。</summary>
        public static void WriteTo(CampaignState state)
        {
            CombatSite site = HomeSite;
            SiegeState st = StateOf(state);
            if (state == null || site == null || site.IsDisposed || st == null)
            {
                return;
            }
            foreach (SiegeStructureRecord r in st.Structures)
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                if (b == null)
                {
                    continue;
                }
                if (site.TryGetSiegeStructHealth(r.Serial, out float hp, out _, out bool alive) && alive && IsBuilt(b))
                {
                    float write = b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && hp <= CombatSiegeConst.HealthFloor + 1e-6f ? BuildingOps.CoreFloorHealth : hp;
                    if (Mathf.Abs(BuildingOps.Durability(b) - write) > 0.01f)
                    {
                        b.Health = write; // 有效耐久没变（记录里存着超过上限的旧值也算满）时不改记录
                    }
                    if (Rt.TryGetValue(r.Serial, out StructRt rt))
                    {
                        rt.LastPushedHp = b.Health;
                    }
                }
            }
        }

        /// <summary>自检 / 确定性对照：攻城域与正在攻城的队伍的规范化快照。</summary>
        public static string Snapshot(CampaignState state)
        {
            SiegeState st = StateOf(state);
            if (st == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(256);
            sb.Append(st.TheaterActive ? 'T' : 'F').Append(st.MinX).Append(',').Append(st.MinY).Append(',').Append(st.MaxX).Append(',').Append(st.MaxY)
              .Append('|').Append(st.NextSerial).Append('|').Append(st.TotalUnfolded).Append('|').Append(st.TotalExited).Append('|').Append(st.TotalDestroyedBuildings);
            foreach (SiegeStructureRecord r in st.Structures)
            {
                sb.Append("|S").Append(r.Serial).Append(':').Append(r.BuildingId);
            }
            foreach (SiegeSiteDamageRecord r in st.SiteDamage)
            {
                sb.Append("|D").Append(r.BuildingId).Append(':').Append(r.Damage.ToString("R", CultureInfo.InvariantCulture));
            }
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (g == null)
                {
                    continue;
                }
                sb.Append("|G").Append(g.GroupId).Append(':').Append((int)g.State).Append(g.Engaged ? 'E' : '-').Append(g.Intercepted ? 'I' : '-').Append(g.SiegeRetreat ? 'R' : '-')
                  .Append(g.UnfoldedCount).Append('/').Append(g.ExitedCount).Append('/').Append(g.UnitCount).Append('@').Append(g.GatherX).Append(',').Append(g.GatherY)
                  .Append(':').Append(g.SiegeRetreatReason).Append('#').Append(g.SiegeRetreatTick);
            }
            return sb.ToString();
        }

        /// <summary>状态行（突袭 HUD / 自检）：正在攻城的总台数、各职能、已损失比例。</summary>
        public static string StatusLine(CampaignState state)
        {
            CombatSite site = HomeSite;
            if (site == null)
            {
                return string.Empty;
            }
            int total = 0, a = 0, b = 0, c = 0, unfolded = 0, lost = 0;
            foreach (TransitGroupRecord g in SiegingGroups(state))
            {
                int alive = site.CountSiegeGroup(KeyOf(g), RoleScratch);
                total += alive;
                a += RoleScratch[1];
                b += RoleScratch[2];
                c += RoleScratch[3];
                unfolded += g.UnfoldedCount;
                lost += Math.Max(0, g.UnfoldedCount - alive - g.ExitedCount);
            }
            if (unfolded == 0)
            {
                return string.Empty;
            }
            return GameText.Format("siege.status.active", total, a, b, c, Mathf.RoundToInt(100f * lost / unfolded));
        }

        private static bool IsBuilt(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                      && !HomeGridService.IsRelocationGhost(b);
    }
}
