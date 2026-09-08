using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.URDFExport;
using Xunit;

namespace SW2URDF.Test
{
    [Collection("Requires SW Test Collection")]
    public class TestSerialization : SW2URDFTest
    {
        public TestSerialization(SWTestFixture fixture) : base(fixture)
        {
        }

        [Theory]
        [InlineData("3_DOF_ARM", 4)]
        public void TestLoadConfigFromStringXML(string modelName, int expNumLinks)
        {
            ModelDoc2 doc = OpenSWDocument(modelName);
            PrivateType serialization = new PrivateType(typeof(ConfigurationSerialization));
            object swAttObj = serialization.InvokeStatic(
                "FindSWSaveAttribute", new object[] { doc, "URDF Export Configuration" });
            Xunit.Assert.NotNull(swAttObj);

            Attribute swAtt = (Attribute)swAttObj;
            Parameter param = swAtt.GetParameter("data");

            Xunit.Assert.NotNull(param);
            string data = param.GetStringValue();

            Xunit.Assert.NotNull(data);
            Xunit.Assert.NotEmpty(data);

            LinkNode baseNode = (LinkNode)serialization.InvokeStatic(
                "LoadConfigFromStringXML", new object[] { data });
            Link link = baseNode.RebuildLink();
            Xunit.Assert.Equal(expNumLinks, CommonSwOperations.GetCount(link));
        }

        [Theory]
        [InlineData("3_DOF_ARM", 4)]
        [InlineData("4_WHEELER", 5)]
        public void TestLoadBaseNodeFromModel(string modelName, int expNumLinks)
        {
            ModelDoc2 doc = OpenSWDocument(modelName);
            LinkNode baseNode = ConfigurationSerialization.LoadBaseNodeFromModel(doc, out bool error);
            Xunit.Assert.False(error);
            Xunit.Assert.NotNull(baseNode);
            Xunit.Assert.Equal(expNumLinks, CommonSwOperations.GetCount(baseNode.RebuildLink()));
        }

        [Theory]
        [InlineData("3_DOF_ARM")]
        [InlineData("4_WHEELER")]
        public void TestSerializeToString(string modelName)
        {
            ModelDoc2 doc = OpenSWDocument(modelName);
            LinkNode baseNode = ConfigurationSerialization.LoadBaseNodeFromModel(doc, out bool error);
            Xunit.Assert.False(error);

            PrivateType serialization = new PrivateType(typeof(ConfigurationSerialization));
            string newData = (string)serialization.InvokeStatic(
                "SerializeToString", new object[] { baseNode });
            Xunit.Assert.NotNull(newData);
            Xunit.Assert.NotEmpty(newData);

        }

        [Fact]
        public void TestSerializeToStringPreservesUnicodeGeometryNames()
        {
            LinkNode baseNode = new LinkNode
            {
                Name = "base_link",
                Text = "base_link",
                IsBaseNode = true
            };
            baseNode.Link.Name = "base_link";
            baseNode.Link.Joint.CoordinateSystemName = "坐标系1";

            LinkNode childNode = new LinkNode
            {
                Name = "left_arm_link",
                Text = "left_arm_link"
            };
            childNode.Link.Name = "left_arm_link";
            childNode.Link.Joint.Name = "Joint_左臂";
            childNode.Link.Joint.Type = "revolute";
            childNode.Link.Joint.CoordinateSystemName = "双臂机器人头部装配体<1>/坐标系1";
            childNode.Link.Joint.AxisName = "轴1";
            baseNode.Nodes.Add(childNode);

            PrivateType serialization = new PrivateType(typeof(ConfigurationSerialization));
            string data = (string)serialization.InvokeStatic(
                "SerializeToString", new object[] { baseNode });
            Xunit.Assert.Contains("双臂机器人头部装配体", data);
            Xunit.Assert.Contains("坐标系1", data);
            Xunit.Assert.DoesNotContain("????", data);

            LinkNode reloaded = (LinkNode)serialization.InvokeStatic(
                "DeserializeFromString", new object[] { data });
            LinkNode reloadedChild = (LinkNode)reloaded.Nodes[0];
            Xunit.Assert.Equal("双臂机器人头部装配体<1>/坐标系1",
                reloadedChild.Link.Joint.CoordinateSystemName);
            Xunit.Assert.Equal("轴1", reloadedChild.Link.Joint.AxisName);
        }
    }
}
