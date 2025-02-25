﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.DotNet.Interactive.App.Connection;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;

namespace Microsoft.DotNet.Interactive.App.Tests
{
    public class StdIoKernelConnectorTests
    {
        private static StdIoKernelConnector CreateConnector()
        {
            var pocketLoggerPath = Environment.GetEnvironmentVariable("POCKETLOGGER_LOG_PATH");
            string loggingArgs = null;

            if (File.Exists(pocketLoggerPath))
            {
                var logDir = Path.GetDirectoryName(pocketLoggerPath);
                loggingArgs = $"--verbose --log-path {logDir}";
            }

            var binPath =
                Path.GetDirectoryName(
                    Path.GetDirectoryName(
                        Path.GetDirectoryName(
                            Path.GetDirectoryName(
                                typeof(StdIoKernelConnector).Assembly.Location))));

            Func<string, bool> predicate =
                filePath =>
                    !string.Equals(
                        Path.GetFileName(Path.GetDirectoryName(filePath)),
                        "publish",
                        StringComparison.OrdinalIgnoreCase);

            var toolAppDllPath =
                Directory.GetFiles(
                    Path.Combine(binPath, "dotnet-interactive"),
                    "Microsoft.DotNet.Interactive.App.dll",
                    SearchOption.AllDirectories)
                        .Single(predicate);

            var hostUri = KernelHost.CreateHostUri("host");
            var connector = new StdIoKernelConnector(
                new[] { "dotnet", $""" "{toolAppDllPath}" stdio {loggingArgs}""" },
                rootProxyKernelLocalName: "rootProxy",
                hostUri);

            return connector;
        }

        [Fact]
        public async Task it_can_return_a_proxy_to_a_remote_composite()
        {
            var connector = CreateConnector();
            using var rootProxyKernel = await connector.CreateRootProxyKernelAsync();

            using var _ = new AssertionScope();
            rootProxyKernel.KernelInfo.IsProxy.Should().BeTrue();
            rootProxyKernel.KernelInfo.IsComposite.Should().BeTrue();
        }

        [Fact]
        public async Task it_can_return_a_proxy_to_a_specific_remote_composite()
        {
            var connector = CreateConnector();
            using var proxyKernel = await connector.CreateProxyKernelAsync(remoteName: ".NET");

            using var _ = new AssertionScope();
            proxyKernel.KernelInfo.IsProxy.Should().BeTrue();
            proxyKernel.KernelInfo.IsComposite.Should().BeTrue();
        }

        [Fact]
        public async Task it_can_create_a_proxy_to_a_specific_remote_subkernel()
        {
            var connector = CreateConnector();
            using var proxyKernel = await connector.CreateProxyKernelAsync(remoteName: "csharp");

            using var _ = new AssertionScope();
            proxyKernel.KernelInfo.IsProxy.Should().BeTrue();
            proxyKernel.KernelInfo.IsComposite.Should().BeFalse();
            proxyKernel.KernelInfo.LanguageName.Should().Be("C#");
            proxyKernel.Name.Should().Be("csharp");
        }

        [Fact]
        public async Task it_can_create_a_proxy_kernel_with_a_different_name_than_the_remote()
        {
            var connector = CreateConnector();
            using var proxyKernel = await connector.CreateProxyKernelAsync(remoteName: "fsharp", localNameOverride: "fsharp2");

            using var _ = new AssertionScope();
            proxyKernel.KernelInfo.IsProxy.Should().BeTrue();
            proxyKernel.KernelInfo.IsComposite.Should().BeFalse();
            proxyKernel.KernelInfo.LanguageName.Should().Be("F#");
            proxyKernel.Name.Should().Be("fsharp2");
        }

        [Fact]
        public async Task it_can_create_a_proxy_kernel_to_more_than_one_remote_subkernel()
        {
            var connector = CreateConnector();
            using var rootProxyKernel = await connector.CreateRootProxyKernelAsync();

            var result = await rootProxyKernel.SendAsync(new RequestKernelInfo());
            var kernelInfos = result.Events.OfType<KernelInfoProduced>().Select(e => e.KernelInfo);

            using var _ = new AssertionScope();

            var csharpKernelInfo = kernelInfos.Should().ContainSingle(i => i.LanguageName == "C#").Which;
            using var csharpProxyKernel = await connector.CreateProxyKernelAsync(remoteInfo: csharpKernelInfo);
            var expectedCSharpKernelInfo = new KernelInfo(csharpKernelInfo.LocalName)
            {
                IsProxy = true,
                IsComposite = false,
                LanguageName = csharpKernelInfo.LanguageName,
                LanguageVersion = csharpKernelInfo.LanguageVersion,
                RemoteUri = csharpKernelInfo.Uri,
                SupportedDirectives = csharpKernelInfo.SupportedDirectives,
                SupportedKernelCommands = csharpKernelInfo.SupportedKernelCommands
            };

            var fsharpKernelInfo = kernelInfos.Should().ContainSingle(i => i.LanguageName == "F#").Which;
            using var fsharpProxyKernel = await connector.CreateProxyKernelAsync(remoteInfo: fsharpKernelInfo, localNameOverride: "fsharp2");
            var expectedFSharpKernelInfo = new KernelInfo("fsharp2")
            {
                IsProxy = true,
                IsComposite = false,
                LanguageName = fsharpKernelInfo.LanguageName,
                LanguageVersion = fsharpKernelInfo.LanguageVersion,
                RemoteUri = fsharpKernelInfo.Uri,
                SupportedDirectives = fsharpKernelInfo.SupportedDirectives,
                SupportedKernelCommands = fsharpKernelInfo.SupportedKernelCommands
            };

            csharpProxyKernel.Name.Should().Be(csharpKernelInfo.LocalName);
            csharpProxyKernel.KernelInfo.Should().BeEquivalentTo(expectedCSharpKernelInfo);

            fsharpProxyKernel.Name.Should().Be("fsharp2");
            fsharpProxyKernel.KernelInfo.Should().BeEquivalentTo(expectedFSharpKernelInfo);
        }

