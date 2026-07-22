using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using ZLogger;

namespace ZLoggerOpenTelemetry;

/// <summary>
/// An implementation of <see cref="IAsyncLogProcessor"/> that exports ZLogger entries to OpenTelemetry.
/// It uses an internal batching mechanism and a background thread to avoid blocking the main logging path.
/// </summary>
public sealed class OpenTelemetryZLoggerProcessor : IAsyncLogProcessor
{
    private static readonly Func<LogRecord> CreateLogRecord = BuildFactory();

    /// <summary>
    /// Uses reflection to build a factory for <see cref="LogRecord"/> instances.
    /// This is necessary because <see cref="LogRecord"/> does not expose a public parameterless constructor
    /// in some versions of the OpenTelemetry SDK.
    /// </summary>
    private static Func<LogRecord> BuildFactory()
    {
        var ctor = typeof(LogRecord).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

        if (ctor == null)
        {
            throw new InvalidOperationException(
                "LogRecord parameterless constructor not found. " +
                "OpenTelemetry API may have changed.");
        }

        // Compile to a delegate - pays reflection cost once at startup,
        // each subsequent call is a fast delegate invocation.
        var newExpr = Expression.New(ctor);
        var lambda = Expression.Lambda<Func<LogRecord>>(newExpr);
        return lambda.Compile();
    }

    // Tunable parameters.
    private readonly int _batchSize;
    private readonly TimeSpan _exportInterval;
    private readonly Action<Exception> _errorHandler;

    // Internals
    private readonly BaseExporter<LogRecord> _exporter;
    private readonly Channel<EntryWithContext> _channel;
    private readonly Task _writeLoop;
    private int _droppedCount;

    /// <summary>
    /// Tracks dropped entries for diagnostics.
    /// </summary>
    public int DroppedCount => _droppedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenTelemetryZLoggerProcessor"/> class.
    /// </summary>
    /// <param name="exporter">The OpenTelemetry exporter to use for sending logs.</param>
    /// <param name="channelCapacity">The maximum number of log entries to buffer up before dropping newer entries.</param>
    /// <param name="batchSize">The maximum number of log entries to include in a single export batch.</param>
    /// <param name="exportInterval">The maximum amount of time to wait for a batch to fill before exporting.</param>
    /// <param name="errorHandler">An optional handler for exceptions that occur during the background write loop.</param>
    public OpenTelemetryZLoggerProcessor(
        BaseExporter<LogRecord> exporter,
        int channelCapacity = 1024,
        int batchSize = 24,
        TimeSpan? exportInterval = null,
        Action<Exception>? errorHandler = null)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _batchSize = batchSize;
        _exportInterval = exportInterval ?? TimeSpan.FromMilliseconds(200);
        _errorHandler = errorHandler ?? (_ =>
        {
            /* swallow */
        });

