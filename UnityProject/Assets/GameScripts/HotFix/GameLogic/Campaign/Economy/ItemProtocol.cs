using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>FG8-LOOT-01：一件物品 / 一枚固件带回来以后的去向（FGR-LOOT-001“直接使用 / 需要反破解”）。</summary>
    public enum ItemDestination : byte
    {
        /// <summary>可直接使用：带回来就能装配，或直接进产线（己方 / 中立；已破解的敌方固件）。</summary>
        DirectUse = 0,
        /// <summary>需要解析：敌方加密物品（未解析模块、仓库里的敌方加密固件、数据核心），先送解析台。</summary>
        NeedsAnalysis = 1,
        /// <summary>需要解析，破解前可以裸跑：固件库里未破解的敌方固件芯片——装不进机器 / 炮塔，只能放进信号核裸跑（FGR-SIG-060）。</summary>
        NeedsAnalysisOrRaw = 2,
    }

    /// <summary>
    /// FG8-LOOT-01（FG08 FGR-LOOT-001 物品协议；验收 FGT-EXP-010）：物品协议与两种去向的唯一判定入口。
    /// - 物品的协议来自物品表（fg.TbEcoItem.protocol → <see cref="ItemDef.Protocol"/>）；固件芯片按固件本身（fg.TbFirmwareKind.protocol），
    ///   敌方固件是否“已破解”按战役（<see cref="FirmwareKinds.IsRaw(CampaignState, string)"/>，按内容记，解析台完成时写入）。
    /// - 界面用图标 + 文字标明协议（■己方 / ●中立 / ▲敌方加密，形状不只靠颜色，B15），详情写明“需要解析”或“可直接使用”。
    /// - 拒绝直接使用敌方加密物品时给出说明（<see cref="CheckDirectUse"/>；固件的宿主判定仍是 <see cref="FirmwareKinds.CanInstall"/>，这里只补“去向”文字）。
    /// 全部 O(1)，只在界面刷新 / 玩家操作 / 掉落结算时调用，不按帧。
    /// </summary>
    public static class ItemProtocol
    {
        public const string GlyphOwn = "■";
        public const string GlyphNeutral = "●";
        public const string GlyphEnemy = "▲";

        /// <summary>第一次拒绝直接使用敌方加密物品（回收站退回、装配被拒）的引导钩子（内容在 FG15-UX-04）。</summary>
        public const string HookFirstRejected = GuidanceHooks.LootFirstEncryptedRejected;

        /// <summary>自检读点：本进程拒绝过几次。</summary>
        public static int RejectedCount { get; private set; }

        public static void ResetSessionState() => RejectedCount = 0;

        public static ItemProtocolKind Of(ItemDef item) => item?.Protocol ?? ItemProtocolKind.Neutral;

        public static ItemProtocolKind Of(string itemId) => ItemCatalog.TryGet(itemId, out ItemDef d) ? d.Protocol : ItemProtocolKind.Neutral;

        /// <summary>固件本身的协议（fg.TbFirmwareKind.protocol：own / neutral / enemy；不是固件时己方）。</summary>
        public static ItemProtocolKind OfFirmware(string firmwareId)
        {
            if (FirmwareKinds.IsEnemyProtocol(firmwareId))
            {
                return ItemProtocolKind.Enemy;
            }
            return FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row) && row.Protocol == "neutral"
                ? ItemProtocolKind.Neutral
                : ItemProtocolKind.Own;
        }

        /// <summary>物品的去向：敌方加密 = 需要解析；其余可直接使用。</summary>
        public static ItemDestination DestinationOf(ItemDef item) =>
            Of(item) == ItemProtocolKind.Enemy ? ItemDestination.NeedsAnalysis : ItemDestination.DirectUse;

        /// <summary>一枚固件芯片的去向：未破解的敌方固件 = 需要解析（破解前可以裸跑）；已破解的敌方固件、己方 / 中立固件 = 可直接使用。</summary>
        public static ItemDestination DestinationOfFirmware(CampaignState state, string firmwareId) =>
            FirmwareKinds.IsRaw(state, firmwareId) ? ItemDestination.NeedsAnalysisOrRaw : ItemDestination.DirectUse;

        public static string Glyph(ItemProtocolKind kind) => kind switch
        {
            ItemProtocolKind.Own => GlyphOwn,
            ItemProtocolKind.Enemy => GlyphEnemy,
            _ => GlyphNeutral,
        };

        /// <summary>协议名文本键（与固件库的协议筛选同一套文字）。</summary>
        public static string NameKey(ItemProtocolKind kind) => kind switch
        {
            ItemProtocolKind.Own => "firmware.protocol.own",
            ItemProtocolKind.Enemy => "firmware.protocol.enemy",
            _ => "firmware.protocol.neutral",
        };

        /// <summary>图标 + 文字的协议角标（“▲ 敌方加密”）。</summary>
        public static string Badge(ItemProtocolKind kind) => GameText.Format("loot.protocol.badge", Glyph(kind), GameText.Get(NameKey(kind)));

        /// <summary>去向的短文字（“需要解析” / “可直接使用”）。</summary>
        public static string DestinationShort(ItemDestination d) => GameText.Get(d == ItemDestination.DirectUse ? "loot.dest.direct" : "loot.dest.analysis");

        /// <summary>去向的详情文字（物品详情 / 图鉴 / 固件库详情）。</summary>
        public static string DestinationDetail(ItemDestination d) => d switch
        {
            ItemDestination.NeedsAnalysis => GameText.Get("loot.dest.analysis.detail"),
            ItemDestination.NeedsAnalysisOrRaw => GameText.Get("loot.dest.raw.detail"),
            _ => GameText.Get("loot.dest.direct.detail"),
        };

        /// <summary>物品详情的一行：“协议：▲ 敌方加密（需要解析：敌方加密，先送解析台反破解）”。</summary>
        public static string DetailLine(ItemDef item) =>
            GameText.Format("loot.protocol.line", Badge(Of(item)), DestinationDetail(DestinationOf(item)));

        /// <summary>固件芯片详情的一行：未破解写“需要解析…裸跑”，已破解的敌方固件写“已破解：可直接使用”。</summary>
        public static string FirmwareDetailLine(CampaignState state, string firmwareId)
        {
            ItemProtocolKind kind = OfFirmware(firmwareId);
            ItemDestination d = DestinationOfFirmware(state, firmwareId);
            string detail = kind == ItemProtocolKind.Enemy && d == ItemDestination.DirectUse ? GameText.Get("loot.dest.cracked.detail") : DestinationDetail(d);
            return GameText.Format("loot.protocol.line", Badge(kind), detail);
        }

        /// <summary>
        /// 能不能把这件物品直接拿去用（装配 / 进产线 / 回收）：敌方加密物品一律不能，<paramref name="reasonText"/> 写明“先送解析台”。
        /// 拒绝时计数并触发第一次拒绝的引导钩子。固件装进机器 / 炮塔 / 陷阱的判定走 <see cref="FirmwareKinds.CanInstall"/>（信号核可以裸跑）。
        /// </summary>
        public static bool CheckDirectUse(ItemDef item, out string reasonText)
        {
            reasonText = null;
            if (item == null || Of(item) != ItemProtocolKind.Enemy)
            {
                return true;
            }
            reasonText = GameText.Format("loot.reject.direct", item.Name);
            NoteRejected();
            return false;
        }

        /// <summary>记一次“拒绝直接使用敌方加密物品”（回收站退回、装配被拒都调）。</summary>
        public static void NoteRejected()
        {
            RejectedCount++;
            GuidanceHooks.Raise(HookFirstRejected);
        }
    }
}
