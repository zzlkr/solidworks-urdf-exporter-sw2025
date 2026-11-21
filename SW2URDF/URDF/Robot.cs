using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Xml;

namespace SW2URDF.URDF
{
    //The base URDF element, a robot
    [DataContract(IsReference = true, Namespace = "http://schemas.datacontract.org/2004/07/SW2URDF")]
    public class Robot : URDFElement
    {
        [DataMember]
        public Link BaseLink { get; private set; }

        [DataMember]
        private readonly URDFAttribute NameAttribute;

        public string Name
        {
            get => (string)NameAttribute.Value;
            set => NameAttribute.Value = value;
        }

        public Robot() : base("robot", true)
        {
            BaseLink = new Link(null);
            NameAttribute = new URDFAttribute("name", true, "");

            ChildElements.Add(BaseLink);
            Attributes.Add(NameAttribute);
        }

        public override void WriteURDF(XmlWriter writer)
        {
            writer.WriteStartDocument();
            string buildVersion = Versioning.Version.GetBuildVersion();
            string commitVersion = Versioning.Version.GetCommitVersion();

            writer.WriteComment(" This URDF was automatically created by SolidWorks to URDF Exporter! " +
                "Originally created by Stephen Brawner (brawner@gmail.com) \r\n" +
                string.Format("     Commit Version: {0}  Build Version: {1}\r\n", commitVersion, buildVersion) +
                "     For more information, please see http://wiki.ros.org/sw_urdf_exporter ");

            base.WriteURDF(writer);
            writer.WriteEndDocument();
            writer.Close();
        }

        public void SetBaseLink(Link link)
        {
            BaseLink = link;
            ChildElements.Clear();
            ChildElements.Add(link);
        }

        // Saved drafts may contain an old joint on the root or joints without
        // endpoints when kinematics computation was turned off. The tree is the
        // authority for connectivity; repair those fields before exporting.
        public string RepairAndValidateStructure()
        {
            if (BaseLink == null) return "根 Link 缺失。";
            var errors = new List<string>();
            var linkNames = new HashSet<string>(System.StringComparer.Ordinal);
            var jointNames = new HashSet<string>(System.StringComparer.Ordinal);
            RepairLink(BaseLink, null, linkNames, jointNames, errors);
            return string.Join("\r\n", errors);
        }

        private static void RepairLink(Link link, Link parent, HashSet<string> linkNames,
            HashSet<string> jointNames, List<string> errors)
        {
            if (link == null) { errors.Add("Link 节点缺失。"); return; }
            if (string.IsNullOrWhiteSpace(link.Name) || !linkNames.Add(link.Name))
                errors.Add("Link 名称为空或重复：" + link.Name);
            link.Parent = parent;
            if (parent == null)
            {
                if (link.Joint != null) link.Joint.Name = "";
            }
            else if (link.Joint == null)
            {
                errors.Add(link.Name + "：缺少 Joint。");
            }
            else
            {
                link.Joint.Parent.Name = parent.Name;
                link.Joint.Child.Name = link.Name;
                if (string.IsNullOrWhiteSpace(link.Joint.Name) || !jointNames.Add(link.Joint.Name))
                    errors.Add(link.Name + "：Joint 名称为空或重复。");
                if (string.IsNullOrWhiteSpace(link.Joint.Type))
                    errors.Add(link.Name + "：Joint 类型为空。");
            }
            foreach (Link child in link.Children)
                RepairLink(child, link, linkNames, jointNames, errors);
        }

        internal string[] GetJointNames(bool includeFixed)
        {
            return BaseLink.GetJointNames(includeFixed);
        }
    }
}
