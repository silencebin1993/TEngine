namespace GameLogic.Campaign
{
    /// <summary>ER4-MCH-01 STORY-EXECUTION-CARDS.md 第1条："生产、第一次工作、远征、接管（0.2：首次与信号同行）、重伤、
    /// 精英击破、Boss参与、返航分别只记一次'首次'"——<see cref="MachineRecord.ExperienceFlags"/>
    /// 的封闭八项取值表。字符串常量（不是 enum）是刻意的：与 <see cref="MachineRecord.InjuryFlags"/>
    /// 同一既有存储形状（<c>string[]</c>），JsonUtility 原生支持，不需要额外的枚举↔字符串转换层。
    ///
    /// 写入唯一入口是 <see cref="MachineRegistry.TryMarkExperience"/>（幂等：已有则不重复追加）。
    /// 八项里目前只有三项在归还谷地场景有真实触发源（见各常量注释），其余五项依赖尚未实现的
    /// 远征/区域战斗/Boss 系统——按 `.claude/rules/projecta-spec-completeness.md` 第5条登记
    /// DEBT-ER4MCH01-01，不臆造假触发。</summary>
    public static class MachineExperienceFlags
    {
        /// <summary>首次在装配站生产完工（<see cref="Regions.HomeValleyFactory"/> Produce 队列完工，
        /// 不含 Retrofit——回厂改造不是"生产一台新机"）。归还谷地开局自带的 ERC-001/002 从未走生产
        /// 队列，天然不会获得这个标记，正确反映"它们不是被生产出来的"。真实触发源：
        /// <see cref="Regions.HomeValleyFactory.SpawnProducedMachine"/>。</summary>
        public const string Produced = "produced";

        /// <summary>首次完成任意一类工作单（Haul/Build/Repair/Salvage/Recharge，
        /// <see cref="Regions.WorkOrderKind"/>）。真实触发源：
        /// <see cref="Regions.HomeValleyWorkOrders"/> 各 CompleteXxx 完成分支。</summary>
        public const string FirstJob = "first_job";

        /// <summary>首次被玩家直控接管（WASD 亲自开）。真实触发源：
        /// <see cref="Regions.HomeValleyController"/> 的 EnsureDirectTarget/CycleControlTarget。</summary>
        public const string Controlled = "controlled";

        /// <summary>首次参与远征（依赖 ER5-REGION-01 远征系统，当前无真实触发源）。</summary>
        public const string Expedition = "expedition";

        /// <summary>首次重伤（依赖机器在区域战斗中被扣血到阈值以下，当前归还谷地无真实机器受伤
        /// 触发源——低威胁残骸靶是被机器攻击的对象，不是反过来伤机器）。</summary>
        public const string SeverelyInjured = "severely_injured";

        /// <summary>首次击破精英敌人（依赖 ER6/ER7 敌人内容与区域战斗，当前无真实触发源）。</summary>
        public const string EliteKill = "elite_kill";

        /// <summary>首次参与 Boss 战（依赖 ER7 Boss 内容，当前无真实触发源）。</summary>
        public const string BossParticipation = "boss_participation";

        /// <summary>首次远征后成功返航（依赖 ER5-REGION-01 远征系统，当前无真实触发源）。</summary>
        public const string Returned = "returned";

        /// <summary>全部封闭八项，供 UI 按固定顺序展示/自检断言"不超过八项"。</summary>
        public static readonly string[] All =
        {
            Produced, FirstJob, Controlled, Expedition, SeverelyInjured, EliteKill, BossParticipation, Returned,
        };

        /// <summary>玩家可见的经历名（FG1-HUD-01 / FG-GAP-011：走文本键 machine.exp.*；0.2 的“接管”改叫“首次与信号同行”）。</summary>
        public static string DisplayName(string flagId)
        {
            switch (flagId)
            {
                case Produced: return GameLogic.Localization.GameText.Get("machine.exp.produced");
                case FirstJob: return GameLogic.Localization.GameText.Get("machine.exp.first_job");
                case Controlled: return GameLogic.Localization.GameText.Get("machine.exp.controlled");
                case Expedition: return GameLogic.Localization.GameText.Get("machine.exp.expedition");
                case SeverelyInjured: return GameLogic.Localization.GameText.Get("machine.exp.severely_injured");
                case EliteKill: return GameLogic.Localization.GameText.Get("machine.exp.elite_kill");
                case BossParticipation: return GameLogic.Localization.GameText.Get("machine.exp.boss_participation");
                case Returned: return GameLogic.Localization.GameText.Get("machine.exp.returned");
                default: return flagId;
            }
        }

        /// <summary>一台机器的经历名列表（按记录顺序，“、”/“, ”连接；没有时“无记录经历”）。</summary>
        public static string Join(string[] flags)
        {
            if (flags == null || flags.Length == 0)
            {
                return GameLogic.Localization.GameText.Get("machine.exp.none");
            }
            return string.Join(GameLogic.Localization.GameText.Get("machine.exp.sep"), System.Array.ConvertAll(flags, DisplayName));
        }
    }

    /// <summary>
    /// FG1-HUD-01（FG01 FGR-SIG-082）：机器经历“与信号同行”的次数与累计时长。
    /// - 次数 <see cref="MachineRecord.SignalUplinkCount"/>：信号每进入这台机器一次 +1（接入完成、Tab 切换、跳转、阵亡回弹到它）；读档恢复不算。
    /// - 时长：已结束的段累计在 <see cref="MachineRecord.SignalUplinkTicks"/>（统一时钟步，60 步 = 1 游戏秒）；正在进行的一段 =
    ///   现在的步数 − <see cref="SignalCoreState.UplinkSinceTick"/>。按统一时钟计：暂停不走，倍速按游戏时间（接入中统一时钟锁 1x），与是否被观察无关。
    /// 唯一写入口是 <c>SignalUplinkService.SetUplink</c>（经 <see cref="OpenSegment"/> / <see cref="CloseSegment"/>）。开销 O(1)。
    /// </summary>
    public static class MachineSignalExperience
    {
        internal static void OpenSegment(CampaignState s, int logicId, long nowTick)
        {
            if (s?.SignalCore != null)
            {
                s.SignalCore.UplinkSinceTick = nowTick;
            }
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null)
            {
                rec.SignalUplinkCount++;
            }
        }

        internal static void CloseSegment(CampaignState s, int logicId, long nowTick)
        {
            if (s?.SignalCore == null || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return;
            }
            long since = s.SignalCore.UplinkSinceTick;
            if (since > 0 && nowTick > since)
            {
                rec.SignalUplinkTicks += nowTick - since;
            }
        }

        /// <summary>这台机器与信号同行的累计步数（含正在进行的一段）。</summary>
        public static long TotalTicks(CampaignState s, MachineRecord rec)
        {
            if (rec == null)
            {
                return 0;
            }
            long total = rec.SignalUplinkTicks;
            SignalCoreState core = s?.SignalCore;
            if (core != null && core.UplinkMachineLogicId == rec.LogicId && core.UplinkSinceTick > 0)
            {
                total += System.Math.Max(0L, GameLogic.Core.GameClock.Ticks - core.UplinkSinceTick);
            }
            return total;
        }

        public static double TotalSeconds(CampaignState s, MachineRecord rec) =>
            TotalTicks(s, rec) / (double)System.Math.Max(1, GameLogic.Core.GameClock.StepHz);

        /// <summary>“与信号同行 N 次，累计 H:MM:SS” / “尚未与信号同行”（机器详情、结算、HUD 共用）。</summary>
        public static string Describe(CampaignState s, MachineRecord rec, string key = "machine.exp.signal")
        {
            if (rec == null || (rec.SignalUplinkCount <= 0 && TotalTicks(s, rec) <= 0))
            {
                return GameLogic.Localization.GameText.Get("machine.exp.signal_none");
            }
            return GameLogic.Localization.GameText.Format(key, rec.SignalUplinkCount, FormatDuration(TotalSeconds(s, rec)));
        }

        /// <summary>时长显示：不到 1 小时 “M:SS”，否则 “H:MM:SS”（游戏时间）。</summary>
        public static string FormatDuration(double seconds)
        {
            long t = (long)System.Math.Floor(System.Math.Max(0, seconds));
            long h = t / 3600;
            long m = (t / 60) % 60;
            long sec = t % 60;
            return h > 0
                ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + m.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":" + sec.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
                : m.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + sec.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// FG1-HUD-01 修复轮（审查 P2“英文模式下伤势仍是中文”）：<see cref="MachineRecord.InjuryFlags"/> 的存档格式与显示。
    /// 新写入的伤势存成“文本键|参数1|参数2…”（例：<c>machine.injury.combat|120|400</c>），显示时按当前语言走文本键；
    /// 旧档里已经写成中文字面串的伤势不是文本键，原样显示（不改存档）。接入 HUD、机器详情共用这一处，O(伤势条数)。
    /// </summary>
    public static class MachineInjury
    {
        /// <summary>战斗损伤：撤离时存活但没满血（ExpeditionReturnService）。</summary>
        public const string CombatKey = "machine.injury.combat";
        private const char Sep = '|';

        public static string Combat(float health, float maxHealth) =>
            CombatKey + Sep + health.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
            + Sep + maxHealth.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>一条伤势的玩家可见文字（当前语言）。</summary>
        public static string Describe(string flag)
        {
            if (string.IsNullOrEmpty(flag))
            {
                return string.Empty;
            }
            string[] parts = flag.Split(Sep);
            if (!parts[0].StartsWith("machine.injury.", System.StringComparison.Ordinal) || !GameLogic.Localization.GameText.Has(parts[0]))
            {
                return flag; // 旧档中文字面串 / 未登记的键：原样显示，不丢信息。
            }
            object[] args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                args[i - 1] = parts[i];
            }
            return GameLogic.Localization.GameText.Format(parts[0], args);
        }

        /// <summary>全部伤势用当前语言的分隔符连起来；没有伤势返回空串。</summary>
        public static string DescribeAll(string[] flags)
        {
            if (flags == null || flags.Length == 0)
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder();
            string sep = GameLogic.Localization.GameText.Get("machine.exp.sep");
            for (int i = 0; i < flags.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }
                sb.Append(Describe(flags[i]));
            }
            return sb.ToString();
        }
    }
}
