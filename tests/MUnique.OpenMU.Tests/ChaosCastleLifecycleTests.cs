// <copyright file="ChaosCastleLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Pathfinding;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Regression tests for the Chaos Castle event lifecycle.
/// </summary>
[TestFixture]
public class ChaosCastleLifecycleTests
{
    /// <summary>
    /// Kills the spawned monster and the other entrant, then lets the real game loop
    /// select its survivor, pay the reward, and automatically evacuate and release the map.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task LastSurvivorReceivesOneRewardAndAutomaticallyLeavesReleasedMapAsync()
    {
        var winner = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await using var context = (GameContext)winner.GameContext;
        context.StopPeriodicTasks();
        var otherPlayer = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        var winnerCharacter = winner.SelectedCharacter!;
        var otherCharacter = otherPlayer.SelectedCharacter!;
        winnerCharacter.Name = "CcWinner";
        otherCharacter.Name = "CcOther";
        context.Configuration.MaximumInventoryMoney = int.MaxValue;
        context.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 13 });
        context.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 14 });
        var homeMap = context.Configuration.Maps.Single(map => map.Number == 0);
        homeMap.ExitGates.Add(new ExitGate { Map = homeMap, X1 = 1, X2 = 1, Y1 = 1, Y2 = 1, IsSpawnGate = true });
        var castleMap = new BasicModel.GameMapDefinition
        {
            Number = 18,
            SafezoneMap = homeMap,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        context.Configuration.Maps.Add(castleMap);
        var monsterDefinition = new BasicModel.MonsterDefinition { Id = Guid.NewGuid(), Number = 777, ObjectKind = NpcObjectKind.Monster };
        monsterDefinition.Attributes.Add(new BasicModel.MonsterAttribute
        {
            AttributeDefinition = MUnique.OpenMU.GameLogic.Attributes.Stats.MaximumHealth,
            Value = 1,
        });
        castleMap.MonsterSpawns.Add(new BasicModel.MonsterSpawnArea
        {
            GameMap = castleMap,
            MonsterDefinition = monsterDefinition,
            SpawnTrigger = SpawnTrigger.ManuallyForEvent,
            Quantity = 1,
            MaximumHealthOverride = 1,
            X1 = 30,
            X2 = 30,
            Y1 = 30,
            Y2 = 30,
        });
        var definition = new BasicModel.MiniGameDefinition
        {
            Type = MiniGameType.ChaosCastle,
            GameLevel = 1,
            MapCreationPolicy = MiniGameMapCreationPolicy.Shared,
            MaximumPlayerCount = 2,
            Entrance = new ExitGate { Map = castleMap, X1 = 10, X2 = 10, Y1 = 10, Y2 = 10 },
            EnterDuration = TimeSpan.FromMinutes(1),
            GameDuration = TimeSpan.FromMinutes(5),
            ExitDuration = TimeSpan.Zero,
        };
        definition.Rewards.Add(new BasicModel.MiniGameReward
        {
            RewardType = MiniGameRewardType.Money,
            RewardAmount = 500,
            RequiredSuccess = MiniGameSuccessFlags.Winner | MiniGameSuccessFlags.Alive,
            RequiredKill = monsterDefinition,
            Rank = 1,
        });
        var mapInitializer = new MapInitializer(context.Configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null)
        {
            PlugInManager = context.PlugInManager,
            PathFinderPool = context.PathFinderPool,
        };
        var game = new ChaosCastleContext(new MiniGameMapKey(0, definition.GameLevel, string.Empty), definition, context, mapInitializer);
        var scoreView = Mock.Get(winner.ViewPlugIns.GetPlugIn<IChaosCastleScoreTableViewPlugin>()!);
        var startingMoney = winner.Money;
        var otherStartingMoney = otherPlayer.Money;
        try
        {
            game.EnsureGameLoopRunning();
            foreach (var player in new[] { winner, otherPlayer })
            {
                Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
                await player.WarpToAsync(definition.Entrance).ConfigureAwait(false);
                await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            }

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
            }).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(sawClosed, Is.True, "The game must close registration and pass through its countdown.");
                Assert.That(game.PlayerCount, Is.EqualTo(2));
                Assert.That(winner.CurrentMap, Is.SameAs(game.Map));
                Assert.That(otherPlayer.CurrentMap, Is.SameAs(game.Map));
            });

            var spawnPosition = new Point(30, 30);
            await WaitUntilAsync(() => game.Map.GetNpcsInRange(spawnPosition, 1).OfType<Monster>().Any()).ConfigureAwait(false);
            var monster = game.Map.GetNpcsInRange(spawnPosition, 1).OfType<Monster>().Single();
            Assert.That(monster.Definition, Is.SameAs(monsterDefinition));
            await monster.ReflectDamageAsync(winner, 10_000).ConfigureAwait(false);
            Assert.That(monster.IsAlive, Is.False);
            Assert.That(game.State, Is.EqualTo(MiniGameState.Playing), "The other entrant must still prevent a survivor win.");

            await otherPlayer.ReflectDamageAsync(winner, 10_000).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(otherPlayer.IsAlive, Is.False);
                Assert.That(otherPlayer.LastDeath?.KillerName, Is.EqualTo(winner.Name));
            });
            await WaitUntilAsync(() => otherPlayer.CurrentMiniGame is null && game.State == MiniGameState.Ended && winner.Money == startingMoney + 500).ConfigureAwait(false);
            Assert.That(otherCharacter.CurrentMap, Is.SameAs(homeMap), "The eliminated entrant must respawn outside the event.");

            // The production exit has two unskippable 30-second waits, even with ExitDuration zero.
            await WaitUntilAsync(() => game.IsDisposed && game.Map.IsTornDown, TimeSpan.FromSeconds(80)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(game.State, Is.EqualTo(MiniGameState.Disposed));
                Assert.That(game.PlayerCount, Is.Zero);
                Assert.That(winner.CurrentMiniGame, Is.Null);
                Assert.That(otherPlayer.CurrentMiniGame, Is.Null);
                Assert.That(winner.CurrentMap, Is.Not.SameAs(game.Map));
                Assert.That(otherPlayer.CurrentMap, Is.Not.SameAs(game.Map));
                Assert.That(winnerCharacter.CurrentMap, Is.SameAs(homeMap));
                Assert.That(winner.Money, Is.EqualTo(startingMoney + 500), "The survivor reward must be paid exactly once.");
                Assert.That(otherPlayer.Money, Is.EqualTo(otherStartingMoney));
            });
            scoreView.Verify(view => view.ShowScoreTableAsync(true, winner.Name, 1, 1, 0), Times.Once);
            scoreView.VerifyNoOtherCalls();
        }
        finally
        {
            // Failure cleanup only; all automatic disposal assertions precede this block.
            if (!game.IsDisposed)
            {
                await game.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A dead entrant can remain registered until respawn; it must not become the winner in that interval.
    /// </summary>
    /// <param name="delayedShrink">Whether the final monster triggers a delayed terrain change which eliminates the player.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DeadEntrantIsNotRankedOrRewardedBeforeRespawnAsync(bool delayedShrink)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await using var context = (GameContext)player.GameContext;
        context.StopPeriodicTasks();
        player.SelectedCharacter!.Name = "CcFallen";
        context.Configuration.MaximumInventoryMoney = int.MaxValue;
        context.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 13 });
        context.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 14 });
        var homeMap = context.Configuration.Maps.Single();
        homeMap.ExitGates.Add(new ExitGate { Map = homeMap, X1 = 1, X2 = 1, Y1 = 1, Y2 = 1, IsSpawnGate = true });
        var map = new BasicModel.GameMapDefinition
        {
            Number = 18,
            SafezoneMap = homeMap,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        context.Configuration.Maps.Add(map);
        var definition = new BasicModel.MiniGameDefinition
        {
            Type = MiniGameType.ChaosCastle,
            MaximumPlayerCount = 70,
            Entrance = new ExitGate { Map = map, X1 = 10, X2 = 10, Y1 = 10, Y2 = 10 },
            EnterDuration = TimeSpan.FromMinutes(1),
            GameDuration = TimeSpan.FromMinutes(5),
            ExitDuration = TimeSpan.Zero,
        };
        var terrainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (delayedShrink)
        {
            var monster = new BasicModel.MonsterDefinition { Id = Guid.NewGuid(), Number = 777, ObjectKind = NpcObjectKind.Monster };
            monster.Attributes.Add(new BasicModel.MonsterAttribute { AttributeDefinition = MUnique.OpenMU.GameLogic.Attributes.Stats.MaximumHealth, Value = 1 });
            map.MonsterSpawns.Add(new BasicModel.MonsterSpawnArea
            {
                GameMap = map,
                MonsterDefinition = monster,
                SpawnTrigger = SpawnTrigger.ManuallyForEvent,
                Quantity = 1,
                MaximumHealthOverride = 1,
                X1 = 30, X2 = 30, Y1 = 30, Y2 = 30,
            });
            var change = new BasicModel.MiniGameChangeEvent { Index = 1, NumberOfKills = 1, Target = KillTarget.AnyMonster };
            change.TerrainChanges.Add(new BasicModel.MiniGameTerrainChange
            {
                StartX = 10, EndX = 10, StartY = 10, EndY = 10,
                TerrainAttribute = TerrainAttributeType.NoGround,
                SetTerrainAttribute = true,
                IsClientUpdateRequired = true,
            });
            definition.ChangeEvents.Add(change);
            Mock.Get(player.ViewPlugIns.GetPlugIn<IChangeTerrainAttributesViewPlugin>()!)
                .Setup(view => view.ChangeAttributesAsync(TerrainAttributeType.NoGround, true, It.IsAny<IReadOnlyCollection<(byte, byte, byte, byte)>>()))
                .Returns(async () =>
                {
                    terrainEntered.TrySetResult();
                    await releaseTerrain.Task.ConfigureAwait(false);
                });
        }
        definition.Rewards.Add(new BasicModel.MiniGameReward
        {
            RewardType = MiniGameRewardType.Money,
            RewardAmount = 500,
            RequiredSuccess = MiniGameSuccessFlags.Winner,
            Rank = 1,
        });
        var initializer = new MapInitializer(context.Configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null)
        {
            PlugInManager = context.PlugInManager,
            PathFinderPool = context.PathFinderPool,
        };
        var game = new ChaosCastleContext(new MiniGameMapKey(0, 1, string.Empty), definition, context, initializer);
        var score = Mock.Get(player.ViewPlugIns.GetPlugIn<IChaosCastleScoreTableViewPlugin>()!);
        var initialMoney = player.Money;
        try
        {
            game.EnsureGameLoopRunning();
            Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
            await player.WarpToAsync(definition.Entrance).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            await WaitUntilAsync(() =>
            {
                if (game.State == MiniGameState.Playing)
                {
                    return true;
                }

                game.SkipCurrentWait();
                return false;
            }).ConfigureAwait(false);
            if (delayedShrink)
            {
                await WaitUntilAsync(() => game.Map.GetNpcsInRange(new Point(30, 30), 1).OfType<Monster>().Any()).ConfigureAwait(false);
                var monster = game.Map.GetNpcsInRange(new Point(30, 30), 1).OfType<Monster>().Single();
                await monster.ReflectDamageAsync(player, 10_000).ConfigureAwait(false);
                await terrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Task.Delay(1_200).ConfigureAwait(false);
                Assert.That(player.Money, Is.EqualTo(initialMoney), "Winner selection must wait for the last kill's pending terrain change.");
                releaseTerrain.TrySetResult();
                await WaitUntilAsync(() => !player.IsAlive).ConfigureAwait(false);
            }
            else
            {
                await player.KillInstantlyAsync().ConfigureAwait(false);
            }

            Assert.That(player.IsAlive, Is.False);
            await WaitUntilAsync(() => game.State == MiniGameState.Ended && score.Invocations.Any(invocation => invocation.Method.Name == nameof(IChaosCastleScoreTableViewPlugin.ShowScoreTableAsync))).ConfigureAwait(false);
            Assert.That(player.Money, Is.EqualTo(initialMoney), "A fallen entrant is not a winner, even before its delayed map removal.");
            score.Verify(view => view.ShowScoreTableAsync(false, player.Name, delayedShrink ? 1 : 0, 0, 0), Times.Once);
            score.VerifyNoOtherCalls();
        }
        finally
        {
            releaseTerrain.TrySetResult();
            await game.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tests that the last player can finish when no monster was created from the configured spawns.
    /// </summary>
    [Test]
    public async Task LastPlayerCanWinWhenNoMonsterWasSpawnedAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 13 });
        player.GameContext.Configuration.Items.Add(new ItemDefinition { Group = 14, Number = 14 });
        var mapDefinition = new Mock<GameMapDefinition>();
        mapDefinition.SetupGet(map => map.MonsterSpawns).Returns(new List<MonsterSpawnArea>());
        mapDefinition.SetupGet(map => map.ExitGates).Returns(new List<ExitGate>());

        var definitionMock = new Mock<MiniGameDefinition>();
        definitionMock.SetupGet(definition => definition.Rewards).Returns(new List<MiniGameReward>());
        definitionMock.SetupGet(definition => definition.SpawnWaves).Returns(new List<MiniGameSpawnWave>());
        definitionMock.SetupGet(definition => definition.ChangeEvents).Returns(new List<MiniGameChangeEvent>());
        definitionMock.SetupGet(definition => definition.Entrance).Returns(new ExitGate { Map = mapDefinition.Object });
        var definition = definitionMock.Object;
        definition.Type = MiniGameType.ChaosCastle;
        definition.MapCreationPolicy = MiniGameMapCreationPolicy.Shared;
        definition.MaximumPlayerCount = 70;

        var mapInitializer = new Mock<IMapInitializer>();
        mapInitializer.Setup(initializer => initializer.CreateGameMap(mapDefinition.Object))
            .Returns<GameMapDefinition>(map => new GameMap(map, TimeSpan.FromMinutes(1), 16));

        var game = new ChaosCastleContext(new MiniGameMapKey(0, 0, string.Empty), definition, player.GameContext, mapInitializer.Object);
        try
        {
            Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));

            var spawnMonsters = typeof(ChaosCastleContext).GetMethod("SpawnMonstersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)spawnMonsters.Invoke(game, null)!).ConfigureAwait(false);

            var hasGameEnded = typeof(ChaosCastleContext).GetMethod("HasGameEnded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.That((bool)hasGameEnded.Invoke(game, null)!, Is.True);
        }
        finally
        {
            await game.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, "The real Chaos Castle loop did not reach the expected lifecycle state in time.");
    }
}
