using UnityEngine;
using UnityEngine.UI;
using TEngine;
using GameLogic.Ability;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;

namespace GameLogic
{
    /// <summary>
    /// 战斗常驻 HUD（Spec §12.1）。数据来源与 <see cref="Battle.CellDebugHud"/> 的
    /// DrawHud 完全一致（同一份 CellStageFlow 公开属性），只是把渲染层从 IMGUI 换成
    /// UIWindow + prefab（Preflight battle-ui/story-001 决策 S1/T1/B1/I1）。
    /// </summary>
    [Window(UILayer.UI,location:"BattleMainUI")]
    class BattleMainUI : UIWindow
    {
        #region 脚本工具生成的代码
        private Transform _tf_Hud;
        private Text _text_Phase;
        private Text _text_RunTime;
        private Text _text_HpVolume;
        private Text _text_LevelEvo;
        private Text _text_Resources;
        private Text _text_Pollution;
        private Text _text_CardsCombo;
        private Text _text_EnemyPressure;
        private Text _text_EcoEvent;
        private HorizontalLayoutGroup _hlay_Skills;
        private Text _text_Skill0;
        private Text _text_Skill1;
        private Text _text_Skill2;
        private Text _text_Skill3;
        private Text _text_Skill4;
        protected override void ScriptGenerator()
        {
            _tf_Hud = FindChild("m_tf_Hud");
            _text_Phase = FindChildComponent<Text>("m_tf_Hud/m_text_Phase");
            _text_RunTime = FindChildComponent<Text>("m_tf_Hud/m_text_RunTime");
            _text_HpVolume = FindChildComponent<Text>("m_tf_Hud/m_text_HpVolume");
            _text_LevelEvo = FindChildComponent<Text>("m_tf_Hud/m_text_LevelEvo");
            _text_Resources = FindChildComponent<Text>("m_tf_Hud/m_text_Resources");
            _text_Pollution = FindChildComponent<Text>("m_tf_Hud/m_text_Pollution");
            _text_CardsCombo = FindChildComponent<Text>("m_tf_Hud/m_text_CardsCombo");
            _text_EnemyPressure = FindChildComponent<Text>("m_tf_Hud/m_text_EnemyPressure");
            _text_EcoEvent = FindChildComponent<Text>("m_tf_Hud/m_text_EcoEvent");
            _hlay_Skills = FindChildComponent<HorizontalLayoutGroup>("m_tf_Hud/m_hlay_Skills");
            _text_Skill0 = FindChildComponent<Text>("m_tf_Hud/m_hlay_Skills/m_text_Skill0");
            _text_Skill1 = FindChildComponent<Text>("m_tf_Hud/m_hlay_Skills/m_text_Skill1");
            _text_Skill2 = FindChildComponent<Text>("m_tf_Hud/m_hlay_Skills/m_text_Skill2");
            _text_Skill3 = FindChildComponent<Text>("m_tf_Hud/m_hlay_Skills/m_text_Skill3");
            _text_Skill4 = FindChildComponent<Text>("m_tf_Hud/m_hlay_Skills/m_text_Skill4");
        }
        #endregion

        #region 事件
        #endregion

        private static readonly string[] SkillKeys = { "空格", "Q", "E", "R", "F" };

        private Text[] _skillTexts;

        protected override void OnCreate()
        {
            _skillTexts = new[] { _text_Skill0, _text_Skill1, _text_Skill2, _text_Skill3, _text_Skill4 };
        }

        /// <summary>
        /// 按 IsRunning 自控显隐（Preflight V1：不改 GameApp.cs 的启动展示逻辑），
        /// 进局中逐帧轮询 CellStageFlow 快照刷新文本（Preflight B1，与 CellDebugHud 同一模式）。
        /// battle-ui-toolkit story-001 验收通过后，本 UGUI HUD 降级为按 U 键切回来的对照面板
        /// （<see cref="BattleHudToolkit.NewHudActive"/> 为 true 时新 UI Toolkit HUD 是默认显示项）。
        /// </summary>
        protected override void OnUpdate()
        {
            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning && !BattleHudToolkit.NewHudActive;
            _tf_Hud.gameObject.SetActive(running);
            if (!running)
            {
                return;
            }

            RefreshHud(cell);
        }

        private void RefreshHud(CellStageFlow cell)
        {
            StatSheet st = cell.Stats;
            PhaseTimeline tl = cell.Timeline;

            if (tl?.Current != null)
            {
                _text_Phase.text = $"{tl.Current.Name}　{tl.CurrentIndex + 1}/6　{tl.PhaseProgress:P0}";
                int rm = (int)(tl.RunElapsed / 60f);
                int rs = (int)(tl.RunElapsed % 60f);
                _text_RunTime.text = $"本局 {rm:00}:{rs:00}";
            }

            float maxHp = st.Get(StatId.MaxHealth);
            float hp = cell.Sim.PlayerHealth;
            _text_HpVolume.text = $"生命 {hp:F0}/{maxHp:F0}　体积 {st.Get(StatId.Volume):F2}";

            ProgressionModule prog = cell.Progression;
            _text_LevelEvo.text =
                $"等级 {prog.Level}　进化能 {cell.Wallet.EvoEnergy:F0}/{prog.CurrentThreshold:F0}　({prog.Progress:P0})";

            _text_Resources.text = $"营养质 {cell.Wallet.Nutrient:F0}　突变质 {cell.Wallet.Mutagen:F0}";

            // 污染度只在有污染卡时显示（Spec §12.1，与 CellDebugHud.DrawHud 一致）
            bool showPollution = cell.Wallet.Pollution > 0f;
            _text_Pollution.gameObject.SetActive(showPollution);
            if (showPollution)
            {
                _text_Pollution.text = $"污染度 {cell.Wallet.Pollution:F0}/{st.Get(StatId.PollutionCap):F0}";
            }

            _text_CardsCombo.text = $"卡牌 {cell.Deck.TotalCards}　连吃 {cell.Devour.Combo}";

            _text_EnemyPressure.text =
                $"敌人 {cell.Director.LiveHostiles}　压力 {cell.Director.CurrentPressure:F0}/{cell.Director.Budget:F0}";

            _text_EcoEvent.text = cell.Events.Active != null
                ? $"生态事件：{cell.Events.Active.Name}"
                : $"下次事件 {cell.Events.NextEventCountdown:F0}s";

            RefreshSkillSlots(cell);
        }

        /// <summary>技能槽上限 5（AbilitySystem.Slots），未装填的槽位隐藏对应节点。</summary>
        private void RefreshSkillSlots(CellStageFlow cell)
        {
            var slots = cell.Abilities.Slots;
            for (int i = 0; i < _skillTexts.Length; i++)
            {
                Text t = _skillTexts[i];
                if (i >= slots.Count)
                {
                    t.gameObject.SetActive(false);
                    continue;
                }

                t.gameObject.SetActive(true);
                AbilityRuntime rt = slots[i];
                string key = i < SkillKeys.Length ? SkillKeys[i] : "?";
                string state = rt.Ready ? $"就绪 x{rt.ChargesLeft}" : $"{rt.CooldownLeft:F1}s";
                t.text = $"[{key}] {rt.Spec.Name}\n{state}";
            }
        }
    }
}
