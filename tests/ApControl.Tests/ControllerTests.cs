using ApControl.Core.Control;
using ApControl.Core.Events;
using ApControl.Core.Models;
using ApControl.Core.Monitoring;
using ApControl.Core.Registry;
using ApControl.Core.Workflows;
using ApControl.Drivers;
using ApControl.Drivers.Builds;
using ApControl.Drivers.Cli;
using ApControl.Drivers.Simulation;

namespace ApControl.Tests;

public class ControllerTests
{
    private sealed class Rig
    {
        public Rig(int clients = 8)
        {
            Bus = new EventBus();
            Fleet = new SimulatedFleet(new SimulationOptions { DelayScale = 0, FailureRate = 0, CrashRate = 0, Seed = 1 });
            Registry = new ClientRegistry(SimulatedFleet.GenerateInventory(clients), Bus);
            var drivers = DriverResolver.Simulated(Fleet);
            var builds = new StaticBuildProvider(new[]
            {
                new StaticBuild
                {
                    Id = "qv-latest", Version = "CL42", Branch = "QV",
                    Packages = new() { [Platform.Xbox] = @"\\x\game.xvc", [Platform.PS5] = @"\\p\game.pkg" }
                }
            });
            var options = new ControlOptions { StatusPollSeconds = 0.01, BootTimeoutSeconds = 5, MatchmakingTimeoutSeconds = 5, StateTimeoutSeconds = 5 };
            Controller = new AutoplayerController(Registry, drivers, builds, Bus, options,
                new Dictionary<Platform, PlatformLaunchOptions>(),
                new[] { new ExperienceDefinition { Id = "conquest", Name = "Conquest" } });
            Workflows = new WorkflowRunner(Controller, Bus);
            Monitor = new StatusMonitor(Registry, drivers, Controller, options);
        }

        public EventBus Bus { get; }
        public SimulatedFleet Fleet { get; }
        public ClientRegistry Registry { get; }
        public AutoplayerController Controller { get; }
        public WorkflowRunner Workflows { get; }
        public StatusMonitor Monitor { get; }
        public IReadOnlyList<string> All => Registry.Resolve(new ClientSelector(All: true));
    }

    private static WorkflowDefinition PrepareConquest => new()
    {
        Name = "Prepare Conquest",
        Steps =
        {
            new ControlRequest { Action = ControlAction.Launch },
            new ControlRequest { Action = ControlAction.Join, ExperienceId = "conquest" },
            new ControlRequest { Action = ControlAction.Deploy },
            new ControlRequest { Action = ControlAction.StartAutoplayer, Profile = "Aggressive" }
        }
    };

    [Fact]
    public async Task Full_stage1_workflow_gets_every_client_autoplaying()
    {
        var rig = new Rig(32);
        var run = await rig.Workflows.RunAsync(PrepareConquest, rig.All);

        Assert.Equal(WorkflowStatus.Completed, run.Status);
        Assert.Equal(32, run.Ready);
        Assert.All(rig.Registry.Snapshot(), c =>
        {
            Assert.Equal(ApState.Autoplaying, c.State);
            Assert.Equal("conquest", c.Experience);
            Assert.Equal("Aggressive", c.Profile);
        });
    }

    [Fact]
    public async Task Failed_client_drops_out_and_retry_resumes_from_failed_step()
    {
        var rig = new Rig(4);
        await rig.Controller.ExecuteAsync(new ControlRequest { Action = ControlAction.Launch }, rig.All);
        rig.Fleet.FailNext("AP-002"); // its join will fail

        var wf = new WorkflowDefinition
        {
            Name = "join+deploy",
            Steps =
            {
                new ControlRequest { Action = ControlAction.Join, ExperienceId = "conquest" },
                new ControlRequest { Action = ControlAction.Deploy }
            }
        };
        var run = await rig.Workflows.RunAsync(wf, rig.All);

        Assert.Equal(WorkflowStatus.CompletedWithFailures, run.Status);
        Assert.Equal("3/4 Ready", run.Summary);
        var failure = Assert.Single(run.Failed);
        Assert.Equal("AP-002", failure.ClientId);
        Assert.Equal(0, failure.FailedStep);
        Assert.Equal(ApState.Error, rig.Registry.StateOf("AP-002"));

        // Error state from a failed join still has a running game in FE, so retry can re-join.
        rig.Workflows.RetryFailed(run.RunId);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        WorkflowRunSnapshot? after;
        do { await Task.Delay(20); after = rig.Workflows.Get(run.RunId); }
        while (after!.Status == WorkflowStatus.Running && DateTime.UtcNow < deadline);

        Assert.Equal(WorkflowStatus.Completed, after.Status);
        Assert.Equal(4, after.Ready);
        Assert.Equal(ApState.Deployed, rig.Registry.StateOf("AP-002"));
    }

    [Fact]
    public async Task Game_commands_fail_fast_when_game_not_running()
    {
        var rig = new Rig(2);
        await rig.Monitor.PollOnceAsync(CancellationToken.None); // Unknown -> Online
        var result = await rig.Controller.ExecuteAsync(new ControlRequest { Action = ControlAction.Join, ExperienceId = "conquest" }, rig.All);

        Assert.Equal(0, result.Succeeded);
        Assert.All(result.Results, r => Assert.Contains("not running", r.Message));
    }

    [Fact]
    public async Task Install_resolves_build_per_platform_and_records_version()
    {
        var rig = new Rig(2);
        var result = await rig.Controller.ExecuteAsync(new ControlRequest { Action = ControlAction.InstallBuild, BuildId = "qv-latest" }, rig.All);

        Assert.Equal(2, result.Succeeded);
        Assert.All(rig.Registry.Snapshot(), c => Assert.Equal("CL42", c.BuildVersion));
    }

    [Fact]
    public async Task Monitor_flags_lost_game_as_error()
    {
        var rig = new Rig(1);
        var id = rig.All[0];
        await rig.Workflows.RunAsync(PrepareConquest, rig.All);
        await rig.Fleet.TerminateAsync(rig.Registry.Definition(id), CancellationToken.None); // simulate crash behind our back

        await rig.Monitor.PollOnceAsync(CancellationToken.None);

        var snap = rig.Registry.Get(id)!;
        Assert.Equal(ApState.Error, snap.State);
        Assert.Contains("stopped responding", snap.LastMessage);
    }

    [Fact]
    public void Selector_unions_ids_groups_and_ignores_unknown()
    {
        var rig = new Rig(8); // Bank A = 1..4, Bank B = 5..8
        var ids = rig.Registry.Resolve(new ClientSelector(new[] { "AP-005", "nope" }, new[] { "Bank A" }));
        Assert.Equal(new[] { "AP-001", "AP-002", "AP-003", "AP-004", "AP-005" }, ids);

        rig.Registry.SetGroup("Attackers", new[] { "AP-007", "AP-008" });
        Assert.Equal(2, rig.Registry.Resolve(new ClientSelector(Groups: new[] { "attackers" })).Count);
    }

    [Fact]
    public void Cli_templates_expand_placeholders()
    {
        var s = CliPlatformDriver.Expand("launch /x:{address} {appId} {args}",
            new Dictionary<string, string> { ["address"] = "10.0.0.5", ["appId"] = "Game!App", ["args"] = "-autoplayer" });
        Assert.Equal("launch /x:10.0.0.5 Game!App -autoplayer", s);
    }
}
