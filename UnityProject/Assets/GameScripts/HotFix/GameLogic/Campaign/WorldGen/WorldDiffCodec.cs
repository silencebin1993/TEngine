using System;
using System.Collections.Generic;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>区块里一个被修改过的格子（相对“按种子重新生成的结果”）。</summary>
    public readonly struct ChunkCellDiff
    {
        public const byte TerrainBit = 1;
        public const byte PollutionBit = 2;

        public readonly ushort Index;
        public readonly byte Mask;
        public readonly byte Terrain;
        public readonly byte Pollution;

        public ChunkCellDiff(ushort index, byte mask, byte terrain, byte pollution)
        {
            Index = index;
            Mask = mask;
            Terrain = terrain;
            Pollution = pollution;
        }
    }

    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-060、062）：<see cref="ChunkDiffRecord.DiffPayload"/> 的编码。只记被修改的格子，所以存档体积随
    /// **被修改的面积**增长，与探索面积无关。
    ///
    /// 格式 v1：<c>"d1:" + Base64( [区块边长 u8][条数 u16 LE] + 条数 × [格子序号 u16 LE][层掩码 u8][地形 u8][污染 u8] )</c>。
    /// 层掩码：1 = 地形层、2 = 污染层（其它层——建筑、传送带、管线、地面物——各有自己的记录，不进区块差异）。
    /// 解码做完整校验（前缀、长度、序号越界、掩码、污染 0～3、同一格重复）；不合法返回 false 和原因，读档据此判定存档损坏。
    /// </summary>
    public static class WorldDiffCodec
    {
        public const string Prefix = "d1:";
        private const int EntryBytes = 5;

        public static string Encode(int chunkSize, IReadOnlyList<ChunkCellDiff> cells)
        {
            if (chunkSize < 1 || chunkSize > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }
            if (cells.Count > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(cells));
            }
            var bytes = new byte[3 + cells.Count * EntryBytes];
            bytes[0] = (byte)chunkSize;
            bytes[1] = (byte)(cells.Count & 0xFF);
            bytes[2] = (byte)(cells.Count >> 8);
            int o = 3;
            for (int i = 0; i < cells.Count; i++)
            {
                ChunkCellDiff c = cells[i];
                bytes[o++] = (byte)(c.Index & 0xFF);
                bytes[o++] = (byte)(c.Index >> 8);
                bytes[o++] = c.Mask;
                bytes[o++] = c.Terrain;
                bytes[o++] = c.Pollution;
            }
            return Prefix + Convert.ToBase64String(bytes);
        }

        public static bool TryDecode(string payload, int expectedChunkSize, List<ChunkCellDiff> into, out string error)
        {
            into?.Clear();
            error = null;
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
            {
                error = "区块差异编码前缀不是 d1:";
                return false;
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload.Substring(Prefix.Length));
            }
            catch (FormatException)
            {
                error = "区块差异不是合法的 Base64";
                return false;
            }
            if (bytes.Length < 3)
            {
                error = "区块差异太短";
                return false;
            }
            if (bytes[0] != expectedChunkSize)
            {
                error = $"区块差异的区块边长 {bytes[0]} 与当前 {expectedChunkSize} 不符";
                return false;
            }
            int count = bytes[1] | (bytes[2] << 8);
            if (bytes.Length != 3 + count * EntryBytes)
            {
                error = $"区块差异长度 {bytes.Length} 与条数 {count} 不符";
                return false;
            }
            int cellsPerChunk = expectedChunkSize * expectedChunkSize;
            var seen = new HashSet<int>();
            int o = 3;
            for (int i = 0; i < count; i++)
            {
                int index = bytes[o] | (bytes[o + 1] << 8);
                byte mask = bytes[o + 2];
                byte terrain = bytes[o + 3];
                byte pollution = bytes[o + 4];
                o += EntryBytes;
                if (index >= cellsPerChunk)
                {
                    error = $"区块差异第 {i} 条格子序号 {index} 越界";
                    return false;
                }
                if (mask == 0 || (mask & ~(ChunkCellDiff.TerrainBit | ChunkCellDiff.PollutionBit)) != 0)
                {
                    error = $"区块差异第 {i} 条层掩码 {mask} 非法";
                    return false;
                }
                if (pollution > 3)
                {
                    error = $"区块差异第 {i} 条污染等级 {pollution} 超出 0～3";
                    return false;
                }
                if (!seen.Add(index))
                {
                    error = $"区块差异第 {i} 条格子 {index} 重复";
                    return false;
                }
                into?.Add(new ChunkCellDiff((ushort)index, mask, terrain, pollution));
            }
            return true;
        }
    }
}
