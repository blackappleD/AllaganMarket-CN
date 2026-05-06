using System;
using System.Collections.Generic;

using AllaganLib.Universalis.Models;

using AllaganMarket;
using AllaganMarket.Services;
using AllaganMarket.Services.Interfaces;

using Autofac;

using DalaMock.Core.Mocks;
using DalaMock.Shared.Interfaces;

using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AllaganMarketMock;

public class AllaganMarketPluginMock : AllaganMarketPlugin
{
    public AllaganMarketPluginMock(MockReplacementContainer mockReplacementContainer, IDalamudPluginInterface pluginInterface)
        : base(pluginInterface)
    {
        this.ReplacementContainer = mockReplacementContainer;
    }

    public override IReplacementContainer ReplacementContainer { get; }

    public override void ConfigureContainer(ContainerBuilder containerBuilder)
    {
        base.ConfigureContainer(containerBuilder);
        containerBuilder.RegisterType<MockRetainerService>().AsSelf().As<IRetainerService>().SingleInstance();
        containerBuilder.RegisterType<MockWindow>().AsSelf().As<Window>().SingleInstance();
        containerBuilder.RegisterType<MockCharacterWindow>().AsSelf().As<Window>().SingleInstance();
        containerBuilder.RegisterType<MockBootService>().AsSelf().AsImplementedInterfaces().SingleInstance();
        containerBuilder.Register<UniversalisUserAgent>(c =>
        {
            return new UniversalisUserAgent("AllaganMarket", "DEV");
        });
    }

    public override void ReplaceHostedServices(Dictionary<Type, Type> replacements)
    {
        replacements.Add(typeof(InventoryService), typeof(MockInventoryService));
        replacements.Add(typeof(GameInterfaceService), typeof(MockGameInterfaceService));
        replacements.Add(typeof(RetainerMarketService), typeof(MockRetainerMarketService));
        replacements.Add(typeof(RetainerService), typeof(MockRetainerService));
    }
}
