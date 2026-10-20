using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-FAC-01（FG09 第 4 章；承接 DEBT-FG8GEN02-01）：室内场地的敌人与首领内容（地下中继大厅）。由 <see cref="InteriorSiteController"/> 在有手工点位的室内表面上创建。
    ///
    /// - 敌人：编成表 relay_hall 的 base 行（监听节点、磁暴棱柱、回声猎手 ×2、诱饵中继、寂听者）在第一次进入时播种，之后跟着记录（打掉的不复活）；
    ///   每次开战按当天锁定的静默反制追加 counter_* 行（FGR-FAC-004）。内核单位全部经 <see cref="EnemySpawnBuilder"/> 按表生成（读法 + 标签）。
    /// - 首领：新进入（不是读档恢复）且首领在休眠 → <see cref="SilentMindBoss.Engage"/>；每个模拟步 <see cref="SilentMindBoss.Tick"/>；部件伤害经
    ///   <see cref="RelayHallCombatRules.ApplyExternalEnemyDamage"/> 交给状态机；阶段变了同步内核标志并对账召唤物。
    /// - 全灭 / 全员撤出（室内卸载且删快照）→ <see cref="SilentMindBoss.ResetToPreFight"/>（ER7-FAIL-01 同一语义）；读档 / 世界卸载不复位。
    /// - 伏笔地点（fg.TbInteriorPoint kind = poi）：内核兴趣点，机器走近即读取，收进资料库（FG9-NAR-01）并提示一次。
    /// - 表现（被观察时）：敌人占位几何体 + 头顶标记（按读法 / 标签 / 阶段着色），偏折场光环、断链束光线、中继节点亮起 / 真节点、主脑藏身时隐藏；
    ///   刷新由事件驱动（阵亡 / 潜伏 / 偏折场阶段 / 首领修订号），另每 0.25 真实秒对账一次可见的静默状态（大厅内容有上限，O(大厅敌人数) ≤ 约 20）。
    /// </summary>
    public sealed class RelayHallContent
    {
        public readonly string SiteId;
        public readonly RelayHallCombatRules Rules;
        private CombatSite _combat;
        private GameObject _root;
        private readonly Dictionary<string, GameObject> _views = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, WorldBadge> _badges = new Dictionary<string, WorldBadge>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _fields = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, LineRenderer> _beams = new Dictionary<string, LineRenderer>(StringComparer.Ordinal);
        private readonly List<InteriorPoint> _pois = new List<InteriorPoint>(2);
        private readonly GroundLootVisuals _loot = new GroundLootVisuals();
        private int _bossRevision = int.MinValue;
        private int _recordsSeen = -1;
        private float _visualTimer;
        private bool _dirtyVisuals;

        /// <summary>半透明占位（潜伏单位、偏折场光环）：带 alpha 混合的着色器，与其他半透明叠加层（覆盖圈、屋顶预览）相同。</summary>
        private const string TransparentShader = "Sprites/Default";
        private static readonly List<Renderer> RendererScratch = new List<Renderer>(8);

        public RelayHallContent(string siteId)
        {
            SiteId = siteId;
            Rules = new RelayHallCombatRules(this);
            foreach (InteriorPoint p in RelayHallLayout.Points(siteId))
            {
                if (p.Kind == RelayHallLayout.KindPoi)
                {
                    _pois.Add(p);
                }
            }
        }

        public IReadOnlyList<InteriorPoint> Pois => _pois;
        public CombatSite Combat => _combat;
        public int ViewCount => _views.Count;

        // ── 载入 / 卸载 ──

        /// <summary>室内地点内核打开之后调用（<paramref name="restored"/> = 内核从存档快照恢复）。<paramref name="resume"/> = 读档恢复（机器本来就在室内）。</summary>
        public void OnOpened(CampaignState state, CombatSite combat, bool resume, bool restored)
        {
            _combat = combat;
            SilentCombatFeedback.Changed -= OnSilentChanged;
            SilentCombatFeedback.Changed += OnSilentChanged;
            if (!restored)
            {
                var pts = new List<float3>();
                var reached = new List<bool>();
                foreach (InteriorPoint p in _pois)
                {
                    if (RelayHallLayout.TryPoint(state, p.Id, out Vector2 at))
                    {
                        pts.Add(new float3(at.x, at.y, PoiRadius));
                        reached.Add(Array.IndexOf(state.Factions?.PoisRead ?? Array.Empty<string>(), p.Id) >= 0);
                    }
                }
                combat.SetPois(pts, reached);
            }
            RegionEnemySites.SeedEncounters(state, SiteId, EnemyDefs.ConditionBase);
            if (!resume)
            {
                SilentMindBoss.Engage(state);
                string counter = SilentMindBoss.Record(state)?.CounterId;
                if (!string.IsNullOrEmpty(counter))
                {
                    RegionEnemySites.SeedEncounters(state, SiteId, counter);
                }
            }
            Reconcile(state);
        }

        /// <summary>室内卸载（<paramref name="dropRecord"/> = 全部离开 / 全灭，快照删除）：首领战没打完就复位（读档 / 世界卸载不复位）。</summary>
        public void OnUnloaded(CampaignState state, bool dropRecord, bool wiped)
        {
            SilentCombatFeedback.Changed -= OnSilentChanged;
            if (dropRecord && state != null)
            {
                SilentMindBoss.ResetToPreFight(state, wiped);
            }
            DestroyVisuals();
            _combat = null;
        }

        /// <summary>每个模拟步（与观察无关）：首领状态机推进；阶段 / 部件变了 → 对账内核单位与标志。</summary>
        public void SimStep(CampaignState state)
        {
            if (_combat == null || state == null)
            {
                return;
            }
            SilentMindBoss.Tick(state);
            int records = state.RegionEnemies?.Length ?? 0;
            if (SilentMindBoss.Revision != _bossRevision || records != _recordsSeen)
            {
                Reconcile(state);
            }
        }

        private void Reconcile(CampaignState state)
        {
            if (_combat == null || _combat.IsDisposed)
            {
                return;
            }
            CombatDemoContent.ReconcileEnemies(_combat, state, SiteId);
            SilentMindBoss.SyncFlags(_combat, state);
            _bossRevision = SilentMindBoss.Revision;
            _recordsSeen = state.RegionEnemies?.Length ?? 0;
            _dirtyVisuals = true;
        }

        public float PoiRadius => GridContentRadius();

        private static float GridContentRadius() => Grid.GridContent.TryGetTuning("interior.exit_radius", out float r) && r > 0f ? r : 6f;

        // ── 编队命令的敌对目标（右键敌人 = 攻击）──

        public RegionHostileInfo? FindHostileNear(Vector2 p, float radius) =>
            _combat != null && !_combat.IsDisposed && _combat.TryFindNearestEnemy(p, radius, out string id) ? ResolveHostile(id) : null;

        public RegionHostileInfo? ResolveHostile(string id) =>
            _combat != null && _combat.TryGetEnemyPosition(id, out Vector2 pos) ? new RegionHostileInfo(id, pos, _combat.IsEnemyAlive(id)) : (RegionHostileInfo?)null;

        public int HostileUnit(string id) => _combat != null && _combat.TryGetEnemyUnit(id, out int u) ? u : 0;

        // ── 伏笔地点 ──

        public static int PoisReadCount { get; private set; }

        internal void OnPoiReached(int index)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || index < 0 || index >= _pois.Count)
            {
                return;
            }
            InteriorPoint p = _pois[index];
            CampaignFgStateDomains.EnsureAll(state);
            if (Array.IndexOf(state.Factions.PoisRead, p.Id) >= 0)
            {
                return;
            }
            var list = new List<string>(state.Factions.PoisRead) { p.Id };
            list.Sort(StringComparer.Ordinal);
            state.Factions.PoisRead = list.ToArray();
            PoisReadCount++;
            GameEvent.Send(Narrative.NarrativeService.FeedEvent, Narrative.NarrativeCatalog.TriggerTerminal + p.BodyKey);
            RelayHallLayout.TryPoint(state, p.Id, out Vector2 at);
            NotificationCenter.Post("silent_boss", GameText.Format("interior.poi.read", GameText.Get(p.NameKey)), new Vector3(at.x, 0f, at.y));
        }

        // ── 表现 ──

        public void BuildVisuals(CampaignState state, Transform root)
        {
            DestroyVisuals();
            _root = new GameObject("[RelayHallContent]");
            _root.transform.SetParent(root, false);
            foreach (InteriorPoint p in _pois)
            {
                if (RelayHallLayout.TryPoint(state, p.Id, out Vector2 at))
                {
                    // 天空扫描墙：贴墙的一面浅色板 + 名字标（B15 形状 + 颜色区分）。
                    GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    wall.name = "Poi_" + p.Id;
                    wall.transform.SetParent(_root.transform, false);
                    wall.transform.position = new Vector3(at.x, 1.5f, at.y);
                    wall.transform.localScale = new Vector3(0.3f, 3f, 5f);
                    UnityObjects.Release(wall.GetComponent<Collider>());
                    wall.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(new Color(0.85f, 0.85f, 0.7f));
                }
            }
            _dirtyVisuals = true;
            SyncVisuals(state);
        }

        public void DestroyVisuals()
        {
            foreach (GameObject go in _views.Values)
            {
                if (go != null && _combat != null && !_combat.IsDisposed && _combat.TryGetEnemyUnit(go.name.Substring(6), out int unit))
                {
                    _combat.Sync?.Unbind(unit);
                }
            }
            _views.Clear();
            _badges.Clear();
            _fields.Clear();
            _beams.Clear();
            _loot.Clear();
            if (_root != null)
            {
                UnityObjects.Release(_root);
                _root = null;
            }
        }

        private void OnSilentChanged(CombatSite site, int unit)
        {
            if (ReferenceEquals(site, _combat))
            {
                _dirtyVisuals = true;
            }
        }

        /// <summary>被观察时每帧调用：事件脏了立即对账，另每 0.25 真实秒对账一次（偏折场倒计时 / 断链束光线随单位移动）。</summary>
        public void FrameUpdate(CampaignState state)
        {
            if (_root == null || state == null)
            {
                return;
            }
            _visualTimer -= Time.unscaledDeltaTime;
            if (!_dirtyVisuals && _visualTimer > 0f && _bossRevision == SilentMindBoss.Revision)
            {
                return;
            }
            _visualTimer = 0.25f;
            SyncVisuals(state);
        }

        private void SyncVisuals(CampaignState state)
        {
            _dirtyVisuals = false;
            if (_root == null || state?.RegionEnemies == null || _combat == null || _combat.IsDisposed)
            {
                return;
            }
            _loot.Sync(state, SiteId, _root.transform, includeGround: true);
            SilentBossRecord boss = SilentMindBoss.Record(state);
            SilentMindPhase phase = SilentMindBoss.PhaseOf(state);
            bool intel = SilentMindBoss.RealNodeKnown(state) || SilentMindBoss.IsRevealed(state);
            foreach (RegionEnemyRecord e in state.RegionEnemies)
            {
                if (e == null || e.RegionId != SiteId)
                {
                    continue;
                }
                if (!_views.TryGetValue(e.EnemyInstanceId, out GameObject go) || go == null)
                {
                    go = BuildEnemyView(e);
                }
                if (go == null)
                {
                    continue;
                }
                bool unitKnown = _combat.TryGetEnemyUnit(e.EnemyInstanceId, out int unit);
                CombatUnitFlags flags = unitKnown && _combat.UnitHasFlag(unit, CombatUnitFlags.Alive) ? FlagsOf(unit) : CombatUnitFlags.None;
                bool alive = e.IsAlive && (flags & CombatUnitFlags.Alive) != 0;
                bool stealth = (flags & CombatUnitFlags.Stealthed) != 0;
                bool hiddenCore = e.EnemyInstanceId == SilentMindBoss.CoreInstanceId && (phase == SilentMindPhase.Hiding || phase == SilentMindPhase.Dormant);
                Color c = EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) ? EnemyDefs.ViewColor(row) : Color.magenta;
                int node = SilentMindBoss.NodeIndexOf(e.EnemyInstanceId);
                if (node >= 0 && boss != null && alive && phase == SilentMindPhase.Hiding)
                {
                    if (node == boss.JumpTarget)
                    {
                        c = new Color(1f, 0.85f, 0.2f); // 中继跳转：目标节点提前亮起（预警）
                    }
                    else if (node == boss.RealNode && intel)
                    {
                        c = new Color(0.95f, 0.25f, 0.3f); // 情报 / 现形：真节点
                    }
                }
                // 潜伏 = 半透明（Standard 是不透明材质、alpha 不生效：潜伏改用带透明混合的共享材质，与其他半透明叠加层同一个着色器）。
                Material mat = !alive ? ViewMaterials.Standard(new Color(0.25f, 0.25f, 0.25f))
                    : stealth ? ViewMaterials.Get(TransparentShader, new Color(c.r, c.g, c.b, 0.25f))
                    : ViewMaterials.Standard(c);
                go.GetComponentsInChildren(true, RendererScratch);
                foreach (Renderer part in RendererScratch)
                {
                    if (part.sharedMaterial != mat)
                    {
                        part.sharedMaterial = mat;
                    }
                }
                RendererScratch.Clear();
                go.SetActive(!hiddenCore && (alive || node < 0));
                if (_badges.TryGetValue(e.EnemyInstanceId, out WorldBadge badge) && badge != null)
                {
                    badge.SetVisible(alive && !stealth && !hiddenCore);
                }
                SyncField(e, unit, unitKnown && alive);
                SyncBeam(e, unit, unitKnown && alive);
            }
        }

        private CombatUnitFlags FlagsOf(int unit)
        {
            CombatUnitFlags f = CombatUnitFlags.None;
            if (_combat.UnitHasFlag(unit, CombatUnitFlags.Alive))
            {
                f |= CombatUnitFlags.Alive;
            }
            if (_combat.UnitHasFlag(unit, CombatUnitFlags.Stealthed))
            {
                f |= CombatUnitFlags.Stealthed;
            }
            if (_combat.UnitHasFlag(unit, CombatUnitFlags.DeflectArmed))
            {
                f |= CombatUnitFlags.DeflectArmed;
            }
            return f;
        }

        private GameObject BuildEnemyView(RegionEnemyRecord e)
        {
            Vector2 at = _combat.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 live) ? live : e.Position;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Enemy_" + e.EnemyInstanceId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(at.x, 1f, at.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) ? EnemyDefs.ViewColor(row) : Color.magenta;
            renderer.sharedMaterial = ViewMaterials.Standard(baseColor);
            PlaceholderSilhouette.ApplyEnemy(go, renderer, e.EnemyTypeId);
            if (_combat.TryGetEnemyUnit(e.EnemyInstanceId, out int unit))
            {
                _combat.BindView(unit, go.transform, 1f);
            }
            WorldBadge badge = WorldBadge.Create(go.transform, "Badge", go.transform.position + Vector3.up * 2.2f, 1.1f);
            badge.SetIcon(UI.Common.ContentIcons.IconIdFor(e.EnemyTypeId));
            _badges[e.EnemyInstanceId] = badge;
            _views[e.EnemyInstanceId] = go;
            return go;
        }

        /// <summary>偏折场光环：预警 = 琥珀色细环，展开 = 蓝色半透明圆盘（半径 = 行为参数）。</summary>
        private void SyncField(RegionEnemyRecord e, int unit, bool alive)
        {
            if (!alive || !_combat.TryGetUnitSilent(unit, out CombatSilentUnit sl, out CombatBehaviorProfile p) || p.DeflectRadius <= 0f)
            {
                if (_fields.TryGetValue(e.EnemyInstanceId, out GameObject old) && old != null)
                {
                    old.SetActive(false);
                }
                return;
            }
            if (!_fields.TryGetValue(e.EnemyInstanceId, out GameObject ring) || ring == null)
            {
                ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "DeflectField_" + e.EnemyInstanceId;
                ring.transform.SetParent(_root.transform, false);
                UnityObjects.Release(ring.GetComponent<Collider>());
                _fields[e.EnemyInstanceId] = ring;
            }
            bool armed = _combat.UnitHasFlag(unit, CombatUnitFlags.DeflectArmed);
            ring.SetActive(armed && sl.Deflect != 0);
            if (_combat.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 at))
            {
                ring.transform.position = new Vector3(at.x, 0.05f, at.y);
            }
            float rad = Mathf.Min(p.DeflectRadius, 60f);
            ring.transform.localScale = new Vector3(rad * 2f, sl.Deflect == 2 ? 0.04f : 0.02f, rad * 2f);
            // 半透明光环：带透明混合的共享材质（Standard 不透明，80 米的偏折场会画成一整块实心圆盘挡住大厅）。
            ring.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Get(TransparentShader, sl.Deflect == 2 ? new Color(0.35f, 0.55f, 1f, 0.28f) : new Color(1f, 0.7f, 0.2f, 0.35f));
        }

        /// <summary>断链束：照射中画一条从寂听者到被接入机器的红线（光束本身就是预警线）。</summary>
        private void SyncBeam(RegionEnemyRecord e, int unit, bool alive)
        {
            bool on = alive && _combat.TryGetUnitSilent(unit, out CombatSilentUnit sl, out CombatBehaviorProfile p) && p.SeverRange > 0f && sl.SeverTarget > 0 && sl.SeverT > 0f;
            if (!on)
            {
                if (_beams.TryGetValue(e.EnemyInstanceId, out LineRenderer oldBeam) && oldBeam != null)
                {
                    oldBeam.enabled = false;
                }
                return;
            }
            _combat.TryGetUnitSilent(unit, out CombatSilentUnit s2, out _);
            if (!_beams.TryGetValue(e.EnemyInstanceId, out LineRenderer beam) || beam == null)
            {
                var go = new GameObject("SeverBeam_" + e.EnemyInstanceId);
                go.transform.SetParent(_root.transform, false);
                beam = go.AddComponent<LineRenderer>();
                beam.positionCount = 2;
                beam.startWidth = beam.endWidth = 0.25f;
                beam.sharedMaterial = ViewMaterials.Standard(new Color(1f, 0.15f, 0.3f));
                _beams[e.EnemyInstanceId] = beam;
            }
            if (_combat.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 from) && _combat.TryGetUnitPosition(s2.SeverTarget, out double2 to))
            {
                beam.enabled = true;
                beam.SetPosition(0, new Vector3(from.x, 1.4f, from.y));
                beam.SetPosition(1, new Vector3((float)to.x, 1f, (float)to.y));
            }
        }
    }

    /// <summary>FG9-FAC-01：地下中继大厅的战斗结算挂点（首领部件伤害交状态机、阵亡掉落、伏笔地点）。</summary>
    public sealed class RelayHallCombatRules : CombatSiteRules
    {
        private readonly RelayHallContent _content;

        public RelayHallCombatRules(RelayHallContent content)
        {
            _content = content;
        }

        public override void OnEnemyDamaged(CombatSite site, RegionEnemyRecord enemy, float damage)
        {
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
        }

        public override void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy)
        {
            CampaignState state = CampaignSession.Current;
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyDestroyed, enemy.Position);
            if (state == null || !EnemyDefs.TryGet(enemy.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            Economy.TestRangeService.NoteEnemyDefeated(state, row.Id);
            if (EnemyDefs.IsElite(row))
            {
                // 精英（寂听者）：三选一数据包，交给附近的机器（FGR-LOOT-010 精英掉落）。
                int holder = Economy.LootPickService.NearestMachine(site.SiteId, enemy.Position, 30f, 0);
                Economy.LootPickService.Grant(state, Economy.LootPickService.SourceElite, row.Faction, holder, site.SiteId, enemy.Position);
            }
            if (row.ScrapLoot > 0)
            {
                HomeValleyCargo.SpawnGroundItem(state, site.SiteId, enemy.Position, CampaignEconomyLedger.ResourceScrap, row.ScrapLoot, enemy.EnemyInstanceId + ":scrap");
            }
        }

        public override bool ApplyExternalEnemyDamage(CombatSite site, RegionEnemyRecord enemy, float damage)
        {
            CampaignState state = CampaignSession.Current;
            (bool ok, string reason) = SilentMindBoss.ApplyDamage(state, enemy, damage);
            if (ok)
            {
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
            }
            else
            {
                Log.Info($"[RelayHall] 对 {enemy.EnemyInstanceId} 的伤害未结算：{reason}");
            }
            SilentMindBoss.SyncFlags(site, state);
            return ok;
        }

        public override string InvulnerableDetail(RegionEnemyRecord enemy) => SilentMindBoss.InvulnerableDetail(CampaignSession.Current, enemy);

        public override void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition) => _content.OnPoiReached(poiIndex);
    }
}
