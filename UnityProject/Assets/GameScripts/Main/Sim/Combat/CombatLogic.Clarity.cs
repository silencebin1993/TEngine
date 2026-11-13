using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG10-FAC-01（FG09 FGR-FAC-008 澄净专属语法；第 6 章“化工（澄净）”五类单位与精英）：喷淋布区（喷洒车 / 液氮泵车）、枢轴喷灌（沉淀巨罐）、
    /// 自爆与油迹（油污爬行者）、分裂液（巨罐阵亡后）、受寒与冻结（液氮“先减速再冻结”）、飞行与投放（灭活无人机）、酸沼（每秒扣最大耐久的比例，DEBT-FG7ENV05-01 / DEBT-FG8EXP02-03）、
    /// 喷淋池被冲洗（冷却液冲淡酸液、燃烧蒸干液氮）。全部是进入仿真结算的机制，参数来自行为参数（<see cref="CombatBehaviorProfile"/> 的 Spray* / Pool* / Pivot* / Burst* /
    /// Trail* / Split* / Airborne / Pollute* / AcidResist / ImmuneMask / StructPref，热更层按敌人表填），状态在 <see cref="CombatData.Clarity"/>（随快照进存档，格式 15）。
    ///
    /// - 喷淋布区：冷却到点、锥形射程内有己方地面单位 → 喷嘴蓄压（≥ 0.8 秒，原地不动，ClarityCue/1）→ 锥形喷出：命中的单位吃伤害、挂状态、加受寒，锥形中段落下一块喷淋池（敌方阵营，按节拍伤害 / 挂状态 / 加受寒）。
    /// - 枢轴喷灌：喷淋臂半径内有己方地面单位 → 蓄压（≥ 0.8 秒，整圈预警）→ 臂绕本体转动，臂扫过的扇区按节拍伤害并挂状态；臂前方预警扇区 = 转速 × 预警秒数
    ///   （静止的单位从进入预警扇区到被扫中不少于预警秒数）；周围没有目标满 3 秒收臂。喷灌期间本体不移动、不用武器。
    /// - 自爆与油迹：爬行时每隔 TrailInterval 秒（走出一个油迹半径以上）留一摊中立油迹；身上一直带着油（没在燃烧时补上）。己方单位进入触发距离 → 引信（≥ 0.8 秒，原地不动）→
    ///   爆炸（只伤己方，溅上油，留一摊油迹）。被击毁时：身上在燃烧 → 中立爆燃（双方都挨，伤害 × DeflagMult，燃烧）；没在燃烧 → 只留一摊油迹。油迹遇到燃烧整片爆燃（液洼同一套整片反应）。
    /// - 分裂：带 SplitCount 的单位被击毁 → 原地生成几滩分裂液（匿名内核单位，爬向最近的己方地面单位，接触按节拍腐蚀，SplitLife 秒后蒸发）。
    ///   多滩分裂液同一节拍对同一个单位只算一次（不叠加）；分裂液与酸沼、喷淋池各按各的规则结算（相加），状态叠层按全局上限封顶。
    /// - 受寒：液氮命中 / 站在液氮池里累积受寒（0～1），移动速度 × (1 − 最大减速 × 受寒)；满 1 冻结（不能移动、不能开火、命令暂停），燃烧或高热（积热过半）加快解冻；
    ///   解冻后一段时间不再受寒（不会被永久冻住）。
    /// - 飞行：不受地形碰撞、地面区域、酸沼、喷淋池影响；格斗 / 力场 / 布区打不到（FG18 FGR-AIR-010），射弹、无人机能打。灭活无人机有投放目标（净化塔）时直线飞过去，
    ///   投放预警（≥ 0.8 秒）后发不丢的玩法事件（热更层让周围再次污染）并自毁。
    /// - 酸沼：每游戏秒一次，站在酸沼里的地面单位（机器 / 敌人；不含炮塔与建筑）扣 最大耐久 × 比例 × (1 − 酸蚀抗性) ×（湿润时冲淡倍率）。危害区与外圈带重叠时只扣一次。
    /// 每步 O(带参数单位 × 局部查询 + 区域)；与是否被观察无关（FGR-BASE-021）。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>枢轴喷灌：周围没有目标累计这么多秒就收臂。</summary>
        private const float PivotIdleSeconds = 3f;
        /// <summary>分裂液找目标的距离（米）。</summary>
        private const float PuddleSense = 12f;
        /// <summary>一次结算死亡效果的上限（连锁爆燃的保险丝）。</summary>
        private const int MaxPendingPerStep = 512;


        // ─────────────────────────────── 查询（各处共用，O(1)） ───────────────────────────────

        /// <summary>空中单位（灭活无人机）。</summary>
        internal static bool IsAir(ref CombatData d, int i) => d.Clarity[i].Air != 0;

        /// <summary>受寒后的移动速度倍率（冻结 / 麻痹 = 0；没有受寒 = 1）。EMG2-PHS-01：受寒 = 寒量累积量（phase_chill，减速上限 p1 = 0.6，与云端同数）。</summary>
        internal static float ChillFactor(ref CombatData d, int i)
        {
            if (IsImmobile(ref d, i))
            {
                return 0f;
            }
            float chill = d.EmgUnit[i].AccA.x;
            if (chill <= 0f)
            {
                return 1f;
            }
            return 1f - math.min(0.95f, d.EmgPhases[EmgPhaseConst.Chill].P1) * math.saturate(chill / math.max(0.01f, d.EmgPhases[EmgPhaseConst.Chill].Enter));
        }

        /// <summary>己方机器的移动速度（只吃受寒；状态标签的减速对己方机器的口径不在本卡改）。</summary>
        internal static float ChillSpeed(ref CombatData d, int i, float speed) => speed * ChillFactor(ref d, i) * EmgWreckSlow(ref d, i) * EmgWireSlow(ref d, i); // EMG1-ENT-01：穿过残骸减速；EMG2-RECL-01：导线拦索

        /// <summary>这个单位的武器能不能打空中目标：格斗 / 力场 / 布区只打同层（FG18 FGR-AIR-010）；射弹、无人机能打；没有武器（只用来感知 / 后撤）算能看见。</summary>
        internal static bool CanHitAir(ref CombatData d, int i)
        {
            int w = d.Weapon[i];
            if (w < 0 || w >= d.Weapons.Length)
            {
                return true;
            }
            CombatCarrier c = d.Weapons[w].Reading.Carrier;
            return c != CombatCarrier.Melee && c != CombatCarrier.Aura && c != CombatCarrier.Field;
        }

        /// <summary>澄净专属行为正把这个单位定在原地（喷嘴蓄压、枢轴喷灌、自爆引信）：这一步不执行行为（不移动、不用武器）。</summary>
        internal static bool ClarityHolds(ref CombatData d, int i)
        {
            CombatClarityUnit c = d.Clarity[i];
            return c.Spray == 1 || c.Pivot != 0 || c.Fuse == 1;
        }

        /// <summary>这个单位的攻击由澄净专属行为结算（喷淋布区 / 枢轴喷灌 / 自爆）：武器列只是参数来源，普通开火路径不出手。</summary>
        internal static bool ClarityOwnsAttack(ref CombatData d, int i)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                return false;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            return p.SprayRange > 0f || p.PivotRadius > 0f || p.BurstRadius > 0f;
        }

        /// <summary>
        /// EMG4-LOOT-01 修复轮 1：这个单位的普通开火是否由澄净阶段接管——敌方按 <see cref="ClarityOwnsAttack"/>；己方只接管枢轴喷灌（环形喷淋架，<see cref="StepClarity"/> 只给己方跑这一种），
        /// 别的澄净参数（被劫持的喷洒车等）照旧普通开火。
        /// </summary>
        internal static bool ClarityOwnsAttackFor(ref CombatData d, int i)
        {
            if (d.Faction[i] == (byte)CombatFaction.Hostile)
            {
                return ClarityOwnsAttack(ref d, i);
            }
            int bp = d.BProfile[i];
            return bp >= 0 && bp < d.Profiles.Length && d.Profiles[bp].PivotRadius > 0f;
        }

        /// <summary>
        /// 腐蚀蜂群“专门腐蚀建筑”（行为参数 StructPref）：射程内最近的己方建筑 / 炮塔（存活、可选中、没潜行、不是只受区域伤害的单位），并列取槽位大者；没有返回 -1。
        /// O(局部格子 + 单位)，与 <see cref="FindTarget"/> 同一套格子查询。
        /// </summary>
        internal static int FindStructureTarget(ref CombatData d, ref CombatGrid grid, int i, float range)
        {
            if (range <= 0f)
            {
                return -1;
            }
            double2 pos = d.Pos[i];
            int best = -1;
            double bestDist = range;
            double reach = range + GridSlack;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        byte kind = d.Kind[k];
                        if (k == i || d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable)
                            || d.Has(k, CombatUnitFlags.Stealthed) || d.Has(k, CombatUnitFlags.AreaOnly)
                            || (kind != (byte)CombatUnitKind.Turret && kind != (byte)CombatUnitKind.Structure))
                        {
                            continue;
                        }
                        double dist = math.distance(pos, d.Pos[k]);
                        if (dist > bestDist || (dist == bestDist && k < best))
                        {
                            continue;
                        }
                        bestDist = dist;
                        best = k;
                    }
                }
            }
            return best;
        }

        /// <summary>酸液（酸沼 / 喷淋池 / 分裂液）对这个单位的伤害倍率 = (1 − 酸蚀抗性) ×（湿润时冲淡倍率）；玩家机器的抗性取它与防腐涂层的较大值。</summary>
        internal static float AcidFactor(ref CombatData d, int k, double now)
        {
            // EmgResist：档案腐蚀抗性（澄净材质 1）、运行时覆盖与防腐涂层下限（FG10-REG-01，派生量，读档后由热更层重写）取大
            float f = 1f - math.saturate(math.max(d.Clarity[k].AcidResist, EmgResist(ref d, k, EmgPhaseConst.ResCorrode)));
            if (f > 0f && d.AcidDilute > 0f && IsWet(ref d, k, now))
            {
                f *= math.saturate(d.AcidDilute);
            }
            return f;
        }

        /// <summary>湿润：身上挂着湿润标签，或站在带湿润标签的区域里（冷却液 / 水）。湿润标签的状态位由热更层写（0 = 本地点没有湿润，永不冲淡）。</summary>
        private static bool IsWet(ref CombatData d, int k, double now)
        {
            uint wet = d.WetBit;
            if (wet == 0u)
            {
                return false;
            }
            if ((d.Status[k] & wet) != 0u && d.StatusActive(k, now))
            {
                return true;
            }
            double2 pos = d.Pos[k];
            float r = d.Radius[k];
            for (int z = 0; z < d.Zones.Length; z++)
            {
                CombatZone zn = d.Zones[z];
                if ((zn.StatusMask & wet) != 0u && zn.Until > now && math.distance(zn.Pos, pos) <= zn.Radius + r)
                {
                    return true;
                }
            }
            // EMG2-MAT-02：站在湿润的斑块里（迁入斑块按区域标签、物质斑块按沾染位 = 湿润：水 / 冷却液）
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone pz = d.Patches[i];
                if (pz.Matter != 0 && pz.Until > now && (PatchTagMask(ref d, pz) & wet) != 0u && math.distance(pz.Pos, pos) <= pz.Radius + r)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>身上正在燃烧（燃烧标签的状态位由热更层写）。</summary>
        private static bool IsBurning(ref CombatData d, int k, double now) =>
            d.FireBit != 0u && (d.Status[k] & d.FireBit) != 0u && d.StatusActive(k, now);

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>敌方单位行动之后（铸造阶段之后）：死亡效果、受寒消退、喷淋 / 枢轴 / 自爆、喷淋池冲洗、酸沼、分裂液接触。</summary>
        private static void StepClarity(ref CombatData d, ref CombatGrid grid, float dt)
        {
            ProcessClarityPending(ref d);
            double now = d.Scalars[0].Time;
            int n = d.Count;
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i))
                {
                    continue;
                }
                StepPhase(ref d, i, dt, now); // EMG2-PHS-01：7 个累积量与相态（取代云端 StepChill）
                StepStick(ref d, i, dt); // EMG2-REACT-01（C06）：胶膜固化粘住计时
                bool hostile = d.Faction[i] == (byte)CombatFaction.Hostile;
                if ((!hostile && d.Faction[i] != (byte)CombatFaction.Player) || IsImmobile(ref d, i) || d.Behavior[i] == (byte)CombatBehavior.Puddle)
                {
                    continue;
                }
                int bp = d.BProfile[i];
                if (bp < 0 || bp >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[bp];
                if (!hostile)
                {
                    // EMG4-LOOT-01 修复轮 1（FGR-FAC-009 环形喷淋架“枢轴喷灌”，规则与敌方版本一致）：己方只跑枢轴喷灌；炮塔要先启用武器（通电 / 有补给，与普通开火同一开关）
                    if (p.PivotRadius > 0f && (d.Kind[i] != (byte)CombatUnitKind.Turret || d.Has(i, CombatUnitFlags.WeaponEnabled)))
                    {
                        StepPivot(ref d, i, in p, dt, now);
                    }
                    continue;
                }
                if (p.SprayRange > 0f)
                {
                    StepSpray(ref d, ref grid, i, in p, dt);
                }
                if (p.PivotRadius > 0f && d.IsAlive(i))
                {
                    StepPivot(ref d, i, in p, dt, now);
                }
                if (p.BurstRadius > 0f && d.IsAlive(i))
                {
                    StepCrawler(ref d, i, in p, dt, now);
                }
            }
            StepCoatFx(ref d, dt); // EMG2-REACT-02（C05 / C07）：酸液滴火、泥浆计时
            StepAcidMarsh(ref d, dt, now);
            StepPuddleContact(ref d, dt, now);
        }

        /// <summary>给单位加受寒（液氮命中 / 液氮池节拍）。EMG2-PHS-01：并入寒量累积量（EmgAddAcc：冻结中 / 免疫窗内不加、按寒抗折减、满了进入冻结）；空中单位不加。</summary>
        internal static void ApplyChill(ref CombatData d, int k, float amount)
        {
            if (!(amount > 0f) || k < 0 || k >= d.Count || !d.IsAlive(k) || d.Clarity[k].Air != 0)
            {
                return;
            }
            EmgAddAcc(ref d, k, EmgPhaseConst.Chill, amount);
        }

        /// <summary>冻结的敌方单位：架炮驻锄回到未架设（FG09 第 10 节负向“架设到一半被冻结”）。</summary>
        private static void InterruptFrozen(ref CombatData d, int i)
        {
            CombatFoundryUnit f = d.Foundry[i];
            if (f.Deploy != 0)
            {
                f.Deploy = 0;
                f.DeployT = 0f;
                d.Foundry[i] = f;
                d.Secondary[i] = 0f;
            }
        }

        // ─────────────────────────────── 喷淋布区 ───────────────────────────────

        private static void StepSpray(ref CombatData d, ref CombatGrid grid, int i, in CombatBehaviorProfile p, float dt)
        {
            CombatClarityUnit c = d.Clarity[i];
            if (c.Spray == 0)
            {
                if (c.SprayT > 0f)
                {
                    c.SprayT = math.max(0f, c.SprayT - dt);
                    d.Clarity[i] = c;
                    return;
                }
                // 审查修复：按外沿距离选目标（与攻城的 EdgeDistance 停驻口径、锥形命中的“射程 + 目标半径”一致），否则攻城中停在建筑外沿射程处的喷淋单位永远不出手。
                int t = FindSprayTarget(ref d, ref grid, i, p.SprayRange, d.Has(i, CombatUnitFlags.NeedsLos));
                if (t < 0)
                {
                    return;
                }
                double2 to = d.Pos[t] - d.Pos[i];
                c.Spray = 1;
                c.SprayT = math.max(0.8f, p.SprayWarn);
                c.SprayDir = math.lengthsq(to) > 1e-12 ? (float2)math.normalize(to) : new float2(0f, 1f);
                c.SprayAt = d.Pos[t];
                c.Anchor = d.Pos[i];
                d.Clarity[i] = c;
                TurnToward(ref d, i, d.Pos[t], 360f);
                ClarityCue(ref d, i, d.Id[t], c.SprayT, PoolLanding(d.Pos[i], c, p.SprayRange), 1, p.SprayChill > 0f ? (byte)2 : (byte)1);
                return;
            }
            c.SprayT -= dt;
            if (c.SprayT > 0f)
            {
                d.Clarity[i] = c;
                return;
            }
            float2 dir = c.SprayDir;
            double2 landing = PoolLanding(d.Pos[i], c, p.SprayRange);
            c.Spray = 0;
            c.SprayT = math.max(0.5f, p.SprayCycle);
            d.Clarity[i] = c;
            SprayCone(ref d, i, in p, dir, landing);
        }

        /// <summary>喷淋池落点：沿喷淋方向，取“到蓄压时锁定的目标的距离”与射程的较小者（池子盖住被瞄准的那片地，而不是固定落在射程中段够不着停驻距离上的目标）。</summary>
        internal static double2 PoolLanding(double2 origin, in CombatClarityUnit c, float range)
        {
            double dist = math.min(math.distance(origin, c.SprayAt), (double)range);
            return origin + (double2)c.SprayDir * dist;
        }

        /// <summary>
        /// 喷淋目标：射程内（外沿距离 = 中心距离 − 目标半径 ≤ 射程）最近的己方地面单位（含建筑 / 炮塔；不含空中、潜行、只受区域伤害的单位），
        /// 按外沿距离取最近、并列取槽位大者；<paramref name="needsLos"/> 时过滤视线被挡的。O(局部格子 + 单位)。
        /// </summary>
        private static int FindSprayTarget(ref CombatData d, ref CombatGrid grid, int i, float range, bool needsLos)
        {
            if (range <= 0f)
            {
                return -1;
            }
            double2 pos = d.Pos[i];
            int best = -1;
            double bestEdge = double.MaxValue;
            double reach = range + GridSlack + grid.MaxRadius;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (k == i || d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable)
                            || d.Has(k, CombatUnitFlags.Stealthed) || d.Has(k, CombatUnitFlags.AreaOnly) || IsAir(ref d, k))
                        {
                            continue;
                        }
                        double edge = math.max(0.0, math.distance(pos, d.Pos[k]) - d.Radius[k]);
                        if (edge > range || edge > bestEdge || (edge == bestEdge && k < best))
                        {
                            continue;
                        }
                        if (needsLos && !LineOfSight(ref d, pos, d.Pos[k]))
                        {
                            continue;
                        }
                        bestEdge = edge;
                        best = k;
                    }
                }
            }
            return best;
        }

        /// <summary>FGR-DEF-003“优先攻击正在破坏建筑的敌人”：澄净专属攻击这一下打到己方建筑 / 炮塔就置“正在破坏建筑”，没打到就清掉（与普通开火同一口径）。</summary>
        private static void MarkSiegeAttack(ref CombatData d, int i, bool hitBuilding)
        {
            if (d.Faction[i] == (byte)CombatFaction.Hostile)
            {
                d.Set(i, CombatUnitFlags.SiegeAttack, hitBuilding);
            }
        }

        private static bool IsBuilding(ref CombatData d, int k) =>
            d.Kind[k] == (byte)CombatUnitKind.Turret || d.Kind[k] == (byte)CombatUnitKind.Structure;

        /// <summary>锥形喷出：锥形内的己方地面单位吃伤害、挂状态、加受寒；锥形中段落下一块喷淋池。</summary>
        private static void SprayCone(ref CombatData d, int i, in CombatBehaviorProfile p, float2 dir, double2 landing)
        {
            double2 origin = d.Pos[i];
            bool hitBuilding = false;
            float cosHalf = p.SprayCone >= 180f ? -2f : math.cos(math.radians(math.max(1f, p.SprayCone)));
            FxOf(ref d, p.SprayStatus, out float sdps, out float sslow, out float svuln);
            int hits = 0;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                {
                    continue;
                }
                double2 to = d.Pos[k] - origin;
                double dist = math.length(to);
                if (dist > p.SprayRange + d.Radius[k])
                {
                    continue;
                }
                if (dist > 1e-6 && math.dot(dir, (float2)(to / dist)) < cosHalf)
                {
                    continue;
                }
                if (p.SprayDamage > 0f)
                {
                    DamageFrom(ref d, k, p.SprayDamage, i, EmgCode.SrcZone);
                }
                if ((p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus) != 0u && d.IsAlive(k))
                {
                    ApplyStatus(ref d, k, p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus, p.SprayStatusSeconds, sdps, sslow, svuln, i, p.SprayDamage); // EMG2-PHS-01：液氮喷淋的受寒由 SprayChill 给，冻结标签不另换寒量
                }
                if (p.SprayChill > 0f)
                {
                    ApplyChill(ref d, k, p.SprayChill);
                }
                hitBuilding |= IsBuilding(ref d, k);
                hits++;
            }
            MarkSiegeAttack(ref d, i, hitBuilding);
            if (p.PoolRadius > 0f && p.PoolSeconds > 0f)
            {
                SpawnPool(ref d, i, landing, p.PoolRadius, p.PoolSeconds, p.PoolDps, p.SprayStatus, p.SprayStatusSeconds, sdps, sslow, svuln, p.PoolChill);
            }
            CombatCounters c = d.Counters[0];
            c.Sprays++;
            d.Counters[0] = c;
            ClarityCue(ref d, i, hits, p.SprayRange, origin, 2, p.SprayChill > 0f ? (byte)2 : (byte)1);
        }

        /// <summary>状态位组合的效果（持续伤害 / 减速 / 易伤各取大，按状态位表）。</summary>
        private static void FxOf(ref CombatData d, uint mask, out float dps, out float slow, out float vuln)
        {
            dps = 0f;
            slow = 0f;
            vuln = 0f;
            uint bits = mask & ~CombatConst.StatusBitZoneSlow;
            while (bits != 0u)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1u;
                MergeFx(d.StatusFx[b], ref dps, ref slow, ref vuln);
            }
        }

        /// <summary>一块喷淋池（中立，EMG2-BOSS-01；与油迹共用上限）。</summary>
        private static bool SpawnPool(ref CombatData d, int owner, double2 pos, float radius, float seconds, float dps, uint mask, float statusSeconds,
            float sdps, float sslow, float svuln, float chill)
        {
            // EMG2-MAT-02：迁入斑块（mat.acid / mat.cryo）。EMG2-BOSS-01（FG19B §7 #1 #2）：池中立，别的阵营踩进去同样挨，澄净靠抗性列（腐蚀 1、寒 0.5）；炮塔与建筑照旧挨（StepZones 的池分支）
            if (!SpawnZone(ref d, owner, CombatFaction.Neutral, pos, radius, seconds, dps, 0f, 1f, mask, statusSeconds, sdps, sslow, svuln, CombatConst.ZoneKindPool, migrate: true))
            {
                return false;
            }
            if (TryGetLastZone(ref d, out CombatZone z))
            {
                z.Look = CombatZoneLook.Pool;
                z.Chill = math.max(0f, chill);
                SetLastZone(ref d, z);
            }
            return true;
        }

        /// <summary>一摊中立油迹（与喷淋池共用上限）。</summary>
        private static bool SpawnSlick(ref CombatData d, int owner, double2 pos, float radius, float seconds, uint mask, float statusSeconds)
        {
            if (mask == 0u || !(radius > 0f) || !(seconds > 0f))
            {
                return false;
            }
            if (!SpawnZone(ref d, owner, CombatFaction.Neutral, pos, radius, seconds, 0f, 0f, 1f, mask, math.max(0f, statusSeconds), 0f, 0f, 0f, CombatConst.ZoneKindSlick, migrate: true))
            {
                return false;
            }
            SetLastZoneLook(ref d, CombatZoneLook.Pool);
            return true;
        }

        // ─────────────────────────────── 枢轴喷灌 ───────────────────────────────

        private static void StepPivot(ref CombatData d, int i, in CombatBehaviorProfile p, float dt, double now)
        {
            CombatClarityUnit c = d.Clarity[i];
            double2 center = d.Pos[i];
            byte foe = d.Faction[i] == (byte)CombatFaction.Hostile ? (byte)CombatFaction.Player : (byte)CombatFaction.Hostile; // EMG4-LOOT-01：己方环形喷淋架扫敌方
            int nearest = NearestPlayerGround(ref d, center, p.PivotRadius, out double2 at, foe);
            if (c.Pivot == 0)
            {
                if (nearest < 0)
                {
                    return;
                }
                c.Pivot = 1;
                c.PivotT = math.max(0.8f, p.PivotWarn);
                // 起始臂角：离最近的目标至少“一个预警扇区 + 半个臂宽”（静止的目标从预警扇区出现到被扫中不少于预警秒数），且不少于 90°。
                float lead = math.max(90f, p.PivotSpeed * math.max(0.8f, p.PivotWarn) + p.PivotWidth * 0.5f);
                c.PivotAngle = Wrap360(AngleDeg(at - center) - lead);
                c.Anchor = center;
                d.Clarity[i] = c;
                ClarityCue(ref d, i, d.Id[nearest], c.PivotT, center, 3, 0);
                return;
            }
            if (c.Pivot == 1)
            {
                c.PivotT -= dt;
                if (c.PivotT <= 0f)
                {
                    c.Pivot = 2;
                    c.PivotT = 0f;
                    c.PivotPrev = Wrap360(c.PivotAngle - p.PivotWidth); // 起转：前沿从“臂的后缘”算起，第一拍把起始臂已经盖住的扇区也算上
                }
                d.Clarity[i] = c;
                return;
            }
            c.PivotAngle = Wrap360(c.PivotAngle + p.PivotSpeed * dt);
            c.PivotT = nearest < 0 ? c.PivotT + dt : 0f;
            if (c.PivotT >= PivotIdleSeconds)
            {
                c.Pivot = 0;
                c.PivotT = 0f;
                d.Clarity[i] = c;
                return;
            }
            d.Clarity[i] = c;
            float tick = ZoneTickOf(ref d);
            if (!CrossedTick(now, dt, tick))
            {
                return;
            }
            float half = math.max(1f, p.PivotWidth * 0.5f);
            // 审查修复（复核第 2 轮）：按臂的**前沿**这一节拍扫过的弧 [上一拍前沿, 这一拍前沿) 结算——每个目标每扫过一次恰好命中一次，
            // 不论每拍转角比臂宽大还是小都不漏、不重复；一次命中的伤害 = 每秒伤害 × 臂扫过它的时长（臂宽 ÷ 转速）。臂不转（冻结）时弧为 0、不伤人。
            float arcStart = c.PivotPrev + half;
            float arcLen = Wrap360(c.PivotAngle - c.PivotPrev);
            c.PivotPrev = c.PivotAngle;
            d.Clarity[i] = c;
            if (arcLen <= 0f)
            {
                return;
            }
            FxOf(ref d, p.SprayStatus, out float sdps, out float sslow, out float svuln);
            float dmg = math.max(0f, p.PivotDps) * math.max(1f, p.PivotWidth) / math.max(1f, p.PivotSpeed);
            bool hitBuilding = false;
            // EMG4-LOOT-01 修复轮 1：己方喷灌臂扫中 = 这把武器的一次出手（evt.hit 进它的程序：环形喷淋架声明的“命中 → 排放酸液”在被扫中的目标脚下生成斑块）；
            // 敌方的喷灌照旧不开出手上下文（逐字不变）。
            int w = d.Weapon[i];
            bool shot = false;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == i || d.Faction[k] != foe || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                {
                    continue;
                }
                double2 to = d.Pos[k] - center;
                if (math.length(to) > p.PivotRadius + d.Radius[k])
                {
                    continue;
                }
                if (Wrap360(AngleDeg(to) - arcStart) >= arcLen)
                {
                    continue;
                }
                hitBuilding |= IsBuilding(ref d, k);
                if (!shot && foe == (byte)CombatFaction.Hostile && w >= 0 && w < d.Weapons.Length && d.Weapons[w].Program >= 0)
                {
                    shot = true;
                    EmgShotBegin(ref d, i, k, d.Weapons[w]);
                }
                if (dmg > 0f)
                {
                    if (shot)
                    {
                        DamageUnit(ref d, k, dmg, i); // 在出手上下文里结算（DamageFrom 会换成无程序的来源上下文，evt.hit 进不了程序）
                    }
                    else
                    {
                        DamageFrom(ref d, k, dmg, i, EmgCode.SrcMelee);
                    }
                }
                if ((p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus) != 0u && d.IsAlive(k))
                {
                    ApplyStatus(ref d, k, p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus, p.SprayStatusSeconds, sdps, sslow, svuln, i, dmg); // 与判断同一掩码（审查修复 P2）
                }
                CombatCounters cc = d.Counters[0];
                cc.PivotHits++;
                d.Counters[0] = cc;
            }
            if (shot)
            {
                EmgShotEnd(ref d);
            }
            if (foe == (byte)CombatFaction.Player)
            {
                MarkSiegeAttack(ref d, i, hitBuilding);
            }
        }

        /// <summary>方向的角度（度，0 = +x，逆时针）。</summary>
        internal static float AngleDeg(double2 v) => math.lengthsq(v) < 1e-12 ? 0f : math.degrees((float)math.atan2(v.y, v.x));

        internal static float Wrap360(float a)
        {
            a %= 360f;
            return a < 0f ? a + 360f : a;
        }

        /// <summary>a − b 规约到 (−180, 180]。</summary>
        internal static float AngleDiff(float a, float b)
        {
            float x = Wrap360(a - b);
            return x > 180f ? x - 360f : x;
        }

        /// <summary>半径内最近的己方地面单位（含建筑 / 炮塔；不含空中）。<paramref name="faction"/> = 找哪一方（EMG4-LOOT-01：己方环形喷淋架找敌方；缺省己方，旧调用逐字不变）。</summary>
        private static int NearestPlayerGround(ref CombatData d, double2 pos, float radius, out double2 at, byte faction = (byte)CombatFaction.Player)
        {
            int best = -1;
            double bestD = double.MaxValue;
            at = pos;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != faction || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                {
                    continue;
                }
                double dist = math.distance(pos, d.Pos[k]) - d.Radius[k];
                if (dist > radius || dist > bestD || (dist == bestD && k > best))
                {
                    continue;
                }
                bestD = dist;
                best = k;
                at = d.Pos[k];
            }
            return best;
        }

        // ─────────────────────────────── 自爆与油迹 ───────────────────────────────

        private static void StepCrawler(ref CombatData d, int i, in CombatBehaviorProfile p, float dt, double now)
        {
            // 身上一直带着油：没在燃烧、油也掉了（被引燃消耗）时补上。燃烧中不补——被火击毁才算“被火击毁”。
            if (p.TrailStatus != 0u && !IsBurning(ref d, i, now) && ((d.Status[i] & p.TrailStatus) != p.TrailStatus || !d.StatusActive(i, now)))
            {
                ApplyStatus(ref d, i, p.TrailStatus, math.max(5f, TrailStatusSecondsOf(in p)), 0f, 0f, 0f, i, 0f);
                if (!d.IsAlive(i))
                {
                    return;
                }
            }
            CombatClarityUnit c = d.Clarity[i];
            double2 pos = d.Pos[i];
            if (c.Fuse == 0 && p.TrailInterval > 0f)
            {
                c.TrailT -= dt;
                if (c.TrailT <= 0f)
                {
                    c.TrailT = p.TrailInterval;
                    if (math.distancesq(pos, c.TrailLast) >= (double)p.TrailRadius * p.TrailRadius)
                    {
                        c.TrailLast = pos;
                        SpawnSlick(ref d, i, pos, p.TrailRadius, p.TrailSeconds, p.TrailStatus, p.TrailStatusSeconds);
                    }
                }
            }
            if (c.Fuse == 0)
            {
                float trigger = p.BurstTrigger > 0f ? p.BurstTrigger : p.BurstRadius * 0.6f;
                if (AnyPlayerGroundWithin(ref d, pos, trigger))
                {
                    c.Fuse = 1;
                    c.FuseT = math.max(0.8f, p.BurstFuse);
                    c.Anchor = pos;
                    d.Clarity[i] = c;
                    ClarityCue(ref d, i, 0, p.BurstRadius, pos, 4, 0);
                    return;
                }
                d.Clarity[i] = c;
                return;
            }
            if (c.Fuse == 1)
            {
                c.FuseT -= dt;
                if (c.FuseT > 0f)
                {
                    d.Clarity[i] = c;
                    return;
                }
                c.Fuse = 2;
                d.Clarity[i] = c;
                CrawlerBurst(ref d, i, in p);
                return;
            }
            d.Clarity[i] = c;
        }

        private static bool AnyPlayerGroundWithin(ref CombatData d, double2 pos, float radius)
        {
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] == (byte)CombatFaction.Player && d.IsAlive(k) && d.Has(k, CombatUnitFlags.Targetable) && !IsAir(ref d, k)
                    && math.distance(d.Pos[k], pos) <= radius + d.Radius[k])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>自爆：半径内的己方地面单位吃伤害并溅上油，原地留一摊油迹，本体阵亡（不算任何人击杀）。</summary>
        private static void CrawlerBurst(ref CombatData d, int i, in CombatBehaviorProfile p)
        {
            double2 pos = d.Pos[i];
            float radius = math.max(0.5f, p.BurstRadius);
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], pos) > radius + d.Radius[k])
                {
                    continue;
                }
                DamageFrom(ref d, k, p.BurstDamage, i, EmgCode.SrcZone);
                if (p.TrailStatus != 0u && d.IsAlive(k))
                {
                    ApplyStatus(ref d, k, p.TrailStatus, TrailStatusSecondsOf(in p), 0f, 0f, 0f, i, p.BurstDamage);
                }
            }
            SpawnSlick(ref d, i, pos, math.max(p.TrailRadius, radius * 0.6f), p.TrailSeconds, p.TrailStatus, p.TrailStatusSeconds);
            CombatCounters c = d.Counters[0];
            c.Bursts++;
            d.Counters[0] = c;
            ClarityCue(ref d, i, 0, radius, pos, 5, 0);
            SelfDestruct(ref d, i); // 审查修复：自爆不是被谁击毁（不计击杀、不掉落、不解锁靶子）
        }

        /// <summary>油迹 / 溅油挂的状态秒数：表 statusSeconds；没填按油迹存留秒数（至少 3 秒）。</summary>
        private static float TrailStatusSecondsOf(in CombatBehaviorProfile p) =>
            p.TrailStatusSeconds > 0f ? p.TrailStatusSeconds : math.max(3f, p.TrailSeconds);

        // ─────────────────────────────── 死亡效果（分裂 / 爆燃） ───────────────────────────────

        /// <summary>
        /// <see cref="Kill"/> 里调用（O(1)）：带分裂参数的单位记一条“分裂”，带自爆参数且不是自己引爆的单位记一条“阵亡效果”（身上燃烧 = 爆燃倍率，否则 0 = 只留油迹）。
        /// 结算放到下一个澄净阶段（阵亡可能发生在弹体 / 区域的遍历中途，不在遍历里追加单位或区域）。
        /// </summary>
        private static void ClarityOnKill(ref CombatData d, int t, int killerId)
        {
            int bp = d.BProfile[t];
            if (bp < 0 || bp >= d.Profiles.Length || d.Behavior[t] == (byte)CombatBehavior.Puddle || d.Faction[t] != (byte)CombatFaction.Hostile)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            if (p.SplitCount >= 1f)
            {
                d.ClarityPending.Add(new CombatClarityPending { Kind = 2, Profile = bp, Source = killerId, Pos = d.Pos[t] });
            }
            if (p.BurstRadius > 0f && d.Clarity[t].Fuse != 2)
            {
                bool burning = IsBurning(ref d, t, d.Scalars[0].Time);
                d.ClarityPending.Add(new CombatClarityPending
                {
                    Kind = 1,
                    Profile = bp,
                    Source = killerId,
                    Mult = burning ? math.max(1f, p.DeflagMult) : 0f,
                    Pos = d.Pos[t],
                });
            }
        }

        private static void ProcessClarityPending(ref CombatData d)
        {
            if (d.ClarityPending.Length == 0)
            {
                return;
            }
            // 结算中可能连锁（爆燃炸死另一只爬行者）：新记的一条排在后面，同一阶段接着结算（上限防死循环）。
            for (int q = 0; q < d.ClarityPending.Length && q < MaxPendingPerStep; q++)
            {
                CombatClarityPending e = d.ClarityPending[q];
                if (e.Profile < 0 || e.Profile >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[e.Profile];
                if (e.Kind == 2)
                {
                    SpawnSplit(ref d, in e, in p);
                }
                else if (e.Kind == 1)
                {
                    DeathBurst(ref d, in e, in p);
                }
            }
            d.ClarityPending.Clear();
        }

        /// <summary>自爆单位被击毁：身上燃烧 → 中立爆燃（双方地面单位都挨，挂燃烧，留爆燃的残留区域）；否则只留一摊油迹。</summary>
        private static void DeathBurst(ref CombatData d, in CombatClarityPending e, in CombatBehaviorProfile p)
        {
            if (!(e.Mult > 0f))
            {
                SpawnSlick(ref d, -1, e.Pos, math.max(0.5f, p.TrailRadius * 1.5f), p.TrailSeconds, p.TrailStatus, p.TrailStatusSeconds);
                return;
            }
            // EMG2-REACT-02（FG19B §7 #5 / §12 #5，按推荐默认执行（2026-10-30），待用户确认）：被火击毁不再 ×1.5 爆伤——原地排 15 份高温油，下一节拍由 E01 整片爆燃（附带 8 + 燃烧区）
            float radius = math.max(0.5f, p.BurstRadius * 1.2f);
            EmgRuleSpawn(ref d, EmgMatterConst.Oil, e.Pos, radius, 15f, 300f, 0f, e.Source, 0, 0);
            CombatCounters c = d.Counters[0];
            c.Deflagrations++;
            d.Counters[0] = c;
            ClarityCue(ref d, -1, e.Source, radius, e.Pos, 5, 1);
        }

        /// <summary>分裂：原地生成 SplitCount 滩分裂液（匿名内核单位：外部键 -1、阵亡即移除、不参与攻城计数）。</summary>
        private static void SpawnSplit(ref CombatData d, in CombatClarityPending e, in CombatBehaviorProfile p)
        {
            int count = math.clamp((int)math.round(p.SplitCount), 1, 8);
            float radius = math.max(0.3f, p.SplitRadius * 0.5f);
            for (int j = 0; j < count; j++)
            {
                float ang = 6.2831853f * j / count;
                double2 pos = e.Pos + new double2(math.cos(ang), math.sin(ang)) * 0.9;
                CombatScalars s = d.Scalars[0];
                int id = s.NextId++;
                s.Revision++;
                d.Scalars[0] = s;
                int slot = d.Append(new CombatSpawn
                {
                    ExtKey = -1,
                    Kind = CombatUnitKind.Enemy,
                    Faction = CombatFaction.Hostile,
                    Behavior = CombatBehavior.Puddle,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
                    Position = pos,
                    Home = pos,
                    Radius = radius,
                    Speed = math.max(0f, p.SplitSpeed),
                    Health = math.max(1f, p.SplitHp),
                    MaxHealth = math.max(1f, p.SplitHp),
                    Weapon = -1,
                    BehaviorProfile = e.Profile,
                    Priority = 1,
                    ArmorHalfAngleDeg = 90f,
                    ArmorFacing = new float2(0f, -1f),
                }, id);
                CombatClarityUnit c = default;
                c.Life = p.SplitLife > 0f ? p.SplitLife : 20f;
                c.AcidResist = math.saturate(p.AcidResist);
                c.ImmuneMask = p.ImmuneMask;
                d.Clarity[slot] = c;
            }
            CombatCounters cc = d.Counters[0];
            cc.Splits++;
            d.Counters[0] = cc;
            ClarityCue(ref d, -1, count, p.SplitRadius, e.Pos, 8, (byte)count);
        }

        /// <summary>分裂液：存活到期蒸发；否则慢速爬向最近的己方地面单位（接触结算在 <see cref="StepPuddleContact"/>）。</summary>
        private static void StepPuddle(ref CombatData d, int i, float dt)
        {
            CombatClarityUnit c = d.Clarity[i];
            c.Life -= dt;
            d.Clarity[i] = c;
            if (c.Life <= 0f)
            {
                SelfDestruct(ref d, i); // 蒸发不是被谁击毁
                return;
            }
            float speed = SlowedSpeed(ref d, i, d.Speed[i]);
            if (speed <= 0f)
            {
                return;
            }
            int t = NearestPlayerGround(ref d, d.Pos[i], PuddleSense, out double2 at);
            if (t < 0)
            {
                return;
            }
            MoveToward(ref d, i, d.Pos[i], at, speed * dt, d.Radius[i] * 0.5);
        }

        /// <summary>分裂液接触（按区域节拍）：同一节拍里一个己方单位最多被分裂液腐蚀一次（多滩不叠加）；伤害吃酸蚀抗性与冲淡。</summary>
        private static void StepPuddleContact(ref CombatData d, float dt, double now)
        {
            float tick = ZoneTickOf(ref d);
            if (!CrossedTick(now, dt, tick))
            {
                return;
            }
            long tickId = (long)math.floor(now / tick);
            int n = d.Count;
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i) || d.Behavior[i] != (byte)CombatBehavior.Puddle)
                {
                    continue;
                }
                int bp = d.BProfile[i];
                if (bp < 0 || bp >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[bp];
                float reach = math.max(d.Radius[i], p.SplitRadius);
                FxOf(ref d, p.SprayStatus, out float sdps, out float sslow, out float svuln);
                for (int k = 0; k < n; k++)
                {
                    if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                    {
                        continue;
                    }
                    if (math.distance(d.Pos[k], d.Pos[i]) > reach + d.Radius[k])
                    {
                        continue;
                    }
                    CombatClarityUnit vc = d.Clarity[k];
                    if (vc.PuddleTick == tickId)
                    {
                        continue; // 这一节拍已经被别的分裂液腐蚀过：多滩不叠加
                    }
                    vc.PuddleTick = tickId;
                    d.Clarity[k] = vc;
                    float dmg = math.max(0f, p.SplitDps) * tick * AcidFactor(ref d, k, now);
                    if (dmg > 0f)
                    {
                        DamageFrom(ref d, k, dmg, i, EmgCode.SrcPatch);
                    }
                    if ((p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus) != 0u && d.IsAlive(k))
                    {
                        ApplyStatus(ref d, k, p.SprayChill > 0f ? p.SprayStatus & ~d.FrozenBit : p.SprayStatus, p.SprayStatusSeconds, sdps, sslow, svuln, i, dmg); // 与判断同一掩码（审查修复 P2）
                    }
                }
            }
        }

        // ─────────────────────────────── 酸沼 ───────────────────────────────

        /// <summary>
        /// 酸沼（每游戏秒一次）：站在酸沼里的地面单位（机器 / 敌人；炮塔与建筑不在这里结算）扣 最大耐久 × 比例 × 酸液倍率（抗性、冲淡）。
        /// 危害区与外圈带、两片危害区重叠时只扣一次。血量在热更层的机器走伤害请求（与其它伤害同一入口）。O(单位 × 酸沼形状)。
        /// </summary>
        private static void StepAcidMarsh(ref CombatData d, float dt, double now)
        {
            // 每游戏秒一次（跨拍判定带余量，见 CrossedTick；FG10-FAC-01 实测不带余量每秒 2 次）。
            if (d.AcidShapes.Length == 0 || !(d.AcidPct > 0f) || !CrossedTick(now, dt, 1.0))
            {
                return;
            }
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (!d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || d.Faction[k] == (byte)CombatFaction.Neutral || IsAir(ref d, k))
                {
                    continue;
                }
                byte kind = d.Kind[k];
                if (kind != (byte)CombatUnitKind.Machine && kind != (byte)CombatUnitKind.Enemy)
                {
                    continue;
                }
                if (!InAcid(ref d, d.Pos[k]))
                {
                    continue;
                }
                float f = AcidFactor(ref d, k, now);
                float dmg = d.MaxHp[k] * d.AcidPct * f;
                if (!(dmg > 0f))
                {
                    continue;
                }
                DamageFrom(ref d, k, dmg, -1, EmgCode.SrcPatch); // 酸沼 = 常驻 mat.acid 虚拟世界源（FG19 物质表下注）
                CombatCounters c = d.Counters[0];
                c.AcidTicks++;
                d.Counters[0] = c;
                if (d.Faction[k] == (byte)CombatFaction.Player)
                {
                    ClarityCue(ref d, k, 0, dmg, d.Pos[k], 11, 0);
                }
            }
        }

        /// <summary>某点在不在任一酸沼形状里（圆环：内半径 ≤ 距离 ≤ 外半径；内半径 0 = 实心圆）。</summary>
        internal static bool InAcid(ref CombatData d, double2 pos)
        {
            for (int s = 0; s < d.AcidShapes.Length; s++)
            {
                float4 sh = d.AcidShapes[s];
                double dx = pos.x - sh.x;
                double dy = pos.y - sh.y;
                double r2 = dx * dx + dy * dy;
                if (r2 <= (double)sh.w * sh.w && r2 >= (double)sh.z * sh.z)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 飞行与投放 ───────────────────────────────

        /// <summary>空中的敌方单位（灭活无人机）：有投放目标时飞去投放；攻城中按职能打（不走流场、不破墙）；否则扑向感知范围内的己方单位、没有就飞向目标点。</summary>
        private static void StepAirUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            float speed = SlowedSpeed(ref d, i, p.Speed > 0f ? p.Speed : d.Speed[i]);
            bool retreat = d.Siege[i].Role != 0 && d.Siege[i].Mode == (byte)CombatSiegeMode.Retreat;
            // FG10-RAID-01：家园内核只让攻城中的单位投放（PolluteSiegeOnly）；正在投放的那一架照常投完（StepPollute 先处理 Pollute == 1）。
            bool mayPollute = d.PolluteSiegeOnly == 0 || CombatSiegeLogic.UsesSiege(ref d, i) || d.Clarity[i].Pollute == 1;
            if (!retreat && mayPollute && p.PolluteRange > 0f && StepPollute(ref d, i, in p, speed, dt))
            {
                return;
            }
            if (CombatSiegeLogic.UsesSiege(ref d, i))
            {
                CombatSiegeLogic.StepAirRaider(ref d, ref grid, i, speed, dt);
                return;
            }
            int w = d.Weapon[i];
            if (w < 0 || w >= d.Weapons.Length)
            {
                AirMoveToward(ref d, i, d.Home[i], speed * dt, 0.5);
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            float sense = p.SenseRange > 0f ? p.SenseRange : wp.Range;
            int t = FindTarget(ref d, ref grid, i, sense, false, wp.TargetMode);
            float c0 = d.Cycle[i] - dt;
            d.Cycle[i] = c0;
            if (t < 0)
            {
                AirMoveToward(ref d, i, d.Home[i], speed * dt, 0.5);
                return;
            }
            double dist = math.distance(d.Pos[i], d.Pos[t]);
            float engage = EngageRange(ref d, i, wp);
            if (dist > engage)
            {
                AirMoveToward(ref d, i, d.Pos[t], speed * dt, engage * 0.9);
                return;
            }
            if (c0 > 0f)
            {
                return;
            }
            d.Cycle[i] = EffectiveCooldown(wp);
            UnitAttack(ref d, i, t, wp);
        }

        /// <summary>投放：飞向最近的投放目标，进入接近距离 → 投放预警（≥ 0.8 秒，悬停）→ 发不丢的“再污染”事件并自毁。没有目标返回 false（按普通空中单位行动）。</summary>
        private static bool StepPollute(ref CombatData d, int i, in CombatBehaviorProfile p, float speed, float dt)
        {
            CombatClarityUnit c = d.Clarity[i];
            if (c.Pollute == 1)
            {
                c.PolluteT -= dt;
                if (c.PolluteT > 0f)
                {
                    d.Clarity[i] = c;
                    return true;
                }
                c.Pollute = 0;
                d.Clarity[i] = c;
                float r = 0f;
                for (int q = 0; q < d.PolluteTargets.Length; q++)
                {
                    float3 pt = d.PolluteTargets[q];
                    if (math.distancesq(new double2(pt.x, pt.y), c.PolluteAt) < 0.01)
                    {
                        r = pt.z;
                        break;
                    }
                }
                Gameplay(ref d, CombatEventKind.Repolluted, i, d.Siege[i].Group, r, 0f, c.PolluteAt, 0, 0); // Other = 投放者所属攻城队伍的键（FG10-RAID-01：再污染记到那一份结算；非攻城单位 0）
                CombatCounters cc = d.Counters[0];
                cc.Repollutions++;
                d.Counters[0] = cc;
                SelfDestruct(ref d, i); // 审查修复：投放后自毁不是被谁击毁
                return true;
            }
            if (d.PolluteTargets.Length == 0)
            {
                return false;
            }
            double2 pos = d.Pos[i];
            int best = -1;
            double bestD = double.MaxValue;
            for (int q = 0; q < d.PolluteTargets.Length; q++)
            {
                float3 pt = d.PolluteTargets[q];
                double dist = math.distance(pos, new double2(pt.x, pt.y));
                if (dist < bestD)
                {
                    bestD = dist;
                    best = q;
                }
            }
            float3 goal = d.PolluteTargets[best];
            double2 at = new double2(goal.x, goal.y);
            if (bestD <= p.PolluteRange)
            {
                c.Pollute = 1;
                c.PolluteT = math.max(0.8f, p.PolluteChannel);
                c.PolluteAt = at;
                c.Anchor = pos;
                d.Clarity[i] = c;
                ClarityCue(ref d, i, 0, c.PolluteT, at, 9, 0);
                return true;
            }
            AirMoveToward(ref d, i, at, speed * dt, p.PolluteRange * 0.8);
            return true;
        }

        /// <summary>空中直线移动（不走格网碰撞、不绕障、不分离）；朝向跟着移动方向。</summary>
        internal static void AirMoveToward(ref CombatData d, int i, double2 target, double stepLen, double stopAt)
        {
            double2 pos = d.Pos[i];
            double2 to = target - pos;
            double len = math.length(to);
            if (len <= stopAt || len < 1e-6)
            {
                return;
            }
            d.Pos[i] = pos + to / len * math.min(stepLen, len - stopAt);
            float4 a = d.Armor[i];
            float2 f = (float2)(to / len);
            d.Armor[i] = new float4(a.x, a.y, f.x, f.y);
        }

        // ─────────────────────────────── 提示 ───────────────────────────────

        private static void ClarityCue(ref CombatData d, int slot, int other, float v, double2 pos, byte code, byte code2)
        {
            int before = d.Cues.Length;
            Cue(ref d, CombatEventKind.ClarityCue, slot, other, v, pos, code);
            if (d.Cues.Length > before)
            {
                CombatEvent e = d.Cues[d.Cues.Length - 1];
                e.Code2 = code2;
                d.Cues[d.Cues.Length - 1] = e;
            }
        }
    }
}
