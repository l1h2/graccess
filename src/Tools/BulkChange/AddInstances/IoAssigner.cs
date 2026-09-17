using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IGalaxy = ArchestrA.GRAccess.IGalaxy;
using IGalaxyConfigurationV25 = ArchestrA.Configuration.IGalaxyConfigurationV25;
using EASSIGNTYPE = ArchestrA.Core.EASSIGNTYPE;
using EIDEVIEW = ArchestrA.Core.EIDEVIEW;
using EPACKAGEOPERATIONSTATUS = ArchestrA.Core.EPACKAGEOPERATIONSTATUS;
using ERRORCODE = ArchestrA.Core.ERRORCODE;
using FAILURESTATUS = ArchestrA.Core.FAILURESTATUS;
using SCANGROUPINFO = ArchestrA.Core.SCANGROUPINFO;

namespace GRAccessTools.BulkChange
{
    // Assigns objects to a device scan group with the call the IDE uses for "assign to IO device"
    // (IGalaxyConfigurationV25.AssignObjectsToDIOScanGroup on the object IGalaxy.GetGalaxyConfiguration() returns).
    // GRAccess has no documented call for this. It runs through the same package server and database procedure as the
    // IDE, so the galaxy applies its usual checks (e.g. deployed objects are left alone). The types come from
    // ArchestrA.Configuration.dll and ArchestrA.Core.dll in the GAC (see references.txt); they are aliased here because
    // ArchestrA.Core also defines types with the same names as GRAccess.
    class IoAssigner : IDisposable
    {
        readonly IGalaxy galaxy;
        object configuration;
        readonly Dictionary<int, SCANGROUPINFO[]> scanGroups = new Dictionary<int, SCANGROUPINFO[]>();

        public IoAssigner(IGalaxy galaxy)
        {
            this.galaxy = galaxy;
        }

        // Assigns the objects (gobject ids) to the device's scan group. Returns "" when the call reported success,
        // otherwise what went wrong. The caller checks the result in the database either way.
        public string Assign(int deviceId, string scanGroup, int[] objectIds)
        {
            IGalaxyConfigurationV25 config = Configuration();

            int scanGroupId = 0;
            foreach (SCANGROUPINFO info in ScanGroups(config, deviceId))
            {
                if (string.Equals(info.scangroupName, scanGroup, StringComparison.OrdinalIgnoreCase))
                    scanGroupId = info.scangroup_Mx_Primitive_Id;
            }
            if (scanGroupId == 0)
                return "the galaxy did not list scan group " + scanGroup + " for the device";

            FAILURESTATUS[] failures;
            EPACKAGEOPERATIONSTATUS status = config.AssignObjectsToDIOScanGroup(
                EASSIGNTYPE.eMyLinkedDevice, deviceId, objectIds, EIDEVIEW.eModelView, scanGroupId, out failures);

            List<string> problems = new List<string>();
            if (status != EPACKAGEOPERATIONSTATUS.ePackageSuccess)
                problems.Add(status.ToString());
            if (failures != null)
            {
                foreach (FAILURESTATUS failure in failures)
                {
                    if (failure.reasoncode != ERRORCODE.eNoError)
                        problems.Add("object " + failure.gObjectId + ": " + failure.reasoncode + (string.IsNullOrEmpty(failure.reason) ? "" : " (" + failure.reason.Trim() + ")"));
                }
            }
            return string.Join("; ", problems.ToArray());
        }

        IGalaxyConfigurationV25 Configuration()
        {
            if (configuration == null)
                configuration = galaxy.GetGalaxyConfiguration();
            IGalaxyConfigurationV25 config = configuration as IGalaxyConfigurationV25;
            if (config == null)
                throw new InvalidOperationException("this galaxy does not offer the IDE's I/O assignment call");
            return config;
        }

        SCANGROUPINFO[] ScanGroups(IGalaxyConfigurationV25 config, int deviceId)
        {
            SCANGROUPINFO[] infos;
            if (!scanGroups.TryGetValue(deviceId, out infos))
            {
                string result;
                if (!config.GetScanGroupInfo(deviceId, out infos, out result) || infos == null)
                    infos = new SCANGROUPINFO[0];
                scanGroups.Add(deviceId, infos);
            }
            return infos;
        }

        public void Dispose()
        {
            if (configuration != null && Marshal.IsComObject(configuration))
                Marshal.FinalReleaseComObject(configuration);
            configuration = null;
        }
    }
}
