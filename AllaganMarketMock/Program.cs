using System.IO;

using Autofac;

using DalaMock.Core.Configuration;
using DalaMock.Core.Plugin;
using DalaMock.Core.Windows;

namespace AllaganMarketMock;

internal class Program
{
    private static void Main(string[] args)
    {
        var dalamudConfiguration = new MockDalamudConfiguration()
        {
            GamePath = new DirectoryInfo("/var/home/blair/.xlcore/ffxiv/game/sqpack"),
            PluginSavePath = new DirectoryInfo("/var/home/blair/.xlcore/pluginConfigs"),
        };
        var mockContainer = new MockContainer(dalamudConfiguration);
        var mockDalamudUi = mockContainer.GetMockUi();
        var pluginLoader = mockContainer.GetPluginLoader();
        var mockPlugin = pluginLoader.AddPlugin(typeof(AllaganMarketPluginMock));
        mockDalamudUi.Run();
    }
}
