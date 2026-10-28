// <copyright file="DoppelgangerLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AttributeSystem;
using System.Collections.Concurrent;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.Doppelganger;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises a configured Doppelganger run through spawn, monster kill, reward chests, and natural map release.
/// </summary>
[TestFixture]
public class DoppelgangerLifecycleTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Enters through the production ticket action, kills an actual path monster, opens both configured
    /// reward chests, then lets the event timer and exit countdown release the map naturally.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task MonsterKillAndRewardChestOpeningCompleteNaturalLifecycleAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var eventDefinition = DoppelgangerEventDefinition.CreateDefault(configuration);
        var butcher = configuration.Monsters.Single(monster => monster.Number == 530);

        // Keep the real spawn/death/chest path while removing unrelated herd, ice-walker, and minute-based spawns.
        eventDefinition.HerdMonsters.Clear();
        eventDefinition.HerdSizes.Clear();
        eventDefinition.HerdInterval = TimeSpan.FromHours(1);
        eventDefinition.AdditionalSpawns = [new DoppelgangerMonsterSpawn { SpawnTime = TimeSpan.Zero, Monsters = [butcher] }];
        eventDefinition.IceWalkerSpawnTime = TimeSpan.FromHours(1);
        eventDefinition.InterimChestMonsters = [butcher];
        eventDefinition.InterimChestCount = 1;
        eventDefinition.LarvaChance = 0;
        eventDefinition.MonsterScalings.Clear();

        foreach (var definition in configuration.MiniGameDefinitions.Where(definition => definition.Type == MiniGameType.Doppelganger))
        {
            definition.EnterDuration = TimeSpan.FromSeconds(30);
            definition.GameDuration = TimeSpan.FromSeconds(30);
            definition.ExitDuration = TimeSpan.Zero;
        }

        var plugInManager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var featurePlugIn = new DoppelgangerFeaturePlugIn { Configuration = eventDefinition };
        plugInManager.RegisterPlugInAtPlugInPoint<IFeaturePlugIn>(featurePlugIn);
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(value => value.NextDouble()).Returns(0);
        randomizer.Setup(value => value.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int _) => minimum);
        randomizer.Setup(value => value.NextRandomBool(It.IsAny<double>())).Returns(true);
        var dropGenerator = new DefaultDropGenerator(configuration, randomizer.Object);
        var changeMediator = new ConfigurationChangeMediator();
        var mapInitializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, dropGenerator, changeMediator);
        var eventLog = new EventLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(eventLog));
        var gameContext = new GameContext(configuration, provider, mapInitializer, loggerFactory, plugInManager, dropGenerator, changeMediator);
        mapInitializer.PlugInManager = plugInManager;
        mapInitializer.PathFinderPool = gameContext.PathFinderPool;
        gameContext.StopPeriodicTasks();

        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        var inventory = player.Inventory!;
        character.Name = "DoppelgangerLifecyclePlayer";
        player.Attributes![Stats.Level] = 1;
        player.Attributes.AddElement(new SimpleElement(1_000_000, AggregateType.AddRaw), Stats.AttackRatePvm);
        player.Attributes.AddElement(new SimpleElement(100_000, AggregateType.AddRaw), Stats.MinimumPhysBaseDmg);
        player.Attributes.AddElement(new SimpleElement(100_000, AggregateType.AddRaw), Stats.MaximumPhysBaseDmg);
        player.Attributes.AddElement(new SimpleElement(1_000_000, AggregateType.AddRaw), Stats.MaximumHealth);
        player.Attributes[Stats.CurrentHealth] = 1_000_000;
        var ticket = configuration.Items.Single(item => item.Group == 13 && item.Number == 125);
        var ticketItem = player.PersistenceContext.CreateNew<Item>();
        ticketItem.Definition = ticket;
        ticketItem.Durability = 1;
        Assert.That(await inventory.AddItemAsync(20, ticketItem).ConfigureAwait(false), Is.True);

        DoppelgangerContext? gameToDispose = null;
        try
        {
            var activeFeaturePlugIn = gameContext.FeaturePlugIns.GetPlugIn<DoppelgangerFeaturePlugIn>();
            Assert.That(activeFeaturePlugIn, Is.Not.Null, "The configured Doppelganger feature plugin must be active in this game context.");
            Assert.That(activeFeaturePlugIn!.Configuration, Is.SameAs(eventDefinition), "The event context must use the fixture's configured spawn/path/reward definition.");
            await new EnterDoppelgangerAction().TryEnterAsync(player, byte.MaxValue).ConfigureAwait(false);
            var enteredGame = player.CurrentMiniGame as DoppelgangerContext;
            Assert.That(enteredGame, Is.Not.Null, "The configured entry action must admit the player and consume the ticket.");
            var game = enteredGame!;
            gameToDispose = game;
            Assert.That(inventory.Items.Any(item => ReferenceEquals(item.Definition, ticket)), Is.False);
            var path = eventDefinition.Paths.SingleOrDefault(candidate => candidate.MapNumber == game.Map.MapId);
            Assert.That(path, Is.Not.Null, $"The selected Doppelganger map {game.Map.MapId} must have its configured monster route.");
            var startingArea = path!.Areas[0];
            Point? walkablePoint = null;
            for (var x = startingArea.X1 + 1; x <= startingArea.X2 && walkablePoint is null; x++)
            {
                for (var y = startingArea.Y1 + 1; y <= startingArea.Y2; y++)
                {
                    if (game.Map.Terrain.WalkMap[x, y])
                    {
                        walkablePoint = new Point((byte)x, (byte)y);
                        break;
                    }
                }
            }

            Assert.That(walkablePoint, Is.Not.Null, $"The configured starting area {startingArea} on Doppelganger map {game.Map.MapId} must contain a walkable tile.");
            var startingPoint = walkablePoint!.Value;
            path.Areas[0] = new DoppelgangerPathArea((byte)(startingPoint.X - 1), (byte)(startingPoint.Y - 1), startingPoint.X, startingPoint.Y);
            player.Position = startingPoint;
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            await WaitForPlayingAsync(game).ConfigureAwait(false);

            var spawnedMonster = await WaitForNpcAsync<Monster>(game, player, npc => npc.Definition.Number == butcher.Number, PhaseTimeout, eventLog).ConfigureAwait(false);
            Assert.That(spawnedMonster.IsAlive, Is.True, "The event's real additional-spawn loop must create the configured butcher.");
            await KillWithRealAttackAsync(player, spawnedMonster).ConfigureAwait(false);
            Assert.That(spawnedMonster.IsAlive, Is.False, "The normal attack path must cause the monster death event.");

            var interimChest = await WaitForNpcAsync<Destructible>(game, player, npc => npc.Definition.Number == 541, PhaseTimeout, eventLog).ConfigureAwait(false);
            Assert.That(game.IsRewardChest(interimChest), Is.True);
            var interimChestPosition = interimChest.Position;
            var interimRewardItems = interimChest.Definition.DropItemGroups.SelectMany(group => group.PossibleItems).ToHashSet();
            player.Position = interimChestPosition;
            await game.OpenRewardChestAsync(player, interimChest).ConfigureAwait(false);
            Assert.That(game.Map.GetNpcsInRange(interimChest.Position, 1), Does.Not.Contain(interimChest), "Opening an interim chest must consume it through the event API.");
            Assert.That(
                game.Map.GetDropsInRange(interimChestPosition, 2).OfType<DroppedItem>().Any(drop => drop.Item.Definition is { } itemDefinition && interimRewardItems.Contains(itemDefinition)),
                Is.True,
                "Opening the configured interim chest must create one of its configured item rewards.");

            await WaitUntilAsync(() => game.State == MiniGameState.Ended, TimeSpan.FromSeconds(50)).ConfigureAwait(false);
            var finalChest = await WaitForNpcAsync<Destructible>(game, player, npc => npc.Definition.Number == 542, PhaseTimeout, eventLog).ConfigureAwait(false);
            Assert.That(game.IsRewardChest(finalChest), Is.True, "A successful natural timeout must spawn the final reward chest.");
            var finalChestPosition = finalChest.Position;
            var finalRewardItems = finalChest.Definition.DropItemGroups.SelectMany(group => group.PossibleItems).ToHashSet();
            player.Position = finalChestPosition;
            await game.OpenRewardChestAsync(player, finalChest).ConfigureAwait(false);
            Assert.That(game.Map.GetNpcsInRange(finalChest.Position, 1), Does.Not.Contain(finalChest), "The final reward chest must be opened and removed.");
            Assert.That(
                game.Map.GetDropsInRange(finalChestPosition, 2).OfType<DroppedItem>().Any(drop => drop.Item.Definition is { } itemDefinition && finalRewardItems.Contains(itemDefinition)),
                Is.True,
                "Opening the configured final chest must create one of its configured item rewards.");

            await WaitUntilAsync(() => game.IsDisposed && game.Map.IsTornDown, TimeSpan.FromSeconds(80)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(game.State, Is.EqualTo(MiniGameState.Disposed));
                Assert.That(game.PlayerCount, Is.Zero);
                Assert.That(game.Map.IsTornDown, Is.True);
                Assert.That(player.CurrentMiniGame, Is.Null);
                Assert.That(player.CurrentMap, Is.Not.SameAs(game.Map));
            });
        }
        finally
        {
            if (gameToDispose is { IsDisposed: false })
            {
                await gameToDispose.DisposeAsync().ConfigureAwait(false);
            }

            await gameContext.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForPlayingAsync(MiniGameContext game)
    {
        var sawClosed = false;
        await WaitUntilAsync(() =>
        {
            sawClosed |= game.State == MiniGameState.Closed;
            if (game.State != MiniGameState.Playing)
            {
                game.SkipCurrentWait();
                return false;
            }

            return true;
        }, PhaseTimeout).ConfigureAwait(false);

        Assert.That(sawClosed, Is.True, "The game loop must close registration before starting play.");
    }

    private static async Task<TNpc> WaitForNpcAsync<TNpc>(MiniGameContext game, Player player, Func<TNpc, bool> predicate, TimeSpan timeout, EventLogProvider eventLog)
        where TNpc : NonPlayerCharacter
    {
        var deadline = DateTime.UtcNow + timeout;
        IList<NonPlayerCharacter> observedNpcs = [];
        while (DateTime.UtcNow < deadline)
        {
            observedNpcs = game.Map.GetNpcsInRange(new Point(128, 128), 255);
            if (observedNpcs.OfType<TNpc>().FirstOrDefault(predicate) is { } result)
            {
                return result;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail($"No matching {typeof(TNpc).Name} appeared. State={game.State}, Map={game.Map.MapId}, Players={game.PlayerCount}, PlayerAlive={player.IsAlive}, PlayerPosition={player.Position}, PlayerMap={player.CurrentMap?.MapId}, NPCs=[{string.Join(",", observedNpcs.Select(npc => npc.Definition.Number))}], Errors=[{string.Join(" | ", eventLog.Errors)}].");
        return null!;
    }

    private static async Task KillWithRealAttackAsync(Player player, Monster monster)
    {
        player.Position = monster.Position;
        for (var attempt = 0; attempt < 20 && monster.IsAlive; attempt++)
        {
            await monster.AttackByAsync(player, null, false).ConfigureAwait(false);
        }

        Assert.That(monster.IsAlive, Is.False, $"A configured high-damage normal attack must kill monster {monster.Definition.Number}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, "The event did not reach the expected real lifecycle state in time.");
    }

    private sealed class EventLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IEnumerable<string> Errors => this._errors.ToArray();

        public ILogger CreateLogger(string categoryName) => new EventLogger(categoryName, this._errors);

        public void Dispose()
        {
        }

        private sealed class EventLogger(string category, ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (this.IsEnabled(logLevel))
                {
                    errors.Enqueue($"{category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
