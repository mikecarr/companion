using System;
using Prism.Events;
using Serilog;

namespace OpenIPC_Config.Services;

public interface IEventSubscriptionService
{
    void Subscribe<TEvent, TPayload>(Action<TPayload> action) where TEvent : PubSubEvent<TPayload>, new();

    void Publish<TEvent, TPayload>(TPayload payload) where TEvent : PubSubEvent<TPayload>, new();
}

public class EventSubscriptionService : IEventSubscriptionService
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger _logger;

    public EventSubscriptionService(IEventAggregator eventAggregator, ILogger logger)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _logger = logger?.ForContext(GetType()) ?? 
                  throw new ArgumentNullException(nameof(logger));
    }
    
    public void Subscribe<TEvent, TPayload>(Action<TPayload> action) where TEvent : PubSubEvent<TPayload>, new()
    {
        _eventAggregator.GetEvent<TEvent>().Subscribe(action, ThreadOption.UIThread);
        _logger.Verbose($"Subscribed to event {typeof(TEvent).Name}");
    }

    public void Publish<TEvent, TPayload>(TPayload payload) where TEvent : PubSubEvent<TPayload>, new()
    {
        _eventAggregator.GetEvent<TEvent>().Publish(payload);
        _logger.Verbose($"Published event {typeof(TEvent).Name} with payload {payload}");
    }
}