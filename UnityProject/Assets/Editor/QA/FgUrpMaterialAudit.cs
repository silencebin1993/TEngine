using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-PL-01（MERGE-PLAN §5.3）：切 URP 后 Built-in Standard 材质会画成品红。
    /// <see cref="ConvertGameRes"/> 用 URP 材质转换器（Render Pipeline Converter 的 Material Upgrade）同一套升级器把 GameRes 的 Standard 材质一次转成 URP Lit，前后列清单；
    /// <see cref="Run"/> 是全量自检段：工程里不许再有 URP 画不了的材质（Built-in Standard / Legacy / 丢 shader），转换过的材质颜色与贴图照抄，负向用临时材质证明审计会报。
    /// </summary>
    public static class FgUrpMaterialAudit
    {
        public const string UrpLit = "Universal Render Pipeline/Lit";
        private const string TempFolder = "Assets/_FgUrpAuditTmp";

        /// <summary>不审的目录：TextMesh Pro 官方示例（不进包、不被游戏引用）。点开头的目录（.ThirdParty）Unity 本来就不导入。</summary>
        private static readonly string[] Excluded = { "Assets/TextMesh Pro/Examples & Extras/" };

        private static StringBuilder _report;
        private static int _fail;

        /// <summary>材质在 URP 下的问题；null = 能画。</summary>
        public static string Problem(Material m)
        {
            if (m == null)
            {
                return "读不出材质";
            }
            if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader")
            {
                return "shader 丢失";
            }
            return RpTrialShim.IsStandardFamily(m.shader.name) ? $"Built-in 着色器 {m.shader.name}" : null;
        }

        /// <summary>某目录下 URP 画不了的材质（路径 → 原因），按路径排序。</summary>
        public static List<(string Path, string Why)> Offenders(string folder)
        {
            return AssetDatabase.FindAssets("t:Material", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) && !Excluded.Any(p.StartsWith))
                .Distinct()
                .Select(p => (p, Problem(AssetDatabase.LoadAssetAtPath<Material>(p))))
                .Where(x => x.Item2 != null)
                .OrderBy(x => x.p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 一次性转换入口（batchmode：bash tools/unity-validate.sh GameLogic.EditorTools.FgUrpMaterialAudit.ConvertGameRes）。
        /// 清单写到环境变量 BINGAMES_URP_CONVERT_OUT 指的文件（不设只进日志）。转换后 GameRes 里仍有画不了的材质 → 退出码 1。
        /// </summary>
        public static void ConvertGameRes()
        {
            var sb = new StringBuilder("[FG15-PL-01] GameRes Standard 材质 → URP Lit（URP Material Upgrade 升级器）\n");
            List<(string Path, string Why)> before = Offenders("Assets/GameRes");
            sb.AppendLine($"转换前：{before.Count} 份");
            List<MaterialUpgrader> upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));
            foreach ((string path, string why) in before)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                string message = string.Empty;
                bool ok = m != null && MaterialUpgrader.Upgrade(m, upgraders, MaterialUpgrader.UpgradeFlags.LogMessageWhenNoUpgraderFound, ref message);
                if (ok)
                {
                    EditorUtility.SetDirty(m);
                }
                sb.AppendLine($"  {(ok ? "✓" : "✗")} {path}（{why} → {(m != null ? m.shader.name : "?")}）{message}");
            }
            AssetDatabase.SaveAssets();
            List<(string Path, string Why)> after = Offenders("Assets/GameRes");
            sb.AppendLine($"转换后仍画不了：{after.Count} 份");
            foreach ((string path, string why) in after)
            {
                sb.AppendLine($"  ✗ {path}（{why}）");
            }
            sb.AppendLine(after.Count == 0 ? "结论：PASS" : "结论：FAIL");
            string outPath = Environment.GetEnvironmentVariable("BINGAMES_URP_CONVERT_OUT");
            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath, sb.ToString());
            }
            Debug.Log(sb.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(after.Count == 0 ? 0 : 1);
            }
        }

        [MenuItem("QA/自检/渲染/URP 材质审计")]
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
            Line("\n[URP 材质] 工程里没有 URP 画不了的材质（FG15-PL-01，MERGE-PLAN §5.3）");
            try
            {
                CheckProject();
                CheckConvertedCopiesValues();
                CheckNegative();
            }
            catch (Exception e)
            {
                Fail($"URP 材质审计抛异常：{e}");
            }
            finally
            {
                DeleteTemp();
            }
            return _fail;
        }

        private static void CheckProject()
        {
            List<(string Path, string Why)> bad = Offenders("Assets");
            Expect(bad.Count == 0, $"Assets 下（除 TMP 官方示例）URP 画不了的材质 = 0（实际 {bad.Count}{(bad.Count > 0 ? "：" + string.Join("；", bad.Take(8).Select(b => $"{b.Path}（{b.Why}）")) : string.Empty)}）");
        }

        /// <summary>转换器换 shader 后旧属性仍保存在材质里（URP Lit 也声明了隐藏的 _Color / _MainTex）：照抄了才算转对。</summary>
        private static void CheckConvertedCopiesValues()
        {
            int lit = 0, wrong = 0;
            var samples = new List<string>();
            foreach (string path in AssetDatabase.FindAssets("t:Material", new[] { "Assets/GameRes/Raw/Actor", "Assets/GameRes/Raw/Effects" }).Select(AssetDatabase.GUIDToAssetPath).Distinct())
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null || m.shader == null || m.shader.name != UrpLit)
                {
                    continue;
                }
                lit++;
                Texture oldTex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                bool texOk = oldTex == null || m.GetTexture("_BaseMap") == oldTex;
                bool colorOk = !m.HasProperty("_Color") || Approx(m.GetColor("_Color"), m.GetColor("_BaseColor"));
                if (!texOk || !colorOk)
                {
                    wrong++;
                    samples.Add(path);
                }
            }
            Expect(lit >= 39, $"GameRes 角色 / 弹体材质已是 URP Lit ≥ 39 份（实际 {lit}）");
            Expect(wrong == 0, $"转换后 _BaseMap = 原 _MainTex、_BaseColor = 原 _Color（不一致 {wrong}{(wrong > 0 ? "：" + string.Join("；", samples.Take(5)) : string.Empty)}）");
        }

        /// <summary>负向：故意留一份没转的 Standard 材质（临时资产，用完删除），审计必须报出它；转换器转过之后不再报。</summary>
        private static void CheckNegative()
        {
            Shader standard = Shader.Find("Standard");
            Expect(standard != null && Problem(new Material(standard)) != null, "负向：内存里的 Standard 材质判为 URP 画不了");
            Expect(Problem(null) != null, "负向：读不出的材质判为有问题");
            Shader urpLit = Shader.Find(UrpLit);
            Expect(urpLit != null && Problem(new Material(urpLit)) == null, "对照：URP Lit 材质判为能画");

            DeleteTemp();
            AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            string path = TempFolder + "/LeftStandard.mat";
            var left = new Material(standard) { color = new Color(0.8f, 0.2f, 0.1f) };
            AssetDatabase.CreateAsset(left, path);
            AssetDatabase.SaveAssets();
            List<(string Path, string Why)> found = Offenders(TempFolder);
            Expect(found.Count == 1 && found[0].Path == path, $"负向：磁盘上留一份未转换的 Standard 材质，审计报出它（报出 {found.Count} 份）");
            Expect(Offenders("Assets").Any(b => b.Path == path), "负向：全工程扫描同样报出这份");

            string message = string.Empty;
            bool upgraded = MaterialUpgrader.Upgrade(left, MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset)), MaterialUpgrader.UpgradeFlags.None, ref message);
            AssetDatabase.SaveAssets();
            Expect(upgraded && left.shader.name == UrpLit && Approx(left.GetColor("_BaseColor"), new Color(0.8f, 0.2f, 0.1f)), $"转换器把它转成 URP Lit 且颜色照抄（{left.shader.name}）");
            Expect(Offenders(TempFolder).Count == 0, "转换后审计不再报");
        }

        private static void DeleteTemp()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        private static bool Approx(Color a, Color b) => Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;

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

        private static void Line(string text) => _report.AppendLine(text);
    }
}
