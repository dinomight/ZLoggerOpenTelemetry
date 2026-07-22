using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using ZLogger;

namespace ZLoggerOpenTelemetry.Benchmarks;

[MemoryDiagnoser]
public partial class LoggingBenchmarks : IAsyncDisposable
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<LoggingBenchmarks>? _logger;
    private OpenTelemetryZLoggerProcessor? _processor;

    [ZLoggerMessage(LogLevel.Information, "Hello, {name}! The count is {count}.")]
    public partial void ZLoggerSourceGenerator(ILogger logger, string name, int count);

    [LoggerMessage(LogLevel.Information, "Hello, {name}! The count is {count}.")]
    public static partial void MelSourceGenerator(ILogger logger, string name, int count);

    [GlobalSetup]
    public void Setup()
    {
        _processor = new OpenTelemetryZLoggerProcessor(new NullExporter());

        _loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerLogProcessor(_processor);
        });

        _logger = _loggerFactory.CreateLogger<LoggingBenchmarks>();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _loggerFactory?.Dispose();
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }
    }

    [Benchmark]
    public void LogLiteralString()
    {
        _logger?.ZLogInformation($"Hello, world!");
    }

    [Benchmark]
    public void LogStructuredTwoParameters()
    {
        const string name = "World";
        const int count = 42;
        _logger?.ZLogInformation($"Hello, {name}! The count is {count}.");
    }

    [Benchmark]
    public void ZLoggerSourceGeneratorTwoParameters()
    {
        const string name = "World";
        const int count = 42;
        if (_logger != null)
        {
            ZLoggerSourceGenerator(_logger, name, count);
        }
    }

    [Benchmark]
    public void MelSourceGeneratorTwoParameters()
    {
        const string name = "World";
        const int count = 42;
        if (_logger != null)
        {
            MelSourceGenerator(_logger, name, count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _loggerFactory?.Dispose();

        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }
        
        GC.SuppressFinalize(this);
    }
}

public class NullExporter : BaseExporter<LogRecord>
{
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        return ExportResult.Success;
    }
}