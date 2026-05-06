using AllaganLib.Shared.Services;

using AllaganMarket.Services.Interfaces;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

using Microsoft.Extensions.Logging;

namespace AllaganMarket.Services;

/// <summary>
/// Wrapper for the item order module's retainer ID.
/// </summary>
public class RetainerService(ILogger<RetainerService> logger, IFramework framework, IObjectTable objectTable) : HostedFrameworkService(logger, framework), IRetainerService
{
    private ulong retainerId;
    public uint RetainerWorldId => objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;

    public ulong RetainerId => this.retainerId;

    public unsafe uint RetainerGil => this.RetainerId == 0 ? 0 : InventoryManager.Instance()->GetRetainerGil();

    public override unsafe void FrameworkOnUpdate(IFramework framework)
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->UIModule;
        if (uiModule == null)
        {
            this.retainerId = 0;
            return;
        }

        var module = uiModule->GetItemOrderModule();
        if (module == null)
        {
            this.retainerId = 0;
            return;
        }

        this.retainerId = module->ActiveRetainerId;
    }
}
