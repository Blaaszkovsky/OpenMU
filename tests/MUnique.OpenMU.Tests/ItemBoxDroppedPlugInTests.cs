// <copyright file="ItemBoxDroppedPlugInTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Pathfinding;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests box consumption through the inventory action and the configured reward generator.
/// </summary>
[TestFixture]
public class ItemBoxDroppedPlugInTests
{
    /// <summary>
    /// A box without a matching level table must stay in the inventory.
    /// </summary>
    /// <param name="requiredLevel">The minimum character level of the available table.</param>
    /// <param name="boxLevel">The box level.</param>
    [TestCase(0, 1)]
    [TestCase(400, 0)]
    public async ValueTask InvalidBoxKeepsItsInventorySlotAsync(short requiredLevel, byte boxLevel)
    {
        var generator = new Mock<IDropGenerator>();
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext(dropGenerator: generator.Object);
        context.StopPeriodicTasks();
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        var definition = new BasicModel.ItemDefinition { Width = 1, Height = 1, IsDroppable = true };
        definition.DropItems.Add(new BasicModel.ItemDropItemGroup { SourceItemLevel = 0, RequiredCharacterLevel = requiredLevel });
        var box = new BasicModel.Item { Definition = definition, Level = boxLevel };
        const byte inventorySlot = 20;
        Assert.That(await player.Inventory!.AddItemAsync(inventorySlot, box).ConfigureAwait(false), Is.True);
        context.PlugInManager.RegisterPlugInAtPlugInPoint<IItemDropPlugIn>(new ItemBoxDroppedPlugIn());

        await new DropItemAction().DropItemAsync(player, inventorySlot, new Point(10, 10)).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(inventorySlot), Is.SameAs(box));
        generator.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A valid money reward consumes its source box exactly once.
    /// </summary>
    [Test]
    public async ValueTask ValidBoxGrantsConfiguredMoneyDropAsync()
    {
        var generator = new Mock<IDropGenerator>();
        generator.Setup(g => g.GenerateItemDrop(It.IsAny<IEnumerable<DropItemGroup>>()))
            .Returns((null, (uint?)12345, ItemDropEffect.Undefined));
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext(dropGenerator: generator.Object);
        context.StopPeriodicTasks();
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        var definition = new BasicModel.ItemDefinition { Width = 1, Height = 1, IsDroppable = true };
        definition.DropItems.Add(new BasicModel.ItemDropItemGroup { SourceItemLevel = 0 });
        var box = new BasicModel.Item { Definition = definition };
        const byte inventorySlot = 20;
        Assert.That(await player.Inventory!.AddItemAsync(inventorySlot, box).ConfigureAwait(false), Is.True);
        context.PlugInManager.RegisterPlugInAtPlugInPoint<IItemDropPlugIn>(new ItemBoxDroppedPlugIn());

        await new DropItemAction().DropItemAsync(player, inventorySlot, new Point(10, 10)).ConfigureAwait(false);
        await new DropItemAction().DropItemAsync(player, inventorySlot, new Point(10, 10)).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(inventorySlot), Is.Null);
        Assert.That(player.CurrentMap!.GetDropsInRange(player.Position, 1).OfType<DroppedMoney>().Single().Amount, Is.EqualTo(12345));
        generator.Verify(g => g.GenerateItemDrop(It.Is<IEnumerable<DropItemGroup>>(groups => groups.Single() == definition.DropItems.Single())), Times.Once);
    }

    /// <summary>
    /// A generated item appears on the map and its source box cannot be opened again.
    /// </summary>
    [Test]
    public async ValueTask ValidBoxGrantsOneItemDropAsync()
    {
        var reward = new BasicModel.Item { Definition = new BasicModel.ItemDefinition { Width = 1, Height = 1 } };
        var generator = new Mock<IDropGenerator>();
        generator.Setup(g => g.GenerateItemDrop(It.IsAny<IEnumerable<DropItemGroup>>()))
            .Returns((reward, null, ItemDropEffect.Undefined));
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext(dropGenerator: generator.Object);
        context.StopPeriodicTasks();
        var player = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        var definition = new BasicModel.ItemDefinition { Width = 1, Height = 1, IsDroppable = true };
        definition.DropItems.Add(new BasicModel.ItemDropItemGroup());
        var box = new BasicModel.Item { Definition = definition };
        const byte inventorySlot = 20;
        Assert.That(await player.Inventory!.AddItemAsync(inventorySlot, box).ConfigureAwait(false), Is.True);
        context.PlugInManager.RegisterPlugInAtPlugInPoint<IItemDropPlugIn>(new ItemBoxDroppedPlugIn());

        var action = new DropItemAction();
        await action.DropItemAsync(player, inventorySlot, new Point(10, 10)).ConfigureAwait(false);
        await action.DropItemAsync(player, inventorySlot, new Point(10, 10)).ConfigureAwait(false);

        Assert.That(player.Inventory.GetItem(inventorySlot), Is.Null);
        Assert.That(player.CurrentMap!.GetDropsInRange(player.Position, 1).OfType<DroppedItem>().Single().Item, Is.SameAs(reward));
        generator.Verify(g => g.GenerateItemDrop(It.IsAny<IEnumerable<DropItemGroup>>()), Times.Once);
    }
}
