using System.Collections.Concurrent;
using ApControl.Core.Control;
using ApControl.Core.Events;

namespace ApControl.Core.Workflows;

/// <summary>A reusable sequence of control steps, e.g. "Launch → Join Conquest → Deploy → Start AP".</summary>
public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<ControlRequest> Steps { get; set; } = new();
}

public enum WorkflowStatus { Running, Completed, CompletedWithFailures, Cancelled }

public sealed record ClientProgress(string ClientId, int CompletedSteps, int? FailedStep, string? FailedStepName, string? Error);

public sealed record WorkflowRunSnapshot(
    string RunId,
    string Workflow,
    WorkflowStatus Status,
    int TotalSteps,
    int CurrentStep,
    string? CurrentStepName,
    int Requested,
    int Ready,
    IReadOnlyList<ClientProgress> Failed,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>"30/32 Ready" style summary line.</summary>
    public string Summary => Status == WorkflowStatus.Running
        ? $"Step {CurrentStep + 1}/{TotalSteps}: {CurrentStepName} — {Requested - Failed.Count}/{Requested} on track"
        : $"{Ready}/{Requested} Ready";
}

/// <summary>
/// Runs workflows over a set of clients. A client that fails a step drops out of the
/// remaining steps; the run ends with an exception summary and supports "Retry failed",
/// which resumes each failed client from the step it failed on.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly AutoplayerController _controller;
    private readonly EventBus _bus;
    private readonly ConcurrentDictionary<string, WorkflowRun> _runs = new();

    public WorkflowRunner(AutoplayerController controller, EventBus bus)
    {
        _controller = controller;
        _bus = bus;
    }

    public IReadOnlyList<WorkflowRunSnapshot> Runs() =>
        _runs.Values.Select(r => r.Snapshot()).OrderByDescending(r => r.StartedAt).ToList();

    public WorkflowRunSnapshot? Get(string runId) => _runs.TryGetValue(runId, out var r) ? r.Snapshot() : null;

    /// <summary>Starts a run in the background and returns immediately with its id.</summary>
    public WorkflowRunSnapshot Start(WorkflowDefinition workflow, IReadOnlyList<string> clientIds, CancellationToken ct = default)
    {
        var run = new WorkflowRun(workflow, clientIds);
        _runs[run.Id] = run;
        _ = Task.Run(() => ExecuteAsync(run, clientIds.Select(id => (id, 0)).ToList(), ct), CancellationToken.None);
        return run.Snapshot();
    }

    /// <summary>Runs to completion (useful for tests and scripted scenarios).</summary>
    public async Task<WorkflowRunSnapshot> RunAsync(WorkflowDefinition workflow, IReadOnlyList<string> clientIds, CancellationToken ct = default)
    {
        var run = new WorkflowRun(workflow, clientIds);
        _runs[run.Id] = run;
        await ExecuteAsync(run, clientIds.Select(id => (id, 0)).ToList(), ct);
        return run.Snapshot();
    }

    /// <summary>Retries only the failed clients of a finished run, each from the step it failed on.</summary>
    public WorkflowRunSnapshot? RetryFailed(string runId, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(runId, out var run)) return null;
        var snap = run.Snapshot();
        if (snap.Status == WorkflowStatus.Running || snap.Failed.Count == 0) return snap;

        var resume = snap.Failed.Select(f => (f.ClientId, f.FailedStep ?? 0)).ToList();
        run.ResetForRetry(resume.Select(r => r.ClientId));
        _ = Task.Run(() => ExecuteAsync(run, resume, ct), CancellationToken.None);
        return run.Snapshot();
    }

    private async Task ExecuteAsync(WorkflowRun run, List<(string ClientId, int FromStep)> clients, CancellationToken ct)
    {
        Publish(run, $"Workflow '{run.Workflow.Name}' started for {clients.Count} client(s)");
        try
        {
            var steps = run.Workflow.Steps;
            var firstStep = clients.Count == 0 ? steps.Count : clients.Min(c => c.FromStep);
            for (var i = firstStep; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Clients join at their resume step and leave once they fail.
                var active = clients.Where(c => c.FromStep <= i && !run.HasFailed(c.ClientId))
                                    .Select(c => c.ClientId).ToList();
                if (active.Count == 0) continue;

                run.SetStep(i, steps[i].ToString());
                Publish(run, $"Step {i + 1}/{steps.Count}: {steps[i]} ({active.Count} clients)");

                var result = await _controller.ExecuteAsync(steps[i], active, ct);
                foreach (var r in result.Results)
                {
                    if (r.Success) run.StepSucceeded(r.ClientId, i);
                    else run.Fail(r.ClientId, i, steps[i].ToString(), r.Message);
                }
            }
            run.Complete(cancelled: false);
        }
        catch (OperationCanceledException)
        {
            run.Complete(cancelled: true);
        }
        catch (Exception ex)
        {
            _bus.Publish("log", null, $"Workflow '{run.Workflow.Name}' crashed: {ex.Message}");
            run.Complete(cancelled: true);
        }
        Publish(run, $"Workflow '{run.Workflow.Name}' finished: {run.Snapshot().Summary}");
    }

    private void Publish(WorkflowRun run, string message) => _bus.Publish("workflow", null, message, run.Snapshot());

    private sealed class WorkflowRun
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ClientProgress> _progress;
        private WorkflowStatus _status = WorkflowStatus.Running;
        private int _currentStep;
        private string? _currentStepName;
        private DateTimeOffset? _completedAt;

        public WorkflowRun(WorkflowDefinition workflow, IReadOnlyList<string> clientIds)
        {
            Workflow = workflow;
            _progress = clientIds.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(id => id, id => new ClientProgress(id, 0, null, null, null), StringComparer.OrdinalIgnoreCase);
        }

        public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
        public WorkflowDefinition Workflow { get; }
        public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

        public bool HasFailed(string id) { lock (_gate) return _progress[id].FailedStep != null; }

        public void SetStep(int i, string name) { lock (_gate) { _currentStep = i; _currentStepName = name; } }

        public void StepSucceeded(string id, int step)
        {
            lock (_gate) _progress[id] = _progress[id] with { CompletedSteps = step + 1 };
        }

        public void Fail(string id, int step, string stepName, string? error)
        {
            lock (_gate) _progress[id] = _progress[id] with { FailedStep = step, FailedStepName = stepName, Error = error };
        }

        public void ResetForRetry(IEnumerable<string> ids)
        {
            lock (_gate)
            {
                foreach (var id in ids)
                    _progress[id] = _progress[id] with { FailedStep = null, FailedStepName = null, Error = null };
                _status = WorkflowStatus.Running;
                _completedAt = null;
            }
        }

        public void Complete(bool cancelled)
        {
            lock (_gate)
            {
                _completedAt = DateTimeOffset.UtcNow;
                _status = cancelled ? WorkflowStatus.Cancelled
                    : _progress.Values.Any(p => p.FailedStep != null) ? WorkflowStatus.CompletedWithFailures
                    : WorkflowStatus.Completed;
            }
        }

        public WorkflowRunSnapshot Snapshot()
        {
            lock (_gate)
            {
                var total = Workflow.Steps.Count;
                var failed = _progress.Values.Where(p => p.FailedStep != null)
                    .OrderBy(p => p.ClientId, StringComparer.OrdinalIgnoreCase).ToList();
                var ready = _progress.Values.Count(p => p.FailedStep == null && p.CompletedSteps >= total);
                return new WorkflowRunSnapshot(Id, Workflow.Name, _status, total, _currentStep, _currentStepName,
                    _progress.Count, ready, failed, StartedAt, _completedAt);
            }
        }
    }
}
