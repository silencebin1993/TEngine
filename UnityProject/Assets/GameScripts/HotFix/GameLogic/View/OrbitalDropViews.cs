using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG12-EVT-04（FGR-EVT-030“天空出现火光轨迹，地面标出落点圈”）：预警中的轨道坠落在主视图里的画面（纯表现，只在家园 / 星球表面被观察时由
    /// <see cref="Campaign.Regions.HomeValleyController"/> 每帧调用；只读事件导演的实例记录，不改模拟）。
    /// - 落点圈：地面上一块半透明橙色圆盘（半径 = event.orbital_drop.radius），随撞击临近脉动加快。
    /// - 火光轨迹：一团橙黄色火球按预警进度从高空斜着落向落点，身后拖一条渐细的光带（LineRenderer）。
    /// 占位几何体（B22；美术阶段替换，DEBT-FG12EVT04-01）。开销：每帧 O(预警中的坠落数)；没有时 O(进行中事件数) 的一次扫描。
    /// </summary>
    public static class OrbitalDropViews
    {
        private const float SkyHeight = 90f;
        private const float SkyOffset = 70f;

        private static readonly Color DiskColor = new Color(1f, 0.45f, 0.08f, 0.32f);
        private static readonly Color BallColor = new Color(1f, 0.72f, 0.2f);
        private static readonly Color TrailColor = new Color(1f, 0.55f, 0.15f, 0.7f);

        private static readonly List<GameObject> Disks = new List<GameObject>(2);
        private static readonly List<GameObject> Balls = new List<GameObject>(2);
        private static readonly List<LineRenderer> Trails = new List<LineRenderer>(2);
        private static readonly List<EventDirectorInstanceRecord> Scratch = new List<EventDirectorInstanceRecord>(2);
        private static Transform _root;

        /// <summary>自检 / 冒烟读：画着的落点圈数与第 i 个的圆盘 / 火球。</summary>
        public static int VisibleCount { get; private set; }
        public static GameObject DiskAt(int i) => i >= 0 && i < Disks.Count ? Disks[i] : null;
        public static GameObject BallAt(int i) => i >= 0 && i < Balls.Count ? Balls[i] : null;

        public static void FrameUpdate(CampaignState state, Transform root)
        {
            if (state == null || root == null)
            {
                return;
            }
            if (!ReferenceEquals(root, _root))
            {
                Clear();
                _root = root;
            }
            OrbitalDropService.Warned(state, Scratch);
            float radius = OrbitalDropService.Radius;
            bool calm = GameLogic.Settings.GameSettings.FlashReductionEnabled;
            int used = 0;
            foreach (EventDirectorInstanceRecord r in Scratch)
            {
                float k = OrbitalDropService.Progress(r);
                var ground = new Vector3(r.X, 0.06f, r.Y);
                GameObject disk = Disk(used);
                float pulse = calm ? 1f : 1f + 0.06f * Mathf.Sin(Time.unscaledTime * Mathf.Lerp(3f, 14f, k));
                disk.transform.position = ground;
                disk.transform.localScale = new Vector3(radius * 2f * pulse, 0.02f, radius * 2f * pulse);
                disk.SetActive(true);
                // 火球从高空（落点的西北上方）沿直线落下，越接近撞击越快（进度平方）。
                var sky = new Vector3(r.X - SkyOffset, SkyHeight, r.Y + SkyOffset * 0.5f);
                Vector3 ball = Vector3.Lerp(sky, ground, k * k);
                GameObject b = Ball(used);
                b.transform.position = ball;
                b.transform.localScale = Vector3.one * Mathf.Lerp(1.4f, 2.6f, k);
                b.SetActive(true);
                LineRenderer lr = Trail(used);
                lr.positionCount = 2;
                lr.SetPosition(0, Vector3.Lerp(sky, ball, 0.55f));
                lr.SetPosition(1, ball);
                lr.enabled = true;
                used++;
            }
            for (int i = used; i < Disks.Count; i++)
            {
                Disks[i].SetActive(false);
                Balls[i].SetActive(false);
                Trails[i].enabled = false;
            }
            Scratch.Clear();
            VisibleCount = used;
        }

        private static GameObject Disk(int index)
        {
            Ensure(index);
            return Disks[index];
        }

        private static GameObject Ball(int index)
        {
            Ensure(index);
            return Balls[index];
        }

        private static LineRenderer Trail(int index)
        {
            Ensure(index);
            return Trails[index];
        }

        private static void Ensure(int index)
        {
            while (Disks.Count <= index)
            {
                int n = Disks.Count;
                Disks.Add(Primitive(PrimitiveType.Cylinder, "OrbitalDropDisk" + n, ViewMaterials.Get("Sprites/Default", DiskColor)));
                Balls.Add(Primitive(PrimitiveType.Sphere, "OrbitalDropBall" + n, ViewMaterials.Standard(BallColor)));
                var go = new GameObject("OrbitalDropTrail" + n);
                if (_root != null)
                {
                    go.transform.SetParent(_root, false);
                }
                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.startWidth = 0.2f;
                lr.endWidth = 1.6f;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.sharedMaterial = ViewMaterials.Get("Sprites/Default", TrailColor);
                lr.enabled = false;
                Trails.Add(lr);
            }
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
                UnityObjects.Release(c); // 纯表现：不挡点选 / 右键射线（落点圈里还要能下移动命令）
            }
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go;
        }

        /// <summary>离开家园 / 自检收尾：对象随 root 销毁（这里清登记）。</summary>
        public static void Clear()
        {
            foreach (GameObject go in Disks)
            {
                if (go != null)
                {
                    UnityObjects.Release(go);
                }
            }
            foreach (GameObject go in Balls)
            {
                if (go != null)
                {
                    UnityObjects.Release(go);
                }
            }
            foreach (LineRenderer lr in Trails)
            {
                if (lr != null)
                {
                    UnityObjects.Release(lr.gameObject);
                }
            }
            Disks.Clear();
            Balls.Clear();
            Trails.Clear();
            _root = null;
            VisibleCount = 0;
        }
    }
}
