using System;
using System.Collections.Generic;

namespace BinGames.Sim.Logistics
{
    /// <summary>一个用电实体这一刻的供电结果。</summary>
    public enum PowerUse : byte
    {
        /// <summary>不用电（或没有在运转）。</summary>
        None = 0,
        Powered = 1,
        /// <summary>所在电网供电不足，按优先级被断电。</summary>
        Brownout = 2,
        /// <summary>不在任何电力节点的覆盖里（没有接入电网）。</summary>
        Unconnected = 3,
    }

    /// <summary>
    /// 电网内核的一个实体（热更层每次拓扑变化时按建筑记录组装，一座建筑一行）。
    /// 占地用包围盒（格，含两端）；节点（电塔 / 归还核心）的中心 = 包围盒中心。
    /// </summary>
    public struct PowerEntity
    {
        /// <summary>热更层分配的稳定键（本局内同一建筑不变；-1 = 没有建筑记录的虚拟配电中心）。</summary>
        public int Key;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        /// <summary>电力覆盖半径（格）。&gt; 0 = 电力节点。</summary>
        public float NodeRadius;
        /// <summary>节点此刻是否导电（运转中）。不导电的节点既不覆盖别人、也不和别的节点相连。</summary>
        public bool Conducts;
        public float Supply;
        public bool SupplyOn;
        public float Demand;
        public bool DemandOn;
        /// <summary>优先级 1～4（只用于读数；分配顺序 = 实体在输入数组里的顺序，热更层按“优先级 → 建筑编号”排好）。</summary>
        public int Priority;
        /// <summary>储能容量（电·秒）；0 = 不储能。</summary>
        public double StorageCapacity;
        /// <summary>最大充放电功率（电）。</summary>
        public float StorageRate;
        public bool StorageOn;

        // ── FG4-ECO-04 能源扩展（默认值 = FG3-LOG-06 的行为：一种发电、储能随充随放、给所有建筑放电）──
        /// <summary>发电类别（0～<see cref="PowerKernel.MaxSourceClasses"/>−1，热更层按 fg.TbPowerSource 分配）：曲线按类别分开记；
        /// 类别系数（<see cref="PowerKernel.SetClassFactor"/>，太阳能的昼夜 / 天气）乘在 <see cref="Supply"/> 上。</summary>
        public byte SourceClass;
        /// <summary>按负荷出力（燃油发电机）：电网先用不按负荷的发电（核心、发电机、太阳能），缺口和储能充电才由它补；
        /// 它这一刻的负荷比例见 <see cref="PowerSubnetInfo.DispatchLoad"/>（热更层按负荷烧燃油）。</summary>
        public bool Dispatchable;
        /// <summary>储能：玩家关掉了充电。</summary>
        public bool StorageNoCharge;
        /// <summary>储能：玩家关掉了放电。</summary>
        public bool StorageNoDischarge;
        /// <summary>储能只给优先级 ≤ 这个数（1～3）的用电建筑放电，留着电给高优先级建筑；0 = 给所有建筑放电。</summary>
        public byte StorageReserve;
    }

    /// <summary>一个电网（互相连通的一组节点及其覆盖的建筑）的汇总。</summary>
    public struct PowerSubnetInfo
    {
        /// <summary>玩家看到的电网编号（跨拓扑变化尽量保持：多数节点原来属于哪个电网就沿用哪个编号）。</summary>
        public int Serial;
        public float Supply;
        /// <summary>需要（全部运转中用电建筑的需求之和）。</summary>
        public float Demand;
        /// <summary>实际供上的用电。</summary>
        public float Delivered;
        /// <summary>储能此刻的功率：+ 充电，− 放电（电）。</summary>
        public float StorageFlow;
        /// <summary>储能容量 / 存量（电·秒）。</summary>
        public double StorageCapacity;
        public double Stored;
        public float StorageRate;
        public int Nodes;
        public int Consumers;
        public int Brownouts;
        public int Producers;
        public int StorageUnits;
        /// <summary>这个电网里下标最小的节点（热更层用它定位 / 做存档锚点）。</summary>
        public int FirstNode;
        /// <summary>FG4-ECO-04：按负荷出力的发电（燃油发电机）能发多少（电）。</summary>
        public float DispatchSupply;
        /// <summary>FG4-ECO-04：按负荷出力的发电这一刻的负荷比例（0～1）：（实际供上的用电 + 储能充电 − 不按负荷的发电）÷ <see cref="DispatchSupply"/>。</summary>
        public float DispatchLoad;
        /// <summary>FG4-ECO-04：这一秒储能最多能放出多少（电；不含关掉放电的储能）。</summary>
        public float StorageDischargeAvailable;
        /// <summary>FG4-ECO-04：这一秒储能最多能充进多少（电；不含关掉充电的储能）。</summary>
        public float StorageChargeAvailable;
        /// <summary>FG4-ECO-04：因为储能留给高优先级（放电对象设置）而没能从储能取电、停机的建筑数。</summary>
        public int ReserveHeld;
    }

    /// <summary>一次拓扑变化里“一个电网断成几段”的记录（热更层据此发警告）。</summary>
    public readonly struct PowerSplit
    {
        public readonly int OldSerial;
        public readonly int Parts;
        /// <summary>断开后各段的电网编号（第一个沿用原编号）。</summary>
        public readonly int[] NewSerials;

        public PowerSplit(int oldSerial, int parts, int[] newSerials)
        {
            OldSerial = oldSerial;
            Parts = parts;
            NewSerials = newSerials;
        }
    }

    /// <summary>一个电网的曲线（环形缓冲，最旧 → 最新）。发电、需要、实际用电（电），储能（电·分钟）；
    /// FG4-ECO-04 起另记每个发电类别的发电（电，<see cref="PowerKernel.MaxSourceClasses"/> 类，“各类发电设施在电力曲线里分开显示”）。</summary>
    public sealed class PowerCurve
    {
        public readonly int Capacity;
        private readonly float[] _supply;
        private readonly float[] _demand;
        private readonly float[] _delivered;
        private readonly float[] _stored;
        private readonly float[] _classes;
        private int _head;

        public int Count { get; private set; }

        public PowerCurve(int capacity)
        {
            Capacity = Math.Max(2, capacity);
            _supply = new float[Capacity];
            _demand = new float[Capacity];
            _delivered = new float[Capacity];
            _stored = new float[Capacity];
            _classes = new float[Capacity * PowerKernel.MaxSourceClasses];
        }

        public void Add(float supply, float demand, float delivered, float storedMinutes) => Add(supply, demand, delivered, storedMinutes, null, 0);

        /// <summary>记一个点；<paramref name="classes"/> 从 <paramref name="offset"/> 起 <see cref="PowerKernel.MaxSourceClasses"/> 个数 = 各类别的发电（null = 全 0）。</summary>
        public void Add(float supply, float demand, float delivered, float storedMinutes, float[] classes, int offset)
        {
            _supply[_head] = supply;
            _demand[_head] = demand;
            _delivered[_head] = delivered;
            _stored[_head] = storedMinutes;
            int m = PowerKernel.MaxSourceClasses;
            for (int c = 0; c < m; c++)
            {
                _classes[_head * m + c] = classes != null && offset + c < classes.Length ? classes[offset + c] : 0f;
            }
            _head = (_head + 1) % Capacity;
            if (Count < Capacity)
            {
                Count++;
            }
        }

        private int At(int i) => (_head - Count + i + Capacity * 2) % Capacity;

        /// <summary>第 i 个点（0 = 最旧）。</summary>
        public void Get(int i, out float supply, out float demand, out float delivered, out float storedMinutes)
        {
            int at = At(i);
            supply = _supply[at];
            demand = _demand[at];
            delivered = _delivered[at];
            storedMinutes = _stored[at];
        }

        /// <summary>第 i 个点里第 <paramref name="sourceClass"/> 类发电（电）。</summary>
        public float GetClass(int i, int sourceClass)
        {
            if (sourceClass < 0 || sourceClass >= PowerKernel.MaxSourceClasses)
            {
                return 0f;
            }
            return _classes[At(i) * PowerKernel.MaxSourceClasses + sourceClass];
        }

