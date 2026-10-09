using System;
using System.Collections.Generic;
using ArchestrA.GRAccess;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Finds instances by name, and every instance in an area and its sub-areas.
    /// </summary>
    public static class GalaxyInstances
    {
        /// <summary>Accepts the tagname or the full hierarchical name (Container.ContainedName). Returns null when not found.</summary>
        public static IgObject TryFind(IGalaxy galaxy, string name)
        {
            string[] names = { name };
            IgObjects byTagname = galaxy.QueryObjectsByName(EgObjectIsTemplateOrInstance.gObjectIsInstance, ref names);
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find instance " + name);
            if (byTagname.count > 0)
                return byTagname[1];

            // hierarchicalNameLike uses SQL LIKE, where _ matches any character, so only the exact name counts
            IgObjects byHierarchicalName = galaxy.QueryObjects(EgObjectIsTemplateOrInstance.gObjectIsInstance, EConditionType.hierarchicalNameLike, name, EMatch.MatchCondition);
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find instance " + name);
            foreach (IgObject candidate in byHierarchicalName)
            {
                if (string.Equals(candidate.HierarchicalName, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Every instance in the area and in all of its sub-areas, sorted by full name, without the area itself.
        /// belongsToArea returns the objects directly in an area, contained objects included; the sub-areas are the
        /// area objects among them.
        /// </summary>
        public static List<IgObject> FindInArea(IGalaxy galaxy, string areaName)
        {
            IgObject area = TryFind(galaxy, areaName);
            if (area == null)
                throw new GRAccessException("Area '" + areaName + "' not found in " + galaxy.Name + ".");
            if (area.category != ECATEGORY.idxCategoryArea)
                throw new GRAccessException("'" + areaName + "' is not an area.");

            SortedDictionary<string, IgObject> found = new SortedDictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visitedAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { area.Tagname };
            Queue<string> areas = new Queue<string>();
            areas.Enqueue(area.Tagname);
            while (areas.Count > 0)
            {
                string current = areas.Dequeue();
                IgObjects members = galaxy.QueryObjects(EgObjectIsTemplateOrInstance.gObjectIsInstance, EConditionType.belongsToArea, current, EMatch.MatchCondition);
                GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find the objects in area " + current);
                foreach (IgObject member in members)
                {
                    if (!string.Equals(member.Area, current, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string hierarchicalName = member.HierarchicalName;
                    if (!found.ContainsKey(hierarchicalName))
                        found.Add(hierarchicalName, member);
                    if (member.category == ECATEGORY.idxCategoryArea && visitedAreas.Add(member.Tagname))
                        areas.Enqueue(member.Tagname);
                }
            }
            return new List<IgObject>(found.Values);
        }
    }
}
