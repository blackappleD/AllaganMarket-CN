using System;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

using Microsoft.Extensions.Hosting;

namespace AllaganMarket.Services;

/// <summary>
/// Exposes the automatic undercut to other plugins over Dalamud IPC.
/// <list type="bullet">
/// <item><c>AllaganMarket.ApiVersion</c>: <c>Func&lt;int&gt;</c>.</item>
/// <item><c>AllaganMarket.AutoUndercut.Start</c>: <c>Func&lt;bool&gt;</c>, reprices the retainers whose auto-undercut box is ticked.</item>
/// <item><c>AllaganMarket.AutoUndercut.StartForRetainers</c>: <c>Func&lt;ulong[], bool&gt;</c>, reprices exactly the given retainer ids of the active character.</item>
/// <item><c>AllaganMarket.AutoUndercut.Cancel</c>: <c>Action</c>.</item>
/// <item><c>AllaganMarket.AutoUndercut.IsRunning</c>: <c>Func&lt;bool&gt;</c>.</item>
/// <item><c>AllaganMarket.AutoUndercut.EnabledRetainerCount</c>: <c>Func&lt;int&gt;</c>, how many of the active character's retainers have the auto-undercut box ticked.</item>
/// <item><c>AllaganMarket.AutoUndercut.IsBusy</c>: <c>Func&lt;bool&gt;</c>, true while any automation of this plugin is running.</item>
/// <item><c>AllaganMarket.AutoUndercut.Started</c>: event, no arguments.</item>
/// <item><c>AllaganMarket.AutoUndercut.Finished</c>: event, <c>bool</c> = every selected retainer was visited.</item>
/// </list>
/// Start returns false when nothing was started: another automation is running, no character is logged in,
/// or no retainer matched. When RetainerList is closed the run interacts with a summoning bell in reach to open it,
/// and gives up (Finished(false)) when there is none or it does not open.
/// </summary>
public sealed class IpcService : IHostedService
{
    private const int ApiVersion = 1;
    private const string Prefix = "AllaganMarket.";

    private readonly IFramework framework;
    private readonly IPluginLog pluginLog;
    private readonly AutoUndercutService autoUndercutService;
    private readonly AutoListingService autoListingService;
    private readonly MannequinRestockService mannequinRestockService;

    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<bool> startProvider;
    private readonly ICallGateProvider<ulong[], bool> startForRetainersProvider;
    private readonly ICallGateProvider<object> cancelProvider;
    private readonly ICallGateProvider<bool> isRunningProvider;
    private readonly ICallGateProvider<bool> isBusyProvider;
    private readonly ICallGateProvider<int> enabledRetainerCountProvider;
    private readonly ICallGateProvider<object> startedProvider;
    private readonly ICallGateProvider<bool, object> finishedProvider;

    public IpcService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog pluginLog,
        AutoUndercutService autoUndercutService,
        AutoListingService autoListingService,
        MannequinRestockService mannequinRestockService)
    {
        this.framework = framework;
        this.pluginLog = pluginLog;
        this.autoUndercutService = autoUndercutService;
        this.autoListingService = autoListingService;
        this.mannequinRestockService = mannequinRestockService;

        this.apiVersionProvider = pluginInterface.GetIpcProvider<int>(Prefix + "ApiVersion");
        this.startProvider = pluginInterface.GetIpcProvider<bool>(Prefix + "AutoUndercut.Start");
        this.startForRetainersProvider = pluginInterface.GetIpcProvider<ulong[], bool>(Prefix + "AutoUndercut.StartForRetainers");
        this.cancelProvider = pluginInterface.GetIpcProvider<object>(Prefix + "AutoUndercut.Cancel");
        this.isRunningProvider = pluginInterface.GetIpcProvider<bool>(Prefix + "AutoUndercut.IsRunning");
        this.isBusyProvider = pluginInterface.GetIpcProvider<bool>(Prefix + "AutoUndercut.IsBusy");
        this.enabledRetainerCountProvider = pluginInterface.GetIpcProvider<int>(Prefix + "AutoUndercut.EnabledRetainerCount");
        this.startedProvider = pluginInterface.GetIpcProvider<object>(Prefix + "AutoUndercut.Started");
        this.finishedProvider = pluginInterface.GetIpcProvider<bool, object>(Prefix + "AutoUndercut.Finished");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.apiVersionProvider.RegisterFunc(() => ApiVersion);
        this.startProvider.RegisterFunc(() => this.StartAutoUndercut(null));
        this.startForRetainersProvider.RegisterFunc(retainerIds => this.StartAutoUndercut(retainerIds ?? []));
        this.cancelProvider.RegisterAction(this.autoUndercutService.Cancel);
        this.isRunningProvider.RegisterFunc(() => this.autoUndercutService.IsRunning);
        this.isBusyProvider.RegisterFunc(this.IsBusy);
        this.enabledRetainerCountProvider.RegisterFunc(this.autoUndercutService.CountAutoUndercutRetainers);
        this.autoUndercutService.Started += this.OnStarted;
        this.autoUndercutService.Finished += this.OnFinished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.autoUndercutService.Started -= this.OnStarted;
        this.autoUndercutService.Finished -= this.OnFinished;
        this.apiVersionProvider.UnregisterFunc();
        this.startProvider.UnregisterFunc();
        this.startForRetainersProvider.UnregisterFunc();
        this.cancelProvider.UnregisterAction();
        this.isRunningProvider.UnregisterFunc();
        this.isBusyProvider.UnregisterFunc();
        this.enabledRetainerCountProvider.UnregisterFunc();
        return Task.CompletedTask;
    }

    private bool IsBusy()
    {
        return this.autoUndercutService.IsRunning || this.autoListingService.IsRunning ||
               this.mannequinRestockService.IsRestocking;
    }

    private bool StartAutoUndercut(ulong[]? retainerIds)
    {
        if (this.IsBusy())
        {
            this.pluginLog.Information("IPC: automatic undercut not started, another automation is running.");
            return false;
        }

        var started = this.autoUndercutService.Start(retainerIds);
        this.pluginLog.Information($"IPC: automatic undercut start requested, started = {started}.");
        return started;
    }

    private void OnStarted()
    {
        this.framework.RunOnFrameworkThread(() => this.startedProvider.SendMessage());
    }

    private void OnFinished(bool completed)
    {
        this.framework.RunOnFrameworkThread(() => this.finishedProvider.SendMessage(completed));
    }
}
