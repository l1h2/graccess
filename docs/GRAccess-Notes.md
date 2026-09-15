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

## Changing objects (from the AVEVA sample, not yet verified here)

CheckOut, then change the object (for example `attribute.SetValue(mxValue)` or `AddUDA`), then `Save()` and `CheckIn(comment)`. Call `UndoCheckOut()` if something fails. Deployed instances that were changed still need to be redeployed.

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
