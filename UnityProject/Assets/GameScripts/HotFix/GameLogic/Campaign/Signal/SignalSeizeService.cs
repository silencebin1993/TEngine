using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// EMG4-HIJ-02（FG19 FGR-EMG-165）：信号劫持、夺控、槽位封锁与反向接入的统一入口——共用一套状态机与事件 <c>evt.hijack</c> / <c>evt.seize</c> / <c>evt.release</c>：
    /// - 单位方向（劫持：玩家 → 敌机；夺控：敌方 → 己方机器）都记在所在地点战斗内核的同一张表里（CombatKernel 段 27，随内核快照进存档），按时间到点在内核步进里结算；
    /// - 槽位封锁（敌方 → 玩家信号核槽位）记在信号核存档域（<see cref="SignalCoreState.SlotBlocks"/>，随战役存档）：被封槽按空槽重编译、冷却不重置、解除立即恢复；
    ///   封锁 / 解除时给玩家此刻接入的那台机器注入 evt.seize / evt.release（Code = 来源，Other = 槽号），与夺控同一事件码（FG19A N41 反夺控“本机被夺控或槽位被封锁时”）；
    /// - 反向接入：玩家对被夺控的机器发起接入（覆盖判定沿用 FG01，SignalUplinkService.Request）→ 引导 emg.seize.reclaim_seconds 游戏秒后夺回。
    /// 具体的信号幽灵（FG12-EVT-03）、余烬主机（FG11-BOSS-01）、根权限压制（FG13-END-05）只调用本类，不各写各的。
    /// 热更层没有每帧遍历：HUD 行按版本号与地点数（≤ 3）重建。
    /// </summary>
    public static class SignalSeizeService
    {
        /// <summary>来源码（随 evt.seize 的 Code 发出、HUD / 通知写名字）。</summary>
        public const int SourceUnknown = 0, SourceGhost = 1, SourceRoot = 2, SourceEmber = 3;

        /// <summary>状态变化（夺控事件、封锁 / 解除）时 +1：HUD 状态行据此重建。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>自检读数：槽位封锁 / 解除次数、注入到接入机器的 evt.seize / evt.release 结果。</summary>
        public static int SlotBlockedCount, SlotRestoredCount;
        public static EmgInjectResult LastSlotInject;

        public static float SeizeSeconds => Tuning("emg.seize.seconds", EmgHijackConst.DefaultSeizeSeconds);
        public static float ReclaimSeconds => Tuning("emg.seize.reclaim_seconds", EmgHijackConst.DefaultReclaimSeconds);

        public static void Touch() => Revision++;

        public static string SourceName(int source) => GameText.Get(source switch
        {
            SourceGhost => "emg.seize.source.ghost",
            SourceRoot => "emg.seize.source.root",
            SourceEmber => "emg.seize.source.ember",
            _ => "emg.seize.source.unknown",
        });

        // ── 夺控（敌方 → 己方机器）──

        /// <summary>夺控机器 <paramref name="logicId"/>（seconds ≤ 0 = 直到 <see cref="Release"/>）。玩家此刻接入的那台永远不会被夺控（FGR-END-022、FGR-EVT-021）；
        /// 正在接入过渡中的目标也算（否则过渡结束会把信号接进一台敌方控制、带着核心固件的机器，FGR-SIG-090）。</summary>
        public static EmgSeizeResult Seize(CampaignState s, int logicId, int source, float seconds, string seizerEnemyId = null)
        {
            if (SignalUplinkService.IsUplinked(s, logicId) || (logicId > 0 && SignalUplinkService.PendingTargetLogicId == logicId))
            {
                return EmgSeizeResult.Uplinked;
            }
            CombatSite site = SiteOf(logicId);
            EmgSeizeResult r = site != null ? site.SeizeMachine(logicId, seconds, source, seizerEnemyId) : EmgSeizeResult.NoUnit;
            if (r == EmgSeizeResult.Ok)
            {
                Revision++;
            }
            return r;
        }

        /// <summary>调用方立即解除夺控（事件结束、首领阶段结束；瘫痪的恢复耐久）。</summary>
        public static bool Release(int logicId)
        {
            bool ok = SiteOf(logicId)?.ReleaseSeizedMachine(logicId) ?? false;
            if (ok)
            {
                Revision++;
            }
            return ok;
        }

        public static bool IsSeized(int logicId) => SiteOf(logicId)?.TryGetSeizedMachine(logicId, out _, out _, out _, out _) ?? false;

        /// <summary>
        /// 反向接入（FG10 FGR-EVT-022）：对被夺控的机器发起接入——覆盖判定由调用方按 FG01 先做（SignalUplinkService.Request 的 ValidateCore）；
        /// 引导 <see cref="ReclaimSeconds"/> 游戏秒后夺回（内核到点结算，暂停不走、倍速按游戏秒、进快照）。瘫痪中 / 已在引导时拒绝并写原因。
        /// </summary>
        public static bool BeginReclaim(int logicId, out string text)
        {
            string label = SignalPresence.MachineLabel(logicId);
            CombatSite site = SiteOf(logicId);
            if (site == null || !site.TryGetSeizedMachine(logicId, out _, out bool downed, out float left, out _))
            {
                text = GameText.Format("emg.seize.reclaim.not_seized", label);
                return false;
            }
            if (downed)
            {
                text = GameText.Format("emg.seize.reclaim.downed", label);
                return false;
            }
            if (left > 0f)
            {
                text = GameText.Format("emg.seize.reclaim.pending", label, Mathf.CeilToInt(left).ToString(CultureInfo.InvariantCulture));
                return false;
            }
            bool ok = site.BeginReclaimMachine(logicId, ReclaimSeconds);
            text = GameText.Format(ok ? "emg.seize.reclaim.started" : "emg.seize.reclaim.not_seized", label, Mathf.CeilToInt(ReclaimSeconds).ToString(CultureInfo.InvariantCulture));
            if (ok)
            {
                Revision++;
            }
            return ok;
        }

        // ── 槽位封锁（敌方 → 玩家信号核槽位，原称“槽位劫持”）──

        public static bool IsBlocked(CampaignState s, int slot) => IndexOf(s, slot) >= 0;

        /// <summary>封锁 <paramref name="slot"/>（0 起）。已封锁 / 槽号越界 → false。被封槽里的固件按空槽重编译，冷却照常走、不重置。</summary>
        public static bool BlockSlot(CampaignState s, int slot, int source)
        {
            if (s == null || slot < 0 || slot >= SignalCoreService.MaxSlots || IsBlocked(s, slot))
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<SignalSlotBlockRecord>(s.SignalCore.SlotBlocks) { new SignalSlotBlockRecord { Slot = slot, Source = source, SinceTick = GameClock.Ticks } };
            s.SignalCore.SlotBlocks = list.ToArray();
            SignalCoreService.NotifySlotBlockChanged();
            Revision++;
            SlotBlockedCount++;
            GuidanceHooks.Raise(GuidanceHooks.EmgFirstSlotBlock);
            string fw = SignalCoreService.SlotContentId(s, slot);
            string what = fw.Length > 0 ? GameText.Format("emg.seize.slot.firmware", EmgProgramTreeText.FirmwareName(fw)) : string.Empty;
            Notifications.NotificationCenter.Post("emg_slot_blocked", GameText.Format("emg.seize.notify.slot_blocked", SlotNo(slot), SourceName(source), what), null);
            InjectHost(s, EmgEventKind.Seize, slot, source);
            return true;
        }

        /// <summary>解除封锁（立即恢复；冷却不重置）。没封锁 → false。</summary>
        public static bool UnblockSlot(CampaignState s, int slot)
        {
            int i = IndexOf(s, slot);
            if (i < 0)
            {
                return false;
            }
            var list = new List<SignalSlotBlockRecord>(s.SignalCore.SlotBlocks);
            int source = list[i].Source;
            list.RemoveAt(i);
            s.SignalCore.SlotBlocks = list.ToArray();
            SignalCoreService.NotifySlotBlockChanged();
            Revision++;
            SlotRestoredCount++;
            Notifications.NotificationCenter.Post("emg_slot_restored", GameText.Format("emg.seize.notify.slot_restored", SlotNo(slot), SourceName(source)), null);
            InjectHost(s, EmgEventKind.Release, slot, source);
            return true;
        }

        /// <summary>解除某来源的全部封锁（首领被击毁、战斗结束、撤离）。返回解除的槽数。</summary>
        public static int UnblockAll(CampaignState s, int source)
        {
            int n = 0;
            SignalSlotBlockRecord[] blocks = s?.SignalCore?.SlotBlocks ?? Array.Empty<SignalSlotBlockRecord>();
            foreach (SignalSlotBlockRecord b in blocks)
            {
                if (b != null && b.Source == source && UnblockSlot(s, b.Slot))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>被封锁的槽的原因行（没封锁返回空串）：“被余烬主机封锁：槽里的指令按空槽重编译，冷却不重置”；槽里是触发器时再写“它放出的子载荷没有了，下游指令回到上一层节点”。</summary>
        public static string BlockedReason(CampaignState s, int slot)
        {
            int i = IndexOf(s, slot);
            if (i < 0)
            {
                return string.Empty;
            }
            string line = GameText.Format("emg.seize.slot.reason", SourceName(s.SignalCore.SlotBlocks[i].Source));
            string fw = SignalCoreService.SlotContentId(s, slot);
            return fw.Length > 0 && IsTrigger(fw) ? line + GameText.Get("emg.seize.slot.trigger_note") : line;
        }

        /// <summary>这条固件编进程序后是不是触发器（单条编译、按目录版本缓存；只在 HUD / 面板重建时调用）。按端口判，不认内容 ID。</summary>
        public static bool IsTrigger(string firmwareId)
        {
            if (_triggerRevision != EmgCatalog.Revision)
            {
                _triggerRevision = EmgCatalog.Revision;
                TriggerCache.Clear();
            }
            if (!TriggerCache.TryGetValue(firmwareId, out bool trig))
            {
                EmgProgramResult r = EmgProgramCompiler.Compile(new[] { firmwareId }, Array.Empty<string>(), FirmwareCarrier.Projectile);
                trig = r.Steps.Exists(st => st.Kind == EmgStepKind.Trigger);
                TriggerCache[firmwareId] = trig;
            }
            return trig;
        }

        // ── HUD（FGR-EMG-165“HUD 显示被封锁槽位与被夺控机器”）──

        /// <summary>HUD 状态行：被夺控的机器（剩余 / 瘫痪 / 反向接入进度）与被封锁的槽。都没有返回空串。O(地点数 + 记录数)。</summary>
        public static string HudLine(CampaignState s)
        {
            if (CombatSites.SeizeTotal() == 0 && (s?.SignalCore?.SlotBlocks == null || s.SignalCore.SlotBlocks.Length == 0))
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            string sep = GameText.Get("emg.seize.hud.sep");
            foreach (CombatSite site in CombatSites.All)
            {
                site.AppendSeizeHud(sb, sep);
            }
            foreach (SignalSlotBlockRecord b in s?.SignalCore?.SlotBlocks ?? Array.Empty<SignalSlotBlockRecord>())
            {
                if (b != null)
                {
                    sb.Append(sb.Length > 0 ? sep : string.Empty).Append(GameText.Format("emg.seize.hud.slot", SlotNo(b.Slot), SourceName(b.Source)));
                }
            }
            return sb.ToString();
        }

        /// <summary>HUD 行的变化键（版本号 + 每台被夺控机器的整秒读数）。没有夺控也没有封锁时 O(地点数)。</summary>
        public static int HudKey(CampaignState s)
        {
            int key = Revision * 31 + (s?.SignalCore?.SlotBlocks?.Length ?? 0);
            if (CombatSites.SeizeTotal() == 0)
            {
                return key;
            }
            foreach (CombatSite site in CombatSites.All)
            {
                key = site.SeizeHudKey(key);
            }
            return key;
        }

        public static void ResetForTests()
        {
            Revision++;
            SlotBlockedCount = SlotRestoredCount = 0;
            LastSlotInject = default;
            TriggerCache.Clear();
        }

        // ── 内部 ──

        private static readonly Dictionary<string, bool> TriggerCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static int _triggerRevision = -1;

        private static string SlotNo(int slot) => (slot + 1).ToString(CultureInfo.InvariantCulture);

        private static int IndexOf(CampaignState s, int slot)
        {
            SignalSlotBlockRecord[] blocks = s?.SignalCore?.SlotBlocks;
            for (int i = 0; blocks != null && i < blocks.Length; i++)
            {
                if (blocks[i] != null && blocks[i].Slot == slot)
                {
                    return i;
                }
            }
            return -1;
        }

        private static CombatSite SiteOf(int logicId) =>
            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null && rec.IsAlive ? CombatSites.Get(rec.RegionId) : null;

        /// <summary>封锁 / 解除注入到玩家此刻接入的机器（FG19 FGR-EMG-066 热更注入；没有接入 / 程序不读这个事件 → 不入队）。</summary>
        private static void InjectHost(CampaignState s, EmgEventKind kind, int slot, int source)
        {
            int id = SignalUplinkService.CurrentMachine(s);
            CombatSite site = id != 0 ? SiteOf(id) : null;
            LastSlotInject = site != null ? site.InjectEmgMachineEvent(id, kind, (ushort)Math.Max(0, source), slot + 1) : EmgInjectResult.NoUnit;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
