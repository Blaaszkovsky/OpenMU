// <copyright file="ConfiguredBoxLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Opens every configured droppable box through the ordinary inventory drop action.
/// </summary>
[TestFixture]
public class ConfiguredBoxLifecycleTests
{
    /// <summary>
    /// Each supported source level consumes one box; a repeated request cannot grant another drop.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task EveryConfiguredDroppableBoxOpensOnceAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var persistence = provider.CreateNewContext();
        var configuration = (await persistence.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var randomizer = new Mock<IRandomizer>();
        var choices = new Random(60023);
        randomizer.Setup(value => value.NextDouble()).Returns(0);
        randomizer.Setup(value => value.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int maximum) => choices.Next(minimum, maximum));
        randomizer.Setup(value => value.NextRandomBool(It.IsAny<double>())).Returns(true);
        var drops = new DefaultDropGenerator(configuration, randomizer.Object);
        var plugIns = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        plugIns.RegisterPlugInAtPlugInPoint<IItemDropPlugIn>(new ItemBoxDroppedPlugIn());
        var mediator = new ConfigurationChangeMediator();
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, drops, mediator);
        await using var context = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance, plugIns, drops, mediator);
        initializer.PlugInManager = plugIns;
        initializer.PathFinderPool = context.PathFinderPool;
        context.StopPeriodicTasks();
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        player.Attributes![Stats.Level] = 400;
        var map = (await context.GetMapAsync(0).ConfigureAwait(false))!;
        var point = map.Terrain.GetWalkableCoordinate(map.SafeZoneSpawnGate)!.Value;
        await player.WarpToAsync(new ExitGate { Map = map.Definition, X1 = point.X, X2 = point.X, Y1 = point.Y, Y2 = point.Y }).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        var cases = 0;
        foreach (var definition in configuration.Items.Where(item => item.IsDroppable && item.DropItems.Count > 0))
        {
            foreach (var level in definition.DropItems.Select(group => group.SourceItemLevel).Distinct())
            {
                var box = player.PersistenceContext.CreateNew<Item>();
                box.Definition = definition;
                box.Level = checked((byte)level);
                box.Durability = 1;
                Assert.That(await player.Inventory!.AddItemAsync(20, box).ConfigureAwait(false), Is.True);
                var effect = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowItemDropEffectPlugIn>()!);
                effect.Invocations.Clear();
                var groups = definition.DropItems.Where(group => group.SourceItemLevel == level).ToArray();
                var before = map.GetDropsInRange(new Point(128, 128), 256).ToArray();
                await new DropItemAction().DropItemAsync(player, 20, point).ConfigureAwait(false);
                Assert.That(player.Inventory.GetItem(20), Is.Null, $"{definition.Group}/{definition.Number} level {level} must open through the normal drop action.");
                var afterFirst = map.GetDropsInRange(new Point(128, 128), 256).ToArray();
                if (groups.All(group => group.ItemType == SpecialItemType.None))
                {
                    effect.Verify(view => view.ShowEffectAsync(It.Is<ItemDropEffect>(value => groups.Any(group => group.DropEffect == value)), point), Times.Once);
                }
                else
                {
                    Assert.That(afterFirst.Except(before), Is.Not.Empty, $"{definition.Group}/{definition.Number} level {level} must generate its configured reward when the reward roll succeeds.");
                }

                var effectCount = effect.Invocations.Count;
                await new DropItemAction().DropItemAsync(player, 20, point).ConfigureAwait(false);
                Assert.That(map.GetDropsInRange(new Point(128, 128), 256), Is.EquivalentTo(afterFirst), "Repeating the consumed box's request must not add another reward.");
                Assert.That(effect.Invocations, Has.Count.EqualTo(effectCount), "Repeating a consumed fireworks item must not repeat its effect.");
                TestContext.Progress.WriteLine($"Opened {definition.Group}/{definition.Number} level {level}: {definition.Name}");
                cases++;
            }
        }

        Assert.That(cases, Is.EqualTo(33), "Keep the initialized box/source-level catalog complete when adding or changing configured rewards.");
    }
}
