using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// 家园告警（ER3-WRK-02 起；FG4-ECO-09 补全：FG04 FGR-ECO-080、FGT-ECO-009、FG00 B08；FG-GAP-001）。
    /// 危险优先级全序“核心受威胁 &gt; 电力 &gt; 仓满 &gt; 工厂堵塞 &gt; 机器重伤 &gt; 工作受阻”六级，<b>每一级都接真实数据源</b>：
    /// - 核心受威胁：归还核心耐久没满（<see cref="BuildingOps.ApplyDamage"/> 是唯一伤害入口）；突袭部队已到达家园（<see cref="RaidState.InTransit"/> 里已到达的突袭队伍，与常驻规则“战时预案”同一判定）。
    /// - 电力：电网结算里因缺电停机的建筑（<see cref="HomeValleyPowerGrid.TryGetCachedSummary"/>，O(1) 读缓存）。
    /// - 仓满：搬运单因仓库满而等待（原有）；某种物品库存达到家园容量（按库存 / 仓库版本号缓存，不每次重算）。
    /// - 工厂堵塞：生产建筑“持续缺料 / 输出持续堵塞”超过 alarm.jam_seconds（随生产步 O(1) 计时，<see cref="TrackProducer"/>，计时进存档）。
    /// - 机器重伤：机器耐久降到上限的 alarm.machine_wounded_fraction 以下（在 <see cref="MachineRegistry.ApplyDamage"/> 那一刻判定，<see cref="OnMachineChanged"/>）。
    /// - 工作受阻：工单因路径卡住而等待（原有）。
    /// “持续缺料 / 输出持续堵塞 / 机器重伤”在进入那一刻各发一次警告级通知（同类聚合、可定位、进历史；B08），其余几级的通知由各自系统发（建筑受损、突袭到达、缺电、仓满）。
    /// <see cref="Collect"/> 每次界面刷新调用：O(告警条数 + 工单数 + 突袭队伍数)，仓满按版本号缓存；不逐建筑 / 逐机器扫描（机器重伤集合只在换战役时重建一次）。
    /// 每条告警带定位（建筑 / 机器 / 突袭队伍的位置），点击走 <see cref="Locate"/>（与通知定位同一个镜头入口）。
    /// </summary>
    public static class HomeValleyAlarms
    {
        public enum Severity
        {
            CoreThreatened = 0,
            PowerCritical = 1,
            StorageFull = 2,
            FactoryJammed = 3,
            MachineWounded = 4,
            WorkBlocked = 5,
        }

        public const int JamNone = 0;
        public const int JamStarved = 1;
        public const int JamBlocked = 2;

        public readonly struct AlertRecord
        {
            public readonly string Key;
            public readonly Severity Severity;
            public readonly string Message;
            /// <summary>0＝没有可选中的具体机器。</summary>
            public readonly int MachineLogicId;
            /// <summary>空＝不是某一座建筑。</summary>
            public readonly string BuildingId;
            public readonly bool HasLocation;
            public readonly string RegionId;
            public readonly Vector3 Position;

            public AlertRecord(string key, Severity severity, string message, int machineLogicId, string buildingId = null, Vector3? position = null,
                string regionId = null)
            {
                Key = key;
                Severity = severity;
                Message = message;
                MachineLogicId = machineLogicId;
                BuildingId = buildingId ?? string.Empty;
                HasLocation = position.HasValue;
                Position = position ?? Vector3.zero;
                RegionId = string.IsNullOrEmpty(regionId) ? HomeValleyLayout.RegionId : regionId;
            }

            /// <summary>界面一行：“[电力] 3 座建筑缺电停机…”（等级写成文字，不只靠颜色，B05 / B15）。</summary>
            public string RowText => GameText.Format("alarm.row", LevelName(Severity), Message);
        }

        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>正在“持续缺料 / 输出持续堵塞”的生产建筑（已告过警的那一段），按建筑 ID 有序（告警顺序确定）。</summary>
        private static readonly SortedDictionary<string, ProductionService.Producer> Jammed = new SortedDictionary<string, ProductionService.Producer>(StringComparer.Ordinal);
        /// <summary>正处于重伤的机器（活着、耐久在阈值以下）。</summary>
        private static readonly SortedSet<int> Wounded = new SortedSet<int>();
        private static CampaignState _woundedState;
        private static CampaignState _coreState;
        private static string _coreId;
        private static int _storageInvRev = -1;
        private static int _storageOpsRev = -1;
        private static CampaignState _storageState;
        private static readonly List<string> _storageFull = new List<string>(4);
        private static long _jamTicks = -1;
        private static int _jamHz;
        private static int _jamTuningRev = -1;

        // ── 自检读点 ──
        public static int Revision { get; private set; } = 1;
        public static int JamAlerts { get; private set; }
        public static int WoundedAlerts { get; private set; }
        /// <summary>重伤集合因换战役重建的次数（自检证明不每次刷新都扫描机器）。</summary>
        public static int WoundedRebuilds { get; private set; }
        public static int StorageScans { get; private set; }

        public static float WoundedFraction => Mathf.Clamp(GridContent.Tuning("alarm.machine_wounded_fraction"), 0.01f, 0.99f);
        public static float JamSeconds => Math.Max(1f, GridContent.Tuning("alarm.jam_seconds"));

        /// <summary>FG0-ARCH-01：会话开始时清空告警的运行时记忆（存档里的卡住计时不动；读档后第一次生产步按存档恢复“已告警”的那一段，不重新告警）。</summary>
        public static void ResetSessionState()
        {
            _seen.Clear();
            Jammed.Clear();
            Wounded.Clear();
            _woundedState = null;
            _coreState = null;
            _coreId = null;
            _storageState = null;
            _storageInvRev = -1;
            _storageOpsRev = -1;
            _storageFull.Clear();
            _jamTicks = -1;
            JamAlerts = 0;
            WoundedAlerts = 0;
            WoundedRebuilds = 0;
            StorageScans = 0;
            Revision++;
        }

        public static string LevelName(Severity s)
        {
            switch (s)
            {
                case Severity.CoreThreatened: return GameText.Get("alarm.level.core");
                case Severity.PowerCritical: return GameText.Get("alarm.level.power");
                case Severity.StorageFull: return GameText.Get("alarm.level.storage");
                case Severity.FactoryJammed: return GameText.Get("alarm.level.factory");
                case Severity.MachineWounded: return GameText.Get("alarm.level.machine");
                default: return GameText.Get("alarm.level.work");
            }
        }

        // ── 工厂堵塞：随生产步 O(1) 计时（ProductionService.Step 逐座调用）────────────────────

        private static long JamTicks
        {
            get
            {
                if (_jamTicks < 0 || _jamHz != GameClock.StepHz || _jamTuningRev != GridContent.Revision)
                {
                    _jamHz = GameClock.StepHz;
                    _jamTuningRev = GridContent.Revision;
                    _jamTicks = Math.Max(1L, GameClock.TicksFor(JamSeconds));
                }
                return _jamTicks;
            }
        }

        /// <summary>这一步这座建筑是缺料（缺某种具体物品）、输出堵塞还是正常。与瓶颈统计同一判定（脚下废墟拆完、废液池空闲不算缺料）。</summary>
        public static int JamKindOf(ProductionService.Producer p) =>
            ProductionService.IsStarvedOnItem(p) ? JamStarved : p != null && p.State == ProdState.OutputBlocked ? JamBlocked : JamNone;

        /// <summary>
        /// 生产步之后逐座调用（O(1)，零分配）：卡住的种类变了就从这一步重新计时；同一段卡住超过 alarm.jam_seconds 发一次警告（“持续缺料 / 输出持续堵塞”，同类聚合、可定位）。
        /// 计时按世界步序号（暂停不走、倍速一帧多走几步、与观察无关），并存在建筑记录上（读档后接着同一段，已告过警的不再告）。
        /// </summary>
        public static void TrackProducer(CampaignState state, ProductionService.Producer p)
        {
            if (p?.Rec == null)
            {
                return;
            }
            ProducerRecord r = p.Rec;
            int kind = JamKindOf(p);
            long now = GameClock.Ticks;
            if (kind != r.JamKind)
            {
                r.JamKind = kind;
                r.JamSinceTick = now;
                r.JamAlerted = false;
                if (p.JamListed)
                {
                    p.JamListed = false;
                    if (p.Id != null)
                    {
                        Jammed.Remove(p.Id);
                    }
                    Revision++;
                }
                return;
            }
            if (kind == JamNone || p.Id == null)
            {
                return;
            }
            if (r.JamAlerted)
            {
                if (!p.JamListed)
                {
                    p.JamListed = true;
                    Jammed[p.Id] = p; // 读档 / 生产索引重建后：已经告过警的那一段静默恢复进告警列表（不重复告警）。
                    Revision++;
                }
                return;
            }
            if (now - r.JamSinceTick < JamTicks)
            {
                return;
            }
            r.JamAlerted = true;
            p.JamListed = true;
            Jammed[p.Id] = p;
            JamAlerts++;
            Revision++;
            string name = BuildingOps.NameOf(p.Building);
            string since = AwayReportService.Duration(now - r.JamSinceTick);
            var at = new Vector3(p.Building.Position.x, 0f, p.Building.Position.y);
            if (kind == JamStarved)
            {
                NotificationCenter.Post("input_starved", GameText.Format("alarm.notify.starved", name, ItemCatalog.NameOf(p.ReasonItem?.Id), since), at);
            }
            else
            {
                NotificationCenter.Post("output_blocked", GameText.Format("alarm.notify.blocked", name, since, ProductionService.ReasonText(state, p)), at);
            }
            GuidanceHooks.Raise(GuidanceHooks.AlarmFirstFactoryJam);
        }

        // ── 机器重伤：在受伤 / 阵亡的那一刻判定（MachineRegistry 唯一写入口调用）──────────────

        /// <summary>机器受伤（<paramref name="healthBefore"/> = 这一下之前的耐久）或阵亡：跌破阈值的那一下发一次“机器重伤”警告；阵亡的移出重伤集合（阵亡另有紧急通知）。</summary>
        public static void OnMachineChanged(MachineRecord m, float healthBefore)
        {
            if (m == null)
            {
                return;
            }
            if (!m.IsAlive)
            {
                if (Wounded.Remove(m.LogicId))
                {
                    Revision++;
                }
                return;
            }
            float limit = m.MaxHealth * WoundedFraction;
            if (m.MaxHealth <= 0f || m.Health > limit)
            {
                return;
            }
            if (Wounded.Add(m.LogicId))
            {
                Revision++;
            }
            if (healthBefore > limit)
            {
                WoundedAlerts++;
                Vector2 pos = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                NotificationCenter.Post("machine_wounded",
                    GameText.Format("alarm.notify.wounded", MachineNaming.Short(m), Mathf.RoundToInt(m.Health), Mathf.RoundToInt(m.MaxHealth)), new Vector3(pos.x, 0f, pos.y));
                GuidanceHooks.Raise(GuidanceHooks.AlarmFirstMachineWounded);
            }
        }

        private static void EnsureWounded(CampaignState state)
        {
            if (ReferenceEquals(_woundedState, state))
            {
                return;
            }
            // 换战役 / 读档：按存档里的耐久重建一次（O(机器数)，只在这里）。
            _woundedState = state;
            Wounded.Clear();
            WoundedRebuilds++;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.MaxHealth > 0f && m.Health <= m.MaxHealth * WoundedFraction)
                {
                    Wounded.Add(m.LogicId);
                }
            }
            Revision++;
        }

        // ── 汇总 ─────────────────────────────────────────────────────────────

        private static readonly List<int> _woundedScratch = new List<int>(4);
        private static readonly List<string> _jamScratch = new List<string>(4);

        /// <summary>每次界面刷新调用：返回当前应显示的告警（已去重、已按威胁排序，同级按键排序）。</summary>
        public static List<AlertRecord> Collect(CampaignState state)
        {
            var result = new List<AlertRecord>();
            if (state == null)
            {
                _seen.Clear();
                return result;
            }
            CollectCore(state, result);
            CollectPower(state, result);
            CollectStorage(state, result);
            CollectFactory(state, result);
            CollectMachines(state, result);
            CollectWork(state, result);
            CollectSoftlock(state, result);
            result.Sort((a, b) => a.Severity != b.Severity
                ? a.Severity.CompareTo(b.Severity)
                : string.CompareOrdinal(a.Key, b.Key));
            return result;
        }

        private static void CollectCore(CampaignState state, List<AlertRecord> into)
        {
            BuildingRecord core = Core(state);
            if (core != null && BuildingOps.IsWorn(core))
            {
                into.Add(new AlertRecord("core:" + core.BuildingId, Severity.CoreThreatened,
                    GameText.Format("alarm.core.damaged", Mathf.RoundToInt(BuildingOps.Durability(core)), Mathf.RoundToInt(BuildingOps.MaxDurability(core.BuildingTypeId))),
                    0, core.BuildingId, new Vector3(core.Position.x, 0f, core.Position.y)));
            }
            foreach (TransitGroupRecord g in state.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>())
            {
                if (g != null && g.Kind == TransitGroupKind.Raid && g.State == TransitGroupState.Arrived)
                {
                    into.Add(new AlertRecord("raid:" + g.GroupId, Severity.CoreThreatened,
                        GameText.Format("alarm.core.raid", g.UnitCount.ToString(CultureInfo.InvariantCulture)), 0, null, new Vector3((float)g.PosX, 0f, (float)g.PosY)));
                }
            }
        }

        private static BuildingRecord Core(CampaignState state)
        {
            if (!ReferenceEquals(_coreState, state) || _coreId == null)
            {
                _coreState = state;
                _coreId = null;
                foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
                {
                    if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && b.RegionId == HomeValleyLayout.RegionId)
                    {
                        _coreId = b.BuildingId; // 核心不能拆、不能搬：编号整局不变，换战役时才重找。
                        break;
                    }
                }
            }
            return _coreId == null ? null : HomeGridService.FindBuilding(state, _coreId);
        }

        private static void CollectPower(CampaignState state, List<AlertRecord> into)
        {
            if (!HomeValleyPowerGrid.TryGetCachedSummary(state, out HomeValleyPowerGrid.GridSummary sum) || sum.BrownoutBuildingIds == null || sum.BrownoutBuildingIds.Length == 0)
            {
                return;
            }
            string first = sum.BrownoutBuildingIds[0];
            BuildingRecord b = HomeGridService.FindBuilding(state, first);
            into.Add(new AlertRecord("power", Severity.PowerCritical,
                GameText.Format("alarm.power.brownout", sum.BrownoutBuildingIds.Length.ToString(CultureInfo.InvariantCulture), b != null ? BuildingOps.NameOf(b) : first),
                0, first, b != null ? new Vector3(b.Position.x, 0f, b.Position.y) : (Vector3?)null));
        }

        private static void CollectStorage(CampaignState state, List<AlertRecord> into)
        {
            if (!ReferenceEquals(_storageState, state) || _storageInvRev != HomeInventory.Revision || _storageOpsRev != BuildingOps.Revision)
            {
                _storageState = state;
                _storageInvRev = HomeInventory.Revision;
                _storageOpsRev = BuildingOps.Revision;
                _storageFull.Clear();
                StorageScans++;
                bool wh = HomeInventory.WarehouseOperational(state);
                foreach (ItemDef item in ItemCatalog.Items)
                {
                    if (item == null || item.Form != ItemForm.Solid)
                    {
                        continue;
                    }
                    int stock = HomeInventory.Stock(state, item);
                    int cap = HomeInventory.Capacity(state, item, wh);
                    if (stock > 0 && cap > 0 && stock >= cap)
                    {
                        _storageFull.Add(item.Id);
                    }
                }
            }
            if (_storageFull.Count > 0)
            {
                var names = new List<string>(_storageFull.Count);
                foreach (string id in _storageFull)
                {
                    names.Add(ItemCatalog.NameOf(id));
                }
                BuildingRecord wh = FirstWarehouse(state);
                into.Add(new AlertRecord("storage:items", Severity.StorageFull, GameText.Format("alarm.storage.items", string.Join(GameText.Get("stats.list_sep"), names)),
                    0, wh?.BuildingId, wh != null ? new Vector3(wh.Position.x, 0f, wh.Position.y) : (Vector3?)null));
            }
        }

        private static BuildingRecord FirstWarehouse(CampaignState state)
        {
            IReadOnlyList<BuildingRecord> list = BuildingOps.WarehousesOf(state);
            return list != null && list.Count > 0 ? list[0] : Core(state);
        }

        private static void CollectFactory(CampaignState state, List<AlertRecord> into)
        {
            if (Jammed.Count == 0)
            {
                return;
            }
            long now = GameClock.Ticks;
            _jamScratch.Clear();
            foreach (KeyValuePair<string, ProductionService.Producer> kv in Jammed)
            {
                ProductionService.Producer p = kv.Value;
                // 生产索引重建过（建筑被拆 / 读档）时这个视图已失效：丢掉，下一个生产步按存档重新登记。
                if (p?.Rec == null || p.Rec.JamKind == JamNone || !p.Rec.JamAlerted || !ProductionService.TryGet(state, kv.Key, out ProductionService.Producer live) || !ReferenceEquals(live, p))
                {
                    _jamScratch.Add(kv.Key);
                    if (p != null)
                    {
                        p.JamListed = false;
                    }
                    continue;
                }
                // 修复轮（审查 P2，FGR-ECO-080 / B08 聚合）：同一种卡法（缺料 / 堵塞）聚成一条，定位到卡得最久的那座（同样久按建筑 ID 先后，确定）。
                bool starved = p.Rec.JamKind == JamStarved;
                if (starved)
                {
                    _starvedCount++;
                    if (_starvedWorst == null || p.Rec.JamSinceTick < _starvedWorst.Rec.JamSinceTick)
                    {
                        _starvedWorst = p;
                    }
                }
                else
                {
                    _blockedCount++;
                    if (_blockedWorst == null || p.Rec.JamSinceTick < _blockedWorst.Rec.JamSinceTick)
                    {
                        _blockedWorst = p;
                    }
                }
            }
            foreach (string id in _jamScratch)
            {
                Jammed.Remove(id);
            }
            AddJam(state, _starvedWorst, _starvedCount, now, into);
            AddJam(state, _blockedWorst, _blockedCount, now, into);
            _starvedWorst = _blockedWorst = null;
            _starvedCount = _blockedCount = 0;
        }

        private static ProductionService.Producer _starvedWorst;
        private static ProductionService.Producer _blockedWorst;
        private static int _starvedCount;
        private static int _blockedCount;

        /// <summary>一种卡法一条告警：只有一座时写那一座；多座时写“N 座建筑持续缺料，最久：…”（点击定位到最久的那座）。</summary>
        private static void AddJam(CampaignState state, ProductionService.Producer p, int count, long now, List<AlertRecord> into)
        {
            if (p == null || count <= 0)
            {
                return;
            }
            string name = BuildingOps.NameOf(p.Building);
            string since = AwayReportService.Duration(now - p.Rec.JamSinceTick);
            bool starved = p.Rec.JamKind == JamStarved;
            string one = starved
                ? GameText.Format("alarm.factory.starved", name, ItemCatalog.NameOf(p.ReasonItem?.Id), since)
                : GameText.Format("alarm.factory.blocked", name, since, ProductionService.ReasonText(state, p));
            string msg = count == 1 ? one : GameText.Format(starved ? "alarm.factory.starved_many" : "alarm.factory.blocked_many", count, one);
            string key = count == 1 ? "jam:" + p.Id : starved ? "jam:~starved" : "jam:~blocked";
            into.Add(new AlertRecord(key, Severity.FactoryJammed, msg, 0, p.Id, new Vector3(p.Building.Position.x, 0f, p.Building.Position.y)));
        }

        /// <summary>工单面板告警栏的行数：等于等级数（六级），每个等级都能至少露出一条。</summary>
        public const int DisplayRows = 6;

        /// <summary>
        /// 修复轮（审查 P2）：告警栏只有 <paramref name="max"/> 行时挑哪些显示——先保证每个等级至少露出一条（按等级从高到低），
        /// 再按原顺序补满；结果保持 <see cref="Collect"/> 的排序。<paramref name="hidden"/> = 没显示出来的条数（界面写“另有 N 条”）。O(告警数)。
        /// </summary>
        public static List<AlertRecord> PickForDisplay(List<AlertRecord> all, int max, out int hidden)
        {
            hidden = 0;
            if (all == null || all.Count <= max || max <= 0)
            {
                return all ?? new List<AlertRecord>();
            }
            var take = new bool[all.Count];
            int taken = 0;
            for (int i = 0; i < all.Count && taken < max; i++)
            {
                if (i == 0 || all[i].Severity != all[i - 1].Severity)
                {
                    take[i] = true;
                    taken++;
                }
            }
            for (int i = 0; i < all.Count && taken < max; i++)
            {
                if (!take[i])
                {
                    take[i] = true;
                    taken++;
                }
            }
            var shown = new List<AlertRecord>(max);
            for (int i = 0; i < all.Count; i++)
            {
                if (take[i])
                {
                    shown.Add(all[i]);
                }
            }
            hidden = all.Count - shown.Count;
            return shown;
        }

        private static void CollectMachines(CampaignState state, List<AlertRecord> into)
        {
            EnsureWounded(state);
            if (Wounded.Count == 0)
            {
                return;
            }
            float frac = WoundedFraction;
            _woundedScratch.Clear();
            foreach (int id in Wounded)
            {
                // 修好了（维修 / 送修把耐久写回）或已阵亡：移出（不需要维修路径另外通知告警）。
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || !m.IsAlive || m.MaxHealth <= 0f || m.Health > m.MaxHealth * frac)
                {
                    _woundedScratch.Add(id);
                    continue;
                }
                Vector2 pos = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
                into.Add(new AlertRecord("machine:" + id.ToString("D6", CultureInfo.InvariantCulture), Severity.MachineWounded,
                    GameText.Format("alarm.machine.wounded", MachineNaming.Short(m), Mathf.RoundToInt(m.Health), Mathf.RoundToInt(m.MaxHealth)),
                    id, null, new Vector3(pos.x, 0f, pos.y), m.RegionId));
            }
            foreach (int id in _woundedScratch)
            {
                Wounded.Remove(id);
            }
        }

        /// <summary>
        /// FG4-ECO-10（FGR-ECO-072）：死锁检测已经告过警的那几处——传送带闭环卡死（归“工厂堵塞”级）、施工 / 维修目标机器到不了（归“工作受阻”级）。
        /// 只读存档里的检测结果（<see cref="SoftlockService"/> 按间隔检测），O(告警条数)；恢复后检测结果清掉，这里随之消失。
        /// </summary>
        private static void CollectSoftlock(CampaignState state, List<AlertRecord> into)
        {
            SoftlockState st = state.Economy?.Softlock;
            if (st == null)
            {
                return;
            }
            foreach (LoopWatchRecord w in st.Loops ?? Array.Empty<LoopWatchRecord>())
            {
                if (w == null || !w.Alerted)
                {
                    continue;
                }
                int items = 0;
                if (Logistics.BeltNetworkService.IsRunning)
                {
                    int net = Logistics.BeltNetworkService.Kernel.NetworkOf(w.X, w.Y);
                    if (net >= 0 && Logistics.BeltNetworkService.Kernel.TryGetNetworkStats(net, out BinGames.Sim.Logistics.BeltNetworkStats ns))
                    {
                        items = ns.Items;
                    }
                }
                into.Add(new AlertRecord("loop:" + w.X.ToString(CultureInfo.InvariantCulture) + "," + w.Y.ToString(CultureInfo.InvariantCulture), Severity.FactoryJammed,
                    GameText.Format("softlock.loop.alarm", w.X, w.Y, items), 0, null, new Vector3(w.X, 0f, w.Y)));
            }
            foreach (ReachWatchRecord w in st.Sites ?? Array.Empty<ReachWatchRecord>())
            {
                if (w == null || !w.Alerted)
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, w.BuildingId);
                if (b == null)
                {
                    continue;
                }
                into.Add(new AlertRecord("reach:" + w.BuildingId, Severity.WorkBlocked,
                    GameText.Format("softlock.reach.alarm", BuildingOps.NameOf(b), SoftlockService.SiteKindText(state, w.BuildingId)), 0, w.BuildingId,
                    new Vector3(b.Position.x, 0f, b.Position.y)));
            }
        }

        private static void CollectWork(CampaignState state, List<AlertRecord> into)
        {
            if (state.WorkOrders == null || state.WorkOrders.Length == 0)
            {
                _seen.Clear();
                return;
            }
            HashSet<string> stillActive = null;
            foreach (WorkOrderRecord order in state.WorkOrders)
            {
                if (order.State != WorkOrderState.Waiting)
                {
                    continue;
                }
                Severity? severity = null;
                string message = null;
                if (order.FailureReason == "path-blocked")
                {
                    severity = Severity.WorkBlocked;
                    message = GameText.Format("alarm.work.blocked", WorkKindName(order.Kind));
                }
                else if (order.FailureReason != null && order.FailureReason.StartsWith("storage-full", StringComparison.Ordinal))
                {
                    severity = Severity.StorageFull;
                    message = GameText.Get("alarm.storage.haul");
                }
                if (severity == null)
                {
                    continue; // 其余等待原因（等材料、没劳动力……）由施工队列 / 通知说明，不进告警。
                }
                (stillActive ??= new HashSet<string>(StringComparer.Ordinal)).Add(order.WorkOrderId);
                if (_seen.Add(order.WorkOrderId))
                {
                    Log.Warning($"[HomeValleyAlarms] {message}（{order.WorkOrderId}）。");
                }
                int machine = order.AssignedMachineLogicId;
                Vector3? at = null;
                if (machine > 0 && MachineRegistry.TryGetLivePosition(machine, out Vector2 live))
                {
                    at = new Vector3(live.x, 0f, live.y);
                }
                into.Add(new AlertRecord(order.WorkOrderId, severity.Value, message, machine, null, at));
            }
            if (stillActive == null)
            {
                _seen.Clear();
            }
            else
            {
                _seen.IntersectWith(stillActive); // 离开告警状态的订单从记忆移除，允许未来重新告警。
            }
        }

        /// <summary>工单种类的显示名（文本键）。</summary>
        public static string WorkKindName(WorkOrderKind kind)
        {
            string fromRules = StandingRuleService.KindTextOf(kind);
            if (!string.IsNullOrEmpty(fromRules))
            {
                return fromRules;
            }
            switch (kind)
            {
                case WorkOrderKind.Haul: return GameText.Get("alarm.kind.haul");
                case WorkOrderKind.Build: return GameText.Get("alarm.kind.build");
                case WorkOrderKind.Repair: return GameText.Get("alarm.kind.repair");
                case WorkOrderKind.Salvage: return GameText.Get("alarm.kind.salvage");
                case WorkOrderKind.Recharge: return GameText.Get("alarm.kind.recharge");
                default: return GameText.Get("alarm.kind.other");
            }
        }

        // ── 定位 ─────────────────────────────────────────────────────────────

        /// <summary>点击告警：镜头飞到告警的位置（与通知定位同一个入口；没有位置 / 没有镜头时给出原因文本键，不静默）。</summary>
        public static bool Locate(AlertRecord a, out string failureKey)
        {
            failureKey = "ui.notify.no_location";
            if (!a.HasLocation)
            {
                return false;
            }
            if (NotificationCenter.LocateHandler == null)
            {
                failureKey = "ui.notify.no_camera";
                return false;
            }
            return NotificationCenter.LocateHandler(a.RegionId, a.Position, out failureKey);
        }

        /// <summary>自检：当前在“工厂堵塞”里的建筑数 / “机器重伤”里的机器数。</summary>
        public static int JammedCount => Jammed.Count;
        public static int WoundedCount => Wounded.Count;
    }
}
