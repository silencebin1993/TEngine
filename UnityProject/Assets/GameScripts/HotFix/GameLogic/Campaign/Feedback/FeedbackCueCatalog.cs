using System.Collections.Generic;
using TEngine;

namespace GameLogic.Campaign.Feedback
{
    /// <summary><see cref="FeedbackCueCatalog"/> 的一行：一个反馈时刻的全部表现参数。</summary>
    public sealed class FeedbackCueDef
    {
        public FeedbackCueId Id;

        /// <summary>音效资源名（<c>Assets/GameRes/Raw/Audios/Sfx/&lt;SfxId&gt;.wav</c>，YooAsset 按文件名寻址）。
        /// 空串＝这个时刻刻意不发声。调用方可以用内容目录里的 <c>MechanicalContentDef.SfxId</c> 覆盖。</summary>
        public string SfxId;

        /// <summary>走哪条音量分类：玩法音效 Sound，界面反馈 UISound（分别受设置里“音效/界面音量”控制）。</summary>
        public AudioType Channel = AudioType.Sound;

        /// <summary>0～1，乘在分类音量之上，用来平衡高频音（开火/命中）与关键提示音的相对响度。</summary>
        public float Volume = 1f;

        /// <summary>同一个时刻两次发声的最小间隔（真实秒）。高频战斗音靠它把每秒发声数压在常数级。</summary>
        public float MinIntervalSeconds = 0.3f;

        /// <summary>字幕条类别标签（“接管”“断电”……）。类别由文字表达，不靠颜色（AC-ACC-002）。</summary>
        public string Tag;

        /// <summary>默认正文；调用方给了 detail 时拼成“正文：detail”，正文为空则只显示 detail。</summary>
        public string Caption;

        /// <summary>0～1，这个时刻给屏幕震动注入的震动量（<see cref="ScreenShake"/>）；0＝不震。
        /// 只给真正的大事件，并随距离衰减；设置“屏幕震动”关闭时整体不生效。</summary>
        public float Shake;

        public FeedbackCaptionMode CaptionMode = FeedbackCaptionMode.Always;
        public FeedbackTone Tone = FeedbackTone.Info;
        public float CaptionSeconds = 4f;

        /// <summary>这一行对应的验收/债务出处，供审计核对，不给玩家看。</summary>
        public string RequirementNote;
    }

    /// <summary>ER8-CONTENT-01：反馈时刻的唯一定义表（DEBT-ER8CONTENT01-01 / DEBT-ER7CORE01-01 的落点）。
    ///
    /// 规则：
    /// - 新增时刻＝在 <see cref="FeedbackCueId"/> 追加一个值 + 在这里加一行；自检
    ///   （<c>FeedbackCueSelfCheck</c>）会校验每个 id 都有定义、每个非空 SfxId 都有真实音频文件、
    ///   AC-AUD-001 的十一类都同时有声音和常显字幕。
    /// - 占位音效由 <c>tools/audio/gen_placeholder_sfx.py</c> 合成；正式音效到位后同名覆盖 wav 即可，
    ///   这张表和所有调用点都不用改。</summary>
    public static class FeedbackCueCatalog
    {
        private static readonly FeedbackCueDef[] Defs = BuildDefs();

        /// <summary>按 id 取定义；越界或未定义返回 null（调用方静默跳过，不抛异常打断玩法）。</summary>
        public static FeedbackCueDef Get(FeedbackCueId id)
        {
            int index = (int)id;
            return index > 0 && index < Defs.Length ? Defs[index] : null;
        }

        public static IEnumerable<FeedbackCueDef> All
        {
            get
            {
                for (int i = 1; i < Defs.Length; i++)
                {
                    if (Defs[i] != null)
                    {
                        yield return Defs[i];
                    }
                }
            }
        }

