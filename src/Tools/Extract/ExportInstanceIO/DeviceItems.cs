using System;
using System.Collections.Generic;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    enum ItemStatus
    {
        Found,        // the item is one of the scan group's device items; the reference is the item reference it maps to
        Direct,       // the scan group has no device items, so the item is sent to the server as is; it is the reference
        NotFound,     // the scan group has device items, but the item is not one of them (unmapped)
        NoItem,       // the path ends at the device's scan group, without an item (e.g. MWSUB_RDI_2.MDWSUB.)
        NoScanGroup,  // the device has no scan group or attribute by that name (e.g. a misspelled scan group)
        Unassigned,   // ---Auto--- without an assigned I/O device (<IODevice>...)
        NotSet,       // no I/O reference is set (---)
        NotDevice     // the path does not point to a device scan group: another object (e.g. Me.PV) or an attribute of the device
    }

    // Finds the item reference (the address in the topic) of I/O paths (Device.ScanGroup.Item) from the device items of that
    // scan group on the device integration object. Each device, scan group and device attribute is read only once.
    // A scan group with device items maps item names (aliases) to item references, and an item that is not one of them is
    // unmapped. A scan group without any device items passes every item to the server as is, so the item is the reference;
    // on EMGALAXY that is how the redundant DI objects address their PLCs (e.g. 401016 F, F8:6, HMI_STATUS[8].1).
    class DeviceItems
    {
        const string NotSetReference = "---";

        readonly GalaxySession session;
        readonly Dictionary<string, IgObject> devices = new Dictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, string>> scanGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> deviceAttributes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public DeviceItems(GalaxySession session)
        {
            this.session = session;
        }

        // The reference is set only for Found and Direct
        public ItemStatus Find(string path, out string reference)
        {
            reference = null;
            if (path.Length == 0 || path == NotSetReference)
                return ItemStatus.NotSet;
            if (path.StartsWith("<IODevice>.", StringComparison.Ordinal))
                return ItemStatus.Unassigned;

            string[] parts = path.Split(new[] { '.' }, 3);
            IgObject device = parts.Length < 2 ? null : Device(parts[0]);
            if (device == null)
                return ItemStatus.NotDevice;

            Dictionary<string, string> items = Items(device, parts[0], parts[1]);
            if (items == null)  // e.g. SBH_RDI.ConnectionAlarm reads an attribute of the device itself
                return HasAttribute(device, parts[0], parts[1]) ? ItemStatus.NotDevice : ItemStatus.NoScanGroup;
            if (parts.Length < 3 || parts[2].Length == 0)
                return ItemStatus.NoItem;
            if (items.Count == 0)
            {
                reference = parts[2];
                return ItemStatus.Direct;
            }
            return items.TryGetValue(parts[2], out reference) ? ItemStatus.Found : ItemStatus.NotFound;
        }

        // Item name -> item reference for the scan group (names compared without case, like all ArchestrA names; empty when
        // it has no device items), or null when the device has no such scan group. They are kept as XML in
        // ScanGroup.AliasDatabase: <ItemsList><Item Name="AV:3002575:PRESENT-VALUE" Alias="LSC3_CH3.ACC_MAP_OUT_FREQ"/></ItemsList>,
        // where Alias is the item name the objects use and Name is the item reference. ScanGroup.ItemList lists those item names.
        Dictionary<string, string> Items(IgObject device, string tagname, string scanGroup)
        {
            string key = tagname + "." + scanGroup;
            Dictionary<string, string> items;
            if (scanGroups.TryGetValue(key, out items))
                return items;

            string attributeName = scanGroup + ".AliasDatabase";
            if (device.Attributes[attributeName] != null)
            {
                items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<XmlElement> elements;
                try
                {
                    elements = ObjectXml.SelectElements(device, attributeName, "/ItemsList/Item");
                }
                catch (XmlException ex)
                {
                    throw new GRAccessException("Could not read the device items in " + key + ".AliasDatabase: " + ex.Message);
                }

                foreach (XmlElement element in elements)
                {
                    string reference = element.GetAttribute("Name");
                    string name = element.GetAttribute("Alias");
                    if (name.Length == 0)
                        name = reference;  // an item without an alias is used by its reference
                    if (name.Length > 0 && !items.ContainsKey(name))
                        items.Add(name, reference);
                }
            }
            scanGroups.Add(key, items);
            return items;
        }

        bool HasAttribute(IgObject device, string tagname, string attributeName)
        {
            string key = tagname + "." + attributeName;
            bool exists;
            if (!deviceAttributes.TryGetValue(key, out exists))
            {
                exists = device.Attributes[attributeName] != null;
                deviceAttributes.Add(key, exists);
            }
            return exists;
        }

        // The device integration object (one with scan groups) with this tagname, or null for any other name
        IgObject Device(string tagname)
        {
            IgObject device;
            if (!devices.TryGetValue(tagname, out device))
            {
                device = tagname.StartsWith("$", StringComparison.Ordinal) ? null : session.FindObject(tagname);
                if (device != null && device.Attributes["ScanGroupList"] == null)
                    device = null;
                devices.Add(tagname, device);
            }
            return device;
        }
    }
}
