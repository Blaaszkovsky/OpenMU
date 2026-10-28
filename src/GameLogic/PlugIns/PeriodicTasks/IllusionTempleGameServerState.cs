// <copyright file="IllusionTempleGameServerState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

/// <summary>Tracks the scheduled Illusion Temple run.</summary>
public sealed class IllusionTempleGameServerState : PeriodicTaskGameServerState
{
    /// <summary>Initializes a new instance of the <see cref="IllusionTempleGameServerState"/> class.</summary>
    /// <param name="context">The game context.</param>
    public IllusionTempleGameServerState(IGameContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Description => IllusionTempleResources.EventName;
}
