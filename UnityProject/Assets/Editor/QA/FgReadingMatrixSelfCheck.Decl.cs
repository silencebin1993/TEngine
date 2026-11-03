using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FW-01（FGT-EMG-003 stageGate ≤ 1 部分；FGR-EMG-015 规则二、FGR-EMG-019）：C 段扩展到“声明开 / 关”与“程序首位 / 末位”，并做单轨比对。
    /// 起真实系统：真实声明表（emg.stage = 1 覆盖）→ 真实蓝图编译（UplinkCompiler）→ 真实程序编译器 → 真实 CombatSite.ProgramReading → 真实内核编队攻击 4 游戏秒。
    /// </summary>
    public static partial class FgReadingMatrixSelfCheck
    {
        /// <summary>填充指令：效果类、阶段 1 走声明轨（余震 = 动能写端的过渡字段）。放在被测固件前 / 后，比 [被测, 填充] / [填充, 被测] 与只有 [填充] 的行为指纹。</summary>
        private const string DeclFiller = "fw_echo";

        /// <summary>审查修复（第 1 轮）：触发填充——效果固件排在触发器之后（落进子节点）也要有行为（[分裂, 被测] 对 [分裂]）。</summary>
        private const string TriggerFiller = "fw_split";

        private static void CheckDeclMatrix()
        {
            Line("  · C2. EMG1-FW-01（FGT-EMG-003 阶段 1）：stageGate ≤ 1 的固件声明在 5 载体 × 程序首位 / 末位都有行为指纹差异（对照 = 只有填充指令）；单轨比对：" +
                 "程序轨读法 = 独立按声明推出的“只留根节点效果写端过渡字段”读法，新旧两轨同时结算（不跳旧字段）必须被比出来");
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
            try
            {
                EmgCatalog.OverrideForTests(src);
                var gate = src.Decls.GroupBy(d => d.Owner).ToDictionary(g => g.Key, g => g.First().StageGate, StringComparer.Ordinal);
                // EMG1-FW-02：FG19A 第 6 节新固件也进本矩阵——读的事件在这组场景里自然发生的（命中）直接比；读阵亡 / 反弹 / 穿透 / 过热 / 冷却完成的
                // 条件型（FG19A 第 1.8 节）在 FgEmgAddonFirmwareSelfCheck 的条件场景里逐载体 × 首位 / 末位比（先造出条件）
                List<string> stage1 = FirmwareKinds.BaseRows.Select(r => r.Id)
                    .Concat(FirmwareKinds.AddonRows.Select(r => r.Id).Where(id => !IsConditionalAddon(src, id)))
                    .Where(id => gate.TryGetValue(id, out int g) && g <= 1).ToList();
                var dead = new List<string>();
                var dual = new List<string>();
                var landing = new List<string>();
                int injectPairs = 0;
                int combos = 0;
                int trigPairs = 0;
                var sw = Stopwatch.StartNew();
                foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
                {
                    string baseline = DeclDigest(comp, new[] { DeclFiller }, out _, out _);
                    string trigBase = DeclDigest(comp, new[] { TriggerFiller }, out _, out _);
                    foreach (string fw in stage1)
                    {
                        if (fw == DeclFiller)
                        {
                            combos++;
                            if (DeclDigest(comp, new[] { fw }, out _, out _) == DeclDigest(comp, Array.Empty<string>(), out _, out _))
                            {
                                dead.Add($"{carrier}/{fw}（单条）");
                            }
                            continue;
                        }
                        combos += 2;
                        if (DeclDigest(comp, new[] { fw, DeclFiller }, out _, out _) == baseline)
                        {
                            dead.Add($"{carrier}/{fw}（首位）");
                        }
                        if (DeclDigest(comp, new[] { DeclFiller, fw }, out _, out _) == baseline)
                        {
                            dead.Add($"{carrier}/{fw}（末位）");
                        }
                        // 效果固件排在触发器之后（指令落进子节点）：[分裂, 被测] 对 [分裂]。这里是 emg.stage = 1 的回滚对照（过渡口径作用在本机出手）；现行口径（阶段 ≥ 2 落点结算）见下面的 stage2Dead
                        // 同时写弹道修饰的（装甲击穿、撞墙……）在子节点上的修饰行为由 FgEmgGrammarSelfCheck B2 逐格对照，这里只比纯效果固件
                        if (fw != TriggerFiller && HasEffectWrite(fw, carrier) && !HasModWrite(fw, carrier))
                        {
                            combos++;
                            trigPairs++;
                            if (DeclDigest(comp, new[] { TriggerFiller, fw }, out _, out _) == trigBase)
                            {
                                dead.Add($"{carrier}/{fw}（触发器后）");
                            }
                        }
                        // 单轨比对（只做单条程序：独立推导的保留字段 = 该固件声明里写端口的过渡字段；修饰 / 触发 / 只改数字的写端不保留旧字段）
                        string why = SingleTrackProblem(comp, carrier, fw, false);
                        if (why != null)
                        {
                            dual.Add($"{carrier}/{fw}：{why}");
                        }
                    }
                }
                sw.Stop();
                // EMG2-PHS-01（DEBT-EMG1FW01-03 注入类，卡片“C2 触发器后对照改断言落点”）：命中注能在阶段 ≥ 2 才有内核执行者（物质层），本段临时把生效阶段提到 2——
                // 注入写端排在触发器后只在子弹药 / 子区落点结算：有目标累积到注入量，且本机主目标的累积量低于同一条固件放根节点（本机命中注入）时；不再靠本机出手上的过渡字段 / 旧标签“有差异”。
                src.Tuning = id => id == EmgCatalog.StageKey ? (true, 2f) : tuning(id);
                EmgCatalog.OverrideForTests(src);
                var stage2Dead = new List<string>();
                var stage2Fields = new List<string>();
                int stage2Pairs = 0;
                foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
                {
                    // EMG2-REACT-02（DEBT-EMG1FW01-03 余部）：全部效果固件排在触发器后，阶段 2 的现行执行者在落点——行为必须与 [分裂] 有差异（零死对，FGR-EMG-015 规则二），本机出手的读法不留过渡字段
                    DeclDigest(comp, new[] { TriggerFiller }, out _, out CombatReading trigReading);
                    string stage2Base = DeclDigest(comp, new[] { TriggerFiller }, out _, out _);
                    foreach (string fw in stage1)
                    {
                        if (fw == TriggerFiller || fw == DeclFiller || !HasEffectWrite(fw, carrier) || HasModWrite(fw, carrier))
                        {
                            continue;
                        }
                        stage2Pairs++;
                        string d2 = DeclDigest(comp, new[] { TriggerFiller, fw }, out _, out CombatReading r2);
                        // 无人机只打最近目标，预置场景里的残血目标不在子弹药落点附近；处决另造全部残血的场景比
                        bool landed = d2 != stage2Base || (fw == FirmwareCatalog.FwExecuteId && SummonExecuted(comp, new[] { TriggerFiller, fw }) > SummonExecuted(comp, new[] { TriggerFiller }));
                        if (!landed)
                        {
                            stage2Dead.Add($"{carrier}/{fw}");
                        }
                        if (!r2.Equals(trigReading))
                        {
                            stage2Fields.Add($"{carrier}/{fw}");
                        }
                    }
                    foreach (string fw in stage1)
                    {
                        if (fw == TriggerFiller || !HasInjectWrite(fw, carrier))
                        {
                            continue;
                        }
                        injectPairs++;
                        Obs root = DeclObs(comp, new[] { fw }), child = DeclObs(comp, new[] { TriggerFiller, fw });
                        if (!(root.InjectRoot > 0 && child.InjectChild > 0 && child.InjectRoot == 0))
                        {
                            landing.Add($"{carrier}/{fw}（单条：根节点注能 {root.InjectRoot} 次；排在分裂后：根节点 {child.InjectRoot} 次、子载荷落点 {child.InjectChild} 次）");
                        }
                    }
                }
                src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
                EmgCatalog.OverrideForTests(src);
                Expect(stage1.Count >= 30 && dead.Count == 0 && trigPairs >= 40,
                    $"阶段 1 走声明轨的 {stage1.Count} 条固件 × 5 载体 × 首位 / 末位 + 效果固件在触发器后（{trigPairs} 对）共 {combos} 对全部有行为指纹差异，死对 {dead.Count} 对（{sw.ElapsedMilliseconds} 毫秒）{(dead.Count > 0 ? "：" + string.Join("、", dead) : string.Empty)}");
                Expect(stage2Pairs >= 40 && stage2Dead.Count == 0, $"阶段 2 效果固件排在触发器后（{stage2Pairs} 对）在子弹药 / 子区落点都有行为差异，死对 {stage2Dead.Count}：{string.Join("、", stage2Dead)}");
                Expect(stage2Fields.Count == 0, $"阶段 2 效果固件排在触发器后：本机出手的读法与只有触发器时逐字段一致（过渡字段已撤）{Detail(stage2Fields)}");
                Expect(injectPairs >= 5 && landing.Count == 0,
                    $"注入类（en.heat / en.elec / en.mag）排在触发器后：{injectPairs} 对全部只在子弹药 / 子区落点注能（因果链注入记录全在链深 ≥ 1，本机出手 0 次；单条放根节点时本机命中注能）{Detail(landing)}");
                Expect(dual.Count == 0, $"单轨比对：{stage1.Count - 1} 条 × 5 载体的程序轨读法与独立推导的单轨读法逐字段一致（同一 owner 不两轨同时结算）{Detail(dual)}");
                // 负向：把旧轨字段也算进去（不跳旧字段 = 两轨同时结算），比对必须报失败
                string caught = SingleTrackProblem("comp_gun", FirmwareCarrier.Projectile, "fw_split", true);
                string caught2 = SingleTrackProblem("comp_sprayer", FirmwareCarrier.Field, "fw_cluster", true);
                Expect(caught != null && caught2 != null,
                    $"负向：故意两轨同时结算（分裂的旧 split 读法 + 子载荷；子母弹的旧爆开 + 子弹药）被单轨比对报出——{caught}；{caught2}");
                // 负向（EMG1-PRG-01 审查补记）：某条固件在本载体上没有任何生效声明（carriers 不含该载体）时仍占程序位、计表值热量，预览标“本载体不适用”
                EmgSource narrow = EmgCatalog.FromTables();
                narrow.Tuning = src.Tuning;
                foreach (EmgSource.Decl d in narrow.Decls.Where(d => d.Owner == "fw_echo"))
                {
                    d.Carriers = "projectile";
                }
                EmgCatalog.OverrideForTests(narrow);
                EmgProgramResult onMelee = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_echo", "fw_pierce" }, FirmwareCarrier.Melee);
                bool held = onMelee.Steps.Count == 2 && onMelee.Steps[0].OwnerId == "fw_echo" && onMelee.Steps[0].Decls.Count == 0 && onMelee.Steps[1].Ordinal == 1
                            && onMelee.HeatInstr >= FirmwareKinds.HeatOf("fw_echo") + FirmwareKinds.HeatOf("fw_pierce") - 1e-4f;
                Expect(held, $"负向：回波的声明只适用射弹时装在格斗上——仍占程序位 0（后面的贯穿在第 {(onMelee.Steps.Count > 1 ? onMelee.Steps[1].Ordinal : -1)} 位）、无生效声明、" +
                             $"表值热量照计（指令热量 {onMelee.HeatInstr:0.##}）；预览按“本载体不适用”显示（EmgProgramTreeText）");
                EmgCatalog.OverrideForTests(src);
                CheckPartDecls(src);
                CheckSourcedPorts(src);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        /// <summary>
        /// 审查修复（第 1 轮，FGT-EMG-003 部件部分）：stageGate ≤ 1 的部件声明在自身载体 / 宿主上比开 / 关（装 / 不装）——正式蓝图路径编出的武器在同一组场景里行为指纹不同，
        /// 写 ent.drone 的另验“装了才有无人机”。主组件：关 = 不出手的空武器；功能组件：宿主 = 液压刺锤机器，关 = 功能位空。
        /// 只在被击时结算的（读 evt.hurt 的爆炸反应装甲）由 FgEmgEraSelfCheck（A 装、G 旧轨）覆盖；结构模块写 prop.mass 由 FgEmgPartSelfCheck D（内核单位质量）覆盖；
        /// 维修束写 prop.integrity 由 FgEmgTriggerSelfCheck I2 覆盖。底盘特性 stageGate 全为 3，不计入阶段 1。
        /// </summary>
        private static void CheckPartDecls(EmgSource src)
        {
            Line("  · C3. FGT-EMG-003（阶段 1 部件）：stageGate ≤ 1 的部件声明在自身载体 / 宿主上装与不装行为指纹不同");
            string chassis = HomeValleyLayout.Erc003ChassisId;
            var owners = src.Decls.Where(d => d.StageGate <= 1 && d.OwnerKind != "firmware").GroupBy(d => d.Owner).ToList();
            var dead = new List<string>();
            var covered = new List<string>();
            int tested = 0;
            foreach (var g in owners)
            {
                string owner = g.Key;
                bool passive = g.Any(d => d.Dir == "read" && d.Port == "evt.hurt");
                if (g.First().OwnerKind == "structure" || passive || g.Any(d => d.Port == "prop.integrity"))
                {
                    covered.Add(owner);
                    continue;
                }
                if (GapOwners.TryGetValue(owner, out string gap))
                {
                    covered.Add(owner + "（" + gap + "）");
                    continue;
                }
                bool utility = owner.StartsWith("func_", StringComparison.Ordinal);
                CombatWeapon on = utility ? HostWeapon(chassis, ComponentCatalog.CompGunId, owner) : HostWeapon(chassis, owner, null);
                CombatWeapon off = utility ? HostWeapon(chassis, ComponentCatalog.CompGunId, null) : new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 0 };
                // 未预置标记的场景（标记源的行为要有它才看得见）+ 条件场景
                Obs a = RunArena(on, 4f, prepared: false), b = RunArena(off, 4f, prepared: false);
                Obs ac = RunArena(on, 4f, prepared: true, conditions: true), bc = RunArena(off, 4f, prepared: true, conditions: true);
                if (g.Any(d => d.Dir == "write" && d.Port == "evt.marked"))
                {
                    float decl = g.Where(d => d.Dir == "write" && d.Port == "evt.marked").Sum(d => d.Amount);
                    Expect(Mathf.Approximately(on.MarkSeconds, decl) && off.MarkSeconds == 0f,
                        $"{owner}：标记秒数由声明给（evt.marked emit 幅度 {decl} 秒 → 武器 {on.MarkSeconds} 秒；不装 {off.MarkSeconds}），不按组件 ID");
                }
                tested++;
                bool differs = a.Digest + ac.Digest != b.Digest + bc.Digest;
                if (g.Any(d => d.Dir == "write" && d.Port == "evt.marked"))
                {
                    differs |= a.MarkedCount > b.MarkedCount; // 标记不进伤害指纹：装了才有被标记的敌人
                }
                if (g.Any(d => d.Dir == "write" && d.Port == "ent.drone"))
                {
                    differs &= a.MaxDrones > 0 && b.MaxDrones == 0;
                }
                if (!differs)
                {
                    dead.Add(owner);
                }
            }
            Expect(tested >= 8 && dead.Count == 0,
                $"部件声明装 / 不装：{tested} 个部件在自身载体 / 宿主上行为指纹都不同（无人机舱装了才有无人机）{(dead.Count > 0 ? "；无差异：" + string.Join("、", dead) : string.Empty)}；" +
                $"另由专段覆盖 {covered.Count} 个：{string.Join("、", covered)}");
        }

        /// <summary>阶段 1 声明了、但 0.2 战斗里还没有落点的部件（已登记缺口，不计入“装 / 不装”对照）。</summary>
        private static readonly Dictionary<string, string> GapOwners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "func_dash", "FG-GAP-176：冲刺器没有冲刺 / 撞击行为" },
        };

        private static CombatWeapon HostWeapon(string chassis, string primary, string utility)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(chassis, primary, utility, null, Array.Empty<string>());
            return CombatSite.MachineWeaponFrom(UplinkCompiler.CompileUplinked(board, Array.Empty<string>()));
        }

        /// <summary>
        /// 审查修复（第 1 轮，FGR-EMG-015 规则三 / FGT-EMG-003“无源端口 0”）：stageGate ≤ 1 的声明读到的每个端口，都有 stageGate ≤ 1 的声明写者，
        /// 或有正常游戏里一直存在的内核 / 世界源（下表逐条写来源，按端口不按内容 ID）。
        /// </summary>
        private static void CheckSourcedPorts(EmgSource src)
        {
            Line("  · C4. 无源端口为 0（FGR-EMG-015 规则三）：阶段 1 读端口都有写者或世界源");
            var writers = new HashSet<string>(src.Decls.Where(d => d.StageGate <= 1 && d.Dir == "write").Select(d => d.Port), StringComparer.Ordinal);
            bool prism = EnemyDefs.TryGet("enemy_prism", out GameConfig.fg.MechEnemy row) && row.Behavior == EnemyDefs.BehaviorDeflect;
            string World(string port)
            {
                if (port.StartsWith("evt.", StringComparison.Ordinal))
                {
                    return "内核事件总线（EMG1-BUS-01）";
                }
                if (port.StartsWith("ter.", StringComparison.Ordinal))
                {
                    return "地图障碍 / 掩体（PCG 地形）";
                }
                switch (port)
                {
                    case "chs.moving": return "内核单位移动状态";
                    case "prop.heat": return "内核武器积热（每次出手）";
                    case "prop.charge": return writers.Contains("en.elec") ? "en.elec 写者注入的电荷" : null;
                    case "fld.deflect": return prism ? "敌方偏折单位（enemy_prism）" : null;
                    default: return null;
                }
            }
            var reads = src.Decls.Where(d => d.StageGate <= 1 && d.Dir == "read").Select(d => d.Port).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sourceless = reads.Where(p => !writers.Contains(p) && World(p) == null).ToList();
            var viaWorld = reads.Where(p => !writers.Contains(p)).Select(p => p + "←" + World(p)).ToList();
            Expect(reads.Count >= 10 && sourceless.Count == 0,
                $"阶段 1 声明读到 {reads.Count} 个端口，无源 {sourceless.Count} 个{(sourceless.Count > 0 ? "：" + string.Join("、", sourceless) : string.Empty)}；没有声明写者、靠世界源的：{string.Join("；", viaWorld)}");
        }

        /// <summary>EMG1-FW-02：新固件的触发事件在 C2 的常规场景里不会自然发生（阵亡 / 反弹 / 穿透 / 过热 / 冷却完成……），要在条件场景里比。</summary>
        internal static bool IsConditionalAddon(EmgSource src, string fw) =>
            src.Decls.Any(d => d.Owner == fw && d.Dir == "read" && d.Role == "trigger" && d.Port != "evt.hit" && d.Port != "evt.fire" && d.Port != "evt.expire");

        private static bool HasModWrite(string fw, FirmwareCarrier carrier)
        {
            EmgCompiled cat = EmgCatalog.Current;
            byte bit = (byte)(1 << (int)carrier);
            return cat.OwnerIndex.TryGetValue(fw, out int oi)
                   && cat.Decls.Any(d => d.Owner == oi && d.Dir == EmgDirs.Write && (d.Carriers & bit) != 0 && cat.PortIdByCode[d.Port].StartsWith("grm.mod.", StringComparison.Ordinal));
        }

        /// <summary>EMG2-PHS-01：被测固件在该载体上有能量注入写端（en.heat / en.elec / en.mag，<see cref="EmgProgramCompiler.RetiredTags"/> 非空的端口）。</summary>
        private static bool HasInjectWrite(string fw, FirmwareCarrier carrier)
        {
            EmgCompiled cat = EmgCatalog.Current;
            byte bit = (byte)(1 << (int)carrier);
            return cat.OwnerIndex.TryGetValue(fw, out int oi)
                   && cat.Decls.Any(d => d.Owner == oi && d.Dir == EmgDirs.Write && (d.Carriers & bit) != 0 && EmgProgramCompiler.RetiredTags(cat.PortIdByCode[d.Port]).Length > 0);
        }

        /// <summary>EMG2-PHS-01：按正式路径编出武器跑预置场景 4 游戏秒（因果链注入记录按链深分根节点 / 子载荷）。</summary>
        private static Obs DeclObs(string comp, string[] fws, bool prepared = true)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            return RunArena(w, 4f, prepared, program, comp);
        }

        /// <summary>把敌人压到 10% 血（总量大到 4 秒打不死）后跑预置场景，返回阵亡数：只有直接击毁（处决）才会让它们阵亡。</summary>
        private static int SummonExecuted(string comp, string[] fws)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            using Arena a = Arena.Build(w, true, null, program, comp);
            for (int i = 1; i < ArenaCount; i++)
            {
                a.K.SetHealth(a.Ids[i], 10000f, 100000f, true);
            }
            a.Run(4f);
            int dead = 0;
            for (int i = 1; i < ArenaCount; i++)
            {
                dead += a.K.TryGetUnit(a.Ids[i], out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0 ? 0 : 1;
            }
            return dead;
        }

        /// <summary>被测固件在该载体上有效果写端（非语法、非元程序）。</summary>
        private static bool HasEffectWrite(string fw, FirmwareCarrier carrier)
        {
            EmgCompiled cat = EmgCatalog.Current;
            if (!cat.OwnerIndex.TryGetValue(fw, out int oi))
            {
                return false;
            }
            byte bit = (byte)(1 << (int)carrier);
            return cat.Decls.Any(d => d.Owner == oi && d.Dir == EmgDirs.Write && (d.Carriers & bit) != 0
                                      && EmgCatalog.FamilyOf(cat.PortIdByCode[d.Port]) is var f && f != EmgFamily.Grammar && f != EmgFamily.Meta);
        }

        /// <summary>按正式路径编出武器（接入口装 <paramref name="fws"/>）→ 程序 → 程序轨读法 → 内核编队攻击 4 游戏秒的行为指纹。</summary>
        private static string DeclDigest(string comp, string[] fws, out EmgProgramResult program, out CombatReading reading)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            reading = w.Reading;
            // 三个场景都跑：原 C 段的预置场景 + 条件场景（障碍、残骸、热信号差）+ 掩体场景（障碍 / 残骸各一版挡在主目标前、其后另有敌人），FG19A 第 1.8 节；任一场景不同即算有行为
            return RunArena(w, 4f, prepared: true, program, comp).Digest + "#" + RunArena(w, 4f, prepared: true, program, comp, conditions: true).Digest
                   + "#" + RunArena(w, 4f, prepared: true, program, comp, cover: true).Digest
                   + "#" + RunArena(w, 4f, prepared: true, program, comp, wreckCover: true).Digest;
        }

        /// <summary>
        /// 单轨比对：程序轨读法（CombatSite.ProgramReading）与“独立推导”的读法逐字段比较。独立推导只看声明表：被测固件的旧字段只保留它写端口的过渡字段
        /// （<see cref="EmgProgramCompiler.TransitionFields"/>），旧标签只在有非语法写端时保留；只改数字的端口由折叠按旧字段加回。<paramref name="forceDual"/> = 负向：
        /// 拿“不跳任何旧字段”的读法（两轨同时结算）去比。返回 null = 一致。
        /// </summary>
        private static string SingleTrackProblem(string comp, FirmwareCarrier carrier, string fw, bool forceDual)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult pr = EmgProgramCompiler.Compile(p);
            if (pr.IsEmpty || w.Mode == CombatWeaponMode.Projectile)
            {
                return pr.IsEmpty ? "程序为空（声明未生效）" : null;
            }
            bool damageFromCompile = !p.HasCannonPrimary && !(CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent c) && c.Damage > 0f);
            CombatReading actual = forceDual
                ? CombatSite.FoldProgramReadings(CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, CombatSite.ReadingFirmware(p), damageFromCompile), pr, damageFromCompile)
                : CombatSite.ProgramReading(p, w.Reading, pr);
            // 独立推导：从声明表读被测固件的写端口（单条程序全部在根节点）
            EmgCompiled cat = EmgCatalog.Current;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var retired = new HashSet<string>(StringComparer.Ordinal);
            bool tags = false;
            if (cat.OwnerIndex.TryGetValue(fw, out int oi))
            {
                byte bit = (byte)(1 << (int)carrier);
                foreach (EmgDeclRow d in cat.Decls.Where(d => d.Owner == oi && d.Dir == EmgDirs.Write && (d.Carriers & bit) != 0))
                {
                    string port = cat.PortIdByCode[d.Port];
                    EmgFamily fam = EmgCatalog.FamilyOf(port);
                    retired.UnionWith(EmgProgramCompiler.RetiredTags(port)); // EMG2-PHS-01：能量注入写端的旧标签退役（FGR-EMG-019）
                    if (fam != EmgFamily.Grammar && fam != EmgFamily.Meta)
                    {
                        if (!EmgProgramCompiler.IsRootKernelPort(port)) // EMG1-FIX-01：根节点由内核直接执行的端口（追射）不留过渡字段
                        {
                            keep.UnionWith(EmgProgramCompiler.TransitionFields(port));
                        }
                        tags = true;
                    }
                }
            }
            CombatReading expected = CombatSite.FoldProgramReadings(
                CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, CombatSite.ReadingFirmware(p), damageFromCompile,
                    (f, field) => f == fw && (field == EmgProgramCompiler.TagsField ? !tags
                        : field.StartsWith(EmgProgramCompiler.TagsField + ":", StringComparison.Ordinal) ? retired.Contains(field.Substring(EmgProgramCompiler.TagsField.Length + 1))
                        : !keep.Contains(field))),
                pr, damageFromCompile);
            string a = ReadingFields(actual), e = ReadingFields(expected);
            if (a == e)
            {
                return null;
            }
            string[] aa = a.Split(';'), ee = e.Split(';');
            var diff = aa.Zip(ee, (x, y) => (x, y)).Where(t => t.x != t.y).Select(t => t.x + "≠" + t.y.Substring(t.y.IndexOf('=') + 1)).Take(3);
            return "读法不一致 " + string.Join("，", diff);
        }

        private static readonly FieldInfo[] ReadingFieldInfos = typeof(CombatReading).GetFields(BindingFlags.Public | BindingFlags.Instance);

        private static string ReadingFields(CombatReading r)
        {
            var sb = new StringBuilder();
            object boxed = r;
            foreach (FieldInfo f in ReadingFieldInfos)
            {
                sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(boxed), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }
    }
}
