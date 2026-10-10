using System.Runtime.CompilerServices;
using DBToRestAPI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DBToRestAPI.Tests;

/// <summary>
/// The engine encrypts the sections listed under settings_encryption in its config files, in place,
/// the first time it starts. Test classes start engines in parallel against the same files in the
/// test output folder, so after a clean build one could read a file while another rewrites it, and
/// its whole class fails. Encrypting them once, before any test runs, leaves nothing to rewrite.
/// </summary>
internal static class EncryptTestConfigurationOnce
{
#pragma warning disable CA2255 // a test assembly, run by the test host like an application
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Run()
    {
        try
        {
            // The service reads the list of additional files from settings.xml and rewrites each one.
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddXmlFile("config/settings.xml", optional: true, reloadOnChange: false)
                .Build();
            _ = new SettingsEncryptionService(configuration, NullLogger<SettingsEncryptionService>.Instance);
        }
        catch (Exception)
        {
            // The engines encrypt the files themselves; this only takes the race away.
        }
    }
}
