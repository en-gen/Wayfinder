namespace Wayfinder.Silo.Infrastructure.Options
{
    public class AzureOptions
    {
        public const string ConfigKey = "Azure";

        // The Azure:Storage section is read twice, by design (co-located SDK + app keys):
        //
        // 1. Microsoft.Extensions.Azure's AddBlobServiceClient(section) - the SDK picks the auth
        //    mode from the section's shape (verified against the shipped Microsoft.Extensions.Azure
        //    1.14.0, decompiled ClientFactory):
        //      - a "connectionString" key (or a bare scalar section value) selects the
        //        connection-string constructor - the local/Azurite case;
        //      - otherwise constructor parameters are matched BY NAME from the section, so a
        //        "serviceUri" key selects the endpoint constructor with a TokenCredential, where
        //        ClientFactory.CreateCredential reads "credential" ("managedidentity",
        //        OrdinalIgnoreCase; optional "clientId" for a user-assigned identity) - the
        //        deployed shape (work item #30), e.g.:
        //          "Storage": {
        //            "serviceUri": "https://<account>.blob.core.windows.net",
        //            "credential": "managedidentity",
        //            "CaseStateContainer": "case-flow-casestate"
        //          }
        //    Keys the SDK does not recognize are ignored: ClientFactory.CreateClient consults
        //    only constructor-parameter names, CreateCredential only its fixed credential keys,
        //    and the section->BlobClientOptions binding skips unmatched keys (binder default) -
        //    which is what makes co-locating app keys like CaseStateContainer in the same
        //    section safe.
        //
        // 2. This options class - bound from the Azure section (Program.cs ConfigureServices),
        //    consumed via IOptionsMonitor<AzureOptions>. The binder likewise ignores the
        //    SDK-only keys (connectionString/serviceUri/credential) since no matching
        //    properties exist here.
        public const string StorageSectionKey = "Azure:Storage";

        // Same section-shape convention as Azure:Storage above (work item #30): a
        // "connectionString" key selects the connection-string TableServiceClient (Azurite/local),
        // "serviceUri" + "credential" the TokenCredential one (deployed) - see
        // Program.cs ConfigureServices (AddTableServiceClient) and ConfigureDeployedOrleans
        // (UseAzureStorageClustering / UseAzureTableReminderService), which both resolve the same
        // DI TableServiceClient built from this section - real Orleans cluster membership AND
        // durable reminders ride the same table endpoint. No bound options class for this section
        // (unlike StorageOptions.CaseStateContainer): nothing app-level needs a typed read of it,
        // only the SDK client factory (section-shape auth) and Orleans' own options.
        public const string ClusteringSectionKey = "Azure:Clustering";

        public StorageOptions Storage { get; set; } = new();

        public class StorageOptions
        {
            // Also the name of the AddAzureClients registration for the case-state
            // BlobContainerClient (WithName(nameof(CaseStateContainer)) in Program.cs) - the
            // Orleans container factory resolves the client by this name.
            //
            // Blob container names must be lowercase alphanumeric plus hyphens (Azure rule:
            // ^[a-z0-9]+(-[a-z0-9]+)*$, 3-63 chars - see AzureBlobUtils.ValidateContainerName
            // in Orleans.Persistence.AzureStorage), which is why the VALUE can't follow the
            // project's dot-separated naming convention (e.g. Case.Wayfinder.CaseState).
            public string CaseStateContainer { get; set; } = "case-flow-casestate";
        }
    }
}
