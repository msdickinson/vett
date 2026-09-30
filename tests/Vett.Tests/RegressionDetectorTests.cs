using System.Text.Json;
using Vett.Runner;

namespace Vett.Tests;

/// <summary>
/// Unit tests for the 5-bucket regression thresholds computed in ResolutionClassifier.
/// The actual vett bench regress command logic is integration-level; these tests verify
/// the math: given a set of InstanceResult lists, the derived rates are correct and
/// delta comparisons fire at the right thresholds.
/// </summary>
public class RegressionDetectorTests
{
    private static List<InstanceResult> MakeInstances(int pass, int fc, int timeout, int abstain, int nml)
    {
        var list = new List<InstanceResult>();
        for (int i = 0; i < pass;    i++) list.Add(new() { Resolution = ResolutionClassifier.Pass });
        for (int i = 0; i < fc;      i++) list.Add(new() { Resolution = ResolutionClassifier.FalseConfidence });
        for (int i = 0; i < timeout; i++) list.Add(new() { Resolution = ResolutionClassifier.Timeout });
        for (int i = 0; i < abstain; i++) list.Add(new() { Resolution = ResolutionClassifier.Abstain });
        for (int i = 0; i < nml;     i++) list.Add(new() { Resolution = ResolutionClassifier.NotModelable });
        return list;
    }

    [Fact]
    public void BucketCounts_AreCorrect()
    {
        // 10 instances: 3 pass, 4 fc, 1 timeout, 1 abstain, 1 nml
        var instances = MakeInstances(3, 4, 1, 1, 1);

        Assert.Equal(10, instances.Count);
        Assert.Equal(3, instances.Count(r => r.Resolution == ResolutionClassifier.Pass));
        Assert.Equal(4, instances.Count(r => r.Resolution == ResolutionClassifier.FalseConfidence));
        Assert.Equal(1, instances.Count(r => r.Resolution == ResolutionClassifier.Timeout));
        Assert.Equal(1, instances.Count(r => r.Resolution == ResolutionClassifier.Abstain));
        Assert.Equal(1, instances.Count(r => r.Resolution == ResolutionClassifier.NotModelable));
    }

    [Fact]
    public void PassRate_ComputedCorrectly()
    {
        var instances = MakeInstances(25, 70, 3, 1, 1); // 100 total, 25 pass
        int attempted = instances.Count;
        double passRate = 100.0 * instances.Count(r => r.Resolution == ResolutionClassifier.Pass) / attempted;
        Assert.Equal(25.0, passRate, 1);
    }

    [Fact]
    public void FcRate_ComputedCorrectly()
    {
        var instances = MakeInstances(25, 60, 10, 4, 1); // 100 total, 60 fc
        int attempted = instances.Count;
        double fcRate = 100.0 * instances.Count(r => r.Resolution == ResolutionClassifier.FalseConfidence) / attempted;
        Assert.Equal(60.0, fcRate, 1);
    }

    [Fact]
    public void NmlRate_ComputedCorrectly()
    {
        var instances = MakeInstances(10, 70, 5, 5, 10); // 100 total, 10 nml
        int attempted = instances.Count;
        double nmlRate = 100.0 * instances.Count(r => r.Resolution == ResolutionClassifier.NotModelable) / attempted;
        Assert.Equal(10.0, nmlRate, 1);
    }

    [Fact]
    public void MetricsJson_RoundTrips()
    {
        // Simulate what bench grade writes to metrics.json.
        var entries = new[]
        {
            new { run_id = "run-A", attempted = 100, pass = 30, false_confidence = 55, not_modelable = 5, timeout = 8, abstain = 2 },
            new { run_id = "run-B", attempted = 100, pass = 28, false_confidence = 57, not_modelable = 5, timeout = 8, abstain = 2 },
        };

        var json = JsonSerializer.Serialize(entries);
        var rt = JsonSerializer.Deserialize<List<JsonElement>>(json)!;

        Assert.Equal(2, rt.Count);
        Assert.Equal("run-A", rt[0].GetProperty("run_id").GetString());
        Assert.Equal(30, rt[0].GetProperty("pass").GetInt32());
        Assert.Equal("run-B", rt[1].GetProperty("run_id").GetString());
        Assert.Equal(28, rt[1].GetProperty("pass").GetInt32());
    }

    [Theory]
    // Below threshold: no regression
    [InlineData(30.0, 30.0, false)]  // Δpass=0, no regression
    [InlineData(28.5, 30.0, false)]  // Δpass=-1.5, below -2pp threshold
    // At/above threshold: regression
    [InlineData(27.9, 30.0, true)]   // Δpass=-2.1, above threshold
    [InlineData(20.0, 30.0, true)]   // Δpass=-10, well above threshold
    public void PassRegression_DetectedAbove2ppDrop(double latestPass, double bestPass, bool expectRegression)
    {
        double delta = latestPass - bestPass;
        bool isRegression = delta < -2.0;
        Assert.Equal(expectRegression, isRegression);
    }

    [Theory]
    [InlineData(55.0, 55.0, false)]  // ΔFC=0, no regression
    [InlineData(56.9, 55.0, false)]  // ΔFC=+1.9, below threshold
    [InlineData(57.1, 55.0, true)]   // ΔFC=+2.1, above threshold
    public void FcRegression_DetectedAbove2ppRise(double latestFc, double bestFc, bool expectRegression)
    {
        double delta = latestFc - bestFc;
        bool isRegression = delta > 2.0;
        Assert.Equal(expectRegression, isRegression);
    }

    [Theory]
    [InlineData(5.0, 5.0, false)]   // ΔNML=0, no regression
    [InlineData(9.9, 5.0, false)]   // ΔNML=+4.9, below threshold
    [InlineData(10.1, 5.0, true)]   // ΔNML=+5.1, above threshold
    public void NmlRegression_DetectedAbove5ppRise(double latestNml, double bestNml, bool expectRegression)
    {
        double delta = latestNml - bestNml;
        bool isRegression = delta > 5.0;
        Assert.Equal(expectRegression, isRegression);
    }
}
