// <copyright file="FereaMonstersInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

/// <summary>
/// Defines Ferea's monster identities and the ordinary hunting spawns.
/// </summary>
internal sealed class FereaMonstersInitializer : InitializerBase
{
    /// <summary>The configured default General health for the Season 6 solo encounter balance.</summary>
    internal const int GeneralMaximumHealth = 100000;

    /// <summary>The configured default Lord health for the Season 6 solo encounter balance.</summary>
    internal const int LordMaximumHealth = 300000;

    private const short FirstSpawnNumber = 200;

    private static readonly (byte X, byte Y)[] HuntingLocations =
    [
        (234, 198),
        (235, 215),
        (222, 217),
        (205, 114),
        (165, 154),
        (166, 137),
    ];

    private static readonly MonsterProfile[] Profiles =
    [
        new(730, "FereaKnight", 121, 30000, 75, 100, 200, 1),
        new(731, "FereaArcher", 122, 26000, 65, 90, 180, 4),
        new(732, "FereaFighter", 123, 36000, 85, 115, 220, 1),
        new(733, "FereaGeneral", 150, GeneralMaximumHealth, 140, 190, 350, 1),
        new(734, "LordOfFerea", 150, LordMaximumHealth, 180, 250, 500, 2),
        new(735, "FereaCrystalOrb", 150, 150000, 0, 0, 250, 1),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="FereaMonstersInitializer"/> class.
    /// </summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public FereaMonstersInitializer(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        var map = this.GameConfiguration.Maps.FirstOrDefault(candidate => candidate.Number == Ferea.Number && candidate.Discriminator == 0)
            ?? throw new InvalidOperationException("Ferea map is required before its monsters can be configured.");

        for (var profileIndex = 0; profileIndex < Profiles.Length; profileIndex++)
        {
            var profile = Profiles[profileIndex];
            var definition = this.EnsureDefinition(profile);
            if (profileIndex < 3)
            {
                this.EnsureHuntingSpawns(map, definition, profileIndex);
            }
        }
    }

    private MonsterDefinition EnsureDefinition(MonsterProfile profile)
    {
        if (this.GameConfiguration.Monsters.FirstOrDefault(monster => monster.Number == profile.Number) is { } existing)
        {
            return existing;
        }

        var monster = this.Context.CreateNew<MonsterDefinition>();
        this.GameConfiguration.Monsters.Add(monster);
        monster.Number = profile.Number;
        monster.Designation = MonsterNames.ResourceManager.GetLocalizedString(profile.NameKey);
        monster.MoveRange = 6;
        monster.AttackRange = profile.AttackRange;
        monster.ViewRange = 10;
        monster.MoveDelay = TimeSpan.FromMilliseconds(400);
        monster.AttackDelay = TimeSpan.FromMilliseconds(1800);
        monster.RespawnDelay = TimeSpan.FromSeconds(15);
        monster.NumberOfMaximumItemDrops = 0;

        // The reference client's level range needs a separate balance pass for our three-class Season 6 rules.
        monster.AddAttributes(
            new Dictionary<AttributeDefinition, float>
            {
                { Stats.Level, profile.Level },
                { Stats.MaximumHealth, profile.Health },
                { Stats.MinimumPhysBaseDmg, profile.MinimumDamage },
                { Stats.MaximumPhysBaseDmg, profile.MaximumDamage },
                { Stats.DefenseBase, profile.Defense },
                { Stats.AttackRatePvm, 600 },
                { Stats.DefenseRatePvm, 150 },
            },
            this.Context,
            this.GameConfiguration);
        monster.SetGuid(monster.Number);
        return monster;
    }

    private void EnsureHuntingSpawns(GameMapDefinition map, MonsterDefinition definition, int profileIndex)
    {
        for (var locationIndex = 0; locationIndex < HuntingLocations.Length; locationIndex++)
        {
            var spawnNumber = (short)(FirstSpawnNumber + (profileIndex * HuntingLocations.Length) + locationIndex);
            var spawnId = GuidHelper.CreateGuid<MonsterSpawnArea>(map.Number, spawnNumber);
            var location = HuntingLocations[locationIndex];
            var x = (byte)(location.X + (profileIndex == 1 ? 3 : 0));
            var y = (byte)(location.Y + (profileIndex * 2));
            if (map.MonsterSpawns.FirstOrDefault(spawn => spawn.GetId() == spawnId) is { } existing)
            {
                if (existing.MonsterDefinition?.Number != definition.Number)
                {
                    throw new InvalidOperationException($"Ferea spawn {spawnNumber} is already assigned to another monster.");
                }

                existing.X1 = x;
                existing.X2 = x;
                existing.Y1 = y;
                existing.Y2 = y;
                continue;
            }

            var spawn = this.Context.CreateNew<MonsterSpawnArea>();
            spawn.SetGuid(spawnId);
            spawn.GameMap = map;
            spawn.MonsterDefinition = definition;
            spawn.Quantity = 1;
            spawn.Direction = Direction.Undefined;
            spawn.SpawnTrigger = SpawnTrigger.Automatic;
            spawn.X1 = x;
            spawn.X2 = x;
            spawn.Y1 = y;
            spawn.Y2 = y;
            map.MonsterSpawns.Add(spawn);
        }
    }

    private sealed record MonsterProfile(short Number, string NameKey, int Level, int Health, int MinimumDamage, int MaximumDamage, int Defense, byte AttackRange);
}
