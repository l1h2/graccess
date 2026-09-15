using System;
using System.Collections.Generic;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    enum ItemStatus
    {
        Listed,      // the item is in the scan group's ItemList
        NotListed,   // the device and scan group exist, but the item is not in the ItemList
        Unassigned,  // ---Auto--- without an assigned I/O device (<IODevice>...)
        NotChecked   // the path does not point to a device scan group with an ItemList, e.g. Me.PV
    }

    // Checks I/O paths (Device.ScanGroup.Item) against the ItemList of that scan group on the device integration object.
    // Each device and ItemList is read only once.
    class ItemLists
    {
        readonly GalaxySession session;
        readonly Dictionary<string, IgObject> devices = new Dictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HashSet<string>> itemLists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public ItemLists(GalaxySession session)
        {
            this.session = session;
        }

        public ItemStatus Check(string path)
        {
            if (path.StartsWith("<IODevice>.", StringComparison.Ordinal))
                return ItemStatus.Unassigned;

            string[] parts = path.Split(new[] { '.' }, 3);
            if (parts.Length < 3)
                return ItemStatus.NotChecked;

            HashSet<string> items = ItemList(parts[0], parts[1]);
            if (items == null)
                return ItemStatus.NotChecked;
            return items.Contains(parts[2]) ? ItemStatus.Listed : ItemStatus.NotListed;
        }

        // The scan group's items (compared without case, like all ArchestrA names), or null when there is no such device
        // or the scan group has no ItemList
        HashSet<string> ItemList(string device, string scanGroup)
        {
            string key = device + "." + scanGroup;
            HashSet<string> items;
            if (itemLists.TryGetValue(key, out items))
                return items;

            IgObject deviceObject = Device(device);
            IAttribute list = deviceObject == null ? null : deviceObject.Attributes[scanGroup + ".ItemList"];
            if (list != null)
            {
                items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                MxValue value = list.value;
                int size;
                value.GetDimensionSize(out size);
                for (int i = 1; i <= size; i++)  // array elements are 1-based
                {
                    MxValue element = new MxValueClass();
                    value.GetElement(i, element);
                    items.Add(element.GetString());
                }
            }
            itemLists.Add(key, items);
            return items;
        }

        IgObject Device(string tagname)
        {
            IgObject device;
            if (!devices.TryGetValue(tagname, out device))
            {
                device = tagname.StartsWith("$", StringComparison.Ordinal) ? null : session.FindObject(tagname);
                devices.Add(tagname, device);
            }
            return device;
        }
    }
}
