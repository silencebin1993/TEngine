using System;
using System.Collections.Generic;

namespace GameLogic.Core
{
    /// <summary>
    /// 模块容器。负责注册、按优先级更新、按类型解析。
    ///
    /// 这是"新增功能不改老代码"的落点之一：加一个系统 = Register 一行，
    /// 老模块完全不动。详见 DesignDocs/Game_Framework_Design.md §5.1。
    /// </summary>
    public sealed class ModuleHub
    {
        private readonly List<IGameModule> _ordered = new List<IGameModule>(32);
        private readonly Dictionary<Type, IGameModule> _byType = new Dictionary<Type, IGameModule>(32);
        private readonly List<IGameModule> _pendingInit = new List<IGameModule>(8);

        private bool _entered;
        private bool _dirty;

        public IReadOnlyList<IGameModule> Modules => _ordered;

        /// <summary>
        /// 注册模块。允许在 Enter 之后热插（新模块会立刻收到 OnInit + OnEnter）。
        /// </summary>
        public T Register<T>(T module) where T : IGameModule
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            Type t = module.GetType();
            if (_byType.ContainsKey(t))
            {
                TEngine.Log.Warning($"[ModuleHub] 模块 {t.Name} 已注册，忽略重复注册。");
                return module;
            }

            _byType[t] = module;
            _ordered.Add(module);
            _dirty = true;

            module.OnInit(this);
            if (_entered)
            {
                module.OnEnter();
            }
            else
            {
                _pendingInit.Add(module);
            }

            return module;
        }

        /// <summary>按类型解析。找不到返回 default，不抛异常——调用方自己决定是否必需。</summary>
        public T Get<T>() where T : class, IGameModule
        {
            if (_byType.TryGetValue(typeof(T), out IGameModule m))
            {
                return m as T;
            }
            // 支持按基类/接口查找
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (_ordered[i] is T hit)
                {
                    return hit;
                }
            }
            return null;
        }

        /// <summary>解析必需模块。缺失直接抛，暴露装配错误而不是留个 null 到处传。</summary>
        public T Require<T>() where T : class, IGameModule
        {
            T m = Get<T>();
            if (m == null)
            {
                throw new InvalidOperationException($"[ModuleHub] 必需模块 {typeof(T).Name} 未注册。");
            }
            return m;
        }

        public void Enter()
        {
            if (_entered)
            {
                return;
            }
            _entered = true;
            SortIfDirty();
            for (int i = 0; i < _pendingInit.Count; i++)
            {
                _pendingInit[i].OnEnter();
            }
            _pendingInit.Clear();
        }

        public void Update(float dt)
        {
            if (!_entered)
            {
                return;
            }
            SortIfDirty();
            for (int i = 0; i < _ordered.Count; i++)
            {
                _ordered[i].OnUpdate(dt);
            }
        }

        /// <summary>一局结束。逆序 Exit，让依赖方先收摊。</summary>
        public void Exit()
        {
            if (!_entered)
            {
                return;
            }
            for (int i = _ordered.Count - 1; i >= 0; i--)
            {
                _ordered[i].OnExit();
            }
            _entered = false;
        }

        public void Dispose()
        {
            Exit();
            for (int i = _ordered.Count - 1; i >= 0; i--)
            {
                _ordered[i].OnDispose();
            }
            _ordered.Clear();
            _byType.Clear();
            _pendingInit.Clear();
        }

        private void SortIfDirty()
        {
            if (!_dirty)
            {
                return;
            }
            _ordered.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
            _dirty = false;
        }
    }
}
