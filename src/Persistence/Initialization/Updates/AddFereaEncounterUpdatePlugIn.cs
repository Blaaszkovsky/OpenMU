// <copyright file="AddFereaEncounterUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Ferea;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the optional Ferea encounter without overwriting operator settings.
/// </summary>
[PlugIn]
[Display(Name = nameof(FereaResources.UpdateName), Description = nameof(FereaResources.UpdateDescription), ResourceType = typeof(FereaResources))]
[Guid("A8C7138D-9BC1-4936-B8A0-2538A67F6915")]
public sealed class AddFereaEncounterUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => FereaResources.UpdateName;

    /// <inheritdoc />
    public override string Description => FereaResources.UpdateDescription;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [new UpdateDependency(typeof(AddBaskiPrototypeUpdatePlugIn).GUID, minVersion: 4)];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        new FereaEncounterInitializer(context, gameConfiguration).Initialize();
        if (gameConfiguration.PlugInConfigurations.All(p => p.TypeId != typeof(FereaEncounterPlugIn).GUID))
        {
            var configuration = context.CreateNew<PlugInConfiguration>();
            configuration.SetGuid(typeof(FereaEncounterPlugIn).GUID);
            configuration.TypeId = typeof(FereaEncounterPlugIn).GUID;
            configuration.IsActive = true;
            configuration.SetConfiguration(new FereaEncounterConfiguration(), null);
            gameConfiguration.PlugInConfigurations.Add(configuration);
        }

        return ValueTask.CompletedTask;
    }
}
