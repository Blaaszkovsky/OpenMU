// <copyright file="OfflineAccountAccess.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.Interfaces;

/// <summary>Coordinates exclusive edits with normal logins and the game servers which own offline sessions.</summary>
internal static class OfflineAccountAccess
{
    /// <summary>Runs an edit only after reserving the login and excluding all active offline sessions.</summary>
    /// <param name="login">The login coordinator.</param>
    /// <param name="servers">The game server registry.</param>
    /// <param name="loginName">The account login name.</param>
    /// <param name="operation">The edit or preview, which reloads its data after reservation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    internal static async ValueTask<JsonObject> ExecuteAsync(ILoginServer login, IServerProvider servers, string loginName, Func<CancellationToken, ValueTask<JsonObject>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        cancellationToken = timeout.Token;
        var reservationId = RandomNumberGenerator.GetInt32(byte.MaxValue + 1, int.MaxValue);
        JsonObject? result = null;
        Exception? operationFailure = null;
        try
        {
            if (!await login.TryReserveAccountAsync(loginName, reservationId).ConfigureAwait(false))
            {
                throw new AdminOperationException("The account is online, reserved, or its login state is unavailable. Disconnect it first; in distributed deployments, log in and out once to initialize a new account's login state.");
            }

            // A transition claims its offline-manager entry before releasing the normal login registration.
            await RequireNoSessionAsync(servers, loginName, cancellationToken).ConfigureAwait(false);
            result = await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            // Also release after a lost reserve response; ownership checking makes this safe for online accounts.
            await login.ReleaseAccountReservationAsync(loginName, reservationId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AdminOperationException(
                "The offline edit or preview finished, but releasing its login reservation could not be confirmed. Check the current state and login coordination before retrying.",
                AdminOperationErrorKind.Uncertain,
                operationFailure is null ? exception : new AggregateException(operationFailure, exception));
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return result ?? throw new InvalidOperationException("The offline operation produced no result.");
    }

    private static async ValueTask RequireNoSessionAsync(IServerProvider servers, string loginName, CancellationToken cancellationToken)
    {
        foreach (var server in servers.Servers.OfType<IGameServer>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.ServerState == ServerState.Stopped)
            {
                continue;
            }

            if (server.ServerState != ServerState.Started || server is not IGameServerAdministration administration)
            {
                throw new AdminOperationException("A game server's offline session state is unavailable. Check server_overview before editing the account.", AdminOperationErrorKind.Unavailable);
            }

            bool active;
            try
            {
                active = await administration.HasOfflineSessionAsync(loginName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new AdminOperationException("A game server's offline session state could not be confirmed. No account edit was executed.", AdminOperationErrorKind.Unavailable, exception);
            }

            if (active)
            {
                throw new AdminOperationException("The account has an active offline-leveling session. Use players_disconnect before editing it.");
            }
        }
    }
}
