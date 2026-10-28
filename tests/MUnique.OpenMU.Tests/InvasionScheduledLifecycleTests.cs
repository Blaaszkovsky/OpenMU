// <copyright file="InvasionScheduledLifecycleTests.cs" company="MUnique">
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
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Runs the installed invasions through the periodic scheduler, combat, reward, and natural expiry.
/// </summary>
[TestFixture]
public class InvasionScheduledLifecycleTests
{
    /// <summary>
    /// Keeps the real default spawn counts, maps, monsters, drops, and message policy throughout a full run.
    /// </summary>
    /// <param name="whiteWizard">Whether to run White Wizard instead of Red Dragon.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Timeout(60_000)]
    public async ValueTask PeriodicInvasionExpiresAfterRealBossDeathAsync(bool whiteWizard)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        BaseInvasionPlugIn<PeriodicInvasionConfiguration> invasion = whiteWizard
            ? new WhiteWizardInvasionPlugIn()
            : new RedDragonInvasionPlugIn();
        var schedule = (PeriodicInvasionConfiguration)((ISupportDefaultCustomConfiguration)invasion).CreateDefaultConfig();

        // Compress only scheduler waits. Retain all 5 dragons / 26 wizard corps, random eligible maps,
        // normal boss definitions and guaranteed default jewels; no lifecycle callbacks or outcome injection.
        schedule.PreStartMessageDelay = TimeSpan.FromMilliseconds(100);
        schedule.TaskDuration = TimeSpan.FromSeconds(5);
        invasion.Configuration = schedule;
        Assert.That(schedule.Mobs.Sum(spawn => spawn.Count), Is.EqualTo(whiteWizard ? 26 : 5));
        var bossNumber = whiteWizard ? 135 : 44;
        var bossDefinition = configuration.Monsters.Single(monster => monster.Number == bossNumber);
        var rewardGroup = bossDefinition.DropItemGroups.Single(group => group.Chance == 1);
        Assert.That(rewardGroup.PossibleItems.Any(item => item.Group == 14 && item.Number == 13), Is.True);

