// Purpose: diagnose .NET Framework environment inheritance under Codex.
// Dependencies: Windows csc and .NET Framework; outputs exception or PASS.
// Compile: csc /target:exe EnvironmentProbe.cs; run EnvironmentProbe.exe.
using System;using System.Diagnostics;class EnvironmentProbe{static void Main(){try{var p=new ProcessStartInfo();p.EnvironmentVariables["PSModulePath"]="C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\Modules";Console.WriteLine("PASS: environment initialized");}catch(Exception e){Console.WriteLine(e.ToString());}}}
