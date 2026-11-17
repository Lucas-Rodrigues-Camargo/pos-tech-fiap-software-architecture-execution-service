namespace Execution;
public enum JobStatus { Queued, Diagnosing, Diagnosed, Repairing, Finished, Canceled }
public sealed class WorkItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid OrderId { get; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public string? Diagnosis { get; private set; }
    public WorkItem(Guid orderId)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.");
        OrderId = orderId;
    }
    public void StartDiagnosis() => Move(JobStatus.Queued, JobStatus.Diagnosing);
    public void RecordDiagnosis(string diagnosis)
    {
        if (string.IsNullOrWhiteSpace(diagnosis)) throw new ArgumentException("Diagnosis required.");
        Move(JobStatus.Diagnosing, JobStatus.Diagnosed); Diagnosis = diagnosis;
    }
    public void StartRepair(bool paymentConfirmed)
    {
        if (!paymentConfirmed) throw new InvalidOperationException("Payment confirmation required.");
        Move(JobStatus.Diagnosed, JobStatus.Repairing);
    }
    public void Finish() => Move(JobStatus.Repairing, JobStatus.Finished);
    public void Cancel()
    {
        if (Status is JobStatus.Repairing or JobStatus.Finished)
            throw new InvalidOperationException("Manual intervention required after physical repair begins.");
        Status = JobStatus.Canceled;
    }
    private void Move(JobStatus expected, JobStatus next)
    {
        if (Status != expected) throw new InvalidOperationException($"Expected {expected}, got {Status}.");
        Status = next;
    }
}

