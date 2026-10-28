// <copyright file="RemoveSeedSphereCrafting.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Craftings;

using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// Crafting to remove a mounted seed sphere from a socket item.
/// </summary>
public class RemoveSeedSphereCrafting : SimpleItemCraftingHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoveSeedSphereCrafting"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    public RemoveSeedSphereCrafting(SimpleCraftingSettings settings)
        : base(settings)
    {
    }

    /// <summary>
    /// Gets the reference to the socket item which must be specified in the <see cref="ItemCraftingRequiredItem.Reference"/>.
    /// </summary>
    public static byte SocketItemReference { get; } = 0x88;

    /// <inheritdoc />
    protected override CraftingResult? ValidateMix(Player player, IList<CraftingRequiredItemLink> requiredItems, byte socketSlot)
    {
        var socketItem = requiredItems.Single(i => i.ItemRequirement.Reference == SocketItemReference).Items.Single();
        if (socketItem.ItemOptions.Any(optionLink => optionLink.ItemOption?.OptionType == ItemOptionTypes.SocketOption && optionLink.Index == socketSlot))
        {
            return null;
        }

        player.Logger.LogWarning("Cannot remove a seed sphere: socket slot {socketSlot} is empty.", socketSlot);
        return CraftingResult.LackingMixItems;
    }

    /// <inheritdoc />
    protected override async ValueTask<List<Item>> CreateOrModifyResultItemsAsync(IList<CraftingRequiredItemLink> requiredItems, Player player, byte socketSlot, byte successRate)
    {
        var socketItem = requiredItems.Single(i => i.ItemRequirement.Reference == SocketItemReference).Items.Single();

        var socketOption = socketItem.ItemOptions
            .First(optionLink => optionLink.ItemOption?.OptionType == ItemOptionTypes.SocketOption && optionLink.Index == socketSlot);
        socketItem.ItemOptions.Remove(socketOption);
        await player.PersistenceContext.DeleteAsync(socketOption).ConfigureAwait(false);

        if (socketOption.Index < 3
            && socketItem.ItemOptions.FirstOrDefault(o => o.ItemOption?.OptionType == ItemOptionTypes.SocketBonusOption) is { } bonusOption)
        {
            socketItem.ItemOptions.Remove(bonusOption);
            await player.PersistenceContext.DeleteAsync(bonusOption).ConfigureAwait(false);
        }

        return new List<Item> { socketItem };
    }
}
