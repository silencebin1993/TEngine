using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
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
    /// FG4-ECO-05（FG04 FGR-ECO-010～013；FG13 FGU-09；卡片“建筑改名；启用和禁用；批量修改优先级；面板跳到上下游建筑；‘?’帮助”）：
    /// 所有建筑共用的操作——名称与编号、改名、启用 / 禁用、电力优先级（单座与同类批量）、耐久（受损 / 摧毁）与维修、等级（原地升级的等级部分）、
    /// 清空缓存到仓库、仓库“只存哪些物品”、上下游建筑。数据在 fg.TbBuildingService / fg.TbBuildingTier（数值是初值，FG16 调参）。
    /// <b>唯一写入口</b>：<see cref="BuildingRecord.CustomName"/>、<see cref="BuildingRecord.StoreFilter"/>、玩家启用 / 禁用（Operational ↔ Disabled）、
    /// 受损（<see cref="ApplyDamage"/>）；摧毁仍走 <see cref="HomeValleyPowerGrid.ApplyBuildingDestroyed"/>（它回调 <see cref="OnBuildingDestroyed"/>）。
    /// 全部操作只改战役状态，不依赖镜头或表现对象（FGR-BASE-021）；没有每帧遍历——效率统计由生产推进累加（<see cref="ProductionService"/>），读数在面板打开时按需算。
    /// 不做玩家没要求的事（FGR-BASE-020）：受损不自动修、摧毁不自动重建（自动重建是 FG6-DEF-03 玩家开的规则）、批量只改玩家点名的那一类。
    /// </summary>
    public static class BuildingOps
    {
        /// <summary>任何一座建筑的名字 / 启停 / 过滤 / 耐久 / 等级变了 +1（面板与悬停据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        public static void Touch() => Revision++;

        private static bool _loaded;
        private static TbBuildingService _services;
        private static TbBuildingTier _tiers;
        private static readonly Dictionary<string, List<BuildingTier>> TiersByType = new Dictionary<string, List<BuildingTier>>(StringComparer.Ordinal);

        public static void Reload()
        {
            _loaded = false;
            _services = null;
            _tiers = null;
            TiersByType.Clear();
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                GameConfig.Tables tables = ConfigSystem.Instance.Tables;
                _services = tables?.TbBuildingService;
                _tiers = tables?.TbBuildingTier;
            }
            catch (Exception e)
            {
                Log.Error("[BuildingOps] 读取 fg.TbBuildingService / fg.TbBuildingTier 失败：" + e.Message);
            }
            if (_tiers != null)
            {
                foreach (BuildingTier t in _tiers.DataList)
                {
                    if (!TiersByType.TryGetValue(t.TypeId, out List<BuildingTier> list))
                    {
                        TiersByType[t.TypeId] = list = new List<BuildingTier>(3);
                    }
                    list.Add(t);
                }
                foreach (List<BuildingTier> list in TiersByType.Values)
                {
                    list.Sort((a, b) => a.Tier.CompareTo(b.Tier));
                }
            }
        }

        // ── 表 ─────────────────────────────────────────────────────────────────

        public static BuildingService Service(string typeId)
        {
            EnsureLoaded();
            return typeId != null && _services != null ? _services.GetOrDefault(typeId) : null;
        }

        public static float MaxDurability(string typeId) => Math.Max(1f, Service(typeId)?.MaxDurability ?? 100f);

        /// <summary>
        /// FG6-DEF-02 复审修复（FGR-DEF-010“三个等级，耐久递增”）：升级换了建筑类型（屏障 T1 → T2）时按耐久比例换算——满耐久的 T1 升完是满耐久的 T2，
        /// 掉了一半的升完仍是一半。两种类型最大耐久相同（同类型的等级升级）时原样。生命 ≤ 0（旧档“满”）原样。
        /// </summary>
        public static float HealthAfterTypeChange(float health, string fromType, string toType)
        {
            if (health <= 0f || fromType == toType)
            {
                return health;
            }
            float from = MaxDurability(fromType);
            float to = MaxDurability(toType);
            return Math.Abs(from - to) < 0.01f ? health : Math.Min(health, from) / from * to;
        }

        public static bool CanDisableType(string typeId) => typeId != HomeValleyLayout.BuildingTypeCore && (Service(typeId)?.CanDisable ?? 1) == 1;

        /// <summary>面板“?”打开的图鉴条目（没有行 = 建筑通用条目）。</summary>
        public static string CodexIdOf(string typeId) => Service(typeId)?.CodexId ?? "codex.build.building";

        public static bool HasTiers(string typeId)
        {
            EnsureLoaded();
            return typeId != null && TiersByType.TryGetValue(typeId, out List<BuildingTier> l) && l.Count > 1;
        }

        public static int MaxTier(string typeId)
        {
            EnsureLoaded();
            return typeId != null && TiersByType.TryGetValue(typeId, out List<BuildingTier> l) && l.Count > 0 ? l[l.Count - 1].Tier : 1;
        }

        /// <summary>这座建筑的等级（旧存档 / 没有等级的建筑 = 1）。</summary>
        public static int TierOf(BuildingRecord b) => b == null ? 1 : Math.Max(1, Math.Min(Math.Max(1, MaxTier(b.BuildingTypeId)), b.Tier));

        public static BuildingTier TierRow(string typeId, int tier)
        {
            EnsureLoaded();
            if (typeId == null || !TiersByType.TryGetValue(typeId, out List<BuildingTier> l))
            {
                return null;
            }
            foreach (BuildingTier t in l)
            {
                if (t.Tier == tier)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>“仓库 T2”。没有等级的建筑只写类型名。</summary>
        public static string TierName(string typeId, int tier) =>
            HasTiers(typeId) ? GameText.Format("building.tier.name", HomeGridService.DisplayName(typeId), Math.Max(1, tier)) : HomeGridService.DisplayName(typeId);

        /// <summary>这一级的效果说明（“容量 600”）；没有等级时 null。</summary>
        public static string TierEffect(string typeId, int tier)
        {
            BuildingTier row = TierRow(typeId, tier);
            return row == null ? null : GameText.Format(row.ValueKey, row.Value.ToString("0", CultureInfo.InvariantCulture));
        }

        // ── 名称与编号 ─────────────────────────────────────────────────────────────

        /// <summary>编号：建筑 ID 的实例序号（“generator_2#3” → 3）；开局预置 / 每类第一座 = 1。稳定（随存档，不随拆建别的建筑变化）。</summary>
        public static int NumberOf(BuildingRecord b)
        {
            string id = b?.BuildingId;
            if (id == null)
            {
                return 0;
            }
            int hash = id.LastIndexOf('#');
            return hash >= 0 && int.TryParse(id.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;
        }

        /// <summary>显示名：玩家起的名字，没有就是类型名。</summary>
        public static string NameOf(BuildingRecord b) =>
            b == null ? string.Empty : !string.IsNullOrEmpty(b.CustomName) ? b.CustomName : HomeGridService.DisplayName(b.BuildingTypeId);

        /// <summary>身份行：“精炼炉 · 编号 #3 · T2”。</summary>
        public static string IdentLine(BuildingRecord b) =>
            GameText.Format("bp.ident", HomeGridService.DisplayName(b.BuildingTypeId), NumberOf(b).ToString(CultureInfo.InvariantCulture),
                HasTiers(b.BuildingTypeId) ? GameText.Format("bp.ident_tier", TierOf(b)) : string.Empty);

        public static int MaxNameChars => Math.Max(4, GridContent.TuningInt("building.name.max_chars"));

        /// <summary>
        /// 改名（FGR-ECO-011）。<paramref name="name"/> 去首尾空白；空 = 恢复默认名；超过 building.name.max_chars 个字符 / 含换行或控制字符时拒绝并写明原因（FG00 B06）。
        /// 可逆操作，不弹确认（B04）。返回是否改了。
        /// </summary>
        public static bool TryRename(CampaignState state, string buildingId, string name, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null)
            {
                message = GameText.Get("grid.reason.no_building");
                return false;
            }
            string n = (name ?? string.Empty).Trim();
            foreach (char c in n)
            {
                if (char.IsControl(c))
                {
                    message = GameText.Get("bp.rename_invalid");
                    return false;
                }
            }
            if (n.Length > MaxNameChars)
            {
                message = GameText.Format("bp.rename_too_long", MaxNameChars, n.Length);
                return false;
            }
            string next = n.Length == 0 || n == HomeGridService.DisplayName(b.BuildingTypeId) ? null : n;
            if (string.Equals(next, string.IsNullOrEmpty(b.CustomName) ? null : b.CustomName, StringComparison.Ordinal))
            {
                message = GameText.Get("bp.rename_same");
                return false;
            }
            b.CustomName = next;
            // 搬迁 / 升级中的虚影跟着显示新名字（完工时以原建筑为准）。
            if (HomeGridService.FindRelocationGhost(state, b.BuildingId) is BuildingRecord ghost)
            {
                ghost.CustomName = next;
            }
            Revision++;
            message = next == null ? GameText.Format("bp.rename_reset_done", NameOf(b)) : GameText.Format("bp.rename_done", next);
            return true;
        }

        // ── 启用 / 禁用 ───────────────────────────────────────────────────────────

        /// <summary>现在能不能启用 / 禁用这座建筑（不能时给原因）。只有已建成的（运转 / 已禁用）才能切换；归还核心不能禁用。</summary>
        public static bool CanToggle(CampaignState state, BuildingRecord b, out string reason)
        {
            reason = null;
            if (b == null)
            {
                reason = GameText.Get("grid.reason.no_building");
                return false;
            }
            if (!CanDisableType(b.BuildingTypeId))
            {
                reason = GameText.Get("bp.cannot_disable_core");
                return false;
            }
            if (HomeGridService.IsRelocationGhost(b) || (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled))
            {
                reason = GameText.Format("bp.cannot_toggle_state", GameText.Get(UiStatusNameKey(BuildingStatusService.Evaluate(state, b).Kind)));
                return false;
            }
            return true;
        }

        public static bool IsDisabled(BuildingRecord b) => b != null && b.ConstructionState == BuildingConstructionState.Disabled;

        /// <summary>
        /// 启用 / 禁用一座建筑（FGR-ECO-011“启用 / 禁用开关”；对所有能禁用的建筑，不只用电建筑——DEBT-FG4ECO04-04）。
        /// 禁用 = <see cref="BuildingConstructionState.Disabled"/>：不工作、不用电、不发电、不导电、端口撤掉（物品不收不发）、仓库不提供容量（已存的不丢）；
        /// 正在做的那一份停在原处，启用后接着做。电网、信号覆盖、生产、端口按各自的“运转中”判定立即跟着变。返回是否改了。
        /// </summary>
        public static bool TrySetEnabled(CampaignState state, string buildingId, bool enabled, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (!CanToggle(state, b, out string why))
            {
                message = why;
                return false;
            }
            bool isDisabled = b.ConstructionState == BuildingConstructionState.Disabled;
            if (isDisabled == !enabled)
            {
                message = GameText.Format(enabled ? "bp.enabled_done" : "bp.disabled_done", NameOf(b));
                return false;
            }
            b.ConstructionState = enabled ? BuildingConstructionState.Operational : BuildingConstructionState.Disabled;
            AfterStateChange(state, b);
            message = GameText.Format(enabled ? "bp.enabled_done" : "bp.disabled_done", NameOf(b));
            return true;
        }

        /// <summary>建筑运行状态变了之后让各系统立即跟上（电网重算、覆盖与端口标脏、画面重画、库存容量）。O(各系统一次重算)。</summary>
        private static void AfterStateChange(CampaignState state, BuildingRecord b)
        {
            BuildingVisualFeed.Mark(b);
            HomeValleyPowerGrid.Recompute(state);
            Signal.SignalCoverageService.Invalidate();
            HomeInventory.Touch();
            Revision++;
        }

        // ── 电力优先级 ──────────────────────────────────────────────────────────────

        public static bool HasPriority(string typeId) =>
            typeId != null && HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) p) && p.PowerDemand > 0f;

        public static bool TrySetPriority(CampaignState state, string buildingId, int priority, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || !HasPriority(b.BuildingTypeId))
            {
                message = GameText.Get("bp.priority_none");
                return false;
            }
            priority = Math.Max(1, Math.Min(4, priority));
            if (b.PowerPriority == priority)
            {
                message = GameText.Format("bp.priority_done", priority);
                return false;
            }
            if (HomeValleyController.IsPlannedGhost(b))
            {
                b.PowerPriority = priority; // 虚影还没进电网：直接写记录，建成接入时按它仲裁。
            }
            else if (!HomeValleyPowerGrid.TrySetPriority(state, buildingId, priority).Success)
            {
                message = GameText.Get("bp.priority_none");
                return false;
            }
            Revision++;
            message = GameText.Format("bp.priority_done", priority);
            return true;
        }

        // ── 批量（同类建筑）────────────────────────────────────────────────────────

        public enum BatchOp : byte
        {
            Enable,
            Disable,
            Priority,
        }

        /// <summary>批量修改的规划：会改哪些、已经是这样的几座、不能改的几座（与第一条原因）。</summary>
        public sealed class BatchPlan
        {
            public BatchOp Op;
            public int Priority;
            public string TypeId;
            public readonly List<string> Change = new List<string>(8);
            public int Same;
            public int Refused;
            public string FirstRefusal;
        }

        /// <summary>同类建筑（含这一座；不含搬迁 / 升级虚影）按 <paramref name="op"/> 规划批量修改（FG04 第 4 节“可以批量修改建筑的优先级、启用或禁用”）。O(建筑数)，只在玩家点击时。</summary>
        public static BatchPlan PlanBatch(CampaignState state, string buildingId, BatchOp op, int priority)
        {
            var plan = new BatchPlan { Op = op, Priority = Math.Max(1, Math.Min(4, priority)) };
            BuildingRecord src = HomeGridService.FindBuilding(state, buildingId);
            if (src == null)
            {
                return plan;
            }
            plan.TypeId = src.BuildingTypeId;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || b.BuildingTypeId != src.BuildingTypeId || HomeGridService.IsRelocationGhost(b))
                {
                    continue;
                }
                switch (op)
                {
                    case BatchOp.Enable:
                    case BatchOp.Disable:
                        if (!CanToggle(state, b, out string why))
                        {
                            plan.Refused++;
                            plan.FirstRefusal ??= why;
                        }
                        else if (IsDisabled(b) == (op == BatchOp.Disable))
                        {
                            plan.Same++;
                        }
                        else
                        {
                            plan.Change.Add(b.BuildingId);
                        }
                        break;
                    case BatchOp.Priority:
                        if (!HasPriority(b.BuildingTypeId))
                        {
                            plan.Refused++;
                            plan.FirstRefusal ??= GameText.Get("bp.priority_none");
                        }
                        else if (b.PowerPriority == plan.Priority)
                        {
                            plan.Same++;
                        }
                        else
                        {
                            plan.Change.Add(b.BuildingId);
                        }
                        break;
                }
            }
            return plan;
        }

        public static string BatchOpName(BatchPlan plan) =>
            plan.Op == BatchOp.Enable ? GameText.Get("bp.enable") : plan.Op == BatchOp.Disable ? GameText.Get("bp.disable")
            : GameText.Format("bp.priority_done", plan.Priority);

        /// <summary>执行批量修改（玩家确认后）。电网只在最后重算一次。返回实际改了几座。</summary>
        public static int ApplyBatch(CampaignState state, BatchPlan plan, out string message)
        {
            int changed = 0;
            foreach (string id in plan.Change)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                if (b == null)
                {
                    continue;
                }
                if (plan.Op == BatchOp.Priority)
                {
                    if (HasPriority(b.BuildingTypeId) && b.PowerPriority != plan.Priority)
                    {
                        b.PowerPriority = plan.Priority;
                        changed++;
                    }
                }
                else if (CanToggle(state, b, out _) && IsDisabled(b) != (plan.Op == BatchOp.Disable))
                {
                    b.ConstructionState = plan.Op == BatchOp.Disable ? BuildingConstructionState.Disabled : BuildingConstructionState.Operational;
                    BuildingVisualFeed.Mark(b);
                    changed++;
                }
            }
            if (changed > 0)
            {
                HomeValleyPowerGrid.Recompute(state);
                Signal.SignalCoverageService.Invalidate();
                HomeInventory.Touch();
                Revision++;
            }
            string name = HomeGridService.DisplayName(plan.TypeId);
            message = plan.Refused > 0
                ? GameText.Format("bp.batch_done", name, changed, plan.Same, plan.Refused, plan.FirstRefusal ?? string.Empty)
                : GameText.Format("bp.batch_done_simple", name, changed, plan.Same);
            return changed;
        }

        // ── 耐久与维修（FGR-ECO-013）────────────────────────────────────────────────

        /// <summary>归还核心不会被摧毁：耐久被打到 0 时记成这个极小值（显示为 0、算受损、可以维修），与“旧存档里已建成却记着 0 = 满耐久”的兼容规则区分开。
        /// 核心归零的后果（失败）属于 FG6 / FG11。</summary>
        public const float CoreFloorHealth = 0.001f;

        /// <summary>当前耐久（夹在 0～上限）。已建成却记着 0 的（Demo 起修复不改耐久的旧存档）按满算——新规则下耐久归零即摧毁，运转中的建筑不会是 0
        /// （核心被打到 0 时记 <see cref="CoreFloorHealth"/>，不会落进这条兼容规则）。</summary>
        public static float Durability(BuildingRecord b)
        {
            if (b == null)
            {
                return 0f;
            }
            float max = MaxDurability(b.BuildingTypeId);
            if (b.Health <= 0f && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled))
            {
                return max;
            }
            return Mathf.Clamp(b.Health, 0f, max);
        }

        /// <summary>已建成（运转 / 禁用）且耐久没满 = 受损（照常工作）。</summary>
        public static bool IsWorn(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                      && !HomeGridService.IsRelocationGhost(b) && Durability(b) < MaxDurability(b.BuildingTypeId) - 0.01f;

        /// <summary>修满要几件维修件（按缺的比例，至少 1 件）；不能用维修件修 = 0。</summary>
        public static int RepairKitsFor(BuildingRecord b)
        {
            BuildingService s = Service(b?.BuildingTypeId);
            if (b == null || s == null || s.RepairKits <= 0)
            {
                return 0;
            }
            float max = MaxDurability(b.BuildingTypeId);
            float missing = Mathf.Clamp01((max - Durability(b)) / max);
            return missing <= 0f ? 0 : Math.Max(1, Mathf.CeilToInt(s.RepairKits * missing - 1e-4f));
        }

        public static float RepairSecondsFor(BuildingRecord b)
        {
            BuildingService s = Service(b?.BuildingTypeId);
            float max = MaxDurability(b?.BuildingTypeId);
            float missing = b == null ? 0f : Mathf.Clamp01((max - Durability(b)) / max);
            return Math.Max(Math.Max(0.1f, GridContent.Tuning("building.repair.min_seconds")), (s?.RepairSeconds ?? 10f) * missing);
        }

        /// <summary>重建（被摧毁的建筑）要多少废料与工期：有 Demo 修复造价的按它（开局预置的残骸）；没有的按新建造价（被摧毁的 FG 建筑留下虚影，重建 = 按原造价再建一次）；
        /// 两样都没有的（开局预置、不能新建的装配站 / 解析台 / 维修台）按 fg.TbBuildingService 的重建造价。三样都没有 = 不能原地重建（-1；数据校验保证不会出现）。
        /// 升过级的建筑（重建后保留等级）另加已升各级的差额：重建回来的是原来那一级（审查 P2：只收基础造价却保留等级 = 白送升级）。</summary>
        public static int RebuildCost(BuildingRecord b, out float seconds)
        {
            seconds = 0f;
            if (b == null)
            {
                return -1;
            }
            int baseCost;
            if (HomeValleyLayout.RepairProfile.TryGetValue(b.BuildingTypeId, out (int ScrapCost, float Seconds) r) && r.ScrapCost > 0)
            {
                seconds = r.Seconds;
                baseCost = r.ScrapCost;
            }
            else if (HomeValleyLayout.BuildProfile.TryGetValue(b.BuildingTypeId, out (int ScrapCost, float Seconds) n) && n.ScrapCost > 0)
            {
                seconds = n.Seconds;
                baseCost = n.ScrapCost;
            }
            else if (Service(b.BuildingTypeId) is BuildingService svc && svc.RebuildScrap > 0)
            {
                seconds = Math.Max(0.1f, svc.RebuildSeconds);
                baseCost = svc.RebuildScrap;
            }
            else
            {
                return -1;
            }
            int tier = TierOf(b);
            for (int t = 2; t <= tier; t++)
            {
                BuildingTier row = TierRow(b.BuildingTypeId, t);
                if (row != null)
                {
                    baseCost += Math.Max(0, row.DiffScrap);
                    seconds += Math.Max(0f, row.Seconds);
                }
            }
            return baseCost;
        }

        public static int RebuildScrapFor(BuildingRecord b) => RebuildCost(b, out _);

        /// <summary>
        /// 建筑受到伤害（FG6 突袭、FG7 天气、FG10 事件的接入点；本 Story 的负向测试经此入口）：耐久下降，效率不变；归零 = 摧毁（留下虚影）。
        /// 只对已建成的建筑生效（虚影 / 已摧毁的不再扣）。发“建筑受损”警告（同类聚合、可定位）。返回是否被摧毁。
        /// </summary>
        public static bool ApplyDamage(CampaignState state, string buildingId, float amount)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || amount <= 0f || HomeGridService.IsRelocationGhost(b)
                || (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled))
            {
                return false;
            }
            float max = MaxDurability(b.BuildingTypeId);
            float before = Durability(b);
            b.Health = Mathf.Max(0f, before - amount);
            b.LastHitTick = GameClock.Ticks; // FG6-DEF-03：维修无人机在突袭中先修正在挨打的目标
            Revision++;
            BuildingVisualFeed.Mark(b);
            if (b.Health <= 0f)
            {
                if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    // 核心被打到 0 的后果（失败）属于 FG6 / FG11，这里只记耐久：记极小值（显示 0、受损、可维修），不能记 0——0 会被旧存档兼容规则读成满耐久。
                    b.Health = CoreFloorHealth;
                    NotificationCenter.Post("building_damaged", GameText.Format("building.notify.damaged", NameOf(b), 0, Mathf.RoundToInt(max)),
                        new Vector3(b.Position.x, 0f, b.Position.y));
                    return false;
                }
                return HomeValleyPowerGrid.ApplyBuildingDestroyed(state, buildingId);
            }
            if (before >= max - 0.01f)
            {
                GuidanceHooks.Raise(GuidanceHooks.BuildingFirstDamaged);
            }
            NotificationCenter.Post("building_damaged", GameText.Format("building.notify.damaged", NameOf(b), Mathf.RoundToInt(b.Health), Mathf.RoundToInt(max)),
                new Vector3(b.Position.x, 0f, b.Position.y));
            return false;
        }

        /// <summary>
        /// 建筑刚被摧毁（<see cref="HomeValleyPowerGrid.ApplyBuildingDestroyed"/> 唯一调用）：在升级 / 搬迁的取消（已运到的差额材料全额退回），
        /// 受损维修单取消（维修件全额退回）；发“建筑被摧毁”通知。卡片负向“升级中被摧毁”走这里，不做逐建筑每帧检查。
        /// </summary>
        public static void OnBuildingDestroyed(CampaignState state, BuildingRecord b)
        {
            if (state == null || b == null)
            {
                return;
            }
            if (HomeGridService.FindRelocationGhost(state, b.BuildingId) is BuildingRecord ghost)
            {
                bool upgrade = HomeGridService.IsUpgradeGhost(ghost);
                WorkOrderRecord build = HomeValleyWorkOrders.FindActiveBuild(state, ghost.BuildingId);
                if (build != null)
                {
                    HomeValleyWorkOrders.CancelOrder(state, build.WorkOrderId, b.Position);
                }
                HomeGridService.Invalidate();
                if (upgrade)
                {
                    NotificationCenter.Post("building_destroyed", GameText.Format("building.upgrade.cancelled_destroyed", NameOf(b)),
                        new Vector3(b.Position.x, 0f, b.Position.y));
                }
            }
            WorkOrderRecord repair = HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId);
            if (repair != null && !string.IsNullOrEmpty(repair.ReservedItemId))
            {
                HomeValleyWorkOrders.CancelOrder(state, repair.WorkOrderId, b.Position);
            }
            Revision++;
            HomeInventory.Touch();
            NotificationCenter.Post("building_destroyed", GameText.Format("building.notify.destroyed", NameOf(b)), new Vector3(b.Position.x, 0f, b.Position.y));
            Defense.RepairDroneService.OnBuildingDestroyed(state, b); // FG6-DEF-03：维修无人机站被摧毁 = 出动中的无人机全部坠毁（当场结算）。
            StandingRuleService.OnBuildingDestroyed(state, b); // FG4-ECO-06：自动重建规则在下一个模拟步处理（没有玩家开的规则就只留虚影）。
        }

        /// <summary>升级完工（等级 / 类型已经写回）：容量、覆盖等效果立即生效；引导钩子。</summary>
        public static void OnUpgradeCompleted(CampaignState state, BuildingRecord b)
        {
            Signal.SignalCoverageService.Invalidate();
            HomeInventory.Touch();
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.BuildingFirstUpgrade);
        }

        /// <summary>
        /// 面板“维修 / 重建”（FGR-ECO-013）：受损的派维修单（维修件），被摧毁的派重建单（废料）；都由机器上门执行（待分配池）。
        /// 不能派时返回 false 并写明原因（维修件 / 废料不够写缺多少与从哪来；升级中、已有单、满耐久、不能修）。
        /// </summary>
        public static bool TryOrderRepair(CampaignState state, string buildingId, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || HomeGridService.IsRelocationGhost(b))
            {
                message = GameText.Get("bp.repair_cannot");
                return false;
            }
            if (HomeValleyWorkOrders.FindActiveRepair(state, buildingId) != null)
            {
                message = GameText.Get("bp.repair_busy");
                return false;
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                int scrap = RebuildScrapFor(b);
                if (scrap < 0)
                {
                    message = GameText.Get("bs.reason.destroyed_norepair");
                    return false;
                }
                if (Mathf.FloorToInt(state.Scrap) < scrap)
                {
                    message = GameText.Format("bp.repair_no_scrap", scrap, Mathf.FloorToInt(state.Scrap));
                    return false;
                }
                HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateRepairPool(state, buildingId, null, 0, 0f);
                message = r.Success ? GameText.Format("bp.rebuild_queued", scrap) : GameText.Format("bp.repair_no_scrap", scrap, Mathf.FloorToInt(state.Scrap));
                if (r.Success)
                {
                    Revision++;
                }
                return r.Success;
            }
            if (!IsWorn(b))
            {
                message = GameText.Get(HomeValleyController.IsPlannedGhost(b) ? "bp.repair_cannot" : "bp.repair_full");
                return false;
            }
            if (HomeGridService.FindRelocationGhost(state, buildingId) != null)
            {
                message = GameText.Get("bp.repair_upgrading");
                return false;
            }
            int kits = RepairKitsFor(b);
            if (kits <= 0)
            {
                message = GameText.Get("bp.repair_cannot");
                return false;
            }
            int have = HomeInventory.Stock(state, RepairKitId);
            if (have < kits)
            {
                message = GameText.Format("bp.repair_no_kits", kits, have);
                return false;
            }
            HomeValleyWorkOrders.WorkOrderOpResult o = HomeValleyWorkOrders.TryCreateRepairPool(state, buildingId, RepairKitId, kits, RepairSecondsFor(b));
            message = o.Success ? GameText.Format("bp.repair_queued", kits) : GameText.Format("bp.repair_no_kits", kits, have);
            if (o.Success)
            {
                Revision++;
            }
            return o.Success;
        }

        /// <summary>取消这座建筑的维修 / 重建单（预留的维修件 / 废料全额退回）。</summary>
        public static bool TryCancelRepair(CampaignState state, string buildingId, out string message)
        {
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveRepair(state, buildingId);
            if (o == null)
            {
                message = GameText.Get("bp.repair_cannot");
                return false;
            }
            int kits = o.ReservedItemAmount;
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            HomeValleyWorkOrders.CancelOrder(state, o.WorkOrderId, b?.Position ?? Vector2.zero);
            Revision++;
            message = GameText.Format("bp.repair_cancelled", kits);
            return true;
        }

        public const string RepairKitId = "repair_kit";

        // ── 清空缓存到仓库（FG-GAP-095）──────────────────────────────────────────────

        /// <summary>这座建筑有没有可以清空的缓存（生产建筑的输入 / 输出缓存、装配站的材料缓存）。</summary>
        public static int BufferedCount(CampaignState state, BuildingRecord b)
        {
            if (b == null)
            {
                return 0;
            }
            if (ProductionService.TryGet(state, b.BuildingId, out ProductionService.Producer p))
            {
                return ProductionService.Total(p.Rec.In) + ProductionService.Total(p.Rec.Out);
            }
            return AssemblyMaterials.IsStation(b) ? ProductionService.Total(AssemblyMaterials.Buffer(state)) : 0;
        }

        /// <summary>
        /// 面板“清空缓存到仓库”：输入 / 输出缓存（装配站：材料缓存）里的东西送回仓库，仓库放不下的放在建筑旁边成为地面物（机器之后搬走，不丢）。
        /// 正在做的那一份不退（做完照常产出）。返回送回的件数。
        /// </summary>
        public static int ClearBuffers(CampaignState state, string buildingId, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            int n = 0;
            if (b != null && ProductionService.TryGet(state, b.BuildingId, out ProductionService.Producer p))
            {
                n = ProductionService.ReturnAllBuffers(state, p, "bp-clear:" + b.BuildingId + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                if (n > 0 && p.Rec.Running)
                {
                    message = GameText.Format("bp.clear_done", n) + "\n" + GameText.Get("bp.clear_running");
                    Revision++;
                    return n;
                }
            }
            else if (AssemblyMaterials.IsStation(b))
            {
                n = AssemblyMaterials.ReturnBuffer(state, b, "bp-clear:" + b.BuildingId + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            if (n > 0)
            {
                Revision++;
            }
            message = n > 0 ? GameText.Format("bp.clear_done", n) : GameText.Get("bp.clear_empty");
            return n;
        }

        // ── 仓库：只存哪些物品、容量（DEBT-FG4ECO01-03 的等级 / 过滤部分）─────────────────────

        public static bool IsWarehouse(BuildingRecord b) => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse;

        /// <summary>这座仓库收不收这种物品（只看过滤：空 = 全部；tier:层级；item:物品 ID）。</summary>
        public static bool StoreAllows(BuildingRecord wh, ItemDef item)
        {
            if (item == null)
            {
                return false;
            }
            string f = wh?.StoreFilter;
            if (string.IsNullOrEmpty(f))
            {
                return true;
            }
            if (f.StartsWith("tier:", StringComparison.Ordinal))
            {
                return string.Equals(item.Tier, f.Substring(5), StringComparison.Ordinal);
            }
            if (f.StartsWith("item:", StringComparison.Ordinal))
            {
                return string.Equals(item.Id, f.Substring(5), StringComparison.Ordinal);
            }
            return true;
        }

        /// <summary>这座仓库一种物品的容量（按等级：fg.TbBuildingTier 的 value；没有等级行 = home.warehouse_capacity）。</summary>
        public static int WarehouseTierCapacity(BuildingRecord wh)
        {
            BuildingTier row = TierRow(wh?.BuildingTypeId, TierOf(wh));
            return row != null ? Mathf.RoundToInt(row.Value) : HomeValleyLayout.WarehouseCapacity;
        }

        private static BuildingRecord[] _whIndexed;
        private static readonly List<BuildingRecord> Warehouses = new List<BuildingRecord>(2);

        /// <summary>家园里的仓库（按建筑数组引用缓存；O(仓库数)）。</summary>
        public static IReadOnlyList<BuildingRecord> WarehousesOf(CampaignState state)
        {
            BuildingRecord[] records = state?.BuildingRecords;
            if (!ReferenceEquals(records, _whIndexed))
            {
                _whIndexed = records;
                Warehouses.Clear();
                foreach (BuildingRecord b in records ?? Array.Empty<BuildingRecord>())
                {
                    if (b != null && b.RegionId == HomeValleyLayout.RegionId && IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b))
                    {
                        Warehouses.Add(b);
                    }
                }
            }
            return Warehouses;
        }

        /// <summary>运转中的仓库给这种固体提供的容量合计（按每座的等级与“只存”过滤）。O(仓库数)。</summary>
        public static int WarehouseCapacityFor(CampaignState state, ItemDef item)
        {
            int sum = 0;
            IReadOnlyList<BuildingRecord> list = WarehousesOf(state);
            for (int i = 0; i < list.Count; i++)
            {
                BuildingRecord wh = list[i];
                if (wh.ConstructionState == BuildingConstructionState.Operational && StoreAllows(wh, item))
                {
                    sum += WarehouseTierCapacity(wh);
                }
            }
            return ResearchService.ScaleCapacity(state, "warehouse", sum); // FG5-RND-01：研发“货架优化”容量加成
        }

        /// <summary>“只存”的说明文字。</summary>
        public static string StoreFilterName(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return GameText.Get("bp.store_all");
            }
            if (filter.StartsWith("tier:", StringComparison.Ordinal))
            {
                return GameText.Format("bp.store_cat", GameText.Get("item.tier." + filter.Substring(5)));
            }
            if (filter.StartsWith("item:", StringComparison.Ordinal) && ItemCatalog.TryGet(filter.Substring(5), out ItemDef d))
            {
                return GameText.Format("bp.store_item", d.Name);
            }
            return GameText.Get("bp.store_all");
        }

        /// <summary>仓库“只存哪些物品”可选的过滤（全部、按层级、按每种可上带的固体）。</summary>
        public static void StoreFilterChoices(List<string> into)
        {
            into.Clear();
            into.Add(string.Empty);
            var tiers = new List<string>(6);
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (d.Form == ItemForm.Solid && !tiers.Contains(d.Tier))
                {
                    tiers.Add(d.Tier);
                }
            }
            foreach (string t in tiers)
            {
                into.Add("tier:" + t);
            }
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (HomeInventory.IsBeltStorable(d))
                {
                    into.Add("item:" + d.Id);
                }
            }
        }

        /// <summary>设置仓库只存哪些物品（已经存着的不丢；超过新容量的部分只是不再收）。返回是否改了。</summary>
        public static bool TrySetStoreFilter(CampaignState state, string buildingId, string filter, out string message)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (!IsWarehouse(b))
            {
                message = GameText.Get("grid.reason.no_building");
                return false;
            }
            string next = string.IsNullOrEmpty(filter) ? null : filter;
            if (next != null && !(next.StartsWith("tier:", StringComparison.Ordinal) || (next.StartsWith("item:", StringComparison.Ordinal) && ItemCatalog.TryGet(next.Substring(5), out _))))
            {
                message = GameText.Get("grid.reason.no_building");
                return false;
            }
            if (string.Equals(next, string.IsNullOrEmpty(b.StoreFilter) ? null : b.StoreFilter, StringComparison.Ordinal))
            {
                message = GameText.Format("bp.store_done", StoreFilterName(next));
                return false;
            }
            b.StoreFilter = next;
            HomeInventory.Touch();
            Revision++;
            message = GameText.Format("bp.store_done", StoreFilterName(next));
            return true;
        }

        // ── 上下游（FG04 第 4 节“建筑面板可以直接跳到配方的上游建筑和下游建筑”）────────────────────

        public static int MaxLinks => Math.Max(1, Math.Min(4, GridContent.TuningInt("building.jump.max_links")));

        private static readonly List<ItemDef> NeedScratch = new List<ItemDef>(4);
        private static readonly List<ItemDef> MakeScratch = new List<ItemDef>(4);
        private static readonly List<ItemDef> OtherScratch = new List<ItemDef>(4);

        /// <summary>这座建筑配方的输入（<paramref name="inputs"/> = true）或产出。回收站的输入 = 任何固体、废液池 = 任何流体，不列（会把整个家园都列进来）。</summary>
        public static bool RecipeItems(CampaignState state, BuildingRecord b, bool inputs, List<ItemDef> into)
        {
            into.Clear();
            if (b == null)
            {
                return false;
            }
            if (ProductionService.TryGet(state, b.BuildingId, out ProductionService.Producer p))
            {
                RecipeDef r = p.Recipe ?? p.Def.FixedRecipe;
                switch (p.Def.Mode)
                {
                    case ProducerMode.Recipe:
                        if (r == null)
                        {
                            return false;
                        }
                        foreach (RecipeLine l in r.Lines)
                        {
                            if ((l.Role == RecipeRole.In) == inputs && l.Item != null && l.Item.Form != ItemForm.Entity && !into.Contains(l.Item))
                            {
                                into.Add(l.Item);
                            }
                        }
                        return true;
                    case ProducerMode.Drill:
                        if (!inputs && p.VeinOre != null)
                        {
                            into.Add(p.VeinOre);
                        }
                        return true;
                    case ProducerMode.Recycler:
                        if (!inputs && ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap))
                        {
                            into.Add(scrap);
                        }
                        return true;
                    case ProducerMode.Pump:
                        if (!inputs && p.SourceFluid != null)
                        {
                            into.Add(p.SourceFluid);
                        }
                        return true;
                    case ProducerMode.Generator:
                        if (inputs)
                        {
                            foreach (ProductionService.FluidRt f in p.Fluids)
                            {
                                if (!f.Def.IsOutput && f.Fluid != null)
                                {
                                    into.Add(f.Fluid);
                                }
                            }
                        }
                        return true;
                    default:
                        return true;
                }
            }
            if (AssemblyMaterials.IsStation(b))
            {
                if (inputs)
                {
                    foreach (ItemDef m in AssemblyMaterials.MaterialItems)
                    {
                        into.Add(m);
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 上游（产出这座建筑输入的建筑）或下游（用这座建筑产出的建筑），按离这座的距离排序，最多 <see cref="MaxLinks"/> 座；<paramref name="more"/> = 没列出的座数。
        /// 返回 false = 不适用（这座建筑不按配方生产）。O(生产建筑数)，只在面板刷新时。
        /// </summary>
        public static bool CollectLinks(CampaignState state, BuildingRecord b, bool upstream, List<BuildingRecord> into, out int more)
        {
            into.Clear();
            more = 0;
            if (!RecipeItems(state, b, upstream, NeedScratch))
            {
                return false;
            }
            if (NeedScratch.Count == 0)
            {
                return true;
            }
            var found = new List<BuildingRecord>(8);
            foreach (ProductionService.Producer o in ProductionService.All)
            {
                if (ReferenceEquals(o.Building, b) || o.Building == null || HomeValleyController.IsPlannedGhost(o.Building))
                {
                    continue;
                }
                // 回收站（收任何固体）与废液池（收任何流体）不当作谁的下游：它们是“去处”，列出来会把家园全列进来。
                if (!upstream && (o.Def.Mode == ProducerMode.Recycler || o.Def.Mode == ProducerMode.Waste))
                {
                    continue;
                }
                if (!RecipeItems(state, o.Building, !upstream, OtherScratch))
                {
                    continue;
                }
                foreach (ItemDef d in OtherScratch)
                {
                    if (ContainsId(NeedScratch, d))
                    {
                        found.Add(o.Building);
                        break;
                    }
                }
            }
            if (!upstream)
            {
                BuildingRecord station = AssemblyMaterials.Station(state);
                if (station != null && !ReferenceEquals(station, b))
                {
                    foreach (ItemDef d in NeedScratch)
                    {
                        if (ContainsId(AssemblyMaterials.MaterialItems, d))
                        {
                            found.Add(station);
                            break;
                        }
                    }
                }
            }
            Vector2 at = b.Position;
            found.Sort((x, y) =>
            {
                int c = (x.Position - at).sqrMagnitude.CompareTo((y.Position - at).sqrMagnitude);
                return c != 0 ? c : string.CompareOrdinal(x.BuildingId, y.BuildingId);
            });
            int max = MaxLinks;
            for (int i = 0; i < found.Count; i++)
            {
                if (i < max)
                {
                    into.Add(found[i]);
                }
                else
                {
                    more++;
                }
            }
            return true;
        }

        /// <summary>按物品 ID 比较（物品表重载后旧的物品对象与新的不是同一个引用）。</summary>
        private static bool ContainsId(IReadOnlyList<ItemDef> list, ItemDef item)
        {
            if (list == null || item == null)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Id == item.Id)
                {
                    return true;
                }
            }
            return false;
        }

        public static string UiStatusNameKey(BuildingStatusKind k)
        {
            switch (k)
            {
                case BuildingStatusKind.Working: return "status.working.name";
                case BuildingStatusKind.Idle: return "status.idle.name";
                case BuildingStatusKind.NoMaterial: return "status.no_material.name";
                case BuildingStatusKind.NoPower: return "status.no_power.name";
                case BuildingStatusKind.NoFluid: return "status.no_fluid.name";
                case BuildingStatusKind.OutputBlocked: return "status.output_blocked.name";
                case BuildingStatusKind.Disabled: return "status.disabled.name";
                case BuildingStatusKind.Damaged: return "status.damaged.name";
                case BuildingStatusKind.Destroyed: return "status.destroyed.name";
                case BuildingStatusKind.Upgrading: return "status.upgrading.name";
                default: return "status.building.name";
            }
        }
    }
}
