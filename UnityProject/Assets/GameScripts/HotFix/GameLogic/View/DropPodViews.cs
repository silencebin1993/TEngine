using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG8-DROP-01：在飞的投送舱的画面（纯表现，只在家园 / 星球表面被观察时由 <see cref="HomeValleyController"/> 每帧调用；只读投送域，不改任何模拟数据）。
    /// 舱是一个黄色的方块（占位几何体，B22；美术阶段替换），按航线插值的位置在空中飞，着陆的最后几秒降到地面；舱下方的地面画一条细线连到终点（看得出要落在哪里）。
    /// 开销：每帧 O(在飞的舱数)（舱数 ≤ 投送站数 × 8，与单位 / 物品数量无关）；没有在飞的舱时 O(1)（按投送服务版本号短路）。
    /// </summary>
    public static class DropPodViews
    {
        private const float CruiseHeight = 9f;

        private static readonly List<GameObject> Pods = new List<GameObject>(8);
        private static readonly List<LineRenderer> Trails = new List<LineRenderer>(8);
        private static readonly List<(Vector2 Pos, Vector2 To, double SecondsLeft)> Scratch = new List<(Vector2, Vector2, double)>(8);
        private static Transform _root;
        private static int _idleKey = int.MinValue;

        /// <summary>自检 / 冒烟读：画着的舱数。</summary>
        public static int VisibleCount { get; private set; }

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
            // 没有在飞的舱：服务版本号没变就什么都不做（O(1)）。
            if (VisibleCount == 0 && _idleKey == DropPodService.Revision)
            {
                return;
            }
            DropPodService.FlyingForWorld(state, Scratch);
            int used = 0;
            foreach ((Vector2 pos, Vector2 to, double left) in Scratch)
            {
                float h = left <= DropPodService.LandingSeconds ? Mathf.Lerp(0.6f, CruiseHeight, (float)(left / Math.Max(0.01f, DropPodService.LandingSeconds))) : CruiseHeight;
                GameObject pod = Pod(used);
                pod.transform.position = new Vector3(pos.x, h, pos.y);
                pod.SetActive(true);
                LineRenderer lr = Trail(used);
                lr.positionCount = 2;
                lr.SetPosition(0, new Vector3(pos.x, 0.15f, pos.y));
                lr.SetPosition(1, new Vector3(to.x, 0.15f, to.y));
                lr.enabled = true;
                used++;
            }
            for (int i = used; i < Pods.Count; i++)
            {
                Pods[i].SetActive(false);
                Trails[i].enabled = false;
            }
            Scratch.Clear();
            VisibleCount = used;
            _idleKey = used == 0 ? DropPodService.Revision : int.MinValue;
        }

        private static GameObject Pod(int index)
        {
            if (index < Pods.Count)
            {
                return Pods[index];
            }
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "DropPod" + index;
            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }
            go.transform.localScale = new Vector3(1.6f, 1.2f, 1.6f);
            Collider c = go.GetComponent<Collider>();
            if (c != null)
            {
                UnityEngine.Object.Destroy(c); // 纯表现：不挡点击射线
            }
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = ViewMaterials.Standard(new Color(0.95f, 0.78f, 0.25f));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Pods.Add(go);
            return go;
        }

        private static LineRenderer Trail(int index)
        {
            if (index < Trails.Count)
            {
                return Trails[index];
            }
            var go = new GameObject("DropPodTrail" + index);
            if (_root != null)
            {
                go.transform.SetParent(_root, false);
            }
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.15f;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.98f, 0.82f, 0.3f, 0.6f));
            Trails.Add(lr);
            return lr;
        }

        /// <summary>离开家园 / 自检收尾：对象随 root 销毁（这里清登记）。</summary>
        public static void Clear()
        {
            foreach (GameObject go in Pods)
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
            Pods.Clear();
            Trails.Clear();
            _root = null;
            _idleKey = int.MinValue;
            VisibleCount = 0;
        }
    }
}
