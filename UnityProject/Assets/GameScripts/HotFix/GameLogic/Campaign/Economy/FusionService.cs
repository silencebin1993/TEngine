using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>FG5-RND-04：熔合操作被拒绝的原因（稳定码，测试与界面共用；文本见 fusion.reason.*）。</summary>
    public enum FusionFailure : byte
    {
        None = 0,
        NoCampaign,
        NoSynth,
        NotWorking,
        PickTwo,
        Same,
        NotFirmware,
        Mixed,
        Raw,
        NotHeld,
        Tech,
        Substrate,
        NoRecipe,
        NoChip,
        QueueFull,
        NotFound,
        NotCancellable,
    }

    /// <summary>熔合操作的结果：成功 / 失败原因码 + 给玩家看的一句话（B06）。</summary>
    public readonly struct FusionOpResult
    {
        public readonly bool Success;
        public readonly FusionFailure Failure;
        public readonly string Text;
        /// <summary>模拟有配方 / 正式熔合 = 配方 ID；模拟无反应 = null。</summary>
        public readonly string RecipeId;
        /// <summary>正式熔合入队时的任务 ID。</summary>
        public readonly string JobId;

        private FusionOpResult(bool ok, FusionFailure f, string text, string recipeId, string jobId)
        {
            Success = ok;
            Failure = f;
            Text = text ?? string.Empty;
            RecipeId = recipeId;
            JobId = jobId;
        }

        public static FusionOpResult Ok(string text, string recipeId = null, string jobId = null) => new FusionOpResult(true, FusionFailure.None, text, recipeId, jobId);
        public static FusionOpResult Fail(FusionFailure f, string text) => new FusionOpResult(false, f, text, null, null);
    }

    /// <summary>
    /// FG5-RND-04（FG05 FGR-RND-040～045；FG02 FGR-FW-050；FG13 FGU-21；FGT-RND-005～007）：熔合、配方书与线索。
    /// - 模拟熔合（FGR-RND-040）：<see cref="Simulate"/>——两条已破解、不是混合固件、固件库里有的固件，花技术数据 <see cref="FusionCatalog.SimTech"/>，立刻出结果；
    ///   无配方 = “无反应”，固件一条都不消耗（同一对的结果记进模拟记录，面板提示“模拟过”）。
    /// - 正式熔合（FGR-RND-041）：<see cref="EnqueueFormal"/>——与 Demo 电路合成台同一套原子事务：两枚父固件芯片按任务 ID 预留（<see cref="PrimitiveInventory.TryReserveForCraft"/>），
    ///   芯片基板与技术数据入队时从仓库取走记在任务上；任一步失败整体回滚。完成时二次核验芯片仍被本任务预留 → 消耗两枚、产出混合固件芯片；
    ///   <see cref="Cancel"/> / 合成台被毁或被拆 → 回滚：释放预留（芯片回到固件库）、退回芯片基板（仓库放不下落在合成台旁由机器搬回）与技术数据。不复制、不丢失。
    /// - 发现后量产（FGR-RND-042）：第一次成功 → 记入配方书、写入解锁记录（蓝图可装配、固件刻录台可刻，每枚芯片基板 <see cref="FusionCatalog.BurnSubstrate"/>，见 <see cref="IsDiscovered"/>）。
    /// - 线索（FGR-RND-044）：远征 / 突袭场次结束（<see cref="Combat.ReactionAttribution"/> 关场次时调 <see cref="OnSessionClosed"/>）后，仿真实验室按这一场各具名反应的触发次数
    ///   （= 两种状态标签同时出现的次数）给出部分 / 完整线索；没有实验室时先存着，建好后在世界步里分析（<see cref="Tick"/>）。
    /// 推进：<see cref="Tick"/> 由家园世界步调用（与观察无关，暂停不走、倍速按步）。热更层每步 O(进行中的任务数（≤ 合成台数 × 队列上限）)；没有任务、没有待分析记录时 O(1)。
    /// </summary>
    public static class FusionService
    {
        public const string TypeId = FusionCatalog.TypeId;
        public const string KindSim = "sim";
        private const int FinishedKeep = 8;
        private const int AnalyzedKeep = 64;

        public static int Revision { get; private set; } = 1;
        public static string LastFeedback { get; private set; } = string.Empty;
        /// <summary>最近一次打开的合成台（配方书 / 线索“按这条线索选固件”优先用它）。</summary>
        public static string LastSynthId { get; set; }

        private static bool _builtHookRaised;

        private static void Touch() => Revision++;

        private static void Feedback(string text)
        {
            LastFeedback = text ?? string.Empty;
            Touch();
        }

        /// <summary>刻录台接口（FG4-ECO-03 留下的 <see cref="FusionHooks"/>）：读档 / 新档 / 启动时绑定一次。</summary>
        public static void Bind()
        {
            if (FusionHooks.Service == null)
            {
                FusionHooks.Service = new Hook();
            }
        }

        public static void ResetSessionState()
        {
            LastFeedback = string.Empty;
            LastSynthId = null;
            _builtHookRaised = false;
            Bind();
            Touch();
        }

        private sealed class Hook : IFusionService
        {
            public bool IsDiscovered(CampaignState state, string mixedFirmwareId) => FusionService.IsDiscoveredMix(state, mixedFirmwareId);
            public int BurnSubstrateCost(string mixedFirmwareId) => FusionCatalog.BurnSubstrate;
        }

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static FusionState StateOf(CampaignState s) => s?.Research?.Fusion;

        /// <summary>读档 / 新档时补全熔合域（<see cref="CampaignFgStateDomains"/> 调）：数组补成空、序号至少 1、已移除的配方从配方书 / 线索 / 模拟记录里去掉（B10）。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Research == null)
            {
                return;
            }
            Bind();
            FusionState f = s.Research.Fusion ??= new FusionState();
            f.Discovered ??= Array.Empty<string>();
            f.Jobs ??= Array.Empty<FusionJobRecord>();
            f.Clues ??= Array.Empty<FusionClueRecord>();
            f.SimLog ??= Array.Empty<FusionSimRecord>();
            f.Pending ??= Array.Empty<FusionPendingRecord>();
            f.AnalyzedSessions ??= Array.Empty<string>();
            if (f.NextSerial < 1)
            {
                f.NextSerial = 1;
            }
            foreach (FusionPendingRecord p in f.Pending)
            {
                if (p != null)
                {
                    p.Reactions ??= Array.Empty<string>();
                    p.Counts ??= Array.Empty<int>();
                }
            }
            if (FusionCatalog.Recipes.Count > 0)
            {
                f.Discovered = Array.FindAll(f.Discovered, id => FusionCatalog.TryGet(id, out _));
                f.Clues = Array.FindAll(f.Clues, c => c != null && FusionCatalog.TryGet(c.RecipeId, out _));
            }
        }

        private static int NextSerial(FusionState f) => f.NextSerial++;

        // ─────────────────────────────── 合成台建筑 ───────────────────────────────

        public static bool IsSynth(BuildingRecord b) => b != null && b.BuildingTypeId == TypeId;

        /// <summary>家园里的电路合成台（不含规划虚影、搬迁虚影；按建筑 ID 排序）。</summary>
        public static List<BuildingRecord> Synths(CampaignState s)
        {
            var list = new List<BuildingRecord>();
            if (s?.BuildingRecords == null)
            {
                return list;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (IsSynth(b) && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b))
                {
                    list.Add(b);
                }
            }
            list.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
            return list;
        }

        public static BuildingRecord FindSynth(CampaignState s, string buildingId)
        {
            if (s == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            return IsSynth(b) && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b) ? b : null;
        }

        /// <summary>被毁（只剩可重建虚影 Damaged）或 Destroyed——正在做的熔合回滚、不再接活；重建完成后恢复。</summary>
        public static bool IsDestroyed(BuildingRecord b) =>
            b == null || b.ConstructionState == BuildingConstructionState.Destroyed || b.ConstructionState == BuildingConstructionState.Damaged;

        /// <summary>能接正式熔合：建成且没被毁（禁用 / 缺电时照收、排队等待）。</summary>
        public static bool CanQueue(BuildingRecord b) =>
            b != null && !IsDestroyed(b) && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled);

        /// <summary>正在工作：建成、没禁用、有电。</summary>
        public static bool IsWorking(BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;

        /// <summary>这座合成台现在能不能用（模拟要它在工作）。不能时 <paramref name="reason"/> = 建筑面板同一句原因（B06）。</summary>
        public static bool IsUsable(CampaignState s, BuildingRecord b, out string reason)
        {
            reason = string.Empty;
            if (b == null)
            {
                reason = GameText.Get("fusion.reason.no_synth");
                return false;
            }
            if (IsWorking(b))
            {
                return true;
            }
            reason = BuildingStatusService.Evaluate(s, b).Reason;
            if (string.IsNullOrEmpty(reason))
            {
                reason = GameText.Get("fusion.reason.no_synth");
            }
            return false;
        }

        /// <summary>配方书 / 线索要用的合成台：最近打开过且能用的那座，否则第一座能用的；都不能用时第一座（面板显示原因）。</summary>
        public static BuildingRecord PickSynth(CampaignState s)
        {
            BuildingRecord last = FindSynth(s, LastSynthId);
            if (last != null && IsWorking(last))
            {
                return last;
            }
            List<BuildingRecord> all = Synths(s);
            foreach (BuildingRecord b in all)
            {
                if (IsWorking(b))
                {
                    return b;
                }
            }
            return last ?? (all.Count > 0 ? all[0] : null);
        }

        /// <summary>电网结算之后（完工 / 启停 / 被毁，<c>HomeValleyPowerGrid</c> 调）：第一次有建成的合成台 → 引导钩子（图鉴条目随之解锁）。O(建筑数)，只在结算时。</summary>
        public static void OnPowerApplied(CampaignState s, bool notify)
        {
            if (!notify || _builtHookRaised || s?.BuildingRecords == null)
            {
                return;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (IsSynth(b) && b.ConstructionState == BuildingConstructionState.Operational && !HomeGridService.IsRelocationGhost(b))
                {
                    _builtHookRaised = true;
                    GuidanceHooks.Raise(GuidanceHooks.FusionFirstBuilt);
                    return;
                }
            }
        }

        // ─────────────────────────────── 固件库里能熔合的固件 ───────────────────────────────

        /// <summary>面板“固件 A / B”下拉的一行：固件 ID、固件库里有几枚、其中空闲（在固件库存放里、没被预留）几枚、能不能熔合与原因。</summary>
        public sealed class Candidate
        {
            public string FirmwareId;
            public int Held;
            public int Free;
            public bool Mixed;
            public bool Raw;
        }

        /// <summary>固件库里的全部固件（按名字排序；混合固件与未破解的也列出，选了会写明原因——FG05 负向“选了混合固件”）。O(芯片数)，只在面板刷新时。</summary>
        public static List<Candidate> Candidates(CampaignState s)
        {
            var map = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            foreach (PrimitiveChipRecord c in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c == null || string.IsNullOrEmpty(c.CardDefId) || !FirmwareKinds.IsFirmware(c.CardDefId))
                {
                    continue;
                }
                if (!map.TryGetValue(c.CardDefId, out Candidate e))
                {
                    e = new Candidate
                    {
                        FirmwareId = c.CardDefId, Mixed = FirmwareKinds.IsMixed(c.CardDefId), Raw = FirmwareKinds.IsRaw(s, c.CardDefId),
                    };
                    map[c.CardDefId] = e;
                }
                e.Held++;
                if (IsFree(c))
                {
                    e.Free++;
                }
            }
            var list = new List<Candidate>(map.Values);
            list.Sort((a, b) =>
            {
                int c = (a.Mixed || a.Raw).CompareTo(b.Mixed || b.Raw);
                return c != 0 ? c : string.CompareOrdinal(FirmwareKinds.DisplayName(a.FirmwareId) ?? a.FirmwareId, FirmwareKinds.DisplayName(b.FirmwareId) ?? b.FirmwareId);
            });
            return list;
        }

        /// <summary>空闲的芯片：在固件库存放里（不在信号核 / 电路草稿 / 待领取）、没被任何事务预留。</summary>
        public static bool IsFree(PrimitiveChipRecord c) =>
            c != null && c.State == PrimitiveChipState.Bag && string.IsNullOrEmpty(c.ReservedByTransactionId);

        private static int HeldCount(CampaignState s, string fw)
        {
            int n = 0;
            foreach (PrimitiveChipRecord c in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && c.CardDefId == fw)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>这条固件的第一枚空闲芯片（按实例 ID 排序，确定性）；没有返回 null。</summary>
        public static PrimitiveChipRecord FirstFreeChip(CampaignState s, string fw, string exceptPartId = null)
        {
            PrimitiveChipRecord best = null;
            foreach (PrimitiveChipRecord c in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && c.CardDefId == fw && IsFree(c) && c.PartId != exceptPartId
                    && (best == null || string.CompareOrdinal(c.PartId, best.PartId) < 0))
                {
                    best = c;
                }
            }
            return best;
        }

        public static string Name(string fw) => FirmwareKinds.DisplayName(fw) ?? fw ?? string.Empty;

        /// <summary>两条固件能不能拿来熔合（模拟与正式共用的判定，FG05 负向“选了混合固件”）。</summary>
        private static bool CheckPair(CampaignState s, string a, string b, out FusionOpResult fail)
        {
            fail = default;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                fail = FusionOpResult.Fail(FusionFailure.PickTwo, GameText.Get("fusion.reason.pick_two"));
                return false;
            }
            if (a == b)
            {
                fail = FusionOpResult.Fail(FusionFailure.Same, GameText.Get("fusion.reason.same"));
                return false;
            }
            foreach (string fw in new[] { a, b })
            {
                if (!FirmwareKinds.IsFirmware(fw))
                {
                    fail = FusionOpResult.Fail(FusionFailure.NotFirmware, GameText.Format("fusion.reason.not_firmware", fw));
                    return false;
                }
                if (FirmwareKinds.IsMixed(fw))
                {
                    fail = FusionOpResult.Fail(FusionFailure.Mixed, GameText.Format("fusion.reason.mixed", Name(fw)));
                    return false;
                }
                if (FirmwareKinds.IsRaw(s, fw))
                {
                    fail = FusionOpResult.Fail(FusionFailure.Raw, GameText.Format("fusion.reason.raw", Name(fw)));
                    return false;
                }
                if (HeldCount(s, fw) <= 0)
                {
                    fail = FusionOpResult.Fail(FusionFailure.NotHeld, GameText.Format("fusion.reason.not_held", Name(fw)));
                    return false;
                }
            }
            return true;
        }

        private static ItemDef TechItem => ItemCatalog.Find(ItemCatalog.TechDataId);
        private static ItemDef SubstrateItem => ItemCatalog.Find(SubstrateId);
        public const string SubstrateId = "chip_substrate";

        // ─────────────────────────────── 模拟熔合（FGR-RND-040）───────────────────────────────

        /// <summary>这一对之前模拟过的结果（没模拟过 = null）。</summary>
        public static FusionSimRecord SimResultOf(CampaignState s, string a, string b)
        {
            FusionState f = StateOf(s);
            if (f == null || string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return null;
            }
            string key = FusionCatalog.PairKey(a, b);
            foreach (FusionSimRecord r in f.SimLog)
            {
                if (r != null && r.PairKey == key)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// FGR-RND-040 模拟熔合：合成台在工作、两条固件都已破解、都不是混合固件、固件库里都有；花技术数据 <see cref="FusionCatalog.SimTech"/>。
        /// 有配方 → 返回配方（面板显示混合固件预览与正式熔合的成本），并记一条“模拟确认”的完整线索；没有 → “无反应”。**任何情况下都不消耗固件。**
        /// </summary>
        public static FusionOpResult Simulate(CampaignState s, string buildingId, string a, string b)
        {
            if (s == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoCampaign, GameText.Get("fusion.reason.no_campaign"));
            }
            EnsureState(s);
            BuildingRecord synth = FindSynth(s, buildingId) ?? PickSynth(s);
            if (synth == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoSynth, GameText.Get("fusion.reason.no_synth"));
            }
            if (!IsUsable(s, synth, out string why))
            {
                return FusionOpResult.Fail(FusionFailure.NotWorking, GameText.Format("fusion.reason.not_working", why));
            }
            if (!CheckPair(s, a, b, out FusionOpResult fail))
            {
                return fail;
            }
            int cost = FusionCatalog.SimTech;
            int have = HomeInventory.Stock(s, TechItem);
            if (have < cost)
            {
                return FusionOpResult.Fail(FusionFailure.Tech, GameText.Format("fusion.reason.tech", cost, have));
            }
            if (cost > 0)
            {
                HomeInventory.RemoveUpTo(s, TechItem, cost);
                ProductionStats.RecordUnits(s, TechItem, cost, produced: false);
            }
            FusionState f = StateOf(s);
            f.Simulations++;
            f.TechSpent += cost;
            GuidanceHooks.Raise(GuidanceHooks.FusionFirstSimulate);
            FusionCatalog.TryGetByPair(a, b, out FusionRecipeDef recipe);
            RecordSim(f, a, b, recipe?.Id);
            if (recipe == null)
            {
                f.SimulationMisses++;
                string miss = GameText.Format("fusion.feedback.sim_miss", Name(a), Name(b), cost);
                Feedback(miss);
                FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.Denied, synth.Position, miss);
                return FusionOpResult.Ok(miss);
            }
            if (!IsDiscovered(s, recipe.Id))
            {
                AddOrUpgradeClue(s, recipe, full: true, recipe.Reaction, 0, KindSim, string.Empty, 0);
            }
            string hit = GameText.Format("fusion.feedback.sim_hit", Name(a), Name(b), recipe.Name);
            Feedback(hit);
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.ProductionComplete, synth.Position, hit);
            return FusionOpResult.Ok(hit, recipe.Id);
        }

        private static void RecordSim(FusionState f, string a, string b, string recipeId)
        {
            string key = FusionCatalog.PairKey(a, b);
            var list = new List<FusionSimRecord>(f.SimLog.Length + 1);
            foreach (FusionSimRecord r in f.SimLog)
            {
                if (r != null && r.PairKey != key)
                {
                    list.Add(r);
                }
            }
            list.Add(new FusionSimRecord { Serial = NextSerial(f), PairKey = key, RecipeId = recipeId ?? string.Empty });
            int keep = FusionCatalog.SimLogKeep;
            if (list.Count > keep)
            {
                list.RemoveRange(0, list.Count - keep);
            }
            f.SimLog = list.ToArray();
        }

        /// <summary>这一对已经确认有配方：模拟出了配方，或配方已经发现过（正式熔合的前提）。</summary>
        public static bool IsPairKnown(CampaignState s, string a, string b)
        {
            if (!FusionCatalog.TryGetByPair(a, b, out FusionRecipeDef r))
            {
                return false;
            }
            if (IsDiscovered(s, r.Id))
            {
                return true;
            }
            FusionSimRecord sim = SimResultOf(s, a, b);
            return sim != null && sim.RecipeId == r.Id;
        }

        /// <summary>预览一条配方的产物（面板“有配方”那一段）：名字、负载、每发积热、读法摘要。</summary>
        public static string PreviewText(FusionRecipeDef r)
        {
            if (r == null || !FirmwareCatalog.TryGet(r.MixId, out MechanicalContentDef def))
            {
                return string.Empty;
            }
            string cats = GameText.Format("firmware.mixed.categories", GameText.Get("firmware.category." + CategoryKey(FirmwareKinds.CategoryOf(r.MixId))),
                GameText.Get("firmware.category." + CategoryKey(FirmwareKinds.Category2Of(r.MixId))));
            return GameText.Format("fusion.panel.preview", def.DisplayName, def.Load, FirmwareKinds.HeatOf(r.MixId).ToString("0.#", CultureInfo.InvariantCulture), cats)
                   + "\n" + (Content.CarrierReadings.Reading(r.MixId, FirmwareCarrier.Projectile) ?? string.Empty);
        }

        public static string CategoryKey(FirmwareCategory c) => c switch
        {
            FirmwareCategory.Fuse => "fuse",
            FirmwareCategory.Limiter => "limiter",
            FirmwareCategory.Fluid => "fluid",
            FirmwareCategory.Electromagnetic => "em",
            _ => "fuse",
        };

        /// <summary>正式熔合成本的一行（“A + B + 芯片基板 ×2 + 技术数据 30 · 30 秒”）。</summary>
        public static string CostText(FusionRecipeDef r) =>
            r == null ? string.Empty : GameText.Format("fusion.panel.cost", Name(r.ParentA), Name(r.ParentB), FusionCatalog.FormalSubstrate, FusionCatalog.FormalTech,
                Mathf.RoundToInt(FusionCatalog.FormalSeconds));

        // ─────────────────────────────── 正式熔合（FGR-RND-041）───────────────────────────────

        public static bool IsActive(FusionJobRecord j) => j != null && (j.State == FusionJobState.Queued || j.State == FusionJobState.Running);

        public static FusionJobRecord FindJob(CampaignState s, string jobId)
        {
            foreach (FusionJobRecord j in StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (j != null && j.JobId == jobId)
                {
                    return j;
                }
            }
            return null;
        }

        /// <summary>这座合成台的队列（进行中的在前、按序号；之后是最近结束的几项）。</summary>
        public static List<FusionJobRecord> JobsOf(CampaignState s, string buildingId)
        {
            var list = new List<FusionJobRecord>();
            foreach (FusionJobRecord j in StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (j != null && j.BuildingId == buildingId)
                {
                    list.Add(j);
                }
            }
            list.Sort((a, b) =>
            {
                int c = IsActive(b).CompareTo(IsActive(a));
                return c != 0 ? c : a.Serial.CompareTo(b.Serial);
            });
            return list;
        }

        public static int ActiveCount(CampaignState s, string buildingId)
        {
            int n = 0;
            foreach (FusionJobRecord j in StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (IsActive(j) && (buildingId == null || j.BuildingId == buildingId))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>按固件 ID 正式熔合：各取一枚空闲芯片（按实例 ID 最小的那枚）后走 <see cref="EnqueueFormalParts"/>。</summary>
        public static FusionOpResult EnqueueFormal(CampaignState s, string buildingId, string a, string b)
        {
            if (s == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoCampaign, GameText.Get("fusion.reason.no_campaign"));
            }
            EnsureState(s);
            if (!CheckPair(s, a, b, out FusionOpResult fail))
            {
                return fail;
            }
            PrimitiveChipRecord ca = FirstFreeChip(s, a);
            PrimitiveChipRecord cb = FirstFreeChip(s, b);
            if (ca == null || cb == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoChip, GameText.Format("fusion.reason.no_chip", Name(ca == null ? a : b)));
            }
            return EnqueueFormalParts(s, buildingId, ca.PartId, cb.PartId);
        }

        /// <summary>
        /// FGR-RND-041 正式熔合入队（原子：任一步失败 → 已做的全部撤销，返回失败，不留半成品）：
        /// 合成台能接活 → 两枚芯片是空闲的、能熔合的固件 → 这一对有配方 → 队列没满 → 仓库里芯片基板与技术数据够 →
        /// 预留两枚芯片（第二枚失败则释放第一枚）→ 取走芯片基板与技术数据（记在任务上）→ 入队。
        /// </summary>
        public static FusionOpResult EnqueueFormalParts(CampaignState s, string buildingId, string partA, string partB)
        {
            if (s == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoCampaign, GameText.Get("fusion.reason.no_campaign"));
            }
            EnsureState(s);
            FusionState f = StateOf(s);
            FusionOpResult check = CheckFormalParts(s, buildingId, partA, partB, out BuildingRecord synth, out PrimitiveChipRecord ca, out PrimitiveChipRecord cb,
                out FusionRecipeDef recipe);
            if (!check.Success)
            {
                return check;
            }
            int tech = FusionCatalog.FormalTech;
            int sub = FusionCatalog.FormalSubstrate;
            int haveTech = HomeInventory.Stock(s, TechItem);
            int serial = NextSerial(f);
            string jobId = "fusion:" + serial.ToString(CultureInfo.InvariantCulture);
            CircuitOpResult ra = PrimitiveInventory.TryReserveForCraft(s, ca.PartId, jobId);
            if (!ra.Success)
            {
                return FusionOpResult.Fail(FusionFailure.NoChip, GameText.Format("fusion.reason.no_chip", Name(ca.CardDefId)));
            }
            CircuitOpResult rb = PrimitiveInventory.TryReserveForCraft(s, cb.PartId, jobId);
            if (!rb.Success)
            {
                PrimitiveInventory.ReleaseCraftReservation(s, ca.PartId, jobId); // 回滚第一枚
                return FusionOpResult.Fail(FusionFailure.NoChip, GameText.Format("fusion.reason.no_chip", Name(cb.CardDefId)));
            }
            int tookTech = tech > 0 ? HomeInventory.RemoveUpTo(s, TechItem, tech) : 0;
            int tookSub = sub > 0 ? HomeInventory.RemoveUpTo(s, SubstrateItem, sub) : 0;
            if (tookTech != tech || tookSub != sub)
            {
                // 防御：库存在两次查询之间变了（不会发生在单线程模拟里）——整体撤销。
                RefundMaterials(s, synth.Position, tookTech, tookSub, jobId);
                PrimitiveInventory.ReleaseCraftReservation(s, ca.PartId, jobId);
                PrimitiveInventory.ReleaseCraftReservation(s, cb.PartId, jobId);
                return FusionOpResult.Fail(FusionFailure.Tech, GameText.Format("fusion.reason.tech", tech, haveTech));
            }
            var job = new FusionJobRecord
            {
                JobId = jobId, Serial = serial, BuildingId = synth.BuildingId, RecipeId = recipe.Id, PartA = ca.PartId, PartB = cb.PartId,
                Substrate = tookSub, Tech = tookTech, Progress = 0f, Duration = FusionCatalog.FormalSeconds, State = FusionJobState.Queued,
                PosX = synth.Position.x, PosY = synth.Position.y,
            };
            var jobs = new List<FusionJobRecord>(f.Jobs) { job };
            f.Jobs = jobs.ToArray();
            HomeInventory.Touch();
            string text = GameText.Format("fusion.feedback.queued", recipe.Name, Mathf.RoundToInt(job.Duration));
            Feedback(text);
            return FusionOpResult.Ok(text, recipe.Id, jobId);
        }

        /// <summary>正式熔合的全部前置条件（不改任何状态）：合成台能接活 → 两枚芯片是空闲的、能熔合的固件 → 这一对已确认有配方 → 队列没满 →
        /// 仓库里芯片基板与技术数据够。入队与面板的“正式熔合…”（弹确认框之前）共用同一套判定，原因同一句（B06）。</summary>
        private static FusionOpResult CheckFormalParts(CampaignState s, string buildingId, string partA, string partB, out BuildingRecord synth,
            out PrimitiveChipRecord ca, out PrimitiveChipRecord cb, out FusionRecipeDef recipe)
        {
            ca = null;
            cb = null;
            recipe = null;
            synth = FindSynth(s, buildingId);
            if (synth == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoSynth, GameText.Get("fusion.reason.no_synth"));
            }
            if (!CanQueue(synth))
            {
                return FusionOpResult.Fail(FusionFailure.NotWorking, GameText.Format("fusion.reason.not_working", BuildingStatusService.Evaluate(s, synth).Reason));
            }
            ca = PrimitiveInventory.Find(s, partA);
            cb = PrimitiveInventory.Find(s, partB);
            if (ca == null || cb == null || partA == partB)
            {
                return FusionOpResult.Fail(FusionFailure.PickTwo, GameText.Get("fusion.reason.pick_two"));
            }
            if (!CheckPair(s, ca.CardDefId, cb.CardDefId, out FusionOpResult fail))
            {
                return fail;
            }
            if (!IsFree(ca) || !IsFree(cb))
            {
                return FusionOpResult.Fail(FusionFailure.NoChip, GameText.Format("fusion.reason.no_chip", Name(!IsFree(ca) ? ca.CardDefId : cb.CardDefId)));
            }
            // 先模拟（FGR-RND-040“先做模拟熔合”）：这一对要已经模拟出配方或已经发现过；否则不告诉玩家有没有配方（不能绕过模拟免费试探）。
            if (!IsPairKnown(s, ca.CardDefId, cb.CardDefId) || !FusionCatalog.TryGetByPair(ca.CardDefId, cb.CardDefId, out recipe))
            {
                return FusionOpResult.Fail(FusionFailure.NoRecipe, GameText.Format("fusion.reason.simulate_first", Name(ca.CardDefId), Name(cb.CardDefId), FusionCatalog.SimTech));
            }
            if (ActiveCount(s, synth.BuildingId) >= FusionCatalog.QueueMax)
            {
                return FusionOpResult.Fail(FusionFailure.QueueFull, GameText.Format("fusion.reason.queue_full", FusionCatalog.QueueMax));
            }
            int haveTech = HomeInventory.Stock(s, TechItem);
            if (haveTech < FusionCatalog.FormalTech)
            {
                return FusionOpResult.Fail(FusionFailure.Tech, GameText.Format("fusion.reason.tech", FusionCatalog.FormalTech, haveTech));
            }
            int haveSub = HomeInventory.Stock(s, SubstrateItem);
            if (haveSub < FusionCatalog.FormalSubstrate)
            {
                return FusionOpResult.Fail(FusionFailure.Substrate, GameText.Format("fusion.reason.substrate", FusionCatalog.FormalSubstrate, haveSub));
            }
            return FusionOpResult.Ok(string.Empty, recipe.Id);
        }

        /// <summary>面板“正式熔合…”在弹确认框之前问一次：现在能不能熔合（按固件 ID，取第一枚空闲芯片）。不改任何状态。</summary>
        public static FusionOpResult PreflightFormal(CampaignState s, string buildingId, string a, string b)
        {
            if (s == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoCampaign, GameText.Get("fusion.reason.no_campaign"));
            }
            if (!CheckPair(s, a, b, out FusionOpResult fail))
            {
                return fail;
            }
            PrimitiveChipRecord ca = FirstFreeChip(s, a);
            PrimitiveChipRecord cb = FirstFreeChip(s, b);
            if (ca == null || cb == null)
            {
                return FusionOpResult.Fail(FusionFailure.NoChip, GameText.Format("fusion.reason.no_chip", Name(ca == null ? a : b)));
            }
            return CheckFormalParts(s, buildingId, ca.PartId, cb.PartId, out _, out _, out _, out _);
        }

        /// <summary>取消排队中 / 熔合中的一项：两枚芯片回到固件库，芯片基板与技术数据全部退回（B03）。</summary>
        public static FusionOpResult Cancel(CampaignState s, string jobId)
        {
            FusionJobRecord j = FindJob(s, jobId);
            if (j == null)
            {
                return FusionOpResult.Fail(FusionFailure.NotFound, GameText.Get("fusion.reason.not_found"));
            }
            if (!IsActive(j))
            {
                return FusionOpResult.Fail(FusionFailure.NotCancellable, GameText.Get("fusion.reason.not_cancellable"));
            }
            Release(s, j, FindSynth(s, j.BuildingId));
            j.State = FusionJobState.Cancelled;
            j.Reason = string.Empty;
            TrimFinished(StateOf(s));
            FusionCatalog.TryGet(j.RecipeId, out FusionRecipeDef r);
            string text = GameText.Format("fusion.feedback.cancelled", r?.Name ?? j.RecipeId);
            Feedback(text);
            return FusionOpResult.Ok(text, j.RecipeId, j.JobId);
        }

        /// <summary>释放一项的全部占用：两枚芯片的预留、记在任务上的芯片基板（仓库放不下落在合成台旁）与技术数据。之后任务上的材料清零（不会被退两次）。</summary>
        private static void Release(CampaignState s, FusionJobRecord j, BuildingRecord synth)
        {
            PrimitiveInventory.ReleaseCraftReservation(s, j.PartA, j.JobId);
            PrimitiveInventory.ReleaseCraftReservation(s, j.PartB, j.JobId);
            RefundMaterials(s, synth?.Position ?? new Vector2(j.PosX, j.PosY), j.Tech, j.Substrate, j.JobId);
            j.Tech = 0;
            j.Substrate = 0;
        }

        private static void RefundMaterials(CampaignState s, Vector2 at, int tech, int substrate, string key)
        {
            if (tech > 0)
            {
                HomeInventory.Add(s, TechItem, tech, clampToSpace: false); // 数字资源不限容量
            }
            if (substrate > 0)
            {
                int stored = HomeInventory.Add(s, SubstrateItem, substrate);
                if (stored < substrate)
                {
                    HomeValleyConstruction.ReturnMaterials(s, at, SubstrateItem.ResourceType, substrate - stored, "fusion-refund:" + key);
                }
            }
            HomeInventory.Touch();
            ItemDistribution.Invalidate();
        }

        private static void TrimFinished(FusionState f)
        {
            if (f == null)
            {
                return;
            }
            int finished = 0;
            foreach (FusionJobRecord j in f.Jobs)
            {
                if (j != null && !IsActive(j))
                {
                    finished++;
                }
            }
            if (finished <= FinishedKeep * 2)
            {
                return;
            }
            // 只留最近 FinishedKeep 项结束的（按序号），进行中的全部保留。
            var ended = new List<FusionJobRecord>();
            foreach (FusionJobRecord j in f.Jobs)
            {
                if (j != null && !IsActive(j))
                {
                    ended.Add(j);
                }
            }
            ended.Sort((a, b) => b.Serial.CompareTo(a.Serial));
            var keep = new HashSet<FusionJobRecord>(ended.GetRange(0, Math.Min(FinishedKeep, ended.Count)));
            f.Jobs = Array.FindAll(f.Jobs, j => j != null && (IsActive(j) || keep.Contains(j)));
        }

        // ─────────────────────────────── 每个世界步 ───────────────────────────────

        /// <summary>世界步（<see cref="HomeValleyController.SimStep"/>）：待分析的战斗记录（有实验室时）、每座合成台的队首推进 / 被毁回滚。</summary>
        public static void Tick(CampaignState s, float dt)
        {
            FusionState f = StateOf(s);
            if (f == null)
            {
                return;
            }
            if (f.Pending.Length > 0 && LabAvailable(s))
            {
                AnalyzePending(s);
            }
            if (f.Jobs.Length == 0)
            {
                return;
            }
            bool any = false;
            foreach (FusionJobRecord j in f.Jobs)
            {
                if (IsActive(j))
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return;
            }
            // 每座合成台：被毁 / 被拆 → 全部回滚；否则队首（序号最小的进行中项）在工作时推进。O(进行中的任务数)。
            // 遍历开头的快照（回滚 / 完成时会整理已结束的项，不影响本步的遍历顺序）。
            FusionJobRecord[] jobs = f.Jobs;
            for (int i = 0; i < jobs.Length; i++)
            {
                FusionJobRecord j = jobs[i];
                if (!IsActive(j))
                {
                    continue;
                }
                BuildingRecord synth = HomeGridService.FindBuilding(s, j.BuildingId);
                bool removed = synth == null || !IsSynth(synth);
                if (removed || IsDestroyed(synth))
                {
                    RollBack(s, j, removed ? null : synth, removed ? "removed" : "destroyed");
                    continue;
                }
                if (!IsHead(f, j))
                {
                    continue;
                }
                if (!IsWorking(synth))
                {
                    j.Reason = synth.ConstructionState == BuildingConstructionState.Disabled ? "disabled" : "no_power";
                    continue;
                }
                j.State = FusionJobState.Running;
                j.Reason = string.Empty;
                j.Progress += dt;
                if (j.Progress >= j.Duration)
                {
                    Complete(s, j, synth);
                }
            }
        }

        private static bool IsHead(FusionState f, FusionJobRecord job)
        {
            foreach (FusionJobRecord o in f.Jobs)
            {
                if (o != job && IsActive(o) && o.BuildingId == job.BuildingId && o.Serial < job.Serial)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>FG05 负向“正式熔合途中被突袭打断（合成台被毁）”：事务回滚，父固件退回、材料退回，不复制、不丢失。</summary>
        private static void RollBack(CampaignState s, FusionJobRecord j, BuildingRecord synth, string reason)
        {
            Release(s, j, synth);
            j.State = FusionJobState.RolledBack;
            j.Reason = reason;
            FusionState f = StateOf(s);
            f.RolledBack++;
            FusionCatalog.TryGet(j.RecipeId, out FusionRecipeDef r);
            string text = GameText.Format("fusion.feedback.rolled_back", r?.Name ?? j.RecipeId, GameText.Get("fusion.rollback." + reason));
            Feedback(text);
            Vector2? at = synth?.Position ?? new Vector2(j.PosX, j.PosY);
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.Failure, at, text);
            NotificationCenter.Post("fusion_rolled_back", r?.Name ?? j.RecipeId, at.HasValue ? new Vector3(at.Value.x, 0f, at.Value.y) : (Vector3?)null);
            TrimFinished(f);
        }

        /// <summary>到时提交：二次核验两枚芯片仍被本任务预留（中途被别的入口拿走 → 回滚，不凭空产出）→ 消耗两枚 → 产出混合固件芯片 → 第一次成功记入配方书。</summary>
        private static void Complete(CampaignState s, FusionJobRecord j, BuildingRecord synth)
        {
            j.Progress = j.Duration;
            PrimitiveChipRecord ca = PrimitiveInventory.Find(s, j.PartA);
            PrimitiveChipRecord cb = PrimitiveInventory.Find(s, j.PartB);
            bool intact = ca != null && cb != null && ca.State == PrimitiveChipState.Bag && cb.State == PrimitiveChipState.Bag
                          && ca.ReservedByTransactionId == j.JobId && cb.ReservedByTransactionId == j.JobId;
            if (!intact || !FusionCatalog.TryGet(j.RecipeId, out FusionRecipeDef recipe))
            {
                RollBack(s, j, synth, "chip_missing");
                return;
            }
            PrimitiveInventory.ConsumeReservedMaterial(s, j.PartA, j.JobId);
            PrimitiveInventory.ConsumeReservedMaterial(s, j.PartB, j.JobId);
            ProductionStats.RecordUnits(s, TechItem, j.Tech, produced: false);
            ProductionStats.RecordUnits(s, SubstrateItem, j.Substrate, produced: false);
            FusionState f = StateOf(s);
            f.TechSpent += j.Tech;
            j.Tech = 0;
            j.Substrate = 0;
            bool first = !IsDiscovered(s, recipe.Id);
            if (first)
            {
                MarkDiscovered(s, recipe); // 先写解锁记录，再发芯片（芯片进固件库时按已解锁处理）
            }
            j.OutputPartId = PrimitiveInventory.GrantCrafted(s, recipe.MixId) ?? string.Empty;
            j.State = FusionJobState.Done;
            j.Reason = string.Empty;
            f.Fused++;
            string text = first
                ? GameText.Format("fusion.feedback.discovered", recipe.Name, Name(recipe.ParentA), Name(recipe.ParentB))
                : GameText.Format("fusion.feedback.done", recipe.Name);
            Feedback(text);
            Vector2 at = synth.Position;
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.ProductionComplete, at, text);
            NotificationCenter.Post(first ? "fusion_discovered" : "fusion_done",
                first ? GameText.Format("fusion.notify.discovered", recipe.Name, Name(recipe.ParentA), Name(recipe.ParentB)) : recipe.Name,
                new Vector3(at.x, 0f, at.y));
            TrimFinished(f);
        }

        // ─────────────────────────────── 配方书（FGR-RND-042 / 045）───────────────────────────────

        public static bool IsDiscovered(CampaignState s, string recipeId) =>
            StateOf(s)?.Discovered != null && Array.IndexOf(StateOf(s).Discovered, recipeId) >= 0;

        /// <summary>这条混合固件的配方已发现（固件刻录台可以量产）。</summary>
        public static bool IsDiscoveredMix(CampaignState s, string mixId) =>
            FusionCatalog.TryGetByMix(mixId, out FusionRecipeDef r) && IsDiscovered(s, r.Id);

        /// <summary>第一次成功：记入配方书、写入解锁记录（蓝图可装配、固件刻录台可刻）、引导钩子与图鉴。</summary>
        private static void MarkDiscovered(CampaignState s, FusionRecipeDef r)
        {
            FusionState f = StateOf(s);
            var list = new List<string>(f.Discovered) { r.Id };
            f.Discovered = list.ToArray();
            s.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(s.UnlockedContentIds, r.MixId) < 0)
            {
                var u = new List<string>(s.UnlockedContentIds) { r.MixId };
                s.UnlockedContentIds = u.ToArray();
            }
            GuidanceHooks.Raise(GuidanceHooks.FusionFirstDiscovery);
            Touch();
        }

        /// <summary>已发现的配方（按配方书排序，<paramref name="family"/> 为空 = 全部类别）。</summary>
        public static List<FusionRecipeDef> DiscoveredRecipes(CampaignState s, string family)
        {
            var list = new List<FusionRecipeDef>();
            foreach (FusionRecipeDef r in FusionCatalog.Recipes)
            {
                if ((string.IsNullOrEmpty(family) || r.Family == family) && IsDiscovered(s, r.Id))
                {
                    list.Add(r);
                }
            }
            return list;
        }

        /// <summary>FGR-RND-045“每个类别还剩多少个未发现”：= 配方表里这一类的条数 − 已发现的（随表变化，不写死数字）。</summary>
        public static int Remaining(CampaignState s, string family) =>
            Math.Max(0, FusionCatalog.CountInFamily(family) - DiscoveredRecipes(s, family).Count);

        public static string RemainingText(CampaignState s, string family)
        {
            int left = Remaining(s, family);
            string name = FusionCatalog.FamilyName(family);
            return left > 0 ? GameText.Format("fusion.panel.remaining", name, left) : GameText.Format("fusion.panel.remaining_done", name);
        }

        public static string RecipeLine(FusionRecipeDef r) =>
            r == null ? string.Empty : GameText.Format("fusion.panel.book_row", r.Name, Name(r.ParentA), Name(r.ParentB));

        // ─────────────────────────────── 线索（FGR-RND-044）───────────────────────────────

        /// <summary>有没有能分析战斗记录的仿真实验室（建成、没被毁；分析是一次性的，不要求此刻有电）。</summary>
        public static bool LabAvailable(CampaignState s)
        {
            foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == ResearchCatalog.LabTypeId && b.RegionId == HomeValleyLayout.RegionId
                    && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                    && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 一场远征 / 突袭结束（<see cref="Combat.ReactionAttribution"/> 关场次时调；FGR-RND-044“每次远征或突袭结束后”）：把这一场各反应的触发次数做成快照；
        /// 有仿真实验室就立刻分析，没有就先存着（最多 <see cref="FusionCatalog.PendingKeep"/> 场）。同一场只分析一次。线索在结束时一次性生成，不在战斗中实时统计（FG05 第 7 节）。
        /// </summary>
        public static void OnSessionClosed(CampaignState s, ReactionSessionRecord r)
        {
            if (s == null || r == null || string.IsNullOrEmpty(r.SessionId))
            {
                return;
            }
            EnsureState(s);
            FusionState f = StateOf(s);
            if (Array.IndexOf(f.AnalyzedSessions, r.SessionId) >= 0)
            {
                return;
            }
            foreach (FusionPendingRecord p in f.Pending)
            {
                if (p != null && p.SessionId == r.SessionId)
                {
                    return;
                }
            }
            var ids = new List<string>();
            var counts = new List<int>();
            foreach (ReactionShareRecord x in r.Reactions ?? Array.Empty<ReactionShareRecord>())
            {
                if (x != null && !string.IsNullOrEmpty(x.ReactionId) && x.Count > 0)
                {
                    ids.Add(x.ReactionId);
                    counts.Add(x.Count);
                }
            }
            var snap = new FusionPendingRecord
            {
                SessionId = r.SessionId, Kind = r.Kind ?? string.Empty, SiteId = r.SiteId ?? string.Empty, Ordinal = r.Ordinal,
                EndTick = r.EndTick, Reactions = ids.ToArray(), Counts = counts.ToArray(),
            };
            if (LabAvailable(s))
            {
                Analyze(s, snap);
                return;
            }
            var list = new List<FusionPendingRecord>(f.Pending) { snap };
            int keep = FusionCatalog.PendingKeep;
            if (list.Count > keep)
            {
                list.RemoveRange(0, list.Count - keep);
            }
            f.Pending = list.ToArray();
            Touch();
        }

        private static void AnalyzePending(CampaignState s)
        {
            FusionState f = StateOf(s);
            FusionPendingRecord[] pending = f.Pending;
            f.Pending = Array.Empty<FusionPendingRecord>();
            foreach (FusionPendingRecord p in pending)
            {
                if (p != null)
                {
                    Analyze(s, p);
                }
            }
            Touch();
        }

        /// <summary>分析一场：触发次数 ≥ <see cref="FusionCatalog.CluePartialMin"/> 的反应按次数从多到少，每条反应找一条还没发现、线索还能升级的配方
        /// （按配方书顺序）：次数 ≥ <see cref="FusionCatalog.ClueFullMin"/> → 完整线索，否则部分线索；一场最多 <see cref="FusionCatalog.CluePerSession"/> 条。</summary>
        private static void Analyze(CampaignState s, FusionPendingRecord p)
        {
            FusionState f = StateOf(s);
            var list = new List<string>(f.AnalyzedSessions) { p.SessionId };
            if (list.Count > AnalyzedKeep)
            {
                list.RemoveRange(0, list.Count - AnalyzedKeep);
            }
            f.AnalyzedSessions = list.ToArray();
            var order = new List<int>();
            for (int i = 0; i < p.Reactions.Length && i < p.Counts.Length; i++)
            {
                if (p.Counts[i] >= FusionCatalog.CluePartialMin)
                {
                    order.Add(i);
                }
            }
            order.Sort((a, b) =>
            {
                int c = p.Counts[b].CompareTo(p.Counts[a]);
                return c != 0 ? c : string.CompareOrdinal(p.Reactions[a], p.Reactions[b]);
            });
            int made = 0;
            foreach (int i in order)
            {
                if (made >= FusionCatalog.CluePerSession)
                {
                    break;
                }
                bool full = p.Counts[i] >= FusionCatalog.ClueFullMin;
                FusionRecipeDef target = null;
                foreach (FusionRecipeDef r in FusionCatalog.Recipes)
                {
                    if (r.Reaction != p.Reactions[i] || IsDiscovered(s, r.Id))
                    {
                        continue;
                    }
                    FusionClueRecord have = ClueOf(f, r.Id);
                    if (have == null || (full && !have.Full))
                    {
                        target = r;
                        break;
                    }
                }
                if (target != null && AddOrUpgradeClue(s, target, full, p.Reactions[i], p.Counts[i], p.Kind, p.SiteId, p.Ordinal))
                {
                    made++;
                }
            }
        }

        public static FusionClueRecord ClueOf(FusionState f, string recipeId)
        {
            foreach (FusionClueRecord c in f?.Clues ?? Array.Empty<FusionClueRecord>())
            {
                if (c != null && c.RecipeId == recipeId)
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>给一条配方加线索（已有部分线索时升级成完整线索；已有完整线索 / 已发现时不动）。返回 true = 有新内容（标“新”、发通知）。</summary>
        private static bool AddOrUpgradeClue(CampaignState s, FusionRecipeDef r, bool full, string reactionId, int count, string kind, string siteId, int ordinal)
        {
            FusionState f = StateOf(s);
            FusionClueRecord have = ClueOf(f, r.Id);
            if (IsDiscovered(s, r.Id) || (have != null && (have.Full || !full)))
            {
                return false;
            }
            var clue = new FusionClueRecord
            {
                Serial = NextSerial(f), RecipeId = r.Id, Full = full, KnownParent = full ? string.Empty : r.ParentA,
                ReactionId = reactionId ?? string.Empty, Count = count, SessionKind = kind ?? string.Empty, SiteId = siteId ?? string.Empty,
                Ordinal = ordinal, Tick = GameClock.Ticks,
            };
            var list = new List<FusionClueRecord>(f.Clues.Length + 1);
            foreach (FusionClueRecord c in f.Clues)
            {
                if (c != null && c.RecipeId != r.Id)
                {
                    list.Add(c);
                }
            }
            list.Add(clue);
            int keep = FusionCatalog.ClueKeep;
            while (list.Count > keep)
            {
                // 先去掉最旧的、配方已经发现的线索；没有就去掉最旧的。
                int drop = list.FindIndex(c => IsDiscovered(s, c.RecipeId));
                list.RemoveAt(drop >= 0 ? drop : 0);
            }
            f.Clues = list.ToArray();
            if (kind != KindSim)
            {
                if (!f.FirstClueSeen)
                {
                    f.FirstClueSeen = true;
                    GuidanceHooks.Raise(GuidanceHooks.FusionFirstClue);
                }
                NotificationCenter.Post("fusion_clue", ClueText(s, clue));
            }
            Touch();
            return true;
        }

        /// <summary>线索列表（<paramref name="newestFirst"/> 按序号倒序；<paramref name="family"/> 为空 = 全部类别）。</summary>
        public static List<FusionClueRecord> Clues(CampaignState s, string family, bool newestFirst)
        {
            var list = new List<FusionClueRecord>();
            foreach (FusionClueRecord c in StateOf(s)?.Clues ?? Array.Empty<FusionClueRecord>())
            {
                if (c != null && FusionCatalog.TryGet(c.RecipeId, out FusionRecipeDef r) && (string.IsNullOrEmpty(family) || r.Family == family))
                {
                    list.Add(c);
                }
            }
            list.Sort((a, b) => newestFirst ? b.Serial.CompareTo(a.Serial) : a.Serial.CompareTo(b.Serial));
            return list;
        }

        public static bool IsNewClue(CampaignState s, FusionClueRecord c) => c != null && c.Serial > (StateOf(s)?.SeenClueSerial ?? 0);

        /// <summary>配方书打开时：线索都记为看过（这次打开期间界面仍标“新”，由界面在打开前取一次）。</summary>
        public static void MarkCluesSeen(CampaignState s)
        {
            FusionState f = StateOf(s);
            if (f == null)
            {
                return;
            }
            int max = f.SeenClueSerial;
            foreach (FusionClueRecord c in f.Clues)
            {
                if (c != null && c.Serial > max)
                {
                    max = c.Serial;
                }
            }
            if (max != f.SeenClueSerial)
            {
                f.SeenClueSerial = max;
                Touch();
            }
        }

        /// <summary>线索的一句话（当前语言现拼）：“燃烧与油污同时出现 12 次（远征 · 破碎都市 第 2 次），可能与「燃迹 + 漏油」有关”。</summary>
        public static string ClueText(CampaignState s, FusionClueRecord c)
        {
            if (c == null || !FusionCatalog.TryGet(c.RecipeId, out FusionRecipeDef r))
            {
                return string.Empty;
            }
            string text;
            if (c.SessionKind == KindSim)
            {
                text = GameText.Format("fusion.clue.from_sim", Name(r.ParentA), Name(r.ParentB));
            }
            else
            {
                string tagA = string.Empty;
                string tagB = string.Empty;
                if (NamedReactionCatalog.TryGet(c.ReactionId, out GameConfig.fg.Reaction row))
                {
                    tagA = StatusTagCatalog.NameOf(row.TagA) ?? row.TagA;
                    tagB = StatusTagCatalog.NameOf(row.TagB) ?? row.TagB;
                }
                string site = Combat.CombatSites.SiteName(c.SiteId);
                string where = c.SessionKind == Combat.ReactionAttribution.KindRaid
                    ? GameText.Format("reaction.attr.title_raid", site)
                    : GameText.Format("reaction.attr.title_expedition", site, c.Ordinal);
                text = c.Full
                    ? GameText.Format("fusion.clue.full", tagA, tagB, c.Count, where, Name(r.ParentA), Name(r.ParentB))
                    : GameText.Format("fusion.clue.partial", tagA, tagB, c.Count, where, Name(c.KnownParent));
            }
            if (IsDiscovered(s, r.Id))
            {
                text += GameText.Format("fusion.clue.discovered", r.Name);
            }
            return text;
        }

        /// <summary>物资分布（悬停物品图标）：正式熔合在办项上的芯片基板与技术数据（O(在办任务数)，只在分布重建时）。</summary>
        public static void CollectDistribution(CampaignState s, Dictionary<string, long> into)
        {
            foreach (FusionJobRecord j in StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (!IsActive(j))
                {
                    continue;
                }
                if (j.Substrate > 0)
                {
                    into.TryGetValue(SubstrateId, out long a);
                    into[SubstrateId] = a + j.Substrate;
                }
                if (j.Tech > 0)
                {
                    into.TryGetValue(ItemCatalog.TechDataId, out long t);
                    into[ItemCatalog.TechDataId] = t + j.Tech;
                }
            }
        }

        // ─────────────────────────────── 建筑状态（B05）───────────────────────────────

        /// <summary>建筑面板 / 悬停 / “为什么不工作”：熔合中（名字、进度）/ 缺电或禁用暂停 / 空闲（怎么开始）。</summary>
        public static BuildingStatus StatusOf(CampaignState s, BuildingRecord b)
        {
            FusionJobRecord head = null;
            foreach (FusionJobRecord j in StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (IsActive(j) && j.BuildingId == b.BuildingId && (head == null || j.Serial < head.Serial))
                {
                    head = j;
                }
            }
            if (head == null)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "fusion.idle", GameText.Get("bs.reason.fusion_idle"));
            }
            FusionCatalog.TryGet(head.RecipeId, out FusionRecipeDef r);
            return new BuildingStatus(BuildingStatusKind.Working, "fusion.running", GameText.Format("bs.reason.fusion_running", r?.Name ?? head.RecipeId,
                Mathf.FloorToInt(head.Progress), Mathf.RoundToInt(head.Duration)));
        }

        public static string StateText(FusionJobRecord j)
        {
            if (j == null)
            {
                return string.Empty;
            }
            switch (j.State)
            {
                case FusionJobState.Queued:
                    return GameText.Get(j.Reason == "disabled" ? "fusion.state.disabled" : j.Reason == "no_power" ? "fusion.state.no_power" : "fusion.state.queued");
                case FusionJobState.Running:
                    return GameText.Get(j.Reason == "disabled" ? "fusion.state.disabled" : j.Reason == "no_power" ? "fusion.state.no_power" : "fusion.state.running");
                case FusionJobState.Done: return GameText.Get("fusion.state.done");
                case FusionJobState.Cancelled: return GameText.Get("fusion.state.cancelled");
                default: return GameText.Get("fusion.state.rolled_back");
            }
        }
    }
}
