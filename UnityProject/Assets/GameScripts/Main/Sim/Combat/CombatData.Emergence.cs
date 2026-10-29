using Unity.Collections;

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
            EmgBus = new NativeList<EmgEvent>(16, Allocator.Persistent);
            Wrecks = new NativeList<EmgWreck>(4, Allocator.Persistent);
            EProfile = new NativeList<short>(capacity, Allocator.Persistent);
            EmgUnit = new NativeList<EmgUnitState>(capacity, Allocator.Persistent);
        }

        private void EmgDispose()
        {
            EmgS.Dispose();
            EmgPrograms.Dispose();
            EmgNodes.Dispose();
            EmgInstrs.Dispose();
            EmgBus.Dispose();
            Wrecks.Dispose();
            EProfile.Dispose();
            EmgUnit.Dispose();
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
            EmgS[0] = EmgScalars.Initial;
        }

        /// <summary>未处理的事件条数。</summary>
        public int EmgBusPending => EmgBus.Length - EmgS[0].BusHead;
    }
}