        /// <summary>第 i 个点的各类别发电复制到 <paramref name="into"/>（从 <paramref name="offset"/> 起 <see cref="PowerKernel.MaxSourceClasses"/> 个）。</summary>
        public void GetClasses(int i, float[] into, int offset)
        {
            int m = PowerKernel.MaxSourceClasses;
            Array.Copy(_classes, At(i) * m, into, offset, m);
        }

        public PowerCurve Clone()
        {
            var c = new PowerCurve(Capacity);
            var cls = new float[PowerKernel.MaxSourceClasses];
            for (int i = 0; i < Count; i++)
            {
                Get(i, out float a, out float b, out float d, out float s);
                GetClasses(i, cls, 0);
                c.Add(a, b, d, s, cls, 0);
            }
            return c;
        }
    }

    /// <summary>存档快照（热更层写进 PowerGridState）。曲线按电网拼接（最旧 → 最新），每个电网 CurveCounts[i] 个点。</summary>
    public sealed class PowerSnapshot
    {
        public int FormatVersion = PowerKernel.FormatVersion;
        public int NextSerial = 1;
        public int[] SubnetSerials = Array.Empty<int>();
        public int[] SubnetAnchorKeys = Array.Empty<int>();
        public int[] CurveCounts = Array.Empty<int>();
        public float[] CurveSupply = Array.Empty<float>();
        public float[] CurveDemand = Array.Empty<float>();
        public float[] CurveDelivered = Array.Empty<float>();
        public float[] CurveStored = Array.Empty<float>();
        /// <summary>FG4-ECO-04（格式 2）：每个曲线点的各类别发电，按点拼接，每点 <see cref="PowerKernel.MaxSourceClasses"/> 个。格式 1 的存档没有这一列（读成全 0）。</summary>
        public float[] CurveClassSupply = Array.Empty<float>();
        public int[] StorageKeys = Array.Empty<int>();
        public double[] StorageStored = Array.Empty<double>();
    }

    /// <summary>
    /// FG3-LOG-06（FG03 FGR-LOG-060、061）：电力子网内核（AOT，托管代码，结果确定）。
    ///
    /// - 拓扑（<see cref="Rebuild"/>）：节点按空间哈希找邻居，距离 ≤ 两者较大的半径即相连（并查集）；每座建筑接到“覆盖它、距离最近”的导电节点所在的电网
    ///   （同距离取下标小的）。只在拓扑变化时调用：O(节点 × 附近节点 + 建筑 × 附近节点)。
    /// - 结算（<see cref="Settle"/>）：每个电网各自按输入顺序（热更层已按优先级 → 建筑编号排好）贪心分配，放不下的断电——与 ER3-PWR-01 的全局仲裁同一规则，
    ///   只是按电网分开；储能在缺电时按功率上限放电、盈余时充电。
    /// - 时间（<see cref="StepSecond"/>、<see cref="Sample"/>）：储能每游戏秒积分一次（没有储能的电网不做任何事），曲线按游戏时间采样；每次 O(电网数 + 储能实体)。
    /// - 电网编号跨拓扑变化保持：新电网沿用它的节点里“多数原来所在的电网”的编号；一个旧电网的节点散到几个新电网 = 断开（<see cref="LastSplits"/>）。
    /// - FG4-ECO-04 能源扩展：发电分类别（曲线分开记；类别系数 = 太阳能的昼夜 / 天气）；按负荷出力的发电（燃油发电机）只补缺口与储能充电，报告负荷比例；
    ///   储能可以关掉充电 / 放电，放电可以只留给高优先级建筑（放电按“优先级 1 先”的分配顺序取，先取只给高优先级的那部分，把给所有建筑的留到后面）。
    /// </summary>
    public sealed class PowerKernel
    {
        /// <summary>存档格式：1 = FG3-LOG-06；2 = FG4-ECO-04（曲线多存各类别发电）。读档两种都认。</summary>
        public const int FormatVersion = 2;
        public const int VirtualHubKey = -1;
        /// <summary>发电类别上限（曲线每个点存这么多类）。</summary>
        public const int MaxSourceClasses = 8;
        /// <summary>优先级档数（1～4）。</summary>
        public const int PriorityLevels = 4;
        private const int MinBucket = 16;

        private PowerEntity[] _e = Array.Empty<PowerEntity>();
        private int _n;
        private int[] _subnetOf = Array.Empty<int>();
        private PowerUse[] _use = Array.Empty<PowerUse>();
        private int[] _link = Array.Empty<int>();
        private int[] _coverNode = Array.Empty<int>();
        private double[] _stored = Array.Empty<double>();
        private int[] _uf = Array.Empty<int>();
        private PowerSubnetInfo[] _subnets = Array.Empty<PowerSubnetInfo>();
        private float[] _remaining = Array.Empty<float>();
        private float[] _discharge = Array.Empty<float>();
        // FG4-ECO-04：每个电网 × 发电类别的发电；每个电网 × 放电对象档（0 = 只给优先级 1 … 3 = 给所有建筑）的可放电量 / 已放电量；可充电量；从发电（非储能）供上的用电。
        private float[] _classSupply = Array.Empty<float>();
        private float[] _pool = Array.Empty<float>();
        private float[] _poolUsed = Array.Empty<float>();
        private float[] _chargeRoom = Array.Empty<float>();
        /// <summary>每个电网每个放电档的储能这一秒还能充多少（充电只给“放电对象够不着停机建筑”的储能，见 <see cref="_chargeCut"/>）。</summary>
        private float[] _poolRoom = Array.Empty<float>();
        /// <summary>每个电网能充电的放电档上限（不含）：停机建筑里最高的优先级档 p（0 起）→ 只有放电只给 1～p 档的储能能充；没有停机 = 全部档。</summary>
        private int[] _chargeCut = Array.Empty<int>();
        private float[] _supplyUsed = Array.Empty<float>();
        private readonly float[] _classFactor = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        private readonly List<int> _nodes = new List<int>(64);
        // 节点空间索引：平铺的桶网格（链表头 + 下一个），桶边长随节点分布自适应（至少 16 格，网格最多约 512 × 512 桶）。
        private int _bucket = MinBucket;
        private int _gx0;
        private int _gy0;
        private int _gw;
        private int _gh;
        private int[] _head = Array.Empty<int>();
        private int[] _next = Array.Empty<int>();
        private readonly List<int> _nbr = new List<int>(16);
        private float _maxRadius;
        /// <summary>桶里节点（小半径）的最大半径；大半径节点（归还核心等，半径超过桶宽、个数很少）不进桶，逐个直接比。
        /// 这样桶扫描范围按电塔的半径而不是核心的半径定，建筑接入查询的候选数减半以上。</summary>
        private float _maxSmallRadius;
        private readonly List<int> _bigNodes = new List<int>(4);
        private const int MaxBigNodes = 8;
        private readonly Dictionary<int, int> _nodeSerial = new Dictionary<int, int>();
        private readonly Dictionary<int, PowerCurve> _curves = new Dictionary<int, PowerCurve>();
        private readonly Dictionary<int, double> _storedByKey = new Dictionary<int, double>();
        private readonly List<PowerSplit> _splits = new List<PowerSplit>(2);
        private readonly int _curveCapacity;
        private bool _anyStorage;

        public PowerKernel(int curveCapacity)
        {
            _curveCapacity = Math.Max(2, curveCapacity);
        }

        public int CurveCapacity => _curveCapacity;
        public int Count => _n;
        public int SubnetCount { get; private set; }
        public int NextSerial { get; private set; } = 1;
        /// <summary>每次 <see cref="Rebuild"/> +1（叠加层据此重画）。</summary>
        public int TopologyVersion { get; private set; }
        /// <summary>每次供电结果或储能变化 +1（面板据此刷新）。</summary>
        public int StateVersion { get; private set; }
        public int RebuildCount { get; private set; }
        public int SettleCount { get; private set; }
        public int SampleCount { get; private set; }
        public bool AnyStorage => _anyStorage;
        /// <summary>最近一次 <see cref="Rebuild"/> 里断开的电网。</summary>
        public IReadOnlyList<PowerSplit> LastSplits => _splits;

        public PowerEntity Entity(int i) => _e[i];
        public int SubnetOf(int i) => _subnetOf[i];
        public PowerUse UseOf(int i) => _use[i];
        /// <summary>节点在连通树里的上一个节点（叠加层画连线；根与非节点为 -1）。</summary>
        public int LinkOf(int i) => _link[i];
        /// <summary>接入这个实体的节点（-1 = 没接入）。节点自己返回自己。</summary>
        public int CoverNodeOf(int i) => _coverNode[i];
        public double StoredOf(int i) => _stored[i];
        public PowerSubnetInfo Subnet(int s) => _subnets[s];

