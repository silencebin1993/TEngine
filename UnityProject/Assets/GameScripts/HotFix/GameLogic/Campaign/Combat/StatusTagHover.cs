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
        /// <summary>FG9-UX-01：当前悬停的是敌人时它的类型 ID（悬停读数写名字 / 弱点，按图鉴键跳到敌人条目）；不是敌人为 null。</summary>
        private static string _hoveredEnemyType;

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
            int drone = 0;
            int emgKey = 0;
            Vector3 screen = InputRouter.Reader.MousePosition;
            if (!InputRouter.IsUiPointerBlocked() && TryScreenToGround(camera, screen, out Vector2 ground))
            {
                unit = PickAt(site, new double2(ground.x, ground.y));
                drone = unit == 0 ? PickFlashDrone(site, new double2(ground.x, ground.y)) : 0;
                emgKey = unit == 0 && drone == 0 ? PickEmg(site, new double2(ground.x, ground.y)) : 0;
            }
            if (drone != 0)
            {
                HoverDrone(site, drone, new Vector2(screen.x, screen.y));
                return;
            }
            if (emgKey != 0)
            {
                HoverEmg(site, emgKey, new Vector2(screen.x, screen.y));
                return;
            }
            HoverPicked(site, unit, new Vector2(screen.x, screen.y));
        }

        // ── EMG1-CHAIN-01（FG19 FGR-EMG-181）：光标停在子弹药 / 破片 / 残骸上——“是什么、谁造成的、接下来会怎样”三段（EmgChainView）。拾取在内核（O(弹体数 + 残骸数)），
        //    这里只拼一件的文字；按稳定 Id 跟住同一件（下标会因压实变），悬停期间按提示的刷新间隔重取（剩余秒数在走）。──

        /// <summary>悬停键：子弹药 / 破片 = −(基数 + 弹体 Id)，残骸 = −(基数 + 残骸 Id)；与单位（&gt; 0）、无人机（−2000000…）和施工 / 管线（−731…）不重叠。</summary>
        public const int WreckHoverKeyBase = 3000000;
        public const int ProjHoverKeyBase = 100000000;

        private static int _hoveredEmg;
        private static readonly System.Func<TooltipContent> EmgProvider = ProvideEmg;

        public static int HoveredEmgKey => _hoveredEmg;

        /// <summary>地面点下的子弹药 / 破片（优先：小而快，只有贴近才算）或残骸，返回悬停键（没有 0）。</summary>
        public static int PickEmg(CombatSite site, double2 at)
        {
            if (site == null || site.IsDisposed)
            {
                return 0;
            }
            CombatKernel k = site.Kernel;
            int p = k.PickEmgProjectile(at, PickRadius);
            if (p >= 0 && k.TryGetProjectile(p, out CombatProjectile pr) && pr.Id > 0)
            {
                return -(ProjHoverKeyBase + pr.Id);
            }
            int w = k.PickWreck(at, 0.2f);
            return w >= 0 && k.TryGetWreck(w, out EmgWreck wr) ? -(WreckHoverKeyBase + wr.Id) : 0;
        }

        /// <summary>自检：光标停在地面点上（与 <see cref="Tick"/> 同一套单位 → 无人机 → 子弹药 / 残骸的拾取顺序）。返回涌现悬停键（0 = 没有）。</summary>
        public static int HoverEmgGroundForTests(CombatSite site, double2 at)
        {
            int unit = PickAt(site, at);
            int key = unit == 0 && PickFlashDrone(site, at) == 0 ? PickEmg(site, at) : 0;
            if (key == 0)
            {
                HoverPicked(site, unit, Vector2.zero);
                return 0;
            }
            HoverEmg(site, key, Vector2.zero);
            return key;
        }

        /// <summary>自检：当前涌现悬停读数。</summary>
        public static TooltipContent HoveredEmgContentForTests() => ProvideEmg();

        private static void HoverEmg(CombatSite site, int key, Vector2 screen)
        {
            _hoveredUnit = 0;
            _hoveredDrone = 0;
            _hoveredEnemyType = null;
            _hoveredSite = site;
            _hoveredEmg = key;
            UiTooltip.HoverWorld(key, screen, EmgProvider);
        }

        private static TooltipContent ProvideEmg()
        {
            CombatSite site = _hoveredSite;
            int key = _hoveredEmg;
            if (site == null || site.IsDisposed || key == 0)
            {
                return null;
            }
            CampaignState state = CampaignSession.Current;
            int id = -key;
            if (id >= ProjHoverKeyBase)
            {
                int i = site.Kernel.ProjectileIndexOf(id - ProjHoverKeyBase);
                return i >= 0 ? EmgChainView.ProjectileContent(site, state, i) : null;
            }
            int w = site.Kernel.WreckIndexOf(id - WreckHoverKeyBase);
            return w >= 0 ? EmgChainView.WreckContent(site, state, w) : null;
        }

        // ── EMG1-FLASH-01（FG19 FGR-EMG-036）：光标停在持有刷写程序的无人机 / 哨戒桩上——“刷写 3 / 4 条（带宽已满）”“离母机过远，暂停 1 条”，
        //    以及被截 / 暂停的是哪几条（原因 + 解决办法，与程序树预览同一套文本 EmgProgramTreeText.ReasonText）。拾取在内核（O(无人机数)），这里只拼一架的文字。──

        /// <summary>悬停键：−(基数 + 无人机稳定 Id)，与单位 ID（&gt; 0）和施工 / 管线等悬停键（−731…）不重叠。</summary>
        public const int DroneHoverKeyBase = 2000000;

        private static int _hoveredDrone;
        private static readonly System.Func<TooltipContent> DroneProvider = ProvideDrone;

        public static int HoveredDrone => _hoveredDrone;

        /// <summary>光标所在地面点下持有刷写程序的无人机 / 哨戒桩（稳定 Id；没有 0）。没有变体表的无人机（没有程序）不弹。</summary>
        public static int PickFlashDrone(CombatSite site, double2 at)
        {
            if (site == null || site.IsDisposed)
            {
                return 0;
            }
            int q = site.Kernel.PickDrone(at, PickRadius);
            return q >= 0 && site.Kernel.TryGetDroneFlash(q, out CombatDroneFlash f) ? f.Id : 0;
        }

        /// <summary>自检：光标停在地面点上（与 <see cref="Tick"/> 同一套无人机拾取）。返回悬停到的无人机稳定 Id（0 = 没有）。</summary>
        public static int HoverDroneGroundForTests(CombatSite site, double2 at)
        {
            int unit = PickAt(site, at);
            int drone = unit == 0 ? PickFlashDrone(site, at) : 0;
            if (drone == 0)
            {
                HoverPicked(site, unit, Vector2.zero);
                return 0;
            }
            HoverDrone(site, drone, Vector2.zero);
            return drone;
        }

        private static void HoverDrone(CombatSite site, int droneId, Vector2 screen)
        {
            if (_hoveredUnit != 0)
            {
                _hoveredUnit = 0;
                _hoveredEnemyType = null;
            }
            _hoveredSite = site;
            _hoveredEmg = 0;
            _hoveredDrone = droneId;
            UiTooltip.HoverWorld(-(DroneHoverKeyBase + droneId), screen, DroneProvider);
        }

        private static TooltipContent ProvideDrone()
        {
            CombatSite site = _hoveredSite;
            return site == null || site.IsDisposed || _hoveredDrone == 0 ? null : DroneContent(site, _hoveredDrone);
        }

        /// <summary>一架无人机 / 哨戒桩的刷写读数（自检直接断言）。找不到这架或它没有刷写程序返回 null。</summary>
        public static TooltipContent DroneContent(CombatSite site, int droneId)
        {
            CombatKernel k = site?.Kernel;
            if (k == null || k.IsDisposed)
            {
                return null;
            }
            return k.TryGetDroneFlash(k.DroneIndexOf(droneId), out CombatDroneFlash f) // 按 Id 找在内核（O(无人机数) 不在热更层）
                ? new TooltipContent { Title = GameText.Get("emg.flash.hover.title"), Body = FlashBody(site.FlashProgramOf(f.Weapon), f) }
                : null;
        }

        /// <summary>读数正文：持有条数（带宽已满时注明）、距离暂停、信号离开后的剥离，再逐条列出被截 / 暂停的指令（知道程序时）。</summary>
        public static string FlashBody(Emergence.EmgProgramResult program, in CombatDroneFlash f)
        {
            var sb = new StringBuilder();
            sb.Append(GameText.Format("emg.flash.hover.held", f.Held.ToString(CultureInfo.InvariantCulture), f.Length.ToString(CultureInfo.InvariantCulture)));
            if (f.Truncated > 0)
            {
                sb.Append(GameText.Get("emg.flash.hover.full"));
            }
            if (f.Pause > 0)
            {
                sb.Append('\n').Append(GameText.Format("emg.flash.hover.range", f.Pause.ToString(CultureInfo.InvariantCulture)));
            }
            if (f.Stripped)
            {
                sb.Append('\n').Append(GameText.Get("emg.flash.hover.stripped"));
            }
            List<Emergence.EmgProgramStep> steps = program != null ? Emergence.EmgProgramCompiler.FlashSteps(program, f.Stripped) : null;
            if (steps == null || steps.Count != f.Length)
            {
                return sb.ToString(); // 程序不在本次会话的结算记录里（例如读档后母机已换装配）：只给条数
            }
            for (int i = f.Held; i < f.Length; i++)
            {
                sb.Append('\n').Append(Emergence.EmgProgramTreeText.ReasonText(Emergence.EmgProgramTreeText.NoteFlashTruncated, Emergence.EmgProgramTreeText.StepName(steps[i])));
            }
            for (int i = f.Effective; i < f.Held; i++)
            {
                sb.Append('\n').Append(Emergence.EmgProgramTreeText.ReasonText(Emergence.EmgProgramTreeText.NoteFlashRange, Emergence.EmgProgramTreeText.StepName(steps[i])));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 光标所在地面点下要弹悬停读数的单位：先找带标签的；没有就找没标签的敌人（FG9-UX-01，FGR-UX-030 / 051；FGR-FAC-005：名字 / 职能 / 弱点 + 图鉴链接；
        /// 己方没标签的单位照旧不弹）。潜伏中 / 不可选中的敌人（静默潜伏、藏身的首领本体）看不见，不弹、不算遭遇。没有返回 0。
        /// </summary>
        public static int PickAt(CombatSite site, double2 at)
        {
            if (site == null || site.IsDisposed)
            {
                return 0;
            }
            int unit = site.Kernel.PickUnit(at, PickRadius, true, true);
            if (unit != 0)
            {
                return unit;
            }
            int any = site.Kernel.PickUnit(at, PickRadius, false, true);
            // EMG1-CHAIN-01（FG-GAP-171）：带程序的己方机器没标签时也弹（附程序行）；没程序的己方单位照旧不弹。
            return any != 0 && (site.TryGetEnemyOfUnit(any, out _) || HasProgram(site, any)) ? any : 0;
        }

        private static bool HasProgram(CombatSite site, int unit) =>
            site.TryGetMachineOfUnit(unit, out int logicId) && site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) && info.Program != null && info.Program.Steps.Count > 0;

        private static void AppendProgramLine(CombatSite site, int unit, ref TooltipContent content)
        {
            string line = EmgChainView.MachineProgramLine(site, unit);
            if (line == null)
            {
                return;
            }
            content ??= new TooltipContent { Title = EmgChainView.PartyName(site, CampaignSession.Current, unit) };
            content.Body = string.IsNullOrEmpty(content.Body) ? line : content.Body + "\n" + line;
        }

        /// <summary>自检：光标停在地面点 <paramref name="at"/> 上（与 <see cref="Tick"/> 同一套拾取与开始悬停，跳过相机换算）。返回悬停到的单位（0 = 没有）。</summary>
        public static int HoverGroundForTests(CombatSite site, double2 at)
        {
            int unit = PickAt(site, at);
            HoverPicked(site, unit, Vector2.zero);
            return unit;
        }

        private static void HoverPicked(CombatSite site, int unit, Vector2 screen)
        {
            if (unit == 0)
            {
                Leave(site);
                return;
            }
            _hoveredDrone = 0;
            _hoveredEmg = 0;
            if (unit != _hoveredUnit || site != _hoveredSite)
            {
                OnHoverStarted(site, unit);
            }
            _hoveredSite = site;
            _hoveredUnit = unit;
            UiTooltip.HoverWorld(unit, screen, Provider);
        }

        /// <summary>自检：当前悬停读数（没有悬停返回 null）。</summary>
        public static TooltipContent HoveredContentForTests() => ProvideHovered();

        private static TooltipContent ProvideHovered()
        {
            CombatSite site = _hoveredSite;
            if (site == null || site.IsDisposed || _hoveredUnit == 0)
            {
                return null;
            }
            TooltipContent tags = BuildContent(site.Kernel, _hoveredUnit);
            if (_hoveredEnemyType != null)
            {
                return EnemyContent(_hoveredEnemyType, tags);
            }
            AppendProgramLine(site, _hoveredUnit, ref tags); // EMG1-CHAIN-01（FG-GAP-171）
            return tags;
        }

        /// <summary>
        /// FG9-UX-01：开始悬停一个单位（换目标时一次，不按帧）：光标停在敌人身上 = 在战场上看到它，解锁敌人图鉴条目（首次遭遇）；
        /// 单位身上的状态标签解锁对应的状态标签条目。
        /// </summary>
        private static void OnHoverStarted(CombatSite site, int unit)
        {
            _hoveredEnemyType = null;
            if (site.TryGetEnemyOfUnit(unit, out string instanceId))
            {
                _hoveredEnemyType = site.EnemyTypeOf(CampaignSession.Current, instanceId);
                Progression.MechanicCodex.OnEnemyEncountered(_hoveredEnemyType);
            }
            if (site.Kernel.TryGetStatus(unit, out uint mask, out _, out _, out _, out _))
            {
                uint bits = mask & ~CombatConst.StatusBitZoneSlow;
                for (int b = 0; b < 31; b++)
                {
                    if ((bits & (1u << b)) != 0u)
                    {
                        Progression.MechanicCodex.OnStatusSeen(NamedReactionCatalog.TagOfBit(b));
                    }
                }
            }
        }

        /// <summary>敌人的悬停读数：名字为标题；职能、弱点（已解锁时；没解锁只写“按图鉴键查看”）+ 身上的标签；图鉴键跳到敌人条目。</summary>
        public static TooltipContent EnemyContent(string enemyTypeId, TooltipContent tags)
        {
            string id = Progression.MechanicCodex.EnemyEntryId(enemyTypeId);
            var sb = new StringBuilder();
            if (EnemyDefs.TryGet(enemyTypeId, out GameConfig.fg.MechEnemy row))
            {
                sb.Append(GameText.Format("codex.label.role", GameText.Get("enemy.role." + row.Role), GameText.Get("enemy.tier." + row.Tier)));
                if (Progression.MechanicCodex.IsUnlocked(id))
                {
                    sb.Append('\n').Append(GameText.Format("codex.label.weakness", GameText.Get(row.WeaknessKey)));
                }
            }
            if (tags != null && !string.IsNullOrEmpty(tags.Body))
            {
                sb.Append('\n').Append(tags.Body);
            }
            return new TooltipContent { Title = EnemyDefs.NameOf(enemyTypeId) ?? GameText.Get("tag.hover.title"), Body = sb.ToString(), CodexEntryId = id };
        }

        /// <summary>离开（光标移开 / 地点不再被观察）。</summary>
        public static void Leave(CombatSite site)
        {
            if ((_hoveredUnit != 0 || _hoveredDrone != 0 || _hoveredEmg != 0) && (site == null || site == _hoveredSite))
            {
                _hoveredUnit = 0;
                _hoveredDrone = 0;
                _hoveredEmg = 0;
                _hoveredSite = null;
                _hoveredEnemyType = null;
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
            // FG9-UX-01：按图鉴键跳到第一个标签的状态标签条目。
            return new TooltipContent { Title = GameText.Get("tag.hover.title"), Body = sb.ToString(), CodexEntryId = FirstTagEntry(kernel, unitId) };
        }

        private static string FirstTagEntry(CombatKernel kernel, int unitId)
        {
            if (!kernel.TryGetStatus(unitId, out uint mask, out _, out _, out _, out _))
            {
                return null;
            }
            uint bits = mask & ~CombatConst.StatusBitZoneSlow;
            for (int b = 0; b < 31; b++)
            {
                string tag = (bits & (1u << b)) != 0u ? NamedReactionCatalog.TagOfBit(b) : null;
                if (tag != null && StatusTagCatalog.IsStatus(tag))
                {
                    return Progression.MechanicCodex.StatusEntryId(tag);
                }
            }
            return null;
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
            _ = until;
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
                // FG2-FW-04（DEBT-FG2FW03-02）：每个标签显示它自己的剩余时间（逐位到期），不再是整组的。
                float left = (float)kernel.StatusBitSecondsLeft(unitId, b);
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
