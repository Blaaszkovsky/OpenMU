// <copyright file="BaskiPrototypeInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

/// <summary>
/// Initializes the provisional Baski identity and its Ferea spawn.
/// </summary>
internal sealed class BaskiPrototypeInitializer : InitializerBase
{
    /// <summary>
    /// A collision-checked local wire identifier; the original Season 16 number is not established.
    /// </summary>
    internal const short Number = 900;

    private const short SpawnNumber = 100;

    private const byte SpawnX = 215;

    private const byte SpawnY = 165;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaskiPrototypeInitializer"/> class.
    /// </summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public BaskiPrototypeInitializer(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        var map = this.GameConfiguration.Maps.FirstOrDefault(candidate => candidate.Number == Ferea.Number && candidate.Discriminator == 0)
            ?? throw new InvalidOperationException("Ferea map is required before Baski can be configured.");
        var baski = this.EnsureDefinition();
        this.EnsureSpawn(map, baski);
    }

    private MonsterDefinition EnsureDefinition()
    {
        if (this.GameConfiguration.Monsters.FirstOrDefault(monster => monster.Number == Number) is { } existing)
        {
            return existing;
        }

        var baski = this.Context.CreateNew<MonsterDefinition>();
        this.GameConfiguration.Monsters.Add(baski);
        baski.Number = Number;
        baski.Designation = MonsterNames.ResourceManager.GetLocalizedString("Baski");
        baski.MoveRange = 6;
        baski.AttackRange = 1;
        baski.ViewRange = 10;
        baski.MoveDelay = TimeSpan.FromMilliseconds(400);
        baski.AttackDelay = TimeSpan.FromMilliseconds(1800);
        baski.RespawnDelay = TimeSpan.FromMinutes(5);
        baski.NumberOfMaximumItemDrops = 0;

        // These are controlled test values, not asserted Season 16 combat data.
        baski.AddAttributes(
            new Dictionary<AttributeDefinition, float>
            {
                { Stats.Level, 100 },
                { Stats.MaximumHealth, 20000 },
                { Stats.MinimumPhysBaseDmg, 30 },
                { Stats.MaximumPhysBaseDmg, 45 },
                { Stats.DefenseBase, 100 },
                { Stats.AttackRatePvm, 500 },
                { Stats.DefenseRatePvm, 100 },
            },
            this.Context,
            this.GameConfiguration);
        baski.SetGuid(baski.Number);
        return baski;
    }

    private void EnsureSpawn(GameMapDefinition map, MonsterDefinition baski)
    {
        if (map.MonsterSpawns.Any(spawn => spawn.MonsterDefinition?.Number == Number))
        {
            return;
        }

        var spawnId = GuidHelper.CreateGuid<MonsterSpawnArea>(map.Number, SpawnNumber);
        if (map.MonsterSpawns.Any(spawn => spawn.GetId() == spawnId))
        {
            throw new InvalidOperationException("Ferea spawn 100 is already assigned to another monster.");
        }

        var spawn = this.Context.CreateNew<MonsterSpawnArea>();
        spawn.SetGuid(spawnId);
        spawn.GameMap = map;
        spawn.MonsterDefinition = baski;
        spawn.Quantity = 1;
        spawn.Direction = Direction.Undefined;
        spawn.SpawnTrigger = SpawnTrigger.Automatic;
        spawn.X1 = SpawnX;
        spawn.X2 = SpawnX;
        spawn.Y1 = SpawnY;
        spawn.Y2 = SpawnY;
        map.MonsterSpawns.Add(spawn);
    }
}
