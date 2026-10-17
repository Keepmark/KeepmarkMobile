namespace Keepmark.Core.Workflow;

/// <summary>Connectivity results belong only to the latest selected endpoint.</summary>
public sealed class ServiceCheckState
{
    public int Generation { get; private set; }
    public bool Ready { get; private set; }
    public bool CanCreate { get; private set; }
    public int Invalidate()
    {
        Ready = CanCreate = false;
        return ++Generation;
    }
    public bool Complete(int generation, Models.ServiceStatus status)
    {
        if (generation != Generation) return false;
        Ready = status is Models.ServiceStatus.Online or Models.ServiceStatus.NoNewSyncs;
        CanCreate = status == Models.ServiceStatus.Online;
        return Ready;
    }
}
