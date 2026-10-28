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
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
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
    /// FG10-BOSS-01 洗礼塔（FG09 第 6 章“首领：洗礼塔”，FGR-FAC-006；FGT-FAC-003；卡片负向“把小兵引进反应区（借力打力是有意设计）”“全灭重试”）。
    /// 区域旅程片段（星球世界真实步进 + 真实机器走进湖心平台）：
    /// A 数据：反应塔（首领、固定环形喷淋架、耐久在状态机）+ 3 种泵站（油 / 酸 / 水）、调参（预警 ≥ 0.8）、读法注册、文本与通知类型。
    /// B 开战与灌注：第二幕机器进场 → 开战（部件进星球内核、反应塔冻不住、不怕酸）；落点先预警 ≥ 0.8 秒再落流体，站进去的机器沾上标签；打掉一座泵站后它不再灌。
    /// C 反应阶段：耐久打到门槛停在门槛上进阶段二（一发巨额伤害不跳阶段），召来一波单位；主动反应先预警 ≥ 0.8 秒，再在机器最多的那片流体上点燃 / 通电——
    ///   站在里面的敌方单位同样起反应（借力打力）。
    /// D 泄压：封闭时伤害 × 0.5 且冻不住（先冻再烧打不出热震），泄压时伤害 × 2 且能冻住（热震打得出来）。
    /// E 击败：奖励（反应增幅 / 流体类固件 / 三选一）只发一次，澄净领地平定（主线“反应核”），反应塔从内核撤走；HUD 显示名称、阶段与分阶段血条。
    /// F 场地边界（FGR-FAC-006）：机器全部离开场地（含全灭）满 20 秒 → 阶段不变、耐久回到本阶段开始时（灌注阶段泵站修回）、波次撤走、待命不挨打；
    ///   机器回来接着打本阶段（反应阶段重新召波次）。F3 全灭后读首领战前自动存档（BossEngageEnter）→ 首领沉寂、再进场重新开战。
    /// B4 正式伤害链（内核伤害区域 → 伤害请求 → ApplyDamage）；C0b 首领战与突袭交错的音乐 / 提示音；E2 奖励不重复。
    /// C0（本机验证补）：首领占位音乐随开战 / 击败切换（RaidAudioDirector.PhaseBoss）；阶段转换提示音 + 字幕 + 通知；非法跳阶段被唯一写入口拒绝。
    /// G 存读档：首领战中存档读回阶段 / 耐久 / 泵站 / 流体 / 计时完全一致、不重新开战、能接着打；旧档缺首领记录读回为沉寂。
    /// H 暂停与 0.5x～3x：预警按游戏步数、真实时长 = 步数 ÷ 倍速；暂停时世界与离场计时都不走。P 性能：首领战整步耗时（只测一次）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgBaptismTowerSelfCheck）。
    /// </summary>
    public static class FgBaptismTowerSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/FG 洗礼塔")]
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
            Line("\n[洗礼塔] 三阶段（灌注 / 反应 / 泄压）、泵站、主动反应与借力打力、热震、奖励与平定、复位重试、HUD（FG10-BOSS-01）");
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
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbaptism-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameRoot.BindWorldProviders();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();
                BaptismTowerBoss.ResetForTests();
                BaptismTowerBoss.SaveOnEngage = false;

                Step(CheckData);
                Step(CheckFight);
                Step(CheckArenaReset);
                Step(CheckRetryFromAutosave);
                Step(CheckSaveLoad);
                Step(CheckPauseSpeed);
                Step(CheckPerf);
            }
            finally
            {
                BaptismTowerBoss.SaveOnEngage = true;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                SignalUplinkService.RealTimeForTests = null;
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
            Line(_fail == 0 ? $"  洗礼塔：全部 {_pass} 项通过" : $"  洗礼塔：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            MechEnemy tower = BaptismTowerBoss.TowerRow();
            List<MechEnemy> pumps = BaptismTowerBoss.PumpRows();
            bool towerOk = tower != null && tower.Tier == "boss" && EnemyDefs.HasTag(tower, EnemyDefs.TagExternalHealth) && tower.Behavior == EnemyDefs.BehaviorRingSpray
                           && EnemyBehaviorReadings.TryGet(tower.Behavior, out _) && tower.WarnSeconds >= 0.8f && tower.Components.StartsWith("fw_amplify", StringComparison.Ordinal);
            bool pumpsOk = pumps.Select(p => p.StatusTag).SequenceEqual(new[] { "Oil", "Acid", "Wet" });
            bool warn = BaptismTowerBoss.FillWarnSeconds >= 0.8f && BaptismTowerBoss.ReactWarnSeconds >= 0.8f && BaptismTowerBoss.VentWarnSeconds >= 0.8f;
            bool texts = new[] { "boss.clarity.notify.engaged", "boss.clarity.notify.retry", "boss.clarity.hud.title", "enemy.baptism_tower.name", "enemy.baptism_tower.weakness" }
                .All(GameText.Has) && NotificationCatalog.TryGetType(BaptismTowerBoss.NotifyType, out _);
            Expect(towerOk && pumpsOk && warn && texts,
                $"A1 反应塔（首领、耐久在状态机、固定环形喷淋架、预警 {tower?.WarnSeconds} 秒、奖励 {tower?.Components}）；泵站 {string.Join(" / ", pumps.Select(p => p.StatusTag))}；" +
                $"灌注 / 反应 / 泄压预警 {BaptismTowerBoss.FillWarnSeconds} / {BaptismTowerBoss.ReactWarnSeconds} / {BaptismTowerBoss.VentWarnSeconds} 秒；文本与通知类型（{texts}）");
        }

        // ─────────────────────────────── B～E 一场完整的首领战 ───────────────────────────────

        private static void CheckFight()
        {
            CampaignState s = NewWorld(42);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (!BaptismTowerBoss.TryArena(s, out Vector2 center) || home == null)
            {
                Fail("B0 夹具：没有湖心平台锚点或星球战斗内核");
                return;
            }
            // 场外先放一台机器：不开战。
            int m = Spawn(s, center + new Vector2(BaptismTowerBoss.ArenaRadius + 30f, 0f));
            StepSeconds(2);
            bool quietOutside = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Dormant;
            IntelService.EnsureState(s);
            bool intelBefore = IntelService.NextBoss(s, GameClock.Ticks)?.Id == IntelCatalog.BossIdBaptismTower;
            RaidAudioDirector.ResetSession();
            RaidAudioDirector.Tick(s, true, 1f, force: true);
            string musicBefore = RaidAudioDirector.CurrentMusic;
            MoveTo(m, center + new Vector2(BaptismTowerBoss.PumpRing * 0.5f, 0f));
            StepSeconds(2);
            RaidAudioDirector.Tick(s, true, 1f, force: true);
            string musicFight = RaidAudioDirector.CurrentMusic;
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            bool engaged = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Fill && BaptismTowerBoss.Engages == 1 && r.Attempt == 1;
            bool parts = home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out int towerUnit)
                         && Enumerable.Range(0, BaptismTowerBoss.PumpCount).All(i => home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(i), out _));
            bool sealedImmune = home.TryGetClarity(towerUnit, out CombatClarityUnit tc) && (tc.ImmuneMask & Bit("Frozen")) != 0u && (tc.ImmuneMask & Bit("Acid")) != 0u;
            Expect(quietOutside && engaged && parts && sealedImmune,
                $"B1 场外机器不开战（{quietOutside}）；走进湖心平台 → 开战（灌注阶段，第 {r.Attempt} 次挑战），反应塔与 4 座泵站进星球战斗内核（{parts}），反应塔不怕酸、此刻冻不住（{sealedImmune}）");

            // B1b DEBT-FG10BOSS01-03：遭遇过（开战过一次）之后监听站才排“首领弱点”破译，情报一行写洗礼塔的阶段门槛与弱点（数值取首领调参）。
            bool needs = IntelCatalog.TryGet(IntelCatalog.KindBoss, out IntelKindDef bossKind) && IntelService.Needed(s, bossKind, GameClock.Ticks, out _);
            bool nextIsTower = IntelService.NextBoss(s, GameClock.Ticks)?.Id == IntelCatalog.BossIdBaptismTower;
            IntelRecord weak = InjectBossIntel(s);
            string intelLine = IntelService.Summary(s, weak, GameClock.Ticks);
            bool intelText = intelLine.Contains(GameText.Get("intel.boss.baptism_tower.name")) && intelLine.Contains(Mathf.RoundToInt(BaptismTowerBoss.Phase2HpFraction * 100f) + "%")
                             && intelLine.Contains("×" + BaptismTowerBoss.VentDamageMult.ToString("0.#")) && intelLine.Contains(GameText.Get("intel.boss.baptism_tower.advice"));
            bool notAgain = IntelService.NextBoss(s, GameClock.Ticks) == null;
            Expect(!intelBefore && needs && nextIsTower && intelText && notAgain,
                $"B1b 监听站首领弱点情报（DEBT-FG10BOSS01-03）：开战前不排洗礼塔（{!intelBefore}）；遭遇过后要破译、下一个首领是洗礼塔（{needs}/{nextIsTower}）；" +
                $"情报一行写阶段门槛 / 泄压增伤 / 应对（{intelText}：“{intelLine.Replace("\n", " ")}”）；已有有效情报不再重复排（{notAgain}）");

            // B2 灌注：落点先预警 ≥ 0.8 秒再落，站进去的机器沾上标签。
            long warnAt = -1, pourAt = -1;
            int fills0 = BaptismTowerBoss.FillsPoured;
            for (int i = 0; i < GameClock.StepHz * 12 && pourAt < 0; i++)
            {
                WorldSimulation.StepMany(1);
                if (warnAt < 0 && r.FillPump >= 0)
                {
                    warnAt = GameClock.Ticks;
                }
                if (BaptismTowerBoss.FillsPoured > fills0)
                {
                    pourAt = GameClock.Ticks;
                }
            }
            ClarityBossFill first = r.Active.LastOrDefault();
            bool warned = warnAt > 0 && pourAt > warnAt && (pourAt - warnAt) >= GameClock.TicksFor(0.8);
            bool tagged = false;
            if (first != null)
            {
                MoveTo(m, new Vector2(first.X, first.Y));
                StepSeconds(1.5f);
                tagged = home.TryGetMachineUnit(m, out int mu) && home.Kernel.TryGetStatus(mu, out uint mask, out _, out _, out _, out _) && (mask & Bit(first.Tag)) != 0u;
            }
            Expect(warned && first != null && tagged,
                $"B2 灌注：落点先亮起 {(pourAt - warnAt) / (float)GameClock.StepHz:0.00} 秒（≥ 0.8）再落下一片{first?.Tag}；机器站进去沾上这个标签（{tagged}）");

            // B3 打掉一座泵站：它的流体不再灌入。
            home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(0), out int pump0);
            home.Kernel.Kill(pump0, 0);
            StepSeconds(1);
            bool pumpDead = BaptismTowerBoss.Pump(s, 0)?.IsAlive == false;
            var used = new HashSet<int>();
            for (int i = 0; i < GameClock.StepHz * 30; i++)
            {
                WorldSimulation.StepMany(1);
                if (r.FillPump >= 0)
                {
                    used.Add(r.FillPump);
                }
            }
            Expect(pumpDead && used.Count > 0 && !used.Contains(0),
                $"B3 泵站 0（{BaptismTowerBoss.FluidOf(s, 0)}）被打掉 → 之后的灌注只轮到 {string.Join("、", used.OrderBy(x => x))} 号泵站");

            // B4 正式伤害链（FGT-FAC-003）：内核里的伤害区域命中反应塔 → CombatSite 发伤害请求 → HomeValleyCombatRules → BaptismTowerBoss.ApplyDamage，
            //    耐久在状态机里扣；内核单位带 ExternalHealth、不会被内核自己打死。
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            float hpChain0 = tower.Health;
            bool external = home.Kernel.TryGetUnit(towerUnit, out CombatUnitView tv) && (tv.Flags & CombatUnitFlags.ExternalHealth) != 0;
            home.SpawnNeutralField(BaptismTowerBoss.PartPosition(s, BaptismTowerBoss.TowerInstanceId), 3f, 2f, tower.MaxHealth * 0.05f, 0u, 0f, 0f, CombatZoneLook.Pulse);
            StepSeconds(3f);
            bool chainHit = tower.Health < hpChain0 - tower.MaxHealth * 0.02f && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Fill
                            && home.Kernel.TryGetUnit(towerUnit, out CombatUnitView tv2) && (tv2.Flags & CombatUnitFlags.Alive) != 0;
            Expect(external && chainHit,
                $"B4 正式伤害链：内核伤害区域命中反应塔 → 状态机耐久 {hpChain0:0} → {tower.Health:0}（{chainHit}），内核单位带 ExternalHealth（{external}）且没被内核打死");

            // C1 阶段二：一发巨额伤害只打到门槛（不跳阶段），召来一波单位。
            int phaseCues0 = FeedbackCues.CountOf(FeedbackCueId.BossPhase);
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 5f);
            string reactText = GameText.Format("boss.clarity.notify.phase", BaptismTowerBoss.PhaseName(BaptismPhase.React));
            bool prompt = FeedbackCues.CountOf(FeedbackCueId.BossPhase) == phaseCues0 + 1 && Told(reactText) && Told(GameText.Get("boss.clarity.notify.phase_react"));
            // FGT-FAC-003 负向：阶段只能沿 灌注 → 反应 → 泄压 → 击败 走唯一写入口；跳阶段 / 倒退都被拒绝，阶段不变。
            int rejected0 = BaptismTowerBoss.RejectedTransitions;
            bool illegal = !BaptismTowerBoss.TryTransition(s, BaptismPhase.Defeated) & !BaptismTowerBoss.TryTransition(s, BaptismPhase.Fill)
                           && BaptismTowerBoss.RejectedTransitions == rejected0 + 2 && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React;
            bool clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Raw/Audios/Sfx/" + RaidAudioDirector.MusicBoss + ".wav") != null;
            Expect(musicBefore.Length == 0 && musicFight == RaidAudioDirector.MusicBoss && clip && prompt && illegal,
                $"C0 首领音乐：开战前无音乐层（“{musicBefore}”）、开战后切到首领占位音乐（“{musicFight}”，资源在 {clip}）；阶段转换提示：反应阶段的提示音 + 字幕 + 通知（{prompt}）；" +
                $"非法跳阶段（反应 → 击败 / 反应 → 灌注）被拒绝、阶段不变（{illegal}）");
            CheckAudioInterleave();
            bool atGate = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React && Mathf.Approximately(tower.Health, tower.MaxHealth * BaptismTowerBoss.Phase2HpFraction);
            // 复核修复的回归：召来的单位要留得住（攻城服务每 0.5 秒清一次无主队伍，洗礼塔的那一波由 BaptismTowerBoss.OwnsKey 认领）。
            StepSeconds(2);
            bool wave = r.WaveSpawned && r.WaveUnits.Length > 0
                        && r.WaveUnits.All(u => home.Kernel.TryGetUnit(u, out CombatUnitView wv) && (wv.Flags & CombatUnitFlags.Alive) != 0);
            Expect(atGate && wave,
                $"C1 一发 5 倍耐久的伤害只打到阶段二门槛（耐久 {tower.Health:0} / {tower.MaxHealth:0}，进入{BaptismTowerBoss.PhaseName(BaptismTowerBoss.PhaseOf(s))}），召来 {r.WaveUnits.Length} 台澄净单位");

            // C2 主动反应：预警 ≥ 0.8 秒；落在机器最多的那片流体上；站在里面的敌方单位同样起反应（借力打力）。
            ClarityBossFill target = r.Active.LastOrDefault(f => f.Tag == "Oil") ?? r.Active.LastOrDefault();
            int bait = 0;
            if (target != null)
            {
                MoveTo(m, new Vector2(target.X, target.Y));
                bait = home.Kernel.Spawn(Hostile(new double2(target.X + 0.5f, target.Y)));
            }
            long reactWarn = -1, reactAt = -1;
            int reactions0 = BaptismTowerBoss.ReactionsTriggered;
            int pickedSerial = -1;
            string trigger = target != null ? BaptismTowerBoss.TriggerOf(target.Tag) : "Fire";
            for (int i = 0; i < GameClock.StepHz * 20 && reactAt < 0; i++)
            {
                WorldSimulation.StepMany(1);
                if (reactWarn < 0 && r.ReactFill >= 0)
                {
                    reactWarn = GameClock.Ticks;
                    pickedSerial = r.ReactFill;
                }
                if (BaptismTowerBoss.ReactionsTriggered > reactions0)
                {
                    reactAt = GameClock.Ticks;
                }
            }
            StepSeconds(1);
            bool baitHit = bait != 0 && home.Kernel.TryGetStatus(bait, out uint bm, out _, out _, out _, out _) && (bm & Bit(trigger)) != 0u;
            Expect(target != null && reactWarn > 0 && reactAt - reactWarn >= GameClock.TicksFor(0.8) && pickedSerial == target.Serial && baitHit,
                $"C2 主动反应：预警 {(reactAt - reactWarn) / (float)GameClock.StepHz:0.00} 秒（≥ 0.8），挑中机器站着的那片{target?.Tag}（{pickedSerial == target?.Serial}）并{(trigger == "Shock" ? "通电" : "点燃")}；" +
                $"站在里面的敌方单位同样挨到（借力打力，{baitHit}）");

            // D 泄压：封闭时减伤且冻不住，泄压时增伤且能冻住（热震打得出来）。
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            bool vent = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Vent && Mathf.Approximately(tower.Health, tower.MaxHealth * BaptismTowerBoss.Phase3HpFraction);
            float sealedMult = BaptismTowerBoss.DamageMult(s);
            int shockIndex = RuleIndex("reaction_thermalshock");
            int shock0 = home.Kernel.ReactionCountOf(shockIndex);
            bool frozeSealed = FreezeThenBurn(home, towerUnit);
            int shockSealed = home.Kernel.ReactionCountOf(shockIndex) - shock0;
            int vents0 = BaptismTowerBoss.VentsOpened;
            for (int i = 0; i < GameClock.StepHz * 20 && BaptismTowerBoss.VentsOpened == vents0; i++)
            {
                WorldSimulation.StepMany(1);
            }
            float ventMult = BaptismTowerBoss.DamageMult(s);
            bool frozenOk = home.TryGetClarity(towerUnit, out CombatClarityUnit vc) && (vc.ImmuneMask & Bit("Frozen")) == 0u;
            int shock1 = home.Kernel.ReactionCountOf(shockIndex);
            bool frozeVent = FreezeThenBurn(home, towerUnit);
            int shockVent = home.Kernel.ReactionCountOf(shockIndex) - shock1;
            Expect(vent && Mathf.Approximately(sealedMult, BaptismTowerBoss.SealedDamageMult) && !frozeSealed && shockSealed == 0 && Mathf.Approximately(ventMult, BaptismTowerBoss.VentDamageMult)
                   && frozenOk && frozeVent && shockVent >= 1,
                $"D1 泄压阶段：封闭时伤害 × {sealedMult}、冻不住（挂上冰冻 {frozeSealed}）、火烧不出热震（{shockSealed} 次）；泄压时伤害 × {ventMult}、能冻住（{frozeVent}）、再烧打出热震（{shockVent} 次）");

            // E 击败：奖励一次、领地平定、HUD。
            bool hud = BaptismTowerHud.Compose(s, out string title, out float hpPct, out float mark, out string hpLine, out string lines)
                       && title.Contains(BaptismTowerBoss.PhaseName(BaptismPhase.Vent)) && hpPct > 0f && lines.Length > 0;
            int grants0 = BaptismTowerBoss.RewardGrants;
            int packets0 = LootPickService.Count(s);
            int core0 = HomeInventory.Stock(s, "reactor_core");
            string[] rewardFw = BaptismTowerBoss.TowerRow().Components.Split(',').Select(x => x.Trim()).ToArray();
            bool fluidBefore = rewardFw.Length > 1 && FirmwareLibrary.IsObtained(s, rewardFw[1]);
            for (int i = 0; i < 4 && !BaptismTowerBoss.IsDefeated(s); i++)
            {
                BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            }
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            StepSeconds(1);
            bool defeated = BaptismTowerBoss.IsDefeated(s) && BaptismTowerBoss.RewardGrants == grants0 + 1 && !home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out _);
            bool fw = FirmwareLibrary.IsObtained(s, "fw_amplify") && rewardFw.Length > 1 && !fluidBefore && FirmwareLibrary.IsObtained(s, rewardFw[1]);
            LootPackageRecord packet = LootPickService.Packets(s).LastOrDefault();
            bool pick = LootPickService.Count(s) == packets0 + 1 && packet?.Source == LootPickService.SourceBoss && packet.Faction == EnemyDefs.FactionClarity && packet.Candidates.Length == 3;
            bool core = HomeInventory.Stock(s, "reactor_core") == core0 + 1;
            bool pacified = TerritoryControlService.Find(s, EnemyDefs.FactionClarity)?.LeaderDefeated == true;
            RaidAudioDirector.Tick(s, true, 1f, force: true);
            bool musicOff = RaidAudioDirector.CurrentMusic.Length == 0;
            IntelService.Step(s, 1);
            bool intelGone = !IntelService.HasValid(s, IntelCatalog.KindBoss, IntelCatalog.BossIdBaptismTower, GameClock.Ticks, out _) && IntelService.NextBoss(s, GameClock.Ticks) == null;
            Expect(intelGone, $"E1b 击败后洗礼塔的弱点情报标已过时、不再排破译（{intelGone}）");
            Expect(hud && defeated && fw && pick && core && pacified && musicOff,
                $"E1 HUD 显示名称与阶段、分阶段血条（“{title}”，{hpLine}）；击毁 → 奖励只发一次：核心固件反应增幅 + 此前没有的流体类固件 {(rewardFw.Length > 1 ? rewardFw[1] : "无")}（{fw}）、" +
                $"一个来源为首领的三选一数据包（{pick}）、关键材料反应核进核心保管库（{core0} → {HomeInventory.Stock(s, "reactor_core")}）；反应塔撤出内核、澄净领地平定（{pacified}）、首领音乐停下（{musicOff}）");

            // E2 奖励不重复：已击败的首领再挨打 / 再有机器进场，什么都不再发。
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            StepSeconds(2);
            Expect(BaptismTowerBoss.RewardGrants == grants0 + 1 && LootPickService.Count(s) == packets0 + 1 && HomeInventory.Stock(s, "reactor_core") == core0 + 1 && BaptismTowerBoss.Engages == 1,
                $"E2 击败后再挨打 / 机器仍在场：奖励不再发（发放 {BaptismTowerBoss.RewardGrants - grants0} 次，数据包 {LootPickService.Count(s) - packets0} 个），不重新开战");
        }

        // ─────────────────────────────── F 复位 ───────────────────────────────

        private static void CheckArenaReset()
        {
            CampaignState s = NewWorld(7);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            BaptismTowerBoss.TryArena(s, out Vector2 center);
            int m = Spawn(s, center + new Vector2(5f, 0f));
            StepSeconds(2);
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            bool engaged = BaptismTowerBoss.IsEngaged(s) && r.Attempt == 1;
            int engages0 = BaptismTowerBoss.Engages;

            // F1 灌注阶段离场：反应塔掉了一截、泵站 0 被打掉 → 满 20 秒复位：阶段不变、耐久回满、泵站修回；等机器回来期间不灌注、首领音乐不放。
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 0.1f);
            home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(0), out int pump0);
            home.Kernel.Kill(pump0, 0);
            StepSeconds(1);
            int resets0 = BaptismTowerBoss.Resets;
            MoveTo(m, center + new Vector2(BaptismTowerBoss.ArenaRadius + 40f, 0f));
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds - 5f);
            bool notYet = BaptismTowerBoss.IsEngaged(s) && BaptismTowerBoss.ArenaLeaveSecondsLeft(s) > 0f && BaptismTowerBoss.Resets == resets0;
            StepSeconds(8f);
            bool reset1 = BaptismTowerBoss.Resets == resets0 + 1 && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Fill && r.ArenaIdle && r.LastReachedPhase == (int)BaptismPhase.Fill
                          && Mathf.Approximately(tower.Health, tower.MaxHealth) && BaptismTowerBoss.Pump(s, 0)?.IsAlive == true && PartsAlive(home) == "11111";
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == BaptismTowerBoss.NotifyType);
            string expected = GameText.Format(r.RetrySaved ? "boss.clarity.notify.retry" : "boss.clarity.notify.retry_nosave", BaptismTowerBoss.PhaseName(BaptismPhase.Fill));
            bool told = (note?.Latest?.DetailText ?? note?.Text ?? string.Empty).Contains(expected);
            int fills0 = BaptismTowerBoss.FillsPoured;
            int resetsIdle = BaptismTowerBoss.Resets;
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds + 5f);
            bool idle = BaptismTowerBoss.FillsPoured == fills0 && BaptismTowerBoss.Resets == resetsIdle && r.FillPump < 0 && !BaptismTowerBoss.IsFighting(s)
                        && RaidAudioDirector.Evaluate(s) != RaidAudioDirector.PhaseBoss;
            MoveTo(m, center + new Vector2(5f, 0f));
            StepSeconds(2);
            bool resumed = BaptismTowerBoss.IsFighting(s) && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Fill && BaptismTowerBoss.Engages == engages0 && r.Attempt == 1;
            Expect(engaged && notYet && reset1 && told && idle && resumed,
                $"F1 FGR-FAC-006 场地边界（灌注阶段）：离开场地 {BaptismTowerBoss.ArenaLeaveSeconds} 秒内不复位（{notYet}），满了 → 阶段不变、耐久回到本阶段开始时（满）、被打掉的泵站修回（{reset1}），" +
                $"通知如实写明有没有首领战前的自动存档可回（{told}）；没人回来时首领原地待命：不灌注、不再计时、首领音乐不放（{idle}）；机器回来接着打本阶段，不算新的一次挑战（{resumed}）");

            // F1b 反应阶段离场：耐久回到阶段二门槛（不回满、不退回灌注），召来的单位撤走、回来时重新召；反应阶段打掉的泵站保持打掉。
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth); // → 反应阶段门槛
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 0.1f);
            StepSeconds(1);
            bool waveUp = r.WaveSpawned && r.WaveUnits.Length > 0;
            home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(1), out int pump1);
            home.Kernel.Kill(pump1, 0);
            StepSeconds(1);
            int resets1 = BaptismTowerBoss.Resets;
            MoveTo(m, center + new Vector2(BaptismTowerBoss.ArenaRadius + 40f, 0f));
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds + 3f);
            bool reset2 = BaptismTowerBoss.Resets == resets1 + 1 && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React && r.ArenaIdle
                          && Mathf.Approximately(tower.Health, tower.MaxHealth * BaptismTowerBoss.Phase2HpFraction) && !r.WaveSpawned && r.WaveUnits.Length == 0
                          && BaptismTowerBoss.Pump(s, 1)?.IsAlive == false;
            string diag2 = $"复位 {BaptismTowerBoss.Resets - resets1} 次、阶段 {BaptismTowerBoss.PhaseOf(s)}、待命 {r.ArenaIdle}、波次 {r.WaveSpawned}/{r.WaveUnits.Length}、泵站 1 {BaptismTowerBoss.Pump(s, 1)?.IsAlive}、场内 {BaptismTowerBoss.MachinesInArena(s, center)}、机器活着 {MachineRegistry.TryGetRecord(m, out MachineRecord mr) && mr.IsAlive}";
            MoveTo(m, center + new Vector2(5f, 0f));
            StepSeconds(2);
            bool waveAgain = BaptismTowerBoss.IsFighting(s) && r.WaveSpawned && r.WaveUnits.Length > 0
                             && r.WaveUnits.All(u => home.Kernel.TryGetUnit(u, out CombatUnitView wv) && (wv.Flags & CombatUnitFlags.Alive) != 0);
            Expect(waveUp && reset2 && waveAgain,
                $"F1b 场地边界（反应阶段）：满 {BaptismTowerBoss.ArenaLeaveSeconds} 秒 → 仍是反应阶段、耐久回到阶段二门槛 {tower.Health:0} / {tower.MaxHealth:0}、召来的单位撤走、打掉的泵站 1 不复活（{reset2}：{diag2}）；" +
                $"机器回来 → 重新召来 {r.WaveUnits.Length} 台（{waveAgain}）");

            // F2 卡片负向“全灭”：场上的机器真被打光（不是走开）→ 同样满 20 秒复位到本阶段开始时；新机器进场接着打。从头重试见 F3（首领战前自动存档）。
            int resets2 = BaptismTowerBoss.Resets;
            bool killed = MachineRegistry.MarkDeadByLogicId(m).Success; // 机器阵亡的唯一翻转点（内核里的死亡只是它的回声，CombatSite.cs）
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 0.1f);
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds + 2f);
            bool wipeReset = BaptismTowerBoss.Resets == resets2 + 1 && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React && r.ArenaIdle
                             && Mathf.Approximately(tower.Health, tower.MaxHealth * BaptismTowerBoss.Phase2HpFraction);
            string diag3 = $"复位 {BaptismTowerBoss.Resets - resets2} 次、阶段 {BaptismTowerBoss.PhaseOf(s)}、待命 {r.ArenaIdle}、耐久 {tower.Health:0}、场内 {BaptismTowerBoss.MachinesInArena(s, center)}";
            int m2 = Spawn(s, center + new Vector2(5f, 0f));
            StepSeconds(2);
            bool retry = m2 != 0 && BaptismTowerBoss.IsFighting(s) && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React && r.Attempt == 1;
            Expect(killed && wipeReset && retry,
                $"F2 负向“全灭”：场上机器被击毁 → 满 {BaptismTowerBoss.ArenaLeaveSeconds} 秒复位到反应阶段开始时（{wipeReset}：{diag3}）；新机器进场接着打（{retry}）");
        }

        // ─────────────────────────────── F3 全灭后从首领战前自动存档重试（FGR-FAC-006 / FGT-FAC-004 / 卡片负向“全灭重试”）───────────────────────────────

        private static void CheckRetryFromAutosave()
        {
            CampaignState s = NewWorld(9);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            BaptismTowerBoss.TryArena(s, out Vector2 center);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            if (File.Exists(slotFile))
            {
                File.Delete(slotFile);
            }
            int m;
            BaptismTowerBoss.SaveOnEngage = true;
            try
            {
                m = Spawn(s, center + new Vector2(BaptismTowerBoss.ArenaRadius + 30f, 0f));
                MoveTo(m, center + new Vector2(5f, 0f));
                StepSeconds(2);
            }
            finally
            {
                BaptismTowerBoss.SaveOnEngage = false;
            }
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            long engageTick = r.EngagedTick;
            bool saved = BaptismTowerBoss.IsEngaged(s) && r.RetrySaved && r.Attempt == 1 && File.Exists(slotFile);
            string preFight = Path.Combine(_dir, "pre-baptism.json");
            if (File.Exists(slotFile))
            {
                File.Copy(slotFile, preFight, true);
            }
            // 打进反应阶段后全灭 → 复位通知走“可以从首领战前的自动存档重来”那一句。
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            StepSeconds(1);
            MachineRegistry.MarkDeadByLogicId(m);
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds + 2f);
            string retryText = GameText.Format("boss.clarity.notify.retry", BaptismTowerBoss.PhaseName(BaptismPhase.React));
            bool told = r.ArenaIdle && Told(retryText);
            // 读首领战前的自动存档（主菜单同一路径）：首领沉寂、还没开战（第 0 次挑战）、部件不在内核、奖励没发；
            // 存档写在开战那一步整步提交之后（RunAfterStep，ERD-SAV-002）：存档里的时钟 = 开战步。
            bool preOk = false, reEngage = false;
            string detail = "没有首领战前的存档";
            if (File.Exists(preFight))
            {
                File.Copy(preFight, slotFile, true);
                CampaignState l = LoadLikeMenu();
                if (l != null)
                {
                    ClarityBossRecord lr = BaptismTowerBoss.Record(l);
                    CombatSite lh = CombatSites.Get(HomeValleyLayout.RegionId);
                    long loadedTick = GameClock.Ticks;
                    preOk = lr.Phase == (int)BaptismPhase.Dormant && lr.Attempt == 0 && !lr.RewardsGranted && PartsAlive(lh) == "00000" && loadedTick == engageTick;
                    detail = $"阶段 {lr.Phase}、第 {lr.Attempt} 次、内核 {PartsAlive(lh)}、存档时钟 {loadedTick} / 开战步 {engageTick}";
                    int m2 = Spawn(l, center + new Vector2(BaptismTowerBoss.ArenaRadius + 30f, 0f));
                    MoveTo(m2, center + new Vector2(5f, 0f));
                    StepSeconds(2);
                    reEngage = BaptismTowerBoss.IsFighting(l) && BaptismTowerBoss.PhaseOf(l) == BaptismPhase.Fill && lr.Attempt == 1 && PartsAlive(lh) == "11111";
                }
            }
            Expect(saved && told && preOk && reEngage,
                $"F3 负向“全灭重试”：开战时写了首领战前自动存档（BossEngageEnter，{saved}）；反应阶段全灭 → 复位通知“可以从首领战前的自动存档重来”（{told}）；" +
                $"读这份存档 → 首领还没开战（{detail}，{preOk}）；机器再进场重新开战、部件回到内核（{reEngage}）");
        }

        // ─────────────────────────────── G 存读档（FG09 第 10 节“首领战中存档”、FGT-FAC-003）───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(11);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            BaptismTowerBoss.TryArena(s, out Vector2 center);
            Spawn(s, center + new Vector2(5f, 0f));
            StepSeconds(2);
            home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(1), out int pump1);
            home.Kernel.Kill(pump1, 0);
            StepSeconds(1);
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth); // 停在阶段二门槛
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 0.05f); // 阶段内再掉一截
            StepSeconds(8);
            string kernelBefore = PartsAlive(home);
            int engages = BaptismTowerBoss.Engages;
            long ticks = GameClock.Ticks;
            string before = Sig(s);
            SaveNow();
            CampaignState l = LoadLikeMenu();
            if (l == null)
            {
                return;
            }
            string after = Sig(l);
            StepSeconds(2);
            CombatSite lh = CombatSites.Get(HomeValleyLayout.RegionId);
            string inKernel = PartsAlive(lh);
            bool kernel = inKernel == "11011" && kernelBefore == "11011";
            bool noReEngage = BaptismTowerBoss.Engages == engages && BaptismTowerBoss.PhaseOf(l) == BaptismPhase.React;
            RegionEnemyRecord lt = BaptismTowerBoss.Tower(l);
            bool goesOn = lt != null && BaptismTowerBoss.ApplyDamage(l, lt, lt.MaxHealth).ok && BaptismTowerBoss.PhaseOf(l) == BaptismPhase.Vent;
            Expect(before == after && kernel && noReEngage && goesOn,
                $"G1 首领战中（反应阶段、泵站 1 已毁、场上有流体）存档 → 读档：阶段 / 耐久 / 场地状态完全一致（{(before == after ? before : before + " ≠ " + after)}，第 {ticks} 步）；" +
                $"反应塔与存活泵站回到内核、毁掉的泵站不复活（塔 + 泵站在内核里活着：存档前 {kernelBefore} / 读档后 {inKernel}）；不重新开战（{noReEngage}）；读档后接着打能进泄压（{goesOn}）");

            // G2 旧档没有首领记录（FG10 之前的存档）→ 读回是“沉寂、没挑战过”，不报错。
            CampaignState o = NewWorld(12);
            o.Factions.ClarityBoss = null;
            SaveNow();
            CampaignState ol = LoadLikeMenu();
            ClarityBossRecord orc = ol != null ? BaptismTowerBoss.Record(ol) : null;
            Expect(orc != null && orc.Phase == (int)BaptismPhase.Dormant && orc.Attempt == 0 && !orc.RewardsGranted,
                $"G2 旧档缺首领记录 → 读回为沉寂、第 0 次挑战、未发奖励（{orc?.Phase} / {orc?.Attempt}）");
        }

        /// <summary>C0b 首领战与家园突袭交错（ADR-FAC-005 第 6 节第 6 条优先级 突袭 &gt; 首领战 &gt; 预警；FG6-DEF-06 预警 / 突袭 / 结束提示音不被首领战吞掉）。</summary>
        private static void CheckAudioInterleave()
        {
            CampaignState s = CampaignSession.Current;
            RaidAudioDirector.ResetSession();
            var log = new List<string>();
            string Feed(int phase, bool warning)
            {
                int n0 = RaidAudioDirector.StingCount;
                RaidAudioDirector.ApplyEvaluated(phase, warning);
                string st = RaidAudioDirector.StingCount > n0 ? RaidAudioDirector.LastSting : "-";
                log.Add($"{phase}{(warning ? "+预警" : "")}→{RaidAudioDirector.CurrentMusic}/{st}");
                return st;
            }
            bool ok = Feed(RaidAudioDirector.PhaseBoss, false) == "-" && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicBoss;
            ok &= Feed(RaidAudioDirector.PhaseBoss, true) == RaidAudioDirector.StingWarning && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicBoss; // 首领战中来了预警：音乐不换、提示音响
            ok &= Feed(RaidAudioDirector.PhaseBoss, true) == "-"; // 同一条预警不重复响
            ok &= Feed(RaidAudioDirector.PhaseRaid, false) == RaidAudioDirector.StingRaid && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicRaid; // 突袭到达盖过首领战
            ok &= Feed(RaidAudioDirector.PhaseBoss, false) == RaidAudioDirector.StingEnd && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicBoss; // 突袭结束、首领战仍在
            ok &= Feed(RaidAudioDirector.PhaseWarning, true) == RaidAudioDirector.StingWarning && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicWarning; // 首领倒下时有新预警
            ok &= Feed(RaidAudioDirector.PhaseBoss, true) == "-" && Feed(RaidAudioDirector.PhaseWarning, true) == "-"; // 已响过的预警：进出首领战不重复响
            RaidAudioDirector.ResetSession();
            RaidAudioDirector.Tick(s, true, 1f, force: true);
            Expect(ok && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicBoss,
                $"C0b 首领战与突袭交错：{string.Join("；", log)}（突袭 > 首领战 > 预警，预警 / 突袭 / 结束提示音都不被首领战吞掉）");
        }

        /// <summary>反应塔 + 4 座泵站在战斗内核里是否活着（“1” = 活着）。</summary>
        private static string PartsAlive(CombatSite site) => site == null ? "无内核" : string.Join("",
            new[] { BaptismTowerBoss.TowerInstanceId }.Concat(Enumerable.Range(0, BaptismTowerBoss.PumpCount).Select(BaptismTowerBoss.PumpInstanceId))
                .Select(id => site.TryGetEnemyUnit(id, out int u) && site.Kernel.TryGetUnit(u, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0 ? "1" : "0"));

        private static string Sig(CampaignState s)
        {
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            RegionEnemyRecord t = BaptismTowerBoss.Tower(s);
            string pumps = string.Join("", Enumerable.Range(0, BaptismTowerBoss.PumpCount).Select(i => BaptismTowerBoss.Pump(s, i)?.IsAlive == true ? "1" : "0"));
            string fills = string.Join(",", r.Active.Select(f => $"{f.Serial}:{f.Tag}@{f.X:0.##},{f.Y:0.##}/{f.UntilTick}"));
            return $"阶段 {r.Phase} 第 {r.Attempt} 次 耐久 {t?.Health:0.##} 泵站 {pumps} 灌注 {r.Fills}/{r.PumpCursor}/{r.NextFillTick}/{r.FillPump} " +
                   $"反应 {r.Reactions}/{r.NextReactTick}/{r.ReactFill} 波次 {r.WaveSpawned}/{r.WaveUnits.Length} 场地 {r.OutsideSinceTick} 流体[{fills}]";
        }

        // ─────────────────────────────── H 暂停与 0.5x～3x ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            CampaignState s = NewWorld(13);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            BaptismTowerBoss.TryArena(s, out Vector2 center);
            int m = Spawn(s, center + new Vector2(5f, 0f));
            StepSeconds(2);
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            bool engaged = BaptismTowerBoss.IsEngaged(s);
            const float dt = 1f / 120f;
            var warnTicks = new List<long>();
            var parts = new List<string>();
            bool realOk = true;
            foreach (float speed in GameClock.Speeds)
            {
                GameClock.SetSpeed(speed);
                for (int g = 0; g < 120 * 20 && r.FillPump >= 0; g++)
                {
                    WorldSimulation.Frame(dt);
                }
                int fills0 = BaptismTowerBoss.FillsPoured;
                long wStart = -1;
                int fStart = 0;
                for (int f = 0; f < 120 * 40; f++)
                {
                    WorldSimulation.Frame(dt);
                    if (wStart < 0 && r.FillPump >= 0)
                    {
                        wStart = GameClock.Ticks;
                        fStart = f;
                    }
                    if (wStart >= 0 && BaptismTowerBoss.FillsPoured > fills0)
                    {
                        long wt = GameClock.Ticks - wStart;
                        float real = (f - fStart) * dt;
                        float expect = wt / (GameClock.StepHz * speed);
                        realOk &= Mathf.Abs(real - expect) <= 2f * dt + 1f / (GameClock.StepHz * speed);
                        warnTicks.Add(wt);
                        parts.Add($"{speed}x：{wt} 步 / 真实 {real:0.00} 秒");
                        break;
                    }
                }
            }
            bool sameTicks = warnTicks.Count == GameClock.Speeds.Length && warnTicks.Max() - warnTicks.Min() <= 1 && warnTicks.Min() >= GameClock.TicksFor(0.8);

            // 暂停：世界不走（步数、首领计时、流体、耐久都不变）。
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(true);
            string sig0 = Sig(s);
            long t0 = GameClock.Ticks;
            for (int f = 0; f < 600; f++)
            {
                WorldSimulation.Frame(dt);
            }
            bool frozen = GameClock.Ticks == t0 && Sig(s) == sig0;
            // 离场计时同样按游戏时间：开始计时后暂停 30 真实秒不复位；3x 下约 20 / 3 真实秒复位。
            GameClock.SetPaused(false);
            MoveTo(m, center + new Vector2(BaptismTowerBoss.ArenaRadius + 40f, 0f));
            for (int g = 0; g < 240 && r.OutsideSinceTick < 0; g++)
            {
                WorldSimulation.Frame(dt);
            }
            GameClock.SetPaused(true);
            for (int f = 0; f < 120 * 30; f++)
            {
                WorldSimulation.Frame(dt);
            }
            bool heldPaused = BaptismTowerBoss.IsEngaged(s) && r.OutsideSinceTick >= 0;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(3f);
            int resets0 = BaptismTowerBoss.Resets;
            int frames = 0;
            while (BaptismTowerBoss.Resets == resets0 && frames < 120 * 30)
            {
                WorldSimulation.Frame(dt);
                frames++;
            }
            float resetReal = frames * dt;
            float expectReset = BaptismTowerBoss.ArenaLeaveSeconds / 3f;
            bool resetOk = BaptismTowerBoss.Resets == resets0 + 1 && resetReal > expectReset - 0.6f && resetReal < expectReset + 0.6f;
            Expect(engaged && sameTicks && realOk && frozen && heldPaused && resetOk,
                $"H1 倍速矩阵：灌注预警按游戏时间（{string.Join("；", parts)}，步数一致 {sameTicks}、真实时长 = 步数 ÷ 倍速 {realOk}）；暂停 5 真实秒世界不走（{frozen}）；" +
                $"离场计时暂停 30 真实秒不复位（{heldPaused}），3x 下 {resetReal:0.00} 真实秒复位（期望约 {expectReset:0.00}）");
        }

        // ─────────────────────────────── P 性能（首领战整步）───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorld(17);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            BaptismTowerBoss.TryArena(s, out Vector2 center);
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                Spawn(s, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 10f);
            }
            StepSeconds(2);
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            if (tower != null)
            {
                BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth); // 进反应阶段：波次 + 灌注 + 主动反应同时在跑
            }
            StepSeconds(12);
            const int n = 600;
            var sw = new System.Diagnostics.Stopwatch();
            double total = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                sw.Restart();
                WorldSimulation.StepMany(1);
                double ms = sw.Elapsed.TotalMilliseconds;
                total += ms;
                max = Math.Max(max, ms);
            }
            double avg = total / n;
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            bool ok = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.React && r.WaveSpawned;
            // 120 帧 = 8.33 ms / 帧；3x 时每帧平均 1.5 步，世界模拟约占一半帧预算 → 每步 ≤ 2 ms。Editor batchmode 无渲染负载、Mono 未 IL2CPP，比真机偏慢。
            PerfGate.Expect(ok,
                $"P1 首领战整步（12 台机器 + 反应塔 + 泵站 + {r.WaveUnits.Length} 台召来单位 + {r.Active.Length} 片流体，{n} 步，Editor batchmode）：平均 {avg:0.000} ms / 最大 {max:0.000} ms",
                new[] { PerfGate.Le(avg, 2.0, "首领战每步平均 ms") }, Expect, Line);
        }

        private static void SaveNow()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("存档写入失败：" + r.Message);
            }
        }

        private static CampaignState LoadLikeMenu()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            SignalLinkService.ResetForTests();
            BaptismTowerBoss.SaveOnEngage = false;
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(Slot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return r.State;
        }

        private static bool Told(string text) => NotificationCenter.History.Any(n => n.Type != null && n.Type.Id == BaptismTowerBoss.NotifyType
                                                                                     && n.Members.Any(mb => (mb.DetailText ?? string.Empty).Contains(text)));

        // ─────────────────────────────── 工具 ───────────────────────────────

        /// <summary>先挂冰冻，再在它脚下铺一块会烧的区域（区域节拍带伤害，纯倍率反应才会结算——hit = 0 的直接挂标签不结算）。返回冰冻挂上了没有。</summary>
        private static bool FreezeThenBurn(CombatSite site, int unit)
        {
            float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
            site.Kernel.ApplyStatus(unit, Bit("Frozen"), 3f, 0f, 0f, 0f, 0);
            bool frozen = site.Kernel.TryGetStatus(unit, out uint mask, out _, out _, out _, out _) && (mask & Bit("Frozen")) != 0u;
            if (site.Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                site.Kernel.SpawnFieldZone(CombatFaction.Neutral, v.Position, 3f, 1.5f, 0f, Bit("Fire"), 3f, Mathf.Max(1f, fireDps), 0f, 0f);
            }
            StepSeconds(1f);
            return frozen;
        }

        private static int RuleIndex(string id)
        {
            IReadOnlyList<Reaction> rules = NamedReactionCatalog.TagRules;
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        private static CombatSpawn Hostile(double2 at) => new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Speed = 0f,
            Health = 1e6f,
            MaxHealth = 1e6f,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        };

        private static int Spawn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 5000f, 5000f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            WorldSimulation.StepMany(2);
            return r.Success ? r.LogicId : 0;
        }

        private static void MoveTo(int logicId, Vector2 at)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static void StepSeconds(float seconds) => WorldSimulation.StepMany(Mathf.Max(1, Mathf.RoundToInt(seconds * GameClock.StepHz)));

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
            AcidMarshService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgbaptism-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgBaptismTowerSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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

        private static IntelRecord InjectBossIntel(CampaignState s)
        {
            IntelState st = IntelService.StateOf(s);
            var rec = new IntelRecord
            {
                Serial = 9100, Kind = IntelCatalog.KindBoss, Subject = IntelCatalog.BossIdBaptismTower, Source = "post", ProducedTick = GameClock.Ticks,
                ExpiresTick = GameClock.Ticks + GameClock.TicksFor(3600.0),
            };
            st.Records = new List<IntelRecord>(st.Records ?? Array.Empty<IntelRecord>()) { rec }.ToArray();
            return rec;
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
