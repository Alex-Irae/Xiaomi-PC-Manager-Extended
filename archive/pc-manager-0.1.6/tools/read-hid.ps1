# Purpose: inventory HID collections without sending reports or changing devices.
# Dependencies: Windows PowerShell and Windows HID/SetupAPI. Output: JSON collection descriptors.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/read-hid.ps1 -OutputPath results/path/hid.json
param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ReadOnlyHid {
 [StructLayout(LayoutKind.Sequential)] struct Interface { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }
 [StructLayout(LayoutKind.Sequential)] struct Caps {
  public ushort Usage, Page, Input, Output, Feature;
  [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
  public ushort Nodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
 }
 public sealed class Collection { public string Path; public int Page, Usage, Input, Output, Feature; }
 public static Collection[] Read() {
  Guid guid; HidD_GetHidGuid(out guid);
  IntPtr set=SetupDiGetClassDevsW(ref guid,IntPtr.Zero,IntPtr.Zero,18);
  var list=new List<Collection>();
  if(set==new IntPtr(-1)) throw new InvalidOperationException("HID inventory unavailable");
  try {
   var di=new Interface { Size=Marshal.SizeOf(typeof(Interface)) };
   for(int i=0;SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref di);i++) {
    int size; SetupDiGetDeviceInterfaceDetailW(set,ref di,IntPtr.Zero,0,out size,IntPtr.Zero);
    if(size<=0) continue;
    IntPtr detail=Marshal.AllocHGlobal(size);
    try {
     Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
     if(!SetupDiGetDeviceInterfaceDetailW(set,ref di,detail,size,out size,IntPtr.Zero)) continue;
     string path=Marshal.PtrToStringUni(detail+4);
     using(var file=CreateFileW(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
      IntPtr data;
      if(file.IsInvalid||!HidD_GetPreparsedData(file,out data)) continue;
      try { Caps c; if(HidP_GetCaps(data,out c)==0x110000) list.Add(new Collection {Path=path,Page=c.Page,Usage=c.Usage,Input=c.Input,Output=c.Output,Feature=c.Feature}); }
      finally { HidD_FreePreparsedData(data); }
     }
    } finally { Marshal.FreeHGlobal(detail); }
   }
  } finally { SetupDiDestroyDeviceInfoList(set); }
  return list.ToArray();
 }
 [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
 [DllImport("hid.dll")][return:MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle h,out IntPtr data);
 [DllImport("hid.dll")][return:MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
 [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data,out Caps caps);
 [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevsW(ref Guid guid,IntPtr enumerator,IntPtr hwnd,int flags);
 [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr info,ref Guid guid,int index,ref Interface di);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,ref Interface di,IntPtr detail,int size,out int required,IntPtr info);
 [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
}
'@
$taskCollections=@([ReadOnlyHid]::Read())
@{time=(Get-Date).ToString('o');protocol='Read-only HID capabilities, zero device reports';collections=$taskCollections;keyboardBacklightCollections=@($taskCollections | Where-Object {$_.Page -eq 12 -and $_.Usage -eq 7})} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
