using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG6-DEF-05（承接 DEBT-FG3LOG05-12 / DEBT-FG6DEF03-02，FGR-DEF-015 列举的“管线”）：管线件的耐久——与传送带（FGR-LOG-027）同一做法。
    /// 满耐久的不记；掉了耐久的格进存档（<see cref="PipeFluidState.Damage"/>）。到 0 摧毁：件从内核移除（储罐存量 / 阀门缓冲随之流失），原位置留下保留设置的虚影
    /// （种类、等级、朝向、储罐模式与优先级、阀门开关、流体），排给自动重建规则（“传送带与物流节点”范围）。维修无人机可以修（按修好的比例收维修件）。
    /// 破损泄漏（液洼与标签反应，FGR-LOG-046）在 FG6-LOG-10。伤害来源：攻城溅射（SiegeService），天气在 FG7。
    /// </summary>
    public static partial class PipeNetworkService
    {
        private static readonly Dictionary<GridCell, int> DamageLost = new Dictionary<GridCell, int>();
        private static readonly Dictionary<GridCell, long> LastHit = new Dictionary<GridCell, long>();

        public static int DestroyedPieces { get; private set; }
        public static long LastDestroyedLostMl { get; private set; }

        /// <summary>一格管线件的最大耐久（储罐 logistics.pipe.hp_tank；其余按等级 logistics.pipe.hp_t1 / t2）。</summary>
        public static int MaxHp(PipePieceKind kind, int tier) =>
            Math.Max(1, GridContent.TuningInt(kind == PipePieceKind.Tank ? "logistics.pipe.hp_tank" : tier <= 0 ? "logistics.pipe.hp_t1" : "logistics.pipe.hp_t2"));

        /// <summary>修满一格管线要的维修件（维修无人机，与传送带同口径）。</summary>
        public static float RepairKitsPerPiece => Math.Max(0f, GridContent.Tuning("logistics.pipe.repair_kits"));

        /// <summary>这一格现在的耐久（没有管线件时 -1）。O(1)。</summary>
        public static int HpOf(GridCell cell)
        {
            if (!IsRunning || !_kernel.TryGetKind(cell.X, cell.Y, out PipePieceKind kind, out int tier))
            {
                return -1;
            }
            return Math.Max(0, MaxHp(kind, tier) - (DamageLost.TryGetValue(cell, out int lost) ? lost : 0));
        }

        public static int DamageOf(GridCell cell) => DamageLost.TryGetValue(cell, out int l) ? l : 0;

        public static long LastHitOf(GridCell cell) => LastHit.TryGetValue(cell, out long t) ? t : 0;

        /// <summary>全部受损的管线格（按存档里的顺序，确定性）。维修无人机找目标用。</summary>
        public static void DamagedCells(CampaignState state, List<GridCell> into)
        {
            into.Clear();
            foreach (PipeDamageRecord r in state?.Pipes?.Damage ?? Array.Empty<PipeDamageRecord>())
            {
                if (r != null && r.Lost > 0)
                {
                    into.Add(new GridCell(r.X, r.Y));
                }
            }
        }

        /// <summary>
        /// 对一格管线件造成伤害：扣耐久（记“最近挨打”）；到 0 摧毁并留下保留设置的虚影（排给自动重建规则）、发通知。返回是否摧毁了。
        /// 拆掉会让两种流体接在一起的地下管线口（FGR-LOG-047）摧毁不了：耐久停在 1，原因记在 <paramref name="result"/>。
        /// </summary>
        public static bool TryDamage(CampaignState state, GridCell cell, int amount, out PipeOpResult result)
        {
            if (!CanEdit(state, out result))
            {
                return false;
            }
            if (amount <= 0)
            {
                result = PipeOpResult.Kernel(PipeResult.InvalidArgument);
                return false;
            }
            if (!_kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo info))
            {
                result = PipeOpResult.Kernel(PipeResult.NotFound);
                return false;
            }
            LastHit[cell] = GameClock.Ticks;
            int max = MaxHp(info.Kind, info.Tier);
            int lost = (DamageLost.TryGetValue(cell, out int l) ? l : 0) + amount;
            result = PipeOpResult.Success;
            if (lost < max)
            {
                SetDamage(state, cell, lost);
                return false;
            }
            return DestroyPiece(state, cell, info, max, out result);
        }

        private static bool DestroyPiece(CampaignState state, GridCell cell, in PipeCellInfo info, int max, out PipeOpResult result)
        {
            int settings = info.Kind == PipePieceKind.Tank ? Grid.PlanSettings.PackTank(info.TankMode, info.Priority)
                : info.Kind == PipePieceKind.Valve ? Grid.PlanSettings.PackValve(info.ValveOpen) : 0;
            PipePieceKind kind = info.Kind;
            int tier = info.Tier;
            int dir = info.Dir;
            int fluid = info.Fluid;
            result = TryRemove(state, cell, out long lostMl);
            if (!result.Ok)
            {
                SetDamage(state, cell, max - 1); // 拆不掉（会把两种流体接在一起）：留 1 点耐久，不凭空连通两网
                return false;
            }
            SetDamage(state, cell, 0);
            string ghostId = Regions.HomeValleyConstruction.AddDestroyedPipeGhost(state, cell, kind, tier, dir, settings, fluid);
            Economy.StandingRuleService.OnBeltGhost(state, ghostId);
            DestroyedPieces++;
            LastDestroyedLostMl = lostMl;
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstDestroyed);
            NotificationCenter.Post("failure", GameText.Format("pipe.destroyed.notify", cell.X, cell.Y, Liters(lostMl)), new Vector3(cell.X, 0f, cell.Y));
            return true;
        }

        /// <summary>给一格受损的管线件恢复 <paramref name="amount"/> 点耐久（不超过满值）。返回实际恢复的点数。</summary>
        public static int TryRepair(CampaignState state, GridCell cell, int amount)
        {
            if (state?.Pipes == null || amount <= 0 || !IsRunning || !_kernel.TryGetKind(cell.X, cell.Y, out _, out _))
            {
                return 0;
            }
            int lost = DamageLost.TryGetValue(cell, out int l) ? l : 0;
            if (lost <= 0)
            {
                return 0;
            }
            int fix = Math.Min(lost, amount);
            SetDamage(state, cell, lost - fix);
            return fix;
        }

        private static void RestoreDamage(CampaignState state)
        {
            DamageLost.Clear();
            LastHit.Clear();
            PipeDamageRecord[] recs = state.Pipes?.Damage ?? Array.Empty<PipeDamageRecord>();
            var keep = new List<PipeDamageRecord>(recs.Length);
            foreach (PipeDamageRecord r in recs)
            {
                if (r != null && r.Lost > 0 && IsRunning && _kernel.TryGetKind(r.X, r.Y, out _, out _))
                {
                    var c = new GridCell(r.X, r.Y);
                    DamageLost[c] = r.Lost;
                    if (r.LastHitTick > 0)
                    {
                        LastHit[c] = r.LastHitTick;
                    }
                    keep.Add(r);
                }
            }
            if (state.Pipes != null && keep.Count != recs.Length)
            {
                state.Pipes.Damage = keep.ToArray();
            }
        }

        private static void SetDamage(CampaignState state, GridCell cell, int lost)
        {
            if (lost > 0)
            {
                DamageLost[cell] = lost;
            }
            else
            {
                DamageLost.Remove(cell);
                LastHit.Remove(cell);
            }
            long hit = LastHit.TryGetValue(cell, out long lh) ? lh : 0;
            PipeFluidState p = state.Pipes ??= new PipeFluidState();
            var list = new List<PipeDamageRecord>((p.Damage?.Length ?? 0) + 1);
            bool found = false;
            foreach (PipeDamageRecord r in p.Damage ?? Array.Empty<PipeDamageRecord>())
            {
                if (r == null)
                {
                    continue;
                }
                if (r.X == cell.X && r.Y == cell.Y)
                {
                    found = true;
                    if (lost > 0)
                    {
                        r.Lost = lost;
                        r.LastHitTick = hit;
                        list.Add(r);
                    }
                    continue;
                }
                list.Add(r);
            }
            if (!found && lost > 0)
            {
                list.Add(new PipeDamageRecord { X = cell.X, Y = cell.Y, Lost = lost, LastHitTick = hit });
            }
            p.Damage = list.ToArray();
        }

        /// <summary>悬停读数的“耐久”一行（受损时才写）。</summary>
        public static string HpLine(GridCell cell)
        {
            int lost = DamageOf(cell);
            int hp = HpOf(cell);
            return lost <= 0 || hp < 0 ? string.Empty : GameText.Format("pipe.hover.hp", hp.ToString(CultureInfo.InvariantCulture), (hp + lost).ToString(CultureInfo.InvariantCulture));
        }
    }
}
