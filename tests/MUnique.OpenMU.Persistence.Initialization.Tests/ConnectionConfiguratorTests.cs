// <copyright file="ConnectionConfiguratorTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;

/// <summary>
/// Tests parsing of PostgreSQL role credentials from connection strings.
/// </summary>
[TestFixture]
internal class ConnectionConfiguratorTests
{
    /// <summary>
    /// Verifies both Npgsql username aliases are handled by the connection string parser.
    /// </summary>
    /// <param name="userNameKeyword">The username keyword to parse.</param>
    [TestCase("Username")]
    [TestCase("User Id")]
    public void GetRoleNameFromConnectionStringSupportsNpgsqlAliases(string userNameKeyword)
    {
        var connectionString = $"Host=127.0.0.1;Port=5432;Database=openmu;{userNameKeyword}=event_role;Password=secret;";

        Assert.That(ConnectionConfigurator.GetRoleNameFromConnectionString(connectionString), Is.EqualTo("event_role"));
    }

    /// <summary>
    /// Verifies a password with a semicolon and quote is parsed as one value.
    /// </summary>
    [Test]
    public void GetRolePasswordFromConnectionStringPreservesEscapedDelimiters()
    {
        const string expectedPassword = "semi;colon'quote";
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 5432,
            Database = "openmu",
            Username = "event_role",
            Password = expectedPassword,
        }.ConnectionString;

        Assert.That(ConnectionConfigurator.GetRolePasswordFromConnectionString(connectionString), Is.EqualTo(expectedPassword));
    }
}
