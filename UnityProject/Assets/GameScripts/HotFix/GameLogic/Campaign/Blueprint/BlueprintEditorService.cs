using System;
using System.Linq;
using GameLogic.Campaign.Content;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>ER4-BLP-01 STORY-EXECUTION-CARDS.md：蓝图编辑器"新建/打开/复制/另存/保存/取消/恢复草稿/
    /// 归档"的唯一 CRUD 入口。<see cref="BlueprintCircuitBoard"/> 只是可变编辑模型（装/卸/画边/校验/签名），
    /// 本类是把编辑模型落成 <see cref="BlueprintRecord"/>/<see cref="BlueprintVersionRecord"/> 的编排层——
    /// UI（<c>CircuitBoardPanelUIToolkit</c>）与自动化测试都必须经本类完成"保存"这一步，不直接改
    /// <see cref="CampaignState.BlueprintRecords"/>。</summary>
    public readonly struct BlueprintSaveResult
    {
        public readonly bool Success;
        public readonly string FailureReason;
        public readonly string BlueprintId;
        public readonly int Version;
        public readonly string ReactionId;
        public readonly int TechDataCharged;

        private BlueprintSaveResult(bool success, string failureReason, string blueprintId, int version,
            string reactionId, int techDataCharged)
        {
            Success = success;
            FailureReason = failureReason;
            BlueprintId = blueprintId;
            Version = version;
            ReactionId = reactionId;
            TechDataCharged = techDataCharged;
        }

        public static BlueprintSaveResult Ok(string blueprintId, int version, string reactionId, int techDataCharged) =>
            new BlueprintSaveResult(true, null, blueprintId, version, reactionId, techDataCharged);

        public static BlueprintSaveResult Fail(string reason) =>
            new BlueprintSaveResult(false, reason, null, 0, null, 0);
    }

    public static class BlueprintEditorService
    {
        public static BlueprintRecord Find(CampaignState state, string blueprintId)
        {
            if (state?.BlueprintRecords == null || string.IsNullOrEmpty(blueprintId))
            {
                return null;
            }
            return state.BlueprintRecords.FirstOrDefault(b => b != null && b.BlueprintId == blueprintId);
        }

        public static BlueprintVersionRecord FindActiveVersion(CampaignState state, string blueprintId)
        {
            BlueprintRecord record = Find(state, blueprintId);
            return record?.Versions?.FirstOrDefault(v => v.Version == record.ActiveVersion);
        }

        public static string GenerateBlueprintId() => "bp_" + Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>"新建"：登记一条没有任何已保存版本的空白记录（<c>ActiveVersion=0</c>，
        /// <c>Versions</c> 为空数组）。玩家在电路板编辑一个从 <see cref="BlueprintCircuitBoard.CreateDefault"/>
        /// 起步的草稿，首次点"保存"时才真正追加版本 1（见 <see cref="TrySave"/>）——"新建"这一步本身不消耗
        /// 任何资源，也不因为半途放弃而留下垃圾版本。</summary>
        public static BlueprintRecord CreateNew(CampaignState state, string displayName)
        {
            if (state == null)
            {
                return null;
            }
            state.BlueprintRecords ??= Array.Empty<BlueprintRecord>();
            var record = new BlueprintRecord
            {
                BlueprintId = GenerateBlueprintId(),
                DisplayName = string.IsNullOrEmpty(displayName) ? "新蓝图" : displayName,
                ActiveVersion = 0,
                Archived = false,
                Versions = Array.Empty<BlueprintVersionRecord>(),
            };
            state.BlueprintRecords = state.BlueprintRecords.Append(record).ToArray();
            return record;
        }

        /// <summary>"复制"：以 <paramref name="sourceBlueprintId"/> 当前活跃版本的完整内容（外层装配+
        /// 3×3 电路+固件）起草一条全新独立记录（新 <see cref="BlueprintRecord.BlueprintId"/>），不共享任何
        /// 版本引用。找不到来源或来源尚无已保存版本时返回 null，不生成空壳。</summary>
        public static BlueprintRecord Duplicate(CampaignState state, string sourceBlueprintId, string newDisplayName)
        {
            BlueprintVersionRecord sourceVersion = FindActiveVersion(state, sourceBlueprintId);
            if (sourceVersion == null)
            {
                return null;
            }
            BlueprintRecord source = Find(state, sourceBlueprintId);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.FromVersion(sourceVersion);
            BlueprintRecord record = CreateNew(state,
                string.IsNullOrEmpty(newDisplayName) ? $"{source?.DisplayName ?? sourceBlueprintId} 副本" : newDisplayName);
            BlueprintVersionRecord newVersion = board.ToVersion(1, state.PlaySeconds);
            record.Versions = new[] { newVersion };
            record.ActiveVersion = 1;
            return record;
        }

        /// <summary>"归档"：只翻 <see cref="BlueprintRecord.Archived"/> 标记，永不删除记录或任何版本——
        /// AC-BLP-004"被机器引用的版本不可删除，只可归档；读档引用仍解析"。归档后的记录仍可被
        /// <see cref="FindActiveVersion"/> 正常解析（机器/队列锁定的版本引用不受影响），只是不再出现在
        /// 编辑器默认的"可打开"列表（UI 侧过滤，不是本类职责）。</summary>
        public static CircuitOpResult TryArchive(CampaignState state, string blueprintId)
        {
            BlueprintRecord record = Find(state, blueprintId);
            if (record == null)
            {
                return CircuitOpResult.Fail("blueprint_not_found", $"找不到蓝图 '{blueprintId}'。");
            }
            record.Archived = true;
            return CircuitOpResult.Ok();
        }

        public static CircuitOpResult TryUnarchive(CampaignState state, string blueprintId)
        {
            BlueprintRecord record = Find(state, blueprintId);
            if (record == null)
            {
                return CircuitOpResult.Fail("blueprint_not_found", $"找不到蓝图 '{blueprintId}'。");
            }
            record.Archived = false;
            return CircuitOpResult.Ok();
        }

        /// <summary>反应对应的一次性技术数据成本——DEMO-CONTENT-LOCK.md §2.5："首次成功保存标记跳转
        /// 蓝图 −10；首次成功保存熔穿过载蓝图 −15"。</summary>
        public static int ReactionTechDataCost(string reactionId)
        {
            if (reactionId == MechanicalReactionCatalog.ReactionMarkJumpId)
            {
                return 10;
            }
            if (reactionId == MechanicalReactionCatalog.ReactionMeltOverloadId)
            {
                return 15;
            }
            return 0;
        }

        /// <summary>同一 <paramref name="reactionId"/> 是否已在本战役扣过研究费——"同 campaignId+reactionId
        /// 一次；后续版本免费"。单局 <see cref="CampaignState"/> 即隐含 campaignId 作用域，EventLedger 内
        /// 幂等标记键本身不需要再拼 campaignId。</summary>
        public static bool IsReactionCharged(CampaignState state, string reactionId)
        {
            if (state?.EventLedger == null || string.IsNullOrEmpty(reactionId))
            {
                return false;
            }
            string marker = ReactionChargeEventId(reactionId);
            return state.EventLedger.Any(e => e != null && e.EventId == marker);
        }

        public static string ReactionChargeEventId(string reactionId) => $"blueprint_reaction_charge:{reactionId}";

        /// <summary>外层槽保存前的最后一道防线——即便 UI 只暴露已解锁选项，execute_code/未来其它调用方
        /// 仍可能直接摆一个非法/未解锁组合进 <see cref="BlueprintCircuitBoard"/> 再调用保存，这里补上
        /// "保存前校验：内容已解锁、槽位匹配"的服务端校验，不能只信任 UI 层已经挡过一次。</summary>
        private static CircuitOpResult ValidateOuterSlots(CampaignState state, BlueprintCircuitBoard board)
        {
            if (string.IsNullOrEmpty(board.ChassisId))
            {
                return CircuitOpResult.Fail("chassis_required", "尚未选择底盘。");
            }
            string chassisArchetype = ChassisCatalog.ResolveArchetype(board.ChassisId) ?? board.ChassisId;
            if (!ChassisCatalog.TryGet(chassisArchetype, out MechanicalContentDef chassisDef))
            {
                return CircuitOpResult.Fail("unknown_chassis", $"'{board.ChassisId}' 不是已知底盘。");
            }
            if (!MechanicalContentUnlock.IsUnlocked(state, chassisArchetype))
            {
                return CircuitOpResult.Fail("chassis_locked", $"底盘“{chassisDef.DisplayName}”尚未解锁。");
            }

            // PRIMITIVE-FULL-DEMO-SPEC.md/BlueprintCircuitDefaults.cs 既有先例：ERC-001（搬运轮式）
            // 是"无主组件"的纯搬运底盘，没有可攻击的 8 号汇槽，这是设计使然不是缺口——主组件因此是
            // "正常应有 1 个"而非保存期硬性必填，只要有值就必须合法+已解锁，为空则跳过（既有 hauler 蓝图
            // 仍可正常重新保存，不因本 Story 新增校验而回归）。
            if (!string.IsNullOrEmpty(board.PrimaryId))
            {
                if (!ComponentCatalog.TryGet(board.PrimaryId, out MechanicalContentDef primaryDef)
                    || primaryDef.Category != MechanicalContentCategory.MainComponent)
                {
                    return CircuitOpResult.Fail("wrong_slot", $"'{board.PrimaryId}' 不是合法的主组件（错槽）。");
                }
                if (!MechanicalContentUnlock.IsUnlocked(state, board.PrimaryId))
                {
                    return CircuitOpResult.Fail("primary_locked", $"主组件“{primaryDef.DisplayName}”尚未解锁。");
                }
            }

            if (!string.IsNullOrEmpty(board.UtilityId))
            {
                if (!ComponentCatalog.TryGet(board.UtilityId, out MechanicalContentDef utilityDef)
                    || utilityDef.Category != MechanicalContentCategory.FunctionComponent)
                {
                    return CircuitOpResult.Fail("wrong_slot", $"'{board.UtilityId}' 不是合法的功能组件（错槽）。");
                }
                if (!MechanicalContentUnlock.IsUnlocked(state, board.UtilityId))
                {
                    return CircuitOpResult.Fail("utility_locked", $"功能组件“{utilityDef.DisplayName}”尚未解锁。");
                }
            }

            if (!string.IsNullOrEmpty(board.StructureId))
            {
                if (!ComponentCatalog.TryGet(board.StructureId, out MechanicalContentDef structureDef)
                    || structureDef.Category != MechanicalContentCategory.Structure)
                {
                    return CircuitOpResult.Fail("wrong_slot", $"'{board.StructureId}' 不是合法的结构（错槽）。");
                }
                if (!MechanicalContentUnlock.IsUnlocked(state, board.StructureId))
                {
                    return CircuitOpResult.Fail("structure_locked", $"结构“{structureDef.DisplayName}”尚未解锁。");
                }
            }

            foreach (string firmwareId in board.FirmwareSlots)
            {
                if (string.IsNullOrEmpty(firmwareId))
                {
                    continue;
                }
                if (!FirmwareCatalog.TryGet(firmwareId, out MechanicalContentDef fwDef))
                {
                    return CircuitOpResult.Fail("firmware_unknown", $"'{firmwareId}' 不是已知固件。");
                }
                if (!MechanicalContentUnlock.IsUnlocked(state, firmwareId))
                {
                    return CircuitOpResult.Fail("firmware_locked", $"固件“{fwDef.DisplayName}”尚未解锁。");
                }
            }

            return CircuitOpResult.Ok();
        }

        /// <summary>"保存"（追加新版本到既有记录）/"另存为"（<paramref name="saveAsNewRecord"/>=true，
        /// 强制新建一条独立记录，忽略 <paramref name="blueprintId"/> 是否已存在）的统一入口。
        ///
        /// 顺序：①外层槽校验 → ②3×3 电路校验（复用既有 <see cref="BlueprintCircuitBoard.Validate"/>）→
        /// ③反应识别+技术数据扣费（仅首次同反应）→ ④落盘。任何一步失败立即返回 Fail 并保留草稿，此前
        /// 不触碰任何资源池——"任何失败保持草稿且不扣资源"（DEMO-IMPLEMENTATION-SPEC.md）。</summary>
        public static BlueprintSaveResult TrySave(CampaignState state, BlueprintCircuitBoard board,
            string blueprintId, string displayName, bool saveAsNewRecord)
        {
            if (state == null || board == null)
            {
                return BlueprintSaveResult.Fail("no-active-campaign");
            }

            CircuitOpResult outerCheck = ValidateOuterSlots(state, board);
            if (!outerCheck.Success)
            {
                return BlueprintSaveResult.Fail($"[{outerCheck.Code}] {outerCheck.Message}");
            }

            CircuitValidationResult validation = board.Validate();
            if (!validation.IsValid)
            {
                string reason = string.Join("；", validation.Issues.Select(i => i.Message));
                return BlueprintSaveResult.Fail(reason);
            }

            string reactionId = BlueprintCircuitCompiler.DetectReactionId(board);
            int techCost = ReactionTechDataCost(reactionId);
            bool needsCharge = reactionId != null && techCost > 0 && !IsReactionCharged(state, reactionId);
            string chargeTxId = null;

            if (needsCharge)
            {
                chargeTxId = "blueprint_reaction_charge_tx_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                CampaignEconomyLedger.LedgerResult propose = CampaignEconomyLedger.ProposeConsume(
                    state, chargeTxId, blueprintId ?? "blueprint_editor", CampaignEconomyLedger.ResourceTechData, techCost);
                if (!propose.Success)
                {
                    return BlueprintSaveResult.Fail($"技术数据登记失败：{propose.FailureReason}");
                }
                CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, chargeTxId);
                if (!reserve.Success)
                {
                    CampaignEconomyLedger.Cancel(state, chargeTxId);
                    return BlueprintSaveResult.Fail($"技术数据不足（需要 {techCost}）：{reserve.FailureReason}");
                }
            }

            BlueprintRecord record = saveAsNewRecord ? null : Find(state, blueprintId);
            if (record == null)
            {
                state.BlueprintRecords ??= Array.Empty<BlueprintRecord>();
                string newId = saveAsNewRecord || string.IsNullOrEmpty(blueprintId) ? GenerateBlueprintId() : blueprintId;
                record = new BlueprintRecord
                {
                    BlueprintId = newId,
                    DisplayName = string.IsNullOrEmpty(displayName) ? "新蓝图" : displayName,
                    ActiveVersion = 0,
                    Archived = false,
                    Versions = Array.Empty<BlueprintVersionRecord>(),
                };
                state.BlueprintRecords = state.BlueprintRecords.Append(record).ToArray();
            }
            else if (!string.IsNullOrEmpty(displayName))
            {
                record.DisplayName = displayName;
            }

            int nextVersion = (record.Versions?.Length > 0 ? record.Versions.Max(v => v.Version) : 0) + 1;
            BlueprintVersionRecord newVersion = board.ToVersion(nextVersion, state.PlaySeconds);
            record.Versions = (record.Versions ?? Array.Empty<BlueprintVersionRecord>()).Append(newVersion).ToArray();
            record.ActiveVersion = nextVersion;

            if (chargeTxId != null)
            {
                CampaignEconomyLedger.Commit(state, chargeTxId);
                state.EventLedger ??= Array.Empty<EventLedgerEntry>();
                state.EventLedger = state.EventLedger.Append(new EventLedgerEntry
                {
                    EventId = ReactionChargeEventId(reactionId),
                    Category = "BlueprintReactionCharge",
                    GrantedAtPlaySeconds = state.PlaySeconds,
                    Payload = reactionId,
                }).ToArray();
            }

            // ER6-EXPOSE-01："首次使用异派固件 +10"——"异派"＝跨派系组合，board.ComputeFactionTags()
            // 已经是"跨派系"的唯一权威判定（Length>=2，见该方法类注释），不重新发明第二套判据。
            // 按 blueprintId+version 记账：同一蓝图的每个版本号只会被保存一次，天然满足"首次"语义。
            if (board.ComputeFactionTags().Length >= 2)
            {
                CampaignExposureLedger.GrantCrossFactionFirmwareFirstUse(state, record.BlueprintId, nextVersion);
            }

            // ER6-LOOP-01：蓝图保存是 OBJ-06/08"蓝图保存"子条件的唯一真实写入口，保存成功后立即重算，
            // 不必等玩家回到家园下一帧或下一次远征触发才反映。
            CampaignObjectiveTracker.Recompute(state);

            return BlueprintSaveResult.Ok(record.BlueprintId, nextVersion, reactionId, needsCharge ? techCost : 0);
        }
    }
}
