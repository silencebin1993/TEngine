using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.Objective;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-SIG-05 核心固件迁移与 AI 边界的自动验收（FG01 FGR-SIG-090；FG02 FGR-FW-003；FGT-SIG-010、011；卡片负向“AI 驾驶带接入口的机器”
    /// “机器被交还给 AI 时正处在过载状态”；Demo 内容迁移与可通关）。
    /// 全部用正式种类表（不注入），起真实系统：整个世界（家园 / 破碎都市 / 铸造前哨控制器 + 战斗内核 + 统一时钟）、真实接入入口（机器列表同一入口）、
    /// 真实蓝图保存（BlueprintEditorService.TrySave）、真实编队攻击命令、真实存档文件——行为坏了会失败：
    /// A 数据（6 条核心名单、目录里的核心固件、AI 许可、文本键）；B AI 边界（AI 驾驶带接入口的机器 / 旧档电路里的核心固件 / 敌方 AI，FGT-SIG-011 对照）；
    /// C 玩家接入触发（FGT-SIG-010：熔穿过载与标记跳转，正式表冷却生效）；D 交还 AI 时正处在过载（过热 / 瞄准中 / 刚发动）；
    /// E Demo 内容迁移（反应研究费、OBJ-06 / OBJ-08、核心门三灯、敌方适应）与可通关（AI 编队不靠熔穿过载打掉护甲机）；
    /// F 暂停与 0.5x～3x；G 存读档；H 观察无关；I 性能；J 界面文字。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgCoreFirmwareBoundarySelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_sig05_cannon_up";
        private const string BpCannonLegacy = "bp_selfcheck_sig05_cannon_legacy_overload";
        private const string BpGun = "bp_selfcheck_sig05_gun";
        private const string BpMarkUp = "bp_selfcheck_sig05_mark_up";
        private const float FrameDt = 0.05f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 核心固件与 AI 边界")]
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
            PerfLines.Clear();
            Line("\n[核心固件] 核心固件迁移与 AI 边界（FG1-SIG-05）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig05-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests(); // 正式种类表，不注入
                UplinkReactionReadiness.ResetForTests();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => FrameDt;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckAiBoundary);
                Step(CheckPlayerUplinkTriggers);
                Step(CheckMarkJumpUplink);
                Step(CheckHandoffWhileOverloaded);
                Step(CheckContentMigration);
                Step(CheckPassableWithoutOverload);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservationIndependence);
                Step(CheckPerformance);
                Step(CheckTexts);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"核心固件自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                SignalUplinkService.ResetForTests();
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                UplinkReactionReadiness.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                GameSettings.SetLanguage(originalLanguage);
                UiEscapeStack.Clear();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [核心固件] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据（FGR-FW-003）────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 6 条核心固件名单来自 fg.TbFirmwareKind；目录里已有的过载、标记跳转是核心、AI 许可为“仅玩家”");
            var expected = new Dictionary<string, string>
            {
                ["fw_overload"] = "过载", ["fw_capacitor"] = "电容蓄力", ["fw_marktag"] = "标记跳转",
                ["fw_swarm"] = "集群协议", ["fw_amplify"] = "反应增幅", ["fw_execute"] = "处决",
            };
            IReadOnlyList<string> roster = FirmwareKinds.CoreRosterIds;
            bool namesOk = roster.All(id =>
            {
                GameConfig.fg.FirmwareKind row = FirmwareKinds.Rows.FirstOrDefault(r => r.Id == id);
                return row != null && expected.TryGetValue(id, out string zh) && GameText.Get(row.NameKey, GameLanguage.ZhCn) == zh
                       && !string.IsNullOrEmpty(GameText.Get(row.NameKey, GameLanguage.En)) && GameText.Get(row.NameKey, GameLanguage.En) != zh;
            });
            Expect(roster.Count == 6 && new HashSet<string>(roster).SetEquals(expected.Keys) && namesOk,
                $"核心固件恰好 6 条（FGR-FW-003）：{string.Join("、", roster.Select(id => GameText.Get(FirmwareKinds.Rows.First(r => r.Id == id).NameKey)))}；中英文名都有");

            var catalogCore = FirmwareCatalog.All.Keys.Where(FirmwareKinds.IsCore).OrderBy(x => x, StringComparer.Ordinal).ToList();
            bool aiPermission = FirmwareCatalog.All.All(kv => FirmwareKinds.IsCore(kv.Key)
                ? kv.Value.AiPermission == MechanicalContentAiPermission.PlayerOnly
                : kv.Value.AiPermission != MechanicalContentAiPermission.PlayerOnly);
            // FG2-FW-01：44 条固件全部迁入目录，6 条核心固件的本体都在目录里（DEBT-FG1SIG05-01 关闭）。
            Expect(new HashSet<string>(catalogCore).SetEquals(expected.Keys) && catalogCore.Count == 6 && aiPermission
                   && catalogCore.All(id => FirmwareKinds.CoreCooldownSeconds(id) > 0f),
                $"固件目录里的核心固件 = 6 条（正式表，不注入）：{string.Join("、", catalogCore.Select(FirmwareKinds.DisplayName))}，冷却都 > 0 秒；" +
                "目录的 AI 许可与种类一致（核心 = 仅玩家，常规 ≠ 仅玩家）");

            string[] migrated = { FirmwareCatalog.FwCapacitorId, FirmwareCatalog.FwSwarmId, FirmwareCatalog.FwAmplifyId, FirmwareCatalog.FwExecuteId };
            Expect(migrated.All(id => FirmwareCatalog.TryGet(id, out _) && FirmwareKinds.KindOf(id) == FirmwareKind.Core
                                      && !FirmwareKinds.CanInstall(CampaignSession.Current, id, FirmwareHost.MachineCircuit, out _)
                                      && !FirmwareKinds.CanInstall(CampaignSession.Current, id, FirmwareHost.Turret, out _)
                                      && FirmwareKinds.CanInstall(CampaignSession.Current, id, FirmwareHost.SignalCore, out _)),
                "FG2-FW-01 迁入的 4 条（电容蓄力、集群协议、反应增幅、处决）在目录里即是核心：装不进机器电路和炮塔，只能放进信号核（FGT-FW-002）");
        }

        // ── B. AI 边界（FGR-SIG-090，FGT-SIG-011 对照）──────────────────────────

        private static void CheckAiBoundary()
        {
            Line("  · B. AI 永远不用核心固件、不填接入口：AI 驾驶带接入口的重炮机 / 旧档电路里残留过载的重炮机 / 敌方 AI，真开火对照");
            CampaignState s = NewState(8501);
            EquipChip(s, FirmwareCatalog.FwOverloadId, 0);
            int up = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-30f, -20f), 900f);
            int legacy = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonLegacy, new Vector2(-32f, -20f), 900f);
            FoundryOutpostController fo = OpenFoundry(s, up, legacy);
            WorldView.Observe(fo.SiteId);
            CombatSite site = fo.Combat;

            site.TryGetMachineWeapon(up, out MachineWeaponInfo upInfo);
            MachineCombatResolution upAi = MachineLoadoutRegistry.ResolveForAi(s, up, s.RandomSeed);
            MachineCombatResolution upPilot = MachineLoadoutRegistry.ResolveForPilot(s, up, s.RandomSeed);
            Expect(SignalCoreService.SlotContentId(s, 0) == FirmwareCatalog.FwOverloadId && !upInfo.Uplinked && !upInfo.ReactionGated
                   && WeaponOf(site, up).Mode == CombatWeaponMode.Cannon && WeaponOf(site, up).Reaction == CombatReaction.None
                   && upAi.Preview.UplinkFirmwareIds.Length == 0 && upPilot.Preview.UplinkFirmwareIds.Length == 0 && upPilot.Preview.ReactionId == null
                   && Mathf.Approximately(upAi.Preview.HeatBudget, 40f),
                "AI 驾驶带接入口的重炮机：信号核里有过载，但接入口按空槽（AI / 驾驶两个结算出口都没插入），内核武器没有反应、热量 40");

            MachineCombatResolution legacyAi = MachineLoadoutRegistry.ResolveForAi(s, legacy, s.RandomSeed);
            Expect(legacyAi.Success && legacyAi.Preview.ReactionId == null && legacyAi.Preview.InertCoreFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId })
                   && !legacyAi.Preview.FirmwareIds.Contains(FirmwareCatalog.FwOverloadId) && Mathf.Approximately(legacyAi.Preview.HeatBudget, 40f)
                   && WeaponOf(site, legacy).Reaction == CombatReaction.None,
                "旧档电路里残留过载的重炮机（绕过校验直接写进版本）：AI 驾驶时过载不生效（不产生反应、不加热量），编译结果列出它为“不生效的核心固件”");

            // 真开火：两台都由 AI 编队攻击命令打左护甲机正面——穿甲只有正面减伤，没有熔穿过载的 30% 穿甲、没有熔穿过载提示、冷却没开始。
            string left = FoundryOutpostLayout.ArmorBotLeftSpawnId;
            RegionEnemyRecord armor = PrepArmorTarget(s, site, left);
            Vector2 front = armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f;
            Place(site, up, front);
            Place(site, legacy, front + new Vector2(0.6f, 0f));
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            int fired0 = SignalUplinkService.CoreFiredCount;
            var hits = new List<float>();
            float heat0 = Heat(site, up);
            fo.SquadCommands.DebugSelectMany(new[] { up, legacy });
            fo.SquadCommands.IssueAttack(left, paused: false);
            CollectShotDamage(site, armor, new[] { up, legacy }, 2, 60 * 12, hits);
            float expectedHit = FracturedCityLayout.CannonBaseDamage * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct);
            Expect(hits.Count >= 2 && hits.All(h => Mathf.Abs(h - expectedHit) < 0.05f) && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == melt0
                   && SignalUplinkService.CoreFiredCount == fired0 && SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId) <= 0
                   && Heat(site, up) - heat0 <= FracturedCityLayout.CannonBaseHeatPerShot + 0.01f,
                $"FGT-SIG-011 对照：AI 编队命令真开火 {hits.Count} 发，每发 {string.Join("/", hits.Select(h => h.ToString("F1")))}（= 55 × 0.6，没有熔穿过载穿甲），" +
                $"没有熔穿过载提示、过载冷却没开始，单发积热 ≤ 40");

            // 敌方 AI：三个地点里所有敌方单位的武器都没有具名反应（核心固件反应只属于信号）。
            int hostiles = 0;
            int withReaction = 0;
            foreach (CombatSite cs in new[] { site })
            {
                for (int slot = 0; slot < cs.Kernel.SlotCount; slot++)
                {
                    CombatUnitView v = cs.Kernel.ViewAt(slot);
                    if (v.Faction != CombatFaction.Hostile || v.Weapon < 0)
                    {
                        continue;
                    }
                    hostiles++;
                    if (cs.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w) && w.Reaction != CombatReaction.None)
                    {
                        withReaction++;
                    }
                }
            }
            WorldSimulation.UnloadAll();
            CampaignState c = NewState(8502);
            FracturedCityController city = OpenCity(c);
            for (int slot = 0; slot < city.Combat.Kernel.SlotCount; slot++)
            {
                CombatUnitView v = city.Combat.Kernel.ViewAt(slot);
                if (v.Faction == CombatFaction.Hostile && v.Weapon >= 0)
                {
                    hostiles++;
                    if (city.Combat.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w) && w.Reaction != CombatReaction.None)
                    {
                        withReaction++;
                    }
                }
            }
            Expect(hostiles >= 4 && withReaction == 0, $"敌方 AI：铸造前哨外围（两台护甲机、步进炮；维修机已被打掉）与破碎都市（干扰机）的 {hostiles} 个带武器敌方单位，没有一个的武器带具名反应");
        }

        // ── C. 玩家接入触发（FGT-SIG-010）──────────────────────────────────────

        private static void CheckPlayerUplinkTriggers()
        {
            Line("  · C. FGT-SIG-010：带“过载”接入重炮机打护甲机正面 → 熔穿过载（穿甲 30%、积热 65），正式表冷却 8 秒内不再发动（DEBT-FG1SIG03-05 关闭）");
            CampaignState s = NewState(8511);
            EquipChip(s, FirmwareCatalog.FwOverloadId, 0);
            int a = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-30f, -20f), 900f);
            int g = SpawnRegion(FoundryOutpostLayout.RegionId, BpGun, new Vector2(-34f, -20f), 900f);
            FoundryOutpostController fo = OpenFoundry(s, a, g);
            WorldView.Observe(fo.SiteId);
            CombatSite site = fo.Combat;
            string left = FoundryOutpostLayout.ArmorBotLeftSpawnId;
            RegionEnemyRecord armor = PrepArmorTarget(s, site, left);
            CommitVia(a);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
            Expect(info.Uplinked && info.ReactionGated && WeaponOf(site, a).Reaction == CombatReaction.MeltOverload,
                "接入后：接入口插入过载，内核武器换成熔穿过载（信号带进来的核心固件，门控）");

            Place(site, a, armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f);
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            int fired0 = SignalUplinkService.CoreFiredCount;
            float h0 = Heat(site, a);
            float hp0 = armor.Health;
            bool shot1 = FireCannon(site, a, left, out bool melt1);
            float d1 = hp0 - armor.Health;
            float heat1 = Heat(site, a) - h0;
            double cd = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
            float pierceHit = FracturedCityLayout.CannonBaseDamage * (1f - (FoundryOutpostLayout.ArmorBotFrontalReductionPct - FracturedCityLayout.OverloadArmorPierceBonus));
            Expect(shot1 && melt1 && Mathf.Abs(d1 - pierceHit) < 0.05f && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) > melt0
                   && SignalUplinkService.CoreFiredCount == fired0 + 1 && Mathf.Abs(heat1 - 65f) < 0.5f
                   && cd > FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId) - 1.5 && cd <= FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId),
                $"接入后开火：熔穿过载命中 {d1:F1}（55 ×（1 −（0.4 − 0.3）））、积热 {heat1:F0}；过载进入冷却（剩 {cd:F2} 游戏秒，正式表 {FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId)} 秒）");

            float hp1 = armor.Health;
            SetHeat(site, a, 0f);
            bool shot2 = FireCannon(site, a, left, out bool melt2);
            float d2 = hp1 - armor.Health;
            Expect(shot2 && !melt2 && Mathf.Abs(d2 - FracturedCityLayout.CannonBaseDamage * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct)) < 0.05f
                   && SignalUplinkService.CoreFiredCount == fired0 + 1,
                $"冷却中下一发（{FracturedCityLayout.CannonCooldownSeconds} 秒后）：固件照样插着，但不发动熔穿过载（{d2:F1}），核心发动次数不变——正式表里“每一发都熔穿”的问题不再存在");
        }

        private static void CheckMarkJumpUplink()
        {
            Line("  · C2. 标记跳转：连射器 + 标记器 + 接入口，信号核带标记跳转——你接入时第三发跳到已标记的邻近敌人；同一台交还 AI 后不跳（对照）");
            CampaignState s = NewState(8512);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(FirmwareCatalog.FwMarkTagId).Distinct().ToArray();
            EquipChip(s, FirmwareCatalog.FwMarkTagId, 0);
            int m = SpawnRegion(FracturedCityLayout.RegionId, BpMarkUp, new Vector2(-2f, -24f), 900f);
            int other = SpawnRegion(FracturedCityLayout.RegionId, BpGun, new Vector2(2f, -24f), 900f);
            FracturedCityController city = OpenCity(s, m, other);
            WorldView.Observe(city.SiteId);
            CombatSite site = city.Combat;
            CommitVia(m);
            Expect(WeaponOf(site, m).Reaction == CombatReaction.MarkJump && SignalUplinkService.IsUplinked(s, m),
                "接入后：接入口插入标记跳转，内核武器带“标记跳转”反应");

            string s1 = FracturedCityLayout.Scout1SpawnId;
            string s2 = FracturedCityLayout.Scout2SpawnId;
            PlaceEnemy(site, s1, new Vector2(-10f, -8f));
            PlaceEnemy(site, s2, new Vector2(-6f, -8f));
            int jump0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump);
            int fired0 = SignalUplinkService.CoreFiredCount;
            FracturedCityRegion.TryAttackEnemy(s, m, s2, s.RandomSeed, isAiSource: false);
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            float s2Before = Enemy(s, s2).Health;
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            float jumped = s2Before - Enemy(s, s2).Health;
            Expect(jumped > 0.01f && FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump) > jump0 && SignalUplinkService.CoreFiredCount == fired0 + 1
                   && SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwMarkTagId) > 0,
                $"你接入时：第三发命中已标记目标 → 跳到已标记的邻近敌人（{jumped:F1}），标记跳转进入冷却");

            // 对照：信号跳到另一台，这台交还 AI；等冷却结束后再打已标记目标——AI 驾驶不跳（两个侦察机先回满血，排除“打死了所以没跳”）。
            CommitVia(other);
            WorldSimulation.StepMany((int)Math.Ceiling(FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwMarkTagId) * GameClock.StepHz) + 2);
            foreach (string sid in new[] { s1, s2 })
            {
                RegionEnemyRecord rec = Enemy(s, sid);
                rec.Health = rec.MaxHealth;
                site.SyncEnemyFromRecord(rec);
            }
            PlaceEnemy(site, s1, new Vector2(-10f, -8f));
            PlaceEnemy(site, s2, new Vector2(-6f, -8f));
            FracturedCityRegion.TryAttackEnemy(s, m, s2, s.RandomSeed, isAiSource: false);
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            float s2b = Enemy(s, s2).Health;
            int jump1 = FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump);
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            Expect(SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwMarkTagId) <= 0 && WeaponOf(site, m).Reaction == CombatReaction.None
                   && Enemy(s, s1).IsAlive && Enemy(s, s2).IsAlive && Mathf.Approximately(Enemy(s, s2).Health, s2b) && FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump) == jump1,
                "FGT-SIG-011 对照（标记跳转）：同一台交还 AI、冷却已过，再打已标记目标不跳转、邻近敌人不掉血");
        }

        // ── D. 交还 AI 时正处在过载状态 ─────────────────────────────────────────

        private static void CheckHandoffWhileOverloaded()
        {
            Line("  · D. 负向：交还 AI 时正处在过载状态——过热留在机体、AI 散热后才开火且不带熔穿过载；瞄准中交还；刚发动就交还冷却照记");
            CampaignState s = NewState(8521);
            EquipChip(s, FirmwareCatalog.FwOverloadId, 0);
            int a = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-30f, -20f), 900f);
            int g = SpawnRegion(FoundryOutpostLayout.RegionId, BpGun, new Vector2(-40f, -20f), 900f);
            FoundryOutpostController fo = OpenFoundry(s, a, g);
            WorldView.Observe(fo.SiteId);
            CombatSite site = fo.Combat;
            string left = FoundryOutpostLayout.ArmorBotLeftSpawnId;
            RegionEnemyRecord armor = PrepArmorTarget(s, site, left);
            CommitVia(a);
            Place(site, a, armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f);
            SetHeat(site, a, 50f);
            bool shot = FireCannon(site, a, left, out bool melt);
            float heatAfter = Heat(site, a);
            bool overheated = (Unit(site, a).Flags & CombatUnitFlags.Overheated) != 0;
            double cdLeft = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);

            int handoff0 = SignalUplinkService.HandoffOverheatedCount;
            CommitVia(g); // 信号跳到另一台：这台交还 AI
            site.TryGetMachineWeapon(a, out MachineWeaponInfo aiInfo);
            string fb = SignalUplinkService.LastFeedbackText;
            float heatHandoff = Heat(site, a);
            Expect(shot && melt && overheated && heatAfter >= 100f && !aiInfo.Uplinked && WeaponOf(site, a).Reaction == CombatReaction.None
                   && (Unit(site, a).Flags & CombatUnitFlags.Overheated) != 0 && heatHandoff > FracturedCityLayout.WeaponHeatRecoverThreshold
                   && SignalUplinkService.HandoffOverheatedCount == handoff0 + 1
                   && fb.Contains(SignalPresence.MachineLabel(a)) && fb.Contains(FracturedCityLayout.WeaponHeatRecoverThreshold.ToString("0")),
                $"熔穿过载打到过热（{heatAfter:F0}）后信号跳走：这台交还 AI，武器回到本地配置（无反应），过热与热量（{heatHandoff:F0}）留在机体；HUD“{fb}”");
            Expect(Math.Abs(SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId) - cdLeft) <= 0.5 && cdLeft > 0,
                $"刚发动就交还：过载冷却照样记在信号上（{SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId):F2} 游戏秒），不给“打完立刻切走”留空子");

            // AI 编队攻击命令：散热到 60 以下之前一发不开，之后开火也只有普通重炮（不穿甲、积热 40、没有熔穿过载）。
            double next0 = NextFire(site, a);
            float hpStart = armor.Health;
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            fo.SquadCommands.DebugSelectMany(new[] { a });
            fo.SquadCommands.IssueAttack(left, paused: false);
            double tIssue = GameClock.GameSeconds;
            int guard = 0;
            bool firedWhileHot = false;
            while (NextFire(site, a) == next0 && guard++ < 60 * 30)
            {
                bool hotBefore = (Unit(site, a).Flags & CombatUnitFlags.Overheated) != 0 && Heat(site, a) > FracturedCityLayout.WeaponHeatRecoverThreshold;
                WorldSimulation.StepMany(1);
                if (NextFire(site, a) != next0 && hotBefore)
                {
                    firedWhileHot = true;
                }
            }
            double waited = GameClock.GameSeconds - tIssue;
            double expectWait = (heatHandoff - FracturedCityLayout.WeaponHeatRecoverThreshold) / FracturedCityLayout.WeaponHeatDissipationPerSecond;
            float aiHit = hpStart - armor.Health;
            Expect(!firedWhileHot && NextFire(site, a) != next0 && waited >= expectWait - 0.5
                   && Mathf.Abs(aiHit - FracturedCityLayout.CannonBaseDamage * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct)) < 0.05f
                   && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == melt0,
                $"交还后的 AI：过热期间一发不开，等了 {waited:F1} 游戏秒（热量 {heatHandoff:F0} → 60 约需 {expectWait:F1} 秒）才开火；这一发 {aiHit:F1}（普通重炮，没有熔穿过载）");

            // 瞄准中交还：接入、开始瞄准（1 秒）后立刻跳走，AI 接着把这一发打完——不带熔穿过载。
            WorldSimulation.StepMany((int)Math.Ceiling(FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId) * GameClock.StepHz) + 2);
            CommitVia(a);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo reup);
            // 接入期间编队命令保留但不执行；清掉 AI 之前留下的瞄准 / 武器冷却与热量，从一次干净的瞄准开始。
            site.TryGetMachineUnit(a, out int aUnit);
            site.Kernel.SetWeaponState(aUnit, 0f, false, 0, 0);
            bool aiming = site.TryFireAtEnemy(a, left, out CombatFireResult r0, out _) && r0 == CombatFireResult.StillAiming;
            int melt1 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            int fired1 = SignalUplinkService.CoreFiredCount;
            CommitVia(g);
            float hpAim = armor.Health;
            double nextAim = NextFire(site, a);
            fo.SquadCommands.DebugSelectMany(new[] { a });
            fo.SquadCommands.IssueAttack(left, paused: false);
            guard = 0;
            while (NextFire(site, a) == nextAim && guard++ < 60 * 10)
            {
                WorldSimulation.StepMany(1);
            }
            float aimHit = hpAim - armor.Health;
            Expect(reup.Uplinked && reup.ReactionGated && aiming && NextFire(site, a) != nextAim && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == melt1
                   && SignalUplinkService.CoreFiredCount == fired1 && Mathf.Abs(aimHit - FracturedCityLayout.CannonBaseDamage * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct)) < 0.05f,
                $"接入中开始瞄准、瞄准没完就交还：AI 打完的这一发是普通重炮（{aimHit:F1}），不发动熔穿过载、不记核心冷却");
        }

        // ── E. Demo 内容迁移 ──────────────────────────────────────────────────────

        private static void CheckContentMigration()
        {
            Line("  · E. Demo 内容迁移：反应研究费、OBJ-06 / OBJ-08、核心门三灯、敌方适应都按“接入时能打出”判定（真实保存入口）");
            CampaignState s = NewState(8531);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>())
                .Concat(new[] { ComponentCatalog.CompCannonId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId }).Distinct().ToArray();
            s.TechData = 100;
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);

            // 旧口径：把过载写进机器电路——保存被拒（核心固件只能由信号携带）。
            BlueprintCircuitBoard oldStyle = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null,
                new[] { FirmwareCatalog.FwOverloadId });
            BlueprintSaveResult oldSave = BlueprintEditorService.TrySave(s, oldStyle, null, "旧口径熔穿过载", saveAsNewRecord: true);
            Expect(!oldSave.Success && oldSave.FailureReason.Contains(GameText.Format("signal.reason.core_in_circuit", GameText.Get("firmware.fw_overload.name")))
                   && !BlueprintEditorService.IsReactionCharged(s, MechanicalReactionCatalog.ReactionMeltOverloadId) && s.TechData == 100,
                $"旧口径（过载装在机器电路里）保存被拒：“{oldSave.FailureReason}”，不扣技术数据");

            // 没有接入口 / 接入口没接通：不算熔穿过载蓝图，不扣费。
            BlueprintCircuitBoard plain = CannonBoard(null);
            BlueprintCircuitBoard offPath = CannonBoard(3);
            BlueprintSaveResult plainSave = BlueprintEditorService.TrySave(s, plain, null, "重炮", saveAsNewRecord: true);
            BlueprintSaveResult offSave = BlueprintEditorService.TrySave(s, offPath, null, "重炮接入口没接通", saveAsNewRecord: true);
            Expect(plainSave.Success && plainSave.ReactionId == null && offSave.Success && offSave.ReactionId == null && s.TechData == 100
                   && !FoundryOutpostRegion.ComputeCoreGateLights(s).OverloadBlueprintSaved,
                "没有接入口、接入口不在源→汇路径上的重炮蓝图：接入时打不出熔穿过载，不算“熔穿过载蓝图”、不扣费、灯 2 不亮");

            // 技术数据不够：照样保存，研究延后；攒够后再存一次才研究（扣 15、灯 2 亮）。
            s.TechData = 5;
            BlueprintSaveResult poor = BlueprintEditorService.TrySave(s, CannonBoard(2), null, "熔穿过载接入蓝图", saveAsNewRecord: true);
            Expect(poor.Success && poor.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId && poor.TechDataCharged == 0 && poor.ResearchPendingCost == 15
                   && s.TechData == 5 && !BlueprintEditorService.IsReactionCharged(s, MechanicalReactionCatalog.ReactionMeltOverloadId),
                "技术数据 5 < 15：带接入口的重炮蓝图照样保存（不逼玩家拆接入口），研究记为待办、写明还差 15");
            s.TechData = 40;
            BlueprintSaveResult rich = BlueprintEditorService.TrySave(s, CannonBoard(2), poor.BlueprintId, null, saveAsNewRecord: false);
            BlueprintSaveResult again = BlueprintEditorService.TrySave(s, CannonBoard(2), poor.BlueprintId, null, saveAsNewRecord: false);
            Expect(rich.Success && rich.TechDataCharged == 15 && again.Success && again.TechDataCharged == 0 && again.ResearchPendingCost == 0 && s.TechData == 25
                   && BlueprintEditorService.IsReactionCharged(s, MechanicalReactionCatalog.ReactionMeltOverloadId)
                   && FoundryOutpostRegion.ComputeCoreGateLights(s).OverloadBlueprintSaved,
                $"攒够后再保存：扣 15 技术数据研究熔穿过载（只扣一次，再存免费，剩 {s.TechData}）→ 灯 2“{GameText.Get("foundry.gate.lamp.overload_saved")}”亮");

            // 灯 3：旧口径机器（电路里残留过载）不算；装上接入就绪版本的现役机才算；阵亡后灭。
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            int legacy = SpawnVersion(FoundryOutpostLayout.RegionId, BpCannonLegacy, 1);
            FoundryOutpostRegion.CoreGateLights l0 = FoundryOutpostRegion.ComputeCoreGateLights(s);
            int ready = SpawnVersion(FoundryOutpostLayout.RegionId, poor.BlueprintId, rich.Version);
            FoundryOutpostRegion.CoreGateLights l1 = FoundryOutpostRegion.ComputeCoreGateLights(s);
            FoundryOutpostRegion.ActionResult enter = FoundryOutpostRegion.CanEnterCoreZone(s);
            ObjectiveDef obj08 = CampaignObjectiveCatalog.All.First(o => o.Id == CampaignObjectiveTracker.Obj08);
            bool obj08Items = obj08.Items.All(i => i.IsDone(s)) && obj08.Items.Skip(1).All(i => !GameText.ContainsMarker(i.Label) && i.Label.Contains("接入口"));
            MachineRegistry.MarkDeadByLogicId(ready);
            FoundryOutpostRegion.CoreGateLights l2 = FoundryOutpostRegion.ComputeCoreGateLights(s);
            Expect(l0.CannonAnalyzed && l0.OverloadBlueprintSaved && !l0.MachineEquipped && l1.AllReady && enter.Success && obj08Items && !l2.MachineEquipped,
                $"灯 3：只有旧口径机器（电路里残留过载）时不亮——AI 不用它；装上接入就绪版本的现役机后三灯全亮、核心门放行，OBJ-08 三项全勾；那台阵亡后灯 3 灭（legacy #{legacy}）");

            // OBJ-06：连射器 + 标记器 + 接入口；标记跳转没解析时不算（刻印不出来），解析后保存扣 10。
            s.TechData = 50;
            BlueprintSaveResult markLocked = BlueprintEditorService.TrySave(s, MarkBoard(1), null, "标记跳转（未解析）", saveAsNewRecord: true);
            s.UnlockedContentIds = s.UnlockedContentIds.Append(FirmwareCatalog.FwMarkTagId).ToArray();
            BlueprintSaveResult markOk = BlueprintEditorService.TrySave(s, MarkBoard(1), markLocked.BlueprintId, null, saveAsNewRecord: false);
            ObjectiveDef obj06 = CampaignObjectiveCatalog.All.First(o => o.Id == CampaignObjectiveTracker.Obj06);
            ObjectiveItemDef saveItem = obj06.Items.First(i => i.Label == GameText.Get("objective.obj06.save_markjump"));
            Expect(markLocked.Success && markLocked.ReactionId == null && markOk.Success && markOk.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId
                   && markOk.TechDataCharged == 10 && saveItem.IsDone(s) && s.TechData == 40,
                "OBJ-06：标记跳转没解析时带接入口的标记蓝图不算（刻印不出来）；解析后保存 → 扣 10、清单“保存带接入口的标记跳转蓝图”打勾");

            // 敌方适应：构筑统计按接入就绪——2 台接入就绪重炮 + 1 台旧口径（按常规算）→ 熔穿过载主力 → 耐热反制。
            MachineRegistry.ResetForNewCampaign();
            SpawnVersion(FoundryOutpostLayout.RegionId, poor.BlueprintId, rich.Version);
            SpawnVersion(FoundryOutpostLayout.RegionId, poor.BlueprintId, rich.Version);
            SpawnVersion(FoundryOutpostLayout.RegionId, BpCannonLegacy, 1);
            s.SignalExposure = 70f;
            string adapt = EnemyAdaptationService.ComputeAdaptation(s);
            MachineRegistry.ResetForNewCampaign();
            SpawnVersion(FoundryOutpostLayout.RegionId, BpCannonLegacy, 1);
            SpawnVersion(FoundryOutpostLayout.RegionId, BpCannonLegacy, 1);
            string adaptLegacy = EnemyAdaptationService.ComputeAdaptation(s);
            Expect(adapt == AdaptationCatalog.HeatResistant && adaptLegacy == AdaptationCatalog.JammerSupport,
                $"敌方适应：接入就绪的重炮为主 → {adapt}；只有旧口径机器（AI 用不了过载）→ {adaptLegacy}（按常规配置统计）");
        }

        /// <summary>可通关：AI 编队（不接入、不靠熔穿过载）照样打掉铸造前哨外围两台护甲机（维修机在旁边治疗）。</summary>
        private static void CheckPassableWithoutOverload()
        {
            Line("  · E2. 可通关：只用 AI 编队（没有熔穿过载）打铸造前哨外围两台护甲机——维修机照常治疗，也能打掉");
            CampaignState s = NewState(8541);
            int c1 = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-10f, -20f), 900f);
            int c2 = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-8f, -20f), 900f);
            int gn = SpawnRegion(FoundryOutpostLayout.RegionId, BpGun, new Vector2(-6f, -20f), 900f);
            FoundryOutpostController fo = OpenFoundry(s, c1, c2, gn);
            WorldView.Observe(fo.SiteId);
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            double t0 = GameClock.GameSeconds;
            var killed = new List<string>();
            foreach (string target in new[] { FoundryOutpostLayout.ArmorBotLeftSpawnId, FoundryOutpostLayout.ArmorBotRightSpawnId })
            {
                fo.SquadCommands.DebugSelectMany(new[] { c1, c2, gn });
                fo.SquadCommands.IssueAttack(target, paused: false);
                int guard = 0;
                while (Enemy(s, target).IsAlive && guard++ < 60 * 120)
                {
                    WorldSimulation.StepMany(1);
                }
                if (!Enemy(s, target).IsAlive)
                {
                    killed.Add(target);
                }
            }
            Expect(killed.Count == 2 && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == melt0 && !SignalUplinkService.IsUplinked(s, c1),
                $"AI 编队 2 重炮 + 1 连射器在 {GameClock.GameSeconds - t0:F0} 游戏秒内打掉两台护甲机，全程没有熔穿过载——原本由 AI 触发熔穿过载的外围战斗不靠它也能通关");
        }

        // ── F. 暂停与倍速 ─────────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · F. 暂停与 0.5x～3x：交还 AI 的过热机器按游戏时间散热（暂停不走、各档倍速恢复开火的游戏时刻一致）");
            var times = new List<double>();
            bool pauseOk = true;
            bool gameTimeOk = true;
            var detail = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewHome(8550 + (int)(speed * 10));
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                WorldSimulation.StepMany(2);
                CombatSite site = WorldSimulation.Home.Combat;
                site.TryGetMachineUnit(a, out int unit);
                site.Kernel.SetWeaponState(unit, 120f, true, 0, 0);
                GameClock.SetPaused(true);
                Frames(20);
                pauseOk &= Mathf.Approximately(Heat(site, a), 120f) && (Unit(site, a).Flags & CombatUnitFlags.Overheated) != 0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                double start = GameClock.GameSeconds;
                int guard = 0;
                while ((Unit(site, a).Flags & CombatUnitFlags.Overheated) != 0 && guard++ < 4000)
                {
                    Frames(1);
                }
                double elapsed = GameClock.GameSeconds - start;
                float h = Heat(site, a);
                times.Add(elapsed);
                // 散热只按游戏时间走：量到的热量 = 120 − 10 × 游戏秒（与倍速无关）；过热在降到 60 以下的那一帧解除。
                gameTimeOk &= Math.Abs((120f - h) / FracturedCityLayout.WeaponHeatDissipationPerSecond - elapsed) <= GameClock.StepSeconds + 1e-4
                               && h < FracturedCityLayout.WeaponHeatRecoverThreshold
                               && h >= FracturedCityLayout.WeaponHeatRecoverThreshold - FracturedCityLayout.WeaponHeatDissipationPerSecond * (FrameDt * speed + GameClock.StepSeconds) - 1e-3;
                detail.Add($"{speed}x:{elapsed:F2}s/{h:F1}");
                GameClock.SetSpeed(1f);
            }
            double expect = (120f - FracturedCityLayout.WeaponHeatRecoverThreshold) / FracturedCityLayout.WeaponHeatDissipationPerSecond;
            Expect(pauseOk, "暂停 1 真实秒：过热机器的热量一点没降、过热没解除");
            Expect(gameTimeOk && times.All(t => t >= expect - 1e-3 && t <= expect + 0.2),
                $"0.5x / 1x / 2x / 3x 下散热只按游戏时间走、过热在降到 60 以下的那一帧解除（{string.Join("，", detail)}；理论 {expect:F1} 游戏秒，误差上限一帧）");
        }

        // ── G. 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · G. 存读档（真实文件、按主菜单“继续”的顺序恢复）：交还 AI 时仍过热的机器、旧档电路里的核心固件、反应研究记录");
            CampaignState s = NewHome(8561);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int legacy = SpawnHome(BpCannonLegacy, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipChip(s, FirmwareCatalog.FwOverloadId, 0);
            CampaignEventLedger.TryGrant(s, BlueprintEditorService.ReactionChargeEventId(MechanicalReactionCatalog.ReactionMeltOverloadId), "BlueprintReactionCharge", 0f);
            HomeValleyController home = WorldSimulation.Home;
            Expect(home.TrySelectMachine(a), "家园里选中重炮机");
            Press(Key(GameActionId.ToggleCameraView));
            Frames(10);
            CombatSite site = home.Combat;
            bool uplinked = SignalUplinkService.IsUplinked(s, a) && WeaponOf(site, a).Reaction == CombatReaction.MeltOverload;
            site.TryGetMachineUnit(a, out int unit);
            site.Kernel.SetWeaponState(unit, 110f, true, 0, 0);
            int h0 = SignalUplinkService.HandoffOverheatedCount;
            Frames(40); // 等镜头飞到直控跟随（镜头过渡期间接入键不响应）
            string beforeLeave = $"镜头 {WorldView.Director.Mode}，信号在 #{SignalPresence.CurrentMachineLogicId}，接入 {uplinked}";
            Press(Key(GameActionId.ToggleCameraView)); // 离开：镜头拉回战略，过渡结束时信号回到归还核心、这台交还 AI
            Frames(20);
            string fb = SignalUplinkService.LastFeedbackText;
            float heatBefore = Heat(site, a);
            Expect(uplinked && !SignalUplinkService.IsUplinked(s, a) && SignalUplinkService.HandoffOverheatedCount == h0 + 1
                   && fb == GameText.Format("signal.uplink.handoff_overheated", SignalPresence.MachineLabel(a), FracturedCityLayout.WeaponHeatRecoverThreshold.ToString("0")),
                $"按接入键离开（正式输入）：过热的重炮交还 AI，HUD“{fb}”（不被“已离开”盖掉；离开前 {beforeLeave}，离开后镜头 {WorldView.Director.Mode}）");

            SaveNow();
            CampaignState l = LoadLikeMenu();
            CombatSite ls = WorldSimulation.Home?.Combat;
            bool loaded = l != null && ls != null;
            CombatUnitView lv = loaded ? Unit(ls, a) : default;
            MachineCombatResolution legacyRes = loaded ? MachineLoadoutRegistry.ResolveForAi(l, legacy, l.RandomSeed) : default;
            Expect(loaded && (lv.Flags & CombatUnitFlags.Overheated) != 0 && Mathf.Abs(lv.Heat - heatBefore) < 1.5f && !SignalUplinkService.IsUplinked(l, a)
                   && WeaponOf(ls, a).Reaction == CombatReaction.None,
                $"读档后：那台仍在过热（热量 {lv.Heat:F1}，存档前 {heatBefore:F1}），信号在归还核心，AI 武器没有反应");
            Expect(loaded && legacyRes.Success && legacyRes.Preview.ReactionId == null && legacyRes.Preview.InertCoreFirmwareIds.Contains(FirmwareCatalog.FwOverloadId)
                   && WeaponOf(ls, legacy).Reaction == CombatReaction.None
                   && BlueprintEditorService.IsReactionCharged(l, MechanicalReactionCatalog.ReactionMeltOverloadId)
                   && SignalCoreService.SlotContentId(l, 0) == FirmwareCatalog.FwOverloadId,
                "读档后：旧档电路里的过载照样不生效（版本原样保留、不删不改），熔穿过载研究记录与信号核里的过载都在");
        }

        // ── H. 观察无关 ───────────────────────────────────────────────────────────

        private static void CheckObservationIndependence()
        {
            Line("  · H. 家园不被观察时结果一致：AI 驾驶的武器与反应边界不随镜头变化");
            CampaignState s = NewHome(8571);
            EquipChip(s, FirmwareCatalog.FwOverloadId, 0);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int legacy = SpawnHome(BpCannonLegacy, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            CombatWeapon w1 = WeaponOf(site, a);
            CombatWeapon w2 = WeaponOf(site, legacy);
            FracturedCityController city = OpenCity(s);
            WorldView.Observe(city.SiteId);
            WorldSimulation.StepMany(30);
            MachineLoadoutRegistry.NotifyChanged(a);
            MachineLoadoutRegistry.NotifyChanged(legacy);
            WorldSimulation.StepMany(1);
            Expect(WorldSimulation.Home != null && w1.Reaction == CombatReaction.None && w2.Reaction == CombatReaction.None
                   && WeaponOf(site, a).Equals(w1) && WeaponOf(site, legacy).Equals(w2),
                "镜头去破碎都市、家园后台运行并重编译：两台 AI 重炮的武器参数与观察时逐字段相同，都没有反应");
        }

        // ── I. 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · I. 性能：接入就绪判定按蓝图签名缓存，核心门 / 适应统计与机器数线性、与帧无关");
            CampaignState s = NewState(8581);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            UplinkReactionReadiness.ResetForTests();
            var sw = Stopwatch.StartNew();
            string cold = UplinkReactionReadiness.ReadyReactionId(s, CannonBoard(2));
            double coldMs = sw.Elapsed.TotalMilliseconds;
            int compiles = UplinkReactionReadiness.CompileCount;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                UplinkReactionReadiness.ReadyReactionId(s, CannonBoard(2));
            }
            double warmMs = sw.Elapsed.TotalMilliseconds / 200.0;
            for (int i = 0; i < 50; i++)
            {
                SpawnVersion(FoundryOutpostLayout.RegionId, i % 3 != 0 ? BpCannonUp : BpCannonLegacy, 1);
            }
            // 敌方适应的构筑统计逐台读 50 台机器（核心门三灯遇到第一台就绪机就停，用适应统计量最坏情况）。
            s.SignalExposure = 70f;
            string adapt = EnemyAdaptationService.ComputeAdaptation(s);
            sw.Restart();
            for (int i = 0; i < 20; i++)
            {
                EnemyAdaptationService.ComputeAdaptation(s);
                FoundryOutpostRegion.ComputeCoreGateLights(s);
            }
            double sumMs = sw.Elapsed.TotalMilliseconds / 20.0;
            Expect(cold == MechanicalReactionCatalog.ReactionMeltOverloadId && compiles == 1 && UplinkReactionReadiness.CompileCount <= 3
                   && adapt == AdaptationCatalog.HeatResistant,
                $"首次判定 {coldMs:F2} ms（跑一次真实接入编译）；命中缓存平均 {warmMs:F4} ms；50 台机器的适应统计 + 核心门三灯 {sumMs:F2} ms/次；编译总次数 {UplinkReactionReadiness.CompileCount}（两种蓝图各一次）");
            Expect(warmMs < 1.0 && sumMs < 20.0, $"开销门槛：命中缓存 < 1 ms（{warmMs:F4}）、50 台统计 < 20 ms（{sumMs:F2}）——只在保存蓝图 / 核心门输入变化 / 出发预览时算，不在帧循环");
            PerfLines.Add($"接入就绪判定 冷 {coldMs:F2} ms / 热 {warmMs:F4} ms；50 台适应统计 + 核心门 {sumMs:F2} ms——Editor 下 Mono JIT，真机 HybridCLR 解释执行预计慢数倍（真机复测归 FG15-SYS-02），均为输入变化时一次，不在帧循环");
        }

        // ── J. 界面文字 ───────────────────────────────────────────────────────────

        private static void CheckTexts()
        {
            Line("  · J. 界面文字：本 Story 新增的文本键中英文齐全；核心门、任务清单、双态预览说明读文本键");
            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files =
            {
                "Campaign/Regions/FoundryOutpostRegion.cs", "UI/Objective/MissionLogUIToolkit.cs", "Campaign/CampaignObjectiveCatalog.cs",
                "Campaign/Blueprint/UplinkCompiler.cs", "UI/CircuitBoard/CircuitBoardPanelUIToolkit.cs", "Campaign/Signal/SignalUplinkService.cs",
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match mt in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)),
                             "\"((?:foundry\\.gate|objective\\.obj0|circuit\\.reaction|blueprint\\.save\\.reaction|circuit\\.uplink\\.note\\.core_inert|signal\\.uplink\\.handoff)[a-z0-9_.]*)\""))
                {
                    keys.Add(mt.Groups[1].Value);
                }
            }
            var missing = keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k, GameLanguage.En))
                                          || GameText.Get(k, GameLanguage.En) == GameText.Get(k, GameLanguage.ZhCn)).ToList();
            Expect(keys.Count >= 20 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个 FG1-SIG-05 文本键都在 fg.TbLocText、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");

            CampaignState s = NewState(8591);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            string zh = MissionLogUIToolkit.ComposeGateText(s);
            GameSettings.SetLanguage(GameLanguage.En);
            string en = MissionLogUIToolkit.ComposeGateText(s);
            string enItem = CampaignObjectiveCatalog.All.First(o => o.Id == CampaignObjectiveTracker.Obj08).Items[1].Label;
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            BlueprintCircuitBoard legacyBoard = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null,
                new[] { FirmwareCatalog.FwOverloadId });
            legacyBoard.TrySetUplink(2);
            UplinkDualPreview dual = UplinkCompiler.CompileDual(legacyBoard, new[] { FirmwareCatalog.FwOverloadId });
            string inertNote = GameText.Format("circuit.uplink.note.core_inert", GameText.Get("firmware.fw_overload.name"));
            Expect(zh.Contains(GameText.Get("foundry.gate.lamp.machine_ready")) && !GameText.ContainsMarker(zh) && en.Contains("uplink") && !GameText.ContainsMarker(en)
                   && enItem.Contains("uplink") && dual.Notes.Contains(inertNote) && dual.Ai.ReactionId == null
                   && dual.Uplinked.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId,
                $"核心门（中）“{zh}”；（英）“{en}”；OBJ-08 英文“{enItem}”；旧草稿双态预览说明“{inertNote}”，左栏无反应、接入栏熔穿过载");
        }

        // ── 世界与机器 ────────────────────────────────────────────────────────────

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig05-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            foreach (string type in new[] { HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeAssemblyStation })
            {
                BuildingRecord r = s.BuildingRecords.FirstOrDefault(x => x.BuildingTypeId == type);
                if (r != null)
                {
                    r.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            AddBlueprints(s);
            return s;
        }

        private static CampaignState NewState(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig05-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            AddBlueprints(s);
            return s;
        }

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            InputRouter.DebugSetReader(Keys);
            Keys.Down = KeyCode.None;
            UiEscapeStack.Clear();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
        }

        private static BlueprintCircuitBoard CannonBoard(int? uplinkSlot)
        {
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            if (uplinkSlot.HasValue)
            {
                CircuitOpResult r = b.TrySetUplink(uplinkSlot.Value);
                if (!r.Success)
                {
                    Fail($"测试准备：重炮蓝图 {uplinkSlot} 号格标接入口失败：{r.Message}");
                }
            }
            return b;
        }

        private static BlueprintCircuitBoard MarkBoard(int uplinkSlot)
        {
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null,
                Array.Empty<string>());
            CircuitOpResult r = b.TrySetUplink(uplinkSlot);
            if (!r.Success)
            {
                Fail($"测试准备：标记蓝图 {uplinkSlot} 号格标接入口失败：{r.Message}");
            }
            return b;
        }

        private static void AddBlueprints(CampaignState s)
        {
            AddBlueprint(s, BpCannonUp, CannonBoard(2));
            // 旧档：过载写在机器电路里（种类表改成核心之前保存的版本；校验已经不让这样保存，这里直接写版本模拟旧档）。
            AddBlueprint(s, BpCannonLegacy, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null,
                new[] { FirmwareCatalog.FwOverloadId }));
            AddBlueprint(s, BpGun, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            AddBlueprint(s, BpMarkUp, MarkBoard(1));
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static int SpawnHome(string bp, Vector2 at)
        {
            int id = SpawnRegion(HomeValleyLayout.RegionId, bp, HomeSpot(at));
            CircuitOpResult r = MachineLoadoutRegistry.Register(CampaignSession.Current, id, bp, 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记装配失败：{r.Message}");
            }
            return id;
        }

        private static Vector2 HomeSpot(Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 away = (core - dummy).sqrMagnitude > 1e-4f ? (core - dummy).normalized : Vector2.right;
            Vector2 p = core + offset;
            int guard = 0;
            while (Vector2.Distance(p, dummy) < HomeValleyCombatTargets.EngageRange + 4f && guard++ < 20)
            {
                p += away * 3f;
            }
            return p;
        }

        private static int SpawnRegion(string region, string bp, Vector2 at, float hp = 400f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static int SpawnVersion(string region, string bp, int version)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, new Vector2(-30f, -20f), 400f, 400f, "Player", version);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static FracturedCityController OpenCity(CampaignState s, params int[] ids)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(ids, resume: false);
        }

        private static FoundryOutpostController OpenFoundry(CampaignState s, params int[] ids)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFoundryOutpost(ids, resume: false);
        }

        /// <summary>护甲机当靶子：血量调大（只数伤害，不让它提前阵亡）、维修机打掉（不在开火那一步回血，伤害差值可比）。</summary>
        private static RegionEnemyRecord PrepArmorTarget(CampaignState s, CombatSite site, string id)
        {
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.RepairBotSpawnId, 1e6f);
            RegionEnemyRecord armor = Enemy(s, id);
            armor.MaxHealth = 1e6f;
            armor.Health = 1e6f;
            site.SyncEnemyFromRecord(armor);
            WorldSimulation.StepMany(1);
            return armor;
        }

        /// <summary>推模拟步，记录 <paramref name="shooters"/> 每次真开火（NextFireAt 前移）那一步靶子掉的血，直到收集够 <paramref name="want"/> 发。</summary>
        private static void CollectShotDamage(CombatSite site, RegionEnemyRecord target, int[] shooters, int want, int maxSteps, List<float> hits)
        {
            var next = shooters.ToDictionary(id => id, id => NextFire(site, id));
            for (int i = 0; i < maxSteps && hits.Count < want; i++)
            {
                float before = target.Health;
                WorldSimulation.StepMany(1);
                int firedNow = shooters.Count(id => NextFire(site, id) != next[id]);
                // 同一步里几台一起开火时取平均（它们的单发伤害应当相同，不同就会让平均偏离期望值而失败）。
                for (int k = 0; k < firedNow; k++)
                {
                    hits.Add((before - target.Health) / firedNow);
                }
                foreach (int id in shooters)
                {
                    next[id] = NextFire(site, id);
                }
            }
        }

        private static void EquipChip(CampaignState s, string firmwareId, int slot)
        {
            SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
            if (!pr.Success)
            {
                Fail($"测试准备：刻印 {firmwareId} 失败（{pr.Message}）");
                return;
            }
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, pr.CreatedId, slot);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!r.Success)
            {
                Fail($"测试准备：{firmwareId} 装入 {slot + 1} 号槽失败：{r.Message}");
            }
        }

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 40)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        /// <summary>重炮两段式开火（直控点击的正式入口 CombatSite.TryFireAtEnemy）。返回是否真的开了火、这一发有没有熔穿过载。</summary>
        private static bool FireCannon(CombatSite site, int logicId, string enemyId, out bool melt)
        {
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
            double next0 = NextFire(site, logicId);
            int guard = 0;
            while (guard++ < 400)
            {
                site.TryFireAtEnemy(logicId, enemyId, out CombatFireResult res, out _);
                if (NextFire(site, logicId) > next0)
                {
                    break;
                }
                if (res != CombatFireResult.StillAiming && res != CombatFireResult.Cooldown && res != CombatFireResult.Overheated)
                {
                    melt = false;
                    return false;
                }
                WorldSimulation.StepMany(1);
            }
            melt = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) > melt0;
            return NextFire(site, logicId) > next0;
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
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(Slot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            SignalUplinkService.RestoreAfterLoad();
            return r.State;
        }

        // ── 输入与帧 ──────────────────────────────────────────────────────────────

        private static KeyCode Key(GameActionId action) => GameSettings.KeyBindings.GetKey(action);

        private static void Press(KeyCode key)
        {
            Keys.Down = key;
            Frames(1);
            Keys.Down = KeyCode.None;
        }

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt);
            }
        }

        private static CombatWeapon WeaponOf(CombatSite site, int logicId) =>
            site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) && info.WeaponIndex >= 0 && site.Kernel.TryGetWeapon(info.WeaponIndex, out CombatWeapon w)
                ? w : default;

        private static CombatUnitView Unit(CombatSite site, int logicId) =>
            site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static float Heat(CombatSite site, int logicId) => Unit(site, logicId).Heat;

        private static double NextFire(CombatSite site, int logicId) => Unit(site, logicId).NextFireAt;

        private static void SetHeat(CombatSite site, int logicId, float heat)
        {
            if (site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                site.Kernel.SetWeaponState(unit, heat, false, v.AimReadyAt, v.NextFireAt);
            }
        }

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static void PlaceEnemy(CombatSite site, string enemyId, Vector2 at)
        {
            if (site.TryGetEnemyUnit(enemyId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static RegionEnemyRecord Enemy(CampaignState s, string id) => s.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == id);

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        // ── 报告 ──────────────────────────────────────────────────────────────────

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
            _report.AppendLine(text);
        }
    }
}
