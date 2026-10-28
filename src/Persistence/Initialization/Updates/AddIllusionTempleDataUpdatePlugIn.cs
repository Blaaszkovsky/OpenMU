// <copyright file="AddIllusionTempleDataUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Events;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the Season 6 Illusion Temple definitions and replaces only the obsolete seeded Renewal spawns.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddIllusionTempleDataUpdatePlugIn_Name), Description = nameof(PlugInResources.AddIllusionTempleDataUpdatePlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("FA550F85-CCF9-4B3B-AD54-3B3917CB9ED7")]
public class AddIllusionTempleDataUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string Name => PlugInResources.AddIllusionTempleDataUpdatePlugIn_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddIllusionTempleDataUpdatePlugIn_Description;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override async ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var initializer = new IllusionTempleInitializer(context, gameConfiguration);
        await initializer.RemoveLegacySpawnsAsync().ConfigureAwait(false);
        initializer.Initialize();
    }
}
