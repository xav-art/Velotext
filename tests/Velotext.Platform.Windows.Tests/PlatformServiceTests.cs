// Velotext: tests for the message-loop thread, process tree, autostart, single instance and key classification.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Win32;
using Velotext.Core.Input;
using Velotext.Platform.Windows.Input;
using Velotext.Platform.Windows.Interop;
using Velotext.Platform.Windows.Lifecycle;
using Velotext.Platform.Windows.Native;
using Velotext.Platform.Windows.Threading;

namespace Velotext.Platform.Windows.Tests;

/// <summary>Windows services that are safe to exercise on any machine (no keyboard or clipboard use).</summary>
public sealed class PlatformServiceTests
{
    [Fact]
    public async Task Message_loop_thread_runs_work_on_its_own_thread_and_reports_errors()
    {
        var errors = new List<Exception>();
        var handled = new TaskCompletionSource<nint>();
        using var loop = new MessageLoopThread(
            "Test loop",
            ThreadPriority.Normal,
            (message, wParam, _) =>
            {
                if (message == NativeMethods.WmApp + 7)
                {
                    handled.TrySetResult(wParam);
                    return 0;
                }

                return null;
            },
            errors.Add);

        var threadId = await loop.InvokeAsync(() => Environment.CurrentManagedThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
        await loop.InvokeAsync(() => Assert.True(loop.IsCurrentThread));

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.InvokeAsync<int>(() => throw new InvalidOperationException()));

        loop.Post(() => throw new FormatException());
        await loop.InvokeAsync(() => { });
        Assert.IsType<FormatException>(Assert.Single(errors));

        NativeMethods.PostMessageW(loop.WindowHandle, NativeMethods.WmApp + 7, 99, 0);
        Assert.Equal(99, await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Message_loop_thread_refuses_work_after_dispose()
    {
        var loop = new MessageLoopThread("Disposed loop", ThreadPriority.Normal, null, _ => { });
        loop.Dispose();
        loop.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => loop.InvokeAsync(() => 1));
        loop.Post(() => { });
    }

    [Fact]
    public void Process_tree_recognises_self_and_children()
    {
        var self = (uint)Environment.ProcessId;
        using var current = Process.GetCurrentProcess();

        Assert.True(ProcessTree.IsSameOrDescendant(self, self));
        Assert.False(ProcessTree.IsSameOrDescendant(0, self));
        Assert.False(ProcessTree.IsSameOrDescendant(self, 0));
        Assert.False(ProcessTree.IsSameOrDescendant(self, uint.MaxValue - 1));

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.True(ProcessTree.IsSameOrDescendant((uint)child.Id, self));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Foreground_target_reports_own_process()
    {
        Assert.True(new ForegroundTarget(1, (uint)Environment.ProcessId, 1).IsOwnProcess);
        Assert.False(new ForegroundTarget(1, 4, 1).IsOwnProcess);
        _ = ForegroundTarget.Current();
    }

    [Fact]
    public void Autostart_turns_on_off_reconciles_and_respects_task_manager()
    {
        var root = @"Software\Velotext.Tests\" + Guid.NewGuid().ToString("N");
        var service = new AutostartService("VelotextTest", root + @"\Run", root + @"\Approved");
        const string Exe = @"C:\Apps\Velotext\Velotext.exe";
        try
        {
            Assert.Equal(AutostartState.Off, service.GetState(Exe));

            service.Set(Exe, enabled: true);
            Assert.Equal(AutostartState.On, service.GetState(Exe));
            Assert.Equal(AutostartState.Off, service.GetState(@"C:\Other\Velotext.exe"));

            using (var approved = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
            {
                approved.SetValue("VelotextTest", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }

            Assert.Equal(AutostartState.DisabledInTaskManager, service.GetState(Exe));

            // Reconcile with a new path keeps the user's Task Manager choice.
            service.Reconcile(@"C:\Apps\Velotext\new\Velotext.exe", wanted: true);
            Assert.Equal(AutostartState.Off, service.GetState(@"C:\Apps\Velotext\new\Velotext.exe"));

            // Turning it on explicitly clears the Task Manager flag.
            service.Set(Exe, enabled: true);
            Assert.Equal(AutostartState.On, service.GetState(Exe));

            service.Reconcile(Exe, wanted: false);
            Assert.Equal(AutostartState.Off, service.GetState(Exe));

            service.Reconcile(Exe, wanted: true);
            Assert.Equal(AutostartState.On, service.GetState(Exe));
            Assert.Equal("\"" + Exe + "\" --autostart", AutostartService.CommandFor(Exe));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Velotext.Tests", throwOnMissingSubKey: false);
        }

        Assert.Throws<ArgumentException>(() => new AutostartService(" "));
        Assert.Throws<ArgumentException>(() => service.Set(" ", enabled: true));
    }

    [Fact]
    public async Task Single_instance_detects_second_start_and_forwards_activation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "velotext-instance-" + Guid.NewGuid().ToString("N"));
        using var first = new SingleInstance(folder);
        var activated = new TaskCompletionSource();
        first.ListenForActivation(() => activated.TrySetResult());

        var otherFolder = Task.Run(() =>
        {
            using var other = new SingleInstance(folder + "-other");
            return other.IsFirstInstance;
        });

        var second = Task.Run(() =>
        {
            using var instance = new SingleInstance(folder);
            if (!instance.IsFirstInstance)
            {
                instance.SignalFirstInstance();
            }

            return instance.IsFirstInstance;
        });

        Assert.True(first.IsFirstInstance);
        Assert.False(await second);
        Assert.True(await otherFolder);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0x41, KeyMeaningKind.Text)]
    [InlineData(0x20, KeyMeaningKind.Text)]
    [InlineData(0x0D, KeyMeaningKind.Text)]
    [InlineData(0x09, KeyMeaningKind.Text)]
    [InlineData(0x25, KeyMeaningKind.Reset)]
    [InlineData(0x24, KeyMeaningKind.Reset)]
    [InlineData(0x2E, KeyMeaningKind.Reset)]
    [InlineData(0x1B, KeyMeaningKind.Reset)]
    [InlineData(0x74, KeyMeaningKind.Reset)]
    [InlineData(0x5D, KeyMeaningKind.Reset)]
    [InlineData(0xAF, KeyMeaningKind.Ignore)]
    [InlineData(0x2C, KeyMeaningKind.Ignore)]
    public void Key_classification(int vk, KeyMeaningKind expected) => Assert.Equal(expected, KeyboardTranslator.Classify(vk));

    [Fact]
    public void Translator_handles_packet_and_modifier_keys()
    {
        var translator = new KeyboardTranslator();

        Assert.Equal("\u00e9", translator.Translate(new KeyStroke(0xE7, 0xE9, KeyStrokeAttributes.None, KeyOrigin.Physical)).Text);
        Assert.Equal(KeyMeaningKind.Modifier, translator.Translate(new KeyStroke(0xA0, 0x2A, KeyStrokeAttributes.None, KeyOrigin.Physical)).Kind);
        Assert.Equal(KeyMeaningKind.Modifier, translator.Translate(new KeyStroke(0x14, 0x3A, KeyStrokeAttributes.None, KeyOrigin.Physical)).Kind);
        Assert.Equal(KeyMeaningKind.Reset, translator.Translate(new KeyStroke(0x25, 0x4B, KeyStrokeAttributes.Extended, KeyOrigin.Physical)).Kind);
    }
}
