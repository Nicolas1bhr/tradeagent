using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace TradeAgent.Tests;

/// <summary>
/// THE END OF A TEST ASSEMBLY'S RUN, WHERE ITS HOME IS DELETED (<see cref="TestEnv"/>): xunit's own
/// framework with one step added — after the last test has finished, before the run is reported
/// finished. Registered for every test project in <c>tests/Directory.Build.props</c>.
///
/// <para><b>Not process exit alone, which is where it was first put.</b> Once a test host's results
/// are in, the test platform asks it to stop and kills it about 100 ms later (VSTest's
/// <c>VSTEST_TESTHOST_SHUTDOWN_TIMEOUT</c>): an exit handler that slept two seconds was measured never
/// finishing, and deleting one full Unit home took 136 ms on the dev Mac. Here nothing is waiting to
/// kill the host yet.</para>
///
/// <para><b>The step never throws.</b> An exception from it would be reported as an assembly cleanup
/// failure, and a run that went red over its own scratch directory is exactly the kind of red this
/// harness is not allowed to produce.</para>
/// </summary>
public sealed class TestHomeFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider,
        IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        /// <summary>
        /// xunit 2.9.3's own body — its state machine calls exactly these members, read off its IL —
        /// with <see cref="Runner"/> in place of <see cref="XunitTestAssemblyRunner"/>.
        /// </summary>
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases,
            IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink,
                executionOptions);
            await runner.RunAsync();
        }
    }

    sealed class Runner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink,
            executionOptions)
    {
        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await base.BeforeTestAssemblyFinishedAsync();
            TestEnv.DeleteHome();
        }
    }
}
