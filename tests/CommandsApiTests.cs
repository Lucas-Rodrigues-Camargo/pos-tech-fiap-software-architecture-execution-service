using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Platform;
namespace Tests;

public class CommandsApiTests
{
    [Fact]
    public async Task DiagnosisAndFinish()
    {
        using var app = new ApiFactory(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("Admin"));
        var id = Guid.NewGuid(); await app.Store.Save(new State { Id = id, Status = "Diagnosing" }, 0);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/jobs/{id}/diagnosis", new { description = "Repair", amount = 100 })).StatusCode);
        var state = (await app.Store.Get(id))!; var version = state.Version; state.Move("ReadyForRepair"); await app.Store.Save(state, version);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/jobs/{id}/start", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/jobs/{id}/finish", null)).StatusCode);
    }
}
