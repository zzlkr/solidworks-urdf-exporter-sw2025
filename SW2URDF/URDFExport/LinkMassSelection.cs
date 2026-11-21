using System;
using System.Collections.Generic;
using System.Linq;
using SW2URDF.URDF;

namespace SW2URDF.URDFExport
{
    // Component2.Name2 is an instance path, including its parent subassemblies.
    internal static class LinkMassSelection
    {
        private static IEnumerable<string> Ancestors(string path)
        {
            for (int end = path.LastIndexOf('/'); end >= 0; end = path.LastIndexOf('/'))
            {
                path = path.Substring(0, end);
                yield return path;
            }
        }

        internal static string[] WholeComponents(IEnumerable<string> paths)
        {
            var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = new HashSet<string>(unique, StringComparer.OrdinalIgnoreCase);
            return unique.Where(path => !Ancestors(path).Any(selected.Contains)).ToArray();
        }

        internal static string FindConflict(IEnumerable<KeyValuePair<string, string>> assignments)
        {
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in assignments)
            {
                if (owner.TryGetValue(entry.Key, out string link) && link != entry.Value)
                    return Conflict(entry.Key, link, entry.Value);
                owner[entry.Key] = entry.Value;
            }

            foreach (var entry in owner)
                foreach (string parent in Ancestors(entry.Key))
                    if (owner.TryGetValue(parent, out string link) && link != entry.Value)
                        return Conflict(entry.Key, link, entry.Value);
            return null;
        }

        private static string Conflict(string path, string first, string second) =>
            $"组件“{path}”在 Link“{first}”和“{second}”中重复计入。请将整个子装配体归到一个 Link，" +
            "或移除整组关联、仅分配各个子零件。整组覆盖质量不能自动拆分到多个 Link。";

        internal static void Apply(Link link, double mass, double[] center, double[] moment)
        {
            // Validate all results before touching the Link, so a failed COM read
            // cannot mix new mass with old center of mass or inertia.
            if (!IsFinite(mass) || mass < 0 || center == null || center.Length != 3 ||
                moment == null || moment.Length != 9 || !center.All(IsFinite) || !moment.All(IsFinite))
                throw new InvalidOperationException($"SW 返回了不完整或无效的质量属性：Link“{link.Name}”。");

            link.Inertial.Mass.Value = mass;
            link.Inertial.Origin.SetXYZ(center);
            link.Inertial.Origin.SetRPY(new double[3]);
            // Convert SW products of inertia to URDF tensor entries using the existing sign convention.
            link.Inertial.Inertia.SetMomentMatrix(moment);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
