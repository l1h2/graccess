using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // I/O device assignments (object -> Device.ScanGroup) read from the galaxy database, where GRAccess does not expose
    // them (see docs\GRAccess-Notes.md). Every statement is a SELECT.
    class GalaxyIo
    {
        const int Timeout = 120;

        // The scan group name comes from the device's checked-in version, or its checked-out version for a scan group
        // added since
        const string LinkColumns =
            "d.tag_name, COALESCE(sgIn.primitive_name, sgOut.primitive_name) ";
        const string LinkJoins =
            "LEFT JOIN dbo.object_device_linkage l ON l.gobject_id = g.gobject_id " +
            "LEFT JOIN dbo.gobject d ON d.gobject_id = l.dio_id " +
            "LEFT JOIN dbo.primitive_instance sgIn ON sgIn.gobject_id = l.dio_id AND sgIn.package_id = d.checked_in_package_id AND sgIn.mx_primitive_id = l.sg_mx_primitive_id " +
            "LEFT JOIN dbo.primitive_instance sgOut ON sgOut.gobject_id = l.dio_id AND sgOut.package_id = d.checked_out_package_id AND sgOut.mx_primitive_id = l.sg_mx_primitive_id ";

        readonly SqlConnection connection;

        public GalaxyIo(SqlConnection connection)
        {
            this.connection = connection;
        }

        // Device.ScanGroup an area is assigned to, or null when it has none
        public string AreaIo(string area)
        {
            string sql =
                "SELECT " + LinkColumns +
                "FROM dbo.gobject g " + LinkJoins +
                "WHERE g.tag_name = @area AND g.namespace_id = 1 AND g.is_template = 0";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            {
                command.Parameters.AddWithValue("@area", area);
                using (SqlDataReader reader = command.ExecuteReader())
                    return reader.Read() ? Io(reader, 0) : null;
            }
        }

        // The gobject id of an instance, or 0 when there is none
        public int InstanceId(string tagname)
        {
            const string sql = "SELECT gobject_id FROM dbo.gobject WHERE tag_name = @name AND namespace_id = 1 AND is_template = 0";
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            {
                command.Parameters.AddWithValue("@name", tagname);
                object id = command.ExecuteScalar();
                return id == null ? 0 : Convert.ToInt32(id);
            }
        }

        // The instance and every object it contains, with their area and I/O assignment
        public List<ObjectState> Objects(string tagname)
        {
            string sql =
                "WITH tree AS ( " +
                "  SELECT gobject_id, 0 AS depth FROM dbo.gobject WHERE tag_name = @name AND namespace_id = 1 AND is_template = 0 " +
                "  UNION ALL " +
                "  SELECT g.gobject_id, t.depth + 1 FROM dbo.gobject g JOIN tree t ON g.contained_by_gobject_id = t.gobject_id WHERE g.namespace_id = 1 " +
                ") " +
                "SELECT g.tag_name, g.hierarchical_name, a.tag_name, " + LinkColumns + ", " +
                "       (SELECT COUNT(*) FROM dbo.attribute_reference r WHERE r.gobject_id = g.gobject_id AND r.package_id = g.checked_in_package_id AND r.reference_string = '---Auto---'), " +
                "       g.gobject_id " +
                "FROM tree t JOIN dbo.gobject g ON g.gobject_id = t.gobject_id " +
                "LEFT JOIN dbo.gobject a ON a.gobject_id = g.area_gobject_id " + LinkJoins +
                "ORDER BY t.depth, g.hierarchical_name";
            List<ObjectState> objects = new List<ObjectState>();
            using (SqlCommand command = GalaxyDatabase.Command(connection, sql, Timeout))
            {
                command.Parameters.AddWithValue("@name", tagname);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        ObjectState state = new ObjectState();
                        state.Tagname = reader.GetString(0);
                        state.HierarchicalName = reader.IsDBNull(1) ? state.Tagname : reader.GetString(1);
                        state.Area = reader.IsDBNull(2) ? null : reader.GetString(2);
                        state.Io = Io(reader, 3);
                        state.AutoReferences = Convert.ToInt32(reader.GetValue(5));
                        state.Id = reader.GetInt32(6);
                        objects.Add(state);
                    }
                }
            }
            return objects;
        }

        static string Io(SqlDataReader reader, int first)
        {
            if (reader.IsDBNull(first))
                return null;
            return reader.GetString(first) + "." + (reader.IsDBNull(first + 1) ? "(unknown scan group)" : reader.GetString(first + 1));
        }
    }
}
