using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Finds what still uses an attribute: the galaxy database's attribute_reference table holds the references the
    /// galaxy resolved from graphics, scripts and object settings (see docs\GRAccess-Notes.md). Read-only, with the
    /// Windows login of the user running the tool.
    /// </summary>
    public static class AttributeReferences
    {
        const string Sql =
            "SELECT referrer.tag_name, ar.reference_string, resolved.tag_name, pi.primitive_name " +
            "FROM attribute_reference ar " +
            "JOIN gobject referrer ON referrer.gobject_id = ar.gobject_id " +
            "  AND ar.package_id IN (referrer.checked_in_package_id, referrer.checked_out_package_id) " +
            "LEFT JOIN gobject resolved ON resolved.gobject_id = ar.resolved_gobject_id " +
            "LEFT JOIN primitive_instance pi ON pi.gobject_id = ar.gobject_id AND pi.package_id = ar.package_id " +
            "  AND pi.mx_primitive_id = ar.referring_mx_primitive_id " +
            "WHERE ar.reference_string LIKE @pattern";

        // References to <object>.<name> (or one of its properties, e.g. <object>.<name>.Msg) where the object is one of
        // the given tagnames, including Me.<name> inside them. The attribute's own extensions are left out: their
        // primitive carries the attribute's name. Returns "referrer: reference" texts.
        public static List<string> Find(string node, string galaxyName, string name, List<string> tagnames)
        {
            HashSet<string> family = new HashSet<string>(tagnames, StringComparer.OrdinalIgnoreCase);
            List<string> found = new List<string>();
            using (SqlConnection connection = GalaxyDatabase.Open(node, galaxyName))
            using (SqlCommand command = GalaxyDatabase.Command(connection, Sql, 120))
            {
                // _ is a LIKE wildcard, so this finds a superset; the exact match is checked below
                command.Parameters.AddWithValue("@pattern", "%" + name + "%");
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string referrer = reader.GetString(0);
                        string reference = reader.GetString(1);
                        string resolved = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        string primitive = reader.IsDBNull(3) ? "" : reader.GetString(3);

                        string[] parts = reference.Split('.');
                        int at = Array.FindIndex(parts, 1, part => string.Equals(part, name, StringComparison.OrdinalIgnoreCase));
                        if (at < 0)
                            continue;

                        bool isMe = string.Equals(parts[0], "Me", StringComparison.OrdinalIgnoreCase);
                        bool toFamily = family.Contains(resolved)
                            || (isMe && family.Contains(referrer))
                            || (resolved.Length == 0 && family.Contains(parts[0]));
                        if (!toFamily)
                            continue;
                        if (family.Contains(referrer) && string.Equals(primitive, name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string text = referrer + ": " + reference;
                        if (!found.Contains(text))
                            found.Add(text);
                    }
                }
            }
            return found;
        }
    }
}
