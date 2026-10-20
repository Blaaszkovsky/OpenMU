// <copyright file="AddFereaDataUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds Ferea to existing Season 6 configurations without replacing customized entries.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddFereaDataUpdatePlugIn_Name), Description = nameof(PlugInResources.AddFereaDataUpdatePlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("EB3B9249-0E7D-481C-91D6-991E96A3F02C")]
public class AddFereaDataUpdatePlugIn : UpdatePlugInBase
{
    private const ushort WarpIndex = 50;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddFereaDataUpdatePlugIn_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddFereaDataUpdatePlugIn_Description;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override async ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var serverConfigurations = (await context.GetAsync<GameServerDefinition>().ConfigureAwait(false))
            .Where(server => ReferenceEquals(server.GameConfiguration, gameConfiguration))
            .Select(server => server.ServerConfiguration)
            .OfType<GameServerConfiguration>()
            .Distinct()
            .ToList();
        if (serverConfigurations.Count == 0)
        {
            throw new InvalidOperationException("Ferea requires a game server configuration to host the map.");
        }

        var map = gameConfiguration.Maps.FirstOrDefault(candidate => candidate.Number == Ferea.Number && candidate.Discriminator == 0);
        if (map is null)
        {
            new Ferea(context, gameConfiguration).Initialize();
            map = gameConfiguration.Maps.Single(candidate => candidate.Number == Ferea.Number && candidate.Discriminator == 0);
        }
        else if (map.TerrainData is null)
        {
            map.UpdateTerrainFromResources();
        }

        var spawnGate = map.ExitGates.FirstOrDefault(gate => gate.IsSpawnGate);
        if (spawnGate is null)
        {
            spawnGate = context.CreateNew<ExitGate>();
            spawnGate.Map = map;
            spawnGate.X1 = 236;
            spawnGate.X2 = 237;
            spawnGate.Y1 = 152;
            spawnGate.Y2 = 153;
            spawnGate.Direction = Direction.Undefined;
            spawnGate.IsSpawnGate = true;
            map.ExitGates.Add(spawnGate);
        }

        map.SafezoneMap ??= map;

        if (gameConfiguration.WarpList.All(warp => warp.Index != WarpIndex))
        {
            var warp = context.CreateNew<WarpInfo>();
            warp.Index = WarpIndex;
            warp.Name = WarpNames.ResourceManager.GetLocalizedString(nameof(Ferea));
            warp.Costs = 50000;
            warp.LevelRequirement = 400;
            warp.Gate = spawnGate;
            gameConfiguration.WarpList.Add(warp);
        }

        if (!serverConfigurations.Any(server => server.Maps.Contains(map)))
        {
            var host = serverConfigurations.FirstOrDefault(server => server.Maps.Any(candidate => candidate.Number == 0))
                ?? serverConfigurations[0];
            host.Maps.Add(map);
        }
    }
}
