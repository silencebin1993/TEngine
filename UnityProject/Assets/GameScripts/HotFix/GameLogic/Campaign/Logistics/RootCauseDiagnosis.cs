using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG3-LOG-08：一条原因链属于哪一类。<b>多重根源时按这个顺序显示（数字小的在前）</b>：先是建筑本身（受损 / 关停 / 施工没完成），
    /// 再是电力（未接入 → 缺电），再是输入（缺料 / 没货 / 缺流体），最后是输出（堵塞 / 仓库满）。
    /// 理由（ADR-LOG-008，待用户复核）：按“先修哪一个才有意义”排——建筑本身不在运转时电和料都用不上；没电时有料也不开工；
    /// 有电没料时下游堵不堵无关紧要。与 Factorio 的状态优先级（没电 &gt; 缺料 &gt; 输出满）一致。
    /// </summary>
    public enum DiagCategory : byte
    {
        Structure = 0,
        Power = 1,
        Input = 2,
        Output = 3,
    }

    /// <summary>FG3-LOG-08：原因链里一步的种类（自检按它断言“追溯到了正确的根源”，不按文字）。</summary>
    public enum DiagCode : byte
    {
        Damaged,
        RepairNoLabor,
        Disabled,
        Unconnected,
        Brownout,
        GridOverload,
        SupplyDamaged,
        SupplyDisabled,
        GridShortRoot,
        OutputBlocked,
        BeltDownstream,
        BeltSaturated,
        BeltLoop,
        BeltTerminal,
        StoreFullRoot,
        PortTaken,
        StoreEmpty,
        StoreFull,
        QueueBlocked,
        QueueExitBlocked,
        SiteStalled,
        SiteMaterials,
        SiteNoLabor,
        SiteUnreachable,
        BeltNetwork,
        PipeNetwork,
        PipeNoSource,
        PipeLimited,
        PipeShortage,
        /// <summary>FG4-ECO-02：生产建筑待机（没选配方）/ 缺料 / 缺流体 / 输出堵塞 / 不在资源点上。</summary>
        ProdIdle,
        ProdStarved,
        ProdFluid,
        ProdBlocked,
        ProdNoResource,
        /// <summary>FG4-ECO-02（DEBT-FG3LOG08-03 跨生产建筑的缺料链）：顺着输入传送带找到的上游供货建筑，以及它为什么没在供货。</summary>
        ProdUpstream,
        /// <summary>输入传送带上没有能供应这种物品的建筑 / 仓库输出口。</summary>
        ProdNoSupplier,
        /// <summary>FG4-ECO-04：缺电的根源是这个电网里的燃油发电机没有燃油。</summary>
        SupplyNoFuel,
        /// <summary>FG4-ECO-04：缺电的根源是这个电网里的太阳能光照不足（夜晚 / 沙暴）。</summary>
        SupplyDark,
    }

    /// <summary>原因链里的一步：文字、能不能点、点了镜头去哪。</summary>
    public sealed class DiagStep
    {
        public DiagCode Code;
        public string Text;
        public bool HasPosition;
        public Vector2 Position;
        /// <summary>这一步指向的建筑（没有 = null）。</summary>
        public string TargetId;
    }

    /// <summary>一条原因链：从症状（第一步）读到根源（最后一步）。</summary>
    public sealed class DiagChain
    {
        public DiagCategory Category;
        public readonly List<DiagStep> Steps = new List<DiagStep>(4);
        public DiagStep Symptom => Steps.Count > 0 ? Steps[0] : null;
        public DiagStep Root => Steps.Count > 0 ? Steps[Steps.Count - 1] : null;
    }

    /// <summary>诊断对象的种类：建筑 / 施工现场 / 传送带网络 / 流体网络。</summary>
    public enum DiagSubject : byte
    {
        Building = 0,
        Site = 1,
        BeltNetwork = 2,
        PipeNetwork = 3,
    }

    /// <summary>一个停工对象的全部原因（已按 <see cref="DiagCategory"/> 排好序，第一条是主因）。</summary>
    public sealed class DiagReport
    {
        public DiagSubject Subject;
        public string SubjectId;
        public string Name;
        public Vector2 Position;
        public readonly List<DiagChain> Chains = new List<DiagChain>(2);
        public DiagChain Primary => Chains.Count > 0 ? Chains[0] : null;
    }

    /// <summary>
    /// FG3-LOG-08（FG03 FGR-LOG-082“为什么不工作”：每座停工的建筑都给出原因，并尽量追溯到根源，原因条目可以点击，镜头跳到对应位置；FGT-LOG-009）。
    ///
    /// 只读派生：诊断不改任何状态、不进存档，只读建筑记录、电网结算结果（<see cref="HomeValleyPowerGrid"/>）、端口与传送带内核（堵塞追溯在 AOT
    /// <see cref="BeltKernel.TryTraceBlock"/> / <see cref="BeltKernel.CollectBlockRoots"/>）、管线内核的网络读数、施工队列、生产 / 解析队列。
    /// 所以家园不被观察时的结果与观察时一致（读的是同一份状态），暂停 / 倍速不影响追溯结果（FGR-BASE-021）。
    ///
    /// 性能：整份诊断是 O(建筑数 + 端口数 + 施工单数 + 网络数)（逐格的循环在 AOT 内核里），只在“为什么不工作”面板、堵塞叠加层或选择器开着时进行，
    /// 而且<b>分帧执行</b>（<see cref="Refresh"/>）：每帧最多诊断 diag.slice_buildings 座建筑，施工 / 传送带网络 / 流体网络各占一帧，整轮做完才发布结果与版本号；
    /// 每轮开始间隔 diag.refresh_seconds 真实秒。所以任何一帧的热更层开销与建筑总数无关（FG03 第 7 节、帧率下限 120）。
    /// <see cref="Collect"/>（同步整份）只给自检与“打开面板时手上没有新结果”这种一次性场合用。悬停只诊断指着的那一座（O(这座的端口数 + 追溯格数)）。
    /// </summary>
    public static class RootCauseDiagnosis
    {
        private static readonly List<DiagReport> Cached = new List<DiagReport>(32);
        private static readonly List<BeltPortService.PortView> PortScratch = new List<BeltPortService.PortView>(4);
        private static readonly List<BeltCellInfo> RootScratch = new List<BeltCellInfo>(64);
        private static readonly List<HomeValleyConstruction.QueueEntry> QueueScratch = new List<HomeValleyConstruction.QueueEntry>(32);
        private static readonly List<Unity.Mathematics.int3> AnchorScratch = new List<Unity.Mathematics.int3>(16);
        private static CampaignState _cachedState;
        private static float _lastPassStart = float.NegativeInfinity;
        private static float _lastPublish = float.NegativeInfinity;
        private static int _lastSliceFrame = -1;
        private static ulong _cachedSignature;
        private static ulong _cachedStructure;

        private const int PhaseIdle = 0;
        private const int PhaseDown = 1;
        private const int PhaseBuildings = 2;
        private const int PhaseSites = 3;
        private const int PhaseBelts = 4;
        private const int PhasePipes = 5;
        private const int PhaseDone = 6;

        /// <summary>
        /// 一轮诊断的进度与中间结果。界面用的那一轮（<see cref="Live"/>）分帧推进；自检 / 一次性的同步整份用 <see cref="Sync"/>；悬停用 <see cref="HoverPass"/>
        /// （只诊断一座，不记录“已解释过的传送带源头”——那张表只给整份诊断去重用，悬停记录它只会让表无限变长）。
        /// </summary>
        private sealed class Pass
        {
            public CampaignState State;
            public BuildingRecord[] Buildings = Array.Empty<BuildingRecord>();
            public int Phase = PhaseIdle;
            public int Cursor;
            public readonly bool RecordTerminals;
            public readonly List<DiagReport> BuildingReports = new List<DiagReport>(16);
            public readonly List<DiagReport> Sites = new List<DiagReport>(8);
            public readonly List<DiagReport> Belts = new List<DiagReport>(4);
            public readonly List<DiagReport> Pipes = new List<DiagReport>(4);
            /// <summary>已经在某座建筑的原因链里解释过的传送带源头（同一个源头不在“传送带网络”里重复列）。</summary>
            public readonly HashSet<long> UsedTerminals = new HashSet<long>();
            /// <summary>每个电网里编号最小的受损 / 关停发电建筑（缺电链的根源）。</summary>
            public readonly Dictionary<int, BuildingRecord> Down = new Dictionary<int, BuildingRecord>();
            public bool DownValid;

            public Pass(bool recordTerminals)
            {
                RecordTerminals = recordTerminals;
            }

            public bool Active => Phase != PhaseIdle;

            public void Begin(CampaignState s)
            {
                State = s;
                Buildings = s?.BuildingRecords ?? Array.Empty<BuildingRecord>();
                Phase = PhaseDown;
                Cursor = 0;
                BuildingReports.Clear();
                Sites.Clear();
                Belts.Clear();
                Pipes.Clear();
                UsedTerminals.Clear();
                Down.Clear();
                DownValid = false;
            }

            /// <summary>结束这一轮并放掉对战役的引用（换存档后旧战役可以被回收）。</summary>
            public void End()
            {
                State = null;
                Buildings = Array.Empty<BuildingRecord>();
                Phase = PhaseIdle;
                Cursor = 0;
                BuildingReports.Clear();
                Sites.Clear();
                Belts.Clear();
                Pipes.Clear();
                UsedTerminals.Clear();
                Down.Clear();
                DownValid = false;
            }
        }

        private static readonly Pass Live = new Pass(true);
        private static readonly Pass Sync = new Pass(true);
        private static readonly Pass HoverPass = new Pass(false);

        /// <summary>最近一次发布的结果（按显示顺序：建筑 → 施工 → 传送带网络 → 流体网络）。</summary>
        public static IReadOnlyList<DiagReport> Reports => Cached;
        /// <summary>结果内容（含文字里的实时数字、位置）变了就 +1：界面按它原地更新文字。</summary>
        public static int Revision { get; private set; }
        /// <summary>结果的结构（对象、原因链的类别、每步的种类与步数）变了就 +1：界面只在它变时才重建列表。</summary>
        public static int StructureRevision { get; private set; }
        /// <summary>发布过几次结果（同步整份 + 分帧整轮）。</summary>
        public static int RefreshCount { get; private set; }
        /// <summary>最近一次<b>同步整份</b>诊断的耗时（自检 / 打开面板时手上没有新结果）。</summary>
        public static double LastCollectMs { get; private set; }
        /// <summary>分帧诊断：最近一帧、历史最大一帧的耗时（毫秒），最近一整轮用了几帧。</summary>
        public static double LastSliceMs { get; private set; }
        public static double MaxSliceMs { get; private set; }
        public static int LastPassFrames { get; private set; }
        private static int _passFrames;
        /// <summary>最近一次发布里被截掉（超过 diag.max_reports）的对象数。</summary>
        public static int Truncated { get; private set; }

        /// <summary>让下一次 <see cref="Refresh"/> 立刻开始新的一轮（正在进行的一轮作废）。</summary>
        public static void Invalidate()
        {
            Live.End();
            _lastPassStart = float.NegativeInfinity;
        }

        public static void ResetForTests()
        {
            Cached.Clear();
            Live.End();
            Sync.End();
            HoverPass.End();
            _cachedState = null;
            _cachedSignature = 0;
            _cachedStructure = 0;
            _lastPassStart = float.NegativeInfinity;
            _lastPublish = float.NegativeInfinity;
            _lastSliceFrame = -1;
            MaxSliceMs = 0;
            LastSliceMs = 0;
            Revision++;
            StructureRevision++;
        }

        private static float RefreshSeconds => Mathf.Max(0.1f, GridContent.Tuning("diag.refresh_seconds"));
        private static int MaxReports => Math.Max(8, GridContent.TuningInt("diag.max_reports"));
        private static int MaxBeltRoots => Math.Max(4, GridContent.TuningInt("diag.max_belt_roots"));
        private static int SliceBuildings => Math.Max(4, GridContent.TuningInt("diag.slice_buildings"));

        /// <summary>
        /// 面板 / 叠加层 / 选择器开着时每帧调用：推进一帧的分帧诊断（同一帧里多处调用只推进一次）；整轮做完才发布到 <see cref="Reports"/>，返回这次有没有发布。
        /// 每轮开始间隔 diag.refresh_seconds 真实秒；换了战役立刻重开一轮。<paramref name="force"/> = 同步整份（一次性场合与自检用）。
        /// </summary>
        public static bool Refresh(CampaignState state, bool force = false)
        {
            if (force)
            {
                Live.End();
                float now = Time.realtimeSinceStartup;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Collect(state, Cached);
                LastCollectMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _lastPassStart = now;
                PublishDone(state, now);
                return true;
            }
            int frame = Time.frameCount;
            if (frame == _lastSliceFrame)
            {
                return false;
            }
            _lastSliceFrame = frame;
            return Step(state);
        }

        /// <summary>自检：推进一帧的分帧诊断（不看帧号——编辑模式下帧号不走）。</summary>
        public static bool StepForTests(CampaignState state) => Step(state);

        /// <summary>手上有没有这个战役刚发布的结果（不超过两个刷新间隔）。打开面板时有就先显示它、并立刻开始新的一轮；没有才同步整份。</summary>
        public static bool HasFreshResult(CampaignState state) =>
            state != null && ReferenceEquals(state, _cachedState) && Time.realtimeSinceStartup - _lastPublish < RefreshSeconds * 2f;

        private static bool Step(CampaignState state)
        {
            float now = Time.realtimeSinceStartup;
            if (!Live.Active || !ReferenceEquals(Live.State, state))
            {
                Live.End();
                if (!ReferenceEquals(state, _cachedState) && Cached.Count > 0)
                {
                    // 换了战役：旧战役的结果不再显示（新的一轮做完之前清单是空的）。
                    Cached.Clear();
                    Truncated = 0;
                    _cachedState = null;
                    _cachedSignature = 0;
                    _cachedStructure = 0;
                    Revision++;
                    StructureRevision++;
                }
                if (ReferenceEquals(state, _cachedState) && now - _lastPassStart < RefreshSeconds)
                {
                    return false;
                }
                Live.Begin(state);
                _lastPassStart = now;
                _passFrames = 0;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _passFrames++;
            bool done = Advance(Live, SliceBuildings);
            if (done)
            {
                Publish(Live, Cached);
                Live.End();
            }
            LastSliceMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastSliceMs > MaxSliceMs)
            {
                MaxSliceMs = LastSliceMs;
            }
            if (!done)
            {
                return false;
            }
            LastPassFrames = _passFrames;
            PublishDone(state, now);
            return true;
        }

        private static void PublishDone(CampaignState state, float now)
        {
            _cachedState = state;
            _lastPublish = now;
            RefreshCount++;
            Signature(Cached, out ulong sig, out ulong structure);
            if (sig != _cachedSignature)
            {
                _cachedSignature = sig;
                Revision++;
            }
            if (structure != _cachedStructure)
            {
                _cachedStructure = structure;
                StructureRevision++;
            }
        }

        /// <summary>
        /// 推进一轮诊断。<paramref name="budget"/> = 这一帧最多诊断几座建筑（int.MaxValue = 一次做完）。建“受损发电建筑表”每座按 1/4 份计；
        /// 施工、传送带网络、流体网络各占新的一帧（它们各自是 O(施工单数) / AOT 源头扫描 / O(网络数)）。做完返回 true。
        /// </summary>
        private static bool Advance(Pass p, int budget)
        {
            bool unlimited = budget == int.MaxValue;
            int limit = unlimited ? int.MaxValue : budget * 4;
            int units = 0;
            CampaignState state = p.State;
            if (state == null)
            {
                p.Phase = PhaseDone;
                return true;
            }
            while (true)
            {
                switch (p.Phase)
                {
                    case PhaseDown:
                        while (p.Cursor < p.Buildings.Length)
                        {
                            if (units >= limit)
                            {
                                return false;
                            }
                            AddDown(p, p.Buildings[p.Cursor++]);
                            units++;
                        }
                        p.DownValid = true;
                        p.Phase = PhaseBuildings;
                        p.Cursor = 0;
                        break;
                    case PhaseBuildings:
                        while (p.Cursor < p.Buildings.Length)
                        {
                            if (units >= limit)
                            {
                                return false;
                            }
                            DiagReport r = DiagnoseBuildingCore(p, p.Buildings[p.Cursor++]);
                            if (r != null)
                            {
                                p.BuildingReports.Add(r);
                            }
                            units += 4;
                        }
                        p.Phase = PhaseSites;
                        break;
                    case PhaseSites:
                        if (!unlimited && units > 0)
                        {
                            return false;
                        }
                        CollectSites(state, p.Sites);
                        p.Phase = PhaseBelts;
                        units = unlimited ? 0 : limit;
                        break;
                    case PhaseBelts:
                        if (!unlimited && units > 0)
                        {
                            return false;
                        }
                        CollectBeltNetworks(p, p.Belts);
                        p.Phase = PhasePipes;
                        units = unlimited ? 0 : limit;
                        break;
                    case PhasePipes:
                        if (!unlimited && units > 0)
                        {
                            return false;
                        }
                        CollectPipeNetworks(state, p.Pipes);
                        p.Phase = PhaseDone;
                        return true;
                    default:
                        return true;
                }
            }
        }

        /// <summary>把一轮的结果按显示顺序写进 <paramref name="into"/>（先清空）：建筑按主因类别、再按建筑编号（稳定顺序，与观察与否无关）→ 施工 → 传送带网络 → 流体网络。</summary>
        private static void Publish(Pass p, List<DiagReport> into)
        {
            into.Clear();
            Truncated = 0;
            int max = MaxReports;
            p.BuildingReports.Sort((a, c) =>
            {
                int ca = (int)a.Primary.Category;
                int cc = (int)c.Primary.Category;
                return ca != cc ? ca.CompareTo(cc) : string.CompareOrdinal(a.SubjectId, c.SubjectId);
            });
            AddCapped(into, p.BuildingReports, max);
            AddCapped(into, p.Sites, max);
            AddCapped(into, p.Belts, max);
            AddCapped(into, p.Pipes, max);
        }

        /// <summary>全部停工对象的诊断（按显示顺序），同步一次做完。<paramref name="into"/> 先清空。自检与一次性场合用；界面每帧走 <see cref="Refresh"/> 的分帧版本。</summary>
        public static void Collect(CampaignState state, List<DiagReport> into)
        {
            if (state == null)
            {
                into.Clear();
                Truncated = 0;
                return;
            }
            Sync.Begin(state);
            Advance(Sync, int.MaxValue);
            Publish(Sync, into);
            Sync.End();
        }

        private static void AddCapped(List<DiagReport> into, List<DiagReport> add, int max)
        {
            foreach (DiagReport r in add)
            {
                if (into.Count < max)
                {
                    into.Add(r);
                }
                else
                {
                    Truncated++;
                }
            }
        }

        // ── 建筑 ───────────────────────────────────────────────────────────────

        /// <summary>一座建筑的诊断；没有问题（或不是家园里在运转 / 应当运转的建筑）返回 null。O(这座的端口数 + 追溯格数)；缺电时另 O(建筑数) 找受损的发电建筑。</summary>
        public static DiagReport DiagnoseBuilding(CampaignState state, BuildingRecord b)
        {
            if (state == null)
            {
                return null;
            }
            HoverPass.Begin(state);
            DiagReport r = DiagnoseBuildingCore(HoverPass, b);
            HoverPass.End();
            return r;
        }

        private static DiagReport DiagnoseBuildingCore(Pass p, BuildingRecord b)
        {
            CampaignState state = p.State;
            if (state == null || b == null || b.RegionId != HomeValleyLayout.RegionId || HomeValleyController.IsPlannedGhost(b)
                || b.ConstructionState == BuildingConstructionState.Destroyed || HomeGridService.IsRelocationGhost(b))
            {
                return null;
            }
            var report = new DiagReport
            {
                Subject = DiagSubject.Building,
                SubjectId = b.BuildingId,
                Name = HomeGridService.DisplayName(b.BuildingTypeId),
                Position = b.Position,
            };
            AddStructure(state, b, report);
            if (b.ConstructionState == BuildingConstructionState.Operational)
            {
                DiagChain power = PowerChain(p, b, report.Name);
                if (power != null)
                {
                    report.Chains.Add(power);
                }
                AddPorts(p, b, report);
                AddQueues(state, b, report);
                AddProduction(p, b, report);
            }
            if (report.Chains.Count == 0)
            {
                return null;
            }
            // 多重根源：稳定排序（同类按加入顺序）。
            SortChains(report.Chains);
            return report;
        }

        private static void SortChains(List<DiagChain> chains)
        {
            for (int i = 1; i < chains.Count; i++)
            {
                DiagChain c = chains[i];
                int j = i - 1;
                while (j >= 0 && chains[j].Category > c.Category)
                {
                    chains[j + 1] = chains[j];
                    j--;
                }
                chains[j + 1] = c;
            }
        }

        private static void AddStructure(CampaignState state, BuildingRecord b, DiagReport report)
        {
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                DiagChain c = NewChain(DiagCategory.Structure);
                Step(c, DiagCode.Damaged, GameText.Format("diag.step.damaged", report.Name), b.Position, b.BuildingId);
                if (HomeValleyConstruction.NoLabor)
                {
                    Step(c, DiagCode.RepairNoLabor, GameText.Get("diag.step.repair_no_labor"), b.Position, b.BuildingId);
                }
                report.Chains.Add(c);
            }
            else if (b.ConstructionState == BuildingConstructionState.Disabled)
            {
                DiagChain c = NewChain(DiagCategory.Structure);
                Step(c, DiagCode.Disabled, GameText.Format("diag.step.disabled", report.Name), b.Position, b.BuildingId);
                report.Chains.Add(c);
            }
        }

        /// <summary>
        /// 电力链：未接入电网 → 根源是这座建筑本身不在覆盖里；缺电 → 所在电网超载 → 根源是这个电网里受损 / 关停的发电建筑（有的话），否则“发电不够”。
        /// 只对运转中的用电建筑；读 <see cref="BuildingRecord.PowerState"/>（电网结算结果）与 <see cref="HomeValleyPowerGrid"/> 的子网读数，O(建筑数) 找发电建筑只在缺电时。
        /// </summary>
        private static DiagChain PowerChain(Pass p, BuildingRecord b, string name)
        {
            CampaignState state = p.State;
            if (b == null || !HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
            {
                return null;
            }
            if (b.PowerState == BuildingPowerState.Unpowered)
            {
                DiagChain c = NewChain(DiagCategory.Power);
                Step(c, DiagCode.Unconnected, GameText.Format("diag.step.unconnected", name), b.Position, b.BuildingId);
                return c;
            }
            if (b.PowerState != BuildingPowerState.Brownout)
            {
                return null;
            }
            DiagChain chain = NewChain(DiagCategory.Power);
            Step(chain, DiagCode.Brownout, GameText.Format("diag.step.brownout", name, b.PowerPriority), b.Position, b.BuildingId);
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            if (k == null || !HomeValleyPowerGrid.TryGetBuildingPower(state, b.BuildingId, out BuildingPowerInfo info) || info.Subnet < 0)
            {
                return chain;
            }
            PowerSubnetInfo n = k.Subnet(info.Subnet);
            string gridName = HomeValleyPowerGrid.SubnetName(n.Serial);
            Vector2 anchor = HomeValleyPowerGrid.SubnetAnchorPosition(info.Subnet);
            Step(chain, DiagCode.GridOverload, GameText.Format("diag.step.grid_overload", gridName, HomeValleyPowerGrid.Num(n.Supply),
                HomeValleyPowerGrid.Num(n.Demand), HomeValleyPowerGrid.Num(Mathf.Max(0f, n.Demand - n.Delivered))), anchor, null);
            // 根源：这个电网里受损 / 被关停的发电建筑（修好 / 重新启用就能补上缺口）；按建筑编号取第一座（稳定）。
            // 一次诊断里每个电网只找一遍（O(建筑数) 建表——分帧诊断时建表本身也分帧，见 Advance——之后每座缺电建筑 O(1)）。
            EnsureDownMap(p);
            p.Down.TryGetValue(info.Subnet, out BuildingRecord down);
            if (down != null && down.ConstructionState == BuildingConstructionState.Operational)
            {
                // FG4-ECO-04：运转中的发电建筑出不了力——燃油发电机没油 / 太阳能光照不足。
                string gen = HomeGridService.DisplayName(down.BuildingTypeId);
                bool fuel = HomeValleyPowerGrid.IsFuelGeneratorType(down.BuildingTypeId);
                Step(chain, fuel ? DiagCode.SupplyNoFuel : DiagCode.SupplyDark,
                    fuel ? GameText.Format("diag.step.supply_no_fuel", gen, gridName)
                         : GameText.Format("diag.step.supply_dark", gen, gridName, HomeValleyPowerGrid.Num(HomeValleyPowerGrid.AvailableSupplyOf(state, down.BuildingId))),
                    down.Position, down.BuildingId);
            }
            else if (down != null)
            {
                string gen = HomeGridService.DisplayName(down.BuildingTypeId);
                Step(chain, down.ConstructionState == BuildingConstructionState.Damaged ? DiagCode.SupplyDamaged : DiagCode.SupplyDisabled,
                    GameText.Format(down.ConstructionState == BuildingConstructionState.Damaged ? "diag.step.supply_damaged" : "diag.step.supply_disabled", gen, gridName),
                    down.Position, down.BuildingId);
            }
            else
            {
                Step(chain, DiagCode.GridShortRoot, GameText.Format("diag.step.grid_short_root", gridName), anchor, null);
            }
            return chain;
        }

        /// <summary>端口：输出口推不出去（追溯到下游源头）、输出口登记冲突、输出口没货、输入口仓库满。</summary>
        private static void AddPorts(Pass p, BuildingRecord b, DiagReport report)
        {
            CampaignState state = p.State;
            if (GridContent.PortsOf(b.BuildingTypeId).Count == 0)
            {
                return;
            }
            BeltPortService.CollectViews(state, b, PortScratch);
            BeltKernel k = BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) ? BeltNetworkService.Kernel : null;
            foreach (BeltPortService.PortView v in PortScratch)
            {
                if (!v.Active || !v.Store)
                {
                    continue;
                }
                string side = GameText.Get(GridMath.DirTextKey(v.Face));
                if (v.IsOutput && !v.Bound && k != null && BeltPortService.TryFindSourceAt(v.BeltCell, out BeltPortService.Binding other) && other.BuildingId != b.BuildingId)
                {
                    // DEBT-FG3LOG03-09：同一格只能登记一个输出口——写明被谁占了。
                    DiagChain c = NewChain(DiagCategory.Output);
                    Step(c, DiagCode.PortTaken, GameText.Format("diag.step.port_taken", report.Name, v.BeltCell.X, v.BeltCell.Y, BeltPortService.BuildingName(other.BuildingId)),
                        CellPos(v.BeltCell), other.BuildingId);
                    report.Chains.Add(c);
                    continue;
                }
                if (!v.Bound || !v.Connected || k == null)
                {
                    continue;
                }
                if (v.IsOutput)
                {
                    if (v.Info.Pending > 0 && k.TryGetCellInfo(v.BeltCell.X, v.BeltCell.Y, out BeltCellInfo head) && head.Block != BeltBlock.None && head.Count > 0)
                    {
                        DiagChain c = NewChain(DiagCategory.Output);
                        Step(c, DiagCode.OutputBlocked, GameText.Format("diag.step.output_blocked", report.Name, side, v.BeltCell.X, v.BeltCell.Y), b.Position, b.BuildingId);
                        AppendBeltTrace(p, k, c, v.BeltCell);
                        report.Chains.Add(c);
                    }
                    else if (v.Filter != BeltPortService.FilterOff && v.Info.Pending <= 0 && BeltPortService.OutputStockEmpty(state, v.Filter))
                    {
                        DiagChain c = NewChain(DiagCategory.Input);
                        string what = v.Filter == BeltPortService.FilterAll ? GameText.Get("logistics.port.filter_all") : BeltItems.Name(BeltPortService.ItemForFilter(v.Filter));
                        Step(c, DiagCode.StoreEmpty, GameText.Format("diag.step.store_empty", report.Name, what), b.Position, b.BuildingId);
                        report.Chains.Add(c);
                    }
                }
                else if (v.PortId >= 0 && BeltPortService.StoreFull(state, v.PortId, out Economy.ItemDef kind))
                {
                    // FG4-ECO-01：按输入口缓存里那一种判断（每种物品的容量各算各的）。
                    DiagChain c = NewChain(DiagCategory.Output);
                    Step(c, DiagCode.StoreFull, GameText.Format("diag.step.store_full_item", report.Name, kind.Name, Economy.HomeInventory.Stock(state, kind),
                        Economy.HomeInventory.Capacity(state, kind)), b.Position, b.BuildingId);
                    report.Chains.Add(c);
                }
            }
        }

        /// <summary>
        /// FG4-ECO-02：生产建筑（缺电由电力链负责，受损 / 关停由建筑链负责）：待机 / 不在资源点上 / 缺料 / 缺流体 / 输出堵塞。
        /// 缺固体时顺着输入口的来料传送带找上游供货建筑（同一个传送带网络上、产出这种物品的建筑输出口或仓库输出口），再接上那座建筑自己的状态与原因——
        /// 例如“精炼炉缺金属矿 → 上游提取钻没电”（DEBT-FG3LOG08-03，接口见 ADR-LOG-008“对后续 Story 的约定”）。输出堵塞时顺着输出传送带追到堵点。
        /// O(端口数)（找上游只在缺料时），分帧诊断按建筑切片调用。
        /// </summary>
        private static void AddProduction(Pass p, BuildingRecord b, DiagReport report)
        {
            CampaignState state = p.State;
            if (!Economy.ProductionService.TryGet(state, b.BuildingId, out Economy.ProductionService.Producer pr))
            {
                return;
            }
            string reason = Economy.ProductionService.ReasonText(state, pr).Replace("\n", " · ");
            string head = GameText.Format("diag.step.prod_state", report.Name, Economy.ProductionService.StateText(pr), reason);
            switch (pr.State)
            {
                case Economy.ProdState.Idle:
                {
                    DiagChain c = NewChain(DiagCategory.Input);
                    Step(c, DiagCode.ProdIdle, head, b.Position, b.BuildingId);
                    report.Chains.Add(c);
                    break;
                }
                case Economy.ProdState.NoResource:
                {
                    DiagChain c = NewChain(DiagCategory.Structure);
                    Step(c, DiagCode.ProdNoResource, head, b.Position, b.BuildingId);
                    report.Chains.Add(c);
                    break;
                }
                case Economy.ProdState.MissingFluid:
                {
                    DiagChain c = NewChain(DiagCategory.Input);
                    Step(c, DiagCode.ProdFluid, head, b.Position, b.BuildingId);
                    report.Chains.Add(c);
                    break;
                }
                case Economy.ProdState.MissingInput:
                {
                    DiagChain c = NewChain(DiagCategory.Input);
                    Step(c, DiagCode.ProdStarved, head, b.Position, b.BuildingId);
                    AppendUpstream(p, pr, c);
                    report.Chains.Add(c);
                    break;
                }
                case Economy.ProdState.OutputBlocked:
                {
                    DiagChain c = NewChain(DiagCategory.Output);
                    Step(c, DiagCode.ProdBlocked, head, b.Position, b.BuildingId);
                    BeltKernel k = BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) ? BeltNetworkService.Kernel : null;
                    BeltPortService.Binding outBind = pr.ReasonPort < 0 ? Economy.ProductionService.FindPort(pr, true) : null;
                    if (k != null && outBind != null && outBind.PortId >= 0 && k.TryGetPortInfo(outBind.PortId, out BeltPortInfo info) && info.Connected
                        && k.TryGetCellInfo(outBind.BeltCell.X, outBind.BeltCell.Y, out BeltCellInfo headCell) && headCell.Block != BeltBlock.None && headCell.Count > 0)
                    {
                        AppendBeltTrace(p, k, c, outBind.BeltCell);
                    }
                    report.Chains.Add(c);
                    break;
                }
            }
        }

        /// <summary>缺固体：输入传送带 → 同一网络上产出这种物品的建筑输出口 / 仓库输出口 → 那座建筑为什么没在供货（根源）。</summary>
        private static void AppendUpstream(Pass p, Economy.ProductionService.Producer pr, DiagChain c)
        {
            CampaignState state = p.State;
            Economy.ItemDef want = pr.ReasonItem;
            BeltPortService.Binding inBind = Economy.ProductionService.FindInPortFor(pr, want); // FG4-ECO-03：多输入口建筑追“收这种物品的那个口”的来料带
            BeltKernel k = BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) ? BeltNetworkService.Kernel : null;
            if (want == null || inBind == null || k == null || inBind.PortId < 0 || !k.TryGetPortInfo(inBind.PortId, out BeltPortInfo info) || !info.Connected)
            {
                return; // 没接传送带：原因（第一步）已经写明要在哪里铺。
            }
            int net = k.NetworkOf(inBind.BeltCell.X, inBind.BeltCell.Y);
            // 生产建筑按它输出口外那一格找（关停 / 受损 / 没电的建筑端口已经撤掉，不能只看端口绑定）。
            foreach (Economy.ProductionService.Producer up in Economy.ProductionService.All)
            {
                if (up.Id == pr.Id || !Produces(up, want))
                {
                    continue;
                }
                HomeGridService.PortsOf(up.Building, PortPlacements);
                bool onNet = false;
                foreach (PortPlacement pp in PortPlacements)
                {
                    if (pp.IsOutput)
                    {
                        Vector2Int v = GridMath.DirVector(pp.Dir);
                        onNet |= k.NetworkOf(pp.Cell.X + v.x, pp.Cell.Y + v.y) == net;
                    }
                }
                if (onNet)
                {
                    BuildingRecord ub = up.Building;
                    string upName = HomeGridService.DisplayName(ub.BuildingTypeId);
                    if (up.State == Economy.ProdState.Working)
                    {
                        Step(c, DiagCode.ProdUpstream, GameText.Format("diag.step.prod_upstream_working", upName, ub.GridX, ub.GridY, want.Name), ub.Position, ub.BuildingId);
                    }
                    else
                    {
                        Step(c, DiagCode.ProdUpstream, GameText.Format("diag.step.prod_upstream", upName, ub.GridX, ub.GridY, Economy.ProductionService.StateText(up),
                            Economy.ProductionService.ReasonText(state, up).Replace("\n", " · ")), ub.Position, ub.BuildingId);
                    }
                    return;
                }
            }
            BeltPortService.Binding store = null;
            foreach (BeltPortService.Binding s in BeltPortService.All)
            {
                if (s.IsOutput && s.Store && s.PortId >= 0 && s.Record != null && (s.Record.Filter == BeltPortService.FilterAll || s.Record.Filter == want.BeltId)
                    && k.NetworkOf(s.BeltCell.X, s.BeltCell.Y) == net)
                {
                    store = s;
                    break;
                }
            }
            if (store != null)
            {
                BuildingRecord sb = HomeGridService.FindBuilding(state, store.BuildingId);
                if (Economy.HomeInventory.Stock(state, want) <= 0)
                {
                    Step(c, DiagCode.StoreEmpty, GameText.Format("diag.step.prod_store_empty", BeltPortService.BuildingName(store.BuildingId), want.Name),
                        sb?.Position ?? CellPos(store.PortCell), store.BuildingId);
                    return;
                }
                Step(c, DiagCode.ProdUpstream, GameText.Format("diag.step.prod_upstream_working", BeltPortService.BuildingName(store.BuildingId),
                    sb?.GridX ?? store.PortCell.X, sb?.GridY ?? store.PortCell.Y, want.Name), sb?.Position ?? CellPos(store.PortCell), store.BuildingId);
                return;
            }
            Step(c, DiagCode.ProdNoSupplier, GameText.Format("diag.step.prod_no_supplier", want.Name), CellPos(inBind.BeltCell), null);
        }

        private static readonly List<PortPlacement> PortPlacements = new List<PortPlacement>(4);

        private static bool Produces(Economy.ProductionService.Producer up, Economy.ItemDef want)
        {
            switch (up.Def.Mode)
            {
                case Economy.ProducerMode.Drill:
                    return ReferenceEquals(up.VeinOre, want);
                case Economy.ProducerMode.Recycler:
                    return want.Id == Economy.ItemCatalog.ScrapId;
                case Economy.ProducerMode.Recipe:
                    if (up.Recipe != null)
                    {
                        foreach (Economy.RecipeLine l in up.Recipe.Lines)
                        {
                            if (l.Role != Economy.RecipeRole.In && ReferenceEquals(l.Item, want))
                            {
                                return true;
                            }
                        }
                    }
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>从传送带格 <paramref name="start"/> 顺着下游追到源头（AOT），把中间一步与根源写进链里。</summary>
        private static void AppendBeltTrace(Pass p, BeltKernel k, DiagChain c, GridCell start)
        {
            CampaignState state = p.State;
            if (!k.TryTraceBlock(start.X, start.Y, out BeltBlockTrace t))
            {
                return;
            }
            BeltCellInfo end = t.End;
            var endCell = new GridCell(end.X, end.Y);
            if (p.RecordTerminals)
            {
                p.UsedTerminals.Add(CellKey(end.X, end.Y));
            }
            if (t.Looped)
            {
                Step(c, DiagCode.BeltLoop, GameText.Format("diag.step.belt_loop", start.X, start.Y), CellPos(start), null);
                return;
            }
            if (t.Hops > 0)
            {
                Step(c, DiagCode.BeltDownstream, GameText.Format("diag.step.belt_downstream", end.X, end.Y, t.Hops), CellPos(endCell), null);
            }
            if (end.Block == BeltBlock.None)
            {
                Step(c, DiagCode.BeltSaturated, GameText.Format("diag.step.belt_saturated", end.X, end.Y, end.RatedItemsPerMinute), CellPos(endCell), null);
                return;
            }
            if ((end.Block == BeltBlock.SinkFull || end.Block == BeltBlock.SinkRejects) && BeltPortService.TryGetBinding(end.SinkPortId, out BeltPortService.Binding sink))
            {
                BuildingRecord owner = HomeGridService.FindBuilding(state, sink.BuildingId);
                Vector2 at = owner?.Position ?? CellPos(endCell);
                if (end.Block == BeltBlock.SinkFull && sink.Store && BeltPortService.StoreFull(state, sink.PortId, out Economy.ItemDef kind))
                {
                    Step(c, DiagCode.StoreFullRoot, GameText.Format("diag.step.store_full_item", BeltPortService.BuildingName(sink.BuildingId), kind.Name,
                        Economy.HomeInventory.Stock(state, kind), Economy.HomeInventory.Capacity(state, kind)), at, sink.BuildingId);
                    return;
                }
                Step(c, DiagCode.BeltTerminal, GameText.Format("diag.step.belt_terminal", end.X, end.Y, BeltNetworkService.DescribeBlock(end)), at, sink.BuildingId);
                return;
            }
            Step(c, DiagCode.BeltTerminal, GameText.Format("diag.step.belt_terminal", end.X, end.Y, BeltNetworkService.DescribeBlock(end)), CellPos(endCell), null);
        }

        /// <summary>装配站的生产队列、解析台的解析队列：卡住的项（每种卡法一条链；缺电时接上这座建筑的电力链，不重复列两条）。</summary>
        private static void AddQueues(CampaignState state, BuildingRecord b, DiagReport report)
        {
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation)
            {
                bool power = false, exit = false, resources = false, target = false;
                foreach (FactoryQueueItemRecord it in state.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>())
                {
                    if (it == null)
                    {
                        continue;
                    }
                    switch (it.State)
                    {
                        case FactoryQueueState.WaitingPower when !power:
                            power = true;
                            AddQueuePowerChain(state, b, report);
                            break;
                        case FactoryQueueState.OutputBlocked when !exit:
                        {
                            exit = true;
                            DiagChain c = NewChain(DiagCategory.Output);
                            Step(c, DiagCode.QueueExitBlocked, GameText.Format("diag.step.queue_exit", report.Name), b.Position, b.BuildingId);
                            report.Chains.Add(c);
                            break;
                        }
                        case FactoryQueueState.WaitingResources when !resources:
                        case FactoryQueueState.WaitingTarget when !target:
                        {
                            bool res = it.State == FactoryQueueState.WaitingResources;
                            resources |= res;
                            target |= !res;
                            DiagChain c = NewChain(res ? DiagCategory.Input : DiagCategory.Structure);
                            Step(c, DiagCode.QueueBlocked, GameText.Format("diag.step.queue_blocked", report.Name,
                                HomeValleyFactory.DescribeWait(it) ?? HomeValleyFactory.DescribeFailure(it.BlockedReason)), b.Position, b.BuildingId);
                            report.Chains.Add(c);
                            break;
                        }
                    }
                }
            }
            else if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench)
            {
                foreach (AnalysisQueueItemRecord it in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
                {
                    if (it != null && it.State == AnalysisQueueState.WaitingPower)
                    {
                        AddQueuePowerChain(state, b, report);
                        return;
                    }
                }
            }
        }

        /// <summary>队列因为没电停住：“队列停住” → 这座建筑的电力链（未接入 / 缺电 → 电网超载 → 发电机受损）。
        /// 建筑自己的电力链已经在报告里时，把“队列停住”加到那条链的最前面（不重复列两条）。</summary>
        private static void AddQueuePowerChain(CampaignState state, BuildingRecord b, DiagReport report)
        {
            DiagChain existing = null;
            foreach (DiagChain ch in report.Chains)
            {
                if (ch.Category == DiagCategory.Power)
                {
                    existing = ch;
                    break;
                }
            }
            var head = new DiagStep
            {
                Code = DiagCode.QueueBlocked,
                Text = GameText.Format("diag.step.queue_blocked", report.Name, GameText.Get("diag.step.queue_no_power")),
                HasPosition = true,
                Position = b.Position,
                TargetId = b.BuildingId,
            };
            if (existing != null)
            {
                existing.Steps.Insert(0, head);
                return;
            }
            DiagChain c = NewChain(DiagCategory.Power);
            c.Steps.Add(head);
            report.Chains.Add(c);
        }

        /// <summary>悬停（只诊断一座）时才会走到这里整张建表；整份诊断在 <see cref="Advance"/> 的第一段逐帧建好。</summary>
        private static void EnsureDownMap(Pass p)
        {
            if (p.DownValid)
            {
                return;
            }
            p.DownValid = true;
            p.Down.Clear();
            foreach (BuildingRecord g in p.Buildings)
            {
                AddDown(p, g);
            }
        }

        private static void AddDown(Pass p, BuildingRecord g)
        {
            if (g == null || g.RegionId != HomeValleyLayout.RegionId || !HomeValleyLayout.PowerSupplyProfile.ContainsKey(g.BuildingTypeId)
                || !HomeValleyPowerGrid.TryGetBuildingPower(p.State, g.BuildingId, out BuildingPowerInfo gi) || gi.Subnet < 0)
            {
                return;
            }
            // 发电建筑少发的电：受损 / 关停 = 满额全丢；FG4-ECO-04：运转中但出不了满额的（燃油发电机没油、太阳能光照不足）= 满额 − 现在能发的。
            // 每个电网取少发最多的那一座当根源（补上它最能缓解缺口）；一样多时按建筑编号取第一座（稳定）。
            float lost = Lost(p, g);
            if (lost <= 0.01f)
            {
                return;
            }
            if (!p.Down.TryGetValue(gi.Subnet, out BuildingRecord have))
            {
                p.Down[gi.Subnet] = g;
                return;
            }
            float haveLost = Lost(p, have);
            if (lost > haveLost + 0.01f || (Math.Abs(lost - haveLost) <= 0.01f && string.CompareOrdinal(g.BuildingId, have.BuildingId) < 0))
            {
                p.Down[gi.Subnet] = g;
            }
        }

        /// <summary>这座发电建筑少发的电（满额 − 现在能发的；受损 / 关停 = 满额；运转中的燃油没油 / 太阳能光照不足按电网实际系数算）。</summary>
        private static float Lost(Pass p, BuildingRecord g)
        {
            if (!HomeValleyLayout.PowerSupplyProfile.TryGetValue(g.BuildingTypeId, out float full))
            {
                return 0f;
            }
            if (g.ConstructionState == BuildingConstructionState.Damaged || g.ConstructionState == BuildingConstructionState.Disabled)
            {
                return full;
            }
            return g.ConstructionState == BuildingConstructionState.Operational ? Math.Max(0f, full - HomeValleyPowerGrid.AvailableSupplyOf(p.State, g.BuildingId)) : 0f;
        }

        private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        // ── 施工 ───────────────────────────────────────────────────────────────

        private static void CollectSites(CampaignState state, List<DiagReport> into)
        {
            HomeValleyConstruction.CollectQueue(state, QueueScratch);
            foreach (HomeValleyConstruction.QueueEntry e in QueueScratch)
            {
                WorkOrderRecord o = e.Order;
                if (o == null)
                {
                    continue;
                }
                DiagCode code;
                DiagCategory cat;
                string reason = o.FailureReason;
                if (o.State == WorkOrderState.Waiting && reason != null && reason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, StringComparison.Ordinal))
                {
                    code = DiagCode.SiteMaterials;
                    cat = DiagCategory.Input;
                }
                else if (HomeValleyWorkOrders.IsUnreachableReason(reason) || (o.State == WorkOrderState.Waiting && reason == "path-blocked"))
                {
                    code = DiagCode.SiteUnreachable;
                    cat = DiagCategory.Structure;
                }
                else if ((o.State == WorkOrderState.Ready || o.State == WorkOrderState.Waiting) && HomeValleyConstruction.NoLabor)
                {
                    code = DiagCode.SiteNoLabor;
                    cat = DiagCategory.Structure;
                }
                else
                {
                    continue;
                }
                var r = new DiagReport { Subject = DiagSubject.Site, SubjectId = o.WorkOrderId, Name = e.Name, Position = e.Position };
                DiagChain c = NewChain(cat);
                Step(c, DiagCode.SiteStalled, GameText.Format("diag.step.site", e.Name), e.Position, o.TargetId);
                Step(c, code, e.Status, e.Position, o.TargetId);
                r.Chains.Add(c);
                into.Add(r);
            }
        }

        // ── 传送带网络 ─────────────────────────────────────────────────────────

        private static void CollectBeltNetworks(Pass p, List<DiagReport> into)
        {
            CampaignState state = p.State;
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                return;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            k.CollectBlockRoots(RootScratch, MaxBeltRoots);
            if (RootScratch.Count == 0)
            {
                return;
            }
            DiagReport current = null;
            int currentNet = int.MinValue;
            foreach (BeltCellInfo root in RootScratch)
            {
                if (p.UsedTerminals.Contains(CellKey(root.X, root.Y)))
                {
                    continue; // 已经在某座建筑的原因链里解释过（同一个源头不重复列）。
                }
                if (root.Network != currentNet || current == null)
                {
                    current = null;
                    foreach (DiagReport r in into)
                    {
                        if (r.SubjectId == "belt-net:" + root.Network)
                        {
                            current = r;
                            break;
                        }
                    }
                    if (current == null)
                    {
                        k.TryGetNetworkStats(root.Network, out BeltNetworkStats stats);
                        string label = GameText.Format("diag.subject.belt_net", root.Network + 1);
                        current = new DiagReport
                        {
                            Subject = DiagSubject.BeltNetwork,
                            SubjectId = "belt-net:" + root.Network,
                            Name = label,
                            Position = new Vector2(root.X, root.Y),
                        };
                        current.Chains.Add(NewChain(DiagCategory.Output));
                        Step(current.Chains[0], DiagCode.BeltNetwork, GameText.Format("diag.step.belt_net", label, stats.BlockedCells), current.Position, null);
                        into.Add(current);
                    }
                    currentNet = root.Network;
                }
                DiagChain c = current.Chains.Count == 1 && current.Chains[0].Steps.Count == 1 ? current.Chains[0] : NewChainAfter(current);
                Vector2 at = new Vector2(root.X, root.Y);
                string owner = null;
                if ((root.Block == BeltBlock.SinkFull || root.Block == BeltBlock.SinkRejects) && BeltPortService.TryGetBinding(root.SinkPortId, out BeltPortService.Binding sink))
                {
                    owner = sink.BuildingId;
                    BuildingRecord ob = HomeGridService.FindBuilding(state, sink.BuildingId);
                    if (ob != null)
                    {
                        at = ob.Position;
                    }
                }
                Step(c, root.Block == BeltBlock.SinkFull && owner != null && HomeValleyCargo.GetAvailableSpace(state, CampaignEconomyLedger.ResourceScrap) <= 0
                        ? DiagCode.StoreFullRoot : DiagCode.BeltTerminal,
                    GameText.Format("diag.step.belt_terminal", root.X, root.Y, BeltNetworkService.DescribeBlock(k, root)), at, owner);
            }
        }

        /// <summary>同一网络的第二个源头：另起一条链（第一步复用网络行）。</summary>
        private static DiagChain NewChainAfter(DiagReport r)
        {
            DiagChain c = NewChain(DiagCategory.Output);
            DiagStep head = r.Chains[0].Steps[0];
            c.Steps.Add(head);
            r.Chains.Add(c);
            return c;
        }

        // ── 流体网络 ───────────────────────────────────────────────────────────

        private static void CollectPipeNetworks(CampaignState state, List<DiagReport> into)
        {
            if (!PipeNetworkService.IsRunning || !ReferenceEquals(PipeNetworkService.BoundState, state))
            {
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            if (k.NetworkCount == 0)
            {
                return;
            }
            k.CollectNetworkAnchors(AnchorScratch);
            foreach (Unity.Mathematics.int3 a in AnchorScratch)
            {
                if (!k.TryGetNetworkInfo(a.z, out PipeNetInfo n))
                {
                    continue;
                }
                PipeNetIssue i = n.Issues;
                bool starving = (i & (PipeNetIssue.NoSupply | PipeNetIssue.Shortage)) != 0 && n.DemandLpm > 0.0;
                if (!starving)
                {
                    continue;
                }
                string label = GameText.Format("diag.subject.pipe_net", PipeNetworkService.FluidName(n.Fluid), n.Cells);
                var at = new Vector2(a.x, a.y);
                var r = new DiagReport { Subject = DiagSubject.PipeNetwork, SubjectId = "pipe-net:" + a.x + "," + a.y, Name = label, Position = at };
                DiagChain c = NewChain(DiagCategory.Input);
                Step(c, DiagCode.PipeNetwork, GameText.Format("diag.step.pipe_net", label, PipeNetworkService.DescribeState(n).Replace("\n", "；")), at, null);
                if ((i & PipeNetIssue.NoSource) != 0)
                {
                    Step(c, DiagCode.PipeNoSource, GameText.Get("diag.step.pipe_no_source"), at, null);
                }
                else if ((i & PipeNetIssue.PipeLimited) != 0 && n.PipeCells > 0)
                {
                    Step(c, DiagCode.PipeLimited, GameText.Format("diag.step.pipe_limited", n.BottleneckX, n.BottleneckY, Mathf.RoundToInt((float)n.CapLpm)),
                        new Vector2(n.BottleneckX, n.BottleneckY), null);
                }
                else
                {
                    Step(c, DiagCode.PipeShortage, GameText.Format("diag.step.pipe_shortage", Mathf.RoundToInt((float)Math.Max(n.UnmetLpm, n.DemandLpm - n.DeliveredLpm))), at, null);
                }
                r.Chains.Add(c);
                into.Add(r);
            }
        }

        // ── 文本与定位 ─────────────────────────────────────────────────────────

        public static string CategoryName(DiagCategory c)
        {
            switch (c)
            {
                case DiagCategory.Power: return GameText.Get("diag.cat.power");
                case DiagCategory.Input: return GameText.Get("diag.cat.input");
                case DiagCategory.Output: return GameText.Get("diag.cat.output");
                default: return GameText.Get("diag.cat.structure");
            }
        }

        /// <summary>一条链的一行文字：“症状 → 中间 → 根源”。</summary>
        public static string ChainText(DiagChain c)
        {
            if (c == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(96);
            string arrow = GameText.Get("diag.chain.arrow");
            for (int i = 0; i < c.Steps.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(arrow);
                }
                sb.Append(c.Steps[i].Text);
            }
            return sb.ToString();
        }

        /// <summary>悬停用：这座建筑为什么不工作（每条链一行，按类别顺序）；没有问题返回 false。</summary>
        public static bool TryDescribeForHover(CampaignState state, BuildingRecord b, out string text)
        {
            text = null;
            DiagReport r = DiagnoseBuilding(state, b);
            if (r == null)
            {
                return false;
            }
            var sb = new StringBuilder(160);
            sb.Append(GameText.Get("diag.hover.title"));
            for (int i = 0; i < r.Chains.Count; i++)
            {
                sb.Append('\n').Append(GameText.Format("diag.hover.line", i + 1, CategoryName(r.Chains[i].Category), ChainText(r.Chains[i])));
            }
            sb.Append('\n').Append(GameText.Format("diag.hover.more", InputDisplay.ForAction(GameActionId.OpenDiagnosis)));
            text = sb.ToString();
            return true;
        }

        /// <summary>点原因条目：镜头飞到这一步指向的位置（家园）。建造模式开着时保持开着（FG-GAP-071）。</summary>
        public static bool Locate(DiagStep step)
        {
            if (step == null || !step.HasPosition)
            {
                return false;
            }
            LocateCount++;
            LastLocated = step.Position;
            return GameLogic.Campaign.WorldSim.WorldView.FlyTo(HomeValleyLayout.RegionId, step.Position);
        }

        public static int LocateCount { get; private set; }
        public static Vector2 LastLocated { get; private set; }

        private static DiagChain NewChain(DiagCategory cat) => new DiagChain { Category = cat };

        private static void Step(DiagChain c, DiagCode code, string text, Vector2 at, string targetId)
        {
            c.Steps.Add(new DiagStep { Code = code, Text = text, HasPosition = true, Position = at, TargetId = targetId });
        }

        private static Vector2 CellPos(GridCell c) => new Vector2(c.X, c.Y);

        /// <summary>
        /// 两个指纹：<paramref name="structure"/> 只看对象、名字、原因链类别、每步种类与步数（界面按它决定要不要重建列表）；
        /// <paramref name="full"/> 另含每步文字（带实时数字）与位置（界面按它原地更新文字，不重建）。
        /// </summary>
        private static void Signature(List<DiagReport> reports, out ulong full, out ulong structure)
        {
            ulong h = 1469598103934665603UL;
            ulong s = 1469598103934665603UL;
            foreach (DiagReport r in reports)
            {
                s = Mix(s, r.SubjectId);
                s = Mix(s, r.Name);
                h = Mix(h, r.SubjectId);
                h = Mix(h, r.Name);
                h = MixFloat(h, r.Position.x);
                h = MixFloat(h, r.Position.y);
                foreach (DiagChain c in r.Chains)
                {
                    s = (s ^ (ulong)c.Category) * 1099511628211UL;
                    h = (h ^ (ulong)c.Category) * 1099511628211UL;
                    foreach (DiagStep st in c.Steps)
                    {
                        s = (s ^ ((ulong)st.Code + 0x100UL) ^ (st.HasPosition ? 0x10000UL : 0UL)) * 1099511628211UL;
                        h = Mix(h, st.Text);
                        h = MixFloat(h, st.Position.x);
                        h = MixFloat(h, st.Position.y);
                        h = Mix(h, st.TargetId);
                    }
                    s = (s ^ 0x2F) * 1099511628211UL;
                }
            }
            full = h;
            structure = s;
        }

        private static ulong MixFloat(ulong h, float v) => (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;

        private static ulong Mix(ulong h, string s)
        {
            if (s == null)
            {
                return (h ^ 0xFF) * 1099511628211UL;
            }
            for (int i = 0; i < s.Length; i++)
            {
                h = (h ^ s[i]) * 1099511628211UL;
            }
            return (h ^ 0x1F) * 1099511628211UL;
        }

        /// <summary>整份诊断的文字（自检比较“观察 / 不观察”“不同倍速”结果是否一致用）。</summary>
        public static string Fingerprint(List<DiagReport> reports)
        {
            var sb = new StringBuilder(256);
            foreach (DiagReport r in reports)
            {
                sb.Append(r.SubjectId).Append('|');
                foreach (DiagChain c in r.Chains)
                {
                    sb.Append((int)c.Category).Append(':');
                    foreach (DiagStep s in c.Steps)
                    {
                        sb.Append((int)s.Code).Append(',');
                    }
                    sb.Append(';');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string FormatNumber(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
