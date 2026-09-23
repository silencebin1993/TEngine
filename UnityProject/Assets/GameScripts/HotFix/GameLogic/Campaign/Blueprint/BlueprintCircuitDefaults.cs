using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>ER4-PRIM-02 STORY-EXECUTION-CARDS.md 第1条："默认线 0→1→2→5→8 让所有默认机器不经编辑
    /// 可攻击……旧档缺板时迁入默认合法板，不复制玩家仓实例"。
    ///
    /// 本类是这条要求的唯一实现：①为 ERC-001（唯一没有被
    /// <see cref="Regions.HomeValleyFactory.EnsureBlueprintsSeeded"/> 覆盖的默认底盘——它是开局固定单位，
    /// 不进装配站生产列表）补一条默认蓝图；②对 <see cref="CampaignState.BlueprintRecords"/> 里任何缺电路数据
    /// 的版本（无论是刚被 <see cref="Regions.HomeValleyFactory.EnsureBlueprintsSeeded"/> 创建的骨架，还是
    /// ER1-SAVE-01～ER4-FAC-01 期间创建、本 Story 上线前就已存在的旧档）就地迁入默认合法电路板。
    ///
    /// 默认装配数值来自 DEMO-CONTENT-LOCK.md §2.4 各条 SourceDetail 明确点名的"默认初装"，并非臆造：
    /// ERC-003＝连射器+冲刺器+寻的+拖尾；新搬运轮式（bp_hauler）＝切割束；维修悬浮＝维修束+两空固件槽；
    /// ERC-001＝货舱（无主组件——它是纯搬运型，没有可攻击的 8 号汇槽，这是设计使然，不是缺口）。
    /// 用 <see cref="BlueprintCircuitBoard.ComputeScrapCost"/> 反推验证：三条工厂可生产蓝图的合计成本
    /// 精确等于 <see cref="Regions.HomeValleyLayout.FactoryProduceDefaults"/> 既有数字（60/35/55），
    /// 证明该表隐含的默认装配从一开始就应该是这几件套装。</summary>
    public static class BlueprintCircuitDefaults
    {
        private sealed class DefaultLoadout
        {
            public string ChassisId;
            public string PrimaryId;
            public string UtilityId;
            public string StructureId;
            public string[] Firmware = Array.Empty<string>();
            public string DisplayName;
        }

        private static readonly IReadOnlyDictionary<string, DefaultLoadout> Loadouts =
            new Dictionary<string, DefaultLoadout>
            {
                [HomeValleyLayout.BlueprintErc001Id] = new DefaultLoadout
                {
                    ChassisId = HomeValleyLayout.Erc001ChassisId,
                    StructureId = ComponentCatalog.StructCargoId,
                    DisplayName = "搬运轮式 ERC-001",
                },
                [HomeValleyLayout.BlueprintHaulerId] = new DefaultLoadout
                {
                    ChassisId = HomeValleyLayout.Erc002ChassisId,
                    PrimaryId = ComponentCatalog.CompBeamId,
                    DisplayName = "搬运机",
                },
                [HomeValleyLayout.BlueprintErc003Id] = new DefaultLoadout
                {
                    ChassisId = HomeValleyLayout.Erc003ChassisId,
                    PrimaryId = ComponentCatalog.CompGunId,
                    UtilityId = ComponentCatalog.FuncDashId,
                    Firmware = new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwTrailId },
                    DisplayName = "战斗履带 ERC-003",
                },
                [HomeValleyLayout.BlueprintHoverId] = new DefaultLoadout
                {
                    ChassisId = ChassisCatalog.ChassisHoverId,
                    PrimaryId = ComponentCatalog.CompRepairBeamId,
                    DisplayName = "维修机",
                },
            };

        /// <summary>幂等；供 <see cref="Regions.HomeValleyFactory.EnsureBlueprintsSeeded"/> 尾部调用
        /// （覆盖 <c>HomeValleyController.Enter</c>/<c>TryEnqueueProduce</c>/execute_code 手工构造
        /// <c>CampaignState</c> 三类既有调用路径，同 ER4-FAC-01 "不等正式编辑器落地才补" 先例），
        /// 也可在测试里单独调用。</summary>
        public static void EnsureCircuitDataSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.BlueprintRecords ??= Array.Empty<BlueprintRecord>();

            if (!state.BlueprintRecords.Any(b => b.BlueprintId == HomeValleyLayout.BlueprintErc001Id))
            {
                DefaultLoadout lo = Loadouts[HomeValleyLayout.BlueprintErc001Id];
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(
                    lo.ChassisId, lo.PrimaryId, lo.UtilityId, lo.StructureId, lo.Firmware);
                BlueprintVersionRecord version = board.ToVersion(1, state.PlaySeconds);
                var record = new BlueprintRecord
                {
                    BlueprintId = HomeValleyLayout.BlueprintErc001Id,
                    DisplayName = lo.DisplayName,
                    ActiveVersion = 1,
                    Archived = false,
                    Versions = new[] { version },
                };
                state.BlueprintRecords = state.BlueprintRecords.Append(record).ToArray();
            }

            foreach (BlueprintRecord record in state.BlueprintRecords)
            {
                if (record?.Versions == null)
                {
                    continue;
                }
                for (int i = 0; i < record.Versions.Length; i++)
                {
                    BlueprintVersionRecord version = record.Versions[i];
                    if (version == null)
                    {
                        continue;
                    }
                    bool needsMigration = version.CircuitSlotContentIds == null
                        || version.CircuitSlotContentIds.Length != BlueprintCircuitLayout.SlotCount
                        || version.CircuitSlotTypes == null;
                    if (!needsMigration)
                    {
                        continue;
                    }

                    Loadouts.TryGetValue(record.BlueprintId, out DefaultLoadout lo);
                    string chassisId = lo?.ChassisId ?? version.ChassisId;
                    string primaryId = lo?.PrimaryId ?? version.PrimaryId;
                    string utilityId = lo?.UtilityId ?? version.UtilityId;
                    string structureId = lo?.StructureId ?? version.StructureId;
                    string[] firmware = lo?.Firmware ?? version.OrderedFirmwareIds ?? Array.Empty<string>();

                    BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(
                        chassisId, primaryId, utilityId, structureId, firmware);
                    // 保留原始 Version 号与 CreatedAtPlaySeconds——迁移的是"缺失的电路字段"，不是重新
                    // 开一条新版本；ScrapCost/CompileSignature 等其余字段由 board 用真实内容目录重算，
                    // 与旧骨架里 ER4-FAC-01 写入的字面值核对一致（见类注释），不是静默改变游戏数值。
                    BlueprintVersionRecord migrated = board.ToVersion(version.Version, version.CreatedAtPlaySeconds);
                    record.Versions[i] = migrated;
                }
            }
        }
    }
}
