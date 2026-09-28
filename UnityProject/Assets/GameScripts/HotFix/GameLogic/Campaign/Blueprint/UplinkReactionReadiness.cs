using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>
    /// FG1-SIG-05（FG01 FGR-SIG-090；FG02 FGR-FW-003）：一张电路“你接入时”能打出哪条具名反应。
    ///
    /// Demo 里熔穿过载（重炮 + 过载）、标记跳转（连射器 / 切割束 + 标记器 + 标记跳转）由机器电路自己的固件触发，AI 驾驶时也会打；
    /// 正式版里过载、标记跳转是核心固件：放不进机器电路（FGR-SIG-012），AI 永远不用（FGR-SIG-090），只能由信号带着、接入时插进接入口。
    /// 所以 Demo 里读“这张蓝图含不含某反应”的内容（反应研究费、OBJ-06 / OBJ-08、铸造前哨核心门三灯、敌方适应的构筑统计）改读这里：
    /// “这张蓝图在你接入、信号核带上触发固件时，能不能打出这条反应”。
    ///
    /// 判定只走真实接入的同一个编译入口（<see cref="UplinkCompiler.CompileUplinked(BlueprintCircuitBoard, IReadOnlyList{string}, int)"/>：
    /// 接入口、配额、路径上限、接通判定都与接入后的正式结算一致，IC-REQ-010），不另写第二套配方：
    /// 对每条由核心固件触发的具名反应，假设信号核只带它的触发固件，看编译结果是否正是这条反应。
    /// AI 驾驶时本来就有的反应（只由常规固件构成；目前没有）也算就绪。
    ///
    /// 开销：一张电路的结果按签名（<see cref="BlueprintCircuitBoard.ComputeSignature"/>，含接入口与内容版本）缓存，
    /// 固件种类表重载时整体失效；只在保存蓝图、核心门输入变化、出发前适应预览时调用，与帧无关。
    /// </summary>
    public static class UplinkReactionReadiness
    {
        /// <summary>与 <see cref="BlueprintCircuitCompiler.DetectReactionId(string, string, IEnumerable{string})"/> 的优先级一致（标记跳转先于熔穿过载）。</summary>
        private static readonly string[] ReactionOrder =
        {
            MechanicalReactionCatalog.ReactionMarkJumpId,
            MechanicalReactionCatalog.ReactionMeltOverloadId,
        };

        private const int CacheLimit = 256;
        private static readonly Dictionary<string, string[]> Cache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static int _kindsRevision = -1;

        /// <summary>真正跑编译的次数（缓存未命中）。自检用来证明缓存生效、开销与调用次数无关。</summary>
        public static int CompileCount { get; private set; }

        /// <summary>
        /// 这张电路“你接入时”能打出的具名反应（null = 没有）。<paramref name="state"/> 非空时，触发固件必须已解锁
        /// （没解锁的固件刻印不出来，谈不上就绪）；为空时只看电路本身。
        /// </summary>
        public static string ReadyReactionId(CampaignState state, BlueprintCircuitBoard board)
        {
            if (board == null)
            {
                return null;
            }
            foreach (string reactionId in ReadyReactions(board))
            {
                string trigger = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
                if (state == null || trigger == null || MechanicalContentUnlock.IsUnlocked(state, trigger))
                {
                    return reactionId;
                }
            }
            return null;
        }

        /// <summary>某个已保存版本“你接入时”能打出的具名反应。</summary>
        public static string ReadyReactionId(CampaignState state, BlueprintVersionRecord version) =>
            version == null ? null : ReadyReactionId(state, BlueprintCircuitBoard.FromVersion(version));

        /// <summary>这台机器当前装的蓝图版本“你接入时”能打出的具名反应（阵亡 / 没有蓝图 / 版本不可解析为 null）。</summary>
        public static string MachineReadyReaction(CampaignState state, MachineRecord machine)
        {
            if (state == null || machine == null || !machine.IsAlive || string.IsNullOrEmpty(machine.BlueprintId))
            {
                return null;
            }
            BlueprintRecord record = BlueprintEditorService.Find(state, machine.BlueprintId);
            BlueprintVersionRecord version = record?.Versions?.FirstOrDefault(v => v != null && v.Version == machine.BlueprintVersion);
            if (version == null || string.IsNullOrEmpty(version.PrimaryId))
            {
                return null;
            }
            return ReadyReactionId(state, version);
        }

        /// <summary>这张电路按优先级排列的全部就绪反应（与解锁无关；按签名缓存）。</summary>
        private static string[] ReadyReactions(BlueprintCircuitBoard board)
        {
            if (_kindsRevision != FirmwareKinds.Revision)
            {
                Cache.Clear();
                _kindsRevision = FirmwareKinds.Revision;
            }
            string key = board.ComputeSignature();
            if (Cache.TryGetValue(key, out string[] cached))
            {
                return cached;
            }
            CompileCount++;
            var ready = new List<string>(1);
            string aiReaction = BlueprintCircuitCompiler.DetectReactionId(board);
            if (aiReaction != null)
            {
                ready.Add(aiReaction);
            }
            if (board.HasUplink)
            {
                foreach (string reactionId in ReactionOrder)
                {
                    string trigger = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
                    if (trigger == null || ready.Contains(reactionId))
                    {
                        continue;
                    }
                    BlueprintCircuitPreview up = UplinkCompiler.CompileUplinked(board, new[] { trigger });
                    if (up.ReactionId == reactionId)
                    {
                        ready.Add(reactionId);
                    }
                }
            }
            string[] result = ready.ToArray();
            if (Cache.Count >= CacheLimit)
            {
                Cache.Clear();
            }
            Cache[key] = result;
            return result;
        }

        public static void ResetForTests()
        {
            Cache.Clear();
            _kindsRevision = -1;
            CompileCount = 0;
        }
    }
}
