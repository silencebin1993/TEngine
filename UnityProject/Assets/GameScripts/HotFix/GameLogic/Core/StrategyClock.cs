using System;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// ER2-INPUT-01 AC-UI-005：战略速度 0.5x/1x/2x。全局单例——归还谷地与细胞阶段互斥运行
    /// （不会同时活跃），共用同一个倍率不会有两边打架的问题，也让 HUD 只需要一个速度选择器。
    ///
    /// 只管速度倍率，不管暂停：暂停已经有各自战场既定的机制（<c>CellStageFlow._paused</c>/
    /// <c>HomeValleyController</c> 的镜像实现），且都绑定 Space（Strategy 域），二者天然独立——
    /// "Space 暂停恢复此前速度"因此是免费成立的：暂停不touch <see cref="SpeedMultiplier"/>，
    /// 恢复时不管过去多久，倍率还是暂停前那个值。
    /// </summary>
    public static class StrategyClock
    {
        public static readonly float[] AllowedMultipliers = { 0.5f, 1f, 2f };

        public static float SpeedMultiplier { get; private set; } = 1f;

        public static void SetSpeed(float multiplier)
        {
            float nearest = AllowedMultipliers[0];
            float bestDelta = Mathf.Abs(multiplier - nearest);
            foreach (float candidate in AllowedMultipliers)
            {
                float delta = Mathf.Abs(multiplier - candidate);
                if (delta < bestDelta)
                {
                    nearest = candidate;
                    bestDelta = delta;
                }
            }
            SpeedMultiplier = nearest;
        }

        /// <summary>下一档（1x→2x→...循环）。HUD 按钮可以直接调 <see cref="SetSpeed"/> 指定档位，
        /// 本方法给"单键循环切换"这类更简的入口用。</summary>
        public static void CycleSpeed()
        {
            int index = Array.IndexOf(AllowedMultipliers, SpeedMultiplier);
            int next = (index < 0 ? 0 : index + 1) % AllowedMultipliers.Length;
            SpeedMultiplier = AllowedMultipliers[next];
        }

        /// <summary><paramref name="directLocked"/>=true（当前是直控视角）时无视速度倍率、锁 1x。
        /// 战场自己的暂停语义（_paused）不在这里管——调用方在暂停时通常根本不会走到这一步
        /// （细胞阶段暂停早退在这句之后；归还谷地暂停直接跳过对应 Tick），本方法只做倍率缩放。</summary>
        public static float GetScaledDt(float rawDt, bool directLocked)
        {
            return directLocked ? rawDt : rawDt * SpeedMultiplier;
        }

        /// <summary>新战役/回主菜单时复位，避免上一局选的倍率粘到下一局（同 InputRouter.Reset 的纪律）。</summary>
        public static void Reset()
        {
            SpeedMultiplier = 1f;
        }
    }
}
