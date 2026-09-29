using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG2-FW-03（FG02 FGR-FW-031“标签可见”；承接 DEBT-FG2FW02-03）：敌人和己方单位头顶的状态标签图标由内核实例化绘制（<see cref="CombatRenderer"/>，
    /// 形状为主、颜色为辅，底部小点 = 叠层）；光标停在带标签的单位上时，悬停读数列出每个标签的名称、剩余时间与叠层数。
    ///
    /// 每帧（地点被观察时，由 <see cref="CombatSite.FrameRender"/> 调用）只做一次常数开销的拾取：离光标最近的带标签单位由 AOT 内核 <see cref="CombatKernel.PickUnit"/> 找
    /// （逐单位扫描在内核里），热更层只拼一个单位的文字。
    /// </summary>
    public static class StatusTagHover
    {
        /// <summary>光标离单位边缘多远以内算“停在它上面”（米）。</summary>
        public const float PickRadius = 0.8f;

        private static CombatSite _hoveredSite;
        private static int _hoveredUnit;

        // 每帧不分配：提示内容的来源是一个缓存的委托（读当前悬停的地点 / 单位），拼文字复用同一组缓冲。
        private static readonly System.Func<TooltipContent> Provider = ProvideHovered;
        private static readonly List<(string Glyph, string Name, float Seconds, int Stacks)> LineBuffer = new List<(string, string, float, int)>(8);
        private static readonly StringBuilder TextBuffer = new StringBuilder(128);

        public static int HoveredUnit => _hoveredUnit;

        /// <summary>每帧：光标下带标签的单位 → 世界悬停提示；没有就离开。</summary>
        public static void Tick(CombatSite site, Camera camera)
        {
            if (site == null || site.IsDisposed || camera == null)
            {
                return;
            }
            int unit = 0;
            Vector3 screen = InputRouter.Reader.MousePosition;
            if (!InputRouter.IsUiPointerBlocked() && TryScreenToGround(camera, screen, out Vector2 ground))
            {
                unit = site.Kernel.PickUnit(new double2(ground.x, ground.y), PickRadius, true);
            }
            if (unit == 0)
            {
                Leave(site);
                return;
            }
            _hoveredSite = site;
            _hoveredUnit = unit;
            UiTooltip.HoverWorld(unit, new Vector2(screen.x, screen.y), Provider);
        }

        private static TooltipContent ProvideHovered()
        {
            CombatSite site = _hoveredSite;
            return site == null || site.IsDisposed || _hoveredUnit == 0 ? null : BuildContent(site.Kernel, _hoveredUnit);
        }

        /// <summary>离开（光标移开 / 地点不再被观察）。</summary>
        public static void Leave(CombatSite site)
        {
            if (_hoveredUnit != 0 && (site == null || site == _hoveredSite))
            {
                _hoveredUnit = 0;
                _hoveredSite = null;
                UiTooltip.LeaveWorld();
            }
        }

        /// <summary>一个单位的标签读数：标题 + 每个标签一行（形状 名称　剩余 N 秒　×层数）。单位没有标签返回 null。</summary>
        public static TooltipContent BuildContent(CombatKernel kernel, int unitId)
        {
            List<(string Glyph, string Name, float Seconds, int Stacks)> lines = LineBuffer;
            if (!Describe(kernel, unitId, lines) || lines.Count == 0)
            {
                return null;
            }
            StringBuilder sb = TextBuffer;
            sb.Clear();
            foreach ((string glyph, string name, float seconds, int stacks) in lines)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(GameText.Format("tag.hover.line", glyph, name, seconds.ToString("0.0", CultureInfo.InvariantCulture), stacks.ToString(CultureInfo.InvariantCulture)));
            }
            if (lines.Count > CombatRenderer.MaxIconsPerUnit)
            {
                sb.Append('\n').Append(GameText.Format("tag.hover.more", (lines.Count - CombatRenderer.MaxIconsPerUnit).ToString(CultureInfo.InvariantCulture)));
            }
            return new TooltipContent { Title = GameText.Get("tag.hover.title"), Body = sb.ToString() };
        }

        /// <summary>一个单位此刻身上的状态标签（按内核位序，与头顶图标同序）：形状、名称、剩余游戏秒、叠层。自检直接断言。</summary>
        public static List<(string Glyph, string Name, float Seconds, int Stacks)> Describe(CombatKernel kernel, int unitId)
        {
            var list = new List<(string, string, float, int)>();
            return Describe(kernel, unitId, list) ? list : null;
        }

        /// <summary>同上，写进调用方给的缓冲（先清空）；单位不存在 / 没有状态返回 false。</summary>
        public static bool Describe(CombatKernel kernel, int unitId, List<(string Glyph, string Name, float Seconds, int Stacks)> list)
        {
            list.Clear();
            if (kernel == null || kernel.IsDisposed || !kernel.TryGetStatus(unitId, out uint mask, out double until, out _, out _, out _))
            {
                return false;
            }
            uint bits = mask & ~CombatConst.StatusBitZoneSlow;
            float left = (float)System.Math.Max(0.0, until - kernel.Time);
            for (int b = 0; b < 31; b++)
            {
                if ((bits & (1u << b)) == 0u)
                {
                    continue;
                }
                string tag = NamedReactionCatalog.TagOfBit(b);
                string name = tag != null ? StatusTagCatalog.NameOf(tag) : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                list.Add((StatusTagCatalog.ShapeOf(tag) ?? string.Empty, name, left, System.Math.Max(1, kernel.StatusStacksOf(unitId, b))));
            }
            return true;
        }

        private static bool TryScreenToGround(Camera camera, Vector3 screen, out Vector2 ground)
        {
            ground = Vector2.zero;
            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = camera.ScreenPointToRay(screen);
            if (!plane.Raycast(ray, out float enter))
            {
                return false;
            }
            Vector3 hit = ray.GetPoint(enter);
            ground = new Vector2(hit.x, hit.z);
            return true;
        }
    }
}
