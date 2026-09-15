using System;
using System.Collections.Generic;
using ArchestrA.GRAccess;

namespace GRAccessTools.Common
{
    /// <summary>
    /// A logged-in connection to one galaxy. Always use it in a using block: Dispose logs out.
    /// It also keeps the GRAccessApp object alive, which GRAccess requires while any of its objects are in use.
    /// </summary>
    public sealed class GalaxySession : IDisposable
    {
        private GRAccessApp app;
        private IGalaxy galaxy;

        private GalaxySession(GRAccessApp app, IGalaxy galaxy)
        {
            this.app = app;
            this.galaxy = galaxy;
        }

        public IGalaxy Galaxy
        {
            get
            {
                if (galaxy == null)
                    throw new ObjectDisposedException("GalaxySession");
                return galaxy;
            }
        }

        /// <summary>Names of the galaxies on a Galaxy Repository node. No login needed.</summary>
        public static string[] ListGalaxies(string node)
        {
            GRAccessApp app = new GRAccessApp();
            IGalaxies galaxies = app.QueryGalaxies(node);
            GRAccessException.ThrowIfFailed(app.CommandResult, "QueryGalaxies on " + node);

            List<string> names = new List<string>();
            foreach (IGalaxy g in galaxies)
                names.Add(g.Name);

            GC.KeepAlive(app);
            return names.ToArray();
        }

        /// <summary>
        /// Finds the galaxy (name is case-insensitive) and logs in.
        /// Blank user and password work when galaxy security is off.
        /// </summary>
        public static GalaxySession Open(string node, string galaxyName, string user, string password)
        {
            GRAccessApp app = new GRAccessApp();
            IGalaxies galaxies = app.QueryGalaxies(node);
            GRAccessException.ThrowIfFailed(app.CommandResult, "QueryGalaxies on " + node);

            IGalaxy galaxy = null;
            List<string> names = new List<string>();
            foreach (IGalaxy g in galaxies)
            {
                names.Add(g.Name);
                if (string.Equals(g.Name, galaxyName, StringComparison.OrdinalIgnoreCase))
                    galaxy = g;
            }
            if (galaxy == null)
                throw new GRAccessException("Galaxy '" + galaxyName + "' was not found on " + node + ". Galaxies there: " + string.Join(", ", names.ToArray()));

            galaxy.Login(user ?? "", password ?? "");
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Login to " + galaxy.Name);

            return new GalaxySession(app, galaxy);
        }

        /// <summary>Finds a template (name starts with '$') or an instance by tagname. Returns null if it does not exist.</summary>
        public IgObject FindObject(string tagname)
        {
            EgObjectIsTemplateOrInstance kind = tagname.StartsWith("$", StringComparison.Ordinal)
                ? EgObjectIsTemplateOrInstance.gObjectIsTemplate
                : EgObjectIsTemplateOrInstance.gObjectIsInstance;

            string[] names = { tagname };
            IgObjects objects = Galaxy.QueryObjectsByName(kind, ref names);
            GRAccessException.ThrowIfFailed(Galaxy.CommandResult, "QueryObjectsByName(" + tagname + ")");

            return objects.count == 0 ? null : objects[1];  // GRAccess collections are 1-based
        }

        public void Dispose()
        {
            if (galaxy == null)
                return;

            galaxy.Logout();
            galaxy = null;
            GC.KeepAlive(app);
            app = null;
        }
    }
}
