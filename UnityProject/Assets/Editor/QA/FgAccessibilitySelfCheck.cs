using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-UX-02 可访问性全集（FG13 FGR-UX-060；FGT-UX-007“色盲模式下所有状态仍然可以通过形状区分”、FGT-UX-008“只用鼠标完成主流程”；
    /// 负向：UI 缩放 150% + 1280×720）。
    /// A 设置项与旧设置迁移 / 本机持久化 / 恢复默认 / 减少动态效果总开关；B FGT-UX-007 形状（状态 / 沾染 26～29 / 相态 / 物质纹样两两不同）；
    /// C 色觉重映射（三种模式的矩阵、着色器全局、模拟色觉下颜色差变大）；D 界面高对比度与色盲语义色（布局探针实测样式）；
    /// E 文字大小（UI Toolkit 与 uGUI 实测字号）+ 负向 150% × 1280×720（越界探针 + 最小可点尺寸实测）；F 按住改切换（镜头平移 / 直控移动 / 长按交互，反方向、换作用域、完成后松开）；
    /// G 键盘指针（世界侧位置 / 左右键 / 真鼠标接管 / 关掉直通；界面侧在指针处真点到按钮）；H FGT-UX-008 只用鼠标的主流程入口；
    /// I 登记册承接项（顶栏固定物品排序 + 存档往返、研发树键盘平移 / 缩放、难度快捷键、名册队名、聚焦镜工坊改名、斑块 / 离开音效钩子）。
    /// </summary>
    public static class FgAccessibilitySelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKit = "Assets/GameRes/Raw/UI/UiKit/";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("QA/自检/设置/可访问性全集（FG15-UX-02）")]
        public static void RunFromMenu()
        {
            var sb = new StringBuilder();
            int fail = Run(sb);
            Debug.Log(sb.ToString());
            Debug.Log(fail == 0 ? "FgAccessibilitySelfCheck 全部通过" : $"FgAccessibilitySelfCheck 失败 {fail} 项");
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[可访问性全集] FG15-UX-02（FGR-UX-060；FGT-UX-007 / 008；负向 UI 150% + 1280×720）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写本机设置）");
                return 0;
            }
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Section("A 设置项 / 迁移 / 持久化 / 恢复默认", CheckSettings);
                Section("B FGT-UX-007 形状两两可分", CheckShapes);
                Section("C 色觉重映射", CheckColorVision);
                Section("D 高对比度与色盲语义色（界面）", CheckUiClasses);
                Section("E 文字大小 + 负向 UI 150% × 1280×720", CheckTextScaleAndNegative);
                Section("E2 负向叠加文字 150% 与高对比度", CheckTextScaleNegative);
                Section("F 按住改切换", CheckHoldToggle);
                Section("G 键盘指针", CheckKeyboardPointer);
                Section("H FGT-UX-008 只用鼠标的主流程（入口清点 + 真点按钮 + 直控鼠标走 / 交互）", CheckMouseOnly);
                Section("I 登记册承接项", CheckRegisterItems);
            }
            catch (Exception ex)
            {
                Fail("自检异常：" + ex);
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                InputRouter.ClearHoldLatches();
                InputRouter.Reset();
                ColorVision.ApplyToShaders(ColorblindMode.Off);
                AccessibilityApplier.ResetForTests();
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
            }
            Line($"  合计：通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A ────────────────────────────────────────────────────────────

        private static void CheckSettings()
        {
            var d = new GameSettingsData();
            Expect(GameSettings.ColorblindMode == ColorblindMode.Off && Mathf.Approximately(GameSettings.TextScale, 1f) && !GameSettings.HighContrastEnabled
                   && !GameSettings.KeyboardPointerEnabled && !GameSettings.ReduceMotion,
                $"新装默认：色盲关、文字 100%、高对比度关、键盘指针关、减少动态效果关（{GameSettings.ColorblindMode}/{GameSettings.TextScale}/{GameSettings.HighContrastEnabled}/{GameSettings.KeyboardPointerEnabled}/{GameSettings.ReduceMotion}）");

            // 旧设置（只有布尔“色盲安全图标”、没有新字段）→ 迁移成红绿；没写的新字段补默认。
            d.ColorblindSafeIconsEnabled = true;
            string json = Regex.Replace(JsonUtility.ToJson(d), "\"(ColorblindMode|HighContrastEnabled|TextScale|KeyboardPointerEnabled)\":[^,}]*,?", string.Empty).Replace(",}", "}");
            PlayerPrefs.SetString(SettingsPrefsKey, json);
            GameSettings.Load();
            Expect(GameSettings.ColorblindMode == ColorblindMode.RedGreen && Mathf.Approximately(GameSettings.TextScale, 1f) && !GameSettings.HighContrastEnabled,
                $"旧设置迁移：旧布尔开 → 红绿色盲；缺的新字段补默认（{GameSettings.ColorblindMode}/{GameSettings.TextScale}；JSON 无新字段 {!json.Contains("ColorblindMode")}）");

            // 设置面板的条目真的改到设置上。
            SettingItem mode = SettingsCatalog.Find("access.colorblind_mode");
            SettingItem text = SettingsCatalog.Find("access.text_scale");
            SettingItem hc = SettingsCatalog.Find("access.high_contrast");
            SettingItem motion = SettingsCatalog.Find("access.reduce_motion");
            SettingItem kb = SettingsCatalog.Find("access.kb_pointer");
            bool present = mode != null && text != null && hc != null && motion != null && kb != null && mode.Choices().Count == 4;
            Expect(present, $"可访问性分类有：文字大小、色盲模式（4 档：关 / 红绿 / 蓝黄 / 全色盲）、高对比度、减少动态效果、键盘指针（{present}）");
            if (!present)
            {
                return;
            }
            mode.SetIndex(2);
            text.SetFloat(9f);
            float clampedHigh = GameSettings.TextScale;
            text.SetFloat(0.1f);
            float clampedLow = GameSettings.TextScale;
            text.SetFloat(1.25f);
            hc.SetBool(true);
            kb.SetBool(true);
            Expect(GameSettings.ColorblindMode == ColorblindMode.BlueYellow && GameSettings.ColorblindSafeIconsEnabled && Mathf.Approximately(clampedHigh, 1.5f)
                   && Mathf.Approximately(clampedLow, 0.8f) && Mathf.Approximately(GameSettings.TextScale, 1.25f) && GameSettings.HighContrastEnabled && GameSettings.KeyboardPointerEnabled,
                $"面板条目生效：色盲 = 蓝黄（旧布尔同步为开）、文字大小夹在 80%～150%（{clampedLow}/{clampedHigh}）、高对比度、键盘指针");

            motion.SetBool(true);
            bool on = GameSettings.FlashReductionEnabled && !GameSettings.ScreenShakeEnabled && !GameSettings.ReactionSlowMotionEnabled && !GameSettings.ReactionCameraNudgeEnabled && motion.GetBool();
            GameSettings.SetScreenShakeEnabled(true);
            bool brokenByOne = !motion.GetBool();
            motion.SetBool(false);
            bool off = !GameSettings.FlashReductionEnabled && GameSettings.ScreenShakeEnabled && GameSettings.ReactionSlowMotionEnabled == d.ReactionSlowMotionEnabled
                       && GameSettings.ReactionCameraNudgeEnabled == d.ReactionCameraNudgeEnabled;
            Expect(on && brokenByOne && off, $"减少动态效果：一键 = 闪光减弱 + 关震屏 + 关慢放 + 关镜头推动；单改一项后总开关显示为关；关 = 四项回默认（{on}/{brokenByOne}/{off}）");

            // 本机持久化：重读后仍是刚才的值。
            GameSettings.Load();
            Expect(GameSettings.ColorblindMode == ColorblindMode.BlueYellow && Mathf.Approximately(GameSettings.TextScale, 1.25f) && GameSettings.HighContrastEnabled && GameSettings.KeyboardPointerEnabled,
                "持久化：色盲模式 / 文字大小 / 高对比度 / 键盘指针重读后不变");

            motion.SetBool(true);
            SettingsCatalog.ResetCategory(SettingsCategory.Accessibility);
            bool motionReset = !GameSettings.ReduceMotion && !motion.GetBool() && GameSettings.ReactionSlowMotionEnabled == d.ReactionSlowMotionEnabled
                               && GameSettings.ReactionCameraNudgeEnabled == d.ReactionCameraNudgeEnabled && GameSettings.ScreenShakeEnabled == d.ScreenShakeEnabled;
            Expect(GameSettings.ColorblindMode == ColorblindMode.Off && Mathf.Approximately(GameSettings.TextScale, 1f) && !GameSettings.HighContrastEnabled && !GameSettings.KeyboardPointerEnabled && motionReset,
                $"可访问性“恢复默认”连新项一起复位，先开“减少动态效果”再恢复默认后慢放 / 镜头推动 / 震屏也回默认、总开关显示为关（{GameSettings.ColorblindMode}/{GameSettings.TextScale}/{GameSettings.HighContrastEnabled}/{GameSettings.KeyboardPointerEnabled}/{motionReset}）");
        }

        // ── B ────────────────────────────────────────────────────────────

        private static void CheckShapes()
        {
            float2[] status = NamedReactionCatalog.BuildStatusVisuals();
            var statusCodes = new Dictionary<int, int>();
            var dupes = new List<string>();
            for (int b = 0; b < status.Length; b++)
            {
                if (status[b].x < 0f)
                {
                    continue;
                }
                int code = (int)status[b].x;
                if (statusCodes.TryGetValue(code, out int other))
                {
                    dupes.Add($"位 {other} 与位 {b} 都是 {NamedReactionCatalog.GlyphOf(code)}");
                }
                statusCodes[code] = b;
            }
            Expect(dupes.Count == 0 && statusCodes.Count == 30, dupes.Count == 0
                ? $"状态标签头顶图标 {statusCodes.Count} 个，形状码两两不同（只看形状、不看颜色也能分开）"
                : "状态标签形状重复：" + string.Join("；", dupes));

            int[] matterBits = { 26, 27, 28, 29 };
            bool matterIcons = matterBits.All(b => status[b].x >= 0f);
            bool matterNames = matterBits.All(b => StatusTagCatalog.Rows.Any(r => r != null && r.Bit == b && r.Kind == "status" && !string.IsNullOrEmpty(r.NameKey) && GameText.Get(r.NameKey) != r.NameKey));
            Expect(matterIcons && matterNames, $"DEBT-EMG2MAT01-03：沾染位 26～29（霜冻 / 熔渣 / 碎屑 / 遮蔽）有标签行名字与头顶图标（图标 {matterIcons}，名字 {matterNames}）");

            float2[] phases = EmgPhaseFeedback.BuildVisuals();
            var phaseCodes = phases.Where(p => p.x >= 0f).Select(p => (int)p.x).ToList();
            bool phaseDistinct = phaseCodes.Count == phaseCodes.Distinct().Count() && phaseCodes.Count >= 6;
            bool disjoint = phaseCodes.All(c => !statusCodes.ContainsKey(c));
            Expect(phaseDistinct && disjoint, $"DEBT-EMG2PHS01-03：相态图标 {phaseCodes.Count} 个两两不同（{phaseDistinct}），且与全部状态标签的形状都不同（{disjoint}；同一单位头顶并排显示）");

            bool roundTrip = statusCodes.Keys.Concat(phaseCodes).All(c => NamedReactionCatalog.ShapeIndex(NamedReactionCatalog.GlyphOf(c)) == c && c % NamedReactionCatalog.MarkStride < NamedReactionCatalog.Shapes.Length);
            Expect(roundTrip, "形状码 = 字形 + 32 × 角标，可逆（着色器按同一规则画字形与中心圆点 / 横杠角标）");

            string shader = File.ReadAllText("Assets/GameScripts/Main/Sim/Shaders/CombatInstanced.shader");
            bool shaderMarks = shader.Contains("int glyph = code % 32;") && shader.Contains("int mark = code / 32;");
            Expect(shaderMarks, "着色器头顶图标按“字形 = 码 % 32、角标 = 码 / 32”解码（与 NamedReactionCatalog.MarkStride 一致）");

            var patterns = new List<int>();
            for (int m = 1; m <= BinGames.Sim.Combat.EmgMatterConst.Count; m++)
            {
                patterns.Add(GameLogic.Campaign.Emergence.EmgMatterCatalog.Current.Rows[m].Pattern);
            }
            Expect(patterns.Count == patterns.Distinct().Count(), $"物质斑块 {patterns.Count} 种纹样两两不同（形状为主、颜色为辅，B15）");
            CheckFieldAndZoneGlyphs(status, statusCodes);
        }

        /// <summary>FG15-UX-02（FGT-UX-007；FG19 B15“覆盖物质与场地物”）：6 种场的环外观相同、区域波纹只按放置方式分，所以场心 / 区域中心叠字形。
        /// 走正式渲染缓冲（CombatRenderer.Draw → 图标实例），只看形状码、不看颜色。</summary>
        private static void CheckFieldAndZoneGlyphs(float2[] status, Dictionary<int, int> statusCodes)
        {
            float2[] fields = NamedReactionCatalog.BuildFieldVisuals();
            var fieldCodes = fields.Select(v => (int)v.x).ToList();
            bool fieldDistinct = fields.Length == 6 && fields.All(v => v.x >= 0f) && fieldCodes.Distinct().Count() == 6 && fieldCodes.All(c => !statusCodes.ContainsKey(c));
            Expect(fieldDistinct, $"6 种场（牵引 / 推斥 / 涡旋 / 磁 / 偏折 / 电晕）的场心字形两两不同、且不与任何状态字形相同（{string.Join(" ", fieldCodes.Select(NamedReactionCatalog.GlyphOf))}）");

            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            var k = new CombatKernel(cfg, 16);
            var renderer = new CombatRenderer();
            try
            {
                string[] zoneTags = { "Fire", "Acid", "Shock" };
                for (int i = 0; i < zoneTags.Length; i++)
                {
                    k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(i * 10.0, 0.0), 2f, 30f, 0f, NamedReactionCatalog.BitOf(zoneTags[i]));
                }
                for (int i = 0; i < 6; i++)
                {
                    k.SpawnField((ushort)(BinGames.Sim.Combat.EmgFieldConst.PortPull + i), i * 10.0, 30.0, 3f, 30f, 0, CombatFaction.Player);
                }
                renderer.SetStatusVisuals(status);
                renderer.SetPhaseVisuals(EmgPhaseFeedback.BuildVisuals());
                renderer.SetFieldVisuals(fields);
                renderer.Draw(k, null, 1f, double2.zero, 0.6f);
                CombatInstance[] icons = renderer.IconInstances.ToArray();
                var zoneIcons = icons.Where(c => Math.Abs(c.A.y) < 0.01f).OrderBy(c => c.A.x).Select(c => (int)c.B.y).ToList();
                var fieldIcons = icons.Where(c => Math.Abs(c.A.y - 30f) < 0.01f).OrderBy(c => c.A.x).Select(c => (int)c.B.y).ToList();
                var expectZone = zoneTags.Select(t => (int)status[math.tzcnt(NamedReactionCatalog.BitOf(t))].x).ToList();
                bool zonesOk = zoneIcons.SequenceEqual(expectZone) && zoneIcons.Distinct().Count() == 3;
                bool fieldsOk = fieldIcons.SequenceEqual(fieldCodes);
                Expect(zonesOk && fieldsOk && k.FieldCount == 6,
                    $"渲染缓冲：燃烧 / 腐蚀 / 电击区域中心叠各自状态字形（{string.Join(" ", zoneIcons.Select(NamedReactionCatalog.GlyphOf))}），6 个场的场心叠场字形（{string.Join(" ", fieldIcons.Select(NamedReactionCatalog.GlyphOf))}）——全色盲下只看形状也分得开");
            }
            finally
            {
                renderer.Dispose();
                k.Dispose();
            }
        }

        // ── C ────────────────────────────────────────────────────────────

        private static float Dist(Color a, Color b) => Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));

        private static float SeenGap(Color a, Color b, ColorblindMode mode, bool remap) =>
            Dist(ColorVision.Simulate(remap ? ColorVision.Apply(a, mode) : a, mode), ColorVision.Simulate(remap ? ColorVision.Apply(b, mode) : b, mode));

        private static void CheckColorVision()
        {
            Color red = new Color(0.86f, 0.2f, 0.2f), green = new Color(0.25f, 0.7f, 0.25f), blue = new Color(0.25f, 0.35f, 0.85f), teal = new Color(0.25f, 0.65f, 0.55f);
            float rgBefore = SeenGap(red, green, ColorblindMode.RedGreen, false), rgAfter = SeenGap(red, green, ColorblindMode.RedGreen, true);
            float byBefore = SeenGap(blue, teal, ColorblindMode.BlueYellow, false), byAfter = SeenGap(blue, teal, ColorblindMode.BlueYellow, true);
            Expect(rgAfter > rgBefore * 1.3f, $"红绿模式：红 / 绿在红绿色觉下的颜色差 {rgBefore:0.000} → {rgAfter:0.000}（变大）");
            Expect(byAfter > byBefore * 1.3f, $"蓝黄模式：蓝 / 青绿在蓝黄色觉下的颜色差 {byBefore:0.000} → {byAfter:0.000}（变大）");
            Color g = ColorVision.Apply(red, ColorblindMode.Achromat);
            Expect(Mathf.Approximately(g.r, g.g) && Mathf.Approximately(g.g, g.b) && ColorVision.Apply(red, ColorblindMode.Off) == red,
                $"全色盲 = 只看明暗（{g}），关闭 = 原色");

            ColorVision.ApplyToShaders(ColorblindMode.RedGreen);
            bool onRg = Mathf.Approximately(Shader.GetGlobalFloat(ColorVision.ShaderOnId), 1f) && Shader.GetGlobalMatrix(ColorVision.ShaderMatrixId) == ColorVision.MatrixFor(ColorblindMode.RedGreen);
            ColorVision.ApplyToShaders(ColorblindMode.Off);
            bool offOk = Mathf.Approximately(Shader.GetGlobalFloat(ColorVision.ShaderOnId), 0f);
            string shader = File.ReadAllText("Assets/GameScripts/Main/Sim/Shaders/CombatInstanced.shader");
            int uses = Regex.Matches(shader, "cvdRemap\\(").Count;
            Expect(onRg && offOk && uses >= 4, $"世界侧：着色器全局 _BgCvdOn / _BgCvdMatrix 随模式写入（{onRg}/{offOk}），头顶图标 / 斑块 / 效果 / 敌我色都过 cvdRemap（{uses - 1} 处调用）");
        }

        // ── D ────────────────────────────────────────────────────────────

        private static void CheckUiClasses()
        {
            Color labelOff = default, labelOn = default, toneRg = default, toneOff = default, toneAch = default;
            float btnBorder = -1f;
            string res = UiToolkitLayoutProbe.Probe(UiKit + "SettingsPanel.uxml", "SettingsRoot", false, UiToolkitLayoutProbe.DefaultPanelSettingsPath, root =>
            {
                var tone = new VisualElement { name = "A11yTone" };
                tone.AddToClassList("uk-status");
                tone.AddToClassList("uk-tone-good");
                var shape = new VisualElement { name = "A11yShape" };
                shape.AddToClassList("uk-shape-a");
                tone.Add(shape);
                root.Add(tone);
                root.Add(new Label("对比度") { name = "A11yLabel" });
                root.Add(new Button { name = "A11yButton", text = "按钮" });
            }, 1f, new[] { new Vector2Int(1920, 1080) }, (root, r) =>
            {
                VisualElement tree = root.panel.visualTree;
                labelOff = root.Q<Label>("A11yLabel").resolvedStyle.color;
                toneOff = root.Q("A11yShape").resolvedStyle.backgroundColor;
                AccessibilityApplier.ApplyClasses(tree, true, ColorblindMode.RedGreen);
                UiToolkitLayoutProbe.ForceLayout(root);
                labelOn = root.Q<Label>("A11yLabel").resolvedStyle.color;
                btnBorder = root.Q<Button>("A11yButton").resolvedStyle.borderTopWidth;
                toneRg = root.Q("A11yShape").resolvedStyle.backgroundColor;
                AccessibilityApplier.ApplyClasses(tree, false, ColorblindMode.Achromat);
                UiToolkitLayoutProbe.ForceLayout(root);
                toneAch = root.Q("A11yShape").resolvedStyle.backgroundColor;
                AccessibilityApplier.ApplyClasses(tree, false, ColorblindMode.Off);
            });
            Expect(labelOn == Color.white && Mathf.Approximately(btnBorder, 2f),
                $"高对比度：面板根挂 uk-high-contrast 后文字纯白（{labelOff} → {labelOn}）、按钮 2px 白框（{btnBorder}）");
            Expect(toneRg != toneOff && Mathf.Approximately(toneAch.r, toneAch.g) && Mathf.Approximately(toneAch.g, toneAch.b),
                $"色盲语义色：红绿模式“好”状态色换成天蓝（{toneOff} → {toneRg}），全色盲换成灰阶（{toneAch}）；状态图标本身仍是形状组合");
            Expect(res.StartsWith("PASS", StringComparison.Ordinal) || !res.Contains("加载失败"), "设置面板可载入探针");
        }

        // ── E ────────────────────────────────────────────────────────────

        private static readonly string[] NegativePanels =
        {
            "SettingsPanel.uxml|SettingsRoot", "PauseMenu.uxml|PauseMenuRoot",
        };

        private static void CheckTextScaleAndNegative()
        {
            // 文字大小（UI Toolkit）：字号 = 原字号 × 倍率，面板代码改过的字号以新值为原字号，倍率回 1 复原。
            float baseSize = 0f, scaled = 0f, back = 0f, rebased = 0f;
            UiToolkitLayoutProbe.Probe(UiKit + "SettingsPanel.uxml", "SettingsRoot", false, UiToolkitLayoutProbe.DefaultPanelSettingsPath,
                root => root.Add(new Label("文字大小") { name = "A11yText" }), 1f, new[] { new Vector2Int(1920, 1080) }, (root, r) =>
                {
                    AccessibilityApplier.ResetForTests();
                    Label l = root.Q<Label>("A11yText");
                    baseSize = l.resolvedStyle.fontSize;
                    AccessibilityApplier.ApplyTextScale(root, 1.5f);
                    UiToolkitLayoutProbe.ForceLayout(root);
                    scaled = l.resolvedStyle.fontSize;
                    l.style.fontSize = 20f; // 面板代码改字号
                    UiToolkitLayoutProbe.ForceLayout(root);
                    AccessibilityApplier.ApplyTextScale(root, 1.5f);
                    UiToolkitLayoutProbe.ForceLayout(root);
                    rebased = l.resolvedStyle.fontSize;
                    AccessibilityApplier.ApplyTextScale(root, 1f);
                    UiToolkitLayoutProbe.ForceLayout(root);
                    back = l.resolvedStyle.fontSize;
                });
            Expect(baseSize > 0f && Mathf.Abs(scaled - baseSize * 1.5f) < 0.11f && Mathf.Abs(rebased - 30f) < 0.11f && Mathf.Abs(back - 20f) < 0.11f,
                $"文字大小（UI Toolkit）：{baseSize}px × 150% = {scaled}px；面板改成 20px 后 → {rebased}px；回 100% → {back}px");

            // 文字大小（uGUI 旧链：主菜单等）。
            var go = new GameObject("__A11yUgui") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var tmp = go.AddComponent<TMPro.TextMeshProUGUI>();
                tmp.fontSize = 24f;
                AccessibilityApplier.ApplyTextScaleUgui(go.transform, 1.25f);
                float up = tmp.fontSize;
                AccessibilityApplier.ApplyTextScaleUgui(go.transform, 1f);
                Expect(Mathf.Approximately(up, 30f) && Mathf.Approximately(tmp.fontSize, 24f), $"文字大小（uGUI TMP）：24 → {up} → {tmp.fontSize}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            // 负向：UI 缩放 150% + 1280×720（再叠文字 150%、高对比度）：越界 / 截断探针 + 最小可点尺寸实测。
            float minHitPx = UiTuningValues.Get("ui.min_hit_px");
            var res720 = new[] { new Vector2Int(1280, 720) };
            foreach (string spec in NegativePanels)
            {
                string[] parts = spec.Split('|');
                float smallest = float.MaxValue;
                string smallestName = null;
                int controls = 0;
                string result = UiToolkitLayoutProbe.Probe(UiKit + parts[0], string.IsNullOrEmpty(parts[1]) ? null : parts[1], true, UiToolkitLayoutProbe.DefaultPanelSettingsPath,
                    null, 1.5f, res720, (root, r) =>
                    {
                        Rect panel = root.panel.visualTree.worldBound;
                        float toScreen = r.y / Mathf.Max(1f, panel.height);
                        root.Query<VisualElement>().ForEach(e =>
                        {
                            if (!(e is Button) && !(e is Toggle) && !(e is DropdownField) || e.resolvedStyle.display == DisplayStyle.None || e.worldBound.width < 1f)
                            {
                                return;
                            }
                            controls++;
                            float side = Mathf.Min(e.worldBound.width, e.worldBound.height) * toScreen;
                            if (side < smallest)
                            {
                                smallest = side;
                                smallestName = e.name;
                            }
                        });
                    });
                bool layoutOk = result.StartsWith("PASS", StringComparison.Ordinal);
                bool hitOk = controls > 0 && smallest >= minHitPx;
                Expect(layoutOk && hitOk, $"负向 UI 150% × 1280×720：{parts[0]} 布局{(layoutOk ? "无越界 / 截断" : "有问题")}；可点控件 {controls} 个，最小边 {(controls == 0 ? 0f : smallest):0.0}px（{smallestName}）≥ {minHitPx}px"
                    + (layoutOk ? string.Empty : "\n" + string.Join("\n", result.Split('\n').Take(6))));
            }
        }

        /// <summary>负向叠加：UI 150% + 文字 150% + 高对比度 × 1280×720 的布局探针（文字放大后在布局前写进字号）。</summary>
        private static void CheckTextScaleNegative()
        {
            var res720 = new[] { new Vector2Int(1280, 720) };
            foreach (string spec in NegativePanels)
            {
                string[] parts = spec.Split('|');
                float sample = 0f;
                string result = UiToolkitLayoutProbe.Probe(UiKit + parts[0], parts[1], true, UiToolkitLayoutProbe.DefaultPanelSettingsPath, root =>
                {
                    AccessibilityApplier.ResetForTests();
                    AccessibilityApplier.ApplyClasses(root.panel.visualTree, true, ColorblindMode.Achromat);
                    UiToolkitLayoutProbe.ForceLayout(root);
                    AccessibilityApplier.ApplyTextScale(root, 1.5f);
                }, 1.5f, res720, (root, r) => sample = root.Q<Label>()?.resolvedStyle.fontSize ?? 0f);
                bool ok = result.StartsWith("PASS", StringComparison.Ordinal);
                Expect(ok && sample > 0f, $"负向 UI 150% + 文字 150% + 高对比度 × 1280×720：{parts[0]} {(ok ? "无越界 / 截断" : "有问题")}（首个文字实测 {sample}px）"
                    + (ok ? string.Empty : "\n" + string.Join("\n", result.Split('\n').Take(8))));
            }
        }

        // ── F ────────────────────────────────────────────────────────────

        private sealed class FakeReader : IInputReader
        {
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public readonly HashSet<KeyCode> Down = new HashSet<KeyCode>();
            public Vector3 Mouse;
            public bool GetKey(KeyCode key) => Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => Down.Contains(key);
            public bool GetMouseButtonDown(int button) => Down.Contains(KeyCode.Mouse0 + button);
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
        }

        private static KeyCode Key(GameActionId a) => GameSettings.KeyBindings.GetChord(a).Key;

        private static void NextFrame(FakeReader r)
        {
            r.Down.Clear();
            InputRouter.ClearHoldLatches(framesOnly: true);
            InputRouter.DebugClearConsumedKeys();
        }

        private static void Tap(FakeReader r, KeyCode k)
        {
            NextFrame(r);
            r.Down.Add(k);
        }

        private static void CheckHoldToggle()
        {
            var r = new FakeReader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(r);
            KeyCode w = Key(GameActionId.MoveForward), s = Key(GameActionId.MoveBack), e = Key(GameActionId.Interact);

            GameSettings.SetHoldActionsToggle(false);
            r.Held.Add(w);
            bool holdMode = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            r.Held.Clear();
            NextFrame(r);
            bool releasedStops = !InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);

            GameSettings.SetHoldActionsToggle(true);
            InputRouter.ClearHoldLatches();
            Tap(r, w);
            bool latchOn = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            bool sameFrame = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            NextFrame(r);
            bool keeps = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            Tap(r, s);
            bool back = InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct);
            NextFrame(r);
            bool forwardCleared = !InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            Tap(r, s);
            bool stop = !InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct);
            Expect(holdMode && releasedStops && latchOn && sameFrame && keeps && back && forwardCleared && stop,
                $"直控移动 / 镜头平移切换：关 = 按住才走；开 = 按一下持续走、同帧多读不翻转、按反方向换向（前进锁存清掉）、再按一下停（{holdMode}/{releasedStops}/{latchOn}/{sameFrame}/{keeps}/{back}/{forwardCleared}/{stop}）");

            Tap(r, w);
            InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            InputRouter.SetScope(InputScope.Strategy);
            NextFrame(r);
            bool scopeCleared = !InputRouter.IsHoldLatched(GameActionId.MoveForward);
            InputRouter.SetScope(InputScope.Direct);
            Expect(scopeCleared, "切到战略视角时清掉直控的切换锁存（不会带着平移停不下来）");

            CheckInteractToggleBehavior(r, e);

            // 暂停 / 倍速：切换锁存只看按键帧，与游戏时钟无关（暂停中照样能开关，镜头平移按真实时间走）。
            GameClock.SetPaused(true);
            Tap(r, w);
            bool pausedToggle = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            GameClock.SetPaused(false);
            GameClock.SetSpeed(3f);
            NextFrame(r);
            bool fastKeeps = InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            GameClock.SetSpeed(0.5f);
            Tap(r, w);
            bool slowStop = !InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct);
            GameClock.SetSpeed(1f);
            Expect(pausedToggle && fastKeeps && slowStop, $"暂停 / 3x / 0.5x 下切换照常（{pausedToggle}/{fastKeeps}/{slowStop}）");
            GameSettings.SetHoldActionsToggle(false);
            InputRouter.ClearHoldLatches();
            InputRouter.DebugSetReader(null);
        }

        /// <summary>长按交互经真实 <see cref="RegionInteractionSystem"/> 跑：完成、走开、换目标、校验失败、门禁失败后锁存都松开，
        /// 走到下一个长按目标不按键就不会自己开始（FG 纪律第 4 条）。</summary>
        private static void CheckInteractToggleBehavior(FakeReader r, KeyCode e)
        {
            var cands = new List<RegionInteractCandidate>();
            RegionInteractFailure gate = RegionInteractFailure.None;
            var done = new List<string>();
            bool invalid = false;
            RegionInteractCandidate Make(string id) => new RegionInteractCandidate
            {
                Id = id, Position = new Vector2(1f, 0f), HoldSeconds = 1f, ActionVerb = "拆解",
                Validate = () => invalid ? RegionInteractResult.Fail(RegionInteractFailure.NotOwned, "不行") : RegionInteractResult.Ok(),
                Complete = () => { done.Add(id); return RegionInteractResult.Ok(); },
            };
            var marker = new HomeValleyMachineMarker(1, "chassis_test", null, 1);
            var sys = new RegionInteractionSystem();
            sys.Bind(new RegionInteractContext
            {
                GetPossessed = () => marker, GateCheck = () => gate, BuildCandidates = () => cands, GetFacing = () => Vector2.zero,
                Obstacles = new List<(Vector2, float)>(),
            });
            void Frame(float dt, bool tap = false)
            {
                if (tap)
                {
                    Tap(r, e);
                }
                else
                {
                    NextFrame(r);
                }
                sys.Tick(dt);
            }
            bool WaitsOnB(RegionInteractCandidate next)
            {
                cands.Clear();
                cands.Add(next);
                for (int i = 0; i < 4; i++)
                {
                    Frame(0.5f);
                }
                return sys.Progress01 == 0f && !done.Contains(next.Id) && !InputRouter.IsHoldLatched(GameActionId.Interact);
            }

            // ① 完成：按一下 E 开始，松手照样推进到满，完成后锁存松开，下一个目标不自动开始。
            cands.Add(Make("A"));
            Frame(0.4f, true);
            Frame(0.4f);
            float midway = sys.Progress01;
            Frame(0.4f);
            bool completed = done.Contains("A") && !InputRouter.IsHoldLatched(GameActionId.Interact);
            bool afterDone = WaitsOnB(Make("B"));
            // ② 走开：A 推进到一半走出范围，再走到 C 不自动开始。
            cands.Clear();
            cands.Add(Make("A2"));
            Frame(0.3f, true);
            cands.Clear();
            Frame(0.3f);
            bool walkAway = WaitsOnB(Make("C"));
            // ③ 换目标：A3 进行中直接换成 D（候选切换）。
            cands.Clear();
            cands.Add(Make("A3"));
            Frame(0.3f, true);
            bool swap = WaitsOnB(Make("D"));
            // ④ 校验失败：进行中目标变得不合法，恢复后不自动续上。
            cands.Clear();
            cands.Add(Make("A4"));
            Frame(0.3f, true);
            invalid = true;
            Frame(0.3f);
            invalid = false;
            bool invalidOk = WaitsOnB(Make("E"));
            // ⑤ 门禁失败（受控机失联）：恢复后不自动续上。
            cands.Clear();
            cands.Add(Make("A5"));
            Frame(0.3f, true);
            gate = RegionInteractFailure.MachineLostControl;
            Frame(0.3f);
            gate = RegionInteractFailure.None;
            bool gateOk = WaitsOnB(Make("F"));
            // ⑥ 切换中再按一下 = 取消（进度清零、不完成）。
            cands.Clear();
            cands.Add(Make("A6"));
            Frame(0.3f, true);
            Frame(0.3f, true);
            Frame(0.5f);
            bool cancel = sys.Progress01 == 0f && !done.Contains("A6");
            sys.Unbind();
            Expect(midway > 0.7f && completed && afterDone && walkAway && swap && invalidOk && gateOk && cancel && done.Count == 1,
                $"长按交互切换（真跑 RegionInteractionSystem）：按一下开始、松手照常推进到完成（{midway:0.00}/{completed}）；完成 / 走开 / 换目标 / 校验失败 / 失联后锁存松开，"
                + $"走到下一个长按目标不按键不会自己开始（{afterDone}/{walkAway}/{swap}/{invalidOk}/{gateOk}）；再按一下取消（{cancel}）；只完成了 A（{string.Join(",", done)}）");
        }

        // ── G ────────────────────────────────────────────────────────────

        private static void CheckKeyboardPointer()
        {
            var raw = new FakeReader { Mouse = new Vector3(400f, 300f, 0f) };
            var kp = new KeyboardPointerReader(raw) { ScreenSize = new Vector2Int(1280, 720) };
            GameSettings.SetKeyboardPointerEnabled(false);
            kp.Step(0.1f);
            raw.Held.Add(Key(GameActionId.PointerRight));
            kp.Step(0.1f);
            bool passthrough = kp.MousePosition == raw.Mouse && !kp.Active;
            raw.Held.Clear();

            GameSettings.SetKeyboardPointerEnabled(true);
            float speed = UiTuningValues.Get("access.kb_pointer_speed");
            kp.Step(0.1f);
            raw.Held.Add(Key(GameActionId.PointerRight));
            raw.Held.Add(Key(GameActionId.PointerUp));
            kp.Step(0.5f);
            raw.Held.Clear();
            Vector3 p = kp.MousePosition;
            float expect = speed * 0.5f / Mathf.Sqrt(2f);
            bool moved = kp.Active && Mathf.Abs(p.x - (400f + expect)) < 0.5f && Mathf.Abs(p.y - (300f + expect)) < 0.5f;

            raw.Held.Add(Key(GameActionId.PointerRight));
            kp.Step(100f);
            raw.Held.Clear();
            bool clamped = Mathf.Approximately(kp.MousePosition.x, 1279f);

            raw.Held.Add(Key(GameActionId.PointerClick));
            kp.Step(0.02f);
            bool down = kp.GetMouseButtonDown(0) && kp.GetKey(KeyCode.Mouse0);
            kp.Step(0.02f);
            bool heldNoRepeat = !kp.GetMouseButtonDown(0) && kp.GetKey(KeyCode.Mouse0);
            raw.Held.Clear();
            kp.Step(0.02f);
            bool up = kp.GetMouseButtonUp(0) && !kp.GetKey(KeyCode.Mouse0);
            raw.Held.Add(Key(GameActionId.PointerRightClick));
            kp.Step(0.02f);
            bool right = kp.GetMouseButtonDown(1);
            raw.Held.Clear();
            kp.Step(0.02f);

            raw.Mouse = new Vector3(100f, 100f, 0f); // 真鼠标动了
            kp.Step(0.02f);
            bool realTakesOver = kp.MousePosition == raw.Mouse && !kp.Active;
            Expect(passthrough && moved && clamped && down && heldNoRepeat && up && right && realTakesOver,
                $"键盘指针（世界侧）：关着直通；开着时小键盘移动 {speed}px/s（斜向归一，0.5s → {p}）、夹在屏幕内、5 = 左键按下 / 按住 / 松开（拖框）、0 = 右键、真鼠标一动即接管"
                + $"（{passthrough}/{moved}/{clamped}/{down}/{heldNoRepeat}/{up}/{right}/{realTakesOver}）");

            // 生产路径：InputRouter 默认读键盘指针装饰器；世界读到的指针位置 = 虚拟指针。
            InputRouter.Reset();
            Expect(ReferenceEquals(InputRouter.Reader, KeyboardPointerReader.Default), "InputRouter 默认后端是键盘指针装饰器（悬停读数、框选、放置走同一条鼠标路径）");
            kp.DebugWarp(new Vector2(640f, 360f));
            InputRouter.DebugSetReader(kp);
            bool routed = InputRouter.TryGetPointer(InputScope.Direct, out Vector3 sp) && Mathf.Abs(sp.x - 640f) < 0.01f;
            InputRouter.DebugSetReader(null);
            Expect(routed, $"InputRouter.TryGetPointer 读到虚拟指针（{sp}）");

            // 界面侧：在指针处真点到按钮（PointerDown / Up 派发到面板），这一下世界收不到左键。
            int clicks = 0;
            bool worldSawClick = true, markerShown = false, passthroughWorld = false;
            UiToolkitLayoutProbe.Probe(UiKit + "SettingsPanel.uxml", "SettingsRoot", false, UiToolkitLayoutProbe.DefaultPanelSettingsPath,
                root =>
                {
                    var b = new Button(() => clicks++) { name = "A11yClick", text = "点我" };
                    b.style.position = Position.Absolute;
                    b.style.left = 200f;
                    b.style.top = 200f;
                    b.style.width = 160f;
                    b.style.height = 60f;
                    root.panel.visualTree.Add(b);
                    var hudText = new Label("穿透") { name = "A11yPassthrough" };
                    hudText.AddToClassList(GameLogic.UI.Common.UiWindowFocus.PointerPassthroughClass);
                    hudText.style.position = Position.Absolute;
                    hudText.style.left = 600f;
                    hudText.style.top = 200f;
                    hudText.style.width = 160f;
                    hudText.style.height = 60f;
                    root.panel.visualTree.Add(hudText);
                }, 1f, new[] { new Vector2Int(1920, 1080) }, (root, r) =>
                {
                    VisualElement tree = root.panel.visualTree;
                    Button b = tree.Q<Button>("A11yClick");
                    Vector2 c = b.worldBound.center; // 面板坐标（左上原点）；1920×1080 下与屏幕像素 1:1
                    var raw2 = new FakeReader { Mouse = new Vector3(5f, 5f, 0f) };
                    var kp2 = new KeyboardPointerReader(raw2) { ScreenSize = r, UiRootOverride = tree };
                    kp2.Step(0.02f);
                    kp2.DebugWarp(new Vector2(c.x, r.y - c.y));
                    raw2.Held.Add(Key(GameActionId.PointerClick));
                    kp2.Step(0.02f);
                    worldSawClick = kp2.GetMouseButtonDown(0);
                    raw2.Held.Clear();
                    kp2.Step(0.02f);
                    markerShown = kp2.DebugMarker != null && kp2.DebugMarker.resolvedStyle.display != DisplayStyle.None;
                    // 压在穿透类 HUD 文字上：与真鼠标一样放行给世界（世界收到左键）。
                    Vector2 hc = tree.Q<Label>("A11yPassthrough").worldBound.center;
                    kp2.DebugWarp(new Vector2(hc.x, r.y - hc.y));
                    raw2.Held.Add(Key(GameActionId.PointerClick));
                    kp2.Step(0.02f);
                    passthroughWorld = kp2.GetMouseButtonDown(0);
                    raw2.Held.Clear();
                    kp2.Step(0.02f);
                    GameSettings.SetKeyboardPointerEnabled(false);
                    kp2.Step(0.02f);
                });
            Expect(clicks == 1 && !worldSawClick && markerShown && passthroughWorld,
                $"键盘指针（界面侧）：小键盘 5 在指针处点中按钮 {clicks} 次、世界没收到这一下（{!worldSawClick}）、面板上画出指针标记（{markerShown}）；压在穿透类 HUD 文字上世界照样收到左键（{passthroughWorld}）");
            GameSettings.SetKeyboardPointerEnabled(false);
        }

        // ── H ────────────────────────────────────────────────────────────

        private static void CheckMouseOnly()
        {
            var src = new StringBuilder();
            foreach (string f in Directory.GetFiles("Assets/GameScripts/HotFix/GameLogic", "*.cs", SearchOption.AllDirectories))
            {
                src.Append(File.ReadAllText(f));
            }
            string all = src.ToString();
            var hud = new HashSet<string>(Regex.Matches(all, "Shortcut = GameActionId\\.(\\w+)").Cast<Match>().Select(m => m.Groups[1].Value));

            // 主流程：暂停 / 调速、建造、研发、名册、规则、通知、统计、固件库、图鉴、物品、设置 / 改键、难度、存档退出、选择与命令（鼠标键）、镜头（边缘平移 / 拖动）。
            string[] hudActions = { "TogglePause", "OpenBuildMenu", "OpenResearch", "OpenRoster", "OpenRules", "ToggleNotificationCenter", "OpenDefense", "OpenPowerGrid", "Undo", "DemolishMode" };
            string[] missingHud = hudActions.Where(a => !hud.Contains(a)).ToArray();
            Expect(missingHud.Length == 0, missingHud.Length == 0
                ? $"HUD 上有鼠标按钮（Shortcut 绑定同一动作）：{string.Join("、", hudActions)}"
                : "主流程动作没有 HUD 鼠标按钮：" + string.Join("、", missingHud));

            var pause = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKit + "PauseMenu.uxml");
            VisualElement tree = pause != null ? pause.Instantiate() : null;
            string[] pauseButtons = { "PauseResume", "PauseSettings", "PauseKeyBindings", "PauseCodex", "PauseFirmware", "PauseStats", "PauseItems", "PauseRoster", "PauseDifficulty", "PauseSaveQuit" };
            string[] missingPause = pauseButtons.Where(n => tree?.Q<Button>(n) == null).ToArray();
            Expect(tree != null && missingPause.Length == 0, missingPause.Length == 0
                ? $"暂停菜单（Esc 或 HUD 暂停菜单按钮）有鼠标按钮：{string.Join("、", pauseButtons)}"
                : "暂停菜单缺按钮：" + string.Join("、", missingPause));

            bool speedButtons = Regex.IsMatch(File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/WorldBarHudUIToolkit.cs"), "SetSpeed\\(");
            bool mouseCommands = InputActionCatalog.TryGet(GameActionId.PrimaryAction, out InputActionDef p) && p.MouseOnly
                                 && InputActionCatalog.TryGet(GameActionId.SecondaryAction, out InputActionDef s) && s.MouseOnly;
            bool edgePan = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/View/CameraDirector.cs").Contains("EdgePan");
            Expect(speedButtons && mouseCommands && edgePan,
                $"调速有顶栏按钮（{speedButtons}）；选择 / 确认、命令 / 取消只绑鼠标键（左键选、右键移动 / 攻击，{mouseCommands}）；镜头可边缘平移（{edgePan}）");
            CheckMouseOnlyBehavior();
        }

        /// <summary>在面板坐标 <paramref name="at"/> 处派发一次真实的鼠标按下 / 松开（与真鼠标同一条 UI Toolkit 指针事件路径，Clickable 据此触发 clicked）。</summary>
        private static void MouseClick(VisualElement root, Vector2 at)
        {
            VisualElement picked = root.panel.Pick(at);
            foreach (EventType t in new[] { EventType.MouseDown, EventType.MouseUp })
            {
                var e = new Event { type = t, button = 0, clickCount = 1, mousePosition = at };
                using (EventBase evt = t == EventType.MouseDown ? PointerDownEvent.GetPooled(e) : PointerUpEvent.GetPooled(e))
                {
                    evt.target = picked;
                    picked?.SendEvent(evt);
                }
            }
        }

        private static void MouseClick(VisualElement root, string button) => MouseClick(root, root.Q<Button>(button).worldBound.center);

        /// <summary>FGT-UX-008 行为：读输入的后端只报鼠标键（键盘一个键都不按），真点顶栏 / 暂停菜单按钮看状态变化；直控用右键走、中键交互。</summary>
        private static void CheckMouseOnlyBehavior()
        {
            var mouse = new FakeReader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(mouse);
            GameObject barGo = null, pauseGo = null, rosterGo = null, camGo = null;
            try
            {
                VisualElement barRoot = FgProductionSelfCheck.MountUxml(UiKit + "WorldBar.uxml", out barGo);
                barGo.AddComponent<WorldBarHudUIToolkit>().BindView(barRoot);
                VisualElement rosterRoot = FgProductionSelfCheck.MountUxml(UiKit + "RosterPanel.uxml", out rosterGo);
                rosterGo.AddComponent<RosterPanelUIToolkit>().BindView(rosterRoot);
                VisualElement pauseRoot = FgProductionSelfCheck.MountUxml(UiKit + "PauseMenu.uxml", out pauseGo);
                PauseMenuUIToolkit pm = pauseGo.AddComponent<PauseMenuUIToolkit>();
                pm.BindView(pauseRoot);
                // 顶栏只在世界里显示（不在世界时带 uk-hidden）；这里当作已在世界里，摘掉再排一次版，按钮才有可点的位置。
                barRoot.Q<VisualElement>("WorldBar").RemoveFromClassList("uk-hidden");
                UiToolkitLayoutProbe.ForceLayout(barRoot);

                var speeds = new List<float>();
                for (int i = GameClock.Speeds.Length - 1; i >= 0; i--)
                {
                    MouseClick(barRoot, "WorldSpeed" + i);
                    speeds.Add(GameClock.Speed);
                }
                bool speedOk = speeds.SequenceEqual(GameClock.Speeds.Reverse());
                MouseClick(barRoot, "WorldLabor");
                bool rosterFromBar = RosterPanelUIToolkit.IsOpen;
                RosterPanelUIToolkit.Close();
                pm.SetOpen(true);
                UiToolkitLayoutProbe.ForceLayout(pauseRoot);
                MouseClick(pauseRoot, "PauseRoster");
                bool rosterFromPause = RosterPanelUIToolkit.IsOpen;
                RosterPanelUIToolkit.Close();
                UiToolkitLayoutProbe.ForceLayout(pauseRoot);
                MouseClick(pauseRoot, "PauseResume");
                bool resumed = !PauseMenuUIToolkit.IsOpen;
                Expect(speedOk && rosterFromBar && rosterFromPause && resumed,
                    $"只用鼠标真点：顶栏四档调速依次生效（{string.Join("/", speeds)}）、点劳动力打开名册（{rosterFromBar}）、暂停菜单点“名册”打开名册（{rosterFromPause}）、点“继续”关菜单（{resumed}）");
            }
            finally
            {
                GameClock.SetSpeed(1f);
                RosterPanelUIToolkit.Close();
                PauseMenuUIToolkit.Close();
                foreach (GameObject g in new[] { barGo, rosterGo, pauseGo })
                {
                    if (g != null)
                    {
                        UnityEngine.Object.DestroyImmediate(g);
                    }
                }
            }

            // 直控：按住右键朝指针下的地面走，离指针 access.direct_mouse_stop 米内停；“按住改切换”时点一下走、再点一下停。中键 = 交互键。
            try
            {
                camGo = new GameObject("__A11yMouseCam") { hideFlags = HideFlags.HideAndDontSave };
                Camera cam = camGo.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(new Vector3(0f, 30f, 0f), Quaternion.Euler(90f, 0f, 0f));
                InputRouter.SetScope(InputScope.Direct);
                var machine = new Vector3(0f, 1f, 0f);
                mouse.Mouse = cam.WorldToScreenPoint(new Vector3(8f, 0f, 0f));
                KeyCode right = KeyCode.Mouse0 + InputRouter.PhysicalButton(1);
                GameSettings.SetHoldActionsToggle(false);
                NextFrame(mouse);
                bool idle = InputRouter.DirectMouseMove(cam, machine) == Vector2.zero;
                mouse.Held.Add(right);
                Vector2 go = InputRouter.DirectMouseMove(cam, machine);
                mouse.Mouse = cam.WorldToScreenPoint(new Vector3(0.2f, 0f, 0f));
                bool stops = InputRouter.DirectMouseMove(cam, machine) == Vector2.zero;
                mouse.Held.Clear();
                mouse.Mouse = cam.WorldToScreenPoint(new Vector3(0f, 0f, -8f));
                GameSettings.SetHoldActionsToggle(true);
                InputRouter.ClearHoldLatches();
                Tap(mouse, right);
                Vector2 toggled = InputRouter.DirectMouseMove(cam, machine);
                NextFrame(mouse);
                bool keepsGoing = InputRouter.DirectMouseMove(cam, machine).y < -0.99f;
                Tap(mouse, right);
                bool toggledOff = InputRouter.DirectMouseMove(cam, machine) == Vector2.zero;
                GameSettings.SetHoldActionsToggle(false);
                Expect(idle && go.x > 0.99f && stops && toggled.y < -0.99f && keepsGoing && toggledOff,
                    $"直控只用鼠标走：不按不动、按住右键朝指针方向走（{go}）、走到指针附近停（{stops}）；切换模式点一下持续走（{toggled}/{keepsGoing}）、再点一下停（{toggledOff}）");

                // 中键交互：点击式目标点一下完成、长按目标按住中键推进到完成（与 E 同一条路径）。
                var done = new List<string>();
                var cands = new List<RegionInteractCandidate>
                {
                    new RegionInteractCandidate { Id = "tap", Position = new Vector2(1f, 0f), HoldSeconds = 0f, ActionVerb = "读取", Priority = 1,
                        Validate = () => RegionInteractResult.Ok(), Complete = () => { done.Add("tap"); return RegionInteractResult.Ok(); } },
                };
                var sys = new RegionInteractionSystem();
                var marker = new HomeValleyMachineMarker(1, "chassis_test", null, 1);
                sys.Bind(new RegionInteractContext { GetPossessed = () => marker, BuildCandidates = () => cands, GetFacing = () => Vector2.zero, Obstacles = new List<(Vector2, float)>() });
                mouse.Mouse = new Vector3(5f, 5f, 0f);
                Tap(mouse, InputRouter.InteractMouseKey);
                sys.Tick(0.1f);
                cands[0] = new RegionInteractCandidate { Id = "hold", Position = new Vector2(1f, 0f), HoldSeconds = 0.5f, ActionVerb = "拆解",
                    Validate = () => RegionInteractResult.Ok(), Complete = () => { done.Add("hold"); return RegionInteractResult.Ok(); } };
                mouse.Held.Add(InputRouter.InteractMouseKey);
                for (int i = 0; i < 4; i++)
                {
                    NextFrame(mouse);
                    sys.Tick(0.2f);
                }
                mouse.Held.Clear();
                sys.Unbind();
                Expect(done.SequenceEqual(new[] { "tap", "hold" }), $"直控只用鼠标交互：中键点一下完成点击式目标、按住中键完成长按目标（{string.Join(",", done)}）");
            }
            finally
            {
                if (camGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(camGo);
                }
                GameSettings.SetHoldActionsToggle(false);
                InputRouter.ClearHoldLatches();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        // ── I ────────────────────────────────────────────────────────────

        private static void CheckRegisterItems()
        {
            // DEBT-FG4ECO08-04：顶栏固定物品排序（随存档往返）。
            var state = new CampaignState();
            ProductionStatsState st = ProductionStats.Ensure(state);
            st.Pins = new[] { new PinnedItemRecord { ItemId = "a" }, new PinnedItemRecord { ItemId = "b" }, new PinnedItemRecord { ItemId = "c" } };
            int rev = ResourcePins.Revision;
            bool moved = ResourcePins.TryMove(state, "c", -1);
            bool edge = !ResourcePins.TryMove(state, "a", -1);
            string order = string.Join("", ResourcePins.Pins(state).Select(x => x.ItemId));
            var copy = JsonUtility.FromJson<ProductionStatsState>(JsonUtility.ToJson(st));
            string reloaded = string.Join("", copy.Pins.Select(x => x.ItemId));
            Expect(moved && edge && order == "acb" && reloaded == "acb" && ResourcePins.Revision > rev,
                $"顶栏固定物品：中键前移一位（abc → {order}）、到头不动、顺序随存档往返（{reloaded}）、顶栏随修订号重画");
            Expect(File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/WorldBarHudUIToolkit.cs").Contains("ResourcePins.TryMove(CampaignSession.Current, id, -1)"),
                "生产路径：顶栏固定物品的中键调用 ResourcePins.TryMove");

            // DEBT-FG5RND01-06 / DEBT-FG6DEF09-05：研发树键盘平移 / 缩放、难度快捷键（都可重绑）。
            GameActionId[] keys = { GameActionId.PanelPanUp, GameActionId.PanelPanDown, GameActionId.PanelPanLeft, GameActionId.PanelPanRight, GameActionId.PanelZoomIn, GameActionId.PanelZoomOut, GameActionId.OpenDifficulty };
            bool bound = keys.All(k => InputActionCatalog.TryGet(k, out _) && GameSettings.KeyBindings.GetChord(k).IsBound);
            string pump = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/UiKitInputPump.cs");
            string tree = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/ResearchTreePanelUIToolkit.cs");
            Expect(bound && pump.Contains("GameActionId.OpenDifficulty") && pump.Contains("DifficultyPanelUIToolkit.Open();") && tree.Contains("KeyboardViewInput(Time.unscaledDeltaTime)"),
                $"研发树方向键平移 / +- 缩放、难度面板 Ctrl+K：动作在表里且有默认键（{bound}），生产路径接线");
            CheckResearchKeys();

            // DEBT-FG8EXP01-01：名册里写队名与颜色名。
            string fmt = GameText.Get("exp.roster.place_team");
            Expect(fmt.Contains("{0}") && fmt.Contains("{1}") && fmt.Contains("{2}") && File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Campaign/MachineRoster.cs").Contains("exp.roster.place_team"),
                $"名册远征队成员的去向写“队名（颜色名）· 状态”（{fmt}）");

            // FG-GAP-102：Demo 数值合成台改名，与电路合成台不同名。
            string lens = GameText.Get("craft.lens.entry"), circuit = GameText.Get("building.circuit_synth.name");
            Expect(!string.IsNullOrEmpty(lens) && lens != "craft.lens.entry" && lens != circuit && !lens.Contains("合成台"), $"Demo 数值合成台改名“{lens}”，与“{circuit}”区分");

            // DEBT-EMG2MAT01-07 / DEBT-FG1HUD01-05：音效钩子与独立占位音。
            FeedbackCueDef sp = FeedbackCueCatalog.Get(FeedbackCueId.MatterSpawn), en = FeedbackCueCatalog.Get(FeedbackCueId.MatterEnter), lv = FeedbackCueCatalog.Get(FeedbackCueId.UplinkLeave);
            bool cues = sp != null && en != null && lv != null
                        && File.Exists($"Assets/GameRes/Raw/Audios/Sfx/{sp.SfxId}.wav") && File.Exists($"Assets/GameRes/Raw/Audios/Sfx/{en.SfxId}.wav")
                        && File.Exists($"Assets/GameRes/Raw/Audios/Sfx/{lv.SfxId}.wav") && lv.SfxId != "sfx_signal_restore";
            Expect(cues, $"斑块生成 / 踏进斑块音效钩子有定义与占位音文件；信号离开机器换成独立的降调音（{cues}）");
            CheckMatterCuesBehavior();
        }

        /// <summary>DEBT-EMG2MAT01-07 行为：真内核里生成斑块、单位踏进去 → 被观察的地点各出一声；不被观察（后台）照常结算、不出声，计数跟上（之后切回观察不补响）。</summary>
        private static void CheckMatterCuesBehavior()
        {
            const float dt = 1f / 60f;
            var site = new CombatSite("ux02_matter_cue", CombatSite.ConfigFromTuning());
            try
            {
                BinGames.Sim.Combat.CombatKernel k = site.Kernel;
                site.Step(dt, k.Time + dt); // 正式路径：不被观察（WorldView.IsObserved 为假），已见计数从当前值起
                (int Born, int Enters) seen0 = site.DebugMatterCueSeen;
                int Run(double x, bool observed, out int dSpawn, out int dEnter)
                {
                    int sp = FeedbackCues.CountOf(FeedbackCueId.MatterSpawn), en = FeedbackCues.CountOf(FeedbackCueId.MatterEnter);
                    k.Spawn(new BinGames.Sim.Combat.CombatSpawn
                    {
                        Kind = BinGames.Sim.Combat.CombatUnitKind.Enemy, Faction = BinGames.Sim.Combat.CombatFaction.Hostile, Behavior = BinGames.Sim.Combat.CombatBehavior.HoldFire,
                        Flags = BinGames.Sim.Combat.CombatUnitFlags.Alive | BinGames.Sim.Combat.CombatUnitFlags.Targetable,
                        Position = new double2(x, 0), Home = new double2(x, 0), Radius = 0.4f, Health = 1000f, MaxHealth = 1000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                    });
                    k.SpawnPatch(BinGames.Sim.Combat.EmgMatterConst.Oil, new double2(x, 0), 2f, 3f, 30f);
                    for (int i = 0; i < 60; i++)
                    {
                        k.Step(dt, k.Time + dt); // 只推内核（不经 ProcessEvents），钩子由下面一次调用结算增量
                    }
                    site.DebugRaiseMatterCues(observed);
                    dSpawn = FeedbackCues.CountOf(FeedbackCueId.MatterSpawn) - sp;
                    dEnter = FeedbackCues.CountOf(FeedbackCueId.MatterEnter) - en;
                    return k.PatchStats.MatterEnters;
                }
                int entersA = Run(0, true, out int spA, out int enA);
                int entersB = Run(20, false, out int spB, out int enB);
                (int Born, int Enters) seenB = site.DebugMatterCueSeen;
                site.DebugRaiseMatterCues(true); // 切回观察：后台发生过的不补响
                int spC = FeedbackCues.CountOf(FeedbackCueId.MatterSpawn), enC = FeedbackCues.CountOf(FeedbackCueId.MatterEnter);
                site.DebugRaiseMatterCues(true);
                bool noReplay = FeedbackCues.CountOf(FeedbackCueId.MatterSpawn) == spC && FeedbackCues.CountOf(FeedbackCueId.MatterEnter) == enC;
                Expect(seen0.Born >= 0 && entersA > seen0.Enters && spA == 1 && enA == 1,
                    $"被观察的地点：生成斑块 → 斑块音 {spA} 声、单位踏进 → 踏入音 {enA} 声（内核踏入计数 {seen0.Enters} → {entersA}）");
                Expect(entersB > entersA && spB == 0 && enB == 0 && seenB.Born == k.PatchStats.PatchesBorn && seenB.Enters == entersB && noReplay,
                    $"后台地点（不被观察）照常生成斑块、踏入计数 {entersA} → {entersB}，但不出声（{spB} / {enB}）；已见计数跟上（{seenB}），切回观察不补响（{noReplay}）");
            }
            finally
            {
                site.Dispose();
            }
        }

        private static void CheckResearchKeys()
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKit + "ResearchTreePanel.uxml", out GameObject go);
            var r = new FakeReader();
            try
            {
                ResearchTreePanelUIToolkit panel = go.AddComponent<ResearchTreePanelUIToolkit>();
                panel.BindView(root);
                InputRouter.Reset();
                InputRouter.DebugSetReader(r);
                InputRouter.SetModalUi(true); // 面板开着 = 界面上下文
                float z0 = panel.Zoom;
                Tap(r, Key(GameActionId.PanelZoomIn));
                panel.KeyboardViewInput(0f);
                float zIn = panel.Zoom;
                Tap(r, Key(GameActionId.PanelZoomOut));
                panel.KeyboardViewInput(0f);
                float zOut = panel.Zoom;
                NextFrame(r);
                Vector2 p0 = panel.PanOffset;
                r.Held.Add(Key(GameActionId.PanelPanRight));
                panel.KeyboardViewInput(0.1f);
                r.Held.Clear();
                Vector2 p1 = panel.PanOffset;
                r.Held.Add(Key(GameActionId.PanelPanUp));
                panel.KeyboardViewInput(0.1f);
                r.Held.Clear();
                Vector2 p2 = panel.PanOffset;
                InputRouter.SetTextInputFocused(true);
                r.Held.Add(Key(GameActionId.PanelPanRight));
                panel.KeyboardViewInput(0.1f);
                r.Held.Clear();
                bool textBlocks = panel.PanOffset == p2;
                InputRouter.SetTextInputFocused(false);
                Expect(zIn > z0 && Mathf.Approximately(zOut, z0) && p1.x <= p0.x && p2.y >= p1.y && (p1 != p0 || p2 != p1) && textBlocks,
                    $"研发树键盘：+ 放大 {z0}→{zIn}、- 缩小回 {zOut}；→ 平移 {p0}→{p1}、↑ 平移 → {p2}（夹在画布范围内）；文本框有焦点时方向键不平移（{textBlocks}）");
            }
            finally
            {
                InputRouter.SetModalUi(false);
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── 工具 ──────────────────────────────────────────────────────────

        private static void Section(string name, Action body)
        {
            Line("  · " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail($"{name} 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
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