        public int SubnetIndexOfSerial(int serial)
        {
            for (int s = 0; s < SubnetCount; s++)
            {
                if (_subnets[s].Serial == serial)
                {
                    return s;
                }
            }
            return -1;
        }

        public bool TryGetCurve(int serial, out PowerCurve curve) => _curves.TryGetValue(serial, out curve);

        // ── FG4-ECO-04：发电类别、按负荷出力、储能设置 ─────────────────────────────

        /// <summary>电网 s 里第 c 类发电（电，已乘类别系数；上一次结算的结果）。</summary>
        public float ClassSupply(int s, int c) =>
            s >= 0 && s < SubnetCount && c >= 0 && c < MaxSourceClasses ? _classSupply[s * MaxSourceClasses + c] : 0f;

        /// <summary>第 c 类发电的系数（太阳能的昼夜 / 天气；默认 1）。</summary>
        public float ClassFactor(int c) => c >= 0 && c < MaxSourceClasses ? _classFactor[c] : 1f;

        /// <summary>改第 c 类发电的系数（0～1 之外夹住）。变了返回 true——调用方随后 <see cref="Settle"/>（不重建拓扑）。</summary>
        public bool SetClassFactor(int c, float factor)
        {
            if (c < 0 || c >= MaxSourceClasses || float.IsNaN(factor))
            {
                return false;
            }
            factor = Math.Max(0f, Math.Min(1f, factor));
            if (_classFactor[c] == factor)
            {
                return false;
            }
            _classFactor[c] = factor;
            return true;
        }

        /// <summary>实体 i 这一刻实际能发的电（SupplyOn 且乘了类别系数；不接入电网也照算，调用方看 <see cref="SubnetOf"/>）。</summary>
        public float EffectiveSupply(int i)
        {
            if (i < 0 || i >= _n || !_e[i].SupplyOn || _e[i].Supply <= 0f)
            {
                return 0f;
            }
            return _e[i].Supply * _classFactor[Math.Min(_e[i].SourceClass, (byte)(MaxSourceClasses - 1))];
        }

        /// <summary>实体 i 的负荷比例：按负荷出力的发电 = 它所在电网的 <see cref="PowerSubnetInfo.DispatchLoad"/>；其余发电 = 1；没接入 / 不发电 = 0。</summary>
        public float LoadOf(int i)
        {
            if (i < 0 || i >= _n || _subnetOf[i] < 0 || !_e[i].SupplyOn || _e[i].Supply <= 0f)
            {
                return 0f;
            }
            return _e[i].Dispatchable ? _subnets[_subnetOf[i]].DispatchLoad : 1f;
        }

        /// <summary>开关实体 i 的发电（燃油发电机烧完 / 来油）。变了返回 true——调用方随后 <see cref="Settle"/>（不重建拓扑）。</summary>
        public bool SetSupplyOn(int i, bool on)
        {
            if (i < 0 || i >= _n || _e[i].SupplyOn == on)
            {
                return false;
            }
            _e[i].SupplyOn = on;
            return true;
        }

        /// <summary>改实体 i 的储能设置（玩家在电网面板改）。变了返回 true——调用方随后 <see cref="Settle"/>。</summary>
        public bool SetStorageSettings(int i, bool noCharge, bool noDischarge, byte reserve)
        {
            if (i < 0 || i >= _n || _e[i].StorageCapacity <= 0)
            {
                return false;
            }
            reserve = (byte)Math.Min(PriorityLevels - 1, (int)reserve);
            if (_e[i].StorageNoCharge == noCharge && _e[i].StorageNoDischarge == noDischarge && _e[i].StorageReserve == reserve)
            {
                return false;
            }
            _e[i].StorageNoCharge = noCharge;
            _e[i].StorageNoDischarge = noDischarge;
            _e[i].StorageReserve = reserve;
            return true;
        }

        /// <summary>储能实体 i 这一秒的充放电功率（+ 充电，− 放电；与 <see cref="StepSecond"/> 同一个分摊公式）。不是储能 / 没接入 = 0。</summary>
        public float StorageFlowOf(int i)
        {
            if (i < 0 || i >= _n || _subnetOf[i] < 0 || !_e[i].StorageOn || _e[i].StorageCapacity <= 0)
            {
                return 0f;
            }
            int s = _subnetOf[i];
            float flow = _subnets[s].StorageFlow;
            if (flow < 0f && !_e[i].StorageNoDischarge)
            {
                int k = s * PriorityLevels + PoolOf(_e[i]);
                float avail = (float)Math.Min(_e[i].StorageRate, _stored[i]);
                return _pool[k] > 0f ? -_poolUsed[k] * (avail / _pool[k]) : 0f;
            }
            if (flow > 0f && !_e[i].StorageNoCharge && _chargeRoom[s] > 0f && PoolOf(_e[i]) < _chargeCut[s])
            {
                float room = (float)Math.Max(0, Math.Min(_e[i].StorageRate, _e[i].StorageCapacity - _stored[i]));
                return flow * (room / _chargeRoom[s]);
            }
            return 0f;
        }

        /// <summary>
        /// 储能实体 i 这一秒因“电网有停机建筑、它的放电对象够得着它们”而不充电（悬停 / 面板写明原因，B06）。
        /// 放电只给更高优先级（够不着任何停机建筑）的储能照常用剩余电量充电。
        /// </summary>
        public bool StorageChargeHeldByBrownout(int i)
        {
            if (i < 0 || i >= _n || _subnetOf[i] < 0 || !_e[i].StorageOn || _e[i].StorageCapacity <= 0 || _e[i].StorageNoCharge)
            {
                return false;
            }
            int s = _subnetOf[i];
            return _subnets[s].Brownouts > 0 && PoolOf(_e[i]) >= _chargeCut[s];
        }

        /// <summary>放电对象档：0 = 只给优先级 1 … 3 = 给所有建筑（<see cref="PowerEntity.StorageReserve"/> 0 = 所有建筑）。</summary>
        private static int PoolOf(in PowerEntity e) => e.StorageReserve == 0 ? PriorityLevels - 1 : Math.Min(PriorityLevels - 1, e.StorageReserve - 1);

        private static float CenterX(in PowerEntity e) => (e.MinX + e.MaxX) * 0.5f;
        private static float CenterY(in PowerEntity e) => (e.MinY + e.MaxY) * 0.5f;

        /// <summary>点 (cx, cy) 到实体占地格中心所在矩形的距离平方。</summary>
        private static float DistSqToRect(float cx, float cy, in PowerEntity e)
        {
            float x = Math.Max(e.MinX, Math.Min(e.MaxX, cx)) - cx;
            float y = Math.Max(e.MinY, Math.Min(e.MaxY, cy)) - cy;
            return x * x + y * y;
        }

        private int FloorDiv(float v) => (int)Math.Floor(v / _bucket);

        /// <summary>桶 (bx, by) 在平铺网格里的下标；网格外返回 -1。</summary>
        private int BucketIndex(int bx, int by)
        {
            int x = bx - _gx0;
            int y = by - _gy0;
            return x < 0 || y < 0 || x >= _gw || y >= _gh ? -1 : y * _gw + x;
        }

