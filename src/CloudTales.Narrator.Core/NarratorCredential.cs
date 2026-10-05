using Azure.Core;
using Azure.Identity;

namespace CloudTales.Narrator.Core;

/// <summary>
/// Picks the Entra ID credential explicitly instead of letting DefaultAzureCredential probe:
/// Managed Identity when the host sets NARRATOR_USE_MANAGED_IDENTITY=true (the deployed Function),
/// the Azure CLI login everywhere else (dev machine, local Function run).
/// </summary>
public static class NarratorCredential
{
    public const string ManagedIdentitySwitch = "NARRATOR_USE_MANAGED_IDENTITY";

    /// <summary>Creates the credential for the current environment.</summary>
    public static TokenCredential Create() =>
        string.Equals(Environment.GetEnvironmentVariable(ManagedIdentitySwitch), "true", StringComparison.OrdinalIgnoreCase)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new AzureCliCredential();
}
