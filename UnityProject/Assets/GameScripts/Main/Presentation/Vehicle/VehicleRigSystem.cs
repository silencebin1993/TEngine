// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace BinGames.Presentation
{
    /// <summary>每台机器每帧输出的驱动信号（art-rules 19 号 §10.11 名称）：给 VEH-06 轮 / 履带、FG-ART-10 关节驱动器读。</summary>
    public struct VehicleRigSignals
    {
        /// <summary>move_speed：沿车头方向的速度（米 / 游戏秒，倒车为负）。</summary>
        public float MoveSpeed;
        /// <summary>turn_rate：车身偏航角速度（度 / 游戏秒，俯视顺时针为正 = 右转）。</summary>
        public float TurnRate;
        /// <summary>steer_angle：车体期望转向角（度，按轴距由角速度与速度换算，限 ±35°；俯视顺时针为正）。</summary>
        public float SteerAngle;
    }

    /// <summary>
    /// PL-VEH-02 机器表现 Rig 的注册表（静态，热更适配 VehicleRigAdapter 在 AttachView / DetachView 时登记 / 注销）。
    /// 每台机器两个节点（白模的 Silhouette 与 MorphRig；正式资产来了换成 p_hull），Rig 只原地写它们的 localRotation / localPosition（不改层级、不加子节点）。
    /// 动作由 <see cref="VehicleRigModule"/>（功能 pl.vehrig）每帧一个 Burst 作业算；功能关时这里照常登记（只存引用，不写变换）。
    /// 键 = 机器 LogicId；同一台机器在两个地点各挂一份表现时，后挂的覆盖先挂的，注销时只摘“自己那份”（同 MachineMorphView）。
    /// </summary>
    public static class VehicleRigSystem
    {
        internal struct Entry
        {
            public int Key;
            public object Owner;
            public CombatKernel Kernel;
            public int UnitId;
            public VehicleArchetype Archetype;
            public Transform A;
            public Transform B;
            public Vector3 BaseA;
            public Vector3 BaseB;
            public long SeenSteps;
        }

        internal static Entry[] Entries = new Entry[16];
        internal static RigInput[] Inputs = new RigInput[16];
        internal static RigState[] States = new RigState[16];
        internal static VehicleRigSignals[] Signals = new VehicleRigSignals[16];
        private static readonly Dictionary<int, int> IndexOfKey = new Dictionary<int, int>(64);
        // 注销时记下最后朝向（没装甲的机器停着时 Facing 不随移动更新），同一台再挂上时从这个朝向开始，镜头飞走再回来不跳成默认朝向
        private static readonly Dictionary<int, float> LastYaw = new Dictionary<int, float>(64);

        public static int Count { get; private set; }

        /// <summary>登记 / 注销 / 换节点时 +1（模块据此重建 TransformAccessArray）。</summary>
        public static int Version { get; private set; }

        public static bool IsRegistered(int key) => IndexOfKey.ContainsKey(key);

        /// <summary>登记一台机器。<paramref name="kernel"/> 为 null 时用 <see cref="SetSyntheticInput"/> 喂合成输入（自检 / Lab）。</summary>
        public static void Register(int key, object owner, CombatKernel kernel, int unitId, VehicleArchetype archetype, Transform a, Transform b)
        {
            if (a == null && b == null)
            {
                return;
            }
            if (IndexOfKey.TryGetValue(key, out int old))
            {
                RemoveAt(old);
            }
            if (Count == Entries.Length)
            {
                int n = Count * 2;
                System.Array.Resize(ref Entries, n);
                System.Array.Resize(ref Inputs, n);
                System.Array.Resize(ref States, n);
                System.Array.Resize(ref Signals, n);
            }
            int i = Count++;
            Entries[i] = new Entry
            {
                Key = key, Owner = owner, Kernel = kernel, UnitId = unitId, Archetype = archetype, A = a, B = b,
                BaseA = a != null ? a.localPosition : Vector3.zero, BaseB = b != null ? b.localPosition : Vector3.zero, SeenSteps = -1,
            };
            Inputs[i] = new RigInput { Archetype = (byte)archetype, Phase = Phase(key) };
            States[i] = LastYaw.TryGetValue(key, out float yaw) ? new RigState { SeedYaw = yaw, HasSeedYaw = 1 } : default;
            Signals[i] = default;
            IndexOfKey[key] = i;
            Version++;
        }

        /// <summary>注销。<paramref name="owner"/> 非 null 时只在登记的正是这份表现时才注销（别处新挂上的不动）。节点还在时复位成登记时的姿态。</summary>
        public static void Unregister(int key, object owner = null)
        {
            if (IndexOfKey.TryGetValue(key, out int i) && (owner == null || Entries[i].Owner == null || ReferenceEquals(Entries[i].Owner, owner)))
            {
                RemoveAt(i);
            }
        }

        /// <summary>某个节点被主线重建了（MorphRig 随装配变更重建）：换成新节点，O(1)。<paramref name="slot"/> 0 = A、1 = B。</summary>
        public static void ReplaceNode(int key, int slot, Transform t)
        {
            if (!IndexOfKey.TryGetValue(key, out int i))
            {
                return;
            }
            ref Entry e = ref Entries[i];
            if (slot == 0 ? e.A == t : e.B == t)
            {
                return;
            }
            Vector3 basePos = t != null ? t.localPosition : Vector3.zero;
            if (slot == 0)
            {
                e.A = t;
                e.BaseA = basePos;
            }
            else
            {
                e.B = t;
                e.BaseB = basePos;
            }
            Version++;
        }

        public static Transform NodeOf(int key, int slot) =>
            IndexOfKey.TryGetValue(key, out int i) ? (slot == 0 ? Entries[i].A : Entries[i].B) : null;

        /// <summary>读一台机器本帧的驱动信号（没登记 / 功能关时为 0）。</summary>
        public static bool TryGetSignals(int key, out VehicleRigSignals s)
        {
            if (IndexOfKey.TryGetValue(key, out int i))
            {
                s = Signals[i];
                return true;
            }
            s = default;
            return false;
        }

        /// <summary>当前车身偏航（度，俯视顺时针、0 = +Z）；没登记为 NaN。</summary>
        public static float YawDegOf(int key) => IndexOfKey.TryGetValue(key, out int i) ? math.degrees(States[i].Yaw) : float.NaN;

        public static float PitchDegOf(int key) => IndexOfKey.TryGetValue(key, out int i) ? math.degrees(States[i].Pitch) : float.NaN;

        public static float RollDegOf(int key) => IndexOfKey.TryGetValue(key, out int i) ? math.degrees(States[i].Roll) : float.NaN;

        /// <summary>
        /// 冲量通道（给 MORPH-01 下蹲、VEH-05 开火后挫）：在当前姿态上叠加俯仰 <paramref name="pitchDeg"/>（正 = 车头下沉）与上下 <paramref name="y"/> 米，
        /// 按 (1 − t/时长)² 在 <paramref name="duration"/> 游戏秒内回零；新冲量覆盖旧的。
        /// </summary>
        public static void AddImpulse(int key, float pitchDeg, float y, float duration)
        {
            if (!IndexOfKey.TryGetValue(key, out int i) || duration <= 0f)
            {
                return;
            }
            ref RigState s = ref States[i];
            s.ImpPitch = math.radians(pitchDeg);
            s.ImpY = y;
            s.ImpT = 0f;
            s.ImpDur = duration;
        }

        /// <summary>没有内核的登记（自检 / Lab）：直接给这一模拟步的上一步 / 本步位置与朝向。</summary>
        public static void SetSyntheticInput(int key, double2 prev, double2 pos, float2 facing, bool armored)
        {
            if (!IndexOfKey.TryGetValue(key, out int i))
            {
                return;
            }
            ref RigInput n = ref Inputs[i];
            n.Prev = prev;
            n.Pos = pos;
            n.Facing = facing;
            n.Armored = armored ? (byte)1 : (byte)0;
            n.Valid = 1;
        }

        /// <summary>摘掉节点已销毁或内核已释放的登记（重建时调；正常路径 DetachView 已成对注销，这里兜底视图被整棵销毁的情况）。</summary>
        public static int PurgeInvalid()
        {
            int removed = 0;
            for (int i = Count - 1; i >= 0; i--)
            {
                Entry e = Entries[i];
                if ((e.A == null && e.B == null) || (e.Kernel != null && e.Kernel.IsDisposed))
                {
                    RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>读档 / 新开局：清掉记下的朝向（不同战役同一 LogicId 不是同一台）。</summary>
        public static void ForgetYaw() => LastYaw.Clear();

        /// <summary>全部清空（自检收尾）。节点复位。</summary>
        public static void Clear()
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                RemoveAt(i);
            }
            LastYaw.Clear();
        }

        /// <summary>
        /// 把 Rig 写过的节点复位成登记时的姿态（功能关时调），并让动作状态下次从当前 sim 重新起步。
        /// <paramref name="keepSeed"/> = false（读档 / 新开局）时连朝向种子一起清掉：上一局同一 LogicId 的朝向不带进新一局。
        /// </summary>
        internal static void ResetAll(bool keepSeed = true)
        {
            for (int i = 0; i < Count; i++)
            {
                ResetNodes(i);
                float yaw = States[i].Init != 0 ? States[i].Yaw : States[i].SeedYaw;
                byte has = (byte)(keepSeed && (States[i].Init != 0 || States[i].HasSeedYaw != 0) ? 1 : 0);
                States[i] = new RigState { SeedYaw = yaw, HasSeedYaw = has };
                Signals[i] = default;
            }
        }

        // 只复位 Rig 写过的节点（Init 由动作作业在第一次写变换的同一步置位）：功能关 / 合主线时 Rig 从没跑过，摘下 / 重挂不碰 FG 节点的局部变换
        private static void ResetNodes(int i)
        {
            if (States[i].Init == 0)
            {
                return;
            }
            Entry e = Entries[i];
            if (e.A != null)
            {
                e.A.localRotation = Quaternion.identity;
                e.A.localPosition = e.BaseA;
            }
            if (e.B != null)
            {
                e.B.localRotation = Quaternion.identity;
                e.B.localPosition = e.BaseB;
            }
        }

        private static void RemoveAt(int i)
        {
            ResetNodes(i);
            int key = Entries[i].Key;
            if (States[i].Init != 0)
            {
                LastYaw[key] = States[i].Yaw;
            }
            IndexOfKey.Remove(key);
            int last = --Count;
            if (i != last)
            {
                Entries[i] = Entries[last];
                Inputs[i] = Inputs[last];
                States[i] = States[last];
                Signals[i] = Signals[last];
                IndexOfKey[Entries[i].Key] = i;
            }
            Entries[last] = default;
            Version++;
        }

        // (种子 = LogicId) 的纯函数相位，悬浮机不同步起伏
        private static float Phase(int key) => (math.hash(new int2(key, 0x5EED)) & 0xFFFF) / 65536f * 2f * math.PI;

        /// <summary>主线程每帧：内核走了新的模拟步才重读该台的位置 / 朝向（TryGetUnit，O(机器数)，在 AOT 里）。</summary>
        internal static void RefreshInputs()
        {
            for (int i = 0; i < Count; i++)
            {
                ref Entry e = ref Entries[i];
                CombatKernel k = e.Kernel;
                if (k == null)
                {
                    continue; // 合成输入
                }
                ref RigInput n = ref Inputs[i];
                if (k.IsDisposed)
                {
                    n.Valid = 0;
                    continue;
                }
                long steps = k.Steps;
                if (steps == e.SeenSteps && n.Valid != 0)
                {
                    continue;
                }
                e.SeenSteps = steps;
                if (!k.TryGetUnit(e.UnitId, out CombatUnitView v))
                {
                    n.Valid = 0;
                    continue;
                }
                n.Prev = v.PrevPosition;
                n.Pos = v.Position;
                n.Facing = v.Facing;
                // 带正面装甲的己方机器内核每步把装甲朝向对准移动方向（CombatLogic FaceMotion），画面直接跟它；其余按位置差
                n.Armored = (byte)(v.Kind == CombatUnitKind.Machine && k.ArmorOf(e.UnitId) > 0f ? 1 : 0);
                n.Valid = 1;
            }
        }
    }

    internal struct RigInput
    {
        public double2 Prev;
        public double2 Pos;
        public float2 Facing;
        public float Phase;
        public byte Armored;
        public byte Valid;
        public byte Archetype;
    }

    internal struct RigState
    {
        public double2 LastP;
        public float Yaw;
        public float TargetYaw;
        public float YawRate;
        public float Speed;
        public float Accel;
        public float Pitch;
        public float PitchVel;
        public float Roll;
        public float RollVel;
        public float ImpPitch;
        public float ImpY;
        public float ImpT;
        public float ImpDur;
        public float SeedYaw;
        public byte HasSeedYaw;
        public byte Init;
    }

    internal struct RigPose
    {
        public quaternion Rotation;
        public float3 Offset;
        public byte Valid;
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct VehicleRigDynamicsJob : IJobFor
    {
        [ReadOnly] public NativeArray<RigInput> Inputs;
        [ReadOnly] public NativeArray<VehicleRigProfile> Profiles;
        public NativeArray<RigState> States;
        [WriteOnly] public NativeArray<RigPose> Poses;
        [WriteOnly] public NativeArray<VehicleRigSignals> Signals;
        public float Dt;
        public float Alpha;
        public double Time;
        public float Amplitude;

        public void Execute(int i)
        {
            RigInput n = Inputs[i];
            RigState s = States[i];
            if (n.Valid == 0)
            {
                Poses[i] = default; // 单位不在了：不写变换，停在最后姿态
                Signals[i] = default;
                return;
            }
            VehicleRigProfile pr = Profiles[n.Archetype];
            double2 p = math.lerp(n.Prev, n.Pos, Alpha);
            bool turns = pr.turnRateDeg > 0f;
            float2 facing = n.Facing;
            bool hasFacing = n.Armored != 0 && math.lengthsq(facing) > 1e-6f;
            if (s.Init == 0 || Dt <= 0f)
            {
                Signals[i] = default;
                if (s.Init == 0)
                {
                    s = default;
                    RigState seed = States[i];
                    s.Yaw = !turns ? 0f : hasFacing ? math.atan2(facing.x, facing.y) : seed.HasSeedYaw != 0 ? seed.SeedYaw : 0f;
                    s.TargetYaw = s.Yaw;
                    s.LastP = p;
                    s.Init = 1;
                }
            }
            else
            {
                double2 dp = p - s.LastP;
                s.LastP = p;
                float2 v = (float2)(dp / Dt);
                if (math.lengthsq(dp) > 9.0 || Dt > 0.5f)
                {
                    v = float2.zero; // 传送 / 长停顿：不当成一次猛加速
                    s.Speed = 0f;
                    s.Accel = 0f;
                }
                float speed = math.length(v);
                if (turns)
                {
                    if (hasFacing)
                    {
                        s.TargetYaw = math.atan2(facing.x, facing.y);
                    }
                    else if (speed > pr.minTurnSpeed)
                    {
                        s.TargetYaw = math.atan2(v.x, v.y);
                    }
                }
                float maxStep = math.radians(pr.turnRateDeg) * Dt;
                float step = math.clamp(Wrap(s.TargetYaw - s.Yaw), -maxStep, maxStep);
                s.Yaw = Wrap(s.Yaw + step);
                s.YawRate = step / Dt;
                float rawA = (speed - s.Speed) / Dt;
                s.Speed = speed;
                float smooth = pr.accelSmoothSeconds > 1e-4f ? 1f - math.exp(-Dt / pr.accelSmoothSeconds) : 1f;
                s.Accel += (rawA - s.Accel) * smooth;
                float k = pr.pitchPerAccelDeg;
                float pitchTarget = s.Accel >= 0f
                    ? -math.min(k * s.Accel, pr.maxPitchDeg)
                    : math.min(k * math.max(0f, -s.Accel - pr.brakeDecel), pr.brakePitchDeg);
                float rollTarget = math.clamp(pr.rollGainDeg * s.YawRate * speed, -pr.maxRollDeg, pr.maxRollDeg);
                float omega = pr.settleSeconds > 1e-4f ? 4.74f / pr.settleSeconds : 0f; // 临界阻尼：ωt = 4.74 时偏差剩 5%
                Spring(ref s.Pitch, ref s.PitchVel, math.radians(pitchTarget * Amplitude), omega, Dt);
                Spring(ref s.Roll, ref s.RollVel, math.radians(rollTarget * Amplitude), omega, Dt);
                if (s.ImpDur > 0f)
                {
                    s.ImpT += Dt;
                    if (s.ImpT >= s.ImpDur)
                    {
                        s.ImpDur = 0f;
                    }
                }
                float fwdSpeed = math.dot(v, new float2(math.sin(s.Yaw), math.cos(s.Yaw)));
                float steer = math.degrees(math.atan2(s.YawRate * pr.wheelbase, math.max(math.abs(fwdSpeed), 0.5f)));
                Signals[i] = new VehicleRigSignals { MoveSpeed = fwdSpeed, TurnRate = math.degrees(s.YawRate), SteerAngle = math.clamp(steer, -35f, 35f) };
            }
            float env = s.ImpDur > 0f ? math.square(1f - s.ImpT / s.ImpDur) : 0f;
            float bob = pr.bobMeters * Amplitude * math.sin((float)(2.0 * math.PI_DBL * pr.bobHz * Time % (2.0 * math.PI_DBL)) + n.Phase);
            float pitch = s.Pitch + s.ImpPitch * env;
            quaternion r = math.mul(quaternion.RotateY(s.Yaw), math.mul(quaternion.RotateX(pitch), quaternion.RotateZ(s.Roll)));
            float3 pivot = new float3(0f, pr.pivotY, 0f);
            // 绕车身中心转时下沉的那一端（轮 / 履带）会插进地面：按接地面半长 / 半宽抬回去，等于绕着着地的那一端转（一阶近似，多抬不到 1 mm）
            float lift = pr.footHalfLength * math.abs(math.sin(pitch)) + pr.footHalfWidth * math.abs(math.sin(s.Roll));
            Poses[i] = new RigPose { Rotation = r, Offset = pivot - math.mul(r, pivot) + new float3(0f, bob + s.ImpY * env + lift, 0f), Valid = 1 };
            States[i] = s;
        }

        private static float Wrap(float a) => a - 2f * math.PI * math.floor((a + math.PI) / (2f * math.PI));

        // 临界阻尼弹簧的解析步（目标在 dt 内不变），任何 dt 都稳定
        private static void Spring(ref float x, ref float v, float target, float omega, float dt)
        {
            if (omega <= 0f)
            {
                x = target;
                v = 0f;
                return;
            }
            float y = x - target;
            float e = math.exp(-omega * dt);
            float c = v + omega * y;
            x = target + (y + c * dt) * e;
            v = (v - omega * c * dt) * e;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct VehicleRigWriteJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<RigPose> Poses;
        [ReadOnly] public NativeArray<float3> Bases;

        public void Execute(int index, TransformAccess t)
        {
            RigPose p = Poses[index >> 1];
            if (p.Valid == 0 || !t.isValid)
            {
                return;
            }
            t.localRotation = p.Rotation;
            t.localPosition = Bases[index] + p.Offset;
        }
    }

    /// <summary>
    /// PL-VEH-02 功能模块（pl.vehrig）：每帧读 <see cref="VehicleRigSystem"/> 的登记，Burst 算动作、并行写两个节点的局部变换。
    /// 时间：游戏时间（frame.SmoothGameSeconds 的差值 = 与单位插值位置同一条时间轴；暂停时不写变换、倍速按游戏时间走）。只读 sim，不回写。
    /// </summary>
    [PresentationTime(TimeDomain.Game)]
    public sealed class VehicleRigModule : IPresentationModule
    {
        private TransformAccessArray _nodes;
        private NativeArray<RigInput> _inputs;
        private NativeArray<RigState> _states;
        private NativeArray<RigPose> _poses;
        private NativeArray<VehicleRigSignals> _signals;
        private NativeArray<float3> _bases;
        private NativeArray<VehicleRigProfile> _profiles;
        private Transform[] _nodeList = new Transform[0];
        private int _seenVersion = -1;
        private int _seenProfiles = -1;
        private double _lastTime = double.NaN;
        private float _amplitude = 1f;
        // 注册表是静态的：同时只让一个实例工作，第二个实例启用后什么都不做（否则同一帧 dt 积两遍、两个实例写同一批节点）
        private static VehicleRigModule s_owner;

        public string Id => PresentationFeatureRegistry.VehRigId;

        /// <summary>这个实例是否正在驱动 Rig（同时启用多个实例时只有第一个为 true）。</summary>
        public bool IsOwner => ReferenceEquals(s_owner, this);

        /// <summary>自检：最近一帧算了几台。</summary>
        public int LastJobCount { get; private set; }

        public void OnEnable()
        {
            if (s_owner != null && !ReferenceEquals(s_owner, this))
            {
                Debug.LogWarning("[PL-VEH-02] 已有一个 VehicleRigModule 在工作，这个实例不驱动 Rig（采集 / 自检请复用同一实例）");
                return;
            }
            s_owner = this;
            _profiles = new NativeArray<VehicleRigProfile>(4, Allocator.Persistent);
            _seenVersion = -1;
            _seenProfiles = -1;
            _lastTime = double.NaN;
            VehicleRigSystem.ResetAll();
        }

        public void OnDisable()
        {
            if (!IsOwner)
            {
                return;
            }
            s_owner = null;
            VehicleRigSystem.ResetAll();
            if (_nodes.isCreated)
            {
                _nodes.Dispose();
            }
            Dispose(ref _inputs);
            Dispose(ref _states);
            Dispose(ref _poses);
            Dispose(ref _signals);
            Dispose(ref _bases);
            Dispose(ref _profiles);
            _nodeList = new Transform[0];
            LastJobCount = 0;
        }

        public void Rebuild(RebuildReason reason)
        {
            if (!IsOwner)
            {
                return;
            }
            if (reason == RebuildReason.Reload || reason == RebuildReason.SiteSwitch)
            {
                VehicleRigSystem.PurgeInvalid();
                if (reason == RebuildReason.Reload)
                {
                    VehicleRigSystem.ForgetYaw();
                }
                // 弹簧余量是 Transient；朝向下次从当前 sim 重算。读档时新局的机器可能已在同一帧挂上并从上一局的 LastYaw 取了种子，一并清掉
                VehicleRigSystem.ResetAll(reason != RebuildReason.Reload);
                _lastTime = double.NaN;
            }
            else if (reason == RebuildReason.Settings)
            {
                _seenProfiles = -1;
            }
        }

        public void Tick(in PresentationFrame frame)
        {
            int count = VehicleRigSystem.Count;
            LastJobCount = 0;
            if (!IsOwner)
            {
                return;
            }
            if (count == 0)
            {
                _lastTime = frame.SmoothGameSeconds;
                return;
            }
            if (_seenVersion != VehicleRigSystem.Version)
            {
                SyncNodes(count);
            }
            if (_seenProfiles != VehicleRigProfiles.Revision)
            {
                _seenProfiles = VehicleRigProfiles.Revision;
                VehicleRigProfileFile f = VehicleRigProfiles.Current;
                for (int a = 0; a < 4; a++)
                {
                    _profiles[a] = VehicleRigProfiles.Of(f, (VehicleArchetype)a);
                }
                _amplitude = f.amplitude;
            }
            VehicleRigSystem.RefreshInputs();
            double now = frame.SmoothGameSeconds;
            float dt = double.IsNaN(_lastTime) ? 0f : (float)(now - _lastTime);
            _lastTime = now;
            bool anyUninit = false;
            for (int i = 0; i < count && !anyUninit; i++)
            {
                anyUninit = VehicleRigSystem.States[i].Init == 0;
            }
            if (dt <= 0f && !anyUninit)
            {
                return; // 暂停 / 时间没走：变换保持不变
            }
            NativeArray<RigInput>.Copy(VehicleRigSystem.Inputs, 0, _inputs, 0, count);
            NativeArray<RigState>.Copy(VehicleRigSystem.States, 0, _states, 0, count);
            var dyn = new VehicleRigDynamicsJob
            {
                Inputs = _inputs, Profiles = _profiles, States = _states, Poses = _poses, Signals = _signals,
                Dt = math.max(0f, dt), Alpha = math.saturate(frame.StepAlpha), Time = now, Amplitude = _amplitude,
            };
            JobHandle h = dyn.Schedule(count, default);
            h = new VehicleRigWriteJob { Poses = _poses, Bases = _bases }.Schedule(_nodes, h);
            h.Complete();
            NativeArray<RigState>.Copy(_states, 0, VehicleRigSystem.States, 0, count);
            NativeArray<VehicleRigSignals>.Copy(_signals, 0, VehicleRigSystem.Signals, 0, count);
            LastJobCount = count;
        }

        // 登记变化时（挂上 / 摘下 / 换节点，不是每帧）：重排节点表，容量不够时按 2 倍扩
        private void SyncNodes(int count)
        {
            _seenVersion = VehicleRigSystem.Version;
            if (!_inputs.IsCreated || _inputs.Length < count)
            {
                int cap = math.max(16, math.ceilpow2(count));
                Dispose(ref _inputs);
                Dispose(ref _states);
                Dispose(ref _poses);
                Dispose(ref _signals);
                Dispose(ref _bases);
                _inputs = new NativeArray<RigInput>(cap, Allocator.Persistent);
                _states = new NativeArray<RigState>(cap, Allocator.Persistent);
                _poses = new NativeArray<RigPose>(cap, Allocator.Persistent);
                _signals = new NativeArray<VehicleRigSignals>(cap, Allocator.Persistent);
                _bases = new NativeArray<float3>(cap * 2, Allocator.Persistent);
            }
            if (_nodeList.Length != count * 2)
            {
                _nodeList = new Transform[count * 2];
            }
            for (int i = 0; i < count; i++)
            {
                VehicleRigSystem.Entry e = VehicleRigSystem.Entries[i];
                _nodeList[2 * i] = e.A;
                _nodeList[2 * i + 1] = e.B;
                _bases[2 * i] = e.BaseA;
                _bases[2 * i + 1] = e.BaseB;
            }
            if (!_nodes.isCreated)
            {
                _nodes = new TransformAccessArray(_nodeList);
            }
            else
            {
                _nodes.SetTransforms(_nodeList);
            }
        }

        private static void Dispose<T>(ref NativeArray<T> a) where T : struct
        {
            if (a.IsCreated)
            {
                a.Dispose();
            }
        }
    }
}
