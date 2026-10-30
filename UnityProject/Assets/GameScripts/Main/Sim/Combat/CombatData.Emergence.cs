using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG1-KRN-01：涌现容器的建 / 放与每单位段随单位的追加、搬移、截断（字段声明在 CombatData.cs，同一结构体的 partial）。</summary>
    public partial struct CombatData
    {
        private void EmgCreate(int capacity)
        {
            EmgS = new NativeArray<EmgScalars>(1, Allocator.Persistent);
            EmgS[0] = EmgScalars.Initial;
            EmgPrograms = new NativeList<EmgProgram>(4, Allocator.Persistent);
            EmgNodes = new NativeList<EmgNode>(8, Allocator.Persistent);
            EmgInstrs = new NativeList<EmgInstr>(16, Allocator.Persistent);
            FlashHead = new NativeList<Unity.Mathematics.int4>(4, Allocator.Persistent);
            FlashMap = new NativeList<int>(16, Allocator.Persistent);
            EmgBus = new NativeList<EmgEvent>(16, Allocator.Persistent);
            Wrecks = new NativeList<EmgWreck>(4, Allocator.Persistent);
            EProfile = new NativeList<short>(capacity, Allocator.Persistent);
            EmgUnit = new NativeList<EmgUnitState>(capacity, Allocator.Persistent);
            EmgStats = new NativeArray<EmgBusStats>(1, Allocator.Persistent);
            EmgKindEnq = new NativeArray<long>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgLastByKind = new NativeArray<EmgEvent>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgAct = new NativeList<int>(0, Allocator.Persistent);
            EmgListen = 0u;
            EmgCtx = EmgCtx.None;
            EmgPending = new NativeList<EmgSpawn>(4, Allocator.Persistent);
            EmgSubForms = new NativeArray<EmgSubForm>(6, Allocator.Persistent);
            for (int k = 1; k <= 5; k++)
            {
                EmgSubForms[k] = EmgSubForm.Default(k);
            }
            EmgHash = EmgHash.Create();
            EmgProfiles = new NativeList<EmgProfile>(EmgConst.ProfileSlots, Allocator.Persistent);
            EmgProfiles.Add(EmgProfile.Steel(1f));                  // 弹体
            EmgProfiles.Add(EmgProfile.Steel(4f));                  // 重型弹体
            EmgProfiles.Add(EmgProfile.Steel(0.5f, 0.5f, 0.1f, 0.5f)); // 无人机
            EmgProfiles.Add(EmgProfile.Steel(6f, 0.9f, 0.05f, 0.9f, 0)); // 哨戒桩（插地不位移）
            EmgProfiles.Add(EmgProfile.Steel(0.2f, 0.9f, 0f, 0.9f)); // 破片
            EmgProfiles.Add(EmgProfile.Steel(6f));                  // 机器（轮式）
            EmgProfiles.Add(EmgProfile.Steel(0f));                  // 敌人（8 × 半径²）
            EmgProfiles.Add(EmgProfile.Steel(200f, 0.9f, 0.1f, 0.9f, 0)); // 首领
            EmgProfiles.Add(EmgProfile.Steel(20f, 0.9f, 0.1f, 0.9f, 0));  // 炮塔
            EmgProfiles.Add(EmgProfile.Steel(20f, 0.9f, 0.1f, 0.9f, 0));  // 结构
            EmgProfiles.Add(EmgProfile.Steel(0f));                  // 残骸（继承原单位）
            EmgEntParams = new NativeArray<EmgEntityParams>(1, Allocator.Persistent);
            EmgEntParams[0] = EmgEntityParams.Default;
            EmgMerge = new NativeList<EmgMergeReq>(4, Allocator.Persistent);
            EmgMergeCont = 0;
            EmgRing = new NativeArray<EmgChainRec>(EmgConst.ChainRing, Allocator.Persistent);
            EmgRingHead = new NativeArray<int>(2, Allocator.Persistent);
            EmgRuleKey = new NativeArray<int>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgRuleCount = new NativeArray<int>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgPortCount = new NativeArray<long>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgRuleFeed = new NativeArray<EmgRuleFeed>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgDirty = new NativeList<short>(8, Allocator.Persistent);
            EmgDirtyMark = new NativeArray<byte>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgEraDirty = new NativeList<int>(8, Allocator.Persistent);
            EmgFriendly = new NativeList<int2>(8, Allocator.Persistent);
        }

        private void EmgDispose()
        {
            EmgS.Dispose();
            EmgPrograms.Dispose();
            EmgNodes.Dispose();
            EmgInstrs.Dispose();
            FlashHead.Dispose();
            FlashMap.Dispose();
            EmgBus.Dispose();
            Wrecks.Dispose();
            EProfile.Dispose();
            EmgUnit.Dispose();
            EmgStats.Dispose();
            EmgKindEnq.Dispose();
            EmgLastByKind.Dispose();
            EmgAct.Dispose();
            EmgPending.Dispose();
            EmgSubForms.Dispose();
            EmgHash.Dispose();
            EmgProfiles.Dispose();
            EmgEntParams.Dispose();
            EmgMerge.Dispose();
            EmgRing.Dispose();
            EmgRingHead.Dispose();
            EmgRuleKey.Dispose();
            EmgRuleCount.Dispose();
            EmgPortCount.Dispose();
            EmgRuleFeed.Dispose();
            EmgDirty.Dispose();
            EmgDirtyMark.Dispose();
            EmgEraDirty.Dispose();
            EmgFriendly.Dispose();
        }

        private void EmgAppend()
        {
            EProfile.Add(-1);
            EmgUnit.Add(default);
        }

        private void EmgMoveSlot(int from, int to)
        {
            EProfile[to] = EProfile[from];
            EmgUnit[to] = EmgUnit[from];
        }

        private void EmgTruncate(int length)
        {
            EProfile.ResizeUninitialized(length);
            EmgUnit.ResizeUninitialized(length);
        }

        /// <summary>清掉随战斗走的涌现状态（总线、残骸、发号器）。程序表与武器表一样是“表”，另行覆盖。</summary>
        private void EmgClearState()
        {
            EmgBus.Clear();
            Wrecks.Clear();
            EmgPending.Clear();
            EmgS[0] = EmgScalars.Initial; // 游标归 0、EmgHash 置脏（EmgScalars.Initial）
            EmgResetStats();
            EmgCtx = EmgCtx.None;
            EmgClearChain();
        }

        /// <summary>EMG1-CHAIN-01：清因果链环、可靠计数、进给与脏列表（清战斗状态时；读档换一份新数据时天然为空 / 由快照填计数）。</summary>
        internal void EmgClearChain()
        {
            EmgRingHead[0] = 0;
            EmgRingHead[1] = 0;
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                EmgRuleKey[i] = 0;
                EmgRuleCount[i] = 0;
                EmgRuleFeed[i] = default;
                EmgDirtyMark[i] = 0;
            }
            for (int k = 0; k < EmgPortCount.Length; k++)
            {
                EmgPortCount[k] = 0;
            }
            EmgDirty.Clear();
            EmgEraDirty.Clear();
            EmgEraDirtyPublished = 0;
            EmgFriendly.Clear();
            EmgFriendlyPublished = 0;
        }

        /// <summary>EMG1-BUS-01：清调试计数（清战斗状态时；读档换一份新数据时天然为 0）。</summary>
        internal void EmgResetStats()
        {
            EmgStats[0] = default;
            for (int k = 0; k < EmgKindEnq.Length; k++)
            {
                EmgKindEnq[k] = 0;
                EmgLastByKind[k] = default;
            }
        }

        /// <summary>未处理的事件条数。</summary>
        public int EmgBusPending => EmgBus.Length - EmgS[0].BusHead;
    }
}
