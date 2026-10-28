// <copyright file="IllusionTempleSkillRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MiniGames;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameServer.MessageHandler.MuHelper;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.PlugIns;

/// <summary>Handles Illusion Temple skill requests (BF/02).</summary>
[PlugIn]
[Display(Name = nameof(IllusionTempleResources.EventName), Description = nameof(IllusionTempleResources.FeatureDescription), ResourceType = typeof(IllusionTempleResources))]
[Guid("99FCE46C-5A4B-4996-A1B3-1659EC5E6C1B")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
internal sealed class IllusionTempleSkillRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => IllusionTempleSkillRequest.SubCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < IllusionTempleSkillRequest.Length
            || player.SelectedCharacter?.CharacterClass is null
            || player.CurrentMiniGame is not IllusionTempleContext context)
        {
            return;
        }

        IllusionTempleSkillRequest request = packet;

        // Distance is supplied by the client and isn't authoritative; the event context validates range and target.
        await context.UseSkillAsync(player, request.SkillNumber, request.TargetObjectIndex).ConfigureAwait(false);
    }
}
