using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Xml;
using GRAccessTools.Common;

namespace GRAccessTools.Evaluate
{
    // A graphic that references the instance, across its checked-in and checked-out versions
    class GraphicHit
    {
        public string GraphicId;            // "objectId|primitiveId" for graphics, "obj|objectId" for a ViewApp template
        public string DefinitionGraphicId;  // the graphic its definition is inherited from (its own id when not inherited)
        public int ObjectId;
        public readonly SortedSet<string> Reasons = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> NamesInDefinition = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool InCheckedIn;
        public bool InCheckedOut;
    }

    // Where a graphic is shown: a ViewApp, or a graphic that nothing else embeds, and the graphics in between
    class ShownIn
    {
        public string GraphicId;
        public bool IsViewApp;
        public string From;                                // the graphic that was hit that the path starts at
        public List<string> Through = new List<string>();  // graphic ids from that graphic up to this one, both excluded
    }

    // Finds the graphics that reference an instance or the objects it contains. Every statement is a SELECT.
    class GraphicSearch
    {
        const int Timeout = 600;
        const string GalaxyPrefix = "galaxy:";

        readonly GalaxyGraphics graphics;
        readonly GalaxyObject instance;
        readonly List<GalaxyObject> targets = new List<GalaxyObject>();  // the instance and every object it contains
        readonly HashSet<int> targetIds = new HashSet<int>();
        readonly List<GalaxyObject> containers = new List<GalaxyObject>();  // the objects that contain the instance, innermost first

        public readonly Dictionary<string, GraphicHit> Hits = new Dictionary<string, GraphicHit>();
        public int DefinitionsSearched;

        public GraphicSearch(GalaxyGraphics graphics, GalaxyObject instance)
        {
            this.graphics = graphics;
            this.instance = instance;

            targets.Add(instance);
            for (int i = 0; i < targets.Count; i++)
            {
                foreach (GalaxyObject obj in graphics.Objects.Values)
                {
                    if (obj.ContainedBy == targets[i].Id && obj.Namespace == GalaxyObject.AutomationNamespace && !targetIds.Contains(obj.Id))
                        targets.Add(obj);
                }
                targetIds.Add(targets[i].Id);
            }

            GalaxyObject container;
            for (GalaxyObject obj = instance; obj.ContainedBy != 0 && graphics.Objects.TryGetValue(obj.ContainedBy, out container); obj = container)
                containers.Add(container);
        }

        public IList<GalaxyObject> Targets
        {
            get { return targets; }
        }

        // Graphics and ViewApp templates that embed a symbol owned by a target, or a symbol name that names one
        public void FindEmbeddedSymbols()
        {
            foreach (EmbeddedSymbol embed in graphics.Embeds)
            {
                if (!graphics.IsCurrent(embed.ReferrerObjectId, embed.ReferrerPackageId))
                    continue;

                string symbol = TargetSymbol(embed.CheckedInBoundKey) ?? TargetSymbol(embed.CheckedOutBoundKey);
                if (symbol != null)
                {
                    AddHit(embed.ReferrerObjectId, embed.ReferrerPackageId, embed.ReferrerPrimitiveId,
                        "embeds " + symbol + (embed.Relative ? " (relative reference)" : ""), null);
                    continue;
                }
                foreach (string name in embed.UnboundNames)
                {
                    if (NamesTarget(name))
                    {
                        AddHit(embed.ReferrerObjectId, embed.ReferrerPackageId, embed.ReferrerPrimitiveId,
                            "embeds " + name + ", which is not bound to a symbol", null);
                        break;
                    }
                }
            }
        }

