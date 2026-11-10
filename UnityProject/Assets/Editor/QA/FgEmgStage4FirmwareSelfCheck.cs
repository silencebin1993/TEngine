using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static GameLogic.EditorTools.FgReadingMatrixSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-FW-01（FG19A 第 6 节 N39～N41“劫持新固件落表”；FGT-EMG-002 / 003 / 006，FGT-EMG-061 用正式固件复跑，FGJ-EMG-4 第一步；关闭 DEBT-EMG4HIJ01-01）。
    /// 起真实系统：真实 Luban 表与声明编译 / 准入（EmgCatalog）、真实蓝图 → 预览 → 程序编译（UplinkCompiler / EmgProgramCompiler）→ 地点武器结算（CombatSite.WithProgram，含刷写变体登记）、
    /// 真实内核（命中注入入侵度、阵亡统一入口劫持、夺控、信号脉冲 / 中继组）、真实掉落 / 三选一 / 数据复原入口、真实存档往返。
    /// A 数据：3 条接在阶段 3 之后、既有行不动；stageGate 4、准入、声明轨；文本键 / 5 载体读法 / 占位图标；三选一追加行排在阶段 3 之后；详情端口行。
    /// B 零死对（FGT-EMG-003）：重启注入 / 劫持中继 / 反夺控 × 5 载体 × 程序首位 / 末位，各自的行为证据成立、对照不成立。
    /// C 获取（FGT-EMG-006）：emg.stage = 3 时 3 条都拿不到；= 4 时全部进三选一 / 数据复原，杂兵残骸来源的进掉落。
    /// D 重启注入（FGT-EMG-061 正式固件 / FGJ-EMG-4 第一步）：两发满阈值 → 劫持、30 秒期满自毁、击杀只结算一次；精英 / 首领按抗性拒绝并写原因、悬停“不可劫持”；上限满只加入侵度。
    /// E 劫持中继（N40，DEBT-EMG4HIJ01-01）：被劫持的敌机改刷劫持者程序（去核心列表、占带宽、带宽 1 截到 1 条、带宽满照旧原生程序）；它开火给别的敌人写入侵度；对照不装 / 不劫持不执行；不扩大本机继承。
    /// F 反夺控（N41）：被夺控 → 按原阵营向中继组广播 0 号频道，装中继应答的友机开火；接入中槽位被封锁同样广播；没被夺控、evt.release、普通命中都不广播。
    /// G 存读档 / 暂停 / 倍速：劫持中继中途存读档哈希一致、续跑同步；0.5x～3x 期满 30 游戏秒；旧存档读入新固件不凭空进库存。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgStage4FirmwareSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgStage4FirmwareSelfCheck。
    /// </summary>
    public static class FgEmgStage4FirmwareSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail, _pass;
        private const int Slot = 7;
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        /// <summary>FG19A 第 6 节暂定 ID（定稿 = 暂定）与写端口。</summary>
        internal static readonly (string Id, string Write)[] Spec = { ("fw_reboot", "hij.hack"), ("fw_hijackrelay", "grm.inherit"), ("fw_failsafe", "evt.ping") };

        [MenuItem("BinGames/QA/自检/劫持新固件（EMG4-FW-01）")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = _pass = 0;
            Line("\n[阶段 4 新固件] FG19A 第 6 节 N39～N41 落表（EMG4-FW-01；FGT-EMG-002 / 003 / 006 / 061，FGJ-EMG-4 第一步，DEBT-EMG4HIJ01-01）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）");
            string dir = Path.Combine(Path.GetTempPath(), "bingames-emg4fw01");
            GameLanguage lang = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                CarrierReadings.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                EmgPhaseCatalog.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                Step(CheckData);
                Step(CheckZeroDead);
                Step(CheckSources);
                Step(CheckReboot);
                Step(CheckRelay);
                Step(CheckFailsafe);
                Step(CheckSaveSpeed);
            }
            finally
            {
                EmgCatalog.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CampaignSession.Clear();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [阶段 4 新固件] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 3 条接在阶段 3 之后、既有行不动；stageGate 4、准入、声明轨、文本键与占位图标齐；三选一追加行排在阶段 3 之后");
            var rows = FirmwareKinds.Rows.Where(r => r != null && !FirmwareKinds.IsMixedRow(r)).Select(r => r.Id).ToList();
            var addon = FirmwareKinds.AddonRows.Select(r => r.Id).ToList();
            int before = 8 + FgEmgStage3FirmwareSelfCheck.Spec.Length;
            Expect(addon.Count == before + Spec.Length && addon.Skip(before).SequenceEqual(Spec.Select(s => s.Id)) && rows.Skip(44 + before).SequenceEqual(Spec.Select(s => s.Id))
                   && FirmwareKinds.BaseRows.Count == 44 && addon.Take(8).Concat(FgEmgStage3FirmwareSelfCheck.Spec.Select(s => s.Id)).SequenceEqual(addon.Take(before))
                   && Spec.All(s => FirmwareCatalog.All.ContainsKey(s.Id) && FirmwareCatalog.IsAddon(s.Id)),
                $"新固件 3 条按 FG19A 顺序追加在表尾第 {45 + before}～{44 + before + Spec.Length} 行（{string.Join("、", addon.Skip(before))}）；名表 44 条、阶段 1 / 3 的 {before} 条不动；都进固件目录");
            EmgSource src = EmgCatalog.FromTables();
            var ports = src.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var bad = new List<string>();
            foreach ((string id, string write) in Spec)
            {
                var decls = src.Decls.Where(d => d.Owner == id).ToList();
                if (decls.Count == 0 || decls.Any(d => d.StageGate != 4) || !decls.Any(d => d.Dir == "write" && d.Port == write))
                {
                    bad.Add($"{id} 声明 stageGate {string.Join("/", decls.Select(d => d.StageGate).Distinct())} / 写端 {string.Join("/", decls.Where(d => d.Dir == "write").Select(d => d.Port))}");
                }
                if (!EmgCatalog.Admit(EmgCatalog.DeclsOf(src, id), ports, out string why))
                {
                    bad.Add($"{id} 准入被拒：{why}");
                }
                if (!EmgCatalog.IsOwnerOnDeclTrack(id))
                {
                    bad.Add($"{id} 阶段 4 未走声明轨");
                }
                FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
                string[] keys = { row.NameKey, row.DescKey, row.AcquireKey, row.ReadProjectileKey, row.ReadMeleeKey, row.ReadSummonKey, row.ReadAuraKey, row.ReadFieldKey };
                foreach (string k in keys)
                {
                    string zh = GameText.Get(k, GameLanguage.ZhCn), en = GameText.Get(k, GameLanguage.En);
                    if (string.IsNullOrEmpty(zh) || zh == k || string.IsNullOrEmpty(en) || en == k || zh.Contains("hij.") || zh.Contains("evt.") || zh.Contains("grm."))
                    {
                        bad.Add($"{id} 文本键 {k} 缺中 / 英或露出内部端口名");
                    }
                }
                string icon = Path.Combine(Application.dataPath, "GameRes", "Raw", "UI", "Icons", row.Icon + ".png");
                if (row.Icon != "icon_" + id || !File.Exists(icon) || !File.Exists(icon + ".meta"))
                {
                    bad.Add($"{id} 占位图标 {row.Icon} 缺失");
                }
                if (row.Protocol != "enemy" || (row.Source != "salvage" && row.Source != "elite") || row.ReadFields != "none" || row.LegacyId != "none" || row.Kind != "regular")
                {
                    bad.Add($"{id} 协议 / 来源 / 读法字段 / 种类不是“敌方加密 · 杂兵残骸或精英 · 只走声明 · 常规”");
                }
            }
            // N41 的读端：evt.seize trigger + hij.seized condition（FG19A 表 N41 行；状态端口只作 condition），N40 只读 hij.hijacked condition
            bool n41 = src.Decls.Any(d => d.Owner == "fw_failsafe" && d.Dir == "read" && d.Port == "evt.seize" && d.Role == "trigger")
                       && src.Decls.Any(d => d.Owner == "fw_failsafe" && d.Dir == "read" && d.Port == "hij.seized" && d.Role == "condition");
            bool n40 = src.Decls.Where(d => d.Owner == "fw_hijackrelay" && d.Dir == "read").Select(d => d.Port + "/" + d.Role).SequenceEqual(new[] { "hij.hijacked/condition" });
            Expect(bad.Count == 0 && n41 && n40,
                $"3 条 stageGate = 4、全部准入且在声明轨（N41 读 evt.seize trigger + hij.seized condition、N40 读 hij.hijacked condition）、名称 / 描述 / 获取途径 / 5 载体读法中英齐、占位图标与 .meta（B22 占位）{Detail(bad)}");
            Line("    · 例（射弹）：" + string.Join("；", Spec.Select(s => FirmwareKinds.Reading(s.Id, FirmwareCarrier.Projectile))));
            string detail = CarrierReadings.DetailLines("fw_failsafe") ?? string.Empty;
            Expect(detail.Contains(GameText.Get("emg.port.evt.seize.name")) && detail.Contains(GameText.Get("emg.port.hij.seized.name")) && detail.Contains(GameText.Get("emg.port.evt.ping.name")),
                $"固件详情页列出读 / 写端口（夺控 · 被夺控 → 信号脉冲）：{detail.Replace('\n', '｜')}");
            var picks = LootPickCatalog.Rows.Where(r => FirmwareCatalog.IsAddon(r.Target)).ToList();
            var e3 = picks.Where(r => FgEmgStage3FirmwareSelfCheck.Spec.Any(s => s.Id == r.Target)).ToList();
            var e4 = picks.Where(r => Spec.Any(s => s.Id == r.Target)).ToList();
            Expect(e3.Count > 0 && e4.Count == Spec.Length && e3.Max(r => r.SortOrder) < e4.Min(r => r.SortOrder),
                $"fg.TbLootPick：阶段 3 追加行 sortOrder {e3.Min(r => r.SortOrder)}～{e3.Max(r => r.SortOrder)}（不变），阶段 4 的 {e4.Count} 行 {(e4.Count > 0 ? e4.Min(r => r.SortOrder) + "～" + e4.Max(r => r.SortOrder) : "无")} 接在后面（权重 1，临时值）");
            // 预览：劫持中继写明作用对象；带条件的 grm.inherit 不扩大本机继承（[重启注入, 分裂, 劫持中继] 的子节点与 [重启注入, 分裂] 相同）
            EmgProgramResult with = P("fw_reboot", "fw_split", "fw_hijackrelay"), without = P("fw_reboot", "fw_split"), widened = P("fw_reboot", "fw_split", "fw_swarm");
            string tree = TreeText(with);
            string hackRow = EmgConst.PortHack + ":";
            Expect(EmgProgramCompiler.HasHijackFlash(with) && !EmgProgramCompiler.HasHijackFlash(without) && !EmgProgramCompiler.HasHijackFlash(widened)
                   && tree.Contains(GameText.Get("emg.tree.tag.hijack_flash").Split('：')[0]) && ChildShape(with) == ChildShape(without) && !ChildShape(with).Contains(hackRow),
                $"编译：带劫持中继的程序标 HijackFlash（预览“{tree}”）；子节点继承与不装时一致（“{ChildShape(with)}” = “{ChildShape(without)}”：hij.hijacked 条件的扩大继承只作用于被劫持的敌机，" +
                $"不把重启注入继承进子弹药）；参照：无条件写 grm.inherit 的集群协议子节点“{ChildShape(widened)}”");
        }

        private static ushort InheritPort => EmgCatalog.Current.PortIdByCode.First(kv => kv.Value == "grm.inherit").Key;

        /// <summary>子节点里的内核行（端口 / 角色；不含劫持中继自己那两行）——比较继承是否被扩大。</summary>
        private static string ChildShape(EmgProgramResult r) => string.Join(";", r.KernelNodes.Skip(1).Select(n =>
            string.Join(",", Enumerable.Range(n.FirstInstr, n.InstrCount).Select(i => r.KernelInstrs[i]).Where(x => x.Port != EmgConst.PortHijacked && x.Port != InheritPort && x.Port != 0).Select(x => x.Port + ":" + x.Role))));

        // ── B. 零死对（FGT-EMG-003）──

        private static void CheckZeroDead()
        {
            Line("  · B. 零死对：3 条 × 5 载体 × 程序首位 / 末位（正式蓝图编译 + 地点武器结算 + 真实内核），行为证据成立、对照不成立");
            var dead = new List<string>();
            var lines = new List<string>();
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                // N39：命中给敌方目标写入侵度（对照：只装贯穿 = 0）
                float first = MaxHack(comp, new[] { "fw_reboot", "fw_pierce" }), last = MaxHack(comp, new[] { "fw_pierce", "fw_reboot" }), ctl = MaxHack(comp, new[] { "fw_pierce" });
                Judge(dead, carrier, "重启注入", first > 0f && last > 0f && ctl == 0f);
                // N40：劫持后被劫持体改刷本机程序（对照：只装重启注入不改刷）
                bool fl = Relayed(comp, new[] { "fw_hijackrelay", "fw_reboot" }), ll = Relayed(comp, new[] { "fw_reboot", "fw_hijackrelay" }), cl = Relayed(comp, new[] { "fw_reboot" });
                Judge(dead, carrier, "劫持中继", fl && ll && !cl);
                // N41：被夺控时广播（对照：不装不广播）
                long fs = Failsafe(comp, new[] { "fw_failsafe", "fw_pierce" }), ls = Failsafe(comp, new[] { "fw_pierce", "fw_failsafe" }), cs = Failsafe(comp, new[] { "fw_pierce" });
                Judge(dead, carrier, "反夺控", fs == 1 && ls == 1 && cs == 0);
                lines.Add($"{carrier}：入侵度 {first:0.##}/{last:0.##}/对照 {ctl:0.##}，改刷 {fl}/{ll}/对照 {cl}，求援 {fs}/{ls}/对照 {cs}");
            }
            Expect(dead.Count == 0, $"15 组（3 条 × 5 载体，首位 / 末位 / 对照）全部有行为差异：{string.Join("｜", lines)}{Detail(dead)}");
        }

        private static void Judge(List<string> dead, FirmwareCarrier carrier, string what, bool ok)
        {
            if (!ok)
            {
                dead.Add($"{what} × {carrier}");
            }
        }

        /// <summary>机器（接入口装 <paramref name="fws"/>）对一台 5000 血的敌机出手 3 秒，返回敌机入侵度的最大值。</summary>
        private static float MaxHack(string comp, string[] fws)
        {
            using var site = NewSite("b-hack");
            CombatKernel k = site.Kernel;
            int me = Machine(k, new double2(0, 0), MachineWeapon(site, comp, fws, out _));
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), -1, 5000f);
            float max = 0f;
            for (int s = 0; s < 180; s++)
            {
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                max = Math.Max(max, k.AccumOf(foe, EmgPhaseConst.Hack));
            }
            return max;
        }

        /// <summary>入侵度打满后由机器击毁（阵亡统一入口）→ 被劫持的敌机是否改刷了劫持者程序。</summary>
        private static bool Relayed(string comp, string[] fws)
        {
            using var site = NewSite("b-relay");
            CombatKernel k = site.Kernel;
            int me = Machine(k, new double2(0, 0), MachineWeapon(site, comp, fws, out _));
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), k.AddWeapon(Instant(5f, 12f, -1)), 5000f);
            return HijackByHits(k, me, foe, out _) && k.HijackFlashed(foe);
        }

        /// <summary>装 <paramref name="fws"/> 的机器 A 被夺控；同一中继组里有装中继应答的 B。返回 A 的广播次数。</summary>
        private static long Failsafe(string comp, string[] fws)
        {
            using var site = NewSite("b-fail");
            CombatKernel k = site.Kernel;
            int a = Machine(k, new double2(0, 0), MachineWeapon(site, comp, fws, out _));
            int b = Listener(k, new double2(4, 0));
            Unit(k, CombatFaction.Hostile, new double2(30, 30), -1, 500f);
            k.Seize(a, 45f, 0, 1);
            StepN(k, 3);
            return k.EmgBusStats.PingsSent;
        }

        // ── C. 获取（FGT-EMG-006）──

        private static void CheckSources()
        {
            Line("  · C. FGT-EMG-006：只在 emg.stage ≥ 4 时开放（杂兵残骸掉落 / 精英三选一 / 数据复原 / 可装机）；阶段 3 及以前的新固件不受影响");
            CampaignState s = CampaignState.CreateNew("emg4fw01-c", "Standard", 1);
            var pool = new List<LootPickDef>();
            try
            {
                foreach (int stage in new[] { 3, 4 })
                {
                    UseStage(stage);
                    var leaked = new List<string>();
                    int open = 0;
                    foreach ((string id, _) in Spec)
                    {
                        string fac = FirmwareKinds.FactionOf(id);
                        LootPickCatalog.PoolFor(fac, pool);
                        FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
                        bool inDrop = LootCatalog.EnemyFirmwareOf(fac).Contains(id);
                        bool inPick = pool.Any(p => p.Target == id);
                        bool inRestore = FirmwareRestoreService.Candidates(s).Contains(id);
                        bool obtainable = FirmwareKinds.IsObtainable(id) && FirmwareKinds.HasMachineImplementation(id);
                        bool want = stage >= 4;
                        bool wantDrop = want && row.Source == "salvage";
                        if (inPick && inRestore && obtainable && inDrop == wantDrop)
                        {
                            open++;
                        }
                        if (inDrop != wantDrop || inPick != want || inRestore != want || obtainable != want)
                        {
                            leaked.Add($"{id}（掉落 {inDrop} 三选一 {inPick} 复原 {inRestore} 可装 {obtainable}，应 {want}）");
                        }
                    }
                    int prior = FirmwareKinds.AddonRows.Take(8 + FgEmgStage3FirmwareSelfCheck.Spec.Length).Count(r => FirmwareKinds.IsObtainable(r.Id));
                    Expect(leaked.Count == 0 && open == (stage >= 4 ? Spec.Length : 0) && prior == 8 + FgEmgStage3FirmwareSelfCheck.Spec.Length,
                        $"emg.stage = {stage}：阶段 4 新固件开放 {open} / {Spec.Length} 条（负向：stageGate 未生效时不进掉落 / 三选一 / 复原、不能装机）；阶段 1 / 3 的 {prior} 条照常开放{Detail(leaked)}");
                }
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Expect(EmgCatalog.EffectiveStage >= 4 && Spec.All(x => FirmwareKinds.IsObtainable(x.Id)),
                $"正式表 emg.stage = {EmgCatalog.EffectiveStage}：3 条在正常游戏里拿得到（重启注入来自杂兵残骸，FGJ-EMG-4 第一步“劫持一台敌机”可从正式入口装出）");
        }

        // ── D. 重启注入（FGT-EMG-061 正式固件，FGJ-EMG-4 第一步）──

        private static void CheckReboot()
        {
            Line("  · D. 重启注入（正式固件）：劫持一台敌机、30 秒期满自毁、击杀只结算一次；精英 / 首领按抗性拒绝并写原因；上限满只加入侵度");
            using (var site = NewSite("d-core"))
            {
                CombatKernel k = site.Kernel;
                int me = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot" }, out _));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 3), k.AddWeapon(Instant(5f, 12f, -1)), 5000f);
                var ev = new List<CombatEvent>();
                bool full = HijackByHits(k, me, foe, out float hack, ev);
                bool hijacked = View(k, foe).Behavior == CombatBehavior.Hijacked && View(k, foe).Faction == CombatFaction.Player && k.HijackCount == 1 && Count(ev, CombatEventKind.Hijacked, foe) == 1
                                && Count(ev, CombatEventKind.Killed, foe) == 0 && k.WreckCount == 0;
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                double t0 = k.Time;
                for (int s = 0; s < 2000 && Alive(k, foe); s++)
                {
                    k.Step(Dt, k.Time + Dt);
                    ev.AddRange(Drain(k));
                }
                double lived = k.Time - t0;
                Expect(full && hijacked && !Alive(k, foe) && lived > 29.4 && lived < 30.6 && Count(ev, CombatEventKind.Killed, foe) == 1 && Count(ev, CombatEventKind.HijackEnded, foe) == 1 && k.WreckCount == 1,
                    $"正式固件 [重启注入]：命中累积入侵度 {hack:0.##}（满阈值 1，两发 0.6）时那一发打死 → 劫持成立（不发 Killed、不留残骸、劫持 {k.HijackCount}）；{lived:0.##} 秒后期满自毁，Killed / HijackEnded 各 1 次、残骸 {k.WreckCount}");
            }
            // 精英 / 首领：入侵抗性 1，命中被拒（写原因），入侵度不累积，击毁照常阵亡；悬停写“不可劫持”
            using (var site = NewSite("d-elite"))
            {
                CombatKernel k = site.Kernel;
                int me = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot" }, out _));
                var rec = new RegionEnemyRecord { EnemyInstanceId = "emg4fw-elite", RegionId = "d-elite", Position = new Vector2(0f, 3f), Health = 5000f, MaxHealth = 5000f, IsAlive = true };
                int elite = site.SpawnEnemy(rec, new CombatSpawn
                {
                    Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire, Flags = Live | CombatUnitFlags.Elite | CombatUnitFlags.Report,
                    Radius = 0.5f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                var normalRec = new RegionEnemyRecord { EnemyInstanceId = "emg4fw-grunt", RegionId = "d-elite", Position = new Vector2(20f, 20f), Health = 50f, MaxHealth = 50f, IsAlive = true };
                int grunt = site.SpawnEnemy(normalRec, new CombatSpawn
                {
                    Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire, Flags = Live | CombatUnitFlags.Report, Radius = 0.5f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                var ev = new List<CombatEvent>();
                for (int s = 0; s < 180; s++)
                {
                    k.FireAt(me, elite, k.Time);
                    k.Step(Dt, k.Time + Dt);
                    ev.AddRange(Drain(k));
                }
                float hack = k.AccumOf(elite, EmgPhaseConst.Hack);
                string hover = site.UnhackableHoverLine(elite), plain = site.UnhackableHoverLine(grunt);
                k.Kill(elite, me);
                ev.AddRange(Drain(k));
                int refused = ev.Count(e => e.Kind == CombatEventKind.HijackRefused && e.Unit == elite && e.Code == EmgHijackConst.RefuseResist);
                Expect(hack == 0f && refused > 0 && !Alive(k, elite) && k.HijackCount == 0 && Count(ev, CombatEventKind.Killed, elite) == 1
                       && hover != null && hover.Contains("不可劫持") && plain == null,
                    $"负向：重启注入打精英（入侵抗性 {k.EffectiveResist(elite, EmgPhaseConst.ResHack):0.##}）→ 入侵度 {hack:0.##}、拒绝事件 {refused} 次（原因 = 抗性，热更层每台通知一次）、击毁照常阵亡（劫持 {k.HijackCount}）；" +
                    $"悬停“{hover}”；普通敌机不显示（{plain ?? "null"}）");
            }
            // 上限满（2 台）：第三台命中照样累积入侵度，击毁时拒绝（上限）照常阵亡
            using (var site = NewSite("d-cap"))
            {
                CombatKernel k = site.Kernel;
                int me = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot" }, out _));
                var foes = new[] { new double2(-3, 3), new double2(3, 3), new double2(0, 4) }.Select(p => Unit(k, CombatFaction.Hostile, p, -1, 5000f)).ToArray();
                bool two = HijackByHits(k, me, foes[0], out _) & HijackByHits(k, me, foes[1], out _);
                var ev = new List<CombatEvent>();
                bool third = HijackByHits(k, me, foes[2], out float hack3, ev);
                Expect(two && !third && hack3 >= 0.5f && k.HijackCount == 2 && !Alive(k, foes[2]) && ev.Any(e => e.Kind == CombatEventKind.HijackRefused && e.Unit == foes[2] && e.Code == EmgHijackConst.RefuseCap)
                       && Count(ev, CombatEventKind.Killed, foes[2]) == 1,
                    $"负向：已劫持 2 台（上限 2）时第三台照样被写入侵度（击毁前 {hack3:0.##}，致死一发后满阈值才会判上限）、击毁时拒绝（上限）照常阵亡，劫持仍 {k.HijackCount} 台");
            }
        }

        // ── E. 劫持中继（N40，DEBT-EMG4HIJ01-01）──

        private static void CheckRelay()
        {
            Line("  · E. 劫持中继：被劫持的敌机改刷劫持者程序（去核心、占带宽）；它开火给别的敌人写入侵度；对照不装 / 没被劫持不执行；带宽 1 截到 1 条、带宽满照旧原生程序");
            foreach (bool relay in new[] { true, false })
            {
                using var site = NewSite("e-" + relay);
                CombatKernel k = site.Kernel;
                string[] fws = relay ? new[] { "fw_reboot", "fw_hijackrelay" } : new[] { "fw_reboot" };
                int wi = MachineWeapon(site, ComponentCatalog.CompGunId, fws, out EmgProgramResult pr);
                int me = Machine(k, new double2(0, 0), wi);
                int native = k.AddWeapon(Instant(5f, 12f, -1));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), native, 5000f);
                int bystander = Unit(k, CombatFaction.Hostile, new double2(-6, 0), -1, 5000f, CombatBehavior.HoldFire); // 被打但不被劫持的敌机：武器不变
                k.FireAt(me, bystander, k.Time);
                StepN(k, 5);
                HijackByHits(k, me, foe, out _);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                int4 head = k.FlashHeadOf(wi);
                int held = k.TryGetHijack(foe, out _, out _, out int h) ? h : -1;
                int want = relay ? k.FlashVariantOf(wi, Math.Min(k.FlashBandwidth, head.z), true) : native;
                bool weaponOk = View(k, foe).Weapon == want && View(k, bystander).Weapon == -1;
                // 被劫持的敌机开火：附近另一台敌机的入侵度（改刷了 [重启注入] 才有）
                int target = Unit(k, CombatFaction.Hostile, new double2(0, 7), -1, 5000f, CombatBehavior.HoldFire);
                float got = 0f;
                for (int s = 0; s < 240; s++)
                {
                    k.Step(Dt, k.Time + Dt);
                    got = Math.Max(got, k.AccumOf(target, EmgPhaseConst.Hack));
                }
                string hover = site.HijackHoverLine(foe) ?? string.Empty;
                if (relay)
                {
                    Expect(EmgProgramCompiler.HasHijackFlash(pr) && head.x >= 0 && k.HijackFlashed(foe) && weaponOk && held == head.z && k.FlashUsed(me) == held && got > 0f
                           && hover.Contains(GameText.Get("emg.hijack.hover.flashed")),
                        $"[重启注入, 劫持中继]：被劫持的敌机武器 = 劫持者去核心刷写变体（{View(k, foe).Weapon} = {want}），持有 {held} / {head.z} 条、劫持者带宽占用 {k.FlashUsed(me)}；" +
                        $"它开火后旁边敌机入侵度 {got:0.##}（改刷的重启注入生效）；被打但没被劫持的敌机武器不变；悬停“{hover.Replace('\n', '｜')}”");
                }
                else
                {
                    Expect(!k.HijackFlashed(foe) && weaponOk && got == 0f && !hover.Contains(GameText.Get("emg.hijack.hover.flashed")),
                        $"对照 [重启注入]（不装劫持中继）：被劫持的敌机仍带原生武器（{View(k, foe).Weapon} = {want}），开火后旁边敌机入侵度 {got:0.##}（不执行劫持者程序）");
                }
            }
            // 带宽 1：截到 1 条；带宽满（第二台）：照旧原生程序、不占带宽
            using (var site = NewSite("e-bw", 1))
            {
                CombatKernel k = site.Kernel;
                int wi = MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot", "fw_hijackrelay" }, out _);
                int me = Machine(k, new double2(0, 0), wi);
                int native = k.AddWeapon(Instant(5f, 12f, -1));
                int f1 = Unit(k, CombatFaction.Hostile, new double2(-2, 2), native, 5000f), f2 = Unit(k, CombatFaction.Hostile, new double2(2, 2), native, 5000f);
                HijackByHits(k, me, f1, out _);
                HijackByHits(k, me, f2, out _);
                int h1 = k.TryGetHijack(f1, out _, out _, out int a) ? a : -1, h2 = k.TryGetHijack(f2, out _, out _, out int b) ? b : -1;
                Expect(k.FlashBandwidth == 1 && h1 == 1 && k.HijackFlashed(f1) && View(k, f1).Weapon == k.FlashVariantOf(wi, 1, true) && h2 == 0 && !k.HijackFlashed(f2) && View(k, f2).Weapon == native
                       && k.FlashUsed(me) == 1 && k.HijackCount == 2,
                    $"带宽 1：第一台截到 1 条（武器 = 前 1 条变体）；第二台带宽已满 → 照旧原生武器、占 {h2} 条（劫持仍成立，{k.HijackCount} 台），劫持者占用 {k.FlashUsed(me)}");
            }
            // 修复轮 P1-1：[重启注入, 劫持中继, 自毁装药] 改刷体期满自毁 → 还原原生武器，阵亡效果按原生程序与原阵营：不以敌方阵营放玩家的自毁子载荷
            using (var site = NewSite("e-scuttle"))
            {
                CombatKernel k = site.Kernel;
                int wi = MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot", "fw_hijackrelay" }, out _, new[] { "fw_scuttle" }); // 接入配额 2：自毁装药装本地
                int me = Machine(k, new double2(0, 0), wi);
                int ally = Machine(k, new double2(2, 0), -1);
                int native = k.AddWeapon(Instant(5f, 12f, -1));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), native, 5000f);
                bool hj = HijackByHits(k, me, foe, out _);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                bool flashed = k.HijackFlashed(foe);
                int4 head = k.FlashHeadOf(wi);
                float hpMe = View(k, me).Health, hpAlly = View(k, ally).Health;
                long sub = k.EmgBusStats.SubSpawned;
                for (int s = 0; s < (int)((EmgHijackConst.DefaultSeconds + 3f) / Dt); s++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                Expect(hj && flashed && head.z >= 3 && !Alive(k, foe) && k.HijackCount == 0 && k.EmgBusStats.SubSpawned == sub
                       && View(k, me).Health == hpMe && View(k, ally).Health == hpAlly,
                    $"[重启注入, 劫持中继, 自毁装药]：改刷体（去核心 {head.z} 条）期满自毁后放出子载荷 {k.EmgBusStats.SubSpawned - sub} 份（应 0：结束时还原原生武器），" +
                    $"劫持者耐久 {hpMe:0.#} → {View(k, me).Health:0.#}、友机 {hpAlly:0.#} → {View(k, ally).Health:0.#}");
            }
            // 修复轮 P1-2（FGR-EMG-036“适用”含被劫持敌机）：改刷体离劫持者超过 emg.flash.range 后每再远 emg.flash.step 暂停末尾 1 条，悬停“离母机过远”；回到范围内恢复
            using (var site = NewSite("e-range"))
            {
                CombatKernel k = site.Kernel;
                int wi = MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot", "fw_hijackrelay" }, out _);
                int me = Machine(k, new double2(0, 0), wi);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), k.AddWeapon(Instant(5f, 12f, -1)), 5000f);
                HijackByHits(k, me, foe, out _);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                int held = k.TryGetHijack(foe, out _, out _, out int h0) ? h0 : -1;
                k.SetPosition(foe, new double2(0, EmgConst.DefaultFlashRange + 2.5));
                int pause = 0;
                double dist = 0.0;
                for (int s = 0; s < 30 && pause == 0; s++)
                {
                    k.Step(Dt, k.Time + Dt);
                    pause = k.HijackFlashPause(foe);
                    dist = math.distance(View(k, foe).Position, View(k, me).Position);
                }
                int want = Math.Min(held, (int)Math.Ceiling((dist - EmgConst.DefaultFlashRange) / EmgConst.DefaultFlashStep));
                string far = site.HijackHoverLine(foe) ?? string.Empty;
                bool farOk = held >= 1 && pause == want && View(k, foe).Weapon == k.FlashVariantOf(wi, held - pause, true) && k.FlashUsed(me) == held
                             && far.Contains(GameText.Format("emg.flash.hover.range", pause));
                k.SetPosition(foe, new double2(0, 2));
                StepN(k, 20);
                string near = site.HijackHoverLine(foe) ?? string.Empty;
                Expect(farOk && k.HijackFlashPause(foe) == 0 && View(k, foe).Weapon == k.FlashVariantOf(wi, held, true) && !near.Contains(GameText.Format("emg.flash.hover.range", pause)),
                    $"距离衰减：改刷体离劫持者 {dist:0.##} 米 → 暂停 {pause} / 应 {want} 条（持有 {held}，武器 = 前 {held - pause} 条变体，带宽占用不变 {k.FlashUsed(me)}），" +
                    $"悬停“{far.Replace('\n', '｜')}”；回到 2 米内暂停 {k.HijackFlashPause(foe)}、武器回到前 {held} 条");
            }
            // 修复轮 P2：被夺控机器上的普通 ping 写者（N30 击杀回传，不带 hij.seized）按此刻阵营找中继组，不向玩家中继组报敌方战果；只有 N41 按原阵营
            using (var site = NewSite("e-ping"))
            {
                CombatKernel k = site.Kernel;
                int a = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_killping" }, out _));
                k.SetUnitRelay(a, true);
                Listener(k, new double2(4, 0));
                k.Seize(a, 45f, 0, 1);
                int victim = Machine(k, new double2(0, 3), -1);
                k.SetHealth(victim, 1f, 2000f, true);
                for (int s = 0; s < 240 && Alive(k, victim); s++)
                {
                    k.FireAt(a, victim, k.Time);
                    k.Step(Dt, k.Time + Dt);
                }
                StepN(k, 3);
                Expect(!Alive(k, victim) && k.EmgBusStats.PingsSent >= 1 && k.EmgBusStats.PingsDelivered == 0,
                    $"负向：被夺控机器击杀己方机器（N30 击杀回传，不带 hij.seized）→ 不向玩家中继组送达（送达 {k.EmgBusStats.PingsDelivered}、广播 {k.EmgBusStats.PingsSent}）");
            }
        }

        // ── F. 反夺控（N41）──

        private static void CheckFailsafe()
        {
            Line("  · F. 反夺控：被夺控 → 按原阵营向中继组广播 0 号频道、装中继应答的友机开火；接入中槽位被封锁同样广播；没被夺控 / evt.release / 普通命中都不广播");
            // F1 被夺控
            using (var site = NewSite("f-seize"))
            {
                CombatKernel k = site.Kernel;
                int a = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_failsafe" }, out EmgProgramResult pr));
                int b = Listener(k, new double2(4, 0));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 8), -1, 5000f, CombatBehavior.HoldFire);
                // 负向：普通战斗（命中 / 击中）不广播
                for (int s = 0; s < 120; s++)
                {
                    k.FireAt(a, foe, k.Time);
                    k.Step(Dt, k.Time + Dt);
                }
                long idle = k.EmgBusStats.PingsSent;
                EmgSeizeResult r = k.Seize(a, 45f, 0, 1);
                StepN(k, 3);
                bool rec = FindPing(k, 0, out EmgChainRec ping);
                string tree = TreeText(pr);
                Expect(idle == 0 && r == EmgSeizeResult.Ok && k.EmgBusStats.PingsSent == 1 && k.EmgBusStats.PingsDelivered == 1 && k.EmgBusStats.SubSpawned >= 1 && rec && ping.SrcUnit == a && ping.Other == b
                       && View(k, a).Faction == CombatFaction.Hostile && tree.Contains(GameText.Format("emg.tree.tag.ping_send", 0)),
                    $"[反夺控] 被夺控（此刻敌对阵营）→ 广播 {k.EmgBusStats.PingsSent} 次、按原阵营送达同组友机 {k.EmgBusStats.PingsDelivered} 次（B 的中继应答放 {k.EmgBusStats.SubSpawned} 份）；" +
                    $"链“信号脉冲：A 广播 0 号频道 → B”；夺控前普通开火 2 秒广播 {idle} 次；预览“{tree}”");
                // evt.release（夺控结束）不触发
                long before = k.EmgBusStats.PingsSent;
                k.ReleaseSeize(a);
                StepN(k, 3);
                Expect(k.EmgBusStats.PingsSent == before, $"负向：夺控结束（evt.release）不广播（{before} → {k.EmgBusStats.PingsSent}）");
            }
            // F2 槽位封锁：接入中的机器收到封锁注入的 evt.seize → 广播；F3 没被夺控、也没在接入的机器收到 evt.seize（hij.seized 不成立）→ 不广播
            foreach (bool possessed in new[] { true, false })
            {
                using var site = NewSite("f-slot-" + possessed);
                CombatKernel k = site.Kernel;
                int a = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_failsafe" }, out _));
                Listener(k, new double2(4, 0));
                Unit(k, CombatFaction.Hostile, new double2(30, 30), -1, 500f, CombatBehavior.HoldFire);
                k.SetFlag(a, CombatUnitFlags.Possessed, possessed);
                EmgInjectResult inj = k.InjectEmgUnitEvent(EmgEventKind.Seize, a, (ushort)SignalSeizeService.SourceRoot, 1);
                StepN(k, 3);
                Expect(inj == EmgInjectResult.Enqueued && k.EmgBusStats.PingsSent == (possessed ? 1 : 0) && k.EmgBusStats.PingsDelivered == (possessed ? 1 : 0),
                    possessed
                        ? $"接入中的机器第 1 槽被封锁（注入 evt.seize，Other = 槽号 + 1）→ hij.seized 成立，广播 {k.EmgBusStats.PingsSent} 次、送达 {k.EmgBusStats.PingsDelivered} 次"
                        : $"负向：没被夺控、也不在接入的机器收到 evt.seize → hij.seized 不成立，不广播（{k.EmgBusStats.PingsSent}）");
            }
        }

        // ── G. 存读档 / 暂停 / 倍速 ──

        private static void CheckSaveSpeed()
        {
            Line("  · G. 劫持中继中途存读档；暂停 / 0.5x～3x；旧存档读入新固件不凭空进库存");
            using (var site = NewSite("g-save"))
            {
                CombatKernel a = site.Kernel;
                int wi = MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot", "fw_hijackrelay" }, out _);
                int me = Machine(a, new double2(0, 0), wi);
                int foe = Unit(a, CombatFaction.Hostile, new double2(0, 2), a.AddWeapon(Instant(5f, 12f, -1)), 5000f);
                Unit(a, CombatFaction.Hostile, new double2(0, 7), -1, 5000f, CombatBehavior.HoldFire);
                HijackByHits(a, me, foe, out _);
                a.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                StepN(a, 120);
                Drain(a);
                byte[] snap = a.Serialize();
                using var site2 = NewSite("g-load");
                CombatKernel b = site2.Kernel;
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && a.StateHash() == b.StateHash() && b.HijackFlashed(foe) && View(b, foe).Weapon == View(a, foe).Weapon && b.FlashUsed(me) == a.FlashUsed(me);
                int stepsA = 0, stepsB = 0;
                for (int s = 0; s < 2400 && (Alive(a, foe) || Alive(b, foe)); s++)
                {
                    if (Alive(a, foe)) { a.Step(Dt, a.Time + Dt); stepsA++; }
                    if (Alive(b, foe)) { b.Step(Dt, b.Time + Dt); stepsB++; }
                }
                Expect(same && stepsA == stepsB && a.StateHash() == b.StateHash(),
                    $"劫持中继中途存档（格式 {CombatKernel.PeekFormat(snap)}）→ 新内核读档：StateHash 一致、仍是改刷状态、武器与带宽占用一致；续跑在同一步（{stepsA} / {stepsB}）期满、终局哈希一致");
            }
            var lived = new List<string>();
            bool ok = true;
            foreach (float mul in new[] { 0.5f, 1f, 2f, 3f })
            {
                using var site = NewSite("g-speed-" + mul);
                CombatKernel k = site.Kernel;
                int me = Machine(k, new double2(0, 0), MachineWeapon(site, ComponentCatalog.CompGunId, new[] { "fw_reboot", "fw_hijackrelay" }, out _));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), k.AddWeapon(Instant(5f, 12f, -1)), 5000f);
                HijackByHits(k, me, foe, out _);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                double t0 = k.Time;
                k.TryGetHijack(foe, out _, out float r0, out _);
                for (int i = 0; i < 30; i++)
                {
                    k.Step(0f, k.Time);
                }
                k.TryGetHijack(foe, out _, out float rp, out _);
                bool flashed = k.HijackFlashed(foe);
                for (int s = 0; s < 4000 && Alive(k, foe); s++)
                {
                    k.Step(Dt * mul, k.Time + Dt * mul);
                }
                double t = k.Time - t0;
                bool good = flashed && Mathf.Approximately(r0, rp) && !Alive(k, foe) && t > 29.4 && t < 30.6;
                ok &= good;
                lived.Add($"{mul}x：{t:0.##}s{(good ? string.Empty : "（异常）")}");
            }
            Expect(ok, $"劫持中继：暂停时剩余时间不动；0.5x～3x 倍速下都按 30 游戏秒期满（{string.Join("、", lived)}）");
            // 旧存档：新建战役存档 → 读档，3 条新固件不在库存 / 已解锁里
            try
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                CampaignState s = CampaignState.CreateNew("emg4fw01-g", "Standard", 2);
                CampaignSession.Set(Slot, s);
                PrimitiveInventory.EnsureSeeded(s);
                SignalCoreService.EnsureInitialized(s);
                CampaignFgStateDomains.EnsureAll(s);
                int chips = s.PrimitiveChips.Length;
                SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
                LoadResult loaded = CampaignSaveService.Load(Slot);
                string[] ids = Spec.Select(x => x.Id).ToArray();
                bool clean = loaded.Success && loaded.State.PrimitiveChips.Length == chips
                             && !loaded.State.PrimitiveChips.Any(c => ids.Contains(c.CardDefId)) && !ids.Any(id => MechanicalContentUnlock.IsUnlocked(loaded.State, id));
                Expect(saved.Success && clean, $"负向：存档 → 读档（emg.stage = 4）：芯片 {chips} 枚原样、3 条新固件没有凭空进库存或解锁");
            }
            finally
            {
                CampaignSession.Clear();
            }
        }

        // ── 夹具 ──

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        private static CombatSite NewSite(string tag, int bandwidth = 0)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            if (bandwidth > 0)
            {
                c.EmgFlashBandwidth = bandwidth;
            }
            return new CombatSite("emg4fw-" + tag, c);
        }

        /// <summary>正常装配路径：搬运轮式 + 作战组件 <paramref name="comp"/>，接入口装 <paramref name="fws"/>（配额 2）→ 预览 → 地点武器结算（程序登记、刷写变体）。返回武器下标。</summary>
        private static int MachineWeapon(CombatSite site, string comp, string[] fws, out EmgProgramResult program, string[] local = null)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, comp, null, null, local ?? Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = site.WithProgram(p, CombatSite.MachineWeaponFrom(p), null, out program, out _);
            return site.WeaponIndex(w);
        }

        private static int Machine(CombatKernel k, double2 p, int weapon) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live | CombatUnitFlags.Report,
            Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = 2000f, MaxHealth = 2000f, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
        });

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatBehavior behavior = CombatBehavior.None) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = f, Behavior = behavior, Flags = Live | CombatUnitFlags.Report, Position = p, Home = p, Radius = 0.5f, Speed = 4f,
            Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
        });

        /// <summary>同一中继组里的接收方：装中继模块、武器程序 = 正式固件 [中继应答]（收听 0 号频道）。</summary>
        private static int Listener(CombatKernel k, double2 p)
        {
            int prog = CombatSite.RegisterProgram(k, P("fw_pingfire"));
            int b = Machine(k, p, k.AddWeapon(Instant(1f, 30f, prog)));
            k.SetUnitRelay(b, true);
            return b;
        }

        private static CombatWeapon Instant(float damage, float range, int program) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0.5f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        /// <summary>
        /// 正常击毁路径：机器一直对 foe 出手；入侵度过半后把 foe 的血量压到 1（模拟“第二发打死”），下一发命中先写入侵度、再致死 → 阵亡统一入口判劫持。
        /// 返回 foe 是否被劫持（最多 10 秒）；<paramref name="maxHack"/> = 过程中入侵度最大值。
        /// </summary>
        private static bool HijackByHits(CombatKernel k, int me, int foe, out float maxHack, List<CombatEvent> ev = null)
        {
            maxHack = 0f;
            bool low = false;
            for (int s = 0; s < 600; s++)
            {
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                if (ev != null)
                {
                    ev.AddRange(Drain(k));
                }
                if (!Alive(k, foe) || View(k, foe).Behavior == CombatBehavior.Hijacked)
                {
                    return Alive(k, foe) && View(k, foe).Behavior == CombatBehavior.Hijacked;
                }
                float acc = k.AccumOf(foe, EmgPhaseConst.Hack);
                maxHack = Math.Max(maxHack, acc);
                if (!low && acc >= 0.5f)
                {
                    low = true;
                    k.SetHealth(foe, 1f, View(k, foe).MaxHealth, true);
                }
            }
            return false;
        }

        private static EmgProgramResult P(params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, 0);

        private static string TreeText(EmgProgramResult r)
        {
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows);
            return string.Join(" | ", rows.Select(x => x.Text));
        }

        private static bool FindPing(CombatKernel k, int channel, out EmgChainRec rec)
        {
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out rec) && rec.Kind == EmgChainNode.Relay && rec.Aux == EmgSquadConst.AuxPing + channel)
                {
                    return true;
                }
            }
            rec = default;
            return false;
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            var list = new List<CombatEvent>();
            for (int guard = 0; guard < 100; guard++)
            {
                CombatEvent[] buf = k.DrainGameplay(4096, out int n);
                if (n == 0)
                {
                    break;
                }
                for (int i = 0; i < n; i++)
                {
                    list.Add(buf[i]);
                }
            }
            return list;
        }

        private static CombatUnitView View(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v : default;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static int Count(List<CombatEvent> ev, CombatEventKind kind, int unit) => ev.Count(e => e.Kind == kind && e.Unit == unit);

        private static string Detail(List<string> bad) => bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad.Take(8));

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string s) => _report.AppendLine(s);
    }
}
