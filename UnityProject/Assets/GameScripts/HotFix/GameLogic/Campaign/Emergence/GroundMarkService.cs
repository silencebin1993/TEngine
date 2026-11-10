using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG4-TER-02（FG19 FGR-EMG-153、154，FG19B V03，ADR-EMG-001 ⑩）：战痕写入家园格网。
    /// - 入口只有一个：家园内核发出的 <see cref="CombatEventKind.GroundMark"/> 玩法事件（每步 ≤ 64 条、顺延不丢），每条 O(本次标记覆盖的格数 + 已有战痕数 ≤ 64)，不按帧扫描。
    /// - 地面材质写进格网地形层（fg.TbGridTerrain 的 scorched / glazed / frozen / meltpit），走现有区块差异存档；“一处战痕”的索引记在 <see cref="WorldGenState.GroundMarks"/>。
    /// - 写格规则：焦土 / 玻化地面 / 冻土只盖空地（与其他非熔坑战痕后写覆盖先写）；熔坑只盖空地、可熔地面与其他战痕；有建筑 / 传送带 / 管线的格、资源点、悬崖、水源一律不改（不毁玩家的东西）。
    /// - 每地点 ≤ emg.terrain.marks_max 处；同一处（同种、圆心落在已有战痕里或在 emg.terrain.pit_gather 内）反复标记 = 并进原记录；满了并进最近的同种（没有同种并进最近的一处），外接圆不超过 <see cref="MaxRadius"/>。
    /// - 清除：建造模式“清除场地物”下工作单（SitePersistService 目标种类 g:），完工后这一处的格回到空地（熔坑 = 填平；同处的内核熔坑一并填平）。
    /// - 下次载入：格网差异还原地面材质；熔坑地形不可通行（寻路镜像 navCost 0）、不可建造；焦土圈写回内核（<see cref="SyncKernel"/>），圈里可燃斑块点燃阈值下调。
    /// 远征 / 室内地点不由家园格网承载（FG19 第 14 节 #15 核实：每次派遣重建、只有野外寿命），内核在那里不发这种事件。
    /// </summary>
    public static class GroundMarkService
    {
        private static readonly string[] TerrainIds = { null, "scorched", "glazed", "frozen", "meltpit" };
        private static readonly string[] HoverKeys = { null, "emg.mark.hover.scorched", "emg.mark.hover.glazed", "emg.mark.hover.frozen", "emg.mark.hover.meltpit" };
        /// <summary>合并后外接圆的半径上限（米）：防止满额后一处战痕无限长大。不是玩法数值。</summary>
        public const float MaxRadius = 8f;
        private const float MinRadius = 0.5f, MaxEventRadius = 4f;

        /// <summary>战痕集合每变一次 +1（内核焦土圈按它同步，O(1) 比较）。</summary>
        public static int Revision { get; private set; }
        /// <summary>自检读点：本会话合并次数（同一处 + 满额合并）、整处都没能写格（落在资源点 / 建筑上）而丢弃的次数、写进格网的格数。</summary>
        public static int Merged { get; private set; }
        public static int Skipped { get; private set; }
        public static int CellsWritten { get; private set; }

        public static int MaxMarks => Math.Max(1, GridContent.TuningInt("emg.terrain.marks_max"));
        private static float SameSpot => Math.Max(0.5f, GridContent.Tuning("emg.terrain.pit_gather"));

        public static void ResetSessionState()
        {
            Revision++;
            Merged = 0;
            Skipped = 0;
            CellsWritten = 0;
        }

        /// <summary>种类 → 地形层字节值（表里没有这一行返回 false：旧配置不写格）。</summary>
        public static bool TryCode(int kind, out byte code)
        {
            code = 0;
            return kind >= 1 && kind <= EmgGroundMarkConst.KindLast && GridContent.TryTerrainCode(TerrainIds[kind], out code);
        }

        private static bool IsMarkCode(byte code, out int kind)
        {
            for (kind = 1; kind <= EmgGroundMarkConst.KindLast; kind++)
            {
                if (TryCode(kind, out byte c) && c == code)
                {
                    return true;
                }
            }
            kind = 0;
            return false;
        }

        private static bool CanWrite(int kind, byte current)
        {
            if (current == 0)
            {
                return true; // 空地（R15：code 0 = 可建空地）
            }
            if (IsMarkCode(current, out int was))
            {
                return was != EmgGroundMarkConst.KindPit; // 熔坑只能被清除工作单填平，不被别的战痕盖掉
            }
            return kind == EmgGroundMarkConst.KindPit && GridContent.TerrainByCode(current)?.Meltable == 1; // 熔坑烧穿的是可熔地面
        }

        public static GroundMarkRecord Find(CampaignState state, int id)
        {
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r != null && r.Id == id)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>点 <paramref name="p"/> 落在哪处战痕里（熔坑优先，其次后写的）；没有返回 null。O(战痕数 ≤ 64)。</summary>
        public static GroundMarkRecord At(CampaignState state, double2 p)
        {
            GroundMarkRecord best = null;
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r == null || math.distance(p, new double2(r.X, r.Y)) > r.Radius + 0.5)
                {
                    continue;
                }
                bool pit = r.Kind == EmgGroundMarkConst.KindPit, bestPit = best != null && best.Kind == EmgGroundMarkConst.KindPit;
                if (best == null || (pit && !bestPit) || (pit == bestPit && r.Id > best.Id))
                {
                    best = r;
                }
            }
            return best;
        }

        // ── 写入（内核地面标记事件）────────────────────────────────────────

        /// <summary>
        /// 家园内核的一条地面标记事件：按格写进格网（<paramref name="map"/> 为空时只记索引不写格——自检夹具），返回落到的那处战痕（整处都写不了时 null）。
        /// </summary>
        public static GroundMarkRecord Apply(CampaignState state, HomeGridMap map, int kind, double2 pos, float radius)
        {
            if (state?.World == null || !TryCode(kind, out byte code) || !math.all(math.isfinite(pos)))
            {
                return null;
            }
            radius = math.clamp(float.IsFinite(radius) ? radius : MinRadius, MinRadius, MaxEventRadius);
            GroundMarkRecord[] list = state.World.GroundMarks ?? Array.Empty<GroundMarkRecord>();
            // 1. 同一处（同种，圆心落在已有战痕里或在“同一位置”合并半径内）：并进去
            GroundMarkRecord target = null;
            double best = double.MaxValue;
            foreach (GroundMarkRecord r in list)
            {
                double d = math.distance(pos, new double2(r.X, r.Y));
                if (r.Kind == kind && (d <= r.Radius || d <= SameSpot) && d < best)
                {
                    best = d;
                    target = r;
                }
            }
            // 2. 满额：并进最近的同种；没有同种并进最近的一处（它的种类不变，这次的格只写它能写的）
            if (target == null && list.Length >= MaxMarks)
            {
                double bestSame = double.MaxValue, bestAny = double.MaxValue;
                GroundMarkRecord same = null, any = null;
                foreach (GroundMarkRecord r in list)
                {
                    double d = math.distance(pos, new double2(r.X, r.Y));
                    if (r.Kind == kind && d < bestSame)
                    {
                        bestSame = d;
                        same = r;
                    }
                    if (d < bestAny)
                    {
                        bestAny = d;
                        any = r;
                    }
                }
                target = same ?? any;
            }
            if (target != null)
            {
                Enclose(target, pos, radius);
                target.Hits++;
                Merged++;
                if (TryCode(target.Kind, out byte tcode))
                {
                    WriteCells(map, target.Kind, tcode, pos, radius, target);
                }
                Revision++;
                return target;
            }
            var rec = new GroundMarkRecord { Id = Math.Max(1, state.World.NextGroundMarkId), Kind = kind, X = (float)pos.x, Y = (float)pos.y, Radius = radius };
            if (map != null && WriteCells(map, kind, code, pos, radius, rec) == 0)
            {
                Skipped++; // 整处都落在资源点 / 建筑 / 悬崖上：格网不变，不占名额
                return null;
            }
            state.World.NextGroundMarkId = rec.Id + 1;
            var grown = new GroundMarkRecord[list.Length + 1];
            Array.Copy(list, grown, list.Length);
            grown[list.Length] = rec;
            state.World.GroundMarks = grown;
            Revision++;
            return rec;
        }

        /// <summary>把记录的圆扩成同时盖住 (<paramref name="p"/>, <paramref name="r"/>) 的外接圆，半径不超过 <see cref="MaxRadius"/>（超了保持圆心、半径取上限）。</summary>
        private static void Enclose(GroundMarkRecord rec, double2 p, float r)
        {
            var c = new double2(rec.X, rec.Y);
            double d = math.distance(c, p);
            if (d + r <= rec.Radius)
            {
                return;
            }
            if (d + rec.Radius <= r)
            {
                rec.X = (float)p.x;
                rec.Y = (float)p.y;
                rec.Radius = Math.Min(MaxRadius, r);
                return;
            }
            double nr = (d + rec.Radius + r) / 2;
            if (nr > MaxRadius)
            {
                rec.Radius = MaxRadius;
                return;
            }
            double2 nc = c + (p - c) * ((nr - rec.Radius) / Math.Max(1e-6, d));
            rec.X = (float)nc.x;
            rec.Y = (float)nc.y;
            rec.Radius = (float)nr;
        }

        /// <summary>写格：(<paramref name="p"/>, <paramref name="r"/>) 里、且落在记录圆里的格（格中心 = 整数坐标，1 格 = 1 米），能写的改成 <paramref name="code"/>。返回写了几格。</summary>
        private static int WriteCells(HomeGridMap map, int kind, byte code, double2 p, float r, GroundMarkRecord rec)
        {
            if (map == null)
            {
                return 0;
            }
            int n = 0;
            int x0 = (int)Math.Floor(p.x - r), x1 = (int)Math.Ceiling(p.x + r), y0 = (int)Math.Floor(p.y - r), y1 = (int)Math.Ceiling(p.y + r);
            var rc = new double2(rec.X, rec.Y);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var cp = new double2(x, y);
                    if (math.distance(cp, p) > r || math.distance(cp, rc) > rec.Radius + 0.5)
                    {
                        continue;
                    }
                    var cell = new GridCell(x, y);
                    byte cur = map.GetTerrain(cell);
                    if (cur == code)
                    {
                        n++; // 同一格反复标记：已经是这种材质，不再改（不产生新的差异）
                        continue;
                    }
                    if (map.OccupantAt(cell) != null || map.GetBelt(cell) != 0 || map.GetPipe(cell) != 0 || !CanWrite(kind, cur))
                    {
                        continue;
                    }
                    map.SetTerrain(cell, code);
                    CellsWritten++;
                    n++;
                }
            }
            return n;
        }

        // ── 清除（FGR-EMG-154）──────────────────────────────────────────

        /// <summary>清除一处战痕：这一处圆里仍是它那种材质的格回到空地，记录删除。返回是否找到。</summary>
        public static bool Clear(CampaignState state, HomeGridMap map, int id)
        {
            GroundMarkRecord rec = Find(state, id);
            if (rec == null)
            {
                return false;
            }
            if (map != null && TryCode(rec.Kind, out byte code))
            {
                int x0 = (int)Math.Floor(rec.X - rec.Radius - 0.5f), x1 = (int)Math.Ceiling(rec.X + rec.Radius + 0.5f);
                int y0 = (int)Math.Floor(rec.Y - rec.Radius - 0.5f), y1 = (int)Math.Ceiling(rec.Y + rec.Radius + 0.5f);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var cell = new GridCell(x, y);
                        if (math.distance(new double2(x, y), new double2(rec.X, rec.Y)) <= rec.Radius + 0.5 && map.GetTerrain(cell) == code)
                        {
                            map.SetTerrain(cell, 0);
                        }
                    }
                }
            }
            state.World.GroundMarks = Array.FindAll(state.World.GroundMarks, r => r != null && r.Id != id);
            Revision++;
            return true;
        }

        /// <summary>内核熔坑被填平（“清除场地物”k: 工作单）：圆心落在这处熔坑战痕里的格网熔坑一并填平。</summary>
        public static void ClearPitAt(CampaignState state, HomeGridMap map, double2 pos)
        {
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r != null && r.Kind == EmgGroundMarkConst.KindPit && math.distance(pos, new double2(r.X, r.Y)) <= r.Radius + 0.5)
                {
                    Clear(state, map, r.Id);
                    return;
                }
            }
        }

        /// <summary>内核玻化层 / 冰斑块被清除（“清除场地物”p: 工作单）：圆心落在其中的玻化地面 / 冻土战痕一并清掉（同一处只下一张单）。</summary>
        public static void ClearSolidAt(CampaignState state, HomeGridMap map, double2 pos)
        {
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r != null && (r.Kind == EmgGroundMarkConst.KindGlazed || r.Kind == EmgGroundMarkConst.KindFrozen) && math.distance(pos, new double2(r.X, r.Y)) <= r.Radius + 0.5)
                {
                    Clear(state, map, r.Id);
                    return;
                }
            }
        }

        /// <summary>这处战痕是不是由内核熔坑代表（熔坑战痕圈里有内核熔坑：清除列表里只出那个熔坑，避免一个坑两张单）。</summary>
        public static bool HasKernelPit(CombatSite site, GroundMarkRecord r)
        {
            if (site == null || r == null || r.Kind != EmgGroundMarkConst.KindPit)
            {
                return false;
            }
            for (int i = 0; i < site.PitCount; i++)
            {
                if (math.distance(site.PitAt(i).Pos, new double2(r.X, r.Y)) <= r.Radius + 0.5)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>堵路检查基线（FGR-EMG-154）：所有熔坑战痕圈里、此刻仍是熔坑地形的格（填平后回到空地 = 假设它不在）。只在堵路检查发现到不了时调用，O(熔坑战痕数 × 圈面积)。</summary>
        public static void CollectPitCells(CampaignState state, HomeGridMap map, List<int2> into)
        {
            into.Clear();
            if (map == null || !TryCode(EmgGroundMarkConst.KindPit, out byte code))
            {
                return;
            }
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r == null || r.Kind != EmgGroundMarkConst.KindPit)
                {
                    continue;
                }
                int x0 = (int)Math.Floor(r.X - r.Radius - 0.5f), x1 = (int)Math.Ceiling(r.X + r.Radius + 0.5f);
                int y0 = (int)Math.Floor(r.Y - r.Radius - 0.5f), y1 = (int)Math.Ceiling(r.Y + r.Radius + 0.5f);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        if (math.distance(new double2(x, y), new double2(r.X, r.Y)) <= r.Radius + 0.5 && map.GetTerrain(new GridCell(x, y)) == code)
                        {
                            into.Add(new int2(x, y));
                        }
                    }
                }
            }
        }

        // ── 内核焦土圈、悬停 ────────────────────────────────────────────

        /// <summary>焦土战痕 → 内核焦土圈（x, y, 半径）。O(战痕数 ≤ 64)，只在 <see cref="Revision"/> 变了时调用。</summary>
        public static void CollectScorch(CampaignState state, List<float3> into)
        {
            into.Clear();
            foreach (GroundMarkRecord r in state?.World?.GroundMarks ?? Array.Empty<GroundMarkRecord>())
            {
                if (r != null && r.Kind == EmgGroundMarkConst.KindScorch)
                {
                    into.Add(new float3(r.X, r.Y, r.Radius));
                }
            }
        }

        public static string NameOf(int kind) =>
            kind >= 1 && kind <= EmgGroundMarkConst.KindLast && TryCode(kind, out byte code) && GridContent.TerrainByCode(code) is GameConfig.fg.GridTerrain t ? GameText.Get(t.NameKey) : string.Empty;

        /// <summary>悬停（B05）：“战痕 · 焦土”+ 是什么、有什么影响 + 怎么清除 / 已排单怎么取消。</summary>
        public static TooltipContent HoverContent(CampaignState state, int id)
        {
            GroundMarkRecord r = Find(state, id);
            if (r == null)
            {
                return null;
            }
            string body = r.Kind == EmgGroundMarkConst.KindScorch
                ? GameText.Format(HoverKeys[r.Kind], Math.Round(GridContent.Tuning("emg.terrain.scorch_ignite")))
                : GameText.Get(HoverKeys[Math.Max(1, Math.Min(EmgGroundMarkConst.KindLast, r.Kind))]);
            string target = SitePersistService.TargetOf(EmgPersistItem.KindMark, r.Id);
            string hint = SitePersistService.ActiveOrder(state, target) != null ? GameText.Get("emg.persist.hover_queued") : GameText.Get("emg.mark.hover.clear");
            return new TooltipContent { Title = GameText.Format("emg.mark.hover.title", NameOf(r.Kind)), Body = body + "\n" + hint };
        }
    }
}
