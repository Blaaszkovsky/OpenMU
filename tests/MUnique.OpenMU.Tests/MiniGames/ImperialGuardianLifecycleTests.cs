// <copyright file="ImperialGuardianLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.ImperialGuardian;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises actual Imperial Guardian map waves, kills, boss drop, experience reward, and natural map release.
/// </summary>
[TestFixture]
public class ImperialGuardianLifecycleTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Enters Monday's configured event with its required ticket and party, kills one real configured monster
    /// in each zone including the boss, receives its fragment and XP, then observes natural map teardown.
    /// </summary>
    [Test]
    [Timeout(100_000)]
    public async Task ConfiguredWavesBossRewardAndNaturalExitCompleteAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var eventMapDefinition = configuration.Maps.Single(map => map.Number == 69);
        var targetMonsterByWave = new Dictionary<byte, short> { [10] = 512, [11] = 513, [12] = 508 };
        var gateNumbers = new HashSet<short> { 524, 525, 526, 527, 528 };

        // Retain the real Monday zone templates and all configured gates, but keep one actual wave monster
        // per zone so this lifecycle regression completes quickly without substituting success or kills.
        foreach (var spawn in eventMapDefinition.MonsterSpawns
                     .Where(spawn => spawn.SpawnTrigger == SpawnTrigger.OnceAtWaveStart && targetMonsterByWave.ContainsKey(spawn.WaveNumber))
                     .ToList())
        {
            var number = spawn.MonsterDefinition?.Number;
            if (number is null || (!gateNumbers.Contains(number.Value) && number.Value != targetMonsterByWave[spawn.WaveNumber]))
            {
                eventMapDefinition.MonsterSpawns.Remove(spawn);
            }
            else
            {
                spawn.Quantity = 1;
            }
        }

        var eventDefinition = new ImperialGuardianEventDefinition
        {
            FixedDay = 1,
            StandbyDuration = TimeSpan.FromMilliseconds(10),
            ZoneDuration = TimeSpan.FromSeconds(20),
            ExperienceRewards = [new ImperialGuardianExperienceReward { MaximumPlayerLevel = int.MaxValue, Experience = 25_000 }],
            MonsterScalings = [],
        };
        var plugInManager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var featurePlugIn = new ImperialGuardianFeaturePlugIn { Configuration = eventDefinition };
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
        character.Name = "ImperialGuardianLifecyclePlayer";
        player.Attributes![Stats.Level] = 150;
        player.Attributes.AddElement(new SimpleElement(1_000_000, AggregateType.AddRaw), Stats.AttackRatePvm);
        player.Attributes.AddElement(new SimpleElement(100_000, AggregateType.AddRaw), Stats.MinimumPhysBaseDmg);
        player.Attributes.AddElement(new SimpleElement(100_000, AggregateType.AddRaw), Stats.MaximumPhysBaseDmg);
        player.Attributes.AddElement(new SimpleElement(1_000_000, AggregateType.AddRaw), Stats.MaximumHealth);
        player.Attributes[Stats.CurrentHealth] = 1_000_000;
        var ticket = configuration.Items.Single(item => item.Group == 14 && item.Number == 102);
        var ticketItem = player.PersistenceContext.CreateNew<Item>();
        ticketItem.Definition = ticket;
        ticketItem.Durability = 1;
        Assert.That(await inventory.AddItemAsync(20, ticketItem).ConfigureAwait(false), Is.True);
        var party = gameContext.PartyManager.CreateParty();
        Assert.That(await party.AddAsync(player).ConfigureAwait(false), Is.True);

        ImperialGuardianContext? gameToDispose = null;
        try
        {
            var activeFeaturePlugIn = gameContext.FeaturePlugIns.GetPlugIn<ImperialGuardianFeaturePlugIn>();
            Assert.That(activeFeaturePlugIn, Is.Not.Null, "The configured Imperial Guardian feature plugin must be active in this game context.");
            Assert.That(activeFeaturePlugIn!.Configuration, Is.SameAs(eventDefinition), "The event context must use the fixture's configured wave/reward definition.");
            await new EnterImperialGuardianAction().TryEnterAsync(player).ConfigureAwait(false);
            var enteredGame = player.CurrentMiniGame as ImperialGuardianContext;
            Assert.That(enteredGame, Is.Not.Null, "The production Imperial Guardian action must admit the ticketed party member.");
            var game = enteredGame!;
            gameToDispose = game;
            Assert.That(inventory.Items.Any(item => ReferenceEquals(item.Definition, ticket)), Is.False);
            Assert.That(game.ZoneCount, Is.EqualTo(3), "All three Monday waves must remain configured on the initialized map.");
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            await WaitForPlayingAsync(game).ConfigureAwait(false);

            var earlyGate = await WaitForNpcAsync<ImperialGuardianGate>(game, npc => npc.Definition.Number == 525, PhaseTimeout, eventLog).ConfigureAwait(false);
            await WaitUntilAsync(() => earlyGate.IsAttackable, PhaseTimeout).ConfigureAwait(false);
            Assert.That(earlyGate.IsAttackable, Is.True, "The first-zone gate becomes attackable after its wave starts.");

            await KillWaveMonsterAsync(game, player, targetMonsterByWave[10], eventLog).ConfigureAwait(false);
            await WaitUntilAsync(() => game.Zone == 1 && game.RemainingMonsterCount == 1, PhaseTimeout).ConfigureAwait(false);
            await KillWaveMonsterAsync(game, player, targetMonsterByWave[11], eventLog).ConfigureAwait(false);
            await WaitUntilAsync(() => game.Zone == 2 && game.RemainingMonsterCount == 1, PhaseTimeout).ConfigureAwait(false);

            var boss = await WaitForNpcAsync<Monster>(game, npc => npc.Definition.Number == targetMonsterByWave[12], PhaseTimeout, eventLog).ConfigureAwait(false);
            var bossPosition = boss.Position;
            await KillWithRealAttackAsync(player, boss).ConfigureAwait(false);
            Assert.That(boss.IsAlive, Is.False, "The final zone's configured boss must die from a player attack.");

            var fragmentDefinition = configuration.Items.Single(item => item.Group == 14 && item.Number == 103);
            await WaitUntilAsync(
                () => game.Map.GetDropsInRange(bossPosition, 10).OfType<DroppedItem>().Any(drop => ReferenceEquals(drop.Item.Definition, fragmentDefinition)),
                PhaseTimeout).ConfigureAwait(false);

            var startingExperience = character.Experience;
            await WaitUntilAsync(
                () => game.State == MiniGameState.Ended && character.Experience >= startingExperience + 25_000,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            await WaitUntilAsync(() => game.IsDisposed && game.Map.IsTornDown, TimeSpan.FromSeconds(80)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(game.State, Is.EqualTo(MiniGameState.Disposed));
                Assert.That(game.PlayerCount, Is.Zero);
                Assert.That(game.Map.IsTornDown, Is.True);
                Assert.That(player.CurrentMiniGame, Is.Null);
                Assert.That(player.CurrentMap, Is.Not.SameAs(game.Map));
                Assert.That(character.CurrentMap, Is.SameAs(game.Map.Definition.SafezoneMap), "Natural event exit must return the character to its configured safezone map.");
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

        Assert.That(sawClosed, Is.True, "The event loop must close registration before starting the first zone.");
    }

    private static async Task KillWaveMonsterAsync(ImperialGuardianContext game, Player player, short monsterNumber, EventLogProvider eventLog)
    {
        var monster = await WaitForNpcAsync<Monster>(game, npc => npc.Definition.Number == monsterNumber, PhaseTimeout, eventLog).ConfigureAwait(false);
        player.Position = monster.Position;
        await KillWithRealAttackAsync(player, monster).ConfigureAwait(false);
        Assert.That(monster.IsAlive, Is.False, $"Configured wave monster {monsterNumber} must die through normal combat.");
    }

    private static async Task<TNpc> WaitForNpcAsync<TNpc>(MiniGameContext game, Func<TNpc, bool> predicate, TimeSpan timeout, EventLogProvider eventLog)
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

        var gameState = game is ImperialGuardianContext imperialGuardian
            ? $" Day={imperialGuardian.Day}, Zone={imperialGuardian.Zone}, RemainingMonsters={imperialGuardian.RemainingMonsterCount}"
            : string.Empty;
        Assert.Fail($"No matching {typeof(TNpc).Name} appeared. State={game.State}, Map={game.Map.MapId}, Players={game.PlayerCount},{gameState}, NPCs=[{string.Join(",", observedNpcs.Select(npc => npc.Definition.Number))}], Errors=[{string.Join(" | ", eventLog.Errors)}].");
        return null!;
    }

    private static async Task KillWithRealAttackAsync(Player player, IAttackable attackable)
    {
        if (attackable is NonPlayerCharacter npc)
        {
            player.Position = npc.Position;
        }

        for (var attempt = 0; attempt < 20 && attackable.IsAlive; attempt++)
        {
            await attackable.AttackByAsync(player, null, false).ConfigureAwait(false);
        }

        Assert.That(attackable.IsAlive, Is.False, "A configured high-damage normal attack must cause the target's real death path.");
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
