using DeckFlow.Web.Controllers.Api;
using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ArchidektCacheJobsControllerTests
{
    [Fact]
    public async Task StartAsync_ReturnsAccepted_WithJobPayload()
    {
        var job = new ArchidektCacheJobStatus(
            Guid.NewGuid(),
            ArchidektCacheJobState.Queued,
            600,
            DateTimeOffset.UtcNow,
            null,
            null,
            0,
            0,
            null);
        var controller = CreateController(new FakeArchidektCacheJobService(job, startedNewJob: true))
        {
            Url = new FakeUrlHelper()
        };

        var response = await controller.StartAsync(new ArchidektCacheJobStartRequest
        {
            DurationSeconds = 600
        }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedAtActionResult>(response.Result);
        var payload = Assert.IsType<ArchidektCacheJobEnqueueResponse>(accepted.Value);
        Assert.Equal(job.JobId, payload.JobId);
        Assert.True(payload.StartedNewJob);
        Assert.Equal("Queued", payload.State);
    }

    [Fact]
    public async Task StartAsync_ReturnsBadRequest_WhenDurationInvalid()
    {
        var controller = CreateController(new FakeArchidektCacheJobService(null, startedNewJob: false));

        var response = await controller.StartAsync(new ArchidektCacheJobStartRequest
        {
            DurationSeconds = 0
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public void GetByIdAsync_ReturnsStatus_WhenJobExists()
    {
        var job = new ArchidektCacheJobStatus(
            Guid.NewGuid(),
            ArchidektCacheJobState.Succeeded,
            600,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            25,
            18,
            null);
        var controller = new ArchidektCacheJobsController(new FakeArchidektCacheJobService(job, startedNewJob: false));

        var response = controller.GetByIdAsync(job.JobId);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<ArchidektCacheJobStatusResponse>(ok.Value);
        Assert.Equal(25, payload.DecksProcessed);
        Assert.Equal(18, payload.AdditionalDecksFound);
        Assert.Equal("Succeeded", payload.State);
    }

    [Fact]
    public void GetByIdAsync_ReturnsNotFound_WhenJobMissing()
    {
        var controller = new ArchidektCacheJobsController(new FakeArchidektCacheJobService(null, startedNewJob: false));

        var response = controller.GetByIdAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task StartAsync_ReturnsForbidden_WhenOriginIsCrossSite()
    {
        var controller = new ArchidektCacheJobsController(new FakeArchidektCacheJobService(null, startedNewJob: false))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var response = await controller.StartAsync(new ArchidektCacheJobStartRequest
        {
            DurationSeconds = 300
        }, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    private static ArchidektCacheJobsController CreateController(IArchidektCacheJobService service)
    {
        var controller = new ArchidektCacheJobsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://deckflow.test";
        return controller;
    }

    private sealed class FakeArchidektCacheJobService : IArchidektCacheJobService
    {
        private readonly ArchidektCacheJobStatus? _job;
        private readonly bool _startedNewJob;

        public FakeArchidektCacheJobService(ArchidektCacheJobStatus? job, bool startedNewJob)
        {
            _job = job;
            _startedNewJob = startedNewJob;
        }

        public Task<ArchidektCacheJobEnqueueResult> EnqueueAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            if (_job is null)
            {
                throw new InvalidOperationException("No fake job was configured.");
            }

            return Task.FromResult(new ArchidektCacheJobEnqueueResult(_job, _startedNewJob));
        }

        public ArchidektCacheJobStatus? GetJob(Guid jobId)
            => _job?.JobId == jobId ? _job : null;

        public ArchidektCacheJobStatus? GetActiveJob()
            => _job;
    }

    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new()
        {
            HttpContext = new DefaultHttpContext()
        };

        public FakeUrlHelper()
        {
            ActionContext.HttpContext.Request.Scheme = "https";
            ActionContext.HttpContext.Request.Host = new HostString("example.test");
        }

        public string? Action(UrlActionContext actionContext)
            => TryGetJobId(actionContext.Values, out var jobId)
                ? $"/api/archidekt-cache-jobs/{jobId}"
                : "/api/archidekt-cache-jobs";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;

        public string? ActionLink(string? action, string? controller, object? values, string? protocol, string? host, string? fragment)
            => values is not null
                ? $"/api/archidekt-cache-jobs/{values.GetType().GetProperty("jobId")?.GetValue(values)}"
                : "/api/archidekt-cache-jobs";

        private static bool TryGetJobId(object? values, out object? jobId)
        {
            jobId = values?.GetType().GetProperty("jobId")?.GetValue(values);
            return jobId is not null;
        }
    }
}
