// <copyright file="IllusionTempleBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Creates the Season 6 NPCs shared by the six Illusion Temple maps.
/// </summary>
internal abstract class IllusionTempleBase : BaseMapInitializer
{
    /// <summary>
    /// The original client models support two neutral statues and a general/storage pair per team.
    /// General/storage coordinates match the active client mini-map. The neutral statue coordinates
    /// are balanced defaults of this fork, validated against the terrain after its Play water opening.
    /// </summary>
    internal static readonly (short MonsterNumber, byte X, byte Y)[] EventNpcSpawns =
    [
        (380, 128, 126),
        (380, 210, 44),
        (381, 138, 44),
        (382, 193, 126),
        (383, 138, 58),
        (384, 192, 113),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="IllusionTempleBase"/> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    protected IllusionTempleBase(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    protected override IEnumerable<MonsterSpawnArea> CreateMonsterSpawns()
    {
        short number = 200;
        foreach (var spawn in EventNpcSpawns)
        {
            yield return this.CreateMonsterSpawn(number++, this.NpcDictionary[spawn.MonsterNumber], spawn.X, spawn.Y, Direction.Undefined, SpawnTrigger.AutomaticDuringEvent);
        }
    }
}
