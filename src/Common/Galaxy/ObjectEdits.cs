using System;
using ArchestrA.GRAccess;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Steps for changing an object: CheckOut, make the changes, then SaveAndCheckIn, or UndoCheckOut if anything fails.
    /// </summary>
    public static class ObjectEdits
    {
        public static void CheckOut(IgObject obj)
        {
            obj.CheckOut();
            GRAccessException.ThrowIfFailed(obj.CommandResult, "Check out " + obj.Tagname);
        }

        /// <summary>Saves a checked-out object and checks it in. An object with configuration errors is not checked in.</summary>
        public static void SaveAndCheckIn(IgObject obj, string comment)
        {
            obj.Save();
            GRAccessException.ThrowIfFailed(obj.CommandResult, "Save " + obj.Tagname);
            if (obj.ValidationStatus == EPACKAGESTATUS.ePackageBad)
                throw new GRAccessException(obj.Tagname + " has configuration errors: " + string.Join("; ", obj.Errors ?? new string[0]));

            obj.CheckIn(comment);
            GRAccessException.ThrowIfFailed(obj.CommandResult, "Check in " + obj.Tagname);
        }

        /// <summary>
        /// Undoes a check-out and returns whether it worked. After Save, GRAccess refuses the undo while the object is
        /// still "being edited" by this session; unloading the object first lets it go through.
        /// </summary>
        public static bool UndoCheckOut(IgObject obj)
        {
            try
            {
                obj.UndoCheckOut();
                if (obj.CommandResult.Successful)
                    return true;

                obj.Unload();
                obj.UndoCheckOut();
                return obj.CommandResult.Successful;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
