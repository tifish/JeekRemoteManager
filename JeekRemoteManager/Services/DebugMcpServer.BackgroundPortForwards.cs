using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JeekRemoteManager.Views;
using System.Threading.Tasks;

namespace JeekRemoteManager.Services;

internal static partial class DebugMcpServer
{
    private sealed class ForwardListenerFixture : BackgroundPortForwards.ITransport
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public bool IsReady { get; private set; } = true;
        public int LocalPort { get; }
        public ForwardListenerFixture()
        {
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        public void Dispose() { IsReady = false; _listener.Stop(); }
    }

    private static async Task<JsonObject> BackgroundPortForwardManagerCheckAsync()
    {
        if (!DebugInstanceContext.IsDebugBuild)
            return ToolText("Run this probe in a Debug instance.", true);

        using var forwards = new BackgroundPortForwards(startTimer: false);
        var owner = Guid.NewGuid().ToString();
        var target = new BackgroundPortForwards.Target("fixture-shared", "fixture.invalid", 28400, "fixture", "Test/SSH");
        var choice = new BackgroundPortForwardWindow.ConnectionChoice(target.ConnectionId, target.TreePath, target.Host);
        var checks = new Dictionary<string, bool>();
        var state = await OnUiAsync(() =>
        {
            var main = (MainWindow)Desktop!.MainWindow!;
            main.ActivateMainWindow();
            var actions = main.FindControl<Button>("ConnectionPanelActionsButton");
            var flyout = (MenuFlyout)actions!.Flyout!;
            var entry = flyout.Items.OfType<MenuItem>().Single(item => item.Name == "BackgroundPortForwardMenuItem");
            var alreadyOpen = main.OwnedWindows.OfType<BackgroundPortForwardWindow>().SingleOrDefault();
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var manager = main.OwnedWindows.OfType<BackgroundPortForwardWindow>().Single();
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            checks["connectionMenuOpensOneManager"] = manager.IsVisible
                && main.OwnedWindows.OfType<BackgroundPortForwardWindow>().Single() == manager;
            if (alreadyOpen is null)
                manager.Close();

            var fixture = new BackgroundPortForwardWindow(forwards, () => [choice],
                (_, _, port) => new(target with { RemotePort = port }, (_, _) => new ForwardListenerFixture()));
            fixture.Show(main);
            return (Main: main, Fixture: fixture, Sessions: main.EnumerateTerminalSessions().Count());
        });

        T Find<T>(string name) where T : Control => state.Fixture.GetVisualDescendants().OfType<T>()
            .Single(control => control.Name == name);
        Button Button(string name) => Find<Button>("BackgroundForward" + name);
        ListBox List() => Find<ListBox>("BackgroundForwardList");
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        async Task<bool> UntilAsync(Func<bool> matches)
        {
            var deadline = Environment.TickCount64 + 4_000;
            do
            {
                if (await OnUiAsync(matches))
                    return true;
                await Task.Delay(50);
            } while (Environment.TickCount64 < deadline);
            return false;
        }

        try
        {
            checks["emptyDisablesRowActions"] = await OnUiAsync(() =>
                List().ItemCount == 0 && !Button("Resume").IsEnabled && !Button("Stop").IsEnabled);
            var leased = await forwards.EnsureAsync(owner, target, (_, _) => new ForwardListenerFixture());
            var leasedId = leased["forward_id"]!.GetValue<string>();
            checks["mcpForwardAppearsAutomatically"] = await UntilAsync(() => List().Items
                .OfType<BackgroundPortForwards.Overview>().Any(row => row.Id == leasedId && row.LocalPort > 0));
            checks["invalidPortRejected"] = await OnUiAsync(() =>
            {
                Find<TextBox>("BackgroundForwardRemotePort").Text = "655350";
                Click("Open");
                return Find<TextBlock>("BackgroundForwardMessage").IsVisible && List().ItemCount == 1;
            });
            await OnUiAsync(() =>
            {
                Find<TextBox>("BackgroundForwardRemotePort").Text = "28401";
                Click("Open");
                return true;
            });
            checks["manualForwardCanOpen"] = await UntilAsync(() => List().SelectedItem
                is BackgroundPortForwards.Overview { IsManual: true, State: "ready", LocalPort: > 0 }
                && Button("Stop").IsEnabled && Button("Copy").IsEnabled);
            var localPort = await OnUiAsync(() =>
            {
                var row = List().SelectedItem as BackgroundPortForwards.Overview;
                Click("Stop");
                return row?.LocalPort ?? 0;
            });
            checks["manualStopReleasesListener"] = await UntilAsync(() => List().SelectedItem
                is BackgroundPortForwards.Overview { IsManual: true, State: "closed", LocalPort: 0 }
                && Button("Resume").IsEnabled && !Button("Copy").IsEnabled) && localPort > 0 && ListenerReleased(localPort);
            await OnUiAsync(() => { Click("Resume"); return true; });
            checks["manualForwardCanReopen"] = await UntilAsync(() => List().SelectedItem
                is BackgroundPortForwards.Overview { IsManual: true, State: "ready", LocalPort: > 0 });

            await OnUiAsync(() =>
            {
                List().SelectedItem = List().Items.OfType<BackgroundPortForwards.Overview>().Single(row => row.Id == leasedId);
                Click("Stop");
                return true;
            });
            checks["mcpForwardCanStop"] = await UntilAsync(() => List().SelectedItem
                is BackgroundPortForwards.Overview { IsManual: false, Error: "stopped_by_user" });
            var ensured = await forwards.EnsureAsync(owner, target, (_, _) => new ForwardListenerFixture());
            checks["mcpEnsureHonorsManualStop"] = ensured["state"]!.GetValue<string>() == "closed";
            await OnUiAsync(() => { Click("Resume"); return true; });
            checks["mcpForwardCanReopen"] = await UntilAsync(() => List().SelectedItem
                is BackgroundPortForwards.Overview { IsManual: false, State: "ready", LocalPort: > 0 });
        }
        finally
        {
            checks["noTerminalTabsOpened"] = await OnUiAsync(() =>
            {
                state.Fixture.Close();
                return state.Main.EnumerateTerminalSessions().Count() == state.Sessions;
            });
        }
        checks["closingManagerKeepsListener"] = forwards.List().Any(row => row.IsManual && row.State == "ready");
        var passed = checks.Values.All(value => value);
        return ToolText(new JsonObject
        {
            ["passed"] = passed,
            ["checks"] = new JsonObject(checks.Select(check => KeyValuePair.Create(check.Key, (JsonNode?)JsonValue.Create(check.Value)))),
        }.ToJsonString(), !passed);
    }

