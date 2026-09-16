using System.Data.SqlClient;

namespace GRAccessTools.Common
{
    /// <summary>
    /// The galaxy's SQL Server database on the Galaxy Repository node, for data GRAccess does not expose (see
    /// docs\GRAccess-Notes.md). Tools only read from it, with the Windows login of the user running the tool.
    /// </summary>
    public static class GalaxyDatabase
    {
        /// <summary>An open connection to the galaxy's database. Use it in a using block.</summary>
        public static SqlConnection Open(string node, string galaxyName)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            builder.DataSource = node;
            builder.InitialCatalog = galaxyName;
            builder.IntegratedSecurity = true;
            builder.ConnectTimeout = 15;

            SqlConnection connection = new SqlConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch (SqlException ex)
            {
                connection.Dispose();
                throw new GRAccessException("Could not open the " + galaxyName + " database on " + node + " with your Windows login: " + ex.Message);
            }
        }

        /// <summary>A command on the connection; the timeout is in seconds.</summary>
        public static SqlCommand Command(SqlConnection connection, string sql, int timeout)
        {
            SqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = timeout;
            return command;
        }
    }
}
