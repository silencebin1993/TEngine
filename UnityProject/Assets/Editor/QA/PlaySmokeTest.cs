using System;
using System.IO;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Objective;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Button = UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 真进 Play 的冒烟测试（batchmode 自检测不到的运行时错误靠它抓——2026-09-25 主菜单空引用就是这么漏掉的）：
    /// 打开 main.unity 进 Play → 存档改到临时目录（绝不碰玩家槽位）→ 点主菜单“新建”→ 点空槽位 → 进归还谷地 →
    /// 读目标条 → 按 J 开任务日志、Esc 关 → 按出征同样的调用顺序进破碎都市 → 进铸造前哨 → 回归还谷地。
    /// 全程收集 Error/Exception/Assert，任何一条都算失败。
    /// FG0-DATA-01：每段扫描界面文本里的 ⟦key⟧ 缺失标记；工单目标名走文本键；区域播种的敌人生命等于 fg.TbMechEnemy 表值。
    ///
    /// 用法：<c>bash tools/unity-play-smoke.sh</c>（影子工程里跑，编辑器开着也行）。进 Play 会重载域，
    /// 驱动状态存在 SessionState 里，[InitializeOnLoad] 重载后接着跑。
    /// </summary>
    [InitializeOnLoad]
    public static class PlaySmokeTest
    {
        private const string K = "BinGames.PlaySmoke.";
        private const double TotalTimeoutSeconds = 480;

        static PlaySmokeTest()
        {
            if (SessionState.GetBool(K + "Active", false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            string outPath = Environment.GetEnvironmentVariable("BINGAMES_SMOKE_OUT");
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(Path.GetTempPath(), "bingames-play-smoke.txt");
            }
            File.WriteAllText(outPath, string.Empty);
            SessionState.SetString(K + "Out", outPath);
            SessionState.SetString(K + "Saves", Path.Combine(Path.GetTempPath(), "bingames-play-smoke-saves-" + Guid.NewGuid().ToString("N")));
            SessionState.SetBool(K + "Active", true);
            SessionState.SetInt(K + "Errors", 0);
            SessionState.SetFloat(K + "RepairedAt", 0f);
            SessionState.SetBool(K + "VfxRaised", false);
            SessionState.SetBool(K + "VfxChecked", false);
            SessionState.SetFloat(K + "Start", (float)EditorApplication.timeSinceStartup);
            Next(0, "开始：打开 Assets/Scenes/main.unity 并进入 Play");
            EditorSceneManager.OpenScene("Assets/Scenes/main.unity", OpenSceneMode.Single);
            Hook();
            EditorApplication.EnterPlaymode();
        }

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }
            // 编辑器自带搜索索引在 batchmode 下的内部异常，与游戏无关。
            if ((stackTrace ?? string.Empty).Contains("UnityEditor.Search."))
            {
                Write($"  - 忽略编辑器内部报错：{condition.Trim()}");
                return;
            }
            SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            string firstFrames = string.Join(" | ", (stackTrace ?? string.Empty).Split('\n').Where(l => l.Trim().Length > 0).Take(4));
            Write($"  ✗ [{type}] {condition.Trim()}  @ {firstFrames}");
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - SessionState.GetFloat(K + "Start", 0f) > TotalTimeoutSeconds)
            {
                Finish("总超时");
                return;
            }
            int step = SessionState.GetInt(K + "Step", 0);
            double inStep = now - SessionState.GetFloat(K + "StepStart", 0f);
            try
            {
                switch (step)
                {
                    case 0: StepEnterPlay(inStep); break;
                    case 1: StepClickNew(inStep); break;
                    case 2: StepPickSlot(inStep); break;
                    case 3: StepWaitHome(inStep); break;
                    case 4: StepHomeSoak(inStep); break;
                    case 5: StepMissionLogOpen(inStep); break;
                    case 6: StepMissionLogClosed(inStep); break;
                    case 10: StepClickGenerator(inStep); break;
                    case 11: StepRepairOrdered(inStep); break;
                    case 12: StepRepairDone(inStep); break;
                    case 13: StepFactoryOpen(inStep); break;
                    case 14: StepFactoryClosed(inStep); break;
                    case 7: StepRuins(inStep); break;
                    case 8: StepFoundry(inStep); break;
                    case 9: StepBackHome(inStep); break;
                }
            }
            catch (Exception e)
            {
                Write($"  ✗ 驱动步骤 {step} 抛异常：{e.GetType().Name}: {e.Message}");
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
                Finish("驱动异常");
            }
        }

        // ── 步骤 ────────────────────────────────────────────────────

        private static void StepEnterPlay(double inStep)
        {
            if (!EditorApplication.isPlaying)
            {
                if (inStep > 90)
                {
                    Finish("90 秒内没能进入 Play");
                }
                return;
            }
            CampaignSaveService.SaveDirectoryOverrideForTests = SessionState.GetString(K + "Saves", null);
            Next(1, "已进入 Play；存档目录改到临时目录：" + CampaignSaveService.SaveDirectoryOverrideForTests);
        }

        private static void StepClickNew(double inStep)
        {
            Button button = FindActiveButton("m_btn_New");
            if (button == null)
            {
                if (inStep > 150)
                {
                    Finish("150 秒内主菜单没出现（找不到“新建”按钮）");
                }
                return;
            }
            if (inStep < 2)
            {
                return; // 等主菜单稳定一下。
            }
            button.onClick.Invoke();
            Next(2, $"主菜单出现（{inStep:F0} 秒），点“新建”");
        }

        private static void StepPickSlot(double inStep)
        {
            // 有空槽时“新建”直接开新战役（不经过槽位列表）；三槽全满才弹覆盖确认。
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                Next(4, $"新建直接进入归还谷地（{inStep:F0} 秒）");
                return;
            }
            Button confirm = FindActiveButton("m_btn_ConfirmYes");
            if (confirm != null)
            {
                confirm.onClick.Invoke();
                Write("  - 出现覆盖确认，点“是”");
                return;
            }
            Button slot = FindActiveButton("m_btn_Slot0Action");
            if (slot == null)
            {
                if (inStep > 120)
                {
                    Finish("120 秒内既没进入归还谷地、也没出现存档槽列表");
                }
                return;
            }
            slot.onClick.Invoke();
            Next(3, "点存档槽 0");
        }

        private static void StepWaitHome(double inStep)
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                Next(4, $"进入归还谷地（{inStep:F0} 秒）");
                return;
            }
            if (inStep > 120)
            {
                Finish("120 秒内没进入归还谷地");
            }
        }

        private static void StepHomeSoak(double inStep)
        {
            if (inStep < 6)
            {
                return;
            }
            Write("  - 目标条：" + ObjectiveTitle());
            Write($"  - 当前目标：{CampaignObjectiveTracker.CurrentObjectiveId(CampaignSession.Current) ?? "无"}；场景里剪影 {CountNamed("Silhouette")} 个");
            Transform pin = FindNamed("Badge_Objective");
            SpriteRenderer pinRenderer = pin != null ? pin.GetComponent<SpriteRenderer>() : null;
            Check(pinRenderer != null && pinRenderer.enabled && pinRenderer.sprite != null,
                $"定位针贴图已加载并显示（位置 {(pin != null ? pin.position.ToString("F1") : "无")}）");
            CheckNoTextMarkers("归还谷地");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleMissionLog));
            Next(5, "归还谷地运行 6 秒；按任务日志键");
        }

        private static void StepMissionLogOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Write(MissionLogUIToolkit.IsOpen ? "  ✓ 任务日志已打开" : "  ✗ 按键后任务日志没有打开");
            if (!MissionLogUIToolkit.IsOpen)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(6, "按取消键关闭任务日志");
        }

        private static void StepMissionLogClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Write(!MissionLogUIToolkit.IsOpen ? "  ✓ 任务日志已关闭" : "  ✗ 取消键没能关闭任务日志");
            if (MissionLogUIToolkit.IsOpen)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
            Campaign.Regions.HomeValleyMachineMarker hauler = Object.FindObjectsByType<Campaign.Regions.HomeValleyMachineMarker>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .OrderBy(m => m.LogicId).FirstOrDefault();
            if (hauler == null)
            {
                Finish("归还谷地里找不到可点选的机器");
                return;
            }
            ClickWorld(hauler.transform.position);
            SessionState.SetInt(K + "Worker", hauler.LogicId);
            Next(10, $"鼠标左键点选机器 #{hauler.LogicId}");
        }

        private static void StepClickGenerator(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Transform generator = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            if (generator == null)
            {
                Finish("场景里找不到发电机");
                return;
            }
            ClickWorld(generator.position);
            Next(11, "鼠标左键点受损的发电机（下令修复）");
        }

        private static void StepRepairOrdered(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool ordered = state?.WorkOrders != null && state.WorkOrders.Any(o =>
                o.Kind == WorkOrderKind.Repair && o.TargetId == Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator
                && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed);
            Check(ordered, "点选机器再点发电机 → 修复工单已下达");
            if (!ordered)
            {
                Finish("鼠标下令修复没有生效");
                return;
            }
            // FG0-DATA-01：工单目标名 = fg.TbBuilding.nameKey → GameText（表经 Play 模式的资源系统加载）。
            string generatorLabel = Campaign.Content.MechanicalContentFacade.ResolveWorkOrderTargetLabel(
                Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            Check(generatorLabel == Localization.GameText.Get("building.generator.name") && !Localization.GameText.ContainsMarker(generatorLabel)
                  && generatorLabel != Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator,
                $"工单目标名走文本键：“{generatorLabel}”（Play 模式下 fg 表已加载）");
            Next(12, "等机器走过去修好发电机");
        }

        private static void StepRepairDone(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord generator = state?.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == Campaign.Regions.HomeValleyLayout.RegionId && b.BuildingTypeId == Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            if (generator == null || generator.ConstructionState != BuildingConstructionState.Operational)
            {
                if (inStep > 120)
                {
                    Finish("120 秒内发电机没修好");
                }
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (SessionState.GetFloat(K + "RepairedAt", 0f) <= 0f)
            {
                SessionState.SetFloat(K + "RepairedAt", (float)now);
                Write($"  - 发电机修好（下令后 {inStep:F0} 秒）");
                return;
            }
            if (now - SessionState.GetFloat(K + "RepairedAt", 0f) < 1.5)
            {
                return; // 目标 0.5 秒一算、目标条 0.25 秒一刷：修好之后等 1.5 秒再读。
            }
            string title = ObjectiveTitle();
            Write($"  - 修好 1.5 秒后目标条：{title}");
            Check(title.Contains("目标 2/10"), "修好发电机后目标条跳到目标 2/10");
            CheckNoTextMarkers("发电机修好后");
            Transform station = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation);
            if (station == null)
            {
                Finish("场景里找不到装配站");
                return;
            }
            ClickWorld(station.position);
            Next(13, "鼠标左键点装配站（打开生产面板）");
        }

        private static void StepFactoryOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            bool open = GameRoot.HomeValley != null && GameRoot.HomeValley.IsFactoryPanelOpen;
            Check(open, "点装配站打开生产面板");
            string hint = LabelText("[HomeValleyFactoryHost]", "ProduceHintLabel");
            Write($"  - 生产面板提示行：{hint}");
            bool hoverUnlocked = Campaign.Regions.HomeValleyFactory.IsBlueprintUnlocked(CampaignSession.Current,
                Campaign.Regions.HomeValleyLayout.BlueprintHoverId);
            Check(!open || (hoverUnlocked ? !hint.Contains("先让解析台通电") : hint.Contains("先让解析台通电")),
                hoverUnlocked ? "维修机已解锁：提示行不再显示解锁条件" : "维修机未解锁：提示行写明解锁条件");
            Transform station = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation);
            if (station != null)
            {
                ClickWorld(station.position);
            }
            Next(14, "再点一次装配站（关闭生产面板）");
        }

        private static void StepFactoryClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsFactoryPanelOpen, "再点装配站关闭生产面板");
            InputRouter.DebugSetReader(null);

            int[] roster = HomeMachines();
            UnlockLikeDeparture(Campaign.Regions.FracturedCityRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            GameRoot.HomeValley?.Exit();
            GameRoot.StartFracturedCity(roster);
            Next(7, $"测试捷径：标记破碎都市可出征并记一次出征，按出征同样的调用顺序进入（{roster.Length} 台机器）");
        }

        private static void StepRuins(double inStep)
        {
            if (inStep < 8)
            {
                return;
            }
            bool active = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive;
            Write($"  - 破碎都市激活：{active}；目标条：{ObjectiveTitle()}；剪影 {CountNamed("Silhouette")} 个");
            string item = LabelText("[ObjectiveHudHost]", "ObjectiveItemText1");
            string note = LabelText("[ObjectiveHudHost]", "ObjectiveNote");
            Write($"  - 目标条第 2 项：{item}；备注行：{note}");
            Check(item.Contains("：在地面") || item.Contains("：已装上") || item.Contains("：摧毁监听节点后掉落"), "远征中目标条写出关键物现状或获得方式");
            Write($"  - 世界特效活动中 {VfxActive()} 个");
            CheckEnemiesFromTable(Campaign.Regions.FracturedCityLayout.RegionId, Campaign.Content.EnemyCatalog.ScoutId);
            CheckNoTextMarkers("破碎都市");
            int[] roster = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).Select(m => m.LogicId).ToArray();
            GameRoot.FracturedCity?.Exit(evacuateSuccess: false);
            Campaign.Regions.FoundryOutpostRegion.EnsureRegionRecordSeeded(CampaignSession.Current);
            UnlockLikeDeparture(Campaign.Regions.FoundryOutpostRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            GameRoot.StartFoundryOutpost(roster);
            Next(8, "测试捷径：标记铸造前哨外围可出征，进入");
        }

        private static void StepFoundry(double inStep)
        {
            if (inStep >= 3 && !SessionState.GetBool(K + "VfxRaised", false))
            {
                SessionState.SetBool(K + "VfxRaised", true);
                CampaignState state = CampaignSession.Current;
                RegionEnemyRecord enemy = state?.RegionEnemies?.FirstOrDefault(e =>
                    e != null && e.IsAlive && e.RegionId == Campaign.Regions.FoundryOutpostLayout.RegionId);
                Vector2 at = enemy != null ? enemy.Position : Vector2.zero;
                Campaign.Feedback.FeedbackCues.RaiseAt(Campaign.Feedback.FeedbackCueId.EnemyHit, at);
                Campaign.Feedback.FeedbackCues.RaiseAt(Campaign.Feedback.FeedbackCueId.ReactionMeltOverload, at);
                SessionState.SetInt(K + "VfxFrame", Time.frameCount);
                Write($"  - 在敌人位置 {at} 报一次命中 + 熔穿过载时刻");
                return;
            }
            if (SessionState.GetBool(K + "VfxRaised", false) && !SessionState.GetBool(K + "VfxChecked", false)
                && Time.frameCount >= SessionState.GetInt(K + "VfxFrame", 0) + 2)
            {
                SessionState.SetBool(K + "VfxChecked", true);
                int activeVfx = VfxActive();
                Check(activeVfx >= 2, $"命中/反应时刻生成了世界特效（活动 {activeVfx} 个，贴图运行时加载）");
                return;
            }
            if (inStep < 8)
            {
                return;
            }
            bool active = GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive;
            Write($"  - 铸造前哨激活：{active}；目标条：{ObjectiveTitle()}；剪影 {CountNamed("Silhouette")} 个；世界特效活动中 {VfxActive()} 个");
            CheckEnemiesFromTable(Campaign.Regions.FoundryOutpostLayout.RegionId, Campaign.Content.EnemyCatalog.ArmorBotId);
            CheckNoTextMarkers("铸造前哨");
            GameRoot.FoundryOutpost?.Exit(evacuateSuccess: false);
            GameRoot.ResumeHomeValley();
            Next(9, "回到归还谷地");
        }

        private static void StepBackHome(double inStep)
        {
            if (inStep < 4)
            {
                return;
            }
            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            Write($"  - 归还谷地激活：{active}");
            CheckNoTextMarkers("回到归还谷地");
            Finish(active ? "完成" : "回不到归还谷地");
        }

        // ── 工具 ────────────────────────────────────────────────────

        private static void Next(int step, string message)
        {
            Write(message);
            SessionState.SetInt(K + "Step", step);
            SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
        }

        private static void Finish(string reason)
        {
            int errors = SessionState.GetInt(K + "Errors", 0);
            bool pass = reason == "完成" && errors == 0;
            Write($"结论：{(pass ? "PASS" : "FAIL")}（{reason}，报错 {errors} 条）");
            SessionState.SetBool(K + "Active", false);
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            try
            {
                InputRouter.DebugSetReader(null);
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                string saves = SessionState.GetString(K + "Saves", null);
                if (!string.IsNullOrEmpty(saves) && Directory.Exists(saves))
                {
                    Directory.Delete(saves, true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响结论。
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(pass ? 0 : 1);
            }
            else if (EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
            }
        }

        private static void Write(string line)
        {
            string path = SessionState.GetString(K + "Out", null);
            if (!string.IsNullOrEmpty(path))
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private static Button FindActiveButton(string name) =>
            Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(b => b.name == name && b.isActiveAndEnabled && b.interactable);

        /// <summary>测试捷径：满足区域门禁并记一次出征（真实出征由 ExpeditionDepartureService.TryDepart 做同样的事）。</summary>
        private static void UnlockLikeDeparture(RegionRecord region, Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget target)
        {
            if (region == null)
            {
                Write("  ✗ 区域记录不存在");
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
                return;
            }
            if (region.State == RegionState.Locked)
            {
                region.State = RegionState.Available;
            }
            region.ExpeditionCount += 1;
            CampaignObjectiveTracker.OnDeparted(CampaignSession.Current, target);
        }

        private static int VfxActive()
        {
            GameObject host = GameObject.Find("[FeedbackVfxHost]");
            var presenter = host != null ? host.GetComponent<View.FeedbackVfxPresenter>() : null;
            return presenter != null ? presenter.ActiveCount : -1;
        }

        private static int[] HomeMachines() =>
            MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == Campaign.Regions.HomeValleyLayout.RegionId)
                .Select(m => m.LogicId).ToArray();

        private static int CountNamed(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Count(t => t.name == name);

        private static string ObjectiveTitle()
        {
            GameObject host = GameObject.Find("[ObjectiveHudHost]");
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            Label title = doc?.rootVisualElement?.Q<Label>("ObjectiveTitle");
            return title != null ? $"“{title.text}”" : "（目标条节点没找到）";
        }

        private static void Check(bool ok, string message)
        {
            Write((ok ? "  ✓ " : "  ✗ ") + message);
            if (!ok)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
        }

        /// <summary>FG0-DATA-01：扫描当前所有可见界面文本（UI Toolkit 与 UGUI），任何 ⟦key⟧ 缺失标记都算失败——
        /// 缺失的文本键在正常流程里必须被发现，而不是靠人眼。</summary>
        private static void CheckNoTextMarkers(string where)
        {
            var hits = new System.Collections.Generic.List<string>();
            int scanned = 0;
            foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (doc.rootVisualElement == null)
                {
                    continue;
                }
                doc.rootVisualElement.Query<TextElement>().ForEach(t =>
                {
                    scanned++;
                    if (Localization.GameText.ContainsMarker(t.text))
                    {
                        hits.Add(t.text);
                    }
                });
            }
            foreach (UnityEngine.UI.Text t in Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                scanned++;
                if (Localization.GameText.ContainsMarker(t.text))
                {
                    hits.Add(t.text);
                }
            }
            Check(hits.Count == 0 && scanned > 0,
                $"{where}：扫描界面文本 {scanned} 个，缺失键标记 {hits.Count} 个{(hits.Count > 0 ? "：" + string.Join("｜", hits.Take(5)) : string.Empty)}");
        }

        /// <summary>FG0-DATA-01：Play 模式下区域播种的敌人生命来自 fg.TbMechEnemy。</summary>
        private static void CheckEnemiesFromTable(string regionId, string enemyTypeId)
        {
            float expected = Campaign.Content.FgContentTables.Enemy(enemyTypeId).MaxHp;
            RegionEnemyRecord[] enemies = CampaignSession.Current?.RegionEnemies?
                .Where(e => e != null && e.RegionId == regionId && e.EnemyTypeId == enemyTypeId).ToArray() ?? Array.Empty<RegionEnemyRecord>();
            Check(enemies.Length > 0 && enemies.All(e => Mathf.Approximately(e.MaxHealth, expected)),
                $"{enemyTypeId} 播种 {enemies.Length} 个，MaxHealth 全部等于表值 {expected}");
        }

        private static Transform FindNamed(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(t => t.name == name);

        private static string LabelText(string hostName, string labelName)
        {
            GameObject host = GameObject.Find(hostName);
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            Label label = doc?.rootVisualElement?.Q<Label>(labelName);
            return label != null ? label.text ?? string.Empty : "（节点没找到）";
        }

        /// <summary>模拟按下一次键：只在下一帧报告按下。</summary>
        private static void PressKey(KeyCode key)
        {
            InputRouter.DebugSetReader(new ScriptedReader { Key = key, KeyFrame = Time.frameCount + 1 });
        }

        /// <summary>模拟鼠标左键点世界里一点：光标移到它的屏幕位置，下一帧按下、再下一帧抬起（和人点一次一样）。</summary>
        private static void ClickWorld(Vector3 world)
        {
            Camera cam = Camera.main;
            Vector3 screen = cam != null ? cam.WorldToScreenPoint(world) : Vector3.zero;
            InputRouter.DebugSetReader(new ScriptedReader
            {
                Mouse = new Vector3(screen.x, screen.y, 0f),
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
            });
        }

        private sealed class ScriptedReader : IInputReader
        {
            public KeyCode Key = KeyCode.None;
            public int KeyFrame = -1;
            public Vector3 Mouse;
            public int DownFrame = -1;
            public int UpFrame = -1;

            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => key == Key && Time.frameCount == KeyFrame;
            public bool GetMouseButtonDown(int button) => button == 0 && Time.frameCount == DownFrame;
            public bool GetMouseButtonUp(int button) => button == 0 && Time.frameCount == UpFrame;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
        }
    }
}
