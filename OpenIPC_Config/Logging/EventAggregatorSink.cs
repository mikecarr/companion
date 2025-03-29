using System;
using System.Globalization;
using OpenIPC_Config.Events;
using Prism.Events;
using Serilog.Core;
using Serilog.Events;

namespace OpenIPC_Config.Logging;

public class EventAggregatorSink : ILogEventSink
{
    private readonly IEventAggregator _eventAggregator;
    private readonly IFormatProvider _formatProvider;

    public EventAggregatorSink(IEventAggregator eventAggregator, IFormatProvider formatProvider = null)
    {
        _eventAggregator = eventAggregator;
        _formatProvider = formatProvider ?? CultureInfo.InvariantCulture;
    }

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(_formatProvider);

        // Enqueue the log message instead of directly publishing
        LogQueue.Enqueue(message);
    }
}