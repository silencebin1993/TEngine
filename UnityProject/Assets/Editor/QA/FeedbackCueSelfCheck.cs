using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8-CONTENT-01 / AC-AUD-001 / DEBT-ER8CONTENT01-01 / DEBT-ER7CORE01-01 的自动验收：
    /// 反馈时刻表完整、每个音效名都有真实音频文件、AC-AUD-001 十一类都同时有声音和常显字幕、
    /// 字幕文字零禁用词零内部 ID、真实入口（电网重算、Boss 状态机）在状态翻转时恰好出声一次、
    /// 设置音量能推到音频模块、字幕条在四种分辨率下不越界。
    ///
    /// 编辑器非 Play 下运行：<see cref="FeedbackCues"/> 此时只记录“本应发声”的音效名，不碰音频模块，
    /// 断言读的就是这份记录。已并入 <c>CellFrameworkValidate.RunAll</c>（tools/unity-validate.sh 默认跑它）。
    /// </summary>
    public static class FeedbackCueSelfCheck
    {
        private const string SfxFolder = "Assets/GameRes/Raw/Audios/Sfx/";
        private const string CaptionHudUxml = "Assets/GameRes/Raw/UI/Feedback/FeedbackCaptionHud.uxml";

        // 与 ER2-THEME-01 / ER8-CONTENT-01 静态审计同一张禁用词表。
        internal static readonly Regex ForbiddenWords = new Regex("细胞|孢子|菌丝|基因|器官|吞噬|代谢|谱系|萌生|债兽");

        // 内部 ID 的典型形态：snake_case 标识符、区域前缀、占位字样。
        internal static readonly Regex InternalIdPattern = new Regex(@"[a-z]+_[a-z0-9_]+|home_valley:|placeholder|TODO", RegexOptions.IgnoreCase);

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：反馈音效与字幕")]
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

        /// <summary>把结果追加到 <paramref name="report"/>，返回失败条数。</summary>
        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[反馈] 音效与字幕（ER8-CONTENT-01 AC-AUD-001）");

            bool savedSubtitles = GameSettings.SubtitlesEnabled;
            try
            {
                CheckCatalogComplete();
                CheckSfxAssetsExist();
                CheckContentSfxResolved();
                CheckAcAud001Categories();
                CheckCaptionTexts();
                CheckPlayerFacingEnumTexts();
                CheckRaiseCaptionAndThrottle();
                CheckSubtitlesSetting();
                CheckPowerGridEdges();
                CheckBossTransitions();
                CheckSettingsBridge();
                CheckCaptionHudLayout();
                CheckMainMenuClickSounds();
            }
            catch (Exception e)
            {
                Fail($"反馈自检抛异常：{e}");
            }
            finally
            {
                // 自检改过“字幕”开关（写 PlayerPrefs），无论成败都还原成运行前的值。
                if (GameSettings.SubtitlesEnabled != savedSubtitles)
                {
                    GameSettings.SetSubtitlesEnabled(savedSubtitles);
                }
                FeedbackCues.ResetForTests();
            }
            return _fail;
        }

        // ── 静态：表与资源 ────────────────────────────────────────────────

        private static void CheckCatalogComplete()
        {
            var missing = new List<string>();
            for (int i = 1; i < (int)FeedbackCueId.Max; i++)
            {
                FeedbackCueDef def = FeedbackCueCatalog.Get((FeedbackCueId)i);
                if (def == null || (int)def.Id != i)
                {
                    missing.Add(((FeedbackCueId)i).ToString());
                }
            }
            Expect(missing.Count == 0, missing.Count == 0
                ? $"反馈时刻表覆盖全部 {(int)FeedbackCueId.Max - 1} 个 id"
                : "反馈时刻缺定义：" + string.Join("、", missing));
        }

        private static void CheckSfxAssetsExist()
        {
            List<string> ids = FeedbackCues.CollectAllSfxIds();
            var missing = new List<string>();
            var badLength = new List<string>();
            foreach (string id in ids)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SfxFolder + id + ".wav");
                if (clip == null)
                {
                    missing.Add(id);
                }
                else if (clip.length < 0.02f || clip.length > 4f)
                {
                    badLength.Add($"{id}({clip.length:F2}s)");
                }
            }
            Expect(missing.Count == 0, missing.Count == 0
                ? $"全部 {ids.Count} 个音效名都有真实音频文件（{SfxFolder}）"
                : "音效文件缺失：" + string.Join("、", missing));
            Expect(badLength.Count == 0, badLength.Count == 0
                ? "音效时长都在 0.02～4 秒之间"
                : "音效时长异常：" + string.Join("、", badLength));
        }

        private static void CheckContentSfxResolved()
        {
            var placeholders = MechanicalContentFacade.All.Values
                .Where(d => d.SfxId != null && d.SfxId.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(d => d.Id)
                .ToList();
            Expect(placeholders.Count == 0, placeholders.Count == 0
                ? $"内容目录 {MechanicalContentFacade.All.Count} 条的 SfxId 零占位字样"
                : "内容目录仍有占位 SfxId：" + string.Join("、", placeholders));

            // 发声类内容必须有专属音色：武器、反应、底盘、敌人、建筑。结构件/固件允许空（刻意不发声）。
            var mustSound = MechanicalContentFacade.All.Values
                .Where(d => d.Category != MechanicalContentCategory.Structure && d.Category != MechanicalContentCategory.Firmware)
                .Where(d => string.IsNullOrEmpty(d.SfxId))
                .Select(d => d.Id)
                .ToList();
            Expect(mustSound.Count == 0, mustSound.Count == 0
                ? "武器/功能/反应/底盘/敌人/建筑都登记了专属音色"
                : "应发声的内容没有音色：" + string.Join("、", mustSound));
        }

        private static void CheckAcAud001Categories()
        {
            foreach (KeyValuePair<string, FeedbackCueId[]> category in FeedbackCueCatalog.AcAud001Categories)
            {
                var problems = new List<string>();
                foreach (FeedbackCueId id in category.Value)
                {
                    FeedbackCueDef def = FeedbackCueCatalog.Get(id);
                    if (def == null)
                    {
                        problems.Add($"{id} 无定义");
                        continue;
                    }
                    if (string.IsNullOrEmpty(def.SfxId))
                    {
                        problems.Add($"{id} 无声音");
                    }
                    if (def.CaptionMode != FeedbackCaptionMode.Always || string.IsNullOrEmpty(def.Tag))
                    {
                        problems.Add($"{id} 缺常显字幕（非声音反馈不能被字幕开关关掉）");
                    }
                }
                Expect(problems.Count == 0, problems.Count == 0
                    ? $"AC-AUD-001「{category.Key}」：{category.Value.Length} 个时刻都有声音 + 常显字幕"
                    : $"AC-AUD-001「{category.Key}」：" + string.Join("；", problems));
            }
        }

        private static void CheckCaptionTexts()
        {
            var bad = new List<string>();
            foreach (FeedbackCueDef def in FeedbackCueCatalog.All)
            {
                foreach (string text in new[] { def.Tag, def.Caption })
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    if (ForbiddenWords.IsMatch(text) || InternalIdPattern.IsMatch(text))
                    {
                        bad.Add($"{def.Id}:“{text}”");
                    }
                }
            }
            Expect(bad.Count == 0, bad.Count == 0
                ? "字幕标签/正文零禁用词、零内部 ID（AC-THEME-001）"
                : "字幕文字违规：" + string.Join("、", bad));
        }

        /// <summary>本轮修掉的动态拼接泄漏（工单面板、资源 HUD）：每个枚举值/原因码都必须映射成
        /// 零 ASCII 字母的玩家文字，新增枚举值忘了补映射时这里会红。</summary>
        private static void CheckPlayerFacingEnumTexts()
        {
            var ascii = new Regex("[A-Za-z]");
            var bad = new List<string>();
            foreach (WorkOrderKind kind in Enum.GetValues(typeof(WorkOrderKind)))
            {
                string t = GameLogic.UI.WorkOrder.WorkOrderPanelUIToolkit.KindText(kind);
                if (string.IsNullOrEmpty(t) || ascii.IsMatch(t) || t == "工作") bad.Add($"工单类型 {kind}→“{t}”");
            }
            foreach (WorkOrderState state in Enum.GetValues(typeof(WorkOrderState)))
            {
                string t = GameLogic.UI.WorkOrder.WorkOrderPanelUIToolkit.StateText(state);
                if (string.IsNullOrEmpty(t) || ascii.IsMatch(t)) bad.Add($"工单状态 {state}→“{t}”");
            }
            foreach (string code in new[] { "machine-died", "machine-not-found", "target-destroyed", "source-vanished",
                         "cargo-lost", "path-blocked", "storage-full:need=3:have=0", "some-new-code" })
            {
                string t = GameLogic.UI.WorkOrder.WorkOrderPanelUIToolkit.ReasonText(code);
                if (string.IsNullOrEmpty(t) || ascii.IsMatch(t)) bad.Add($"工单原因 {code}→“{t}”");
            }
            foreach (ResourceTransactionState state in Enum.GetValues(typeof(ResourceTransactionState)))
            {
                string t = CampaignEconomyLedger.StateDisplayName(state);
                if (string.IsNullOrEmpty(t) || ascii.IsMatch(t)) bad.Add($"交易状态 {state}→“{t}”");
            }
            foreach (string res in new[] { CampaignEconomyLedger.ResourceScrap, CampaignEconomyLedger.ResourceTechData,
                         CampaignEconomyLedger.ResourcePower, "UnknownType" })
            {
                string t = CampaignEconomyLedger.ResourceDisplayName(res);
                if (string.IsNullOrEmpty(t) || ascii.IsMatch(t)) bad.Add($"资源 {res}→“{t}”");
            }
            Expect(bad.Count == 0, bad.Count == 0
                ? "工单类型/状态/原因码、资源名、交易状态全部映射为玩家文字（零英文枚举、零原因码，AC-THEME-001）"
                : "玩家文字映射缺失：" + string.Join("、", bad));
        }

        // ── 行为：统一出口 ───────────────────────────────────────────────

        private static void CheckRaiseCaptionAndThrottle()
        {
            FeedbackCues.ResetForTests();
            FeedbackCues.Raise(FeedbackCueId.Takeover, "#7");
            Expect(FeedbackCues.CountOf(FeedbackCueId.Takeover) == 1 && FeedbackCues.LastSfxId == "sfx_takeover",
                $"接管：触发 1 次、请求音效 sfx_takeover（实际 {FeedbackCues.CountOf(FeedbackCueId.Takeover)} / {FeedbackCues.LastSfxId}）");
            Expect(FeedbackCues.ActiveCaptions.Count == 1 && FeedbackCues.LastCaptionText == "【接管】已接管：#7",
                $"接管：字幕条出现一行“【接管】已接管：#7”（实际 {FeedbackCues.ActiveCaptions.Count} 行 / {FeedbackCues.LastCaptionText}）");

            FeedbackCues.Raise(FeedbackCueId.Takeover, "#7");
            Expect(FeedbackCues.ActiveCaptions.Count == 1 && FeedbackCues.ActiveCaptions[0].Count == 2,
                $"同一条字幕在显示期间重复触发合并为 ×2，不刷屏（实际 {FeedbackCues.ActiveCaptions.Count} 行，×{(FeedbackCues.ActiveCaptions.Count > 0 ? FeedbackCues.ActiveCaptions[0].Count : 0)}）");

            int before = FeedbackCues.SfxRequestCount;
            for (int i = 0; i < 20; i++)
            {
                FeedbackCues.Raise(FeedbackCueId.WeaponFire, null, "sfx_cannon_fire");
            }
            int played = FeedbackCues.SfxRequestCount - before;
            Expect(played == 1 && FeedbackCues.CountOf(FeedbackCueId.WeaponFire) == 20,
                $"高频开火节流：同一瞬间 20 次触发只发声 1 次（实际 {played}），计数仍记 20");
            Expect(FeedbackCues.LastSfxId == "sfx_cannon_fire", $"内容专属音色覆盖默认音（实际 {FeedbackCues.LastSfxId}）");
            Expect(FeedbackCues.ActiveCaptions.All(c => c.Cue != FeedbackCueId.WeaponFire),
                "开火只发声、不出字幕（弹体与伤害数字是等价视觉）");

            for (int i = 0; i < FeedbackCues.MaxVisibleCaptions + 3; i++)
            {
                FeedbackCues.Raise(FeedbackCueId.ProductionComplete, "#" + i);
            }
            Expect(FeedbackCues.ActiveCaptions.Count == FeedbackCues.MaxVisibleCaptions
                   && FeedbackCues.ActiveCaptions[FeedbackCues.ActiveCaptions.Count - 1].Text.EndsWith("#" + (FeedbackCues.MaxVisibleCaptions + 2)),
                $"字幕条最多 {FeedbackCues.MaxVisibleCaptions} 行，挤掉最旧的、最新的在最后（实际 {FeedbackCues.ActiveCaptions.Count} 行）");
        }

        private static void CheckSubtitlesSetting()
        {
            FeedbackCues.ResetForTests();
            GameSettings.SetSubtitlesEnabled(false);
            FeedbackCues.Raise(FeedbackCueId.MachineDamaged, "#3 耐久 40/100");
            FeedbackCues.Raise(FeedbackCueId.PowerLost, "装配站 停机");
            bool damagedHidden = FeedbackCues.ActiveCaptions.All(c => c.Cue != FeedbackCueId.MachineDamaged);
            bool powerShown = FeedbackCues.ActiveCaptions.Any(c => c.Cue == FeedbackCueId.PowerLost);
            Expect(damagedHidden && powerShown,
                "字幕关闭：声音字幕（受损）隐藏，但断电这类事件通知照常显示（AC-AUD-001 非声音反馈不受字幕开关影响）");
            Expect(FeedbackCues.LastSfxId == "sfx_power_down" && FeedbackCues.CountOf(FeedbackCueId.MachineDamaged) == 1,
                "字幕关闭不影响发声");

            FeedbackCues.ResetForTests();
            GameSettings.SetSubtitlesEnabled(true);
            FeedbackCues.Raise(FeedbackCueId.MachineDamaged, "#3 耐久 40/100");
            Expect(FeedbackCues.ActiveCaptions.Any(c => c.Cue == FeedbackCueId.MachineDamaged),
                "字幕开启：受损声音出字幕“【受损】#3 耐久 40/100”（AC-ACC-002 关键声音有字幕等价反馈）");
        }

        // ── 行为：真实入口 ───────────────────────────────────────────────

        private static void CheckPowerGridEdges()
        {
            FeedbackCues.ResetForTests();
            CampaignState state = CampaignState.CreateNew("feedback-selfcheck", "Standard", 1);
            BuildingRecord core = NewBuilding(HomeValleyLayout.BuildingTypeCore);
            BuildingRecord station = NewBuilding(HomeValleyLayout.BuildingTypeAssemblyStation);
            BuildingRecord generator = NewBuilding(HomeValleyLayout.BuildingTypeGenerator);
            generator.ConstructionState = BuildingConstructionState.Damaged;
            state.BuildingRecords = new[] { core, station, generator };

            // 基础供给 20：核心 10 分到电，装配站 25 分不到 → 断电一次。
            HomeValleyPowerGrid.Recompute(state);
            Expect(station.PowerState == BuildingPowerState.Brownout && FeedbackCues.CountOf(FeedbackCueId.PowerLost) == 1,
                $"电网：装配站分不到电时“断电”恰好触发 1 次（实际状态 {station.PowerState}，次数 {FeedbackCues.CountOf(FeedbackCueId.PowerLost)}）");
            Expect(FeedbackCues.LastCaptionText != null && FeedbackCues.LastCaptionText.Contains("装配站"),
                $"断电字幕用建筑展示名，不漏内部 ID（实际 {FeedbackCues.LastCaptionText}）");

            HomeValleyPowerGrid.Recompute(state);
            HomeValleyPowerGrid.Recompute(state);
            Expect(FeedbackCues.CountOf(FeedbackCueId.PowerLost) == 1,
                "电网：状态不变的重复重算（读档/刷新）不重复报断电");

            generator.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(state);
            Expect(station.PowerState == BuildingPowerState.Powered && FeedbackCues.CountOf(FeedbackCueId.PowerRestored) == 1,
                $"电网：发电机修好后“供电恢复”恰好 1 次（实际状态 {station.PowerState}，次数 {FeedbackCues.CountOf(FeedbackCueId.PowerRestored)}）");
        }

        private static BuildingRecord NewBuilding(string typeId)
        {
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) profile);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = profile.PowerPriority,
                Health = 100f,
            };
        }

        private static void CheckBossTransitions()
        {
            FeedbackCues.ResetForTests();
            CampaignState state = CampaignState.CreateNew("feedback-selfcheck-boss", "Standard", 2);
            var region = new RegionRecord { RegionId = FoundryOutpostLayout.RegionId };
            bool shielded = FoundryOutpostCoreBoss.TryTransition(state, region, CoreBossState.Shielded);
            bool phase1 = FoundryOutpostCoreBoss.TryTransition(state, region, CoreBossState.Phase1);
            Expect(shielded && phase1 && FeedbackCues.CountOf(FeedbackCueId.BossPhase) == 2,
                $"Boss：每次合法阶段转换“首领阶段”各出声一次（实际 {FeedbackCues.CountOf(FeedbackCueId.BossPhase)} 次）");
            Expect(FeedbackCues.LastCaptionText != null && FeedbackCues.LastCaptionText.Contains(FoundryOutpostCoreBoss.DisplayPhaseText(region))
                   && !FeedbackCues.LastCaptionText.Contains("Phase1"),
                $"Boss 字幕用玩家文案、不漏枚举名（实际 {FeedbackCues.LastCaptionText}）");

            FoundryOutpostCoreBoss.TryTransition(state, region, CoreBossState.Transition);
            FoundryOutpostCoreBoss.TryTransition(state, region, CoreBossState.Phase2);
            FoundryOutpostCoreBoss.TryTransition(state, region, CoreBossState.Destroyed);
            Expect(FeedbackCues.CountOf(FeedbackCueId.BossDestroyed) == 1,
                "Boss：摧毁主核心出“主核心已摧毁”");
        }

        private static void CheckSettingsBridge()
        {
            int revision = GameSettings.Revision;
            GameSettings.SetSfxVolume(GameSettings.SfxVolume);
            Expect(GameSettings.Revision == revision + 1, "设置每次写盘版本号 +1（音量桥据此重新应用）");
            Expect(!FeedbackCues.AudioSettingsInSync, "改设置后音量桥标记为待同步");
            FeedbackCues.Tick();
            Expect(FeedbackCues.AudioSettingsInSync, "下一帧 Tick 后设置音量已推给音频模块（设置滑条不再是摆设）");
        }

        /// <summary>DEBT-ER2BOOT01-04 音效部分：uGUI 主菜单每个按钮按下都有点击音（UI Toolkit 面板的点击音在共享面板根上）。</summary>
        private static void CheckMainMenuClickSounds()
        {
            const string prefabPath = "Assets/GameRes/Raw/UI/MainMenuUI.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Fail($"找不到主菜单预制体 {prefabPath}");
                return;
            }
            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                int attached = MainMenuUI.AttachClickSounds(instance);
                FeedbackCues.ResetForTests();
                int invoked = 0;
                foreach (UnityEngine.UI.Button button in instance.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                {
                    if (button.onClick.GetPersistentEventCount() > 0)
                    {
                        continue; // 预制体里另有序列化监听的按钮不在编辑器里触发（避免调用到未绑定的窗口逻辑）。
                    }
                    button.onClick.Invoke();
                    invoked++;
                }
                Expect(attached >= 10 && invoked == attached && FeedbackCues.CountOf(FeedbackCueId.UiClick) == invoked,
                    $"主菜单 {attached} 个按钮按下都有点击音（触发 {invoked} 个，点击音 {FeedbackCues.CountOf(FeedbackCueId.UiClick)} 次）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
                FeedbackCues.ResetForTests();
            }
        }

        private static void CheckCaptionHudLayout()
        {
            string result = UiToolkitLayoutProbe.Probe(CaptionHudUxml, "FeedbackCaptionRoot", true,
                UiToolkitLayoutProbe.DefaultPanelSettingsPath, root =>
                {
                    // 5 行全部显示、塞满最长的真实文案，覆盖运行时最拥挤的情形。
                    root.Query<VisualElement>(className: "fc-row").ForEach(row => row.RemoveFromClassList("fc-row-hidden"));
                    root.Query<Label>(className: "fc-tag").ForEach(l => l.text = "【遭到攻击】");
                    root.Query<Label>(className: "fc-text").ForEach(l =>
                        l.text = "信号中断：#12 受到干扰，3 秒内离开干扰区可恢复；" + UiToolkitLayoutProbe.StressText);
                    root.Query<Label>(className: "fc-count").ForEach(l => l.text = "×99");
                });
            bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
            Expect(pass, pass ? "字幕条布局探针 PASS（四种分辨率 × 5 行超长文字）" : "字幕条布局探针未通过：\n" + result);
        }

        // ── 报告 ────────────────────────────────────────────────────────

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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
