using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG0-ARCH-01：表现对象的统一销毁入口。Play 中走 <see cref="Object.Destroy(Object)"/>（帧末销毁，与原行为一致）；
    /// 编辑模式（batchmode 自检直接起真实区域控制器验证“整个世界同时运行”）走 DestroyImmediate——
    /// 编辑模式下调 Destroy 会报错且对象不会被销毁。
    /// </summary>
    public static class UnityObjects
    {
        public static void Release(Object obj)
        {
            if (obj == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(obj);
            }
            else
            {
                Object.DestroyImmediate(obj);
            }
        }
    }
}
