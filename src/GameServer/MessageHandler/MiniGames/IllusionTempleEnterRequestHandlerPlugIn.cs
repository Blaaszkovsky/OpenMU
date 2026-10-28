// <copyright file="IllusionTempleEnterRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MiniGames;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameServer.MessageHandler.MuHelper;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.PlugIns;

/// <summary>Handles the Mirage Illusion Temple entry request (BF/00).</summary>
[PlugIn]
[Display(Name = nameof(IllusionTempleResources.EventName), Description = nameof(IllusionTempleResources.FeatureDescription), ResourceType = typeof(IllusionTempleResources))]
[Guid("AFCAB9F2-7333-44AA-91CB-32F5CF13C7AB")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
internal sealed class IllusionTempleEnterRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    private readonly EnterIllusionTempleAction _enterAction = new();

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => IllusionTempleEnterRequest.SubCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < IllusionTempleEnterRequest.Length || player.SelectedCharacter?.CharacterClass is null)
        {
            return;
        }

        IllusionTempleEnterRequest request = packet;
        await this._enterAction.TryEnterAsync(player, request.MapNumber, request.ItemSlot).ConfigureAwait(false);
    }
}
