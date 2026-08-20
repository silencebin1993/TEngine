using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模地面/战场边界锚（story-004）。
    ///
    /// 纯静态的一次性遮挡面：局内 <see cref="ArenaHalfExtent"/> 不变，不需要跟随玩家
    /// 或做无限地形，故不套用 Presenter 三段式（同 <see cref="WhiteboxObstacleVisual"/>）。
    /// 一次性 <see cref="Spawn"/>，局末 <see cref="Dispose"/>。
    /// </summary>
    public static class WhiteboxGroundAnchor
    {
        private static readonly Color GroundColor = new Color(0.10f, 0.12f, 0.11f, 1f);

        private static GameObject _root;

        /// <summary>一次性生成战场地面锚。同局重复调用会先清理旧的。</summary>
        public static void Spawn(float arenaHalfExtent)
        {
            Dispose();

            _root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _root.name = "GroundAnchor";
            Object.Destroy(_root.GetComponent<Collider>());

            _root.transform.localScale = new Vector3(arenaHalfExtent * 2f, 1f, arenaHalfExtent * 2f);
            _root.transform.localPosition = new Vector3(0f, -0.5f, 0f);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            var mat = new Material(shader) { color = GroundColor };

            var mr = _root.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        public static void Dispose()
        {
            if (_root == null)
            {
                return;
            }

            var mr = _root.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                Object.Destroy(mr.sharedMaterial);
            }

            Object.Destroy(_root);
            _root = null;
        }
    }
}
