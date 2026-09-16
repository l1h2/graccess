# GRAccess notes

What has been verified about GRAccess on this machine (AVEVA Application Server 2020 R2 SP1 P01, ArchestrA.GRAccess 2.0.0.0). Add to this file whenever a tool uncovers something new.

The official reference and samples are in `C:\Program Files (x86)\ArchestrA\Toolkits\Docs` (GRAccess.chm and GRAccess.pdf).

## Process setup

- GRAccess is a 32-bit, apartment-threaded COM component. Tools must be built as x86 and `Main` must be marked `[STAThread]`. `build.ps1` and `ToolRunner` check both.
- PowerShell cannot drive GRAccess: the objects it returns come back blank or fail with TYPE_E_LIBNOTREGISTERED. Tools are written in C#.
- The `GRAccessApp` object must stay alive while any GRAccess object is in use. `GalaxySession` takes care of this.
- The interop assembly is `C:\Program Files (x86)\Common Files\ArchestrA\ArchestrA.GRAccess.dll` (the same file is in the GAC). It is not copied to `bin\`; each machine uses its installed copy.

## Calling GRAccess

- Failed calls do not throw. Check `CommandResult` after every call (`CommandResults` for calls on many objects). In tools, use `GRAccessException.ThrowIfFailed`.
- Log in before reading galaxy properties: `VersionString` and `UpgradeRequired` fail with RPC_E_SERVERFAULT before `Login`.
- `Login("", "")` works when galaxy security is off. On this node EMGALAXY has security enabled; Galaxy_test, TESTGALAXY and TrainingGalaxy do not.
- Collections are 1-based: `objects[1]` is the first item, and `objects[0]` fails with E_FAIL.
- `QueryObjectsByName` must be told whether the names are templates or instances. Template names start with `$`.
- A query for an object that does not exist succeeds with `count == 0`. An attribute that does not exist returns `null`.
- `attribute.value.GetString()` works for every data type. Values that are not set read as `No Data`.
- GRAccess objects live in `GRAccessApp.exe`, a separate 32-bit process started for each tool run, and stay there until .NET releases their wrappers. That only happens when the tool collects garbage, which a tool rarely needs on its own. Every read of `IgObject.Attributes` builds the whole attribute collection there. Reading the I/O of the 1118 instances of EMGALAXY's MU area that way grew `GRAccessApp.exe` to 1.6 GB until it crashed (Application Error 0xc0000409), and the tool then failed with "The RPC server is unavailable" (0x800706BA). Read `Attributes` once per object and call `GC.Collect()` and `GC.WaitForPendingFinalizers()` every few dozen objects; `ExportInstanceIO` then peaks below 300 MB on MU.

## Templates, toolsets and attributes

- Cast a template's `IgObject` to `ITemplate` to read `Toolset`. It is the full toolset path with `$` between levels, e.g. `Radix$Equipment$Pump`. Toolset names on their own are not unique (EMGALAXY has four toolsets called System), so always work with the full path.
- `IGalaxy.QueryToolsets()` returns every toolset in the galaxy as a full path. `IToolset.GetChildToolsets(1)` returns a toolset's direct children, also as full paths.
- There is no query condition for "templates in a toolset". Query all templates with `QueryObjects(gObjectIsTemplate, EConditionType.namedLike, "%", EMatch.MatchCondition)` and check each template's `Toolset`. `namedLike` uses SQL wildcards (`%`); `*` matches nothing.
- The query itself is fast (about 25 ms for 287 templates on EMGALAXY), but reading `Toolset` takes about 30 ms per template, so checking every template takes about 9 seconds.
- Contained templates (e.g. `$PumpVFDControl.SetPointControl`) are returned as templates with their own `Toolset`, like any other template.
- `IgObject.Attributes` lists every attribute: internal ones whose names start with `_`, attributes that belong to UDAs and extensions (e.g. `Start.Description`), script attributes, and so on. A few internal names (`_ExternalName`, `_InternalName`) appear twice. `ConfigurableAttributes` returned the same list for `$Pump`.
- UDAs are listed as XML in the `UDAs` and `_InheritedUDAs` attributes: `<UDAInfo><Attribute Name="Start" DataType="MxBoolean" IsArray="false" InheritedFromTagName="" .../></UDAInfo>`.
- Extensions are listed as XML in the `Extensions` and `_InheritedExtensions` attributes: `<ExtensionInfo><ObjectExtension>...</ObjectExtension><AttributeExtension><Attribute Name="Cmd" ExtensionType="outputextension" .../></AttributeExtension></ExtensionInfo>`. The I/O extension types are `inputextension`, `outputextension` and `inputoutputextension`.
- `IAttribute.Description` reads `No Data` when there is no description. `IAttribute.EngUnits` fails with OLE_E_NOSTORAGE on templates.
- `IAttribute.UpperBoundDim1` is -1 for attributes that are not arrays.

## Changing templates (verified on Galaxy_test)

- The sequence is `CheckOut()`, the changes, `Save()`, `CheckIn(comment)`. `ObjectEdits` in `src\Common` wraps it.
- After `Save()`, `UndoCheckOut()` fails with "Object is being edited by <user>" (reported as checked out to someone else). Calling `Unload()`, which reports the same error, and then `UndoCheckOut()` again works. `ObjectEdits.UndoCheckOut` does this.
- `AddUDA` accepts only `MxCategoryCalculated` and `MxCategoryWriteable_USC_Lockable` (User writeable), plus `MxCategoryWriteable_C_Lockable` for InternationalizedString. It rejects `MxCategoryWriteable_S` (Object writeable), even though the IDE can create it. A name that already exists is rejected ("conflicts with another attribute").
- `UpdateUDA(name, ...)` changes the data type and category of a UDA defined in the template. On a Boolean it also removes the Boolean labels.
- `AddExtensionPrimitive(type, attributeName, false)` adds an I/O extension (`inputextension`, `outputextension`, `inputoutputextension`); input adds `.InputSource`, output adds `.OutputDest`. `DeleteExtensionPrimitive(type, attributeName)` removes one.
- Boolean labels: write `<CmdData><BooleanLabel><Attribute Name="X"/></BooleanLabel></CmdData>` to `_CmdAdd`. This creates `X.OnMsg`, `X.OffMsg` and `X.Msg` exactly like the IDE. `_CmdAdd` only handles Boolean labels, and writing `CmdData` directly reports an error.
- Descriptions and engineering units: set `attribute.Description = text` or `attribute.EngUnits = text` on the UDA. The help only mentions reading these properties, but the interop has setters. GRAccess then creates `X.Description` or `X.EngUnits` the way the IDE does: a "User extended" attribute (`dynamic_attribute_type` 4 in the galaxy database) that is not listed as a UDA and can be locked with `SetLocked`. Engineering units only work on numeric attributes; on a Boolean nothing happens.
- Do not create them with `AddUDA("X.Description", ...)`: that makes a separate, ordinary UDA (type 1) that the IDE lists on its own, even though the IDE's Description field then shows its value.
- Set InternationalizedString values with `PutInternationalString(1033, text)`; `PutString` is rejected ("Value must be a valid string").
- `SetLocked(MxLockedInMe)` locks a value in the template; derived objects show it as `MxLockedInParent`.
- Locking the I/O block in the IDE locks the extension's lockable settings: `X.InputSource`, `X.OutputDest`, `X.OutputEveryScan`, `X.InvertValue` (Booleans), `X.Deadband` (numbers) and `X.DiffOutputDest` (input/output). Status attributes such as `X.ReadStatus`, `X.WriteStatus` and `X.WriteValue` are not lockable.
- `QueryObjects(kind, EConditionType.derivedOrInstantiatedFrom, tagname, EMatch.MatchCondition)` returns only direct children (templates or instances), so walk the tree to find every descendant.
- Changes reach derived templates and instances when the template is checked in, and reading them through GRAccess right afterwards shows the change. Deployed instances then need to be redeployed.
- `CheckIn` of a template fails with "Object or its descendent(s) is in use" while any derived template or instance is checked out. Nothing is checked in or propagated; undo the template's check-out, deal with the checked-out objects, and try again.
- In the galaxy database a UDA's `dynamic_attribute` rows exist only in the template that defines it; derived objects get the extension primitives (`primitive_instance`) but inherit the definition. Check propagation through GRAccess, not with SQL on `dynamic_attribute`.

## Instances, areas and I/O references

- A contained instance has its own tagname (e.g. `LSC3_SPControl_PumpVFD_CHWR`); `HierarchicalName` gives the dotted name (`LSC3_PumpVFDControl_CHWR.SetPointControl`). `QueryObjectsByName` and `EConditionType.NameEquals` only match tagnames. `EConditionType.hierarchicalNameLike` finds the dotted name, but it uses SQL LIKE, where `_` matches any character, so compare the results exactly.
- Contained templates are already named with dots (`$PumpVFDControl.SetPointControl`), and an instance's `DerivedFrom` returns that name.
- `QueryObjects(gObjectIsInstance, EConditionType.belongsToArea, area, EMatch.MatchCondition)` returns the objects directly in the area, contained objects included, but not the area itself. Sub-areas are the area objects (`category == idxCategoryArea`) among them, so walk them to cover a whole area tree.
- I/O references that come from templates are usually `---Auto---`, and GRAccess does not resolve them (not even the internal `IGalaxyFx.GetObjectReferences`). The system builds the path from the object's I/O device assignment: `<device>.<scan group>.<item>`, where the item comes from the naming rule (`<HierarchicalName>.<AttributeName>` by default). The assignments are only in the galaxy database: `object_device_linkage` (object, device and scan group primitive), `autobind_device` and `autobind_device_topic` (overridden naming rules) and `autobind_naming_rule_spec`. `ExportInstanceIO` reads them there.
- A device's scan group keeps its device items as XML in `ScanGroup.AliasDatabase` (an MxBigString): `<ItemsList><Item Name="AV:3002575:PRESENT-VALUE" Alias="LSC3_CH3.ACC_MAP_OUT_FREQ"/></ItemsList>`. `Alias` is the item name objects use and `Name` is the item reference (the address in the topic). A scan group without items has `<ItemsList></ItemsList>`. Verified on `$DDESuiteLinkClient`, `$aDDESuiteLinkClient`, `$OPCClient` and `$aRedundantDIObject` devices.
- EMGALAXY uses both ways of addressing. The `BACLite_DDESuiteLink` scan groups have device items, and objects use the item names (e.g. `LSC3_CH3.ACC_NSP`), so an item that is not one of them is unmapped. The redundant DI objects (`*_RDI`, `$aRedundantDIObject`) have no device items at all, and objects use the PLC addresses directly as items (e.g. `401016 F`, `F8:6`, `HMI_STATUS[8].1`); their primary and backup DI objects (`DISourcePrimary`, `DISourceBackup`, `$aDDESuiteLinkClient`) have no device items for those scan groups either. `ExportInstanceIO` therefore treats the item as the reference when its scan group has no device items. A scan group whose device items were never imported would look the same.
- `ScanGroup.ItemList` is a string array of the same item names; on EMGALAXY it matched the aliases in `AliasDatabase` exactly in every scan group. It is not a complete list of what is assigned to the scan group (`GPM_actual` and `GPM_SP` of `LSC3_PumpVFDControl_CHWR` are assigned but not listed, so they are unmapped). Read arrays with `value.GetDimensionSize(out size)` and `value.GetElement(i, element)` for `i` from 1 to `size`. Compare item names without case, like all ArchestrA names: the list has `LSC3_PumpVFDControl_CHWR.CMD` for the `Cmd` attribute.
- On an input/output extension, `X.DiffOutputDest` is true when the output writes to its own reference in `X.OutputDest`; otherwise `X.OutputDest` is `---` and the attribute reads and writes the `X.InputSource` reference.

## Graphics (verified on EMGALAXY)

- GRAccess has no call that lists the graphics that use an object. The undocumented `IGalaxyFx.GetObjectCrossReferences` misses embedded symbols and took minutes. `ArchestrA.Visualization.GraphicAccess.ExportGraphicToXml` exports one Graphic Toolbox symbol (only `$Symbol` ones, not symbols owned by objects) as XML, but took over 10 minutes for a large overview. `FindInstanceGraphics` and `ExportInstanceGraphics` read the galaxy database instead, with SELECT statements only (`GalaxyGraphics` in `src\Common`). Reading all of it takes a few seconds, the same for one instance or a whole area.
- Area membership is in `gobject.area_gobject_id`, so it can be read without GRAccess: an object contained in another object has its container's area, or the container itself when the container is an area (as in MU). Areas are `template_definition.category_id` 13. Keep to `namespace_id` 1: EMGALAXY also has three engine rows in namespace 2 with the same tagnames. Walking the areas this way gave the same instance counts as GRAccess `belongsToArea` for LifeSci3_ChilledWaterLoop (9), Radix (756), MU (1118) and theGalaxy (216).
- `gobject.namespace_id` is 1 for automation objects and 3 for Graphic Toolbox and OMI elements. `visual_element.visual_element_type` is `Symbol`, `Layout`, `ScreenProfile`, `DisplayModule`, `ClientControl` or `Widget`. OMI ViewApps are `template_definition.category_id` 17, InTouch ViewApps 26.
- `visual_element_version` has one row per graphic and package: Graphic Toolbox elements (`mx_primitive_id` 101 on EMGALAXY) and symbols owned by templates and instances (`primitive_instance.primitive_name` is the symbol name). `inherited_from_*` points to the version whose definition it uses. Only defining versions have an `owned_visual_element` row with the definition (`visual_element_definition`, binary; names in it are ASCII or UTF-16) and the cross-reference XML (`visual_element_crossRef`: `<Reference Name="Tag.Attr" CPName="" Type="..."/>`).
- `visual_element_reference` has one row per embedded symbol, including relative (`Me.`) ones: the embedding graphic (or a ViewApp template, whose InTouch windows or OMI content embed it) and the embedded version in `checked_in_bound_*` and `checked_out_bound_*`, or its name in `*_unbound_*` when it could not be bound. OMI layouts embed each other in cycles (e.g. `Layout_ECCP` and `Layout_Main`), so walk it with a visited set.
- `attribute_reference` has the tag references the galaxy resolved (`resolved_gobject_id`), from graphics (joined on `gobject_id`, `package_id`, `referring_mx_primitive_id` to `visual_element_version`) and from object scripts and I/O.
- Current versions are the object's `checked_in_package_id` and `checked_out_package_id`; other packages are history and deployed copies. A graphic saved in the IDE but not checked in is already in the checked-out package.
- Not in the database: edits not yet saved in the IDE, tag references typed into InTouch windows (files under `G:\ArchestrA\Framework\FileRepository\<galaxy>`; AVEVA is installed on G: on this machine), and OMI asset navigation, which shows an area's objects by browsing.

## C# 5

The only compiler on this machine is the .NET Framework 4.8 `csc.exe`, which supports C# 5. Code copied from newer examples often needs rewriting:

| Not available in C# 5 | Use instead |
|---|---|
| `$"Hello {name}"` | `"Hello " + name` or `string.Format` |
| `obj?.Name` | an explicit null check |
| `nameof(x)` | a string literal |
| `int X => 1;` and `public int X { get; } = 1;` | a full property |
| `out var x`, tuples, local functions | declared variables, small classes, private methods |

## PowerShell tips

- Run programs from the current folder with `.\`, e.g. `.\bin\ReadGalaxyProperty.exe`.
- Put names that start with `$` in single quotes: `-Object '$UserDefined'`. Without quotes, or in double quotes, PowerShell replaces them with an empty string.
- If `build.ps1` is blocked after the project was copied from another machine: `Get-ChildItem -Recurse *.ps1 | Unblock-File`.

## Minimal example without src\Common

Useful for trying something out quickly:

```csharp
using System;
using ArchestrA.GRAccess;

class Minimal
{
    [STAThread]
    static void Main()
    {
        GRAccessApp grAccess = new GRAccessApp();
        IGalaxy galaxy = grAccess.QueryGalaxies(Environment.MachineName)["TrainingGalaxy"];

        galaxy.Login("", "");
        if (!galaxy.CommandResult.Successful)
        {
            Console.WriteLine("Login failed: " + galaxy.CommandResult.CustomMessage);
            return;
        }

        string[] names = { "$UserDefined" };
        IgObjects objects = galaxy.QueryObjectsByName(EgObjectIsTemplateOrInstance.gObjectIsTemplate, ref names);
        Console.WriteLine(objects[1].Attributes["SecurityGroup"].value.GetString());

        galaxy.Logout();
        GC.KeepAlive(grAccess);
    }
}
```

Build it by hand:

```
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /reference:"C:\Program Files (x86)\Common Files\ArchestrA\ArchestrA.GRAccess.dll" /out:Minimal.exe Minimal.cs
```
