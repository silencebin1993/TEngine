using System;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-005、009～011；FG13 FGU-08 / FGU-11；FGT-LOG-004 / 013）：建造模式里的规划工具——
    /// 复制 / 粘贴、布局库入口、升级规划、吸管、复制设置、撤销 / 重做。输入全部经 <see cref="InputRouter"/> 的动作（可重绑，B02），按钮与按键同一路径。
    /// 每帧开销与建筑数无关：粘贴预览只在光标换格 / 转向 / 规划变化时重算（O(布局件数)）。
    /// </summary>
    public sealed partial class HomeValleyBuildMode
    {
        /// <summary>当前画着的“放不了”标记（红叉）数（自检读）。</summary>
        public int ActiveMarkCount { get; private set; }

        // ── 快捷键 ──────────────────────────────────────────────────────────────

        /// <summary>建造模式没开时（战略视角）也能用的规划键：复制 / 粘贴 / 升级 / 吸管进入建造模式；撤销 / 重做 / 布局库直接生效。返回是否消费了一个键。</summary>
        private bool ConsumePlanKeysClosed(Camera camera, CampaignState state)
        {
            // 电路编辑器等占用了撤销键（UiUndoRouter）时，Ctrl+Z / Ctrl+Y 归它（FG1-SIG-02），建造撤销不抢。
            bool editorOwnsUndo = UiUndoRouter.HasTarget;
            if (!editorOwnsUndo && InputRouter.ConsumeAction(GameActionId.Undo, InputScope.Strategy))
            {
                Undo(state);
                return true;
            }
            if (!editorOwnsUndo && InputRouter.ConsumeAction(GameActionId.Redo, InputScope.Strategy))
            {
                Redo(state);
                return true;
            }
            if (InputRouter.ConsumeAction(GameActionId.Copy, InputScope.Strategy))
            {
                SetCopyMode(true);
                return true;
            }
            if (InputRouter.ConsumeAction(GameActionId.Paste, InputScope.Strategy))
            {
                StartPaste(state, Clipboard, null);
                return true;
            }
            if (InputRouter.ConsumeAction(GameActionId.UpgradePlan, InputScope.Strategy))
            {
                SetUpgradeMode(true);
                return true;
            }
            if (InputRouter.ConsumeAction(GameActionId.LayoutLibrary, InputScope.Strategy))
            {
                LayoutLibraryPanelUIToolkit.Toggle();
                return true;
            }
            if (InputRouter.ConsumeAction(GameActionId.Eyedropper, InputScope.Strategy))
            {
                if (camera != null && InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer) && TryPointerCell(camera, pointer, out GridCell cell))
                {
                    Eyedrop(state, cell);
                }
                return true;
            }
            return false;
        }

        private void ConsumePlanKeysOpen(CampaignState state)
        {
            bool editorOwnsUndo = UiUndoRouter.HasTarget;
            if (!editorOwnsUndo && InputRouter.ConsumeAction(GameActionId.Undo, InputScope.Strategy))
            {
                Undo(state);
            }
            if (!editorOwnsUndo && InputRouter.ConsumeAction(GameActionId.Redo, InputScope.Strategy))
            {
                Redo(state);
            }
            if (InputRouter.ConsumeAction(GameActionId.Copy, InputScope.Strategy))
            {
                SetCopyMode(!CopyMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.Paste, InputScope.Strategy))
            {
                StartPaste(state, Clipboard, null);
            }
            if (InputRouter.ConsumeAction(GameActionId.UpgradePlan, InputScope.Strategy))
            {
                SetUpgradeMode(!UpgradeMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.LayoutLibrary, InputScope.Strategy))
            {
                LayoutLibraryPanelUIToolkit.Toggle();
            }
            if (HasHover && InputRouter.ConsumeAction(GameActionId.Eyedropper, InputScope.Strategy))
            {
                Eyedrop(state, HoverCell);
            }
            if (HasHover && InputRouter.ConsumeAction(GameActionId.CopySettings, InputScope.Strategy))
            {
                CopySettingsAt(state, HoverCell);
            }
            if (HasHover && InputRouter.ConsumeAction(GameActionId.PasteSettings, InputScope.Strategy))
            {
                PasteSettingsAt(state, HoverCell);
            }
        }

        // ── 模式开关（按钮与按键同一路径）──────────────────────────────────────────────

        private void EnterPlanMode()
        {
            if (!IsOpen)
            {
                Open();
            }
            ExitPlanModes();
            SelectedTypeId = null;
            SelectedToolId = null;
            DemolishMode = false;
            RelocateMode = false;
            PrioritizeMode = false;
            ClearMode = false;
            CarryBuildingId = null;
            Preview = null;
            ToolPreview = null;
            CancelDrag();
            _previewKey = int.MinValue;
        }

        /// <summary>复制模式：拖一个框（松开 = 复制框里的东西，然后直接进入粘贴）。</summary>
        public void SetCopyMode(bool on)
        {
            if (on)
            {
                EnterPlanMode();
                CopyMode = true;
                SetStatus(GameText.Format("plan.copy.mode", InputDisplay.ForAction(GameActionId.LayoutLibrary)), false);
            }
            else
            {
                CopyMode = false;
                CancelDrag();
            }
            Revision++;
        }

        /// <summary>升级规划模式：拖一个框，框里能升级的建筑与传送带 / 管线原地升一级。</summary>
        public void SetUpgradeMode(bool on)
        {
            if (on)
            {
                EnterPlanMode();
                UpgradeMode = true;
                SetStatus(GameText.Get("plan.upgrade.mode"), false);
            }
            else
            {
                UpgradeMode = false;
                CancelDrag();
            }
            Revision++;
        }

        /// <summary>“复制设置”模式（按钮）：先点一件记下设置，再点别的件写上去；右键退出。</summary>
        public void SetSettingsMode(bool on)
        {
            if (on)
            {
                EnterPlanMode();
                SettingsMode = true;
                SetStatus(SettingsClip == null
                    ? GameText.Format("plan.settings.mode_pick", InputDisplay.ForAction(GameActionId.CopySettings))
                    : GameText.Format("plan.settings.mode_apply", SettingsClip.SourceName, InputDisplay.ForAction(GameActionId.PasteSettings)), false);
            }
            else
            {
                SettingsMode = false;
            }
            Revision++;
        }

        /// <summary>
        /// 开始粘贴 <paramref name="source"/>（剪贴板或布局库里的一个布局；<paramref name="name"/> 为布局名，剪贴板为 null）。
        /// 剪贴板是空的时给提示（先按复制键框选）。
        /// </summary>
        public bool StartPaste(CampaignState state, PlanEntryBlock source, string name)
        {
            if (PlanEntries.CountOf(source) == 0)
            {
                if (!IsOpen)
                {
                    Open();
                }
                SetStatus(GameText.Format("plan.paste.empty_clipboard", InputDisplay.ForAction(GameActionId.Copy), InputDisplay.ForAction(GameActionId.LayoutLibrary)), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return false;
            }
            EnterPlanMode();
            PasteMode = true;
            PasteSource = source;
            PasteName = name ?? string.Empty;
            PasteQuarter = 0;
            _pasteKey = int.MinValue;
            int locked = PlanEntries.LockedCount(state, source, out int unknown);
            string head = string.IsNullOrEmpty(name) ? GameText.Format("plan.paste.mode", PlanEntries.CountOf(source), InputDisplay.ForAction(GameActionId.Rotate))
                : GameText.Format("plan.paste.mode_named", name, PlanEntries.CountOf(source), InputDisplay.ForAction(GameActionId.Rotate));
            if (locked + unknown > 0)
            {
                // 负向“布局里包含未解锁的建筑”：一开始就写明有几件放不了（放置时这些件标红叉、不放）。
                head += "\n" + GameText.Format("plan.paste.locked_note", locked, unknown);
            }
            SetStatus(head, false);
            Revision++;
            return true;
        }

        // ── 复制 ────────────────────────────────────────────────────────────────

        private void CommitCopy(CampaignState state, GridCell a, GridCell b)
        {
            PlanEntryBlock block = PlanningService.Capture(state, a, b, out GridReason? refuse);
            if (block == null)
            {
                SetStatus(refuse?.Describe() ?? string.Empty, true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                LastResult = GridOpResult.Fail(refuse ?? GridReason.Of(GridBlockReason.NoBuilding));
                return;
            }
            Clipboard = block;
            GuidanceHooks.Raise(GuidanceHooks.BuildFirstCopy);
            StartPaste(state, block, null); // 复制完直接进入粘贴（布局跟着鼠标，旋转键转向）
            string skipped = PlanningService.LastSkipped > 0 ? GameText.Format("plan.copy.skipped", PlanningService.LastSkipped) : string.Empty;
            SetStatus(GameText.Format("plan.copy.done", block.Count, InputDisplay.ForAction(GameActionId.Rotate), InputDisplay.ForAction(GameActionId.LayoutLibrary)) + skipped, false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
        }

        /// <summary>把剪贴板存进布局库（布局库面板的“保存”与自检走这里）。</summary>
        public static bool SaveClipboardToLibrary(string name, out LayoutRecord saved, out string reason)
        {
            if (PlanEntries.CountOf(Clipboard) == 0)
            {
                saved = null;
                reason = GameText.Format("plan.library.clipboard_empty", InputDisplay.ForAction(GameActionId.Copy));
                return false;
            }
            return LayoutLibrary.TrySave(name, Clipboard, out saved, out reason);
        }

        /// <summary>自检 / 测试：直接设剪贴板（正式流程由复制框写）。</summary>
        public static void SetClipboardForTests(PlanEntryBlock block) => Clipboard = block;

        /// <summary>自检 / 测试：清掉复制设置的剪贴板。</summary>
        public static void ClearSettingsClipForTests() => SettingsClip = null;

        // ── 粘贴 ────────────────────────────────────────────────────────────────

        private void RefreshPastePreview(CampaignState state)
        {
            if (!PasteMode || PasteSource == null || !HasHover || state == null)
            {
                if (PastePreview != null)
                {
                    PastePreview = null;
                    Revision++;
                }
                return;
            }
            int key = HashCode(HoverCell.X, HoverCell.Y, PasteQuarter, HomeValleyConstruction.Revision * 31 + (BeltNetworkService.IsRunning ? BeltNetworkService.Kernel.CellCount : 0));
            if (key == _pasteKey && PastePreview != null && ReferenceEquals(_pasteRecordsRef, state.BuildingRecords))
            {
                return;
            }
            _pasteKey = key;
            _pasteRecordsRef = state.BuildingRecords;
            PastePreview = PlanningService.PlanPaste(state, PasteSource, HoverCell, PasteQuarter, into: _pasteBuffer);
            Revision++;
        }

        /// <summary>左键粘贴：放下合法的部分（虚影），非法的不放、写明第一处原因；一次粘贴是撤销栈里的一步。粘贴模式保持（可以接着再贴）。</summary>
        private GridOpResult CommitPaste(CampaignState state)
        {
            RefreshPastePreview(state);
            PastePlan plan = PastePreview;
            if (plan == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoRegion)), state);
            }
            if (plan.OkCount == 0)
            {
                LastResult = GridOpResult.Fail(plan.FirstBadItem?.Reason ?? GridReason.Of(GridBlockReason.NoBuilding));
                SetStatus(GameText.Format("plan.paste.none_ok", plan.BadCount, plan.DescribeFirstBad()), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            PlanApplyResult r = PlanHistory.Paste(state, plan);
            LastPaste = r;
            _pasteKey = int.MinValue;
            int notPlaced = plan.BadCount + r.Failed;
            string text = GameText.Format("plan.paste.done", r.PlacedBuildings, r.PlacedPieces, plan.Cost, state.Scrap);
            if (notPlaced > 0)
            {
                string why = plan.BadCount > 0 ? plan.DescribeFirstBad() : (r.FirstFailureName + "：" + (r.FirstFailure?.Describe() ?? string.Empty));
                text += "\n" + GameText.Format("plan.paste.some_bad", notPlaced, why);
            }
            LastResult = new GridOpResult(GridOpResult.Kind.Pasted, null);
            SetStatus(text, notPlaced > 0);
            Feedback.FeedbackCues.Raise(r.Placed > 0 ? Feedback.FeedbackCueId.CommandAck : Feedback.FeedbackCueId.Denied, StatusText);
            return LastResult;
        }

        /// <summary>粘贴预览：每件的格子（能放绿、不能放红），不能放的件另叠一个红叉（形状区分，色盲安全）。最多画 plan.preview_max_tiles 格。</summary>
        private int PlacePasteTiles(int tile, out int mark)
        {
            mark = 0;
            int max = Math.Max(16, GridContent.TuningInt("plan.preview_max_tiles"));
            int hidden = 0;
            foreach (PasteItem it in PastePreview.Items)
            {
                foreach (GridCell c in it.Cells)
                {
                    if (tile >= max)
                    {
                        hidden++;
                        continue;
                    }
                    PlaceTile(tile++, c, it.Ok ? _okMaterial : _badMaterial, 0.35f);
                }
                if (!it.Ok)
                {
                    PlaceMark(mark++, it.Cells.Count > 0 ? it.Cells[0] : it.Cell);
                }
            }
            PasteTilesHidden = hidden;
            return tile;
        }

        private int PlaceUpgradeTiles(CampaignState state, int tile)
        {
            int max = Math.Max(16, GridContent.TuningInt("plan.preview_max_tiles"));
            foreach (UpgradeGroup g in UpgradePreview.Groups)
            {
                foreach (GridCell c in g.Cells)
                {
                    if (tile < max)
                    {
                        PlaceTile(tile++, c, _okMaterial, 0.35f);
                    }
                }
            }
            foreach (string id in UpgradePreview.Buildings)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, id);
                if (b == null)
                {
                    continue;
                }
                HomeGridService.FootprintOf(b, _cells);
                foreach (GridCell c in _cells)
                {
                    if (tile < max)
                    {
                        PlaceTile(tile++, c, _okMaterial, 1.2f);
                    }
                }
            }
            return tile;
        }

        /// <summary>一个“放不了”的红叉（池化）。</summary>
        private void PlaceMark(int index, GridCell cell)
        {
            while (_marks.Count <= index)
            {
                var m = new GameObject("PasteBadMark" + _marks.Count);
                m.transform.SetParent(_root.transform, false);
                for (int i = 0; i < 2; i++)
                {
                    GameObject bar = NewPrimitive(PrimitiveType.Cube, "Bar" + i, m.transform, _badMaterial);
                    bar.transform.localRotation = Quaternion.Euler(0f, i == 0 ? 45f : -45f, 0f);
                    bar.transform.localScale = new Vector3(1.1f, 0.12f, 0.22f);
                }
                _marks.Add(m);
            }
            GameObject go = _marks[index];
            go.SetActive(true);
            go.transform.position = new Vector3(cell.X, 0.7f, cell.Y);
        }

        // ── 升级 ────────────────────────────────────────────────────────────────

        private void CommitUpgrade(CampaignState state, UpgradeBoxPlan plan)
        {
            if (plan.IsEmpty)
            {
                LastResult = GridOpResult.Fail(plan.FirstRefusal ?? GridReason.Of(GridBlockReason.NoUpgrade));
                SetStatus(plan.FirstRefusal != null
                    ? GameText.Format("plan.upgrade.none_reason", plan.Refused, plan.FirstRefusal.Value.Describe())
                    : GameText.Get("plan.upgrade.none"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return;
            }
            int done = PlanHistory.Upgrade(state, plan, out GridReason? fail);
            LastResult = done > 0 ? new GridOpResult(GridOpResult.Kind.UpgradePlanned, null) : GridOpResult.Fail(fail ?? GridReason.Of(GridBlockReason.NoUpgrade));
            string text = GameText.Format("plan.upgrade.done_box", done, plan.Cost, state.Scrap);
            if (plan.Refused > 0 && plan.FirstRefusal != null)
            {
                text += "\n" + GameText.Format("plan.upgrade.some_refused", plan.Refused, plan.FirstRefusal.Value.Describe());
            }
            SetStatus(text, done == 0);
            Feedback.FeedbackCues.Raise(done > 0 ? Feedback.FeedbackCueId.CommandAck : Feedback.FeedbackCueId.Denied, StatusText);
        }

        // ── 吸管 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 吸管（FGR-LOG-011）：指着一座建筑 / 一件物流件（建成的或虚影）→ 选中同类（建造菜单同一个条目），朝向与它相同，并带上它的设置（下一次放置生效）。
        /// 指着空地给提示；本局不能建造的开局建筑给原因。
        /// </summary>
        public GridOpResult Eyedrop(CampaignState state, GridCell cell)
        {
            if (state == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoRegion)), null);
            }
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            string id;
            int rotation;
            int s0 = 0, s1 = 0, s2 = 0;
            if (b != null)
            {
                id = b.BuildingTypeId;
                if (!BuildCatalog.TryGet(id, out _))
                {
                    if (!IsOpen)
                    {
                        Open();
                    }
                    LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NotPlaceable));
                    SetStatus(GameText.Format("plan.eyedrop.not_placeable", HomeGridService.DisplayName(id)), true);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                    return LastResult;
                }
                rotation = GridMath.NormalizeRotation(b.Rotation);
                s0 = PlanSettings.FamilyOf(PlanEntryKind.Building, id) == PlanSettings.Family.Power ? Math.Max(1, b.PowerPriority) : 0;
            }
            else if (PlanSettings.TryReadPiece(state, cell, out _, out id, out s0, out s1, out s2))
            {
                rotation = PieceDir(state, cell) * 90;
            }
            else
            {
                if (!IsOpen)
                {
                    Open();
                }
                LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
                SetStatus(GameText.Get("plan.eyedrop.nothing"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            Select(id);
            GhostRotation = GridMath.NormalizeRotation(rotation);
            PendingS0 = s0;
            PendingS1 = s1;
            PendingS2 = s2;
            _previewKey = int.MinValue;
            GuidanceHooks.Raise(GuidanceHooks.BuildFirstEyedropper);
            PlanEntryKind kind = PlanEntries.KindOf(id, out _);
            PlanSettings.Family fam = PlanSettings.FamilyOf(kind, id);
            string settings = fam != PlanSettings.Family.None && (s0 != 0 || s1 != 0 || s2 != 0) ? GameText.Format("plan.eyedrop.with_settings", PlanSettings.Describe(fam, s0, s1, s2)) : string.Empty;
            SetStatus(GameText.Format("plan.eyedrop.done", PlanEntries.NameOf(id), GameText.Get(GridMath.DirTextKey(GridMath.FacingOf(GhostRotation)))) + settings, false);
            LastResult = new GridOpResult(GridOpResult.Kind.Failed, null);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
            return LastResult;
        }

        private static int PieceDir(CampaignState state, GridCell cell)
        {
            if (BeltNetworkService.IsRunning && BeltNetworkService.TryGetNode(cell, out BeltNodeInfo node))
            {
                return (int)node.Dir;
            }
            if (BeltNetworkService.IsRunning && BeltNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return (int)info.Dir;
            }
            if (PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo pi))
            {
                return pi.Dir;
            }
            return HomeValleyConstruction.TryFindPlannedCell(state, cell, out PlannedBeltRecord p, out int i) ? p.Dirs[i] : 0;
        }

        // ── 复制设置 ──────────────────────────────────────────────────────────────

        /// <summary>记下 (cell) 那一件的设置（Alt+C / 复制设置模式的第一下）。</summary>
        public GridOpResult CopySettingsAt(CampaignState state, GridCell cell)
        {
            if (!PlanSettings.TryRead(state, cell, out PlanSettings.Clip clip, out GridReason why))
            {
                LastResult = GridOpResult.Fail(why);
                SetStatus(why.Describe(), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            SettingsClip = clip;
            LastResult = new GridOpResult(GridOpResult.Kind.Failed, null);
            SetStatus(GameText.Format("plan.settings.copied", clip.SourceName, PlanSettings.Describe(clip.Family, clip.S0, clip.S1, clip.S2),
                InputDisplay.ForAction(GameActionId.PasteSettings)), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
            return LastResult;
        }

        /// <summary>把记下的设置写到 (cell) 那一件上（Alt+V / 复制设置模式的后续点击）。不兼容时写明原因。进撤销栈。</summary>
        public GridOpResult PasteSettingsAt(CampaignState state, GridCell cell)
        {
            if (SettingsClip == null)
            {
                LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NoSettings));
                SetStatus(GameText.Format("plan.settings.no_clip", InputDisplay.ForAction(GameActionId.CopySettings)), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            if (!PlanHistory.ApplySettings(state, cell, SettingsClip, out string target, out GridReason why))
            {
                LastResult = GridOpResult.Fail(why);
                SetStatus(why.Describe(), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            LastResult = new GridOpResult(GridOpResult.Kind.SettingsApplied, null);
            SetStatus(GameText.Format("plan.settings.applied", target, PlanSettings.Describe(SettingsClip.Family, SettingsClip.S0, SettingsClip.S1, SettingsClip.S2)), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
            return LastResult;
        }

        // ── 撤销 / 重做 ──────────────────────────────────────────────────────────

        public PlanStepResult Undo(CampaignState state) => Replay(state, redo: false);

        public PlanStepResult Redo(CampaignState state) => Replay(state, redo: true);

        private PlanStepResult Replay(CampaignState state, bool redo)
        {
            CancelDrag();
            PlanStepResult r = redo ? PlanHistory.Redo(state) : PlanHistory.Undo(state);
            LastStep = r;
            _previewKey = int.MinValue;
            _pasteKey = int.MinValue;
            if (HasHover && state != null)
            {
                HoverBuildingId = HomeGridService.BuildingAt(state, HoverCell)?.BuildingId;
            }
            SetStatus(r.Text, !r.Done || (r.Changed == 0 && r.Skipped > 0));
            Feedback.FeedbackCues.Raise(r.Done && r.Changed > 0 ? Feedback.FeedbackCueId.CommandAck : Feedback.FeedbackCueId.Denied, StatusText);
            return r;
        }
    }
}
