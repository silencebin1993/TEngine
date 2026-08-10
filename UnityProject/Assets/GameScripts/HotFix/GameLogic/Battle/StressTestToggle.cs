using TEngine;
using UnityEngine;

namespace GameLogic.Battle
{
    /// <summary>
    /// 让 <see cref="SimStressTest"/> 在正式局内也能用 F11 唤出，而不需要手动挂组件。
    ///
    /// <see cref="SimStressTest"/> 一旦激活就会常驻跑一份**独立于本局**的合成压测负载
    /// （见其文档），所以这里只负责「首次按 F11 时懒加载它」——激活后交由它自己的
    /// F11 分支循环切挡位，本组件不再插手，避免两边同时抢 F11 语义。
    /// 激活后不提供关闭入口：这是开发自查用的一次性 devtool，需要关掉就重进游戏。
    /// </summary>
    public sealed class StressTestToggle : MonoBehaviour
    {
        private bool _spawned;

        private void Update()
        {
            if (_spawned || !Input.GetKeyDown(KeyCode.F11))
            {
                return;
            }
            _spawned = true;

            var host = new GameObject("[SimStressTest]");
            host.transform.SetParent(transform, false);
            host.AddComponent<SimStressTest>();
            Log.Warning("[StressTestToggle] 已激活内核压力测试（独立合成负载，与本局无关，仅供性能自查）");
        }
    }
}
