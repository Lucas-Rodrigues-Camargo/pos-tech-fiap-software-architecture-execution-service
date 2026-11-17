using Platform;
namespace Tests;
public class WorkflowTests
{
    readonly ServiceWorkflow workflow = new();
    async Task Apply(State s,string type,Payload? data=null)=>await workflow.Apply(s,Message.Create(s.Id,type,"execution",data??new()),default);
    [Fact] public async Task DiagnosisToRepair()
    {
        var s=new State{Id=Guid.NewGuid()};
        await Apply(s,"CreateJob",new(Cpf:"cpf"));
        await Apply(s,"CompleteDiagnosis",new(Amount:150,Diagnosis:"Repair"));
        await Apply(s,"StartRepair"); await Apply(s,"FinishRepair");
        Assert.Equal("Finished",s.Status); Assert.Equal(3,s.Outbox.Count);
    }
    [Theory] [InlineData("New")] [InlineData("Diagnosing")] [InlineData("Diagnosed")]
    public async Task CancelBeforeRepair(string status)
    {
        var s=new State{Id=Guid.NewGuid(),Status=status};
        await Apply(s,"CancelJob"); Assert.Equal("Canceled",s.Status); Assert.Equal("JobCanceled",s.Outbox.Single().Type);
    }
    [Theory] [InlineData("Repairing")] [InlineData("Finished")]
    public async Task PhysicalWorkCannotRollback(string status)
    {
        var s=new State{Id=Guid.NewGuid(),Status=status};
        await Apply(s,"CancelJob"); Assert.Equal(status,s.Status); Assert.Equal("CompensationFailed",s.Outbox.Single().Type);
    }
    [Fact] public async Task InvalidOperations()
    {
        var s=new State{Status="Diagnosing"};
        await Assert.ThrowsAsync<ArgumentException>(()=>Apply(s,"CompleteDiagnosis",new(Amount:0,Diagnosis:"")));
        await Assert.ThrowsAsync<ArgumentException>(()=>Apply(s,"Unknown"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Apply(s,"StartRepair"));
    }
}