        _channel = Channel.CreateBounded<EntryWithContext>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false,
                SingleWriter = false,
                SingleReader = true
            }
        );

        _writeLoop = Task.Run(WriteLoop);

        // Self-test: verify we can still construct LogRecord via reflection.
        try
        {
            var test = CreateLogRecord();
            test.Timestamp = DateTime.UtcNow;
            test.ObservedTimestamp = DateTime.UtcNow;
            test.CategoryName = "self-test";
            test.LogLevel = LogLevel.Information;
            test.TraceId = default;
            // If we got here, the API contract still holds.
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"{nameof(OpenTelemetryZLoggerProcessor)} cannot create LogRecord instances. " +
                "The OpenTelemetry API may have changed. " +
                "Check that the LogRecord parameterless constructor and public " +
                $"properties are still accessible. Inner exception: {e.Message}", e);
        }
    }

    /// <summary>
    /// Enqueues a log entry for background processing and export.
    /// </summary>
    /// <param name="log">The log entry to process.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Post(IZLoggerEntry log)
    {
        var ctx = new EntryWithContext(log);
        if (!_channel.Writer.TryWrite(ctx))
        {
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <summary>
    /// Shuts down the background write loop and disposes of the exporter.
    /// This will wait for any buffered logs to be exported before returning.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writeLoop.ConfigureAwait(false);
        _exporter.Shutdown();
    }

    /// <summary>
    /// The background loop that reads from the channel, batches logs, and exports them.
    /// </summary>
    private async Task WriteLoop()
    {
        var list = new List<EntryWithContext>(_batchSize);
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            list.Clear();

            if (_exportInterval > TimeSpan.Zero)
            {
                using var cts = new CancellationTokenSource(_exportInterval);
                try
                {
                    while (list.Count < _batchSize)
                    {
                        if (reader.TryRead(out var item))
                        {
                            list.Add(item);
                        }
                        else
                        {
                            if (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                            {
                                continue;
                            }

                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                // Greedy drain
                while (list.Count < _batchSize && reader.TryRead(out var item))
                {
                    list.Add(item);
                }
            }

            if (list.Count == 0)
            {
                continue;
            }

            try
            {
                // Suppress instrumentation to avoid infinite loops if the exporter itself logs
                using var scope = SuppressInstrumentationScope.Begin();

                var batch = new LogRecord[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    batch[i] = ConvertToLogRecord(list[i]);
                }

                var openTelBatch = new Batch<LogRecord>(batch, batch.Length);
                _exporter.Export(in openTelBatch);
            }
            catch (Exception e)
            {
                _errorHandler(e);
            }
            finally
            {
                // Return the ZLogger entries to the pool
                foreach (var ctx in list)
                {
                    ctx.Entry.Return();
                }
            }
        }
    }

    /// <summary>
    /// Converts a ZLogger entry and its context into an OpenTelemetry <see cref="LogRecord"/>.
    /// </summary>
    private static LogRecord ConvertToLogRecord(EntryWithContext ctx)
    {
        var entry = ctx.Entry;
        var info = entry.LogInfo;

        var record = CreateLogRecord();

        record.Timestamp = info.Timestamp.Utc.DateTime;
        record.ObservedTimestamp = DateTime.UtcNow;
        record.CategoryName = info.Category.Name;
        record.LogLevel = info.LogLevel;
        record.EventId = info.EventId;
        record.Exception = info.Exception;
        record.FormattedMessage = entry.ToString();

        record.TraceId = ctx.TraceId;
        record.SpanId = ctx.SpanId;
        record.TraceFlags = ctx.TraceFlags;

        record.Attributes = ExtractAttributes(entry);

        return record;
    }

    /// <summary>
    /// Extracts structured logging parameters from a ZLogger entry as OpenTelemetry attributes.
    /// </summary>
    private static List<KeyValuePair<string, object?>> ExtractAttributes(IZLoggerEntry entry)
    {
        var attributes = new List<KeyValuePair<string, object?>>(entry.ParameterCount + 6)
        {
            new("message_template.text", entry.GetOriginalFormat())
        };

        if (entry.LogInfo.ThreadInfo.ThreadId > 0)
        {
            attributes.Add(new KeyValuePair<string, object?>("thread.id", entry.LogInfo.ThreadInfo.ThreadId));
        }

        if (!string.IsNullOrEmpty(entry.LogInfo.ThreadInfo.ThreadName))
        {
            attributes.Add(new KeyValuePair<string, object?>("thread.name", entry.LogInfo.ThreadInfo.ThreadName));
        }

        if (!string.IsNullOrEmpty(entry.LogInfo.FilePath))
        {
            attributes.Add(new KeyValuePair<string, object?>("code.file.path", entry.LogInfo.FilePath));
        }

        if (entry.LogInfo.LineNumber > 0)
        {
            attributes.Add(new KeyValuePair<string, object?>("code.line.number", entry.LogInfo.LineNumber));
        }
        
        var functionName = ResolveFunctionName(entry.LogInfo.MemberName, entry.LogInfo.Category.Name);
        if (!string.IsNullOrEmpty(functionName))
        {
            attributes.Add(new KeyValuePair<string, object?>("code.function.name", functionName));
        }

        for (var i = 0; i < entry.ParameterCount; i++)
        {
            var key = entry.GetParameterKeyAsString(i);
            var value = entry.GetParameterValue(i);
            attributes.Add(new KeyValuePair<string, object?>(key, value));
        }

        return attributes;
    }

    private static string? ResolveFunctionName(string? memberName, string? categoryName)
    {
        if (string.IsNullOrEmpty(memberName))
        {
            return null;
        }

        // Synthetic member names from top-level statements and file-based programs should fall back to different
        // attributes.
        if (memberName is "<Main>$" or "<Program>$")
        {
            return null;
        }

        // OTel expects the function name to be fully-qualified, so assume the category name is the class name.
        return string.IsNullOrEmpty(categoryName)
            ? memberName
            : $"{categoryName}.{memberName}";
    }
}