// <copyright file="RaklionFullCycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Raklion;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises real egg deaths, Selupan spawn, battle outcome, hatchery evacuation, and the next cycle.
/// </summary>
[TestFixture]
public class RaklionFullCycleTests
{
    /// <summary>
    /// Completes both a successful hunt and an abandoned battle without forcing event state or invoking death handlers.
    /// </summary>
    /// <param name="killSelupan">Whether the player kills Selupan or leaves the closed hatchery.</param>
    [TestCase(true)]
    [TestCase(false)]
    [Timeout(90_000)]
    public async Task EggsAndBossResolveThroughNaturalLifecycleAsync(bool killSelupan)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();

        // Only waiting times and the deterministic reward are compressed. All fifteen configured eggs,
        // actual monster identities, spawn areas, death subscriptions, and player-removal logic remain real.
        var definition = new RaklionEventDefinition
        {
            SelupanAppearanceDelay = TimeSpan.Zero,
            HatcheryCloseDelay = TimeSpan.Zero,
            HatcheryOpenDelay = TimeSpan.Zero,
            PlayerRemovalDelay = TimeSpan.Zero,
            SelupanSkillDelay = TimeSpan.FromHours(1),
        };
        var reward = configuration.Items.Single(item => item.Group == 14 && item.Number == 13);
        var bossDefinition = configuration.Monsters.Single(monster => monster.Number == 459);
        bossDefinition.DropItemGroups.Clear();
        var rewardGroup = new Persistence.BasicModel.DropItemGroup { Chance = 1, ItemLevel = 0 };
        rewardGroup.PossibleItems.Add(reward);
        bossDefinition.DropItemGroups.Add(rewardGroup);

        var plugIns = new PlugInManager([], NullLoggerFactory.Instance, null, null);
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(value => value.NextDouble()).Returns(0);
        randomizer.Setup(value => value.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int _) => minimum);
        randomizer.Setup(value => value.NextRandomBool(It.IsAny<double>())).Returns(true);
        var drops = new DefaultDropGenerator(configuration, randomizer.Object);
        var mediator = new ConfigurationChangeMediator();
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, drops, mediator);
        await using var gameContext = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance, plugIns, drops, mediator);
        initializer.PlugInManager = plugIns;
        initializer.PathFinderPool = gameContext.PathFinderPool;
        gameContext.StopPeriodicTasks();
        using var context = new RaklionContext(gameContext, definition);
        await context.InitializeAsync().ConfigureAwait(false);
        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        player.SelectedCharacter!.Name = "RaklionCycle";
        player.Attributes!.AddElement(new ConstantElement(1_000_000), Stats.AttackRatePvm);
        player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MinimumPhysBaseDmg);
        player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MaximumPhysBaseDmg);
        player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumHealth);
        player.Attributes[Stats.CurrentHealth] = 1_000_000;
        await gameContext.AddPlayerAsync(player).ConfigureAwait(false);
        var hatchery = (await gameContext.GetMapAsync((ushort)definition.HatcheryMapNumber).ConfigureAwait(false))!;
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IRaklionEventViewPlugIn>()!);

        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.Idle));
        Assert.That(context.RemainingSpiderEggs, Is.EqualTo(15));
        Assert.That(await context.CanEnterThroughGateAsync(player, hatchery.Definition).ConfigureAwait(false), Is.True);
        await player.WarpToAsync(hatchery.Definition.ExitGates.Single()).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);

        var eggs = hatchery.GetNpcsInRange(new Point(145, 31), 30).OfType<Monster>()
            .Where(monster => monster.SpawnArea.WaveNumber == definition.SpiderEggWaveNumber).ToList();
        Assert.That(eggs, Has.Count.EqualTo(15));
        foreach (var egg in eggs)
        {
            await KillAsync(player, egg).ConfigureAwait(false);
        }

        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.Standby));
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.StartBattle));
        var boss = hatchery.GetNpcsInRange(new Point(145, 31), 30).OfType<Monster>().Single(monster => monster.Definition.Number == 459);
        Assert.That(boss.IsAlive, Is.True);
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.CloseDoor));
        Assert.That(await context.CanEnterThroughGateAsync(player, hatchery.Definition).ConfigureAwait(false), Is.False);

        if (killSelupan)
        {
            await KillAsync(player, boss).ConfigureAwait(false);
            await WaitUntilAsync(
                () => hatchery.GetDropsInRange(boss.Position, 4).OfType<DroppedItem>().Any(drop => ReferenceEquals(drop.Item.Definition, reward)),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        else
        {
            await player.WarpToSafezoneAsync().ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        }

        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.Notify4));
        view.Verify(value => value.ShowBattleResultAsync(killSelupan), Times.Once);
        view.Verify(value => value.ShowBattleResultAsync(!killSelupan), Times.Never);
        Assert.That(hatchery.GetNpcsInRange(new Point(145, 31), 30).OfType<Monster>().Any(monster => monster.IsAlive && monster.Definition.Number == 459), Is.False);

        await context.TickAsync().ConfigureAwait(false); // Arm the normal delayed player evacuation.
        await context.TickAsync().ConfigureAwait(false); // Evacuate, then reopen.
        Assert.That(context.State, Is.EqualTo(RaklionState.End));
        Assert.That(player.CurrentMap, Is.Not.SameAs(hatchery));
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(RaklionState.Idle));
        Assert.That(context.RemainingSpiderEggs, Is.EqualTo(15), "A fresh cycle must spawn exactly its eggs without retaining the previous boss.");
        Assert.That(context.IsEventRunning, Is.False);
        Assert.That(context.CanEnterHatchery, Is.True);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, "The configured Raklion lifecycle did not reach its expected state in time.");
    }

    private static async Task KillAsync(Player player, Monster monster)
    {
        player.Position = monster.Position;
        for (var attempt = 0; attempt < 20 && monster.IsAlive; attempt++)
        {
            await monster.AttackByAsync(player, null, false).ConfigureAwait(false);
        }

        Assert.That(monster.IsAlive, Is.False, $"Normal attacks must kill monster {monster.Definition.Number}.");
    }
}
