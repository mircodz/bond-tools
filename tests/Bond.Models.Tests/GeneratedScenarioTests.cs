using System;

namespace Bond.Models.Tests;

public sealed class GeneratedScenarioTests
{
    [Fact]
    public void ScenarioFailuresPreserveTheirOriginalExceptionAndMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GeneratedScenario.Run("", """
            Require(true, "a passing requirement should not throw");
            throw new InvalidOperationException("scenario failed");
            """));

        Assert.Equal("scenario failed", error.Message);
        Assert.Contains("Scenario.Run", error.StackTrace);
    }

    [Fact]
    public void FailedRequirementsPreserveTheirDiagnosticMessage()
    {
        var error = Assert.Throws<Exception>(() => GeneratedScenario.Run("", """
            Require(false, "shared model reference was lost");
            """));

        Assert.Equal("shared model reference was lost", error.Message);
    }
}
