using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG12-EVT-06（FGR-EVT-050“从迷雾外来到家园附近”）：停在家园的流浪商队在主视图里的画面（纯表现，只在家园被观察时由
    /// <see cref="Campaign.Regions.HomeValleyController"/> 每帧调用；只读事件导演的实例记录，不改模拟）。
    /// - 占位外形（B22；美术阶段替换，DEBT-FG12EVT06-01）：一节沙色车厢 + 车头 + 一面绿色中立旗；到达后 arrive_seconds 内从迷雾外走到停靠点。
    /// - 没有碰撞体：不挡点选 / 右键射线、不进战斗内核（中立，炮塔与敌人选不到它）。
    /// - 左键点中商队 = 打开交易面板：这里不读键，由 HomeValleyController.HandleSelectionClick 的单点选中分支调 <see cref="TryClick"/>
    ///   （与机器 / 建筑点选共用同一次 Strategy 域、按键映射换算后的主动作点击；框选 / 武装命令确认 / 直控 / 炮塔瞄准 / 建造模式 / 模态面板都落不到这里）。
    /// 开销：每帧 O(进行中事件数) 一次扫描，商队在时常数次计算。
    /// </summary>
    public static class CaravanViews
    {
        private static readonly Color BodyColor = new Color(0.78f, 0.66f, 0.42f);
        private static readonly Color CabColor = new Color(0.45f, 0.38f, 0.28f);
        private static readonly Color FlagColor = new Color(0.25f, 0.85f, 0.45f);

        private static GameObject _body;
        private static GameObject _cab;
        private static GameObject _flag;
        private static Transform _root;

        /// <summary>自检 / 冒烟读：商队是否画着、画在哪、点中打开了几次。</summary>
        public static bool Visible { get; private set; }
        public static Vector3 Position { get; private set; }
        public static int ClickOpens { get; private set; }
        public static GameObject Body => _body;

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
            EventDirectorInstanceRecord r = CaravanService.Present(state);
            if (r == null)
            {
                SetActive(false);
                return;
            }
            Ensure();
            Vector2 p = CaravanService.ViewPosition(state, r);
            Vector2 stop = new Vector2(r.X, r.Y);
            Vector2 dir = stop - CaravanService.EntryPoint(state, r);
            float yaw = dir.sqrMagnitude > 1e-6f ? Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg : 0f;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var pos = new Vector3(p.x, 0f, p.y);
            _body.transform.SetPositionAndRotation(pos + Vector3.up * 0.8f, rot);
            _cab.transform.SetPositionAndRotation(pos + rot * new Vector3(0f, 0.6f, 1.7f), rot);
            _flag.transform.SetPositionAndRotation(pos + rot * new Vector3(0f, 2.3f, -0.8f), rot);
            SetActive(true);
            Position = pos;
        }

        /// <summary>FG12-EVT-06：单点选中分支传来的射线落在画着的商队上 = 打开交易面板并返回 true（调用方据此不再落到建筑 / 空地分支）。</summary>
        public static bool TryClick(Ray ray)
        {
            if (!Visible || !HitsCaravan(ray, Position))
            {
                return false;
            }
            ClickOpens++;
            GameLogic.UI.Kit.CaravanTradePanelUIToolkit.Open();
            return true;
        }

        /// <summary>射线到商队中心（离地 1 米）的最近距离 ≤ click_radius。</summary>
        public static bool HitsCaravan(Ray ray, Vector3 pos)
        {
            Vector3 c = pos + Vector3.up;
            Vector3 v = c - ray.origin;
            float t = Mathf.Max(0f, Vector3.Dot(v, ray.direction));
            return (ray.origin + ray.direction * t - c).sqrMagnitude <= CaravanService.ClickRadius * CaravanService.ClickRadius;
        }

        private static void Ensure()
        {
            if (_body != null)
            {
                return;
            }
            _body = Box("CaravanBody", new Vector3(1.8f, 1.6f, 3.2f), ViewMaterials.Standard(BodyColor));
            _cab = Box("CaravanCab", new Vector3(1.6f, 1.2f, 1.0f), ViewMaterials.Standard(CabColor));
            _flag = Box("CaravanFlag", new Vector3(0.05f, 0.7f, 1.0f), ViewMaterials.Standard(FlagColor));
        }

        private static GameObject Box(string name, Vector3 scale, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }
            go.transform.localScale = scale;
            Collider c = go.GetComponent<Collider>();
            if (c != null)
            {
                UnityObjects.Release(c); // 纯表现：不挡点选 / 右键射线
            }
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            go.SetActive(false);
            return go;
        }

        private static void SetActive(bool on)
        {
            Visible = on && _body != null;
            _body?.SetActive(on);
            _cab?.SetActive(on);
            _flag?.SetActive(on);
        }

        /// <summary>离开家园 / 自检收尾：对象随 root 销毁（这里清登记）。</summary>
        public static void Clear()
        {
            foreach (GameObject go in new[] { _body, _cab, _flag })
            {
                if (go != null)
                {
                    UnityObjects.Release(go);
                }
            }
            _body = _cab = _flag = null;
            _root = null;
            Visible = false;
        }
    }
}
