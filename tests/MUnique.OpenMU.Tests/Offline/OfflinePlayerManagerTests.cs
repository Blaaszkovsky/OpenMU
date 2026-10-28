// <copyright file="OfflinePlayerManagerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Administration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.Views.Login;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Tests for <see cref="OfflinePlayerManager"/>.
/// </summary>
[TestFixture]
public class OfflinePlayerManagerTests
{
    private const string TestUserLoginName = "test";
    private const string TestCharacterName = "testChar";

    private IGameContext _gameContext = null!;
    private IPersistenceContextProvider _contextProvider = null!;

    /// <summary>
    /// Sets up a fresh game context before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
        this._contextProvider = this._gameContext.PersistenceContextProvider;
    }

    /// <summary>
    /// Tests that <see cref="OfflinePlayerManager.StartAsync"/> returns true on success.
    /// </summary>
    [Test]
    public async ValueTask StartAsync_WithValidPlayer_ReturnsTrueAsync()
    {
        // Arrange
        var manager = new OfflinePlayerManager();
        var realPlayer = await this.CreatePlayerWithPersistedAccountAsync().ConfigureAwait(false);
        realPlayer.TryAddMoney(1_000_000);
        realPlayer.Attributes![Stats.Level] = 100;

        // Act
        var result = await manager.StartAsync(realPlayer, TestUserLoginName).ConfigureAwait(false);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(manager.IsActive(TestUserLoginName), Is.True);
    }

    /// <summary>
    /// Tests that <see cref="OfflinePlayerManager.StartAsync"/> fails if the player has insufficient Zen.
    /// </summary>
    [Test]
    public async ValueTask StartAsync_WithInsufficientZen_ReturnsFalseAsync()
    {
        // Arrange
        var manager = new OfflinePlayerManager();
        var realPlayer = await this.CreatePlayerWithPersistedAccountAsync().ConfigureAwait(false);
        realPlayer.Money = 0; // No money
        realPlayer.Attributes![Stats.Level] = 100;

        // Act
        var result = await manager.StartAsync(realPlayer, TestUserLoginName).ConfigureAwait(false);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(manager.IsActive(TestUserLoginName), Is.False);
    }

    /// <summary>
    /// Tests that <see cref="OfflinePlayerManager.StopAsync"/> successfully stops a session.
    /// </summary>
    [Test]
    public async ValueTask StopAsync_WhenSessionActive_StopsSuccessfullyAsync()
    {
        // Arrange
        var manager = new OfflinePlayerManager();
        var realPlayer = await this.CreatePlayerWithPersistedAccountAsync().ConfigureAwait(false);
        realPlayer.TryAddMoney(1_000_000);
        realPlayer.Attributes![Stats.Level] = 100;

        var started = await manager.StartAsync(realPlayer, TestUserLoginName).ConfigureAwait(false);
        Assert.That(started, Is.True);
        Assert.That(manager.IsActive(TestUserLoginName), Is.True);

        // Act
        await manager.StopAsync(TestUserLoginName).ConfigureAwait(false);

        // Assert
        Assert.That(manager.IsActive(TestUserLoginName), Is.False);
    }

    /// <summary>Concurrent stops keep ownership visible until initialization and final teardown complete.</summary>
    /// <param name="initializing">Whether to pause before the real player's final save or during ghost teardown.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task StopWaitsForInitializationAndTeardownAsync(bool initializing)
    {
        var manager = this._gameContext.OfflinePlayerManager;
        var administration = new GameServerAdministration(this._gameContext);
        await using var realPlayer = await this.CreatePlayerWithPersistedAccountAsync().ConfigureAwait(false);
        realPlayer.TryAddMoney(1_000_000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (initializing)
        {
            Mock.Get(realPlayer.ViewPlugIns.GetPlugIn<ILogoutPlugIn>()!).Setup(p => p.LogoutAsync(LogoutType.CloseGame)).Returns(async () =>
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            });
        }

        var starting = manager.StartAsync(realPlayer, TestUserLoginName).AsTask();
        if (!initializing)
        {
            Assert.That(await starting.ConfigureAwait(false), Is.True);
            Assert.That(manager.TryGetPlayer(TestUserLoginName, out var offline), Is.True);
            offline!.PlayerDisconnected += async _ =>
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            };
        }

        var stopping = manager.StopAsync(TestUserLoginName).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var concurrentStop = manager.StopAsync(TestUserLoginName).AsTask();
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(stopping.IsCompleted, Is.False);
                Assert.That(concurrentStop.IsCompleted, Is.False);
                Assert.That(manager.IsActive(TestUserLoginName), Is.True);
            });
            Assert.That(await administration.HasOfflineSessionAsync(TestUserLoginName, default).ConfigureAwait(false), Is.True);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(starting, stopping, concurrentStop).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That(await administration.HasOfflineSessionAsync(TestUserLoginName, default).ConfigureAwait(false), Is.False);
    }

    /// <summary>An automatic stop from the AI loop completes without waiting on its own loop task.</summary>
    [Test]
    public async Task InsufficientZenStopsWithoutBlockingExternalStopAsync()
    {
        var manager = this._gameContext.OfflinePlayerManager;
        var configuration = this._gameContext.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>()!.Configuration ??= new MuHelperConfiguration();
        await using var realPlayer = await this.CreatePlayerWithPersistedAccountAsync().ConfigureAwait(false);
        realPlayer.TryAddMoney(1_000_000);
        using (var context = this._contextProvider.CreateNewPlayerContext(this._gameContext.Configuration))
        {
            var account = await context.GetAccountByLoginNameAsync(TestUserLoginName).ConfigureAwait(false);
            var character = account!.Characters.Single();
            var level = context.CreateNew<StatAttributeDefinition>();
            level.Attribute = Stats.Level;
            level.BaseValue = 100;
            character.CharacterClass!.StatAttributes.Add(level);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        Assert.That(await manager.StartAsync(realPlayer, TestUserLoginName).ConfigureAwait(false), Is.True);
        Assert.That(manager.TryGetPlayer(TestUserLoginName, out var offline), Is.True);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offline!.PlayerDisconnected += _ =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };
        await offline.RunPersistenceExclusiveAsync(() =>
        {
            offline.Attributes![Stats.Level] = 100;
            offline.Money = 0;
            configuration.PayInterval = TimeSpan.Zero;
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        Assert.That(offline.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));
        Assert.That(MuHelperZenCostCalculator.Calculate(offline, configuration, offline.StartTimestamp), Is.GreaterThan(0));
        Assert.That(offline.Account!.IsBot, Is.False);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await manager.StopAsync(TestUserLoginName).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(manager.IsActive(TestUserLoginName), Is.False);
        Assert.That((await this._gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<OfflinePlayer>(), Is.Empty);
        Assert.That(offline.IsDisposed, Is.True);
    }

    /// <summary>
    /// Creates a player whose account is stored in the in-memory repository,
    /// so that <see cref="OfflinePlayer.InitializeAsync"/> can find it.
    /// </summary>
    private async ValueTask<Player> CreatePlayerWithPersistedAccountAsync()
    {
        var config = this._gameContext.Configuration;

        // Create persisted account + character in the in-memory repository.
        using (var ctx = this._contextProvider.CreateNewPlayerContext(config))
        {
            var account = ctx.CreateNew<MUnique.OpenMU.DataModel.Entities.Account>();
            account.LoginName = TestUserLoginName;

            var character = ctx.CreateNew<MUnique.OpenMU.DataModel.Entities.Character>();
            character.Name = TestCharacterName;

            if (config.CharacterClasses.FirstOrDefault() is { } existingClass)
            {
                character.CharacterClass = existingClass;
            }
            else
            {
                // Build a minimal CharacterClass so OnPlayerEnteredWorldAsync does not throw.
                var characterClass = ctx.CreateNew<MUnique.OpenMU.DataModel.Configuration.CharacterClass>();
                characterClass.HomeMap = config.Maps.FirstOrDefault();
                character.CharacterClass = characterClass;
            }

            account.Characters.Add(character);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }

        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        player.Account!.LoginName = TestUserLoginName;

        // Ensure the mock character name matches the persisted one.
        var mockCharacter = player.SelectedCharacter!;
        mockCharacter.Name = TestCharacterName;
        mockCharacter.CharacterClass ??= player.Account.UnlockedCharacterClasses.FirstOrDefault();
        if (mockCharacter.CharacterClass is null && config.CharacterClasses.FirstOrDefault() is { } cc)
        {
            mockCharacter.CharacterClass = cc;
        }

        return player;
    }
}
