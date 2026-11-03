using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-MAT-02（FG19 FGR-EMG-087 / 211；FG19B §1.5、§7；ADR-EMG-001 ⑤ ⑩ ⑭ #1 #2 #13）：现有“像物质的区域”等价迁入斑块。
    /// - 读法驻留 / 拖尾（漏油、冷却液、液氮、酸液、粘合剂、蔓延、子母弹污染坑、燃迹）、反应残留燃烧区、云端喷淋池与油迹、管线液洼：斑块层开着（emg.stage ≥ 2 且物质表已写）时
    ///   生成为 Patches 里的“迁入斑块”（<see cref="EmgMatterConst.FlagLegacy"/>），物质码按“StatusMask → 物质码”映射（FG19B §1.5），映射不出（减速 / 电击）或斑块层满了照旧落区域。
    /// - 迁入斑块带着区域载荷，按 <see cref="ZoneTick"/>（StepZones 原循环体）结算——迁移前后 FGJ-M6 火墙、管线泄漏遇火烧产线、FGJ-M10 结果一致；
    ///   不走物质通用接触 / 衰减 / 合并、不给弹体沾染；参与：空间哈希（evt.enter / leave、渲染；只并进物质掩码、不占单位接触名额；不进物质斑块悬停——沿用原区域的悬停归属，液洼 = 液洼读数，规则改写后并入）、液洼整片反应与喷淋池冲洗“遇到的物质”、快照段 17。
    /// - 设施受击聚合（ADR ⑤）：建筑 / 传送带 / 管线（血量在热更层）不在内核遍历——物质斑块按 fg.TbMatter structDps、燃烧的液洼残留按键聚合进
    ///   <see cref="CombatData.EmgStructPending"/>，每步最多发布 64 条到 <see cref="CombatData.EmgStructOut"/>（超出顺延；待发布表封顶 256 条，没有消费者的地点丢最旧的并计数）；炮塔血量在内核，只由内核接触结算（热更层跳过炮塔），热更层只处理取走的条目。
    /// - 读 ≤ 16 版快照：旧液洼 / 喷淋池 / 油迹 / 驻留类读法区域在读档收尾按同一映射转成迁入斑块（补发 Id），映射不了的保留为旧区域直到到期。
    /// 全部逐区域 / 逐斑块逻辑在 Main/Sim（Burst），热更层不遍历斑块。
    /// </summary>
    public static partial class CombatLogic
    {
        internal static bool IsLegacy(in CombatZone z) => z.Matter != 0 && (z.PFlags & EmgMatterConst.FlagLegacy) != 0;

        /// <summary>
        /// “StatusMask → 物质码”映射（FG19B §1.5）：按 油 → 冷却液（浸湿：冷却液 / 蔓延 / 冲击波）→ 液氮（冻结位）→ 酸 → 粘合剂 → 污染 → 明火 → 扬尘 → 水 的优先级，
        /// 取区域标签里第一个能对上某物质沾染位的；冻结位（热更层写的 <see cref="CombatData.FrozenBit"/>）映射液氮。都对不上 = 0（保留为区域：减速网、电击……）。只认位，不认内容 ID。
        /// </summary>
        internal static byte MatterOfMask(ref CombatData d, uint mask)
        {
            mask &= ~CombatConst.StatusBitZoneSlow;
            if (mask == 0u || !d.EmgMatters.IsCreated)
            {
                return 0;
            }
            for (int k = 0; k < 9; k++)
            {
                byte m = k switch
                {
                    0 => EmgMatterConst.Oil,
                    1 => EmgMatterConst.Coolant,
                    2 => EmgMatterConst.Cryo,
                    3 => EmgMatterConst.Acid,
                    4 => EmgMatterConst.Polymer,
                    5 => EmgMatterConst.Sludge,
                    6 => EmgMatterConst.Fire,
                    7 => EmgMatterConst.Dust,
                    _ => EmgMatterConst.Water,
                };
                EmgMatter mt = d.EmgMatters[m];
                if (!mt.Valid)
                {
                    continue;
                }
                bool hit = mt.CoatBit >= 0 && mt.CoatBit < 32 && (mask & (1u << mt.CoatBit)) != 0u;
                if (m == EmgMatterConst.Cryo && d.FrozenBit != 0u && (mask & d.FrozenBit) != 0u)
                {
                    hit = true;
                }
                if (hit)
                {
                    return m;
                }
            }
            return 0;
        }

        /// <summary>一块斑块对“遇到的标签”的贡献：迁入斑块 = 区域载荷的标签；物质斑块 = 物质沾染位（−1 不沾染 = 0）。</summary>
        internal static uint PatchTagMask(ref CombatData d, in CombatZone z)
        {
            if ((z.PFlags & EmgMatterConst.FlagLegacy) != 0)
            {
                return z.StatusMask & ~CombatConst.StatusBitZoneSlow;
            }
            EmgMatter m = MatterOf(ref d, z.Matter);
            return m.CoatBit >= 0 && m.CoatBit < 31 ? 1u << m.CoatBit : 0u;
        }

        /// <summary>
        /// 把一块区域落成迁入斑块：返回 Patches 下标，−1 = 没迁（层没开 / 映射不出物质 / 斑块总上限或世界源保留额满——调用方照旧落区域，不丢）。
        /// 计时（Born / Until / NextTick / 节拍）原样保留；补发斑块 Id；不占每步生成额度（区域本来就是即时生成，顺延会改时序）。
        /// </summary>
        internal static int EmgPlaceLegacy(ref CombatData d, in CombatZone zone, byte matter = 0, bool world = false)
        {
            if (!EmgPatchesOn(ref d) || zone.EmgProgRef > 0 || zone.EmgSub != 0)
            {
                return -1;
            }
            byte m = matter != 0 && MatterOf(ref d, matter).Valid ? matter : MatterOfMask(ref d, zone.StatusMask);
            if (m == 0)
            {
                return -1;
            }
            int free = -1, worldN = 0, combat = 0;
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone p = d.Patches[i];
                if (p.Matter == 0)
                {
                    if (free < 0)
                    {
                        free = i;
                    }
                }
                else if (PatchWorld(p))
                {
                    worldN++;
                }
                else
                {
                    combat++;
                }
            }
            if (world ? worldN >= PatchReserveOf(ref d) : combat >= PatchCapOf(ref d) - PatchReserveOf(ref d))
            {
                EmgScalars full = d.EmgS[0];
                full.LegacyKeptZone++; // 斑块层满：照旧落区域（计数可见）
                d.EmgS[0] = full;
                return -1;
            }
            EmgMatter mt = MatterOf(ref d, m);
            CombatZone z = zone;
            z.Matter = m;
            z.Layer = mt.Layer;
            z.PFlags = (byte)(EmgMatterConst.FlagLegacy | (world ? EmgMatterConst.FlagWorld : 0));
            z.Amount = 1f;
            z.Temp = mt.TempInit;
            z.Conc = 1f;
            z.R0 = z.Radius;
            z.Owners = new int4(z.Owner > 0 ? z.Owner : 0, 0, 0, 0);
            EmgScalars es = d.EmgS[0];
            z.EmgId = es.NextPatchId++;
            es.PatchesBorn++;
            es.PatchLive++;
            es.LegacyBorn++;
            es.HashDirty = 1;
            d.EmgS[0] = es;
            if (free >= 0)
            {
                d.Patches[free] = z;
                return free;
            }
            d.Patches.Add(z);
            return d.Patches.Length - 1;
        }

        /// <summary>某种区域此刻有几块迁入斑块（只在区域总数接近上限时调用；<paramref name="pool"/> = 喷淋池与油迹共用上限）。</summary>
        private static int LegacyOfKind(ref CombatData d, byte kind, bool pool)
        {
            int n = 0;
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone p = d.Patches[i];
                if (IsLegacy(p) && (p.Kind == kind || (pool && (p.Kind == CombatConst.ZoneKindPool || p.Kind == CombatConst.ZoneKindSlick))))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>最近一次 SpawnZone 生成的那块（在 Zones 或 Patches 里）。</summary>
        internal static bool TryGetLastZone(ref CombatData d, out CombatZone z)
        {
            int at = d.LastZoneAt;
            if (at >= 0 && at < d.Zones.Length)
            {
                z = d.Zones[at];
                return true;
            }
            int p = at < 0 && at != int.MinValue ? -1 - at : -1;
            if (p >= 0 && p < d.Patches.Length && d.Patches[p].Matter != 0)
            {
                z = d.Patches[p];
                return true;
            }
            z = default;
            return false;
        }

        internal static void SetLastZone(ref CombatData d, in CombatZone z)
        {
            int at = d.LastZoneAt;
            if (at >= 0 && at < d.Zones.Length)
            {
                d.Zones[at] = z;
                return;
            }
            int p = at < 0 && at != int.MinValue ? -1 - at : -1;
            if (p >= 0 && p < d.Patches.Length && d.Patches[p].Matter != 0)
            {
                d.Patches[p] = z;
            }
        }

        /// <summary>迁入斑块的推进（StepZones 末尾）：到期 → 墓碑；增长；到节拍按区域载荷结算（<see cref="ZoneTick"/>）。O(斑块)，只在有斑块时。</summary>
        private static void StepLegacyPatches(ref CombatData d, float dt, double now)
        {
            // 按斑块 Id 升序结算（不按槽位）：墓碑复用让连续跑与读档后的槽位布局不同，按槽位会让读档续跑的结算顺序变（FGT-EMG-040 / B10）
            var order = LegacyOrder(ref d);
            for (int o = 0; o < order.Length; o++)
            {
                int i = (int)(order[o] & 0xFFFFFFFFL);
                CombatZone z = d.Patches[i];
                if (!IsLegacy(z))
                {
                    continue;
                }
                if (z.Until <= now)
                {
                    KillPatch(ref d, i);
                    EmgScalars es = d.EmgS[0];
                    es.PatchesExpired++;
                    d.EmgS[0] = es;
                    continue;
                }
                if (z.Growth != 0f)
                {
                    z.Radius = math.max(0f, z.Radius + z.Growth * dt);
                    if (z.Growth > 0f)
                    {
                        EmgScalars es = d.EmgS[0];
                        es.HashDirty = 1;
                        d.EmgS[0] = es;
                    }
                    if ((z.PFlags & EmgMatterConst.FlagSpread) != 0 && z.Radius >= z.R0 - 1e-4f)
                    {
                        z.Radius = z.R0; // EMG2-RECL-01（蔓延）：扩到目标半径就停（读法 ZoneGrowth 的增长不停）
                        z.Growth = 0f;
                    }
                }
                ZoneTick(ref d, ref z, -1, i, now);
                if (d.Patches[i].Matter != 0)
                {
                    d.Patches[i] = z;
                }
            }
            order.Dispose();
        }

        /// <summary>活着的迁入斑块按 (Id, 槽位) 升序的列表（调用方 Dispose）。O(斑块 log 斑块)。</summary>
        internal static NativeList<long> LegacyOrder(ref CombatData d)
        {
            var order = new NativeList<long>(math.max(1, d.Patches.Length), Allocator.Temp);
            for (int i = 0; i < d.Patches.Length; i++)
            {
                if (IsLegacy(d.Patches[i]))
                {
                    order.Add(((long)d.Patches[i].EmgId << 32) | (uint)i);
                }
            }
            order.Sort();
            return order;
        }

        /// <summary>液洼整片反应“遇到的标签”里来自斑块的那部分：重叠的迁入斑块（不含还没反应的液洼 / 油迹）按区域标签，物质斑块按沾染位；不含自己。O(斑块)。</summary>
        private static uint MetFromPatches(ref CombatData d, in CombatZone leak, int selfPatch, double now)
        {
            uint met = 0u;
            for (int i = 0; i < d.Patches.Length; i++)
            {
                if (i == selfPatch)
                {
                    continue;
                }
                CombatZone o = d.Patches[i];
                if (o.Matter == 0 || o.Until <= now)
                {
                    continue;
                }
                if (IsLegacy(o) && (o.Kind == CombatConst.ZoneKindLeak || o.Kind == CombatConst.ZoneKindSlick) && o.Phase == CombatConst.LeakPhasePuddle)
                {
                    continue; // 两摊还没反应的液体挨着不算（与区域口径一致）
                }
                if (math.distance(o.Pos, leak.Pos) <= o.Radius + leak.Radius)
                {
                    met |= PatchTagMask(ref d, o);
                }
            }
            return met;
        }

        /// <summary>迁入斑块整片反应成残留：物质码换成残留标签对应的物质（燃烧 = 明火），不再是常驻世界源（按残留时长到期，外圈显示剩余）。</summary>
        private static void LegacyReacted(ref CombatData d, ref CombatZone z)
        {
            byte m = MatterOfMask(ref d, z.StatusMask);
            if (m != 0)
            {
                z.Matter = m;
                z.Layer = MatterOf(ref d, m).Layer;
            }
            z.PFlags = (byte)(z.PFlags & ~EmgMatterConst.FlagWorld);
            z.R0 = z.Radius;
            EmgScalars es = d.EmgS[0];
            es.HashDirty = 1;
            d.EmgS[0] = es;
        }

        /// <summary>自检入口：按喷洒车 / 液氮泵车同一条路径落一块喷淋池（状态效果按标签表取）。</summary>
        internal static bool SpawnPoolForTests(ref CombatData d, int ownerId, double2 pos, float radius, float seconds, float dps, uint mask, float statusSeconds, float chill)
        {
            FxOf(ref d, mask, out float sdps, out float sslow, out float svuln);
            return SpawnPool(ref d, d.SlotOf(ownerId), pos, radius, seconds, dps, mask, statusSeconds, sdps, sslow, svuln, chill);
        }

        /// <summary>自检入口：按油污爬行者同一条路径落一摊油迹。</summary>
        internal static bool SpawnSlickForTests(ref CombatData d, int ownerId, double2 pos, float radius, float seconds, uint mask, float statusSeconds) =>
            SpawnSlick(ref d, d.SlotOf(ownerId), pos, radius, seconds, mask, statusSeconds);

        // ─────────────────────────────── 设施受击聚合（ADR ⑤）───────────────────────────────

        /// <summary>记一条设施受击（同键合并：伤害相加、位置 / 半径取最新、标志或起来）。O(待发布条目)。</summary>
        internal static void AddStructHit(ref CombatData d, int key, double2 pos, float radius, float damage, byte matter, byte flags)
        {
            if (key == 0 || !(radius > 0f))
            {
                return;
            }
            for (int i = 0; i < d.EmgStructPending.Length; i++)
            {
                EmgStructHit h = d.EmgStructPending[i];
                if (h.Key != key)
                {
                    continue;
                }
                h.Damage += math.max(0f, damage);
                h.Pos = pos;
                h.Radius = radius;
                h.Matter = matter;
                h.Flags |= flags;
                d.EmgStructPending[i] = h;
                return;
            }
            if (d.EmgStructPending.Length >= EmgMatterConst.StructHitsPendingCap)
            {
                // 没有消费者的地点（只有家园取走）：已发布表满后待发布表封顶，丢最旧的一条并计数（斑块 Id 只增不减，按键合并挡不住新斑块）
                d.EmgStructPending.RemoveAt(0);
                EmgScalars es = d.EmgS[0];
                es.StructHitsDropped++;
                d.EmgS[0] = es;
            }
            d.EmgStructPending.Add(new EmgStructHit { Key = key, Pos = pos, Radius = radius, Damage = math.max(0f, damage), Matter = matter, Flags = flags });
        }

        /// <summary>每步一次：待发布条目按先进先出发布最多 64 条（已发布表满 256 条 = 没有消费者时不发布，条目留在待发布表里按键合并；待发布表也封顶 256 条，见 <see cref="AddStructHit"/>）。</summary>
        internal static void FlushStructHits(ref CombatData d)
        {
            int room = EmgMatterConst.StructHitsOutCap - d.EmgStructOut.Length;
            int k = math.min(d.EmgStructPending.Length, math.min(EmgMatterConst.StructHitsPerStep, room));
            if (k <= 0)
            {
                if (d.EmgStructPending.Length > 0)
                {
                    EmgScalars es = d.EmgS[0];
                    es.StructHitsDeferred++;
                    d.EmgS[0] = es;
                }
                return;
            }
            for (int i = 0; i < k; i++)
            {
                d.EmgStructOut.Add(d.EmgStructPending[i]);
            }
            d.EmgStructPending.RemoveRange(0, k);
            if (d.EmgStructPending.Length > 0)
            {
                EmgScalars es = d.EmgS[0];
                es.StructHitsDeferred++;
                d.EmgS[0] = es;
            }
        }

        // ─────────────────────────────── 读 ≤ 16 版快照（FGR-EMG-211 / ADR ⑩ 版本 17 缺省）───────────────────────────────

        /// <summary>
        /// 读档收尾（格式 ≤ 16）：液洼（Kind 2）、喷淋池（3）、油迹（4）与驻留 / 残留类读法区域（Kind 0、无程序、外观不是脉冲 / 减速网）按“StatusMask → 物质码”转成迁入斑块，
        /// 补发 Id，计时与载荷原样；映射不了的（减速网、电击、带程序的布区 / 子区……）保留为旧区域直到到期。斑块层没开时什么都不做。返回转了几块。O(区域 + 斑块)。
        /// </summary>
        internal static int MigrateLegacyZones(ref CombatData d)
        {
            if (!EmgPatchesOn(ref d) || d.Zones.Length == 0)
            {
                return 0;
            }
            int write = 0, moved = 0;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                CombatZone zn = d.Zones[z];
                bool candidate = zn.EmgProgRef == 0 && zn.EmgSub == 0
                                 && (zn.Kind == CombatConst.ZoneKindLeak || zn.Kind == CombatConst.ZoneKindPool || zn.Kind == CombatConst.ZoneKindSlick
                                     || (zn.Kind == CombatConst.ZoneKindReading && zn.Look != CombatZoneLook.Pulse && zn.Look != CombatZoneLook.Web));
                bool world = zn.Kind == CombatConst.ZoneKindLeak && zn.Phase == CombatConst.LeakPhasePuddle;
                if (candidate && EmgPlaceLegacy(ref d, zn, 0, world) >= 0)
                {
                    moved++;
                    continue;
                }
                d.Zones[write++] = zn;
            }
            d.Zones.ResizeUninitialized(write);
            if (moved > 0)
            {
                EmgScalars es = d.EmgS[0];
                es.LegacyMigrated += moved;
                d.EmgS[0] = es;
            }
            return moved;
        }
    }
}
