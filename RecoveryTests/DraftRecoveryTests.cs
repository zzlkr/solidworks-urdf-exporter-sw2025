using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using SW2URDF.UI;

class FakeWorkspace : IAssemblyWorkspace
{
    public event Action<AssemblySelectionChange> SelectionChanged;
    public string AssemblyPath { get; set; }
    public string Title => "测试机械臂.SLDASM";
    public bool Available = true;
    public int SelectionReads;
    public int FullSelectionReads;
    public int RecentSelectionReads;
    public int HighlightCalls;
    public int PickHighlightCalls;
    public ComponentPick LastHighlightedPick;
    public int[] LastHighlightIndices;
    public int DisposeCalls;
    public bool RaiseEventOnHighlight;
    public bool LastReadWasDirect;
    public int SavedTreeReads;
    public bool FailLoad;
    public Action DuringPrepareExport;
    public List<JointFrameMismatch> FrameMismatches = new List<JointFrameMismatch>();
    public int FrameMismatchReads;
    public int PrepareExportCalls;
    public bool[] LastComputeOptions;
    public List<ComponentPick> Selection = new List<ComponentPick>();
    public List<ComponentPick> SurfaceSelection = new List<ComponentPick>();
    public HashSet<string> InvalidIds = new HashSet<string>();
    public LinkNode Saved;
    public List<string> Coordinates => new List<string> { "坐标系1", "机械臂原点" };
    public List<string> Axes => new List<string> { "旋转轴1" };
    public bool IsBoundTo(object document, string assemblyPath) => ReferenceEquals(document, DocumentMarker) &&
        string.Equals(AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase);
    public readonly object DocumentMarker = new object();
    public bool IsAvailable(out string reason) { reason = Available ? "测试：SW 装配体已连接" : "测试：装配体已切换，SW 操作暂停"; return Available; }
    public LinkNode LoadSavedTree()
    {
        SavedTreeReads++;
        if (FailLoad) throw new COMException("Call rejected", unchecked((int)0x80010001));
        return Saved;
    }
    public void RefreshGeometry() { }
    public List<ComponentPick> ReadSelection(bool directComponentsOnly)
    {
        SelectionReads++; FullSelectionReads++; LastReadWasDirect = directComponentsOnly;
        return directComponentsOnly ? Selection : Selection.Concat(SurfaceSelection).ToList();
    }
    public ComponentPick ReadLatestSelection()
    {
        SelectionReads++; RecentSelectionReads++; LastReadWasDirect = false;
        return Selection.Concat(SurfaceSelection).LastOrDefault();
    }
    public List<string> ResolveTree(LinkNode root) => new List<string>();
    public void HighlightPick(ComponentPick pick)
    {
        PickHighlightCalls++; LastHighlightedPick = pick;
        if (RaiseEventOnHighlight) SelectionChanged?.Invoke(AssemblySelectionChange.Added);
    }
    public List<InvalidComponentReference> FindInvalidReferences(LinkNode root)
    {
        var invalid = new List<InvalidComponentReference>();
        FindInvalid(root, invalid);
        return invalid;
    }
    public List<JointFrameMismatch> FindJointFrameMismatches(LinkNode root)
    {
        FrameMismatchReads++;
        return FrameMismatches;
    }
    void FindInvalid(LinkNode node, List<InvalidComponentReference> invalid)
    {
        for (int i = 0; i < node.Link.SWComponentPIDs.Count; i++)
            if (InvalidIds.Contains(Convert.ToBase64String(node.Link.SWComponentPIDs[i])))
                invalid.Add(new InvalidComponentReference { Node = node, Index = i });
        foreach (LinkNode child in node.Nodes) FindInvalid(child, invalid);
    }
    public void Highlight(LinkNode node, int[] indices)
    {
        HighlightCalls++; LastHighlightIndices = indices;
        if (RaiseEventOnHighlight) SelectionChanged?.Invoke(AssemblySelectionChange.Added);
    }
    public void SaveConfiguration(LinkNode root) { Saved = root; }
    public Form PrepareExport(LinkNode root, bool[] compute)
    {
        PrepareExportCalls++;
        LastComputeOptions = compute;
        DuringPrepareExport?.Invoke();
        return new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000) };
    }
    public void RaiseSelectionChanged(AssemblySelectionChange change = AssemblySelectionChange.Added) { SelectionChanged?.Invoke(change); }
    public void Dispose() { DisposeCalls++; }
}

