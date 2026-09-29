using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// FG1-SIG-06 裸跑敌方固件（FG01 FGR-SIG-060～062）。
    ///
    /// - **是什么**：协议为“敌方加密”（fg.TbFirmwareKind protocol=enemy）且解析台还没破解（内容未解锁）的固件是“未破解”（<see cref="FirmwareKinds.IsRaw(CampaignState,string)"/>）。
    ///   它只能放进信号核（接入时插进接入口照常生效），不能装进机器电路或炮塔（<see cref="FirmwareKinds.CanInstall"/>）。
    /// - **从哪来**：带回的敌方加密物品（破碎都市终端的标记跳转协议数据盒、铸造外围的装甲击穿技术缓存）撤离成功时，基元仓多一枚对应的未破解固件芯片
    ///   （<see cref="GrantRecovered"/>，按回收实例幂等；读档时补发旧档里已带回的）。数据盒本身照旧送解析台破解。
    /// - **代价（确定性，FGR-SIG-061）**：每次“发动”暴露 +2；经过接入口那条武器的积热 ×1.5（编译结果 <see cref="BlueprintCircuitPreview.RawHeatMultiplier"/>）。
    ///   “发动”：核心固件 = 具名反应真正打出的那一发（与核心冷却同一时刻，<see cref="SignalUplinkService.OnReactionFired(CampaignState,int,string,bool)"/>）；
    ///   常规固件没有“发动”键，按武器开火计——开火那一发计一次，之后 signal.raw.charge_interval_seconds 游戏秒内的开火不再计（记在信号上，换机器不重置，进存档）。
    ///   内核按 <c>CombatUnitFlags.RawGated / RawSpent</c> 当场压住（与核心反应门控同一套），发不丢的玩法事件 <c>RawFirmwareFired</c>，本类结算暴露。
    /// - **破解后（FGR-SIG-062）**：解析台完成 → 内容解锁 → 同一种固件所有实例去掉标记、可以刻印、装进机器和炮塔；已在信号核里的那件自动更新
    ///   （接入中的机器立刻重编译：积热回到 1 倍、不再计裸跑暴露），核心冷却不重置（冷却按固件种类记在信号上，<see cref="OnCracked"/>）。
    /// 开销：与机器数无关——计次只在被接入的那一台上发生；到期检查 O(1)（<see cref="SimStep"/>）。
    /// </summary>
    public static class RawFirmwareService
    {
        // ── 调参（fg.TbHomeTuning signal.raw.*）──────────────────────────────────────

        public static float ExposurePerFire => Math.Max(0f, Tuning("signal.raw.exposure_per_fire", 2f));
        public static float HeatMultiplier => Math.Max(1f, Tuning("signal.raw.heat_multiplier", 1.5f));
        public static float ChargeIntervalSeconds => Math.Max(0f, Tuning("signal.raw.charge_interval_seconds", 8f));

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[RawFirmwareService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        // ── 自检读点 ────────────────────────────────────────────────────────────

        /// <summary>常规裸跑固件“发动”被结算的次数（一次开火可能带几枚未破解固件，每枚各计一笔暴露）。</summary>
        public static int RawFiredCount { get; private set; }
        public static int GrantedCount { get; private set; }
        public static int CrackedCount { get; private set; }
        public static int RearmCount { get; private set; }

        public static void ResetForTests()
        {
            RawFiredCount = 0;
            GrantedCount = 0;
            CrackedCount = 0;
            RearmCount = 0;
        }

        // ── 计次（常规裸跑固件）────────────────────────────────────────────────────

        /// <summary>下一次开火还要多少游戏秒才会再计一次暴露（0 = 下一发就计）。</summary>
        public static double ChargeRemaining(CampaignState s)
        {
            return GameClock.SecondsUntil(s?.SignalCore?.RawChargeReadyTick ?? 0);
        }

        /// <summary>
        /// 战斗内核翻译武器参数时问：这台接入的机器要不要带 <c>RawGated</c>（下一发算一次“发动”）。
        /// 条件：接入口生效的固件里有未破解的**常规**固件（核心的走反应），且信号上的计次间隔已过。
        /// </summary>
        public static bool ShouldArmCharge(CampaignState s, string[] rawFirmwareIds)
        {
            if (s == null || rawFirmwareIds == null || rawFirmwareIds.Length == 0 || ChargeRemaining(s) > 0)
            {
                return false;
            }
            for (int i = 0; i < rawFirmwareIds.Length; i++)
            {
                if (!FirmwareKinds.IsCore(rawFirmwareIds[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 内核报告：带 RawGated 的机器开了一枪（玩法事件，不丢）。这台机器接入口里每枚未破解常规固件各计一次暴露（+2），
        /// 信号开始计次间隔；这台（以及信号现在所在的那台）重新下发武器参数（间隔内不带门控）。
        /// 事件被顺延处理、信号已经换了机器：仍按信号核现在的内容插进这台的接入口来结算（内核只在接入时给它带门控，不会误计 AI 驾驶）。
        /// </summary>
        public static void OnRawFired(CampaignState s, int logicId)
        {
            if (s == null || logicId <= 0)
            {
                return;
            }
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForUplink(s, logicId, s.RandomSeed, SignalCoreService.CurrentContentIds(s));
            string[] raws = r.Success && r.Preview?.RawFirmwareIds != null
                ? r.Preview.RawFirmwareIds.Where(id => !FirmwareKinds.IsCore(id)).ToArray()
                : Array.Empty<string>();
            if (raws.Length > 0 && ChargeRemaining(s) <= 0)
            {
                float total = 0f;
                foreach (string fw in raws)
                {
                    CampaignExposureLedger.GrantRawFire(s, fw);
                    total += ExposurePerFire;
                    RawFiredCount++;
                }
                CampaignFgStateDomains.EnsureAll(s);
                s.SignalCore.RawChargeReadyTick = GameClock.TickAfter(ChargeIntervalSeconds); // 整数步（DEBT-FG1SIG07-05）
                SignalUplinkService.PushFeedback(GameText.Format("signal.raw.fired",
                    string.Join(GameText.Get("signal.core.summary_sep"), raws.Select(id => FirmwareKinds.DisplayName(id) ?? id)),
                    total.ToString("0.#", CultureInfo.InvariantCulture)));
            }
            MachineLoadoutRegistry.NotifyChanged(logicId);
            int cur = SignalUplinkService.CurrentMachine(s);
            if (cur != 0 && cur != logicId)
            {
                MachineLoadoutRegistry.NotifyChanged(cur);
            }
        }

        /// <summary>每个模拟步（整个世界，与观察无关）：计次间隔到期 → 清零，信号所在的机器重新带上门控。O(1)。</summary>
        public static void SimStep(CampaignState s)
        {
            SignalCoreState core = s?.SignalCore;
            if (core == null || core.RawChargeReadyTick <= 0 || GameClock.Ticks < core.RawChargeReadyTick)
            {
                return;
            }
            core.RawChargeReadyTick = 0;
            RearmCount++;
            int id = SignalUplinkService.CurrentMachine(s);
            if (id != 0)
            {
                MachineLoadoutRegistry.NotifyChanged(id);
            }
        }

        // ── 获得（带回的敌方加密固件）───────────────────────────────────────────────

        /// <summary>这件回收物解析后解锁的是不是敌方加密固件；是就给出固件 ID。</summary>
        public static bool TryEncryptedFirmwareOf(string questContentId, out string firmwareId)
        {
            firmwareId = null;
            if (string.IsNullOrEmpty(questContentId) || !HomeValleyAnalysis.YieldTable.TryGetValue(questContentId, out HomeValleyAnalysis.YieldInfo info))
            {
                return false;
            }
            if (!FirmwareKinds.IsEnemyProtocol(info.UnlockContentId))
            {
                return false;
            }
            firmwareId = info.UnlockContentId;
            return true;
        }

        /// <summary>
        /// 已带回（Recovered）的敌方加密固件，各发一枚固件芯片到基元仓（仓满进待领取）。按回收实例幂等：撤离结算、读档、进家园都可以调。
        /// <paramref name="notify"/>：给玩家字幕 / 提示音（读档补发时不响）。返回本次新发了几枚。O(回收物数)，不按帧。
        /// </summary>
        public static int GrantRecovered(CampaignState s, bool notify = true)
        {
            if (s?.RegionQuestItems == null)
            {
                return 0;
            }
            int granted = 0;
            foreach (RegionQuestItemRecord item in s.RegionQuestItems)
            {
                if (item == null || item.State != RegionQuestItemState.Recovered || !TryEncryptedFirmwareOf(item.ContentId, out string fw))
                {
                    continue;
                }
                string partId = PrimitiveInventory.TryGrantEncryptedFirmware(s, item.SalvageInstanceId, fw, out bool pending);
                if (partId == null)
                {
                    continue;
                }
                granted++;
                GrantedCount++;
                if (notify)
                {
                    GuidanceHooks.Raise(GuidanceHooks.SignalFirstEncryptedFirmware);
                    string name = FirmwareKinds.DisplayName(fw) ?? fw;
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Pickup,
                        GameText.Format(pending ? "signal.raw.granted_pending" : "signal.raw.granted", name));
                }
            }
            if (granted > 0)
            {
                FirmwareKinds.NotifyCrackStateChanged();
            }
            return granted;
        }

        // ── 破解 ─────────────────────────────────────────────────────────────────

        /// <summary>解析台破解了 <paramref name="contentId"/>（已写进解锁表之后调用）：界面刷新标记；信号在某台机器里就让它按已破解重编译；给出说明。冷却不动。</summary>
        public static void OnCracked(CampaignState s, string contentId)
        {
            if (s == null || !FirmwareKinds.IsEnemyProtocol(contentId))
            {
                return;
            }
            CrackedCount++;
            FirmwareKinds.NotifyCrackStateChanged();
            int id = SignalUplinkService.CurrentMachine(s);
            if (id != 0)
            {
                MachineLoadoutRegistry.NotifyChanged(id);
            }
            SignalUplinkService.PushFeedback(GameText.Format("signal.raw.cracked", FirmwareKinds.DisplayName(contentId) ?? contentId,
                GameText.Get(FirmwareKinds.AfterCrackKey(contentId))));
        }
    }
}
