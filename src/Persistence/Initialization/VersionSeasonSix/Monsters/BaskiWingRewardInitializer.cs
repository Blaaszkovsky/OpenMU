// <copyright file="BaskiWingRewardInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;

using MUnique.OpenMU.Interfaces;

/// <summary>
/// Adds the local Baski wing reward without replacing operator reward tables.
/// </summary>
internal sealed class BaskiWingRewardInitializer : InitializerBase
{
    /// <summary>The default chance for one Wings of Unity on a Baski kill.</summary>
    internal const double RewardChance = 0.05;

    private static readonly TimeSpan LegacyRespawnDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RewardRespawnDelay = TimeSpan.FromMinutes(5);

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
            reward.Chance = RewardChance;
            reward.ItemLevel = 0;
            reward.PossibleItems.Add(this.GameConfiguration.Items.Single(item => item is { Group: 12, Number: 245 }));
            this.GameConfiguration.DropItemGroups.Add(reward);
        }

        // A detached v1 prototype group is upgraded when it is reattached.
        if (IsLegacyReward(reward))
        {
            reward.Chance = RewardChance;
        }

        baski.DropItemGroups.Add(reward);
        baski.NumberOfMaximumItemDrops = 1;
        if (baski.RespawnDelay == LegacyRespawnDelay)
        {
            baski.RespawnDelay = RewardRespawnDelay;
        }
    }

    /// <summary>Upgrades only the unmodified v1 prototype reward.</summary>
    internal void BalanceLegacyReward()
    {
        var baski = this.GameConfiguration.Monsters.Single(monster => monster.Number == BaskiPrototypeInitializer.Number);
        var rewardId = GuidHelper.CreateGuid<DropItemGroup>(BaskiPrototypeInitializer.Number, 1);
        var reward = baski.DropItemGroups.SingleOrDefault(group => group.GetId() == rewardId);
        if (reward is null || !IsLegacyReward(reward) || baski.NumberOfMaximumItemDrops != 1)
        {
            return;
        }

        reward.Chance = RewardChance;
        if (baski.RespawnDelay == LegacyRespawnDelay)
        {
            baski.RespawnDelay = RewardRespawnDelay;
        }
    }

    private static bool IsLegacyReward(DropItemGroup reward)
        => reward.Chance == 1
            && reward.ItemLevel == 0
            && reward.ItemType == SpecialItemType.None
            && reward.PossibleItems.Count == 1
            && reward.PossibleItems.Single() is { Group: 12, Number: 245 };
}
