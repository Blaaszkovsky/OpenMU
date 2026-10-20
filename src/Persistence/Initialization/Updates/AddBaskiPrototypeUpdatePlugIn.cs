// <copyright file="AddBaskiPrototypeUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the isolated Baski prototype to existing Ferea configurations.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.AddBaskiPrototypeUpdatePlugIn_Name), Description = nameof(PlugInResources.AddBaskiPrototypeUpdatePlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("FE8B1F53-3ED6-4310-A451-198146E69603")]
public class AddBaskiPrototypeUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => PlugInResources.AddBaskiPrototypeUpdatePlugIn_Name;

    /// <inheritdoc />
    public override string Description => PlugInResources.AddBaskiPrototypeUpdatePlugIn_Description;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [typeof(AddFereaDataUpdatePlugIn).GUID];

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        new BaskiPrototypeInitializer(context, gameConfiguration).Initialize();
        return ValueTask.CompletedTask;
    }
}