        [Fact]
        public async Task it_throws_if_there_is_no_remote_subkernel_with_the_specified_name()
        {
            var connector = CreateConnector();
            var action = async () => await connector.CreateProxyKernelAsync(remoteName: "non-existent");
            await action.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task when_only_root_proxy_is_created_and_disposed_then_the_remote_process_is_killed()
        {
            var connector = CreateConnector();
            var rootProxyKernel = await connector.CreateRootProxyKernelAsync();

            using var _ = new AssertionScope();

            var processId = connector.ProcessId;
            processId.Should().NotBeNull();
            var process = Process.GetProcessById(processId.Value);
            HasProcessExited(process).Should().BeFalse();

            rootProxyKernel.Dispose();
            HasProcessExited(process).Should().BeTrue();
        }

        [Fact]
        public async Task when_all_created_proxies_have_been_disposed_then_the_remote_process_is_killed()
        {
            var connector = CreateConnector();
            var pwshProxyKernel = await connector.CreateProxyKernelAsync("pwsh");
            var csharpProxyKernel = await connector.CreateProxyKernelAsync("csharp");
            var fsharpProxyKernel = await connector.CreateProxyKernelAsync("fsharp");

            using var _ = new AssertionScope();

            var processId = connector.ProcessId;
            processId.Should().NotBeNull();
            var process = Process.GetProcessById(processId.Value);
            HasProcessExited(process).Should().BeFalse();

            pwshProxyKernel.Dispose();
            HasProcessExited(process).Should().BeFalse();

            csharpProxyKernel.Dispose();
            HasProcessExited(process).Should().BeFalse();

            fsharpProxyKernel.Dispose();
            HasProcessExited(process).Should().BeTrue();
        }

        [Fact]
        public async Task when_all_created_proxies_including_the_root_proxy_have_been_disposed_then_the_remote_process_is_killed()
        {
            var connector = CreateConnector();
            var rootProxyKernel = await connector.CreateRootProxyKernelAsync();
            var csharpProxyKernel = await connector.CreateProxyKernelAsync("csharp");
            var fsharpProxyKernel = await connector.CreateProxyKernelAsync("fsharp");

            using var _ = new AssertionScope();

            var processId = connector.ProcessId;
            processId.Should().NotBeNull();
            var process = Process.GetProcessById(processId.Value);
            HasProcessExited(process).Should().BeFalse();

            csharpProxyKernel.Dispose();
            HasProcessExited(process).Should().BeFalse();

            rootProxyKernel.Dispose();
            HasProcessExited(process).Should().BeFalse();

            fsharpProxyKernel.Dispose();
            HasProcessExited(process).Should().BeTrue();
        }

        [Fact]
        public async Task encoding_is_preserved()
        {
            var connector = CreateConnector();
            using var rootProxyKernel = await connector.CreateRootProxyKernelAsync();
            using var csharpProxyKernel = await connector.CreateProxyKernelAsync("csharp");

            var result = await csharpProxyKernel.SendAsync(new SubmitCode("""var x = "abáéíőúűóüÁÉÍŐÚŰÓÜ"; x"""));

            result.Events.OfType<ReturnValueProduced>().Should().ContainSingle()
                .Which.FormattedValues.Should().ContainSingle()
                .Which.Value.Should().Be("abáéíőúűóüÁÉÍŐÚŰÓÜ");
        }

        private static bool HasProcessExited(Process process)
        {
            if (process.HasExited)
            {
                return true;
            }
            else
            {
                /// Since <see cref="Process.Kill"/> executes asynchronously, we may encounter a race where the process
                /// has not exited yet. So we try once more after a short delay.
                process.WaitForExit(500);
                return process.HasExited;
            }
        }
    }
}
