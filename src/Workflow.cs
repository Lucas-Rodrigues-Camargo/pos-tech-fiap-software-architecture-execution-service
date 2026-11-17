namespace Platform;
public sealed class ServiceWorkflow : IWorkflow
{
    public Task Apply(State s, Message m, CancellationToken ct)
    {
        switch(m.Type)
        {
            case "CreateJob":
                if (s.Status == "Canceled") break;
                s.Require("New"); s.Data = m.Data; s.Move("Diagnosing"); break;
            case "CompleteDiagnosis":
                if (s.Status == "Canceled") break;
                s.Require("Diagnosing");
                if (string.IsNullOrWhiteSpace(m.Data.Diagnosis) || m.Data.Amount <= 0 || decimal.Round(m.Data.Amount, 2) != m.Data.Amount)
                    throw new ArgumentException("Diagnosis and positive estimate with at most two decimal places required.");
                s.Data = s.Data with { Diagnosis = m.Data.Diagnosis, Amount = m.Data.Amount };
                s.Move("Diagnosed"); s.Emit(m, "DiagnosisCompleted", "os"); break;
            case "AuthorizeRepair":
                if (s.Status == "Canceled") break;
                s.Require("Diagnosed"); s.Move("ReadyForRepair"); s.Emit(m, "RepairQueued", "os"); break;
            case "StartRepair":
                s.Require("ReadyForRepair"); s.Move("Repairing"); s.Emit(m, "RepairStarted", "os"); break;
            case "FinishRepair":
                s.Require("Repairing"); s.Move("Finished"); s.Emit(m, "RepairFinished", "os"); break;
            case "CancelJob":
                if (s.Status is "Repairing" or "Finished")
                {
                    s.Emit(m, "CompensationFailed", "os"); break;
                }
                s.Move("Canceled"); s.Emit(m, "JobCanceled", "os"); break;
            default: throw new ArgumentException("Unknown event type.");
        }
        return Task.CompletedTask;
    }
}
