using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>炮塔操作的失败原因（自检按它断言，界面显示 <see cref="TurretOpResult.Message"/>）。</summary>
    public enum TurretFailure : byte
    {
        None = 0,
        NoCampaign,
        NotFound,
        BlueprintMissing,
        NotFixedChassis,
        NoPrimary,
        NoOutput,
        SizeMismatch,
        BadFirmware,
        CompileFailed,
        ModeUnknown,
        NoBlueprintForSize,
        NotOperational,
        NoPort,
        SignalBusy,
        SignalInMachine,
        SilentNight,
        NotUplinked,
        NoTarget,
        FireFailed,
        /// <summary>FG6-DEF-01 审查修复（FGR-SIG-053）：炮塔座不在与归还核心连通的信号覆盖里。</summary>
        OutOfCoverage,
    }

    public readonly struct TurretOpResult
    {
        public readonly bool Ok;
        public readonly TurretFailure Failure;
        public readonly string Message;
        public readonly int Count;

        private TurretOpResult(bool ok, TurretFailure failure, string message, int count)
        {
            Ok = ok;
            Failure = failure;
            Message = message ?? string.Empty;
            Count = count;
        }

        public static TurretOpResult Success(string message, int count = 1) => new TurretOpResult(true, TurretFailure.None, message, count);
        public static TurretOpResult Fail(TurretFailure f, string message) => new TurretOpResult(false, f, message, 0);
    }

    /// <summary>FG6-DEF-01：一座炮塔给面板 / 名册 / 自检看的读数（同一份数据，界面不另算）。</summary>
    public struct TurretReadout
    {
        public string BuildingId;
        public string Name;
        public string BlueprintId;
        public string BlueprintName;
        public int BlueprintVersion;
        public int LatestVersion;
        public string Size;
        public int TargetMode;
        public int Kills;
        public int EliteKills;
        public bool Built;
        public bool HasUnit;
        public bool Valid;
        public string InvalidReason;
        public float Health;
        public float MaxHealth;
        public float Heat;
        public float OverheatAt;
        public float RecoverBelow;
        public bool Overheated;
        public float Range;
        public float TurnRate;
        public float DamagePerShot;
        public float Cooldown;
        public bool Projectile;
        public string Carrier;
        public bool HasPort;
        public bool Uplinked;
        public string SupplyLine;
        public bool SupplyShort;
        public float Ammo;
        public BuildingStatus Status;
    }

    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-001～005；FG02 FGR-FW-012；FGT-DEF-001）：炮塔（固定底盘的机器）的唯一业务入口。
    ///
    /// - 数据（FGR-DEF-001）：一座炮塔 = 一座炮塔座建筑（turret_light 2×2 / turret_heavy 3×3：放置、施工、耐久、电力、启停、改名、维修、搬迁、升级全走建筑通用流程）
    ///   + 一条 <see cref="TurretRecord"/>（装的固定底盘蓝图与版本、目标模式、击毁数）。蓝图与机器同一套编译（<see cref="BlueprintCircuitCompiler"/>）、
    ///   同一个翻译（<see cref="CombatSite.MachineWeaponFrom(BlueprintCircuitPreview, bool)"/>）、同一个战斗内核（逐发 / 逐弹体 / 选目标在 Main/Sim/Combat）。
    ///   占地由主作战组件决定（fg.TbTurretProfile.size）：放置时按选中的蓝图选炮塔座；换蓝图只能换同一种炮塔座。
    /// - 射程 / 转速 / 弹速（FGR-DEF-002）：按主组件（fg.TbTurretProfile），T2 / T3 炮塔座射程 × 等级效果值；射弹载体打真实弹体（内核 Projectile 模式，
    ///   弹体只与敌对阵营碰撞 → 己方屏障不挡己方炮塔的弹道）。
    /// - 目标模式（FGR-DEF-003）：五种，内核按模式的比较规则选目标（不做额外判断）；逐座 / 全部 / 同蓝图批量设置。
    /// - 补给（FGR-DEF-004）：电网供电（建筑电力状态不是“吃到电”就停火）；流体类固件每发消耗流体（fg.TbTurretFluid），炮塔边上的管线接一个消费者（缓存 = 炮塔存的流体），
    ///   按对账间隔把整发的补给装进内核（内核一发一扣）。补给不够一发停火，原因写明缺哪种流体、怎么办。
    /// - 接入（FGR-DEF-005）：见 <see cref="TurretUplink"/>。
    /// 不做玩家没要求的事（FGR-BASE-020）：炮塔只按玩家选的模式打射程内的敌人；放下的炮塔装玩家在建造栏选的蓝图（没选过 = 最近一次的 / 默认炮塔蓝图）。
    /// 推进（<see cref="WorldStep"/>）按步序号定时对账，与观察无关（FGR-BASE-021）；热更层每次 O(炮塔数)，逐发 / 逐弹体在内核。
    /// </summary>
    public static partial class TurretService
    {
        public const string DefaultLightBlueprintId = "bp_turret_light";
        public const string DefaultHeavyBlueprintId = "bp_turret_heavy";

        /// <summary>界面刷新用：任何炮塔设置 / 状态变化 +1。</summary>
        public static int Revision { get; private set; }

        /// <summary>最近一次操作的反馈（当前语言；面板消息行读它）。</summary>
        public static string LastFeedback { get; private set; } = string.Empty;

        private static void Touch() => Revision++;

        internal static void Feedback(string text)
        {
            LastFeedback = text ?? string.Empty;
            Touch();
        }

        /// <summary>立即对账一座炮塔（接入 / 离开、反应冷却开始后调用）。</summary>
        public static void Refresh(CampaignState s, string buildingId) => ApplyNow(s, Find(s, buildingId));

        /// <summary>这座炮塔装的蓝图能用、有接入口（接入的前提之一）。</summary>
        public static bool HasUsablePort(CampaignState s, string buildingId)
        {
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            TurretRecord r = IsTurretProper(b) ? Find(s, buildingId) : null;
            if (r == null)
            {
                return false;
            }
            Runtime rt = RuntimeOf(s, r, b);
            return rt.Valid && rt.Board.HasUplink;
        }

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static TurretState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Raids ??= new RaidState();
            s.Raids.Turrets ??= new TurretState();
            return s.Raids.Turrets;
        }

        /// <summary>读档 / 新档补全炮塔域（<see cref="CampaignFgStateDomains.EnsureAll"/> 调）：数组补成空、序号补齐不重复、目标模式钳到 0～4、重复的建筑 ID 只留第一条。</summary>
        public static void EnsureState(CampaignState s)
        {
            // 炮塔的内核事件（阵亡 / 击毁 / 反应发动）交给本服务结算——在任何地点内核走第一步之前绑定（读档 / 新档都先补域）。
            CombatSite.TurretEvent ??= OnKernelEvent;
            TurretState st = StateOf(s);
            if (st == null)
            {
                return;
            }
            st.Turrets ??= Array.Empty<TurretRecord>();
            st.PlacementLight ??= string.Empty;
            st.PlacementHeavy ??= string.Empty;
            st.UplinkTurretId ??= string.Empty;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenSerials = new HashSet<int>();
            var keep = new List<TurretRecord>(st.Turrets.Length);
            int maxSerial = 0;
            foreach (TurretRecord r in st.Turrets)
            {
                if (r == null || string.IsNullOrEmpty(r.BuildingId) || !seenIds.Add(r.BuildingId))
                {
                    continue;
                }
                r.BlueprintId ??= string.Empty;
                r.TargetMode = Math.Max(0, Math.Min(4, r.TargetMode));
                r.KillCount = Math.Max(0, r.KillCount);
                r.EliteKills = Math.Max(0, Math.Min(r.KillCount, r.EliteKills));
                r.ConsumerIds ??= Array.Empty<int>();
                r.ConsumerFluids ??= Array.Empty<int>();
                if (r.ConsumerIds.Length != r.ConsumerFluids.Length)
                {
                    r.ConsumerIds = Array.Empty<int>();
                    r.ConsumerFluids = Array.Empty<int>();
                }
                r.HeldFluids ??= Array.Empty<int>();
                r.HeldMl ??= Array.Empty<long>();
                if (r.HeldFluids.Length != r.HeldMl.Length)
                {
                    r.HeldFluids = Array.Empty<int>();
                    r.HeldMl = Array.Empty<long>();
                }
                if (r.Serial > 0 && !seenSerials.Add(r.Serial))
                {
                    r.Serial = 0; // 重复序号：下面重新编号
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
            foreach (TurretRecord r in keep)
            {
                if (r.Serial <= 0)
                {
                    r.Serial = st.NextSerial++;
                }
            }
            if (keep.Count != st.Turrets.Length)
            {
                st.Turrets = keep.ToArray();
            }
            st.TotalKills = Math.Max(0, st.TotalKills);
        }

        public static IReadOnlyList<TurretRecord> All(CampaignState s) => StateOf(s)?.Turrets ?? Array.Empty<TurretRecord>();

        // FG6-DEF-01 审查修复（每帧 / 每事件的查找不随炮塔数线性增长）：建筑 ID / 序号 → 炮塔记录。记录数组整体替换（补记录、清记录、读档）时按引用失效重建；
        // 命中时核对记录确实是这个 ID / 序号（数组被原位改写也不会读到别的记录），没命中时退回线性扫描（只有查不存在的 ID 才走到）。
        private static TurretRecord[] _indexArray;
        private static readonly Dictionary<string, TurretRecord> IndexById = new Dictionary<string, TurretRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<int, TurretRecord> IndexBySerial = new Dictionary<int, TurretRecord>();

        private static TurretRecord[] Indexed(CampaignState s)
        {
            TurretRecord[] all = StateOf(s)?.Turrets ?? Array.Empty<TurretRecord>();
            if (!ReferenceEquals(all, _indexArray))
            {
                IndexById.Clear();
                IndexBySerial.Clear();
                foreach (TurretRecord r in all)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(r.BuildingId) && !IndexById.ContainsKey(r.BuildingId))
                    {
                        IndexById.Add(r.BuildingId, r); // 与原线性扫描一致：重复的取第一条
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

        public static TurretRecord Find(CampaignState s, string buildingId)
        {
            if (s == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            TurretRecord[] all = Indexed(s);
            if (IndexById.TryGetValue(buildingId, out TurretRecord hit) && hit.BuildingId == buildingId)
            {
                return hit;
            }
            foreach (TurretRecord r in all)
            {
                if (r != null && r.BuildingId == buildingId)
                {
                    _indexArray = null; // 数组被原位改写过：下次重建
                    return r;
                }
            }
            return null;
        }

        public static TurretRecord FindBySerial(CampaignState s, int serial)
        {
            if (s == null || serial <= 0)
            {
                return null;
            }
            TurretRecord[] all = Indexed(s);
            if (IndexBySerial.TryGetValue(serial, out TurretRecord hit) && hit.Serial == serial)
            {
                return hit;
            }
            foreach (TurretRecord r in all)
            {
                if (r != null && r.Serial == serial)
                {
                    _indexArray = null;
                    return r;
                }
            }
            return null;
        }

        public static bool IsTurret(BuildingRecord b) => b != null && TurretCatalog.IsTurretType(b.BuildingTypeId);

        /// <summary>这座建筑记录是不是“炮塔本身”（不是搬迁 / 升级目标虚影）。</summary>
        private static bool IsTurretProper(BuildingRecord b) => IsTurret(b) && !HomeGridService.IsRelocationGhost(b);

        /// <summary>
        /// 给一座炮塔座建筑补一条炮塔记录（放下时 / 对账时 / 布局粘贴后）。已有记录原样返回。新记录装“这种炮塔座最近在建造栏选的蓝图”，
        /// 没选过 / 已不能用时装第一张可用的同类蓝图（默认炮塔蓝图排在前面）。没有可用蓝图时记录照样建（蓝图空），状态写明“没有装炮塔蓝图”。
        /// </summary>
        public static TurretRecord EnsureRecord(CampaignState s, BuildingRecord b)
        {
            if (s == null || !IsTurretProper(b))
            {
                return null;
            }
            TurretRecord r = Find(s, b.BuildingId);
            if (r != null)
            {
                return r;
            }
            TurretState st = StateOf(s);
            string size = TurretCatalog.SizeOfType(b.BuildingTypeId);
            string bp = DefaultBlueprintFor(s, size);
            r = new TurretRecord
            {
                BuildingId = b.BuildingId,
                Serial = st.NextSerial++,
                BlueprintId = bp ?? string.Empty,
                BlueprintVersion = bp != null ? (BlueprintEditorService.FindActiveVersion(s, bp)?.Version ?? 0) : 0,
                TargetMode = (int)CombatTargetMode.Nearest,
            };
            st.Turrets = st.Turrets.Append(r).ToArray();
            Touch();
            return r;
        }

        // ─────────────────────────────── 蓝图 ───────────────────────────────

        /// <summary>
        /// 一张蓝图能不能装上 <paramref name="size"/>（null = 不限）的炮塔座：存在且有保存过的版本、没归档、固定底盘、有能装炮塔的主作战组件（有炮塔参数）、
        /// 固件全部能装进炮塔（核心固件 / 未破解固件 / 没有机器实现的拒绝，FGT-SIG-003）、编译出攻击出口。失败给原因（B06）。
        /// </summary>
        public static bool ValidateBlueprint(CampaignState s, string blueprintId, string size, out BlueprintVersionRecord ver,
            out TurretProfileDef profile, out TurretFailure failure, out string message)
        {
            ver = null;
            profile = null;
            failure = TurretFailure.None;
            message = null;
            BlueprintRecord rec = BlueprintEditorService.Find(s, blueprintId);
            ver = rec != null && !rec.Archived ? BlueprintEditorService.FindActiveVersion(s, blueprintId) : null;
            if (ver == null)
            {
                failure = TurretFailure.BlueprintMissing;
                message = GameText.Get("turret.reason.blueprint_missing");
                return false;
            }
            string name = BlueprintName(rec);
            if (!CarrierReadings.IsFixedChassis(ver.ChassisId))
            {
                failure = TurretFailure.NotFixedChassis;
                message = GameText.Format("turret.reason.not_fixed", name);
                return false;
            }
            if (string.IsNullOrEmpty(ver.PrimaryId) || !TurretCatalog.TryGetProfile(ver.PrimaryId, out profile))
            {
                failure = TurretFailure.NoPrimary;
                message = GameText.Format("turret.reason.no_primary", name);
                return false;
            }
            if (size != null && profile.Size != size)
            {
                failure = TurretFailure.SizeMismatch;
                message = GameText.Format("turret.reason.size_mismatch", name, TurretCatalog.SizeName(profile.Size), TurretCatalog.SizeName(size));
                return false;
            }
            BlueprintCircuitBoard board = BlueprintCircuitBoard.FromVersion(ver);
            var bad = new List<string>(2);
            foreach (string fw in board.FirmwareSlots)
            {
                if (!string.IsNullOrEmpty(fw) && !FirmwareKinds.CanInstall(s, fw, FirmwareHost.Turret, out string reasonKey))
                {
                    bad.Add(GameText.Format(reasonKey, FirmwareKinds.DisplayName(fw) ?? fw));
                }
            }
            if (bad.Count > 0)
            {
                failure = TurretFailure.BadFirmware;
                message = GameText.Format("turret.reason.not_turret_firmware", name, string.Join(GameText.Get("turret.supply.sep"), bad));
                return false;
            }
            foreach (string comp in new[] { ver.PrimaryId, ver.UtilityId })
            {
                if (!string.IsNullOrEmpty(comp) && !CarrierReadings.CanMount(ver.ChassisId, comp, out string mountReason, out _))
                {
                    string cn = MechanicalContentFacade.TryGet(comp, out MechanicalContentDef d) ? d.DisplayName : comp;
                    failure = TurretFailure.CompileFailed;
                    message = GameText.Format("turret.reason.compile_failed", name, GameText.Format(mountReason, cn));
                    return false;
                }
            }
            BlueprintCircuitPreview p = BlueprintCircuitCompiler.CompilePreview(board, s?.RandomSeed ?? 1);
            if (p == null || (!p.HasCombatOutput && !p.HasCannonPrimary))
            {
                failure = TurretFailure.NoOutput;
                message = GameText.Format("turret.reason.no_output", name);
                return false;
            }
            return true;
        }

        public static string BlueprintName(BlueprintRecord rec) => rec == null ? string.Empty : (string.IsNullOrEmpty(rec.DisplayName) ? rec.BlueprintId : rec.DisplayName);

        public static string BlueprintName(CampaignState s, string blueprintId) => BlueprintName(BlueprintEditorService.Find(s, blueprintId));

        /// <summary>能装上 <paramref name="size"/> 炮塔座的蓝图（默认炮塔蓝图在前，其余按名字）。</summary>
        public static void BlueprintChoices(CampaignState s, string size, List<string> into)
        {
            into.Clear();
            if (s?.BlueprintRecords == null)
            {
                return;
            }
            foreach (BlueprintRecord rec in s.BlueprintRecords)
            {
                if (rec != null && ValidateBlueprint(s, rec.BlueprintId, size, out _, out _, out _, out _))
                {
                    into.Add(rec.BlueprintId);
                }
            }
            into.Sort((a, b) =>
            {
                int pa = a == DefaultLightBlueprintId || a == DefaultHeavyBlueprintId ? 0 : 1;
                int pb = b == DefaultLightBlueprintId || b == DefaultHeavyBlueprintId ? 0 : 1;
                return pa != pb ? pa.CompareTo(pb) : string.Compare(BlueprintName(s, a), BlueprintName(s, b), StringComparison.Ordinal);
            });
        }

        /// <summary>放下一座 <paramref name="size"/> 炮塔座时装哪张蓝图：建造栏最近选的（仍可用时），否则第一张可用的。没有 = null。</summary>
        public static string DefaultBlueprintFor(CampaignState s, string size)
        {
            TurretState st = StateOf(s);
            string pick = st == null ? null : (size == TurretCatalog.SizeHeavy ? st.PlacementHeavy : st.PlacementLight);
            if (!string.IsNullOrEmpty(pick) && ValidateBlueprint(s, pick, size, out _, out _, out _, out _))
            {
                return pick;
            }
            var list = new List<string>(4);
            BlueprintChoices(s, size, list);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>建造栏选的炮塔蓝图（按它的主组件记到轻型 / 重型）。不能装炮塔的蓝图拒绝并给原因。</summary>
        public static TurretOpResult SetPlacementBlueprint(CampaignState s, string blueprintId)
        {
            if (s == null)
            {
                return TurretOpResult.Fail(TurretFailure.NoCampaign, GameText.Get("turret.reason.no_campaign"));
            }
            if (!ValidateBlueprint(s, blueprintId, null, out _, out TurretProfileDef prof, out TurretFailure f, out string msg))
            {
                return TurretOpResult.Fail(f, msg);
            }
            TurretState st = StateOf(s);
            if (prof.Size == TurretCatalog.SizeHeavy)
            {
                st.PlacementHeavy = blueprintId;
            }
            else
            {
                st.PlacementLight = blueprintId;
            }
            Touch();
            return TurretOpResult.Success(BlueprintName(s, blueprintId));
        }

        /// <summary>放置校验（<see cref="HomeGridService.ValidatePlacement"/> 调）：这种炮塔座有没有能装的蓝图。没有时给原因（怎么配一张）。</summary>
        public static bool HasPlaceableBlueprint(CampaignState s, string typeId, out GridReason reason)
        {
            reason = default;
            if (!TurretCatalog.IsTurretType(typeId))
            {
                return true;
            }
            EnsureSeeded(s); // 默认炮塔蓝图（幂等）：新档在第一次对账之前就放炮塔也有蓝图可装
            string size = TurretCatalog.SizeOfType(typeId);
            if (DefaultBlueprintFor(s, size) != null)
            {
                return true;
            }
            reason = new GridReason(GridBlockReason.TurretNoBlueprint, "turret.reason.no_blueprint_for_size", size == TurretCatalog.SizeHeavy ? "turret.size.heavy" : "turret.size.light");
            return false;
        }

        /// <summary>给一座炮塔换蓝图（立即生效：下一次对账按新蓝图编译）。只能换同一种炮塔座（B06 写明怎么办）。</summary>
        public static TurretOpResult TryAssignBlueprint(CampaignState s, string buildingId, string blueprintId)
        {
            if (s == null)
            {
                return TurretOpResult.Fail(TurretFailure.NoCampaign, GameText.Get("turret.reason.no_campaign"));
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            TurretRecord r = IsTurretProper(b) ? EnsureRecord(s, b) : null;
            if (r == null)
            {
                return TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found"));
            }
            if (!ValidateBlueprint(s, blueprintId, TurretCatalog.SizeOfType(b.BuildingTypeId), out BlueprintVersionRecord ver, out _, out TurretFailure f, out string msg))
            {
                return TurretOpResult.Fail(f, msg);
            }
            r.BlueprintId = blueprintId;
            r.BlueprintVersion = ver.Version;
            ApplyNow(s, r);
            string text = GameText.Format("turret.feedback.blueprint", BuildingOps.NameOf(b), BlueprintName(s, blueprintId));
            Feedback(text);
            return TurretOpResult.Success(text);
        }

        // ─────────────────────────────── 目标模式 ───────────────────────────────

        /// <summary>逐座设置目标模式（FGR-DEF-003）。可逆操作，不弹确认（B04）。</summary>
        public static TurretOpResult TrySetMode(CampaignState s, string buildingId, int mode)
        {
            if (!TurretCatalog.TryGetMode(mode, out TurretModeDef def))
            {
                return TurretOpResult.Fail(TurretFailure.ModeUnknown, GameText.Get("turret.reason.mode_unknown"));
            }
            BuildingRecord b = HomeGridService.FindBuilding(s, buildingId);
            TurretRecord r = IsTurretProper(b) ? EnsureRecord(s, b) : null;
            if (r == null)
            {
                return TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found"));
            }
            r.TargetMode = mode;
            ApplyNow(s, r);
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstMode);
            string text = GameText.Format("turret.feedback.mode", BuildingOps.NameOf(b), def.Name);
            Feedback(text);
            return TurretOpResult.Success(text);
        }

        /// <summary>批量设置目标模式（FG06 第 4 节“批量设置目标模式”）：<paramref name="sameBlueprintAs"/> 为空 = 全部炮塔；否则只改与这座炮塔装同一张蓝图的。返回改了几座。</summary>
        public static TurretOpResult TrySetModeBatch(CampaignState s, int mode, string sameBlueprintAs = null)
        {
            if (s == null)
            {
                return TurretOpResult.Fail(TurretFailure.NoCampaign, GameText.Get("turret.reason.no_campaign"));
            }
            if (!TurretCatalog.TryGetMode(mode, out TurretModeDef def))
            {
                return TurretOpResult.Fail(TurretFailure.ModeUnknown, GameText.Get("turret.reason.mode_unknown"));
            }
            string bp = null;
            if (!string.IsNullOrEmpty(sameBlueprintAs))
            {
                TurretRecord src = Find(s, sameBlueprintAs);
                if (src == null)
                {
                    return TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found"));
                }
                bp = src.BlueprintId;
            }
            int changed = 0;
            foreach (TurretRecord r in All(s))
            {
                if (r == null || (bp != null && r.BlueprintId != bp) || r.TargetMode == mode)
                {
                    continue;
                }
                r.TargetMode = mode;
                ApplyNow(s, r);
                changed++;
            }
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstMode);
            string text = changed > 0 ? GameText.Format("turret.feedback.mode_batch", changed, def.Name) : GameText.Format("turret.feedback.mode_batch_none", def.Name);
            Feedback(text);
            return TurretOpResult.Success(text, changed);
        }

        // ─────────────────────────────── 复制设置（FG3-LOG-07 吸管 / 复制设置 / 布局粘贴）───────────────────────────────

        /// <summary>炮塔的设置编码（<see cref="PlanSettings"/> 的 S1 / S2）：S1 = 蓝图的稳定编号（-1 = 没装），S2 = 目标模式 + 1。</summary>
        public static void SettingsOf(CampaignState s, BuildingRecord b, out int s1, out int s2)
        {
            s1 = 0;
            s2 = 0;
            TurretRecord r = IsTurretProper(b) ? Find(s, b.BuildingId) : null;
            if (r == null)
            {
                return;
            }
            s1 = string.IsNullOrEmpty(r.BlueprintId) ? -1 : PlanSettings.StableId(r.BlueprintId);
            s2 = r.TargetMode + 1;
        }

        /// <summary>把设置写到一座炮塔（建成的或虚影）：蓝图（同一种炮塔座才写）、目标模式。返回是否写了任何一项。</summary>
        public static bool ApplySettings(CampaignState s, BuildingRecord b, int s1, int s2)
        {
            if (s == null || !IsTurretProper(b))
            {
                return false;
            }
            TurretRecord r = EnsureRecord(s, b);
            bool applied = false;
            if (s1 > 0)
            {
                string bp = BlueprintIdOf(s, s1);
                if (bp != null && ValidateBlueprint(s, bp, TurretCatalog.SizeOfType(b.BuildingTypeId), out BlueprintVersionRecord ver, out _, out _, out _))
                {
                    r.BlueprintId = bp;
                    r.BlueprintVersion = ver.Version;
                    applied = true;
                }
            }
            if (s2 >= 1 && s2 <= 5)
            {
                r.TargetMode = s2 - 1;
                applied = true;
            }
            if (applied)
            {
                ApplyNow(s, r);
            }
            return applied;
        }

        public static string BlueprintIdOf(CampaignState s, int stableId)
        {
            if (stableId <= 0 || s?.BlueprintRecords == null)
            {
                return null;
            }
            foreach (BlueprintRecord rec in s.BlueprintRecords)
            {
                if (rec != null && PlanSettings.StableId(rec.BlueprintId) == stableId)
                {
                    return rec.BlueprintId;
                }
            }
            return null;
        }

        /// <summary>复制设置状态行里的炮塔设置说明（“蓝图 X、模式 Y”）；不是炮塔设置时 null。</summary>
        public static string DescribeSettings(CampaignState s, int s1, int s2)
        {
            string bp = BlueprintIdOf(s, s1);
            if (bp == null || s2 < 1 || s2 > 5)
            {
                return null;
            }
            return GameText.Format("turret.plan.settings", BlueprintName(s, bp), TurretCatalog.ModeName(s2 - 1));
        }

        // ─────────────────────────────── 默认炮塔蓝图 ───────────────────────────────

        /// <summary>新档开局 / 旧档第一次读档：蓝图库里补默认炮塔蓝图——轻型 = 固定底盘 + 连射器 + 寻的（都在基础蓝图库，开局就补）；
        /// 重型 = 固定底盘 + 铸造重炮（重炮解锁之后才补，不提前送出没解锁的组件）。第一次玩的玩家不用先学蓝图编辑器就能放炮塔（自查三问第 1 问）。
        /// 各只补一次（玩家归档 / 改名后不再补）；不带流体类固件（只用电）。</summary>
        public static void EnsureSeeded(CampaignState s)
        {
            TurretState st = StateOf(s);
            if (st == null || (st.DefaultsSeeded && st.HeavyDefaultSeeded))
            {
                return;
            }
            if (!st.DefaultsSeeded)
            {
                Seed(s, DefaultLightBlueprintId, ComponentCatalog.CompGunId, new[] { FirmwareCatalog.FwHomingId }, "turret.bp.light_default");
                st.DefaultsSeeded = true;
                Touch();
            }
            if (!st.HeavyDefaultSeeded && MechanicalContentUnlock.IsUnlocked(s, ComponentCatalog.CompCannonId))
            {
                Seed(s, DefaultHeavyBlueprintId, ComponentCatalog.CompCannonId, Array.Empty<string>(), "turret.bp.heavy_default");
                st.HeavyDefaultSeeded = true;
                Touch();
            }
        }

        private static void Seed(CampaignState s, string id, string primary, string[] firmware, string nameKey)
        {
            s.BlueprintRecords ??= Array.Empty<BlueprintRecord>();
            if (BlueprintEditorService.Find(s, id) != null)
            {
                return;
            }
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(CarrierReadings.FixedChassisId, primary, null, null, firmware);
            BlueprintVersionRecord v = board.ToVersion(1, s.PlaySeconds);
            s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
            {
                BlueprintId = id,
                DisplayName = GameText.Get(nameKey),
                ActiveVersion = 1,
                Archived = false,
                Versions = new[] { v },
            }).ToArray();
        }
    }
}
