using System.Diagnostics;
using System.Runtime.CompilerServices;
using ZLogger;

namespace ZLoggerOpenTelemetry;

internal readonly struct EntryWithContext
{
    public readonly IZLoggerEntry Entry;
    public readonly ActivityTraceId TraceId;
    public readonly ActivitySpanId SpanId;
    public readonly ActivityTraceFlags TraceFlags;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntryWithContext(IZLoggerEntry entry)
    {
        Entry = entry;
        var activity = Activity.Current;
        TraceId = activity?.TraceId ?? default;
        SpanId = activity?.SpanId ?? default;
        TraceFlags = activity?.ActivityTraceFlags ?? default;
    }
}