using System;
using System.Collections.Generic;
using System.Linq;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Graph;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>单条编译路径的通用预览摘要，供 UI 逐路径展示。</summary>
    public sealed class BlueprintCircuitPathPreview
    {
        public int[] SlotPath;
        public float Damage;
        public HashSet<string> Tags = new HashSet<string>();

        /// <summary>FG1-SIG-02：这条路径经过接入口，且接入口里插了固件（只有“你接入时”的编译会是 true）。</summary>
        public bool ThroughUplink;
    }

    /// <summary>PRIMITIVE-FULL-DEMO-SPEC.md §3.6/§3.7：电路板的通用预览结果。即使当前组合不触发任何
    /// 具名反应（<see cref="ReactionHint"/> 仍会给出中性说明文案），预览本身永远存在——"不存在具名反应的
    /// 合法组合必须有通用预览，不提示缺反应"（STORY-EXECUTION-CARDS.md ER4-PRIM-02 第2条）。</summary>
    public sealed class BlueprintCircuitPreview
    {
        public int PathCount;

        /// <summary>是否存在可攻击的合法出口（8 号汇槽有内容）。false 是合法状态（如维修/搬运类底盘
        /// 没有主武器），不是错误——UI 应显示中性说明而非报错。</summary>
        public bool HasCombatOutput;

        /// <summary>§3.6 第6条："总基础伤害按 1/路径数分摊"——跨全部路径的事件伤害之和，再除以路径数。</summary>
        public float TotalNormalizedDamage;

        public readonly List<BlueprintCircuitPathPreview> Paths = new List<BlueprintCircuitPathPreview>();

        /// <summary>命中的具名反应展示名，或找不到时的中性说明（永不为 null/空，永不显示"缺反应"错误）。</summary>
        public string ReactionHint = "无具名反应（通用预览已生成）。";

        /// <summary>ER6-REACT-01：<see cref="BlueprintCircuitCompiler.DetectReactionId"/> 的原始结果
        /// （<see cref="Content.MechanicalReactionCatalog.ReactionMarkJumpId"/> 等），供战斗结算代码
        /// （<see cref="Regions.FracturedCityRegion.TryAttackEnemy"/>）直接判定用，不必重新解析
        /// <see cref="ReactionHint"/> 展示文本或重新拿 <see cref="BlueprintCircuitBoard"/> 现算一次——
        /// "AI 与直控读同一 reactionId"（验收卡原文）字面上就是读这个字段。null＝无具名反应。</summary>
        public string ReactionId;

        /// <summary>ER6-REACT-01：功能槽是否装了静默标记器（<see cref="Content.ComponentCatalog.FuncMarkerId"/>）——
        /// 内容目录原文"标记主目标及附近至多两个敌人"：只要装了标记器，命中就会打标记，即使没有配齐
        /// 标记跳转固件+兼容主组件（那样只是标记本身不产生跳转，"无标记跳转固件仍可以标记"是内容
        /// 目录独立于反应组合的单独行为，不是本 Story 臆造）。</summary>
        public bool HasMarkerFunction;

        /// <summary>ER6-REACT-02：主组件是否为铸造重炮（<see cref="Content.ComponentCatalog.CompCannonId"/>）——
        /// 战斗结算据此判断是否走 <see cref="Regions.CannonCombat"/> 的瞄准线/冷却/热量状态机，而不是
        /// 普通连射器/切割束那种即时命中路径。</summary>
        public bool HasCannonPrimary;

        /// <summary>ER8-CONTENT-01：主组件内容 ID（comp_gun 等），与 <see cref="HasCannonPrimary"/> 同一时刻
        /// 从电路板外层槽读出。战斗反馈据此播放该武器在 ComponentCatalog 里登记的专属开火音。</summary>
        public string PrimaryId;

        /// <summary>FG1-VFX-01：功能组件内容 ID（func_dash / func_marker，没有为 null）。机身形变按“装了哪些作战组件”挑形变部件（FGR-FW-020：每个作战组件三套状态）。</summary>
        public string UtilityId;

        /// <summary>ER6-REACT-02：结构槽是否装了散热鳍（<see cref="Content.ComponentCatalog.StructFinId"/>）——
        /// 额外 +5/秒散热（DEMO-CONTENT-LOCK.md §2.4）。</summary>
        public bool HasHeatSinkStructure;

        public string NoteText;

        /// <summary>FG1-SIG-02：热量预算（与 <see cref="BlueprintCircuitBoard.ComputeHeatBudget(string, IEnumerable{string})"/> 同一规则），
        /// 按本次编译实际生效的固件算——双态预览对比“热量的变化”读这里。</summary>
        public float HeatBudget;

        /// <summary>FG1-SIG-02：本次编译实际生效的固件（机器电路自己的固件在前，接入口插入的在后）。</summary>
        public string[] FirmwareIds = Array.Empty<string>();

        /// <summary>FG1-SIG-02：接入口所在格（没有接入口为 0）。AI 驾驶与你接入时相同。</summary>
        public int UplinkSlot;

        /// <summary>FG1-SIG-02：接入口在不在任何一条源→汇路径上（不在时插进去的固件不生效）。</summary>
        public bool UplinkOnPath;

        /// <summary>FG1-SIG-02：插进接入口并**生效**的固件（AI 驾驶时永远为空，FGR-SIG-021、FGR-SIG-090）。</summary>
        public string[] UplinkFirmwareIds = Array.Empty<string>();

        /// <summary>FG1-SIG-05（FGR-SIG-090）：机器电路自己的固件槽里残留的核心固件（旧草稿 / 旧档）。AI 永远不用核心固件，
        /// 所以它们不进 <see cref="FirmwareIds"/>、不产生反应与热量；界面据此说明（保存校验也会拒绝）。</summary>
        public string[] InertCoreFirmwareIds = Array.Empty<string>();

        /// <summary>FG1-SIG-06（FGR-SIG-060、061）：插进接入口并生效的固件里“未破解”的（敌方加密、解析台还没破解）——信号裸跑。
        /// AI 驾驶时永远为空（接入口是空槽；机器电路装不进未破解固件）。</summary>
        public string[] RawFirmwareIds = Array.Empty<string>();

        /// <summary>FG1-SIG-06（FGR-SIG-061）：裸跑时这台机器武器的积热倍率（同类已破解固件的 1.5 倍，fg.TbHomeTuning signal.raw.heat_multiplier）；
        /// 没有裸跑为 1。<see cref="HeatBudget"/> 已乘上；战斗内核的每发积热（CombatSite.MachineWeaponFrom）读这里，预览与实战同一个数。</summary>
        public float RawHeatMultiplier = 1f;

        /// <summary>FG2-FW-01（FGR-FW-001“热量”）：生效固件自己带来的每发积热（表 heat 列之和，未乘裸跑倍率；重炮 + 过载时过载一项由熔穿过载取代）。
        /// 重炮的内核每发积热 = (基础 40 + 本值) × <see cref="RawHeatMultiplier"/>（CombatSite.MachineWeaponFrom），与 <see cref="HeatBudget"/> 同一规则。</summary>
        public float FirmwareHeatPerShot;

        /// <summary>FG2-FW-01（FGR-FW-001“能耗”、FG-GAP-028）：生效固件每发耗电之和（表 power 列）。双态预览两栏与差异各有一行。</summary>
        public int PowerCost;

        /// <summary>FG2-FW-02（FGR-FW-012）：底盘 ID——固定底盘（炮塔）上“兼容但读法调整”的组件（推铲 → 击退铲）按它换读法。</summary>
        public string ChassisId;
    }

    /// <summary>ER4-PRIM-02 STORY-EXECUTION-CARDS.md 第2条正式电路板"通用预览"的编译入口——
    /// PRIMITIVE-FULL-DEMO-SPEC.md §2 原文"最初有疑义的跨数据结构适配以既有 CarrierCompiler、PathCompiler
    /// 和 ComposeEngine 的真实调用链为准"：本类复用 <see cref="PathCompiler"/> 做路径枚举（§9 既有实现，
    /// 见 <see cref="BlueprintCircuitBoard.ToSlotGrid"/>），链头/链尾模块由 0/8 号槽内容的
    /// <c>CardCatalog.CreateModule</c> 自然产生（<b>DEBT-ER4PRIM01-01 的裁决落点</b>——0 号槽选了哪个
    /// Source 内容，这里就真的实例化哪个模块，不像旧 <c>CarrierCompiler.BuildRecipe</c> 硬编码
    /// <c>new EnergyCore(10f)</c>），固件解析规则镜像 <see cref="GameLogic.MetabolicSlice.Carrier.CarrierCompiler.BuildRecipe"/>
    /// （contract 基因走 <see cref="GeneCatalog.Get"/>，module 基因走 <see cref="GeneCatalog.GetModule"/>，
    /// 插入链尾之前）。
    ///
    /// 本类只产出**预览数据**，不驱动真实战斗（不修改任何机器 HP/弹药/世界状态）——真正把这套编译结果接进
    /// 正式 Sim/战斗桥属于 ER4-BLP-02（SYSTEMS-SPEC.md 复用边界；REQUIREMENT-TO-PLAYABLE-TRACE.md
    /// "AC-PRM-002～004/014；正式战斗接线由 ER4-BLP-02 续接"），本 Story 只需保证编译出的结构自洽、可测。</summary>
    public static class BlueprintCircuitCompiler
    {
        /// <summary>机器电路自己的编译结果 = AI 驾驶时（接入口按空槽处理：照样传导、不产生芯片效果，FGR-SIG-021）。</summary>
        public static BlueprintCircuitPreview CompilePreview(BlueprintCircuitBoard board, int seed = 1) =>
            Compile(board, seed, null);

        /// <summary>唯一的编译实现。<paramref name="uplinkFirmwareIds"/> 为 null 或空 = 接入口是空槽（AI 驾驶时）；
        /// 非空 = 你接入时插进接入口的固件（已按配额与路径上限截断，见 <see cref="UplinkCompiler.Plan"/>）。
        /// 插入的固件只作用于**经过接入口的路径**：模块插在接入口那一格的位置，契约并进这些路径的规则；
        /// 不经过接入口的路径与 AI 驾驶时逐字相同。双态预览与正式结算（<see cref="MachineLoadoutRegistry"/>）都只调这里（IC-REQ-010）。</summary>
        internal static BlueprintCircuitPreview Compile(BlueprintCircuitBoard board, int seed, IReadOnlyList<string> uplinkFirmwareIds)
        {
            board.SyncFixedSlots();
            var preview = new BlueprintCircuitPreview();
            preview.UplinkSlot = board.HasUplink ? board.UplinkSlot : BlueprintCircuitLayout.NoUplink;
            bool hasInsertion = board.HasUplink && uplinkFirmwareIds != null && uplinkFirmwareIds.Count > 0;
            List<PathCompiler.CompiledPath> compiled = null;
            if (!string.IsNullOrEmpty(board.SlotContentIds[BlueprintCircuitLayout.SinkSlot]))
            {
                compiled = PathCompiler.Compile(board.ToSlotGrid());
            }
            // “接通” = 有一条源→汇简单路径经过接入口（与下面 ThroughUplink 同一口径）。编得出路径时直接看编出的路径；
            // 编不出时（铸造重炮这类主组件没有接入组合装配链）按同一套简单路径 DFS 只看导线——接入口插进去的固件
            // 照样要参与反应与热量（熔穿过载 = 重炮 + 过载）。
            preview.UplinkOnPath = board.HasUplink && (compiled != null && compiled.Count > 0
                ? compiled.Any(p => IndexOf(p.SlotPath, board.UplinkSlot) >= 0)
                : board.IsOnSourceSinkPath(board.UplinkSlot));
            // 接入口不在任何一条源→汇路径上：插进去的固件不接通，不生效（面板会说明）。
            string[] uplinkEffective = hasInsertion && preview.UplinkOnPath
                ? uplinkFirmwareIds.Where(id => !string.IsNullOrEmpty(id)).ToArray()
                : Array.Empty<string>();
            preview.UplinkFirmwareIds = uplinkEffective;
            // FG1-SIG-05（FGR-SIG-090）：机器电路自己的固件里只有常规固件生效——核心固件（旧草稿 / 旧档残留）AI 永远不用；
            // 核心固件只能经信号插进接入口（uplinkEffective）。
            string[] localEffective = FirmwareKinds.AiUsable(board.FirmwareSlots);
            preview.InertCoreFirmwareIds = FirmwareKinds.InertCore(board.FirmwareSlots);
            preview.FirmwareIds = localEffective.Concat(uplinkEffective).ToArray();
            // FG1-SIG-06（FGR-SIG-061）：信号裸跑未破解的敌方固件——热量增长是同类已破解固件的 1.5 倍（确定性，不靠随机）。
            // 破解状态按当前战役判定（与接入结算同一个战役）；破解后下一次编译自动回到 1 倍。
            preview.RawFirmwareIds = FirmwareKinds.RawOf(CampaignSession.Current, uplinkEffective);
            preview.RawHeatMultiplier = preview.RawFirmwareIds.Length > 0 ? RawFirmwareService.HeatMultiplier : 1f;
            preview.HeatBudget = BlueprintCircuitBoard.ComputeHeatBudget(board.PrimaryId, preview.FirmwareIds) * preview.RawHeatMultiplier;
            preview.FirmwareHeatPerShot = BlueprintCircuitBoard.ComputeFirmwareHeat(board.PrimaryId, preview.FirmwareIds);
            preview.PowerCost = BlueprintCircuitBoard.ComputePowerCost(preview.FirmwareIds);

            // ER6-REACT-01/02：反应/标记/重炮/散热鳍这几个标志只是"电路板外层槽装了什么"的直接读取，
            // 与下面"ComposeEngine 能不能真的编出一条 source→sink 路径"完全无关，必须放在任何早退
            // return 之前——真实踩过的坑：comp_cannon 在 MechanicalContentFacade 里没有 LegacyFacadeId
            // （DEBT-ER4CONTENT01-05，铸造重炮尚未接入共享 OrganelleCatalog/CarrierCompiler 装配链，
            // 见 MechanicalContentCombatFactory 类注释），导致 SinkSlot 内容恒为空、下面的早退分支必然
            // 命中——如果这几个字段放在早退之后才算，铸造重炮永远读不到 HasCannonPrimary=true，
            // ER6-REACT-02 的整条战斗链会在第一步就被误判"没有武器"拒绝（execute_code 实测复现过
            // 这个问题）。铸造重炮的伤害本来就是 CannonCombat 里的固定设计常量、不读
            // TotalNormalizedDamage，不依赖这里的 ComposeEngine 编译结果，所以提前计算完全安全。
            preview.ReactionId = DetectReactionId(board.PrimaryId, board.UtilityId, preview.FirmwareIds);
            preview.HasMarkerFunction = board.UtilityId == ComponentCatalog.FuncMarkerId;
            preview.HasCannonPrimary = board.PrimaryId == ComponentCatalog.CompCannonId;
            preview.PrimaryId = board.PrimaryId;
            preview.ChassisId = board.ChassisId;
            preview.UtilityId = string.IsNullOrEmpty(board.UtilityId) ? null : board.UtilityId; // 存读档后空槽可能是 ""：统一成 null，编译结果逐字段稳定
            preview.HasHeatSinkStructure = board.StructureId == ComponentCatalog.StructFinId;

            if (string.IsNullOrEmpty(board.SlotContentIds[BlueprintCircuitLayout.SinkSlot]))
            {
                preview.HasCombatOutput = false;
                preview.NoteText = "该底盘当前主组件无可攻击输出（如搬运/维修类底盘），通用预览不适用于战斗数值。";
                return preview;
            }

            preview.HasCombatOutput = true;
            preview.PathCount = compiled.Count;
            if (compiled.Count == 0)
            {
                preview.NoteText = "当前无合法源→汇路径，无法生成预览（保存前必须先修好电路）。";
                return preview;
            }

            (List<IContract> contracts, List<Func<IModule>> moduleGeneFactories) = ResolveFirmware(localEffective);
            (List<IContract> uplinkContracts, List<Func<IModule>> uplinkModuleFactories) = ResolveFirmware(uplinkEffective);

            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var world = new WorldState();
            RuleVector rules = engine.NormalizeContracts(contracts);
            RuleVector uplinkRules = uplinkEffective.Length > 0
                ? engine.NormalizeContracts(contracts.Concat(uplinkContracts).ToList())
                : rules;

            float totalDamage = 0f;
            var aggregatedTags = new HashSet<string>();

            foreach (PathCompiler.CompiledPath path in compiled)
            {
                var chain = new List<IModule>(path.Modules);
                if (chain.Count == 0)
                {
                    continue;
                }
                // 接入口插入：模块放在接入口那一格（空格只有一个槽被动模块）之后，按信号核槽位顺序。
                int uplinkIndex = uplinkEffective.Length > 0 ? IndexOf(path.SlotPath, board.UplinkSlot) : -1;
                bool throughUplink = uplinkIndex >= 0 && path.SlotModuleStart != null;
                if (throughUplink)
                {
                    int insertAt = path.SlotModuleStart[uplinkIndex] + 1;
                    for (int i = 0; i < uplinkModuleFactories.Count; i++)
                    {
                        chain.Insert(insertAt + i, uplinkModuleFactories[i]());
                    }
                }
                IModule tail = chain[chain.Count - 1];
                chain.RemoveAt(chain.Count - 1);
                foreach (Func<IModule> factory in moduleGeneFactories)
                {
                    chain.Add(factory());
                }
                chain.Add(tail);

                IReadOnlyList<HitEvent> raw = engine.RunAssembly(chain, ticks: 1, seed: seed);
                var pathPreview = new BlueprintCircuitPathPreview { SlotPath = path.SlotPath.ToArray(), ThroughUplink = throughUplink };
                RuleVector pathRules = throughUplink ? uplinkRules : rules;
                foreach (HitEvent evt in raw)
                {
                    HitEvent final = engine.ApplyPipeline(evt, pathRules, world);
                    pathPreview.Damage += final.Damage;
                    foreach (string tag in final.Tags)
                    {
                        pathPreview.Tags.Add(tag);
                        aggregatedTags.Add(tag);
                    }
                }
                totalDamage += pathPreview.Damage;
                preview.Paths.Add(pathPreview);
            }

            preview.TotalNormalizedDamage = compiled.Count > 0 ? totalDamage / compiled.Count : 0f;
            preview.ReactionHint = ResolveReactionHint(preview.ReactionId);
            return preview;
        }

        private static int IndexOf(IReadOnlyList<int> path, int slot)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (path[i] == slot)
                {
                    return i;
                }
            }
            return -1;
        }

        private static (List<IContract>, List<Func<IModule>>) ResolveFirmware(IEnumerable<string> firmwareIds)
        {
            var contracts = new List<IContract>();
            var moduleGeneFactories = new List<Func<IModule>>();

            foreach (string firmwareId in firmwareIds)
            {
                if (string.IsNullOrEmpty(firmwareId))
                {
                    continue;
                }
                if (!FirmwareCatalog.TryGet(firmwareId, out MechanicalContentDef def) || def.LegacyFacadeId == null)
                {
                    continue;
                }
                string geneId = def.LegacyFacadeId;

                Func<IContract> createContract = GeneCatalog.Get(geneId);
                if (createContract != null)
                {
                    contracts.Add(createContract());
                    continue;
                }

                Func<IModule> createModule = GeneCatalog.GetModule(geneId);
                if (createModule != null)
                {
                    moduleGeneFactories.Add(createModule);
                }
            }

            return (contracts, moduleGeneFactories);
        }

        /// <summary>DEMO-CONTENT-LOCK.md §3 两条具名反应的纯函数判定（<see cref="MechanicalReactionCatalog"/>，
        /// ER4-CONTENT-01 已实现）——单一判定入口，供预览（<see cref="ResolveReactionHint"/>）与保存期扣费
        /// （<c>BlueprintEditorService.TrySave</c>）共用同一结果（STORY-EXECUTION-CARDS.md ER4-BLP-01
        /// 第2条"预览与实际编译共用同一结果"）。返回 null 表示当前组合未触发任何具名反应（合法状态，不是
        /// 错误——合法组合仍按通用正交组合结算）。
        /// FG1-SIG-05：这是“AI 驾驶时”的反应——机器电路里的核心固件不算（AI 永远不用核心固件，FGR-SIG-090）；
        /// “你接入时能打出”的反应见 <see cref="UplinkReactionReadiness"/>。</summary>
        public static string DetectReactionId(BlueprintCircuitBoard board) =>
            DetectReactionId(board.PrimaryId, board.UtilityId, FirmwareKinds.AiUsable(board.FirmwareSlots));

        /// <summary>FG1-SIG-02：同一判定的纯函数版本——“你接入时”把接入口里生效的固件并进 <paramref name="firmwareIdsIn"/>。</summary>
        public static string DetectReactionId(string primaryId, string utilityId, IEnumerable<string> firmwareIdsIn)
        {
            var mainComponentIds = new[] { primaryId }.Where(id => !string.IsNullOrEmpty(id)).ToList();
            var functionComponentIds = new[] { utilityId }.Where(id => !string.IsNullOrEmpty(id)).ToList();
            var firmwareIds = (firmwareIdsIn ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).ToList();

            if (MechanicalReactionCatalog.DetectMarkJump(mainComponentIds, functionComponentIds, firmwareIds))
            {
                return MechanicalReactionCatalog.ReactionMarkJumpId;
            }
            if (MechanicalReactionCatalog.DetectMeltOverload(mainComponentIds, firmwareIds))
            {
                return MechanicalReactionCatalog.ReactionMeltOverloadId;
            }
            return null;
        }

        /// <summary>不存在具名反应时返回中性说明，不是错误——满足 STORY-EXECUTION-CARDS.md ER4-PRIM-02
        /// 第2条"不存在具名反应的合法组合必须有通用预览，不提示缺反应"。</summary>
        private static string ResolveReactionHint(string reactionId)
        {
            if (reactionId != null && MechanicalReactionCatalog.TryGet(reactionId, out MechanicalContentDef def))
            {
                return $"触发具名反应：{def.DisplayName}。";
            }
            return "无具名反应（通用预览已生成，效果按普通正交组合结算）。";
        }
    }
}
