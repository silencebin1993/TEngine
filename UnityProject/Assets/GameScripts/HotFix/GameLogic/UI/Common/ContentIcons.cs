using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
    /// <summary>ER8-CONTENT-01 / DEBT-ER8CONTENT01-01：内容图标的唯一查表与加载入口。
    ///
    /// - 查表 <see cref="IconIdFor"/>：内容目录 ID（chassis_wheel、comp_gun…）、机器记录里的机型编号
    ///   （erc_001 → 底盘类别）、旧基元等价 ID（LegacyFacadeId）、两件基元芯片（organ_focus…）都能查到；
    ///   查不到返回 null，界面只显示文字，不出破图。
    /// - 加载：贴图放 <c>GameRes/Raw/UI/Icons/&lt;iconId&gt;.png</c>（YooAsset UI 组按文件名寻址），
    ///   首次用到时异步加载并常驻缓存（全部图标不到五十张小图）；<see cref="Tick"/> 在启动后预加载全部。
    /// - 显示：<see cref="Apply"/> 给一个图标位元素设背景图；元素带 <c>mw-icon</c> 类，无图标时加
    ///   <see cref="EmptyClass"/>（占位但不可见，行内文字不跳动）。
    ///
    /// 类别由图标外形区分（AC-ACC-002 不只靠颜色），外形规则见 tools/icons/gen_placeholder_icons.py。</summary>
    public static class ContentIcons
    {
        public const string EmptyClass = "mw-icon-empty";

        /// <summary>建筑状态图标（3D 世界悬浮标记与界面共用）。</summary>
        public const string StateBrownout = "icon_state_brownout";
        public const string StateDamaged = "icon_state_damaged";
        public const string StateUnpowered = "icon_state_unpowered";
        public const string StateBlocked = "icon_state_blocked";

        private static readonly Dictionary<string, string> ChipIcons = new Dictionary<string, string>
        {
            ["organ_focus"] = "icon_chip_focus",
            ["organ_focus_plus"] = "icon_chip_focus_plus",
        };

        private static readonly Dictionary<string, Texture2D> Loaded = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, List<VisualElement>> Waiting = new Dictionary<string, List<VisualElement>>();
        private static Dictionary<string, string> _legacyToIcon;
        private static bool _preloadStarted;

        /// <summary>内容 → 图标名；null＝这条内容没有图标（界面只显示文字）。</summary>
        public static string IconIdFor(string contentId)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return null;
            }
            if (MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def) && !string.IsNullOrEmpty(def.IconId))
            {
                return def.IconId;
            }
            if (ChipIcons.TryGetValue(contentId, out string chipIcon))
            {
                return chipIcon;
            }
            string archetype = ChassisCatalog.ResolveArchetype(contentId);
            if (archetype != null && ChassisCatalog.TryGet(archetype, out MechanicalContentDef chassis))
            {
                return chassis.IconId;
            }
            return LegacyMap().TryGetValue(contentId, out string legacyIcon) ? legacyIcon : null;
        }

        /// <summary>全部会被用到的图标名（内容目录 + 芯片 + 建筑状态），自检逐个核对贴图存在。</summary>
        public static List<string> AllIconIds()
        {
            var ids = new List<string>();
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                if (!string.IsNullOrEmpty(def.IconId) && !ids.Contains(def.IconId))
                {
                    ids.Add(def.IconId);
                }
            }
            foreach (string chip in ChipIcons.Values)
            {
                ids.Add(chip);
            }
            ids.Add(StateBrownout);
            ids.Add(StateDamaged);
            ids.Add(StateUnpowered);
            ids.Add(StateBlocked);
            return ids;
        }

        public static void Apply(VisualElement target, string contentId)
        {
            ApplyIcon(target, IconIdFor(contentId));
        }

        public static void ApplyIcon(VisualElement target, string iconId)
        {
            if (target == null)
            {
                return;
            }
            target.userData = iconId;
            if (string.IsNullOrEmpty(iconId))
            {
                target.AddToClassList(EmptyClass);
                target.style.backgroundImage = StyleKeyword.None;
                return;
            }
            target.RemoveFromClassList(EmptyClass);
            if (Loaded.TryGetValue(iconId, out Texture2D texture) && texture != null)
            {
                target.style.backgroundImage = new StyleBackground(texture);
                return;
            }
            target.style.backgroundImage = StyleKeyword.None;
            if (!Application.isPlaying)
            {
                return; // 编辑器非 Play（自检/探针）：资源模块未启动，不发起加载。
            }
            if (!Waiting.TryGetValue(iconId, out List<VisualElement> list))
            {
                list = new List<VisualElement>(2);
                Waiting[iconId] = list;
                Load(iconId).Forget();
            }
            if (!list.Contains(target))
            {
                list.Add(target);
            }
        }

        /// <summary>已加载的贴图（3D 世界标记用）；还没加载好返回 false 并发起加载。</summary>
        public static bool TryGetTexture(string iconId, out Texture2D texture)
        {
            if (!string.IsNullOrEmpty(iconId) && Loaded.TryGetValue(iconId, out texture) && texture != null)
            {
                return true;
            }
            texture = null;
            if (Application.isPlaying && !string.IsNullOrEmpty(iconId) && !Loaded.ContainsKey(iconId) && !Waiting.ContainsKey(iconId))
            {
                Waiting[iconId] = new List<VisualElement>(0);
                Load(iconId).Forget();
            }
            return false;
        }

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

        /// <summary>3D 世界标记用的 Sprite（由已加载贴图生成一次并缓存，1 个贴图宽度＝1 世界单位）。</summary>
        public static bool TryGetSprite(string iconId, out Sprite sprite)
        {
            if (!string.IsNullOrEmpty(iconId) && Sprites.TryGetValue(iconId, out sprite) && sprite != null)
            {
                return true;
            }
            sprite = null;
            if (!TryGetTexture(iconId, out Texture2D texture))
            {
                return false;
            }
            sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), texture.width);
            sprite.name = iconId;
            Sprites[iconId] = sprite;
            return true;
        }

        /// <summary>每帧由 GameRoot 调用：Play 后第一帧预加载全部图标，之后 O(1) 早退。</summary>
        public static void Tick()
        {
            if (_preloadStarted || !Application.isPlaying)
            {
                return;
            }
            _preloadStarted = true;
            foreach (string iconId in AllIconIds())
            {
                TryGetTexture(iconId, out _);
            }
        }

        private static async UniTaskVoid Load(string iconId)
        {
            Texture2D texture = null;
            try
            {
                texture = await GameModule.Resource.LoadAssetAsync<Texture2D>(iconId);
            }
            catch (System.Exception e)
            {
                Log.Warning($"[ContentIcons] 图标 {iconId} 加载失败：{e.Message}");
            }
            Loaded[iconId] = texture;
            if (!Waiting.TryGetValue(iconId, out List<VisualElement> list))
            {
                return;
            }
            Waiting.Remove(iconId);
            if (texture == null)
            {
                return;
            }
            foreach (VisualElement element in list)
            {
                // 等待期间行可能已被复用去显示别的内容——只给仍然要这个图标的元素赋值。
                if (element != null && element.userData as string == iconId)
                {
                    element.style.backgroundImage = new StyleBackground(texture);
                }
            }
        }

        private static Dictionary<string, string> LegacyMap()
        {
            if (_legacyToIcon != null)
            {
                return _legacyToIcon;
            }
            _legacyToIcon = new Dictionary<string, string>();
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                if (!string.IsNullOrEmpty(def.LegacyFacadeId) && !string.IsNullOrEmpty(def.IconId)
                    && !_legacyToIcon.ContainsKey(def.LegacyFacadeId))
                {
                    _legacyToIcon[def.LegacyFacadeId] = def.IconId;
                }
            }
            return _legacyToIcon;
        }
    }
}