        // Deterministic choice within the existing default drop group, without changing its reward policy.
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(value => value.NextDouble()).Returns(0);
        randomizer.Setup(value => value.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int _) => minimum);
        randomizer.Setup(value => value.NextRandomBool(It.IsAny<double>())).Returns(true);
        var drops = new DefaultDropGenerator(configuration, randomizer.Object);
        // No automatic feature discovery: all map callbacks must use this same scheduled instance.
        var plugIns = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        plugIns.RegisterPlugInAtPlugInPoint<IObjectAddedToMapPlugIn>(invasion);
        var mediator = new ConfigurationChangeMediator();
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, drops, mediator);
        await using var context = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance, plugIns, drops, mediator);
        initializer.PlugInManager = plugIns;
        initializer.PathFinderPool = context.PathFinderPool;
        context.StopPeriodicTasks();

        var observers = new List<Player>();
        var maps = new List<GameMap>();
        foreach (var mapId in schedule.Mobs.SelectMany(spawn => spawn.MapIds).Distinct())
        {
            var map = (await context.GetMapAsync(mapId).ConfigureAwait(false))!;
            maps.Add(map);
            var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
            player.SelectedCharacter!.Name = $"Invasion{mapId}";
            player.Attributes!.AddElement(new ConstantElement(1_000_000), Stats.AttackRatePvm);
            player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MinimumPhysBaseDmg);
            player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MaximumPhysBaseDmg);
            player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumHealth);
            player.Attributes[Stats.CurrentHealth] = 1_000_000;
            await context.AddPlayerAsync(player).ConfigureAwait(false);
            await player.WarpToAsync(CreateObserverGate(map)).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!).Invocations.Clear();
            Mock.Get(player.ViewPlugIns.GetPlugIn<IMapEventStateUpdatePlugIn>()!).Invocations.Clear();
            observers.Add(player);
        }

        Monster[] ActiveMonsters() => maps.SelectMany(map => map.GetNpcsInRange(new Point(128, 128), 256))
            .OfType<Monster>().Where(monster => schedule.Mobs.Any(spawn => spawn.MonsterId == monster.Definition.Number)).ToArray();

        Assert.That(ActiveMonsters(), Is.Empty);
        invasion.ForceStart();
        await invasion.ExecuteTaskAsync(context).ConfigureAwait(false); // NotStarted -> Prepared.
        Assert.That(ActiveMonsters(), Is.Empty, "The real pre-start interval must precede spawning.");
        foreach (var observer in observers)
        {
            Assert.That(Messages(observer), Has.Length.EqualTo(1));
        }

        await invasion.ExecuteTaskAsync(context).ConfigureAwait(false);
        Assert.That(ActiveMonsters(), Is.Empty, "An early scheduler tick must retain Prepared.");
        await Task.Delay(schedule.PreStartMessageDelay + TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        await invasion.ExecuteTaskAsync(context).ConfigureAwait(false); // Prepared -> Started.
        var monsters = ActiveMonsters();
        Assert.That(monsters, Has.Length.EqualTo(whiteWizard ? 26 : 5));
        foreach (var spawn in schedule.Mobs)
        {
            var matching = monsters.Where(monster => monster.Definition.Number == spawn.MonsterId).ToArray();
            Assert.That(matching, Has.Length.EqualTo(spawn.Count));
            Assert.That(matching.All(monster => spawn.MapIds.Contains((ushort)monster.CurrentMap!.Definition.Number)), Is.True);
        }

        var invadedMap = monsters[0].CurrentMap!;
        Assert.That(monsters.All(monster => ReferenceEquals(monster.CurrentMap, invadedMap)), Is.True);
        var killer = observers.Single(player => ReferenceEquals(player.CurrentMap, invadedMap));
        var mapName = invadedMap.Definition.Name.GetTranslation(killer.Culture);
        var startMessage = schedule.StartMessage.GetTranslation(killer.Culture)!.Replace("{mapName}", mapName, StringComparison.InvariantCulture);
        var endMessage = schedule.EndMessage.GetTranslation(killer.Culture)!.Replace("{mapName}", mapName, StringComparison.InvariantCulture);
        Assert.That(Messages(killer), Is.EqualTo(new[] { startMessage }));
        var boss = monsters.First(monster => monster.Definition.Number == bossNumber);
        killer.Position = boss.Position;
        for (var attempt = 0; attempt < 20 && boss.IsAlive; attempt++)
        {
            await boss.AttackByAsync(killer, null, false).ConfigureAwait(false);
        }

        Assert.That(boss.IsAlive, Is.False, "The actual combat/death pipeline must defeat the configured invasion boss.");
        await WaitUntilAsync(() => invadedMap.GetDropsInRange(boss.Position, 4).OfType<DroppedItem>()
            .Any(drop => drop.Item.Definition is { Group: 14, Number: 13 }), TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        var deathMessage = killer.GetLocalizedMessage(nameof(PlayerMessage.InvasionMonsterDefeated),
            killer.SelectedCharacter!.Name, mapName, boss.Definition.Designation.GetTranslation(killer.Culture));
        if (whiteWizard)
        {
            await WaitUntilAsync(() => Messages(killer).Contains(deathMessage), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        else
        {
            Assert.That(Messages(killer), Does.Not.Contain(deathMessage), "The default Red Dragon spawn does not announce individual deaths.");
        }

        var remaining = monsters.Where(monster => !ReferenceEquals(monster, boss)).ToArray();
        await invasion.ExecuteTaskAsync(context).ConfigureAwait(false);
        Assert.That(remaining.All(monster => monster.IsAlive && !monster.IsDisposed), Is.True,
            "A boss kill must not shortcut the configured invasion duration or remove its remaining NPCs.");
        await WaitUntilAsync(async () =>
        {
            await invasion.ExecuteTaskAsync(context).ConfigureAwait(false);
            return remaining.All(monster => monster.IsDisposed);
        }, schedule.TaskDuration + TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert.That(DateTime.UtcNow - started, Is.GreaterThanOrEqualTo(schedule.TaskDuration));
        Assert.That(ActiveMonsters(), Is.Empty);
        foreach (var observer in observers)
        {
            var expected = whiteWizard ? new[] { startMessage, deathMessage, endMessage } : new[] { startMessage, endMessage };
            Assert.That(Messages(observer), Is.EqualTo(expected));
            var mapView = Mock.Get(observer.ViewPlugIns.GetPlugIn<IMapEventStateUpdatePlugIn>()!);
            var receivesMapState = !whiteWizard && ReferenceEquals(observer.CurrentMap, invadedMap);
            mapView.Verify(view => view.UpdateStateAsync(true, MapEventType.RedDragonInvasion), receivesMapState ? Times.Once() : Times.Never());
            mapView.Verify(view => view.UpdateStateAsync(false, MapEventType.RedDragonInvasion), receivesMapState ? Times.Once() : Times.Never());
        }

        // A subsequent tick after natural expiry must not respawn stale tracked monsters.
        await invasion.ExecuteTaskAsync(context).ConfigureAwait(false);
        Assert.That(ActiveMonsters(), Is.Empty);
    }

    /// <summary>
    /// A blocked first exit gate must not leave an invasion observer in the recovery map transition.
    /// </summary>
    [Test]
    public async Task ObserverEntersWorldWhenFirstExitGateIsBlockedAsync()
    {
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        context.StopPeriodicTasks();
        var definition = context.Configuration.Maps.Single();
        definition.ExitGates.Add(new Persistence.BasicModel.ExitGate { Map = definition, X1 = 5, X2 = 5, Y1 = 5, Y2 = 5 });
        definition.ExitGates.Add(new Persistence.BasicModel.ExitGate { Map = definition, X1 = 6, X2 = 6, Y1 = 5, Y2 = 5, IsSpawnGate = true });
        var map = (await context.GetMapAsync(0).ConfigureAwait(false))!;
        map.Terrain.WalkMap[5, 5] = false;
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        try
        {
            await player.WarpToAsync(CreateObserverGate(map)).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);

            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
            Assert.That(player.CurrentMap, Is.SameAs(map));
            Assert.That(map.Terrain.WalkMap[player.Position.X, player.Position.Y], Is.True);
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
            await map.TearDownAsync().ConfigureAwait(false);
        }
    }

    private static ExitGate CreateObserverGate(GameMap map)
    {
        var point = map.Terrain.GetWalkableCoordinate(map.SafeZoneSpawnGate)
            ?? map.Definition.ExitGates.Select(map.Terrain.GetWalkableCoordinate).FirstOrDefault(candidate => candidate.HasValue);
        Assert.That(point, Is.Not.Null, "An invasion observer needs a walkable entry tile.");
        return new ExitGate { Map = map.Definition, X1 = point!.Value.X, X2 = point.Value.X, Y1 = point.Value.Y, Y2 = point.Value.Y };
    }

    private static string[] Messages(Player player) => Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!).Invocations.ToArray()
        .Where(invocation => invocation.Method.Name == nameof(IShowMessagePlugIn.ShowMessageAsync)
            && invocation.Arguments[1] is MessageType.GoldenCenter)
        .Select(invocation => (string)invocation.Arguments[0]).ToArray();

    private static ValueTask WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        => WaitUntilAsync(() => new ValueTask<bool>(condition()), timeout);

    private static async ValueTask WaitUntilAsync(Func<ValueTask<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail("The scheduled invasion did not reach its expected natural state before the deadline.");
    }
}
