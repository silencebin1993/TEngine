using System;
using System.Collections.Generic;
using System.Text;
using GameLogic.Campaign.Feedback;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8-CONTENT-01（DEBT-ER8CONTENT01-01 VFX 半边）：世界特效层的回归闸门——时刻→外观表、带位置时刻的环形缓冲、
    /// 特效生成/放大/淡出/结束、对象池上限、“降低闪光”减弱。编辑器里 Awake/Update 不跑，直接调 <c>Tick</c>，
    /// 贴图换成测试贴图。并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FeedbackVfxSelfCheck
    {
        private static readonly FeedbackCueId[] VfxCues =
        {
            FeedbackCueId.EnemyHit, FeedbackCueId.ArmorHit, FeedbackCueId.EnemyDestroyed, FeedbackCueId.EnemyAttack,
            FeedbackCueId.CannonFire, FeedbackCueId.ReactionMarkJump, FeedbackCueId.ReactionMeltOverload,
            FeedbackCueId.SignalLost, FeedbackCueId.SignalRestored,
        };

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：世界特效")]
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
            Line("\n[特效] 命中/击毁/反应/信号的世界特效（ER8-CONTENT-01 DEBT-ER8CONTENT01-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会替换特效贴图来源）");
                return 0;
            }
            bool savedFlash = GameSettings.FlashReductionEnabled;
            Func<string, Sprite> savedResolver = FeedbackVfxPresenter.SpriteResolver;
            Texture2D texture = null;
            Sprite sprite = null;
            GameObject host = null;
            FeedbackVfxPresenter presenter = null;
            try
            {
                CheckSpecTable();
                CheckMomentBuffer();

                texture = new Texture2D(16, 16) { hideFlags = HideFlags.HideAndDontSave };
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 16f, 16f), new Vector2(0.5f, 0.5f), 16f);
                sprite.hideFlags = HideFlags.HideAndDontSave;
                Sprite testSprite = sprite;
                FeedbackVfxPresenter.SpriteResolver = _ => testSprite;
                if (GameSettings.FlashReductionEnabled)
                {
                    GameSettings.SetFlashReductionEnabled(false);
                }

                host = new GameObject("__FeedbackVfxSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
                presenter = host.AddComponent<FeedbackVfxPresenter>();
                CheckPresenter(presenter);
            }
            catch (Exception e)
            {
                Fail($"特效自检抛异常：{e}");
            }
            finally
            {
                FeedbackVfxPresenter.SpriteResolver = savedResolver;
                if (GameSettings.FlashReductionEnabled != savedFlash)
                {
                    GameSettings.SetFlashReductionEnabled(savedFlash);
                }
                if (presenter != null)
                {
                    presenter.ReleasePool();
                }
                if (host != null)
                {
                    Object.DestroyImmediate(host);
                }
                if (sprite != null)
                {
                    Object.DestroyImmediate(sprite);
                }
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
                FeedbackCues.ResetForTests();
            }
            return _fail;
        }

        private static void CheckSpecTable()
        {
            var bad = new List<string>();
            List<string> knownSprites = ContentIcons.AllIconIds();
            foreach (FeedbackCueId cue in VfxCues)
            {
                if (!FeedbackVfxPresenter.TryGetSpec(cue, false, out FeedbackVfxPresenter.VfxSpec spec)
                    || !knownSprites.Contains(spec.SpriteId) || spec.Duration <= 0.1f || spec.Duration > 1f
                    || spec.FromSize <= 0f || spec.ToSize <= 0f || spec.Color.a <= 0.5f)
                {
                    bad.Add(cue.ToString());
                    continue;
                }
                FeedbackVfxPresenter.TryGetSpec(cue, true, out FeedbackVfxPresenter.VfxSpec reduced);
                bool dimmer = reduced.Color.a <= spec.Color.a * 0.5f;
                bool narrower = Mathf.Abs(reduced.ToSize - reduced.FromSize) < Mathf.Abs(spec.ToSize - spec.FromSize);
                if (!dimmer || !narrower)
                {
                    bad.Add(cue + "（降低闪光未减弱）");
                }
            }
            Expect(bad.Count == 0, $"{VfxCues.Length} 类战斗/反应/信号时刻都有特效（贴图已登记、时长 0.1～1 秒），降低闪光时更暗、放大更少" +
                                   (bad.Count == 0 ? string.Empty : "：" + string.Join("、", bad)));
            Expect(!FeedbackVfxPresenter.TryGetSpec(FeedbackCueId.WeaponFire, false, out _),
                "开火与命中同点同时发生，只出命中特效（不叠两层）");
        }

        private static void CheckMomentBuffer()
        {
            long before = FeedbackCues.MomentSequence;
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, new Vector2(3f, 4f));
            FeedbackCues.RaiseAt(FeedbackCueId.ArmorHit, new Vector3(5f, 0f, 6f));
            var read = new List<FeedbackCues.PositionalMoment>();
            long cursor = FeedbackCues.ReadMoments(before, read);
            Expect(read.Count == 2 && read[0].Cue == FeedbackCueId.EnemyHit && read[0].Position == new Vector3(3f, 0f, 4f)
                   && read[1].Cue == FeedbackCueId.ArmorHit && cursor == FeedbackCues.MomentSequence,
                "带位置时刻按先后记录（地面坐标换成世界坐标），读方按序号接着读");

            read.Clear();
            FeedbackCues.ReadMoments(cursor, read);
            Expect(read.Count == 0, "没有新时刻时读到 0 条（不重放）");

            for (int i = 0; i < 70; i++)
            {
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, new Vector2(i, 0f));
            }
            read.Clear();
            long latest = FeedbackCues.ReadMoments(cursor, read);
            Expect(read.Count == 64 && read[read.Count - 1].Position.x == 69f && latest == FeedbackCues.MomentSequence,
                $"读方落后超过缓冲容量：只给最近 64 条、不越界（实际 {read.Count} 条）");
        }

        private static void CheckPresenter(FeedbackVfxPresenter presenter)
        {
            presenter.Tick(0f); // 建池：从这一刻开始读，之前的时刻不补放。
            Expect(presenter.ActiveCount == 0, "建池时不补放之前的时刻");

            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, new Vector2(3f, 4f));
            FeedbackCues.RaiseAt(FeedbackCueId.ReactionMarkJump, new Vector2(-2f, 1f));
            FeedbackCues.RaiseAt(FeedbackCueId.WeaponFire, new Vector2(3f, 4f));
            presenter.Tick(10f);
            FeedbackVfxPresenter.TryGetSpec(FeedbackCueId.EnemyHit, false, out FeedbackVfxPresenter.VfxSpec hitSpec);
            bool hitFound = Find(presenter, FeedbackCueId.EnemyHit, out Vector3 hitPos, out float hitSize, out float hitAlpha);
            bool ringFound = Find(presenter, FeedbackCueId.ReactionMarkJump, out Vector3 ringPos, out _, out _);
            Expect(presenter.ActiveCount == 2 && hitFound && ringFound
                   && (hitPos - new Vector3(3f, hitSpec.Height, 4f)).sqrMagnitude < 1e-4f && Mathf.Abs(hitSize - hitSpec.FromSize) < 1e-3f
                   && Mathf.Abs(ringPos.y - 0.06f) < 1e-4f,
                $"命中特效立在单位中心高度、反应环贴地，开火不单独出特效（活动 {presenter.ActiveCount} 个）");

            presenter.Tick(10f + hitSpec.Duration * 0.5f);
            Find(presenter, FeedbackCueId.EnemyHit, out _, out float midSize, out float midAlpha);
            Expect(midSize > hitSize && midAlpha < hitAlpha, $"特效随时间放大并淡出（尺寸 {hitSize:F2}→{midSize:F2}，透明度 {hitAlpha:F2}→{midAlpha:F2}）");

            presenter.Tick(12f);
            Expect(presenter.ActiveCount == 0, "到时长后全部结束、回到池里");

            for (int i = 0; i < 40; i++)
            {
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, new Vector2(i, 0f));
            }
            presenter.Tick(20f);
            Expect(presenter.ActiveCount == FeedbackVfxPresenter.PoolSize, $"同时 40 个时刻：最多 {FeedbackVfxPresenter.PoolSize} 个特效（池满回收最旧的，不新建对象）");
            presenter.Tick(30f);

            GameSettings.SetFlashReductionEnabled(true);
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, new Vector2(1f, 1f));
            presenter.Tick(40f);
            Find(presenter, FeedbackCueId.EnemyHit, out _, out _, out float reducedAlpha);
            Expect(reducedAlpha <= hitSpec.Color.a * 0.5f, $"降低闪光开启：同一命中特效更暗（透明度 {reducedAlpha:F2}，常规 {hitSpec.Color.a:F2}）");
        }

        private static bool Find(FeedbackVfxPresenter presenter, FeedbackCueId cue, out Vector3 position, out float size, out float alpha)
        {
            for (int i = 0; i < FeedbackVfxPresenter.PoolSize; i++)
            {
                if (presenter.TryGetEffect(i, out FeedbackCueId c, out position, out size, out alpha) && c == cue)
                {
                    return true;
                }
            }
            position = default;
            size = 0f;
            alpha = 0f;
            return false;
        }

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
