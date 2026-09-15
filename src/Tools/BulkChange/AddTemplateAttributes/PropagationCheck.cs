using System;
using System.Collections.Generic;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // What was found on one derived template or instance
    class PropagationResult
    {
        public string Template;
        public string Tagname;
        public string Kind;  // Template or Instance
        public List<string> Problems = new List<string>();
        public string Note = "";
    }

    // Checking in a template propagates its changes to every template and instance derived from it. Objects that
    // cannot be updated (for example because they are checked out) keep the old definition; this finds them so the
    // change can be applied again once the cause is fixed.
    static class PropagationCheck
    {
        public static List<PropagationResult> Run(IGalaxy galaxy, TemplateGroup group)
        {
            List<AttributeRow> changes = group.Rows.FindAll(row => row.Result == "Added" || row.Result == "Updated");
            List<KeyValuePair<IgObject, bool>> descendants = FindDescendants(galaxy, group.Tagname);
            int templateCount = descendants.FindAll(d => d.Value).Count;
            Console.WriteLine("  Checking propagation to " + templateCount + " derived template(s) and " + (descendants.Count - templateCount) + " instance(s)...");

            List<PropagationResult> results = new List<PropagationResult>();
            int pendingDeploy = 0;
            foreach (KeyValuePair<IgObject, bool> descendant in descendants)
            {
                IgObject obj = descendant.Key;
                PropagationResult result = new PropagationResult();
                result.Template = group.Tagname;
                result.Tagname = obj.Tagname;
                result.Kind = descendant.Value ? "Template" : "Instance";
                foreach (AttributeRow row in changes)
                    result.Problems.AddRange(Compare(obj, row));

                if (result.Problems.Count > 0 && obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    result.Note = "checked out" + (string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " by " + obj.checkedOutBy);
                if (!descendant.Value && ((IInstance)obj).DeploymentStatus == EDeploymentStatus.deployedWithPendingChanges)
                    pendingDeploy++;
                results.Add(result);
            }

            List<PropagationResult> failures = results.FindAll(r => r.Problems.Count > 0);
            if (failures.Count == 0)
            {
                Console.WriteLine("  All " + descendants.Count + " derived object(s) have the change.");
            }
            else
            {
                Console.Error.WriteLine("  PROPAGATION FAILED for " + failures.Count + " of " + descendants.Count + " derived object(s):");
                foreach (PropagationResult failure in failures)
                    Console.Error.WriteLine("    " + failure.Tagname + ": " + string.Join("; ", failure.Problems.ToArray()) + (failure.Note.Length > 0 ? " (" + failure.Note + ")" : ""));
                Console.Error.WriteLine("  Fix the cause (for example check in objects that are checked out), then run the same file again with -o to reapply the change.");
            }
            if (pendingDeploy > 0)
                Console.WriteLine("  " + pendingDeploy + " deployed instance(s) now have pending changes and need to be redeployed (this tool never deploys).");
            return results;
        }

        // GRAccess refuses to check in a template while any template or instance derived from it is checked out
        // ("Object or its descendent(s) is in use"), so nothing propagates. Lists the checked-out descendants.
        public static void ReportBlockedCheckIn(IGalaxy galaxy, string tagname)
        {
            List<string> checkedOut = new List<string>();
            foreach (KeyValuePair<IgObject, bool> descendant in FindDescendants(galaxy, tagname))
            {
                IgObject obj = descendant.Key;
                if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    checkedOut.Add(obj.Tagname + (string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " (checked out by " + obj.checkedOutBy + ")"));
            }

            Console.Error.WriteLine("  PROPAGATION BLOCKED: " + tagname + " cannot be checked in while objects derived from it are in use, so nothing was changed or propagated.");
            if (checkedOut.Count > 0)
            {
                Console.Error.WriteLine("  Checked-out derived objects:");
                foreach (string line in checkedOut)
                    Console.Error.WriteLine("    " + line);
            }
            else
            {
                Console.Error.WriteLine("  None of them is checked out; one may be open in the IDE or used by another session.");
            }
            Console.Error.WriteLine("  Check them in or undo their check-outs, then run the same command again.");
        }

        public static void WriteFile(string path, List<PropagationResult> results)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("template", "object", "kind", "status", "problems", "note");
                foreach (PropagationResult result in results)
                    csv.WriteRow(result.Template, result.Tagname, result.Kind, result.Problems.Count == 0 ? "OK" : "Failed", string.Join("; ", result.Problems.ToArray()), result.Note);
            }
        }

        // derivedOrInstantiatedFrom only returns direct children, so walk down the derivation tree.
        // The bool is true for templates and false for instances.
        static List<KeyValuePair<IgObject, bool>> FindDescendants(IGalaxy galaxy, string tagname)
        {
            List<KeyValuePair<IgObject, bool>> found = new List<KeyValuePair<IgObject, bool>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> parents = new Queue<string>();
            parents.Enqueue(tagname);
            while (parents.Count > 0)
            {
                string parent = parents.Dequeue();
                foreach (bool isTemplate in new[] { true, false })
                {
                    EgObjectIsTemplateOrInstance kind = isTemplate ? EgObjectIsTemplateOrInstance.gObjectIsTemplate : EgObjectIsTemplateOrInstance.gObjectIsInstance;
                    IgObjects children = galaxy.QueryObjects(kind, EConditionType.derivedOrInstantiatedFrom, parent, EMatch.MatchCondition);
                    GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find objects derived from " + parent);
                    foreach (IgObject child in children)
                    {
                        if (!seen.Add(child.Tagname))
                            continue;
                        found.Add(new KeyValuePair<IgObject, bool>(child, isTemplate));
                        if (isTemplate)
                            parents.Enqueue(child.Tagname);
                    }
                }
            }
            return found;
        }

        static List<string> Compare(IgObject obj, AttributeRow row)
        {
            List<string> problems = new List<string>();
            IAttribute attribute = obj.Attributes[row.Name];
            if (attribute == null)
            {
                problems.Add(row.Name + " is missing");
                return problems;
            }

            if (attribute.DataType != row.DataType)
                problems.Add(row.Name + " is " + attribute.DataType.ToString().Substring(2) + " instead of " + row.DataTypeName);
            if (row.Description.Length > 0)
                Expect(obj, row.Name + ".Description", row.Description, problems);
            if (row.OffMessage != null)
            {
                Expect(obj, row.Name + ".OffMsg", row.OffMessage, problems);
                Expect(obj, row.Name + ".OnMsg", row.OnMessage, problems);
            }
            if (row.EngUnits != null)
                Expect(obj, row.Name + ".EngUnits", row.EngUnits, problems);

            // Input extensions add .InputSource and output extensions add .OutputDest (input/output adds both)
            bool hasInput = obj.Attributes[row.Name + ".InputSource"] != null;
            bool hasOutput = obj.Attributes[row.Name + ".OutputDest"] != null;
            bool wantInput = row.IoExtensionType == "inputextension" || row.IoExtensionType == "inputoutputextension";
            bool wantOutput = row.IoExtensionType == "outputextension" || row.IoExtensionType == "inputoutputextension";
            if (hasInput != wantInput || hasOutput != wantOutput)
                problems.Add(row.Name + " I/O is " + IoText(hasInput, hasOutput) + " instead of " + IoText(wantInput, wantOutput));
            return problems;
        }

        static void Expect(IgObject obj, string name, string expected, List<string> problems)
        {
            IAttribute attribute = obj.Attributes[name];
            if (attribute == null)
            {
                problems.Add(name + " is missing");
                return;
            }
            string actual = attribute.value.GetString();
            if (actual != expected)
                problems.Add(name + " is '" + actual + "' instead of '" + expected + "'");
        }

        static string IoText(bool input, bool output)
        {
            return input && output ? "IO" : input ? "I" : output ? "O" : "none";
        }
    }
}
