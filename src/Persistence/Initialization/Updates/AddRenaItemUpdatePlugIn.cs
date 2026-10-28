// <copyright file="AddRenaItemUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// This update adds the missing Rena item, which is required for the item registration feature.
/// </summary>
[PlugIn]
[Display(Name = nameof(MUnique.OpenMU.Persistence.Initialization.Properties.PlugInResources.AddRenaItemUpdatePlugIn_Name), Description = nameof(MUnique.OpenMU.Persistence.Initialization.Properties.PlugInResources.AddRenaItemUpdatePlugIn_Description), ResourceType = typeof(MUnique.OpenMU.Persistence.Initialization.Properties.PlugInResources))]
[Guid("6A1B7C3D-2E5F-4A7B-8C9D-1E1F2A3B4C6E")]
public class AddRenaItemUpdatePlugIn : UpdatePlugInBase
{
    /// <summary>
    /// The plug in name.
    /// </summary>
    internal const string PlugInName = "Add Rena Item";

    /// <summary>
    /// The plug in description.
    /// </summary>
    internal const string PlugInDescription = "This update adds the missing Rena item, which is required for the item registration feature.";

    /// <summary>
    /// The item group of Rena.
    /// </summary>
    private const byte ItemGroup = 14;

    /// <summary>
    /// The item number of Rena.
    /// </summary>
    private const short ItemNumber = 21;

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 07, 26, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Resolves the plain Rena source without confusing it with the level3 Sign of Lord variant.
    /// </summary>
    /// <param name="configuration">The game configuration.</param>
    /// <returns>The seeded or owner-defined plain source, if present.</returns>
    internal static DropItemGroup? FindPlainRenaDropGroup(GameConfiguration configuration)
    {
        var groups = configuration.DropItemGroups.Where(group => group is not ItemDropItemGroup
            && group.ItemLevel is null or 0
            && group.PossibleItems.Any(item => item.Group == ItemGroup && item.Number == ItemNumber));
        var seededId = GuidHelper.CreateGuid<DropItemGroup>(ItemGroup, ItemNumber);
        return groups.FirstOrDefault(group => group.GetId() == seededId) ?? groups.FirstOrDefault();
    }

    /// <summary>
    /// Gets the default Blood Castle and Devil Square5–7 maps independently of translated names.
    /// </summary>
    /// <param name="configuration">The game configuration.</param>
    /// <returns>The maps on which the Rena variants can drop by default.</returns>
    internal static IEnumerable<GameMapDefinition> GetDefaultEventMaps(GameConfiguration configuration)
    {
        return configuration.Maps.Where(map =>
            (map.Discriminator == 0 && (map.Number is >= 11 and <= 17 or 52))
            || (map.Number == 32 && map.Discriminator is >= 5 and <= 7));
    }

    /// <inheritdoc />
#pragma warning disable CS1998
    protected override async ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
#pragma warning restore CS1998
    {
        var itemDefinition = gameConfiguration.Items.FirstOrDefault(item => item.Group == ItemGroup && item.Number == ItemNumber);
        if (itemDefinition is null)
        {
            itemDefinition = context.CreateNew<ItemDefinition>();
            itemDefinition.Name = "Rena";
            itemDefinition.Number = ItemNumber;
            itemDefinition.Group = ItemGroup;
            itemDefinition.DropLevel = 0;
            itemDefinition.DropsFromMonsters = false;
            itemDefinition.Durability = 1;
            itemDefinition.Width = 1;
            itemDefinition.Height = 1;
            itemDefinition.SetGuid(itemDefinition.Group, itemDefinition.Number);
            gameConfiguration.Items.Add(itemDefinition);
        }

        var dropItemGroup = FindPlainRenaDropGroup(gameConfiguration);
        if (dropItemGroup is null)
        {
            // An owner may have converted the seeded source to another Rena variant.
            // Preserve it instead of creating a second group with the same identifier.
            if (gameConfiguration.DropItemGroups.Any(group => group.GetId() == GuidHelper.CreateGuid<DropItemGroup>(ItemGroup, ItemNumber)))
            {
                return;
            }

            dropItemGroup = context.CreateNew<DropItemGroup>();
            dropItemGroup.SetGuid(ItemGroup, ItemNumber);
            dropItemGroup.PossibleItems.Add(itemDefinition);
            dropItemGroup.Chance = 0.01; // 1 Percent
            dropItemGroup.Description = "The drop item group for Rena";
            dropItemGroup.MinimumMonsterLevel = 30;
            dropItemGroup.MaximumMonsterLevel = 255;
            gameConfiguration.DropItemGroups.Add(dropItemGroup);
        }

        // Always (re-)wire, even if the item/group already existed - a previous, incomplete run
        // must not be able to skip this step.
        foreach (var map in GetDefaultEventMaps(gameConfiguration))
        {
            if (!map.DropItemGroups.Contains(dropItemGroup))
            {
                map.DropItemGroups.Add(dropItemGroup);
            }
        }
    }
}
