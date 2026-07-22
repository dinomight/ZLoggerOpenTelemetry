using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using ZLogger;
using ZLoggerOpenTelemetry;
using ConsoleApp;

// Initialize the OpenTelemetry Console Exporter
var exporter = new ConsoleLogRecordExporter(new ConsoleExporterOptions());

// Initialize the ZLogger OpenTelemetry Processor with the exporter.
// By default, it uses a 200ms export interval to improve batching efficiency.
var processor = new OpenTelemetryZLoggerProcessor(exporter);

// Configure Logging
using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(LogLevel.Trace);
    logging.AddZLoggerLogProcessor(options =>
    {
        options.CaptureThreadInfo = true;
        options.IncludeScopes = true;
        return processor;
    });
});

var logger = loggerFactory.CreateLogger("Program");

// Log some messages
logger.ZLogInformation($"Hello, ZLogger with OpenTelemetry! Current time is {DateTime.Now}");

const int userId = 123;
const string action = "Login";
logger.ZLogInformation($"User {userId} performed {action}");

// Invoke BusinessLogic
var businessLogic = new BusinessLogic(loggerFactory.CreateLogger<BusinessLogic>());
businessLogic.PerformAction("DataMigration", 3);

try 
{
    throw new InvalidOperationException("Something went wrong!");
}
catch (Exception ex)
{
    businessLogic.HandleError("CriticalTask", ex);
}

// ZLogger and OpenTelemetryZLoggerProcessor are asynchronous.
// Properly disposing the processor ensures all buffered logs are exported.
await processor.DisposeAsync();

Console.WriteLine("Done.");