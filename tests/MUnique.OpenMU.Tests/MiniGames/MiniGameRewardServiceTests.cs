// <copyright file="MiniGameRewardServiceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;
using EfModel = MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>Exercises generated rewards against the real persistence model and inventory conversion.</summary>
[TestFixture]
public class MiniGameRewardServiceTests
{
    /// <summary>Shared reward callers must wait for an independent save before creating or inserting tracked items.</summary>
    [Test]
    public async Task RewardWaitsForIndependentPersistenceOperationAsync()
    {
        await using var world = await RewardWorld.CreateAsync().ConfigureAwait(false);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingGate = Task.Run(() => world.Player.RunPersistenceExclusiveAsync(async () =>
        {
            acquired.SetResult();
            await release.Task.ConfigureAwait(false);
        }).AsTask());
        Task<(int BonusScore, int GivenMoney)>? reward = null;
        try
        {
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            reward = world.Service.GiveRewardsAndGetBonusScoreAsync(world.Player, 1);

            // Generator, factory, inventory, and mock view all complete synchronously in this fixture.
            // With no persistence gate the entire reward completes before this assertion; no sleep is needed.
            Assert.Multiple(() =>
            {
                Assert.That(reward.IsCompleted, Is.False, "An independent player operation must block the shared reward entry point.");
                Assert.That(world.CreatedItems, Is.Empty, "The persistence factory must not run while another operation owns the context.");
                Assert.That(world.Storage.Items, Is.Empty);
                world.Appearance.Verify(view => view.ItemAppearAsync(It.IsAny<Item>()), Times.Never);
            });
        }
        finally
        {
            release.TrySetResult();
            await holdingGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (reward is not null)
            {
                await reward.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        var item = world.CreatedItems.Single();
        Assert.That(world.Storage.Items.Single(), Is.SameAs(item));
        world.Appearance.Verify(view => view.ItemAppearAsync(item), Times.Once);
    }

    /// <summary>Direct rewards must use the player's factory, including when only extension space remains.</summary>
    /// <param name="extensionOnly">Whether only an inventory extension has room.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task GeneratedRewardUsesPersistentInventoryItemAsync(bool extensionOnly)
    {
        await using var world = await RewardWorld.CreateAsync(extensionOnly ? (byte)1 : (byte)0).ConfigureAwait(false);
        if (extensionOnly)
        {
            await world.FillMainInventoryAsync().ConfigureAwait(false);
        }

        var slot = world.Player.Inventory!.CheckInvSpace(world.GeneratedSample);
        Assert.That(slot, Is.Not.Null);
        if (extensionOnly)
        {
            Assert.That(slot, Is.EqualTo(InventoryConstants.FirstExtensionItemSlotIndex));
        }

        await world.Service.GiveRewardsAndGetBonusScoreAsync(world.Player, 1).ConfigureAwait(false);

        var item = (EfModel.Item)world.Player.Inventory.GetItem(slot!.Value)!;
        Assert.Multiple(() =>
        {
            Assert.That(world.CreatedItems, Has.Count.EqualTo(1));
            Assert.That(item, Is.SameAs(world.CreatedItems.Single()));
            Assert.That(item.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(item.Definition, Is.SameAs(world.ItemDefinition));
            Assert.That(item.DefinitionId, Is.EqualTo(world.ItemDefinition.Id));
            Assert.That(item.RawItemStorage, Is.SameAs(world.Storage));
            Assert.That(item.ItemStorageId, Is.EqualTo(world.Storage.Id));
            Assert.That(item.ItemSlot, Is.EqualTo(slot.Value));
            Assert.That(world.Storage.RawItems.Count(candidate => candidate.Definition == world.ItemDefinition), Is.EqualTo(1));
            Assert.That(item.ItemOptions.Single().ItemOption, Is.SameAs(world.Option));
            Assert.That(item.ItemOptions.Single(), Is.TypeOf<EfModel.ItemOptionLink>());
            Assert.That(world.Drops, Is.Empty);
        });
        world.Appearance.Verify(view => view.ItemAppearAsync(item), Times.Once);
        world.Appearance.VerifyNoOtherCalls();
    }

    /// <summary>A full inventory keeps the reward temporary until an ordinary pickup can persist it.</summary>
    [Test]
    public async Task FullInventoryRewardFallsBackToOrdinaryPickupAsync()
    {
        await using var world = await RewardWorld.CreateAsync().ConfigureAwait(false);
        await world.FillMainInventoryAsync().ConfigureAwait(false);
        Assert.That(world.Player.Inventory!.CheckInvSpace(world.GeneratedSample), Is.Null);

        await world.Service.GiveRewardsAndGetBonusScoreAsync(world.Player, 1).ConfigureAwait(false);

        var drop = world.Drops.Single();
        Assert.Multiple(() =>
        {
            Assert.That(drop.Item, Is.TypeOf<TemporaryItem>());
            Assert.That(world.CreatedItems, Is.Empty, "A ground reward must not create an unowned persistent item.");
            Assert.That(drop.IsPlayerAnOwner(world.Player), Is.True);
        });
        world.Appearance.Verify(view => view.ItemAppearAsync(It.IsAny<Item>()), Times.Never);
        await world.Player.Inventory.RemoveItemAsync(world.Player.Inventory.GetItem(12)!).ConfigureAwait(false);
        world.Player.Position = drop.Position;

        await new PickupItemAction().PickupItemAsync(world.Player, drop.Id).ConfigureAwait(false);

        var item = world.CreatedItems.Single();
        Assert.Multiple(() =>
        {
            Assert.That(world.Storage.Items, Does.Contain(item));
            Assert.That(item.RawItemStorage, Is.SameAs(world.Storage));
            Assert.That(item.ItemStorageId, Is.EqualTo(world.Storage.Id));
            Assert.That(item.ItemSlot, Is.EqualTo(12));
            Assert.That(world.Drops, Is.Empty);
        });
        world.Appearance.Verify(view => view.ItemAppearAsync(item), Times.Once);
    }

    /// <summary>Explicit ground rewards must not be inserted merely because inventory space exists.</summary>
    [Test]
    public async Task ExplicitItemDropRemainsTemporaryOnGroundAsync()
    {
        await using var world = await RewardWorld.CreateAsync().ConfigureAwait(false);
        world.Reward.RewardType = MiniGameRewardType.ItemDrop;

        await world.Service.GiveRewardsAndGetBonusScoreAsync(world.Player, 1).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(world.Drops.Single().Item, Is.TypeOf<TemporaryItem>());
            Assert.That(world.Storage.Items, Is.Empty);
            Assert.That(world.CreatedItems, Is.Empty);
        });
        world.Appearance.Verify(view => view.ItemAppearAsync(It.IsAny<Item>()), Times.Never);
    }

    /// <summary>A missing live inventory must preserve the ordinary ground fallback.</summary>
    [Test]
    public async Task MissingInventoryRewardFallsBackToGroundAsync()
    {
        await using var world = await RewardWorld.CreateAsync().ConfigureAwait(false);
        var storages = (PlayerStorages)typeof(Player).GetField("_storages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.Player)!;
        typeof(PlayerStorages).GetProperty(nameof(PlayerStorages.Inventory))!.SetValue(storages, null);

        await world.Service.GiveRewardsAndGetBonusScoreAsync(world.Player, 1).ConfigureAwait(false);

        Assert.That(world.Drops.Single().Item, Is.TypeOf<TemporaryItem>());
        Assert.That(world.CreatedItems, Is.Empty);
    }

    private sealed class RewardWorld : IAsyncDisposable
    {
        private RewardWorld(Player player, EfModel.ItemStorage storage, EfModel.ItemDefinition itemDefinition, EfModel.IncreasableItemOption option, MiniGameReward reward, MiniGameRewardService service, TemporaryItem generatedSample, List<EfModel.Item> createdItems)
        {
            this.Player = player;
            this.Storage = storage;
            this.ItemDefinition = itemDefinition;
            this.Option = option;
            this.Reward = reward;
            this.Service = service;
            this.GeneratedSample = generatedSample;
            this.CreatedItems = createdItems;
        }

        public Player Player { get; }

        public EfModel.ItemStorage Storage { get; }

        public EfModel.ItemDefinition ItemDefinition { get; }

        public EfModel.IncreasableItemOption Option { get; }

        public MiniGameReward Reward { get; }

        public MiniGameRewardService Service { get; }

        public TemporaryItem GeneratedSample { get; }

        public List<EfModel.Item> CreatedItems { get; }

        public Mock<IItemAppearPlugIn> Appearance => Mock.Get(this.Player.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!);

        public IEnumerable<DroppedItem> Drops => this.Player.CurrentMap!.GetDropsInRange(new Point(128, 128), 255).OfType<DroppedItem>();

        public static async ValueTask<RewardWorld> CreateAsync(byte extensions = 0)
        {
            var configuration = PlayerTestHelper.CreateGameConfiguration();
            configuration.ItemDropDuration = TimeSpan.FromMinutes(1);
            var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
            var gameContext = new GameContext(configuration, new InMemoryPersistenceContextProvider(), initializer, NullLoggerFactory.Instance, new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
            initializer.PlugInManager = gameContext.PlugInManager;
            initializer.PathFinderPool = gameContext.PathFinderPool;
            gameContext.StopPeriodicTasks();
            var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
            player.IsAlive = true;
            var storage = new EfModel.ItemStorage { Id = Guid.NewGuid() };
            player.SelectedCharacter!.Inventory = storage;
            player.SelectedCharacter.InventoryExtensions = extensions;
            var createdItems = new List<EfModel.Item>();
            var persistence = new Mock<IPlayerContext>();
            persistence.Setup(context => context.CreateNew<Item>(It.IsAny<object?[]>())).Returns(() =>
            {
                var item = new EfModel.Item { Id = Guid.NewGuid() };
                createdItems.Add(item);
                return item;
            });
            typeof(Player).GetField($"<{nameof(Player.PersistenceContext)}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, persistence.Object);
            var storages = (PlayerStorages)typeof(Player).GetField("_storages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            storages.CreateForCharacter(player.SelectedCharacter);
            var definition = new EfModel.ItemDefinition { Id = Guid.NewGuid(), Group = 12, Number = 15, Width = 1, Height = 1, Durability = 1, MaximumItemLevel = 15 };
            var option = new EfModel.IncreasableItemOption { Id = Guid.NewGuid() };
            var optionDefinition = new EfModel.ItemOptionDefinition { Id = Guid.NewGuid(), AddsRandomly = true, AddChance = 1, MaximumOptionsPerItem = 1 };
            optionDefinition.PossibleOptions.Add(option);
            definition.PossibleItemOptions.Add(optionDefinition);
            player.GameContext.Configuration.Items.Add(definition);
            var group = new EfModel.DropItemGroup { Id = Guid.NewGuid(), Chance = 1, ItemLevel = 3 };
            group.PossibleItems.Add(definition);
            var reward = new BasicModel.MiniGameReward { Rank = 1, RewardType = MiniGameRewardType.Item, RewardAmount = 1, ItemReward = group };
            var miniGame = new BasicModel.MiniGameDefinition();
            miniGame.Rewards.Add(reward);
            var generator = new DefaultDropGenerator(player.GameContext.Configuration, Rand.GetRandomizer());
            var sample = (TemporaryItem)generator.GenerateItemDrop(group)!;
            Assert.That(sample.ItemOptions, Has.Count.EqualTo(1), "The real generator must exercise option conversion.");
            var service = new MiniGameRewardService(miniGame, player.GameContext, player.CurrentMap!, () => generator, () => TimeSpan.Zero, () => null, NullLogger.Instance, miniGame);
            return new RewardWorld(player, storage, definition, option, reward, service, sample, createdItems);
        }

        public async ValueTask FillMainInventoryAsync()
        {
            var fillerDefinition = new EfModel.ItemDefinition { Id = Guid.NewGuid(), Width = 1, Height = 1 };
            for (byte slot = InventoryConstants.EquippableSlotsCount; slot < InventoryConstants.FirstExtensionItemSlotIndex; slot++)
            {
                Assert.That(await this.Player.Inventory!.AddItemAsync(slot, new EfModel.Item { Id = Guid.NewGuid(), Definition = fillerDefinition }).ConfigureAwait(false), Is.True);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ((GameContext)this.Player.GameContext).DisposeAsync().ConfigureAwait(false);
        }
    }
}
