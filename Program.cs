using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;
using UNVToUNXTransform.Infrastructure;
using UNVToUNXTransform.Services;

namespace UNVToUNXTransform;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Setup Configuration
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        IConfiguration configuration = builder.Build();

        // 2. Setup Dependency Injection
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection, configuration);
        var serviceProvider = serviceCollection.BuildServiceProvider();

        // 3. Application Flow
        var logger = serviceProvider.GetRequiredService<ILogger>();
        logger.LogInformation("Starting UNV to UNX Data Source Migration Tool...");

        IAuthenticationService? authService = null;

        try
        {
            authService = serviceProvider.GetRequiredService<IAuthenticationService>();
            var docService = serviceProvider.GetRequiredService<IDocumentService>();
            var excelService = serviceProvider.GetRequiredService<IExcelMappingService>();
            var config = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<UNVToUNXTransform.Configuration.ApplicationConfiguration>>().Value;

            // Step 1: Authentication
            bool isAuthenticated = await authService.LogonAsync();
            if (!isAuthenticated)
            {
                logger.LogError("Authentication failed. Exiting application.");
                return;
            }

            // Step 2 (Revised): Read Mapping File
            string mappingFilePath = Path.Combine(config.OutputDirectory ?? AppDomain.CurrentDomain.BaseDirectory, config.MappingFileName);
            
            if (!File.Exists(mappingFilePath))
            {
                logger.LogError($"Migration aborted. The mapping Excel file was not found at: {mappingFilePath}");
                logger.LogWarning("Please create an Excel file with headers 'Source UNV ID' and 'Target UNX ID' containing your data source mappings.");
                // Even on return, we should logoff if authenticated. 
                // However, returns inside Try block make Finally run.
            }
            else 
            {
                var loadedMappings = excelService.ReadMappings(mappingFilePath);
                if (loadedMappings.Count == 0)
                {
                    logger.LogError("No mappings found in the Excel file.");
                }
                else
                {
                    logger.LogInformation($"Loaded {loadedMappings.Count} mappings from Excel.");

                    // Step 3: List Documents & Match with Mappings
                    var documentCandidtes = await docService.GetDocumentsWithUnvDataSourcesAsync();
                    
                    int matchCount = 0;
                    if (documentCandidtes.Count > 0)
                    {
                        logger.LogInformation("Matching documents against loaded mappings...");
                        
                        var backedUpDocumentIds = new HashSet<int>();

                        // First pass: Match documents to mappings
                        foreach (var doc in documentCandidtes)
                        {
                            var mapping = loadedMappings.FirstOrDefault(m => m.SourceUnvId == doc.SourceUnvId);
                            if (mapping != null)
                            {
                                matchCount++;
                                doc.TargetUnxId = mapping.TargetUnxId;
                                logger.LogSuccess($"MATCH: Document '{doc.DocumentName}' (ID: {doc.DocumentId}) DataProvider '{doc.DataProviderName}' uses Source UNV '{doc.SourceUnvId}'. Will convert to Target UNX '{doc.TargetUnxId}'.");
                            }
                        }

                        logger.LogInformation($"--------------------------------------------------");
                        logger.LogSuccess($"Matching Complete: Found {matchCount} data providers matching the provided source IDs.");

                        // Second pass: Process updates grouped by Document
                        // Group by Document Id first
                        var docGroups = documentCandidtes
                                        .Where(d => !string.IsNullOrEmpty(d.TargetUnxId))
                                        .GroupBy(d => d.DocumentId)
                                        .ToList();

                if (docGroups.Any())
                {
                    logger.LogInformation("--------------------------------------------------");
                    logger.LogInformation("PHASE 1: BACKUP OPERATIONS");
                    logger.LogInformation("Creating backups for all matched documents before any modification...");

                    // PHASE 1: Perform ALL backups first
                    foreach (var group in docGroups)
                    {
                        int docId = group.Key;
                        string docName = group.First().DocumentName;

                        if (!backedUpDocumentIds.Contains(docId))
                        {
                            string backupName = $"{docName}_Backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                            var backupId = await docService.CopyDocumentAsync(docId, backupName);

                            if (backupId.HasValue)
                            {
                                backedUpDocumentIds.Add(docId);
                                // Release the backup copy's WebI session immediately - it isn't touched again.
                                await docService.CloseDocumentAsync(backupId.Value);
                            }
                            else
                            {
                                logger.LogError($"Backup failed for document {docId}. Proceeding with caution.");
                            }
                        }
                    }

                    logger.LogInformation("--------------------------------------------------");
                    logger.LogInformation("PHASE 2: UPDATE OPERATIONS");
                    logger.LogInformation("Starting data source conversion process for original documents...");

                    // PHASE 2: Perform ALL updates
                    foreach (var group in docGroups)
                    {
                        int docId = group.Key; // This preserves the ORIGINAL Document ID
                        string docName = group.First().DocumentName;

                        logger.LogInformation($"Processing Document ID: {docId} ({docName})");

                        // Group by Target UNX ID within the document
                        var targetGroups = group.GroupBy(g => g.TargetUnxId);

                        foreach (var targetGroup in targetGroups)
                        {
                            string targetUnxId = targetGroup.Key;
                            var dpIds = targetGroup.Select(x => x.DataProviderId).ToList();

                            logger.LogInformation($"  - Converting {dpIds.Count} Data Providers to {targetUnxId}...");
                            
                            bool success = await docService.ChangeDataSourceAsync(docId, dpIds, targetUnxId);
                            
                            if (success)
                            {
                                logger.LogSuccess($"  - SUCCESS: Updated Document {docId}.");
                                
                                // 3. Save/Refresh the document
                                await docService.SaveDocumentAsync(docId);
                            }
                            else
                            {
                                logger.LogError($"  - FAILURE: Could not update Document {docId} for target {targetUnxId}.");
                            }
                        }

                        // Release the original document's WebI session now that all its target
                        // groups have been processed, to avoid exhausting the server's connection
                        // limit (SAP error RWI 00239) across a run with many documents.
                        await docService.CloseDocumentAsync(docId);
                    }
                }

                        logger.LogInformation($"--------------------------------------------------");
                        logger.LogSuccess($"Analysis Complete: Found {matchCount} documents matching the provided source IDs out of {documentCandidtes.Count} total scanned documents.");
                    }
                    else
                    {
                        logger.LogWarning("No documents with data sources found.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError("An unhandled exception occurred.", ex);
        }
        finally
        {
            if (authService != null)
            {
                await authService.LogoffAsync();
            }
            logger.LogInformation("Application finished. Press any key to exit.");
            Console.ReadKey();
        }
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        services.Configure<SapBoConfiguration>(configuration.GetSection(SapBoConfiguration.SectionName));
        services.Configure<ApplicationConfiguration>(configuration.GetSection(ApplicationConfiguration.SectionName));

        // Infrastructure
        services.AddSingleton<ILogger, ConsoleLogger>();
        services.AddHttpClient<IHttpClientWrapper, HttpClientWrapper>((serviceProvider, client) =>
        {
            var config = serviceProvider.GetRequiredService<IOptions<SapBoConfiguration>>().Value;
            var baseUrl = config.BaseUrl;
            
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }
            
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            // No Credentials set (SocketsHttpHandler's equivalent of
            // HttpClientHandler.UseDefaultCredentials = false) - logon is
            // handled explicitly via the X-SAP-LogonToken header.
            // On a long run (hours), an intermediate device (firewall, load
            // balancer, SAP Web Dispatcher) can silently drop an idle/aged TCP
            // connection without the client noticing. Without a bound here the
            // pool holds onto such a connection forever and every request
            // routed onto it hangs until HttpClient.Timeout - which is exactly
            // what "everything times out after ~1.5 hours" looks like. Forcing
            // periodic recycling means a dead connection is replaced long
            // before it gets a chance to strand a request.
            PooledConnectionLifetime = TimeSpan.FromMinutes(4),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        });

        // Services
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IDocumentService, DocumentService>(); // Injects Auth Service automatically
        services.AddSingleton<IExcelMappingService, ExcelMappingService>();
    }
}
