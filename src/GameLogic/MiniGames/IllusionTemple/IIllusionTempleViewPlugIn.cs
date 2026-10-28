// <copyright file="IIllusionTempleViewPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Displays the Illusion Temple match without coupling game logic to its wire format.
/// </summary>
[Guid("C6E02B62-940F-40A1-B732-8977CC1541C1")]
public interface IIllusionTempleViewPlugIn : IViewPlugIn
{
    /// <summary>Shows the level and phase (0 waiting, 1 ready, 2 playing, 3 ended).</summary>
    /// <param name="level">The event level.</param>
    /// <param name="phase">The phase.</param>
    ValueTask ShowPhaseAsync(byte level, byte phase);

    /// <summary>Shows the authoritative clock, relic, scores, and own team positions.</summary>
    /// <param name="remaining">The remaining playing time.</param>
    /// <param name="holder">The relic holder, if any.</param>
    /// <param name="relicPosition">The relic position, if available.</param>
    /// <param name="alliedScore">The allied score.</param>
    /// <param name="illusionScore">The illusion score.</param>
    /// <param name="team">The recipient's team.</param>
    /// <param name="members">The recipient's teammates.</param>
    ValueTask ShowStateAsync(TimeSpan remaining, Player? holder, Point? relicPosition, byte alliedScore, byte illusionScore, byte team, IReadOnlyCollection<Player> members);

    /// <summary>Shows the available skill points.</summary>
    /// <param name="points">The skill points.</param>
    ValueTask ShowSkillPointsAsync(byte points);

    /// <summary>Shows the outcome of an event skill request.</summary>
    /// <param name="skill">The event skill.</param>
    /// <param name="source">The caster.</param>
    /// <param name="target">The target.</param>
    /// <param name="success">Whether the skill was applied.</param>
    ValueTask ShowSkillResultAsync(ushort skill, Player source, Player target, bool success);

    /// <summary>Ends a timed event skill effect.</summary>
    /// <param name="skill">The event skill.</param>
    /// <param name="target">The target.</param>
    ValueTask ShowSkillEndAsync(ushort skill, Player target);

    /// <summary>Shows the match results.</summary>
    /// <param name="alliedScore">The allied team's score.</param>
    /// <param name="illusionScore">The illusion team's score.</param>
    /// <param name="results">The players, teams, and awarded experience.</param>
    ValueTask ShowResultAsync(byte alliedScore, byte illusionScore, IReadOnlyList<(Player Player, byte Team, int Experience)> results);

    /// <summary>Shows the player counts of the six event levels.</summary>
    /// <param name="counts">The counts, ordered by event level.</param>
    ValueTask ShowPlayerCountsAsync(IReadOnlyList<byte> counts);
}
