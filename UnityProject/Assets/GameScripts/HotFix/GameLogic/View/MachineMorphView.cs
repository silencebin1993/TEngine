using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG1-VFX-01 机身形变的世界表现（纯表现，不写任何游戏状态；FG02 FGR-FW-020～022）。
    ///
    /// ── 数据来源（FGR-FW-021）──
    /// 目标状态只读战斗桥接层与武器参数同一次解析得出的 <see cref="MachineWeaponInfo.Morph"/>（= 编译结果里生效固件的类别集合）：
    /// 接入 / 离开 / 装配变更 / 冷却起止时 <see cref="CombatSite.RefreshMachineWeapon"/> 发出 <see cref="CombatSite.MachineWeaponRefreshed"/>，
    /// 这里 O(1) 查到那台机器的表现并切换目标。阵亡（<see cref="MachineRegistry.MachineDied"/>）立即收起全部状态（残骸不带形变）。
    ///
    /// ── 过渡（FGR-FW-021，初值 0.3 秒）──
    /// 每类状态一个挂点组、一个进度（0 = 收起，1 = 完全展开），接入和离开都播放、完全可逆：进度从当前值朝新目标走，
    /// 快速反复接入离开时就地折返，不叠加、不重建部件。按真实时间计、不随倍速、战略暂停中不走（与接入过渡同一口径）。
    /// 每帧只推进“正在过渡”的机器（<see cref="AnimatingCount"/>，平时为 0），与机器总数无关。
    ///
    /// ── 生命周期 ──
    /// 机器表现随地点观察建立 / 销毁：<see cref="HomeValleyMachineMarker.AttachView"/> 时按当前状态**直接到位**（读档、镜头飞回来都不重播过渡），
    /// <see cref="HomeValleyMachineMarker.DetachView"/> 时登记随之删除（部件是表现对象的子节点，随它销毁）。整个世界卸载时 <see cref="Clear"/> 释放共享网格与材质。
    /// </summary>
    public static class MachineMorphView
    {
        public const string RigName = "MorphRig";

        /// <summary>占位标记（B22）：部件节点名都带它，调试层一眼可见“这是占位低模”。</summary>
        public const string PlaceholderTag = "[placeholder]";

        private static readonly string[] GroupNames = { "Limiter", "Fluid", "Electromagnetic" };

        private sealed class Entry
        {
            public int LogicId;
            public MachineView View;
            public CombatSite Site;
            public Transform Rig;
            public string Primary;
            public string Utility;
            public MorphMask Target;
            public readonly float[] Progress = new float[3];
            public readonly Transform[] Groups = new Transform[3];
            public bool Animating;
            public int PartCount;

            /// <summary>形变态挂点组里的信号光柱（没有作战组件时为 null）；只在信号接在这台机器上时显示。</summary>
            public Transform Beam;
            public bool Uplinked;
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>(32);
        private static readonly List<Entry> Animating = new List<Entry>(8);
        private static bool _hooked;

        /// <summary>自检：每次目标切换（不含直接到位）累计一次。</summary>
        public static int TransitionStarts { get; private set; }

        /// <summary>正在过渡的机器数（每帧开销与它成正比；平时为 0）。</summary>
        public static int AnimatingCount => Animating.Count;

        /// <summary>挂着形变登记的机器表现数。</summary>
        public static int ViewCount => Entries.Count;

        /// <summary>测试注入：替换过渡秒数（null = 读表）。</summary>
        public static float? TransitionSecondsOverrideForTests;

        private static float Duration => TransitionSecondsOverrideForTests ?? MachineMorph.TransitionSeconds;

        // ── 查询（自检 / 冒烟 / HUD 用）──

        public static bool IsRegistered(int logicId) => Entries.ContainsKey(logicId);

        /// <summary>这台机器表现的目标状态（没有表现时 None）。</summary>
        public static MorphMask TargetOf(int logicId) => Entries.TryGetValue(logicId, out Entry e) ? e.Target : MorphMask.None;

        /// <summary>此刻画面上看得见（进度 &gt; 0）的状态。</summary>
        public static MorphMask VisibleOf(int logicId)
        {
            if (!Entries.TryGetValue(logicId, out Entry e))
            {
                return MorphMask.None;
            }
            MorphMask m = MorphMask.None;
            for (int i = 0; i < 3; i++)
            {
                if (e.Progress[i] > 0f)
                {
                    m |= MachineMorph.Categories[i];
                }
            }
            return m;
        }

        /// <summary>某类状态的过渡进度（0～1）。</summary>
        public static float ProgressOf(int logicId, MorphMask category)
        {
            int i = MachineMorph.IndexOf(category);
            return i >= 0 && Entries.TryGetValue(logicId, out Entry e) ? e.Progress[i] : 0f;
        }

        /// <summary>某类状态的挂点组节点（没建过为 null）。</summary>
        public static Transform GroupOf(int logicId, MorphMask category)
        {
            int i = MachineMorph.IndexOf(category);
            return i >= 0 && Entries.TryGetValue(logicId, out Entry e) && e.Groups[i] != null ? e.Groups[i] : null;
        }

        /// <summary>这台机器的信号光柱此刻是否打开（随形变态挂点组一起展开 / 收起；没接入时关着）。</summary>
        public static bool SignalBeamShownOf(int logicId) =>
            Entries.TryGetValue(logicId, out Entry e) && e.Beam != null && e.Beam.gameObject.activeSelf;

        /// <summary>这台机器表现下已经建出的形变部件数（反复接入离开时不应增长）。</summary>
        public static int PartCountOf(int logicId) => Entries.TryGetValue(logicId, out Entry e) ? e.PartCount : 0;

        // ── 机器表现挂上 / 摘下 ──

        public static void OnViewAttached(HomeValleyMachineMarker marker)
        {
            if (marker?.View == null)
            {
                return;
            }
            EnsureHooks();
            if (Entries.TryGetValue(marker.LogicId, out Entry old))
            {
                Animating.Remove(old);
                Entries.Remove(marker.LogicId);
            }
            var e = new Entry { LogicId = marker.LogicId, View = marker.View, Site = marker.Site };
            Entries[marker.LogicId] = e;
            Apply(e, instant: true);
        }

        public static void OnViewDetached(int logicId)
        {
            if (Entries.TryGetValue(logicId, out Entry e))
            {
                Animating.Remove(e);
                Entries.Remove(logicId);
            }
        }

        // ── 事件 ──

        private static void EnsureHooks()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            CombatSite.MachineWeaponRefreshed += OnWeaponRefreshed;
            MachineRegistry.MachineDied += OnMachineDied;
        }

        private static void OnWeaponRefreshed(CombatSite site, int logicId)
        {
            if (Entries.TryGetValue(logicId, out Entry e) && e.Site == site)
            {
                Apply(e, instant: false);
            }
        }

        private static void OnMachineDied(int logicId)
        {
            if (Entries.TryGetValue(logicId, out Entry e))
            {
                Apply(e, instant: true); // 阵亡：当场收起（过渡中阵亡也一样），不留半展开的部件。
            }
        }

        /// <summary>按当前编译结果（与武器参数同一次解析）求目标状态。机器已阵亡、没有武器解析结果时为 None。</summary>
        private static MorphMask ResolveTarget(Entry e, out string primary, out string utility, out bool uplinked)
        {
            primary = null;
            utility = null;
            uplinked = false;
            if (e.Site == null || e.Site.IsDisposed || !e.Site.TryGetMachineWeapon(e.LogicId, out MachineWeaponInfo info))
            {
                return MorphMask.None;
            }
            primary = string.IsNullOrEmpty(info.PrimaryId) ? null : info.PrimaryId;
            utility = string.IsNullOrEmpty(info.UtilityId) ? null : info.UtilityId;
            if (!MachineRegistry.TryGetRecord(e.LogicId, out MachineRecord rec) || rec == null || !rec.IsAlive)
            {
                return MorphMask.None;
            }
            uplinked = info.Uplinked;
            return info.Morph;
        }

        private static void Apply(Entry e, bool instant)
        {
            if (e.View == null)
            {
                Animating.Remove(e);
                RemoveIfCurrent(e);
                return;
            }
            MorphMask target = ResolveTarget(e, out string primary, out string utility, out bool uplinked);
            if (e.Rig == null || primary != e.Primary || utility != e.Utility)
            {
                RebuildParts(e, primary, utility); // 组件换了（装配变更）：部件换成新组件的，进度保留。
            }
            bool changed = target != e.Target;
            e.Target = target;
            e.Uplinked = uplinked;
            ApplyBeam(e);
            if (!instant && changed && target != MorphMask.None)
            {
                // B14：第一次在玩家眼前“变身”（接入 / 离开 / 换装引起的过渡开始）时发钩子，引导内容在 FG15-UX-04；之后不重复。
                // 挂上表现时直接到位（AI 自带固件的常驻状态、读档、镜头飞回来）不算——那不是玩家的动作引起的，也可能根本不在镜头里。
                GuidanceHooks.Raise(GuidanceHooks.MorphFirstSeen);
            }
            if (instant)
            {
                for (int i = 0; i < 3; i++)
                {
                    e.Progress[i] = (target & MachineMorph.Categories[i]) != 0 ? 1f : 0f;
                    ApplyGroup(e, i);
                }
                if (e.Animating)
                {
                    e.Animating = false;
                    Animating.Remove(e);
                }
                return;
            }
            if (changed)
            {
                TransitionStarts++;
            }
            if (!e.Animating && NeedsAnimation(e))
            {
                e.Animating = true;
                Animating.Add(e);
            }
        }

        private static void ApplyBeam(Entry e)
        {
            if (e.Beam != null && e.Beam.gameObject.activeSelf != e.Uplinked)
            {
                e.Beam.gameObject.SetActive(e.Uplinked);
            }
        }

        private static void RemoveIfCurrent(Entry e)
        {
            if (Entries.TryGetValue(e.LogicId, out Entry cur) && cur == e)
            {
                Entries.Remove(e.LogicId);
            }
        }

        private static bool NeedsAnimation(Entry e)
        {
            for (int i = 0; i < 3; i++)
            {
                float goal = (e.Target & MachineMorph.Categories[i]) != 0 ? 1f : 0f;
                if (e.Progress[i] != goal)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 每帧 ──

        /// <summary>由 <c>WorldSimulation.Frame</c> 每帧调用（真实时间；战略暂停中不走）。O(正在过渡的机器数)。</summary>
        public static void FrameTick(float realDt)
        {
            if (Animating.Count == 0 || GameClock.Paused || realDt <= 0f)
            {
                return;
            }
            float duration = Duration;
            float step = duration <= 1e-4f ? 1f : realDt / duration;
            for (int k = Animating.Count - 1; k >= 0; k--)
            {
                Entry e = Animating[k];
                if (e.View == null || e.Rig == null)
                {
                    e.Animating = false;
                    Animating.RemoveAt(k);
                    RemoveIfCurrent(e);
                    continue;
                }
                bool done = true;
                for (int i = 0; i < 3; i++)
                {
                    float goal = (e.Target & MachineMorph.Categories[i]) != 0 ? 1f : 0f;
                    float p = Mathf.MoveTowards(e.Progress[i], goal, step);
                    if (Mathf.Abs(goal - p) < 1e-4f)
                    {
                        p = goal; // 浮点累加差一点点时直接落到端点，不留 0.9999999 的“永远差一口气”
                    }
                    if (p != e.Progress[i])
                    {
                        e.Progress[i] = p;
                        ApplyGroup(e, i);
                    }
                    done &= p == goal;
                }
                if (done)
                {
                    e.Animating = false;
                    Animating.RemoveAt(k);
                }
            }
        }

        private static void ApplyGroup(Entry e, int i)
        {
            Transform g = e.Groups[i];
            if (g == null)
            {
                return;
            }
            float p = e.Progress[i];
            bool show = p > 0f;
            if (g.gameObject.activeSelf != show)
            {
                g.gameObject.SetActive(show);
            }
            if (show)
            {
                float s = p * p * (3f - 2f * p); // smoothstep：展开有起落，不是线性伸缩
                g.localScale = Vector3.one * Mathf.Max(0.02f, s);
            }
        }

        // ── 部件 ──

        private static void RebuildParts(Entry e, string primary, string utility)
        {
            if (e.Rig != null)
            {
                UnityObjects.Release(e.Rig.gameObject);
            }
            e.Rig = null;
            e.Primary = primary;
            e.Utility = utility;
            e.PartCount = 0;
            e.Beam = null;
            for (int i = 0; i < 3; i++)
            {
                e.Groups[i] = null;
            }
            e.Rig = BuildRig(e.View.transform, primary, utility, out int parts, e.Groups);
            e.PartCount = parts;
            Transform limiterGroup = e.Groups[MachineMorph.IndexOf(MorphMask.Limiter)];
            e.Beam = limiterGroup != null ? limiterGroup.Find(MachineMorphLibrary.SignalBeamName) : null;
            ApplyBeam(e);
            for (int i = 0; i < 3; i++)
            {
                ApplyGroup(e, i);
            }
        }

        /// <summary>
        /// 在 <paramref name="host"/>（机器命中盒）下建形变挂点：三类状态各一组，组里是底盘件 + 主组件件 + 功能组件件（各自实体 / 发光）。
        /// 没有作战组件时不建任何部件。每个部件一个 GameObject、一个 MeshFilter、共享网格与共享材质（可实例化）。
        /// 新建的组默认收起（未激活）。证据截图工具也调这里（与游戏同一套部件）。
        /// </summary>
        public static Transform BuildRig(Transform host, string primary, string utility, out int partCount, Transform[] groupsOut = null)
        {
            partCount = 0;
            var rig = new GameObject(RigName).transform;
            rig.SetParent(host, false);
            bool any = MachineMorph.IsMorphComponent(primary) || MachineMorph.IsMorphComponent(utility);
            for (int i = 0; i < 3; i++)
            {
                MorphMask cat = MachineMorph.Categories[i];
                var group = new GameObject(GroupNames[i]).transform;
                group.SetParent(rig, false);
                group.gameObject.SetActive(false);
                if (groupsOut != null)
                {
                    groupsOut[i] = group;
                }
                if (!any)
                {
                    continue;
                }
                partCount += AddParts(group, MachineMorphLibrary.ChassisKey, cat);
                if (MachineMorph.IsMorphComponent(primary))
                {
                    partCount += AddParts(group, primary, cat);
                }
                if (MachineMorph.IsMorphComponent(utility))
                {
                    partCount += AddParts(group, utility, cat);
                }
            }
            return rig;
        }

        private static readonly MachineMorphLibrary.PartRole[] PartRoles =
        {
            MachineMorphLibrary.PartRole.Solid, MachineMorphLibrary.PartRole.Glow, MachineMorphLibrary.PartRole.Signal,
        };

        private static int AddParts(Transform group, string componentKey, MorphMask cat)
        {
            int n = 0;
            foreach (MachineMorphLibrary.PartRole role in PartRoles)
            {
                Mesh mesh = MachineMorphLibrary.MeshFor(componentKey, cat, role);
                if (mesh == null)
                {
                    continue;
                }
                var go = new GameObject($"{componentKey}.{role}{PlaceholderTag}");
                go.transform.SetParent(group, false);
                if (role == MachineMorphLibrary.PartRole.Signal)
                {
                    go.SetActive(false); // 信号光柱默认关着，由 ApplyBeam 按“信号是否接在这台机器上”打开
                }
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = MachineMorphLibrary.MaterialFor(cat, role);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // 美术规则 02 §4.1：单位不投实时阴影
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                n++;
            }
            return n;
        }

        /// <summary>整个世界卸载（回主菜单 / 读档）/ 自检之间：清掉登记，释放共享网格与材质（部件随机器表现对象一起销毁）。</summary>
        public static void Clear()
        {
            Entries.Clear();
            Animating.Clear();
            TransitionStarts = 0;
            MachineMorphLibrary.ReleaseAll();
        }

        public static void ResetForTests()
        {
            Clear();
            TransitionSecondsOverrideForTests = null;
        }
    }
}
