using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>研究节点此刻的状态（树上每个节点都有文字 + 符号，不只靠颜色，B05 / B15）。</summary>
    public enum ResearchNodeState : byte
    {
        Done = 0,
        /// <summary>在队列里，正在接收研究点（队列里第一个前置齐、关键材料齐的节点）。</summary>
        Researching = 1,
        /// <summary>在队列里，排在后面（或在等前置完成）。</summary>
        Queued = 2,
        /// <summary>在队列里，但核心保管库里缺它要的关键材料：跳过它，研究点给后面的节点。</summary>
        WaitingKey = 3,
        Available = 4,
        /// <summary>前置还没完成。</summary>
        Locked = 5,
        /// <summary>阵营分支还没开放。</summary>
        Closed = 6,
        /// <summary>要解锁的内容还没做（后续版本开放）。</summary>
        Later = 7,
    }

    /// <summary>研究整体在做什么（研发树顶部状态行、顶栏、通知）。</summary>
    public enum ResearchStatus : byte
    {
        Running = 0,
        QueueEmpty = 1,
        NoLab = 2,
        NoTech = 3,
        NoPower = 4,
        Disabled = 5,
        WaitingKey = 6,
    }

    /// <summary>一座仿真实验室此刻的状态。</summary>
    public enum LabState : byte
    {
        Working = 0,
        NoTech = 1,
        NoPower = 2,
        Disabled = 3,
        Destroyed = 4,
        NotBuilt = 5,
    }

    /// <summary>
    /// FG5-RND-01（FG05 FGR-RND-001 研究点产出、FGR-RND-002 研究进度、FGR-RND-010～013 研发树；FGT-RND-001 / 002）：研究的唯一写入口。
    /// - 仿真实验室：每座按周期把技术数据转成研究点——周期开始时取 1 件技术数据（初值每分钟 2 件 = 每 30 游戏秒一个周期），周期结束按“每分钟研究点 ÷ 每分钟技术数据 × 等级效果值 ×
    ///   （1 + 研发加成）”记研究点（千分之一点精度，零头留着不丢）。缺电 / 禁用 / 被摧毁时停住、进度不清零；技术数据不足时停工并写明原因。多座产出相加。
    /// - 研究队列：最多 research.queue.max（5）项，按顺序把研究点投进第一个前置齐、关键材料齐的节点；缺关键材料的跳过（研究点给后面的）。没有实验室研究不推进。
    ///   移出队列 / 取消：已投入的研究点留在节点上（<see cref="ResearchState.Progress"/>），下次接着做（FGR-RND-002）。
    /// - 完成：记已完成、门槛随之放开（<see cref="ResearchGate"/>）、新解锁的建造菜单条目标“新”（<see cref="ResearchState.NewEntries"/>）、效率 / 容量效果立即生效、发“研究完成”通知（进离家报告）。
    /// 推进只看世界步序号（WorldSimulation 每 eco.prod.step_ticks 步调一次，与生产建筑同一节拍），与观察、帧率、倍速无关；暂停时不走。开销 O(实验室数 + 队列长度)。
    /// </summary>
    public static class ResearchService
    {
        public const string TypeId = ResearchCatalog.LabTypeId;

        /// <summary>研究点 / 队列 / 进度 / 完成 / 新标记变化时 +1（界面据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static int StepCount { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        public static int CompletedThisSession { get; private set; }
        /// <summary>最近一次完成的节点（自检 / 冒烟读）。</summary>
        public static string LastCompleted { get; private set; }
        public static string LastStallCode { get; private set; }

        // ── 调参 ──
        public static double TechPerMinute => Math.Max(0.01, GridContent.Tuning("research.lab.tech_per_minute"));
        public static double PointsPerMinute => Math.Max(0.0, GridContent.Tuning("research.lab.points_per_minute"));
        public static int QueueMax => Math.Max(1, GridContent.TuningInt("research.queue.max"));
        public static double EfficiencyCap => Math.Max(0.0, GridContent.Tuning("research.efficiency_cap"));
        private static int StepTicks => Math.Max(1, GridContent.TuningInt("eco.prod.step_ticks"));

        private static BuildingRecord[] _labIndexed;
        private static readonly List<BuildingRecord> LabList = new List<BuildingRecord>(4);
        private static bool _labHookRaised;

        public static void ResetSessionState()
        {
            _labIndexed = null;
            LabList.Clear();
            _effState = null;
            _effDone = null;
            _openKey = -1;
            _openState = null;
            UnlockedCodexFactions.Clear();
            _labHookRaised = false;
            StepCount = 0;
            MaxStepMs = 0;
            LastStepMs = 0;
            CompletedThisSession = 0;
            LastCompleted = null;
            LastStallCode = null;
            Revision++;
        }

        public static void ResetForTests() => ResetSessionState();

        // ── 实验室 ───────────────────────────────────────────────────────────────

        /// <summary>家园里的仿真实验室（不含升级虚影；按建筑数组引用缓存，O(实验室数)）。</summary>
        public static IReadOnlyList<BuildingRecord> LabsOf(CampaignState state)
        {
            BuildingRecord[] records = state?.BuildingRecords;
            if (!ReferenceEquals(records, _labIndexed))
            {
                _labIndexed = records;
                LabList.Clear();
                foreach (BuildingRecord b in records ?? Array.Empty<BuildingRecord>())
                {
                    if (b != null && b.BuildingTypeId == TypeId && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b))
                    {
                        LabList.Add(b);
                    }
                }
            }
            return LabList;
        }

        /// <summary>建成了的实验室座数（运转 / 禁用 / 缺电都算；虚影、已摧毁不算）。</summary>
        public static int BuiltLabCount(CampaignState state)
        {
            int n = 0;
            foreach (BuildingRecord b in LabsOf(state))
            {
                n += IsBuilt(b) ? 1 : 0;
            }
            return n;
        }

        private static bool IsBuilt(BuildingRecord b) =>
            b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled;

        private static bool Powered(BuildingRecord b) =>
            !(HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof) && prof.PowerDemand > 0f)
            || b.PowerState == BuildingPowerState.Powered;

        public static LabState LabStateOf(CampaignState state, BuildingRecord b)
        {
            if (b == null)
            {
                return LabState.NotBuilt;
            }
            switch (b.ConstructionState)
            {
                case BuildingConstructionState.Operational:
                    break;
                case BuildingConstructionState.Disabled:
                    return LabState.Disabled;
                case BuildingConstructionState.Damaged:
                case BuildingConstructionState.Destroyed:
                    return LabState.Destroyed;
                default:
                    return LabState.NotBuilt;
            }
            if (!Powered(b))
            {
                return LabState.NoPower;
            }
            LabRecord rec = FindLab(state, b.BuildingId);
            if ((rec == null || !rec.Loaded) && Math.Max(0, state?.TechData ?? 0) < 1)
            {
                return LabState.NoTech;
            }
            return LabState.Working;
        }

        public static LabRecord FindLab(CampaignState state, string buildingId)
        {
            LabRecord[] labs = state?.Research?.Labs;
            if (labs == null || buildingId == null)
            {
                return null;
            }
            for (int i = 0; i < labs.Length; i++)
            {
                if (labs[i] != null && labs[i].BuildingId == buildingId)
                {
                    return labs[i];
                }
            }
            return null;
        }

        /// <summary>一个周期多少世界步（每件技术数据一个周期：60 ÷ 每分钟技术数据 游戏秒）。</summary>
        public static long CycleTicks(int worldHz) => Math.Max(1L, (long)Math.Round(60.0 / TechPerMinute * Math.Max(1, worldHz)));

        /// <summary>这座实验室的转换效率（等级效果值 × (1 + 研发加成)）。</summary>
        public static double LabEfficiency(CampaignState state, BuildingRecord b)
        {
            BuildingTier row = BuildingOps.TierRow(TypeId, BuildingOps.TierOf(b));
            double tierValue = row != null ? row.Value : 1.0;
            return tierValue * (1.0 + EffectTotal(state, ResearchCatalog.EffectLab, TypeId));
        }

        /// <summary>这座实验室一个周期产出的研究点（千分之一点）。</summary>
        public static int PerCycleMilli(CampaignState state, BuildingRecord b) =>
            (int)Math.Round(1000.0 * PointsPerMinute / TechPerMinute * LabEfficiency(state, b));

        /// <summary>这座实验室满速时每分钟产出的研究点。</summary>
        public static double LabPointsPerMinute(CampaignState state, BuildingRecord b) => PointsPerMinute * LabEfficiency(state, b);

        /// <summary>此刻正在工作的实验室合计每分钟产出的研究点。</summary>
        public static double RatePerMinute(CampaignState state)
        {
            double sum = 0;
            foreach (BuildingRecord b in LabsOf(state))
            {
                if (LabStateOf(state, b) == LabState.Working)
                {
                    sum += LabPointsPerMinute(state, b);
                }
            }
            return sum;
        }

        /// <summary>实验室的通用状态（建筑面板 / 悬停 / “为什么不工作”，B05 / B06）。</summary>
        public static BuildingStatus LabStatus(CampaignState state, BuildingRecord b)
        {
            switch (LabStateOf(state, b))
            {
                case LabState.NoTech:
                    return new BuildingStatus(BuildingStatusKind.NoMaterial, "lab.no_tech", GameText.Format("bs.reason.lab_no_tech", Math.Max(0, state?.TechData ?? 0)));
                case LabState.NoPower:
                    string why = b.PowerState == BuildingPowerState.Brownout
                        ? GameText.Format("prod.reason.power_brownout", b.PowerPriority)
                        : GameText.Get("prod.reason.power_unconnected");
                    return new BuildingStatus(BuildingStatusKind.NoPower, "lab.no_power", GameText.Format("bs.reason.lab_no_power", why));
                case LabState.Disabled:
                    return new BuildingStatus(BuildingStatusKind.Disabled, "lab.disabled", GameText.Get("bs.reason.lab_disabled"));
                case LabState.Destroyed:
                    return new BuildingStatus(BuildingStatusKind.Destroyed, "destroyed", GameText.Get("bs.reason.destroyed_norepair"));
                case LabState.NotBuilt:
                    return new BuildingStatus(BuildingStatusKind.Building, "ghost", string.Empty);
                default:
                    LabRecord rec = FindLab(state, b.BuildingId);
                    long cycle = CycleTicks(GameClock.StepHz);
                    int pct = rec != null && rec.Loaded ? (int)Math.Min(99, rec.ProgressTicks * 100 / Math.Max(1, cycle)) : 0;
                    return new BuildingStatus(BuildingStatusKind.Working, "lab.working",
                        GameText.Format("bs.reason.working_lab", Num(TechPerMinute), Num(LabPointsPerMinute(state, b)), pct));
            }
        }

        public static string Num(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 0.005 ? "0" : "0.##", CultureInfo.InvariantCulture);

        // ── 推进 ─────────────────────────────────────────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在生产建筑之后调用）：每 eco.prod.step_ticks 步推进一次。只看步序号。</summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null)
            {
                return;
            }
            int k = StepTicks;
            if (ticksBefore % k != 0)
            {
                return;
            }
            Step(state, k, worldHz);
        }

        /// <summary>推进全部实验室与研究队列 <paramref name="ticks"/> 个世界步（自检直接驱动；生产路径由 <see cref="WorldStep"/> 调）。</summary>
        public static void Step(CampaignState state, int ticks, int worldHz)
        {
            if (state == null || ticks <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CampaignFgStateDomains.EnsureAll(state);
            ResearchState r = state.Research;
            IReadOnlyList<BuildingRecord> labs = LabsOf(state);
            SyncLabRecords(state, labs);
            long cycle = CycleTicks(worldHz);
            bool anyOperational = false;
            foreach (BuildingRecord b in labs)
            {
                anyOperational |= b.ConstructionState == BuildingConstructionState.Operational;
                if (LabStateOf(state, b) != LabState.Working)
                {
                    continue;
                }
                LabRecord rec = FindLab(state, b.BuildingId);
                long budget = ticks;
                for (int guard = 0; guard < 8 && budget > 0; guard++)
                {
                    if (!rec.Loaded)
                    {
                        if (!TakeTech(state))
                        {
                            break; // 技术数据用完：停在周期开头，状态写“技术数据不足”。
                        }
                        rec.Loaded = true;
                        rec.ProgressTicks = 0;
                    }
                    long use = Math.Min(cycle - rec.ProgressTicks, budget);
                    rec.ProgressTicks += use;
                    budget -= use;
                    if (rec.ProgressTicks >= cycle)
                    {
                        r.PointsMilli += PerCycleMilli(state, b);
                        rec.Loaded = false;
                        rec.ProgressTicks = 0;
                    }
                }
            }
            if (anyOperational && !_labHookRaised)
            {
                _labHookRaised = true;
                GuidanceHooks.Raise(GuidanceHooks.ResearchLabFirstBuilt);
            }
            if (r.PointsMilli >= 1000)
            {
                int n = r.PointsMilli / 1000;
                r.PointsMilli -= n * 1000;
                r.PointsProduced += n;
                if (ItemCatalog.TryGet(ItemCatalog.ResearchPointsId, out ItemDef rp))
                {
                    HomeInventory.Add(state, rp, n, clampToSpace: false);
                    ProductionStats.RecordUnits(state, rp, n, produced: true);
                }
                else
                {
                    r.Points += n;
                }
                Revision++;
            }
            Invest(state, notify: true);
            UpdateStall(state);
            StepCount++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
        }

        public static void ResetStepStats() => MaxStepMs = 0;

        private static bool TakeTech(CampaignState state)
        {
            if (!ItemCatalog.TryGet(ItemCatalog.TechDataId, out ItemDef tech) || HomeInventory.RemoveUpTo(state, tech, 1) != 1)
            {
                return false;
            }
            state.Research.TechConsumed++;
            ProductionStats.RecordUnits(state, tech, 1, produced: false);
            TechDataFlow.Spend(state, TechDataFlow.Lab, 1); // FG5-E2E-01：统计面板“技术数据支出 · 实验室”
            return true;
        }

        /// <summary>实验室记录与建筑对齐：新建的补记录；建筑已经不在（不是经拆除流程移走的）就退回本周期已取的技术数据并删掉记录。</summary>
        private static void SyncLabRecords(CampaignState state, IReadOnlyList<BuildingRecord> labs)
        {
            ResearchState r = state.Research;
            bool changed = r.Labs.Length != labs.Count;
            if (!changed)
            {
                for (int i = 0; i < labs.Count && !changed; i++)
                {
                    changed = FindLab(state, labs[i].BuildingId) == null;
                }
            }
            if (!changed)
            {
                return;
            }
            var keep = new List<LabRecord>(labs.Count);
            foreach (BuildingRecord b in labs)
            {
                LabRecord rec = FindLab(state, b.BuildingId) ?? new LabRecord { BuildingId = b.BuildingId };
                keep.Add(rec);
            }
            foreach (LabRecord old in r.Labs)
            {
                if (old != null && old.Loaded && !keep.Contains(old))
                {
                    RefundTech(state);
                }
            }
            keep.Sort((a, b) => string.CompareOrdinal(a.BuildingId, b.BuildingId));
            r.Labs = keep.ToArray();
        }

        private static void RefundTech(CampaignState state)
        {
            if (ItemCatalog.TryGet(ItemCatalog.TechDataId, out ItemDef tech))
            {
                HomeInventory.Add(state, tech, 1, clampToSpace: false);
                state.Research.TechConsumed = Math.Max(0, state.Research.TechConsumed - 1);
                TechDataFlow.Unspend(state, TechDataFlow.Lab, 1);
            }
        }

        /// <summary>拆除实验室（拆除流程在移走建筑记录之前调）：本周期已取的技术数据退回，记录删掉。</summary>
        public static void OnDemolished(CampaignState state, BuildingRecord building)
        {
            if (state?.Research == null || building == null || building.BuildingTypeId != TypeId)
            {
                return;
            }
            LabRecord rec = FindLab(state, building.BuildingId);
            if (rec == null)
            {
                return;
            }
            if (rec.Loaded)
            {
                RefundTech(state);
            }
            var list = new List<LabRecord>(state.Research.Labs);
            list.Remove(rec);
            state.Research.Labs = list.ToArray();
            Revision++;
        }

        // ── 研究队列 ─────────────────────────────────────────────────────────────

        public static bool IsCompleted(CampaignState state, string nodeId)
        {
            string[] done = state?.Research?.CompletedNodes;
            return done != null && nodeId != null && Array.IndexOf(done, nodeId) >= 0;
        }

        public static int Invested(CampaignState state, string nodeId)
        {
            if (IsCompleted(state, nodeId))
            {
                return ResearchCatalog.TryGet(nodeId, out ResearchNodeDef n) ? n.Cost : 0;
            }
            ResearchProgressRecord rec = FindProgress(state, nodeId);
            return rec?.Invested ?? 0;
        }

        private static ResearchProgressRecord FindProgress(CampaignState state, string nodeId)
        {
            ResearchProgressRecord[] list = state?.Research?.Progress;
            if (list == null || nodeId == null)
            {
                return null;
            }
            foreach (ResearchProgressRecord p in list)
            {
                if (p != null && p.NodeId == nodeId)
                {
                    return p;
                }
            }
            return null;
        }

        public static int QueueIndex(CampaignState state, string nodeId)
        {
            string[] q = state?.Research?.Queue;
            return q == null || nodeId == null ? -1 : Array.IndexOf(q, nodeId);
        }

        public static IReadOnlyList<string> Queue(CampaignState state) => state?.Research?.Queue ?? Array.Empty<string>();

        public static bool PrereqsDone(CampaignState state, ResearchNodeDef n)
        {
            foreach (string p in n.Prereqs)
            {
                if (!IsCompleted(state, p))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>核心保管库里缺的关键材料（逐条“监听阵列核 ×1——来源”）；都有返回 null。研究不消耗关键材料。</summary>
        public static string KeyShortfall(CampaignState state, ResearchNodeDef n)
        {
            List<string> parts = null;
            foreach (BuildMaterialNeed need in n.KeyItems)
            {
                if (HomeInventory.Stock(state, need.Item) < need.Amount)
                {
                    parts ??= new List<string>(1);
                    parts.Add(GameText.Format("research.reason.key_missing", need.Item.Name + " ×" + need.Amount, BuildMaterials.SourceOf(need.Item)));
                }
            }
            return parts == null ? null : string.Join("；", parts);
        }

        public static bool KeysPresent(CampaignState state, ResearchNodeDef n) => KeyShortfall(state, n) == null;

        /// <summary>阵营分支是否开放：通用分支恒开放；阵营分支在破解该阵营第一件技术（任一枚该阵营的敌方加密固件已破解）后开放。</summary>
        public static bool BranchOpen(CampaignState state, ResearchBranchDef branch)
        {
            if (branch == null || !branch.IsFaction)
            {
                return true;
            }
            EnsureOpenFactions(state);
            return OpenFactions.Contains(branch.Faction);
        }

        private static readonly HashSet<string> OpenFactions = new HashSet<string>(StringComparer.Ordinal);
        private static int _openKey = -1;
        private static CampaignState _openState;

        private static void EnsureOpenFactions(CampaignState state)
        {
            string[] unlocked = state?.UnlockedContentIds;
            int key = HashCode.Combine(unlocked?.Length ?? 0, Content.FirmwareCatalog.Revision);
            if (ReferenceEquals(state, _openState) && key == _openKey)
            {
                return;
            }
            _openState = state;
            _openKey = key;
            OpenFactions.Clear();
            foreach (GameConfig.fg.FirmwareKind row in Signal.FirmwareKinds.Rows)
            {
                if (row != null && Signal.FirmwareKinds.IsEnemyProtocol(row.Id) && Content.MechanicalContentUnlock.IsUnlocked(state, row.Id))
                {
                    OpenFactions.Add(Signal.FirmwareKinds.FactionOf(row.Id));
                }
            }
            // 阵营分支开放：它的节点在图鉴“研究”页签解锁（通用分支的节点一开始就能看）。只在开放集合变化时走一遍。
            foreach (ResearchBranchDef b in ResearchCatalog.Branches)
            {
                if (b.IsFaction && OpenFactions.Contains(b.Faction) && UnlockedCodexFactions.Add(b.Faction))
                {
                    foreach (ResearchNodeDef n in b.Nodes)
                    {
                        Progression.MechanicCodex.Unlock(Progression.MechanicCodex.ResearchEntryId(n.Id));
                    }
                }
            }
        }

        private static readonly HashSet<string> UnlockedCodexFactions = new HashSet<string>(StringComparer.Ordinal);

        public static ResearchNodeState StateOf(CampaignState state, ResearchNodeDef n)
        {
            if (n == null)
            {
                return ResearchNodeState.Later;
            }
            if (IsCompleted(state, n.Id))
            {
                return ResearchNodeState.Done;
            }
            int qi = QueueIndex(state, n.Id);
            if (qi >= 0)
            {
                if (!KeysPresent(state, n))
                {
                    return ResearchNodeState.WaitingKey;
                }
                return ReferenceEquals(Head(state), n) ? ResearchNodeState.Researching : ResearchNodeState.Queued;
            }
            if (!BranchOpen(state, n.Branch))
            {
                return ResearchNodeState.Closed; // 阵营分支没开放：先写怎么开放（不剧透内容进度）
            }
            if (!n.IsReady)
            {
                return ResearchNodeState.Later;
            }
            return PrereqsDone(state, n) ? ResearchNodeState.Available : ResearchNodeState.Locked;
        }

        /// <summary>队列里此刻接收研究点的节点（第一个前置齐、关键材料齐的）；没有 = null。</summary>
        public static ResearchNodeDef Head(CampaignState state)
        {
            foreach (string id in Queue(state))
            {
                if (ResearchCatalog.TryGet(id, out ResearchNodeDef n) && !IsCompleted(state, id) && PrereqsDone(state, n) && KeysPresent(state, n))
                {
                    return n;
                }
            }
            return null;
        }

        /// <summary>加入队列（排在最后）。拒绝时 <paramref name="reason"/> 写明原因（B06）。前置可以已完成或已经在队列里（排在它前面）。</summary>
        public static bool TryEnqueue(CampaignState state, string nodeId, out string reason)
        {
            reason = null;
            if (state == null || !ResearchCatalog.TryGet(nodeId, out ResearchNodeDef n))
            {
                reason = GameText.Get("research.reason.unknown");
                return false;
            }
            CampaignFgStateDomains.EnsureAll(state);
            if (IsCompleted(state, nodeId))
            {
                reason = GameText.Get("research.reason.done");
                return false;
            }
            if (QueueIndex(state, nodeId) >= 0)
            {
                reason = GameText.Get("research.reason.queued");
                return false;
            }
            if (!BranchOpen(state, n.Branch))
            {
                reason = GameText.Format("research.reason.closed", n.Branch.Name);
                return false;
            }
            if (!n.IsReady)
            {
                reason = GameText.Format("research.reason.later", n.Name);
                return false;
            }
            var missing = new List<string>(2);
            foreach (string p in n.Prereqs)
            {
                if (!IsCompleted(state, p) && QueueIndex(state, p) < 0)
                {
                    missing.Add(ResearchCatalog.Find(p)?.Name ?? p);
                }
            }
            if (missing.Count > 0)
            {
                reason = GameText.Format("research.reason.prereq", string.Join(GameText.Get("build.cost.extra_sep"), missing));
                return false;
            }
            if (state.Research.Queue.Length >= QueueMax)
            {
                reason = GameText.Format("research.reason.queue_full", QueueMax);
                return false;
            }
            var q = new List<string>(state.Research.Queue) { nodeId };
            state.Research.Queue = q.ToArray();
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.ResearchFirstQueued);
            // 有攒着的研究点时立刻投入（与世界步里的投入同一套规则）。
            Invest(state, notify: true);
            return true;
        }

        /// <summary>移出队列（取消）：已投入的进度留在节点上（FGR-RND-002）。排在它后面、以它为前置的节点一起移出（否则它们永远等不到前置）。</summary>
        public static bool TryDequeue(CampaignState state, string nodeId, out string reason)
        {
            reason = null;
            if (QueueIndex(state, nodeId) < 0)
            {
                reason = GameText.Get("research.reason.not_queued");
                return false;
            }
            var q = new List<string>(state.Research.Queue);
            q.Remove(nodeId);
            // 依赖它（直接或间接）而它又没完成的后续节点一起移出，进度同样保留。
            bool removed = true;
            while (removed)
            {
                removed = false;
                for (int i = q.Count - 1; i >= 0; i--)
                {
                    ResearchNodeDef d = ResearchCatalog.Find(q[i]);
                    if (d == null)
                    {
                        continue;
                    }
                    foreach (string p in d.Prereqs)
                    {
                        if (!IsCompleted(state, p) && !q.Contains(p))
                        {
                            q.RemoveAt(i);
                            removed = true;
                            break;
                        }
                    }
                }
            }
            state.Research.Queue = q.ToArray();
            Revision++;
            return true;
        }

        /// <summary>调整顺序：把 <paramref name="nodeId"/> 挪到第 <paramref name="toIndex"/> 位（0 起）。不能排到它没完成的前置前面，也不能把别的节点挪到它们的前置前面。</summary>
        public static bool TryMove(CampaignState state, string nodeId, int toIndex, out string reason)
        {
            reason = null;
            int from = QueueIndex(state, nodeId);
            if (from < 0)
            {
                reason = GameText.Get("research.reason.not_queued");
                return false;
            }
            var q = new List<string>(state.Research.Queue);
            toIndex = Math.Max(0, Math.Min(q.Count - 1, toIndex));
            if (toIndex == from)
            {
                return true;
            }
            q.RemoveAt(from);
            q.Insert(toIndex, nodeId);
            for (int i = 0; i < q.Count; i++)
            {
                ResearchNodeDef d = ResearchCatalog.Find(q[i]);
                if (d == null)
                {
                    continue;
                }
                foreach (string p in d.Prereqs)
                {
                    int pi = q.IndexOf(p);
                    if (pi > i)
                    {
                        reason = GameText.Format("research.reason.order", ResearchCatalog.Find(p)?.Name ?? p);
                        return false;
                    }
                }
            }
            state.Research.Queue = q.ToArray();
            Revision++;
            Invest(state, notify: true);
            return true;
        }

        /// <summary>把攒着的研究点按队列顺序投进去（没有实验室不推进，FGR-RND-002）。</summary>
        private static void Invest(CampaignState state, bool notify)
        {
            ResearchState r = state.Research;
            if (r.Points <= 0 || r.Queue.Length == 0 || BuiltLabCount(state) == 0)
            {
                return;
            }
            for (int guard = 0; guard < 16 && r.Points > 0; guard++)
            {
                ResearchNodeDef n = Head(state);
                if (n == null)
                {
                    return;
                }
                ResearchProgressRecord rec = FindProgress(state, n.Id);
                int have = rec?.Invested ?? 0;
                int take = Math.Min(r.Points, n.Cost - have);
                if (take > 0)
                {
                    if (rec == null)
                    {
                        rec = new ResearchProgressRecord { NodeId = n.Id };
                        var list = new List<ResearchProgressRecord>(r.Progress) { rec };
                        list.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
                        r.Progress = list.ToArray();
                    }
                    rec.Invested = have + take;
                    if (ItemCatalog.TryGet(ItemCatalog.ResearchPointsId, out ItemDef rp))
                    {
                        HomeInventory.RemoveUpTo(state, rp, take);
                        ProductionStats.RecordUnits(state, rp, take, produced: false);
                    }
                    else
                    {
                        r.Points -= take;
                    }
                    r.PointsInvested += take;
                    Revision++;
                }
                if ((rec?.Invested ?? have) >= n.Cost)
                {
                    Complete(state, n, notify);
                }
            }
        }

        /// <summary>节点完成：记已完成、移出队列与进度、新解锁的建造菜单条目标“新”、效果生效、通知（进离家报告）、钩子。</summary>
        private static void Complete(CampaignState state, ResearchNodeDef n, bool notify)
        {
            ResearchState r = state.Research;
            if (IsCompleted(state, n.Id))
            {
                return;
            }
            var done = new List<string>(r.CompletedNodes) { n.Id };
            r.CompletedNodes = done.ToArray();
            var q = new List<string>(r.Queue);
            q.Remove(n.Id);
            r.Queue = q.ToArray();
            var prog = new List<ResearchProgressRecord>(r.Progress);
            prog.RemoveAll(p => p == null || p.NodeId == n.Id);
            r.Progress = prog.ToArray();
            var fresh = new List<string>(r.NewEntries);
            foreach (string u in n.Unlocks)
            {
                if (u.StartsWith("build:", StringComparison.Ordinal))
                {
                    string entry = u.Substring(6);
                    if (!fresh.Contains(entry))
                    {
                        fresh.Add(entry);
                    }
                }
            }
            r.NewEntries = fresh.ToArray();
            Revision++;
            CompletedThisSession++;
            LastCompleted = n.Id;
            _effDone = null;
            if (n.EffectKind == ResearchCatalog.EffectCapacity && n.EffectTarget == "energy_storage" && WorldSim.WorldSimulation.Home != null)
            {
                HomeValleyPowerGrid.Recompute(state); // 储能站容量立即按新加成重建（其余效果按需读取）。
            }
            HomeInventory.Touch();
            Progression.MechanicCodex.Unlock(Progression.MechanicCodex.ResearchEntryId(n.Id));
            if (!notify)
            {
                return;
            }
            GuidanceHooks.Raise(GuidanceHooks.ResearchFirstCompleted);
            string what = UnlockSummary(n);
            BuildingRecord lab = FirstLab(state);
            NotificationCenter.Post("research_done", GameText.Format("research.notify.done", n.Name, what),
                lab != null ? new Vector3(lab.Position.x, 0f, lab.Position.y) : (Vector3?)null);
        }

        private static BuildingRecord FirstLab(CampaignState state)
        {
            foreach (BuildingRecord b in LabsOf(state))
            {
                if (IsBuilt(b))
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>“解锁 分流器、合流器” / “效果已生效”（通知与离家报告用）。</summary>
        public static string UnlockSummary(ResearchNodeDef n)
        {
            var names = new List<string>(4);
            foreach (string u in n.Unlocks)
            {
                names.Add(TargetName(u));
            }
            return names.Count > 0
                ? GameText.Format("research.notify.done_unlocks", string.Join(GameText.Get("build.cost.extra_sep"), names))
                : GameText.Get("research.notify.done_effect");
        }

        /// <summary>解锁目标的玩家名：build:splitter → “分流器”；tier:warehouse.t2 → “仓库 T2”。</summary>
        public static string TargetName(string target)
        {
            if (target != null && target.StartsWith("build:", StringComparison.Ordinal) && BuildCatalog.TryGet(target.Substring(6), out BuildEntry e))
            {
                return e.Name;
            }
            if (ResearchCatalog.TryParseTier(target, out string typeId, out int tier))
            {
                return BuildingOps.TierName(typeId, tier);
            }
            // FG6-DEF-03：rule:auto_rebuild → 常驻规则的类型名（“自动重建”）。
            if (target != null && target.StartsWith("rule:", StringComparison.Ordinal) && StandingRuleService.KindRow(target.Substring(5)) is GameConfig.fg.RuleKind rk)
            {
                return GameText.Format("research.unlock.rule", GameText.Get(rk.NameKey));
            }
            return target ?? string.Empty;
        }

        // ── 整体状态与“研究暂停”通知 ────────────────────────────────────────────

        public static ResearchStatus StatusOf(CampaignState state)
        {
            if (Queue(state).Count == 0)
            {
                return ResearchStatus.QueueEmpty;
            }
            if (BuiltLabCount(state) == 0)
            {
                return ResearchStatus.NoLab;
            }
            if (Head(state) == null)
            {
                return ResearchStatus.WaitingKey;
            }
            bool noTech = false, noPower = false;
            foreach (BuildingRecord b in LabsOf(state))
            {
                switch (LabStateOf(state, b))
                {
                    case LabState.Working:
                        return ResearchStatus.Running;
                    case LabState.NoTech:
                        noTech = true;
                        break;
                    case LabState.NoPower:
                        noPower = true;
                        break;
                }
            }
            return noTech ? ResearchStatus.NoTech : noPower ? ResearchStatus.NoPower : ResearchStatus.Disabled;
        }

        public static string StatusText(CampaignState state)
        {
            switch (StatusOf(state))
            {
                case ResearchStatus.QueueEmpty:
                    return GameText.Get("research.status.queue_empty");
                case ResearchStatus.NoLab:
                    return GameText.Get("research.status.no_lab");
                case ResearchStatus.WaitingKey:
                    return GameText.Get("research.status.waiting_key");
                case ResearchStatus.NoTech:
                    return GameText.Get("research.status.paused_no_tech");
                case ResearchStatus.NoPower:
                    return GameText.Get("research.status.paused_no_power");
                case ResearchStatus.Disabled:
                    return GameText.Get("research.status.paused_disabled");
                default:
                    ResearchNodeDef h = Head(state);
                    return h != null ? GameText.Format("research.status.running", h.Name, Invested(state, h.Id), h.Cost) : string.Empty;
            }
        }

        /// <summary>“研究点 12 · 实验室 2 座（工作中 1）· 每分钟 +1”。</summary>
        public static string SummaryLine(CampaignState state)
        {
            int working = 0;
            foreach (BuildingRecord b in LabsOf(state))
            {
                working += LabStateOf(state, b) == LabState.Working ? 1 : 0;
            }
            return GameText.Format("research.status.line", Math.Max(0, state?.Research?.Points ?? 0), BuiltLabCount(state), working, Num(RatePerMinute(state)));
        }

        private static void UpdateStall(CampaignState state)
        {
            ResearchState r = state.Research;
            ResearchStatus s = StatusOf(state);
            string code;
            string detail;
            switch (s)
            {
                case ResearchStatus.NoTech:
                    code = "no_tech";
                    detail = GameText.Get("research.notify.stalled_no_tech");
                    break;
                case ResearchStatus.NoPower:
                    code = "no_power";
                    detail = GameText.Get("research.notify.stalled_no_power");
                    break;
                case ResearchStatus.WaitingKey:
                    ResearchNodeDef first = ResearchCatalog.Find(Queue(state).Count > 0 ? Queue(state)[0] : null);
                    code = "key:" + first?.Id;
                    detail = first != null ? GameText.Format("research.notify.stalled_key", first.Name, KeyShortfall(state, first) ?? string.Empty) : string.Empty;
                    break;
                default:
                    if (s == ResearchStatus.Running || s == ResearchStatus.QueueEmpty)
                    {
                        r.StallNotified = string.Empty;
                    }
                    return;
            }
            if (code == r.StallNotified)
            {
                return;
            }
            r.StallNotified = code;
            LastStallCode = code;
            GuidanceHooks.Raise(GuidanceHooks.ResearchFirstStalled);
            BuildingRecord lab = FirstLab(state);
            NotificationCenter.Post("research_stalled", detail, lab != null ? new Vector3(lab.Position.x, 0f, lab.Position.y) : (Vector3?)null);
        }

        // ── 效果（效率 / 容量）────────────────────────────────────────────────────

        private static CampaignState _effState;
        private static string[] _effDone;
        private static int _effCatalog;
        private static int _effGrid;
        private static double _effCap;
        /// <summary>效果种类 → 作用目标 → 合计。两级字符串键：查询不拼接字符串、不分配（生产步里每座生产建筑都会查，见 FgResearchSelfCheck L2）。</summary>
        private static readonly Dictionary<string, Dictionary<string, double>> EffTotals = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        /// <summary>建筑类型 → 工作速度倍率（随效果缓存一起失效）。生产步里 AccrueTheory / 周期开始每座生产建筑都查，命中后只是一次字典查找。</summary>
        private static readonly Dictionary<string, double> SpeedByType = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>按（存档、已完成数组引用、节点表版本、格网表版本）缓存效果合计；没有已完成节点时返回 false（一切效果为 0）。</summary>
        private static bool EnsureEffects(CampaignState state)
        {
            string[] done = state?.Research?.CompletedNodes;
            if (done == null || done.Length == 0)
            {
                return false;
            }
            if (ReferenceEquals(state, _effState) && ReferenceEquals(done, _effDone) && _effCatalog == ResearchCatalog.Revision && _effGrid == GridContent.Revision)
            {
                return true;
            }
            _effState = state;
            _effDone = done;
            _effCatalog = ResearchCatalog.Revision;
            _effGrid = GridContent.Revision;
            _effCap = EfficiencyCap;
            SpeedByType.Clear();
            foreach (Dictionary<string, double> byTarget in EffTotals.Values)
            {
                byTarget.Clear();
            }
            foreach (string id in done)
            {
                if (ResearchCatalog.TryGet(id, out ResearchNodeDef n) && n.EffectKind != "none" && n.EffectValue > 0f)
                {
                    if (!EffTotals.TryGetValue(n.EffectKind, out Dictionary<string, double> byTarget))
                    {
                        byTarget = new Dictionary<string, double>(StringComparer.Ordinal);
                        EffTotals[n.EffectKind] = byTarget;
                    }
                    string t0 = n.EffectTarget ?? string.Empty;
                    byTarget[t0] = (byTarget.TryGetValue(t0, out double v) ? v : 0.0) + n.EffectValue;
                }
            }
            return true;
        }

        /// <summary>已完成节点的效果合计（speed / lab 封顶 research.efficiency_cap）。按已完成数组引用缓存，查询 O(1)、零分配。</summary>
        public static double EffectTotal(CampaignState state, string kind, string target)
        {
            if (kind == null || target == null || !EnsureEffects(state))
            {
                return 0.0;
            }
            double total = EffTotals.TryGetValue(kind, out Dictionary<string, double> map) && map.TryGetValue(target, out double t) ? t : 0.0;
            return kind == ResearchCatalog.EffectCapacity ? total : Math.Min(_effCap, total);
        }

        /// <summary>生产建筑的工作速度倍率（按建造菜单分类：采集 / 加工 / 制造）。按类型缓存，生产步里查询零分配。</summary>
        public static double SpeedFactor(CampaignState state, string typeId)
        {
            if (typeId == null || !EnsureEffects(state))
            {
                return 1.0;
            }
            if (SpeedByType.TryGetValue(typeId, out double cached))
            {
                return cached;
            }
            BuildingGrid g = GridContent.Building(typeId);
            double f = g == null ? 1.0 : 1.0 + EffectTotal(state, ResearchCatalog.EffectSpeed, g.Category);
            SpeedByType[typeId] = f;
            return f;
        }

        /// <summary>按工作速度缩短一个周期的步数（至少 1）。</summary>
        public static long ScaleTicks(CampaignState state, string typeId, long ticks)
        {
            double f = SpeedFactor(state, typeId);
            return f <= 1.0 ? ticks : Math.Max(1L, (long)Math.Round(ticks / f));
        }

        /// <summary>容量倍率（warehouse / energy_storage）。</summary>
        public static double CapacityFactor(CampaignState state, string target) => 1.0 + EffectTotal(state, ResearchCatalog.EffectCapacity, target);

        public static int ScaleCapacity(CampaignState state, string target, int capacity)
        {
            double f = CapacityFactor(state, target);
            return f <= 1.0 ? capacity : (int)Math.Round(capacity * f);
        }

        // ── 建造菜单“新”标记 ──────────────────────────────────────────────────

        public static bool IsNew(CampaignState state, string entryId)
        {
            string[] list = state?.Research?.NewEntries;
            return list != null && list.Length > 0 && entryId != null && Array.IndexOf(list, entryId) >= 0;
        }

        public static bool AnyNewInCategory(CampaignState state, string categoryId)
        {
            string[] list = state?.Research?.NewEntries;
            if (list == null || list.Length == 0)
            {
                return false;
            }
            foreach (string id in list)
            {
                if (BuildCatalog.TryGet(id, out BuildEntry e) && e.CategoryId == categoryId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>玩家看过这些条目了：去掉“新”标记。</summary>
        public static int MarkSeen(CampaignState state, ICollection<string> entryIds)
        {
            string[] list = state?.Research?.NewEntries;
            if (list == null || list.Length == 0 || entryIds == null || entryIds.Count == 0)
            {
                return 0;
            }
            var keep = new List<string>(list.Length);
            foreach (string id in list)
            {
                if (!entryIds.Contains(id))
                {
                    keep.Add(id);
                }
            }
            int removed = list.Length - keep.Count;
            if (removed > 0)
            {
                state.Research.NewEntries = keep.ToArray();
                Revision++;
            }
            return removed;
        }

        // ── 研究门槛的说明（建造菜单悬停 / 原因）────────────────────────────────

        /// <summary>“研发树里：◆ 可研究 · 5 研究点”——门槛是研究节点时给出它在树上的状态；不是研究门槛返回空串。</summary>
        public static string GateStatusLine(CampaignState state, string rule)
        {
            string node = ResearchGate.NodeOf(rule);
            if (node == null || !ResearchGate.TreeAvailable || !ResearchCatalog.TryGet(node, out ResearchNodeDef n))
            {
                return string.Empty;
            }
            return GameText.Format("research.build.gate_status", StateText(state, n));
        }

        /// <summary>节点状态的一行文字（符号 + 文字 + 进度）。</summary>
        public static string StateText(CampaignState state, ResearchNodeDef n)
        {
            int inv = Invested(state, n.Id);
            switch (StateOf(state, n))
            {
                case ResearchNodeState.Done: return GameText.Get("research.state.done");
                case ResearchNodeState.Researching: return GameText.Format("research.state.researching", inv, n.Cost);
                case ResearchNodeState.Queued: return GameText.Format("research.state.queued", QueueIndex(state, n.Id) + 1, inv, n.Cost);
                case ResearchNodeState.WaitingKey: return GameText.Format("research.state.waiting_key", inv, n.Cost);
                case ResearchNodeState.Available:
                    return inv > 0 ? GameText.Format("research.state.available_progress", n.Cost, inv) : GameText.Format("research.state.available", n.Cost);
                case ResearchNodeState.Locked: return GameText.Format("research.state.locked", n.Cost);
                case ResearchNodeState.Closed: return GameText.Get("research.state.closed");
                default: return GameText.Get("research.state.later");
            }
        }

        // ── 旧档迁移与测试 ──────────────────────────────────────────────────────

        /// <summary>
        /// 研究域 1 → 2（研发树开放）：旧档在研发树开放前就能建造的内容（建筑 / 工具解锁、建筑升级、超控阵列各级）对应的节点记为已完成，
        /// 旧档里已经在用的东西不会突然造不了；效率 / 容量节点、实验室 T2 / T3（仿真实验室本身是研发树带来的新建筑）不送。不发通知。
        /// </summary>
        public static void MigrateFromV1(CampaignState state)
        {
            ResearchState r = state?.Research;
            if (r == null)
            {
                return;
            }
            var done = new List<string>(r.CompletedNodes ?? Array.Empty<string>());
            foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
            {
                if (n.IsReady && n.Unlocks.Length > 0 && !UnlocksResearchOnlyContent(n) && !done.Contains(n.Id))
                {
                    done.Add(n.Id);
                }
            }
            r.CompletedNodes = done.ToArray();
            r.DomainVersion = ResearchState.CurrentVersion;
            Revision++;
        }

        /// <summary>
        /// 旧档迁移（<see cref="MigrateFromV1"/>）会记为已完成的节点：研发树开放前就能建造的内容（建筑 / 工具、升级、超控阵列各级——要关键材料的照样算，
        /// 关键材料是另一道施工门槛）。FG5-E2E-01：研发树开放前写成的旧旅程按同一口径登记“已研究”进度夹具。
        /// </summary>
        public static List<ResearchNodeDef> MigratedNodes()
        {
            var list = new List<ResearchNodeDef>(24);
            foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
            {
                if (n.IsReady && n.Unlocks.Length > 0 && !UnlocksResearchOnlyContent(n))
                {
                    list.Add(n);
                }
            }
            return list;
        }

        /// <summary>节点解锁的东西是不是研发树开放时 / 之后才有的新内容（仿真实验室本身与它的等级；FG6-DEF-01 炮塔座的 T2 / T3）：旧档里根本没有，迁移不送，
        /// 开局技术数据的换算也不计（与表生成的 POST_TREE_NODES 同一口径，DEBT-FG5RND01-08）。</summary>
        private static bool UnlocksResearchOnlyContent(ResearchNodeDef n)
        {
            foreach (string u in n.Unlocks)
            {
                if (u == null)
                {
                    continue;
                }
                if (u.StartsWith("rule:", StringComparison.Ordinal))
                {
                    return true; // FG6-DEF-03：研发树门控的规则类型（自动重建）从落地起就由研究门控（与表生成的 POST_TREE_NODES 同一口径）
                }
                foreach (string type in PostTreeTypes)
                {
                    if (u == "build:" + type || u.StartsWith("tier:" + type + ".", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // FG6-DEF-02：屏障 / 闸门 / 护盾 / 陷阱同样是研发树开放之后才落地的内容（从一开始就由研究门控）。
        private static readonly string[] PostTreeTypes = { ResearchCatalog.LabTypeId, Defense.TurretCatalog.LightTypeId, Defense.TurretCatalog.HeavyTypeId,
            Defense.DefenseCatalog.BarrierT1, Defense.DefenseCatalog.BarrierT2, Defense.DefenseCatalog.BarrierT3, Defense.DefenseCatalog.GateTypeId,
            Defense.DefenseCatalog.ShieldTypeId, Defense.DefenseCatalog.TrapTypeId,
            Defense.RepairDroneCatalog.StationTypeId }; // FG6-DEF-03：维修无人机站

        // ── FG5-E2E-01：新档的技术数据供给（DEBT-FG5RND01-08）与研发加成的数值来源行（DEBT-FG5RND01-07）──────────────

        /// <summary>新战役开局带来的技术数据（research.start_tech_data，过渡初值，见 ADR-QA-022）。</summary>
        public static int StartTechData => Math.Max(0, GridContent.TuningInt("research.start_tech_data"));

        /// <summary>
        /// 研发树开放前（FG-M3 / M4）开局就能建造、现在要先研究的内容对应的节点：已就绪、有解锁、不是研发树自己带来的新建筑（仿真实验室与它的等级）、
        /// 不需要关键材料（超控阵列 / 监听站要首领给的关键材料，开放前也造不出来）。与旧档迁移（<see cref="MigrateFromV1"/>）同一口径，只多排除关键材料节点。
        /// </summary>
        public static List<ResearchNodeDef> LegacyOpenNodes()
        {
            var list = new List<ResearchNodeDef>(20);
            foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
            {
                if (n.IsReady && n.Unlocks.Length > 0 && !UnlocksResearchOnlyContent(n) && n.KeyItems.Count == 0)
                {
                    list.Add(n);
                }
            }
            return list;
        }

        /// <summary>研究完 <see cref="LegacyOpenNodes"/> 要的研究点合计。</summary>
        public static int LegacyOpenPoints()
        {
            int sum = 0;
            foreach (ResearchNodeDef n in LegacyOpenNodes())
            {
                sum += n.Cost;
            }
            return sum;
        }

        /// <summary>用 T1 实验室（不算研发加成）把 <see cref="LegacyOpenPoints"/> 研究出来要的技术数据（研究点 × 每分钟技术数据 ÷ 每分钟研究点，向上取整）。</summary>
        public static int LegacyOpenTechCost() =>
            PointsPerMinute <= 0 ? int.MaxValue : (int)Math.Ceiling(LegacyOpenPoints() * TechPerMinute / PointsPerMinute - 1e-9);

        /// <summary>
        /// 新战役开局：把 research.start_tech_data 记进技术数据，并记在 <see cref="ResearchState.TechStart"/>（统计面板对账）。只在主菜单“新建”创建战役时调一次；
        /// 旧档、自检用的空白战役不经过这里。初值放开局，而不是放进任何持续来源：研究成本、实验室转换率、黑匣子 / 解析台产出都保持 FG05 初值（ADR-QA-022）。
        /// </summary>
        public static void ApplyNewGameStart(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            int n = StartTechData;
            state.TechData += n;
            state.Research.TechStart = n;
            Revision++;
        }

        /// <summary>
        /// 研发加成的数值来源行（B13：复合数值能展开来源；DEBT-FG5RND01-07）：这类建筑此刻吃到的研发加成与来自哪些已完成的节点，没有加成返回 null。
        /// 生产建筑 = 工作速度（按建造菜单分类）；仿真实验室 = 转换效率；仓库 / 储能站 = 容量。建筑面板、悬停按这一行写，与真实生效的倍率同一读口（<see cref="EffectTotal"/>）。
        /// 只在面板刷新时调（O(已完成节点数)）。
        /// </summary>
        public static string BonusLine(CampaignState state, string typeId)
        {
            if (state == null || string.IsNullOrEmpty(typeId))
            {
                return null;
            }
            if (typeId == TypeId)
            {
                return BonusText(state, ResearchCatalog.EffectLab, TypeId, "research.bonus.lab", capped: true);
            }
            if (typeId == HomeValleyLayout.BuildingTypeWarehouse)
            {
                return BonusText(state, ResearchCatalog.EffectCapacity, HomeValleyLayout.BuildingTypeWarehouse, "research.bonus.capacity", capped: false);
            }
            if (typeId == "energy_storage")
            {
                return BonusText(state, ResearchCatalog.EffectCapacity, "energy_storage", "research.bonus.capacity", capped: false);
            }
            BuildingGrid g = GridContent.Building(typeId);
            return g == null || !ProducerCatalog.TryGet(typeId, out _) ? null
                : BonusText(state, ResearchCatalog.EffectSpeed, g.Category, "research.bonus.speed", capped: true);
        }

        /// <summary>物品悬停里的研发加成行：固体 / 保管库物品吃仓库容量加成时写一行，否则 null（DEBT-FG5RND01-07“物资悬停的数值来源展开”）。</summary>
        public static string ItemBonusLine(CampaignState state, ItemDef item)
        {
            if (state == null || item == null || item.Form != ItemForm.Solid)
            {
                return null;
            }
            return BonusText(state, ResearchCatalog.EffectCapacity, HomeValleyLayout.BuildingTypeWarehouse, "research.bonus.item_capacity", capped: false);
        }

        private static string BonusText(CampaignState state, string kind, string target, string key, bool capped)
        {
            double total = EffectTotal(state, kind, target);
            if (total <= 1e-9)
            {
                return null;
            }
            var names = new List<string>(4);
            foreach (string id in state.Research?.CompletedNodes ?? Array.Empty<string>())
            {
                if (ResearchCatalog.TryGet(id, out ResearchNodeDef n) && n.EffectKind == kind && n.EffectTarget == target && n.EffectValue > 0f)
                {
                    names.Add(GameText.Format("research.bonus.node", n.Name, Mathf.RoundToInt(n.EffectValue * 100f)));
                }
            }
            string pct = Mathf.RoundToInt((float)(total * 100.0)).ToString(CultureInfo.InvariantCulture);
            string list = string.Join(GameText.Get("stats.list_sep"), names);
            return capped
                ? GameText.Format(key, pct, list, Mathf.RoundToInt((float)(EfficiencyCap * 100.0)).ToString(CultureInfo.InvariantCulture))
                : GameText.Format(key, pct, list);
        }

        /// <summary>测试夹具：直接把这些节点记为已完成（不发通知、不标“新”）。只给自检 / 冒烟跳过与被测系统无关的研究过程用。</summary>
        public static void CompleteForTests(CampaignState state, params string[] nodeIds)
        {
            CampaignFgStateDomains.EnsureAll(state);
            var done = new List<string>(state.Research.CompletedNodes);
            foreach (string id in nodeIds)
            {
                if (ResearchCatalog.TryGet(id, out _) && !done.Contains(id))
                {
                    done.Add(id);
                }
            }
            state.Research.CompletedNodes = done.ToArray();
            Revision++;
        }
    }
}
