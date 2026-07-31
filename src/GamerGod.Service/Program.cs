using System.Runtime.Versioning;
using GamerGod.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: SupportedOSPlatform("windows")]

// gmsvc.exe. The name is the installer's, not a choice: install/Install-GamerGod.ps1 looks
// for exactly this file and skips service registration cleanly when it is missing.
//
// It runs as a Windows service in production and as an ordinary console program when started
// by hand, which is the difference between debugging boot recovery and guessing at it.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "GamerGodService");

// Built here rather than in the service so that reading this machine's topology, which can
// fail on hardware nobody has tested, cannot stop the host from starting.
builder.Services.AddSingleton(_ => LedgerRecoveryPass.ForThisMachine());
builder.Services.AddHostedService<RecoveryService>();

var host = builder.Build();
await host.RunAsync();