        // Tag references in animations and scripts that the galaxy resolved to a target (attribute_reference)
        public void FindTagReferences(SqlConnection connection)
        {
            string sql =
                "SELECT gobject_id, package_id, referring_mx_primitive_id, reference_string FROM dbo.attribute_reference " +
                "WHERE resolved_gobject_id IN (" + string.Join(",", new List<int>(targetIds).ConvertAll(id => id.ToString()).ToArray()) + ")";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    int objectId = reader.GetInt32(0), packageId = reader.GetInt32(1), primitiveId = Convert.ToInt32(reader.GetValue(2));
                    if (graphics.Versions.ContainsKey(GraphicVersion.VersionKey(objectId, packageId, primitiveId)) && graphics.IsCurrent(objectId, packageId))
                        AddHit(objectId, packageId, primitiveId, "references " + reader.GetString(3), null);
                }
            }
        }

        // References listed in each graphic's cross-reference XML, including the ones the galaxy could not resolve, and
        // relative references (Me.<contained name>) in symbols of the objects that contain the instance
        public void FindCrossReferences(SqlConnection connection)
        {
            const string sql =
                "SELECT gobject_id, package_id, mx_primitive_id, visual_element_crossRef FROM dbo.owned_visual_element " +
                "WHERE visual_element_crossRef IS NOT NULL";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string definitionKey = GraphicVersion.VersionKey(reader.GetInt32(0), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2)));
                    foreach (string reference in ReferenceNames(reader.GetString(3)))
                    {
                        string name = reference.StartsWith(GalaxyPrefix, StringComparison.OrdinalIgnoreCase) ? reference.Substring(GalaxyPrefix.Length) : reference;
                        if (NamesTarget(name))
                        {
                            foreach (GraphicVersion version in graphics.CurrentInheritors(definitionKey))
                                AddHit(version.ObjectId, version.PackageId, version.PrimitiveId, "references " + reference, null);
                            continue;
                        }

                        HashSet<int> owners = ContainersNamingTarget(name);
                        if (owners.Count == 0)
                            continue;
                        foreach (GraphicVersion version in graphics.CurrentInheritors(definitionKey))
                        {
                            if (owners.Contains(version.ObjectId))
                                AddHit(version.ObjectId, version.PackageId, version.PrimitiveId, "references " + reference + " (relative reference)", null);
                        }
                    }
                }
            }
        }

        // The tagname or hierarchical name of a target written anywhere in a graphic definition, e.g. in a script or a
        // custom property value. Names are matched whole and without case.
        public void FindNamesInDefinitions(SqlConnection connection)
        {
            List<string> names = new List<string>();
            foreach (GalaxyObject target in targets)
            {
                names.Add(target.Tagname);
                names.Add(target.HierarchicalName);
            }
            NameFinder finder = new NameFinder(names);

            const string sql =
                "SELECT gobject_id, package_id, mx_primitive_id, visual_element_definition FROM dbo.owned_visual_element " +
                "WHERE visual_element_definition IS NOT NULL";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
            {
                while (reader.Read())
                {
                    string definitionKey = GraphicVersion.VersionKey(reader.GetInt32(0), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2)));
                    byte[] definition = (byte[])reader.GetValue(3);
                    DefinitionsSearched++;
                    foreach (string name in finder.NamesIn(definition))
                    {
                        foreach (GraphicVersion version in graphics.CurrentInheritors(definitionKey))
                            AddHit(version.ObjectId, version.PackageId, version.PrimitiveId, null, name);
                    }
                }
            }
        }

        // The ViewApps that show a graphic (through any graphics and layouts that embed it), and the graphics above it
        // that nothing else embeds. Found breadth first, so the path to each is the shortest one.
        public List<ShownIn> WhereShown(IEnumerable<string> graphicIds)
        {
            Dictionary<int, List<string>> embeddedBy = EmbeddedByElement();
            Dictionary<string, string> previous = new Dictionary<string, string>();
            Queue<string> queue = new Queue<string>();
            HashSet<string> starts = new HashSet<string>(graphicIds);
            foreach (string id in starts)
            {
                previous[id] = null;
                queue.Enqueue(id);
            }

            List<ShownIn> results = new List<ShownIn>();
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                if (id.StartsWith("obj|", StringComparison.Ordinal))
                {
                    GalaxyObject viewApp = graphics.Objects[int.Parse(id.Substring(4))];
                    bool hasInstances = false;
                    foreach (GalaxyObject obj in graphics.Objects.Values)
                    {
                        if (obj.DerivedFrom == viewApp.Id && !obj.IsTemplate)
                        {
                            results.Add(NewShownIn("obj|" + obj.Id, true, id, previous));
                            hasInstances = true;
                        }
                    }
                    if (!hasInstances)
                        results.Add(NewShownIn(id, true, id, previous));
                    continue;
                }

                bool embedded = false;
                foreach (string parent in Parents(id, embeddedBy))
                {
                    embedded = true;
                    if (previous.ContainsKey(parent))
                        continue;
                    previous[parent] = id;
                    queue.Enqueue(parent);
                }
                if (!embedded && !starts.Contains(id))
                    results.Add(NewShownIn(id, false, id, previous));
            }
            return results;
        }

        public string NameOf(string graphicId)
        {
            if (graphicId.StartsWith("obj|", StringComparison.Ordinal))
                return graphics.TagnameOf(int.Parse(graphicId.Substring(4)));

            GraphicVersion version = AnyVersion(graphicId);
            string owner = graphics.TagnameOf(version.ObjectId);
            return version.PrimitiveName.Length == 0 ? owner : owner + "." + version.PrimitiveName;
        }

        public string KindOf(string graphicId)
        {
            if (graphicId.StartsWith("obj|", StringComparison.Ordinal))
            {
                GalaxyObject obj = graphics.Objects[int.Parse(graphicId.Substring(4))];
                string kind = obj.Category == GalaxyObject.OmiViewAppCategory ? "OMI ViewApp"
                    : obj.Category == GalaxyObject.InTouchViewAppCategory ? "InTouch ViewApp" : "object";
                return obj.IsTemplate ? kind + " template" : kind;
            }

            GraphicVersion version = AnyVersion(graphicId);
            GalaxyObject owner = graphics.Objects[version.ObjectId];
            if (owner.Namespace != GalaxyObject.GraphicNamespace)
                return "symbol of " + (owner.IsTemplate ? "template " : "instance ") + owner.Tagname;
            switch (version.Type)
            {
                case "Symbol": return "Graphic Toolbox symbol";
                case "Layout": return "OMI layout";
                case "ScreenProfile": return "OMI screen profile";
                case "DisplayModule": return "OMI display module";
                case "ClientControl": return "client control";
                case "Widget": return "widget";
                default: return version.Type;
            }
        }

        public GalaxyObject OwnerOf(string graphicId)
        {
            int objectId = graphicId.StartsWith("obj|", StringComparison.Ordinal) ? int.Parse(graphicId.Substring(4)) : AnyVersion(graphicId).ObjectId;
            return graphics.Objects[objectId];
        }

        // reachedFrom is the node the search got to: the result itself, or the ViewApp template of a ViewApp instance
        static ShownIn NewShownIn(string graphicId, bool isViewApp, string reachedFrom, Dictionary<string, string> previous)
        {
            ShownIn shown = new ShownIn();
            shown.GraphicId = graphicId;
            shown.IsViewApp = isViewApp;
            // Walk back to the graphic that was hit, keeping the graphics in between (a ViewApp template is left out)
            shown.From = reachedFrom;
            for (string id = previous[reachedFrom]; id != null; id = previous[id])
            {
                if (previous[id] == null)
                {
                    shown.From = id;
                    break;
                }
                shown.Through.Add(id);
            }
            shown.Through.Reverse();
            return shown;
        }

        // Graphic ids of the graphics and ViewApp templates that embed any current version of this graphic
        IEnumerable<string> Parents(string graphicId, Dictionary<int, List<string>> embeddedBy)
        {
            HashSet<string> parents = new HashSet<string>();
            foreach (GraphicVersion version in graphics.VersionsByGraphic[graphicId])
            {
                if (!graphics.IsCurrent(version.ObjectId, version.PackageId))
                    continue;
                List<string> referrers;
                if (!embeddedBy.TryGetValue(version.VisualElementId, out referrers))
                    continue;
                foreach (string referrer in referrers)
                {
                    if (referrer != graphicId && parents.Add(referrer))
                        yield return referrer;
                }
            }
        }

        // Visual element id -> graphic ids of the current graphics and ViewApp templates that embed it
        Dictionary<int, List<string>> EmbeddedByElement()
        {
            Dictionary<int, List<string>> embeddedBy = new Dictionary<int, List<string>>();
            foreach (EmbeddedSymbol embed in graphics.Embeds)
            {
                if (!graphics.IsCurrent(embed.ReferrerObjectId, embed.ReferrerPackageId))
                    continue;
                string referrer = GraphicIdOf(embed.ReferrerObjectId, embed.ReferrerPackageId, embed.ReferrerPrimitiveId);
                foreach (string boundKey in new[] { embed.CheckedInBoundKey, embed.CheckedOutBoundKey })
                {
                    GraphicVersion bound;
                    if (boundKey == null || !graphics.Versions.TryGetValue(boundKey, out bound))
                        continue;
                    List<string> referrers;
                    if (!embeddedBy.TryGetValue(bound.VisualElementId, out referrers))
                    {
                        referrers = new List<string>();
                        embeddedBy.Add(bound.VisualElementId, referrers);
                    }
                    if (!referrers.Contains(referrer))
                        referrers.Add(referrer);
                }
            }
            return embeddedBy;
        }

        void AddHit(int objectId, int packageId, int primitiveId, string reason, string nameInDefinition)
        {
            GalaxyObject owner;
            if (targetIds.Contains(objectId) || !graphics.Objects.TryGetValue(objectId, out owner))
                return;  // the symbols of the instance and its contained objects are not other graphics

            string graphicId = GraphicIdOf(objectId, packageId, primitiveId);
            GraphicHit hit;
            if (!Hits.TryGetValue(graphicId, out hit))
            {
                hit = new GraphicHit();
                hit.GraphicId = graphicId;
                hit.ObjectId = objectId;
                GraphicVersion version, definition;
                hit.DefinitionGraphicId = graphics.Versions.TryGetValue(GraphicVersion.VersionKey(objectId, packageId, primitiveId), out version)
                    && graphics.Versions.TryGetValue(version.InheritedKey, out definition) ? definition.GraphicId : graphicId;
                Hits.Add(graphicId, hit);
            }
            if (reason != null)
                hit.Reasons.Add(reason);
            if (nameInDefinition != null)
                hit.NamesInDefinition.Add(nameInDefinition);
            if (packageId == owner.CheckedInPackage)
                hit.InCheckedIn = true;
            else
                hit.InCheckedOut = true;
        }

        string GraphicIdOf(int objectId, int packageId, int primitiveId)
        {
            GraphicVersion version;
            return graphics.Versions.TryGetValue(GraphicVersion.VersionKey(objectId, packageId, primitiveId), out version) ? version.GraphicId : "obj|" + objectId;
        }

        GraphicVersion AnyVersion(string graphicId)
        {
            return graphics.VersionsByGraphic[graphicId][0];
        }

        // The full name of a symbol owned by a target (e.g. Pump_001.Faceplate), or null
        string TargetSymbol(string versionKey)
        {
            GraphicVersion version;
            if (versionKey == null || !graphics.Versions.TryGetValue(versionKey, out version) || !targetIds.Contains(version.ObjectId))
                return null;
            return graphics.TagnameOf(version.ObjectId) + "." + version.PrimitiveName;
        }

        // Whether a reference or symbol name is a target's tagname or hierarchical name, or starts with one followed by a dot
        bool NamesTarget(string name)
        {
            foreach (GalaxyObject target in targets)
            {
                if (StartsWithName(name, target.Tagname) || StartsWithName(name, target.HierarchicalName))
                    return true;
            }
            return false;
        }

        // The containers of the instance whose symbols would reach a target with this relative reference (Me.<contained name>...)
        HashSet<int> ContainersNamingTarget(string reference)
        {
            HashSet<int> owners = new HashSet<int>();
            if (containers.Count == 0 || !reference.StartsWith("Me.", StringComparison.OrdinalIgnoreCase))
                return owners;

            string relativeName = reference.Substring(3);
            foreach (GalaxyObject container in containers)
            {
                string prefix = container.HierarchicalName + ".";
                foreach (GalaxyObject target in targets)
                {
                    if (target.HierarchicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && StartsWithName(relativeName, target.HierarchicalName.Substring(prefix.Length)))
                        owners.Add(container.Id);
                }
            }
            return owners;
        }

        static bool StartsWithName(string reference, string name)
        {
            return reference.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && (reference.Length == name.Length || reference[name.Length] == '.');
        }

        static List<string> ReferenceNames(string xml)
        {
            List<string> names = new List<string>();
            try
            {
                using (XmlReader reader = XmlReader.Create(new StringReader(xml)))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Reference")
                            continue;
                        string name = reader.GetAttribute("Name");
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
                }
            }
            catch (XmlException)
            {
                // A damaged cross-reference list is skipped; the definition search still covers that graphic
            }
            return names;
        }

        // Finds object names in binary graphic definitions, where text is stored as ASCII or UTF-16. Each definition is read
        // once: runs of name characters (letters, digits, _ and .) are split at the dots, and every sequence of whole
        // parts is looked up, so ECCP_CH1_X is found in "ECCP_CH1_X.PV" but not inside ECCP_CH10_X.
        class NameFinder
        {
            // { character width, first offset }: ASCII, then UTF-16 little endian at even and at odd offsets
            static readonly int[][] Passes = { new[] { 1, 0 }, new[] { 2, 0 }, new[] { 2, 1 } };

            readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly bool[] nameBytes = new bool[256];
            readonly int maxParts;
            readonly int minLength = int.MaxValue;

            public NameFinder(IEnumerable<string> searched)
            {
                foreach (char c in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.")
                    nameBytes[c] = true;
                foreach (string name in searched)
                {
                    names.Add(name);
                    maxParts = Math.Max(maxParts, name.Split('.').Length);
                    minLength = Math.Min(minLength, name.Length);
                    foreach (char c in name)
                    {
                        if (c > ' ' && c < 128)
                            nameBytes[c] = true;  // other characters used in these names, e.g. -
                    }
                }
            }

            public SortedSet<string> NamesIn(byte[] data)
            {
                SortedSet<string> found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                StringBuilder run = new StringBuilder();
                foreach (int[] pass in Passes)
                {
                    int width = pass[0];
                    run.Length = 0;
                    for (int i = pass[1]; i + width <= data.Length; i += width)
                    {
                        byte b = data[i];
                        if (nameBytes[b] && (width == 1 || data[i + 1] == 0))
                        {
                            run.Append((char)b);
                            continue;
                        }
                        Check(run, found);
                        run.Length = 0;
                    }
                    Check(run, found);
                }
                return found;
            }

            void Check(StringBuilder run, SortedSet<string> found)
            {
                if (run.Length < minLength)
                    return;
                string[] parts = run.ToString().Split('.');
                for (int first = 0; first < parts.Length; first++)
                {
                    string candidate = null;
                    for (int last = first; last < parts.Length && last - first < maxParts; last++)
                    {
                        candidate = candidate == null ? parts[last] : candidate + "." + parts[last];
                        if (candidate.Length >= minLength && names.Contains(candidate))
                            found.Add(candidate);
                    }
                }
            }
        }
    }
}
