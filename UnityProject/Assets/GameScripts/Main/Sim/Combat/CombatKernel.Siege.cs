using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032）：战斗内核的攻城接口。热更层（GameLogic.Campaign.Combat.CombatSite 门面）在突袭展开时开剧场、写职能表与集结点、
    /// 给结构单位标类别与占地、给突袭单位标职能与队伍；内核每步维护流场、按职能选目标、破墙、撤退。逐单位 / 逐格的 O(N) 全部在 AOT（Burst）。
    /// </summary>
    public sealed partial class CombatKernel
    {
        private static readonly System.Diagnostics.Stopwatch SiegeWatch = new System.Diagnostics.Stopwatch();

        /// <summary>上一步攻城剧场维护的耗时（毫秒）与本内核的最大值；上一步发生了格子变化时的耗时（流场增量更新的证据）。</summary>
        public double LastSiegeMs { get; private set; }
        public double MaxSiegeMs { get; private set; }
        public double LastSiegeChangeMs { get; private set; }
        public double MaxSiegeChangeMs { get; private set; }
        public double MaxSiegeResetMs { get; private set; }

        private void MaintainSiege()
        {
            if (!_d.SiegeState.IsCreated)
            {
                return;
            }
            bool active = _d.SiegeState.Cfg[0].Enabled != 0 || _d.SiegeState.Rect[0].z > 0;
            if (!active)
            {
                LastSiegeMs = 0;
                return;
            }
            long resetsBefore = _d.SiegeState.Stats[0].Resets;
            long t0 = SiegeWatch.ElapsedTicks;
            SiegeWatch.Start();
            new CombatSiegeMaintainJob { D = _d }.Run();
            SiegeWatch.Stop();
            LastSiegeMs = (SiegeWatch.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxSiegeMs = Math.Max(MaxSiegeMs, LastSiegeMs);
            CombatSiegeStats st = _d.SiegeState.Stats[0];
            if (st.Resets != resetsBefore)
            {
                MaxSiegeResetMs = Math.Max(MaxSiegeResetMs, LastSiegeMs);
            }
            else if (st.LastChanged > 0)
            {
                LastSiegeChangeMs = LastSiegeMs;
                MaxSiegeChangeMs = Math.Max(MaxSiegeChangeMs, LastSiegeMs);
            }
        }

        /// <summary>开 / 改 / 关攻城剧场（矩形变了 → 下一步缓存全部重建；Enabled = 0 → 释放）。矩形格数超过上限时按上限拒绝（返回 false）。</summary>
        public bool SetSiegeConfig(in CombatSiegeConfig cfg)
        {
            CombatSiegeConfig c = cfg;
            long w = (long)c.Max.x - c.Min.x + 1;
            long h = (long)c.Max.y - c.Min.y + 1;
            if (c.Enabled != 0 && (w <= 0 || h <= 0 || w * h > CombatSiegeConst.MaxRectCells))
            {
                return false;
            }
            _d.SiegeState.Cfg[0] = c;
            Touch();
            return true;
        }

        public CombatSiegeConfig SiegeConfig => _d.SiegeState.Cfg[0];

        /// <summary>职能表：<paramref name="masks"/>[r] = 职能 r 的目标类别位（r = 0 突击 / 1 破坏 / 2 攻城；第 3 项撤退忽略），
        /// <paramref name="bias"/>[r × 6 + 类别位序号] = 偏好代价（10 = 1 米；<see cref="CombatSiegeConst.Inf"/> = 不当目标）。表变了下一步全部流场重建。</summary>
        public void SetSiegeRoles(IReadOnlyList<int> masks, IReadOnlyList<int> bias)
        {
            bool changed = false;
            for (int r = 0; r < CombatSiegeConst.RoleCount; r++)
            {
                int m = r < CombatSiegeConst.RoleRetreat && masks != null && r < masks.Count ? masks[r] & 0x3F : 0;
                changed |= _d.SiegeState.RoleMask[r] != m;
                _d.SiegeState.RoleMask[r] = m;
            }
            for (int i = 0; i < CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount; i++)
            {
                int b = bias != null && i < bias.Count ? Math.Max(0, Math.Min(CombatSiegeConst.Inf, bias[i])) : CombatSiegeConst.Inf;
                changed |= _d.SiegeState.Bias[i] != b;
                _d.SiegeState.Bias[i] = b;
            }
            if (changed && _d.SiegeState.Rect[0].z > 0)
            {
                // 职能表换了：起点值都变了，增量不适用——让下一步按同一矩形整体重建（结果与重新开剧场相同）。
                _d.SiegeState.Rect[0] = new int4(int.MinValue, int.MinValue, 0, 0);
            }
        }

        /// <summary>撤退的集结点（格）。</summary>
        public void SetSiegeExits(IReadOnlyList<int2> exits)
        {
            _d.SiegeState.Exits.Clear();
            if (exits == null)
            {
                return;
            }
            for (int i = 0; i < exits.Count; i++)
            {
                _d.SiegeState.Exits.Add(exits[i]);
            }
        }

        public int SiegeExitCount => _d.SiegeState.Exits.Length;

        /// <summary>给单位写攻城属性（结构单位的类别 / 占地，突袭单位的职能 / 队伍……）。</summary>
        public bool SetSiegeUnit(int id, in CombatSiegeUnit siege)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Siege[i] = siege;
            Touch();
            return true;
        }

        public bool TryGetSiegeUnit(int id, out CombatSiegeUnit siege)
        {
            int i = _d.SlotOf(id);
            siege = i >= 0 ? _d.Siege[i] : default;
            return i >= 0;
        }

        /// <summary>一支队伍（<see cref="CombatSiegeUnit.Group"/>）在内核里还活着几台；<paramref name="perRole"/>（长度 ≥ 4）按职能 1～3 计数，[0] = 撤退中。O(单位数)。</summary>
        public int CountSiegeGroup(int group, int[] perRole = null)
        {
            if (perRole != null)
            {
                Array.Clear(perRole, 0, perRole.Length);
            }
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i))
                {
                    continue;
                }
                CombatSiegeUnit su = _d.Siege[i];
                if (su.Role == 0 || su.Group != group)
                {
                    continue;
                }
                n++;
                if (perRole != null && perRole.Length >= 4)
                {
                    perRole[su.Mode == (byte)CombatSiegeMode.Retreat ? 0 : su.Role]++;
                }
            }
            return n;
        }

        /// <summary>
        /// FG6-DEF-06（FGR-DEF-042 倍速观战）：一支队伍还活着的攻城单位的重心（观战镜头的跟随点）。返回台数；0 台时 x / y = 0。
        /// 只读查询，不改内核状态（观战只改速度与镜头，不影响模拟）。O(单位数)。
        /// </summary>
        public int SiegeGroupCentroid(int group, out double x, out double y)
        {
            x = 0;
            y = 0;
            int n = 0;
            double sx = 0, sy = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i))
                {
                    continue;
                }
                CombatSiegeUnit su = _d.Siege[i];
                if (su.Role == 0 || su.Group != group)
                {
                    continue;
                }
                n++;
                sx += _d.Pos[i].x;
                sy += _d.Pos[i].y;
            }
            if (n > 0)
            {
                x = sx / n;
                y = sy / n;
            }
            return n;
        }

        /// <summary>把一支队伍的全部攻城单位切到 <paramref name="mode"/>（撤退）。返回切换的台数。重选目标立即生效。</summary>
        public int SetSiegeGroupMode(int group, CombatSiegeMode mode)
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i))
                {
                    continue;
                }
                CombatSiegeUnit su = _d.Siege[i];
                if (su.Role == 0 || su.Group != group || su.Mode == (byte)mode)
                {
                    continue;
                }
                su.Mode = (byte)mode;
                su.Breach = 0;
                su.Hold = 0;
                _d.Siege[i] = su;
                _d.Secondary[i] = 0f;
                CombatCommand c = _d.Cmd[i];
                c.Target = 0;
                _d.Cmd[i] = c;
                n++;
            }
            if (n > 0)
            {
                Touch();
            }
            return n;
        }

        /// <summary>移除一支队伍还在内核里的全部攻城单位（队伍被并回行进队伍 / 测试清场）。返回移除个数。</summary>
        public int DespawnSiegeGroup(int group)
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.Id[i] <= 0 || _d.Siege[i].Role == 0 || _d.Siege[i].Group != group)
                {
                    continue;
                }
                _d.SlotOfId[_d.Id[i]] = -1;
                _d.Id[i] = 0;
                _d.Flags[i] = 0;
                n++;
            }
            if (n > 0)
            {
                CombatLogic.Compact(ref _d);
            }
            return n;
        }

        /// <summary>取走至多 <paramref name="max"/> 格还没结算的溅射命中（按先后）。剩下的留到下一步（进存档）。</summary>
        public int DrainSiegeImpacts(int max, List<CombatSiegeImpact> into)
        {
            into?.Clear();
            ref CombatSiegeData s = ref _d.SiegeState;
            int n = Math.Min(Math.Max(0, max), s.Impacts.Length);
            if (n == 0)
            {
                return 0;
            }
            for (int k = 0; k < n; k++)
            {
                into?.Add(s.Impacts[k]);
            }
            s.Impacts.RemoveRange(0, n);
            s.ImpactIndex.Clear();
            for (int k = 0; k < s.Impacts.Length; k++)
            {
                CombatSiegeImpact im = s.Impacts[k];
                s.ImpactIndex.TryAdd(((long)im.Cell.x << 32) ^ (uint)im.Cell.y, k);
            }
            return n;
        }

        public int SiegeImpactCount => _d.SiegeState.Impacts.Length;

        /// <summary>正在被破墙的结构单位（每步按攻城单位的破墙目标去重；按先后）。</summary>
        public int SiegeBreachCount => _d.SiegeState.Breaches.Length;

        public int SiegeBreachAt(int index) => index >= 0 && index < _d.SiegeState.Breaches.Length ? _d.SiegeState.Breaches[index] : 0;

        public CombatSiegeStats SiegeStats => _d.SiegeState.Stats[0];

        /// <summary>当前缓存对应的剧场矩形（min.x, min.y, 宽, 高；宽 = 0 = 没开）。</summary>
        public int4 SiegeRect => _d.SiegeState.Rect[0];

        public bool SiegeFieldValid(int field) => field >= 0 && field < CombatSiegeConst.FieldCount && _d.SiegeState.FieldValid[field] != 0;

        /// <summary>第 <paramref name="field"/> 张流场（职能 × 2 + 开路 0 / 破墙 1）在 <paramref name="cell"/> 的代价（10 = 1 米；到不了 / 不在剧场 / 没算 = Inf）。</summary>
        public int SiegeDistAt(int field, int2 cell) => CombatSiegeLogic.DistAt(ref _d, field, CombatSiegeLogic.IndexOf(ref _d, cell));

        /// <summary>从 <paramref name="from"/> 沿第 <paramref name="field"/> 张流场下坡追踪路线（叠加层 / 预览 / 自检），至多 <paramref name="maxPoints"/> 格。返回格数。</summary>
        public int TraceSiegePath(int field, int2 from, int maxPoints, List<int2> into)
        {
            into?.Clear();
            int idx = CombatSiegeLogic.IndexOf(ref _d, from);
            if (idx < 0 || !SiegeFieldValid(field) || CombatSiegeLogic.DistAt(ref _d, field, idx) >= CombatSiegeConst.Inf)
            {
                return 0;
            }
            into?.Add(from);
            int n = 1;
            for (int k = 0; k < maxPoints; k++)
            {
                int next = CombatSiegeLogic.NextStep(ref _d, field, idx);
                if (next < 0)
                {
                    break;
                }
                idx = next;
                into?.Add(CombatSiegeLogic.CellAtIndex(ref _d, idx));
                n++;
            }
            return n;
        }

        /// <summary>从 <paramref name="from"/> 沿破墙场往前看至多 <paramref name="lookahead"/> 格，第一段挡路的结构单位 ID（0 = 没有）。</summary>
        public int SiegeBreachAhead(int field, int2 from, int lookahead)
        {
            int idx = CombatSiegeLogic.IndexOf(ref _d, from);
            return idx < 0 || !SiegeFieldValid(field) ? 0 : CombatSiegeLogic.BreachAhead(ref _d, field, idx, lookahead);
        }

        /// <summary>
        /// 自检：先维护一次（把两步之间的变化收进来，与下一步开头的维护幂等），再把每张有效流场按当前输入全量重算、与增量维护的结果逐格比对，
        /// 并核对格子缓存（结构单位的占地 / 类别与内核单位一致）。返回不一致的格数（流场 + 缓存）；<paramref name="fields"/> = 比对了几张流场。
        /// </summary>
        public int VerifySiegeFields(out int fields)
        {
            MaintainSiege();
            var o = new NativeArray<int>(2, Allocator.TempJob);
            try
            {
                new CombatSiegeVerifyJob { D = _d, Out = o }.Run();
                fields = o[1];
                return o[0];
            }
            finally
            {
                o.Dispose();
            }
        }

        /// <summary>FG6-DEF-05：内核里还活着的攻城 / 拦截单位所属的队伍键（去重，按出现顺序）。热更层据此清掉“队伍已经不在”的无主单位。O(单位数)。</summary>
        public int CollectSiegeGroups(List<int> into)
        {
            into.Clear();
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i) || _d.Faction[i] != (byte)CombatFaction.Hostile)
                {
                    continue;
                }
                int g = _d.Siege[i].Group;
                if (g != 0 && !into.Contains(g))
                {
                    into.Add(g);
                }
            }
            return into.Count;
        }
        /// <summary>自检 / 诊断：剧场里一格的缓存（通行字节、占用的结构单位 ID、类别位、破墙代价）。不在剧场里返回 false。</summary>
        public bool SiegeCellInfo(int2 cell, out int nav, out int occ, out int cat, out int pen)
        {
            nav = occ = cat = pen = 0;
            int idx = CombatSiegeLogic.IndexOf(ref _d, cell);
            if (idx < 0)
            {
                return false;
            }
            CombatSiegeCell c = _d.SiegeState.Cells[idx];
            nav = c.Nav;
            occ = c.Occ;
            cat = c.Cat;
            pen = c.Pen;
            return true;
        }
        /// <summary>立即维护一次攻城剧场（不走步；自检与放置预览在两步之间读最新流场用）。与下一步开头的维护幂等。</summary>
        public void MaintainSiegeNow() => MaintainSiege();

        /// <summary>头顶职能图标（Burst）：追加到 <paramref name="icons"/>（状态标签图标之后）。<paramref name="visuals"/>[r] = (形状序号, 打包颜色)，r = 0 突击 / 1 破坏 / 2 攻城 / 3 撤退中。</summary>
        public void PrepareSiegeIcons(NativeList<CombatInstance> icons, NativeArray<float2> visuals, double2 origin, float iconSize)
        {
            new CombatSiegeIconJob { D = _d, Icons = icons, Visuals = visuals, Origin = origin, Size = iconSize }.Run();
        }

        /// <summary>FG6-DEF-05（DEBT-FG4ECO07-02）：设置 / 清掉己方机器的驻防点与守点半径（半径 0 = 不守点）。值没变不写。返回是否找到单位。</summary>
        public bool SetMachineGuard(int unitId, double2 post, float radius)
        {
            int i = _d.SlotOf(unitId);
            if (i < 0)
            {
                return false;
            }
            CombatSiegeUnit su = _d.Siege[i];
            float r = radius > 0f && !float.IsInfinity(radius) ? radius : 0f;
            double2 p = r > 0f ? post : double2.zero;
            if (su.GuardRadius == r && su.GuardPost.Equals(p))
            {
                return true;
            }
            su.GuardRadius = r;
            su.GuardPost = p;
            su.HealCd = 0f;
            _d.Siege[i] = su;
            return true;
        }
        /// <summary>离某点最近的存活、可选中的指定阵营与种类的单位（半径内；并列取槽位小者）。0 = 没有。O(单位数)。</summary>
        public int FindNearestKind(double2 point, float radius, CombatFaction faction, CombatUnitKind kind)
        {
            int best = -1;
            double bestDist = radius;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i) || _d.Faction[i] != (byte)faction || _d.Kind[i] != (byte)kind || !_d.Has(i, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double dist = math.distance(point, _d.Pos[i]);
                if (dist < bestDist || (dist == bestDist && best < 0))
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best >= 0 ? _d.Id[best] : 0;
        }

        /// <summary>内核里有攻城属性的突袭单位总数（图标 / 自检）。</summary>
        public int SiegeUnitCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _d.Count; i++)
                {
                    if (_d.IsAlive(i) && _d.Siege[i].Role != 0)
                    {
                        n++;
                    }
                }
                return n;
            }
        }
    }
}
