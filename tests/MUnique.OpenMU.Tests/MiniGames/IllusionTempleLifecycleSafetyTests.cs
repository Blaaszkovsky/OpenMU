// <copyright file="IllusionTempleLifecycleSafetyTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using ExitGate = MUnique.OpenMU.Persistence.BasicModel.ExitGate;
using ItemDefinition = MUnique.OpenMU.Persistence.BasicModel.ItemDefinition;
using MiniGameDefinition = MUnique.OpenMU.Persistence.BasicModel.MiniGameDefinition;

/// <summary>Regressions for map state and persisted match tokens.</summary>
[TestFixture]
public class IllusionTempleLifecycleSafetyTests
{
    /// <summary>The normal pickup action must not re-display a token removed by its event callback.</summary>
    [Test]
    public async Task RejectedRelicPickupDoesNotReappearInClientInventoryAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.ItemDropDuration = TimeSpan.FromMinutes(1);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        player.CurrentMap = game.Map;
        await game.Map.AddAsync(player).ConfigureAwait(false);
        var item = new TemporaryItem { Definition = new ItemDefinition { Group = 14, Number = 64, Width = 1, Height = 1 }, Durability = 1 };
        var drop = new DroppedItem(item, player.Position, game.Map, null, null);
        await game.Map.AddAsync(drop).ConfigureAwait(false);

        await new PickupItemAction().PickupItemAsync(player, drop.Id).ConfigureAwait(false);

