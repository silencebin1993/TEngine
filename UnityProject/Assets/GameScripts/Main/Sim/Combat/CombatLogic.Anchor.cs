using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG4-FUSE-02（FG19 FGR-EMG-044；FG19A 第 4.2 节落点表；ADR-EMG-001 ②；FG-GAP-205 / DEBT-EMG3FUSE01-01）：EMG3-FUSE-01 之外的落点钩子。
    /// 每个钩子先判程序带接线（<see cref="EmgProgAnchored"/>，没有接线的程序 O(1) 返回，金标准与空场景不变），逐单位 / 逐斑块的扫描只在带接线的程序上、只在 Main/Sim。
    /// 规则只看 A 的写端口种类（EmgAnchorConst），不认任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>
        /// 沿途排放（FG19A 第 4.2 节“沿途排放的 mat.*（读 evt.fire）”，单次上限 4）：出手（evt.fire）时从炮口外到主目标按长度均分 n 个段点
        /// （n = 路径长 ÷ 段距，1～4；段距 = 写端声明的 param2，燃迹 / 拖尾 1.5 米，FG19A 第 2 节第 33 / 34 行“每 1.5 米 0.3 份”；没声明的退回 2 × 斑块半径），
        /// 每个段点排一块真斑块并作为 A 的落点（段点附近的敌对单位当落点对象，腐焰弹头“给段上单位注入腐蚀度”）。格斗 / 力场等非弹道载体 = 在主目标处排 1 块。
        /// 同一序号读 chs.moving（sense）而出手者静止时不铺线，n 段的合计份量在终结点（主目标）堆成一块。物质不认敌我（FG19）：段点压在谁身上都照排。
        /// 修复第 3 轮②：解析不到终结点（主目标）= 这一发不排，不退回出手者脚下，调用方也不再按出手位置补排。恒返回 true（这条写端已由沿途排放处理）。
        /// </summary>
        internal static bool EmgTrailEmit(ref CombatData d, in EmgInstr x, in EmgEvent e, in EmgCtx host, float a, float b, int first, int last)
        {
            int ts = host.MainTarget != 0 ? d.SlotOf(host.MainTarget) : e.OtherKind == EmgEntKind.Unit && e.Other != 0 ? d.SlotOf(e.Other) : -1;
            if (ts < 0 || !d.IsAlive(ts))
            {
                return true;
            }
            bool path = host.Carrier == (byte)CombatCarrier.Projectile || host.Carrier == (byte)CombatCarrier.Summon;
            double2 from = e.Pos, end = d.Pos[ts], to = end;
            if (path)
            {
                // 段点从炮口外算起（出手者半径 + 斑块半径），不把排放物铺在出手者自己脚下
                double2 dir = math.normalizesafe(to - from);
                int os = host.Owner >= 0 && host.Owner < d.Count ? host.Owner : -1;
                from += dir * ((os >= 0 ? d.Radius[os] : 0f) + a);
                path = math.dot(to - from, dir) > 0.0;
            }
            float step = x.Param2 > 0f ? x.Param2 : math.max(1f, 2f * a);
            int n = path && CombatAblation.TrailSegments.Data == 0 ? math.clamp((int)math.floor(math.distance(from, to) / step), 1, 4) : 1;
            bool anchored = EmgProgAnchored(ref d, host.Prog);
            if (!path || EmgStillSensed(ref d, host.Owner, first, last, x.Ordinal))
            {
                // FG19A 第 2 节第 33 / 34 行（燃迹 / 拖尾读 chs.moving sense）：本机静止开火时不铺线，同样份量在终结点堆成一块；非弹道载体（或目标贴脸）同样只在终结点排
                EmgInstr y = x;
                y.Amount = (x.Amount > 0f ? x.Amount : 1f) * n;
                if (EmgEmitPatch(ref d, y, end, a, b, host.Owner, e.Chain, e.Depth))
                {
                    NoteReading(ref d, CombatConst.ReadingFeedZone, end, host.Owner); // 拖尾退役过渡读法后，沿途排放照样给“区域展开”读法反馈（音效 / 弹字，FG2-FW-04）
                }
                if (anchored)
                {
                    EmgAnchorAt(ref d, EmgAnchorConst.Trail, host, end, EmgNearestHostile(ref d, end, math.max(1f, a), host.Faction, -1), x.Ordinal);
                }
                return true;
            }
            bool fed = false;
            for (int i = 0; i < n; i++)
            {
                double2 p = math.lerp(from, to, (i + 0.5) / n);
                if (EmgEmitPatch(ref d, x, p, a, b, host.Owner, e.Chain, e.Depth) && !fed)
                {
                    fed = true;
                    NoteReading(ref d, CombatConst.ReadingFeedZone, p, host.Owner); // 一发只报一次
                }
                if (anchored)
                {
                    EmgAnchorAt(ref d, EmgAnchorConst.Trail, host, p, EmgNearestHostile(ref d, p, math.max(1f, a), host.Faction, -1), x.Ordinal);
                }
            }
            return true;
        }

        /// <summary>同一序号有 chs.moving sense 读端（[<paramref name="first"/>, <paramref name="end"/>) 内，按端口 + 角色，不看固件 ID）且出手者此刻没在移动。O(节点指令数)，只在沿途排放时。</summary>
        private static bool EmgStillSensed(ref CombatData d, int owner, int first, int end, byte ordinal)
        {
            if (owner < 0 || owner >= d.Count || (ChsMask(ref d, owner) & EmgTraitConst.ChsMoving) != 0)
            {
                return false;
            }
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ordinal && r.Role == EmgConst.RoleSense && r.Port == EmgConst.PortChsMoving)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary><paramref name="p"/> 处 <paramref name="r"/> 米内最近的、与 <paramref name="faction"/> 敌对的活单位（落点对象）；没有 = −1。O(单位)，只在带接线的程序上调用。</summary>
        internal static int EmgNearestHostile(ref CombatData d, double2 p, float r, byte faction, int skip)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int u = 0; u < d.Count; u++)
            {
                if (u == skip || !d.IsAlive(u) || d.Faction[u] == faction || !d.Has(u, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double q = math.distance(d.Pos[u], p) - d.Radius[u];
                if (q <= r && q < bd)
                {
                    (bd, best) = (q, u);
                }
            }
            return best;
        }

        /// <summary>
        /// 场的完整落点集（FG19A 第 4.2 节 fld.* 行）：场生成点之外，场内受力 / 会被扫过的单位（生成那一刻在场半径内、与场敌对的单位；推斥环按最终半径）各是一个落点。
        /// 偏折场的落点是被它反射的弹体（<see cref="EmgReflectAnchor"/>），不在这里。<paramref name="skip"/> = 生成点已当落点对象的单位（不重复执行）。上限随 emg.fuse.anchor_max。
        /// </summary>
        internal static void EmgFieldAnchors(ref CombatData d, in EmgCtx host, in EmgInstr x, double2 center, int skip)
        {
            if (!EmgProgAnchored(ref d, host.Prog) || x.Port == EmgFieldConst.PortDeflect)
            {
                return;
            }
            float r = x.Amount > 0f ? x.Amount : 2f;
            if (host.Carrier == (byte)CombatCarrier.Summon)
            {
                r = math.min(r, EmgFieldConst.DroneFieldRadius);
            }
            for (int u = 0; u < d.Count; u++)
            {
                if (u == skip || u == host.Owner || !d.IsAlive(u) || d.Faction[u] == host.Faction || math.distance(d.Pos[u], center) > r + d.Radius[u])
                {
                    continue;
                }
                EmgAnchorAt(ref d, EmgAnchorConst.Field, host, d.Pos[u], u, x.Ordinal);
            }
        }

        /// <summary>
        /// 注能的完整落点集（FG19A 第 4.2 节 en.* 行“被注入能量的单位 / 斑块”）：注入点 <paramref name="radius"/> 米内被这次注能加热 / 充电的斑块中心各是一个落点
        /// （与 <see cref="EmgInjectPatches"/> 同一筛选：热 = 非定温、非世界斑块；电 = 导电 ≥ 0.3；磁 / 腐蚀不注斑块）。O(斑块)，只在带接线的程序上。
        /// </summary>
        internal static void EmgInjectSpots(ref CombatData d, in EmgCtx host, ushort port, double2 pos, float radius, byte src)
        {
            if (!EmgProgAnchored(ref d, host.Prog) || (port != EmgConst.PortHeat && port != EmgConst.PortElec) || !EmgPatchesOn(ref d))
            {
                return;
            }
            radius = math.max(0.5f, radius);
            // 先收齐注入那一刻已有的被加热 / 充电斑块，再逐个执行 B：B 在落点上排出的新斑块（放进空位或追加到末尾）不再被当成 A 的落点，也不占落点名额
            var spots = new FixedList512Bytes<double2>();
            for (int j = 0; j < d.Patches.Length && spots.Length < spots.Capacity; j++)
            {
                CombatZone o = d.Patches[j];
                if (!PatchAlive(o) || math.distance(o.Pos, pos) > o.Radius + radius)
                {
                    continue;
                }
                EmgMatter m = MatterOf(ref d, o.Matter);
                bool takes = port == EmgConst.PortHeat ? m.FixedTemp == 0 && !PatchWorld(o) : m.Conductive >= EmgRuleConst.DefaultConductMinStrength;
                if (takes)
                {
                    spots.Add(o.Pos); // shortcut: 最多收 31 个（远大于 emg.fuse.anchor_max 缺省 4），超出的不再计截断数；anchor_max 调到 31 以上时改成整表扫描后计截断
                }
            }
            for (int q = 0; q < spots.Length; q++)
            {
                EmgAnchorAt(ref d, EmgAnchorConst.Inject, host, spots[q], -1, src);
            }
        }

        /// <summary>
        /// 被偏折场反射的弹体（FG19A 第 4.2 节 fld.* 行；第 4.3 节湮灭镜像“处决效果刷写给被反射的弹体”）：场的主人这把武器的程序带场落点接线时，给反射回去的弹体记
        /// <see cref="EmgProjFlags.AnchorReflect"/>，它之后打中单位时在命中点执行 B（<see cref="EmgAnchorHit"/>）。程序由场主人的武器取（场不另存程序号，快照不变）。
        /// </summary>
        internal static void EmgReflectAnchor(ref CombatData d, ref CombatProjectile pr, int ownerId)
        {
            int os = d.SlotOf(ownerId);
            int w = os >= 0 ? d.Weapon[os] : -1;
            if (w >= 0 && w < d.Weapons.Length && EmgProgAnchored(ref d, d.Weapons[w].Program))
            {
                pr.Flags |= EmgProjFlags.AnchorReflect;
            }
        }

        /// <summary>
        /// 命中结算里的接线落点（EmgOnDamage 调用，伤害结算之前）：①追射弹（<see cref="EmgProjFlags.AnchorFollow"/>，改打的第二目标）——B 刷写给这枚追射弹，在它的命中点执行；
        /// ②被偏折场反射的弹体（<see cref="EmgProjFlags.AnchorReflect"/>）——以场主人的武器程序为宿主在命中点执行。没有这两个记号 O(1) 返回。
        /// </summary>
        internal static void EmgAnchorHit(ref CombatData d, int t, in EmgCtx c0)
        {
            if ((c0.PFlags & (EmgProjFlags.AnchorFollow | EmgProjFlags.AnchorReflect)) == 0)
            {
                return;
            }
            if ((c0.PFlags & EmgProjFlags.AnchorFollow) != 0)
            {
                EmgAnchorAt(ref d, EmgAnchorConst.Follow, c0, d.Pos[t], t);
                return;
            }
            int os = d.SlotOf(c0.OwnerId);
            int w = os >= 0 && d.IsAlive(os) ? d.Weapon[os] : -1;
            if (w < 0 || w >= d.Weapons.Length)
            {
                return;
            }
            EmgCtx h = c0;
            h.Prog = d.Weapons[w].Program;
            h.Node = 0;
            h.Owner = os;
            h.Weapon = w;
            h.Faction = d.Faction[os];
            h.Carrier = (byte)d.Weapons[w].Reading.Carrier;
            h.PFlags = EmgProjFlags.None;
            EmgAnchorAt(ref d, EmgAnchorConst.Field, h, d.Pos[t], t);
        }

        /// <summary>
        /// 追射接线（FG19A 第 4.2 节 ent.projectile 行，单次上限 1）：程序在这条追射（序号 <paramref name="ordinal"/>）后接了线，就改打原目标 5 米内另一个敌对目标，并给补射记
        /// <see cref="EmgProjFlags.AnchorFollow"/>；没有第二个目标 → 打原目标、不记（B 与母节点同端口同目标，按 FGR-EMG-017 重复无效，预览 emg.compile.anchor_follow_solo 写原因）。
        /// 返回改打的目标 Id（0 = 不改）。
        /// </summary>
        internal static int EmgFollowRetarget(ref CombatData d, in EmgCtx host, int targetId, byte ordinal)
        {
            // 补射记录与补射弹固定挂根节点（Node = 0），命中时按根节点找 B：追射在子节点时不改打（照常打原目标、不带 B），免得“改打了第二目标、B 却不执行”。
            // shortcut: 子节点里的追射接线不生效（编译期写 emg.compile.anchor_follow_child、不复制 B，这里只是防御），需要时把补射记录的 Node 改成追射所在节点（ADR-EMG-006 第 7 条，DEBT-EMG4FUSE02-05）
            if (host.Node != 0 || !EmgProgAnchored(ref d, host.Prog) || !EmgHasAnchored(ref d, host.Prog, EmgAnchorConst.Follow, ordinal))
            {
                return 0;
            }
            int t = d.SlotOf(targetId);
            if (t < 0)
            {
                return 0;
            }
            int other = EmgNearestHostile(ref d, d.Pos[t], EmgConst.ChildSeekRadius, host.Faction, t);
            return other >= 0 ? d.Id[other] : 0;
        }

        /// <summary>
        /// 落点上的破片写端（ent.fragment spawn）：同一序号有处决行时破片随处决结算（EmgTargetWrite），这里不重复；只有破片时在落点向四周放 round(幅度) 片（链深到 3 不再放）。
        /// </summary>
        private static void EmgAnchorFragments(ref CombatData d, in EmgInstr x, in EmgCtx host, double2 pos, int first, int end)
        {
            for (int j = first; j < end; j++)
            {
                if (d.EmgInstrs[j].Ordinal == x.Ordinal && d.EmgInstrs[j].Port == EmgRuleConst.PortIntegrity && (d.EmgInstrs[j].Flags & EmgInstrFlags.Anchored) != 0)
                {
                    return;
                }
            }
            int frags = math.clamp((int)math.round(x.Amount), 0, 8);
            EmgEntityParams ep = d.EmgEntParams[0];
            for (int q = 0; q < frags && host.Depth < EmgConst.MaxDepth; q++)
            {
                float ang = 2f * math.PI * q / math.max(1, frags);
                EmgSpawnFragment(ref d, pos, new float2(math.cos(ang), math.sin(ang)), EmgIdOf(ref d, host.Owner), (CombatFaction)host.Faction, (byte)(host.Depth + 1), host.Chain, ep.FragDamage, ep.FragSpeed, ep.FragRange);
            }
        }

        /// <summary>程序尾段有 <paramref name="kind"/> 种类、复制自 B = <paramref name="src"/> + 2 的 Anchored 行（[A, 接线, B] 的序号相邻）。O(尾段行数)。</summary>
        private static bool EmgHasAnchored(ref CombatData d, int prog, byte kind, byte src)
        {
            EmgProgram p = d.EmgPrograms[prog];
            if (p.NodeCount < 1 || p.FirstNode + p.NodeCount > d.EmgNodes.Length)
            {
                return false;
            }
            EmgNode root = d.EmgNodes[p.FirstNode], last = d.EmgNodes[p.FirstNode + p.NodeCount - 1];
            int end = math.min(root.FirstInstr + p.InstrTotal, d.EmgInstrs.Length);
            for (int i = last.FirstInstr + last.InstrCount; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.AnchorKind == kind && (x.Flags & EmgInstrFlags.Anchored) != 0 && x.Ordinal == src + 2)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 只给性能消融用（FgEmergenceFuzzSelfCheck H 段，BINGAMES_FUZZ_ABLATE=1，用完复位）：TrailSegments ≠ 0 时沿途排放只铺 1 个段点，量 EMG4-FUSE-02“1～4 段点”多出的开销。
    /// 缺省 0，行为与哈希不变。用 SharedStatic：Burst 作业不能读普通可写静态字段（BC1040，会让整个内核退回托管执行）。
    /// </summary>
    public static class CombatAblation
    {
        private struct TrailKey
        {
        }

        public static readonly Unity.Burst.SharedStatic<int> TrailSegments = Unity.Burst.SharedStatic<int>.GetOrCreate<TrailKey>();
    }
}
