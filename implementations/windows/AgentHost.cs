// Process entry point of the compiled Windows payload. The bootstrap loader
// compiles the concatenated source either to agent.exe, which starts
// directly, or to agent.dll, which a separate Windows PowerShell 5.1 process
// loads from disk. With no arguments the host reads agent.json (written by
// the loader next to the assembly) and runs the agent; with --job it runs one
// piped job. The assembly kind decides which implementation may run: an exe
// requires windows-dotnet, a hosted library requires windows-dotnet-dll, and
// anything else is rejected before the transport is opened.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;

    public static class AgentHost
    {
        private const string JobArgument = "--job";
        private const string ConfigName = "agent.json";
        internal const string ExeImplementation = "windows-dotnet";
        internal const string DllImplementation = "windows-dotnet-dll";

        public static int Main(string[] args)
        {
            if (args != null && args.Length == 1 && args[0] == JobArgument)
            {
                return JobEntry.Run();
            }
            if (args != null && args.Length == 0)
            {
                return RunAgent();
            }
            Console.Error.WriteLine(
                "titella agent: usage: agent (agent) | agent --job (one job)");
            return 2;
        }

        private static int RunAgent()
        {
            string location;
            string directory;
            try
            {
                location = Assembly.GetExecutingAssembly().Location;
                directory = string.IsNullOrEmpty(location)
                    ? null : Path.GetDirectoryName(location);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("titella agent: " + error.Message);
                return 2;
            }
            if (string.IsNullOrEmpty(directory))
            {
                Console.Error.WriteLine("titella agent: the executable has no location");
                return 2;
            }
            string configPath = Path.Combine(directory, ConfigName);
            string text;
            try
            {
                text = File.ReadAllText(configPath);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "titella agent: agent.json is unreadable: " + error.Message);
                return 2;
            }
            // agent.json carries exactly the arguments the loader passes to
            // Agent.Run: {"bootstrap_dir":..., "link_id":..., "port":...,
            // "heartbeat_ms":5000, "grace_ms":30000} and, for the hosted DLL,
            // {"implementation":"windows-dotnet-dll"}. A config missing a
            // required field is invalid, never defaulted.
            Dictionary<string, object> config;
            string bootstrapDirectory;
            string linkId;
            string port;
            long heartbeat;
            long grace;
            bool library = string.Equals(
                Path.GetExtension(location), ".dll", StringComparison.OrdinalIgnoreCase);
            string implementation;
            try
            {
                config = Json.Parse(text) as Dictionary<string, object>;
                if (config == null)
                {
                    throw new WireError("invalid_json", "agent.json is not an object");
                }
                bootstrapDirectory = GetText(config, "bootstrap_dir");
                linkId = GetText(config, "link_id");
                port = GetText(config, "port");
                if (!Json.TryGetInt64(GetOrNull(config, "heartbeat_ms"), out heartbeat) ||
                    !Json.TryGetInt64(GetOrNull(config, "grace_ms"), out grace) ||
                    heartbeat < 0 || heartbeat > int.MaxValue ||
                    grace < 0 || grace > int.MaxValue)
                {
                    throw new WireError(
                        "invalid_json",
                        "agent.json requires heartbeat_ms and grace_ms integers");
                }
                implementation = Implementation(library, GetOrNull(config, "implementation"));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("titella agent: agent.json is invalid: " + error.Message);
                return 2;
            }
            return Agent.Run(bootstrapDirectory, linkId, port,
                             (int)heartbeat, (int)grace, implementation);
        }

        /// <summary>
        /// The identity this process may announce. An exe defaults to
        /// windows-dotnet; a hosted library has no default, because an absent
        /// field there would claim an implementation this image cannot be.
        /// </summary>
        private static string Implementation(bool library, object declared)
        {
            string expected = library ? DllImplementation : ExeImplementation;
            if (declared == null)
            {
                if (library)
                {
                    throw new WireError(
                        "invalid_json",
                        "a hosted agent.dll requires implementation=" + DllImplementation);
                }
                return expected;
            }
            string value = declared as string;
            if (value != expected)
            {
                throw new WireError(
                    "invalid_json",
                    "implementation must be " + expected + " for this assembly, got "
                    + (value == null ? declared.ToString() : value));
            }
            return expected;
        }

        private static object GetOrNull(Dictionary<string, object> config, string key)
        {
            object value;
            return config.TryGetValue(key, out value) ? value : null;
        }

        private static string GetText(Dictionary<string, object> config, string key)
        {
            string value = GetOrNull(config, key) as string;
            if (value == null)
            {
                throw new WireError("invalid_json", "agent.json requires " + key);
            }
            return value;
        }
    }
}
