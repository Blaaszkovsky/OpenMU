// <copyright file="MiniGameDisposalResilienceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using ExitGate = MUnique.OpenMU.Persistence.BasicModel.ExitGate;
using MiniGameDefinition = MUnique.OpenMU.Persistence.BasicModel.MiniGameDefinition;

/// <summary>Regressions for mini-game teardown when a disconnect races character selection.</summary>
[TestFixture]
public class MiniGameDisposalResilienceTests
{
    [Test]
    public async Task MapRemovalAfterCharacterSelectionDetachesMiniGameParticipantAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var gameContext = player.GameContext;
        var definition = new MiniGameDefinition
        {
            Type = MiniGameType.IllusionTemple,
            Entrance = new ExitGate { Map = player.CurrentMap!.Definition },
            EnterDuration = TimeSpan.FromMinutes(1),
            MaximumPlayerCount = 10,
        };
        var game = await gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
        Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        player.CurrentMap = game.Map;
        await game.Map.AddAsync(player).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(null).ConfigureAwait(false);
        player.CurrentMap = null;

        Assert.DoesNotThrowAsync(async () => await game.Map.RemoveAsync(player).ConfigureAwait(false));
        Assert.Multiple(() =>
        {
            Assert.That(game.Map.GetPlayers(), Does.Not.Contain(player));
            Assert.That(player.CurrentMiniGame, Is.Null);
            Assert.That(game.PlayerCount, Is.Zero);
        });

        await game.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A participant with no active character must not prevent other members or the map from being cleaned up.</summary>
    [Test]
    public async Task MissingCharacterDoesNotAbortMiniGameTeardownAsync()
    {
        var disconnected = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var gameContext = disconnected.GameContext;
        var remaining = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        var definition = new MiniGameDefinition
        {
            Type = MiniGameType.IllusionTemple,
            Entrance = new ExitGate { Map = disconnected.CurrentMap!.Definition },
            EnterDuration = TimeSpan.FromMinutes(1),
            MaximumPlayerCount = 10,
        };

        var game = await gameContext.MiniGames.GetOrCreateAsync(definition, disconnected).ConfigureAwait(false);
        Assert.That(await game.TryEnterAsync(disconnected).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        Assert.That(await game.TryEnterAsync(remaining).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));

        disconnected.CurrentMap = game.Map;
        remaining.CurrentMap = game.Map;
        await game.Map.AddAsync(disconnected).ConfigureAwait(false);
        await game.Map.AddAsync(remaining).ConfigureAwait(false);

        // This is the state observed when a late map-ready/disconnect crosses character selection:
        // the object was admitted and on the event map, but no character/map can now resolve an exit.
        await disconnected.SetSelectedCharacterAsync(null).ConfigureAwait(false);
        disconnected.CurrentMap = null;

        await game.DisposeAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(disconnected.CurrentMiniGame, Is.Null);
            Assert.That(remaining.CurrentMiniGame, Is.Null);
            Assert.That(game.Map.GetPlayers(), Is.Empty);
            Assert.That(game.Map.IsTornDown, Is.True);
            Assert.That(gameContext.MiniGames.TryGetRunningMiniGame(definition, remaining), Is.Null);
            Assert.That(gameContext.MiniGames.Maps, Does.Not.Contain(game.Map));
        });

        // Complete the ordinary client acknowledgement to verify the surviving member was actually
        // sent to its configured safezone, rather than merely having its mini-game reference cleared.
        await remaining.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        Assert.That(remaining.CurrentMap?.Definition, Is.SameAs(game.Map.Definition.SafezoneMap ?? game.Map.Definition));
    }

    /// <summary>A late map-ready packet after logout to character selection is ignored.</summary>
    [Test]
    public async Task LateClientReadyAfterCharacterSelectionIsIgnoredAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);

        await player.RemoveFromGameAsync().ConfigureAwait(false);

        Assert.That(player.SelectedCharacter, Is.Null);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        Assert.That(player.CurrentMap, Is.Null);
    }

    /// <summary>A full disconnect after character selection detaches a participant whose map transition is still pending.</summary>
    [Test]
    public async Task DisconnectAfterCharacterSelectionDetachesPendingMiniGameParticipantAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var gameContext = player.GameContext;
        var definition = new MiniGameDefinition
        {
            Type = MiniGameType.IllusionTemple,
            Entrance = new ExitGate { Map = player.CurrentMap!.Definition },
            EnterDuration = TimeSpan.FromMinutes(1),
            MaximumPlayerCount = 10,
        };
        var game = await gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
        Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        player.CurrentMap = game.Map;
        await game.Map.AddAsync(player).ConfigureAwait(false);
        Assert.That(player.CurrentMiniGame, Is.SameAs(game));
        Assert.That(game.Map.GetPlayers(), Does.Contain(player));

        // Returning to character selection can leave the mini-game reference in place while F3:12
        // for the safezone is pending and CurrentMap has already been cleared by WarpToAsync.
        await player.SetSelectedCharacterAsync(null).ConfigureAwait(false);
        player.CurrentMap = null;
        Assert.That(player.SelectedCharacter, Is.Null);
        Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(player.CurrentMiniGame, Is.SameAs(game));
        Assert.That(game.Map.GetPlayers(), Does.Contain(player));

        await player.DisconnectAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(player.CurrentMiniGame, Is.Null);
            Assert.That(game.PlayerCount, Is.Zero);
            Assert.That(game.Map.GetPlayers(), Is.Empty);
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.Finished));
        });

        await game.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A repeated detach after disposal is harmless when the character selection has already ended.</summary>
    /// <summary>Detaching a player again after the context has disposed its cancellation source is harmless.</summary>
    [Test]
    public async Task RepeatedDetachAfterDisposeDoesNotCancelDisposedGameTokenAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var gameContext = player.GameContext;
        var definition = new MiniGameDefinition
        {
            Type = MiniGameType.IllusionTemple,
            Entrance = new ExitGate { Map = player.CurrentMap!.Definition },
            EnterDuration = TimeSpan.FromMinutes(1),
            MaximumPlayerCount = 10,
        };
        var game = await gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
        Assert.That(await game.TryEnterAsync(player).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        player.CurrentMap = game.Map;
        await game.Map.AddAsync(player).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(null).ConfigureAwait(false);

        await game.DisposeAsync().ConfigureAwait(false);

        Assert.DoesNotThrowAsync(async () => await game.DetachPlayerAsync(player).ConfigureAwait(false));
        Assert.Multiple(() =>
        {
            Assert.That(player.CurrentMiniGame, Is.Null);
            Assert.That(game.PlayerCount, Is.Zero);
            Assert.That(game.Map.GetPlayers(), Is.Empty);
            Assert.That(game.Map.IsTornDown, Is.True);
            Assert.That(gameContext.MiniGames.TryGetRunningMiniGame(definition, player), Is.Null);
        });
    }
}
