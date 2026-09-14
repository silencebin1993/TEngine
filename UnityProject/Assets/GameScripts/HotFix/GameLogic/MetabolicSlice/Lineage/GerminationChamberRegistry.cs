using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Control;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>
    /// M3-05：萌生腔与新生传播。GDD §5 把"谱系"定义为"由同一萌生腔维持的繁殖家族"——
    /// 一个谱系天然对应一个萌生腔，本类型不另建 Pod 对象，直接按 <c>lineageId</c> 记萌生队列。
    ///
    /// 版本隔离（验收核心）：新生个体在生成那一刻绑定<b>当时的</b> <see cref="PhenotypeTemplateVersion"/>
    /// 对象引用（不可变，见该类型注释）。谱系之后提交新版本只是往历史里追加，绝不touch旧版本对象，
    /// 已生成个体的绑定字段也不会被这次提交改写——"旧单位保持 V1" 在结构上就是"没人碰过它的引用"。
    ///
    /// 只消费蓝图库/谱系已建立的对象模型（<see cref="LineageRegistry"/>/<see cref="BiomassLedger"/>），
    /// 不碰 ComposeEngine 核心与 <c>CarrierCompiler</c> 的既有职责——生成个体只是在
    /// <see cref="Control.UnitLoadoutRegistry"/> 里挂一份"主器官=模板 OrganelleId"的显式装配，
    /// 供内核驱动的 AI 自动开火路径消费；基因对装配的影响仍是 <c>CompileFromRecipe</c> 的事，
    /// 只有当这具身体未来被直控接管时才会经那条路真正结算（本期不做，见下）。
    ///
    /// 接管资格（自行拍板，见类型末尾方法 <see cref="BuildSpawnRequest"/>）：GDD 现有文本
    /// （§5.1/§6.6）没有把"萌生腔新生个体"列入玩家可意识传递对象的范围，M2-06 试玩门禁的
    /// 可接管白名单也只点名了两具固定友军。按更保守的方向处理——新生个体一律
    /// <c>ExcludeFromControl=true</c>，直到有规格明确要求把它们纳入接管候选。
    /// </summary>
    public sealed class GerminationChamberRegistry : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        /// <summary>萌生耗时占位数值（秒）。非目标"不做最终孕育动画"——这个数字只是让"入队→生成"
        /// 之间有一段可取消的窗口，不代表任何美术/节奏设计意图。</summary>
        public const float GerminationSeconds = 3f;

        /// <summary>新生个体的占位属性——本期原型槎位没有"底盘"驱动的数值来源（GDD §6.5 底盘槎位
        /// 留给后续故事），先给一组与现有可控友军量级一致的常量，真实数值表接入时按这几个常量替换。</summary>
        private const float PlaceholderHealth = 40f;
        private const float PlaceholderRadius = 0.8f;
        private const float PlaceholderSpeed = 4f;

        private sealed class GerminationTicket
        {
            public int TicketId;
            public string LineageId;
            public string TemplateName;
            public PhenotypeTemplateVersion Version;
            public float Cost;
            public float SecondsLeft;
            public bool Cancelled;
        }

        /// <summary>某实体绑定的谱系/表型/版本——供后续故事（M3-06 回巢改造）比较
        /// "这具身体当前的签名" vs "谱系表型的最新签名"来判断是否需要回巢。</summary>
        public readonly struct UnitBinding
        {
            public readonly string LineageId;
            public readonly string TemplateName;
            public readonly PhenotypeTemplateVersion Version;

            public UnitBinding(string lineageId, string templateName, PhenotypeTemplateVersion version)
            {
                LineageId = lineageId;
                TemplateName = templateName;
                Version = version;
            }
        }

        private struct PendingBind
        {
            public int LogicId;
            public string LineageId;
            public string TemplateName;
            public PhenotypeTemplateVersion Version;
            public int Attempts;
        }

        /// <summary>挂起解析重试上限，与 <see cref="Control.UnitLoadoutRegistry.MaxResolveAttempts"/> 同口径。</summary>
        private const int MaxResolveAttempts = 120;

        private readonly Dictionary<string, List<GerminationTicket>> _queues = new Dictionary<string, List<GerminationTicket>>();
        private readonly HashSet<string> _destroyedLineages = new HashSet<string>();
        private readonly Dictionary<SimEntityId, UnitBinding> _bindings = new Dictionary<SimEntityId, UnitBinding>();
        private readonly List<PendingBind> _pendingBinds = new List<PendingBind>(4);
        private readonly List<UnitLoadoutOrgan> _organScratch = new List<UnitLoadoutOrgan>(1);

        private SimBridge _sim;
        private LineageRegistry _lineages;
        private BiomassLedger _biomass;
        private UnitLoadoutRegistry _unitLoadouts;
        private int _nextTicketId = 1;
        private float _spawnOffsetStagger;

        public void Bind(SimBridge sim, LineageRegistry lineages, BiomassLedger biomass, UnitLoadoutRegistry unitLoadouts)
        {
            _sim = sim;
            _lineages = lineages;
            _biomass = biomass;
            _unitLoadouts = unitLoadouts;
        }

        public override void OnEnter()
        {
            _queues.Clear();
            _destroyedLineages.Clear();
            _bindings.Clear();
            _pendingBinds.Clear();
            _nextTicketId = 1;
            _spawnOffsetStagger = 0f;
        }

        /// <summary>入队一次萌生：取该谱系表型的<b>最新</b>版本、扣生物质（Reject-to-Safe：
        /// 余额不足或腔体已被摧毁则不入队、不扣费）。<paramref name="capturedVersion"/> 是本次入队
        /// 实际捕获、之后不会再变的版本对象——之后谱系再提交新版本，这张已入队的票也不会跟着换版本，
        /// 这正是"旧单位保持 V1、新单位表达 V2"的机制来源（生成时用的就是入队那一刻捕获的引用）。</summary>
        public int Enqueue(string lineageId, string templateName, out PhenotypeTemplateVersion capturedVersion, out string error)
        {
            capturedVersion = null;

            if (_destroyedLineages.Contains(lineageId))
            {
                error = "萌生腔已被摧毁，不能再入队";
                return 0;
            }

            Lineage lineage = _lineages?.GetLineage(lineageId);
            PhenotypeTemplateVersion version = lineage?.GetLatest(templateName);
            if (version == null)
            {
                error = $"谱系 {lineageId} 下没有可用的表型模板版本：{templateName}";
                return 0;
            }

            if (_biomass == null || !_biomass.TryDeduct(lineageId, version.BiomassCost))
            {
                error = "生物质不足，萌生腔拒绝入队";
                return 0;
            }

            var ticket = new GerminationTicket
            {
                TicketId = _nextTicketId++,
                LineageId = lineageId,
                TemplateName = templateName,
                Version = version,
                Cost = version.BiomassCost,
                SecondsLeft = GerminationSeconds,
                Cancelled = false,
            };

            if (!_queues.TryGetValue(lineageId, out List<GerminationTicket> queue))
            {
                queue = new List<GerminationTicket>();
                _queues[lineageId] = queue;
            }
            queue.Add(ticket);

            capturedVersion = version;
            error = null;
            return ticket.TicketId;
        }

        /// <summary>取消一个尚未完成的萌生项，全额退款。已完成/已取消/查无此单返回 false（幂等，
        /// 不会重复退款）。</summary>
        public bool Cancel(int ticketId)
        {
            foreach (List<GerminationTicket> queue in _queues.Values)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    GerminationTicket ticket = queue[i];
                    if (ticket.TicketId != ticketId || ticket.Cancelled)
                    {
                        continue;
                    }

                    ticket.Cancelled = true;
                    _biomass?.Refund(ticket.LineageId, ticket.Cost);
                    queue.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>萌生腔被摧毁：队列里所有未完成项整体失败退款，之后该谱系拒绝新的入队请求
        /// （GDD §5"谱系毁灭会失去本地生产能力"）。对已经生成的个体没有任何影响——它们已经是
        /// 地图上独立的真实单位，不会被腔体销毁连带清除。</summary>
        public void DestroyPod(string lineageId)
        {
            _destroyedLineages.Add(lineageId);

            if (_queues.TryGetValue(lineageId, out List<GerminationTicket> queue))
            {
                foreach (GerminationTicket ticket in queue)
                {
                    if (!ticket.Cancelled)
                    {
                        ticket.Cancelled = true;
                        _biomass?.Refund(ticket.LineageId, ticket.Cost);
                    }
                }
                queue.Clear();
            }
        }

        public int PendingCount(string lineageId)
        {
            return _queues.TryGetValue(lineageId, out List<GerminationTicket> queue) ? queue.Count : 0;
        }

        public bool TryGetBinding(SimEntityId entityId, out UnitBinding binding)
        {
            return _bindings.TryGetValue(entityId, out binding);
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            foreach (KeyValuePair<string, List<GerminationTicket>> pair in _queues)
            {
                List<GerminationTicket> queue = pair.Value;
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    GerminationTicket ticket = queue[i];
                    ticket.SecondsLeft -= dt;
                    if (ticket.SecondsLeft > 0f)
                    {
                        continue;
                    }

                    queue.RemoveAt(i);
                    SpawnFromTicket(ticket);
                }
            }

            ResolvePendingBinds();
        }

        private void SpawnFromTicket(GerminationTicket ticket)
        {
            int logicId = _sim.NextLogicId();
            _spawnOffsetStagger += 0.6f;

            _sim.Spawn(BuildSpawnRequest(ticket, logicId));

            _organScratch.Clear();
            _organScratch.Add(new UnitLoadoutOrgan(ticket.Version.OrganelleId, LoadoutAction.Primary));
            _unitLoadouts?.RegisterExplicitPending(logicId, _organScratch);

            _pendingBinds.Add(new PendingBind
            {
                LogicId = logicId,
                LineageId = ticket.LineageId,
                TemplateName = ticket.TemplateName,
                Version = ticket.Version,
                Attempts = 0,
            });
        }

        /// <summary>占位生成点：玩家位置旁按入队顺序摆开，避免叠在同一点（GDD 没有"萌生腔物理坐标"
        /// 概念，真实落点设计留给后续故事）。接管资格判断见类型注释——固定 ExcludeFromControl=true。</summary>
        private SpawnRequest BuildSpawnRequest(GerminationTicket ticket, int logicId)
        {
            float2 basePos = _sim.PlayerPosition + new float2(6f, -2f);
            float2 offset = new float2(_spawnOffsetStagger % 4f, (_spawnOffsetStagger / 4f) % 4f);

            return new SpawnRequest
            {
                Position = basePos + offset,
                Velocity = float2.zero,
                Health = PlaceholderHealth,
                Radius = PlaceholderRadius,
                MaxSpeed = PlaceholderSpeed,
                ArchetypeId = ArchetypeLoadoutTable.SporeArchetypeId,
                Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI,
                LogicId = logicId,
                VisualId = ArchetypeLoadoutTable.SporeArchetypeId,
                ExcludeFromControl = true,
            };
        }

        private void ResolvePendingBinds()
        {
            if (_pendingBinds.Count == 0)
            {
                return;
            }

            SimSnapshot snapshot = _sim.Snapshot;
            for (int p = _pendingBinds.Count - 1; p >= 0; p--)
            {
                PendingBind pending = _pendingBinds[p];
                SimEntityId found = SimEntityId.None;

                if (snapshot.LogicId.IsCreated && snapshot.EntityId.IsCreated)
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (snapshot.Alive[i] != 0 && snapshot.LogicId[i] == pending.LogicId)
                        {
                            found = snapshot.EntityId[i];
                            break;
                        }
                    }
                }

                if (found.IsValid)
                {
                    _bindings[found] = new UnitBinding(pending.LineageId, pending.TemplateName, pending.Version);
                    _pendingBinds.RemoveAt(p);
                    continue;
                }

                pending.Attempts++;
                if (pending.Attempts >= MaxResolveAttempts)
                {
                    _pendingBinds.RemoveAt(p);
                }
                else
                {
                    _pendingBinds[p] = pending;
                }
            }
        }
    }
}
