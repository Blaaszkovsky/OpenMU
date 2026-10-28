// <copyright file="EnterDevilSquareCommand.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.TestActors;

/// <summary>
/// Runs the ordinary Devil Square level 7 entry action for one explicitly approved R12 actor.
/// The protocol intentionally supplies no level, ticket slot, map, or other gameplay parameters.
/// </summary>
public sealed record EnterDevilSquareCommand() : ActorCommand("enterDS7");
