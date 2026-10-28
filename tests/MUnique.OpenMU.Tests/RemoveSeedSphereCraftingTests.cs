// <copyright file="RemoveSeedSphereCraftingTests.cs" company="MUnique">
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
/// Tests validation and item/currency effects when removing a seed sphere.
/// </summary>
[TestFixture]
public class RemoveSeedSphereCraftingTests
{
    /// <summary>
    /// Verifies a slot without a mounted sphere is rejected without changing the item or money.
    /// </summary>
    [TestCase((byte)0)]
    [TestCase((byte)5)]
    public async ValueTask EmptyOrOutOfRangeSocketSlotIsRejectedWithoutSideEffectsAsync(byte socketSlot)
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        await using var player = scenario.Player;

        var before = CaptureState(player, scenario.SocketItem);
        (CraftingResult Result, Item? Item)? result = null;
        Exception? exception = null;
        try
        {
            result = await new RemoveSeedSphereCrafting(scenario.Settings).DoMixAsync(player, socketSlot).ConfigureAwait(false);
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
    /// Verifies a valid removal costs the configured price, removes its bonus for a slot below three, and preserves other options.
    /// </summary>
    [Test]
    public async ValueTask ValidSocketSlotRemovesSphereAndAssociatedBonusAsync()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        await using var player = scenario.Player;

        var initialMoney = player.Money;
        var result = await new RemoveSeedSphereCrafting(scenario.Settings).DoMixAsync(player, 1).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result, Is.EqualTo(CraftingResult.Success));
            Assert.That(result.Item, Is.SameAs(scenario.SocketItem));
            Assert.That(player.Money, Is.EqualTo(initialMoney - scenario.Settings.Money));
            Assert.That(player.TemporaryStorage!.Items, Is.EquivalentTo(new[] { scenario.SocketItem }));
            Assert.That(scenario.SocketItem.ItemOptions, Has.Count.EqualTo(2));
            Assert.That(scenario.SocketItem.ItemOptions, Does.Contain(scenario.OtherSocketOption));
            Assert.That(scenario.SocketItem.ItemOptions, Does.Contain(scenario.UnrelatedOption));
            Assert.That(scenario.SocketItem.ItemOptions, Does.Not.Contain(scenario.RemovedSocketOption));
            Assert.That(scenario.SocketItem.ItemOptions, Does.Not.Contain(scenario.SocketBonusOption));
        });
    }

    private static async ValueTask<Scenario> CreateScenarioAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        var context = player.PersistenceContext;

        var socketItemDefinition = CreateDefinition(context, 201);
        socketItemDefinition.MaximumSockets = 5;
        var socketItem = context.CreateNew<Item>();
        socketItem.Definition = socketItemDefinition;
        socketItem.SocketCount = 5;

        var removedSocketOption = CreateOption(context, ItemOptionTypes.SocketOption, 1);
        var otherSocketOption = CreateOption(context, ItemOptionTypes.SocketOption, 2);
        var unrelatedOption = CreateOption(context, ItemOptionTypes.Option, 3);
        var bonusOption = CreateOption(context, ItemOptionTypes.SocketBonusOption, 4);
        var removedSocketLink = CreateOptionLink(context, removedSocketOption, 1);
        var otherSocketLink = CreateOptionLink(context, otherSocketOption, 4);
        var unrelatedLink = CreateOptionLink(context, unrelatedOption, 0);
        var bonusLink = CreateOptionLink(context, bonusOption, 0);
        socketItem.ItemOptions.Add(removedSocketLink);
        socketItem.ItemOptions.Add(otherSocketLink);
        socketItem.ItemOptions.Add(unrelatedLink);
        socketItem.ItemOptions.Add(bonusLink);

        var settings = context.CreateNew<SimpleCraftingSettings>();
        settings.SuccessPercent = 100;
        settings.Money = 1_000_000;
        var requirement = context.CreateNew<ItemCraftingRequiredItem>();
        requirement.MinimumAmount = 1;
        requirement.MaximumAmount = 1;
        requirement.MinimumItemLevel = 0;
        requirement.MaximumItemLevel = 15;
        requirement.SuccessResult = MixResult.StaysAsIs;
        requirement.FailResult = MixResult.StaysAsIs;
        requirement.Reference = RemoveSeedSphereCrafting.SocketItemReference;
        requirement.PossibleItems.Add(socketItemDefinition);
        settings.RequiredItems.Add(requirement);

        Assert.That(await player.TemporaryStorage!.AddItemAsync(0, socketItem).ConfigureAwait(false), Is.True);
        var handler = new RemoveSeedSphereCrafting(settings);
        var precondition = handler.TryGetRequiredItems(player, out var requiredItems, out var successRate);
        Assert.Multiple(() =>
        {
            Assert.That(precondition, Is.Null);
            Assert.That(requiredItems, Has.Count.EqualTo(1));
            Assert.That(successRate, Is.EqualTo(100));
        });

        player.Money = 2_000_000;
        return new Scenario(player, settings, socketItem, removedSocketLink, otherSocketLink, unrelatedLink, bonusLink);
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

    private static IncreasableItemOption CreateOption(IContext context, ItemOptionType optionType, int number)
    {
        var option = context.CreateNew<IncreasableItemOption>();
        option.OptionType = optionType;
        option.Number = number;
        return option;
    }

    private static ItemOptionLink CreateOptionLink(IContext context, IncreasableItemOption option, int index)
    {
        var link = context.CreateNew<ItemOptionLink>();
        link.ItemOption = option;
        link.Index = index;
        return link;
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

    private sealed record Scenario(
        Player Player,
        SimpleCraftingSettings Settings,
        Item SocketItem,
        ItemOptionLink RemovedSocketOption,
        ItemOptionLink OtherSocketOption,
        ItemOptionLink UnrelatedOption,
        ItemOptionLink SocketBonusOption);

    private sealed record StateSnapshot(int Money, Item[] TemporaryItems, SocketOptionSnapshot[] SocketOptions);

    private sealed record SocketOptionSnapshot(int Index, int Level, Guid? ItemOptionId, int? Number, Guid? OptionTypeId);
}
