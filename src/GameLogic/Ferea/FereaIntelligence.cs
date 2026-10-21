// <copyright file="FereaIntelligence.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Reuses normal monster combat with arena boundaries and per-instance encounter damage rules.
/// </summary>
internal sealed class FereaIntelligence : BasicMonsterIntelligence, INpcHitFilter
{
    private readonly FereaEncounter _encounter;
    private readonly bool _stationary;

    /// <summary>
    /// Initializes a new instance of the <see cref="FereaIntelligence"/> class.
    /// </summary>
    /// <param name="encounter">The encounter.</param>
    /// <param name="stationary">Whether this is a passive Crystal Orb.</param>
    public FereaIntelligence(FereaEncounter encounter, bool stationary)
    {
        this._encounter = encounter;
        this._stationary = stationary;
    }

    /// <inheritdoc />
    public HitInfo FilterHit(HitInfo hit, IAttacker attacker) => this._encounter.FilterHit(this.Monster, hit, attacker);

    /// <inheritdoc />
    public override bool CanWalkOn(Point target) => this._encounter.Configuration.Contains(target) && base.CanWalkOn(target);

    /// <inheritdoc />
    protected override ValueTask<bool> CanAttackAsync() => ValueTask.FromResult(!this._stationary && !this._encounter.IsProtected(this.Monster));

    /// <inheritdoc />
    protected override async ValueTask<IAttackable?> SearchNextTargetAsync()
    {
        var target = await base.SearchNextTargetAsync().ConfigureAwait(false);
        return target is not null && this._encounter.Configuration.Contains(target.Position) ? target : null;
    }
}
