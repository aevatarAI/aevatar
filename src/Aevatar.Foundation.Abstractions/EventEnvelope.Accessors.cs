namespace Aevatar.Foundation.Abstractions;

public partial class EventEnvelope
{
    public EnvelopeRoute EnsureRoute()
    {
        Route ??= new EnvelopeRoute();
        return Route;
    }

    public EnvelopePropagation EnsurePropagation()
    {
        Propagation ??= new EnvelopePropagation();
        return Propagation;
    }

    public EnvelopeRuntime EnsureRuntime()
    {
        Runtime ??= new EnvelopeRuntime();
        return Runtime;
    }
}

public partial class EnvelopePropagation
{
    public TraceContext EnsureTrace()
    {
        Trace ??= new TraceContext();
        return Trace;
    }
}

public partial class EnvelopeRuntime
{
    public DeliveryDeduplication EnsureDeduplication()
    {
        Deduplication ??= new DeliveryDeduplication();
        return Deduplication;
    }

    public EnvelopeRetryContext EnsureRetry()
    {
        Retry ??= new EnvelopeRetryContext();
        return Retry;
    }

    public EnvelopeCallbackContext EnsureCallback()
    {
        Callback ??= new EnvelopeCallbackContext();
        return Callback;
    }

    public EnvelopeForwardingContext EnsureForwarding()
    {
        Forwarding ??= new EnvelopeForwardingContext();
        return Forwarding;
    }

    public EnvelopeDispatchControl EnsureDispatch()
    {
        Dispatch ??= new EnvelopeDispatchControl();
        return Dispatch;
    }
}
