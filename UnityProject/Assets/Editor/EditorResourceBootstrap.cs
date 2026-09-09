using System;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEditor;
using UnityEngine;
using YooAsset;

namespace BinGames.EditorTools
{
    /// <summary>
    /// Edit 模式（未进 Play）下把 TEngine <c>ResourceModule</c> + YooAsset 拉起到
    /// <see cref="EPlayMode.EditorSimulateMode"/>，让免 Play 的 UI 验收能真的加载资源。
    /// 起因见 ui-visual-overhaul/story-009：资源模块原本只在 Play 的 Procedure FSM
    /// （<c>ProcedureInitPackage</c>）里初始化，Edit 模式下任何走 YooAsset 的 UI 都起不来。
    ///
    /// 硬约束：纯 Editor-only（<c>Assets/Editor/</c> ⇒ Assembly-CSharp-Editor），不进热更层、
    /// 不进打包路径、不改 Procedure FSM 既有流程；不 <c>.Wait()/.Result</c> 阻塞等待
    /// （会死锁主线程），一律走 <see cref="EditorApplication.update"/> 泵 + <see cref="IsReady"/> 轮询。
    ///
    /// ── Edit 模式的两条硬限制（实测，不是猜） ──────────────────────────────
    /// ① YooAsset 的驱动器是 MonoBehaviour，Edit 模式不 Update ⇒ 本类自己泵
    ///    <c>YooAssets.Update()</c>（YooAsset 已 <c>InternalsVisibleTo("Assembly-CSharp-Editor")</c>）。
    /// ② <b><see cref="Time.frameCount"/> 在 Edit 模式是冻结的</b>（编辑器不在前台时播放循环根本不跑；
    ///    实测 168 次编辑器 tick 后 frameCount 恒为 3）。而 UniTask 的 <c>EnumeratorPromise</c>
    ///    （<c>await someOperation.ToUniTask()</c> 走的就是它）靠 <c>initialFrame == Time.frameCount</c>
    ///    判「同一帧跳过」⇒ 该判断恒真 ⇒ <b>任何基于 IEnumerator 的 ToUniTask await 永不完成</b>。
    ///    用 <see cref="ProbeEnumerator"/> 可当场复现（与 YooAsset 无关，纯自造枚举器也卡）。
    ///
    /// 因此本类：
    /// * 不调 <c>ResourceModule.InitPackage</c>（它内部 await ToUniTask，Edit 模式必卡死），
    ///   改为按框架自己的 EditorSimulateMode 分支同款参数直接 <c>InitializeAsync</c> + 轮询 <c>IsDone</c>。
    /// * 提供 <see cref="Prewarm"/>：用**同步** <c>LoadAsset</c> 把资源装进 ResourceModule 的资源池，
    ///   之后 <c>LoadAssetAsync</c> 命中池、只 <c>await UniTask.Yield()</c>（Yield 不依赖 frameCount，
    ///   Edit 模式可完成），从而在 Edit 模式下真的返回资源。
    ///   <b>未预热的冷加载在 Edit 模式仍会卡死</b>——这是框架层限制，见 story-009 报告。
    /// </summary>
    public static class EditorResourceBootstrap
    {
        public enum BootState
        {
            Idle,
            Running,
            Ready,
            Failed
        }

        private const string DriverObjectName = "[YooAssets]";
        private const string PackageName = "DefaultPackage";

        /// <summary>当前状态。域重载会重置为 <see cref="BootState.Idle"/>，重新 <see cref="Begin"/> 即可。</summary>
        public static BootState State { get; private set; } = BootState.Idle;

        /// <summary>失败原因（<see cref="BootState.Failed"/> 时有值）。</summary>
        public static string LastError { get; private set; }

        /// <summary>资源模块已就绪。</summary>
        public static bool IsReady => State == BootState.Ready;

        private static bool _pumpHooked;

        /// <summary>当前在等的那一步异步操作（初始化 → 请求版本 → 更新清单），null = 不在等。</summary>
        private static AsyncOperationBase _pending;

