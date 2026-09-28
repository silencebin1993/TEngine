using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Signal
{
    /// <summary>
    /// FG1-SIG-07（FG01 FGR-SIG-050）：信号覆盖网络的连通计算（AOT + Burst）。
    ///
    /// 覆盖源 = 圆（x, y = 圆心格坐标，z = 半径格数）。前 <c>rootCount</c> 个是根（归还核心 / 远征接入点），永远连通；
    /// 其余覆盖源只有**圆心落在某个已连通覆盖源的覆盖里**才连通（“中继要立在已连通的覆盖里”），广度优先，O(覆盖源²)。
    /// 覆盖源里有机器上的中继模块（随机器移动），所以放在 AOT：热更层只调用本类的非泛型静态方法（HybridCLR 下值类型泛型实例化只放 AOT），
    /// 不直接调度作业。结果与调用时机、帧率、是否被观察无关（纯函数）。
    /// </summary>
    public static class SignalNetKernel
    {
        /// <summary>连通计算调用次数（自检核对“覆盖源没变时不重算”）。</summary>
        public static int ConnectCount { get; private set; }

        /// <summary>
        /// 计算连通：<paramref name="connectedOut"/>[i] = 1 表示第 i 个覆盖源与根连通；<paramref name="parentOut"/>[i] = 把它接进网络的那个覆盖源下标（根与不连通的为 -1）。
        /// 两个输出数组长度至少为 <paramref name="sources"/>.Count。
        /// </summary>
        public static void Connect(IReadOnlyList<float3> sources, int rootCount, byte[] connectedOut, int[] parentOut)
        {
            ConnectCount++;
            int n = sources?.Count ?? 0;
            if (n == 0)
            {
                return;
            }
            var src = new NativeArray<float3>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var connected = new NativeArray<byte>(n, Allocator.TempJob);
            var parent = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var queue = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    src[i] = sources[i];
                }
                var job = new SignalNetConnectJob
                {
                    Sources = src,
                    RootCount = math.clamp(rootCount, 0, n),
                    Connected = connected,
                    Parent = parent,
                    Queue = queue,
                };
                job.Run();
                for (int i = 0; i < n; i++)
                {
                    if (connectedOut != null && i < connectedOut.Length)
                    {
                        connectedOut[i] = connected[i];
                    }
                    if (parentOut != null && i < parentOut.Length)
                    {
                        parentOut[i] = parent[i];
                    }
                }
            }
            finally
            {
                src.Dispose();
                connected.Dispose();
                parent.Dispose();
                queue.Dispose();
            }
        }

        /// <summary>托管实现（同一规则，给自检做三路对照：Burst 作业结果必须与它逐项相同）。</summary>
        public static void ConnectManaged(IReadOnlyList<float3> sources, int rootCount, byte[] connectedOut, int[] parentOut)
        {
            int n = sources?.Count ?? 0;
            var queue = new int[n];
            int head = 0;
            int tail = 0;
            for (int i = 0; i < n; i++)
            {
                connectedOut[i] = 0;
                parentOut[i] = -1;
            }
            for (int i = 0; i < math.min(rootCount, n); i++)
            {
                connectedOut[i] = 1;
                queue[tail++] = i;
            }
            while (head < tail)
            {
                int a = queue[head++];
                float3 sa = sources[a];
                double r2 = (double)sa.z * sa.z;
                for (int j = 0; j < n; j++)
                {
                    if (connectedOut[j] != 0)
                    {
                        continue;
                    }
                    float3 sb = sources[j];
                    double dx = (double)sb.x - sa.x;
                    double dy = (double)sb.y - sa.y;
                    if (dx * dx + dy * dy <= r2)
                    {
                        connectedOut[j] = 1;
                        parentOut[j] = a;
                        queue[tail++] = j;
                    }
                }
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct SignalNetConnectJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Sources;
        public int RootCount;
        public NativeArray<byte> Connected;
        public NativeArray<int> Parent;
        public NativeArray<int> Queue;

        public void Execute()
        {
            int n = Sources.Length;
            int head = 0;
            int tail = 0;
            for (int i = 0; i < n; i++)
            {
                Connected[i] = 0;
                Parent[i] = -1;
            }
            for (int i = 0; i < RootCount; i++)
            {
                Connected[i] = 1;
                Queue[tail++] = i;
            }
            // 广度优先：规范顺序（按下标），结果只取决于输入。
            while (head < tail)
            {
                int a = Queue[head++];
                float3 sa = Sources[a];
                double r2 = (double)sa.z * sa.z;
                for (int j = 0; j < n; j++)
                {
                    if (Connected[j] != 0)
                    {
                        continue;
                    }
                    float3 sb = Sources[j];
                    double dx = (double)sb.x - sa.x;
                    double dy = (double)sb.y - sa.y;
                    if (dx * dx + dy * dy <= r2)
                    {
                        Connected[j] = 1;
                        Parent[j] = a;
                        Queue[tail++] = j;
                    }
                }
            }
        }
    }
}
