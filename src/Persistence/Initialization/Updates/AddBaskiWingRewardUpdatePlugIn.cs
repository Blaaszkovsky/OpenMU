// <copyright file="AddBaskiWingRewardUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the optional local Wings of Unity reward to Baski without replacing operator reward tables.
/// </summary>
[PlugIn]
[Display(Name = nameof(BaskiWingRewardResources.Name), Description = nameof(BaskiWingRewardResources.Description), ResourceType = typeof(BaskiWingRewardResources))]
[Guid("582825BA-7C5B-4D16-8C08-DB72741D1C67")]
public sealed class AddBaskiWingRewardUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => BaskiWingRewardResources.Name;

    /// <inheritdoc />
    public override string Description => BaskiWingRewardResources.Description;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn =>
    [
        new UpdateDependency(typeof(AddBaskiPrototypeUpdatePlugIn).GUID, minVersion: 4),
        new UpdateDependency(typeof(AddExperimentalFearlessWingUpdatePlugIn).GUID, minVersion: 2),
    ];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        new BaskiWingRewardInitializer(context, gameConfiguration).Initialize();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration, int? installedVersion)
    {
        var initializer = new BaskiWingRewardInitializer(context, gameConfiguration);
        if (installedVersion == 1)
        {
            initializer.BalanceLegacyReward();
        }

        initializer.Initialize();
        return ValueTask.CompletedTask;
    }
}
