# CsWinRT marshal surface probe v2 (ASCII only for PS 5.1)
$ErrorActionPreference = 'Stop'
$asmPath = 'D:\Window\src\WinCanvas.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\WinRT.Runtime.dll'
$asm = [Reflection.Assembly]::LoadFrom($asmPath)
foreach ($name in @('WinRT.MarshalInspectable`1', 'WinRT.CastExtensions', 'WinRT.IWinRTObject', 'WinRT.IObjectReference')) {
    $t = $asm.GetType($name)
    if ($null -eq $t) { Write-Output ("NOT FOUND: " + $name); continue }
    Write-Output ("FOUND: " + $t.FullName)
    if ($name -like '*MarshalInspectable*') {
        foreach ($m in $t.GetMethods([Reflection.BindingFlags]'Public,Static')) {
            Write-Output ("  static: " + $m.ToString())
        }
    }
}
