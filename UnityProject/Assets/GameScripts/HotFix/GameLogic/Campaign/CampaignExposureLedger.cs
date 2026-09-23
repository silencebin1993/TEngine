using System;
using System.Linq;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>ER6-EXPOSE-01 STORY-EXECUTION-CARDS.md：信号暴露（<see cref="CampaignState.SignalExposure"/>，
    /// ERD-DAT-001 骨架字段，此前从未被真实写入过）的唯一写入口。六类批准来源（DEMO-IMPLEMENTATION-SPEC.md
    /// ERD-ECO-004 唯一权威数值）：
    /// <list type="bullet">
    /// <item>生产重型机 +8（<see cref="GrantHeavyMachineProduced"/>）</item>
    /// <item>一次远征中每累计30秒直控 +5（<see cref="TickDirectControlExposure"/>）</item>
    /// <item>首次使用异派固件 +10（<see cref="GrantCrossFactionFirmwareFirstUse"/>）</item>
    /// <item>摧毁监听节点：战斗 +8 与链路切断 -15 两笔独立记录，净 -7（<see cref="GrantNodeDestroyed"/>）</item>
    /// <item>家园关闭信号塔主动广播每10秒 -2（<see cref="TickTowerBroadcastOff"/>），同时带宽 -3
    /// （<see cref="TowerBroadcastOffBandwidthPenalty"/>，由 <c>HomeValleyPowerGrid.Recompute</c> 读取）</item>
    /// </list>
    /// "暴露只由批准来源变化"（AC-EXP-005）——本类是唯一写入口，任何其它代码都不得直接改
    /// <see cref="CampaignState.SignalExposure"/>。每笔写入同时经 <see cref="CampaignEventLedger"/>
    /// 幂等判重（防止同一事件被重复结算）与 <see cref="CampaignState.SignalExposureEvents"/>
    /// 明细记录（HUD"来源"展示唯一权威来源，"净值不合并成神秘数值"）。
    ///
    /// ── 真实发现的问题：`HomeValleySoftlockGuard` 曾经绕过本类直接写 SignalExposure ──
    /// `SpawnRescueMachine`（紧急救援机生成）此前有一行 `state.SignalExposure = Mathf.Clamp(state.
    /// SignalExposure + 10f, 0f, 100f);`——"紧急救援机生成"不在 ERD-ECO-004 点名的六类批准来源里，
    /// 是一条真实存在、违反"暴露只由批准来源变化"的未批准写入（此前该字段从未被任何系统真正使用，
    /// 这条写入因此从未产生过可观测后果）。本 Story 已把它删除（见 <c>HomeValleySoftlockGuard</c>
    /// 改动）——紧急救援机生成不应该影响信号暴露，这不是"6选1近似"，是这条写入本身就不该存在。</summary>
    public static class CampaignExposureLedger
    {
        public const float MinExposure = 0f;
        public const float MaxExposure = 100f;

        public const float HeavyMachineProducedDelta = 8f;
        public const float DirectControl30sDelta = 5f;
        public const float CrossFactionFirmwareFirstUseDelta = 10f;
        public const float NodeCombatDelta = 8f;
        public const float NodeLinkCutDelta = -15f;
        public const float TowerBroadcastOffDeltaPer10Seconds = -2f;
        /// <summary>塔关广播的带宽代价——由 <c>HomeValleyPowerGrid.Recompute</c> 读取
        /// <see cref="CampaignState.SignalTowerBroadcastOff"/> 时直接使用，不在本类里改写带宽字段
        /// （带宽唯一真相仍是电网仲裁，见 <see cref="Regions.HomeValleySignal"/> 类注释纪律）。</summary>
        public const float TowerBroadcastOffBandwidthPenalty = 3f;

        public const float ThresholdScoutTip = 30f;
        public const float ThresholdAdaptationIntel = 60f;
        public const float ThresholdCoreReinforcement = 90f;

        private static RegionRecord FindCurrentRegion(CampaignState state) =>
            state?.RegionRecords?.FirstOrDefault(r => r.RegionId == state.CurrentRegionId);

        /// <summary>唯一真正改写 <see cref="CampaignState.SignalExposure"/> 的地方。幂等经
        /// <paramref name="eventId"/>；成功时记 HUD 明细 + 检查阈值跨越。</summary>
        private static bool TryApply(CampaignState state, string eventId, string source, float delta)
        {
            if (state == null || string.IsNullOrEmpty(eventId))
            {
                return false;
            }
            if (!CampaignEventLedger.TryGrant(state, eventId, "SignalExposure", state.PlaySeconds,
                source + ":" + delta.ToString("+0.#;-0.#")))
            {
                return false;
            }

            float before = state.SignalExposure;
            float after = Mathf.Clamp(before + delta, MinExposure, MaxExposure);
            state.SignalExposure = after;

            state.SignalExposureEvents = (state.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>())
                .Append(new SignalExposureEventRecord
                {
                    EventId = eventId,
                    Source = source,
                    Delta = delta,
                    AtPlaySeconds = state.PlaySeconds,
                    ResultingExposure = after,
                }).ToArray();

            Log.Info($"[CampaignExposureLedger] {source} {delta:+0.#;-0.#} -> {after:F0}（{eventId}）。");
            CheckThresholdCrossing(state, before, after);
            return true;
        }

        // ── 六类批准来源 ─────────────────────────────────────────────────────────

        /// <summary>生产重型机 +8——由 <c>HomeValleyFactory.SpawnProducedMachine</c> 在产出
        /// <see cref="HomeValleyLayout.Erc003ChassisId"/>（战斗履带，Demo 唯一"重型"机型）时调用。
        /// <paramref name="machineLogicId"/> 保证同一台机器只记一次（同一 LogicId 永不复用）。</summary>
        public static bool GrantHeavyMachineProduced(CampaignState state, int machineLogicId) =>
            TryApply(state, $"exposure:heavy_produced:{machineLogicId}", "重型机生产", HeavyMachineProducedDelta);

        /// <summary>首次使用异派固件 +10——由 <c>BlueprintEditorService.TrySave</c> 在
        /// <c>board.ComputeFactionTags().Length &gt;= 2</c>（跨派系，"异派"字面对应）时调用，
        /// <paramref name="blueprintId"/>+<paramref name="version"/> 保证"首次"按蓝图版本首次保存
        /// 判定（同一蓝图后续版本若仍跨派系不会重复计——每个 version 号只会被保存一次，天然满足
        /// "首次"语义，不需要额外的"是否已经因跨派系加过分"标记）。</summary>
        public static bool GrantCrossFactionFirmwareFirstUse(CampaignState state, string blueprintId, int version) =>
            TryApply(state, $"exposure:cross_faction_firmware:{blueprintId}:{version}",
                "异派固件首次使用", CrossFactionFirmwareFirstUseDelta);

        /// <summary>摧毁监听节点——由 <c>FracturedCityRegion.TryDestroyListeningNode</c> 成功时调用，
        /// 同时产生两笔独立记录（战斗 +8、链路切断 -15，净 -7 但不合并——DEMO-IMPLEMENTATION-SPEC.md
        /// ERD-ECO-004 原文"两笔来源独立"）。</summary>
        public static void GrantNodeDestroyed(CampaignState state, string nodeId)
        {
            TryApply(state, $"exposure:node_combat:{nodeId}", "摧毁节点", NodeCombatDelta);
            TryApply(state, $"exposure:node_link_cut:{nodeId}", "监听链摧毁", NodeLinkCutDelta);
        }

        /// <summary>家园关闭信号塔主动广播——由 <c>HomeValleyController.Update</c> 每帧调用，累计到
        /// 10秒结算一笔 -2（可能一帧内跨越多个10秒周期，如长时间暂停后恢复，<c>while</c> 循环逐个
        /// 结算，不丢单不合并）。</summary>
        public static void TickTowerBroadcastOff(CampaignState state, float dt)
        {
            if (state == null || !state.SignalTowerBroadcastOff || dt <= 0f)
            {
                return;
            }
            state.TowerBroadcastOffElapsedSeconds += dt;
            while (state.TowerBroadcastOffElapsedSeconds >= 10f)
            {
                state.TowerBroadcastOffElapsedSeconds -= 10f;
                // 序号不需要额外持久化字段——直接数已有多少条同前缀记录作为下一个序号，
                // CampaignEventLedger 本身已经是权威真相，不重复保存一份"计数"。
                int tick = (state.EventLedger?.Count(e => e != null && e.EventId != null
                    && e.EventId.StartsWith("exposure:tower_off_tick:", StringComparison.Ordinal)) ?? 0) + 1;
                TryApply(state, $"exposure:tower_off_tick:{tick}", "塔关广播", TowerBroadcastOffDeltaPer10Seconds);
            }
        }

        /// <summary>一次远征中每累计30秒直控 +5——由 <c>FracturedCityController.Update</c> 每帧调用
        /// （仅在存在直控目标且未暂停时才应该累计，调用方负责只在那种状态下传入正的 dt）。
        /// <paramref name="region"/> 的累计秒数在每次真正出发时清零（"一次远征"的边界，见
        /// <see cref="Regions.ExpeditionDepartureService.TryDepart"/>）。</summary>
        public static void TickDirectControlExposure(CampaignState state, RegionRecord region, float dt)
        {
            if (state == null || region == null || dt <= 0f)
            {
                return;
            }
            region.DirectControlAccumulatedSeconds += dt;
            while (region.DirectControlAccumulatedSeconds >= 30f)
            {
                region.DirectControlAccumulatedSeconds -= 30f;
                int idx = ++state.DirectControlExposureGrantCount;
                TryApply(state, $"exposure:direct_control_30s:{idx}", "远征直控30秒", DirectControl30sDelta);
            }
        }

        /// <summary>玩家主动切换信号塔广播开关——唯一写入口。切换即重置10秒累计计时器（避免"刚关闭
        /// 0.1秒又打开"这种抖动在下次关闭时立刻触发一笔）。</summary>
        public static void SetTowerBroadcastOff(CampaignState state, bool off)
        {
            if (state == null)
            {
                return;
            }
            state.SignalTowerBroadcastOff = off;
            state.TowerBroadcastOffElapsedSeconds = 0f;
        }

        // ── 阈值事件（DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-004"重复规则"）──────────────

        /// <summary>"阈值事件只在从下向上跨越时触发一次"——30 可重复（降到阈值下再升高再次触发）；
        /// 60/90 按"当前区域+当次远征"幂等（每区域每远征最多一次），键天然随 <see cref="RegionRecord.ExpeditionCount"/>
        /// 递增而变化，不需要额外的"是否已重算"标记字段。</summary>
        private static void CheckThresholdCrossing(CampaignState state, float before, float after)
        {
            if (before < ThresholdScoutTip && after >= ThresholdScoutTip)
            {
                state.ScoutTipCrossCount++;
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_scout_tip:{state.ScoutTipCrossCount}",
                    "ExposureThresholdScoutTip", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过30：静默侦察提示。");
            }
            if (before < ThresholdAdaptationIntel && after >= ThresholdAdaptationIntel)
            {
                RegionRecord region = FindCurrentRegion(state);
                string key = region != null ? $"{region.RegionId}:{region.ExpeditionCount}" : "no-region";
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_adaptation:{key}",
                    "ExposureThresholdAdaptationIntel", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过60：下一次出征 adaptation 情报可用。");
            }
            if (before < ThresholdCoreReinforcement && after >= ThresholdCoreReinforcement)
            {
                RegionRecord region = FindCurrentRegion(state);
                string key = region != null ? $"{region.RegionId}:{region.ExpeditionCount}" : "no-region";
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_core_reinforcement:{key}",
                    "ExposureThresholdCoreReinforcement", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过90：下一次核心战入口护甲机增援预告。");
            }
        }

        /// <summary>只读查询：最近 N 笔暴露事件（HUD 展示用，按时间倒序）。</summary>
        public static SignalExposureEventRecord[] RecentEvents(CampaignState state, int count)
        {
            if (state?.SignalExposureEvents == null)
            {
                return Array.Empty<SignalExposureEventRecord>();
            }
            return state.SignalExposureEvents
                .OrderByDescending(e => e.AtPlaySeconds)
                .Take(count)
                .ToArray();
        }

        /// <summary>三档阈值是否已跨越（HUD/日志展示当前"档位"，不重复实现判定逻辑）。</summary>
        public static bool HasReachedScoutTip(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdScoutTip;
        public static bool HasReachedAdaptationIntel(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdAdaptationIntel;
        public static bool HasReachedCoreReinforcement(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdCoreReinforcement;
    }
}
