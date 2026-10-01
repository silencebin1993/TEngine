using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG3-LOG-05（FG03 FGR-LOG-040～045；FGR-LOG-090 物流内核；FG14 FGR-ARC-004）：管线与流体内核（AOT，BinGames.Sim）。
    ///
    /// 模型（FGR-LOG-042“不做压力模拟”）：
    /// - 相连的管线件（管线、泵、储罐）组成一个流体网络；**一个网络只能有一种流体**（FGR-LOG-040），把两种流体的网络接在一起的编辑被拒绝（<see cref="PipeResult.FluidConflict"/>）。
    /// - 阀门不属于任何网络：它把后方（上游）网络和前方（下游）网络单向连起来，可以手动关闭（FGR-LOG-043）。
    /// - 每个内核步，每个网络按“总供给和总需求”结算：吞吐上限 = 网络里<b>最低等级</b>管线的吞吐；供给不足时按消费者优先级 1～4 分配
    ///   （同一优先级按需求比例）；供给来源依次是上游阀门、泵、储罐（只出 / 双向）；有余量时灌进双向储罐（缓冲）。
    /// - 泵按固定速率抽取，没人要就空转（不抽），不会凭空溢出。
    ///
    /// 复杂度与“拓扑变化时才重算”（参照电网子网）：
    /// - 编辑（放 / 拆 / 转阀门 / 挂消费者）只标脏；下一次读数或步进前重算一次网络（BFS，O(格数)），稳态每步不重算。
    /// - 每步结算 O(网络数 + 泵 + 储罐 + 阀门 + 消费者)，与管线格数无关；逐格循环只在重算、冲洗、流体认定（网络第一次有流体）时发生，
    ///   全部在本 AOT 程序集里（热更层每帧 / 每步只调 <see cref="Step"/> 和 O(1) 的读数）。
    /// - 每步零托管分配（数组预分配，按容量翻倍增长只在编辑时）。
    ///
    /// 体积用整数毫升，每步配额 = 按绝对步序号取整差分（<see cref="Budget"/>），结果逐位确定，读档后接着跑与一直跑一致。
    /// 结冰（FGR-LOG-045）本 Story 只预留游戏时钟接口：每个网络记“最近一次有流动的步”（<see cref="PipeNetInfo.LastFlowStep"/>，进存档），
    /// 结冰规则由 FG7-ENV-03（寒潮）接入。
    /// </summary>
    public sealed class PipeKernel
    {
        private readonly PipeConfig _config;

        // ── 每格（按铺设顺序，删除时保序压紧）──
        private int _n;
        private int[] _x = new int[64];
        private int[] _y = new int[64];
        private byte[] _kind = new byte[64];
        private byte[] _tier = new byte[64];
        private byte[] _dir = new byte[64];
        private byte[] _fluid = new byte[64];
        private byte[] _mode = new byte[64];
        private byte[] _prio = new byte[64];
        private byte[] _open = new byte[64];
        private byte[] _bufFluid = new byte[64];
        private long[] _stock = new long[64];
        private long[] _buf = new long[64];
        private long[] _pumpTotal = new long[64];
        private long[] _seed = new long[64];
        // 派生（重算时写）
        private int[] _net = new int[64];
        private int[] _oldNet = new int[64];
        private int[] _mask = new int[64];
        private int[] _valA = new int[64];
        private int[] _valB = new int[64];
        /// <summary>FG4-ECO-04（FG-GAP-082）：地下管线口配对的另一口（-1 = 没配对；重算时写）。</summary>
        private int[] _partner = new int[64];
        // 每步读数
        private long[] _stepIn = new long[64];
        private long[] _stepOut = new long[64];
        private long[] _bufStart = new long[64];
        private byte[] _mismatch = new byte[64];
        private readonly Dictionary<long, int> _lookup = new Dictionary<long, int>(64);

        // ── 外部消费者 ──
        private int _cn;
        private int[] _cId = new int[8];
        private int[] _cX = new int[8];
        private int[] _cY = new int[8];
        private byte[] _cFluid = new byte[8];
        private int[] _cLpm = new int[8];
        private byte[] _cPrio = new byte[8];
        private int[] _cNet = new int[8];
        private long[] _cDemand = new long[8];
        private long[] _cGot = new long[8];
        private long[] _cTotal = new long[8];
        // FG4-ECO-02：带缓存的消费者（建筑的流体输入口）——送达的流体进缓存，缓存满了就不再要；容量 0 = 不带缓存（送达即消耗，例如废液池销毁）。
        private long[] _cBuf = new long[8];
        private long[] _cCap = new long[8];
        private int _nextConsumerId = 1;

        // ── 外部供给者（FG4-ECO-02：建筑的流体输出口）──
        // 挂在口外那一格的管线件上；建筑把产出的流体放进它的存量（有上限），网络按需取用（取用顺序：上游阀门 → 泵 → 供给者 → 储罐）。
        private int _rn;
        private int[] _rId = new int[8];
        private int[] _rX = new int[8];
        private int[] _rY = new int[8];
        private byte[] _rFluid = new byte[8];
        private long[] _rStock = new long[8];
        private long[] _rCap = new long[8];
        private int[] _rNet = new int[8];
        private long[] _rOut = new long[8];
        private long[] _rTotal = new long[8];
        private int _nextProducerId = 1;

        // ── 网络（重算时写）──
        private int _netCount;
        private byte[] _nFluid = new byte[16];
        private int[] _nCells = new int[16];
        private int[] _nPipe = new int[16];
        private int[] _nMinTier = new int[16];
        private int[] _nMinCount = new int[16];
        private byte[] _nMixed = new byte[16];
        private int[] _nBx = new int[16];
        private int[] _nBy = new int[16];
        private long[] _nLastFlow = new long[16];
        private long[] _nLastFlowOld = new long[16];
        private long[] _nSupply = new long[16];
        private long[] _nDemand = new long[16];
        private long[] _nMoved = new long[16];
        private long[] _nFill = new long[16];
        private long[] _nUnmet = new long[16];
        private int[] _nIssues = new int[16];
        // 读数窗口（1 游戏秒 = StepHz 步的合计）：每步配额按步序号取整，单步读数会在 499.2 / 500.4 之间跳；悬停与面板按最近一整秒的合计换算，
        // 与设计值一致（500 升/分钟就显示 500）。重算时清零，窗口没走满之前按已走的步数折算。
        private long[] _aSupply = new long[16];
        private long[] _aDemand = new long[16];
        private long[] _aMoved = new long[16];
        private long[] _aFill = new long[16];
        private long[] _aUnmet = new long[16];
        private int[] _aCount = new int[16];
        private long[] _pSupply = new long[16];
        private long[] _pDemand = new long[16];
        private long[] _pMoved = new long[16];
        private long[] _pFill = new long[16];
        private long[] _pUnmet = new long[16];
        private byte[] _pValid = new byte[16];
        /// <summary>FG3-LOG-09：已发布的窗口实际累计了几步（按绝对步序号对齐整秒发布，重建后的第一个窗口可能不满一秒）。</summary>
        private int[] _pCount = new int[16];
        // CSR：每个网络的格 / 泵 / 储罐 / 上游阀门（阀门前方 = 本网络）/ 下游阀门（阀门后方 = 本网络）/ 消费者
        private readonly Csr _cellsOf = new Csr();
        private readonly Csr _pumpsOf = new Csr();
        private readonly Csr _tanksOf = new Csr();
        private readonly Csr _valveInOf = new Csr();
        private readonly Csr _valveOutOf = new Csr();
        private readonly Csr _consOf = new Csr();
        private readonly Csr _prodOf = new Csr();
        private int[] _valves = new int[8];
        private int _valveCount;
        private int[] _tanks = new int[8];
        private int _tankCount;
        private int[] _queue = new int[64];

        // 分配用的临时数组（按需翻倍，稳态不分配）
        private long[] _dAmt = new long[32];
        private long[] _dGive = new long[32];
        private int[] _dRef = new int[32];
        private byte[] _dType = new byte[32];
        private byte[] _dPrio = new byte[32];

        private bool _dirty = true;
        private int _noSupplyCount;
        private readonly Stopwatch _watch = new Stopwatch();

        public PipeKernel(PipeConfig config)
        {
            if (!config.IsValid(out string reason))
            {
                throw new ArgumentException("PipeConfig 非法：" + reason);
            }
            _config = config;
        }

        // ── 属性 ───────────────────────────────────────────────────────────────

        public PipeConfig Config => _config;
        public int CellCount
        {
            get
            {
                Compact();
                return _n;
            }
        }
        public long StepIndex { get; private set; }
        /// <summary>渲染 / 格网层 / 读数的版本：编辑、流体认定、冲洗、储罐液位跨档、阀门状态变化时 +1。</summary>
        public int Revision { get; private set; }
        /// <summary>网络重算的次数（“拓扑变化时才重算”的证据：稳态步进不增加）。</summary>
        public int RebuildCount { get; private set; }
        public int ConsumerCount => _cn;
        /// <summary>FG4-ECO-02：外部供给者（建筑流体输出口）的个数。</summary>
        public int ProducerCount => _rn;
        /// <summary>FG4-ECO-02：外部供给者累计送出的流体（毫升）。</summary>
        public long TotalProducedOutMl { get; private set; }
        public long TotalPumpedMl { get; private set; }
        public long TotalDeliveredMl { get; private set; }
        public long TotalFlushedMl { get; private set; }
        /// <summary>拆除储罐 / 阀门时随之排空的流体（毫升）。</summary>
        public long TotalRemovedMl { get; private set; }
        /// <summary>读档或重算时发现同一网络里有两种流体（只可能来自损坏的存档）并按最小编号统一的次数。</summary>
        public int FluidConflictsResolved { get; private set; }
        /// <summary>读档时丢弃的非法记录数。</summary>
        public int DroppedOnLoad { get; private set; }
        /// <summary>读档时储罐存量超过当前容量、被夹到容量而倒掉的流体（毫升；调低储罐容量后读老存档）。</summary>
        public long ClampedOnLoadMl { get; private set; }
        /// <summary>最近一步耗时（毫秒）。</summary>
        public double LastStepMs { get; private set; }
        public double LastRebuildMs { get; private set; }
        /// <summary>最近一步“有需求却什么都没送到”的网络数（热更层 O(1) 读它发引导钩子）。</summary>
        public int LastNoSupplyNetworks { get; private set; }

        public int NetworkCount
        {
            get
            {
                Rebuild();
                return _netCount;
            }
        }

        /// <summary>每一步的配额（毫升）：速率 <paramref name="litersPerMinute"/> 在第 <paramref name="step"/> 步能走多少。按绝对步序号取整差分，
        /// 长期累计精确等于 升 / 分钟 × 分钟数（例如 300 升 / 分钟 在 20 Hz 下每步恰好 250 毫升）。</summary>
        public long Budget(int litersPerMinute, long step)
        {
            if (litersPerMinute <= 0)
            {
                return 0;
            }
            long num = litersPerMinute * 1000L;
            long den = 60L * _config.StepHz;
            long q = num / den;
            long r = num % den;
            long m = step % den;
            if (m < 0)
            {
                m += den;
            }
            return q + ((m + 1) * r / den - m * r / den);
        }

        private static long Key(int x, int y) => BeltDirs.Key(x, y);

        public bool HasCell(int x, int y) => _lookup.ContainsKey(Key(x, y));

        public bool TryGetKind(int x, int y, out PipePieceKind kind, out int tier)
        {
            if (_lookup.TryGetValue(Key(x, y), out int i))
            {
                kind = (PipePieceKind)_kind[i];
                tier = _tier[i];
                return true;
            }
            kind = PipePieceKind.Pipe;
            tier = 0;
            return false;
        }

        /// <summary>第 <paramref name="i"/> 格（铺设顺序）的坐标与种类（格网层同步、自检用）。</summary>
        public void GetCellAt(int i, out int x, out int y, out PipePieceKind kind, out int tier)
        {
            Compact();
            x = _x[i];
            y = _y[i];
            kind = (PipePieceKind)_kind[i];
            tier = _tier[i];
        }

        // ── 连接规则 ─────────────────────────────────────────────────────────────

        /// <summary>第 i 格朝方向 d 有没有接口：管线 / 泵 / 储罐四面都有；阀门只有前后两面；地下管线口只有地面一侧（方向的反面）。</summary>
        private bool Opens(int i, int d) => OpensAs((PipePieceKind)_kind[i], _dir[i], d);

        private static bool OpensAs(PipePieceKind kind, int dir, int d) =>
            kind == PipePieceKind.Valve ? d == dir || d == BeltDirs.Opposite(dir)
            : kind == PipePieceKind.Underground ? d == BeltDirs.Opposite(dir)
            : true;

        /// <summary>
        /// FG4-ECO-04（FG-GAP-082）：(x, y) 朝 <paramref name="dir"/> 的地下管线口会和哪一件配对——沿方向跨度以内的第一件地下管线口，
        /// 它要朝回来（方向相反）、且它往回看到的第一件也是这里（互为第一件才配对）。<paramref name="ignore"/> 下标的格当作不存在（拆除预判用）。
        /// 返回配对件的下标；没有 = -1。O(跨度)。
        /// </summary>
        private int FindPartner(int x, int y, int dir, int tier, int ignore = -1)
        {
            int span = _config.UndergroundSpan(tier);
            int dx = BeltDirs.Dx(dir), dy = BeltDirs.Dy(dir);
            for (int k = 1; k <= span + 1; k++)
            {
                if (!_lookup.TryGetValue(Key(x + dx * k, y + dy * k), out int j) || j == ignore || _kind[j] != (byte)PipePieceKind.Underground)
                {
                    continue;
                }
                // 第一件地下管线口：朝回来才配对（朝同一方向 = 这一段被它挡住，不配对）。等级不同的也能配（跨度按这一口的等级算）。
                return _dir[j] == BeltDirs.Opposite(dir) ? j : -1;
            }
            return -1;
        }

        /// <summary>重算时给每个地下管线口找配对（互为第一件）。O(地下管线口 × 跨度)。</summary>
        private void PairUnderground()
        {
            for (int i = 0; i < _n; i++)
            {
                _partner[i] = -1;
            }
            for (int i = 0; i < _n; i++)
            {
                if (_kind[i] != (byte)PipePieceKind.Underground || _partner[i] >= 0)
                {
                    continue;
                }
                int j = FindPartner(_x[i], _y[i], _dir[i], _tier[i]);
                if (j >= 0 && FindPartner(_x[j], _y[j], _dir[j], _tier[j]) == i)
                {
                    _partner[i] = j;
                    _partner[j] = i;
                }
            }
        }

        /// <summary>FG4-ECO-04：放在 (x, y)、朝 <paramref name="dir"/> 的地下管线口会接上的地下那一侧网络的流体（没配对 / 没流体 = 0）。</summary>
        /// <summary>
        /// 还没放下的地下口（x, y）放下后真会配对的那一口（互为第一件：从它往回看第一件就是新口，且新口在它自己那一等级的跨度内）。
        /// 新口和 j 之间没有别的地下口（j 是新口这一侧的第一件），所以只差 j 的跨度：T1 口看不到 9～12 格外的 T2 新口（修复轮 P2）。
        /// </summary>
        private int FindNewPartner(int x, int y, int dir, int tier)
        {
            int j = FindPartner(x, y, dir, tier);
            if (j < 0)
            {
                return -1;
            }
            int dist = Math.Abs(_x[j] - x) + Math.Abs(_y[j] - y);
            return dist <= _config.UndergroundSpan(_tier[j]) + 1 ? j : -1;
        }

        private int TunnelFluid(int x, int y, int dir, int tier)
        {
            int j = FindNewPartner(x, y, dir, tier);
            if (j < 0)
            {
                return 0;
            }
            // 新口插在一对已配对的口之间时，对面那一口改和新口配对：新口接上的是对面那一口所在的网络。
            int net = _net[j];
            return net >= 0 && net < _netCount ? _nFluid[net] : _fluid[j];
        }

        private int Neighbor(int i, int d) =>
            _lookup.TryGetValue(Key(_x[i] + BeltDirs.Dx(d), _y[i] + BeltDirs.Dy(d)), out int j) ? j : -1;

        // ── 编辑 ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 能不能在 (x, y) 放一件（不改状态；规划预览与 <see cref="Place"/> 同一套）：坐标、种类、等级、方向、流体来源合法，这一格没被占，
        /// 且不会把两种不同流体接在一起——管线 / 泵 / 储罐：相连的各网络的流体（加上泵自己的水源流体）最多一种；阀门：后方与前方网络的流体相同或有一侧没有流体。
        /// 冲突时 <paramref name="fluidA"/> / <paramref name="fluidB"/> 给出两种流体（原因文本用）。
        /// </summary>
        public PipeResult CheckPlace(int x, int y, PipePieceKind kind, int tier, int dir, int sourceFluid, out int fluidA, out int fluidB)
        {
            fluidA = 0;
            fluidB = 0;
            if (Math.Abs(x) > PipeConst.CoordLimit || Math.Abs(y) > PipeConst.CoordLimit)
            {
                return PipeResult.OutOfRange;
            }
            if (kind > PipePieceKind.Underground)
            {
                return PipeResult.InvalidArgument;
            }
            if (tier < 0 || tier >= PipeConst.TierCount)
            {
                return PipeResult.InvalidTier;
            }
            if (dir < 0 || dir > 3)
            {
                return PipeResult.InvalidDirection;
            }
            if (kind == PipePieceKind.Pump ? sourceFluid < 1 || sourceFluid > PipeConst.MaxFluid : sourceFluid != 0)
            {
                return PipeResult.InvalidFluid;
            }
            if (_lookup.ContainsKey(Key(x, y)))
            {
                return PipeResult.Occupied;
            }
            Rebuild();
            if (kind == PipePieceKind.Valve)
            {
                // 阀门不能前后直接串联：阀门不属于任何网络，两个阀门对接时中间没有网络，流体过不去（修复轮 P2）。
                for (int side = 0; side < 2; side++)
                {
                    int d = side == 0 ? dir : BeltDirs.Opposite(dir);
                    if (_lookup.TryGetValue(Key(x + BeltDirs.Dx(d), y + BeltDirs.Dy(d)), out int j)
                        && _kind[j] == (byte)PipePieceKind.Valve && Opens(j, BeltDirs.Opposite(d)))
                    {
                        return PipeResult.ValveChained;
                    }
                }
                int a = SideFluid(x, y, BeltDirs.Opposite(dir));
                int b = SideFluid(x, y, dir);
                if (a != 0 && b != 0 && a != b)
                {
                    fluidA = a;
                    fluidB = b;
                    return PipeResult.FluidConflict;
                }
                return PipeResult.Ok;
            }
            int seen = sourceFluid;
            for (int d = 0; d < 5; d++)
            {
                // d = 4：地下管线口地下那一侧（配对口所在的网络）。
                int f = d == 4 ? (kind == PipePieceKind.Underground ? TunnelFluid(x, y, dir, tier) : 0)
                    : OpensAs(kind, dir, d) ? SideFluid(x, y, d) : 0;
                if (f == 0)
                {
                    continue;
                }
                if (seen == 0)
                {
                    seen = f;
                }
                else if (f != seen)
                {
                    fluidA = seen;
                    fluidB = f;
                    return PipeResult.FluidConflict;
                }
            }
            return PipeResult.Ok;
        }

        /// <summary>放在 (x, y) 的件朝 d 那一侧相连的网络的流体（没接 / 没有流体 = 0；接在阀门开口上 = 阀门另一侧网络或缓冲的流体）。</summary>
        private int SideFluid(int x, int y, int d)
        {
            if (!_lookup.TryGetValue(Key(x + BeltDirs.Dx(d), y + BeltDirs.Dy(d)), out int j) || !Opens(j, BeltDirs.Opposite(d)))
            {
                return 0;
            }
            if (_kind[j] == (byte)PipePieceKind.Valve)
            {
                // 接在阀门的开口面上：新件会经阀门与阀门另一侧的网络同流体（阀门两侧流体必须相同），把那一侧的流体、
                // 或缓冲里还有的流体算进冲突检查（修复轮 P2：之前直接当“没接”，会放行把阀门前后接成两种流体）。
                int far = SideNet(j, d);
                if (far >= 0 && far < _netCount && _nFluid[far] != 0)
                {
                    return _nFluid[far];
                }
                return _buf[j] > 0 ? _bufFluid[j] : 0;
            }
            int net = _net[j];
            return net >= 0 && net < _netCount ? _nFluid[net] : _fluid[j];
        }

        /// <summary>
        /// 规划一整段路径时的流体检查（FGR-LOG-040）：把 (x, y) 处一件新件会相连的各网络的流体加进 <paramref name="fluids"/>（去重）。
        /// 路径上各格加完后，列表里多于一种流体 = 这段路径会把不同流体接在一起。
        /// </summary>
        /// <param name="tunnel">地下管线口：是否也查地下那一侧（配对口所在的网络）。粘贴时配对口也在这次粘贴里的，由调用方并组，传 false。</param>
        public void AddAdjacentFluids(int x, int y, PipePieceKind kind, int dir, List<int> fluids, int tier = 0, bool tunnel = true)
        {
            Rebuild();
            for (int d = 0; d < 4; d++)
            {
                if (!OpensAs(kind, dir, d) || kind == PipePieceKind.Valve)
                {
                    continue;
                }
                int f = SideFluid(x, y, d);
                if (f != 0 && !fluids.Contains(f))
                {
                    fluids.Add(f);
                }
            }
            if (kind == PipePieceKind.Underground && tunnel)
            {
                int t = TunnelFluid(x, y, dir, tier);
                if (t != 0 && !fluids.Contains(t))
                {
                    fluids.Add(t);
                }
            }
        }

        /// <summary>放一件（见 <see cref="CheckPlace"/>）。泵的 <paramref name="sourceFluid"/> = 它所在水源 / 油井的流体；其余件为 0。</summary>
        public PipeResult Place(int x, int y, PipePieceKind kind, int tier, int dir, int sourceFluid)
        {
            PipeResult r = CheckPlace(x, y, kind, tier, dir, sourceFluid, out _, out _);
            if (r != PipeResult.Ok)
            {
                return r;
            }
            EnsureCells(_n + 1);
            int i = _n++;
            _x[i] = x;
            _y[i] = y;
            _kind[i] = (byte)kind;
            _tier[i] = (byte)(kind == PipePieceKind.Pipe || kind == PipePieceKind.Underground ? tier : 0);
            _dir[i] = (byte)(kind == PipePieceKind.Valve || kind == PipePieceKind.Underground ? dir : 0);
            _fluid[i] = (byte)sourceFluid;
            _mode[i] = (byte)PipeTankMode.Both;
            _prio[i] = (byte)PipeConst.TankDefaultPriority;
            _open[i] = 1;
            _bufFluid[i] = 0;
            _stock[i] = 0;
            _buf[i] = 0;
            _pumpTotal[i] = 0;
            _seed[i] = -1;
            _net[i] = -1;
            _mask[i] = 0;
            _valA[i] = -1;
            _valB[i] = -1;
            _stepIn[i] = 0;
            _stepOut[i] = 0;
            _mismatch[i] = 0;
            _lookup[Key(x, y)] = i;
            MarkDirty();
            return PipeResult.Ok;
        }

        /// <summary>
        /// FG4-ECO-04 修复轮（P1，FGR-LOG-047 / FGR-LOG-040）：拆掉 (x, y) 这一件会不会把两种流体接在一起。
        /// 只有地下管线口会挡住别的地下口配对（<see cref="FindPartner"/> 只认第一件地下口），所以拆一口可能让被它隔开的两口重新配对；
        /// 两口所在网络的流体不同（都不为 0）时拒绝，<paramref name="fluidA"/> / <paramref name="fluidB"/> = 两边的流体。
        /// 不是地下口：O(1) 直接放行；是地下口：沿四个方向各找第一件地下口（O(跨度)），真有新配对时才重算一次网络取流体。
        /// </summary>
        public PipeResult CheckRemove(int x, int y, out int fluidA, out int fluidB)
        {
            fluidA = 0;
            fluidB = 0;
            if (!_lookup.TryGetValue(Key(x, y), out int i))
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Underground || !RelinksOnRemove(i, false, out _, out _))
            {
                return PipeResult.Ok;
            }
            // 网络流体要按最新拓扑读：重算会压紧数组、平移下标，重新查一次这一格。
            Rebuild();
            i = _lookup[Key(x, y)];
            return RelinksOnRemove(i, true, out fluidA, out fluidB) ? PipeResult.FluidConflict : PipeResult.Ok;
        }

        /// <summary>
        /// 拆掉第 <paramref name="i"/> 件（地下口）后会新配对的地下口对：沿四个方向找第一件地下口 q，q 朝回来且 i 在它的跨度内（i 正挡着它）时，
        /// 按“拆掉 i 之后”（ignore = i）重新找 q 的配对 j，并要求互为第一件。<paramref name="compareFluids"/> = false 时只回答“有没有新配对”；
        /// = true 时把全部新配对一起看：按网络并查集合并后，同一连通块里有两种流体（都不为 0）才返回 true（十字交叉处拆口经空网络串接也算）。
        /// </summary>
        private bool RelinksOnRemove(int i, bool compareFluids, out int fluidA, out int fluidB)
        {
            fluidA = 0;
            fluidB = 0;
            int reach = 0;
            for (int t = 0; t < PipeConst.TierCount; t++)
            {
                reach = Math.Max(reach, _config.UndergroundSpan(t) + 1);
            }
            // 复审 P1：拆一口最多同时放出 x 轴、y 轴两对新配对，两对可能经过同一个没有流体的网络串成“水 → 空网 → 原油”。
            // 所以不能逐对比较：把每对新配对两端所在的网络当节点、新配对当边，做一次小并查集（最多 8 个节点），
            // 合并后同一连通块里出现两种流体才算冲突。用拆前的网络划分是偏保守的：拆掉 i 只会让网络变小、不会变大。
            int pairCount = 0;
            for (int d = 0; d < 4; d++)
            {
                int dx = BeltDirs.Dx(d), dy = BeltDirs.Dy(d);
                for (int k = 1; k <= reach; k++)
                {
                    if (!_lookup.TryGetValue(Key(_x[i] + dx * k, _y[i] + dy * k), out int q) || _kind[q] != (byte)PipePieceKind.Underground)
                    {
                        continue;
                    }
                    // 这个方向的第一件地下口：它朝 i 找、且 i 在它的跨度内，才是被 i 挡住的那一口；否则更远的口被它自己挡着，与 i 无关。
                    if (_dir[q] == BeltDirs.Opposite(d) && k <= _config.UndergroundSpan(_tier[q]) + 1)
                    {
                        int j = FindPartner(_x[q], _y[q], _dir[q], _tier[q], i);
                        if (j >= 0 && FindPartner(_x[j], _y[j], _dir[j], _tier[j], i) == q)
                        {
                            if (!compareFluids)
                            {
                                return true;
                            }
                            _relinkA[pairCount] = q;
                            _relinkB[pairCount] = j;
                            pairCount++;
                        }
                    }
                    break;
                }
            }
            return compareFluids && pairCount > 0 && RelinkedFluidConflict(pairCount, out fluidA, out fluidB);
        }

        // 拆一口最多 4 个方向各一对（同一对会从两头各数一次），节点最多 8 个；预分配，避免拆除路径上产生垃圾。
        private readonly int[] _relinkA = new int[4];
        private readonly int[] _relinkB = new int[4];
        private readonly int[] _relinkNode = new int[8];
        private readonly int[] _relinkParent = new int[8];
        private readonly int[] _relinkFluid = new int[8];

        /// <summary>
        /// 新配对（<see cref="_relinkA"/>[p] ↔ <see cref="_relinkB"/>[p]）把两端所在网络连起来之后，同一连通块里是否有两种流体。
        /// 节点 = 网络编号（还没分网络的格子用 -(格下标 + 1) 当独立节点）；O(配对数²)，配对数 ≤ 4。
        /// </summary>
        private bool RelinkedFluidConflict(int pairCount, out int fluidA, out int fluidB)
        {
            fluidA = 0;
            fluidB = 0;
            int nodes = 0;
            for (int p = 0; p < pairCount; p++)
            {
                int a = RelinkNode(_relinkA[p], ref nodes);
                int b = RelinkNode(_relinkB[p], ref nodes);
                int ra = RelinkRoot(a), rb = RelinkRoot(b);
                if (ra != rb)
                {
                    _relinkParent[ra] = rb;
                }
            }
            for (int n = 0; n < nodes; n++)
            {
                int f = _relinkFluid[n];
                if (f == 0)
                {
                    continue;
                }
                int root = RelinkRoot(n);
                for (int m = n + 1; m < nodes; m++)
                {
                    int g = _relinkFluid[m];
                    if (g != 0 && g != f && RelinkRoot(m) == root)
                    {
                        fluidA = f;
                        fluidB = g;
                        return true;
                    }
                }
            }
            return false;
        }

        private int RelinkNode(int cell, ref int nodes)
        {
            int net = _net[cell];
            int id = net >= 0 && net < _netCount ? net : -(cell + 1);
            for (int n = 0; n < nodes; n++)
            {
                if (_relinkNode[n] == id)
                {
                    return n;
                }
            }
            _relinkNode[nodes] = id;
            _relinkParent[nodes] = nodes;
            _relinkFluid[nodes] = CellFluid(cell);
            return nodes++;
        }

        private int RelinkRoot(int n)
        {
            while (_relinkParent[n] != n)
            {
                n = _relinkParent[n];
            }
            return n;
        }

        /// <summary>第 <paramref name="i"/> 格所在网络的流体（还没分网络时取这一格自己的流体）。</summary>
        private int CellFluid(int i)
        {
            int net = _net[i];
            return net >= 0 && net < _netCount ? _nFluid[net] : _fluid[i];
        }

        /// <summary>
        /// 拆掉 (x, y) 的件。储罐里的存量、阀门缓冲里的流体随之排空（<paramref name="lostMl"/>，热更层写明）。
        /// 拆掉地下口会让两种流体的地下口重新配对时拒绝（<see cref="PipeResult.FluidConflict"/>，见 <see cref="CheckRemove"/>）。
        /// </summary>
        public PipeResult Remove(int x, int y, out long lostMl)
        {
            lostMl = 0;
            PipeResult check = CheckRemove(x, y, out _, out _);
            if (check != PipeResult.Ok)
            {
                return check;
            }
            int i = _lookup[Key(x, y)];
            lostMl = _stock[i] + _buf[i];
            TotalRemovedMl += lostMl;
            // O(1)：只从查找表摘掉并打上“已拆”标记，压紧数组留到下一次重算前一次做完（<see cref="Compact"/>）。
            // 框选拆除一帧拆几百格 = K 次 O(1) + 一次 O(格数) 压紧 + 一次重算，而不是 K 次整网重算（O(K×N)）。
            // 各格的网络编号（_net）压紧时随格平移、重算前不变，重算按“格子所在旧网络”继承最近流动步（结冰接口），不需要先重算一次种种子。
            _lookup.Remove(Key(x, y));
            _kind[i] = DeadKind;
            _stock[i] = 0;
            _buf[i] = 0;
            _bufFluid[i] = 0;
            _deadCount++;
            MarkDirty();
            return PipeResult.Ok;
        }

        /// <summary>已拆（待压紧）格的种类标记。只在 <see cref="Remove"/> 与 <see cref="Compact"/> 之间存在；任何按下标遍历格的地方都先压紧。</summary>
        private const byte DeadKind = 0xFF;

        private int _deadCount;

        /// <summary>把已拆的格压出数组（保序），查找表只改平移了的格。O(格数)，只在有拆除之后的第一次重算 / 按下标读格时发生一次。</summary>
        private void Compact()
        {
            if (_deadCount == 0)
            {
                return;
            }
            int w = 0;
            for (int r = 0; r < _n; r++)
            {
                if (_kind[r] == DeadKind)
                {
                    continue;
                }
                if (w != r)
                {
                    _x[w] = _x[r];
                    _y[w] = _y[r];
                    _kind[w] = _kind[r];
                    _tier[w] = _tier[r];
                    _dir[w] = _dir[r];
                    _fluid[w] = _fluid[r];
                    _mode[w] = _mode[r];
                    _prio[w] = _prio[r];
                    _open[w] = _open[r];
                    _bufFluid[w] = _bufFluid[r];
                    _stock[w] = _stock[r];
                    _buf[w] = _buf[r];
                    _pumpTotal[w] = _pumpTotal[r];
                    _seed[w] = _seed[r];
                    _net[w] = _net[r];
                    _stepIn[w] = _stepIn[r];
                    _stepOut[w] = _stepOut[r];
                    _mismatch[w] = _mismatch[r];
                    _lookup[Key(_x[w], _y[w])] = w;
                }
                w++;
            }
            _n = w;
            _deadCount = 0;
        }

        /// <summary>按坐标找格的下标。先压紧（有待压紧的已拆格时），保证返回的下标在随后的 <see cref="Rebuild"/> 之后仍然有效。</summary>
        private int Find(int x, int y)
        {
            Compact();
            return _lookup.TryGetValue(Key(x, y), out int i) ? i : -1;
        }

        /// <summary>FG3-LOG-07（FGR-LOG-010 升级规划）：原地改一格管线的等级（只有管线分等级；流体、存量、拓扑不变，网络按最低等级限流在下次重算时更新）。</summary>
        public PipeResult SetTier(int x, int y, int tier)
        {
            if (tier < 0 || tier >= PipeConst.TierCount)
            {
                return PipeResult.InvalidTier;
            }
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Pipe)
            {
                return PipeResult.WrongKind;
            }
            if (_tier[i] == tier)
            {
                return PipeResult.Ok;
            }
            _tier[i] = (byte)tier;
            MarkDirty();
            return PipeResult.Ok;
        }

        /// <summary>FGR-LOG-043：储罐模式（双向 / 只进 / 只出）。</summary>
        public PipeResult SetTankMode(int x, int y, PipeTankMode mode)
        {
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Tank)
            {
                return PipeResult.WrongKind;
            }
            if (mode > PipeTankMode.OutOnly)
            {
                return PipeResult.InvalidArgument;
            }
            _mode[i] = (byte)mode;
            Revision++;
            return PipeResult.Ok;
        }

        /// <summary>FGR-LOG-042：只进储罐的优先级（1～4）。</summary>
        public PipeResult SetTankPriority(int x, int y, int priority)
        {
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Tank)
            {
                return PipeResult.WrongKind;
            }
            if (priority < PipeConst.PriorityMin || priority > PipeConst.PriorityMax)
            {
                return PipeResult.InvalidPriority;
            }
            _prio[i] = (byte)priority;
            Revision++;
            return PipeResult.Ok;
        }

        /// <summary>FGR-LOG-043：手动开 / 关阀门（关着时不输送，缓冲保留）。</summary>
        public PipeResult SetValveOpen(int x, int y, bool open)
        {
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Valve)
            {
                return PipeResult.WrongKind;
            }
            _open[i] = open ? (byte)1 : (byte)0;
            Revision++;
            return PipeResult.Ok;
        }

        /// <summary>阀门原地调头（上下游对调）。缓冲里的流体留在阀门里，调头后往新的下游送；新的下游是别的流体时停止输送并写明原因。</summary>
        public PipeResult ReverseValve(int x, int y)
        {
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] != (byte)PipePieceKind.Valve)
            {
                return PipeResult.WrongKind;
            }
            Rebuild();
            int a = _valA[i] >= 0 ? _nFluid[_valA[i]] : 0;
            int b = _valB[i] >= 0 ? _nFluid[_valB[i]] : 0;
            if (a != 0 && b != 0 && a != b)
            {
                return PipeResult.FluidConflict;
            }
            _dir[i] = (byte)BeltDirs.Opposite(_dir[i]);
            MarkDirty();
            return PipeResult.Ok;
        }

        /// <summary>
        /// FGR-LOG-044：冲洗 (x, y) 所在的网络——储罐清空、网络与两侧阀门缓冲里的流体清空、网络回到“没有流体”（泵保留水源流体，
        /// 下一步重新认定）。<paramref name="flushedMl"/> = 清掉的量。阀门本身不属于网络：对阀门调用返回 <see cref="PipeResult.WrongKind"/>。
        /// 确认框在热更层（FG00 B04）。
        /// </summary>
        public PipeResult Flush(int x, int y, out long flushedMl)
        {
            flushedMl = 0;
            int i = Find(x, y);
            if (i < 0)
            {
                return PipeResult.NotFound;
            }
            if (_kind[i] == (byte)PipePieceKind.Valve)
            {
                return PipeResult.WrongKind;
            }
            Rebuild();
            int net = _net[i];
            if (net < 0)
            {
                return PipeResult.NotFound;
            }
            int s = _cellsOf.Start[net];
            int e = s + _cellsOf.Count[net];
            for (int k = s; k < e; k++)
            {
                int c = _cellsOf.Items[k];
                flushedMl += _stock[c];
                _stock[c] = 0;
                if (_kind[c] != (byte)PipePieceKind.Pump)
                {
                    _fluid[c] = 0;
                }
            }
            for (int k = 0; k < _valveCount; k++)
            {
                int v = _valves[k];
                if (_valA[v] == net || _valB[v] == net)
                {
                    flushedMl += _buf[v];
                    _buf[v] = 0;
                    _bufFluid[v] = 0;
                }
            }
            // 网络里还有泵：冲洗后立即按泵的水源重新认定（与下一步结算的结果相同）。不能留一段“网络没有流体”的窗口：
            // 那期间 CheckPlace 会放行把它接到另一种流体的网络，合并后另一种流体被静默改名（修复轮 P2，FGR-LOG-040）。
            int keep = 0;
            int kps = _pumpsOf.Start[net], kpe = kps + _pumpsOf.Count[net];
            for (int k = kps; k < kpe && keep == 0; k++)
            {
                keep = _fluid[_pumpsOf.Items[k]];
            }
            if (keep != 0)
            {
                AssignFluid(net, keep);
            }
            else
            {
                _nFluid[net] = 0;
            }
            TotalFlushedMl += flushedMl;
            Revision++;
            return PipeResult.Ok;
        }

        // ── 外部消费者（FG4 的建筑配方接入；自检直接驱动）──────────────────────────────

        /// <summary>在 (x, y)（一格管线件）挂一个消费者：要 <paramref name="fluid"/>，速率 <paramref name="litersPerMinute"/>，优先级 1～4。返回编号（失败 -1）。</summary>
        public int AddConsumer(int x, int y, int fluid, int litersPerMinute, int priority)
        {
            // FG4-ECO-02：fluid = PipeConst.AnyFluid（0）= 收网络里的任何流体（废液池）。
            if (fluid < PipeConst.AnyFluid || fluid > PipeConst.MaxFluid || litersPerMinute < 0 || priority < PipeConst.PriorityMin || priority > PipeConst.PriorityMax)
            {
                return -1;
            }
            EnsureConsumers(_cn + 1);
            int k = _cn++;
            _cIndexDirty = true;
            _cId[k] = _nextConsumerId++;
            _cX[k] = x;
            _cY[k] = y;
            _cFluid[k] = (byte)fluid;
            _cLpm[k] = litersPerMinute;
            _cPrio[k] = (byte)priority;
            _cNet[k] = -1;
            _cDemand[k] = 0;
            _cGot[k] = 0;
            _cTotal[k] = 0;
            _cBuf[k] = 0;
            _cCap[k] = 0;
            MarkDirty();
            return _cId[k];
        }

        /// <summary>FG4-ECO-02：给消费者一个缓存（建筑的流体输入缓存）：送达的流体进缓存，每步的需求 = min(速率配额, 容量 − 缓存)，缓存满了就不再要。
        /// 容量 0 = 不带缓存（送达即消耗）。<paramref name="initialMl"/> 夹到容量。只用于固定流体的消费者。</summary>
        public bool SetConsumerBuffer(int id, long capacityMl, long initialMl)
        {
            int k = ConsumerIndex(id);
            if (k < 0 || capacityMl < 0 || (capacityMl > 0 && _cFluid[k] == PipeConst.AnyFluid))
            {
                return false;
            }
            _cCap[k] = capacityMl;
            _cBuf[k] = capacityMl > 0 ? Math.Max(0, Math.Min(capacityMl, initialMl)) : 0;
            return true;
        }

        /// <summary>FG4-ECO-02：从消费者缓存里取走最多 <paramref name="ml"/> 毫升（建筑开工时扣料），返回实际取走的量。</summary>
        public long TakeConsumerBuffer(int id, long ml)
        {
            int k = ConsumerIndex(id);
            if (k < 0 || ml <= 0)
            {
                return 0;
            }
            long take = Math.Min(ml, _cBuf[k]);
            _cBuf[k] -= take;
            return take;
        }

        /// <summary>FG4-ECO-02：消费者缓存里现有多少（毫升；不存在 = 0）。O(消费者)。</summary>
        public long ConsumerBuffer(int id)
        {
            int k = ConsumerIndex(id);
            return k < 0 ? 0 : _cBuf[k];
        }

        /// <summary>删掉一个消费者，返回它缓存里还剩的量（调用方决定留在建筑里还是丢弃）。</summary>
        public bool RemoveConsumer(int id, out long bufferedMl)
        {
            int k = ConsumerIndex(id);
            bufferedMl = k >= 0 ? _cBuf[k] : 0;
            return RemoveConsumer(id);
        }

        // ── 外部供给者（FG4-ECO-02：建筑的流体输出口）────────────────────────────────

        /// <summary>在 (x, y)（口外那一格的管线件）挂一个供给者：只出 <paramref name="fluid"/>，存量上限 <paramref name="capacityMl"/>。返回编号（失败 -1）。</summary>
        public int AddProducer(int x, int y, int fluid, long capacityMl, long initialMl)
        {
            if (fluid < 1 || fluid > PipeConst.MaxFluid || capacityMl <= 0)
            {
                return -1;
            }
            EnsureProducers(_rn + 1);
            int k = _rn++;
            _rIndexDirty = true;
            _rId[k] = _nextProducerId++;
            _rX[k] = x;
            _rY[k] = y;
            _rFluid[k] = (byte)fluid;
            _rCap[k] = capacityMl;
            _rStock[k] = Math.Max(0, Math.Min(capacityMl, initialMl));
            _rNet[k] = -1;
            _rOut[k] = 0;
            _rTotal[k] = 0;
            MarkDirty();
            return _rId[k];
        }

        private int ProducerIndex(int id)
        {
            if (_rIndexDirty)
            {
                _rIndex.Clear();
                for (int k = 0; k < _rn; k++)
                {
                    _rIndex[_rId[k]] = k;
                }
                _rIndexDirty = false;
            }
            return _rIndex.TryGetValue(id, out int i) ? i : -1;
        }

        /// <summary>删掉一个供给者，返回它还没送出的存量。</summary>
        public bool RemoveProducer(int id, out long stockMl)
        {
            int k = ProducerIndex(id);
            stockMl = k >= 0 ? _rStock[k] : 0;
            if (k < 0)
            {
                return false;
            }
            int tail = _rn - k - 1;
            if (tail > 0)
            {
                Array.Copy(_rId, k + 1, _rId, k, tail);
                Array.Copy(_rX, k + 1, _rX, k, tail);
                Array.Copy(_rY, k + 1, _rY, k, tail);
                Array.Copy(_rFluid, k + 1, _rFluid, k, tail);
                Array.Copy(_rStock, k + 1, _rStock, k, tail);
                Array.Copy(_rCap, k + 1, _rCap, k, tail);
                Array.Copy(_rOut, k + 1, _rOut, k, tail);
                Array.Copy(_rTotal, k + 1, _rTotal, k, tail);
            }
            _rn--;
            _rIndexDirty = true;
            MarkDirty();
            return true;
        }

        /// <summary>把建筑产出的流体放进供给者存量，最多放到上限，返回实际放进去的量。</summary>
        public long AddProducerStock(int id, long ml)
        {
            int k = ProducerIndex(id);
            if (k < 0 || ml <= 0)
            {
                return 0;
            }
            long put = Math.Min(ml, _rCap[k] - _rStock[k]);
            if (put <= 0)
            {
                return 0;
            }
            _rStock[k] += put;
            return put;
        }

        /// <summary>供给者还能放多少（毫升；不存在 = 0）。</summary>
        public long ProducerSpace(int id)
        {
            int k = ProducerIndex(id);
            return k < 0 ? 0 : Math.Max(0, _rCap[k] - _rStock[k]);
        }

        public bool TryGetProducer(int id, out PipeProducerInfo info)
        {
            Rebuild();
            int k = ProducerIndex(id);
            info = default;
            if (k < 0)
            {
                return false;
            }
            info = new PipeProducerInfo
            {
                Id = _rId[k],
                X = _rX[k],
                Y = _rY[k],
                Fluid = _rFluid[k],
                Network = _rNet[k],
                StockMl = _rStock[k],
                CapacityMl = _rCap[k],
                LastDeliveredMl = _rOut[k],
                TotalDeliveredMl = _rTotal[k],
            };
            return true;
        }

        private void EnsureProducers(int need)
        {
            if (need <= _rId.Length)
            {
                return;
            }
            int cap = Math.Max(need, _rId.Length * 2);
            Array.Resize(ref _rId, cap);
            Array.Resize(ref _rX, cap);
            Array.Resize(ref _rY, cap);
            Array.Resize(ref _rFluid, cap);
            Array.Resize(ref _rStock, cap);
            Array.Resize(ref _rCap, cap);
            Array.Resize(ref _rNet, cap);
            Array.Resize(ref _rOut, cap);
            Array.Resize(ref _rTotal, cap);
        }

        // FG4-ECO-02：编号 → 下标的索引（建筑每个生产步都按编号查自己的消费者 / 供给者，线性查找会变成 O(建筑数²)）。
        // 增加时直接登记；删除会让后面的下标前移，标脏后下一次查找时整表重建（删除是低频编辑）。
        private readonly Dictionary<int, int> _cIndex = new Dictionary<int, int>(8);
        private readonly Dictionary<int, int> _rIndex = new Dictionary<int, int>(8);
        private bool _cIndexDirty = true;
        private bool _rIndexDirty = true;

        private int ConsumerIndex(int id)
        {
            if (_cIndexDirty)
            {
                _cIndex.Clear();
                for (int k = 0; k < _cn; k++)
                {
                    _cIndex[_cId[k]] = k;
                }
                _cIndexDirty = false;
            }
            return _cIndex.TryGetValue(id, out int i) ? i : -1;
        }

        public bool RemoveConsumer(int id)
        {
            int k = ConsumerIndex(id);
            if (k < 0)
            {
                return false;
            }
            int tail = _cn - k - 1;
            if (tail > 0)
            {
                Array.Copy(_cId, k + 1, _cId, k, tail);
                Array.Copy(_cX, k + 1, _cX, k, tail);
                Array.Copy(_cY, k + 1, _cY, k, tail);
                Array.Copy(_cFluid, k + 1, _cFluid, k, tail);
                Array.Copy(_cLpm, k + 1, _cLpm, k, tail);
                Array.Copy(_cPrio, k + 1, _cPrio, k, tail);
                Array.Copy(_cDemand, k + 1, _cDemand, k, tail);
                Array.Copy(_cGot, k + 1, _cGot, k, tail);
                Array.Copy(_cTotal, k + 1, _cTotal, k, tail);
                Array.Copy(_cBuf, k + 1, _cBuf, k, tail);
                Array.Copy(_cCap, k + 1, _cCap, k, tail);
            }
            _cn--;
            _cIndexDirty = true;
            MarkDirty();
            return true;
        }

        public bool SetConsumer(int id, int litersPerMinute, int priority)
        {
            int k = ConsumerIndex(id);
            if (k < 0 || litersPerMinute < 0 || priority < PipeConst.PriorityMin || priority > PipeConst.PriorityMax)
            {
                return false;
            }
            _cLpm[k] = litersPerMinute;
            if (_cPrio[k] != priority)
            {
                _cPrio[k] = (byte)priority;
            }
            return true;
        }

        public bool TryGetConsumer(int id, out PipeConsumerInfo info)
        {
            Rebuild();
            int k = ConsumerIndex(id);
            info = default;
            if (k < 0)
            {
                return false;
            }
            info = new PipeConsumerInfo
            {
                Id = _cId[k],
                X = _cX[k],
                Y = _cY[k],
                Fluid = _cFluid[k],
                LitersPerMinute = _cLpm[k],
                Priority = _cPrio[k],
                Network = _cNet[k],
                LastDemandMl = _cDemand[k],
                LastDeliveredMl = _cGot[k],
                TotalDeliveredMl = _cTotal[k],
                BufferMl = _cBuf[k],
                CapacityMl = _cCap[k],
            };
            return true;
        }

        private void MarkDirty()
        {
            _dirty = true;
            Revision++;
        }

        // ── 拓扑重算（只在编辑之后）──────────────────────────────────────────────

        /// <summary>编辑之后第一次读数 / 步进时重算网络（BFS，O(格数 + 消费者数)）；没有编辑时直接返回。</summary>
        public void Rebuild()
        {
            if (!_dirty)
            {
                return;
            }
            _watch.Restart();
            _dirty = false;
            RebuildCount++;
            Compact();
            // 旧网络的“最近流动步”先保存，新网络继承其格子所在旧网络的最大值（结冰计时不因为拆 / 接一段管子而清零或跳变）。
            EnsureNets(_netCount + 1);
            Array.Copy(_nLastFlow, _nLastFlowOld, _netCount);
            int oldCount = _netCount;
            PairUnderground();
            for (int i = 0; i < _n; i++)
            {
                _oldNet[i] = _net[i] < oldCount ? _net[i] : -1;
                _net[i] = -1;
                int m = 0;
                for (int d = 0; d < 4; d++)
                {
                    if (!Opens(i, d))
                    {
                        continue;
                    }
                    int j = Neighbor(i, d);
                    if (j >= 0 && Opens(j, BeltDirs.Opposite(d)))
                    {
                        m |= 1 << d;
                    }
                }
                _mask[i] = m;
            }
            // BFS（按铺设顺序开始，编号确定）。阀门不进网络。
            int nets = 0;
            _cellsOf.Begin(_n);
            for (int i = 0; i < _n; i++)
            {
                if (_net[i] >= 0 || _kind[i] == (byte)PipePieceKind.Valve)
                {
                    continue;
                }
                EnsureNets(nets + 1);
                int id = nets++;
                _cellsOf.Open(id);
                int head = 0;
                int tail = 0;
                EnsureQueue(_n);
                _queue[tail++] = i;
                _net[i] = id;
                while (head < tail)
                {
                    int c = _queue[head++];
                    _cellsOf.Add(c);
                    int m = _mask[c];
                    for (int d = 0; d < 4; d++)
                    {
                        if ((m & (1 << d)) == 0)
                        {
                            continue;
                        }
                        int j = Neighbor(c, d);
                        if (j >= 0 && _net[j] < 0 && _kind[j] != (byte)PipePieceKind.Valve)
                        {
                            _net[j] = id;
                            _queue[tail++] = j;
                        }
                    }
                    // FG4-ECO-04（FG-GAP-082）：地下管线口与配对口同一网络（中间的格子不参与）。
                    int pj = _partner[c];
                    if (pj >= 0 && _net[pj] < 0)
                    {
                        _net[pj] = id;
                        _queue[tail++] = pj;
                    }
                }
                _cellsOf.Close();
            }
            _netCount = nets;
            // 网络汇总
            for (int n = 0; n < nets; n++)
            {
                _nFluid[n] = 0;
                _nCells[n] = 0;
                _nPipe[n] = 0;
                _nMinTier[n] = int.MaxValue;
                _nMinCount[n] = 0;
                _nMixed[n] = 0;
                _nBx[n] = 0;
                _nBy[n] = 0;
                _nLastFlow[n] = -1;
                _nSupply[n] = 0;
                _nDemand[n] = 0;
                _nMoved[n] = 0;
                _nFill[n] = 0;
                _nUnmet[n] = 0;
                _nIssues[n] = 0;
                _aSupply[n] = _aDemand[n] = _aMoved[n] = _aFill[n] = _aUnmet[n] = 0;
                _aCount[n] = 0;
                _pValid[n] = 0;
                _pCount[n] = 0;
            }
            for (int n = 0; n < nets; n++)
            {
                int s = _cellsOf.Start[n];
                int e = s + _cellsOf.Count[n];
                int fluid = 0;
                int firstTier = -1;
                for (int k = s; k < e; k++)
                {
                    int c = _cellsOf.Items[k];
                    _nCells[n]++;
                    int f = _fluid[c];
                    if (f != 0)
                    {
                        if (fluid == 0)
                        {
                            fluid = f;
                        }
                        else if (f != fluid)
                        {
                            FluidConflictsResolved++;
                            fluid = Math.Min(fluid, f);
                        }
                    }
                    if (_kind[c] == (byte)PipePieceKind.Pipe || _kind[c] == (byte)PipePieceKind.Underground)
                    {
                        // 地下管线口按它的等级参与“最低等级限流”（地下段的吞吐与同等级管线相同）。
                        _nPipe[n]++;
                        int t = _tier[c];
                        if (firstTier < 0)
                        {
                            firstTier = t;
                        }
                        else if (t != firstTier)
                        {
                            _nMixed[n] = 1;
                        }
                        if (t < _nMinTier[n])
                        {
                            _nMinTier[n] = t;
                            _nMinCount[n] = 1;
                            _nBx[n] = _x[c];
                            _nBy[n] = _y[c];
                        }
                        else if (t == _nMinTier[n])
                        {
                            _nMinCount[n]++;
                        }
                    }
                    long lf = _oldNet[c] >= 0 ? _nLastFlowOld[_oldNet[c]] : _seed[c];
                    if (lf > _nLastFlow[n])
                    {
                        _nLastFlow[n] = lf;
                    }
                }
                if (_nPipe[n] == 0)
                {
                    _nMinTier[n] = -1;
                }
                if (_nLastFlow[n] < 0)
                {
                    _nLastFlow[n] = StepIndex;
                }
                _nFluid[n] = (byte)fluid;
                for (int k = s; k < e; k++)
                {
                    int c = _cellsOf.Items[k];
                    if (_kind[c] != (byte)PipePieceKind.Pump)
                    {
                        _fluid[c] = (byte)fluid;
                    }
                }
            }
            for (int i = 0; i < _n; i++)
            {
                _seed[i] = -1;
            }
            // 阀门两侧、储罐 / 泵 / 消费者按网络分组（按铺设顺序 / 消费者编号顺序，分配确定）。
            _valveCount = 0;
            _tankCount = 0;
            for (int i = 0; i < _n; i++)
            {
                if (_kind[i] == (byte)PipePieceKind.Valve)
                {
                    EnsureInts(ref _valves, _valveCount + 1);
                    _valves[_valveCount++] = i;
                    _valA[i] = SideNet(i, BeltDirs.Opposite(_dir[i]));
                    _valB[i] = SideNet(i, _dir[i]);
                }
                else if (_kind[i] == (byte)PipePieceKind.Tank)
                {
                    EnsureInts(ref _tanks, _tankCount + 1);
                    _tanks[_tankCount++] = i;
                }
            }
            GroupBy(_pumpsOf, nets, PipePieceKind.Pump);
            GroupBy(_tanksOf, nets, PipePieceKind.Tank);
            // 下游阀门（阀门后方 = 本网络，本网络往阀门里送）与上游阀门（阀门前方 = 本网络，从阀门里取）。
            _valveOutOf.Begin(_valveCount);
            for (int n = 0; n < nets; n++)
            {
                _valveOutOf.Open(n);
                for (int k = 0; k < _valveCount; k++)
                {
                    if (_valA[_valves[k]] == n)
                    {
                        _valveOutOf.Add(_valves[k]);
                    }
                }
                _valveOutOf.Close();
            }
            _valveInOf.Begin(_valveCount);
            for (int n = 0; n < nets; n++)
            {
                _valveInOf.Open(n);
                for (int k = 0; k < _valveCount; k++)
                {
                    if (_valB[_valves[k]] == n)
                    {
                        _valveInOf.Add(_valves[k]);
                    }
                }
                _valveInOf.Close();
            }
            for (int k = 0; k < _cn; k++)
            {
                int c = Find(_cX[k], _cY[k]);
                _cNet[k] = c >= 0 && _kind[c] != (byte)PipePieceKind.Valve ? _net[c] : -1;
                if (_cNet[k] < 0)
                {
                    _cDemand[k] = 0;
                    _cGot[k] = 0;
                }
            }
            _consOf.Begin(_cn);
            for (int n = 0; n < nets; n++)
            {
                _consOf.Open(n);
                for (int k = 0; k < _cn; k++)
                {
                    if (_cNet[k] == n)
                    {
                        _consOf.Add(k);
                    }
                }
                _consOf.Close();
            }
            // FG4-ECO-02：外部供给者按网络分组（按编号顺序，分配确定）。
            for (int k = 0; k < _rn; k++)
            {
                int c = Find(_rX[k], _rY[k]);
                _rNet[k] = c >= 0 && _kind[c] != (byte)PipePieceKind.Valve ? _net[c] : -1;
                if (_rNet[k] < 0)
                {
                    _rOut[k] = 0;
                }
            }
            _prodOf.Begin(_rn);
            for (int n = 0; n < nets; n++)
            {
                _prodOf.Open(n);
                for (int k = 0; k < _rn; k++)
                {
                    if (_rNet[k] == n)
                    {
                        _prodOf.Add(k);
                    }
                }
                _prodOf.Close();
            }
            Revision++;
            _watch.Stop();
            LastRebuildMs = _watch.Elapsed.TotalMilliseconds;
        }

        private int SideNet(int i, int d)
        {
            int j = Neighbor(i, d);
            if (j < 0 || !Opens(j, BeltDirs.Opposite(d)) || _kind[j] == (byte)PipePieceKind.Valve)
            {
                return -1;
            }
            return _net[j];
        }

        private void GroupBy(Csr csr, int nets, PipePieceKind kind)
        {
            csr.Begin(_n);
            for (int n = 0; n < nets; n++)
            {
                csr.Open(n);
                int s = _cellsOf.Start[n];
                int e = s + _cellsOf.Count[n];
                for (int k = s; k < e; k++)
                {
                    int c = _cellsOf.Items[k];
                    if (_kind[c] == (byte)kind)
                    {
                        csr.Add(c);
                    }
                }
                csr.Close();
            }
        }

        // ── 固定步长 ─────────────────────────────────────────────────────────────

        /// <summary>内核一步（热更层按世界步序号换算的节拍调用，与传送带同频）。O(网络 + 泵 + 储罐 + 阀门 + 消费者)，零分配。</summary>
        public void Step()
        {
            _watch.Restart();
            Rebuild();
            long s = StepIndex;
            long cap0 = Budget(_config.LitersPerMinuteT1, s);
            long cap1 = Budget(_config.LitersPerMinuteT2, s);
            long pumpB = Budget(_config.PumpLitersPerMinute, s);
            long tankB = Budget(_config.TankLitersPerMinute, s);
            long valveB = Budget(_config.ValveLitersPerMinute, s);
            long tankCap = _config.TankLiters * 1000L;
            for (int k = 0; k < _valveCount; k++)
            {
                int v = _valves[k];
                _bufStart[v] = _buf[v];
                _stepIn[v] = 0;
                _stepOut[v] = 0;
                int a = _valA[v] >= 0 ? _nFluid[_valA[v]] : 0;
                int b = _valB[v] >= 0 ? _nFluid[_valB[v]] : 0;
                byte mm = (byte)((a != 0 && b != 0 && a != b) || (_bufFluid[v] != 0 && b != 0 && _bufFluid[v] != b && _buf[v] > 0) ? 1 : 0);
                if (mm != _mismatch[v])
                {
                    _mismatch[v] = mm;
                    Revision++;
                }
            }
            for (int k = 0; k < _tankCount; k++)
            {
                int t = _tanks[k];
                _stepIn[t] = 0;
                _stepOut[t] = 0;
            }
            _noSupplyCount = 0;
            for (int n = 0; n < _netCount; n++)
            {
                Settle(n, s, cap0, cap1, pumpB, tankB, valveB, tankCap);
            }
            LastNoSupplyNetworks = _noSupplyCount;
            StepIndex = s + 1;
            _watch.Stop();
            LastStepMs = _watch.Elapsed.TotalMilliseconds;
        }

        private void Settle(int n, long s, long cap0, long cap1, long pumpB, long tankB, long valveB, long tankCap)
        {
            int f = _nFluid[n];
            int ps = _pumpsOf.Start[n], pe = ps + _pumpsOf.Count[n];
            int ts = _tanksOf.Start[n], te = ts + _tanksOf.Count[n];
            int vis = _valveInOf.Start[n], vie = vis + _valveInOf.Count[n];
            int vos = _valveOutOf.Start[n], voe = vos + _valveOutOf.Count[n];
            int cs = _consOf.Start[n], ce = cs + _consOf.Count[n];
            int rs = _prodOf.Start[n], re = rs + _prodOf.Count[n];
            for (int k = rs; k < re; k++)
            {
                _rOut[_prodOf.Items[k]] = 0;
            }
            // 网络还没有流体：由泵的水源、上游阀门送来的流体、或有存量的外部供给者（建筑出口，FG4-ECO-02）认定（FGR-LOG-040“一个网络只能有一种流体”）。
            if (f == 0)
            {
                for (int k = ps; k < pe && f == 0; k++)
                {
                    f = _fluid[_pumpsOf.Items[k]];
                }
                for (int k = rs; k < re && f == 0; k++)
                {
                    int r = _prodOf.Items[k];
                    if (_rStock[r] > 0)
                    {
                        f = _rFluid[r];
                    }
                }
                for (int k = vis; k < vie && f == 0; k++)
                {
                    int v = _valveInOf.Items[k];
                    if (_open[v] != 0 && _bufStart[v] > 0 && _bufFluid[v] != 0)
                    {
                        f = _bufFluid[v];
                    }
                }
                if (f != 0)
                {
                    AssignFluid(n, f);
                }
            }
            long cap = _nPipe[n] > 0 ? (_nMinTier[n] <= 0 ? cap0 : cap1) : cap1;
            // 供给
            long vS = 0, pS = 0, tS = 0;
            int sources = 0;
            for (int k = vis; k < vie; k++)
            {
                int v = _valveInOf.Items[k];
                if (_open[v] != 0 && _mismatch[v] == 0 && (_bufFluid[v] == f || f == 0))
                {
                    vS += Math.Min(_bufStart[v], valveB); // 阀门每步最多放出 valveB（阀门吞吐）
                }
                if (_open[v] != 0 && _valA[v] >= 0)
                {
                    sources++;
                }
            }
            for (int k = ps; k < pe; k++)
            {
                int p = _pumpsOf.Items[k];
                _stepOut[p] = 0;
                if (f != 0 && _fluid[p] == f)
                {
                    pS += pumpB;
                    sources++;
                }
            }
            // FG4-ECO-02：外部供给者（建筑出口）按存量供给；流体不对的供给者送不出去（热更层写“接错流体”）。
            long rS = 0;
            for (int k = rs; k < re; k++)
            {
                int r = _prodOf.Items[k];
                if (f != 0 && _rFluid[r] == f && _rStock[r] > 0)
                {
                    rS += _rStock[r];
                    sources++;
                }
            }
            long stored = 0;
            long storeCap = 0;
            for (int k = ts; k < te; k++)
            {
                int t = _tanksOf.Items[k];
                stored += _stock[t];
                storeCap += tankCap;
                if (_mode[t] != (byte)PipeTankMode.InOnly && _stock[t] > 0)
                {
                    tS += Math.Min(_stock[t], tankB);
                    sources++;
                }
            }
            // 需求（按优先级；流体不对的消费者拿不到，计入没满足）
            int dn = 0;
            long mismatched = 0;
            for (int k = cs; k < ce; k++)
            {
                int c = _consOf.Items[k];
                long d = Budget(_cLpm[c], s);
                if (_cCap[c] > 0)
                {
                    d = Math.Min(d, _cCap[c] - _cBuf[c]); // FG4-ECO-02：带缓存的消费者，缓存满了就不再要。
                }
                _cDemand[c] = d;
                _cGot[c] = 0;
                if (d <= 0)
                {
                    continue;
                }
                if (f == 0 || (_cFluid[c] != PipeConst.AnyFluid && _cFluid[c] != f))
                {
                    mismatched += d;
                    continue;
                }
                AddDemand(ref dn, 0, c, _cPrio[c], d);
            }
            for (int k = ts; k < te; k++)
            {
                int t = _tanksOf.Items[k];
                if (_mode[t] == (byte)PipeTankMode.InOnly)
                {
                    long d = Math.Min(tankCap - _stock[t], tankB);
                    if (d > 0)
                    {
                        AddDemand(ref dn, 1, t, _prio[t], d);
                    }
                }
            }
            for (int k = vos; k < voe; k++)
            {
                int v = _valveOutOf.Items[k];
                if (_open[v] == 0 || _mismatch[v] != 0 || _valB[v] < 0 || (_bufFluid[v] != 0 && _bufFluid[v] != f && _bufStart[v] > 0))
                {
                    continue;
                }
                // 阀门缓冲容量 = 两步配额：下游每步只取步初存量（最多 valveB），上游每步最多补 valveB。
                // 只给一步容量时，下游取光后缓冲在“满 / 空”之间交替，实际吞吐只有 valveB / 2、下游根因每步来回跳（修复轮 P1）；
                // 两步容量下无论网络结算顺序，稳态都是每步进 valveB、出 valveB。
                long d = Math.Min(valveB, 2 * valveB - _bufStart[v]);
                if (d > 0)
                {
                    AddDemand(ref dn, 2, v, PipeConst.ValvePriority, d);
                }
            }
            long D = 0;
            for (int k = 0; k < dn; k++)
            {
                D += _dAmt[k];
            }
            long S = vS + pS + rS + tS;
            long moved = Math.Min(Math.Min(D, S), cap);
            // 按优先级分配（1 最先）；同一优先级不够时按需求比例，余数按顺序一毫升一毫升补齐。
            long rem = moved;
            for (int p = PipeConst.PriorityMin; p <= PipeConst.PriorityMax; p++)
            {
                long dp = 0;
                int cnt = 0;
                for (int k = 0; k < dn; k++)
                {
                    if (_dPrio[k] == p)
                    {
                        dp += _dAmt[k];
                        cnt++;
                    }
                }
                if (dp == 0)
                {
                    continue;
                }
                if (rem >= dp)
                {
                    for (int k = 0; k < dn; k++)
                    {
                        if (_dPrio[k] == p)
                        {
                            _dGive[k] = _dAmt[k];
                        }
                    }
                    rem -= dp;
                    continue;
                }
                long given = 0;
                for (int k = 0; k < dn; k++)
                {
                    if (_dPrio[k] == p)
                    {
                        long g = _dAmt[k] * rem / dp;
                        _dGive[k] = g;
                        given += g;
                    }
                }
                long left = rem - given;
                for (int k = 0; k < dn && left > 0; k++)
                {
                    if (_dPrio[k] == p && _dGive[k] < _dAmt[k])
                    {
                        _dGive[k]++;
                        left--;
                    }
                }
                rem = 0;
                for (int q = p + 1; q <= PipeConst.PriorityMax; q++)
                {
                    for (int k = 0; k < dn; k++)
                    {
                        if (_dPrio[k] == q)
                        {
                            _dGive[k] = 0;
                        }
                    }
                }
                break;
            }
            // 送达
            for (int k = 0; k < dn; k++)
            {
                long g = _dGive[k];
                int r = _dRef[k];
                switch (_dType[k])
                {
                    case 0:
                        _cGot[r] = g;
                        _cTotal[r] += g;
                        if (_cCap[r] > 0)
                        {
                            _cBuf[r] += g;
                        }
                        break;
                    case 1:
                        _stock[r] += g;
                        _stepIn[r] += g;
                        break;
                    default:
                        if (g > 0)
                        {
                            _buf[r] += g;
                            _bufFluid[r] = (byte)f;
                            _stepIn[r] += g;
                        }
                        break;
                }
            }
            // 取用供给：上游阀门 → 泵 → 储罐
            long need = moved;
            long usedV = TakeValves(vis, vie, f, valveB, ref need);
            long usedP = TakePumps(ps, pe, f, pumpB, ref need);
            long usedR = TakeProducers(rs, re, f, ref need);
            if (need > 0)
            {
                for (int k = ts; k < te && need > 0; k++)
                {
                    int t = _tanksOf.Items[k];
                    if (_mode[t] == (byte)PipeTankMode.InOnly)
                    {
                        continue;
                    }
                    long take = Math.Min(Math.Min(_stock[t], tankB), need);
                    _stock[t] -= take;
                    _stepOut[t] += take;
                    need -= take;
                }
            }
            // 余量灌进双向储罐（缓冲，FGR-LOG-043）：只用阀门 / 泵没用完的供给，且总流量不超过吞吐上限。
            long free = vS - usedV + pS - usedP + rS - usedR;
            long room = cap - moved;
            long fill = 0;
            if (free > 0 && room > 0 && f != 0)
            {
                long want = Math.Min(free, room);
                for (int k = ts; k < te && want > 0; k++)
                {
                    int t = _tanksOf.Items[k];
                    if (_mode[t] != (byte)PipeTankMode.Both || _stepOut[t] > 0)
                    {
                        continue;
                    }
                    long put = Math.Min(Math.Min(tankCap - _stock[t], tankB), want);
                    if (put <= 0)
                    {
                        continue;
                    }
                    _stock[t] += put;
                    _stepIn[t] += put;
                    want -= put;
                    fill += put;
                }
                long needFill = fill;
                TakeValves(vis, vie, f, valveB, ref needFill);
                TakePumps(ps, pe, f, pumpB, ref needFill);
                TakeProducers(rs, re, f, ref needFill);
            }
            // 读数与根因
            _nSupply[n] = S;
            _nDemand[n] = D + mismatched;
            _nMoved[n] = moved;
            _nFill[n] = fill;
            _nUnmet[n] = D - moved + mismatched;
            int issues = 0;
            if (f == 0)
            {
                issues |= (int)PipeNetIssue.Empty;
            }
            if (sources == 0)
            {
                issues |= (int)PipeNetIssue.NoSource;
            }
            if (D + mismatched > 0 && moved == 0)
            {
                issues |= (int)PipeNetIssue.NoSupply;
                _noSupplyCount++;
            }
            if (_nUnmet[n] > 0)
            {
                issues |= (int)PipeNetIssue.Shortage;
            }
            if (Math.Min(D, S) > cap || (moved + fill == cap && free > fill && fill > 0))
            {
                issues |= (int)PipeNetIssue.PipeLimited;
            }
            if (D == 0 && fill == 0 && (pS > 0 || rS > 0))
            {
                issues |= (int)PipeNetIssue.NoDemand;
            }
            _nIssues[n] = issues;
            _aSupply[n] += S;
            _aDemand[n] += D + mismatched;
            _aMoved[n] += moved;
            _aFill[n] += fill;
            _aUnmet[n] += D - moved + mismatched;
            // FG3-LOG-09：窗口按绝对步序号对齐整秒发布（此前按“自上次重建起每满一秒”发布：读档 / 改线重建后相位跟着变，
            // 同一时刻的实测读数与不存档一直跑的不同）。重建后的第一个窗口可能不满一秒，读数按实际步数折算。
            ++_aCount[n];
            if ((s + 1) % _config.StepHz == 0)
            {
                _pCount[n] = _aCount[n];
                _pSupply[n] = _aSupply[n];
                _pDemand[n] = _aDemand[n];
                _pMoved[n] = _aMoved[n];
                _pFill[n] = _aFill[n];
                _pUnmet[n] = _aUnmet[n];
                _pValid[n] = 1;
                _aSupply[n] = _aDemand[n] = _aMoved[n] = _aFill[n] = _aUnmet[n] = 0;
                _aCount[n] = 0;
            }
            if (moved + fill > 0)
            {
                _nLastFlow[n] = s;
            }
            TotalDeliveredMl += moved;
            // 储罐液位跨档（1/16）时让画面跟上（O(储罐)）。
            long after = 0;
            for (int k = ts; k < te; k++)
            {
                after += _stock[_tanksOf.Items[k]];
            }
            if (tankCap > 0 && te > ts && (after * 16 / (tankCap * (te - ts))) != (stored * 16 / (tankCap * (te - ts))))
            {
                Revision++;
            }
        }

        private long TakeValves(int vis, int vie, int f, long valveB, ref long need)
        {
            long used = 0;
            for (int k = vis; k < vie && need > 0; k++)
            {
                int v = _valveInOf.Items[k];
                if (_open[v] == 0 || _mismatch[v] != 0 || (_bufFluid[v] != f && f != 0))
                {
                    continue;
                }
                long avail = Math.Min(_bufStart[v], valveB) - _stepOut[v];
                long take = Math.Min(avail, need);
                if (take <= 0)
                {
                    continue;
                }
                _buf[v] -= take;
                _stepOut[v] += take;
                if (_buf[v] == 0)
                {
                    _bufFluid[v] = 0;
                }
                need -= take;
                used += take;
            }
            return used;
        }

        private long TakePumps(int ps, int pe, int f, long pumpB, ref long need)
        {
            long used = 0;
            for (int k = ps; k < pe && need > 0; k++)
            {
                int p = _pumpsOf.Items[k];
                if (f == 0 || _fluid[p] != f)
                {
                    continue;
                }
                long take = Math.Min(pumpB - _stepOut[p], need);
                if (take <= 0)
                {
                    continue;
                }
                _stepOut[p] += take;
                _pumpTotal[p] += take;
                TotalPumpedMl += take;
                need -= take;
                used += take;
            }
            return used;
        }

        /// <summary>FG4-ECO-02：从外部供给者（建筑出口）的存量里取（按编号顺序，确定）。</summary>
        private long TakeProducers(int rs, int re, int f, ref long need)
        {
            long used = 0;
            for (int k = rs; k < re && need > 0; k++)
            {
                int r = _prodOf.Items[k];
                if (f == 0 || _rFluid[r] != f)
                {
                    continue;
                }
                long take = Math.Min(_rStock[r], need);
                if (take <= 0)
                {
                    continue;
                }
                _rStock[r] -= take;
                _rOut[r] += take;
                _rTotal[r] += take;
                TotalProducedOutMl += take;
                need -= take;
                used += take;
            }
            return used;
        }

        private void AddDemand(ref int dn, byte type, int reference, int prio, long amount)
        {
            if (dn >= _dAmt.Length)
            {
                int cap = _dAmt.Length * 2;
                Array.Resize(ref _dAmt, cap);
                Array.Resize(ref _dGive, cap);
                Array.Resize(ref _dRef, cap);
                Array.Resize(ref _dType, cap);
                Array.Resize(ref _dPrio, cap);
            }
            _dAmt[dn] = amount;
            _dGive[dn] = 0;
            _dRef[dn] = reference;
            _dType[dn] = type;
            _dPrio[dn] = (byte)Math.Max(PipeConst.PriorityMin, Math.Min(PipeConst.PriorityMax, prio));
            dn++;
        }

        /// <summary>网络第一次有流体：记到网络与它的每格（O(网络格数)，只在认定的那一步发生一次）。</summary>
        private void AssignFluid(int n, int f)
        {
            _nFluid[n] = (byte)f;
            int s = _cellsOf.Start[n];
            int e = s + _cellsOf.Count[n];
            for (int k = s; k < e; k++)
            {
                int c = _cellsOf.Items[k];
                if (_kind[c] != (byte)PipePieceKind.Pump)
                {
                    _fluid[c] = (byte)f;
                }
            }
            Revision++;
        }

        // ── 读数 ─────────────────────────────────────────────────────────────────

        /// <summary>FG3-LOG-08（叠加层标签 / 诊断定位）：每个网络的锚点格（铺设顺序里这个网络的第一格），z = 网络编号。一次 O(格数)，AOT。</summary>
        public void CollectNetworkAnchors(List<Unity.Mathematics.int3> into)
        {
            Rebuild();
            into.Clear();
            if (_netCount <= 0)
            {
                return;
            }
            var seen = new bool[_netCount];
            for (int i = 0; i < _n; i++)
            {
                int net = _net[i];
                if (net < 0 || net >= _netCount || seen[net])
                {
                    continue;
                }
                seen[net] = true;
                into.Add(new Unity.Mathematics.int3(_x[i], _y[i], net));
            }
        }

        public int NetworkAt(int x, int y)
        {
            Rebuild();
            int i = Find(x, y);
            return i >= 0 ? _net[i] : -1;
        }

        public bool TryGetCellInfo(int x, int y, out PipeCellInfo info)
        {
            Rebuild();
            int i = Find(x, y);
            info = default;
            if (i < 0)
            {
                return false;
            }
            bool valve = _kind[i] == (byte)PipePieceKind.Valve;
            bool tank = _kind[i] == (byte)PipePieceKind.Tank;
            bool pump = _kind[i] == (byte)PipePieceKind.Pump;
            info = new PipeCellInfo
            {
                X = _x[i],
                Y = _y[i],
                Kind = (PipePieceKind)_kind[i],
                Tier = _tier[i],
                Dir = _dir[i],
                Fluid = valve ? _bufFluid[i] : _fluid[i],
                Network = _net[i],
                Mask = _mask[i],
                TankStockMl = tank ? _stock[i] : 0,
                TankCapacityMl = tank ? _config.TankLiters * 1000L : 0,
                TankMode = (PipeTankMode)_mode[i],
                Priority = tank ? _prio[i] : valve ? PipeConst.ValvePriority : 0,
                TankInMl = tank ? _stepIn[i] : 0,
                TankOutMl = tank ? _stepOut[i] : 0,
                ValveOpen = !valve || _open[i] != 0,
                ValveBufferMl = valve ? _buf[i] : 0,
                ValveFrom = valve ? _valA[i] : -1,
                ValveTo = valve ? _valB[i] : -1,
                ValveFlowMl = valve ? _stepOut[i] : 0,
                ValveFluidMismatch = valve && _mismatch[i] != 0,
                PumpedMl = pump ? _stepOut[i] : 0,
                PumpTotalMl = pump ? _pumpTotal[i] : 0,
                UndergroundLinked = _partner[i] >= 0,
                PartnerX = _partner[i] >= 0 ? _x[_partner[i]] : 0,
                PartnerY = _partner[i] >= 0 ? _y[_partner[i]] : 0,
            };
            return true;
        }

        /// <summary>FG4-ECO-04（FG-GAP-082）：放在 (x, y)、朝 <paramref name="dir"/>、等级 <paramref name="tier"/> 的地下管线口会和哪一口配对（放置预览用，不改状态）。</summary>
        public bool TryPreviewUnderground(int x, int y, int dir, int tier, out int partnerX, out int partnerY)
        {
            Rebuild();
            partnerX = partnerY = 0;
            if (dir < 0 || dir > 3)
            {
                return false;
            }
            int j = FindNewPartner(x, y, dir, tier);
            if (j < 0)
            {
                return false;
            }
            partnerX = _x[j];
            partnerY = _y[j];
            return true;
        }

        private double Lpm(long mlPerStep) => mlPerStep * 60.0 * _config.StepHz / 1000.0;

        /// <summary>读数：最近一个已发布的整秒窗口（按绝对步序号对齐；重建后的第一个窗口按实际累计的步数折算）换算成升 / 分钟；还没发布过时按已走的步数折算；一步都没走时用最近一步。</summary>
        private double WindowLpm(int net, long[] published, long[] acc, long[] lastStep)
        {
            if (_pValid[net] != 0)
            {
                return published[net] * 60.0 * _config.StepHz / (1000.0 * Math.Max(1, _pCount[net]));
            }
            if (_aCount[net] > 0)
            {
                return acc[net] * 60.0 * _config.StepHz / (1000.0 * _aCount[net]);
            }
            return Lpm(lastStep[net]);
        }

        public bool TryGetNetworkInfo(int net, out PipeNetInfo info)
        {
            Rebuild();
            info = default;
            if (net < 0 || net >= _netCount)
            {
                return false;
            }
            long stored = 0;
            long storeCap = 0;
            int ts = _tanksOf.Start[net], te = ts + _tanksOf.Count[net];
            for (int k = ts; k < te; k++)
            {
                stored += _stock[_tanksOf.Items[k]];
                storeCap += _config.TankLiters * 1000L;
            }
            int capLpm = _nPipe[net] > 0 ? _config.TierLitersPerMinute(_nMinTier[net]) : _config.LitersPerMinuteT2;
            info = new PipeNetInfo
            {
                Id = net,
                Fluid = _nFluid[net],
                Cells = _nCells[net],
                PipeCells = _nPipe[net],
                Pumps = _pumpsOf.Count[net],
                Tanks = _tanksOf.Count[net],
                Consumers = _consOf.Count[net],
                Producers = _prodOf.Count[net],
                ValvesIn = _valveInOf.Count[net],
                ValvesOut = _valveOutOf.Count[net],
                MinTier = _nMinTier[net],
                MinTierCells = _nMinCount[net],
                Mixed = _nMixed[net] != 0,
                BottleneckX = _nBx[net],
                BottleneckY = _nBy[net],
                CapLpm = capLpm,
                SupplyLpm = WindowLpm(net, _pSupply, _aSupply, _nSupply),
                DemandLpm = WindowLpm(net, _pDemand, _aDemand, _nDemand),
                DeliveredLpm = WindowLpm(net, _pMoved, _aMoved, _nMoved),
                BufferedLpm = WindowLpm(net, _pFill, _aFill, _nFill),
                UnmetLpm = WindowLpm(net, _pUnmet, _aUnmet, _nUnmet),
                StoredMl = stored,
                StorageCapMl = storeCap,
                Issues = (PipeNetIssue)_nIssues[net],
                LastFlowStep = _nLastFlow[net],
            };
            return true;
        }

        /// <summary>全部格的流体量（储罐 + 阀门缓冲，毫升）——守恒核对用，O(格数)。</summary>
        public long TotalStoredSlow()
        {
            Compact();
            long t = 0;
            for (int i = 0; i < _n; i++)
            {
                t += _stock[i] + _buf[i];
            }
            return t;
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────

        public PipeSnapshot Serialize()
        {
            Rebuild();
            var s = new PipeSnapshot
            {
                FormatVersion = PipeConst.FormatVersion,
                StepIndex = StepIndex,
                TotalPumpedMl = TotalPumpedMl,
                TotalDeliveredMl = TotalDeliveredMl,
                TotalFlushedMl = TotalFlushedMl,
                TotalRemovedMl = TotalRemovedMl,
                X = new int[_n],
                Y = new int[_n],
                Kind = new byte[_n],
                Tier = new byte[_n],
                Dir = new byte[_n],
                Fluid = new byte[_n],
                Stock = new long[_n],
                Mode = new byte[_n],
                Priority = new byte[_n],
                Open = new byte[_n],
                Buffer = new long[_n],
                BufferFluid = new byte[_n],
                LastFlow = new long[_n],
                PumpTotal = new long[_n],
                ConsumerId = new int[_cn],
                ConsumerX = new int[_cn],
                ConsumerY = new int[_cn],
                ConsumerFluid = new byte[_cn],
                ConsumerLpm = new int[_cn],
                ConsumerPriority = new byte[_cn],
                ConsumerTotal = new long[_cn],
                NextConsumerId = _nextConsumerId,
                ConsumerBuffer = new long[_cn],
                ConsumerCapacity = new long[_cn],
                ProducerId = new int[_rn],
                ProducerX = new int[_rn],
                ProducerY = new int[_rn],
                ProducerFluid = new byte[_rn],
                ProducerStock = new long[_rn],
                ProducerCapacity = new long[_rn],
                ProducerTotal = new long[_rn],
                NextProducerId = _nextProducerId,
                TotalProducedOutMl = TotalProducedOutMl,
            };
            Array.Copy(_x, s.X, _n);
            Array.Copy(_y, s.Y, _n);
            Array.Copy(_kind, s.Kind, _n);
            Array.Copy(_tier, s.Tier, _n);
            Array.Copy(_dir, s.Dir, _n);
            Array.Copy(_fluid, s.Fluid, _n);
            Array.Copy(_stock, s.Stock, _n);
            Array.Copy(_mode, s.Mode, _n);
            Array.Copy(_prio, s.Priority, _n);
            Array.Copy(_open, s.Open, _n);
            Array.Copy(_buf, s.Buffer, _n);
            Array.Copy(_bufFluid, s.BufferFluid, _n);
            Array.Copy(_pumpTotal, s.PumpTotal, _n);
            for (int i = 0; i < _n; i++)
            {
                s.LastFlow[i] = _net[i] >= 0 ? _nLastFlow[_net[i]] : -1;
            }
            Array.Copy(_cId, s.ConsumerId, _cn);
            Array.Copy(_cX, s.ConsumerX, _cn);
            Array.Copy(_cY, s.ConsumerY, _cn);
            Array.Copy(_cFluid, s.ConsumerFluid, _cn);
            Array.Copy(_cLpm, s.ConsumerLpm, _cn);
            Array.Copy(_cPrio, s.ConsumerPriority, _cn);
            Array.Copy(_cTotal, s.ConsumerTotal, _cn);
            Array.Copy(_cBuf, s.ConsumerBuffer, _cn);
            Array.Copy(_cCap, s.ConsumerCapacity, _cn);
            Array.Copy(_rId, s.ProducerId, _rn);
            Array.Copy(_rX, s.ProducerX, _rn);
            Array.Copy(_rY, s.ProducerY, _rn);
            Array.Copy(_rFluid, s.ProducerFluid, _rn);
            Array.Copy(_rStock, s.ProducerStock, _rn);
            Array.Copy(_rCap, s.ProducerCapacity, _rn);
            Array.Copy(_rTotal, s.ProducerTotal, _rn);
            return s;
        }

        /// <summary>
        /// 从快照恢复（内核须为空）。格式版本不认识 → 返回 false、什么都不改（热更层原样保留存档）。单条记录非法（坐标重复 / 越界、种类 / 等级 / 方向 / 流体 / 模式 /
        /// 优先级越界、存量为负）→ 丢弃那一条并计数（<see cref="DroppedOnLoad"/>），其余照常恢复。储罐存量超过当前容量 → 夹到容量，多出的计入 <see cref="ClampedOnLoadMl"/>。
        /// </summary>
        public bool Deserialize(PipeSnapshot s, out string error)
        {
            error = null;
            DroppedOnLoad = 0;
            ClampedOnLoadMl = 0;
            Compact();
            if (s == null)
            {
                error = "snapshot_null";
                return false;
            }
            if (s.FormatVersion != PipeConst.FormatVersion)
            {
                error = "format_version";
                return false;
            }
            if (_n != 0 || _cn != 0 || _rn != 0)
            {
                error = "not_empty";
                return false;
            }
            int n = s.X?.Length ?? 0;
            bool shapeOk = s.Y?.Length == n && s.Kind?.Length == n && s.Tier?.Length == n && s.Dir?.Length == n && s.Fluid?.Length == n
                           && s.Stock?.Length == n && s.Mode?.Length == n && s.Priority?.Length == n && s.Open?.Length == n
                           && s.Buffer?.Length == n && s.BufferFluid?.Length == n && s.LastFlow?.Length == n && s.PumpTotal?.Length == n;
            if (!shapeOk)
            {
                error = "shape";
                return false;
            }
            StepIndex = Math.Max(0, s.StepIndex);
            TotalPumpedMl = s.TotalPumpedMl;
            TotalDeliveredMl = s.TotalDeliveredMl;
            TotalFlushedMl = s.TotalFlushedMl;
            TotalRemovedMl = s.TotalRemovedMl;
            TotalProducedOutMl = Math.Max(0, s.TotalProducedOutMl);
            long tankCap = _config.TankLiters * 1000L;
            for (int k = 0; k < n; k++)
            {
                int x = s.X[k], y = s.Y[k];
                byte kind = s.Kind[k];
                bool ok = Math.Abs(x) <= PipeConst.CoordLimit && Math.Abs(y) <= PipeConst.CoordLimit && kind <= (byte)PipePieceKind.Underground
                          && s.Tier[k] < PipeConst.TierCount && s.Dir[k] <= 3 && s.Fluid[k] <= PipeConst.MaxFluid && s.BufferFluid[k] <= PipeConst.MaxFluid
                          && s.Mode[k] <= (byte)PipeTankMode.OutOnly && s.Priority[k] >= PipeConst.PriorityMin && s.Priority[k] <= PipeConst.PriorityMax
                          && s.Stock[k] >= 0 && s.Buffer[k] >= 0 && s.PumpTotal[k] >= 0
                          && (kind != (byte)PipePieceKind.Pump || s.Fluid[k] >= 1)
                          && !_lookup.ContainsKey(Key(x, y));
                if (!ok)
                {
                    DroppedOnLoad++;
                    continue;
                }
                EnsureCells(_n + 1);
                int i = _n++;
                _x[i] = x;
                _y[i] = y;
                _kind[i] = kind;
                _tier[i] = s.Tier[k];
                _dir[i] = s.Dir[k];
                _fluid[i] = s.Fluid[k];
                _stock[i] = kind == (byte)PipePieceKind.Tank ? s.Stock[k] : 0;
                if (_stock[i] > tankCap)
                {
                    // 调低了储罐容量（logistics.pipe.tank_liters）后读老存档：夹到新容量，多出的计数（热更层提示），不丢整座储罐。
                    ClampedOnLoadMl += _stock[i] - tankCap;
                    _stock[i] = tankCap;
                }
                _mode[i] = s.Mode[k];
                _prio[i] = s.Priority[k];
                _open[i] = s.Open[k] != 0 ? (byte)1 : (byte)0;
                _buf[i] = kind == (byte)PipePieceKind.Valve ? s.Buffer[k] : 0;
                _bufFluid[i] = kind == (byte)PipePieceKind.Valve ? s.BufferFluid[k] : (byte)0;
                _pumpTotal[i] = s.PumpTotal[k];
                _seed[i] = s.LastFlow[k];
                _net[i] = -1;
                _stepIn[i] = 0;
                _stepOut[i] = 0;
                _mismatch[i] = 0;
                _lookup[Key(x, y)] = i;
            }
            int cn = s.ConsumerId?.Length ?? 0;
            if (s.ConsumerX?.Length == cn && s.ConsumerY?.Length == cn && s.ConsumerFluid?.Length == cn && s.ConsumerLpm?.Length == cn
                && s.ConsumerPriority?.Length == cn && s.ConsumerTotal?.Length == cn)
            {
                // FG4-ECO-02：缓存两列是新加的，旧存档没有（或长度对不上）时按 0（= 之前的“送达即消耗”）；流体 0 = 收任何流体。
                bool buffers = s.ConsumerBuffer?.Length == cn && s.ConsumerCapacity?.Length == cn;
                for (int k = 0; k < cn; k++)
                {
                    if (s.ConsumerFluid[k] > PipeConst.MaxFluid || s.ConsumerLpm[k] < 0
                        || s.ConsumerPriority[k] < PipeConst.PriorityMin || s.ConsumerPriority[k] > PipeConst.PriorityMax)
                    {
                        DroppedOnLoad++;
                        continue;
                    }
                    EnsureConsumers(_cn + 1);
                    int c = _cn++;
                    _cIndexDirty = true;
                    _cId[c] = s.ConsumerId[k];
                    _cX[c] = s.ConsumerX[k];
                    _cY[c] = s.ConsumerY[k];
                    _cFluid[c] = s.ConsumerFluid[k];
                    _cLpm[c] = s.ConsumerLpm[k];
                    _cPrio[c] = s.ConsumerPriority[k];
                    _cNet[c] = -1;
                    _cDemand[c] = 0;
                    _cGot[c] = 0;
                    _cTotal[c] = s.ConsumerTotal[k];
                    long cap = buffers ? s.ConsumerCapacity[k] : 0;
                    _cCap[c] = cap > 0 && _cFluid[c] != PipeConst.AnyFluid ? cap : 0;
                    _cBuf[c] = _cCap[c] > 0 ? Math.Max(0, Math.Min(_cCap[c], s.ConsumerBuffer[k])) : 0;
                }
            }
            else if (cn > 0)
            {
                DroppedOnLoad += cn;
            }
            _nextConsumerId = Math.Max(1, s.NextConsumerId);
            for (int c = 0; c < _cn; c++)
            {
                _nextConsumerId = Math.Max(_nextConsumerId, _cId[c] + 1);
            }
            // FG4-ECO-02：外部供给者（旧存档没有这几列 = 空）。
            int rn = s.ProducerId?.Length ?? 0;
            if (s.ProducerX?.Length == rn && s.ProducerY?.Length == rn && s.ProducerFluid?.Length == rn && s.ProducerStock?.Length == rn
                && s.ProducerCapacity?.Length == rn && s.ProducerTotal?.Length == rn)
            {
                for (int k = 0; k < rn; k++)
                {
                    if (s.ProducerFluid[k] < 1 || s.ProducerFluid[k] > PipeConst.MaxFluid || s.ProducerCapacity[k] <= 0 || s.ProducerStock[k] < 0)
                    {
                        DroppedOnLoad++;
                        continue;
                    }
                    EnsureProducers(_rn + 1);
                    int r = _rn++;
                    _rIndexDirty = true;
                    _rId[r] = s.ProducerId[k];
                    _rX[r] = s.ProducerX[k];
                    _rY[r] = s.ProducerY[k];
                    _rFluid[r] = s.ProducerFluid[k];
                    _rCap[r] = s.ProducerCapacity[k];
                    _rStock[r] = Math.Min(s.ProducerStock[k], s.ProducerCapacity[k]);
                    _rNet[r] = -1;
                    _rOut[r] = 0;
                    _rTotal[r] = Math.Max(0, s.ProducerTotal[k]);
                }
            }
            else if (rn > 0)
            {
                DroppedOnLoad += rn;
            }
            _nextProducerId = Math.Max(1, s.NextProducerId);
            for (int r = 0; r < _rn; r++)
            {
                _nextProducerId = Math.Max(_nextProducerId, _rId[r] + 1);
            }
            _netCount = 0;
            MarkDirty();
            Rebuild();
            if (DroppedOnLoad > 0)
            {
                error = "records_dropped";
            }
            return true;
        }

        /// <summary>全部状态的指纹（确定性对照：一直跑 vs 存读档接着跑、观察 vs 不观察）。不含派生的网络编号。</summary>
        public ulong Hash()
        {
            Rebuild();
            ulong h = 1469598103934665603UL;
            void Mix(long v)
            {
                unchecked
                {
                    for (int b = 0; b < 8; b++)
                    {
                        h ^= (byte)(v >> (b * 8));
                        h *= 1099511628211UL;
                    }
                }
            }
            Mix(StepIndex);
            Mix(TotalPumpedMl);
            Mix(TotalDeliveredMl);
            Mix(TotalFlushedMl);
            Mix(TotalRemovedMl);
            for (int i = 0; i < _n; i++)
            {
                Mix(_x[i]);
                Mix(_y[i]);
                Mix(_kind[i] | (_tier[i] << 8) | (_dir[i] << 16) | ((long)_fluid[i] << 24) | ((long)_mode[i] << 32) | ((long)_prio[i] << 40) | ((long)_open[i] << 48) | ((long)_bufFluid[i] << 56));
                Mix(_stock[i]);
                Mix(_buf[i]);
                Mix(_pumpTotal[i]);
                Mix(_net[i] >= 0 ? _nLastFlow[_net[i]] : -1);
            }
            for (int c = 0; c < _cn; c++)
            {
                Mix(_cId[c]);
                Mix(_cTotal[c]);
                Mix(_cLpm[c] | ((long)_cPrio[c] << 32));
                Mix(_cBuf[c]);
                Mix(_cCap[c]);
            }
            Mix(TotalProducedOutMl);
            for (int r = 0; r < _rn; r++)
            {
                Mix(_rId[r]);
                Mix(_rX[r]);
                Mix(_rY[r]);
                Mix(_rFluid[r]);
                Mix(_rStock[r]);
                Mix(_rCap[r]);
                Mix(_rTotal[r]);
            }
            return h;
        }

        // ── 渲染 ─────────────────────────────────────────────────────────────────

        private PipeInstance[] _render = new PipeInstance[64];
        private int _renderRevision = -1;
        private int _renderCount;

        /// <summary>
        /// 渲染实例（内核版本变了才重填，O(格数)，在本 AOT 程序集里；没变时 O(1)）。图标：泵 / 储罐 / 阀门每件都画，管线按坐标每 <paramref name="iconEvery"/> 格画一个
        /// （颜色之外还有形状区分流体，B15）。
        /// </summary>
        public PipeInstance[] PrepareRender(int iconEvery, out int count, out bool changed)
        {
            Rebuild();
            changed = _renderRevision != Revision;
            if (changed)
            {
                if (_render.Length < _n)
                {
                    _render = new PipeInstance[Math.Max(64, _n * 3 / 2)];
                }
                int every = Math.Max(1, iconEvery);
                long tankCap = Math.Max(1L, _config.TankLiters * 1000L);
                for (int i = 0; i < _n; i++)
                {
                    bool valve = _kind[i] == (byte)PipePieceKind.Valve;
                    int fluid = valve ? (_bufFluid[i] != 0 ? _bufFluid[i] : (_valA[i] >= 0 ? _nFluid[_valA[i]] : 0)) : _fluid[i];
                    int flags = 0;
                    if (_kind[i] != (byte)PipePieceKind.Pipe || (((_x[i] + _y[i]) % every) + every) % every == 0)
                    {
                        flags |= 1;
                    }
                    if (valve && _open[i] == 0)
                    {
                        flags |= 2;
                    }
                    if (valve && _mismatch[i] != 0)
                    {
                        flags |= 4;
                    }
                    if (_kind[i] == (byte)PipePieceKind.Underground && _partner[i] < 0)
                    {
                        flags |= 8; // 地下管线口没配对：着色器叠红色斜纹（颜色之外有花纹，B15）
                    }
                    _render[i] = new PipeInstance
                    {
                        Ax = _x[i],
                        Ay = _y[i],
                        Az = _kind[i] + 8 * _dir[i], // FG4-ECO-04：种类 0～7 + 8 × 方向（着色器同步解码）
                        Aw = _mask[i],
                        Bx = fluid,
                        By = _tier[i],
                        Bz = _kind[i] == (byte)PipePieceKind.Tank ? (float)((double)_stock[i] / tankCap) : 0f,
                        Bw = flags,
                    };
                }
                _renderCount = _n;
                _renderRevision = Revision;
            }
            count = _renderCount;
            return _render;
        }

        // ── 容量 ─────────────────────────────────────────────────────────────────

        private void EnsureCells(int need)
        {
            if (need <= _x.Length)
            {
                return;
            }
            int cap = Math.Max(need, _x.Length * 2);
            Array.Resize(ref _x, cap);
            Array.Resize(ref _y, cap);
            Array.Resize(ref _kind, cap);
            Array.Resize(ref _tier, cap);
            Array.Resize(ref _dir, cap);
            Array.Resize(ref _fluid, cap);
            Array.Resize(ref _mode, cap);
            Array.Resize(ref _prio, cap);
            Array.Resize(ref _open, cap);
            Array.Resize(ref _bufFluid, cap);
            Array.Resize(ref _stock, cap);
            Array.Resize(ref _buf, cap);
            Array.Resize(ref _pumpTotal, cap);
            Array.Resize(ref _seed, cap);
            Array.Resize(ref _net, cap);
            Array.Resize(ref _oldNet, cap);
            Array.Resize(ref _mask, cap);
            Array.Resize(ref _valA, cap);
            Array.Resize(ref _valB, cap);
            Array.Resize(ref _partner, cap);
            Array.Resize(ref _stepIn, cap);
            Array.Resize(ref _stepOut, cap);
            Array.Resize(ref _bufStart, cap);
            Array.Resize(ref _mismatch, cap);
        }

        private void EnsureConsumers(int need)
        {
            if (need <= _cId.Length)
            {
                return;
            }
            int cap = Math.Max(need, _cId.Length * 2);
            Array.Resize(ref _cId, cap);
            Array.Resize(ref _cX, cap);
            Array.Resize(ref _cY, cap);
            Array.Resize(ref _cFluid, cap);
            Array.Resize(ref _cLpm, cap);
            Array.Resize(ref _cPrio, cap);
            Array.Resize(ref _cNet, cap);
            Array.Resize(ref _cDemand, cap);
            Array.Resize(ref _cGot, cap);
            Array.Resize(ref _cTotal, cap);
            Array.Resize(ref _cBuf, cap);
            Array.Resize(ref _cCap, cap);
        }

        private void EnsureNets(int need)
        {
            if (need <= _nFluid.Length)
            {
                return;
            }
            int cap = Math.Max(need, _nFluid.Length * 2);
            Array.Resize(ref _nFluid, cap);
            Array.Resize(ref _nCells, cap);
            Array.Resize(ref _nPipe, cap);
            Array.Resize(ref _nMinTier, cap);
            Array.Resize(ref _nMinCount, cap);
            Array.Resize(ref _nMixed, cap);
            Array.Resize(ref _nBx, cap);
            Array.Resize(ref _nBy, cap);
            Array.Resize(ref _nLastFlow, cap);
            Array.Resize(ref _nLastFlowOld, cap);
            Array.Resize(ref _nSupply, cap);
            Array.Resize(ref _nDemand, cap);
            Array.Resize(ref _nMoved, cap);
            Array.Resize(ref _nFill, cap);
            Array.Resize(ref _nUnmet, cap);
            Array.Resize(ref _nIssues, cap);
            Array.Resize(ref _aSupply, cap);
            Array.Resize(ref _aDemand, cap);
            Array.Resize(ref _aMoved, cap);
            Array.Resize(ref _aFill, cap);
            Array.Resize(ref _aUnmet, cap);
            Array.Resize(ref _aCount, cap);
            Array.Resize(ref _pSupply, cap);
            Array.Resize(ref _pDemand, cap);
            Array.Resize(ref _pMoved, cap);
            Array.Resize(ref _pFill, cap);
            Array.Resize(ref _pUnmet, cap);
            Array.Resize(ref _pValid, cap);
            Array.Resize(ref _pCount, cap);
        }

        private void EnsureQueue(int need)
        {
            if (need > _queue.Length)
            {
                _queue = new int[Math.Max(need, _queue.Length * 2)];
            }
        }

        private static void EnsureInts(ref int[] a, int need)
        {
            if (need > a.Length)
            {
                Array.Resize(ref a, Math.Max(need, a.Length * 2));
            }
        }

        /// <summary>压缩行存储：每个网络一段（Start、Count），元素连续放在 Items 里。重算时按网络顺序一次写完。</summary>
        private sealed class Csr
        {
            public int[] Start = new int[16];
            public int[] Count = new int[16];
            public int[] Items = new int[64];
            private int _len;
            private int _open;

            public void Begin(int maxItems)
            {
                _len = 0;
                if (Items.Length < maxItems)
                {
                    Items = new int[Math.Max(maxItems, Items.Length * 2)];
                }
            }

            public void Open(int net)
            {
                if (net >= Start.Length)
                {
                    int cap = Math.Max(net + 1, Start.Length * 2);
                    Array.Resize(ref Start, cap);
                    Array.Resize(ref Count, cap);
                }
                _open = net;
                Start[net] = _len;
                Count[net] = 0;
            }

            public void Add(int item)
            {
                if (_len >= Items.Length)
                {
                    Array.Resize(ref Items, Items.Length * 2);
                }
                Items[_len++] = item;
                Count[_open]++;
            }

            public void Close()
            {
            }
        }
    }
}
