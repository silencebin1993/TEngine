using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>发电类别的种类（fg.TbPowerSource.kind）。</summary>
    public enum PowerSourceKind : byte
    {
        /// <summary>运转中就按满额发电（归还核心、发电机）。</summary>
        Fixed = 0,
        /// <summary>烧燃油、按负荷出力（燃油发电机；燃油由生产建筑运行时 ProductionService 烧）。</summary>
        Fuel = 1,
        /// <summary>乘昼夜 / 天气系数（太阳能阵列，<see cref="PowerEnvironment"/>）。</summary>
        Solar = 2,
    }

    /// <summary>一种发电建筑的类别（fg.TbPowerSource 一行的运行时视图）。</summary>
    public readonly struct PowerSourceDef
    {
        public readonly string TypeId;
        public readonly int ClassIndex;
        public readonly PowerSourceKind Kind;
        public readonly string LegendKey;

        public PowerSourceDef(string typeId, int classIndex, PowerSourceKind kind, string legendKey)
        {
            TypeId = typeId;
            ClassIndex = classIndex;
            Kind = kind;
            LegendKey = legendKey;
        }
    }

    /// <summary>一座储能站的充放电设置（卡片“储能站的充放电设置”）。</summary>
    public readonly struct StorageSettings
    {
        public readonly bool NoCharge;
        public readonly bool NoDischarge;
        /// <summary>放电只给优先级 ≤ 这个数（1～3）；0 = 给所有建筑。</summary>
        public readonly int Reserve;

        public StorageSettings(bool noCharge, bool noDischarge, int reserve)
        {
            NoCharge = noCharge;
            NoDischarge = noDischarge;
            Reserve = reserve;
        }

        public bool IsDefault => !NoCharge && !NoDischarge && Reserve == 0;
    }

    /// <summary>
    /// FG4-ECO-04 能源扩展（FG04 第 3.3 节能源三行；卡片“各类发电设施在电力曲线里分开显示”“储能站的充放电设置”；负向“燃油耗尽”“储能站满或空”）：
    /// - 发电类别（fg.TbPowerSource）：每种发电建筑一个类别，曲线按类别分开记；太阳能的类别乘 <see cref="PowerEnvironment.SolarFactor"/>；燃油按负荷出力。
    /// - 燃油发电机：有没有油由生产建筑运行时决定（ProductionService 按负荷烧、烧空 / 来油时调 <see cref="SetGeneratorFueled"/>），这里只开关它的发电并重新结算（不重建拓扑）。
    /// - 储能站设置（<see cref="TrySetStorageSettings"/>）：关掉充电 / 放电、放电只留给优先级 1～N；进存档（PowerGridState），拆掉的储能站的设置在重算时清掉。
    /// - 环境：每游戏秒查一次太阳能系数（O(1)），变了才重新结算；储能放空发警告（O(电网数)）。
    /// 全部只在状态变化点做 O(建筑) 的写回；逐建筑结算在 AOT 内核。
    /// </summary>
    public static partial class HomeValleyPowerGrid
    {
        private static readonly Dictionary<string, PowerSourceDef> SourceDefs = new Dictionary<string, PowerSourceDef>(StringComparer.Ordinal);
        private static readonly List<string> ClassLegendKeys = new List<string>(8);
        private static readonly List<PowerSourceKind> ClassKinds = new List<PowerSourceKind>(8);
        private static int _sourceRevision = -1;
        private static int _otherClass = -1;

        /// <summary>读 fg.TbPowerSource（按 sortOrder；类别按首次出现的顺序编号，最多 8 类）。表里没有的发电建筑归“其他”类（数据检查会拦，运行时兜底不崩）。</summary>
        private static void EnsureSourceDefs()
        {
            if (_sourceRevision == GridContent.Revision && ClassLegendKeys.Count > 0)
            {
                return;
            }
            SourceDefs.Clear();
            ClassLegendKeys.Clear();
            ClassKinds.Clear();
            _otherClass = -1;
            var classIds = new List<string>(8);
            GameConfig.fg.TbPowerSource table = ConfigSystem.Instance?.Tables?.TbPowerSource;
            var rows = new List<GameConfig.fg.PowerSource>();
            if (table != null)
            {
                rows.AddRange(table.DataList);
                rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.TypeId, b.TypeId));
            }
            foreach (GameConfig.fg.PowerSource row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.TypeId))
                {
                    continue;
                }
                PowerSourceKind kind = row.Kind == "fuel" ? PowerSourceKind.Fuel : row.Kind == "solar" ? PowerSourceKind.Solar : PowerSourceKind.Fixed;
                int c = classIds.IndexOf(row.SourceClass);
                if (c < 0)
                {
                    if (classIds.Count >= PowerKernel.MaxSourceClasses - 1)
                    {
                        Log.Error($"[HomeValleyPowerGrid] fg.TbPowerSource 发电类别超过 {PowerKernel.MaxSourceClasses - 1} 类，{row.TypeId} 归“其他”类");
                        c = OtherClass(classIds);
                    }
                    else
                    {
                        classIds.Add(row.SourceClass);
                        ClassLegendKeys.Add(row.LegendKey);
                        ClassKinds.Add(kind);
                        c = classIds.Count - 1;
                    }
                }
                SourceDefs[row.TypeId] = new PowerSourceDef(row.TypeId, c, kind, row.LegendKey);
            }
            if (ClassLegendKeys.Count == 0)
            {
                // 表缺失（编辑器里表还没生成）：至少有一个类别，保证核心基础供电能记曲线。
                ClassLegendKeys.Add("power.source.core");
                ClassKinds.Add(PowerSourceKind.Fixed);
                classIds.Add("core");
            }
            _sourceRevision = GridContent.Revision;
        }

        private static int OtherClass(List<string> classIds)
        {
            if (_otherClass < 0)
            {
                classIds.Add("@other");
                ClassLegendKeys.Add("power.source.other");
                ClassKinds.Add(PowerSourceKind.Fixed);
                _otherClass = classIds.Count - 1;
            }
            return _otherClass;
        }

        /// <summary>这种建筑的发电类别（核心 = “core” 那一行）。表里没有时归“其他”类。</summary>
        public static PowerSourceDef SourceOf(string typeId)
        {
            EnsureSourceDefs();
            if (typeId != null && SourceDefs.TryGetValue(typeId, out PowerSourceDef d))
            {
                return d;
            }
            var ids = new List<string>(ClassLegendKeys.Count);
            for (int i = 0; i < ClassLegendKeys.Count; i++)
            {
                ids.Add(i == _otherClass ? "@other" : ClassLegendKeys[i]);
            }
            int other = _otherClass >= 0 ? _otherClass : OtherClass(ids);
            return new PowerSourceDef(typeId, other, PowerSourceKind.Fixed, "power.source.other");
        }

        public static bool TryGetSourceDef(string typeId, out PowerSourceDef def)
        {
            EnsureSourceDefs();
            def = default;
            return typeId != null && SourceDefs.TryGetValue(typeId, out def);
        }

        /// <summary>发电类别个数（曲线 / 图例按 0..N−1 画）。</summary>
        public static int SourceClassCount
        {
            get
            {
                EnsureSourceDefs();
                return ClassLegendKeys.Count;
            }
        }

        /// <summary>第 c 类发电的名字（图例 / 读数）。</summary>
        public static string SourceClassName(int c)
        {
            EnsureSourceDefs();
            return c >= 0 && c < ClassLegendKeys.Count ? GameText.Get(ClassLegendKeys[c]) : GameText.Get("power.source.other");
        }

        public static PowerSourceKind SourceClassKind(int c)
        {
            EnsureSourceDefs();
            return c >= 0 && c < ClassKinds.Count ? ClassKinds[c] : PowerSourceKind.Fixed;
        }

        /// <summary>这种建筑是燃油发电机（发电类别 kind = fuel）。</summary>
        public static bool IsFuelGeneratorType(string typeId) => TryGetSourceDef(typeId, out PowerSourceDef d) && d.Kind == PowerSourceKind.Fuel;

        /// <summary>这种建筑是太阳能（发电类别 kind = solar）。</summary>
        public static bool IsSolarType(string typeId) => TryGetSourceDef(typeId, out PowerSourceDef d) && d.Kind == PowerSourceKind.Solar;

        /// <summary>这种建筑是储能站（电力节点表里有储能）。</summary>
        public static bool IsStorageType(string typeId) => TryGetNodeDef(typeId, out PowerNodeDef d) && d.StorageCapacity > 0f && d.StorageRate > 0f;

        // ── 环境（太阳能系数）──────────────────────────────────────────────────

        /// <summary>按 <see cref="PowerEnvironment.SolarFactor"/> 刷新太阳能类别的系数（量化到 1%，免得黄昏时每秒都重新结算）。变了返回 true（调用方随后结算）。</summary>
        private static bool RefreshEnvironment(CampaignState state, PowerKernel k)
        {
            EnsureSourceDefs();
            float f = Mathf.Round(PowerEnvironment.SolarFactor(state) * 100f) / 100f;
            bool changed = false;
            for (int c = 0; c < ClassKinds.Count; c++)
            {
                if (ClassKinds[c] == PowerSourceKind.Solar)
                {
                    changed |= k.SetClassFactor(c, f);
                }
            }
            return changed;
        }

        /// <summary>太阳能这一刻的系数（读数用；没有绑定时按环境现算）。</summary>
        public static float SolarFactorNow(CampaignState state)
        {
            EnsureSourceDefs();
            if (_kernel != null && ReferenceEquals(state, _state))
            {
                for (int c = 0; c < ClassKinds.Count; c++)
                {
                    if (ClassKinds[c] == PowerSourceKind.Solar)
                    {
                        return _kernel.ClassFactor(c);
                    }
                }
            }
            return Mathf.Round(PowerEnvironment.SolarFactor(state) * 100f) / 100f;
        }

        // ── 燃油发电机（ProductionService 调）────────────────────────────────────

        /// <summary>
        /// 燃油发电机有油 / 烧空（ProductionService 在状态翻转时调）：开关这座的发电，重新结算（不重建拓扑）、写回供电结果与汇总、发缺电 / 恢复反馈。
        /// 返回内核里这座建筑的发电开关变了没有（没绑定 / 不在电网实体里时 false，下一次重算按记录里的 Fueled 组装）。
        /// </summary>
        public static bool SetGeneratorFueled(CampaignState state, string buildingId, bool fueled)
        {
            if (_kernel == null || !ReferenceEquals(state, _state) || buildingId == null || !EntityOf.TryGetValue(buildingId, out int i) || i >= _entityCount)
            {
                return false;
            }
            BuildingRecord b = _recordAt[i];
            bool on = fueled && b != null && b.ConstructionState == BuildingConstructionState.Operational && _entities[i].Supply > 0f;
            if (!_kernel.SetSupplyOn(i, on))
            {
                return false;
            }
            _entities[i].SupplyOn = on;
            _kernel.Settle();
            ApplyResults(state, topologyChanged: false);
            return true;
        }

        /// <summary>燃油发电机这一刻的负荷比例（0～1；没接入电网 / 没油 = 0）。O(1)。</summary>
        public static float GeneratorLoad(CampaignState state, string buildingId)
        {
            if (_kernel == null || !ReferenceEquals(state, _state) || buildingId == null || !EntityOf.TryGetValue(buildingId, out int i) || i >= _entityCount)
            {
                return 0f;
            }
            return _kernel.LoadOf(i);
        }

        /// <summary>这座发电建筑是否接入了电网（O(1)）。</summary>
        public static bool IsConnected(CampaignState state, string buildingId) =>
            TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info) && info.Subnet >= 0;

        /// <summary>这座发电建筑这一刻实际送进电网的电（接入 + 发电开着 + 乘类别系数；燃油发电机再乘负荷）。O(1)。</summary>
        public static float OutputOf(CampaignState state, string buildingId)
        {
            if (!TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info) || info.Subnet < 0)
            {
                return 0f;
            }
            float eff = _kernel.EffectiveSupply(info.Entity);
            return _entities[info.Entity].Dispatchable ? eff * _kernel.LoadOf(info.Entity) : eff;
        }

        /// <summary>这座发电建筑这一刻能发的电（不乘负荷；燃油没油 = 0、太阳能乘光照）。O(1)。没接入也照算。</summary>
        public static float AvailableSupplyOf(CampaignState state, string buildingId)
        {
            if (!TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info))
            {
                return 0f;
            }
            return _kernel.EffectiveSupply(info.Entity);
        }

        /// <summary>按存档里的燃油状态组装（BuildEntities 用）：fuel 类建筑的 ProducerRecord.Fueled。只在有燃油发电机时建表，O(生产建筑)。</summary>
        private static readonly Dictionary<string, bool> FueledScratch = new Dictionary<string, bool>(StringComparer.Ordinal);

        private static void CollectFueled(CampaignState state)
        {
            FueledScratch.Clear();
            ProducerRecord[] prods = state.Economy?.Producers;
            if (prods == null)
            {
                return;
            }
            foreach (ProducerRecord r in prods)
            {
                if (r != null && r.Fueled && r.BuildingId != null)
                {
                    FueledScratch[r.BuildingId] = true;
                }
            }
        }

        // ── 储能站设置 ────────────────────────────────────────────────────────

        private static readonly Dictionary<string, StorageSettings> StorageScratch = new Dictionary<string, StorageSettings>(StringComparer.Ordinal);

        /// <summary>读存档里的储能站设置（BuildEntities 用；O(有设置的储能站)）。</summary>
        private static void CollectStorageSettings(CampaignState state)
        {
            StorageScratch.Clear();
            PowerGridState p = state.Power;
            if (p?.StorageSettingIds == null)
            {
                return;
            }
            for (int i = 0; i < p.StorageSettingIds.Length; i++)
            {
                string id = p.StorageSettingIds[i];
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                bool nc = p.StorageNoCharge != null && i < p.StorageNoCharge.Length && p.StorageNoCharge[i];
                bool nd = p.StorageNoDischarge != null && i < p.StorageNoDischarge.Length && p.StorageNoDischarge[i];
                int rs = p.StorageReserve != null && i < p.StorageReserve.Length ? Mathf.Clamp(p.StorageReserve[i], 0, PowerKernel.PriorityLevels - 1) : 0;
                StorageScratch[id] = new StorageSettings(nc, nd, rs);
            }
        }

        /// <summary>一座储能站的设置（没设置过 = 默认）。</summary>
        public static StorageSettings GetStorageSettings(CampaignState state, string buildingId)
        {
            PowerGridState p = state?.Power;
            if (p?.StorageSettingIds != null && buildingId != null)
            {
                int i = Array.IndexOf(p.StorageSettingIds, buildingId);
                if (i >= 0)
                {
                    return new StorageSettings(i < p.StorageNoCharge.Length && p.StorageNoCharge[i], i < p.StorageNoDischarge.Length && p.StorageNoDischarge[i],
                        i < p.StorageReserve.Length ? Mathf.Clamp(p.StorageReserve[i], 0, PowerKernel.PriorityLevels - 1) : 0);
                }
            }
            return default;
        }

        /// <summary>放电对象最多能留给前几档优先级（fg.TbHomeTuning power.storage.reserve_levels，1～3）。</summary>
        public static int MaxReserveLevel => Mathf.Clamp(GridContent.TryGetTuning("power.storage.reserve_levels", out float v) ? Mathf.RoundToInt(v) : 3, 1, PowerKernel.PriorityLevels - 1);

        /// <summary>
        /// 玩家在电网面板改储能站设置（卡片“储能站的充放电设置”；负向“停电期间按优先级断电，储能站按设置放电”）。null = 这一项不变。
        /// 写进存档域、改内核里这座的设置、重新结算（不重建拓扑）。不是储能站 / 找不到 / 放电对象超范围时拒绝。
        /// </summary>
        public static GridResult TrySetStorageSettings(CampaignState state, string buildingId, bool? noCharge = null, bool? noDischarge = null, int? reserve = null)
        {
            if (state == null || string.IsNullOrEmpty(buildingId))
            {
                return GridResult.Fail("invalid-args");
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || HomeGridService.IsRelocationGhost(b))
            {
                return GridResult.Fail($"building-not-found:{buildingId}");
            }
            if (!IsStorageType(b.BuildingTypeId))
            {
                return GridResult.Fail($"not-storage:{b.BuildingTypeId}");
            }
            if (reserve.HasValue && (reserve.Value < 0 || reserve.Value > MaxReserveLevel))
            {
                return GridResult.Fail($"reserve-out-of-range:{reserve.Value}");
            }
            StorageSettings cur = GetStorageSettings(state, buildingId);
            var next = new StorageSettings(noCharge ?? cur.NoCharge, noDischarge ?? cur.NoDischarge, reserve ?? cur.Reserve);
            WriteStorageSettings(state, buildingId, next);
            if (_kernel != null && ReferenceEquals(state, _state) && EntityOf.TryGetValue(buildingId, out int i) && i < _entityCount)
            {
                if (_kernel.SetStorageSettings(i, next.NoCharge, next.NoDischarge, (byte)next.Reserve))
                {
                    _entities[i].StorageNoCharge = next.NoCharge;
                    _entities[i].StorageNoDischarge = next.NoDischarge;
                    _entities[i].StorageReserve = (byte)next.Reserve;
                    _kernel.Settle();
                    ApplyResults(state, topologyChanged: false);
                }
            }
            else
            {
                Recompute(state);
            }
            return GridResult.Ok();
        }

        private static void WriteStorageSettings(CampaignState state, string buildingId, StorageSettings s)
        {
            CampaignFgStateDomains.EnsureAll(state);
            PowerGridState p = state.Power;
            var ids = new List<string>(p.StorageSettingIds);
            var nc = new List<bool>(p.StorageNoCharge);
            var nd = new List<bool>(p.StorageNoDischarge);
            var rs = new List<int>(p.StorageReserve);
            while (nc.Count < ids.Count)
            {
                nc.Add(false);
            }
            while (nd.Count < ids.Count)
            {
                nd.Add(false);
            }
            while (rs.Count < ids.Count)
            {
                rs.Add(0);
            }
            int i = ids.IndexOf(buildingId);
            if (s.IsDefault)
            {
                if (i >= 0)
                {
                    ids.RemoveAt(i);
                    nc.RemoveAt(i);
                    nd.RemoveAt(i);
                    rs.RemoveAt(i);
                }
            }
            else if (i >= 0)
            {
                nc[i] = s.NoCharge;
                nd[i] = s.NoDischarge;
                rs[i] = s.Reserve;
            }
            else
            {
                ids.Add(buildingId);
                nc.Add(s.NoCharge);
                nd.Add(s.NoDischarge);
                rs.Add(s.Reserve);
            }
            p.StorageSettingIds = ids.ToArray();
            p.StorageNoCharge = nc.ToArray();
            p.StorageNoDischarge = nd.ToArray();
            p.StorageReserve = rs.ToArray();
        }

        /// <summary>拆掉的储能站：设置随之清掉（只在重算时、且有设置记录时检查，O(设置数)）。</summary>
        private static void PruneStorageSettings(CampaignState state)
        {
            PowerGridState p = state.Power;
            if (p?.StorageSettingIds == null || p.StorageSettingIds.Length == 0)
            {
                return;
            }
            bool any = false;
            foreach (string id in p.StorageSettingIds)
            {
                if (id == null || !EntityOf.ContainsKey(id))
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return;
            }
            var ids = new List<string>();
            var nc = new List<bool>();
            var nd = new List<bool>();
            var rs = new List<int>();
            for (int i = 0; i < p.StorageSettingIds.Length; i++)
            {
                string id = p.StorageSettingIds[i];
                if (id == null || !EntityOf.ContainsKey(id))
                {
                    continue;
                }
                ids.Add(id);
                nc.Add(i < p.StorageNoCharge.Length && p.StorageNoCharge[i]);
                nd.Add(i < p.StorageNoDischarge.Length && p.StorageNoDischarge[i]);
                rs.Add(i < p.StorageReserve.Length ? p.StorageReserve[i] : 0);
            }
            p.StorageSettingIds = ids.ToArray();
            p.StorageNoCharge = nc.ToArray();
            p.StorageNoDischarge = nd.ToArray();
            p.StorageReserve = rs.ToArray();
        }

        /// <summary>设置的一句话（“充电开 · 放电只给优先级 1～2”）。</summary>
        public static string DescribeStorageSettings(StorageSettings s)
        {
            string charge = GameText.Get(s.NoCharge ? "power.storage.charge_off" : "power.storage.charge_on");
            return GameText.Format("power.state.storage_setting", charge, DischargeText(s.NoDischarge ? -1 : s.Reserve));
        }

        /// <summary>放电对象：-1 = 不放电；0 = 所有建筑；1 = 只给优先级 1；N = 只给优先级 1～N。</summary>
        public static string DischargeText(int option) =>
            option < 0 ? GameText.Get("power.storage.discharge_off")
            : option == 0 ? GameText.Get("power.storage.discharge_all")
            : option == 1 ? GameText.Get("power.storage.discharge_p1")
            : GameText.Format("power.storage.discharge_upto", option);

        /// <summary>一座储能站这一刻的状态（已充满 / 已放空 / 充电 / 放电 / 不充不放 / 未接入）。</summary>
        public static string StorageStateText(CampaignState state, string buildingId)
        {
            if (!TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info) || !info.IsStorage)
            {
                return string.Empty;
            }
            BuildingRecord rec = _recordAt[info.Entity];
            if (rec != null && rec.ConstructionState != BuildingConstructionState.Operational)
            {
                // 还没建成 / 受损 / 关停：不充不放，写明是哪一种（不误报“未接入电网”）。
                return GameText.Get(rec.ConstructionState == BuildingConstructionState.Damaged ? "prod.state.damaged"
                    : rec.ConstructionState == BuildingConstructionState.Disabled ? "prod.state.disabled" : "prod.state.building");
            }
            if (info.Subnet < 0 || !_entities[info.Entity].StorageOn)
            {
                return GameText.Get("power.storage.unconnected");
            }
            double stored = _kernel.StoredOf(info.Entity);
            double cap = _entities[info.Entity].StorageCapacity;
            float flow = _kernel.StorageFlowOf(info.Entity);
            if (flow < -0.05f)
            {
                return GameText.Format("power.storage.discharging", Num(-flow));
            }
            if (flow > 0.05f)
            {
                return GameText.Format("power.storage.charging", Num(flow));
            }
            if (stored >= cap - 0.5)
            {
                return GameText.Get("power.storage.full");
            }
            if (_kernel.StorageChargeHeldByBrownout(info.Entity))
            {
                // FG4-ECO-04 修复轮（P2，B06）：电网有建筑停机、这座储能的放电对象又够得着它们——充进去马上就会放出去，所以不充；写明怎么办。
                return GameText.Get(stored <= 0.5 ? "power.storage.empty_brownout" : "power.storage.no_charge_brownout");
            }
            if (stored <= 0.5)
            {
                return GameText.Get("power.storage.empty");
            }
            return GameText.Get("power.storage.idle");
        }

        /// <summary>电网 s 的储能站（电网面板“储能站”一节；O(实体)，只在面板刷新时）。</summary>
        public static void StoragesOf(int s, List<BuildingRecord> into)
        {
            into.Clear();
            if (_kernel == null)
            {
                return;
            }
            for (int i = 0; i < _entityCount; i++)
            {
                if (_recordAt[i] != null && _entities[i].StorageCapacity > 0 && _kernel.SubnetOf(i) == s)
                {
                    into.Add(_recordAt[i]);
                }
            }
        }

        /// <summary>储能站的存量 / 容量（电·分钟）。</summary>
        public static bool TryGetStorage(CampaignState state, string buildingId, out double storedMinutes, out double capacityMinutes)
        {
            storedMinutes = capacityMinutes = 0;
            if (!TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info) || !info.IsStorage)
            {
                return false;
            }
            storedMinutes = _kernel.StoredOf(info.Entity) / 60.0;
            capacityMinutes = _entities[info.Entity].StorageCapacity / 60.0;
            return true;
        }

        // ── 储能放空警告（负向“储能站满或空”）─────────────────────────────────────

        /// <summary>存过电（超过容量 5%）的电网——放空时才警告；从没充过电的新储能站不算“放空”。</summary>
        private static readonly HashSet<int> EmptyNotified = new HashSet<int>();

        /// <summary>
        /// 每游戏秒（储能积分之后）：一个电网的储能曾经存过电（超过容量 5%），现在在缺电中放到 0 → 警告一次（可定位）、钩子；
        /// 之后再充到 5% 以上才重新“上膛”。刚建好、从没充过电的储能站不报。O(电网数)。
        /// </summary>
        private static void CheckStorageEmpty()
        {
            for (int s = 0; s < _kernel.SubnetCount; s++)
            {
                PowerSubnetInfo n = _kernel.Subnet(s);
                if (n.StorageUnits == 0 || n.StorageCapacity <= 0)
                {
                    continue;
                }
                if (n.Stored > n.StorageCapacity * 0.05)
                {
                    EmptyNotified.Add(n.Serial); // 上膛：存过电了
                }
                else if (n.Stored <= 0.5 && n.Demand > n.Delivered && EmptyNotified.Remove(n.Serial) && !_suppressFeedback)
                {
                    Vector2 at = SubnetAnchorPosition(s);
                    NotificationCenter.Post("power_storage_empty", GameText.Format("power.notify.storage_empty", SubnetName(n.Serial)), new Vector3(at.x, 0f, at.y));
                    GuidanceHooks.Raise(GuidanceHooks.EnergyFirstStorageEmpty);
                    LastStorageEmptyNotices++;
                }
            }
        }

        public static int LastStorageEmptyNotices { get; private set; }

        // ── 读数 ─────────────────────────────────────────────────────────────

        /// <summary>“发电构成：归还核心 20、发电机 80、太阳能 60”（只列有发电的类别）。</summary>
        public static string DescribeSources(int s)
        {
            if (_kernel == null || s < 0 || s >= _kernel.SubnetCount)
            {
                return string.Empty;
            }
            var parts = new List<string>(4);
            int n = SourceClassCount;
            for (int c = 0; c < n; c++)
            {
                float v = _kernel.ClassSupply(s, c);
                if (v > 0f)
                {
                    parts.Add(GameText.Format("power.hover.source_item", SourceClassName(c), Num(v)));
                }
            }
            return parts.Count == 0 ? string.Empty : GameText.Format("power.hover.sources", string.Join(GameText.Language == GameLanguage.En ? ", " : "、", parts));
        }

        /// <summary>发电建筑的读数行（太阳能写光照，燃油写负载 / 没油）。不是这两类时返回 null。</summary>
        private static string DescribeGeneratorExtra(CampaignState state, BuildingRecord b, BuildingPowerInfo info)
        {
            if (!TryGetSourceDef(b.BuildingTypeId, out PowerSourceDef def))
            {
                return null;
            }
            if (def.Kind == PowerSourceKind.Solar)
            {
                float f = SolarFactorNow(state);
                string phase = GameText.Get(f <= 0f ? "power.state.solar_night" : f >= 0.999f ? "power.state.solar_day" : "power.state.solar_dim");
                return GameText.Format("power.state.solar", Mathf.RoundToInt(f * 100f), phase);
            }
            if (def.Kind == PowerSourceKind.Fuel)
            {
                if (!_entities[info.Entity].SupplyOn)
                {
                    return b.ConstructionState == BuildingConstructionState.Operational ? GameText.Get("power.state.fuel_out") : null;
                }
                float load = _kernel.LoadOf(info.Entity);
                float lpm = Economy.ProducerCatalog.TryGet(b.BuildingTypeId, out Economy.ProducerDef pd) ? pd.FluidLpm : 0f;
                return GameText.Format("power.state.fuel_load", Mathf.RoundToInt(load * 100f), (lpm * load).ToString("0.#", CultureInfo.InvariantCulture));
            }
            return null;
        }

        /// <summary>储能站的读数行：存量 / 容量 · 状态，下一行写设置。</summary>
        private static string DescribeStorage(CampaignState state, BuildingRecord b, BuildingPowerInfo info)
        {
            double stored = _kernel.StoredOf(info.Entity) / 60.0;
            double cap = _entities[info.Entity].StorageCapacity / 60.0;
            return GameText.Format("power.state.storage", stored.ToString("0.#", CultureInfo.InvariantCulture), cap.ToString("0.#", CultureInfo.InvariantCulture),
                       StorageStateText(state, b.BuildingId))
                   + "\n" + DescribeStorageSettings(GetStorageSettings(state, b.BuildingId));
        }

        /// <summary>能源建筑第一次建成（钩子）：只在组装时数一次（O(建筑)，已在组装循环里）。</summary>
        private static int _lastEnergyCount;
    }
}