        /// <summary>AC-AUD-001 原文点名的十一类，每类映射到至少一个 cue；自检按这张表逐条断言
        /// “有声音 + 有常显字幕”。键是验收原文里的类别词。</summary>
        public static readonly KeyValuePair<string, FeedbackCueId[]>[] AcAud001Categories =
        {
            new KeyValuePair<string, FeedbackCueId[]>("接管", new[] { FeedbackCueId.Takeover }),
            new KeyValuePair<string, FeedbackCueId[]>("拒绝", new[] { FeedbackCueId.Denied }),
            new KeyValuePair<string, FeedbackCueId[]>("断电", new[] { FeedbackCueId.PowerLost }),
            new KeyValuePair<string, FeedbackCueId[]>("失联", new[] { FeedbackCueId.SignalLost }),
            new KeyValuePair<string, FeedbackCueId[]>("仓满", new[] { FeedbackCueId.StorageFull }),
            new KeyValuePair<string, FeedbackCueId[]>("生产", new[] { FeedbackCueId.ProductionComplete }),
            new KeyValuePair<string, FeedbackCueId[]>("失败", new[] { FeedbackCueId.Failure, FeedbackCueId.ExpeditionWiped, FeedbackCueId.CoreDestroyed }),
            new KeyValuePair<string, FeedbackCueId[]>("解析", new[] { FeedbackCueId.AnalysisComplete }),
            new KeyValuePair<string, FeedbackCueId[]>("反应", new[] { FeedbackCueId.ReactionMarkJump, FeedbackCueId.ReactionMeltOverload }),
            new KeyValuePair<string, FeedbackCueId[]>("Boss 阶段", new[] { FeedbackCueId.BossPhase, FeedbackCueId.BossLockoutWarning, FeedbackCueId.BossLockoutActive, FeedbackCueId.BossDestroyed }),
            new KeyValuePair<string, FeedbackCueId[]>("信标", new[] { FeedbackCueId.BeaconLaunch, FeedbackCueId.Victory }),
        };