class DraftRecoveryTests
{
    static int passed;
    static string output;
    static BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Flags).GetValue(obj);
    static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags).Invoke(obj, args);
    static string Serialize(LinkNode node) => (string)typeof(ConfigurationSerialization).GetMethod("SerializeToString", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { node });
    static LinkNode Deserialize(string xml) => (LinkNode)typeof(ConfigurationSerialization).GetMethod("DeserializeFromString", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { xml });
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
    static LinkNode Root(string name = "base_link")
    {
        var root = new LinkNode { Name = name, Text = name, IsBaseNode = true };
        root.Link.Name = name;
        root.Link.Joint.CoordinateSystemName = "坐标系1";
        return root;
    }
    static ExportDraft Draft(string path, LinkNode tree) => new ExportDraft { AssemblyPath = path, ConfigurationXml = Serialize(tree) };

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            // Suppress the upstream home-directory logger in this isolated test executable.
            typeof(SW2URDF.Utilities.Logger).GetField("Initialized", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true);
            Application.EnableVisualStyles();
            StoreTests();
            RobotStructureTests();
            LinkMassSelectionTests();
            EditorTests();
            GuardTests();
            BusyStartupTests();
            ComponentInteractionTests();
            LargeLinkAndInvalidCleanupTests();
            JointFramePreflightTests();
            ExportSelectionIsolationTests();
            AssemblySwitchTests();
            Console.WriteLine("ALL " + passed + " CHECKS PASSED");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }

    static void LinkMassSelectionTests()
    {
        Type helper = typeof(Link).Assembly.GetType("SW2URDF.URDFExport.LinkMassSelection", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo whole = helper.GetMethod("WholeComponents", flags);
        MethodInfo conflict = helper.GetMethod("FindConflict", flags);
        MethodInfo apply = helper.GetMethod("Apply", flags);
        var paths = new[] { "小装配-1/支架-1", "小装配-1", "小装配-2/支架-1", "小装配-1", "电池-1" };
        Check(((string[])whole.Invoke(null, new object[] { paths })).SequenceEqual(
            new[] { "小装配-1", "小装配-2/支架-1", "电池-1" }),
            "whole subassembly replaces its own descendant picks without merging other instances");

        var assignments = new[] { new KeyValuePair<string, string>("小装配-1", "base_link"),
            new KeyValuePair<string, string>("小装配-1/支架-1", "arm_link") };
        string message = (string)conflict.Invoke(null, new object[] { assignments });
        Check(message != null && message.Contains("base_link") && message.Contains("arm_link"),
            "split whole-subassembly mass conflict reports both Links");
        assignments[1] = new KeyValuePair<string, string>("小装配-2/支架-1", "arm_link");
        Check(conflict.Invoke(null, new object[] { assignments }) == null,
            "same part under a separate subassembly instance does not conflict");
        assignments[1] = new KeyValuePair<string, string>("小装配-1", "arm_link");
        Check(conflict.Invoke(null, new object[] { assignments }) != null,
            "assigning a single instance to two Links is rejected");

        Link link = Root().Link;
        double[] center = { 0.12, -0.04, 0.38 };
        double[] moment = { 2, 0.1, -0.2, 0.1, 3, 0.3, -0.2, 0.3, 4 };
        apply.Invoke(null, new object[] { link, 12.5, center, moment });
        Check(link.Inertial.Mass.Value == 12.5 && link.Inertial.Origin.GetXYZ().SequenceEqual(center) &&
            link.Inertial.Origin.GetRPY().SequenceEqual(new double[3]) &&
            link.Inertial.Inertia.Ixx == 2 && link.Inertial.Inertia.Iyy == 3 && link.Inertial.Inertia.Izz == 4 &&
            link.Inertial.Inertia.Ixy == -0.1 && link.Inertial.Inertia.Ixz == 0.2 && link.Inertial.Inertia.Iyz == -0.3,
            "SW mass, COM and all six inertia values are written together with URDF signs");
        bool rejected = false;
        try { apply.Invoke(null, new object[] { link, 13.0, center, new[] { double.NaN } }); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
        Check(rejected && link.Inertial.Mass.Value == 12.5 && link.Inertial.Inertia.Izz == 4,
            "invalid inertia read cannot leave a partially updated Link");
    }

    static void StoreTests()
    {
        var store = new DraftStore(Path.Combine(output, "store"));
        string path = Path.Combine(output, "装配体", "机器人.SLDASM");
        var root = Root();
        var child = Root("未完成 link"); child.IsBaseNode = false; child.Link.Joint.Name = "";
        child.Link.Joint.AxisName = "旋转轴1";
        child.Link.SWComponentPIDs.Add(new byte[] { 0, 255, 127, 3 });
        root.Nodes.Add(child); root.Nodes.Add(Root("未完成 link"));
        var draft = Draft(path, root); draft.SelectedNode = new[] { 1 }; draft.ComputeOptions[2] = false;
        store.Save(draft);
        var recovered = store.Load(path, out string warning);
        var recoveredTree = Deserialize(recovered.ConfigurationXml);
        Check(warning == null && recoveredTree.Nodes.Count == 2, "incomplete tree and duplicate names survive disk roundtrip");
        Check(((LinkNode)recoveredTree.Nodes[0]).Link.Joint.AxisName == "旋转轴1" && recoveredTree.Link.Joint.CoordinateSystemName == "坐标系1", "Unicode geometry names preserved");
        Check(((LinkNode)recoveredTree.Nodes[0]).Link.SWComponentPIDs[0].SequenceEqual(new byte[] { 0, 255, 127, 3 }), "binary component IDs preserved without COM");
        Check(recovered.SelectedNode[0] == 1 && !recovered.ComputeOptions[2], "editing position and compute options preserved");
        Check(store.GetPath(path.ToLowerInvariant()) == store.GetPath(path.ToUpperInvariant()), "Windows path casing resolves to same draft");
        Check(store.GetPath(path) != store.GetPath(Path.Combine(output, "另一个目录", "机器人.SLDASM")), "same filename in different folders remains isolated");
        string first = recovered.ConfigurationXml;
        store.Save(Draft(path, Root("second")));
        File.WriteAllText(store.GetPath(path), "<broken");
        recovered = store.Load(path, out warning);
        Check(recovered.ConfigurationXml == first && warning != null, "corrupt primary falls back to previous complete checkpoint");
        store.Save(Draft(path, Root("third")));
        File.WriteAllText(store.GetPath(path), "<broken again");
        Check(store.Load(path, out warning).ConfigurationXml == first, "replacing corrupt primary does not destroy good backup");
        store.Save(Draft(path, Root("valid")));
        bool rejected = false;
        try { store.Save(new ExportDraft { AssemblyPath = path, ConfigurationXml = "<invalid/>" }); }
        catch { rejected = true; }
        Check(rejected && Deserialize(store.Load(path, out warning).ConfigurationXml).Name == "valid", "invalid snapshot cannot overwrite a good draft");
        store.SaveCommitted(path, Serialize(Root("advanced_saved")));
        Check(Deserialize(store.Load(path, out warning).ConfigurationXml).Name == "advanced_saved", "later detailed-form save supersedes older editor draft");
        store.Discard(path);
        Check(store.Load(path, out warning) == null && !File.Exists(store.GetPath(path) + ".bak"), "explicit discard clears both generations");
    }

    static void RobotStructureTests()
    {
        var robot = new Robot();
        var root = new Link { Name = "base_link" };
        root.Joint.Name = "wheel_fr_y_Link"; // stale joint in an older base-link draft
        root.Joint.Type = "revolute";
        var camera = new Link { Name = "perception_cam_5_link" };
        camera.Joint.Name = "perception_cam_5_link";
        camera.Joint.Type = "fixed";
        root.Children.Add(camera); // endpoints were never computed
        robot.SetBaseLink(root);
        Check(robot.RepairAndValidateStructure() == "" && root.Joint.Name == "" &&
            camera.Joint.Parent.Name == "base_link" && camera.Joint.Child.Name == camera.Name &&
            ReferenceEquals(camera.Parent, root), "export repairs root joint and missing child endpoints");

        var duplicate = new Link { Name = "wheel_fr_y_Link" };
        duplicate.Joint.Name = camera.Joint.Name;
        duplicate.Joint.Type = "fixed";
        root.Children.Add(duplicate);
        Check(robot.RepairAndValidateStructure().Contains("Joint 名称为空或重复"),
            "export blocks duplicate joint names");
    }

    static bool TextFits(Label label)
    {
        int width = TextRenderer.MeasureText(label.Text ?? "", label.Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        return width <= label.ClientSize.Width - label.Padding.Horizontal;
    }

    static void GuardTests()
    {
        object bound = new object(), active = bound;
        string path = @"C:\models\robot.SLDASM";
        bool editingSelf = true, rejectActive = false, rejectEdit = false;
        int reports = 0;
        var guard = new DocumentSessionGuard(bound,
            () => { if (rejectActive) throw new COMException("Rejected", unchecked((int)0x80010001)); return active; },
            () => path,
            () => { if (rejectEdit) throw new COMException("Unavailable edit target", unchecked((int)0x80004005)); return editingSelf; },
            path, "robot", (step, error) => reports++);
        Check(guard.IsAvailable(out string reason), "normal assembly passes status check without IsSame or GetEditTargetComponent");
        active = new object();
        Check(!guard.IsAvailable(out reason), "different document with identical path cannot pass identity guard");
        active = null;
        Check(!guard.IsAvailable(out reason), "closed document pauses SW actions");
        active = bound; editingSelf = false;
        Check(!guard.IsAvailable(out reason) && reason.Contains("编辑零件"), "in-context editing pauses SW actions");
        editingSelf = true; path = @"C:\models\renamed.SLDASM";
        Check(!guard.IsAvailable(out reason) && reason.Contains("路径"), "Save As pauses bound document session");
        path = @"c:\MODELS\ROBOT.sldasm";
        Check(guard.IsAvailable(out reason), "same path with different casing remains available");
        rejectActive = true;
        Check(!guard.IsAvailable(out reason) && reason.Contains("80010001") && reason.Contains("读取活动文档"), "COM rejection retains HRESULT and failing operation");
        guard.IsAvailable(out reason);
        Check(reports == 1, "repeated timer failures do not flood the log");
        rejectActive = false; rejectEdit = true;
        Check(!guard.IsAvailable(out reason) && reason.Contains("80004005") && reason.Contains("IsEditingSelf"), "edit-target query failure is reported without misclassifying document closure");
        rejectEdit = false;
        Check(guard.IsAvailable(out reason), "SW actions resume automatically after transient COM failure");

        // Exercise the native IUnknown path with real COM wrappers, without touching SolidWorks.
        object first = null, wrapper = null, other = null;
        IntPtr identity = IntPtr.Zero;
        try
        {
            var comType = Type.GetTypeFromProgID("Scripting.Dictionary", true);
            first = Activator.CreateInstance(comType);
            other = Activator.CreateInstance(comType);
            identity = Marshal.GetIUnknownForObject(first);
            wrapper = Marshal.GetUniqueObjectForIUnknown(identity);
            Check(!ReferenceEquals(first, wrapper), "native identity test uses distinct managed COM wrappers");
            Check(DocumentSessionGuard.SameDocument(first, wrapper), "distinct wrappers for the same COM object match");
            Check(!DocumentSessionGuard.SameDocument(first, other), "different live COM objects do not match");
        }
        finally
        {
            if (identity != IntPtr.Zero) Marshal.Release(identity);
            if (wrapper != null) Marshal.ReleaseComObject(wrapper);
            if (other != null) Marshal.ReleaseComObject(other);
            if (first != null) Marshal.ReleaseComObject(first);
        }
    }

    static void BusyStartupTests()
    {
        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "busy.SLDASM"), Available = false, Saved = Root("装配体已有配置") };
        var store = new DraftStore(Path.Combine(output, "busy-store"));
        var editor = new ExportConfigurationForm(workspace, store);
        Check(!Field<bool>(editor, "ready") && workspace.SavedTreeReads == 0,
            "busy startup waits before reading configuration or generating empty tree");
        Check(!Field<Control>(editor, "configurationBody").Enabled && Field<List<Control>>(editor, "swActions").All(c => !c.Enabled),
            "unloaded configuration disables edits and SW actions");
        editor.Close(); editor.Dispose();
        Check(store.Load(workspace.AssemblyPath, out string warning) == null,
            "closing during busy startup cannot overwrite saved assembly config with empty draft");
        editor = new ExportConfigurationForm(workspace, store);
        workspace.Available = true;
        Call(editor, "UpdateDocumentState");
        Check(Field<bool>(editor, "ready") && Field<TextBox>(editor, "linkName").Text == "装配体已有配置",
            "startup automatically loads original configuration when SW becomes available");
        editor.Close(); editor.Dispose();

        workspace.Available = false;
        editor = new ExportConfigurationForm(workspace, store);
        Check(Field<bool>(editor, "ready") && Field<TextBox>(editor, "linkName").Text == "装配体已有配置",
            "existing draft opens while SW is unavailable");
        Field<TextBox>(editor, "linkName").Text = "离线草稿修改";
        editor.Close(); editor.Dispose();
        Check(Deserialize(store.Load(workspace.AssemblyPath, out warning).ConfigurationXml).Name == "离线草稿修改",
            "offline recovered draft remains editable and durable");

        workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "rejected-load.SLDASM"), FailLoad = true, Saved = Root("原始数据") };
        editor = new ExportConfigurationForm(workspace, store);
        Check(!Field<bool>(editor, "ready") && store.Load(workspace.AssemblyPath, out warning) == null,
            "COM failure during config load leaves window alive and writes no draft");
        workspace.FailLoad = false;
        Call(editor, "UpdateDocumentState");
        Check(Field<bool>(editor, "ready") && Field<TextBox>(editor, "linkName").Text == "原始数据",
            "failed config read retries successfully without replacing original data");
        editor.Close(); editor.Dispose();
    }

    static void EditorTests()
    {
        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "window_model.SLDASM") };
        var store = new DraftStore(Path.Combine(output, "editor-store"));
        var editor = new ExportConfigurationForm(workspace, store);
        editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-30000, -30000);
        editor.Show(); Application.DoEvents();
        Check(!editor.Modal, "editor opens modeless");
        Check(TextFits(Field<Label>(editor, "treeHeader")), "tree heading is not clipped at current display scaling");
        Check(Field<List<Label>>(editor, "fieldLabels").All(TextFits), "all field labels fit at current display scaling");
        Field<TextBox>(editor, "linkName").Text = "中文底座";
        Call(editor, "AddChild");
        Field<TextBox>(editor, "linkName").Text = "机械臂_link";
        Field<TextBox>(editor, "jointName").Text = "关节_joint";
        Field<ComboBox>(editor, "coordinates").SelectedItem = "机械臂原点";
        Field<ComboBox>(editor, "axes").SelectedItem = "旋转轴1";
        workspace.Selection.Add(new ComponentPick { Id = new byte[] { 12, 34, 56 }, Name = "电机壳体-1" });
        Call(editor, "AddSwSelection");
        workspace.Selection.Clear();
        workspace.Available = false; Call(editor, "UpdateDocumentState");
        Field<TextBox>(editor, "jointName").Text = "切换模型后仍可填写";
        Check(workspace.SelectionReads == 1, "ordinary SW selection changes never rewrite component assignments");
        Check(Field<List<Control>>(editor, "swActions").All(button => !button.Enabled), "SW actions disabled when bound document is inactive");
        var timer = Field<Timer>(editor, "documentTimer");
        editor.Close(); Application.DoEvents();
        Check(editor.IsDisposed && !timer.Enabled, "closing window disposes timer and preserves draft");
        var recovered = store.Load(workspace.AssemblyPath, out string warning);
        var tree = Deserialize(recovered.ConfigurationXml);
        Check(tree.Name == "中文底座" && tree.Nodes.Count == 1, "window close restores full hierarchy");
        Check(((LinkNode)tree.Nodes[0]).Link.Joint.Name == "切换模型后仍可填写", "last unconfirmed textbox edit saved immediately");
        Check(((LinkNode)tree.Nodes[0]).Link.SWComponentPIDs.Count == 1, "empty host selection at close does not erase stored component");

        workspace.Available = true;
        editor = new ExportConfigurationForm(workspace, store);
        editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-30000, -30000);
        editor.Show(); Application.DoEvents();
        Check(Field<TextBox>(editor, "linkName").Text == "机械臂_link" && Field<TextBox>(editor, "jointName").Text == "切换模型后仍可填写", "reopening automatically restores selected node and fields");
        Check(Field<ListBox>(editor, "componentList").Items[0].ToString() == "电机壳体-1", "component label survives restart");
        var bitmap = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(output, "independent-window.png")); bitmap.Dispose();
        Field<ListBox>(editor, "componentList").SetSelected(0, true);
        Call(editor, "RemoveComponents");
        editor.Close();
        Check(((LinkNode)Deserialize(store.Load(workspace.AssemblyPath, out warning).ConfigurationXml).Nodes[0]).Link.SWComponentPIDs.Count == 0,
            "explicit removal of final component is persisted");

        var r = Root(); var a = Root("a"); var b = Root("b"); r.Nodes.Add(a); a.Nodes.Add(b);
        Check(!ExportConfigurationForm.CanReparent(a, b, r) && !ExportConfigurationForm.CanReparent(r, b, r), "tree drag prevents cycles and moving base link");
        Check(ExportConfigurationForm.CanReparent(b, r, r), "valid reparent operation allowed");

        using (var jointsTree = new TreeView())
        {
            r.Nodes.Remove(a);
            jointsTree.Nodes.Add(a);
            LinkNode snap = AssemblyExportForm.BuildRecoveryTree(r, jointsTree.Nodes, false);
            Check(snap.Nodes.Count == 1 && snap.Nodes[0].Nodes.Count == 1 && jointsTree.Nodes.Count == 1,
                "detail draft includes detached joint subtree without mutating UI tree");
            jointsTree.Nodes.Remove(a); r.Nodes.Add(a);
            snap = AssemblyExportForm.BuildRecoveryTree(r, jointsTree.Nodes, false);
            Check(snap.Nodes.Count == 1 && snap.Nodes[0].Nodes.Count == 1,
                "detail cancel after reattaching joints does not save an empty tree");
        }

        editor = new ExportConfigurationForm(workspace, store);
        editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-30000, -30000);
        editor.Show(); Application.DoEvents();
        Field<TextBox>(editor, "linkName").Text = "Escape_保留";
        Call(editor, "ProcessCmdKey", new Message(), Keys.Escape);
        Check(editor.IsDisposed && ((LinkNode)Deserialize(store.Load(workspace.AssemblyPath, out warning).ConfigurationXml).Nodes[0]).Name == "Escape_保留",
            "Escape key closes modeless window and preserves last edit");

        var large = Root(); for (int i = 0; i < 100; i++) large.Nodes.Add(Root("link_" + i));
        var watch = Stopwatch.StartNew(); store.Save(Draft(workspace.AssemblyPath, large)); watch.Stop();
        Check(watch.ElapsedMilliseconds < 1500, "100-link checkpoint finishes within 1.5 seconds (" + watch.ElapsedMilliseconds + " ms)");
    }

    static void ComponentInteractionTests()
    {
        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "component-interaction.SLDASM"), Saved = Root() };
        var store = new DraftStore(Path.Combine(output, "component-interaction-store"));
        var editor = new ExportConfigurationForm(workspace, store);
        editor.ShowInTaskbar = false; editor.Show(); Application.DoEvents();
        workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { 1, 2, 3 }, Name = "表面拾取零件-1", NeedsComponentHighlight = true });
        ListBox componentList = Field<ListBox>(editor, "componentList");
        Check(Field<CheckBox>(editor, "captureSelection").Checked && workspace.HighlightCalls > 0,
            "opening the editor activates the component target and highlights the current Link like the original plugin");
        Field<CheckBox>(editor, "captureSelection").Checked = false;
        Field<TextBox>(editor, "linkName").Focus(); Application.DoEvents();
        componentList.Focus(); Application.DoEvents();
        Check(Field<CheckBox>(editor, "captureSelection").Checked, "focusing the association list starts continuous pick");
        workspace.RaiseEventOnHighlight = true;
        workspace.RaiseSelectionChanged(); Application.DoEvents();
        Check(!workspace.LastReadWasDirect && componentList.Items.Count == 1,
            "surface-owner selection reaches the association list without being filtered out");
        workspace.SurfaceSelection.Clear();
        workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { 4, 5, 6 }, Name = "表面拾取零件-2", NeedsComponentHighlight = true });
        workspace.RaiseSelectionChanged(); Application.DoEvents();
        Check(componentList.Items.Count == 2, "successive surface picks accumulate component associations");
        Check(workspace.RecentSelectionReads == 2 && workspace.FullSelectionReads == 0,
            "automatic picking reads only the latest interactive component");
        Check(workspace.PickHighlightCalls == 2 && workspace.LastHighlightedPick.Name == "表面拾取零件-2",
            "each surface pick immediately highlights its owner without recursive capture");
        workspace.RaiseSelectionChanged(); Application.DoEvents();
        Check(workspace.PickHighlightCalls == 3 && componentList.Items.Count == 2,
            "repicking an associated surface restores highlight without duplicating the row");
        int beforeListHighlight = workspace.HighlightCalls;
        componentList.SetSelected(0, true);
        typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(componentList, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0) });
        Check(workspace.HighlightCalls > beforeListHighlight && workspace.LastHighlightIndices.SequenceEqual(new[] { 0 }),
            "clicking one association row highlights only that component, including after MouseDown");
        Call(editor, "ComponentListKeyDown", componentList, new KeyEventArgs(Keys.Enter));
        Check(workspace.LastHighlightIndices.SequenceEqual(new[] { 0 }), "Enter preserves the selected-row highlight");
        Check(componentList.ContextMenuStrip != null && componentList.ContextMenuStrip.Items.Count >= 10,
            "association list restores useful right-click commands");
        int beforeClearRestore = workspace.HighlightCalls;
        workspace.RaiseSelectionChanged(AssemblySelectionChange.Cleared);
        System.Threading.Thread.Sleep(190); Application.DoEvents();
        Check(componentList.Items.Count == 2 && workspace.HighlightCalls == beforeClearRestore,
            "global SW selection clearing preserves associations without reselecting or interrupting SW");
        workspace.RaiseSelectionChanged(AssemblySelectionChange.Cleared);
        workspace.RaiseSelectionChanged();
        System.Threading.Thread.Sleep(190); Application.DoEvents();
        Check(componentList.Items.Count == 2, "picking after row highlight and clear keeps associations without duplicates");
        workspace.Selection.Clear(); workspace.SurfaceSelection.Clear();
        int beforeEmptyPick = workspace.HighlightCalls;
        workspace.RaiseSelectionChanged(); Application.DoEvents();
        Check(workspace.HighlightCalls == beforeEmptyPick && componentList.Items.Count == 2,
            "non-component selection does not force old components back into SW selection");
        TableLayoutPanel fieldLayout = Field<TableLayoutPanel>(editor, "fieldLayout");
        int rowIndex = Field<int>(editor, "componentRowIndex");
        Panel grip = Field<Panel>(editor, "componentResizeGrip");
        float heightBefore = fieldLayout.RowStyles[rowIndex].Height;
        Call(editor, "ResizeComponentListStart", grip, new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0));
        Call(editor, "ResizeComponentListBy", 80);
        grip.Capture = false;
        Check(fieldLayout.RowStyles[rowIndex].Height > heightBefore && grip.Cursor == Cursors.HSplit,
            "association list can be enlarged by dragging its lower resize grip");
        int readsBeforeTimer = workspace.SelectionReads;
        Call(editor, "UpdateDocumentState");
        Check(workspace.SelectionReads == readsBeforeTimer, "status timer does not poll SW selection");
        workspace.Available = false; Call(editor, "UpdateDocumentState");
        Check(!Field<CheckBox>(editor, "captureSelection").Checked, "continuous pick stops when SW document becomes unavailable");
        editor.Close(); editor.Dispose();
        Check(workspace.DisposeCalls == 1, "closing editor unsubscribes and disposes the SW workspace once");
        int readsAfterClose = workspace.SelectionReads;
        workspace.RaiseSelectionChanged(); Application.DoEvents();
        Check(workspace.SelectionReads == readsAfterClose, "closed editor no longer receives SW selection events");
    }

    static void LargeLinkAndInvalidCleanupTests()
    {
        var root = Root();
        for (int i = 0; i < 300; i++) root.Link.SWComponentPIDs.Add(BitConverter.GetBytes(i));
        var child = Root("child_link");
        child.IsBaseNode = false;
        child.Link.SWComponentPIDs.Add(new byte[] { 80 });
        child.Link.SWComponentPIDs.Add(new byte[] { 81 });
        root.Nodes.Add(child);
        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "large-link.SLDASM"), Saved = root };
        var store = new DraftStore(Path.Combine(output, "large-link-store"));
        using (var editor = new ExportConfigurationForm(workspace, store))
        {
            editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual;
            editor.Location = new Point(-30000, -30000); editor.Show(); Application.DoEvents();
            int highlights = workspace.HighlightCalls;
            workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { 99 }, Name = "new_pick", NeedsComponentHighlight = true });
            workspace.RaiseSelectionChanged(); Application.DoEvents();
            Check(workspace.RecentSelectionReads == 1 && workspace.FullSelectionReads == 0 &&
                workspace.HighlightCalls == highlights && workspace.PickHighlightCalls == 1 &&
                Field<ListBox>(editor, "componentList").Items.Count == 301,
                "large-Link pick highlights only the new component without scanning or re-highlighting all 300 components");
            workspace.SurfaceSelection[0].NeedsComponentHighlight = false;
            workspace.RaiseSelectionChanged(); Application.DoEvents();
            Check(workspace.PickHighlightCalls == 1 && Field<ListBox>(editor, "componentList").Items.Count == 301,
                "direct component selection is left selected without selecting it a second time");

            workspace.InvalidIds.Add(Convert.ToBase64String(root.Link.SWComponentPIDs[0]));
            workspace.InvalidIds.Add(Convert.ToBase64String(root.Link.SWComponentPIDs[1]));
            workspace.InvalidIds.Add(Convert.ToBase64String(child.Link.SWComponentPIDs[0]));
            Call(editor, "ClearInvalidReferences");
            Check(root.Link.SWComponentPIDs.Count == 299 && child.Link.SWComponentPIDs.Count == 1 &&
                Field<ListBox>(editor, "componentList").Items.Count == 299 &&
                !Field<CheckBox>(editor, "captureSelection").Checked,
                "one cleanup removes invalid references across Links and stops picking");
            LinkNode saved = Deserialize(store.Load(workspace.AssemblyPath, out string warning).ConfigurationXml);
            Check(saved.Link.SWComponentPIDs.Count == 299 && ((LinkNode)saved.Nodes[0]).Link.SWComponentPIDs.Count == 1,
                "bulk reference cleanup is checkpointed immediately");
        }
    }

    static void JointFramePreflightTests()
    {
        double[] zero = { 0, 0, 0 };
        Check(JointFrameConsistency.Check("flange_Link", "flange_y", new[] { 0.0, 0.0, 0.148 }, zero,
            new[] { 0.0, 0.0, 0.2353 }, zero).DistanceMm > 87,
            "changed J6 frame is detected before STL export when old joint pose remains");
        Check(JointFrameConsistency.Check("flange_Link", "flange_y", zero, zero,
            new[] { 0.0, 0.0, 0.001 }, new[] { 0.0, 0.0, 2 * Math.PI }) == null,
            "CAD rounding and equivalent Euler angles do not trigger a false frame warning");
        Check(JointFrameConsistency.Check("flange_Link", "flange_y", zero, zero,
            zero, new[] { 0.0, 0.0, 0.03 }).AngleDeg > 1,
            "rotated coordinate systems also trigger the pre-export check");

        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "j6-frame.SLDASM"), Saved = Root() };
        workspace.FrameMismatches.Add(new JointFrameMismatch { LinkName = "flange_Link", JointName = "flange_y", DistanceMm = 87.3 });
        var store = new DraftStore(Path.Combine(output, "j6-frame-store"));
        using (var editor = new ExportConfigurationForm(workspace, store))
        {
            editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual;
            editor.Location = new Point(-30000, -30000); editor.Show(); Application.DoEvents();
            CheckBox[] options = Field<CheckBox[]>(editor, "compute");
            options[0].Checked = false; options[2].Checked = false;
            string prompt = null;
            typeof(ExportConfigurationForm).GetField("confirmFrameRecalculation", Flags)
                .SetValue(editor, new Func<string, DialogResult>(message => { prompt = message; return DialogResult.Cancel; }));
            Call(editor, "ContinueExport");
            Check(prompt.Contains("flange_y") && prompt.Contains("87.3") && workspace.PrepareExportCalls == 0 &&
                !options[2].Checked && !Field<CheckBox>(editor, "captureSelection").Checked,
                "canceling frame repair stops export and continuous picking before generating meshes");
            typeof(ExportConfigurationForm).GetField("confirmFrameRecalculation", Flags)
                .SetValue(editor, new Func<string, DialogResult>(message => DialogResult.Yes));
            Call(editor, "ContinueExport");
            Check(workspace.PrepareExportCalls == 1 && workspace.LastComputeOptions[0] &&
                workspace.LastComputeOptions[2] && options[0].Checked && options[2].Checked,
                "accepting frame repair enables joint and inertial recomputation before export");
            Field<Form>(editor, "details").Close(); Application.DoEvents();
            Check(store.Load(workspace.AssemblyPath, out string warning).ComputeOptions[2],
                "frame-recalculation choice is preserved in the recovery draft");
            options[0].Checked = false; options[2].Checked = false;
            typeof(ExportConfigurationForm).GetField("confirmFrameRecalculation", Flags)
                .SetValue(editor, new Func<string, DialogResult>(message => DialogResult.No));
            Call(editor, "ContinueExport");
            Check(workspace.PrepareExportCalls == 2 && !workspace.LastComputeOptions[0] &&
                !workspace.LastComputeOptions[2], "intentional manual joint offsets can be retained explicitly");
            Field<Form>(editor, "details").Close(); Application.DoEvents();
            editor.Close();
        }
    }

    static void ExportSelectionIsolationTests()
    {
        var workspace = new FakeWorkspace { AssemblyPath = Path.Combine(output, "export-isolation.SLDASM"), Saved = Root() };
        var store = new DraftStore(Path.Combine(output, "export-isolation-store"));
        using (var editor = new ExportConfigurationForm(workspace, store))
        {
            editor.ShowInTaskbar = false;
            editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-30000, -30000);
            editor.Show(); Application.DoEvents();
            var capture = Field<CheckBox>(editor, "captureSelection");
            var list = Field<ListBox>(editor, "componentList");
            workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { 10 }, Name = "original" });
            workspace.RaiseSelectionChanged(); Application.DoEvents();
            string originalXml = Serialize(Field<LinkNode>(editor, "root"));
            int reads = workspace.SelectionReads, highlights = workspace.HighlightCalls;
            workspace.SurfaceSelection.Clear();
            workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { 20 }, Name = "export-link-A" });
            // Queue a callback but enter export before the message loop gets to it.
            workspace.RaiseSelectionChanged();
            workspace.DuringPrepareExport = () =>
            {
                Check(!capture.Checked && Field<bool>(editor, "exportInProgress") && !editor.Enabled,
                    "Next stops picking before export preparation starts");
                workspace.RaiseSelectionChanged();
                Application.DoEvents();
                Check(workspace.SelectionReads == reads && workspace.HighlightCalls == highlights,
                    "selection notifications and queued callbacks during preparation cannot capture or highlight");
            };
            Call(editor, "ContinueExport");
            Form detail = Field<Form>(editor, "details");
            for (int i = 0; i < 3; i++)
            {
                workspace.SurfaceSelection.Clear();
                workspace.SurfaceSelection.Add(new ComponentPick { Id = new byte[] { (byte)(30 + i) }, Name = "detail-link-" + i });
                workspace.RaiseSelectionChanged(AssemblySelectionChange.Cleared);
                workspace.RaiseSelectionChanged();
                Application.DoEvents();
            }
            Check(workspace.SelectionReads == reads && workspace.HighlightCalls == highlights &&
                Serialize(Field<LinkNode>(editor, "root")) == originalXml,
                "switching several export Links cannot accumulate highlights or contaminate editor associations");
            detail.Close(); Application.DoEvents();
            Check(editor.Enabled && !capture.Checked && !Field<bool>(editor, "exportInProgress"),
                "returning from export restores editor without automatically restarting continuous pick");
            Check(Serialize(Deserialize(store.Load(workspace.AssemblyPath, out string warning).ConfigurationXml)) == originalXml,
                "export navigation cannot be saved into the association draft");

            // A queued pre-export callback must not revive even when picking restarts
            // before that callback gets a chance to run.
            capture.Checked = true;
            workspace.RaiseSelectionChanged();
            workspace.DuringPrepareExport = null;
            Call(editor, "ContinueExport");
            Field<Form>(editor, "details").Close();
            capture.Checked = true;
            Application.DoEvents();
            Check(workspace.SelectionReads == reads && list.Items.Count == 1,
                "old queued pick is invalidated across export and rapid return to picking");
            workspace.RaiseSelectionChanged(); Application.DoEvents();
            Check(list.Items.Count == 2, "fresh user selection still works after returning from export");

            workspace.DuringPrepareExport = () => { throw new InvalidOperationException("simulated preparation failure"); };
            bool failed = false;
            try { Call(editor, "ContinueExport"); }
            catch (TargetInvocationException error) { failed = error.InnerException is InvalidOperationException; }
            Check(failed && editor.Enabled && !capture.Checked && !Field<bool>(editor, "exportInProgress"),
                "failed export preparation restores an editable window with picking stopped");
            Field<TextBox>(editor, "linkName").Text = "still-editable";
            Check(Deserialize(store.Load(workspace.AssemblyPath, out warning).ConfigurationXml).Name == "still-editable",
                "draft checkpoints resume after export preparation failure");
            editor.Close();
        }
    }

    static void AssemblySwitchTests()
    {
        var store = new DraftStore(Path.Combine(output, "switch-store"));
        var first = new FakeWorkspace { AssemblyPath = Path.Combine(output, "first.SLDASM"), Saved = Root("刚关闭的装配体") };
        var firstEditor = new ExportConfigurationForm(first, store);
        firstEditor.Close(); firstEditor.Dispose();
        var second = new FakeWorkspace { AssemblyPath = Path.Combine(output, "legacy-v14.SLDASM"), Saved = Root("1.4版本保存的配置") };
        var secondEditor = new ExportConfigurationForm(second, store);
        Check(secondEditor.IsForAssembly(second.DocumentMarker, second.AssemblyPath) &&
            !secondEditor.IsForAssembly(first.DocumentMarker, first.AssemblyPath), "configuration window binds to the current document instance and full path");
        Check(Field<TextBox>(secondEditor, "linkName").Text == "1.4版本保存的配置",
            "new assembly loads embedded v1.4 configuration instead of previous assembly data");
        Check(!secondEditor.IsForAssembly(new object(), second.AssemblyPath), "reopened file with same path receives a new document-bound window");
        secondEditor.Close(); secondEditor.Dispose();
    }
}
