using System.Collections.Generic;
using GameLogic.Campaign.Combat;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG12-EVT-03（FGR-EVT-021“被夺控的机器有明显的故障特效和红色信号标记”）：被夺控机器的画面（纯表现，只在地点被观察时由各地点控制器每帧调用；只读内核夺控表，不改模拟）。
    /// - 红色信号标记：机身上方一枚旋转、脉动的红色菱形（瘫痪时变暗红、不再脉动；反向接入引导中闪青色）。
    /// - 故障特效：机身外一层半透明红色外壳，按帧不规律闪烁、轻微抖动（瘫痪时不闪）。
    /// 占位几何体（B22；美术阶段替换，登记 DEBT-FG12EVT03-01）。开销：每帧 O(本地点夺控数)；没有夺控时 O(1)。
    /// 信号劫持（玩家控制敌机）不在这里：它不是夺控记录。
    /// </summary>
    public static class SeizeViews
    {
        private const float MarkHeight = 2.4f;

        private static readonly Color MarkRed = new Color(1f, 0.12f, 0.1f);
        private static readonly Color MarkDowned = new Color(0.45f, 0.08f, 0.08f);
        private static readonly Color MarkReclaim = new Color(0.2f, 0.9f, 1f);
        private static readonly Color ShellRed = new Color(1f, 0.1f, 0.1f, 0.35f);

        private static readonly List<GameObject> Marks = new List<GameObject>(4);
        private static readonly List<GameObject> Shells = new List<GameObject>(4);
        private static Transform _root;

        /// <summary>自检 / 冒烟读：画着的标记数与第 i 个标记 / 外壳。</summary>
        public static int VisibleCount { get; private set; }
        public static GameObject MarkAt(int i) => i >= 0 && i < Marks.Count ? Marks[i] : null;
        public static GameObject ShellAt(int i) => i >= 0 && i < Shells.Count ? Shells[i] : null;

        public static void FrameUpdate(CombatSite site, Transform root)
        {
            if (root == null)
            {
                return;
            }
            if (!ReferenceEquals(root, _root))
            {
                Clear();
                _root = root;
            }
            int n = site?.SeizedCount ?? 0;
            if (n == 0 && VisibleCount == 0)
            {
                return;
            }
            float t = Time.unscaledTime;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                if (!site.TryGetSeizedView(i, out Transform view, out bool downed, out float reclaimLeft))
                {
                    continue;
                }
                Vector3 p = view.position;
                GameObject mark = Mark(used);
                float pulse = downed ? 1f : 1f + 0.25f * Mathf.Sin(t * 9f);
                mark.transform.position = p + Vector3.up * MarkHeight;
                mark.transform.rotation = Quaternion.Euler(45f, t * 120f, 45f);
                mark.transform.localScale = Vector3.one * (0.45f * pulse);
                Color c = downed ? MarkDowned : reclaimLeft > 0f && Mathf.Repeat(t * 6f, 1f) < 0.5f ? MarkReclaim : MarkRed;
                mark.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(c);
                mark.SetActive(true);
                GameObject shell = Shell(used);
                // 不规律闪烁：两路不同频率的方波叠加，约一半时间可见；位置每帧小幅抖动（与模拟无关，用真实时间）。
                // 设置“降低闪光”（AC-ACC-003）：外壳常亮、不抖动。
                bool calm = GameLogic.Settings.GameSettings.FlashReductionEnabled;
                bool on = !downed && (calm || Mathf.Sin(t * 37f + used) > -0.2f ^ Mathf.Sin(t * 13f) > 0.6f);
                shell.transform.position = p + (calm ? new Vector3(0f, 0.4f, 0f) : new Vector3(Mathf.Sin(t * 71f) * 0.06f, 0.4f, Mathf.Cos(t * 53f) * 0.06f));
                shell.SetActive(on);
                used++;
            }
            for (int i = used; i < Marks.Count; i++)
            {
                Marks[i].SetActive(false);
                Shells[i].SetActive(false);
            }
            VisibleCount = used;
        }

        private static GameObject Mark(int index)
        {
            if (index < Marks.Count)
            {
                return Marks[index];
            }
            GameObject go = Primitive(PrimitiveType.Cube, "SeizeMark" + index, ViewMaterials.Standard(MarkRed));
            Marks.Add(go);
            Shell(index);
            return go;
        }

        private static GameObject Shell(int index)
        {
            while (Shells.Count <= index)
            {
                GameObject go = Primitive(PrimitiveType.Cube, "SeizeShell" + Shells.Count, ViewMaterials.Get("Sprites/Default", ShellRed));
                go.transform.localScale = new Vector3(1.7f, 1.3f, 1.7f);
                go.SetActive(false);
                Shells.Add(go);
            }
            return Shells[index];
        }

        private static GameObject Primitive(PrimitiveType type, string name, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }
            Collider c = go.GetComponent<Collider>();
            if (c != null)
            {
                UnityObjects.Release(c); // 纯表现：不挡点选射线（被夺控的机器要能被右键攻击 / 选中反向接入）
            }
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>离开地点 / 自检收尾：对象随 root 销毁（这里清登记）。</summary>
        public static void Clear()
        {
            foreach (GameObject go in Marks)
            {
                if (go != null)
                {
                    UnityObjects.Release(go);
                }
            }
            foreach (GameObject go in Shells)
            {
                if (go != null)
                {
                    UnityObjects.Release(go);
                }
            }
            Marks.Clear();
            Shells.Clear();
            _root = null;
            VisibleCount = 0;
        }
    }
}
