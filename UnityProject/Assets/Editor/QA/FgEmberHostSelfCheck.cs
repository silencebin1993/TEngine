using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.SilentMind;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG11-BOSS-01 余烬主机自检（FG09 第 7 章；FG19 FGR-EMG-031 / 165；验收 FGT-FAC-006、FGT-EMG-061；承接 FG-GAP-226、DEBT-EMG4FW01-01、DEBT-FG8GEN02-01、
    /// DEBT-FG8EXP03-01、DEBT-FG4ECO11-02、DEBT-FG5RND05-05、DEBT-FG8LOOT02-05、DEBT-FG8QST01-02）。全部从正式入口（数据中心入口 → InteriorSiteService.TryEnter）起真实世界跑：
    /// A 数据（敌人 / 编成 / 点位 / 调参 / 情报 / 剧情 / 通知 / 反馈，术语不出现“劫持”）；
    /// B 开战与阶段一（进门前自动存档、阶段唯一写入口、场地升温 → 内核热量上限实测下降、散热口张开 / 闭合受伤倍率）；
    /// C 阶段二槽位封锁（FGT-FAC-006 / FGT-EMG-061：被封槽固件保留不生效、程序树按 FGR-EMG-031 重排、HUD 与预览来源标注、共用事件 evt.seize；尖刺全毁夺回 → 同签名、冷却不重置、evt.release）；
    /// D 阶段三熔毁倒计时（HUD 倒计时、警报音效次数、熔毁重创全场含自己、负向“熔毁后战斗继续”、击毁奖励与只发一次、负向“熔毁前击毁不熔毁”）；
    /// E 全灭复位与首领战前自动存档重试、全员撤出复位；F 存读档（阶段二 / 阶段三中途，倒计时剩余与升温在读档后一致）；G 暂停与 0.5x～3x、观察与否一致；
    /// H 室内教义自卫（FG-GAP-226）；I 反夺控正式入口用例（DEBT-EMG4FW01-01）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmberHostSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmberHostSelfCheck。
    /// </summary>
    public static class FgEmberHostSelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string Center = EmberHostBoss.SiteId;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const float Dt = 1f / 60f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("QA/自检/FG 余烬主机")]
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
            Line("\n[余烬主机] 升频 / 槽位封锁 / 熔毁倒计时、奖励与平定、复位重试、存读档与倍速、室内教义自卫、反夺控（FG11-BOSS-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            Func<bool> oldExpedition = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgember-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
                GameRoot.BindWorldProviders();
                NarrativeService.EnsureListening();
                EmberHostBoss.ResetForTests();

                Step(CheckData);
                Step(CheckEngageAndUpclock);
                Step(CheckLockoutMeltdownAndRewards);
                Step(CheckKillBeforeMeltdown);
                Step(CheckWipeRetryAndExit);
                Step(CheckSaveLoad);
                Step(CheckSpeedMatrixAndObservation);
                Step(CheckInteriorSelfDefense);
                Step(CheckFailsafeViaLockout);
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                SignalUplinkService.RealTimeForTests = null;
                SignalCoreService.ExpeditionUnderwayOverrideForTests = oldExpedition;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  余烬主机：全部 {_pass} 项通过" : $"  余烬主机：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            MechEnemy host = Row("enemy_ember_host"), spike = Row("enemy_ember_spike");
            bool rows = host != null && host.Role == EnemyDefs.RoleBoss && EnemyDefs.IsBoss(host) && EnemyDefs.HasTag(host, EnemyDefs.TagExternalHealth) && host.Faction == EnemyDefs.FactionOverclock
                        && host.Components.Split(',')[0].Trim() == "fw_execute" && spike != null && EnemyDefs.IsStructure(spike) && !EnemyDefs.HasTag(spike, EnemyDefs.TagExternalHealth);
            int guards = EnemyDefs.Encounters(Center, EnemyDefs.ConditionBase).Count;
            bool parts = EmberHostBoss.CoreInstanceId != null && EmberHostBoss.Spikes.Count == 3 && guards >= 3;
            bool points = RelayHallLayout.IsInteriorWithPoints(Center) && RelayHallLayout.Points(Center).Count >= 8;
            bool tuning = Math.Abs(EmberHostBoss.MeltdownSeconds - 60f) < 1e-4f && EmberHostBoss.VentWarnSeconds >= 0.8f && EmberHostBoss.VentOpenSeconds < EmberHostBoss.VentCycleSeconds
                          && EmberHostBoss.HeatCapMin < 1f && EmberHostBoss.VentDamageMult > 1f && EmberHostBoss.SealedDamageMult < 1f;
            bool intel = IntelCatalog.TryGetBoss(IntelCatalog.BossIdEmberHost, out IntelBossDef bd) && bd.Rule == IntelCatalog.BossRuleEmberHost;
            bool narrative = NarrativeCatalog.All.Any(en => en.Trigger == NarrativeCatalog.TriggerLeader + EnemyDefs.FactionOverclock && en.Node);
            bool notify = NotificationCatalog.TryGetType("ember_boss", out _);
            FeedbackCueDef cd = FeedbackCueCatalog.Get(FeedbackCueId.EmberCountdown);
            bool cues = cd != null && !string.IsNullOrEmpty(cd.SfxId) && FeedbackCueCatalog.Get(FeedbackCueId.EmberVentWarn) != null && FeedbackCueCatalog.Get(FeedbackCueId.EmberBlast) != null;
            // 术语（FGR-EMG-165 第 14 节 #12）：首领文案一律“槽位封锁”，不出现“劫持”。
            string[] keys =
            {
                "boss.ember.phase.lockout", "boss.ember.notify.lockout", "boss.ember.hud.spikes", "boss.ember.invulnerable.lockout", "boss.ember.entrance.unknown",
                "boss.ember.entrance.preview", "intel.boss.ember_host.phases", "intel.boss.ember_host.weakness", "enemy.ember_host.desc", "enemy.ember_spike.desc",
            };
            bool term = keys.All(k => GameText.Get(k) != k && !GameText.Get(k).Contains("劫持")) && GameText.Get("boss.ember.phase.lockout").Contains("槽位封锁");
            Expect(rows && parts && points && tuning && intel && narrative && notify && cues && term,
                $"A1 数据：余烬主机（首领 / 外部耐久 / 奖励处决）与数据尖刺（内核耐久结构）入表 {rows}；数据中心编成（本体 + 3 尖刺 + 守卫 {guards}）{parts}、点位 {points}；" +
                $"熔毁 60 秒、散热口预警 ≥ 0.8 秒等调参 {tuning}；首领情报 {intel}、剧情节点 leader:overclock {narrative}、通知类型 {notify}、倒计时音效等反馈 {cues}；文案用“槽位封锁”不写“劫持” {term}");
            // 3 个种子下点位都落在空地（B25）：读室内地形，点位格不是悬崖。
            var bad = new List<string>();
            foreach (int seed in new[] { 1, 42, 20261017 })
            {
                CampaignState s = NewWorldState(seed);
                HomeGridMap map = WorldGenService.MapFor(s, Center);
                byte cliff = GridContent.TerrainCode("cliff");
                foreach (InteriorPoint p in RelayHallLayout.Points(Center))
                {
                    if (!RelayHallLayout.TryPoint(s, p.Id, out Vector2 at) || map.GetTerrain(new GridCell((int)at.x, (int)at.y)) == cliff)
                    {
                        bad.Add(seed + ":" + p.Id);
                    }
                }
            }
            Expect(bad.Count == 0, $"A2 B25：3 个种子下数据中心的 8 个点位都能落位、不在立柱上（{(bad.Count == 0 ? "全部空地" : string.Join(",", bad))}）");
        }

        // ─────────────────────────────── B 开战与阶段一 ───────────────────────────────

        private static InteriorSiteController EnterCenter(CampaignState s, int count, out int[] machines, out PlannedAnchor gate)
        {
            gate = InteriorSiteService.EntranceFor(s, Center);
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            HomeGridService.RevealArea(s, e, 30f);
            var ids = new List<int>();
            for (int i = 0; i < count; i++)
            {
                ids.Add(Spawn(s, e + new Vector2(i - count / 2f, 1f)));
            }
            machines = ids.ToArray();
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            if (!InteriorSiteService.TryEnter(s, gate.Id, out string why))
            {
                Fail("夹具：进入数据中心被拒：" + why);
            }
            return WorldSimulation.Interior(Center);
        }

        /// <summary>打掉守卫（只测首领）：守卫是首次进入播种的普通敌人，打掉的不复活。</summary>
        private static void ClearGuards(InteriorSiteController site)
        {
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(Center, EnemyDefs.ConditionBase))
            {
                site.Combat.TryDamageEnemy(enc.Id, 1e6f);
            }
            WorldSimulation.StepMany(2);
        }

        private static void HitCore(InteriorSiteController site, float dmg) => site.Combat.TryDamageEnemy(EmberHostBoss.CoreInstanceId, dmg);

        /// <summary>步进到散热口处于 <paramref name="state"/>（0 闭合 / 2 张开）的那一段中间。</summary>
        private static void StepToVent(CampaignState s, int state)
        {
            for (int i = 0; i < GameClock.StepHz * 30; i++)
            {
                if (EmberHostBoss.VentStateAt(EmberHostBoss.Record(s), GameClock.Ticks) == state && EmberHostBoss.VentSecondsLeft(EmberHostBoss.Record(s), GameClock.Ticks) > 0.5f)
                {
                    WorldSimulation.StepMany(1); // 状态机在下一步结算这次切换（提示信号）
                    return;
                }
                WorldSimulation.StepMany(1);
            }
        }

        private static int MachineUnit(InteriorSiteController site, int id) => site.Combat.TryGetMachineUnit(id, out int u) ? u : 0;

        private static CombatUnitView View(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static void CheckEngageAndUpclock()
        {
            CampaignState s = NewWorld(42);
            RaidAudioDirector.ResetSession();
            int musicBefore = RaidAudioDirector.Evaluate(s);
            string previewBefore = InteriorSiteService.BossPreview(s, Center);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            if (File.Exists(slotFile))
            {
                File.Delete(slotFile);
            }
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            bool autosave = File.Exists(slotFile);
            EmberBossRecord r = EmberHostBoss.Record(s);
            RegionEnemyRecord core = EmberHostBoss.Core(s);
            int coreUnit = site.Combat.TryGetEnemyUnit(EmberHostBoss.CoreInstanceId, out int cu) ? cu : 0;
            bool engaged = EmberHostBoss.PhaseOf(s) == EmberPhase.Upclock && r.Attempt == 1 && core != null && core.IsAlive && Math.Abs(core.Health - core.MaxHealth) < 1e-3f
                           && EmberHostBoss.SpikesAlive(s) == 0 && coreUnit != 0 && site.Combat.UnitHasFlag(coreUnit, CombatUnitFlags.WeaponEnabled)
                           && !site.Combat.UnitHasFlag(coreUnit, CombatUnitFlags.Invulnerable);
            WorldView.Observe(Center);
            bool hud = EmberHostHud.Compose(s, out string title, out _, out float mark, out string hpLine, out string lines);
            bool hudHost = SilentMindHudUIToolkit.Compose(s, out _, out _, out _, out _, out _) == false && hud;
            int rejected = EmberHostBoss.RejectedTransitions;
            bool illegal = !EmberHostBoss.TryTransition(s, EmberPhase.Defeated) && EmberHostBoss.RejectedTransitions == rejected + 1 && EmberHostBoss.PhaseOf(s) == EmberPhase.Upclock;
            // FGR-FAC-006“首领战有专属音乐”：开战前平静、开战后音乐层切到首领战（占位曲 MusicBoss）
            RaidAudioDirector.Tick(s, true, 0f, true);
            bool music = musicBefore != RaidAudioDirector.PhaseBoss && RaidAudioDirector.Phase == RaidAudioDirector.PhaseBoss && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicBoss;
            Expect(music, $"B1b 首领战音乐（FGR-FAC-006）：开战前音乐阶段 {musicBefore}（非首领战）→ 开战后 {RaidAudioDirector.Phase}，播放“{RaidAudioDirector.CurrentMusic}”");
            Expect(previewBefore.Contains(GameText.Get("boss.ember.entrance.unknown")) && autosave && engaged && hudHost && title.Contains(EmberHostBoss.PhaseName(EmberPhase.Upclock))
                   && Math.Abs(mark - EmberHostBoss.Phase2HpFraction) < 1e-4f && illegal,
                $"B1 开战（FGR-FAC-006）：入口说明“{previewBefore.Trim()}”；进门前写首领战前自动存档（{autosave}）；进门即开战——{EmberHostBoss.PhaseName(EmberPhase.Upclock)}（第 {r.Attempt} 次挑战），" +
                $"本体满血可伤、开火（{engaged}），尖刺还没出现；首领 HUD“{title}”（寂听主脑 HUD 不抢位 {hudHost}），血条在 {mark:P0} 标出阶段分界，“{hpLine}”；非法转换 升频→已击毁 被拒（{illegal}）");

            // 场地升温：每 heat_step_seconds 一级，数据中心内核里机器的武器过热点按倍率下降（敌我同一规则）。
            ClearGuards(site);
            int mu = MachineUnit(site, m[0]);
            float cap0 = site.Combat.Kernel.OverheatAtFor(mu);
            WorldSimulation.StepMany((int)GameClock.TicksFor(EmberHostBoss.HeatStepSeconds * 2f) + 2);
            float cap2 = site.Combat.Kernel.OverheatAtFor(mu);
            float want = 1f - 2f * EmberHostBoss.HeatStep;
            bool heated = r.HeatLevel == 2 && Math.Abs(EmberHostBoss.HeatCapMult(r) - want) < 1e-4f && cap0 > 0f && Math.Abs(cap2 / cap0 - want) < 1e-3f
                          && site.Combat.Kernel.HeatRadiationZoneCount == 1;
            EmberHostHud.Compose(s, out _, out _, out _, out _, out string heatLines);
            bool heatHud = heatLines.Contains(GameText.Format("boss.ember.hud.heat", Mathf.RoundToInt(want * 100f).ToString()));
            Expect(heated && heatHud,
                $"B2 阶段一·升频：{EmberHostBoss.HeatStepSeconds * 2f:0} 游戏秒升温 {r.HeatLevel} 级，热量上限倍率 {EmberHostBoss.HeatCapMult(r):0.00}；内核实测机器武器过热点 {cap0:0.##} → {cap2:0.##}" +
                $"（比值 {(cap0 > 0f ? cap2 / cap0 : 0f):0.000}），HUD 写“场地温度”（{heatHud}）");

            // 散热口：闭合时受伤 × sealed，张开时 × vent（重创窗口）；张开前有预警信号。
            int warn0 = EmberHostBoss.VentWarns, open0 = EmberHostBoss.VentOpens;
            StepToVent(s, 0);
            float h0 = core.Health;
            HitCore(site, 10f);
            float closedLoss = h0 - core.Health;
            StepToVent(s, 2);
            float h1 = core.Health;
            HitCore(site, 10f);
            float openLoss = h1 - core.Health;
            EmberHostHud.Compose(s, out _, out _, out _, out _, out string ventLines);
            bool vents = Math.Abs(closedLoss - 10f * EmberHostBoss.SealedDamageMult) < 1e-3f && Math.Abs(openLoss - 10f * EmberHostBoss.VentDamageMult) < 1e-3f
                         && EmberHostBoss.VentWarns > warn0 && EmberHostBoss.VentOpens > open0
                         && ventLines.Contains(GameText.Format("boss.ember.hud.vent_open", string.Empty, string.Empty).Substring(0, 4));
            Expect(vents, $"B3 散热口开合：闭合时 10 点伤害只掉 {closedLoss:0.##}（×{EmberHostBoss.SealedDamageMult}），张开时掉 {openLoss:0.##}（×{EmberHostBoss.VentDamageMult}）；" +
                          $"张开前预警 {EmberHostBoss.VentWarns - warn0} 次、张开提示 {EmberHostBoss.VentOpens - open0} 次（各至少一次）；HUD“{ventLines.Replace("\n", " / ")}”");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── C / D 阶段二、三与奖励 ───────────────────────────────

        private const string Split = "fw_split", Homing = "fw_homing", Pierce = "fw_pierce", Oil = "fw_oilleak";

        private static void Equip(CampaignState s, string firmwareId, int slot)
        {
            string part = PrimitiveInventory.GrantBossFirmware(s, "selfcheck:ember:" + firmwareId + ":" + slot, firmwareId);
            SignalCoreResult eq = SignalCoreService.TryEquip(s, part, slot);
            if (!eq.Success)
            {
                Fail($"测试准备：装 {firmwareId} 到 {slot + 1} 号槽失败（{eq.Message}）");
            }
        }

        private static EmgProgramResult CompileCore(CampaignState s) =>
            EmgProgramCompiler.Compile(SignalCoreService.ActiveContentIds(s).Where(x => x.Length > 0).ToArray(), new[] { Pierce, Oil }, FirmwareCarrier.Projectile);

        private static string Tree(EmgProgramResult r) =>
            string.Join(" | ", r.Nodes.Select(n => $"{n.Gen}代:" + string.Join("+", n.Entries.Select(e => r.Steps.First(st => st.Ordinal == e.Ordinal).FirmwareId.Replace("fw_", string.Empty)))));

        /// <summary>阶段一打到门槛 → 阶段二（耐久停在门槛上）；返回被封的槽。</summary>
        private static int DriveToLockout(InteriorSiteController site, CampaignState s)
        {
            HitCore(site, 1e6f);
            WorldSimulation.StepMany(2); // 室内内容对账：尖刺生成内核单位
            return EmberHostBoss.Record(s).BlockedSlot;
        }

        private static void KillSpike(InteriorSiteController site, int index)
        {
            site.Combat.TryDamageEnemy(EmberHostBoss.Spikes[index].Id, 1e6f);
            WorldSimulation.StepMany(2);
        }

        private static void CheckLockoutMeltdownAndRewards()
        {
            CampaignState s = NewWorld(1);
            Equip(s, Split, 0);
            Equip(s, Homing, 1);
            EmgProgramResult before = CompileCore(s);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            ClearGuards(site);
            WorldView.Observe(Center);
            EmberBossRecord r = EmberHostBoss.Record(s);
            RegionEnemyRecord core = EmberHostBoss.Core(s);
            int expect = EmberHostBoss.PickSlot(s, r);
            string expectFw = SignalCoreService.SlotContentId(s, expect);
            var cd = new SignalCoreCooldownRecord { ContentId = expectFw, ReadyTick = GameClock.Ticks + 6000 };
            s.SignalCore.CoreCooldowns = new[] { cd };
            double cd0 = SignalUplinkService.CooldownRemaining(s, expectFw);
            long cdTick0 = GameClock.Ticks;
            int blockedNotes = NotifyCount("emg_slot_blocked");
            int slot = DriveToLockout(site, s);
            EmgProgramResult during = CompileCore(s);
            bool lockout = EmberHostBoss.PhaseOf(s) == EmberPhase.Lockout && slot == expect && SignalSeizeService.IsBlocked(s, slot)
                           && s.SignalCore.SlotBlocks.Any(b => b.Slot == slot && b.Source == SignalSeizeService.SourceEmber)
                           && SignalCoreService.ActiveContentIds(s)[slot].Length == 0 && SignalCoreService.SlotContentId(s, slot) == expectFw
                           && Math.Abs(core.Health - core.MaxHealth * EmberHostBoss.Phase2HpFraction) < 1e-2f && EmberHostBoss.SpikesAlive(s) == 3
                           && EmberHostBoss.Spikes.All(sp => site.Combat.TryGetEnemyUnit(sp.Id, out _)) && NotifyCount("emg_slot_blocked") > blockedNotes;
            // FGR-EMG-031 重排：[分裂, 寻的] + [贯穿, 漏油]：封掉分裂（触发器）→ 下游回到根；封掉寻的 → 根 = 分裂 + 漏油、子节点只剩贯穿。
            string treeDuring = Tree(during);
            bool reorder = before.Signature != during.Signature && during.Steps.All(st => st.FirmwareId != expectFw)
                           && (expectFw == Split ? during.Nodes.Count == 1 : during.Nodes.Count == 2);
            string reason = SignalCoreService.SlotStatusLine(s, slot);
            bool preview = reason.Contains(GameText.Get("emg.seize.source.ember")) && (expectFw != Split || reason.Contains(GameText.Get("emg.seize.slot.trigger_note")));
            EmberHostHud.Compose(s, out string title, out _, out _, out _, out string lines);
            string status = SignalSeizeService.HudLine(s);
            bool hud = title.Contains(EmberHostBoss.PhaseName(EmberPhase.Lockout)) && lines.Contains(GameText.Format("boss.ember.hud.spikes", "3", "3"))
                       && lines.Contains(GameText.Format("emg.seize.hud.slot", (slot + 1).ToString(), GameText.Get("emg.seize.source.ember")))
                       && status.Contains(GameText.Format("emg.seize.hud.slot", (slot + 1).ToString(), GameText.Get("emg.seize.source.ember")));
            float hpLock = core.Health;
            HitCore(site, 500f);
            bool shielded = Math.Abs(core.Health - hpLock) < 1e-3f;
            Expect(lockout && reorder && preview && hud && shielded,
                $"C1 FGT-FAC-006 / FGT-EMG-061 阶段二·槽位封锁：耐久停在 {EmberHostBoss.Phase2HpFraction:P0} 门槛 → 封锁第 {slot + 1} 槽（按种子 + 第几次挑战，预期 {expect + 1}；来源余烬主机；固件“{expectFw}”留在槽里、生效列表为空）{lockout}；" +
                $"程序树按 FGR-EMG-031 重排：{Tree(before)} → {treeDuring}（{reorder}）；预览原因行“{reason}”（{preview}）；首领 HUD 写尖刺 3/3 与被封锁的槽，HUD 状态行“{status}”（{hud}）；本阶段本体不可伤（{shielded}）");

            // 负向：阶段二本体不可伤 → 散热口照转，但不发预警 / “重创窗口”提示（修复轮 1）。
            int warnL = EmberHostBoss.VentWarns, openL = EmberHostBoss.VentOpens;
            StepToVent(s, 1);
            StepToVent(s, 2);
            bool ventSilent = EmberHostBoss.VentWarns == warnL && EmberHostBoss.VentOpens == openL && EmberHostBoss.Record(s).VentState == 2 && EmberHostBoss.PhaseOf(s) == EmberPhase.Lockout;
            Expect(ventSilent, $"C1b 负向：阶段二散热口转到张开（状态 {EmberHostBoss.Record(s).VentState}），预警 / 张开提示没有发出（{EmberHostBoss.VentWarns - warnL} / {EmberHostBoss.VentOpens - openL}）");

            // 尖刺：拆 2 根槽仍封锁；拆完 3 根 → 夺回（同签名、冷却不重置）→ 阶段三。
            KillSpike(site, 0);
            KillSpike(site, 1);
            bool stillBlocked = SignalSeizeService.IsBlocked(s, slot) && EmberHostBoss.PhaseOf(s) == EmberPhase.Lockout && r.SpikesLeft == 1;
            int restoredNotes = NotifyCount("emg_slot_restored");
            int beeps0 = EmberHostBoss.CountdownBeeps; // 含进入阶段三时的第一声
            KillSpike(site, 2);
            EmgProgramResult after = CompileCore(s);
            double cd1 = SignalUplinkService.CooldownRemaining(s, expectFw);
            long endTick = r.MeltdownEndTick;
            bool retaken = !SignalSeizeService.IsBlocked(s, slot) && after.Signature == before.Signature && s.SignalCore.CoreCooldowns[0].ReadyTick == cd.ReadyTick
                           && Math.Abs((cd0 - cd1) - (GameClock.Ticks - cdTick0) / (double)GameClock.StepHz) < 0.05 && cd1 > 0 && NotifyCount("emg_slot_restored") > restoredNotes
                           && EmberHostBoss.PhaseOf(s) == EmberPhase.Meltdown && endTick - GameClock.Ticks > GameClock.TicksFor(59.0);
            Expect(stillBlocked && retaken,
                $"C2 摧毁尖刺夺回槽位：拆 2 根仍封锁（还剩 {1}，{stillBlocked}）；拆完 3 根 → 立即恢复，程序树与封锁前同签名、冷却 ReadyTick 不变（剩 {cd1:0.#} 秒，不重置）、恢复通知 → {EmberHostBoss.PhaseName(EmberPhase.Meltdown)}（{retaken}）");

            // 阶段三：HUD 倒计时、警报（开始、30 秒、最后 10 秒每秒）；到时熔毁重创全场（含自己），战斗继续。
            EmberHostHud.Compose(s, out _, out _, out _, out _, out string cdLines);
            bool cdHud = cdLines.Contains(GameText.Format("boss.ember.hud.countdown", "60")) || cdLines.Contains(GameText.Format("boss.ember.hud.countdown", "59"));
            float coreBefore = core.Health;
            float mHp0 = View(site.Combat, MachineUnit(site, m[0])).Health, mMax = View(site.Combat, MachineUnit(site, m[0])).MaxHealth;
            int blasts0 = EmberHostBoss.Blasts;
            for (int i = 0; i < GameClock.StepHz * 61 && EmberHostBoss.PhaseOf(s) == EmberPhase.Meltdown; i++)
            {
                WorldSimulation.StepMany(1);
            }
            int beeps = EmberHostBoss.CountdownBeeps - beeps0;
            float mHp1 = View(site.Combat, MachineUnit(site, m[0])).Health;
            bool blasted = EmberHostBoss.Blasts == blasts0 + 1 && r.MeltedDown && EmberHostBoss.PhaseOf(s) == EmberPhase.Burnout
                           && Math.Abs((mHp0 - mHp1) - mMax * EmberHostBoss.MeltdownMachineFraction) < 1f
                           && Math.Abs((coreBefore - core.Health) - core.MaxHealth * EmberHostBoss.MeltdownSelfFraction) < 1e-2f;
            // 负向：熔毁之后战斗继续——地点还在、机器活着、本体可选中可伤。
            int cu = site.Combat.TryGetEnemyUnit(EmberHostBoss.CoreInstanceId, out int cuu) ? cuu : 0;
            float hb = core.Health;
            StepToVent(s, 2);
            HitCore(site, 10f);
            bool goesOn = WorldSimulation.Interior(Center) != null && m.All(id => Rec(id)?.IsAlive == true && Rec(id).RegionId == Center)
                          && site.Combat.UnitHasFlag(cu, CombatUnitFlags.Targetable) && core.Health < hb && core.IsAlive;
            EmberHostHud.Compose(s, out _, out _, out _, out _, out string burnLines);
            Expect(cdHud && beeps == 12 && blasted && goesOn && burnLines.Contains(GameText.Get("boss.ember.hud.burnout")),
                $"C3 阶段三·熔毁倒计时：HUD 写倒计时（{cdHud}）；倒计时警报 {beeps} 次（开始 1 + 剩 30 秒 1 + 最后 10 秒每秒 1 = 12）；到时熔毁：机器失去 {mHp0 - mHp1:0.#}/{mMax:0} 耐久（× {EmberHostBoss.MeltdownMachineFraction}），" +
                $"本体自己失去 {coreBefore - core.Health:0.#}（× {EmberHostBoss.MeltdownSelfFraction}）（{blasted}）；负向“熔毁之后战斗继续”：地点在、机器活着、本体仍可选中可伤（{goesOn}）");

            // 击毁：奖励（处决 + 三选一）、超频领地平定 → 主线 / 超算残核、剧情“破译”、解除封锁与降温、情报不再破译、奖励只发一次。
            int packets0 = LootPickService.Count(s);
            HitCore(site, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool defeated = EmberHostBoss.IsDefeated(s);
            bool execute = s.PrimitiveChips.Any(p => p.CardDefId == "fw_execute" && p.Origin == PrimitiveInventory.OriginBoss);
            bool packet = LootPickService.Count(s) == packets0 + 1 && LootPickService.Packets(s).Last().Source == LootPickService.SourceBoss;
            bool pacified = TerritoryControlService.Find(s, EnemyDefs.FactionOverclock)?.LeaderDefeated == true;
            KeyMaterial keyCore = QuestCatalog.KeyMaterialOf("supercomputer_core");
            bool keyMat = keyCore != null && CampaignObjectiveTracker.HasKeyMaterial(s, keyCore);
            string nodeId = NarrativeCatalog.All.FirstOrDefault(en => en.Trigger == NarrativeCatalog.TriggerLeader + EnemyDefs.FactionOverclock)?.Id ?? "-";
            bool narrative = NarrativeService.Has(s, nodeId);
            bool intelDone = IntelCatalog.TryGetBoss(IntelCatalog.BossIdEmberHost, out IntelBossDef bdef) && IntelService.BossDefeated(s, bdef);
            bool cleaned = (s.SignalCore.SlotBlocks?.Length ?? 0) == 0 && site.Combat.Kernel.HeatRadiationZoneCount == 0 && !EmberHostHud.Compose(s, out _, out _, out _, out _, out _);
            int grants = EmberHostBoss.RewardGrants;
            EmberHostBoss.ApplyDamage(s, core, 100f);
            bool once = EmberHostBoss.RewardGrants == grants && s.PrimitiveChips.Count(p => p.CardDefId == "fw_execute" && p.Origin == PrimitiveInventory.OriginBoss) == 1;
            bool defeatedPreview = InteriorSiteService.BossPreview(s, Center).Contains(GameText.Get("boss.ember.notify.defeated"));
            bool musicOff = RaidAudioDirector.Evaluate(s) != RaidAudioDirector.PhaseBoss; // 击毁后首领战音乐层退场
            Expect(defeated && execute && packet && pacified && keyMat && narrative && intelDone && cleaned && once && defeatedPreview && musicOff,
                $"C4 击毁余烬主机（FGR-FAC-006 奖励）：核心固件“处决” {execute}、三选一数据包 {packet}（DEBT-FG8LOOT02-05）；超频领地平定 {pacified}（DEBT-FG8EXP03-01）→ 主线“让数据城降温”关键材料“超算残核”进保管库 {keyMat}" +
                $"（DEBT-FG4ECO11-02 / DEBT-FG8QST01-02）；剧情节点“破译”（{nodeId}）{narrative}；监听站不再破译它 {intelDone}；封锁全部解除、场地降温、HUD 收起 {cleaned}；奖励只发一次 {once}；入口说明写已击毁 {defeatedPreview}；首领战音乐退场 {musicOff}");
            RaidAudioDirector.ResetSession();
            SignalCoverageService.OverrideForTests = null;
        }

        /// <summary>负向：熔毁倒计时结束前击毁 → 不熔毁（机器不掉血、计数不变）、封锁已解除。</summary>
        private static void CheckKillBeforeMeltdown()
        {
            CampaignState s = NewWorld(42);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            ClearGuards(site);
            DriveToLockout(site, s);
            for (int i = 0; i < 3; i++)
            {
                KillSpike(site, i);
            }
            int blasts0 = EmberHostBoss.Blasts;
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            float mHp = View(site.Combat, MachineUnit(site, m[0])).Health;
            HitCore(site, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz * 45);
            bool ok = EmberHostBoss.IsDefeated(s) && EmberHostBoss.Blasts == blasts0 && Math.Abs(View(site.Combat, MachineUnit(site, m[0])).Health - mHp) < 1e-3f
                      && (s.SignalCore.SlotBlocks?.Length ?? 0) == 0;
            Expect(ok, $"D1 负向：倒计时第 20 秒击毁 → 之后 45 秒不熔毁（熔毁次数 {EmberHostBoss.Blasts - blasts0}）、机器不掉血、没有残留封锁（{ok}）");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── E 全灭 / 撤出 ───────────────────────────────

        private static void CheckWipeRetryAndExit()
        {
            CampaignState s = NewWorld(42);
            Equip(s, Pierce, 0);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            string preFight = Path.Combine(_dir, "ember-pre-fight.json");
            File.Copy(slotFile, preFight, true);
            ClearGuards(site);
            EmberBossRecord r = EmberHostBoss.Record(s);
            WorldSimulation.StepMany((int)GameClock.TicksFor(EmberHostBoss.HeatStepSeconds) + 2);
            int slot = DriveToLockout(site, s);
            bool inLockout = EmberHostBoss.PhaseOf(s) == EmberPhase.Lockout && slot >= 0 && SignalSeizeService.IsBlocked(s, slot);
            int notes0 = NotifyCount("ember_boss");
            foreach (int id in m)
            {
                if (site.Combat.TryGetMachineUnit(id, out int u))
                {
                    site.Combat.SetUnitHealth(u, 0f, 100f, false);
                }
                MachineRegistry.MarkDeadByLogicId(id);
            }
            WorldSimulation.StepMany(3);
            RegionEnemyRecord core = EmberHostBoss.Core(s);
            bool unloaded = WorldSimulation.Interior(Center) == null;
            bool reset = EmberHostBoss.PhaseOf(s) == EmberPhase.Dormant && r.LastReachedPhase == (int)EmberPhase.Lockout && core.Health >= core.MaxHealth - 1e-3f
                         && EmberHostBoss.Spikes.All(sp => EmberHostBoss.FindPart(s, sp.Id) == null) && !SignalSeizeService.IsBlocked(s, slot) && r.HeatLevel == 0;
            string retryText = LatestDetail("ember_boss");
            bool retryNote = NotifyCount("ember_boss") > notes0 && retryText.Contains(EmberHostBoss.PhaseName(EmberPhase.Lockout));
            bool preview = InteriorSiteService.BossPreview(s, Center).Contains(EmberHostBoss.PhaseName(EmberPhase.Lockout));
            File.Copy(preFight, CampaignSaveService.SlotPath(Slot), true);
            CampaignState retry = Reload(Slot);
            bool preState = retry != null && EmberHostBoss.PhaseOf(retry) == EmberPhase.Dormant && (retry.SignalCore.SlotBlocks?.Length ?? 0) == 0
                            && m.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord rr) && rr.IsAlive && rr.RegionId == HomeValleyLayout.RegionId);
            Expect(inLockout && unloaded && reset && retryNote && preview && preState,
                $"E1 负向“全灭重试”：阶段二全灭 → 数据中心卸载（{unloaded}），首领复位（满血、尖刺撤走、封锁解除、场地降温，{reset}）并记下“上次止步于{EmberHostBoss.PhaseName((EmberPhase)r.LastReachedPhase)}”；" +
                $"通知“{retryText}”（{retryNote}），入口说明同样写出（{preview}）；读首领战前的自动存档 → 机器在入口外、首领休眠、没有封锁（{preState}）");

            // 再次进入：第 2 次挑战；打到阶段二后全员撤出 → 同样复位、解除封锁。
            InteriorSiteController again = EnterCenter(retry, 2, out int[] m2, out _);
            ClearGuards(again);
            EmberBossRecord r2 = EmberHostBoss.Record(retry);
            bool second = EmberHostBoss.PhaseOf(retry) == EmberPhase.Upclock && r2.Attempt >= 1;
            int slot2 = DriveToLockout(again, retry);
            foreach (int id in m2)
            {
                if (again.Combat.TryGetMachineUnit(id, out int u))
                {
                    again.Combat.SetUnitPosition(u, again.ExitPoint + new Vector2(0.3f * (id % 2), 1f));
                }
            }
            WorldSimulation.StepMany(2);
            bool exited = InteriorSiteService.TryExit(retry, Center, out string why);
            WorldSimulation.StepMany(3);
            bool exitReset = EmberHostBoss.PhaseOf(retry) == EmberPhase.Dormant && r2.LastReachedPhase == (int)EmberPhase.Lockout && (slot2 < 0 || !SignalSeizeService.IsBlocked(retry, slot2));
            Expect(second && exited && exitReset,
                $"E2 再次进入重新开战（{second}）；打到阶段二后全员撤出（{exited}{(exited ? string.Empty : "：" + why)}）→ 首领复位、封锁解除（{exitReset}）——不跨出击磨血、不把封锁带回家");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── F 存读档 ───────────────────────────────

        private static string Fingerprint(CampaignState s)
        {
            EmberBossRecord r = EmberHostBoss.Record(s);
            RegionEnemyRecord core = EmberHostBoss.Core(s);
            CombatSite site = CombatSites.Get(Center);
            string spikes = string.Join(",", EmberHostBoss.Spikes.Select(sp => EmberHostBoss.FindPart(s, sp.Id)?.IsAlive == true ? "1" : "0"));
            string blocks = string.Join(",", (s.SignalCore.SlotBlocks ?? Array.Empty<SignalSlotBlockRecord>()).Select(b => b.Slot + ":" + b.Source));
            string heat = site != null ? site.Kernel.HeatRadiationZoneCount.ToString() : "-";
            long left = r.MeltdownEndTick > 0 ? r.MeltdownEndTick - GameClock.Ticks : -1;
            return $"{r.Phase}|{r.HeatLevel}|{r.NextHeatTick}|{r.VentState}|{r.BlockedSlot}|{blocks}|{spikes}|{left}|{core?.Health.ToString("R")}|{heat}|{GameClock.Ticks}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(1);
            Equip(s, Pierce, 0);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            ClearGuards(site);
            WorldSimulation.StepMany((int)GameClock.TicksFor(EmberHostBoss.HeatStepSeconds) + 2);
            int slot = DriveToLockout(site, s);
            KillSpike(site, 0);
            WorldView.Observe(Center);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string saved = Fingerprint(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string continued = Fingerprint(s);
            CampaignState back = Reload(Slot);
            int resumed = InteriorSiteService.ResumeAll(back);
            string loaded = Fingerprint(back);
            InteriorSiteController bs = WorldSimulation.Interior(Center);
            int mu = bs != null ? MachineUnit(bs, m[0]) : 0;
            bool heatBack = bs != null && bs.Combat.Kernel.HeatRadiationZoneCount == 1 && EmberHostBoss.HeatCapMult(EmberHostBoss.Record(back)) < 1f && mu != 0;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string after = Fingerprint(back);
            Expect(sv.Success && resumed == 1 && loaded == saved && after == continued && SignalSeizeService.IsBlocked(back, slot) && heatBack,
                $"F1 阶段二中途存档（已拆 1 根尖刺、升温 1 级、第 {slot + 1} 槽被封）：读档后阶段、升温、散热口、封锁、尖刺、本体耐久一致（{loaded == saved}），场地升温重新写进内核（{heatBack}），" +
                $"接着跑 3 秒与不存档逐位相同（{after == continued}）：{after}");

            // 阶段三中途存档：倒计时剩余秒数读档后一致。
            KillSpike(bs, 1);
            KillSpike(bs, 2);
            WorldSimulation.StepMany(GameClock.StepHz * 17);
            float leftSaved = (EmberHostBoss.Record(back).MeltdownEndTick - GameClock.Ticks) / (float)GameClock.StepHz;
            SaveResult sv2 = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string saved2 = Fingerprint(back);
            CampaignState back2 = Reload(Slot);
            InteriorSiteService.ResumeAll(back2);
            float leftLoaded = (EmberHostBoss.Record(back2).MeltdownEndTick - GameClock.Ticks) / (float)GameClock.StepHz;
            Expect(sv2.Success && Fingerprint(back2) == saved2 && EmberHostBoss.PhaseOf(back2) == EmberPhase.Meltdown && Math.Abs(leftSaved - leftLoaded) < 1e-4f && leftLoaded > 30f,
                $"F2 阶段三中途存档：读档后熔毁倒计时剩 {leftLoaded:0.00} 秒（存档时 {leftSaved:0.00}），状态逐位一致");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── G 暂停 / 倍速 / 观察 ───────────────────────────────

        private static string RunBoss(float speed, bool pauseHalf, bool observe)
        {
            CampaignState s = NewWorld(42);
            Equip(s, Pierce, 0);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            ClearGuards(site);
            WorldView.Observe(observe ? Center : HomeValleyLayout.RegionId);
            long t0 = GameClock.Ticks;
            long lockAt = t0 + GameClock.StepHz * 25, spikeAt = t0 + GameClock.StepHz * 28, target = t0 + GameClock.StepHz * 40;
            GameClock.SetSpeed(speed);
            bool locked = false, spiked = false, pausedOk = true;
            int frames = 0;
            float leftAtPause = -1f;
            while (GameClock.Ticks < target && frames < 60000)
            {
                if (!locked && GameClock.Ticks >= lockAt)
                {
                    HitCore(site, 1e6f);
                    locked = true;
                }
                if (locked && !spiked && GameClock.Ticks >= spikeAt)
                {
                    foreach (EnemyEncounter sp in EmberHostBoss.Spikes)
                    {
                        site.Combat.TryDamageEnemy(sp.Id, 1e6f);
                    }
                    spiked = true;
                }
                if (pauseHalf && spiked && leftAtPause < 0f && EmberHostBoss.PhaseOf(s) == EmberPhase.Meltdown)
                {
                    GameClock.SetPaused(true);
                    string p0 = Fingerprint(s);
                    leftAtPause = (EmberHostBoss.Record(s).MeltdownEndTick - GameClock.Ticks) / (float)GameClock.StepHz;
                    for (int k = 0; k < 60; k++)
                    {
                        WorldSimulation.Frame(1f / 60f, target);
                    }
                    pausedOk = Fingerprint(s) == p0;
                    GameClock.SetPaused(false);
                }
                long limit = !locked ? lockAt : !spiked ? spikeAt : target;
                WorldSimulation.Frame(1f / 60f, limit);
                frames++;
            }
            GameClock.SetSpeed(1f);
            SignalCoverageService.OverrideForTests = null;
            return (pausedOk ? string.Empty : "PAUSE-MOVED ") + Fingerprint(s);
        }

        private static void CheckSpeedMatrixAndObservation()
        {
            string r05 = RunBoss(0.5f, false, true);
            string r1 = RunBoss(1f, true, true);
            string r2 = RunBoss(2f, false, true);
            string r3 = RunBoss(3f, false, true);
            string unobserved = RunBoss(1f, false, false);
            Expect(r05 == r1 && r1 == r2 && r2 == r3 && !r1.Contains("PAUSE") && unobserved == r1 && r1.StartsWith(((int)EmberPhase.Meltdown).ToString() + "|"),
                $"G1 B09 / FGR-BASE-021：余烬主机 40 游戏秒（升温两级、第 25 秒进阶段二封锁、第 28 秒拆完尖刺进熔毁倒计时）在 0.5x / 1x（倒计时中暂停 60 帧不走）/ 2x / 3x 下逐位相同" +
                $"（含倒计时剩余步数），镜头不看数据中心时也一样（{unobserved == r1}）：{r1}");
        }

        // ─────────────────────────────── H 室内教义自卫（FG-GAP-226）───────────────────────────────

        private static int Dummy(CombatKernel k, Vector2 pos, float hp) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = new double2(pos.x, pos.y), Home = new double2(pos.x, pos.y), Radius = 0.8f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = -1, BehaviorProfile = -1, Priority = 1,
        });

        private static void CheckInteriorSelfDefense()
        {
            CampaignState s = NewWorld(1);
            InteriorSiteController site = EnterCenter(s, 3, out int[] m, out _);
            ClearGuards(site);
            CombatSite c = site.Combat;
            CombatKernel k = c.Kernel;
            Vector2 basePos = site.ExitPoint + new Vector2(0f, 8f);
            // 机器 0：空闲；机器 1：被接入（不交给教义）；机器 2：右键移动途中。三者分开站，各自旁边一个假人；另放一个潜伏的假人。
            Vector2 p0 = basePos + new Vector2(-20f, 0f), p1 = basePos + new Vector2(20f, 0f), p2 = basePos + new Vector2(-20f, 14f);
            c.SetUnitPosition(MachineUnit(site, m[0]), p0);
            c.SetUnitPosition(MachineUnit(site, m[1]), p1);
            c.SetUnitPosition(MachineUnit(site, m[2]), p2);
            c.SetUnitFlag(MachineUnit(site, m[1]), CombatUnitFlags.Possessed, true);
            int idleFoe = Dummy(k, p0 + new Vector2(0f, 4f), 60f);
            int possFoe = Dummy(k, p1 + new Vector2(0f, 4f), 60f);
            int lurker = Dummy(k, p0 + new Vector2(4f, 0f), 60f);
            k.SetFlag(lurker, CombatUnitFlags.Stealthed, true);
            long engagements0 = c.SelfDefenseEngagements;
            WorldSimulation.StepMany(GameClock.StepHz); // 对账写标记（siege.sync_seconds）
            var dest = p2 + new Vector2(30f, 0f);
            int moveFoe = Dummy(k, p2 + new Vector2(12f, 4f), 60f);
            site.SquadCommands.DebugSelectMany(new[] { m[2] });
            site.SquadCommands.IssueMoveTo(dest, paused: false);
            int mover = MachineUnit(site, m[2]);
            bool engagedIdle = false, engagedMove = false, kept = true;
            for (int i = 0; i < GameClock.StepHz * 12; i++)
            {
                WorldSimulation.StepMany(1);
                engagedIdle |= c.IsSelfDefending(MachineUnit(site, m[0]));
                engagedMove |= c.IsSelfDefending(mover);
                bool has = c.TryGetCommand(mover, out CombatCommand cmd) && cmd.Kind != CombatCommandKind.None;
                kept &= !has || cmd.Kind == CombatCommandKind.Move;
            }
            float hpIdle = View(c, idleFoe).Health, hpPoss = View(c, possFoe).Health, hpLurk = View(c, lurker).Health, hpMove = View(c, moveFoe).Health;
            string hover = c.SelfDefenseHoverLine(MachineUnit(site, m[0])) ?? c.SelfDefenseHoverLine(mover) ?? string.Empty;
            Expect(engagedIdle && hpIdle < 60f && engagedMove && hpMove < 60f && kept && Math.Abs(hpPoss - 60f) < 1e-3f && Math.Abs(hpLurk - 60f) < 1e-3f
                   && c.SelfDefenseEngagements > engagements0,
                $"H1 FG-GAP-226 室内教义自卫（数据中心独立内核）：空闲机器射程内有敌人原地还手（假人 60 → {hpIdle:0.#}）；右键移动途中的机器路边还手（→ {hpMove:0.#}）且命令始终是移动（{kept}）；" +
                $"被接入的机器不交给教义（假人 {hpPoss:0.#} 不掉血）；潜伏敌人不被锁定（{hpLurk:0.#}）；对账日志记 {c.SelfDefenseEngagements - engagements0} 次开始自卫，悬停“{hover}”");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── I 反夺控正式入口（DEBT-EMG4FW01-01）───────────────────────────────

        private static void CheckFailsafeViaLockout()
        {
            CampaignState s = NewWorld(42);
            Equip(s, Pierce, 0);
            InteriorSiteController site = EnterCenter(s, 2, out int[] m, out _);
            ClearGuards(site);
            CombatSite c = site.Combat;
            CombatKernel k = c.Kernel;
            // 接入中的那台：武器程序带反夺控（fw_failsafe，读 evt.seize + hij.seized）；同组友机：中继模块 + [中继应答]（收听 0 号频道）。
            int a = MachineUnit(site, m[0]);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { "fw_failsafe" });
            CombatWeapon w = c.WithProgram(p, CombatSite.MachineWeaponFrom(p), null, out _, out _);
            k.SetUnitWeapon(a, c.WeaponIndex(w));
            c.SetUnitFlag(a, CombatUnitFlags.Possessed, true);
            s.SignalCore.UplinkMachineLogicId = m[0];
            int b = MachineUnit(site, m[1]);
            int listen = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_pingfire" }, FirmwareCarrier.Projectile, 0));
            k.SetUnitWeapon(b, k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Cooldown = 0.5f, Program = listen,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            }));
            k.SetUnitRelay(b, true);
            Vector2 posB = c.TryGetMachinePosition(m[0], out Vector2 pa) ? pa + new Vector2(3f, 0f) : site.ExitPoint;
            c.SetUnitPosition(b, posB);
            // 友机射程内放一个敌对假人：中继应答要有目标才放出子载荷（与 EMG4-FW-01 F1 同一前提）
            Dummy(k, posB + new Vector2(0f, 8f), 5000f);
            long sent0 = k.EmgBusStats.PingsSent, delivered0 = k.EmgBusStats.PingsDelivered, sub0 = k.EmgBusStats.SubSpawned;
            DriveToLockout(site, s);
            EmgInjectResult inj = SignalSeizeService.LastSlotInject;
            WorldSimulation.StepMany(3);
            long sent = k.EmgBusStats.PingsSent - sent0, delivered = k.EmgBusStats.PingsDelivered - delivered0, sub = k.EmgBusStats.SubSpawned - sub0;
            Expect(EmberHostBoss.PhaseOf(s) == EmberPhase.Lockout && inj == EmgInjectResult.Enqueued && sent >= 1 && delivered >= 1 && sub >= 1,
                $"I1 DEBT-EMG4FW01-01 反夺控正式入口：余烬主机阶段二封锁槽位（SignalSeizeService.BlockSlot 注入 evt.seize：{inj}）→ 接入中、装反夺控的机器向中继组广播 0 号频道 {sent} 次、" +
                $"装中继应答的友机收到 {delivered} 次并开火、放出子载荷 {sub} 份");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private static CampaignState NewWorldState(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgember-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            return s;
        }

        private static CampaignState NewWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            CampaignState s = CampaignState.CreateNew("fgember-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static string LatestDetail(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e.Type != null && e.Type.Id == typeId)?.Latest?.DetailText ?? string.Empty;

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgEmberHostSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
