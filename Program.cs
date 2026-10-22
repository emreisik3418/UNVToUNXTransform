using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            var migrationService = serviceProvider.GetRequiredService<IMigrationService>();
            var excelService = serviceProvider.GetRequiredService<IExcelMappingService>();
            var config = serviceProvider.GetRequiredService<IOptions<ApplicationConfiguration>>().Value;

            // Step 1: Read the mapping file (before logging on - nothing to do without it)
            string mappingFilePath = Path.Combine(config.OutputDirectory ?? AppDomain.CurrentDomain.BaseDirectory, config.MappingFileName);
            if (!File.Exists(mappingFilePath))
            {
                logger.LogError($"Migration aborted. The mapping Excel file was not found at: {mappingFilePath}");
                logger.LogWarning("Please create an Excel file with headers 'Source UNV ID' and 'Target UNX ID' containing your data source mappings.");
                return;
            }

            var loadedMappings = excelService.ReadMappings(mappingFilePath);
            if (loadedMappings.Count == 0)
            {
                logger.LogError("No mappings found in the Excel file.");
                return;
            }
            logger.LogInformation($"Loaded {loadedMappings.Count} mappings from Excel.");

            // Step 2: Authentication
            if (!await authService.LogonAsync())
            {
                logger.LogError("Authentication failed. Exiting application.");
                return;
            }

            // Step 3: Scan and convert, one document at a time
            await migrationService.RunAsync(loadedMappings);
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
        services.AddSingleton<LogonTokenStore>();
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
        services.AddSingleton<IMigrationService, MigrationService>();
    }
}
