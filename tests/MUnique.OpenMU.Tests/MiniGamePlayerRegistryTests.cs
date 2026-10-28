// <copyright file="MiniGamePlayerRegistryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests for the <see cref="MiniGamePlayerRegistry"/> entering policy: entering is only
/// possible while the game is open and not full, atomically with state transitions.
/// </summary>
[TestFixture]
public class MiniGamePlayerRegistryTests
{
    /// <summary>
    /// Tests that entering works while the game is open.
    /// </summary>
    [Test]
    public async Task EnterWhileOpenSucceedsAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.Success));
    }

    /// <summary>
    /// Tests that entering fails once the entrance has been closed.
    /// </summary>
    [Test]
    public async Task EnterAfterCloseIsRejectedAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        await registry.SetStateAsync(MiniGameState.Closed).ConfigureAwait(false);

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.NotOpen));
    }

    /// <summary>
    /// Tests that entering works while closed when entering while playing is allowed,
    /// for the instant start which passes through the closed state in milliseconds.
    /// </summary>
    [Test]
    public async Task EnterWhileClosedSucceedsWhenAllowedAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        await registry.SetStateAsync(MiniGameState.Closed).ConfigureAwait(false);

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true), () => true).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.Success));
    }

    /// <summary>
    /// Tests that entering fails when the game is full with other players.
    /// </summary>
    [Test]
    public async Task EnterWhenFullIsRejectedAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        Assert.That(await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.Full));
    }

    /// <summary>
    /// Tests that entering the same player twice isn't reported as a second successful admission.
    /// </summary>
    [Test]
    public async Task EnteringSamePlayerTwiceIsRejectedAsync()
    {
        var registry = new MiniGamePlayerRegistry(new MiniGameDefinition { MaximumPlayerCount = 2 });
        var player = CreatePlayer();

        Assert.That(await registry.TryEnterAsync(player, _ => ValueTask.FromResult(true)).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        var duplicateResult = await registry.TryEnterAsync(player, _ => ValueTask.FromResult(true)).ConfigureAwait(false);

        Assert.That(duplicateResult, Is.EqualTo(EnterResult.Failed));
        Assert.That(await registry.GetSnapshotAsync().ConfigureAwait(false), Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Tests that concurrent entrants cannot exceed the ten-player Blood Castle and Devil Square limit.
    /// </summary>
    [Test]
    public async Task ConcurrentEntrantsCannotExceedTenPlayerLimitAsync()
    {
        var registry = new MiniGamePlayerRegistry(new MiniGameDefinition { MaximumPlayerCount = 10 });
        for (var i = 0; i < 9; i++)
        {
            Assert.That(await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false), Is.EqualTo(EnterResult.Success));
        }

        var firstValidationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstValidation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntry = registry.TryEnterAsync(CreatePlayer(), async _ =>
        {
            firstValidationStarted.SetResult(true);
            await releaseFirstValidation.Task.ConfigureAwait(false);
            return true;
        }).AsTask();
        await firstValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var secondEntry = registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).AsTask();
        try
        {
            Assert.That(firstEntry.IsCompleted, Is.False);
            Assert.That(secondEntry.IsCompleted, Is.False, "The second entrant must wait while the final slot is being validated.");
        }
        finally
        {
            releaseFirstValidation.TrySetResult(true);
        }

        var results = await Task.WhenAll(firstEntry, secondEntry).WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        Assert.That(results.Count(result => result == EnterResult.Success), Is.EqualTo(1));
        Assert.That(results.Count(result => result == EnterResult.Full), Is.EqualTo(1));
        Assert.That(await registry.GetSnapshotAsync().ConfigureAwait(false), Has.Count.EqualTo(10));
    }

    /// <summary>
    /// Tests that entering while the game runs is rejected by default.
    /// </summary>
    [Test]
    public async Task EnterWhilePlayingIsRejectedByDefaultAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        await registry.SetStateAsync(MiniGameState.Playing).ConfigureAwait(false);

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true)).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.NotOpen));
    }

    /// <summary>
    /// Tests that entering while the game runs succeeds when late entering is allowed,
    /// e.g. to rejoin an ongoing event.
    /// </summary>
    [Test]
    public async Task EnterWhilePlayingWithFlagSucceedsAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        await registry.SetStateAsync(MiniGameState.Playing).ConfigureAwait(false);

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true), allowEnterWhilePlaying: () => true).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.Success));
    }

    /// <summary>
    /// Tests that the late entering flag doesn't open ended games.
    /// </summary>
    [Test]
    public async Task EnterWhenEndedWithFlagIsRejectedAsync()
    {
        var registry = new MiniGamePlayerRegistry(this.CreateDefinition());
        await registry.SetStateAsync(MiniGameState.Ended).ConfigureAwait(false);

        var result = await registry.TryEnterAsync(CreatePlayer(), _ => ValueTask.FromResult(true), allowEnterWhilePlaying: () => true).ConfigureAwait(false);

        Assert.That(result, Is.EqualTo(EnterResult.NotOpen));
    }

    private static Player CreatePlayer()
    {
        var contextMock = new Mock<IGameContext>();
        contextMock.SetupGet(c => c.LoggerFactory).Returns(NullLoggerFactory.Instance);
        contextMock.SetupGet(c => c.Configuration).Returns(new GameConfiguration());
        contextMock.SetupGet(c => c.PersistenceContextProvider).Returns(new InMemoryPersistenceContextProvider());
        return new Player(contextMock.Object);
    }

    private MiniGameDefinition CreateDefinition()
    {
        return new MiniGameDefinition { MaximumPlayerCount = 1 };
    }
}
