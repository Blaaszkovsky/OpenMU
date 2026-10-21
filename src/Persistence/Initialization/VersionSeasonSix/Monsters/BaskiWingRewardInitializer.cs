// <copyright file="BaskiWingRewardInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;

using MUnique.OpenMU.Interfaces;

/// <summary>
/// Adds a deterministic, local wing reward for paired-client testing of Baski.
/// </summary>
internal sealed class BaskiWingRewardInitializer : InitializerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BaskiWingRewardInitializer"/> class.
    /// </summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public BaskiWingRewardInitializer(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        var baski = this.GameConfiguration.Monsters.Single(monster => monster.Number == BaskiPrototypeInitializer.Number);
        if (baski.DropItemGroups.Count != 0)
        {
            // An existing reward table, including a disabled group and its drop cap, belongs to the operator.
            return;
        }

        var rewardId = GuidHelper.CreateGuid<DropItemGroup>(BaskiPrototypeInitializer.Number, 1);
        var reward = this.GameConfiguration.DropItemGroups.FirstOrDefault(group => group.GetId() == rewardId);
        if (reward is null)
        {
            reward = this.Context.CreateNew<DropItemGroup>();
            reward.SetGuid(rewardId);
            reward.Description = DropGroupDescriptions.ResourceManager.GetLocalizedString("BaskiWingsOfUnity");
            reward.Chance = 1;
            reward.ItemLevel = 0;
            reward.PossibleItems.Add(this.GameConfiguration.Items.Single(item => item is { Group: 12, Number: 245 }));
            this.GameConfiguration.DropItemGroups.Add(reward);
        }

        // This local prototype reward is deterministic for paired-client testing, not final economy data.
        // Guaranteed monster groups consume the cap before map groups, so no generic drop is produced.
        baski.DropItemGroups.Add(reward);
        baski.NumberOfMaximumItemDrops = 1;
    }
}
