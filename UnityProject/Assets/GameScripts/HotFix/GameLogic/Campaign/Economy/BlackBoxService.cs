using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG5-RND-06（FG05 FGR-RND-060；FG13 FGU-40；FGT-RND-009）：黑匣子陈列馆与纪念墙。唯一写入口（<see cref="BlackBoxState"/> 与黑匣子的区域关键物条目）。
    ///
    /// - 黑匣子：每台阵亡的机器一个，身份 = 机器 LogicId（区域关键物 ID <c>blackbox:&lt;LogicId&gt;</c>，内容 <see cref="ContentId"/>）。
    ///   在区域里的位置 / 携带者沿用远征关键物的 OnGround → Carried → Recovered 生命周期（<see cref="CampaignState.RegionQuestItems"/>，
    ///   拾取走区域交互“装载”、带回走撤离结算 ResolveExtraction），不另建一套掉落逻辑。和其它关键物唯一的不同：黑匣子永不 Lost——
    ///   携带它的机器阵亡或没能撤离时掉在原地（FG08“机器阵亡时，它货舱里的东西和黑匣子掉在原地，其他机器可以捡起来”），下次远征还能再取。
    ///   家园阵亡的直接回收（FGR-RND-060“家园阵亡的直接回收”）。
    /// - 陈列馆：已回收的黑匣子按回收先后排队；每座工作中（建成、没禁用、有电）的陈列馆同时分析 blackbox.boxes_per_gallery 个，
    ///   一个要 blackbox.days_per_box 个游戏日，共产出 blackbox.points_per_box 点技术数据，按进度逐点经经济账本生产事务入账（统计面板计收入）。
    ///   陈列馆缺电 / 禁用 / 被毁时暂停、进度保留在黑匣子上（不绑定哪一座）。多座各自独立、不递减。
    /// - 纪念墙：机器登记表里的阵亡记录（名字、编号、经历、阵亡时刻与地点由 <see cref="MachineRegistry.MarkDeadByLogicId"/> 记下），按时间或按地点排序。
    /// - 推进只看世界步序号（与观察、帧率、倍速无关；暂停时不走）。每步 O(陈列馆数 + 已回收黑匣子数)；入账只在跨过整点时发生。
    /// </summary>
    public static class BlackBoxService
    {
        public const string TypeId = "blackbox_gallery";
        public const string ContentId = "black_box";
        public const string InstancePrefix = "blackbox:";
        public const string NotifyDone = "blackbox_done";

        /// <summary>黑匣子 / 陈列馆 / 纪念墙相关的任何变化 +1（界面据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static int StepCount { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }

        private static BuildingRecord[] _indexed;
        private static readonly List<BuildingRecord> GalleryList = new List<BuildingRecord>(2);
        private static readonly List<BlackBoxRecord> QueueScratch = new List<BlackBoxRecord>(8);
        private static bool _builtHookRaised;

        // EnsureState 的早退键：上次扫描时的关键物数组 / 黑匣子域 / 黑匣子数组（任一换了才重扫）。
        private static RegionQuestItemRecord[] _scannedItems;
        private static BlackBoxState _scannedState;
        private static BlackBoxRecord[] _scannedBoxes;
        private static readonly HashSet<int> ScanIds = new HashSet<int>();

        // 读点视图（面板 / 建筑状态 / 纪念墙）：LogicId → 关键物条目 / 黑匣子记录 / 队列位置。关键物数组、黑匣子数组或 Revision 变了才重建（O(关键物 + 黑匣子)），
        // 之后每行 O(1)。条目状态（地面 / 携带 / 回收）原地读，不进键。
        private static RegionQuestItemRecord[] _viewItems;
        private static BlackBoxRecord[] _viewBoxes;
        private static int _viewRevision = -1;
        private static readonly Dictionary<int, RegionQuestItemRecord> ViewItem = new Dictionary<int, RegionQuestItemRecord>();
        private static readonly Dictionary<int, BlackBoxRecord> ViewBox = new Dictionary<int, BlackBoxRecord>();
        private static readonly Dictionary<int, int> ViewQueuePos = new Dictionary<int, int>();
        private static readonly List<BlackBoxRecord> ViewQueue = new List<BlackBoxRecord>();

        private static int StepTicks => Math.Max(1, GridContent.TuningInt("eco.prod.step_ticks"));

        public static void ResetSessionState()
        {
            _indexed = null;
            GalleryList.Clear();
            QueueScratch.Clear();
            _builtHookRaised = false;
            _scannedItems = null;
            _scannedState = null;
            _scannedBoxes = null;
            ScanIds.Clear();
            InvalidateView();
            StepCount = 0;
            LastStepMs = 0;
            MaxStepMs = 0;
            Revision++;
        }

        public static void ResetStepStats() => MaxStepMs = 0;

        public static void Touch() => Revision++;

        // ─────────────────────────────── 调参 ───────────────────────────────

        /// <summary>每个黑匣子共产出的技术数据（FGR-RND-060 初值 20）。</summary>
        public static int PointsPerBox => Math.Max(1, (int)Math.Round(Tuning("blackbox.points_per_box", 20f)));

        /// <summary>分析一个黑匣子要几个游戏日（FGR-RND-060 初值 1）。</summary>
        public static float DaysPerBox => Math.Max(0.001f, Tuning("blackbox.days_per_box", 1f));

        /// <summary>每座工作中的陈列馆同时分析几个。</summary>
        public static int BoxesPerGallery => Math.Max(1, (int)Math.Round(Tuning("blackbox.boxes_per_gallery", 1f)));

        /// <summary>分析一个黑匣子要的世界步数（游戏日 × clock.day_seconds × 步频）。</summary>
        public static long NeedTicks => Math.Max(1L, GameClock.TicksFor(DaysPerBox * GameClock.DaySeconds));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static BlackBoxState StateOf(CampaignState s) => s?.Research?.BlackBoxes;

        /// <summary>
        /// 读档 / 新档时补全黑匣子域（<see cref="CampaignFgStateDomains"/> 调）：数组补成空；已回收（区域关键物 Recovered）却不在队列里的黑匣子补进队列
        /// （只加不删：撤离结算与回收入队之间存过档、或旧档里有被代码标成 Lost 的黑匣子时也能找回）。
        /// EnsureAll 每个研究推进周期都会调到这里（ResearchService.Step，每 eco.prod.step_ticks 个世界步一次），所以扫描只在
        /// 关键物数组或黑匣子数组换过（读档 / 新档 / 有黑匣子入队或新增）时做一次 O(关键物 + 黑匣子)；平时两次引用比较后早退，O(1)、不分配。
        /// 运行中改关键物状态的写入口（阵亡 / 撤离结算 / 回收）自己维护“Recovered 必在队列里、黑匣子永不 Lost”，不依赖这里的补漏。
        /// </summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Research == null)
            {
                return;
            }
            BlackBoxState f = s.Research.BlackBoxes ??= new BlackBoxState();
            f.Boxes ??= Array.Empty<BlackBoxRecord>();
            RegionQuestItemRecord[] items = s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>();
            if (ReferenceEquals(items, _scannedItems) && ReferenceEquals(f, _scannedState) && ReferenceEquals(f.Boxes, _scannedBoxes))
            {
                return;
            }
            bool invalid = false;
            foreach (BlackBoxRecord b in f.Boxes)
            {
                invalid |= b == null || b.MachineLogicId <= 0;
            }
            if (invalid)
            {
                f.Boxes = Array.FindAll(f.Boxes, b => b != null && b.MachineLogicId > 0);
            }
            ScanIds.Clear();
            foreach (BlackBoxRecord b in f.Boxes)
            {
                ScanIds.Add(b.MachineLogicId);
            }
            List<BlackBoxRecord> missing = null;
            foreach (RegionQuestItemRecord q in items)
            {
                if (!IsBlackBox(q))
                {
                    continue;
                }
                if (q.State == RegionQuestItemState.Lost)
                {
                    q.State = RegionQuestItemState.OnGround; // 黑匣子永不丢失：留在它记着的地点（位置是最后一次记下的值）
                    q.CarrierLogicId = 0;
                }
                int id = q.State == RegionQuestItemState.Recovered ? LogicIdOf(q) : 0;
                if (id > 0 && ScanIds.Add(id))
                {
                    (missing ??= new List<BlackBoxRecord>()).Add(new BlackBoxRecord { MachineLogicId = id, RecoveredTick = 0 });
                }
            }
            if (missing != null)
            {
                InsertAll(f, missing);
            }
            _scannedItems = items;
            _scannedState = f;
            _scannedBoxes = f.Boxes;
        }

        private static void InvalidateView()
        {
            _viewItems = null;
            _viewBoxes = null;
            _viewRevision = -1;
            ViewItem.Clear();
            ViewBox.Clear();
            ViewQueuePos.Clear();
            ViewQueue.Clear();
        }

        /// <summary>读点视图：数组引用与 Revision 都没变时 O(1)；否则重建一次。</summary>
        private static void EnsureView(CampaignState s)
        {
            RegionQuestItemRecord[] items = s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>();
            BlackBoxRecord[] boxes = StateOf(s)?.Boxes ?? Array.Empty<BlackBoxRecord>();
            if (ReferenceEquals(items, _viewItems) && ReferenceEquals(boxes, _viewBoxes) && _viewRevision == Revision)
            {
                return;
            }
            ViewItem.Clear();
            ViewBox.Clear();
            ViewQueuePos.Clear();
            ViewQueue.Clear();
            foreach (RegionQuestItemRecord q in items)
            {
                int id = IsBlackBox(q) ? LogicIdOf(q) : 0;
                if (id > 0 && !ViewItem.ContainsKey(id))
                {
                    ViewItem[id] = q;
                }
            }
            foreach (BlackBoxRecord b in boxes)
            {
                if (b == null || ViewBox.ContainsKey(b.MachineLogicId))
                {
                    continue;
                }
                ViewBox[b.MachineLogicId] = b;
                if (!b.Done)
                {
                    ViewQueuePos[b.MachineLogicId] = ViewQueue.Count;
                    ViewQueue.Add(b);
                }
            }
            _viewItems = items;
            _viewBoxes = boxes;
            _viewRevision = Revision;
        }

        private static BlackBoxState EnsureBox(CampaignState s)
        {
            BlackBoxState f = StateOf(s);
            if (f?.Boxes == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
                f = StateOf(s);
            }
            return f;
        }

        /// <summary>这台机器的黑匣子记录（已回收才有）。走读点视图，平时 O(1)。</summary>
        public static BlackBoxRecord Find(CampaignState s, int logicId)
        {
            if (StateOf(s)?.Boxes == null)
            {
                return null;
            }
            EnsureView(s);
            return ViewBox.TryGetValue(logicId, out BlackBoxRecord b) ? b : null;
        }

        private static BlackBoxRecord Find(BlackBoxState f, int logicId)
        {
            if (f?.Boxes == null)
            {
                return null;
            }
            foreach (BlackBoxRecord b in f.Boxes)
            {
                if (b != null && b.MachineLogicId == logicId)
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>按（回收的世界步，机器 LogicId）插入；调用方保证同一台机器不重复。</summary>
        private static void Insert(BlackBoxState f, BlackBoxRecord rec) => InsertAll(f, new List<BlackBoxRecord>(1) { rec });

        private static void InsertAll(BlackBoxState f, List<BlackBoxRecord> recs)
        {
            var list = new List<BlackBoxRecord>(f.Boxes.Length + recs.Count);
            list.AddRange(f.Boxes);
            list.AddRange(recs);
            list.Sort((a, b) => a.RecoveredTick != b.RecoveredTick ? a.RecoveredTick.CompareTo(b.RecoveredTick) : a.MachineLogicId.CompareTo(b.MachineLogicId));
            f.Boxes = list.ToArray();
        }

        // ─────────────────────────────── 黑匣子身份（区域关键物） ───────────────────────────────

        public static string InstanceIdOf(int logicId) => InstancePrefix + logicId.ToString(CultureInfo.InvariantCulture);

        public static bool IsBlackBox(RegionQuestItemRecord q) => q != null && q.ContentId == ContentId;

        /// <summary>黑匣子属于哪台机器（ID 不是本格式 = 0）。</summary>
        public static int LogicIdOf(RegionQuestItemRecord q)
        {
            string id = q?.SalvageInstanceId;
            int start = InstancePrefix.Length;
            if (id == null || id.Length <= start || !id.StartsWith(InstancePrefix, StringComparison.Ordinal))
            {
                return 0;
            }
            int v = 0;
            for (int i = start; i < id.Length; i++)
            {
                int d = id[i] - '0';
                if (d < 0 || d > 9 || v > (int.MaxValue - d) / 10)
                {
                    return 0; // 不是十进制正整数（或溢出）：不是本格式
                }
                v = v * 10 + d;
            }
            return v; // 逐字符解析，不分配字符串
        }

        /// <summary>这台机器的黑匣子条目（还没阵亡 = null）。走读点视图：关键物数组没换时 O(1)，换了重建一次 O(关键物条数)。</summary>
        public static RegionQuestItemRecord FindItem(CampaignState s, int logicId)
        {
            if (s == null)
            {
                return null;
            }
            EnsureView(s);
            return ViewItem.TryGetValue(logicId, out RegionQuestItemRecord q) ? q : null;
        }

        private static bool IsHome(string regionId) => string.IsNullOrEmpty(regionId) || regionId == HomeValleyLayout.RegionId;

        // ─────────────────────────────── 阵亡 / 拾取 / 带回 ───────────────────────────────

        /// <summary>
        /// 机器阵亡（<see cref="MachineRegistry.MarkDeadByLogicId"/>，存活 → 阵亡的唯一翻转点）：
        /// 1. 它携带的别人的黑匣子掉在阵亡处（家园 = 直接回收）；2. 它自己的黑匣子：家园 → 直接回收进陈列馆队列；远征 → 掉在阵亡处（OnGround）。
        /// 返回阵亡通知里“黑匣子……”的去向文字（当前语言）。O(关键物条数)，只在阵亡时。
        /// </summary>
        public static string OnMachineDied(CampaignState s, MachineRecord m, Vector2 at)
        {
            if (s == null || m == null || m.LogicId <= 0)
            {
                return null;
            }
            BlackBoxState f = EnsureBox(s);
            string region = string.IsNullOrEmpty(m.RegionId) ? HomeValleyLayout.RegionId : m.RegionId;
            bool home = IsHome(region);
            long now = GameClock.Ticks;
            foreach (RegionQuestItemRecord q in s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsBlackBox(q) && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == m.LogicId)
                {
                    if (home)
                    {
                        Recover(s, f, q, now);
                    }
                    else
                    {
                        q.State = RegionQuestItemState.OnGround;
                        q.CarrierLogicId = 0;
                        q.RegionId = region;
                        q.Position = at;
                    }
                }
            }
            RegionQuestItemRecord own = FindItem(s, m.LogicId);
            if (own == null)
            {
                own = new RegionQuestItemRecord
                {
                    SalvageInstanceId = InstanceIdOf(m.LogicId),
                    RegionId = region,
                    ContentId = ContentId,
                    State = RegionQuestItemState.OnGround,
                    CarrierLogicId = 0,
                    Position = at,
                };
                var items = new List<RegionQuestItemRecord>(s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()) { own };
                s.RegionQuestItems = items.ToArray();
            }
            Revision++;
            if (home)
            {
                Recover(s, f, own, now);
                return GameText.Get(WorkingCount(s) > 0 ? "blackbox.where.home_gallery" : "blackbox.where.home_no_gallery");
            }
            GuidanceHooks.Raise(GuidanceHooks.BlackBoxFirstLeftBehind);
            return GameText.Format("blackbox.where.field", PlaceName(region));
        }

        /// <summary>撤离结算里一件关键物被带回（ResolveExtraction 把它标成 Recovered 之后调）：是黑匣子 → 进陈列馆队列。不是黑匣子时不做事。</summary>
        public static void OnRecovered(CampaignState s, RegionQuestItemRecord q)
        {
            if (s == null || !IsBlackBox(q) || q.State != RegionQuestItemState.Recovered)
            {
                return;
            }
            Recover(s, EnsureBox(s), q, GameClock.Ticks);
        }

        /// <summary>
        /// 撤离结算里携带者没能撤离（ResolveExtraction 的“没有幸存”分支）：是黑匣子 → 掉在携带者最后的位置（阵亡位置 / 最后同步的位置），留在区域里，返回 true；
        /// 不是黑匣子返回 false（照旧按 Lost 处理）。
        /// </summary>
        public static bool DropFromCarrier(CampaignState s, RegionQuestItemRecord q)
        {
            if (!IsBlackBox(q))
            {
                return false;
            }
            Vector2 at = q.Position;
            if (MachineRegistry.TryGetRecord(q.CarrierLogicId, out MachineRecord carrier) && carrier != null)
            {
                // 阵亡的用阵亡处；活着的先取实时位置（WorldPosition 平时只在存档前同步，可能是旧值），取不到再用记录值。
                at = !carrier.IsAlive && carrier.DeathTick > 0 ? carrier.DeathPosition
                    : MachineRegistry.TryGetLivePosition(carrier.LogicId, out Vector2 live) ? live : carrier.WorldPosition;
            }
            q.State = RegionQuestItemState.OnGround;
            q.CarrierLogicId = 0;
            q.Position = at;
            Revision++;
            return true;
        }

        private static void Recover(CampaignState s, BlackBoxState f, RegionQuestItemRecord q, long now)
        {
            q.State = RegionQuestItemState.Recovered;
            q.CarrierLogicId = 0;
            int id = LogicIdOf(q);
            if (id > 0 && Find(f, id) == null)
            {
                Insert(f, new BlackBoxRecord { MachineLogicId = id, RecoveredTick = now });
                GuidanceHooks.Raise(GuidanceHooks.BlackBoxFirstRecovered);
            }
            Revision++;
        }

        // ─────────────────────────────── 陈列馆 ───────────────────────────────

        public static bool IsGallery(BuildingRecord b) => b != null && b.BuildingTypeId == TypeId;

        /// <summary>家园里的陈列馆（含虚影与缺电 / 禁用 / 被毁；不含搬迁虚影；按建筑 ID 排序）。建筑数组换了才重建，平时 O(1)（与监听站同一做法，完工是原地改状态）。</summary>
        public static IReadOnlyList<BuildingRecord> GalleriesOf(CampaignState s)
        {
            BuildingRecord[] records = s?.BuildingRecords;
            if (!ReferenceEquals(records, _indexed))
            {
                _indexed = records;
                GalleryList.Clear();
                foreach (BuildingRecord b in records ?? Array.Empty<BuildingRecord>())
                {
                    if (IsGallery(b) && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b))
                    {
                        GalleryList.Add(b);
                    }
                }
                GalleryList.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
            }
            return GalleryList;
        }

        /// <summary>正在工作：建成、没禁用、有电。</summary>
        public static bool IsWorking(BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational
                      && (b.PowerState == BuildingPowerState.Powered || b.PowerState == BuildingPowerState.NotApplicable && !NeedsPower());

        private static bool NeedsPower() =>
            HomeValleyLayout.PowerProfile.TryGetValue(TypeId, out (float PowerDemand, int PowerPriority) prof) && prof.PowerDemand > 0f;

        public static int WorkingCount(CampaignState s)
        {
            int n = 0;
            foreach (BuildingRecord b in GalleriesOf(s))
            {
                n += IsWorking(b) ? 1 : 0;
            }
            return n;
        }

        /// <summary>电网结算之后（完工 / 启停 / 被毁，<c>HomeValleyPowerGrid</c> 调）：第一次有建成的陈列馆 → 引导钩子（图鉴条目随之解锁）。</summary>
        public static void OnPowerApplied(CampaignState s, bool notify)
        {
            Revision++;
            if (!notify || _builtHookRaised)
            {
                return;
            }
            foreach (BuildingRecord b in GalleriesOf(s))
            {
                if (b.ConstructionState == BuildingConstructionState.Operational)
                {
                    _builtHookRaised = true;
                    GuidanceHooks.Raise(GuidanceHooks.BlackBoxFirstBuilt);
                    return;
                }
            }
        }

        // ─────────────────────────────── 每个世界步 ───────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在情报之后调用）：每 eco.prod.step_ticks 步推进一次。只看步序号。</summary>
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
            Step(state, k);
        }

        /// <summary>推进 <paramref name="ticks"/> 个世界步：队列前 N 个（N = 工作中的陈列馆 × 每座同时分析数）各走 ticks 步，跨过整点时入账技术数据，满了标分析完。</summary>
        public static void Step(CampaignState s, int ticks)
        {
            if (s == null || ticks <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BlackBoxState f = EnsureBox(s);
            if (f != null && f.Boxes.Length > 0)
            {
                int slots = WorkingCount(s) * BoxesPerGallery;
                if (slots > 0)
                {
                    long need = NeedTicks;
                    int points = PointsPerBox;
                    foreach (BlackBoxRecord b in f.Boxes)
                    {
                        if (slots <= 0)
                        {
                            break;
                        }
                        if (b == null || b.Done)
                        {
                            continue;
                        }
                        slots--;
                        b.Work = Math.Min(need, b.Work + ticks);
                        int due = b.Work >= need ? points : (int)(b.Work * points / need);
                        if (due > b.PointsGranted)
                        {
                            Grant(s, f, b, due);
                        }
                        if (b.Work >= need)
                        {
                            Complete(s, f, b, points);
                        }
                    }
                }
            }
            StepCount++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
        }

        /// <summary>把技术数据补到 <paramref name="due"/> 点：经济账本生产事务（幂等：同一个黑匣子同一个累计点数只入账一次；统计面板计技术数据收入）。</summary>
        private static void Grant(CampaignState s, BlackBoxState f, BlackBoxRecord b, int due)
        {
            int delta = due - b.PointsGranted;
            string owner = InstanceIdOf(b.MachineLogicId);
            string tx = owner + ":p" + due.ToString(CultureInfo.InvariantCulture);
            // 同一个黑匣子、同一个累计点数的事务已经入过账（例如旧档丢了黑匣子域、重新分析到这里）：账本幂等不再发放，这里也不再计入统计。
            bool fresh = CampaignEconomyLedger.Find(s, tx) == null;
            CampaignEconomyLedger.ProposeProduce(s, tx, owner, CampaignEconomyLedger.ResourceTechData, delta);
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            if (CampaignEconomyLedger.Commit(s, tx).Success)
            {
                b.PointsGranted = due;
                if (fresh)
                {
                    f.PointsProduced += delta;
                    HomeInventory.Touch();
                }
                Revision++;
            }
        }

        private static void Complete(CampaignState s, BlackBoxState f, BlackBoxRecord b, int points)
        {
            b.Done = true;
            b.DoneTick = GameClock.Ticks;
            f.Analyzed++;
            Revision++;
            NotificationCenter.Post(NotifyDone, GameText.Format("blackbox.notify.done", MachineNaming.Short(b.MachineLogicId), b.PointsGranted));
            GuidanceHooks.Raise(GuidanceHooks.BlackBoxFirstAnalyzed);
        }

        // ─────────────────────────────── 读点（面板 / 建筑状态 / 自检） ───────────────────────────────

        /// <summary>待分析的黑匣子（按队列顺序；不含分析完的）。返回新列表（自检 / 一次性读点用）；面板与建筑状态走 <see cref="QueueView"/>。</summary>
        public static List<BlackBoxRecord> Queue(CampaignState s)
        {
            EnsureView(s);
            return new List<BlackBoxRecord>(ViewQueue);
        }

        /// <summary>待分析队列的只读视图（不分配；下一次黑匣子变化后失效，调用方不要长期持有）。</summary>
        private static IReadOnlyList<BlackBoxRecord> QueueView(CampaignState s)
        {
            EnsureView(s);
            return ViewQueue;
        }

        public static int ProgressPercent(BlackBoxRecord b) => b == null ? 0 : b.Done ? 100 : (int)Math.Min(99, b.Work * 100 / Math.Max(1, NeedTicks));

        /// <summary>建筑状态（B05）：这座陈列馆在分析谁的黑匣子 / 空闲。缺电 / 禁用 / 被毁由通用状态先报。</summary>
        public static BuildingStatus StatusOf(CampaignState s, BuildingRecord b)
        {
            int index = 0;
            foreach (BuildingRecord g in GalleriesOf(s))
            {
                if (ReferenceEquals(g, b))
                {
                    break;
                }
                index += IsWorking(g) ? 1 : 0;
            }
            IReadOnlyList<BlackBoxRecord> queue = QueueView(s);
            int per = BoxesPerGallery;
            int start = index * per;
            if (!IsWorking(b) || start >= queue.Count)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "blackbox.idle", GameText.Get("bs.reason.blackbox_idle"));
            }
            BlackBoxRecord cur = queue[start];
            int queued = Math.Max(0, queue.Count - WorkingCount(s) * per);
            return new BuildingStatus(BuildingStatusKind.Working, "blackbox.working", GameText.Format("bs.reason.blackbox_working",
                MachineNaming.Short(cur.MachineLogicId), ProgressPercent(cur), cur.PointsGranted, PointsPerBox, queued));
        }

        /// <summary>面板上部的状态行。</summary>
        public static string StatusText(CampaignState s)
        {
            int working = WorkingCount(s);
            BlackBoxState f = StateOf(s);
            if (working <= 0)
            {
                return GameText.Get("blackbox.panel.status.none");
            }
            string span = GameText.Format("blackbox.panel.day_span", DaysPerBox.ToString("0.##", CultureInfo.InvariantCulture));
            return GameText.Format("blackbox.panel.status.working", working, BoxesPerGallery, PointsPerBox, span, f?.PointsProduced ?? 0, f?.Analyzed ?? 0);
        }

        /// <summary>
        /// 一个黑匣子现在在哪、怎样了（面板“黑匣子”页、纪念墙“黑匣子：”）：地面（地点 + 坐标，可以再取）/ 被谁携带 / 等陈列馆 / 排队第几 / 分析中 / 暂停 / 已分析。
        /// </summary>
        public static string BoxStateText(CampaignState s, int logicId)
        {
            RegionQuestItemRecord q = FindItem(s, logicId);
            if (q == null)
            {
                return GameText.Get("blackbox.state.lost");
            }
            switch (q.State)
            {
                case RegionQuestItemState.OnGround:
                case RegionQuestItemState.Lost:
                    return GameText.Format("blackbox.state.ground", PlaceName(q.RegionId), Coord(q.Position.x), Coord(q.Position.y));
                case RegionQuestItemState.Carried:
                    return GameText.Format("blackbox.state.carried", MachineNaming.Short(q.CarrierLogicId));
            }
            BlackBoxRecord b = Find(s, logicId);
            if (b == null)
            {
                return GameText.Get("blackbox.state.no_gallery");
            }
            if (b.Done)
            {
                return GameText.Format("blackbox.state.done", b.PointsGranted);
            }
            int working = WorkingCount(s);
            int slots = working * BoxesPerGallery;
            EnsureView(s);
            int pos = ViewQueuePos.TryGetValue(logicId, out int qp) ? qp : -1;
            if (working <= 0)
            {
                return b.Work > 0 ? GameText.Format("blackbox.state.paused", ProgressPercent(b)) : GameText.Get("blackbox.state.no_gallery");
            }
            if (pos >= slots)
            {
                return GameText.Format("blackbox.state.queued", pos - slots + 1);
            }
            double secondsLeft = Math.Max(0L, NeedTicks - b.Work) / (double)Math.Max(1, GameClock.StepHz);
            return GameText.Format("blackbox.state.analyzing", ProgressPercent(b), b.PointsGranted, PointsPerBox, GameClock.FormatGameDuration(secondsLeft));
        }

        /// <summary>面板“黑匣子”页的条目：还在区域里的（地面 / 被携带）在前，然后是队列顺序，分析完的在最后（新的先）。</summary>
        public static List<int> BoxOrder(CampaignState s)
        {
            var field = new List<int>();
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsBlackBox(q) && q.State != RegionQuestItemState.Recovered && LogicIdOf(q) > 0)
                {
                    field.Add(LogicIdOf(q));
                }
            }
            field.Sort();
            var done = new List<BlackBoxRecord>();
            foreach (BlackBoxRecord b in StateOf(s)?.Boxes ?? Array.Empty<BlackBoxRecord>())
            {
                if (b == null)
                {
                    continue;
                }
                if (b.Done)
                {
                    done.Add(b);
                }
                else
                {
                    field.Add(b.MachineLogicId);
                }
            }
            done.Sort((a, c) => a.DoneTick != c.DoneTick ? c.DoneTick.CompareTo(a.DoneTick) : c.MachineLogicId.CompareTo(a.MachineLogicId));
            foreach (BlackBoxRecord b in done)
            {
                field.Add(b.MachineLogicId);
            }
            return field;
        }

        public static string BoxRowText(CampaignState s, int logicId) =>
            GameText.Format("blackbox.row", MachineNaming.Short(logicId), BoxStateText(s, logicId));

        /// <summary>
        /// 纪念墙名单（FGU-40）：全部阵亡的机器。<paramref name="byPlace"/> = false 按时间（最近的在前；旧档没记时间的在最后）；
        /// = true 按地点（地点名，没记地点的在最后），同一地点按时间。O(机器数 log 机器数)，只在面板刷新时。
        /// </summary>
        public static List<MachineRecord> Memorial(bool byPlace)
        {
            var list = new List<MachineRecord>();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && !m.IsAlive)
                {
                    list.Add(m);
                }
            }
            var placeKey = new Dictionary<int, string>(list.Count);
            foreach (MachineRecord m in list)
            {
                placeKey[m.LogicId] = string.IsNullOrEmpty(m.DeathRegionId) ? null : PlaceName(m.DeathRegionId);
            }
            int ByTime(MachineRecord a, MachineRecord b)
            {
                bool ka = a.DeathTick > 0, kb = b.DeathTick > 0;
                if (ka != kb)
                {
                    return ka ? -1 : 1;
                }
                return a.DeathTick != b.DeathTick ? b.DeathTick.CompareTo(a.DeathTick) : b.LogicId.CompareTo(a.LogicId);
            }
            list.Sort((a, b) =>
            {
                if (byPlace)
                {
                    string pa = placeKey[a.LogicId], pb = placeKey[b.LogicId];
                    if ((pa == null) != (pb == null))
                    {
                        return pa == null ? 1 : -1;
                    }
                    int c = string.CompareOrdinal(pa ?? string.Empty, pb ?? string.Empty);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                return ByTime(a, b);
            });
            return list;
        }

        /// <summary>纪念墙一行：名字 · 编号 · 型号；阵亡地点 · 时间；经历与统计；黑匣子去向。</summary>
        public static string MemorialText(CampaignState s, MachineRecord m)
        {
            if (m == null)
            {
                return string.Empty;
            }
            string model = MachineNaming.Model(m);
            string name = MachineNaming.HasCustomName(m) ? m.CustomName : model.Length > 0 ? model : GameText.Get("blackbox.memorial.no_model");
            string place = string.IsNullOrEmpty(m.DeathRegionId)
                ? GameText.Get("blackbox.memorial.place_unknown")
                : GameText.Format("blackbox.memorial.place", PlaceName(m.DeathRegionId), Coord(m.DeathPosition.x), Coord(m.DeathPosition.y));
            string time = m.DeathTick > 0
                ? GameClock.FormatDayTime(m.DeathTick / (double)Math.Max(1, GameClock.StepHz))
                : GameText.Get("blackbox.memorial.time_unknown");
            // 起了名：名字 · 编号 · 型号；没起名：型号 · 编号（名字就是型号，不再重复写一遍）。
            string head = MachineNaming.HasCustomName(m)
                ? GameText.Format("blackbox.memorial.name", name, m.DisplayNumber, model.Length > 0 ? model : GameText.Get("blackbox.memorial.no_model"))
                : GameText.Format("blackbox.memorial.name_plain", name, m.DisplayNumber);
            return head
                   + "\n" + GameText.Format("blackbox.memorial.lost", place, time)
                   + "\n" + GameText.Format("blackbox.memorial.history", MachineExperienceFlags.Join(m.ExperienceFlags), m.KillCount, m.JobsCompleted, m.ExpeditionsCompleted,
                       MachineSignalExperience.Describe(s, m))
                   + "\n" + GameText.Format("blackbox.memorial.box", BoxStateText(s, m.LogicId));
        }

        public static int DeadCount()
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                n += m != null && !m.IsAlive ? 1 : 0;
            }
            return n;
        }

        /// <summary>全部黑匣子条目数（区域里的 + 已回收的）。</summary>
        /// <summary>远征全灭结算前：还被携带着的非黑匣子关键物数（全灭时携带者都已阵亡，携带的黑匣子已由 <see cref="OnMachineDied"/> 掉在原地）。O(关键物条数)，只在全灭那一刻。</summary>
        public static int CarriedNonBoxItems(CampaignState s)
        {
            int n = 0;
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                n += q != null && q.State == RegionQuestItemState.Carried && !IsBlackBox(q) ? 1 : 0;
            }
            return n;
        }

        /// <summary>
        /// 远征全灭字幕的补充说明（当前语言）：携带中的其它关键物遗失了几件 / 这片区域地面上有黑匣子留着、下次远征可以再取（黑匣子永不丢失，ADR-RND-006）。
        /// 两样都没有时返回 null（只显示默认正文）。O(关键物条数)，只在全灭那一刻。
        /// </summary>
        public static string WipeDetail(CampaignState s, string regionId, int carriedLost)
        {
            bool boxes = false;
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsBlackBox(q) && q.State == RegionQuestItemState.OnGround && q.RegionId == regionId)
                {
                    boxes = true;
                    break;
                }
            }
            if (carriedLost > 0 && boxes)
            {
                return GameText.Format("blackbox.wipe.items_lost_boxes_left", carriedLost);
            }
            if (carriedLost > 0)
            {
                return GameText.Format("blackbox.wipe.items_lost", carriedLost);
            }
            return boxes ? GameText.Get("blackbox.wipe.boxes_left") : null;
        }

        public static int BoxCount(CampaignState s)
        {
            int n = 0;
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                n += IsBlackBox(q) ? 1 : 0;
            }
            return n;
        }

        public static string PlaceName(string regionId)
        {
            if (string.IsNullOrEmpty(regionId))
            {
                return GameText.Get("blackbox.memorial.place_unknown");
            }
            string n = CombatSites.SiteName(regionId);
            return string.IsNullOrEmpty(n) ? regionId : n;
        }

        private static string Coord(float v) => Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture);

        /// <summary>逐字段快照（自检比较存读档 / 倍速 / 观察一致）：黑匣子域 + 全部黑匣子关键物条目 + 技术数据余额。</summary>
        public static string Snapshot(CampaignState s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(JsonUtility.ToJson(StateOf(s) ?? new BlackBoxState()));
            foreach (RegionQuestItemRecord q in s?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (IsBlackBox(q))
                {
                    sb.Append('|').Append(q.SalvageInstanceId).Append(':').Append((int)q.State).Append(':').Append(q.RegionId).Append(':').Append(q.CarrierLogicId)
                        .Append(':').Append(q.Position.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(q.Position.y.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
            sb.Append("|tech=").Append(s?.TechData ?? 0);
            return sb.ToString();
        }
    }
}
