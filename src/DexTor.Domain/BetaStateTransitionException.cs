namespace DexTor.Domain;

public sealed class BetaStateTransitionException : Exception
{
    public BetaStateTransitionException(BetaState state, BetaEvent @event)
        : base($"State transition is not defined for state '{state}' and event '{@event}'.")
    {
        State = state;
        Event = @event;
    }

    public BetaState State { get; }
    public BetaEvent Event { get; }
}
