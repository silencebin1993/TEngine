using System;
using UnityEngine;
using UnityEngine.UI;
using GameLogic.Campaign;
using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>ER1-SAVE-01 骨架 + ER2-BOOT-01 战役入口收尾：《地球归还》正式主菜单——
    /// 新建/继续/读取/设置/退出五个入口 + 存档卡四态（空槽/正常/坏档/版本不兼容）。
    /// 走 TEngine UIWindow 规范；完整机械美术主题由 ER2-THEME-01 接手打磨，本类不做视觉终稿。
    ///
    /// 冷启动第一屏由 <c>GameApp.StartGameLogic</c> 直接 <c>ShowUIAsync&lt;MainMenuUI&gt;()</c> 打开
    /// （见该方法注释），归还谷地/细胞阶段自然结束后 <see cref="GameLogic.Stage.GameRoot"/> 也会
    /// 重新打开本窗口，"返回菜单"因此复用同一入口。
    ///
    /// ER2-SCENE-01 起：新建/继续/读取成功后，把结果写入 <see cref="CampaignSession"/> 后关闭主菜单
    /// 并调用 <see cref="GameLogic.Stage.GameRoot.StartHomeValley()"/>/
    /// <see cref="GameLogic.Stage.GameRoot.ResumeHomeValley()"/> 进入归还谷地正式场景
    /// （<see cref="GameLogic.Campaign.Regions.HomeValleyController"/>）。</summary>
    [Window(UILayer.UI, location: "MainMenuUI")]
    public class MainMenuUI : UIWindow
    {
        private enum MenuView
        {
            Root,
            SlotList,
            ConfirmOverwrite,
            Settings,
        }

        #region 脚本工具生成的代码

        private Transform _tfRoot;
        private Button _btnNew;
        private Button _btnContinue;
        private Text _textContinueReason;
        private Button _btnLoad;
        private Button _btnSettings;
        private Button _btnQuit;

        private Transform _tfSettings;
        private Button _btnSettingsBack;

        private Transform _tfSlotList;
        private Text[] _slotInfoTexts;
        private Button[] _slotActionButtons;
        private Text[] _slotActionLabels;
        private Button _btnBack;

        private Transform _tfConfirmOverwrite;
        private Text _textConfirmInfo;
        private Button _btnConfirmYes;
        private Button _btnConfirmNo;

        protected override void ScriptGenerator()
        {
            _tfRoot = FindChild("m_tf_Root");
            _btnNew = FindChildComponent<Button>("m_tf_Root/m_btn_New");
            _btnContinue = FindChildComponent<Button>("m_tf_Root/m_btn_Continue");
            _textContinueReason = FindChildComponent<Text>("m_tf_Root/m_text_ContinueReason");
            _btnLoad = FindChildComponent<Button>("m_tf_Root/m_btn_Load");
            _btnSettings = FindChildComponent<Button>("m_tf_Root/m_btn_Settings");
            _btnQuit = FindChildComponent<Button>("m_tf_Root/m_btn_Quit");
            _btnNew.onClick.AddListener(OnNewClicked);
            _btnContinue.onClick.AddListener(OnContinueClicked);
            _btnLoad.onClick.AddListener(OnLoadClicked);
            _btnSettings.onClick.AddListener(OnSettingsClicked);
            _btnQuit.onClick.AddListener(OnQuitClicked);

            _tfSettings = FindChild("m_tf_Settings");
            _btnSettingsBack = FindChildComponent<Button>("m_tf_Settings/m_btn_SettingsBack");
            _btnSettingsBack.onClick.AddListener(OnSettingsBackClicked);

            _tfSlotList = FindChild("m_tf_SlotList");
            _slotInfoTexts = new[]
            {
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot0/m_text_Slot0Info"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot1/m_text_Slot1Info"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot2/m_text_Slot2Info"),
            };
            _slotActionButtons = new[]
            {
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot0/m_btn_Slot0Action"),
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot1/m_btn_Slot1Action"),
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot2/m_btn_Slot2Action"),
            };
            _slotActionLabels = new[]
            {
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot0/m_btn_Slot0Action/m_text_Slot0ActionLabel"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot1/m_btn_Slot1Action/m_text_Slot1ActionLabel"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot2/m_btn_Slot2Action/m_text_Slot2ActionLabel"),
            };
            _btnBack = FindChildComponent<Button>("m_tf_SlotList/m_btn_Back");
            _slotActionButtons[0].onClick.AddListener(() => OnSlotActionClicked(0));
            _slotActionButtons[1].onClick.AddListener(() => OnSlotActionClicked(1));
            _slotActionButtons[2].onClick.AddListener(() => OnSlotActionClicked(2));
            _btnBack.onClick.AddListener(OnBackClicked);

            _tfConfirmOverwrite = FindChild("m_tf_ConfirmOverwrite");
            _textConfirmInfo = FindChildComponent<Text>("m_tf_ConfirmOverwrite/m_text_ConfirmInfo");
            _btnConfirmYes = FindChildComponent<Button>("m_tf_ConfirmOverwrite/m_btn_ConfirmYes");
            _btnConfirmNo = FindChildComponent<Button>("m_tf_ConfirmOverwrite/m_btn_ConfirmNo");
            _btnConfirmYes.onClick.AddListener(OnConfirmYesClicked);
            _btnConfirmNo.onClick.AddListener(OnConfirmNoClicked);
        }

        #endregion

        /// <summary>覆盖写确认目标槽位；仅在"新建"遇到三槽全满时使用。-1 表示当前没有待确认的覆盖。</summary>
        private int _pendingOverwriteSlot = -1;

        protected override void OnCreate()
        {
            SetView(MenuView.Root);
        }

        protected override void OnRefresh()
        {
            SetView(MenuView.Root);
        }

        private void SetView(MenuView view)
        {
            _tfRoot.gameObject.SetActive(view == MenuView.Root);
            _tfSlotList.gameObject.SetActive(view == MenuView.SlotList);
            _tfConfirmOverwrite.gameObject.SetActive(view == MenuView.ConfirmOverwrite);
            _tfSettings.gameObject.SetActive(view == MenuView.Settings);

            if (view == MenuView.Root)
            {
                RefreshRootView();
            }
            else if (view == MenuView.SlotList)
            {
                RefreshSlotList();
            }
        }

        private void RefreshRootView()
        {
            int continueSlot = CampaignSaveService.ResolveContinueSlot();
            bool canContinue = continueSlot >= 0;
            _btnContinue.interactable = canContinue;
            // AC-UI-002：禁用按钮必须同时给出不可用原因，不能只是灰掉。
            _textContinueReason.text = canContinue ? string.Empty : "没有可读取的安全存档，请先新建战役";
        }

        private void RefreshSlotList()
        {
            for (int i = 0; i < CampaignSaveService.SlotCount; i++)
            {
                CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(i);
                RefreshSlotCard(i, meta);
            }
        }

        private void RefreshSlotCard(int slotIndex, CampaignSlotMetadata meta)
        {
            Text info = _slotInfoTexts[slotIndex];
            Button action = _slotActionButtons[slotIndex];
            Text label = _slotActionLabels[slotIndex];

            switch (meta.State)
            {
                case CampaignSlotState.Empty:
                    info.text = $"槽位 {slotIndex + 1}：空槽";
                    action.interactable = true;
                    label.text = "新建于此槽";
                    break;
                case CampaignSlotState.Ready:
                    // ERD-UI-001：存档项必须展示时间、战役阶段、游戏时长、最后区域和内容版本。
                    info.text = $"槽位 {slotIndex + 1}：{meta.CampaignId}\n阶段 {meta.CampaignPhase}　" +
                        $"游戏时长 {meta.PlaySeconds:F0}s\n存档时间 {meta.WrittenAtUtc}\n" +
                        $"最后区域 {(string.IsNullOrEmpty(meta.LastRegionId) ? "（尚未进入任何区域）" : meta.LastRegionId)}　" +
                        $"内容版本 {meta.ContentVersion}";
                    action.interactable = true;
                    label.text = "读取";
                    break;
                case CampaignSlotState.Corrupt:
                    info.text = $"槽位 {slotIndex + 1}：存档损坏（{meta.ErrorMessage}）" +
                        (meta.HasBackup ? "\n可尝试恢复备份" : "\n无可用备份，无法恢复");
                    action.interactable = meta.HasBackup;
                    label.text = "恢复备份";
                    break;
                case CampaignSlotState.Incompatible:
                    info.text = $"槽位 {slotIndex + 1}：存档版本（{meta.SchemaVersion}）比当前客户端更新，无法读取" +
                        (meta.HasBackup ? "\n可尝试恢复备份" : "");
                    action.interactable = meta.HasBackup;
                    label.text = meta.HasBackup ? "恢复备份" : "不可用";
                    break;
            }
        }

        #region 事件

        private void OnNewClicked()
        {
            int emptySlot = FindFirstEmptySlot();
            if (emptySlot >= 0)
            {
                StartNewCampaign(emptySlot);
                return;
            }

            // 三槽全满：按 STORY-EXECUTION-CARDS.md 要求，覆盖写入前先展示原档信息并二次确认；
            // 取消不改任何文件（Save 只在 OnConfirmYesClicked 里才会被调用）。固定取槽位 0，
            // 多槽选择 UI 留给 ER2-BOOT-01 的美术终稿。
            _pendingOverwriteSlot = 0;
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(0);
            _textConfirmInfo.text = $"槽位 1 已有存档：\ncampaignId={meta.CampaignId}\n阶段 {meta.CampaignPhase}　" +
                $"时长 {meta.PlaySeconds:F0}s\n存档时间 {meta.WrittenAtUtc}\n\n新建战役将覆盖此存档，是否继续？";
            SetView(MenuView.ConfirmOverwrite);
        }

        private void OnContinueClicked()
        {
            int slot = CampaignSaveService.ResolveContinueSlot();
            if (slot < 0)
            {
                return;
            }

            LoadIntoSession(slot);
        }

        private void OnLoadClicked()
        {
            SetView(MenuView.SlotList);
        }

        private void OnSettingsClicked()
        {
            SetView(MenuView.Settings);
        }

        private void OnSettingsBackClicked()
        {
            SetView(MenuView.Root);
        }

        private void OnBackClicked()
        {
            SetView(MenuView.Root);
        }

        private void OnQuitClicked()
        {
            Log.Info("[MainMenuUI] 玩家点击退出。");
            // 编辑器内 Application.Quit() 官方文档定义为无操作（不会中断 Play Mode），
            // 正式 Windows 构建里会真正退出进程。
            Application.Quit();
        }

        private void OnSlotActionClicked(int slotIndex)
        {
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slotIndex);
            switch (meta.State)
            {
                case CampaignSlotState.Empty:
                    StartNewCampaign(slotIndex);
                    break;
                case CampaignSlotState.Ready:
                    LoadIntoSession(slotIndex);
                    break;
                case CampaignSlotState.Corrupt:
                case CampaignSlotState.Incompatible:
                    if (meta.HasBackup)
                    {
                        bool restored = CampaignSaveService.RestoreFromBak(slotIndex);
                        Log.Info($"[MainMenuUI] 槽位 {slotIndex} 恢复备份：{(restored ? "成功" : "失败")}");
                    }

                    RefreshSlotList();
                    break;
            }
        }

        private void OnConfirmYesClicked()
        {
            int slot = _pendingOverwriteSlot;
            _pendingOverwriteSlot = -1;
            if (slot >= 0)
            {
                StartNewCampaign(slot);
            }
            else
            {
                SetView(MenuView.Root);
            }
        }

        private void OnConfirmNoClicked()
        {
            // 取消不改文件：这里全程没有调用过 CampaignSaveService.Save。
            _pendingOverwriteSlot = -1;
            SetView(MenuView.Root);
        }

        #endregion

        private int FindFirstEmptySlot()
        {
            for (int i = 0; i < CampaignSaveService.SlotCount; i++)
            {
                if (CampaignSaveService.GetSlotMetadata(i).State == CampaignSlotState.Empty)
                {
                    return i;
                }
            }

            return -1;
        }

        private void StartNewCampaign(int slotIndex)
        {
            string campaignId = Guid.NewGuid().ToString("N");
            // ER1-SAVE-02：不再用 Environment.TickCount 占位（DEBT-ER1SAVE01-05，精度约 15ms，
            // 连续快速新建可能撞种子）；CampaignRandomService.GenerateSeed() 用 CSPRNG 生成一次性种子，
            // 之后全部确定性消费统一走 CampaignRandomService.CreateRng(state)。
            int seed = CampaignRandomService.GenerateSeed();
            CampaignState state = CampaignState.CreateNew(campaignId, "Standard", seed);

            SaveResult result = CampaignSaveService.Save(slotIndex, state, SaveReason.NewCampaign);
            if (!result.Success)
            {
                Log.Warning($"[MainMenuUI] 新建战役保存失败（{result.Outcome}）：{result.Message}");
                SetView(MenuView.Root);
                return;
            }

            // ER1-ID-01：新战役必须清空上一局残留的机器身份分配器状态（AC-LIFE-002：
            // 新战役绝不继承上一战役的机器/控制记录）。哪怕本机进程从未跑过上一局，
            // 这里也无条件重置，不依赖"这是第一次进程内新建"这种脆弱判断。
            MachineRegistry.ResetForNewCampaign();

            CampaignSession.Set(slotIndex, state);
            Log.Info($"[MainMenuUI] 新战役已创建：campaignId={campaignId} slot={slotIndex} phase={state.CampaignPhase}");

            // ER2-SCENE-01：新战役进入归还谷地正式场景。GameApp.MountGameplayUi() 幂等，
            // 首次开局才真正挂载常驻 UI 壳层（战斗 HUD 等），与归还谷地自身的场景构建互不相关。
            GameApp.MountGameplayUi();
            Close();
            GameLogic.Stage.GameRoot.StartHomeValley();
        }

        private void LoadIntoSession(int slotIndex)
        {
            // ER1-SAVE-02：不再自己串 CampaignSaveService.Load + MachineRegistry.LoadFromCampaignState——
            // 统一走 CampaignRestoreOrchestrator，固定 ERD-SAV-003 恢复顺序，任一步致命失败原地中止，
            // 不触碰 CampaignSession，磁盘原档/备份不受影响。
            RestoreResult result = CampaignRestoreOrchestrator.Restore(slotIndex);
            if (!result.Success)
            {
                Log.Warning($"[MainMenuUI] 槽位 {slotIndex} 恢复编排在 {result.FailedStep} 步骤失败：{result.Message}");
                SetView(MenuView.SlotList);
                RefreshSlotList();
                // 安全错误页占位（ER2-BOOT-01 建正式错误页前）：复用槽位卡片显示失败原因 +
                // 保留"恢复备份"（无备份则禁用），不做任何写入或状态切换。
                _slotInfoTexts[slotIndex].text =
                    $"槽位 {slotIndex + 1}：恢复编排失败（{result.FailedStep}）：{result.Message}" +
                    (result.HasBackup ? "\n可尝试恢复备份" : "\n无可用备份，无法恢复");
                _slotActionButtons[slotIndex].interactable = result.HasBackup;
                _slotActionLabels[slotIndex].text = result.HasBackup ? "恢复备份" : "不可用";
                return;
            }

            if (result.Warnings.Length > 0)
            {
                Log.Info($"[MainMenuUI] 槽位 {slotIndex} 恢复编排完成，{result.Warnings.Length} 条非致命提示：" +
                    string.Join(" | ", result.Warnings));
            }

            CampaignSession.Set(slotIndex, result.State);
            Log.Info($"[MainMenuUI] 已读取战役：campaignId={result.State.CampaignId} slot={slotIndex} " +
                $"phase={result.State.CampaignPhase}");

            // ER2-SCENE-01："继续"与"读取"都走恢复语义（GameRoot.ResumeHomeValley，不是新局）——
            // Controller 内部按已有 RegionRecord/MachineRecord 复用，不重复播种。
            GameApp.MountGameplayUi();
            Close();
            GameLogic.Stage.GameRoot.ResumeHomeValley();
        }
    }
}
