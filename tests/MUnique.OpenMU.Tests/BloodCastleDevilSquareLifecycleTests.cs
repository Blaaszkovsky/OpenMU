// <copyright file="BloodCastleDevilSquareLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests real game-loop lifecycle, reward, event-wave, and ranking paths for Blood Castle and Devil Square.
/// </summary>
[TestFixture]
public class BloodCastleDevilSquareLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly List<MiniGameContext> _gamesToDispose = [];

    /// <summary>
    /// Disposes created game contexts and tears down their temporary maps.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var game in this._gamesToDispose)
        {
            if (!game.IsDisposed)
            {
                await game.DisposeAsync().ConfigureAwait(false);
            }
        }

        this._gamesToDispose.Clear();
    }

    /// <summary>
    /// Turns in the Archangel quest item during play and receives the Blood Castle winner reward.
    /// </summary>
    [Test]
    public async Task BloodCastleTurnInCompletesLifecycleAndRewardsWinnerAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var game = this.CreateGame<BloodCastleContext>(player, MiniGameType.BloodCastle, MiniGameSuccessFlags.Winner, 1_000);
        await EnterAndStartAsync(game, player).ConfigureAwait(false);
        var startingMoney = player.Money;

        // Keep this test focused on the event lifecycle and final hand-in; the quest item is seeded directly.
        Assert.That(await player.Inventory!.AddItemAsync(10, new Item { Definition = new ItemDefinition { Group = 13, Number = 19 } }).ConfigureAwait(false), Is.True);
        var bloodCastle = (BloodCastleContext)game;
        await bloodCastle.TalkToNpcArchangelAsync(player).ConfigureAwait(false);

        await WaitUntilAsync(() => game.State == MiniGameState.Ended && player.Money == startingMoney + 1_000).ConfigureAwait(false);
        Assert.That(player.Inventory.Items.Any(item => item.Definition?.Group == 13 && item.Definition.Number == 19), Is.False);

        await game.DisposeAsync().ConfigureAwait(false);
        Assert.That(game.Map.IsTornDown, Is.True);
        Assert.That(player.CurrentMiniGame, Is.Null);
    }

    /// <summary>
    /// Ends Blood Castle without an Archangel turn-in and receives only its no-winner reward.
    /// </summary>
    [Test]
    public async Task BloodCastleWithoutTurnInCompletesAsFailureAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var game = this.CreateGame<BloodCastleContext>(player, MiniGameType.BloodCastle, MiniGameSuccessFlags.WinnerNotExists, 250);
        await EnterAndStartAsync(game, player).ConfigureAwait(false);
        var startingMoney = player.Money;

        FinishAsGameMaster(game);
        await WaitUntilAsync(() => game.State == MiniGameState.Ended && player.Money == startingMoney + 250).ConfigureAwait(false);

        await game.DisposeAsync().ConfigureAwait(false);
        Assert.That(game.Map.IsTornDown, Is.True);
        Assert.That(player.CurrentMiniGame, Is.Null);
    }

    /// <summary>
    /// Completes a Devil Square run and awards its survivor reward through the real game context.
    /// </summary>
    [Test]
    public async Task DevilSquareCompletionRewardsSurvivorAndTearsDownMapAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var game = this.CreateGame<DevilSquareContext>(player, MiniGameType.DevilSquare, MiniGameSuccessFlags.WinnerNotExists, 500);
        await EnterAndStartAsync(game, player).ConfigureAwait(false);
        var startingMoney = player.Money;

        FinishAsGameMaster(game);
        await WaitUntilAsync(() => game.State == MiniGameState.Ended && player.Money == startingMoney + 500).ConfigureAwait(false);

        await game.DisposeAsync().ConfigureAwait(false);
        Assert.That(game.Map.IsTornDown, Is.True);
        Assert.That(player.CurrentMiniGame, Is.Null);
    }

    /// <summary>
    /// Runs a Devil Square wave to the configured event timeout, awards the higher-ranked killer,
    /// and verifies that the game loop disposes the event map without an explicit DisposeAsync call.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DevilSquareWaveKillScoresRankAndAutomaticallyDisposesAfterTimeoutAsync()
    {
        var winner = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var otherPlayer = await PlayerTestHelper.CreatePlayerAsync(winner.GameContext).ConfigureAwait(false);
        Assert.That(winner.SelectedCharacter, Is.Not.Null);
        Assert.That(otherPlayer.SelectedCharacter, Is.Not.Null);
        var winnerCharacter = winner.SelectedCharacter!;
        var otherCharacter = otherPlayer.SelectedCharacter!;
        winnerCharacter.Name = "DsWaveWinner";
        otherCharacter.Name = "DsWaveOther";
        Assert.Multiple(() =>
        {
            Assert.That(winnerCharacter.Name, Is.Not.Empty);
            Assert.That(otherCharacter.Name, Is.Not.Empty);
            Assert.That(winnerCharacter.Name, Is.Not.EqualTo(otherCharacter.Name));
        });

        var spawnWave = new MiniGameSpawnWave { WaveNumber = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(30) };
        // The persistence model initializes Attributes; a bare configuration model has a null
        // Attributes collection, which MonsterAttributeHolder enumerates during Initialize().
        var monsterDefinition = new BasicModel.MonsterDefinition
        {
            Id = Guid.NewGuid(),
            Number = 777,
            ObjectKind = NpcObjectKind.Monster,
        };
        monsterDefinition.Attributes.Add(new BasicModel.MonsterAttribute
        {
            AttributeDefinition = MUnique.OpenMU.GameLogic.Attributes.Stats.MaximumHealth,
            Value = 1,
        });
        var spawnArea = new MonsterSpawnArea
        {
            MonsterDefinition = monsterDefinition,
            GameMap = winner.GameContext.Configuration.Maps.First(map => map.Number == 0),
            WaveNumber = spawnWave.WaveNumber,
            SpawnTrigger = SpawnTrigger.AutomaticDuringWave,
            Quantity = 1,
            MaximumHealthOverride = 1,
            X1 = 10,
            Y1 = 10,
            X2 = 10,
            Y2 = 10,
        };
        var rewards = new List<MiniGameReward>
        {
            new() { RewardType = MiniGameRewardType.Money, RewardAmount = 500, RequiredSuccess = MiniGameSuccessFlags.WinnerNotExists, Rank = 1 },
            new() { RewardType = MiniGameRewardType.Money, RewardAmount = 200, RequiredSuccess = MiniGameSuccessFlags.WinnerNotExists, Rank = 2 },
        };
        var spawnedMonster = new TaskCompletionSource<Monster>(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = this.CreateGame<DevilSquareContext>(
            winner,
            MiniGameType.DevilSquare,
            MiniGameSuccessFlags.WinnerNotExists,
            0,
            rewards,
            [spawnWave],
            TimeSpan.FromSeconds(30),
            maximumPlayerCount: 2,
            configureMapInitializer: initializer => initializer
                .Setup(mapInitializer => mapInitializer.InitializeNpcsOnWaveStartAsync(It.IsAny<GameMap>(), It.IsAny<IEventStateProvider>(), It.IsAny<byte>()))
                .Returns<GameMap, IEventStateProvider, byte>((map, eventStateProvider, waveNumber) =>
                {
                    try
                    {
                        Assert.That(waveNumber, Is.EqualTo(spawnWave.WaveNumber), "The configured wave number must reach the map initializer.");
                        var monster = new Monster(
                            spawnArea,
                            monsterDefinition,
                            map,
                            NullDropGenerator.Instance,
                            new Mock<INpcIntelligence>().Object,
                            winner.GameContext.PlugInManager,
                            winner.GameContext.PathFinderPool,
                            eventStateProvider);
                        monster.Initialize();
                        return new ValueTask(AddWaveMonsterAsync(map, monster, spawnedMonster));
                    }
                    catch (Exception exception)
                    {
                        spawnedMonster.TrySetException(exception);
                        throw;
                    }
                }));

        await EnterAndStartAsync(game, winner, otherPlayer).ConfigureAwait(false);
        Assert.That(game.Definition.SpawnWaves, Has.Count.EqualTo(1));
        Assert.That(game.Definition.SpawnWaves.Single(), Is.SameAs(spawnWave));
        var monster = await spawnedMonster.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.That(game.IsSpawnWaveActive(spawnWave.WaveNumber), Is.True);
        await monster.ReflectDamageAsync(winner, uint.MaxValue).ConfigureAwait(false);

        await WaitUntilAsync(
            () => game.IsDisposed && game.Map.IsTornDown,
            TimeSpan.FromSeconds(110)).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(game.Map.IsTornDown, Is.True);
            Assert.That(winner.CurrentMiniGame, Is.Null);
            Assert.That(otherPlayer.CurrentMiniGame, Is.Null);
            Assert.That(winner.CurrentMap, Is.Not.SameAs(game.Map));
            Assert.That(otherPlayer.CurrentMap, Is.Not.SameAs(game.Map));
            Assert.That(winner.Money, Is.EqualTo(500), "The player whose wave kill increased the score must rank first.");
            Assert.That(otherPlayer.Money, Is.EqualTo(200));
        });
    }

    private TGame CreateGame<TGame>(
        Player player,
        MiniGameType type,
        MiniGameSuccessFlags rewardFlags,
        int rewardAmount,
        IReadOnlyCollection<MiniGameReward>? rewards = null,
        IReadOnlyCollection<MiniGameSpawnWave>? spawnWaves = null,
        TimeSpan? gameDuration = null,
        int maximumPlayerCount = 1,
        Action<Mock<IMapInitializer>>? configureMapInitializer = null)
        where TGame : MiniGameContext
    {
        player.GameContext.Configuration.MaximumInventoryMoney = int.MaxValue;
        var mapDefinition = player.GameContext.Configuration.Maps.First(map => map.Number == 0);
        mapDefinition.ExitGates.Add(new ExitGate
        {
            Map = mapDefinition,
            X1 = 1,
            Y1 = 1,
            X2 = 1,
            Y2 = 1,
            IsSpawnGate = true,
        });
        var definitionMock = new Mock<MiniGameDefinition>();
        definitionMock.SetupGet(definition => definition.Rewards).Returns(rewards?.ToList() ??
        [
            new MiniGameReward
            {
                RewardType = MiniGameRewardType.Money,
                RewardAmount = rewardAmount,
                RequiredSuccess = rewardFlags,
            },
        ]);
        definitionMock.SetupGet(definition => definition.SpawnWaves).Returns(spawnWaves?.ToList() ?? []);
        definitionMock.SetupGet(definition => definition.ChangeEvents).Returns([]);
        definitionMock.SetupGet(definition => definition.Entrance).Returns(new ExitGate { Map = mapDefinition });
        var definition = definitionMock.Object;
        definition.Type = type;
        definition.GameLevel = 1;
        definition.MapCreationPolicy = MiniGameMapCreationPolicy.Shared;
        definition.EnterDuration = TimeSpan.FromMinutes(1);
        definition.GameDuration = gameDuration ?? TimeSpan.FromMinutes(5);
        definition.ExitDuration = TimeSpan.Zero;
        definition.MaximumPlayerCount = maximumPlayerCount;

        var mapInitializer = new Mock<IMapInitializer>();
        mapInitializer.Setup(initializer => initializer.CreateGameMap(It.IsAny<GameMapDefinition>()))
            .Returns<GameMapDefinition>(map => new GameMap(map, TimeSpan.FromMinutes(1), 16));
        configureMapInitializer?.Invoke(mapInitializer);

        MiniGameContext game = type switch
        {
            MiniGameType.BloodCastle => new BloodCastleContext(new MiniGameMapKey(0, definition.GameLevel, string.Empty), definition, player.GameContext, mapInitializer.Object),
            MiniGameType.DevilSquare => new DevilSquareContext(new MiniGameMapKey(0, definition.GameLevel, string.Empty), definition, player.GameContext, mapInitializer.Object),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        this._gamesToDispose.Add(game);
        return (TGame)game;
    }

    private static async Task EnterAndStartAsync(MiniGameContext game, params Player[] players)
    {
        game.EnsureGameLoopRunning();
        foreach (var player in players)
        {
            Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
            player.CurrentMap = game.Map;
            await game.Map.AddAsync(player).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow.Add(Timeout);
        var sawClosed = false;
        while (game.State != MiniGameState.Playing && DateTime.UtcNow < deadline)
        {
            sawClosed |= game.State == MiniGameState.Closed;
            game.SkipCurrentWait();
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawClosed, Is.True, "The real game loop must close registration and enter its countdown phase.");
            Assert.That(game.State, Is.EqualTo(MiniGameState.Playing));
            Assert.That(players.All(player => player.CurrentMiniGame == game), Is.True);
            Assert.That(players.All(player => player.CurrentMap == game.Map), Is.True);
        });
    }

    private static async Task AddWaveMonsterAsync(GameMap map, Monster monster, TaskCompletionSource<Monster> spawnedMonster)
    {
        try
        {
            await map.AddAsync(monster).ConfigureAwait(false);
            spawnedMonster.TrySetResult(monster);
        }
        catch (Exception exception)
        {
            spawnedMonster.TrySetException(exception);
            throw;
        }
    }

    private static void FinishAsGameMaster(MiniGameContext game)
    {
        typeof(MiniGameContext).GetMethod("FinishEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? Timeout);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, "The real mini-game loop did not reach the expected lifecycle state in time.");
    }
}
