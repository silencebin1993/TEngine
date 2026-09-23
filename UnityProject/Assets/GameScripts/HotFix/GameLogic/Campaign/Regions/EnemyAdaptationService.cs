using System;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-ADAPT-01 STORY-EXECUTION-CARDS.md：出发时锁定敌方反制的唯一选择/锁定入口。
    ///
    /// ── 范围裁决（写入证据文档，非本类自行裁剪）──
    /// DEMO-CONTENT-LOCK.md §4.3 与行203"第二次准备/第三次准备"两行清楚区分：第二次出征准备页只有
    /// "外围侦察...主核心区暂不可进入"的目标文案，第三次准备页才第一次出现"敌方反制名、来源和具体
    /// 应对建议；未见情报时显示None"。破碎都市（第一次出征）没有"上次战斗暴露"这个前提（不存在更早
    /// 一次战斗），本类因此只对 <see cref="ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost"/>
    /// 生效；<see cref="ExpeditionDepartureService.ExpeditionTarget.SilentRuins"/> 恒 <see cref="AdaptationCatalog.None"/>。
    ///
    /// ── 选择算法（"根据上次战斗暴露与构筑摘要选择...算法确定、种子可回放"）──
    /// 1. 暴露门槛：<see cref="CampaignExposureLedger.HasReachedAdaptationIntel"/>（60阈值，AC-EXP-008
    ///    "60阈值...下一次出征的adaptation情报"）未达标 → 恒 <see cref="AdaptationCatalog.None"/>（"无历史
    ///    为None"/"None安全默认"，AC-ADP-001 字面要求）。
    /// 2. 构筑摘要：现存活机器（不限区域，反映玩家当前技术投入）逐台解析已装配蓝图版本的
    ///    <see cref="BlueprintCircuitCompiler.DetectReactionId"/>，统计"熔穿过载/标记跳转/常规配置（有
    ///    主武器但未触发反应）"三类数量。玩家主力反应是敌方"现有技术树内编制变化"的唯一依据——熔穿过载
    ///    主力 → HeatResistant（针对性抵消穿甲）；标记跳转主力（点杀分散打法）→ Flanker（侧翼打乱阵型）；
    ///    无明显专精（含没有任何存活机器装配过反应）→ JammerSupport（通用压制）。
    /// 3. 三类计数并列最高时，用 <see cref="CampaignRandomService"/> 播种的确定性 RNG（种子 = 战役种子
    ///    异或区域出征次数）在并列候选中选一个——同一存档同一次出征永远选出同一个结果，不同出征次数
    ///    天然产生不同选择，不需要额外持久化一个"上次选过谁"的历史字段。
    ///
    /// ── 锁定语义 ──
    /// <see cref="ComputeAdaptation"/> 是纯查询（面板每次刷新都可以重算，不写状态，供准备页实时预览）；
    /// <see cref="LockAdaptation"/> 才真正写 <see cref="RegionRecord.AdaptationId"/>，只应由
    /// <see cref="ExpeditionDepartureService.TryDepart"/> 在确认出发、写入 <c>foundry_outpost</c> 区域记录
    /// 时调用一次——写入之后升级蓝图/暴露下降/死亡/读档都不会改变已经写入存档的这个字符串，天然满足
    /// "该次区域锁定...战中不偷换"（同一份 <see cref="RegionRecord"/> 就是持久化载体，不需要额外快照）。</summary>
    public static class EnemyAdaptationService
    {
        private readonly struct BuildSummary
        {
            public readonly int MeltOverloadCount;
            public readonly int MarkJumpCount;
            public readonly int PlainCount;

            public BuildSummary(int meltOverloadCount, int markJumpCount, int plainCount)
            {
                MeltOverloadCount = meltOverloadCount;
                MarkJumpCount = markJumpCount;
                PlainCount = plainCount;
            }

            public int Total => MeltOverloadCount + MarkJumpCount + PlainCount;
        }

        /// <summary>现场解析全部存活机器的装配反应，与 <see cref="FoundryOutpostRegion.ComputeCoreGateLights"/>
        /// "现役机实装"同一读取手法（<see cref="BlueprintEditorService.Find"/> → 对应版本 →
        /// <see cref="BlueprintCircuitBoard.FromVersion"/> → <see cref="BlueprintCircuitCompiler.DetectReactionId"/>），
        /// 不新造第二套蓝图解析路径。</summary>
        private static BuildSummary SummarizeBuilds(CampaignState state)
        {
            int melt = 0, mark = 0, plain = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                BlueprintRecord record = BlueprintEditorService.Find(state, m.BlueprintId);
                BlueprintVersionRecord version = record?.Versions?.FirstOrDefault(v => v.Version == m.BlueprintVersion);
                if (version == null || string.IsNullOrEmpty(version.PrimaryId))
                {
                    continue; // 没有主组件的半成品蓝图不计入统计。
                }
                BlueprintCircuitBoard board = BlueprintCircuitBoard.FromVersion(version);
                string reactionId = BlueprintCircuitCompiler.DetectReactionId(board);
                if (reactionId == MechanicalReactionCatalog.ReactionMeltOverloadId)
                {
                    melt++;
                }
                else if (reactionId == MechanicalReactionCatalog.ReactionMarkJumpId)
                {
                    mark++;
                }
                else
                {
                    plain++;
                }
            }
            return new BuildSummary(melt, mark, plain);
        }

        /// <summary>纯查询——不写任何状态，供准备页实时预览与 <see cref="LockAdaptation"/> 共用同一份
        /// 判定逻辑（"面板显示与出发时锁定必须是同一套算法"，不允许面板预览一套、出发时再算一套）。</summary>
        public static string ComputeAdaptation(CampaignState state)
        {
            if (state == null)
            {
                return AdaptationCatalog.None;
            }
            if (!CampaignExposureLedger.HasReachedAdaptationIntel(state))
            {
                return AdaptationCatalog.None;
            }

            BuildSummary summary = SummarizeBuilds(state);
            if (summary.Total == 0)
            {
                return AdaptationCatalog.None; // 没有任何已装配主武器的存活机器，没有可依据的构筑摘要。
            }

            int top = Math.Max(summary.MeltOverloadCount, Math.Max(summary.MarkJumpCount, summary.PlainCount));
            var candidates = new System.Collections.Generic.List<string>(3);
            if (summary.MeltOverloadCount == top)
            {
                candidates.Add(AdaptationCatalog.HeatResistant);
            }
            if (summary.MarkJumpCount == top)
            {
                candidates.Add(AdaptationCatalog.Flanker);
            }
            if (summary.PlainCount == top)
            {
                candidates.Add(AdaptationCatalog.JammerSupport);
            }
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            RegionRecord region = FoundryOutpostRegion.Find(state);
            int salt = unchecked((region?.ExpeditionCount ?? 0) * 7919 + 0x41444150); // 'ADAP' 常量盐值。
            Random rng = new Random(unchecked(state.RandomSeed ^ salt));
            return candidates[rng.Next(candidates.Count)];
        }

        /// <summary>唯一写入口——只应由 <see cref="ExpeditionDepartureService.TryDepart"/> 在确认出发时调用
        /// 一次，写入后的值即为"该次远征锁定"的最终结果（见类注释锁定语义）。</summary>
        public static string LockAdaptation(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null)
            {
                return AdaptationCatalog.None;
            }
            string id = ComputeAdaptation(state);
            region.AdaptationId = id;
            Log.Info($"[EnemyAdaptationService] 敌方反制已锁定：{id}（暴露{state.SignalExposure:F0}，" +
                $"第{region.ExpeditionCount + 1}次出击）。");
            return id;
        }
    }
}
