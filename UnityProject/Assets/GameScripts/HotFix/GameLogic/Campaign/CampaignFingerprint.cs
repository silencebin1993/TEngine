using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// ER1-SAVE-02：ERD-SAV-004 确定性回放指纹。"固定输入回放比较资源账、机器状态、事件账本、
    /// 区域状态和编译签名"——本类把这些字段折成一个稳定 SHA256 摘要，同种子同输入流跑出的两份
    /// <see cref="CampaignState"/> 必须给出相同指纹。
    ///
    /// 故意排除的字段与原因：
    /// <list type="bullet">
    /// <item><see cref="CampaignState.CampaignId"/>：每局新建都是新 GUID，两次独立复现天然不同，
    /// 不是"结算是否一致"的一部分。</item>
    /// <item>写档时间戳（<c>WrittenAtUtc</c>，属于 <see cref="CampaignSaveService"/> 的 envelope，
    /// 不在 <see cref="CampaignState"/> 本体）：真实世界时间，ERD-SAV-004 原文明确"真实时间...
    /// 不得影响结算"，因此也不该被指纹比较进来。</item>
    /// <item><see cref="CampaignState.PlaySeconds"/>：游戏内经过时长，同输入流两次运行如果驱动方式
    /// 有任何真实帧时间差异会导致该值漂移，但这不代表规则结算本身不一致——同理排除。</item>
    /// <item><see cref="CampaignState.RandomSeed"/>：这是指纹要复现的"输入"之一，不是"输出"，
    /// 混进指纹只会让"同种子应指纹一致"这句话变成套套逻辑。</item>
    /// </list>
    ///
    /// 调用前会执行 <see cref="CampaignState.NormalizeForSave"/>：Dictionary 迭代顺序/未排序数组
    /// 不得影响指纹（ERD-SAV-004 同一条原文），复用 ER1-SAVE-01 已实现的稳定排序，不重新发明。
    /// </summary>
    public static class CampaignFingerprint
    {
        [Serializable]
        private sealed class FingerprintPayload
        {
            public int Scrap;
            public int TechData;
            public float PowerCapacity;
            public float PowerDemand;
            public float SignalBandwidth;
            public float SignalExposure;
            public string[] CompletedObjectiveIds;
            public string[] UnlockedContentIds;
            public MachineRecord[] MachineRecords;
            public BlueprintRecord[] BlueprintRecords;
            public BuildingRecord[] BuildingRecords;
            public RegionRecord[] RegionRecords;
            public EventLedgerEntry[] EventLedger;
            public ObjectiveRecord[] ObjectiveRecords;
            public int ControlledLogicId;
            /// <summary>ER3-STO-01：地面物是仓储守恒的一部分，读档重放后必须逐字节一致
            /// （同 <see cref="MachineRecords"/> 等其余存量字段的处理方式）。</summary>
            public GroundItemRecord[] GroundItems;
            /// <summary>ER3-WRK-01：WorkOrder 现在带 Progress/Duration 真实进度，读档重放后
            /// 必须逐字节一致（同一套"存量字段必须参与指纹"的规则，此前骨架阶段没有真正的进度
            /// 可比较，未纳入；现在纳入。</summary>
            public WorkOrderRecord[] WorkOrders;
            /// <summary>ER4-PRIM-03：基元芯片实例账是仓储守恒的一部分，读档重放后必须逐字节一致
            /// （同 <see cref="GroundItems"/> 的处理方式）。</summary>
            public PrimitiveChipRecord[] PrimitiveChips;
            /// <summary>ER4-PRIM-04：合成队列带真实 Progress/材料预留，读档重放后必须逐字节一致
            /// （同 <see cref="WorkOrders"/>/<see cref="FactoryQueueItemRecord"/> 先例）。</summary>
            public CraftQueueItemRecord[] CraftQueues;
            /// <summary>ER4-PRIM-05：低威胁残骸靶的 HP/再生状态，读档重放后必须逐字节一致
            /// （同 <see cref="GroundItems"/>/<see cref="PrimitiveChips"/> 先例）。</summary>
            public CombatTargetRecord[] CombatTargets;
            /// <summary>ER5-REGION-01：破碎都市敌方/节点、关键任务物状态，读档重放后必须逐字节一致
            /// （同 <see cref="CombatTargets"/>/<see cref="GroundItems"/> 先例）。</summary>
            public RegionEnemyRecord[] RegionEnemies;
            public RegionQuestItemRecord[] RegionQuestItems;
        }

        /// <summary>计算稳定指纹（十六进制 SHA256 字符串）。<paramref name="state"/> 为空时返回空串
        /// （不是异常——调用方在"没有活动战役"的场景下应把空串当成"无从比较"处理，而不是崩溃）。
        /// 注意：本方法会调用 <see cref="CampaignState.NormalizeForSave"/>，对传入实例的数组顺序
        /// 有原地排序副作用（与 <see cref="CampaignSaveService.Save"/> 同一约定），调用方若需要保留
        /// 原始顺序应自行在调用前克隆。</summary>
        public static string Compute(CampaignState state)
        {
            if (state == null)
            {
                return string.Empty;
            }

            state.NormalizeForSave();

            var payload = new FingerprintPayload
            {
                Scrap = state.Scrap,
                TechData = state.TechData,
                PowerCapacity = state.PowerCapacity,
                PowerDemand = state.PowerDemand,
                SignalBandwidth = state.SignalBandwidth,
                SignalExposure = state.SignalExposure,
                CompletedObjectiveIds = state.CompletedObjectiveIds,
                UnlockedContentIds = state.UnlockedContentIds,
                MachineRecords = state.MachineRecords,
                BlueprintRecords = state.BlueprintRecords,
                BuildingRecords = state.BuildingRecords,
                RegionRecords = state.RegionRecords,
                EventLedger = state.EventLedger,
                ObjectiveRecords = state.ObjectiveRecords,
                ControlledLogicId = state.ControlHandoff?.ControlledLogicId ?? 0,
                GroundItems = state.GroundItems,
                WorkOrders = state.WorkOrders,
                PrimitiveChips = state.PrimitiveChips,
                CraftQueues = state.CraftQueues,
                CombatTargets = state.CombatTargets,
                RegionEnemies = state.RegionEnemies,
                RegionQuestItems = state.RegionQuestItems,
            };

            string json = JsonUtility.ToJson(payload);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
