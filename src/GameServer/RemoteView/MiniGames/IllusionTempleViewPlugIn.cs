// <copyright file="IllusionTempleViewPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.MiniGames;

using System.Runtime.InteropServices;
using System.Text;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;

/// <summary>Serializes the Illusion Temple view contract to its legacy BF packets.</summary>
[PlugIn]
[Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.IllusionTempleResources.EventName), Description = nameof(MUnique.OpenMU.GameLogic.Properties.IllusionTempleResources.FeatureDescription), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.IllusionTempleResources))]
[Guid("6C1220DF-21FA-4FC6-AB6A-DC5A3AB93C70")]
public sealed class IllusionTempleViewPlugIn : IIllusionTempleViewPlugIn
{
    private const int PlayerNameLength = 10;
    private readonly RemotePlayer _player;

    /// <summary>Initializes a new instance of the <see cref="IllusionTempleViewPlugIn"/> class.</summary>
    /// <param name="player">The recipient.</param>
    public IllusionTempleViewPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public async ValueTask ShowPhaseAsync(byte level, byte phase)
    {
        await this._player.Connection.SendIllusionTempleSkillEndAsync(level, phase).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowStateAsync(TimeSpan remaining, Player? holder, Point? relicPosition, byte alliedScore, byte illusionScore, byte team, IReadOnlyCollection<Player> members)
    {
        if (this._player.Connection is not { Connected: true } connection)
        {
            return;
        }

        var maximumPartyMembers = (byte.MaxValue - 14) / IllusionTempleStateRef.IllusionTemplePartyEntryRef.Length;
        var party = members.Take(maximumPartyMembers).ToArray();
        int WritePacket()
        {
            var length = IllusionTempleStateRef.GetRequiredSize(party.Length);
            var packet = new IllusionTempleStateRef(connection.Output.GetSpan(length)[..length]);
            packet.RemainingSeconds = (ushort)Math.Clamp(remaining.TotalSeconds, 0, ushort.MaxValue);
            packet.PlayerIndex = holder?.GetId(this._player) ?? ushort.MaxValue;
            packet.PositionX = relicPosition is { } position ? ToByte(position.X) : (byte)0;
            packet.PositionY = relicPosition is { } positionY ? ToByte(positionY.Y) : (byte)0;
            packet.Team1Points = alliedScore;
            packet.Team2Points = illusionScore;
            packet.MyTeam = team;
            packet.PartyCount = (byte)party.Length;

            for (var index = 0; index < party.Length; index++)
            {
                var member = packet[index];
                var player = party[index];
                member.PlayerId = player.GetId(this._player);
                member.MapNumber = (byte)Math.Clamp(player.CurrentMap?.MapId ?? 0, byte.MinValue, byte.MaxValue);
                member.PositionX = ToByte(player.Position.X);
                member.PositionY = ToByte(player.Position.Y);
            }

            return length;
        }

        await connection.SendAsync(WritePacket).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowSkillPointsAsync(byte points)
    {
        await this._player.Connection.SendIllusionTempleSkillPointUpdateAsync(points).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowSkillResultAsync(ushort skill, Player source, Player target, bool success)
    {
        await this._player.Connection.SendIllusionTempleSkillUsageResultAsync(
            success ? (byte)1 : (byte)0,
            skill,
            source.GetId(this._player),
            target.GetId(this._player)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowSkillEndAsync(ushort skill, Player target)
    {
        await this._player.Connection.SendIllusionTempleSkillEndedAsync(skill, target.GetId(this._player)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowResultAsync(byte alliedScore, byte illusionScore, IReadOnlyList<(Player Player, byte Team, int Experience)> results)
    {
        if (this._player.Connection is not { Connected: true } connection)
        {
            return;
        }

        var maximumResultPlayers = (byte.MaxValue - 7) / IllusionTempleResultRef.PlayerResultRef.Length;
        var players = results.Take(maximumResultPlayers).ToArray();
        int WritePacket()
        {
            var length = IllusionTempleResultRef.GetRequiredSize(players.Length);
            var packet = new IllusionTempleResultRef(connection.Output.GetSpan(length)[..length]);
            packet.Team1Points = alliedScore;
            packet.Team2Points = illusionScore;
            packet.PlayerCount = (byte)players.Length;

            for (var index = 0; index < players.Length; index++)
            {
                var (player, team, experience) = players[index];
                var result = packet[index];
                result.Name = TruncateName(player.Name);
                result.MapNumber = (byte)Math.Clamp(player.CurrentMap?.MapId ?? 0, byte.MinValue, byte.MaxValue);
                result.Team = team;
                result.Class = (byte)Math.Clamp(player.SelectedCharacter?.CharacterClass?.Number ?? 0, byte.MinValue, byte.MaxValue);
                result.AddedExperience = unchecked((uint)experience);
            }

            return length;
        }

        await connection.SendAsync(WritePacket).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ShowPlayerCountsAsync(IReadOnlyList<byte> counts)
    {
        await this._player.Connection.SendIllusionTempleUserCountAsync(
            CountAt(counts, 0), CountAt(counts, 1), CountAt(counts, 2), CountAt(counts, 3), CountAt(counts, 4), CountAt(counts, 5))
            .ConfigureAwait(false);
    }

    private static byte ToByte(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);

    private static byte CountAt(IReadOnlyList<byte> counts, int index) => index < counts.Count ? counts[index] : (byte)0;

    private static string TruncateName(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            Span<byte> destination = stackalloc byte[PlayerNameLength];
            var encoder = Encoding.UTF8.GetEncoder();
            encoder.Convert(name.AsSpan(), destination, true, out _, out var bytesUsed, out _);
            return Encoding.UTF8.GetString(destination[..bytesUsed]);
        }

        return string.Empty;
    }
}