        /// <summary>按节点中心建平铺桶网格（每个桶内按下标升序）。O(节点 + 桶)。</summary>
        private void BuildIndex()
        {
            if (_nodes.Count == 0)
            {
                _gw = _gh = 0;
                _bigNodes.Clear();
                _maxSmallRadius = 0f;
                return;
            }
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (int i in _nodes)
            {
                float x = CenterX(_e[i]);
                float y = CenterY(_e[i]);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            float span = Math.Max(maxX - minX, maxY - minY);
            _bucket = Math.Max(MinBucket, (int)Math.Ceiling(span / 512f));
            // 大半径节点（半径超过桶宽）个数不多时单列（见 _bigNodes）；太多就全部进桶，退回原来的做法。
            _bigNodes.Clear();
            _maxSmallRadius = 0f;
            foreach (int i in _nodes)
            {
                if (_e[i].NodeRadius > _bucket)
                {
                    _bigNodes.Add(i);
                }
                else
                {
                    _maxSmallRadius = Math.Max(_maxSmallRadius, _e[i].NodeRadius);
                }
            }
            if (_bigNodes.Count > MaxBigNodes)
            {
                _bigNodes.Clear();
                _maxSmallRadius = _maxRadius;
            }
            _gx0 = FloorDiv(minX);
            _gy0 = FloorDiv(minY);
            _gw = FloorDiv(maxX) - _gx0 + 1;
            _gh = FloorDiv(maxY) - _gy0 + 1;
            int cells = _gw * _gh;
            if (_head.Length < cells)
            {
                _head = new int[Math.Max(cells, _head.Length * 2)];
            }
            for (int b = 0; b < cells; b++)
            {
                _head[b] = -1;
            }
            if (_next.Length < _n)
            {
                _next = new int[_e.Length];
            }
            for (int k = _nodes.Count - 1; k >= 0; k--)
            {
                int i = _nodes[k];
                if (_bigNodes.Count > 0 && _bigNodes.Contains(i))
                {
                    continue;
                }
                int b = BucketIndex(FloorDiv(CenterX(_e[i])), FloorDiv(CenterY(_e[i])));
                _next[i] = _head[b];
                _head[b] = i;
            }
        }

        /// <summary>批量拆除影响查询期间“当作已拆掉”的实体（其余时候为 null）。</summary>
        private HashSet<int> _excludeSet;

        private bool IsExcluded(int j, int exclude) => j == exclude || (_excludeSet != null && _excludeSet.Contains(j));

        /// <summary>节点 i 的全部相连节点（按桶与下标顺序；exclude 不算），写进 into。</summary>
        private void CollectNeighbours(int i, int exclude, List<int> into)
        {
            into.Clear();
            float cx = CenterX(_e[i]);
            float cy = CenterY(_e[i]);
            float reach = Math.Max(_e[i].NodeRadius, _maxSmallRadius) + 1e-3f;
            int bx0 = Math.Max(_gx0, FloorDiv(cx - reach));
            int bx1 = Math.Min(_gx0 + _gw - 1, FloorDiv(cx + reach));
            int by0 = Math.Max(_gy0, FloorDiv(cy - reach));
            int by1 = Math.Min(_gy0 + _gh - 1, FloorDiv(cy + reach));
            for (int y = by0; y <= by1; y++)
            {
                for (int x = bx0; x <= bx1; x++)
                {
                    for (int j = _head[(y - _gy0) * _gw + (x - _gx0)]; j >= 0; j = _next[j])
                    {
                        TryLink(i, j, cx, cy, exclude, into);
                    }
                }
            }
            foreach (int j in _bigNodes)
            {
                TryLink(i, j, cx, cy, exclude, into);
            }
        }

        private void TryLink(int i, int j, float cx, float cy, int exclude, List<int> into)
        {
            if (j == i || IsExcluded(j, exclude))
            {
                return;
            }
            float lim = Math.Max(_e[i].NodeRadius, _e[j].NodeRadius);
            float ddx = CenterX(_e[j]) - cx;
            float ddy = CenterY(_e[j]) - cy;
            if (ddx * ddx + ddy * ddy <= lim * lim + 1e-4f)
            {
                into.Add(j);
            }
        }

        // ── 拓扑 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 按新的实体表重算拓扑（节点连通、建筑接入、电网编号、断开记录），再结算一次。
        /// 储能存量按实体键从上一次继承（新容量更小时夹住）。只在拓扑变化时调用。
        /// </summary>
        public void Rebuild(PowerEntity[] entities, int count)
        {
            RebuildCount++;
            TopologyVersion++;
            // 先把当前存量记回按键的表（上一次的实体可能被移除或换了下标）。
            for (int i = 0; i < _n; i++)
            {
                if (_e[i].StorageCapacity > 0 && _e[i].Key != VirtualHubKey)
                {
                    _storedByKey[_e[i].Key] = _stored[i];
                }
            }
            _n = Math.Max(0, Math.Min(count, entities?.Length ?? 0));
            Ensure(_n);
            if (_n > 0)
            {
                Array.Copy(entities, _e, _n);
            }
            var alive = new HashSet<int>();
            _anyStorage = false;
            for (int i = 0; i < _n; i++)
            {
                alive.Add(_e[i].Key);
                _stored[i] = 0;
                if (_e[i].StorageCapacity > 0 && _storedByKey.TryGetValue(_e[i].Key, out double st))
                {
                    _stored[i] = Math.Max(0, Math.Min(_e[i].StorageCapacity, st));
                }
                _anyStorage |= _e[i].StorageCapacity > 0;
            }
            // 被拆掉的储能实体：存量随之消失。
            if (_storedByKey.Count > 0)
            {
                var gone = new List<int>();
                foreach (int k in _storedByKey.Keys)
                {
                    if (!alive.Contains(k))
                    {
                        gone.Add(k);
                    }
                }
                foreach (int k in gone)
                {
                    _storedByKey.Remove(k);
                }
            }

            // 1) 节点空间索引。
            _nodes.Clear();
            _maxRadius = 0f;
            for (int i = 0; i < _n; i++)
            {
                _subnetOf[i] = -1;
                _link[i] = -1;
                _coverNode[i] = -1;
                _use[i] = PowerUse.None;
                _uf[i] = i;
                if (_e[i].NodeRadius > 0f && _e[i].Conducts)
                {
                    _nodes.Add(i);
                    _maxRadius = Math.Max(_maxRadius, _e[i].NodeRadius);
                }
            }
            BuildIndex();

            // 2) 节点相连（并查集；遍历顺序 = 下标顺序，结果确定）。连通树（画线）用 BFS 另算。
            foreach (int i in _nodes)
            {
                CollectNeighbours(i, -1, _nbr);
                for (int k = 0; k < _nbr.Count; k++)
                {
                    if (_nbr[k] > i)
                    {
                        Union(i, _nbr[k]);
                    }
                }
            }

            // 3) 电网下标：按“电网里下标最小的节点”排序（确定）。
            SubnetCount = 0;
            var rootToSubnet = new Dictionary<int, int>();
            foreach (int i in _nodes)
            {
                int root = Find(i);
                if (!rootToSubnet.TryGetValue(root, out int s))
                {
                    s = SubnetCount++;
                    rootToSubnet[root] = s;
                }
                _subnetOf[i] = s;
                _coverNode[i] = i;
            }
            if (_subnets.Length < SubnetCount)
            {
                _subnets = new PowerSubnetInfo[Math.Max(SubnetCount, _subnets.Length * 2)];
                _remaining = new float[_subnets.Length];
                _discharge = new float[_subnets.Length];
                _classSupply = new float[_subnets.Length * MaxSourceClasses];
                _pool = new float[_subnets.Length * PriorityLevels];
                _poolUsed = new float[_subnets.Length * PriorityLevels];
                _chargeRoom = new float[_subnets.Length];
                _poolRoom = new float[_subnets.Length * PriorityLevels];
                _chargeCut = new int[_subnets.Length];
                _supplyUsed = new float[_subnets.Length];
            }
            for (int s = 0; s < SubnetCount; s++)
            {
                _subnets[s] = new PowerSubnetInfo { FirstNode = -1 };
            }
            foreach (int i in _nodes)
            {
                int s = _subnetOf[i];
                if (_subnets[s].FirstNode < 0)
                {
                    _subnets[s].FirstNode = i;
                }
                _subnets[s].Nodes++;
            }
            BuildLinkTree();

            // 4) 建筑接入：覆盖它、距离最近的导电节点（同距离取下标小的）。节点自己（含不导电的电塔）不“接入”别人。
            for (int i = 0; i < _n; i++)
            {
                if (_e[i].NodeRadius > 0f)
                {
                    continue;
                }
                int best = NearestCovering(_e[i], -1, out _);
                if (best >= 0)
                {
                    _coverNode[i] = best;
                    _subnetOf[i] = _subnetOf[best];
                }
            }

            AssignSerials();
            Settle();
        }

        private void BuildLinkTree()
        {
            if (_nodes.Count == 0)
            {
                return;
            }
            var seen = new bool[_n];
            var queue = new Queue<int>();
            foreach (int start in _nodes)
            {
                if (seen[start])
                {
                    continue;
                }
                seen[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    CollectNeighbours(i, -1, _nbr);
                    foreach (int j in _nbr)
                    {
                        if (!seen[j])
                        {
                            seen[j] = true;
                            _link[j] = i;
                            queue.Enqueue(j);
                        }
                    }
                }
            }
        }

        /// <summary>覆盖这个占地、距离最近的导电节点（exclude 不算；没有返回 -1）。</summary>
        private int NearestCovering(PowerEntity e, int exclude, out float distance)
        {
            distance = float.PositiveInfinity;
            if (_nodes.Count == 0)
            {
                return -1;
            }
            // 能覆盖这个占地的桶节点，中心一定在占地向外扩 _maxSmallRadius 的矩形里：只扫这个矩形压到的桶（精确范围）。
            float reach = _maxSmallRadius + 1e-3f;
            int bx0 = Math.Max(_gx0, FloorDiv(e.MinX - reach));
            int bx1 = Math.Min(_gx0 + _gw - 1, FloorDiv(e.MaxX + reach));
            int by0 = Math.Max(_gy0, FloorDiv(e.MinY - reach));
            int by1 = Math.Min(_gy0 + _gh - 1, FloorDiv(e.MaxY + reach));
            int best = -1;
            float bestD = float.PositiveInfinity;
            long span = (long)Math.Max(0, bx1 - bx0 + 1) * Math.Max(0, by1 - by0 + 1);
            if (span > _nodes.Count * 4L)
            {
                // 占地极大（或节点很少）：直接扫全部节点更快。
                foreach (int j in _nodes)
                {
                    Consider(j);
                }
            }
            else
            {
                for (int by = by0; by <= by1; by++)
                {
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        for (int j = _head[(by - _gy0) * _gw + (bx - _gx0)]; j >= 0; j = _next[j])
                        {
                            Consider(j);
                        }
                    }
                }
                foreach (int j in _bigNodes)
                {
                    Consider(j);
                }
            }
            if (best >= 0)
            {
                distance = (float)Math.Sqrt(bestD);
            }
            return best;

            void Consider(int j)
            {
                if (IsExcluded(j, exclude))
                {
                    return;
                }
                float d = DistSqToRect(CenterX(_e[j]), CenterY(_e[j]), e);
                float lim = _e[j].NodeRadius;
                if (d <= lim * lim + 1e-4f && (d < bestD || (d == bestD && j < best)))
                {
                    bestD = d;
                    best = j;
                }
            }
        }

