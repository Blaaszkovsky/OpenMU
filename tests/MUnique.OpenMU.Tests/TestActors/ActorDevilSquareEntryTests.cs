// <copyright file="ActorDevilSquareEntryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.TestActors;

using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.TestActors;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using Moq;

/// <summary>
/// Exercises the fixed actor entry command through the normal Devil Square action.
/// </summary>
[TestFixture]
public sealed class ActorDevilSquareEntryTests
{
    private InMemoryPersistenceContextProvider _provider = null!;

    private GameContext _gameContext = null!;

    private GameConfiguration _configuration = null!;

    private readonly List<Player> _players = [];

    /// <summary>
    /// Initializes the in-memory Season Six data used by the real entry action.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        this._provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(this._provider, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true)
            .ConfigureAwait(false);
        using var persistenceContext = this._provider.CreateNewContext();
        this._configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var plugIns = new PlugInManager([], NullLoggerFactory.Instance, null, null);
        var mediator = new ConfigurationChangeMediator();
        var mapInitializer = new MapInitializer(this._configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, mediator);
        this._gameContext = new GameContext(this._configuration, this._provider, mapInitializer, NullLoggerFactory.Instance, plugIns, NullDropGenerator.Instance, mediator);
        mapInitializer.PlugInManager = plugIns;
        mapInitializer.PathFinderPool = this._gameContext.PathFinderPool;
        this._gameContext.StopPeriodicTasks();
    }

    /// <summary>
    /// Disposes entered test players and their game context.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var player in this._players)
        {
            await player.DisconnectAsync().ConfigureAwait(false);
        }

        await this._gameContext.DisposeAsync().ConfigureAwait(false);
        this._players.Clear();
    }

    /// <summary>
    /// A valid actor ticket is consumed only after the real level 7 action admits and warps it.
    /// </summary>
    [Test]
    public async Task FixedActorCommandEntersWithOwnedTicketAsync()
    {
        var definition = this.GetDevilSquareDefinition();
        var actor = await this.CreateScriptedActorAsync("gda06").ConfigureAwait(false);
        var openGame = await this._gameContext.MiniGames.GetOrCreateAsync(definition, actor).ConfigureAwait(false);
        Assert.That(openGame.State, Is.EqualTo(MiniGameState.Open), "The ordinary action may enter only while the real shared game is open.");
        var ticket = await this.AddTicketAsync(actor, definition).ConfigureAwait(false);
        var startingMoney = actor.Money;

        var response = await this.SendEntryAsync("gda06", actor).ConfigureAwait(false);

        Assert.That(response.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(response.GetProperty("game_level").GetInt32(), Is.EqualTo(7));
        Assert.That(actor.CurrentMiniGame?.Definition.Type, Is.EqualTo(MiniGameType.DevilSquare));
        Assert.That(actor.CurrentMiniGame?.Definition.GameLevel, Is.EqualTo(7));
        Assert.That(actor.CurrentMap, Is.SameAs(openGame.Map));
        Assert.That(actor.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
        Assert.That(actor.Inventory!.GetItem((byte)ticket.ItemSlot), Is.Null);
        Assert.That(actor.Money, Is.EqualTo(startingMoney - Math.Max(definition.EntranceFee, 0)));
    }

    /// <summary>
    /// A missing ticket and a level refusal leave the actor's map, item and Zen unchanged.
    /// </summary>
    [Test]
    public async Task MissingTicketAndOutOfRangeLevelDoNotConsumeZenOrInventoryAsync()
    {
        var definition = this.GetDevilSquareDefinition();
        var actor = await this.CreateScriptedActorAsync("gda06").ConfigureAwait(false);
        var startingMoney = actor.Money;
        var homeMap = actor.CurrentMap;

        var missing = await this.SendEntryAsync("gda06", actor).ConfigureAwait(false);

        Assert.That(missing.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(missing.GetProperty("code").GetString(), Is.EqualTo(ActorErrorCodes.MiniGameEntryRefused));
        Assert.That(missing.GetProperty("enter_result").GetString(), Is.EqualTo(nameof(EnterResult.Failed)));
        Assert.That(actor.CurrentMiniGame, Is.Null);
        Assert.That(actor.CurrentMap, Is.SameAs(homeMap));
        Assert.That(actor.Money, Is.EqualTo(startingMoney));

        var ticket = await this.AddTicketAsync(actor, definition).ConfigureAwait(false);
        actor.Attributes![Stats.Level] = definition.MaximumCharacterLevel + 1;

        var tooHigh = await this.SendEntryAsync("gda06", actor).ConfigureAwait(false);

        Assert.That(tooHigh.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(tooHigh.GetProperty("enter_result").GetString(), Is.EqualTo(nameof(EnterResult.CharacterLevelTooHigh)));
        Assert.That(actor.CurrentMiniGame, Is.Null);
        Assert.That(actor.CurrentMap, Is.SameAs(homeMap));
        Assert.That(actor.Inventory!.GetItem((byte)ticket.ItemSlot), Is.SameAs(ticket.Item));
        Assert.That(ticket.Item.Durability, Is.EqualTo(1));
        Assert.That(actor.Money, Is.EqualTo(startingMoney));
    }

    /// <summary>
    /// The capacity refusal is returned from the ordinary admission action without charging the actor.
    /// </summary>
    [Test]
    public async Task FullDevilSquareRefusesScriptedEntrantWithoutConsumingTicketOrZenAsync()
    {
        var definition = this.GetDevilSquareDefinition();
        var action = new EnterMiniGameAction();
        MiniGameContext? game = null;

        for (var index = 0; index < definition.MaximumPlayerCount; index++)
        {
            var player = await this.CreateRegularPlayerAsync($"Full{index}").ConfigureAwait(false);
            var ticket = await this.AddTicketAsync(player, definition).ConfigureAwait(false);
            game ??= await this._gameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
            await action.TryEnterMiniGameAsync(player, MiniGameType.DevilSquare, 7, (byte)ticket.ItemSlot).ConfigureAwait(false);
            Assert.That(player.CurrentMiniGame, Is.SameAs(game));
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        }

        var actor = await this.CreateScriptedActorAsync("gda06").ConfigureAwait(false);
        var ticketItem = await this.AddTicketAsync(actor, definition).ConfigureAwait(false);
        var startingMoney = actor.Money;

        var response = await this.SendEntryAsync("gda06", actor).ConfigureAwait(false);

        Assert.That(game!.PlayerCount, Is.EqualTo(definition.MaximumPlayerCount));
        Assert.That(response.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(response.GetProperty("code").GetString(), Is.EqualTo(ActorErrorCodes.MiniGameFull));
        Assert.That(response.GetProperty("enter_result").GetString(), Is.EqualTo(nameof(EnterResult.Full)));
        Assert.That(actor.CurrentMiniGame, Is.Null);
        Assert.That(actor.Inventory!.GetItem((byte)ticketItem.ItemSlot), Is.SameAs(ticketItem.Item));
        Assert.That(ticketItem.Item.Durability, Is.EqualTo(1));
        Assert.That(actor.Money, Is.EqualTo(startingMoney));
        await game.DisposeAsync().ConfigureAwait(false);
    }

    private static BotsController CreateBotsController()
    {
        var locator = new Mock<IGameServerContextLocator>();
        locator.Setup(contextLocator => contextLocator.Contexts).Returns([]);
        return new BotsController(locator.Object, NullLogger<BotsController>.Instance);
    }

    private MiniGameDefinition GetDevilSquareDefinition()
        => this._configuration.MiniGameDefinitions.Single(definition => definition.Type == MiniGameType.DevilSquare && definition.GameLevel == 7);

    private async ValueTask<ScriptedPlayer> CreateScriptedActorAsync(string loginName)
    {
        var template = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var account = template.Account!;
        var character = template.SelectedCharacter!;
        account.LoginName = loginName;
        character.Name = $"Actor-{loginName}";
        character.CharacterClass!.IsMasterClass = true;
        var actor = new ScriptedPlayer(this._gameContext);
        Assert.That(await actor.EnterWorldAsync(account, character).ConfigureAwait(false), Is.True);
        actor.Attributes![Stats.Level] = 400;
        actor.IsAlive = true;
        this._players.Add(actor);
        return actor;
    }

    private async ValueTask<Player> CreateRegularPlayerAsync(string name)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        player.SelectedCharacter!.Name = name;
        player.SelectedCharacter.CharacterClass!.IsMasterClass = true;
        player.Attributes![Stats.Level] = 400;
        player.IsAlive = true;
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        var map = (await this._gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        var point = map.Terrain.GetWalkableCoordinate(map.SafeZoneSpawnGate);
        Assert.That(point, Is.Not.Null);
        var gate = new ExitGate { Map = map.Definition, X1 = point!.Value.X, X2 = point.Value.X, Y1 = point.Value.Y, Y2 = point.Value.Y };
        await player.WarpToAsync(gate).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        this._players.Add(player);
        return player;
    }

    private async ValueTask<(Item Item, int ItemSlot)> AddTicketAsync(Player player, MiniGameDefinition definition)
    {
        var item = new Item
        {
            Definition = definition.TicketItem!,
            Level = (byte)definition.TicketItemLevel,
            Durability = 1,
        };
        Assert.That(await player.Inventory!.AddItemAsync(12, item).ConfigureAwait(false), Is.True);
        return (item, 12);
    }

    private async ValueTask<JsonElement> SendEntryAsync(string loginName, ScriptedPlayer actor)
    {
        var registry = new Mock<IActorRegistry>(MockBehavior.Strict);
        registry.Setup(actorRegistry => actorRegistry.FindAsync(loginName)).Returns(ValueTask.FromResult<ScriptedPlayer?>(actor));
        var response = new ResponseCollector();
        var handler = new ActorProtocolHandler(registry.Object, CreateBotsController());
        await handler.HandleLineAsync($"{{\"id\":\"entry\",\"cmd\":\"enterDS7\",\"actor\":\"{loginName}\"}}", response.WriteAsync, CancellationToken.None)
            .ConfigureAwait(false);
        return response.Single();
    }

    private sealed class ResponseCollector : List<JsonElement>
    {
        public ValueTask WriteAsync(string line)
        {
            this.Add(JsonDocument.Parse(line).RootElement.Clone());
            return ValueTask.CompletedTask;
        }
    }
}
