using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.URDFExport.CSV;
using SW2URDF.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace SW2URDF.UI
{
    // A modeless WinForms window: no PropertyManager command, no selection-list lifecycle coupling.
    public sealed class ExportConfigurationForm : Form
    {
        private static readonly log4net.ILog logger = Logger.GetLogger();
        private readonly IAssemblyWorkspace workspace;
        private readonly DraftStore store;
        private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true };
        private readonly TextBox linkName = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox jointName = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox coordinates = NewCombo();
        private readonly ComboBox axes = NewCombo();
        private readonly ComboBox jointType = NewCombo();
        private readonly NumericUpDown childCount = new NumericUpDown { Minimum = 0, Maximum = 10000, Dock = DockStyle.Fill };
        private readonly ListBox componentList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
        private readonly Label documentStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly Label referenceStatus = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DarkRed };
        private readonly Label csvLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly ToolStripStatusLabel draftStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly CheckBox captureSelection = new CheckBox { Text = "开始连续拾取", Appearance = Appearance.Button,
            AutoSize = true, Height = 32, Padding = new Padding(5, 2, 5, 2), Margin = new Padding(3) };
        private readonly List<Label> fieldLabels = new List<Label>();
        private Label treeHeader;
        private readonly CheckBox[] compute = {
            new CheckBox { Text = "计算质量 / 惯量", Checked = true, AutoSize = true },
            new CheckBox { Text = "计算外观 / 碰撞", Checked = true, AutoSize = true },
            new CheckBox { Text = "计算关节运动学", Checked = true, AutoSize = true },
            new CheckBox { Text = "计算关节限制", Checked = true, AutoSize = true }
        };
        private readonly Timer documentTimer = new Timer { Interval = 800 };
        private readonly Timer pickDraftTimer = new Timer { Interval = 500 };
        private Func<string, DialogResult> confirmFrameRecalculation;
        private readonly List<Control> swActions = new List<Control>();
        private Dictionary<string, string> componentNames = new Dictionary<string, string>();
        private readonly HashSet<string> currentComponentKeys = new HashSet<string>(StringComparer.Ordinal);
        private LinkNode root, current;
        private int updating;
        private bool ready, discarded, exportInProgress, saveFailed;
        private string lastSignature;
        private DateTime lastCheckpointUtc;
        private Form details;
        private System.Windows.Window csvMerge;
        private Control configurationBody;
        private string lastLoadError;
        private bool loadRetryBlocked, draftWarningShown;
        private bool selectionCapturePending, handlingWorkspaceSelection, workspaceDisposed;
        private int selectionGeneration;
        private bool resumeCaptureAfterReconnect;
        private TableLayoutPanel fieldLayout;
        private int componentRowIndex, componentResizeStartY;
        private float componentResizeStartHeight;
        private readonly Panel componentResizeGrip = new Panel
        {
            Dock = DockStyle.Bottom, Height = 9, Cursor = Cursors.HSplit,
            BackColor = SystemColors.ControlLight
        };

        public ExportConfigurationForm(IAssemblyWorkspace workspace, DraftStore store)
        {
            this.workspace = workspace;
            this.store = store;
            confirmFrameRecalculation = message => MessageBox.Show(this, message, "坐标系与关节位置不一致",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            Text = "URDF 配置 · " + workspace.Title;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 700);
            Size = new Size(980, 780);
            KeyPreview = true;
            BuildLayout();
            WireEvents();
            workspace.SelectionChanged += WorkspaceSelectionChanged;
            TryLoadConfiguration();
            documentTimer.Tick += (sender, args) => UpdateDocumentState();
            pickDraftTimer.Tick += (sender, args) => { pickDraftTimer.Stop(); Checkpoint(); };
            documentTimer.Start();
            UpdateDocumentState();
        }

        private static ComboBox NewCombo() => new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

        private Button Button(string text, Action action, bool requiresSw = false)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 32, Padding = new Padding(5, 2, 5, 2), Margin = new Padding(3) };
            button.Click += (sender, args) => Run(action);
            if (requiresSw) swActions.Add(button);
            return button;
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.Controls.Add(documentStatus, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            toolbar.Controls.Add(Button("刷新模型引用", RefreshReferences, true));
            toolbar.Controls.Add(Button("清除失效引用", ClearInvalidReferences, true));
            toolbar.Controls.Add(Button("清空当前 Link", ClearComponents));
            toolbar.Controls.Add(Button("导入 CSV…", ImportCsv, true));
            var topmost = new CheckBox { Text = "窗口置顶", AutoSize = true, Margin = new Padding(18, 8, 3, 3) };
            topmost.CheckedChanged += (sender, args) => TopMost = topmost.Checked;
            toolbar.Controls.Add(topmost);
            layout.Controls.Add(toolbar, 0, 1);

            const string treeHeaderText = "Link / Joint 结构（拖动调整父级）";
            int treePanelWidth = Math.Max(300, MeasureSingleLine(treeHeaderText, Font) + 28);
            var split = new SplitContainer { Size = new Size(940, 560), Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1, Panel1MinSize = 290, Panel2MinSize = 500,
                SplitterDistance = Math.Min(340, treePanelWidth) };
            configurationBody = split;
            configurationBody.Enabled = false;
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            treeHeader = new Label { Text = treeHeaderText, Dock = DockStyle.Fill,
                AutoEllipsis = false, TextAlign = ContentAlignment.MiddleLeft };
            left.Controls.Add(treeHeader, 0, 0);
            left.Controls.Add(tree, 0, 1);
            var treeButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            treeButtons.Controls.Add(Button("添加子 Link", AddChild));
            treeButtons.Controls.Add(Button("删除", RemoveNode));
            left.Controls.Add(treeButtons, 0, 2);
            split.Panel1.Controls.Add(left);

            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(10, 0, 0, 0) };
            fieldLayout = fields;
            int fieldLabelWidth = Math.Max(170, MeasureSingleLine("坐标系 / 全局原点", Font) + 34);
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, fieldLabelWidth));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(fields, "Link 名称", linkName, fieldLabels);
            AddRow(fields, "Joint 名称", jointName, fieldLabels);
            AddRow(fields, "坐标系 / 全局原点", coordinates, fieldLabels);
            AddRow(fields, "关节轴", axes, fieldLabels);
            AddRow(fields, "关节类型", jointType, fieldLabels);
            AddRow(fields, "子 Link 数量", childCount, fieldLabels);
            var componentArea = new Panel { Dock = DockStyle.Fill };
            componentArea.Controls.Add(componentList);
            componentArea.Controls.Add(componentResizeGrip);
            componentResizeGrip.BringToFront();
            componentRowIndex = fields.RowCount;
            AddRow(fields, "关联零件", componentArea, fieldLabels, 128);
            var picks = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            picks.Controls.Add(Button("添加当前 SW 所选", AddSwSelection, true));
            picks.Controls.Add(captureSelection);
            picks.Controls.Add(Button("移除所选", RemoveComponents));
            picks.Controls.Add(Button("在 SW 中高亮", () => HighlightCurrent(), true));
            fields.Controls.Add(picks, 0, fields.RowCount);
            fields.SetColumnSpan(picks, 2);
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); fields.RowCount++;
            AddRow(fields, "", new Label { Text = "点击关联框后，在 SW 连续点选零件表面；点列表项可单独高亮，拖动底边可调整高度。", Dock = DockStyle.Fill }, fieldLabels, 44);
            var computations = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            foreach (CheckBox check in compute) computations.Controls.Add(check);
            AddRow(fields, "导出计算", computations, fieldLabels, 55);
            AddRow(fields, "CSV", csvLabel, fieldLabels, 26);
            AddRow(fields, "引用检查", referenceStatus, fieldLabels, 55);
            split.Panel2.Controls.Add(fields);
            layout.Controls.Add(split, 0, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.Add(Button("保存配置到装配体", SaveToAssembly, true));
            actions.Controls.Add(Button("下一步：导出设置", ContinueExport, true));
            actions.Controls.Add(Button("关闭（保留草稿）", Close));
            actions.Controls.Add(Button("放弃草稿…", Discard));
            layout.Controls.Add(actions, 0, 3);
            var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false };
            status.Items.Add(draftStatus);
            layout.Controls.Add(status, 0, 4);
            Controls.Add(layout);
            jointType.Items.AddRange(new object[] { "Automatically Detect", "continuous", "revolute", "prismatic", "fixed" });
            BuildComponentMenu();
        }

        private static void AddRow(TableLayoutPanel panel, string label, Control control, List<Label> labels, int height = 34)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var rowLabel = new Label { Text = label, Dock = DockStyle.Fill, AutoEllipsis = false,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 0, 8, 0) };
            panel.Controls.Add(rowLabel, 0, row);
            if (!string.IsNullOrEmpty(label)) labels.Add(rowLabel);
            control.Margin = new Padding(3, 3, 3, 5);
            panel.Controls.Add(control, 1, row);
        }

        private static int MeasureSingleLine(string text, Font font)
        {
            return TextRenderer.MeasureText(text ?? "", font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        }

        private void ResizeComponentListStart(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left || fieldLayout == null) return;
            componentResizeStartY = Cursor.Position.Y;
            componentResizeStartHeight = fieldLayout.RowStyles[componentRowIndex].Height;
            componentResizeGrip.Capture = true;
        }

        private void ResizeComponentListMove(object sender, MouseEventArgs args)
        {
            if (!componentResizeGrip.Capture || fieldLayout == null) return;
            ResizeComponentListBy(Cursor.Position.Y - componentResizeStartY);
        }

        private void ResizeComponentListBy(int delta)
        {
            if (fieldLayout == null) return;
            float height = Math.Max(90, Math.Min(520, componentResizeStartHeight + delta));
            fieldLayout.RowStyles[componentRowIndex].SizeType = SizeType.Absolute;
            fieldLayout.RowStyles[componentRowIndex].Height = height;
            fieldLayout.PerformLayout();
        }

        private void BuildComponentMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("开始连续拾取", null, (sender, args) => captureSelection.Checked = !captureSelection.Checked);
            menu.Items.Add("在 SW 中高亮选中项", null, (sender, args) => HighlightCurrent(SelectedComponentIndices()));
            menu.Items.Add("在 SW 中高亮当前 Link 全部", null, (sender, args) => HighlightCurrent());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("移除选中关联", null, (sender, args) => RemoveComponents());
            menu.Items.Add("清空当前 Link 全部关联", null, (sender, args) => ClearComponents());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全选列表", null, (sender, args) => SelectAllComponents());
            menu.Items.Add("复制零件名称", null, (sender, args) => CopyComponentNames());
            menu.Items.Add("刷新模型引用", null, (sender, args) => Run(RefreshReferences));
            menu.Items.Add("清除失效引用", null, (sender, args) => Run(ClearInvalidReferences));
            menu.Opening += (sender, args) =>
            {
                bool hasItems = componentList.Items.Count > 0;
                bool hasSelection = componentList.SelectedIndices.Count > 0;
                menu.Items[0].Text = captureSelection.Checked ? "停止连续拾取" : "开始连续拾取";
                menu.Items[1].Enabled = hasSelection && captureSelection.Enabled;
                menu.Items[2].Enabled = hasItems && captureSelection.Enabled;
                menu.Items[4].Enabled = hasSelection;
                menu.Items[5].Enabled = hasItems;
                menu.Items[7].Enabled = hasItems;
                menu.Items[8].Enabled = hasSelection;
                menu.Items[9].Enabled = captureSelection.Enabled;
                menu.Items[10].Enabled = captureSelection.Enabled;
            };
            componentList.ContextMenuStrip = menu;
        }

        private void WireEvents()
        {
            tree.AfterSelect += (sender, args) =>
            {
                if (updating != 0) return;
                ShowNode((LinkNode)args.Node); Checkpoint();
                if (args.Action != TreeViewAction.Unknown) ActivateComponentPicking();
            };
            linkName.TextChanged += (sender, args) => EditFields();
            jointName.TextChanged += (sender, args) => EditFields();
            coordinates.SelectedIndexChanged += (sender, args) => EditFields();
            axes.SelectedIndexChanged += (sender, args) => EditFields();
            jointType.SelectedIndexChanged += (sender, args) => EditFields();
            childCount.ValueChanged += (sender, args) => { if (updating == 0) Run(ChangeChildCount); };
            foreach (CheckBox check in compute) check.CheckedChanged += (sender, args) => { if (updating == 0) Checkpoint(); };
            componentList.SelectedIndexChanged += (sender, args) =>
            { if (updating == 0 && componentList.SelectedIndices.Count > 0) HighlightSelectedComponents(); };
            componentList.MouseDown += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && captureSelection.Enabled)
                {
                    captureSelection.Checked = true;
                    HighlightSelectedComponents();
                }
                if (args.Button == MouseButtons.Right)
                {
                    int index = componentList.IndexFromPoint(args.Location);
                    if (index >= 0 && !componentList.GetSelected(index))
                    {
                        componentList.ClearSelected();
                        componentList.SetSelected(index, true);
                    }
                }
            };
            componentList.Enter += (sender, args) =>
            { if (captureSelection.Enabled) captureSelection.Checked = true; };
            componentList.KeyDown += ComponentListKeyDown;
            componentResizeGrip.Paint += (sender, args) =>
            {
                int middle = componentResizeGrip.ClientSize.Width / 2;
                using (var pen = new Pen(SystemColors.ControlDark))
                    for (int offset = -12; offset <= 12; offset += 6)
                        args.Graphics.DrawLine(pen, middle + offset, 3, middle + offset + 3, 6);
            };
            componentResizeGrip.MouseDown += ResizeComponentListStart;
            componentResizeGrip.MouseMove += ResizeComponentListMove;
            componentResizeGrip.MouseUp += (sender, args) => componentResizeGrip.Capture = false;
            captureSelection.CheckedChanged += (sender, args) =>
            {
                InvalidatePendingSelection();
                captureSelection.Text = captureSelection.Checked ? "停止连续拾取" : "开始连续拾取";
                componentList.BackColor = captureSelection.Checked ? Color.FromArgb(225, 240, 255) : SystemColors.Window;
                if (captureSelection.Checked)
                    draftStatus.Text = "关联框已激活：在 SW 中连续选择零件";
            };
            tree.ItemDrag += (sender, args) => { if (args.Item != root) tree.DoDragDrop(args.Item, DragDropEffects.Move); };
            tree.DragOver += (sender, args) => args.Effect = DragDropEffects.Move;
            tree.DragDrop += (sender, args) => Run(() =>
            {
                LinkNode moving = args.Data.GetData(typeof(LinkNode)) as LinkNode;
                LinkNode target = tree.GetNodeAt(tree.PointToClient(new Point(args.X, args.Y))) as LinkNode;
                if (!CanReparent(moving, target, root)) return;
                updating++;
                try { moving.Remove(); target.Nodes.Add(moving); tree.SelectedNode = moving; tree.ExpandAll(); }
                finally { updating--; }
                ShowNode(moving); Checkpoint();
            });
            FormClosing += ClosingEditor;
        }

        public static bool CanReparent(LinkNode moving, LinkNode target, LinkNode root)
        {
            if (moving == null || target == null || moving == root || moving == target) return false;
            for (TreeNode ancestor = target; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor == moving) return false;
            return true;
        }

        private void TryLoadConfiguration()
        {
            if (ready || loadRetryBlocked) return;
            try { LoadConfiguration(); }
            catch (Exception error)
            {
                // Never replace unread document configuration with an empty draft after a COM failure.
                string diagnostic = error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message;
                if (diagnostic != lastLoadError) logger.Warn("Configuration loading deferred: " + diagnostic, error);
                lastLoadError = diagnostic;
                bool transient = error is System.Runtime.InteropServices.COMException ||
                    error is System.Runtime.InteropServices.InvalidComObjectException ||
                    (error is InvalidOperationException && !workspace.IsAvailable(out string reason));
                loadRetryBlocked = !transient;
                draftStatus.Text = transient ? "配置暂未读入，将自动重试；尚未创建草稿。" :
                    "配置读取失败，请查看日志后重新打开；尚未创建草稿。";
                referenceStatus.Text = diagnostic;
            }
        }

        private void LoadConfiguration()
        {
            ExportDraft draft = store.Load(workspace.AssemblyPath, out string warning);
            if (warning != null && !draftWarningShown)
            { draftWarningShown = true; MessageBox.Show(this, warning, "URDF 草稿恢复"); }
            if (draft == null && !workspace.IsAvailable(out string reason))
            {
                draftStatus.Text = "等待 SW 恢复后读取原配置；尚未创建草稿。";
                return;
            }
            LinkNode loaded = draft == null ? workspace.LoadSavedTree() : ConfigurationSerialization.DeserializeFromString(draft.ConfigurationXml);
            if (draft != null)
            {
                componentNames = draft.ComponentNames ?? new Dictionary<string, string>();
                if (draft.ComputeOptions != null && draft.ComputeOptions.Length == 4)
                    for (int i = 0; i < 4; i++) compute[i].Checked = draft.ComputeOptions[i];
                csvLabel.Text = draft.CsvLabel ?? "";
            }
            SetTree(loaded ?? EmptyNode(true), draft?.SelectedNode);
            ready = true;
            configurationBody.Enabled = true;
            lastLoadError = null;
            draftStatus.Text = draft == null ? "自动草稿已开启；关闭窗口 / Esc 均保留进度" : "已恢复 " + draft.SavedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + " 的草稿";
            if (workspace.IsAvailable(out string availableReason))
            {
                try { RefreshReferences(); }
                catch (Exception error)
                {
                    logger.Warn("Initial reference refresh deferred; configuration retained", error);
                    referenceStatus.Text = "模型引用暂未刷新，请在 SW 恢复后点击刷新模型引用。";
                }
                ActivateComponentPicking();
            }
        }

        private void SetTree(LinkNode newRoot, int[] selectedPath = null)
        {
            updating++;
            try
            {
                root = newRoot; root.IsBaseNode = true;
                tree.Nodes.Clear(); tree.Nodes.Add(root); tree.ExpandAll();
                LinkNode selected = root;
                foreach (int index in selectedPath ?? new int[0])
                { if (index < 0 || index >= selected.Nodes.Count) break; selected = (LinkNode)selected.Nodes[index]; }
                tree.SelectedNode = selected;
                ShowNode(selected);
            }
            finally { updating--; }
        }

        private static void SelectValue(ComboBox combo, IEnumerable<string> items, string value)
        {
            combo.Items.Clear();
            foreach (string item in items.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct()) combo.Items.Add(item);
            if (!string.IsNullOrEmpty(value) && !combo.Items.Contains(value)) combo.Items.Add(value);
            combo.SelectedItem = value;
            if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void ShowNode(LinkNode node)
        {
            if (!ReferenceEquals(current, node)) InvalidatePendingSelection();
            updating++;
            try
            {
                current = node;
                linkName.Text = node.Link.Name;
                jointName.Text = node.IsBaseNode ? "" : node.Link.Joint.Name;
                jointName.Enabled = axes.Enabled = jointType.Enabled = !node.IsBaseNode;
                childCount.Value = Math.Min(childCount.Maximum, node.Nodes.Count);
                SelectValue(coordinates, new[] { "Automatically Generate" }.Concat(workspace.Coordinates), node.Link.Joint.CoordinateSystemName);
                SelectValue(axes, new[] { "Automatically Generate", "None" }.Concat(workspace.Axes), node.Link.Joint.AxisName);
                SelectValue(jointType, new[] { "Automatically Detect", "continuous", "revolute", "prismatic", "fixed" }, node.Link.Joint.Type);
                if (node.IsBaseNode) { axes.SelectedIndex = -1; jointType.SelectedIndex = -1; }
                RenderComponents();
            }
            finally { updating--; }
        }

        private void EditFields()
        {
            if (updating > 0 || current == null || !ready) return;
            current.Link.Name = current.Name = current.Text = linkName.Text;
            current.Link.Joint.CoordinateSystemName = coordinates.Text;
            if (!current.IsBaseNode)
            {
                current.Link.Joint.Name = jointName.Text;
                current.Link.Joint.AxisName = axes.Text;
                current.Link.Joint.Type = jointType.Text;
            }
            Checkpoint();
        }

        private LinkNode EmptyNode(bool isBase)
        {
            var node = new LinkNode { IsBaseNode = isBase, IsIncomplete = true };
            string name = isBase ? "base_link" : "link_" + (CommonSwOperations.GetCount(tree.Nodes) + 1);
            node.Link.Name = node.Name = node.Text = name;
            node.Link.Joint.Name = isBase ? "" : name + "_joint";
            node.Link.Joint.CoordinateSystemName = "Automatically Generate";
            node.Link.Joint.AxisName = isBase ? "" : "Automatically Generate";
            node.Link.Joint.Type = "Automatically Detect";
            return node;
        }

        private void AddChild()
        {
            if (current == null) return;
            LinkNode child = EmptyNode(false);
            current.Nodes.Add(child); current.Expand(); tree.SelectedNode = child; Checkpoint();
        }

        private void RemoveNode()
        {
            if (current == null || current == root) return;
            if (MessageBox.Show(this, "删除此 Link 及其子节点？", "删除 Link", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            LinkNode parent = (LinkNode)current.Parent;
            updating++;
            try { current.Remove(); tree.SelectedNode = parent; ShowNode(parent); }
            finally { updating--; }
            Checkpoint();
        }

        private void ChangeChildCount()
        {
            if (current == null) return;
            int wanted = (int)childCount.Value;
            if (wanted < current.Nodes.Count && MessageBox.Show(this, "减少数量会删除末尾的子 Link 及其子节点。继续？",
                "减少子 Link", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            { ShowNode(current); return; }
            while (current.Nodes.Count < wanted) current.Nodes.Add(EmptyNode(false));
            while (current.Nodes.Count > wanted) current.Nodes.RemoveAt(current.Nodes.Count - 1);
            current.Expand(); Checkpoint();
        }

        private static string IdKey(byte[] id) => Convert.ToBase64String(id);
        private void AddSwSelection()
        {
            CaptureSwSelection(false, true);
        }

        private bool CaptureSwSelection(bool directComponentsOnly, bool requireSelection, bool automatic = false)
        {
            if (exportInProgress || !Enabled) return false;
            ComponentPick latest = automatic ? workspace.ReadLatestSelection() : null;
            List<ComponentPick> picks = automatic ?
                (latest == null ? new List<ComponentPick>() : new List<ComponentPick> { latest }) :
                workspace.ReadSelection(directComponentsOnly);
            if (picks.Count == 0)
            {
                if (requireSelection) throw new InvalidOperationException("请先在 SW 中选择一个或多个零件。");
                return false;
            }
            bool changed = false;
            foreach (ComponentPick pick in picks)
            {
                string key = IdKey(pick.Id);
                if (currentComponentKeys.Add(key))
                {
                    current.Link.SWComponentPIDs.Add(pick.Id);
                    componentList.Items.Add(pick.Name);
                    changed = true;
                }
                componentNames[key] = pick.Name;
            }
            if (changed) QueuePickCheckpoint();
            if (automatic && latest.NeedsComponentHighlight)
            {
                handlingWorkspaceSelection = true;
                try { workspace.HighlightPick(latest); }
                finally { handlingWorkspaceSelection = false; }
            }
            else if (!automatic) HighlightCurrent();
            return true;
        }

        private int[] SelectedComponentIndices()
        {
            return componentList.SelectedIndices.Cast<int>().OrderBy(index => index).ToArray();
        }

        private void HighlightSelectedComponents()
        {
            int[] indices = SelectedComponentIndices();
            if (indices.Length > 0) HighlightCurrent(indices);
        }

        private void HighlightCurrent(int[] indices = null)
        {
            if (exportInProgress || !Enabled || !ready || current == null || !workspace.IsAvailable(out string reason)) return;
            Run(() =>
            {
                handlingWorkspaceSelection = true;
                try { workspace.Highlight(current, indices); }
                finally { handlingWorkspaceSelection = false; }
            });
        }

        private void ActivateComponentPicking()
        {
            if (exportInProgress || !Enabled || !captureSelection.Enabled || !ready || current == null) return;
            captureSelection.Checked = true;
            HighlightCurrent();
        }

        private void InvalidatePendingSelection()
        {
            selectionGeneration++;
            selectionCapturePending = false;
        }

        private void StopComponentPicking()
        {
            resumeCaptureAfterReconnect = false;
            captureSelection.Checked = false;
            InvalidatePendingSelection();
        }

        private void WorkspaceSelectionChanged(AssemblySelectionChange change)
        {
            if (exportInProgress || !Enabled || handlingWorkspaceSelection || !captureSelection.Checked ||
                !ready || IsDisposed || !IsHandleCreated) return;
            if (change == AssemblySelectionChange.Cleared)
            {
                // SW also clears selection before a new pick or another command. Do not
                // reselect the old components here: that can cancel the user's operation.
                return;
            }
            if (selectionCapturePending) return;
            selectionCapturePending = true;
            int generation = selectionGeneration;
            try { BeginInvoke(new Action(() =>
            {
                if (generation == selectionGeneration) ProcessWorkspaceSelection();
            })); }
            catch (InvalidOperationException) { selectionCapturePending = false; }
        }

        private void ProcessWorkspaceSelection()
        {
            selectionCapturePending = false;
            if (exportInProgress || !Enabled || handlingWorkspaceSelection || !captureSelection.Checked || !ready || IsDisposed) return;
            try
            {
                CaptureSwSelection(false, false, true);
            }
            catch (Exception error)
            {
                logger.Warn("Continuous component pick paused", error);
                captureSelection.Checked = false;
                documentStatus.Text = "连续拾取已暂停：" + error.Message;
            }
        }

        private void RemoveComponents()
        {
            foreach (int index in componentList.SelectedIndices.Cast<int>().OrderByDescending(i => i))
                current.Link.SWComponentPIDs.RemoveAt(index);
            PruneUnusedComponentNames();
            RenderComponents(); Checkpoint(); HighlightCurrent();
        }

        private void ClearInvalidReferences()
        {
            if (root == null) return;
            List<InvalidComponentReference> invalid = workspace.FindInvalidReferences(root);
            if (invalid.Count == 0)
            {
                referenceStatus.Text = "没有可清除的失效引用；被抑制零件会保留。";
                return;
            }
            StopComponentPicking();
            foreach (var group in invalid.GroupBy(item => item.Node))
                foreach (int index in group.Select(item => item.Index).OrderByDescending(index => index))
                    group.Key.Link.SWComponentPIDs.RemoveAt(index);
            PruneUnusedComponentNames();
            List<string> remaining = workspace.ResolveTree(root);
            FillComponentNames(root);
            RenderComponents();
            Checkpoint();
            referenceStatus.Text = "已清除 " + invalid.Count + " 个失效引用。" +
                (remaining.Count == 0 ? "" : " 仍有被抑制或不可用的零件引用，请检查模型。");
            referenceStatus.ForeColor = remaining.Count == 0 ? Color.FromArgb(35, 95, 66) : Color.DarkRed;
        }

        private static void CollectComponentKeys(LinkNode node, HashSet<string> used)
        {
            foreach (byte[] id in node.Link.SWComponentPIDs)
                if (id != null) used.Add(IdKey(id));
            foreach (LinkNode child in node.Nodes) CollectComponentKeys(child, used);
        }

        private void PruneUnusedComponentNames()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            CollectComponentKeys(root, used);
            foreach (string key in componentNames.Keys.ToArray())
                if (!used.Contains(key)) componentNames.Remove(key);
        }

        private void ClearComponents()
        {
            if (current == null || current.Link.SWComponentPIDs.Count == 0) return;
            if (MessageBox.Show(this, "清空当前 Link 的全部关联零件？", "清空关联",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            StopComponentPicking();
            current.Link.SWComponentPIDs.Clear();
            PruneUnusedComponentNames();
            RenderComponents(); Checkpoint(); HighlightCurrent();
        }

        private void SelectAllComponents()
        {
            updating++;
            try
            {
                for (int i = 0; i < componentList.Items.Count; i++) componentList.SetSelected(i, true);
            }
            finally { updating--; }
            HighlightCurrent();
        }

        private void CopyComponentNames()
        {
            string text = string.Join(Environment.NewLine,
                componentList.SelectedIndices.Cast<int>().OrderBy(index => index)
                    .Select(index => componentList.Items[index].ToString()));
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }

        private void ComponentListKeyDown(object sender, KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Delete)
            {
                RemoveComponents(); args.Handled = true; args.SuppressKeyPress = true;
            }
            else if (args.Control && args.KeyCode == Keys.A)
            {
                SelectAllComponents(); args.Handled = true; args.SuppressKeyPress = true;
            }
            else if (args.KeyCode == Keys.Enter)
            {
                HighlightSelectedComponents(); args.Handled = true; args.SuppressKeyPress = true;
            }
        }

        private void RenderComponents()
        {
            updating++;
            componentList.BeginUpdate();
            try
            {
                componentList.Items.Clear();
                currentComponentKeys.Clear();
                int index = 0;
                foreach (byte[] id in current.Link.SWComponentPIDs)
                {
                    index++;
                    string key = id == null ? "" : IdKey(id);
                    currentComponentKeys.Add(key);
                    componentList.Items.Add(componentNames.TryGetValue(key, out string name) ? name : "已关联零件 " + index + "（刷新可检查引用）");
                }
            }
            finally { componentList.EndUpdate(); updating--; }
        }

        private bool Checkpoint()
        {
            if (!ready || discarded || updating > 0 || root == null || exportInProgress) return true;
            try
            {
                string xml = ConfigurationSerialization.SerializeToString(root);
                var path = new List<int>();
                for (TreeNode node = current; node != null && node.Parent != null; node = node.Parent) path.Add(node.Index);
                path.Reverse();
                bool[] options = compute.Select(box => box.Checked).ToArray();
                string signature = xml + "|" + string.Join(",", path) + "|" + string.Join(",", options) + "|" + csvLabel.Text;
                if (signature == lastSignature) return true;
                store.Save(new ExportDraft { AssemblyPath = workspace.AssemblyPath, ConfigurationXml = xml,
                    SelectedNode = path.ToArray(), ComputeOptions = options, CsvLabel = csvLabel.Text,
                    ComponentNames = new Dictionary<string, string>(componentNames) });
                lastSignature = signature; saveFailed = false;
                lastCheckpointUtc = DateTime.UtcNow;
                pickDraftTimer.Stop();
                draftStatus.Text = "草稿已保存 " + DateTime.Now.ToString("HH:mm:ss") + "；关闭窗口后可恢复";
                return true;
            }
            catch (Exception error)
            {
                logger.Error("Could not save modeless editor draft", error);
                draftStatus.Text = "草稿保存失败：" + error.Message;
                if (!saveFailed) { saveFailed = true; MessageBox.Show(this, "自动草稿保存失败。请手动保存配置。\r\n" + error.Message, "草稿保存失败"); }
                return false;
            }
        }

        private void QueuePickCheckpoint()
        {
            // Keep the first pick durable immediately, then coalesce rapid picks.
            // Closing, saving, and export still force a synchronous checkpoint.
            if ((DateTime.UtcNow - lastCheckpointUtc).TotalMilliseconds >= 500) Checkpoint();
            else if (!pickDraftTimer.Enabled) pickDraftTimer.Start();
        }

        private void UpdateDocumentState()
        {
            if (exportInProgress) return;
            if (!ready) TryLoadConfiguration();
            bool available = workspace.IsAvailable(out string reason);
            documentStatus.Text = reason;
            documentStatus.ForeColor = available ? Color.FromArgb(35, 95, 66) : Color.DarkOrange;
            foreach (Control action in swActions) action.Enabled = ready && available && !exportInProgress;
            captureSelection.Enabled = ready && available && !exportInProgress;
            if (!available && captureSelection.Checked)
            {
                resumeCaptureAfterReconnect = true;
                captureSelection.Checked = false;
            }
            else if (available && resumeCaptureAfterReconnect)
            {
                resumeCaptureAfterReconnect = false;
                ActivateComponentPicking();
            }
        }

        private void RefreshReferences()
        {
            workspace.RefreshGeometry();
            List<string> issues = workspace.ResolveTree(root);
            FillComponentNames(root);
            referenceStatus.Text = issues.Count == 0 ? "零件引用正常；导出前会再次检查坐标系和轴。" : string.Join("\r\n", issues.Distinct().Take(2));
            referenceStatus.ForeColor = issues.Count == 0 ? Color.FromArgb(35, 95, 66) : Color.DarkRed;
            ShowNode(current); Checkpoint();
        }

        private void FillComponentNames(LinkNode node)
        {
            if (node.Link.SWComponents.Count == node.Link.SWComponentPIDs.Count)
                for (int i = 0; i < node.Link.SWComponentPIDs.Count; i++) componentNames[IdKey(node.Link.SWComponentPIDs[i])] = node.Link.SWComponents[i].Name2;
            foreach (LinkNode child in node.Nodes) FillComponentNames(child);
        }

        private void SaveToAssembly()
        {
            if (!Checkpoint()) return;
            workspace.SaveConfiguration(root);
            draftStatus.Text = "配置已写入装配体；请用 SW 保存文件。恢复草稿继续保留。";
        }

        private void ContinueExport()
        {
            if (exportInProgress) return;
            if (!Checkpoint()) return;
            StopComponentPicking();
            if (!compute[2].Checked)
            {
                List<JointFrameMismatch> mismatches = workspace.FindJointFrameMismatches(root);
                if (mismatches.Count > 0)
                {
                    string examples = string.Join("\r\n", mismatches.Take(4).Select(item =>
                        item.JointName + "（" + item.LinkName + "）：位置差 " +
                        item.DistanceMm.ToString("0.0") + " mm，方向差 " + item.AngleDeg.ToString("0.0") + "°"));
                    string message = "当前 SW 坐标系与已保存的 URDF 关节位置不一致，共 " + mismatches.Count + " 处：\r\n" +
                        examples + (mismatches.Count > 4 ? "\r\n…" : "") +
                        "\r\n\r\n继续沿用旧关节位置可能让新导出的网格错位。" +
                        "\r\n“是”：开启关节和惯量重算并继续；“否”：保留手工关节值继续；“取消”：不导出。";
                    DialogResult decision = confirmFrameRecalculation(message);
                    if (decision == DialogResult.Cancel) return;
                    if (decision == DialogResult.Yes)
                    {
                        compute[2].Checked = true;
                        compute[0].Checked = true;
                        if (!Checkpoint()) return;
                        referenceStatus.Text = "已启用关节和惯量重算；导出前会使用当前 SW 坐标系。";
                        referenceStatus.ForeColor = Color.FromArgb(35, 95, 66);
                    }
                    else if (decision != DialogResult.No) return;
                }
            }
            // PrepareExport itself changes SW selections and can pump window messages.
            // Suspend the editor before making that call, not only after it returns.
            exportInProgress = true;
            Enabled = false;
            try
            {
            // Export code mutates the tree and SW selections. Work on a separate graph.
            LinkNode exportRoot = ConfigurationSerialization.DeserializeFromString(ConfigurationSerialization.SerializeToString(root));
            details = workspace.PrepareExport(exportRoot, compute.Select(box => box.Checked).ToArray());
            if (details is AssemblyExportForm assemblyDetails)
                assemblyDetails.RecoverySnapshot += SaveDetailSnapshot;
            details.FormClosed += (sender, args) =>
            {
                details = null;
                RestoreEditorAfterExport();
                Run(() =>
                {
                    ExportDraft latest = store.Load(workspace.AssemblyPath, out string warning);
                    if (warning != null) logger.Warn(warning);
                    if (latest != null)
                    {
                        updating++;
                        try
                        {
                            SetTree(ConfigurationSerialization.DeserializeFromString(latest.ConfigurationXml), latest.SelectedNode);
                            if (latest.ComputeOptions != null && latest.ComputeOptions.Length == 4)
                                for (int i = 0; i < 4; i++) compute[i].Checked = latest.ComputeOptions[i];
                        }
                        finally { updating--; }
                    }
                    lastSignature = null; Checkpoint();
                });
                Activate();
                StopComponentPicking();
            };
            details.Show(this);
            }
            catch
            {
                if (details != null)
                {
                    if (details is AssemblyExportForm assemblyDetails)
                        assemblyDetails.RecoverySnapshot -= SaveDetailSnapshot;
                    details.Dispose();
                    details = null;
                }
                RestoreEditorAfterExport();
                throw;
            }
        }

        private void RestoreEditorAfterExport()
        {
            // Keep the guard set while WinForms restores focus to the owner.
            Enabled = true;
            StopComponentPicking();
            exportInProgress = false;
            UpdateDocumentState();
        }

        private void SaveDetailSnapshot(LinkNode snapshot)
        {
            store.Save(new ExportDraft
            {
                AssemblyPath = workspace.AssemblyPath,
                ConfigurationXml = ConfigurationSerialization.SerializeToString(snapshot),
                ComputeOptions = new[] { false, false, false, false },
                CsvLabel = csvLabel.Text,
                ComponentNames = new Dictionary<string, string>(componentNames)
            });
        }

        private void ImportCsv()
        {
            using (var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                List<Link> links;
                using (Stream stream = dialog.OpenFile()) links = ImportExport.LoadURDFRobotFromCSV(stream);
                if (links == null) throw new InvalidDataException("CSV 读取失败。");
                var merger = new TreeMergeWPF(root.RebuildLink().Clone(), links, Path.GetFileName(dialog.FileName), workspace.Title);
                csvMerge = merger; Enabled = false;
                merger.Closed += (sender, args) => { csvMerge = null; Enabled = true; Activate(); };
                merger.TreeMerged += (sender, args) => Run(() =>
                {
                    if (!args.Success || args.MergedTree.Items.Count != 1) throw new InvalidDataException("CSV 合并未成功，原草稿保留。");
                    LinkNode merged = FromMerged((System.Windows.Controls.TreeViewItem)args.MergedTree.Items[0], true);
                    // The merger retains the CAD links' persistent component IDs.
                    CaptureMergedIds(merged);
                    updating++;
                    try
                    {
                        SetTree(merged);
                        compute[0].Checked = !args.UsedCSVInertial; compute[1].Checked = !args.UsedCSVVisualCollision;
                        compute[2].Checked = !args.UsedCSVJointKinematics; compute[3].Checked = !args.UsedCSVJointOther;
                        csvLabel.Text = args.CSVFilename;
                    }
                    finally { updating--; }
                    Checkpoint();
                });
                merger.Show();
            }
        }

        private void CaptureMergedIds(LinkNode node)
        {
            // CSV merging changes metadata, not CAD component assignments.
            if (node.Link.SWComponentPIDs == null) node.Link.SWComponentPIDs = new List<byte[]>();
            foreach (LinkNode child in node.Nodes) CaptureMergedIds(child);
        }

        private static LinkNode FromMerged(System.Windows.Controls.TreeViewItem item, bool isBase)
        {
            Link link = (Link)item.Tag;
            var node = new LinkNode { Link = link, Name = link.Name, Text = link.Name, IsBaseNode = isBase };
            foreach (System.Windows.Controls.TreeViewItem child in item.Items) node.Nodes.Add(FromMerged(child, false));
            return node;
        }

        private void Discard()
        {
            if (MessageBox.Show(this, "放弃草稿并关闭？重新打开将使用装配体中已保存的配置。", "放弃草稿",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            store.Discard(workspace.AssemblyPath); discarded = true; Close();
        }

        private void ClosingEditor(object sender, FormClosingEventArgs args)
        {
            if (exportInProgress || csvMerge != null) { args.Cancel = true; return; }
            if (!Checkpoint() && args.CloseReason == CloseReason.UserClosing)
            { args.Cancel = true; return; }
            pickDraftTimer.Stop();
            documentTimer.Stop();
        }

        public void Shutdown()
        {
            // Called while the add-in still owns its SW references. Preserve the editor state
            // before releasing any COM objects; owned dialogs must not keep a detached add-in alive.
            if (csvMerge != null) csvMerge.Close();
            if (details != null) details.Close();
            if (details != null && !details.IsDisposed) details.Dispose();
            exportInProgress = false;
            Checkpoint();
            Close();
            if (!IsDisposed) Dispose();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                documentTimer.Dispose();
                pickDraftTimer.Dispose();
                if (!workspaceDisposed)
                {
                    workspaceDisposed = true;
                    workspace.SelectionChanged -= WorkspaceSelectionChanged;
                    workspace.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception error) { logger.Error("URDF configuration action failed", error); MessageBox.Show(this, error.Message, "URDF 配置"); }
        }

        public bool IsForAssembly(object document, string assemblyPath)
        {
            return workspace.IsBoundTo(document, assemblyPath);
        }
    }
}
