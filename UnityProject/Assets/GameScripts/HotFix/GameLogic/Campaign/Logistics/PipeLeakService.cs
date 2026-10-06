using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG6-LOG-10（FG03 FGR-LOG-046 破损泄漏）：管线被击穿后的泄漏液洼——唯一业务入口（热更层）。
    ///
    /// - <b>击穿</b>：管线件耐久掉到最大耐久 × logistics.leak.breach_hp_ratio 及以下或被摧毁时（<see cref="PipeNetworkService.TryDamage"/> 回调 <see cref="OnPipeHit"/>），
    ///   破口所在格积起这一格流体的液洼（一格最多一摊，家园上限 logistics.leak.max_puddles）。液洼挂什么标签由 fg.TbFluidLeak 定（燃油 / 原油 → 油污，冷却液 / 水 → 浸湿，酸液 → 腐蚀）。
    /// - <b>液洼 = 战斗内核里的中立区域</b>（<see cref="CombatSite.UpsertLeak"/>）：站进去的己方机器与敌人按节拍挂上标签，挨打时照常触发 FG2-FW-03 的标签反应；
    ///   液洼本身遇到能和它起反应、会留下残留区域的标签（燃油遇火 → 爆燃的燃烧区）时整片反应——逐单位 / 逐区域的判定在 Main/Sim（Burst），这里每步只取走反应记录。
    /// - <b>连锁烧毁产线</b>（有意设计的涌现玩法，卡片负向）：带持续伤害的残留区域每 logistics.leak.sync_seconds 按 logistics.leak.fire_dps 烧坏范围里的管线、传送带与建筑，
    ///   被烧穿的燃油管线再泄漏、再被点燃；起火时发“泄漏起火”告警（可点击定位），悬停与图鉴写明这是有意设计。
    /// - <b>消退</b>：修好管线（耐久回到阈值以上 / 按原设置重建）、冲洗、拆掉虚影或破口两侧没有同种流体后，液洼在 logistics.leak.fade_seconds 内逐渐缩小消失；
    ///   反应完（残留到期）后破口还在就重新积起液洼。
    /// 推进只看步序号（暂停不走、倍速只是一帧多走几步、与观察无关，FGR-BASE-021）；热更层每 logistics.leak.sync_seconds O(液洼数 × 半径内格数)，液洼数有上限。
    /// </summary>
    public static class PipeLeakService
    {
        public const int StateLeaking = 0;
        public const int StateFading = 1;
        public const int StateReacted = 2;

        private static readonly Dictionary<int, FluidLeak> FluidRows = new Dictionary<int, FluidLeak>();
        private static readonly Dictionary<GridCell, PipeLeakRecord> ByCell = new Dictionary<GridCell, PipeLeakRecord>();
        private static readonly List<int2> ReactScratch = new List<int2>(8);
        private static readonly HashSet<string> BurnedBuildings = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> ProblemList = new List<string>();
        private static bool _loaded;
        private static CampaignState _indexed;
        private static PipeLeakRecord[] _indexedArray;
        private static bool _reconciled;

        // ── 本会话统计（自检 / 性能证据）──
        public static int StartedCount { get; private set; }
        public static int ReactedCount { get; private set; }
        public static int FadedCount { get; private set; }
        public static int RefusedCount { get; private set; }
        public static int FireHits { get; private set; }
        public static int FireWarnings { get; private set; }
        public static int SyncCount { get; private set; }
        public static double LastSyncMs { get; private set; }
        public static double MaxSyncMs { get; private set; }
        public static int OrphansRemoved { get; private set; }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        // ── 调参（fg.TbHomeTuning logistics.leak.*）──
        public static float BreachRatio => Mathf.Clamp(GridContent.Tuning("logistics.leak.breach_hp_ratio"), 0.01f, 0.99f);
        public static float BaseRadius => Math.Max(0.1f, GridContent.Tuning("logistics.leak.base_radius"));
        public static float MaxRadius => Math.Max(BaseRadius, GridContent.Tuning("logistics.leak.max_radius"));
        public static float GrowSeconds => Math.Max(0.1f, GridContent.Tuning("logistics.leak.grow_seconds"));
        public static float FadeSeconds => Math.Max(0.1f, GridContent.Tuning("logistics.leak.fade_seconds"));
        public static float FireDps => Math.Max(0f, GridContent.Tuning("logistics.leak.fire_dps"));
        public static float FireTickSeconds => Math.Max(0.05f, GridContent.Tuning("logistics.leak.fire_tick_seconds"));
        public static float SyncSeconds => Math.Max(0.05f, GridContent.Tuning("logistics.leak.sync_seconds"));
        public static int MaxPuddles => Math.Max(1, GridContent.TuningInt("logistics.leak.max_puddles"));
        public static int WarnMarginCells => Math.Max(0, GridContent.TuningInt("logistics.leak.warn_margin_cells"));

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        // ─────────────────────────────── 表 ───────────────────────────────

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
            FluidRows.Clear();
            ProblemList.Clear();
            TbFluidLeak table = null;
            try
            {
                table = ConfigSystem.Instance.Tables?.TbFluidLeak;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbFluidLeak 失败：" + e.Message);
            }
            if (table == null)
            {
                ProblemList.Add("fg.TbFluidLeak 不存在（改 tools/cell_tables/fgdata_leak.py 后重新生成）");
                return;
            }
            foreach (FluidLeak row in table.DataList)
            {
                if (row == null)
                {
                    continue;
                }
                if (NamedReactionCatalog.BitOf(row.Tag) == 0u)
                {
                    ProblemList.Add($"fg.TbFluidLeak 流体 {row.Fluid} 的标签 {row.Tag} 在 fg.TbStatusTag 里没有内核位，这种液洼不会挂标签");
                }
                FluidRows[row.Fluid] = row;
            }
            foreach (string p in ProblemList)
            {
                Log.Error("[PipeLeakService] " + p);
            }
        }

        /// <summary>这种流体漏出来的液洼挂的状态标签（fg.TbStatusTag 主标签 ID；没有行 = null）。</summary>
        public static string TagOf(int fluid)
        {
            EnsureLoaded();
            return FluidRows.TryGetValue(fluid, out FluidLeak row) ? row.Tag : null;
        }

        public static uint MaskOf(int fluid) => NamedReactionCatalog.BitOf(TagOf(fluid));

        private static float ScaleOf(int fluid)
        {
            EnsureLoaded();
            return FluidRows.TryGetValue(fluid, out FluidLeak row) ? Mathf.Clamp(row.RadiusScale, 0.2f, 4f) : 1f;
        }

        // ─────────────────────────────── 状态 ───────────────────────────────

        /// <summary>存档域补齐（旧档没有 = 没有液洼）；坏记录（编号 ≤ 0、重复编号或同一格两摊、没有流体）丢弃。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Pipes == null)
            {
                return;
            }
            PipeFluidState p = s.Pipes;
            p.Leaks ??= Array.Empty<PipeLeakRecord>();
            if (p.NextLeakSerial < 1)
            {
                p.NextLeakSerial = 1;
            }
            var ids = new HashSet<int>();
            var cells = new HashSet<GridCell>();
            List<PipeLeakRecord> keep = null;
            for (int i = 0; i < p.Leaks.Length; i++)
            {
                PipeLeakRecord r = p.Leaks[i];
                bool ok = r != null && r.Id > 0 && r.Fluid > 0 && r.State >= StateLeaking && r.State <= StateReacted
                          && !float.IsNaN(r.FromRadius) && ids.Add(r.Id) && cells.Add(new GridCell(r.X, r.Y));
                if (!ok && keep == null)
                {
                    keep = new List<PipeLeakRecord>(p.Leaks.Length);
                    for (int j = 0; j < i; j++)
                    {
                        keep.Add(p.Leaks[j]);
                    }
                }
                if (ok)
                {
                    keep?.Add(r);
                    if (r.Id >= p.NextLeakSerial)
                    {
                        p.NextLeakSerial = r.Id + 1;
                    }
                }
            }
            if (keep != null)
            {
                p.Leaks = keep.ToArray();
            }
        }

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时索引与统计清零（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            ByCell.Clear();
            _indexed = null;
            _indexedArray = null;
            _reconciled = false;
            StartedCount = 0;
            ReactedCount = 0;
            FadedCount = 0;
            RefusedCount = 0;
            FireHits = 0;
            FireWarnings = 0;
            SyncCount = 0;
            LastSyncMs = 0;
            MaxSyncMs = 0;
            OrphansRemoved = 0;
            BurnedBuildings.Clear();
        }

        public static PipeLeakRecord[] Records(CampaignState state) => state?.Pipes?.Leaks ?? Array.Empty<PipeLeakRecord>();

        public static int Count(CampaignState state) => Records(state).Length;

        /// <summary>这一格的液洼（破口所在格；没有 = null）。O(1)。</summary>
        public static PipeLeakRecord At(CampaignState state, GridCell cell)
        {
            Index(state);
            return ByCell.TryGetValue(cell, out PipeLeakRecord r) ? r : null;
        }

        public static PipeLeakRecord Find(CampaignState state, int id)
        {
            foreach (PipeLeakRecord r in Records(state))
            {
                if (r != null && r.Id == id)
                {
                    return r;
                }
            }
            return null;
        }

        private static void Index(CampaignState state)
        {
            PipeLeakRecord[] arr = Records(state);
            if (ReferenceEquals(state, _indexed) && ReferenceEquals(arr, _indexedArray))
            {
                return;
            }
            ByCell.Clear();
            foreach (PipeLeakRecord r in arr)
            {
                if (r != null)
                {
                    ByCell[new GridCell(r.X, r.Y)] = r;
                }
            }
            _indexed = state;
            _indexedArray = arr;
        }

        private static void Add(CampaignState state, PipeLeakRecord r)
        {
            PipeFluidState p = state.Pipes;
            var arr = new PipeLeakRecord[p.Leaks.Length + 1];
            Array.Copy(p.Leaks, arr, p.Leaks.Length);
            arr[arr.Length - 1] = r;
            p.Leaks = arr;
        }

        private static void Remove(CampaignState state, PipeLeakRecord r)
        {
            PipeFluidState p = state.Pipes;
            var list = new List<PipeLeakRecord>(p.Leaks.Length);
            foreach (PipeLeakRecord x in p.Leaks)
            {
                if (!ReferenceEquals(x, r))
                {
                    list.Add(x);
                }
            }
            p.Leaks = list.ToArray();
            HomeSite?.RemoveLeak(r.Id);
        }

        // ─────────────────────────────── 半径 ───────────────────────────────

        /// <summary>液洼此刻的半径（米）：还在漏 = 初始 + (最大 − 初始) × 已漏时长 / 扩散时长；消退 = 开始消退时的半径 × (1 − 已消退时长 / 消退时长)；
        /// 已反应 = 内核里残留区域的半径（没有内核时取开始反应时的半径）。只由步序号决定。</summary>
        public static float RadiusOf(PipeLeakRecord r, long now)
        {
            if (r == null)
            {
                return 0f;
            }
            float scale = ScaleOf(r.Fluid);
            switch (r.State)
            {
                case StateLeaking:
                {
                    double t = Math.Max(0, now - r.GrowTick) / (double)Math.Max(1, GameClock.StepHz);
                    float f = (float)Math.Min(1.0, t / GrowSeconds);
                    return (BaseRadius + (MaxRadius - BaseRadius) * f) * scale;
                }
                case StateFading:
                {
                    double t = Math.Max(0, now - r.StateTick) / (double)Math.Max(1, GameClock.StepHz);
                    return Math.Max(0f, r.FromRadius * (1f - (float)Math.Min(1.0, t / FadeSeconds)));
                }
                default:
                {
                    CombatSite site = HomeSite;
                    return site != null && site.TryGetLeak(r.Id, out CombatZone z) ? z.Radius : r.FromRadius;
                }
            }
        }

        /// <summary>还在漏时，倒推“开始扩大的步”使得此刻半径 = <paramref name="radius"/>（消退中又被打穿：从当前大小接着扩大，不跳变）。</summary>
        private static long GrowTickFor(float radius, int fluid, long now)
        {
            float scale = ScaleOf(fluid);
            float span = (MaxRadius - BaseRadius) * scale;
            float f = span <= 1e-4f ? 1f : Mathf.Clamp01((radius - BaseRadius * scale) / span);
            return now - GameClock.TicksFor(f * GrowSeconds);
        }

        // ─────────────────────────────── 击穿 ───────────────────────────────

        /// <summary>
        /// 一格管线件挨打之后（<see cref="PipeNetworkService.TryDamage"/> 调用）：耐久到击穿阈值及以下或被摧毁、且这一格有流体时，破口处积起液洼——
        /// 新的一摊（家园上限内）、或让正在消退的那摊重新扩大；已经反应成残留区域的不动（残留到期后若破口还在会重新积起）。返回是否新积起 / 重新扩大了液洼。
        /// </summary>
        public static bool OnPipeHit(CampaignState state, GridCell cell, int fluid, int hpAfter, int maxHp, bool destroyed)
        {
            if (state?.Pipes == null || fluid <= 0 || maxHp <= 0 || MaskOf(fluid) == 0u)
            {
                return false;
            }
            if (!destroyed && hpAfter > maxHp * BreachRatio)
            {
                return false;
            }
            EnsureState(state);
            long now = GameClock.Ticks;
            PipeLeakRecord r = At(state, cell);
            if (r != null)
            {
                if (r.State == StateFading && r.Fluid == fluid)
                {
                    r.GrowTick = GrowTickFor(RadiusOf(r, now), fluid, now);
                    r.State = StateLeaking;
                    r.StateTick = now;
                    Push(r, now);
                    return true;
                }
                return false;
            }
            if (Count(state) >= MaxPuddles)
            {
                RefusedCount++;
                if (RefusedCount == 1)
                {
                    Log.Warning("[PipeLeakService] " + GameText.Format("leak.reason.cap", MaxPuddles));
                }
                return false;
            }
            PipeFluidState p = state.Pipes;
            r = new PipeLeakRecord
            {
                Id = p.NextLeakSerial++,
                X = cell.X,
                Y = cell.Y,
                Fluid = fluid,
                State = StateLeaking,
                GrowTick = now,
                StateTick = now,
            };
            Add(state, r);
            StartedCount++;
            Push(r, now);
            GuidanceHooks.Raise(GuidanceHooks.LogisticsLeakFirst);
            string fluidName = PipeNetworkService.FluidName(fluid);
            NotificationCenter.Post("pipe_leak", GameText.Format("leak.notify.started", cell.X, cell.Y, fluidName, TagLabel(fluid)), new Vector3(cell.X, 0f, cell.Y));
            return true;
        }

        /// <summary>破口还在不在漏：这一格有管线件 = 耐久仍在击穿阈值及以下、且这一格还是这种流体；没有管线件 = 这一格有待重建的管线虚影、且相邻有这种流体的管线件
        /// （拆掉虚影 = 不打算修，两侧的管线照常封口）。O(1)。</summary>
        public static bool IsBreached(CampaignState state, PipeLeakRecord r)
        {
            if (r == null || !PipeNetworkService.IsRunning)
            {
                return false;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            if (k.TryGetCellInfo(r.X, r.Y, out PipeCellInfo info))
            {
                int max = PipeNetworkService.MaxHp(info.Kind, info.Tier);
                int hp = PipeNetworkService.HpOf(new GridCell(r.X, r.Y));
                return info.Fluid == r.Fluid && hp >= 0 && hp <= max * BreachRatio;
            }
            HomeGridMap map = HomeGridService.BoundMap(state);
            if (map == null || !HomeValleyConstruction.IsPlannedMarker(map.GetPipe(new GridCell(r.X, r.Y))))
            {
                return false;
            }
            return NeighbourHas(k, r.X + 1, r.Y, r.Fluid) || NeighbourHas(k, r.X - 1, r.Y, r.Fluid)
                   || NeighbourHas(k, r.X, r.Y + 1, r.Fluid) || NeighbourHas(k, r.X, r.Y - 1, r.Fluid);
        }

        private static bool NeighbourHas(PipeKernel k, int x, int y, int fluid) => k.TryGetCellInfo(x, y, out PipeCellInfo n) && n.Fluid == fluid;

        /// <summary>把液洼记录推给内核（还没反应的那块：位置 / 半径 / 标签）。已反应的内核区域不动。</summary>
        private static void Push(PipeLeakRecord r, long now)
        {
            CombatSite site = HomeSite;
            if (site == null || r.State == StateReacted)
            {
                return;
            }
            float radius = RadiusOf(r, now);
            if (radius <= 0.01f)
            {
                return;
            }
            if (!site.UpsertLeak(r.Id, new Vector2(r.X, r.Y), radius, MaskOf(r.Fluid)) && site.TryGetLeak(r.Id, out CombatZone z) && z.Phase == CombatConst.LeakPhaseReacted)
            {
                // 内核里这一摊已经整片反应了、但本步的反应记录还没取走（例如读档后的第一步）：按内核补记反应。
                MarkReacted(r, RuleOfZone(r.Fluid, z), z, now, notify: false);
            }
        }

        /// <summary>内核区域不记反应规则：按“配料含这种液洼的标签、残留标签 = 区域此刻的标签”在反应表里找回规则下标（读档对账用；找不到 = -1）。</summary>
        private static int RuleOfZone(int fluid, in CombatZone z)
        {
            uint bit = MaskOf(fluid);
            IReadOnlyList<Reaction> rules = NamedReactionCatalog.TagRules;
            for (int i = 0; i < rules.Count; i++)
            {
                Reaction row = rules[i];
                if (row != null && NamedReactionCatalog.BitOf(row.Residue) == z.StatusMask
                    && (NamedReactionCatalog.BitOf(row.TagA) == bit || NamedReactionCatalog.BitOf(row.TagB) == bit))
                {
                    return i;
                }
            }
            return -1;
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>
        /// 世界模拟的每个固定步（家园载入时，在攻城之后）：取走内核本步的液洼反应记录（发告警、钩子）；每 logistics.leak.sync_seconds 对账一次
        /// （击穿是否还在 → 扩大 / 消退 / 消失；残留到期；火烧坏范围里的设施）。暂停不走，与观察无关。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state?.Pipes == null || worldHz <= 0)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (!_reconciled && site != null && !site.IsDisposed)
            {
                Reconcile(state, site, ticksBefore);
            }
            if (site != null && !site.IsDisposed && site.DrainLeakReactions(ReactScratch) > 0)
            {
                foreach (int2 rx in ReactScratch)
                {
                    PipeLeakRecord r = Find(state, rx.x);
                    if (r != null && r.State != StateReacted && site.TryGetLeak(r.Id, out CombatZone z))
                    {
                        MarkReacted(r, rx.y, z, ticksBefore, notify: true);
                    }
                }
            }
            if (Count(state) == 0)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(SyncSeconds * worldHz));
            if (ticksBefore % every == 0)
            {
                Sync(state, ticksBefore);
            }
        }

        /// <summary>读档 / 新会话后第一次有家园战斗地点时：内核里没有记录的液洼区域移除；记录里有、内核里没有的（旧快照写的是不认识液洼的格式）重新登记；
        /// 内核里已反应、记录还没记的按内核补记。O(区域数 + 液洼数)，每会话一次。</summary>
        private static void Reconcile(CampaignState state, CombatSite site, long now)
        {
            _reconciled = true;
            var ids = new HashSet<int>();
            foreach (PipeLeakRecord r in Records(state))
            {
                if (r != null)
                {
                    ids.Add(r.Id);
                }
            }
            var orphans = new List<int>();
            site.LeakIds(orphans);
            orphans.RemoveAll(ids.Contains);
            foreach (int id in orphans)
            {
                OrphansRemoved += site.RemoveLeak(id);
            }
            foreach (PipeLeakRecord r in Records(state))
            {
                if (r == null)
                {
                    continue;
                }
                bool has = site.TryGetLeak(r.Id, out CombatZone z);
                if (has && z.Phase == CombatConst.LeakPhaseReacted && r.State != StateReacted)
                {
                    MarkReacted(r, RuleOfZone(r.Fluid, z), z, now, notify: false);
                }
                else if (!has && r.State != StateReacted)
                {
                    Push(r, now);
                }
            }
        }

        private static void MarkReacted(PipeLeakRecord r, int rule, in CombatZone z, long now, bool notify)
        {
            r.FromRadius = z.Radius;
            r.State = StateReacted;
            r.StateTick = now;
            r.Rule = rule;
            r.ReactUntilTick = now + Math.Max(1, GameClock.TicksFor(Math.Max(0.0, z.Until - z.Born)));
            r.Damaging = z.StatusDps > 0f;
            ReactedCount++;
            GuidanceHooks.Raise(GuidanceHooks.LogisticsLeakFirstReact);
            if (!notify)
            {
                return;
            }
            var cell = new GridCell(r.X, r.Y);
            string fluidName = PipeNetworkService.FluidName(r.Fluid);
            string what = ResidueName(rule);
            bool near = r.Damaging && OwnStructuresNear(cell, z.Radius + WarnMarginCells);
            if (near)
            {
                FireWarnings++;
            }
            NotificationCenter.Post("leak_fire", GameText.Format(near ? "leak.notify.react" : "leak.notify.react_far", cell.X, cell.Y, fluidName, what),
                new Vector3(cell.X, 0f, cell.Y));
        }

        /// <summary>反应留下的残留区域名（“燃烧区”）；表里没有残留名时用反应名；都查不到用通用说法。</summary>
        public static string ResidueName(int rule)
        {
            string id = rule >= 0 ? NamedReactionCatalog.IdOfRule(rule) : null;
            if (id != null && NamedReactionCatalog.TryGet(id, out Reaction row))
            {
                if (!string.IsNullOrEmpty(row.ResidueKey) && row.ResidueKey != "none")
                {
                    return GameText.Get(row.ResidueKey);
                }
                return GameText.Get(row.NameKey);
            }
            return GameText.Get("leak.residue.unknown");
        }

        /// <summary>对账：每摊液洼 O(1)（+ 已反应且带持续伤害的：半径内格数）。</summary>
        public static void Sync(CampaignState state, long now)
        {
            long t0 = Stopwatch.GetTimestamp();
            SyncCount++;
            PipeLeakRecord[] arr = Records(state);
            BurnedBuildings.Clear();
            // 火烧设施每 logistics.leak.fire_tick_seconds 结算一次、按液洼编号错开到不同的对账上（每次对账只处理一部分燃烧区；只看步序号，确定性）。
            long everyTicks = Math.Max(1, GameClock.TicksFor(SyncSeconds));
            int burnEvery = Math.Max(1, (int)Math.Round(FireTickSeconds / SyncSeconds));
            long syncIndex = now / everyTicks;
            for (int i = 0; i < arr.Length; i++)
            {
                PipeLeakRecord r = arr[i];
                if (r == null)
                {
                    continue;
                }
                switch (r.State)
                {
                    case StateLeaking:
                        if (!IsBreached(state, r))
                        {
                            r.FromRadius = RadiusOf(r, now);
                            r.State = StateFading;
                            r.StateTick = now;
                        }
                        Push(r, now);
                        break;
                    case StateFading:
                        if (IsBreached(state, r))
                        {
                            r.GrowTick = GrowTickFor(RadiusOf(r, now), r.Fluid, now);
                            r.State = StateLeaking;
                            r.StateTick = now;
                            Push(r, now);
                        }
                        else if (RadiusOf(r, now) <= 0.01f)
                        {
                            Remove(state, r);
                            FadedCount++;
                            GuidanceHooks.Raise(GuidanceHooks.LogisticsLeakFirstFaded);
                        }
                        else
                        {
                            Push(r, now);
                        }
                        break;
                    default:
                        if (now >= r.ReactUntilTick)
                        {
                            HomeSite?.RemoveLeak(r.Id);
                            if (IsBreached(state, r))
                            {
                                // 烧完了破口还在：重新积起一摊（从初始大小开始）。
                                r.State = StateLeaking;
                                r.GrowTick = now;
                                r.StateTick = now;
                                r.Rule = -1;
                                r.Damaging = false;
                                Push(r, now);
                            }
                            else
                            {
                                Remove(state, r);
                            }
                        }
                        else if (r.Damaging && (syncIndex + r.Id) % burnEvery == 0)
                        {
                            Burn(state, r, now, burnEvery * SyncSeconds);
                        }
                        break;
                }
            }
            LastSyncMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            MaxSyncMs = Math.Max(MaxSyncMs, LastSyncMs);
        }

        /// <summary>带持续伤害的残留区域（燃烧区）烧坏范围里的管线、传送带与建筑：每 logistics.leak.fire_tick_seconds 扣 logistics.leak.fire_dps × 这段时长。被烧穿的管线经
        /// <see cref="PipeNetworkService.TryDamage"/> 回到 <see cref="OnPipeHit"/>：再泄漏、再被点燃（连锁）。建筑经 <see cref="BuildingOps.ApplyDamage"/>（内核里的结构单位也从这里扣，内核区域不重复结算）。</summary>
        private static void Burn(CampaignState state, PipeLeakRecord r, long now, float seconds)
        {
            float dmg = FireDps * seconds;
            if (dmg <= 0f)
            {
                return;
            }
            int amount = Math.Max(1, Mathf.RoundToInt(dmg));
            float radius = RadiusOf(r, now);
            int reach = Mathf.CeilToInt(radius);
            float r2 = radius * radius;
            HomeGridMap map = HomeGridService.BoundMap(state);
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (dx * dx + dy * dy > r2)
                    {
                        continue;
                    }
                    var cell = new GridCell(r.X + dx, r.Y + dy);
                    if (PipeNetworkService.HpOf(cell) >= 0)
                    {
                        PipeNetworkService.TryDamage(state, cell, amount, out _);
                        FireHits++;
                    }
                    if (BeltNetworkService.HpOf(cell) >= 0)
                    {
                        BeltNetworkService.TryDamage(state, cell, amount, out _);
                        FireHits++;
                    }
                    string occ = map?.OccupantAt(cell);
                    if (!string.IsNullOrEmpty(occ) && BurnedBuildings.Add(occ))
                    {
                        BuildingRecord b = HomeGridService.FindBuilding(state, occ);
                        if (b != null && !HomeValleyController.IsPlannedGhost(b))
                        {
                            BuildingOps.ApplyDamage(state, occ, dmg);
                            FireHits++;
                        }
                    }
                }
            }
        }

        /// <summary>这一圈里有没有己方管线、传送带或建成的建筑（泄漏起火告警用）。</summary>
        public static bool OwnStructuresNear(GridCell center, float radius)
        {
            CampaignState state = PipeNetworkService.BoundState;
            HomeGridMap map = state != null ? HomeGridService.BoundMap(state) : null;
            int reach = Mathf.CeilToInt(radius);
            float r2 = radius * radius;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (dx * dx + dy * dy > r2)
                    {
                        continue;
                    }
                    var cell = new GridCell(center.X + dx, center.Y + dy);
                    if (PipeNetworkService.HpOf(cell) >= 0 || BeltNetworkService.HpOf(cell) >= 0 || !string.IsNullOrEmpty(map?.OccupantAt(cell)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ─────────────────────────────── 读数 ───────────────────────────────

        /// <summary>标签的显示：形状 + 名称（“● 油污”；B15 形状为主）。</summary>
        public static string TagLabel(int fluid)
        {
            string tag = TagOf(fluid);
            if (tag == null)
            {
                return string.Empty;
            }
            string shape = StatusTagCatalog.ShapeOf(tag);
            string name = StatusTagCatalog.NameOf(tag);
            return string.IsNullOrEmpty(shape) || shape == "none" ? name : shape + " " + name;
        }

        /// <summary>光标所在的世界点落在哪一摊液洼里（取最近的那摊；没有 = null）。O(液洼数)，只在悬停时。</summary>
        public static PipeLeakRecord AtPoint(CampaignState state, Vector2 xz)
        {
            long now = GameClock.Ticks;
            PipeLeakRecord best = null;
            float bestD = float.MaxValue;
            foreach (PipeLeakRecord r in Records(state))
            {
                if (r == null)
                {
                    continue;
                }
                float d = Vector2.Distance(xz, new Vector2(r.X, r.Y));
                if (d <= RadiusOf(r, now) && d < bestD)
                {
                    best = r;
                    bestD = d;
                }
            }
            return best;
        }

        private static readonly StringBuilder HoverSb = new StringBuilder(256);

        /// <summary>悬停读数：标题 = “燃油液洼（x, y）”；正文 = 标签与半径、阶段（还在漏 / 消退 / 已反应与剩余时间）、有意设计的提示、占位标记。</summary>
        public static bool TryDescribe(CampaignState state, PipeLeakRecord r, out string title, out string body)
        {
            title = null;
            body = null;
            if (r == null)
            {
                return false;
            }
            long now = GameClock.Ticks;
            string fluidName = PipeNetworkService.FluidName(r.Fluid);
            title = GameText.Format("leak.hover.title", fluidName, r.X, r.Y);
            StringBuilder sb = HoverSb;
            sb.Clear();
            AppendLines(sb, state, r, now);
            body = sb.ToString();
            return true;
        }

        private static void AppendLines(StringBuilder sb, CampaignState state, PipeLeakRecord r, long now)
        {
            float radius = RadiusOf(r, now);
            string rad = radius.ToString("0.0", CultureInfo.InvariantCulture);
            string tag = TagOf(r.Fluid);
            string shape = tag != null ? StatusTagCatalog.ShapeOf(tag) : string.Empty;
            string name = tag != null ? StatusTagCatalog.NameOf(tag) : string.Empty;
            switch (r.State)
            {
                case StateLeaking:
                    sb.Append(GameText.Format("leak.hover.tag", shape == "none" ? string.Empty : shape, name, rad)).Append('\n');
                    sb.Append(GameText.Format("leak.hover.leaking", r.X, r.Y, Mathf.RoundToInt(BreachRatio * 100f), Mathf.CeilToInt(FadeSeconds)));
                    break;
                case StateFading:
                {
                    sb.Append(GameText.Format("leak.hover.tag", shape == "none" ? string.Empty : shape, name, rad)).Append('\n');
                    double left = FadeSeconds - Math.Max(0, now - r.StateTick) / (double)Math.Max(1, GameClock.StepHz);
                    sb.Append(GameText.Format("leak.hover.fading", Math.Max(0, (int)Math.Ceiling(left))));
                    break;
                }
                default:
                {
                    int left = (int)Math.Ceiling(GameClock.SecondsUntil(r.ReactUntilTick));
                    sb.Append(GameText.Format(r.Damaging ? "leak.hover.reacted" : "leak.hover.reacted_safe", ResidueName(r.Rule), Math.Max(0, left)));
                    break;
                }
            }
            sb.Append('\n').Append(GameText.Get(CanReactAsWhole(r.Fluid) ? "leak.hover.design" : "leak.hover.react_none"));
            sb.Append('\n').Append(GameText.Get("leak.hover.placeholder"));
        }

        /// <summary>这种液洼会不会整片反应：已登记的反应规则里有没有“配料含它的标签、会留下残留区域”的。</summary>
        public static bool CanReactAsWhole(int fluid)
        {
            uint bit = MaskOf(fluid);
            if (bit == 0u)
            {
                return false;
            }
            foreach (Reaction row in NamedReactionCatalog.TagRules)
            {
                if (row == null || string.IsNullOrEmpty(row.Residue) || row.Residue == "none" || row.ResidueSeconds <= 0f || row.ResidueRadius <= 0f)
                {
                    continue;
                }
                if (NamedReactionCatalog.BitOf(row.TagA) == bit || NamedReactionCatalog.BitOf(row.TagB) == bit)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>管线悬停追加的行：这一格被击穿正在漏 + 这一格的液洼读数（没有 = 空串）。</summary>
        public static string PipeHoverLines(CampaignState state, GridCell cell)
        {
            PipeLeakRecord r = At(state, cell);
            if (r == null)
            {
                return string.Empty;
            }
            StringBuilder sb = HoverSb;
            sb.Clear();
            if (r.State == StateLeaking)
            {
                sb.Append(GameText.Format("leak.pipe.breached", PipeNetworkService.FluidName(r.Fluid))).Append('\n');
            }
            AppendLines(sb, state, r, GameClock.Ticks);
            return sb.ToString();
        }

        /// <summary>自检 / 证据：全部液洼的签名（编号、格、流体、阶段、各个步）。</summary>
        public static string Snapshot(CampaignState state)
        {
            var sb = new StringBuilder();
            foreach (PipeLeakRecord r in Records(state))
            {
                if (r == null)
                {
                    continue;
                }
                sb.Append(r.Id).Append('@').Append(r.X).Append(',').Append(r.Y).Append(':').Append(r.Fluid).Append('/').Append(r.State)
                  .Append('/').Append(r.GrowTick).Append('/').Append(r.StateTick).Append('/').Append(r.FromRadius.ToString("R", CultureInfo.InvariantCulture))
                  .Append('/').Append(r.Rule).Append('/').Append(r.ReactUntilTick).Append(r.Damaging ? "D" : "-").Append(';');
            }
            return sb.ToString();
        }
    }
}
