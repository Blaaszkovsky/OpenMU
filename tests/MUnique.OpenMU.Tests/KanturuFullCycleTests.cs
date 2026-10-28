// <copyright file="KanturuFullCycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.Kanturu;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Exercises the configured Maya waves, both hands, Nightmare, tower window, and natural map release.
/// </summary>
[TestFixture]
public class KanturuFullCycleTests
{
    /// <summary>
    /// Wins through actual monster deaths or loses through the first wave's timer, without forcing victory or ending the game.
    /// </summary>
    /// <param name="completeWaves">Whether the player kills the configured targets or lets the first wave expire.</param>
    [TestCase(true)]
    [TestCase(false)]
    [Timeout(300_000)]
    public async Task ConfiguredPhasesAndTowerResolveThroughNaturalLifecycleAsync(bool completeWaves)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var eventDefinition = KanturuEventDefinition.CreateDefault(configuration);
        var definition = configuration.MiniGameDefinitions.Single(candidate => candidate.Type == MiniGameType.Kanturu);
        definition.EnterDuration = TimeSpan.FromSeconds(30);
        definition.GameDuration = TimeSpan.FromMinutes(2);
        definition.ExitDuration = TimeSpan.Zero;

        // Retain all nine configured phases, original monster identities and wave sizes. Compress only
        // cinematics, standby, tower duration and the first failure timer. Combat is intentionally overpowered;
        // this tests lifecycle wiring, not balance or the separately covered Nightmare HP patterns.
        eventDefinition.IntroDuration = TimeSpan.Zero;
        eventDefinition.MayaAttackInterval = TimeSpan.Zero;
        eventDefinition.VictoryCinematicDuration = TimeSpan.Zero;
        eventDefinition.RequiredItemDurabilityLossInterval = TimeSpan.FromHours(1);
        eventDefinition.TowerClosingWarningOffset = TimeSpan.FromMilliseconds(100);
        foreach (var phase in eventDefinition.Phases)
        {
            phase.StartDelay = TimeSpan.Zero;
            phase.StandbyDuration = TimeSpan.FromMilliseconds(10);
            if (phase.TimeLimit is not null)
            {
                phase.TimeLimit = TimeSpan.FromSeconds(30);
            }

            if (phase.Duration is not null)
            {
                phase.Duration = TimeSpan.FromMilliseconds(100);
            }

            if (phase.Transition is { } transition)
            {
                transition.CinematicDuration = TimeSpan.FromMilliseconds(10);
                transition.WarpAnimationDelay = TimeSpan.Zero;
            }

            if (phase.Nightmare is { } nightmare)
            {
                nightmare.HealthCheckInterval = TimeSpan.FromMilliseconds(10);
                nightmare.SpecialAttackInterval = TimeSpan.Zero;
            }
        }

        if (!completeWaves)
        {
            eventDefinition.Phases[0].TimeLimit = TimeSpan.FromMilliseconds(200);
        }

