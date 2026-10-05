using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>补给不足的细分原因（B06）。</summary>
    public enum TurretSupplyIssue : byte
    {
        None = 0,
        NoPipe,
        WrongFluid,
        Dry,
    }

    public static partial class TurretService
    {
        /// <summary>一座炮塔的运行时缓存（编译结果、炮塔参数、流体需求、补给原因）。不存档：读档 / 换蓝图 / 内容表重载后重建。</summary>
        private sealed class Runtime
        {
            public int Key;
            public BlueprintCircuitBoard Board;
            public BlueprintCircuitPreview Local;
            public BlueprintCircuitPreview Uplinked;
            public int UplinkKey;
            public TurretProfileDef Profile;
            public bool Valid;
            public TurretFailure Failure;
            public string Invalid;
            public readonly List<TurretFluidNeed> Needs = new List<TurretFluidNeed>(2);
            public TurretSupplyIssue Issue;
            public int IssueFluid;
            public int IssueOtherFluid;
            public bool Short;
            public long NextNotifyTick;
            public float LastPushedHp = float.NaN;
            public int Tier;
            public string LabelName;
            public BlueprintCircuitPreview LabelSource;
            /// <summary>上一次对账结束时每种流体的缓存（毫升）：缓存在涨 = 补给正在进来（只是还不够一发），不报“缺补给”。</summary>
            public readonly Dictionary<int, long> PrevMl = new Dictionary<int, long>(2);
            /// <summary>流体需求 / 武器参数按输入缓存（编译结果是同一个对象、模式 / 等级 / 接入 / 补给不变就不重算），对账每次 O(1)。</summary>
            public BlueprintCircuitPreview NeedsFor;
            public BlueprintCircuitPreview WeaponFor;
            public int WeaponMode = -1;
            public float WeaponRangeMul = -1f;
            public bool WeaponSupply;
            public bool WeaponUplinked;
            public bool WeaponSuppress;
            public CombatWeapon Weapon;
            public bool WeaponPushed;
        }

        private static readonly Dictionary<int, Runtime> Rt = new Dictionary<int, Runtime>();
        private static readonly List<GridCell> CellScratch = new List<GridCell>(16);
        private static readonly List<GridCell> RingScratch = new List<GridCell>(24);
        private static readonly HashSet<GridCell> FootScratch = new HashSet<GridCell>();
        private static readonly List<TurretFluidNeed> NeedScratch = new List<TurretFluidNeed>(2);
        private static readonly HashSet<string> IdScratch = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> SerialScratch = new HashSet<int>();
        /// <summary>自检读：最近一次对账时的步序号（只在整拍对账）。</summary>
        public static long LastSyncTick { get; private set; } = long.MinValue;
        private static bool _builtHookRaised;

        /// <summary>自检读：对账真正执行的次数（按步序号定时，暂停不走）。</summary>
        public static int SyncCount { get; private set; }

        /// <summary>新会话（新建 / 读档 / 回主菜单）：运行时缓存清空（存档数据不动）。</summary>
        public static void ResetSessionState()
        {
            Rt.Clear();
            LastSyncTick = long.MinValue;
            _builtHookRaised = false;
            SyncCount = 0;
            LastFeedback = string.Empty;
            TurretUplink.ResetSession();
            Touch();
        }

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>世界模拟的每个固定步（家园载入时，<see cref="WorldSimulation"/> 调）：每 turret.sync_seconds 游戏秒对账一次。只看步序号，与观察无关、暂停不走、倍速按步。</summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || worldHz <= 0)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(TurretCatalog.SyncSeconds * worldHz));
            // FG6-DEF-01 审查修复（P2：存档不改变结果）：对账只在整拍做——读档后的第一步也不例外（原来读档后第一步不管在不在拍上都对账一次，
            // 补给提前装填，“读档接着跑”与“不存档一路跑”分叉）。读档后炮塔需要立即补回的只有不进内核快照的接入态，在家园开内核时补（RestoreAfterLoad）。
            if (ticksBefore % every == 0)
            {
                Sync(state);
            }
            TurretUplink.SimStep(state);
        }

        /// <summary>
        /// 家园战斗内核刚建好 / 从快照恢复之后（<see cref="Regions.HomeValleyController"/> 开内核时调，在任何一步之前）：补回不进内核快照的状态——
        /// 信号在哪座炮塔里（接入态 Possessed，按存档里的 <see cref="TurretState.UplinkTurretId"/>），让读档后的第一步与存档前一样不自动开火。
        /// 炮塔单位 / 武器 / 补给 / 朝向随快照恢复，其余照常在下一个整拍对账。O(1)。
        /// </summary>
        public static void RestoreAfterLoad(CampaignState state, CombatSite site)
        {
            CombatSite.TurretEvent ??= OnKernelEvent;
            string id = TurretUplink.ActiveTurretId(state);
            if (state == null || site == null || site.IsDisposed || string.IsNullOrEmpty(id))
            {
                return;
            }
            TurretRecord r = Find(state, id);
            if (r != null && site.TryGetTurretUnit(r.Serial, out _))
            {
                site.SetTurretFlag(r.Serial, CombatUnitFlags.Possessed, true);
            }
        }

        /// <summary>
        /// 对账（O(炮塔数)）：炮塔记录 ↔ 炮塔座建筑（补记录、清掉建筑已不在的记录）、建成的炮塔在内核里有单位（被摧毁 / 虚影 / 拆除的没有）、
        /// 耐久双向对账（受伤 = 内核 → 建筑；维修 = 建筑 → 内核）、武器参数（蓝图 / 目标模式 / 等级 / 接入 / 补给需求）、能不能开火（建成、没禁用、吃到电、蓝图能用）、管线补给装填。
        /// 正式流程由 <see cref="WorldStep"/> 定时调；操作后要立即生效时（换蓝图、改模式）调 <see cref="ApplyNow"/>。
        /// </summary>
        public static void Sync(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            LastSyncTick = GameClock.Ticks;
            SyncCount++;
            CombatSite.TurretEvent ??= OnKernelEvent;
            EnsureSeeded(state); // 默认炮塔蓝图（只补一次）
            CombatSite site = HomeSite;
            BuildingRecord[] buildings = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            // 1. 记录 ↔ 建筑（按建筑 ID / 序号建一次索引，O(建筑数 + 炮塔数)）
            TurretState st = StateOf(state);
            IdScratch.Clear();
            foreach (TurretRecord r in st.Turrets)
            {
                if (r != null && !string.IsNullOrEmpty(r.BuildingId))
                {
                    IdScratch.Add(r.BuildingId);
                }
            }
            foreach (BuildingRecord b in buildings)
            {
                if (IsTurretProper(b) && !IdScratch.Contains(b.BuildingId))
                {
                    EnsureRecord(state, b);
                }
            }
            bool anyGone = false;
            foreach (TurretRecord r in st.Turrets)
            {
                if (r == null || !IsTurretProper(HomeGridService.FindBuilding(state, r.BuildingId)))
                {
                    anyGone = true;
                    break;
                }
            }
            if (anyGone)
            {
                var keep = new List<TurretRecord>(st.Turrets.Length);
                foreach (TurretRecord r in st.Turrets)
                {
                    if (r == null || !IsTurretProper(HomeGridService.FindBuilding(state, r.BuildingId)))
                    {
                        if (r != null)
                        {
                            Drop(state, site, r);
                        }
                        continue;
                    }
                    keep.Add(r);
                }
                st.Turrets = keep.ToArray();
                Touch();
            }
            if (site == null || site.IsDisposed)
            {
                return;
            }
            // 2. 内核里有、记录里没有的炮塔单位（读档对账）：拿掉。
            if (site.TurretUnitCount > 0)
            {
                SerialScratch.Clear();
                foreach (TurretRecord r in st.Turrets)
                {
                    SerialScratch.Add(r.Serial);
                }
                foreach (int serial in site.TurretSerials())
                {
                    if (!SerialScratch.Contains(serial))
                    {
                        site.RemoveTurretUnit(serial);
                    }
                }
            }
            // 3. 逐座对账
            foreach (TurretRecord r in st.Turrets)
            {
                SyncOne(state, site, r, HomeGridService.FindBuilding(state, r.BuildingId));
            }
        }

        /// <summary>改了某座炮塔的设置之后立即对账这一座（不等下一次定时对账）。</summary>
        private static void ApplyNow(CampaignState state, TurretRecord r)
        {
            Touch();
            CombatSite site = HomeSite;
            if (state == null || r == null || site == null || site.IsDisposed)
            {
                return;
            }
            SyncOne(state, site, r, HomeGridService.FindBuilding(state, r.BuildingId));
        }

        private static bool IsBuilt(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                      && !HomeGridService.IsRelocationGhost(b);

        private static Runtime RuntimeOf(CampaignState state, TurretRecord r, BuildingRecord b)
        {
            // FG6-DEF-01 审查修复（P2）：键里含当前语言——“蓝图用不了”的原因（rt.Invalid）是按当前语言格式化好的文本，切语言后要重建。
            int key = HashCode.Combine(r.BlueprintId, r.BlueprintVersion, b.BuildingTypeId, FirmwareKinds.Revision, TurretCatalog.Revision, state.RandomSeed, (int)GameText.Language);
            if (Rt.TryGetValue(r.Serial, out Runtime rt) && rt.Key == key)
            {
                return rt;
            }
            Runtime old = rt;
            rt = new Runtime
            {
                Key = key,
                LastPushedHp = old?.LastPushedHp ?? float.NaN,
                NextNotifyTick = old?.NextNotifyTick ?? 0,
                Short = old?.Short ?? false,
                // 管线这边的处境与蓝图 / 语言无关：上一次对账的缓存量与缺补给原因带过来（下一次对账照常重算），重建不改变“补给在不在进来”的判断。
                Issue = old?.Issue ?? TurretSupplyIssue.None,
                IssueFluid = old?.IssueFluid ?? 0,
                IssueOtherFluid = old?.IssueOtherFluid ?? 0,
            };
            if (old != null)
            {
                foreach (KeyValuePair<int, long> kv in old.PrevMl)
                {
                    rt.PrevMl[kv.Key] = kv.Value;
                }
            }
            Rt[r.Serial] = rt;
            string size = TurretCatalog.SizeOfType(b.BuildingTypeId);
            if (string.IsNullOrEmpty(r.BlueprintId))
            {
                rt.Valid = false;
                rt.Failure = TurretFailure.BlueprintMissing;
                rt.Invalid = null;
                return rt;
            }
            BlueprintRecord rec = BlueprintEditorService.Find(state, r.BlueprintId);
            BlueprintVersionRecord ver = null;
            if (rec?.Versions != null)
            {
                foreach (BlueprintVersionRecord v in rec.Versions)
                {
                    if (v != null && v.Version == r.BlueprintVersion)
                    {
                        ver = v;
                    }
                }
            }
            // 装的版本还在就按装的版本；没有（旧档 / 被删）时退回当前活动版本——炮塔记录的版本号随之更新。
            if (ver == null && rec != null && !rec.Archived)
            {
                ver = BlueprintEditorService.FindActiveVersion(state, r.BlueprintId);
                if (ver != null)
                {
                    r.BlueprintVersion = ver.Version;
                }
            }
            if (ver == null || !CarrierReadings.IsFixedChassis(ver.ChassisId) || !TurretCatalog.TryGetProfile(ver.PrimaryId, out TurretProfileDef prof) || prof.Size != size)
            {
                // 用同一个校验给出原因（版本缺失 / 不是固定底盘 / 没有主组件 / 占地不符）。
                ValidateBlueprint(state, r.BlueprintId, size, out _, out _, out TurretFailure f, out string msg);
                rt.Valid = false;
                rt.Failure = f == TurretFailure.None ? TurretFailure.BlueprintMissing : f;
                rt.Invalid = msg ?? GameText.Get("turret.reason.blueprint_missing");
                return rt;
            }
            rt.Profile = prof;
            rt.Board = BlueprintCircuitBoard.FromVersion(ver);
            foreach (string fw in rt.Board.FirmwareSlots)
            {
                if (!string.IsNullOrEmpty(fw) && !FirmwareKinds.CanInstall(state, fw, FirmwareHost.Turret, out string reasonKey))
                {
                    rt.Valid = false;
                    rt.Failure = TurretFailure.BadFirmware;
                    rt.Invalid = GameText.Format("turret.reason.not_turret_firmware", BlueprintName(rec), GameText.Format(reasonKey, FirmwareKinds.DisplayName(fw) ?? fw));
                    return rt;
                }
            }
            rt.Local = BlueprintCircuitCompiler.CompilePreview(rt.Board, state.RandomSeed);
            if (rt.Local == null || (!rt.Local.HasCombatOutput && !rt.Local.HasCannonPrimary))
            {
                rt.Valid = false;
                rt.Failure = TurretFailure.NoOutput;
                rt.Invalid = GameText.Format("turret.reason.no_output", BlueprintName(rec));
                return rt;
            }
            rt.Valid = true;
            rt.Failure = TurretFailure.None;
            rt.Invalid = null;
            return rt;
        }

        /// <summary>当前生效的编译结果：信号在这座炮塔里 = 接入态（信号核固件插进接入口），否则 = 本地配置（AI 驾驶）。</summary>
        private static BlueprintCircuitPreview ActivePreview(CampaignState state, TurretRecord r, Runtime rt)
        {
            if (!rt.Valid)
            {
                return null;
            }
            if (!TurretUplink.IsUplinkedTo(state, r.BuildingId) || !rt.Board.HasUplink)
            {
                return rt.Local;
            }
            int key = HashCode.Combine(SignalCoreService.Revision, rt.Key);
            if (rt.Uplinked == null || rt.UplinkKey != key)
            {
                rt.Uplinked = UplinkCompiler.CompileUplinked(rt.Board, SignalCoreService.ActiveContentIds(state), state.RandomSeed);
                rt.UplinkKey = key;
            }
            return rt.Uplinked;
        }

        /// <summary>
        /// FG6-DEF-01：炮塔的武器参数 = 机器同一个翻译（<see cref="CombatSite.MachineWeaponFrom(BlueprintCircuitPreview, bool)"/>：伤害、冷却、积热、载体与固件读法）
        /// + 炮塔参数（射程 × 等级、转速、目标模式、每发补给）。射弹载体打真实弹体（Projectile 模式；读法在命中时结算，伤害倍率折进单发伤害，与“每发伤害”同源）；
        /// 接入后的重炮保留两段式瞄准（与机器直控同一规则，熔穿过载照常）。
        /// </summary>
        public static CombatWeapon BuildWeapon(BlueprintCircuitPreview p, TurretProfileDef prof, float rangeMul, int mode, bool needsSupply, bool uplinked, bool suppressReaction)
        {
            CombatWeapon w = CombatSite.MachineWeaponFrom(p, suppressReaction);
            w.TargetMode = (CombatTargetMode)Mathf.Clamp(mode, 0, 4);
            w.Range = prof.Range * Mathf.Max(0.1f, rangeMul);
            w.TurnRate = prof.TurnRate;
            w.AmmoPerShot = needsSupply ? 1f : 0f;
            if (p.HasCannonPrimary)
            {
                w.HasOutput = 1;
            }
            bool keepCannon = uplinked && w.Mode == CombatWeaponMode.Cannon;
            if (prof.FiresProjectiles && !keepCannon)
            {
                float scale = w.Reading.DamageScale > 0f ? w.Reading.DamageScale : 1f;
                w.Damage = Mathf.Max(0f, w.Damage) * scale;
                w.Reading.DamageScale = 1f;
                w.Mode = CombatWeaponMode.Projectile;
                w.AimSeconds = 0f;
                w.ProjectileSpeed = prof.ProjectileSpeed;
                w.ProjectileRadius = prof.ProjectileRadius;
                w.ProjectileLife = 0f;
                w.Reaction = CombatReaction.None;
                w.OverloadExtraHeat = 0f;
            }
            return w;
        }

        private static float RangeMultiplier(BuildingRecord b)
        {
            GameConfig.fg.BuildingTier t = BuildingOps.TierRow(b.BuildingTypeId, BuildingOps.TierOf(b));
            return t != null && t.Value > 0f ? t.Value : 1f;
        }

        private static void SyncOne(CampaignState state, CombatSite site, TurretRecord r, BuildingRecord b)
        {
            if (r == null || b == null)
            {
                return;
            }
            Runtime rt = RuntimeOf(state, r, b);
            bool built = IsBuilt(b);
            if (!built)
            {
                // 虚影 / 被摧毁：内核里没有它；流体留在炮塔自己身上（重建后放回）。
                if (site.TryGetTurretUnit(r.Serial, out _))
                {
                    PullHealth(site, r, b, rt);
                    site.RemoveTurretUnit(r.Serial);
                }
                ReleaseConsumers(r);
                rt.Short = false;
                return;
            }
            BlueprintCircuitPreview p = ActivePreview(state, r, rt);
            bool uplinked = TurretUplink.IsUplinkedTo(state, r.BuildingId);
            if (p == null || !ReferenceEquals(rt.NeedsFor, p))
            {
                NeedScratch.Clear();
                if (p != null)
                {
                    TurretCatalog.FluidNeeds(p.FirmwareIds, NeedScratch);
                }
                rt.Needs.Clear();
                rt.Needs.AddRange(NeedScratch);
                rt.NeedsFor = p;
            }
            bool needsSupply = rt.Needs.Count > 0;
            int tier = BuildingOps.TierOf(b);
            Vector2 center = b.Position;
            if (!site.TryGetTurretUnit(r.Serial, out _))
            {
                GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g);
                float radius = g != null ? Mathf.Min(g.FootprintW, g.FootprintH) * 0.45f : 0.9f;
                CombatWeapon w0 = p != null ? WeaponOf(state, rt, p, b, r.TargetMode, needsSupply, uplinked) : default;
                float max = BuildingOps.MaxDurability(b.BuildingTypeId);
                float hp = Mathf.Clamp(BuildingOps.Durability(b), 1f, max);
                site.SpawnTurretUnit(r.Serial, center, radius, hp, max, w0, TurretCatalog.TierArmor(tier), false, Vector2.up, 0f);
                b.Health = hp;
                rt.LastPushedHp = hp;
                rt.Tier = tier;
                rt.WeaponPushed = p != null;
                rt.LabelName = null; // 新单位：下面重新登记反应日志 / 弹字的名字
                if (!_builtHookRaised)
                {
                    _builtHookRaised = true;
                    GuidanceHooks.Raise(GuidanceHooks.TurretFirstBuilt);
                }
            }
            // 反应日志 / 弹字里写“炮塔·名字”（改名 / 换蓝图 / 接入切换后跟着更新；只在变了时写）。
            string label = BuildingOps.NameOf(b);
            if (rt.LabelName != label || !ReferenceEquals(rt.LabelSource, p))
            {
                site.SetUnitLabel(UnitOf(site, r.Serial), "reaction.log.turret_named", label, p?.FirmwareIds);
                rt.LabelName = label;
                rt.LabelSource = p;
            }
            if (!site.TryGetTurretState(r.Serial, out TurretUnitState us))
            {
                return;
            }
            // 位置（搬迁完工后建筑换了位置）。
            if ((us.Position - center).sqrMagnitude > 1e-4f)
            {
                site.SetTurretPosition(r.Serial, center);
            }
            // 耐久双向对账：建筑记录被别处改过（维修）= 推给内核；否则把内核的受伤写回建筑。
            float maxHp = BuildingOps.MaxDurability(b.BuildingTypeId);
            if (!float.IsNaN(rt.LastPushedHp) && Mathf.Abs(b.Health - rt.LastPushedHp) > 0.01f)
            {
                float hp = Mathf.Clamp(b.Health, 1f, maxHp);
                site.SetTurretHealth(r.Serial, hp, maxHp);
                rt.LastPushedHp = hp;
                b.Health = hp;
            }
            else
            {
                PullHealth(site, r, b, rt);
            }
            if (rt.Tier != tier)
            {
                site.SetTurretArmor(r.Serial, TurretCatalog.TierArmor(tier));
                rt.Tier = tier;
            }
            // 武器参数（只在变了时换行；同一份参数共用武器表的一行）。
            if (p != null)
            {
                CombatWeapon w = WeaponOf(state, rt, p, b, r.TargetMode, needsSupply, uplinked);
                if (!rt.WeaponPushed)
                {
                    if (!site.TryGetTurretWeapon(r.Serial, out CombatWeapon cur) || !cur.Equals(w))
                    {
                        site.SetTurretWeapon(r.Serial, w);
                    }
                    rt.WeaponPushed = true;
                }
                site.SetTurretFlag(r.Serial, CombatUnitFlags.HeatSink, p.HasHeatSinkStructure);
                site.SetTurretFlag(r.Serial, CombatUnitFlags.ReactionGated, uplinked && SignalUplinkService.IsCoreGatedReaction(p.ReactionId, p.UplinkFirmwareIds));
            }
            bool enabled = rt.Valid && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;
            site.SetTurretFlag(r.Serial, CombatUnitFlags.WeaponEnabled, enabled);
            site.SetTurretFlag(r.Serial, CombatUnitFlags.Possessed, uplinked);
            // 补给（只在能开火时装填与判缺：没电 / 禁用时原因写“缺电 / 已禁用”，不再叠一个“缺流体”）。
            if (needsSupply)
            {
                Supply(state, site, r, b, rt, us);
            }
            else
            {
                ReleaseConsumers(r);
                rt.Issue = TurretSupplyIssue.None;
                rt.Short = false;
            }
        }

        private static int UnitOf(CombatSite site, int serial) => site.TryGetTurretUnit(serial, out int u) ? u : 0;

        /// <summary>这座炮塔此刻的武器参数（按输入缓存：编译结果同一个对象、模式 / 等级倍率 / 补给 / 接入 / 反应抑制都没变就复用上一次的；变了标记“要推给内核”）。</summary>
        private static CombatWeapon WeaponOf(CampaignState state, Runtime rt, BlueprintCircuitPreview p, BuildingRecord b, int mode, bool needsSupply, bool uplinked)
        {
            float mul = RangeMultiplier(b);
            bool suppress = SuppressReaction(state, p, uplinked);
            if (!ReferenceEquals(rt.WeaponFor, p) || rt.WeaponMode != mode || rt.WeaponRangeMul != mul || rt.WeaponSupply != needsSupply
                || rt.WeaponUplinked != uplinked || rt.WeaponSuppress != suppress)
            {
                rt.Weapon = BuildWeapon(p, rt.Profile, mul, mode, needsSupply, uplinked, suppress);
                rt.WeaponFor = p;
                rt.WeaponMode = mode;
                rt.WeaponRangeMul = mul;
                rt.WeaponSupply = needsSupply;
                rt.WeaponUplinked = uplinked;
                rt.WeaponSuppress = suppress;
                rt.WeaponPushed = false;
            }
            return rt.Weapon;
        }

        private static bool SuppressReaction(CampaignState state, BlueprintCircuitPreview p, bool uplinked)
        {
            if (!uplinked || p == null || string.IsNullOrEmpty(p.ReactionId))
            {
                return false;
            }
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(p.ReactionId);
            return fw != null && FirmwareKinds.IsCore(fw) && SignalUplinkService.CooldownRemaining(state, fw) > 0;
        }

        private static void PullHealth(CombatSite site, TurretRecord r, BuildingRecord b, Runtime rt)
        {
            if (site.TryGetTurretState(r.Serial, out TurretUnitState us) && us.Alive)
            {
                b.Health = us.Health;
                rt.LastPushedHp = us.Health;
            }
        }

        /// <summary>拆除 / 记录作废：内核单位拿掉、管线消费者撤掉（缓存里的流体随拆除丢弃）、信号在里面时回到归还核心。</summary>
        private static void Drop(CampaignState state, CombatSite site, TurretRecord r)
        {
            site?.RemoveTurretUnit(r.Serial);
            ReleaseConsumers(r);
            r.HeldFluids = Array.Empty<int>();
            r.HeldMl = Array.Empty<long>();
            Rt.Remove(r.Serial);
            TurretUplink.OnTurretGone(state, r.BuildingId);
        }

        // ─────────────────────────────── 补给 ───────────────────────────────

        /// <summary>
        /// FGR-DEF-004：每种需要的流体在炮塔边上找一格装着它的管线挂消费者（缓存 = 炮塔存的流体，容量 = turret.supply.buffer_shots 发），
        /// 再按整发把补给装进内核（内核一发一扣，上限 turret.supply.magazine_shots 发）。不够一发 → 停火并记原因（没接管线 / 管线里是别的流体 / 管线里没有了）。
        /// O(炮塔周边格数)，按对账间隔做。
        /// </summary>
        private static void Supply(CampaignState state, CombatSite site, TurretRecord r, BuildingRecord b, Runtime rt, in TurretUnitState us)
        {
            bool pipes = PipeNetworkService.IsRunning;
            PerimeterOf(b, RingScratch);
            int minShots = int.MaxValue;
            TurretSupplyIssue issue = TurretSupplyIssue.None;
            int issueFluid = 0;
            int issueOther = 0;
            foreach (TurretFluidNeed need in rt.Needs)
            {
                long perShotMl = Math.Max(1, (long)Math.Round(need.LitersPerShot * 1000.0));
                long capMl = perShotMl * TurretCatalog.BufferShots;
                int at = IndexOf(r.ConsumerFluids, need.FluidId);
                int consumer = at >= 0 ? r.ConsumerIds[at] : 0;
                PipeConsumerInfo info = default;
                bool alive = consumer > 0 && pipes && PipeNetworkService.Kernel.TryGetConsumer(consumer, out info);
                // 找接点：优先装着这种流体的管线格，其次空网络（还没流体），记下别的流体（原因用）。
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
                        if (ci.Fluid == need.FluidId)
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
                    // 接点变了（管线拆了 / 换了位置）：撤掉旧消费者，缓存留在炮塔身上。
                    RemoveConsumerAt(r, at);
                    alive = false;
                    consumer = 0;
                }
                if (!alive && attach != null)
                {
                    long held = TakeHeld(r, need.FluidId);
                    int id = PipeNetworkService.Kernel.AddConsumer(attach.Value.X, attach.Value.Y, need.FluidId, TurretCatalog.SupplyLpm, TurretCatalog.PipePriority);
                    if (id > 0)
                    {
                        PipeNetworkService.Kernel.SetConsumerBuffer(id, capMl, Math.Min(capMl, held));
                        AddHeld(r, need.FluidId, Math.Max(0, held - capMl));
                        SetConsumer(r, need.FluidId, id);
                        consumer = id;
                        alive = true;
                        justAttached = true;
                    }
                    else
                    {
                        AddHeld(r, need.FluidId, held);
                    }
                }
                long buffered = alive ? PipeNetworkService.Kernel.ConsumerBuffer(consumer) : HeldOf(r, need.FluidId);
                int shots = (int)Math.Min(int.MaxValue, buffered / perShotMl);
                if (shots < minShots)
                {
                    minShots = shots;
                }
                // 不够一发：缓存在涨（补给正在进来，只是跟不上射速）/ 刚接上 / 第一次看到它时不算缺；两次对账之间一点没进来才写原因。
                bool flowing = justAttached || !rt.PrevMl.TryGetValue(need.FluidId, out long prevMl) || buffered > prevMl;
                if (shots == 0 && !flowing && issue == TurretSupplyIssue.None)
                {
                    issueFluid = need.FluidId;
                    issue = pick != null || empty != null ? TurretSupplyIssue.Dry : other != 0 ? TurretSupplyIssue.WrongFluid : TurretSupplyIssue.NoPipe;
                    issueOther = other;
                }
            }
            if (minShots == int.MaxValue)
            {
                minShots = 0;
            }
            // 按整发装进内核。
            int loaded = Mathf.FloorToInt(us.Ammo + 1e-4f);
            int load = Math.Max(0, Math.Min(TurretCatalog.MagazineShots - loaded, minShots));
            if (load > 0)
            {
                foreach (TurretFluidNeed need in rt.Needs)
                {
                    long take = load * Math.Max(1, (long)Math.Round(need.LitersPerShot * 1000.0));
                    int at = IndexOf(r.ConsumerFluids, need.FluidId);
                    if (at >= 0 && pipes)
                    {
                        take -= PipeNetworkService.Kernel.TakeConsumerBuffer(r.ConsumerIds[at], take);
                    }
                    if (take > 0)
                    {
                        AddHeld(r, need.FluidId, -take);
                    }
                }
                site.SetTurretAmmo(r.Serial, us.Ammo + load);
            }
            foreach (TurretFluidNeed need in rt.Needs)
            {
                int at = IndexOf(r.ConsumerFluids, need.FluidId);
                rt.PrevMl[need.FluidId] = at >= 0 && pipes ? PipeNetworkService.Kernel.ConsumerBuffer(r.ConsumerIds[at]) : HeldOf(r, need.FluidId);
            }
            bool shortNow = us.Ammo + load < 1f - 1e-4f && issue != TurretSupplyIssue.None;
            rt.Issue = shortNow ? issue : TurretSupplyIssue.None;
            rt.IssueFluid = shortNow ? issueFluid : 0;
            rt.IssueOtherFluid = shortNow ? issueOther : 0;
            if (shortNow && !rt.Short && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered
                && GameClock.Ticks >= rt.NextNotifyTick)
            {
                // B08：同一座炮塔的通知有冷却，同类通知按聚合窗口合并（“3 座炮塔缺补给停火”）；点通知定位到炮塔。
                rt.NextNotifyTick = GameClock.TickAfter(TurretCatalog.NotifyCooldownSeconds);
                NotificationCenter.Post("turret_supply", GameText.Format("turret.supply.notify", BuildingOps.NameOf(b), PipeNetworkService.FluidName(rt.IssueFluid)),
                    new Vector3(b.Position.x, 0f, b.Position.y));
                GuidanceHooks.Raise(GuidanceHooks.TurretFirstSupplyShort);
            }
            if (rt.Short != shortNow)
            {
                rt.Short = shortNow;
                Touch();
            }
        }

        /// <summary>炮塔占地四周（四邻，不含占地本身）的格子，按占地格顺序、北东南西去重（确定性）。</summary>
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

        private static int IndexOf(int[] arr, int v)
        {
            if (arr == null)
            {
                return -1;
            }
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == v)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void SetConsumer(TurretRecord r, int fluid, int id)
        {
            int at = IndexOf(r.ConsumerFluids, fluid);
            if (at >= 0)
            {
                r.ConsumerIds[at] = id;
                return;
            }
            var ids = new int[r.ConsumerIds.Length + 1];
            var fl = new int[r.ConsumerFluids.Length + 1];
            Array.Copy(r.ConsumerIds, ids, r.ConsumerIds.Length);
            Array.Copy(r.ConsumerFluids, fl, r.ConsumerFluids.Length);
            ids[ids.Length - 1] = id;
            fl[fl.Length - 1] = fluid;
            r.ConsumerIds = ids;
            r.ConsumerFluids = fl;
        }

        private static void RemoveConsumerAt(TurretRecord r, int at)
        {
            if (at < 0 || at >= r.ConsumerIds.Length)
            {
                return;
            }
            int id = r.ConsumerIds[at];
            int fluid = r.ConsumerFluids[at];
            if (id > 0 && PipeNetworkService.IsRunning && PipeNetworkService.Kernel.RemoveConsumer(id, out long buffered))
            {
                AddHeld(r, fluid, buffered);
            }
            var ids = new List<int>(r.ConsumerIds);
            var fl = new List<int>(r.ConsumerFluids);
            ids.RemoveAt(at);
            fl.RemoveAt(at);
            r.ConsumerIds = ids.ToArray();
            r.ConsumerFluids = fl.ToArray();
        }

        /// <summary>撤掉这座炮塔的全部管线消费者，缓存里的流体留在炮塔身上（<see cref="TurretRecord.HeldMl"/>）。</summary>
        private static void ReleaseConsumers(TurretRecord r)
        {
            for (int i = r.ConsumerIds.Length - 1; i >= 0; i--)
            {
                RemoveConsumerAt(r, i);
            }
        }

        private static long HeldOf(TurretRecord r, int fluid)
        {
            int at = IndexOf(r.HeldFluids, fluid);
            return at >= 0 ? r.HeldMl[at] : 0;
        }

        private static long TakeHeld(TurretRecord r, int fluid)
        {
            long v = HeldOf(r, fluid);
            AddHeld(r, fluid, -v);
            return v;
        }

        private static void AddHeld(TurretRecord r, int fluid, long delta)
        {
            if (delta == 0)
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

        /// <summary>这座炮塔某种流体此刻存着多少（升；消费者缓存 + 留在身上的；不含已经装进内核的整发）。</summary>
        public static float StoredLiters(CampaignState state, string buildingId, int fluid)
        {
            TurretRecord r = Find(state, buildingId);
            if (r == null)
            {
                return 0f;
            }
            long ml = HeldOf(r, fluid);
            int at = IndexOf(r.ConsumerFluids, fluid);
            if (at >= 0 && PipeNetworkService.IsRunning)
            {
                ml += PipeNetworkService.Kernel.ConsumerBuffer(r.ConsumerIds[at]);
            }
            return ml / 1000f;
        }

        // ─────────────────────────────── 内核事件 ───────────────────────────────

        /// <summary>CombatSite 把炮塔的内核事件交到这里（<see cref="CombatSite.TurretEvent"/>，世界载入时绑定）。O(1)。</summary>
        public static void OnKernelEvent(CombatSite site, int serial, CombatEvent e)
        {
            CampaignState state = CampaignSession.Current;
            TurretRecord r = FindBySerial(state, serial);
            if (state == null || r == null)
            {
                return;
            }
            switch (e.Kind)
            {
                case CombatEventKind.TurretKill:
                {
                    r.KillCount++;
                    if (e.Code == 1)
                    {
                        r.EliteKills++;
                    }
                    StateOf(state).TotalKills++;
                    GuidanceHooks.Raise(GuidanceHooks.TurretFirstKill);
                    Touch();
                    return;
                }
                case CombatEventKind.Killed:
                {
                    // 炮塔耐久归零 = 炮塔座被摧毁（FGR-ECO-013：留下虚影，可以重建；设置保留）。
                    BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                    site.RemoveTurretUnit(serial);
                    if (Rt.TryGetValue(serial, out Runtime rt))
                    {
                        rt.LastPushedHp = float.NaN;
                    }
                    ReleaseConsumers(r);
                    TurretUplink.OnTurretGone(state, r.BuildingId);
                    if (b != null)
                    {
                        HomeValleyPowerGrid.ApplyBuildingDestroyed(state, b.BuildingId);
                    }
                    Touch();
                    return;
                }
                case CombatEventKind.ReactionFired:
                    TurretUplink.OnReactionFired(state, r, e);
                    return;
            }
        }

        /// <summary>存档前（<see cref="WorldSimulation.SyncAllForSave"/>）：把内核里的耐久写回炮塔座建筑（受伤不丢）。热量 / 补给 / 朝向随家园内核快照，流体随管线快照。</summary>
        public static void WriteTo(CampaignState state)
        {
            CombatSite site = HomeSite;
            if (state == null || site == null || site.IsDisposed)
            {
                return;
            }
            foreach (TurretRecord r in All(state))
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                if (b != null && site.TryGetTurretState(r.Serial, out TurretUnitState us) && us.Alive)
                {
                    b.Health = us.Health;
                    if (Rt.TryGetValue(r.Serial, out Runtime rt))
                    {
                        rt.LastPushedHp = us.Health;
                    }
                }
            }
        }

        // ─────────────────────────────── 状态与读数 ───────────────────────────────

        /// <summary>
        /// 炮塔的功能状态（<see cref="BuildingStatusService"/> 在通用状态之后调：缺电 / 禁用 / 虚影 / 被毁 / 升级中已由通用状态先报）：
        /// 没装蓝图 / 蓝图用不了 → 待机 + 原因；信号接入中 → 工作；过热 → 待机（过热停火）；缺补给 → 缺流体（缺什么、怎么办）；射程内有敌人 → 工作；否则 → 待机。
        /// 开销：一次射程内目标查询（只在面板 / 悬停按需时）。
        /// </summary>
        public static BuildingStatus StatusOf(CampaignState state, BuildingRecord b)
        {
            // 刚建成 / 刚放下、还没轮到对账的炮塔座（例如完工后马上暂停）：先补记录（装建造栏选的蓝图），不误报“没有装炮塔蓝图”。
            TurretRecord r = b != null && IsTurretProper(b) ? EnsureRecord(state, b) : null;
            if (r == null || b == null)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "turret.no_blueprint", GameText.Get("bs.reason.turret_no_blueprint"));
            }
            Runtime rt = RuntimeOf(state, r, b);
            if (!rt.Valid)
            {
                return rt.Failure == TurretFailure.BlueprintMissing && string.IsNullOrEmpty(r.BlueprintId)
                    ? new BuildingStatus(BuildingStatusKind.Idle, "turret.no_blueprint", GameText.Get("bs.reason.turret_no_blueprint"))
                    : new BuildingStatus(BuildingStatusKind.Idle, "turret.bad_blueprint", GameText.Format("bs.reason.turret_bad_blueprint", rt.Invalid ?? string.Empty));
            }
            CombatSite site = HomeSite;
            TurretUnitState us = default;
            bool hasUnit = site != null && site.TryGetTurretState(r.Serial, out us);
            string mode = TurretCatalog.ModeName(r.TargetMode);
            float range = rt.Profile.Range * RangeMultiplier(b);
            string rangeText = Mathf.RoundToInt(range).ToString(CultureInfo.InvariantCulture);
            if (TurretUplink.IsUplinkedTo(state, r.BuildingId))
            {
                if (rt.Issue != TurretSupplyIssue.None)
                {
                    // FG6-DEF-01 审查修复（P2）：接入中断供也写明缺什么、怎么办（左键开火会被拒，状态行不能只写“接入中”）。
                    return new BuildingStatus(BuildingStatusKind.NoFluid, "turret.uplinked.no_supply." + rt.Issue.ToString().ToLowerInvariant(),
                        GameText.Format("bs.reason.turret_uplinked_short", BuildingOps.NameOf(b), PipeNetworkService.FluidName(rt.IssueFluid), IssueText(rt)));
                }
                return new BuildingStatus(BuildingStatusKind.Working, "turret.uplinked", GameText.Format("bs.reason.turret_uplinked", BuildingOps.NameOf(b)));
            }
            if (hasUnit && us.Overheated && site.TryGetTurretWeapon(r.Serial, out CombatWeapon w))
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "turret.overheated",
                    GameText.Format("bs.reason.turret_overheated", Mathf.RoundToInt(us.Heat), Mathf.RoundToInt(w.OverheatAt), Mathf.RoundToInt(w.RecoverBelow)));
            }
            if (rt.Issue != TurretSupplyIssue.None)
            {
                return new BuildingStatus(BuildingStatusKind.NoFluid, "turret.no_supply." + rt.Issue.ToString().ToLowerInvariant(),
                    GameText.Format("bs.reason.turret_no_supply", PipeNetworkService.FluidName(rt.IssueFluid), IssueText(rt)));
            }
            string low = string.Empty;
            if (hasUnit && rt.Needs.Count > 0)
            {
                int shots = Mathf.FloorToInt(us.Ammo + 1e-4f) + StoredShots(state, r, rt);
                if (shots <= TurretCatalog.LowShots)
                {
                    low = GameText.Format("bs.reason.turret_low_supply", PipeNetworkService.FluidName(rt.Needs[0].FluidId), shots);
                }
            }
            bool engaged = hasUnit && site.TurretHasTarget(r.Serial);
            return engaged
                ? new BuildingStatus(BuildingStatusKind.Working, "turret.firing", GameText.Format("bs.reason.turret_firing", mode, rangeText, r.KillCount) + low)
                : new BuildingStatus(BuildingStatusKind.Idle, "turret.idle", GameText.Format("bs.reason.turret_idle", mode, rangeText, r.KillCount) + low);
        }

        private static string IssueText(Runtime rt)
        {
            string need = PipeNetworkService.FluidName(rt.IssueFluid);
            switch (rt.Issue)
            {
                case TurretSupplyIssue.NoPipe: return GameText.Format("turret.supply.no_pipe", need);
                case TurretSupplyIssue.WrongFluid: return GameText.Format("turret.supply.wrong_fluid", need, PipeNetworkService.FluidName(rt.IssueOtherFluid));
                default: return GameText.Format("turret.supply.dry", need);
            }
        }

        /// <summary>炮塔存着的流体还能装几发（不含已在内核里的）。</summary>
        private static int StoredShots(CampaignState state, TurretRecord r, Runtime rt)
        {
            int min = int.MaxValue;
            foreach (TurretFluidNeed n in rt.Needs)
            {
                float l = StoredLiters(state, r.BuildingId, n.FluidId);
                min = Math.Min(min, Mathf.FloorToInt(l / Mathf.Max(0.001f, n.LitersPerShot) + 1e-4f));
            }
            return min == int.MaxValue ? 0 : min;
        }

        /// <summary>补给一行（“燃油 34 升（还能打 17 发，每发 2 升）；冷却液……” / “只用电”）。</summary>
        public static string SupplyLine(CampaignState state, string buildingId)
        {
            TurretRecord r = Find(state, buildingId);
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (r == null || b == null)
            {
                return string.Empty;
            }
            Runtime rt = RuntimeOf(state, r, b);
            if (rt.Needs.Count == 0)
            {
                return GameText.Get("turret.supply.power_only");
            }
            CombatSite site = HomeSite;
            float ammo = site != null && site.TryGetTurretState(r.Serial, out TurretUnitState us) ? us.Ammo : 0f;
            var parts = new List<string>(rt.Needs.Count);
            foreach (TurretFluidNeed n in rt.Needs)
            {
                float liters = StoredLiters(state, buildingId, n.FluidId) + ammo * n.LitersPerShot;
                int shots = Mathf.FloorToInt(liters / Mathf.Max(0.001f, n.LitersPerShot) + 1e-4f);
                parts.Add(GameText.Format("turret.supply.line", PipeNetworkService.FluidName(n.FluidId), Mathf.RoundToInt(liters), shots,
                    n.LitersPerShot.ToString("0.#", CultureInfo.InvariantCulture)));
            }
            return string.Join(GameText.Get("turret.supply.sep"), parts);
        }

        /// <summary>面板 / 名册 / 自检读的一座炮塔的读数（同一份数据，界面不另算）。</summary>
        public static bool TryGetReadout(CampaignState state, string buildingId, out TurretReadout ro)
        {
            ro = default;
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            TurretRecord r = IsTurretProper(b) ? EnsureRecord(state, b) : null;
            if (r == null)
            {
                return false;
            }
            Runtime rt = RuntimeOf(state, r, b);
            BlueprintRecord rec = BlueprintEditorService.Find(state, r.BlueprintId);
            ro.BuildingId = buildingId;
            ro.Name = BuildingOps.NameOf(b);
            ro.BlueprintId = r.BlueprintId;
            ro.BlueprintName = BlueprintName(rec);
            ro.BlueprintVersion = r.BlueprintVersion;
            ro.LatestVersion = rec?.ActiveVersion ?? 0;
            ro.Size = TurretCatalog.SizeOfType(b.BuildingTypeId);
            ro.TargetMode = r.TargetMode;
            ro.Kills = r.KillCount;
            ro.EliteKills = r.EliteKills;
            ro.Built = IsBuilt(b);
            ro.Valid = rt.Valid;
            ro.InvalidReason = rt.Valid ? string.Empty : (string.IsNullOrEmpty(r.BlueprintId) ? GameText.Get("bs.reason.turret_no_blueprint") : rt.Invalid ?? string.Empty);
            ro.Health = BuildingOps.Durability(b);
            ro.MaxHealth = BuildingOps.MaxDurability(b.BuildingTypeId);
            ro.HasPort = rt.Valid && rt.Board.HasUplink;
            ro.Uplinked = TurretUplink.IsUplinkedTo(state, buildingId);
            ro.SupplyLine = SupplyLine(state, buildingId);
            ro.SupplyShort = rt.Issue != TurretSupplyIssue.None;
            CombatSite site = HomeSite;
            if (site != null && site.TryGetTurretState(r.Serial, out TurretUnitState us))
            {
                ro.HasUnit = true;
                ro.Health = us.Health;
                ro.MaxHealth = us.MaxHealth;
                ro.Heat = us.Heat;
                ro.Overheated = us.Overheated;
                ro.Ammo = us.Ammo;
            }
            if (rt.Valid)
            {
                BlueprintCircuitPreview p = ActivePreview(state, r, rt) ?? rt.Local;
                CombatWeapon w = BuildWeapon(p, rt.Profile, RangeMultiplier(b), r.TargetMode, rt.Needs.Count > 0, ro.Uplinked, false);
                ro.Range = w.Range;
                ro.TurnRate = w.TurnRate;
                ro.DamagePerShot = CombatSite.MachineHitDamage(p);
                ro.Cooldown = w.Cooldown * (w.Reading.CooldownScale > 0f ? w.Reading.CooldownScale : 1f);
                ro.Projectile = w.Mode == CombatWeaponMode.Projectile;
                ro.OverheatAt = w.OverheatAt;
                ro.RecoverBelow = w.RecoverBelow;
                ro.Carrier = CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent comp) ? GameText.Get(comp.SubtypeKey) : string.Empty;
            }
            ro.Status = BuildingStatusService.Evaluate(state, b);
            return true;
        }

        /// <summary>DEBT-FG2VFX02-03：炮塔的机身状态（与机器同一个 <see cref="MachineMorph.MaskOf"/>：生效固件的类别集合；接入时含接入口插入的固件）与作战组件。蓝图用不了时 false。</summary>
        public static bool TryGetMorph(CampaignState state, string buildingId, out string primary, out string utility, out MorphMask mask) =>
            TryGetMorph(state, Find(state, buildingId), out primary, out utility, out mask);

        /// <summary>同上，直接给炮塔记录（炮塔头对账遍历记录时用，不再按建筑 ID 查一次）。O(1)（编译结果按输入缓存）。</summary>
        public static bool TryGetMorph(CampaignState state, TurretRecord r, out string primary, out string utility, out MorphMask mask)
        {
            primary = null;
            utility = null;
            mask = MorphMask.None;
            BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
            if (!IsTurretProper(b))
            {
                return false;
            }
            Runtime rt = RuntimeOf(state, r, b);
            BlueprintCircuitPreview p = ActivePreview(state, r, rt);
            if (p == null)
            {
                return false;
            }
            primary = string.IsNullOrEmpty(p.PrimaryId) ? null : p.PrimaryId;
            utility = string.IsNullOrEmpty(p.UtilityId) ? null : p.UtilityId;
            mask = MachineMorph.MaskOf(p);
            return true;
        }

        /// <summary>放置预览的射程圈半径（米，T1；按选中的蓝图的主组件）。蓝图用不了时 0。</summary>
        public static float RangeOf(CampaignState state, string blueprintId)
        {
            if (!ValidateBlueprint(state, blueprintId, null, out _, out TurretProfileDef prof, out _, out _))
            {
                return 0f;
            }
            return prof.Range;
        }

        /// <summary>一座已有炮塔现在的射程（米；含等级倍率）。蓝图用不了时 0。</summary>
        public static float RangeOfTurret(CampaignState state, string buildingId)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            TurretRecord r = IsTurretProper(b) ? Find(state, buildingId) : null;
            if (r == null)
            {
                return 0f;
            }
            Runtime rt = RuntimeOf(state, r, b);
            return rt.Valid ? rt.Profile.Range * RangeMultiplier(b) : 0f;
        }
    }
}
