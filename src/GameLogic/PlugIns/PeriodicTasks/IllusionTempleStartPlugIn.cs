// <copyright file="IllusionTempleStartPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>Starts the shared Illusion Temple registration windows.</summary>
[PlugIn]
[Guid("435939D7-F688-4E72-9335-CB238442F3B4")]
[Display(Name = nameof(IllusionTempleResources.StartName), Description = nameof(IllusionTempleResources.StartDescription), ResourceType = typeof(IllusionTempleResources))]
public sealed class IllusionTempleStartPlugIn : MiniGameStartBasePlugIn<IllusionTempleStartConfiguration, IllusionTempleGameServerState>
{
    /// <inheritdoc />
    public override MiniGameType Key => MiniGameType.IllusionTemple;

    /// <inheritdoc />
    public override object CreateDefaultConfig() => IllusionTempleStartConfiguration.Default;

    /// <inheritdoc />
    protected override IllusionTempleGameServerState CreateState(IGameContext gameContext) => new(gameContext);
}
