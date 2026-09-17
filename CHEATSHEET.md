# Cheat sheet

All tools: `-Galaxy <name>` `-Node <name>` `-User <name>` `-Help`

```powershell
.\build.ps1
.\build.ps1 -Clean
```

## AddInstances

```powershell
.\bin\AddInstances.exe -f config\AddInstances-test.csv
.\bin\AddInstances.exe -f config\AddInstances-test.csv -Apply
```

- `-f <file.csv>` `template,name,area,io`
- `-Apply`

## AddTemplateAttributes

```powershell
.\bin\AddTemplateAttributes.exe -f config\AddTemplateAttributes-test.csv
.\bin\AddTemplateAttributes.exe -f config\AddTemplateAttributes-test.csv -Apply
.\bin\AddTemplateAttributes.exe -f config\AddTemplateAttributes-test.csv -o -Apply
```

- `-f <file.csv>` `template,name,Description,IO,dataType,label`
- `-o` update existing
- `-Apply`

## ExportInstanceGraphics

```powershell
.\bin\ExportInstanceGraphics.exe -i ECCP_CH1_FlowMeter_CHWS
.\bin\ExportInstanceGraphics.exe -a Radix
```

- `-i <instance>`
- `-a <area>`

## ExportInstanceIO

```powershell
.\bin\ExportInstanceIO.exe -i LSC3_PumpVFDControl_CHWR.SetPointControl
.\bin\ExportInstanceIO.exe -a ECCP_ChilledWaterLoop
```

- `-i <instance>`
- `-a <area>`

## ExportTemplateAttributes

```powershell
.\bin\ExportTemplateAttributes.exe -t Pump
.\bin\ExportTemplateAttributes.exe -d Radix/Equipment/Pump
.\bin\ExportTemplateAttributes.exe -d Radix/Equipment -r
```

- `-t <template>`
- `-d <toolset/path>`
- `-r` with `-d`, include sub-toolsets

## FindInstanceGraphics

```powershell
.\bin\FindInstanceGraphics.exe -i ECCP_CH1_FlowMeter_CHWS
```

- `-i <instance>`

## ReadGalaxyProperty

```powershell
.\bin\ReadGalaxyProperty.exe
.\bin\ReadGalaxyProperty.exe -Object '$UserDefined' -Attribute CodeBase
```

- `-Object <tagname>`
- `-Attribute <name>`
