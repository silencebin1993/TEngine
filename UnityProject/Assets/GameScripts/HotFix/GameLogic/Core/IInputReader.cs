using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// 硬件键鼠输入的读取接口。<see cref="InputRouter"/> 是本仓库读取硬件输入的唯一入口，
    /// 这里只收敛它内部实际用到的最小方法集，不追求覆盖 <see cref="UnityEngine.Input"/> 全部 API。
    /// 生产实现见 <see cref="UnityInputReader"/>；测试用的按帧回放实现在 Editor 测试文件内
    /// （`CellFrameworkValidate.ScriptedInputReader`），不随生产代码一起热更。
    /// </summary>
    public interface IInputReader
    {
        bool GetKey(KeyCode key);
        bool GetKeyDown(KeyCode key);
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        Vector3 MousePosition { get; }
        float MouseScrollDelta { get; }
    }
}
