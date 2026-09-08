using Bunit;
using DeckFlow.Studio.Shared;

namespace DeckFlow.Studio.Tests;

/// <summary>
/// bUnit coverage for PipelineStepper.razor, covering Phase 116 finding F-06: every accepted
/// stage value marks exactly one step current, an unrecognised value degrades to three plain
/// links, and the current step is announced to assistive technology rather than linked.
/// </summary>
public sealed class PipelineStepperTests : BunitContext
{
    [Fact]
    public void Stepper_RendersTheThreePipelineStages()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "harvest"));

        var steps = cut.FindAll("li.pipeline-step");

        Assert.Equal(3, steps.Count);
        Assert.Equal(["harvest", "review", "publish"], steps.Select(s => s.GetAttribute("data-stage")));
    }

    [Fact]
    public void Stepper_MarksHarvestCurrent()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "harvest"));

        var current = cut.Find("[data-stage='harvest'] .pipeline-step-current");

        Assert.Equal("step", current.GetAttribute("aria-current"));
        Assert.Empty(cut.FindAll("[data-stage='harvest'] a"));
    }

    [Fact]
    public void Stepper_MarksReviewCurrent()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "review"));

        var current = cut.Find("[data-stage='review'] .pipeline-step-current");

        Assert.Equal("step", current.GetAttribute("aria-current"));
        Assert.Empty(cut.FindAll("[data-stage='review'] a"));
    }

    [Fact]
    public void Stepper_MarksPublishCurrent()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "publish"));

        var current = cut.Find("[data-stage='publish'] .pipeline-step-current");

        Assert.Equal("step", current.GetAttribute("aria-current"));
        Assert.Empty(cut.FindAll("[data-stage='publish'] a"));
    }

    [Fact]
    public void Stepper_NonCurrentStagesAreLinks()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "review"));

        Assert.Equal("/harvest", cut.Find("[data-stage='harvest'] a").GetAttribute("href"));
        Assert.Equal("/publish", cut.Find("[data-stage='publish'] a").GetAttribute("href"));
    }

    [Fact]
    public void Stepper_DirectPushMarksThePublishStage()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "direct-push"));

        var publishStep = cut.Find("[data-stage='publish']");

        Assert.Equal("direct-push", publishStep.GetAttribute("data-variant"));
        Assert.NotNull(publishStep.QuerySelector(".pipeline-step-current"));
        Assert.Contains("Direct", publishStep.TextContent);
    }

    [Fact]
    public void Stepper_ExactlyOneStepIsEverCurrent()
    {
        foreach (var stage in new[] { "harvest", "review", "publish", "direct-push" })
        {
            var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, stage));

            Assert.Single(cut.FindAll(".pipeline-step-current"));
        }
    }

    [Fact]
    public void Stepper_UnknownStageMarksNoneCurrent()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "not-a-stage"));

        Assert.Empty(cut.FindAll(".pipeline-step-current"));
        Assert.Equal(3, cut.FindAll("li.pipeline-step a").Count);
    }

    [Fact]
    public void Stepper_StageMatchIsCaseInsensitive()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "Review"));

        Assert.NotNull(cut.Find("[data-stage='review'] .pipeline-step-current"));
    }

    [Fact]
    public void Stepper_ExposesAnAccessibleNavLandmark()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.CurrentStage, "harvest"));

        var nav = cut.Find("nav.pipeline-stepper");

        Assert.Equal("Content pipeline progress", nav.GetAttribute("aria-label"));
    }
}
