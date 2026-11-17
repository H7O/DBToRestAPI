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
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryMSI)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    public void TheDriverHasAProviderForEachSignInAServerUses(SqlAuthenticationMethod method)
    {
        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.True(provider != null,
            $"The SQL Server driver has no provider for {method}: the engine's project must reference "
            + "Microsoft.Data.SqlClient.Extensions.Azure, in the driver's own version.");
        Assert.True(provider.IsSupported(method));
    }

    /// <summary>
    /// The four values that topic 13 names, as the engine hands them to the driver: the engine
    /// rewrites a SQL Server connection string before it opens the connection.
    /// </summary>
    [Theory]
    [InlineData("Active Directory Managed Identity", SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData("Active Directory Service Principal", SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    [InlineData("Active Directory Workload Identity", SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity)]
    [InlineData("Active Directory Default", SqlAuthenticationMethod.ActiveDirectoryDefault)]
    public void TheValuesTheDocsName_ReachTheDriver(string value, SqlAuthenticationMethod method)
    {
        const string clientId = "11111111-2222-3333-4444-555555555555";

        var handed = DbConnectionFactory.NormalizeConnectionStringPaths("Microsoft.Data.SqlClient",
            $"Server=myserver.database.windows.net;Database=app;Authentication={value};User Id={clientId};Password=secret;");

        var read = new SqlConnectionStringBuilder(handed);
        Assert.Equal(method, read.Authentication);
        Assert.Equal(clientId, read.UserID);
        Assert.Equal("secret", read.Password);
    }

    /// <summary>
    /// The package brings the native library of the Windows account broker (36 MB for Linux x64).
    /// Only the sign-ins that go through that broker call it (Interactive, Integrated, Password,
    /// Device Code Flow), which the engine doesn't support, so the engine's project leaves it out
    /// with <c>ExcludeAssets="native"</c>.
    /// </summary>
    [Fact]
    public void TheAccountBrokersNativeLibraryIsLeftOut()
    {
        var found = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*msalruntime*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(AppContext.BaseDirectory, path))
            .ToList();

        Assert.True(found.Count == 0,
            "The account broker's native library reached the build: " + string.Join(", ", found)
            + ". The reference to Microsoft.Data.SqlClient.Extensions.Azure needs ExcludeAssets=\"native\".");
    }
}
