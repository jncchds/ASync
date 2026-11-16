using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ASync.Contracts;

namespace ASync.Tests.Api;

/// <summary>Resumable uploads through the real server and EF Core (the unit tests use an in-memory repository).</summary>
[Trait("Category", "Api")]
public class ApiUploadTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    [SkippableFact]
    public async Task APartSentAgain_AfterItsAcknowledgementWasLost_IsStoredAgain_NotA500()
    {
        // Found by scripts/e2e-sync.sh: the session is loaded with its parts tracked, and storing a part it already had
        // collided with the tracked copy ("another instance with the same key value is already being tracked").
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var content = RandomNumberGenerator.GetBytes(1000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var start = await world.PostAsync<UploadSessionDto>($"api/sync/folders/{world.AliceFolder}/storage/uploads",
            new StartUploadRequest("resent.bin", content.Length, 1_700_000_000_000), world.AliceDeviceToken);
        Assert.Equal(1, start.PartCount);

        async Task<HttpStatusCode> PutPart()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/sync/folders/{world.AliceFolder}/storage/uploads/{start.Id}/parts/0")
            {
                Content = new ByteArrayContent(content),
            };
            request.Headers.Authorization = new("Bearer", world.AliceDeviceToken);
            request.Headers.Add("X-Content-SHA256", hash);
            using var response = await world.Client.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.NoContent, await PutPart());
        // Continuing the upload: the server says which parts it has, and a client cut off mid-request sends one again.
        var again = await world.PostAsync<UploadSessionDto>($"api/sync/folders/{world.AliceFolder}/storage/uploads",
            new StartUploadRequest("resent.bin", content.Length, 1_700_000_000_000), world.AliceDeviceToken);
        Assert.Equal(start.Id, again.Id);
        Assert.Equal([0], again.ReceivedParts);
        Assert.Equal(HttpStatusCode.NoContent, await PutPart());

        using var complete = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/storage/uploads/{start.Id}/complete",
            world.AliceDeviceToken);
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
    }
}
