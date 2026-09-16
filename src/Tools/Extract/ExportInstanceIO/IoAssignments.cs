using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    // Auto-assigned I/O (---Auto---): the I/O device and scan group each object is assigned to, and the naming rule that
    // turns the object and attribute into an item name. GRAccess does not expose these, so they are read from the galaxy
    // database (object_device_linkage and the autobind_* tables), the same data the IDE uses to show the reference.
    class IoAssignments
    {
        const string DefaultRule = "<HierarchicalName>.<AttributeName>";

        const string LinkageQuery =
            "SELECT o.tag_name, device.tag_name, scanGroup.primitive_name, " +
            "       COALESCE(topic.overridden_naming_rule_id, autoDevice.overridden_naming_rule_id, 0) " +
            "FROM dbo.object_device_linkage linkage " +
            "JOIN dbo.gobject o ON o.gobject_id = linkage.gobject_id " +
            "JOIN dbo.gobject device ON device.gobject_id = linkage.dio_id " +
            "JOIN dbo.primitive_instance scanGroup ON scanGroup.gobject_id = linkage.dio_id " +
            "     AND scanGroup.package_id = device.checked_in_package_id AND scanGroup.mx_primitive_id = linkage.sg_mx_primitive_id " +
            "LEFT JOIN dbo.autobind_device autoDevice ON autoDevice.dio_id = linkage.dio_id " +
            "LEFT JOIN dbo.autobind_device_topic topic ON topic.dio_id = linkage.dio_id AND topic.sg_mx_primitive_id = linkage.sg_mx_primitive_id";

        readonly Dictionary<string, string> scanGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // tagname -> Device.ScanGroup
        readonly Dictionary<string, int> namingRules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // tagname -> naming rule id
        readonly Dictionary<string, string> ruleSpecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "ruleId:I" -> rule spec

        public static IoAssignments Load(string node, string galaxyName)
        {
            IoAssignments assignments = new IoAssignments();
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            builder.DataSource = node;
            builder.InitialCatalog = galaxyName;
            builder.IntegratedSecurity = true;
            builder.ConnectTimeout = 15;

            try
            {
                using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(LinkageQuery, connection))
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string tagname = reader.GetString(0);
                            assignments.scanGroups[tagname] = reader.GetString(1) + "." + reader.GetString(2);
                            assignments.namingRules[tagname] = reader.GetInt32(3);
                        }
                    }
                    using (SqlCommand command = new SqlCommand("SELECT rule_id, io_type, rule_spec FROM dbo.autobind_naming_rule_spec", connection))
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            assignments.ruleSpecs[reader.GetInt32(0) + ":" + reader.GetString(1).Trim()] = reader.GetString(2);
                    }
                }
            }
            catch (SqlException ex)
            {
                throw new GRAccessException("Could not read the I/O device assignments from the " + galaxyName + " database on " + node + ": " + ex.Message);
            }
            return assignments;
        }

        // Device.ScanGroup.ItemName for an object with an assigned device. Without one the path starts with <IODevice>,
        // the way the IDE shows it.
        public string Resolve(string tagname, string hierarchicalName, string attribute, char ioType)
        {
            string scanGroup;
            bool assigned = scanGroups.TryGetValue(tagname, out scanGroup);

            int ruleId;
            if (!namingRules.TryGetValue(tagname, out ruleId))
                ruleId = 0;
            string spec;
            if (!ruleSpecs.TryGetValue(ruleId + ":" + ioType, out spec))
                spec = DefaultRule;

            string item = spec.Replace("<HierarchicalName>", hierarchicalName).Replace("<AttributeName>", attribute);
            return (assigned ? scanGroup : "<IODevice>") + "." + item;
        }
    }
}