        var startConfiguration = new KanturuStartConfiguration
        {
            EventDefinition = eventDefinition,
            TowerOpenDuration = TimeSpan.FromSeconds(30),
        };
        var plugIns = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var startPlugIn = new KanturuStartPlugIn { Configuration = startConfiguration };
        plugIns.RegisterPlugInAtPlugInPoint<IPeriodicMiniGameStartPlugIn>(startPlugIn);
        var reward = configuration.Items.Single(item => item.Group == 14 && item.Number == 13);
        var nightmareDefinition = eventDefinition.Phases.Single(phase => phase.Kind == KanturuPhaseKind.Nightmare).Nightmare!.Monster!;
        nightmareDefinition.DropItemGroups.Clear();
        var rewardGroup = new Persistence.BasicModel.DropItemGroup { Chance = 1, ItemLevel = 0 };
        rewardGroup.PossibleItems.Add(reward);
        nightmareDefinition.DropItemGroups.Add(rewardGroup);

        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(value => value.NextDouble()).Returns(0);
        randomizer.Setup(value => value.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int _) => minimum);
        randomizer.Setup(value => value.NextRandomBool(It.IsAny<double>())).Returns(true);
        var drops = new DefaultDropGenerator(configuration, randomizer.Object);
        var mediator = new ConfigurationChangeMediator();
        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, drops, mediator);
        var eventLog = new EventLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(eventLog));
        await using var gameContext = new GameContext(configuration, provider, initializer, loggerFactory, plugIns, drops, mediator);
        initializer.PlugInManager = plugIns;
        initializer.PathFinderPool = gameContext.PathFinderPool;
        gameContext.StopPeriodicTasks();
        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        player.SelectedCharacter!.Name = "KanturuCycle";
        var homeMap = (await gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        var homePoint = homeMap.Terrain.GetWalkableCoordinate(homeMap.SafeZoneSpawnGate);
        Assert.That(homePoint, Is.Not.Null, "The test character needs a valid home-map spawn point before normal inventory actions.");
        var homeGate = new ExitGate
        {
            Map = homeMap.Definition,
            X1 = homePoint!.Value.X,
            X2 = homePoint.Value.X,
            Y1 = homePoint.Value.Y,
            Y2 = homePoint.Value.Y,
        };
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        player.IsAlive = true;
        await player.WarpToAsync(homeGate).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        Assert.That(player.CurrentMap, Is.SameAs(homeMap), "The character must finish its initial world warp before equipping the admission item.");
        player.Attributes![Stats.Level] = 350;
        player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.AttackRatePvm);
        player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MinimumPhysBaseDmg);
        player.Attributes.AddElement(new ConstantElement(10_000_000), Stats.MaximumPhysBaseDmg);
        player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumHealth);
        player.Attributes[Stats.CurrentHealth] = 1_000_000;
        var pendant = configuration.Items.Single(item => item.Group == 13 && item.Number == 38);
        // PlayerTestHelper supplies a detached mock character class. Use a real initialized class so
        // the ordinary equipment action can validate the Moonstone's QualifiedCharacters relation.
        Assert.That(pendant.QualifiedCharacters, Is.Not.Empty, "The initialized Moonstone must have eligible character classes.");
        player.SelectedCharacter!.CharacterClass = pendant.QualifiedCharacters.First();
        Assert.That(pendant.ItemSlot?.ItemSlots, Does.Contain((int)InventoryConstants.Ring1Slot), "The initialized Moonstone Pendant is configured for the ring equipment slots.");
        var equippedMoonstone = player.PersistenceContext.CreateNew<Item>();
        equippedMoonstone.Definition = pendant;
        equippedMoonstone.Durability = 120;
        Assert.That(await player.Inventory!.AddItemAsync(20, equippedMoonstone).ConfigureAwait(false), Is.True);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld), "Normal inventory actions require the player to be in the world.");
        Assert.That(player.CurrentMiniGame, Is.Null, "The Moonstone must be equipped before entering the event.");
        Assert.That(player.CompliesRequirements(equippedMoonstone), Is.True, "The selected character must satisfy the initialized Moonstone's level/class requirements.");
        await new MoveItemAction().MoveItemAsync(player, 20, Storages.Inventory, InventoryConstants.Ring1Slot, Storages.Inventory).ConfigureAwait(false);
        Assert.That(player.Inventory.GetItem(InventoryConstants.Ring1Slot), Is.SameAs(equippedMoonstone), "The player must equip the Moonstone through the normal inventory action.");
        var equippedSlots = string.Join(",", player.Inventory.EquippedItems.Select(item => $"{item.ItemSlot}:{item.Definition?.Group}/{item.Definition?.Number}"));
        var basePowerUps = string.Join(",", pendant.BasePowerUpAttributes.Select(powerUp => powerUp.TargetAttribute?.Designation ?? "<null>"));
        var effectivePowerUps = player.Attributes.ItemPowerUps.TryGetValue(equippedMoonstone, out var appliedPowerUps)
            ? string.Join(",", appliedPowerUps)
            : "<none>";
        var trackedItems = string.Join(",", player.Attributes.ItemPowerUps.Select(entry => $"{entry.Key.ItemSlot}:{entry.Key.Definition?.Group}/{entry.Key.Definition?.Number}={string.Join(";", entry.Value)}"));
        var sameInventoryItem = player.Inventory.EquippedItems.Any(item => ReferenceEquals(item, equippedMoonstone));
        Assert.That(
            player.Attributes[Stats.MoonstonePendantEquipped],
            Is.GreaterThanOrEqualTo(1),
            $"The actual equipped Moonstone item must satisfy map admission. DefinitionSlots=[{string.Join(",", pendant.ItemSlot?.ItemSlots ?? [])}], BasePowerUps=[{basePowerUps}], EffectivePowerUps=[{effectivePowerUps}], TrackedItems=[{trackedItems}], SameInventoryItem={sameInventoryItem}, InventoryType={player.Inventory.AsInventoryStorage?.GetType().Name}, Equipped=[{equippedSlots}].");
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IKanturuEventViewPlugIn>()!);

        // The scheduler normally creates the shared lobby. Only lobby/countdown waits use its public test/GM
        // skip API. No playing-phase wait, kill target, victory flag or cleanup is skipped.
        startPlugIn.ForceStart();
        await startPlugIn.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        await startPlugIn.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        var game = (KanturuContext)await gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
        try
        {
            await new EnterMiniGameAction().TryEnterMiniGameAsync(player, MiniGameType.Kanturu, 1, byte.MaxValue).ConfigureAwait(false);
            Assert.That(player.CurrentMiniGame, Is.SameAs(game), "The normal admission action must enter the configured shared lobby.");
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            // Kanturu has a 30-second minimum entrance window. Let it elapse naturally before
            // skipping the minute-long countdown; an earlier observation of the open state is not
            // evidence that the entrance phase is ready to close.
            await WaitUntilAsync(
                () => game.State == MiniGameState.Closed,
                TimeSpan.FromSeconds(40),
                () => $"CurrentState={game.State}, CurrentKanturuState={game.CurrentKanturuState}, DetailState={game.CurrentKanturuDetailState}, PlayerCount={game.PlayerCount}, IsDisposed={game.IsDisposed}, Errors=[{string.Join(" | ", eventLog.Errors)}]").ConfigureAwait(false);
            await WaitUntilAsync(() => game.State == MiniGameState.Closed && game.SkipCurrentWait(), TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            if (completeWaves)
            {
                foreach (var phase in eventDefinition.Phases.Where(phase => phase.SpawnWaveNumber is not null && phase.Duration is null))
                {
                    await WaitUntilAsync(() => game.CurrentKanturuState == phase.State
                        && (game.CurrentKanturuDetailState == phase.DetailState || game.CurrentKanturuDetailState == phase.Nightmare?.BattleDetailState)
                        && GetLivingWaveMonsters(game, phase.SpawnWaveNumber!.Value).Count >= phase.KillTarget, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    var targets = GetLivingWaveMonsters(game, phase.SpawnWaveNumber!.Value);
                    Assert.That(targets.Count(monster => phase.CountedMonsters.Any(counted => counted.Number == monster.Definition.Number)), Is.GreaterThanOrEqualTo(phase.KillTarget));
                    foreach (var monster in targets)
                    {
                        await KillAsync(player, monster).ConfigureAwait(false);
                    }

                    if (phase.Kind == KanturuPhaseKind.Nightmare)
                    {
                        await WaitUntilAsync(
                            () => targets.Any(target => game.Map.GetDropsInRange(target.Position, 4).OfType<DroppedItem>().Any(drop => ReferenceEquals(drop.Item.Definition, reward))),
                            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }

                await WaitUntilAsync(() => game.CurrentKanturuState == KanturuState.Tower, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                view.Verify(value => value.ShowBattleResultAsync(true), Times.Once);
                Assert.That(startConfiguration.TowerOpenUntilUtc, Is.GreaterThan(DateTime.UtcNow));
                foreach (var phase in eventDefinition.Phases)
                {
                    view.Verify(value => value.ShowStateChangeAsync(phase.State, phase.DetailState), Times.AtLeastOnce);
                }
            }

            await WaitUntilAsync(() => game.State == MiniGameState.Ended, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            view.Verify(value => value.ShowBattleResultAsync(false), completeWaves ? Times.Never() : Times.Once());
            view.Verify(value => value.ShowBattleResultAsync(true), completeWaves ? Times.Once() : Times.Never());
            Assert.That(startConfiguration.TowerOpenUntilUtc, Is.Null, "A closed or lost tower must not retain its re-entry window.");
            await WaitUntilAsync(() => game.IsDisposed && game.Map.IsTornDown, TimeSpan.FromSeconds(80)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(game.State, Is.EqualTo(MiniGameState.Disposed));
                Assert.That(game.PlayerCount, Is.Zero);
                Assert.That(player.CurrentMiniGame, Is.Null);
                Assert.That(player.CurrentMap, Is.Not.SameAs(game.Map));
            });
        }
        finally
        {
            if (!game.IsDisposed)
            {
                await game.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static List<Monster> GetLivingWaveMonsters(KanturuContext game, byte wave)
        => game.Map.GetNpcsInRange(new Point(127, 127), 255).OfType<Monster>()
            .Where(monster => monster.IsAlive && monster.SpawnArea.WaveNumber == wave).ToList();

    private static async Task KillAsync(Player player, Monster monster)
    {
        player.Position = monster.Position;
        for (var attempt = 0; attempt < 20 && monster.IsAlive; attempt++)
        {
            await monster.AttackByAsync(player, null, false).ConfigureAwait(false);
        }

        Assert.That(monster.IsAlive, Is.False, $"Normal attacks must kill monster {monster.Definition.Number}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string>? failureDetails = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reached = condition();
        while (!reached && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
            reached = condition();
        }

        Assert.That(reached, Is.True, $"The configured Kanturu lifecycle did not reach its expected state in time. {failureDetails?.Invoke()}");
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
