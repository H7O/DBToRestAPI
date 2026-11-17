using DBToRestAPI.Services;
using Microsoft.Data.SqlClient;

namespace DBToRestAPI.Tests;

/// <summary>
/// SQL Server sign-in with Entra ID (<c>Authentication=Active Directory ...</c> in a connection
/// string). The driver doesn't contain it: it looks for the package
/// Microsoft.Data.SqlClient.Extensions.Azure by name, and without that package no such connection
/// can sign in. Nothing fails at build time when the package is missing, and an ordinary connection
/// keeps working, so these tests are what notices.
/// </summary>
public class SqlServerEntraIdTests
{
    /// <summary>Every Entra ID sign-in the driver offers, by its name in the driver.</summary>
    public static IEnumerable<object[]> EntraIdSignIns() =>
        Enum.GetNames<SqlAuthenticationMethod>()
            .Where(name => name.StartsWith("ActiveDirectory", StringComparison.Ordinal))
            .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(EntraIdSignIns))]
    public void TheDriverHasAProviderForEverySignIn(string name)
    {
        var method = Enum.Parse<SqlAuthenticationMethod>(name);
        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.True(provider != null,
            $"The SQL Server driver has no provider for {method}: the engine's project must reference "
            + "Microsoft.Data.SqlClient.Extensions.Azure, in the driver's own version.");
        Assert.True(provider.IsSupported(method));
    }

    /// <summary>
    /// The values that topic 13 names, as the engine hands them to the driver: the engine rewrites
    /// a SQL Server connection string before it opens the connection.
    /// </summary>
    [Theory]
    [InlineData("Active Directory Managed Identity", "ActiveDirectoryManagedIdentity")]
    [InlineData("Active Directory Service Principal", "ActiveDirectoryServicePrincipal")]
    [InlineData("Active Directory Workload Identity", "ActiveDirectoryWorkloadIdentity")]
    [InlineData("Active Directory Default", "ActiveDirectoryDefault")]
    [InlineData("Active Directory Integrated", "ActiveDirectoryIntegrated")]
    [InlineData("Active Directory Interactive", "ActiveDirectoryInteractive")]
    [InlineData("Active Directory Device Code Flow", "ActiveDirectoryDeviceCodeFlow")]
    [InlineData("Active Directory Password", "ActiveDirectoryPassword")]
    public void TheValuesTheDocsName_ReachTheDriver(string value, string name)
    {
        var method = Enum.Parse<SqlAuthenticationMethod>(name);
        const string clientId = "11111111-2222-3333-4444-555555555555";

        var handed = DbConnectionFactory.NormalizeConnectionStringPaths("Microsoft.Data.SqlClient",
            $"Server=myserver.database.windows.net;Database=app;Authentication={value};User Id={clientId};Password=secret;");

        var read = new SqlConnectionStringBuilder(handed);
        Assert.Equal(method, read.Authentication);
        Assert.Equal(clientId, read.UserID);
        Assert.Equal("secret", read.Password);
    }

    /// <summary>
    /// The package brings the native library of the Windows account broker, which the sign-ins that
    /// go through that broker call (Interactive, Integrated, Password, Device Code Flow). It ships
    /// with the engine, so that every sign-in the driver offers has what it needs: it costs disk
    /// only, because nothing else loads it. A reference with <c>ExcludeAssets="native"</c> would
    /// leave it out without any build error.
    /// </summary>
    [Fact]
    public void TheAccountBrokersNativeLibraryShips()
    {
        var found = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*msalruntime*", SearchOption.AllDirectories)
            .ToList();

        Assert.True(found.Count > 0,
            "The account broker's native library (msalruntime) isn't in the build: the reference to "
            + "Microsoft.Data.SqlClient.Extensions.Azure must keep its native assets.");
    }
}
