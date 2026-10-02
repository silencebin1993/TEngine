using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG-TOOL-01：旅程断点。完整旅程（从主菜单出发）跑到旅程登记的关键步骤（<see cref="JourneyDef.CheckpointAfter"/>）时自动写一份断点：
    /// 正式存档入口（<see cref="CampaignAutoSaveService.SaveWithExport"/>）写出的存档 + 旅程临时目录里的其他文件（例如布局库）+ 旅程变量 + 指纹。
    /// 调试时 <c>bash tools/unity-journey.sh FGJ-M3 --from 某步</c> 从断点续跑：进 Play → 主菜单“读取”→ 点断点存档的槽位（正式读档路径）→
    /// 恢复旅程变量与倍速 → 接着跑该步之后的步骤。**断点只用于迭代；交付验收仍必须从主菜单完整跑一遍**（续跑的报告写到另一个文件，结论里写明“断点续跑”）。
    ///
    /// 拒绝使用（提示重新完整跑）：没有断点；旅程版本号不同；该步及之前的步骤（编号、标题、超时、重试次数、种子）变了；
    /// 存档格式 / 内容版本 / 世界生成器版本不同；游戏代码与配置表指纹不同（Assets/GameScripts 的源码 + GameRes/Raw/Configs）。
    /// 只有显式加 <c>--allow-code-change</c> 时代码指纹不同才放行，报告里明写“断点来自旧代码，结论只供迭代参考”。
    /// 续跑不写新断点（断点只来自完整跑）。
    /// </summary>
    public static class JourneyCheckpoints
    {
        public const int FormatVersion = 1;
        public const string MetaFile = "checkpoint.json";
        public const string SavesDir = "saves";

        /// <summary>旅程变量里不随断点恢复的键：本次运行自己的临时存档目录。</summary>
        public static readonly string[] NotRestoredVars = { "saves" };

        /// <summary>
        /// FG4-E2E-01：步骤作用域的键（<see cref="FgjM3Common.SK"/>：s&lt;步序&gt;.&lt;第几次尝试&gt;.&lt;名字&gt;）只属于写断点前已经做完的步骤。
        /// 续跑的旅程步序与完整跑不同（前面换成了放回断点的几步），恢复它们会让同一序号的新步骤误以为“已经点过”、一直等到超时重试，不恢复。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex StepScopedVar = new System.Text.RegularExpressions.Regex(@"^s\d+\.\d+\.");

        public static bool IsRestoredVar(string key) => !string.IsNullOrEmpty(key) && !NotRestoredVars.Contains(key) && !StepScopedVar.IsMatch(key);

        // ── 元数据 ─────────────────────────────────────────────────────────────

        [Serializable]
        public sealed class Var
        {
            public string k;
            public string v;
        }

        [Serializable]
        public sealed class Meta
        {
            public int format = FormatVersion;
            public string journey;
            public int journeyVersion;
            public string stepId;
            public int stepIndex;
            public string stepsHash;
            public int seed;
            public string codeHash;
            public int codeFiles;
            public int saveSchema;
            public int contentVersion;
            public int generatorVersion;
            public int slot;
            public float speed;
            public bool paused;
            public long gameTicks;
            public string writtenLocal;
            public string unity;
            public List<Var> vars = new List<Var>();
        }

        // ── 路径 ───────────────────────────────────────────────────────────────

        /// <summary>断点根目录：环境变量 BINGAMES_JOURNEY_CHECKPOINT_DIR；缺省为仓库根下的 .journey-checkpoints（gitignore，只在本机）。</summary>
        public static string RootDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_CHECKPOINT_DIR");
                if (!string.IsNullOrEmpty(env))
                {
                    return env;
                }
                string repo = RepoRoot();
                return repo != null ? Path.Combine(repo, ".journey-checkpoints") : Path.Combine(Path.GetTempPath(), "bingames-journey-checkpoints");
            }
        }

        public static string Dir(string root, string journeyId, string stepId) => Path.Combine(root, journeyId, stepId);

        /// <summary>仓库根（有 tools/cell_tables/gen_all.py 的目录），从工程目录往上找（真工程与两个影子工程都在仓库根下）。</summary>
        public static string RepoRoot()
        {
            string dir = Path.GetDirectoryName(Application.dataPath);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "tools", "cell_tables", "gen_all.py")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // ── 指纹 ───────────────────────────────────────────────────────────────

        /// <summary>某步及之前全部步骤的指纹：版本号、种子、每步编号 / 标题 / 超时 / 重试次数（步骤语义或顺序变了 → 不同）。</summary>
        public static string StepsHash(JourneyDef def, int uptoIndex)
        {
            var sb = new StringBuilder();
            sb.Append(def.Id).Append('|').Append(def.Version.ToString(CultureInfo.InvariantCulture)).Append('|').Append(def.Seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i <= uptoIndex && i < def.Steps.Count; i++)
            {
                JourneyStep s = def.Steps[i];
                sb.Append(s.Id).Append('\t').Append(s.Title).Append('\t').Append(s.TimeoutSeconds.ToString("R", CultureInfo.InvariantCulture))
                    .Append('\t').Append(s.MaxRetries.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string _codeHash;
        private static int _codeFiles;

        /// <summary>GameRes/Raw 下纳入指纹的界面布局 / 预制体类文本资源（不含 Fonts 目录与 .meta）。</summary>
        public static readonly string[] RawLayoutExtensions = { ".prefab", ".uxml", ".uss", ".tss", ".asset" };

        /// <summary>
        /// 游戏代码、配置表与界面资源指纹：Assets/GameScripts 下的 .cs / .asmdef / .asmref、Assets/GameRes/Raw/Configs 下的数据文件、
        /// Assets/GameRes/Raw 下的 .prefab / .uxml / .uss / .tss / .asset（Fonts 目录除外）（相对路径 + 内容，按路径排序）。
        /// 不含 Editor：旅程脚本自身的改动由版本号与步骤指纹管——改了断点之前任何步骤的实现（lambda 内容、FgjM3Common 等辅助函数），
        /// 必须把旅程的 Version 加 1，否则旧断点会被放行（编号 / 标题 / 超时 / 重试次数变了才会自动失效）。同一次 Unity 进程里只算一次。
        /// </summary>
        public static string CodeHash(out int files)
        {
            if (_codeHash == null)
            {
                _codeHash = ComputeCodeHash(Application.dataPath, out _codeFiles);
            }
            files = _codeFiles;
            return _codeHash;
        }

        public static string ComputeCodeHash(string assetsDir, out int files)
        {
            var list = new List<string>();
            string scripts = Path.Combine(assetsDir, "GameScripts");
            if (Directory.Exists(scripts))
            {
                list.AddRange(Directory.GetFiles(scripts, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
                                || p.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)));
            }
            string raw = Path.Combine(assetsDir, "GameRes", "Raw");
            string configs = Path.Combine(raw, "Configs");
            if (Directory.Exists(configs))
            {
                list.AddRange(Directory.GetFiles(configs, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));
            }
            // FG-TOOL-01 修复：界面布局与预制体也会改变断点之前的步骤跑出来的世界（按钮做什么、组件上的序列化参数），一并纳入。
            // 只取文本序列化的几类（约 150 个文件、不到 1 MB）；Fonts 目录排除——batchmode 每次都会改写 TMP 字体图集，纳入会让断点每次都失效。
            if (Directory.Exists(raw))
            {
                list.AddRange(Directory.GetFiles(raw, "*", SearchOption.AllDirectories)
                    .Where(p => RawLayoutExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                                && !p.StartsWith(configs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                && !p.Substring(raw.Length).Replace('\\', '/').Split('/').Any(seg => string.Equals(seg, "Fonts", StringComparison.OrdinalIgnoreCase))));
            }
            var rel = list.Select(p => (rel: p.Substring(assetsDir.Length).Replace('\\', '/').TrimStart('/'), full: p))
                .OrderBy(x => x.rel, StringComparer.Ordinal).ToList();
            using (SHA256 sha = SHA256.Create())
            {
                foreach ((string r, string full) in rel)
                {
                    byte[] name = Encoding.UTF8.GetBytes(r + "\n");
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    byte[] data = File.ReadAllBytes(full);
                    sha.TransformBlock(data, 0, data.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                files = rel.Count;
                return Hex(sha.Hash);
            }
        }

        private static string Sha(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        private static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));

        // ── 校验（纯逻辑：自检直接调）──────────────────────────────────────────

        /// <summary>当前环境（代码指纹 / 存档格式 / 生成器版本）——拆出来是为了自检能注入。</summary>
        public struct EnvInfo
        {
            public string CodeHash;
            public int SaveSchema;
            public int ContentVersion;
            public int GeneratorVersion;

            public static EnvInfo Current()
            {
                return new EnvInfo
                {
                    CodeHash = CodeHash(out _),
                    SaveSchema = CampaignSaveService.CurrentSchemaVersion,
                    ContentVersion = CampaignSaveService.CurrentContentVersion,
                    GeneratorVersion = WorldGenVersions.Current,
                };
            }
        }

        /// <summary>
        /// 断点能不能用。<paramref name="codeWarning"/> = 代码指纹不同但 <paramref name="allowCodeChange"/> 放行时的说明（写进报告）。
        /// 返回 false 时 <paramref name="why"/> 写明原因（报告与命令行照抄，后面接“请重新完整跑”）。
        /// </summary>
        public static bool Validate(JourneyDef def, string stepId, Meta meta, EnvInfo env, bool allowCodeChange, out string why, out string codeWarning)
        {
            why = null;
            codeWarning = null;
            if (meta == null)
            {
                why = $"没有 {def?.Id} 在步骤“{stepId}”的断点（先完整跑一次：断点只在完整跑时写，登记的断点步骤：{string.Join("、", def?.CheckpointAfter ?? Array.Empty<string>())}）";
                return false;
            }
            int idx = def.Steps.FindIndex(s => s.Id == stepId);
            if (idx < 0)
            {
                why = $"旅程 {def.Id} 里没有步骤“{stepId}”";
                return false;
            }
            if (!def.CheckpointAfter.Contains(stepId))
            {
                why = $"步骤“{stepId}”不是登记的断点步骤（可选：{string.Join("、", def.CheckpointAfter)}）";
                return false;
            }
            if (meta.format != FormatVersion)
            {
                why = $"断点格式 v{meta.format} ≠ 当前 v{FormatVersion}";
                return false;
            }
            if (meta.journey != def.Id || meta.stepId != stepId)
            {
                why = $"断点属于 {meta.journey} / {meta.stepId}，不是 {def.Id} / {stepId}";
                return false;
            }
            if (meta.journeyVersion != def.Version)
            {
                why = $"旅程版本不匹配：断点 v{meta.journeyVersion}，当前旅程 v{def.Version}";
                return false;
            }
            if (meta.seed != def.Seed)
            {
                why = $"种子不匹配：断点 {meta.seed}，当前旅程 {def.Seed}";
                return false;
            }
            if (meta.stepIndex != idx || meta.stepsHash != StepsHash(def, idx))
            {
                why = $"“{stepId}”及之前的步骤变了（编号 / 标题 / 超时 / 重试次数或顺序；断点在第 {meta.stepIndex + 1} 步，当前在第 {idx + 1} 步）";
                return false;
            }
            if (meta.saveSchema != env.SaveSchema || meta.contentVersion != env.ContentVersion || meta.generatorVersion != env.GeneratorVersion)
            {
                why = $"存档格式 / 内容版本 / 世界生成器版本不匹配：断点 v{meta.saveSchema}/{meta.contentVersion}/生成器 v{meta.generatorVersion}，" +
                      $"当前 v{env.SaveSchema}/{env.ContentVersion}/生成器 v{env.GeneratorVersion}";
                return false;
            }
            if (meta.codeHash != env.CodeHash)
            {
                string text = $"游戏代码或配置表 / 界面资源（预制体、UXML、USS）与写断点时不同（指纹 {Short(meta.codeHash)} → {Short(env.CodeHash)}）";
                if (!allowCodeChange)
                {
                    why = text + "：断点里的世界是旧代码跑出来的；改了代码请完整跑（只想迭代后面的步骤、明知风险时加 --allow-code-change）";
                    return false;
                }
                codeWarning = text + "，按 --allow-code-change 放行：断点来自旧代码，结论只供迭代参考";
            }
            return true;
        }

        private static string Short(string h) => string.IsNullOrEmpty(h) ? "（空）" : h.Substring(0, Math.Min(12, h.Length));

        public static Meta ReadMeta(string dir)
        {
            string path = Path.Combine(dir, MetaFile);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return JsonUtility.FromJson<Meta>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── 续跑：组装旅程 ─────────────────────────────────────────────────────

        /// <summary>
        /// 续跑用的旅程：原旅程的第 1 步（进 Play）+ 断点前置步骤（放回文件 → 主菜单“读取”→ 点槽位 → 等读档进家园 → 恢复变量 / 倍速 / 旅程自己的外部状态）+
        /// 断点那一步之后的全部步骤。不写新断点。
        /// </summary>
        public static JourneyDef BuildResumeDef(JourneyDef def, string stepId, string dir, Meta meta, string codeWarning)
        {
            int idx = def.Steps.FindIndex(s => s.Id == stepId);
            var steps = new List<JourneyStep> { def.Steps[0] };
            steps.AddRange(PreludeSteps(def, dir, meta, codeWarning));
            steps.AddRange(def.Steps.Skip(idx + 1));
            return new JourneyDef
            {
                Id = def.Id,
                Title = def.Title,
                Seed = def.Seed,
                Version = def.Version,
                TotalTimeoutSeconds = def.TotalTimeoutSeconds,
                FailOnErrorLog = def.FailOnErrorLog,
                Steps = steps,
                OnFinish = def.OnFinish,
                OnCheckpointRestored = def.OnCheckpointRestored,
                CheckpointAfter = Array.Empty<string>(),
                ResumedFrom = stepId,
            };
        }

        private static IEnumerable<JourneyStep> PreludeSteps(JourneyDef def, string dir, Meta meta, string codeWarning)
        {
            yield return JourneyCommon.S("ckpt_files", $"断点续跑：放回断点“{meta.stepId}”的存档与旅程文件（{meta.writtenLocal} 写）", 20, null, c =>
            {
                string saves = c.Get("saves");
                if (string.IsNullOrEmpty(saves))
                {
                    return StepOutcome.Fail("本次的临时存档目录还没建（进 Play 那一步没跑完）");
                }
                int copied = CopyTree(Path.Combine(dir, SavesDir), saves);
                c.Log($"断点：{def.Id} 第 {meta.stepIndex + 1} 步“{meta.stepId}”之后；游戏时间第 {meta.gameTicks} 步；放回 {copied} 个文件到 {saves}");
                if (!string.IsNullOrEmpty(codeWarning))
                {
                    c.Log("⚠ " + codeWarning);
                }
                return copied > 0 && File.Exists(CampaignSaveService.SlotPath(meta.slot))
                    ? StepOutcome.Done($"放回断点文件 {copied} 个（槽位 {meta.slot + 1} 的存档在）")
                    : StepOutcome.Fail($"断点里没有槽位 {meta.slot + 1} 的存档（{dir}）");
            });
            yield return JourneyCommon.S("ckpt_menu", "主菜单出现后点“读取”", 150, null, c =>
            {
                UnityEngine.UI.Button button = JourneyInput.FindActiveButton("m_btn_Load");
                if (button == null || c.StepElapsed < 2)
                {
                    return StepOutcome.Wait;
                }
                return JourneyInput.ClickUgui(button)
                    ? StepOutcome.Done("主菜单点“读取”")
                    : StepOutcome.Retry("点“读取”失败：" + JourneyInput.LastUiFailure);
            }, retries: 1);
            yield return JourneyCommon.S("ckpt_slot", $"点断点存档所在的槽位 {meta.slot + 1}", 20, null, c =>
            {
                if (c.GetInt("ckptSlotClicked") == 1)
                {
                    return StepOutcome.Done($"点了槽位 {meta.slot + 1} 的“读取”");
                }
                if (c.StepElapsed < 1)
                {
                    return StepOutcome.Wait;
                }
                UnityEngine.UI.Button action = JourneyInput.FindActiveButton($"m_btn_Slot{meta.slot}Action");
                if (action == null)
                {
                    return c.StepElapsed > 8 ? StepOutcome.Fail($"找不到存档槽 {meta.slot + 1} 的“读取”按钮") : StepOutcome.Wait;
                }
                if (!JourneyInput.ClickUgui(action))
                {
                    return StepOutcome.Fail("点存档槽失败：" + JourneyInput.LastUiFailure);
                }
                c.SetInt("ckptSlotClicked", 1);
                return StepOutcome.Wait;
            });
            yield return JourneyCommon.S("ckpt_loaded", "读档进入家园：一进家园就按暂停键冻结世界，恢复断点时的旅程变量，调用旅程自己的恢复（例如布局库）", 120, null, c =>
            {
                if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive || !CampaignSession.HasActiveCampaign)
                {
                    return StepOutcome.Wait;
                }
                // 断点是“那一步完成的那一刻”的世界；恢复期间世界不能多走（完整跑写完断点下一帧就进下一步）。一进家园就按暂停键，
                // 等变量与倍速恢复好再按暂停键继续（ckpt_speed）。实测不冻结时会多走约 140 步，后台核对这类对出发时状态敏感的步骤会跑偏。
                if (c.GetInt("ckptFrozen") == 0)
                {
                    c.SetInt("ckptFrozen", 1);
                    c.SetLong("ckptLoadTicks", GameClock.Ticks);
                    JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true);
                    return StepOutcome.Wait;
                }
                if (!GameClock.Paused)
                {
                    return c.StepElapsed > 5 ? StepOutcome.Fail("读档后按了暂停键，世界没有暂停") : StepOutcome.Wait;
                }
                if (c.StepElapsed < 2)
                {
                    return StepOutcome.Wait;
                }
                long frozenAt = GameClock.Ticks;
                int restored = 0;
                foreach (Var v in meta.vars)
                {
                    if (v == null || !IsRestoredVar(v.k))
                    {
                        continue;
                    }
                    c.Set(v.k, v.v);
                    restored++;
                }
                def.OnCheckpointRestored?.Invoke(c);
                return StepOutcome.Done($"读档进入家园、按暂停键冻结在游戏时间第 {frozenAt} 步（断点写于第 {meta.gameTicks} 步，读档后多走 {frozenAt - meta.gameTicks} 步）；恢复旅程变量 {restored} 个");
            });
            yield return JourneyCommon.S("ckpt_speed", $"按倍速键恢复断点时的倍速（{meta.speed}x），再按暂停键继续", 10, c => PressSpeed(meta.speed), c =>
            {
                if (c.StepElapsed < 0.5)
                {
                    return StepOutcome.Wait;
                }
                if (!Mathf.Approximately(GameClock.Speed, meta.speed))
                {
                    return StepOutcome.Retry($"倍速 {GameClock.Speed}x，要 {meta.speed}x");
                }
                if (GameClock.Paused != meta.paused)
                {
                    if (c.GetInt("ckptUnpausePressed") == 0)
                    {
                        c.SetInt("ckptUnpausePressed", 1);
                        JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, meta.paused);
                    }
                    return c.StepElapsed > 5 ? StepOutcome.Fail("按了暂停键，暂停状态没恢复成断点时的样子") : StepOutcome.Wait;
                }
                return StepOutcome.Done($"{GameClock.Speed}x、{(GameClock.Paused ? "暂停中" : "运行中")}（与断点时相同；游戏时间第 {GameClock.Ticks} 步）；断点续跑开始，接着跑“{meta.stepId}”之后的步骤");
            }, retries: 1);
        }

        private static void PressSpeed(float speed)
        {
            if (Mathf.Approximately(GameClock.Speed, speed))
            {
                return;
            }
            GameActionId? action = Mathf.Approximately(speed, 0.5f) ? GameActionId.SpeedHalf
                : Mathf.Approximately(speed, 1f) ? GameActionId.SpeedNormal
                : Mathf.Approximately(speed, 2f) ? GameActionId.SpeedDouble
                : Mathf.Approximately(speed, 3f) ? GameActionId.SpeedTriple
                : (GameActionId?)null;
            if (action.HasValue)
            {
                JourneyInput.PressAction(action.Value);
            }
        }

        // ── 写断点（完整跑时，关键步骤完成后由运行器调用）──────────────────────────

        /// <summary>界面是否处于中性状态（没开模态 / 面板、没进建造模式、在家园里）：只有这时写的断点读回来才接得上后面的步骤。</summary>
        public static bool NeutralState(out string why)
        {
            why = null;
            if (!Application.isPlaying || !CampaignSession.HasActiveCampaign)
            {
                why = "不在游戏中";
            }
            else if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                why = "不在家园";
            }
            else if (InputRouter.PanelModalOpen)
            {
                why = "有模态界面开着";
            }
            else if (HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.IsOpen)
            {
                why = "建造模式开着";
            }
            else if (GameRoot.HomeValley.IsFactoryPanelOpen || GameRoot.HomeValley.IsExpeditionPrepPanelOpen)
            {
                why = "装配站 / 远征准备面板开着";
            }
            else if (DiagnosisPanelUIToolkit.IsOpen || ConstructionQueuePanelUIToolkit.IsOpen || BeltPortPanelUIToolkit.IsOpen || LayoutLibraryPanelUIToolkit.IsOpen)
            {
                why = "诊断 / 施工队列 / 端口 / 布局库面板开着";
            }
            return why == null;
        }

        /// <summary>写断点：先写到临时目录，整份写完再替换旧断点（写到一半失败不会留下半份断点）。返回写好的目录；失败抛异常（运行器记一行，不改变旅程结论）。</summary>
        public static string Write(JourneyRunner runner, JourneyStep step, int stepIndex)
        {
            if (!NeutralState(out string why))
            {
                throw new InvalidOperationException("界面不在中性状态（" + why + "），这一步不适合当断点：在旅程里换一个断点步骤");
            }
            JourneyDef def = runner.Journey;
            string root = RootDir;
            string final = Dir(root, def.Id, step.Id);
            string tmp = final + ".writing";
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, true);
            }
            Directory.CreateDirectory(tmp);

            string journeySaves = runner.Store.GetString("v.saves", string.Empty);
            int slot = CampaignSession.ActiveSlotIndex;
            string tmpSaves = Path.Combine(tmp, SavesDir);
            if (!string.IsNullOrEmpty(journeySaves) && Directory.Exists(journeySaves))
            {
                CopyTree(journeySaves, tmpSaves);
            }
            Directory.CreateDirectory(tmpSaves);

            // 正式存档入口写进断点目录（与暂停菜单“保存并返回主菜单”同一条路径：先同步全部地点的实时状态再写盘）。
            CampaignState state = CampaignSession.Current;
            SaveReason reasonBefore = state.LastSaveReason;
            string dirBefore = CampaignSaveService.SaveDirectoryOverrideForTests;
            SaveResult saved;
            try
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = tmpSaves;
                saved = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = dirBefore;
                state.LastSaveReason = reasonBefore; // 写断点不改变存档原因（后面的步骤可能核对存档卡）
            }
            if (!saved.Success)
            {
                throw new IOException("断点存档没写成：" + saved.Message);
            }

            var meta = new Meta
            {
                journey = def.Id,
                journeyVersion = def.Version,
                stepId = step.Id,
                stepIndex = stepIndex,
                stepsHash = StepsHash(def, stepIndex),
                seed = def.Seed,
                codeHash = CodeHash(out int files),
                codeFiles = files,
                saveSchema = CampaignSaveService.CurrentSchemaVersion,
                contentVersion = CampaignSaveService.CurrentContentVersion,
                generatorVersion = WorldGenVersions.Current,
                slot = slot,
                speed = GameClock.Speed,
                paused = GameClock.Paused,
                gameTicks = GameClock.Ticks,
                writtenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                unity = Application.unityVersion,
                vars = runner.SnapshotVars().Select(kv => new Var { k = kv.Key, v = kv.Value }).ToList(),
            };
            File.WriteAllText(Path.Combine(tmp, MetaFile), JsonUtility.ToJson(meta, true), new UTF8Encoding(false));

            if (Directory.Exists(final))
            {
                Directory.Delete(final, true);
            }
            Directory.Move(tmp, final);
            long bytes = new FileInfo(Path.Combine(final, SavesDir, Path.GetFileName(CampaignSaveService.SlotPath(slot)))).Length;
            runner.Write($"  ⤓ 写断点“{step.Id}”：存档 {bytes / 1024} KB（槽位 {slot + 1}，游戏时间第 {meta.gameTicks} 步，{meta.speed}x），旅程变量 {meta.vars.Count} 个 → {final}");
            return final;
        }

        /// <summary>复制目录树（覆盖同名文件），返回文件数。</summary>
        public static int CopyTree(string from, string to)
        {
            if (!Directory.Exists(from))
            {
                return 0;
            }
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\', '/');
                string dst = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? to);
                File.Copy(f, dst, true);
                n++;
            }
            return n;
        }
    }
}