        private enum Step
        {
            None,
            InitPackage,
            RequestVersion,
            UpdateManifest
        }

        private static Step _step = Step.None;

        /// <summary>
        /// 启动（幂等、非阻塞）。返回 false 表示当前环境不该用本入口。
        /// 调用方轮询 <see cref="IsReady"/> 判断就绪，**不要**同步等待。
        /// </summary>
        public static bool Begin()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying)
            {
                LastError = "Play 模式由 ProcedureInitPackage 负责初始化，本入口只服务 Edit 模式。";
                return false;
            }

            if (State == BootState.Running || State == BootState.Ready)
            {
                return true;
            }

            LastError = null;
            HookPump();

            try
            {
                IResourceModule resource = ModuleSystem.GetModule<IResourceModule>();
                if (resource == null)
                {
                    throw new InvalidOperationException("ModuleSystem.GetModule<IResourceModule>() 返回 null。");
                }

                // 与 ResourceModuleDriver.Start() 同口径的最小必要配置（模拟模式不需要服务器地址）。
                resource.DefaultPackageName = PackageName;
                resource.PlayMode = EPlayMode.EditorSimulateMode;
                resource.EncryptionType = EncryptionType.None;
                resource.Milliseconds = 30;
                resource.AutoUnloadBundleWhenUnused = false;
                resource.DownloadingMaxNum = 10;
                resource.FailedTryAgain = 3;
                resource.UpdatableWhilePlaying = false;

                EnsureYooAssetsInitialized();

                // 走框架自己的 Initialize：它负责创建/登记默认包裹并挂上对象池
                // （_assetPool 就是在这里建的，跳过它 LoadAsset 会 NRE）。
                // 此时 YooAssets 已初始化，内部那句 YooAssets.Initialize() 只会打一条
                // "YooAssets is initialized !" 警告后返回，其余步骤照常执行。
                resource.Initialize();

                resource.AssetAutoReleaseInterval = 60f;
                resource.AssetCapacity = 64;
                resource.AssetExpireTime = 60f;
                resource.AssetPriority = 0;

                ResourcePackage package = YooAssets.GetPackage(PackageName);
                if (package.InitializeStatus == EOperationStatus.Succeed)
                {
                    // 已初始化过（例如上一次 Begin 只走到一半），直接从请求版本这步续上。
                    StartRequestVersion(package);
                    State = BootState.Running;
                    return true;
                }

                // ResourceModule.InitPackage 的 EditorSimulateMode 分支同款参数，
                // 但不经它的 await（见类注释②）。
                PackageInvokeBuildResult buildResult = EditorSimulateModeHelper.SimulateBuild(PackageName);
                EditorSimulateModeParameters parameters = new EditorSimulateModeParameters();
                parameters.EditorFileSystemParameters =
                    FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory);
                parameters.AutoUnloadBundleWhenUnused = false;

