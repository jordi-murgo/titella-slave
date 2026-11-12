// In-process PowerShell engine host for one-shot jobs.
//
// A job is the agent executable itself started with --job and assigned to a
// job object (so cancellation and timeouts keep killing the whole process
// tree), but the user script never touches the child's command line. The
// command line only says --job; the script itself arrives on stdin and
// executes in a custom PSHost, which keeps the script text out of every
// classic PowerShell log channel (Id 400/800 HostApplication, 4103/4104
// script text, PSReadLine history).

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Management.Automation;
    using System.Management.Automation.Host;
    using System.Management.Automation.Runspaces;
    using System.Security;
    using System.Text;

    internal sealed class JobHostUserInterface : PSHostUserInterface
    {
        private readonly Action<string> _stdout;
        private readonly Action<string> _stderr;
        internal bool SawError;

        public JobHostUserInterface(Action<string> stdout, Action<string> stderr)
        {
            _stdout = stdout;
            _stderr = stderr;
        }

        public override void Write(string value)
        {
            _stdout(value);
        }

        public override void WriteLine()
        {
            _stdout(string.Empty);
        }

        public override void WriteLine(string value)
        {
            _stdout(value);
        }

        public override void Write(ConsoleColor foreground, ConsoleColor background, string value)
        {
            _stdout(value);
        }

        public override void WriteLine(ConsoleColor foreground, ConsoleColor background, string value)
        {
            _stdout(value);
        }

        public override void WriteErrorLine(string value)
        {
            SawError = true;
            _stderr(value);
        }

        public override void WriteDebugLine(string value) { }

        public override void WriteVerboseLine(string value) { }

        public override void WriteWarningLine(string value)
        {
            _stderr("WARNING: " + value);
        }

        public override void WriteProgress(long sourceId, ProgressRecord record) { }

        public override string ReadLine()
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; Read-Host is not available");
        }

        public override SecureString ReadLineAsSecureString()
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; Read-Host is not available");
        }

        public override Dictionary<string, PSObject> Prompt(
            string caption, string message,
            Collection<FieldDescription> descriptions)
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; Prompt is not available");
        }

        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName)
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; credential prompts are not available");
        }

        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedTypes, PSCredentialUIOptions options)
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; credential prompts are not available");
        }

        public override int PromptForChoice(
            string caption, string message,
            Collection<ChoiceDescription> choices, int defaultChoice)
        {
            throw new PSInvalidOperationException(
                "this host is non-interactive; choice prompts are not available");
        }

        public override PSHostRawUserInterface RawUI
        {
            get { return null; }
        }
    }

    internal sealed class JobHost : PSHost
    {
        private readonly JobHostUserInterface _ui;
        private readonly Guid _instanceId = Guid.NewGuid();
        private readonly Action<int> _setExit;

        public JobHost(Action<string> stdout, Action<string> stderr, Action<int> setExit)
        {
            _ui = new JobHostUserInterface(stdout, stderr);
            _setExit = setExit;
        }

        internal bool SawError
        {
            get { return _ui.SawError; }
        }

        public override CultureInfo CurrentCulture
        {
            get { return CultureInfo.InvariantCulture; }
        }

        public override CultureInfo CurrentUICulture
        {
            get { return CultureInfo.InvariantCulture; }
        }

        public override Guid InstanceId
        {
            get { return _instanceId; }
        }

        public override string Name
        {
            get { return "Default Host"; }
        }

        public override PSHostUserInterface UI
        {
            get { return _ui; }
        }

        public override Version Version
        {
            get { return new Version(5, 1); }
        }

        public override void EnterNestedPrompt()
        {
            throw new PSNotSupportedException();
        }

        public override void ExitNestedPrompt()
        {
            throw new PSNotSupportedException();
        }

        public override void NotifyBeginApplication() { }

        public override void NotifyEndApplication() { }

        public override void SetShouldExit(int exitCode)
        {
            _setExit(exitCode);
        }
    }

    /// <summary>
    /// One script executed in one in-process Runspace with a custom host.
    /// Mirrors the semantics of the previous powershell.exe -EncodedCommand
    /// child: UTF-8 output encoding is forced, stdout and stderr are
    /// separated (Write-Host and pipeline output go to stdout; host error
    /// writes and cmdlet error records go to stderr), the exit code is the
    /// argument of exit (or 0/1 depending on success), and native commands
    /// inherit redirected stdout/stderr so they stay separated too.
    /// </summary>
    internal sealed class JobEngine
    {
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly StringBuilder _stderr = new StringBuilder();
        private int _exitCode;
        private bool _exitCalled;

        public static int Run(string script, TextWriter stdout, TextWriter stderr)
        {
            JobEngine engine = new JobEngine();
            return engine.RunCore(script, stdout, stderr);
        }

        private int RunCore(string script, TextWriter stdout, TextWriter stderr)
        {
            JobHost host = new JobHost(
                delegate(string line)
                {
                    lock (_stdout)
                    {
                        _stdout.Append(line);
                        _stdout.Append('\n');
                    }
                },
                delegate(string line)
                {
                    lock (_stderr)
                    {
                        _stderr.Append(line);
                        _stderr.Append('\n');
                    }
                },
                delegate(int code)
                {
                    _exitCode = code;
                    _exitCalled = true;
                });
            Runspace runspace = null;
            try
            {
                runspace = RunspaceFactory.CreateRunspace(host);
                runspace.Open();
                Pipeline pipeline = runspace.CreatePipeline();
                pipeline.Commands.AddScript(script);
                pipeline.Commands.Add("Out-Default");
                try
                {
                    pipeline.Invoke();
                }
                catch (RuntimeException)
                {
                    // Terminating errors are already reported through the
                    // host error stream by the engine; the job must finish
                    // with a non-zero exit code like powershell.exe would.
                    _exitCode = 1;
                    return Finish(stdout, stderr);
                }
                finally
                {
                    FlushBuffers(stdout, stderr);
                }
                if (_exitCalled)
                {
                    return Finish(stdout, stderr);
                }
                // Non-terminating cmdlet errors are rendered to the host
                // error stream by Out-Default; powershell.exe -Command
                // exits 1 when any error was written, and the runner
                // keeps that contract.
                _exitCode = host.SawError ? 1 : 0;
                return Finish(stdout, stderr);
            }
            finally
            {
                if (runspace != null)
                {
                    try
                    {
                        runspace.Close();
                    }
                    catch (Exception)
                    {
                        // Closing after a failed pipeline is best effort.
                    }
                }
            }
        }

        private int Finish(TextWriter stdout, TextWriter stderr)
        {
            FlushBuffers(stdout, stderr);
            return _exitCode;
        }

        private void FlushBuffers(TextWriter stdout, TextWriter stderr)
        {
            string outText;
            string errText;
            lock (_stdout)
            {
                outText = _stdout.ToString();
                _stdout.Length = 0;
            }
            lock (_stderr)
            {
                errText = _stderr.ToString();
                _stderr.Length = 0;
            }
            if (outText.Length > 0)
            {
                stdout.Write(outText);
            }
            if (errText.Length > 0)
            {
                stderr.Write(errText);
            }
        }
    }

    /// <summary>
    /// Child-process entry point (--job): read the script from stdin (the
    /// parent closes its end after writing, which is the EOF signal) and
    /// execute it in the in-process Runspace, mapping the exit code to
    /// the process exit code exactly like powershell.exe -Command does.
    /// </summary>
    internal static class JobEntry
    {
        public static int Run()
        {
            try
            {
                // The job child has redirected pipes and no console: the
                // Win32 console APIs behind Console.OutputEncoding fail with
                // UnauthorizedAccessException on a console-less process, so
                // UTF-8 is enforced by the writers alone.
                string script = Console.In.ReadToEnd();
                Encoding utf8 = new UTF8Encoding(false);
                using (StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), utf8))
                using (StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), utf8))
                {
                    stdout.AutoFlush = true;
                    stderr.AutoFlush = true;
                    return JobEngine.Run(script, stdout, stderr);
                }
            }
            catch (Exception e)
            {
                try
                {
                    Console.Error.WriteLine("jobentry_failed: " + e.GetType().Name
                                            + ": " + e.Message);
                }
                catch (Exception)
                {
                    // Nothing else can be reported from here.
                }
                return 4;
            }
        }
    }
}
