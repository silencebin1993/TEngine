using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.FirstPlayable
{
    /// <summary>主菜单。Spec §10 要求的主菜单入口。</summary>
    public sealed class FPMenuStage : IFPStage
    {
        private FPGame _game;
        private Canvas _canvas;
        private readonly List<FPClickable> _clicks = new List<FPClickable>();

        private const string Controls =
            "<b>细胞阶段</b>\n" +
            "WASD  移动（带惯性漂移）\n" +
            "撞向体积更小的目标即可吞噬；体积差不足只会弹开\n\n" +
            "<b>生物阶段</b>\n" +
            "WASD  移动    鼠标左键  近战    空格  冲刺（消耗体力，带无敌帧）\n" +
            "鼠标右键  放电（需装备原始放电囊）\n\n" +
            "<b>调试快捷键</b>\n" +
            "F1  切换时间倍速 1x / 2x / 4x / 8x（用于快速验证全流程）\n" +
            "F2  +50 进化点 +150 生物质    F3  跳过当前阶段";

        public void Enter(FPGame game)
        {
            _game = game;
            _game.Run.ResetAll();
            _canvas = FPUIKit.CreateCanvas("FPMenuCanvas", game.transform, 10);

            FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1920f, 1080f), new Color(0.05f, 0.06f, 0.08f, 1f));

            FPUIKit.Label(_canvas.transform, "文明织造", 92, new Color(0.55f, 0.9f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -110f),
                new Vector2(1200f, 120f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform, "First Playable 白模验证 Demo", 34,
                new Color(0.7f, 0.75f, 0.82f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -215f), new Vector2(1200f, 50f), TextAnchor.MiddleCenter);

            FPUIKit.Label(_canvas.transform,
                "验证目标：细胞微选择 → 器官模块构筑 → 生物阶段能力差异，一条三段继承链",
                24, new Color(0.55f, 0.6f, 0.68f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -262f), new Vector2(1300f, 40f), TextAnchor.MiddleCenter);

            RectTransform box = FPUIKit.Panel(_canvas.transform, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(940f, 400f),
                new Color(0.10f, 0.12f, 0.16f, 0.95f));

            FPUIKit.Label(box, Controls, 25, new Color(0.80f, 0.84f, 0.90f),
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(38f, -30f),
                new Vector2(870f, 350f));

            _clicks.Add(FPUIKit.Button(_canvas.transform, "开始（Enter）", 34,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 130f),
                new Vector2(380f, 78f), StartRun, KeyCode.Return));
        }

        private void StartRun()
        {
            _game.GoTo(FPStage.Cell);
        }

        public void Tick(float dt)
        {
            FPClickable.PollAll(_clicks);
            if (Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                StartRun();
            }
        }

        public void Exit()
        {
            _clicks.Clear();
            if (_canvas != null)
            {
                Object.Destroy(_canvas.gameObject);
                _canvas = null;
            }
        }
    }
}