        private int Find(int i)
        {
            while (_uf[i] != i)
            {
                _uf[i] = _uf[_uf[i]];
                i = _uf[i];
            }
            return i;
        }

        private void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra == rb)
            {
                return;
            }
            // 小下标做根（确定）。
            if (ra < rb)
            {
                _uf[rb] = ra;
            }
            else
            {
                _uf[ra] = rb;
            }
        }

        /// <summary>电网编号：多数节点原来在哪个电网就沿用哪个编号；同一个旧编号被几段争用 = 断开（票多的一段保留编号，其余拿新编号并复制旧曲线）。</summary>
        private void AssignSerials()
        {
            _splits.Clear();
            int m = SubnetCount;
            var best = new int[m];
            var bestVotes = new int[m];
            var votesBySerial = new Dictionary<int, List<int>>(); // 旧编号 → 有它的选票的新电网（按下标）
            var tally = new Dictionary<int, int>();
            for (int s = 0; s < m; s++)
            {
                best[s] = 0;
                bestVotes[s] = 0;
            }
            // 逐电网统计选票（节点按下标顺序）。
            var perSubnet = new List<int>[m];
            foreach (int i in _nodes)
            {
                int s = _subnetOf[i];
                (perSubnet[s] ??= new List<int>(4)).Add(i);
            }
            for (int s = 0; s < m; s++)
            {
                tally.Clear();
                foreach (int i in perSubnet[s])
                {
                    if (_nodeSerial.TryGetValue(_e[i].Key, out int old))
                    {
                        tally.TryGetValue(old, out int v);
                        tally[old] = v + 1;
                    }
                }
                foreach (KeyValuePair<int, int> kv in tally)
                {
                    if (kv.Value > bestVotes[s] || (kv.Value == bestVotes[s] && kv.Key < best[s]))
                    {
                        best[s] = kv.Key;
                        bestVotes[s] = kv.Value;
                    }
                    if (!votesBySerial.TryGetValue(kv.Key, out List<int> owners))
                    {
                        owners = new List<int>(2);
                        votesBySerial[kv.Key] = owners;
                    }
                    owners.Add(s);
                }
            }
            // 争用同一旧编号：票最多的一段保留（同票取下标小的）。
            var serialOwner = new Dictionary<int, int>();
            for (int s = 0; s < m; s++)
            {
                if (bestVotes[s] == 0)
                {
                    continue;
                }
                if (!serialOwner.TryGetValue(best[s], out int holder)
                    || bestVotes[s] > bestVotes[holder] || (bestVotes[s] == bestVotes[holder] && s < holder))
                {
                    serialOwner[best[s]] = s;
                }
            }
            var newCurves = new Dictionary<int, PowerCurve>();
            for (int s = 0; s < m; s++)
            {
                int serial;
                if (bestVotes[s] > 0 && serialOwner[best[s]] == s)
                {
                    serial = best[s];
                    if (_curves.TryGetValue(serial, out PowerCurve c))
                    {
                        newCurves[serial] = c;
                    }
                }
                else
                {
                    serial = NextSerial++;
                    if (bestVotes[s] > 0 && _curves.TryGetValue(best[s], out PowerCurve parent))
                    {
                        newCurves[serial] = parent.Clone(); // 断开出来的一段：带着断开前的历史。
                    }
                }
                _subnets[s].Serial = serial;
                if (!newCurves.ContainsKey(serial))
                {
                    newCurves[serial] = new PowerCurve(_curveCapacity);
                }
            }
            // 断开记录：一个旧编号的节点散到了几个新电网（按旧编号升序，确定）。
            var olds = new List<int>(votesBySerial.Keys);
            olds.Sort();
            foreach (int old in olds)
            {
                List<int> owners = votesBySerial[old];
                if (owners.Count < 2)
                {
                    continue;
                }
                var serials = new int[owners.Count];
                int w = 0;
                if (serialOwner.TryGetValue(old, out int keep) && owners.Contains(keep))
                {
                    serials[w++] = _subnets[keep].Serial;
                }
                foreach (int s in owners)
                {
                    if (!(serialOwner.TryGetValue(old, out int k2) && k2 == s))
                    {
                        serials[w++] = _subnets[s].Serial;
                    }
                }
                _splits.Add(new PowerSplit(old, owners.Count, serials));
            }
            _curves.Clear();
            foreach (KeyValuePair<int, PowerCurve> kv in newCurves)
            {
                _curves[kv.Key] = kv.Value;
            }
            _nodeSerial.Clear();
            foreach (int i in _nodes)
            {
                _nodeSerial[_e[i].Key] = _subnets[_subnetOf[i]].Serial;
            }
        }

        // ── 结算 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 按当前拓扑、储能存量与设置结算一次（不经过时间）。返回是否有实体的供电结果变了。
        /// 每个电网：发电（乘类别系数）先供；不够时从储能取——储能按放电对象分档（只给优先级 1 / ≤2 / ≤3 / 所有建筑），
        /// 优先级 p 的建筑只能取“给 ≤p 及更宽”的档，先取最窄的档（宽档留给后面低优先级的建筑）。盈余充进允许充电的储能。
        /// 按负荷出力的发电（燃油）的负荷 =（从发电供上的用电 + 充电 − 不按负荷的发电）÷ 它能发的电。
        /// </summary>
        public bool Settle()
        {
            SettleCount++;
            int m = SubnetCount;
            for (int s = 0; s < m; s++)
            {
                PowerSubnetInfo info = _subnets[s];
                info.Supply = 0f;
                info.Demand = 0f;
                info.Delivered = 0f;
                info.StorageFlow = 0f;
                info.StorageCapacity = 0;
                info.Stored = 0;
                info.StorageRate = 0f;
                info.Consumers = 0;
                info.Brownouts = 0;
                info.Producers = 0;
                info.StorageUnits = 0;
                info.DispatchSupply = 0f;
                info.DispatchLoad = 0f;
                info.StorageDischargeAvailable = 0f;
                info.StorageChargeAvailable = 0f;
                info.ReserveHeld = 0;
                _subnets[s] = info;
                _chargeRoom[s] = 0f;
                _chargeCut[s] = PriorityLevels;
                _supplyUsed[s] = 0f;
                for (int c = 0; c < MaxSourceClasses; c++)
                {
                    _classSupply[s * MaxSourceClasses + c] = 0f;
                }
                for (int k = 0; k < PriorityLevels; k++)
                {
                    _pool[s * PriorityLevels + k] = 0f;
                    _poolUsed[s * PriorityLevels + k] = 0f;
                    _poolRoom[s * PriorityLevels + k] = 0f;
                }
            }
            for (int i = 0; i < _n; i++)
            {
                int s = _subnetOf[i];
                if (s < 0)
                {
                    continue;
                }
                if (_e[i].SupplyOn && _e[i].Supply > 0f)
                {
                    int cls = Math.Min((int)_e[i].SourceClass, MaxSourceClasses - 1);
                    float eff = _e[i].Supply * _classFactor[cls];
                    _subnets[s].Supply += eff;
                    _subnets[s].Producers++;
                    _classSupply[s * MaxSourceClasses + cls] += eff;
                    if (_e[i].Dispatchable)
                    {
                        _subnets[s].DispatchSupply += eff;
                    }
                }
                if (_e[i].StorageOn && _e[i].StorageCapacity > 0)
                {
                    _subnets[s].StorageCapacity += _e[i].StorageCapacity;
                    _subnets[s].Stored += _stored[i];
                    _subnets[s].StorageRate += _e[i].StorageRate;
                    _subnets[s].StorageUnits++;
                    if (!_e[i].StorageNoDischarge)
                    {
                        // 一秒内最多放出存量本身。
                        float avail = (float)Math.Min(_e[i].StorageRate, _stored[i]);
                        _pool[s * PriorityLevels + PoolOf(_e[i])] += avail;
                        _subnets[s].StorageDischargeAvailable += avail;
                    }
                    if (!_e[i].StorageNoCharge)
                    {
                        float room = (float)Math.Max(0, Math.Min(_e[i].StorageRate, _e[i].StorageCapacity - _stored[i]));
                        _poolRoom[s * PriorityLevels + PoolOf(_e[i])] += room;
                        _subnets[s].StorageChargeAvailable += room;
                    }
                }
            }
            for (int s = 0; s < m; s++)
            {
                _remaining[s] = _subnets[s].Supply;
            }
            bool changed = false;
            for (int i = 0; i < _n; i++)
            {
                PowerUse before = _use[i];
                PowerUse now;
                if (!_e[i].DemandOn)
                {
                    now = PowerUse.None;
                }
                else
                {
                    int s = _subnetOf[i];
                    if (s < 0)
                    {
                        now = PowerUse.Unconnected;
                    }
                    else
                    {
                        float need = _e[i].Demand;
                        _subnets[s].Demand += need;
                        _subnets[s].Consumers++;
                        int p = Math.Max(1, Math.Min(PriorityLevels, _e[i].Priority)) - 1;
                        int baseIdx = s * PriorityLevels;
                        float avail = _remaining[s];
                        for (int k = p; k < PriorityLevels; k++)
                        {
                            avail += _pool[baseIdx + k] - _poolUsed[baseIdx + k];
                        }
                        if (avail >= need)
                        {
                            float take = Math.Min(_remaining[s], need);
                            _remaining[s] -= take;
                            _supplyUsed[s] += take;
                            float left = need - take;
                            for (int k = p; k < PriorityLevels && left > 0f; k++)
                            {
                                float d = Math.Min(left, _pool[baseIdx + k] - _poolUsed[baseIdx + k]);
                                if (d > 0f)
                                {
                                    _poolUsed[baseIdx + k] += d;
                                    left -= d;
                                }
                            }
                            _subnets[s].Delivered += need;
                            now = PowerUse.Powered;
                        }
                        else
                        {
                            _subnets[s].Brownouts++;
                            _chargeCut[s] = Math.Min(_chargeCut[s], p);
                            // 储能里还有电、只是留给了更高优先级：记一笔（悬停 / 面板写明“储能留给高优先级”）。
                            float reserved = 0f;
                            for (int k = 0; k < p; k++)
                            {
                                reserved += _pool[baseIdx + k] - _poolUsed[baseIdx + k];
                            }
                            if (avail + reserved >= need)
                            {
                                _subnets[s].ReserveHeld++;
                            }
                            now = PowerUse.Brownout;
                        }
                    }
                }
                _use[i] = now;
                changed |= now != before;
            }
            for (int s = 0; s < m; s++)
            {
                float discharged = 0f;
                for (int k = 0; k < PriorityLevels; k++)
                {
                    discharged += _poolUsed[s * PriorityLevels + k];
                }
                float charge = 0f;
                // 能充电的储能：放电对象够不着任何一座停机建筑的（FG4-ECO-04 修复轮 P2）。剩下的零头本来就不够任何一座停机建筑用；
                // 若拿去充一座“能放给停机建筑”的储能，下一秒它刚够一座就放、放完又停，会每隔几秒反复启停（通知刷屏），所以这类储能缺电时不充；
                // 但“只给优先级 1～N”的储能放不到更低优先级的停机建筑，充它不会引起启停——静默夜前电网长期满载时也能给高优先级攒电。
                float room = 0f;
                for (int k = 0; k < _chargeCut[s]; k++)
                {
                    room += _poolRoom[s * PriorityLevels + k];
                }
                _chargeRoom[s] = room;
                if (discharged > 0f)
                {
                    _subnets[s].StorageFlow = -discharged;
                }
                else if (_chargeRoom[s] > 0f)
                {
                    charge = Math.Max(0f, Math.Min(_remaining[s], _chargeRoom[s]));
                    _subnets[s].StorageFlow = charge;
                }
                float disp = _subnets[s].DispatchSupply;
                if (disp > 0f)
                {
                    float free = _subnets[s].Supply - disp;
                    _subnets[s].DispatchLoad = Math.Max(0f, Math.Min(1f, (_supplyUsed[s] + charge - free) / disp));
                }
            }
            StateVersion++;
            return changed;
        }

        /// <summary>
        /// 经过一游戏秒：每个有储能的电网按上一次结算的功率充 / 放电，再结算一次。放电：每个放电档的已放电量按各储能这一秒能放的量（min(功率, 存量)）分摊；
        /// 充电：按各储能这一秒能充的量（min(功率, 剩余容量)）分摊——都不会超过单个储能的上限，电量守恒。
        /// 没有储能时什么都不做（返回 false）。返回是否有实体的供电结果变了。
        /// </summary>
        public bool StepSecond()
        {
            if (!_anyStorage)
            {
                return false;
            }
            bool moved = false;
            for (int s = 0; s < SubnetCount; s++)
            {
                if (_subnets[s].StorageFlow != 0f && _subnets[s].StorageUnits > 0)
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
            {
                return false;
            }
            for (int i = 0; i < _n; i++)
            {
                int s = _subnetOf[i];
                if (s < 0 || !_e[i].StorageOn || _e[i].StorageCapacity <= 0)
                {
                    continue;
                }
                float flow = _subnets[s].StorageFlow;
                double share = 0;
                if (flow < 0f && !_e[i].StorageNoDischarge)
                {
                    int k = s * PriorityLevels + PoolOf(_e[i]);
                    float avail = (float)Math.Min(_e[i].StorageRate, _stored[i]);
                    if (_pool[k] > 0f && _poolUsed[k] > 0f)
                    {
                        share = -(double)_poolUsed[k] * (avail / _pool[k]);
                    }
                }
                else if (flow > 0f && !_e[i].StorageNoCharge && _chargeRoom[s] > 0f && PoolOf(_e[i]) < _chargeCut[s])
                {
                    float room = (float)Math.Max(0, Math.Min(_e[i].StorageRate, _e[i].StorageCapacity - _stored[i]));
                    share = (double)flow * (room / _chargeRoom[s]);
                }
                if (share != 0)
                {
                    _stored[i] = Math.Max(0, Math.Min(_e[i].StorageCapacity, _stored[i] + share));
                }
            }
            return Settle();
        }

        /// <summary>给每个电网的曲线记一个点（发电、需要、实际用电、储能 电·分钟，及各类别发电）。</summary>
        public void Sample()
        {
            SampleCount++;
            for (int s = 0; s < SubnetCount; s++)
            {
                PowerSubnetInfo info = _subnets[s];
                if (!_curves.TryGetValue(info.Serial, out PowerCurve c))
                {
                    c = new PowerCurve(_curveCapacity);
                    _curves[info.Serial] = c;
                }
                c.Add(info.Supply, info.Demand, info.Delivered, (float)(info.Stored / 60.0), _classSupply, s * MaxSourceClasses);
            }
        }

        // ── 查询（放置预览、拆除确认）──────────────────────────────────────────

        /// <summary>这个占地会接到哪个节点（-1 = 不在覆盖里）；distance = 到那个节点的距离。</summary>
        public int CoveringNode(int minX, int minY, int maxX, int maxY, out float distance)
        {
            var e = new PowerEntity { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            return NearestCovering(e, -1, out distance);
        }

        /// <summary>离这个占地最近的导电节点有多远（不管覆不覆盖；没有节点返回 +∞）。O(节点)，只在预览时调用。</summary>
        public float NearestNodeDistance(int minX, int minY, int maxX, int maxY)
        {
            var e = new PowerEntity { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            float best = float.PositiveInfinity;
            foreach (int j in _nodes)
            {
                best = Math.Min(best, DistSqToRect(CenterX(_e[j]), CenterY(_e[j]), e));
            }
            return float.IsPositiveInfinity(best) ? best : (float)Math.Sqrt(best);
        }

        /// <summary>在 (cx, cy) 放一个半径 radius 的节点会连到哪些电网（下标，去重，按下标升序）。最多看 maxNodes 个节点。</summary>
        public void SubnetsReachableFrom(float cx, float cy, float radius, List<int> into, int maxNodes = 64)
        {
            into.Clear();
            if (_nodes.Count == 0)
            {
                return;
            }
            float reach = Math.Max(radius, _maxSmallRadius);
            int r = (int)Math.Ceiling(reach / _bucket);
            int bx = FloorDiv(cx);
            int by = FloorDiv(cy);
            int looked = 0;
            foreach (int j in _bigNodes) // 大半径节点（核心）先看，不会因为上限被挤掉。
            {
                Reach(j);
            }
            for (int y = Math.Max(_gy0, by - r); y <= Math.Min(_gy0 + _gh - 1, by + r) && looked < maxNodes; y++)
            {
                for (int x = Math.Max(_gx0, bx - r); x <= Math.Min(_gx0 + _gw - 1, bx + r) && looked < maxNodes; x++)
                {
                    for (int j = _head[(y - _gy0) * _gw + (x - _gx0)]; j >= 0 && looked < maxNodes; j = _next[j])
                    {
                        Reach(j);
                    }
                }
            }
            into.Sort();
            return;

            void Reach(int j)
            {
                float lim = Math.Max(radius, _e[j].NodeRadius);
                float ddx = CenterX(_e[j]) - cx;
                float ddy = CenterY(_e[j]) - cy;
                if (ddx * ddx + ddy * ddy <= lim * lim + 1e-4f)
                {
                    looked++; // 上限按“够得着的节点”计数（防极端密集布局下预览卡顿）
                    if (!into.Contains(_subnetOf[j]))
                    {
                        into.Add(_subnetOf[j]);
                    }
                }
            }
        }

        /// <summary>在 (cx, cy) 放一个半径 radius 的节点能覆盖多少座用电 / 发电建筑，其中多少座现在没接入电网。O(实体)，只在预览时调用。</summary>
        public void CountCoverable(float cx, float cy, float radius, out int covered, out int nowUnconnected)
        {
            covered = 0;
            nowUnconnected = 0;
            float lim = radius * radius + 1e-4f;
            for (int i = 0; i < _n; i++)
            {
                if (_e[i].NodeRadius > 0f || !(_e[i].DemandOn || _e[i].SupplyOn || _e[i].StorageOn))
                {
                    continue;
                }
                if (DistSqToRect(cx, cy, _e[i]) <= lim)
                {
                    covered++;
                    if (_subnetOf[i] < 0)
                    {
                        nowUnconnected++;
                    }
                }
            }
        }

        /// <summary>
        /// 拆掉 / 失去节点 node 会怎样（拆除确认用）：它所在的电网断成几段（没断 = 1）、有几座正在用电或发电的建筑会失去电网连接。
        /// O(这个电网的节点 × 附近节点 + 实体 × 附近节点)，只在玩家点拆除时调用。
        /// </summary>
        public void RemovalImpact(int node, out int parts, out int orphans)
        {
            parts = 1;
            orphans = 0;
            if (node < 0 || node >= _n || _subnetOf[node] < 0 || !(_e[node].NodeRadius > 0f && _e[node].Conducts))
            {
                return;
            }
            int s = _subnetOf[node];
            var seen = new HashSet<int> { node };
            parts = 0;
            var queue = new Queue<int>();
            foreach (int start in _nodes)
            {
                if (_subnetOf[start] != s || seen.Contains(start))
                {
                    continue;
                }
                parts++;
                seen.Add(start);
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    CollectNeighbours(i, node, _nbr);
                    foreach (int j in _nbr)
                    {
                        if (seen.Add(j))
                        {
                            queue.Enqueue(j);
                        }
                    }
                }
            }
            for (int i = 0; i < _n; i++)
            {
                if (_subnetOf[i] != s || i == node || (_e[i].NodeRadius > 0f && _e[i].Conducts) || !(_e[i].DemandOn || _e[i].SupplyOn))
                {
                    continue;
                }
                if (NearestCovering(_e[i], node, out _) < 0)
                {
                    orphans++;
                }
            }
        }

        /// <summary>
        /// 一次拆掉一批实体（框选拆除）会怎样：按拆完后的整体拓扑算——受影响的电网里有几个会断成两段以上（splitSubnets）、
        /// 不在这批里的建筑有几座正在用电或发电却会失去电网连接（orphans）。两座互为备份的电塔一起拆也能查出断开。
        /// O(受影响电网的节点 × 附近节点 + 实体 × 附近节点)，只在玩家提交框选拆除时调用。
        /// </summary>
        public void RemovalImpact(IReadOnlyList<int> removed, out int splitSubnets, out int orphans)
        {
            splitSubnets = 0;
            orphans = 0;
            if (removed == null || removed.Count == 0)
            {
                return;
            }
            var set = new HashSet<int>();
            var subnets = new HashSet<int>();
            foreach (int r in removed)
            {
                if (r < 0 || r >= _n)
                {
                    continue;
                }
                set.Add(r);
                if (_subnetOf[r] >= 0 && _e[r].NodeRadius > 0f && _e[r].Conducts)
                {
                    subnets.Add(_subnetOf[r]);
                }
            }
            if (subnets.Count == 0)
            {
                return;
            }
            _excludeSet = set;
            try
            {
                var seen = new HashSet<int>();
                var queue = new Queue<int>();
                foreach (int s in subnets)
                {
                    int parts = 0;
                    foreach (int start in _nodes)
                    {
                        if (_subnetOf[start] != s || set.Contains(start) || seen.Contains(start))
                        {
                            continue;
                        }
                        parts++;
                        seen.Add(start);
                        queue.Enqueue(start);
                        while (queue.Count > 0)
                        {
                            int i = queue.Dequeue();
                            CollectNeighbours(i, -1, _nbr);
                            foreach (int j in _nbr)
                            {
                                if (seen.Add(j))
                                {
                                    queue.Enqueue(j);
                                }
                            }
                        }
                    }
                    if (parts > 1)
                    {
                        splitSubnets++;
                    }
                }
                for (int i = 0; i < _n; i++)
                {
                    if (set.Contains(i) || _subnetOf[i] < 0 || !subnets.Contains(_subnetOf[i]) || (_e[i].NodeRadius > 0f && _e[i].Conducts)
                        || !(_e[i].DemandOn || _e[i].SupplyOn))
                    {
                        continue;
                    }
                    if (NearestCovering(_e[i], -1, out _) < 0)
                    {
                        orphans++;
                    }
                }
            }
            finally
            {
                _excludeSet = null;
            }
        }

        // ── 存档 ─────────────────────────────────────────────────────────────

        public PowerSnapshot Serialize()
        {
            var snap = new PowerSnapshot { FormatVersion = FormatVersion, NextSerial = NextSerial };
            int m = SubnetCount;
            snap.SubnetSerials = new int[m];
            snap.SubnetAnchorKeys = new int[m];
            snap.CurveCounts = new int[m];
            int total = 0;
            for (int s = 0; s < m; s++)
            {
                snap.SubnetSerials[s] = _subnets[s].Serial;
                snap.SubnetAnchorKeys[s] = _subnets[s].FirstNode >= 0 ? _e[_subnets[s].FirstNode].Key : VirtualHubKey;
                if (_curves.TryGetValue(_subnets[s].Serial, out PowerCurve c))
                {
                    snap.CurveCounts[s] = c.Count;
                    total += c.Count;
                }
            }
            snap.CurveSupply = new float[total];
            snap.CurveDemand = new float[total];
            snap.CurveDelivered = new float[total];
            snap.CurveStored = new float[total];
            snap.CurveClassSupply = new float[total * MaxSourceClasses];
            int w = 0;
            for (int s = 0; s < m; s++)
            {
                if (!_curves.TryGetValue(_subnets[s].Serial, out PowerCurve c))
                {
                    continue;
                }
                for (int i = 0; i < c.Count; i++, w++)
                {
                    c.Get(i, out snap.CurveSupply[w], out snap.CurveDemand[w], out snap.CurveDelivered[w], out snap.CurveStored[w]);
                    c.GetClasses(i, snap.CurveClassSupply, w * MaxSourceClasses);
                }
            }
            var keys = new List<int>();
            var vals = new List<double>();
            for (int i = 0; i < _n; i++)
            {
                if (_e[i].StorageCapacity > 0 && _e[i].Key != VirtualHubKey)
                {
                    keys.Add(_e[i].Key);
                    vals.Add(_stored[i]);
                }
            }
            snap.StorageKeys = keys.ToArray();
            snap.StorageStored = vals.ToArray();
            return snap;
        }

        /// <summary>
        /// 从快照恢复（在第一次 <see cref="Rebuild"/> 之前调用）：锚点节点 → 电网编号、曲线、按键的储能存量、下一个编号。
        /// 形状不对返回 false 并给原因（调用方保留原始数据不覆盖）。坏的单条（负存量、NaN）丢弃并计数。
        /// </summary>
        public bool Restore(PowerSnapshot snap, out string error, out int dropped)
        {
            error = null;
            dropped = 0;
            if (snap == null)
            {
                error = "no-snapshot";
                return false;
            }
            // 格式 1（FG3-LOG-06，没有各类别发电）与 2 都认；更新的格式整体拒绝（调用方保留原始数据不覆盖）。
            if (snap.FormatVersion != FormatVersion && snap.FormatVersion != 1)
            {
                error = $"format {snap.FormatVersion} != {FormatVersion}";
                return false;
            }
            int m = snap.SubnetSerials?.Length ?? 0;
            if ((snap.SubnetAnchorKeys?.Length ?? 0) != m || (snap.CurveCounts?.Length ?? 0) != m
                || (snap.StorageKeys?.Length ?? 0) != (snap.StorageStored?.Length ?? 0))
            {
                error = "shape";
                return false;
            }
            int total = 0;
            for (int s = 0; s < m; s++)
            {
                if (snap.CurveCounts[s] < 0)
                {
                    error = "shape";
                    return false;
                }
                total += snap.CurveCounts[s];
            }
            if ((snap.CurveSupply?.Length ?? 0) != total || (snap.CurveDemand?.Length ?? 0) != total
                || (snap.CurveDelivered?.Length ?? 0) != total || (snap.CurveStored?.Length ?? 0) != total)
            {
                error = "shape";
                return false;
            }
            float[] classes = snap.FormatVersion >= 2 ? snap.CurveClassSupply : null;
            if (classes != null && classes.Length != total * MaxSourceClasses)
            {
                error = "shape";
                return false;
            }
            _nodeSerial.Clear();
            _curves.Clear();
            _storedByKey.Clear();
            int maxSerial = 0;
            int r = 0;
            for (int s = 0; s < m; s++)
            {
                int serial = snap.SubnetSerials[s];
                int count = snap.CurveCounts[s];
                if (serial <= 0 || _curves.ContainsKey(serial))
                {
                    dropped++;
                    r += count;
                    continue;
                }
                maxSerial = Math.Max(maxSerial, serial);
                _nodeSerial[snap.SubnetAnchorKeys[s]] = serial;
                var c = new PowerCurve(_curveCapacity);
                int skip = Math.Max(0, count - _curveCapacity);
                for (int i = 0; i < count; i++, r++)
                {
                    if (i < skip)
                    {
                        continue;
                    }
                    float a = snap.CurveSupply[r], b = snap.CurveDemand[r], d = snap.CurveDelivered[r], st = snap.CurveStored[r];
                    if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(d) || float.IsNaN(st) || HasNaN(classes, r * MaxSourceClasses))
                    {
                        dropped++;
                        continue;
                    }
                    c.Add(a, b, d, st, classes, r * MaxSourceClasses);
                }
                _curves[serial] = c;
            }
            for (int i = 0; i < snap.StorageKeys.Length; i++)
            {
                double v = snap.StorageStored[i];
                if (double.IsNaN(v) || v < 0)
                {
                    dropped++;
                    continue;
                }
                _storedByKey[snap.StorageKeys[i]] = v;
            }
            NextSerial = Math.Max(Math.Max(1, snap.NextSerial), maxSerial + 1);
            return true;
        }

        private static bool HasNaN(float[] a, int offset)
        {
            if (a == null)
            {
                return false;
            }
            for (int c = 0; c < MaxSourceClasses; c++)
            {
                if (float.IsNaN(a[offset + c]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>确定性指纹（自检比较“存读档 / 倍速 / 观察”一致性）：电网编号、汇总、每个实体的供电结果与存量、曲线、各类别发电与负荷。</summary>
        public string Fingerprint()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("n=").Append(_n).Append(" m=").Append(SubnetCount).Append(" next=").Append(NextSerial);
            for (int s = 0; s < SubnetCount; s++)
            {
                PowerSubnetInfo info = _subnets[s];
                sb.Append(" |#").Append(info.Serial).Append(' ').Append(info.Supply.ToString("R")).Append('/').Append(info.Demand.ToString("R"))
                    .Append('/').Append(info.Delivered.ToString("R")).Append(" st=").Append(info.Stored.ToString("R"))
                    .Append(" n").Append(info.Nodes).Append(" c").Append(info.Consumers).Append(" b").Append(info.Brownouts)
                    .Append(" load=").Append(info.DispatchLoad.ToString("R")).Append(" flow=").Append(info.StorageFlow.ToString("R"));
                for (int k = 0; k < MaxSourceClasses; k++)
                {
                    float v = _classSupply[s * MaxSourceClasses + k];
                    if (v != 0f)
                    {
                        sb.Append(" k").Append(k).Append('=').Append(v.ToString("R"));
                    }
                }
                if (_curves.TryGetValue(info.Serial, out PowerCurve c))
                {
                    sb.Append(" curve").Append(c.Count);
                    if (c.Count > 0)
                    {
                        c.Get(c.Count - 1, out float a, out float b, out float d, out float st);
                        sb.Append('[').Append(a.ToString("R")).Append(',').Append(b.ToString("R")).Append(',').Append(d.ToString("R")).Append(',').Append(st.ToString("R")).Append(']');
                    }
                }
            }
            sb.Append(" |use=");
            for (int i = 0; i < _n; i++)
            {
                sb.Append((int)_use[i]);
                if (_e[i].StorageCapacity > 0)
                {
                    sb.Append('(').Append(_stored[i].ToString("R")).Append(')');
                }
            }
            return sb.ToString();
        }

        private void Ensure(int n)
        {
            if (_e.Length >= n)
            {
                return;
            }
            int cap = Math.Max(n, Math.Max(16, _e.Length * 2));
            Array.Resize(ref _e, cap);
            Array.Resize(ref _subnetOf, cap);
            Array.Resize(ref _use, cap);
            Array.Resize(ref _link, cap);
            Array.Resize(ref _coverNode, cap);
            Array.Resize(ref _stored, cap);
            Array.Resize(ref _uf, cap);
        }
    }
}
