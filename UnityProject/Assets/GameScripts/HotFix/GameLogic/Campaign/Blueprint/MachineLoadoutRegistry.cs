using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>ER4-BLP-02 STORY-EXECUTION-CARDS.md 第1/3条："用最小 Facade 把正式电路图接到唯一战斗桥，
    /// 不复制命中、伤害、目标、角度或冷却算法……区域卸载/重新生成、机器阵亡、工厂出厂、内容版本迁移时
    /// 登记/解绑 UnitLoadoutRegistry 成对，不能让旧机体槽位复用后继承前一台武器"。
    ///
    /// ── 为什么不是字面上的 <c>Control.UnitLoadoutRegistry</c> ──
    /// 卡片原文点名的那个类是旧"细胞阶段"单一玩家本体系统留下的、按 <c>SimEntityId</c> 索引的注册表
    /// （见其类注释：键必须是当前 SimWorld 会话内的实体 id）。归还谷地/战役层的 <see cref="MachineRecord"/>
    /// 至今没有、也不会在本 Story 范围内获得对应的 <c>SimEntityId</c>——ER5/ER6 区域战斗尚未实现
    /// （STORY-BOARD.md 排在本 Story 之后），HomeValley 是纯管理场景，不跑 DOTS/Burst Sim 内核。
    /// SYSTEMS-SPEC.md §"复用边界"允许"若现有数据结构不兼容，只扩最小适配层"——本类正是那层最小适配：
    /// 语义与 <c>UnitLoadoutRegistry</c> 逐条对应（成对登记/解绑、查询永不抛出、失败可见不臆造默认装配），
    /// 只是键从 <c>SimEntityId</c> 换成本战役稳定的 <see cref="MachineRecord.LogicId"/>——待 ER5/ER6 真的
    /// 需要把战役机器投进 Sim 战斗时，两条登记表将并存：本类回答"这台战役机器的蓝图装配是什么"，
    /// <c>UnitLoadoutRegistry</c> 回答"这个战场实体正在用哪份装配"，届时由那个 Story 负责把两者接上。
    ///
    /// ── 不复制算法 ──
    /// 本类只做"哪台机器绑定了哪个 BlueprintVersion"的登记，以及把登记结果解析成
    /// <see cref="BlueprintCircuitPreview"/>（真正编译发生在 <see cref="BlueprintCircuitCompiler.CompilePreview"/>，
    /// ER4-BLP-01 已实现，本类不重新实现一遍）。命中/伤害/目标/角度/冷却的落地算法仍然只存在于
    /// <c>MetabolicSliceBridge.ApplyEvent</c>/<c>ApplyChassisDamage</c> 一处，本类不涉足。
    ///
    /// ── 单一出口 ──
    /// <see cref="ResolveForAi"/>/<see cref="ResolveForDirectControl"/> 是同一份实现
    /// （<see cref="Resolve"/>）的两个语义化别名——AI 行动与玩家直控调同一个方法体，结构上不可能
    /// "各自另算一套数值"，这正是 AC-REA-003/固定种子 AI-玩家一致性要求的落点。</summary>
    public readonly struct MachineCombatResolution
    {
        public readonly bool Success;
        public readonly string FailureReason;
        public readonly string CompileSignature;
        public readonly BlueprintCircuitPreview Preview;

        private MachineCombatResolution(bool success, string failureReason, string compileSignature, BlueprintCircuitPreview preview)
        {
            Success = success;
            FailureReason = failureReason;
            CompileSignature = compileSignature;
            Preview = preview;
        }

        public static MachineCombatResolution Ok(string compileSignature, BlueprintCircuitPreview preview) =>
            new MachineCombatResolution(true, null, compileSignature, preview);

        public static MachineCombatResolution Fail(string reason) =>
            new MachineCombatResolution(false, reason, null, null);
    }

    public static class MachineLoadoutRegistry
    {
        private struct Entry
        {
            public string BlueprintId;
            public int Version;
        }

        private static readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();

        public static int Count => _entries.Count;

        public static bool IsRegistered(int machineLogicId) => _entries.ContainsKey(machineLogicId);

        /// <summary>登记：把 <paramref name="machineLogicId"/> 与它当下应使用的 (blueprintId, version) 绑定。
        /// 校验该版本此刻确实可解析（存在于 <see cref="CampaignState.BlueprintRecords"/>）——绑定失败时
        /// 拒绝登记并返回可见原因，绝不登记一个查不到内容、日后会静默退化成"默认强力替身"的悬空条目
        /// （STORY-EXECUTION-CARDS.md 第2条"任何环节绑定失败进入可见错误并拒绝生成默认强力替身"）。
        /// 同一 <paramref name="machineLogicId"/> 重复调用直接覆盖——"旧机体槽位复用后继承前一台武器"这类
        /// bug 在这里结构上不成立：覆盖写入即时生效，不存在两份并存的装配。</summary>
        public static CircuitOpResult Register(CampaignState state, int machineLogicId, string blueprintId, int version)
        {
            if (machineLogicId <= 0)
            {
                return CircuitOpResult.Fail("invalid_machine_id", $"非法 LogicId {machineLogicId}，拒绝登记。");
            }
            if (string.IsNullOrEmpty(blueprintId))
            {
                return CircuitOpResult.Fail("blueprint_id_required", "BlueprintId 为空，拒绝登记。");
            }

            BlueprintRecord record = BlueprintEditorService.Find(state, blueprintId);
            BlueprintVersionRecord versionRecord = record?.Versions?.FirstOrDefault(v => v.Version == version);
            if (versionRecord == null)
            {
                return CircuitOpResult.Fail("blueprint_version_not_found",
                    $"'{blueprintId}' 第 {version} 版不存在或不可解析，拒绝登记。");
            }

            _entries[machineLogicId] = new Entry { BlueprintId = blueprintId, Version = version };
            return CircuitOpResult.Ok();
        }

        /// <summary>解绑：区域卸载/机器阵亡/装配变更（回厂改造换版本前先解绑旧的，再登记新的）均调用本方法。
        /// 未登记时安全返回 false，不是错误——解绑一个从未登记过的 id 是合法的幂等操作。</summary>
        public static bool Unregister(int machineLogicId) => _entries.Remove(machineLogicId);

        /// <summary>整表清空——区域整体卸载（<c>HomeValleyController.Exit</c>）时调用，语义与
        /// <c>Control.UnitLoadoutRegistry.Unbind</c> 一致："只清映射，不清 CampaignState 里的长期记录"，
        /// 机器数据本身仍在 <see cref="CampaignState.MachineRecords"/>，下次进场由调用方重新登记。</summary>
        public static void Clear() => _entries.Clear();

        /// <summary>唯一真正的解析实现——把登记的 (blueprintId, version) 现场解析成
        /// <see cref="BlueprintCircuitPreview"/>。不读取任何缓存的编译结果：内容版本迁移
        /// （<see cref="Blueprint.BlueprintCircuitDefaults.EnsureCircuitDataSeeded"/>）就地重写
        /// 同一 Version 号的记录字段时，下一次调用本方法会自动读到迁移后的最新内容——不需要额外的
        /// "内容版本迁移时刷新登记表"步骤，结构上不会读到过期数据。</summary>
        public static MachineCombatResolution Resolve(CampaignState state, int machineLogicId, int seed)
        {
            if (!_entries.TryGetValue(machineLogicId, out Entry entry))
            {
                return MachineCombatResolution.Fail($"机器 {machineLogicId} 未在装配登记表中，拒绝生成默认强力替身。");
            }

            BlueprintRecord record = BlueprintEditorService.Find(state, entry.BlueprintId);
            BlueprintVersionRecord versionRecord = record?.Versions?.FirstOrDefault(v => v.Version == entry.Version);
            if (versionRecord == null)
            {
                return MachineCombatResolution.Fail(
                    $"'{entry.BlueprintId}' 第 {entry.Version} 版已不可解析，拒绝生成默认强力替身。");
            }

            BlueprintCircuitBoard board = BlueprintCircuitBoard.FromVersion(versionRecord);
            BlueprintCircuitPreview preview = BlueprintCircuitCompiler.CompilePreview(board, seed);
            return MachineCombatResolution.Ok(versionRecord.CompileSignature, preview);
        }

        /// <summary>AI 行动出口——与 <see cref="ResolveForDirectControl"/> 调用同一个
        /// <see cref="Resolve"/>，结构上保证"AI/玩家使用同装配"。</summary>
        public static MachineCombatResolution ResolveForAi(CampaignState state, int machineLogicId, int seed) =>
            Resolve(state, machineLogicId, seed);

        /// <summary>玩家直控出口——同上。</summary>
        public static MachineCombatResolution ResolveForDirectControl(CampaignState state, int machineLogicId, int seed) =>
            Resolve(state, machineLogicId, seed);
    }
}
