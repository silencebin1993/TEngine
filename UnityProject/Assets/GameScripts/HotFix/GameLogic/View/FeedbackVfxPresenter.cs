using System;
using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Settings;
using GameLogic.UI.Common;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>ER8-CONTENT-01（DEBT-ER8CONTENT01-01 VFX 半边）：命中、厚甲弹开、击毁、敌方开火、重炮、标记跳转、
    /// 熔穿过载、信号断连/恢复的世界特效。此前这些时刻只有声音与字幕，3D 世界里什么都不发生。
    ///
    /// 统一读 <see cref="FeedbackCues.ReadMoments"/> 的带位置时刻——玩法代码只报时刻，不直接调特效，与音效/字幕
    /// 同一出口。白色占位贴图按时刻着色、放大并淡出；击中类立在单位中心高度并朝向镜头，反应/信号类平铺在地面。
    /// 固定 <see cref="PoolSize"/> 个对象的池，每帧只处理新时刻与池内活动特效，开销与敌人数无关；池满时回收最旧的。
    /// “降低闪光”开启时亮度降到 45%、放大幅度收窄（AC-ACC-003）。</summary>
    public sealed class FeedbackVfxPresenter : MonoBehaviour
    {
        public const int PoolSize = 32;

        /// <summary>一种时刻的特效外观。尺寸是世界单位（贴图宽度）。</summary>
        public readonly struct VfxSpec
        {
            public readonly string SpriteId;
            public readonly Color Color;
            public readonly float FromSize;
            public readonly float ToSize;
            public readonly float Duration;
            public readonly float Height;
            public readonly bool Flat;

            public VfxSpec(string spriteId, Color color, float fromSize, float toSize, float duration, float height, bool flat)
            {
                SpriteId = spriteId;
                Color = color;
                FromSize = fromSize;
                ToSize = toSize;
                Duration = duration;
                Height = height;
                Flat = flat;
            }
        }

        /// <summary>自检可替换：贴图来源（默认走 <see cref="ContentIcons.TryGetSprite"/>，还没加载好返回 null，这次特效跳过）。</summary>
        public static Func<string, Sprite> SpriteResolver = id => ContentIcons.TryGetSprite(id, out Sprite s) ? s : null;

        private sealed class Effect
        {
            public GameObject Go;
            public SpriteRenderer Renderer;
            /// <summary>每个池槽一份材质实例（建池时一次性创建），着色写它的主色，不逐帧新建材质。</summary>
            public Material Material;
            public FeedbackCueId Cue;
            public VfxSpec Spec;
            public float Start;
            public float SpriteWidth;
            public bool Active;
        }

        private readonly Effect[] _pool = new Effect[PoolSize];
        private readonly List<FeedbackCues.PositionalMoment> _incoming = new List<FeedbackCues.PositionalMoment>(16);
        private long _cursor;

        public int ActiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pool.Length; i++)
                {
                    if (_pool[i] != null && _pool[i].Active)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>自检读取：第 <paramref name="index"/> 个池槽的当前状态。</summary>
        public bool TryGetEffect(int index, out FeedbackCueId cue, out Vector3 position, out float size, out float alpha)
        {
            Effect e = index >= 0 && index < _pool.Length ? _pool[index] : null;
            cue = e?.Cue ?? FeedbackCueId.None;
            position = e != null ? e.Go.transform.position : default;
            size = e != null && e.SpriteWidth > 0f ? e.Go.transform.localScale.x * e.SpriteWidth : 0f;
            alpha = e != null ? e.Material.color.a : 0f;
            return e != null && e.Active;
        }

        private bool _built;

        private void Awake()
        {
            EnsurePool();
        }

        /// <summary>建池（Awake 调用；编辑器自检里 Awake 不执行，由第一次 <see cref="Tick"/> 补建）。从建池那一刻开始读，
        /// 之前（例如主菜单里）的时刻不补放。</summary>
        private void EnsurePool()
        {
            if (_built)
            {
                return;
            }
            _built = true;
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject("Vfx" + i);
                go.transform.SetParent(transform, false);
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.enabled = false;
                // 显式复制默认精灵材质（不用 renderer.material：编辑器下会警告泄漏，自检也在编辑器里建池）。
                Material source = renderer.sharedMaterial;
                var material = source != null ? new Material(source) : new Material(Shader.Find("Sprites/Default"));
                renderer.sharedMaterial = material;
                _pool[i] = new Effect { Go = go, Renderer = renderer, Material = material };
            }
            _cursor = FeedbackCues.MomentSequence;
        }

        private void Update()
        {
            Tick(Time.unscaledTime);
        }

        private void OnDestroy()
        {
            ReleasePool();
        }

        /// <summary>释放池里的材质实例（对象随本节点一起销毁）。自检在编辑器里手动调用。</summary>
        public void ReleasePool()
        {
            for (int i = 0; i < _pool.Length; i++)
            {
                Material m = _pool[i]?.Material;
                if (m == null)
                {
                    continue;
                }
                if (Application.isPlaying)
                {
                    Destroy(m);
                }
                else
                {
                    DestroyImmediate(m);
                }
                _pool[i].Material = null;
            }
        }

        /// <summary>取新时刻生成特效，推进所有活动特效。自检直接调用。</summary>
        public void Tick(float now)
        {
            EnsurePool();
            _incoming.Clear();
            _cursor = FeedbackCues.ReadMoments(_cursor, _incoming);
            bool reduced = GameSettings.FlashReductionEnabled;
            for (int i = 0; i < _incoming.Count; i++)
            {
                // FG0-ARCH-01：只画镜头正在观察的地点里发生的时刻（其它地点照常在模拟，但不在眼前）。
                if (!FeedbackCues.IsInObservedSite(_incoming[i].SiteId))
                {
                    continue;
                }
                if (TryGetSpec(_incoming[i].Cue, reduced, out VfxSpec spec))
                {
                    Spawn(_incoming[i], spec, now);
                }
            }

            for (int i = 0; i < _pool.Length; i++)
            {
                Effect e = _pool[i];
                if (!e.Active)
                {
                    continue;
                }
                float t = e.Spec.Duration > 0f ? (now - e.Start) / e.Spec.Duration : 1f;
                if (t >= 1f)
                {
                    e.Active = false;
                    e.Renderer.enabled = false;
                    continue;
                }
                float eased = 1f - (1f - t) * (1f - t);
                float size = Mathf.Lerp(e.Spec.FromSize, e.Spec.ToSize, eased);
                e.Go.transform.localScale = Vector3.one * (size / e.SpriteWidth);
                Color c = e.Spec.Color;
                c.a *= Mathf.Pow(1f - t, 1.5f);
                e.Material.color = c;
            }
        }

        private void Spawn(FeedbackCues.PositionalMoment moment, VfxSpec spec, float now)
        {
            Sprite sprite = SpriteResolver?.Invoke(spec.SpriteId);
            if (sprite == null || sprite.bounds.size.x <= 0f)
            {
                return;
            }
            Effect slot = null;
            for (int i = 0; i < _pool.Length; i++)
            {
                if (!_pool[i].Active)
                {
                    slot = _pool[i];
                    break;
                }
                if (slot == null || _pool[i].Start < slot.Start)
                {
                    slot = _pool[i]; // 池满：回收最旧的。
                }
            }

            slot.Cue = moment.Cue;
            slot.Spec = spec;
            slot.Start = now;
            slot.SpriteWidth = sprite.bounds.size.x;
            slot.Active = true;
            slot.Renderer.sprite = sprite;
            slot.Material.color = spec.Color;
            slot.Renderer.enabled = true;
            Transform t = slot.Go.transform;
            t.position = new Vector3(moment.Position.x, moment.Position.y + spec.Height, moment.Position.z);
            t.localScale = Vector3.one * (spec.FromSize / slot.SpriteWidth);
            Camera cam = Camera.main;
            t.rotation = spec.Flat ? Quaternion.Euler(90f, 0f, 0f) : cam != null ? cam.transform.rotation : Quaternion.identity;
        }

        /// <summary>时刻 → 特效外观（纯函数，自检直接断言）。没有列出的时刻不出特效（例如开火与命中同点同时发生，只出命中）。</summary>
        public static bool TryGetSpec(FeedbackCueId cue, bool flashReduced, out VfxSpec spec)
        {
            switch (cue)
            {
                case FeedbackCueId.EnemyHit:
                    spec = Make(ContentIcons.VfxSpark, new Color(1f, 0.72f, 0.3f, 0.95f), 0.5f, 1.1f, 0.22f, 1.1f, false, flashReduced);
                    return true;
                case FeedbackCueId.ArmorHit:
                    spec = Make(ContentIcons.VfxRing, new Color(0.75f, 0.82f, 0.9f, 0.95f), 0.4f, 1.2f, 0.3f, 1.1f, false, flashReduced);
                    return true;
                case FeedbackCueId.EnemyDestroyed:
                    spec = Make(ContentIcons.VfxBurst, new Color(1f, 0.55f, 0.2f, 0.85f), 0.6f, 2.6f, 0.5f, 1.0f, false, flashReduced);
                    return true;
                case FeedbackCueId.EnemyAttack:
                    spec = Make(ContentIcons.VfxSpark, new Color(1f, 0.3f, 0.25f, 0.95f), 0.4f, 0.9f, 0.18f, 1.3f, false, flashReduced);
                    return true;
                case FeedbackCueId.CannonFire:
                    spec = Make(ContentIcons.VfxBurst, new Color(1f, 0.6f, 0.15f, 0.9f), 0.8f, 2.2f, 0.35f, 1.1f, false, flashReduced);
                    return true;
                case FeedbackCueId.ReactionMarkJump:
                    spec = Make(ContentIcons.VfxRing, new Color(0.35f, 0.85f, 1f, 0.9f), 1f, 6f, 0.6f, 0.06f, true, flashReduced);
                    return true;
                case FeedbackCueId.ReactionMeltOverload:
                    spec = Make(ContentIcons.VfxRing, new Color(1f, 0.35f, 0.1f, 0.9f), 0.8f, 3.5f, 0.5f, 0.06f, true, flashReduced);
                    return true;
                case FeedbackCueId.SignalLost:
                    // 收缩的灰环：链路“缩回去”。
                    spec = Make(ContentIcons.VfxRing, new Color(0.6f, 0.6f, 0.65f, 0.85f), 2.5f, 0.8f, 0.6f, 0.06f, true, flashReduced);
                    return true;
                case FeedbackCueId.SignalRestored:
                    spec = Make(ContentIcons.VfxRing, new Color(0.4f, 0.9f, 0.55f, 0.85f), 0.8f, 2.5f, 0.5f, 0.06f, true, flashReduced);
                    return true;
                default:
                    spec = default;
                    return false;
            }
        }

        private static VfxSpec Make(string sprite, Color color, float from, float to, float duration, float height, bool flat, bool flashReduced)
        {
            if (flashReduced)
            {
                color.a *= 0.45f;
                to = from + (to - from) * 0.7f;
            }
            return new VfxSpec(sprite, color, from, to, duration, height, flat);
        }
    }
}
