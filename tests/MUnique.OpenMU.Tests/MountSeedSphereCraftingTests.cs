// <copyright file="MountSeedSphereCraftingTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Craftings;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Tests the validation and item/currency effects of mounting a seed sphere.
/// </summary>
[TestFixture]
public class MountSeedSphereCraftingTests
{
    /// <summary>
    /// Verifies an out-of-range socket slot is rejected without changing ingredients, options, or money.
    /// </summary>
    [Test]
    public async ValueTask OutOfRangeSocketSlotIsRejectedWithoutSideEffectsAsync()
    {
        var scenario = await CreateScenarioAsync(socketSlot: 3, occupySlot: false).ConfigureAwait(false);
        await using var player = scenario.Player;

        var before = CaptureState(player, scenario.SocketItem);
        (CraftingResult Result, Item? Item)? result = null;
        Exception? exception = null;
        try
        {
            result = await new MountSeedSphereCrafting(scenario.Settings).DoMixAsync(player, 3).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            exception = error;
        }

        var after = CaptureState(player, scenario.SocketItem);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.Null);
            Assert.That(result?.Result, Is.EqualTo(CraftingResult.LackingMixItems));
            Assert.That(after.Money, Is.EqualTo(before.Money));
            Assert.That(after.TemporaryItems, Is.EqualTo(before.TemporaryItems));
            Assert.That(after.SocketOptions, Is.EqualTo(before.SocketOptions));
        });
    }

    /// <summary>
    /// Verifies an occupied socket slot is rejected without changing ingredients, options, or money.
    /// </summary>
    [Test]
    public async ValueTask OccupiedSocketSlotIsRejectedWithoutSideEffectsAsync()
    {
        var scenario = await CreateScenarioAsync(socketSlot: 0, occupySlot: true).ConfigureAwait(false);
        await using var player = scenario.Player;

        var before = CaptureState(player, scenario.SocketItem);
        (CraftingResult Result, Item? Item)? result = null;
        Exception? exception = null;
        try
        {
            result = await new MountSeedSphereCrafting(scenario.Settings).DoMixAsync(player, 0).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            exception = error;
        }

        var after = CaptureState(player, scenario.SocketItem);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.Null);
            Assert.That(result?.Result, Is.EqualTo(CraftingResult.LackingMixItems));
            Assert.That(after.Money, Is.EqualTo(before.Money));
            Assert.That(after.TemporaryItems, Is.EqualTo(before.TemporaryItems));
            Assert.That(after.SocketOptions, Is.EqualTo(before.SocketOptions));
        });
    }

    /// <summary>
    /// Verifies the actual mix operation still charges the configured price and mounts the sphere in a free slot.
    /// </summary>
    [Test]
    public async ValueTask ValidSocketSlotMountsSeedSphereAsync()
    {
        var scenario = await CreateScenarioAsync(socketSlot: 0, occupySlot: false).ConfigureAwait(false);
        await using var player = scenario.Player;

        var initialMoney = player.Money;
        var result = await new MountSeedSphereCrafting(scenario.Settings).DoMixAsync(player, 0).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result, Is.EqualTo(CraftingResult.Success));
            Assert.That(result.Item, Is.SameAs(scenario.SocketItem));
            Assert.That(player.Money, Is.EqualTo(initialMoney - scenario.Settings.Money));
            Assert.That(player.TemporaryStorage!.Items, Is.EquivalentTo(new[] { scenario.SocketItem }));
            Assert.That(scenario.SocketItem.ItemOptions, Has.Count.EqualTo(1));
            Assert.That(scenario.SocketItem.ItemOptions.Single().Index, Is.EqualTo(0));
            Assert.That(scenario.SocketItem.ItemOptions.Single().ItemOption, Is.SameAs(scenario.SocketOption));
            Assert.That(scenario.SocketItem.ItemOptions.Single().Level, Is.EqualTo(1));
        });
    }

    private static async ValueTask<Scenario> CreateScenarioAsync(byte socketSlot, bool occupySlot)
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        var context = player.PersistenceContext;

        var socketOptionDefinition = context.CreateNew<ItemOptionDefinition>();
        var socketOption = context.CreateNew<IncreasableItemOption>();
        socketOption.OptionType = ItemOptionTypes.SocketOption;
        socketOption.Number = 1;
        socketOptionDefinition.PossibleOptions.Add(socketOption);

        var seedSphereDefinition = CreateDefinition(context, 100);
        seedSphereDefinition.PossibleItemOptions.Add(socketOptionDefinition);
        var socketItemDefinition = CreateDefinition(context, 101);
        socketItemDefinition.MaximumSockets = 3;
        socketItemDefinition.PossibleItemOptions.Add(socketOptionDefinition);
        var chaosDefinition = CreateDefinition(context, 102);
        var creationDefinition = CreateDefinition(context, 103);

        var seedSphere = CreateItem(context, seedSphereDefinition);
        seedSphere.Level = 1;
        var socketItem = CreateItem(context, socketItemDefinition);
        socketItem.SocketCount = 3;
        var chaos = CreateItem(context, chaosDefinition);
        var creation = CreateItem(context, creationDefinition);

        if (occupySlot)
        {
            var occupiedOption = context.CreateNew<ItemOptionLink>();
            occupiedOption.ItemOption = socketOption;
            occupiedOption.Index = socketSlot;
            socketItem.ItemOptions.Add(occupiedOption);
        }

        var settings = context.CreateNew<SimpleCraftingSettings>();
        settings.SuccessPercent = 100;
        settings.Money = 1_000_000;
        settings.RequiredItems.Add(CreateRequirement(context, seedSphereDefinition, MountSeedSphereCrafting.SeedSphereReference));
        settings.RequiredItems.Add(CreateRequirement(context, socketItemDefinition, MountSeedSphereCrafting.SocketItemReference, MixResult.StaysAsIs, MixResult.StaysAsIs));
        settings.RequiredItems.Add(CreateRequirement(context, chaosDefinition));
        settings.RequiredItems.Add(CreateRequirement(context, creationDefinition));

        foreach (var (slot, item) in new[] { seedSphere, socketItem, chaos, creation }.Select((item, slot) => (slot, item)))
        {
            Assert.That(await player.TemporaryStorage!.AddItemAsync((byte)slot, item).ConfigureAwait(false), Is.True);
        }

        var craftingHandler = new MountSeedSphereCrafting(settings);
        var precondition = craftingHandler.TryGetRequiredItems(player, out var requiredItems, out var successRate);
        Assert.Multiple(() =>
        {
            Assert.That(precondition, Is.Null);
            Assert.That(requiredItems, Has.Count.EqualTo(4));
            Assert.That(successRate, Is.EqualTo(100));
        });

        player.Money = 2_000_000;

        return new Scenario(player, settings, socketItem, socketOption);
    }

    private static ItemDefinition CreateDefinition(IContext context, short number)
    {
        var definition = context.CreateNew<ItemDefinition>();
        definition.Group = 12;
        definition.Number = number;
        definition.Width = 1;
        definition.Height = 1;
        definition.Durability = 1;
        return definition;
    }

    private static Item CreateItem(IContext context, ItemDefinition definition)
    {
        var item = context.CreateNew<Item>();
        item.Definition = definition;
        item.Durability = 1;
        return item;
    }

    private static ItemCraftingRequiredItem CreateRequirement(
        IContext context,
        ItemDefinition definition,
        byte reference = 0,
        MixResult successResult = MixResult.Disappear,
        MixResult failResult = MixResult.Disappear)
    {
        var requirement = context.CreateNew<ItemCraftingRequiredItem>();
        requirement.MinimumAmount = 1;
        requirement.MaximumAmount = 1;
        requirement.MinimumItemLevel = 0;
        requirement.MaximumItemLevel = 15;
        requirement.SuccessResult = successResult;
        requirement.FailResult = failResult;
        requirement.Reference = reference;
        requirement.PossibleItems.Add(definition);
        return requirement;
    }

    private static StateSnapshot CaptureState(Player player, Item socketItem)
    {
        return new StateSnapshot(
            player.Money,
            player.TemporaryStorage!.Items.ToArray(),
            socketItem.ItemOptions
                .Select(link => new SocketOptionSnapshot(
                    link.Index,
                    link.Level,
                    (link.ItemOption as MUnique.OpenMU.Persistence.IIdentifiable)?.Id,
                    link.ItemOption?.Number,
                    link.ItemOption?.OptionType?.Id))
                .ToArray());
    }

    private sealed record Scenario(Player Player, SimpleCraftingSettings Settings, Item SocketItem, IncreasableItemOption SocketOption);

    private sealed record StateSnapshot(int Money, Item[] TemporaryItems, SocketOptionSnapshot[] SocketOptions);

    private sealed record SocketOptionSnapshot(int Index, int Level, Guid? ItemOptionId, int? Number, Guid? OptionTypeId);
}
