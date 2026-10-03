using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class WorkOrderIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Work_order_requires_checklist_then_supports_reject_resubmit_and_approval_with_versions()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var workerId = await WorkerIdAsync("worker");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var worker = await factory.CreateAuthenticatedClientAsync("worker");
        var order = await CreateWorkOrderAsync(dispatcher, workerId);

        var accepted = await TransitionAsync(worker, order.Id, "Accept", order.Version);
        var started = await TransitionAsync(worker, order.Id, "Start", accepted.Version);
        using var incompleteSubmit = await SmartParkWebApplicationFactory.SendJsonAsync(worker, HttpMethod.Post, $"/api/operations/work-orders/{order.Id}/transition", new
        {
            action = "Submit",
            version = started.Version,
            text = "Completed without checklist",
            checklistResults = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, incompleteSubmit.StatusCode);

        var submitted = await TransitionAsync(worker, order.Id, "Submit", started.Version, "Initial completion", [new { name = "检查项", result = "已检查" }]);
        Assert.Equal("PendingReview", submitted.Status);
        var rejected = await TransitionAsync(dispatcher, order.Id, "Reject", submitted.Version, "请补充处置说明");
        Assert.Equal("InProgress", rejected.Status);
        var resubmitted = await TransitionAsync(worker, order.Id, "Submit", rejected.Version, "已补充说明", [new { name = "检查项", result = "复查完成" }]);
        var approved = await TransitionAsync(dispatcher, order.Id, "Approve", resubmitted.Version, "验收通过");

        Assert.Equal("Completed", approved.Status);
        Assert.Equal(order.Version + 6, approved.Version);
        var persistence = await factory.InDatabaseAsync(async db => new
        {
            WorkOrder = await db.WorkOrders.SingleAsync(x => x.Id == order.Id),
            Feedbacks = await db.WorkOrderFeedbacks.Where(x => x.WorkOrderId == order.Id).ToListAsync(),
            Logs = await db.WorkOrderLogs.Where(x => x.WorkOrderId == order.Id).ToListAsync(),
            Checklist = await db.WorkOrderChecklistItems.SingleAsync(x => x.WorkOrderId == order.Id),
        });
        Assert.Equal("Completed", persistence.WorkOrder.Status);
        Assert.Equal(2, persistence.Feedbacks.Count);
        Assert.All(persistence.Feedbacks, feedback => Assert.True(feedback.SubmittedAt >= persistence.WorkOrder.CreatedAt));
        Assert.Equal(7, persistence.Logs.Count);
        Assert.All(persistence.Logs, log => Assert.InRange(log.OccurredAt, startedAt, DateTimeOffset.UtcNow));
        Assert.True(persistence.Checklist.Completed);
        Assert.Equal("复查完成", persistence.Checklist.Result);
    }

    [Fact]
    public async Task Related_work_order_drives_event_from_assigned_through_closed_and_queues_completion_outbox()
    {
        var workerId = await WorkerIdAsync("worker");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var worker = await factory.CreateAuthenticatedClientAsync("worker");
        var eventState = await CreateEventAsync(dispatcher);
        var order = await CreateWorkOrderAsync(dispatcher, workerId, eventState.Id);

        var assigned = await factory.InDatabaseAsync(db => db.ParkEvents.SingleAsync(x => x.Id == eventState.Id));
        Assert.Equal("Assigned", assigned.Status);
        Assert.Equal(eventState.Version + 1, assigned.Version);

        var accepted = await TransitionAsync(worker, order.Id, "Accept", order.Version);
        var started = await TransitionAsync(worker, order.Id, "Start", accepted.Version);
        var processing = await factory.InDatabaseAsync(db => db.ParkEvents.SingleAsync(x => x.Id == eventState.Id));
        Assert.Equal("Processing", processing.Status);

        var submitted = await TransitionAsync(worker, order.Id, "Submit", started.Version, "Event work completed", [new { name = "检查项", result = "已处理" }]);
        var approved = await TransitionAsync(dispatcher, order.Id, "Approve", submitted.Version, "Verified");
        Assert.Equal("Completed", approved.Status);
        var pending = await factory.InDatabaseAsync(async db => new
        {
            Event = await db.ParkEvents.SingleAsync(x => x.Id == eventState.Id),
            Actions = await db.EventLogs.Where(x => x.EventId == eventState.Id).Select(x => x.Action).ToListAsync(),
            Outbox = await db.OutboxMessages.SingleAsync(x => x.Kind == "WorkOrderCompleted" && x.PayloadJson.Contains(order.Id.ToString()))
        });
        Assert.Equal("PendingClosure", pending.Event.Status);
        Assert.Contains("WorkOrderAssigned", pending.Actions);
        Assert.Contains("WorkOrderStarted", pending.Actions);
        Assert.Contains("TasksCompleted", pending.Actions);
        using (var payload = JsonDocument.Parse(pending.Outbox.PayloadJson))
        {
            Assert.Equal(order.Id, payload.RootElement.GetProperty("workOrderId").GetGuid());
            Assert.Equal(eventState.Id, payload.RootElement.GetProperty("eventId").GetGuid());
        }

        var closed = await TransitionEventAsync(dispatcher, eventState.Id, "Close", pending.Event.Version, "All related work verified");
        Assert.Equal("Closed", closed.Status);
    }

    [Fact]
    public async Task Work_order_creation_is_rejected_for_closed_and_false_alarm_events()
    {
        var workerId = await WorkerIdAsync("worker");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        var openEvent = await CreateEventAsync(dispatcher);
        var pending = await TransitionEventAsync(dispatcher, openEvent.Id, "RequestClosure", openEvent.Version, "No work is required");
        var closed = await TransitionEventAsync(dispatcher, openEvent.Id, "Close", pending.Version, "Closure approved");
        var falseAlarmEvent = await CreateEventAsync(dispatcher);
        var falseAlarm = await TransitionEventAsync(dispatcher, falseAlarmEvent.Id, "FalseAlarm", falseAlarmEvent.Version, "Duplicate alert");
        Assert.Equal("Closed", closed.Status);
        Assert.Equal("FalseAlarm", falseAlarm.Status);

        using var closedAttempt = await CreateWorkOrderResponseAsync(dispatcher, workerId, openEvent.Id);
        using var falseAlarmAttempt = await CreateWorkOrderResponseAsync(dispatcher, workerId, falseAlarmEvent.Id);
        Assert.Equal(HttpStatusCode.Conflict, closedAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, falseAlarmAttempt.StatusCode);
        var taskCounts = await factory.InDatabaseAsync(async db => new
        {
            Closed = await db.WorkOrders.CountAsync(x => x.EventId == openEvent.Id),
            FalseAlarm = await db.WorkOrders.CountAsync(x => x.EventId == falseAlarmEvent.Id)
        });
        Assert.Equal(0, taskCounts.Closed);
        Assert.Equal(0, taskCounts.FalseAlarm);
    }

    [Fact]
    public async Task Reassignment_immediately_revokes_old_worker_and_stale_version_is_conflict()
    {
        var workerId = await WorkerIdAsync("worker");
        var worker2Id = await WorkerIdAsync("worker2");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var formerWorker = await factory.CreateAuthenticatedClientAsync("worker");
        using var newWorker = await factory.CreateAuthenticatedClientAsync("worker2");
        var order = await CreateWorkOrderAsync(dispatcher, workerId);

        var reassigned = await TransitionAsync(dispatcher, order.Id, "Reassign", order.Version, "调整处理人", assigneeId: worker2Id);
        Assert.Equal("Assigned", reassigned.Status);
        using var formerWorkerAttempt = await SmartParkWebApplicationFactory.SendJsonAsync(formerWorker, HttpMethod.Post, $"/api/operations/work-orders/{order.Id}/transition", new { action = "Accept", version = reassigned.Version });
        Assert.Equal(HttpStatusCode.Forbidden, formerWorkerAttempt.StatusCode);

        var accepted = await TransitionAsync(newWorker, order.Id, "Accept", reassigned.Version);
        using var staleStart = await SmartParkWebApplicationFactory.SendJsonAsync(newWorker, HttpMethod.Post, $"/api/operations/work-orders/{order.Id}/transition", new { action = "Start", version = reassigned.Version });
        Assert.Equal(HttpStatusCode.Conflict, staleStart.StatusCode);
        var started = await TransitionAsync(newWorker, order.Id, "Start", accepted.Version);
        Assert.Equal("InProgress", started.Status);
    }

    [Fact]
    public async Task Dispatcher_cannot_assign_a_work_order_to_a_nonworker_account()
    {
        var visitorId = await WorkerIdAsync("visitor");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, "/api/operations/work-orders", new
        {
            title = $"Invalid assignee {Guid.NewGuid():N}",
            type = "Inspection",
            assigneeId = visitorId,
            detailsJson = "{}",
            checklist = new[] { "检查项" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Worker_cannot_read_other_workers_order_or_its_optional_photo()
    {
        var workerId = await WorkerIdAsync("worker");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var owner = await factory.CreateAuthenticatedClientAsync("worker");
        using var otherWorker = await factory.CreateAuthenticatedClientAsync("worker2");
        var order = await CreateWorkOrderAsync(dispatcher, workerId);
        var accepted = await TransitionAsync(owner, order.Id, "Accept", order.Version);
        var started = await TransitionAsync(owner, order.Id, "Start", accepted.Version);

        using var inaccessibleDetail = await otherWorker.GetAsync($"/api/operations/work-orders/{order.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, inaccessibleDetail.StatusCode);

        using var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j0ioAAAAASUVORK5CYII="));
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "file", "proof.png");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/operations/work-orders/{order.Id}/photos") { Content = form };
        uploadRequest.Headers.Add("X-CSRF-TOKEN", await SmartParkWebApplicationFactory.GetCsrfAsync(owner));
        using var upload = await owner.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var payload = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var fileId = payload.RootElement.GetProperty("id").GetGuid();

        using var inaccessiblePhoto = await otherWorker.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.Forbidden, inaccessiblePhoto.StatusCode);
        Assert.Equal("InProgress", (await factory.InDatabaseAsync(db => db.WorkOrders.Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync())));
    }

    private async Task<Guid> WorkerIdAsync(string userName) => await factory.InDatabaseAsync(db => db.Users.Where(x => x.UserName == userName).Select(x => x.Id).SingleAsync());

    private static async Task<WorkOrderState> TransitionAsync(HttpClient client, Guid id, string action, int version, string? text = null, object[]? checklistResults = null, Guid? assigneeId = null)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(client, HttpMethod.Post, $"/api/operations/work-orders/{id}/transition", new { action, version, text, checklistResults, assigneeId });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new WorkOrderState(document.RootElement.GetProperty("status").GetString()!, document.RootElement.GetProperty("version").GetInt32());
    }

    private static async Task<WorkOrderState> CreateWorkOrderAsync(HttpClient dispatcher, Guid assigneeId, Guid? eventId = null)
    {
        using var response = await CreateWorkOrderResponseAsync(dispatcher, assigneeId, eventId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new WorkOrderState(document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("version").GetInt32());
    }

    private static Task<HttpResponseMessage> CreateWorkOrderResponseAsync(HttpClient dispatcher, Guid assigneeId, Guid? eventId) => SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, "/api/operations/work-orders", new
    {
        title = $"Test work order {Guid.NewGuid():N}",
        type = "Inspection",
        assigneeId,
        eventId,
        priority = "Normal",
        detailsJson = "{\"test\":true}",
        checklist = new[] { "检查项" },
    });

    private static async Task<EventState> CreateEventAsync(HttpClient dispatcher)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, "/api/emergency/events", new { title = $"Work order event {Guid.NewGuid():N}", category = "Inspection", severity = "Warning", description = "Work order lifecycle test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new EventState(document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("version").GetInt32(), document.RootElement.GetProperty("status").GetString()!);
    }

    private static async Task<EventState> TransitionEventAsync(HttpClient client, Guid id, string action, int version, string text)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(client, HttpMethod.Post, $"/api/emergency/events/{id}/transition", new { action, version, text });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new EventState(id, document.RootElement.GetProperty("version").GetInt32(), document.RootElement.GetProperty("status").GetString()!);
    }

    private readonly record struct WorkOrderState(Guid Id, int Version)
    {
        public WorkOrderState(string status, int version) : this(Guid.Empty, version) => Status = status;
        public string Status { get; } = "";
    }

    private readonly record struct EventState(Guid Id, int Version, string Status);
}
