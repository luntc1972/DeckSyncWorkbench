using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Security;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers.Api;

[ApiController]
[Route("api/archidekt-cache-jobs")]
public sealed class ArchidektCacheJobsController : ControllerBase
{
    private readonly IArchidektCacheJobService _jobService;

    public ArchidektCacheJobsController(IArchidektCacheJobService jobService)
    {
        _jobService = jobService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ArchidektCacheJobEnqueueResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ArchidektCacheJobEnqueueResponse>> StartAsync([FromBody] ArchidektCacheJobStartRequest? request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        var durationSeconds = request?.DurationSeconds ?? 0;
        if (durationSeconds <= 0)
        {
            return BadRequest(new { Message = "DurationSeconds must be greater than zero." });
        }

        if (durationSeconds > 3600)
        {
            return BadRequest(new { Message = "DurationSeconds cannot exceed 3600 seconds." });
        }

        var result = await _jobService.EnqueueAsync(TimeSpan.FromSeconds(durationSeconds), cancellationToken);
        var response = ToEnqueueResponse(result);
        return AcceptedAtAction(nameof(GetByIdAsync), new { jobId = response.JobId }, response);
    }

    [HttpGet("{jobId:guid}", Name = nameof(GetByIdAsync))]
    [ProducesResponseType(typeof(ArchidektCacheJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ArchidektCacheJobStatusResponse> GetByIdAsync(Guid jobId)
    {
        var job = _jobService.GetJob(jobId);
        return job is null ? NotFound() : Ok(ToStatusResponse(job));
    }

    [HttpGet("active")]
    [ProducesResponseType(typeof(ArchidektCacheJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ArchidektCacheJobStatusResponse> GetActiveAsync()
    {
        var job = _jobService.GetActiveJob();
        return job is null ? NotFound() : Ok(ToStatusResponse(job));
    }

    /// <summary>
    /// Converts an enqueue result into the API response payload.
    /// </summary>
    /// <param name="result">Enqueue result returned by the background-job service.</param>
    /// <returns>API response payload describing the queued job.</returns>
    private ArchidektCacheJobEnqueueResponse ToEnqueueResponse(ArchidektCacheJobEnqueueResult result)
    {
        var statusUrl = Url.ActionLink(nameof(GetByIdAsync), values: new { jobId = result.Job.JobId }) ?? $"/api/archidekt-cache-jobs/{result.Job.JobId}";
        return new ArchidektCacheJobEnqueueResponse
        {
            JobId = result.Job.JobId,
            State = result.Job.State.ToString(),
            DurationSeconds = result.Job.DurationSeconds,
            RequestedUtc = result.Job.RequestedUtc,
            StartedUtc = result.Job.StartedUtc,
            CompletedUtc = result.Job.CompletedUtc,
            DecksProcessed = result.Job.DecksProcessed,
            AdditionalDecksFound = result.Job.AdditionalDecksFound,
            ErrorMessage = result.Job.ErrorMessage,
            StartedNewJob = result.StartedNewJob,
            StatusUrl = statusUrl
        };
    }

    /// <summary>
    /// Converts the internal job status object into the public API response shape.
    /// </summary>
    /// <param name="job">Tracked background job.</param>
    /// <returns>Public status response payload.</returns>
    private static ArchidektCacheJobStatusResponse ToStatusResponse(ArchidektCacheJobStatus job)
        => new()
        {
            JobId = job.JobId,
            State = job.State.ToString(),
            DurationSeconds = job.DurationSeconds,
            RequestedUtc = job.RequestedUtc,
            StartedUtc = job.StartedUtc,
            CompletedUtc = job.CompletedUtc,
            DecksProcessed = job.DecksProcessed,
            AdditionalDecksFound = job.AdditionalDecksFound,
            ErrorMessage = job.ErrorMessage
        };
}
