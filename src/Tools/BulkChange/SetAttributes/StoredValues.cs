using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // The values an object stores itself, as the galaxy database keeps them (dynamic_attribute rows of its checked-in
    // package: its own UDA values and their descriptions and units). Once a template holds a value of a UDA's new data
    // type, GRAccess shows the derived objects' own values converted to that type, so only the database shows a value
    // still stored with the old type (see docs\GRAccess-Notes.md). Read-only, with the Windows login of the user running
    // the tool.
    static class StoredValues
    {
        const string Sql =
            "SELECT d.attribute_name, CAST(d.mx_value AS varchar(max)) " +
            "FROM dynamic_attribute d " +
            "JOIN gobject g ON g.gobject_id = d.gobject_id AND d.package_id = g.checked_in_package_id " +
            "WHERE g.tag_name = @tagname AND d.mx_value IS NOT NULL";

        // Attribute name -> stored value: hex text (0x...) whose first byte is the value's MxDataType
        public static Dictionary<string, string> Read(SqlConnection connection, string tagname)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (SqlCommand command = GalaxyDatabase.Command(connection, Sql, 120))
            {
                command.Parameters.AddWithValue("@tagname", tagname);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string name = reader.GetString(0);
                        if (!values.ContainsKey(name))
                            values.Add(name, reader.IsDBNull(1) ? "" : reader.GetString(1));
                    }
                }
            }
            return values;
        }

        // The data type the value is stored with (MxNoData when there is none)
        public static MxDataType TypeOf(string hex)
        {
            byte[] bytes = Bytes(hex);
            return bytes.Length == 0 ? MxDataType.MxNoData : (MxDataType)bytes[0];
        }

        // The stored value as bool, int, float or double, or null for other data types
        public static object ValueOf(string hex)
        {
            byte[] bytes = Bytes(hex);
            switch (TypeOf(hex))
            {
                case MxDataType.MxBoolean:
                    return bytes.Length >= 2 ? (object)(bytes[1] != 0) : null;
                case MxDataType.MxInteger:
                    return bytes.Length >= 5 ? (object)BitConverter.ToInt32(bytes, 1) : null;
                case MxDataType.MxFloat:
                    return bytes.Length >= 5 ? (object)BitConverter.ToSingle(bytes, 1) : null;
                case MxDataType.MxDouble:
                    return bytes.Length >= 9 ? (object)BitConverter.ToDouble(bytes, 1) : null;
                default:
                    return null;
            }
        }

        static byte[] Bytes(string hex)
        {
            string digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
            byte[] bytes = new byte[digits.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(digits.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
