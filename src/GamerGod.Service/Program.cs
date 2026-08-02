using System.Runtime.Versioning;
using GamerGod.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

// Registered as a singleton and then handed to the host, rather than AddHostedService<T>()
// building its own: the watchdog waits on this instance's RecoveryCompleted, and two instances
// would mean it waits for a boot pass that is not the one that ran.
builder.Services.AddSingleton<RecoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecoveryService>());

// Article X's third escape path. Undoes a session whose program has died, without waiting for
// the next reboot. It needs no channel from that program: the journal already records who owns
// each session, durably, before the first change is applied.
builder.Services.AddHostedService(sp => new WatchdogService(
    sp.GetRequiredService<IBootRecoveryPass>(),
    sp.GetRequiredService<RecoveryService>(),
    sp.GetRequiredService<ILogger<WatchdogService>>()));

var host = builder.Build();
await host.RunAsync();
