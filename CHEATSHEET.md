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

- `-f <file.csv>` `template,name,Description,IO,dataType,label[,category]`
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

## RemoveTemplateAttributes

```powershell
.\bin\RemoveTemplateAttributes.exe -f config\topics\EMGALAXY\TemplateIssues\01-CHLR_STR-remove.csv
.\bin\RemoveTemplateAttributes.exe -f config\topics\EMGALAXY\TemplateIssues\01-CHLR_STR-remove.csv -Apply
```

- `-f <file.csv>` `template,name`
- `-Apply`

## SetAttributes

```powershell
.\bin\SetAttributes.exe -f config\topics\EMGALAXY\TemplateIssues\followup-1-Chiller.csv
.\bin\SetAttributes.exe -f config\topics\EMGALAXY\TemplateIssues\followup-1-Chiller.csv -Apply
```

- `-f <file.csv>` `object,attribute,set,to` (`set`: `value` or `lock`; `to`: the value, or `locked`/`unlocked`)
- `-Apply`

## SetDeviceItems

```powershell
.\bin\SetDeviceItems.exe -f config\topics\EMGALAXY\IOTopics\BACLite_DDESuiteLink-items.csv
.\bin\SetDeviceItems.exe -Galaxy TESTGALAXY -f <file.csv> -Apply
```

- `-f <file.csv>` `device,scanGroup,item,reference` (a scan group's only row may leave item and reference empty: no items)
- `-Apply` (only fills scan groups that have no items yet)
