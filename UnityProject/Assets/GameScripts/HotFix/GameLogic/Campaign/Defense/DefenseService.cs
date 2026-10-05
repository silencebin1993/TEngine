using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>防御建筑操作的失败原因（自检按它断言，界面显示 <see cref="DefenseOpResult.Message"/>）。</summary>
    public enum DefenseFailure : byte
    {
        None = 0,
        NoCampaign,
        NotFound,
        NotTrap,
        NoProfile,
        FirmwareLocked,
        BadFirmware,
        PatternUnknown,
    }

    public readonly struct DefenseOpResult
    {
        public readonly bool Ok;
        public readonly DefenseFailure Failure;
        public readonly string Message;

        private DefenseOpResult(bool ok, DefenseFailure failure, string message)
        {
            Ok = ok;
            Failure = failure;
            Message = message ?? string.Empty;
        }

        public static DefenseOpResult Success(string message) => new DefenseOpResult(true, DefenseFailure.None, message);
        public static DefenseOpResult Fail(DefenseFailure f, string message) => new DefenseOpResult(false, f, message);
    }

    /// <summary>FG6-DEF-02：一座护盾发生器给面板 / 状态行 / 自检看的读数（同一份数据，界面不另算）。</summary>
    public struct ShieldReadout
    {
        public string BuildingId;
        public string Name;
        public bool Built;
        public bool Powered;
        public string StateId;
        public string StateName;
        public string StateDescription;
        public bool Absorbs;
        public float Hp;
        public float MaxHp;
        public float Radius;
        /// <summary>当前状态还有几秒结束（&lt; 0 = 不会自己结束）。</summary>
        public float SecondsLeft;
        public float PowerDemand;
        public float BasePower;
        public float ExtraPower;
        public float LoadDps;
        public double Absorbed;
        public int Hits;
        public int Overloads;
        /// <summary>复审修复：地点护盾数已达上限、这座没登记进内核（不工作）。</summary>
        public bool Capped;
        /// <summary>状态行文本键与分类（fg.TbShieldState.statusKey / statusKind；代码不认状态 ID）。</summary>
        public string StatusKey;
        public string StatusKind;
    }

    /// <summary>FG6-DEF-02：一座陷阱发射器的读数。</summary>
    public struct TrapReadout
    {
        public string BuildingId;
        public string Name;
        public bool Built;
        public string FirmwareId;
        public string FirmwareName;
        public string FieldName;
        public int Pattern;
        public string PatternName;
        public bool Valid;
        public string InvalidReason;
        public string SupplyLine;
        public bool SupplyShort;
        public long Lays;
        public int FluidId;
        public float LitersPerLay;
        /// <summary>复审修复：上一轮因场地已满没铺的块数（0 = 都铺了）。</summary>
        public int FieldsRefused;
        /// <summary>“场地已满”的说明（没满时空串）。</summary>
        public string FieldsFullLine;
    }

    /// <summary>
    /// FG6-DEF-02（FG06 FGR-DEF-010～013；FGT-DEF-002）：屏障、闸门、护盾发生器、陷阱发射器的唯一业务入口。
    ///
    /// - 屏障（FGR-DEF-010）：1×1，三个等级（barrier_t1 / t2 / t3，耐久递增）；建造模式按住拖拽成一段（<see cref="PlanWall"/>，全有或全无，与传送带同一条自动转角路径）。
    ///   建成后是战斗内核里的己方结构单位（敌方弹体打在墙上；己方弹体只与敌对阵营碰撞，己方炮塔隔墙开火）；寻路格网里挡全部移动类别。
    ///   被摧毁 / 还是虚影的屏障不挡路（<see cref="ApplyNavBlockBits"/>）。放置时预览敌方来路的变化（<see cref="PreviewRoutes"/>）。
    /// - 闸门（FGR-DEF-011）：1×1；寻路格网里只挡敌方类别（写死的规则，己方机器通过），对敌人等同于屏障。
    /// - 护盾发生器（FGR-DEF-012）：内核护盾（逐弹体吸收在 Main/Sim）；状态机全部按 fg.TbShieldState（初始 / 吸收 / 时长 / 耗尽 / 断电 / 来电 / 回复 / 耗电倍率）；
    ///   耗电 = 基础 × 状态倍率 + 最近承受的伤害（每秒）× shield.power_per_dps（按档位取整，档位变了才重新结算电网）。
    /// - 陷阱发射器（FGR-DEF-013）：装一枚固件（fg.TbTrapProfile），每 trap.lay_seconds 沿朝向的一条线 / 周围一片铺一轮内核场地（挂固件的状态标签、可带节拍伤害），
    ///   流体走管线消费者（与炮塔同一套），电走电力子网（复用，D-05）。和炮塔组合出阵地反应（油膜带 + 燃迹 = 爆燃 → 燃烧区，FGT-DEF-002）。
    /// 不做玩家没要求的事（FGR-BASE-020）：陷阱只铺玩家装的固件、在玩家摆的朝向上；护盾只按表的状态机运转。推进（<see cref="WorldStep"/>）只看步序号，
    /// 与观察无关（FGR-BASE-021）；热更层每步 O(护盾数)，每拍 O(防御建筑数)，逐弹体 / 逐单位 / 区域节拍在内核。
    /// </summary>
    public static partial class DefenseService
    {
        /// <summary>界面刷新用：任何防御建筑设置 / 状态变化 +1。</summary>
        public static int Revision { get; private set; }

        /// <summary>最近一次操作的反馈（当前语言；面板消息行读它）。</summary>
        public static string LastFeedback { get; private set; } = string.Empty;

        private static void Touch() => Revision++;

        private static void Feedback(string text)
        {
            LastFeedback = text ?? string.Empty;
            Touch();
        }

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static DefenseState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Raids ??= new RaidState();
            s.Raids.Defense ??= new DefenseState();
            return s.Raids.Defense;
        }

        /// <summary>读档 / 新档补全防御域（<see cref="CampaignFgStateDomains.EnsureAll"/> 调）：数组补成空、序号补齐不重复、坏值钳回、重复的建筑 ID 只留第一条。</summary>
        public static void EnsureState(CampaignState s)
        {
            CombatSite.DefenseEvent ??= OnKernelEvent;
            DefenseState st = StateOf(s);
            if (st == null)
            {
                return;
            }
            st.Records ??= Array.Empty<DefenseRecord>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenSerials = new HashSet<int>();
            var keep = new List<DefenseRecord>(st.Records.Length);
            int maxSerial = 0;
            foreach (DefenseRecord r in st.Records)
            {
                if (r == null || string.IsNullOrEmpty(r.BuildingId) || !seenIds.Add(r.BuildingId))
                {
                    continue;
                }
                r.ShieldState ??= string.Empty;
                r.TrapFirmware ??= string.Empty;
                r.TrapPattern = r.TrapPattern == DefenseCatalog.PatternArea ? DefenseCatalog.PatternArea : DefenseCatalog.PatternLine;
                r.ShieldHp = float.IsNaN(r.ShieldHp) || r.ShieldHp < 0f ? 0f : r.ShieldHp;
                r.ShieldLoadBand = Math.Max(0, r.ShieldLoadBand);
                r.ShieldOverloads = Math.Max(0, r.ShieldOverloads);
                r.TrapLays = Math.Max(0, r.TrapLays);
                if (double.IsNaN(r.ShieldAbsorbed) || r.ShieldAbsorbed < 0)
                {
                    r.ShieldAbsorbed = 0;
                }
                if (double.IsNaN(r.ShieldLoadAbsorbed) || r.ShieldLoadAbsorbed < 0)
                {
                    r.ShieldLoadAbsorbed = 0;
                }
                r.HeldFluids ??= Array.Empty<int>();
                r.HeldMl ??= Array.Empty<long>();
                if (r.HeldFluids.Length != r.HeldMl.Length)
                {
                    r.HeldFluids = Array.Empty<int>();
                    r.HeldMl = Array.Empty<long>();
                }
                if (r.ConsumerId < 0)
                {
                    r.ConsumerId = 0;
                }
                if (r.Serial > 0 && !seenSerials.Add(r.Serial))
                {
                    r.Serial = 0;
                }
                maxSerial = Math.Max(maxSerial, r.Serial);
                keep.Add(r);
            }
            if (st.NextSerial <= maxSerial)
            {
                st.NextSerial = maxSerial + 1;
            }
            if (st.NextSerial < 1)
            {
                st.NextSerial = 1;
            }
            foreach (DefenseRecord r in keep)
            {
                if (r.Serial <= 0)
                {
                    r.Serial = st.NextSerial++;
                }
            }
            if (keep.Count != st.Records.Length)
            {
                st.Records = keep.ToArray();
            }
            st.TotalOverloads = Math.Max(0, st.TotalOverloads);
            st.TotalLays = Math.Max(0, st.TotalLays);
        }

        public static IReadOnlyList<DefenseRecord> All(CampaignState s) => StateOf(s)?.Records ?? Array.Empty<DefenseRecord>();

        private static DefenseRecord[] _indexArray;
        private static readonly Dictionary<string, DefenseRecord> IndexById = new Dictionary<string, DefenseRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<int, DefenseRecord> IndexBySerial = new Dictionary<int, DefenseRecord>();

        private static DefenseRecord[] Indexed(CampaignState s)
        {
            DefenseRecord[] all = StateOf(s)?.Records ?? Array.Empty<DefenseRecord>();
            if (!ReferenceEquals(all, _indexArray))
            {
                IndexById.Clear();
                IndexBySerial.Clear();
                foreach (DefenseRecord r in all)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(r.BuildingId) && !IndexById.ContainsKey(r.BuildingId))
                    {
                        IndexById.Add(r.BuildingId, r);
                    }
                    if (r.Serial > 0 && !IndexBySerial.ContainsKey(r.Serial))
                    {
                        IndexBySerial.Add(r.Serial, r);
                    }
                }
                _indexArray = all;
            }
            return all;
        }

        public static DefenseRecord Find(CampaignState s, string buildingId)
        {
            if (s == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            DefenseRecord[] all = Indexed(s);
            if (IndexById.TryGetValue(buildingId, out DefenseRecord hit) && hit.BuildingId == buildingId)
            {
                return hit;
            }
            foreach (DefenseRecord r in all)
            {
                if (r != null && r.BuildingId == buildingId)
                {
                    _indexArray = null;
                    return r;
                }
            }
            return null;
        }

        public static DefenseRecord FindBySerial(CampaignState s, int serial)
        {
            if (s == null || serial <= 0)
            {
                return null;
            }
            DefenseRecord[] all = Indexed(s);
            if (IndexBySerial.TryGetValue(serial, out DefenseRecord hit) && hit.Serial == serial)
            {
                return hit;
            }
            foreach (DefenseRecord r in all)
            {
                if (r != null && r.Serial == serial)
                {
                    _indexArray = null;
                    return r;
                }
            }
            return null;
        }

        public static bool IsDefense(BuildingRecord b) => b != null && DefenseCatalog.IsDefenseType(b.BuildingTypeId);

        /// <summary>这座建筑记录是不是“防御建筑本身”（不是搬迁 / 升级目标虚影）。</summary>
        private static bool IsProper(BuildingRecord b) => IsDefense(b) && !HomeGridService.IsRelocationGhost(b);

        /// <summary>给一座防御建筑补一条记录（放下时 / 对账时 / 复制设置时）。已有记录原样返回。</summary>
        public static DefenseRecord EnsureRecord(CampaignState s, BuildingRecord b)
        {
            if (s == null || !IsProper(b))
            {
                return null;
            }
            DefenseRecord r = Find(s, b.BuildingId);
            if (r != null)
            {
                return r;
            }
            DefenseState st = StateOf(s);
            r = new DefenseRecord
            {
                BuildingId = b.BuildingId,
                Serial = st.NextSerial++,
                TrapPattern = DefenseCatalog.PatternLine,
            };
            st.Records = st.Records.Append(r).ToArray();
            Touch();
            return r;
        }

        /// <summary>
        /// 复审修复（P2：界面读取不改存档）：还没对账到的新建筑在读路径上用这份默认记录（只读，从不写），不分配序号、不追加记录——
        /// 记录顺序与序号只由对账（<see cref="Sync"/>）和玩家操作决定，与“有没有在看”无关（内核单位槽位 / 状态哈希一致）。
        /// </summary>
        private static readonly DefenseRecord EmptyRecord = new DefenseRecord { TrapPattern = DefenseCatalog.PatternLine };

        // ─────────────────────────────── 寻路挡路位（FGR-DEF-010 / 011）───────────────────────────────

        /// <summary>
        /// 寻路推进区块时（<see cref="Nav.NavService"/>）：屏障 / 闸门的挡路位——建成的屏障挡全部类别，建成的闸门只挡敌方类别（写死的规则），
        /// 还是虚影 / 已被摧毁的不挡。<paramref name="bits"/> 按占用编号 − 1 下标（默认全挡）。按建筑记录遍历（刚放下、还没轮到对账补记录的虚影也算），O(建筑数)，只在推进区块时。
        /// </summary>
        public static void ApplyNavBlockBits(CampaignState s, HomeGridMap map, byte[] bits, int count)
        {
            if (s == null || map == null || bits == null || s.BuildingRecords == null)
            {
                return;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b == null || !DefenseCatalog.BlocksMovement(b.BuildingTypeId))
                {
                    continue; // 搬迁 / 升级目标虚影也按“还没建成”不挡路（NavBlockBitsOf 判 IsBuilt）
                }
                int v = map.OccupancyValueOf(b.BuildingId);
                if (v <= 0 || v > count)
                {
                    continue;
                }
                bits[v - 1] = NavBlockBitsOf(b);
            }
        }

        /// <summary>一座屏障 / 闸门此刻的挡路位（不是屏障 / 闸门 = 全挡）。</summary>
        public static byte NavBlockBitsOf(BuildingRecord b)
        {
            if (b == null || !DefenseCatalog.BlocksMovement(b.BuildingTypeId))
            {
                return NavConst.BlockAll;
            }
            if (!IsBuilt(b))
            {
                return 0;
            }
            return DefenseCatalog.KindOf(b.BuildingTypeId) == DefenseKind.Gate ? (byte)(1 << NavConst.ClassHostile) : NavConst.BlockAll;
        }

        // ─────────────────────────────── 陷阱：固件与铺设方式 ───────────────────────────────

        /// <summary>
        /// 一枚固件能不能装进陷阱发射器：在陷阱参数表里（流体类 / 电磁类常规固件）、能装（核心固件 / 未破解固件拒绝，与炮塔同一判定 <see cref="FirmwareKinds.CanInstall"/>）、已解锁。失败给原因（B06）。
        /// </summary>
        public static bool ValidateTrapFirmware(CampaignState s, string firmwareId, out TrapProfileDef profile, out DefenseFailure failure, out string message)
        {
            profile = null;
            failure = DefenseFailure.None;
            message = null;
            string name = FirmwareKinds.DisplayName(firmwareId) ?? firmwareId ?? string.Empty;
            if (!FirmwareKinds.CanInstall(s, firmwareId, FirmwareHost.Trap, out string reasonKey))
            {
                failure = DefenseFailure.BadFirmware;
                message = GameText.Format(reasonKey, name);
                return false;
            }
            if (!DefenseCatalog.TryGetTrap(firmwareId, out profile))
            {
                failure = DefenseFailure.NoProfile;
                message = GameText.Format("defense.reason.no_profile", name);
                return false;
            }
            if (!MechanicalContentUnlock.IsUnlocked(s, firmwareId))
            {
                failure = DefenseFailure.FirmwareLocked;
                message = GameText.Format("defense.reason.firmware_locked", name);
                return false;
            }
            return true;
        }

        /// <summary>能装进陷阱发射器的固件（按表排序）。</summary>
        public static void TrapChoices(CampaignState s, List<string> into)
        {
            into.Clear();
            foreach (TrapProfileDef d in DefenseCatalog.TrapProfiles)
            {
                if (ValidateTrapFirmware(s, d.FirmwareId, out _, out _, out _))
                {
                    into.Add(d.FirmwareId);
                }
            }
        }

        private static DefenseRecord TrapRecord(CampaignState s, string buildingId, out BuildingRecord b, out DefenseOpResult fail)
        {
            fail = default;
            b = HomeGridService.FindBuilding(s, buildingId);
            if (s == null)
            {
                fail = DefenseOpResult.Fail(DefenseFailure.NoCampaign, GameText.Get("defense.reason.no_campaign"));
                return null;
            }
            if (b == null || !IsProper(b))
            {
                fail = DefenseOpResult.Fail(DefenseFailure.NotFound, GameText.Get("defense.reason.not_found"));
                return null;
            }
            if (DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Trap)
            {
                fail = DefenseOpResult.Fail(DefenseFailure.NotTrap, GameText.Get("defense.reason.not_trap"));
                return null;
            }
            return EnsureRecord(s, b);
        }

        /// <summary>给陷阱发射器装一枚固件（立即生效：下一轮按新固件铺；换了流体时旧流体留在发射器里，换回来时放回）。可逆操作，不弹确认（B04）。</summary>
        public static DefenseOpResult TrySetTrapFirmware(CampaignState s, string buildingId, string firmwareId)
        {
            DefenseRecord r = TrapRecord(s, buildingId, out BuildingRecord b, out DefenseOpResult fail);
            if (r == null)
            {
                return fail;
            }
            if (!ValidateTrapFirmware(s, firmwareId, out TrapProfileDef prof, out DefenseFailure f, out string msg))
            {
                return DefenseOpResult.Fail(f, msg);
            }
            if (r.TrapFirmware != firmwareId)
            {
                r.TrapFirmware = firmwareId;
                if (r.ConsumerId > 0 && r.ConsumerFluid != prof.FluidId)
                {
                    ReleaseTrapConsumer(r); // 换了流体：旧消费者撤掉，缓存留在发射器身上
                }
                if (Rt.TryGetValue(r.Serial, out Runtime rt))
                {
                    rt.TrapIssue = TrapSupplyIssue.None;
                    rt.PrevMl = -1;
                }
            }
            string text = GameText.Format("defense.feedback.firmware", BuildingOps.NameOf(b), FirmwareKinds.DisplayName(firmwareId) ?? firmwareId, prof.FieldName);
            Feedback(text);
            return DefenseOpResult.Success(text);
        }

        /// <summary>改铺设方式（0 一条线 / 1 一片区域）。可逆操作，不弹确认（B04）。</summary>
        public static DefenseOpResult TrySetTrapPattern(CampaignState s, string buildingId, int pattern)
        {
            DefenseRecord r = TrapRecord(s, buildingId, out BuildingRecord b, out DefenseOpResult fail);
            if (r == null)
            {
                return fail;
            }
            if (pattern != DefenseCatalog.PatternLine && pattern != DefenseCatalog.PatternArea)
            {
                return DefenseOpResult.Fail(DefenseFailure.PatternUnknown, GameText.Get("defense.reason.pattern_unknown"));
            }
            r.TrapPattern = pattern;
            string text = GameText.Format("defense.feedback.pattern", BuildingOps.NameOf(b), DefenseCatalog.PatternName(pattern));
            Feedback(text);
            return DefenseOpResult.Success(text);
        }

        // ─────────────────────────────── 复制设置（FG3-LOG-07 吸管 / 复制设置 / 布局粘贴）───────────────────────────────

        /// <summary>陷阱发射器的设置编码（<see cref="PlanSettings"/> 的 S1 / S2）：S1 = 固件稳定编号（-1 = 没装），S2 = 铺设方式 + 1。其它防御建筑没有设置。</summary>
        public static bool SettingsOf(CampaignState s, BuildingRecord b, out int s1, out int s2)
        {
            s1 = 0;
            s2 = 0;
            if (!IsProper(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Trap)
            {
                return false;
            }
            DefenseRecord r = Find(s, b.BuildingId);
            if (r == null)
            {
                return true;
            }
            s1 = string.IsNullOrEmpty(r.TrapFirmware) ? -1 : PlanSettings.StableId(r.TrapFirmware);
            s2 = r.TrapPattern + 1;
            return true;
        }

        /// <summary>把设置写到一座陷阱发射器（建成的或虚影）：固件（能装才写）、铺设方式。返回是否写了任何一项。</summary>
        public static bool ApplySettings(CampaignState s, BuildingRecord b, int s1, int s2)
        {
            if (s == null || !IsProper(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Trap)
            {
                return false;
            }
            DefenseRecord r = EnsureRecord(s, b);
            bool applied = false;
            if (s1 > 0)
            {
                string fw = PlanSettings.FirmwareIdOf(s1);
                if (fw != null && ValidateTrapFirmware(s, fw, out TrapProfileDef prof, out _, out _))
                {
                    if (r.TrapFirmware != fw && r.ConsumerId > 0 && r.ConsumerFluid != prof.FluidId)
                    {
                        ReleaseTrapConsumer(r);
                    }
                    r.TrapFirmware = fw;
                    applied = true;
                }
            }
            if (s2 == DefenseCatalog.PatternLine + 1 || s2 == DefenseCatalog.PatternArea + 1)
            {
                r.TrapPattern = s2 - 1;
                applied = true;
            }
            if (applied)
            {
                Touch();
            }
            return applied;
        }

        /// <summary>复制设置状态行里的陷阱设置说明；不是陷阱设置时 null。</summary>
        public static string DescribeSettings(int s1, int s2)
        {
            string fw = PlanSettings.FirmwareIdOf(s1);
            if (fw == null || !DefenseCatalog.TryGetTrap(fw, out TrapProfileDef d) || (s2 != 1 && s2 != 2))
            {
                return null;
            }
            return GameText.Format("defense.feedback.firmware", string.Empty, FirmwareKinds.DisplayName(fw) ?? fw, d.FieldName).TrimStart('：', ':', ' ')
                   + (GameText.Language == GameLanguage.En ? ", " : "，") + DefenseCatalog.PatternShortName(s2 - 1);
        }

        // ─────────────────────────────── 耗电（FGR-DEF-012“耗电随承受的伤害上升”）───────────────────────────────

        /// <summary>
        /// 电网结算时（<see cref="HomeValleyPowerGrid.DemandOf"/>）：护盾发生器的耗电 = 基础 × 状态倍率（fg.TbShieldState.powerMul）+ 额外档位 × shield.power_band
        /// （额外 = 最近承受的伤害每秒 × shield.power_per_dps，上限 shield.power_extra_cap）。其它建筑原样返回。O(1)。
        /// </summary>
        public static float PowerDemandOf(BuildingRecord b, float baseDemand)
        {
            if (b == null || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Shield)
            {
                return baseDemand;
            }
            CampaignState s = CampaignSession.Current;
            DefenseRecord r = s != null ? Find(s, b.BuildingId) : null;
            if (r == null || !DefenseCatalog.TryGetState(r.ShieldState, out ShieldStateDef def))
            {
                return baseDemand;
            }
            return baseDemand * def.PowerMul + ExtraPower(r);
        }

        private static float ExtraPower(DefenseRecord r) => Math.Min(DefenseCatalog.ShieldPowerExtraCap, r.ShieldLoadBand * DefenseCatalog.ShieldPowerBand);

        // ─────────────────────────────── 状态（B05 / B06）───────────────────────────────

        /// <summary>
        /// 防御建筑的功能状态（<see cref="BuildingStatusService"/> 在通用状态之后调：缺电 / 禁用 / 虚影 / 被毁 / 升级中已由通用状态先报）：
        /// 屏障 / 闸门 = 挡路中（耐久）；护盾 = 展开 / 充能 / 过载倒计时 / 重启 / 离线（护盾值）；陷阱 = 铺设中 / 没装固件 / 固件用不了 / 缺流体（缺什么、怎么办）。
        /// </summary>
        public static BuildingStatus StatusOf(CampaignState s, BuildingRecord b)
        {
            DefenseKind kind = b != null ? DefenseCatalog.KindOf(b.BuildingTypeId) : DefenseKind.None;
            DefenseRecord r = b != null && IsProper(b) ? Find(s, b.BuildingId) : null; // 复审修复：读路径不补记录（补记录只在对账 / 玩家操作）
            string hp = Mathf.RoundToInt(DurabilityOf(s, b, r)).ToString(CultureInfo.InvariantCulture);
            string max = Mathf.RoundToInt(b != null ? BuildingOps.MaxDurability(b.BuildingTypeId) : 0f).ToString(CultureInfo.InvariantCulture);
            switch (kind)
            {
                case DefenseKind.Barrier:
                    return new BuildingStatus(BuildingStatusKind.Working, "defense.barrier", GameText.Format("bs.reason.barrier_up", hp, max));
                case DefenseKind.Gate:
                    return new BuildingStatus(BuildingStatusKind.Working, "defense.gate", GameText.Format("bs.reason.gate_up", hp, max));
                case DefenseKind.Shield:
                {
                    if (!TryGetShieldReadout(s, b.BuildingId, out ShieldReadout ro))
                    {
                        return new BuildingStatus(BuildingStatusKind.Idle, "defense.shield", string.Empty);
                    }
                    if (ro.Capped)
                    {
                        return new BuildingStatus(BuildingStatusKind.Idle, "defense.shield.capped", GameText.Format("bs.reason.shield_cap", BinGames.Sim.Combat.CombatConst.MaxShields));
                    }
                    string v = Mathf.RoundToInt(ro.Hp).ToString(CultureInfo.InvariantCulture);
                    string m = Mathf.RoundToInt(ro.MaxHp).ToString(CultureInfo.InvariantCulture);
                    string secs = Mathf.CeilToInt(Mathf.Max(0f, ro.SecondsLeft)).ToString(CultureInfo.InvariantCulture);
                    // 复审修复：文本与分类都按表（fg.TbShieldState.statusKey / statusKind）选，代码不认状态 ID；改表加状态不用改代码。
                    BuildingStatusKind sk = ro.StatusKind == "working" ? BuildingStatusKind.Working : ro.StatusKind == "nopower" ? BuildingStatusKind.NoPower : BuildingStatusKind.Idle;
                    string code = "defense.shield." + (string.IsNullOrEmpty(ro.StateId) ? "charging" : ro.StateId);
                    return new BuildingStatus(sk, code, GameText.Format(string.IsNullOrEmpty(ro.StatusKey) ? "bs.reason.shield_charging" : ro.StatusKey, v, m, secs, HomeValleyPowerGrid.Num(ro.PowerDemand)));
                }
                case DefenseKind.Trap:
                {
                    if (!TryGetTrapReadout(s, b.BuildingId, out TrapReadout tr))
                    {
                        return new BuildingStatus(BuildingStatusKind.Idle, "defense.trap.no_firmware", GameText.Get("bs.reason.trap_no_firmware"));
                    }
                    if (string.IsNullOrEmpty(tr.FirmwareId))
                    {
                        return new BuildingStatus(BuildingStatusKind.Idle, "defense.trap.no_firmware", GameText.Get("bs.reason.trap_no_firmware"));
                    }
                    if (!tr.Valid)
                    {
                        return new BuildingStatus(BuildingStatusKind.Idle, "defense.trap.bad_firmware", GameText.Format("bs.reason.trap_bad_firmware", tr.InvalidReason));
                    }
                    if (tr.SupplyShort && r != null && Rt.TryGetValue(r.Serial, out Runtime rt))
                    {
                        return new BuildingStatus(BuildingStatusKind.NoFluid, "defense.trap.no_supply." + rt.TrapIssue.ToString().ToLowerInvariant(),
                            GameText.Format("bs.reason.trap_no_supply", PipeNetworkService.FluidName(tr.FluidId), TrapIssueText(rt, tr.FluidId)));
                    }
                    string laying = GameText.Format("bs.reason.trap_laying", tr.FieldName, tr.PatternName, tr.SupplyLine);
                    return tr.FieldsRefused > 0
                        ? new BuildingStatus(BuildingStatusKind.Working, "defense.trap.fields_full", laying + "\n" + tr.FieldsFullLine) // 复审修复：场地已满写明原因（B06 / B12）
                        : new BuildingStatus(BuildingStatusKind.Working, "defense.trap.laying", laying);
                }
                default:
                    return new BuildingStatus(BuildingStatusKind.Idle, "defense.unknown", string.Empty);
            }
        }

        /// <summary>防御建筑此刻的耐久（建成且内核里有单位 = 内核读数，否则 = 建筑记录）。</summary>
        public static float DurabilityOf(CampaignState s, BuildingRecord b, DefenseRecord r = null)
        {
            if (b == null)
            {
                return 0f;
            }
            r ??= Find(s, b.BuildingId);
            CombatSite site = HomeSite;
            if (r != null && site != null && site.TryGetDefenseHealth(r.Serial, out float hp, out _, out bool alive) && alive)
            {
                return hp;
            }
            return BuildingOps.Durability(b);
        }

        /// <summary>护盾发生器的读数（面板 / 状态行 / 自检同一份）。</summary>
        public static bool TryGetShieldReadout(CampaignState s, string buildingId, out ShieldReadout ro)
        {
            ro = default;
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            if (b == null || !IsProper(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Shield)
            {
                return false;
            }
            DefenseRecord r = Find(s, b.BuildingId) ?? EmptyRecord; // 复审修复：读路径不补记录（还没对账到的新建筑 = 默认读数）
            ro.BuildingId = buildingId;
            ro.Name = BuildingOps.NameOf(b);
            ro.Built = IsBuilt(b);
            ro.Capped = r.Serial > 0 && Rt.TryGetValue(r.Serial, out Runtime capRt) && capRt.ShieldCapped;
            ro.Powered = b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;
            ro.MaxHp = DefenseCatalog.ShieldCapacity;
            ro.Radius = DefenseCatalog.ShieldRadius;
            ro.Hp = r.ShieldHp;
            ro.Absorbed = r.ShieldAbsorbed;
            CombatSite site = HomeSite;
            if (site != null && site.TryGetShield(r.Serial, out CombatShield sh))
            {
                ro.Hp = sh.Hp;
                ro.MaxHp = sh.MaxHp;
                ro.Radius = sh.Radius;
                ro.Absorbed = sh.Absorbed;
                ro.Hits = sh.Hits;
            }
            ro.Overloads = r.ShieldOverloads;
            ro.StateId = r.ShieldState ?? string.Empty;
            if (DefenseCatalog.TryGetState(r.ShieldState, out ShieldStateDef def))
            {
                ro.StateName = def.Name;
                ro.StateDescription = def.Description;
                ro.StatusKey = def.StatusKey;
                ro.StatusKind = def.StatusKind;
                ro.Absorbs = def.Absorbs;
                ro.SecondsLeft = def.Seconds > 0f && r.ShieldUntilTick > 0 ? Math.Max(0f, (r.ShieldUntilTick - GameClock.Ticks) / (float)Math.Max(1, GameClock.StepHz)) : -1f;
            }
            else if (ro.Built && DefenseCatalog.InitialState is ShieldStateDef init)
            {
                // 刚建成、状态机还没走过一步（例如游戏暂停中）：显示表里的初始状态和它的完整时长，还不吸收。
                ro.StateId = init.Id;
                ro.StateName = init.Name;
                ro.StateDescription = init.Description;
                ro.StatusKey = init.StatusKey;
                ro.StatusKind = init.StatusKind;
                ro.Absorbs = false;
                ro.SecondsLeft = init.Seconds > 0f ? init.Seconds : -1f;
            }
            else
            {
                ro.StateName = string.Empty;
                ro.StateDescription = string.Empty;
                ro.SecondsLeft = -1f;
            }
            ro.BasePower = HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) p) ? p.PowerDemand : 0f;
            ro.ExtraPower = ExtraPower(r);
            ro.PowerDemand = PowerDemandOf(b, ro.BasePower);
            ro.LoadDps = r.Serial > 0 && Rt.TryGetValue(r.Serial, out Runtime rt) ? rt.LoadDps : 0f;
            return true;
        }

        /// <summary>陷阱发射器的读数。</summary>
        public static bool TryGetTrapReadout(CampaignState s, string buildingId, out TrapReadout ro)
        {
            ro = default;
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            if (b == null || !IsProper(b) || DefenseCatalog.KindOf(b.BuildingTypeId) != DefenseKind.Trap)
            {
                return false;
            }
            DefenseRecord r = Find(s, b.BuildingId) ?? EmptyRecord; // 复审修复：读路径不补记录
            ro.BuildingId = buildingId;
            ro.Name = BuildingOps.NameOf(b);
            ro.Built = IsBuilt(b);
            ro.FieldsRefused = r.Serial > 0 && Rt.TryGetValue(r.Serial, out Runtime fullRt) ? fullRt.FieldsRefused : 0;
            ro.FieldsFullLine = ro.FieldsRefused > 0 ? GameText.Format("trap.field_full", ro.FieldsRefused, DefenseCatalog.TrapFieldCapacity) : string.Empty;
            ro.FirmwareId = r.TrapFirmware ?? string.Empty;
            ro.Pattern = r.TrapPattern;
            ro.PatternName = DefenseCatalog.PatternName(r.TrapPattern);
            ro.Lays = r.TrapLays;
            if (string.IsNullOrEmpty(r.TrapFirmware))
            {
                ro.Valid = false;
                ro.InvalidReason = GameText.Get("bs.reason.trap_no_firmware");
                ro.SupplyLine = string.Empty;
                return true;
            }
            ro.FirmwareName = FirmwareKinds.DisplayName(r.TrapFirmware) ?? r.TrapFirmware;
            ro.Valid = ValidateTrapFirmware(s, r.TrapFirmware, out TrapProfileDef prof, out _, out string why);
            ro.InvalidReason = why ?? string.Empty;
            if (prof != null)
            {
                ro.FieldName = prof.FieldName;
                ro.FluidId = prof.FluidId;
                ro.LitersPerLay = prof.LitersPerLay;
            }
            ro.SupplyLine = TrapSupplyLine(s, r, prof);
            ro.SupplyShort = r.Serial > 0 && Rt.TryGetValue(r.Serial, out Runtime rt) && rt.TrapIssue != TrapSupplyIssue.None;
            return true;
        }

        private static string TrapSupplyLine(CampaignState s, DefenseRecord r, TrapProfileDef prof)
        {
            if (prof == null)
            {
                return string.Empty;
            }
            if (prof.FluidId == 0)
            {
                return GameText.Get("trap.supply.power_only");
            }
            float liters = TrapStoredLiters(r, prof.FluidId);
            int lays = Mathf.FloorToInt(liters / Mathf.Max(0.001f, prof.LitersPerLay) + 1e-4f);
            return GameText.Format("trap.supply.line", PipeNetworkService.FluidName(prof.FluidId), Mathf.RoundToInt(liters), lays,
                prof.LitersPerLay.ToString("0.#", CultureInfo.InvariantCulture));
        }

        private static string TrapIssueText(Runtime rt, int fluid)
        {
            string need = PipeNetworkService.FluidName(fluid);
            switch (rt.TrapIssue)
            {
                case TrapSupplyIssue.NoPipe: return GameText.Format("trap.supply.no_pipe", need);
                case TrapSupplyIssue.WrongFluid: return GameText.Format("trap.supply.wrong_fluid", need, PipeNetworkService.FluidName(rt.TrapIssueOther));
                default: return GameText.Format("trap.supply.dry", need);
            }
        }
    }
}
