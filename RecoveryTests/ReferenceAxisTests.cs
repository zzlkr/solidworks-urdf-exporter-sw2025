using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using SW2URDF.URDFExport;

partial class DraftRecoveryTests
{
    // Use the plugin's own embedded COM interfaces. Unexpected calls, including
    // selection changes, fail even when the exporter catches the exception.
    sealed class AxisProxy : RealProxy, IRemotingTypeInfo
    {
        readonly Func<string, object[], object> dispatch;
        readonly Type[] interfaces;
        readonly List<string> unexpected;

        public AxisProxy(List<string> unexpected, Func<string, object[], object> dispatch, params string[] names)
            : base(InteropType(names[0]))
        {
            this.dispatch = dispatch;
            this.unexpected = unexpected;
            interfaces = names.Select(InteropType).ToArray();
        }

        static Type InteropType(string name) =>
            typeof(ExportHelper).Assembly.GetType("SolidWorks.Interop.sldworks." + name, true);

        public string TypeName { get; set; }
        public bool CanCastTo(Type type, object obj) => interfaces.Any(type.IsAssignableFrom);

        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try
            {
                object value = call.MethodBase.DeclaringType == typeof(object) && call.MethodName == "GetType"
                    ? interfaces[0] : dispatch(call.MethodName, call.Args);
                return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
            }
            catch (Exception ex)
            {
                unexpected.Add(call.MethodName + ": " + ex.Message);
                return new ReturnMessage(ex, call);
            }
        }
    }

    static void ReferenceAxisTests()
    {
        var unexpected = new List<string>();
        const string axisName = "J2旋转";
        const string componentPath = "机械臂-1/上臂-2";
        const string instanceConfiguration = "装配中使用的配置";
        const string otherComponentPath = "机械臂-1/上臂-3";
        const string otherConfiguration = "第二配置";
        double[] endpoints = { 1, 2, 3, 1, 5, 3 }; // start minus end is negative Y
        bool suppressed = false;
        string[] suppressionConfigurations = null;
        object MakeFeature(Func<double[]> readEndpoints)
        {
            var axis = new AxisProxy(unexpected, (name, args) =>
            {
                if (name == "GetRefAxisParams") return readEndpoints();
                throw new InvalidOperationException(name);
            }, "RefAxis").GetTransparentProxy();
            return new AxisProxy(unexpected, (name, args) =>
            {
                switch (name)
                {
                    case "GetTypeName2": return "RefAxis";
                    case "IsSuppressed2":
                        suppressionConfigurations = (string[])args[1];
                        return new[] { suppressed };
                    case "GetSpecificFeature2": return axis;
                    default: throw new InvalidOperationException(name);
                }
            }, "Feature").GetTransparentProxy();
        }
        var feature = MakeFeature(() => endpoints);
        var otherFeature = MakeFeature(() => new double[] { 1, 2, 3, 4, 2, 3 }); // negative X
        var transform = new AxisProxy(unexpected, (name, args) =>
        {
            if (name == "get_ArrayData") return new double[] { 0, 1, 0, -1, 0, 0, 0, 0, 1, 7, 8, 9, 1, 0, 0, 0 };
            throw new InvalidOperationException(name);
        }, "MathTransform").GetTransparentProxy();
        var extension = new AxisProxy(unexpected, (name, args) =>
        {
            if (name == "GetCoordinateSystemTransformByName" && (string)args[0] == "J2坐标系") return transform;
            throw new InvalidOperationException(name);
        }, "ModelDocExtension").GetTransparentProxy();
        var configuration = new AxisProxy(unexpected, (name, args) =>
        {
            if (name == "get_Name") return instanceConfiguration;
            throw new InvalidOperationException(name);
        }, "Configuration").GetTransparentProxy();
        var configurations = new AxisProxy(unexpected, (name, args) =>
        {
            if (name == "get_ActiveConfiguration") return configuration;
            throw new InvalidOperationException(name);
        }, "ConfigurationManager").GetTransparentProxy();
        var part = new AxisProxy(unexpected, (name, args) =>
        {
            if (name == "GetType") return 1; // swDocPART
            if (name == "get_ConfigurationManager") return configurations;
            if (name == "FeatureByName") return (string)args[0] == axisName ? feature : null;
            throw new InvalidOperationException(name);
        }, "ModelDoc2", "PartDoc").GetTransparentProxy();
        object MakeComponent(string path, string configurationName, object instanceFeature)
        {
            return new AxisProxy(unexpected, (name, args) =>
            {
                switch (name)
                {
                    case "get_Name2": return path;
                    case "GetModelDoc2": return part;
                    case "get_Transform2": return transform;
                    case "get_ReferencedConfiguration": return configurationName;
                    case "FeatureByName": return (string)args[0] == axisName ? instanceFeature : null;
                    case "IsSuppressed": return false;
                    default: throw new InvalidOperationException(name);
                }
            }, "Component2").GetTransparentProxy();
        }
        var component = MakeComponent(componentPath, instanceConfiguration, feature);
        var otherComponent = MakeComponent(otherComponentPath, otherConfiguration, otherFeature);
        var componentWithoutAxis = MakeComponent("机械臂-1/上臂-4", "第三配置", null);
        var assembly = new AxisProxy(unexpected, (name, args) =>
        {
            switch (name)
            {
                case "GetType": return 2; // swDocASSEMBLY
                case "get_Extension": return extension;
                case "GetComponents": return new[] { component, otherComponent, componentWithoutAxis };
                case "FeatureByName": return (string)args[0] == axisName ? feature : null;
                default: throw new InvalidOperationException(name);
            }
        }, "ModelDoc2", "AssemblyDoc").GetTransparentProxy();

        // Opening a live document and collecting its geometry belong to the constructor.
        // Exercise the actual public read path against the supplied document instead.
        var helper = (ExportHelper)FormatterServices.GetUninitializedObject(typeof(ExportHelper));
        typeof(ExportHelper).GetField("ActiveSWModel").SetValue(helper, assembly);
        var zero = new double[3];
        Check(helper.EstimateAxis(axisName).SequenceEqual(new[] { 0.0, -1, 0 }),
            "top-level named axis retains normalized start-minus-end direction");
        Check(helper.EstimateAxis(axisName + " <" + componentPath + ">").SequenceEqual(new[] { 1.0, 0, 0 }),
            "nested instance axis rotates exactly once and ignores instance translation");
        Check(suppressionConfigurations != null && suppressionConfigurations.SequenceEqual(new[] { instanceConfiguration }),
            "nested axis checks suppression in the component's referenced configuration");
        Check(helper.EstimateAxis(axisName + " <" + otherComponentPath + ">").SequenceEqual(new[] { 0.0, -1, 0 }),
            "second instance uses its own configured axis without changing the shared part's active configuration");
        Check(helper.EstimateAxis(axisName + " <missing-1>").SequenceEqual(zero),
            "missing instance cannot fall back to its same-named top-level axis");
        Check(helper.EstimateAxis(axisName + " <机械臂-1/上臂-4>").SequenceEqual(zero),
            "instance missing its axis cannot borrow a same-named axis from the part document or assembly");
        Check(helper.EstimateAxis("removed-axis").SequenceEqual(zero),
            "a deleted reference axis has no preview direction");
        suppressed = true;
        Check(helper.EstimateAxis(axisName).SequenceEqual(zero), "a suppressed reference axis is unavailable");
        suppressed = false;
        endpoints = new double[6];
        Check(helper.EstimateAxis(axisName).SequenceEqual(zero), "degenerate reference axis cannot produce NaN");
        Check(helper.EstimateAxis("None").SequenceEqual(zero) && helper.EstimateAxis("Automatically Generate").SequenceEqual(zero),
            "None and automatic axis placeholders retain an empty preview");
        var child = Root("upper_arm_Link").Link;
        child.isFixedFrame = false;
        child.Joint.Type = "revolute";
        child.Joint.CoordinateSystemName = "J2坐标系";
        child.Joint.AxisName = "removed-axis";
        typeof(ExportHelper).GetField("ReferenceCoordinateSystemNames", Flags)
            .SetValue(helper, new List<string> { child.Joint.CoordinateSystemName });
        string errorMessage = "";
        try { Call(helper, "CreateJoint", Root().Link, child); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            errorMessage = ex.InnerException.Message;
        }
        Check(child.Joint.AxisName == "removed-axis" && errorMessage.Contains(child.Name) && errorMessage.Contains(child.Joint.AxisName),
            "joint calculation identifies the Link and the unavailable named axis (" + errorMessage + ")");
        Check(unexpected.Count == 0, "reference-axis operations only read geometry: " + string.Join(", ", unexpected));
    }
}
