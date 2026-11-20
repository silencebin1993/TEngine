using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-05 诱饵终端（FG10 FGR-EVT-040 / 041，FGT-EVT-006；承接 DEBT-FG12EVT01-02 的诱饵部分）。
    /// - 点位：生成器在阵营领地里放的旧终端（<see cref="WorldFeatureKind.Terminal"/>，FG8-GEN-02）里，按（世界种子, 分布格）以 event.decoy_terminal.chance
    ///   确定哪些是诱饵（不改生成器、不进存档，B25）。外观与普通旧终端相同；破绽按种子二选一：周围地面有焦痕（伏击）/ 半埋的地雷（雷区）——悬停写明、主视图有占位外形。
    /// - 扫描：带标记器的机器（任何底盘）走到已发现的诱饵终端 scan_radius 格内即识别（<see cref="WorldStep"/>，每游戏秒）。
    /// - 交互 = 派机器读取（右键，与普通终端同一张工单）。机器到场那一刻（<see cref="OnArrive"/>）：
    ///   带标记器的机器（功能槽声明写 evt.marked，FGR-EMG-013 按端口不按内容 ID）先扫描 → 识别为陷阱（<see cref="OnScanned"/>，工单作废、不触发）；
    ///   没有标记器 → 触发（工单作废、终端作废，排进伏击队列，经事件导演请求 decoy_terminal，trigger=site）。
    /// - 识别后右键 = 安全拆除（同一张工单，工期 / 奖励按拆除），完工给少量技术数据，不触发伏击（FGR-EVT-041）。
    /// - 伏击（导演开始那一步，<see cref="OnStart"/>）：终端周围按所在阵营的突袭编成生成伏击单位（就地交战）；雷区变体先在终端处炸响地雷（圈内单位受击）再出少量单位。
    ///   伏击单位全部被击毁 → 以“完成”结束，导演给三选一数据包（reward=packet）；时限到 → 剩下的撤走、没有奖励。
    /// - 防刷：同一终端只能触发一次（触发即记作已完工，再派单被拒），触发 / 识别 / 拆除状态与伏击记录随存档（<see cref="HomeExplorationState"/>）。
    /// 开销：只在机器到场、导演开始 / 结束、伏击单位被击毁、读档对账时调用；没有每帧逻辑。逐单位的生成 / 计数 / 伤害在战斗内核（Main/Sim）。
    /// </summary>
    public static class DecoyTerminalService
    {
        public const string EventId = "decoy_terminal";
        public const string MarkPort = "evt.marked";
        /// <summary>伏击组键区间 [KeyBase, KeyBase + KeyCount)：在迎战键 [200000, 215000) 与洗礼塔召来的波次 215100 之外，且 ExtKey = −(3000000 + 键×64 + 种类) 在 float 精确范围 2^24 内（键 ≤ 215268，CombatSite.Siege）。</summary>
        public const int KeyBase = 215200;
        public const int KeyCount = 68;
        public const int VariantAmbush = 0;
        public const int VariantMines = 1;
        private const uint SaltDecoy = 0x44434F59u;   // "DCOY"
        private const uint SaltVariant = 0x44435652u; // "DCVR"
        private const uint SaltUnits = 0x4443554Eu;   // "DCUN"

        // ── 会话读数（自检 / 总览）──
        public static int Identified { get; private set; }
        public static int Sprung { get; private set; }
        public static int Disarmed { get; private set; }
        public static int Ambushes { get; private set; }
        public static int Repelled { get; private set; }
        public static int MineHits { get; private set; }
        public static int Respawned { get; private set; }

        public sealed class Handler : EventDirectorHandler
        {
            public override string EventId => DecoyTerminalService.EventId;
            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => DecoyTerminalService.Prepare(s, r);
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => DecoyTerminalService.OnStart(s, r);
            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) => DecoyTerminalService.OnEnd(s, r, outcome);
        }

        public static void ResetSessionState()
        {
            Identified = Sprung = Disarmed = Ambushes = Repelled = MineHits = Respawned = ScanChecks = 0;
            _scannersDirty = true;
            _candidateRevision = -1;
        }

        /// <summary>自检用：临时改调参（如 mine_units = 0 的零伏击分支）；正式运行恒为 null。</summary>
        public static Func<string, float?> TuningOverrideForTests;

        private static float T(string id, float fallback) =>
            TuningOverrideForTests?.Invoke(id) is float o ? o : GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static float Chance => Mathf.Clamp01(T("event.decoy_terminal.chance", 0.3f));
        public static float DisarmSeconds => Mathf.Max(0.1f, T("event.decoy_terminal.disarm_seconds", 10f));
        public static int DisarmTech => Mathf.Max(1, Mathf.RoundToInt(T("event.decoy_terminal.disarm_tech", 5f)));
        public static int AmbushUnits => Mathf.Clamp(Mathf.RoundToInt(T("event.decoy_terminal.ambush_units", 4f)), 1, 12);
        public static int MineUnits => Mathf.Clamp(Mathf.RoundToInt(T("event.decoy_terminal.mine_units", 2f)), 0, 12);
        public static float AmbushRing => Mathf.Max(2f, T("event.decoy_terminal.ambush_ring", 7f));
        public static float MineRadius => Mathf.Max(0.5f, T("event.decoy_terminal.mine_radius", 4f));
        public static float ScanRadius => Mathf.Max(1f, T("event.decoy_terminal.scan_radius", 10f));
        public static float MineDamage => Mathf.Max(0f, T("event.decoy_terminal.mine_damage", 120f));
        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
        private static uint Seed(CampaignState s) => unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));

        // ── 哪些终端是诱饵（纯函数：世界种子 + 分布格，B25）──

        private static WorldFeature Terminal(CampaignState s, string id)
        {
            if (s == null || id == null || !id.StartsWith(WorldFeatures.TerminalPrefix, StringComparison.Ordinal))
            {
                return null;
            }
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            WorldFeature f = ctx != null ? WorldFeatures.FindById(ctx, id) : null;
            return f != null && f.Kind == WorldFeatureKind.Terminal ? f : null;
        }

        /// <summary>这个生成器点位是诱饵终端：阵营领地里的旧终端，按（种子, 分布格）以 chance 抽中。</summary>
        public static bool IsDecoy(CampaignState s, WorldFeature f) =>
            s != null && f != null && f.Kind == WorldFeatureKind.Terminal && !string.IsNullOrEmpty(f.TerritoryId)
            && WorldGenMath.Hash(Seed(s), f.CellX, f.CellY, SaltDecoy) / 4294967296.0 < Chance;

        public static bool IsDecoy(CampaignState s, string terminalId) => IsDecoy(s, Terminal(s, terminalId));

        /// <summary>破绽 / 变体：0 = 焦痕（伏击），1 = 半埋的地雷（雷区）。</summary>
        public static int VariantOf(CampaignState s, WorldFeature f) =>
            f == null ? VariantAmbush : (int)(WorldGenMath.Hash(Seed(s), f.CellX, f.CellY, SaltVariant) & 1u);

        private static HomeExplorationState St(CampaignState s) => HomeExplorationService.StateOf(s);
        private static bool Has(string[] a, string id) => a != null && Array.IndexOf(a, id) >= 0;
        public static bool IsIdentified(CampaignState s, string id) => Has(St(s)?.DecoysIdentified, id);
        public static bool IsDisarmed(CampaignState s, string id) => Has(St(s)?.DecoysDisarmed, id);
        public static bool IsSprung(CampaignState s, string id) => Has(St(s)?.DecoysSprung, id);

        /// <summary>这台机器能扫描：登记的装配里功能槽声明写 evt.marked（标记器；按端口声明，不按组件 ID）。</summary>
        public static bool CanScan(CampaignState s, int machineLogicId) =>
            machineLogicId > 0 && MachineLoadoutRegistry.TryGetUtilityId(s, machineLogicId, out string utility)
            && Emergence.EmgCatalog.OwnerDeclaresWrite(utility, MarkPort);

        // ── 标记器扫描（靠近即识别）──

        /// <summary>每游戏秒一次：带标记器的机器（功能槽声明写 evt.marked）走到已发现、还没被看穿的诱饵终端 scan_radius 格内 → 识别。
        /// 开销 O(候选诱饵数 × 带标记器的机器数)，两者都只在状态变化时重建（探索版本号 / 装配登记变化），与单位总数无关；只看步序号与存档，与观察无关。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            RebuildCandidates(s);
            if (Candidates.Count == 0)
            {
                return;
            }
            RebuildScanners(s);
            if (Scanners.Count == 0)
            {
                return;
            }
            ScanChecks++;
            float r2 = ScanRadius * ScanRadius;
            for (int c = Candidates.Count - 1; c >= 0; c--)
            {
                WorldFeature f = Candidates[c];
                foreach (int id in Scanners)
                {
                    if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || !m.IsAlive || m.IsInFactory || m.RegionId != HomeValleyLayout.RegionId)
                    {
                        continue;
                    }
                    Vector2 p = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
                    if ((p - new Vector2(f.X, f.Y)).sqrMagnitude <= r2)
                    {
                        OnScanned(s, f.Id, id); // 改了探索版本号：下一秒重建候选
                        break;
                    }
                }
            }
        }

        public static int ScanChecks { get; private set; }
        private static readonly List<WorldFeature> Candidates = new List<WorldFeature>(4);
        private static readonly List<int> Scanners = new List<int>(4);
        private static int _candidateRevision = -1;
        private static CampaignState _candidateState;
        private static bool _scannersDirty = true;
        private static bool _hooked;

        private static void RebuildCandidates(CampaignState s)
        {
            if (_candidateRevision == HomeExplorationService.Revision && ReferenceEquals(_candidateState, s))
            {
                return;
            }
            _candidateRevision = HomeExplorationService.Revision;
            _candidateState = s;
            Candidates.Clear();
            foreach (string id in St(s)?.PoisFound ?? Array.Empty<string>())
            {
                WorldFeature f = Terminal(s, id);
                if (IsDecoy(s, f) && !Revealed(s, id) && !HomeExplorationService.IsRecovered(s, id))
                {
                    Candidates.Add(f);
                }
            }
        }

        private static void RebuildScanners(CampaignState s)
        {
            if (!_hooked)
            {
                _hooked = true;
                MachineLoadoutRegistry.Changed += _ => _scannersDirty = true;
            }
            if (!_scannersDirty)
            {
                return;
            }
            _scannersDirty = false;
            Scanners.Clear();
            foreach (int id in MachineLoadoutRegistry.RegisteredMachines)
            {
                if (CanScan(s, id))
                {
                    Scanners.Add(id);
                }
            }
            Scanners.Sort(); // 固定顺序：同一秒两台都在范围内时记先登记的那台
        }

        // ── 交互（机器到场）──

        /// <summary>
        /// 工单到场那一刻（<see cref="HomeValleyWorkOrders.OnArrivedAtWork"/>，进入施工前）。返回 true = 本方法已处理（扫描识别 / 触发陷阱），工单已作废、不进入读取。
        /// 已识别的诱饵照常进入施工（= 安全拆除）。
        /// </summary>
        public static bool OnArrive(CampaignState s, WorkOrderRecord order)
        {
            if (s == null || order == null || order.Kind != WorkOrderKind.Salvage)
            {
                return false;
            }
            WorldFeature f = Terminal(s, order.TargetId);
            if (!IsDecoy(s, f) || IsStaleRead(s, f.Id))
            {
                return false;
            }
            if (IsIdentified(s, f.Id) && !IsSprung(s, f.Id))
            {
                order.Duration = DisarmSeconds; // 赶路途中被（这台或别的标记器）识别：读取单按拆除工期施工
                return false;
            }
            int machine = order.AssignedMachineLogicId;
            Vector2 pos = MachineRegistry.TryGetLivePosition(machine, out Vector2 live) ? live : new Vector2(f.X, f.Y);
            if (!IsSprung(s, f.Id) && CanScan(s, machine))
            {
                OnScanned(s, f.Id, machine);
            }
            else if (!IsSprung(s, f.Id))
            {
                Spring(s, f, machine);
            }
            HomeValleyWorkOrders.CancelOrder(s, order.WorkOrderId, pos); // 已触发过的终端再到场（旧档 / 竞态）：只作废工单，不再触发（防刷）
            return true;
        }

        /// <summary>
        /// 扫描结果入口：诱饵终端被标记器扫描命中 → 识别为陷阱（幂等）。机器到场时带标记器的机器调用；以后有远距扫描（标记器的区域扫描）的也调这一个入口。
        /// 返回 true = 这次新识别出来。
        /// </summary>
        public static bool OnScanned(CampaignState s, string terminalId, int machineLogicId)
        {
            WorldFeature f = Terminal(s, terminalId);
            HomeExplorationState st = St(s);
            if (st == null || !IsDecoy(s, f) || IsIdentified(s, f.Id) || IsSprung(s, f.Id) || IsDisarmed(s, f.Id) || IsStaleRead(s, f.Id))
            {
                return false;
            }
            st.DecoysIdentified = Append(st.DecoysIdentified, f.Id);
            Identified++;
            HomeExplorationService.Touch();
            string who = machineLogicId > 0 ? MachineNaming.Short(machineLogicId) : GameText.Get("decoy_terminal.scanner");
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(f.X, f.Y),
                GameText.Format("decoy_terminal.notify.identified", who, f.X, f.Y, TellText(s, f), Num(DisarmSeconds), DisarmTech));
            return true;
        }

        /// <summary>没有识别就交互：陷阱触发（终端作废、排进伏击队列、请求事件导演）。同一终端只触发一次。</summary>
        private static void Spring(CampaignState s, WorldFeature f, int machineLogicId)
        {
            HomeExplorationState st = St(s);
            if (st == null || IsSprung(s, f.Id))
            {
                return;
            }
            st.DecoysSprung = Append(st.DecoysSprung, f.Id);
            if (!Has(st.PoisDone, f.Id))
            {
                st.PoisDone = Append(st.PoisDone, f.Id); // 作废：不能再读取 / 再触发
            }
            st.DecoyQueue = Append(st.DecoyQueue, f.Id);
            Sprung++;
            HomeExplorationService.Touch();
            string who = machineLogicId > 0 ? MachineNaming.Short(machineLogicId) : GameText.Get("decoy_terminal.scanner");
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, new Vector2(f.X, f.Y),
                GameText.Format(VariantOf(s, f) == VariantMines ? "decoy_terminal.notify.sprung_mines" : "decoy_terminal.notify.sprung", who, f.X, f.Y));
            EventDirectorService.Request(s, EventId, at: new Vector2(f.X, f.Y));
        }

        /// <summary>
        /// 读取 / 拆除单完工（<see cref="HomeExplorationService.CompleteRecovery"/> 的终端分支）。返回 null = 不是诱饵（照普通终端结算）；否则返回发了多少技术数据。
        /// 已识别 → 安全拆除，给少量技术数据（经济账本，事务号 poi-read:ID 天然幂等）；没识别却完工（旧档里读取中的单）→ 当场触发。
        /// </summary>
        public static int? CompleteWork(CampaignState s, string terminalId, int machineLogicId)
        {
            WorldFeature f = Terminal(s, terminalId);
            HomeExplorationState st = St(s);
            if (st == null || !IsDecoy(s, f))
            {
                return null;
            }
            if (IsSprung(s, f.Id) || IsDisarmed(s, f.Id))
            {
                return 0;
            }
            if (!IsIdentified(s, f.Id))
            {
                Spring(s, f, machineLogicId);
                return 0;
            }
            st.DecoysDisarmed = Append(st.DecoysDisarmed, f.Id);
            if (!Has(st.PoisDone, f.Id))
            {
                st.PoisDone = Append(st.PoisDone, f.Id);
            }
            int tech = DisarmTech;
            string tx = HomeExplorationService.TerminalTxPrefix + f.Id;
            int paid = 0;
            if (CampaignEconomyLedger.Find(s, tx) == null)
            {
                CampaignEconomyLedger.ProposeProduce(s, tx, f.Id, CampaignEconomyLedger.ResourceTechData, tech);
                CampaignEconomyLedger.Reserve(s, tx);
                CampaignEconomyLedger.MarkRunning(s, tx);
                if (CampaignEconomyLedger.Commit(s, tx).Success)
                {
                    st.DecoyTechData += tech;
                    paid = tech;
                    HomeInventory.Touch();
                }
            }
            Disarmed++;
            HomeExplorationService.Touch();
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(f.X, f.Y), GameText.Format("decoy_terminal.notify.disarmed", f.X, f.Y, paid));
            return paid;
        }

        // ── 悬停 / 地图文字 ──

        public static string TellText(CampaignState s, WorldFeature f) =>
            GameText.Get(VariantOf(s, f) == VariantMines ? "decoy_terminal.tell.mines" : "decoy_terminal.tell.scorch");

        /// <summary>诱饵终端的悬停标题：识别 / 触发 / 拆除后写“诱饵终端”，之前与普通旧终端一样。</summary>
        public static bool Revealed(CampaignState s, string id) => IsIdentified(s, id) || IsSprung(s, id) || IsDisarmed(s, id);

        /// <summary>旧档升级：本卡之前就已读过（在 PoisDone 里）、又没被触发 / 拆除的终端——按普通的已读终端处理（不扫描、不显示破绽、不改悬停）。</summary>
        public static bool IsStaleRead(CampaignState s, string id) => !Revealed(s, id) && HomeExplorationService.IsRecovered(s, id);

        /// <summary>悬停正文（诱饵终端专用的那一行）；不是诱饵返回 null。<paramref name="available"/> = 普通终端“可读取……”那一行（没识别时照样显示，外加破绽）。</summary>
        public static string HoverLine(CampaignState s, string id, string available)
        {
            WorldFeature f = Terminal(s, id);
            if (!IsDecoy(s, f) || IsStaleRead(s, id))
            {
                return null;
            }
            if (IsSprung(s, id))
            {
                return GameText.Get("decoy_terminal.hover.sprung");
            }
            if (IsDisarmed(s, id))
            {
                return GameText.Format("decoy_terminal.hover.disarmed", DisarmTech);
            }
            if (IsIdentified(s, id))
            {
                return GameText.Format("decoy_terminal.hover.identified", TellText(s, f), Num(DisarmSeconds), DisarmTech) + "\n" + GameText.Get("decoy_terminal.hover.hint");
            }
            return available + "\n" + GameText.Format("decoy_terminal.hover.tell", TellText(s, f));
        }

        /// <summary>地图标签；不是诱饵 / 还没被看穿返回 null（与普通终端一样显示）。</summary>
        public static string MapLabel(CampaignState s, string id)
        {
            if (!Revealed(s, id) || !IsDecoy(s, id))
            {
                return null;
            }
            string key = IsSprung(s, id) ? "ui.map.decoy_terminal.sprung" : IsDisarmed(s, id) ? "ui.map.decoy_terminal.disarmed" : "ui.map.decoy_terminal.identified";
            return GameText.Format(key, GameText.Get("event_director.name.decoy_terminal"));
        }

        // ── 事件导演处理器 ──

        private static bool Prepare(CampaignState s, EventDirectorInstanceRecord r)
        {
            HomeExplorationState st = St(s);
            if (st == null || st.DecoyQueue.Length == 0)
            {
                return false; // 没有已触发、等伏击的终端（不是玩家交互来的请求）：不安排
            }
            WorldFeature f = Terminal(s, st.DecoyQueue[0]);
            st.DecoyQueue = Remove(st.DecoyQueue, 0);
            if (f == null)
            {
                return false;
            }
            r.RegionId = HomeValleyLayout.RegionId; // 领地在星球表面（家园所在的战斗内核）
            r.X = f.X;
            r.Y = f.Y;
            var list = new List<DecoyAmbushRecord>(st.DecoyAmbushes)
            {
                new DecoyAmbushRecord { Serial = r.Serial, TerminalId = f.Id, Variant = VariantOf(s, f), Key = FreeKey(st) },
            };
            st.DecoyAmbushes = list.ToArray();
            return true;
        }

        private static int FreeKey(HomeExplorationState st)
        {
            for (int i = 0; i < KeyCount; i++)
            {
                int k = KeyBase + i;
                bool used = false;
                foreach (DecoyAmbushRecord a in st.DecoyAmbushes)
                {
                    used |= a != null && a.Key == k;
                }
                if (!used)
                {
                    return k;
                }
            }
            return KeyBase;
        }

        public static DecoyAmbushRecord Ambush(CampaignState s, int serial)
        {
            foreach (DecoyAmbushRecord a in St(s)?.DecoyAmbushes ?? Array.Empty<DecoyAmbushRecord>())
            {
                if (a != null && a.Serial == serial)
                {
                    return a;
                }
            }
            return null;
        }

        /// <summary>攻城服务清理无主队伍时问：这个组键是不是进行中的诱饵伏击（<see cref="SiegeService.GroupOwnerOf"/>）。</summary>
        public static bool OwnsKey(CampaignState s, int key)
        {
            if (key < KeyBase || key >= KeyBase + KeyCount)
            {
                return false;
            }
            foreach (DecoyAmbushRecord a in St(s)?.DecoyAmbushes ?? Array.Empty<DecoyAmbushRecord>())
            {
                if (a != null && a.Key == key)
                {
                    return true;
                }
            }
            return false;
        }

        private static void OnStart(CampaignState s, EventDirectorInstanceRecord r)
        {
            DecoyAmbushRecord a = Ambush(s, r.Serial);
            CombatSite site = HomeSite;
            if (a == null || site == null)
            {
                EventDirectorService.Retract(s, r.Serial, "decoy_terminal.block.no_site"); // 星球没载入：撤回成推迟，载入后补上（OnEnd 把终端放回队首）
                return;
            }
            var at = new Vector2(r.X, r.Y);
            if (a.Variant == VariantMines && MineDamage > 0f)
            {
                MineHits += site.StrikeCircle(at, MineRadius, MineDamage * DifficultyService.EnemyDamageMul(s));
            }
            int want = a.Variant == VariantMines ? MineUnits : AmbushUnits;
            a.Unfolded = SpawnSquad(s, site, a, at, want);
            a.Alive = a.Unfolded;
            a.Spawned = true;
            Ambushes++;
            HomeExplorationService.Touch();
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, at,
                GameText.Format(a.Variant == VariantMines ? "decoy_terminal.notify.mines" : "decoy_terminal.notify.ambush", a.Unfolded));
            if (a.Unfolded == 0)
            {
                // 雷区没有伏击单位（mine_units = 0）或内核满：没有要打退的伏击 → 不给三选一（结果“作罢”）。
                EventDirectorService.Complete(s, r.Serial, a.Variant == VariantMines ? EventDirectorService.OutcomeFailed : EventDirectorService.OutcomeCancelled);
            }
        }

        /// <summary>按终端所在阵营的突袭编成（fg.TbRaidUnit，当前幕可出的）在终端周围一圈生成伏击单位，“就地交战”。返回生成数。O(台数)，一次性。</summary>
        private static int SpawnSquad(CampaignState s, CombatSite site, DecoyAmbushRecord a, Vector2 at, int count)
        {
            WorldFeature f = Terminal(s, a.TerminalId);
            string faction = f?.TerritoryId ?? string.Empty;
            int act = SandboxService.EffectiveAct(s);
            var defs = new List<RaidUnitDef>(4);
            RaidUnitDef any = null;
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (u == null || u.Faction != faction)
                {
                    continue;
                }
                any ??= u;
                if (u.MinLevel <= act)
                {
                    defs.Add(u);
                }
            }
            if (defs.Count == 0 && any != null)
            {
                defs.Add(any);
            }
            if (defs.Count == 0 && RaidCatalog.Units.Count > 0)
            {
                defs.Add(RaidCatalog.Units[0]);
            }
            if (defs.Count == 0 || count <= 0)
            {
                return 0;
            }
            uint seed = Seed(s);
            int offset = (int)(WorldGenMath.Hash(seed, a.Serial, 0, SaltUnits) % (uint)defs.Count);
            float ring = AmbushRing;
            float hp = DifficultyService.EnemyHealthMul(s);
            float dmg = DifficultyService.EnemyDamageMul(s);
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                float ang = (i * 360f / count + (WorldGenMath.Hash(seed, a.Serial, 1, SaltUnits) % 360u)) * Mathf.Deg2Rad;
                Vector2 p = at + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                GridCell c = SiegeService.NearestPassable(new GridCell(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)), 6);
                int unit = SiegeService.SpawnOne(site, a.Key, new Vector2(c.X + 0.5f, c.Y + 0.5f), at, defs[(offset + i) % defs.Count], false, hp, dmg);
                if (unit > 0)
                {
                    site.SetSiegeUnitMode(unit, BinGames.Sim.Combat.CombatSiegeMode.Skirmish); // 就地迎战（不走攻城流场去拆家园）
                    Progression.MechanicCodex.OnEnemyEncountered(defs[(offset + i) % defs.Count].EnemyTypeId);
                    placed++;
                }
            }
            return placed;
        }

        /// <summary>伏击组的单位被击毁 / 自毁（<see cref="RaidResultService.OnKilled"/> 开头）：剩余减一，全灭 → 以“完成”结束（导演给三选一）。O(进行中伏击数)。</summary>
        public static void OnGroupLoss(CampaignState s, int key)
        {
            if (s == null || key < KeyBase || key >= KeyBase + KeyCount)
            {
                return;
            }
            DecoyAmbushRecord hit = null;
            foreach (DecoyAmbushRecord a in St(s)?.DecoyAmbushes ?? Array.Empty<DecoyAmbushRecord>())
            {
                if (a != null && a.Key == key && a.Spawned)
                {
                    hit = a;
                }
            }
            if (hit == null)
            {
                return;
            }
            hit.Alive = Math.Max(0, hit.Alive - 1);
            HomeExplorationService.Touch();
            if (hit.Alive == 0)
            {
                Repelled++;
                EventDirectorService.Complete(s, hit.Serial, EventDirectorService.OutcomeSuccess);
            }
        }

        private static void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
        {
            HomeExplorationState st = St(s);
            DecoyAmbushRecord a = Ambush(s, r.Serial);
            if (st == null || a == null)
            {
                return;
            }
            st.DecoyAmbushes = Array.FindAll(st.DecoyAmbushes, x => x != null && x.Serial != r.Serial);
            if (outcome == EventDirectorService.OutcomePostponed)
            {
                // 撤回（星球没载入）：终端放回队首，补上时重新 Prepare（新的序号）。
                var q = new List<string>(st.DecoyQueue);
                q.Insert(0, a.TerminalId);
                st.DecoyQueue = q.ToArray();
            }
            else
            {
                CombatSite site = HomeSite;
                int left = site?.DespawnSiegeGroup(a.Key) ?? 0; // 时限到：剩下的伏击单位撤走（完成时已经没有了）
                if (outcome == EventDirectorService.OutcomeExpired && left > 0)
                {
                    FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(r.X, r.Y), GameText.Format("decoy_terminal.notify.withdrew", left));
                }
            }
            HomeExplorationService.Touch();
            if (st.DecoyQueue.Length > 0)
            {
                EventDirectorService.Request(s, EventId); // 还有排队的终端：再请求一次（地点由 Prepare 按队首终端定）
            }
        }

        /// <summary>
        /// 家园战斗内核打开 / 读档后对账（第一步之前）：进行中的伏击单位随内核快照恢复；内核是重建的（没有快照）、伏击组不在了 → 按剩余台数补回（不能因为读档白拿三选一）。
        /// O(进行中伏击数)。
        /// </summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            int n = 0;
            foreach (DecoyAmbushRecord a in St(s)?.DecoyAmbushes ?? Array.Empty<DecoyAmbushRecord>())
            {
                if (a == null || !a.Spawned || a.Alive <= 0 || site.CountSiegeGroup(a.Key) + site.CountHijackedInGroup(a.Key) > 0)
                {
                    continue; // 组还在（含全员正被劫持：期满那次击毁照常减员）
                }
                EventDirectorInstanceRecord r = null;
                foreach (EventDirectorInstanceRecord x in EventDirectorService.Active(s))
                {
                    r = x != null && x.Serial == a.Serial ? x : r;
                }
                if (r == null)
                {
                    continue;
                }
                int placed = SpawnSquad(s, site, a, new Vector2(r.X, r.Y), a.Alive);
                n += placed;
                a.Alive = placed; // 内核满少放了：按实际台数，打完就算打退
                if (placed == 0)
                {
                    EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeCancelled); // 一台也补不回：作罢、不给三选一（同 OnStart）
                }
            }
            Respawned += n;
            return n;
        }

        // ── 工具 ──

        private static string Num(float v) => v.ToString(v == Mathf.Round(v) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

        private static string[] Append(string[] a, string v)
        {
            var list = new List<string>(a ?? Array.Empty<string>()) { v };
            return list.ToArray();
        }

        private static string[] Remove(string[] a, int index)
        {
            var list = new List<string>(a);
            list.RemoveAt(index);
            return list.ToArray();
        }
    }
}
