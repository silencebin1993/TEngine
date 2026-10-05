using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>陷阱发射器缺流体的细分原因（B06）。</summary>
    public enum TrapSupplyIssue : byte
    {
        None = 0,
        NoPipe,
        WrongFluid,
        Dry,
    }

    public static partial class DefenseService
    {
        /// <summary>一座防御建筑的运行时缓存（不存档：读档 / 新会话后重建）。</summary>
        private sealed class Runtime
        {
            public float LastPushedHp = float.NaN;
            public bool BlockingKnown;
            public bool Blocking;
            public TrapSupplyIssue TrapIssue;
            public int TrapIssueOther;
            /// <summary>上一轮结束时缓存里的流体（毫升；-1 = 还没看过）：缓存在涨 = 补给正在进来，不报“缺流体”。</summary>
            public long PrevMl = -1;
            public bool Short;
            public long NextNotifyTick;
            public long NextShieldNotifyTick;
            public float LoadDps;
            /// <summary>复审修复：上一轮铺设被内核拒绝（场地已满）的块数——状态行 / 面板写明原因（B06 / B12“满”）。</summary>
            public int FieldsRefused;
            /// <summary>复审修复：护盾登记被内核拒绝（每个地点护盾数已达上限）——状态行 / 面板写明原因。</summary>
            public bool ShieldCapped;
        }

        private static readonly Dictionary<int, Runtime> Rt = new Dictionary<int, Runtime>();
        private static readonly List<DefenseRecord> ShieldRecords = new List<DefenseRecord>(8);
        private static readonly List<DefenseRecord> TrapRecords = new List<DefenseRecord>(8);
        private static readonly List<GridCell> CellScratch = new List<GridCell>(8);
        private static readonly List<GridCell> RingScratch = new List<GridCell>(16);
        private static readonly HashSet<GridCell> FootScratch = new HashSet<GridCell>();
        private static readonly HashSet<string> IdScratch = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> SerialScratch = new HashSet<int>();
        private static bool _powerDirty;
        private static readonly HashSet<DefenseKind> HookedKinds = new HashSet<DefenseKind>();

        /// <summary>自检读：对账真正执行的次数 / 最近一次对账的步序号 / 铺设轮数（本会话）。</summary>
        public static int SyncCount { get; private set; }
        public static long LastSyncTick { get; private set; } = long.MinValue;
        public static long LayCount { get; private set; }
        /// <summary>自检读：最近一次铺设时被内核拒绝（区域满）的块数。</summary>
        public static int LastRefusedFields { get; private set; }

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时缓存清空（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            Rt.Clear();
            ShieldRecords.Clear();
            TrapRecords.Clear();
            _listsFor = null;
            HookedKinds.Clear();
            _powerDirty = false;
            SyncCount = 0;
            LayCount = 0;
            LastSyncTick = long.MinValue;
            LastFeedback = string.Empty;
            _indexArray = null;
            Touch();
        }

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static bool IsBuilt(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                      && !HomeGridService.IsRelocationGhost(b);

        private static bool IsPowered(BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;

        private static Runtime RuntimeOf(DefenseRecord r)
        {
            if (!Rt.TryGetValue(r.Serial, out Runtime rt))
            {
                rt = new Runtime();
                Rt[r.Serial] = rt;
            }
            return rt;
        }

        /// <summary>
        /// 世界模拟的每个固定步（家园载入时，<see cref="WorldSimulation"/> 在家园内核一步之后调）：护盾状态机每步推进（O(护盾数)，按步序号判定到期，与观察 / 倍速无关）；
        /// 每 defense.sync_seconds 对账一次（O(防御建筑数)）；每 trap.lay_seconds 铺一轮陷阱（O(陷阱数)）。暂停不走。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || worldHz <= 0)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(DefenseCatalog.SyncSeconds * worldHz));
            if (ticksBefore % every == 0)
            {
                Sync(state);
            }
            CombatSite site = HomeSite;
            if (site != null && !site.IsDisposed)
            {
                EnsureLists(state);
                StepShields(state, site);
                long layEvery = Math.Max(1, (long)Math.Round(DefenseCatalog.TrapLaySeconds * worldHz));
                if (ticksBefore % layEvery == 0)
                {
                    LayTraps(state, site);
                }
            }
            if (_powerDirty)
            {
                _powerDirty = false;
                HomeValleyPowerGrid.Recompute(state); // 护盾耗电变了（状态倍率 / 承受伤害的档位）：重新结算电网
            }
        }

        /// <summary>家园战斗内核刚建好 / 从快照恢复之后：绑定事件。结构单位与护盾随快照恢复，其余在下一个整拍对账（旧快照没有护盾表时由状态机按记录重新登记）。</summary>
        public static void RestoreAfterLoad(CampaignState state, CombatSite site)
        {
            CombatSite.DefenseEvent ??= OnKernelEvent;
        }

        /// <summary>
        /// 对账（O(防御建筑数)）：记录 ↔ 建筑（补记录、清掉建筑已不在的记录）、建成的防御建筑在内核里有结构单位（虚影 / 被摧毁的没有）、耐久双向对账、
        /// 屏障 / 闸门挡不挡路变了时让寻路格网重新推进那一块、护盾登记与“最近承受的伤害”档位。正式流程由 <see cref="WorldStep"/> 定时调。
        /// </summary>
        public static void Sync(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            LastSyncTick = GameClock.Ticks;
            SyncCount++;
            CombatSite.DefenseEvent ??= OnKernelEvent;
            CombatSite site = HomeSite;
            BuildingRecord[] buildings = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            DefenseState st = StateOf(state);
            IdScratch.Clear();
            foreach (DefenseRecord r in st.Records)
            {
                if (r != null && !string.IsNullOrEmpty(r.BuildingId))
                {
                    IdScratch.Add(r.BuildingId);
                }
            }
            foreach (BuildingRecord b in buildings)
            {
                if (IsProper(b) && !IdScratch.Contains(b.BuildingId))
                {
                    EnsureRecord(state, b);
                }
            }
            bool anyGone = false;
            foreach (DefenseRecord r in st.Records)
            {
                if (r == null || !IsProper(HomeGridService.FindBuilding(state, r.BuildingId)))
                {
                    anyGone = true;
                    break;
                }
            }
            if (anyGone)
            {
                var keep = new List<DefenseRecord>(st.Records.Length);
                foreach (DefenseRecord r in st.Records)
                {
                    if (r == null || !IsProper(HomeGridService.FindBuilding(state, r.BuildingId)))
                    {
                        if (r != null)
                        {
                            Drop(site, r);
                        }
                        continue;
                    }
                    keep.Add(r);
                }
                st.Records = keep.ToArray();
                Touch();
            }
            _listsFor = null; // 记录可能换了：下面按当前记录重建护盾 / 陷阱清单
            EnsureLists(state);
            if (site == null || site.IsDisposed)
            {
                return;
            }
            // 内核里有、记录里没有的结构单位 / 护盾（读档对账）：拿掉。
            if (site.DefenseUnitCount > 0 || site.ShieldCount > 0)
            {
                SerialScratch.Clear();
                foreach (DefenseRecord r in st.Records)
                {
                    SerialScratch.Add(r.Serial);
                }
                foreach (int serial in site.DefenseSerials())
                {
                    if (!SerialScratch.Contains(serial))
                    {
                        site.RemoveDefenseUnit(serial);
                    }
                }
                foreach (int serial in site.ShieldSerials())
                {
                    if (!SerialScratch.Contains(serial) || !IsShieldRecord(serial))
                    {
                        site.RemoveShield(serial);
                    }
                }
            }
            foreach (DefenseRecord r in st.Records)
            {
                SyncOne(state, site, r, HomeGridService.FindBuilding(state, r.BuildingId));
            }
        }

        private static DefenseRecord[] _listsFor;

        /// <summary>
        /// 护盾 / 陷阱清单（按记录顺序，确定性）：每步的状态机与每轮铺设只遍历它们，不遍历全部防御建筑。记录数组被整体替换（补记录 / 清记录 / 读档）时重建，
        /// 读档后的第一步也已就绪（不等第一次整拍对账）——“读档接着跑”与“不存档一路跑”同一步做同样的事。O(防御建筑数)，只在记录变化时。
        /// </summary>
        private static void EnsureLists(CampaignState state)
        {
            DefenseRecord[] records = StateOf(state)?.Records ?? Array.Empty<DefenseRecord>();
            if (ReferenceEquals(records, _listsFor))
            {
                return;
            }
            _listsFor = records;
            ShieldRecords.Clear();
            TrapRecords.Clear();
            foreach (DefenseRecord r in records)
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                DefenseKind k = b != null ? DefenseCatalog.KindOf(b.BuildingTypeId) : DefenseKind.None;
                if (k == DefenseKind.Shield)
                {
                    ShieldRecords.Add(r);
                }
                else if (k == DefenseKind.Trap)
                {
                    TrapRecords.Add(r);
                }
            }
        }

        private static bool IsShieldRecord(int serial)
        {
            foreach (DefenseRecord r in ShieldRecords)
            {
                if (r.Serial == serial)
                {
                    return true;
                }
            }
            return false;
        }

        private static void SyncOne(CampaignState state, CombatSite site, DefenseRecord r, BuildingRecord b)
        {
            if (r == null || b == null)
            {
                return;
            }
            Runtime rt = RuntimeOf(r);
            DefenseKind kind = DefenseCatalog.KindOf(b.BuildingTypeId);
            bool built = IsBuilt(b);
            if (DefenseCatalog.BlocksMovement(b.BuildingTypeId) && (!rt.BlockingKnown || rt.Blocking != built))
            {
                rt.BlockingKnown = true;
                rt.Blocking = built;
                MarkNavDirty(state, b);
            }
            if (!built)
            {
                // 虚影 / 被摧毁：内核里没有它（护盾收起、状态清掉——重建后从初始状态重新充能）；陷阱的流体留在发射器身上。
                if (site.TryGetDefenseUnit(r.Serial, out _))
                {
                    // 不把内核血量写回：变成虚影 / 受损是别处结算好的（例如 BuildingOps.ApplyDamage 打到 0），记录里的耐久才是真相。
                    site.RemoveDefenseUnit(r.Serial);
                    rt.LastPushedHp = float.NaN;
                }
                if (kind == DefenseKind.Shield)
                {
                    PullShield(site, r);
                    site.RemoveShield(r.Serial);
                    r.ShieldState = string.Empty;
                    r.ShieldHp = 0f;
                    r.ShieldUntilTick = 0;
                    rt.ShieldCapped = false;
                }
                if (kind == DefenseKind.Trap)
                {
                    ReleaseTrapConsumer(r);
                    rt.TrapIssue = TrapSupplyIssue.None;
                    rt.FieldsRefused = 0;
                }
                return;
            }
            float maxHp = BuildingOps.MaxDurability(b.BuildingTypeId);
            if (!site.TryGetDefenseUnit(r.Serial, out _))
            {
                GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g);
                float radius = g != null ? Mathf.Min(g.FootprintW, g.FootprintH) * 0.45f : 0.45f;
                float hp = Mathf.Clamp(BuildingOps.Durability(b), 1f, maxHp);
                site.SpawnDefenseUnit(r.Serial, b.Position, radius, hp, maxHp);
                site.SetUnitLabel(UnitOf(site, r.Serial), "reaction.log.structure_named", BuildingOps.NameOf(b));
                b.Health = hp;
                rt.LastPushedHp = hp;
                RaiseBuiltHook(kind);
            }
            if (site.TryGetDefenseHealth(r.Serial, out float cur, out float kernelMax, out bool alive) && alive)
            {
                // 位置（搬迁完工后换了位置）。
                site.SetDefensePosition(r.Serial, b.Position);
                // 复审修复（P1，FGR-DEF-010 耐久递增）：原地升级完工换了类型（屏障 T1 → T2 / T3），内核上限还是旧等级——按内核此刻的耐久比例换算到新上限并推给内核。
                // 先于下面的双向对账（升级完工时建筑记录的耐久也按比例换算过，但内核读数更新：这 0.5 秒内挨的打不丢）。
                if (Mathf.Abs(kernelMax - maxHp) > 0.01f)
                {
                    float hp = Mathf.Clamp(cur / Mathf.Max(1f, kernelMax) * maxHp, 1f, maxHp);
                    site.SetDefenseHealth(r.Serial, hp, maxHp);
                    rt.LastPushedHp = hp;
                    b.Health = hp;
                    BuildingOps.Touch();
                }
                // 耐久双向对账：建筑记录被别处改过（维修）= 推给内核；否则把内核的受伤写回建筑。
                else if (!float.IsNaN(rt.LastPushedHp) && Mathf.Abs(b.Health - rt.LastPushedHp) > 0.01f)
                {
                    float hp = Mathf.Clamp(b.Health, 1f, maxHp);
                    site.SetDefenseHealth(r.Serial, hp, maxHp);
                    rt.LastPushedHp = hp;
                    b.Health = hp;
                }
                else
                {
                    // FG6-DEF-03（FGR-DEF-014“优先修理正在受攻击的目标”）：内核耐久比上次写回的低 = 这段时间挨过打（与建筑记录比，存档前已写回，读档不改变结果）。
                    if (cur < b.Health - 0.01f)
                    {
                        b.LastHitTick = GameClock.Ticks;
                    }
                    b.Health = cur;
                    rt.LastPushedHp = cur;
                }
            }
            if (kind == DefenseKind.Shield)
            {
                SyncShieldLoad(r, site, rt);
            }
        }

        private static int UnitOf(CombatSite site, int serial) => site.TryGetDefenseUnit(serial, out int u) ? u : 0;

        private static void RaiseBuiltHook(DefenseKind kind)
        {
            if (!HookedKinds.Add(kind))
            {
                return;
            }
            switch (kind)
            {
                case DefenseKind.Barrier: GuidanceHooks.Raise(GuidanceHooks.DefenseBarrierFirstBuilt); break;
                case DefenseKind.Gate: GuidanceHooks.Raise(GuidanceHooks.DefenseGateFirstBuilt); break;
                case DefenseKind.Shield: GuidanceHooks.Raise(GuidanceHooks.DefenseShieldFirstBuilt); break;
                case DefenseKind.Trap: GuidanceHooks.Raise(GuidanceHooks.DefenseTrapFirstBuilt); break;
            }
        }

        private static void PullHealth(CombatSite site, DefenseRecord r, BuildingRecord b, Runtime rt)
        {
            if (site.TryGetDefenseHealth(r.Serial, out float hp, out _, out bool alive) && alive)
            {
                b.Health = hp;
                rt.LastPushedHp = hp;
            }
        }

        private static void PullShield(CombatSite site, DefenseRecord r)
        {
            if (site.TryGetShield(r.Serial, out CombatShield sh))
            {
                r.ShieldHp = sh.Hp;
                r.ShieldAbsorbed = sh.Absorbed;
            }
        }

        /// <summary>
        /// 复审修复（P2，FG06 第 5 节“读档后流场完全一致”）：施工完工的那一刻（<see cref="HomeValleyWorkOrders"/>）屏障 / 闸门立即标脏，下一步开头就挡路——
        /// 读档时寻路镜像按格网实况重建（建成即挡），不存档一路跑也在同一步开始挡，不再差最多一个对账周期。O(占地格数)。
        /// </summary>
        public static void OnConstructionCompleted(CampaignState state, BuildingRecord b)
        {
            if (state == null || b == null || !DefenseCatalog.BlocksMovement(b.BuildingTypeId))
            {
                return;
            }
            DefenseRecord r = Find(state, b.BuildingId);
            if (r != null)
            {
                Runtime rt = RuntimeOf(r);
                rt.BlockingKnown = true;
                rt.Blocking = IsBuilt(b);
            }
            MarkNavDirty(state, b);
        }

        /// <summary>屏障 / 闸门挡不挡路变了：占地格所在区块标成要重新推进寻路镜像（下一步开头生效）。</summary>
        private static void MarkNavDirty(CampaignState state, BuildingRecord b)
        {
            HomeGridMap map = HomeGridService.BoundMap(state);
            if (map == null || b == null || !GridContent.TryGetBuilding(b.BuildingTypeId, out _))
            {
                return;
            }
            CellScratch.Clear();
            HomeGridService.FootprintOf(b, CellScratch);
            foreach (GridCell c in CellScratch)
            {
                map.MarkNavDirtyAt(c);
            }
        }

        /// <summary>拆除 / 记录作废：内核单位与护盾拿掉、管线消费者撤掉（缓存里的流体随拆除丢弃，与炮塔一致）。</summary>
        private static void Drop(CombatSite site, DefenseRecord r)
        {
            site?.RemoveDefenseUnit(r.Serial);
            site?.RemoveShield(r.Serial);
            ReleaseTrapConsumer(r);
            r.HeldFluids = Array.Empty<int>();
            r.HeldMl = Array.Empty<long>();
            Rt.Remove(r.Serial);
        }

        // ─────────────────────────────── 护盾（FGR-DEF-012；状态机 = fg.TbShieldState）───────────────────────────────

        private static void StepShields(CampaignState state, CombatSite site)
        {
            for (int i = 0; i < ShieldRecords.Count; i++)
            {
                DefenseRecord r = ShieldRecords[i];
                BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                if (!IsBuilt(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Shield)
                {
                    continue; // 虚影 / 被摧毁 / 已拆：对账处理
                }
                StepShield(state, site, r, b);
            }
        }

        private static void StepShield(CampaignState state, CombatSite site, DefenseRecord r, BuildingRecord b)
        {
            float cap = DefenseCatalog.ShieldCapacity;
            bool powered = IsPowered(b);
            if (!DefenseCatalog.TryGetState(r.ShieldState, out ShieldStateDef def))
            {
                ShieldStateDef init = DefenseCatalog.InitialState;
                if (init == null)
                {
                    return;
                }
                Enter(state, site, r, b, null, init, powered);
                return;
            }
            if (!site.TryGetShield(r.Serial, out CombatShield sh))
            {
                // 内核里没有（读档的旧快照 / 刚载入 / 之前满了没登记上）：按记录登记（护盾值、状态照旧）。满了（每个地点 CombatConst.MaxShields 座）
                // 就不工作、状态机不往下走，状态行写明原因；有空位的那一步自动登记上（复审修复：不再悄悄停在“充能”）。
                bool ok = site.SetShield(r.Serial, b.Position, DefenseCatalog.ShieldRadius, Mathf.Clamp(r.ShieldHp, 0f, cap), cap, def.Absorbs, powered ? def.RegenPerSec * cap : 0f);
                SetShieldCapped(r, !ok);
                if (!ok || !site.TryGetShield(r.Serial, out sh))
                {
                    return;
                }
            }
            ShieldStateDef next = null;
            bool depleted = false;
            if (!powered && DefenseCatalog.TryGetState(def.OnPowerLost, out ShieldStateDef lost) && lost != def)
            {
                next = lost;
            }
            else if (powered && DefenseCatalog.TryGetState(def.OnPowerBack, out ShieldStateDef back) && back != def)
            {
                next = back;
            }
            else if (def.Absorbs && sh.Active == 0 && DefenseCatalog.TryGetState(def.OnDepleted, out ShieldStateDef dep))
            {
                next = dep;
                depleted = true;
            }
            else if (def.Seconds > 0f && GameClock.Ticks >= r.ShieldUntilTick && DefenseCatalog.TryGetState(def.Next, out ShieldStateDef timed))
            {
                next = timed;
            }
            if (next != null)
            {
                Enter(state, site, r, b, def, next, powered);
                if (depleted)
                {
                    OnOverload(state, r, b, next);
                }
                return;
            }
            float regen = powered ? def.RegenPerSec * cap : 0f;
            Vector2 at = b.Position;
            if (Mathf.Abs(sh.RegenPerSec - regen) > 1e-4f || (sh.Active != 0) != def.Absorbs || Mathf.Abs((float)sh.Pos.x - at.x) > 1e-3f || Mathf.Abs((float)sh.Pos.y - at.y) > 1e-3f)
            {
                site.SetShield(r.Serial, at, sh.Radius > 0f ? sh.Radius : DefenseCatalog.ShieldRadius, sh.Hp, cap, def.Absorbs, regen);
            }
        }

        /// <summary>进入一个状态：记到期步、按表设护盾值、展开 / 收起、回复量；耗电倍率变了标记电网重算。</summary>
        private static void Enter(CampaignState state, CombatSite site, DefenseRecord r, BuildingRecord b, ShieldStateDef from, ShieldStateDef to, bool powered)
        {
            float cap = DefenseCatalog.ShieldCapacity;
            float hp = site.TryGetShield(r.Serial, out CombatShield sh) ? sh.Hp : r.ShieldHp;
            if (to.EnterHp >= 0f)
            {
                hp = cap * to.EnterHp;
            }
            hp = Mathf.Clamp(hp, 0f, cap);
            r.ShieldState = to.Id;
            r.ShieldUntilTick = to.Seconds > 0f ? GameClock.Ticks + Math.Max(1, (long)Math.Round(to.Seconds * GameClock.StepHz)) : 0;
            r.ShieldHp = hp;
            SetShieldCapped(r, !site.SetShield(r.Serial, b.Position, DefenseCatalog.ShieldRadius, hp, cap, to.Absorbs, powered ? to.RegenPerSec * cap : 0f));
            if (from == null || Math.Abs(from.PowerMul - to.PowerMul) > 1e-4f)
            {
                _powerDirty = true;
            }
            BuildingVisualFeed.Mark(b);
            Touch();
        }

        private static void SetShieldCapped(DefenseRecord r, bool capped)
        {
            Runtime rt = RuntimeOf(r);
            if (rt.ShieldCapped != capped)
            {
                rt.ShieldCapped = capped;
                Touch();
            }
        }

        /// <summary>自检 / 状态行读：这座护盾因为地点护盾数已达上限而没登记进内核。</summary>
        public static bool IsShieldCapped(CampaignState s, string buildingId)
        {
            DefenseRecord r = Find(s, buildingId);
            return r != null && Rt.TryGetValue(r.Serial, out Runtime rt) && rt.ShieldCapped;
        }

        /// <summary>护盾耗尽进入过载：计数、警告通知（同一座有冷却、同类聚合，点击定位）、声音与字幕（B07）、首次过载引导钩子。</summary>
        private static void OnOverload(CampaignState state, DefenseRecord r, BuildingRecord b, ShieldStateDef overloaded)
        {
            r.ShieldOverloads++;
            StateOf(state).TotalOverloads++;
            Runtime rt = RuntimeOf(r);
            string text = GameText.Format("shield.notify.overload", BuildingOps.NameOf(b), Mathf.RoundToInt(overloaded.Seconds));
            if (GameClock.Ticks >= rt.NextShieldNotifyTick)
            {
                rt.NextShieldNotifyTick = GameClock.TickAfter(DefenseCatalog.ShieldNotifyCooldownSeconds);
                NotificationCenter.Post("shield_overload", text, new Vector3(b.Position.x, 0f, b.Position.y));
            }
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Failure, text);
            GuidanceHooks.Raise(GuidanceHooks.DefenseShieldFirstOverload);
        }

        /// <summary>“最近承受的伤害（每秒）”：每过一个统计窗口按内核累计吸收量差分一次，换算成额外耗电档位；档位变了标记电网重算。</summary>
        private static void SyncShieldLoad(DefenseRecord r, CombatSite site, Runtime rt)
        {
            if (!site.TryGetShield(r.Serial, out CombatShield sh))
            {
                return;
            }
            r.ShieldAbsorbed = sh.Absorbed;
            r.ShieldHp = sh.Hp;
            int hz = Math.Max(1, GameClock.StepHz);
            long window = Math.Max(1, (long)Math.Round(DefenseCatalog.ShieldLoadWindowSeconds * hz));
            long now = GameClock.Ticks;
            if (r.ShieldLoadTick <= 0 || now < r.ShieldLoadTick || sh.Absorbed < r.ShieldLoadAbsorbed)
            {
                r.ShieldLoadTick = now;
                r.ShieldLoadAbsorbed = sh.Absorbed;
                return;
            }
            if (now - r.ShieldLoadTick < window)
            {
                return;
            }
            float seconds = (now - r.ShieldLoadTick) / (float)hz;
            float dps = (float)((sh.Absorbed - r.ShieldLoadAbsorbed) / Math.Max(1e-3f, seconds));
            rt.LoadDps = dps;
            float extra = Math.Min(DefenseCatalog.ShieldPowerExtraCap, dps * DefenseCatalog.ShieldPowerPerDps);
            int band = extra <= 1e-4f ? 0 : (int)Math.Ceiling(extra / DefenseCatalog.ShieldPowerBand - 1e-6);
            if (band != r.ShieldLoadBand)
            {
                r.ShieldLoadBand = band;
                _powerDirty = true;
                Touch();
            }
            r.ShieldLoadTick = now;
            r.ShieldLoadAbsorbed = sh.Absorbed;
        }

        // ─────────────────────────────── 陷阱（FGR-DEF-013）───────────────────────────────

        private static void LayTraps(CampaignState state, CombatSite site)
        {
            LastRefusedFields = 0;
            for (int i = 0; i < TrapRecords.Count; i++)
            {
                DefenseRecord r = TrapRecords[i];
                BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                if (!IsPowered(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Trap || HomeGridService.IsRelocationGhost(b))
                {
                    continue; // 缺电 / 禁用 / 虚影 / 被毁：通用状态先报原因，不铺也不取流体
                }
                Runtime rt = RuntimeOf(r);
                if (string.IsNullOrEmpty(r.TrapFirmware) || !ValidateTrapFirmware(state, r.TrapFirmware, out TrapProfileDef prof, out _, out _))
                {
                    ReleaseTrapConsumer(r);
                    rt.TrapIssue = TrapSupplyIssue.None;
                    continue;
                }
                if (prof.FluidId > 0)
                {
                    if (!FeedTrap(state, r, b, rt, prof))
                    {
                        continue;
                    }
                }
                else
                {
                    ReleaseTrapConsumer(r);
                    SetTrapIssue(r, rt, TrapSupplyIssue.None, 0);
                }
                Lay(state, site, r, b, prof);
            }
        }

        /// <summary>按铺设方式算出这一轮每块场地的圆心（世界坐标，格 = 米）。线：从发射器沿朝向一串；区域：发射器周围一圈圈。确定性（只看位置、朝向、调参）。</summary>
        public static void FieldCenters(BuildingRecord b, int pattern, List<Vector2> into)
        {
            into.Clear();
            if (b == null)
            {
                return;
            }
            float zr = DefenseCatalog.TrapZoneRadius;
            Vector2 c = b.Position;
            if (pattern == DefenseCatalog.PatternArea)
            {
                float spacing = zr * DefenseCatalog.TrapAreaSpacing;
                float area = DefenseCatalog.TrapAreaRadius;
                into.Add(c);
                for (int ring = 1; ring * spacing <= area + 1e-4f; ring++)
                {
                    int n = Math.Max(6, (int)Math.Round(2f * Mathf.PI * ring));
                    for (int k = 0; k < n; k++)
                    {
                        float a = 2f * Mathf.PI * k / n;
                        into.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (ring * spacing));
                    }
                }
                return;
            }
            Vector2Int d = GridMath.DirVector(GridMath.FacingOf(GridMath.NormalizeRotation(b.Rotation)));
            var dir = new Vector2(d.x, d.y);
            float step = zr * DefenseCatalog.TrapLineSpacing;
            float len = DefenseCatalog.TrapLineLength;
            float offset = DefenseCatalog.TrapLineOffset;
            int count = Math.Max(1, (int)Math.Floor(len / step));
            for (int k = 0; k < count; k++)
            {
                into.Add(c + dir * (offset + step * (k + 0.5f)));
            }
        }

        private static readonly List<Vector2> CenterScratch = new List<Vector2>(32);

        private static void Lay(CampaignState state, CombatSite site, DefenseRecord r, BuildingRecord b, TrapProfileDef prof)
        {
            FieldCenters(b, r.TrapPattern, CenterScratch);
            float statusDps = 0f;
            float slow = 0f;
            float vuln = 0f;
            if (CarrierReadings.TryGetTagEffect(prof.Tag, out _, out string effect, out float amount))
            {
                switch (effect)
                {
                    case "dot": statusDps = amount; break;
                    case "slow": slow = Mathf.Clamp01(amount); break;
                    case "vuln": vuln = Mathf.Max(0f, amount); break;
                }
            }
            float zr = DefenseCatalog.TrapZoneRadius;
            float secs = DefenseCatalog.TrapZoneSeconds;
            int refused = 0;
            foreach (Vector2 p in CenterScratch)
            {
                if (!site.SpawnTrapField(p, zr, secs, prof.Dps, prof.StatusBit, statusDps, slow, vuln))
                {
                    refused++; // 场地已满（陷阱场地自己的上限，不挤读法区域）：这一块不铺
                }
            }
            LastRefusedFields += refused;
            Runtime rt = RuntimeOf(r);
            if (rt.FieldsRefused != refused)
            {
                rt.FieldsRefused = refused;
                Touch(); // 状态行 / 面板写“场地已满，上一轮有 N 块没铺”
            }
            r.TrapLays++;
            StateOf(state).TotalLays++;
            LayCount++;
        }

        /// <summary>
        /// 每轮铺设前从管线取一轮的流体（与炮塔同一套管线消费者：发射器边上找一格装着这种流体的管线挂消费者，缓存 = 发射器存的流体，容量 trap.buffer_lays 轮）。
        /// 不够一轮 → 这一轮不铺，原因（没接管线 / 管线里是别的流体 / 管线里没有了；缓存在涨 = 补给正在进来，不报缺）。返回 true = 取到了。
        /// </summary>
        private static bool FeedTrap(CampaignState state, DefenseRecord r, BuildingRecord b, Runtime rt, TrapProfileDef prof)
        {
            bool pipes = PipeNetworkService.IsRunning;
            int fluid = prof.FluidId;
            long needMl = Math.Max(1, (long)Math.Round(prof.LitersPerLay * 1000.0));
            long capMl = needMl * DefenseCatalog.TrapBufferLays;
            if (r.ConsumerId > 0 && r.ConsumerFluid != fluid)
            {
                ReleaseTrapConsumer(r);
            }
            PerimeterOf(b, RingScratch);
            PipeConsumerInfo info = default;
            bool alive = r.ConsumerId > 0 && pipes && PipeNetworkService.Kernel.TryGetConsumer(r.ConsumerId, out info);
            GridCell? pick = null;
            GridCell? empty = null;
            int other = 0;
            if (pipes)
            {
                foreach (GridCell c in RingScratch)
                {
                    if (!PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ci) || ci.Kind == PipePieceKind.Pump)
                    {
                        continue;
                    }
                    if (ci.Fluid == fluid)
                    {
                        pick = c;
                        break;
                    }
                    if (ci.Fluid == 0)
                    {
                        empty ??= c;
                    }
                    else if (other == 0)
                    {
                        other = ci.Fluid;
                    }
                }
            }
            GridCell? attach = pick ?? empty;
            bool justAttached = false;
            if (alive && (attach == null || attach.Value.X != info.X || attach.Value.Y != info.Y))
            {
                ReleaseTrapConsumer(r);
                alive = false;
            }
            if (!alive && attach != null && pipes)
            {
                long held = TakeHeld(r, fluid);
                int id = PipeNetworkService.Kernel.AddConsumer(attach.Value.X, attach.Value.Y, fluid, DefenseCatalog.TrapSupplyLpm, DefenseCatalog.TrapPipePriority);
                if (id > 0)
                {
                    PipeNetworkService.Kernel.SetConsumerBuffer(id, capMl, Math.Min(capMl, held));
                    AddHeld(r, fluid, Math.Max(0, held - capMl));
                    r.ConsumerId = id;
                    r.ConsumerFluid = fluid;
                    alive = true;
                    justAttached = true;
                }
                else
                {
                    AddHeld(r, fluid, held);
                }
            }
            long buffered = alive ? PipeNetworkService.Kernel.ConsumerBuffer(r.ConsumerId) : HeldOf(r, fluid);
            if (buffered >= needMl)
            {
                long take = needMl;
                if (alive)
                {
                    take -= PipeNetworkService.Kernel.TakeConsumerBuffer(r.ConsumerId, take);
                }
                if (take > 0)
                {
                    AddHeld(r, fluid, -take);
                }
                rt.PrevMl = alive ? PipeNetworkService.Kernel.ConsumerBuffer(r.ConsumerId) : HeldOf(r, fluid);
                SetTrapIssue(r, rt, TrapSupplyIssue.None, 0);
                return true;
            }
            bool flowing = justAttached || rt.PrevMl < 0 || buffered > rt.PrevMl;
            rt.PrevMl = buffered;
            if (!flowing)
            {
                TrapSupplyIssue issue = pick != null || empty != null ? TrapSupplyIssue.Dry : other != 0 ? TrapSupplyIssue.WrongFluid : TrapSupplyIssue.NoPipe;
                SetTrapIssue(r, rt, issue, other);
                if (rt.TrapIssue != TrapSupplyIssue.None && GameClock.Ticks >= rt.NextNotifyTick)
                {
                    rt.NextNotifyTick = GameClock.TickAfter(DefenseCatalog.TrapNotifyCooldownSeconds);
                    NotificationCenter.Post("trap_supply", GameText.Format("trap.supply.notify", BuildingOps.NameOf(b), PipeNetworkService.FluidName(fluid)),
                        new Vector3(b.Position.x, 0f, b.Position.y));
                    GuidanceHooks.Raise(GuidanceHooks.DefenseTrapFirstSupplyShort);
                }
            }
            return false;
        }

        private static void SetTrapIssue(DefenseRecord r, Runtime rt, TrapSupplyIssue issue, int other)
        {
            bool shortNow = issue != TrapSupplyIssue.None;
            if (rt.TrapIssue != issue || rt.Short != shortNow)
            {
                rt.TrapIssue = issue;
                rt.TrapIssueOther = other;
                rt.Short = shortNow;
                if (!shortNow)
                {
                    rt.NextNotifyTick = Math.Min(rt.NextNotifyTick, GameClock.Ticks); // 补给来了：下一次断供照常可以通知（冷却仍按上一条起算）
                }
                Touch();
            }
        }

        /// <summary>占地四周（四邻，不含占地本身）的格子，按占地格顺序、北东南西去重（确定性）。</summary>
        private static void PerimeterOf(BuildingRecord b, List<GridCell> into)
        {
            into.Clear();
            CellScratch.Clear();
            HomeGridService.FootprintOf(b, CellScratch);
            FootScratch.Clear();
            foreach (GridCell c in CellScratch)
            {
                FootScratch.Add(c);
            }
            foreach (GridCell c in CellScratch)
            {
                for (int d = 0; d < 4; d++)
                {
                    GridCell n = d == 0 ? new GridCell(c.X, c.Y + 1) : d == 1 ? new GridCell(c.X + 1, c.Y) : d == 2 ? new GridCell(c.X, c.Y - 1) : new GridCell(c.X - 1, c.Y);
                    if (!FootScratch.Contains(n) && !into.Contains(n))
                    {
                        into.Add(n);
                    }
                }
            }
        }

        /// <summary>撤掉陷阱的管线消费者，缓存里的流体留在发射器身上。</summary>
        private static void ReleaseTrapConsumer(DefenseRecord r)
        {
            if (r == null || r.ConsumerId <= 0)
            {
                return;
            }
            if (PipeNetworkService.IsRunning && PipeNetworkService.Kernel.RemoveConsumer(r.ConsumerId, out long buffered))
            {
                AddHeld(r, r.ConsumerFluid, buffered);
            }
            r.ConsumerId = 0;
            r.ConsumerFluid = 0;
        }

        private static int IndexOf(int[] arr, int v)
        {
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                if (arr[i] == v)
                {
                    return i;
                }
            }
            return -1;
        }

        private static long HeldOf(DefenseRecord r, int fluid)
        {
            int at = IndexOf(r.HeldFluids, fluid);
            return at >= 0 ? r.HeldMl[at] : 0;
        }

        private static long TakeHeld(DefenseRecord r, int fluid)
        {
            long v = HeldOf(r, fluid);
            AddHeld(r, fluid, -v);
            return v;
        }

        private static void AddHeld(DefenseRecord r, int fluid, long delta)
        {
            if (delta == 0 || fluid <= 0)
            {
                return;
            }
            int at = IndexOf(r.HeldFluids, fluid);
            if (at < 0)
            {
                if (delta < 0)
                {
                    return;
                }
                var fl = new int[r.HeldFluids.Length + 1];
                var ml = new long[r.HeldMl.Length + 1];
                Array.Copy(r.HeldFluids, fl, r.HeldFluids.Length);
                Array.Copy(r.HeldMl, ml, r.HeldMl.Length);
                fl[fl.Length - 1] = fluid;
                ml[ml.Length - 1] = delta;
                r.HeldFluids = fl;
                r.HeldMl = ml;
                return;
            }
            r.HeldMl[at] = Math.Max(0, r.HeldMl[at] + delta);
        }

        /// <summary>陷阱发射器某种流体此刻存着多少（升；消费者缓存 + 留在身上的）。</summary>
        public static float TrapStoredLiters(DefenseRecord r, int fluid)
        {
            if (r == null || fluid <= 0)
            {
                return 0f;
            }
            long ml = HeldOf(r, fluid);
            if (r.ConsumerId > 0 && r.ConsumerFluid == fluid && PipeNetworkService.IsRunning)
            {
                ml += PipeNetworkService.Kernel.ConsumerBuffer(r.ConsumerId);
            }
            return ml / 1000f;
        }

        // ─────────────────────────────── 内核事件与存档 ───────────────────────────────

        /// <summary>防御结构单位阵亡（= 建筑被摧毁，FGR-ECO-013：留下虚影，可以重建；陷阱设置保留）：内核单位 / 护盾拿掉、消费者撤掉、寻路格网让开这一格。O(1)。</summary>
        public static void OnKernelEvent(CombatSite site, int serial, CombatEvent e)
        {
            CampaignState state = CampaignSession.Current;
            DefenseRecord r = FindBySerial(state, serial);
            if (state == null || r == null || e.Kind != CombatEventKind.Killed)
            {
                return;
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
            site.RemoveDefenseUnit(serial);
            Runtime rt = RuntimeOf(r);
            rt.LastPushedHp = float.NaN;
            if (b != null && DefenseCatalog.KindOf(b.BuildingTypeId) == DefenseKind.Shield)
            {
                PullShield(site, r);
                site.RemoveShield(serial);
                r.ShieldState = string.Empty;
                r.ShieldHp = 0f;
                r.ShieldUntilTick = 0;
                _powerDirty = true;
            }
            ReleaseTrapConsumer(r);
            if (b != null)
            {
                DestroyBuilding(state, b);
                if (DefenseCatalog.BlocksMovement(b.BuildingTypeId))
                {
                    rt.BlockingKnown = true;
                    rt.Blocking = false;
                    MarkNavDirty(state, b);
                }
            }
            Touch();
        }

        /// <summary>建筑被摧毁：用电的（护盾 / 陷阱）走电网的摧毁入口（电网立即重算）；不用电的（屏障 / 闸门）直接标成受损，不触发电网重算（攻城时一次拆很多段墙）。</summary>
        private static void DestroyBuilding(CampaignState state, BuildingRecord b)
        {
            if (HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
            {
                HomeValleyPowerGrid.ApplyBuildingDestroyed(state, b.BuildingId);
                return;
            }
            if (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled)
            {
                return;
            }
            b.DisabledWhenDestroyed = b.ConstructionState == BuildingConstructionState.Disabled;
            b.ConstructionState = BuildingConstructionState.Damaged;
            b.Health = 0f;
            BuildingVisualFeed.Mark(b);
            BuildingOps.OnBuildingDestroyed(state, b);
        }

        /// <summary>存档前（<see cref="WorldSimulation.SyncAllForSave"/>）：内核里的耐久写回建筑、护盾值 / 累计吸收写回记录（护盾本身随内核快照，流体随管线快照）。</summary>
        public static void WriteTo(CampaignState state)
        {
            CombatSite site = HomeSite;
            if (state == null || site == null || site.IsDisposed)
            {
                return;
            }
            foreach (DefenseRecord r in All(state))
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                if (b == null)
                {
                    continue;
                }
                if (site.TryGetDefenseHealth(r.Serial, out float hp, out _, out bool alive) && alive)
                {
                    b.Health = hp;
                    if (Rt.TryGetValue(r.Serial, out Runtime rt))
                    {
                        rt.LastPushedHp = hp;
                    }
                }
                PullShield(site, r);
            }
        }
    }
}