                _pending = package.InitializeAsync(parameters);
                _step = Step.InitPackage;
                State = BootState.Running;
                return true;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                State = BootState.Failed;
                return true;
            }
        }

        /// <summary>
        /// 同步预热一份资源到 ResourceModule 的资源池。预热后同一 location 的
        /// <c>LoadAssetAsync</c> 会命中池、在 Edit 模式下也能真的返回资源（见类注释）。
        /// </summary>
        public static UnityEngine.Object Prewarm(string location, Type assetType)
        {
            if (!IsReady)
            {
                LastError = "Prewarm 前资源模块尚未就绪。";
                return null;
            }

            return ModuleSystem.GetModule<IResourceModule>().LoadAsset(location, assetType);
        }

        [MenuItem("BinGames/资源模块/Edit 模式启动资源模块")]
        private static void BeginMenu()
        {
            if (!Begin())
            {
                Debug.LogWarning("[EditorResourceBootstrap] " + LastError);
            }
        }

        [MenuItem("BinGames/资源模块/关闭 Edit 模式资源模块")]
        public static void Shutdown()
        {
            UnhookPump();
            _pending = null;
            _step = Step.None;

            // 必须**先**收掉驱动器再 YooAssets.Destroy()：后者内部用的是 GameObject.Destroy，
            // Edit 模式下它会直接报 "Destroy may not be called from edit mode!"。
            // 先 DestroyImmediate 掉，YooAssets.Destroy() 里的 `_driver != null` 就不成立了。
            // 注意用 FindObjectsOfTypeAll 而不是 GameObject.Find：驱动器被打了
            // HideAndDontSave，GameObject.Find 找不到它（实测 Shutdown 后仍残留）。
            DestroyDriverImmediate();

            if (YooAssets.Initialized)
            {
                YooAssets.Destroy();
            }

            // YooAssets.Destroy() 里可能又 new 不出来但仍留旧引用；再扫一次兜底。
            DestroyDriverImmediate();

            // 丢掉 Edit 模式下建出来的模块实例，让下一次进 Play 从干净状态起。
            ModuleSystem.Shutdown();

            // GameModule 把各模块缓存在自己的静态字段里。只清 ModuleSystem 不清它，
            // 下一次 GameModule.Resource 拿到的是**上一轮那个已被丢弃的** ResourceModule
            // （资源池/加载中列表全是旧的），实测会让 LoadAssetAsync 永远挂着。
            GameModule.Shutdown();

            State = BootState.Idle;
            LastError = null;
        }

        /// <summary>一行诊断串，方便 execute_code 里直接回读。</summary>
        public static string Describe()
        {
            ResourcePackage package = YooAssets.Initialized ? YooAssets.TryGetPackage(PackageName) : null;
            return string.Format(
                "state={0} step={1} yoo={2} pkg={3} err={4}",
                State,
                _step,
                YooAssets.Initialized,
                package == null ? "null" : package.InitializeStatus.ToString(),
                string.IsNullOrEmpty(LastError) ? "-" : LastError);
        }

        /// <summary>
        /// <c>YooAssets.Initialize()</c> 内部 new 出驱动器后调 <c>Object.DontDestroyOnLoad</c>，
        /// Edit 模式下这一句**必抛** InvalidOperationException（"can only be used in play mode
        /// ... cannot be part of an editor script"）。抛出点在 <c>OperationSystem.Initialize()</c>
        /// 之前，而 <c>_isInitialize</c> / 驱动器已经就位，所以这里吞掉该异常并补齐被跳过的那一步
        /// ——否则异步操作系统的计时器为 null，所有异步操作永远不推进。
        /// </summary>
        private static void EnsureYooAssetsInitialized()
        {
            if (!YooAssets.Initialized)
            {
                try
                {
                    YooAssets.Initialize();
                }
                catch (InvalidOperationException)
                {
                    // 预期内：见方法注释。
                }

                if (!YooAssets.Initialized)
                {
                    throw new InvalidOperationException("YooAssets.Initialize() 未能置位 Initialized。");
                }

                // 驱动器是 new GameObject + DontDestroyOnLoad：Edit 模式下 DontDestroyOnLoad 无效，
                // 它会落进当前打开的场景并把场景标脏。打上 HideAndDontSave，既不入层级面板也不进场景文件。
                GameObject driver = GameObject.Find(DriverObjectName);
                if (driver != null)
                {
                    driver.hideFlags = HideFlags.HideAndDontSave;
                }
            }

            // 幂等（只是重置一只 Stopwatch）。放在条件外：上一次 Begin 失败时可能已经把
            // Initialized 置成 true 却没补过这一步，重试时必须还能补上。
            OperationSystem.Initialize();
        }

        /// <summary>收掉 YooAsset 驱动器（Edit 模式必须 DestroyImmediate）。</summary>
        private static void DestroyDriverImmediate()
        {
            YooAssetsDriver[] drivers = Resources.FindObjectsOfTypeAll<YooAssetsDriver>();
            for (int i = 0; i < drivers.Length; i++)
            {
                if (drivers[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(drivers[i].gameObject);
                }
            }
        }

        private static void HookPump()
        {
            if (_pumpHooked)
            {
                return;
            }
            EditorApplication.update += Pump;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _pumpHooked = true;
        }

        private static void UnhookPump()
        {
            if (!_pumpHooked)
            {
                return;
            }
            EditorApplication.update -= Pump;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            _pumpHooked = false;
        }

        /// <summary>替 Edit 模式下不会 Update 的 <c>YooAssetsDriver</c> 泵一帧异步操作系统。</summary>
        private static void Pump()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                return;
            }

            // 编辑器在前台时这一句能把播放循环推起来（Time.frameCount 才会动，
            // UniTask 的 EnumeratorPromise 才可能完成）；编辑器在后台时它无效——
            // 这正是 Edit 模式冷加载卡死的根因，见类注释②。
            EditorApplication.QueuePlayerLoopUpdate();

            if (YooAssets.Initialized)
            {
                YooAssets.Update();
            }

            AdvanceSteps();
        }

        /// <summary>
        /// 三步链（初始化包裹 → 请求资源清单版本 → 激活清单）的轮询推进。
        /// 少了后两步，<c>CheckLocationValid</c> 会直接抛 "Can not found active package manifest !"
        /// ——这正是 <c>ProcedureInitPackage</c> 之后 <c>ProcedureInitResources</c> 干的事。
        /// </summary>
        private static void AdvanceSteps()
        {
            if (State != BootState.Running || _pending == null || !_pending.IsDone)
            {
                return;
            }

            if (_pending.Status != EOperationStatus.Succeed)
            {
                LastError = _step + " 失败：" + _pending.Status + " / " + _pending.Error;
                State = BootState.Failed;
                _pending = null;
                _step = Step.None;
                return;
            }

            ResourcePackage package = YooAssets.GetPackage(PackageName);

            if (_step == Step.InitPackage)
            {
                StartRequestVersion(package);
                return;
            }

            if (_step == Step.RequestVersion)
            {
                RequestPackageVersionOperation versionOp = (RequestPackageVersionOperation)_pending;
                _pending = package.UpdatePackageManifestAsync(versionOp.PackageVersion);
                _step = Step.UpdateManifest;
                return;
            }

            // UpdateManifest 完成 = 清单已激活，资源可加载。
            _pending = null;
            _step = Step.None;
            State = BootState.Ready;
        }

        private static void StartRequestVersion(ResourcePackage package)
        {
            _pending = package.RequestPackageVersionAsync();
            _step = Step.RequestVersion;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // 进 Play 前把 Edit 模式这套拆掉，交还给 ProcedureInitPackage 的正规流程。
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                Shutdown();
            }
        }

        #region 诊断

        /// <summary>
        /// 决定性诊断：<c>await someEnumerator.ToUniTask()</c>（UniTask 的 EnumeratorPromise）
        /// 在 Edit 模式能否完成。与 YooAsset 无关——只用一个自造的三步枚举器。
        /// 实测结果：**永远停在 "await"**，因为 Time.frameCount 冻结。
        /// </summary>
        public static string EnumProbe { get; private set; } = "-";

        [MenuItem("BinGames/资源模块/诊断：Edit 模式 IEnumerator.ToUniTask 能否完成")]
        public static void ProbeEnumerator()
        {
            EnumProbe = "started";
            ProbeEnumeratorAsync().Forget();
        }

        private static async UniTaskVoid ProbeEnumeratorAsync()
        {
            EnumProbe = "await frame=" + Time.frameCount;
            await EnumeratorAsyncExtensions.ToUniTask(new CountdownEnumerator(3));
            EnumProbe = "done frame=" + Time.frameCount;
        }

        private sealed class CountdownEnumerator : System.Collections.IEnumerator
        {
            private int _left;
            public CountdownEnumerator(int steps) { _left = steps; }
            public bool MoveNext() { _left--; return _left > 0; }
            public void Reset() { }
            public object Current { get { return null; } }
        }

        #endregion
    }
}
