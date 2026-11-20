// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>PL-RP-01 自检段（PresentationValidate.Segments 末尾两行）。两段在 PL 影子 2（Built-in）与 URP 试验影子里都能跑，断言按当前管线分支。</summary>
    public static class RpTrialValidate
    {
        public const string FgRef = "fg/projecta/0.2-fullgame";

        private static void Expect(bool ok, string what) => PresentationValidate.Expect(ok, what);

        /// <summary>隔离：工作树 ProjectSettings 与 FG 已提交版本一致、工作树里没有 Assets/_PLRP、GameRes 无改动；当前工程的管线与色彩空间和它是哪份影子对得上；垫片开 / 关 sim 哈希一致。</summary>
        public static void RpTrialIsolation()
        {
            string root = PresentationCapture.RepoRoot();
            string tengine = Path.Combine(root, "TEngine");
            // 合流后的 FG 检出没有这个本地 ref：降级为只断言“无未提交改动”（下一条）
            bool hasRef = Git(tengine, $"rev-parse --verify --quiet {FgRef}", out string _) == 0;
            string diffOut = $"（没有 {FgRef}，跳过）";
            int diff = hasRef ? Git(tengine, $"diff --quiet {FgRef} -- UnityProject/ProjectSettings", out diffOut) : 0;
            Expect(diff == 0, $"工作树 TEngine/UnityProject/ProjectSettings 与 {FgRef} 一致（git diff --quiet 退出码 {diff}{(diff == 0 ? string.Empty : "：" + diffOut.Trim())}）");
            int st = Git(tengine, "status --porcelain -- UnityProject/ProjectSettings", out string stOut);
            Expect(st == 0 && stOut.Trim().Length == 0, $"工作树 ProjectSettings 没有未提交改动（{(stOut.Trim().Length == 0 ? "干净" : stOut.Trim())}）");
            string plrp = Path.Combine(tengine, "UnityProject", "Assets", "_PLRP");
            Expect(!Directory.Exists(plrp), $"工作树里没有 Assets/_PLRP（{plrp}）");
            int gr = Git(Path.Combine(tengine, "UnityProject", "Assets", "GameRes"), "status --porcelain", out string grOut);
            Expect(gr == 0 && grOut.Trim().Length == 0, $"GameRes（只读）没有改动（垫片换 shader 不写回磁盘材质）：{(grOut.Trim().Length == 0 ? "干净" : grOut.Trim().Split('\n').Length + " 项")}");

            bool shadow = RpTrialSetup.InTrialShadow;
            if (shadow)
            {
                Expect(!PresentationMaterials.IsBuiltIn && QualitySettings.activeColorSpace == ColorSpace.Linear,
                    $"URP 试验影子：管线 {GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"}、色彩空间 {QualitySettings.activeColorSpace}（应为 PLRP_URP / Linear）");
            }
            else
            {
                Expect(PresentationMaterials.IsBuiltIn && QualitySettings.activeColorSpace == ColorSpace.Gamma,
                    $"非试验影子：管线 {(PresentationMaterials.IsBuiltIn ? "Built-in" : GraphicsSettings.currentRenderPipeline.name)}、色彩空间 {QualitySettings.activeColorSpace}（应为 Built-in / Gamma，同 FG）");
            }
            PresentationFeature f = PresentationFeatureRegistry.Features.FirstOrDefault(x => x.Id == PresentationFeatureRegistry.RpTrialId);
            Expect(f != null && !f.DefaultPL && !f.DefaultMain, "pl.rptrial 已登记，PL 分支 / 合主线默认都关");
            Expect(PresentationSimHash.CheckOnOff(out string d, new RpTrialShim()), "垫片开 / 关 sim 哈希一致：" + d);
        }

        /// <summary>垫片：Built-in 下什么都不改；URP 下 Standard → URP Lit（颜色照抄、透明模式进透明队列）、停用后换回；URP 能不能画的判定对得上已知 shader。</summary>
        public static void ShimSegment()
        {
            Shader standard = Shader.Find("Standard");
            var color = new Color(0.2f, 0.4f, 0.6f, 1f);
            var opaque = new Material(standard) { color = color, name = "pl-rp opaque", hideFlags = HideFlags.DontSave };
            var clear = new Material(standard) { color = color, name = "pl-rp transparent", hideFlags = HideFlags.DontSave };
            clear.SetFloat("_Mode", 3f); // Standard 预乘透明：One / OneMinusSrcAlpha、不写深、透明队列、_ALPHAPREMULTIPLY_ON
            clear.SetFloat("_SrcBlend", (float)BlendMode.One);
            clear.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            clear.SetFloat("_ZWrite", 0f);
            clear.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            clear.renderQueue = 3000;
            var cutout = new Material(standard) { color = color, name = "pl-rp cutout", hideFlags = HideFlags.DontSave };
            cutout.SetFloat("_Mode", 1f);
            cutout.EnableKeyword("_ALPHATEST_ON");
            cutout.renderQueue = 2450;
            string Sig(Material m) => $"{m.shader.name}|src {m.GetFloat("_SrcBlend")}|dst {m.GetFloat("_DstBlend")}|zw {m.GetFloat("_ZWrite")}|q {m.renderQueue}|kw {string.Join(",", m.shaderKeywords.OrderBy(k => k))}";
            string clearBefore = Sig(clear), cutoutBefore = Sig(cutout), opaqueBefore = Sig(opaque);
            var shim = new RpTrialShim();
            try
            {
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.RpTrialId, true);
                PresentationHost.Register(shim);
                PresentationHost.Tick(PresentationFrame.Synthetic(0.05f, 0.05));
                Expect(PresentationHost.IsActive(shim), "垫片登记后启用");
                if (PresentationMaterials.IsBuiltIn)
                {
                    Expect(opaque.shader == standard && shim.Remapped == 0, $"Built-in：不换任何材质（换了 {shim.Remapped} 个）");
                    Expect(RpTrialShim.UrpVerdict(standard) == "ok", "Built-in：品红判定不判（恒 ok）");
                }
                else
                {
                    string lit = PresentationMaterials.ShaderName(PresentationMaterialUse.Lit);
                    Expect(opaque.shader.name == lit && opaque.GetColor("_BaseColor") == color, $"URP：Standard → {opaque.shader.name}，_BaseColor {opaque.GetColor("_BaseColor")}（应 {lit} / {color}）");
                    Expect(clear.renderQueue >= (int)RenderQueue.Transparent && clear.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), $"URP：透明模式进透明队列（{clear.renderQueue}）");
                    Expect(cutout.IsKeywordEnabled("_ALPHATEST_ON") && cutout.GetFloat("_AlphaClip") == 1f, "URP：Standard 镂空（_Mode 1）→ URP Lit 开 Alpha Clip");
                    string vs = RpTrialShim.UrpVerdict(standard), vl = RpTrialShim.UrpVerdict(opaque.shader), vu = RpTrialShim.UrpVerdict(Shader.Find("BinGames/SimInstancedUnlit"));
                    Expect(vs == "error" && vl == "ok" && vu == "ok",
                        $"URP 能不能画：Standard = {vs}（应 error）、URP Lit = {vl}（应 ok）、无 LightMode 的顶点片元 shader SimInstancedUnlit = {vu}（应 ok）；Standard 各 pass 的 LightMode：{RpTrialShim.PassTags(standard)}");
                    Expect(RpTrialShim.StandIns.Values.All(n => AssetDatabase.LoadAssetAtPath<Shader>($"{RpTrialSetup.ShaderFolder}/{n}.shader") is Shader s && s.isSupported),
                        "3 个地形替身 shader 已导入且受支持：" + string.Join(", ", RpTrialShim.StandInNames()));
                }
                PresentationHost.Unregister(shim);
                Expect(opaque.shader == standard && clear.shader == standard && shim.Remapped == 0, "停用后全部换回 Standard");
                Expect(Sig(clear) == clearBefore && Sig(cutout) == cutoutBefore && Sig(opaque) == opaqueBefore,
                    $"停用后混合 / 写深 / 队列 / 关键字原样还原：透明 {Sig(clear)}（换前 {clearBefore}）；镂空 {Sig(cutout)}（换前 {cutoutBefore}）");
                MethodInfo addB = RpTrialCapture.PerfPrivate("AddBuildings"), spawnM = RpTrialCapture.PerfPrivate("SpawnMachines");
                Type[] want = { typeof(GameLogic.Campaign.CampaignState), typeof(GameLogic.Campaign.Grid.GridCell), typeof(int) };
                Expect(new[] { addB, spawnM }.All(mi => mi.ReturnType == typeof(int) && mi.GetParameters().Select(p => p.ParameterType).SequenceEqual(want)),
                    "RpFactory 反射调的 FgPerfBaseline.AddBuildings / SpawnMachines 在、签名仍是 (CampaignState, GridCell, int) → int（FG 改名或改签名时在这里先红）");
                int before = Camera.onPreCull?.GetInvocationList().Length ?? 0;
                RpTrialCapture.SetCombatDrawHook(true);
                RpTrialCapture.SetCombatDrawHook(true);
                int on = Camera.onPreCull?.GetInvocationList().Length ?? 0;
                RpTrialCapture.SetCombatDrawHook(false);
                int off = Camera.onPreCull?.GetInvocationList().Length ?? 0;
                Expect(on == before + 1 && off == before && !RpTrialCapture.CombatHookOn && PresentationCapture.BeforeRender == null, $"夜袭战斗补提交钩子开两次只挂一份、关后摘净（含采集管线的 BeforeRender；前 {before} / 开 {on} / 关 {off}）");
            }
            finally
            {
                PresentationHost.Unregister(shim);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                Object.DestroyImmediate(opaque);
                Object.DestroyImmediate(clear);
                Object.DestroyImmediate(cutout);
            }
        }

        private static int Git(string dir, string args, out string output)
        {
            var psi = new ProcessStartInfo("git", $"-C \"{dir}\" {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception e)
            {
                output = "起不来 git：" + e.Message;
                return -1;
            }
        }
    }
}
