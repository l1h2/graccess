using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace GRAccessTools.Common
{
    // A galaxy object: an automation object, a Graphic Toolbox element or an OMI element
    public class GalaxyObject
    {
        public const int AutomationNamespace = 1;
        public const int GraphicNamespace = 3;
        public const int AreaCategory = 13;
        public const int OmiViewAppCategory = 17;
        public const int InTouchViewAppCategory = 26;

        public int Id;
        public string Tagname;
        public string HierarchicalName;
        public int ContainedBy;
        public bool IsTemplate;
        public int Namespace;
        public int Category;
        public int DerivedFrom;
        public int Area;
        public int CheckedInPackage;
        public int CheckedOutPackage;  // 0 when not checked out
        public string CheckedOutBy;

        public bool IsViewApp
        {
            get { return Category == OmiViewAppCategory || Category == InTouchViewAppCategory; }
        }

        // The checked-in version, or the version checked out for editing; other packages are history or deployed copies
        public bool IsCurrent(int packageId)
        {
            return packageId == CheckedInPackage || (CheckedOutPackage != 0 && packageId == CheckedOutPackage);
        }
    }

    // One package version of a graphic: a Graphic Toolbox or OMI element, or a symbol owned by a template or instance
    public class GraphicVersion
    {
        public int ObjectId;
        public int PackageId;
        public int PrimitiveId;
        public int VisualElementId;
        public string Type;           // Symbol, Layout, ScreenProfile, DisplayModule, ClientControl, Widget
        public string PrimitiveName;  // the symbol name for owned symbols; empty for Graphic Toolbox and OMI elements
        public string InheritedKey;   // the version this one inherits its definition from (its own key when defined here)

        public string Key
        {
            get { return VersionKey(ObjectId, PackageId, PrimitiveId); }
        }

        // The graphic across its package versions
        public string GraphicId
        {
            get { return ObjectId + "|" + PrimitiveId; }
        }

        public static string VersionKey(int objectId, int packageId, int primitiveId)
        {
            return objectId + "|" + packageId + "|" + primitiveId;
        }
    }

    // A symbol embedded in a graphic, or in a ViewApp template (visual_element_reference)
    public class EmbeddedSymbol
    {
        public int ReferrerObjectId;
        public int ReferrerPackageId;
        public int ReferrerPrimitiveId;
        public string CheckedInBoundKey;   // version key of the embedded symbol's checked-in version; null when not bound
        public string CheckedOutBoundKey;  // the same for its checked-out version
        public bool Relative;              // embedded as Me.<symbol>
        public List<string> UnboundNames = new List<string>();  // names kept when the symbol could not be bound

        public string ReferrerKey
        {
            get { return GraphicVersion.VersionKey(ReferrerObjectId, ReferrerPackageId, ReferrerPrimitiveId); }
        }
    }

    // The objects, graphics and embedded symbols of a galaxy, read from its database (every statement is a SELECT)
    public class GalaxyGraphics
    {
        const int Timeout = 300;

        public readonly Dictionary<int, GalaxyObject> Objects = new Dictionary<int, GalaxyObject>();
        public readonly Dictionary<string, GraphicVersion> Versions = new Dictionary<string, GraphicVersion>();
        public readonly Dictionary<string, List<GraphicVersion>> VersionsByInheritedKey = new Dictionary<string, List<GraphicVersion>>();
        public readonly Dictionary<string, List<GraphicVersion>> VersionsByGraphic = new Dictionary<string, List<GraphicVersion>>();
        public readonly List<EmbeddedSymbol> Embeds = new List<EmbeddedSymbol>();

        public static GalaxyGraphics Load(SqlConnection connection)
        {
            GalaxyGraphics graphics = new GalaxyGraphics();
            graphics.LoadObjects(connection);
            graphics.LoadVersions(connection);
            graphics.LoadEmbeds(connection);
            return graphics;
        }

        // The automation object with this tagname or hierarchical name (without case), preferring an instance to a
        // template; null when there is none
        public GalaxyObject FindAutomationObject(string name)
        {
            GalaxyObject template = null;
            foreach (GalaxyObject obj in Objects.Values)
            {
                if (obj.Namespace != GalaxyObject.AutomationNamespace)
                    continue;
                if (!string.Equals(obj.Tagname, name, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(obj.HierarchicalName, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!obj.IsTemplate)
                    return obj;
                template = obj;
            }
            return template;
        }

        public string TagnameOf(int objectId)
        {
            GalaxyObject obj;
            return Objects.TryGetValue(objectId, out obj) ? obj.Tagname : "(object " + objectId + ")";
        }

        // Every current version (checked in or checked out) that inherits its definition from this version key, itself included
        public IEnumerable<GraphicVersion> CurrentInheritors(string definitionKey)
        {
            List<GraphicVersion> versions;
            if (!VersionsByInheritedKey.TryGetValue(definitionKey, out versions))
                yield break;
            foreach (GraphicVersion version in versions)
            {
                if (IsCurrent(version.ObjectId, version.PackageId))
                    yield return version;
            }
        }

        public bool IsCurrent(int objectId, int packageId)
        {
            GalaxyObject obj;
            return Objects.TryGetValue(objectId, out obj) && obj.IsCurrent(packageId);
        }

        void LoadObjects(SqlConnection connection)
        {
            const string sql =
                "SELECT g.gobject_id, g.tag_name, g.hierarchical_name, g.contained_by_gobject_id, g.is_template, g.namespace_id, " +
                "       td.category_id, g.derived_from_gobject_id, g.area_gobject_id, g.checked_in_package_id, g.checked_out_package_id, " +
                "       u.user_profile_name " +
                "FROM dbo.gobject g " +
                "JOIN dbo.template_definition td ON td.template_definition_id = g.template_definition_id " +
                "LEFT JOIN dbo.user_profile u ON u.user_guid = g.checked_out_by_user_guid";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    GalaxyObject obj = new GalaxyObject();
                    obj.Id = reader.GetInt32(0);
                    obj.Tagname = reader.GetString(1);
                    obj.HierarchicalName = Text(reader, 2) ?? obj.Tagname;
                    obj.ContainedBy = Number(reader, 3);
                    obj.IsTemplate = Convert.ToBoolean(reader.GetValue(4));
                    obj.Namespace = Number(reader, 5);
                    obj.Category = Number(reader, 6);
                    obj.DerivedFrom = Number(reader, 7);
                    obj.Area = Number(reader, 8);
                    obj.CheckedInPackage = Number(reader, 9);
                    obj.CheckedOutPackage = Number(reader, 10);
                    obj.CheckedOutBy = Text(reader, 11);
                    Objects[obj.Id] = obj;
                }
            }
        }

        void LoadVersions(SqlConnection connection)
        {
            const string sql =
                "SELECT v.gobject_id, v.package_id, v.mx_primitive_id, v.visual_element_id, ve.visual_element_type, p.primitive_name, " +
                "       v.inherited_from_gobject_id, v.inherited_from_package_id, v.inherited_from_mx_primitive_id " +
                "FROM dbo.visual_element_version v " +
                "JOIN dbo.visual_element ve ON ve.visual_element_id = v.visual_element_id " +
                "LEFT JOIN dbo.primitive_instance p ON p.gobject_id = v.gobject_id AND p.package_id = v.package_id AND p.mx_primitive_id = v.mx_primitive_id";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    GraphicVersion version = new GraphicVersion();
                    version.ObjectId = reader.GetInt32(0);
                    version.PackageId = reader.GetInt32(1);
                    version.PrimitiveId = Number(reader, 2);
                    version.VisualElementId = reader.GetInt32(3);
                    version.Type = Text(reader, 4) ?? "";
                    version.PrimitiveName = Text(reader, 5) ?? "";
                    version.InheritedKey = GraphicVersion.VersionKey(Number(reader, 6), Number(reader, 7), Number(reader, 8));
                    Versions[version.Key] = version;
                    AddTo(VersionsByInheritedKey, version.InheritedKey, version);
                    AddTo(VersionsByGraphic, version.GraphicId, version);
                }
            }
        }

        static void AddTo(Dictionary<string, List<GraphicVersion>> index, string key, GraphicVersion version)
        {
            List<GraphicVersion> versions;
            if (!index.TryGetValue(key, out versions))
            {
                versions = new List<GraphicVersion>();
                index.Add(key, versions);
            }
            versions.Add(version);
        }

        void LoadEmbeds(SqlConnection connection)
        {
            const string sql =
                "SELECT gobject_id, package_id, mx_primitive_id, is_relative_reference, " +
                "       checked_in_bound_visual_element_gobject_id, checked_in_bound_visual_element_package_id, checked_in_bound_visual_element_mx_primitive_id, " +
                "       checked_out_bound_visual_element_gobject_id, checked_out_bound_visual_element_package_id, checked_out_bound_visual_element_mx_primitive_id, " +
                "       checked_in_unbound_visual_element_name, checked_in_unbound_tag_name, checked_out_unbound_visual_element_name, checked_out_unbound_tag_name " +
                "FROM dbo.visual_element_reference";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    EmbeddedSymbol embed = new EmbeddedSymbol();
                    embed.ReferrerObjectId = reader.GetInt32(0);
                    embed.ReferrerPackageId = reader.GetInt32(1);
                    embed.ReferrerPrimitiveId = Number(reader, 2);
                    embed.Relative = Text(reader, 3) == "1";
                    embed.CheckedInBoundKey = BoundKey(reader, 4);
                    embed.CheckedOutBoundKey = BoundKey(reader, 7);
                    for (int i = 10; i <= 13; i++)
                    {
                        string name = Text(reader, i);
                        if (!string.IsNullOrEmpty(name))
                            embed.UnboundNames.Add(name);
                    }
                    Embeds.Add(embed);
                }
            }
        }

        static string BoundKey(SqlDataReader reader, int first)
        {
            int objectId = Number(reader, first);
            return objectId == 0 ? null : GraphicVersion.VersionKey(objectId, Number(reader, first + 1), Number(reader, first + 2));
        }

        static int Number(IDataRecord record, int i)
        {
            return record.IsDBNull(i) ? 0 : Convert.ToInt32(record.GetValue(i));
        }

        static string Text(IDataRecord record, int i)
        {
            return record.IsDBNull(i) ? null : Convert.ToString(record.GetValue(i)).Trim();
        }
    }
}
