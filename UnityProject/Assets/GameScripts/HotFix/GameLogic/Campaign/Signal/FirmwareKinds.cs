using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>固件种类（FG02 FGR-FW-001“种类：常规 / 核心”、FGR-FW-003）。</summary>
    public enum FirmwareKind : byte
    {
        /// <summary>不是固件（例如 3×3 电路板用的基元芯片“聚焦镜”），不能放进信号核。</summary>
        NotFirmware = 0,
        /// <summary>常规固件：可以装在机器上，也可以放进信号核（FGR-SIG-012 第 3 条）。</summary>
        Regular = 1,
        /// <summary>核心固件：只能由信号携带，放不进机器电路（FGR-SIG-012）。</summary>
        Core = 2,
    }

    /// <summary>FG1-VFX-01（FG02 FGR-FW-001“类别”、设计案 5.3 四类物理包装）：机身形变按类别切换（FGR-FW-020），引信类只改弹体特效、不改机身。</summary>
    public enum FirmwareCategory : byte
    {
        /// <summary>不是固件，或表里没有类别（漏登记是开发错误，记 Error，不产生形变）。</summary>
        Unknown = 0,
        /// <summary>引信与弹芯：炮管不变、弹芯自己变——不改机身（弹体特效变体归 FG2-FW-04）。</summary>
        Fuse = 1,
        /// <summary>限制器与击发逻辑：散热鳍展开、枪管发红、天线升起（形变态）。核心固件全部在这一类。</summary>
        Limiter = 2,
        /// <summary>流体改道：机身开出喷口、管线外露（喷口态）。</summary>
        Fluid = 3,
        /// <summary>电磁场控：线圈发光（线圈态）。</summary>
        Electromagnetic = 4,
    }

    /// <summary>FG2-FW-01（FGR-FW-001“每种载体的读法说明”，设计案 5.1）：固件装在哪种载体上。与作战组件的载体一一对应：
    /// 射弹 = Projectile，格斗 = Melee，无人机 = Summon，力场 = Aura，布区 = Field。</summary>
    public enum FirmwareCarrier : byte
    {
        Projectile = 0,
        Melee = 1,
        Summon = 2,
        Aura = 3,
        Field = 4,
    }

    /// <summary>FG1-SIG-06（FGT-SIG-003）：固件能装进去的宿主。</summary>
    public enum FirmwareHost : byte
    {
        /// <summary>信号核（接入时插在接入口）。</summary>
        SignalCore = 0,
        /// <summary>机器电路（蓝图固件槽 / 3×3 电路格 / 保存校验）。</summary>
        MachineCircuit = 1,
        /// <summary>炮塔（FG6-DEF-02 做炮塔固件槽时调 <see cref="FirmwareKinds.CanInstall"/>）。</summary>
        Turret = 2,
    }

    /// <summary>
    /// FG1-SIG-01：固件种类的唯一真相——fg.TbFirmwareKind（数据源 tools/cell_tables/fgdata_signal.py）。
    /// 机器电路的两个入口（蓝图固件槽 <see cref="Blueprint.BlueprintCircuitBoard.TrySetFirmware"/>、3×3 电路格
    /// <see cref="Blueprint.BlueprintCircuitBoard.TryPlaceChip"/>、保存校验）与信号核都只读这里，不按固件 ID 写特例（IC-REQ-012）。
    ///
    /// 是不是固件：内容在 <see cref="FirmwareCatalog"/> 里。表里查不到种类的固件按“常规”处理并记 Error
    /// （自检保证目录里每条固件都有一行，漏登记是开发错误，不能让玩家卡住）。
    /// 名称：表里的 nameKey → <see cref="GameText"/>；表不可用时回退 Demo 目录的 DisplayName。
    /// </summary>
    public static class FirmwareKinds
    {
        private static TbFirmwareKind _table;
        private static TbFirmwareKind _tableOverride;
        private static string _tableOverrideError;
        private static int _catalogRevision = 1;
        private static bool _loaded;
        private static string _loadError;
        private static Dictionary<string, FirmwareKind> _override;
        private static readonly HashSet<string> WarnedMissing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>重载 / 测试注入时 +1，界面据此刷新。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        /// <summary>表里的全部行（自检比对源数据用）。表不可用时为空。</summary>
        public static IReadOnlyList<GameConfig.fg.FirmwareKind> Rows
        {
            get
            {
                EnsureLoaded();
                return _table?.DataList ?? (IReadOnlyList<GameConfig.fg.FirmwareKind>)Array.Empty<GameConfig.fg.FirmwareKind>();
            }
        }

        /// <summary>FG2-FW-01：固件目录（<see cref="FirmwareCatalog"/>）据此判断要不要按表重建——表重载、表注入、种类注入时 +1
        /// （破解状态变化不影响目录内容，不 +1）。</summary>
        internal static int CatalogRevision => _catalogRevision;

        /// <summary>FG2-FW-01：按固件 ID 取表里的一行（FGR-FW-001 全部字段）。表不可用或查不到时 false。O(1)。</summary>
        public static bool TryGetRow(string contentId, out GameConfig.fg.FirmwareKind row)
        {
            row = null;
            if (string.IsNullOrEmpty(contentId))
            {
                return false;
            }
            EnsureLoaded();
            return _table != null && _table.DataMap.TryGetValue(contentId, out row) && row != null;
        }

        /// <summary>目录构建用：一行的种类（尊重测试注入），不经过 <see cref="IsFirmware"/>（避免目录构建时递归）。</summary>
        internal static FirmwareKind KindOfRow(GameConfig.fg.FirmwareKind row)
        {
            if (row == null)
            {
                return FirmwareKind.NotFirmware;
            }
            if (_override != null)
            {
                return _override.TryGetValue(row.Id, out FirmwareKind forced) ? forced : FirmwareKind.Regular;
            }
            return row.Kind == "core" ? FirmwareKind.Core : FirmwareKind.Regular;
        }

        private static bool TryGetFirmwareRow(string contentId, out GameConfig.fg.FirmwareKind row)
        {
            row = null;
            return FirmwareCatalog.TryGet(contentId, out _) && TryGetRow(contentId, out row);
        }

        /// <summary>每发耗电（FGR-FW-001“能耗”）。不是固件时 0。</summary>
        public static int PowerOf(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? Math.Max(0, row.Power) : 0;

        /// <summary>每发积热（FGR-FW-001“热量”）。不是固件时 0。热量预算与重炮内核积热都读它（BlueprintCircuitBoard.ComputeHeatBudget）。</summary>
        public static float HeatOf(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? Math.Max(0f, row.Heat) : 0f;

        /// <summary>稀有度键（common / rare / epic）；名称走文本键 firmware.rarity.&lt;值&gt;。不是固件时 null。</summary>
        public static string RarityOf(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? row.Rarity : null;

        /// <summary>获取途径键（base / terminal / cache / relic / salvage / elite / boss）。不是固件时 null。</summary>
        public static string SourceOf(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? row.Source : null;

        /// <summary>获取途径说明（当前语言，图鉴与锁定提示用）。不是固件时 null。</summary>
        public static string AcquireText(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? GameText.Get(row.AcquireKey) : null;

        /// <summary>旧基因 ID（只用于追溯与调试层，玩家可见文本里不出现；设计案“新建”的两条为 none）。不是固件时 null。</summary>
        public static string LegacyIdOf(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) ? row.LegacyId : null;

        /// <summary>产生的状态标签（旧引擎标签字符串 = fg.TbStatusTag 的 ID；显示名见 <see cref="StatusTagCatalog"/>）。不是固件或不产生时为空。</summary>
        public static string[] TagsOf(string contentId)
        {
            if (!TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) || string.IsNullOrEmpty(row.Tags) || row.Tags == "none")
            {
                return Array.Empty<string>();
            }
            return row.Tags.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>FGR-FW-011：这条固件装在 <paramref name="carrier"/> 上会怎样（文本键）。不是固件时 null。</summary>
        public static string ReadingKey(string contentId, FirmwareCarrier carrier)
        {
            if (!TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row))
            {
                return null;
            }
            switch (carrier)
            {
                case FirmwareCarrier.Projectile: return row.ReadProjectileKey;
                case FirmwareCarrier.Melee: return row.ReadMeleeKey;
                case FirmwareCarrier.Summon: return row.ReadSummonKey;
                case FirmwareCarrier.Aura: return row.ReadAuraKey;
                case FirmwareCarrier.Field: return row.ReadFieldKey;
                default: return null;
            }
        }

        /// <summary>FGR-FW-011：读法说明（当前语言）。不是固件时 null。</summary>
        public static string Reading(string contentId, FirmwareCarrier carrier)
        {
            string key = ReadingKey(contentId, carrier);
            return key == null ? null : GameText.Get(key);
        }

        /// <summary>FGR-FW-020 / 021：机身形变状态键（none / limiter / fluid / em，表 morph 列；check_luban R24 保证与类别对应）。
        /// 类别被测试注入时跟着注入走（形变自检靠它验证叠加规则）。不是固件时 none。</summary>
        public static string MorphOf(string contentId)
        {
            if (!IsFirmware(contentId))
            {
                return "none";
            }
            if (_categoryOverride != null && _categoryOverride.ContainsKey(contentId))
            {
                switch (CategoryOf(contentId))
                {
                    case FirmwareCategory.Limiter: return "limiter";
                    case FirmwareCategory.Fluid: return "fluid";
                    case FirmwareCategory.Electromagnetic: return "em";
                    default: return "none";
                }
            }
            return TryGetRow(contentId, out GameConfig.fg.FirmwareKind row) && !string.IsNullOrEmpty(row.Morph) ? row.Morph : "none";
        }

        /// <summary>出厂即已破解（表 cracked 列：己方 / 中立 = true；敌方加密 = false）。战役里的破解状态见 <see cref="IsRaw(CampaignState, string)"/>。</summary>
        public static bool IsCrackedByDefault(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) && row.Cracked;

        public static bool IsFirmware(string contentId) =>
            !string.IsNullOrEmpty(contentId) && FirmwareCatalog.TryGet(contentId, out _);

        public static FirmwareKind KindOf(string contentId)
        {
            if (!IsFirmware(contentId))
            {
                return FirmwareKind.NotFirmware;
            }
            if (_override != null)
            {
                return _override.TryGetValue(contentId, out FirmwareKind forced) ? forced : FirmwareKind.Regular;
            }
            EnsureLoaded();
            if (_table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null)
            {
                return row.Kind == "core" ? FirmwareKind.Core : FirmwareKind.Regular;
            }
            if (WarnedMissing.Add(contentId))
            {
                Log.Error($"[FirmwareKinds] 固件 {contentId} 在 fg.TbFirmwareKind 里没有种类（改 tools/cell_tables/fgdata_signal.py 后重新生成），暂按常规处理。");
            }
            return FirmwareKind.Regular;
        }

        public static bool IsCore(string contentId) => KindOf(contentId) == FirmwareKind.Core;

        // ── FG1-VFX-01：类别（机身形变）────────────────────────────────────────────

        private static Dictionary<string, FirmwareCategory> _categoryOverride;
        private static readonly HashSet<string> WarnedCategory = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>固件的类别（fg.TbFirmwareKind category：fuse / limiter / fluid / em）。不是固件时 <see cref="FirmwareCategory.Unknown"/>；
        /// 表里查不到或拼错时记一次 Error 并返回 Unknown（不产生形变，不让玩家卡住；check_luban R22 保证正式表不会出现）。O(1)。</summary>
        public static FirmwareCategory CategoryOf(string contentId)
        {
            if (!IsFirmware(contentId))
            {
                return FirmwareCategory.Unknown;
            }
            if (_categoryOverride != null && _categoryOverride.TryGetValue(contentId, out FirmwareCategory forced))
            {
                return forced;
            }
            EnsureLoaded();
            if (_table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null)
            {
                switch (row.Category)
                {
                    case "fuse": return FirmwareCategory.Fuse;
                    case "limiter": return FirmwareCategory.Limiter;
                    case "fluid": return FirmwareCategory.Fluid;
                    case "em": return FirmwareCategory.Electromagnetic;
                }
            }
            if (WarnedCategory.Add(contentId))
            {
                Log.Error($"[FirmwareKinds] 固件 {contentId} 在 fg.TbFirmwareKind 里没有合法类别（改 tools/cell_tables/fgdata_signal.py 后重新生成），不产生机身形变。");
            }
            return FirmwareCategory.Unknown;
        }

        /// <summary>测试注入：把列出的固件改成指定类别（其余仍读表）。正式内容里还没有电磁类固件（FG2-FW-01 迁入），
        /// 线圈态的完整链路靠它验证。用完 <see cref="ResetForTests"/>。</summary>
        public static void OverrideCategoryForTests(IReadOnlyDictionary<string, FirmwareCategory> categories)
        {
            _categoryOverride = categories == null ? null : new Dictionary<string, FirmwareCategory>(categories, StringComparer.Ordinal);
            Revision++;
        }

        // ── FG1-SIG-06：协议、来源阵营、未破解（裸跑）──────────────────────────────────

        /// <summary>来源阵营键（fg.TbFirmwareKind faction：reclaim / silent / foundry）。不是固件或表里没有时为 reclaim。</summary>
        public const string FactionReclaim = "reclaim";

        /// <summary>FGR-FW-001“协议：敌方加密”——解析台破解前只能由信号裸跑（FGR-SIG-060）。不是固件时 false。</summary>
        public static bool IsEnemyProtocol(string contentId)
        {
            if (!IsFirmware(contentId))
            {
                return false;
            }
            if (_protocolOverride != null)
            {
                return _protocolOverride.Contains(contentId);
            }
            EnsureLoaded();
            return _table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null && row.Protocol == "enemy";
        }

        /// <summary>来源阵营键（暴露面板“各阵营贡献”、异派技术判定）。</summary>
        public static string FactionOf(string contentId)
        {
            EnsureLoaded();
            return _table != null && !string.IsNullOrEmpty(contentId) && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row)
                   && row != null && !string.IsNullOrEmpty(row.Faction)
                ? row.Faction
                : FactionReclaim;
        }

        /// <summary>
        /// FGR-SIG-060：这枚固件在 <paramref name="state"/> 这个战役里是不是“未破解”——敌方加密协议、且解析台还没破解（内容没解锁）。
        /// 破解是按内容记的（<see cref="CampaignState.UnlockedContentIds"/>，解析台完成时写入）：同一种固件的所有实例一起去掉标记，
        /// 已经装在信号核里的那件自动更新（FGR-SIG-062），冷却不受影响（冷却按固件种类记在信号上）。
        /// </summary>
        public static bool IsRaw(CampaignState state, string contentId) =>
            IsEnemyProtocol(contentId) && !MechanicalContentUnlock.IsUnlocked(state, contentId);

        /// <summary>按当前战役（<see cref="CampaignSession.Current"/>）判定未破解——编译器等不带战役参数的纯计算用它（与接入结算同一个战役）。</summary>
        public static bool IsRaw(string contentId) => IsRaw(CampaignSession.Current, contentId);

        /// <summary>从 <paramref name="firmwareIds"/> 里挑出未破解的（顺序不变）。</summary>
        public static string[] RawOf(CampaignState state, IEnumerable<string> firmwareIds)
        {
            if (firmwareIds == null)
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(1);
            foreach (string id in firmwareIds)
            {
                if (!string.IsNullOrEmpty(id) && IsRaw(state, id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// FGR-SIG-012 / 060、FGT-SIG-003：某枚固件能不能装进 <paramref name="host"/>。唯一判定——信号核、机器电路、炮塔的入口都问这里。
        /// 信号核：任何固件都行（核心、常规、未破解）；机器电路：只有已破解、且有机器电路可编译实现的常规固件；炮塔：同机器电路（核心固件只属于信号，未破解的只能裸跑）。
        /// 失败时 <paramref name="reasonKey"/> 是文本键（带一个参数：固件名）。
        /// </summary>
        public static bool CanInstall(CampaignState state, string contentId, FirmwareHost host, out string reasonKey)
        {
            reasonKey = null;
            if (!IsFirmware(contentId))
            {
                reasonKey = "signal.reason.not_firmware_host";
                return false;
            }
            if (host == FirmwareHost.SignalCore)
            {
                return true;
            }
            if (IsCore(contentId))
            {
                reasonKey = host == FirmwareHost.Turret ? "signal.reason.core_turret" : "signal.reason.core_signal_only";
                return false;
            }
            if (IsRaw(state, contentId))
            {
                reasonKey = host == FirmwareHost.Turret ? "signal.reason.raw_turret" : "signal.reason.raw_signal_only";
                return false;
            }
            // FG1-SIG-06 修复轮：机器电路 / 炮塔要能把固件编译成真实效果；没有可编译实现的（装甲击穿，DEBT-FG1SIG06-07 → FG2-FW-01）
            // 破解后也只能放进信号核——判定与 BlueprintCircuitBoard.TrySetFirmware 一致，不给“能装”的假承诺。
            if (!HasMachineImplementation(contentId))
            {
                reasonKey = "signal.reason.no_machine_impl";
                return false;
            }
            return true;
        }

        /// <summary>这枚固件有没有机器电路可结算的实现：带旧基因等价实现（LegacyFacadeId），或者有读法字段（FG2-FW-02：装甲击穿的原生读法“破甲”，
        /// DEBT-FG1SIG06-07 关闭）。核心固件、不是固件时 false。</summary>
        public static bool HasMachineImplementation(string contentId) =>
            !IsCore(contentId) && FirmwareCatalog.TryGet(contentId, out MechanicalContentDef def)
            && (!string.IsNullOrEmpty(def.LegacyFacadeId) || CarrierReadings.HasReading(contentId));

        /// <summary>稳定原因码（<c>CircuitOpResult.Code</c>）：由 <see cref="CanInstall"/> 的文本键映射，调用方按码分支时不会把“不是固件”误判成“未破解”。</summary>
        public static string InstallFailureCode(string reasonKey)
        {
            switch (reasonKey)
            {
                case "signal.reason.not_firmware_host": return "firmware_unknown";
                case "signal.reason.raw_signal_only":
                case "signal.reason.raw_turret": return "raw_signal_only";
                case "signal.reason.core_signal_only":
                case "signal.reason.core_turret": return "core_signal_only";
                case "signal.reason.no_machine_impl": return "firmware_no_machine_impl";
                default: return "firmware_rejected";
            }
        }

        /// <summary>破解后这枚固件能装到哪里（文本键，按种类区分，FGR-SIG-012 / 062）：核心 → 仍只属于信号；没有机器实现 → 仍只能进信号核；否则可刻印、可装机器。</summary>
        public static string AfterCrackKey(string contentId) =>
            IsCore(contentId) ? "signal.raw.after_crack.core"
            : HasMachineImplementation(contentId) ? "signal.raw.after_crack.machine"
            : "signal.raw.after_crack.signal_only";

        /// <summary>FGR-SIG-060：列表 / 下拉项里跟在名字后面的“ ▲未破解”标记（形状 + 文字）；已破解、己方固件、不是固件时为空串。</summary>
        public static string RawTagSuffix(CampaignState state, string contentId) =>
            IsRaw(state, contentId) ? " " + GameText.Get("signal.core.raw_tag") : string.Empty;

        /// <summary>破解状态变了（解析台完成一件敌方固件）：界面按 <see cref="Revision"/> 刷新“未破解”标记。</summary>
        public static void NotifyCrackStateChanged() => Revision++;

        /// <summary>测试注入：只把列出的固件当“敌方加密协议”（其余己方）。用完 <see cref="ResetForTests"/>。</summary>
        public static void OverrideProtocolForTests(IEnumerable<string> enemyProtocolIds)
        {
            _protocolOverride = enemyProtocolIds == null ? null : new HashSet<string>(enemyProtocolIds, StringComparer.Ordinal);
            Revision++;
        }

        private static HashSet<string> _protocolOverride;

        /// <summary>
        /// FG1-SIG-05（FGR-SIG-090）：AI 永远不使用核心固件。机器电路自己的固件里，只有这里返回 true 的才参与 AI 驾驶时的编译
        /// （反应、热量、伤害）；核心固件只能经信号带进接入口。空串 / null 返回 false。
        /// </summary>
        public static bool IsAiUsable(string contentId) => !string.IsNullOrEmpty(contentId) && !IsCore(contentId);

        /// <summary>FG1-SIG-05：从机器电路的固件里剔掉核心固件（顺序不变，空位丢弃）——AI 驾驶时真正生效的那几枚。</summary>
        public static string[] AiUsable(IEnumerable<string> firmwareIds)
        {
            if (firmwareIds == null)
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(2);
            foreach (string id in firmwareIds)
            {
                if (IsAiUsable(id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        /// <summary>FG1-SIG-05：机器电路里残留的核心固件（旧草稿 / 旧档）——AI 不用它们，界面据此说明原因。</summary>
        public static string[] InertCore(IEnumerable<string> firmwareIds)
        {
            if (firmwareIds == null)
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(1);
            foreach (string id in firmwareIds)
            {
                if (!string.IsNullOrEmpty(id) && IsCore(id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        /// <summary>FG1-SIG-05（FGR-FW-003）：表里种类为核心的全部固件 ID（含固件本体尚未进目录、由 FG2-FW-01 补上的 4 条）。
        /// 表不可用时为空。check_luban R22 保证它恰好是 6 条核心固件名单。</summary>
        public static IReadOnlyList<string> CoreRosterIds
        {
            get
            {
                EnsureLoaded();
                var ids = new List<string>(6);
                if (_table != null)
                {
                    foreach (GameConfig.fg.FirmwareKind row in _table.DataList)
                    {
                        if (row != null && row.Kind == "core" && !IsMixedRow(row)) // FG5-RND-04：名表的 6 条（含核心父固件的混合固件也是核心，但不在名单里）
                        {
                            ids.Add(row.Id);
                        }
                    }
                }
                return ids;
            }
        }

        /// <summary>FG1-SIG-03（FGR-SIG-033）：核心固件发动后的冷却秒数（表 cooldown 列，按游戏时间）。不是核心固件、表里没有这一行时为 0
        /// （常规固件没有“发动”，也就没有冷却）。测试注入种类时冷却仍读正式表，改表不需要改代码。</summary>
        public static float CoreCooldownSeconds(string contentId)
        {
            if (!IsCore(contentId))
            {
                return 0f;
            }
            EnsureLoaded();
            return _table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null
                ? Math.Max(0f, row.Cooldown)
                : 0f;
        }

        /// <summary>玩家可见的固件名（文本键）；不是固件时返回 null。</summary>
        public static string DisplayName(string contentId)
        {
            if (!FirmwareCatalog.TryGet(contentId, out MechanicalContentDef def))
            {
                return null;
            }
            EnsureLoaded();
            if (_table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null
                && !string.IsNullOrEmpty(row.NameKey))
            {
                return GameText.Get(row.NameKey);
            }
            return def.DisplayName;
        }

        /// <summary>“◆ 核心 / ● 常规 / ■ 基元芯片”——形状 + 文字，不只靠颜色（FG00 B15）。</summary>
        public static string KindLabel(FirmwareKind kind) => GameText.Get(kind switch
        {
            FirmwareKind.Core => "signal.core.kind.core",
            FirmwareKind.Regular => "signal.core.kind.regular",
            _ => "signal.core.kind.chip",
        });

        public static string KindTip(FirmwareKind kind) => GameText.Get(kind switch
        {
            FirmwareKind.Core => "signal.core.kind.core_tip",
            FirmwareKind.Regular => "signal.core.kind.regular_tip",
            _ => "signal.core.kind.chip_tip",
        });

        // ── FG5-RND-04（FGR-FW-050）：混合固件（熔合产物）────────────────────────────────

        /// <summary>设计案 5.4 名表的 44 条正式固件（表里去掉混合固件的行，按表顺序）。只在目录 / 自检 / 面板刷新时用，不按帧。</summary>
        public static List<GameConfig.fg.FirmwareKind> BaseRows
        {
            get
            {
                var list = new List<GameConfig.fg.FirmwareKind>(48);
                foreach (GameConfig.fg.FirmwareKind row in Rows)
                {
                    if (row != null && !IsMixedRow(row))
                    {
                        list.Add(row);
                    }
                }
                return list;
            }
        }

        /// <summary>这条固件是不是混合固件（表 mixA 列不为 none）。不是固件时 false。O(1)。</summary>
        public static bool IsMixed(string contentId) =>
            TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) && IsMixedRow(row);

        /// <summary>表里的一行是不是混合固件（目录 / 图鉴构建用，不经过目录查找）。</summary>
        public static bool IsMixedRow(GameConfig.fg.FirmwareKind row) =>
            row != null && !string.IsNullOrEmpty(row.MixA) && row.MixA != "none";

        /// <summary>混合固件的两条父固件（表 mixA / mixB）。不是混合固件时 false。</summary>
        public static bool TryGetParents(string contentId, out string parentA, out string parentB)
        {
            parentA = null;
            parentB = null;
            if (!TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) || !IsMixedRow(row))
            {
                return false;
            }
            parentA = row.MixA;
            parentB = row.MixB;
            return true;
        }

        /// <summary>混合固件的第二个类别（“类别显示两个图标”）。不是混合固件时 <see cref="FirmwareCategory.Unknown"/>。</summary>
        public static FirmwareCategory Category2Of(string contentId)
        {
            if (!TryGetFirmwareRow(contentId, out GameConfig.fg.FirmwareKind row) || !IsMixedRow(row))
            {
                return FirmwareCategory.Unknown;
            }
            switch (row.Category2)
            {
                case "fuse": return FirmwareCategory.Fuse;
                case "limiter": return FirmwareCategory.Limiter;
                case "fluid": return FirmwareCategory.Fluid;
                case "em": return FirmwareCategory.Electromagnetic;
                default: return FirmwareCategory.Unknown;
            }
        }

        /// <summary>
        /// 编译 / 反应判定用的“等价固件列表”：混合固件展开成两条父固件（同时具有两者的读法——旧引擎模块、装配反应识别都按父固件算），
        /// 其余原样。读法字段（<see cref="Content.CarrierReadings.FieldsOf"/>）不走这里：混合固件的读法字段已按 fg.TbFusionMerge 的上限合并在表里。
        /// 只在装配结算时调用（不按帧）。
        /// </summary>
        public static List<string> ExpandMixed(IEnumerable<string> firmwareIds)
        {
            var list = new List<string>();
            if (firmwareIds == null)
            {
                return list;
            }
            foreach (string id in firmwareIds)
            {
                if (TryGetParents(id, out string a, out string b))
                {
                    list.Add(a);
                    list.Add(b);
                }
                else
                {
                    list.Add(id);
                }
            }
            return list;
        }

        public static void Reload()
        {
            _loaded = false;
            _table = null;
            _loadError = null;
            WarnedMissing.Clear();
            WarnedCategory.Clear();
            Revision++;
            _catalogRevision++;
            EnsureLoaded();
        }

        /// <summary>FG2-FW-01 测试注入：用构造出来的表替换真实固件表（证明固件目录、名称、数值都经表取得：改表 → 行为跟着变）。
        /// <paramref name="table"/> 为 null 表示模拟“表没加载上”。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideTableForTests(TbFirmwareKind table, string loadError = null)
        {
            _tableOverride = table;
            _tableOverrideError = loadError ?? (table == null ? "测试注入：固件表为空" : null);
            Reload();
        }

        /// <summary>测试注入：用给定的“固件 ID → 种类”替换表（没列出的固件按常规）。用完必须 <see cref="ResetForTests"/>。
        /// FG1-SIG-05 起正式表里过载、标记跳转已是核心；注入只用于“种类改变时规则跟着变”的对照与内核机制回归。</summary>
        public static void OverrideForTests(IReadOnlyDictionary<string, FirmwareKind> kinds)
        {
            _override = kinds == null ? null : new Dictionary<string, FirmwareKind>(kinds, StringComparer.Ordinal);
            Revision++;
            _catalogRevision++; // 目录的 AI 许可跟着种类走
        }

        public static void ResetForTests()
        {
            _override = null;
            _protocolOverride = null;
            _categoryOverride = null;
            _tableOverride = null;
            _tableOverrideError = null;
            Reload();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            if (_tableOverride != null || _tableOverrideError != null)
            {
                _table = _tableOverride;
                _loadError = _tableOverrideError;
                if (_loadError != null)
                {
                    Log.Error($"[FirmwareKinds] {_loadError}");
                }
                return;
            }
            try
            {
                _table = ConfigSystem.Instance.Tables?.TbFirmwareKind;
                if (_table == null)
                {
                    _loadError = "配置表 fg.TbFirmwareKind 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[FirmwareKinds] {_loadError}");
            }
        }
    }
}
