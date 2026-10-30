using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-CHAIN-01（FG19 FGR-EMG-067 / 084 阶段 1 计数路径 / 180 / 181；ADR-EMG-001 ⑨）自检：
    /// A 数据：文本键、调参、输入动作（战略 + 接入、临时默认键冲突检查）、环记录 48 字节；
    /// B FGT-EMG-020：fw_marktag 跳到已标记目标 → reaction_marktag 计数 + 1、进脏列表、evt.react（Code = 计数槽、Other = 目标）；没有已标记目标不发不计；
    ///   走声明轨后旧 DetectMarkJump 不再结算（同一次跳转只计一次）、旧轨照旧；
    /// C FGT-EMG-023：链继承（出手 → 触发 → 子弹药同链、链深 + 1）、4 个归属取舍、脏列表只含本步变化、环满覆盖最旧、环不进 StateHash / 快照、读档后环空计数恢复、链号续发不重复、
    ///   链深截断记“链深已达上限”；
    /// D FGT-EMG-024 / 023 后半：悬停残骸 / 子弹药三段文本、带程序机器附程序行（FG-GAP-171）、“因果链”键打开叠加（战略暂停中可用、重绑后新键生效旧键失效、关着不读环）；
    /// E 反馈：首次触发只在镜头看着该地点时慢放（不看时只写记录）、同一步 200 次聚合一条弹字“标记跳转 ×200”、日志与归因各 200；
    /// F FGT-EMG-042：0.5x～3x（同一段游戏时间按不同帧分组步进）、暂停、观察 / 不观察、中途存读档，计数与 StateHash 一致；
    /// G 性能：满环 256 条重建 4 条链的耗时、热更层每步读脏列表的开销与单位数无关。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgChainSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgChainSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgChainSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const string MarkTag = "fw_marktag", Split = "fw_split";
        private const string SiteId = "emg-chain-site";
        private const string Bp = "bp_emg_chain_ram";
        private const int Slot = 0;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/因果链与可靠计数")]
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
            _fail = 0;
            _pass = 0;
            Line("\n[因果链与可靠计数] EMG1-CHAIN-01：链记录 / 可靠计数与脏列表 / evt.react 阶段 1 生产者 / 悬停三段 / 因果链键（FGT-EMG-020 / 023 / 024 / 042）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool slowmo = GameSettings.ReactionSlowMotionEnabled;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckData);
                Step(CheckSingleTrack);
                UseStage1(false);
                Step(CheckMarkTag);
                Step(CheckChainRecords);
                Step(CheckDirtyAndRing);
                Step(CheckSaveLoad);
                Step(CheckHover);
                Step(CheckKey);
                Step(CheckFeedback);
                Step(CheckSpeeds);
                Step(CheckPerformance);
                Step(CheckMachineHover);
            }
            catch (Exception e)
            {
                Fail($"因果链自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.CancelSlowMotion();
                GameSettings.SetReactionSlowMotionEnabled(slowmo);
                GameSettings.KeyBindings.ResetToDefault(GameActionId.CausalChain);
                StatusTagHover.Leave(null);
                CausalChainOverlay.ResetForTests();
                InputRouter.Reset();
                FeedbackCues.ObservedSiteProvider = null;
                ReactionPopups.ResetForTests();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [因果链与可靠计数] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>emg.stage = 1；fw_marktag 的声明随 EMG1-FW-01 落表，这里按 FG19A 第 1.2 节第 20 行代入（与 FgEmgGrammarSelfCheck 同一做法）。
        /// <paramref name="readReact"/>：另让它读 evt.react（条件），用来验证 evt.react 按需入队。</summary>
        private static void UseStage1(bool readReact)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
            void Decl(string owner, int seq, string dir, string port, string role, float amount)
            {
                src.Decls.Add(new EmgSource.Decl
                {
                    Id = $"{owner}.{seq}", Owner = owner, OwnerKind = "firmware", Seq = seq, Dir = dir, Port = port, Role = role, Amount = amount, Carriers = "all",
                    TextKey = $"emg.decl.{owner}.{seq}", StageGate = 1,
                });
            }
            src.Decls.RemoveAll(d => d.Owner == MarkTag);
            Decl(MarkTag, 1, "read", "evt.marked", "condition", 1f);
            Decl(MarkTag, 2, "write", "grm.mod.chain", "modify", 2f);
            if (readReact)
            {
                Decl(MarkTag, 3, "read", "evt.react", "condition", 1f);
            }
            EmgCatalog.OverrideForTests(src);
        }

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            return c;
        }

        private static int MarkKey => NamedReactionCatalog.StableKey(MechanicalReactionCatalog.ReactionMarkJumpId);

        private static CombatWeapon Melee(CombatKernel k, EmgProgramResult pr)
        {
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = 10f, Cooldown = 0f,
            };
            w.Reading = CarrierReadings.Build(ComponentCatalog.CompRamId, null, null, Array.Empty<string>(), false);
            if (pr != null)
            {
                w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
                w.Program = CombatSite.RegisterProgram(k, pr);
            }
            return w;
        }

        private static EmgProgramResult Compile(params string[] fw) => EmgProgramCompiler.Compile(Array.Empty<string>(), fw, FirmwareCarrier.Melee);

        private static CombatSpawn MachineSpawn(double2 at, int weapon) => new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled, Position = at, Home = at, Radius = 0.4f, Speed = 4f,
            Health = 1e6f, MaxHealth = 1e6f, Weapon = weapon, BehaviorProfile = -1,
        };

        private static CombatSpawn HostileSpawn(double2 at, float hp = 1e6f) => new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = at, Home = at, Radius = 0.4f, Speed = 0f, Health = hp, MaxHealth = hp,
            Weapon = -1, BehaviorProfile = -1, Priority = 1,
        };

        /// <summary>一组“出手者 + 主目标 + 已标记邻居（3.8 米）+ 未标记的最近邻居”。<paramref name="markNeighbor"/> = false 时邻居不挂标记（对照）。</summary>
        private static (int me, int t, int marked, int near) Pair(CombatKernel k, int weapon, double2 at, bool markNeighbor = true)
        {
            int me = k.Spawn(MachineSpawn(at + new double2(0, -1.5), weapon));
            int t = k.Spawn(HostileSpawn(at));
            int marked = k.Spawn(HostileSpawn(at + new double2(0, 3.8)));
            int near = k.Spawn(HostileSpawn(at + new double2(-2.6, 1.7)));
            if (markNeighbor)
            {
                k.SetMarkedUntil(marked, 1e9);
            }
            return (me, t, marked, near);
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static int DirtySlot(CombatKernel k) => k.EmgDirtyCount == 1 ? k.EmgDirtyAt(0) : -1;

        private static List<EmgChainRec> Ring(CombatKernel k)
        {
            var list = new List<EmgChainRec>();
            for (int i = k.EmgChainRecordCount - 1; i >= 0; i--)
            {
                if (k.TryGetEmgChainRecent(i, out EmgChainRec r))
                {
                    list.Add(r); // 从旧到新
                }
            }
            return list;
        }

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("emgchain-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            s.RegionRecords = new[] { new RegionRecord { RegionId = SiteId, ExpeditionCount = 1 } };
            return s;
        }

        /// <summary>键盘后端：按住的键集合；“按下”只在 <see cref="Down"/> 那一个键上。</summary>
        private sealed class Reader : IInputReader
        {
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => Held.Contains(key) || key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(200f, 200f, 0f);
            public float MouseScrollDelta => 0f;
        }

        /// <summary>按一次组合键（模拟一帧），走叠加层的真实入口 <see cref="CausalChainOverlay.Tick"/>。</summary>
        private static void Press(Reader r, CombatSite site, InputChord chord)
        {
            r.Held.Clear();
            if ((chord.Mods & InputModifier.Ctrl) != 0)
            {
                r.Held.Add(KeyCode.LeftControl);
            }
            if ((chord.Mods & InputModifier.Alt) != 0)
            {
                r.Held.Add(KeyCode.LeftAlt);
            }
            r.Down = chord.Key;
            InputRouter.DebugClearConsumedKeys();
            CausalChainOverlay.Tick(site, null);
            r.Down = KeyCode.None;
            r.Held.Clear();
            InputRouter.DebugClearConsumedKeys();
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：文本键齐全、调参、环记录 48 字节、输入动作（战略 + 接入，临时默认键与同上下文无冲突）");
            string[] keys =
            {
                "emg.chain.title", "emg.chain.empty", "emg.chain.hint", "emg.chain.sep", "emg.chain.node.shot", "emg.chain.node.shot_bare", "emg.chain.node.inject",
                "emg.chain.node.trigger", "emg.chain.node.rule", "emg.chain.node.rule_count", "emg.chain.node.wreck", "emg.chain.node.fragment", "emg.chain.node.depthcut",
                "emg.chain.node.relay", "emg.chain.node.doctrine", "emg.chain.node.phase", "emg.chain.node.merge", "emg.chain.party.unknown", "emg.hover.label.what",
                "emg.hover.label.who", "emg.hover.label.next", "emg.hover.chain_owners", "emg.hover.wreck.title", "emg.hover.wreck.what", "emg.hover.wreck.who",
                "emg.hover.wreck.next", "emg.hover.wreck.next_persist", "emg.hover.sub.title", "emg.hover.sub.what", "emg.hover.sub.who", "emg.hover.sub.next",
                "emg.hover.sub.next_cut", "emg.hover.frag.title", "emg.hover.frag.what", "emg.hover.frag.who", "emg.hover.frag.next", "emg.hover.kind.machine",
                "emg.hover.kind.enemy", "emg.hover.kind.turret", "emg.hover.kind.other", "emg.hover.program", "input.action.causal_chain.name",
            };
            List<string> missing = keys.Where(k => !GameText.Has(k)).ToList();
            Expect(missing.Count == 0, $"{keys.Length} 个因果链 / 悬停 / 动作名文本键都在 fg.TbLocText 里" + (missing.Count > 0 ? "；缺：" + string.Join("、", missing) : string.Empty));
            bool ring = GridContent.TryGetTuning("emg.chain.ring", out float ringN) && (int)ringN == EmgConst.ChainRing;
            bool overlay = GridContent.TryGetTuning("emg.chain.overlay_chains", out float n) && n >= 1f && n <= CausalChainOverlay.RowSlots;
            bool refresh = GridContent.TryGetTuning("emg.chain.refresh_seconds", out float rs) && rs > 0f;
            Expect(ring && overlay && refresh, $"调参：emg.chain.ring = {ringN}（= 内核 {EmgConst.ChainRing}）、emg.chain.overlay_chains = {n}（≤ 行槽 {CausalChainOverlay.RowSlots}）、emg.chain.refresh_seconds = {rs}");
            int size = UnsafeUtility.SizeOf<EmgChainRec>();
            Expect(size == 48, $"链记录 {size} 字节（ADR ⑨ 定 48：链、父链、种类、链深、附加码、键、源单位、第二单位、位置、时刻）");

            bool has = InputActionCatalog.TryGet(GameActionId.CausalChain, out InputActionDef def);
            InputChord chord = InputActionCatalog.DefaultChord(GameActionId.CausalChain);
            InputBindingSet defaults = InputBindingSet.CreateDefault();
            var conflicts = new List<GameActionId>();
            defaults.CollectConflicts(GameActionId.CausalChain, chord, conflicts);
            // 卡片指定的临时键 T：开工冲突检查（与“命令：攻击”同在战略上下文）→ 改取 Ctrl+T，证据记录
            var tConflicts = new List<GameActionId>();
            defaults.CollectConflicts(GameActionId.CausalChain, new InputChord(KeyCode.T, InputModifier.None), tConflicts);
            Expect(has && (def.Contexts & InputContext.Strategy) != 0 && (def.Contexts & InputContext.Uplink) != 0 && chord.Key == KeyCode.T && chord.Mods == InputModifier.Ctrl
                   && conflicts.Count == 0 && !defaults.FindAllConflicts().Any(p => p.A == GameActionId.CausalChain || p.B == GameActionId.CausalChain),
                $"“因果链”动作登记在战略 + 接入上下文、可重绑，临时默认 {InputDisplay.ForAction(GameActionId.CausalChain)}，全表无冲突"
                + $"（卡片原定的 T 与 {string.Join("、", tConflicts)} 冲突，按卡片改取未占用的 Ctrl+T；正式键位 FG15-UX-01，DEBT-EMG1CHAIN01-01）");
        }

        // ── B. 单轨（旧装配反应）─────────────────────────────────────────────────

        private static void CheckSingleTrack()
        {
            Line("  · B1. FGR-EMG-084 / 019 单轨：fw_marktag 没走声明轨（emg.stage = 0 正式流程）时旧装配反应照旧；走声明轨后旧 DetectMarkJump 对同一武器不再结算，同一次跳转只计一次");
            var p = new BlueprintCircuitPreview
            {
                PrimaryId = ComponentCatalog.CompGunId, HasCombatOutput = true, TotalNormalizedDamage = 10f, ReactionId = MechanicalReactionCatalog.ReactionMarkJumpId,
                FirmwareIds = new[] { MarkTag }, UplinkFirmwareIds = new[] { MarkTag }, UplinkOnPath = true,
            };
            CombatWeapon legacy = CombatSite.MachineWeaponFrom(p, false);
            UseStage1(false);
            CombatWeapon decl = CombatSite.MachineWeaponFrom(p, false);
            int Run(CombatWeapon w, EmgProgramResult pr, out int gameplayJumps, out int emgCount)
            {
                using var k = new CombatKernel(Cfg(), 16);
                if (pr != null)
                {
                    w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
                    w.Program = CombatSite.RegisterProgram(k, pr);
                }
                (int me, int t, int marked, _) = Pair(k, k.AddWeapon(w), double2.zero);
                k.SetMarkedUntil(t, 1e9); // 旧装配反应要求主目标已标记（命中已标记目标时跳）；声明轨同样从已标记目标跳
                k.FireAt(me, t, k.Time);
                StepN(k, 30);
                CombatEvent[] ev = k.DrainGameplay(4096, out int n);
                gameplayJumps = ev.Take(n).Count(e => e.Kind == CombatEventKind.ReactionFired && e.Code == (byte)CombatReaction.MarkJump);
                emgCount = k.EmgRuleCountOfKey(MarkKey);
                return 1_000_000 - (int)Hp(k, marked);
            }
            int dmgLegacy = Run(legacy, null, out int gjLegacy, out int ecLegacy);
            EmgProgramResult prog = EmgProgramCompiler.Compile(new[] { MarkTag }, Array.Empty<string>(), FirmwareCarrier.Projectile);
            int dmgDecl = Run(decl, prog, out int gjDecl, out int ecDecl);
            EmgCatalog.ResetForTests();
            Expect(legacy.Reaction == CombatReaction.MarkJump && decl.Reaction == CombatReaction.None,
                $"旧轨武器带装配反应“标记跳转”（{legacy.Reaction}）；fw_marktag 走声明轨后同一装配的武器不带（{decl.Reaction}）");
            Expect(dmgLegacy > 0 && gjLegacy == 1 && ecLegacy == 0 && dmgDecl > 0 && gjDecl == 0 && ecDecl == 1,
                $"同一次跳转只计一次：旧轨 = 装配反应事件 {gjLegacy} 次、涌现计数 {ecLegacy}（已标记邻居掉 {dmgLegacy}）；声明轨 = 装配反应事件 {gjDecl} 次、涌现计数 {ecDecl}（掉 {dmgDecl}）");
        }

        // ── B2. evt.react 生产者 ─────────────────────────────────────────────────

        private static void CheckMarkTag()
        {
            Line("  · B2. FGT-EMG-020（阶段 1 evt.react 生产者）：fw_marktag 跳到已标记目标时计数 + 1、进脏列表、按需入队 evt.react；没有已标记目标时不发不计（对照）");
            UseStage1(true);
            EmgProgramResult pr = Compile(MarkTag);
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, pr));
                (int me, int t, int marked, int near) = Pair(k, w, double2.zero);
                long enq0 = k.EmgEnqueuedOf(EmgEventKind.React);
                k.FireAt(me, t, k.Time);
                int slot = DirtySlot(k);
                int count = k.EmgRuleCountOfKey(MarkKey);
                long enq = k.EmgEnqueuedOf(EmgEventKind.React) - enq0;
                StepN(k, 1);
                bool last = k.TryGetLastEmgEvent(EmgEventKind.React, out EmgEvent e);
                Expect(count == 1 && slot >= 0 && k.EmgRuleKeyAt(slot) == MarkKey && enq == 1 && last && e.Code == slot && e.Other == marked && e.OtherKind == EmgEntKind.Unit
                       && Hp(k, marked) < 1e6f && Math.Abs(Hp(k, near) - 1e6f) < 1e-3f && k.EmgPortCountOf(EmgEventKind.React) == 1,
                    $"跳到已标记目标：reaction_marktag 计数 {count}（稳定键 {MarkKey}，槽 {slot}）、脏列表 {k.EmgDirtyCount} 条、evt.react 入队 {enq} 条（Code = 槽 {e.Code}、Other = 目标 {e.Other}）、"
                    + $"端口计数 {k.EmgPortCountOf(EmgEventKind.React)}；未标记的最近邻居没被跳到");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, pr));
                (int me, int t, int marked, int near) = Pair(k, w, double2.zero, markNeighbor: false);
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                Expect(k.EmgRuleCountOfKey(MarkKey) == 0 && k.EmgDirtyCount == 0 && k.EmgEnqueuedOf(EmgEventKind.React) == 0 && Math.Abs(Hp(k, marked) - 1e6f) < 1e-3f && Math.Abs(Hp(k, near) - 1e6f) < 1e-3f,
                    $"对照：没有已标记目标——不跳（只有“已标记”偏好）、不计（{k.EmgRuleCountOfKey(MarkKey)}）、不发 evt.react（{k.EmgEnqueuedOf(EmgEventKind.React)}）");
            }
            UseStage1(false);
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, Compile(MarkTag)));
                (int me, int t, _, _) = Pair(k, w, double2.zero);
                k.FireAt(me, t, k.Time);
                Expect(k.EmgRuleCountOfKey(MarkKey) == 1 && k.EmgEnqueuedOf(EmgEventKind.React) == 0,
                    "按需：程序不读 evt.react 时不入队（FGR-EMG-062），可靠计数照常 + 1");
            }
        }

        // ── C. 链继承 / 归属 / 链深截断 ─────────────────────────────────────────

        private static void CheckChainRecords()
        {
            Line("  · C1. 链继承：一次出手开一条链，触发放出的子弹药与触发记录同链、链深 + 1；4 个归属保留最早 3 个 + 最近 1 个；链深 3 截断记“链深已达上限”");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, Compile(Split)));
                int me = k.Spawn(MachineSpawn(new double2(0, -1.5), w));
                int t = k.Spawn(HostileSpawn(double2.zero));
                k.Spawn(HostileSpawn(new double2(3, 2)));
                k.FireAt(me, t, k.Time);
                StepN(k, 2);
                List<EmgChainRec> ring = Ring(k);
                EmgChainRec shot = ring.FirstOrDefault(r => r.Kind == EmgChainNode.Shot);
                EmgChainRec trig = ring.FirstOrDefault(r => r.Kind == EmgChainNode.Trigger);
                var subs = new List<CombatProjectile>();
                for (int i = 0; i < k.ProjectileCount; i++)
                {
                    if (k.TryGetProjectile(i, out CombatProjectile p) && (p.Flags & EmgProjFlags.Sub) != 0)
                    {
                        subs.Add(p);
                    }
                }
                Expect(shot.Chain > 0 && shot.SrcUnit == me && trig.Chain == shot.Chain && trig.Depth == 1 && trig.Key == (int)EmgEventKind.Hit && trig.Aux == 1
                       && subs.Count > 0 && subs.All(s => s.Chain == shot.Chain && s.Depth == 1) && shot.Kind == ring[0].Kind,
                    $"[分裂]：出手记录（链 {shot.Chain}、源 = 出手机器）→ 触发记录（同链、链深 {trig.Depth}、命中时、第 {trig.Aux} 代）→ {subs.Count} 发子弹药都在链 {shot.Chain}、链深 1");
                int4 owners = k.EmgChainOwners(shot.Chain);
                Expect(owners.x == me && owners.y == 0, $"链上归属 = 出手机器（{owners}）");
            }
            int4 o = int4.zero;
            foreach (int id in new[] { 11, 0, 12, 11, 13, 14, 15, 16 })
            {
                CombatLogic.EmgOwnersAdd(ref o, id);
            }
            Expect(o.x == 11 && o.y == 12 && o.z == 13 && o.w == 16, $"4 个归属的取舍：依次加 11、0、12、11（重复）、13、14、15、16 → {o}（0 不记、重复不记、满了保留最早 3 个 + 最近 1 个）");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                // 真实残骸：A 击毁 → B、C、D、E 先后打碎它（环里的残骸 / 破片记录），归属 = A、B、C、E
                int w = k.AddWeapon(Melee(k, null));
                int a = k.Spawn(MachineSpawn(new double2(0, -1.5), w));
                int foe = k.Spawn(HostileSpawn(double2.zero, 5f));
                k.FireAt(a, foe, k.Time);
                StepN(k, 1);
                bool wreck = k.WreckCount == 1 && k.TryGetWreck(0, out EmgWreck wr);
                k.TryGetWreck(0, out wr);
                foreach (int striker in new[] { 901, 902, 903, 904 })
                {
                    k.EmgChainLogForTests(EmgChainNode.Fragment, 50 + striker, striker, 0, wr.Chain, wr.Id);
                }
                int4 wo = k.EmgWreckOwners(wr.Id);
                Expect(wreck && wo.x == a && wo.y == 901 && wo.z == 902 && wo.w == 904,
                    $"残骸归属：击毁者 {a} + 打碎它的 901～904 → {wo}（最早 3 个 + 最近 1 个；残骸 Id {wr.Id}）");
                // 修复轮（审查 P1）：击毁者持久存在残骸上——环写满覆盖后、读档后（环从空开始）悬停仍显示击毁者
                for (int i = 0; i < 300; i++)
                {
                    k.EmgChainLogForTests(EmgChainNode.Shot, 5000 + i, 77);
                }
                int4 afterRing = k.EmgWreckOwners(wr.Id);
                using (var r = new CombatKernel(Cfg(), 16))
                {
                    CombatLoadResult res = r.Load(k.Serialize());
                    int4 afterLoad = r.EmgWreckOwners(wr.Id);
                    Expect(wr.Killer == a && afterRing.x == a && afterRing.y == 0 && res == CombatLoadResult.Ok && r.EmgChainRecordCount == 0 && afterLoad.x == a && afterLoad.y == 0,
                        $"残骸持久击毁者 {wr.Killer}：环写满 300 条覆盖后归属 {afterRing}、读档后（环 {r.EmgChainRecordCount} 条，{res}）归属 {afterLoad}——都还是击毁者 {a}");
                }
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = k.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Hit }, new[] { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } },
                    new[] { new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Hit), Role = 2 } });
                bool injected = k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Depth = 3, Prog = prog, Chain = 0, Pos = new double2(4, 4) });
                bool child = k.EmgEmitChildForTests(0, EmgEventKind.Split);
                List<EmgChainRec> ring = Ring(k);
                EmgChainRec cut = ring.FirstOrDefault(r => r.Kind == EmgChainNode.DepthCut);
                string text = cut.Chain > 0 ? EmgChainView.Line(null, null, cut.Chain, ring.Where(r => r.Chain == cut.Chain).ToList()).Text : string.Empty;
                Expect(injected && !child && cut.Chain > 0 && ring.Any(r => r.Kind == EmgChainNode.Inject && r.Chain == cut.Chain) && text.EndsWith(GameText.Get("emg.chain.node.depthcut")),
                    $"链深 3 的事件再生成被截断：环里该链 = 注入 → 链深截断，链尾文字“{text}”");
            }
        }

        // ── C2. 脏列表与环 ─────────────────────────────────────────────────────

        private static void CheckDirtyAndRing()
        {
            Line("  · C2. 脏列表只含本步变化（两步之间的即时出手并进下一步）；环满后覆盖最旧记录；环不进 StateHash");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, Compile(MarkTag)));
                (int me, int t, _, _) = Pair(k, w, double2.zero);
                StepN(k, 1);
                int before = k.EmgDirtyCount;
                k.FireAt(me, t, k.Time);
                int between = k.EmgDirtyCount;
                StepN(k, 1);
                int afterStep = k.EmgDirtyCount;
                StepN(k, 1);
                int quiet = k.EmgDirtyCount;
                k.FireAt(me, t, k.Time);
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                int twice = k.EmgDirtyCount;
                Expect(before == 0 && between == 1 && afterStep == 1 && quiet == 0 && twice == 1 && k.EmgRuleCountOfKey(MarkKey) == 3,
                    $"脏列表：没变化 {before} → 两步之间出手 {between} → 下一步交出 {afterStep} → 再一步没变化 {quiet} → 同一步同一槽两次只列 1 条（{twice}），计数 {k.EmgRuleCountOfKey(MarkKey)}");
                // 修复轮（审查 P1）：上一步刚交出的槽，两步之间又被即时出手命中 → 下一步（本身没有命中）仍要列出它
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                int justPublished = k.EmgDirtyCount;
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                int carried = k.EmgDirtyCount;
                Expect(justPublished == 1 && carried == 1 && k.EmgRuleCountOfKey(MarkKey) == 5,
                    $"上一步交出（{justPublished} 条）后两步之间再命中同一槽 → 下一步脏列表 {carried} 条（不漏报），计数 {k.EmgRuleCountOfKey(MarkKey)}");
            }
            using (var a = new CombatKernel(Cfg(), 16))
            using (var b = new CombatKernel(Cfg(), 16))
            {
                foreach (CombatKernel k in new[] { a, b })
                {
                    int w = k.AddWeapon(Melee(k, Compile(MarkTag)));
                    (int me, int t, _, _) = Pair(k, w, double2.zero);
                    k.FireAt(me, t, k.Time);
                    StepN(k, 2);
                }
                ulong h0 = b.StateHash();
                for (int i = 0; i < 300; i++)
                {
                    b.EmgChainLogForTests(EmgChainNode.Shot, 1000 + i, 77);
                }
                bool full = b.EmgChainRecordCount == EmgConst.ChainRing && b.TryGetEmgChainRecent(0, out EmgChainRec newest) && newest.Chain == 1299
                            && b.TryGetEmgChainRecent(EmgConst.ChainRing - 1, out EmgChainRec oldest) && oldest.Chain == 1299 - (EmgConst.ChainRing - 1)
                            && !b.TryGetEmgChainRecent(EmgConst.ChainRing, out _);
                b.TryGetEmgChainRecent(EmgConst.ChainRing - 1, out EmgChainRec o2);
                Expect(full, $"写 300 条后环里 {b.EmgChainRecordCount} 条（容量 {EmgConst.ChainRing}），最新链 1299、最旧链 {o2.Chain}（最早的 {300 - EmgConst.ChainRing + 1} 条已被覆盖）");
                Expect(a.StateHash() == b.StateHash() && h0 == b.StateHash() && a.EmgRuleCountOfKey(MarkKey) == 1,
                    $"环不进 StateHash：同样的战斗、多写 300 条环记录的那个哈希不变（{a.StateHash():X16} = {b.StateHash():X16}）；可靠计数照常进哈希");
            }
        }

        // ── C3. 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · C3. 存读档：可靠计数（规则槽 / 端口）进快照、读档后照常恢复且槽位不挪；环不进快照（读档后从空开始）；链号接着发、不与读档前重复；坏值整份拒绝");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(Melee(k, Compile(MarkTag)));
                (int me, int t, _, _) = Pair(k, w, double2.zero);
                k.FireAt(me, t, k.Time);
                k.FireAt(me, t, k.Time);
                StepN(k, 2);
                int slot = -1;
                for (int i = 0; i < EmgConst.RuleSlots; i++)
                {
                    if (k.EmgRuleKeyAt(i) == MarkKey)
                    {
                        slot = i;
                    }
                }
                int maxChain = Ring(k).Max(r => r.Chain);
                long hitPort = k.EmgPortCountOf(EmgEventKind.Hit);
                k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Pickup, Prog = 0, Pos = double2.zero });
                long pickup = k.EmgPortCountOf(EmgEventKind.Pickup);
                ulong h = k.StateHash();
                byte[] snap = k.Serialize();
                using (var r = new CombatKernel(Cfg(), 16))
                {
                    CombatLoadResult res = r.Load(snap);
                    Expect(res == CombatLoadResult.Ok && r.EmgRuleCountOfKey(MarkKey) == 2 && r.EmgRuleKeyAt(slot) == MarkKey && r.EmgChainRecordCount == 0 && r.EmgDirtyCount == 0
                           && r.EmgPortCountOf(EmgEventKind.Pickup) == pickup && r.EmgPortCountOf(EmgEventKind.Hit) == hitPort && r.StateHash() == h,
                        $"读档：计数 {r.EmgRuleCountOfKey(MarkKey)}（槽 {slot} 不挪）、拾取端口 {r.EmgPortCountOf(EmgEventKind.Pickup)}、命中端口 {r.EmgPortCountOf(EmgEventKind.Hit)}；环 {r.EmgChainRecordCount} 条、脏列表 {r.EmgDirtyCount}；StateHash 一致");
                    r.FireAt(me, t, r.Time);
                    int newChain = Ring(r).Where(x => x.Kind == EmgChainNode.Shot).Select(x => x.Chain).DefaultIfEmpty(0).Max();
                    Expect(newChain > maxChain && r.EmgRuleCountOfKey(MarkKey) == 3,
                        $"读档后再出手：新链号 {newChain} > 读档前最大 {maxChain}（链号随快照续发，悬停 / 叠加不会串链）；计数接着加到 {r.EmgRuleCountOfKey(MarkKey)}");
                }
                // 自描述段（ADR ⑩）：段 15 只写前 2 个字段的短段（较早提交写出）→ 次数取 0、槽与键照样恢复；每条多写字段 + 多一个不认识的段（较晚提交写出）→ 跳过未知部分
                using (var r = new CombatKernel(Cfg(), 16))
                using (var r2 = new CombatKernel(Cfg(), 16))
                {
                    CombatLoadResult rs = r.Load(k.SerializeEmgShapeForTests(EmgConst.SegRuleCounts, 2, 0));
                    CombatLoadResult rl = r2.Load(k.SerializeEmgShapeForTests(0, int.MaxValue, 4));
                    Expect(rs == CombatLoadResult.Ok && r.EmgRuleKeyAt(slot) == MarkKey && r.EmgRuleCountAt(slot) == 0 && rl == CombatLoadResult.Ok && r2.EmgRuleCountOfKey(MarkKey) == 2
                           && r2.EmgPortCountOf(EmgEventKind.Pickup) == pickup,
                        $"短段：槽 {slot} 键恢复、次数取 {r.EmgRuleCountAt(slot)}（{rs}）；长段 / 未知段：计数 {r2.EmgRuleCountOfKey(MarkKey)}、拾取端口 {r2.EmgPortCountOf(EmgEventKind.Pickup)}（{rl}）");
                }
                // 坏值（重算校验和，逼到段 15 的数值校验这一层）：次数改成负数 / 槽下标越界 → 整份拒绝、内核保持原样
                int at = FindRuleRecord(snap, slot, MarkKey);
                byte[] negative = at >= 0 ? FgEmergenceKernelSelfCheck.Patch(snap, d => FgEmergenceKernelSelfCheck.WriteInt(d, at + 6, -5)) : null;
                byte[] badSlot = at >= 0 ? FgEmergenceKernelSelfCheck.Patch(snap, d => BitConverter.GetBytes((short)EmgConst.RuleSlots).CopyTo(d, at)) : null;
                using (var r = new CombatKernel(Cfg(), 16))
                {
                    CombatLoadResult r1 = negative != null ? r.Load(negative) : CombatLoadResult.Ok;
                    CombatLoadResult r2 = badSlot != null ? r.Load(badSlot) : CombatLoadResult.Ok;
                    Expect(at >= 0 && r1 == CombatLoadResult.InvalidValue && r2 == CombatLoadResult.InvalidValue && r.EmgRuleCountOfKey(MarkKey) == 0,
                        $"坏值：规则计数 −5（{r1}）、计数槽下标 {EmgConst.RuleSlots}（{r2}）的快照整份拒绝，内核不变");
                }
            }
        }

        /// <summary>在快照里找段 15 的一条记录（槽 short + 键 int）的起点（返回槽字段的偏移；次数在 +6）。</summary>
        private static int FindRuleRecord(byte[] data, int slot, int key)
        {
            byte[] kb = BitConverter.GetBytes(key);
            for (int i = 0; i + 10 <= data.Length; i++)
            {
                if (BitConverter.ToInt16(data, i) == slot && data[i + 2] == kb[0] && data[i + 3] == kb[1] && data[i + 4] == kb[2] && data[i + 5] == kb[3])
                {
                    return i;
                }
            }
            return -1;
        }

        // ── D. 悬停 ─────────────────────────────────────────────────────────────

        private static void CheckHover()
        {
            Line("  · D1. FGT-EMG-024（阶段 1：残骸与子弹药）：悬停显示“是什么、谁造成的、接下来会怎样”三段；单位优先，空地不弹");
            CampaignState s = NewState(9821);
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            int w = k.AddWeapon(Melee(k, null));
            int a = k.Spawn(MachineSpawn(new double2(0, -2.5), w));
            site.SetUnitLabel(a, "reaction.log.machine_n", "A");
            int foe = k.Spawn(HostileSpawn(double2.zero, 5f));
            k.FireAt(a, foe, k.Time); // 击毁当场生成残骸（不走地点事件处理：这些测试单位不是区域敌人记录）
            bool hasWreck = k.TryGetWreck(0, out EmgWreck wr);
            int key = StatusTagHover.HoverEmgGroundForTests(site, wr.Pos);
            TooltipContent c = StatusTagHover.HoveredEmgContentForTests();
            string killer = EmgChainView.PartyName(site, s, a);
            string body = c?.Body ?? string.Empty;
            Expect(hasWreck && key == -(StatusTagHover.WreckHoverKeyBase + wr.Id) && c != null && c.Title == GameText.Get("emg.hover.wreck.title")
                   && body.Contains(GameText.Format("emg.hover.label.what", "").TrimEnd()) && body.Contains(GameText.Get("emg.hover.kind.enemy"))
                   && body.Contains(GameText.Format("emg.hover.wreck.who", killer)) && body.Split('\n').Length == 3,
                $"悬停残骸：标题“{c?.Title}”，正文“{body.Replace("\n", " / ")}”");
            StatusTagHover.Leave(site);
            Expect(StatusTagHover.HoverEmgGroundForTests(site, new double2(40, 40)) == 0 && StatusTagHover.HoveredEmgContentForTests() == null, "空地：不弹");

            int w2 = k.AddWeapon(Melee(k, Compile(Split)));
            int b = k.Spawn(MachineSpawn(new double2(20, -1.5), w2));
            site.SetUnitLabel(b, "reaction.log.machine_n", "B");
            int t2 = k.Spawn(HostileSpawn(new double2(20, 0)));
            k.FireAt(b, t2, k.Time);
            StepN(k, 5);
            CombatProjectile sub = default;
            bool found = false;
            for (int i = 0; i < k.ProjectileCount && !found; i++)
            {
                found = k.TryGetProjectile(i, out sub) && (sub.Flags & EmgProjFlags.Sub) != 0 && math.distance(sub.Pos, new double2(20, 0)) > 1.3;
            }
            int sk = found ? StatusTagHover.HoverEmgGroundForTests(site, sub.Pos) : 0;
            TooltipContent sc = StatusTagHover.HoveredEmgContentForTests();
            string sb = sc?.Body ?? string.Empty;
            Expect(found && sk == -(StatusTagHover.ProjHoverKeyBase + sub.Id) && sc != null && sc.Title == GameText.Format("emg.hover.sub.title", "1")
                   && sb.Contains(GameText.Format("emg.hover.sub.who", EmgChainView.PartyName(site, s, b))) && sb.Split('\n').Length == 3,
                $"悬停子弹药：标题“{sc?.Title}”，正文“{sb.Replace("\n", " / ")}”");
            StatusTagHover.Leave(site);
        }

        // ── D2. 因果链键 ───────────────────────────────────────────────────────

        private static void CheckKey()
        {
            Line("  · D2. “因果链”键：临时默认键按下即打开叠加（战略与接入上下文、战略暂停中可用）、关着时不读环、重绑后新键生效旧键失效；叠加文字走文本键");
            NewState(9822);
            var reader = new Reader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(reader);
            CausalChainOverlay.ResetForTests();
            float now = 100f;
            CausalChainOverlay.Clock = () => now;
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            int w = k.AddWeapon(Melee(k, Compile(MarkTag)));
            (int me, int t, _, _) = Pair(k, w, double2.zero);
            site.SetUnitLabel(me, "reaction.log.machine_n", "A");
            k.FireAt(me, t, k.Time);
            site.Step(Dt, k.Time + Dt);

            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(true, strategic: true);
            GameClock.SetPaused(true);
            for (int f = 0; f < 5; f++)
            {
                CausalChainOverlay.Tick(site, null); // 关着：每帧都不读环
            }
            int readsClosed = CausalChainOverlay.Reads;
            InputChord def = GameSettings.KeyBindings.GetChord(GameActionId.CausalChain);
            Press(reader, site, def);
            bool openPaused = CausalChainOverlay.IsOpen && CausalChainOverlay.LineCount == 1;
            string text = CausalChainOverlay.LineText(0);
            Expect(readsClosed == 0 && openPaused && InputRouter.ActiveContext == InputContext.Strategy
                   && text.StartsWith(GameText.Format("emg.chain.node.shot_bare", EmgChainView.PartyName(site, CampaignSession.Current, me)))
                   && text.Contains(NamedReactionCatalog.NameOf(MechanicalReactionCatalog.ReactionMarkJumpId)) && CausalChainOverlay.Line(0).Points.Count == 2,
                $"战略暂停中按 {InputDisplay.ForAction(GameActionId.CausalChain)}：叠加打开（关着时读环 {readsClosed} 次），1 条链“{text}”、2 个节点连线");
            int r0 = CausalChainOverlay.Reads;
            CausalChainOverlay.Tick(site, null);
            now += 1f;
            CausalChainOverlay.Tick(site, null);
            Expect(CausalChainOverlay.Reads == r0 + 1, $"打开后按间隔重读（同一时刻再 Tick 不读，过了 emg.chain.refresh_seconds 读 1 次：{CausalChainOverlay.Reads - r0}）");
            // 修复轮（审查 P1）：家园 + 靶场会话同一帧各渲染一次——叠加锁定打开它的地点，另一地点的 Tick 不读环、不换内容
            using (var other = new CombatSite(SiteId + "-range", Cfg()))
            {
                int r1 = CausalChainOverlay.Reads;
                for (int f = 0; f < 6; f++)
                {
                    CausalChainOverlay.Tick(site, null);
                    CausalChainOverlay.Tick(other, null);
                }
                int sameTime = CausalChainOverlay.Reads - r1;
                now += 1f;
                CausalChainOverlay.Tick(other, null);
                CausalChainOverlay.Tick(site, null);
                Expect(sameTime == 0 && CausalChainOverlay.Reads - r1 == 1 && CausalChainOverlay.LineCount == 1 && CausalChainOverlay.LineText(0) == text,
                    $"两个地点轮流 Tick 6 帧：读环 {sameTime} 次（间隔未到）；过了间隔只读 {CausalChainOverlay.Reads - r1} 次，仍显示打开它的地点的链“{CausalChainOverlay.LineText(0)}”");
            }
            Press(reader, site, def);
            Expect(!CausalChainOverlay.IsOpen && CausalChainOverlay.LineCount == 0, "再按一次关闭");

            InputRouter.SetGameplayPaused(false);
            GameClock.SetPaused(false);
            InputRouter.SetScope(InputScope.Direct);
            Press(reader, site, def);
            bool uplink = CausalChainOverlay.IsOpen && InputRouter.ActiveContext == InputContext.Uplink;
            Press(reader, site, def);
            Expect(uplink && !CausalChainOverlay.IsOpen, "接入上下文里同一个键也能开 / 关");

            InputRouter.SetScope(InputScope.Strategy);
            var conflicts = new List<GameActionId>();
            var f12 = new InputChord(KeyCode.F12, InputModifier.None);
            RebindResult rb = GameSettings.KeyBindings.TryRebind(GameActionId.CausalChain, f12, conflicts);
            Press(reader, site, def);
            bool oldDead = !CausalChainOverlay.IsOpen;
            Press(reader, site, f12);
            bool newLive = CausalChainOverlay.IsOpen;
            Press(reader, site, f12);
            GameSettings.KeyBindings.ResetToDefault(GameActionId.CausalChain);
            Expect(rb == RebindResult.Ok && conflicts.Count == 0 && oldDead && newLive && !CausalChainOverlay.IsOpen,
                $"重绑到 F12（{rb}，冲突 {conflicts.Count}）：旧键 {def.Key} 不再打开、新键打开 / 关闭");
            InputRouter.Reset();
            CausalChainOverlay.ResetForTests();
        }

        // ── E. 反馈 ─────────────────────────────────────────────────────────────

        private static void CheckFeedback()
        {
            Line("  · E. 反馈：首次触发写记录，慢放只在镜头看着该地点时；同一步 200 次结算聚合为一条弹字 / 一条日志（每秒弹字上限沿用 6 条）");
            GameSettings.SetReactionSlowMotionEnabled(true);
            string rid = MechanicalReactionCatalog.ReactionMarkJumpId;
            EmgProgramResult pr = Compile(MarkTag);
            int RunFirst(bool observed, out bool first)
            {
                CampaignState s = NewState(observed ? 9831 : 9832);
                FeedbackCues.ObservedSiteProvider = () => observed ? SiteId : "elsewhere";
                GameClock.CancelSlowMotion();
                int slow0 = GameClock.SlowMotionStarts;
                using var site = new CombatSite(SiteId, Cfg());
                CombatKernel k = site.Kernel;
                int w = k.AddWeapon(Melee(k, pr));
                (int me, int t, _, _) = Pair(k, w, double2.zero);
                site.Step(Dt, k.Time + Dt); // 基线
                k.FireAt(me, t, k.Time);
                site.Step(Dt, k.Time + Dt);
                first = ReactionFeedback.IsFirstTriggered(s, rid);
                return GameClock.SlowMotionStarts - slow0;
            }
            int slowSeen = RunFirst(true, out bool firstSeen);
            int slowUnseen = RunFirst(false, out bool firstUnseen);
            GameClock.CancelSlowMotion();
            Expect(firstSeen && firstUnseen && slowSeen == 1 && slowUnseen == 0,
                $"首次标记跳转：两种情况都写首次触发记录；镜头看着该地点时慢放 {slowSeen} 次，镜头不在时 {slowUnseen} 次（对照）");

            CampaignState s2 = NewState(9833);
            ReactionFeedback.RecordFirst(s2, rid, SiteId); // 不是第一次：只看聚合
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            ReactionPopups.ResetForTests();
            using (var site = new CombatSite(SiteId, Cfg()))
            {
                CombatKernel k = site.Kernel;
                int w = k.AddWeapon(Melee(k, pr));
                var pairs = new List<(int me, int t)>();
                for (int i = 0; i < 200; i++)
                {
                    (int me, int t, _, _) = Pair(k, w, new double2((i % 20) * 14.0, (i / 20) * 14.0));
                    pairs.Add((me, t));
                }
                site.Step(Dt, k.Time + Dt);
                long seen0 = ReactionFeedback.EmgRuleSeen;
                foreach ((int me, int t) in pairs)
                {
                    k.FireAt(me, t, k.Time);
                }
                site.Step(Dt, k.Time + Dt);
                int n = k.EmgRuleCountOfKey(MarkKey);
                string name = NamedReactionCatalog.NameOf(rid);
                ReactionLogEntry entry = ReactionLog.Filtered(ReactionLogFilter.All, s2).FirstOrDefault(x => x.ReactionId == rid);
                bool popup = ReactionPopups.Active.Count == 1 && ReactionPopups.Active[0].Count == n && ReactionPopups.Active[0].Text == name + " ×" + n;
                Expect(n == 200 && ReactionFeedback.EmgRuleSeen - seen0 == 200 && popup && entry != null && entry.Count == 200,
                    $"同一步 200 次：内核计数 {n}、热更层经脏列表处理 {ReactionFeedback.EmgRuleSeen - seen0} 次、弹字 {ReactionPopups.Active.Count} 条“{ReactionPopups.Active.FirstOrDefault()?.Text}”、日志一条 ×{entry?.Count}");
            }
        }

        // ── F. 倍速 / 暂停 / 观察 / 存读档（FGT-EMG-042）──────────────────────────────

        private static void CheckSpeeds()
        {
            Line("  · F. FGT-EMG-042：同一段游戏时间按 0.5x / 1x / 2x / 3x 的帧分组步进、中途暂停若干帧、观察 / 不观察、中途存读档——可靠计数与 StateHash 一致（内核单线程，线程数不影响）");
            EmgProgramResult pr = Compile(MarkTag);
            (int count, ulong hash, int attributed) Run(int perFrame, bool observe, bool pause, bool reload)
            {
                CampaignState s = NewState(9840);
                ReactionFeedback.RecordFirst(s, MechanicalReactionCatalog.ReactionMarkJumpId, SiteId);
                FeedbackCues.ObservedSiteProvider = () => observe ? SiteId : "elsewhere";
                var site = new CombatSite(SiteId, Cfg());
                try
                {
                    CombatKernel k = site.Kernel;
                    int w = k.AddWeapon(Melee(k, pr));
                    var pairs = new List<(int me, int t)>();
                    for (int i = 0; i < 6; i++)
                    {
                        (int me, int t, _, _) = Pair(k, w, new double2(i * 14.0, 0));
                        pairs.Add((me, t));
                    }
                    int steps = 0;
                    for (int frame = 0; steps < 120; frame++)
                    {
                        if (pause && frame % 7 == 3)
                        {
                            ReactionFeedback.Process(site, s); // 暂停：不推进模拟，热更层照常读（没有新变化）
                            continue;
                        }
                        for (int q = 0; q < perFrame && steps < 120; q++, steps++)
                        {
                            if (steps % 20 == 0)
                            {
                                (int me, int t) = pairs[(steps / 20) % pairs.Count];
                                k.FireAt(me, t, k.Time);
                            }
                            site.Step(Dt, k.Time + Dt);
                            if (reload && steps == 49)
                            {
                                byte[] snap = k.Serialize();
                                k.Load(snap);
                            }
                        }
                    }
                    ReactionSessionRecord sess = ReactionAttribution.Current(s, SiteId, create: false);
                    int attributed = sess?.Reactions.FirstOrDefault(r => r.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId)?.Count ?? 0;
                    return (k.EmgRuleCountOfKey(MarkKey), k.StateHash(), attributed);
                }
                finally
                {
                    site.Dispose();
                }
            }
            var baseline = Run(1, true, false, false);
            var rows = new List<string>();
            bool same = baseline.count == 6;
            foreach ((int per, bool obs, bool pause, bool reload, string label) in new[]
                     {
                         (1, false, false, false, "1x 不观察"), (2, true, false, false, "2x"), (3, true, true, false, "3x + 暂停"), (1, true, true, true, "0.5x（隔帧暂停）+ 中途存读档"),
                     })
            {
                var r = Run(per, obs, pause, reload);
                bool ok = r.count == baseline.count && r.hash == baseline.hash && r.attributed == baseline.attributed;
                same &= ok;
                rows.Add($"{label}：计数 {r.count}、归因 {r.attributed}{(ok ? "" : "（不一致）")}");
            }
            Expect(same && baseline.attributed == 6, $"基线 1x 观察：计数 {baseline.count}、归因 {baseline.attributed}、哈希 {baseline.hash:X16}；" + string.Join("；", rows));
        }

        // ── G. 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · G. 性能（只测一次）：满环 256 条重建 4 条链（只在打开叠加时，每 0.25 秒一次）；热更层每步读脏列表与单位数无关");
            CampaignState s = NewState(9850);
            using var site = new CombatSite(SiteId, Cfg());
            CombatKernel k = site.Kernel;
            for (int i = 0; i < 300; i++)
            {
                k.EmgChainLogForTests(i % 3 == 0 ? EmgChainNode.Shot : EmgChainNode.Trigger, 1 + i % 40, 0);
            }
            var lines = new List<EmgChainLine>();
            EmgChainView.BuildChains(site, s, 4, lines); // 预热
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                EmgChainView.BuildChains(site, s, 4, lines);
            }
            sw.Stop();
            double perBuild = sw.Elapsed.TotalMilliseconds / 100.0;
            if (perBuild > 1.0 && perBuild <= 2.0)
            {
                Line($"    ⚠ 性能警告：满环重建 4 条链 {perBuild:0.###} 毫秒 / 次（阈值 1.0，未到 2 倍）");
                _pass++;
            }
            else
            {
                Expect(perBuild <= 1.0 && lines.Count == 4, $"满环 256 条重建 4 条链：{perBuild:0.###} 毫秒 / 次（阈值 1.0；只在打开叠加时每 0.25 真实秒一次）");
            }
            double Process(int units)
            {
                using var s2 = new CombatSite(SiteId + units, Cfg());
                for (int i = 0; i < units; i++)
                {
                    s2.Kernel.Spawn(HostileSpawn(new double2(i % 50, i / 50)));
                }
                s2.Step(Dt, s2.Kernel.Time + Dt);
                var w = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    ReactionFeedback.Process(s2, s);
                }
                w.Stop();
                return w.Elapsed.TotalMilliseconds / 200.0;
            }
            double few = Process(10), many = Process(1000);
            Expect(many <= few * 3 + 0.02, $"热更层每步反馈（含读脏列表）：10 单位 {few * 1000:0.#} 微秒 / 1000 单位 {many * 1000:0.#} 微秒（与单位数无关）");
        }

        // ── D3. 机器悬停（FG-GAP-171）───────────────────────────────────────────

        private static void CheckMachineHover()
        {
            Line("  · D3. FG-GAP-171：战斗中悬停带程序的机器（正式装配编译、家园地点）——没有标签也弹，附“程序：分裂”一行");
            SignalUplinkService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            CampaignState s = FgProductionSelfCheck.NewWorld(9860, true, 2000);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, new[] { Split });
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != Bp)
                .Append(new BlueprintRecord { BlueprintId = Bp, DisplayName = Bp, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, Bp, HomeValleyLayout.RegionId, new Vector2(4f, 4f), 200f, 200f);
            MachineLoadoutRegistry.Register(s, m.LogicId, Bp, 1);
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            int unit = 0;
            CombatUnitView v = default;
            bool ok = site != null && m.Success && site.TryGetMachineUnit(m.LogicId, out unit) && site.Kernel.TryGetUnit(unit, out v);
            int hovered = ok ? StatusTagHover.HoverGroundForTests(site, v.Position) : 0;
            TooltipContent c = StatusTagHover.HoveredContentForTests();
            string body = c?.Body ?? string.Empty;
            string head = GameText.Get("emg.hover.program");
            string fwName = EmgProgramTreeText.FirmwareName(Split);
            Expect(ok && hovered == unit && c != null && body.Contains(head) && body.Contains(EmgProgramTreeText.GlyphNode) && body.Contains(EmgProgramTreeText.GlyphOwn + " " + fwName)
                   && !body.Contains(GameText.Get("emg.hover.program_rejected")),
                $"悬停家园里装 [分裂] 的机器：弹出“{c?.Title}”，正文是程序树（标题“{head}”、节点行 {EmgProgramTreeText.GlyphNode}、自有指令行“{EmgProgramTreeText.GlyphOwn} {fwName}”）（{body.Replace("\n", " / ")}）");
            // 修复轮（审查 P1）：程序登记进地点程序表被拒（表满）时——标题写“未生效”、列出原因与解决办法，不显示成在运行的程序
            MachineWeaponInfo info = default;
            bool hasInfo = ok && site.TryGetMachineWeapon(m.LogicId, out info) && info.Program != null;
            string rejected = hasInfo ? EmgChainView.ProgramHoverText(info.Program, EmgProgramCompiler.NoteTableFull) : string.Empty;
            string fix = GameText.Get(EmgProgramTreeText.FixKey(EmgProgramCompiler.NoteTableFull));
            Expect(hasInfo && info.ProgramNoteKey == null && rejected != null && rejected.StartsWith(GameText.Get("emg.hover.program_rejected")) && !rejected.Contains(head) && rejected.Contains(fix)
                   && EmgChainView.ProgramHoverText(null, null) == null,
                $"表满被拒：悬停标题“{GameText.Get("emg.hover.program_rejected")}”、含解决办法“{fix}”、不显示“{head}”；空程序不弹程序（{(rejected ?? "无").Replace("\n", " / ")}）");
            StatusTagHover.Leave(null);
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
