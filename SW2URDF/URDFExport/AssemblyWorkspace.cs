using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SW2URDF.URDF;
using SW2URDF.UI;
using SW2URDF.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SW2URDF.URDFExport
{
    // Compare document COM identities locally. ISldWorks.IsSame is not needed for this check.
    // The delegates also let regression tests exercise busy/disconnected SW states.
    public sealed class DocumentSessionGuard
    {
        private readonly object document;
        private readonly Func<object> activeDocument;
        private readonly Func<string> currentPath;
        private readonly Func<bool> editingSelf;
        private readonly string originalPath, title;
        private readonly Action<string, Exception> reportError;
        private string previousError;

        public DocumentSessionGuard(object document, Func<object> activeDocument, Func<string> currentPath,
            Func<bool> editingSelf, string originalPath, string title, Action<string, Exception> reportError)
        {
            this.document = document; this.activeDocument = activeDocument; this.currentPath = currentPath;
            this.editingSelf = editingSelf; this.originalPath = originalPath; this.title = title;
            this.reportError = reportError;
        }

        public static bool SameDocument(object first, object second)
        {
            if (first == null || second == null) return false;
            if (ReferenceEquals(first, second)) return true;
            if (!Marshal.IsComObject(first) || !Marshal.IsComObject(second)) return false;
            IntPtr left = IntPtr.Zero, right = IntPtr.Zero;
            try
            {
                left = Marshal.GetIUnknownForObject(first);
                right = Marshal.GetIUnknownForObject(second);
                return left == right;
            }
            finally
            {
                if (right != IntPtr.Zero) Marshal.Release(right);
                if (left != IntPtr.Zero) Marshal.Release(left);
            }
        }

        public bool IsAvailable(out string reason)
        {
            string step = "读取活动文档";
            try
            {
                object active = activeDocument();
                step = "核对装配体身份";
                if (!SameDocument(active, document))
                { reason = "已暂停 SW 操作：请切回 " + title + "（草稿仍可编辑）"; return false; }
                step = "读取装配体路径";
                if (!string.Equals(currentPath(), originalPath, StringComparison.OrdinalIgnoreCase))
                { reason = "装配体路径已改变，请保留草稿后关闭窗口，并从新文件重新打开。"; return false; }
                step = "检查编辑目标 (IsEditingSelf)";
                if (!editingSelf())
                { reason = "正在编辑零件：退出零件编辑后可继续选择和导出；草稿保留。"; return false; }
                previousError = null;
                reason = "已连接：" + title + "  ·  可同时操作 SolidWorks";
                return true;
            }
            catch (Exception error) when (error is COMException || error is InvalidComObjectException)
            {
                string diagnostic = step + "，HRESULT 0x" + error.HResult.ToString("X8");
                if (diagnostic != previousError) reportError?.Invoke(diagnostic, error);
                previousError = diagnostic;
                reason = "SW 接口暂不可用，将自动重试；" + diagnostic;
                return false;
            }
        }
    }

    public sealed class ComponentPick
    {
        public byte[] Id;
        public string Name;
        public bool NeedsComponentHighlight;
    }

    public sealed class InvalidComponentReference
    {
        public LinkNode Node;
        public int Index;
    }

    public sealed class JointFrameMismatch
    {
        public string LinkName;
        public string JointName;
        public double DistanceMm;
        public double AngleDeg;
    }

    public static class JointFrameConsistency
    {
        // Ignore CAD rounding while catching frame edits large enough to visibly
        // separate meshes. Compare rotation matrices so Euler wrap is harmless.
        public static JointFrameMismatch Check(string linkName, string jointName,
            double[] storedPosition, double[] storedRpy, double[] currentPosition, double[] currentRpy)
        {
            if (storedPosition == null || storedRpy == null || currentPosition == null || currentRpy == null ||
                storedPosition.Length != 3 || storedRpy.Length != 3 || currentPosition.Length != 3 || currentRpy.Length != 3 ||
                storedPosition.Concat(storedRpy).Concat(currentPosition).Concat(currentRpy)
                    .Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new InvalidOperationException("关节坐标包含无效数值，无法检查导出位置。");
            double squared = 0;
            for (int i = 0; i < 3; i++) squared += Math.Pow(storedPosition[i] - currentPosition[i], 2);
            double distanceMm = Math.Sqrt(squared) * 1000;
            var rotation = MathOps.GetRotation(storedRpy).Transpose() * MathOps.GetRotation(currentRpy);
            double cosine = Math.Max(-1, Math.Min(1, (rotation[0, 0] + rotation[1, 1] + rotation[2, 2] - 1) / 2));
            double angleDeg = Math.Acos(cosine) * 180 / Math.PI;
            if (distanceMm < 2 && angleDeg < 1) return null;
            return new JointFrameMismatch { LinkName = linkName, JointName = jointName,
                DistanceMm = distanceMm, AngleDeg = angleDeg };
        }
    }

    public enum AssemblySelectionChange
    {
        Added,
        Cleared
    }

    // A small seam for exercising the editor without opening or changing a user's CAD model.
    public interface IAssemblyWorkspace : IDisposable
    {
        event Action<AssemblySelectionChange> SelectionChanged;
        string AssemblyPath { get; }
        string Title { get; }
        bool IsBoundTo(object document, string assemblyPath);
        bool IsAvailable(out string reason);
        LinkNode LoadSavedTree();
        List<string> Coordinates { get; }
        List<string> Axes { get; }
        void RefreshGeometry();
        List<ComponentPick> ReadSelection(bool directComponentsOnly);
        ComponentPick ReadLatestSelection();
        void HighlightPick(ComponentPick pick);
        List<string> ResolveTree(LinkNode root);
        List<InvalidComponentReference> FindInvalidReferences(LinkNode root);
        List<JointFrameMismatch> FindJointFrameMismatches(LinkNode root);
        void Highlight(LinkNode node, int[] componentIndices = null);
        void SaveConfiguration(LinkNode root);
        Form PrepareExport(LinkNode root, bool[] compute);
    }

    public sealed class AssemblyWorkspace : IAssemblyWorkspace
    {
        private readonly SldWorks app;
        private readonly ModelDoc2 model;
        private readonly AssemblyDoc assembly;
        private ExportHelper exporter;
        private readonly DocumentSessionGuard session;
        private bool disposed;
        public event Action<AssemblySelectionChange> SelectionChanged;
        public string AssemblyPath { get; }
        public string Title { get; }
        public List<string> Coordinates => exporter?.GetRefCoordinateSystems() ?? new List<string>();
        public List<string> Axes => exporter?.GetRefAxes() ?? new List<string>();

        public AssemblyWorkspace(SldWorks app, ModelDoc2 model)
        {
            this.app = app;
            this.model = model;
            assembly = (AssemblyDoc)model;
            AssemblyPath = model.GetPathName();
            Title = model.GetTitle();
            if (string.IsNullOrWhiteSpace(AssemblyPath))
                throw new InvalidOperationException("请先保存装配体，再打开 URDF 配置窗口。");
            session = new DocumentSessionGuard(model, () => app.ActiveDoc, () => model.GetPathName(),
                () => model.IsEditingSelf(), AssemblyPath, Title,
                (step, error) => Utilities.Logger.GetLogger().Warn("Assembly status check failed: " + step, error));
            assembly.UserSelectionPostNotify += new DAssemblyDocEvents_UserSelectionPostNotifyEventHandler(OnNewSelection);
            assembly.ClearSelectionsNotify += new DAssemblyDocEvents_ClearSelectionsNotifyEventHandler(OnClearSelections);
        }

        private int OnNewSelection()
        {
            SelectionChanged?.Invoke(AssemblySelectionChange.Added);
            return 0;
        }

        private int OnClearSelections()
        {
            SelectionChanged?.Invoke(AssemblySelectionChange.Cleared);
            return 0;
        }

        public bool IsAvailable(out string reason)
        {
            return session.IsAvailable(out reason);
        }

        public bool IsBoundTo(object document, string assemblyPath)
        {
            return DocumentSessionGuard.SameDocument(model, document) &&
                string.Equals(AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase);
        }

        private void RequireAvailable()
        {
            if (!IsAvailable(out string reason)) throw new InvalidOperationException(reason);
        }

        public LinkNode LoadSavedTree()
        {
            RequireAvailable();
            LinkNode result = ConfigurationSerialization.LoadBaseNodeFromModel(model, out bool error);
            if (error) throw new InvalidDataException("无法读取此装配体的 URDF 配置。");
            return result;
        }

        public void RefreshGeometry()
        {
            RequireAvailable();
            if (exporter == null) exporter = new ExportHelper(app) { URDFRobot = new Robot { Name = Title } };
            else exporter.UpdateReferenceGeometries();
        }

        public List<ComponentPick> ReadSelection(bool directComponentsOnly)
        {
            RequireAvailable();
            SelectionMgr selection = model.SelectionManager;
            var result = new List<ComponentPick>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i <= selection.GetSelectedObjectCount2(-1); i++)
            {
                ComponentPick pick = ReadPick(selection, i, directComponentsOnly);
                if (pick != null && seen.Add(Convert.ToBase64String(pick.Id))) result.Add(pick);
            }
            return result;
        }

        public ComponentPick ReadLatestSelection()
        {
            RequireAvailable();
            SelectionMgr selection = model.SelectionManager;
            int count = selection.GetSelectedObjectCount2(-1);
            return count == 0 ? null : ReadPick(selection, count, false);
        }

        private ComponentPick ReadPick(SelectionMgr selection, int index, bool directComponentsOnly)
        {
            if (directComponentsOnly && selection.GetSelectedObjectType3(index, -1) != (int)swSelectType_e.swSelCOMPONENTS)
                return null;
            Component2 component = selection.GetSelectedObject6(index, -1) as Component2;
            bool needsComponentHighlight = component == null;
            if (component == null) component = selection.GetSelectedObjectsComponent4(index, -1) as Component2;
            if (component == null) return null;
            byte[] id = model.Extension.GetPersistReference3(component);
            if (id == null || id.Length == 0) throw new InvalidOperationException("无法记录所选零件的持久引用。");
            return new ComponentPick { Id = id, Name = component.Name2,
                NeedsComponentHighlight = needsComponentHighlight };
        }

        public void HighlightPick(ComponentPick pick)
        {
            RequireAvailable();
            if (pick == null || !pick.NeedsComponentHighlight) return;
            int error;
            object resolved = model.Extension.GetObjectByPersistReference3(pick.Id, out error);
            if (error != 0 || !(resolved is Component2 component))
                throw new InvalidOperationException("无法高亮刚拾取的零件，请刷新模型引用：" + pick.Name);
            SelectionMgr selection = model.SelectionManager;
            // Promote the picked face/edge to its owning component without clearing
            // Ctrl selections or resolving/reselecting the entire Link.
            if (!component.Select4(true, selection.CreateSelectData(), false))
                throw new InvalidOperationException("无法在 SW 中高亮零件：" + pick.Name);
        }

        public List<string> ResolveTree(LinkNode root)
        {
            RequireAvailable();
            var issues = new List<string>();
            ResolveNode(root, issues);
            return issues;
        }

        private void ResolveNode(LinkNode node, List<string> issues)
        {
            var components = new List<Component2>();
            foreach (byte[] id in node.Link.SWComponentPIDs ?? new List<byte[]>())
            {
                int error = -1;
                object resolved = id == null ? null : model.Extension.GetObjectByPersistReference3(id, out error);
                if (error == 0 && resolved is Component2 component) components.Add(component);
                else issues.Add(node.Name + "：零件引用已失效或被抑制，请重新选择。");
            }
            node.Link.SWComponents = components;
            foreach (LinkNode child in node.Nodes) ResolveNode(child, issues);
        }

        public List<InvalidComponentReference> FindInvalidReferences(LinkNode root)
        {
            RequireAvailable();
            var invalid = new List<InvalidComponentReference>();
            FindInvalidReferences(root, invalid);
            return invalid;
        }

        public List<JointFrameMismatch> FindJointFrameMismatches(LinkNode root)
        {
            EnsureAssemblyReady();
            if (exporter == null) RefreshGeometry();
            var mismatches = new List<JointFrameMismatch>();
            CheckJointFrame(root, mismatches, new Dictionary<string, MathTransform>(StringComparer.Ordinal));
            return mismatches;
        }

        private void CheckJointFrame(LinkNode parent, List<JointFrameMismatch> mismatches,
            Dictionary<string, MathTransform> frames)
        {
            foreach (LinkNode child in parent.Nodes)
            {
                string parentFrame = parent.Link.Joint.CoordinateSystemName;
                string childFrame = child.Link.Joint.CoordinateSystemName;
                if (!string.IsNullOrWhiteSpace(parentFrame) && parentFrame != "Automatically Generate" &&
                    !string.IsNullOrWhiteSpace(childFrame) && childFrame != "Automatically Generate")
                {
                    MathTransform parentTransform = CachedFrame(parentFrame, frames);
                    MathTransform childTransform = CachedFrame(childFrame, frames);
                    if (parentTransform == null || childTransform == null)
                        throw new InvalidOperationException(child.Name + "：无法读取当前坐标系，请刷新模型引用。");
                    var expected = MathOps.GetTransformation(parentTransform).Inverse() *
                        MathOps.GetTransformation(childTransform);
                    JointFrameMismatch mismatch = JointFrameConsistency.Check(child.Name, child.Link.Joint.Name,
                        child.Link.Joint.Origin.GetXYZ(), child.Link.Joint.Origin.GetRPY(),
                        MathOps.GetXYZ(expected), MathOps.GetRPY(expected));
                    if (mismatch != null) mismatches.Add(mismatch);
                }
                CheckJointFrame(child, mismatches, frames);
            }
        }

        private MathTransform CachedFrame(string name, Dictionary<string, MathTransform> frames)
        {
            if (!frames.TryGetValue(name, out MathTransform transform))
            {
                transform = exporter.GetCoordinateSystemTransform(name);
                frames.Add(name, transform);
            }
            return transform;
        }

        private void FindInvalidReferences(LinkNode node, List<InvalidComponentReference> invalid)
        {
            for (int i = 0; i < node.Link.SWComponentPIDs.Count; i++)
            {
                byte[] id = node.Link.SWComponentPIDs[i];
                int state = -1;
                object resolved = id == null ? null : model.Extension.GetObjectByPersistReference3(id, out state);
                // A suppressed component may return no object today but become valid again.
                if (state >= 0 && (state & (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Suppressed) != 0)
                    continue;
                if (state != (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok || !(resolved is Component2))
                    invalid.Add(new InvalidComponentReference { Node = node, Index = i });
                else
                {
                    Component2 component = (Component2)resolved;
                    // A lightweight component can have no ModelDoc2 while its file is
                    // healthy. Require both an unloaded model and a missing external
                    // path before treating an otherwise valid reference as stale.
                    string path = component.GetPathName();
                    if (!component.IsVirtual && !string.IsNullOrWhiteSpace(path) &&
                        System.IO.Path.IsPathRooted(path) && !System.IO.File.Exists(path) &&
                        component.GetModelDoc2() == null)
                        invalid.Add(new InvalidComponentReference { Node = node, Index = i });
                }
            }
            foreach (LinkNode child in node.Nodes) FindInvalidReferences(child, invalid);
        }

        public void Highlight(LinkNode node, int[] componentIndices = null)
        {
            RequireAvailable();
            // List indices refer to persistent IDs. A missing component must not shift
            // those indices and cause a different row's component to be highlighted.
            var selectedComponents = new List<Component2>();
            IEnumerable<int> indices = componentIndices ?? Enumerable.Range(0, node.Link.SWComponentPIDs.Count).ToArray();
            foreach (int index in indices.Distinct())
            {
                if (index < 0 || index >= node.Link.SWComponentPIDs.Count) continue;
                int error = -1;
                byte[] id = node.Link.SWComponentPIDs[index];
                object resolved = id == null ? null : model.Extension.GetObjectByPersistReference3(id, out error);
                if (error != 0 || !(resolved is Component2 component)) continue;
                selectedComponents.Add(component);
            }
            model.ClearSelection2(true);
            SelectionMgr selection = model.SelectionManager;
            SelectData data = selection.CreateSelectData();
            foreach (Component2 component in selectedComponents)
                if (!component.Select4(true, data, false))
                    throw new InvalidOperationException("无法在 SW 中高亮零件：" + component.Name2);
            if (selection.GetSelectedObjectCount2(-1) < selectedComponents.Count)
                throw new InvalidOperationException("SW 未能选中全部关联零件，请刷新模型引用后重试。");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                assembly.UserSelectionPostNotify -= new DAssemblyDocEvents_UserSelectionPostNotifyEventHandler(OnNewSelection);
                assembly.ClearSelectionsNotify -= new DAssemblyDocEvents_ClearSelectionsNotifyEventHandler(OnClearSelections);
            }
            catch (Exception error) when (error is COMException || error is InvalidComObjectException)
            {
                Utilities.Logger.GetLogger().Warn("Could not detach assembly selection listener", error);
            }
            SelectionChanged = null;
        }

        public void SaveConfiguration(LinkNode root)
        {
            RequireAvailable();
            // IDs already belong to this assembly. Never rebuild them from a cleared SW selection.
            ConfigurationSerialization.SaveConfigTreeXML(app, model, root, false);
        }

        public Form PrepareExport(LinkNode root, bool[] compute)
        {
            EnsureAssemblyReady();
            RefreshGeometry();
            var issues = ResolveTree(root);
            issues.AddRange(ConfigurationValidation.Validate(root, Coordinates, Axes));
            if (issues.Count > 0) throw new InvalidOperationException(string.Join("\r\n", issues.Distinct()));
            SaveConfiguration(root);
            exporter.SetComputeInertial(compute[0]);
            exporter.SetComputeVisualCollision(compute[1]);
            exporter.SetComputeJointKinematics(compute[2]);
            exporter.SetComputeJointLimits(compute[3]);
            if (!exporter.CreateRobotFromTreeView(root))
                throw new InvalidOperationException("计算 URDF 数据未完成，草稿仍保留，请检查模型。 ");
            return new AssemblyExportForm(app, root, exporter);
        }

        private void EnsureAssemblyReady()
        {
            RequireAvailable();
            if (model.Extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
            {
                if (!model.EditRebuild3() || model.Extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
                    throw new InvalidOperationException("模型重建未完成，请在 SW 中解决重建问题后再导出。草稿仍保留。");
            }
            int resolve = ((AssemblyDoc)model).ResolveAllLightWeightComponents(true);
            if (resolve != (int)swComponentResolveStatus_e.swResolveOk)
                throw new InvalidOperationException("请先将轻化零件设为还原状态，再继续导出。");
        }
    }

    public static class ConfigurationValidation
    {
        public static List<string> Validate(LinkNode root, List<string> coordinates, List<string> axes)
        {
            var errors = new List<string>();
            Check(root, new HashSet<string>(), new HashSet<string>(), coordinates, axes, errors);
            return errors;
        }

        private static void Check(LinkNode node, HashSet<string> links, HashSet<string> joints,
            List<string> coords, List<string> axes, List<string> errors)
        {
            string name = node.Link.Name;
            if (string.IsNullOrWhiteSpace(name) || !links.Add(name)) errors.Add("Link 名称为空或重复：" + name);
            if (!node.IsBaseNode && (string.IsNullOrWhiteSpace(node.Link.Joint.Name) || !joints.Add(node.Link.Joint.Name)))
                errors.Add(name + "：Joint 名称为空或重复。");
            if (node.Link.SWComponents.Count != node.Link.SWComponentPIDs.Count)
                errors.Add(name + "：存在失效的零件引用。");
            if (node.Nodes.Count > 0 && node.Link.SWComponents.Count == 0)
                errors.Add(name + "：有子 link 的节点必须关联零件。");
            string coord = node.Link.Joint.CoordinateSystemName;
            if (string.IsNullOrEmpty(coord) || coord == "Automatically Generate")
            {
                if (node.Link.SWComponents.Count == 0) errors.Add(name + "：请选择坐标系或关联零件。");
            }
            else if (!coords.Contains(coord)) errors.Add(name + "：坐标系已失效：" + coord);
            if (!node.IsBaseNode)
            {
                string axis = node.Link.Joint.AxisName;
                if (!string.IsNullOrEmpty(axis) && axis != "None" && axis != "Automatically Generate" && !axes.Contains(axis))
                    errors.Add(name + "：轴引用已失效：" + axis);
                if (node.Link.SWComponents.Count == 0 && (axis == "Automatically Generate" ||
                    node.Link.Joint.Type == "Automatically Detect")) errors.Add(name + "：空 link 不能自动计算轴或关节类型。");
            }
            foreach (Component2 component in node.Link.SWComponents)
                if (component.GetModelDoc2() == null) errors.Add(name + "：零件尚未完全加载。");
            foreach (LinkNode child in node.Nodes) Check(child, links, joints, coords, axes, errors);
        }
    }
}
