// <copyright file="IllusionTempleRewardRequestHandlerPlugIn.cs" company="MUnique">
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

/// <summary>Handles Illusion Temple reward requests (BF/05).</summary>
[PlugIn]
[Display(Name = nameof(IllusionTempleResources.EventName), Description = nameof(IllusionTempleResources.FeatureDescription), ResourceType = typeof(IllusionTempleResources))]
[Guid("05F53444-02F7-4ED1-A845-EA3F4737D4E2")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
internal sealed class IllusionTempleRewardRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => IllusionTempleRewardRequest.SubCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < IllusionTempleRewardRequest.Length
            || player.SelectedCharacter?.CharacterClass is null
            || player.CurrentMiniGame is not IllusionTempleContext context)
        {
            return;
        }

        await context.ClaimRewardAsync(player).ConfigureAwait(false);
    }
}
