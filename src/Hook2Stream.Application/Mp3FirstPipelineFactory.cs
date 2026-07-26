using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public static class Mp3FirstPipelineFactory
{
    public static PipelineRun CreateInitial(ReleaseProject project, string trigger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        if (project.FlowKind != FlowKind.Mp3First)
        {
            throw new InvalidOperationException(
                "Only an audio-first release can initialize the MP3-first pipeline.");
        }

        var run = new PipelineRun
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.WaitingUser,
            Trigger = trigger.Trim()
        };
        run.Stages = Enum.GetValues<WorkflowLane>()
            .Select(lane => new PipelineStage
            {
                PipelineRun = run,
                PipelineRunId = run.Id,
                Lane = lane,
                State = lane == WorkflowLane.Audio
                    ? PipelineStageState.WaitingUser
                    : PipelineStageState.NotStarted,
                BlockerCode = lane == WorkflowLane.Audio
                    ? "audio.upload_required"
                    : "audio.not_ready"
            })
            .ToList();
        return run;
    }
}