    private static bool ListenerReleased(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static async Task<JsonObject> BackgroundPortForwardCheckAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using var forwards = new BackgroundPortForwards(() => now, startTimer: false);
        var owner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var target = new BackgroundPortForwards.Target("fixture-pp", "fixture.invalid", 28190, "pp");
        var opens = 0;
        ForwardListenerFixture? first = null;
        BackgroundPortForwards.ITransport Dial()
        {
            opens++;
            return new ForwardListenerFixture();
        }
        string Id(JsonObject r) => r["forward_id"]!.GetValue<string>();
        string State(JsonObject r) => r["state"]!.GetValue<string>();
        var a = await forwards.EnsureAsync(owner, target, (_, _) => first = (ForwardListenerFixture)Dial());
        var repeat = await forwards.EnsureAsync(owner, target, (_, _) => Dial());
        var b = await forwards.EnsureAsync(other, target, (_, _) => Dial());
        var c = await forwards.EnsureAsync(owner, target with { ConnectionId = "fixture-prod", RouteIdentity = "prod" }, (_, _) => Dial());
        var isolated = Id(a) == Id(repeat) && Id(a) != Id(b) && Id(a) != Id(c) && opens == 3;
        var unauthorized = false;
        try { forwards.Read(Id(a), other, close: true); }
        catch (InvalidOperationException) { unauthorized = true; }
        var port = first!.LocalPort;
        var conflict = false;
        try { using var blocked = new TcpListener(IPAddress.Loopback, port); blocked.Start(); }
        catch (SocketException) { conflict = true; }
        first.Dispose();
        var disconnected = State(forwards.Read(Id(a), owner)) == "failed";
        var recovered = await forwards.EnsureAsync(owner, target, (_, _) => Dial());
        var closed = forwards.Read(Id(a), owner, close: true);
        var ownClosed = State(closed) == "closed" && ListenerReleased(closed["local_port"]!.GetValue<int>())
            && State(forwards.Read(Id(b), other)) == "ready";
        now += TimeSpan.FromSeconds(121);
        forwards.Sweep();
        var expired = State(forwards.Read(Id(b), other)) == "closed"
            && ListenerReleased(b["local_port"]!.GetValue<int>());
        var passed = isolated && unauthorized && conflict && disconnected && ownClosed && expired
            && State(recovered) == "ready" && a["local_host"]!.GetValue<string>() == "127.0.0.1";
        return ToolText(new JsonObject
        {
            ["passed"] = passed, ["fixture_only"] = true,
            ["idempotent_and_isolated"] = isolated, ["owner_checked"] = unauthorized,
            ["port_conflict_checked"] = conflict, ["disconnect_detected"] = disconnected,
            ["close_released_only_owner"] = ownClosed, ["expired_released"] = expired,
        }.ToJsonString(), isError: !passed);
    }
}
