using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>信号核操作的结果：成功 / 失败、稳定的原因码（测试与日志用）、玩家可见文字（原因 + 解决办法，FG00 B06）。</summary>
    public readonly struct SignalCoreResult
    {
        public readonly bool Success;
        public readonly string Code;
        public readonly string Message;
        /// <summary>新建出来的对象 ID（刻印的芯片实例 / 保存的预设），没有时为 null。</summary>
        public readonly string CreatedId;

        private SignalCoreResult(bool success, string code, string message, string createdId)
        {
            Success = success;
            Code = code;
            Message = message;
            CreatedId = createdId;
        }

        public static SignalCoreResult Ok(string message, string createdId = null) => new SignalCoreResult(true, null, message, createdId);
        public static SignalCoreResult Fail(string code, string message) => new SignalCoreResult(false, code, message, null);
    }

    /// <summary>
    /// FG1-SIG-01（FG01 FGR-SIG-010～012、第 4 章预设、第 6 章存档；FG13 FGU-19）：信号核的唯一写入口。
    ///
    /// - 槽位：初始 2 槽、最多 5 槽（fg.TbHomeTuning signal.core.*），第 3～5 槽由超控阵列 T1～T3 解锁（FG4-ECO-11 落地前等级恒 0）；
    ///   槽位有顺序（下标 0 = 1 号槽，后续接入口按此顺序插入，FG1-SIG-03）。
    /// - 装卸：固件芯片在基元仓与槽位之间原子转移（实例状态经 <see cref="PrimitiveInventory"/> 的唯一写入口改写，
    ///   “仓 / 草稿 / 待领取 / 信号核”四态恰一，不复制、不丢失）。常规与核心固件都能放进信号核；基元芯片不能。
    ///   基元仓满时卸下被拒绝并保留原槽。
    /// - 远征途中（有远征地点在外，<see cref="ExpeditionUnderway"/>）不能修改：装、卸、换位、切换预设全部拒绝并给原因；
    ///   保存 / 改名 / 删除预设不改变携带的固件，允许。
    /// - 预设：按槽位记固件内容 ID（不记实例），切换时按内容从信号核与基元仓里找实例；切换是整体原子的——
    ///   仓位不够时整个切换被拒绝，什么都不改；缺少的固件对应槽位留空并在结果里列出。
    /// - 读档与进入家园时 <see cref="EnsureConsistent"/> 修复悬空引用（槽位指向不存在 / 不在信号核的实例清空；
    ///   标为信号核却不在任何槽位的实例退回基元仓，仓满进待领取——宁可多一步领取，也不丢）。
    /// 没有逐帧逻辑；每个操作 O(槽位数 + 基元仓实例数)，与机器总数无关（FG01 第 7 章）。
    /// </summary>
    public static class SignalCoreService
    {
        public const string CodeNoCampaign = "no-campaign";
        public const string CodeExpedition = "expedition";
        public const string CodeBagFull = "bag-full";
        public const string CodeNotFirmware = "not-firmware";
        public const string CodeNotFound = "not-found";
        public const string CodeNotInBag = "not-in-bag";
        public const string CodeReserved = "reserved";
        public const string CodeNothingSelected = "nothing-selected";
        public const string CodeSlotInvalid = "slot-invalid";
        public const string CodeSlotLocked = "slot-locked";
        public const string CodeSlotEmpty = "slot-empty";
        public const string CodeSameSlot = "same-slot";
        public const string CodePresetLimit = "preset-limit";
        public const string CodePresetNotFound = "preset-not-found";
        public const string CodePresetNameLong = "preset-name-long";
        public const string CodePresetNameEmpty = "preset-name-empty";
        public const string CodePresetBagFull = "preset-bag-full";
        public const string CodePrintLocked = "print-locked";
        public const string CodePrintBagFull = "print-bag-full";
        public const string CodePrintNoPower = "print-no-power";
        public const string CodePrintScrap = "print-scrap";
        public const string CodePrintUnknown = "print-unknown";
        public const string CodeInternal = "internal";

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>任何成功的修改（或读档修复）后 +1；界面据此判断要不要重建。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>跨模块事件（TEngine GameEvent）：请求开关 / 打开信号核面板。发起方是玩法层（家园点归还核心）与
        /// 其它面板（远征准备“编辑信号核”），界面层 <c>SignalCoreHudUIToolkit</c> 订阅；玩法层不直接引用界面类。</summary>
        public const string PanelToggleEvent = "SignalCore.PanelToggleRequested";
        public const string PanelOpenEvent = "SignalCore.PanelOpenRequested";

        public static void RequestPanelToggle() => GameEvent.Send(PanelToggleEvent);
        public static void RequestPanelOpen() => GameEvent.Send(PanelOpenEvent);

        // ── 调参（fg.TbHomeTuning，数据源 tools/cell_tables/fgdata_signal.py）──────────────

        public static int InitialSlots => TuningInt("signal.core.initial_slots", 2);
        public static int MaxSlots => Math.Max(1, TuningInt("signal.core.max_slots", 5));
        public static int MaxPresets => Math.Max(1, TuningInt("signal.core.max_presets", 8));
        public static int PresetNameMaxChars => Math.Max(1, TuningInt("signal.core.preset_name_max_chars", 16));
        public static int FirmwareChipPrintScrap => Math.Max(0, TuningInt("signal.firmware_chip.print_scrap", 10));

        private static int TuningInt(string id, int fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return (int)Math.Round(v);
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[SignalCoreService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        // ── 槽位解锁（超控阵列，FG4-ECO-11）────────────────────────────────────────

        /// <summary>超控阵列等级来源。FG4-ECO-11（超控阵列建筑）落地前没有这座建筑，等级恒 0；那条 Story 在这里接上真实来源。
        /// 自检用它注入等级验证第 3～5 槽的解锁规则。</summary>
        public static Func<CampaignState, int> OverrideArrayTierProvider;

        public static int OverrideArrayTier(CampaignState s) => Math.Max(0, OverrideArrayTierProvider?.Invoke(s) ?? 0);

        /// <summary>已解锁的槽位数 = 初始槽位 + 超控阵列等级，不超过最多槽位。</summary>
        public static int UnlockedSlots(CampaignState s) => Math.Max(1, Math.Min(MaxSlots, InitialSlots + OverrideArrayTier(s)));

        public static bool IsSlotUnlocked(CampaignState s, int index) => index >= 0 && index < UnlockedSlots(s);

        /// <summary>解锁第 <paramref name="index"/>（0 起）号槽需要的超控阵列等级（初始槽为 0）。</summary>
        public static int TierForSlot(int index) => Math.Max(0, index + 1 - InitialSlots);

        // ── 远征锁（FGR-SIG-011：远征途中不能修改信号核）────────────────────────────────

        /// <summary>自检注入“远征是否在外”；为 null 时读真实世界（<see cref="WorldSim.WorldSimulation.ActiveExpedition"/>）。</summary>
        public static Func<bool> ExpeditionUnderwayOverrideForTests;

        /// <summary>有远征地点在外（与“同一时刻只允许一支远征队”同一判据，ExpeditionDepartureService）。</summary>
        public static bool ExpeditionUnderway => ExpeditionUnderwayOverrideForTests != null
            ? ExpeditionUnderwayOverrideForTests()
            : WorldSim.WorldSimulation.ActiveExpedition != null;

        /// <summary>现在能不能修改信号核（装、卸、换位、切换预设）。不能时给出原因。</summary>
        public static bool CanEdit(CampaignState s, out SignalCoreResult denial)
        {
            if (s == null)
            {
                denial = SignalCoreResult.Fail(CodeNoCampaign, GameText.Get("signal.reason.no_campaign"));
                return false;
            }
            if (ExpeditionUnderway)
            {
                denial = SignalCoreResult.Fail(CodeExpedition, GameText.Get("signal.reason.expedition"));
                return false;
            }
            denial = default;
            return true;
        }

        // ── 初始化与一致性 ───────────────────────────────────────────────────────────

        /// <summary>进入家园时调用（幂等）：补齐状态域、槽位数组长度，并修复悬空引用。</summary>
        public static void EnsureInitialized(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(s);
            EnsureConsistent(s);
            EnsureSlotArray(s);
        }

        /// <summary>读档后 / 进入家园时修复信号核与实例账的一致性，返回修复条数（0 = 本来就一致）。
        /// 永不删除实例：悬空的槽位只清空引用；标为信号核却没有槽位的实例退回基元仓（仓满进待领取）。</summary>
        public static int EnsureConsistent(CampaignState s)
        {
            if (s == null)
            {
                return 0;
            }
            CampaignFgStateDomains.EnsureAll(s);
            SignalCoreState core = s.SignalCore;
            int max = MaxSlots;
            string[] old = core.SlotPartIds ?? Array.Empty<string>();
            // 只修真正的不一致：比最多槽位短的数组保持原样（读取时越界当空槽，写入前 EnsureSlotArray 再补齐），
            // 这样没动过信号核的存档读写逐字往返、不漂移；超出最多槽位的部分截掉，里面的实例按孤儿退回。
            int keep = Math.Min(old.Length, max);
            var slots = new string[keep];
            int repairs = 0;
            bool changed = old.Length > max;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < keep; i++)
            {
                string id = old[i] ?? string.Empty;
                if (id.Length > 0)
                {
                    PrimitiveChipRecord chip = PrimitiveInventory.Find(s, id);
                    if (chip == null || chip.State != PrimitiveChipState.SignalCore || !seen.Add(id))
                    {
                        Log.Warning($"[SignalCoreService] 信号核 {i + 1} 号槽指向的实例 {id} 不存在、不在信号核或重复，已清空该槽（实例本身不受影响）。");
                        id = string.Empty;
                        repairs++;
                        changed = true;
                    }
                }
                slots[i] = id;
            }
            if (changed)
            {
                core.SlotPartIds = slots;
            }

            // 标为信号核、却不在任何槽位（含超出最多槽位数的旧槽）的实例：退回基元仓，仓满进待领取。
            PrimitiveChipRecord[] orphans = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                .Where(c => c != null && c.State == PrimitiveChipState.SignalCore && !seen.Contains(c.PartId ?? string.Empty))
                .OrderBy(c => c.PartId, StringComparer.Ordinal)
                .ToArray();
            foreach (PrimitiveChipRecord orphan in orphans)
            {
                PrimitiveInventory.TryReturnFromSignalCore(s, orphan.PartId, pendingIfFull: true);
                Log.Warning($"[SignalCoreService] 实例 {orphan.PartId}（{orphan.CardDefId}）标为信号核却不在任何槽位，已退回{(orphan.State == PrimitiveChipState.Pending ? "待领取" : "基元仓")}。");
                repairs++;
            }

            // 预设：去掉空记录；当前预设指向已删除的预设时清空。
            if (core.Presets.Any(p => p == null || string.IsNullOrEmpty(p.PresetId)))
            {
                core.Presets = core.Presets.Where(p => p != null && !string.IsNullOrEmpty(p.PresetId)).ToArray();
                repairs++;
            }
            if (core.ActivePresetId.Length > 0 && FindPreset(s, core.ActivePresetId) == null)
            {
                core.ActivePresetId = string.Empty;
                repairs++;
            }
            if (repairs > 0)
            {
                Revision++;
            }
            return repairs;
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────────

        public static string SlotPartId(CampaignState s, int index)
        {
            string[] slots = s?.SignalCore?.SlotPartIds;
            return slots != null && index >= 0 && index < slots.Length ? slots[index] ?? string.Empty : string.Empty;
        }

        public static PrimitiveChipRecord SlotChip(CampaignState s, int index)
        {
            string id = SlotPartId(s, index);
            return id.Length > 0 ? PrimitiveInventory.Find(s, id) : null;
        }

        /// <summary>该槽装着的固件内容 ID（空槽为空串）。</summary>
        public static string SlotContentId(CampaignState s, int index) => SlotChip(s, index)?.CardDefId ?? string.Empty;

        public static int EquippedCount(CampaignState s)
        {
            int n = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (SlotPartId(s, i).Length > 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>按槽位顺序的固件内容 ID（长度 = 最多槽位数，空槽为空串）。</summary>
        public static string[] CurrentContentIds(CampaignState s)
        {
            var ids = new string[MaxSlots];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = SlotContentId(s, i);
            }
            return ids;
        }

        /// <summary>基元仓里可以放进信号核的固件芯片（在仓、未被合成预留），按名字再按实例 ID 排序（稳定）。</summary>
        public static List<PrimitiveChipRecord> BagFirmware(CampaignState s, List<PrimitiveChipRecord> into = null)
        {
            List<PrimitiveChipRecord> list = into ?? new List<PrimitiveChipRecord>();
            list.Clear();
            foreach (PrimitiveChipRecord c in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && c.State == PrimitiveChipState.Bag && FirmwareKinds.IsFirmware(c.CardDefId))
                {
                    list.Add(c);
                }
            }
            list.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.CardDefId, b.CardDefId);
                return byName != 0 ? byName : string.CompareOrdinal(a.PartId, b.PartId);
            });
            return list;
        }

        /// <summary>“过载、寻的”；全空时“没有固件”。</summary>
        public static string DescribeLoadout(IEnumerable<string> contentIds)
        {
            var names = new List<string>();
            foreach (string id in contentIds ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id))
                {
                    names.Add(FirmwareKinds.DisplayName(id) ?? id);
                }
            }
            return names.Count == 0
                ? GameText.Get("signal.core.loadout_empty")
                : string.Join(GameText.Get("signal.core.summary_sep"), names);
        }

        /// <summary>“信号核：过载、寻的”（远征准备面板、提示用）。</summary>
        public static string SummaryText(CampaignState s)
        {
            string[] ids = CurrentContentIds(s);
            return ids.Any(id => id.Length > 0)
                ? GameText.Format("signal.core.summary", DescribeLoadout(ids))
                : GameText.Get("signal.core.summary_empty");
        }

        // ── 装卸（FGR-SIG-011）────────────────────────────────────────────────────────

        /// <summary>把基元仓里的固件芯片装进第 <paramref name="slotIndex"/>（0 起）号槽。槽里已有固件时二者交换
        /// （原来的卸回基元仓——进来的那枚先离开基元仓，所以不会因为仓满失败）。芯片已在另一个槽位时等同换位。</summary>
        public static SignalCoreResult TryEquip(CampaignState s, string partId, int slotIndex)
        {
            if (!CanEdit(s, out SignalCoreResult denial))
            {
                return denial;
            }
            if (string.IsNullOrEmpty(partId))
            {
                return SignalCoreResult.Fail(CodeNothingSelected, GameText.Get("signal.reason.nothing_selected"));
            }
            if (slotIndex < 0 || slotIndex >= MaxSlots)
            {
                return SignalCoreResult.Fail(CodeSlotInvalid, GameText.Get("signal.reason.slot_invalid"));
            }
            if (!IsSlotUnlocked(s, slotIndex))
            {
                return SignalCoreResult.Fail(CodeSlotLocked, GameText.Format("signal.reason.slot_locked", slotIndex + 1, TierForSlot(slotIndex)));
            }
            PrimitiveChipRecord chip = PrimitiveInventory.Find(s, partId);
            if (chip == null)
            {
                return SignalCoreResult.Fail(CodeNotFound, GameText.Get("signal.reason.not_found"));
            }
            if (!FirmwareKinds.IsFirmware(chip.CardDefId))
            {
                return SignalCoreResult.Fail(CodeNotFirmware, GameText.Get("signal.reason.not_firmware"));
            }
            if (chip.State == PrimitiveChipState.SignalCore)
            {
                int from = IndexOfPart(s, partId);
                if (from == slotIndex)
                {
                    return SignalCoreResult.Fail(CodeSameSlot, GameText.Get("signal.reason.same_slot"));
                }
                if (from >= 0)
                {
                    return TrySwapSlots(s, from, slotIndex);
                }
            }
            if (chip.State != PrimitiveChipState.Bag)
            {
                return SignalCoreResult.Fail(CodeNotInBag, GameText.Get("signal.reason.not_in_bag"));
            }
            if (!string.IsNullOrEmpty(chip.ReservedByTransactionId))
            {
                return SignalCoreResult.Fail(CodeReserved, GameText.Get("signal.reason.reserved"));
            }

            SignalCoreState core = s.SignalCore;
            EnsureSlotArray(s);
            string outgoing = core.SlotPartIds[slotIndex] ?? string.Empty;
            CircuitOpResult moved = PrimitiveInventory.TryMoveToSignalCore(s, partId);
            if (!moved.Success)
            {
                return SignalCoreResult.Fail(MapInventoryCode(moved.Code), MapInventoryMessage(moved.Code));
            }
            core.SlotPartIds[slotIndex] = partId;
            string name = FirmwareKinds.DisplayName(chip.CardDefId) ?? chip.CardDefId;
            if (outgoing.Length == 0)
            {
                Revision++;
                return SignalCoreResult.Ok(GameText.Format("signal.core.equipped", name, slotIndex + 1));
            }
            CircuitOpResult back = PrimitiveInventory.TryReturnFromSignalCore(s, outgoing);
            if (!back.Success)
            {
                // 进来的那枚刚离开基元仓，理论上一定放得下；万一失败，整体回滚，什么都不改。
                core.SlotPartIds[slotIndex] = outgoing;
                chip.State = PrimitiveChipState.Bag;
                Log.Error($"[SignalCoreService] 交换时卸回 {outgoing} 失败（{back.Code}），已回滚。");
                return SignalCoreResult.Fail(CodeInternal, MapInventoryMessage(back.Code));
            }
            Revision++;
            string outName = FirmwareKinds.DisplayName(PrimitiveInventory.Find(s, outgoing)?.CardDefId) ?? outgoing;
            return SignalCoreResult.Ok(GameText.Format("signal.core.swapped", name, slotIndex + 1, outName));
        }

        /// <summary>把第 <paramref name="slotIndex"/> 号槽的固件卸回基元仓。基元仓满时拒绝，固件留在原槽。</summary>
        public static SignalCoreResult TryUnequip(CampaignState s, int slotIndex)
        {
            if (!CanEdit(s, out SignalCoreResult denial))
            {
                return denial;
            }
            if (slotIndex < 0 || slotIndex >= MaxSlots)
            {
                return SignalCoreResult.Fail(CodeSlotInvalid, GameText.Get("signal.reason.slot_invalid"));
            }
            string partId = SlotPartId(s, slotIndex);
            if (partId.Length == 0)
            {
                return SignalCoreResult.Fail(CodeSlotEmpty, GameText.Format("signal.reason.slot_empty", slotIndex + 1));
            }
            if (PrimitiveInventory.BagCount(s) >= PrimitiveInventory.Capacity)
            {
                return SignalCoreResult.Fail(CodeBagFull, GameText.Get("signal.reason.bag_full"));
            }
            string contentId = PrimitiveInventory.Find(s, partId)?.CardDefId;
            CircuitOpResult back = PrimitiveInventory.TryReturnFromSignalCore(s, partId);
            if (!back.Success)
            {
                return SignalCoreResult.Fail(MapInventoryCode(back.Code), MapInventoryMessage(back.Code));
            }
            s.SignalCore.SlotPartIds[slotIndex] = string.Empty;
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.unequipped", FirmwareKinds.DisplayName(contentId) ?? contentId));
        }

        /// <summary>对调两个槽位（改变插入顺序）。目标槽未解锁且要放进固件时拒绝。</summary>
        public static SignalCoreResult TrySwapSlots(CampaignState s, int a, int b)
        {
            if (!CanEdit(s, out SignalCoreResult denial))
            {
                return denial;
            }
            int max = MaxSlots;
            if (a < 0 || a >= max || b < 0 || b >= max)
            {
                return SignalCoreResult.Fail(CodeSlotInvalid, GameText.Get("signal.reason.slot_invalid"));
            }
            if (a == b)
            {
                return SignalCoreResult.Fail(CodeSameSlot, GameText.Get("signal.reason.same_slot"));
            }
            string pa = SlotPartId(s, a);
            string pb = SlotPartId(s, b);
            if (pa.Length == 0 && pb.Length == 0)
            {
                return SignalCoreResult.Fail(CodeSlotEmpty, GameText.Format("signal.reason.slot_empty", a + 1));
            }
            if (pa.Length > 0 && !IsSlotUnlocked(s, b))
            {
                return SignalCoreResult.Fail(CodeSlotLocked, GameText.Format("signal.reason.slot_locked", b + 1, TierForSlot(b)));
            }
            if (pb.Length > 0 && !IsSlotUnlocked(s, a))
            {
                return SignalCoreResult.Fail(CodeSlotLocked, GameText.Format("signal.reason.slot_locked", a + 1, TierForSlot(a)));
            }
            EnsureSlotArray(s);
            s.SignalCore.SlotPartIds[a] = pb;
            s.SignalCore.SlotPartIds[b] = pa;
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.moved", Math.Min(a, b) + 1, Math.Max(a, b) + 1));
        }

        // ── 刻印（过渡渠道，DEBT-FG1SIG01-02）──────────────────────────────────────────

        /// <summary>在家园装配站刻印一枚固件芯片进基元仓（不改信号核本身，远征途中也可以下单）。</summary>
        public static SignalCoreResult TryPrintFirmwareChip(CampaignState s, string firmwareId)
        {
            if (s == null)
            {
                return SignalCoreResult.Fail(CodeNoCampaign, GameText.Get("signal.reason.no_campaign"));
            }
            bool isFirmware = FirmwareKinds.IsFirmware(firmwareId);
            if (!isFirmware)
            {
                return SignalCoreResult.Fail(CodePrintUnknown, GameText.Format("signal.reason.print_unknown", firmwareId ?? string.Empty));
            }
            string name = FirmwareKinds.DisplayName(firmwareId) ?? firmwareId;
            int cost = FirmwareChipPrintScrap;
            CircuitOpResult r = PrimitiveInventory.TryPrintFirmwareChip(s, firmwareId, true,
                MechanicalContentUnlock.IsUnlocked(s, firmwareId), cost, out string partId);
            if (!r.Success)
            {
                switch (r.Code)
                {
                    case "not-unlocked":
                        return SignalCoreResult.Fail(CodePrintLocked, GameText.Format("signal.reason.print_locked", name));
                    case "bag-full":
                        return SignalCoreResult.Fail(CodePrintBagFull, GameText.Get("signal.reason.print_bag_full"));
                    case "no-power":
                        return SignalCoreResult.Fail(CodePrintNoPower, GameText.Get("signal.reason.print_no_power"));
                    default:
                        return SignalCoreResult.Fail(CodePrintScrap, GameText.Format("signal.reason.print_scrap", cost));
                }
            }
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.printed", name), partId);
        }

        /// <summary>可以刻印的固件（已解锁），按固件目录顺序。</summary>
        public static List<string> PrintableFirmware(CampaignState s)
        {
            var list = new List<string>();
            foreach (string id in FirmwareCatalog.All.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (MechanicalContentUnlock.IsUnlocked(s, id))
                {
                    list.Add(id);
                }
            }
            return list;
        }

        // ── 预设（FG01 第 4 章：保存为预设、一键切换，只能在家园切换）────────────────────────

        public static SignalCorePresetRecord FindPreset(CampaignState s, string presetId) =>
            string.IsNullOrEmpty(presetId) ? null : s?.SignalCore?.Presets?.FirstOrDefault(p => p != null && p.PresetId == presetId);

        /// <summary>预设内容与当前信号核是否一致（界面显示“（已改动）”用）。</summary>
        public static bool PresetMatchesCurrent(CampaignState s, SignalCorePresetRecord p)
        {
            if (p == null)
            {
                return false;
            }
            string[] cur = CurrentContentIds(s);
            for (int i = 0; i < cur.Length; i++)
            {
                string want = p.SlotContentIds != null && i < p.SlotContentIds.Length ? p.SlotContentIds[i] ?? string.Empty : string.Empty;
                if (!string.Equals(want, cur[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>把当前信号核另存为新预设。<paramref name="name"/> 为空时用“配置 N”。</summary>
        public static SignalCoreResult TrySavePreset(CampaignState s, string name)
        {
            if (s == null)
            {
                return SignalCoreResult.Fail(CodeNoCampaign, GameText.Get("signal.reason.no_campaign"));
            }
            CampaignFgStateDomains.EnsureAll(s);
            SignalCoreState core = s.SignalCore;
            if (core.Presets.Length >= MaxPresets)
            {
                return SignalCoreResult.Fail(CodePresetLimit, GameText.Format("signal.reason.preset_limit", MaxPresets));
            }
            string clean = (name ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                clean = GameText.Format("signal.core.preset.default_name", core.NextPresetSerial);
            }
            if (!NameFits(clean))
            {
                return SignalCoreResult.Fail(CodePresetNameLong, GameText.Format("signal.reason.preset_name_long", PresetNameMaxChars));
            }
            string id = "sigpreset_" + core.NextPresetSerial.ToString(CultureInfo.InvariantCulture);
            core.NextPresetSerial++;
            var record = new SignalCorePresetRecord { PresetId = id, Name = clean, SlotContentIds = CurrentContentIds(s) };
            core.Presets = core.Presets.Append(record).ToArray();
            core.ActivePresetId = id;
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.preset.saved", clean), id);
        }

        /// <summary>用当前信号核覆盖一个已有预设（界面先弹确认框，列出新旧配置）。</summary>
        public static SignalCoreResult TryOverwritePreset(CampaignState s, string presetId)
        {
            SignalCorePresetRecord p = FindPreset(s, presetId);
            if (p == null)
            {
                return SignalCoreResult.Fail(CodePresetNotFound, GameText.Get("signal.reason.preset_not_found"));
            }
            p.SlotContentIds = CurrentContentIds(s);
            s.SignalCore.ActivePresetId = p.PresetId;
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.preset.overwritten", p.Name));
        }

        public static SignalCoreResult TryRenamePreset(CampaignState s, string presetId, string name)
        {
            SignalCorePresetRecord p = FindPreset(s, presetId);
            if (p == null)
            {
                return SignalCoreResult.Fail(CodePresetNotFound, GameText.Get("signal.reason.preset_not_found"));
            }
            string clean = (name ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return SignalCoreResult.Fail(CodePresetNameEmpty, GameText.Get("signal.reason.preset_name_empty"));
            }
            if (!NameFits(clean))
            {
                return SignalCoreResult.Fail(CodePresetNameLong, GameText.Format("signal.reason.preset_name_long", PresetNameMaxChars));
            }
            p.Name = clean;
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.preset.renamed", clean));
        }

        public static SignalCoreResult TryDeletePreset(CampaignState s, string presetId)
        {
            SignalCorePresetRecord p = FindPreset(s, presetId);
            if (p == null)
            {
                return SignalCoreResult.Fail(CodePresetNotFound, GameText.Get("signal.reason.preset_not_found"));
            }
            SignalCoreState core = s.SignalCore;
            core.Presets = core.Presets.Where(x => x != p).ToArray();
            if (core.ActivePresetId == p.PresetId)
            {
                core.ActivePresetId = string.Empty;
            }
            Revision++;
            return SignalCoreResult.Ok(GameText.Format("signal.core.preset.deleted", p.Name));
        }

        /// <summary>切换到预设（只能在家园）。整体原子：先算好每个槽用哪个实例、要卸回几枚，仓位不够就整个拒绝、什么都不改；
        /// 实例优先用已经在正确槽位的，其次信号核里别的槽位的，最后基元仓里的（同内容按实例 ID 排序，结果确定）。
        /// 找不到的固件对应槽位留空，并在结果文字里列出。</summary>
        public static SignalCoreResult TryApplyPreset(CampaignState s, string presetId)
        {
            if (!CanEdit(s, out SignalCoreResult denial))
            {
                return denial;
            }
            SignalCorePresetRecord p = FindPreset(s, presetId);
            if (p == null)
            {
                return SignalCoreResult.Fail(CodePresetNotFound, GameText.Get("signal.reason.preset_not_found"));
            }
            EnsureSlotArray(s);
            int max = MaxSlots;
            int unlocked = UnlockedSlots(s);
            string[] current = (string[])s.SignalCore.SlotPartIds.Clone();
            var desired = new string[max];
            for (int i = 0; i < max; i++)
            {
                desired[i] = i < unlocked && p.SlotContentIds != null && i < p.SlotContentIds.Length ? p.SlotContentIds[i] ?? string.Empty : string.Empty;
            }

            var result = new string[max];
            var used = new HashSet<string>(StringComparer.Ordinal);
            // 1) 已经在正确槽位的保持不动。
            for (int i = 0; i < max; i++)
            {
                if (desired[i].Length > 0 && current[i].Length > 0 && PrimitiveInventory.Find(s, current[i])?.CardDefId == desired[i])
                {
                    result[i] = current[i];
                    used.Add(current[i]);
                }
            }
            // 2) 其余按槽位顺序：信号核里别的槽位的同内容实例，其次基元仓里的。
            List<PrimitiveChipRecord> bag = BagFirmware(s).Where(c => string.IsNullOrEmpty(c.ReservedByTransactionId)).ToList();
            var missing = new List<string>();
            for (int i = 0; i < max; i++)
            {
                if (result[i] != null)
                {
                    continue;
                }
                if (desired[i].Length == 0)
                {
                    result[i] = string.Empty;
                    continue;
                }
                string pick = null;
                for (int j = 0; j < max && pick == null; j++)
                {
                    if (current[j].Length > 0 && !used.Contains(current[j]) && PrimitiveInventory.Find(s, current[j])?.CardDefId == desired[i])
                    {
                        pick = current[j];
                    }
                }
                if (pick == null)
                {
                    pick = bag.FirstOrDefault(c => c.CardDefId == desired[i] && !used.Contains(c.PartId))?.PartId;
                }
                if (pick == null)
                {
                    missing.Add(FirmwareKinds.DisplayName(desired[i]) ?? desired[i]);
                    result[i] = string.Empty;
                    continue;
                }
                result[i] = pick;
                used.Add(pick);
            }

            var fromBag = result.Where(id => id.Length > 0 && PrimitiveInventory.Find(s, id)?.State == PrimitiveChipState.Bag).ToList();
            var toReturn = current.Where(id => id.Length > 0 && !used.Contains(id)).ToList();
            int free = PrimitiveInventory.Capacity - PrimitiveInventory.BagCount(s);
            int net = toReturn.Count - fromBag.Count;
            if (net > free)
            {
                return SignalCoreResult.Fail(CodePresetBagFull, GameText.Format("signal.reason.preset_bag_full", net, Math.Max(0, free)));
            }

            // 提交：先把要装的从仓里拿出来（腾出仓位），再把要卸的放回去。任何一步意外失败都整体回滚。
            var snapshot = new Dictionary<string, PrimitiveChipState>(StringComparer.Ordinal);
            foreach (string id in fromBag.Concat(toReturn))
            {
                snapshot[id] = PrimitiveInventory.Find(s, id).State;
            }
            bool ok = true;
            foreach (string id in fromBag)
            {
                if (ok)
                {
                    ok = PrimitiveInventory.TryMoveToSignalCore(s, id).Success;
                }
            }
            foreach (string id in toReturn)
            {
                if (ok)
                {
                    ok = PrimitiveInventory.TryReturnFromSignalCore(s, id).Success;
                }
            }
            if (!ok)
            {
                foreach (KeyValuePair<string, PrimitiveChipState> kv in snapshot)
                {
                    PrimitiveInventory.Find(s, kv.Key).State = kv.Value;
                }
                Log.Error($"[SignalCoreService] 切换预设 {p.PresetId} 中途失败，已整体回滚。");
                return SignalCoreResult.Fail(CodeInternal, GameText.Get("signal.reason.not_found"));
            }
            s.SignalCore.SlotPartIds = result;
            s.SignalCore.ActivePresetId = p.PresetId;
            Revision++;
            return missing.Count == 0
                ? SignalCoreResult.Ok(GameText.Format("signal.core.preset.applied", p.Name))
                : SignalCoreResult.Ok(GameText.Format("signal.core.preset.applied_missing", p.Name, string.Join(GameText.Get("signal.core.summary_sep"), missing)));
        }

        // ── 内部 ─────────────────────────────────────────────────────────────────────

        private static bool NameFits(string name) => new StringInfo(name).LengthInTextElements <= PresetNameMaxChars;

        private static int IndexOfPart(CampaignState s, string partId)
        {
            string[] slots = s?.SignalCore?.SlotPartIds ?? Array.Empty<string>();
            for (int i = 0; i < slots.Length; i++)
            {
                if (string.Equals(slots[i], partId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>写入前把槽位数组补齐到最多槽位数（空槽为空串）；超长时先按一致性修复截断。</summary>
        private static void EnsureSlotArray(CampaignState s)
        {
            int max = MaxSlots;
            string[] a = s.SignalCore.SlotPartIds ?? Array.Empty<string>();
            if (a.Length > max)
            {
                EnsureConsistent(s);
                a = s.SignalCore.SlotPartIds;
            }
            if (a.Length == max)
            {
                return;
            }
            var padded = new string[max];
            for (int i = 0; i < max; i++)
            {
                padded[i] = i < a.Length ? a[i] ?? string.Empty : string.Empty;
            }
            s.SignalCore.SlotPartIds = padded;
        }

        private static string MapInventoryCode(string code) => code switch
        {
            "part-not-found" => CodeNotFound,
            "not-in-bag" => CodeNotInBag,
            "reserved-for-craft" => CodeReserved,
            "bag-full" => CodeBagFull,
            _ => CodeInternal,
        };

        private static string MapInventoryMessage(string code) => code switch
        {
            "part-not-found" => GameText.Get("signal.reason.not_found"),
            "not-in-bag" => GameText.Get("signal.reason.not_in_bag"),
            "reserved-for-craft" => GameText.Get("signal.reason.reserved"),
            "bag-full" => GameText.Get("signal.reason.bag_full"),
            _ => GameText.Get("signal.reason.not_found"),
        };

        /// <summary>自检用：清掉注入与缓存的告警。</summary>
        public static void ResetForTests()
        {
            OverrideArrayTierProvider = null;
            ExpeditionUnderwayOverrideForTests = null;
            WarnedTuning.Clear();
            Revision++;
        }
    }
}
