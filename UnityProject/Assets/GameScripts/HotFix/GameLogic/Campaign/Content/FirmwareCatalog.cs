using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG2-FW-01（FG02 FGR-FW-001～003）：固件目录——44 条固件，**全部由 Luban 表 fg.TbFirmwareKind 生成**
    /// （数据源 tools/cell_tables/fgdata_firmware.py；Demo 的 6 条手写条目已迁入表，ID 不变，存档 / 蓝图 / 信号核里的实例原样可用）。
    ///
    /// - 玩家可见的名称、描述、获取途径、数值摘要都来自文本键（fg.TbLocText），按当前语言生成；
    ///   旧基因 ID 只写进 <see cref="MechanicalContentDef.LegacyFacadeId"/>（机器电路编译用的等价实现）与调试字段，不进任何玩家可见文本。
    /// - 目录按 <see cref="FirmwareKinds.CatalogRevision"/> 与当前语言缓存：表重载 / 测试注入表或种类 / 切换语言后下一次访问自动重建（O(44)，不按帧）。
    /// - 表不可用（安装或热更包损坏）时目录为空并记 Error（<see cref="LoadError"/>）；读档对账遇到这种情况不会把固件芯片当成“已移除内容”
    ///   转成废料（<see cref="SaveContentReconciler"/>）。
    /// 装配顺序 / 负载上限校验在 <see cref="Blueprint.BlueprintCircuitBoard"/>；种类 / 协议 / 读法等规则在 <see cref="FirmwareKinds"/>。
    /// </summary>
    public static class FirmwareCatalog
    {
        public const string FwHomingId = "fw_homing";
        public const string FwSplitId = "fw_split";
        public const string FwTrailId = "fw_trail";
        public const string FwOverloadId = "fw_overload";
        public const string FwMarkTagId = "fw_marktag";
        public const string FwArmorPierceId = "fw_armorpierce";
        // FG1-SIG-05 预留、FG2-FW-01 迁入的另外 4 条核心固件
        public const string FwCapacitorId = "fw_capacitor";
        public const string FwSwarmId = "fw_swarm";
        public const string FwAmplifyId = "fw_amplify";
        public const string FwExecuteId = "fw_execute";

        /// <summary>设计案 5.4 的全量条数（FGR-FW-002）。</summary>
        public const int ExpectedCount = 44;

        /// <summary>Demo 6 条的调试层字段（真实代码入口、DEBT 号、剪影说明）：只给审计与调试看，不进玩家可见文本。
        /// 其余 38 条的这些字段按表统一生成。</summary>
        private static readonly Dictionary<string, (string ActionId, string VfxId, string SilhouetteNote, string DebtId)> DemoDebug =
            new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal)
            {
                [FwHomingId] = ("SandboxAssembler.Compose(geneIds:[\"gene_taxis\"]) — 真实 Engine.Fire 链路，HomingModule", "vfx_none_placeholder",
                    "无独立几何体，随弹道表现（HomingModule 数值 0.85，非精确 30° 角上限）。", "DEBT-ER4CONTENT01-03"),
                [FwSplitId] = ("SandboxAssembler.Compose(geneIds:[\"gene_split\"]) — 真实 Engine.Fire 链路，SplitModule", "vfx_none_placeholder",
                    "无独立几何体，随命中特效表现。", "DEBT-ER4CONTENT01-03"),
                [FwTrailId] = ("SandboxAssembler.Compose(geneIds:[\"gene_slime\"]) — 真实 Engine.Fire 链路，TrailModule", "vfx_trail_placeholder",
                    "路径拖影表现（含减速副作用）。", "DEBT-ER4CONTENT01-03"),
                [FwOverloadId] = ("CombatSite.MachineWeaponFrom（熔穿过载）+ gene_heatshock 等价实现（接入口插入）", "vfx_overload_placeholder",
                    "过热红光表现。", "DEBT-ER4CONTENT01-09"),
                [FwMarkTagId] = ("CombatLogic.MarkJump（内核标记跳转，接入口插入）", "vfx_chain_placeholder",
                    "跳跃弧线表现（全新内容，无旧基因参照）。", "DEBT-ER4CONTENT01-06"),
                [FwArmorPierceId] = ("CarrierReadings.Build（原生读法字段 armor）→ CombatLogic.StrikeDamage 从正面装甲减伤里扣掉（FG2-FW-02，DEBT-FG1SIG06-03 关闭）", "vfx_none_placeholder",
                    "无独立几何体（全新内容，无旧基因参照）。", null),
            };

        private static Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>(StringComparer.Ordinal);
        private static int _builtRevision = -1;
        private static GameLanguage _builtLanguage;
        private static int _builtTextRevision = -1;
        /// <summary>每重建一次 +1：<see cref="Revision"/> 用它，Invalidate / 文本表重载后重建出的新目录一定换版本号。</summary>
        private static int _buildCount;
        private static bool _building;

        /// <summary>全部固件（按表生成）。</summary>
        public static IReadOnlyDictionary<string, MechanicalContentDef> All
        {
            get
            {
                EnsureBuilt();
                return _defs;
            }
        }

        /// <summary>目录内容的版本（表重载 / 注入 / 切换语言 / 文本表重载 / <see cref="Invalidate"/> 后变化）。聚合目录 <see cref="MechanicalContentFacade"/> 据此重建。</summary>
        public static int Revision
        {
            get
            {
                EnsureBuilt();
                return _buildCount;
            }
        }

        /// <summary>固件表加载失败的原因；null 表示正常。</summary>
        public static string LoadError => FirmwareKinds.LoadError;

        public static bool TryGet(string id, out MechanicalContentDef def)
        {
            if (string.IsNullOrEmpty(id))
            {
                def = null;
                return false;
            }
            EnsureBuilt();
            return _defs.TryGetValue(id, out def);
        }

        /// <summary>强制下一次访问重建（文本表经 GameText.Reload / OverrideForTests 换过时已自动重建，不必再调）。
        /// 重建后 <see cref="Revision"/> 变化，聚合目录随之重建。</summary>
        public static void Invalidate() => _builtRevision = -1;

        private static void EnsureBuilt()
        {
            int rev = FirmwareKinds.CatalogRevision;
            GameLanguage lang = GameText.Language;
            int textRev = GameText.Revision;
            if (_building || (_builtRevision == rev && _builtLanguage == lang && _builtTextRevision == textRev))
            {
                return;
            }
            _building = true;
            try
            {
                var defs = new Dictionary<string, MechanicalContentDef>(StringComparer.Ordinal);
                foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id))
                    {
                        continue;
                    }
                    if (defs.ContainsKey(row.Id))
                    {
                        Log.Error($"[FirmwareCatalog] fg.TbFirmwareKind 里固件 {row.Id} 重复，只取第一行（check_luban R2 应已拦下）。");
                        continue;
                    }
                    defs[row.Id] = Build(row);
                }
                _defs = defs;
                _builtRevision = rev;
                _builtLanguage = lang;
                _builtTextRevision = textRev;
                _buildCount++;
            }
            finally
            {
                _building = false;
            }
        }

        private static MechanicalContentDef Build(GameConfig.fg.FirmwareKind row)
        {
            bool core = FirmwareKinds.KindOfRow(row) == FirmwareKind.Core;
            bool hasLegacy = !string.IsNullOrEmpty(row.LegacyId) && row.LegacyId != "none";
            string acquire = GameText.Get(row.AcquireKey);
            DemoDebug.TryGetValue(row.Id, out (string ActionId, string VfxId, string SilhouetteNote, string DebtId) demo);
            return new MechanicalContentDef
            {
                Id = row.Id,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = GameText.Get(row.NameKey),
                Description = GameText.Get(row.DescKey),
                Source = SourceOf(row.Source),
                SourceDetail = acquire,
                Slot = "固件",
                ScrapCost = Math.Max(0, row.Scrap),
                Load = Math.Max(1, row.Load),
                // FG4-ECO-04（DEBT-FG2FW01-02 范围变更）：不再显示每发耗电（{1} 位置留空，能耗只在数据层）。
                ValuesSummary = GameText.Format("firmware.values_summary",
                    row.Load.ToString(CultureInfo.InvariantCulture), string.Empty,
                    row.Heat.ToString("0.#", CultureInfo.InvariantCulture), row.Scrap.ToString(CultureInfo.InvariantCulture)),
                // FG1-SIG-05（FGR-SIG-090）：核心固件 AI 永远不用。
                AiPermission = core ? MechanicalContentAiPermission.PlayerOnly : MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = row.Icon,
                ModelId = "primitive:capsule",
                ActionId = demo.ActionId ?? (hasLegacy
                    ? $"CarrierReadings.Build（读法字段 {row.ReadFields}，载体 × 字段查 fg.TbCarrierReading）→ 战斗内核 CombatLogic；伤害另经 GeneCatalog.GetModule(\"{row.LegacyId}\") 编译"
                    : $"CarrierReadings.Build（原生读法字段 {row.ReadFields}）→ 战斗内核 CombatLogic"),
                VfxId = demo.VfxId ?? "vfx_none_placeholder",
                SfxId = string.Empty, // 固件本身不发声，声音由所属武器或反应发出（FeedbackCueSelfCheck 约定）
                PreviewId = "preview_" + row.Id,
                SaveCompatible = true,
                LockedHintText = acquire,
                SilhouetteNote = demo.SilhouetteNote ?? $"机身形变：{row.Morph}（类别 {row.Category}，FGR-FW-020）；引信类只改弹体特效。",
                LegacyFacadeId = hasLegacy ? row.LegacyId : null,
                DebtId = demo.DebtId,
            };
        }

        /// <summary>调试 / 行为探针层（FG2-FW-01 自检、FG2-FW-03 反应探针复用）：实例化这条固件在机器电路里的等价实现模块，
        /// 对一个空能量包跑一步，返回它真实贴上的状态标签（旧引擎标签字符串）。没有等价实现（核心之外的新建固件）或不是固件时返回 null。
        /// 与编译器同一个入口（<see cref="MetabolicSlice.ContentCatalog.GeneCatalog.GetModule"/>），不另写一套。</summary>
        public static string[] ProbeModuleTags(string firmwareId)
        {
            if (!TryGet(firmwareId, out MechanicalContentDef def) || string.IsNullOrEmpty(def.LegacyFacadeId))
            {
                return null;
            }
            Func<ComposeEngine.Core.IModule> create = MetabolicSlice.ContentCatalog.GeneCatalog.GetModule(def.LegacyFacadeId);
            if (create == null)
            {
                return null;
            }
            var packet = new ComposeEngine.Core.Packet { Energy = 10f };
            ComposeEngine.Core.Packet result = create().Step(packet, new ComposeEngine.Core.SimContext(1)) ?? packet;
            var tags = new List<string>(result.Tags);
            tags.Sort(StringComparer.Ordinal);
            return tags.ToArray();
        }

        /// <summary>表的获取途径键 → 内容来源（解锁规则 <see cref="MechanicalContentUnlock"/> 只认“基础蓝图库”开局可用，其余要解锁）。</summary>
        internal static MechanicalContentSource SourceOf(string source)
        {
            switch (source)
            {
                case "base": return MechanicalContentSource.BaseBlueprint;
                case "terminal": return MechanicalContentSource.SilentRuinsSalvage;
                case "cache": return MechanicalContentSource.FoundryOptionalCache;
                case "relic": return MechanicalContentSource.RelicTerminal;
                case "salvage": return MechanicalContentSource.RegionSalvage;
                case "elite": return MechanicalContentSource.EliteDrop;
                case "boss": return MechanicalContentSource.FactionBoss;
                default:
                    Log.Error($"[FirmwareCatalog] 未知的获取途径 {source}（check_luban R24 应已拦下），按区域掉落处理。");
                    return MechanicalContentSource.RegionSalvage;
            }
        }
    }
}
