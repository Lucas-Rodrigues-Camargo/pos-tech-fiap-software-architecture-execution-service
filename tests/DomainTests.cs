using Execution;
namespace Tests;
public class DomainTests
{
    [Fact] public void CompleteExecution()
    {
        var item = new WorkItem(Guid.NewGuid()); item.StartDiagnosis(); item.RecordDiagnosis("Oil leak");
        item.StartRepair(true); item.Finish();
        Assert.Equal(JobStatus.Finished, item.Status); Assert.Equal("Oil leak", item.Diagnosis);
        Assert.Throws<InvalidOperationException>(() => item.Cancel());
    }
    [Fact] public void RequiresValidOrder() => Assert.Throws<ArgumentException>(() => new WorkItem(Guid.Empty));
    [Fact] public void RequiresDiagnosisAndPayment()
    {
        var item = new WorkItem(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => item.Finish());
        item.StartDiagnosis();
        Assert.Throws<ArgumentException>(() => item.RecordDiagnosis(""));
        item.RecordDiagnosis("Repair"); Assert.Throws<InvalidOperationException>(() => item.StartRepair(false));
        item.StartRepair(true); Assert.Throws<InvalidOperationException>(() => item.Cancel());
    }
    [Fact] public void CanCancelBeforeRepairs()
    {
        var item = new WorkItem(Guid.NewGuid()); item.Cancel(); item.Cancel();
        Assert.Equal(JobStatus.Canceled, item.Status);
        Assert.Throws<InvalidOperationException>(() => item.StartDiagnosis());
    }
}

