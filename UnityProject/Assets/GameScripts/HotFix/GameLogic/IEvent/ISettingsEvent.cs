using TEngine;

namespace GameLogic
{
    /// <summary>ER2-INPUT-01 AC-ACC-001：设置变更需要跨系统即时预览（UI 缩放、音量、色盲安全
    /// 图标等）。设置面板改值后广播本事件，订阅方各自按新值刷新，不由设置层直接持有其他系统的引用。</summary>
    [EventInterface(EEventGroup.GroupUI)]
    public interface ISettingsEvent
    {
        void OnSettingsChanged();
    }
}
