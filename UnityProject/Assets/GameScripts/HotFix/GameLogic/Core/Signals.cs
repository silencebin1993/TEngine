using System;
using System.Collections.Generic;

namespace GameLogic.Core
{
    /// <summary>
    /// 局内信号总线。结构体信号 + 泛型订阅，零装箱。
    ///
    /// 三种事件机制的边界（框架文档 §5.2）：
    /// - <see cref="Signals"/>      —— 局内高频事件（吞噬、击杀、升级）
    /// - TEngine GameEvent          —— 跨模块持久事件
    /// - UIBase.AddUIEvent          —— UI 内部事件
    ///
    /// 局末必须 <see cref="Clear"/>，否则跨局残留订阅会造成 TEngine 事件泄漏的经典问题。
    /// </summary>
    public static class Signals
    {
        private abstract class Bucket
        {
            public abstract void Clear();
        }

        private sealed class Bucket<T> : Bucket where T : struct
        {
            private readonly List<Action<T>> _handlers = new List<Action<T>>(8);
            private readonly List<Action<T>> _pendingAdd = new List<Action<T>>(4);
            private readonly List<Action<T>> _pendingRemove = new List<Action<T>>(4);
            private int _depth;

            public void Add(Action<T> h)
            {
                if (h == null)
                {
                    return;
                }
                if (_depth > 0)
                {
                    _pendingAdd.Add(h);
                    return;
                }
                if (!_handlers.Contains(h))
                {
                    _handlers.Add(h);
                }
            }

            public void Remove(Action<T> h)
            {
                if (h == null)
                {
                    return;
                }
                if (_depth > 0)
                {
                    _pendingRemove.Add(h);
                    return;
                }
                _handlers.Remove(h);
            }

            public void Publish(in T signal)
            {
                _depth++;
                // 单个 handler 抛异常不应中断整条链——一张卡的 bug 不该让整局崩
                for (int i = 0; i < _handlers.Count; i++)
                {
                    try
                    {
                        _handlers[i].Invoke(signal);
                    }
                    catch (Exception e)
                    {
                        TEngine.Log.Error($"[Signals] {typeof(T).Name} handler 异常: {e}");
                    }
                }
                _depth--;

                if (_depth != 0)
                {
                    return;
                }
                for (int i = 0; i < _pendingRemove.Count; i++)
                {
                    _handlers.Remove(_pendingRemove[i]);
                }
                _pendingRemove.Clear();
                for (int i = 0; i < _pendingAdd.Count; i++)
                {
                    if (!_handlers.Contains(_pendingAdd[i]))
                    {
                        _handlers.Add(_pendingAdd[i]);
                    }
                }
                _pendingAdd.Clear();
            }

            public override void Clear()
            {
                _handlers.Clear();
                _pendingAdd.Clear();
                _pendingRemove.Clear();
                _depth = 0;
            }
        }

        private static readonly Dictionary<Type, Bucket> Buckets = new Dictionary<Type, Bucket>(32);

        private static Bucket<T> Of<T>() where T : struct
        {
            Type t = typeof(T);
            if (!Buckets.TryGetValue(t, out Bucket b))
            {
                b = new Bucket<T>();
                Buckets[t] = b;
            }
            return (Bucket<T>)b;
        }

        public static void Subscribe<T>(Action<T> handler) where T : struct
        {
            Of<T>().Add(handler);
        }

        public static void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            Of<T>().Remove(handler);
        }

        public static void Publish<T>(in T signal) where T : struct
        {
            Of<T>().Publish(signal);
        }

        /// <summary>局末调用。必须。</summary>
        public static void Clear()
        {
            foreach (var kv in Buckets)
            {
                kv.Value.Clear();
            }
        }
    }

    /// <summary>
    /// 订阅作用域。UI 窗口与临时系统用它自动退订，避免手写成对的 Unsubscribe。
    /// </summary>
    public sealed class SignalScope : IDisposable
    {
        private readonly List<Action> _unsubs = new List<Action>(8);

        public SignalScope On<T>(Action<T> handler) where T : struct
        {
            Signals.Subscribe(handler);
            _unsubs.Add(() => Signals.Unsubscribe(handler));
            return this;
        }

        public void Dispose()
        {
            for (int i = 0; i < _unsubs.Count; i++)
            {
                _unsubs[i].Invoke();
            }
            _unsubs.Clear();
        }
    }
}
