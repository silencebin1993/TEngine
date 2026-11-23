using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// FG15-SYS-02（DEBT-FG15SYS01-04）：存档校验和用的 SHA-256（FIPS 180-4），Burst 直接调用，任意线程可用（存档在线程池里写）。
    /// 结果与 System.Security.Cryptography.SHA256 逐位相同（FgSaveRobustSelfCheck K 段对照断言）；只换实现、不换算法，存档格式与校验和不变。
    /// Editor Mono 下托管 SHA256 约 37 MB/s（2.3 MB 存档读档校验和 61.8 ms），这里是原生代码。
    /// </summary>
    [BurstCompile]
    public static unsafe class BurstSha256
    {
        public struct State
        {
            public fixed uint H[8];
            public fixed byte Buf[64];
            public int BufLen;
            public long Total;
        }

        private static readonly uint[] K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        public static void Init(ref State s)
        {
            s.H[0] = 0x6a09e667; s.H[1] = 0xbb67ae85; s.H[2] = 0x3c6ef372; s.H[3] = 0xa54ff53a;
            s.H[4] = 0x510e527f; s.H[5] = 0x9b05688c; s.H[6] = 0x1f83d9ab; s.H[7] = 0x5be0cd19;
            s.BufLen = 0;
            s.Total = 0;
        }

        /// <summary>喂入 <paramref name="data"/>[<paramref name="offset"/>, +<paramref name="count"/>)。</summary>
        public static void Update(ref State s, byte[] data, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }
            if (data == null || offset < 0 || count > data.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            fixed (byte* p = data)
            {
                UpdateCore(ref s, p + offset, count);
            }
        }

        /// <summary>收尾，返回 32 字节摘要。</summary>
        public static byte[] Final(ref State s)
        {
            var digest = new byte[32];
            fixed (byte* o = digest)
            {
                FinalCore(ref s, o);
            }
            return digest;
        }

        [BurstCompile(CompileSynchronously = true)]
        private static void UpdateCore(ref State s, byte* data, int len)
        {
            fixed (uint* h = s.H)
            fixed (byte* buf = s.Buf)
            {
                s.Total += len;
                int i = 0;
                if (s.BufLen > 0)
                {
                    int take = math.min(64 - s.BufLen, len);
                    UnsafeUtility.MemCpy(buf + s.BufLen, data, take);
                    s.BufLen += take;
                    i = take;
                    if (s.BufLen < 64)
                    {
                        return;
                    }
                    Compress(h, buf);
                    s.BufLen = 0;
                }
                for (; i + 64 <= len; i += 64)
                {
                    Compress(h, data + i);
                }
                if (i < len)
                {
                    UnsafeUtility.MemCpy(buf, data + i, len - i);
                    s.BufLen = len - i;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private static void FinalCore(ref State s, byte* digest)
        {
            fixed (uint* h = s.H)
            fixed (byte* buf = s.Buf)
            {
                long bits = s.Total * 8;
                buf[s.BufLen++] = 0x80;
                if (s.BufLen > 56)
                {
                    while (s.BufLen < 64)
                    {
                        buf[s.BufLen++] = 0;
                    }
                    Compress(h, buf);
                    s.BufLen = 0;
                }
                while (s.BufLen < 56)
                {
                    buf[s.BufLen++] = 0;
                }
                for (int i = 0; i < 8; i++)
                {
                    buf[56 + i] = (byte)(bits >> (56 - 8 * i));
                }
                Compress(h, buf);
                for (int i = 0; i < 8; i++)
                {
                    digest[4 * i] = (byte)(h[i] >> 24);
                    digest[4 * i + 1] = (byte)(h[i] >> 16);
                    digest[4 * i + 2] = (byte)(h[i] >> 8);
                    digest[4 * i + 3] = (byte)h[i];
                }
            }
        }

        private static void Compress(uint* h, byte* block)
        {
            uint* w = stackalloc uint[64];
            for (int t = 0; t < 16; t++)
            {
                w[t] = ((uint)block[4 * t] << 24) | ((uint)block[4 * t + 1] << 16) | ((uint)block[4 * t + 2] << 8) | block[4 * t + 3];
            }
            for (int t = 16; t < 64; t++)
            {
                uint x = w[t - 15], y = w[t - 2];
                uint s0 = math.ror(x, 7) ^ math.ror(x, 18) ^ (x >> 3);
                uint s1 = math.ror(y, 17) ^ math.ror(y, 19) ^ (y >> 10);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }
            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (int t = 0; t < 64; t++)
            {
                uint S1 = math.ror(e, 6) ^ math.ror(e, 11) ^ math.ror(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = hh + S1 + ch + K[t] + w[t];
                uint S0 = math.ror(a, 2) ^ math.ror(a, 13) ^ math.ror(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + maj;
                hh = g;
                g = f;
                f = e;
                e = d + t1;
                d = c;
                c = b;
                b = a;
                a = t1 + t2;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }
    }
}