        Assert.That(player.Inventory!.Items.Any(candidate => candidate.Definition is { Group: 14, Number: 64 }), Is.False);
        Mock.Get(player.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!).Verify(view => view.ItemAppearAsync(It.IsAny<Item>()), Times.Never);
    }

    /// <summary>A rejected pickup must delete the converted inventory item, rather than its temporary ground representation.</summary>
    [Test]
    public async Task RejectedTemporaryRelicPickupRemovesConvertedInventoryItemAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.ItemDropDuration = TimeSpan.FromMinutes(1);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        var item = new TemporaryItem { Definition = new ItemDefinition { Group = 14, Number = 64, Width = 1, Height = 1 }, Durability = 1 };
        var drop = new DroppedItem(item, player.Position, game.Map, null, null);
        await game.Map.AddAsync(drop).ConfigureAwait(false);
        Assert.That((await drop.TryPickUpByAsync(player).ConfigureAwait(false)).Success, Is.True);
        var converted = player.Inventory!.Items.Single(candidate => candidate.Definition is { Group: 14, Number: 64 });
        await player.OnPickedUpItemAsync(drop).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(player.Inventory.Items, Does.Not.Contain(converted));
            Assert.That(player.Inventory.Items.Any(candidate => candidate.Definition is { Group: 14, Number: 64 }), Is.False);
        });
        Assert.That(await player.PersistenceContext.GetByIdAsync<Item>(converted.GetId()).ConfigureAwait(false), Is.Null);
    }

    /// <summary>Dropping the event token consumes its old persistent serial and puts only a temporary replacement on the ground.</summary>
    [Test]
    public async Task RelicDropConsumesPersistentSerialAndPickupCreatesNewOneAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.ItemDropDuration = TimeSpan.FromMinutes(1);
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new ItemDefinition { Group = 14, Number = 64, Width = 1, Height = 1 };
        item.Durability = 1;
        Assert.That(await player.Inventory!.AddItemAsync(20, item).ConfigureAwait(false), Is.True);
        var originalId = item.GetId();
        Assert.That(await player.PersistenceContext.GetByIdAsync<Item>(originalId).ConfigureAwait(false), Is.SameAs(item));
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        typeof(IllusionTempleContext).GetField("_holder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(game, player);
        var dropMethod = typeof(IllusionTempleContext).GetMethod("DropRelicAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((ValueTask)dropMethod.Invoke(game, [player])!).ConfigureAwait(false);
        var drop = game.Map.GetDropsInRange(new Point(128, 128), 255).OfType<DroppedItem>().Single();
        var remainingOriginal = await player.PersistenceContext.GetByIdAsync<Item>(originalId).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(drop.Item, Is.InstanceOf<TemporaryItem>());
            Assert.That(remainingOriginal, Is.Null, "The old serial must be deleted rather than saved without an owner.");
            Assert.That(player.Inventory.Items, Does.Not.Contain(item));
        });

        var picker = await PlayerTestHelper.CreatePlayerAsync(player.GameContext).ConfigureAwait(false);
        var pickedUp = await drop.TryPickUpByAsync(picker).ConfigureAwait(false);
        Assert.That(pickedUp.Success, Is.True);
        var newItem = picker.Inventory!.Items.Single(candidate => candidate.Definition is { Group: 14, Number: 64 });
        Assert.Multiple(() =>
        {
            Assert.That(newItem, Is.Not.InstanceOf<TemporaryItem>());
            Assert.That(newItem.GetId(), Is.Not.EqualTo(originalId));
            Assert.That(newItem.Definition, Is.SameAs(item.Definition));
        });
    }

    /// <summary>The legacy result window requires at least two deposits for a winning team.</summary>
    /// <param name="allied">The allied score.</param>
    /// <param name="illusion">The illusion score.</param>
    /// <param name="winner">The expected winner, or 255 when neither team wins.</param>
    [TestCase(0, 0, 255)]
    [TestCase(1, 0, 255)]
    [TestCase(0, 1, 255)]
    [TestCase(2, 0, 0)]
    [TestCase(0, 2, 1)]
    [TestCase(2, 2, 255)]
    [TestCase(3, 2, 0)]
    public async Task FinalScoreMatchesClientWinnerRuleAsync(byte allied, byte illusion, byte winner)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        var scores = (byte[])typeof(IllusionTempleContext).GetField("_scores", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        scores[0] = allied;
        scores[1] = illusion;
        var finish = typeof(IllusionTempleContext).GetMethod("GameEndedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((ValueTask)finish.Invoke(game, [Array.Empty<Player>()])!).ConfigureAwait(false);

        Assert.That(typeof(IllusionTempleContext).GetField("_winningTeam", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game), Is.EqualTo(winner));
    }

    /// <summary>A winning threshold below the client's minimum cannot produce a consistent result.</summary>
    /// <param name="score">The score required to end a match.</param>
    /// <param name="valid">Whether the configured threshold is valid.</param>
    [TestCase(1, false)]
    [TestCase(2, true)]
    [TestCase(7, true)]
    public void WinningThresholdCannotBeLowerThanClientMinimum(byte score, bool valid)
    {
        var settings = IllusionTempleStartConfiguration.Default;
        settings.WinningScore = score;
        Assert.That(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            settings,
            new System.ComponentModel.DataAnnotations.ValidationContext(settings),
            null,
            true), Is.EqualTo(valid));
    }

    /// <summary>Mixed lobby rectangles must not send a successfully admitted player back out through blocked-spawn recovery.</summary>
    /// <param name="blocked">Whether the randomly selected landing is blocked.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task EntryRepairsOnlyBlockedLandingsAsync(bool blocked)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = new MiniGameDefinition
        {
            Entrance = new ExitGate { Map = player.CurrentMap!.Definition, X1 = 98, X2 = 108, Y1 = 128, Y2 = 137 },
            MaximumPlayerCount = 10,
        };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        game.Map.Terrain.ApplyTerrainAttribute(99, 133, TerrainAttributeType.Blocked, true);
        player.Position = blocked ? new Point(99, 133) : new Point(102, 132);

        var repaired = game.GetEntrySpawnPosition(player);
        Assert.That(repaired, blocked ? Is.EqualTo(new Point(98, 128)) : Is.Null);
    }

    /// <summary>Death does not eliminate an IT participant, so disconnecting while dead must retain rejoin eligibility.</summary>
    [Test]
    public async Task DeadParticipantCanBeRememberedForRejoinAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        await game.TryEnterAsync(player).ConfigureAwait(false);
        player.CurrentMap = game.Map;
        await game.Map.AddAsync(player).ConfigureAwait(false);
        player.IsAlive = false;
        game.RememberDisconnectedPlayer(player);
        await game.Map.RemoveAsync(player).ConfigureAwait(false);

        var reconnected = await PlayerTestHelper.CreatePlayerAsync(player.GameContext).ConfigureAwait(false);
        reconnected.SelectedCharacter!.Id = player.SelectedCharacter.Id;
        Assert.That(await game.TryRejoinAsync(reconnected).ConfigureAwait(false), Is.True);
    }

    /// <summary>The server must mirror the client's water opening and restore every original bit afterward.</summary>
    [Test]
    public async Task WaterOpeningAndClosingPreserveOriginalTerrainAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        var original = TerrainAttributeType.Water | TerrainAttributeType.Blocked | TerrainAttributeType.Safezone;
        game.Map.Terrain.ApplyTerrainAttribute(40, 40, original, true);
        game.Map.Terrain.ApplyTerrainAttribute(40, 40, TerrainAttributeType.Safezone, true);
        var change = typeof(IllusionTempleContext).GetMethod("SetWaterOpen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        change.Invoke(game, [true]);
        var playingFlags = game.Map.Terrain.AttributeMap[40, 40];
        var playingSafezone = game.Map.Terrain.SafezoneMap[40, 40];
        change.Invoke(game, [false]);
        Assert.Multiple(() =>
        {
            Assert.That(playingFlags, Is.Zero);
            Assert.That(playingSafezone, Is.False);
            Assert.That(game.Map.Terrain.AttributeMap[40, 40], Is.EqualTo((byte)original));
            Assert.That(game.Map.Terrain.SafezoneMap[40, 40], Is.True);
        });
    }

    /// <summary>A relic saved before a server crash must not remain usable outside its match after login.</summary>
    [Test]
    public async Task EnteringWorldWithoutMatchRemovesOrphanRelicAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new ItemDefinition { Group = 14, Number = 64, Width = 1, Height = 1 };
        item.Durability = 1;
        Assert.That(await player.Inventory!.AddItemAsync(20, item).ConfigureAwait(false), Is.True);

        if ((object)new IllusionTemplePlugIn() is IPlayerStateChangedPlugIn lifeCycle)
        {
            await lifeCycle.PlayerStateChangedAsync(player, PlayerState.CharacterSelection, PlayerState.EnteredWorld).ConfigureAwait(false);
        }

        Assert.That(player.Inventory.Items.Any(candidate => candidate.Definition is { Group: 14, Number: 64 }), Is.False);
    }
}
