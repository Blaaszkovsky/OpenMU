// <copyright file="MiniGameAdmissionLimitTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Checks configured event limits through the actual admission and ticket-consumption action.
/// </summary>
[TestFixture]
public class MiniGameAdmissionLimitTests
{
    /// <summary>
    /// Admits ten players, rejects the eleventh without consuming its ticket, and rejects a duplicate entry.
    /// </summary>
    [TestCase(MiniGameType.BloodCastle)]
    [TestCase(MiniGameType.DevilSquare)]
    [TestCase(MiniGameType.IllusionTemple)]
    public async Task ConfiguredTenPlayerLimitPreservesRejectedTicketsAsync(MiniGameType type)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var persistenceContext = provider.CreateNewContext();
        var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        Assert.That(configuration.MiniGameDefinitions.Where(definition => definition.Type == type).Select(definition => definition.MaximumPlayerCount), Is.All.EqualTo(10));
        var definition = configuration.MiniGameDefinitions.Single(definition => definition.Type == type && definition.GameLevel == 1);
        var plugIns = new PlugInManager([], NullLoggerFactory.Instance, null, null);
        var mediator = new ConfigurationChangeMediator();
        var mapInitializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, mediator);
        await using var gameContext = new GameContext(configuration, provider, mapInitializer, NullLoggerFactory.Instance, plugIns, NullDropGenerator.Instance, mediator);
        mapInitializer.PlugInManager = plugIns;
        mapInitializer.PathFinderPool = gameContext.PathFinderPool;
        gameContext.StopPeriodicTasks();
        var homeMap = (await gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        var homePoint = homeMap.Terrain.GetWalkableCoordinate(homeMap.SafeZoneSpawnGate);
        Assert.That(homePoint, Is.Not.Null);
        var homeGate = new ExitGate { Map = homeMap.Definition, X1 = homePoint!.Value.X, X2 = homePoint.Value.X, Y1 = homePoint.Value.Y, Y2 = homePoint.Value.Y };
        var action = new EnterMiniGameAction();
        var entrants = new List<Player>();
        MiniGameContext? game = null;
        try
        {
            for (var index = 0; index < 11; index++)
            {
                var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
                var inventory = player.Inventory!;
                player.SelectedCharacter!.Name = $"Cap{index}";
                player.Attributes![Stats.Level] = definition.MinimumCharacterLevel;
                await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
                player.IsAlive = true;
                await player.WarpToAsync(homeGate).ConfigureAwait(false);
                await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                Assert.That(player.CurrentMap, Is.Not.Null, "Each entrant starts inside the home map.");
                var ticket = new Item { Definition = definition.TicketItem, Level = (byte)definition.TicketItemLevel, Durability = 1 };
                Assert.That(await inventory.AddItemAsync(12, ticket).ConfigureAwait(false), Is.True);
                var startingMoney = player.Money;
                game ??= await gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
                await action.TryEnterMiniGameAsync(player, type, 1, 12).ConfigureAwait(false);
                if (index < 10)
                {
                    Assert.That(player.CurrentMiniGame, Is.Not.Null, $"Entrant {index + 1} must be admitted.");
                    game ??= player.CurrentMiniGame!;
                    Assert.That(player.CurrentMiniGame, Is.SameAs(game));
                    Assert.That(inventory.GetItem(12), Is.Null);
                    await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                    Assert.That(game.PlayerCount, Is.EqualTo(index + 1));
                }
                else
                {
                    Assert.That(player.CurrentMiniGame, Is.Null);
                    Assert.That(player.CurrentMap!.Definition.Number, Is.EqualTo(0));
                    Assert.That(inventory.GetItem(12), Is.SameAs(ticket));
                    Assert.That(ticket.Durability, Is.EqualTo(1));
                    Assert.That(player.Money, Is.EqualTo(startingMoney));
                    Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMiniGameEnterResultPlugIn>()!)
                        .Verify(view => view.ShowResultAsync(type, EnterResult.Full), Times.Once);
                }

                entrants.Add(player);
            }

            var first = entrants[0];
            var firstInventory = first.Inventory!;
            var spareTicket = new Item { Definition = definition.TicketItem, Level = (byte)definition.TicketItemLevel, Durability = 1 };
            Assert.That(await firstInventory.AddItemAsync(12, spareTicket).ConfigureAwait(false), Is.True);
            var moneyBeforeDuplicate = first.Money;
            await action.TryEnterMiniGameAsync(first, type, 1, 12).ConfigureAwait(false);
            Assert.That(game!.PlayerCount, Is.EqualTo(10));
            Assert.That(firstInventory.GetItem(12), Is.SameAs(spareTicket));
            Assert.That(spareTicket.Durability, Is.EqualTo(1));
            Assert.That(first.Money, Is.EqualTo(moneyBeforeDuplicate));
            Mock.Get(first.ViewPlugIns.GetPlugIn<IShowMiniGameEnterResultPlugIn>()!)
                .Verify(view => view.ShowResultAsync(type, EnterResult.Failed), Times.Once);
        }
        finally
        {
            if (game is not null)
            {
                await game.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
