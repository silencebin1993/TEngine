using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>结算界面上下文，由结束阶段写入 <see cref="FPGame.Result"/>。</summary>
    public sealed class FPResultContext
    {
        public FPStage FromStage;
        public bool Success;
        public string Title;
        public string Message;
        /// <summary>Spec §9：整局重开，RunData 全部清空。</summary>
        public bool CanRestartRun;
        /// <summary>Spec §9：生物阶段失败可保留构筑重试（FP 专属开发期便利）。</summary>
        public bool CanRetryCreature;
    }

    /// <summary>
    /// 阶段结算 + First Playable 结束总结。Spec §10 / §14：明确展示主导路线与其来源，
    /// 让三段继承链可被看见。
    /// </summary>
    public sealed class FPResultStage : IFPStage
    {
        private FPGame _game;
        private FPRunData _run;
        private GameObject _root;
        private Canvas _canvas;
        private readonly List<FPClickable> _clicks = new List<FPClickable>();

        public void Enter(FPGame game)
        {
            _game = game;
            _run = game.Run;
            FPResultContext ctx = game.Result ?? new FPResultContext
            {
                Title = "本局结束", Message = "", CanRestartRun = true,
            };

            _root = new GameObject("FPResultStage");
            _root.transform.SetParent(game.transform, false);
            game.ConfigureCamera(null, 16f, new Vector3(0f, 30f, 0f), new Vector3(90f, 0f, 0f));

            _canvas = FPUIKit.CreateCanvas("FPResultCanvas", _root.transform, 60);
            FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1920f, 1080f), new Color(0.04f, 0.05f, 0.07f, 1f));

            Color accent = ctx.Success
                ? new Color(0.55f, 1f, 0.70f)
                : new Color(1f, 0.55f, 0.50f);

            FPUIKit.Label(_canvas.transform, ctx.Success ? "验证完成" : "本局失败", 40, accent,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -44f),
                new Vector2(1400f, 50f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform, ctx.Title, 56, Color.white,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -98f),
                new Vector2(1500f, 70f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform, ctx.Message, 26, new Color(0.78f, 0.83f, 0.90f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -172f),
                new Vector2(1400f, 70f), TextAnchor.UpperCenter);

            BuildSummary();
            BuildButtons(ctx);
        }

        /// <summary>三段继承链展示：细胞微选择 → 器官模块 → 生物阶段属性。</summary>
        private void BuildSummary()
        {
            FPRoute route = _run.DominantRoute(out bool mixed);
            _run.ScoreRoutes(out int d, out int s, out int t, out int _);
            FPStats stats = _run.ResolveStats();

            string routeText = mixed ? "混合型" : FPModuleTable.RouteName(route);
            FPUIKit.Label(_canvas.transform, $"主导路线：{routeText}", 40,
                new Color(1f, 0.88f, 0.50f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -256f), new Vector2(1400f, 52f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform,
                $"路线计分  吞噬扩张 {d} · 功能特化 {s} · 科技统治 {t}" +
                (mixed ? "（三路各 1 点，如实判定为混合型）" : ""),
                24, new Color(0.66f, 0.72f, 0.80f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -302f), new Vector2(1400f, 36f), TextAnchor.MiddleCenter);

            const float colW = 430f;
            const float gap = 34f;
            float startX = -(colW * 3f + gap * 2f) * 0.5f + colW * 0.5f;

            string microName = MicroName(_run.MicroChoice);
            Column(startX, "第一段 · 细胞阶段",
                $"微选择：<b>{microName}</b>\n" +
                $"路线倾向：{FPModuleTable.RouteName(FPModuleTable.MicroChoiceRoute(_run.MicroChoice))}\n\n" +
                $"留场 {FormatTime(_run.CellSeconds)}，打到第 {_run.WaveReached} 波\n" +
                $"吞噬食物 {_run.FoodEaten} 个，威胁 {_run.ThreatEaten} 个\n\n" +
                "微选择决定了积累效率与存活能力，\n从而决定能留场多久、攒到多少生物质。", colW);

            Column(startX + colW + gap, "第二段 · 器官阶段",
                $"构筑：<b>{_run.ModuleSummary()}</b>\n" +
                $"槽位 {_run.Modules.Count} / {FPRunData.SlotLimit}\n" +
                $"剩余生物质 {_run.Biomass}\n\n" +
                ModuleDetail(), colW);

            string zapLine = stats.HasZap
                ? $"放电 {FPModuleTable.ZapDamage:0} 伤害／{FPModuleTable.ZapRange:0.#} 米"
                : "无远程能力（3 米外伤害为 0）";
            Column(startX + (colW + gap) * 2f, "第三段 · 生物阶段",
                $"最大生命值 <b>{stats.MaxHp:0}</b>\n" +
                $"移速 <b>{stats.Speed:0.0}</b>\n" +
                $"近战伤害 <b>{stats.MeleeDamage:0.#}</b>\n" +
                $"冲刺无敌帧 {stats.DashInvuln:0.00} 秒\n" +
                $"体力 {stats.StaminaMax:0}，冲刺耗力 {stats.DashCost:0}\n" +
                $"敌人察觉范围 ×{stats.AggroMul:0.00}\n" +
                $"{zapLine}\n" +
                (stats.KillHeal > 0f ? $"击杀回血 {stats.KillHeal:0}\n" : "") +
                (_run.EliteFightSeconds > 0.1f
                    ? $"\n精英战耗时 {_run.EliteFightSeconds:0.0} 秒"
                    : ""), colW);
        }

        private void Column(float x, string header, string body, float w)
        {
            RectTransform box = FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(x, -30f), new Vector2(w, 400f),
                new Color(0.10f, 0.12f, 0.16f, 0.96f));

            FPUIKit.Label(box, header, 30, new Color(0.60f, 0.90f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -20f),
                new Vector2(w - 40f, 40f), TextAnchor.MiddleCenter);

            FPUIKit.Label(box, body, 24, new Color(0.82f, 0.87f, 0.93f),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -70f),
                new Vector2(w - 52f, 310f));
        }

        private string ModuleDetail()
        {
            if (_run.Modules.Count == 0)
            {
                return "未购买任何模块，以基础形态进入生物阶段。";
            }
            string s = "";
            for (int i = 0; i < _run.Modules.Count; i++)
            {
                FPModuleDef def = FPModuleTable.Get(_run.Modules[i]);
                if (def == null)
                {
                    continue;
                }
                s += $"· {def.Name}（{def.Price}）\n  {def.Desc}\n";
            }
            return s;
        }

        private static string MicroName(FPMicroChoice choice)
        {
            switch (choice)
            {
                case FPMicroChoice.Gluttony: return "贪食囊";
                case FPMicroChoice.Phototaxis: return "趋光纤毛";
                case FPMicroChoice.Metabolic: return "代谢泡";
                default: return "未选择";
            }
        }

        private static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60:0}:{s % 60:00}";
        }

        private void BuildButtons(FPResultContext ctx)
        {
            List<string> labels = new List<string>();
            List<System.Action> actions = new List<System.Action>();
            List<KeyCode> keys = new List<KeyCode>();

            if (ctx.CanRetryCreature)
            {
                labels.Add("保留构筑重试生物阶段（R）");
                actions.Add(RetryCreature);
                keys.Add(KeyCode.R);
            }
            if (ctx.CanRestartRun)
            {
                labels.Add("整局重开（Enter）");
                actions.Add(RestartRun);
                keys.Add(KeyCode.Return);
            }
            labels.Add("返回主菜单（Esc）");
            actions.Add(BackToMenu);
            keys.Add(KeyCode.Escape);

            const float w = 420f;
            const float gap = 30f;
            float startX = -(w * labels.Count + gap * (labels.Count - 1)) * 0.5f + w * 0.5f;

            for (int i = 0; i < labels.Count; i++)
            {
                _clicks.Add(FPUIKit.Button(_canvas.transform, labels[i], 28,
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(startX + i * (w + gap), 64f), new Vector2(w, 70f),
                    actions[i], keys[i]));
            }
        }

        /// <summary>Spec §9：保留构筑重试生物阶段，RunData 不清空。</summary>
        private void RetryCreature()
        {
            _run.EliteFightSeconds = 0f;
            _run.CreatureRetryCount++;
            _game.Result = null;
            _game.GoTo(FPStage.Creature);
        }

        /// <summary>Spec §9：整局重开，RunData 全部清空，回到细胞阶段 0:00。</summary>
        private void RestartRun()
        {
            _run.ResetAll();
            _game.Result = null;
            _game.GoTo(FPStage.Cell);
        }

        private void BackToMenu()
        {
            _run.ResetAll();
            _game.Result = null;
            _game.GoTo(FPStage.None);
        }

        public void Tick(float dt)
        {
            FPClickable.PollAll(_clicks);
        }

        public void Exit()
        {
            _clicks.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            _canvas = null;
        }
    }
}
