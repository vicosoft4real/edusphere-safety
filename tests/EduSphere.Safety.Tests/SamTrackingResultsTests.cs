using System.Security.Cryptography;
using System.Text.Json;
using EduSphere.Safety.EdgeAgent;
using Xunit;

public sealed class SamTrackingResultsTests
{
    private static (string Dir, string Video) Fixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "clip.mp4"); File.WriteAllBytes(video, [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(new {
            model = "sam-3.1", sourceSha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])),
            width = 100, height = 80, frames = 3, fps = 10 }));
        File.WriteAllText(Path.Combine(dir, "tracks.jsonl"), """
            {"frame":1,"tracks":[{"id":42,"box":[10,20,30,60],"confidence":0.9}]}
            """);
        return (dir, video);
    }

    [Fact]
    public void ResultsBindToSourceAndKeepSkippedFramesEmpty()
    {
        var (dir, video) = Fixture();
        try {
            var results = SamTrackingResults.Load(dir, video, 10);
            Assert.Empty(results.ForFrame(0));
            var detection = Assert.Single(results.ForFrame(1));
            Assert.Equal(42, detection.ModelId);
            Assert.Equal(0.1, detection.Box.X, 9); Assert.Equal(0.5, detection.Box.H, 9);
        } finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WrongSourceOrFrameRateIsRejected()
    {
        var (dir, video) = Fixture();
        try {
            Assert.Throws<InvalidDataException>(() => SamTrackingResults.Load(dir, video, 5));
            File.WriteAllBytes(video, [4, 5, 6]);
            Assert.Throws<InvalidDataException>(() => SamTrackingResults.Load(dir, video, 10));
        } finally { Directory.Delete(dir, true); }
    }
}
