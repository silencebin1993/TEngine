using System.Collections.Generic;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG2-PERSIST-01：一件长期留存物（残骸或固态斑块）的只读摘要。</summary>
    public struct EmgPersistItem
    {
        public const byte KindWreck = 1, KindPatch = 2;
        public byte Kind;
        public int Id;
        public double2 Pos;
        public float Radius;
        /// <summary>斑块物质码（残骸 = 0）。</summary>
        public byte Matter;
        /// <summary>固态且阻路（fg.TbMatter solid = 2）。</summary>
        public bool Blocks;
        public double Born;
    }

    /// <summary>
    /// EMG2-PERSIST-01（FG19 FGR-EMG-150、154）：长期留存物给热更层的门面。全部 O(残骸 + 斑块)，只在玩家下单、悬停、每 eco.softlock.reach_check_seconds 的堵路检查与存档时调用，不按帧。
    /// </summary>
    public sealed partial class CombatKernel
    {
        /// <summary>本地点是否长期留存（家园内核）。</summary>
        public bool PersistSite => _d.Config.EmgPersist != 0;

        /// <summary>活着的阻路斑块集合的签名（没有 = 0）；每步算准，变了才把覆盖格推进寻路镜像。</summary>
        public uint BlockSig => _d.EmgS[0].BlockSig;

        /// <summary>长期留存崩解 / 工作单清除 / 固态挡弹的可靠计数（进快照）。</summary>
        public (int Capped, int Cleared, int Absorbs) PersistCounts
        {
            get
            {
                EmgScalars es = _d.EmgS[0];
                return (es.PersistCapped, es.PersistCleared, es.SolidAbsorbs);
            }
        }

        /// <summary>残骸脚下起火的累计次数（进快照）。</summary>
        public int WreckFires => _d.EmgS[0].WreckFires;

        /// <summary>这块残骸此刻在燃烧（悬停）。</summary>
        public bool WreckBurning(in EmgWreck w) => CombatLogic.EmgWreckBurning(ref _d, w);

        /// <summary>此刻的残骸数 / 长期留存口径的固态斑块数（离家报告、调试层）。</summary>
        public (int Wrecks, int Solids) PersistTally()
        {
            int solids = 0;
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                solids += z.Matter != 0 && CombatLogic.MatterOf(ref _d, z.Matter).Solid != 0 ? 1 : 0;
            }
            return (_d.Wrecks.Length, solids);
        }

        /// <summary>把全部残骸与固态斑块写进 <paramref name="into"/>（按种类、再按表内顺序，确定性）。</summary>
        public void CollectPersist(List<EmgPersistItem> into)
        {
            into.Clear();
            for (int i = 0; i < _d.Wrecks.Length; i++)
            {
                EmgWreck w = _d.Wrecks[i];
                if (w.Hp > 0f)
                {
                    into.Add(new EmgPersistItem { Kind = EmgPersistItem.KindWreck, Id = w.Id, Pos = w.Pos, Radius = w.Radius, Born = w.Born });
                }
            }
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                if (z.Matter != 0 && CombatLogic.MatterOf(ref _d, z.Matter).Solid != 0)
                {
                    into.Add(new EmgPersistItem
                    {
                        Kind = EmgPersistItem.KindPatch, Id = z.EmgId, Pos = z.Pos, Radius = z.Radius, Matter = z.Matter, Blocks = CombatLogic.PatchBlocks(ref _d, z), Born = z.Born,
                    });
                }
            }
        }

        /// <summary>按稳定 Id 取一件长期留存物（工作单走到目标、核对目标还在）。</summary>
        public bool TryGetPersist(byte kind, int id, out EmgPersistItem item)
        {
            item = default;
            if (kind == EmgPersistItem.KindWreck)
            {
                for (int i = 0; i < _d.Wrecks.Length; i++)
                {
                    EmgWreck w = _d.Wrecks[i];
                    if (w.Id == id)
                    {
                        item = new EmgPersistItem { Kind = kind, Id = id, Pos = w.Pos, Radius = w.Radius, Born = w.Born };
                        return w.Hp > 0f;
                    }
                }
                return false;
            }
            int p = PatchIndexOf(id);
            if (p < 0 || p >= _d.Patches.Length)
            {
                return false;
            }
            CombatZone z = _d.Patches[p];
            if (z.Matter == 0 || CombatLogic.MatterOf(ref _d, z.Matter).Solid == 0)
            {
                return false;
            }
            item = new EmgPersistItem { Kind = kind, Id = id, Pos = z.Pos, Radius = z.Radius, Matter = z.Matter, Blocks = CombatLogic.PatchBlocks(ref _d, z), Born = z.Born };
            return true;
        }

        /// <summary>工作单完工：移除一件长期留存物（计 PersistCleared；不产生碎屑、不发耗尽计数）。目标已经没了返回 false。</summary>
        public bool ClearPersist(byte kind, int id)
        {
            if (!TryGetPersist(kind, id, out _))
            {
                return false;
            }
            CombatLogic.EmgPersistClear(ref _d, kind, id);
            Touch();
            return true;
        }

        /// <summary>长期留存物集合的签名（种类、Id，残骸另含所在整格：它会被场推动、滑行；空 = 0）。堵路检查发现变了才重新泛洪。</summary>
        public uint PersistSignature()
        {
            uint h = 0u;
            for (int i = 0; i < _d.Wrecks.Length; i++)
            {
                EmgWreck w = _d.Wrecks[i];
                h = (h ^ (uint)w.Id) * 16777619u + 1u;
                h = (h ^ (uint)(int)math.floor(w.Pos.x) ^ ((uint)(int)math.floor(w.Pos.y) * 19349663u)) * 16777619u + 1u;
            }
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                if (z.Matter != 0 && CombatLogic.MatterOf(ref _d, z.Matter).Solid != 0)
                {
                    h = (h ^ (uint)z.EmgId ^ 0x5bd1e995u) * 16777619u + 1u;
                }
            }
            return h;
        }

        /// <summary>阻路斑块的圆（x, y, 半径）：推进寻路镜像用，只在 <see cref="BlockSig"/> 变化时调用。</summary>
        public void CollectBlockCircles(List<float3> into)
        {
            into.Clear();
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                if (z.Matter != 0 && CombatLogic.PatchBlocks(ref _d, z))
                {
                    into.Add(new float3((float)z.Pos.x, (float)z.Pos.y, z.Radius));
                }
            }
        }
    }
}
