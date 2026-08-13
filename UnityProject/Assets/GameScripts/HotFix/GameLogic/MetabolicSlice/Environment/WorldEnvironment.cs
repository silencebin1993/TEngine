using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.Environment
{
    /// <summary>
    /// 战场格子的稀疏容器（§1）。CellId 复用 WorldState/HitEvent.TargetId 已有的 string id 惯例，
    /// 不建真实坐标/寻路网格。Tags = 地形常驻 ∪ 存活残留，实时同步进 State 供 ComposeEngine 反应解析复用
    /// （不新建第二套反应解析器，走 MetabolicSliceRunner 已在用的 engine.ApplyPipeline(evt, rules, world.State) 路径）。
    /// </summary>
    public sealed class WorldEnvironment
    {
        private readonly Dictionary<string, TerrainCell> _cells = new Dictionary<string, TerrainCell>();

        public WorldState State { get; } = new WorldState();

        public TerrainCell GetOrCreateCell(string cellId)
        {
            if (!_cells.TryGetValue(cellId, out var cell))
            {
                cell = new TerrainCell(cellId);
                _cells[cellId] = cell;
            }
            return cell;
        }

        public HashSet<string> GetTags(string cellId) => State.GetTags(cellId);

        public void AddTerrainTag(string cellId, string tag)
        {
            GetOrCreateCell(cellId).Tags.Add(tag);
            SyncCellTags(cellId);
        }

        public void RemoveTerrainTag(string cellId, string tag)
        {
            GetOrCreateCell(cellId).Tags.Remove(tag);
            SyncCellTags(cellId);
        }

        public void AddResidue(string cellId, string tag, float amount, int ttlTicks, string sourceId = null)
        {
            GetOrCreateCell(cellId).Residues.Add(new ResidueStack(tag, amount, ttlTicks, sourceId));
            SyncCellTags(cellId);
        }

        /// <summary>残留寿命衰减；过期整格重算 tag，避免误删同名共存 tag（如两条残留同用一个 tag）。</summary>
        public void Tick(int dt)
        {
            foreach (var cell in _cells.Values)
            {
                if (cell.Residues.Count == 0) continue;
                bool changed = false;
                for (int i = cell.Residues.Count - 1; i >= 0; i--)
                {
                    var residue = cell.Residues[i];
                    residue.TtlTicks -= dt;
                    if (residue.IsExpired)
                    {
                        cell.Residues.RemoveAt(i);
                        changed = true;
                    }
                }
                if (changed) SyncCellTags(cell.CellId);
            }
        }

        /// <summary>
        /// GDD §1.5 FireWithWorld 的落地形式：设 TargetId → ApplyPipeline → 读 Payload["LeaveResidue"] 逐条落地 → 回传最终 HitEvent。
        /// ComposeEngine 核心保持被动，不直接写格子；残留回写协议由本方法承接。
        /// </summary>
        public HitEvent ResolveHit(Engine engine, HitEvent evt, RuleVector rules, string cellId)
        {
            evt.TargetId = cellId;
            var final = engine.ApplyPipeline(evt, rules, State);

            if (final.Payload.TryGetValue("LeaveResidue", out var raw) && raw is List<ResidueDeposit> deposits)
            {
                foreach (var deposit in deposits)
                {
                    if (deposit.Trigger != ResidueTrigger.OnHit) continue;
                    AddResidue(cellId, deposit.Tag, deposit.Amount, deposit.Ttl);
                }
            }
            return final;
        }

        private void SyncCellTags(string cellId)
        {
            var cell = GetOrCreateCell(cellId);
            var tags = State.GetTags(cellId);
            tags.Clear();
            tags.UnionWith(cell.Tags);
            foreach (var residue in cell.Residues)
            {
                if (!residue.IsExpired) tags.Add(residue.Tag);
            }
        }
    }
}
