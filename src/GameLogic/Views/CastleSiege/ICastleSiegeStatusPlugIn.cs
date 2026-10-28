// <copyright file="ICastleSiegeStatusPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.CastleSiege;

/// <summary>
/// A view which shows the current Castle Siege status, e.g. the state, schedule and castle owner.
/// </summary>
public interface ICastleSiegeStatusPlugIn : IViewPlugIn
{
    /// <summary>
    /// Shows the Castle Siege status.
    /// </summary>
    /// <param name="status">The status, or <see langword="null"/> when no status is available.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    ValueTask ShowStatusAsync(CastleSiegeStatus? status);

    /// <summary>
    /// Updates the battle mode, including the client's siege terrain and available targets.
    /// </summary>
    /// <param name="isStarted">Whether the Castle Siege battle is running.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    ValueTask ShowBattleStateAsync(bool isStarted);

    /// <summary>
    /// Shows the remaining battle time on the siege warfare display.
    /// </summary>
    /// <param name="remainingTime">The remaining battle time, or zero after the battle.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    ValueTask ShowRemainingTimeAsync(TimeSpan remainingTime);
}
