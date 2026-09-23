using System;
using System.Collections.Generic;
using System.Linq;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Campaign.Content;
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

        public string NoteText;
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
        public static BlueprintCircuitPreview CompilePreview(BlueprintCircuitBoard board, int seed = 1)
        {
            board.SyncFixedSlots();
            var preview = new BlueprintCircuitPreview();

            if (string.IsNullOrEmpty(board.SlotContentIds[BlueprintCircuitLayout.SinkSlot]))
            {
                preview.HasCombatOutput = false;
                preview.NoteText = "该底盘当前主组件无可攻击输出（如搬运/维修类底盘），通用预览不适用于战斗数值。";
                return preview;
            }

            preview.HasCombatOutput = true;
            SlotGrid grid = board.ToSlotGrid();
            List<PathCompiler.CompiledPath> compiled = PathCompiler.Compile(grid);
            preview.PathCount = compiled.Count;
            if (compiled.Count == 0)
            {
                preview.NoteText = "当前无合法源→汇路径，无法生成预览（保存前必须先修好电路）。";
                return preview;
            }

            (List<IContract> contracts, List<Func<IModule>> moduleGeneFactories) = ResolveFirmware(board);

            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var world = new WorldState();
            RuleVector rules = engine.NormalizeContracts(contracts);

            float totalDamage = 0f;
            var aggregatedTags = new HashSet<string>();

            foreach (PathCompiler.CompiledPath path in compiled)
            {
                var chain = new List<IModule>(path.Modules);
                if (chain.Count == 0)
                {
                    continue;
                }
                IModule tail = chain[chain.Count - 1];
                chain.RemoveAt(chain.Count - 1);
                foreach (Func<IModule> factory in moduleGeneFactories)
                {
                    chain.Add(factory());
                }
                chain.Add(tail);

                IReadOnlyList<HitEvent> raw = engine.RunAssembly(chain, ticks: 1, seed: seed);
                var pathPreview = new BlueprintCircuitPathPreview { SlotPath = path.SlotPath.ToArray() };
                foreach (HitEvent evt in raw)
                {
                    HitEvent final = engine.ApplyPipeline(evt, rules, world);
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
            preview.ReactionHint = ResolveReactionHint(board);
            return preview;
        }

        private static (List<IContract>, List<Func<IModule>>) ResolveFirmware(BlueprintCircuitBoard board)
        {
            var contracts = new List<IContract>();
            var moduleGeneFactories = new List<Func<IModule>>();

            foreach (string firmwareId in board.FirmwareSlots)
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
        /// 错误——合法组合仍按通用正交组合结算）。</summary>
        public static string DetectReactionId(BlueprintCircuitBoard board)
        {
            var mainComponentIds = new[] { board.PrimaryId }.Where(id => !string.IsNullOrEmpty(id)).ToList();
            var functionComponentIds = new[] { board.UtilityId }.Where(id => !string.IsNullOrEmpty(id)).ToList();
            var firmwareIds = board.FirmwareSlots.Where(id => !string.IsNullOrEmpty(id)).ToList();

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
        private static string ResolveReactionHint(BlueprintCircuitBoard board)
        {
            string reactionId = DetectReactionId(board);
            if (reactionId != null && MechanicalReactionCatalog.TryGet(reactionId, out MechanicalContentDef def))
            {
                return $"触发具名反应：{def.DisplayName}。";
            }
            return "无具名反应（通用预览已生成，效果按普通正交组合结算）。";
        }
    }
}
