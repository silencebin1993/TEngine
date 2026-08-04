using System.Collections.Generic;
using GameLogic.Core;

namespace GameLogic.Stage
{
    /// <summary>
    /// 阶段调度。只认 <see cref="IStageFlow"/>。
    ///
    /// 关键：后续七个阶段各自实现 IStageFlow 并 Register 一次，
    /// **本类永远不需要改动**。这是框架文档 §7 表格最后一行的落点。
    /// </summary>
    public sealed class StageDirector
    {
        private readonly Dictionary<StageId, IStageFlow> _flows = new Dictionary<StageId, IStageFlow>(8);

        private IStageFlow _current;
        private StageOutcome _lastOutcome;
        private StageId _pending = StageId.None;
        private bool _hasPending;

        public IStageFlow Current => _current;
        public StageId CurrentId => _current?.Id ?? StageId.None;
        public StageOutcome LastOutcome => _lastOutcome;

        public void Register(IStageFlow flow)
        {
            if (flow == null)
            {
                return;
            }
            if (_flows.ContainsKey(flow.Id))
            {
                TEngine.Log.Warning($"[StageDirector] 阶段 {flow.Id} 已注册，忽略。");
                return;
            }
            _flows[flow.Id] = flow;
        }

        public T Get<T>(StageId id) where T : class, IStageFlow
        {
            return _flows.TryGetValue(id, out IStageFlow f) ? f as T : null;
        }

        /// <summary>
        /// 请求切换阶段。真正切换发生在本帧 Update 之后，
        /// 避免阶段在自己的 Update 里销毁自身。
        /// </summary>
        public void GoTo(StageId id)
        {
            _pending = id;
            _hasPending = true;
        }

        public void Update(float dt)
        {
            _current?.Update(dt);

            if (!_hasPending)
            {
                return;
            }
            _hasPending = false;
            SwitchTo(_pending);
        }

        private void SwitchTo(StageId id)
        {
            if (_current != null)
            {
                _lastOutcome = _current.Exit();
                _current = null;
            }

            if (id == StageId.None)
            {
                return;
            }

            if (!_flows.TryGetValue(id, out IStageFlow next))
            {
                TEngine.Log.Error($"[StageDirector] 阶段 {id} 未注册，无法进入。");
                return;
            }

            _current = next;
            // 把上一阶段产物传给下一阶段——"连续继承"支柱的技术实现
            _current.Enter(_lastOutcome);
        }

        /// <summary>结束当前阶段并回到无阶段状态（回主菜单）。</summary>
        public void EndCurrent()
        {
            GoTo(StageId.None);
        }

        public void Dispose()
        {
            if (_current != null)
            {
                _lastOutcome = _current.Exit();
                _current = null;
            }
            _flows.Clear();
        }
    }
}
