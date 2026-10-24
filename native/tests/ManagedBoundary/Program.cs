using System;
using Playkitu.Producer;

// Actual managed loader and production/legacy-export fixture, CREATED only.
// No settings, credentials, SDK start, devices, tickets or cloud operations.
if(args.Length!=2)return 2;
var current=new ProductionNativeRun(args[0]);
current.Load();
if(current.Error!=null || !current.StopAndRelease())throw new Exception("Current trusted-region loader failed: "+current.Error);
var old=new ProductionNativeRun(args[1]);
old.Load();
if(old.Error!="native_region_unsupported" || !old.StopAndRelease())throw new Exception("Legacy capability did not fail closed");
Console.WriteLine("PASS managed_native_capability_checks=4 created_only=1");
return 0;
