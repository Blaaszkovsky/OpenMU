// <copyright file="FereaEncounterInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Ferea;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;

/// <summary>
/// Defines encounter-only waves on walkable tiles of the shipped World113 terrain.
/// </summary>
internal sealed class FereaEncounterInitializer : InitializerBase
{
    /// <summary>
    /// The first encounter spawn identity, after hunting spawns 200-217.
    /// </summary>
    private const short FirstSpawnNumber = 300;

    /// <summary>
    /// Initializes a new instance of the <see cref="FereaEncounterInitializer"/> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="gameConfiguration">The configuration.</param>
    public FereaEncounterInitializer(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        this.EnsureVictoryReward();
        var defaults = new FereaEncounterConfiguration();
        var map = this.GameConfiguration.Maps.Single(m => m.Number == defaults.MapNumber && m.Discriminator == 0);
        (short Monster, byte Wave, byte X, byte Y)[] spawns =
        [
            (735, defaults.OrbWave, 30, 120),
            (730, defaults.GuardianWave, 28, 120),
            (731, defaults.GuardianWave, 32, 120),
            (732, defaults.GuardianWave, 30, 118),
            (734, defaults.LordWave, 30, 120),
            (733, defaults.GeneralWave, 38, 120),
            (730, defaults.ReinforcementWave, 40, 116),
            (731, defaults.ReinforcementWave, 40, 120),
            (732, defaults.ReinforcementWave, 40, 124),
        ];

        for (var index = 0; index < spawns.Length; index++)
        {
            var profile = spawns[index];
            var id = GuidHelper.CreateGuid<MonsterSpawnArea>(map.Number, (short)(FirstSpawnNumber + index));
            if (map.MonsterSpawns.Any(s => s.GetId() == id))
            {
                // Existing wave settings belong to the operator. Only fill missing data.
                continue;
            }

            var spawn = this.Context.CreateNew<MonsterSpawnArea>();
            spawn.SetGuid(id);
            spawn.GameMap = map;
            spawn.MonsterDefinition = this.GameConfiguration.Monsters.Single(m => m.Number == profile.Monster);
            spawn.Quantity = 1;
            spawn.SpawnTrigger = SpawnTrigger.OnceAtWaveStart;
            spawn.WaveNumber = profile.Wave;
            spawn.Direction = Direction.Undefined;
            spawn.X1 = spawn.X2 = profile.X;
            spawn.Y1 = spawn.Y2 = profile.Y;
            map.MonsterSpawns.Add(spawn);
        }
    }

    private void EnsureVictoryReward()
    {
        var lord = this.GameConfiguration.Monsters.Single(m => m.Number == 734);
        if (lord.DropItemGroups.Count != 0)
        {
            // Preserve the owner's entire reward table, including an intentionally disabled group.
            return;
        }

        var rewardId = GuidHelper.CreateGuid<DropItemGroup>(734, 1);
        if (this.GameConfiguration.DropItemGroups.FirstOrDefault(g => g.GetId() == rewardId) is { } existing)
        {
            lord.DropItemGroups.Add(existing);
            return;
        }

        var reward = this.Context.CreateNew<DropItemGroup>();
        reward.SetGuid(734, 1);
        reward.Description = DropGroupDescriptions.ResourceManager.GetLocalizedString("FereaVictory");
        reward.Chance = 1;
        foreach (var identifier in new[] { ItemConstants.JewelOfBless, ItemConstants.JewelOfSoul, ItemConstants.JewelOfChaos })
        {
            reward.PossibleItems.Add(this.GameConfiguration.Items.Single(i => i.Group == identifier.Group && i.Number == identifier.Number));
        }

        this.GameConfiguration.DropItemGroups.Add(reward);
        lord.DropItemGroups.Add(reward);
    }
}