        private static FeedbackCueDef[] BuildDefs()
        {
            var defs = new FeedbackCueDef[(int)FeedbackCueId.Max];

            void Add(FeedbackCueDef d)
            {
                defs[(int)d.Id] = d;
            }

            // ── AC-AUD-001 十一类 ─────────────────────────────────────────────
            Add(new FeedbackCueDef { Id = FeedbackCueId.Takeover, SfxId = "sfx_takeover", Tag = "接管", Caption = "已接管", Tone = FeedbackTone.Good, CaptionSeconds = 3f, RequirementNote = "AC-AUD-001 接管" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.Denied, SfxId = "sfx_ui_deny", Channel = AudioType.UISound, MinIntervalSeconds = 0.25f, Tag = "拒绝", Caption = string.Empty, Tone = FeedbackTone.Warning, RequirementNote = "AC-AUD-001 拒绝" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.PowerLost, SfxId = "sfx_power_down", Tag = "断电", Caption = "电力不足", Tone = FeedbackTone.Danger, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 断电" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.PowerRestored, SfxId = "sfx_power_up", Tag = "供电", Caption = "供电恢复", Tone = FeedbackTone.Good, CaptionSeconds = 3f, RequirementNote = "AC-AUD-001 断电（恢复）" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.SignalLost, SfxId = "sfx_signal_jam", Tag = "失联", Caption = "信号中断", Tone = FeedbackTone.Danger, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 失联" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.SignalRestored, SfxId = "sfx_signal_restore", Tag = "信号", Caption = "信号恢复", Tone = FeedbackTone.Good, CaptionSeconds = 3f, RequirementNote = "AC-AUD-001 失联（恢复）" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.StorageFull, SfxId = "sfx_storage_full", Tag = "仓满", Caption = "仓库已满", Tone = FeedbackTone.Warning, CaptionSeconds = 5f, MinIntervalSeconds = 1.5f, RequirementNote = "AC-AUD-001 仓满" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ProductionComplete, SfxId = "sfx_production_complete", Tag = "生产", Caption = "生产完成", Tone = FeedbackTone.Good, RequirementNote = "AC-AUD-001 生产" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.Failure, SfxId = "sfx_failure", Tag = "失败", Caption = string.Empty, Tone = FeedbackTone.Danger, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 失败" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ExpeditionWiped, SfxId = "sfx_failure", Tag = "失败", Caption = "远征队已全部失去行动能力", Tone = FeedbackTone.Danger, CaptionSeconds = 6f, RequirementNote = "AC-AUD-001 失败（远征全灭）" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.CoreDestroyed, Shake = 0.8f, SfxId = "sfx_core_destroyed", Tag = "失败", Caption = "归还核心被摧毁", Tone = FeedbackTone.Danger, CaptionSeconds = 6f, RequirementNote = "AC-AUD-001 失败（核心被毁）" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.AnalysisComplete, SfxId = "sfx_analysis_complete", Tag = "解析", Caption = "解析完成", Tone = FeedbackTone.Good, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 解析" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ReactionMarkJump, SfxId = "sfx_reaction_markjump", MinIntervalSeconds = 0.15f, Tag = "反应", Caption = "标记跳转", Tone = FeedbackTone.Info, CaptionSeconds = 2.5f, RequirementNote = "AC-AUD-001 反应" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ReactionMeltOverload, SfxId = "sfx_reaction_melt", MinIntervalSeconds = 0.15f, Tag = "反应", Caption = "熔穿过载", Tone = FeedbackTone.Info, CaptionSeconds = 2.5f, RequirementNote = "AC-AUD-001 反应" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.BossPhase, Shake = 0.45f, SfxId = "sfx_boss_phase", Tag = "首领", Caption = "主核心阶段变化", Tone = FeedbackTone.Warning, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 Boss 阶段" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.BossLockoutWarning, SfxId = "sfx_alarm_warning", Tag = "警报", Caption = "核心分区即将封锁，2 秒后无法退回外围", Tone = FeedbackTone.Danger, CaptionSeconds = 4f, RequirementNote = "AC-AUD-001 Boss 阶段；DEBT-ER7CORE01-01 可听预警" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.BossLockoutActive, Shake = 0.5f, SfxId = "sfx_lockout_engaged", Tag = "警报", Caption = "核心分区已封锁，无法退回外围", Tone = FeedbackTone.Danger, CaptionSeconds = 5f, RequirementNote = "AC-AUD-001 Boss 阶段；DEBT-ER7CORE01-01" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.BossDestroyed, Shake = 0.8f, SfxId = "sfx_core_destroyed", Tag = "首领", Caption = "主核心已摧毁", Tone = FeedbackTone.Good, CaptionSeconds = 6f, RequirementNote = "AC-AUD-001 Boss 阶段" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.BeaconLaunch, Shake = 0.35f, SfxId = "sfx_beacon_launch", Tag = "信标", Caption = "导航信标启动", Tone = FeedbackTone.Good, CaptionSeconds = 6f, RequirementNote = "AC-AUD-001 信标" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.Victory, SfxId = "sfx_victory", Tag = "信标", Caption = "信标发射完成", Tone = FeedbackTone.Good, CaptionSeconds = 6f, RequirementNote = "AC-AUD-001 信标" });

            // ── 战斗与远征 ─────────────────────────────────────────────────
            // 开火/命中本身有弹体与伤害数字等价视觉，不出字幕；对“看不见也该知道”的声音（遭到攻击、
            // 装甲削减、蓄力、击毁）出字幕，并且只在“字幕”开启时出。
            Add(new FeedbackCueDef { Id = FeedbackCueId.WeaponFire, SfxId = "sfx_weapon_fire", Volume = 0.7f, MinIntervalSeconds = 0.08f, CaptionMode = FeedbackCaptionMode.None, RequirementNote = "ComponentCatalog.SfxId 消费点" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.CannonCharge, SfxId = "sfx_cannon_charge", Volume = 0.8f, MinIntervalSeconds = 0.2f, Tag = "重炮", Caption = "蓄力", CaptionMode = FeedbackCaptionMode.SubtitlesOnly, CaptionSeconds = 2f, RequirementNote = "ER6-REACT-02 瞄准阶段" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.CannonFire, Shake = 0.25f, SfxId = "sfx_cannon_fire", MinIntervalSeconds = 0.1f, CaptionMode = FeedbackCaptionMode.None, RequirementNote = "ComponentCatalog comp_cannon" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.EnemyHit, SfxId = "sfx_hit", Volume = 0.6f, MinIntervalSeconds = 0.06f, CaptionMode = FeedbackCaptionMode.None });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ArmorHit, SfxId = "sfx_hit_armor", Volume = 0.7f, MinIntervalSeconds = 0.1f, Tag = "命中", Caption = "装甲正面，伤害被削减", CaptionMode = FeedbackCaptionMode.SubtitlesOnly, CaptionSeconds = 2f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.EnemyDestroyed, Shake = 0.15f, SfxId = "sfx_enemy_destroyed", MinIntervalSeconds = 0.12f, Tag = "击毁", Caption = string.Empty, CaptionMode = FeedbackCaptionMode.SubtitlesOnly, CaptionSeconds = 2.5f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.EnemyAttack, SfxId = "sfx_enemy_fire", Volume = 0.7f, MinIntervalSeconds = 0.15f, CaptionMode = FeedbackCaptionMode.None, RequirementNote = "EnemyCatalog.SfxId 消费点；字幕由紧随其后的“受损”给出，避免一次攻击两条字幕" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.MachineDamaged, SfxId = "sfx_machine_damaged", Volume = 0.8f, MinIntervalSeconds = 0.4f, Tag = "受损", Caption = string.Empty, CaptionMode = FeedbackCaptionMode.SubtitlesOnly, Tone = FeedbackTone.Warning, CaptionSeconds = 2f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.MachineDestroyed, Shake = 0.3f, SfxId = "sfx_machine_destroyed", Tag = "损失", Caption = string.Empty, Tone = FeedbackTone.Danger, CaptionSeconds = 5f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.WeaponOverheat, SfxId = "sfx_weapon_overheat", MinIntervalSeconds = 0.5f, Tag = "过热", Caption = "重炮过热停火", Tone = FeedbackTone.Warning, CaptionSeconds = 3f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.Pickup, SfxId = "sfx_pickup", Tag = "装载", Caption = string.Empty, Tone = FeedbackTone.Good, CaptionSeconds = 3f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.ExpeditionDepart, SfxId = "sfx_expedition_depart", Tag = "出征", Caption = "远征队出发", Tone = FeedbackTone.Info, CaptionSeconds = 4f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.Evacuate, SfxId = "sfx_evacuate", Tag = "撤离", Caption = "远征队已返回归还谷地", Tone = FeedbackTone.Good, CaptionSeconds = 4f });

            // ── 家园与通用 ─────────────────────────────────────────────────
            Add(new FeedbackCueDef { Id = FeedbackCueId.BuildComplete, SfxId = "sfx_build_complete", Tag = "建造", Caption = "完工", Tone = FeedbackTone.Good, RequirementNote = "BuildingCatalog.SfxId 消费点" });
            Add(new FeedbackCueDef { Id = FeedbackCueId.SaveComplete, SfxId = "sfx_save_ok", Channel = AudioType.UISound, Tag = "存档", Caption = "已保存", Tone = FeedbackTone.Info, CaptionSeconds = 2.5f, MinIntervalSeconds = 1f });
            Add(new FeedbackCueDef { Id = FeedbackCueId.CommandAck, SfxId = "sfx_command_ack", Channel = AudioType.UISound, Volume = 0.7f, MinIntervalSeconds = 0.15f, CaptionMode = FeedbackCaptionMode.None, RequirementNote = "ChassisCatalog.SfxId 消费点（命令确认音按底盘区分）" });
            // 按钮本身的按下态就是等价视觉反馈，不出字幕。
            Add(new FeedbackCueDef { Id = FeedbackCueId.UiClick, SfxId = "sfx_ui_click", Channel = AudioType.UISound, Volume = 0.8f, MinIntervalSeconds = 0.04f, CaptionMode = FeedbackCaptionMode.None });

            return defs;
        }
    }
}
