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
    /// F 复位：机器全部离开场地满 20 秒 → 部件回满、波次撤走、阶段 = 沉寂，记“上次止步”；再进场重新开战（第 2 次挑战）。
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
            MoveTo(m, center + new Vector2(BaptismTowerBoss.PumpRing * 0.5f, 0f));
            StepSeconds(2);
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            bool engaged = BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Fill && BaptismTowerBoss.Engages == 1 && r.Attempt == 1;
            bool parts = home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out int towerUnit)
                         && Enumerable.Range(0, BaptismTowerBoss.PumpCount).All(i => home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(i), out _));
            bool sealedImmune = home.TryGetClarity(towerUnit, out CombatClarityUnit tc) && (tc.ImmuneMask & Bit("Frozen")) != 0u && (tc.ImmuneMask & Bit("Acid")) != 0u;
            Expect(quietOutside && engaged && parts && sealedImmune,
                $"B1 场外机器不开战（{quietOutside}）；走进湖心平台 → 开战（灌注阶段，第 {r.Attempt} 次挑战），反应塔与 4 座泵站进星球战斗内核（{parts}），反应塔不怕酸、此刻冻不住（{sealedImmune}）");

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

            // C1 阶段二：一发巨额伤害只打到门槛（不跳阶段），召来一波单位。
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth * 5f);
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
            for (int i = 0; i < 4 && !BaptismTowerBoss.IsDefeated(s); i++)
            {
                BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            }
            BaptismTowerBoss.ApplyDamage(s, tower, tower.MaxHealth);
            StepSeconds(1);
            bool defeated = BaptismTowerBoss.IsDefeated(s) && BaptismTowerBoss.RewardGrants == grants0 + 1 && !home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out _);
            bool fw = FirmwareLibrary.IsObtained(s, "fw_amplify");
            bool pacified = TerritoryControlService.Find(s, EnemyDefs.FactionClarity)?.LeaderDefeated == true;
            Expect(hud && defeated && fw && pacified,
                $"E1 HUD 显示名称与阶段、分阶段血条（“{title}”，{hpLine}）；击毁 → 奖励只发一次（核心固件反应增幅 {fw}）、反应塔撤出内核、澄净领地平定（{pacified}）");
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
            bool engaged = BaptismTowerBoss.IsEngaged(s);
            int resets0 = BaptismTowerBoss.Resets;
            MoveTo(m, center + new Vector2(BaptismTowerBoss.ArenaRadius + 40f, 0f));
            StepSeconds(BaptismTowerBoss.ArenaLeaveSeconds - 5f);
            bool notYet = BaptismTowerBoss.IsEngaged(s) && BaptismTowerBoss.ArenaLeaveSecondsLeft(s) > 0f;
            StepSeconds(8f);
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            bool reset = BaptismTowerBoss.Resets == resets0 + 1 && BaptismTowerBoss.PhaseOf(s) == BaptismPhase.Dormant && r.LastReachedPhase == (int)BaptismPhase.Fill
                         && !home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out _) && BaptismTowerBoss.Tower(s)?.Health == BaptismTowerBoss.Tower(s)?.MaxHealth;
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == BaptismTowerBoss.NotifyType);
            string expected = GameText.Format(r.RetrySaved ? "boss.clarity.notify.retry" : "boss.clarity.notify.retry_nosave", BaptismTowerBoss.PhaseName(BaptismPhase.Fill));
            bool told = (note?.Latest?.DetailText ?? note?.Text ?? string.Empty).Contains(expected);
            MoveTo(m, center + new Vector2(5f, 0f));
            StepSeconds(2);
            bool again = BaptismTowerBoss.IsEngaged(s) && r.Attempt == 2;
            Expect(engaged && notYet && reset && told && again,
                $"F1 负向“全员离开 / 全灭”：离开场地 {BaptismTowerBoss.ArenaLeaveSeconds} 秒内不复位（{notYet}），满了 → 复位到开战前（部件回满、撤出内核，记“上次止步：灌注”），" +
                $"通知如实写明有没有首领战前的自动存档可回（{told}）；再进场重新开战（第 {r.Attempt} 次挑战）");
        }

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
