#if DEBUG
using System.Diagnostics.Tracing;
using WinMuse.Helpers;

namespace WinMuse.Diagnostics;

/// <summary>Test aid (Debug only): logs why each full (gen2) garbage collection started.</summary>
internal sealed class GcReasonLog : EventListener
{
    private static GcReasonLog? _instance;
    public static void Start() => _instance ??= new GcReasonLog();

    private static readonly string[] Reasons =
    {
        "small-object allocation", "induced (GC.Collect)", "low memory", "empty", "large-object allocation", "out of space (SOH)",
        "out of space (LOH)", "induced, not forced", "stress", "low memory (induced)", "induced compacting", "low memory (host)",
        "PM full GC", "low memory (host, blocking)", "bgc tuning", "induced (no GC region)",
    };

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1); // GC
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCStart") != true || e.Payload == null) return;
        int depth = Convert.ToInt32(e.Payload[e.PayloadNames!.IndexOf("Depth")]);
        if (depth < 2) return;
        int reason = Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("Reason")]);
        int type = Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("Type")]);
        AppPaths.Log($"GC gen2 started: {(reason < Reasons.Length ? Reasons[reason] : reason.ToString())}, {(type == 1 ? "background" : "blocking")}");
    }
}
#endif
